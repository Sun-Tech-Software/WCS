using Wcs.Engine.Items;
using Wcs.Wms.Contracts;
using Wcs.Wms.Contracts.Test;
using Xunit;

namespace Wcs.Engine.Test;

/// <summary>
/// Le due operazioni del compito 13, viste dal lato del motore: <see cref="PathMission.AllocateLoadUnitAsync"/>
/// e <see cref="PathMission.DescribeCellsByDoorAsync"/> (quest'ultima già del compito 9, qui usata da
/// <c>PathMission.Methods.AllocateOnStorage</c> per decidere la quota di deposito prima del
/// deposito vero e proprio). <c>PathMission.Methods.AllocateOnItem</c>/<c>AllocateOnStorage</c> e
/// <c>AutomationDeviceLuManager.ProcessLuBusinessLogic</c> restano fuori dalla portata di un test
/// unitario - hanno bisogno di un <see cref="AutomationDevice"/> completo, stesso limite già
/// documentato in <c>ReserveStorageOutcomeTest</c>/<c>CellLockReachesStoringDecisionTest</c>. Il
/// collaudo del guasto a metà (le due operazioni non sono transazionali, voce D118 di
/// <c>WCS/doc/debito-tecnico.md</c>) vive in <c>WMS.Services.Test.MagazzinoPerWcsTest</c>, dove la
/// scrittura succede davvero.
/// </summary>
public sealed class LoadUnitAllocationTest : IDisposable
{
    private readonly IWarehouse? _warehousePrecedente = Navigator.Warehouse;

    public void Dispose() => Navigator.Warehouse = _warehousePrecedente;

    /// <summary>
    /// <see cref="PathMission.AllocateLoadUnitAsync"/> passa la richiesta cosi' com'e' al gestionale:
    /// non ricalcola, non filtra - la stessa richiesta che <c>AllocateOnItem</c>/<c>AllocateOnStorage</c>
    /// costruiscono arriva intatta a <see cref="IWarehouse.AllocateLoadUnitAsync"/>.
    /// </summary>
    [Fact]
    public async Task AllocateLoadUnitAsyncSendsTheRequestToTheWarehouseUnchanged()
    {
        var warehouse = new FakeWarehouse();
        Navigator.Warehouse = warehouse;

        var mission = new PathMission(Guid.NewGuid());
        var request = new AllocateLoadUnitRequest(LoadUnitId: 42, CellId: 7, StorageDoorCode: "A7",
            ResetAssignment: true, Manual: false, StorageDepth: 150, DetectedDepth: 148, RequestedByUserId: "user1");

        await mission.AllocateLoadUnitAsync(request);

        var sent = Assert.Single(warehouse.AllocatedLoadUnits);
        Assert.Equal(request, sent);
        Assert.Equal([nameof(IWarehouse.AllocateLoadUnitAsync)], warehouse.Calls);
    }

    /// <summary>
    /// Senza <see cref="Navigator.Warehouse"/> configurato l'operazione lo dice, non lo aggira -
    /// stesso guardiano già scritto per le altre operazioni di <see cref="PathMission"/> (compiti
    /// 7-12).
    /// </summary>
    [Fact]
    public async Task AllocateLoadUnitAsyncThrowsWhenWarehouseIsNotConfigured()
    {
        Navigator.Warehouse = null;

        var mission = new PathMission(Guid.NewGuid());
        var request = new AllocateLoadUnitRequest(42, 7, "A7", true, false, 150, 148, "user1");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => mission.AllocateLoadUnitAsync(request));

        Assert.Contains("AllocateLoadUnitAsync", ex.Message);
        Assert.Contains(mission.MissionId.ToString(), ex.Message);
    }

    /// <summary>
    /// <see cref="PathMission.DescribeCellsByDoorAsync"/>: la stessa lettura di
    /// <see cref="IWarehouse.DescribeCellsByDoorAsync"/> (compito 9), qui passando dalla missione
    /// perché <c>AllocateOnStorage</c> (compito 13) la interroga prima di decidere la quota di
    /// deposito.
    /// </summary>
    [Fact]
    public async Task DescribeCellsByDoorAsyncOnAMissionAnswersWithTheCellsOfThatDoor()
    {
        var warehouse = new FakeWarehouse();
        warehouse.CellsByDoor["A7"] =
        [
            new CellDescription(7, 1, 1, Depth: 300, Inverted: false, Locked: CellLockState.None,
                AllowedLoadUnitTypeId: null, GroupId: null, GroupOrder: 0, DoorCodeA: "A7", DoorCodeB: "",
                DoorCount: 1, MultiDepthStorage: true)
        ];
        Navigator.Warehouse = warehouse;

        var mission = new PathMission(Guid.NewGuid());

        var cells = await mission.DescribeCellsByDoorAsync("A7");

        var cell = Assert.Single(cells);
        Assert.Equal(7m, cell.CellId);
        Assert.True(cell.MultiDepthStorage);
    }

    [Fact]
    public async Task DescribeCellsByDoorAsyncThrowsWhenWarehouseIsNotConfigured()
    {
        Navigator.Warehouse = null;

        var mission = new PathMission(Guid.NewGuid());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => mission.DescribeCellsByDoorAsync("A7"));

        Assert.Contains("DescribeCellsByDoorAsync", ex.Message);
    }
}
