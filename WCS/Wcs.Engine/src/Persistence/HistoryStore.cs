using Microsoft.EntityFrameworkCore;
using Wcs.Engine.Items;
using Wcs.Engine.Persistence.Entities;

namespace Wcs.Engine.Persistence;

/// <summary>
/// Una scrittura sullo storico del WCS: un'operazione sul contesto del mese corrente.
/// </summary>
public delegate Task HistoryWrite(WcsHistoryDbContext context);

/// <summary>
/// La mappa fra i record in memoria e le righe dello storico.
/// </summary>
internal static class HistoryStore
{
    /// <summary>
    /// La riga di un evento del navigatore, con la data di adesso: e' il momento dell'evento, non
    /// quello in cui la riga verra' scritta.
    /// </summary>
    public static NavigatorLogEntry Snapshot(NavigatorLogRecord record, DateTime now) => new()
    {
        Date = now,
        ItemId = NullIfEmpty(record.SourceItemId),
        Asi = NullIfEmpty(record.ASI),
        Partition = NullIfEmpty(record.PARTITION),
        Subitem = NullIfEmpty(record.SUBITEM),
        Status = record.Status,
        Description = string.IsNullOrEmpty(record.Description) ? null : record.Description,
        DestinationItemId = NullIfEmpty(record.DestinationItemId),
        MissionId = NullIfEmpty(record.MissionId),
        MissionStart = record.StartMission == default(DateTime) ? null : record.StartMission,
        MissionEnd = record.EndMission == default(DateTime) ? null : record.EndMission,
        MissionStatus = (short)record.StatusMission,
        RunningItemId = NullIfEmpty(record.RunningItemId)
    };

    #region UDC movimentate

    /// <summary>La riga di una UDC movimentata, presa ora dall'oggetto.</summary>
    public static HandledLoadUnit Snapshot(LoadUnit value) => new()
    {
        MissionId = value.MissionId,
        Type = (short)value.Type,
        Date = value.Date,
        CellId = value.CellId,
        LoadUnitCode = NullIfEmpty(value.LuCode),
        ProcessId = value.ProcessId == 0 ? null : value.ProcessId,
        ResultDescription = NullIfEmpty(value.ResultDescription),
        LoadUnitProcessId = value.LuProcessId == 0 ? null : value.LuProcessId,
        NoteHeaderId = value.NoteHeaderId == 0 ? null : value.NoteHeaderId,
        ExtractionHeaderId = value.LuExtractionHeaderId == 0 ? null : value.LuExtractionHeaderId,
        LoadUnitId = value.LuId == 0 ? null : value.LuId
    };

    /// <summary>Copia la riga nell'oggetto, come faceva la lettura da <c>LU_HANDLED</c>.</summary>
    public static void Apply(HandledLoadUnit row, LoadUnit value)
    {
        value.SetId(row.Id);
        value.Type = (LoadUnit.LoadUnitTypes)row.Type;
        value.Date = row.Date;
        value.CellId = row.CellId;
        value.LuCode = row.LoadUnitCode;
        value.ProcessId = row.ProcessId ?? 0;
        value.ResultDescription = row.ResultDescription;
        value.LuProcessId = row.LoadUnitProcessId ?? 0;
        value.NoteHeaderId = row.NoteHeaderId ?? 0;
        value.LuExtractionHeaderId = row.ExtractionHeaderId ?? 0;
        value.LuId = row.LoadUnitId ?? 0;
    }

    public static Task<HandledLoadUnit?> FindByMissionAsync(WcsHistoryDbContext context, Guid missionId) =>
        context.HandledLoadUnits.AsNoTracking().SingleOrDefaultAsync(h => h.MissionId == missionId);

    /// <summary>
    /// Inserisce la riga della missione se non c'e', altrimenti la aggiorna: la missione e' la
    /// chiave, cosi' chi scrive non aspetta una chiave dal database. Due scritture della stessa
    /// missione nello stesso lotto si incontrano nel contesto.
    /// </summary>
    public static async Task UpsertByMissionAsync(WcsHistoryDbContext context, HandledLoadUnit snapshot)
    {
        var tracked = context.ChangeTracker.Entries<HandledLoadUnit>().Select(e => e.Entity)
            .FirstOrDefault(h => h.MissionId == snapshot.MissionId);
        var existing = tracked ?? await context.HandledLoadUnits.SingleOrDefaultAsync(h => h.MissionId == snapshot.MissionId);

        if (existing == null)
        {
            context.HandledLoadUnits.Add(snapshot);
            return;
        }

        snapshot.Id = existing.Id;
        context.Entry(existing).CurrentValues.SetValues(snapshot);
    }

    public static async Task DeleteByMissionAsync(WcsHistoryDbContext context, Guid missionId)
    {
        var existing = await context.HandledLoadUnits.SingleOrDefaultAsync(h => h.MissionId == missionId);
        if (existing != null) context.HandledLoadUnits.Remove(existing);
    }

    #endregion UDC movimentate

    private static Guid? NullIfEmpty(Guid? value) => value is null || value == Guid.Empty ? null : value;

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}

/// <summary>
/// Esegue un lotto di scritture sullo storico: un contesto sul file del mese di adesso, tutte le
/// operazioni, un solo <c>SaveChanges</c>. Un lotto a cavallo del cambio di mese finisce per
/// intero nel mese nuovo: e' l'imprecisione di un intervallo di raccolta, pochi millisecondi.
/// </summary>
internal sealed class WcsHistoryBatchExecutor(WcsHistoryDatabase history)
{
    public async Task ExecuteAsync(IReadOnlyList<HistoryWrite> batch, CancellationToken cancellationToken)
    {
        await using var context = history.CreateContext();

        foreach (var write in batch)
            await write(context).ConfigureAwait(false);

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
