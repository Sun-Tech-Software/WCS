using Microsoft.EntityFrameworkCore;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;

namespace Wcs.Engine.Api;

public static class ExtractionQueueQueries
{
    public static Task<List<ExtractionQueueItem>> ListAsync(bool? processed, decimal? loadUnitId, int take) =>
        StoreAccess.ReadAsync(c =>
        {
            var query = c.ExtractionQueue.AsNoTracking().AsQueryable();
            if (processed != null) query = query.Where(x => x.Processed == processed);
            if (loadUnitId != null) query = query.Where(x => x.LoadUnitId == loadUnitId);
            return query.OrderBy(x => x.Priority).ThenBy(x => x.Id).Take(Math.Clamp(take, 1, 1000)).ToListAsync();
        });

    public static Task<ExtractionQueueItem?> FindAsync(long id) =>
        StoreAccess.ReadAsync(c => c.ExtractionQueue.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id));

    /// <summary>
    /// La UDC e' in coda? Con <paramref name="excludeRow"/> (l'<c>ExtractionRowId</c> della riga
    /// corrente) risponde «e' in un'altra estrazione?», sulle sole righe nate da un'estrazione per
    /// unita' di carico. <paramref name="excludeAssignmentId"/> fa la stessa domanda sulle righe
    /// nate da un'assegnazione di nota (compito 14), indipendente dal primo.
    /// </summary>
    public static Task<bool> ContainsLoadUnitAsync(decimal loadUnitId, decimal? excludeRow = null, decimal? excludeAssignmentId = null) =>
        StoreAccess.ReadAsync(c => ExtractionQueueStore.AnyByLoadUnitAsync(c, loadUnitId, excludeRow, excludeAssignmentId));

    public static Task<bool> ContainsCellAsync(decimal cellId) =>
        StoreAccess.ReadAsync(c => ExtractionQueueStore.AnyByCellAsync(c, cellId));

    /// <summary>Le celle con una riga non ancora lavorata: quello che getDtTIPO_RICERCA_CELLE escludeva con NOT EXISTS.</summary>
    public static Task<List<decimal>> CellsAsync() =>
        StoreAccess.ReadAsync(c => c.ExtractionQueue.AsNoTracking().Where(x => !x.Processed && x.CellId != null)
            .Select(x => x.CellId!.Value).Distinct().ToListAsync());

    public static Task<List<decimal>> LoadUnitsAsync() =>
        StoreAccess.ReadAsync(c => c.ExtractionQueue.AsNoTracking().Where(x => !x.Processed)
            .Select(x => x.LoadUnitId).Distinct().ToListAsync());

    public static async Task<bool> SetPriorityAsync(long id, int priority)
    {
        var found = false;
        await StoreAccess.WriteAsync(async context =>
        {
            var row = await context.ExtractionQueue.SingleOrDefaultAsync(x => x.Id == id);
            if (row == null) return;
            found = true;
            row.Priority = priority;
            context.Deferred.Add(() => Navigator.EventSink?.ExtractionQueueChanged(row, EngineQueueChange.Updated));
        });
        return found;
    }
}
