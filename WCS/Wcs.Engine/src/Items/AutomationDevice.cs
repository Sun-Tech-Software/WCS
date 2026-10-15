using Helper.Converters;
using Wcs.Common.Events;
using Wcs.Engine.Interfaces;
using Wcs.Engine.Items.ExecutionStrategies;
using Wcs.Engine.Items.Managers;
using Wcs.Engine.ORM;
using Log.Interface;
using System.Data;
using System.Runtime.CompilerServices;
using Wcs.Connection.Interface.Events;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using static Wcs.Common.Enums;
using static Wcs.Engine.Items.PathMission;
using CommandToDo = Wcs.Engine.ORM.CommandToDo;

namespace Wcs.Engine.Items;

/// <summary>
/// Rappresenta un dispositivo di automazione che gestisce il trasferimento e l'elaborazione delle unità di carico (LU).
/// Fornisce funzionalità complete per il controllo di dispositivi ASI, gestione degli stati, esecuzione di missioni
/// e coordinamento con altri dispositivi nel sistema di automazione.
/// </summary>
public partial class AutomationDevice : IDisposable
{

    #region Internal Fields

    /// <summary>
    /// Token di cancellazione utilizzato per interrompere le operazioni durante il processo di dispose del dispositivo.
    /// </summary>
    internal CancellationToken CancelDisposingToken;

    /// <summary>
    /// Manager responsabile della gestione delle unità di carico (LU) per questo dispositivo di automazione.
    /// </summary>
    internal AutomationDeviceLuManager? LuManager;

    /// <summary>
    /// Esecutore responsabile dell'elaborazione e dell'esecuzione degli step delle missioni.
    /// </summary>
    internal AutomationDeviceStepExecutor? StepExecutor;

    /// <summary>
    /// Manager responsabile della gestione delle operazioni di storage e magazzino.
    /// </summary>
    internal AutomationDeviceStorageManager? StorageManager;

    /// <summary>
    /// Il manager di gruppo di <b>questo</b> dispositivo: quello che il setter di
    /// <c>GroupExecuteStepRunning</c> fa ripartire, e che quindi contende il gettone.
    /// </summary>
    /// <remarks>
    /// <c>internal</c> come gli altri tre manager, e non piu' privato: un collaudo che costruisse
    /// un <c>AutomationDeviceGroupManager</c> per conto suo proverebbe un oggetto diverso da quello
    /// che gira in sfondo sullo stesso dispositivo, e non potrebbe spegnerlo. Vedi
    /// <c>GroupExecuteStepFailureTest</c>.
    /// </remarks>
    internal AutomationDeviceGroupManager? GroupManager;

    #endregion Internal Fields

    #region Private Fields

    private CancellationToken _cancelCircularDistToken;
    private CancellationTokenSource? _cancellationCircularDistTokenSource;
    private CancellationTokenSource? _cancellationDisposingTokenSource;

    private AutomationDeviceConnectionManager? _connectionManager;
    private bool _disposedValue;
    private AutomationDeviceErrorHandler? _errorHandler;

    /// <summary>
    /// Strategia di esecuzione corrente del dispositivo, conservata fra un giro e l'altro.
    /// Ricrearla ad ogni chiamata azzerava lo stato che la strategia tiene fra due esecuzioni
    /// (in particolare <c>Standard._buildRouteOnNoStepToDo</c>), rendendo inerte la protezione
    /// contro il riaccodamento ripetuto della stessa UDC.
    /// L'accesso e' serializzato dal semaforo di <see cref="ExecuteStep"/>: una sola esecuzione
    /// per dispositivo alla volta.
    /// </summary>
    private IExecutionStrategy? _executionStrategy;

    private AutomationDeviceStateMachine? _stateMachine;

    #endregion Private Fields

    #region Public Constructors

    /// <summary>
    /// Inizializza una nuova istanza della classe <see cref="AutomationDevice"/> con valori predefiniti.
    /// </summary>
    public AutomationDevice()
    {
        Initialize();
    }

    /// <summary>
    /// Inizializza una nuova istanza della classe <see cref="AutomationDevice"/> con un GUID specificato.
    /// </summary>
    /// <param name="value">Il GUID univoco che identifica questo dispositivo di automazione.</param>
    public AutomationDevice(Guid value)
    {
        ItemGuid = value;
        Initialize();
    }

    #endregion Public Constructors

    #region Private Destructors

    /// <summary>
    /// Finalizzatore che assicura la corretta liberazione delle risorse non gestite.
    /// </summary>
    ~AutomationDevice()
    {
        Dispose(false);
    }

    #endregion Private Destructors

    #region Public Delegates

    /// <summary>
    /// Rappresenta il metodo che gestirà l'evento di cambiamento dello stato di carico delle LU.
    /// </summary>
    /// <param name="sender">L'origine dell'evento.</param>
    /// <param name="e">Gli argomenti dell'evento contenenti le informazioni sul cambiamento di stato.</param>
    public delegate void LuLoadStatusChangedEventHandler(object sender, AutomationDeviceStatusChangedEventArgs e);

    /// <summary>
    /// Rappresenta il metodo che gestirà l'evento di cambiamento dello step in esecuzione.
    /// </summary>
    /// <param name="sender">L'origine dell'evento.</param>
    /// <param name="e">Gli argomenti dell'evento contenenti l'ID del nuovo step in esecuzione.</param>
    public delegate void RunningStepChangedEventHandler(object sender, AutomationDeviceRunningStepIdChangedEventArgs e);

    /// <summary>
    /// Rappresenta il metodo che gestirà l'evento di cambiamento dello stato del dispositivo.
    /// </summary>
    /// <param name="sender">L'origine dell'evento.</param>
    /// <param name="e">Gli argomenti dell'evento contenenti le informazioni sul cambiamento di stato.</param>
    public delegate void StatusChangedEventHandler(object sender, AutomationDeviceStatusChangedEventArgs e);

    #endregion Public Delegates

    #region Public Events

    /// <summary>
    /// Si verifica quando cambia lo stato di carico delle unità di carico (LU) del dispositivo.
    /// </summary>
    public event LuLoadStatusChangedEventHandler? LuLoadStatusChanged;

    /// <summary>
    /// Si verifica quando cambia l'identificativo dello step attualmente in esecuzione.
    /// </summary>
    public event RunningStepChangedEventHandler? RunningStepChanged;

    /// <summary>
    /// Si verifica quando cambia lo stato operativo del dispositivo di automazione.
    /// </summary>
    public event StatusChangedEventHandler? StatusChanged;

    #endregion Public Events

    #region Internal Enums

    /// <summary>
    /// Definisce i tipi di movimento per i gruppi di dispositivi.
    /// </summary>
    internal enum GroupMovementType : byte
    {
        /// <summary>
        /// Nessun movimento in corso.
        /// </summary>
        None = 0,
        /// <summary>
        /// Movimento di caricamento in corso.
        /// </summary>
        Loading = 1,
        /// <summary>
        /// Movimento di estrazione in corso.
        /// </summary>
        Extracting = 2
    }

    /// <summary>
    /// Definisce gli stati di costruzione del percorso per le missioni.
    /// </summary>
    internal enum RouteBuildStatusTypes : byte
    {
        /// <summary>
        /// Percorso disponibile e costruito con successo.
        /// </summary>
        Available = 1,
        /// <summary>
        /// Percorso non disponibile.
        /// </summary>
        NotAvailable = 2,
        /// <summary>
        /// Percorso rifiutato.
        /// </summary>
        Rejected = 3,
        /// <summary>
        /// Percorso calcolato e non scritto: un altro ha cambiato rotta, destinazione o posizione mentre
        /// lo si calcolava (voce 41, disegno C), e la missione e' gia' in coda al giro delle rotte con lo
        /// stato di chi ha vinto. Non e' un fallimento: niente scarto, niente freno, niente cella
        /// restituita (decisione 1 del proprietario del 10/10).
        /// </summary>
        TakenOver = 4,

        /// <summary>
        /// Item 54, decision 5 of the owner of 13/10: the load unit is on the relocator the operator sent
        /// it to «only to bring it there» (<c>Api.RelocatorArrival.Stay</c>), and it stays. Nothing to
        /// build and nothing to ask: not a failure (no brake, no rejection). The route loop leaves it out
        /// of its queue as after a route found; the grouped store search goes on with the others.
        /// </summary>
        Stays = 5
    }

    /// <summary>
    /// Definisce gli stati di disponibilità per l'esecuzione degli step.
    /// </summary>
    internal enum StepAvailableStatusTypes : byte
    {
        /// <summary>
        /// Step non disponibile per l'esecuzione.
        /// </summary>
        NotAvailable = 0,
        /// <summary>
        /// Step disponibile per l'esecuzione.
        /// </summary>
        Available = 1,
        /// <summary>
        /// Destinazione non disponibile.
        /// </summary>
        DestinationNotAvailable = 2,
        /// <summary>
        /// Destinazione disponibile.
        /// </summary>
        DestinationAvailable = 3
    }

    #endregion Internal Enums

    #region Public Methods

    /// <summary>
    /// Cancella lo stato di errore del dispositivo permettendo la ripresa delle operazioni normali.
    /// </summary>
    public void ClearError()
    {
        _errorHandler?.ClearError();
    }

    /// <summary>
    /// Gestisce un evento ASI ricevuto dalla connessione del dispositivo.
    /// </summary>
    /// <param name="e">Gli argomenti dell'evento ASI da processare.</param>
    public void ConnectionAsiEvent(AsiEventEventArgs? e)
    {
        _connectionManager?.AsiEvent(e);
    }

    /// <summary>
    /// Gestisce la notifica di completamento di un movimento ASI.
    /// </summary>
    /// <param name="e">Gli argomenti dell'evento contenenti i dettagli del movimento completato.</param>
    public async Task ConnectionAsiMovementIsCompleted(AsiMovementIsCompletedEventArgs? e)
    {
        if (_connectionManager != null) await _connectionManager.AsiMovementIsCompleted(e);
    }

    /// <summary>
    /// Gestisce l'invio di dati LU attraverso la connessione ASI.
    /// </summary>
    /// <param name="e">Gli argomenti dell'evento contenenti i dati LU da inviare.</param>
    public async Task ConnectionAsiSendLuData(AsiSendLuDataEventArgs? e)
    {
        if (_connectionManager != null) await _connectionManager.AsiSendLuData(e);
    }

    /// <summary>
    /// Gestisce la notifica di una nuova LU rilevata sul sistema ASI.
    /// </summary>
    /// <param name="e">Gli argomenti dell'evento contenenti le informazioni sulla nuova LU.</param>
    public async Task ConnectionNewLuOnASI(NewLuOnAsiEventArgs? e)
    {
        if (_connectionManager != null) await _connectionManager.NewLuOnASI(e);
    }

    /// <summary>
    /// Libera tutte le risorse utilizzate dal dispositivo di automazione.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Esegue il prossimo step della missione corrente del dispositivo.
    /// </summary>
    /// <param name="delay">
    /// Pausa opzionale in millisecondi prima dell'esecuzione dello step, fatta fuori dal semaforo e
    /// prima del controllo di marcia.
    /// </param>
    /// <returns>
    /// True se l'esecuzione e' stata avviata; false se e' stata <b>rifiutata</b> — dispositivo
    /// impegnato, navigatore non in marcia, o esecuzione interrotta. Non solleva: la cancellazione
    /// (dispositivo in chiusura) e ogni altra eccezione si scrivono nel registro e rispondono false.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Un elemento impegnato rifiuta l'esecuzione: non la rimanda e non la accoda.</b> Il
    /// motore ha gia' il proprio modo di far ripartire un elemento fermo, ed e' il richiamo: chi
    /// trova occupato si iscrive in <see cref="DevicesReadyToTransferLu"/> della destinazione, e
    /// la destinazione lo richiama quando si libera. Rimandare l'esecuzione in coda sarebbe un
    /// secondo meccanismo che corre in parallelo a quello, e farebbe partire un movimento in un
    /// momento che nessuno ha piu' scelto — per un comando dell'operatore, un movimento che
    /// avviene molto dopo il rifiuto.
    /// </para>
    /// <para>
    /// «Impegnato» sono due cose distinte, e il rifiuto le distingue nel log: un comando in corso
    /// verso il campo (<c>RunningStepId</c> valorizzato, che e' esattamente
    /// <see cref="IsReadyToExecuteStep"/> a rovescio) oppure un'altra esecuzione gia' dentro il
    /// semaforo.
    /// </para>
    /// <para>
    /// La pausa chiesta con <paramref name="delay"/> si fa <b>prima</b> di tutto il resto, fuori dal
    /// semaforo: una chiamata con la pausa equivale a un chiamante che aspetta e poi chiama
    /// <c>ExecuteStep()</c>, ed e' la forma originale (Atomind dormiva prima della guardia). Tenuta
    /// dentro il semaforo, per tutta la sua durata il dispositivo risultava impegnato per tutti: il
    /// commit del cambio di destinazione (che allora prendeva il semaforo con quella che oggi e'
    /// <see cref="TryHoldExecutionForTesting"/>; dal compito 5 della voce 41 confronta la versione della
    /// rotta e non lo prende piu'), i comandi dell'operatore
    /// che guardano <see cref="IsBusyWithCommand"/>, la rete del gettone appeso e gli altri
    /// <c>ExecuteStep</c>, rifiutati mentre il dispositivo dormiva soltanto (voce 41, corsa 4). La
    /// marcia del navigatore si guarda dopo la pausa: un passaggio a Stop mentre dura la ferma.
    /// </para>
    /// </remarks>
    public async Task<bool> ExecuteStep(int delay = 0)
    {
        // Il semaforo si rilascia solo se e' stato davvero preso: il try comincia prima della
        // presa, e un rilascio non dovuto lo alzerebbe di un gettone lasciando entrare due
        // esecuzioni insieme.
        var acquired = false;

        try
        {
            // La pausa resta DENTRO il try, come da D41: se il token scatta mentre e' in corso,
            // l'eccezione deve finire nel catch qui sotto, non risalire a chi chiama
            // `_ = device.ExecuteStep(500)`, cioe' a nessuno. Il semaforo non e' ancora preso, e il
            // finally non rilascia niente: un rilascio qui lo alzerebbe di un gettone.
            if (delay > 0)
                await (ExecutionPauseForTesting?.Invoke(delay, CancelDisposingToken)
                    ?? Task.Delay(delay, CancelDisposingToken));

            if (Navigator.NavStatus != NavigatorStatusTypes.Run)
                return false;

            // La rete: prima di rifiutare, si guarda se cio' che blocca sia un gettone appeso.
            // Sta DENTRO il try: solleva eventi (RunningStepChanged, EventSink.ItemChanged) e da
            // fuori la sua eccezione sarebbe risalita a diciotto chiamanti che invocano questo
            // metodo come `_ = device.ExecuteStep()`, cioe' a nessuno.
            TryReleaseHungRunningStep();

            // Il secondo passo della rete, indipendente dal primo: la prenotazione tenuta per uno
            // step che non esiste piu'. TryReleaseHungRunningStep esce alla prima riga se il gettone
            // e' vuoto, e non vedrebbe questo stato - che e' proprio quello in cui una prenotazione
            // resta appesa senza nessuna via di restituzione.
            TryReleaseHungIncomingReservation();

            // Impegnato con il campo: c'e' un comando in volo e la risposta non e' ancora arrivata.
            // Farlo avanzare adesso vorrebbe dire mandarne un secondo. Il getter di RunningStepId
            // restituisce Guid.Empty se lo step non esiste piu', quindi un identificativo rimasto
            // appeso non blocca il dispositivo per sempre.
            if (IsBusyWithCommand)
            {
                // Il messaggio dice QUALE dei due gettoni blocca. Prima diceva «step» e basta, e
                // l'indagine del 2026-09-09 comincio' cercando la causa nella prenotazione mentre a
                // bloccare era RunningStepId.
                Navigator.WriteLog(
                    $"StepExecution {ItemGuid} rifiutata: impegnata (stato {Status}, RunningStepId {RunningStepId}, WaitingStepId {WaitingStepId})",
                    LogEntryType.Information);
                return false;
            }

            // Anche questa dentro il try: con il token gia' annullato — dispositivo in chiusura —
            // WaitAsync solleva OperationCanceledException, e da fuori quell'eccezione non la
            // osservava nessuno.
            if (!await _executionSemaphore.WaitAsync(0, CancelDisposingToken))
            {
                // Impegnato con se' stesso, e sfuggito al controllo sopra perche' fra i due c'e' una
                // finestra. Il canale e' WriteLog e non RecordNavigatorLog perche' e' un percorso caldo,
                // e il secondo scrive sul database ad ogni chiamata.
                Navigator.WriteLog(
                    $"StepExecution {ItemGuid} rifiutata: un'esecuzione e' gia' in corso",
                    LogEntryType.Information);
                return false;
            }

            acquired = true;

            Navigator.WriteLog($"StepExecution {ItemGuid}", LogEntryType.Information);

            await ExecuteStepCoreAsync(CancelDisposingToken);

            return true;
        }
        catch (OperationCanceledException)
        {
            Navigator.WriteLog($"Step execution cancelled for device {ItemGuid}", LogEntryType.Information);
            // Non ri-lanciare - lascia che il finally rilasci il semaphore e poi esci normalmente
            return false;
        }
        catch (Exception ex)
        {
            LogError(ex);
            return false;
        }
        finally
        {
            if (acquired)
            {
                _executionSemaphore.Release();

                // Un avvio che ha rinunciato durante questa esecuzione ha chiesto di riprovare: adesso il
                // semaforo e' libero (ExecuteAgainWhenFree).
                if (Interlocked.Exchange(ref _executeAgain, 0) == 1)
                    RunObserved(() => ExecuteStep());
            }
        }
    }

    /// <summary>
    /// Chiede una nuova esecuzione di questo dispositivo appena e' libero: subito, se nessuna esecuzione
    /// tiene il semaforo; altrimenti alla fine di quella che lo tiene (<see cref="ExecuteStep"/>), che la
    /// lancia dopo averlo rilasciato. La chiama ogni avvio di uno step che rinuncia al gettone perche' la
    /// rotta e' cambiata: il movimento singolo (<see cref="StartPutToMOVE_LU()"/>), il <c>GetFrom</c>, il
    /// prelievo e il deposito in cella, il movimento di gruppo e i movimenti singoli del gruppo (questi
    /// quattro dal giro finale delle correzioni della voce 41), e il deposito che trova a bordo un'altra
    /// missione. Di solito gira dentro l'esecuzione di questo dispositivo, e un <c>ExecuteStep</c>
    /// chiamato da li' sarebbe rifiutato («un'esecuzione e' gia' in corso»). Risposta 4 del proprietario
    /// dell'11/10.
    /// </summary>
    /// <remarks>
    /// Il segno si alza prima di guardare il semaforo, e chi rilascia il semaforo lo guarda dopo averlo
    /// rilasciato: in qualunque ordine arrivino, uno dei due vede l'altro, e l'esecuzione chiesta parte
    /// una volta. L'esecuzione nuova parte sul pool, osservata (<see cref="RunObserved"/>), e passa da
    /// tutte le guardie di <see cref="ExecuteStep"/>: in Stop non fa niente.
    /// </remarks>
    internal void ExecuteAgainWhenFree()
    {
        Interlocked.Exchange(ref _executeAgain, 1);

        if (_executionSemaphore.CurrentCount > 0 && Interlocked.Exchange(ref _executeAgain, 0) == 1)
            RunObserved(() => ExecuteStep());
    }

    /// <summary>Vedi <see cref="ExecuteAgainWhenFree"/>: 1 se un'esecuzione e' chiesta per quando il semaforo si libera.</summary>
    private int _executeAgain;

    /// <summary>
    /// Solo per le prove: vero se un'esecuzione e' chiesta per quando il semaforo si libera
    /// (<see cref="ExecuteAgainWhenFree"/>) e non e' ancora partita. Una prova che tiene il semaforo
    /// (<see cref="TryHoldExecutionForTesting"/>), come lo tiene l'esecuzione dentro cui l'avvio gira,
    /// vede qui la richiesta senza che un'esecuzione parta sul pool.
    /// </summary>
    internal bool ExecutionAskedWhenFreeForTesting => Volatile.Read(ref _executeAgain) == 1;

    /// <summary>
    /// Prende il semaforo di esecuzione senza aspettare, per un tratto breve fatto da fuori: oggi lo
    /// usano solo le prove, per tenere il dispositivo «in esecuzione» senza contare sul tempo. Fino al
    /// compito 5 della voce 41 lo prendeva il commit del cambio di destinazione dell'operatore, contro
    /// la rimozione della rotta della strategia <c>Standard</c>; ora quella rimozione confronta la
    /// versione della rotta (<c>PathMission.RemoveTheRouteSeenWithoutAStepToDo</c>), e il commit non
    /// rifiuta piu' il cambio perche' la posizione esegue (corsa 4 della ricognizione).
    /// </summary>
    /// <returns>False se un'esecuzione e' in corso: niente e' stato preso, e chi chiama rinuncia.</returns>
    /// <remarks>
    /// Chi lo prende lo restituisce con <see cref="ReleaseExecutionHoldForTesting"/>, e sotto di lui non
    /// chiama il gestionale e non aspetta niente: un <see cref="ExecuteStep"/> che arriva intanto viene
    /// rifiutato («un'esecuzione e' gia' in corso»), come quando il dispositivo esegue gia'. La
    /// restituzione non lancia l'esecuzione chiesta intanto con <see cref="ExecuteAgainWhenFree"/>: la
    /// richiesta resta (<see cref="ExecutionAskedWhenFreeForTesting"/>). Fino al giro finale delle
    /// correzioni della voce 41 si chiamavano <c>TryHoldExecution</c> e <c>ReleaseExecutionHold</c>,
    /// senza il nome che dice che sono per le prove.
    /// </remarks>
    internal bool TryHoldExecutionForTesting() => _executionSemaphore.Wait(0);

    /// <summary>
    /// Solo per le prove: fa la pausa di <see cref="ExecuteStep"/> al posto di <c>Task.Delay</c>, con
    /// la durata chiesta e il token di chiusura del dispositivo. Serve a sapere quando la pausa
    /// comincia e a farla finire quando si vuole, senza contare sul tempo. Nullo in esercizio.
    /// </summary>
    internal Func<int, CancellationToken, Task>? ExecutionPauseForTesting;

    /// <summary>
    /// Solo per le prove: la strategia <c>Standard</c> la chiama quando ha visto la missione caricata
    /// senza uno step da fare, prima di toglierle la rotta. Una prova ci fa scrivere la rotta a un altro
    /// scrittore: e' il punto della corsa 6 della ricognizione della voce 41. Nulla in esercizio.
    /// </summary>
    internal Action? NoStepToDoPauseForTesting;

    /// <summary>
    /// Solo per le prove: l'avvio di uno step la chiama con il gettone non ancora scritto e, dove l'avvio
    /// lo prende prima del gettone, con il posto sulla destinazione gia' preso. Una prova ci fa togliere
    /// la rotta a un altro scrittore: e' il punto della corsa 5 della ricognizione della voce 41. La
    /// chiama il dispositivo che avvia: questo nel movimento singolo (<see cref="StartPutToMOVE_LU()"/>),
    /// chi riceve nel <c>GetFrom</c>, chi preleva nel prelievo dalla cella, chi deposita nel deposito in
    /// cella e il gruppo nei suoi movimenti (compito 6 della voce 41). Nei movimenti singoli del gruppo
    /// (<c>ProcessSingleStep</c>) il posto non e' ancora preso: lo prende dopo il gettone del gruppo
    /// l'avvio del membro. Nulla in esercizio.
    /// </summary>
    internal Action? StepStartPauseForTesting;

    /// <summary>
    /// Solo per le prove: <see cref="RequeueLoadedLu"/>, che rimette in coda al giro delle rotte la UDC
    /// caricata, la chiama dopo aver letto la missione a bordo e prima di accodarla. Ci passano i suoi tre
    /// chiamanti: <c>Standard.AddLoadedLuToRouteToBuildQueue</c>,
    /// <c>AutomationDeviceLuManager.AddLoadedLuToRouteToBuildQueue</c> e
    /// <c>AutomationDeviceConnectionManager.ProcessDeviceTypeSpecificLogic</c>. Una prova ci sposta la
    /// missione su un altro dispositivo (l'accodamento nullo, voce 41, compito 7), o la accoda da un altro
    /// scrittore (l'accodamento doppio, revisione finale della voce 41). Nulla in esercizio.
    /// </summary>
    internal Action? RouteQueueingPauseForTesting;

    /// <summary>
    /// Rimette la UDC caricata in coda al giro delle rotte, se non c'e' gia'.
    /// </summary>
    /// <remarks>
    /// La missione a bordo si legge una volta sola: fra la lettura e l'accodamento puo' lasciare il
    /// dispositivo, e una seconda lettura accodava un nullo (voce 41, compito 7). Si accoda quella vista,
    /// e il giro la costruisce dalla posizione che ha quando la pesca. Lo sguardo «c'e' gia'?» e
    /// l'accodamento stanno insieme sotto il lucchetto della coda
    /// (<see cref="Collections.RoutesToBuild.EnqueueIfAbsent"/>): prima erano tre copie di «guarda, poi
    /// accoda» (la strategia <c>Standard</c>, il gestore delle UDC, quello delle connessioni), e due
    /// accodatori insieme mettevano la missione in coda due volte (revisione finale della voce 41).
    /// </remarks>
    internal void RequeueLoadedLu()
    {
        var loaded = LoadedLu;
        if (loaded == null)
            return;

        RouteQueueingPauseForTesting?.Invoke();
        Collections.RoutesToBuild.Instance.EnqueueIfAbsent(loaded);
    }

    /// <summary>Restituisce il semaforo preso con <see cref="TryHoldExecutionForTesting"/>.</summary>
    internal void ReleaseExecutionHoldForTesting() => _executionSemaphore.Release();

    /// <summary>
    /// Carica una LU sul dispositivo per la missione specificata.
    /// </summary>
    /// <param name="missionId">L'identificativo univoco della missione da caricare.</param>
    /// <param name="activateExecuteStep">Indica se attivare automaticamente l'esecuzione del prossimo step.</param>
    public async Task LoadLu(Guid missionId, bool activateExecuteStep = true)
    {
        if (LuManager != null) await LuManager.LoadLu(missionId, activateExecuteStep);
    }

    /// <summary>
    /// Notifica che una LU è stata scaricata dal sistema ASI.
    /// </summary>
    /// <param name="value">Il tipo di step che ha causato lo scaricamento.</param>
    public async Task LuUnloadedFromAsi(StepTypes value)
    {
        if (LuManager != null) await LuManager.LuUnloadedFromAsi(value);
    }

    /// <summary>
    /// Rifiuta la LU attualmente caricata sul dispositivo.
    /// </summary>
    public async Task RejectLu()
    {
        if (LuManager != null) await LuManager.RejectLu();
    }

    /// <summary>
    /// Reimposta lo stato di errore del dispositivo senza ritardi aggiuntivi.
    /// </summary>
    public void ResetError()
    {
        _errorHandler?.ResetError();
    }

    /// <summary>
    /// Imposta lo stato online/offline del dispositivo.
    /// </summary>
    /// <param name="value">True per impostare il dispositivo offline, false per riportarlo online.</param>
    public async Task SetStatusOffLine(bool value)
    {
        if (_stateMachine != null) await _stateMachine.SetStatusOffLine(value);
    }

    /// <summary>
    /// Avvia il processo di movimento di una LU verso la destinazione specificata nello step corrente.
    /// Coordina il trasferimento tra dispositivi ASI e aggiorna gli stati appropriati.
    /// </summary>
    public void StartPutToMOVE_LU()
    {
        var mission = LoadedLu;

        if (mission?.StepToDo?.DestinationItem == null)
            return;

        // Con un cambio di destinazione dell'operatore in corso lo step della missione non parte, e
        // l'avvio si conta fino al gettone: o l'uno o l'altro (PathMission.TryBeginStepStart). Da
        // qui si arriva anche dal ciclo di un gruppo (ProcessSingleStep), fuori dal semaforo di
        // questo dispositivo: il semaforo da solo non bastava a tenere fermo lo step.
        if (!mission.TryBeginStepStart())
        {
            Navigator.WriteLog(
                $"StartPutToMOVE_LU {ItemGuid}: la missione {mission.MissionId} sta cambiando destinazione, il movimento non parte",
                LogEntryType.Information);
            return;
        }

        try
        {
            // La versione della rotta si legge prima dello step: il gettone si scrive solo se e' ancora
            // quella (PathMission.TryTakeTheStepToken), cioe' se nessuno ha tolto o riscritto la rotta
            // fra la lettura dello step e il gettone (corsa 5 della ricognizione della voce 41).
            var seenRouteVersion = mission.RouteVersion;

            // Lo step si legge dopo essersi contati: letto prima, un cambio cominciato e finito nel
            // mezzo l'avrebbe gia' tolto, e partirebbe uno step che non c'e' piu'.
            if (!ReferenceEquals(LoadedLu, mission) || mission.StepToDo is not { DestinationItem: not null } stepToDo)
                return;

            StartPutToMOVE_LU(mission, stepToDo, seenRouteVersion);
        }
        finally
        {
            mission.EndStepStart();
        }
    }

    /// <summary>
    /// Il corpo di <see cref="StartPutToMOVE_LU()"/>, con l'avvio gia' contato sulla missione.
    /// </summary>
    /// <param name="mission">La missione a bordo, con l'avvio contato.</param>
    /// <param name="stepToDo">Lo step da avviare, risolto una volta dal chiamante.</param>
    /// <param name="seenRouteVersion">La versione della rotta letta prima dello step.</param>
    /// <remarks>
    /// Il gettone (<see cref="RunningStepId"/>) si scrive sotto il lucchetto della rotta della missione,
    /// solo se la rotta e' ancora quella su cui lo step si e' letto (<c>PathMission.TryTakeTheStepToken</c>).
    /// Fra la presa del posto sulla destinazione e il gettone lo step non risulta in volo, e una
    /// ricostruzione della rotta (l'ingresso fra le attive, l'arrivo, il giro delle rotte, un rigetto)
    /// poteva toglierlo: il gettone si scriveva lo stesso per uno step che non c'era piu', il comando
    /// partiva, e l'UDC finiva sulla destinazione mentre per il motore restava qui (corsa 5 della
    /// ricognizione della voce 41). Ora, se la rotta e' cambiata, l'avvio rinuncia prima del comando: il
    /// posto si restituisce e la destinazione torna <c>Ready</c>. Chi ha tolto la rotta la riscrive, o
    /// rimette la missione al giro delle rotte.
    /// <para>
    /// La rinuncia non lascia niente di quello che l'avvio avrebbe scritto per lo step (risposta 4 del
    /// proprietario dell'11/10): lo stato <see cref="MissionStatusTypes.Run"/> della missione, il suo
    /// <c>GroupedMovementId</c> azzerato e l'uscita di questo dispositivo dalla lista di chi aspetta la
    /// destinazione (<c>RemoveDevicesReadyToTransferLu</c>, con il suo <c>LUtoReceive</c>) si scrivono
    /// solo dopo il gettone, e il posto sulla destinazione si restituisce. E questo dispositivo si
    /// risveglia appena e' libero (<see cref="ExecuteAgainWhenFree"/>), e riprova con la rotta nuova:
    /// prima restava li' finche' qualcuno non lo richiamava, e chi lo richiamava mentre era ancora in
    /// esecuzione era rifiutato.
    /// </para>
    /// </remarks>
    private void StartPutToMOVE_LU(PathMission mission, MissionStep stepToDo, long seenRouteVersion)
    {
        var itemToSetBusy = stepToDo.DestinationItem;

        // Il posto sulla destinazione si prende PRIMA di marcarla occupata e prima di mandare il
        // comando. Prima `WaitingStepId` si scriveva piu' sotto, dopo aver gia' marcato `Busy`: fra
        // il controllo `CanStartPutToOperation` e quella scrittura un secondo mittente poteva
        // superare lo stesso controllo, sovrascrivere il gettone del primo, e far partire un
        // secondo comando verso questo stesso elemento. Voce D73.
        // I magazzini restano fuori dalla prenotazione, come prima: per loro `WaitingStepId` non
        // veniva scritto.
        var haPrenotato = itemToSetBusy == null ||
                          itemToSetBusy.DeviceType == DeviceTypes.Storage ||
                          itemToSetBusy.TryReserveForIncomingStep(stepToDo.StepId);

        if (!haPrenotato)
        {
            Navigator.WriteLog(
                $"StartPutToMOVE_LU {ItemGuid}: la destinazione {itemToSetBusy!.ItemGuid} e' gia' impegnata per lo step {itemToSetBusy.WaitingStepId}, il movimento non parte",
                LogEntryType.Information);
            return;
        }

        if (itemToSetBusy != null)
            itemToSetBusy.Status = DeviceStatusTypes.Busy;

        if (stepToDo.PathMission == null || stepToDo.SourceItem == null || stepToDo.DestinationItem == null)
        {
            // Il comando non partira': il posto va restituito, o la destinazione resta impegnata per
            // uno step che non arrivera' mai e nessun altro puo' piu' consegnarle nulla.
            itemToSetBusy?.ReleaseIncomingStep(stepToDo.StepId);
            return;
        }

        StepStartPauseForTesting?.Invoke();

        if (!mission.TryTakeTheStepToken(seenRouteVersion, stepToDo, () => RunningStepId = stepToDo.StepId))
        {
            // Il comando non partira': il posto va restituito, e la destinazione, se e' ancora la Busy
            // messa qui sopra, torna Ready (un errore o un offline arrivati intanto valgono di piu').
            // E' la stessa restituzione del GetFrom che rinuncia. Poi questo dispositivo riprova con la
            // rotta nuova, appena e' libero (vedi le note).
            itemToSetBusy?.ReleaseAfterFailedStep(stepToDo.StepId);
            ExecuteAgainWhenFree();
            return;
        }

        // Dopo il gettone, perche' una rinuncia non le lasci: chi aspetta la destinazione, lo stato della
        // missione e il suo movimento di gruppo (risposta 4 del proprietario dell'11/10).
        RemoveDevicesReadyToTransferLu(stepToDo.DestinationItem);

        stepToDo.PathMission.Status = MissionStatusTypes.Run;

        // Se il movimento proviene da uno spostamento di AutomationDeviceBase singolo imposta a vuoto il GroupedMovementId della PathMission
        if (GroupItemId == 0 && BelongedGroup == null) stepToDo.PathMission.GroupedMovementId = Guid.Empty;

        if (stepToDo.DestinationItem == null)
            return;

        var runAsiItem = Navigator.GetDataAsi(stepToDo.SourceItem);
        var fromAsiItem = Navigator.GetDataAsi(stepToDo.SourceItem);
        var toAsiItem = Navigator.GetDataAsi(stepToDo.DestinationItem);

        stepToDo.PathMission.GatewayItem = stepToDo.DestinationItem;

        stepToDo.SourceItem.Status = DeviceStatusTypes.Moving;

        try
        {
            stepToDo.StartStep = DateTime.Now;
            stepToDo.RunningItem = this;

            DeviceConnection?.MoveLuFromAsiToAsi(Navigator.GetMovementRunAsi(this), runAsiItem, fromAsiItem,
                toAsiItem, RunningStepId, stepToDo.PathMission.LuId, stepToDo.PathMission.HandlingMode);
        }
        catch (Exception ex)
        {
            RunningStepId = Guid.Empty;

            Status = DeviceStatusTypes.Ready;
            if (itemToSetBusy != null)
                itemToSetBusy.Status = DeviceStatusTypes.Ready;

            // Il comando non e' partito: il posto sulla destinazione va restituito insieme allo
            // stato, altrimenti resta impegnata per uno step che non arrivera' mai.
            itemToSetBusy?.ReleaseIncomingStep(stepToDo.StepId);

            LogError(ex);
        }
    }

    /// <summary>
    /// Libera questo dispositivo quando lo step di cui era destinazione e' fallito.
    /// </summary>
    /// <param name="stepId">Lo step fallito.</param>
    /// <remarks>
    /// <para>
    /// Se il mittente va in <c>Warning</c>, la destinazione deve tornare disponibile: per accettare
    /// una missione da un altro elemento, per servire chi si e' iscritto nella sua lista, o per
    /// essere attivata da un terzo. Restare occupata per un movimento che non arrivera' mai
    /// significa fermare il tratto.
    /// </para>
    /// <para>
    /// Sono due cose, e servono entrambe: lo <b>stato</b>, che torna <c>Ready</c> — ma solo se e'
    /// la <c>Busy</c> messa da quel trasferimento, perche' un errore o un offline sopraggiunti
    /// valgono di piu' — e il <b>posto</b> preso con la voce <c>D73</c>, che va restituito o
    /// nessun altro potra' consegnare qui.
    /// </para>
    /// <para>
    /// <b>Non</b> tocca lo step del mittente. Il ritentativo di gruppo si riconosce proprio dal
    /// fatto che il mittente ha ancora <c>RunningStepId</c> valorizzato
    /// (<c>ResetStatusBelongedItems</c>), e azzerarlo qui lo spegnerebbe. Quando il mittente
    /// ritenta, riprende il posto con lo stesso identificativo di step.
    /// </para>
    /// </remarks>
    internal void ReleaseAfterFailedStep(Guid stepId)
    {
        if (Status == DeviceStatusTypes.Busy)
            Status = DeviceStatusTypes.Ready;

        ReleaseIncomingStep(stepId);
    }

    /// <summary>
    /// Notifica il completamento di uno step specifico.
    /// </summary>
    /// <param name="idStepDone">L'identificativo univoco dello step completato.</param>
    /// <param name="stepType">Il tipo di step che è stato completato.</param>
    /// <param name="detectedDepth">La profondità rilevata durante l'operazione (opzionale).</param>
    public async Task StepDone(Guid idStepDone, StepTypes stepType, int detectedDepth = -1)
    {
        if (StepExecutor != null) await StepExecutor.StepDone(idStepDone, stepType, detectedDepth);
    }

    /// <summary>
    /// Notifica il completamento di uno step per un gruppo di dispositivi.
    /// </summary>
    /// <param name="groupedMovementId">L'identificativo del movimento raggruppato completato.</param>
    /// <param name="stepType">Il tipo di step che è stato completato per il gruppo.</param>
    /// <param name="detectedDepths">Lista delle profondità rilevate per le diverse LU del gruppo (opzionale).</param>
    public async Task StepDoneGroup(Guid groupedMovementId, StepTypes stepType, List<LuDetectedDepth>? detectedDepths = null)
    {
        if (GroupManager != null) await GroupManager.StepDoneGroup(groupedMovementId, stepType, detectedDepths);
    }

    #endregion Public Methods

    #region Internal Methods

    /// <summary>
    /// Aggiunge una missione per il trasferimento di una cella vuota tra le porte specificate.
    /// </summary>
    /// <param name="sourceDoorCode">Il codice della porta sorgente.</param>
    /// <param name="destinationDoorCode">Il codice della porta di destinazione.</param>
    /// <param name="idWorkstation">L'identificativo della workstation.</param>
    /// <param name="idLuType">L'identificativo del tipo di LU.</param>
    internal async Task AddMissionToEmptyCell(string sourceDoorCode, string? destinationDoorCode, string? idWorkstation,
        decimal idLuType)
    {
        if (string.IsNullOrEmpty(sourceDoorCode) || string.IsNullOrEmpty(destinationDoorCode) ||
            string.IsNullOrEmpty(idWorkstation))
            return;

        if (EmptyCellStatus == EmptyCellStatusTypes.Active)
            return;

        // Q1: il gettone si prende qui, subito dopo la guardia, e fra le due righe non ci deve
        // essere nessun await. I due ingressi di questo metodo non condividono nessun
        // serializzatore - il comando dell'operatore passa da CommandToDo.Execute, il ciclo del
        // dispositivo da ExecuteStep con il semaforo suo - e un'attesa in mezzo lascerebbe passare
        // la guardia a tutt'e due, accodando due missioni di svuotamento sulla stessa cella.
        var previousEmptyCellStatus = EmptyCellStatus;
        EmptyCellStatus = EmptyCellStatusTypes.Active;

        // Lo svuotamento logico non ha un'unita' di carico reale (LuId resta 0 qui sotto), solo un
        // tipo passato a parte: la modalita' di movimentazione va chiesta per quel tipo, non da
        // DescribeLoadUnitAsync. Era DbInfo.GetHandlingMode, che su un guasto rispondeva zero; ora
        // solleva, e il gettone appena preso va restituito prima che l'eccezione risalga.
        Wcs.Wms.Contracts.LoadUnitTypeHandlingMode? handlingMode;
        try
        {
            handlingMode = await Navigator.RequireWarehouse($"GetLoadUnitTypeHandlingModeAsync sul tipo {idLuType}")
                .GetLoadUnitTypeHandlingModeAsync(idLuType);
        }
        catch
        {
            // Il gettone torna al valore che aveva. In Active lo restituisce solo la fine dello
            // svuotamento (AutomationDeviceStepExecutor.Private.cs:893), e finche' e' preso la
            // guardia in cima fa uscire ogni altra chiamata: lasciandolo qui, un guasto transitorio
            // del gestionale fermerebbe lo svuotamento di questo dispositivo fino al riavvio.
            EmptyCellStatus = previousEmptyCellStatus;
            throw;
        }

        EmptyCellIdWorkstation = idWorkstation;
        EmptyCellIdLuType = idLuType;
        EmptyCellSourceDoor = sourceDoorCode;
        EmptyCellDestinationDoor = destinationDoorCode;

        var bufferCellId = await DbInfo.GetCellId(EmptyCellDestinationDoor);

        await CommandToDo.Update(new Command
        {
            Date = DateTime.Now,
            CommandType = Command.CommandTypes.MoveLuAutomatic,
            LuId = 0,
            PickUpDoorCode = sourceDoorCode,
            StorageDoorCode = destinationDoorCode,
            Status = Command.StatusTypes.Done,
            WorkstationId = idWorkstation,
            BufferCellId = bufferCellId
        });

        var newMission = new PathMission
        {
            Date = DateTime.Now,
            LuId = 0,
            SourceItemId = await DbInfo.GetItemId(EmptyCellSourceDoor),
            PickUpDoorCode = EmptyCellSourceDoor,
            DestinationItemId = await DbInfo.GetItemId(EmptyCellDestinationDoor),
            StorageDoorCode = EmptyCellDestinationDoor,
            // .PositionItemId = .SourceItemId
            BufferCellId = bufferCellId,
            LuCode = string.Empty,
            MissionType = MissionTypes.Extraction,
            OperationType = OperationTypes.Outbound,
            StorageDepth = 0,
            IdLuType = idLuType,
            HandlingMode = handlingMode?.HandlingMode ?? 0
        };

        EnqueueLuToPickUp(newMission);
        ORM.PathMission.Add(newMission);
    }

    /// <summary>
    /// Aggiunge una missione di prelievo alla coda basata sul comando del navigator e sui dati dell'item.
    /// </summary>
    /// <param name="navigatorCommand">Il comando del navigator da processare.</param>
    /// <param name="item">I dati dell'item da cui creare la missione.</param>
    /// <param name="type">Il tipo di missione da creare.</param>
    /// <returns>True se la missione è stata aggiunta con successo, false altrimenti.</returns>
    internal async Task<bool> AddMissionToPickUp(Command navigatorCommand, DataRow item, MissionTypes type)
    {
        await CommandToDo.Update(navigatorCommand);

        var newMission = new PathMission
        {
            // Chiave di collegamento a ESTRAZIONI_AUTOMATICE_UDC
            ExtractionQueueId = item.GetValueOrThrow<decimal>("ID_ESTRAZIONE_UDC")
        };
        // -------------------------------------------------------

        // Chiave di collegamento a ASSEGNAZIONE_NOTE
        if (item.GetValueOrThrow<Guid?>("GUID_ASSEGNAZIONE_UDC") != null)
            newMission.GuidAssignmentLu = item.GetValueOrThrow<Guid>("GUID_ASSEGNAZIONE_UDC");
        // -------------------------------------------------------

        // L'identificativo numerico della stessa assegnazione, che il GUID qui sopra non sostituisce:
        // il GUID nomina tutte le righe dell'assegnazione dell'unita' di carico, il numero una riga
        // sola. E' il numero che il gestionale esclude quando rivaluta un'assegnazione gia' diventata
        // missione (MissionQueries.HoldsLoadUnit, excludeAssignmentId): senza, l'esclusione non
        // escludeva mai e ogni assegnazione servita veniva scartata e cancellata al giro successivo.
        if (item.GetValueOrThrow<decimal?>("ID_ASSEGNAZIONE_UDC") is { } noteAssignmentId)
            newMission.LuNoteAssignmentId = noteAssignmentId;
        // -------------------------------------------------------

        // Chiave di collegamento a LAVORAZIONI_NOTE. Dal compito 13 non serve piu' alla chiusura
        // della nota - quella la ricava il gestionale dall'assegnazione - ma governa due decisioni
        // del motore: la missione di gruppo che non mescola lavorazioni diverse
        // (AutomationDeviceGroupManager) e la scelta del polmone di destinazione
        // (AutomationDeviceLuManager). Con lei e' sparito l'avviso «la nota non verra' chiusa»:
        // una riga con l'assegnazione e senza lavorazione adesso si chiude lo stesso.
        if (item.GetValueOrThrow<decimal?>("ID_LAVORAZIONE") != null)
            newMission.NoteProcessingId = item.GetValueOrThrow<decimal>("ID_LAVORAZIONE");
        // -------------------------------------------------------

        newMission.Date = DateTime.Now;
        newMission.LuId = item.GetValueOrThrow<decimal>("ID_UDC");
        newMission.SourceItemId = item.GetValueOrThrow<Guid>("ID_ITEM_SOURCE");

        newMission.PickUpDoorCode = navigatorCommand.PickUpDoorCode;
        newMission.StorageDoorCode = navigatorCommand.StorageDoorCode;

        newMission.IdCommandToDo = navigatorCommand.IdCommandToDo;

        if (type == MissionTypes.ChangeStoreLocation)
        {
            newMission.DestinationItemId = newMission.SourceItemId;
            newMission.RelocateItemId = item.GetValueOrThrow<Guid>("ID_ITEM_RELOCATOR");
        }
        else
        {
            newMission.DestinationItemId = item.GetValueOrThrow<Guid>("ID_ITEM_DESTINATION");
            newMission.BufferCellId = item.GetValueOrThrow<decimal>("ID_CELLA_POLMONE");
        }

        // Compito 10: una sola domanda al gestionale per codice, tipo, modalita' di movimentazione e
        // tara, al posto delle due chiamate che i vecchi setter di LuId/IdLuType facevano in cascata
        // (tolte da li', non si possono chiamare da un setter sincrono) piu' quella esplicita per il
        // solo codice - punto di costruzione "nel dispositivo" del compito.
        var loadUnitDescription = await Navigator.RequireWarehouse($"AddMissionToPickUp sull'unita' di carico {newMission.LuId}")
            .DescribeLoadUnitAsync(newMission.LuId);
        newMission.LuCode = loadUnitDescription?.Code ?? string.Empty;
        newMission.IdLuType = loadUnitDescription?.LoadUnitTypeId ?? 0;
        newMission.HandlingMode = (int)Math.Round(loadUnitDescription?.HandlingMode ?? 0);
        newMission.Tare = loadUnitDescription?.Tare ?? 0;
        newMission.MissionType = type;
        newMission.OperationType = OperationTypes.Outbound;

        newMission.StorageDepth = await DbInfo.GetLuStoringDepth(item.GetValueOrThrow<decimal>("ID_UDC"));

        if (type == MissionTypes.ChangeStoreLocation)
        {
            EnqueueLuToPickUp(newMission);

            ORM.PathMission.Add(newMission);

            await DbInfo.SetExtractionItemQueueProcessed(item.GetValueOrThrow<decimal>("ID_ESTRAZIONE_UDC"), 1);
            return true;
        }

        // Sonda voluta: la missione e' appena creata, non e' ancora nel motore e non ha una rotta pianificata, quindi si calcola
        var checkMissionSteps = newMission.GetMissionStepsToDestination();

        var step = checkMissionSteps.FirstOrDefault(x => x.FromNodeId == ItemGuid);

        if (step is { DestinationItem: not { IsReadyToReceiveLu: true } }) return false;

        EnqueueLuToPickUp(newMission);

        ORM.PathMission.Add(newMission);

        await DbInfo.SetExtractionItemQueueProcessed(item.GetValueOrThrow<decimal>("ID_ESTRAZIONE_UDC"), 1);
        return true;
    }

    /// <summary>
    /// Attiva l'evento di cambiamento dello stato di carico delle LU e aggiorna il database.
    /// </summary>
    internal async Task FireLuLoadStatusChanged()
    {
        try
        {
            await ORM.AutomationDevice.UpdateStatus(this);

            var localLuStatus = DeviceType is DeviceTypes.GroupOfAutomationDevices or DeviceTypes.Storage
                ? LuLoadStatusTypes.None
                : LuLoadStatus;

            LuLoadStatusChanged?.Invoke(this, new AutomationDeviceStatusChangedEventArgs(localLuStatus, Status));
            Navigator.EventSink?.ItemChanged(this);
        }
        catch (Exception ex)
        {
            LogError(ex);
        }
    }

    /// <summary>
    /// Attiva l'evento di cambiamento dello stato del dispositivo e aggiorna il database.
    /// </summary>
    internal async Task FireStatusChanged()
    {
        try
        {
            await ORM.AutomationDevice.UpdateStatus(this);

            var localLuStatus = DeviceType is DeviceTypes.GroupOfAutomationDevices or DeviceTypes.Storage
                ? LuLoadStatusTypes.None
                : LuLoadStatus;

            StatusChanged?.Invoke(this, new AutomationDeviceStatusChangedEventArgs(localLuStatus, Status));
            Navigator.EventSink?.ItemChanged(this);
        }
        catch (Exception ex)
        {
            LogError(ex);
        }
    }

    /// <summary>
    /// Registra un errore nel log del sistema con informazioni dettagliate sul dispositivo e il metodo chiamante.
    /// </summary>
    /// <param name="ex">L'eccezione da registrare.</param>
    /// <param name="callerName">Il nome del metodo chiamante (fornito automaticamente).</param>
    internal void LogError(Exception ex, [CallerMemberName] string callerName = "")
    {
        Navigator.WriteLog(
            $"[ASI {ASI}.{SUBITEM}.{PARTITION}] Error{(callerName != "" ? $" in {callerName}" : "")}: {ex.Message}\nStackTrace: {ex.StackTrace}",
            LogEntryType.Warning);
    }

    /// <summary>
    /// Ricostruisce il processo di rifiuto per un gruppo di dispositivi, rifiutando tutte le LU caricate.
    /// </summary>
    internal async Task RebuildGroupRejection()
    {
        var currentStatus = Status;

        Status = DeviceStatusTypes.Recovering;

        var rejectStatus = true;

        // Snapshot per evitare InvalidOperationException se la collection viene modificata durante iterazione
        var lUsSnapshot = LUsLoaded.OfType<PathMission>().ToList();
        foreach (var lu in lUsSnapshot)
        {
            // Con uno step in volo il rigetto di questa UDC si rimanda, come in RejectTo, prima di
            // toccarle qualunque cosa: rifare la rotta vuol dire cancellare lo step in volo, che rende
            // libero a meta' movimento il dispositivo che lo esegue (decisione del 2026-09-03). Prima
            // gli step si toglievano qui, prima della guardia di RejectTo, che cosi' non vedeva piu'
            // niente (voce 41, corsa 7). La rimozione la fa RejectTo, dopo la sua guardia, con lo
            // stesso resetGroupExecuteStepRunning a falso.
            if (lu.StepInFlight() is { } inFlight)
            {
                Navigator.WriteLog(
                    $"Rigetto di gruppo della missione {lu.MissionId} rimandato: lo step {inFlight.StepId} e' in volo su {inFlight.FromNodeId}",
                    LogEntryType.Information);
                rejectStatus = false;
                continue;
            }

            var currentItemStatus = DeviceStatusTypes.Ready;

            if (lu.PositionItem != null)
            {
                currentItemStatus = lu.PositionItem.Status;
                lu.PositionItem.Status = DeviceStatusTypes.Recovering;
            }

            // Gateway, indirizzi e filtro si tolgono solo con il rigetto che si fa, dentro RejectTo e
            // sotto il lucchetto della rotta: lo sguardo qui sopra e' fuori dal lucchetto, e RejectTo
            // puo' ancora rimandare (uno step partito intanto). Tolti prima, restavano tolti a una
            // missione con lo step in volo (revisione finale della voce 41).
            var resultTest = await lu.RejectTo(RejectionTypes.DefaultNavigatorRejectionItem, false,
                withTheRejection: () =>
                {
                    lu.Gateway = Guid.Empty;
                    lu.SourceAsi = string.Empty;
                    lu.SourceSubitem = string.Empty;
                    lu.SourcePartition = string.Empty;
                    lu.DestinationAsi = string.Empty;
                    lu.DestinationSubitem = string.Empty;
                    lu.DestinationPartition = string.Empty;
                    lu.ActiveFilter = false;
                });

            if (resultTest)
                lu.StatusDescription = "User requested to reject LU";

            if (rejectStatus && !resultTest)
                rejectStatus = false;

            if (lu.PositionItem != null) lu.PositionItem.Status = currentItemStatus;
        }

        if (rejectStatus)
        {
            Status = DeviceStatusTypes.Ready;
            await Task.Delay(500);
            GroupExecuteStepRunning = false;
        }
        else
        {
            Status = currentStatus;
        }
    }

    /// <summary>
    /// Rimuove uno step dalla collezione e da tutti i dispositivi appartenenti al gruppo.
    /// </summary>
    /// <param name="value">Lo step da rimuovere.</param>
    /// <param name="resetGroupExecuteStepRunning">Indica se reimpostare lo stato di esecuzione del gruppo.</param>
    internal void RemoveStep(MissionStep? value, bool resetGroupExecuteStepRunning = true)
    {
        if (value == null) return;

        if (DeviceType != DeviceTypes.GroupOfAutomationDevices && RunningStepId == value.StepId)
            RunningStepId = Guid.Empty;

        Collections.MissionSteps.Instance.Remove(value);

        // The step left the route: a change of the operator waiting for the end of the mission's step
        // looks again (item 54, decision 4). Only a nudge: it runs on the thread pool.
        Api.PendingMissionChanges.Nudge(value.MissionId);

        // Snapshot della collection per thread-safety
        var belongedSnapshot = BelongedItems.ToList();
        foreach (var item in belongedSnapshot)
            item?.RemoveStep(value);

        if (resetGroupExecuteStepRunning)
            CheckGroupExecuteStepRunning();
    }

    /// <summary>
    /// Rimuove tutti gli step associati a un movimento raggruppato specifico.
    /// </summary>
    /// <param name="groupedMovementId">L'identificativo del movimento raggruppato.</param>
    /// <remarks>
    /// <b>Il gettone si azzera prima della cancellazione</b>, come in <see cref="RemoveStep"/>
    /// sedici righe sopra: il getter di <c>RunningStepId</c> risponde <c>Guid.Empty</c> quando lo
    /// step non e' piu' in <c>MissionSteps</c>, quindi azzerandolo dopo il confronto era falso,
    /// l'assegnazione non avveniva, e il setter non cercava il prenotato da liberare. Qui era
    /// spesso inerte — nel caso di gruppo <c>RunningStepId</c> vale il <c>groupedMovementId</c>,
    /// che di norma non e' la chiave di nessuna riga di <c>MissionSteps</c> — ma l'asimmetria fra
    /// due metodi adiacenti che fanno la stessa cosa e' un difetto per conto suo.
    /// </remarks>
    internal void RemoveStepsByGroupedMovementId(Guid groupedMovementId)
    {
        if (groupedMovementId == Guid.Empty)
            return;

        if (RunningStepId == groupedMovementId)
            RunningStepId = Guid.Empty;

        Collections.MissionSteps.Instance.RemoveAll(x =>
            x.SourceItem != null && x.SourceItem.ItemGuid == ItemGuid &&
            x.GroupedMovementId == groupedMovementId);

        // Snapshot della collection per thread-safety
        var belongedSnapshot = BelongedItems.ToList();
        foreach (var item in belongedSnapshot)
            item?.RemoveStepsByGroupedMovementId(groupedMovementId);
    }

    /// <summary>
    /// Reimposta lo stato di tutti i dispositivi appartenenti al gruppo che sono in stato di warning o errore.
    /// </summary>
    internal async Task ResetStatusBelongedItems()
    {
        // Snapshot per thread-safety
        var itemsToReset = BelongedItems.Where(x =>
                     x is { Status: DeviceStatusTypes.Warning or DeviceStatusTypes.Error }).ToList();
        foreach (var item in itemsToReset)
        {
            item?.Status = DeviceStatusTypes.Ready;
        }

        if (RunningStepId != Guid.Empty)
        {
            var listOfSteps = Collections.MissionSteps.Instance.Where(x => RunningStepId == x.GroupedMovementId)
                .ToList();

            foreach (var step in listOfSteps)
            {
                if (step.SourceItem is { Status: DeviceStatusTypes.Warning or DeviceStatusTypes.Error })
                    step.SourceItem.Status = DeviceStatusTypes.Ready;

                if (step.DestinationItem is { Status: DeviceStatusTypes.Warning or DeviceStatusTypes.Error })
                    step.DestinationItem.Status = DeviceStatusTypes.Ready;
            }

            await Task.Delay(200);
        }

        switch (Status)
        {
            case DeviceStatusTypes.Warning:
            case DeviceStatusTypes.Error:
                {
                    Status = DeviceStatusTypes.Ready;

                    if (RunningStepId != Guid.Empty && MissionSteps.Count != 0)
                    {
                        // Le graffe sono necessarie: senza, l'else si legava all'if interno su
                        // StepExecutor e non a quello su GroupedMoveLuTo, quindi il ramo dei
                        // movimenti singoli era irraggiungibile.
                        if (GroupedMoveLuTo)
                        {
                            if (StepExecutor != null) await StepExecutor.RepeatGroupedSteps();
                        }
                        else
                        {
                            if (StepExecutor != null) await StepExecutor.RepeatSingleSteps();
                        }
                    }
                    else
                    {
                        GroupExecuteStepRunning = false;
                    }

                    break;
                }
            case DeviceStatusTypes.Ready:
                GroupExecuteStepRunning = false;
                break;
            case DeviceStatusTypes.Moving:
            case DeviceStatusTypes.Loading:
            case DeviceStatusTypes.Unloading:
            case DeviceStatusTypes.Busy:
            case DeviceStatusTypes.Recovering:
            case DeviceStatusTypes.OffLine:
            case DeviceStatusTypes.Locked:
            default:
                break;
        }
    }

    /// <summary>
    /// Avvia i processi attivi appropriati basati sul tipo di dispositivo.
    /// </summary>
    internal void StartActiveProcesses()
    {
        switch (DeviceType)
        {
            case DeviceTypes.CircularDistributor:
                if (GroupItemId == 0) StartProcessCircularDistributorExecuteStep();
                break;
            case DeviceTypes.GroupOfAutomationDevices:
                if (GroupItemId > 0) GroupManager?.StartProcessGroupExecuteStep();
                break;
            case DeviceTypes.None:
                return;
            default:
                if (IsLinkedToStorage && BelongedGroup == null) StorageManager?.StartProcessExtractionLu();
                break;
        }
    }

    /// <summary>
    /// Avvia il processo di esecuzione step per un distributore circolare.
    /// </summary>
    internal void StartProcessCircularDistributorExecuteStep()
    {
        if (_cancellationCircularDistTokenSource != null) return;

        _cancellationCircularDistTokenSource = new CancellationTokenSource();
        _cancelCircularDistToken = _cancellationCircularDistTokenSource.Token;

        // Il try sta DENTRO il while, non intorno: con il catch fuori dal ciclo la prima eccezione
        // spegneva la distribuzione circolare di questo dispositivo per il resto della vita del
        // processo, e in silenzio - il Task non lo osservava nessuno. E' lo stesso difetto della
        // voce B20, riparato a suo tempo su AutomationDeviceStorageManager e non qui.
        Task.Run(async () =>
        {
            while (!_cancelCircularDistToken.IsCancellationRequested)
            {
                try
                {
                    // StepExecutor e' costruito da Initialize e non torna mai nullo: la guardia
                    // sostituisce la forma "await x?.M()!", che sollevava NullReferenceException
                    // invece di non fare nulla.
                    if (StepExecutor != null) await StepExecutor.StartCircularDistribution();
                }
                catch (OperationCanceledException)
                {
                    // Arresto del processo: non e' un errore.
                    break;
                }
                catch (Exception ex)
                {
                    LogError(ex);
                }

                try
                {
                    await Task.Delay(1000, _cancelCircularDistToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, _cancelCircularDistToken);
    }

    /// <summary>
    /// Trova lo step da eseguire per una missione specifica su questo dispositivo.
    /// </summary>
    /// <param name="value">La missione di percorso per cui trovare lo step.</param>
    /// <returns>Lo step da eseguire o null se non trovato.</returns>
    internal MissionStep? StepToDoSource(PathMission? value)
    {
        if (Collections.MissionSteps.Instance.Count == 0 || value == null)
            return null;

        return Collections.MissionSteps.Instance.FirstOrDefault(x =>
            x.MissionId == value.MissionId &&
            x.FromNodeId == ItemGuid);
    }

    /// <summary>
    /// Interrompe tutti i processi attivi del dispositivo.
    /// </summary>
    internal void StopActiveProcesses()
    {
        StorageManager?.CancellationExtractionTokenSource?.Cancel();
        if (StorageManager != null) StorageManager.CancellationExtractionTokenSource = null;
        GroupManager?.CancellationGroupExStepTokenSource?.Cancel();
        if (GroupManager != null) GroupManager.CancellationGroupExStepTokenSource = null;
        _cancellationCircularDistTokenSource?.Cancel();
        _cancellationCircularDistTokenSource = null;
    }

    /// <summary>
    /// Il dispositivo va in errore e aspetta l'operatore, o l'operatore lo rimette in marcia: le
    /// conferme della cella delle missioni a bordo si buttano (risposta 6 del proprietario dell'8/10,
    /// <c>PathMission.ForgetStorageCellConfirmation</c>), e il deposito dopo la ripartenza richiede al
    /// gestionale. A bordo sono le missioni date (quelle degli step in errore), quelle sul dispositivo
    /// (su un gruppo, sui suoi membri), e, per un membro, quelle del suo gruppo, che l'errore del membro
    /// manda in errore con lui.
    /// </summary>
    /// <param name="inError">Le missioni degli step in errore; nessuna alla ripartenza.</param>
    /// <remarks>
    /// Lo chiamano l'errore (<c>StepError</c>, <c>StepErrorGroup</c>) e la macchina a stati quando il
    /// dispositivo esce dall'errore (<c>AutomationDeviceStateMachine.LeaveTheError</c>): la conferma
    /// che il navigatore da' mentre il dispositivo aspetta cade alla ripartenza. Prima la conferma
    /// restava: una cella confermata prima o durante l'errore faceva partire il deposito alla
    /// ripartenza, ore dopo, senza chiedere se il gestionale teneva ancora la cella. Nessuna chiamata
    /// al gestionale qui: solo il lucchetto di ogni missione, per il tempo di buttare la conferma.
    /// </remarks>
    internal void ForgetTheCellConfirmationsOnBoard(IEnumerable<PathMission?> inError)
    {
        var onBoard = LUsLoaded.Concat(inError);
        if (BelongedGroup is { } group) onBoard = onBoard.Concat(group.LUsLoaded);

        foreach (var mission in onBoard.OfType<PathMission>().Distinct())
            mission.ForgetStorageCellConfirmation();
    }

    /// <summary>
    /// Verifica dove allocare un gruppo di LU basandosi sulla strategia di allocazione configurata.
    /// </summary>
    /// <param name="lUtoAllocate">Lista delle LU da allocare.</param>
    /// <remarks>
    /// Il gruppo lavora sulle missioni dei suoi membri - prende i selettori, ne riscrive porta e
    /// destinazione, ne ricostruisce la rotta - dal suo ciclo, che nessun semaforo di esecuzione
    /// esclude. Per questo il lavoro si conta su ogni missione come l'avvio di uno step
    /// (<c>PathMission.TryBeginStepStart</c>, compito 3 del piano delle missioni): una missione che
    /// sta cambiando destinazione per mano dell'operatore resta fuori dal giro, e mentre il gruppo ci
    /// lavora il cambio non comincia.
    /// </remarks>
    internal async Task VerifyWhereAllocateLuGrouped(List<PathMission?> lUtoAllocate)
    {
        var started = PathMission.TryBeginStepStarts(lUtoAllocate.Concat(LUsLoaded),
            $"{nameof(VerifyWhereAllocateLuGrouped)} {ItemGuid}");

        try
        {
            await VerifyWhereAllocateLuGroupedAsync(lUtoAllocate, started);
        }
        finally
        {
            foreach (var mission in started)
                mission.EndStepStart();
        }
    }

    /// <summary>Il corpo di <see cref="VerifyWhereAllocateLuGrouped"/>, sulle sole missioni contate.</summary>
    /// <param name="lUtoAllocate">Lista delle LU da allocare.</param>
    /// <param name="started">Le missioni su cui il lavoro si e' contato: le altre restano fuori.</param>
    private async Task VerifyWhereAllocateLuGroupedAsync(List<PathMission?> lUtoAllocate, List<PathMission> started)
    {
        var scope = new GroupedStorageScope(this, (resource, failure) =>
            Navigator.WriteLog(
                $"Grouped allocation cleanup failed for {resource} on {ItemGuid}: {failure}",
                LogEntryType.Error));
        Exception? primaryFailure = null;

        try
        {
            Status = DeviceStatusTypes.Recovering;

            // Prima, e da sole, le copie che hanno lasciato la porta: il giro della modalita' le guarda
            // al prossimo passaggio, se serve ancora.
            var copies = lUtoAllocate.OfType<PathMission>()
                .Where(lu => lu.LeftACopiedDoor && started.Contains(lu) &&
                             GroupManager?.CopiedDoorRetries.IsWaiting(lu.MissionId) != true)
                .Distinct().ToList();

            if (copies.Count > 0)
            {
                await RedoTheCopiedDoorsAsync(lUtoAllocate, copies, started, scope);
                return;
            }

            switch (LookWhereAllocateLu)
            {
                case LookWhereAllocateLuTypes.BuildRouteToStoreByIdLuType:
                    {
                        var idTypes = new List<LuSearchLocationByTypeLu>();

                        foreach (var lu in lUtoAllocate)
                        {
                            if (lu?.PositionItem == null || !started.Contains(lu)) continue;

                            var currentItemStatus = lu.PositionItem.Status;
                            scope.TrackPosition(lu.PositionItem);
                            lu.PositionItem.Status = DeviceStatusTypes.Recovering;

                            var def = idTypes.Where(x => lu.IdLuType == x.LuTypeId);

                            var luSearchLocationByTypeLus = def as LuSearchLocationByTypeLu[] ?? def.ToArray();

                            // La destinazione scelta dall'operatore non prende il selettore del suo
                            // tipo e non ne lascia uno alle altre (FindNewStoreLocationGrouped fa lo
                            // stesso): la sua rotta la costruisce FindWhereStoreLu verso di lei
                            // (decisione del proprietario del 30/9), e non e' la cella che il
                            // gestionale ha dato a quel tipo di UDC. Il segno non cambia durante il
                            // giro, perche' il lavoro e' contato sulla missione e il cambio
                            // dell'operatore non comincia (PathMission.TryBeginStepStart): la
                            // guardia di scrittura resta come rete, e senza il segno non scrive righe.
                            //
                            // Non lo prende neanche la missione che ha perso la cella (StorageCellLost):
                            // un'altra gliela prenota il navigatore (MissionGateway.KeepTheStorageCellAsync),
                            // con le celle rifiutate dal PLC escluse (risposta 7 dell'8/10 sera), e
                            // FindWhereStoreLu qui sotto non le da' una porta. Prima il selettore glielo
                            // dava: una copia rifiutata da sola, che con il gestionale muto lascia la porta
                            // come una cella persa sua (MissionGateway.LeaveTheRefusedCell), riprendeva
                            // dal giro del gruppo arrivato prima del navigatore la porta della missione da
                            // cui aveva copiato, che la tiene ancora, e depositava di nuovo nella cella
                            // appena rifiutata (terzo giro di correzioni del compito 13).
                            //
                            // Item 54 (answer 8 of the owner of 13/10): neither does a mission bound for a
                            // device that is not a storage (PathMission.KeepsItsDestination). It keeps its
                            // destination, FindWhereStoreLu below builds the route to it, and its
                            // destination is not a cell for its load unit type.
                            var selector = luSearchLocationByTypeLus.FirstOrDefault();

                            // Item 54, decision 5: nor does a mission on the relocator the operator sent it
                            // to, with a choice of what the relocator does with it (RelocatorArrivalApplies):
                            // that choice decides, in FindWhereStoreLu below.
                            if (selector != null && !lu.OperatorDestination && !lu.StorageCellLost &&
                                !lu.KeepsItsDestination && !lu.RelocatorArrivalApplies && lu.UnlessChosenByOperator(() =>
                                {
                                    lu.StorageDoorCode = selector.StorageDoorCode;
                                    lu.DestinationItemId = selector.DestinationItemId;

                                    lu.Status = MissionStatusTypes.Run;
                                }))
                            {
                                await lu.BuildPathToDestination();
                            }
                            else if (await Navigator.FindWhereStoreLu(lu, lu.PositionItem, LookWhereAllocateLu) ==
                                     RouteBuildStatusTypes.Available)
                            {
                                if (!lu.OperatorDestination && !lu.KeepsItsDestination)
                                    idTypes.Add(new LuSearchLocationByTypeLu
                                    {
                                        LuTypeId = lu.IdLuType,
                                        StorageDoorCode = lu.StorageDoorCode ?? string.Empty,
                                        DestinationItemId = lu.DestinationItemId
                                    });
                            }

                            // Non cambia la fotografia osservata dalla missione successiva; il
                            // finally serve quando il giro non raggiunge questo ripristino.
                            lu.PositionItem.Status = currentItemStatus;
                        }

                        break;
                    }

                case LookWhereAllocateLuTypes.BuildRouteToStore:
                case LookWhereAllocateLuTypes.BuildRouteToStoreForcing:
                    {
                        foreach (var lu in LUsLoaded.Where(x => x != null && started.Contains(x) && !string.IsNullOrEmpty(x.StorageDoorCode)))
                            await Navigator.FindWhereStoreLu(lu, lu?.PositionItem, LookWhereAllocateLu);

                        break;
                    }

                case LookWhereAllocateLuTypes.BuildRouteToDifferentStoreLocation:
                    {
                        foreach (var lu in LUsLoaded.Where(x => x != null && started.Contains(x) && string.IsNullOrEmpty(x.StorageDoorCode)))
                            await Navigator.FindWhereStoreLu(lu, lu?.PositionItem, LookWhereAllocateLu);

                        break;
                    }
            }
        }
        catch (Exception failure)
        {
            primaryFailure = failure;
            throw;
        }
        finally
        {
            await scope.RestoreAsync(primaryFailure);
        }
    }

    #endregion Internal Methods

    #region Private Methods

    /// <summary>
    /// Le copie che hanno lasciato la porta (<c>PathMission.LeftACopiedDoor</c>) si rifanno con la
    /// ricerca per tipo del gruppo (risposta 8 del proprietario dell'8/10), qualunque sia la modalita'
    /// di deposito del gruppo: le copie le fa anche la ricerca di una cella nuova dopo un rifiuto del
    /// PLC (<c>AutomationDeviceStorageManager.FindNewStoreLocationGrouped</c>), che cerca per tipo in
    /// ogni modalita'. La copia prende la porta di una missione del gruppo dello stesso tipo con una cella
    /// prenotata per lei (<c>StorageCellReserved</c>, non dell'operatore e non persa) - di solito quella
    /// da cui aveva copiato, nella sua cella nuova - e senza una cerca la sua cella dal gestionale,
    /// lasciando il selettore alle copie dopo di lei, con la regola della ricerca per tipo.
    /// </summary>
    /// <param name="lUtoAllocate">Le missioni del giro: quelle con la cella danno il selettore.</param>
    /// <param name="copies">Le copie da rifare, gia' contate (<c>PathMission.TryBeginStepStart</c>).</param>
    /// <param name="started">Le missioni su cui il lavoro si e' contato: solo loro danno il selettore.</param>
    /// <param name="scope">Il ripristino degli stati delle posizioni, come per il resto del giro.</param>
    /// <remarks>
    /// <para>
    /// Prima la copia senza porta la rifaceva solo il giro della modalita' per tipo di UDC: con la
    /// modalita' <c>No</c> nessuno la cercava, e con le altre il ciclo del gruppo tornava a ogni
    /// passata nella ricerca della sua modalita', che non la guardava, e non arrivava mai ai
    /// trasferimenti e alle estrazioni (primo giro di correzioni del compito 12).
    /// </para>
    /// <para>
    /// Le missioni che danno il selettore non si toccano e non si richiedono al gestionale: la cella
    /// gliel'ha appena prenotata il navigatore, e il loro deposito chiede comunque la conferma
    /// (<c>PathMission.MayStartTheDeposit</c>), con le copie che aspettano con loro
    /// (<c>AutomationDeviceStepExecutor.BeginStepStarts</c>). Prima delle altre missioni, non nell'ordine
    /// del gruppo: una copia che venisse prima cercherebbe una cella sua invece di tornare con lei.
    /// </para>
    /// <para>
    /// Il selettore lo danno solo le missioni contate (<paramref name="started"/>): per tutto il giro
    /// ne' il navigatore ne' l'operatore le possono fermare (<c>PathMission.TryBeginOperatorChange</c>), e
    /// la loro porta, letta una volta prima delle attese del giro (la rotta, la domanda al gestionale
    /// di una copia), resta quella. Una missione che resta fuori la sta cambiando qualcuno: il
    /// navigatore che ne ha visto persa la cella e ne sta lasciando la porta, con
    /// <c>MissionGateway.StopTheCopiesOfTheDoor</c> che non vede la copia senza porta. Prima il
    /// selettore veniva anche da lei: la copia prendeva la porta che la missione stava lasciando, e ci
    /// depositava da sola, senza segno ne' una missione con quella porta che la trattenesse (secondo
    /// giro di correzioni del compito 12). Senza una missione contata del suo tipo la copia cerca la
    /// sua cella, «altrove» come la risposta 8 permette.
    /// </para>
    /// <para>
    /// Fa eccezione la copia di una missione del suo tipo che ha perso la cella (<c>StorageCellLost</c>) e
    /// a cui il navigatore ne sta rifacendo una: la copia non cerca una cella sua, aspetta con il freno, e
    /// al giro dopo la cella nuova prende la porta di lei. La missione attesa e' una qualunque missione della
    /// configurazione del suo tipo, sul gruppo, verso un magazzino, con la cella persa: di solito quella
    /// che, con il gestionale muto dopo un rifiuto del PLC, ha lasciato la porta rifiutata con le sue copie
    /// (<c>MissionGateway.LeaveTheRefusedCell</c>), ma anche una copia rifiutata da sola che l'ha lasciata
    /// come una cella persa sua, o una missione che ha perso la cella dopo un cambio rifiutato
    /// (<c>MissionGateway.LoseTheCell</c>). Non si guarda chi aveva prestato la porta alla copia: il
    /// motore non lo ricorda, e la copia prende comunque la porta del primo del suo tipo che ha la cella.
    /// Il navigatore le rifa' quando il gestionale risponde, e il ciclo del gruppo, ripartito con
    /// l'operatore, poteva arrivare prima di lui. Senza l'attesa la
    /// copia si prenotava una cella sua e depositava da sola, come se avesse perso la cella lei (primo giro
    /// di correzioni del compito 13). Le copie fermate dal navigatore con la missione
    /// (<c>MissionGateway.KeepTheStorageCellAsync</c>) non arrivano qui finche' lei non ha la cella nuova.
    /// </para>
    /// <para>
    /// Una copia che non trova una porta prende il freno del gruppo
    /// (<c>AutomationDeviceGroupManager.CopiedDoorRetries</c>), con una riga di avviso al primo
    /// fallimento e poi di rado: finche' il freno non scade il ciclo del gruppo non torna nella ricerca
    /// per lei, e va avanti ai trasferimenti e alle estrazioni. Prima tornava li' a ogni passata, in
    /// ogni modalita', chiedeva di nuovo al gestionale e non andava avanti, senza una riga (revisione
    /// finale del secondo giro).
    /// </para>
    /// </remarks>
    private async Task RedoTheCopiedDoorsAsync(List<PathMission?> lUtoAllocate, List<PathMission> copies,
        List<PathMission> started, GroupedStorageScope scope)
    {
        var idTypes = lUtoAllocate.OfType<PathMission>()
            .Where(lu => !copies.Contains(lu) && started.Contains(lu) &&
                         lu is { OperatorDestination: false, StorageCellLost: false, StorageCellReserved: true } &&
                         !string.IsNullOrEmpty(lu.StorageDoorCode) &&
                         lu.DestinationItem is { DeviceType: DeviceTypes.Storage })
            .Select(lu => new LuSearchLocationByTypeLu
            {
                LuTypeId = lu.IdLuType,
                StorageDoorCode = lu.StorageDoorCode ?? string.Empty,
                DestinationItemId = lu.DestinationItemId
            })
            .ToList();

        foreach (var copy in copies)
        {
            if (copy.PositionItem is not { } position) continue;

            var currentItemStatus = position.Status;
            scope.TrackPosition(position);
            position.Status = DeviceStatusTypes.Recovering;

            var selector = idTypes.FirstOrDefault(x => x.LuTypeId == copy.IdLuType);

            // Una missione del suo tipo ha perso la cella e il navigatore gliene rifa' una: la copia la
            // aspetta, senza cercare una cella sua, e prende la sua porta quando l'ha (vedi le note).
            var lost = selector == null
                ? lUtoAllocate.OfType<PathMission>().FirstOrDefault(lu =>
                    !ReferenceEquals(lu, copy) && lu is { OperatorDestination: false, StorageCellLost: true } &&
                    lu.IdLuType == copy.IdLuType && lu.DestinationItem is { DeviceType: DeviceTypes.Storage })
                : null;

            if (selector != null && copy.UnlessChosenByOperator(() =>
                {
                    copy.StorageDoorCode = selector.StorageDoorCode;
                    copy.DestinationItemId = selector.DestinationItemId;
                    copy.Status = MissionStatusTypes.Run;
                }))
            {
                copy.CopiedDoorRedone();
                await copy.BuildPathToDestination();
                Navigator.WriteLog(
                    $"{nameof(VerifyWhereAllocateLuGrouped)} {ItemGuid}: la missione {copy.MissionId}, che aveva lasciato la porta copiata, prende la porta {selector.StorageDoorCode} del suo tipo di UDC",
                    LogEntryType.Information);
            }
            else if (selector == null && lost == null &&
                     await Navigator.FindWhereStoreLu(copy, position, LookWhereAllocateLuTypes.BuildRouteToStoreByIdLuType) ==
                     RouteBuildStatusTypes.Available)
            {
                copy.CopiedDoorRedone();
                if (!copy.OperatorDestination && !string.IsNullOrEmpty(copy.StorageDoorCode))
                    idTypes.Add(new LuSearchLocationByTypeLu
                    {
                        LuTypeId = copy.IdLuType,
                        StorageDoorCode = copy.StorageDoorCode,
                        DestinationItemId = copy.DestinationItemId
                    });
            }

            position.Status = currentItemStatus;

            if (!copy.LeftACopiedDoor)
            {
                GroupManager?.CopiedDoorRetries.Forget(copy.MissionId);
                continue;
            }

            // Nessuna porta: il freno, e il ciclo del gruppo va avanti senza di lei fino alla scadenza.
            // La riga esce al primo fallimento e poi di rado, come per la riverifica della cella.
            var attempts = GroupManager?.CopiedDoorRetries.RegisterFailure(copy.MissionId) ?? 1;
            if (lost != null)
            {
                if (attempts == 1 || attempts % 30 == 0)
                    Navigator.WriteLog(
                        $"{nameof(VerifyWhereAllocateLuGrouped)} {ItemGuid}: la missione {copy.MissionId}, che aveva lasciato la porta copiata, aspetta la missione {lost.MissionId} del suo tipo, che ha perso la cella e a cui il navigatore ne rifa' una ({attempts} giri): il gruppo va avanti con trasferimenti ed estrazioni",
                        LogEntryType.Information);
            }
            else if (attempts == 1 || attempts % 30 == 0)
                Navigator.WriteLog(
                    $"{nameof(VerifyWhereAllocateLuGrouped)} {ItemGuid}: la missione {copy.MissionId}, che aveva lasciato la porta copiata, non ha trovato una porta ({attempts} ricerche): il gruppo va avanti con trasferimenti ed estrazioni, e la ricerca si riprova quando scade il freno",
                    LogEntryType.Warning);
        }
    }

    /// <summary>
    /// Verifica e reimposta lo stato di esecuzione del gruppo quando non ci sono più step da eseguire.
    /// Questo metodo viene chiamato per gruppi di dispositivi di automazione per assicurare che lo stato
    /// sia corretto quando tutte le missioni sono state completate.
    /// </summary>
    private void CheckGroupExecuteStepRunning()
    {
        if (!(DeviceType == DeviceTypes.GroupOfAutomationDevices && MissionSteps.Count == 0)) return;

        RunningStepId = Guid.Empty;
        GroupExecuteStepRunning = false;

        if (Status != DeviceStatusTypes.OffLine)
            Status = DeviceStatusTypes.Ready;
    }

    /// <summary>
    /// Libera le risorse gestite e non gestite utilizzate dal dispositivo di automazione.
    /// Questo metodo implementa il pattern Dispose standard per garantire una corretta pulizia
    /// delle risorse quando il dispositivo viene distrutto.
    /// </summary>
    /// <param name="disposing">True se chiamato da Dispose(), false se chiamato dal finalizzatore</param>
    private void Dispose(bool disposing)
    {
        if (_disposedValue) return;
        if (disposing)
        {
            _cancellationDisposingTokenSource?.Cancel();

            StopActiveProcesses();

            GroupManager?.Dispose();
            StorageManager?.Dispose();
            _stateMachine?.Dispose();

            // Non smaltire il semaforo qui: Cancel risveglia un ExecuteStep ancora dentro il
            // semaforo (la sua esecuzione, che attende col token di chiusura), il cui finally deve
            // poter restituire il gettone; la pausa di ExecuteStep non c'entra piu', si fa prima della
            // presa (voce 41, compito 1). Dispose e Release in
            // concorrenza producono ObjectDisposedException. Il semaforo resta confinato a questo
            // dispositivo ormai irraggiungibile e viene raccolto insieme ad esso.

            Navigator.StatusChanged -= Navigator_StatusChanged;
        }

        _disposedValue = true;
    }

    /// <summary>
    /// Esegue il core dell'operazione di step utilizzando la strategia di esecuzione appropriata.
    /// Questo metodo seleziona e applica la strategia corretta basata sul tipo e configurazione del dispositivo.
    /// </summary>
    /// <param name="cancellationToken">Token di cancellazione per interrompere l'operazione se necessario</param>
    /// <returns>Un task che rappresenta l'operazione asincrona di esecuzione dello step</returns>
    private async Task ExecuteStepCoreAsync(CancellationToken cancellationToken)
    {
        var strategy = GetExecutionStrategy();
        await strategy.ExecuteAsync(cancellationToken);
    }

    /// <summary>
    /// Determina e restituisce la strategia di esecuzione appropriata per questo dispositivo.
    /// La strategia viene selezionata in base al tipo di dispositivo e alla sua appartenenza a gruppi.
    /// </summary>
    /// <returns>L'istanza della strategia di esecuzione da utilizzare per questo dispositivo</returns>
    /// <remarks>
    /// L'istanza viene riusata fra una chiamata e l'altra e ricreata solo quando cambia il tipo
    /// di strategia richiesta (appartenenza a un gruppo o tipo di dispositivo): cosi' lo stato
    /// che la strategia tiene fra due esecuzioni sopravvive al giro successivo.
    /// </remarks>
    private IExecutionStrategy GetExecutionStrategy()
    {
        if (BelongedGroup != null)
            return _executionStrategy as Grouped ?? KeepExecutionStrategy(new Grouped(this));

        return DeviceType switch
        {
            DeviceTypes.GroupOfAutomationDevices =>
                _executionStrategy as GroupDevice ?? KeepExecutionStrategy(new GroupDevice(this)),
            DeviceTypes.CircularDistributor =>
                _executionStrategy as CircularDistributor ?? KeepExecutionStrategy(new CircularDistributor(this)),
            _ => _executionStrategy as Standard ?? KeepExecutionStrategy(new Standard(this))
        };
    }

    /// <summary>
    /// Memorizza la strategia di esecuzione appena costruita e la restituisce.
    /// </summary>
    /// <param name="value">La strategia da conservare per le esecuzioni successive</param>
    /// <returns>La stessa strategia ricevuta</returns>
    private IExecutionStrategy KeepExecutionStrategy(IExecutionStrategy value)
    {
        _executionStrategy = value;
        return value;
    }

    ///<summary>
    /// Inizializza tutti i componenti e manager necessari per il funzionamento del dispositivo di automazione.
    /// Questo metodo configura i token di cancellazione, i manager interni, registra gli eventi
    /// e avvia i processi di esecuzione automatica degli step.
    /// </summary>
    private void Initialize()
    {
        StepExecutor = new AutomationDeviceStepExecutor(this);
        LuManager = new AutomationDeviceLuManager(this);
        GroupManager = new AutomationDeviceGroupManager(this);
        _connectionManager = new AutomationDeviceConnectionManager(this);
        _errorHandler = new AutomationDeviceErrorHandler(this);
        StorageManager = new AutomationDeviceStorageManager(this);
        _stateMachine = new AutomationDeviceStateMachine(this);

        _cancellationDisposingTokenSource = new CancellationTokenSource();
        CancelDisposingToken = _cancellationDisposingTokenSource.Token;

        Navigator.StatusChanged += Navigator_StatusChanged;
    }

    /// <summary>
    /// Legge la configurazione del dispositivo dal database.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Stava dentro <see cref="Initialize"/>, cioe' dentro il costruttore: costruire un dispositivo
    /// faceva una query e lanciava un'esecuzione. Con
    /// <c>PathDevices.Add(Guid)</c> a costruire il dispositivo quando manca, caricare il grafo
    /// significava una query per nodo, dentro la funzione di fabbrica di un
    /// <c>ConcurrentDictionary</c> — che puo' essere invocata piu' di una volta e scartare i
    /// perdenti. Separando le tre fasi un dispositivo si puo' costruire in memoria, e quindi
    /// collaudare.
    /// </para>
    /// <para>
    /// L'avvio dei processi era invece un effetto collaterale dei setter di <c>DeviceType</c> e
    /// <c>GroupItemId</c>, chiamati durante la lettura della riga: a seconda dell'ordine delle
    /// colonne si poteva avviare un ciclo su un dispositivo non ancora configurato del tutto. Ora
    /// si avvia una volta sola, qui, a configurazione completa.
    /// </para>
    /// <para>
    /// Si vedano le voci <c>D62</c> e <c>D63</c> del registro in
    /// <c>WCS/doc/debito-tecnico.md</c>.
    /// </para>
    /// </remarks>
    internal void Load()
    {
        bool trovato;

        // Get popola il dispositivo assegnandone le proprieta' una per una, e ogni setter passa da
        // SetProperty, che riscrive l'intera riga: 59 UPDATE per dispositivo a ogni caricamento.
        // Era il compito di Navigator.LoadingStatus, che pero' non veniva mai assegnato. La guardia
        // e' per istanza e non statica, cosi' due caricamenti concorrenti non si spengono a vicenda
        // le scritture. Si veda la voce B7 del registro in WCS/doc/debito-tecnico.md.
        _loadingFromDatabase = true;

        try
        {
            trovato = ORM.AutomationDevice.Get(this);
        }
        finally
        {
            _loadingFromDatabase = false;
        }

        // Riga assente sul database: si scrive con i valori correnti. Fuori dalla guardia, perche'
        // questa e' una scrittura voluta e non un effetto collaterale della lettura.
        if (!trovato) ORM.AutomationDevice.Create(this);
    }

    /// <summary>
    /// Applica una configurazione arrivata dall'API: i setter non scrivono uno per uno
    /// (<c>_loadingFromDatabase</c> li mette a tacere). Lo stato dal vivo non si tocca.
    /// </summary>
    /// <remarks>
    /// Il metodo <b>non</b> scrive la riga: la scrittura la fa, e la <b>attende</b>, la facciata
    /// <see cref="Api.PlantQueries.UpsertItemConfigurationAsync"/>. Finche' era qui passava da
    /// <c>ORM.AutomationDevice.Update</c>, cioe' da <c>EnqueueOrWait</c>, che non e' attendibile:
    /// la facciata attendeva una scrittura vuota come barriera e rispondeva <c>204</c> anche
    /// quando il lotto era fallito e la riga vera era finita fra le scritture scartate.
    /// </remarks>
    internal void ApplyConfiguration(PlantItem row)
    {
        _loadingFromDatabase = true;
        try
        {
            PlantStore.ApplyConfiguration(row, this);
        }
        finally
        {
            _loadingFromDatabase = false;
        }
    }

    /// <summary>Applica una riga gia' catturata senza rileggere o riscrivere lo store.</summary>
    internal void Apply(PlantItem row)
    {
        _loadingFromDatabase = true;
        try
        {
            PlantStore.Apply(row, this);
        }
        finally
        {
            _loadingFromDatabase = false;
        }
    }

    /// <summary>
    /// Vero mentre <see cref="Load"/> sta copiando la riga del database dentro il dispositivo.
    /// </summary>
    /// <remarks>
    /// Volatile perche' letto da <c>SetProperty</c>, che puo' girare su un thread diverso da quello
    /// che ha avviato il caricamento.
    /// </remarks>
    private volatile bool _loadingFromDatabase;

    /// <summary>
    /// Avvia i processi di sfondo del dispositivo. Va chiamato dopo <see cref="Load"/>.
    /// </summary>
    /// <remarks>
    /// L'avvio era un effetto collaterale dei setter di <c>DeviceType</c> e <c>GroupItemId</c>,
    /// chiamati mentre <see cref="Load"/> assegnava le colonne una per una: a seconda dell'ordine si
    /// poteva avviare un ciclo su un dispositivo non ancora configurato del tutto, e lo si faceva
    /// tante volte quante le colonne rilevanti. Ora si avvia una volta sola, a configurazione
    /// completa. Si veda la voce <c>D63</c> del registro in <c>WCS/doc/debito-tecnico.md</c>.
    /// </remarks>
    internal void Start()
    {
        StartActiveProcesses();

        RunObserved(() => ExecuteStep());
    }

    /// <summary>
    /// Gestisce i cambiamenti di stato del Navigator, attivando le operazioni appropriate
    /// quando il sistema passa allo stato di esecuzione (Run).
    /// </summary>
    /// <param name="sender">L'origine dell'evento (Navigator)</param>
    /// <param name="value">Gli argomenti dell'evento contenenti il nuovo stato del Navigator</param>
    private void Navigator_StatusChanged(object? sender, NavigatorStatusChangedEventArgs value)
    {
        if (value.NewStatus != NavigatorStatusTypes.Run) return;

        // Questo gestore e' invocato per OGNI dispositivo dell'impianto, in sequenza e sul thread
        // di chi ha scritto NavStatus. Le due operazioni partono quindi sul thread pool: restano
        // indipendenti fra loro come prima, ma il comando di avvio non percorre piu' l'intero
        // impianto prima di tornare, e un loro errore non svanisce piu' nel vuoto.
        var luManager = LuManager;

        if (AutoUnloadLu && luManager != null)
            RunObserved(() => luManager.LuUnloadedFromAsi(StepTypes.Automatic));

        if (DeviceType != DeviceTypes.Storage && AutoExecuteStep)
            RunObserved(() => ExecuteStep());
    }

    /// <summary>
    /// Avvia un'operazione senza attenderla, ma osservandone l'esito.
    /// </summary>
    /// <param name="operation">L'operazione da avviare</param>
    /// <param name="callerName">Il metodo che l'ha avviata, per il log</param>
    /// <remarks>
    /// Sostituisce le partenze "lancia e dimentica" scritte come <c>_ = Metodo()</c> o come una
    /// chiamata a un metodo async usata come istruzione: in quella forma un'eccezione non viene
    /// osservata da nessuno e sparisce senza lasciare traccia. Qui l'errore finisce in
    /// <see cref="LogError"/>, e l'operazione parte comunque sul thread pool invece che sul thread
    /// del chiamante. La visibilita' del messaggio dipende da <c>Navigator.ActivateDebugLog</c>,
    /// come per ogni altra chiamata a <c>LogError</c>. Si veda la voce <c>B26</c> del registro in
    /// <c>WCS/doc/debito-tecnico.md</c>.
    /// </remarks>
    private void RunObserved(Func<Task> operation, [CallerMemberName] string callerName = "")
    {
        _ = BackgroundWork.Track(Task.Run(async () =>
        {
            try
            {
                await operation();
            }
            catch (OperationCanceledException)
            {
                // Chiusura del dispositivo: non e' un errore.
            }
            catch (Exception ex)
            {
                LogError(ex, callerName);
            }
        }, CancelDisposingToken));
    }

    /// <summary>
    /// Rimuove questo dispositivo dalla lista dei dispositivi pronti per il trasferimento di LU
    /// del dispositivo specificato e pulisce i riferimenti alla LU da ricevere se necessario.
    /// </summary>
    /// <param name="value">Il dispositivo da cui rimuovere questo dispositivo dalla lista di trasferimento</param>
    private void RemoveDevicesReadyToTransferLu(AutomationDevice? value)
    {
        if (value == null)
            return;

        value.RemoveDeviceReadyToTransferLu(this);

        if (value.LoadedLu != null && value.LoadedLu == LUtoReceive) LUtoReceive = null;
    }

    #endregion Private Methods

    #region Public Classes

    /// <summary>
    /// Rappresenta le informazioni sulla profondità rilevata per una LU specifica.
    /// </summary>
    public class LuDetectedDepth
    {

        #region Public Properties

        /// <summary>
        /// Ottiene o imposta la profondità rilevata durante l'operazione.
        /// </summary>
        /// <value>La profondità rilevata in unità specifiche del sistema.</value>
        public int DetectedDepth { get; set; }

        /// <summary>
        /// Ottiene o imposta l'identificativo univoco della LU per cui è stata rilevata la profondità.
        /// </summary>
        /// <value>L'ID della LU associata a questa misurazione di profondità.</value>
        public decimal LuId { get; set; }

        #endregion Public Properties

    }

    /// <summary>
    /// Rappresenta le informazioni di ricerca per l'allocazione di LU basata sul tipo.
    /// </summary>
    public class LuSearchLocationByTypeLu
    {

        #region Public Fields

        /// <summary>
        /// L'identificativo univoco del dispositivo di destinazione per l'allocazione.
        /// </summary>
        public Guid? DestinationItemId;

        /// <summary>
        /// L'identificativo del tipo di LU da cercare.
        /// </summary>
        public decimal LuTypeId;

        /// <summary>
        /// Il codice della porta di storage dove allocare la LU.
        /// </summary>
        public string? StorageDoorCode;

        #endregion Public Fields

    }

    #endregion Public Classes

}
