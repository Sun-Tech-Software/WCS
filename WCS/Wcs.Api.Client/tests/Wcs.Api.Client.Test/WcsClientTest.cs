using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Wcs.Api.Client;
using Wcs.Api.Contracts;
using Wcs.Api.Contracts.Dtos;
using Wcs.Engine.Api;
using Wcs.Host.Test;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Api.Client.Test;

[Collection("host")]
public sealed class WcsClientTest(HostFixture host)
{
    private WcsClient Client() => new(new WcsClientOptions { BaseAddress = host.Server.BaseAddress, ApiKey = HostFixture.ApiKey },
        host.Server.CreateHandler());

    /// <summary>
    /// Concede il permesso di modifica al client vero e lo rilascia con Dispose (compito 4b,
    /// estensione oltre le rotte del permesso stesso): a differenza di
    /// <see cref="HostFixture.GrantEditPermission"/> non tocca un <c>HttpClient</c> grezzo ma
    /// <see cref="WcsClient.EditHolder"/>. Non e' unita a <see cref="Client"/> apposta:
    /// <see cref="TheClientAcquiresRenewsAndReleasesTheEditLock"/> prova l'acquisizione vera del
    /// permesso con le proprie identita', e un titolare fisso concesso da <see cref="Client"/> per
    /// ogni test la farebbe fallire trovando il permesso gia' preso.
    /// </summary>
    private static IDisposable GrantEditPermission(WcsClient client, [CallerMemberName] string testName = "")
    {
        var holder = $"{testName}-{Guid.NewGuid()}";
        if (!EditLock.TryAcquire(holder, TimeSpan.FromMinutes(5)))
            throw new InvalidOperationException($"Permesso di modifica non concesso a {holder}.");
        client.EditHolder = holder;
        return new EditPermissionRelease(holder);
    }

    private sealed class EditPermissionRelease(string holder) : IDisposable
    {
        public void Dispose() => EditLock.Release(holder);
    }

    private sealed class RecordingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public List<(Uri Uri, bool HasAuthorization, string? ApiKey)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!, request.Headers.Authorization is not null,
                request.Headers.TryGetValues(ApiRoutes.ApiKeyHeader, out var values)
                    ? values.Single()
                    : null));
            return base.SendAsync(request, cancellationToken);
        }
    }

    [Fact]
    public async Task TheClientReadsTheStatusAndEnqueuesACommand()
    {
        using var client = Client();
        Assert.Equal(NavigatorStatusTypes.Run, (await client.GetStatusAsync()).NavStatus);
        var command = await client.EnqueueCommandAsync(new CommandRequest(1001, WorkstationId: "PC9"));
        Assert.True(command.Id > 0);
        Assert.Equal(command.Id, (await client.GetCommandAsync(command.Id))!.Id);
        Assert.Null(await client.GetCommandAsync(command.Id + 1000));
        Assert.Empty(await client.GetItemsAsync());
        Assert.False(await client.ExtractionQueueContainsLoadUnitAsync(123456));
    }

    [Fact]
    public async Task TheClientReadsAllMissionStepsInOneCall()
    {
        using var client = Client();
        Assert.Empty(await client.GetAllMissionStepsAsync());
    }

    [Fact]
    public async Task TheClientPassesTheNewFiltersThrough()
    {
        using var client = Client();
        Assert.Empty(await client.GetItemsAsync([DeviceTypes.Buffer]));
        Assert.Empty(await client.GetMissionsAsync(loadUnitId: 1, type: MissionTypes.Rejection));
        Assert.Empty(await client.GetExtractionQueueAsync(loadUnitId: 999999));
        Assert.Empty(await client.GetCommandsAsync(itemId: Guid.NewGuid(), maxType: 2999, excludeUserId: "ORCHESTRA_ROUTE"));
        var summary = await client.GetHandledLoadUnitSummaryAsync(new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1), DateTime.Now, SummaryPeriod.Month);
        Assert.All(summary, s => Assert.Equal(1, s.Period.Day));
    }

    /// <summary>
    /// GetDeviceTypesAsync, SetItemGeometryAsync e SetItemGeometriesAsync sono le tre rotte
    /// corrette oggi con l'involucro R(...): senza, la richiesta perdeva il prefisso /api/v1
    /// (aggiunto da WcsClient al BaseAddress) e cadeva fuori dal gruppo di rotte che l'host mappa
    /// sotto quel prefisso (Program.cs), quindi 404. Passano per il client vero — non per
    /// l'HttpClient grezzo che gia' collauda l'host — cosi' la stessa dimenticanza, se tornasse,
    /// non passerebbe piu' inosservata.
    /// </summary>
    [Fact]
    public async Task TheClientReadsTheDeviceTypesWithThePrefix()
    {
        using var client = Client();
        var types = await client.GetDeviceTypesAsync();
        Assert.Equal(DeviceTypeRegistry.All.Select(t => t.Code), types.Select(t => t.Code));
    }

    [Fact]
    public async Task TheClientSetsTheItemGeometryWithThePrefix()
    {
        using var client = Client();
        using var _ = GrantEditPermission(client);
        Wcs.Engine.Navigator.NavStatus = NavigatorStatusTypes.Stop;
        try
        {
            var id = Guid.NewGuid();
            await client.CreateItemAsync(new PlantItemConfigDto { ItemId = id, Name = "Geometria" });
            var before = await client.GetItemAsync(id);
            var confirmation = await client.SetItemGeometryAsync(
                new PlantItemGeometryUpdateDto(id, 11.5, 22.5, 33, 44, 15, before!.GeometryVersion));
            Assert.Equal(id, confirmation.ItemId);
            Assert.NotEqual(before.GeometryVersion, confirmation.GeometryVersion);
            var item = await client.GetItemAsync(id);
            Assert.Equal(11.5, item!.X);
            Assert.Equal(22.5, item.Y);
            Assert.Equal(confirmation.GeometryVersion, item.GeometryVersion);
        }
        finally
        {
            Wcs.Engine.Navigator.NavStatus = NavigatorStatusTypes.Run;
            Wcs.Engine.Collections.PathDevices.Instance.Clear();
        }
    }

    [Fact]
    public async Task TheClientSetsTheItemGeometriesInBatchWithThePrefix()
    {
        using var client = Client();
        using var _ = GrantEditPermission(client);
        Wcs.Engine.Navigator.NavStatus = NavigatorStatusTypes.Stop;
        try
        {
            var a = Guid.NewGuid(); var b = Guid.NewGuid();
            await client.CreateItemAsync(new PlantItemConfigDto { ItemId = a, Name = "Geo A" });
            await client.CreateItemAsync(new PlantItemConfigDto { ItemId = b, Name = "Geo B" });
            var beforeA = await client.GetItemAsync(a);
            var beforeB = await client.GetItemAsync(b);
            var confirmations = await client.SetItemGeometriesAsync(
            [
                new PlantItemGeometryUpdateDto(a, 1, 2, 10, 10, 0, beforeA!.GeometryVersion),
                new PlantItemGeometryUpdateDto(b, 3, 4, 10, 10, 0, beforeB!.GeometryVersion)
            ]);
            Assert.Equal(2, confirmations.Count);
            Assert.Equal(1, (await client.GetItemAsync(a))!.X);
            Assert.Equal(4, (await client.GetItemAsync(b))!.Y);
        }
        finally
        {
            Wcs.Engine.Navigator.NavStatus = NavigatorStatusTypes.Run;
            Wcs.Engine.Collections.PathDevices.Instance.Clear();
        }
    }

    [Fact]
    public async Task TheClientTurnsATypedGeometry409IntoAGeometryConflict()
    {
        using var client = Client();
        using var _ = GrantEditPermission(client);
        Wcs.Engine.Navigator.NavStatus = NavigatorStatusTypes.Stop;
        try
        {
            var id = Guid.NewGuid();
            await client.CreateItemAsync(new PlantItemConfigDto { ItemId = id });
            var before = await client.GetItemAsync(id);
            await client.SetItemGeometryAsync(
                new PlantItemGeometryUpdateDto(id, 1, 2, 3, 4, 5, before!.GeometryVersion));

            var conflict = await Assert.ThrowsAsync<GeometryConflictException>(() =>
                client.SetItemGeometryAsync(
                    new PlantItemGeometryUpdateDto(id, 90, 91, 92, 93, 94, before.GeometryVersion)));

            Assert.Equal([id], conflict.ItemIds);
            Assert.Equal(System.Net.HttpStatusCode.Conflict, conflict.StatusCode);
        }
        finally
        {
            Wcs.Engine.Navigator.NavStatus = NavigatorStatusTypes.Run;
            Wcs.Engine.Collections.PathDevices.Instance.Clear();
        }
    }

    [Fact]
    public async Task TheClientKeepsAnUnrelatedGeometry409Generic()
    {
        using var client = Client();
        using var _ = GrantEditPermission(client);
        Wcs.Engine.Navigator.NavStatus = NavigatorStatusTypes.Stop;
        try
        {
            var id = Guid.NewGuid();
            await client.CreateItemAsync(new PlantItemConfigDto { ItemId = id });
            var before = await client.GetItemAsync(id);
            Wcs.Engine.Navigator.NavStatus = NavigatorStatusTypes.Run;

            var failure = await Record.ExceptionAsync(() =>
                client.SetItemGeometryAsync(
                    new PlantItemGeometryUpdateDto(id, 1, 2, 3, 4, 5, before!.GeometryVersion)));

            var apiFailure = Assert.IsType<WcsApiException>(failure);
            Assert.Equal(System.Net.HttpStatusCode.Conflict, apiFailure.StatusCode);
        }
        finally
        {
            Wcs.Engine.Navigator.NavStatus = NavigatorStatusTypes.Run;
            Wcs.Engine.Collections.PathDevices.Instance.Clear();
        }
    }

    /// <summary>
    /// Item 54, decision 4: with a step of the mission in flight the host answers 202, and the client says
    /// that the move and the change of destination are applied at the end of the movement - no exception,
    /// and not «now».
    /// </summary>
    [Fact]
    public async Task TheClientSaysWhenAChangeWaitsForTheEndOfTheMovement()
    {
        using var client = Client();
        var from = new Wcs.Engine.Items.AutomationDevice(Guid.NewGuid()) { DeviceType = DeviceTypes.Automation };
        var to = new Wcs.Engine.Items.AutomationDevice(Guid.NewGuid()) { DeviceType = DeviceTypes.Automation };
        Wcs.Engine.Collections.PathDevices.Instance.Add(from);
        Wcs.Engine.Collections.PathDevices.Instance.Add(to);
        var mission = new Wcs.Engine.Items.PathMission(Guid.NewGuid()) { LuId = 82, LuCode = "UDC82", PositionItemId = from.ItemGuid };
        Wcs.Engine.Collections.ActiveMissions.Instance.Add(mission, buildRoute: false);
        var step = new Wcs.Engine.Items.MissionStep { StepId = Guid.NewGuid(), MissionId = mission.MissionId, FromNodeId = from.ItemGuid, ToNodeId = to.ItemGuid };
        Wcs.Engine.Collections.MissionSteps.Instance.Add(step);
        try
        {
            await mission.PositionWork;
            from.RunningStepId = step.StepId;
            from.Status = DeviceStatusTypes.Moving;

            Assert.Equal(MissionChangeApplied.AtTheEndOfTheMovement, await client.SetMissionPositionAsync(mission.MissionId, to.ItemGuid));
            Assert.Equal(MissionChangeApplied.AtTheEndOfTheMovement, await client.SetMissionDestinationAsync(mission.MissionId, to.ItemGuid));
            Assert.Equal(from.ItemGuid, (await client.GetMissionAsync(mission.MissionId))!.PositionItemId);
        }
        finally
        {
            await PendingMissionChanges.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));
            PendingMissionChanges.Reset();
            await mission.PositionWork;
            Wcs.Engine.Collections.ActiveMissions.Instance.RemoveAll(m => m.MissionId == mission.MissionId);
            Wcs.Engine.Collections.MissionSteps.Instance.RemoveAll(s => s.MissionId == mission.MissionId);
            Wcs.Engine.Collections.PathDevices.Instance.Remove(from.ItemGuid);
            Wcs.Engine.Collections.PathDevices.Instance.Remove(to.ItemGuid);
        }
    }

    [Fact]
    public async Task TheClientMovesAMissionAndReportsTheRefusal()
    {
        using var client = Client();
        var from = new Wcs.Engine.Items.AutomationDevice(Guid.NewGuid()) { DeviceType = DeviceTypes.Automation };
        var to = new Wcs.Engine.Items.AutomationDevice(Guid.NewGuid()) { DeviceType = DeviceTypes.Automation };
        Wcs.Engine.Collections.PathDevices.Instance.Add(from);
        Wcs.Engine.Collections.PathDevices.Instance.Add(to);
        var mission = new Wcs.Engine.Items.PathMission(Guid.NewGuid()) { LuId = 81, LuCode = "UDC81", PositionItemId = from.ItemGuid };
        Wcs.Engine.Collections.ActiveMissions.Instance.Add(mission, buildRoute: false);
        try
        {
            Assert.Equal(MissionChangeApplied.Now, await client.SetMissionPositionAsync(mission.MissionId, to.ItemGuid));
            Assert.Equal(to.ItemGuid, (await client.GetMissionAsync(mission.MissionId))!.PositionItemId);

            var refused = await Assert.ThrowsAsync<WcsApiException>(() => client.SetMissionPositionAsync(mission.MissionId, Guid.NewGuid()));
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, refused.StatusCode);
        }
        finally
        {
            Wcs.Engine.Collections.ActiveMissions.Instance.RemoveAll(m => m.MissionId == mission.MissionId);
            Wcs.Engine.Collections.PathDevices.Instance.Remove(from.ItemGuid);
            Wcs.Engine.Collections.PathDevices.Instance.Remove(to.ItemGuid);
        }
    }

    /// <summary>
    /// Il cambio di destinazione dal client, a motore fermo: la scelta sul gateway arriva all'host
    /// (verso Y passando per G, che resta sulla missione), e ogni rifiuto arriva come
    /// <see cref="WcsApiException"/> con lo stato e, nel corpo, il titolo del problema - un 409, il
    /// 422 di nessun percorso con i suoi campi (tenendo G, verso X non si passa; senza G si'), un 400
    /// e il 404. P e' collegato a X e a G, G a Y.
    /// </summary>
    [Fact]
    public async Task TheClientChangesAMissionsDestinationAndCarriesEachRefusalWithItsTitle()
    {
        using var client = Client();
        var devices = new[] { "P", "X", "G", "Y" }.ToDictionary(name => name,
            _ => new Wcs.Engine.Items.AutomationDevice(Guid.NewGuid()) { DeviceType = DeviceTypes.Automation });
        foreach (var device in devices.Values) Wcs.Engine.Collections.PathDevices.Instance.Add(device);
        var junctions = new[] { ("P", "X"), ("P", "G"), ("G", "Y") }
            .Select(link => new Wcs.Engine.Items.AutomationJunction(devices[link.Item1].ItemGuid, devices[link.Item2].ItemGuid) { Priority = 99 })
            .ToList();
        foreach (var junction in junctions) Wcs.Engine.Collections.PathJunctions.Instance.Add(junction);
        var mission = new Wcs.Engine.Items.PathMission(Guid.NewGuid()) { LuId = 83, LuCode = "UDC83", PositionItemId = devices["P"].ItemGuid };
        Wcs.Engine.Collections.ActiveMissions.Instance.Add(mission, buildRoute: false);
        await mission.PositionWork;
        Wcs.Engine.Navigator.NavStatus = NavigatorStatusTypes.Stop;
        try
        {
            Assert.Equal(MissionChangeApplied.Now,
                await client.SetMissionDestinationAsync(mission.MissionId, devices["Y"].ItemGuid, MissionGatewayUpdate.Set(devices["G"].ItemGuid)));
            var changed = (await client.GetMissionAsync(mission.MissionId))!;
            Assert.Equal((devices["Y"].ItemGuid, devices["G"].ItemGuid), (changed.DestinationItemId, changed.GatewayItemId));

            var alreadyThere = await Assert.ThrowsAsync<WcsApiException>(() =>
                client.SetMissionDestinationAsync(mission.MissionId, devices["P"].ItemGuid));
            Assert.Equal(System.Net.HttpStatusCode.Conflict, alreadyThere.StatusCode);
            Assert.Equal("UDC già nella destinazione", ProblemOf(alreadyThere).GetProperty("title").GetString());

            var noRoute = await Assert.ThrowsAsync<WcsApiException>(() =>
                client.SetMissionDestinationAsync(mission.MissionId, devices["X"].ItemGuid));
            Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity, noRoute.StatusCode);
            var problem = ProblemOf(noRoute);
            Assert.Equal("Nessun percorso verso la destinazione", problem.GetProperty("title").GetString());
            Assert.Equal(devices["G"].ItemGuid, problem.GetProperty("gatewayItemId").GetGuid());
            Assert.True(problem.GetProperty("wouldRouteWithoutGateway").GetBoolean());

            var unknown = await Assert.ThrowsAsync<WcsApiException>(() =>
                client.SetMissionDestinationAsync(mission.MissionId, Guid.NewGuid(), MissionGatewayUpdate.Clear));
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, unknown.StatusCode);
            Assert.Equal("Destinazione sconosciuta", ProblemOf(unknown).GetProperty("title").GetString());

            var gone = await Assert.ThrowsAsync<WcsApiException>(() =>
                client.SetMissionDestinationAsync(Guid.NewGuid(), devices["X"].ItemGuid));
            Assert.Equal(System.Net.HttpStatusCode.NotFound, gone.StatusCode);
            Assert.Equal("Missione non attiva", ProblemOf(gone).GetProperty("title").GetString());

            Assert.Equal(devices["Y"].ItemGuid, (await client.GetMissionAsync(mission.MissionId))!.DestinationItemId);
        }
        finally
        {
            // Prima si toglie quello che la prova ha messo, poi il motore torna in Run: il passaggio
            // sveglia P, che si avvia da se', e con la missione ancora a bordo partirebbe per P->G->Y
            // mentre la pulizia la toglie.
            Wcs.Engine.Collections.ActiveMissions.Instance.RemoveAll(m => m.MissionId == mission.MissionId);
            Wcs.Engine.Collections.MissionSteps.Instance.RemoveAll(s => s.MissionId == mission.MissionId);
            Wcs.Engine.Collections.PathJunctions.Instance.RemoveAll(junctions.Contains);
            foreach (var device in devices.Values) Wcs.Engine.Collections.PathDevices.Instance.Remove(device.ItemGuid);
            Wcs.Engine.Navigator.NavStatus = NavigatorStatusTypes.Run;
            await using var context = host.Store.CreateContext();
            await context.Missions.Where(m => m.MissionId == mission.MissionId).ExecuteDeleteAsync();
        }
    }

    /// <summary>
    /// Item 54, task 6: the client sends the whole body of the change of destination - here what the
    /// relocator R (of the storage S) does with the load unit, which the mission's row then shows - and
    /// asks the storage cells of a device: one with no cells, and the storage S for a load unit with nothing
    /// to ask, are refusals with their titles. P->R->S, the mission on P, the engine stopped.
    /// </summary>
    [Fact]
    public async Task TheClientSendsTheRelocatorsArrivalAndReadsTheStorageCells()
    {
        using var client = Client();
        var devices = new[] { "P", "R", "S" }.ToDictionary(name => name,
            name => new Wcs.Engine.Items.AutomationDevice(Guid.NewGuid())
                { DeviceType = name == "S" ? DeviceTypes.Storage : DeviceTypes.Automation });
        devices["S"].RelocateItemId = devices["R"].ItemGuid;
        foreach (var device in devices.Values) Wcs.Engine.Collections.PathDevices.Instance.Add(device);
        var junctions = new[] { ("P", "R"), ("R", "S") }
            .Select(link => new Wcs.Engine.Items.AutomationJunction(devices[link.Item1].ItemGuid, devices[link.Item2].ItemGuid) { Priority = 99 })
            .ToList();
        foreach (var junction in junctions) Wcs.Engine.Collections.PathJunctions.Instance.Add(junction);
        var mission = new Wcs.Engine.Items.PathMission(Guid.NewGuid()) { LuId = 84, LuCode = "UDC84", PositionItemId = devices["P"].ItemGuid };
        Wcs.Engine.Collections.ActiveMissions.Instance.Add(mission, buildRoute: false);
        await mission.PositionWork;
        Wcs.Engine.Navigator.NavStatus = NavigatorStatusTypes.Stop;
        try
        {
            Assert.Equal(MissionChangeApplied.Now, await client.SetMissionDestinationAsync(mission.MissionId,
                new MissionDestinationUpdate(devices["R"].ItemGuid, AtTheRelocator: MissionRelocatorArrival.Stay)));
            var changed = (await client.GetMissionAsync(mission.MissionId))!;
            Assert.Equal((devices["R"].ItemGuid, MissionRelocatorArrival.Stay), (changed.DestinationItemId, changed.RelocatorArrival));

            var noCells = await Assert.ThrowsAsync<WcsApiException>(() =>
                client.GetMissionStorageCellsAsync(mission.MissionId, devices["P"].ItemGuid));
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, noCells.StatusCode);
            Assert.Equal("Il dispositivo non ha celle", ProblemOf(noCells).GetProperty("title").GetString());

            // S is a storage, and the load unit without the warehouse's identifier has nothing to ask:
            // the storage S reached the engine. The list itself is proven on the host
            // (MissionDestinationEndpointTest.TheStorageCellsAreListedForTheChoiceAndEachRefusalHasItsTitle).
            mission.LuId = 0;
            var nothingToAsk = await Assert.ThrowsAsync<WcsApiException>(() =>
                client.GetMissionStorageCellsAsync(mission.MissionId, devices["S"].ItemGuid));
            Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity, nothingToAsk.StatusCode);
            Assert.Equal("Nessuna cella da chiedere al gestionale", ProblemOf(nothingToAsk).GetProperty("title").GetString());
        }
        finally
        {
            Wcs.Engine.Collections.ActiveMissions.Instance.RemoveAll(m => m.MissionId == mission.MissionId);
            Wcs.Engine.Collections.MissionSteps.Instance.RemoveAll(s => s.MissionId == mission.MissionId);
            Wcs.Engine.Collections.PathJunctions.Instance.RemoveAll(junctions.Contains);
            foreach (var device in devices.Values) Wcs.Engine.Collections.PathDevices.Instance.Remove(device.ItemGuid);
            Wcs.Engine.Navigator.NavStatus = NavigatorStatusTypes.Run;
            await using var context = host.Store.CreateContext();
            await context.Missions.Where(m => m.MissionId == mission.MissionId).ExecuteDeleteAsync();
        }
    }

    /// <summary>Il problema nel corpo di un rifiuto: il client lo porta com'e', e chi lo usa ne legge titolo e campi.</summary>
    private static JsonElement ProblemOf(WcsApiException refused)
    {
        using var problem = JsonDocument.Parse(refused.Body);
        return problem.RootElement.Clone();
    }

    [Fact]
    public async Task TheClientReadsTheCellSearchContextAndPassesTheGuardFiltersThrough()
    {
        using var client = Client();
        var context = await client.GetCellSearchContextAsync();
        Assert.NotNull(context.AutomaticCells);
        Assert.NotNull(context.CellsInExtraction);
        Assert.NotNull(context.LoadUnitsInExtraction);
        Assert.False(await client.MissionExistsForLoadUnitAsync(1, [MissionStatusTypes.Run, MissionStatusTypes.Stop]));
        Assert.False(await client.ExtractionQueueContainsLoadUnitAsync(123456, excludeRow: 1));
    }

    /// <summary>
    /// Il client legge i messaggi dei PLC dall'host: e' la strada per cui il <c>FrmPcmLog</c> del
    /// Client potra' lasciare la <c>SELECT</c> su <c>PCM_LOG</c>. Le righe stanno nel database dei
    /// messaggi dei PLC, che dalla voce <c>D183</c> non e' piu' lo storico; la rotta e il metodo
    /// del client non cambiano.
    /// </summary>
    [Fact]
    public async Task TheClientReadsThePlcMessages()
    {
        using var client = Client();
        var firstOfThisMonth = new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1);
        var day = firstOfThisMonth.AddDays(4).AddHours(6);
        await using (var context = host.PlcLog.CreateContext())
        {
            context.PlcMessages.Add(new Wcs.Engine.Persistence.Entities.PlcMessage
            {
                Date = day, PlcId = 11, Type = 1, Asi = "0400", MessageId = 777, Xml = "<FromClient />"
            });
            await context.SaveChangesAsync();
        }

        var rows = await client.GetPlcMessagesAsync(firstOfThisMonth, day.AddDays(1), take: 100);
        var row = Assert.Single(rows, m => m.MessageId == 777);
        Assert.Equal(11m, row.PlcId);
        Assert.Equal("0400", row.Asi);
    }

    [Fact]
    public void TheRetryPolicyNeverGivesUpAndTopsOutAtThirtySeconds()
    {
        var policy = new InfiniteRetryPolicy();
        // La politica predefinita di SignalR si ferma al quarto tentativo: questa no.
        Assert.Equal(TimeSpan.Zero, policy.NextRetryDelay(new RetryContext { PreviousRetryCount = 0 }));
        Assert.NotNull(policy.NextRetryDelay(new RetryContext { PreviousRetryCount = 4 }));
        Assert.Equal(InfiniteRetryPolicy.MaxDelay, policy.NextRetryDelay(new RetryContext { PreviousRetryCount = 5 }));
        Assert.Equal(InfiniteRetryPolicy.MaxDelay, policy.NextRetryDelay(new RetryContext { PreviousRetryCount = 100_000 }));
    }

    [Fact]
    public async Task TheEventsConnectionRaisesTheStatusEvent()
    {
        await using var events = new PlantEventsConnection(new WcsClientOptions { BaseAddress = host.Server.BaseAddress, ApiKey = HostFixture.ApiKey },
            host.Server.CreateHandler());
        var received = new TaskCompletionSource<EngineStatus>();
        events.NavigatorStatusChanged += s => received.TrySetResult(s);
        await events.StartAsync();
        Wcs.Engine.Navigator.NavStatus = NavigatorStatusTypes.Pause;
        try { Assert.Equal(NavigatorStatusTypes.Pause, (await received.Task.WaitAsync(TimeSpan.FromSeconds(5))).NavStatus); }
        finally { Wcs.Engine.Navigator.NavStatus = NavigatorStatusTypes.Run; }
    }

    [Fact]
    public async Task TheEventsClientSendsTheKeyOnlyInTheApiKeyHeader()
    {
        using var recorder = new RecordingHandler(host.Server.CreateHandler());
        await using var events = new PlantEventsConnection(new WcsClientOptions
        {
            BaseAddress = host.Server.BaseAddress,
            ApiKey = HostFixture.ApiKey
        }, recorder);
        await events.StartAsync();
        await events.StopAsync();

        Assert.NotEmpty(recorder.Requests);
        Assert.All(recorder.Requests, request =>
        {
            Assert.DoesNotContain("access_token", request.Uri.Query, StringComparison.OrdinalIgnoreCase);
            Assert.False(request.HasAuthorization);
            Assert.Equal(HostFixture.ApiKey, request.ApiKey);
        });
    }

    [Fact]
    public async Task TheClientSavesAConfigurationAndSeesTheConflictWhileRunning()
    {
        using var client = Client();
        using var _ = GrantEditPermission(client);
        Wcs.Engine.Navigator.NavStatus = NavigatorStatusTypes.Stop;
        try
        {
            var id = Guid.NewGuid();
            await client.CreateItemAsync(new PlantItemConfigDto { ItemId = id });
            await client.SaveItemConfigurationAsync(new PlantItemConfigDto { ItemId = id, Name = "Client" });
            Assert.Equal("Client", (await client.GetItemConfigurationAsync(id))!.Name);
            Assert.Contains(await client.GetItemConfigurationsAsync(), c => c.ItemId == id);
            var other = Guid.NewGuid();
            await client.CreateItemAsync(new PlantItemConfigDto { ItemId = other });
            var junction = await client.AddJunctionAsync(new JunctionRequest(id, other, 3));
            Assert.True(await client.SetJunctionPriorityAsync(junction.JunctionId, new JunctionRequest(id, other, 1)));
            await client.SetConnectionTypeForAllAsync(ConnectionTypes.Test);
            Wcs.Engine.Navigator.NavStatus = NavigatorStatusTypes.Run;
            var conflict = await Assert.ThrowsAsync<WcsApiException>(() => client.SaveItemConfigurationAsync(new PlantItemConfigDto { ItemId = id }));
            Assert.Equal(System.Net.HttpStatusCode.Conflict, conflict.StatusCode);
            var unavailable = await Assert.ThrowsAsync<WcsApiException>(() => client.GetWmsCellsAsync(2, true));
            Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        }
        finally
        {
            Wcs.Engine.Navigator.NavStatus = NavigatorStatusTypes.Run;
            Wcs.Engine.Collections.PathDevices.Instance.Clear();
            Wcs.Engine.Collections.PathJunctions.Instance.Clear();
        }
    }

    /// <summary>
    /// Il permesso di modifica visto dal client vero (compito 4, 4b), non dall'<c>HttpClient</c>
    /// grezzo che gia' collauda l'host in <c>EditLockEndpointsTest</c>: concede, nega a un'altra
    /// identita' con chi lo tiene, rinnova spostando la scadenza, rilascia e lo fa riottenere subito.
    /// </summary>
    [Fact]
    public async Task TheClientAcquiresRenewsAndReleasesTheEditLock()
    {
        using var client = Client();
        var holder = $"client-test-{Guid.NewGuid()}";
        var otherHolder = $"client-test-other-{Guid.NewGuid()}";
        try
        {
            var granted = await client.AcquireEditLockAsync(holder);
            Assert.True(granted.IsMine);
            Assert.Equal(holder, granted.Holder);

            var denied = await client.AcquireEditLockAsync(otherHolder);
            Assert.False(denied.IsMine);
            Assert.Equal(holder, denied.Holder);

            var renewed = await client.RenewEditLockAsync(holder);
            Assert.True(renewed.IsMine);
            Assert.True(renewed.ExpiresLocal >= granted.ExpiresLocal);

            await client.ReleaseEditLockAsync(holder);
            var afterRelease = await client.AcquireEditLockAsync(otherHolder);
            Assert.True(afterRelease.IsMine);
            Assert.Equal(otherHolder, afterRelease.Holder);
        }
        finally
        {
            EditLock.Release(holder);
            EditLock.Release(otherHolder);
        }
    }

    /// <summary>Lo svuotamento dell'impianto che «Nuovo diagramma» dell'Editor chiede all'host.</summary>
    [Fact]
    public async Task TheClientEmptiesThePlantAndSeesTheConflictWhileRunning()
    {
        using var client = Client();
        using var _ = GrantEditPermission(client);
        Wcs.Engine.Navigator.NavStatus = NavigatorStatusTypes.Stop;
        try
        {
            var a = Guid.NewGuid(); var b = Guid.NewGuid();
            await client.CreateItemAsync(new PlantItemConfigDto { ItemId = a, Name = "Da svuotare A" });
            await client.CreateItemAsync(new PlantItemConfigDto { ItemId = b, Name = "Da svuotare B" });
            // Contiene, non conta: come il gemello dell'host, lo store e' condiviso da tutta la
            // collezione "host", e il conteggio assoluto dipende da che cosa hanno scritto le altre classi.
            var written = await client.GetItemConfigurationsAsync();
            Assert.Contains(written, c => c.ItemId == a);
            Assert.Contains(written, c => c.ItemId == b);

            Wcs.Engine.Navigator.NavStatus = NavigatorStatusTypes.Run;
            var conflict = await Assert.ThrowsAsync<WcsApiException>(() => client.RemoveAllItemsAsync());
            Assert.Equal(System.Net.HttpStatusCode.Conflict, conflict.StatusCode);

            Wcs.Engine.Navigator.NavStatus = NavigatorStatusTypes.Stop;
            await client.RemoveAllItemsAsync();
            var afterRemoval = await client.GetItemConfigurationsAsync();
            Assert.DoesNotContain(afterRemoval, c => c.ItemId == a);
            Assert.DoesNotContain(afterRemoval, c => c.ItemId == b);
        }
        finally
        {
            Wcs.Engine.Navigator.NavStatus = NavigatorStatusTypes.Run;
            Wcs.Engine.Collections.PathDevices.Instance.Clear();
            Wcs.Engine.Collections.PathJunctions.Instance.Clear();
        }
    }
}
