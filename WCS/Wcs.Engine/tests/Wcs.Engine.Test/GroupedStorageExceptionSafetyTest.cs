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
/// Ogni uscita dalle ricerche raggruppate, compresi guasto e annullamento nel mezzo di un gruppo,
/// restituisce stati e blocchi temporanei ai valori osservati all'ingresso.
/// </summary>
public sealed class GroupedStorageExceptionSafetyTest : IAsyncLifetime
{
    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly NavigatorStatusTypes _previousStatus = Navigator.NavStatus;
    private readonly FakeWarehouse _warehouse = new();
    private RoutingTestPlant _plant = null!;
    private WcsDatabase _store = null!;
    private QueuedDatabaseWriter<WcsWrite> _writer = null!;

    public Task InitializeAsync()
    {
        _plant = new RoutingTestPlant(NavigatorStatusTypes.Run);
        _store = WcsDatabase.InMemory();
        _writer = new QueuedDatabaseWriter<WcsWrite>(new WcsBatchExecutor(_store).ExecuteAsync,
            new QueuedDatabaseWriterOptions
            {
                DropOldestWhenFull = false,
                MaxBatchSize = 100,
                FlushInterval = TimeSpan.FromMilliseconds(10)
            });
        Navigator.Store = _store;
        Navigator.StateWriter = _writer;
        Navigator.Warehouse = _warehouse;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        Navigator.Warehouse = _previousWarehouse;
        Navigator.StateWriter = null;
        Navigator.Store = null;
        _plant.Dispose();
        Navigator.NavStatus = _previousStatus;
        await _writer.StopAsync();
        _store.Dispose();
    }

    [Fact]
    public async Task FailureOnTheSecondMissionRestoresEveryStatusAndOwnedDoor()
    {
        var scenario = await TwoMissionGroupAsync();
        FailSecondReservation(new WarehouseUnavailableException(nameof(IWarehouse.ReserveStorageAsync)));

        await Assert.ThrowsAsync<WarehouseUnavailableException>(
            () => scenario.Group.StorageManager!.FindNewStoreLocationGroupedForTest());

        Assert.Equal(DeviceStatusTypes.Busy, scenario.Group.Status);
        Assert.Equal([DeviceStatusTypes.Warning, DeviceStatusTypes.Error],
            scenario.Missions.Select(mission => mission.PositionItem!.Status));
        foreach (var door in scenario.OwnedDoorCodes)
            Assert.False(await ORM.DbInfo.GetDoorLockedForStoring(door));
    }

    [Fact]
    public async Task CancellationDuringTheSecondMissionRestoresTheFirstAndSecondPositions()
    {
        var scenario = await TwoMissionGroupAsync();
        FailSecondReservation(new OperationCanceledException("test cancellation"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => scenario.Group.StorageManager!.FindNewStoreLocationGroupedForTest());

        Assert.Equal(DeviceStatusTypes.Busy, scenario.Group.Status);
        Assert.Equal([DeviceStatusTypes.Warning, DeviceStatusTypes.Error],
            scenario.Missions.Select(mission => mission.PositionItem!.Status));
    }

    [Fact]
    public async Task AllocationVerificationRestoresTheGroupAndCurrentPositionAfterFailure()
    {
        var scenario = await TwoMissionGroupAsync();
        FailSecondReservation(new WarehouseUnavailableException(nameof(IWarehouse.ReserveStorageAsync)));

        await Assert.ThrowsAsync<WarehouseUnavailableException>(
            () => scenario.Group.VerifyWhereAllocateLuGrouped([.. scenario.Missions]));

        Assert.Equal(DeviceStatusTypes.Busy, scenario.Group.Status);
        Assert.Equal([DeviceStatusTypes.Warning, DeviceStatusTypes.Error],
            scenario.Missions.Select(mission => mission.PositionItem!.Status));
    }

    [Fact]
    public async Task ACompletedMissionPositionIsRestoredBeforeSelectingTheNextOne()
    {
        var scenario = await TwoMissionGroupAsync();
        DeviceStatusTypes[]? statesSeenBySecondReservation = null;
        var calls = 0;
        _warehouse.Before = operation =>
        {
            if (operation == nameof(IWarehouse.ReserveStorageAsync) && ++calls == 2)
                statesSeenBySecondReservation =
                    [.. scenario.Missions.Select(mission => mission.PositionItem!.Status)];
            return Task.CompletedTask;
        };

        Assert.True(await scenario.Group.StorageManager!.FindNewStoreLocationGroupedForTest());

        Assert.Equal([DeviceStatusTypes.Warning, DeviceStatusTypes.Recovering],
            statesSeenBySecondReservation);
        Assert.Equal([DeviceStatusTypes.Warning, DeviceStatusTypes.Error],
            scenario.Missions.Select(mission => mission.PositionItem!.Status));
    }

    private void FailSecondReservation(Exception failure)
    {
        var calls = 0;
        _warehouse.Before = operation =>
        {
            if (operation == nameof(IWarehouse.ReserveStorageAsync) && ++calls == 2)
                return Task.FromException(failure);
            return Task.CompletedTask;
        };
    }

    private async Task<Scenario> TwoMissionGroupAsync()
    {
        var group = _plant.Device("G", DeviceTypes.GroupOfAutomationDevices);
        group.GroupItemId = 71;
        group.LookWhereAllocateLu = LookWhereAllocateLuTypes.BuildRouteToStoreByIdLuType;
        group.Status = DeviceStatusTypes.Busy;

        var first = _plant.Device("A");
        first.BelongingGroupId = group.GroupItemId;
        first.OrderNumberGroup = 1;
        first.LinkedCellId = 301;
        first.Status = DeviceStatusTypes.Warning;

        var second = _plant.Device("B");
        second.BelongingGroupId = group.GroupItemId;
        second.OrderNumberGroup = 2;
        second.LinkedCellId = 302;
        second.Status = DeviceStatusTypes.Error;

        var storage = _plant.Device("S", DeviceTypes.Storage);
        storage.RelocateItemId = group.ItemGuid;
        _plant.Link("G", "S");
        _plant.Link("A", "S");
        _plant.Link("B", "S");

        var firstMission = Mission(first, 11, 1, "OLD-A");
        var secondMission = Mission(second, 22, 2, "OLD-B");
        var missions = new[] { firstMission, secondMission };

        await PlantPersistence.SaveDoorMappingsAsync(storage.ItemGuid,
        [
            new DoorMapping { ItemId = storage.ItemGuid, DoorCode = "OLD-A", CellId = 101, Side = "A" },
            new DoorMapping { ItemId = storage.ItemGuid, DoorCode = "OLD-B", CellId = 102, Side = "A" },
            new DoorMapping { ItemId = storage.ItemGuid, DoorCode = "NEW-A", CellId = 201, Side = "A" },
            new DoorMapping { ItemId = storage.ItemGuid, DoorCode = "NEW-B", CellId = 202, Side = "A" }
        ]);

        PrepareReservation(firstMission, 201, "NEW-A");
        PrepareReservation(secondMission, 202, "NEW-B");
        _warehouse.Cells[first.LinkedCellId] = LinkedCell(first.LinkedCellId);
        _warehouse.Cells[second.LinkedCellId] = LinkedCell(second.LinkedCellId);

        Assert.Equal(2, group.LUsLoaded.Count);
        Assert.True(group.IsLinkedToStorage);
        return new Scenario(group, missions, ["OLD-A", "OLD-B"]);
    }

    private static PathMission Mission(AutomationDevice position, decimal loadUnitId, decimal typeId, string oldDoor)
    {
        var mission = new PathMission
        {
            PositionItemId = position.ItemGuid,
            LuId = loadUnitId,
            IdLuType = typeId,
            StorageDoorCode = oldDoor,
            Date = DateTime.Now.AddTicks((long)loadUnitId)
        };
        ActiveMissions.Instance.Add(mission, buildRoute: false);
        Assert.Same(mission, position.LoadedLu);
        return mission;
    }

    private void PrepareReservation(PathMission mission, decimal cellId, string doorCode)
    {
        _warehouse.Cells[cellId] = new CellDescription(cellId, AreaId: 1, WarehouseId: 1, Depth: 1,
            Inverted: false, CellLockState.None, AllowedLoadUnitTypeId: null, GroupId: null, GroupOrder: 0,
            doorCode, DoorCodeB: string.Empty, DoorCount: 1, MultiDepthStorage: false);
        _warehouse.CellPartitionMaps[cellId] = new CellPartitionMap(cellId, "0001", "0002",
            AsiDoorB: string.Empty, SubitemDoorB: string.Empty, [new CellPartition("0003", 0)]);
        _warehouse.StorageReservations[mission.LuId] = new StorageReservation(mission.MissionId,
            StorageReservationOutcome.CellFound, cellId, doorCode, "0003", 100, NeedsCompaction: false);
    }

    private static CellDescription LinkedCell(decimal cellId) =>
        new(cellId, AreaId: 1, WarehouseId: 1, Depth: 1, Inverted: false, CellLockState.None,
            AllowedLoadUnitTypeId: null, GroupId: null, GroupOrder: 0, "LINK", DoorCodeB: string.Empty,
            DoorCount: 1, MultiDepthStorage: false);

    private sealed record Scenario(
        AutomationDevice Group,
        IReadOnlyList<PathMission> Missions,
        IReadOnlyList<string> OwnedDoorCodes);
}
