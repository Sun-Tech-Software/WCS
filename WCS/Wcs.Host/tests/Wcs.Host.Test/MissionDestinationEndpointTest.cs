using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Api.Contracts;
using Wcs.Api.Contracts.Dtos;
using Wcs.Engine;
using Wcs.Engine.Api;
using Wcs.Engine.Collections;
using Wcs.Engine.Items;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Wcs.Host.Endpoints;
using Wcs.Host.Security;
using Wcs.Wms.Contracts;
using Wcs.Wms.Contracts.Test;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Host.Test;

/// <summary>
/// <c>PUT /missions/{id}/destination</c> (piano delle missioni del 30/9, compito 5): ogni esito del
/// motore arriva con il suo stato HTTP e il suo titolo, la scelta sul gateway arriva al motore, il
/// rifiuto «nessun percorso» porta il gateway e se senza di lui il percorso c'e' come campi del
/// problema, e la rotta non chiede il permesso di modifica.
/// </summary>
/// <remarks>
/// <para>
/// Il motore e' quello vero (<see cref="MissionGateway.ChangeDestinationAsync"/>), su un impianto in
/// memoria: P al centro, collegato ad A, a X, a G (che porta a Y) e al magazzino S, di cui e' il
/// ricollocatore; Z non collegato a niente. Su P una missione verso A con la rotta P->A. Le missioni
/// si aggiungono con il motore in Run, come nell'host di prova; il cambio si chiede a motore fermo,
/// perche' deve funzionare anche li', e lo stato torna Run alla fine. Il gestionale e' quello finto.
/// </para>
/// <para>
/// I titoli e i nomi dei campi si scrivono per esteso, non con le costanti del contratto: sono
/// quello che la pagina e uno script leggono, e un cambio della costante viaggerebbe in silenzio da
/// tutte e due le parti della prova.
/// </para>
/// </remarks>
[Collection("host")]
public sealed class MissionDestinationEndpointTest(HostFixture host) : IAsyncLifetime
{
    private const string DoorCodeOfS = "DST5";
    private const decimal CellOfS = 5005;

    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly FakeWarehouse _warehouse = new();
    private readonly Dictionary<string, AutomationDevice> _devices = [];
    private readonly List<AutomationJunction> _junctions = [];
    private readonly List<Guid> _missionIds = [];

    public Task InitializeAsync()
    {
        Navigator.Warehouse = _warehouse;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        // Prima si toglie quello che la prova ha messo, poi il motore torna in Run: il passaggio in
        // Run sveglia ogni dispositivo che si avvia da se', e con le missioni della prova ancora a
        // bordo P potrebbe partire per la sua rotta mentre la pulizia la toglie. The changes that wait
        // for the end of a step (item 54) go first: they are static, and only in memory.
        await PendingMissionChanges.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));
        PendingMissionChanges.Reset();
        ActiveMissions.Instance.RemoveAll(m => _missionIds.Contains(m.MissionId));
        MissionSteps.Instance.RemoveAll(s => _missionIds.Contains(s.MissionId));
        PathJunctions.Instance.RemoveAll(j => _junctions.Contains(j));
        foreach (var device in _devices.Values) PathDevices.Instance.Remove(device.ItemGuid);
        Navigator.Warehouse = _previousWarehouse;
        Navigator.NavStatus = NavigatorStatusTypes.Run;

        var deviceIds = _devices.Values.Select(d => d.ItemGuid).ToList();
        await using var context = host.Store.CreateContext();
        await context.Missions.Where(m => _missionIds.Contains(m.MissionId)).ExecuteDeleteAsync();
        await context.DoorMappings.Where(d => deviceIds.Contains(d.ItemId)).ExecuteDeleteAsync();
    }

    // ---------------------------------------------------------------------------------------
    // Il cambio che passa
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Item 54, decision 3 of the owner of 13/10: a defragmentation and a change of cell are redirected
    /// through the endpoint like every other mission - 204, the destination X and the route P->X -
    /// where until item 54 the answer was 409 «Missione non reindirizzabile».
    /// </summary>
    [Theory]
    [InlineData(MissionTypes.DefragmentCell, 611)]
    [InlineData(MissionTypes.ChangeStoreLocation, 612)]
    public async Task ADefragmentationAndAChangeOfCellAreRedirectedLikeEveryOtherMission(MissionTypes type, int loadUnitId)
    {
        var client = host.CreateAuthenticatedClient();
        var mission = await AMissionOnPBoundForA(loadUnitId);
        mission.MissionType = type;

        var changed = await client.PutAsJsonAsync(Url(mission), new MissionDestinationUpdate(Id("X")));

        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        Assert.Equal(Id("X"), (await MissionOnTheHost(client, mission)).DestinationItemId);
        var step = Assert.Single(await StepsOnTheHost(client, mission));
        Assert.Equal((Id("P"), Id("X")), (step.FromNodeId, step.ToNodeId));
        Assert.Equal(MissionTypes.Extraction, mission.MissionType);
    }

    /// <summary>
    /// Il caso di tutti i giorni, a motore fermo e senza il permesso di modifica (il client non manda
    /// <c>X-Edit-Holder</c>): 204, la missione va verso X con la rotta P->X, e la stessa scelta una
    /// seconda volta e' ancora 204, con la stessa rotta.
    /// </summary>
    [Fact]
    public async Task TheDestinationChangesWithTheEngineStoppedAndWithoutTheEditPermission()
    {
        var client = host.CreateAuthenticatedClient();
        var mission = await AMissionOnPBoundForA(601);

        var changed = await client.PutAsJsonAsync(Url(mission), new MissionDestinationUpdate(Id("X")));

        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        Assert.Equal(Id("X"), (await MissionOnTheHost(client, mission)).DestinationItemId);
        var step = Assert.Single(await StepsOnTheHost(client, mission));
        Assert.Equal((Id("P"), Id("X")), (step.FromNodeId, step.ToNodeId));

        var again = await client.PutAsJsonAsync(Url(mission), new MissionDestinationUpdate(Id("X")));

        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        Assert.Equal(step.StepId, Assert.Single(await StepsOnTheHost(client, mission)).StepId);
        Assert.Equal(NavigatorStatusTypes.Stop, Navigator.NavStatus);
    }

    /// <summary>
    /// La rotta dichiara nella sua mappatura che non esige il permesso, e perche': e' la
    /// dichiarazione che il cancello delle rotte di scrittura (<c>EditPermissionGateTest</c>) conta.
    /// </summary>
    [Fact]
    public void TheRouteDeclaresThatItDoesNotRequireTheEditPermission()
    {
        var route = Assert.Single(host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>(),
            e => e.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == "SetMissionDestination");

        Assert.Equal("/api/v1/missions/{id:guid}/destination", route.RoutePattern.RawText);
        Assert.Equal(new[] { "PUT" }, route.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods);
        Assert.Null(route.Metadata.GetMetadata<RequiresEditPermissionMarker>());
        Assert.NotNull(route.Metadata.GetMetadata<EditPermissionNotRequiredMarker>());
    }

    // ---------------------------------------------------------------------------------------
    // La scelta sul gateway
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Le tre scelte arrivano al motore come tali. La missione ha il gateway G: senza scelta, o
    /// tenendolo, verso X non c'e' percorso (422, con G); toglierlo va dritto a X, senza gateway;
    /// impostare G verso Y passa da G, e G resta sulla missione. Il corpo si scrive a mano, con i
    /// nomi in minuscolo che il contratto della rotta promette.
    /// </summary>
    [Fact]
    public async Task TheGatewayChoiceReachesTheEngine()
    {
        var client = host.CreateAuthenticatedClient();
        var mission = await AMissionOnPBoundForA(602);
        mission.Gateway = Id("G");

        var unsaid = await PutRawAsync(client, mission, $$"""{ "itemId": "{{Id("X")}}" }""");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, unsaid.StatusCode);
        Assert.Equal(Id("G"), (await ProblemOf(unsaid)).GetProperty("gatewayItemId").GetGuid());

        var kept = await PutRawAsync(client, mission, $$"""{ "itemId": "{{Id("X")}}", "gateway": { "mode": "keep" } }""");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, kept.StatusCode);
        Assert.Equal(Id("G"), (await ProblemOf(kept)).GetProperty("gatewayItemId").GetGuid());
        Assert.Equal(Id("A"), mission.DestinationItemId);

        var cleared = await PutRawAsync(client, mission, $$"""{ "itemId": "{{Id("X")}}", "gateway": { "mode": "clear" } }""");

        Assert.Equal(HttpStatusCode.NoContent, cleared.StatusCode);
        Assert.Equal([("P", "X")], RouteOf(await StepsOnTheHost(client, mission)));
        Assert.Equal(Guid.Empty, mission.Gateway);

        var set = await PutRawAsync(client, mission,
            $$"""{ "itemId": "{{Id("Y")}}", "gateway": { "mode": "set", "itemId": "{{Id("G")}}" } }""");

        Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);
        Assert.Equal([("P", "G"), ("G", "Y")], RouteOf(await StepsOnTheHost(client, mission)));
        Assert.Equal(Id("G"), (await MissionOnTheHost(client, mission)).GatewayItemId);
    }

    /// <summary>
    /// Una scelta sul gateway che non e' ben formata e' rifiutata prima del motore (400, con il suo
    /// titolo e il perche' della forma arrivata, scritta come sul filo), e la missione resta verso A:
    /// un dispositivo con «keep» o con «clear», un «set» senza dispositivo o con quello vuoto. Un modo
    /// che non esiste come nome, scritto con un'altra maiuscola, o dato come numero (anche uno che
    /// esiste, 0, che il convertitore di serie avrebbe letto «keep»), non si lega nemmeno al corpo, e
    /// non diventa «keep»: e' rifiutato dal legame del corpo, prima della rotta - in Production con il
    /// 400 del framework, qui (l'host di prova e' in Development, dove il legame solleva) con il 500
    /// del gestore delle eccezioni, come per ogni corpo malformato di questo host. Lo stato quindi non
    /// si fissa; che niente sia cambiato si'. «Clear» e 1, che legati diventerebbero un cambio verso X,
    /// provano che la maiuscola e il numero non passano.
    /// </summary>
    [Fact]
    public async Task AGatewayChoiceThatIsNotWellFormedIsRefusedBeforeTheEngine()
    {
        var client = host.CreateAuthenticatedClient();
        var mission = await AMissionOnPBoundForA(603);
        var x = Id("X");
        var g = Id("G");

        foreach (var (body, why) in new[]
                 {
                     ($$"""{ "itemId": "{{x}}", "gateway": { "mode": "keep", "itemId": "{{g}}" } }""", "«keep» non vuole un dispositivo"),
                     ($$"""{ "itemId": "{{x}}", "gateway": { "mode": "clear", "itemId": "{{g}}" } }""", "«clear» non vuole un dispositivo"),
                     ($$"""{ "itemId": "{{x}}", "gateway": { "mode": "set" } }""", "«set» vuole il dispositivo da impostare"),
                     ($$"""{ "itemId": "{{x}}", "gateway": { "mode": "set", "itemId": "{{Guid.Empty}}" } }""", "«set» vuole il dispositivo da impostare")
                 })
        {
            var refused = await PutRawAsync(client, mission, body);

            Assert.True(HttpStatusCode.BadRequest == refused.StatusCode, $"{refused.StatusCode} per {body}");
            var problem = await ProblemOf(refused);
            Assert.Equal("Scelta del gateway non valida", problem.GetProperty("title").GetString());
            Assert.Contains(why, problem.GetProperty("detail").GetString());
        }

        foreach (var mode in new[] { "\"drop\"", "\"Clear\"", "0", "1", "7" })
        {
            var unknownMode = await PutRawAsync(client, mission, $$"""{ "itemId": "{{x}}", "gateway": { "mode": {{mode}} } }""");

            Assert.False(unknownMode.IsSuccessStatusCode, $"{unknownMode.StatusCode} per {mode}");
        }

        Assert.Equal(Id("A"), mission.DestinationItemId);
        Assert.Equal([("P", "A")], RouteOf(await StepsOnTheHost(client, mission)));
    }

    /// <summary>
    /// Il corpo come lo scrivono il client e chiunque serializzi il contratto con le regole del web: i
    /// nomi in minuscolo del contratto della rotta, che la prova qui sopra manda a mano all'host.
    /// </summary>
    [Fact]
    public void TheBodyIsWrittenWithTheNamesTheRouteReads()
    {
        var x = Guid.Parse("8f0c2f5e-1111-4a5b-9c3d-000000000001");
        var g = Guid.Parse("8f0c2f5e-2222-4a5b-9c3d-000000000002");
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        Assert.Equal("""{"itemId":"8f0c2f5e-1111-4a5b-9c3d-000000000001","gateway":null,"atTheRelocator":null,"cellDoorCode":null}""",
            JsonSerializer.Serialize(new MissionDestinationUpdate(x), web));
        Assert.Equal("""{"itemId":"8f0c2f5e-1111-4a5b-9c3d-000000000001","gateway":{"mode":"keep","itemId":null},"atTheRelocator":null,"cellDoorCode":null}""",
            JsonSerializer.Serialize(new MissionDestinationUpdate(x, MissionGatewayUpdate.Keep), web));
        Assert.Equal("""{"itemId":"8f0c2f5e-1111-4a5b-9c3d-000000000001","gateway":{"mode":"clear","itemId":null},"atTheRelocator":null,"cellDoorCode":null}""",
            JsonSerializer.Serialize(new MissionDestinationUpdate(x, MissionGatewayUpdate.Clear), web));
        Assert.Equal("""{"itemId":"8f0c2f5e-1111-4a5b-9c3d-000000000001","gateway":{"mode":"set","itemId":"8f0c2f5e-2222-4a5b-9c3d-000000000002"},"atTheRelocator":null,"cellDoorCode":null}""",
            JsonSerializer.Serialize(new MissionDestinationUpdate(x, MissionGatewayUpdate.Set(g)), web));

        // Item 54: the relocator's arrival by its name in lower case, and the precise cell by its code.
        Assert.Equal("""{"itemId":"8f0c2f5e-1111-4a5b-9c3d-000000000001","gateway":null,"atTheRelocator":"deposit","cellDoorCode":null}""",
            JsonSerializer.Serialize(new MissionDestinationUpdate(x, AtTheRelocator: MissionRelocatorArrival.Deposit), web));
        Assert.Equal("""{"itemId":"8f0c2f5e-1111-4a5b-9c3d-000000000001","gateway":null,"atTheRelocator":"stay","cellDoorCode":null}""",
            JsonSerializer.Serialize(new MissionDestinationUpdate(x, AtTheRelocator: MissionRelocatorArrival.Stay), web));
        Assert.Equal("""{"itemId":"8f0c2f5e-1111-4a5b-9c3d-000000000001","gateway":null,"atTheRelocator":null,"cellDoorCode":"3B02.0363.0001"}""",
            JsonSerializer.Serialize(new MissionDestinationUpdate(x, CellDoorCode: "3B02.0363.0001"), web));
    }

    // ---------------------------------------------------------------------------------------
    // I rifiuti, uno per esito
    // ---------------------------------------------------------------------------------------

    /// <summary>Missione, destinazione o gateway che non ci sono: 404 e due 400, ognuno con il suo titolo.</summary>
    [Fact]
    public async Task AnUnknownMissionDestinationOrGatewayIsRefusedWithItsOwnTitle()
    {
        var client = host.CreateAuthenticatedClient();
        var mission = await AMissionOnPBoundForA(604);

        await AssertRefusedAsync(
            await client.PutAsJsonAsync($"{ApiRoutes.Prefix}{ApiRoutes.Missions}/{Guid.NewGuid()}/destination",
                new MissionDestinationUpdate(Id("X"))),
            HttpStatusCode.NotFound, "Missione non attiva");
        await AssertRefusedAsync(
            await client.PutAsJsonAsync(Url(mission), new MissionDestinationUpdate(Guid.NewGuid())),
            HttpStatusCode.BadRequest, "Destinazione sconosciuta");
        await AssertRefusedAsync(
            await client.PutAsJsonAsync(Url(mission), new MissionDestinationUpdate(Id("X"), MissionGatewayUpdate.Set(Guid.NewGuid()))),
            HttpStatusCode.BadRequest, "Gateway sconosciuto");

        Assert.Equal(Id("A"), mission.DestinationItemId);
    }

    /// <summary>
    /// I 409, ognuno con il suo titolo, perche' la pagina li dice in parole diverse: la ricerca della
    /// cella in corso, la posizione scelta come destinazione. The step in flight on P is not a refusal
    /// any more (item 54, decision 4: <see cref="AChangeDuringAStepIsAcceptedAndTheMissionShowsItWhileItWaits"/>),
    /// and the defragmentation «not redirectable» is gone with item 54 too
    /// (<see cref="ADefragmentationAndAChangeOfCellAreRedirectedLikeEveryOtherMission"/>).
    /// </summary>
    [Fact]
    public async Task EachConflictHasItsOwnTitle()
    {
        var client = host.CreateAuthenticatedClient();
        var mission = await AMissionOnPBoundForA(605);

        var search = new TaskCompletionSource();
        Assert.True(Navigator.TryClaimCellSearch(mission.MissionId, search.Task));
        try
        {
            await AssertRefusedAsync(await client.PutAsJsonAsync(Url(mission), new MissionDestinationUpdate(Id("X"))),
                HttpStatusCode.Conflict, "Ricerca della cella o altro cambio in corso");
        }
        finally
        {
            Navigator.ReleaseCellSearchClaim(mission.MissionId, search.Task);
            search.SetResult();
        }

        await AssertRefusedAsync(await client.PutAsJsonAsync(Url(mission), new MissionDestinationUpdate(Id("P"))),
            HttpStatusCode.Conflict, "UDC già nella destinazione");

        Assert.Equal(Id("A"), mission.DestinationItemId);
        Assert.Equal([("P", "A")], RouteOf(await StepsOnTheHost(client, mission)));
    }

    /// <summary>
    /// Item 54, decision 4 of the owner of 13/10: the step P->A of the mission is in flight on P, and the
    /// operator sends the load unit to X. The answer is 202 - the change waits for the end of the step -
    /// with nothing changed meanwhile, and the mission, read again, carries the change that waits: a change
    /// of destination towards X. Until item 54 it was the 409 «Passo della missione in esecuzione».
    /// </summary>
    [Fact]
    public async Task AChangeDuringAStepIsAcceptedAndTheMissionShowsItWhileItWaits()
    {
        var client = host.CreateAuthenticatedClient();
        var mission = await AMissionOnPBoundForA(606);
        var oldStep = Assert.Single(mission.MissionSteps);
        _devices["P"].RunningStepId = oldStep.StepId;
        _devices["P"].Status = DeviceStatusTypes.Moving;

        var answer = await client.PutAsJsonAsync(Url(mission), new MissionDestinationUpdate(Id("X")));

        Assert.Equal(HttpStatusCode.Accepted, answer.StatusCode);
        var row = (await client.GetFromJsonAsync<MissionDto>($"{ApiRoutes.Prefix}{ApiRoutes.Missions}/{mission.MissionId}"))!;
        Assert.Equal(MissionPendingChangeKind.Destination, row.PendingChange);
        Assert.Equal(Id("X"), row.PendingChangeItemId);
        Assert.Equal(Id("A"), row.DestinationItemId);
        Assert.Equal([("P", "A")], RouteOf(await StepsOnTheHost(client, mission)));
    }

    /// <summary>
    /// Nessun percorso: 422 con il suo titolo, e il gateway e il percorso senza di lui come campi del
    /// problema. Verso Z, senza gateway: il gateway e' nullo e il campo dice di no. Verso X passando
    /// per G, che a X non porta: il gateway e' G, e senza di lui il percorso c'e'. Verso Z passando
    /// per G: il gateway e' G, e senza di lui il percorso non c'e' lo stesso - il caso in cui la
    /// pagina non deve proporre di togliere il gateway, perche' il cambio rimandato senza tornerebbe
    /// con lo stesso rifiuto. E' il solo dei tre in cui gateway e campo non dicono la stessa cosa.
    /// </summary>
    [Fact]
    public async Task NoRouteCarriesTheGatewayAndWhetherThereIsARouteWithoutIt()
    {
        var client = host.CreateAuthenticatedClient();
        var mission = await AMissionOnPBoundForA(607);

        var towardsZ = await client.PutAsJsonAsync(Url(mission), new MissionDestinationUpdate(Id("Z")));

        var withoutGateway = await AssertRefusedAsync(towardsZ, (HttpStatusCode)422, "Nessun percorso verso la destinazione");
        Assert.Equal(JsonValueKind.Null, withoutGateway.GetProperty("gatewayItemId").ValueKind);
        Assert.False(withoutGateway.GetProperty("wouldRouteWithoutGateway").GetBoolean());

        var throughG = await client.PutAsJsonAsync(Url(mission), new MissionDestinationUpdate(Id("X"), MissionGatewayUpdate.Set(Id("G"))));

        var withGateway = await AssertRefusedAsync(throughG, (HttpStatusCode)422, "Nessun percorso verso la destinazione");
        Assert.Equal(Id("G"), withGateway.GetProperty("gatewayItemId").GetGuid());
        Assert.True(withGateway.GetProperty("wouldRouteWithoutGateway").GetBoolean());
        Assert.Contains("senza il gateway il percorso c'è", withGateway.GetProperty("detail").GetString());

        var towardsZThroughG = await client.PutAsJsonAsync(Url(mission), new MissionDestinationUpdate(Id("Z"), MissionGatewayUpdate.Set(Id("G"))));

        var neitherWay = await AssertRefusedAsync(towardsZThroughG, (HttpStatusCode)422, "Nessun percorso verso la destinazione");
        Assert.Equal(Id("G"), neitherWay.GetProperty("gatewayItemId").GetGuid());
        Assert.False(neitherWay.GetProperty("wouldRouteWithoutGateway").GetBoolean());
        Assert.Contains("e nemmeno senza", neitherWay.GetProperty("detail").GetString());

        Assert.Equal(Id("A"), mission.DestinationItemId);
        Assert.Equal(Guid.Empty, mission.Gateway);
    }

    /// <summary>
    /// Verso il magazzino S: il gestionale che non ha celle e' un 422 con un titolo diverso da quello
    /// del percorso, e il campo dice che non e' «un'altra cella» a mancare; il gestionale che non
    /// risponde e' un 503. Poi il cambio passa (la cella 5005 di S), e scegliere di nuovo S senza
    /// un'altra cella libera e' lo stesso 422, con il campo che lo dice.
    /// </summary>
    [Fact]
    public async Task TowardsAStorageTheRefusalsSayWhatIsMissing()
    {
        var client = host.CreateAuthenticatedClient();
        var mission = await AMissionOnPBoundForA(608);
        await SIsAStorageWithCell5005();
        TheWarehouseHasNoFreeCellFor(mission);

        var noCell = await AssertRefusedAsync(await client.PutAsJsonAsync(Url(mission), new MissionDestinationUpdate(Id("S"))),
            (HttpStatusCode)422, "Nessuna cella da prenotare nel magazzino");
        Assert.False(noCell.GetProperty("noOtherFreeCell").GetBoolean());

        _warehouse.Unavailable = true;
        var unreachable = await AssertRefusedAsync(await client.PutAsJsonAsync(Url(mission), new MissionDestinationUpdate(Id("S"))),
            HttpStatusCode.ServiceUnavailable, "Il gestionale non è raggiungibile dal motore");
        // Voce 34: quale dei due 503 e' lo dice il campo, non il titolo.
        Assert.Equal("warehouseUnreachable", unreachable.GetProperty("unavailable").GetString());
        _warehouse.Unavailable = false;
        Assert.Equal(Id("A"), mission.DestinationItemId);

        TheWarehouseAnswersCell5005For(mission);
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PutAsJsonAsync(Url(mission), new MissionDestinationUpdate(Id("S")))).StatusCode);
        Assert.Equal(DoorCodeOfS, mission.StorageDoorCode);

        TheWarehouseHasNoFreeCellFor(mission);
        var noOtherCell = await AssertRefusedAsync(await client.PutAsJsonAsync(Url(mission), new MissionDestinationUpdate(Id("S"))),
            (HttpStatusCode)422, "Nessuna cella da prenotare nel magazzino");
        Assert.True(noOtherCell.GetProperty("noOtherFreeCell").GetBoolean());
        Assert.Equal(DoorCodeOfS, mission.StorageDoorCode);
    }

    /// <summary>
    /// Ogni esito del motore ha una risposta: lo stato e il titolo di ognuno sono scritti qui, e
    /// l'elenco degli esiti scritto qui deve essere quello del motore - un esito nuovo senza la sua
    /// risposta farebbe un 500 alla prima richiesta, e qui fallisce prima.
    /// </summary>
    [Fact]
    public void EveryOutcomeOfTheEngineHasItsAnswer()
    {
        var expected = new Dictionary<MissionDestinationOutcome, (int Status, string? Title)>
        {
            [MissionDestinationOutcome.Changed] = (204, null),
            [MissionDestinationOutcome.Unchanged] = (204, null),
            [MissionDestinationOutcome.MissionNotFound] = (404, "Missione non attiva"),
            [MissionDestinationOutcome.DeviceNotFound] = (400, "Destinazione sconosciuta"),
            [MissionDestinationOutcome.GatewayNotFound] = (400, "Gateway sconosciuto"),
            [MissionDestinationOutcome.StepInFlight] = (409, "Passo della missione in esecuzione"),
            [MissionDestinationOutcome.Pending] = (202, null),
            [MissionDestinationOutcome.CopyInFlight] = (409, "Un'altra UDC della stessa cella in movimento"),
            [MissionDestinationOutcome.CellSearchInFlight] = (409, "Ricerca della cella o altro cambio in corso"),
            [MissionDestinationOutcome.AlreadyThere] = (409, "UDC già nella destinazione"),
            [MissionDestinationOutcome.NoRoute] = (422, "Nessun percorso verso la destinazione"),
            [MissionDestinationOutcome.NoStorageCell] = (422, "Nessuna cella da prenotare nel magazzino"),
            [MissionDestinationOutcome.WarehouseUnavailable] = (503, "Il gestionale non è raggiungibile dal motore"),
            [MissionDestinationOutcome.WarehouseRefused] = (422, "Il gestionale ha rifiutato il cambio"),
            [MissionDestinationOutcome.NotARelocator] = (400, "Il dispositivo non è un ricollocatore"),
            [MissionDestinationOutcome.CellRefused] = (422, "Cella rifiutata")
        };

        Assert.Equal(Enum.GetValues<MissionDestinationOutcome>().Order(), expected.Keys.Order());
        var update = new MissionDestinationUpdate(Guid.NewGuid(), MissionGatewayUpdate.Set(Guid.NewGuid()));
        foreach (var (outcome, (status, title)) in expected)
        {
            var answer = MissionEndpoints.DestinationAnswer(Guid.NewGuid(), update, new MissionDestinationResult(outcome));

            Assert.True(status == ((IStatusCodeHttpResult)answer).StatusCode, $"{outcome}: {((IStatusCodeHttpResult)answer).StatusCode}");
            Assert.Equal(title, (answer as ProblemHttpResult)?.ProblemDetails.Title);
            // Voce 34: il campo che dice quale 503 e' sta sul gestionale che non risponde, e solo li'.
            var reason = (answer as ProblemHttpResult)?.ProblemDetails.Extensions.TryGetValue("unavailable", out var value) == true ? value : null;
            Assert.True(Equals(outcome == MissionDestinationOutcome.WarehouseUnavailable ? "warehouseUnreachable" : null, reason),
                $"{outcome}: unavailable = {reason ?? "(assente)"}");
        }
    }

    /// <summary>
    /// Il cambio rifiutato perche' un'altra UDC della stessa cella (una copia della porta) non si puo'
    /// fermare (risposta 4 del 9/10): il 409 ha un titolo suo, e le parole dicono che a muoversi e'
    /// l'altra UDC, non la missione scelta. Prima era il titolo del passo in volo, «La missione ha un
    /// passo in esecuzione», per una missione ferma.
    /// </summary>
    [Fact]
    public void ACopyThatMovesHasItsOwnTitleAndWords()
    {
        var answer = (ProblemHttpResult)MissionEndpoints.DestinationAnswer(Guid.NewGuid(),
            new MissionDestinationUpdate(Guid.NewGuid()), new MissionDestinationResult(MissionDestinationOutcome.CopyInFlight));

        Assert.Equal(409, answer.StatusCode);
        Assert.Equal("Un'altra UDC della stessa cella in movimento", answer.ProblemDetails.Title);
        Assert.Equal("Un'altra UDC che condivide la cella della missione si sta muovendo: la missione è ferma e la destinazione non è cambiata, riprovare fra un momento",
            answer.ProblemDetails.Detail);
    }

    /// <summary>
    /// Il no del gestionale alla prima segnalazione del cambio cella (risposta 4 dell'8/10 sera): il
    /// titolo e le parole dicono che il gestionale ha rifiutato il cambio e che l'estrazione resta, non
    /// che non ha risposto. Prima il motore lo dava come gestionale fermo, e l'host rispondeva il 503
    /// «non ha risposto».
    /// </summary>
    [Fact]
    public void TheWarehouseRefusalSaysTheWarehouseRefusedTheChange()
    {
        var answer = (ProblemHttpResult)MissionEndpoints.DestinationAnswer(Guid.NewGuid(),
            new MissionDestinationUpdate(Guid.NewGuid()), new MissionDestinationResult(MissionDestinationOutcome.WarehouseRefused));

        Assert.Equal(422, answer.StatusCode);
        Assert.Equal("Il gestionale ha rifiutato il cambio", answer.ProblemDetails.Title);
        Assert.Equal("Il gestionale ha rifiutato il cambio: la destinazione non è cambiata, e la missione resta l'estrazione che era",
            answer.ProblemDetails.Detail);
    }

    /// <summary>
    /// Un rifiuto arrivato dopo la prenotazione verso un magazzino, che ha fatto perdere alla missione
    /// la cella che teneva, lo dice: il campo «lostTheCell» e le parole del dettaglio, oltre ai campi
    /// dell'esito. Senza la cella persa il campo non c'e', e il dettaglio e' quello di sempre.
    /// </summary>
    [Fact]
    public void ARefusalThatLostTheCellSaysSo()
    {
        var update = new MissionDestinationUpdate(Guid.NewGuid());

        var lost = (ProblemHttpResult)MissionEndpoints.DestinationAnswer(Guid.NewGuid(), update,
            new MissionDestinationResult(MissionDestinationOutcome.NoStorageCell, LostTheCell: true));
        var kept = (ProblemHttpResult)MissionEndpoints.DestinationAnswer(Guid.NewGuid(), update,
            new MissionDestinationResult(MissionDestinationOutcome.WarehouseUnavailable));

        Assert.Equal(true, lost.ProblemDetails.Extensions["lostTheCell"]);
        Assert.Equal(false, lost.ProblemDetails.Extensions["noOtherFreeCell"]);
        Assert.Contains("La cella che la missione teneva però è stata lasciata", lost.ProblemDetails.Detail);
        Assert.False(kept.ProblemDetails.Extensions.ContainsKey("lostTheCell"));
        Assert.DoesNotContain("La cella che la missione teneva", kept.ProblemDetails.Detail);
    }

    /// <summary>
    /// Il rifiuto del cambio cella dopo che il gestionale aveva saputo che la missione lascia l'ordine
    /// chiude la missione (risposta del proprietario del 2/10 sulla domanda del compito 4: «il rifiuto
    /// lo dice»), e il rifiuto lo dice: il campo «missionClosed» e le parole del dettaglio, oltre ai
    /// campi dell'esito, con lo stato dell'esito. Senza la chiusura il campo non c'e'. Prima l'host
    /// lasciava cadere la chiusura, e la pagina la indovinava rileggendo l'elenco (revisione finale
    /// della voce 39).
    /// </summary>
    [Fact]
    public void ARefusalThatClosedTheMissionSaysSo()
    {
        var update = new MissionDestinationUpdate(Guid.NewGuid());

        var closed = (ProblemHttpResult)MissionEndpoints.DestinationAnswer(Guid.NewGuid(), update,
            new MissionDestinationResult(MissionDestinationOutcome.NoStorageCell, MissionClosed: true));
        var open = (ProblemHttpResult)MissionEndpoints.DestinationAnswer(Guid.NewGuid(), update,
            new MissionDestinationResult(MissionDestinationOutcome.NoStorageCell));

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, closed.StatusCode);
        Assert.Equal(true, closed.ProblemDetails.Extensions["missionClosed"]);
        Assert.Equal(false, closed.ProblemDetails.Extensions["noOtherFreeCell"]);
        Assert.Contains("la missione è chiusa, e l'UDC resta nella sua cella senza ordine", closed.ProblemDetails.Detail);
        Assert.False(open.ProblemDetails.Extensions.ContainsKey("missionClosed"));
        Assert.DoesNotContain("la missione è chiusa", open.ProblemDetails.Detail);
    }

    // ---------------------------------------------------------------------------------------
    // Item 54, task 6: the relocator's arrival and the precise cell
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Item 54, decision 5: the body says what the relocator R (of the storage S2) does with the load unit,
    /// by its name in lower case; the engine writes it on the mission, and the mission's row shows it.
    /// Towards X, which no storage names, the same choice is the 400 «not a relocator», and nothing changes.
    /// </summary>
    [Fact]
    public async Task TheRelocatorsArrivalReachesTheEngineAndTheMissionShowsIt()
    {
        var client = host.CreateAuthenticatedClient();
        var mission = await AMissionOnPBoundForA(621);
        ARelocatorRWithItsStorageS2();

        var notARelocator = await PutRawAsync(client, mission, $$"""{ "itemId": "{{Id("X")}}", "atTheRelocator": "deposit" }""");
        await AssertRefusedAsync(notARelocator, HttpStatusCode.BadRequest, "Il dispositivo non è un ricollocatore");
        Assert.Equal(Id("A"), mission.DestinationItemId);

        var stay = await PutRawAsync(client, mission, $$"""{ "itemId": "{{Id("R")}}", "atTheRelocator": "stay" }""");

        Assert.Equal(HttpStatusCode.NoContent, stay.StatusCode);
        Assert.Equal(Id("R"), mission.DestinationItemId);
        Assert.Equal(RelocatorArrival.Stay, mission.RelocatorArrival);
        Assert.Equal(MissionRelocatorArrival.Stay, (await MissionOnTheHost(client, mission)).RelocatorArrival);
        var row = await client.GetStringAsync($"{ApiRoutes.Prefix}{ApiRoutes.Missions}/{mission.MissionId}");
        Assert.Contains("\"relocatorArrival\":\"stay\"", row);

        var deposit = await PutRawAsync(client, mission, $$"""{ "itemId": "{{Id("R")}}", "atTheRelocator": "deposit" }""");

        Assert.Equal(HttpStatusCode.NoContent, deposit.StatusCode);
        Assert.Equal(MissionRelocatorArrival.Deposit, (await MissionOnTheHost(client, mission)).RelocatorArrival);
    }

    /// <summary>
    /// Item 54, decision 6: the body names the precise cell DST5 of S; the warehouse reserves it, and the
    /// mission goes to S with that door. Refused by the warehouse, the answer is the 422 «Cella rifiutata»
    /// with the warehouse's reason in the detail and the refusal in the field; the WCS's own refusal (a
    /// door its map does not have) the same, without asking the warehouse.
    /// </summary>
    [Fact]
    public async Task ThePreciseCellReachesTheEngineAndItsRefusalSaysWhy()
    {
        var client = host.CreateAuthenticatedClient();
        var mission = await AMissionOnPBoundForA(622);
        await SIsAStorageWithCell5005();

        _warehouse.ReserveStorageCellAnswer = _ =>
            new StorageCellReservationResult(StorageCellReservationOutcome.CellNotAvailable, null, "la cella e' occupata");
        var refused = await AssertRefusedAsync(
            await PutRawAsync(client, mission, $$"""{ "itemId": "{{Id("S")}}", "cellDoorCode": "DST5" }"""),
            (HttpStatusCode)422, "Cella rifiutata");
        Assert.Equal("cellNotAvailable", refused.GetProperty("cellRefusal").GetString());
        Assert.Equal("La cella DST5 non è stata prenotata: la cella e' occupata", refused.GetProperty("detail").GetString());
        Assert.Equal(Id("A"), mission.DestinationItemId);

        var unknown = await AssertRefusedAsync(
            await PutRawAsync(client, mission, $$"""{ "itemId": "{{Id("S")}}", "cellDoorCode": "NOPE" }"""),
            (HttpStatusCode)422, "Cella rifiutata");
        Assert.Equal("unknownDoor", unknown.GetProperty("cellRefusal").GetString());

        _warehouse.ReserveStorageCellAnswer = null;
        _warehouse.ReservableStorageCells[DoorCodeOfS] = new StorageReservation(Guid.Empty,
            StorageReservationOutcome.CellFound, CellOfS, DoorCodeOfS, Partition: "0014", StorageDepth: 150, NeedsCompaction: false);

        var reserved = await PutRawAsync(client, mission, $$"""{ "itemId": "{{Id("S")}}", "cellDoorCode": " DST5 " }""");

        Assert.Equal(HttpStatusCode.NoContent, reserved.StatusCode);
        Assert.Equal(Id("S"), mission.DestinationItemId);
        Assert.Equal(DoorCodeOfS, mission.StorageDoorCode);
        Assert.Equal([DoorCodeOfS, DoorCodeOfS], _warehouse.StorageCellReservationRequests.Select(request => request.DoorCode));
    }

    /// <summary>
    /// Item 54: a choice that is not well formed is a 400 before the engine - the relocator's arrival and
    /// the precise cell together, a blank cell code - and an arrival that is not a name of the contract
    /// (another capital, a number) does not even bind to the body. The mission stays bound for A. The
    /// arrivals go towards the relocator R (of S2), where bound they would be a change (204): «Stay», 0
    /// and 1 would be the choices «stay» and «deposit» (fix round 1 of task 6; towards S, which is not a
    /// relocator, a bound arrival was the engine's 400 «not a relocator», and the loop could not fail).
    /// </summary>
    [Fact]
    public async Task ADestinationChoiceThatIsNotWellFormedIsRefusedBeforeTheEngine()
    {
        var client = host.CreateAuthenticatedClient();
        var mission = await AMissionOnPBoundForA(623);
        await SIsAStorageWithCell5005();
        var s = Id("S");

        foreach (var (body, why) in new[]
                 {
                     ($$"""{ "itemId": "{{s}}", "atTheRelocator": "stay", "cellDoorCode": "DST5" }""", "non vanno insieme"),
                     ($$"""{ "itemId": "{{s}}", "cellDoorCode": "  " }""", "è vuoto")
                 })
        {
            var refused = await AssertRefusedAsync(await PutRawAsync(client, mission, body),
                HttpStatusCode.BadRequest, "Scelta della destinazione non valida");
            Assert.Contains(why, refused.GetProperty("detail").GetString());
        }

        ARelocatorRWithItsStorageS2();
        var r = Id("R");
        foreach (var arrival in new[] { "\"Stay\"", "\"DEPOSIT\"", "\"keep\"", "0", "1" })
        {
            var unknown = await PutRawAsync(client, mission, $$"""{ "itemId": "{{r}}", "atTheRelocator": {{arrival}} }""");

            Assert.False(unknown.IsSuccessStatusCode, $"{unknown.StatusCode} per {arrival}");
        }

        Assert.Empty(_warehouse.StorageCellReservationRequests);
        Assert.Equal(Id("A"), mission.DestinationItemId);
        Assert.Equal(RelocatorArrival.None, mission.RelocatorArrival);
    }

    /// <summary>
    /// Item 54, decision 6: <c>GET /missions/{id}/storage-cells?itemId=</c> lists the cells the warehouse
    /// proposes for the load unit in S (those that are in S by the WCS's door map), without the edit
    /// permission and with the engine stopped; every refusal has its status and its title.
    /// </summary>
    [Fact]
    public async Task TheStorageCellsAreListedForTheChoiceAndEachRefusalHasItsTitle()
    {
        var client = host.CreateAuthenticatedClient();
        var mission = await AMissionOnPBoundForA(624);
        await SIsAStorageWithCell5005();
        _warehouse.StorageCellCandidates[mission.LuId] =
            [new StorageCellCandidate(CellOfS, DoorCodeOfS, 3, "Area 3", 12, 4, "cella", 1)];

        var cells = await client.GetFromJsonAsync<List<StorageCellCandidateDto>>(CellsUrl(mission.MissionId, Id("S")));

        Assert.Equal([new StorageCellCandidateDto(CellOfS, DoorCodeOfS, 3, "Area 3", 12, 4, "cella", 1)], cells);
        Assert.Equal([Id("P")], Assert.Single(_warehouse.StorageCellCandidateRequests).ReachableDeviceIds);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(CellsUrl(Guid.NewGuid(), Id("S")))).StatusCode);
        await AssertRefusedAsync(await client.GetAsync(CellsUrl(mission.MissionId, Guid.NewGuid())),
            HttpStatusCode.BadRequest, "Dispositivo sconosciuto");
        await AssertRefusedAsync(await client.GetAsync(CellsUrl(mission.MissionId, Id("X"))),
            HttpStatusCode.BadRequest, "Il dispositivo non ha celle");

        _warehouse.Unavailable = true;
        var unreachable = await AssertRefusedAsync(await client.GetAsync(CellsUrl(mission.MissionId, Id("S"))),
            HttpStatusCode.ServiceUnavailable, "Il gestionale non è raggiungibile dal motore");
        Assert.Equal("warehouseUnreachable", unreachable.GetProperty("unavailable").GetString());
        _warehouse.Unavailable = false;

        mission.LuId = 0;
        await AssertRefusedAsync(await client.GetAsync(CellsUrl(mission.MissionId, Id("S"))),
            (HttpStatusCode)422, "Nessuna cella da chiedere al gestionale");
    }

    /// <summary>The relocator R, linked from P, of the storage S2.</summary>
    private void ARelocatorRWithItsStorageS2()
    {
        Device("R");
        Device("S2", DeviceTypes.Storage).RelocateItemId = Id("R");
        Link("P", "R");
        Link("R", "S2");
    }

    private static string CellsUrl(Guid missionId, Guid itemId) =>
        $"{ApiRoutes.Prefix}{ApiRoutes.Missions}/{missionId}/storage-cells?itemId={itemId}";

    // ---------------------------------------------------------------------------------------
    // L'impianto
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// P collegato ad A, a X, a G (che porta a Y) e al magazzino S, di cui e' il ricollocatore; Z
    /// isolato. Su P una missione verso A con la rotta P->A; poi il motore e' fermo.
    /// </summary>
    private async Task<PathMission> AMissionOnPBoundForA(decimal loadUnitId)
    {
        foreach (var name in new[] { "P", "A", "X", "G", "Y", "Z" }) Device(name);
        Device("S", DeviceTypes.Storage).RelocateItemId = Id("P");
        Link("P", "A");
        Link("P", "X");
        Link("P", "G");
        Link("G", "Y");
        Link("P", "S");

        var mission = await AMissionOn("P", loadUnitId);
        mission.DestinationItemId = Id("A");
        MissionSteps.Instance.Add(new MissionStep
        {
            StepId = Guid.NewGuid(),
            MissionId = mission.MissionId,
            Date = DateTime.Now,
            FromNodeId = Id("P"),
            ToNodeId = Id("A")
        });
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        return mission;
    }

    /// <summary>
    /// Una missione a bordo di <paramref name="position"/>, aggiunta con il motore in Run come
    /// nell'host di prova; a motore fermo (una seconda missione nella stessa prova) in Recover, perche'
    /// <c>ActiveMissions.Add</c> in Stop esce subito, e il passaggio in Run sveglierebbe i dispositivi
    /// con la missione di prima a bordo e la sua rotta.
    /// </summary>
    private async Task<PathMission> AMissionOn(string position, decimal loadUnitId)
    {
        var status = Navigator.NavStatus;
        if (status != NavigatorStatusTypes.Run) Navigator.NavStatus = NavigatorStatusTypes.Recover;
        var mission = new PathMission(Guid.NewGuid())
        {
            LuId = loadUnitId,
            LuCode = $"UDC{loadUnitId}",
            MissionType = MissionTypes.MovingToItem,
            OperationType = OperationTypes.Outbound,
            PositionItemId = Id(position)
        };
        _missionIds.Add(mission.MissionId);
        ActiveMissions.Instance.Add(mission, buildRoute: false);
        Assert.Same(mission, ActiveMissions.Instance[mission.MissionId]);
        await mission.PositionWork;
        Navigator.NavStatus = status;
        return mission;
    }

    /// <summary>La porta di S e' la cella 5005, del magazzino 1 del gestionale, con l'indirizzo di campo 0011.0012.</summary>
    private async Task SIsAStorageWithCell5005()
    {
        await PlantPersistence.SaveDoorMappingsAsync(Id("S"),
            [new DoorMapping { ItemId = Id("S"), DoorCode = DoorCodeOfS, CellId = CellOfS, Side = "A" }]);
        _warehouse.Cells[CellOfS] = new CellDescription(CellOfS, AreaId: 1, WarehouseId: 1, Depth: 1, Inverted: false,
            CellLockState.None, AllowedLoadUnitTypeId: null, GroupId: null, GroupOrder: 0, DoorCodeOfS, DoorCodeB: string.Empty,
            DoorCount: 1, MultiDepthStorage: false);
        _warehouse.CellPartitionMaps[CellOfS] = new CellPartitionMap(CellOfS, "0011", "0012",
            AsiDoorB: string.Empty, SubitemDoorB: string.Empty, [new CellPartition("0014", -1)]);
    }

    private void TheWarehouseAnswersCell5005For(PathMission mission) =>
        _warehouse.StorageReservations[mission.LuId] = new StorageReservation(Guid.Empty,
            StorageReservationOutcome.CellFound, CellOfS, DoorCodeOfS, Partition: "0013", StorageDepth: 150, NeedsCompaction: false);

    private void TheWarehouseHasNoFreeCellFor(PathMission mission) =>
        _warehouse.StorageReservations[mission.LuId] = new StorageReservation(Guid.Empty,
            StorageReservationOutcome.NoFreeCells, 0, string.Empty, string.Empty, 0, NeedsCompaction: false);

    private AutomationDevice Device(string name, DeviceTypes type = DeviceTypes.Automation)
    {
        var device = TestDevices.WithRow(Guid.NewGuid(), d => d.DeviceType = type);
        PathDevices.Instance.Add(device);
        _devices[name] = device;
        return device;
    }

    private void Link(string from, string to)
    {
        var junction = new AutomationJunction(Id(from), Id(to)) { Priority = 99 };
        PathJunctions.Instance.Add(junction);
        _junctions.Add(junction);
    }

    private Guid Id(string name) => _devices[name].ItemGuid;

    private string NameOf(Guid? id) => _devices.Single(d => d.Value.ItemGuid == id).Key;

    /// <summary>
    /// Gli step in ordine di percorso, da P: l'host li elenca nell'ordine della collezione, che non e'
    /// quello della rotta. Uno step che non si aggancia resta in coda, cosi' il confronto lo vede.
    /// </summary>
    private (string From, string To)[] RouteOf(IEnumerable<MissionStepDto> steps)
    {
        var left = steps.ToList();
        var route = new List<(string From, string To)>();
        Guid? at = Id("P");
        while (left.FirstOrDefault(s => s.FromNodeId == at) is { } next)
        {
            route.Add((NameOf(next.FromNodeId), NameOf(next.ToNodeId)));
            at = next.ToNodeId;
            left.Remove(next);
        }

        route.AddRange(left.Select(s => (NameOf(s.FromNodeId), NameOf(s.ToNodeId))));
        return [.. route];
    }

    private static string Url(PathMission mission) => $"{ApiRoutes.Prefix}{ApiRoutes.Missions}/{mission.MissionId}/destination";

    private static Task<HttpResponseMessage> PutRawAsync(HttpClient client, PathMission mission, string body) =>
        client.PutAsync(Url(mission), new StringContent(body, Encoding.UTF8, "application/json"));

    private static async Task<MissionDto> MissionOnTheHost(HttpClient client, PathMission mission) =>
        (await client.GetFromJsonAsync<MissionDto>($"{ApiRoutes.Prefix}{ApiRoutes.Missions}/{mission.MissionId}"))!;

    private static async Task<List<MissionStepDto>> StepsOnTheHost(HttpClient client, PathMission mission) =>
        (await client.GetFromJsonAsync<List<MissionStepDto>>($"{ApiRoutes.Prefix}{ApiRoutes.Missions}/{mission.MissionId}/steps"))!;

    private static async Task<JsonElement> ProblemOf(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return problem.RootElement.Clone();
    }

    /// <summary>Lo stato e il titolo del rifiuto; restituisce il problema, per i campi.</summary>
    private static async Task<JsonElement> AssertRefusedAsync(HttpResponseMessage response, HttpStatusCode status, string title)
    {
        Assert.Equal(status, response.StatusCode);
        var problem = await ProblemOf(response);
        Assert.Equal(title, problem.GetProperty("title").GetString());
        Assert.Equal((int)status, problem.GetProperty("status").GetInt32());
        return problem;
    }
}
