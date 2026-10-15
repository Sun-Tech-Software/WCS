using Wcs.Engine.Api;
using Wcs.Engine.ORM;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Wcs.Wms.Contracts;
using Wcs.Wms.Contracts.Test;
using Xunit;

namespace Wcs.Engine.Test;

/// <summary>
/// La segnalazione che ha preso il posto di due ordini
/// (<see cref="IWarehouse.ReportLoadUnitLeftExtractionQueueAsync"/>, ex
/// <c>ClearLoadUnitReservedPositionAsync</c> + <c>MarkNoteUnassignedAsync</c>, che
/// <c>DbInfo.DeleteLuFromExtractionQueue</c> chiamava una dopo l'altra).
/// <para>
/// Il motore non dice piu' che cosa scrivere: dice che l'unita' di carico ha lasciato la coda di
/// estrazione - un fatto suo, la coda e' nel suo database - e quali conseguenze trarne lo decide il
/// gestionale. I due test qui sotto sono anche la prova che il fatto e' <b>uno</b>: la segnalazione
/// parte sempre, con o senza assegnazione, e l'assegnazione e' un campo del fatto e non un secondo
/// fatto.
/// </para>
/// </summary>
public sealed class LoadUnitLeftExtractionQueueTest : IDisposable
{
    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;

    public LoadUnitLeftExtractionQueueTest()
    {
        Navigator.Store = _database;
    }

    public void Dispose()
    {
        Navigator.Store = null;
        Navigator.Warehouse = _previousWarehouse;
        _database.Dispose();
    }

    /// <summary>
    /// La riga di coda porta l'assegnazione, e la segnalazione la porta con se': <b>una</b> chiamata
    /// sola al confine, con l'unita' di carico e l'assegnazione che quella riga stava servendo.
    /// Prima erano due chiamate a due operazioni diverse, e l'assegnazione viaggiava da sola nella
    /// seconda.
    /// <para>
    /// <b>Il test non si aspetta piu' nessuna eccezione, e il compito 13 e' la ragione.</b> Fino al
    /// compito 21 a sollevare era <c>Navigator.WmsBusinessArea.NoteMagazzino.CancellaAssegnazioneUdc</c>,
    /// il gestionale in processo (<see langword="null"/> nei test del motore); poi
    /// <c>DbInfo.DeleteAssignedLuByIdEstrazioneUdc</c>, che cancellava la stessa riga in T-SQL
    /// diretto su <c>Navigator.Database</c>. Quella chiamata era <b>ridondante</b> - l'assegnazione
    /// che leggeva e' la stessa che viaggia nella segnalazione qui sopra, la cui terza conseguenza
    /// dal lato del gestionale e' proprio cancellarla - ed e' sparita senza essere sostituita da
    /// niente. Adesso il comando arriva in fondo, e la riga di coda se ne va: e' l'asserzione
    /// nuova.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheSignalCarriesTheLoadUnitAndTheAssignmentTheQueuedRowWasServing()
    {
        const decimal loadUnitId = 4711m;
        const decimal assignmentId = 777m;

        await ExtractionQueue.UpsertAsync(new ExtractionQueueItem
        {
            LoadUnitId = loadUnitId, AssignmentId = assignmentId, SourceItemId = Guid.NewGuid(), Processed = false
        });

        var warehouse = new FakeWarehouse();
        Navigator.Warehouse = warehouse;

        await DbInfo.DeleteLuFromExtractionQueue(loadUnitId);

        Assert.Equal([new LoadUnitLeftExtractionQueueRequest(loadUnitId, assignmentId)],
            warehouse.LoadUnitsLeftExtractionQueue);
        Assert.Empty(await ExtractionQueueQueries.ListAsync(processed: null, loadUnitId: loadUnitId, take: 10));
        Assert.Single(warehouse.Calls, c => c == nameof(IWarehouse.ReportLoadUnitLeftExtractionQueueAsync));

        // Il sesto punto del compito 21 non e' diventato una settima operazione: la segnalazione era
        // gia' partita una riga sopra, e nessuno dei tre fatti nuovi si aggiunge qui.
        Assert.DoesNotContain(nameof(IWarehouse.ReportMissionLeftAssignmentUnservedAsync), warehouse.Calls);
        Assert.DoesNotContain(nameof(IWarehouse.ReportMissionServedAssignmentAsync), warehouse.Calls);
        Assert.DoesNotContain(nameof(IWarehouse.ReportUnservableQueuedExtractionAsync), warehouse.Calls);
    }

    /// <summary>
    /// <b>L'asimmetria, dichiarata da un test.</b> Una riga di coda senza assegnazione e' lo stesso
    /// fatto - l'unita' di carico lascia la coda - e la segnalazione parte lo stesso, con
    /// l'assegnazione a zero. Prima qui partiva una chiamata sola delle due (la posizione prenotata
    /// si toglieva sempre, la nota no, e il <c>se</c> era un <c>if</c> del motore): adesso il
    /// <c>se</c> e' un campo della segnalazione, e la decisione che ne segue e' del gestionale.
    /// <para>
    /// La riga di coda c'e', ed e' il solo campo dell'assegnazione a mancare: senza la riga il test
    /// passerebbe anche con il campo letto male.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AQueuedRowWithoutAnAssignmentIsStillTheSameFactAndStillSignalled()
    {
        const decimal loadUnitId = 4712m;

        await ExtractionQueue.UpsertAsync(new ExtractionQueueItem
        {
            LoadUnitId = loadUnitId, AssignmentId = null, SourceItemId = Guid.NewGuid(), Processed = false
        });

        var warehouse = new FakeWarehouse();
        Navigator.Warehouse = warehouse;

        await DbInfo.DeleteLuFromExtractionQueue(loadUnitId);

        Assert.Equal([new LoadUnitLeftExtractionQueueRequest(loadUnitId, 0m)],
            warehouse.LoadUnitsLeftExtractionQueue);

        // Il motore non chiede piu' niente sull'assegnazione per conto suo: l'unica cosa che dice
        // sul fatto e' la segnalazione.
        Assert.DoesNotContain(nameof(IWarehouse.ReportMissionLeftAssignmentUnservedAsync), warehouse.Calls);

        // Compito 12: questa riga di coda non serve nessuna riga di estrazione, quindi non c'e'
        // nessun esito da segnalare al gestionale. E' la guardia che il test qui sotto isola.
        Assert.Empty(warehouse.ReportedExtractionOutcomes);
    }

    /// <summary>
    /// Compito 12: se la riga di coda che se ne va serviva una riga di estrazione, quella riga
    /// diventa <see cref="ExtractionRowStatus.NotReachable"/> - l'unita' di carico e' stata tolta
    /// dalla coda a mano, e per quella riga non arrivera' piu' nessuno.
    /// <para>
    /// <b>Qui il comportamento in campo cambia, e in meglio.</b> Prima questo punto chiamava
    /// <c>UpdateStatusExtractionLu(rowIdExtractionQueue, NotReachable)</c>, ma
    /// <c>rowIdExtractionQueue</c> viene da <c>GetExtractionLuRowId</c>, che risponde
    /// <c>ExtractionQueueItem.ExtractionRowId</c> - la riga del <b>gestionale</b> - mentre
    /// <c>UpdateStatusExtractionLu</c> la cercava come <c>ExtractionQueueItem.Id</c>, la chiave della
    /// coda del WCS: due identificativi diversi, e la lettura non trovava niente. E anche se la
    /// chiave fosse stata giusta, a quel punto la riga di coda era gia' stata cancellata qualche riga
    /// sopra. Quello stato non si e' mai scritto. Adesso l'identificativo che il metodo ha gia' in
    /// mano e' proprio quello che la segnalazione vuole, e la segnalazione parte.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheExtractionRowOfTheQueuedRowThatLeavesIsReportedNotReachable()
    {
        const decimal loadUnitId = 4716m;
        const decimal extractionRowId = 4816m;

        await ExtractionQueue.UpsertAsync(new ExtractionQueueItem
        {
            LoadUnitId = loadUnitId, AssignmentId = null, ExtractionRowId = extractionRowId,
            SourceItemId = Guid.NewGuid(), Processed = false
        });

        var warehouse = new FakeWarehouse();
        Navigator.Warehouse = warehouse;

        await DbInfo.DeleteLuFromExtractionQueue(loadUnitId);

        var report = Assert.Single(warehouse.ReportedExtractionOutcomes);
        Assert.Equal(0m, report.ExtractionHeaderId);
        Assert.Equal([new ExtractionRowOutcome(extractionRowId, ExtractionRowStatus.NotReachable)], report.Outcomes);
    }

    /// <summary>
    /// Revisione del compito 12: quella segnalazione sta <b>prima di qualunque cancellazione</b>, e
    /// non dopo. La riga di estrazione si legge in cima al metodo, quindi niente lo impedisce; e la
    /// segnalazione e' idempotente dall'upsert dello stesso compito, quindi anticiparla non costa
    /// niente.
    /// <para>
    /// <b>Il passo che qui fallisce e' cambiato con il compito 13.</b> Era
    /// <c>DeleteAssignedLuByIdEstrazioneUdc</c>, SQL diretto su <c>Navigator.Database</c> che nei
    /// test del motore era nullo; quella chiamata era ridondante ed e' sparita. A fallire adesso e'
    /// la segnalazione dell'unita' di carico che lascia la coda, resa indisponibile da sola: il
    /// passo successivo alla segnalazione dell'esito, come prima. Con la segnalazione in fondo, un
    /// comando manuale che si fermava li' lasciava la riga di coda cancellata e l'esito perso per
    /// sempre - l'identificativo della riga di estrazione non era piu' ricavabile da nessuna parte.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheExtractionRowIsReportedEvenWhenALaterStepOfTheCommandFails()
    {
        const decimal loadUnitId = 4717m;
        const decimal assignmentId = 778m;
        const decimal extractionRowId = 4817m;

        await ExtractionQueue.UpsertAsync(new ExtractionQueueItem
        {
            LoadUnitId = loadUnitId, AssignmentId = assignmentId, ExtractionRowId = extractionRowId,
            SourceItemId = Guid.NewGuid(), Processed = false
        });

        var warehouse = new FakeWarehouse();
        warehouse.UnavailableOperations.Add(nameof(IWarehouse.ReportLoadUnitLeftExtractionQueueAsync));
        Navigator.Warehouse = warehouse;

        await Assert.ThrowsAsync<WarehouseUnavailableException>(() => DbInfo.DeleteLuFromExtractionQueue(loadUnitId));

        var report = Assert.Single(warehouse.ReportedExtractionOutcomes);
        Assert.Equal([new ExtractionRowOutcome(extractionRowId, ExtractionRowStatus.NotReachable)], report.Outcomes);

        // L'ordine, dai due lati: la segnalazione precede la gemella sull'unita' di carico che
        // lascia la coda...
        Assert.True(warehouse.Calls.IndexOf(nameof(IWarehouse.ReportExtractionOutcomesAsync)) <
                    warehouse.Calls.IndexOf(nameof(IWarehouse.ReportLoadUnitLeftExtractionQueueAsync)));

        // ...e la riga di coda, che la cancellazione non ha raggiunto, e' ancora dov'era: e' la
        // prova che l'esito non dipende piu' da quello che succede dopo.
        Assert.Single(await ExtractionQueueQueries.ListAsync(processed: null, loadUnitId: loadUnitId, take: 10));
    }
}
