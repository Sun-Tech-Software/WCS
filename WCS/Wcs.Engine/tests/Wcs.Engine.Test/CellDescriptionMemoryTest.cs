using Wcs.Engine.Items;
using Wcs.Engine.Persistence;
using Wcs.Wms.Contracts;
using Wcs.Wms.Contracts.Test;
using Xunit;

namespace Wcs.Engine.Test;

/// <summary>
/// La memoria per missione delle celle decodificate (compito 5): la stessa cella non torna a
/// chiedere al gestionale una seconda volta nella stessa missione, e missioni diverse non si
/// scambiano la cache.
/// </summary>
public sealed class CellDescriptionMemoryTest : IDisposable
{
    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly IWarehouse? _warehousePrecedente = Navigator.Warehouse;

    public CellDescriptionMemoryTest()
    {
        Navigator.Store = _database;
    }

    public void Dispose()
    {
        Navigator.Store = null;
        Navigator.Warehouse = _warehousePrecedente;
        _database.Dispose();
    }

    [Fact]
    public async Task TheSameCellIsDescribedOnceForTheWholeMission()
    {
        var warehouse = new FakeWarehouse();
        warehouse.Cells[7] = new CellDescription(7, 1, 1, Depth: 2, Inverted: false, Locked: CellLockState.None,
            AllowedLoadUnitTypeId: null, GroupId: 3, GroupOrder: 1, DoorCodeA: "A7", DoorCodeB: "B7",
            DoorCount: 1, MultiDepthStorage: false);
        Navigator.Warehouse = warehouse;

        var mission = await TestMissions.WithDestinationCell(7);

        var prima = await mission.DescribeDestinationAsync();
        var seconda = await mission.DescribeDestinationAsync();

        Assert.Same(warehouse.Cells[7], prima);
        Assert.Same(prima, seconda);
        Assert.Single(warehouse.Calls, c => c == nameof(IWarehouse.DescribeCellAsync));
    }

    /// <summary>
    /// Il complemento del test sopra: senza la memoria, due missioni che chiedono la stessa cella
    /// passerebbero comunque, e questo test non lo dimostrerebbe. Con la memoria per <b>missione</b>
    /// (non globale) ciascuna missione chiede la propria volta, e le due chiamate restano due.
    /// </summary>
    [Fact]
    public async Task TwoDifferentMissionsEachAskOnceForTheSameCell()
    {
        var warehouse = new FakeWarehouse();
        warehouse.Cells[9] = new CellDescription(9, 1, 1, Depth: 1, Inverted: false, Locked: CellLockState.None,
            AllowedLoadUnitTypeId: null, GroupId: null, GroupOrder: 0, DoorCodeA: "A9", DoorCodeB: "B9",
            DoorCount: 1, MultiDepthStorage: false);
        Navigator.Warehouse = warehouse;

        var prima = await TestMissions.WithDestinationCell(9);
        var seconda = await TestMissions.WithDestinationCell(9);

        await prima.DescribeDestinationAsync();
        await seconda.DescribeDestinationAsync();

        Assert.Equal(2, warehouse.Calls.Count(c => c == nameof(IWarehouse.DescribeCellAsync)));
    }

    /// <summary>
    /// Il caso generale, non solo la cella di deposito: <see cref="PathMission.DescribeCellAsync"/>
    /// e' quello che i chiamanti veri usano (una cella qualunque, non necessariamente
    /// <c>StorageDoorCode</c>), e la memoria funziona ugualmente su un id passato direttamente.
    /// </summary>
    [Fact]
    public async Task TheGeneralLookupByCellIdIsMemoizedToo()
    {
        var warehouse = new FakeWarehouse();
        warehouse.Cells[11] = new CellDescription(11, 2, 1, Depth: 3, Inverted: true, Locked: CellLockState.Full,
            AllowedLoadUnitTypeId: 5, GroupId: null, GroupOrder: 0, DoorCodeA: "A11", DoorCodeB: "B11",
            DoorCount: 2, MultiDepthStorage: true);
        Navigator.Warehouse = warehouse;

        var mission = new PathMission(Guid.NewGuid());

        await mission.DescribeCellAsync(11);
        await mission.DescribeCellAsync(11);
        await mission.DescribeCellAsync(11);

        Assert.Single(warehouse.Calls, c => c == nameof(IWarehouse.DescribeCellAsync));
    }
}
