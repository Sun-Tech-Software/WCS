using Helper.Converters;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Xunit;

namespace Wcs.Engine.Test;

/// <summary>
/// La coda di estrazione sul database del WCS: l'ordine, le letture per UDC e per cella, la
/// tabella di compatibilita' con cui i consumatori la leggono ancora.
/// </summary>
public sealed class ExtractionQueueStoreTest : IDisposable
{
    private readonly WcsDatabase _database = WcsDatabase.InMemory();

    public ExtractionQueueStoreTest()
    {
        Navigator.Store = _database;
    }

    public void Dispose()
    {
        Navigator.Store = null;
        _database.Dispose();
    }

    private static ExtractionQueueItem Row(decimal loadUnit, Guid relocator, bool extract = true, decimal? assignment = null, decimal? cell = null) => new()
    {
        LoadUnitId = loadUnit,
        SourceItemId = Guid.NewGuid(),
        RelocatorItemId = relocator,
        Extract = extract,
        AssignmentId = assignment,
        CellId = cell,
        DoorCode = "P01A",
        WorkstationId = "PC-01"
    };

    [Fact]
    public async Task RowsGetAnIncreasingKeyAndComeBackInQueueOrderForTheirRelocator()
    {
        var relocator = Guid.NewGuid();
        var other = Guid.NewGuid();

        await ORM.ExtractionQueue.UpsertAsync(Row(1, relocator, extract: false));
        await ORM.ExtractionQueue.UpsertAsync(Row(2, relocator));
        await ORM.ExtractionQueue.UpsertAsync(Row(3, other));

        await using var context = _database.CreateContext();
        var pending = await ExtractionQueueStore.PendingForAsync(context, relocator);

        Assert.Equal([1m, 2m], pending.Select(r => r.LoadUnitId));
        Assert.True(pending[0].Id < pending[1].Id);
        Assert.False(pending[0].Extract);
    }

    [Fact]
    public async Task RowsTiedToTheWmsComeBackByExtractionRowAndByAssignment()
    {
        var relocator = Guid.NewGuid();
        var withRow = Row(1, relocator);
        withRow.ExtractionRowId = 501;
        var withAssignment = Row(2, relocator, assignment: 77);
        var plain = Row(3, relocator);

        await ORM.ExtractionQueue.UpsertAsync(withRow);
        await ORM.ExtractionQueue.UpsertAsync(withAssignment);
        await ORM.ExtractionQueue.UpsertAsync(plain);

        await using var context = _database.CreateContext();
        Assert.Equal([1m], (await ExtractionQueueStore.WithExtractionRowAsync(context)).Select(r => r.LoadUnitId));
        Assert.Equal([2m], (await ExtractionQueueStore.WithAssignmentAsync(context)).Select(r => r.LoadUnitId));
    }

    /// <summary>
    /// Compito 8: la lavorazione dell'estrazione sopravvive alla scrittura e alla rilettura. Era il
    /// dato che il motore rileggeva da SQL Server a ogni chiusura di movimento
    /// (<c>DbInfo.GetIdLavorazione</c>): se non restasse sulla riga di coda, la chiusura
    /// riceverebbe zero e la lavorazione non si chiuderebbe piu', in silenzio. La riga che non ne
    /// ha una - il ricollocamento di un ostacolo, la riga nata da una nota - resta a zero, che qui
    /// e' il valore di partenza della colonna e non un dato perduto.
    /// <para>
    /// <b>Compito 12: quel lettore non c'e' piu'.</b> La chiusura la decide il gestionale, che la
    /// lavorazione la ricava dalle righe segnalate, e
    /// <see cref="ExtractionQueueItem.ProcessingId"/> oggi si scrive senza che nessuno lo legga -
    /// vedi la sua nota. Questo test resta perche' la colonna resta, e finche' c'e' deve reggere il
    /// dato che ci si mette; il giorno in cui una migrazione la togliera', se ne va anche lui.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheProcessingOfARowSurvivesTheWriteAndTheReadBack()
    {
        var relocator = Guid.NewGuid();
        var withProcessing = Row(50, relocator);
        withProcessing.ExtractionRowId = 81;
        withProcessing.ProcessingId = 4242;

        await ORM.ExtractionQueue.UpsertAsync(withProcessing);
        await ORM.ExtractionQueue.UpsertAsync(Row(51, relocator));

        await using var context = _database.CreateContext();
        var read = await ExtractionQueueStore.FindByLoadUnitAsync(context, 50);

        Assert.Equal(4242m, read!.ProcessingId);
        Assert.Equal(81m, read.ExtractionRowId);
        Assert.Equal(4242m, (await ExtractionQueueStore.FindAsync(context, read.Id))!.ProcessingId);
        Assert.Equal(0m, (await ExtractionQueueStore.FindByLoadUnitAsync(context, 51))!.ProcessingId);
    }

    /// <summary>
    /// Compito 9: la lavorazione della <b>nota</b> sopravvive alla scrittura e alla rilettura, ed e'
    /// un campo diverso da quella dell'estrazione. E' il dato che il motore rileggeva dal gestionale
    /// a ogni giro di ogni ricollocatore (<c>DbInfo.ProcessingIdsByAssignment</c>): se non restasse
    /// sulla riga di coda, la missione nascerebbe con zero e la nota non si chiuderebbe piu'.
    /// <para>
    /// <b>Perche' due campi e non uno.</b> Le due lavorazioni stanno su tabelle diverse
    /// (<c>LAVORAZIONI_ESTRAZIONI_UDC</c> e <c>LAVORAZIONI_NOTE</c>) e si chiudono con due
    /// <c>UPDATE</c> diversi, ma condividono lo spazio degli identificativi: un solo campo per tutt'e
    /// due farebbe chiudere alla fine di ogni movimento di nota una lavorazione di estrazione
    /// omonima - fino al compito 12 <c>UpdateStatusExtractionLu</c> passava
    /// <see cref="ExtractionQueueItem.ProcessingId"/> alla chiusura per <b>ogni</b> riga di coda,
    /// anche quella nata da un'assegnazione. La ridirezione lo mostra anche da sola: sposta la riga
    /// di estrazione e la sua lavorazione, e non deve toccare quella della nota.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheNoteProcessingOfARowSurvivesTheWriteAndStaysApartFromTheExtractionOne()
    {
        var relocator = Guid.NewGuid();
        var fromANote = Row(70, relocator, assignment: 705);
        fromANote.NoteProcessingId = 7050;

        await ORM.ExtractionQueue.UpsertAsync(fromANote);
        await ORM.ExtractionQueue.UpsertAsync(Row(71, relocator));

        await ORM.ExtractionQueue.WriteAsync(context => ExtractionQueueStore.RedirectByLoadUnitAsync(
            context, 70, Guid.NewGuid(), bufferCellId: 9, extractionRowId: 93, processingId: 13));

        await using var context = _database.CreateContext();
        var read = await ExtractionQueueStore.FindByLoadUnitAsync(context, 70);

        // La lavorazione della nota e' rimasta, quella dell'estrazione e' arrivata accanto: sono due.
        Assert.Equal(7050m, read!.NoteProcessingId);
        Assert.Equal(13m, read.ProcessingId);
        Assert.Equal(7050m, (await ExtractionQueueStore.FindAsync(context, read.Id))!.NoteProcessingId);

        // La riga che non nasce da una nota - il ricollocamento di un ostacolo, la riga di
        // un'estrazione per unita' di carico - resta a zero.
        Assert.Equal(0m, (await ExtractionQueueStore.FindByLoadUnitAsync(context, 71))!.NoteProcessingId);
    }

    /// <summary>
    /// Compito 8: la riga di estrazione e la sua lavorazione sono una coppia, e la ridirezione le
    /// cambia insieme. Cambiare solo la prima lascerebbe la riga di coda a puntare alla lavorazione
    /// di prima, e la chiusura guarderebbe il residuo di un'altra lavorazione: prima del compito 8
    /// non poteva succedere, perche' la lavorazione veniva riletta ogni volta dalla riga di
    /// estrazione corrente.
    /// </summary>
    [Fact]
    public async Task RedirectingALoadUnitMovesItsExtractionRowAndItsProcessingTogether()
    {
        var relocator = Guid.NewGuid();
        var row = Row(60, relocator);
        row.ExtractionRowId = 91;
        row.ProcessingId = 11;
        await ORM.ExtractionQueue.UpsertAsync(row);

        await ORM.ExtractionQueue.WriteAsync(context => ExtractionQueueStore.RedirectByLoadUnitAsync(
            context, 60, Guid.NewGuid(), bufferCellId: 9, extractionRowId: 92, processingId: 12));

        await using var context = _database.CreateContext();
        var redirected = await ExtractionQueueStore.FindByLoadUnitAsync(context, 60);

        Assert.Equal(92m, redirected!.ExtractionRowId);
        Assert.Equal(12m, redirected.ProcessingId);
    }

    [Fact]
    public async Task LookupsByLoadUnitAssignmentAndCellAnswerWhatTheOldQueriesAnswered()
    {
        var relocator = Guid.NewGuid();
        await ORM.ExtractionQueue.UpsertAsync(Row(10, relocator, assignment: 500, cell: 42));

        Assert.True(await ORM.DbInfo.IsLuPresentInExtranctionLu(10));
        Assert.False(await ORM.DbInfo.IsLuPresentInExtranctionLu(11));
        Assert.True(await ORM.DbInfo.IsPresentInExtractionLu(500));
        Assert.False(await ORM.DbInfo.IsPresentInExtractionLu(501));
        Assert.Equal(0, await ORM.DbInfo.GetLuExtractionStatus(10));
        Assert.Equal(-1, await ORM.DbInfo.GetLuExtractionStatus(11));

        await using var context = _database.CreateContext();
        Assert.Equal(1, await ExtractionQueueStore.CountPendingByCellAsync(context, 42));
        Assert.True(await ExtractionQueueStore.AnyByCellAsync(context, 42));
        Assert.Equal([500m], await ExtractionQueueStore.QueuedAssignmentIdsAsync(context));
    }

    [Fact]
    public async Task ProcessedRowsLeaveThePendingListAndDeletionTakesThemAway()
    {
        var relocator = Guid.NewGuid();
        await ORM.ExtractionQueue.UpsertAsync(Row(20, relocator));
        await ORM.ExtractionQueue.UpsertAsync(Row(21, relocator));

        long first;
        await using (var context = _database.CreateContext())
            first = (await ExtractionQueueStore.FindByLoadUnitAsync(context, 20))!.Id;

        await ORM.DbInfo.SetExtractionItemQueueProcessed(first, 1);
        Assert.Equal(1, await ORM.DbInfo.GetLuExtractionStatus(20));

        await using (var context = _database.CreateContext())
            Assert.Equal([21m], (await ExtractionQueueStore.PendingForAsync(context, relocator)).Select(r => r.LoadUnitId));

        await ORM.DbInfo.DeleteExtractionItemQueueById(first);
        Assert.False(await ORM.DbInfo.IsLuPresentInExtranctionLu(20));
    }

    [Fact]
    public async Task TheCompatibilityTableCarriesTheOldColumnNamesAndNulls()
    {
        var relocator = Guid.NewGuid();
        var guid = Guid.NewGuid();
        await ORM.ExtractionQueue.UpsertAsync(new ExtractionQueueItem
        {
            LoadUnitId = 30, SourceItemId = Guid.NewGuid(), RelocatorItemId = relocator, AssignmentId = 7,
            AssignmentGuid = guid, BufferCellId = 5, DoorCode = "P02B", WorkstationId = "PC-02",
            NoteProcessingId = 99
        });

        List<ExtractionQueueItem> rows;
        await using (var context = _database.CreateContext())
            rows = await ExtractionQueueStore.PendingForAsync(context, relocator);

        var table = ExtractionQueueStore.ToDataTable(rows);
        var row = Assert.Single(table.Rows.Cast<System.Data.DataRow>());

        Assert.Equal(30m, row.GetValue<decimal>("ID_UDC"));
        Assert.Equal(7m, row.GetValue<decimal>("ID_ASSEGNAZIONE_UDC"));
        Assert.Equal(guid, row.GetValue<Guid>("GUID_ASSEGNAZIONE_UDC"));
        Assert.Equal(99m, row.GetValue<decimal>("ID_LAVORAZIONE"));
        Assert.Equal(5m, row.GetValue<decimal>("ID_CELLA_POLMONE"));
        Assert.Equal("P02B", row.GetValue<string>("CODICE_PORTA"));
        Assert.True(row.GetValue<bool>("ESTRARRE"));
        Assert.False(row.GetValue<bool>("ELABORATA"));
        Assert.Equal(DBNull.Value, row["ID_ITEM_DESTINATION"]);
        Assert.Equal(DBNull.Value, row["ID_CELLA_DESTINAZIONE"]);

        // E la riga costruita dalla tabella torna una riga di coda con gli stessi valori.
        var back = ExtractionQueueStore.FromDataRow(row);
        Assert.Equal(30m, back.LoadUnitId);
        Assert.Equal(7m, back.AssignmentId);
        Assert.Equal(5m, back.BufferCellId);
        Assert.Equal(relocator, back.RelocatorItemId);
        Assert.Null(back.DestinationItemId);
    }

    [Fact]
    public async Task RedirectingALoadUnitChangesItsRowsAndKeepsTheOthers()
    {
        var relocator = Guid.NewGuid();
        await ORM.ExtractionQueue.UpsertAsync(Row(40, relocator, extract: false));
        await ORM.ExtractionQueue.UpsertAsync(Row(41, relocator));

        var destination = Guid.NewGuid();
        await ORM.ExtractionQueue.WriteAsync(context =>
            ExtractionQueueStore.RedirectByLoadUnitAsync(context, 40, destination, bufferCellId: 9, extractionRowId: 123,
                processingId: 456));

        await using var context = _database.CreateContext();
        var redirected = await ExtractionQueueStore.FindByLoadUnitAsync(context, 40);
        var untouched = await ExtractionQueueStore.FindByLoadUnitAsync(context, 41);

        Assert.Equal(destination, redirected!.DestinationItemId);
        Assert.Equal(9m, redirected.BufferCellId);
        Assert.True(redirected.Extract);
        Assert.Equal(123m, redirected.ExtractionRowId);
        Assert.Null(untouched!.BufferCellId);
    }

    /// <summary>
    /// «La UDC e' in un'altra estrazione?» del Bff: ID_RIGA_ESTRAZIONE_UDC &lt;&gt; @IdRigaEstrazione in
    /// T-SQL, dove una riga senza ID_RIGA_ESTRAZIONE_UDC non conta. In EF Core la disuguaglianza da
    /// sola includerebbe i nulli: il filtro li esclude apposta, e questo test lo prova sulla riga nulla.
    /// </summary>
    [Fact]
    public async Task ContainsCanLeaveOutTheCurrentExtractionRowAndIgnoresRowsWithoutOne()
    {
        var relocator = Guid.NewGuid();
        await using (var context = _database.CreateContext())
        {
            await ExtractionQueueStore.UpsertAsync(context, Row(600, relocator));                    // senza riga di estrazione
            var withRow = Row(600, relocator);
            withRow.ExtractionRowId = 15;
            await ExtractionQueueStore.UpsertAsync(context, withRow);
            await context.SaveChangesAsync();
        }

        await using var reading = _database.CreateContext();
        Assert.True(await ExtractionQueueStore.AnyByLoadUnitAsync(reading, 600));
        Assert.False(await ExtractionQueueStore.AnyByLoadUnitAsync(reading, 600, excludeExtractionRowId: 15)); // la nulla non conta, la 15 e' quella corrente
        Assert.True(await ExtractionQueueStore.AnyByLoadUnitAsync(reading, 600, excludeExtractionRowId: 16));  // la 15 e' «un'altra estrazione»
        Assert.False(await ExtractionQueueStore.AnyByLoadUnitAsync(reading, 601, excludeExtractionRowId: 16));
    }

    /// <summary>
    /// L'altra esclusione, quella per assegnazione di nota (compito 14), sui nulli si comporta al
    /// contrario della prima: <c>AssignmentId != excludeAssignmentId</c> su una riga con
    /// <c>AssignmentId</c> nullo la <b>include</b>, ed e' il comportamento voluto - una riga nata da
    /// un'estrazione per unita' di carico non ha assegnazione di nota e deve continuare a contare,
    /// altrimenti il secondo motivo di rifiuto («la UDC e' gia' in coda per un'altra estrazione»)
    /// smetterebbe di vederla. E' l'affermazione su cui poggia quel rifiuto, e finora nessun test la
    /// provava: le tre asserzioni dell'esclusione usavano tutte righe con l'assegnazione valorizzata.
    /// </summary>
    [Fact]
    public async Task ContainsStillCountsARowWithoutAnAssignmentWhenAnAssignmentIsExcluded()
    {
        var relocator = Guid.NewGuid();
        await using (var context = _database.CreateContext())
        {
            await ExtractionQueueStore.UpsertAsync(context, Row(610, relocator));                     // estrazione per UDC: nessuna assegnazione
            await ExtractionQueueStore.UpsertAsync(context, Row(610, relocator, assignment: 800));    // la riga di questa stessa assegnazione
            await ExtractionQueueStore.UpsertAsync(context, Row(611, relocator, assignment: 800));    // un'altra UDC, solo la riga esclusa
            await context.SaveChangesAsync();
        }

        await using var reading = _database.CreateContext();
        // La riga con l'assegnazione nulla resta: e' quello che il commento di AnyByLoadUnitAsync dichiara.
        Assert.True(await ExtractionQueueStore.AnyByLoadUnitAsync(reading, 610, excludeAssignmentId: 800));
        // Che l'esclusione stia davvero funzionando lo dice la UDC che ha solo la riga esclusa: senza
        // questa asserzione la precedente sarebbe verde anche con l'esclusione spenta.
        Assert.False(await ExtractionQueueStore.AnyByLoadUnitAsync(reading, 611, excludeAssignmentId: 800));
        Assert.True(await ExtractionQueueStore.AnyByLoadUnitAsync(reading, 611, excludeAssignmentId: 801));
    }
}
