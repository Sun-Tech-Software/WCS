using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Wcs.Wms.Contracts;
using Wcs.Wms.Contracts.Test;
using Xunit;

namespace Wcs.Engine.Test;

/// <summary>
/// Le aree automatiche di deposito che hanno almeno una cella collegata a un dispositivo
/// (<see cref="LocalQueries.GetAutomaticAreasByWarehouseId"/>). Era l'ultima lettura della ricerca
/// di deposito che stava ancora su SQL Server: una <c>SELECT DISTINCT</c> che univa
/// <c>AREE_MAGAZZINO</c> e <c>CELLE_AREE</c> - tabelle del gestionale - alla mappa porte del WCS,
/// interpolando gli identificativi delle celle mappate dentro il testo della query.
/// <para>
/// Adesso sono due letture e un'intersezione in memoria: la topologia del magazzino la dice il
/// gestionale (<see cref="IWarehouse.GetAreaTopologyAsync"/>), le celle mappate le dice il database
/// del WCS, e il motore incrocia. Il pezzo che solo il motore puo' dare - «l'area ha almeno una
/// cella mappata su un dispositivo» - resta di qua, e il gestionale non ha bisogno di sapere niente
/// della mappa porte.
/// </para>
/// </summary>
/// <remarks>
/// Sostituisce <c>AutomaticAreasWithoutADatabaseTest</c>, che sorvegliava la guardia su
/// <c>Navigator.Database == null</c>: senza query non c'e' piu' nessun database del gestionale da
/// cui guardarsi, e il caso che quella prova descriveva - il motore che deve rispondere senza
/// aprire connessioni - e' diventato la regola di tutto il metodo.
/// </remarks>
public sealed class AutomaticAreasTest : IDisposable
{
    private const decimal WarehouseId = 1;

    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly FakeWarehouse _warehouse = new();

    public AutomaticAreasTest()
    {
        Navigator.Store = _database;
        Navigator.StateWriter = null;
        Navigator.Warehouse = _warehouse;
    }

    public void Dispose()
    {
        Navigator.Store = null;
        Navigator.Warehouse = _previousWarehouse;
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Collega al dispositivo una porta per ogni cella, come fa l'Editor.</summary>
    private static async Task MappedCells(params decimal[] cellIds)
    {
        var itemId = Guid.NewGuid();
        await PlantRows.EnsureAsync(itemId);

        await PlantPersistence.SaveDoorMappingsAsync(itemId,
            cellIds.Select(cellId => new DoorMapping
            {
                ItemId = itemId,
                DoorCode = $"P{cellId}A",
                CellId = cellId,
                Side = "A"
            }).ToList());
    }

    /// <summary>
    /// L'intersezione, con il caso del brief: tre aree automatiche, una cella per area, e solo due
    /// celle collegate a un dispositivo. Le aree che rispondono sono quelle delle due celle
    /// collegate - la terza esiste nel gestionale ma il motore non sa arrivarci.
    /// </summary>
    [Fact]
    public async Task OnlyTheAreasOfTheMappedCellsAnswer()
    {
        _warehouse.Topologies[WarehouseId] = new AreaTopology(WarehouseId,
            [10, 20, 30],
            new Dictionary<decimal, decimal> { [100] = 10, [200] = 20, [300] = 30 });

        await MappedCells(100, 300);

        Assert.Equal(new decimal[] { 10, 30 }, await LocalQueries.GetAutomaticAreasByWarehouseId(WarehouseId));
    }

    /// <summary>
    /// <b>Un'area che il gestionale non dichiara automatica non entra</b>, anche se una sua cella e'
    /// collegata a un dispositivo: era l'<c>AM.AUTOMATICA = 1</c> della vecchia <c>WHERE</c>, ed e'
    /// meta' della condizione. Senza questa prova il filtro potrebbe sparire e il risultato del caso
    /// qui sopra resterebbe lo stesso, perche' li' tutte le aree sono automatiche.
    /// </summary>
    [Fact]
    public async Task AMappedCellOfANonAutomaticAreaDoesNotBringItsArea()
    {
        _warehouse.Topologies[WarehouseId] = new AreaTopology(WarehouseId,
            [10],
            new Dictionary<decimal, decimal> { [100] = 10, [200] = 20 });

        await MappedCells(100, 200);

        Assert.Equal(new decimal[] { 10 }, await LocalQueries.GetAutomaticAreasByWarehouseId(WarehouseId));
    }

    /// <summary>
    /// Distinta come la <c>SELECT DISTINCT</c> di prima, e in ordine crescente, che prima <b>non
    /// era garantito</b>: la query cancellata non aveva nessun <c>ORDER BY</c> e l'ordine era quello
    /// che il database consegnava, quindi l'ordinamento e' un comportamento nuovo chiesto dal brief.
    /// Due celle della stessa area danno un identificativo solo, e l'ordine non e' quello in cui le
    /// celle sono state incontrate. La topologia e' costruita apposta perche' l'ordine naturale
    /// dell'attraversamento (30, 10, 10) sia diverso da quello atteso.
    /// </summary>
    [Fact]
    public async Task TheAreasAreDistinctAndInAscendingOrder()
    {
        _warehouse.Topologies[WarehouseId] = new AreaTopology(WarehouseId,
            [10, 30],
            new Dictionary<decimal, decimal> { [100] = 30, [300] = 10, [400] = 10 });

        await MappedCells(100, 300, 400);

        Assert.Equal(new decimal[] { 10, 30 }, await LocalQueries.GetAutomaticAreasByWarehouseId(WarehouseId));
    }

    /// <summary>
    /// Il magazzino zero non e' un magazzino: nessuna area, e la domanda non parte. Era gia' il
    /// primo guardiano della versione a query, e resta il primo qui - chiedere la topologia di un
    /// magazzino che non c'e' non ha senso nemmeno con il gestionale acceso.
    /// </summary>
    [Fact]
    public async Task WarehouseZeroAnswersNoAreasWithoutAskingTheWarehouse()
    {
        Assert.Empty(await LocalQueries.GetAutomaticAreasByWarehouseId(0));
        Assert.Empty(_warehouse.Calls);
    }

    /// <summary>
    /// Nessuna cella collegata: nessuna area, e <b>senza chiedere niente al gestionale</b>.
    /// L'intersezione con l'insieme vuoto e' vuota qualunque cosa risponda la topologia, quindi la
    /// risposta e' gia' nota dal solo database del WCS - e il vincolo del piano sulle domande al
    /// confine dice di non farne una che non serve, in un metodo che gira nel ciclo di estrazione
    /// di ogni magazzino.
    /// </summary>
    [Fact]
    public async Task APlantWithoutMappedCellsAnswersNoAreasWithoutAskingTheWarehouse()
    {
        Assert.Empty(await LocalQueries.GetAutomaticAreasByWarehouseId(WarehouseId));
        Assert.Empty(_warehouse.Calls);
    }

    /// <summary>
    /// <b>La prova che conta.</b> Il gestionale che non risponde non e' «nessuna area»: la vecchia
    /// lettura scriveva una riga nel registro e rispondeva una lista vuota, che per chi la riceve e'
    /// indistinguibile da un magazzino le cui aree automatiche non hanno celle collegate. E quella
    /// lista non resta qui: e' <c>ReserveStorageRequest.SearchAreaIds</c>, che il gestionale mette
    /// in <c>OpzioneRicercaCelle.ListaIdAreeRicerca</c> e passa come <c>@lsAREE_RICERCARE</c> alla
    /// ricerca della cella di deposito. Una lista <b>vuota</b> li' non restringe niente: la ricerca
    /// gira su <b>tutte</b> le aree del magazzino, comprese quelle non automatiche e quelle senza
    /// celle collegate a un dispositivo. Non e' una restrizione inventata da un guasto, e' una
    /// restrizione <b>sparita</b> per un guasto: la conseguenza non sarebbe «non depositare da
    /// nessuna parte», sarebbe «deposita ovunque». Ora il guasto risale.
    /// </summary>
    [Fact]
    public async Task AWarehouseThatDoesNotAnswerRaisesInsteadOfAnsweringNoAreas()
    {
        await MappedCells(100, 300);

        _warehouse.Unavailable = true;

        await Assert.ThrowsAsync<WarehouseUnavailableException>(
            () => LocalQueries.GetAutomaticAreasByWarehouseId(WarehouseId));
    }
}
