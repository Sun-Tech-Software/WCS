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
/// Le decisioni del proprietario del 27/9 (revisione finale della console dell'Editor), viste dalle
/// rotte: D1, la modalita' di connessione - di tutto l'impianto, o il tipo di connessione e la
/// connessione PLC di un dispositivo - si cambia solo a motore fermo (Stop), mentre le altre
/// proprieta' restano scrivibili in Pause; D2, le scritture dell'impianto rifiutate in Run lo sono
/// anche in Recover e in Dispose, con il 409 che nomina lo stato.
/// </summary>
[Collection("host")]
public sealed class PlantWritesOutsideStopTest(HostFixture host) : IDisposable
{
    private const string Items = ApiRoutes.Prefix + ApiRoutes.Items;
    private readonly NavigatorStatusTypes _previous = Navigator.NavStatus;

    public void Dispose()
    {
        PathDevices.Instance.Clear();
        PathJunctions.Instance.Clear();
        Navigator.NavStatus = _previous;
    }

    private static PlantItemConfigDto Device(Guid id, string name = "Navetta", ConnectionTypes connection = ConnectionTypes.Test, decimal plc = 0) =>
        new() { ItemId = id, Name = name, DeviceType = (short)DeviceTypes.Automation, ConnectionType = (short)connection, DeviceConnectionId = plc };

    /// <summary>Un dispositivo creato per la via ufficiale, a motore fermo.</summary>
    private async Task<Guid> ADeviceAsync(HttpClient client)
    {
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        var id = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(Items, Device(id))).StatusCode);
        return id;
    }

    private static async Task<PlantItemConfigDto> OnTheHostAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<PlantItemConfigDto>($"{Items}/{id}/config"))!;

    [Theory]
    [InlineData(NavigatorStatusTypes.Recover)]
    [InlineData(NavigatorStatusTypes.Dispose)]
    public async Task InRecoverAndDisposeAConfigurationWriteIs409NamingTheState(NavigatorStatusTypes state)
    {
        var client = host.CreateAuthenticatedClient();
        using var _ = host.GrantEditPermission(client);
        var id = await ADeviceAsync(client);
        Navigator.NavStatus = state;

        var put = await client.PutAsJsonAsync($"{Items}/{id}/config", Device(id, name: "Rinominata"));

        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
        var problem = (await put.Content.ReadFromJsonAsync<ProblemDetails>())!;
        Assert.Equal("Il motore non è fermo", problem.Title);
        Assert.Contains($"il motore è in {state}", problem.Detail);
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        Assert.Equal("Navetta", (await OnTheHostAsync(client, id)).Name);
    }

    [Fact]
    public async Task InPauseAConfigurationWriteThatLeavesTheConnectionAloneIsAccepted()
    {
        var client = host.CreateAuthenticatedClient();
        using var _ = host.GrantEditPermission(client);
        var id = await ADeviceAsync(client);
        Navigator.NavStatus = NavigatorStatusTypes.Pause;

        var put = await client.PutAsJsonAsync($"{Items}/{id}/config", Device(id, name: "Rinominata in pausa"));

        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        Assert.Equal("Rinominata in pausa", (await OnTheHostAsync(client, id)).Name);
    }

    [Theory]
    [InlineData(NavigatorStatusTypes.Pause, true)]
    [InlineData(NavigatorStatusTypes.Pause, false)]
    public async Task InPauseChangingADevicesConnectionIs409AndNothingIsWritten(NavigatorStatusTypes state, bool connectionType)
    {
        var client = host.CreateAuthenticatedClient();
        using var _ = host.GrantEditPermission(client);
        var id = await ADeviceAsync(client);
        Navigator.NavStatus = state;

        var put = await client.PutAsJsonAsync($"{Items}/{id}/config", connectionType
            ? Device(id, name: "Rinominata", connection: ConnectionTypes.Connected)
            : Device(id, name: "Rinominata", plc: 7));

        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
        var problem = (await put.Content.ReadFromJsonAsync<ProblemDetails>())!;
        Assert.Equal("Il motore non è fermo", problem.Title);
        Assert.Contains($"il motore è in {state}", problem.Detail);
        Assert.Contains("la ripresa da Pause non le riapre", problem.Detail);
        var stored = await OnTheHostAsync(client, id);
        Assert.Equal(("Navetta", (short)ConnectionTypes.Test, 0m), (stored.Name, stored.ConnectionType, stored.DeviceConnectionId));
    }

    /// <summary>
    /// Decisione del proprietario del 27/9 sulla Pause: un dispositivo nasce solo a motore fermo -
    /// la creazione dalla tavolozza e la duplicazione della pagina passano entrambe da qui
    /// (<c>POST /plant/items</c>) - perche' nascerebbe senza connessione e la ripresa da Pause non gliela
    /// darebbe.
    /// </summary>
    [Theory]
    [InlineData(NavigatorStatusTypes.Pause)]
    [InlineData(NavigatorStatusTypes.Recover)]
    [InlineData(NavigatorStatusTypes.Dispose)]
    public async Task ADeviceIsCreatedOnlyWithTheEngineStopped(NavigatorStatusTypes state)
    {
        var client = host.CreateAuthenticatedClient();
        using var _ = host.GrantEditPermission(client);
        Navigator.NavStatus = state;
        var id = Guid.NewGuid();

        var post = await client.PostAsJsonAsync(Items, Device(id));

        Assert.Equal(HttpStatusCode.Conflict, post.StatusCode);
        var problem = (await post.Content.ReadFromJsonAsync<ProblemDetails>())!;
        Assert.Equal("Il motore non è fermo", problem.Title);
        Assert.Contains($"il motore è in {state}", problem.Detail);
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Items}/{id}/config")).StatusCode);
    }

    /// <summary>
    /// La stessa decisione per l'importazione, qualunque file: con o senza le connessioni PLC, anche con
    /// quelle dell'host (il rilassamento del compito 4c in Pause non c'e' piu').
    /// </summary>
    [Theory]
    [InlineData(NavigatorStatusTypes.Pause, false)]
    [InlineData(NavigatorStatusTypes.Pause, true)]
    [InlineData(NavigatorStatusTypes.Recover, false)]
    [InlineData(NavigatorStatusTypes.Dispose, false)]
    public async Task APlantIsImportedOnlyWithTheEngineStopped(NavigatorStatusTypes state, bool withTheHostsOwnPlcConnections)
    {
        var client = host.CreateAuthenticatedClient();
        using var _ = host.GrantEditPermission(client);
        await ADeviceAsync(client);
        var before = await client.GetStringAsync(ApiRoutes.Prefix + ApiRoutes.PlantGraph);
        var plc = withTheHostsOwnPlcConnections
            ? await client.GetFromJsonAsync<List<PlcConnectionDto>>(ApiRoutes.Prefix + ApiRoutes.PlcConnections)
            : null;
        Navigator.NavStatus = state;

        var put = await client.PutAsJsonAsync(ApiRoutes.Prefix + ApiRoutes.PlantGraph,
            new PlantGraphDto([Device(Guid.NewGuid(), "Importata")], [], [], [], null, plc));

        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
        Assert.Contains($"il motore è in {state}", (await put.Content.ReadFromJsonAsync<ProblemDetails>())!.Detail);
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        Assert.Equal(before, await client.GetStringAsync(ApiRoutes.Prefix + ApiRoutes.PlantGraph));
    }

    [Theory]
    [InlineData(NavigatorStatusTypes.Pause)]
    [InlineData(NavigatorStatusTypes.Recover)]
    [InlineData(NavigatorStatusTypes.Dispose)]
    public async Task TheConnectionModeOfThePlantIs409UnlessTheEngineIsStopped(NavigatorStatusTypes state)
    {
        var client = host.CreateAuthenticatedClient();
        using var _ = host.GrantEditPermission(client);
        var id = await ADeviceAsync(client);
        Navigator.NavStatus = state;

        var post = await client.PostAsJsonAsync($"{Items}/connection-type", new ConnectionTypeUpdate(ConnectionTypes.Connected));

        Assert.Equal(HttpStatusCode.Conflict, post.StatusCode);
        Assert.Contains($"il motore è in {state}", (await post.Content.ReadFromJsonAsync<ProblemDetails>())!.Detail);
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        Assert.Equal((short)ConnectionTypes.Test, (await OnTheHostAsync(client, id)).ConnectionType);
    }
}
