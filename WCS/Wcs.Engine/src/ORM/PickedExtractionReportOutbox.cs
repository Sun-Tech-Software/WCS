using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;

namespace Wcs.Engine.ORM;

/// <summary>
/// La coda locale del fatto «l'estrazione e' diventata un deposito o un cambio cella» (voce 45), con
/// le scritture serializzate come lo stato del WCS. La riga la scrive il commit del cambio, insieme
/// alla riga della missione (<c>Items.PathMission.BecomeADepositOfAPickedExtraction</c>, compito 3),
/// o il cambio cella, insieme alla cancellazione della riga di coda
/// (<see cref="LeaveTheExtractionQueueAsync"/>, compito 4); qui la leggono e la tolgono il
/// dispatcher e le prove.
/// </summary>
internal static class PickedExtractionReportOutbox
{
    /// <summary>
    /// Il cambio cella di un'estrazione con l'UDC ancora nella sua cella (voce 45, compito 4): la riga
    /// <paramref name="queueRowId"/> della coda di estrazione del WCS esce e, se serviva una riga di
    /// estrazione per UDC, il fatto che la rimette da servire (<paramref name="reportFor"/>, con
    /// l'identificativo di quella riga) entra in questa coda, nella stessa scrittura: un arresto fra le
    /// due non lascia ne' la riga di coda sparita senza il fatto, ne' il fatto con la riga ancora li'.
    /// Una riga di coda gia' sparita non accoda niente.
    /// </summary>
    /// <returns>La riga di estrazione rimessa da servire, 0 se nessuna.</returns>
    public static async Task<decimal> LeaveTheExtractionQueueAsync(long queueRowId,
        Func<decimal, PendingPickedExtractionReport> reportFor)
    {
        var queued = 0m;
        await ExtractionQueue.WriteAsync(async context =>
        {
            queued = 0m;
            var row = await ExtractionQueueStore.FindAsync(context, queueRowId);
            if (row?.ExtractionRowId is { } extractionRowId && extractionRowId > 0)
            {
                PendingPickedExtractionReportStore.Add(context, reportFor(extractionRowId));
                queued = extractionRowId;
            }

            await ExtractionQueueStore.DeleteAsync(context, queueRowId);
        });
        return queued;
    }

    public static Task<List<PendingPickedExtractionReport>> PendingAsync(int take) =>
        ExtractionQueue.ReadAsync(context => PendingPickedExtractionReportStore.OldestAsync(context, take),
            new List<PendingPickedExtractionReport>());

    public static Task<bool> ExtractionOutcomeStillPendingAsync(decimal extractionRowId) =>
        ExtractionQueue.ReadAsync(
            context => PendingPickedExtractionReportStore.ExtractionOutcomeStillPendingAsync(context, extractionRowId),
            false);

    public static Task RecordFailureAsync(long id, Exception failure, bool park) =>
        ExtractionQueue.WriteAsync(context =>
            PendingPickedExtractionReportStore.RecordFailureAsync(context, id, failure.Message, park));

    public static Task DeleteAsync(long id) =>
        ExtractionQueue.WriteAsync(context => PendingPickedExtractionReportStore.DeleteAsync(context, id));
}
