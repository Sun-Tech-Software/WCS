using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;

namespace Wcs.Engine.ORM;

/// <summary>
/// The local queue of the cells to give back to the warehouse (item 54), with the writes serialized
/// like the state of the WCS. The row is written by the commit of the operator's change, with the
/// mission's row (<c>Items.PathMission.CommitRoute</c>); the dispatcher and the tests read and delete it here.
/// </summary>
internal static class CellGiveBackOutbox
{
    public static Task<List<PendingCellGiveBack>> PendingAsync(int take) =>
        ExtractionQueue.ReadAsync(context => PendingCellGiveBackStore.OldestAsync(context, take),
            new List<PendingCellGiveBack>());

    public static Task RecordFailureAsync(long id, Exception failure, bool park) =>
        ExtractionQueue.WriteAsync(context => PendingCellGiveBackStore.RecordFailureAsync(context, id, failure.Message, park));

    public static Task<bool> ExistsAsync(long id) =>
        ExtractionQueue.ReadAsync(context => PendingCellGiveBackStore.ExistsAsync(context, id), false);

    /// <summary>
    /// The rows of the load unit for a cell just reserved again for it (item 54, fix round 1 of task 3):
    /// out of the queue, so no later turn gives back the cell a mission now goes to. How many went.
    /// </summary>
    public static async Task<int> DeleteForCellAsync(decimal loadUnitId, decimal cellId, string doorCode)
    {
        var deleted = 0;
        await ExtractionQueue.WriteAsync(async context =>
            deleted = await PendingCellGiveBackStore.DeleteForCellAsync(context, loadUnitId, cellId, doorCode));
        return deleted;
    }

    public static Task DeleteAsync(long id) =>
        ExtractionQueue.WriteAsync(context => PendingCellGiveBackStore.DeleteAsync(context, id));
}
