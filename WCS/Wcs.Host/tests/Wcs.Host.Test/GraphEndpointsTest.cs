using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wcs.Api.Contracts;
using Wcs.Api.Contracts.Dtos;
using Wcs.Engine;
using Wcs.Engine.Collections;
using Wcs.Engine.Persistence.Entities;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Host.Test;

/// <summary>
/// <c>/plant/graph</c> e <c>/plant/graph/legacy-geometry</c> (compito 12: esportazione e
/// importazione).
/// </summary>
/// <remarks>
/// Stesso schema di <see cref="PlantGeometryEndpointsTest"/>: il dispositivo si crea con
/// <c>PUT .../config</c> (l'unico modo verificato che lo mette sia in memoria che sullo store),
/// il motore fermo (<c>Navigator.NavStatus = Stop</c>) durante ogni scrittura.
/// </remarks>
[Collection("host")]
public sealed class GraphEndpointsTest(HostFixture fixture) : IDisposable
{
    private readonly NavigatorStatusTypes _previousStatus = Navigator.NavStatus;
    private const string Items = ApiRoutes.Prefix + ApiRoutes.Items;
    private const string Graph = ApiRoutes.Prefix + ApiRoutes.PlantGraph;
    private const string LegacyGeometry = ApiRoutes.Prefix + ApiRoutes.PlantGraphLegacyGeometry;

    public void Dispose()
    {
        PathDevices.Instance.Clear();
        PathJunctions.Instance.Clear();
        Navigator.NavStatus = _previousStatus;
    }

    private HttpClient Client() => fixture.CreateAuthenticatedClient();

    private static async Task<Guid> AddDeviceAsync(HttpClient client)
    {
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        var itemId = Guid.NewGuid();
        var created = await client.PostAsJsonAsync(Items, new PlantItemConfigDto { ItemId = itemId });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return itemId;
    }

    [Fact]
    public async Task GetReturnsTheDeviceItsGeometryItsJunctionAndItsDoorMapping()
    {
        var client = Client();
        using var _ = fixture.GrantEditPermission(client);
        var source = await AddDeviceAsync(client);
        var destination = await AddDeviceAsync(client);
        var sourceBefore = await client.GetFromJsonAsync<PlantItemDto>($"{Items}/{source}");

        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(
            $"{Items}/{source}/geometry",
            new PlantItemGeometryUpdateDto(source, 10, 20, 30, 40, 0, sourceBefore!.GeometryVersion))).StatusCode);

        var junctionResponse = await client.PostAsJsonAsync(
            $"{ApiRoutes.Prefix}{ApiRoutes.Junctions}", new JunctionRequest(source, destination, 1));
        Assert.Equal(HttpStatusCode.Created, junctionResponse.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(
            $"{ApiRoutes.Prefix}{ApiRoutes.DoorMappings}",
            new[] { new DoorMappingDto(0, source, "A", 42, "front", 1, false) })).StatusCode);

        var graph = await client.GetFromJsonAsync<PlantGraphDto>(Graph);

        Assert.NotNull(graph);
        Assert.Contains(graph!.Items, i => i.ItemId == source);
        Assert.Contains(graph.Items, i => i.ItemId == destination);
        var geometry = graph.Geometries.Single(g => g.ItemId == source);
        Assert.Equal(10, geometry.X);
        Assert.Equal(20, geometry.Y);
        Assert.Contains(graph.Junctions, j => j.SourceItemId == source && j.DestinationItemId == destination);
        Assert.Contains(graph.DoorMappings, m => m.ItemId == source && m.DoorCode == "A");
    }

    [Fact]
    public async Task GetIncludesThePlcConnectionConfigurationReferencedByThePlant()
    {
        var client = Client();
        using var _ = fixture.GrantEditPermission(client);
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        const long connectionId = 41;
        var expected = new PlcConnectionDto(connectionId, "PLC linea", "10.0.0.41", 2041,
            "10.0.1.41", 3041);
        await fixture.DetachEveryDeviceFromItsPlcConnectionAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(
            ApiRoutes.Prefix + ApiRoutes.PlcConnections, new[] { expected })).StatusCode);

        var itemId = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(Items,
            new PlantItemConfigDto { ItemId = itemId, DeviceConnectionId = connectionId })).StatusCode);

        var graph = await client.GetFromJsonAsync<PlantGraphDto>(Graph);

        Assert.NotNull(graph);
        Assert.Equal(expected, Assert.Single(graph.PlcConnections!));
        var json = await client.GetStringAsync(Graph);
        Assert.DoesNotContain("opProgress", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("lastCommandId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("statusSenderChannel", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("statusReceiverChannel", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("outboundChannelStatus", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("inboundChannelStatus", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PutReplacesTheWholePlant()
    {
        var client = Client();
        using var _ = fixture.GrantEditPermission(client);
        var toDiscard = await AddDeviceAsync(client);

        var kept = Guid.NewGuid();
        var replacement = new PlantGraphDto(
            [new PlantItemConfigDto { ItemId = kept, Name = "Nuovo" }],
            [new PlantItemGeometryDto(kept, 5, 6, 7, 8, 0)],
            [],
            []);

        var put = await client.PutAsJsonAsync(Graph, replacement);
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        var after = await client.GetFromJsonAsync<PlantGraphDto>(Graph);
        Assert.NotNull(after);
        Assert.DoesNotContain(after!.Items, i => i.ItemId == toDiscard);
        var survivor = Assert.Single(after.Items);
        Assert.Equal(kept, survivor.ItemId);
        Assert.Equal("Nuovo", survivor.Name);
        var geometry = Assert.Single(after.Geometries, g => g.ItemId == kept);
        Assert.Equal(5, geometry.X);
    }

    [Fact]
    public async Task PutReplacesPlcConnectionsTogetherWithThePlant()
    {
        var client = Client();
        using var _ = fixture.GrantEditPermission(client);
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        var plcUrl = ApiRoutes.Prefix + ApiRoutes.PlcConnections;
        await fixture.DetachEveryDeviceFromItsPlcConnectionAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(plcUrl,
            new[] { new PlcConnectionDto(51, "old", "old", 1051, null, null) })).StatusCode);

        var itemId = Guid.NewGuid();
        var expectedConnection = new PlcConnectionDto(52, "restored", "10.0.0.52", 2052,
            "10.0.1.52", 3052);
        var replacement = new PlantGraphDto(
            [new PlantItemConfigDto { ItemId = itemId, DeviceConnectionId = 52 }],
            [new PlantItemGeometryDto(itemId, 1, 2, 3, 4, 0)], [], [], "drawing",
            [expectedConnection]);

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PutAsJsonAsync(Graph, replacement)).StatusCode);

        var restored = await client.GetFromJsonAsync<PlantGraphDto>(Graph);
        Assert.NotNull(restored);
        Assert.Equal(expectedConnection, Assert.Single(restored.PlcConnections!));
        Assert.Equal(52, Assert.Single(restored.Items).DeviceConnectionId);
    }

    [Fact]
    public async Task PutOfAnOldBackupWithoutPlcConnectionsPreservesTheCurrentOnes()
    {
        var client = Client();
        using var _ = fixture.GrantEditPermission(client);
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        var plcUrl = ApiRoutes.Prefix + ApiRoutes.PlcConnections;
        var existing = new PlcConnectionDto(53, "keep me", "10.0.0.53", 2053, null, null);
        await fixture.DetachEveryDeviceFromItsPlcConnectionAsync();
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PutAsJsonAsync(plcUrl, new[] { existing })).StatusCode);
        var itemId = Guid.NewGuid();
        var oldBackup = $$"""
            {
              "items": [{ "itemId": "{{itemId}}", "name": "from old backup", "deviceConnectionId": 53 }],
              "geometries": [],
              "junctions": [],
              "doorMappings": [],
              "decoration": null
            }
            """;

        var response = await client.PutAsync(Graph,
            new StringContent(oldBackup, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(existing,
            Assert.Single((await client.GetFromJsonAsync<List<PlcConnectionDto>>(plcUrl))!));
        var restored = await client.GetFromJsonAsync<PlantGraphDto>(Graph);
        Assert.Equal(itemId, Assert.Single(restored!.Items).ItemId);
        Assert.Equal(existing, Assert.Single(restored.PlcConnections!));
    }

    [Theory]
    [InlineData("zero")]
    [InlineData("negative")]
    [InlineData("duplicate")]
    [InlineData("missing-reference")]
    public async Task PutRejectsInvalidPlcConnectionsWithoutChangingThePlant(string invalidCase)
    {
        var client = Client();
        using var _ = fixture.GrantEditPermission(client);
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        var plcUrl = ApiRoutes.Prefix + ApiRoutes.PlcConnections;
        var originalItem = Guid.NewGuid();
        var originalConnection = new PlcConnectionDto(61, "original", null, null, null, null);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(Graph,
            new PlantGraphDto([new PlantItemConfigDto
                { ItemId = originalItem, DeviceConnectionId = 61 }], [], [], [], null,
                [originalConnection]))).StatusCode);

        var proposedItem = Guid.NewGuid();
        var (connections, reference) = invalidCase switch
        {
            "zero" => (new[] { new PlcConnectionDto(0, null, null, null, null, null) }, 0L),
            "negative" => (new[] { new PlcConnectionDto(-1, null, null, null, null, null) }, 0L),
            "duplicate" => (new[]
            {
                new PlcConnectionDto(62, "first", null, null, null, null),
                new PlcConnectionDto(62, "second", null, null, null, null)
            }, 62L),
            _ => (new[] { new PlcConnectionDto(62, null, null, null, null, null) }, 63L)
        };
        var invalid = new PlantGraphDto(
            [new PlantItemConfigDto { ItemId = proposedItem, DeviceConnectionId = reference }],
            [], [], [], null, connections);

        var response = await client.PutAsJsonAsync(Graph, invalid);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var after = await client.GetFromJsonAsync<PlantGraphDto>(Graph);
        Assert.NotNull(after);
        Assert.Equal(originalItem, Assert.Single(after.Items).ItemId);
        Assert.Equal(originalConnection, Assert.Single(after.PlcConnections!));
        Assert.Equal(originalConnection, Assert.Single((await client.GetFromJsonAsync<List<PlcConnectionDto>>(plcUrl))!));
    }

    [Fact]
    public async Task ReplacingTheGraphInvalidatesOldGeometryRequestsWithoutAddingVersionsToTheBackup()
    {
        var client = Client();
        using var _ = fixture.GrantEditPermission(client);
        var itemId = await AddDeviceAsync(client);
        var before = await client.GetFromJsonAsync<PlantItemDto>($"{Items}/{itemId}");
        Assert.NotNull(before);

        var exportedJson = await client.GetStringAsync(Graph);
        Assert.DoesNotContain("geometryVersion", exportedJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expectedGeometryVersion", exportedJson, StringComparison.OrdinalIgnoreCase);
        var exported = System.Text.Json.JsonSerializer.Deserialize<PlantGraphDto>(exportedJson,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(Graph, exported)).StatusCode);

        var after = await client.GetFromJsonAsync<PlantItemDto>($"{Items}/{itemId}");
        Assert.NotNull(after);
        Assert.NotEqual(before!.GeometryVersion, after!.GeometryVersion);

        var stale = await client.PutAsJsonAsync($"{Items}/{itemId}/geometry",
            new PlantItemGeometryUpdateDto(itemId, 10, 20, 30, 40, 0, before.GeometryVersion));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var conflict = await stale.Content.ReadFromJsonAsync<PlantItemGeometryConflictDto>();
        Assert.Equal([itemId], conflict!.ItemIds);
    }

    /// <summary>
    /// Il giro completo attraverso PUT /plant/graph, non solo dispositivi e geometrie
    /// (Importante 7 della revisione, punto 2: le mappature porte non erano mai state scritte
    /// attraverso questa rotta da nessun test, e il ramo <c>if (doorMappings.Count > 0)</c> di
    /// <c>PlantQueries.ReplaceGraphAsync</c> non era mai stato preso).
    /// </summary>
    [Fact]
    public async Task PutWritesJunctionsAndDoorMappings()
    {
        var client = Client();
        using var _ = fixture.GrantEditPermission(client);
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        var source = Guid.NewGuid();
        var destination = Guid.NewGuid();
        var junctionId = Guid.NewGuid();

        var replacement = new PlantGraphDto(
            [new PlantItemConfigDto { ItemId = source }, new PlantItemConfigDto { ItemId = destination }],
            [],
            [new JunctionDto(junctionId, source, destination, 3)],
            [new DoorMappingDto(0, source, "A", 42, "front", 1, false)]);

        var put = await client.PutAsJsonAsync(Graph, replacement);
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        var after = await client.GetFromJsonAsync<PlantGraphDto>(Graph);
        Assert.NotNull(after);
        Assert.Contains(after!.Junctions, j => j.JunctionId == junctionId && j.SourceItemId == source && j.DestinationItemId == destination && j.Priority == 3);
        Assert.Contains(after.DoorMappings, m => m.ItemId == source && m.DoorCode == "A" && m.CellId == 42);
    }

    [Theory]
    [InlineData(true, false, false, false, false, false)] // identificativo vuoto
    [InlineData(false, true, false, false, false, false)] // identificativo ripetuto
    [InlineData(false, false, true, false, false, false)] // cappio
    [InlineData(false, false, false, true, false, false)] // giunzione verso un dispositivo sconosciuto
    [InlineData(false, false, false, false, true, false)] // mappatura porte su un dispositivo sconosciuto
    [InlineData(false, false, false, false, false, true)] // identificativo della giunzione ripetuto
    public async Task PutWithAnInvalidGraphIsRejectedWith400AndTouchesNothing(
        bool emptyItemId, bool duplicateItemId, bool selfLoop, bool junctionToUnknownDevice, bool mappingOnUnknownDevice,
        bool duplicateJunctionId)
    {
        var client = Client();
        using var _ = fixture.GrantEditPermission(client);
        var survivor = await AddDeviceAsync(client);

        var a = Guid.NewGuid();
        var items = new List<PlantItemConfigDto> { new() { ItemId = a } };
        if (duplicateItemId) items.Add(new PlantItemConfigDto { ItemId = a });
        if (emptyItemId) items.Add(new PlantItemConfigDto { ItemId = Guid.Empty });

        List<JunctionDto> junctions = [];
        if (selfLoop) junctions.Add(new JunctionDto(Guid.NewGuid(), a, a, 1));
        if (junctionToUnknownDevice) junctions.Add(new JunctionDto(Guid.NewGuid(), a, Guid.NewGuid(), 1));
        if (duplicateJunctionId)
        {
            var junctionId = Guid.NewGuid();
            var destination = Guid.NewGuid();
            items.Add(new PlantItemConfigDto { ItemId = destination });
            junctions.Add(new JunctionDto(junctionId, a, destination, 1));
            junctions.Add(new JunctionDto(junctionId, destination, a, 2));
        }

        List<DoorMappingDto> mappings = [];
        if (mappingOnUnknownDevice) mappings.Add(new DoorMappingDto(0, Guid.NewGuid(), "A", 1, "front", 1, false));

        var replacement = new PlantGraphDto(items, [], junctions, mappings);

        var put = await client.PutAsJsonAsync(Graph, replacement);
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);

        // Correzione Critico 1 della revisione: la validazione precede lo svuotamento, quindi
        // l'impianto di prima e' intatto - non a meta' sostituito.
        var after = await client.GetFromJsonAsync<PlantGraphDto>(Graph);
        Assert.Contains(after!.Items, i => i.ItemId == survivor);
    }

    /// <summary>
    /// Due posizioni per lo stesso dispositivo (voce 5 della coda dei lavori). Fino al 28/9 le
    /// fermava solo la pagina di importazione; l'host le lasciava passare e il motore teneva
    /// l'ultima senza dirlo. Ora il 400 nomina il dispositivo, e l'impianto di prima resta
    /// com'era: il dispositivo che c'era, con la sua posizione, e nessuna traccia di quello proposto.
    /// </summary>
    [Fact]
    public async Task PutWithTwoPositionsForTheSameDeviceIsRejectedWith400NamingItAndWritesNothing()
    {
        var client = Client();
        using var _ = fixture.GrantEditPermission(client);
        var survivor = await AddDeviceAsync(client);
        var survivorBefore = await client.GetFromJsonAsync<PlantItemDto>($"{Items}/{survivor}");
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{Items}/{survivor}/geometry",
            new PlantItemGeometryUpdateDto(survivor, 10, 20, 30, 40, 0, survivorBefore!.GeometryVersion))).StatusCode);

        var twice = Guid.NewGuid();
        var replacement = new PlantGraphDto(
            [new PlantItemConfigDto { ItemId = twice }],
            [new PlantItemGeometryDto(twice, 1, 2, 3, 4, 0), new PlantItemGeometryDto(twice, 5, 6, 7, 8, 0)],
            [],
            []);

        var put = await client.PutAsJsonAsync(Graph, replacement);

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        var problem = await put.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Contains(twice.ToString(), problem!.Detail);
        Assert.Contains("più di una posizione", problem.Detail);
        var after = await client.GetFromJsonAsync<PlantGraphDto>(Graph);
        Assert.Contains(after!.Items, i => i.ItemId == survivor);
        Assert.DoesNotContain(after.Items, i => i.ItemId == twice);
        Assert.DoesNotContain(after.Geometries, g => g.ItemId == twice);
        var geometry = Assert.Single(after.Geometries, g => g.ItemId == survivor);
        Assert.Equal(10, geometry.X);
        Assert.Equal(20, geometry.Y);
    }

    /// <summary>
    /// Due giunzioni fra gli stessi due dispositivi nello stesso verso (voce 17 della coda,
    /// decisione del proprietario del 28/9): rifiutate come fanno i due editor. Fino ad allora il
    /// 400 guardava solo l'identificativo ripetuto, e il motore le scriveva tutte e due. Anche
    /// senza identificativo, come negli archivi vecchi. Il 400 nomina i due dispositivi, e
    /// l'impianto di prima resta com'era.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PutWithTwoJunctionsBetweenTheSameDevicesIsRejectedWith400NamingThemAndWritesNothing(bool withoutIdentifiers)
    {
        var client = Client();
        using var _ = fixture.GrantEditPermission(client);
        var survivor = await AddDeviceAsync(client);

        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var replacement = new PlantGraphDto(
            [new PlantItemConfigDto { ItemId = a, Name = "Rulliera" }, new PlantItemConfigDto { ItemId = b, Name = "Navetta" }],
            [],
            [
                new JunctionDto(withoutIdentifiers ? Guid.Empty : Guid.NewGuid(), a, b, 1),
                new JunctionDto(withoutIdentifiers ? Guid.Empty : Guid.NewGuid(), a, b, 99),
            ],
            []);

        var put = await client.PutAsJsonAsync(Graph, replacement);

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        var problem = await put.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Contains("Rulliera", problem!.Detail);
        Assert.Contains("Navetta", problem.Detail);
        var after = await client.GetFromJsonAsync<PlantGraphDto>(Graph);
        Assert.Contains(after!.Items, i => i.ItemId == survivor);
        Assert.DoesNotContain(after.Items, i => i.ItemId == a || i.ItemId == b);
        Assert.DoesNotContain(after.Junctions, j => j.SourceItemId == a || j.DestinationItemId == a);
    }

    /// <summary>A->B e B->A sono due giunzioni, non un doppione: passano tutte e due.</summary>
    [Fact]
    public async Task PutWithAJunctionEachWayBetweenTheSameTwoDevicesWritesBoth()
    {
        var client = Client();
        using var _ = fixture.GrantEditPermission(client);
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var there = Guid.NewGuid();
        var back = Guid.NewGuid();

        var put = await client.PutAsJsonAsync(Graph, new PlantGraphDto(
            [new PlantItemConfigDto { ItemId = a }, new PlantItemConfigDto { ItemId = b }],
            [],
            [new JunctionDto(there, a, b, 1), new JunctionDto(back, b, a, 99)],
            []));

        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        var after = await client.GetFromJsonAsync<PlantGraphDto>(Graph);
        Assert.Contains(after!.Junctions, j => j.JunctionId == there && j.SourceItemId == a && j.DestinationItemId == b);
        Assert.Contains(after.Junctions, j => j.JunctionId == back && j.SourceItemId == b && j.DestinationItemId == a);
    }

    /// <summary>
    /// Il quinto membro del contratto (decisione del proprietario del 2026-09-19: «il file e' un
    /// backup completo»). Tre fatti in una prova sola perche' sono la stessa distinzione vista dai
    /// due lati: l'esportazione porta via il disegno libero, un file che lo porta lo
    /// <b>sostituisce</b>, e un file che <b>non</b> lo porta lascia in piedi quello che c'e'.
    /// </summary>
    [Fact]
    public async Task TheDecorationTravelsInTheGraphAndOnlyAFileThatCarriesItReplacesIt()
    {
        var client = Client();
        using var _ = fixture.GrantEditPermission(client);
        var itemId = await AddDeviceAsync(client);

        const string drawn = "<mxCell id=\"nota\" value=\"scritta a mano\"/>";
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(
            ApiRoutes.Prefix + ApiRoutes.PlantDecoration, new PlantDecorationDto(drawn))).StatusCode);

        // 1. L'esportazione se lo porta dietro.
        var exported = await client.GetFromJsonAsync<PlantGraphDto>(Graph);
        Assert.Equal(drawn, exported!.Decoration);

        // 2. Un file VECCHIO - senza il campo - non parla del disegno: resta quello che c'e'.
        //    Il file si costruisce come lo costruirebbe un'esportazione di allora, cioe' un JSON
        //    che il campo non ce l'ha proprio, non un DTO con Decoration a nullo: e' la
        //    deserializzazione di quel JSON che deve dare nullo, ed e' quella che si sta provando.
        var oldFileJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            Items = new[] { new { ItemId = itemId } },
            Geometries = Array.Empty<object>(),
            Junctions = Array.Empty<object>(),
            DoorMappings = Array.Empty<object>(),
        });
        Assert.DoesNotContain("Decoration", oldFileJson, StringComparison.Ordinal);
        var oldFile = System.Text.Json.JsonSerializer.Deserialize<PlantGraphDto>(oldFileJson);
        Assert.Null(oldFile!.Decoration);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(Graph, oldFile)).StatusCode);
        Assert.Equal(drawn, (await client.GetFromJsonAsync<PlantGraphDto>(Graph))!.Decoration);

        // 3. Un file che lo porta lo sostituisce - e lo sostituisce anche con il vuoto, che e' un
        //    ripristino e non una svista.
        const string restored = "<mxCell id=\"altra\" value=\"un'altra scritta\"/>";
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PutAsJsonAsync(Graph, oldFile with { Decoration = restored })).StatusCode);
        Assert.Equal(restored, (await client.GetFromJsonAsync<PlantGraphDto>(Graph))!.Decoration);

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PutAsJsonAsync(Graph, oldFile with { Decoration = "" })).StatusCode);
        Assert.Equal("", (await client.GetFromJsonAsync<PlantGraphDto>(Graph))!.Decoration);
    }

    [Fact]
    public async Task PutWithoutThePermissionHeaderIsRefusedWith423()
    {
        var client = Client();
        var put = await client.PutAsJsonAsync(Graph, new PlantGraphDto([], [], [], []));
        Assert.Equal(423, (int)put.StatusCode);
    }

    [Fact]
    public async Task GraphRoutesAreUnavailableWhileMemoryReloadHasFailed()
    {
        var previousLoadError = Navigator.LoadError;
        try
        {
            Navigator.LoadError = "reload-test-failure";
            var client = Client();
            var response = await client.GetAsync(Graph);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Contains("reload-test-failure", (await response.Content.ReadFromJsonAsync<ProblemDetails>())!.Detail);
            var status = await client.GetFromJsonAsync<EngineStatus>(ApiRoutes.Prefix + ApiRoutes.Status);
            Assert.Equal("reload-test-failure", status!.EngineLoadError);
        }
        finally { Navigator.LoadError = previousLoadError; }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PutWithPendingWorkReturnsConflictAndPreservesTheGraph(bool mission)
    {
        var client = Client();
        using var permission = fixture.GrantEditPermission(client);
        var survivor = await AddDeviceAsync(client);
        var before = await client.GetStringAsync(Graph);
        var missionId = Guid.NewGuid();
        await using var context = fixture.Store.CreateContext();
        var queueRow = new ExtractionQueueItem { SourceItemId = survivor };
        try
        {
            if (mission) context.Missions.Add(new Mission { MissionId = missionId });
            else context.ExtractionQueue.Add(queueRow);
            await context.SaveChangesAsync();

            var response = await client.PutAsJsonAsync(Graph, new PlantGraphDto([], [], [], []));
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var problem = (await response.Content.ReadFromJsonAsync<ProblemDetails>())!;
            Assert.Contains("lavoro pendente", problem.Title);
            Assert.Contains("Missioni o coda di estrazione", problem.Detail);
            Assert.Equal(before, await client.GetStringAsync(Graph));
            if (mission) Assert.True(await context.Missions.AnyAsync(m => m.MissionId == missionId));
            else Assert.True(await context.ExtractionQueue.AnyAsync(q => q.Id == queueRow.Id));
        }
        finally
        {
            await context.Missions.Where(m => m.MissionId == missionId).ExecuteDeleteAsync();
            if (!mission) await context.ExtractionQueue.Where(q => q.Id == queueRow.Id).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task PutWhileTheEngineIsRunningIsRefusedWith409()
    {
        var client = Client();
        using var _ = fixture.GrantEditPermission(client);
        Navigator.NavStatus = NavigatorStatusTypes.Run;

        var put = await client.PutAsJsonAsync(Graph, new PlantGraphDto([], [], [], []));
        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
    }

    [Fact]
    public async Task LegacyGeometryReadsThePositionFromAnOldDrawingWithoutWritingAnything()
    {
        var client = Client();
        using var _ = fixture.GrantEditPermission(client);
        var itemId = await AddDeviceAsync(client);

        using var body = new ByteArrayContent(SampleDiagramXml(itemId, 11, 22, 33, 44));
        body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        var response = await client.PostAsync(LegacyGeometry, body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<LegacyGeometryImportDto>();
        Assert.NotNull(result);
        var geometry = Assert.Single(result!.Geometries);
        Assert.Equal(itemId, geometry.ItemId);
        Assert.Equal(11, geometry.X);
        Assert.Equal(0, result.ElementsWithoutGuid);
        Assert.Equal(0, result.GuidsWithoutGeometry);
        Assert.Equal(0, result.PartiallyPlaced);

        // Nessuna scrittura: il dispositivo mantiene la geometria che aveva prima (nessuna, non
        // essendo mai stata collocata) - la lettura sola non deve aver toccato lo store.
        var graph = await client.GetFromJsonAsync<PlantGraphDto>(Graph);
        Assert.Null(graph!.Geometries.Single(g => g.ItemId == itemId).X);
    }

    /// <summary>
    /// Reading an old drawing writes nothing, so it does not ask for the edit permit - neither when
    /// nobody holds it (the state /editor/drawio is in until its first write, measured on 27/9: the
    /// pick of a .edd there came back 423 every time) nor when somebody else does. The import's
    /// write, <c>PUT /plant/graph</c>, still requires it.
    /// </summary>
    [Fact]
    public async Task LegacyGeometryIsReadWithoutThePermission()
    {
        var itemId = Guid.NewGuid();

        var nobodyHoldsIt = await PostLegacyAsync(Client(), itemId);
        Assert.Equal(HttpStatusCode.OK, nobodyHoldsIt.StatusCode);
        Assert.Equal(itemId, Assert.Single((await nobodyHoldsIt.Content.ReadFromJsonAsync<LegacyGeometryImportDto>())!.Geometries).ItemId);

        var somebodyElse = Client();
        using var _ = fixture.GrantEditPermission(somebodyElse);
        var somebodyElseHoldsIt = await PostLegacyAsync(Client(), itemId);
        Assert.Equal(HttpStatusCode.OK, somebodyElseHoldsIt.StatusCode);

        static async Task<HttpResponseMessage> PostLegacyAsync(HttpClient client, Guid itemId)
        {
            using var body = new ByteArrayContent(SampleDiagramXml(itemId, 1, 2, 3, 4));
            body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            return await client.PostAsync(LegacyGeometry, body);
        }
    }

    /// <summary>
    /// Un disegno minimo con un solo dispositivo, nella stessa forma usata da
    /// <c>Wcs.Engine.Test.DiagramGeometryReaderTest</c>: stesso spazio dei nomi DataContract, stesso
    /// filtro <c>OrchestraType=ITEM</c>, stesso <c>BoundingRect</c> con i figli minuscoli in ordine
    /// alfabetico nello spazio dei nomi di <c>System.Drawing</c>.
    /// </summary>
    private static byte[] SampleDiagramXml(Guid itemId, double x, double y, double w, double h)
    {
        const string ns = "http://schemas.datacontract.org/2004/07/Syncfusion.Windows.Forms.Diagram";
        const string arraysNs = "http://schemas.microsoft.com/2003/10/Serialization/Arrays";
        var i = System.Globalization.CultureInfo.InvariantCulture;
        return Encoding.UTF8.GetBytes(
            $"""
             <DiagramDocumentWrapper xmlns="{ns}" xmlns:i="http://www.w3.org/2001/XMLSchema-instance"
                 xmlns:z="http://schemas.microsoft.com/2003/10/Serialization/" xmlns:a="{arraysNs}">
               <Model><Nodes>
                 <Node>
                   <PropertyBag>
                     <a:KeyValueOfstringanyType>
                       <a:Key>OrchestraType</a:Key>
                       <a:Value i:type="a:string">ITEM</a:Value>
                     </a:KeyValueOfstringanyType>
                   </PropertyBag>
                   <Tag>{itemId}</Tag>
                   <BoundingRect xmlns:a="http://schemas.datacontract.org/2004/07/System.Drawing"><a:height>{h.ToString(i)}</a:height><a:width>{w.ToString(i)}</a:width><a:x>{x.ToString(i)}</a:x><a:y>{y.ToString(i)}</a:y></BoundingRect>
                 </Node>
               </Nodes></Model>
             </DiagramDocumentWrapper>
             """);
    }
}
