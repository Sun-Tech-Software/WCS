using Microsoft.EntityFrameworkCore;
using Wcs.Engine.Persistence.Entities;

namespace Wcs.Engine.Persistence;

/// <summary>
/// Le operazioni sullo stesso contesto per la coda del fatto «l'estrazione prelevata e' diventata
/// un deposito» (voce 45), e per il cambio cella prima del prelievo, che rimette da servire la
/// riga di estrazione con <c>AssignmentId</c> 0 e il GUID vuoto (decisione del proprietario dell'11/10).
/// </summary>
internal static class PendingPickedExtractionReportStore
{
    /// <summary>
    /// Aggiunge la riga al contesto, senza salvare: chi chiama la scrive nella stessa transazione
    /// della riga della missione (<c>Items.PathMission</c>, il commit del cambio) o, per il cambio
    /// cella, della cancellazione della riga della coda di estrazione
    /// (<c>ORM.PickedExtractionReportOutbox.LeaveTheExtractionQueueAsync</c>).
    /// </summary>
    public static void Add(WcsDbContext context, PendingPickedExtractionReport report) =>
        context.PendingPickedExtractionReports.Add(report);

    /// <summary>Le righe da consegnare, in ordine di arrivo: le parcheggiate no.</summary>
    public static Task<List<PendingPickedExtractionReport>> OldestAsync(WcsDbContext context, int take) =>
        context.PendingPickedExtractionReports.AsNoTracking()
            .Where(report => !report.Parked)
            .OrderBy(report => report.Id)
            .Take(take)
            .ToListAsync();

    /// <summary>
    /// L'esito «estratta» della stessa riga di estrazione e' ancora nella sua coda
    /// (<see cref="PendingExtractionOutcome"/>): la riapertura arriverebbe al gestionale prima
    /// dell'«estratta», che poi riscriverebbe la riga come prelevata (specifica, sezione 3.4).
    /// </summary>
    public static Task<bool> ExtractionOutcomeStillPendingAsync(WcsDbContext context, decimal extractionRowId) =>
        context.PendingExtractionOutcomes.AsNoTracking()
            .AnyAsync(outcome => outcome.ExtractionRowId == extractionRowId);

    public static async Task RecordFailureAsync(WcsDbContext context, long id, string error, bool park)
    {
        var report = await context.PendingPickedExtractionReports.SingleOrDefaultAsync(row => row.Id == id);
        if (report == null) return;

        report.AttemptCount++;
        report.LastError = error;
        report.Parked = park;
    }

    public static async Task DeleteAsync(WcsDbContext context, long id)
    {
        var report = await context.PendingPickedExtractionReports.SingleOrDefaultAsync(row => row.Id == id);
        if (report != null) context.PendingPickedExtractionReports.Remove(report);
    }
}
