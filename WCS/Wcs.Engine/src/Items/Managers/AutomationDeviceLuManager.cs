using System.Diagnostics;
using System.Text.Json;
using Wcs.Engine.ORM;
using Wcs.Engine.Persistence.Entities;
using Log.Interface;
using Wcs.Wms.Contracts;
using static Wcs.Common.Enums;
using static Wcs.Engine.Items.AutomationDevice;
using static Wcs.Engine.Items.PathMission;

namespace Wcs.Engine.Items.Managers;

/// <summary>
/// Gestisce le operazioni di caricamento, scaricamento e trasferimento delle unità di carico (LU) per i dispositivi di automazione.
/// Fornisce funzionalità per la validazione delle condizioni di trasferimento, l'esecuzione di operazioni di prelievo e deposito,
/// e la gestione del ciclo di vita delle missioni di percorso associate alle LU.
/// </summary>
/// <param name="device">Il dispositivo di automazione associato a questo manager</param>
public class AutomationDeviceLuManager(AutomationDevice device)
{

    #region Private Fields

    /// <summary>
    /// Indice dell'ultimo buffer di destinazione utilizzato per la distribuzione circolare
    /// </summary>
    private int _indexLastBufferDestination;

    /// <summary>
    /// ID dell'ultima lavorazione utilizzata per il buffer di destinazione
    /// </summary>
    private decimal _lastIdLavorazioneBufferDestination;

    #endregion Private Fields

    #region Public Methods

    /// <summary>
    /// Carica una LU sul dispositivo specificato dalla missione fornita
    /// </summary>
    /// <param name="missionId">ID della missione contenente la LU da caricare</param>
    /// <param name="activateExecuteStep">Indica se attivare automaticamente l'esecuzione del prossimo step</param>
    public async Task LoadLu(Guid missionId, bool activateExecuteStep = true)
    {
        if (missionId == Guid.Empty)
            return;

        var luToLoad = Collections.ActiveMissions.Instance.FirstOrDefault(x => x?.MissionId == missionId);
        if (luToLoad == null)
            return;

        Debug.Print($"LoadLU -> ASI {device.ASI}.{device.SUBITEM}.{device.PARTITION} LU ID: {luToLoad.LuId}");

        var (isToReadLuInfo, isToExecuteStep, isToCheckBuildRoute) = ProcessGroupMovements(luToLoad);

        var previousPositionItemId = luToLoad.PositionItemId;
        luToLoad.PositionItemId = device.ItemGuid;

        if (device.OnLoadLuActiveFilter)
            luToLoad.ActiveFilter = true;

        var result = await ProcessDeviceSpecificLogic(previousPositionItemId, activateExecuteStep);

        isToReadLuInfo |= result.isToReadLuInfo;
        isToExecuteStep |= result.isToExecuteStep;
        isToCheckBuildRoute |= result.isToCheckBuildRoute;

        ExecuteNextAction(isToReadLuInfo, isToExecuteStep, isToCheckBuildRoute);
    }

    /// <summary>
    /// Gestisce lo scaricamento di una LU dal dispositivo ASI
    /// </summary>
    /// <param name="value">Tipo di step che ha causato lo scaricamento (automatico o manuale)</param>
    /// <param name="onArrival">
    /// Vero quando la chiamata arriva da dentro <see cref="LoadLu"/>, cioe' mentre il dispositivo
    /// sta ricevendo l'UDC. In quel momento il trasferimento lo tiene <c>Busy</c> e lo rimette
    /// <c>Ready</c> solo nel <c>finally</c> di <c>ThreadLoadLu</c>, dopo che <c>LoadLu</c> e' tornato:
    /// con la sola guardia sullo stato, lo scarico automatico all'arrivo usciva in silenzio e non
    /// scattava mai per un'UDC portata da un movimento (funzionava solo al passaggio in Run, quando
    /// il dispositivo e' Ready). Qui il <c>Busy</c> dell'arrivo e' ammesso; ogni altro stato non
    /// valido continua a fermare lo scarico.
    /// </param>
    public async Task LuUnloadedFromAsi(StepTypes value, bool onArrival = false)
    {
        if (!IsValidForUnloading())
            return;

        if (!IsStatusValidForUnloading(device.Status)
            && !(onArrival && device.Status == DeviceStatusTypes.Busy))
            return;

        // All'arrivo il setter della posizione ha appena avviato in sfondo l'allocazione dell'UDC
        // nella cella collegata: se lo scarico partisse adesso, la disallocazione da scarico
        // precederebbe l'allocazione e la posizione risorgerebbe nel gestionale dopo che l'UDC e'
        // uscita. Si aspetta quel lavoro (non solleva mai: la sua lambda cattura tutto), e solo
        // chi scarica all'arrivo paga l'attesa — il movimento fra un salto e l'altro no.
        if (onArrival && device.LoadedLu is { } arrived)
        {
            await arrived.PositionWork;

            // L'attesa e' per chi guarda: senza, l'UDC spariva dall'uscita nell'istante dell'arrivo.
            // Dopo l'attesa si ricontrolla che l'UDC sia ancora a bordo: nel frattempo puo' averla
            // scaricata un comando dell'operatore.
            if (Navigator.AutoUnloadDelay > TimeSpan.Zero)
            {
                await Task.Delay(Navigator.AutoUnloadDelay);
                if (!ReferenceEquals(device.LoadedLu, arrived)) return;
            }
        }

        if (device.LoadedLu is { ExtractionQueueId: > 0 } pending)
        {
            await pending.PositionWork;
            if (!ReferenceEquals(device.LoadedLu, pending)) return;

            // La posizione puo' essere cambiata dopo il primo tentativo: il retry dell'handoff gia'
            // dovuto non dipende piu' dal collegamento allo Storage del dispositivo corrente. Vale
            // anche per lo scarico manuale: nessuna via puo' staccare missione e coda prima del
            // commit locale. Un guasto WMS successivo al commit continua invece a non bloccare.
            if (!await pending.TryPersistExtractionOutcomeBeforeUnload()) return;
        }

        await ProcessUnloadingByDeviceType(value);
    }

    /// <summary>
    /// Avvia il processo di rifiuto della LU attualmente caricata sul dispositivo
    /// </summary>
    public async Task RejectLu()
    {
        if (device.LUsLoaded is { Count: 0 })
            return;

        if (!IsStatusValidForRejection(device.Status))
            return;

        device.IdCellToDefragment = 0;
        await ProcessRejectionByDeviceType();
    }

    #endregion Public Methods

    #region Internal Methods

    /// <summary>
    /// Verifica se il dispositivo può accettare la LU specificata
    /// </summary>
    /// <param name="luToCheck">La LU da verificare per l'accettazione</param>
    /// <returns>True se il dispositivo può accettare la LU, false altrimenti</returns>
    internal bool CanAcceptLu(PathMission? luToCheck)
    {
        if (luToCheck == null)
            return false;

        try
        {
            CheckLuToReceive();

            // Check if device has LUs loaded
            if (device.LUsLoaded is { Count: > 0 })
            {
                luToCheck.MovingDescription = "The destination item has an LU loaded";
                return false;
            }

            // Check device status and running step in one condition
            if (device.Status != DeviceStatusTypes.Ready || device.RunningStepId != Guid.Empty)
            {
                luToCheck.MovingDescription = "The destination item is busy";
                return false;
            }

            // Check if waiting for different LU (inline for better performance)
            if (IsWaitingForDifferentLu(luToCheck))
            {
                luToCheck.MovingDescription = "The destination item is waiting for a different LU";

                return false;
            }

            // Check crossroad
            if (CheckCrossroad(luToCheck) != null)
            {
                luToCheck.MovingDescription = "The way to go is busy";
                return false;
            }

            // Final acceptance logic evaluation
            var canAccept = EvaluateAcceptanceLogic(luToCheck);
            if (canAccept)
                luToCheck.MovingDescription = string.Empty;

            return canAccept;
        }
        catch (Exception ex)
        {
            device.LogError(ex);
            return false;
        }
    }

    /// <summary>
    /// Verifica se il dispositivo può prelevare la LU specificata
    /// </summary>
    /// <param name="luToCheck">La LU da verificare per il prelievo</param>
    /// <returns>True se il dispositivo può prelevare la LU, false altrimenti</returns>
    internal bool CanPickUpLu(PathMission? luToCheck)
    {
        CheckLuToReceive();

        var stepToDo = StepToDoDestination(luToCheck);

        // Early validation checks
        var validationResult = ValidatePickupConditions(luToCheck, stepToDo);
        if (!validationResult.isValid)
        {
            if (luToCheck != null)
                luToCheck.MovingDescription = validationResult.reason;
            return validationResult.isValid;
        }

        // La prontezza si verifica sul dispositivo a cui lo step pianificato manda davvero l'UDC
        // (quello che ExecuteStep eseguira'), non su una rotta ricalcolata adesso, che con lo stato
        // cambiato nel frattempo poteva indicare un altro dispositivo. Senza uno step pianificato
        // che parta da qui (la missione e' ancora in coda di prelievo, oppure la sua rotta non
        // passa da questo dispositivo) resta la sonda di prima, cosi' un'UDC non sale a bordo se
        // il salto successivo non e' pronto.
        var step = device.StepToDoSource(luToCheck)
                   ?? luToCheck?.GetMissionStepsToDestination().FirstOrDefault(x => x.FromNodeId == device.ItemGuid);

        var canPickup = step?.DestinationItem?.IsReadyToReceiveLu ?? true;

        if (luToCheck != null)
        {
            luToCheck.MovingDescription = canPickup ? string.Empty : "The destination item is not ready to accept LU";
        }

        return canPickup;
    }

    /// <summary>
    /// Verifica se il dispositivo può prelevare tutte le LU specificate nella lista
    /// </summary>
    /// <param name="missions">Lista delle missioni contenenti le LU da verificare</param>
    /// <returns>True se tutte le LU possono essere prelevate, false altrimenti</returns>
    internal async Task<bool> CanPickUpLUs(List<PathMission?> missions)
    {
        if (missions.Count == 0)
            return false;

        return ValidateStorageDepths(missions) && await ValidateAllMissionsPickupable(missions);
    }

    /// <summary>
    /// Controlla se esistono conflitti di percorso (crossroad) per la LU specificata
    /// </summary>
    /// <param name="luToCheck">La LU da verificare per conflitti di percorso</param>
    /// <returns>Il dispositivo che causa il conflitto, null se non ci sono conflitti</returns>
    internal AutomationDevice? CheckCrossroad(PathMission? luToCheck)
    {
        if (luToCheck == null)
            return null;

        try
        {
            if (device.LoadedLu != null)
                return ValidateLoadedLuCrossroad();

            if (device.IsLinkedToStorage && luToCheck.PositionItem != null)
                return ValidateStorageLinkCrossroad();

            if (luToCheck.DestinationItem == null || luToCheck.DestinationItem == device)
                return null;

            if (device.LoadedLu == null && !device.DestinationItemsLinked.Contains(device))
                return null;

            var step = device.StepToDoSource(luToCheck);
            return step?.DestinationItem?.LuManager?.CheckCrossroad(luToCheck);
        }
        catch (Exception ex)
        {
            device.LogError(ex);
            return null;
        }
    }

    /// <summary>
    /// Controlla e gestisce le operazioni di prelievo e ricezione LU in coda
    /// </summary>
    internal async Task CheckGetFromItem()
    {
        while (true) // Replace goto with while loop
        {
            if (device.LUtoPick != null)
            {
                if (!CanPickUpLu(device.LUtoPick) ||
                    (device is not { CanExtractLUs: true, Status: DeviceStatusTypes.Ready } &&
                     device.Status != DeviceStatusTypes.Warning)) return; // Exit if cannot pick up
                if (await VerifyPickUp(device.LUtoPick))
                {
                    if (device.StepExecutor != null) await device.StepExecutor.StartPickUpLu(device.LUtoPick);
                    return;
                }

                await CleanupFailedPickup();
                continue; // Replace goto with continue
            }

            if (device.LUtoReceive != null)
            {
                if (await ProcessReceivingLu())
                    return;

                device.LUtoReceive = null;
            }

            // StepExecutor e' costruito da Initialize e non torna mai nullo: la guardia
            // sostituisce "await x?.M()", che sollevava NullReferenceException.
            if (device.StepExecutor != null) await device.StepExecutor.ExecuteStepOnQueuedProcedure();
            return;
        }
    }

    /// <summary>
    /// Verifica e pulisce i riferimenti alle LU da ricevere
    /// </summary>
    internal void CheckLuToReceive()
    {
        if (device.LUtoReceive == null && device.DevicesReadyToTransferLuCount == 0)
            return;

        device.PruneDevicesReadyToTransferLu(x => x.LoadedLu == null && !x.AcceptLuIfDestinationIsAvailable);

        ValidateAndCleanupLuToReceive();
    }

    /// <summary>
    /// Verifica la disponibilità di uno step per la LU specificata
    /// </summary>
    /// <param name="luToCheck">La LU per cui verificare la disponibilità dello step</param>
    /// <returns>Stato di disponibilità dello step</returns>
    internal StepAvailableStatusTypes StepIsAvailable(PathMission? luToCheck)
    {
        if (luToCheck == null || Collections.MissionSteps.Instance.Count == 0)
            return StepAvailableStatusTypes.NotAvailable;

        var relevantSteps = Collections.MissionSteps.Instance
            .Where(x => x.MissionId == luToCheck.MissionId && x.FromNodeId == device.ItemGuid);

        foreach (var step in relevantSteps)
        {
            if (device.AcceptLuIfDestinationIsAvailable)
            {
                return step.DestinationItem is { IsReadyToReceiveLu: true }
                    ? StepAvailableStatusTypes.DestinationAvailable
                    : StepAvailableStatusTypes.DestinationNotAvailable;
            }
            return StepAvailableStatusTypes.Available;
        }

        return StepAvailableStatusTypes.NotAvailable;
    }

    /// <summary>
    /// Trova lo step di destinazione per la LU specificata
    /// </summary>
    /// <param name="value">La LU per cui trovare lo step di destinazione</param>
    /// <returns>Lo step di missione trovato, null se non trovato</returns>
    internal MissionStep? StepToDoDestination(PathMission? value)
    {
        if (Collections.MissionSteps.Instance.Count == 0 || value == null)
            return null;

        return Collections.MissionSteps.Instance.FirstOrDefault(x =>
            x.MissionId == value.MissionId && x.ToNodeId == device.ItemGuid);
    }

    /// <summary>
    /// Verifica che il prelievo della LU sia valido confrontando con i dati del magazzino
    /// </summary>
    /// <param name="luToCheck">La LU da verificare per il prelievo</param>
    /// <returns>True se il prelievo è valido, false altrimenti</returns>
    internal async Task<bool> VerifyPickUp(PathMission? luToCheck)
    {
        if (luToCheck == null || luToCheck.LuId == 0)
            return true;

        // Le celle che questo ricollocatore serve le sa il motore (mappa porte e RelocateItemId nel
        // suo store): il WMS riceve l'insieme e cerca la UDC solo li'. Era TrovaUdc(idUdc, idItem), con
        // la sottoquery su ITEMS_MAP e NAVIGATOR_ITEMS fatta dal WMS su tabelle che non ha piu'.
        var warehouse = Navigator.RequireWarehouse($"VerifyPickUp sull'unita' di carico {luToCheck.LuId}");
        var luPosition = await warehouse.FindLoadUnitAsync(new FindLoadUnitRequest(
            luToCheck.LuId, null, await DbInfo.GetCellsServedByRelocator(device.ItemGuid),
            LoadUnitLockFilter.Any, CellLockFilter.Any));
        if (luPosition == null)
            return false;

        var unloadingDoorCode = await warehouse.ChoosePickUpDoorAsync(luPosition.LoadUnitId, luPosition.CellId);
        var itemSource = await DbInfo.GetItemId(unloadingDoorCode.DoorCode);
        var storageItem = Collections.PathDevices.Instance[itemSource];

        return storageItem?.RelocateItemId == device.ItemGuid &&
               unloadingDoorCode.DoorCode == luToCheck.PickUpDoorCode &&
               (unloadingDoorCode.ObstacleCount == 0 || device.DeviceType == DeviceTypes.GroupOfAutomationDevices);
    }

    #endregion Internal Methods

    #region Private Methods

    /// <summary>
    /// Verifica se lo stato del dispositivo è valido per operazioni di rifiuto
    /// </summary>
    /// <param name="status">Lo stato del dispositivo da verificare</param>
    /// <returns>True se lo stato è valido per il rifiuto, false altrimenti</returns>
    private static bool IsStatusValidForRejection(DeviceStatusTypes status)
    {
        return status is DeviceStatusTypes.Ready or DeviceStatusTypes.Locked
                      or DeviceStatusTypes.Error or DeviceStatusTypes.Warning;
    }

    /// <summary>
    /// Verifica se lo stato del dispositivo è valido per operazioni di scaricamento
    /// </summary>
    /// <param name="status">Lo stato del dispositivo da verificare</param>
    /// <returns>True se lo stato è valido per lo scaricamento, false altrimenti</returns>
    private static bool IsStatusValidForUnloading(DeviceStatusTypes status)
    {
        return status is DeviceStatusTypes.Ready or DeviceStatusTypes.Locked
                      or DeviceStatusTypes.Error or DeviceStatusTypes.Warning;
    }

    /// <summary>
    /// Aggiunge la LU caricata alla coda per la costruzione del percorso
    /// </summary>
    private void AddLoadedLuToRouteToBuildQueue()
    {
        // Una lettura sola della missione a bordo, e lo sguardo alla coda insieme all'accodamento:
        // AutomationDevice.RequeueLoadedLu (voce 41, compito 7 e revisione finale).
        device.RequeueLoadedLu();
    }

    /// <summary>
    /// Pulisce i riferimenti di una LU quando il prelievo fallisce
    /// </summary>
    private async Task CleanupFailedPickup()
    {
        if (device.LUtoPick == null) return;

        if (device.LUtoPick.ExtractionQueueId > 0)
        {
            // Compito 13: qui c'era DbInfo.SetAssignmentsNotesProcessed, che marcava PRELEVATO = 1
            // l'assegnazione della riga di coda. Non e' diventata la segnalazione «assegnazione
            // servita», perche' qui l'assegnazione NON e' stata servita: il prelievo e' fallito, e
            // il fatto vero di questo punto lo dice la riga qui sotto, l'esito NotReachable sulla
            // riga di coda.
            //
            // La prima stesura aggiungeva che «la missione se n'e' andata senza servire» lo segnala
            // ActiveMissions.RemoveUnused, in fondo a questo metodo. NON E' VERO, e la revisione del
            // compito 13 l'ha corretto. In ActiveMissions.Remove le segnalazioni stanno dentro
            // if (eraSuUnDispositivo), e la prima anche dentro if (GuidAssignmentLu != Guid.Empty);
            // ma RemoveUnused passa da CheckAndRemoveMission, che chiama Remove SOLO quando
            // PositionItem e' nullo o e' un magazzino - la negazione esatta di eraSuUnDispositivo.
            // Per costruzione, da questa strada quelle segnalazioni non partono. Il GUID ci sarebbe
            // (lo assegnano AutomationDevice.AddMissionToPickUp e AutomationDeviceGroupManager dalla
            // GUID_ASSEGNAZIONE_UDC della riga di coda, quando la riga ne porta uno), e anche la
            // seconda strada - quella per riga di coda - sarebbe comunque muta, perche' la riga di
            // coda viene cancellata qui sotto, prima di RemoveUnused.
            //
            // Quindi sul lato dell'assegnazione di nota questo punto non segnala niente. Non lo
            // faceva nemmeno prima: PRELEVATO = 1 diceva «prelevata» di una UDC che nessuno era
            // riuscito a prelevare, cioe' il falso.
            await DbInfo.ReportExtractionRowOutcome(device.LUtoPick.ExtractionQueueId, ExtractionRowStatus.NotReachable);
            LogLuRemoval(device.LUtoPick, "CheckGetFromItem 1");
            await DbInfo.DeleteExtractionItemQueueById(device.LUtoPick.ExtractionQueueId);
        }

        LogLuRemoval(device.LUtoPick, "CheckGetFromItem 2");
        await Collections.ActiveMissions.Instance.RemoveUnused(device.LUtoPick);
        device.LUtoPick = null;
    }

    /// <summary>
    /// Pulisce i riferimenti alla LU specificata dal dispositivo
    /// </summary>
    /// <param name="loadedLu">La LU per cui pulire i riferimenti</param>
    private void CleanupLuReferences(PathMission loadedLu)
    {
        if (loadedLu == device.LUtoReceive) device.LUtoReceive = null;
        if (loadedLu == device.LUtoPick) device.LUtoPick = null;
    }

    /// <summary>
    /// Valuta la logica di accettazione per una LU specificata
    /// </summary>
    /// <param name="luToCheck">La LU da valutare per l'accettazione</param>
    /// <returns>True se la LU può essere accettata, false altrimenti</returns>
    private bool EvaluateAcceptanceLogic(PathMission luToCheck)
    {
        if (device.MissionSteps.Count == 0)
            return true;

        if (device.RespectQueue)
            return ValidateQueueRespect(luToCheck);

        if (luToCheck.DestinationItem == device)
            return true;

        var stepStatus = StepIsAvailable(luToCheck);
        return stepStatus is StepAvailableStatusTypes.Available
                          or StepAvailableStatusTypes.DestinationAvailable
                          or StepAvailableStatusTypes.NotAvailable;
    }

    /// <summary>
    /// Esegue la prossima azione basata sui flag specificati
    /// </summary>
    /// <param name="isToReadLuInfo">Indica se leggere le informazioni della LU</param>
    /// <param name="isToExecuteStep">Indica se eseguire il prossimo step</param>
    /// <param name="isToCheckBuildRoute">Indica se verificare la costruzione del percorso</param>
    private void ExecuteNextAction(bool isToReadLuInfo, bool isToExecuteStep, bool isToCheckBuildRoute)
    {
        if (isToReadLuInfo)
            ReadLuInfo();
        else if (isToExecuteStep)
            _ = device.ExecuteStep();
        else if (isToCheckBuildRoute)
            AddLoadedLuToRouteToBuildQueue();
    }

    /// <summary>
    /// Verifica se il dispositivo è in condizioni valide per lo scaricamento
    /// </summary>
    /// <returns>True se il dispositivo può scaricare, false altrimenti</returns>
    private bool IsValidForUnloading()
    {
        return device.LUsLoaded.Count > 0 &&
               (device.LoadedLu != null || device.DeviceType == DeviceTypes.GroupOfAutomationDevices);
    }

    /// <summary>
    /// Verifica se il dispositivo è in attesa di una LU diversa da quella specificata
    /// </summary>
    /// <param name="luToCheck">La LU da confrontare</param>
    /// <returns>True se il dispositivo è in attesa di una LU diversa, false altrimenti</returns>
    private bool IsWaitingForDifferentLu(PathMission luToCheck)
    {
        return device.LUtoReceive != null &&
               device.LUtoReceive != luToCheck &&
               device.LUtoReceive.PositionItem != null &&
               device.SourceItemsLinked.Contains(device.LUtoReceive.PositionItem);
    }

    /// <summary>
    /// Registra la rimozione di una LU nel log del sistema
    /// </summary>
    /// <param name="lu">La LU rimossa</param>
    /// <param name="context">Il contesto in cui è avvenuta la rimozione</param>
    private void LogLuRemoval(PathMission lu, string context)
    {
        if (lu is { LuId: > 0 })
        {
            Navigator.WriteLog(
                $"DEBUG INFO: Cancellazione MISSION_LU da {context} UDC: {lu.LuId} - {lu.LuCode} Date: {DateTime.Now:dd/MM/yyyy HH:mm:ss}",
                LogEntryType.Information);
        }
    }

    /// <summary>
    /// Gestisce la logica specifica per dispositivi di automazione normali
    /// </summary>
    /// <param name="previousPositionItemId">ID della posizione precedente</param>
    /// <param name="activateExecuteStep">Indica se attivare l'esecuzione dello step</param>
    private async Task<(bool isToReadLuInfo, bool isToExecuteStep, bool isToCheckBuildRoute)> NormalAutomationDevice(Guid previousPositionItemId, bool activateExecuteStep)
    {
        if (device.LoadedLu == null) return (false, false, false);

        CleanupLuReferences(device.LoadedLu);

        if (device.LoadedLu.DestinationItemId == device.ItemGuid &&
            device.LoadedLu.MissionType == MissionTypes.Rejection)
        {
            if (device.AutoUnloadLu) await LuUnloadedFromAsi(StepTypes.Automatic, onArrival: true);
            return (false, false, false); ;
        }

        bool isToReadLuInfo = false, isToCheckBuildRoute = false, isToExecuteStep = false;
        await SetDistributeOnBuffers();

        if (device.AutoUnloadLu)
        {
            await LuUnloadedFromAsi(StepTypes.Automatic, onArrival: true);
        }
        else if (device.LoadedLu.DestinationItemId == device.ItemGuid)
        {
            var result = ProcessDestinationLogic(previousPositionItemId, activateExecuteStep);
            isToReadLuInfo = result.isToReadLuInfo;
            isToCheckBuildRoute = result.isToCheckBuildRoute;
            isToExecuteStep = result.isToExecuteStep;
        }
        else
        {
            var result = ProcessNonDestinationLogic(activateExecuteStep);
            isToExecuteStep = result.isToExecuteStep;
            isToCheckBuildRoute = result.isToCheckBuildRoute;
        }

        return (isToReadLuInfo, isToExecuteStep, isToCheckBuildRoute);
    }

    /// <summary>
    /// Gestisce la logica per dispositivi di tipo Buffer
    /// </summary>
    private async Task ProcessBufferDevice()
    {
        if (device.LoadedLu == null) return;
        CleanupLuReferences(device.LoadedLu);
        ShowItemMessage();

        if (device.AutoUnloadLu)
           await LuUnloadedFromAsi(StepTypes.Automatic, onArrival: true);
    }

    /// <summary>
    /// Gestisce la logica quando il dispositivo corrente è la destinazione della LU
    /// </summary>
    /// <param name="previousPositionItemId">ID della posizione precedente</param>
    /// <param name="activateExecuteStep">Indica se attivare l'esecuzione dello step</param>
    private (bool isToReadLuInfo, bool isToExecuteStep, bool isToCheckBuildRoute) ProcessDestinationLogic(Guid previousPositionItemId, bool activateExecuteStep)
    {

        if (device.BelongedGroup != null) return (false, false, false);

        var isToReadLuInfo = false;
        var isToCheckBuildRoute = false;
        var isToExecuteStep = false;

        if (device.LoadedLu?.StepToDo != null)
        {
            if (activateExecuteStep)
                isToExecuteStep = true;
        }
        else if (ShouldReadLuInfo(previousPositionItemId))
        {
            isToReadLuInfo = true;
        }
        else if (ShouldBuildRoute() || device.LoadedLu is { RelocatorArrival: Api.RelocatorArrival.Deposit, RelocatorArrivalApplies: true })
        {
            // Item 54, decision 5: a load unit the operator sent to this relocator «to deposit it» goes to
            // the route loop also where the relocator's configuration would leave it standing (mode No,
            // no fixed destination): the loop asks the warehouse for a cell the relocator reaches
            // (Navigator.FindWhereStoreLu).
            isToCheckBuildRoute = true;
        }
        return (isToReadLuInfo, isToExecuteStep, isToCheckBuildRoute);
    }

    /// <summary>
    /// Gestisce la logica specifica per ogni tipo di dispositivo
    /// </summary>
    /// <param name="previousPositionItemId">ID della posizione precedente</param>
    /// <param name="activateExecuteStep">Indica se attivare l'esecuzione dello step</param>
    private async Task<(bool isToReadLuInfo, bool isToExecuteStep, bool isToCheckBuildRoute)> ProcessDeviceSpecificLogic(Guid previousPositionItemId, bool activateExecuteStep)
    {
        (bool isToReadLuInfo, bool isToExecuteStep, bool isToCheckBuildRoute) result;

        result.isToExecuteStep = false;
        result.isToCheckBuildRoute = false;
        result.isToReadLuInfo = false;

        switch (device.DeviceType)
        {
            case DeviceTypes.LoadingAndBuffer:
                result = await ProcessLoadingAndBufferDevice(previousPositionItemId, activateExecuteStep);
                break;

            case DeviceTypes.Buffer:
                await ProcessBufferDevice();
                break;

            case DeviceTypes.Storage:
                await ProcessStorageDevice();
                break;

            default:
                result = await NormalAutomationDevice(previousPositionItemId, activateExecuteStep);
                break;
        }

        return result;
    }

    /// <summary>
    /// Gestisce i movimenti di gruppo per la LU da caricare
    /// </summary>
    /// <param name="luToLoad">La LU da caricare</param>
    /// <returns>Tupla che indica le azioni da eseguire successivamente</returns>
    private (bool isToReadLuInfo, bool isToExecuteStep, bool isToCheckBuildRoute) ProcessGroupMovements(PathMission luToLoad)
    {
        var sourceItem = luToLoad.PositionItem;

        // Process source item group logic
        if (sourceItem?.BelongedGroup != null)
        {
            ProcessSourceGroupLogic(sourceItem, luToLoad);
        }

        // Process destination item group logic  
        if (device.BelongedGroup is not { GroupedMoveLuTo: false } ||
            sourceItem?.BelongedGroup is not { IsReadyToExecuteStep: true }) return (false, false, false);
        device.BelongedGroup.Status = DeviceStatusTypes.Ready;
        device.BelongedGroup.GroupExecuteStepRunning = false;

        return (false, false, false);
    }

    /// <summary>
    /// Gestisce lo scaricamento per dispositivi di tipo gruppo
    /// </summary>
    /// <param name="value">Tipo di step</param>
    private async Task ProcessGroupUnloading(StepTypes value)
    {
        if (device.LUsLoaded is { Count: 0 })
            return;

        // BelongedItems puo' contenere voci nulle: una voce nulla non ha nulla da scaricare
        // e il ramo nullo resta silenzioso, come altrove sulla stessa collezione.
        foreach (var itemBelonged in device.BelongedItems)
            if (itemBelonged != null)
                await itemBelonged.LuUnloadedFromAsi(value);
    }

    /// <summary>
    /// Gestisce la logica per dispositivi di tipo LoadingAndBuffer
    /// </summary>
    /// <param name="previousPositionItemId">ID della posizione precedente</param>
    /// <param name="activateExecuteStep">Indica se attivare l'esecuzione dello step</param>
    private async Task<(bool isToReadLuInfo, bool isToExecuteStep, bool isToCheckBuildRoute)> ProcessLoadingAndBufferDevice(Guid previousPositionItemId, bool activateExecuteStep)
    {
        if (device.LoadedLu == null) return (false, false, false);

        bool isToReadLuInfo = false, isToCheckBuildRoute = false, isToExecuteStep = false;

        if (device.LoadedLu.DestinationItem == device)
        {
            CleanupLuReferences(device.LoadedLu);
            ShowItemMessage();

            if (device.AutoUnloadLu)
                await LuUnloadedFromAsi(StepTypes.Automatic, onArrival: true);
        }
        else
        {
            var result = await NormalAutomationDevice(previousPositionItemId, activateExecuteStep);
            isToReadLuInfo = result.isToReadLuInfo;
            isToCheckBuildRoute = result.isToCheckBuildRoute;
            isToExecuteStep = result.isToExecuteStep;
        }

        return (isToReadLuInfo, isToExecuteStep, isToCheckBuildRoute);
    }

    /// <summary>
    /// Gestisce la logica di business per una LU specifica
    /// </summary>
    /// <param name="luId">ID della LU</param>
    /// <param name="value">Tipo di step</param>
    private async Task ProcessLuBusinessLogic(decimal luId, StepTypes value)
    {
        var mission = device.LoadedLu
            ?? throw new InvalidOperationException($"ReleaseLoadUnitAsync su {luId}: missione non presente sul dispositivo.");
        var operationId = Guid.NewGuid();
        var request = new ReleaseLoadUnitRequest(
            luId,
            value == StepTypes.Manual,
            Navigator.UserId,
            operationId);

        // La decisione è durevole prima di attraversare il confine. Se lo scarico viene richiamato
        // mentre la riga è viva, il vincolo missione/tipo restituisce la stessa operazione.
        await WarehouseTransitionOutbox.EnqueueAsync(
            operationId,
            WarehouseTransitionKind.Release,
            mission.MissionId,
            luId,
            JsonSerializer.Serialize(request));
        await Navigator.WarehouseTransitionDispatcher.TryDispatchAsync();
    }

    /// <summary>
    /// Gestisce la logica quando il dispositivo corrente non è la destinazione della LU
    /// </summary>
    /// <param name="activateExecuteStep">Indica se attivare l'esecuzione dello step</param>
    private (bool isToExecuteStep, bool isToCheckBuildRoute) ProcessNonDestinationLogic(bool activateExecuteStep)
    {
        bool isToExecuteStep = false, isToCheckBuildRoute = false;

        if (device.LoadedLu?.StepToDo == null)
        {
            isToCheckBuildRoute = ShouldBuildRoute() ||
                                (device.LoadedLu?.DestinationItem != null && device.LoadedLu.DestinationItem != device);
        }
        else if (device.LoadedLu.StepToDo != null &&
                 device.LookWhereAllocateLu == LookWhereAllocateLuTypes.BuildRouteToStoreForcing)
        {
            isToCheckBuildRoute = true;
            isToExecuteStep = false;
        }
        else if (device.AutoExecuteStep && activateExecuteStep)
        {
            isToExecuteStep = true;
        }

        return (isToExecuteStep, isToCheckBuildRoute);
    }

    /// <summary>
    /// Gestisce il processo di ricezione di una LU
    /// </summary>
    /// <returns>True se il processo è stato avviato con successo, false altrimenti</returns>
    private async Task<bool> ProcessReceivingLu()
    {
        var readyDevices = Collections.PathDevices.Instance
            .Where(x => x?.LoadedLu == device.LUtoReceive && x is { IsReadyToTransferLu: true })
            .ToList();

        foreach (var item in readyDevices)
        {
            // Task.Delay e non Thread.Sleep: qui gira un thread del pool, condiviso da tutti i
            // dispositivi. Fermarlo per 200 ms ferma il lavoro di chi non c'entra, e con molti
            // trasferimenti insieme il pool resta senza thread liberi.
            await Task.Delay(200);

            try
            {
                if (item == null) continue;
                var automationDevice = Collections.PathDevices.Instance[item.ItemGuid];
                if (automationDevice == null) continue;
                device.StepExecutor?.StartReceivingLu(automationDevice);
                return true;
            }
            catch (Exception)
            {
                // ignored
            }
        }

        return false;
    }

    /// <summary>
    /// Gestisce il processo di rifiuto basato sul tipo di dispositivo
    /// </summary>
    private async Task ProcessRejectionByDeviceType()
    {
        switch (device.DeviceType)
        {
            case DeviceTypes.GroupOfAutomationDevices:
                await device.RebuildGroupRejection();
                break;
            default:
                if (device.BelongedGroup == null)
                    await RebuildRejection();
                else
                    await device.BelongedGroup.RebuildGroupRejection();
                break;
        }
    }

    /// <summary>
    /// Gestisce la logica di gruppo per l'elemento sorgente
    /// </summary>
    /// <param name="sourceItem">Il dispositivo sorgente</param>
    /// <param name="luToLoad">La LU da caricare</param>
    private void ProcessSourceGroupLogic(AutomationDevice sourceItem, PathMission luToLoad)
    {
        if (sourceItem.BelongedGroup is { GroupedMoveLuTo: false })
        {
            if (!sourceItem.BelongedGroup.IsReadyToExecuteStep) return;
            sourceItem.BelongedGroup.Status = DeviceStatusTypes.Ready;
            sourceItem.BelongedGroup.GroupExecuteStepRunning = false;
        }
        else if (luToLoad.IsGroupedMovementCompleted)
        {
            luToLoad.ResetGroupedMovementId();
        }
    }

    /// <summary>
    /// Gestisce lo scaricamento standard di una LU
    /// </summary>
    /// <param name="value">Tipo di step</param>
    private async Task ProcessStandardUnloading(StepTypes value)
    {
        if (device.LUsLoaded.Count == 0 || device.LoadedLu == null)
            return;

        if (device.LoadedLu is { LuId: > 0 })
        {
            var luId = device.LoadedLu.LuId;
            await ProcessLuBusinessLogic(luId, value);
            // La pulizia è responsabilità del dispatcher: o è già avvenuta, oppure missione e
            // dispositivo devono restare sospesi per il retry della stessa OperationId.
            return;
        }

        ProcessStepCompletion();
        LogLuRemoval(device.LoadedLu, "UnloadLU");

        // L'esito non si guarda, e qui nessuno riprova la rimozione: non c'e' un outbox come per l'UDC del
        // gestionale (il rilascio qui sopra). Si rifiuta per uno step in volo, che qui e' appena concluso
        // (ProcessStepCompletion), o, su un dispositivo di un gruppo, per le copie della porta di una
        // missione che le puo' avere (MissionGateway.TheCopiesLeaveTheDoorOfAMissionThatLeaves,
        // MayHaveCopies), cioe' con una cella prenotata per lei. Un'UDC senza l'identificativo del
        // gestionale non ne ha mai una: il motore non prenota senza (MissionGateway.ReserveACellAndCommitAsync,
        // PathMission.SearchTheStoreAsync), e la porta di un magazzino gliela da' solo la ricerca per tipo
        // del gruppo, che ne fa una copia. Resta una copia anche quando l'operatore le cambia solo il
        // gateway e lei ne prende il segno (MissionGateway.IsACopy, risposta del 9/10 alla seconda domanda
        // del compito 17). Prima, con il segno, non lo era piu': la rimozione la fermava con le copie della
        // sua porta, si rifiutava se lei o una copia non si fermavano, e il dispositivo tornava Ready con
        // la missione ancora a bordo, per sempre.
        await Collections.ActiveMissions.Instance.Remove(device.LoadedLu);

        device.Status = DeviceStatusTypes.Ready;

        if (device.AutoExecuteStep)
            _ = device.ExecuteStep();
    }

    /// <summary>
    /// Gestisce il completamento di uno step
    /// </summary>
    private void ProcessStepCompletion()
    {
        if (device.RunningStepId == Guid.Empty) return;
        if (device.LoadedLu?.StepToDo?.DestinationItem != null)
            device.LoadedLu.StepToDo.DestinationItem.Status = DeviceStatusTypes.Ready;
        device.RunningStepId = Guid.Empty;
    }

    /// <summary>
    /// Gestisce la logica per dispositivi di tipo Storage
    /// </summary>
    private async Task ProcessStorageDevice()
    {
        if (device.LoadedLu is not { } mission) return;

        // Il setter della posizione prepara e consegna l'Allocate in sfondo. La missione non può
        // essere rimossa mentre quel lavoro sta ancora decidendo se lasciare una riga pendente.
        await mission.PositionWork;
        if (!ReferenceEquals(device.LoadedLu, mission)) return;
        if (await WarehouseTransitionOutbox.HasPendingAllocateAsync(mission.MissionId)) return;

        if (mission.LuId > 0 && mission.DestinationItemId == device.ItemGuid &&
            !string.IsNullOrEmpty(mission.StorageDoorCode))
        {
            // Se la riga non c'è ma il deposito terminale la richiedeva, è fallita la persistenza
            // (o la preparazione della richiesta): rimuovere la missione perderebbe l'unica prova
            // del bancale fisicamente depositato.
            device.Status = DeviceStatusTypes.Warning;
            Navigator.WriteLog(
                $"Missione {mission.MissionId}, UDC {mission.LuId}: deposito fisico senza una transizione Allocate durevole; missione conservata.",
                LogEntryType.Warning);
            return;
        }

        // Per i casi non terminali conserva il comportamento storico di pulizia della missione
        // non più utilizzata.
        CleanupLuReferences(mission);
        LogLuRemoval(mission, "LoadLU");
        await Collections.ActiveMissions.Instance.RemoveUnused(mission);
    }

    /// <summary>
    /// Gestisce lo scaricamento basato sul tipo di dispositivo
    /// </summary>
    /// <param name="value">Tipo di step</param>
    private async Task ProcessUnloadingByDeviceType(StepTypes value)
    {
        switch (device.DeviceType)
        {
            case DeviceTypes.GroupOfAutomationDevices:
                await ProcessGroupUnloading(value);
                break;

            default:
                await ProcessStandardUnloading(value);
                break;
        }
    }

    /// <summary>
    /// Legge le informazioni della LU dal dispositivo ASI
    /// </summary>
    private void ReadLuInfo()
    {
        try
        {
            if (device.LoadedLu == null) return;
            device.DeviceConnection?.RequestLuDataFromAsi(Navigator.GetAskingRunAsi(device));
        }
        catch (Exception ex)
        {
            device.LogError(ex);
        }
    }

    /// <summary>
    /// Ricostruisce il percorso di rifiuto per la LU corrente
    /// </summary>
    private async Task RebuildRejection()
    {
        if (device.LUsLoaded.Count == 0 || device.LoadedLu == null) return;

        device.LoadedLu.Gateway = Guid.Empty;
        var resultTest = await device.LoadedLu.RejectTo(RejectionTypes.DefaultNavigatorRejectionItem);

        if (!resultTest)
            return;

        device.LoadedLu.StatusDescription = "User requested to reject LU";
        device.Status = DeviceStatusTypes.Ready;
        device.RunningStepId = Guid.Empty;

        if (device.AutoExecuteStep)
            _ = device.ExecuteStep();
    }

    /// <summary>
    /// Configura la distribuzione sui buffer per la LU caricata
    /// </summary>
    /// <remarks>
    /// Non tocca una destinazione scelta dall'operatore (<see cref="PathMission.OperatorDestination"/>):
    /// la distribuzione e' configurazione del dispositivo, e la scelta dell'operatore vince sulla
    /// configurazione (decisione del proprietario del 30/9). Arrivata sulla destinazione scelta la
    /// missione il segno non l'ha piu', e da li' la distribuzione vale come per tutte. Il segno si
    /// guarda sotto il lucchetto della scelta, insieme alla scrittura
    /// (<see cref="PathMission.UnlessChosenByOperator"/>): l'arrivo non gira sotto il semaforo del
    /// dispositivo, e l'operatore puo' scegliere mentre gira. Per una missione dell'operatore anche
    /// il giro dei polmoni resta fermo.
    /// </remarks>
    private async Task SetDistributeOnBuffers()
    {
        if (device.LoadedLu is not { } lu || !device.DistributeOnBuffers ||
            device.ReachableBuffers.Count <= 0 ||
            (lu.MissionType != MissionTypes.Extraction &&
             lu.MissionType != MissionTypes.Rejection))
            return;

        var buffers = device.ReachableBuffers;
        AutomationDevice? buffer = null;

        if (!lu.UnlessChosenByOperator(() =>
            {
                UpdateBufferDistributionIndex(buffers);
                buffer = buffers[_indexLastBufferDestination];

                lu.RemoveMissionSteps();
                lu.DestinationItemId = buffer.ItemGuid;
            }))
            return;

        var doorCode = await DbInfo.GetDoorCode(buffer!.ItemGuid);

        if (!lu.UnlessChosenByOperator(() => lu.StorageDoorCode = doorCode))
            return;

        await lu.BuildPathToDestination();

        _indexLastBufferDestination++;
    }

    /// <summary>
    /// Determina se è necessario costruire un percorso per la LU
    /// </summary>
    /// <returns>True se è necessario costruire un percorso, false altrimenti</returns>
    private bool ShouldBuildRoute()
    {
        return device.DestinationItem != null ||
               device.DestinationItem2 != null ||
               device.LookWhereAllocateLu != LookWhereAllocateLuTypes.No;
    }

    /// <summary>
    /// Determina se è necessario leggere le informazioni della LU
    /// </summary>
    /// <param name="previousPositionItemId">ID della posizione precedente</param>
    /// <returns>True se è necessario leggere le informazioni, false altrimenti</returns>
    private bool ShouldReadLuInfo(Guid previousPositionItemId)
    {
        return device.LoadedLu != null &&
               (device.TriggerIdGetLuInfo == Guid.Empty ||
                previousPositionItemId == device.TriggerIdGetLuInfo ||
                device.LoadedLu.SourceItemId == device.TriggerIdGetLuInfo) &&
               device.LoadedLu.LuId == 0 &&
               device.GetLuInfo;
    }

    /// <summary>
    /// Mostra un messaggio informativo sul dispositivo ASI
    /// </summary>
    private void ShowItemMessage()
    {
        if (!device.ShowInfoMessageAssignedLu || device.LoadedLu == null)
            return;

        var messageToShow = string.Empty; //aggiungere qui il messaggio da visualizzare

        try
        {
            device.DeviceConnection?.ShowMessageToAsi(Navigator.GetMovementRunAsi(device), messageToShow);
        }
        catch (Exception ex)
        {
            device.LogError(ex);
        }
    }

    /// <summary>
    /// Aggiorna l'indice di distribuzione dei buffer
    /// </summary>
    /// <param name="buffers">Lista dei buffer disponibili</param>
    private void UpdateBufferDistributionIndex(List<AutomationDevice> buffers)
    {
        if (device.LoadedLu is { NoteProcessingId: > 0 } &&
            _lastIdLavorazioneBufferDestination != device.LoadedLu.NoteProcessingId)
        {
            _indexLastBufferDestination = 0;
            _lastIdLavorazioneBufferDestination = device.LoadedLu.NoteProcessingId;
        }
        else if (_indexLastBufferDestination > buffers.Count - 1)
        {
            _indexLastBufferDestination = 0;
        }
    }

    /// <summary>
    /// Valida che tutte le missioni nella lista possano essere prelevate
    /// </summary>
    /// <param name="missions">Lista delle missioni da validare</param>
    /// <returns>True se tutte le missioni possono essere prelevate, false altrimenti</returns>
    private async Task<bool> ValidateAllMissionsPickupable(List<PathMission?> missions)
    {
        foreach (var lu in missions)
        {
            if (lu != null && !await VerifyPickUp(lu))
            {
                // La segnalazione viene PRIMA della mutazione locale, come gia' fanno
                // CleanupFailedPickup e AutomationDeviceStorageManager.ProcessExistingMission. Con
                // RemoveLuToPickUp per primo, un guasto del confine lasciava la missione fuori dalla
                // coda di prelievo del dispositivo ma viva in ActiveMissions: al giro dopo non era
                // piu' nell'elenco che si valida, e nessun altro punto tornava a segnalare quel
                // fatto. Cosi' invece l'eccezione risale con la coda intatta, e il giro dopo ripassa
                // di qui. Percorso nuovo del compito 12: prima la scrittura non poteva sollevare.
                await DbInfo.ReportExtractionRowOutcome(lu.ExtractionQueueId, ExtractionRowStatus.NotReachable);
                device.RemoveLuToPickUp(lu);
                LogLuRemoval(lu, "CanPickUpLUs");
                await Collections.ActiveMissions.Instance.RemoveUnused(lu);
                return false;
            }

            var canPickupFromBelongedItems = device.BelongedItems
                .Where(x => x != null && lu != null && x.StorageOffset == lu.StorageDepth)
                .Any(item => item is { LuManager: not null } && item.LuManager.CanPickUpLu(lu));

            if (!canPickupFromBelongedItems)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Valida e pulisce i riferimenti alla LU da ricevere
    /// </summary>
    private void ValidateAndCleanupLuToReceive()
    {
        if (device.LUtoReceive == null) return;
        var hasActiveTransfer = Collections.PathDevices.Instance
            .Any(x => x?.LoadedLu == device.LUtoReceive);

        var hasMissionStep = Collections.MissionSteps.Instance
            .Any(x => x.ToNodeId == device.ItemGuid && x.MissionId == device.LUtoReceive.MissionId);

        var isSourceLinked = device.LUtoReceive.PositionItem != null &&
                             device.SourceItemsLinked.Contains(device.LUtoReceive.PositionItem);

        if (!hasActiveTransfer || !hasMissionStep || !isSourceLinked)
        {
            device.LUtoReceive = null;
        }
    }

    /// <summary>
    /// Valida i conflitti di percorso per una LU caricata
    /// </summary>
    /// <returns>Il dispositivo che causa il conflitto, null se non ci sono conflitti</returns>
    private AutomationDevice? ValidateLoadedLuCrossroad()
    {
        var stepToAnalyze = device.LoadedLu?.StepToDo;
        return stepToAnalyze is { DestinationItem: not null } && stepToAnalyze.DestinationItem == device
            ? device
            : null;
    }

    /// <summary>
    /// Valida le condizioni per il prelievo di una LU
    /// </summary>
    /// <param name="luToCheck">La LU da verificare</param>
    /// <param name="stepToDo">Lo step da eseguire</param>
    /// <returns>Tupla che indica se è valido e il motivo in caso negativo</returns>
    private (bool isValid, string reason) ValidatePickupConditions(PathMission? luToCheck, MissionStep? stepToDo)
    {
        if (device.LUsLoaded is { Count: > 0 })
            return (false, "The destination item has an LU loaded");

        if (device.LUtoPick != null && device.LUtoPick != luToCheck)
            return (false, "The destination item is Picking Up an LU");

        if (device.LUtoReceive != null || device.IsReceivingLu)
            return (false, "The destination item is waiting for an LU");

        if (stepToDo != null && device.RunningStepId == stepToDo.StepId)
            return (true, string.Empty);

        if (device.RunningStepId != Guid.Empty)
            return (false, "The destination item status is busy");

        if (device.Status == DeviceStatusTypes.Recovering)
            return (true, string.Empty);

        if (device.Status != DeviceStatusTypes.Ready)
            return (false, "The destination item status is not ready");

        if (CheckCrossroad(luToCheck) != null)
            return (false, "The way to go is busy");

        return (true, string.Empty);
    }

    /// <summary>
    /// Valida il rispetto della coda per una LU specificata
    /// </summary>
    /// <param name="luToCheck">La LU da verificare</param>
    /// <returns>True se la coda è rispettata, false altrimenti</returns>
    private bool ValidateQueueRespect(PathMission luToCheck)
    {
        var stepTodo = device.MissionSteps.FirstOrDefault();
        return stepTodo?.MissionId == luToCheck.MissionId;
    }

    /// <summary>
    /// Valida le profondità di stoccaggio per una lista di missioni
    /// </summary>
    /// <param name="missions">Lista delle missioni da validare</param>
    /// <returns>True se tutte le profondità sono valide, false altrimenti</returns>
    private bool ValidateStorageDepths(List<PathMission?> missions)
    {
        var wrongLu = missions.FirstOrDefault(x => x is { StorageDepth: 0 });
        if (wrongLu == null)
            return true;

        wrongLu.Status = MissionStatusTypes.Error;
        wrongLu.StatusDescription = $"LU {wrongLu.LuCode} has wrong storage depth, please check it and contact service support";
        Navigator.WriteAlert(wrongLu);
        return false;
    }

    /// <summary>
    /// Valida i conflitti di percorso per collegamenti di stoccaggio
    /// </summary>
    /// <returns>Il dispositivo che causa il conflitto, null se non ci sono conflitti</returns>
    private AutomationDevice? ValidateStorageLinkCrossroad()
    {
        var newMission = device.LUtoPick ?? device.QueuedLUtoPickUp.FirstOrDefault();
        // Sonda voluta: la missione puo' venire dalla coda di prelievo, senza una rotta pianificata
        // (LUtoPick invece e' gia' stata presa in carico e puo' averla), quindi si calcola
        var stepToAnalyze = newMission?.GetMissionStepsToDestination()
            .FirstOrDefault(x => x.FromNodeId == device.ItemGuid);

        return stepToAnalyze is { DestinationItem: not null } && stepToAnalyze.DestinationItem == device
            ? device
            : null;
    }

    #endregion Private Methods

}
