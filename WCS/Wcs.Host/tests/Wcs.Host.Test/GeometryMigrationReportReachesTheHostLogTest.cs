using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wcs.Api.Contracts;
using Wcs.Engine;
using Wcs.Engine.Api;
using Wcs.Engine.Persistence;
using Wcs.Host.Engine;
using Wcs.Host.Hubs;
using Wcs.Wms.Contracts;
using Xunit;

namespace Wcs.Host.Test;

/// <summary>
/// Il collaudo a video del 2026-09-13 ha trovato un verbale che non arrivava da nessuna parte: 51
/// dispositivi collocati sull'impianto vero, e nel registro dell'host nessuna riga. La causa era
/// <c>Navigator.WriteLog(..., LogEntryType.Information)</c>, che con
/// <c>Navigator.ActivateDebugLog</c> spento di serie (voce D94) non passa mai — e non lo si poteva
/// riaccendere solo per questa riga, perche' avrebbe riacceso anche la diagnostica sui percorsi
/// caldi.
/// </summary>
/// <remarks>
/// <para>
/// Questa classe prova la meta' che <c>GeometryMigrationReportIsExposedTest</c> (in
/// <c>Wcs.Engine.Test</c>) non arriva a toccare: che <c>EngineHostedService.LoadEngineAsync</c>,
/// dopo un caricamento riuscito, legga <c>Navigator.LastGeometryMigration</c> e lo scriva col
/// proprio <c>ILogger</c> a livello Informazione — lo stesso canale con cui gia' scrive «Motore
/// caricato da ...», che il registro dell'host riceve con la configurazione di serie perche' non
/// passa da <c>Navigator.ActivateDebugLog</c>.
/// </para>
/// <para>
/// Quello che questo collaudo <b>non</b> prova: che la riga arrivi davvero nel registro eventi di
/// Windows o nel file del servizio installato. Costruisce <see cref="EngineHostedService"/> a mano,
/// come <see cref="EngineWarehouseWiringTest"/>, con un <see cref="ILogger{TCategoryName}"/> che
/// registra le chiamate invece di scriverle da qualche parte: prova il cablaggio (il motore
/// espone il fatto, l'host lo legge e chiama <c>LogInformation</c>), non il trasporto oltre
/// <c>ILogger</c>. Quella meta' — la riga visibile a video con l'host riacceso — resta un collaudo
/// dal vivo, non uno automatico.
/// </para>
/// <para>
/// Sta nella collezione "host" perche' <c>Navigator</c> e' statico e la classe lo tocca: lo store,
/// lo storico e il registro dei messaggi dei PLC della fixture si mettono da parte prima di un
/// caricamento che li sostituirebbe, e si rimettono a posto nel <see cref="Dispose"/>, come fa
/// <see cref="EngineLoadFailureTest"/>.
/// </para>
/// </remarks>
[Collection("host")]
public sealed class GeometryMigrationReportReachesTheHostLogTest : IDisposable
{
    private const string Address = "http://localhost:5142";
    private const string Key = "chiave-di-prova";

    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly IEngineEventSink? _previousSink = Navigator.EventSink;
    private readonly Action<Exception>? _previousErrorLog = Navigator.HostErrorLog;
    private readonly Action<string>? _previousHistoryNotice = WcsHistoryDatabase.Notice;
    private readonly Action<string>? _previousPlcLogNotice = WcsPlcLogDatabase.Notice;
    private readonly string _previousDirectory = Navigator.DatabaseDirectory;
    private readonly int _previousMonths = Navigator.HistoryMonthsToKeep;
    private readonly WcsDatabase? _previousStore = Navigator.Store;
    private readonly WcsHistoryDatabase? _previousHistory = Navigator.History;
    private readonly WcsPlcLogDatabase? _previousPlcLog = Navigator.PlcLog;
    private readonly string? _previousLoadError = Navigator.LoadError;
    private readonly GeometryMigrationReport? _previousReport = Navigator.LastGeometryMigration;
    private readonly RecordingClientFactory _factory = new();

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"wcs-geometry-host-log-{Guid.NewGuid():N}");
    private readonly string _dataDirectory;

    public GeometryMigrationReportReachesTheHostLogTest()
    {
        Directory.CreateDirectory(_root);
        _dataDirectory = Path.Combine(_root, "data");
        // Come EngineLoadFailureTest: un caricamento riuscito dispone lo store, lo storico e il
        // registro dei PLC precedenti, che qui sono quelli in memoria della fixture condivisa.
        Navigator.Store = null;
        Navigator.History = null;
        Navigator.PlcLog = null;
        Navigator.LastGeometryMigration = null;
    }

    public void Dispose()
    {
        Navigator.Warehouse = _previousWarehouse;
        Navigator.EventSink = _previousSink;
        Navigator.HostErrorLog = _previousErrorLog;
        WcsHistoryDatabase.Notice = _previousHistoryNotice;
        WcsPlcLogDatabase.Notice = _previousPlcLogNotice;
        Navigator.DatabaseDirectory = _previousDirectory;
        Navigator.HistoryMonthsToKeep = _previousMonths;
        Navigator.Store = _previousStore;
        Navigator.History = _previousHistory;
        Navigator.PlcLog = _previousPlcLog;
        Navigator.LoadError = _previousLoadError;
        Navigator.LastGeometryMigration = _previousReport;
        _factory.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* file ancora aperto: pazienza */ }
    }

    /// <summary>
    /// Un caricamento vero e proprio, su una cartella dati nuova (nessun blob, nessun PlantItem
    /// noto): la migrazione della geometria marca il diagramma con tutti i contatori a zero — lo
    /// stesso caso che <c>GeometryMigrationTest</c> prova per un impianto senza disegno — e
    /// <c>LoadEngineAsync</c> deve scrivere quel verbale col proprio registratore, a Informazione,
    /// accanto alla riga «Motore caricato da ...».
    /// </summary>
    [Fact]
    public async Task TheReportIsLoggedByTheHostAfterASuccessfulLoad()
    {
        var recorder = new RecordingLogger();
        var service = new EngineHostedService(
            Options.Create(new WcsHostOptions
            {
                ApiKey = "test-key", StartEngine = true, HistoryMonthsToKeep = 12,
                DatabaseDirectory = _dataDirectory
            }),
            Options.Create(new WmsOptions { ApiAddress = Address, ApiKey = Key }), _factory,
            new PlantEventRelay(new SilentHub(), NullLogger<PlantEventRelay>.Instance), recorder);

        try
        {
            await service.StartAsync(default);

            // Premessa: il caricamento e' arrivato in fondo, non sul percorso di fallimento che
            // gia' passa da Errore.
            Assert.Null(Navigator.LoadError);

            var report = Navigator.LastGeometryMigration;
            Assert.NotNull(report);
            Assert.True(report!.Executed);

            var expectedMessage =
                "Geometria del disegno migrata: 0 dispositivi collocati (0 con riquadro letto solo in parte), " +
                "0 senza posizione, 0 elementi senza identita', 0 senza riquadro leggibile, " +
                "0 identita' sconosciute (i numeri non sono una partizione).";

            var entry = Assert.Single(recorder.Entries, e => e.Message == expectedMessage);
            Assert.Equal(LogLevel.Information, entry.Level);

            // La riga del verbale segue quella che dice da dove e' stato caricato il motore: e'
            // l'ancora scelta nel rapporto, non un dettaglio accidentale dell'ordine.
            var loadedIndex = recorder.Entries.ToList().FindIndex(e => e.Message.Contains("Motore caricato da"));
            var reportIndex = recorder.Entries.ToList().FindIndex(e => e.Message == expectedMessage);
            Assert.True(loadedIndex >= 0 && reportIndex > loadedIndex,
                "Il verbale non segue la riga «Motore caricato da ...»");
        }
        finally
        {
            // I cicli di sfondo del motore caricato scrivono sullo store: fermi prima di chiuderlo,
            // come EngineLoadFailureTest.TheEngineLoadIsRetriedUntilTheObstacleIsGone.
            await Navigator.StopBackgroundLoopsAsync();
            await service.StopAsync(default);
        }
    }

    private sealed class RecordingLogger : ILogger<EngineHostedService>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get { lock (_entries) return _entries.ToArray(); }
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_entries) _entries.Add((logLevel, formatter(state, exception)));
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
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
