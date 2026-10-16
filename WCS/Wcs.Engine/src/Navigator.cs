using System.Data;
using Helper.Converters;
using Wcs.Engine.Collections;
using Wcs.Common.Events;
using Wcs.Engine.Items;
using Wcs.Engine.ORM;
using Wcs.Engine.Persistence;
using Log.Interface;
using Wcs.Connection.Interface;
using Wcs.Connection.Interface.CommandData;
using Wcs.Connection.Interface.Events;
using Wcs.Wms.Contracts;
using static Wcs.Engine.Items.AutomationDevice;
using AutomationDevice = Wcs.Engine.Items.AutomationDevice;
using AutomationJunction = Wcs.Engine.Items.AutomationJunction;
using CheckDataFromAsiInfo = Wcs.Engine.Items.CheckDataFromAsiInfo;
using CommandToDo = Wcs.Engine.Collections.CommandToDo;
using PathMission = Wcs.Engine.Items.PathMission;
using RecordDataFromAsiInfo = Wcs.Engine.Items.RecordDataFromAsiInfo;
using static Wcs.Common.Enums;

namespace Wcs.Engine;

public sealed class Navigator
{
    #region Public Constructors

    static Navigator()
    {
        DefragmentationChanged += Navigator_DefragmentationChanged;
    }

    #endregion Public Constructors

    #region Public Fields

    public static CheckDataFromAsiInfo? CheckDataFromAsi;
    public static RecordDataFromAsiInfo? RecordDataFromAsi;

    #endregion Public Fields

    #region Internal Fields

    /// <summary>
    /// Lo scrittore a coda dello storico — il registro del navigatore — sul file del mese: un solo
    /// thread scrive, a lotti, e a coda piena cadono le righe piu' vecchie. Nasce con la
    /// configurazione e si svuota in <see cref="Dispose"/>. Nullo finche' il motore non e'
    /// configurato: chi scrive deve poterne fare a meno.
    /// </summary>
    internal static QueuedDatabaseWriter<HistoryWrite>? HistoryWriter;

    /// <summary>
    /// Lo storico del WCS, un file al mese in <see cref="DatabaseDirectory"/>: aperto con la
    /// configurazione, chiuso in <see cref="Dispose"/>. Nullo finche' il motore non e' configurato.
    /// </summary>
    internal static WcsHistoryDatabase? History;

    /// <summary>
    /// Lo scrittore a coda del registro dei messaggi dei PLC, gemello di
    /// <see cref="HistoryWriter"/> e con lo stesso ciclo di vita: nasce con la configurazione e si
    /// svuota in <see cref="Dispose"/>. E' una coda sola per tutti i PLC, non una per dispositivo.
    /// Nullo finche' il motore non e' configurato: chi scrive deve poterne fare a meno.
    /// </summary>
    /// <remarks>
    /// Stava insieme a quello dello storico fino alla voce <c>D183</c>: a coda piena cadevano le
    /// righe piu' vecchie senza guardare di quale registro fossero, e i telegrammi sono molti di
    /// piu' delle righe del navigatore.
    /// </remarks>
    internal static QueuedDatabaseWriter<PlcLogWrite>? PlcLogWriter;

    /// <summary>
    /// Il registro dei messaggi dei PLC, un file al mese in <see cref="DatabaseDirectory"/>:
    /// aperto e chiuso dove lo e' <see cref="History"/>. Nullo finche' il motore non e'
    /// configurato.
    /// </summary>
    internal static WcsPlcLogDatabase? PlcLog;

    /// <summary>
    /// Quanti mesi di storico restano in linea, compreso quello corrente; i file piu' vecchi
    /// vengono cancellati al cambio di mese. Va impostato prima di <see cref="StartAsync"/>.
    /// </summary>
    public static int HistoryMonthsToKeep { get; set; } = 12;

    /// <summary>
    /// L'attesa dello scarico automatico all'arrivo (<c>AutoUnloadLu</c>): l'UDC resta a bordo
    /// dell'elemento di uscita per questo tempo prima di essere scaricata, cosi' chi guarda la
    /// vede arrivare. Zero nel motore nudo (e nei collaudi); l'host la imposta da
    /// <c>Wcs:Engine:AutoUnloadDelayMilliseconds</c>.
    /// </summary>
    public static TimeSpan AutoUnloadDelay { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Lo scrittore a coda dello stato — le missioni — sul database del WCS: lotti piccoli,
    /// intervallo breve, e a coda piena chi scrive aspetta, perche' lo stato non si scarta e deve
    /// restare in ordine. Stesso ciclo di vita di <see cref="HistoryWriter"/>.
    /// </summary>
    internal static QueuedDatabaseWriter<WcsWrite>? StateWriter;

    /// <summary>
    /// Il database del WCS, <c>wcs.db</c> in <see cref="DatabaseDirectory"/>: aperto con la
    /// configurazione, chiuso in <see cref="Dispose"/>. Nullo finche' il motore non e' configurato.
    /// </summary>
    internal static WcsDatabase? Store;

    /// <summary>
    /// L'unico dispatcher degli esiti di estrazione pendenti: missione e ciclo master condividono
    /// sia l'invio in corso sia il freno dei ritentativi.
    /// </summary>
    internal static PendingExtractionOutcomeDispatcher ExtractionOutcomeDispatcher { get; } = new();

    /// <summary>
    /// L'unico dispatcher delle transizioni fisiche pendenti: scarico, ciclo master e recupero
    /// condividono single-flight e freno dei retry.
    /// </summary>
    internal static PendingWarehouseTransitionDispatcher WarehouseTransitionDispatcher { get; } = new();

    /// <summary>
    /// L'unico dispatcher del fatto «l'estrazione prelevata e' diventata un deposito» (voce 45): il
    /// ciclo master lo chiama a ogni giro, dopo quello degli esiti di estrazione.
    /// </summary>
    internal static PendingPickedExtractionReportDispatcher PickedExtractionReportDispatcher { get; } = new();

    /// <summary>
    /// The only dispatcher of the cells to give back to the warehouse (item 54): the master loop calls
    /// it on every turn, after the fact of item 45.
    /// </summary>
    internal static PendingCellGiveBackDispatcher CellGiveBackDispatcher { get; } = new();

    /// <summary>
    /// La cartella dei file del database del WCS. Predefinita: <c>Data</c> accanto all'eseguibile.
    /// Va impostata prima di <see cref="StartAsync"/>.
    /// </summary>
    public static string DatabaseDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "Data");
    public static readonly SemaphoreSlim SyncExtraction = new SemaphoreSlim(1, 1);

    /// <summary>
    /// Tetto di attesa per <see cref="SyncExtraction"/>, uguale per tutti i punti che lo prendono.
    /// </summary>
    /// <remarks>
    /// Il semaforo e' globale all'impianto: chi non lo ottiene entro questo tempo rinuncia al giro
    /// e riprova al successivo, invece di restare appeso. Prima due dei punti di presa aspettavano
    /// sul solo token di cancellazione, senza tetto, e con una conseguenza peggiore dell'attesa: se
    /// il token scattava, l'eccezione saltava il blocco finally, il flag di esecuzione restava a
    /// true e il ciclo moriva in silenzio, per sempre. Si veda la voce B20 del registro in
    /// WCS/doc/debito-tecnico.md.
    /// </remarks>
    public static readonly TimeSpan SyncExtractionTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Il freno dei ritentativi di <see cref="BuildRoutePath"/> (B70): una missione irrisolvibile
    /// smette di far girare una ricerca di percorso completa ad ogni giro da 10 ms. 200 ms per
    /// tentativo, fino a un tetto di 5 secondi. Il conteggio dei fallimenti serve anche a segnalare
    /// a soglia, perche' un avviso per giro allagherebbe il registro.
    /// </summary>
    /// <remarks>
    /// Il conteggio di una missione si dimentica quando la ricerca del giro riesce (un esito diverso
    /// da <c>NotAvailable</c>, in <see cref="StartCellSearch"/>), quando l'operatore la sposta
    /// (<c>MissionGateway.Move</c>, con la rotta subito o senza) o ne cambia la destinazione (il
    /// commit del cambio), quando esce dal motore (<c>ActiveMissions.Remove</c>, e il giro che la
    /// trova ancora in coda dopo l'uscita, in <see cref="BuildRoutePath"/>), e tutto insieme alla
    /// ricarica dell'impianto: non prima di ogni ricerca, come fino alla voce 39, quando l'attesa
    /// restava 200 ms e gli avvisi non partivano mai. Cresce solo per le ricerche fallite: una
    /// missione che torna in coda perche' ha uno step in volo aspetta un passo fisso, senza contare
    /// (<see cref="RetryBackoff.WaitOneStep"/>, scelta tecnica della specifica del 2/10). E'
    /// assegnabile perche' i collaudi lo costruiscano con un orologio finto, come
    /// <c>AutomationDeviceStorageManager.StoreSearchRetries</c>: un freno provato con attese vere
    /// sarebbe lento e, sotto carico, rosso su codice sano.
    /// </remarks>
    internal static RetryBackoff RoutePathRetries =
        new(TimeSpan.FromMilliseconds(RouteRetryFirstWaitMs), TimeSpan.FromMilliseconds(RouteRetryLongestWaitMs));

    /// <summary>La prima attesa del freno del giro delle rotte, in millisecondi; la usa anche <c>CellsToMarkFull.Retries</c>.</summary>
    internal const int RouteRetryFirstWaitMs = 200;

    /// <summary>L'attesa piu' lunga del freno del giro delle rotte, in millisecondi; la usa anche <c>CellsToMarkFull.Retries</c>.</summary>
    internal const int RouteRetryLongestWaitMs = 5000;

    #endregion Internal Fields

    #region Private Fields

    /// <summary>
    /// Vero fra <see cref="OpenDatabases"/> e <see cref="Dispose"/>: i database del WCS sono
    /// aperti e gli scrittori a coda girano. Era la stringa di connessione a SQL Server del
    /// gestionale a dire questa stessa cosa — «il motore e' configurato» — perche' l'apertura
    /// degli store locali le stava appesa; il motore non ha piu' quella stringa e la condizione
    /// si dichiara da se'.
    /// </summary>
    private static bool _databasesOpen;

    private static IWriter? _eventLogWriter;
    private static CancellationTokenSource? _cancellationTokenSource;
    private static CancellationToken _cancelToken;

    /// <summary>
    /// I cicli di sfondo dell'ultima <see cref="LoadNavigatorData"/>: stato, costruzione delle
    /// rotte, MasterProcess. Si fermano con il token; chi deve anche aspettarne la fine, come
    /// <see cref="StopBackgroundLoopsAsync"/>, li trova qui.
    /// </summary>
    private static Task[] _backgroundLoops = [];

    private static bool _isBuildingPath;
    private static bool _masterProcessIsRunning;

    /// <summary>
    /// Le ricerche della cella di deposito in volo, per missione (voce D160): il giro di
    /// <see cref="BuildRoutePath"/> le avvia senza attenderle, e non ne avvia una seconda per la
    /// stessa missione finche' la prima non e' finita. Il valore e' il compito che si completa
    /// alla fine della ricerca, per chi deve aspettarla (i collaudi).
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, Task> CellSearchesInFlight = new();

    #endregion Private Fields

    #region Internal Methods

    /// <summary>La ricerca della cella di deposito in volo per la missione, o <c>null</c> se non ce n'e'.</summary>
    internal static Task? CellSearchInFlight(Guid missionId) => CellSearchesInFlight.GetValueOrDefault(missionId);

    /// <summary>
    /// Prende per la missione il posto delle ricerche della cella, fuori dal giro di
    /// <see cref="BuildRoutePath"/>: lo prendono il cambio di destinazione dell'operatore (compito 3
    /// delle missioni), e lo spostamento che costruisce la rotta subito (<c>MissionGateway.Move</c>).
    /// Se una ricerca e' in volo non lo prende, e finche' lo tiene il giro non ne avvia una
    /// (<see cref="StartCellSearch"/> trova il posto preso e rimette la missione in coda, come per una
    /// ricerca gia' in volo). La domanda e la presa sono una sola operazione, quindi fra le due non
    /// ne parte nessuna.
    /// </summary>
    /// <param name="missionId">La missione da cambiare.</param>
    /// <param name="finished">Il compito che si completa alla fine del cambio, come quello di una ricerca.</param>
    /// <returns>False se una ricerca (o un altro cambio) e' in volo per la missione.</returns>
    internal static bool TryClaimCellSearch(Guid missionId, Task finished) => CellSearchesInFlight.TryAdd(missionId, finished);

    /// <summary>Restituisce il posto preso con <see cref="TryClaimCellSearch"/>, e solo quello.</summary>
    internal static void ReleaseCellSearchClaim(Guid missionId, Task finished) =>
        CellSearchesInFlight.TryRemove(new KeyValuePair<Guid, Task>(missionId, finished));

    /// <summary>
    /// La situazione della missione e' cambiata fuori dal giro di <see cref="BuildRoutePath"/>:
    /// l'operatore l'ha spostata (<c>MissionGateway.Move</c>), con la rotta subito o senza, o le ha
    /// cambiato la destinazione (il commit del cambio, lo stesso con cui il navigatore rifa' la cella
    /// persa); oppure la missione e' uscita dal motore (<c>ActiveMissions.Remove</c>). I fallimenti
    /// che il freno le contava non valgono piu', come quando la ricerca del giro riesce: una
    /// richiesta sua in coda il giro la riprende subito.
    /// </summary>
    internal static void ForgetRouteRetries(Guid missionId) => RoutePathRetries.Forget(missionId);

    /// <summary>
    /// Il fallimento del freno del giro delle rotte con cui un avviso ripetuto si scrive come avviso:
    /// il primo, che dice che la missione ha cominciato ad aspettare, e le soglie a cui scrive il
    /// freno stesso (<see cref="RiaccodaConAttesaCrescente"/>). Fra l'uno e l'altro la riga resta
    /// un'informazione: un avviso per giro, con il freno al tetto di 5 s, sono circa 720 righe l'ora
    /// per missione.
    /// </summary>
    internal static bool IsABrakeThreshold(int failure) => failure == 1 || failure == 10 || failure % 500 == 0;

    #endregion Internal Methods

    #region Public Delegates

    public delegate void DefragmentationChangedEventHandler(object? sender, DefragmentationChangedEventArgs value);

    public delegate void StatusChangedEventHandler(object? sender, NavigatorStatusChangedEventArgs value);

    #endregion Public Delegates

    #region Public Events

    public static event DefragmentationChangedEventHandler? DefragmentationChanged;

    public static event StatusChangedEventHandler? StatusChanged;

    #endregion Public Events

    #region Public Properties

    public static bool ActivateDebugLog { get; set; }

    /// <summary>
    /// Il registro dell'host, sempre acceso: chi ospita il motore (EngineHostedService) vi collega il
    /// proprio ILogger all'avvio. Non dipende da <see cref="ActivateDebugLog"/>, che governa solo la
    /// diagnostica di <see cref="WriteLog"/>: un errore che deve lasciare un segno in produzione passa
    /// da <see cref="ReportError"/>. I due segnalatori per cui era nato — l'onError della BusinessArea
    /// di WMS.Services e la traccia dell'area in processo — sono spariti con il taglio dei riferimenti
    /// ai progetti del gestionale, e oggi dentro il motore <see cref="ReportError"/> non ha chiamanti:
    /// resta il posto dove va un errore che non puo' sollevare, ed e' quello per cui l'host lo cabla.
    /// La voce D94 del registro del debito, che era chiusa proprio per la traccia in processo, va
    /// riletta.
    /// </summary>
    public static Action<Exception>? HostErrorLog { get; set; }

    /// <summary>
    /// Il motore e' configurato: <see cref="StartAsync"/> ha aperto i database del WCS e
    /// <see cref="Dispose"/> non li ha ancora chiusi. Prima qui c'era
    /// <c>DatabaseConnectionString</c>, la stringa di connessione a SQL Server del gestionale, e
    /// chi voleva sapere se il motore fosse configurato guardava se fosse vuota; quella stringa
    /// non esiste piu'.
    /// </summary>
    public static bool IsConfigured => _databasesOpen;

    public static Guid DefaultRejectItem { get; set; } = Guid.Empty;

    public static DefragmentationTypes Defragmentation
    {
        get;
        internal set
        {
            if (field == value) return;
            field = value;
            DefragmentationChanged?.Invoke(null, new DefragmentationChangedEventArgs(Defragmentation));
            EventSink?.NavigatorStatusChanged();
        }
    } = DefragmentationTypes.None;

    public static bool DisableUnloadLu { get; set; }
    public static bool DiscardLuManagedInWarehouse { get; set; }

    /// <summary>
    /// Chi ospita il motore riceve qui gli eventi da spingere fuori. Sostituisce <c>BridgeLinker</c>
    /// (il vecchio <c>ZSocket</c>): il motore non sa piu' che cosa c'e' dall'altra parte.
    /// </summary>
    public static Api.IEngineEventSink? EventSink { get; set; }

    /// <summary>
    /// Non assegnare una nota di spedizione se un'altra lavorazione attiva insiste sulla stessa
    /// cella di picking. Di norma spenta. L'opzione resta qui, nella configurazione dell'host del
    /// WCS, ma dal compito 14 chi la applica e' il gestionale: <see cref="ExtractionProcess"/> la
    /// passa a <see cref="IWarehouse.GetPendingNoteWorkAsync"/>, che la' interroga
    /// <c>LAVORAZIONI_NOTE</c> - tabella sua - con la query che era
    /// <c>DbInfo.NumActiveProcessingSameCellId</c>.
    /// </summary>
    public static bool LimitOneShippingNotePerBuffer { get; set; }

    public static bool LockCellOnStoringError { get; set; }

    public static int MasterProcessOnEvery { get; set; } = 1000;

    /// <summary>
    /// Rende il cambio di <see cref="NavStatus"/> atomico rispetto a chi decide in base allo stato e
    /// agisce subito dopo: oggi <see cref="ActiveMissions.Add"/>, che controlla Stop e Pausa e
    /// inserisce sotto questo lucchetto.
    /// </summary>
    /// <remarks>
    /// Il controllo stava fuori, prima del lucchetto dell'inserimento: chi lo passava in Run
    /// inseriva anche se nel frattempo lo Stop era gia' stato scritto, e a motore fermo compariva
    /// una missione che la sostituzione dell'impianto rifiuta. Il setter prende il lucchetto solo
    /// per l'assegnazione: i gestori di <see cref="StatusChanged"/> girano fuori, perche' possono
    /// aggiungere missioni a loro volta.
    /// </remarks>
    internal static readonly object StatusGate = new();

    public static NavigatorStatusTypes NavStatus
    {
        get;
        internal set
        {
            NavigatorStatusTypes oldStatus;
            lock (StatusGate)
            {
                if (field == value) return;
                oldStatus = field;
                field = value;
            }
            WriteLog(
                $"Navigator Status Changed to: {Enum.GetName(typeof(NavigatorStatusTypes), value)}",
                LogEntryType.Information);
            StatusChanged?.Invoke(null, new NavigatorStatusChangedEventArgs(oldStatus, value));
            EventSink?.NavigatorStatusChanged();

            // Le prenotazioni delle missioni che la ricarica ha chiuso tornano al gestionale adesso, in
            // sottofondo e una volta, non durante il caricamento (risposta 10 dell'8/10 sera).
            if (value == NavigatorStatusTypes.Run)
                ReservationsOfMissionsClosedOnReload.GiveBack();
        }
    } = NavigatorStatusTypes.Stop;

    public static int NumCurrentStoringStep { get; set; }

    public static bool SwitchLuLengthVsLuWidth { get; set; }

    #endregion Public Properties

    #region Internal Properties

    internal static bool ExtractionTimers
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;

            // Il campo va scritto comunque: l'uscita anticipata qui sotto salta solo la
            // propagazione ai dispositivi, non l'aggiornamento del valore letto dalla proprieta'.
            if (PathDevices.Instance.Count == 0)
                return;

            foreach (var item in PathDevices.Instance.List)
                if (value)
                    item.StartActiveProcesses();
                else
                    item?.StopActiveProcesses();
        }
    }

    // Il setter privato non era mai invocato: toglierlo dichiara onestamente che il valore e' fisso.
    // NON segue la rinomina dei namespace: questo valore viene SCRITTO sul database, nella
    // colonna ID_UTENTE delle righe prodotte dal motore. Cambiarlo spezzerebbe la
    // corrispondenza con le righe gia' presenti.
    internal static string UserId { get; } = "KazamControl.Process";

    // NON segue la rinomina dei namespace, per la stessa ragione di UserId: finisce in
    // ID_POSTAZIONE di ESTRAZIONI_UDC_AUTOMATICHE, e le righe gia' presenti portano questo
    // valore. La ricerca in POSTAZIONI che ne prendeva nome e dominio per una riga di registro
    // (GetDescPostazione) non c'e' piu': il registro scrive l'identificativo.
    internal static string WorkstationId { get; } = "KazamControl";

    /// <summary>
    /// Quello che il motore chiede al gestionale. Lo assegna chi ospita il motore, prima di
    /// avviarlo; nei test è una controfigura. Il motore non lo costruisce mai.
    /// </summary>
    internal static IWarehouse? Warehouse { get; set; }

    #endregion Internal Properties

    #region Public Methods

    public static void Dispose()
    {
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;

        // Quello che e' in coda va scritto prima di uscire, con un tetto di tempo: un database
        // irraggiungibile non deve tenere aperto il processo. Prima lo stato, che conta di piu'.
        var state = StateWriter;
        var history = HistoryWriter;
        var plcLog = PlcLogWriter;
        StateWriter = null;
        HistoryWriter = null;
        PlcLogWriter = null;

        if (state != null && !state.StopAsync().Wait(TimeSpan.FromSeconds(10)))
            WriteLog($"StateWriter: arresto scaduto con {state.Pending} scritture ancora in coda", LogEntryType.Warning);
        if (history != null && !history.StopAsync().Wait(TimeSpan.FromSeconds(10)))
            WriteLog($"HistoryWriter: arresto scaduto con {history.Pending} scritture ancora in coda", LogEntryType.Warning);
        if (plcLog != null && !plcLog.StopAsync().Wait(TimeSpan.FromSeconds(10)))
            WriteLog($"PlcLogWriter: arresto scaduto con {plcLog.Pending} scritture ancora in coda", LogEntryType.Warning);

        // I database del WCS si chiudono dopo che gli scrittori hanno finito di scriverci.
        var store = Store;
        Store = null;
        store?.Dispose();

        var historyStore = History;
        History = null;
        historyStore?.Dispose();

        var plcLogStore = PlcLog;
        PlcLog = null;
        plcLogStore?.Dispose();

        // Chiusi i database, il motore non e' piu' configurato: un StartAsync successivo deve
        // riaprire da capo, non saltare OpenDatabases perche' «erano gia' aperti» e caricare su
        // uno store nullo. Nella suite dell'host e' quello che fa cambiare strada ai test dopo un
        // caricamento riuscito (voce D145).
        _databasesOpen = false;
    }

    /// <summary>
    /// Il registro a cui <see cref="WriteLog"/> manda le proprie righe. Chi ospita il motore lo
    /// collega all'avvio; <c>null</c> lo stacca, e allora resta la sola console.
    /// </summary>
    /// <remarks>
    /// Fino alla revisione finale <b>nessuno lo chiamava</b>: le 97 righe di errore e avviso del
    /// motore, quando pure passavano la guardia di <see cref="ActivateDebugLog"/>, finivano solo su
    /// <c>Console.WriteLine</c>, che in un servizio Windows non e' nessun posto. Adesso
    /// <c>EngineHostedService</c> ci collega l'<c>ILogger</c> dell'host, lo stesso di
    /// <see cref="HostErrorLog"/>, e lo stacca allo spegnimento se e' ancora il proprio.
    /// </remarks>
    public static void SetEventLogWriter(IWriter? value)
    {
        _eventLogWriter = value;
    }

    /// <summary>Il registro collegato ora, per chi deve staccare solo il proprio.</summary>
    public static IWriter? EventLogWriter => _eventLogWriter;

    #endregion Public Methods

    #region Internal Methods

    internal static void AsiEvent(object sender, AsiEventEventArgs? e)
    {
        if (e == null) return;

        var item = PathDevices.Instance[e.Data?.Asi, e.Data?.Subitem, e.Data?.Partition];

        if (item == null)
        {
            WriteLog(
                $"AsiEvent: dispositivo non trovato per Asi: {e.Data?.Asi}, Subitem: {e.Data?.Subitem}, Partition: {e.Data?.Partition}",
                LogEntryType.Warning);
            return;
        }

        try
        {
            item.ConnectionAsiEvent(e);
        }
        catch (Exception ex)
        {
            WriteLog($"Error On AsiEvent Description: {ex.Message}\r\nStackTrace: {ex.StackTrace}",
                LogEntryType.Warning);
        }
    }

    internal static void AsiMovementIsCompleted(object sender, AsiMovementIsCompletedEventArgs? e)
    {
        if (e == null) return;

        var item = PathDevices.Instance[e.ItemGuid];

        if (item == null)
        {
            WriteLog($"AsiMovementIsCompleted: dispositivo non trovato per ItemGuid: {e.ItemGuid}",
                LogEntryType.Warning);
            return;
        }

        // Chiamata non attesa: un try/catch qui intorno intercetterebbe solo cio' che esplode
        // prima del primo await, cioe' quasi niente. La protezione vera e' dentro
        // AutomationDeviceConnectionManager.AsiMovementIsCompleted.
        _ = item.ConnectionAsiMovementIsCompleted(e);
    }

    /// <summary>
    /// Gestore dell'evento <c>AsiRequestsLuInfo</c> (compito 10): <c>async void</c> perche' e' un
    /// gestore di evento vero, non una property - non c'e' un chiamante a cui restituire un
    /// <see cref="Task"/>. Stesso schema gia' in uso altrove nel motore per le callback per riga.
    /// </summary>
    /// <remarks>
    /// <b>Il <c>try</c> che copre il corpo non e' ornamentale.</b> Da quando la descrizione
    /// dell'unita' di carico arriva dal gestionale attraverso il confine HTTP, quella riga solleva
    /// <c>WarehouseUnavailableException</c> ogni volta che il gestionale non risponde come deve — e
    /// <see cref="RequireWarehouse"/> solleva se il confine non e' nemmeno cablato. Un'eccezione che
    /// esce da un <c>async void</c> non ha nessun <see cref="Task"/> che la raccolga: viene
    /// rilanciata sul thread pool e, senza <c>AppDomain.UnhandledException</c> registrato da
    /// nessuna parte nel WCS, <b>termina il processo dell'host</b>. I due gemelli qui sotto,
    /// <see cref="LuUnloadedFromAsi"/> e <see cref="NewLuOnASI"/>, hanno sempre avuto questa forma.
    /// <para>
    /// <b>Al PLC non si risponde niente.</b> Senza la descrizione i dati da mandare sarebbero
    /// inventati; la richiesta resta senza risposta e il PLC esce dal proprio timeout, che e' il
    /// comportamento gia' in atto oggi (con l'eccezione, <c>SendLuDataToAsi</c> non partiva
    /// comunque) — con la differenza che adesso il motore e' ancora vivo per il giro dopo.
    /// </para>
    /// </remarks>
    internal static async void AsiRequestsLuData(object sender, AsiRequestsLuInfoEventArgs? e)
    {
        if (e == null) return;

        var item = PathDevices.Instance[e.LuDataFromAsiResult.Asi, e.LuDataFromAsiResult.Subitem,
            e.LuDataFromAsiResult.Partition];

        if (item?.LoadedLu == null)
        {
            WriteLog(
                $"AsiRequestsLuData: dispositivo non trovato o senza UDC caricata per Asi: {e.LuDataFromAsiResult.Asi}, Subitem: {e.LuDataFromAsiResult.Subitem}, Partition: {e.LuDataFromAsiResult.Partition}",
                LogEntryType.Warning);
            return;
        }

        var luId = item.LoadedLu.LuId;

        var requestId = e.LuDataFromAsiResult.RequestId;

        try
        {
            var datiUdc = await RequireWarehouse($"AsiRequestsLuData sull'unita' di carico {luId}").DescribeLoadUnitAsync(luId);

            var dataToSend = new LuInfoDataCommand
            {
                LuId = (int)Math.Round(luId),
                RequestId = requestId,
                DataConfirm = 1,
                LuCode = item.LoadedLu.LuCode ?? string.Empty,
                LuLength = datiUdc?.Width ?? 0,
                LuWidth = datiUdc?.Depth ?? 0,
                LuHeight = datiUdc?.Height ?? 0
            };

            item.DeviceConnection?.SendLuDataToAsi(GetAskingRunAsi(item), dataToSend);

            WriteLog(
                $"AsiRequestsLuData --> Item: {item.ASI}.{item.SUBITEM}.{item.PARTITION}\r\nLU_ID: {dataToSend.LuId}, LU_CODE: {dataToSend.LuCode}, LU_WIDTH: {dataToSend.LuWidth}, LU_HEIGHT: {dataToSend.LuHeight}, LU_LENGHT: {dataToSend.LuLength}, LU_WEIGHT: {dataToSend.LuWeight}",
                LogEntryType.Information);
        }
        catch (Exception ex)
        {
            WriteLog(
                $"AsiRequestsLuData: nessun dato mandato all'unita' di carico {luId} su {item.ASI}.{item.SUBITEM}.{item.PARTITION} (richiesta {requestId}): {ex.Message}\r\nStackTrace: {ex.StackTrace}",
                LogEntryType.Warning);
        }
    }

    internal static void AsiSendLuData(object sender, AsiSendLuDataEventArgs? e)
    {
        if (e == null) return;

        var item = PathDevices.Instance[e.ItemGuid];

        if (item == null)
        {
            WriteLog($"AsiSendLuData: dispositivo non trovato per ItemGuid: {e.ItemGuid}", LogEntryType.Warning);
            return;
        }

        try
        {
            if (SwitchLuLengthVsLuWidth && e.LuDataFromAsiResult != null)
            {
                (e.LuDataFromAsiResult.LuLength, e.LuDataFromAsiResult.LuWidth) = (e.LuDataFromAsiResult.LuWidth, e.LuDataFromAsiResult.LuLength);
            }

            Task.Run(async () =>
            {
                try
                {
                    await item.ConnectionAsiSendLuData(e);
                }
                catch (Exception ex)
                {
                    WriteLog($"{ex.Message}\r\nStackTrace: {ex.StackTrace}", LogEntryType.Warning);
                }
            });
        }
        catch (Exception ex)
        {
            WriteLog($"{ex.Message}\r\nStackTrace: {ex.StackTrace}", LogEntryType.Warning);
        }
    }

    internal static void ClosePlcConnections()
    {
        DeviceConnections.CloseConnection();
    }

    /// <remarks>
    /// Aveva un quarto parametro, <c>resetGroupExecuteStepRunning</c>, dichiarato con valore
    /// predefinito e **mai letto nel corpo**. Quattro chiamanti gli passavano <c>false</c>
    /// esplicito, credendo di ottenere un comportamento che non ottenevano. E' stato tolto invece
    /// che implementato perche' cosa si aspettassero quei chiamanti non e' scritto da nessuna parte:
    /// toglierlo fa dire al codice la verita', implementarlo a caso sarebbe stato inventare.
    /// Si veda la voce <c>B11</c> del registro in <c>WCS/doc/debito-tecnico.md</c>.
    /// </remarks>
    internal static async Task<RouteBuildStatusTypes> FindWhereStoreLu(PathMission? value, AutomationDevice? positionItem,
        LookWhereAllocateLuTypes allocateLuType)
    {
        if (value == null || positionItem == null)
            return RouteBuildStatusTypes.NotAvailable;

        value.MovingDescription = string.Empty;

        // La destinazione scelta dall'operatore vince sulla configurazione del dispositivo di
        // posizione (decisione del proprietario del 30/9): niente destinazione fissa, niente
        // modalita' di deposito, nessuna domanda al gestionale. Il segno cade all'arrivo (setter di
        // PositionItemId), quindi qui la missione non e' mai gia' a destinazione.
        //
        // Guardarlo qui non basta: i rami del deposito aspettano il gestionale (fino a 30 s) e
        // scrivono dopo, e intanto l'operatore puo' scegliere (compito 3 delle missioni). Per questo ogni scrittura
        // della configurazione lo riguarda nel momento in cui scrive (PathMission.
        // UnlessChosenByOperator: la porta, l'indirizzo e la destinazione del deposito, la
        // destinazione fissa, il rigetto per mancanza di celle), e se lo trova la ricerca finisce
        // come se fosse cominciata dopo la scelta: la rotta verso la scelta dell'operatore.
        //
        // Lo stesso per la cella persa (decisioni del proprietario dell'1/10), di qualunque
        // destinazione: un'altra cella gliela prenota il navigatore nel magazzino della destinazione
        // rimasta, con il flusso del cambio (MissionGateway.KeepTheStorageCellAsync), e per una
        // missione della configurazione, se li' non c'e' posto, in un altro magazzino del suo
        // ricollocatore o allo scarto (risposta 5 del 2/10). La configurazione del dispositivo di
        // posizione non la prenota altrove, non le riscrive la destinazione, e senza porta non c'e'
        // rotta (PathMission.BuildPathToDestination).
        //
        // Item 54 (decision 1 and answer 8 of the owner of 13/10): a mission bound for a device that is
        // not a storage keeps its destination in every storing mode, forcing included
        // (PathMission.KeepsItsDestination): it is not a deposit, and the warehouse is not asked for a
        // cell. The mode No is not a storing mode, and its fixed destination applies as before.
        if (value.OperatorDestination || value.StorageCellLost ||
            allocateLuType != LookWhereAllocateLuTypes.No && value.KeepsItsDestination)
            return await BuildRouteStraightToTheDestination(value, positionItem);

        // Item 54 (decision 5 of the owner of 13/10): on the relocator the operator sent it to, the
        // operator's choice decides instead of the relocator's configuration, in every mode (No included).
        // «Only bring it there»: it stays, nothing asked. «Deposit it»: the search the relocator makes for
        // itself (its linked cells, a cell it reaches), which the warehouse chooses and reserves as for
        // every deposit; without a cell, the rejection of every deposit. The cell found makes the
        // destination its storage, and the choice falls with it (setter of DestinationItemId).
        if (value.RelocatorArrivalApplies)
            return value.RelocatorArrival == Api.RelocatorArrival.Stay
                ? RouteBuildStatusTypes.Stays
                : await StoreOrReject(value, positionItem, PathMission.StoringSelectionTypes.StoreInLinkedCell);

        switch (allocateLuType)
        {
            case LookWhereAllocateLuTypes.CheckOnly:
                {
                    // Tiene la prenotazione che la missione ha gia' (compito 17): qui si chiede
                    // «esiste un posto per questa missione», non «un posto diverso».
                    if (!await value.GetStoreToWarehouse(PathMission.StoringSelectionTypes.CheckExistsAvailableCell,
                            keepExistingReservation: true))
                    {
                        // Se non esiste posizione di deposito rigetta

                        return await RejectUnlessChosenByOperator(value, positionItem);
                    }

                    // Se esiste una destinazione da raggiungere costruisce la strada
                    if ((positionItem.DestinationItem != null || positionItem.DestinationItem2 != null) &&
                        await BuildRouteToFixedDestination(value, positionItem))
                        return RouteBuildStatusTypes.TakenOver;

                    return value.MissionSteps.Count > 0 ? RouteBuildStatusTypes.Available : RouteBuildStatusTypes.NotAvailable;
                }
            case LookWhereAllocateLuTypes.BuildRouteToStore:
            case LookWhereAllocateLuTypes.BuildRouteToStoreByIdLuType:
                {
                    var selectionType = positionItem.IsLinkedToStorage
                        ? PathMission.StoringSelectionTypes.StoreInLinkedCell
                        : PathMission.StoringSelectionTypes.StoreInAnyAvailableCell;

                    return await StoreOrReject(value, positionItem, selectionType);
                }

            case LookWhereAllocateLuTypes.BuildRouteToDifferentStoreLocation:
                {
                    var selectionType = positionItem.IsLinkedToStorage
                        ? PathMission.StoringSelectionTypes.StoreInLinkedCell
                        : PathMission.StoringSelectionTypes.StoreInAnyAvailableCellDifferentStoreLocation;

                    return await StoreOrReject(value, positionItem, selectionType);
                }

            case LookWhereAllocateLuTypes.No:
                {
                    var hasFixedDestination = positionItem.DestinationItem != null || positionItem.DestinationItem2 != null;
                    var isNotRejectionToSamePosition = !(value.DestinationItemId == value.PositionItemId && value.MissionType == MissionTypes.Rejection);

                    // Senza destinazione fissa la rotta e' quella dritta verso la destinazione della
                    // missione: la stessa funzione dello spostamento (MissionGateway.Move), che promette
                    // proprio la rotta che questo giro costruirebbe.
                    if (!hasFixedDestination || !isNotRejectionToSamePosition)
                        return await BuildRouteStraightToTheDestination(value, positionItem);

                    if (value.MissionSteps.Count > 0) return RouteBuildStatusTypes.Available;

                    // Se esiste una destinazione da raggiungere costruisce la strada
                    if (await BuildRouteToFixedDestination(value, positionItem))
                        return RouteBuildStatusTypes.TakenOver;

                    return value.MissionSteps.Count > 0 ? RouteBuildStatusTypes.Available : RouteBuildStatusTypes.NotAvailable;
                }
        }

        return RouteBuildStatusTypes.NotAvailable;
    }

    /// <summary>
    /// I rami del deposito di <see cref="FindWhereStoreLu"/>: la cella con la sua rotta, oppure lo
    /// scarto se il gestionale non ne ha una. Una cella trovata e presa, la cui rotta l'ha cambiata un
    /// altro mentre la si scriveva, non e' «nessuna cella»: la missione e' gia' in coda al giro delle
    /// rotte (<see cref="RouteBuildStatusTypes.TakenOver"/>), e la cella l'ha gia' restituita la ricerca,
    /// che ne ha contato il fallimento nel freno (risposta 6 del proprietario dell'11/10). Prima la
    /// ricerca la leggeva come una cella mancante e la mandava allo scarto con la cella ancora prenotata
    /// (rilievo 1 del secondo giro del compito 3 della voce 41).
    /// </summary>
    private static async Task<RouteBuildStatusTypes> StoreOrReject(PathMission value, AutomationDevice positionItem,
        PathMission.StoringSelectionTypes selectionType)
    {
        var (found, _, routeNotWritten) = await value.SearchTheStoreAsync(selectionType, keepExistingReservation: true);

        // Presa da un altro o in attesa del rigetto dal campo rimandato: in tutti e due i casi la missione
        // e' gia' in coda, e qui non si conta niente (il freno l'ha contato la ricerca, solo nel primo).
        if (routeNotWritten != null)
            return RouteBuildStatusTypes.TakenOver;

        if (found)
            return RouteBuildStatusTypes.Available;

        // Se non esiste posizione di deposito rigetta
        return await RejectUnlessChosenByOperator(value, positionItem);
    }

    /// <summary>
    /// La rotta dritta verso la destinazione della missione: il ramo <c>No</c> di
    /// <see cref="FindWhereStoreLu"/> la usa per un dispositivo senza destinazione fissa, e lo stesso
    /// <see cref="FindWhereStoreLu"/> per una missione con la destinazione scelta dall'operatore o
    /// che aspetta la cella persa. La rotta che c'e' resta (e' quella che ha committato il cambio di
    /// destinazione, o il navigatore con la cella nuova); se non c'e' si costruisce verso la
    /// destinazione della missione, e verso un magazzino solo con la porta della cella. La usa anche
    /// lo spostamento di una missione, dove il navigatore andrebbe dritto
    /// (<see cref="GoesStraightToTheDestination"/>).
    /// </summary>
    internal static async Task<RouteBuildStatusTypes> BuildRouteStraightToTheDestination(PathMission value, AutomationDevice positionItem)
    {
        if (value.MissionSteps.Count > 0) return RouteBuildStatusTypes.Available;

        // Una rotta che un altro ha cambiato mentre la si calcolava non e' «nessun percorso»: la
        // missione e' gia' in coda al giro delle rotte, e il freno non conta un fallimento.
        if (value.DestinationItem != positionItem && await value.BuildPathToDestination())
            return RouteBuildStatusTypes.TakenOver;

        return value.MissionSteps.Count > 0 ? RouteBuildStatusTypes.Available : RouteBuildStatusTypes.NotAvailable;
    }

    /// <summary>
    /// Da <paramref name="positionItem"/> il navigatore andrebbe dritto alla destinazione della
    /// missione (<see cref="BuildRouteStraightToTheDestination"/>), senza chiedere niente al
    /// gestionale e senza sceglierne un'altra: la destinazione l'ha scelta l'operatore, oppure il
    /// dispositivo non ha una modalita' di deposito (<c>LookWhereAllocateLu</c> <c>No</c>) ne' una
    /// destinazione fissa. E' dove lo spostamento di una missione (<c>MissionGateway.Move</c>)
    /// costruisce la rotta subito, anche a motore fermo (decisione 2 del proprietario del 30/9; in
    /// Recover lo spostamento e' rifiutato, risposta 4 dell'8/10, e lo guarda da se'); altrove la missione
    /// va in coda come prima, e la rotta la costruisce questo giro in Run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Resta fuori la deframmentazione, che <see cref="PathMission.BuildPathToDestination"/> costruisce a
    /// modo suo e che, fuori dalla sua cella, chiama il gestionale e toglie la missione: lo spostamento e'
    /// sincrono e non chiama il gestionale. The change of cell no longer stays out (item 54, decision 3
    /// of the owner of 13/10): its rebuild reads only the WCS's door map, and moved out of its cell onto a
    /// device that is not its relocator it becomes the deposit it physically is (decision 2), with the
    /// route at once; still in its cell or on its relocator the doors keep it off the straight route as
    /// before (<see cref="PathMission.TheDoorsKeepItOffTheStraightRoute"/>). Lo stesso per una
    /// missione le cui due porte ne fanno un cambio cella prima della ricerca, o un deposito che aspetta
    /// la consegna del prelievo (item 54, <see cref="PathMission.TheDoorsKeepItOffTheStraightRoute"/>),
    /// ma quello non si guarda qui: le porte
    /// si cercano nella mappa del database del WCS, e lo spostamento lo chiede dopo aver preso il
    /// posto delle ricerche della missione. Le due porte da sole non bastano: un'estrazione dal
    /// magazzino verso un polmone, o una missione uscita dal magazzino e mandata dall'operatore su un
    /// polmone, le hanno tutte e due e vanno dritte.
    /// </para>
    /// <para>
    /// La cella persa non serve guardarla qui: senza porta <see cref="PathMission.BuildPathToDestination"/>
    /// non costruisce verso un magazzino, e la missione va in coda al giro, che le prenota una cella
    /// (<c>MissionGateway.KeepTheStorageCellAsync</c>). Una missione dell'operatore diretta in un
    /// magazzino con la sua porta ha la rotta subito; la cella la riguarda il giro prima del deposito,
    /// che senza la sua conferma la manda in coda (<c>PathMission.MayStartTheDeposit</c>).
    /// </para>
    /// <para>
    /// Item 54 (decision 1 and answer 8 of the owner of 13/10): on a device with a storing mode the
    /// navigator goes straight also for a mission bound for a device that is not a storage
    /// (<see cref="PathMission.KeepsItsDestination"/>), the same rule as <see cref="FindWhereStoreLu"/>:
    /// the extraction of the case UC256355, moved onto the entry point CV07, has the route to its bay at
    /// once.
    /// </para>
    /// </remarks>
    internal static bool GoesStraightToTheDestination(PathMission mission, AutomationDevice positionItem) =>
        mission.MissionType is not MissionTypes.DefragmentCell &&
        (mission.OperatorDestination ||
         positionItem.LookWhereAllocateLu != LookWhereAllocateLuTypes.No && mission.KeepsItsDestination ||
         positionItem.LookWhereAllocateLu == LookWhereAllocateLuTypes.No &&
         positionItem.DestinationItem == null && positionItem.DestinationItem2 == null);

    /// <summary>
    /// Il gestionale non ha una cella per la modalita' di deposito: la missione si rigetta, come
    /// sempre, a meno che nel frattempo l'operatore abbia scelto la destinazione. In quel caso il
    /// rigetto non si fa (<see cref="PathMission.RejectTo"/> con <c>unlessChosenByOperator</c>) e la
    /// ricerca finisce verso la scelta dell'operatore.
    /// </summary>
    /// <remarks>
    /// Ci arriva anche la ricerca che ha trovato il segno al momento di scrivere
    /// (<c>StorageLuResultTypes.ChosenByOperator</c>): <c>GetStoreToWarehouse</c> la riporta come
    /// «nessuna cella», ed e' qui, guardando il segno, che l'esito diventa la rotta verso la scelta.
    /// <see cref="RouteBuildStatusTypes.Rejected"/> vale sia per il rigetto fatto, sia per il rigetto
    /// non fatto senza segno (<c>RejectTo</c> risponde falso anche quando non puo' rigettare): e' cio'
    /// che il ramo rispondeva prima del segno. Lo usa anche la cella persa di una missione della
    /// configurazione che non trova posto (<c>Api.MissionGateway.KeepTheStorageCellAsync</c>), che il
    /// punto di scarto lo guarda prima (<see cref="PathMission.NoStoreCellRejectionPoint"/>): senza,
    /// la missione aspetta con il freno invece di restare ferma senza rotta.
    /// </remarks>
    internal static async Task<RouteBuildStatusTypes> RejectUnlessChosenByOperator(PathMission value, AutomationDevice positionItem)
    {
        if (await value.RejectTo(PathMission.RejectionTypes.NoStoreCellAvailable, unlessChosenByOperator: true) ||
            !value.OperatorDestination)
            return RouteBuildStatusTypes.Rejected;

        return await BuildRouteStraightToTheDestination(value, positionItem);
    }

    internal static RunAsi GetAskingRunAsi(AutomationDevice device)
    {
        return new RunAsi(device.ItemGuid, device.ASI, device.SUBITEM, device.PARTITION, device.TimeOutGetLuInfo,
            device.DelayBlind, device.MoveToResult, device.ErrorCodeResult);
    }

    internal static DataAsi GetDataAsi(AutomationDevice device)
    {
        return new DataAsi(device.ASI, device.SUBITEM, device.PARTITION);
    }

    /// <summary>
    /// La missione che porta questa assegnazione di nota, nei tre posti in cui il motore la tiene.
    /// </summary>
    /// <remarks>
    /// La gemella di <see cref="GetMissionByLuId"/>, e aveva lo stesso difetto sul secondo posto:
    /// cercava per <c>LUtoPick</c> e restituiva <c>LoadedLu</c>, cioe' un'altra missione.
    /// </remarks>
    internal static PathMission? GetMissionByLuAssignmentId(decimal luAssignmentId)
    {
        var mission = ActiveMissions.Instance.FirstOrDefault(x => x != null && x.LuNoteAssignmentId == luAssignmentId);
        if (mission != null)
            return mission;

        mission = PathDevices.Instance
            .FirstOrDefault(x => x?.LUtoPick != null && x.LUtoPick.LuNoteAssignmentId == luAssignmentId)?.LUtoPick;

        if (mission != null)
            return mission;

        foreach (var item in PathDevices.Instance.Where(x => x is { IsLinkedToStorage: true }))
        {
            mission = item?.QueuedLUtoPickUp.FirstOrDefault(x => x != null && x.LuNoteAssignmentId == luAssignmentId);

            if (mission != null)
                return mission;
        }

        return null;
    }

    /// <summary>
    /// La missione che porta questa unita' di carico, cercata nei tre posti in cui il motore la
    /// tiene: le missioni attive, l'unita' che un dispositivo sta prelevando (<c>LUtoPick</c>), e le
    /// code di prelievo dei dispositivi legati al deposito.
    /// </summary>
    /// <remarks>
    /// <b>Il secondo posto restituisce la missione trovata, non un'altra.</b> Cercava il
    /// dispositivo per <c>LUtoPick</c> e poi restituiva il suo <c>LoadedLu</c>, che e' la missione
    /// <i>a bordo</i> — un'altra unita' di carico. <c>MissionQueries.HoldsLoadUnit</c> rispondeva
    /// quindi guardando la missione sbagliata, e su un dispositivo che sta prelevando senza avere
    /// niente a bordo — il caso normale — <c>LoadedLu</c> e' nullo e la ricerca proseguiva come se
    /// quell'unita' non fosse tenuta da nessuno.
    /// </remarks>
    internal static PathMission? GetMissionByLuId(decimal luId)
    {
        var mission = ActiveMissions.Instance.FirstOrDefault(x => x != null && x.LuId == luId);

        if (mission != null)
            return mission;

        mission = PathDevices.Instance.FirstOrDefault(x => x?.LUtoPick != null && x.LUtoPick.LuId == luId)?.LUtoPick;

        if (mission != null)
            return mission;

        foreach (var item in PathDevices.Instance.Where(x => x is { IsLinkedToStorage: true }))
        {
            mission = item?.QueuedLUtoPickUp.FirstOrDefault(x => x != null && x.LuId == luId);

            if (mission != null)
                return mission;
        }

        return null;
    }

    internal static RunAsi GetMovementRunAsi(AutomationDevice device)
    {
        return new RunAsi(device.ItemGuid, device.ASI, device.SUBITEM, device.PARTITION, device.TimeOutPlcMoving,
            device.DelayBlind, device.MoveToResult, device.ErrorCodeResult);
    }

    internal static bool IsLuInExtraction(decimal luId)
    {
        return GetMissionByLuId(luId) != null;
    }

    /// <summary>
    /// Il motivo dell'ultimo caricamento fallito da <see cref="StartAsync"/>, nullo dopo un
    /// caricamento riuscito per qualunque via (<see cref="StartAsync"/>, la ricarica del
    /// diagramma, il comando 1006: tutte passano da <see cref="LoadNavigatorData"/>, che lo
    /// azzera in fondo). E' quello che l'host mette in <c>/status</c> mentre e' vivo con il
    /// motore non caricato e lo ritenta, e finche' non e' nullo le rotte che toccano il motore
    /// rispondono 503 (voce D145). Il setter e' interno per i collaudi, che lo rimettono a posto.
    /// </summary>
    public static string? LoadError { get; internal set; }

    /// <summary>
    /// L'esito dell'ultima migrazione della geometria (<see cref="GeometryMigration.RunOnceAsync"/>)
    /// eseguita da <see cref="OpenDatabases"/>, nullo finche' questo processo non l'ha ancora
    /// girata o se e' fallita con un'eccezione. E' un fatto, non un evento — gira una volta per
    /// avvio — e questa proprieta' e' il modo in cui l'host lo legge per scriverlo sul proprio
    /// registro: <see cref="WriteLog"/> alla gravita' Informazione resta sotto
    /// <see cref="ActivateDebugLog"/> (voce D94), spento di serie, e senza questa via il verbale
    /// non arriverebbe mai al registro dell'host con la configurazione predefinita. Il setter e'
    /// interno per i collaudi.
    /// </summary>
    public static GeometryMigrationReport? LastGeometryMigration { get; internal set; }

    /// <summary>
    /// Configura il motore e ne attende l'avvio.
    /// </summary>
    /// <returns>True se il caricamento e' riuscito, false se e' fallito</returns>
    /// <remarks>
    /// <para>
    /// E' il punto d'avvio esplicito che mancava. Prima l'unico modo di avviare il motore era
    /// assegnare <c>DatabaseConnectionString</c>, che lanciava il caricamento in un
    /// <c>Task.Run</c> scartato: il chiamante non poteva attenderlo ne' sapere se fosse fallito,
    /// perche' l'errore finiva solo nel log. Si veda la voce <c>B9</c> del registro in
    /// <c>WCS/doc/debito-tecnico.md</c>.
    /// </para>
    /// <para>
    /// Il parametro era la stringa di connessione a SQL Server del gestionale, che il motore
    /// apriva e passava a ogni query dello strato <c>ORM/</c>. Il motore non legge piu' quelle
    /// tabelle — glielo dice <see cref="Warehouse"/>, oltre il confine — e la cartella dei propri
    /// database e' <see cref="DatabaseDirectory"/>: avviare non richiede piu' nessun argomento.
    /// </para>
    /// </remarks>
    public static async Task<bool> StartAsync()
    {
        // Anche l'apertura dei database sta nel try (voce D145): un wcs.db in mano a un altro
        // processo, o una cartella dei dati che non si puo' creare, sollevava fuori da ogni guardia
        // e non lasciava nemmeno la riga di registro. Se fallisce, i database restano non aperti e
        // il tentativo successivo riapre da capo.
        try
        {
            OpenDatabases();
            // Sotto il cancello delle scritture dell'impianto (D2): si veda PlantQueries.ReloadUnderGateAsync.
            await Api.PlantQueries.ReloadUnderGateAsync(LoadNavigatorData);
            return true;
        }
        catch (Exception ex)
        {
            LoadError = ex.Message;
            WriteLog($"Error loading navigator data: {ex.Message}\r\nStackTrace: {ex.StackTrace}",
                LogEntryType.Error);
            return false;
        }
    }

    /// <summary>
    /// Apre i database del WCS — lo store e lo storico, in <see cref="DatabaseDirectory"/> — con i
    /// loro scrittori a coda, senza avviare nulla. Se erano gia' aperti non fa niente: il segnale
    /// e' <see cref="_databasesOpen"/>, che <see cref="Dispose"/> rimette a falso perche' il
    /// tentativo successivo riapra da capo (voce D145).
    /// </summary>
    private static void OpenDatabases()
    {
        if (_databasesOpen) return;

        // Gli scrittori precedenti, se c'erano, finiscono di scrivere sul database di prima e si
        // fermano per conto loro: chi arriva dopo trova gia' quelli nuovi.
        var previousHistory = HistoryWriter;
        var previousState = StateWriter;
        var previousPlcLog = PlcLogWriter;

        // Il database del WCS: si apre (o si crea) nella cartella configurata, con lo schema portato
        // all'ultima migrazione. Il precedente, se c'era, si chiude dopo che il suo scrittore ha
        // finito. Prima dello storico: un mese che esce dalla ritenzione lascia qui i riferimenti
        // delle sue lavorazioni (voce D103), anche al primo cambio di mese, che avviene nell'apertura.
        var previousStore = Store;
        var store = WcsDatabase.Open(DatabaseDirectory);
        Store = store;

        // Lo storico: un file al mese nella stessa cartella, con la ritenzione configurata.
        var previousHistoryStore = History;
        History = WcsHistoryDatabase.Open(DatabaseDirectory, HistoryMonthsToKeep,
            keepReferences: references => RetiredHandledReferences.Keep(store, references));

        HistoryWriter = new QueuedDatabaseWriter<HistoryWrite>(
            new WcsHistoryBatchExecutor(History).ExecuteAsync,
            log: (message, ex) => WriteLog(
                ex == null ? $"HistoryWriter: {message}" : $"HistoryWriter: {message} {ex.Message}",
                LogEntryType.Warning));

        // Il registro dei messaggi dei PLC: un file al mese suo, accanto a quelli dello storico.
        var previousPlcLogStore = PlcLog;
        PlcLog = WcsPlcLogDatabase.Open(DatabaseDirectory);

        PlcLogWriter = new QueuedDatabaseWriter<PlcLogWrite>(
            new WcsPlcLogBatchExecutor(PlcLog).ExecuteAsync,
            // Le opzioni sono quelle predefinite di QueuedDatabaseWriterOptions — Capacity 50 000,
            // DropOldestWhenFull vero, MaxBatchSize 200, FlushInterval 250 ms — e questa coda le
            // accetta tutte: nominate qui perche' cambiarle la' cambierebbe questa in silenzio.
            // Scartare qui e' accettabile — si perde un telegramma passato, mai il controllo
            // dell'impianto — ed e' la ragione stessa della separazione: quando si riempie deve
            // cadere un telegramma, non la riga del navigatore che dice perche' l'impianto si e'
            // fermato. Il numero resta 50 000 perche' e' il tetto sotto cui i due registri stavano
            // insieme fin qui: la separazione non alza la memoria occupabile (ogni riga porta con
            // se' l'XML). Quanti telegrammi al minuto arrivino non lo sa ancora nessuno: la misura
            // vuole PLC veri, e i contatori sono PlantQueries.PlcLogWriterCounters. Voce D183.
            log: (message, ex) => WriteLog(
                ex == null ? $"PlcLogWriter: {message}" : $"PlcLogWriter: {message} {ex.Message}",
                LogEntryType.Warning));


        // La geometria del disegno entra nel modello una volta sola, all'avvio: da qui in poi la
        // verita' e' il modello e PUT /plant/diagram non accetta piu'. Idempotente - al secondo
        // avvio non fa nulla -, e il suo esito finisce nel registro perche' gira una volta sola
        // in casa d'altri e nessuno la guarda mentre accade.
        //
        // Se il blob e' illeggibile la lettura solleva, e la si intercetta qui, non si lascia
        // uscire: la migrazione non deve impedire al motore di partire. Il motore non legge mai la
        // geometria - e' un dato di chi disegna - e un impianto che non movimenta bancali perche'
        // un file di disegno e' illeggibile sarebbe un modo nuovo di fermarlo, introdotto da noi.
        // L'host resta comunque vivo e rispondente su un blob malformato: e' la garanzia della voce
        // D145, che StartAsync intercetta gia' attorno a OpenDatabases e per cui
        // RetryEngineLoadAsync ritenta OpenDatabases ogni EngineLoadRetrySeconds, all'infinito -
        // qui basta non abortire il resto di questo metodo (gli scrittori nuovi, l'arresto e la
        // dispose di quelli vecchi, _databasesOpen = true), altrimenti ogni ritentativo
        // abbandonerebbe HistoryWriter e PlcLogWriter - gia' creati sopra, con un ciclo di sfondo
        // avviato dal costruttore - e i tre database precedenti, per ore o giorni, perche'
        // _databasesOpen resterebbe falso e il giro ricomincerebbe da capo.
        //
        // Non si marca GeometryMigratedAt su un fallimento (si veda GeometryMigration): il disegno
        // resta percio' non congelato, il vecchio Editor puo' ancora aprirlo e chi installa puo'
        // correggere il blob; al prossimo avvio la migrazione riprova.
        //
        // GetAwaiter().GetResult() e' accettabile qui perche' OpenDatabases e' sincrona e gira
        // all'apertura, prima che il motore giri - non perche' sia gia' la forma in uso altrove in
        // questo file: e' l'unica occorrenza in Navigator.cs.
        GeometryMigrationReport? geometryReport;
        try
        {
            geometryReport = GeometryMigration.RunOnceAsync(Store).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            geometryReport = null;
            WriteLog(
                $"Migrazione della geometria del disegno fallita, il diagramma non e' marcato e si " +
                $"riprova al prossimo avvio: {ex.Message}",
                LogEntryType.Error);
        }

        // I contatori del verbale girano su knownIds, cioe' sui PlantItem che il modello gia'
        // riconosce come dispositivi (si veda GeometryMigration.RunOnceAsync): uno zero dice
        // "niente di cio' che il lettore guarda mancava", non "non c'era altro nel disegno". Un
        // GUID nel blob che non e' un PlantItem non entra in nessuno di questi numeri, tranne
        // UnknownGuids.
        //
        // Il verbale non lo scrive piu' solo questo WriteLog, che a Informazione resta sotto
        // ActivateDebugLog (voce D94) e con la configurazione di serie non arriva da nessuna
        // parte: LastGeometryMigration lo rende disponibile all'host, che lo scrive col proprio
        // registratore accanto alla riga che dice da dove ha caricato il motore
        // (EngineHostedService.LoadEngineAsync), che invece quella gravita' la scrive sempre.
        LastGeometryMigration = geometryReport;

        if (geometryReport is { Executed: true })
            WriteLog(
                $"Geometria del disegno migrata: {geometryReport.Placed} dispositivi collocati " +
                $"({geometryReport.PartiallyPlaced} con riquadro letto solo in parte), " +
                $"{geometryReport.ItemsWithoutPosition} senza posizione, " +
                $"{geometryReport.ElementsWithoutGuid} elementi senza identita', " +
                $"{geometryReport.GuidsWithoutGeometry} senza riquadro leggibile, " +
                $"{geometryReport.UnknownGuids} identita' sconosciute (i numeri non sono una partizione).",
                LogEntryType.Information);

        StateWriter = new QueuedDatabaseWriter<WcsWrite>(
            new WcsBatchExecutor(Store).ExecuteAsync,
            new QueuedDatabaseWriterOptions
            {
                DropOldestWhenFull = false,
                Capacity = 5_000,
                MaxBatchSize = 100,
                FlushInterval = TimeSpan.FromMilliseconds(50)
            },
            ReportStateWriterProblem);

        if (previousHistory != null)
            _ = previousHistory.StopAsync().ContinueWith(_ => previousHistoryStore?.Dispose());
        else
            previousHistoryStore?.Dispose();
        // Stessa forma, e stesso difetto gia' aperto in D193: l'esito di questo arresto non lo
        // guarda nessuno.
        if (previousPlcLog != null)
            _ = previousPlcLog.StopAsync().ContinueWith(_ => previousPlcLogStore?.Dispose());
        else
            previousPlcLogStore?.Dispose();
        if (previousState != null)
            _ = previousState.StopAsync().ContinueWith(_ => previousStore?.Dispose());
        else
            previousStore?.Dispose();

        CheckDataFromAsi = new();
        RecordDataFromAsi = new();
        _databasesOpen = true;
    }

    /// <summary>
    /// Solo per le prove: chiamato come primo passo di <see cref="LoadNavigatorData"/>, prima di
    /// toccare qualunque cosa. Serve a vedere DA DENTRO un caricamento che il cancello delle scritture
    /// e' tenuto (decisione del proprietario del 27/9, D2), e a fermarlo li'. Nullo in esercizio.
    /// </summary>
    internal static Func<Task>? LoadStartingForTesting { get; set; }

    internal static async Task LoadNavigatorData()
    {
        if (LoadStartingForTesting is { } loadStarting) await loadStarting();

        // Svuota lo stato runtime.
        //
        // Le righe qui sotto usavano "Instance.List.Clear()", ma List e' una proprieta' che
        // costruisce una copia nuova a ogni lettura: il Clear agiva sulla copia e veniva buttato
        // via. Quattro di quelle righe non svuotavano nulla.
        //
        // PathDevices e PathJunctions NON si svuotano qui, e non e' una dimenticanza. Add(Guid) fa
        // GetOrAdd(guid, g => new AutomationDevice(g)): finche' la collezione non si svuota i
        // dispositivi vengono riusati, mentre svuotandola se ne creerebbero di nuovi lasciando vivi
        // i vecchi, che restano iscritti a Navigator.StatusChanged e continuano i loro cicli di
        // fondo. Si avrebbero due insiemi di dispositivi che comandano lo stesso impianto. Farlo
        // per bene richiede di rilasciarli. Si vedano le voci B1 e B12 del registro.
        // Lo svuotamento sta piu' sotto, DOPO l'arresto dei cicli della sessione precedente.
        // Nell'ordine inverso -- com'era -- BuildRoutePath e MasterProcess potevano fare un giro
        // intero su collezioni appena svuotate. Si veda il commento li'.

        // Ricrea sempre una nuova CancellationTokenSource per evitare di riutilizzare un token cancellato
        var oldCts = _cancellationTokenSource;
        if (oldCts != null)
        {
            try
            {
                await oldCts.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
                // Già rilasciata; ignora
            }
            finally
            {
                oldCts.Dispose();
            }
        }

        _cancellationTokenSource = new CancellationTokenSource();
        _cancelToken = _cancellationTokenSource.Token;

        // Svuotamento dello stato di esecuzione, a cicli vecchi gia' annullati. L'annullamento e'
        // cooperativo, quindi un giro gia' iniziato finisce comunque; ma da qui in avanti nessun
        // giro parte sul vuoto, e i cicli nuovi partono piu' sotto, a collezioni gia' ricaricate.
        MissionSteps.Instance.Clear();
        await ActiveMissions.Instance.Clear(false);
        ManagedLu.Instance.Clear();
        CommandToDo.Instance.Clear();
        RoutesToBuild.Instance.Items = new();
        RoutePathRetries.Clear();
        ExtractionOutcomeDispatcher.Reset();
        WarehouseTransitionDispatcher.Reset();
        PickedExtractionReportDispatcher.Reset();
        CellGiveBackDispatcher.Reset();
        Api.PendingMissionChanges.Reset();

        // Inizializzazioni. Qui il motore costruiva la BusinessArea di WMS.Services, cioe' caricava
        // nel proprio processo tutto lo strato dei servizi del gestionale. Non lo fa piu': quello
        // che gli serve dal gestionale glielo da' Warehouse, il confine HTTP che l'host gli cabla
        // prima dell'avvio (EngineHostedService).
        Defragmentation = await DbInfo.GetDefragmentationStatus();

        // Loop aggiornamento stato periodico
        var statusLoop = BackgroundLoop("UpdateStatus", UpdateStatus, () => 1000, _cancelToken);
        _backgroundLoops = [statusLoop];

        NavStatus = NavigatorStatusTypes.Recover;

        await BuildNavigatorItemsJunctions();

        await DbInfo.RecoverFailure();

        NavStatus = NavigatorStatusTypes.Stop;

        DeviceConnections.Initialize();

        // Loop build delle route
        var routeLoop = BackgroundLoop("BuildRoutePath", BuildRoutePath, () => 10, _cancelToken);

        // Loop MasterProcess
        var masterLoop = BackgroundLoop("MasterProcess", MasterProcess,
            () => MasterProcessOnEvery > 0 ? MasterProcessOnEvery : 1, _cancelToken);

        _backgroundLoops = [statusLoop, routeLoop, masterLoop];

        // Il motore e' caricato, da qualunque via si sia arrivati qui: il motivo dell'ultimo
        // fallimento non vale piu', e il ritentativo dell'host, che lo guarda, si ferma.
        LoadError = null;
    }

    /// <summary>
    /// Un ciclo di sfondo del motore: esegue <paramref name="body"/>, aspetta, e ricomincia finche'
    /// il motore non si ferma.
    /// </summary>
    /// <param name="name">Il nome che compare nel registro quando un giro fallisce</param>
    /// <param name="body">Il lavoro di un giro</param>
    /// <param name="delayMilliseconds">Quanto aspettare fra un giro e il successivo, riletto a ogni giro</param>
    /// <param name="cancellationToken">Il gettone che ferma il ciclo</param>
    /// <param name="failureSummaryEvery">
    /// Ogni quanto, al piu', esce un riepilogo mentre lo stesso guasto si ripete. Un minuto di
    /// serie; i collaudi lo stringono o lo allargano per non dipendere dall'orologio.
    /// </param>
    /// <remarks>
    /// <b>Il <c>try</c> sta dentro il <c>while</c>, non intorno.</b> I tre cicli avevano il proprio
    /// <c>catch</c> fuori dal ciclo: la prima eccezione lo terminava <b>per la vita del processo</b>,
    /// in silenzio. Su <c>statusLoop</c> voleva dire che un guasto momentaneo di SQLite spegneva
    /// per sempre l'aggiornamento di stato e <c>ExtractionTimers</c>. Un giro che fallisce lascia
    /// una riga e il ciclo prosegue dal giro dopo; solo l'annullamento lo fa uscire.
    /// <para>
    /// L'attesa ha il proprio <c>try</c> perche' e' il punto in cui l'annullamento si presenta
    /// normalmente, e non e' un errore da registrare.
    /// </para>
    /// <para>
    /// <b>Una riga per giro fallito era troppe.</b> <c>BuildRoutePath</c> gira ogni <b>10 ms</b>, e
    /// dopo la voce <c>D94</c> le righe di errore passano sempre e finiscono, tramite
    /// <c>EngineHostedService</c> e l'<c>ILogger</c> dell'host, nel registro eventi di Windows: un
    /// gestionale che non risponde ne avrebbe prodotte un centinaio al secondo, con lo stack trace
    /// dentro. Prima della riparazione il ciclo moriva dopo la prima riga, quindi il volume era uno:
    /// il difetto era peggiore, ma il rimedio ha aperto questo. Adesso il conteggio lo tiene
    /// <see cref="BackgroundLoopFailures"/>, uno per ciclo: la prima occorrenza passa, le ripetizioni
    /// dello <b>stesso</b> messaggio si contano e diventano un riepilogo ogni
    /// <paramref name="failureSummaryEvery"/>, un messaggio diverso passa subito, e quando il giro
    /// torna a riuscire lo dice.
    /// </para>
    /// </remarks>
    internal static Task BackgroundLoop(string name, Func<Task> body, Func<int> delayMilliseconds,
        CancellationToken cancellationToken, TimeSpan? failureSummaryEvery = null) =>
        Task.Run(async () =>
        {
            var failures = new BackgroundLoopFailures(name, failureSummaryEvery ?? TimeSpan.FromMinutes(1));

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await body();
                    failures.TurnWentThrough();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    failures.Report(ex);
                }

                try
                {
                    await Task.Delay(Math.Max(1, delayMilliseconds()), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            // Il ciclo si ferma: se stava fallendo, il conto di quanti giri non si perde.
            failures.WriteSummaryIfAny();
        }, cancellationToken);

    /// <summary>Vero finche' almeno uno dei cicli avviati da <see cref="LoadNavigatorData"/> gira.</summary>
    internal static bool BackgroundLoopsRunning => _backgroundLoops.Any(loop => !loop.IsCompleted);

    /// <summary>
    /// Ferma i cicli di sfondo avviati da <see cref="LoadNavigatorData"/> e ne aspetta la fine,
    /// lasciando aperti store e storico.
    /// </summary>
    /// <remarks>
    /// Serve ai test dell'host: una rotta che ricarica il diagramma passa da
    /// <see cref="LoadNavigatorData"/> e avvia i cicli anche con il motore non avviato, e nessuno
    /// li fermerebbe, perche' con <c>Wcs:StartEngine</c> falso l'host non chiama
    /// <see cref="Dispose"/>. Lasciati vivi, scrivono sullo store dei test per tutta la suite ed
    /// eseguono i comandi che i test creano. <see cref="Dispose"/> annulla senza aspettare e
    /// chiude anche i database: qui si aspetta, perche' chi chiama vuole la certezza che nessun
    /// giro sia ancora in corso quando il test successivo comincia.
    /// </remarks>
    internal static async Task StopBackgroundLoopsAsync()
    {
        var cts = _cancellationTokenSource;
        _cancellationTokenSource = null;
        if (cts != null)
        {
            try
            {
                await cts.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
                // Gia' rilasciata da Dispose: i cicli sono gia' fermi o si stanno fermando.
            }
        }

        try
        {
            await Task.WhenAll(_backgroundLoops);
        }
        catch (OperationCanceledException)
        {
            // Un ciclo annullato prima ancora di partire: e' fermo, che e' quello che si voleva.
        }

        _backgroundLoops = [];
        cts?.Dispose();
    }

    internal static async void LuUnloadedFromAsi(object sender, LuUnloadedFromAsiEventArgs? e)
    {
        if (e == null) return;

        var item = PathDevices.Instance[e.Data?.Asi, e.Data?.Subitem, e.Data?.Partition];

        if (item == null)
        {
            WriteLog(
                $"LuUnloadedFromAsi: dispositivo non trovato per Asi: {e.Data?.Asi}, Subitem: {e.Data?.Subitem}, Partition: {e.Data?.Partition}",
                LogEntryType.Warning);
            return;
        }

        try
        {
            await item.LuUnloadedFromAsi(StepTypes.Automatic);
        }
        catch (Exception ex)
        {
            WriteLog($"Error On LuUnloadedFromAsi Description: {ex.Message}\r\nStackTrace: {ex.StackTrace}",
                LogEntryType.Warning);
        }
    }

    internal static async void NewLuOnASI(object sender, NewLuOnAsiEventArgs? e)
    {
        if (e == null) return;

        var item = PathDevices.Instance[e.Data.Asi, e.Data.Subitem, e.Data.Partition];

        if (item == null)
        {
            WriteLog(
                $"NewLuOnASI: dispositivo non trovato per Asi: {e.Data.Asi}, Subitem: {e.Data.Subitem}, Partition: {e.Data.Partition}",
                LogEntryType.Warning);
            return;
        }

        try
        {
            await item.ConnectionNewLuOnASI(e);
        }
        catch (Exception ex)
        {
            WriteLog($"Error On NewLuOnAsi Description: {ex.Message}\r\nStackTrace: {ex.StackTrace}",
                LogEntryType.Warning);
        }
    }

    internal static void OpenDeviceConnections()
    {
        DeviceConnections.OpenConnection();
    }

    internal static void RecordNavigatorLog(AutomationDevice? sourceItem, string description, LogEntryType status,
        MissionStep? stepDone = null)
    {
        var newLog = new NavigatorLogRecord();

        if (sourceItem != null)
        {
            newLog.ASI = sourceItem.ASI;
            newLog.SUBITEM = sourceItem.SUBITEM;
            newLog.PARTITION = sourceItem.PARTITION;
            newLog.SourceItemId = sourceItem.ItemGuid;
        }
        else
        {
            newLog.ASI = "9999";
            newLog.SUBITEM = "0000";
            newLog.PARTITION = "0000";
            newLog.SourceItemId = Guid.Empty;
        }

        if (stepDone != null)
        {
            newLog.DestinationItemId = stepDone.ToNodeId;
            newLog.MissionId = stepDone.MissionId;
            newLog.StartMission = stepDone.StartStep;
            newLog.EndMission = stepDone.EndStep;
            newLog.StatusMission = stepDone.StepStatus;
            newLog.RunningItemId = stepDone.RunningNode;
        }
        else
        {
            newLog.DestinationItemId = Guid.Empty;
            newLog.MissionId = Guid.Empty;
            newLog.StartMission = default(DateTime);
            newLog.EndMission = default(DateTime);
            newLog.StatusMission = ResultCodeTypes.None;
            newLog.RunningItemId = Guid.Empty;
        }

        newLog.Status = (byte)status;

        newLog.Description = description;

        NavigatorLog.Add(newLog);
    }

    /// <summary>
    /// <see cref="Warehouse"/>, o un'eccezione chiara se il gestionale non e' configurato (compito
    /// 9): punto comune per i chiamanti diretti che non hanno una missione da cui partire (nessuna
    /// memoria per missione possibile), sullo stesso messaggio gia' usato da
    /// <c>MoveObstacleLu</c>/<c>RecoverPickUpQueue</c> (compito 7), scritto una volta sola invece che
    /// copiato ad ogni nuovo punto diretto.
    /// </summary>
    internal static IWarehouse RequireWarehouse(string context) =>
        Warehouse ?? throw new InvalidOperationException($"{context}: Navigator.Warehouse non e' impostato.");

    /// <summary>
    /// La decisione condivisa dalle due copie di "azzera lo stato delle operazioni su una cella"
    /// (compito 9, passo 1 del brief - <see cref="ResetCellOperationsStatus"/> qui sotto e
    /// <c>DbInfo.Private.ResetCellOperationsStatus</c>): libera se non e' occupata ne' per prelievo
    /// ne' per deposito. Estratta perche' le due copie restino sulla stessa regola invece di due
    /// copie incollate a mano che potrebbero scivolare l'una rispetto all'altra senza che nessuno se
    /// ne accorga.
    /// </summary>
    internal static bool ShouldClearCellOperations(CellBusyState busyState) => !busyState.PickUp && !busyState.Storing;

    /// <summary>
    /// Azzera lo stato delle operazioni sulla cella dell'unita' di carico <paramref name="luId"/>
    /// (compito 9, ex due letture dirette <c>IsCellBusyByPickUp</c>/<c>IsCellBusyByStoring</c>, ora
    /// <see cref="IWarehouse.GetCellBusyStateAsync"/>). <paramref name="luId"/> stesso e' l'unita' di
    /// carico da escludere dal conteggio: la missione che sta chiudendo non deve vedersi occupata
    /// dalla propria stessa unita' di carico, come gia' facevano le due letture originali.
    /// </summary>
    internal static async Task ResetCellOperationsStatus(decimal luId)
    {
        if (luId == 0)
            return;

        var idCella = await DbInfo.GetIdCellLocationByLuId(luId);

        var busyState = idCella > 0
            ? await RequireWarehouse($"ResetCellOperationsStatus sull'unita' di carico {luId}").GetCellBusyStateAsync(idCella, luId)
            : new CellBusyState(false, false, false);

        if (ShouldClearCellOperations(busyState))
            await DbInfo.SetOperationLockingCell(idCella, StatoOperazioneCella.Nessuna);
    }

    internal static void WriteAlert(PathMission value)
    {
        var commandToDo = new Command
        {
            CommandType = Command.CommandTypes.ItemExecuteStep,
            Date = DateTime.Now,
            ErrorDescription = value.StatusDescription,
            ItemCommandId = value.PositionItemId,
            LuId = value.LuId,
            Status = Command.StatusTypes.Error,
            WorkstationId = WorkstationId
        };

        // Non atteso perche' WriteAlert non e' asincrona, ma non scartato: con il solo "_ =" un
        // guasto della scrittura restava un'eccezione che nessuno osservava, e l'allarme spariva
        // in silenzio. Stessa forma dei gemelli in Command.SetProperty.
        _ = Task.Run(async () =>
        {
            try
            {
                await ORM.CommandToDo.Update(commandToDo);
            }
            catch (Exception ex)
            {
                WriteLog($"WriteAlert: scrittura dell'allarme fallita per l'unita' di carico {commandToDo.LuId}: {ex.Message}",
                    LogEntryType.Warning);
            }
        });
    }

    /// <summary>
    /// Un errore che deve lasciare un segno anche con la diagnostica spenta: al registro dell'host se
    /// c'e', altrimenti sull'errore standard, con l'eccezione intera e non il solo messaggio. Non
    /// solleva se il registro dell'host solleva a sua volta: <c>ILogger.Log</c> rilancia il guasto
    /// di un provider come <c>AggregateException</c>, e chi arriva qui sta segnalando proprio perche'
    /// non puo' sollevare. I due che ci arrivavano — l'onError della BusinessArea di WMS.Services e
    /// la traccia dell'area in processo — sono spariti con il taglio dei riferimenti ai progetti del
    /// gestionale: dentro il motore oggi non ha chiamanti, e chi ne apre uno nuovo trova qui la
    /// regola. Se la consegna fallisce restano sull'errore standard il guasto del registro e
    /// l'errore originale; l'errore standard stesso non e' protetto.
    /// </summary>
    internal static void ReportError(Exception ex)
    {
        try
        {
            if (HostErrorLog is { } log)
            {
                log(ex);
                return;
            }
        }
        catch (Exception failure)
        {
            // Il registro dell'host ha fallito: la segnalazione non puo' diventare il guasto, si
            // ripiega sull'errore standard come se il registro non ci fosse, e il perche' del
            // fallimento e' l'unica diagnosi di un registro che non riceve nulla.
            Console.Error.WriteLine(failure.ToString());
        }

        Console.Error.WriteLine(ex.ToString());
    }

    /// <summary>
    /// Il registro dello scrittore delle scritture di stato: una riga scartata definitivamente e'
    /// l'unico segno che una riga di <c>wcs.db</c> non c'e'.
    /// </summary>
    /// <remarks>
    /// Passa da <see cref="ReportError"/>, che e' il canale «errore che non puo' sollevare» e non
    /// dipende da <see cref="ActivateDebugLog"/>, oltre che da <see cref="WriteLog"/>. Il solo
    /// <c>WriteLog</c> non bastava: era di livello Avviso e taceva a diagnostica spenta (voce D94),
    /// e <c>Envelope.Completion</c> e' <c>null</c> per tutto cio' che entra da <c>Enqueue</c> o
    /// <c>EnqueueOrWait</c> — cioe' per tutte le scritture di stato ordinarie — quindi nessuno
    /// riceveva nemmeno l'eccezione. La riga spariva in silenzio.
    /// </remarks>
    internal static void ReportStateWriterProblem(string message, Exception? ex)
    {
        var line = ex == null ? $"StateWriter: {message}" : $"StateWriter: {message} {ex.Message}";

        WriteLog(line, LogEntryType.Warning);
        ReportError(ex ?? new InvalidOperationException(line));
    }

    /// <summary>
    /// Vero per i livelli che passano <b>sempre</b>, qualunque sia <see cref="ActivateDebugLog"/>.
    /// </summary>
    /// <remarks>
    /// Errore e Avviso sono segnalazioni, non diagnostica: se non arrivano da nessuna parte, il
    /// guasto non lascia traccia. Nel solo <c>Wcs.Engine/src</c> le chiamate a
    /// <see cref="WriteLog"/> di questi due livelli sono 97, e comprendono l'errore di ogni
    /// scrittura fallita su SQL Server, il rifiuto di <c>ExecuteStep</c>, lo scioglimento di un
    /// gettone appeso, e <c>AutomationDevice.LogError</c>, dove finisce ogni <c>catch</c> dei
    /// gestori — cioe' proprio la traccia con cui il guasto del 2026-09-09 si sarebbe capito in
    /// minuti invece che in un'ora e mezza.
    /// </remarks>
    private static bool IsAlwaysWritten(LogEntryType logType) =>
        logType is LogEntryType.Error or LogEntryType.Warning;

    /// <summary>
    /// La diagnostica del motore.
    /// </summary>
    /// <remarks>
    /// <b>Il flag governava insieme la verbosita' e la gravita', ed e' la ragione per cui non lo si
    /// poteva semplicemente accendere:</b> alzandolo si riaccendono anche le righe informative sui
    /// percorsi caldi, che e' il motivo per cui e' spento di serie
    /// (<c>Wcs.Host/src/appsettings.json</c>). I due canali si separano qui: Errore e Avviso passano
    /// sempre, Informazione e il resto restano sotto <see cref="ActivateDebugLog"/>. E' la voce
    /// <b>D94</b> del registro, ed e' una scelta: da adesso in produzione la console e il registro
    /// dell'host ricevono le segnalazioni di errore e di avviso del motore.
    /// <para>
    /// La scrittura sul registro e' lanciata e non attesa, come prima, ma con il proprio
    /// <c>catch</c>: un registro che si rompe non deve diventare il guasto, e senza quella cattura
    /// l'eccezione viveva in un <see cref="Task"/> che nessuno guarda.
    /// </para>
    /// </remarks>
    internal static void WriteLog(string description, LogEntryType logType)
    {
        // La guardia esce prima di entrare nel try: cosi' la Console.WriteLine nel finally non
        // gira piu' anche a diagnostica spenta.
        if (!ActivateDebugLog && !IsAlwaysWritten(logType)) return;

        try
        {
            Task.Run(() =>
            {
                try
                {
                    _eventLogWriter?.WriteLog(description, logType);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(ex.ToString());
                }
            });
        }
        finally
        {
            Console.WriteLine(description);
        }
    }

    #endregion Internal Methods

    #region Private Methods

    /// <summary>
    /// Serve le testate di estrazione per unita' di carico che il gestionale ha gia' scelto
    /// (compito 15, ex <c>AssignLUbyLuExtractions</c> e la sua gemella <c>AssignLuByLuExtractions</c>,
    /// che differivano per una sola maiuscola). Qui il motore non assegnava niente nemmeno prima:
    /// l'elenco delle unita' di carico da estrarre gli arrivava gia' fatto. Quello che decideva - e
    /// che ora non decide piu' - era quali lavorazioni processare e quando chiuderle; quello che
    /// scriveva in questo ciclo - e che ora non scrive piu' - erano gli stati su
    /// <c>DISTINTA_ESTRAZIONI_UDC</c>. Restano suoi la risoluzione del movimento e la costruzione
    /// dei comandi.
    /// <para>
    /// <b>Compito 12:</b> nemmeno l'altra fase, quella che chiude il movimento, scrive piu' quegli
    /// stati. Era <c>DbInfo.UpdateStatusExtractionLu</c>, che li scriveva e poi chiudeva la
    /// lavorazione quando il residuo andava a zero; adesso e' la stessa segnalazione di qui
    /// (<c>DbInfo.ReportExtractionRowOutcome</c>, con la testata a zero perche' quei punti
    /// conoscono la riga e non la testata) e la chiusura la trae il gestionale. Su
    /// <c>DISTINTA_ESTRAZIONI_UDC</c> il motore non scrive piu' da nessuna parte: la voce
    /// <c>D120</c> del registro in <c>WCS/doc/debito-tecnico.md</c> e' da chiudere (compito 17).
    /// </para>
    /// </summary>
    /// <remarks>
    /// <b>Il guasto su una testata non porta via le seguenti.</b> Gli stati li scriveva il motore
    /// stesso, sul database del gestionale, e l'errore di una scrittura veniva inghiottito: il giro
    /// proseguiva. Ora la scrittura e' una richiesta al confine, e senza questa cattura la prima
    /// testata che non riesce a riferire fermerebbe tutte le altre dello stesso giro. Le righe
    /// rimaste senza esito tornano fra quelle da servire al giro dopo.
    /// </remarks>
    /// <param name="work">Le testate pronte lette dal gestionale, con le loro righe gia' scelte.</param>
    internal static async Task ServeExtractionWork(IReadOnlyList<PendingExtraction> work)
    {
        if (work.Count == 0) return;

        var warehouse = RequireWarehouse("ServeExtractionWork");

        foreach (var extraction in work)
        {
            var outcomes = await ServeExtraction(warehouse, extraction);

            try
            {
                // Anche con l'elenco vuoto: e' quello che fa chiudere una lavorazione le cui righe
                // sono tutte gia' risolte, la porta che nel motore era il controllo del residuo dopo
                // ogni testata, che girava anche quando non era stata scritta nessuna riga.
                await warehouse.ReportExtractionOutcomesAsync(
                    new ReportExtractionOutcomesRequest(extraction.ExtractionHeaderId, WorkstationId, outcomes));
            }
            catch (WarehouseUnavailableException ex)
            {
                WriteLog(
                    $"ServeExtractionWork: gestionale non disponibile mentre riferiva gli esiti della testata {extraction.ExtractionHeaderId}: {ex.Message}",
                    LogEntryType.Warning);
            }
        }
    }

    /// <summary>
    /// Che cosa succede alle righe di una testata: per ognuna si cerca l'unita' di carico nel vivo
    /// del diagramma e, se e' servibile, si costruisce il comando. L'esito di ogni riga e' uno dei
    /// quattro stati di <see cref="ExtractionRowStatus"/>, e vale sia per la riga che diventa un
    /// comando sia per quella che era gia' quella in estrazione: nel motore la variabile nasceva
    /// <c>byte locked = 0</c> e lo zero <b>e' anche</b> il valore di <c>InExtraction</c>, quindi una
    /// riga che attraversava il ciclo senza incontrare nessuno dei rami espliciti veniva scritta lo
    /// stesso - non e' un caso limite, e' il caso normale della riga gia' in lavorazione.
    /// </summary>
    /// <remarks>
    /// I due percorsi che <b>non</b> producono un esito - la guida dell'impianto che non si risolve e
    /// il punto di origine o di destinazione mancante - erano due <c>continue</c> prima della
    /// scrittura, e restano tali: non scrivere non e' lo stesso che scrivere zero, perche' uno zero
    /// terrebbe aperta la lavorazione mentre l'assenza di esito la lascia chiudibile.
    /// <para>
    /// A quei due se ne aggiunge un terzo, che qui non conserva un comportamento ma ne nomina uno:
    /// la coda delle scritture che rifiuta il comando appena costruito
    /// (<see cref="QueuedWriteRejectedException"/>). Un esito, qualunque, consumerebbe una riga per
    /// cui nessun comando esiste; senza esito la riga torna candidata al giro dopo.
    /// </para>
    /// </remarks>
    private static async Task<List<ExtractionRowOutcome>> ServeExtraction(IWarehouse warehouse, PendingExtraction extraction)
    {
        var outcomes = new List<ExtractionRowOutcome>();

        foreach (var row in extraction.Rows)
        {
            var status = ExtractionRowStatus.InExtraction;
            var luId = row.LoadUnitId;

            if (row.LoadUnitLocked)
            {
                status = ExtractionRowStatus.LoadUnitLocked;
            }
            else
            {
                try
                {
                    var luPosition = await warehouse.FindLoadUnitAsync(new FindLoadUnitRequest(
                        luId, extraction.BufferAreaIds, null, LoadUnitLockFilter.Unlocked, CellLockFilter.FreeForUnloading));

                    if (luPosition != null)
                    {
                        var unloadingDoorCode = await warehouse.ChoosePickUpDoorAsync(luPosition.LoadUnitId, luPosition.CellId);

                        var itemSource = await DbInfo.GetItemId(unloadingDoorCode.DoorCode);
                        var itemDestination = await DbInfo.GetItemId(extraction.PickingCellId);

                        if (itemSource == Guid.Empty || itemDestination == Guid.Empty)
                        {
                            WriteLog(
                                "Error On Navigator->ServeExtraction No Guid Item on Source or Destination",
                                LogEntryType.Warning);
                            continue;
                        }

                        var automationItem = PathDevices.Instance[itemSource];

                        if (automationItem == null) continue;

                        if (automationItem.DeviceType == DeviceTypes.Storage)
                            automationItem = PathDevices.Instance[automationItem.RelocateItemId];

                        if (automationItem == null) continue;

                        var queuedRowId = await DbInfo.GetExtractionLuRowId(luId);

                        if (queuedRowId == 0)
                        {
                            status = ExtractionRowStatus.InExtraction;

                            switch (automationItem.DeviceType)
                            {
                                case DeviceTypes.GroupOfAutomationDevices:
                                    {
                                        await ExecutableCommands.BuildGroupedMoveLu(luId, extraction.PickingCellId,
                                            itemSource, itemDestination, luPosition.CellId, unloadingDoorCode, WorkstationId,
                                            row.ExtractionRowId, processingId: row.ProcessingId);
                                        break;
                                    }

                                case DeviceTypes.Automation:
                                case DeviceTypes.Loading:
                                case DeviceTypes.Buffer:
                                case DeviceTypes.Storage:
                                case DeviceTypes.CircularDistributor:
                                case DeviceTypes.LoadingAndBuffer:
                                case DeviceTypes.None:
                                default:
                                    {
                                        await ExecutableCommands.BuildSingleMoveLu(luId, extraction.PickingCellId,
                                            itemSource, itemDestination, luPosition.CellId, unloadingDoorCode, WorkstationId,
                                            row.ExtractionRowId, processingId: row.ProcessingId);
                                        break;
                                    }
                            }
                        }
                        else if (queuedRowId != row.ExtractionRowId)
                        {
                            status = ExtractionRowStatus.InOtherExtraction;
                        }
                    }
                    else
                    {
                        status = ExtractionRowStatus.NotReachable;
                    }
                }
                catch (QueuedWriteRejectedException ex)
                {
                    // La coda delle scritture ha rifiutato il comando appena costruito: lo scrittore
                    // si sta fermando, oppure la coda e' piena. Sono due condizioni legittime e
                    // transitorie, non difetti, e la riga non riceve esito - ne' NotReachable, che
                    // direbbe una cosa falsa (nessun comando e' stato costruito) e la consumerebbe,
                    // ne' InExtraction, che direbbe che il comando c'e'. Senza esito il gestionale
                    // la ripropone al giro dopo, quando lo scrittore avra' ripreso. Le altre righe
                    // della testata proseguono, e gli esiti gia' calcolati vengono riferiti lo
                    // stesso: e' per questo che si registra e si continua invece di lasciar risalire.
                    WriteLog(
                        $"ServeExtraction: scrittura rifiutata sull'unita' di carico {luId}, la riga resta da servire: {ex.Message}",
                        LogEntryType.Warning);
                    continue;
                }
                catch (WarehouseUnavailableException ex)
                {
                    // Qui la regola generale "niente zeri silenziosi" (RequireWarehouse, compito 9)
                    // non vale: NotReachable e' un valore esplicito, usato in tutto il motore per
                    // dire "non risolvibile ora", ed era l'unico dei sette punti di chiamata di
                    // RequireWarehouse con una degradazione riga per riga simile - un progetto
                    // deliberato, non un buco (revisione compito 11, critico 1). Con l'eccezione che
                    // risale, un'indisponibilita' di pochi secondi del gestionale fermerebbe
                    // l'intera testata, comprese le righe che dal gestionale non dipendono: si
                    // registra e si passa alla riga successiva, marcata NotReachable come faceva
                    // l'originale.
                    WriteLog(
                        $"ServeExtraction: gestionale non disponibile sull'unita' di carico {luId}: {ex.Message}",
                        LogEntryType.Warning);
                    status = ExtractionRowStatus.NotReachable;
                }
            }

            outcomes.Add(new ExtractionRowOutcome(row.ExtractionRowId, status));
        }

        return outcomes;
    }

    private static async Task AssignmentsProcess()
    {
        try
        {
            if (NavStatus != NavigatorStatusTypes.Run) return;

            // Le due pulizie della coda, una per famiglia: quella delle note di spedizione e quella
            // delle estrazioni per unita' di carico, che prima girava in testa ad
            // AssignLUbyLuExtractions - stesso punto del giro, ora chiamata da qui perche' il ciclo
            // che la ospitava non c'e' piu'. Cancellano righe della coda del WCS, che vive nel
            // database del WCS: dal gestionale e' passata solo la loro parte che scriveva sui dati
            // di la' (le distinte delle lavorazioni sospese, ora in testa a
            // GetPendingExtractionWorkAsync).
            // Stanno in questo processo e non in ExtractionProcess perche' non devono dipendere dal
            // semaforo dell'estrazione: girano a ogni giro qualunque cosa succeda al lucchetto, e
            // soprattutto non lo tengono mentre chiedono al gestionale il codice di ogni UDC che
            // cancellano - una richiesta di rete per riga, dentro un semaforo da uno che otto punti
            // del motore si contendono con trenta secondi di attesa. Un loro errore ferma questo
            // processo per il giro, non la costruzione della coda, che sta nel proprio.
            await DbInfo.DeleteLuInExtranctionByPausedNote();
            await DbInfo.DeleteLuInExtranctionByPausedExtractionLu();

            await ServeExtractionWork(
                await RequireWarehouse("GetPendingExtractionWork").GetPendingExtractionWorkAsync());
        }
        catch (Exception ex)
        {
            WriteLog(
                $"Error during the ProcessoAssegnazioni process, Description: {ex.Message}\r\nStackTrace: {ex.StackTrace}",
                LogEntryType.Error);
        }
    }

    /// <summary>
    /// Mette in coda di estrazione le assegnazioni delle note di spedizione non ancora accodate.
    /// </summary>
    /// <remarks>
    /// E' la prima delle due porte d'ingresso dell'estrazione; la seconda e' il comando in
    /// <c>COMMAND_TODO</c>, e le due convergono qui sulla stessa risoluzione e sugli stessi
    /// costruttori. Da compito 14 le righe arrivano da <see cref="IWarehouse.GetPendingNoteWorkAsync"/>
    /// gia' assegnate e gia' validate contro le tre cose che solo il WCS sa (missione, coda di
    /// un'altra estrazione, polmone senza aree servite: i tre motivi di rifiuto sono migrati sul
    /// gestionale con l'orchestrazione). A leggerle non e' piu' questo metodo: le legge
    /// <see cref="ExtractionProcess"/> prima di prendere il semaforo dell'estrazione, e le passa qui
    /// in <paramref name="noteWork"/>. Restano qui, sul vivo del motore, il filtro sulle
    /// assegnazioni gia' accodate (prima lo faceva <c>DbInfo.GetNoteAssignmentsToQueue</c> leggendo
    /// la coda del WCS, che il gestionale non vede) e la ricerca della UDC: viene <b>cercata</b> nel
    /// magazzino fra le aree servite dal polmone (<c>TrovaUdc</c>, solo UDC non bloccate in celle
    /// scaricabili), se ne calcolano porta e ostacoli (<c>PickUpDoor</c>), e la scrittura in coda e'
    /// affidata ai costruttori di <see cref="ExecutableCommands"/>: sono loro a chiedere al WMS le
    /// UDC davanti e a scrivere prima una riga di ricollocamento per ognuna (<c>ESTRARRE = 0</c>) e
    /// poi quella della UDC voluta. Una UDC che non si trova fa cancellare l'assegnazione, con un
    /// log, cosi' il WMS puo' assegnarne un'altra.
    /// <para>
    /// <b>Che cosa si rilegge sotto il lucchetto, e che cosa no.</b> Fra la lettura di
    /// <see cref="ExtractionProcess"/> e questo punto puo' esserci passata l'attesa del semaforo,
    /// fino ai trenta secondi di <see cref="SyncExtractionTimeout"/>. Si rileggono qui dentro: le
    /// assegnazioni gia' in coda, la posizione della UDC e la sua prendibilita'
    /// (<see cref="IWarehouse.FindLoadUnitAsync"/>), la porta e gli ostacoli, il diagramma; e al
    /// punto di scrittura i costruttori di <see cref="ExecutableCommands"/> ricontrollano i primi
    /// due dei tre rifiuti del gestionale, con <c>IsLuInExtraction</c> - lo stesso insieme di
    /// <c>MissionQueries.HoldsLoadUnit</c> - e con <c>DbInfo.IsLuPresentInExtranctionLu</c>.
    /// </para>
    /// <para>
    /// <b>Non</b> si rileggono, e sono stantii quanto la finestra:
    /// <list type="number">
    /// <item><description>
    /// <see cref="PendingNote.BufferAreaIds"/>: non e' un identificativo ma il risultato di una
    /// lettura di <c>AREE_PRELIEVO_PICKING</c> fatta dal gestionale al momento della risposta
    /// (<c>MagazzinoPerWcs.AreePrelievoPickingAsync</c>), e qui diventa il <b>filtro</b> della
    /// ricerca. Un elenco stantio produce un rifiuto spurio - la UDC non si trova, l'assegnazione
    /// viene cancellata con il suo motivo nel registro e il gestionale ne assegna un'altra al giro
    /// dopo - oppure lascia passare un polmone che nel frattempo non serve piu' quell'area, ed e' il
    /// terzo rifiuto del gestionale a essere deciso sulla stessa lettura stantia.
    /// </description></item>
    /// <item><description>
    /// La validita' stessa dell'assegnazione: che la riga esista ancora, che sia sbloccata, non
    /// prelevata, e che la lavorazione sia ancora in corso. Le quattro condizioni stanno solo nella
    /// SELECT del gestionale (<c>AssegnazioniProntePerCodaSql</c>), fuori dal lucchetto.
    /// </description></item>
    /// <item><description>
    /// Gli altri campi della riga, che entrano in coda come sono arrivati: la UDC, il polmone, la
    /// destinazione, la postazione e - da compito 9 - la lavorazione della nota. Del terzo si
    /// ricontrolla solo la <b>presenza nel diagramma</b>, mai il valore: se il gestionale ha nel frattempo riscritto l'assegnazione
    /// (<c>NoteMagazzino</c> ne cambia <c>ID_UDC</c>, <c>ID_POSTAZIONE</c>), si accoda quello di
    /// prima.
    /// </description></item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Perche' e' accettato, e che cosa recupera.</b> L'esito peggiore e' mite: un giro perso o un
    /// rifiuto spurio, non un movimento sbagliato - le due guardie fisiche (missione e coda) si
    /// rileggono. Il recupero, per nome: la lavorazione che non e' piu' in corso la prende
    /// <c>DbInfo.DeleteLuInExtranctionByPausedNote</c>, chiamata a ogni giro da
    /// <see cref="AssignmentsProcess"/>, che toglie dalla coda la riga e cancella l'assegnazione.
    /// <b>Non</b> copre pero' l'assegnazione <b>cancellata</b> nel frattempo (dall'operatore in
    /// <c>GestNOTE_REFERENZA</c>, o dal prelievo in <c>NoteMagazzino</c>): quella pulizia guarda lo
    /// stato della lavorazione, non l'esistenza dell'assegnazione, e la riga di coda resta orfana
    /// finche' non la consuma il motore. Quanto al blocco: <c>BLOCCATO</c> oggi viaggia solo da 1 a 0
    /// (<c>NoteMagazzino.AssegnaRiferimentiMagazzinoAutomatico</c> lo azzera a fine ciclo), quindi
    /// una riga letta sbloccata non torna bloccata sotto di noi.
    /// </para>
    /// </remarks>
    /// <param name="noteWork">Le righe pronte lette dal gestionale, gia' assegnate e gia' validate.</param>
    internal static async Task BuildLuExtraction(IReadOnlyList<PendingNote> noteWork)
    {
        try
        {
            // Seconda richiesta del giro: la prima la fa ExtractionProcess, e la fa con questo
            // stesso nome perche' la riga di registro di un guasto resti quella che scriveva questo
            // metodo quando la lettura stava qui.
            var warehouse = RequireWarehouse("BuildLuExtraction");

            if (noteWork.Count == 0) return;

            // Le assegnazioni gia' in coda si tolgono qui: la coda sta nel database del WCS, il
            // gestionale non la vede e continuerebbe a riproporle a ogni giro.
            var queued = await ExtractionQueue.ReadAsync(Persistence.ExtractionQueueStore.QueuedAssignmentIdsAsync, new List<decimal>());
            var alreadyQueued = queued.Count > 0 ? queued.ToHashSet() : [];

            var accodate = 0;

            foreach (var riga in noteWork)
            {
                if (alreadyQueued.Contains(riga.AssignmentId)) continue;

                var loadUnitId = riga.LoadUnitId;
                var assignmentId = riga.AssignmentId;
                var bufferCellId = riga.BufferCellId;
                var bufferAreaIds = riga.BufferAreaIds;

                // Dov'e' la UDC ORA, e se si puo' prendere: solo non bloccata, in una cella scaricabile.
                var cella = await warehouse.FindLoadUnitAsync(new FindLoadUnitRequest(
                    loadUnitId, bufferAreaIds, null, LoadUnitLockFilter.Unlocked, CellLockFilter.FreeForUnloading));

                if (cella == null)
                {
                    // Il quarto motivo di rifiuto: il fatto lo scopre il motore, che e' l'unico a
                    // saper cercare la UDC nel vivo, ma la conseguenza la esegue il gestionale -
                    // cancella l'assegnazione e ne registra il motivo dove finiscono gli altri tre.
                    // Le assegnazioni le fa e le disfa lui.
                    await warehouse.RejectNoteAssignmentAsync(new RejectNoteAssignmentRequest(
                        assignmentId, loadUnitId, "la UDC non e' disponibile nelle aree servite dal polmone"));
                    continue;
                }

                var porta = await warehouse.ChoosePickUpDoorAsync(cella.LoadUnitId, cella.CellId);
                var idSorgente = await DbInfo.GetItemId(porta.DoorCode);

                var idDestinazione = riga.DestinationItemId ?? Guid.Empty;
                if (idDestinazione == Guid.Empty)
                {
                    // Compito 16: ex CelleMagazzino.DecodificaCella, di cui si leggeva la sola porta
                    // A. Il metodo vecchio rispondeva un risultato annullabile e questo chiamante
                    // scriveva ?.CODICE_PORTA_A: senza cella polmone la porta restava nulla e
                    // GetItemId rispondeva Guid.Empty. DescribeCellAsync invece risponde un valore
                    // non annullabile e tratta l'assenza come guasto, quindi la tolleranza va
                    // riscritta esplicitamente - zero e' un dato legittimo (nessuna cella polmone),
                    // non un difetto, come gia' in MoveObstacleLu e RecoverPickUpQueue.
                    var bufferDoorCodeA = bufferCellId == 0
                        ? null
                        : (await warehouse.DescribeCellAsync(bufferCellId)).DoorCodeA;

                    idDestinazione = await DbInfo.GetItemId(bufferDoorCodeA);
                }

                var sorgente = PathDevices.Instance[idSorgente];
                var ricollocatore = sorgente == null ? null :
                    sorgente.DeviceType == DeviceTypes.Storage ? PathDevices.Instance[sorgente.RelocateItemId] : sorgente;

                if (ricollocatore == null || PathDevices.Instance[idDestinazione] == null)
                {
                    // Non e' un difetto dell'assegnazione ma del diagramma: l'assegnazione resta e
                    // verra' ripresa quando il diagramma avra' i dispositivi. Ma va detto.
                    WriteLog(
                        $"BuildLUExtraction: l'assegnazione {assignmentId} della UDC {loadUnitId} indica la porta {porta.DoorCode} e la destinazione {idDestinazione}, ma sorgente, ricollocatore o destinazione non sono nel diagramma: resta in attesa",
                        LogEntryType.Warning);
                    continue;
                }

                var postazione = riga.WorkstationId;
                var guidAssegnazione = riga.AssignmentGuid;

                // Compito 9: la lavorazione della nota entra in coda con la riga, insieme
                // all'assegnazione che la porta. Prima il motore la rileggeva da se' dal gestionale a
                // ogni giro di ogni ricollocatore, per costruire le missioni.
                if (ricollocatore.DeviceType == DeviceTypes.GroupOfAutomationDevices)
                    await ExecutableCommands.BuildGroupedMoveLu(loadUnitId, bufferCellId, idSorgente, idDestinazione, cella.CellId, porta, postazione, 0,
                        assignmentId, guidAssegnazione, noteProcessingId: riga.ProcessingId);
                else
                    await ExecutableCommands.BuildSingleMoveLu(loadUnitId, bufferCellId, idSorgente, idDestinazione, cella.CellId, porta, postazione, 0,
                        assignmentId, guidAssegnazione, noteProcessingId: riga.ProcessingId);

                // Conta le righe tentate, non quelle finite in coda: i due costruttori tornano senza
                // scrivere quando le loro guardie scattano (IsLuInExtraction, o la riga della UDC
                // gia' presente, che aggiornano invece di aggiungere). E' cosi' da prima del compito
                // 14 e resta cosi': gli operatori leggono questa riga di registro. La finestra piu'
                // larga fra la lettura del gestionale e questo punto rende lo scarto piu' frequente.
                accodate++;
            }

            if (accodate > 0)
                WriteLog($"Extracts N° {accodate} LU assigned", LogEntryType.Information);
        }
        catch (Exception ex)
        {
            WriteLog($"Error On Navigator->BuildLUExtraction Description: {ex.Message}\r\nStackTrace: {ex.StackTrace}",
                LogEntryType.Warning);
        }
    }

    /// <summary>
    /// Costruisce l'impianto in memoria: i dispositivi da <b>tutte</b> le righe di
    /// <c>PlantItems</c>, poi le giunzioni e i loro estremi.
    /// </summary>
    /// <remarks>
    /// Voce D161. Prima i dispositivi nascevano dai soli estremi delle giunzioni e
    /// <c>ORM.AutomationDevice.ClearUnUsed</c> cancellava dallo store ogni riga che non era in
    /// memoria: un dispositivo configurato e non ancora collegato perdeva la configurazione al primo
    /// caricamento, in silenzio, e l'Editor lo ricreava vuoto. Il caricamento non cancella nulla:
    /// la cancellazione resta alla sola via che la fa confermare all'operatore
    /// (<c>POST /plant/items/keep</c>, <c>PlantQueries.KeepItemsAsync</c>). Gli estremi di
    /// giunzione senza riga continuano a nascere come prima (<c>AutomationDevice.Load</c> scrive
    /// la riga con i valori correnti).
    /// </remarks>
    internal static async Task BuildNavigatorItemsJunctions()
    {
        if (Store is { } store)
        {
            List<Guid> stored;
            await using (var context = store.CreateContext())
                stored = await PlantStore.LoadItemIdsAsync(context);

            foreach (var itemId in stored)
                PathDevices.Instance.Add(itemId);
        }

        await DbInfo.LoadJunctions();

        foreach (var item in PathJunctions.Instance.List.OfType<AutomationJunction>())
        {
            PathDevices.Instance.Add(item.SourceId);
            PathDevices.Instance.Add(item.DestinationId);
        }
    }

    private static async Task BuildRoutePath()
    {
        // La guardia esce prima di entrare nel try: cosi' il finally che la rilascia gira solo
        // quando questa chiamata l'ha davvero presa, non anche quando trova gia' alzato il flag.
        if (NavStatus != NavigatorStatusTypes.Run || _isBuildingPath)
            return;

        // Le celle rifiutate con il 67 che il gestionale muto non ha segnato piene: a ogni giro del
        // freno, finche' risponde (risposta 3 del proprietario del 9/10). Parte in sottofondo e il giro
        // non l'aspetta; il freno e' suo (CellsToMarkFull.Retries).
        CellsToMarkFull.Retry();

        _isBuildingPath = true;

        PathMission? mission = null;

        try
        {
            mission = RoutesToBuild.Instance.Dequeue();

            // Una missione uscita dal motore (ActiveMissions.Remove, o una chiusa) non si costruisce
            // piu': esce dalla coda senza tornarci, e il freno dimentica i suoi fallimenti. Fino alla
            // revisione finale della voce 39 girava per sempre, contando fallimenti e scrivendo l'avviso
            // del decimo giro senza posizione, o chiedendo al gestionale la sua cella a ogni giro del
            // freno: chi la rimette in coda dopo l'uscita (la fine di una ricerca in volo, un deposito
            // che aspetta la conferma) non lo sa.
            if (mission != null && !ReferenceEquals(ActiveMissions.Instance[mission.MissionId], mission))
            {
                RoutePathRetries.Forget(mission.MissionId);
                return;
            }

            if (mission != null && RoutePathRetries.IsWaiting(mission.MissionId))
            {
                // Non e' ancora il momento di ritentare: si riaccoda senza rifare il lavoro di
                // costruzione, cosi' l'attesa crescente ha effetto anche quando la missione
                // bloccata e' l'unica in coda.
                RoutesToBuild.Instance.Enqueue(mission);
                return;
            }

            var positionItem = mission?.PositionItem;

            if (positionItem == null)
            {
                if (!string.IsNullOrEmpty(mission?.PickUpDoorCode) && mission.RelocateItemId == Guid.Empty)
                {
                    mission.SourceItemId = await DbInfo.GetItemId(mission.PickUpDoorCode);
                    mission.RelocateItemId =
                        mission.SourceItem?.RelocateItemId ?? Guid.Empty;
                }
                else
                {
                    // La missione non e' ancora costruibile: torna in coda invece di sparire.
                    if (mission != null)
                        RiaccodaConAttesaCrescente(mission,
                            "nessun PositionItem e nessun PickUpDoorCode utilizzabile");

                    return;
                }
            }

            // (Il conteggio dei tentativi non si dimentica prima della ricerca: lo dimenticano la
            // ricerca che riesce, lo spostamento o il cambio di destinazione dell'operatore, e l'uscita
            // dal motore. Fino alla voce 39 qui si dimenticava prima di ogni ricerca, e il freno non
            // cresceva mai. Il blocco commentato qui sotto e' piu' vecchio, e non c'entra.)

            //else if (!positionItem.DestinationItemsLinked.Any(x =>
            //             x is { LUsLoaded.Count: 0, Status: DeviceStatusTypes.Ready }))
            //{
            //    if (RoutesToBuild.Instance.Items.All(x =>
            //            mission != null && x != null &&
            //            x.MissionId != mission.MissionId))
            //        RoutesToBuild.Instance.Items.Enqueue(mission);

            //    return Task.CompletedTask;
            //}

            // Con uno step in volo la rotta non si tocca: la missione torna in coda e viene ripresa
            // al giro dopo, quando il movimento sara' finito. E' l'unica via per cui una missione
            // in movimento arriva qui -- il ramo BuildRouteToStoreForcing la accoda apposta con lo
            // step presente. Decisione del 2026-09-03.
            //
            // L'attesa e' quella fissa di un passo, com'era prima della voce 39, e non conta un
            // fallimento: la missione aspetta uno step, non fallisce. Il freno cresce solo per le
            // ricerche fallite (scelta tecnica della specifica, 2/10); con il conto che cresceva
            // anche qui, la ricerca dopo lo step partiva fino a 5 s dopo la sua fine, e uno step
            // lungo scriveva l'avviso del decimo giro.
            if (mission != null && mission.StepInFlight() is not null)
            {
                RoutePathRetries.WaitOneStep(mission.MissionId);
                RoutesToBuild.Instance.Enqueue(mission);
                return;
            }

            // La ricerca della cella parte fuori dal giro, che torna subito: prima la attendeva
            // qui, e con il gestionale lento (tetto 30 s) la costruzione delle rotte di tutto
            // l'impianto scendeva a due missioni al minuto. Voce D160.
            if (mission != null && positionItem != null)
            {
                StartCellSearch(mission, positionItem);
                return;
            }

            if (mission != null)
                RiaccodaConAttesaCrescente(mission, "nessuna posizione di deposito trovata");
        }
        catch (Exception ex)
        {
            WriteLog(
                $"Error On NAVIGATOR->RouteBuilder_Elapsed Description: {ex.Message}\r\nStackTrace: {ex.StackTrace}",
                LogEntryType.Warning);
            if (mission != null)
                RiaccodaConAttesaCrescente(mission, "eccezione durante la costruzione del percorso");
        }
        finally
        {
            _isBuildingPath = false;
        }
    }

    /// <summary>
    /// B70: riaccoda la missione con un'attesa crescente invece di ritentarla subito, cosi' una
    /// missione irrisolvibile non fa girare una ricerca di percorso completa ad ogni giro da
    /// 10 ms. Il conteggio dei fallimenti che il freno restituisce e' anche la soglia di log.
    /// Era una funzione locale di <see cref="BuildRoutePath"/>; la continuazione della ricerca
    /// della cella (<see cref="StartCellSearch"/>) la chiama da fuori.
    /// </summary>
    /// <param name="alreadyQueued">
    /// La missione e' gia' in coda per chi chiama (<see cref="CountALostCellSearch"/>): il fallimento si
    /// conta, e non si accoda. Senza, la missione si accoda solo se non c'e' gia'.
    /// </param>
    /// <remarks>
    /// L'avviso conta i tentativi falliti, non i giri del ciclo: fra un tentativo e l'altro il freno
    /// lascia passare molti giri. Fino alla revisione finale del primo giro della voce 39 (C14) diceva
    /// «da N giri».
    /// </remarks>
    private static void RiaccodaConAttesaCrescente(PathMission m, string motivo, bool alreadyQueued = false)
    {
        var tentativi = RoutePathRetries.RegisterFailure(m.MissionId);

        if (tentativi == 10 || tentativi % 500 == 0)
            RecordNavigatorLog(null,
                $"BuildRoutePath: la missione {m.MissionId} non risulta costruibile dopo {tentativi} tentativi falliti ({motivo}) e continua a essere riaccodata",
                LogEntryType.Warning);

        // Lo sguardo alla coda e l'accodamento insieme (RoutesToBuild.EnqueueIfAbsent): chi l'ha rimessa
        // in coda intanto (un dispositivo con la sua UDC caricata, chi ha perso il confronto della
        // versione) non la fa comparire due volte. Prima due chiamanti guardavano l'indice e poi
        // chiamavano qui (giro finale delle correzioni della voce 41).
        if (!alreadyQueued)
            RoutesToBuild.Instance.EnqueueIfAbsent(m);
    }

    /// <summary>
    /// La ricerca della cella di una missione ha perso la rotta contro un altro scrittore
    /// (<c>PathMission.GiveBackTheCellOfALostSearchAsync</c>): il fallimento conta nel freno, come una
    /// ricerca che non trova, e la missione, gia' di nuovo in coda, non si accoda un'altra volta (risposta
    /// 6 del proprietario dell'11/10). Una missione che non e' piu' fra le attive non si conta: il freno
    /// l'ha dimenticata all'uscita, e il conto resterebbe li' per la vita del processo.
    /// </summary>
    internal static void CountALostCellSearch(PathMission m)
    {
        if (!ReferenceEquals(ActiveMissions.Instance[m.MissionId], m))
            return;

        RiaccodaConAttesaCrescente(m, "la rotta verso la cella trovata l'ha cambiata un altro", alreadyQueued: true);
    }

    /// <summary>
    /// Avvia la ricerca della cella di deposito di una missione come lavoro osservato, fuori dal
    /// giro di <see cref="BuildRoutePath"/> (voce D160): il giro non aspetta piu' il gestionale, e
    /// missioni diverse possono cercare insieme, come gia' fanno le ricerche avviate dai percorsi
    /// dei dispositivi (<c>AutomationDevice.VerifyWhereAllocateLu</c>). Una sola ricerca per
    /// missione: se ce n'e' gia' una in volo la missione torna in coda e basta, senza contare un
    /// fallimento, perche' non e' lei a non essere costruibile, e' il gestionale che non ha
    /// ancora risposto. Alla fine la missione esce dall'insieme in ogni caso; se la ricerca non ha
    /// trovato niente viene riaccodata con l'attesa crescente, che cresce di ricerca in ricerca; se
    /// ha trovato il freno dimentica i suoi fallimenti e si va avanti come faceva il giro
    /// (<c>ExecuteStep</c> se <c>AutoExecuteStep</c>). Il <c>catch</c> sta dentro
    /// la lambda, come in <c>PathMission.PositionWork</c>: fuori, l'eccezione vivrebbe in un
    /// compito che nessuno osserva.
    /// </summary>
    /// <param name="mission">La missione che cerca la cella</param>
    /// <param name="positionItem">Il dispositivo su cui sta, che dice dove cercare</param>
    private static void StartCellSearch(PathMission mission, AutomationDevice positionItem)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!CellSearchesInFlight.TryAdd(mission.MissionId, finished.Task))
        {
            RoutesToBuild.Instance.Enqueue(mission);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                // Prima quello che un altro ha lasciato al giro, con lo step che lo bloccava finito: il
                // rigetto dal campo rimandato, la conversione di un PickUpAndStoreAgain arrivato, la rotta
                // di un rigetto che l'ha persa contro uno spostamento (risposte 1, 3 e 5 del proprietario
                // dell'11/10, PathMission.RedoInTheRouteLoopAsync). Poi la cella di una missione che
                // l'operatore ha mandato in un magazzino, che il motore ha prenotato per lei, o che ha
                // perso la sua: se non e' piu' sua (persa dopo un cambio rifiutato, o tolta dal
                // gestionale) il navigatore ne prenota un'altra con il flusso del cambio, e nessuna
                // missione va verso una cella che non le e' prenotata (decisioni del proprietario
                // dell'1/10, risposta 4 del 2/10). Altrimenti, come sempre.
                var redone = await mission.RedoInTheRouteLoopAsync();
                var built = redone ??
                            await Api.MissionGateway.KeepTheStorageCellAsync(mission) ??
                            await FindWhereStoreLu(mission, positionItem, positionItem.LookWhereAllocateLu);

                if (built == RouteBuildStatusTypes.TakenOver)
                {
                    // La missione e' gia' di nuovo in coda, e non e' un fallimento: la rotta l'ha cambiata
                    // un altro mentre la si scriveva (PathMission.BuildPathToDestination), o la missione
                    // aspetta il rigetto dal campo rimandato, o quello che il giro rifaceva
                    // (RedoInTheRouteLoopAsync) si e' rimandato di nuovo per uno step partito intanto, ha
                    // perso contro un altro scrittore, o aspetta il rigetto. Qui il freno non lo conta: una
                    // ricerca della cella che ha perso l'ha gia' contato lei, restituendo la cella
                    // (CountALostCellSearch, risposta 6 del proprietario dell'11/10), e una rotta dritta o
                    // verso la destinazione fissa non e' un fallimento. Non e' neanche una riuscita, e i
                    // fallimenti contati restano.
                }
                else if (built != RouteBuildStatusTypes.NotAvailable)
                {
                    // Riuscita: i fallimenti che il freno le contava non valgono piu', e un
                    // fallimento di domani riparte dal passo piu' corto. E' l'unico punto del giro
                    // in cui si dimenticano (voce 39).
                    RoutePathRetries.Forget(mission.MissionId);

                    if (positionItem.AutoExecuteStep)
                        _ = positionItem.ExecuteStep();
                }
                else
                {
                    // Il fallimento si conta anche se qualcuno l'ha gia' rimessa in coda intanto (un
                    // dispositivo che riaccoda la sua UDC caricata: Standard.AddLoadedLuToRouteToBuildQueue,
                    // AutomationDeviceLuManager.AddLoadedLuToRouteToBuildQueue,
                    // AutomationDeviceConnectionManager.ProcessDeviceTypeSpecificLogic; non il deposito che
                    // aspetta la conferma, PathMission.MayStartTheDeposit, che con la ricerca in volo non
                    // accoda): senza il conto il giro dopo la ripescava senza
                    // freno, con una domanda al gestionale in piu' subito (revisione finale del primo
                    // giro della voce 39, C14). In coda resta una volta sola (RiaccodaConAttesaCrescente
                    // accoda solo se non c'e' gia'). Il motivo dice chi non ce l'ha fatta: la ricerca, o
                    // quello che il giro rifa' prima di lei (la rotta del rigetto da completare, il
                    // PickUpAndStoreAgain arrivato con il magazzino della porta e nessuna rotta).
                    RiaccodaConAttesaCrescente(mission, redone == null
                        ? "nessuna posizione di deposito trovata"
                        : "nessuna rotta verso il punto di scarto del rigetto da completare o verso il magazzino della porta");
                }
            }
            catch (Exception ex)
            {
                WriteLog(
                    $"Error On NAVIGATOR->RouteBuilder_Elapsed Description: {ex.Message}\r\nStackTrace: {ex.StackTrace}",
                    LogEntryType.Warning);
                RiaccodaConAttesaCrescente(mission, "eccezione durante la ricerca della cella di deposito");
            }
            finally
            {
                CellSearchesInFlight.TryRemove(mission.MissionId, out _);
                finished.SetResult();
            }
        });
    }

    /// <returns>
    /// Vero se la rotta calcolata non si e' scritta perche' un altro l'ha cambiata intanto
    /// (<see cref="PathMission.BuildPathToDestination"/>): la missione e' gia' in coda al giro delle rotte.
    /// </returns>
    private static async Task<bool> BuildRouteToFixedDestination(PathMission? value, AutomationDevice positionItem)
    {
        // Senza missione non c'e' niente da scrivere ne' da costruire. La guardia sostituisce
        // "await value?.BuildPathToDestination()", che con value nullo sollevava
        // NullReferenceException invece di non fare nulla: l'await su un Task nullo. Voce D49.
        if (value == null) return false;

        Guid? destination;

        if (positionItem is { DestinationItem: not null, DestinationItem2: null })
        {
            destination = positionItem.DestinationItem.ItemGuid;
        }
        else if (positionItem.DestinationItem == null && positionItem.DestinationItem2 != null)
        {
            destination = positionItem.DestinationItem2.ItemGuid;
        }
        else
        {
            // Due destinazioni possibili: si prova la rotta verso ognuna con una sonda, e si
            // sceglie quella con piu' dispositivi pronti in fila dalla posizione. Prima si
            // scriveva DestinationItemId due volte per fare le due domande, e ogni scrittura
            // cancellava gli step della missione.
            var first = positionItem.DestinationItem?.ItemGuid ?? Guid.Empty;
            var second = positionItem.DestinationItem2?.ItemGuid ?? Guid.Empty;

            var result1 = CheckAvailableStepToDo(value, positionItem, first);
            var result2 = CheckAvailableStepToDo(value, positionItem, second);

            var chosen = result1 >= result2 ? first : second;

            // Una sola scrittura: se la destinazione scelta e' gia' quella della missione il
            // setter non fa nulla, e cella polmone e porta di deposito restano quelle gia'
            // calcolate per la stessa destinazione. Prima le tre scritture le ricalcolavano
            // ogni volta, con gli stessi valori.

            // Senza nessuna delle due destinazioni la scelta e' Guid.Empty, e si assegna null
            // come faceva il codice di prima: i due valori non sono intercambiabili, perche' in
            // BuildPathToDestination il confronto DestinationItemId == Guid.Empty e' falso
            // quando il campo e' null (confronto sollevato su Guid?) e la costruzione prosegue.
            destination = chosen == Guid.Empty ? null : chosen;
        }

        // La destinazione fissa e' configurazione: con il segno dell'operatore non si scrive, e la
        // ricerca finisce verso la scelta dell'operatore, come i rami del deposito
        // (RejectUnlessChosenByOperator): la rotta che la missione ha resta, e se non ce l'ha si
        // costruisce verso di lei. Il segno si riguarda qui, nel momento in cui si scrive:
        // FindWhereStoreLu lo guarda all'ingresso, ma dal ramo CheckOnly si arriva qui dopo la
        // domanda al gestionale, e la scelta puo' essere arrivata intanto.
        if (!value.UnlessChosenByOperator(() => value.DestinationItemId = destination))
            return await BuildRouteStraightToTheDestination(value, positionItem) == RouteBuildStatusTypes.TakenOver;

        return await value.BuildPathToDestination();
    }

    private static int CheckAvailableStepToDo(PathMission value, AutomationDevice positionItem, Guid destinationId)
    {
        var result = 0;
        CheckRoute(value.ProbeRouteSteps(destinationId), positionItem, ref result);
        return result;
    }

    private static void CheckRoute(List<MissionStep> value, AutomationDevice positionItem, ref int result)
    {
        var destinationItem = value.FirstOrDefault(x => x.SourceItem == positionItem)?.DestinationItem;

        if (destinationItem is not { IsReadyToReceiveLu: true })
            return;

        result++;

        CheckRoute(value, destinationItem, ref result);
    }

    internal static async Task ExtractionProcess()
    {
        // La domanda del lavoro pronto sta prima della presa del semaforo perche' costa 1 + 2N
        // andate e ritorni di rete: una richiesta dal WCS al gestionale, e poi il gestionale che
        // richiama il WCS due volte per ogni assegnazione candidata, per validarla. Tenuta sotto il
        // lucchetto, quella spesa la pagavano gli altri sette punti del motore che si contendono lo
        // stesso semaforo con trenta secondi di attesa. Dentro resta la costruzione della coda, che
        // il lucchetto ce l'ha per davvero.
        IReadOnlyList<PendingNote> noteWork;

        try
        {
            // RequireWarehouse viaggia con la chiamata, e la riga di registro e' quella che
            // scriveva il catch di BuildLuExtraction, parola per parola: chi legge i registri non
            // deve accorgersi dello spostamento, e un guasto del gestionale non deve risalire.
            // L'opzione d'impianto viaggia nella richiesta: resta qui, nella configurazione
            // dell'host del WCS, ma la decisione che governa - non assegnare una nota se un'altra
            // lavorazione attiva insiste sulla stessa cella di picking - vive dal compito 14 nel
            // gestionale, insieme al resto dell'orchestrazione.
            noteWork = await RequireWarehouse("BuildLuExtraction")
                .GetPendingNoteWorkAsync(LimitOneShippingNotePerBuffer);
        }
        catch (Exception ex)
        {
            WriteLog($"Error On Navigator->BuildLUExtraction Description: {ex.Message}\r\nStackTrace: {ex.StackTrace}",
                LogEntryType.Warning);
            return;
        }

        // Niente da accodare: si esce senza prendere il semaforo. Prima si usciva dopo averlo preso
        // e rilasciato, e quella presa non serviva a nessuno - non e' una barriera per altri.
        if (noteWork.Count == 0) return;

        if (!await SyncExtraction.WaitAsync(SyncExtractionTimeout))
        {
            WriteLog("Timeout acquiring extraction lock in ExtractionProcess", LogEntryType.Warning);
            return;
        }

        try
        {
            if (NavStatus != NavigatorStatusTypes.Run) return;

            // Solo la costruzione della coda tiene il semaforo. La pulizia delle righe delle note
            // sospese sta in AssignmentsProcess, dove girava prima del compito 14: da qui non
            // girerebbe affatto nei giri in cui il lucchetto scade, e terrebbe il lucchetto durante
            // una richiesta al gestionale per ogni riga cancellata.
            await BuildLuExtraction(noteWork);
        }
        catch (Exception ex)
        {
            WriteLog(
                $"Error during the ProcessoEstrazioni process, Description: {ex.Message}\r\nStackTrace: {ex.StackTrace}",
                LogEntryType.Error);
        }
        finally
        {
            SyncExtraction.Release();
        }
    }


    internal static async Task MasterProcess()
    {
        try
        {
            await CommandToDo.Instance.Execute();
            await ExtractionOutcomeDispatcher.TryDispatchAsync();
            // Dopo gli esiti: la riapertura di una riga aspetta che la sua «estratta» sia arrivata.
            await PickedExtractionReportDispatcher.TryDispatchAsync();
            await CellGiveBackDispatcher.TryDispatchAsync();
            await WarehouseTransitionDispatcher.TryDispatchAsync();

            // Item 54, decision 4: the changes waiting for the end of a step, for the ways a step stops
            // being in flight that nobody announces (its device in Warning or Error). Only nudged.
            Api.PendingMissionChanges.SweepAll();

            if (NavStatus != NavigatorStatusTypes.Run || _masterProcessIsRunning) return;

            _masterProcessIsRunning = true;

            // La guardia e' presa sopra: il finally che la rilascia sta in un try annidato che
            // si entra solo dopo averla presa, cosi' non la rilascia mai una chiamata che l'ha
            // trovata gia' alzata ed e' uscita prima.
            try
            {
                await AssignmentsProcess();
                await ExtractionProcess();
                await DbInfo.CheckOperation();
            }
            finally
            {
                _masterProcessIsRunning = false;
            }
        }
        catch (Exception ex)
        {
            WriteLog($"Error during the StartRouting process, Description: {ex.Message}\r\nStackTrace: {ex.StackTrace}",
                LogEntryType.Error);
        }
    }

    private static async void Navigator_DefragmentationChanged(object? sender, DefragmentationChangedEventArgs value)
    {
        try
        {
            await DbInfo.UpdateDefragmentationStatus(value.DefragmentationStatus);
        }
        catch (Exception ex)
        {
            WriteLog(
                $"Error On Navigator_DefragmentationChanged Description: {ex.Message}\r\nStackTrace: {ex.StackTrace}",
                LogEntryType.Warning);
        }
    }


    private static async Task UpdateStatus()
    {
        await DbInfo.UpdateNavigatorStatusCounters(NavStatus == NavigatorStatusTypes.Run);
        ExtractionTimers = NavStatus == NavigatorStatusTypes.Run;
    }

    #endregion Private Methods
}
