using Log.Interface;
using System.Text.Json;
using Wcs.Engine.Collections;
using Wcs.Engine.ORM;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Wcs.Wms.Contracts;
using static Wcs.Common.Enums;

namespace Wcs.Engine;

/// <summary>
/// Completa nel motore una transizione che il WMS ha già accettato. Ogni controllo è ripetibile:
/// una replica dopo una risposta persa non deve avanzare né rimuovere due volte la missione.
/// </summary>
internal static class WarehouseTransitionCompletion
{
    public static Task<bool> CompleteAsync(PendingWarehouseTransition transition) => transition.Kind switch
    {
        WarehouseTransitionKind.Release => CompleteReleaseAsync(transition),
        WarehouseTransitionKind.Allocate => CompleteAllocateAsync(transition),
        _ => throw new InvalidOperationException($"Unknown warehouse transition kind {transition.Kind}.")
    };

    public static async Task<bool> CompleteReleaseAsync(PendingWarehouseTransition transition)
    {
        var mission = ActiveMissions.Instance[transition.MissionId];
        if (mission == null)
            return !await ExtractionQueue.ReadAsync(
                context => MissionStore.ExistsAsync(context, transition.MissionId),
                false);

        // Questa lettura precede la rimozione, che azzera PositionItemId. È anche la guardia che
        // impedisce a una missione già spostata altrove di rendere Ready il dispositivo sbagliato.
        var device = mission.PositionItem;
        var belongsToDevice = device != null && ReferenceEquals(device.LoadedLu, mission);

        // La pulizia della cella può attraversare di nuovo il confine WMS. Va fatta prima di
        // mutare lo stato locale: se fallisce, il retry ritrova la missione intatta.
        await Navigator.ResetCellOperationsStatus(transition.LoadUnitId);

        if (belongsToDevice)
        {
            if (device!.RunningStepId != Guid.Empty)
            {
                if (mission.StepToDo?.DestinationItem != null)
                    mission.StepToDo.DestinationItem.Status = DeviceStatusTypes.Ready;
                device.RunningStepId = Guid.Empty;
            }
        }

        var removed = await ActiveMissions.Instance.Remove(mission);
        if (!removed) return false;

        // ActiveMissions conserva per compatibilità una cancellazione fire-and-forget. Qui la
        // ripetiamo e la attendiamo sulla stessa coda: l'outbox non può sparire finché la riga
        // missione non è realmente assente dal database operativo.
        await ExtractionQueue.WriteAsync(context => MissionStore.DeleteAsync(context, mission.MissionId));

        if (mission.LuId > 0)
            Navigator.WriteLog(
                $"DEBUG INFO: Cancellazione MISSION_LU da UnloadLU UDC: {mission.LuId} - {mission.LuCode} Date: {DateTime.Now:dd/MM/yyyy HH:mm:ss}",
                LogEntryType.Information);

        if (belongsToDevice)
        {
            device!.Status = DeviceStatusTypes.Ready;
            if (device.AutoExecuteStep)
                _ = device.ExecuteStep();
        }

        return true;
    }

    public static async Task<bool> CompleteAllocateAsync(PendingWarehouseTransition transition)
    {
        var request = JsonSerializer.Deserialize<AllocateLoadUnitRequest>(transition.PayloadJson)
            ?? throw new JsonException($"Empty payload for warehouse transition {transition.OperationId}.");
        var mission = ActiveMissions.Instance[transition.MissionId];
        if (mission == null)
            return !await ExtractionQueue.ReadAsync(
                context => MissionStore.ExistsAsync(context, transition.MissionId),
                false);

        // Un Allocate con porta è il deposito terminale. Se la missione nel frattempo indica un
        // altro passo, non la si chiude sulla base di una risposta ormai stantia.
        var storage = mission.PositionItem;
        if (string.IsNullOrEmpty(request.StorageDoorCode)) return true;
        if (storage?.DeviceType != DeviceTypes.Storage ||
            mission.PositionItemId != mission.DestinationItemId ||
            mission.LuId != request.LoadUnitId ||
            !string.Equals(mission.StorageDoorCode, request.StorageDoorCode, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Allocate {transition.OperationId} succeeded remotely, but mission {mission.MissionId} " +
                "is no longer on the matching storage step.");
        if (mission.StepInFlight() != null) return false;

        var warehouse = Navigator.RequireWarehouse(
            $"completamento Allocate {transition.OperationId} su {request.LoadUnitId}");
        var reservation = await warehouse.GetStorageReservationAsync(mission.MissionId);
        if (reservation?.CellId == request.CellId)
            await warehouse.ReleaseStorageReservationAsync(mission.MissionId);

        // Il blocco normale protegge la cella fino alla conferma. Quello applicato per una quota
        // fisica oltre la profondità è invece una quarantena e deve sopravvivere al completamento.
        var preserveQuarantine = false;
        if (Navigator.LockCellOnStoringError && request.DetectedDepth > 0)
        {
            var cell = (await warehouse.DescribeCellsByDoorAsync(request.StorageDoorCode)).FirstOrDefault();
            preserveQuarantine = cell != null && request.DetectedDepth > cell.Depth;
        }

        if (!preserveQuarantine)
            await DbInfo.SetCellUnlockedForStoring(request.StorageDoorCode);

        var removed = await ActiveMissions.Instance.Remove(mission, releaseStorageReservation: false);
        if (!removed) return false;

        await ExtractionQueue.WriteAsync(context => MissionStore.DeleteAsync(context, mission.MissionId));
        Navigator.RecordNavigatorLog(storage,
            $"Stored LU {mission.LuCode} to {mission.StorageDoorCode}",
            LogEntryType.Information);

        if (!preserveQuarantine && storage.Status == DeviceStatusTypes.Warning)
            storage.Status = DeviceStatusTypes.Ready;

        return true;
    }

    public static void MarkPending(PendingWarehouseTransition transition, Exception failure, int attempts)
    {
        var mission = ActiveMissions.Instance[transition.MissionId];
        if (mission?.PositionItem is { } device && ReferenceEquals(device.LoadedLu, mission))
            device.Status = DeviceStatusTypes.Warning;

        if (attempts == 1 || attempts % 10 == 0)
            Navigator.WriteLog(
                $"Transizione WMS {transition.OperationId} ({transition.Kind}, UDC {transition.LoadUnitId}) " +
                $"ancora pendente dopo {attempts} tentativo/i: {failure.Message}",
                LogEntryType.Warning);
    }
}
