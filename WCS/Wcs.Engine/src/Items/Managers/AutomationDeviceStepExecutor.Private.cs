using Wcs.Engine.Collections;
using Wcs.Engine.ORM;
using Log.Interface;
using Wcs.Connection.Interface;
using Wcs.Connection.Interface.CommandData;
using Wcs.Wms.Contracts;
using static Wcs.Common.Enums;
using static Wcs.Engine.Items.AutomationDevice;

namespace Wcs.Engine.Items.Managers;

public partial class AutomationDeviceStepExecutor
{

    #region Private Methods

    /// <summary>
    /// Applica uno stato specifico a una lista di dispositivi di automazione
    /// </summary>
    /// <param name="items">Lista dei dispositivi a cui applicare lo stato</param>
    /// <param name="status">Stato da applicare ai dispositivi</param>
    private static void ApplyStatusToItems(List<AutomationDevice> items, DeviceStatusTypes status)
    {
        foreach (var item in items.Where(x => x.DeviceType != DeviceTypes.Storage))
            item.Status = status;
    }

    /// <summary>
    /// Verifica se uno step di missione contiene tutti i dati necessari per l'esecuzione
    /// </summary>
    /// <param name="step">Lo step da validare</param>
    /// <returns>True se lo step è valido, false altrimenti</returns>
    private static bool IsStepValid(MissionStep step)
    {
        return step is { SourceItem: not null, DestinationItem: not null, PathMission: not null };
    }

    /// <summary>
    /// Verifica se uno step GetFrom contiene tutti i dati necessari per l'esecuzione
    /// </summary>
    /// <param name="stepToDo">Lo step da validare</param>
    /// <returns>True se lo step è valido per GetFrom, false altrimenti</returns>
    private static bool IsValidGetFromStep(MissionStep? stepToDo)
    {
        return stepToDo is { PathMission: not null, SourceItem: not null, DestinationItem: not null };
    }

    /// <summary>
    /// Verifica se una lista di LU è omogenea per porta di stoccaggio (tutte nella stessa area di magazzino)
    /// </summary>
    /// <param name="value">Lista delle missioni di percorso da verificare</param>
    /// <returns>True se tutte le LU appartengono alla stessa porta di stoccaggio, false altrimenti</returns>
    private static bool LUsIsStorageHomogeneous(List<PathMission?>? value)
    {
        if (value == null) return false;

        var storageDoor = string.Empty;
        foreach (var lu in value)
        {
            if (string.IsNullOrEmpty(storageDoor) && lu?.StorageDoorCode != null)
                storageDoor = lu.StorageDoorCode;
            else if (storageDoor != lu?.StorageDoorCode)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Ordina una lista di step di missione in base al numero d'ordine del gruppo
    /// </summary>
    /// <param name="value">Lista degli step da ordinare</param>
    private static void SortValueByOrderNumber(List<MissionStep> value)
    {
        value.Sort((x, y) =>
        {
            if (x.PathMission?.PositionItem?.OrderNumberGroup == null ||
                y.PathMission?.PositionItem?.OrderNumberGroup == null)
                return 0;
            return x.PathMission.PositionItem.OrderNumberGroup.CompareTo(y.PathMission.PositionItem.OrderNumberGroup);
        });
    }

    /// <summary>
    /// Applica gli stati ai dispositivi coinvolti nel movimento raggruppato
    /// </summary>
    /// <param name="collections">Tupla contenente le collezioni di dispositivi organizzate per stato</param>
    private void ApplyItemStatuses((List<AutomationDevice> Busy, List<AutomationDevice> Moving, List<AutomationDevice> Loading, List<AutomationDevice> Unloading) collections)
    {
        ApplyStatusToItems(collections.Busy, DeviceStatusTypes.Busy);
        ApplyStatusToItems(collections.Loading, DeviceStatusTypes.Loading);
        ApplyStatusToItems(collections.Unloading, DeviceStatusTypes.Unloading);
        ApplyStatusToItems(collections.Moving, DeviceStatusTypes.Moving);
        device.Status = DeviceStatusTypes.Moving;
    }

    /// <summary>
    /// Verifica se una LU può essere processata dal dispositivo corrente
    /// </summary>
    /// <param name="lu">La missione di percorso da verificare</param>
    /// <returns>True se la LU può essere processata, false altrimenti</returns>
    private bool CanProcessLu(PathMission lu)
    {
        if (device.LuManager?.CanAcceptLu(lu) == true)
            return true;

        if (device.IsLinkedToStorage)
            return false;

        var itemCheckCrossRoad = device.LuManager?.CheckCrossroad(lu);

        if (itemCheckCrossRoad == null)
        {
            if (device.AcceptLuIfDestinationIsAvailable &&
                device.LuManager?.StepIsAvailable(lu) == StepAvailableStatusTypes.DestinationNotAvailable)
                AddDevicesReadyToTransferLu(device.StepToDoSource(lu)?.DestinationItem);
        }
        else
        {
            AddDevicesReadyToTransferLu(itemCheckCrossRoad);
        }

        return false;
    }

    /// <summary>
    /// Verifica lo stato delle celle di stoccaggio per gli step da eseguire
    /// </summary>
    /// <param name="value">Lista degli step da verificare</param>
    private async Task CheckCellStoreStatus(List<MissionStep> value)
    {
        if (value.Count == 0) return;

        var lastCheckedIdCell = default(decimal);

        foreach (var step in value.Where(x =>
                     x.DestinationItem?.DeviceType == DeviceTypes.Storage &&
                     x.PathMission?.MissionType != MissionTypes.DefragmentCell))
        {
            if (step.PathMission?.StorageDoorCode == null) continue;

            var idCell = await DbInfo.GetCellId(step.PathMission.StorageDoorCode);
            if (idCell == 0 || lastCheckedIdCell == idCell) continue;

            lastCheckedIdCell = idCell;

            if (!await ShouldVerifyAllocation(idCell)) continue;
            if (device.LookWhereAllocateLu != LookWhereAllocateLuTypes.No && device.LUsLoaded.Count > 0)
                await device.VerifyWhereAllocateLuGrouped(device.LUsLoaded);
            return;
        }
    }

    /// <summary>
    /// Configura la destinazione di automazione per un comando di movimento LU
    /// </summary>
    /// <param name="step">Lo step di missione da configurare</param>
    /// <param name="lu">Il comando di movimento da configurare</param>
    /// <param name="collections">Collezioni per organizzare i dispositivi per stato</param>
    private void ConfigureAutomationDestination(MissionStep step, MovementLuDataCommand lu,
        (List<AutomationDevice> Busy, List<AutomationDevice> Moving, List<AutomationDevice> Loading, List<AutomationDevice> Unloading) collections)
    {
        if (step.SourceItem == null || step.DestinationItem == null) return;

        switch (step.SourceItem.DeviceType)
        {
            case DeviceTypes.Storage:
                if (!collections.Loading.Contains(step.DestinationItem))
                    collections.Loading.Add(step.DestinationItem);
                break;
            default:
                if (!collections.Busy.Contains(step.DestinationItem))
                    collections.Busy.Add(step.DestinationItem);
                break;
        }

        lu.DestAsi = step.DestinationItem.ASI;
        lu.DestSubitem = step.DestinationItem.SUBITEM;
        lu.DestPartition = step.DestinationItem.PARTITION;
    }

    /// <summary>
    /// Configura la sorgente di automazione per un comando di movimento LU
    /// </summary>
    /// <param name="step">Lo step di missione da configurare</param>
    /// <param name="lu">Il comando di movimento da configurare</param>
    /// <param name="collections">Collezioni per organizzare i dispositivi per stato</param>
    private void ConfigureAutomationSource(MissionStep step, MovementLuDataCommand lu,
        (List<AutomationDevice> Busy, List<AutomationDevice> Moving, List<AutomationDevice> Loading, List<AutomationDevice> Unloading) collections)
    {
        if (step.DestinationItem == null || step.SourceItem == null) return;

        switch (step.DestinationItem.DeviceType)
        {
            case DeviceTypes.Storage:
                if (!collections.Unloading.Contains(step.SourceItem))
                    collections.Unloading.Add(step.SourceItem);
                break;
            default:
                if (!collections.Moving.Contains(step.SourceItem))
                    collections.Moving.Add(step.SourceItem);
                break;
        }

        lu.SourceAsi = step.SourceItem.ASI;
        lu.SourceSubitem = step.SourceItem.SUBITEM;
        lu.SourcePartition = step.SourceItem.PARTITION;
    }

    /// <summary>
    /// Configura la destinazione per un comando di movimento LU
    /// </summary>
    /// <param name="step">Lo step di missione da configurare</param>
    /// <param name="lu">Il comando di movimento da configurare</param>
    /// <param name="collections">Collezioni per organizzare i dispositivi per stato</param>
    private async Task ConfigureDestination(MissionStep step, MovementLuDataCommand lu,
        (List<AutomationDevice> Busy, List<AutomationDevice> Moving, List<AutomationDevice> Loading, List<AutomationDevice> Unloading) collections)
    {
        if (step.DestinationItem == null) return;
        switch (step.DestinationItem.DeviceType)
        {
            case DeviceTypes.Storage:
                 await ConfigureStorageDestination(step, lu);
                break;
            default:
                ConfigureAutomationDestination(step, lu, collections);
                break;
        }
    }

    /// <summary>
    /// Configura uno step GetFrom impostando gli stati appropriati per i dispositivi coinvolti
    /// </summary>
    /// <param name="stepToDo">Lo step da configurare</param>
    /// <param name="sourceItem">La sorgente gia' risolta dal chiamante</param>
    /// <param name="destinationItem">La destinazione gia' risolta dal chiamante</param>
    /// <returns>False se la destinazione e' gia' impegnata per un altro step: il movimento non parte.</returns>
    /// <remarks>
    /// I due dispositivi arrivano risolti da <see cref="StartGetFromMOVE_LU"/> e non si rileggono
    /// piu' da <c>stepToDo</c>: dalla presa del posto in poi si lavora sugli stessi oggetti fino
    /// all'invio del comando.
    /// </remarks>
    private bool ConfigureGetFromStep(MissionStep stepToDo, AutomationDevice sourceItem,
        AutomationDevice destinationItem)
    {
        if (stepToDo.PathMission == null) return false;

        // Il posto sulla destinazione si prende prima di toccarne lo stato. Voce D73.
        if (destinationItem.DeviceType != DeviceTypes.Storage &&
            !destinationItem.TryReserveForIncomingStep(stepToDo.StepId))
        {
            Navigator.WriteLog(
                $"ConfigureGetFromStep {device.ItemGuid}: la destinazione {destinationItem.ItemGuid} e' gia' impegnata per lo step {destinationItem.WaitingStepId}",
                LogEntryType.Information);
            return false;
        }

        destinationItem.Status = DeviceStatusTypes.Busy;

        // Il gateway della missione si scrive dopo il gettone (ExecuteGetFromMovement), come nel
        // movimento singolo: un avvio che rinuncia non lascia alla ricostruzione della rotta il
        // vincolo di passare dalla destinazione di uno step che non c'e' piu' (voce 41, compito 6). Lo
        // stesso per l'uscita dalla lista di chi aspetta la destinazione, lo stato della missione e il
        // suo movimento di gruppo (risposta 4 del proprietario dell'11/10).
        sourceItem.Status = DeviceStatusTypes.Moving;

        return true;
    }

    /// <summary>
    /// Configura uno step di prelievo impostando gli identificatori e i riferimenti appropriati
    /// </summary>
    /// <param name="mission">La missione del prelievo, con l'avvio contato</param>
    /// <param name="stepToDo">Lo step da configurare</param>
    /// <param name="seenRouteVersion">La versione della rotta letta prima dello step</param>
    /// <returns>
    /// False se la destinazione e' gia' impegnata per un altro step, o se la rotta e' cambiata dopo la
    /// lettura dello step: il prelievo non parte.
    /// </returns>
    /// <remarks>
    /// Il gettone e lo stato di lavorazione si scrivono <b>insieme</b>: <c>Loading</c> stava piu'
    /// sotto, in <c>ExecutePickUpMovement</c>, e fra le due scritture restava un istante in cui il
    /// dispositivo era <c>Ready</c> con un identificativo di passo addosso — cioe' proprio la terna
    /// del guasto del 2026-09-09, quella che il motore riconosce come gettone appeso. Tenerle
    /// adiacenti fa si' che quella terna non esista mai su un prelievo che sta partendo.
    /// <para>
    /// Si scrivono tutti e due sotto il lucchetto della rotta, solo se la rotta e' ancora quella su
    /// cui lo step si e' letto (<c>PathMission.TryTakeTheStepToken</c>): fra il posto e il gettone una
    /// ricostruzione poteva togliere lo step, e il prelievo partiva lo stesso per uno step che non
    /// c'era piu' (corsa 5 della ricognizione della voce 41, compito 6). Se la rotta e' cambiata il
    /// posto si restituisce e il prelievo non parte.
    /// </para>
    /// </remarks>
    private bool ConfigurePickUpStep(PathMission mission, MissionStep stepToDo, long seenRouteVersion)
    {
        // Il posto sulla destinazione si prende, non si scrive. Voce D73.
        if (stepToDo.DestinationItem is { DeviceType: not DeviceTypes.Storage } destinazione &&
            !destinazione.TryReserveForIncomingStep(stepToDo.StepId))
        {
            Navigator.WriteLog(
                $"ConfigurePickUpStep {device.ItemGuid}: la destinazione {destinazione.ItemGuid} e' gia' impegnata per lo step {destinazione.WaitingStepId}",
                LogEntryType.Information);
            return false;
        }

        device.StepStartPauseForTesting?.Invoke();

        if (!mission.TryTakeTheStepToken(seenRouteVersion, stepToDo, () =>
            {
                device.RunningStepId = stepToDo.StepId;
                device.Status = DeviceStatusTypes.Loading;
            }))
        {
            stepToDo.DestinationItem?.ReleaseIncomingStep(stepToDo.StepId);

            // Come il movimento singolo che rinuncia, chi preleva riprova con la rotta nuova appena e'
            // libero (AutomationDevice.ExecuteAgainWhenFree, risposta 4 del proprietario dell'11/10).
            device.ExecuteAgainWhenFree();
            return false;
        }

        if (stepToDo.PathMission != null) stepToDo.PathMission.GatewayItem = device;

        return true;
    }

    /// <summary>
    /// Configura la sorgente per un comando di movimento LU
    /// </summary>
    /// <param name="step">Lo step di missione da configurare</param>
    /// <param name="lu">Il comando di movimento da configurare</param>
    /// <param name="collections">Collezioni per organizzare i dispositivi per stato</param>
    private async Task ConfigureSource(MissionStep step, MovementLuDataCommand lu,
        (List<AutomationDevice> Busy, List<AutomationDevice> Moving, List<AutomationDevice> Loading, List<AutomationDevice> Unloading) collections)
    {
        if (step.SourceItem == null) return;

        switch (step.SourceItem.DeviceType)
        {
            case DeviceTypes.Storage:
                await ConfigureStorageSource(step, lu);
                break;
            default:
                ConfigureAutomationSource(step, lu, collections);
                break;
        }
    }

    /// <summary>
    /// Configura sia la sorgente che la destinazione per un comando di movimento LU
    /// </summary>
    /// <param name="step">Lo step di missione da configurare</param>
    /// <param name="lu">Il comando di movimento da configurare</param>
    /// <param name="collections">Collezioni per organizzare i dispositivi per stato</param>
    private async Task ConfigureSourceDestination(MissionStep step, MovementLuDataCommand lu,
        (List<AutomationDevice> Busy, List<AutomationDevice> Moving, List<AutomationDevice> Loading, List<AutomationDevice> Unloading) collections)
    {
        await ConfigureSource(step, lu, collections);
        await ConfigureDestination(step, lu, collections);
    }

    /// <summary>
    /// Configura uno step di missione con i parametri di esecuzione
    /// </summary>
    /// <param name="step">Lo step da configurare</param>
    /// <param name="groupedMovementId">Il gettone del movimento di gruppo, passato dal chiamante e
    /// non riletto da <c>RunningStepId</c>: il getter lo restituiva vuoto finche' nessuno step lo
    /// portava, cioe' proprio qui (voce D166)</param>
    /// <param name="versions">La versione della rotta di ogni missione contata, letta prima dello step</param>
    /// <returns>False se la rotta della missione e' cambiata dopo la scelta dello step: lo step non parte.</returns>
    /// <remarks>
    /// Il gettone del gruppo sullo step e sulla missione (<c>GroupedMovementId</c>) si scrive sotto il
    /// lucchetto della rotta, solo se la versione e' ancora quella letta prima dello step
    /// (<c>PathMission.TryTakeTheStepToken</c>). Fra il posto preso (<c>ReserveDestinations</c>) e
    /// questo gettone ci sono le domande al gestionale, e una ricostruzione poteva togliere lo step:
    /// il gruppo lo rimetteva in <c>MissionSteps</c>, accanto alla rotta nuova, e il comando partiva per
    /// uno step scartato (corsa 5 della ricognizione della voce 41, compito 6). Lo step resta ora fuori,
    /// e non torna in <c>MissionSteps</c>: il confronto lo vuole ancora li'.
    /// </remarks>
    private bool ConfigureStep(MissionStep step, Guid groupedMovementId, Dictionary<PathMission, long> versions)
    {
        // La missione si rilegge dalle attive a ogni accesso: se non e' piu' quella contata all'inizio
        // del giro (tolta, o tolta e rimessa), lo step non parte.
        if (step.PathMission is not { } mission || !versions.TryGetValue(mission, out var seenRouteVersion))
            return false;

        device.StepStartPauseForTesting?.Invoke();

        if (!mission.TryTakeTheStepToken(seenRouteVersion, step, () =>
            {
                step.StartStep = DateTime.Now;
                step.RunningItem = device;
                step.GroupedMovementId = groupedMovementId;
                mission.GroupedMovementId = groupedMovementId;

                var missionToSet = MissionSteps.Find(x => x.StepId == step.StepId);
                if (missionToSet != null)
                    missionToSet.GroupedMovementId = step.GroupedMovementId;
            }, out var versionAfter))
        {
            // Come il movimento singolo che rinuncia, il gruppo riprova con la rotta nuova appena e' libero
            // (AutomationDevice.ExecuteAgainWhenFree, risposta 4 del proprietario dell'11/10).
            device.ExecuteAgainWhenFree();
            return false;
        }

        // Un secondo step della stessa missione nello stesso giro confronta la versione dopo questo gettone.
        versions[mission] = versionAfter;
        return true;
    }

    /// <summary>
    /// Configura i parametri ASI per celle di stoccaggio
    /// </summary>
    /// <param name="step">Lo step di missione</param>
    /// <param name="lu">Il comando di movimento</param>
    /// <param name="cellAsi">I dati ASI della cella</param>
    /// <param name="isSource">True se la cella è sorgente, false se è destinazione</param>
    private static async Task ConfigureStorageAsi(MissionStep step, MovementLuDataCommand lu, DataAsi? cellAsi, bool isSource)
    {

        if (step.DestinationItem == null || step.SourceItem == null || step.PathMission == null) return;

        var recordStorageType = isSource ? step.DestinationItem.RecordStorageLength : step.SourceItem.RecordStorageLength;
        var storagePartition = isSource ? step.DestinationItem.StoragePartition : step.SourceItem.StoragePartition;
        var storagePartitionReversed = isSource ? step.DestinationItem.StoragePartitionReversed : step.SourceItem.StoragePartitionReversed;
        var doorCode = isSource ? step.PathMission.PickUpDoorCode : step.PathMission.StorageDoorCode;

        switch (recordStorageType)
        {
            case RecordStorageTypes.FixedLength:
            case RecordStorageTypes.ConvertDepthToPartition:
                var cellId = await DbInfo.GetCellId(doorCode);
                var isInverted = cellId != 0 && (await step.PathMission.DescribeCellAsync(cellId)).Inverted;
                var partition = isInverted ? storagePartitionReversed : storagePartition;

                if (isSource)
                {
                    lu.SourceAsi = cellAsi?.Asi;
                    lu.SourceSubitem = cellAsi?.Subitem;
                    lu.SourcePartition = partition;
                }
                else
                {
                    lu.DestAsi = cellAsi?.Asi;
                    lu.DestSubitem = cellAsi?.Subitem;
                    lu.DestPartition = partition;
                }
                break;
            default:
                if (isSource)
                {
                    lu.SourceAsi = cellAsi?.Asi;
                    lu.SourceSubitem = cellAsi?.Subitem;
                    lu.SourcePartition = cellAsi?.Partition;
                }
                else
                {
                    lu.DestAsi = cellAsi?.Asi;
                    lu.DestSubitem = cellAsi?.Subitem;
                    lu.DestPartition = cellAsi?.Partition;
                }
                break;
        }
    }

    /// <summary>
    /// Configura la destinazione di stoccaggio per un comando di movimento LU
    /// </summary>
    /// <param name="step">Lo step di missione</param>
    /// <param name="lu">Il comando di movimento</param>
    private async Task ConfigureStorageDestination(MissionStep step, MovementLuDataCommand lu)
    {
        if (string.IsNullOrEmpty(step.PathMission?.DestinationAsi) ||
            string.IsNullOrEmpty(step.PathMission.DestinationSubitem) ||
            string.IsNullOrEmpty(step.PathMission.DestinationPartition))
        {
            if (step.PathMission?.StorageDoorCode == null) return;
            var cellAsi = await step.PathMission.GetAsiCellInfoAsync(step.PathMission.StorageDoorCode);
            await ConfigureStorageAsi(step, lu, cellAsi, false);
        }
        else
        {
            lu.DestAsi = step.PathMission.DestinationAsi;
            lu.DestSubitem = step.PathMission.DestinationSubitem;
            lu.DestPartition = step.PathMission.DestinationPartition;
        }
    }

    /// <summary>
    /// Configura la sorgente di stoccaggio per un comando di movimento LU
    /// </summary>
    /// <param name="step">Lo step di missione</param>
    /// <param name="lu">Il comando di movimento</param>
    private static async Task ConfigureStorageSource(MissionStep step, MovementLuDataCommand lu)
    {
        if (string.IsNullOrEmpty(step.PathMission?.SourceAsi) ||
            string.IsNullOrEmpty(step.PathMission.SourceSubitem) ||
            string.IsNullOrEmpty(step.PathMission.SourcePartition))
        {
            if (step.PathMission?.PickUpDoorCode == null) return;
            var cellAsi = await step.PathMission.GetAsiCellInfoAsync(step.PathMission.PickUpDoorCode);
            await ConfigureStorageAsi(step, lu, cellAsi, true);
        }
        else
        {
            lu.SourceAsi = step.PathMission.SourceAsi;
            lu.SourceSubitem = step.PathMission.SourceSubitem;
            lu.SourcePartition = step.PathMission.SourcePartition;
        }
    }

    /// <summary>
    /// Crea le collezioni per organizzare i dispositivi in base al loro stato durante l'esecuzione
    /// </summary>
    /// <returns>Tupla contenente le liste vuote per ogni categoria di stato</returns>
    private (List<AutomationDevice> Busy, List<AutomationDevice> Moving, List<AutomationDevice> Loading, List<AutomationDevice> Unloading) CreateItemStatusCollections()
    {
        return (
            new List<AutomationDevice>(),
            new List<AutomationDevice>(),
            new List<AutomationDevice>(),
            new List<AutomationDevice>()
        );
    }

    /// <summary>
    /// Crea un comando di movimento LU da uno step di missione
    /// </summary>
    /// <param name="step">Lo step di missione da cui creare il comando</param>
    /// <returns>Il comando di movimento o null se lo step non è valido</returns>
    private MovementLuDataCommand? CreateMovementCommand(MissionStep step)
    {
        if (step.SourceItem == null || step.DestinationItem == null) return null;

        return new MovementLuDataCommand
        {
            Id = (int)(step.PathMission?.LuId ?? 0),
            HandlingMode = step.PathMission?.HandlingMode ?? 0
        };
    }

    /// <summary>
    /// Esegue step bilanciati basandosi sui rapporti di prelievo e deposito configurati
    /// </summary>
    private async Task ExecuteBalancedSteps()
    {
        var currentPickUp = device.CurrentPickUpStep;
        var currentStore = device.CurrentStoreStep;
        var pickUpUnit = device.PickUpUnitStep;
        var storeUnit = device.StoreUnitStep;

        if (currentPickUp >= pickUpUnit && currentStore >= storeUnit)
        {
            device.CurrentPickUpStep = 0;
            device.CurrentStoreStep = 0;
            await ExecuteStepByPriority();
        }
        else if (currentPickUp >= pickUpUnit)
        {
            device.CurrentPickUpStep = 0;
            device.LastExecutedStepType = ExecutedStepTypes.Store;
            await ExecuteQueueToReceiveLu();
        }
        else if (currentPickUp < pickUpUnit)
        {
            device.LastExecutedStepType = ExecutedStepTypes.PickUp;
            await ExecuteQueueLuToPickUp();
        }
        else if (currentStore >= storeUnit)
        {
            device.CurrentStoreStep = 0;
            device.LastExecutedStepType = ExecutedStepTypes.PickUp;
            await ExecuteQueueLuToPickUp();
        }
        else if (currentStore < storeUnit)
        {
            device.LastExecutedStepType = ExecutedStepTypes.Store;
            await ExecuteQueueToReceiveLu();
        }
    }

    /// <summary>
    /// Esegue un'operazione GetFrom con il dispositivo specificato
    /// </summary>
    /// <param name="item">Il dispositivo da cui eseguire il GetFrom</param>
    private void ExecuteGetFrom(AutomationDevice item)
    {
        try
        {
            if (item.LoadedLu?.StepToDo != null && item.Status == DeviceStatusTypes.Ready)
                StartGetFromMOVE_LU(item);
        }
        catch (Exception ex)
        {
            device.LogError(ex);
        }
    }

    /// <summary>
    /// Esegue il movimento GetFrom tra due dispositivi
    /// </summary>
    /// <param name="mission">La missione dello step, con l'avvio contato</param>
    /// <param name="stepToDo">Lo step da eseguire</param>
    /// <param name="sourceItem">La sorgente gia' risolta dal chiamante</param>
    /// <param name="destinationItem">La destinazione gia' risolta dal chiamante</param>
    /// <param name="seenRouteVersion">La versione della rotta letta prima dello step</param>
    /// <remarks>
    /// <b>Qui non c'e' piu' nessuna guardia sui dispositivi, ed e' il punto</b> (l'unica guardia e' quella
    /// della versione della rotta sul gettone, compito 6 della voce 41, nel corpo). Sorgente e destinazione arrivano
    /// gia' risolte da <see cref="StartGetFromMOVE_LU"/>, che le risolve <b>una volta sola e prima
    /// della prenotazione</b>. Prima le due uscite mute stavano qui, dopo che
    /// <see cref="ConfigureGetFromStep"/> aveva gia' preso il posto sulla destinazione: i due
    /// dispositivi si risolvono a ogni lettura attraverso <c>PathDevices</c>, e uno rimosso fra le
    /// due chiamate faceva uscire di qui in silenzio lasciando una prenotazione presa per uno step
    /// che nessuno avrebbe eseguito. Quella prenotazione non aveva nessuna rete: le vie di
    /// restituzione passano tutte dal <c>RunningStepId</c> di qualcuno, e a quel punto nessuno ce
    /// l'aveva ancora.
    /// <para>
    /// Anche le due letture dell'indirizzo di campo sono entrate nel <c>try</c>: erano fuori, e
    /// un'eccezione di li' saltava <see cref="HandleGetFromError"/> per finire nel <c>catch</c> di
    /// <see cref="ExecuteGetFrom"/>, che scrive la riga ma non rimette a posto niente.
    /// </para>
    /// <para>
    /// <b>Un dispositivo solo, non due nomi per lo stesso</b> (revisione dell'ondata 1, voce m2).
    /// Qui c'era anche <c>fromItem</c>, quello che <see cref="StartGetFromMOVE_LU"/> riceve dal
    /// chiamante, e il gettone si scriveva su di lui mentre <see cref="ConfigureGetFromStep"/>
    /// scriveva <c>Moving</c> su <c>sourceItem</c>. Sono lo <b>stesso dispositivo per
    /// costruzione</b> — <c>fromItem.LoadedLu</c> e' la missione posizionata su <c>fromItem</c>, e
    /// <c>StepToDo</c> si risolve da <c>PositionItem.StepToDoSource</c>, che filtra
    /// <c>FromNodeId == PositionItem.ItemGuid</c>, quindi <c>stepToDo.SourceItem</c> e'
    /// <c>PathDevices[fromItem.ItemGuid]</c> — ma potevano essere due <b>oggetti</b> diversi per lo
    /// stesso guid, se <c>PathDevices</c> ne avesse sostituito l'istanza: gettone e stato di
    /// lavorazione sarebbero finiti su oggetti diversi, e quello che il resto del motore legge e'
    /// l'oggetto di <c>PathDevices</c>. Adesso da qui in giu' ce n'e' uno solo, e la scelta e'
    /// <c>sourceItem</c> proprio perche' viene da li'.
    /// </para>
    /// <para>
    /// E la scrittura del gettone e' <b>dentro</b> il <c>try</c>: era fuori, e quel setter solleva
    /// eventi e tocca <c>PathDevices</c>. Se falliva, la prenotazione presa da
    /// <see cref="ConfigureGetFromStep"/> non tornava indietro di qui — la rete della voce I2
    /// l'avrebbe presa al giro dopo, che e' proprio l'uscita che <c>C5</c> era andata a chiudere.
    /// </para>
    /// </remarks>
    private void ExecuteGetFromMovement(PathMission mission, MissionStep stepToDo, AutomationDevice sourceItem,
        AutomationDevice destinationItem, long seenRouteVersion)
    {
        try
        {
            device.StepStartPauseForTesting?.Invoke();

            // Il gettone si scrive sotto il lucchetto della rotta, solo se la rotta e' ancora quella
            // su cui lo step si e' letto: fra il posto preso (ConfigureGetFromStep) e il gettone una
            // ricostruzione poteva togliere lo step, e il comando partiva lo stesso per uno step che
            // non c'era piu' (corsa 5 della ricognizione della voce 41, compito 6). Se la rotta e'
            // cambiata il comando non parte: il posto sulla destinazione si restituisce, lei torna
            // Ready se e' ancora la Busy di ConfigureGetFromStep, e la sorgente torna Ready.
            if (!mission.TryTakeTheStepToken(seenRouteVersion, stepToDo, () => sourceItem.RunningStepId = stepToDo.StepId))
            {
                if (sourceItem.Status == DeviceStatusTypes.Moving)
                    sourceItem.Status = DeviceStatusTypes.Ready;
                destinationItem.ReleaseAfterFailedStep(stepToDo.StepId);

                // Come il movimento singolo che rinuncia: chi riceve riprova con la rotta nuova, appena e'
                // libero (AutomationDevice.ExecuteAgainWhenFree, risposta 4 del proprietario dell'11/10).
                device.ExecuteAgainWhenFree();
                return;
            }

            RemoveDevicesReadyToTransferLu(destinationItem);
            mission.Status = MissionStatusTypes.Run;

            if (device is { GroupItemId: 0, BelongedGroup: null })
                mission.GroupedMovementId = Guid.Empty;

            mission.GatewayItem = destinationItem;

            var fromAsiItem = Navigator.GetDataAsi(sourceItem);
            var toAsiItem = Navigator.GetDataAsi(destinationItem);

            stepToDo.StartStep = DateTime.Now;
            stepToDo.RunningItem = sourceItem;

            device.DeviceConnection?.MoveLuFromAsiToAsi(Navigator.GetMovementRunAsi(sourceItem), toAsiItem, fromAsiItem,
                toAsiItem, sourceItem.RunningStepId, stepToDo.PathMission?.LuId ?? 0, stepToDo.PathMission?.HandlingMode ?? 0);
        }
        catch (Exception ex)
        {
            HandleGetFromError(stepToDo, sourceItem, destinationItem, ex);
        }
    }

    /// <summary>
    /// Esegue il comando di movimento verso il sistema di controllo
    /// </summary>
    /// <param name="lUtoMove">Lista dei comandi di movimento da eseguire</param>
    /// <param name="steps">Gli step di quei comandi: se il comando non parte, i loro posti si restituiscono</param>
    /// <param name="groupedMovementId">Il gettone del gruppo, che il PLC rimanda nel completamento (voce D166)</param>
    private void ExecuteMovementCommand(List<MovementLuDataCommand> lUtoMove, List<MissionStep> steps, Guid groupedMovementId)
    {
        var runAsiItemItem = Navigator.GetDataAsi(device);

        try
        {
            device.DeviceConnection?.MoveLuFromAsiToAsi(Navigator.GetMovementRunAsi(device), runAsiItemItem,
                groupedMovementId, lUtoMove);
        }
        catch (Exception ex)
        {
            HandleMovementError(ex);

            // Il comando non e' partito: il posto sulle destinazioni va restituito insieme allo
            // stato, come fa StartPutToMOVE_LU. Voce D152.
            ReleaseDestinations(steps);
        }
    }

    /// <summary>
    /// Prende il posto su ogni destinazione che non sia un magazzino, con lo <c>StepId</c> dello
    /// step che ci arriva: e' la stessa prenotazione del movimento singolo (voce D73), e come li'
    /// i magazzini restano fuori. Restituisce gli step che possono partire: quello la cui
    /// destinazione e' gia' di un altro step resta fuori dal giro, con una riga che lo dice.
    /// </summary>
    /// <param name="steps">Gli step del movimento raggruppato</param>
    /// <returns>Gli step le cui destinazioni sono prenotate (o non si prenotano)</returns>
    /// <remarks>
    /// Al ritentativo (<c>RepeatGroupedSteps</c>) gli stessi step ripassano di qui e ritrovano il
    /// proprio posto: <c>TryReserveForIncomingStep</c> risponde si' allo step che lo tiene gia'.
    /// Il posto si restituisce in <c>StepDoneGroup</c>, per tutti gli step arrivati e prima di
    /// lavorarli, perche' il setter di <c>RunningStepId</c> del mittente — che lo restituisce nel
    /// movimento singolo — qui porta il gettone del gruppo e non lo <c>StepId</c>. Voce D152.
    /// </remarks>
    private List<MissionStep> ReserveDestinations(List<MissionStep> steps)
    {
        var reserved = new List<MissionStep>(steps.Count);

        foreach (var step in steps)
        {
            if (step.DestinationItem is { DeviceType: not DeviceTypes.Storage } destination &&
                !destination.TryReserveForIncomingStep(step.StepId))
            {
                Navigator.WriteLog(
                    $"ExecuteGroupedMOVE_LU_TO {device.ItemGuid}: la destinazione {destination.ItemGuid} e' gia' impegnata per lo step {destination.WaitingStepId}, lo step {step.StepId} resta fuori da questo giro",
                    LogEntryType.Information);
                continue;
            }

            reserved.Add(step);
        }

        return reserved;
    }

    /// <summary>
    /// Restituisce i posti presi da <see cref="ReserveDestinations"/>: solo quelli ancora tenuti da
    /// quegli step, come vuole <c>ReleaseIncomingStep</c>.
    /// </summary>
    /// <param name="steps">Gli step i cui posti si restituiscono</param>
    private static void ReleaseDestinations(IEnumerable<MissionStep> steps)
    {
        foreach (var step in steps)
            if (step.DestinationItem is { DeviceType: not DeviceTypes.Storage } destination)
                destination.ReleaseIncomingStep(step.StepId);
    }

    /// <summary>
    /// Dopo la verifica delle celle del gruppo (<see cref="CheckCellStoreStatus"/>) rilegge gli step
    /// scelti la cui rotta e' stata riscritta: al posto di ognuno prende lo step della rotta di adesso
    /// con gli stessi due capi, e la versione della rotta della sua missione, letta prima di cercarlo.
    /// Non guarda chi ha riscritto la rotta: la riscrittura della verifica stessa e' il caso per cui
    /// esiste, ma qualunque riscrittura con uno step dagli stessi due capi viene seguita allo stesso
    /// modo (lo step va comunque verso la destinazione su cui il posto e' preso).
    /// Lo step che nella rotta di adesso non c'e' piu' resta fuori da questo giro, con il suo posto
    /// restituito; lo riprende il giro dopo, con la rotta nuova.
    /// </summary>
    /// <param name="steps">Gli step con il posto preso (<see cref="ReserveDestinations"/>), aggiornati qui</param>
    /// <param name="versions">La versione della rotta di ogni missione contata, aggiornata qui</param>
    /// <remarks>
    /// <para>
    /// La verifica riscrive da se' la rotta delle missioni a bordo del gruppo: anche quando tiene la
    /// stessa cella (<c>FindWhereStoreLu</c> con la prenotazione tenuta) la ricostruisce, con step nuovi
    /// (<c>BuildPathToDestination</c>), e la versione sale. Con la versione letta prima della verifica
    /// (<c>BeginStepStarts</c>) il gettone del gruppo rinunciava ogni volta per la riscrittura del
    /// gruppo stesso: il gruppo tornava <c>Ready</c> senza comando, e il giro dopo rifaceva lo stesso,
    /// senza fine. E' quello che fa gia' il deposito in cella dopo la sua riverifica
    /// (<c>AutomationDeviceStorageManager</c>): versione e step si rileggono dopo. Primo giro di
    /// correzioni del compito 6 della voce 41.
    /// </para>
    /// <para>
    /// Lo step scartato non torna in <c>MissionSteps</c> (corsa 5): parte lo step della rotta di
    /// adesso, e solo se va dallo stesso dispositivo allo stesso dispositivo dello step scelto, cioe'
    /// verso la destinazione su cui il posto e' stato preso. Il posto passa allo step nuovo: quello
    /// vecchio si restituisce e il nuovo si prende, e se intanto l'ha preso un altro lo step resta fuori.
    /// Una riscrittura che arriva dopo questa lettura fa salire la versione, e <see cref="ConfigureStep"/>
    /// rinuncia come prima. Qui solo letture della memoria e i posti: niente gestionale.
    /// </para>
    /// </remarks>
    private void FollowTheRewrittenRoutes(List<MissionStep> steps, Dictionary<PathMission, long> versions)
    {
        var reread = new Dictionary<PathMission, long>(ReferenceEqualityComparer.Instance);

        for (var i = steps.Count - 1; i >= 0; i--)
        {
            var step = steps[i];

            if (step.PathMission is not { } mission || !versions.ContainsKey(mission) || MissionSteps.Contains(step))
                continue;

            // La versione prima dello sguardo alla rotta, come in BeginStepStarts: una rimozione dopo
            // lo sguardo la fa salire, e il gettone del gruppo non si scrive.
            if (!reread.TryGetValue(mission, out var version))
            {
                version = mission.RouteVersion;
                reread[mission] = version;
            }

            var current = MissionSteps.FirstOrDefault(x =>
                x.MissionId == mission.MissionId && x.FromNodeId == step.FromNodeId && x.ToNodeId == step.ToNodeId);

            ReleaseDestinations([step]);

            if (current == null ||
                current.DestinationItem is { DeviceType: not DeviceTypes.Storage } destination &&
                !destination.TryReserveForIncomingStep(current.StepId))
            {
                steps.RemoveAt(i);
                Navigator.WriteLog(
                    current == null
                        ? $"ExecuteGroupedMOVE_LU_TO {device.ItemGuid}: la rotta della missione {mission.MissionId} e' cambiata durante la verifica delle celle e non ha piu' uno step come {step.StepId}, che resta fuori da questo giro"
                        : $"ExecuteGroupedMOVE_LU_TO {device.ItemGuid}: la rotta della missione {mission.MissionId} e' cambiata durante la verifica delle celle, e la destinazione del suo step {current.StepId} e' gia' impegnata: resta fuori da questo giro",
                    LogEntryType.Information);
                continue;
            }

            steps[i] = current;
            versions[mission] = version;
        }
    }

    /// <summary>
    /// Esegue un'operazione MoveTo con il dispositivo specificato
    /// </summary>
    /// <param name="item">Il dispositivo verso cui eseguire il MoveTo</param>
    private void ExecuteMoveTo(AutomationDevice item)
    {
        try
        {
            if (item.Status == DeviceStatusTypes.Ready)
                _ = item.ExecuteStep(500);
        }
        catch (Exception ex)
        {
            device.LogError(ex);
        }
    }

    /// <summary>
    /// Esegue il movimento fisico per un'operazione di prelievo
    /// </summary>
    /// <param name="stepToDo">Lo step di prelievo da eseguire</param>
    /// <param name="fromAsiItem">
    /// L'indirizzo di campo da cui prelevare, gia' letto dal chiamante <b>prima</b> di prendere i
    /// gettoni: e' l'unica operazione di questo percorso che puo' fallire, e da qui in poi non
    /// resta nessuna uscita che lasci un gettone appeso.
    /// </param>
    /// <remarks>
    /// La destinazione si risolve <b>una volta</b>, prima del <c>try</c>, e il <c>catch</c> usa
    /// quella: e' la stessa forma della gemella <c>AutomationDeviceStorageManager.StartStoreLu</c>,
    /// che la revisione dell'ondata 1 (voce m3) ha trovato asimmetrica rispetto a questa. Non e'
    /// una rete contro una lettura che risponda nullo — <c>MissionStep.DestinationItem</c> legge
    /// <c>PathDevices[ToNodeId]</c> e sopravvive benissimo alla sparizione dello step — ma le due
    /// gemelle devono avere la stessa forma, e chi restituisce un posto deve sapere a chi lo sta
    /// restituendo senza rileggerlo da una collezione che nel frattempo e' cambiata.
    /// </remarks>
    private async Task ExecutePickUpMovement(MissionStep stepToDo, DataAsi fromAsiItem)
    {
        var toAsiItem = Navigator.GetDataAsi(device);
        var destinationItem = stepToDo.DestinationItem;

        try
        {
            // Task.Delay e non Thread.Sleep: qui gira un thread del pool, condiviso da tutti i
            // dispositivi, e un prelievo che dorme non deve fermare il lavoro degli altri. La
            // durata resta quella di prima: e' il tempo concesso al campo prima del comando, e
            // cambiarla e' una decisione da prendere guardando il PLC. Voce D72 del registro.
            await Task.Delay(200);
            stepToDo.StartStep = DateTime.Now;
            stepToDo.RunningItem = device;

            device.DeviceConnection?.MoveLuFromAsiToAsi(Navigator.GetMovementRunAsi(device), toAsiItem, fromAsiItem,
                toAsiItem, device.RunningStepId, stepToDo.PathMission?.LuId ?? 0, stepToDo.PathMission?.HandlingMode ?? 0);
        }
        catch (Exception ex)
        {
            device.RunningStepId = Guid.Empty;
            device.Status = DeviceStatusTypes.Ready;

            // Anche il posto, esplicitamente: l'azzeramento del gettone qui sopra lo restituirebbe
            // per conto suo, ma solo finche' lo step e' ancora in MissionSteps. Stessa scelta che
            // HandleGetFromError dichiara sul posto.
            destinationItem?.ReleaseAfterFailedStep(stepToDo.StepId);

            device.LogError(ex);
        }
    }

    /// <summary>
    /// Esegue la coda delle LU da prelevare
    /// </summary>
    private async Task ExecuteQueueLuToPickUp()
    {
        if (device.QueuedLuToPickUpCount == 0 || device.LUsLoaded.Count != 0 || device.LUtoPick != null ||
            device.Status is not (DeviceStatusTypes.Ready or DeviceStatusTypes.Recovering))
            return;

        // La presa in carico e' un solo passo. Prima erano due — assegnare LUtoPick e poi togliere
        // dalla coda — e fra l'uno e l'altro un secondo thread poteva prendere la stessa UDC: il
        // dispositivo si trovava a prelevare due volte lo stesso bancale.
        var missionToPickUp = device.TryTakeLuToPickUp(candidate =>
            device.LuManager?.CanPickUpLu(candidate) == true && device.CanExtractLUs);

        if (missionToPickUp == null)
            return;

        if (missionToPickUp.LuId == 0 ||
            (device.LuManager != null && await device.LuManager.VerifyPickUp(missionToPickUp)))
        {
            ActiveMissions.Instance.Add(missionToPickUp);
            await StartPickUpLu(missionToPickUp);
        }
        else
        {
            await HandleFailedPickUp();
        }
    }

    /// <summary>
    /// Esegue la coda per ricevere LU da altri dispositivi
    /// </summary>
    private async Task ExecuteQueueToReceiveLu()
    {
        if (device.LUsLoaded.Count != 0 || device.LUtoPick != null || device.Status != DeviceStatusTypes.Ready)
            return;

        var lUsToReceive = GetLUsToReceive();

        if (lUsToReceive.Count == 0)
        {
            ProcessReadyDevicesWithoutLu();
            return;
        }

        await ProcessLUsToReceive(lUsToReceive);
    }

    /// <summary>
    /// Esegue step basandosi sulla priorità configurata tra prelievi e depositi
    /// </summary>
    private async Task ExecuteStepByPriority()
    {
        var pickUpUnit = device.PickUpUnitStep;
        var storeUnit = device.StoreUnitStep;

        if (pickUpUnit > storeUnit)
        {
            device.LastExecutedStepType = ExecutedStepTypes.PickUp;
            await ExecuteQueueLuToPickUp();
        }
        else if (pickUpUnit < storeUnit)
        {
            device.LastExecutedStepType = ExecutedStepTypes.Store;
            await ExecuteQueueToReceiveLu();
        }
        else if (device.LastExecutedStepType == ExecutedStepTypes.Store)
        {
            device.LastExecutedStepType = ExecutedStepTypes.PickUp;
            await ExecuteQueueLuToPickUp();
        }
        else
        {
            device.LastExecutedStepType = ExecutedStepTypes.Store;
            await ExecuteQueueToReceiveLu();
        }
    }

    /// <summary>
    /// Finalizza il processo di distribuzione circolare
    /// </summary>
    private void FinalizeCircularDistribution()
    {
        if (device.LoadedLu != null) device.LoadedLu.Status = MissionStatusTypes.Stop;
        device.Status = DeviceStatusTypes.Ready;
        device.RunningStepId = Guid.Empty;
        device.StartPutToMOVE_LU();
    }

    /// <summary>
    /// Finalizza uno step eseguito aggiornando gli stati e processando il trasferimento
    /// </summary>
    /// <param name="executedStep">Lo step completato</param>
    /// <param name="stepType">Il tipo di step eseguito</param>
    /// <param name="detectedDepth">La profondità rilevata</param>
    private async Task FinalizeExecutedStep(MissionStep executedStep, StepTypes stepType, int detectedDepth)
    {
        executedStep.EndStep = DateTime.Now;
        executedStep.StepStatus = ResultCodeTypes.Success;

        if (executedStep is { SourceItem: not null, DestinationItem: not null, PathMission: not null })
        {
            var luToTransfer = executedStep.PathMission;

            luToTransfer.StepOrigin = stepType;

            var fromAsiItem = Navigator.GetDataAsi(executedStep.SourceItem);
            var toAsiItem = Navigator.GetDataAsi(executedStep.DestinationItem);

            string? infoDescription = null;

            if (executedStep.ToNodeId == device.ItemGuid)
            {
                infoDescription = await ProcessToNodeTransfer(executedStep, luToTransfer, fromAsiItem, toAsiItem);
            }
            else if (executedStep.FromNodeId == device.ItemGuid)
            {
                infoDescription = await ProcessFromNodeTransfer(executedStep, luToTransfer, detectedDepth, fromAsiItem, toAsiItem);
            }

            if (infoDescription == null) return; // Error case handled in method
        }

        await FinalizeStepProcessing(executedStep);
    }

    /// <summary>
    /// Finalizza il processamento di uno step rimuovendolo dalle code e attivando i processi successivi
    /// </summary>
    /// <param name="executedStep">Lo step da finalizzare</param>
    private async Task FinalizeStepProcessing(MissionStep executedStep)
    {
        // Lo step eseguito esce dalla rotta per identita', senza la versione della rotta (voce 41):
        // l'avanzamento la fa salire il cambio di posizione dell'arrivo (setter di PositionItemId, in
        // ThreadLoadLu, che ProcessSuccessfulTransfer lancia sul pool: arriva prima o dopo di questa
        // riga), e invalida le rotte calcolate dalla posizione di prima. Farla salire anche
        // qui farebbe perdere la ricostruzione partita da quel cambio di posizione (l'arrivo di un
        // PickUpAndStoreAgain) quando questa riga arriva dopo di lei, e il giro delle rotte non la
        // rifa': per una missione gia' sulla sua destinazione BuildPathToDestination non costruisce. Una
        // scrittura della rotta in corso non si mescola con questa rimozione: o lo step e' ancora fra
        // quelli che la scrittura toglie, o non c'e' piu'.
        if (device.BelongedGroup != null)
            device.BelongedGroup.RemoveStep(executedStep);
        else
            device.RemoveStep(executedStep);

        if (device.DeviceType == DeviceTypes.CircularDistributor)
            device.StorageManager?.StartProcessExtractionLu();

        if (device.EmptyCellStatus == EmptyCellStatusTypes.Active)
            device.EmptyCellStatus = EmptyCellStatusTypes.None;

        if (device.AutoExecuteStep)
            _ = device.ExecuteStep(500);
        // LuManager e' costruito da Initialize e non torna mai nullo: la guardia
        // sostituisce "await x?.M()", che sollevava NullReferenceException.
        else if (device.LuManager != null)
            await device.LuManager.CheckGetFromItem();
    }

    /// <summary>
    /// Trova uno step eseguito nella collezione degli step di missione
    /// </summary>
    /// <param name="missionSteps">La collezione in cui cercare</param>
    /// <param name="idStepDone">L'ID dello step completato</param>
    /// <returns>Lo step trovato o null se non trovato</returns>
    private MissionStep? FindExecutedStep(MissionSteps missionSteps, Guid idStepDone)
    {
        return missionSteps.FirstOrDefault(x => x.StepId == idStepDone);
    }

    /// <summary>
    /// Ottiene la lista delle LU pronte per essere ricevute da dispositivi pronti per il trasferimento
    /// </summary>
    /// <returns>Lista delle missioni di percorso pronte per la ricezione</returns>
    private List<PathMission?> GetLUsToReceive()
    {
        return device.DevicesReadyToTransferLu
            .Where(x => x is { LoadedLu: not null, Status: DeviceStatusTypes.Ready })
            .Select(x => x.LoadedLu)
            .ToList();
    }

    /// <summary>
    /// Ottiene le informazioni ASI per un'operazione di prelievo
    /// </summary>
    /// <param name="stepToDo">Lo step di prelievo</param>
    /// <returns>I dati ASI della posizione di prelievo</returns>
    private async Task<DataAsi?> GetPickUpAsiItem(MissionStep stepToDo)
    {
        if (stepToDo.PathMission is not { } mission) return null;

        return device.RecordStorageLength switch
        {
            RecordStorageTypes.ConvertDepthToPartition => await mission.GetAsiCellInfoAsync(mission.PickUpDoorCode, mission.StorageDepth),
            RecordStorageTypes.FromPlc or RecordStorageTypes.FixedLength or RecordStorageTypes.CalculatedStorageDepth => await mission.GetAsiCellInfoAsync(mission.PickUpDoorCode),
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    /// <summary>
    /// Gestisce il fallimento di un'operazione di prelievo
    /// </summary>
    private async Task HandleFailedPickUp()
    {
        var luToPick = device.LUtoPick;
        if (luToPick?.ExtractionQueueId > 0)
        {
            // Compito 13: come nel gemello AutomationDeviceLuManager.CleanupFailedPickup, qui
            // c'era DbInfo.SetAssignmentsNotesProcessed. Il prelievo e' fallito: l'assegnazione non
            // e' stata servita, e il fatto vero di questo punto lo dice la riga qui sotto, l'esito
            // NotReachable sulla riga di coda.
            //
            // La prima stesura diceva che a dirlo e' ActiveMissions.RemoveUnused in fondo al metodo:
            // non e' vero, per la ragione scritta per esteso nel gemello. Le segnalazioni di
            // ActiveMissions.Remove stanno dentro if (eraSuUnDispositivo) - e la prima anche dentro
            // if (GuidAssignmentLu != Guid.Empty) - mentre RemoveUnused passa da
            // CheckAndRemoveMission, che rimuove SOLO le missioni con PositionItem nullo o su un
            // magazzino: la negazione esatta di quella guardia. E la strada per riga di coda sarebbe
            // comunque muta, perche' la riga di coda viene cancellata qui sotto, prima di
            // RemoveUnused. Sul lato dell'assegnazione questo punto non segnala niente, come non
            // segnalava niente di vero prima.
            await DbInfo.ReportExtractionRowOutcome(luToPick.ExtractionQueueId, ExtractionRowStatus.NotReachable);

            if (luToPick.LuId > 0)
                Navigator.WriteLog(
                    $"DEBUG INFO: Cancellazione ESTRAZIONI_UDC_AUTOMATICHE da ExecuteQueueLuToPickUp UDC: {luToPick.LuId} - {luToPick.LuCode} Date: {DateTime.Now:dd/MM/yyyy HH:mm:ss}",
                    LogEntryType.Information);

            await DbInfo.DeleteExtractionItemQueueById(luToPick.ExtractionQueueId);
        }

        if (luToPick?.LuId > 0)
            Navigator.WriteLog(
                $"DEBUG INFO: Cancellazione MISSION_LU da ExecuteQueueLuToPickUp UDC: {luToPick.LuId} - {luToPick.LuCode} Date: {DateTime.Now:dd/MM/yyyy HH:mm:ss}",
                LogEntryType.Information);

        await ActiveMissions.Instance.RemoveUnused(luToPick);
        device.LUtoPick = null;
    }

    /// <summary>
    /// Gestisce errori durante operazioni GetFrom
    /// </summary>
    /// <param name="stepToDo">Lo step che ha causato l'errore</param>
    /// <param name="fromItem">La sorgente: e' lei che tiene il gettone e lo stato di lavorazione</param>
    /// <param name="destinationItem">La destinazione: e' lei che tiene la prenotazione</param>
    /// <param name="ex">L'eccezione verificatasi</param>
    /// <remarks>
    /// <b>In un <c>GetFrom</c> il dispositivo da normalizzare non e' <c>device</c>.</b> Qui
    /// <c>device</c> e' il <i>ricevente</i>, cioe' chi chiede il trasferimento; lo stato di
    /// lavorazione (<c>Moving</c>) lo scrive <see cref="ConfigureGetFromStep"/> e il gettone
    /// (<c>RunningStepId</c>) lo scrive <see cref="ExecuteGetFromMovement"/>, tutti e due sullo
    /// <b>stesso</b> oggetto sorgente — quello di <c>PathDevices</c>, ed e' <paramref name="fromItem"/>
    /// qui. Azzerare <c>device.RunningStepId</c> era un'operazione nulla — non e' mai stato
    /// scritto — e la sorgente restava <c>Moving</c> con il gettone addosso.
    /// <para>
    /// Da li' non si usciva piu' da soli: <c>TryReleaseHungRunningStep</c> pretende <c>Ready</c>
    /// (e la sorgente e' <c>Moving</c>) e che nessun comando sia partito da qui (e
    /// <c>step.RunningNode</c> e' proprio suo); <c>Moving</c> e' fra gli stati che
    /// <c>IsBusyWithCommand</c> considera impegnati, quindi <c>ExecuteStep</c> rifiutava ogni
    /// esecuzione; e <c>ResetError</c> dell'operatore vuole <c>Status == Error</c>. Nessun timeout
    /// del PLC interveniva, perche' il comando non era partito. E' il fratello del guasto del
    /// 2026-09-09, con in piu' il fatto che la rete automatica non poteva prenderlo.
    /// </para>
    /// <para>
    /// Lo stato torna <c>Ready</c> solo se e' ancora la <c>Moving</c> messa da questo
    /// trasferimento: un errore o un offline sopraggiunti valgono di piu'. E' la stessa regola che
    /// <see cref="AutomationDevice.ReleaseAfterFailedStep"/> applica alla destinazione, che resta
    /// il modo giusto di rimettere a posto il ricevente.
    /// </para>
    /// </remarks>
    private void HandleGetFromError(MissionStep stepToDo, AutomationDevice fromItem,
        AutomationDevice? destinationItem, Exception ex)
    {
        // Il setter restituisce anche la prenotazione presa insieme al gettone.
        fromItem.RunningStepId = Guid.Empty;

        if (fromItem.Status == DeviceStatusTypes.Moving)
            fromItem.Status = DeviceStatusTypes.Ready;

        // Anche il posto sulla destinazione. L'azzeramento del gettone qui sopra lo restituirebbe
        // gia' per conto suo, ma dirlo esplicitamente non costa e non dipende da quel meccanismo.
        destinationItem?.ReleaseAfterFailedStep(stepToDo.StepId);

        device.LogError(ex);
    }

    /// <summary>
    /// Gestisce errori durante operazioni di movimento
    /// </summary>
    /// <param name="ex">L'eccezione verificatasi</param>
    private void HandleMovementError(Exception ex)
    {
        device.RunningStepId = Guid.Empty;
        device.Status = DeviceStatusTypes.Ready;
        device.LogError(ex);
        device.GroupExecuteStepRunning = false;
    }

    /// <summary>
    /// Gestisce il caso in cui non ci sia uno step da eseguire per una missione
    /// </summary>
    /// <param name="value">La missione di percorso senza step</param>
    private void HandleNoStepToDo(PathMission value)
    {
        RoutesToBuild.Instance.Enqueue(value);
        device.RunningStepId = Guid.Empty;
        device.Status = DeviceStatusTypes.Ready;
    }

    /// <summary>
    /// Gestisce il caso di dispositivi nulli durante l'esecuzione di uno step
    /// </summary>
    /// <param name="sourceItem">Il dispositivo sorgente (può essere null)</param>
    /// <param name="destinationItem">Il dispositivo destinazione (può essere null)</param>
    private void HandleNullItems(AutomationDevice? sourceItem, AutomationDevice? destinationItem)
    {
        if (sourceItem != null) sourceItem.Status = DeviceStatusTypes.Ready;
        if (destinationItem != null) destinationItem.Status = DeviceStatusTypes.Ready;

        Navigator.WriteLog(
            $"Error On ASI {device.ASI}.{device.SUBITEM}.{device.PARTITION} StepDone SourceItem or Destination item was Nothing",
            LogEntryType.Warning);
    }

    /// <summary>
    /// Gestisce errori generici durante l'esecuzione di step
    /// </summary>
    /// <param name="sourceItem">Il dispositivo sorgente</param>
    /// <param name="destinationItem">Il dispositivo destinazione</param>
    /// <param name="ex">L'eccezione verificatasi</param>
    private void HandleStepError(AutomationDevice? sourceItem, AutomationDevice? destinationItem, Exception ex)
    {
        sourceItem?.Status = DeviceStatusTypes.Ready;
        destinationItem?.Status = DeviceStatusTypes.Ready;
        device.LogError(ex);
    }

    /// <summary>
    /// Gestisce il movimento di una LU verso una posizione di stoccaggio
    /// </summary>
    /// <param name="detectedDepth">La profondità rilevata</param>
    /// <param name="luToTransfer">La LU da trasferire</param>
    private async Task MoveLuToStorage(int detectedDepth, PathMission luToTransfer)
    {
        if (luToTransfer.MissionType != MissionTypes.ChangeStoreLocation)
            device.CurrentStoreStep++;

        if (luToTransfer.LuId <= 0) return;
        switch (device.RecordStorageLength)
        {
            case RecordStorageTypes.ConvertDepthToPartition:
                luToTransfer.DetectedDepth = luToTransfer.StorageDepth;
                break;
            case RecordStorageTypes.FixedLength:
                luToTransfer.DetectedDepth = device.StorageOffset;
                luToTransfer.StorageDepth = luToTransfer.DetectedDepth;
                break;
            case RecordStorageTypes.FromPlc:
                await ProcessFromPlcStorage(detectedDepth, luToTransfer);
                break;
            case RecordStorageTypes.CalculatedStorageDepth:
                luToTransfer.DetectedDepth = luToTransfer.StorageDepth;
                break;
        }
    }

    /// <summary>
    /// Popola i comandi di movimento per un'esecuzione raggruppata
    /// </summary>
    /// <param name="steps">Gli step da convertire in comandi</param>
    /// <param name="lUtoMove">Lista dei comandi di movimento da popolare</param>
    /// <param name="collections">Collezioni per organizzare i dispositivi per stato</param>
    /// <param name="groupedMovementId">Il gettone del gruppo da scrivere su ogni step (voce D166)</param>
    /// <param name="versions">La versione della rotta di ogni missione contata, letta prima dello step</param>
    /// <remarks>
    /// Lo step la cui rotta e' cambiata dopo la scelta (<see cref="ConfigureStep"/> risponde no) resta
    /// fuori dal movimento, e il posto preso per lui si restituisce subito: senza il suo comando nessun
    /// completamento lo restituirebbe (corsa 5 della ricognizione della voce 41, compito 6).
    /// </remarks>
    private async Task PopulateMovementCommands(List<MissionStep> steps, List<MovementLuDataCommand> lUtoMove,
        (List<AutomationDevice> Busy, List<AutomationDevice> Moving, List<AutomationDevice> Loading, List<AutomationDevice> Unloading) collections,
        Guid groupedMovementId, Dictionary<PathMission, long> versions)
    {
        foreach (var step in steps.Where(x => x.PathMission != null))
        {
            if (step.PathMission == null) continue;

            if (!ConfigureStep(step, groupedMovementId, versions))
            {
                ReleaseDestinations([step]);
                continue;
            }

            var lu = CreateMovementCommand(step);

            if (lu == null) continue;

            await ConfigureSourceDestination(step, lu, collections);
            lUtoMove.Add(lu);
        }
    }

    /// <summary>
    /// Processa una LU per distribuzione circolare
    /// </summary>
    /// <returns>True se è stata trovata una destinazione disponibile, false altrimenti</returns>
    /// <remarks>Interno per le prove della voce 40 (<c>CircularDistributorOperatorChoiceTest</c>).</remarks>
    internal bool ProcessCircularDistributionLu()
    {
        var noItemAvailable = true;

        if (device.LoadedLu?.GatewayItem != null)
        {
            if (device.LoadedLu.GatewayItem.LuManager?.CanAcceptLu(device.LoadedLu) == true)
                noItemAvailable = false;
        }
        else
        {
            noItemAvailable = !ProcessLinkedItems();
        }

        return !noItemAvailable;
    }

    /// <summary>
    /// Processa un trasferimento verso una destinazione
    /// </summary>
    /// <param name="executedStep">Lo step eseguito</param>
    /// <param name="luToTransfer">La LU da trasferire</param>
    /// <param name="infoDescription">Descrizione dell'operazione</param>
    /// <param name="fromAsiItem">Dati ASI sorgente</param>
    /// <param name="toAsiItem">Dati ASI destinazione</param>
    /// <returns>True se il trasferimento è riuscito, false altrimenti</returns>
    private bool ProcessDestinationTransfer(MissionStep executedStep, PathMission luToTransfer, string infoDescription, DataAsi fromAsiItem, DataAsi toAsiItem)
    {
        if (executedStep.DestinationItem == null) return false;

        var destinationItem = executedStep.DestinationItem;

        if (destinationItem.LoadedLu == null || destinationItem.DeviceType == DeviceTypes.Storage)
        {
            ProcessSuccessfulTransfer(executedStep, luToTransfer, infoDescription, destinationItem);
            return true;
        }
        else
        {
            ProcessFailedTransfer(executedStep, luToTransfer, infoDescription, fromAsiItem, toAsiItem, destinationItem);
            return false;
        }
    }

    /// <summary>
    /// Processa un trasferimento fallito
    /// </summary>
    /// <param name="executedStep">Lo step eseguito</param>
    /// <param name="luToTransfer">La LU da trasferire</param>
    /// <param name="infoDescription">Descrizione dell'operazione</param>
    /// <param name="fromAsiItem">Dati ASI sorgente</param>
    /// <param name="toAsiItem">Dati ASI destinazione</param>
    /// <param name="destinationItem">Il dispositivo destinazione</param>
    private void ProcessFailedTransfer(MissionStep executedStep, PathMission luToTransfer, string infoDescription, DataAsi fromAsiItem, DataAsi toAsiItem, AutomationDevice destinationItem)
    {
        executedStep.StepStatus = ResultCodeTypes.Error;
        Navigator.RecordNavigatorLog(device, infoDescription, LogEntryType.Error, executedStep);

        // Stato e posto insieme. Qui si riportava solo lo stato a Ready, ma la destinazione restava
        // prenotata per lo step fallito: era Ready e irraggiungibile, perche' StartPutToMOVE_LU di
        // un terzo elemento avrebbe trovato il posto occupato. E il posto non si libera da solo su
        // questo percorso: RemoveStep non viene raggiunto, quindi il mittente conserva
        // RunningStepId. Voce D73.
        destinationItem.ReleaseAfterFailedStep(executedStep.StepId);

        if (luToTransfer == device.LUtoReceive)
            device.LUtoReceive = null;

        luToTransfer.StatusDescription = $"Error moving LU {luToTransfer.LuCode} from {fromAsiItem.Asi}.{fromAsiItem.Subitem}.{fromAsiItem.Partition} to {toAsiItem.Asi}.{toAsiItem.Subitem}.{toAsiItem.Partition} Error: 1001 - Logical presence on destination item, the step was done but is not possible to transfer LU";
        Navigator.WriteAlert(luToTransfer);
        if (executedStep.SourceItem != null) executedStep.SourceItem.Status = DeviceStatusTypes.Warning;
    }

    /// <summary>
    /// Processa un trasferimento da un nodo (dispositivo corrente come sorgente)
    /// </summary>
    /// <param name="executedStep">Lo step eseguito</param>
    /// <param name="luToTransfer">La LU da trasferire</param>
    /// <param name="detectedDepth">La profondità rilevata</param>
    /// <param name="fromAsiItem">Dati ASI sorgente</param>
    /// <param name="toAsiItem">Dati ASI destinazione</param>
    /// <returns>Descrizione dell'operazione o null in caso di errore</returns>
    private async Task<string?> ProcessFromNodeTransfer(MissionStep executedStep, PathMission luToTransfer, int detectedDepth, DataAsi fromAsiItem, DataAsi toAsiItem)
    {
        var luCode = luToTransfer.LuCode;
        string infoDescription;

        if (executedStep.DestinationItem == null)
        {
            return null;
        }

        switch (executedStep.DestinationItem.DeviceType)
        {
            case DeviceTypes.Storage:
                infoDescription = $"Stored LU {luCode} from {fromAsiItem.Asi}.{fromAsiItem.Subitem}.{fromAsiItem.Partition} to {toAsiItem.Asi}.{toAsiItem.Subitem}.{toAsiItem.Partition}";
                await MoveLuToStorage(detectedDepth, luToTransfer);
                break;
            default:
                infoDescription = $"Moved LU {luCode} from {fromAsiItem.Asi}.{fromAsiItem.Subitem}.{fromAsiItem.Partition} to {toAsiItem.Asi}.{toAsiItem.Subitem}.{toAsiItem.Partition}";
                break;
        }

        return !ProcessDestinationTransfer(executedStep, luToTransfer, infoDescription, fromAsiItem, toAsiItem) ? null : infoDescription;
    }

    /// <summary>
    /// Processa lo stoccaggio da PLC con rilevamento automatico della profondità
    /// </summary>
    /// <param name="detectedDepth">La profondità rilevata dal PLC</param>
    /// <param name="luToTransfer">La LU da stoccare</param>
    private async Task ProcessFromPlcStorage(int detectedDepth, PathMission luToTransfer)
    {
        luToTransfer.StorageDepth = -1;

        if (detectedDepth == -1) return;
        
        luToTransfer.DetectedDepth = detectedDepth;

        if (device.AddStorageLengthLuDepth)
        {
            var luDepth = await DbInfo.GetLuDepth(luToTransfer.LuId);
            luToTransfer.DetectedDepth = luToTransfer.DetectedDepth + device.StorageOffset + luDepth;
        }
        else
        {
            luToTransfer.DetectedDepth += device.StorageOffset;
        }

        luToTransfer.StorageDepth = luToTransfer.DetectedDepth;
    }

    /// <summary>
    /// Processa i dispositivi collegati per trovare una destinazione disponibile
    /// </summary>
    /// <returns>True se è stata trovata una destinazione, false altrimenti</returns>
    private bool ProcessLinkedItems()
    {
        // La destinazione scelta dall'operatore si rispetta (voce 40, decisione del proprietario
        // del 10/10): l'UDC con la scelta esce da un'uscita che ci porta, e le altre continuano
        // come sotto, con la prima uscita libera in ordine di PriorityInRoutePath.
        if (device.LoadedLu is { OperatorDestination: true } chosen)
            return ProcessLinkedItemsTowardsTheOperatorsChoice(chosen);

        if (device.LoadedLu is not { } mission)
            return false;

        // Anche senza la scelta la rotta si scrive con l'avvio contato sulla missione
        // (PathMission.TryBeginStepStart), come per l'UDC con la scelta: con un cambio dell'operatore
        // in corso non si scrive niente e si riprova al giro dopo, e mentre si scrive il cambio non
        // comincia. Prima di qui il cambio lo escludeva solo il semaforo del distributore, che il
        // commit prendeva e non prende piu' (voce 41, compito 5), e solo quando il distributore
        // girava dentro ExecuteStep e non dal suo ciclo: con il commit in mezzo, fra lo sguardo alla
        // scelta qui sopra e la scrittura della destinazione, la scelta appena fatta dall'operatore
        // cadeva e l'UDC usciva dalla prima uscita libera.
        if (!mission.TryBeginStepStart()) return false;

        try
        {
            if (!ReferenceEquals(device.LoadedLu, mission) || mission.OperatorDestination) return false;

            // La destinazione e lo step verso l'uscita si scrivono insieme con la primitiva della rotta,
            // confrontando la versione letta qui (voce 41, disegno C): il resto della rotta si toglie
            // nello stesso colpo, e se un altro l'ha scritta intanto (Lost) o la missione aspetta il
            // rigetto dal campo rimandato (AwaitingRejection) non si scrive niente e si riprova al giro
            // dopo. Fino al giro finale delle correzioni della voce 41 si toglieva uno step, si scriveva
            // la destinazione e si aggiungeva lo step a mano, fuori dal lucchetto e senza la versione:
            // una rotta scritta fra la rimozione e l'aggiunta restava accanto allo step del distributore
            // (due step da questo dispositivo), e un rigetto dal campo rimandato perdeva un altro passo,
            // con l'UDC senza i dati del PLC portata all'uscita prima che il giro rifacesse il rigetto.
            var seenRouteVersion = mission.RouteVersion;

            var exit = device.DestinationItemsLinked.OfType<AutomationDevice>()
                .FirstOrDefault(item => item.LuManager?.CanAcceptLu(mission) == true);
            if (exit == null) return false;

            var outcome = mission.TryWriteRoute(seenRouteVersion,
            [
                new MissionStep
                {
                    Date = DateTime.Now,
                    FromNodeId = device.ItemGuid,
                    ToNodeId = exit.ItemGuid,
                    MissionId = mission.MissionId,
                    StepId = Guid.NewGuid()
                }
            ], () =>
            {
                // Il setter scarta la destinazione se uno step della missione e' in volo: allora niente.
                mission.DestinationItemId = exit.ItemGuid;
                return mission.DestinationItemId == exit.ItemGuid;
            });

            return outcome == PathMission.RouteWriteOutcome.Written;
        }
        finally
        {
            mission.EndStepStart();
        }
    }

    /// <summary>
    /// Il distributore circolare con un'UDC la cui destinazione l'ha scelta l'operatore (voce 40,
    /// decisione del proprietario del 10/10): l'UDC esce dalla prima uscita libera, in ordine di
    /// <c>PriorityInRoutePath</c>, da cui la destinazione scelta si raggiunge - l'uscita stessa, un
    /// dispositivo del gruppo scelto, o una rotta che non torna sul distributore. La destinazione
    /// e il segno restano; la rotta della missione diventa il passo verso l'uscita e, da li', la
    /// rotta verso la destinazione.
    /// </summary>
    /// <param name="mission">L'UDC a bordo, con il segno della scelta dell'operatore.</param>
    /// <returns>True se l'uscita c'e' ed e' libera: la rotta e' scritta.</returns>
    /// <remarks>
    /// <para>
    /// Fino alla voce 40 il distributore scriveva come destinazione la sua prima uscita libera
    /// anche a questa UDC, e il setter della destinazione faceva cadere la scelta con una riga di
    /// avviso: l'UDC andava dove andavano tutte.
    /// </para>
    /// <para>
    /// Le uscite che portano alla destinazione ma sono occupate si aspettano come oggi, al giro
    /// dopo, senza avviso: e' l'attesa ordinaria del distributore. Se invece nessuna uscita ci
    /// porta, la missione aspetta con il freno del navigatore (<c>Navigator.RoutePathRetries</c>):
    /// il fallimento si conta, la prova successiva aspetta che il freno lo consenta, e la riga
    /// e' un avviso al primo fallimento e alle soglie del freno (<c>Navigator.IsABrakeThreshold</c>),
    /// un'informazione fra l'una e l'altra. La scelta non cade: l'operatore puo' cambiarla, e il
    /// commit del cambio dimentica i fallimenti. Qui li dimentica solo quando un'uscita che ci porta
    /// e' anche libera e la rotta e' scritta: se l'uscita che torna a portarci e' occupata la
    /// missione aspetta come sopra, con i fallimenti contati, e la prova successiva aspetta ancora
    /// che il freno la consenta (fino a 5 s).
    /// </para>
    /// <para>
    /// Un'uscita in <c>Error</c> (guasto) conta come un'uscita che non ci porta, non come
    /// un'uscita occupata (decisione del proprietario del 10/10, C2/C12): se le sole uscite che
    /// portano alla scelta sono in errore, la missione aspetta con il freno e la riga di avviso
    /// come quando nessuna uscita ci porta, e quando un'uscita torna pronta l'UDC esce da li' da
    /// sola, al primo giro che il freno consente. Prima l'uscita guasta contava come occupata, e la
    /// missione aspettava senza freno e senza riga, all'infinito. Lo stato si guarda solo
    /// sull'uscita, non sui dispositivi della rotta da li' in avanti.
    /// </para>
    /// <para>
    /// Per un magazzino o un gruppo di magazzini scelto la destinazione della missione e' il
    /// magazzino della porta prenotata dal commit del cambio (<c>MissionGateway</c>): l'UDC prosegue
    /// solo verso quel magazzino, l'unico dove ha la cella, e un'uscita che porta soltanto a un
    /// altro magazzino del gruppo non conta. Non si riprenota (decisione del proprietario del
    /// 10/10, C1/C13): niente gestionale dal ciclo del distributore.
    /// </para>
    /// <para>
    /// Il conto e' quello per missione del giro delle rotte (<c>Navigator.RiaccodaConAttesaCrescente</c>):
    /// se tutti e due lavorassero sulla stessa missione i fallimenti si sommerebbero, e una riuscita
    /// dell'uno dimenticherebbe quelli dell'altro, con le soglie dell'avviso saltate. Il conto
    /// condiviso e' un difetto latente, segnalato alla revisione finale della voce 40 (C14), e non
    /// e' riparato qui.
    /// </para>
    /// <para>
    /// La rotta si scrive con l'avvio contato sulla missione (<c>PathMission.TryBeginStepStart</c>):
    /// con un cambio di destinazione dell'operatore in corso non si scrive niente e si riprova al
    /// giro dopo, e mentre si scrive il cambio non comincia. Niente gestionale qui dentro: la sonda
    /// della rotta legge solo i dispositivi in memoria.
    /// </para>
    /// </remarks>
    private bool ProcessLinkedItemsTowardsTheOperatorsChoice(PathMission mission)
    {
        if (Navigator.RoutePathRetries.IsWaiting(mission.MissionId)) return false;
        if (!mission.TryBeginStepStart()) return false;

        try
        {
            var leadsThere = false;

            foreach (var exit in device.DestinationItemsLinked.OfType<AutomationDevice>())
            {
                // Un'uscita in Error non ci porta (C2/C12): vedi le note.
                if (exit.Status == DeviceStatusTypes.Error) continue;

                List<MissionStep> onward;
                if (PathMission.Reaches(exit.ItemGuid, mission.DestinationItemId))
                {
                    onward = [];
                }
                else
                {
                    onward = mission.ProbeRouteSteps(mission.DestinationItemId ?? Guid.Empty,
                        sourceId: exit.ItemGuid, avoidId: device.ItemGuid);
                    if (onward.Count == 0) continue;
                }

                leadsThere = true;

                if (exit.LuManager?.CanAcceptLu(mission) != true) continue;

                mission.MissionSteps =
                [
                    new MissionStep
                    {
                        Date = DateTime.Now,
                        FromNodeId = device.ItemGuid,
                        ToNodeId = exit.ItemGuid,
                        MissionId = mission.MissionId,
                        StepId = Guid.NewGuid()
                    },
                    .. onward
                ];

                Navigator.ForgetRouteRetries(mission.MissionId);
                return true;
            }

            if (!leadsThere)
            {
                var failure = Navigator.RoutePathRetries.RegisterFailure(mission.MissionId);
                Navigator.WriteLog(
                    $"Missione {mission.MissionId}: nessuna uscita del distributore circolare {device.ItemGuid} porta alla destinazione {mission.DestinationItemId} scelta dall'operatore: la missione aspetta con il freno e tiene la scelta (fallimento {failure})",
                    Navigator.IsABrakeThreshold(failure) ? LogEntryType.Warning : LogEntryType.Information);
            }

            return false;
        }
        finally
        {
            mission.EndStepStart();
        }
    }

    /// <summary>
    /// Processa le LU da ricevere da altri dispositivi
    /// </summary>
    /// <param name="lUsToReceive">Lista delle LU da ricevere</param>
    private async Task ProcessLUsToReceive(List<PathMission?> lUsToReceive)
    {
        // Un foreach e non due Where in fila: il secondo predicato ora e' asincrono, e un
        // predicato LINQ non lo puo' essere.
        foreach (var lu in lUsToReceive.OfType<PathMission>().Where(CanProcessLu))
        {
            if (await ProcessLuTransfer(lu))
                break;
        }
    }

    /// <summary>
    /// Processa il trasferimento di una LU verso il dispositivo corrente
    /// </summary>
    /// <param name="lu">La LU da trasferire</param>
    /// <returns>True se il trasferimento è andato a buon fine, false altrimenti</returns>
    private async Task<bool> ProcessLuTransfer(PathMission lu)
    {
        device.LUtoReceive = lu;

        device.RemoveDeviceReadyToTransferLu(lu.PositionItem);

        // Task.Delay e non Thread.Sleep: qui gira un thread del pool, condiviso da tutti i
        // dispositivi, ed e' il punto piu' battuto dello scambio fra elementi. Fermare il
        // thread per 200 ms fermava il lavoro di dispositivi che non c'entravano nulla.
        await Task.Delay(200);

        try
        {
            if (lu.PositionItem != null)
            {
                var itemGuid = lu.PositionItem.ItemGuid;
                var pathDevice = PathDevices.Instance[itemGuid];
                if (pathDevice != null)
                    StartReceivingLu(pathDevice);
            }
        }
        catch (Exception)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Processa i dispositivi pronti per il trasferimento senza LU assegnate
    /// </summary>
    private void ProcessReadyDevicesWithoutLu()
    {
        foreach (var item in device.DevicesReadyToTransferLu.Where(x =>
                     x.LoadedLu == null && x is { Status: DeviceStatusTypes.Ready, AcceptLuIfDestinationIsAvailable: true }))
            _ = item.ExecuteStep(500);
    }

    /// <summary>
    /// Esegue un passo singolo nel contesto di una sequenza di missione
    /// </summary>
    /// <param name="step">Lo step da eseguire</param>
    /// <param name="tempRunningStepId">L'ID temporaneo per il gruppo di esecuzione</param>
    /// <param name="versions">La versione della rotta di ogni missione contata, letta prima dello step</param>
    /// <returns>True se lo step è stato avviato con successo, false altrimenti</returns>
    /// <remarks>
    /// Il gettone del gruppo (il suo <c>RunningStepId</c> e il <c>GroupedMovementId</c> dello step e
    /// della missione) si scrive sotto il lucchetto della rotta, solo se la versione e' ancora quella
    /// letta prima dello step (<c>PathMission.TryTakeTheStepToken</c>). Fra la scelta dello step e questo
    /// punto ci sono le attese fra uno step e l'altro, e una ricostruzione poteva togliere lo step: il
    /// gruppo lo rimetteva in <c>MissionSteps</c>, accanto alla rotta nuova, e si metteva in movimento
    /// per lui (corsa 5 della ricognizione della voce 41, compito 6). Ora lo step non parte, e il
    /// gruppo non tocca ne' il gettone ne' lo stato.
    /// </remarks>
    private async Task<bool> ProcessSingleStep(MissionStep step, Guid tempRunningStepId,
        Dictionary<PathMission, long> versions)
    {
        if (step.SourceItem == null || step.DestinationItem == null || step.PathMission == null) return false;

        step.SourceItem.RunningStepId = Guid.Empty;

        if (!step.SourceItem.IsReadyToTransferLu || !step.DestinationItem.IsReadyToReceiveLu)
            return false;

        // La missione si rilegge dalle attive a ogni accesso: se non e' piu' quella contata all'inizio
        // del giro (tolta, o tolta e rimessa), lo step non parte.
        var mission = step.PathMission;
        if (!versions.TryGetValue(mission, out var seenRouteVersion)) return false;

        device.StepStartPauseForTesting?.Invoke();

        // Un secondo step della stessa missione nello stesso giro non arriva qui: la sua sorgente non ha
        // l'UDC a bordo (IsReadyToTransferLu qui sopra). Basta la versione letta prima del giro.
        if (!mission.TryTakeTheStepToken(seenRouteVersion, step, () =>
            {
                // Il gettone del gruppo si scrive dal valore, non rileggendo RunningStepId: finche'
                // nessuno step porta l'identificativo temporaneo il getter risponde Guid.Empty (voce
                // D166), e lo step, la missione e poi il gruppo stesso restavano col gettone vuoto
                // (revisione finale della voce 41). Prima lo step e la missione, poi il gruppo: il
                // setter annuncia il gruppo con il gettone gia' leggibile.
                step.GroupedMovementId = tempRunningStepId;
                mission.GroupedMovementId = tempRunningStepId;
                device.RunningStepId = tempRunningStepId;
            }))
        {
            // Come il movimento singolo che rinuncia, il gruppo riprova con la rotta nuova appena e' libero
            // (AutomationDevice.ExecuteAgainWhenFree, risposta 4 del proprietario dell'11/10).
            device.ExecuteAgainWhenFree();
            return false;
        }

        device.Status = DeviceStatusTypes.Moving;
        step.SourceItem.StartPutToMOVE_LU();

        // Questa attesa RESTA, ed e' l'unica delle quattro di D67 a restare. Le altre stavano fra
        // un'assegnazione di Status e il suo uso, quindi compensavano un'asincronia che D43 ha tolto.
        // Questa invece sta DOPO un invio al PLC e prima del ritorno: scandisce il dialogo con il
        // campo, non lo stato del motore. Toglierla e' una decisione da prendere guardando il PLC,
        // non leggendo questo codice. Si veda la voce D67 del registro.
        // Task.Delay e non Thread.Sleep: il thread e' del pool e serve anche agli altri
        // dispositivi. Per questo il chiamante scorre gli step con un ciclo esplicito e non con
        // un predicato LINQ, che non puo' attendere. Voce D72 del registro.
        await Task.Delay(100);

        return true;
    }


    /// <summary>
    /// Gestisce il processo di prelievo da una posizione di stoccaggio, aggiornando i contatori e il caricamento della LU
    /// </summary>
    /// <param name="luToTransfer">La missione di percorso che rappresenta la LU prelevata dallo stoccaggio</param>
    private async Task ProcessStoragePickup(PathMission luToTransfer)
    {
        await DbInfo.SetCellUnlockedForStoring(luToTransfer.PickUpDoorCode);
        device.CurrentPickUpStep++;
        await device.LoadLu(luToTransfer.MissionId, false);
        device.LUtoPick = null;
        device.Status = DeviceStatusTypes.Ready;
    }

    /// <summary>
    /// Gestisce un trasferimento di LU completato con successo, aggiornando gli stati dei dispositivi e registrando l'operazione
    /// </summary>
    /// <param name="executedStep">Lo step di missione completato con successo</param>
    /// <param name="luToTransfer">La LU che è stata trasferita</param>
    /// <param name="infoDescription">Descrizione informativa del trasferimento per il logging</param>
    /// <param name="destinationItem">Il dispositivo di destinazione che ha ricevuto la LU</param>
    private void ProcessSuccessfulTransfer(MissionStep executedStep, PathMission luToTransfer, string infoDescription, AutomationDevice destinationItem)
    {
        Navigator.RecordNavigatorLog(device, infoDescription, LogEntryType.Information, executedStep);

        // La destinazione resta Busy finche' la UDC non e' sui suoi libri: il passaggio a Ready sta
        // dentro ThreadLoadLu, dopo il caricamento. Qui c'era prima, e apriva una finestra in cui
        // la destinazione risultava libera e scarica insieme.
        destinationItem.StepExecutor?.ThreadLoadLu(luToTransfer.MissionId);

        if (luToTransfer == device.LUtoReceive)
            device.LUtoReceive = null;

        if (executedStep.SourceItem != null) executedStep.SourceItem.Status = DeviceStatusTypes.Ready;
    }

    /// <summary>
    /// Processa un trasferimento quando il dispositivo corrente è la destinazione (ToNode), gestendo diversi tipi di sorgente
    /// </summary>
    /// <param name="executedStep">Lo step di missione eseguito</param>
    /// <param name="luToTransfer">La LU da trasferire verso il dispositivo corrente</param>
    /// <param name="fromAsiItem">Dati ASI del dispositivo sorgente</param>
    /// <param name="toAsiItem">Dati ASI del dispositivo destinazione</param>
    /// <returns>Descrizione dell'operazione per il logging, o null se il dispositivo sorgente è null</returns>
    private async Task<string?> ProcessToNodeTransfer(MissionStep executedStep, PathMission luToTransfer, DataAsi fromAsiItem, DataAsi toAsiItem)
    {
        var luCode = luToTransfer.LuCode;
        string infoDescription;

        if (executedStep.SourceItem == null)
        {
            return null;
        }

        switch (executedStep.SourceItem.DeviceType)
        {
            case DeviceTypes.Storage:
                infoDescription =
                    $"Picked Up LU {luCode} to move from {fromAsiItem.Asi}.{fromAsiItem.Subitem}.{fromAsiItem.Partition} to {toAsiItem.Asi}.{toAsiItem.Subitem}.{toAsiItem.Partition}";
                await ProcessStoragePickup(luToTransfer);
                break;
            default:
                infoDescription =
                    $"Moved LU {luCode} from {fromAsiItem.Asi}.{fromAsiItem.Subitem}.{fromAsiItem.Partition} to {toAsiItem.Asi}.{toAsiItem.Subitem}.{toAsiItem.Partition}";
                break;
        }

        Navigator.RecordNavigatorLog(device, infoDescription, LogEntryType.Error, executedStep);

        return infoDescription;
    }
    /// <summary>
    /// Rimuove il dispositivo corrente dalla lista dei dispositivi pronti per il trasferimento del dispositivo specificato
    /// </summary>
    /// <param name="value">Il dispositivo da cui rimuovere il dispositivo corrente dalla lista di trasferimento</param>
    private void RemoveDevicesReadyToTransferLu(AutomationDevice? value)
    {
        if (value == null || !value.RemoveDeviceReadyToTransferLu(device)) return;

        if (value.LoadedLu == device.LUtoReceive)
            device.LUtoReceive = null;
    }

    /// <summary>
    /// Determina se è necessario verificare l'allocazione per una cella di stoccaggio specifica
    /// </summary>
    /// <remarks>
    /// Compito 16. Due delle quattro condizioni passavano dal gestionale caricato nel processo e ora
    /// passano dal confine:
    /// <list type="bullet">
    /// <item><description>
    /// <c>CelleMagazzino.NumUdcInCella(idCella) &gt; 0</c> diventa «il contenuto della cella non e'
    /// vuoto» (<see cref="IWarehouse.GetCellContentAsync"/>): la stessa lettura delle posizioni non
    /// assegnate, di cui qui si guardava solo se ce ne fosse almeno una. Le due query non sono pero'
    /// identiche - il conteggio non passava da <c>CELLE_AREE</c>, questa lettura si' - e la
    /// differenza e' riportata nel rapporto del compito.
    /// </description></item>
    /// <item><description>
    /// <c>CelleMagazzino.CellaAreaBloccataCarico(idCella)</c> (<c>BLOCCA &gt;= 2</c> sulla stessa
    /// riga di <c>CELLE_AREE</c>) diventa <see cref="CellLockBlocksStoring"/> sul
    /// <see cref="CellDescription.Locked"/> gia' tipizzato. La soglia era stata migrata com'era;
    /// il 2026-09-09 e' stata corretta e rinominata: vedi la nota su quel metodo.
    /// </description></item>
    /// </list>
    /// La terza condizione (<see cref="DbInfo.GetCellLockedForStoring"/>) e' un dato del WCS e resta
    /// dov'era. Il magazzino si chiede una volta sola, in testa: prima si leggeva
    /// <c>Navigator.WmsBusinessArea.CelleMagazzino</c> con lo stesso anticipo, e la stessa assenza
    /// del gestionale interrompeva comunque il metodo.
    /// </remarks>
    /// <param name="idCell">L'identificativo della cella da verificare</param>
    /// <returns>True se è necessaria la verifica dell'allocazione, false altrimenti</returns>
    private async Task<bool> ShouldVerifyAllocation(decimal idCell)
    {
        var warehouse = Navigator.RequireWarehouse($"ShouldVerifyAllocation sulla cella {idCell}");
        return !LUsIsStorageHomogeneous(device.LUsLoaded) ||
               (await warehouse.GetCellContentAsync(idCell)).Count > 0 ||
               await DbInfo.GetCellLockedForStoring(idCell) ||
               CellLockBlocksStoring((await warehouse.DescribeCellAsync(idCell)).Locked);
    }

    /// <summary>
    /// Il blocco che l'operatore ha messo sulla cella impedisce il deposito: e' cosi' per
    /// <see cref="CellLockState.Full"/> e per <see cref="CellLockState.Loading"/>, mentre una cella
    /// bloccata solo al prelievo (<see cref="CellLockState.Unloading"/>) resta buona per depositare.
    /// E' la stessa regola di <see cref="AutomationDeviceStorageManager.BlocksStoring"/>, l'altra
    /// guardia di deposito del motore.
    /// <para>
    /// <b>Decisione del 2026-09-09.</b> Il compito 16 aveva migrato com'era la soglia di
    /// <c>CelleMagazzino.CellaAreaBloccataCarico</c> (<c>BLOCCA &gt;= 2</c> su <c>CELLE_AREE</c>,
    /// cioe' <see cref="CellLockState.Unloading"/> e <see cref="CellLockState.Full"/>), lasciando
    /// aperta la discrepanza con <c>BlocksStoring</c>. Un'indagine ha accertato che
    /// <c>BLOCCA = 1</c> blocca l'<b>ingresso</b> e <c>BLOCCA = 2</c> l'<b>uscita</b>: lo dicono la
    /// descrizione della colonna nel database, i tre pulsanti del terminale mobile
    /// («Lock Inbound» = 1, «Lock Outbound» = 2) e sette letture concordi fra T-SQL e C#. La soglia
    /// ereditata calcolava dunque «bloccata in uscita» pur decidendo di un deposito, ed e' stata
    /// allineata; il nome, che diceva l'altra cosa, e' cambiato con lei.
    /// </para>
    /// <c>internal</c> e non <c>private</c> perche' un test la raggiunga senza costruire un
    /// dispositivo intero, sullo stesso schema di <see cref="AutomationDeviceStorageManager.BlocksStoring"/>.
    /// </summary>
    internal static bool CellLockBlocksStoring(CellLockState lockState) =>
        lockState is CellLockState.Full or CellLockState.Loading;


    /// <summary>
    /// Avvia un'operazione GetFrom per trasferire una LU da un dispositivo specificato al dispositivo corrente
    /// </summary>
    /// <param name="fromItem">Il dispositivo sorgente da cui prelevare la LU</param>
    /// <remarks>
    /// <b>Sorgente e destinazione si risolvono qui, una volta sola, prima della prenotazione.</b>
    /// <c>MissionStep.SourceItem</c> e <c>MissionStep.DestinationItem</c> non sono campi: rileggono
    /// <c>PathDevices</c> a ogni accesso, e un dispositivo rimosso fra un accesso e l'altro le fa
    /// rispondere <c>null</c>. Finche' le guardie stavano dentro <c>ExecuteGetFromMovement</c>,
    /// cioe' <b>dopo</b> che <see cref="ConfigureGetFromStep"/> aveva preso il posto sulla
    /// destinazione, quel caso lasciava una prenotazione presa per uno step che nessuno avrebbe
    /// eseguito — e senza <c>RunningStepId</c> addosso a nessuno, quindi senza nessuna via di
    /// restituzione. Risolvere una volta toglie la finestra invece di sorvegliarla.
    /// </remarks>
    private void StartGetFromMOVE_LU(AutomationDevice? fromItem)
    {
        var mission = fromItem?.LoadedLu;

        if (fromItem == null || mission == null) return;

        // Con un cambio di destinazione dell'operatore in corso lo step non parte, e l'avvio si
        // conta fino al gettone (PathMission.TryBeginStepStart). Questo GetFrom gira sotto il
        // semaforo di questo dispositivo, non sotto quello della sorgente, e il commit del cambio non
        // prende piu' nessun semaforo (voce 41, compito 5): lo ferma solo il conto. Lo step si legge
        // dopo essersi contati: letto prima, un cambio cominciato e finito nel mezzo l'avrebbe gia'
        // tolto.
        if (!mission.TryBeginStepStart())
        {
            Navigator.WriteLog(
                $"StartGetFromMOVE_LU {device.ItemGuid}: la missione {mission.MissionId} sta cambiando destinazione, il movimento non parte",
                LogEntryType.Information);
            return;
        }

        try
        {
            if (!ReferenceEquals(fromItem.LoadedLu, mission)) return;

            // La versione della rotta si legge prima dello step, come nel movimento singolo: il gettone
            // si scrive solo se e' ancora quella (PathMission.TryTakeTheStepToken, corsa 5 della
            // ricognizione della voce 41, compito 6).
            var seenRouteVersion = mission.RouteVersion;

            var stepToDo = mission.StepToDo;

            if (!IsValidGetFromStep(stepToDo) || stepToDo == null) return;

            var sourceItem = stepToDo.SourceItem;
            var destinationItem = stepToDo.DestinationItem;

            if (sourceItem == null || destinationItem == null) return;

            if (!ConfigureGetFromStep(stepToDo, sourceItem, destinationItem)) return;

            // Da qui in giu' il dispositivo sorgente e' uno solo, quello di PathDevices: si veda il
            // blocco di ExecuteGetFromMovement. fromItem serve fino a questa riga per trovare lo step.
            ExecuteGetFromMovement(mission, stepToDo, sourceItem, destinationItem, seenRouteVersion);
        }
        finally
        {
            mission.EndStepStart();
        }
    }
    
    /// <summary>
    /// Gestisce il caricamento di una LU in un dispositivo, utilizzando un thread separato per la gestione asincrona
    /// </summary>
    /// <param name="missionId">L'ID della missione da caricare</param>
    /// <param name="activateExecuteStep">True per attivare immediatamente l'esecuzione dello step, false altrimenti</param>
    /// <remarks>
    /// <para>
    /// <b>Il passaggio a <c>Ready</c> avviene qui, dopo il caricamento, e non prima.</b> Prima la
    /// destinazione veniva dichiarata <c>Ready</c> e <i>poi</i> le si mandava il caricamento su un
    /// altro thread: fra le due cose restava libera <b>e scarica</b>, perche' la UDC entra sui suoi
    /// libri solo quando <c>LoadLu</c> scrive <c>PositionItemId</c>. In quella finestra un terzo
    /// elemento a monte superava <c>CanAcceptLu</c> e le mandava addosso un secondo bancale. La
    /// finestra dura quanto la schedulazione di un <c>Task.Run</c> — di norma niente, ma e' lo
    /// stesso pool che porta avanti i cicli del motore, e sotto carico si allunga.
    /// </para>
    /// <para>
    /// Il risveglio va rifatto qui per la stessa ragione: durante il caricamento il dispositivo e'
    /// <c>Busy</c>, quindi l'<c>ExecuteStep</c> che <c>LoadLu</c> fa partire viene rifiutato — un
    /// elemento impegnato non accetta comandi. Senza questa chiamata il bancale resterebbe fermo
    /// fino al prossimo evento del campo.
    /// </para>
    /// </remarks>
    private void ThreadLoadLu(Guid missionId, bool activateExecuteStep = true)
    {
        try
        {
            Task.Run(async () =>
            {
                try
                {
                    await device.LoadLu(missionId, activateExecuteStep);
                }
                catch (Exception ex)
                {
                    device.LogError(ex);
                }
                finally
                {
                    // Nel finally, non dopo il caricamento: se LoadLu solleva, un dispositivo
                    // lasciato Busy non tornerebbe mai libero e il tratto si fermerebbe li'. Con la
                    // UDC non caricata Ready e' anche lo stato onesto -- non sta facendo niente -- e
                    // l'errore e' comunque finito nel log.
                    //
                    // Solo la Busy messa da questo trasferimento: se nel frattempo il dispositivo e'
                    // andato in errore o offline, quello stato vale piu' di questo.
                    if (device.Status == DeviceStatusTypes.Busy)
                        device.Status = DeviceStatusTypes.Ready;

                    _ = device.ExecuteStep();
                }
            }, device.CancelDisposingToken);
        }
        catch (Exception ex)
        {
            device.LogError(ex);
        }
    }

    #endregion Private Methods

}
