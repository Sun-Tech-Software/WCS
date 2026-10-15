using Wcs.Engine.Items;
using Wcs.Engine.Items.Managers;
using Wcs.Wms.Contracts;
using Wcs.Wms.Contracts.Test;
using Xunit;

namespace Wcs.Engine.Test;

/// <summary>
/// La saldatura che <see cref="CellLockBlocksStoringTest"/> non prova: che il valore letto dal
/// gestionale arrivi fino a <see cref="AutomationDeviceStorageManager.BlocksStoring"/>, non solo
/// che quella funzione risponda bene a un valore scritto a mano dal test. E' il percorso di tre
/// righe dentro <see cref="AutomationDeviceStorageManager.VerifyStoreInfo"/> -
/// <c>var lockState = ... (await lu.DescribeCellAsync(cellId)).Locked; ... BlocksStoring(lockState)</c>
/// - il punto esatto dove il difetto originale del compito 7 (booleano al posto dell'enum a
/// quattro valori) si nascondeva.
///
/// Dove si ferma la copertura, e perche': questi due test partono da <see cref="FakeWarehouse"/>
/// e arrivano fino a <see cref="PathMission.DescribeCellAsync"/>, lo stesso metodo che
/// <c>VerifyStoreInfo</c> chiama davvero per leggere <c>Locked</c>. Non arrivano a
/// <c>VerifyStoreInfo</c> stesso: e' un metodo privato che dipende da un <c>AutomationDevice</c>
/// completo, da <c>DbInfo.GetCellId</c>/<c>IsCellBusyByExtraction</c> (un database vero) e da
/// <c>Navigator.WmsBusinessArea</c> (il gestionale interno, un canale diverso da
/// <see cref="IWarehouse"/>) - nessuno dei tre e' costruibile in un test unitario, come gia'
/// diceva il commento di <see cref="CellLockBlocksStoringTest"/>. Quello che resta scoperto e'
/// quindi solo l'istradamento interno a <c>VerifyStoreInfo</c> (la ricerca della cella per codice
/// porta, e la condizione composta con <c>IsCellBusyByExtraction</c>): la lettura del blocco dal
/// gestionale e la decisione che ne segue, in coppia, sono provate qui.
/// </summary>
public sealed class CellLockReachesStoringDecisionTest : IDisposable
{
    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;

    public void Dispose() => Navigator.Warehouse = _previousWarehouse;

    [Fact]
    public async Task ACellLockedForUnloadingReadFromTheWarehousePermitsStoring()
    {
        var warehouse = new FakeWarehouse();
        warehouse.Cells[21] = new CellDescription(21, 1, 1, Depth: 1, Inverted: false,
            Locked: CellLockState.Unloading, AllowedLoadUnitTypeId: null, GroupId: null, GroupOrder: 0,
            DoorCodeA: "A21", DoorCodeB: "B21", DoorCount: 1, MultiDepthStorage: false);
        Navigator.Warehouse = warehouse;

        var mission = new PathMission(Guid.NewGuid());
        var cellDescription = await mission.DescribeCellAsync(21);

        Assert.False(AutomationDeviceStorageManager.BlocksStoring(cellDescription.Locked));
    }

    [Fact]
    public async Task ACellLockedForLoadingReadFromTheWarehouseDeniesStoring()
    {
        var warehouse = new FakeWarehouse();
        warehouse.Cells[22] = new CellDescription(22, 1, 1, Depth: 1, Inverted: false,
            Locked: CellLockState.Loading, AllowedLoadUnitTypeId: null, GroupId: null, GroupOrder: 0,
            DoorCodeA: "A22", DoorCodeB: "B22", DoorCount: 1, MultiDepthStorage: false);
        Navigator.Warehouse = warehouse;

        var mission = new PathMission(Guid.NewGuid());
        var cellDescription = await mission.DescribeCellAsync(22);

        Assert.True(AutomationDeviceStorageManager.BlocksStoring(cellDescription.Locked));
    }
}
