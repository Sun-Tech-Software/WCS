using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wcs.Api.Contracts;
using Wcs.Engine;
using Wcs.Api.Contracts.Dtos;
using Wcs.Engine.Api;
using Wcs.Engine.Collections;
using Wcs.Engine.Items;
using Wcs.Engine.Persistence;
using Wcs.Host.Engine;
using Wcs.Host.Hubs;
using Wcs.Host.Mapping;
using Wcs.Wms.Contracts;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Host.Test;

/// <summary>
/// Voce D145: un guasto durante il caricamento del motore non e' un errore di configurazione.
/// L'host resta vivo e degradato — le API e l'hub rispondono — dice il motivo in
/// <c>/status</c>, e ritenta il caricamento finche' non riesce.
/// </summary>
/// <remarks>
/// <para>
/// Prima <c>EngineHostedService.StartAsync</c> sollevava quando <c>Navigator.StartAsync</c>
/// rispondeva <c>false</c>, e l'apertura dei database stava fuori dal <c>try</c> del
/// motore: il processo usciva senza <c>/status</c>, senza hub e, per il database, senza nemmeno la
/// riga di registro. L'ordine di riaccensione dopo un fermo non era libero.
/// </para>
/// <para>
/// L'ostacolo qui e' sul database del WCS: un <i>file</i> con il nome della cartella dei dati,
/// che <c>WcsDatabase.Open</c> non puo' creare. E' la stessa forma di un <c>wcs.db</c> in mano a
/// un altro processo, ed e' deterministica; il guasto del confine verso il gestionale passa dallo
/// stesso <c>catch</c> di <c>Navigator.StartAsync</c>, ma per raggiungerlo al caricamento serve un
/// dispositivo legato al deposito con una riga in coda di estrazione.
/// </para>
/// <para>
/// Come <see cref="EngineWarehouseWiringTest"/> costruisce il servizio a mano; a differenza di
/// quella qui il caricamento arriva fino in fondo, quindi lo store e lo storico della fixture si
/// mettono da parte prima (un caricamento riuscito dispone quelli precedenti) e si rimettono a
/// posto nel <see cref="Dispose"/>. L'host della fixture serve solo per le richieste HTTP: legge
/// lo stesso <c>Navigator</c> statico. <c>LoadError</c> si rimette a posto anche lui, perche' con
/// il motore non caricato le rotte rispondono 503 e il resto della collezione cambierebbe strada;
/// i database aperti li chiude <c>Navigator.Dispose</c>, che <c>StopAsync</c> chiama.
/// </para>
/// </remarks>
[Collection("host")]
public sealed class EngineLoadFailureTest : IDisposable
{
    private readonly HostFixture _host;
    private const string Address = "http://127.0.0.1:1";
    private const string Key = "chiave-di-prova";

    // Qui il servizio nasceva con una configurazione che portava Wms:ConnectionString a un SQL
    // Server inesistente, perche' il caricamento del motore apriva quella connessione e non doveva
    // arrivarci. La chiave non esiste piu': il motore non apre nessun SQL Server.

    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly IEngineEventSink? _previousSink = Navigator.EventSink;
    private readonly Action<Exception>? _previousErrorLog = Navigator.HostErrorLog;
    private readonly Action<string>? _previousNotice = WcsHistoryDatabase.Notice;
    private readonly string _previousDirectory = Navigator.DatabaseDirectory;
    private readonly int _previousMonths = Navigator.HistoryMonthsToKeep;
    private readonly WcsDatabase? _previousStore = Navigator.Store;
    private readonly WcsHistoryDatabase? _previousHistory = Navigator.History;
    private readonly WcsPlcLogDatabase? _previousPlcLog = Navigator.PlcLog;
    private readonly Action<string>? _previousPlcLogNotice = WcsPlcLogDatabase.Notice;
    private readonly NavigatorStatusTypes _previousStatus = Navigator.NavStatus;
    private readonly string? _previousLoadError = Navigator.LoadError;
    private readonly RecordingClientFactory _factory = new();

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"wcs-load-{Guid.NewGuid():N}");
    private readonly string _dataDirectory;

    public EngineLoadFailureTest(HostFixture host)
    {
        _host = host;
        Directory.CreateDirectory(_root);
        _dataDirectory = Path.Combine(_root, "data");
        // Lo store, lo storico e il registro dei messaggi dei PLC della fixture non devono finire
        // disposti da un caricamento riuscito.
        Navigator.Store = null;
        Navigator.History = null;
        Navigator.PlcLog = null;
    }

    public void Dispose()
    {
        Navigator.Warehouse = _previousWarehouse;
        Navigator.EventSink = _previousSink;
        Navigator.HostErrorLog = _previousErrorLog;
        WcsHistoryDatabase.Notice = _previousNotice;
        Navigator.DatabaseDirectory = _previousDirectory;
        Navigator.HistoryMonthsToKeep = _previousMonths;
        Navigator.Store = _previousStore;
        Navigator.History = _previousHistory;
        Navigator.PlcLog = _previousPlcLog;
        WcsPlcLogDatabase.Notice = _previousPlcLogNotice;
        Navigator.NavStatus = _previousStatus;
        Navigator.LoadError = _previousLoadError;
        _factory.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* un file ancora aperto: la cartella temporanea resta */ }
    }

    /// <summary>
    /// Il caricamento fallisce, l'host no: <c>StartAsync</c> torna senza sollevare e lo stato dice
    /// perche'. Prima usciva con l'<c>IOException</c> del database.
    /// </summary>
    [Fact]
    public async Task AFailedEngineLoadLeavesTheHostAliveAndSaysWhy()
    {
        File.WriteAllText(_dataDirectory, string.Empty);
        var service = CreateService(retrySeconds: 3600);
        try
        {
            await service.StartAsync(default);

            Assert.NotNull(Navigator.LoadError);
            Assert.Equal(Navigator.LoadError, DtoMapping.Status().EngineLoadError);
        }
        finally
        {
            await service.StopAsync(default);
        }
    }

    /// <summary>
    /// Tolto l'ostacolo, il ritentativo carica il motore da solo e il motivo sparisce dallo stato.
    /// Senza questo, un host che registra l'errore e non ritenta mai passerebbe il collaudo qui sopra.
    /// </summary>
    [Fact]
    public async Task TheEngineLoadIsRetriedUntilTheObstacleIsGone()
    {
        File.WriteAllText(_dataDirectory, string.Empty);
        var service = CreateService(retrySeconds: 1);
        try
        {
            await service.StartAsync(default);
            Assert.NotNull(Navigator.LoadError);

            File.Delete(_dataDirectory);

            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (Navigator.LoadError != null && DateTime.UtcNow < deadline)
                await Task.Delay(100);

            Assert.Null(Navigator.LoadError);
            Assert.Null(DtoMapping.Status().EngineLoadError);
            Assert.NotNull(Navigator.Store);
            Assert.True(File.Exists(Path.Combine(_dataDirectory, WcsDatabase.OperationalFileName)));
        }
        finally
        {
            // I cicli di sfondo del motore caricato scrivono sullo store: fermi prima di chiuderlo.
            await Navigator.StopBackgroundLoopsAsync();
            await service.StopAsync(default);
        }
    }

    /// <summary>
    /// Il ritentativo non tocca un motore che nel frattempo e' stato caricato per un'altra via
    /// (la ricarica del diagramma da <c>PUT /plant/diagram</c>, il comando 1006): ricaricarlo
    /// svuoterebbe le missioni e fermerebbe un motore magari in Run, senza chiudere i PLC. Qui
    /// l'altra via e' <c>ReloadRouteDiagram</c> su uno store aperto a mano, che e' la forma del
    /// guasto sul gestionale: store aperto, caricamento fallito.
    /// </summary>
    [Fact]
    public async Task TheRetryLeavesAloneAnEngineLoadedAnotherWay()
    {
        File.WriteAllText(_dataDirectory, string.Empty);
        var service = CreateService(retrySeconds: 3);
        try
        {
            await service.StartAsync(default);
            Assert.NotNull(Navigator.LoadError);

            File.Delete(_dataDirectory);
            Navigator.History = WcsHistoryDatabase.Open(_dataDirectory, Navigator.HistoryMonthsToKeep);
            var store = Navigator.Store = WcsDatabase.Open(_dataDirectory);
            await ExecutableCommands.ReloadRouteDiagram();
            Assert.Null(Navigator.LoadError);

            // Una missione in memoria: Add rifiuta in Stop, e il caricamento lascia il motore in Stop.
            Navigator.NavStatus = NavigatorStatusTypes.Recover;
            ActiveMissions.Instance.Add(new PathMission { PositionItemId = Guid.NewGuid() }, buildRoute: false);
            Navigator.NavStatus = NavigatorStatusTypes.Stop;
            Assert.Equal(1, ActiveMissions.Instance.Count);

            // Il ritentativo scatta tre secondi dopo il primo fallimento: lo si lascia passare.
            await Task.Delay(TimeSpan.FromSeconds(4.5));

            Assert.Same(store, Navigator.Store);
            Assert.Equal(1, ActiveMissions.Instance.Count);
            Assert.Null(Navigator.LoadError);
        }
        finally
        {
            ActiveMissions.Instance.RemoveAll(_ => true);
            await Navigator.StopBackgroundLoopsAsync();
            await service.StopAsync(default);
        }
    }

    /// <summary>
    /// Con il motore non caricato le rotte che lo toccano rispondono 503 con il motivo, anche se
    /// lo store e' aperto; <c>/status</c> resta raggiungibile e porta lo stesso motivo. Prima
    /// <c>GET /plant/items</c> rispondeva 200 con una lista vuota, indistinguibile da un impianto
    /// vuoto se non si guardava <c>/status</c>.
    /// </summary>
    [Fact]
    public async Task WhileTheEngineIsNotLoadedTheApiSays503AndTheStatusSaysWhy()
    {
        File.WriteAllText(_dataDirectory, string.Empty);
        var service = CreateService(retrySeconds: 3600);
        try
        {
            await service.StartAsync(default);
            Assert.NotNull(Navigator.LoadError);

            // Lo store aperto: il 503 deve venire dal motore non caricato, non dal database assente.
            File.Delete(_dataDirectory);
            Navigator.History = WcsHistoryDatabase.Open(_dataDirectory, Navigator.HistoryMonthsToKeep);
            Navigator.Store = WcsDatabase.Open(_dataDirectory);

            var client = _host.CreateAuthenticatedClient();
            var items = await client.GetAsync(ApiRoutes.Prefix + ApiRoutes.Items);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, items.StatusCode);
            Assert.Equal("application/problem+json", items.Content.Headers.ContentType?.MediaType);
            var problem = (await items.Content.ReadFromJsonAsync<ProblemDetails>())!;
            Assert.Equal(Navigator.LoadError, problem.Detail);
            // Voce 34: il campo dice quale dei due 503 e', e la pagina legge quello, non il titolo.
            Assert.Equal("engineNotLoaded", ((System.Text.Json.JsonElement)problem.Extensions["unavailable"]!).GetString());

            var status = await client.GetAsync(ApiRoutes.Prefix + ApiRoutes.Status);
            Assert.Equal(HttpStatusCode.OK, status.StatusCode);
            Assert.Equal(Navigator.LoadError, (await status.Content.ReadFromJsonAsync<EngineStatus>())!.EngineLoadError);
        }
        finally
        {
            await service.StopAsync(default);
        }
    }

    /// <summary>
    /// Allo spegnimento un tentativo in volo si abbandona con il gettone dell'host invece di
    /// aspettarlo per intero. Il tentativo qui e' l'avvio automatico dopo un caricamento riuscito
    /// (<c>StartNavigator</c> aspetta due secondi): con il gettone annullato dopo 200 ms,
    /// <c>StopAsync</c> torna ben prima dei due secondi.
    /// </summary>
    [Fact]
    public async Task StoppingTheHostAbandonsAnAttemptInFlight()
    {
        File.WriteAllText(_dataDirectory, string.Empty);
        var service = CreateService(retrySeconds: 1, autoRun: true);
        try
        {
            await service.StartAsync(default);
            Assert.NotNull(Navigator.LoadError);

            File.Delete(_dataDirectory);
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (Navigator.LoadError != null && DateTime.UtcNow < deadline)
                await Task.Delay(50);
            Assert.Null(Navigator.LoadError);

            using var shutdown = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            var watch = Stopwatch.StartNew();
            await service.StopAsync(shutdown.Token);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1.5), $"StopAsync ha aspettato {watch.Elapsed}");
        }
        finally
        {
            // Il tentativo abbandonato finisce da solo e porta il motore in Run: lo si aspetta e lo
            // si ferma, cosi' il Dispose rimette uno stato che nessuno cambia piu'.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (Navigator.NavStatus != NavigatorStatusTypes.Run && DateTime.UtcNow < deadline)
                await Task.Delay(50);
            ExecutableCommands.StopNavigator();
            await Navigator.StopBackgroundLoopsAsync();
            await service.StopAsync(default);
        }
    }

    private EngineHostedService CreateService(int retrySeconds, bool autoRun = false)
    {
        return new EngineHostedService(
            Options.Create(new WcsHostOptions
            {
                ApiKey = "test-key", StartEngine = true, HistoryMonthsToKeep = 1,
                DatabaseDirectory = _dataDirectory, EngineLoadRetrySeconds = retrySeconds, AutoRun = autoRun
            }),
            Options.Create(new WmsOptions { ApiAddress = Address, ApiKey = Key }), _factory,
            new PlantEventRelay(new SilentHub(), NullLogger<PlantEventRelay>.Instance),
            NullLogger<EngineHostedService>.Instance);
    }

    private sealed class RecordingClientFactory : IHttpClientFactory, IDisposable
    {
        public HttpClient? LastClient { get; private set; }
        public HttpClient CreateClient(string name) => LastClient = new HttpClient();
        public void Dispose() => LastClient?.Dispose();
    }

    private sealed class SilentHub : IHubContext<PlantHub, IPlantEvents>
    {
        public IHubClients<IPlantEvents> Clients => throw new NotSupportedException("il collaudo non pubblica eventi");
        public IGroupManager Groups => throw new NotSupportedException("il collaudo non pubblica eventi");
    }
}
