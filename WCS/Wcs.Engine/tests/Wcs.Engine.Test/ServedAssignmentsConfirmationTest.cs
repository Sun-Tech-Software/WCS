using Wcs.Engine.Api;
using Wcs.Engine.ORM;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Wcs.Wms.Contracts;
using Wcs.Wms.Contracts.Test;
using Xunit;

namespace Wcs.Engine.Test;

/// <summary>
/// Compito 19, terza operazione: la conferma delle assegnazioni servite
/// (<see cref="IWarehouse.ConfirmServedAssignmentsAsync"/>, ex <c>DbInfo.GetAssignedDataByExtractionId</c>
/// per intero - la <c>SELECT DISTINCT</c> su tre tabelle del gestionale compresa).
/// <para>
/// Di qua resta la sola cosa che e' del WCS: l'identificativo dell'assegnazione, che sta nella coda
/// di estrazione del suo database. La query e le conferme che ne seguono sono lavoro del gestionale,
/// ed e' l'unico gruppo di questo compito in cui il taglio elimina anche una query SQL diretta.
/// </para>
/// </summary>
public sealed class ServedAssignmentsConfirmationTest : IDisposable
{
    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;

    public ServedAssignmentsConfirmationTest()
    {
        Navigator.Store = _database;
    }

    public void Dispose()
    {
        Navigator.Store = null;
        Navigator.Warehouse = _previousWarehouse;
        _database.Dispose();
    }

    /// <summary>La riga di coda del WCS, con l'assegnazione che il motore leggera' da li'.</summary>
    private static async Task<long> QueueRow(decimal loadUnitId, decimal? assignmentId)
    {
        await ExtractionQueue.UpsertAsync(new ExtractionQueueItem
        {
            LoadUnitId = loadUnitId, AssignmentId = assignmentId, SourceItemId = Guid.NewGuid(), Processed = false
        });

        var queued = await ExtractionQueueQueries.ListAsync(processed: null, loadUnitId: loadUnitId, take: 10);
        return Assert.Single(queued).Id;
    }

    /// <summary>
    /// La richiesta porta l'assegnazione e l'utente, e nient'altro: della testata e delle righe di
    /// distinta il motore non sa piu' niente, perche' la <c>SELECT DISTINCT</c> che le trovava e'
    /// passata di la'.
    /// </summary>
    [Fact]
    public async Task TheServedAssignmentsAreConfirmedWithTheAssignmentAndTheUserOnly()
    {
        const decimal assignmentId = 900m;

        var rowId = await QueueRow(4713m, assignmentId);

        var warehouse = new FakeWarehouse();
        Navigator.Warehouse = warehouse;

        await DbInfo.ConfirmServedAssignmentsByExtractionId(rowId);

        var request = Assert.Single(warehouse.ConfirmedServedAssignments);
        Assert.Equal(assignmentId, request.AssignmentId);
        Assert.Equal(Navigator.UserId, request.UserId);
    }

    /// <summary>
    /// <b>Compito 13</b>: il giro vero della missione arrivata alla postazione
    /// (<c>PathMission.ProcessExtractionItems</c>) arriva fino alla conferma. Fra la segnalazione
    /// dell'esito e la conferma c'era <c>DbInfo.SetAssignmentsNotesProcessed</c>, un
    /// <c>UPDATE ASSEGNAZIONE_NOTE SET PRELEVATO = 1</c> in T-SQL diretto: nelle prove del motore
    /// <c>Navigator.Database</c> era nullo - e dal compito 16 non esiste piu', perche' il motore
    /// non ha nessun SQL Server - quindi quella riga faceva saltare il resto del giro, il
    /// <c>catch</c> del metodo la registrava, e la conferma non partiva ne' la riga di coda se ne
    /// andava.
    /// <para>
    /// Il fatto «assegnazione servita» quel punto lo dice gia', due righe piu' giu' nello stesso
    /// giro (<c>ProcessAssignments</c>). Quanto a <c>PRELEVATO</c>: la leggono <b>due</b> query del
    /// gestionale - <c>AssegnazioniProntePerCodaSql</c>, il filtro delle assegnazioni da consegnare
    /// alla coda del WCS, e il <c>pickUpSql</c> di <c>GetCellBusyStateAsync</c>, tutt'e due in
    /// <c>MagazzinoPerWcs.cs</c> (la prima stesura di questo commento diceva il contrario). La
    /// scrittura si toglie lo stesso perche' su questo percorso l'assegnazione viene
    /// <b>cancellata</b> dalla conseguenza del fatto segnalato nello stesso giro - la conferma la
    /// consuma, e «assegnazione servita» cancella per GUID - non perche' nessuno legga la colonna.
    /// Il percorso di guasto, con la finestra che resta scoperta, e' riderivato per intero nel
    /// commento di <c>PathMission.ProcessExtractionItems</c>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheArrivedMissionConfirmsTheAssignmentOfItsQueueRow()
    {
        const decimal loadUnitId = 4716m;

        var rowId = await QueueRow(loadUnitId, assignmentId: 902m);

        var warehouse = new FakeWarehouse();
        Navigator.Warehouse = warehouse;

        var mission = new Items.PathMission(Guid.NewGuid()) { LuId = loadUnitId, ExtractionQueueId = rowId };

        await mission.ProcessExtractionItems();

        Assert.Equal(902m, Assert.Single(warehouse.ConfirmedServedAssignments).AssignmentId);
        Assert.Empty(await ExtractionQueueQueries.ListAsync(processed: null, loadUnitId: loadUnitId, take: 10));
    }

    /// <summary>
    /// Le due guardie che restano di qua, com'erano: una riga di coda che non esiste e una riga di
    /// coda senza assegnazione non hanno niente da confermare. Sono la sola cosa che il motore
    /// decide su questo percorso.
    /// </summary>
    [Fact]
    public async Task AnExtractionWithoutAQueueRowOrWithoutAnAssignmentConfirmsNothing()
    {
        var rowId = await QueueRow(4714m, assignmentId: null);

        var warehouse = new FakeWarehouse();
        Navigator.Warehouse = warehouse;

        await DbInfo.ConfirmServedAssignmentsByExtractionId(0);
        await DbInfo.ConfirmServedAssignmentsByExtractionId(rowId);

        Assert.Empty(warehouse.ConfirmedServedAssignments);
        Assert.Empty(warehouse.Calls);
    }

    /// <summary>
    /// <b>Il punto in cui il comportamento cambia, e che questo test fissa.</b> Prima l'errore
    /// dell'esecuzione veniva inghiottito: <c>GetAssignedDataByExtractionId</c> scriveva una riga
    /// nel registro e proseguiva, senza marcare nessuno stato. La regola del confine conserva le
    /// degradazioni solo dove il codice marca uno stato che il motore gia' modella e mostra, e qui
    /// non e' il caso: adesso l'eccezione risale.
    /// <para>
    /// Il chiamante e' uno solo, <c>PathMission.ProcessExtractionItems</c>, e ha gia' un
    /// <c>try/catch</c> che registra e prosegue: la missione non resta «per aria», ma la riga di
    /// coda del WCS non viene piu' cancellata in quel giro, perche' la cancellazione sta dopo la
    /// conferma. Vedi il rapporto del compito 19.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AWarehouseFailureConfirmingTheAssignmentsSurfacesInsteadOfBeingSwallowed()
    {
        var rowId = await QueueRow(4715m, assignmentId: 901m);

        var warehouse = new FakeWarehouse { Unavailable = true };
        Navigator.Warehouse = warehouse;

        var ex = await Assert.ThrowsAsync<WarehouseUnavailableException>(
            () => DbInfo.ConfirmServedAssignmentsByExtractionId(rowId));

        Assert.Equal(nameof(IWarehouse.ConfirmServedAssignmentsAsync), ex.Operation);
    }
}
