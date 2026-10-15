using Wcs.Engine.ORM;
using Wcs.Wms.Contracts;
using Wcs.Wms.Contracts.Test;
using Xunit;

namespace Wcs.Engine.Test;

/// <summary>
/// Tre letture che il motore faceva ancora in T-SQL diretto sulle tabelle del gestionale, e che il
/// confine risponde gia' — stessa tabella, stessa chiave, stesso filtro.
/// </summary>
/// <remarks>
/// <para>
/// <c>GetIdCellLocationByLuId</c> e <c>GetLuStoringDepth</c> leggevano le due colonne di
/// <c>POSIZIONI_UDC</c> che <see cref="IWarehouse.LocateLoadUnitAsync"/> porta insieme, con la
/// stessa chiave e lo stesso <c>ASSEGNAZIONE = 0</c>; <c>GetLuDepth</c> leggeva
/// <c>UDC.PROFONDITA</c>, che e' il <c>Depth</c> di <see cref="LoadUnitDescription"/> — la quarta
/// lettura sparsa sulla riga che quel contratto dichiara di aver sostituito. C'erano due strade per
/// lo stesso dato, e in un punto erano adiacenti: <c>AutomationDevice.AddMissionToPickUp</c> chiama
/// <c>DescribeLoadUnitAsync</c> e dieci righe dopo leggeva la profondita' di stoccaggio in T-SQL.
/// </para>
/// <para>
/// <b>Cambia il modo di rompersi:</b> prima un guasto della connessione tornava zero — «non e' a
/// magazzino» — indistinguibile da un dato; adesso solleva, che e' la regola del confine.
/// </para>
/// <para>
/// Queste prove dicevano anche «e non c'e' nessun SQL Server», con un
/// <c>Assert.Null(Navigator.Database)</c> in coda. Quel campo non esiste piu': il motore non
/// referenzia <c>MsSqlHelper</c>, e a tenere chiusa la porta e' <c>EngineDependenciesTest</c>.
/// </para>
/// </remarks>
public sealed class DirectReadsMovedToTheBoundaryTest : IDisposable
{
    private const decimal LoadUnitId = 4242;

    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly FakeWarehouse _warehouse = new();

    public DirectReadsMovedToTheBoundaryTest()
    {
        Navigator.Warehouse = _warehouse;
    }

    public void Dispose()
    {
        Navigator.Warehouse = _previousWarehouse;
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task TheCellAndTheStoringDepthComeFromTheBoundary()
    {
        _warehouse.LoadUnitLocations[LoadUnitId] = new LoadUnitLocation(LoadUnitId, CellId: 77, StoringDepth: 3);

        Assert.Equal(77m, await DbInfo.GetIdCellLocationByLuId(LoadUnitId));
        Assert.Equal(3, await DbInfo.GetLuStoringDepth(LoadUnitId));

        Assert.Contains(nameof(IWarehouse.LocateLoadUnitAsync), _warehouse.Calls);
    }

    [Fact]
    public async Task TheLoadUnitDepthComesFromTheBoundary()
    {
        _warehouse.LoadUnits[LoadUnitId] = new LoadUnitDescription(LoadUnitId, "UDC-4242", LoadUnitTypeId: 1,
            HandlingMode: 0, Tare: 0, Height: 100, Width: 80, Depth: 120, Weight: 250);

        Assert.Equal(120, await DbInfo.GetLuDepth(LoadUnitId));

        Assert.Contains(nameof(IWarehouse.DescribeLoadUnitAsync), _warehouse.Calls);
    }

    /// <summary>
    /// L'unita' di carico che il gestionale non conosce: zero, che e' la risposta di prima e quella
    /// che i chiamanti si aspettano. E' un'assenza di dominio, non un guasto.
    /// </summary>
    [Fact]
    public async Task ALoadUnitTheWarehouseDoesNotKnowAnswersZero()
    {
        Assert.Equal(0m, await DbInfo.GetIdCellLocationByLuId(LoadUnitId));
        Assert.Equal(0, await DbInfo.GetLuStoringDepth(LoadUnitId));
        Assert.Equal(0, await DbInfo.GetLuDepth(LoadUnitId));
    }

    /// <summary>
    /// Il gestionale che non risponde solleva, e non risponde zero: uno zero qui vorrebbe dire «non
    /// e' a magazzino», che e' un dato, non un guasto.
    /// </summary>
    [Fact]
    public async Task AWarehouseThatDoesNotAnswerRaises()
    {
        _warehouse.Unavailable = true;

        await Assert.ThrowsAsync<WarehouseUnavailableException>(() => DbInfo.GetIdCellLocationByLuId(LoadUnitId));
        await Assert.ThrowsAsync<WarehouseUnavailableException>(() => DbInfo.GetLuStoringDepth(LoadUnitId));
        await Assert.ThrowsAsync<WarehouseUnavailableException>(() => DbInfo.GetLuDepth(LoadUnitId));
    }

    /// <summary>
    /// La guardia che resta: l'unita' di carico zero non e' una domanda, e non attraversa il
    /// confine.
    /// </summary>
    [Fact]
    public async Task LoadUnitZeroDoesNotCrossTheBoundary()
    {
        Assert.Equal(0m, await DbInfo.GetIdCellLocationByLuId(0));
        Assert.Equal(0, await DbInfo.GetLuStoringDepth(0));
        Assert.Equal(0, await DbInfo.GetLuDepth(0));

        Assert.Empty(_warehouse.Calls);
    }
}
