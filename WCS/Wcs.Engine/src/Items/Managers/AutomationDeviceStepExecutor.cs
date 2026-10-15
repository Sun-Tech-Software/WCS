using Wcs.Engine.Collections;
using Wcs.Engine.ORM;
using Log.Interface;
using Wcs.Connection.Interface;
using Wcs.Connection.Interface.CommandData;
using static Wcs.Common.Enums;
using static Wcs.Engine.Items.AutomationDevice;

namespace Wcs.Engine.Items.Managers;

public partial class AutomationDeviceStepExecutor(AutomationDevice device)
{

    #region Private Fields

    private static readonly MissionSteps MissionSteps = MissionSteps.Instance;

    #endregion Private Fields

    #region Public Methods

    /// <summary>
    /// Gestisce il completamento di uno step di missione, aggiornando lo stato del dispositivo e processando il trasferimento delle LU
    /// </summary>
    /// <param name="idStepDone">ID univoco dello step completato</param>
    /// <param name="stepType">Tipo di step eseguito (prelievo, deposito, movimento, ecc.)</param>
    /// <param name="detectedDepth">Profondità rilevata per operazioni di stoccaggio (-1 se non applicabile)</param>
    public async Task StepDone(Guid idStepDone, StepTypes stepType, int detectedDepth = -1)
    {
        var steps = MissionSteps.Instance;
        var executedStep = FindExecutedStep(steps, idStepDone);

        if (executedStep?.PathMission == null)
        {
            if (device.DeviceType == DeviceTypes.CircularDistributor)
                device.StorageManager?.StartProcessExtractionLu();
            return;
        }

        var sourceItem = executedStep.SourceItem;
        var destinationItem = executedStep.DestinationItem;

        try
        {
            if (sourceItem == null || destinationItem == null)
            {
                HandleNullItems(sourceItem, destinationItem);
                return;
            }

            await  FinalizeExecutedStep(executedStep, stepType, detectedDepth);
        }
        catch (Exception ex)
        {
            HandleStepError(sourceItem, destinationItem, ex);
        }
    }

    #endregion Public Methods

    #region Internal Methods

    /// <summary>
    /// Aggiunge un dispositivo alla lista dei dispositivi pronti per trasferire una LU
    /// </summary>
    /// <param name="value">Il dispositivo da aggiungere alla lista</param>
    internal void AddDevicesReadyToTransferLu(AutomationDevice? value)
    {
        value?.AddDeviceReadyToTransferLu(device);
    }

    /// <summary>
    /// Esegue un gruppo di movimenti MOVE_LU_TO in modalità raggruppata per ottimizzare le performance
    /// </summary>
    /// <param name="value">Lista degli step di missione da eseguire in gruppo</param>
    /// <remarks>
    /// <b>L'avvio si conta su ogni missione</b> (<c>PathMission.TryBeginStepStart</c>), dalla presa
    /// dei posti a quella dei gettoni del gruppo, e lo step di una missione che sta cambiando
    /// destinazione per mano dell'operatore resta fuori da questo giro, come quello la cui
    /// destinazione e' gia' impegnata. Il ciclo del gruppo non prende nessun semaforo di esecuzione:
    /// senza questo conto il commit del cambio poteva togliere uno step che il gruppo aveva gia'
    /// scelto e stava per mandare al campo (<c>ConfigureStep</c> lo rimette in <c>MissionSteps</c>),
    /// e la missione restava con lo step vecchio e la rotta nuova insieme.
    /// </remarks>
    internal async Task ExecuteGroupedMOVE_LU_TO(List<MissionStep> value)
    {
        var (steps, started, versions) = BeginStepStarts(value, nameof(ExecuteGroupedMOVE_LU_TO));

        try
        {
            await ExecuteGroupedMoveAsync(steps, versions);
        }
        finally
        {
            foreach (var mission in started)
                mission.EndStepStart();
        }
    }

    /// <summary>
    /// Conta l'avvio sulla missione di ogni step che un ciclo di gruppo sta per avviare
    /// (<c>PathMission.TryBeginStepStart</c>) e toglie dal giro gli step delle missioni che stanno
    /// cambiando destinazione per mano dell'operatore, con una riga che lo dice, e i depositi delle
    /// missioni dell'operatore, o con una cella prenotata per loro, la cui cella il navigatore non ha
    /// appena confermato (<c>PathMission.MayStartTheDeposit</c>). Con quei depositi restano fuori
    /// anche quelli delle missioni che ne hanno copiato la porta (vedi le note).
    /// </summary>
    /// <param name="value">Gli step scelti dal ciclo del gruppo</param>
    /// <param name="caller">Chi avvia, per la riga di registro</param>
    /// <returns>
    /// Gli step che restano nel giro, le missioni contate (a ognuna, alla fine, <c>EndStepStart</c>) e
    /// la versione della rotta di ognuna, letta dopo essersi contati e prima di guardare se lo step c'e'
    /// ancora: il gettone del gruppo sullo step si scrive solo se e' ancora quella
    /// (<c>PathMission.TryTakeTheStepToken</c>, corsa 5 della ricognizione della voce 41, compito 6). Nel
    /// movimento raggruppato la rileggono, con lo step, dopo la verifica delle celle, che puo' riscrivere
    /// la rotta da se' (<c>FollowTheRewrittenRoutes</c>).
    /// </returns>
    /// <remarks>
    /// Le missioni di un deposito di gruppo che copiano la porta prenotata da un'altra (il selettore per
    /// tipo di UDC di <c>AutomationDevice.VerifyWhereAllocateLuGrouped</c>) depositano come prima, senza
    /// chiedere al gestionale (scelta tecnica della specifica del 2/10): cioe' nello stesso movimento
    /// della missione da cui l'hanno copiata. Se quella resta fuori dal giro per la conferma della sua
    /// cella, restano fuori anche loro, e il giro dopo partono tutte insieme. Senza, ogni deposito di
    /// gruppo per tipo di UDC si spezzava in due movimenti: le copie partivano da sole verso una cella
    /// che nessuno aveva ancora riconfermato, e se il gestionale l'aveva revocata la missione che la
    /// teneva ne riceveva un'altra mentre le copie erano gia' nella vecchia (revisione finale della
    /// voce 39). La copia si riconosce dalla porta uguale e dal segno che non ha: una missione con il
    /// segno, o dell'operatore, decide da se' con la sua conferma. Aspettano con lei anche quando resta
    /// fuori perche' qualcuno la sta cambiando: il navigatore che le rifa' la cella la ferma cosi'. Se
    /// la cella e' persa, le copie lasciano la porta vecchia con lei (risposta 8 del proprietario
    /// dell'8/10, <c>MissionGateway.KeepTheStorageCellAsync</c>), e nessuna deposita da sola in una
    /// cella che nessuna missione tiene.
    /// </remarks>
    private (List<MissionStep> Steps, List<PathMission> Started, Dictionary<PathMission, long> Versions) BeginStepStarts(
        List<MissionStep> value, string caller)
    {
        var started = new List<PathMission>();
        var versions = new Dictionary<PathMission, long>(ReferenceEqualityComparer.Instance);
        var steps = new List<MissionStep>(value.Count);
        var heldDoors = new HashSet<string>(StringComparer.Ordinal);

        foreach (var step in value)
        {
            var mission = step.PathMission;

            if (mission == null)
            {
                steps.Add(step);
            }
            else if (mission.TryBeginStepStart())
            {
                // La versione prima dello sguardo allo step: una rimozione dopo lo sguardo la fa salire,
                // e il gettone del gruppo non si scrive (ConfigureStep, ProcessSingleStep).
                versions.TryAdd(mission, mission.RouteVersion);

                // Il ciclo ha scelto lo step da MissionSteps un istante prima: se ora non c'e' piu',
                // qualcuno l'ha tolto nel mezzo (un cambio dell'operatore cominciato e finito, ma
                // anche ogni altra rimozione della rotta), e il gruppo ripartirebbe con uno step che
                // non c'e' (ConfigureStep lo rimetterebbe dentro). Vale per ogni giro del gruppo, non
                // solo durante un cambio dell'operatore: e' un cambiamento di comportamento in campo,
                // voluto, perche' rimettere dentro uno step tolto ridava vita a una rotta scartata.
                if (!MissionSteps.Contains(step))
                {
                    mission.EndStepStart();
                    Navigator.WriteLog(
                        $"{caller} {device.ItemGuid}: lo step {step.StepId} della missione {mission.MissionId} non c'e' piu', resta fuori da questo giro",
                        LogEntryType.Information);
                    continue;
                }

                // Il deposito di una missione dell'operatore, o con una cella prenotata per lei, parte
                // solo nella cella che il gestionale le tiene adesso, con la conferma del navigatore
                // (PathMission.MayStartTheDeposit): senza, lo step resta fuori da questo giro e la
                // missione va al navigatore. Il ciclo del gruppo non chiama il gestionale per lei. Le
                // missioni del gruppo che hanno copiato la porta di un'altra non chiedono, e partono con
                // lei (qui sotto, dopo il giro su tutti gli step).
                if (step.DestinationItem is { DeviceType: DeviceTypes.Storage } &&
                    !mission.MayStartTheDeposit(mission.TakeStorageCellConfirmation(), $"{caller} {device.ItemGuid}"))
                {
                    mission.EndStepStart();
                    if (!string.IsNullOrEmpty(mission.StorageDoorCode)) heldDoors.Add(mission.StorageDoorCode);
                    continue;
                }

                started.Add(mission);
                steps.Add(step);
            }
            else
            {
                Navigator.WriteLog(
                    $"{caller} {device.ItemGuid}: la missione {mission.MissionId} sta cambiando destinazione, lo step {step.StepId} resta fuori da questo giro",
                    LogEntryType.Information);

                // Ferma cosi' la tiene anche il navigatore che le rifa' la cella persa
                // (MissionGateway.KeepTheStorageCellAsync): le copie della sua porta aspettano con
                // lei, perche' la cella puo' essere proprio quella che sta perdendo.
                if (step.DestinationItem is { DeviceType: DeviceTypes.Storage } &&
                    !string.IsNullOrEmpty(mission.StorageDoorCode))
                    heldDoors.Add(mission.StorageDoorCode);
            }
        }

        // Le copie della porta di un deposito rimasto fuori aspettano con lui (vedi le note): dopo il
        // giro su tutti, perche' la copia puo' venire prima della missione da cui ha copiato.
        if (heldDoors.Count > 0)
        {
            foreach (var step in steps.ToList())
            {
                // Anche la copia con il segno dell'operatore, a cui ha cambiato solo il gateway: resta una
                // copia (MissionGateway.IsACopy, risposta del 9/10 alla seconda domanda del compito 17), e il
                // suo deposito non chiede la cella (PathMission.MayStartTheDeposit).
                if (step.PathMission is not { } copy || !Api.MissionGateway.IsACopy(copy) ||
                    step.DestinationItem is not { DeviceType: DeviceTypes.Storage } ||
                    string.IsNullOrEmpty(copy.StorageDoorCode) || !heldDoors.Contains(copy.StorageDoorCode))
                    continue;

                steps.Remove(step);
                started.Remove(copy);
                copy.EndStepStart();
                Navigator.WriteLog(
                    $"{caller} {device.ItemGuid}: il deposito della missione {copy.MissionId} nella porta {copy.StorageDoorCode}, copiata da un'altra missione del gruppo, aspetta con lei la conferma della cella",
                    LogEntryType.Information);
            }
        }

        return (steps, started, versions);
    }

    /// <summary>Il corpo di <see cref="ExecuteGroupedMOVE_LU_TO"/>, con gli avvii gia' contati sulle missioni.</summary>
    /// <param name="value">Gli step da eseguire in gruppo, senza quelli delle missioni che stanno cambiando destinazione</param>
    /// <param name="versions">La versione della rotta di ogni missione contata (<see cref="BeginStepStarts"/>)</param>
    private async Task ExecuteGroupedMoveAsync(List<MissionStep> value, Dictionary<PathMission, long> versions)
    {
        if (value is { Count: 0 })
        {
            device.GroupExecuteStepRunning = false;
            return;
        }

        var lUtoMove = new List<MovementLuDataCommand>();
        var itemStatusCollections = CreateItemStatusCollections();

        // Il posto sulle destinazioni si prende PRIMA di ogni lettura verso il gestionale, come fa
        // il movimento singolo (StartPutToMOVE_LU, voce D73): prima di tutto, e in particolare
        // prima della verifica di allocazione (CheckCellStoreStatus), che e' la prima domanda al
        // gestionale. Qui non si prendeva affatto, e le destinazioni si marcavano occupate solo
        // dopo le letture (ApplyItemStatuses): per tutta la durata di quelle chiamate un terzo
        // mittente poteva spedire allo stesso punto. Lo step la cui destinazione e' gia' di un
        // altro resta fuori da questo giro. Voce D152.
        var stepsToMove = ReserveDestinations(value);

        // Il gettone del gruppo si tiene in una variabile e si passa a chi lo scrive sugli step
        // e a chi lo manda al PLC. Rileggerlo da RunningStepId dava Empty: il getter scioglie un
        // identificativo che nessuno step porta ancora, e nessuno lo portava prima di ConfigureStep.
        // Cosi' al PLC partiva Empty come GroupedMovementId, e al ritorno StepDoneGroup,
        // StepErrorGroup e StepTimeOutGroup selezionavano tutti gli step senza gruppo dell'impianto.
        // Voce D166.
        var groupedMovementId = Guid.NewGuid();

        try
        {
            await CheckCellStoreStatus(value);

            // La verifica puo' aver riscritto da se' la rotta delle missioni del gruppo (sempre, se
            // ricostruisce, anche con la stessa cella): gli step e le versioni si rileggono dopo, o il
            // gettone rinuncerebbe a ogni giro per la riscrittura del gruppo stesso.
            FollowTheRewrittenRoutes(stepsToMove, versions);
            device.Status = DeviceStatusTypes.Busy;
            device.RunningStepId = groupedMovementId;

            await PopulateMovementCommands(stepsToMove, lUtoMove, itemStatusCollections, groupedMovementId, versions);
        }
        catch (Exception ex)
        {
            // Il posto si restituisce insieme allo stato, su ogni via d'uscita: anche se a
            // fallire e' la verifica di allocazione, che viene prima di Busy. Una prenotazione
            // appesa rende la destinazione inaccettabile per chiunque e ferma il tratto.
            ReleaseDestinations(stepsToMove);

            // Un solo punto di annullamento per tutto quello che questo metodo ha preso. La
            // verifica di allocazione (CheckCellStoreStatus) e la costruzione dei comandi
            // (ConfigureStorageDestination/Source) leggono dal gestionale, e quelle letture
            // possono fallire: senza questo
            // ripristino il dispositivo restava Busy per sempre, e la guardia d'ingresso di
            // GroupExecuteStep - che ammette solo Ready o Recovering - non lo riammetteva piu'.
            // Il gettone e' vivo finche' uno step lo porta (voce D166), e gli step configurati
            // prima del guasto lo portano: si azzera esplicitamente, senza contare sulla
            // normalizzazione del getter.
            device.RunningStepId = Guid.Empty;
            device.Status = DeviceStatusTypes.Ready;

            // Il chiamante (GroupExecuteStep) cattura e rimette a posto GroupExecuteStepRunning,
            // ma senza scrivere niente: la riga la scriviamo qui, o il guasto resta muto come
            // quello del 2026-09-09.
            device.LogError(ex);
            throw;
        }

        if (lUtoMove.Count == 0)
        {
            // Niente parte: i posti presi qui sopra si restituiscono.
            ReleaseDestinations(stepsToMove);

            // E si restituisce anche il gettone, come nel catch qui sopra: gli step configurati
            // da PopulateMovementCommands lo portano gia', quindi il getter lo rilegge vivo, e
            // un gruppo Ready senza comando in volo restava «impegnato» (IsBusyWithCommand) per
            // un gettone che nessun completamento avrebbe mai sciolto: la rete degli appesi
            // (TryReleaseHungRunningStep) cerca per StepId e non vede i gettoni di gruppo.
            // Voce D166, revisione del lotto F.
            device.RunningStepId = Guid.Empty;
            device.Status = DeviceStatusTypes.Ready;
            device.GroupExecuteStepRunning = false;
            return;
        }

        ApplyItemStatuses(itemStatusCollections);
        ExecuteMovementCommand(lUtoMove, stepsToMove, groupedMovementId);
    }

    /// <summary>
    /// Esegue movimenti MOVE_LU_TO singoli in sequenza
    /// </summary>
    /// <param name="value">Lista degli step di missione da eseguire singolarmente</param>
    /// <remarks>
    /// L'avvio si conta su ogni missione per tutto il giro, come in
    /// <see cref="ExecuteGroupedMOVE_LU_TO"/>: gli step sono scelti prima e avviati uno alla volta,
    /// con un'attesa fra l'uno e l'altro, e <c>ProcessSingleStep</c> rimette in
    /// <c>MissionSteps</c> lo step che non vi trova. Un cambio di destinazione dell'operatore
    /// committato nel mezzo farebbe ripartire uno step che non c'e' piu'.
    /// </remarks>
    internal async Task ExecuteSingleMOVE_LU_TO(List<MissionStep> value)
    {
        var (steps, started, versions) = BeginStepStarts(value, nameof(ExecuteSingleMOVE_LU_TO));

        try
        {
            await ExecuteSingleMovesAsync(steps, versions);
        }
        finally
        {
            foreach (var mission in started)
                mission.EndStepStart();
        }
    }

    /// <summary>Il corpo di <see cref="ExecuteSingleMOVE_LU_TO"/>, con gli avvii gia' contati sulle missioni.</summary>
    /// <param name="value">Gli step da eseguire, senza quelli delle missioni che stanno cambiando destinazione</param>
    /// <param name="versions">La versione della rotta di ogni missione contata (<see cref="BeginStepStarts"/>)</param>
    private async Task ExecuteSingleMovesAsync(List<MissionStep> value, Dictionary<PathMission, long> versions)
    {
        if (value is { Count: 0 })
        {
            device.GroupExecuteStepRunning = false;
            return;
        }

        SortValueByOrderNumber(value);
        var tempRunningStepId = device.RunningStepId == Guid.Empty ? Guid.NewGuid() : device.RunningStepId;
        var isStartedProcedure = false;

        // Ciclo esplicito e non predicato LINQ: ProcessSingleStep attende fra uno step e l'altro,
        // e un predicato non puo' farlo. Come prima, si provano TUTTI gli step validi, non ci si
        // ferma al primo che parte. Voce D72.
        foreach (var step in value.Where(IsStepValid))
        {
            if (await ProcessSingleStep(step, tempRunningStepId, versions))
                isStartedProcedure = true;
        }

        if (!isStartedProcedure)
            device.GroupExecuteStepRunning = false;
    }

    /// <summary>
    /// Esegue gli step in coda secondo la procedura di bilanciamento tra prelievi e depositi
    /// </summary>
    internal async Task ExecuteStepOnQueuedProcedure()
    {
        if (!device.IsReadyToExecuteStep) return;

        if (device.DevicesReadyToTransferLuCount > 0 && device.QueuedLuToPickUpCount > 0)
        {
           await ExecuteBalancedSteps();
        }
        else if (device.QueuedLuToPickUpCount > 0)
        {
            device.LastExecutedStepType = ExecutedStepTypes.PickUp;
            await ExecuteQueueLuToPickUp();
        }
        else if (device.DevicesReadyToTransferLuCount > 0)
        {
            device.LastExecutedStepType = ExecutedStepTypes.Store;
            await ExecuteQueueToReceiveLu();
        }
    }

    /// <summary>
    /// Ripete l'esecuzione di step raggruppati in caso di necessità di retry
    /// </summary>
    internal async Task RepeatGroupedSteps()
    {
        if (device.RunningStepId == Guid.Empty) return;

        var listOfSteps = MissionSteps.Where(x => device.RunningStepId == x.GroupedMovementId).ToList();

        try
        {
            await ExecuteGroupedMOVE_LU_TO(listOfSteps);
        }
        catch (Exception ex)
        {
            device.LogError(ex);
        }
    }

    /// <summary>
    /// Ripete l'esecuzione di step singoli in caso di necessità di retry
    /// </summary>
    internal async Task RepeatSingleSteps()
    {
        if (device.RunningStepId == Guid.Empty) return;

        var listOfSteps = MissionSteps.Where(x => device.RunningStepId == x.GroupedMovementId).ToList();

        try
        {
            await ExecuteSingleMOVE_LU_TO(listOfSteps);
        }
        catch (Exception ex)
        {
            device.LogError(ex);
        }
    }

    /// <summary>
    /// Avvia il processo di distribuzione circolare per dispositivi di tipo CircularDistributor
    /// </summary>
    /// <returns>Task che rappresenta l'operazione asincrona</returns>
    internal async Task StartCircularDistribution()
    {
        if (Navigator.NavStatus != NavigatorStatusTypes.Run) return;

        if (device.LoadedLu == null)
        {
            // LuManager e' costruito da Initialize e non torna mai nullo: la guardia
            // sostituisce "await x?.M()", che sollevava NullReferenceException.
            if (device.LuManager != null) await device.LuManager.CheckGetFromItem();
            return;
        }

        if (!ProcessCircularDistributionLu())
        {
            device.StartProcessCircularDistributorExecuteStep();
            return;
        }

        FinalizeCircularDistribution();
        await Task.CompletedTask;
    }

    /// <summary>
    /// Avvia il processo di prelievo di una LU da una posizione di stoccaggio
    /// </summary>
    /// <param name="value">La missione di percorso che contiene le informazioni per il prelievo</param>
    /// <remarks>
    /// <b>L'indirizzo di campo si legge prima di prendere i gettoni.</b> La lettura passa dal
    /// gestionale e puo' fallire; <c>ConfigurePickUpStep</c> prende invece due gettoni — la
    /// prenotazione della destinazione (che per un prelievo e' questo dispositivo stesso) e
    /// <c>RunningStepId</c> — e li prendeva <b>prima</b>, con la lettura fuori dal <c>try</c> che
    /// li avrebbe restituiti. Il 2026-09-09 un <c>404</c> sulle partizioni della cella 529.0 usci'
    /// proprio di li': <c>SH01.0001.0001</c> resto' prenotato per se' stesso, rifiuto' 2662 esecuzioni di
    /// fila e si sblocco' solo riavviando il motore. In quest'ordine la lettura non ha niente da
    /// restituire quando fallisce, e l'eccezione risale al chiamante invece di lasciare l'impianto
    /// fermo.
    /// <para>
    /// <b>L'avvio si conta sulla missione</b> (<c>PathMission.TryBeginStepStart</c>), dalla lettura
    /// dello step a quella dei gettoni: con un cambio di destinazione dell'operatore in corso il
    /// prelievo non parte, e mentre parte il cambio non comincia. Lo step si legge prima di una
    /// lettura dal gestionale, e un cambio che lo togliesse nel frattempo lascerebbe prendere i
    /// gettoni per uno step che non c'e' piu'. Il gettone va su questo dispositivo, la destinazione
    /// dello step: il semaforo del dispositivo di posizione non l'avrebbe fermato (e il commit del cambio
    /// non lo prende piu', voce 41, compito 5).
    /// </para>
    /// </remarks>
    internal async Task StartPickUpLu(PathMission? value)
    {
        if (value == null) return;

        if (!value.TryBeginStepStart())
        {
            Navigator.WriteLog(
                value.IsWithdrawn
                    ? $"StartPickUpLu {device.ItemGuid}: la missione {value.MissionId} e' stata chiusa, il prelievo non parte"
                    : $"StartPickUpLu {device.ItemGuid}: la missione {value.MissionId} sta cambiando destinazione, il prelievo non parte",
                LogEntryType.Information);
            return;
        }

        MissionStep stepToDo;
        DataAsi fromAsiItem;

        try
        {
            // La versione della rotta si legge prima dello step: il gettone si scrive solo se e' ancora
            // quella (PathMission.TryTakeTheStepToken, corsa 5 della ricognizione della voce 41,
            // compito 6). Fra le due c'e' anche la lettura dal gestionale: una rotta riscritta intanto
            // fa rinunciare il prelievo, che riparte al giro dopo con lo step nuovo.
            var seenRouteVersion = value.RouteVersion;

            var step = device.LuManager?.StepToDoDestination(value);

            if (step?.PathMission == null)
            {
                HandleNoStepToDo(value);
                return;
            }

            var asi = await GetPickUpAsiItem(step);

            if (asi == null)
            {
                Navigator.WriteLog(
                    $"Error On ASI {device.ASI}.{device.SUBITEM}.{device.PARTITION} StartStoreLU->MOVE_LU_TO Description: From ASI Destination is Nothing",
                    LogEntryType.Warning);
                return;
            }

            if (!ConfigurePickUpStep(value, step, seenRouteVersion)) return;

            stepToDo = step;
            fromAsiItem = asi;
        }
        finally
        {
            value.EndStepStart();
        }

        await ExecutePickUpMovement(stepToDo, fromAsiItem);
    }

    /// <summary>
    /// Avvia il processo di ricezione di una LU da un altro dispositivo
    /// </summary>
    /// <param name="item">Il dispositivo sorgente da cui ricevere la LU</param>
    internal void StartReceivingLu(AutomationDevice? item)
    {
        if (item == null) return;

        switch (device.MotionType)
        {
            case MotionTypes.MoveTo:
                ExecuteMoveTo(item);
                break;
            case MotionTypes.GetFrom:
                ExecuteGetFrom(item);
                break;
        }
    }

    #endregion Internal Methods

}