using Microsoft.Extensions.Options;
using Wcs.Engine;
using Wcs.Engine.Collections;
using Wcs.Engine.Persistence;
using Wcs.Wms.Client;
using Wcs.Wms.Contracts;

namespace Wcs.Host.Engine;

/// <summary>
/// Avvia il motore all'avvio dell'host, con Navigator.StartAsync, e lo spegne alla chiusura. E'
/// l'unica via d'avvio: il setter di Navigator.DatabaseConnectionString, che lo faceva partire
/// come effetto collaterale, e' stato cancellato (voce B9) e con il compito 16 e' sparita anche la
/// proprieta', perche' era la connessione a SQL Server del gestionale. Un errore di configurazione ferma
/// l'host; un guasto del caricamento (gestionale, database) no: l'host resta vivo, /status porta
/// il motivo e il caricamento si ritenta (voce D145).
/// </summary>
public sealed class EngineHostedService(IOptions<WcsHostOptions> options, IOptions<WmsOptions> wmsOptions,
    IHttpClientFactory httpClientFactory, PlantEventRelay relay,
    ILogger<EngineHostedService> logger) : IHostedService
{
    /// <summary>
    /// Il nome del client verso il gestionale nella fabbrica dei client, che e' il modo con cui si
    /// costruiscono i client in un host ASP.NET Core: il gestore sottostante e' condiviso, quindi
    /// niente una connessione per chiamata come farebbe un <c>new HttpClient()</c> ogni volta.
    /// <para>Il client e' uno solo e vive quanto il motore, perche' <c>Navigator.Warehouse</c> e'
    /// uno solo: il suo gestore quindi non ruota con la cadenza della fabbrica, e un cambio di DNS
    /// sotto lo stesso nome non lo seguirebbe. Va bene per un indirizzo fisso, scritto nella
    /// configurazione dell'impianto; se un giorno l'indirizzo diventasse mobile, la porta da
    /// cambiare e' questa.</para>
    /// </summary>
    public const string WarehouseClientName = "wms-warehouse";

    /// <summary>
    /// The assembly whose metadata names the build default of the field driver (see
    /// <see cref="ConnectionFactoryLoader.DefaultMetadataKey"/>): the host assembly. Tests point it
    /// to an assembly without a default, to see what a build without a driver does whatever build
    /// they run in.
    /// </summary>
    internal System.Reflection.Assembly DefaultDriverSource { get; init; } = typeof(EngineHostedService).Assembly;

    /// <summary>
    /// Il gestore che questa istanza ha collegato a <c>Navigator.HostErrorLog</c>: allo spegnimento
    /// si stacca solo se e' ancora il proprio. <c>Navigator</c> e' statico, quindi in un processo con
    /// piu' host (i test in memoria) l'ultimo avviato ha gia' preso il posto: azzerare senza guardare
    /// lascerebbe senza registro un host ancora vivo.
    /// </summary>
    private Action<Exception>? _hostErrorLog;

    /// <summary>
    /// Il gestore collegato a <c>WcsHistoryDatabase.Notice</c>, con la stessa regola di
    /// <see cref="_hostErrorLog"/>: statico anche quello, quindi allo spegnimento si stacca solo se
    /// e' ancora il proprio.
    /// </summary>
    private Action<string>? _historyNotice;

    /// <summary>
    /// Il gestore collegato a <c>WcsPlcLogDatabase.Notice</c>, con la stessa regola di
    /// <see cref="_historyNotice"/>: il registro dei messaggi dei PLC ha i suoi file mensili e la
    /// sua ritenzione (voce <c>D183</c>), e anche li' un file cancellato e' un dato che se ne va.
    /// </summary>
    private Action<string>? _plcLogNotice;

    /// <summary>
    /// Il registro collegato a <c>Navigator.SetEventLogWriter</c>, con la stessa regola di
    /// <see cref="_hostErrorLog"/>: allo spegnimento si stacca solo se e' ancora il proprio.
    /// </summary>
    private Log.Interface.IWriter? _engineLog;

    /// <summary>
    /// Il magazzino che questa istanza ha collegato a <c>Navigator.Warehouse</c>, con la stessa
    /// regola di <see cref="_hostErrorLog"/>: allo spegnimento si stacca solo se e' ancora il
    /// proprio, perche' anche <c>Navigator</c> e' statico.
    /// </summary>
    private IWarehouse? _warehouse;

    /// <summary>Ferma il ritentativo del caricamento allo spegnimento (voce D145).</summary>
    private CancellationTokenSource? _loadRetry;
    private Task? _loadRetryTask;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        Navigator.EventSink = relay;

        // Il registro dell'host per gli errori che devono lasciare un segno anche con la
        // diagnostica del motore spenta (la traccia in processo mancata, D94): l'ILogger di ASP.NET
        // Core, che come servizio finisce nel registro eventi di Windows. Prima della guardia su
        // StartEngine: anche un host senza motore lo collega.
        _hostErrorLog = ex => logger.LogError(ex, "{Message}", ex.Message);
        Navigator.HostErrorLog = _hostErrorLog;

        // Il registro di Navigator.WriteLog, che da questa revisione lascia passare sempre Errore e
        // Avviso (voce D94). Senza qualcuno collegato qui quelle righe finivano solo su
        // Console.WriteLine, che in un servizio Windows non e' nessun posto: SetEventLogWriter non
        // aveva un chiamante in tutto il repository.
        _engineLog = new EngineLogWriter(logger);
        Navigator.SetEventLogWriter(_engineLog);

        // La ritenzione dello storico cancella file mensili: un avviso, non un errore, ma nel
        // registro ci deve stare. Anche questo prima della guardia su StartEngine, perche' lo
        // storico si apre appena il motore parte e la prima ritenzione e' proprio quella che su un
        // impianto importato puo' portare via gli anni.
        _historyNotice = message => logger.LogWarning("{Message}", message);
        WcsHistoryDatabase.Notice = _historyNotice;

        // Lo stesso per i file mensili del registro dei messaggi dei PLC, che dalla voce D183 sono
        // una serie a parte con una ritenzione piu' corta.
        _plcLogNotice = message => logger.LogWarning("{Message}", message);
        WcsPlcLogDatabase.Notice = _plcLogNotice;

        // The field driver, chosen by Wcs:ConnectionFactory (see ConnectionFactoryLoader): the
        // engine no longer names a driver. Here, before the StartEngine guard and with the other
        // logs, because it is process wiring and not engine state: the driver has the engine
        // services however the engine starts (automatic start, command 1006, a diagram reload).
        // A name that cannot be loaded stops the host: a plant configured for a driver must not
        // quietly run without it. It is not removed on shutdown, as the driver hooks never were:
        // in a process with several hosts (the in-memory tests) removing it would leave a host
        // that is still running without its driver.
        var factoryName = ConnectionFactoryLoader.ResolveTypeName(settings.ConnectionFactory, DefaultDriverSource);
        var connectionFactory = ConnectionFactoryLoader.Create(factoryName, AppContext.BaseDirectory);
        DeviceConnections.UseConnectionFactory(connectionFactory);
        // Fail-closed unless the explicit fallback is on (refused in Production at start-up,
        // SimulatorFallbackPolicy): a Connected device without its connection goes OffLine and Run
        // is refused, instead of being served by the simulator.
        DeviceConnections.AllowSimulatorForMissingConnections = settings.AllowSimulatorForMissingConnections;
        if (connectionFactory == null)
            logger.LogWarning("No field driver ({Key} {Reason}): only the loopback driver is available, " +
                              "and devices with connection type Connected stay without connection (OffLine), and Run is refused while the plant has any",
                ConnectionFactoryLoader.SettingKey,
                settings.ConnectionFactory == null ? "is not set and this build has no default" : "turns it off");
        else
            logger.LogInformation("Field driver: {Factory}", factoryName);

        if (!settings.StartEngine)
        {
            logger.LogWarning("Motore non avviato: Wcs:StartEngine è falso");
            return;
        }

        // Un valore minore di uno non e' una politica di ritenzione, e' un errore di
        // configurazione: ApplyRetention calcola l'estremo come «primo del mese corrente +
        // (1 - MonthsToKeep) mesi», quindi con 0 l'estremo cade nel MESE PROSSIMO e ogni file
        // mensile esistente ci sta prima - lo storico intero, che dopo un'importazione e' l'unica
        // copia. Con un negativo va ancora piu' avanti. Il valore attraversava tre passaggi senza
        // che nessuno lo guardasse, e Wcs__HistoryMonthsToKeep=0 e' un refuso plausibile in una
        // variabile d'ambiente di installazione. Si rifiuta di partire, come per Wms:ApiKey: la
        // cancellazione lascia una traccia, ma e' una traccia dopo il fatto.
        if (settings.HistoryMonthsToKeep < 1)
            throw new InvalidOperationException(
                $"Wcs:HistoryMonthsToKeep vale {settings.HistoryMonthsToKeep}: il minimo è 1 (il mese corrente). " +
                "Un valore minore cancellerebbe tutto lo storico al primo avvio. " +
                "Si imposta in appsettings.json (Wcs:HistoryMonthsToKeep) o nella variabile d'ambiente " +
                "Wcs__HistoryMonthsToKeep.");

        Navigator.DatabaseDirectory = Path.IsPathRooted(settings.DatabaseDirectory)
            ? settings.DatabaseDirectory
            : Path.Combine(AppContext.BaseDirectory, settings.DatabaseDirectory);
        Navigator.HistoryMonthsToKeep = settings.HistoryMonthsToKeep;
        Navigator.DefaultRejectItem = settings.Engine.DefaultRejectItem;
        Navigator.MasterProcessOnEvery = settings.Engine.MasterProcessOnEvery;
        Navigator.NumCurrentStoringStep = settings.Engine.NumCurrentStoringStep;
        Navigator.LockCellOnStoringError = settings.Engine.LockCellOnStoringError;
        Navigator.LimitOneShippingNotePerBuffer = settings.Engine.LimitOneShippingNotePerBuffer;
        Navigator.DiscardLuManagedInWarehouse = settings.Engine.DiscardLuManagedInWarehouse;
        Navigator.DisableUnloadLu = settings.Engine.DisableUnloadLu;
        Navigator.SwitchLuLengthVsLuWidth = settings.Engine.SwitchLuLengthVsLuWidth;
        Navigator.ActivateDebugLog = settings.Engine.ActivateDebugLog;
        Navigator.AutoUnloadDelay = TimeSpan.FromMilliseconds(Math.Max(0, settings.Engine.AutoUnloadDelayMilliseconds));

        // Il confine verso il gestionale. Il motore non costruisce mai il proprio magazzino: lo
        // prende da Navigator.Warehouse, e chi ospita il motore glielo mette prima di avviarlo.
        // Sta qui, prima di StartAsync, perche' quasi ogni decisione del motore passa di la': un
        // motore avviato senza magazzino non e' un motore a mezzo servizio, e' un motore che
        // solleva al primo movimento. Se la configurazione manca, meglio non partire affatto - e'
        // per questo che CreateWarehouse solleva invece di lasciare nullo.
        _warehouse = CreateWarehouse(wmsOptions.Value);
        Navigator.Warehouse = _warehouse;

        // Qui si leggeva Wms:ConnectionString e si pretendeva che ci fosse: era la connessione a
        // SQL Server del gestionale, che il motore apriva per leggerne le tabelle. Il motore non le
        // legge piu' — passa tutto dal confine, cioe' dal magazzino qui sopra — e la chiave e'
        // uscita dalla configurazione: avviare il motore non richiede piu' nessuna stringa.

        // Da qui in poi non e' piu' configurazione ma esercizio: un gestionale o un database non
        // raggiungibili all'avvio non fermano l'host (voce D145). Le API e l'hub rispondono,
        // /status porta il motivo (Navigator.LoadError) e il caricamento si ritenta da solo finche'
        // non riesce o l'host si spegne. Prima StartAsync sollevava, il processo usciva, e il WCS
        // non poteva ripartire prima del gestionale.
        _loadRetry = new CancellationTokenSource();
        if (!await LoadEngineAsync())
            _loadRetryTask = RetryEngineLoadAsync(_loadRetry.Token);
    }

    /// <summary>
    /// Un tentativo di caricamento del motore, con <c>AutoRun</c> se e' riuscito. Non solleva mai:
    /// l'esito va nel registro e in <c>Navigator.LoadError</c>.
    /// </summary>
    private async Task<bool> LoadEngineAsync()
    {
        if (!await Navigator.StartAsync())
        {
            logger.LogError("Motore non caricato: {Reason}. Nuovo tentativo fra {Seconds} s; intanto l'host risponde e /status lo dice.",
                Navigator.LoadError, RetryDelay.TotalSeconds);
            return false;
        }
        logger.LogInformation("Motore caricato da {Directory}", Navigator.DatabaseDirectory);

        // Il verbale della migrazione della geometria (Navigator.OpenDatabases, dentro lo
        // StartAsync appena tornato) gira una volta per avvio e nessuno lo guarda mentre accade:
        // per questo deve arrivare qui, non restare sotto Navigator.ActivateDebugLog (spento di
        // serie, voce D94), che lascerebbe passare solo Errore e Avviso. Testo identico a quello
        // che Navigator.WriteLog compone per la diagnostica: la precisazione "i numeri non sono
        // una partizione" resta, perche' i contatori girano solo sui dispositivi che il modello
        // gia' riconosce (si veda GeometryMigration.RunOnceAsync) e uno zero dice "niente di cio'
        // che si guarda mancava", non "non c'era altro nel disegno".
        if (Navigator.LastGeometryMigration is { Executed: true } geometryReport)
            logger.LogInformation(
                "Geometria del disegno migrata: {Placed} dispositivi collocati " +
                "({PartiallyPlaced} con riquadro letto solo in parte), {ItemsWithoutPosition} senza posizione, " +
                "{ElementsWithoutGuid} elementi senza identita', {GuidsWithoutGeometry} senza riquadro leggibile, " +
                "{UnknownGuids} identita' sconosciute (i numeri non sono una partizione).",
                geometryReport.Placed, geometryReport.PartiallyPlaced, geometryReport.ItemsWithoutPosition,
                geometryReport.ElementsWithoutGuid, geometryReport.GuidsWithoutGeometry, geometryReport.UnknownGuids);

        if (options.Value.AutoRun)
        {
            // Il motore e' caricato: se l'avvio automatico fallisce (una connessione PLC che
            // solleva) resta un motore fermo da avviare con il comando, non un host morto.
            try
            {
                await ExecutableCommands.StartNavigator();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Motore caricato ma non avviato (Wcs:AutoRun): {Message}", ex.Message);
            }
        }

        return true;
    }

    private async Task RetryEngineLoadAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(RetryDelay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // Nel frattempo il motore puo' essere stato caricato per un'altra via — la ricarica
            // del diagramma dall'Editor, il comando 1006 — e magari messo in Run dall'operatore:
            // LoadNavigatorData azzera LoadError in fondo, da qualunque via. Ricaricarlo qui
            // svuoterebbe le missioni e lo fermerebbe senza chiudere i PLC.
            if (Navigator.LoadError == null)
            {
                logger.LogInformation("Motore caricato per un'altra via: il ritentativo si ferma");
                return;
            }

            if (await LoadEngineAsync())
                return;
        }
    }

    private TimeSpan RetryDelay => TimeSpan.FromSeconds(Math.Max(1, options.Value.EngineLoadRetrySeconds));

    /// <summary>
    /// Costruisce il client verso il gestionale, o solleva dicendo quale impostazione manca e dove
    /// si mette. Ogni messaggio nomina la chiave della configurazione: chi installa deve poter
    /// leggere il registro eventi e sapere che cosa scrivere, senza risalire da un riferimento nullo.
    /// </summary>
    private IWarehouse CreateWarehouse(WmsOptions settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiAddress))
            throw new InvalidOperationException(
                "Wms:ApiAddress manca: senza l'indirizzo dell'API del gestionale il motore non ha nessun magazzino con cui parlare. " +
                "Si mette in appsettings.json, nella sezione Wms (per esempio \"http://localhost:5142\"), " +
                "oppure nella variabile d'ambiente Wms__ApiAddress.");

        if (!Uri.TryCreate(settings.ApiAddress, UriKind.Absolute, out var address) ||
            (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException(
                $"Wms:ApiAddress non è un indirizzo http assoluto: \"{settings.ApiAddress}\". " +
                "Ci vogliono schema, nome e porta, senza percorso: per esempio \"http://localhost:5142\".");

        if (string.IsNullOrWhiteSpace(settings.ApiKey) || settings.ApiKey == WmsOptions.SampleApiKey)
            throw new InvalidOperationException(
                "Wms:ApiKey manca o è quella di esempio: è la chiave con cui il WCS chiama l'API del gestionale, " +
                "e deve essere identica a WcsWarehouse:ApiKey di WMS.Services.API. " +
                "Non va scritta in appsettings.json, che è tracciato da git: si mette nella variabile d'ambiente Wms__ApiKey.");

        if (settings.TimeoutSeconds < WmsOptions.MinimumTimeoutSeconds)
            throw new InvalidOperationException(
                $"Wms:TimeoutSeconds è {settings.TimeoutSeconds}: il minimo è {WmsOptions.MinimumTimeoutSeconds} secondi, " +
                $"il valore di serie è {WmsOptions.DefaultTimeoutSeconds}. Sotto quel minimo ogni chiamata al gestionale " +
                "scadrebbe. Si mette in appsettings.json accanto a Wms:ApiAddress, oppure nella variabile " +
                "d'ambiente Wms__TimeoutSeconds.");

        var client = httpClientFactory.CreateClient(WarehouseClientName);
        client.BaseAddress = address;
        // Senza, resta il tempo massimo di serie di HttpClient: cento secondi, dentro i cicli di
        // decisione del motore. Alla prova generale del 10/9 la prima ricerca della cella a
        // database freddo ha fatto quindici secondi, con l'unita' di carico ferma ad aspettare.
        client.Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds);
        return new HttpWarehouse(client, new HttpWarehouseOptions { ApiKey = settings.ApiKey });
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Prima il ritentativo, che altrimenti potrebbe ricaricare un motore appena disposto. Un
        // tentativo in volo non si puo' interrompere (Navigator.StartAsync non e' annullabile, e
        // con un SQL Server che non risponde sono i quindici secondi del Connect Timeout): lo si
        // aspetta finche' il gettone dell'host lo consente, poi lo si abbandona e si spegne il
        // resto. Il ciclo non riparte comunque, perche' il suo gettone e' gia' annullato.
        var retry = _loadRetry;
        _loadRetry = null;
        if (retry != null)
        {
            await retry.CancelAsync();
            var abandoned = false;
            if (_loadRetryTask != null)
            {
                try
                {
                    await _loadRetryTask.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    abandoned = true;
                    logger.LogWarning("Spegnimento con un tentativo di caricamento del motore ancora in corso: non lo si aspetta");
                }
            }
            // La sorgente resta al tentativo abbandonato, che ne legge ancora il gettone.
            if (!abandoned) retry.Dispose();
        }

        if (options.Value.StartEngine)
        {
            ExecutableCommands.StopNavigator();
            Navigator.Dispose();
        }
        // Stessa regola di _hostErrorLog: EventSink e' statico e in un processo con piu' host
        // (la suite in memoria) l'ultimo avviato ha gia' preso il posto. Si stacca solo il
        // proprio relay, che qui e' il parametro del costruttore (voce D104).
        if (ReferenceEquals(Navigator.EventSink, relay)) Navigator.EventSink = null;
        if (ReferenceEquals(Navigator.Warehouse, _warehouse)) Navigator.Warehouse = null;
        if (ReferenceEquals(Navigator.HostErrorLog, _hostErrorLog)) Navigator.HostErrorLog = null;
        if (ReferenceEquals(Navigator.EventLogWriter, _engineLog)) Navigator.SetEventLogWriter(null);
        if (ReferenceEquals(WcsHistoryDatabase.Notice, _historyNotice)) WcsHistoryDatabase.Notice = null;
        if (ReferenceEquals(WcsPlcLogDatabase.Notice, _plcLogNotice)) WcsPlcLogDatabase.Notice = null;
    }

    /// <summary>
    /// Porta le righe di <c>Navigator.WriteLog</c> sull'<c>ILogger</c> dell'host, con il livello
    /// che il motore ha scelto: come servizio Windows finiscono nel registro eventi.
    /// </summary>
    private sealed class EngineLogWriter(ILogger logger) : Log.Interface.IWriter
    {
        public void WriteLog(string description, Log.Interface.LogEntryType logType)
        {
            switch (logType)
            {
                case Log.Interface.LogEntryType.Error:
                case Log.Interface.LogEntryType.FailureAudit:
                    logger.LogError("{Message}", description);
                    break;
                case Log.Interface.LogEntryType.Warning:
                    logger.LogWarning("{Message}", description);
                    break;
                default:
                    logger.LogInformation("{Message}", description);
                    break;
            }
        }
    }
}
