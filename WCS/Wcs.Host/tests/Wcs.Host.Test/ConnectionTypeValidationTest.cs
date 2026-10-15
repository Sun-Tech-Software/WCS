using System.Net;
using System.Net.Http.Json;
using Wcs.Api.Contracts;
using Wcs.Engine;
using Wcs.Engine.Persistence.Entities;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Host.Test;

/// <summary>
/// <c>POST /plant/items/connection-type</c> non accetta piu' un valore fuori dall'enum.
/// </summary>
/// <remarks>
/// <c>ConnectionTypes</c> ha due soli valori, <c>Test = 0</c> e <c>Connected = 1</c>. Un corpo con
/// <c>7</c> si legava senza errore e finiva su ogni dispositivo in memoria e su ogni riga di
/// <c>PlantItems</c>; alla successiva <c>OpenConnection</c> nessun dispositivo trovava il proprio
/// ramo e l'impianto accettava missioni senza muovere niente. La rotta gemella <c>POST /commands</c>
/// la validazione la faceva gia'.
/// </remarks>
[Collection("host")]
public sealed class ConnectionTypeValidationTest(HostFixture host) : IDisposable
{
    private readonly NavigatorStatusTypes _previousStatus = Navigator.NavStatus;

    public void Dispose() => Navigator.NavStatus = _previousStatus;

    [Fact]
    public async Task AValueOutsideTheEnumIs400AndWritesNothing()
    {
        var client = host.CreateAuthenticatedClient();
        using var _ = host.GrantEditPermission(client);
        var itemId = Guid.NewGuid();
        await using (var context = host.Store.CreateContext())
        {
            context.PlantItems.Add(new PlantItem { ItemId = itemId, ConnectionType = (short)ConnectionTypes.Connected });
            await context.SaveChangesAsync();
        }

        // Fermo: la scrittura della modalita' di connessione e' rifiutata in Run, e il valore
        // sbagliato deve essere rifiutato prima ancora, per il suo motivo.
        Navigator.NavStatus = NavigatorStatusTypes.Stop;

        var url = ApiRoutes.Prefix + ApiRoutes.Items + "/connection-type";
        var refused = await client.PostAsJsonAsync(url, new { connectionType = 7 });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        // La riga non e' stata toccata: senza il controllo ci sarebbe scritto 7.
        await using (var context = host.Store.CreateContext())
            Assert.Equal((short)ConnectionTypes.Connected,
                (await context.PlantItems.FindAsync(itemId))!.ConnectionType);
    }

    [Fact]
    public async Task TheTwoValuesOfTheEnumStillPass()
    {
        var client = host.CreateAuthenticatedClient();
        using var _ = host.GrantEditPermission(client);
        Navigator.NavStatus = NavigatorStatusTypes.Stop;

        var url = ApiRoutes.Prefix + ApiRoutes.Items + "/connection-type";

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsJsonAsync(url, new { connectionType = (int)ConnectionTypes.Test })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsJsonAsync(url, new { connectionType = (int)ConnectionTypes.Connected })).StatusCode);
    }
}
