using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Wcs.Api.Contracts;
using Wcs.Api.Contracts.Dtos;
using Wcs.Engine;
using Wcs.Engine.Collections;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Host.Test;

/// <summary>
/// Compito 4b della console (decisione del proprietario del 26/9): le due guardie sulla
/// sostituzione delle connessioni PLC - il permesso di modifica e il motore fermo - su
/// <c>PUT /plc/connections</c> e sull'altra porta agli stessi dati, <c>PUT /plant/graph</c> quando
/// il file porta le connessioni.
/// </summary>
/// <remarks>
/// <b>Fermo vuol dire Stop, non «non in Run».</b> Le connessioni del driver nascono solo al
/// caricamento dell'impianto (<c>DeviceConnections.Initialize</c>, da
/// <c>Navigator.LoadNavigatorData</c>); in Pause restano aperte e la ripresa non le riapre, in
/// Recover il caricamento le sta per costruire leggendo proprio questa tabella, in Dispose il motore
/// si sta spegnendo. Solo in Stop una sostituzione non tocca connessioni che il motore sta usando.
/// </remarks>
[Collection("host")]
public sealed class PlcConnectionsGuardsTest(HostFixture host) : IDisposable
{
    private const string Plc = ApiRoutes.Prefix + ApiRoutes.PlcConnections;
    private const string Graph = ApiRoutes.Prefix + ApiRoutes.PlantGraph;

    private static readonly PlcConnectionDto Before = new(61, "before", "10.0.0.61", 2061, "10.0.1.61", 3061);
    private static readonly PlcConnectionDto Proposed = new(62, "proposed", "10.0.0.62", 2062, "10.0.1.62", 3062);

    private readonly NavigatorStatusTypes _previousStatus = Navigator.NavStatus;

    public void Dispose()
    {
        Navigator.NavStatus = _previousStatus;
        PathDevices.Instance.Clear();
        PathJunctions.Instance.Clear();
    }

    /// <summary>
    /// L'elenco di partenza, scritto per la via ufficiale: motore fermo, permesso preso. Prima nessun
    /// dispositivo lasciato da altre prove resta su una connessione: dal compito 4c l'host rifiuta di
    /// togliere una connessione che un dispositivo usa, e lo store e' condiviso dalla collezione.
    /// </summary>
    private async Task SeedAsync()
    {
        await host.DetachEveryDeviceFromItsPlcConnectionAsync();
        var client = host.CreateAuthenticatedClient();
        using var _ = host.GrantEditPermission(client);
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(Plc, new[] { Before })).StatusCode);
        // Letto, non dato per scontato: il «niente e' stato sostituito» delle prove vale solo se il
        // punto di partenza e' davvero questo, e non quello lasciato da una prova precedente.
        Assert.Equal([Before], await OnTheHostAsync());
    }

    private async Task<List<PlcConnectionDto>> OnTheHostAsync() =>
        (await host.CreateAuthenticatedClient().GetFromJsonAsync<List<PlcConnectionDto>>(Plc))!;

    [Fact]
    public async Task WithoutThePermitTheReplacementIsRefusedWith423AndNothingIsReplaced()
    {
        await SeedAsync();
        var client = host.CreateAuthenticatedClient();
        Navigator.NavStatus = NavigatorStatusTypes.Stop;

        var put = await client.PutAsJsonAsync(Plc, new[] { Proposed });

        Assert.Equal(423, (int)put.StatusCode);
        Assert.Equal([Before], await OnTheHostAsync());
    }

    /// <summary>Ogni stato che non e' Stop: Run, e anche Pause, Recover e Dispose (si veda la classe).</summary>
    [Theory]
    [InlineData(NavigatorStatusTypes.Run)]
    [InlineData(NavigatorStatusTypes.Pause)]
    [InlineData(NavigatorStatusTypes.Recover)]
    [InlineData(NavigatorStatusTypes.Dispose)]
    public async Task WithThePermitAndTheEngineNotStoppedTheReplacementIsRefusedWith409AndNothingIsReplaced(
        NavigatorStatusTypes state)
    {
        await SeedAsync();
        var client = host.CreateAuthenticatedClient();
        using var _ = host.GrantEditPermission(client);
        Navigator.NavStatus = state;

        var put = await client.PutAsJsonAsync(Plc, new[] { Proposed });

        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
        var problem = (await put.Content.ReadFromJsonAsync<ProblemDetails>())!;
        Assert.Equal("Il motore non è fermo", problem.Title);
        Assert.Contains($"il motore è in {state}", problem.Detail);
        Assert.Equal([Before], await OnTheHostAsync());
    }

    [Fact]
    public async Task WithThePermitAndTheEngineStoppedTheListIsReplaced()
    {
        await SeedAsync();
        var client = host.CreateAuthenticatedClient();
        using var _ = host.GrantEditPermission(client);
        Navigator.NavStatus = NavigatorStatusTypes.Stop;

        var put = await client.PutAsJsonAsync(Plc, new[] { Proposed });

        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        Assert.Equal([Proposed], await OnTheHostAsync());
    }

    /// <summary>
    /// L'altra porta: un impianto che porta le connessioni ha la stessa regola della rotta PLC - in
    /// Run (dove era gia' rifiutato, come ogni sostituzione dell'impianto) e in ogni altro stato che
    /// non sia Stop.
    /// </summary>
    [Theory]
    [InlineData(NavigatorStatusTypes.Run)]
    [InlineData(NavigatorStatusTypes.Pause)]
    [InlineData(NavigatorStatusTypes.Recover)]
    [InlineData(NavigatorStatusTypes.Dispose)]
    public async Task AGraphCarryingPlcConnectionsIsRefusedWith409UnlessTheEngineIsStopped(NavigatorStatusTypes state)
    {
        await SeedAsync();
        var client = host.CreateAuthenticatedClient();
        using var _ = host.GrantEditPermission(client);
        var before = await client.GetStringAsync(Graph);
        Navigator.NavStatus = state;
        var itemId = Guid.NewGuid();

        var put = await client.PutAsJsonAsync(Graph, new PlantGraphDto(
            [new PlantItemConfigDto { ItemId = itemId, DeviceConnectionId = Proposed.Id }],
            [], [], [], null, [Proposed]));

        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
        Assert.Contains($"il motore è in {state}", (await put.Content.ReadFromJsonAsync<ProblemDetails>())!.Detail);
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        Assert.Equal(before, await client.GetStringAsync(Graph));
        Assert.Equal([Before], await OnTheHostAsync());
    }

    /// <summary>
    /// Il rilassamento del compito 4c (in Pause un file con le connessioni dell'host passava) non c'e'
    /// piu': dalla decisione del proprietario del 27/9 sulla Pause un impianto si importa solo a motore
    /// fermo, qualunque cosa porti - l'importazione ricostruisce i dispositivi senza connessione, e la
    /// ripresa da Pause non gliela darebbe (PlantQueries.NewDevicesWaitForStop).
    /// </summary>
    [Fact]
    public async Task AGraphCarryingTheSamePlcConnectionsIsRefusedInPauseToo()
    {
        await SeedAsync();
        var client = host.CreateAuthenticatedClient();
        using var _ = host.GrantEditPermission(client);
        var before = await client.GetStringAsync(Graph);
        Navigator.NavStatus = NavigatorStatusTypes.Pause;

        var put = await client.PutAsJsonAsync(Graph, new PlantGraphDto(
            [new PlantItemConfigDto { ItemId = Guid.NewGuid(), DeviceConnectionId = Before.Id }], [], [], [], null, [Before]));

        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
        Assert.Contains("il motore è in Pause", (await put.Content.ReadFromJsonAsync<ProblemDetails>())!.Detail);
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        Assert.Equal(before, await client.GetStringAsync(Graph));
        Assert.Equal([Before], await OnTheHostAsync());
    }

    /// <summary>
    /// Un solo valore di una connessione cambiato basta: e' una connessione modificata, e in Pause
    /// l'importazione e' rifiutata come ogni cambio delle connessioni; niente e' scritto.
    /// </summary>
    [Fact]
    public async Task AGraphChangingOneValueOfAPlcConnectionIsRefusedInPauseAndNothingIsWritten()
    {
        await SeedAsync();
        var client = host.CreateAuthenticatedClient();
        using var _ = host.GrantEditPermission(client);
        var before = await client.GetStringAsync(Graph);
        Navigator.NavStatus = NavigatorStatusTypes.Pause;

        var put = await client.PutAsJsonAsync(Graph, new PlantGraphDto(
            [new PlantItemConfigDto { ItemId = Guid.NewGuid(), DeviceConnectionId = Before.Id }], [], [], [], null,
            [Before with { LocalPort = 4061 }]));

        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
        var problem = (await put.Content.ReadFromJsonAsync<ProblemDetails>())!;
        Assert.Equal("Il motore non è fermo", problem.Title);
        Assert.Contains("il motore è in Pause", problem.Detail);
        Assert.Contains("si importa solo a motore fermo (Stop)", problem.Detail);
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        Assert.Equal(before, await client.GetStringAsync(Graph));
        Assert.Equal([Before], await OnTheHostAsync());
    }

    /// <summary>
    /// In Recover e in Dispose un file che porta le connessioni e' rifiutato anche con le stesse, come
    /// ogni importazione fuori da Stop. Il rifiuto dice lo stato del motore e non pretende che il file
    /// cambi le connessioni.
    /// </summary>
    [Theory]
    [InlineData(NavigatorStatusTypes.Recover)]
    [InlineData(NavigatorStatusTypes.Dispose)]
    public async Task AGraphCarryingTheSamePlcConnectionsIsStillRefusedInRecoverAndDispose(NavigatorStatusTypes state)
    {
        await SeedAsync();
        var client = host.CreateAuthenticatedClient();
        using var _ = host.GrantEditPermission(client);
        var before = await client.GetStringAsync(Graph);
        Navigator.NavStatus = state;

        var put = await client.PutAsJsonAsync(Graph, new PlantGraphDto(
            [new PlantItemConfigDto { ItemId = Guid.NewGuid(), DeviceConnectionId = Before.Id }], [], [], [], null, [Before]));

        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
        var problem = (await put.Content.ReadFromJsonAsync<ProblemDetails>())!;
        Assert.Equal("Il motore non è fermo", problem.Title);
        Assert.Contains($"il motore è in {state}", problem.Detail);
        Assert.DoesNotContain("cambia le connessioni PLC", problem.Detail);
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        Assert.Equal(before, await client.GetStringAsync(Graph));
        Assert.Equal([Before], await OnTheHostAsync());
    }

    /// <summary>In Run resta rifiutato anche con le stesse connessioni, come ogni sostituzione dell'impianto.</summary>
    [Fact]
    public async Task AGraphCarryingTheSamePlcConnectionsIsStillRefusedInRun()
    {
        await SeedAsync();
        var client = host.CreateAuthenticatedClient();
        using var _ = host.GrantEditPermission(client);
        var before = await client.GetStringAsync(Graph);
        Navigator.NavStatus = NavigatorStatusTypes.Run;

        var put = await client.PutAsJsonAsync(Graph, new PlantGraphDto(
            [new PlantItemConfigDto { ItemId = Guid.NewGuid(), DeviceConnectionId = Before.Id }], [], [], [], null, [Before]));

        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
        Assert.Contains("il motore è in Run", (await put.Content.ReadFromJsonAsync<ProblemDetails>())!.Detail);
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        Assert.Equal(before, await client.GetStringAsync(Graph));
    }

    /// <summary>
    /// Anche un impianto che le connessioni non le porta (un file esportato prima che il campo
    /// esistesse) si importa solo a motore fermo, dalla decisione del 27/9 sulla Pause: prima restava
    /// alla regola delle altre scritture (accettato in Pause, decisione del 2026-09-15).
    /// </summary>
    [Fact]
    public async Task AGraphWithoutPlcConnectionsIsRefusedInPauseToo()
    {
        await SeedAsync();
        var client = host.CreateAuthenticatedClient();
        using var _ = host.GrantEditPermission(client);
        var before = await client.GetStringAsync(Graph);
        Navigator.NavStatus = NavigatorStatusTypes.Pause;

        var put = await client.PutAsJsonAsync(Graph, new PlantGraphDto(
            [new PlantItemConfigDto { ItemId = Guid.NewGuid(), DeviceConnectionId = Before.Id }], [], [], []));

        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        Assert.Equal(before, await client.GetStringAsync(Graph));
        Assert.Equal([Before], await OnTheHostAsync());
    }
}
