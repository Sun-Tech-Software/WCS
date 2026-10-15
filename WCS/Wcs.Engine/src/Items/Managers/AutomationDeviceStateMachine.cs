using Log.Interface;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Items.Managers;

/// <summary>
/// Gestisce la macchina a stati per i dispositivi di automazione, fornendo controllo thread-safe
/// per le transizioni di stato e la sincronizzazione tra dispositivi raggruppati.
/// </summary>
/// <param name="device">Il dispositivo di automazione da gestire</param>
public class AutomationDeviceStateMachine(AutomationDevice device) : IDisposable
{

    #region Private Fields

    /// <summary>
    /// Lock per sincronizzare l'accesso allo stato del dispositivo.
    /// Instance-level per evitare serializzazione globale tra tutti i dispositivi.
    /// </summary>
    /// <remarks>
    /// Non e' piu' un <c>SemaphoreSlim</c>: da quando <c>D52</c> ha portato la pubblicazione fuori
    /// dalla sezione critica, qui dentro non si attende piu' nulla, quindi non serve un lock
    /// asincrono. Un <c>Monitor</c> permette inoltre alla scrittura di <c>Status</c> di essere
    /// sincrona senza rischiare un blocco per rientranza, cosa che un semaforo non consentirebbe.
    /// </remarks>
    private readonly object _stateLock = new();

    /// <summary>
    /// Lock che serializza fra loro le pubblicazioni dello stato (scrittura sul database, evento e
    /// bridge). E' distinto da <see cref="_stateLock"/> proprio perche' il lock di stato non deve
    /// piu' restare preso per tutta la durata dell'I/O: le transizioni non aspettano il database,
    /// ma le pubblicazioni restano ordinate.
    /// </summary>
    private readonly SemaphoreSlim _publishLock = new SemaphoreSlim(1, 1);

    /// <summary>
    /// Numero progressivo dell'ultima transizione accettata. Si incrementa solo sotto
    /// <see cref="_stateLock"/> e serve a riconoscere, al momento della pubblicazione, una
    /// transizione gia' superata da una piu' recente.
    /// </summary>
    private long _stateVersion;

    /// <summary>
    /// Stato corrente del dispositivo. Volatile per garantire la visibilità tra thread.
    /// </summary>
    private volatile DeviceStatusTypes _currentState = DeviceStatusTypes.Ready;

    /// <summary>
    /// Indica se l'oggetto è stato dispose. Volatile per garantire la visibilità tra thread.
    /// </summary>
    private volatile bool _disposed;

    #endregion Private Fields

    #region Public Properties

    /// <summary>
    /// Ottiene lo stato corrente del dispositivo in modalità thread-safe.
    /// </summary>
    /// <value>Lo stato corrente del dispositivo</value>
    public DeviceStatusTypes CurrentState => _currentState;

    #endregion Public Properties

    #region Public Methods

    /// <summary>
    /// Rilascia le risorse utilizzate dalla macchina a stati.
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        _publishLock.Dispose();
    }

    /// <summary>
    /// Forza il dispositivo a uno stato specifico senza validare la transizione.
    /// Questo metodo bypassa i controlli di validità delle transizioni di stato.
    /// </summary>
    /// <param name="newState">Il nuovo stato da impostare</param>
    /// <remarks>
    /// <para>
    /// <b>Nessun chiamante, e va tenuto così.</b> Verificato il 2026-09-01 con ricerca su tutto
    /// il repository: questo metodo non è invocato da nessuna parte. Non è codice morto per
    /// dimenticanza, è una porta lasciata aperta di proposito — lo sblocco manuale di un
    /// dispositivo da parte di un operatore, quando la macchina a stati rifiuterebbe la
    /// transizione.
    /// </para>
    /// <para>
    /// Chi passa di qui a ripulire il codice non utilizzato: non cancellarlo. Si veda la voce
    /// <c>D51</c> del registro in <c>WCS/doc/debito-tecnico.md</c>.
    /// </para>
    /// </remarks>
    public async Task ForceState(DeviceStatusTypes newState)
    {
        if (_disposed) return;

        long version;
        DeviceStatusTypes previous;

        LeaveTheErrorBefore(newState);

        lock (_stateLock)
        {
            // "device.Status = newState" non c'e' piu': era ridondante, perche' il getter di Status
            // legge proprio _currentState, e da quando la scrittura di Status e' sincrona sarebbe
            // stata una rientranza sullo stesso lock.
            previous = _currentState;
            _currentState = newState;
            version = ++_stateVersion;
        }

        LeaveTheError(previous, newState);

        // Anche la forzatura pubblica fuori dal lock di stato e passa dallo stesso cancello di
        // PublishState: se non lo facesse, una forzatura e una transizione potrebbero scrivere sul
        // database in ordine invertito, perche' non condividerebbero il lock di pubblicazione.
        await PublishState(version);
    }

    /// <summary>
    /// Imposta lo stato online/offline del dispositivo con gestione automatica dell'esecuzione dei passi.
    /// </summary>
    /// <param name="value">True per portare il dispositivo offline, false per riportarlo online</param>
    public async Task SetStatusOffLine(bool value)
    {
        if (_disposed) return;

        var targetState = value ? DeviceStatusTypes.OffLine : DeviceStatusTypes.Ready;

        // Verifica se il cambio di stato è valido
        if (!CanChangeToOfflineState(value)) return;

        if (!await TryChangeState(targetState)) return;
        if (value) return; // Se si sta andando offline

        // Reset dello step in esecuzione quando si torna online
        device.RunningStepId = Guid.Empty;

        // Avvia automaticamente l'esecuzione se configurato
        if (device.AutoExecuteStep)
            _ = device.ExecuteStep();
    }

    /// <summary>
    /// Tenta di cambiare lo stato del dispositivo validando la transizione.
    /// Per i gruppi di dispositivi, propaga automaticamente lo stato agli elementi membri.
    /// </summary>
    /// <param name="newState">Il nuovo stato desiderato</param>
    /// <returns>True se la transizione è stata eseguita con successo, false altrimenti</returns>
    public async Task<bool> TryChangeState(DeviceStatusTypes newState)
    {
        // Allineato agli altri due metodi pubblici: senza questa guardia un'assegnazione ancora in
        // volo dopo il Dispose finirebbe su un semaforo gia' rilasciato.
        if (_disposed) return false;

        LeaveTheErrorBefore(newState);

        if (!TryApplyState(newState, out var version, out var propagate, out var previous)) return false;

        LeaveTheError(previous, newState);

        // Fuori dal lock di stato: il giro sul database non serializza piu' le transizioni.
        await PublishAndPropagate(version, newState, propagate);

        return true;
    }

    /// <summary>
    /// Cambia lo stato del dispositivo rendendolo visibile subito, senza attendere la pubblicazione.
    /// </summary>
    /// <param name="newState">Il nuovo stato desiderato</param>
    /// <returns>True se la transizione e' stata accettata, false altrimenti</returns>
    /// <remarks>
    /// E' la via usata dal setter di <c>AutomationDevice.Status</c>. Al ritorno lo stato e' gia'
    /// cambiato: chi assegna e poi rilegge trova il valore nuovo, e due assegnazioni da thread
    /// diversi si applicano nell'ordine in cui prendono il lock. Prima il setter si limitava a
    /// lanciare un <c>Task.Run</c>, quindi il motore proseguiva come se il cambio fosse avvenuto
    /// mentre poteva non esserlo ancora: e' la ragione per cui erano nate le attese fisse subito
    /// dopo le assegnazioni di stato. La pubblicazione resta in sottofondo, ed e' cio' che rompe la
    /// rientranza: un gestore di <c>StatusChanged</c> che riassegni lo stato dello stesso
    /// dispositivo non gira sotto questo lock. Si vedano le voci <c>D43</c> e <c>D59</c> del
    /// registro in <c>WCS/doc/debito-tecnico.md</c>.
    /// </remarks>
    public bool TryChangeStateNow(DeviceStatusTypes newState)
    {
        if (_disposed) return false;

        LeaveTheErrorBefore(newState);

        if (!TryApplyState(newState, out var version, out var propagate, out var previous)) return false;

        LeaveTheError(previous, newState);

        _ = PublishAndPropagate(version, newState, propagate);

        return true;
    }

    #endregion Public Methods

    #region Private Methods

    /// <summary>
    /// Il dispositivo esce dall'errore (da <c>Warning</c> o <c>Error</c> a uno stato che non e' di
    /// errore): e' la ripartenza dell'operatore, e le conferme della cella delle missioni a bordo si
    /// buttano (<c>AutomationDevice.ForgetTheCellConfirmationsOnBoard</c>, risposta 6 del proprietario
    /// dell'8/10): il primo deposito dopo la ripartenza richiede al gestionale.
    /// </summary>
    /// <param name="previous">Lo stato da cui la transizione e' partita.</param>
    /// <param name="newState">Lo stato appena applicato.</param>
    /// <remarks>
    /// <para>
    /// Buttarle quando il dispositivo va in errore (<c>StepError</c>, <c>StepErrorGroup</c>) non basta:
    /// il navigatore non guarda lo stato di chi deposita, e mentre il dispositivo aspetta l'operatore
    /// puo' confermare una cella - la missione era gia' in coda, o le rifa' la cella persa - che
    /// restava fino alla ripartenza, anche ore dopo (primo giro di correzioni del compito 12). Qui
    /// cade ogni conferma data prima della ripartenza, comunque sia arrivata.
    /// </para>
    /// <para>
    /// Prima che lo stato nuovo si veda, e di nuovo dopo: <see cref="LeaveTheErrorBefore"/> le butta
    /// prima di applicare la transizione, cosi' nessun altro thread legge il dispositivo pronto con
    /// la conferma di prima ancora in piedi (un avvio di deposito che guarda lo stato e poi la
    /// conferma); questo le butta dopo, con lo stato da cui la transizione e' davvero partita, per la
    /// conferma arrivata fra i due. Una in piu' non fa danno: il deposito richiede al gestionale. Chi
    /// rimette in marcia ripete gli step dopo aver assegnato lo stato
    /// (<c>AutomationDevice.ResetStatusBelongedItems</c>). Fuori dal lock di stato, perche' prende il
    /// lucchetto di ogni missione; nessuna chiamata al gestionale. Una domanda del navigatore partita
    /// durante l'errore e arrivata dopo la ripartenza conferma lo stesso: la risposta e' di adesso.
    /// Prima le buttava solo dopo, con lo stato nuovo gia' pubblicato (revisione finale del secondo
    /// giro).
    /// </para>
    /// </remarks>
    private void LeaveTheError(DeviceStatusTypes previous, DeviceStatusTypes newState)
    {
        if (previous is DeviceStatusTypes.Warning or DeviceStatusTypes.Error &&
            newState is not (DeviceStatusTypes.Warning or DeviceStatusTypes.Error))
            device.ForgetTheCellConfirmationsOnBoard([]);
    }

    /// <summary>
    /// <see cref="LeaveTheError"/> prima della transizione, con lo stato letto adesso: se il dispositivo
    /// e' in errore e va in uno stato che non lo e', le conferme cadono prima che lo stato nuovo si veda.
    /// Se poi la transizione e' rifiutata, sono cadute per niente, e il deposito richiede al gestionale.
    /// </summary>
    private void LeaveTheErrorBefore(DeviceStatusTypes newState) => LeaveTheError(_currentState, newState);

    /// <summary>
    /// Determina se lo stato specificato è relativo a movimento o operazioni che richiedono esclusività.
    /// </summary>
    /// <param name="state">Lo stato da verificare</param>
    /// <returns>True se lo stato è di movimento o busy, false altrimenti</returns>
    private static bool IsMovementOrBusyState(DeviceStatusTypes state)
    {
        return state is DeviceStatusTypes.Moving
            or DeviceStatusTypes.Recovering
            or DeviceStatusTypes.Unloading
            or DeviceStatusTypes.Busy;
    }

    /// <summary>
    /// Verifica se il dispositivo può cambiare stato verso/da offline.
    /// </summary>
    /// <param name="goingOffline">True se il dispositivo sta andando offline, false se sta tornando online</param>
    /// <returns>True se la transizione è consentita, false altrimenti</returns>
    private bool CanChangeToOfflineState(bool goingOffline)
    {
        if (goingOffline)
        {
            // Può andare offline solo da Ready, OffLine o Locked
            return _currentState is DeviceStatusTypes.Ready
                or DeviceStatusTypes.OffLine
                or DeviceStatusTypes.Locked;
        }

        // Può tornare online solo da OffLine
        return _currentState == DeviceStatusTypes.OffLine;
    }

    /// <summary>
    /// Valida se una transizione di stato è permessa.
    /// </summary>
    /// <param name="from">Stato di partenza</param>
    /// <param name="to">Stato di destinazione</param>
    /// <returns>True se la transizione è valida, false altrimenti</returns>
    /// <remarks>
    /// La clausola <c>device.DeviceType != DeviceTypes.Storage</c> non c'e' piu': teneva ogni
    /// magazzino fermo allo stato di inizializzazione per tutta la vita del processo, scartando in
    /// silenzio le assegnazioni invece di impedirle. Dove il motore vuole davvero non toccare lo
    /// stato di un magazzino lo dice sul posto, con un filtro sul tipo prima della scrittura o con
    /// un ramo <c>case DeviceTypes.Storage:</c> dedicato; qui la clausola era una coperta che
    /// nascondeva anche le scritture volute. Non rimetterla: si veda la voce <c>D44</c> del
    /// registro in <c>WCS/doc/debito-tecnico.md</c>.
    /// </remarks>
    private bool IsValidTransition(DeviceStatusTypes from, DeviceStatusTypes to)
    {
        // Unica validazione presente: non si transita verso lo stato in cui si e' gia'. La tabella
        // degli stati ammessi non esiste ancora e va progettata a parte.
        return from != to;
    }

    /// <summary>
    /// Applica la transizione sotto il lock di stato: valida, scrive e decide la propagazione.
    /// </summary>
    /// <param name="newState">Il nuovo stato desiderato</param>
    /// <param name="version">Numero della transizione appena accettata</param>
    /// <param name="propagate">True se lo stato va propagato ai membri del gruppo</param>
    /// <param name="previous">Lo stato da cui la transizione parte</param>
    /// <returns>True se la transizione e' stata accettata, false altrimenti</returns>
    /// <remarks>
    /// Sezione critica ridotta al minimo, e senza alcuna chiamata verso l'esterno: si leggono solo
    /// due proprieta' del dispositivo, entrambe semplici letture di campo. La propagazione vera
    /// avviene fuori, perche' tocca il lock di altri dispositivi.
    /// </remarks>
    private bool TryApplyState(DeviceStatusTypes newState, out long version, out bool propagate,
        out DeviceStatusTypes previous)
    {
        version = 0;
        propagate = false;

        lock (_stateLock)
        {
            previous = _currentState;
            if (!IsValidTransition(_currentState, newState)) return false;

            _currentState = newState;
            version = ++_stateVersion;

            propagate = device.DeviceType == DeviceTypes.GroupOfAutomationDevices &&
                        device.GroupedMoveLuTo &&
                        !IsMovementOrBusyState(newState);

            return true;
        }
    }

    /// <summary>
    /// Pubblica la transizione e, se e' ancora l'ultima, la propaga ai membri del gruppo.
    /// </summary>
    /// <param name="version">Numero della transizione da pubblicare</param>
    /// <param name="newState">Lo stato da propagare</param>
    /// <param name="propagate">True se la propagazione era stata decisa sotto lock</param>
    /// <remarks>
    /// Chi arriva da <see cref="TryChangeStateNow"/> non attende questo lavoro, quindi un errore
    /// non tornerebbe a nessuno: viene catturato e messo a log qui.
    /// </remarks>
    private async Task PublishAndPropagate(long version, DeviceStatusTypes newState, bool propagate)
    {
        try
        {
            var published = await PublishState(version);

            // Propaga solo se questa transizione e' ancora l'ultima: propagare uno stato gia'
            // superato scriverebbe sui membri del gruppo un valore obsoleto.
            if (published && propagate) PropagateStateToGroupMembers(newState);
        }
        catch (ObjectDisposedException)
        {
            // Dispose durante la pubblicazione: non e' un errore.
        }
        catch (Exception ex)
        {
            Navigator.WriteLog(
                $"[ASI {device.ASI}.{device.SUBITEM}.{device.PARTITION}] Errore pubblicando lo stato {newState}: {ex.Message}",
                LogEntryType.Error);
        }
    }

    /// <summary>
    /// Pubblica lo stato del dispositivo fuori dal lock di stato: scrive sul database, solleva
    /// l'evento e aggiorna il bridge.
    /// </summary>
    /// <param name="version">Numero della transizione che ha chiesto la pubblicazione</param>
    /// <returns>True se la pubblicazione e' avvenuta, false se era gia' superata</returns>
    /// <remarks>
    /// Le pubblicazioni sono serializzate fra loro dal <c>_publishLock</c>. Sotto quel lock si
    /// ricontrolla la versione: se non e' piu' l'ultima, la transizione e' stata superata e la sua
    /// pubblicazione e' obsoleta, quindi si salta. La transizione con la versione piu' alta non
    /// viene mai saltata da nessuno, e legge lo stato corrente al momento della scrittura: il
    /// valore che resta sul database e sul bridge e' quindi sempre l'ultimo stato raggiunto.
    /// </remarks>
    private async Task<bool> PublishState(long version)
    {
        if (_disposed) return false;

        await _publishLock.WaitAsync();
        try
        {
            var lastVersion = Interlocked.Read(ref _stateVersion);
            if (lastVersion != version)
            {
                // Diagnostica, non anomalia: e' l'esito normale di due transizioni ravvicinate.
                // Si usa WriteLog e non RecordNavigatorLog perche' RecordNavigatorLog scrive sul
                // database senza condizioni, e un giro sul database per ogni pubblicazione saltata
                // rimetterebbe in circolo proprio il costo che questa riparazione toglie.
                Navigator.WriteLog(
                    $"[ASI {device.ASI}.{device.SUBITEM}.{device.PARTITION}] Pubblicazione dello stato saltata: transizione {version} superata dalla {lastVersion}",
                    LogEntryType.Information);
                return false;
            }

            await device.FireStatusChanged();
            return true;
        }
        finally
        {
            _publishLock.Release();
        }
    }

    /// <summary>
    /// Propaga il nuovo stato a tutti i dispositivi membri del gruppo.
    /// Utilizzato solo per gruppi di dispositivi di automazione.
    /// </summary>
    /// <param name="newState">Il nuovo stato da propagare</param>
    private void PropagateStateToGroupMembers(DeviceStatusTypes newState)
    {
        foreach (var itemBelonged in device.BelongedItems.OfType<AutomationDevice>())
        {
            itemBelonged.Status = newState;
        }
    }

    #endregion Private Methods

}