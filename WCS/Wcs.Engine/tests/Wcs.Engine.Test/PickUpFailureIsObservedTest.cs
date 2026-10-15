using Wcs.Engine.Collections;
using Wcs.Engine.Items;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Wcs.Engine.Items.ExecutionStrategies;
using Wcs.Engine.Test.Routing;
using Wcs.Wms.Contracts;
using Wcs.Wms.Contracts.Test;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// Un prelievo che fallisce dev'essere <b>osservato</b>: la strategia standard attende il prelievo
/// invece di lanciarlo e dimenticarlo.
/// </summary>
/// <remarks>
/// <para>
/// <c>Standard.ExecuteAsync</c> chiamava <c>CheckGetFromItem</c> senza <c>await</c> — l'unico dei
/// quattro punti di chiamata a farlo. L'eccezione del 2026-09-09 fini' in un <c>Task</c> che
/// nessuno osservava e .NET la scarto' in silenzio: il registro non disse niente per un'ora e
/// mezza, ed e' per questo che il guasto non si vedeva.
/// </para>
/// <para>
/// La prova non usa attese a tempo e non passa da <c>ExecuteStep</c>: la strategia si costruisce e
/// si esegue direttamente, cosi' il collaudo guarda la riga cambiata e non il semaforo del
/// dispositivo — che il passaggio del motore in marcia fa prendere a un'esecuzione automatica, e
/// con l'attesa quella dura adesso quanto tutto il prelievo. Senza <c>await</c>
/// <c>ExecuteAsync</c> torna senza sollevare mentre il prelievo muore; con l'attesa l'eccezione
/// arriva a chi l'ha chiamata, cioe' al <c>catch</c> di <c>ExecuteStep</c> che la scrive.
/// </para>
/// </remarks>
public sealed class PickUpFailureIsObservedTest : IDisposable
{
    private const decimal CellId = 529;
    private const string PickUpDoorCode = "P529A";

    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly RoutingTestPlant _plant = new();
    private readonly FakeWarehouse _warehouse = new();

    public PickUpFailureIsObservedTest()
    {
        Navigator.Store = _database;
        Navigator.Warehouse = _warehouse;
    }

    public void Dispose()
    {
        _plant.Dispose();
        Navigator.Warehouse = _previousWarehouse;
        Navigator.Store = null;
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    private static CellDescription Cell(decimal cellId, string doorCode) =>
        new(cellId, AreaId: 1, WarehouseId: 1, Depth: 1, Inverted: false, CellLockState.None,
            AllowedLoadUnitTypeId: null, GroupId: null, GroupOrder: 0, doorCode, DoorCodeB: string.Empty,
            DoorCount: 1, MultiDepthStorage: false);

    private static CellPartitionMap PartitionMap(decimal cellId, string asi, string subitem, string partition) =>
        new(cellId, asi, subitem, AsiDoorB: string.Empty, SubitemDoorB: string.Empty,
            [new CellPartition(partition, -1)]);

    /// <summary>
    /// Un dispositivo scarico con una UDC gia' presa in carico (<c>LUtoPick</c>) e lo step di
    /// prelievo pianificato: e' il ramo che <c>Standard.ExecuteAsync</c> imbocca quando il
    /// dispositivo non ha niente a bordo. <c>LuId</c> resta a zero perche' <c>VerifyPickUp</c>
    /// confermi senza interrogare il gestionale: l'unica lettura di questo giro e' quella
    /// dell'indirizzo di campo, che e' il punto in prova.
    /// </summary>
    private async Task<AutomationDevice> ADeviceAboutToPickUp()
    {
        var device = _plant.Device("A");
        var storage = _plant.Device("S", DeviceTypes.Storage);

        _plant.Link("S", "A");

        device.RecordStorageLength = RecordStorageTypes.FromPlc;
        device.LinkedCellId = CellId;

        await PlantPersistence.SaveDoorMappingsAsync(storage.ItemGuid,
            [new DoorMapping { ItemId = storage.ItemGuid, DoorCode = PickUpDoorCode, CellId = CellId, Side = "A" }]);

        var mission = new PathMission { PickUpDoorCode = PickUpDoorCode };
        ActiveMissions.Instance.Add(mission, buildRoute: false);
        Assert.NotNull(ActiveMissions.Instance[mission.MissionId]);

        MissionSteps.Instance.Add(new MissionStep
        {
            StepId = Guid.NewGuid(),
            MissionId = mission.MissionId,
            Date = DateTime.Now,
            FromNodeId = storage.ItemGuid,
            ToNodeId = device.ItemGuid
        });

        device.EnqueueLuToPickUp(mission);
        Assert.NotNull(device.TryTakeLuToPickUp(_ => true));
        Assert.Same(mission, device.LUtoPick);

        _warehouse.Cells[CellId] = Cell(CellId, PickUpDoorCode);
        _warehouse.CellPartitionMaps[CellId] = PartitionMap(CellId, "0001", "0002", "0003");

        return device;
    }

    /// <summary>
    /// La lettura fallisce e chi ha avviato il prelievo lo sa: l'eccezione risale invece di
    /// svanire in un compito non osservato.
    /// </summary>
    [Fact]
    public async Task AFailedPickUpReachesTheCaller()
    {
        var device = await ADeviceAboutToPickUp();

        _warehouse.UnavailableOnCells.Add(CellId);

        var strategy = new Standard(device);

        await Assert.ThrowsAsync<WarehouseUnavailableException>(
            () => strategy.ExecuteAsync(CancellationToken.None));
    }

    /// <summary>
    /// L'altra meta' della regola: quando il prelievo riesce la strategia lo porta a termine e il
    /// dispositivo risulta impegnato dal prelievo. Senza questo, un percorso che non parte mai
    /// passerebbe il collaudo qui sopra.
    /// </summary>
    [Fact]
    public async Task ASuccessfulPickUpIsCarriedOutBeforeTheStrategyReturns()
    {
        var device = await ADeviceAboutToPickUp();

        var strategy = new Standard(device);

        await strategy.ExecuteAsync(CancellationToken.None);

        Assert.NotEqual(Guid.Empty, device.RunningStepId);
        Assert.Equal(DeviceStatusTypes.Loading, device.Status);
    }
}
