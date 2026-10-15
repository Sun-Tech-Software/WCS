using Microsoft.EntityFrameworkCore;
using Wcs.Engine.Persistence.Entities;

namespace Wcs.Engine.Persistence;

/// <summary>The operations on the same context for the queue of the cells to give back (item 54).</summary>
internal static class PendingCellGiveBackStore
{
    /// <summary>
    /// Adds the row to the context without saving: the caller writes it in the same transaction as the
    /// mission's row (<c>Items.PathMission.CommitRoute</c>).
    /// </summary>
    public static void Add(WcsDbContext context, PendingCellGiveBack giveBack) =>
        context.PendingCellGiveBacks.Add(giveBack);

    /// <summary>The rows to deliver, in order of arrival: not the parked ones.</summary>
    public static Task<List<PendingCellGiveBack>> OldestAsync(WcsDbContext context, int take) =>
        context.PendingCellGiveBacks.AsNoTracking()
            .Where(row => !row.Parked)
            .OrderBy(row => row.Id)
            .Take(take)
            .ToListAsync();

    public static async Task RecordFailureAsync(WcsDbContext context, long id, string error, bool park)
    {
        var row = await context.PendingCellGiveBacks.SingleOrDefaultAsync(r => r.Id == id);
        if (row == null) return;

        row.AttemptCount++;
        row.LastError = error;
        row.Parked = park;
    }

    /// <summary>Whether the row is still in the queue (parked or not).</summary>
    public static Task<bool> ExistsAsync(WcsDbContext context, long id) =>
        context.PendingCellGiveBacks.AnyAsync(row => row.Id == id);

    /// <summary>
    /// Takes out of the queue every row of the load unit for the cell (by its id or its door), parked
    /// or not, and says how many: the cell has just been reserved again for that load unit.
    /// </summary>
    public static async Task<int> DeleteForCellAsync(WcsDbContext context, decimal loadUnitId, decimal cellId, string doorCode)
    {
        var rows = await context.PendingCellGiveBacks
            .Where(row => row.LoadUnitId == loadUnitId && (row.CellId == cellId || row.DoorCode == doorCode))
            .ToListAsync();
        context.PendingCellGiveBacks.RemoveRange(rows);
        return rows.Count;
    }

    public static async Task DeleteAsync(WcsDbContext context, long id)
    {
        var row = await context.PendingCellGiveBacks.SingleOrDefaultAsync(r => r.Id == id);
        if (row != null) context.PendingCellGiveBacks.Remove(row);
    }
}
