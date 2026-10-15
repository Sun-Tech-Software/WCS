using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Wcs.Api.Contracts;
using Wcs.Api.Contracts.Dtos;
using Wcs.Engine;
using Wcs.Engine.Persistence.Entities;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Host.Test;

/// <summary>
/// Compito 4c della console (decisione del proprietario del 27/9, opzione A): <c>PUT /plc/connections</c>
/// rifiuta un elenco che toglie una connessione ancora usata da un dispositivo dell'impianto
/// (<c>DeviceConnectionId</c>), con <c>409</c> e un testo che nomina la connessione e i dispositivi da
/// spostare prima. Non sostituisce niente.
/// </summary>
/// <remarks>
/// <b>Perche'.</b> <c>PlcSettingsStore.ReplaceConnectionsAsync</c> cancella ogni riga non proposta
/// senza guardare i dispositivi. Un dispositivo rimasto su una connessione tolta tiene l'oggetto della
/// vecchia connessione fino al prossimo caricamento dell'impianto, e i suoi comandi falliscono appena
/// serve un nuovo blocco di identificativi (<c>ReserveCommandIdBlock</c>: «PLC {id} not found»).
/// <para>
/// <b>«Nessuna connessione» e' zero.</b> Un dispositivo con <c>DeviceConnectionId</c> 0 (o minore) non
/// ne usa nessuna: <c>DeviceConnections.Initialize</c> e <c>OpenConnection</c> guardano solo i valori
/// maggiori di zero, e il simulatore lo serve. Non blocca niente, nemmeno una connessione con Id 0 -
/// che la rotta accetta, e che e' l'unico modo di provare che lo zero e' davvero escluso.
/// </para>
/// </remarks>
[Collection("host")]
public sealed class PlcConnectionsInUseTest(HostFixture host) : IDisposable
{
    private const string Plc = ApiRoutes.Prefix + ApiRoutes.PlcConnections;

    private static readonly PlcConnectionDto Line71 = new(71, "Line 71", "10.0.0.71", 2071, "10.0.1.71", 3071);
    private static readonly PlcConnectionDto Line72 = new(72, "Line 72", "10.0.0.72", 2072, "10.0.1.72", 3072);

    private readonly NavigatorStatusTypes _previousStatus = Navigator.NavStatus;
    private readonly List<Guid> _seeded = [];

    public void Dispose()
    {
        // I dispositivi di questa classe se ne vanno: una prova successiva che toglie 71 o 72 non
        // deve trovarli li'.
        using (var context = host.Store.CreateContext())
        {
            context.PlantItems.RemoveRange(context.PlantItems.Where(item => _seeded.Contains(item.ItemId)));
            context.SaveChanges();
        }

        Navigator.NavStatus = _previousStatus;
    }

    /// <summary>
    /// Il punto di partenza: nessun dispositivo di altre prove su una connessione (la collezione
    /// condivide lo store), l'elenco dato scritto per la via ufficiale e riletto, poi i dispositivi di
    /// questa prova scritti sullo store come li scrive l'impianto.
    /// </summary>
    private async Task SeedAsync(PlcConnectionDto[] connections, params (string Name, decimal Connection)[] devices)
    {
        await host.DetachEveryDeviceFromItsPlcConnectionAsync();

        var client = host.CreateAuthenticatedClient();
        using (host.GrantEditPermission(client))
        {
            Navigator.NavStatus = NavigatorStatusTypes.Stop;
            Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(Plc, connections)).StatusCode);
        }

        Assert.Equal(connections.OrderBy(connection => connection.Id), await OnTheHostAsync());

        await using (var context = host.Store.CreateContext())
        {
            foreach (var (name, connection) in devices)
            {
                var itemId = Guid.NewGuid();
                _seeded.Add(itemId);
                context.PlantItems.Add(new PlantItem { ItemId = itemId, Name = name, DeviceConnectionId = connection });
            }

            await context.SaveChangesAsync();
        }
    }

    private async Task<List<PlcConnectionDto>> OnTheHostAsync() =>
        (await host.CreateAuthenticatedClient().GetFromJsonAsync<List<PlcConnectionDto>>(Plc))!;

    private async Task<HttpResponseMessage> ReplaceAsync(params PlcConnectionDto[] connections)
    {
        var client = host.CreateAuthenticatedClient();
        using var _ = host.GrantEditPermission(client);
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        return await client.PutAsJsonAsync(Plc, connections);
    }

    [Fact]
    public async Task RemovingAConnectionNoDeviceUsesIsAccepted()
    {
        await SeedAsync([Line71, Line72], ("Shuttle on 71", 71m));

        var put = await ReplaceAsync(Line71);

        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        Assert.Equal([Line71], await OnTheHostAsync());
    }

    /// <summary>
    /// Il rifiuto nomina la connessione e ogni dispositivo che la usa, per nome e per Id, nel testo e
    /// nel campo che una pagina legge senza interpretare parole; l'elenco resta quello di prima.
    /// </summary>
    [Fact]
    public async Task RemovingAConnectionDevicesUseIsRefusedWith409NamingItAndItsDevicesAndNothingIsReplaced()
    {
        await SeedAsync([Line71, Line72], ("Shuttle A", 71m), ("Shuttle B", 71m), ("Lift on 72", 72m));
        var shuttleA = _seeded[0];
        var shuttleB = _seeded[1];

        var put = await ReplaceAsync(Line72);

        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
        using var problem = JsonDocument.Parse(await put.Content.ReadAsStringAsync());
        var root = problem.RootElement;
        Assert.Equal("Connessione PLC ancora usata da dispositivi", root.GetProperty("title").GetString());
        var detail = root.GetProperty("detail").GetString()!;
        Assert.Contains("la connessione 71", detail);
        Assert.Contains($"Shuttle A ({shuttleA})", detail);
        Assert.Contains($"Shuttle B ({shuttleB})", detail);
        Assert.DoesNotContain("Lift on 72", detail);

        // The wire names, spelled out: they are the contract a page or a script reads, and a rename of
        // the constant or of a DTO property would otherwise travel silently to both sides of this test.
        Assert.Equal("plcConnectionsInUse", PlcConnectionProblems.InUseExtension);
        var wire = Assert.Single(root.GetProperty("plcConnectionsInUse").EnumerateArray());
        Assert.Equal(71, wire.GetProperty("connectionId").GetInt64());
        var wireDevices = wire.GetProperty("devices").EnumerateArray().ToList();
        Assert.Equal(2, wireDevices.Count);
        Assert.Contains(wireDevices, device => device.GetProperty("itemId").GetGuid() == shuttleA &&
                                               device.GetProperty("name").GetString() == "Shuttle A");

        var inUse = root.GetProperty(PlcConnectionProblems.InUseExtension)
            .Deserialize<List<PlcConnectionInUseDto>>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var only = Assert.Single(inUse);
        Assert.Equal(71, only.ConnectionId);
        Assert.Equal(
            new[] { new PlcConnectionUserDto(shuttleA, "Shuttle A"), new PlcConnectionUserDto(shuttleB, "Shuttle B") }.OrderBy(user => user.ItemId),
            only.Devices.OrderBy(user => user.ItemId));

        Assert.Equal([Line71, Line72], await OnTheHostAsync());
    }

    /// <summary>
    /// Cambiare l'Id di una riga e' togliere una connessione e aggiungerne un'altra - quello che
    /// l'host ne fa: con un dispositivo sull'Id vecchio e' rifiutato come ogni altra rimozione.
    /// </summary>
    [Fact]
    public async Task RenumberingAConnectionADeviceUsesIsRefused()
    {
        await SeedAsync([Line71], ("Shuttle on 71", 71m));

        var put = await ReplaceAsync(Line71 with { Id = 73 });

        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
        using var problem = JsonDocument.Parse(await put.Content.ReadAsStringAsync());
        Assert.Contains("la connessione 71", problem.RootElement.GetProperty("detail").GetString());
        Assert.Equal([Line71], await OnTheHostAsync());
    }

    /// <summary>
    /// Zero e' «nessuna connessione» (si veda la classe): un dispositivo con 0 non usa la connessione
    /// con Id 0, e toglierla passa.
    /// </summary>
    [Fact]
    public async Task ADeviceWithNoConnectionDoesNotBlockTheRemoval()
    {
        var line0 = new PlcConnectionDto(0, "Id zero", "10.0.0.70", 2070, "10.0.1.70", 3070);
        await SeedAsync([line0, Line71], ("Simulated", 0m));

        var put = await ReplaceAsync(Line71);

        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        Assert.Equal([Line71], await OnTheHostAsync());
    }

    /// <summary>
    /// Con il motore non fermo vince il rifiuto del motore, come prima di questo compito: la
    /// connessione in uso e' una domanda che si fa solo a una sostituzione che potrebbe avvenire.
    /// </summary>
    [Fact]
    public async Task WithTheEngineNotStoppedTheEngineRefusalComesFirst()
    {
        await SeedAsync([Line71, Line72], ("Shuttle on 71", 71m));
        var client = host.CreateAuthenticatedClient();
        using var _ = host.GrantEditPermission(client);
        Navigator.NavStatus = NavigatorStatusTypes.Pause;

        var put = await client.PutAsJsonAsync(Plc, new[] { Line72 });

        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
        using var problem = JsonDocument.Parse(await put.Content.ReadAsStringAsync());
        Assert.Equal("Il motore non è fermo", problem.RootElement.GetProperty("title").GetString());
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        Assert.Equal([Line71, Line72], await OnTheHostAsync());
    }
}
