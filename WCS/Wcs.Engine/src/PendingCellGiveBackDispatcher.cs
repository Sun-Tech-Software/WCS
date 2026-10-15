using Log.Interface;
using Wcs.Engine.Collections;
using Wcs.Engine.ORM;
using Wcs.Engine.Persistence.Entities;
using Wcs.Wms.Contracts;

namespace Wcs.Engine;

/// <summary>
/// Gives back to the warehouse the storage cells the WCS no longer goes to (item 54, answer 1 of the
/// owner of 13/10): the cell of a change of cell or of a defragmentation that the operator redirected,
/// registered in the local queue by the commit of the change (<see cref="PendingCellGiveBack"/>). The
/// master loop calls it on every turn, like the dispatcher of the fact of item 45. The warehouse
/// releases the cell only if it still holds it for that load unit
/// (<see cref="IWarehouse.ReleaseLoadUnitCellAsync"/>); after its answer, whatever the outcome, the
/// door of the cell is freed in the WCS's door map if no operation still holds it
/// (<c>DbInfo.ResetCellOperationsStatus</c>), and the row leaves the queue.
/// </summary>
/// <remarks>
/// <para>
/// <b>Retried until it succeeds.</b> A transport failure, a timeout, a 5xx, the warehouse not wired,
/// or a failure freeing the door leave the row where it is, and the next turn tries again under the
/// brake; a permanent rejection (a 4xx) parks it with the reason. Both steps are idempotent: a second
/// release of a cell no longer held answers <see cref="LoadUnitCellReleaseOutcome.NotHeld"/>.
/// </para>
/// <para>
/// <b>A cell that is the mission's again is not given back.</b> If an active mission of the same load
/// unit goes to that cell again (the operator sent it back to the same storage and the warehouse gave
/// it the same cell), the row is dropped without calling the warehouse: the cell is held for a live
/// mission, whose own life releases it. The look reads the engine's collections and the WCS's door
/// map, without locks, before the call.
/// </para>
/// <para>
/// <b>Ordered against the reservations of the gateway</b> (fix round 1): the look and the release are
/// one step under the claim of the load unit (<see cref="LoadUnitCellClaims"/>), which the gateway's
/// storage flow holds from before its reservation until its commit has written the new door and has
/// taken the rows of the reserved cell out of the queue. A load unit claimed is skipped until the next
/// turn, without waiting; a row is read again under the claim, because the commit may have taken it
/// out after the batch was read. So a release by load unit and cell never meets a reservation of the
/// same cell that the gateway has taken and not yet written.
/// </para>
/// <para>
/// No lock is held while the warehouse answers: only the gate of this dispatcher, without waiting, so
/// two turns do not send the same row together.
/// </para>
/// </remarks>
internal sealed class PendingCellGiveBackDispatcher
{
    private static readonly Guid RetryKey = new("c3e1a054-2b6d-4f0e-8a54-0d3c5e7f9054");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly RetryBackoff _retry;
    private readonly int _batchSize;

    public PendingCellGiveBackDispatcher(TimeProvider? time = null, int batchSize = 100)
    {
        _retry = new RetryBackoff(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30), time);
        _batchSize = batchSize;
    }

    /// <summary>
    /// Tries a delivery without blocking whoever finds one already running. Does not raise: a failure
    /// leaves the row in the queue for the next turn.
    /// </summary>
    public async Task TryDispatchAsync()
    {
        if (_retry.IsWaiting(RetryKey)) return;
        if (!await _gate.WaitAsync(0)) return;

        PendingCellGiveBack? current = null;
        try
        {
            if (_retry.IsWaiting(RetryKey)) return;

            foreach (var giveBack in await CellGiveBackOutbox.PendingAsync(_batchSize))
            {
                // The claim of the load unit (see the remarks): a reservation for it under way waits for
                // the next turn, and none starts until the row is gone.
                using var claim = LoadUnitCellClaims.TryEnter(giveBack.LoadUnitId);
                if (claim == null) continue;

                // Taken out after the batch was read, by a reservation of its cell for the load unit.
                if (!await CellGiveBackOutbox.ExistsAsync(giveBack.Id)) continue;

                if (await HeldAgainByAMissionAsync(giveBack) is { } mission)
                {
                    await CellGiveBackOutbox.DeleteAsync(giveBack.Id);
                    Navigator.WriteLog(
                        $"Missione {giveBack.MissionId}, UDC {giveBack.LoadUnitId:0}: la cella {giveBack.CellId:0} (porta {giveBack.DoorCode}) " +
                        $"non si restituisce al gestionale, e' di nuovo la cella della missione {mission.MissionId}",
                        LogEntryType.Information);
                    continue;
                }

                current = giveBack;
                LoadUnitCellReleaseOutcome outcome;
                try
                {
                    outcome = await Navigator.RequireWarehouse($"restituzione della cella {giveBack.CellId:0} della missione {giveBack.MissionId}")
                        .ReleaseLoadUnitCellAsync(RequestFor(giveBack));
                }
                catch (WarehouseUnavailableException rejected) when (rejected.IsPermanentRejection)
                {
                    await CellGiveBackOutbox.RecordFailureAsync(giveBack.Id, rejected, park: true);
                    Navigator.WriteLog(
                        $"Missione {giveBack.MissionId}, UDC {giveBack.LoadUnitId:0}: la restituzione della cella {giveBack.CellId:0} " +
                        $"(porta {giveBack.DoorCode}, operazione {giveBack.OperationId}) e' stata RIFIUTATA DEFINITIVAMENTE dal gestionale " +
                        $"e resta parcheggiata nella coda del WCS: non verra' ritentata, e la cella va liberata a mano. Motivo: {rejected.Message}",
                        LogEntryType.Error);
                    current = null;
                    continue;
                }

                // The door of the WCS's door map, freed if nothing holds the cell any more: after the
                // warehouse's answer, because it asks the warehouse whether the cell is still busy.
                await DbInfo.ResetCellOperationsStatus(giveBack.CellId);

                await CellGiveBackOutbox.DeleteAsync(giveBack.Id);
                current = null;

                Navigator.WriteLog(
                    outcome == LoadUnitCellReleaseOutcome.Released
                        ? $"Missione {giveBack.MissionId}, UDC {giveBack.LoadUnitId:0}: la cella {giveBack.CellId:0} (porta {giveBack.DoorCode}) e' tornata al gestionale"
                        : $"Missione {giveBack.MissionId}, UDC {giveBack.LoadUnitId:0}: la cella {giveBack.CellId:0} (porta {giveBack.DoorCode}) il gestionale non la teneva piu' per l'UDC, e non ha toccato niente",
                    LogEntryType.Information);
            }

            _retry.Forget(RetryKey);
        }
        catch (Exception ex)
        {
            var attempts = _retry.RegisterFailure(RetryKey);
            try
            {
                if (current != null)
                    await CellGiveBackOutbox.RecordFailureAsync(current.Id, ex, park: false);
            }
            catch
            {
                // The count of attempts is diagnostics: the row stays in the queue anyway.
            }

            if (attempts == 1 || attempts % 10 == 0)
            {
                try
                {
                    Navigator.ReportError(new InvalidOperationException(
                        $"Cell give-back dispatch failed after {attempts} attempts.", ex));
                }
                catch
                {
                    // Diagnostics do not turn an error already handled into a failure of the caller.
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Clears only the brake in memory: the durable rows are never touched.</summary>
    public void Reset() => _retry.Clear();

    /// <summary>
    /// An active mission of the row's load unit that goes to the row's cell again (see the remarks), or null.
    /// </summary>
    private static async Task<Items.PathMission?> HeldAgainByAMissionAsync(PendingCellGiveBack giveBack)
    {
        foreach (var mission in ActiveMissions.Instance.Where(m => m != null && m.LuId == giveBack.LoadUnitId))
        {
            if (string.IsNullOrEmpty(mission.StorageDoorCode)) continue;
            if (await DbInfo.GetCellId(mission.StorageDoorCode) == giveBack.CellId) return mission;
        }

        return null;
    }

    private static ReleaseLoadUnitCellRequest RequestFor(PendingCellGiveBack giveBack) =>
        new(giveBack.OperationId, giveBack.MissionId, giveBack.LoadUnitId, giveBack.CellId, giveBack.UserId, giveBack.Reason);
}
