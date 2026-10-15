using System.Net;
using System.Net.Http.Json;
using Wcs.Api.Contracts;
using Wcs.Engine;
using Wcs.Engine.Api;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Host.Test;

/// <summary>
/// Il rinnovo del permesso a ogni scrittura autorizzata (compito 4b, ultima passata): il controllo
/// di <see cref="Wcs.Host.Security.EditPermissionFilter"/> corre una volta sola all'ingresso, ma da
/// oggi rinnova la scadenza nello stesso passo se il titolare combacia. Qui si prova il giro vero -
/// il client HTTP, il cancello, la rotta - non <see cref="EditLock.VerifyAndRenew"/> chiamato
/// direttamente (quello lo prova gia' <c>Wcs.Engine.Test.EditLockTest</c>).
/// </summary>
[Collection("host")]
public sealed class EditPermissionRenewalTest(HostFixture host) : IDisposable
{
    private readonly NavigatorStatusTypes _previousStatus = Navigator.NavStatus;
    private readonly Func<DateTime> _originalClock = EditLock.Clock;

    public void Dispose()
    {
        Navigator.NavStatus = _previousStatus;
        EditLock.Clock = _originalClock;
    }

    [Fact]
    public async Task AnAuthorizedWriteMovesTheExpiryForward()
    {
        // A motore fermo: KeepItems con lista vuota non svuota niente (PlantConfigurationEndpointsTest
        // lo documenta gia'), quindi qui e' solo una scrittura autorizzata senza effetti da ripulire.
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        var now = new DateTime(2026, 1, 1, 10, 0, 0);
        EditLock.Clock = () => now;

        var client = host.CreateAuthenticatedClient();
        using var _ = host.GrantEditPermission(client);
        var holder = EditLock.Current!.Holder;
        var expiryAtGrant = EditLock.Current!.ExpiresLocal;

        // Un minuto dopo, ancora dentro il permesso concesso: se la scrittura non lo rinnovasse
        // sarebbe comunque valida adesso, la prova sta nella scadenza dopo, non nell'esito qui.
        now = now.AddMinutes(1);
        var response = await client.PostAsJsonAsync($"{ApiRoutes.Prefix}{ApiRoutes.Items}/keep", new List<Guid>());
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        Assert.Equal(holder, EditLock.Current!.Holder);
        Assert.True(EditLock.Current!.ExpiresLocal > expiryAtGrant,
            "La scrittura autorizzata doveva spostare in avanti la scadenza del permesso.");
    }

    /// <summary>
    /// Il contrario: una scrittura senza il permesso (423, gia' provato da
    /// <c>EditPermissionGateTest.AWriteWithoutThePermissionHeaderIsRefusedWith423</c>) non deve
    /// rinnovare niente, perche' non c'e' nessun titolare autorizzato che l'abbia fatta.
    /// </summary>
    [Fact]
    public async Task ARefusedWriteDoesNotRenewSomeoneElsesPermission()
    {
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        var now = new DateTime(2026, 1, 1, 10, 0, 0);
        EditLock.Clock = () => now;

        var owner = host.CreateAuthenticatedClient();
        using var _ = host.GrantEditPermission(owner);
        var expiryAtGrant = EditLock.Current!.ExpiresLocal;

        var intruder = host.CreateAuthenticatedClient(); // stessa X-Api-Key, nessuna X-Edit-Holder
        now = now.AddSeconds(30);
        var response = await intruder.PostAsJsonAsync($"{ApiRoutes.Prefix}{ApiRoutes.Items}/keep", new List<Guid>());
        Assert.Equal(423, (int)response.StatusCode);

        Assert.Equal(expiryAtGrant, EditLock.Current!.ExpiresLocal);
    }
}
