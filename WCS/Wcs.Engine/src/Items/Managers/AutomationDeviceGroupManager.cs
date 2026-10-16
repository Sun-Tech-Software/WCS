using System.Data;
using Helper.Converters;
using Wcs.Engine.ORM;
using Log.Interface;
using Wcs.Wms.Contracts;
using static Wcs.Common.Enums;
using static Wcs.Engine.Items.AutomationDevice;

namespace Wcs.Engine.Items.Managers;

/// <summary>
/// Gestisce le operazioni di gruppo per i dispositivi di automazione, coordinando l'esecuzione di step multipli
/// e la gestione delle unità di carico (LU) in modalità raggruppata.
/// Fornisce funzionalità per il caricamento, scaricamento e trasferimento sincronizzato di multiple LU
/// tra dispositivi appartenenti allo stesso gruppo.
/// </summary>
/// <param name="device">Il dispositivo di automazione associato a questo manager di gruppo</param>
public class AutomationDeviceGroupManager(AutomationDevice device) : IDisposable
{

    #region Internal Fields

    /// <summary>
    /// Token di cancellazione per l'esecuzione degli step di gruppo
    /// </summary>
    internal CancellationTokenSource? CancellationGroupExStepTokenSource;

    /// <summary>
    /// Il freno della ricerca di una porta per le copie che l'hanno lasciata (<c>PathMission.LeftACopiedDoor</c>,
    /// <c>AutomationDevice.RedoTheCopiedDoorsAsync</c>), per missione: una copia che non trova una porta
    /// non riporta il ciclo del gruppo nella ricerca a ogni passata finche' il freno non scade.
    /// </summary>
    /// <remarks>
    /// Con la ricerca che falliva il ciclo tornava li' a ogni passata, in ogni modalita' anche la
    /// <c>No</c>, chiedeva di nuovo al gestionale e non arrivava mai ai trasferimenti e alle estrazioni,
    /// che potevano liberare proprio la cella che mancava. Le misure sono quelle del freno della
    /// riverifica (<c>AutomationDeviceStorageManager.StoreSearchRetries</c>); assegnabile perche' i
    /// collaudi lo costruiscano con un orologio finto.
    /// </remarks>
    internal RetryBackoff CopiedDoorRetries = new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30));

    #endregion Internal Fields

    #region Private Fields

    /// <summary>
    /// Token di cancellazione per l'interruzione dell'esecuzione degli step di gruppo
    /// </summary>
    private CancellationToken _cancelGroupExStepToken;

    /// <summary>
    /// Contatore per il numero di verifiche delle LU da prelevare
    /// </summary>
    private int _numCheckLUsToPick;

    /// <summary>
    /// Timestamp dell'ultimo controllo eseguito
    /// </summary>
    private DateTime _timeLastCheck;

    #endregion Private Fields

    #region Public Methods

    /// <summary>
    /// Rilascia tutte le risorse utilizzate dal manager di gruppo
    /// </summary>
    public void Dispose()
    {
        CancellationGroupExStepTokenSource?.Cancel();
        CancellationGroupExStepTokenSource?.Dispose();
        CancellationGroupExStepTokenSource = null;
    }

    /// <summary>
    /// Gestisce il completamento di uno step di gruppo, aggiornando lo stato delle missioni
    /// e coordinando le operazioni successive per tutti i dispositivi del gruppo
    /// </summary>
    /// <param name="groupedMovementId">ID univoco del movimento di gruppo</param>
    /// <param name="stepType">Tipo di step completato</param>
    /// <param name="detectedDepths">Liste delle profondità rilevate per le LU (opzionale)</param>
    public async Task StepDoneGroup(Guid groupedMovementId, StepTypes stepType, List<LuDetectedDepth>? detectedDepths = null)
    {
        // Empty non e' il gettone di nessun gruppo: e' il GroupedMovementId di tutti gli step senza
        // gruppo dell'impianto, e il filtro qui sotto li prenderebbe tutti, ne rilascerebbe le
        // destinazioni e li toglierebbe. Si scrive una riga, perche' un gettone vuoto vuol dire
        // che un comando e' partito senza gettone. Voce D166.
        if (groupedMovementId == Guid.Empty)
        {
            Navigator.WriteLog(
                $"StepDoneGroup {device.ItemGuid}: gettone di gruppo vuoto, nessuno step da chiudere",
                LogEntryType.Warning);
            device.GroupExecuteStepRunning = false;
            return;
        }

        var executedSteps = Collections.MissionSteps.Instance.Where(x => x.GroupedMovementId == groupedMovementId);

        var missionSteps = executedSteps as MissionStep[] ?? executedSteps.ToArray();
        if (!missionSteps.Any())
        {
            device.GroupExecuteStepRunning = false;
            return;
        }

        var lusToRelocate = new List<RelocateLu>(missionSteps.OrderByDescending(x => x.SourceItem?.PARTITION)
            .Select(x =>
                new RelocateLu
                {
                    DestinationItem = x.DestinationItem,
                    MissionId = x.MissionId,
                    SourceItem = x.SourceItem
                }));

        var itemsToExecuteStep = new List<AutomationDevice>();
        var groupItemsToExecuteStep = new List<AutomationDevice>();

        var storageDoorToLock = new List<string>();
        var storageDoorToUnLock = new List<string>();
        var pickUpDoorToUnlock = new List<string>();

        // Gli step sono arrivati: i posti che ExecuteGroupedMOVE_LU_TO aveva preso sulle
        // destinazioni si restituiscono tutti qui, prima di lavorare gli step, cosi' il rilascio
        // di ognuno non dipende dal successo dei precedenti. Con il rilascio dentro il ciclo del
        // try un'eccezione a meta' (una lettura verso il gestionale che fallisce) lasciava i posti
        // presi sugli step non ancora lavorati, che restano in MissionSteps: la rete delle
        // prenotazioni appese (TryReleaseHungIncomingReservation) scioglie solo i posti di step
        // che non esistono piu', e la destinazione restava impegnata. Nel movimento singolo il
        // posto lo restituisce il setter di RunningStepId del mittente; qui il mittente porta il
        // gettone del gruppo, non lo StepId. Voce D152.
        foreach (var step in missionSteps)
            if (step.DestinationItem is { DeviceType: not DeviceTypes.Storage } destination)
                destination.ReleaseIncomingStep(step.StepId);

        try
        {
            foreach (var step in missionSteps)
            {
                if (step.PathMission == null || step.SourceItem == null || step.DestinationItem == null)
                    continue;

                var transferringLu = step.PathMission;
                var sourceItem = step.SourceItem;
                var destinationItem = step.DestinationItem;

                step.EndStep = DateTime.Now;
                step.StepStatus = ResultCodeTypes.Success;

                step.PathMission.GroupedMovementId = Guid.Empty;

                transferringLu.StepOrigin = stepType;
                transferringLu.StorageDepth = -1;

                var luCode = transferringLu.LuCode;
                var infoDescription = string.Empty;

                var fromAsiItem = Navigator.GetDataAsi(sourceItem);
                var toAsiItem = Navigator.GetDataAsi(destinationItem);

                if (destinationItem.BelongingGroupId == device.GroupItemId)
                {
                    if (!itemsToExecuteStep.Contains(sourceItem))
                        itemsToExecuteStep.Add(sourceItem);

                    switch (sourceItem.DeviceType)
                    {
                        case DeviceTypes.Storage:
                            {
                                infoDescription =
                                    $"Picked Up LU {luCode} from {fromAsiItem.Asi}.{fromAsiItem.Subitem}.{fromAsiItem.Partition} to {toAsiItem.Asi}.{toAsiItem.Subitem}.{toAsiItem.Partition}";

                                MoveGroupedLuFromStorage(storageDoorToLock, pickUpDoorToUnlock, step, transferringLu);
                                break;
                            }

                        case DeviceTypes.Automation:
                        case DeviceTypes.Loading:
                        case DeviceTypes.Buffer:
                        case DeviceTypes.CircularDistributor:
                        case DeviceTypes.GroupOfAutomationDevices:
                        case DeviceTypes.LoadingAndBuffer:
                        case DeviceTypes.None:
                        default:
                            {
                                infoDescription =
                                    $"Moved LU {luCode} from {fromAsiItem.Asi}.{fromAsiItem.Subitem}.{fromAsiItem.Partition} to {toAsiItem.Asi}.{toAsiItem.Subitem}.{toAsiItem.Partition}";
                                break;
                            }
                    }
                }
                else if (sourceItem.BelongingGroupId == device.GroupItemId)
                {
                    if (!itemsToExecuteStep.Contains(destinationItem))
                        itemsToExecuteStep.Add(destinationItem);

                    if (destinationItem.BelongedGroup != null &&
                        !groupItemsToExecuteStep.Contains(destinationItem.BelongedGroup))
                    {
                        groupItemsToExecuteStep.Add(destinationItem.BelongedGroup);

                        CancellationGroupExStepTokenSource?.Cancel();
                        CancellationGroupExStepTokenSource = null;
                    }

                    switch (destinationItem.DeviceType)
                    {
                        case DeviceTypes.Storage:
                            {
                                infoDescription =
                                    $"Stored LU {luCode} from {fromAsiItem.Asi}.{fromAsiItem.Subitem}.{fromAsiItem.Partition} to {toAsiItem.Asi}.{toAsiItem.Subitem}.{toAsiItem.Partition}";
                                await MoveGroupedLuToStorage(detectedDepths, storageDoorToUnLock, transferringLu, sourceItem);
                                break;
                            }

                        default:
                            {
                                infoDescription =
                                    $"Moved LU {luCode} from {fromAsiItem.Asi}.{fromAsiItem.Subitem}.{fromAsiItem.Partition} to {toAsiItem.Asi}.{toAsiItem.Subitem}.{toAsiItem.Partition}";
                                break;
                            }
                    }
                }

                Navigator.RecordNavigatorLog(device, infoDescription, LogEntryType.Information, step);

                sourceItem.Status = DeviceStatusTypes.Ready;
            }

            device.RemoveStepsByGroupedMovementId(groupedMovementId);

            foreach (var lu in lusToRelocate.OrderByDescending(x => x.DestinationItem?.PARTITION))
            {
                // Una UDC da riposizionare senza destinazione non puo' essere caricata da
                // nessuna parte: prima sollevava NullReferenceException e interrompeva a meta'
                // il ripristino di tutto il gruppo. Ora si salta la sola UDC anomala, e la si
                // segnala con RecordNavigatorLog e non con WriteLog: e' merce che resta ferma
                // senza destinazione, l'operatore la deve vedere, e non e' un percorso caldo
                // ma un ripristino dopo errore di gruppo.
                if (lu.DestinationItem == null)
                {
                    Navigator.RecordNavigatorLog(device,
                        $"Relocation skipped for mission {lu.MissionId}: no destination item", LogEntryType.Warning);
                    continue;
                }

                await lu.DestinationItem.LoadLu(lu.MissionId, false);
                Collections.MissionSteps.Instance.RemoveAll(x => x.MissionId == lu.MissionId);
            }

            foreach (var door in storageDoorToLock.Where(x => !string.IsNullOrEmpty(x)))
                await DbInfo.SetCellLockedForStoring(door);

            foreach (var door in pickUpDoorToUnlock.Where(x => !string.IsNullOrEmpty(x)))
                await DbInfo.SetCellUnlockedForStoring(door);

            foreach (var door in storageDoorToUnLock.Where(x => !string.IsNullOrEmpty(x)))
                await DbInfo.SetCellUnlockedForStoring(door);

            foreach (var item in itemsToExecuteStep.Where(x => x.DeviceType != DeviceTypes.Storage)
                         .OrderByDescending(x => x.SUBITEM))
                item.Status = DeviceStatusTypes.Ready;

            foreach (var item in itemsToExecuteStep
                         .Where(x => x.BelongedGroup != device && x.DeviceType != DeviceTypes.Storage)
                         .OrderByDescending(x => x.SUBITEM))
                if (item.AutoExecuteStep)
                    _ = item.ExecuteStep(100);
                // LuManager e' costruito da Initialize e non torna mai nullo: la guardia
                // sostituisce "await x?.M()", che sollevava NullReferenceException.
                else if (item is { LoadedLu: null, LuManager: not null }) await item.LuManager.CheckGetFromItem();

            foreach (var unused in groupItemsToExecuteStep)
                StartProcessGroupExecuteStep();

            // Attesa fissa tolta con D67: compensava l'asincronia di Status, sincrona da D43.
            device.Status = DeviceStatusTypes.Ready;

            device.LUsToPick.Clear();

            device.GroupExecuteStepRunning = false;
        }
        catch (Exception ex)
        {
            device.LogError(ex);
            device.GroupExecuteStepRunning = false;
        }
    }

    #endregion Public Methods

    #region Internal Methods

    /// <summary>
    /// Lascia concludere un eventuale arresto del ciclo di gruppo e poi ne tenta la ripartenza.
    /// L'operazione termina senza errore se nel frattempo il dispositivo viene rilasciato.
    /// </summary>
    internal async Task RestartAfterDelayAsync()
    {
        try
        {
            // Task.Delay e non Thread.Sleep: l'attesa non trattiene un thread del pool. Il token
            // impedisce che una ripartenza gia' programmata faccia rinascere il ciclo dopo Dispose.
            await Task.Delay(100, device.CancelDisposingToken);
            StartProcessGroupExecuteStep();
        }
        catch (OperationCanceledException)
        {
            // Rilascio durante l'attesa: non c'e' piu' nulla da far ripartire.
        }
        catch (Exception ex)
        {
            // Il setter avvia questa operazione senza attenderla: l'errore deve restare visibile.
            device.LogError(ex);
        }
    }

    /// <summary>
    /// Avvia il processo di esecuzione degli step di gruppo in modalità asincrona.
    /// Crea un task in background che monitora continuamente le condizioni per l'esecuzione degli step
    /// </summary>
    internal void StartProcessGroupExecuteStep()
    {
        if (CancellationGroupExStepTokenSource != null) return;

        // Un dispositivo rilasciato non fa ripartire il proprio ciclo: chiude la finestra fra la
        // fine dell'attesa nel setter di GroupExecuteStepRunning e questa chiamata, dove un
        // Dispose avrebbe gia' azzerato il gettone qui sopra.
        if (device.CancelDisposingToken.IsCancellationRequested) return;

        CancellationGroupExStepTokenSource = new CancellationTokenSource();
        _cancelGroupExStepToken = CancellationGroupExStepTokenSource.Token;

        // Il try sta DENTRO il while: senza, la prima eccezione - anche la sola
        // OperationCanceledException del Task.Delay all'arresto - lasciava un Task faulted che
        // nessuno osservava, e il ciclo di gruppo di questo dispositivo non ripartiva piu'. E' lo
        // stesso difetto della voce B20, riparato a suo tempo sul processo di estrazione.
        Task.Run(async () =>
        {
            while (!_cancelGroupExStepToken.IsCancellationRequested)
            {
                try
                {
                    await GroupExecuteStep();
                }
                catch (OperationCanceledException)
                {
                    // Arresto del processo di gruppo: non e' un errore.
                    break;
                }
                catch (Exception ex)
                {
                    device.LogError(ex);
                }

                try
                {
                    // Pavimento a 1 ms come in Navigator.BackgroundLoop: con 0 (il valore predefinito
                    // di un dispositivo nuovo) il ciclo girava a vuoto sul thread pool, con un
                    // negativo Task.Delay sollevava e il ciclo di gruppo moriva in silenzio.
                    await Task.Delay(Math.Max(1, device.TimeLapseToExecuteStep), _cancelGroupExStepToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, _cancelGroupExStepToken);
    }

    #endregion Internal Methods

    #region Private Methods

    /// <summary>
    /// Verifica ricorsivamente se ci sono LU in arrivo verso un dispositivo specifico
    /// </summary>
    /// <param name="destinationItem">Il dispositivo di destinazione da verificare</param>
    /// <param name="nLevels">Numero di livelli da verificare nella ricorsione</param>
    /// <returns>True se ci sono LU in arrivo, false altrimenti</returns>
    private static bool CheckArrivingLu(AutomationDevice? destinationItem, int nLevels)
    {
        if (destinationItem == null || nLevels == 0)
            return false;

        nLevels--;

        if (destinationItem.DeviceType is not (DeviceTypes.Automation or DeviceTypes.CircularDistributor) ||
            destinationItem.IsLinkedToStorage)
            return false;

        return destinationItem.LoadedLu != null || destinationItem.SourceItemsLinked.OfType<AutomationDevice>()
            .Any(item => CheckArrivingLu(item, nLevels));
    }

    /// <summary>
    /// Gestisce lo stoccaggio di una LU convertendo la profondità in partizione
    /// </summary>
    /// <param name="transferringLu">La missione di percorso della LU da trasferire</param>
    /// <param name="sourceItem">Il dispositivo sorgente</param>
    /// <param name="cellId">ID della cella di stoccaggio</param>
    private static async Task StoreLuWithConvertDepthToPartition(PathMission transferringLu, AutomationDevice sourceItem,
        decimal cellId)
    {
        if (string.IsNullOrWhiteSpace(transferringLu.DestinationPartition))
        {
            var isInverted = cellId != 0 && (await transferringLu.DescribeCellAsync(cellId)).Inverted;

            transferringLu.DetectedDepth = isInverted ? sourceItem.StorageOffsetReversed : sourceItem.StorageOffset;
        }
        else
        {
            transferringLu.DetectedDepth = await transferringLu.GetPartitionOffsetAsync(cellId, transferringLu.DestinationPartition);
        }

        transferringLu.StorageDepth = transferringLu.DetectedDepth;
    }

    /// <summary>
    /// Gestisce lo stoccaggio di una LU con lunghezza fissa
    /// </summary>
    /// <param name="transferringLu">La missione di percorso della LU da trasferire</param>
    /// <param name="sourceItem">Il dispositivo sorgente</param>
    /// <param name="inverted">Indica se la cella è invertita</param>
    private static void StoreLuWithFixedLength(PathMission transferringLu, AutomationDevice sourceItem, bool inverted)
    {
        transferringLu.DetectedDepth = inverted ? sourceItem.StorageOffsetReversed : sourceItem.StorageOffset;

        transferringLu.StorageDepth = transferringLu.DetectedDepth;
    }

    /// <summary>
    /// Gestisce lo stoccaggio di una LU utilizzando dati provenienti dal PLC
    /// </summary>
    /// <param name="detectedDepths">Lista delle profondità rilevate</param>
    /// <param name="transferringLu">La missione di percorso della LU da trasferire</param>
    /// <param name="sourceItem">Il dispositivo sorgente</param>
    private static async Task StoreLuWithFromPlc(List<LuDetectedDepth>? detectedDepths, PathMission transferringLu,
        AutomationDevice sourceItem)
    {
        var detectedDepth = -1;

        if (detectedDepths != null)
            foreach (var depth in detectedDepths.Where(depth => depth.LuId == transferringLu.LuId))
            {
                detectedDepth = depth.DetectedDepth;
                break;
            }

        if (detectedDepth == -1) return;

        transferringLu.DetectedDepth = detectedDepth;
        transferringLu.DetectedDepth = transferringLu.DetectedDepth + sourceItem.StorageOffset +
                                       await DbInfo.GetLuDepth(transferringLu.LuId);
        transferringLu.StorageDepth = transferringLu.DetectedDepth;
    }

    /// <summary>
    /// Verifica se le LU da prelevare sono valide e coerenti con il contenuto della cella
    /// </summary>
    /// <param name="value">Lista delle LU da verificare</param>
    /// <returns>True se le LU sono valide per il prelievo, false altrimenti</returns>
    private static async Task<bool> VerifyLUtoPickUp(List<PathMission?> value)
    {
        if (value.Count == 0)
            return true;

        var mission = value.FirstOrDefault();

        if (mission == null)
            return true;

        var cellId = await DbInfo.GetIdCellLocationByLuId(mission.LuId);

        if (cellId == 0)
            return true;

        // Compito 9: ex DbInfo.GetLUsInCell(cellId), ora IWarehouse.GetCellContentAsync - "mission" e'
        // gia' in mano, quindi passa dalla missione come le altre letture del confine.
        var lUsInCell = await mission.GetCellContentAsync(cellId);

        return lUsInCell.Count == value.Count || lUsInCell.All(lu => value.Any(x => x != null && x.LuId == lu));
    }

    /// <summary>
    /// Aggiunge una nuova missione alla coda di prelievo di gruppo
    /// </summary>
    /// <param name="navigatorCommand">Comando del navigatore</param>
    /// <param name="groupedMovementId">ID del movimento di gruppo</param>
    /// <param name="item">Riga di dati della missione</param>
    /// <param name="type">Tipo di missione</param>
    /// <remarks>
    /// Interno e non privato come il gemello <c>AutomationDevice.AddMissionToPickUp</c>: e' il punto
    /// di costruzione della missione di gruppo, e i test lo attraversano da li'.
    /// </remarks>
    internal async Task AddMissionToGroupPickUp(Command navigatorCommand, Guid groupedMovementId, DataRow item,
        MissionTypes type)
    {
        await CommandToDo.Update(navigatorCommand);

        var newMission = new PathMission();

        if (type != MissionTypes.PickUpAndStoreAgain)
        {
            // Chiave di collegamento a ESTRAZIONI_AUTOMATICE_UDC
            newMission.ExtractionQueueId = item.GetValueOrThrow<decimal>("ID_ESTRAZIONE_UDC");
            // -------------------------------------------------------

            // Chiave di collegamento a ASSEGNAZIONE_NOTE
            if (item.GetValueOrThrow<Guid?>("GUID_ASSEGNAZIONE_UDC") != null)
                newMission.GuidAssignmentLu = item.GetValueOrThrow<Guid>("GUID_ASSEGNAZIONE_UDC");
            // -------------------------------------------------------

            // L'identificativo numerico della stessa assegnazione, come in
            // AutomationDevice.AddMissionToPickUp e per la stessa ragione: e' quello su cui il
            // gestionale esclude l'assegnazione che sta rivalutando (MissionQueries.HoldsLoadUnit).
            if (item.GetValueOrThrow<decimal?>("ID_ASSEGNAZIONE_UDC") is { } noteAssignmentId)
                newMission.LuNoteAssignmentId = noteAssignmentId;
            // -------------------------------------------------------

            // Chiave di collegamento a LAVORAZIONI_NOTE. La guardia leggeva "ID_LAVORAZIONEN", una
            // colonna che non esiste: GetValue risponde nullo invece di sollevare, quindi era sempre
            // falsa e le missioni di gruppo nascevano tutte con la lavorazione a zero. Ora la guardia
            // legge la colonna che la riga sotto assegna, come il gemello
            // AutomationDevice.AddMissionToPickUp.
            // Come nel gemello singolo (AutomationDevice.AddMissionToPickUp): dal compito 13 questa
            // chiave non serve piu' alla chiusura della nota, e con lei e' sparito l'avviso «la nota
            // non verra' chiusa».
            if (item.GetValueOrThrow<decimal?>("ID_LAVORAZIONE") != null)
                newMission.NoteProcessingId = item.GetValueOrThrow<decimal>("ID_LAVORAZIONE");
            // -------------------------------------------------------
        }

        newMission.Date = DateTime.Now;
        newMission.LuId = item.GetValueOrThrow<decimal>("ID_UDC");
        newMission.SourceItemId = item.GetValueOrThrow<Guid>("ID_ITEM_SOURCE");
        newMission.GroupedMovementId = groupedMovementId;

        newMission.PickUpDoorCode = navigatorCommand.PickUpDoorCode;
        newMission.StorageDoorCode = navigatorCommand.StorageDoorCode;

        newMission.PositionItemId = item.GetValueOrThrow<Guid>("ID_ITEM_SOURCE");
        newMission.IdCommandToDo = navigatorCommand.IdCommandToDo;
        newMission.StorageDepth = await DbInfo.GetLuStoringDepth(item.GetValueOrThrow<decimal>("ID_UDC"));

        if (type == MissionTypes.PickUpAndStoreAgain)
        {
            if (device.BufferGroup != null) newMission.DestinationItemId = device.BufferGroup.ItemGuid;
        }
        else
        {
            newMission.DestinationItemId = item.GetValueOrThrow<Guid>("ID_ITEM_DESTINATION");
            newMission.BufferCellId = item.GetValueOrThrow<decimal>("ID_CELLA_POLMONE");
        }

        newMission.RelocateItemId = item.GetValueOrThrow<Guid>("ID_ITEM_RELOCATOR");

        // Compito 10: una sola domanda al gestionale per codice, tipo, modalita' di movimentazione e
        // tara - punto di costruzione "nel gestore dei gruppi" del compito, stesso schema di
        // AutomationDevice.AddMissionToPickUp.
        var loadUnitDescription = await Navigator.RequireWarehouse($"AddMissionToGroupPickUp sull'unita' di carico {newMission.LuId}")
            .DescribeLoadUnitAsync(newMission.LuId);
        newMission.LuCode = loadUnitDescription?.Code ?? string.Empty;
        newMission.IdLuType = loadUnitDescription?.LoadUnitTypeId ?? 0;
        newMission.HandlingMode = (int)Math.Round(loadUnitDescription?.HandlingMode ?? 0);
        newMission.Tare = loadUnitDescription?.Tare ?? 0;
        newMission.OperationType = OperationTypes.Outbound;
        newMission.MissionType = type;

        newMission.Gateway = await GetGateway(newMission) ?? Guid.Empty;

        device.EnqueueLuToPickUp(newMission);
        ORM.PathMission.Add(newMission);

        await DbInfo.SetExtractionItemQueueProcessed(item.GetValueOrThrow<decimal>("ID_ESTRAZIONE_UDC"), 1);
    }

    /// <summary>
    /// Determina il gateway appropriato per una missione di percorso basandosi sulla profondità di stoccaggio
    /// </summary>
    /// <param name="value">La missione di percorso</param>
    /// <returns>Il GUID del gateway appropriato, null se non trovato</returns>
    private async Task<Guid?> GetGateway(PathMission? value)
    {
        var cellId = await DbInfo.GetCellId(value?.PickUpDoorCode);
        var isReversed = value != null && cellId != 0 && (await value.DescribeCellAsync(cellId)).Inverted;

        return isReversed
            ? device.BelongedItems
                .FirstOrDefault(x => x != null && value != null && x.StorageOffsetReversed == value.StorageDepth)
                ?.ItemGuid
            : device.BelongedItems
                .FirstOrDefault(x => value != null && x != null && x.StorageOffset == value.StorageDepth)
                ?.ItemGuid;
    }

    /// <summary>
    /// Esegue il ciclo principale di gestione degli step di gruppo, determinando quale operazione eseguire
    /// basandosi sullo stato del dispositivo e delle LU disponibili
    /// </summary>
    /// <returns>Task completato</returns>
    /// <remarks>
    /// <c>internal</c> e non <c>private</c> perche' il suo unico chiamante di produzione e' il ciclo
    /// lanciato da <see cref="StartProcessGroupExecuteStep"/>: una prova che entrasse di li' dovrebbe
    /// aspettare a tempo, e il tempo non e' un discriminante.
    /// </remarks>
    internal async Task GroupExecuteStep()
    {
        if (device.GroupExecuteStepRunning)
            return;

        device.GroupExecuteStepRunning = true;

        if (Navigator.NavStatus != NavigatorStatusTypes.Run ||
            !device.GroupLoadingActive ||
            device.Status is not (DeviceStatusTypes.Ready or DeviceStatusTypes.Recovering) ||
            device.BelongedItems.Any(x => x is { IsReadyToExecuteStep: false }))
        {
            device.GroupExecuteStepRunning = false;
            return;
        }

        try
        {
            // One list for the whole decision: the getter scans the sources and the missions every
            // time, and reading it again between two checks could also see a different state.
            List<PathMission?> readyToLoad = [];
            if (device.LastGroupMovement is GroupMovementType.Extracting or GroupMovementType.None &&
                !(Navigator.Defragmentation == DefragmentationTypes.Enabled && 
                  device is { EnableDefragmentation: true, IdCellToDefragment: > 0 }) &&
                device.GroupLoadingActive &&
                (!device.WaitBelongedItemsEmptyBeforeLoading ||
                 device is { WaitBelongedItemsEmptyBeforeLoading: true, LUsLoaded.Count: 0 }) &&
                (readyToLoad = device.LUsReadyToLoad).Count > 0)
            {
                // Verifica se sono disponibili LU per il caricamento

                if (_timeLastCheck == default)
                    _timeLastCheck = DateTime.Now;

                if (device.WaitingTimeForcedLoading == 0 ||
                    readyToLoad.Count >= device.BelongedItems.Count ||
                    (DateTime.Now - _timeLastCheck).TotalMilliseconds > device.WaitingTimeForcedLoading ||
                    (LookForArrivingLu(readyToLoad, 4) == false &&
                     !device.IsLinkedToStorage))
                {
                    var lastLevelToWrite = 0;

                    var luToCheck = readyToLoad.FirstOrDefault();

                    if (luToCheck is { PositionItem: not null })
                        lastLevelToWrite = luToCheck.PositionItem.GroupLevelId;

                    if (device.GroupedMoveLuTo)
                        await  StartLoadingGroupedMission();
                    else
                        await StartLoadingSingleMission();

                    if (lastLevelToWrite > 0)
                        device.LastLevelLoaded = lastLevelToWrite;

                    _timeLastCheck = default;
                }
                else
                {
                    device.GroupExecuteStepRunning = false;
                }
            }
            else if (device.LUsLoaded is { Count: > 0 } loaded &&
                     (device.LookWhereAllocateLu != LookWhereAllocateLuTypes.No && device.LUsWithNoStorage ||
                      loaded.Any(lu => lu is { LeftACopiedDoor: true } && !CopiedDoorRetries.IsWaiting(lu.MissionId))))
            {
                // Anche con la modalita' No: le copie che hanno lasciato la porta (risposta 8 del
                // proprietario dell'8/10) le rifa' la ricerca per tipo in ogni modalita', perche' le
                // fa anche la ricerca dopo un rifiuto del PLC, che cerca per tipo in ogni modalita'.
                // Una copia che non ha trovato una porta aspetta il suo freno (CopiedDoorRetries): fino
                // ad allora il ciclo va avanti ai trasferimenti e alle estrazioni.
                await device.VerifyWhereAllocateLuGrouped(device.LUsLoaded);
                device.GroupExecuteStepRunning = false;
            }
            else if (device is { GroupTransferringActive: true, LUsReadyToTransfer.Count: > 0 })
            {
                if (device.GroupedMoveLuTo)
                    await StartTransferringGroupedMission();
                else
                    await StartTransferringSingleMission();
            }
            else if (device.LastGroupMovement is GroupMovementType.Loading or GroupMovementType.None &&
                     device.IsLinkedToStorage)
            {
                if (device.CanExtractLUs)
                {
                    // Verifica se ci sono LU da prelevare o coda di estrazione da processare.
                    // Stessa disciplina degli altri punti di presa: si aspetta con un tetto di
                    // tempo, non indefinitamente. La cancellazione ora e' catturata: prima la sua
                    // eccezione saltava il finally, lasciava GroupExecuteStepRunning a true e
                    // faceva morire il ciclo. Poiche' StopActiveProcesses azzera il token e i
                    // processi possono ripartire, quel flag sopravviveva al riavvio e il gruppo
                    // non eseguiva piu'.
                    var acquired = false;

                    try
                    {
                        acquired = await Navigator.SyncExtraction.WaitAsync(
                            Navigator.SyncExtractionTimeout, _cancelGroupExStepToken);

                        if (!acquired)
                            Navigator.WriteLog(
                                $"[ASI {device.ASI}] Timeout acquiring extraction lock in GroupExecuteStep",
                                LogEntryType.Warning);
                        else
                            await ProcessGroupExtractionLu();
                    }
                    catch (OperationCanceledException)
                    {
                        // Arresto del processo: non e' un errore, ma il flag va comunque rimesso a
                        // posto, altrimenti il gruppo resta bloccato alla ripartenza.
                    }
                    finally
                    {
                        if (acquired) Navigator.SyncExtraction.Release();
                        device.GroupExecuteStepRunning = false;
                    }
                }
                else
                {
                    device.GroupExecuteStepRunning = false;
                }
            }
            else
            {
                device.LastGroupMovement = GroupMovementType.None;
                _timeLastCheck = default;
                device.GroupExecuteStepRunning = false;
            }
        }
        catch (Exception ex)
        {
            // Il gettone si rimetteva a posto e l'eccezione spariva senza lasciare una riga. Cinque
            // strade entrano in questo try e quattro possono sollevare un guasto del confine:
            // StartLoadingGroupedMission e StartLoadingSingleMission (AddMissionToGroupPickUp ->
            // DescribeLoadUnitAsync), VerifyWhereAllocateLuGrouped, StartTransferring* e
            // ProcessGroupExtractionLu (DescribeCellAsync, GetCellContentAsync). La quinta ha gia'
            // la propria riga, aggiunta in AutomationDeviceStepExecutor con il commento «o il guasto
            // resta muto come quello del 2026-09-09»; le altre quattro non l'avevano. Il ramo di
            // gruppo e' gia' la variante meno collaudata (voce D123): un gestionale che non risponde
            // fermava il gruppo senza lasciare nessun appiglio.
            device.LogError(ex);

            device.LastGroupMovement = GroupMovementType.None;
            _timeLastCheck = default;
            device.GroupExecuteStepRunning = false;
        }
    }

    /// <summary>
    /// Cerca LU in arrivo nei dispositivi collegati per determinare se attendere prima di caricare
    /// </summary>
    /// <param name="readyToLoad">Le LU pronte al caricamento, lette una volta dal chiamante</param>
    /// <param name="nLevels">Numero di livelli da verificare nella ricerca</param>
    /// <returns>True se ci sono LU in arrivo, false altrimenti</returns>
    private bool LookForArrivingLu(List<PathMission?> readyToLoad, int nLevels = 0)
    {
        if (readyToLoad.Count == 0)
            return false;

        if (nLevels == 0)
            nLevels = device.BelongedItems.Count;

        var missionsToCheck = readyToLoad[0];

        if (missionsToCheck == null || missionsToCheck.MissionType == MissionTypes.Extraction ||
            missionsToCheck.MissionType == MissionTypes.Rejection || missionsToCheck.PositionItem == null)
            return false;

        var levelToCheck = missionsToCheck.PositionItem.GroupLevelId;

        if (levelToCheck == 0)
            return false;

        if (device is { GroupItemId: > 0, DeviceType: DeviceTypes.GroupOfAutomationDevices })
        {
            return device.BelongedItems.Any(item => CheckArrivingLu(item, nLevels));
        }
        else if (CheckArrivingLu(device, nLevels))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Gestisce il movimento di una LU raggruppata dal magazzino, aggiornando le liste delle porte
    /// da bloccare e sbloccare
    /// </summary>
    /// <param name="storageDoorToLock">Lista delle porte di magazzino da bloccare</param>
    /// <param name="pickUpDoorToUnlock">Lista delle porte di prelievo da sbloccare</param>
    /// <param name="missionStep">Step della missione corrente</param>
    /// <param name="transferringLu">LU in trasferimento</param>
    private void MoveGroupedLuFromStorage(List<string> storageDoorToLock, List<string> pickUpDoorToUnlock,
        MissionStep missionStep, PathMission transferringLu)
    {
        if (transferringLu.MissionType == MissionTypes.PickUpAndStoreAgain)
        {
            if (transferringLu.PickUpDoorCode != null && !storageDoorToLock.Contains(transferringLu.PickUpDoorCode))
                storageDoorToLock.Add(transferringLu.PickUpDoorCode);
            if (transferringLu.PickUpDoorCode != null && pickUpDoorToUnlock.Contains(transferringLu.PickUpDoorCode))
                pickUpDoorToUnlock.Remove(transferringLu.PickUpDoorCode);
        }
        else if (transferringLu.PickUpDoorCode != null && !storageDoorToLock.Contains(transferringLu.PickUpDoorCode) &&
                 !pickUpDoorToUnlock.Contains(transferringLu.PickUpDoorCode))

        {
            pickUpDoorToUnlock.Add(transferringLu.PickUpDoorCode);
        }


        // L'UDC prelevato va spostato in un diverso modulo di automazione
        device.CurrentPickUpStep++;

        if (missionStep.PathMission != null) device.LUsToPick.Remove(missionStep.PathMission);
    }

    /// <summary>
    /// Gestisce il movimento di una LU raggruppata verso il magazzino, calcolando la profondità
    /// di stoccaggio e aggiornando le porte da sbloccare
    /// </summary>
    /// <param name="detectedDepths">Lista delle profondità rilevate</param>
    /// <param name="storageDoorToUnLock">Lista delle porte di magazzino da sbloccare</param>
    /// <param name="transferringLu">LU in trasferimento</param>
    /// <param name="sourceItem">Dispositivo sorgente</param>
    private async Task MoveGroupedLuToStorage(List<LuDetectedDepth>? detectedDepths, List<string> storageDoorToUnLock,
        PathMission transferringLu, AutomationDevice sourceItem)
    {
        if (transferringLu.StorageDoorCode != null && !storageDoorToUnLock.Contains(transferringLu.StorageDoorCode))
            storageDoorToUnLock.Add(transferringLu.StorageDoorCode);

        if (transferringLu.MissionType != MissionTypes.ChangeStoreLocation)
            device.CurrentStoreStep++;

        if (transferringLu.LuId <= 0) return;

        var cellId = await DbInfo.GetCellId(transferringLu.StorageDoorCode);

        if (transferringLu.MissionType == MissionTypes.DefragmentCell)
        {
            transferringLu.DetectedDepth = await transferringLu.GetPartitionOffsetAsync(cellId, transferringLu.DestinationPartition);
            transferringLu.StorageDepth = transferringLu.DetectedDepth;
        }
        else
        {
            // Le tre proprieta' vengono dalla stessa cella: una sola domanda al gestionale (compito
            // 5), gia' in memoria se MoveGroupedLuToStorage l'ha decodificata prima per questa
            // missione (StoreLuWithConvertDepthToPartition, sotto, la trova gia' pronta).
            decimal luTypeId = 0, idArea = 0;
            var inverted = false;
            if (cellId != 0)
            {
                var description = await transferringLu.DescribeCellAsync(cellId);
                luTypeId = description.AllowedLoadUnitTypeId ?? 0;
                idArea = description.AreaId;
                inverted = description.Inverted;
            }

            switch (sourceItem.RecordStorageLength)
            {
                case RecordStorageTypes.FixedLength:
                    {
                        StoreLuWithFixedLength(transferringLu, sourceItem, inverted);

                        await VerifyCellToDefrag(transferringLu, luTypeId, cellId, idArea);
                        break;
                    }
                case RecordStorageTypes.ConvertDepthToPartition:
                    {
                        await StoreLuWithConvertDepthToPartition(transferringLu, sourceItem, cellId);

                        await VerifyCellToDefrag(transferringLu, luTypeId, cellId, idArea);
                        break;
                    }
                case RecordStorageTypes.FromPlc:
                    {
                        await StoreLuWithFromPlc(detectedDepths, transferringLu, sourceItem);
                        break;
                    }
            }
        }
    }

    /// <summary>
    /// Processa la coda di estrazione delle LU per il gruppo, gestendo la creazione di nuove missioni
    /// e la verifica delle condizioni di prelievo
    /// </summary>
    private async Task ProcessGroupExtractionLu()
    {
        if (device.IsExecutingGroupExtractionLu)
            return;

        try
        {
            device.IsExecutingGroupExtractionLu = true;

            if (device.GroupedLuToPickUp.Count > 0 && device.LUsLoaded.Count == 0 &&
                device.Status == DeviceStatusTypes.Ready)
            {
                if (device.LUsToPick.Count == 0)
                {
                    await StartGroupPickUp();
                }
                else
                {
                    if (device.LuManager != null && !await device.LuManager.CanPickUpLUs(device.LUsToPick))
                    {
                        foreach (var lu in device.LUsToPick)
                        {
                            if (lu is { LuId: > 0 })
                                Navigator.WriteLog(
                                    $"DEBUG INFO: Cancellazione MISSION_LU da ProcessGroupExtractionLU UDC: {lu.LuId} - {lu.LuCode} Date: {DateTime.Now:dd/MM/yyyy HH:mm:ss}",
                                    LogEntryType.Information);

                            if (lu != null) await DbInfo.SetExtractionItemQueueProcessed(lu.ExtractionQueueId, 0);
                            await Collections.ActiveMissions.Instance.RemoveUnused(lu);
                        }

                        device.LUsToPick.Clear();

                        device.ClearLuToPickUp();
                        return;
                    }

                    await StartPickUpGroupedMission();
                }
            }
            else if (device.QueuedLuToPickUpCount == 0)
            {
                var dtLu = await DbInfo.GetLuToExtract(device);

                if (dtLu == null || dtLu.Rows.Count == 0)
                    return;

                var cellId = default(decimal);
                var groupedMovementId = Guid.NewGuid();

                var lUtoExtract = new List<decimal>();
                DataRow? rowLastToExtract = null;

                var missionType = MissionTypes.None;

                foreach (DataRow item in dtLu.Rows)
                {
                    if (item.GetValueOrThrow<decimal>("ID_UDC") == 0 || (item.GetValueOrThrow<decimal?>("ID_LAVORAZIONE") != null &&
                                                                  Collections.ActiveMissions.Instance.FindIndex(x =>
                                                                      x != null &&
                                                                      x.LuId == item.GetValueOrThrow<decimal>("ID_UDC") &&
                                                                      x.NoteProcessingId !=
                                                                      item.GetValueOrThrow<decimal>("ID_LAVORAZIONE")) >= 0))
                    {
                        // Compito 21, il fatto senza missione: la riga di coda non e' costruibile -
                        // la sua unita' di carico e' impegnata in un'altra lavorazione, o la riga
                        // non ne ha nessuna - e l'assegnazione che stava aspettando resta senza chi
                        // la porti. La coda e' del WCS, quindi il fatto e' suo; la cancellazione
                        // dell'assegnazione e' una conseguenza del gestionale.
                        //
                        // Il motivo qui e' nuovo: prima di questo compito questo punto cancellava
                        // l'assegnazione senza scrivere niente da nessuna parte.
                        if (item.GetValueOrThrow<Guid?>("GUID_ASSEGNAZIONE_UDC") is { } assignmentGuid)
                        {
                            var queuedLuId = item.GetValueOrThrow<decimal>("ID_UDC");

                            await Navigator.RequireWarehouse("ProcessGroupExtractionLu")
                                .ReportUnservableQueuedExtractionAsync(new UnservableQueuedExtractionRequest(
                                    item.GetValueOrThrow<decimal>("ID_ESTRAZIONE_UDC"),
                                    queuedLuId,
                                    assignmentGuid,
                                    queuedLuId == 0
                                        ? "la riga in coda non ha piu' un'unita' di carico"
                                        : "l'unita' di carico e' gia' in missione per un'altra lavorazione"));
                        }

                        // Compito 12: l'esito della riga di estrazione e' una segnalazione, non piu'
                        // un UPDATE su DISTINTA_ESTRAZIONI_UDC seguito dalla chiusura della
                        // lavorazione. Il bivio di prima - «se la scrittura e' riuscita, togli la
                        // riga dalla coda» - non c'e' piu' perche' non c'e' piu' una scrittura che
                        // risponda falso: un gestionale che non risponde solleva, e la riga di coda
                        // resta dov'e' per il giro dopo. E' lo stesso che fa la segnalazione qui
                        // sopra, che di catch non ne ha mai avuto uno.
                        await DbInfo.ReportExtractionRowOutcome(item.GetValueOrThrow<decimal>("ID_ESTRAZIONE_UDC"),
                            ExtractionRowStatus.InOtherExtraction);
                        await DbInfo.DeleteExtractionItemQueueById(item.GetValueOrThrow<decimal>("ID_ESTRAZIONE_UDC"));

                        continue;
                    }

                    if (Collections.ActiveMissions.Instance.FindIndex(x =>
                            x != null && x.LuId == item.GetValueOrThrow<decimal>("ID_UDC")) >= 0 ||
                        device.QueuedLUtoPickUp.FindIndex(x =>
                            x != null && x.LuId == item.GetValueOrThrow<decimal>("ID_UDC")) >= 0)
                    {
                        await DbInfo.DeleteExtractionItemQueueById(item.GetValueOrThrow<decimal>("ID_ESTRAZIONE_UDC"));
                        continue;
                    }

                    if (cellId == 0)
                    {
                        cellId = item.GetValueOrThrow<decimal>("ID_CELLA");
                        await DbInfo.SetOperationLockingCell(item.GetValueOrThrow<decimal>("ID_CELLA"),
                            StatoOperazioneCella.Prelievo);
                    }
                    else if (cellId != item.GetValueOrThrow<decimal>("ID_CELLA"))
                    {
                        break;
                    }

                    rowLastToExtract = item;

                    var navigatorCommand = new Command
                    {
                        Date = DateTime.Now,
                        CommandType = Command.CommandTypes.MoveLuAutomatic,
                        LuId = item.GetValueOrThrow<decimal>("ID_UDC"),
                        Status = Command.StatusTypes.Done,
                        WorkstationId = item.GetValueOrThrow<string?>("ID_POSTAZIONE"),
                        BufferCellId = item.GetValueOrThrow<decimal>("ID_CELLA_POLMONE"),
                        PickUpDoorCode = item.GetValueOrThrow<string?>("CODICE_PORTA")
                    };

                    if (item.GetValueOrThrow<bool>("ESTRARRE"))
                    {
                        lUtoExtract.Add(item.GetValueOrThrow<decimal>("ID_UDC"));

                        // Compito 16: ex CelleMagazzino.DecodificaCella, di cui si leggeva la sola
                        // porta A. Il metodo vecchio rispondeva annullabile e qui si scriveva
                        // ?.CODICE_PORTA_A; DescribeCellAsync risponde non annullabile e tratta
                        // l'assenza come guasto, quindi la tolleranza per «nessuna cella polmone»
                        // diventa una guardia esplicita sullo zero. Chiamata diretta e non dalla
                        // missione: qui la missione non c'e' ancora (nasce sotto, con
                        // AddMissionToGroupPickUp), come per GetCellContentAsync poco piu' giu'.
                        var bufferDoorCodeA = navigatorCommand.BufferCellId == 0
                            ? null
                            : (await Navigator
                                .RequireWarehouse($"DescribeCellAsync sulla cella polmone {navigatorCommand.BufferCellId}")
                                .DescribeCellAsync(navigatorCommand.BufferCellId)).DoorCodeA;

                        missionType = MissionTypes.Extraction;

                        navigatorCommand.StorageDoorCode = bufferDoorCodeA;
                    }

                    await AddMissionToGroupPickUp(navigatorCommand, groupedMovementId, item, missionType);
                }

                if (lUtoExtract.Count > 0 && cellId > 0 && rowLastToExtract != null)
                {
                    // Compito 9: ex DbInfo.GetLUsInCell(cellId). Nessuna missione ancora costruita in
                    // questo punto (le missioni nascono sotto, con AddMissionToGroupPickUp): chiamata
                    // diretta, come MoveObstacleLu/RecoverPickUpQueue del compito 7.
                    var lUsInCell = await Navigator.RequireWarehouse($"GetCellContentAsync sulla cella {cellId}")
                        .GetCellContentAsync(cellId);

                    if (lUsInCell.Count == lUtoExtract.Count) return;

                    foreach (var lu in lUsInCell.Where(lu => !lUtoExtract.Contains(lu)))
                    {
                        rowLastToExtract["ID_UDC"] = lu;

                        missionType = MissionTypes.PickUpAndStoreAgain;

                        var navigatorCommand = new Command
                        {
                            Date = DateTime.Now,
                            CommandType = Command.CommandTypes.MoveLuAutomatic,
                            LuId = lu,
                            Status = Command.StatusTypes.Done,
                            WorkstationId = rowLastToExtract.GetValueOrThrow<string?>("ID_POSTAZIONE"),
                            BufferCellId = rowLastToExtract.GetValueOrThrow<decimal>("ID_CELLA_POLMONE"),
                            PickUpDoorCode = rowLastToExtract.GetValueOrThrow<string?>("CODICE_PORTA"),
                            StorageDoorCode = rowLastToExtract.GetValueOrThrow<string?>("CODICE_PORTA")
                        };

                        await DbInfo.SetOperationLockingCell(cellId, StatoOperazioneCella.Deposito);

                        await AddMissionToGroupPickUp(navigatorCommand, groupedMovementId, rowLastToExtract,
                            missionType);
                    }
                }
                else
                {
                    device.LastGroupMovement = GroupMovementType.None;
                }
            }
            else if (device.LUsToPick.Count > 0)
            {
                if (_numCheckLUsToPick == 10)
                {
                    _numCheckLUsToPick = 0;
                    device.LUsToPick.Clear();
                }
                else
                {
                    _numCheckLUsToPick++;
                }
            }
        }
        catch (Exception ex)
        {
            device.LogError(ex);
        }
        finally
        {
            device.IsExecutingGroupExtractionLu = false;
        }
    }

    /// <summary>
    /// Avvia il processo di prelievo di gruppo, verificando le condizioni e validando le LU da prelevare
    /// </summary>
    private async Task StartGroupPickUp()
    {
        if (device.GroupedLuToPickUp.Count == 0 || device.LUsLoaded.Count != 0 || device.LUsToPick.Count != 0 ||
            device.Status is not (DeviceStatusTypes.Ready or DeviceStatusTypes.Recovering))
        {
            device.GroupExecuteStepRunning = false;
            return;
        }

        var lUsToVerify = device.GroupedLuToPickUp;

        if (device.LuManager != null && !await device.LuManager.CanPickUpLUs(lUsToVerify))
        {
            foreach (var lu in lUsToVerify)
            {
                if (lu is { LuId: > 0 })
                    Navigator.WriteLog(
                        $"DEBUG INFO: Cancellazione MISSION_LU da StartGroupPickUp UDC: {lu.LuId} - {lu.LuCode} Date: {DateTime.Now:dd/MM/yyyy HH:mm:ss}",
                        LogEntryType.Information);

                if (lu != null) await DbInfo.SetExtractionItemQueueProcessed(lu.ExtractionQueueId, 0);
                await Collections.ActiveMissions.Instance.RemoveUnused(lu);
            }

            lUsToVerify.Clear();

            device.ClearLuToPickUp();

            device.GroupExecuteStepRunning = false;
            return;
        }

        device.LUsToPick = lUsToVerify;

        foreach (var lu in lUsToVerify)
            foreach (var lUinQueue in device.QueuedLUtoPickUp.Where(x =>
                         lu != null && x != null && x.MissionId == lu.MissionId))
            {
                device.RemoveLuToPickUp(lUinQueue);
                break;
            }

        foreach (var lu in device.LUsToPick)
            Collections.ActiveMissions.Instance.Add(lu);

        await StartPickUpGroupedMission();
        device.GroupExecuteStepRunning = false;
    }

    /// <summary>
    /// Avvia una missione di caricamento raggruppata per più LU contemporaneamente
    /// </summary>
    private async Task StartLoadingGroupedMission()
    {
        if (device.DeviceType != DeviceTypes.GroupOfAutomationDevices || device.GroupItemId == 0 ||
            !device.IsReadyToExecuteStep ||
            device.LUsLoaded.Count != 0 || device.LUsReadyToLoad.Count == 0)
        {
            device.GroupExecuteStepRunning = false;
            return;
        }

        try
        {
            var stepsToDo = device.LUsReadyToLoadSteps;

            if (stepsToDo is { Count: > 0 })
            {
                // StepExecutor e' costruito da Initialize e non torna mai nullo: la guardia
                // sostituisce "await x?.M()", che sollevava NullReferenceException.
                if (device.StepExecutor != null) await device.StepExecutor.ExecuteGroupedMOVE_LU_TO(stepsToDo);

                device.LastGroupMovement = GroupMovementType.Loading;
            }
            else
            {
                device.GroupExecuteStepRunning = false;
            }
        }
        catch (Exception ex)
        {
            device.LogError(ex);
            device.GroupExecuteStepRunning = false;
        }
    }

    /// <summary>
    /// Avvia una missione di caricamento singola per una LU alla volta
    /// </summary>
    private async Task StartLoadingSingleMission()
    {
        if (device.DeviceType != DeviceTypes.GroupOfAutomationDevices ||
            device.GroupItemId == 0 ||
            !device.IsReadyToExecuteStep ||
            !(!device.WaitBelongedItemsEmptyBeforeLoading ||
              device is { WaitBelongedItemsEmptyBeforeLoading: true, LUsLoaded.Count: 0 }) ||
            device.LUsReadyToLoad.Count == 0)
        {
            device.GroupExecuteStepRunning = false;
            return;
        }

        var stepsToReceive = device.LUsReadyToLoadSteps;

        try
        {
            if (device.StepExecutor != null) await device.StepExecutor.ExecuteSingleMOVE_LU_TO(stepsToReceive);

            device.LastGroupMovement = GroupMovementType.Loading;
        }
        catch (Exception ex)
        {
            device.LogError(ex);
            device.GroupExecuteStepRunning = false;
        }
    }

    /// <summary>
    /// Avvia una missione di prelievo raggruppata per più LU contemporaneamente
    /// </summary>
    private async Task StartPickUpGroupedMission()
    {
        if (device.DeviceType != DeviceTypes.GroupOfAutomationDevices || device.GroupItemId == 0 ||
            !device.IsReadyToExecuteStep ||
            !(!device.WaitBelongedItemsEmptyBeforeLoading ||
              device is { WaitBelongedItemsEmptyBeforeLoading: true, LUsLoaded.Count: 0 }) ||
            device.LUsToPick.Count == 0)
        {
            device.GroupExecuteStepRunning = false;
            return;
        }

        if (!await VerifyLUtoPickUp(device.LUsToPick))
        {
            foreach (var lu in device.LUsToPick.OfType<PathMission>())
            {
                lu.GuidAssignmentLu = Guid.Empty;

                await DbInfo.SetExtractionItemQueueProcessed(lu.ExtractionQueueId, 0);

                lu.ExtractionQueueId = 0;

                if (lu.LuId > 0)
                    Navigator.WriteLog(
                        $"DEBUG INFO: Cancellazione MISSION_LU da CanPickUpLUs UDC: {lu.LuId} - {lu.LuCode} Date: {DateTime.Now:dd/MM/yyyy HH:mm:ss}",
                        LogEntryType.Information);

                await Collections.ActiveMissions.Instance.RemoveUnused(lu);
            }

            device.LUsToPick.Clear();

            device.GroupExecuteStepRunning = false;
            return;
        }

        try
        {
            // StepExecutor e' costruito da Initialize e non torna mai nullo: la guardia
            // sostituisce "await x?.M()", che sollevava NullReferenceException.
            if (device.StepExecutor != null)
                await device.StepExecutor.ExecuteGroupedMOVE_LU_TO(device.LUsToPickSteps);

            device.LastGroupMovement = GroupMovementType.Extracting;
        }
        catch (Exception ex)
        {
            device.LogError(ex);
            device.GroupExecuteStepRunning = false;
        }
    }

    /// <summary>
    /// Avvia una missione di trasferimento raggruppata per più LU contemporaneamente
    /// </summary>
    private async Task StartTransferringGroupedMission()
    {
        if (device.DeviceType != DeviceTypes.GroupOfAutomationDevices || !device.IsReadyToExecuteStep ||
            device.GroupItemId == 0 ||
            device.LUsLoaded.Count == 0 || device.LUsReadyToTransfer.Count == 0)
        {
            device.GroupExecuteStepRunning = false;
            return;
        }

        try
        {
            // StepExecutor e' costruito da Initialize e non torna mai nullo: la guardia
            // sostituisce "await x?.M()", che sollevava NullReferenceException.
            if (device.StepExecutor != null)
                await device.StepExecutor.ExecuteGroupedMOVE_LU_TO(device.LUsReadyToTransferSteps);
        }
        catch (Exception ex)
        {
            device.LogError(ex);
            device.GroupExecuteStepRunning = false;
        }
    }

    /// <summary>
    /// Avvia una missione di trasferimento singola per una LU alla volta
    /// </summary>
    private async Task StartTransferringSingleMission()
    {
        if (device.LUsReadyToTransfer.Count == 0)
        {
            device.GroupExecuteStepRunning = false;
            return;
        }

        var stepsToTransfer = device.LUsReadyToTransferSteps;

        try
        {
            if (device.StepExecutor != null) await device.StepExecutor.ExecuteSingleMOVE_LU_TO(stepsToTransfer);
        }
        catch (Exception ex)
        {
            device.LogError(ex);
            device.GroupExecuteStepRunning = false;
        }
    }

    /// <summary>
    /// Verifica se una cella necessita di compattazione basandosi sul tipo di LU e area
    /// </summary>
    /// <param name="transferringLu">LU in trasferimento</param>
    /// <param name="luTypeId">ID del tipo di LU</param>
    /// <param name="cellId">ID della cella</param>
    /// <param name="idArea">ID dell'area</param>
    /// <remarks>
    /// <para>
    /// Il verdetto lo da' il gestionale (compito 4): <c>DbInfo.IsCellToDefragmentByLuType</c>
    /// leggeva <c>CELLE_AREE</c>, <c>POSIZIONI_UDC</c> e <c>AREE_MAGAZZINO</c> in T-SQL diretto e
    /// applicava qui le due soglie (85 % e 10 %). Adesso la politica sta di la' dal confine e qui
    /// arriva un si' o un no; il guasto non e' piu' un falso scritto nel registro ma una
    /// <c>WarehouseUnavailableException</c>, che si ferma nel <c>catch</c> per giro di
    /// <c>StepDoneGroup</c>.
    /// </para>
    /// <para>
    /// <b>Le guardie a buon mercato restano prima</b>, e la domanda al confine e' l'ultima cosa:
    /// era il cortocircuito dell'<c>&amp;&amp;</c> di prima, ed e' il motivo per cui la condizione
    /// e' rovesciata in un'uscita anticipata invece che estratta in una variabile attesa. Estrarre
    /// la chiamata prima della condizione avrebbe interrogato il gestionale a ogni deposito, anche
    /// con la compattazione spenta.
    /// </para>
    /// </remarks>
    private async Task VerifyCellToDefrag(PathMission transferringLu, decimal luTypeId, decimal cellId, decimal idArea)
    {
        // Se è attiva la compattazione richiede la verifica che la cella sia compatta
        if (cellId <= 0 ||
            luTypeId <= 0 ||
            idArea <= 0 ||
            Navigator.Defragmentation != DefragmentationTypes.Enabled ||
            !device.EnableDefragmentation || transferringLu.MissionType == MissionTypes.StoringByPickUp)
        {
            device.IdCellToDefragment = 0;
            return;
        }

        var verdict = await Navigator
            .RequireWarehouse($"GetAreaDefragmentationVerdictAsync sull'area {idArea} per il tipo {luTypeId}")
            .GetAreaDefragmentationVerdictAsync(idArea, luTypeId);

        device.IdCellToDefragment = verdict.Needed ? cellId : 0;
    }

    #endregion Private Methods

    #region Private Classes

    /// <summary>
    /// Rappresenta una LU da riposizionare dopo il completamento di un'operazione di gruppo
    /// </summary>
    private class RelocateLu
    {

        #region Public Properties

        /// <summary>
        /// Il dispositivo di destinazione per il riposizionamento
        /// </summary>
        public AutomationDevice? DestinationItem { get; init; }

        /// <summary>
        /// ID univoco della missione associata
        /// </summary>
        public Guid MissionId { get; init; }

        /// <summary>
        /// Il dispositivo sorgente da cui proviene la LU
        /// </summary>
        public AutomationDevice? SourceItem { get; set; }

        #endregion Public Properties

    }

    #endregion Private Classes

}
