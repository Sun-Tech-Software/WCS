using Wcs.Engine.ORM;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Wcs.Wms.Contracts;
using Wcs.Wms.Contracts.Test;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// Il passo 1 del compito 9: la funzione che azzera lo stato delle operazioni su una cella dà lo
/// stesso esito di oggi nei quattro casi (cella libera, in prelievo, in deposito, in entrambi).
/// Esiste in due copie: <see cref="Navigator.ResetCellOperationsStatus"/> (per unità di carico) e
/// <see cref="DbInfo.ResetCellOperationsStatus"/> (per cella, chiamata da <see cref="DbInfo.CheckOperation"/>
/// - resa <c>internal</c> per questo test, vedi il suo commento). Entrambe ora leggono
/// <see cref="IWarehouse.GetCellBusyStateAsync"/> invece delle vecchie <c>IsCellBusyByPickUp</c>/
/// <c>IsCellBusyByStoring</c>, e condividono la stessa regola di decisione,
/// <see cref="Navigator.ShouldClearCellOperations"/>.
/// </summary>
public sealed class CellOperationsResetTest : IDisposable
{
    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly IWarehouse? _warehousePrecedente = Navigator.Warehouse;

    public CellOperationsResetTest()
    {
        Navigator.Store = _database;
    }

    public void Dispose()
    {
        Navigator.Store = null;
        Navigator.Warehouse = _warehousePrecedente;
        _database.Dispose();
    }

    /// <summary>
    /// La regola condivisa dalle due copie, nei quattro casi del brief: libera solo se non è
    /// occupata né per prelievo né per deposito. Lo stato di estrazione non entra in questa
    /// decisione (nessuna delle due copie lo guarda), quindi resta fisso a falso.
    /// </summary>
    [Theory]
    [InlineData(false, false, true)]  // cella libera -> si azzera
    [InlineData(true, false, false)]  // in prelievo -> resta impegnata
    [InlineData(false, true, false)]  // in deposito -> resta impegnata
    [InlineData(true, true, false)]   // entrambi -> resta impegnata
    public void SharedDecisionMatchesTheFourCases(bool pickUp, bool storing, bool expectedClear) =>
        Assert.Equal(expectedClear, Navigator.ShouldClearCellOperations(new CellBusyState(Extraction: false, pickUp, storing)));

    /// <summary>
    /// La copia per cella (<see cref="DbInfo.ResetCellOperationsStatus"/>), end-to-end: una riga di
    /// mappa porte con l'operazione impostata a "Prelievo", una riga in coda di estrazione locale
    /// (cosi' il cancello di <see cref="DbInfo.NumExtractionsByCellId"/> resta aperto in tutt'e
    /// quattro i casi, isolando l'effetto di <see cref="CellBusyState.PickUp"/>/<see cref="CellBusyState.Storing"/>),
    /// e un <see cref="FakeWarehouse"/> che risponde l'occupazione voluta. Dopo la chiamata, la cella
    /// non compare più fra quelle con operazione attiva se e solo se ci si aspettava l'azzeramento.
    /// </summary>
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public async Task TheCellCopyClearsOnlyWhenNeitherPickUpNorStoringIsBusy(bool pickUp, bool storing, bool expectedClear)
    {
        const decimal cellId = 501;
        var doorCode = $"COR{cellId}";

        var warehouse = new FakeWarehouse();
        warehouse.CellBusyStates[cellId] = new CellBusyState(Extraction: true, pickUp, storing);
        Navigator.Warehouse = warehouse;

        var item = Guid.NewGuid();
        await PlantRows.EnsureAsync(item);
        await PlantPersistence.SaveDoorMappingsAsync(item,
            [new DoorMapping { ItemId = item, DoorCode = doorCode, CellId = cellId, Side = "A", OperationActive = (short)StatoOperazioneCella.Prelievo }]);

        await ExtractionQueue.UpsertAsync(new ExtractionQueueItem
        {
            CellId = cellId, SourceItemId = item, WorkstationId = "WS", DoorCode = doorCode, Processed = false
        });

        await DbInfo.ResetCellOperationsStatus(cellId);

        var stillActive = await ExtractionQueue.ReadAsync(
            context => Persistence.DoorMappingStore.CellsWithActiveOperationAsync(context), []);

        Assert.Equal(!expectedClear, stillActive.Contains(cellId));
        Assert.Single(warehouse.Calls, c => c == nameof(IWarehouse.GetCellBusyStateAsync));
    }

    /// <summary>
    /// La copia per unità di carico (<see cref="Navigator.ResetCellOperationsStatus"/>) non ha modo
    /// di essere provata end-to-end nei quattro casi con un'unità di carico vera: risolve la cella
    /// da <c>DbInfo.GetIdCellLocationByLuId</c>, che fino al compito 12 leggeva
    /// <c>Navigator.Database</c> (un vero SQL Server, assente nei test) appena
    /// <paramref name="luId"/> non è zero - lo stesso limite già incontrato per
    /// <c>VerifyStoreInfo</c> (compito 7). Oggi quella lettura passa dal confine, quindi il limite
    /// storico non c'è più e la copertura end-to-end si potrebbe allargare: non è lavoro di questo
    /// compito, che toglie SQL Server al motore e non aggiunge collaudi. Il tratto verificato qui
    /// resta la guardia su un'unità di carico assente: nessuna chiamata al gestionale, nessuna
    /// eccezione.
    /// La regola di decisione che userebbe (<see cref="Navigator.ShouldClearCellOperations"/>) è la
    /// stessa provata sopra per l'altra copia - condivisa, non duplicata a mano.
    /// </summary>
    [Fact]
    public async Task TheLoadUnitCopyDoesNothingWithoutALoadUnit()
    {
        var warehouse = new FakeWarehouse();
        Navigator.Warehouse = warehouse;

        await Navigator.ResetCellOperationsStatus(0);

        Assert.Empty(warehouse.Calls);
    }
}
