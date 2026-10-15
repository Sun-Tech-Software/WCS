using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Wcs.Api.Contracts;
using Wcs.Engine;
using Wcs.Engine.Api;
using Wcs.Engine.Persistence;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Host.Test;

/// <summary>
/// L'host in memoria: motore non avviato (Wcs:StartEngine falso), store, storico e registro dei
/// messaggi dei PLC SQLite in memoria assegnati direttamente a Navigator, chiave "test-key". Ogni
/// classe di test ne ha uno.
/// </summary>
public sealed class HostFixture : WebApplicationFactory<Program>
{
    public const string ApiKey = "test-key";
    public const string ReadOnlyApiKey = "test-read-only-key";
    public WcsDatabase Store { get; } = WcsDatabase.InMemory();
    public WcsHistoryDatabase History { get; } = WcsHistoryDatabase.InMemory();

    /// <summary>Il registro dei messaggi dei PLC, che dalla voce D183 e' un database a parte.</summary>
    public WcsPlcLogDatabase PlcLog { get; } = WcsPlcLogDatabase.InMemory();

    public HostFixture()
    {
        Navigator.Store = Store;
        Navigator.History = History;
        Navigator.PlcLog = PlcLog;
        Navigator.NavStatus = NavigatorStatusTypes.Run;
    }

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        // Esplicito perche' e' la premessa di un collaudo: /openapi e' mappato solo fuori da
        // Production (si veda OpenApiExposureTest).
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Wcs:ApiKey"] = ApiKey,
            ["Wcs:ReadOnlyApiKey"] = ReadOnlyApiKey,
            ["Wcs:StartEngine"] = "false"
        }));
    }

    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(ApiRoutes.ApiKeyHeader, ApiKey);
        return client;
    }

    public HttpClient CreateReadOnlyClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(ApiRoutes.ApiKeyHeader, ReadOnlyApiKey);
        return client;
    }

    /// <summary>
    /// Concede il permesso di modifica (compito 4b, estensione oltre le rotte del permesso stesso)
    /// per la durata del blocco chiamante e mette <see cref="ApiRoutes.EditHolderHeader"/> su
    /// <paramref name="client"/>: i test che scrivono rotte di <c>/plant/*</c> lo chiedono cosi',
    /// con <c>using var _ = fixture.GrantEditPermission(client);</c>, invece di indebolire il
    /// cancello. Il titolare e' unico per chiamata (nome del test chiamante piu' un Guid), cosi'
    /// test diversi non si contendono lo stesso permesso; <see cref="EditLock"/> e' statico e di
    /// processo (come <c>EditLockEndpointsTest</c> gia' documenta), quindi il rilascio a fine
    /// blocco non e' opzionale - senza, il titolare resterebbe occupato (fino alla scadenza vera)
    /// per i test successivi della stessa collezione.
    /// </summary>
    public IDisposable GrantEditPermission(HttpClient client, [CallerMemberName] string testName = "")
    {
        var holder = $"{testName}-{Guid.NewGuid()}";
        if (!EditLock.TryAcquire(holder, TimeSpan.FromMinutes(5)))
            throw new InvalidOperationException(
                $"Permesso di modifica non concesso a {holder}: un test precedente non l'ha rilasciato.");
        client.DefaultRequestHeaders.Remove(ApiRoutes.EditHolderHeader);
        client.DefaultRequestHeaders.Add(ApiRoutes.EditHolderHeader, holder);
        return new EditPermissionRelease(holder);
    }

    /// <summary>
    /// Porta a zero - «nessuna connessione» - il <c>DeviceConnectionId</c> di ogni dispositivo dello
    /// store. Dal compito 4c della console <c>PUT /plc/connections</c> rifiuta di togliere una
    /// connessione che un dispositivo usa, e lo store e' condiviso dalla collezione: una prova che
    /// sostituisce l'elenco non deve trovarsi davanti i dispositivi lasciati da un'altra.
    /// </summary>
    public async Task DetachEveryDeviceFromItsPlcConnectionAsync()
    {
        await using var context = Store.CreateContext();
        await context.PlantItems.ExecuteUpdateAsync(set => set.SetProperty(item => item.DeviceConnectionId, 0m));
    }

    private sealed class EditPermissionRelease(string holder) : IDisposable
    {
        public void Dispose() => EditLock.Release(holder);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        Navigator.Store = null;
        Navigator.History = null;
        Navigator.PlcLog = null;
        Store.Dispose();
        History.Dispose();
        PlcLog.Dispose();
    }
}

[CollectionDefinition("host")]
public sealed class HostCollection : ICollectionFixture<HostFixture>;
