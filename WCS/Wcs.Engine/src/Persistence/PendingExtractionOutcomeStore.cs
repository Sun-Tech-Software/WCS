using Microsoft.EntityFrameworkCore;
using Wcs.Engine.Persistence.Entities;
using Wcs.Wms.Contracts;

namespace Wcs.Engine.Persistence;

/// <summary>Le operazioni locali dell'outbox degli esiti di estrazione diretti al WMS.</summary>
internal static class PendingExtractionOutcomeStore
{
    /// <summary>
    /// Registra l'esito della riga WMS indicata dalla coda. La seconda registrazione della stessa
    /// riga e' gia' durevole: non crea un duplicato e non sposta la data originale.
    /// </summary>
    public static async Task<bool> EnqueueFromQueueAsync(WcsDbContext context, long extractionQueueId,
        ExtractionRowStatus status, string workstationId, DateTimeOffset createdAtUtc)
    {
        var queueRow = await context.ExtractionQueue.SingleOrDefaultAsync(row => row.Id == extractionQueueId);
        if (queueRow?.ExtractionRowId is not > 0) return false;

        var extractionRowId = queueRow.ExtractionRowId.Value;
        if (context.PendingExtractionOutcomes.Local.Any(outcome => outcome.ExtractionRowId == extractionRowId) ||
            await context.PendingExtractionOutcomes.AnyAsync(outcome => outcome.ExtractionRowId == extractionRowId))
            return true;

        context.PendingExtractionOutcomes.Add(new PendingExtractionOutcome
        {
            ExtractionRowId = extractionRowId,
            Status = status,
            WorkstationId = workstationId,
            CreatedAtUtc = createdAtUtc
        });
        return true;
    }

    public static Task<List<PendingExtractionOutcome>> OldestAsync(WcsDbContext context, int take) =>
        context.PendingExtractionOutcomes.AsNoTracking().OrderBy(outcome => outcome.Id).Take(take).ToListAsync();

    public static async Task DeleteAsync(WcsDbContext context, IReadOnlyCollection<long> ids)
    {
        if (ids.Count == 0) return;

        var outcomes = await context.PendingExtractionOutcomes.Where(outcome => ids.Contains(outcome.Id)).ToListAsync();
        context.PendingExtractionOutcomes.RemoveRange(outcomes);
    }
}
