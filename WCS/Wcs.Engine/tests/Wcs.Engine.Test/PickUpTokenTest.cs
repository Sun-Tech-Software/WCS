using Wcs.Engine.Collections;
using Wcs.Engine.Items;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Wcs.Engine.Test.Routing;
using Wcs.Wms.Contracts;
using Wcs.Wms.Contracts.Test;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// I due gettoni del prelievo — la prenotazione della destinazione e <c>RunningStepId</c> — non si
/// prendono prima della lettura che puo' fallire.
/// </summary>
/// <remarks>
/// <para>
/// E' il guasto osservato in esercizio il 2026-09-09 su <c>SH01.0001.0001</c>: la mappa delle
/// partizioni della cella rispose <c>404</c>, la lettura sollevo' <b>dopo</b> che
/// <c>ConfigurePickUpStep</c> aveva gia' preso i due gettoni e <b>fuori</b> dal <c>try</c> che li
/// avrebbe restituiti. Il dispositivo resto' prenotato per se' stesso e rifiuto' ogni esecuzione
/// 2662 volte, finche' il motore non venne riavviato.
/// </para>
/// <para>
/// La riparazione e' l'ordine, non un guardiano in piu': l'indirizzo di campo non ha bisogno di
/// nessun gettone, quindi si legge prima. Quando la lettura fallisce non c'e' niente da restituire.
/// </para>
/// </remarks>
public sealed class PickUpTokenTest : IDisposable
{
    private const decimal CellId = 529;
    private const string PickUpDoorCode = "P529A";

    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly RoutingTestPlant _plant = new();
    private readonly FakeWarehouse _warehouse = new();

    public PickUpTokenTest()
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
    /// Un dispositivo scarico con una missione da prelevare dalla cella <see cref="CellId"/> e lo
    /// step di prelievo gia' pianificato: e' esattamente la terna di <c>SH01.0001.0001</c>, dove la
    /// destinazione dello step di prelievo e' il dispositivo stesso.
    /// </summary>
    private async Task<(AutomationDevice Device, PathMission Mission, MissionStep Step)> APlantAboutToPickUp()
    {
        var device = _plant.Device("A");
        var storage = _plant.Device("S", DeviceTypes.Storage);

        _plant.Link("S", "A");

        device.RecordStorageLength = RecordStorageTypes.FromPlc;
        device.LinkedCellId = CellId;

        await PlantPersistence.SaveDoorMappingsAsync(storage.ItemGuid,
            [new DoorMapping { ItemId = storage.ItemGuid, DoorCode = PickUpDoorCode, CellId = CellId, Side = "A" }]);

        var mission = new PathMission { LuId = 42, PickUpDoorCode = PickUpDoorCode };
        ActiveMissions.Instance.Add(mission, buildRoute: false);

        // L'aggiunta alla collezione attiva esce subito se il motore e' in Stop o in Pause: senza
        // questa verifica il resto del test proverebbe il contrario di cio' che dice il nome.
        Assert.NotNull(ActiveMissions.Instance[mission.MissionId]);

        var step = new MissionStep
        {
            StepId = Guid.NewGuid(),
            MissionId = mission.MissionId,
            Date = DateTime.Now,
            FromNodeId = storage.ItemGuid,
            ToNodeId = device.ItemGuid
        };
        MissionSteps.Instance.Add(step);

        Assert.Same(step, device.LuManager?.StepToDoDestination(mission));

        _warehouse.Cells[CellId] = Cell(CellId, PickUpDoorCode);
        _warehouse.CellPartitionMaps[CellId] = PartitionMap(CellId, "0001", "0002", "0003");

        return (device, mission, step);
    }

    /// <summary>
    /// Il collaudo del guasto vero: la lettura verso il gestionale fallisce e il dispositivo
    /// <b>non</b> resta prenotato.
    /// </summary>
    [Fact]
    public async Task AFailedReadLeavesNoTokenBehind()
    {
        var (device, mission, step) = await APlantAboutToPickUp();

        // Come il 404 sulle partizioni della cella 529.0: il gestionale risponde con un guasto
        // proprio nel giro in cui il prelievo sta per partire.
        _warehouse.Unavailable = true;

        Assert.NotNull(device.StepExecutor);
        await Assert.ThrowsAsync<WarehouseUnavailableException>(() => device.StepExecutor.StartPickUpLu(mission));

        Assert.Equal(Guid.Empty, device.RunningStepId);
        Assert.Equal(Guid.Empty, device.WaitingStepId);
        Assert.False(device.IsBusyWithCommand);

        // Il posto e' davvero libero, non solo dichiarato tale: un altro step lo prende.
        Assert.True(device.TryReserveForIncomingStep(Guid.NewGuid()));
        Assert.NotEqual(Guid.Empty, step.StepId);
    }

    /// <summary>
    /// L'altra meta' della regola: quando la lettura riesce i gettoni si prendono come prima e il
    /// prelievo parte. Senza questo, un «non parte mai» passerebbe il collaudo qui sopra.
    /// </summary>
    [Fact]
    public async Task ASuccessfulReadStillTakesTheTokens()
    {
        var (device, mission, step) = await APlantAboutToPickUp();

        Assert.NotNull(device.StepExecutor);
        await device.StepExecutor.StartPickUpLu(mission);

        Assert.Equal(step.StepId, device.RunningStepId);
        Assert.Equal(step.StepId, device.WaitingStepId);
        Assert.Same(device, mission.GatewayItem);
    }
}
