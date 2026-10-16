using System.Data;
using Microsoft.EntityFrameworkCore;
using Wcs.Engine.Api;
using Wcs.Engine.Persistence.Entities;

namespace Wcs.Engine.Persistence;

/// <summary>
/// Le operazioni sulla coda di estrazione nel database del WCS, e la tabella di compatibilita'
/// con cui i consumatori la leggono ancora.
/// </summary>
/// <remarks>
/// I consumatori — il ciclo di estrazione dei magazzini, la ripresa all'avvio, i gestori di
/// gruppo — leggono la coda come <see cref="DataTable"/> con i nomi delle vecchie colonne:
/// <see cref="ToDataTable"/> gliela costruisce dalle righe, cosi' il passaggio del database non
/// li tocca. E' una forma di transizione: quando i consumatori leggeranno l'entita', sparira'.
/// Le due colonne che venivano da un <c>JOIN</c> con le note del WMS — il GUID dell'assegnazione
/// e la lavorazione — arrivano ora tutt'e due dalla riga stessa: dal compito 9 anche la
/// lavorazione della nota sta in coda (<see cref="ExtractionQueueItem.NoteProcessingId"/>), e la
/// lettura a parte sul WMS che il chiamante passava (<c>DbInfo.ProcessingIdsByAssignment</c>) non
/// c'è più.
/// </remarks>
internal static class ExtractionQueueStore
{
    #region Letture

    public static Task<List<ExtractionQueueItem>> PendingForAsync(WcsDbContext context, Guid relocatorItemId) =>
        context.ExtractionQueue.AsNoTracking()
            .Where(i => i.RelocatorItemId == relocatorItemId && !i.Processed)
            .OrderBy(i => i.Id)
            .ToListAsync();

    public static Task<ExtractionQueueItem?> FindByLoadUnitAsync(WcsDbContext context, decimal loadUnitId) =>
        context.ExtractionQueue.AsNoTracking().OrderBy(i => i.Id).FirstOrDefaultAsync(i => i.LoadUnitId == loadUnitId);

    public static Task<ExtractionQueueItem?> FindAsync(WcsDbContext context, long id) =>
        context.ExtractionQueue.AsNoTracking().SingleOrDefaultAsync(i => i.Id == id);

    /// <summary>
    /// C'e' una riga per l'UDC? Con <paramref name="excludeExtractionRowId"/> si toglie la riga di
    /// estrazione corrente e, come nel T-SQL <c>ID_RIGA_ESTRAZIONE_UDC &lt;&gt; @Id</c>, non contano
    /// le righe senza riga di estrazione (in EF Core <c>!=</c> da solo le includerebbe).
    /// <paramref name="excludeAssignmentId"/> toglie invece la riga nata da quell'assegnazione di
    /// nota (compito 14), indipendentemente dal filtro precedente: qui <c>!=</c> su un
    /// <c>AssignmentId</c> nullo include la riga (nessuna assegnazione non e' quella esclusa), che e'
    /// il comportamento voluto - le righe nate da un'estrazione per unita' di carico non hanno un
    /// <c>AssignmentId</c> e continuano a contare.
    /// </summary>
    public static Task<bool> AnyByLoadUnitAsync(WcsDbContext context, decimal loadUnitId, decimal? excludeExtractionRowId = null, decimal? excludeAssignmentId = null)
    {
        var query = excludeExtractionRowId == null
            ? context.ExtractionQueue.Where(i => i.LoadUnitId == loadUnitId)
            : context.ExtractionQueue.Where(i => i.LoadUnitId == loadUnitId && i.ExtractionRowId != null && i.ExtractionRowId != excludeExtractionRowId);

        if (excludeAssignmentId != null)
            query = query.Where(i => i.AssignmentId != excludeAssignmentId);

        return query.AnyAsync();
    }

    public static Task<bool> AnyByAssignmentAsync(WcsDbContext context, decimal assignmentId) =>
        context.ExtractionQueue.AnyAsync(i => i.AssignmentId == assignmentId);

    public static Task<bool> AnyByCellAsync(WcsDbContext context, decimal cellId) =>
        context.ExtractionQueue.AnyAsync(i => i.CellId == cellId);

    public static Task<int> CountPendingByCellAsync(WcsDbContext context, decimal cellId) =>
        context.ExtractionQueue.CountAsync(i => i.CellId == cellId && !i.Processed);

    public static Task<List<decimal>> QueuedAssignmentIdsAsync(WcsDbContext context) =>
        context.ExtractionQueue.AsNoTracking()
            .Where(i => i.AssignmentId != null)
            .Select(i => i.AssignmentId!.Value)
            .Distinct()
            .ToListAsync();

    /// <summary>
    /// Le righe legate a una riga di estrazione del WMS. Chi controlla le lavorazioni sospese parte
    /// da qui e chiede al WMS solo di queste: il contrario (tutte le righe sospese del WMS in un
    /// <c>IN</c> verso SQLite) superava il numero di variabili ammesse.
    /// </summary>
    public static Task<List<ExtractionQueueItem>> WithExtractionRowAsync(WcsDbContext context) =>
        context.ExtractionQueue.AsNoTracking()
            .Where(i => i.ExtractionRowId != null)
            .ToListAsync();

    /// <summary>Le righe legate a un'assegnazione di nota del WMS, per lo stesso controllo sulle note sospese.</summary>
    public static Task<List<ExtractionQueueItem>> WithAssignmentAsync(WcsDbContext context) =>
        context.ExtractionQueue.AsNoTracking()
            .Where(i => i.AssignmentId != null)
            .ToListAsync();

    #endregion Letture

    #region Scritture

    /// <summary>
    /// Inserisce la riga se non ha chiave, altrimenti la aggiorna. Dopo il salvataggio la riga
    /// inserita porta la chiave assegnata dal database.
    /// </summary>
    /// <remarks>
    /// Ogni evento parte dopo il salvataggio (<see cref="DeferredEvents"/>): un inserimento prima
    /// non ha ancora la chiave che il database gli assegna, e un aggiornamento o una cancellazione
    /// annunciati prima di un lotto che poi fallisce mostrerebbero ai client uno stato mai scritto.
    /// </remarks>
    public static async Task UpsertAsync(WcsDbContext context, ExtractionQueueItem item)
    {
        if (item.Id == 0)
        {
            context.ExtractionQueue.Add(item);
            context.Deferred.Add(() => Navigator.EventSink?.ExtractionQueueChanged(item, EngineQueueChange.Added));
            return;
        }

        var existing = await context.ExtractionQueue.SingleOrDefaultAsync(i => i.Id == item.Id);
        if (existing == null)
        {
            context.ExtractionQueue.Add(item);
            context.Deferred.Add(() => Navigator.EventSink?.ExtractionQueueChanged(item, EngineQueueChange.Added));
        }
        else
        {
            context.Entry(existing).CurrentValues.SetValues(item);
            context.Deferred.Add(() => Navigator.EventSink?.ExtractionQueueChanged(existing, EngineQueueChange.Updated));
        }
    }

    public static async Task DeleteAsync(WcsDbContext context, long id)
    {
        var existing = await context.ExtractionQueue.SingleOrDefaultAsync(i => i.Id == id);
        if (existing != null)
        {
            context.ExtractionQueue.Remove(existing);
            context.Deferred.Add(() => Navigator.EventSink?.ExtractionQueueChanged(existing, EngineQueueChange.Removed));
        }
    }

    public static async Task DeleteByLoadUnitAsync(WcsDbContext context, decimal loadUnitId)
    {
        var rows = await context.ExtractionQueue.Where(i => i.LoadUnitId == loadUnitId).ToListAsync();
        context.ExtractionQueue.RemoveRange(rows);
        foreach (var row in rows)
            context.Deferred.Add(() => Navigator.EventSink?.ExtractionQueueChanged(row, EngineQueueChange.Removed));
    }

    public static async Task SetProcessedAsync(WcsDbContext context, long id, bool processed)
    {
        var existing = await context.ExtractionQueue.SingleOrDefaultAsync(i => i.Id == id);
        if (existing != null)
        {
            existing.Processed = processed;
            context.Deferred.Add(() => Navigator.EventSink?.ExtractionQueueChanged(existing, EngineQueueChange.Updated));
        }
    }

    /// <summary>
    /// Ridirige le righe di una UDC verso un polmone: destinazione, cella polmone, da estrarre, e
    /// la riga di estrazione del WMS se c'e'. Era <c>UpdateExtractionLu</c>.
    /// </summary>
    /// <remarks>
    /// Compito 8: la lavorazione si sposta insieme alla riga di estrazione, non dopo. Prima era
    /// riletta ogni volta dalla riga di estrazione corrente (<c>DbInfo.GetIdLavorazione</c>), quindi
    /// non poteva restare indietro; ora che vive sulla riga di coda, cambiarne una sola lascerebbe
    /// la riga a puntare alla lavorazione di prima, e la chiusura guarderebbe il residuo di
    /// un'altra.
    /// </remarks>
    public static async Task RedirectByLoadUnitAsync(WcsDbContext context, decimal loadUnitId, Guid? destinationItemId,
        decimal bufferCellId, decimal extractionRowId, decimal processingId)
    {
        var rows = await context.ExtractionQueue.Where(i => i.LoadUnitId == loadUnitId).ToListAsync();
        foreach (var row in rows)
        {
            row.DestinationItemId = destinationItemId;
            row.BufferCellId = bufferCellId;
            row.Extract = true;
            row.ExtractionRowId = extractionRowId == 0 ? null : extractionRowId;
            row.ProcessingId = processingId;
        }
    }

    #endregion Scritture

    #region Compatibilita'

    /// <summary>
    /// La coda come la leggono ancora i consumatori: una tabella con i nomi delle vecchie colonne.
    /// </summary>
    /// <param name="rows">Le righe della coda.</param>
    public static DataTable ToDataTable(IEnumerable<ExtractionQueueItem> rows)
    {
        var table = new DataTable("ExtractionQueue");
        table.Columns.Add("ID_ESTRAZIONE_UDC", typeof(decimal));
        table.Columns.Add("ID_ASSEGNAZIONE_UDC", typeof(decimal));
        table.Columns.Add("ID_UDC", typeof(decimal));
        table.Columns.Add("ID_CELLA_POLMONE", typeof(decimal));
        table.Columns.Add("ID_ITEM_SOURCE", typeof(Guid));
        table.Columns.Add("ID_ITEM_DESTINATION", typeof(Guid));
        table.Columns.Add("ESTRARRE", typeof(bool));
        table.Columns.Add("ID_POSTAZIONE", typeof(string));
        table.Columns.Add("ELABORATA", typeof(bool));
        table.Columns.Add("GUID_ASSEGNAZIONE_UDC", typeof(Guid));
        table.Columns.Add("ID_LAVORAZIONE", typeof(decimal));
        table.Columns.Add("ID_CELLA", typeof(decimal));
        table.Columns.Add("CODICE_PORTA", typeof(string));
        table.Columns.Add("ID_ITEM_RELOCATOR", typeof(Guid));
        table.Columns.Add("ID_SOURCE_ASSEGNAZIONE_UDC", typeof(decimal));
        table.Columns.Add("ID_RIGA_ESTRAZIONE_UDC", typeof(decimal));
        table.Columns.Add("ID_CELLA_DESTINAZIONE", typeof(decimal));
        table.Columns.Add("CODICE_PORTA_DESTINAZIONE", typeof(string));

        foreach (var row in rows)
        {
            var record = table.NewRow();
            record["ID_ESTRAZIONE_UDC"] = (decimal)row.Id;
            record["ID_ASSEGNAZIONE_UDC"] = Db(row.AssignmentId);
            record["ID_UDC"] = row.LoadUnitId;
            record["ID_CELLA_POLMONE"] = Db(row.BufferCellId);
            record["ID_ITEM_SOURCE"] = row.SourceItemId;
            record["ID_ITEM_DESTINATION"] = Db(row.DestinationItemId);
            record["ESTRARRE"] = row.Extract;
            record["ID_POSTAZIONE"] = (object?)row.WorkstationId ?? DBNull.Value;
            record["ELABORATA"] = row.Processed;
            record["GUID_ASSEGNAZIONE_UDC"] = Db(row.AssignmentGuid);
            // Compito 9: la lavorazione della nota viene dalla riga, non piu' da una lettura sul WMS.
            // Lo zero resta un nullo, come quando l'assegnazione non aveva risposta: i due
            // consumatori scrivono PathMission.NoteProcessingId solo se la colonna non e' nulla, e
            // una lavorazione zero non e' una lavorazione.
            record["ID_LAVORAZIONE"] = Db(row.NoteProcessingId);
            record["ID_CELLA"] = Db(row.CellId);
            record["CODICE_PORTA"] = (object?)row.DoorCode ?? DBNull.Value;
            record["ID_ITEM_RELOCATOR"] = Db(row.RelocatorItemId);
            record["ID_SOURCE_ASSEGNAZIONE_UDC"] = Db(row.SourceAssignmentId);
            record["ID_RIGA_ESTRAZIONE_UDC"] = Db(row.ExtractionRowId);
            record["ID_CELLA_DESTINAZIONE"] = Db(row.DestinationCellId);
            record["CODICE_PORTA_DESTINAZIONE"] = (object?)row.DestinationDoorCode ?? DBNull.Value;
            table.Rows.Add(record);
        }

        return table;
    }

    /// <summary>La riga di coda come la scrivevano i vecchi chiamanti da una riga di tabella.</summary>
    public static ExtractionQueueItem FromDataRow(DataRow item) => new()
    {
        AssignmentId = Nullable(item, "ID_ASSEGNAZIONE_UDC"),
        BufferCellId = Nullable(item, "ID_CELLA_POLMONE"),
        DestinationItemId = item["ID_ITEM_DESTINATION"] is Guid destination ? destination : null,
        SourceItemId = item["ID_ITEM_SOURCE"] is Guid source ? source : Guid.Empty,
        RelocatorItemId = item["ID_ITEM_RELOCATOR"] is Guid relocator ? relocator : null,
        WorkstationId = item["ID_POSTAZIONE"] as string,
        LoadUnitId = item["ID_UDC"] is decimal lu ? lu : 0,
        CellId = Nullable(item, "ID_CELLA"),
        DoorCode = item["CODICE_PORTA"] as string,
        Extract = true,
        Processed = false
    };

    private static object Db(decimal? value) => value is { } v && v != 0 ? v : DBNull.Value;
    private static object Db(Guid? value) => value is { } v && v != Guid.Empty ? v : DBNull.Value;

    private static decimal? Nullable(DataRow row, string column) =>
        row.Table.Columns.Contains(column) && row[column] is decimal d && d != 0 ? d : null;

    #endregion Compatibilita'
}
