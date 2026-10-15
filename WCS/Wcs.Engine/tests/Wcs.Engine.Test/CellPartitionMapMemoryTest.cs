using Wcs.Engine.Items;
using Wcs.Engine.Persistence;
using Wcs.Wms.Contracts;
using Wcs.Wms.Contracts.Test;
using Xunit;

namespace Wcs.Engine.Test;

/// <summary>
/// La mappa delle partizioni di una cella (compito 8): una sola chiamata al gestionale risponde alle
/// tre domande che prima erano tre letture separate (l'indirizzo di campo per codice porta, la
/// profondità della cella per una partizione, la profondità di stoccaggio per un indirizzo ASI), e la
/// memoria per missione la chiede una volta sola - stesso schema di
/// <see cref="CellDescriptionMemoryTest"/> per <see cref="IWarehouse.DescribeCellAsync"/>.
/// </summary>
public sealed class CellPartitionMapMemoryTest : IDisposable
{
    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly IWarehouse? _warehousePrecedente = Navigator.Warehouse;

    public CellPartitionMapMemoryTest()
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
    /// Le tre domande vecchie, risposte dalla stessa mappa presa una volta sola: l'indirizzo di campo
    /// (<see cref="PathMission.GetAsiCellInfoAsync"/>), la profondità della cella per una partizione
    /// (<see cref="PathMission.GetPartitionOffsetAsync"/>) e la profondità di stoccaggio per un
    /// indirizzo ASI (<see cref="PathMission.GetStorageDepthByAsiAsync"/>) tornano tutt'e tre dalla
    /// stessa <see cref="CellPartitionMap"/>, chiesta al gestionale una volta sola.
    /// </summary>
    [Fact]
    public async Task TheSamePartitionMapAnswersTheThreeOldQuestionsAndIsAskedOnce()
    {
        var warehouse = new FakeWarehouse();
        warehouse.Cells[21] = new CellDescription(21, 5, 1, Depth: 2, Inverted: false, Locked: CellLockState.None,
            AllowedLoadUnitTypeId: null, GroupId: null, GroupOrder: 0, DoorCodeA: "TM21", DoorCodeB: "B-TM21",
            DoorCount: 2, MultiDepthStorage: true);
        warehouse.CellPartitionMaps[21] = new CellPartitionMap(21, "ASI-A", "SUB-A", "ASI-B", "SUB-B",
            [new CellPartition("P1", 1), new CellPartition("P2", 2)]);
        Navigator.Warehouse = warehouse;

        var mission = await TestMissions.WithDestinationCell(21);

        // Domanda 1: l'indirizzo di campo per il codice porta, alla profondita' 2.
        var asi = await mission.GetAsiCellInfoAsync("TM21", 2);
        Assert.Equal("ASI-A", asi?.Asi);
        Assert.Equal("SUB-A", asi?.Subitem);
        Assert.Equal("P2", asi?.Partition);

        // Domanda 2: la profondita' della cella per la partizione "P1".
        var depth = await mission.GetPartitionOffsetAsync(21, "P1");
        Assert.Equal(1, depth);

        // Domanda 3: la profondita' di stoccaggio per l'indirizzo ASI della porta A.
        var storageDepth = await mission.GetStorageDepthByAsiAsync(21, "ASI-A", "SUB-A", "P2");
        Assert.Equal(2, storageDepth);

        Assert.Single(warehouse.Calls, c => c == nameof(IWarehouse.GetCellPartitionMapAsync));
    }

    /// <summary>
    /// Revisione del compito 8: <c>ASI_PORTA_A</c>/<c>SUBITEM_PORTA_A</c> non hanno un vincolo di
    /// unicita' nello schema, quindi <paramref name="cellId"/> potrebbe non essere la cella con
    /// l'indirizzo cercato (un'altra cella potrebbe averlo). <see cref="PathMission.GetStorageDepthByAsiAsync"/>
    /// non deve rispondere zero in questo caso - zero e' anche un offset legittimo - ma -1, cosi' il
    /// chiamante lo riconosce come "non trovato" e non come "profondita' zero".
    /// </summary>
    [Fact]
    public async Task StorageDepthByAsiReportsWhenTheResolvedCellHasADifferentDoorAAddress()
    {
        var warehouse = new FakeWarehouse();
        warehouse.CellPartitionMaps[21] = new CellPartitionMap(21, "ASI-A", "SUB-A", "ASI-B", "SUB-B",
            [new CellPartition("P1", 1), new CellPartition("P2", 2)]);
        Navigator.Warehouse = warehouse;

        var mission = await TestMissions.WithDestinationCell(21);

        // La missione cerca l'indirizzo "ASI-X"/"SUB-X", che non e' la porta A della cella 21: la cella
        // risolta da StorageDoorCode non e' quella che l'indirizzo richiesto identifica.
        var storageDepth = await mission.GetStorageDepthByAsiAsync(21, "ASI-X", "SUB-X", "P2");

        Assert.Equal(-1, storageDepth);
    }
}
