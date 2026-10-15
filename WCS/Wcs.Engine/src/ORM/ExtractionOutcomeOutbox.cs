using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Wcs.Wms.Contracts;

namespace Wcs.Engine.ORM;

/// <summary>L'outbox locale degli esiti di estrazione, con scritture serializzate come lo stato WCS.</summary>
internal static class ExtractionOutcomeOutbox
{
    /// <summary>Registra l'esito prima che la riga della coda possa essere rimossa.</summary>
    public static async Task<bool> EnqueueAsync(decimal extractionQueueId, ExtractionRowStatus status,
        DateTimeOffset? createdAtUtc = null)
    {
        if (Navigator.Store == null)
            throw new InvalidOperationException("The WCS operational store is not available.");

        var persisted = false;
        await ExtractionQueue.WriteAsync(async context =>
            persisted = await PendingExtractionOutcomeStore.EnqueueFromQueueAsync(context, (long)extractionQueueId,
                status, Navigator.WorkstationId, createdAtUtc ?? DateTimeOffset.UtcNow));
        return persisted;
    }

    public static Task<List<PendingExtractionOutcome>> PendingAsync(int take) =>
        ExtractionQueue.ReadAsync(context => PendingExtractionOutcomeStore.OldestAsync(context, take),
            new List<PendingExtractionOutcome>());

    public static Task DeleteAsync(IReadOnlyCollection<long> ids) =>
        ids.Count == 0 ? Task.CompletedTask : ExtractionQueue.WriteAsync(context =>
            PendingExtractionOutcomeStore.DeleteAsync(context, ids));
}
