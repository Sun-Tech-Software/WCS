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
/// Voce D152: il movimento raggruppato prende il posto sulle destinazioni come fa il movimento
/// singolo (<c>StartPutToMOVE_LU</c>, voce D73), e lo prende <b>prima</b> delle letture verso il
/// gestionale. Prima non lo prendeva affatto, e marcava le destinazioni occupate solo dopo le
/// letture: per tutta la durata di quelle chiamate un terzo mittente poteva spedire allo stesso
/// punto.
/// </summary>
/// <remarks>
/// La scena e' quella di <see cref="GroupedStoreTokenTest"/> con uno step in piu': uno verso il
/// magazzino, che e' quello che fa leggere l'indirizzo di campo, e uno verso un dispositivo di
/// automazione, che e' quello da prenotare. Lo step verso il magazzino sta <b>prima</b> nella
/// lista, cosi' una prenotazione presa step per step dentro il ciclo arriverebbe dopo la lettura.
/// </remarks>
public sealed class GroupedMoveReservationTest : IDisposable
{
    private const decimal CellId = 730;
    private const string StorageDoorCode = "P730A";

    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly RoutingTestPlant _plant = new();
    private readonly FakeWarehouse _warehouse = new();

    public GroupedMoveReservationTest()
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

    private static MissionStep Step(PathMission mission, AutomationDevice from, AutomationDevice to)
    {
        var step = new MissionStep
        {
            StepId = Guid.NewGuid(),
            MissionId = mission.MissionId,
            Date = DateTime.Now,
            FromNodeId = from.ItemGuid,
            ToNodeId = to.ItemGuid
        };
        MissionSteps.Instance.Add(step);
        return step;
    }

    private async Task<(AutomationDevice Group, AutomationDevice Destination, MissionStep ToDestination, MissionStep ToStorage)> AGroupedMoveAboutToStart()
    {
        var group = _plant.Device("G");
        var storage = _plant.Device("S", DeviceTypes.Storage);
        var destination = _plant.Device("D");

        _plant.Link("G", "S");
        _plant.Link("G", "D");

        group.RecordStorageLength = RecordStorageTypes.FromPlc;

        await PlantPersistence.SaveDoorMappingsAsync(storage.ItemGuid,
            [new DoorMapping { ItemId = storage.ItemGuid, DoorCode = StorageDoorCode, CellId = CellId, Side = "A" }]);

        var storing = new PathMission { LuId = 42, StorageDoorCode = StorageDoorCode, DestinationItemId = storage.ItemGuid };
        var moving = new PathMission { LuId = 43, DestinationItemId = destination.ItemGuid };
        ActiveMissions.Instance.Add(storing, buildRoute: false);
        ActiveMissions.Instance.Add(moving, buildRoute: false);

        var toStorage = Step(storing, group, storage);
        var toDestination = Step(moving, group, destination);

        _warehouse.Cells[CellId] = Cell(CellId, StorageDoorCode);
        _warehouse.CellContents[CellId] = [7]; // la cella non e' vuota: la verifica di allocazione si ferma prima
        _warehouse.CellPartitionMaps[CellId] = new CellPartitionMap(CellId, "0001", "0002",
            AsiDoorB: string.Empty, SubitemDoorB: string.Empty, [new CellPartition("0003", -1)]);

        return (group, destination, toDestination, toStorage);
    }

    /// <summary>
    /// Mentre il gestionale risponde alla PRIMA domanda del movimento raggruppato, un terzo che
    /// prova a prendere la destinazione la trova gia' presa; e alla fine il posto e' dello step
    /// del gruppo.
    /// </summary>
    /// <remarks>
    /// Il gancio sta sulla prima operazione verso il gestionale, qualunque sia, e non su una per
    /// nome: la prima versione di questo collaudo guardava <c>DescribeCellAsync</c>, l'indirizzo di
    /// campo, ed era cieca sulla verifica di allocazione (<c>CheckCellStoreStatus</c> →
    /// <c>GetCellContentAsync</c>) che viene prima e che la prenotazione non copriva ancora. Il
    /// nome della prima operazione si verifica anche su <see cref="FakeWarehouse.Calls"/>, che
    /// conta tutte le chiamate: cosi' una prima lettura senza gancio non passerebbe inosservata.
    /// </remarks>
    [Fact]
    public async Task TheDestinationIsReservedBeforeTheWarehouseIsAsked()
    {
        var (group, destination, toDestination, toStorage) = await AGroupedMoveAboutToStart();

        string? firstOperation = null;
        bool? competitorAccepted = null;
        _warehouse.Before = operation =>
        {
            if (firstOperation != null) return Task.CompletedTask;

            firstOperation = operation;
            competitorAccepted = destination.TryReserveForIncomingStep(Guid.NewGuid());
            return Task.CompletedTask;
        };

        Assert.NotNull(group.StepExecutor);
        await group.StepExecutor.ExecuteGroupedMOVE_LU_TO([toStorage, toDestination]);

        Assert.Equal(nameof(IWarehouse.GetCellContentAsync), _warehouse.Calls[0]);
        Assert.Equal(_warehouse.Calls[0], firstOperation);
        Assert.False(competitorAccepted, "il terzo mittente non doveva ottenere il posto durante la prima lettura");
        Assert.True(destination.IsWaitingForStep(toDestination.StepId));
    }

    /// <summary>
    /// Il completamento del gruppo si interrompe a meta' (la lettura verso il magazzino di uno
    /// step fallisce) e il posto dello step che non e' stato lavorato torna libero lo stesso.
    /// </summary>
    /// <remarks>
    /// <c>StepDoneGroup</c> esce dal <c>catch</c> senza <c>RemoveStepsByGroupedMovementId</c>:
    /// gli step restano in <c>MissionSteps</c>, e la rete delle prenotazioni appese
    /// (<c>TryReleaseHungIncomingReservation</c>) scioglie solo i posti di step che non esistono
    /// piu'. Quindi il rilascio non puo' dipendere dal successo degli step precedenti. La
    /// collezione enumera gli step in un ordine suo: quale dei due sia quello che fallisce lo
    /// decide il collaudo dopo averla guardata, cosi' il fallimento viene sempre prima.
    /// </remarks>
    [Fact]
    public async Task AFailureHalfwayThroughTheGroupCompletionStillGivesEveryReservationBack()
    {
        var group = _plant.Device("G");
        var storageSide = _plant.Device("A1");
        var destinationSide = _plant.Device("A2");
        var storage = _plant.Device("S", DeviceTypes.Storage);
        var destination = _plant.Device("D");

        group.GroupItemId = 1;
        storageSide.BelongingGroupId = 1;
        destinationSide.BelongingGroupId = 1;

        _plant.Link("A1", "S");
        _plant.Link("A2", "D");

        await PlantPersistence.SaveDoorMappingsAsync(storage.ItemGuid,
            [new DoorMapping { ItemId = storage.ItemGuid, DoorCode = StorageDoorCode, CellId = CellId, Side = "A" }]);

        var storing = new PathMission { LuId = 42, StorageDoorCode = StorageDoorCode, DestinationItemId = storage.ItemGuid };
        var moving = new PathMission { LuId = 43, DestinationItemId = destination.ItemGuid };
        ActiveMissions.Instance.Add(storing, buildRoute: false);
        ActiveMissions.Instance.Add(moving, buildRoute: false);

        var token = Guid.NewGuid();
        MissionSteps.Instance.Add(new MissionStep { StepId = Guid.NewGuid(), GroupedMovementId = token, Date = DateTime.Now });
        MissionSteps.Instance.Add(new MissionStep { StepId = Guid.NewGuid(), GroupedMovementId = token, Date = DateTime.Now });

        var inOrder = MissionSteps.Instance.Where(x => x.GroupedMovementId == token).ToArray();
        var toStorage = inOrder[0];
        var toDestination = inOrder[1];

        toStorage.MissionId = storing.MissionId;
        toStorage.FromNodeId = storageSide.ItemGuid;
        toStorage.ToNodeId = storage.ItemGuid;

        toDestination.MissionId = moving.MissionId;
        toDestination.FromNodeId = destinationSide.ItemGuid;
        toDestination.ToNodeId = destination.ItemGuid;

        // Come ExecuteGroupedMOVE_LU_TO alla partenza.
        Assert.True(destination.TryReserveForIncomingStep(toDestination.StepId));

        // La lettura della cella al completamento dello step verso il magazzino fallisce.
        _warehouse.Cells[CellId] = Cell(CellId, StorageDoorCode);
        _warehouse.UnavailableOnCells.Add(CellId);

        Assert.NotNull(group.GroupManager);
        await group.GroupManager.StepDoneGroup(token, StepTypes.Automatic);

        // La premessa: gli step sono rimasti in MissionSteps, dove la rete non arriva.
        Assert.Contains(toDestination, MissionSteps.Instance.List);
        Assert.Equal(Guid.Empty, destination.WaitingStepId);
    }

    /// <summary>
    /// La lettura fallisce e il posto torna libero: e' la stessa rete di
    /// <see cref="GroupedStoreTokenTest.AFailedReadDoesNotLeaveTheGroupBusy"/>, sul gettone in piu'.
    /// </summary>
    [Fact]
    public async Task AFailedReadGivesTheReservationBack()
    {
        var (group, destination, toDestination, toStorage) = await AGroupedMoveAboutToStart();

        _warehouse.UnavailableOnCells.Add(CellId);

        Assert.NotNull(group.StepExecutor);
        await Assert.ThrowsAsync<WarehouseUnavailableException>(
            () => group.StepExecutor.ExecuteGroupedMOVE_LU_TO([toStorage, toDestination]));

        Assert.Equal(Guid.Empty, destination.WaitingStepId);
    }

    /// <summary>
    /// Una destinazione gia' presa da un altro step resta sua, e lo step che la voleva non parte
    /// in questo giro: come nel movimento singolo, dove <c>StartPutToMOVE_LU</c> esce.
    /// </summary>
    [Fact]
    public async Task ADestinationAlreadyTakenLeavesItsStepOutOfTheRound()
    {
        var (group, destination, toDestination, toStorage) = await AGroupedMoveAboutToStart();

        var holder = Guid.NewGuid();
        Assert.True(destination.TryReserveForIncomingStep(holder));

        Assert.NotNull(group.StepExecutor);
        await group.StepExecutor.ExecuteGroupedMOVE_LU_TO([toStorage, toDestination]);

        Assert.True(destination.IsWaitingForStep(holder));
        Assert.Null(toDestination.RunningItem);
        Assert.Same(group, toStorage.RunningItem);
    }
}
