using System.Reflection;
using Wcs.Engine.Api;
using Wcs.Engine.Collections;
using Wcs.Engine.Items;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Wcs.Engine.Test.Routing;
using Wcs.Wms.Contracts;
using Wcs.Wms.Contracts.Test;
using Xunit;
using static Wcs.Common.Enums;
using PathMission = Wcs.Engine.Items.PathMission;
using RouteBuildStatusTypes = Wcs.Engine.Items.AutomationDevice.RouteBuildStatusTypes;

namespace Wcs.Engine.Test;

/// <summary>
/// Item 54, task 6 (decisions 5 and 6 of the owner of 13/10). Towards a relocator the operator chooses
/// «deposit it» or «only bring it there»: the commit writes the choice on the mission, and arrived at the
/// relocator the choice decides instead of the relocator's configuration - a cell the relocator reaches,
/// asked to the warehouse, or nothing. Towards a precise storage cell, by its door A code: the warehouse
/// reserves that cell or refuses it with the reason, and the mission goes to the cell's storage through
/// the relocator that serves it. The candidate cells are the warehouse's first 20, in the storage chosen.
/// </summary>
/// <remarks>
/// The plant: the conveyor C, the relocator R of the storages S2 (cell 6, door A6, and its door B A6B)
/// and S3 (cell 7, door A7); the relocator R2 of the storage S4 (cell 9, door A9); the devices X and Y.
/// C->R, R->X, R->Y, R->S2, R->S3, R2->S4. R's linked cell is 6. The mission of the load unit 42 is on C,
/// bound for X with the route C->R->X. The navigator is in <c>Stop</c> (the missions are added in
/// <c>Recover</c>), in <c>Run</c> only for the route loop; the WCS's database is in memory, the warehouse
/// is the fake one; no timed wait.
/// </remarks>
public sealed class RelocatorAndPreciseCellTest : IDisposable
{
    private const decimal LoadUnitId = 42;

    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly QueuedDatabaseWriter<WcsWrite>? _previousWriter = Navigator.StateWriter;
    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly RoutingTestPlant _plant = new();
    private readonly FakeWarehouse _warehouse = new();

    public RelocatorAndPreciseCellTest()
    {
        Navigator.Store = _database;
        Navigator.StateWriter = null;
        Navigator.Warehouse = _warehouse;
        RoutesToBuild.Instance.Items = new();
    }

    public void Dispose()
    {
        RoutesToBuild.Instance.Items = new();
        _plant.Dispose();
        Navigator.Warehouse = _previousWarehouse;
        Navigator.StateWriter = _previousWriter;
        Navigator.Store = null;
        _database.Dispose();
    }

    // ---------------------------------------------------------------------------------------
    // Towards a relocator: the choice (decision 5)
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The operator sends the load unit to the relocator R and chooses what R does with it: the change is
    /// the change towards a device of every day (destination R with the operator's mark, the route C->R),
    /// and the commit writes the choice on the mission and in its row of the WCS's database.
    /// </summary>
    [Theory]
    [InlineData(RelocatorArrival.Deposit)]
    [InlineData(RelocatorArrival.Stay)]
    public async Task TheChoiceForTheRelocatorIsWrittenWithTheRouteToIt(RelocatorArrival arrival)
    {
        var mission = await AMissionOnCBoundForX();

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["R"].ItemGuid,
            MissionGatewayChoice.Keep, MissionDestinationOptions.Relocator(arrival));

        Assert.Equal(MissionDestinationOutcome.Changed, result.Outcome);
        Assert.Equal(_plant["R"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.OperatorDestination);
        Assert.Equal(arrival, mission.RelocatorArrival);
        Assert.Equal(["C->R"], StepsOf(mission));
        Assert.Equal((short)arrival, await RelocatorArrivalInTheRow(mission));
    }

    /// <summary>
    /// The choice is for a relocator only: Y is named by no storage, and the change is refused without
    /// touching anything.
    /// </summary>
    [Fact]
    public async Task AChoiceForADeviceThatIsNotARelocatorIsRefused()
    {
        var mission = await AMissionOnCBoundForX();

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["Y"].ItemGuid,
            MissionGatewayChoice.Keep, MissionDestinationOptions.Relocator(RelocatorArrival.Deposit));

        Assert.Equal(MissionDestinationOutcome.NotARelocator, result.Outcome);
        Assert.Equal(_plant["X"].ItemGuid, mission.DestinationItemId);
        Assert.False(mission.OperatorDestination);
        Assert.Equal(RelocatorArrival.None, mission.RelocatorArrival);
        Assert.Equal(["C->R", "R->X"], StepsOf(mission));
    }

    /// <summary>
    /// The same relocator chosen again with the other choice is a change, not «unchanged»: the mission
    /// was going to stay on R, and now R deposits it.
    /// </summary>
    [Fact]
    public async Task TheOtherChoiceForTheSameRelocatorIsAChange()
    {
        var mission = await AMissionOnCBoundForX();
        await ToR(mission, RelocatorArrival.Stay);

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["R"].ItemGuid,
            MissionGatewayChoice.Keep, MissionDestinationOptions.Relocator(RelocatorArrival.Deposit));

        Assert.Equal(MissionDestinationOutcome.Changed, result.Outcome);
        Assert.Equal(RelocatorArrival.Deposit, mission.RelocatorArrival);
        Assert.Equal((short)RelocatorArrival.Deposit, await RelocatorArrivalInTheRow(mission));
    }

    /// <summary>
    /// The same relocator chosen again without a choice: the operator's change takes the old choice away,
    /// and R does with the load unit what its configuration says.
    /// </summary>
    [Fact]
    public async Task TheSameRelocatorChosenWithoutAChoiceTakesItAway()
    {
        var mission = await AMissionOnCBoundForX();
        await ToR(mission, RelocatorArrival.Stay);

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["R"].ItemGuid);

        Assert.Equal(MissionDestinationOutcome.Changed, result.Outcome);
        Assert.Equal(RelocatorArrival.None, mission.RelocatorArrival);
        Assert.Equal((short)0, await RelocatorArrivalInTheRow(mission));
    }

    /// <summary>
    /// The destination changed by any other way (here written as a rejection writes it) takes the choice
    /// away: it was for the relocator, and the mission no longer goes there.
    /// </summary>
    [Fact]
    public async Task ADestinationChangedByAnotherWayDropsTheChoice()
    {
        var mission = await AMissionOnCBoundForX();
        await ToR(mission, RelocatorArrival.Stay);

        mission.DestinationItemId = _plant["Y"].ItemGuid;

        Assert.Equal(RelocatorArrival.None, mission.RelocatorArrival);
        Assert.Equal((short)0, await RelocatorArrivalInTheRow(mission));
    }

    /// <summary>
    /// The choice survives a restart: the row of the WCS's database brings it back with the destination,
    /// and it does not fall at the arrival like the operator's mark - the arrival is where it applies.
    /// </summary>
    [Fact]
    public async Task TheChoiceSurvivesARestartAndTheArrival()
    {
        var mission = await AMissionOnCBoundForX();
        await ToR(mission, RelocatorArrival.Stay);

        await using (var context = _database.CreateContext())
        {
            var row = context.Missions.Single(m => m.MissionId == mission.MissionId);
            Navigator.NavStatus = NavigatorStatusTypes.Recover;
            var reloaded = MissionStore.ToPathMission(row);
            Assert.Equal(RelocatorArrival.Stay, reloaded.RelocatorArrival);
            Assert.Equal(_plant["R"].ItemGuid, reloaded.DestinationItemId);
            Navigator.NavStatus = NavigatorStatusTypes.Stop;
        }

        await TheLoadUnitArrivesOnR(mission);

        Assert.False(mission.OperatorDestination);
        Assert.Equal(RelocatorArrival.Stay, mission.RelocatorArrival);
    }

    // ---------------------------------------------------------------------------------------
    // On the relocator: the choice decides
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// «Only bring it there»: on R, whatever R's storing mode, the route rebuild (<c>FindWhereStoreLu</c>)
    /// asks nothing and builds nothing, and the load unit stays on R with its destination and its choice.
    /// Before task 6 R's configuration decided: a cell of the warehouse with a storing mode, «no route»
    /// with the mode No.
    /// </summary>
    [Theory]
    [InlineData(LookWhereAllocateLuTypes.No)]
    [InlineData(LookWhereAllocateLuTypes.BuildRouteToStore)]
    [InlineData(LookWhereAllocateLuTypes.BuildRouteToStoreForcing)]
    public async Task OnTheRelocatorTheLoadUnitBroughtThereStays(LookWhereAllocateLuTypes mode)
    {
        var mission = await AMissionOnCBoundForX();
        await ToR(mission, RelocatorArrival.Stay);
        await TheLoadUnitArrivesOnR(mission);
        _plant["R"].LookWhereAllocateLu = mode;
        TheWarehouseAnswersCell7();

        var outcome = await Navigator.FindWhereStoreLu(mission, _plant["R"], mode);

        Assert.Equal(RouteBuildStatusTypes.Stays, outcome);
        Assert.Empty(_warehouse.Calls);
        Assert.Equal(_plant["R"].ItemGuid, mission.DestinationItemId);
        Assert.Equal(RelocatorArrival.Stay, mission.RelocatorArrival);
        Assert.Empty(mission.MissionSteps);
    }

    /// <summary>
    /// «Deposit it»: on R, whatever R's storing mode, the route rebuild asks the warehouse for a cell R
    /// reaches (the search R makes for itself, anchored to it: <c>StoreInLinkedCell</c>) and the warehouse
    /// gives the cell 7 of S3: the destination is S3 with the door A7, the route R->S3, and the choice has
    /// fallen with the destination. Before task 6, with the mode No, R did nothing.
    /// </summary>
    [Theory]
    [InlineData(LookWhereAllocateLuTypes.No)]
    [InlineData(LookWhereAllocateLuTypes.BuildRouteToStore)]
    public async Task OnTheRelocatorTheLoadUnitToDepositGetsACellTheRelocatorReaches(LookWhereAllocateLuTypes mode)
    {
        var mission = await AMissionOnCBoundForX();
        await ToR(mission, RelocatorArrival.Deposit);
        await TheLoadUnitArrivesOnR(mission);
        _plant["R"].LookWhereAllocateLu = mode;
        TheWarehouseAnswersCell7();

        var outcome = await Navigator.FindWhereStoreLu(mission, _plant["R"], mode);

        Assert.Equal(RouteBuildStatusTypes.Available, outcome);
        var request = Assert.Single(_warehouse.StorageRequests);
        Assert.Equal(LoadUnitId, request.LoadUnitId);
        Assert.Equal([_plant["R"].ItemGuid], request.ReachableDeviceIds);
        Assert.Equal(StorageCellSearchMode.ConnectedAutomaticOperator, request.CellSearchMode);
        Assert.Equal(_plant["S3"].ItemGuid, mission.DestinationItemId);
        Assert.Equal("A7", mission.StorageDoorCode);
        Assert.Equal(RelocatorArrival.None, mission.RelocatorArrival);
        Assert.Equal(["R->S3"], StepsOf(mission));
    }

    /// <summary>
    /// The arrival on R, with R's mode No and no fixed destination: R would leave the load unit standing
    /// (<c>ShouldBuildRoute</c> is false). With «deposit it» the arrival sends it to the route loop, which
    /// asks the cell; with «only bring it there» it does not.
    /// </summary>
    [Theory]
    [InlineData(RelocatorArrival.Deposit, true)]
    [InlineData(RelocatorArrival.Stay, false)]
    public async Task TheArrivalOnTheRelocatorSendsTheLoadUnitToDepositToTheRouteLoop(RelocatorArrival arrival, bool queued)
    {
        var mission = await AMissionOnCBoundForX();
        await ToR(mission, arrival);
        _plant["R"].LookWhereAllocateLu = LookWhereAllocateLuTypes.No;
        RoutesToBuild.Instance.Items = new();

        await _plant["R"].LoadLu(mission.MissionId, activateExecuteStep: false).WaitAsync(TimeSpan.FromSeconds(30));
        await mission.PositionWork.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(_plant["R"].ItemGuid, mission.PositionItemId);
        Assert.Equal(queued, RoutesToBuild.Instance[mission.MissionId] != null);
    }

    /// <summary>
    /// In Run, the way the engine gets there: the route loop picks the load unit brought to R up, starts
    /// its cell search, asks nothing, and the mission leaves the queue with no failure counted.
    /// </summary>
    [Fact]
    public async Task InRunTheRouteLoopLeavesTheLoadUnitBroughtToTheRelocatorThere()
    {
        var mission = await AMissionOnCBoundForX();
        await ToR(mission, RelocatorArrival.Stay);
        await TheLoadUnitArrivesOnR(mission);
        _plant["R"].LookWhereAllocateLu = LookWhereAllocateLuTypes.BuildRouteToStore;
        foreach (var name in _plant.DeviceNames) _plant[name].AutoExecuteStep = false;
        Navigator.NavStatus = NavigatorStatusTypes.Run;
        try
        {
            await TheRouteLoopLooksForTheCell(mission);
        }
        finally
        {
            Navigator.NavStatus = NavigatorStatusTypes.Stop;
        }

        Assert.Empty(_warehouse.Calls);
        Assert.Null(RoutesToBuild.Instance[mission.MissionId]);
        Assert.Equal(0, Navigator.RoutePathRetries.Failures(mission.MissionId));
        Assert.Equal(_plant["R"].ItemGuid, mission.DestinationItemId);
        Assert.Empty(mission.MissionSteps);
    }

    // ---------------------------------------------------------------------------------------
    // A precise cell (decision 6)
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The operator chooses the cell of the door A6, in S2 or among the cells of R: the warehouse is not
    /// asked for a cell, it reserves that one for the mission, and that reservation replaces the one the
    /// load unit had: nothing is released first (fix round 1 of task 6). The mission goes to S2 with the door A6, its address, the operator's
    /// mark and the cell reserved, through R, which serves it: the route C->R->S2.
    /// </summary>
    [Theory]
    [InlineData("S2")]
    [InlineData("R")]
    public async Task APreciseCellIsReservedAndTheMissionGoesToItsStorageThroughItsRelocator(string chosen)
    {
        var mission = await AMissionOnCBoundForX();
        _warehouse.ReservableStorageCells["A6"] = new StorageReservation(Guid.Empty,
            StorageReservationOutcome.CellFound, 6, "A6", Partition: "0063", StorageDepth: 150, NeedsCompaction: false);

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant[chosen].ItemGuid,
            MissionGatewayChoice.Keep, MissionDestinationOptions.Cell(" A6 "));

        Assert.Equal(MissionDestinationOutcome.Changed, result.Outcome);
        Assert.Equal(_plant["S2"].ItemGuid, mission.DestinationItemId);
        Assert.Equal("A6", mission.StorageDoorCode);
        Assert.True(mission.OperatorDestination);
        Assert.True(mission.StorageCellReserved);
        Assert.Equal("0061", mission.DestinationAsi);
        Assert.Equal("0062", mission.DestinationSubitem);
        Assert.Equal("0063", mission.DestinationPartition);
        Assert.Equal(["C->R", "R->S2"], StepsOf(mission));

        var request = Assert.Single(_warehouse.StorageCellReservationRequests);
        Assert.Equal((mission.MissionId, LoadUnitId, "A6"), (request.MissionId, request.LoadUnitId, request.DoorCode));
        Assert.DoesNotContain(nameof(IWarehouse.ReserveStorageAsync), _warehouse.Calls);
        Assert.DoesNotContain(nameof(IWarehouse.ReleaseStorageReservationAsync), _warehouse.Calls);
        Assert.Equal(6, _warehouse.StorageReservationsByMission[mission.MissionId].CellId);
    }

    /// <summary>
    /// The mission goes to the cell 6 of S2 (door A6); the operator chooses the cell 5 of the same S2
    /// (door A5) and the gateway R at once. A new gateway towards the storage of the cell the mission has
    /// keeps that cell and redoes only the route (decision 6 of the owner of 2/10): not when the operator
    /// chose another precise cell, which the warehouse reserves.
    /// </summary>
    [Fact]
    public async Task APreciseCellWithANewGatewayIsReservedAlsoInTheStorageOfTheCellTheMissionHas()
    {
        var mission = await AMissionOnCBoundForX();
        _warehouse.ReservableStorageCells["A6"] = new StorageReservation(Guid.Empty,
            StorageReservationOutcome.CellFound, 6, "A6", Partition: "0063", StorageDepth: 150, NeedsCompaction: false);
        _warehouse.ReservableStorageCells["A5"] = new StorageReservation(Guid.Empty,
            StorageReservationOutcome.CellFound, 5, "A5", Partition: "0053", StorageDepth: 150, NeedsCompaction: false);
        Assert.Equal(MissionDestinationOutcome.Changed,
            (await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S2"].ItemGuid,
                MissionGatewayChoice.Keep, MissionDestinationOptions.Cell("A6"))).Outcome);

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S2"].ItemGuid,
            MissionGatewayChoice.Set(_plant["R"].ItemGuid), MissionDestinationOptions.Cell("A5"));

        Assert.Equal(MissionDestinationOutcome.Changed, result.Outcome);
        Assert.Equal("A5", mission.StorageDoorCode);
        Assert.Equal("0053", mission.DestinationPartition);
        Assert.Equal(_plant["R"].ItemGuid, mission.Gateway);
        Assert.Equal(["A6", "A5"], _warehouse.StorageCellReservationRequests.Select(request => request.DoorCode));
    }

    /// <summary>
    /// The warehouse answers «reserved» without the reservation: nothing to go to, the change is refused as
    /// a warehouse that does not answer, and the reservation goes back to it.
    /// </summary>
    [Fact]
    public async Task APreciseCellReservedWithoutItsReservationIsGivenBack()
    {
        var mission = await AMissionOnCBoundForX();
        _warehouse.ReserveStorageCellAnswer = _ => new StorageCellReservationResult(StorageCellReservationOutcome.Reserved, null, null);

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S2"].ItemGuid,
            MissionGatewayChoice.Keep, MissionDestinationOptions.Cell("A6"));

        Assert.Equal(MissionDestinationOutcome.WarehouseUnavailable, result.Outcome);
        Assert.Equal([mission.MissionId], _warehouse.ReleasedStorageReservations);
        Assert.Equal(_plant["X"].ItemGuid, mission.DestinationItemId);
        Assert.Equal(["C->R", "R->X"], StepsOf(mission));
    }

    /// <summary>
    /// The warehouse refuses the cell (a door B, a cell not free or not suited, a load unit or a door it
    /// does not know): the change is refused with the warehouse's reason, and the mission is as it was.
    /// </summary>
    [Theory]
    [InlineData(StorageCellReservationOutcome.UnknownDoor, MissionCellRefusal.UnknownDoor)]
    [InlineData(StorageCellReservationOutcome.NotADoorA, MissionCellRefusal.NotADoorA)]
    [InlineData(StorageCellReservationOutcome.CellNotAvailable, MissionCellRefusal.CellNotAvailable)]
    [InlineData(StorageCellReservationOutcome.UnknownLoadUnit, MissionCellRefusal.UnknownLoadUnit)]
    public async Task TheWarehousesRefusalOfThePreciseCellIsTheAnswerWithItsReason(
        StorageCellReservationOutcome refusal, MissionCellRefusal expected)
    {
        var mission = await AMissionOnCBoundForX();
        _warehouse.ReserveStorageCellAnswer = _ => new StorageCellReservationResult(refusal, null, "the warehouse's reason");

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S2"].ItemGuid,
            MissionGatewayChoice.Keep, MissionDestinationOptions.Cell("A6"));

        Assert.Equal(MissionDestinationOutcome.CellRefused, result.Outcome);
        Assert.Equal(expected, result.CellRefusal);
        Assert.Equal("the warehouse's reason", result.CellRefusalReason);
        Assert.Equal(_plant["X"].ItemGuid, mission.DestinationItemId);
        Assert.False(mission.OperatorDestination);
        Assert.True(string.IsNullOrEmpty(mission.StorageDoorCode));
        Assert.Equal(["C->R", "R->X"], StepsOf(mission));
    }

    /// <summary>
    /// Fix round 1 of task 6. The mission is bound for S2 and holds the cell 6 (door A6, its reservation
    /// linked); the operator chooses the cell 5 of S2 (door A5) and the warehouse refuses it - an occupied
    /// cell from a list a few moments old, a code typed by hand. The precise cell's reservation replaces
    /// the one the load unit had, and a refusal writes nothing: the change is refused with the warehouse's
    /// reason, nothing is released, and the mission still goes to A6 with its reservation, its door, its
    /// address and its route. Until this round the reservation of A6 was released before asking for A5,
    /// and the refusal took the cell away from the mission (LostTheCell).
    /// </summary>
    [Theory]
    [InlineData(StorageCellReservationOutcome.CellNotAvailable, MissionCellRefusal.CellNotAvailable)]
    [InlineData(StorageCellReservationOutcome.NotADoorA, MissionCellRefusal.NotADoorA)]
    [InlineData(StorageCellReservationOutcome.UnknownDoor, MissionCellRefusal.UnknownDoor)]
    [InlineData(StorageCellReservationOutcome.UnknownLoadUnit, MissionCellRefusal.UnknownLoadUnit)]
    public async Task TheWarehousesRefusalOfAnotherPreciseCellLeavesTheMissionTheCellItHolds(
        StorageCellReservationOutcome refusal, MissionCellRefusal expected)
    {
        var mission = await AMissionBoundForS2HoldingA6();
        _warehouse.ReserveStorageCellAnswer = request =>
            request.DoorCode == "A5" ? new StorageCellReservationResult(refusal, null, "the warehouse's reason") : null;

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S2"].ItemGuid,
            MissionGatewayChoice.Keep, MissionDestinationOptions.Cell("A5"));

        Assert.Equal(MissionDestinationOutcome.CellRefused, result.Outcome);
        Assert.Equal(expected, result.CellRefusal);
        Assert.Equal("the warehouse's reason", result.CellRefusalReason);
        Assert.False(result.LostTheCell);
        Assert.Empty(_warehouse.ReleasedStorageReservations);
        Assert.Equal(6, _warehouse.StorageReservationsByMission[mission.MissionId].CellId);
        Assert.Equal(_plant["S2"].ItemGuid, mission.DestinationItemId);
        Assert.Equal("A6", mission.StorageDoorCode);
        Assert.True(mission.StorageCellReserved);
        Assert.Equal("0061", mission.DestinationAsi);
        Assert.Equal("0063", mission.DestinationPartition);
        Assert.Equal(["C->R", "R->S2"], StepsOf(mission));
    }

    /// <summary>
    /// Fix round 1 of task 6. The mission holds the cell 6 (door A6) and the warehouse does not answer the
    /// reservation of the cell 5: the answer may have been lost after the reservation, which replaced the
    /// one of A6. The reservation goes back to the warehouse, and the mission loses its cell, as in the
    /// storage flow of every day.
    /// </summary>
    [Fact]
    public async Task APreciseCellWithoutAnAnswerTakesAwayTheCellTheMissionHeld()
    {
        var mission = await AMissionBoundForS2HoldingA6();
        _warehouse.UnavailableOperations.Add(nameof(IWarehouse.ReserveStorageCellAsync));

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S2"].ItemGuid,
            MissionGatewayChoice.Keep, MissionDestinationOptions.Cell("A5"));

        Assert.Equal(MissionDestinationOutcome.WarehouseUnavailable, result.Outcome);
        Assert.True(result.LostTheCell);
        Assert.Equal([mission.MissionId], _warehouse.ReleasedStorageReservations);
        Assert.True(string.IsNullOrEmpty(mission.StorageDoorCode));
    }

    /// <summary>
    /// Fix round 1 of task 6. The mission holds the cell 6 (door A6) and the operator chooses A6 again: the
    /// warehouse would not find free a cell the load unit holds, so the mission's reservation goes first,
    /// as in the storage flow of every day, and then A6 is reserved again.
    /// </summary>
    [Fact]
    public async Task ThePreciseCellTheMissionHoldsIsReleasedBeforeItIsReservedAgain()
    {
        var mission = await AMissionBoundForS2HoldingA6();

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S2"].ItemGuid,
            MissionGatewayChoice.Keep, MissionDestinationOptions.Cell("A6"));

        Assert.Equal(MissionDestinationOutcome.Changed, result.Outcome);
        Assert.Equal("A6", mission.StorageDoorCode);
        Assert.Equal([mission.MissionId], _warehouse.ReleasedStorageReservations);
        Assert.Equal([nameof(IWarehouse.ReleaseStorageReservationAsync), nameof(IWarehouse.ReserveStorageCellAsync)],
            _warehouse.Calls.Where(call => call is nameof(IWarehouse.ReleaseStorageReservationAsync)
                or nameof(IWarehouse.ReserveStorageCellAsync)));
        Assert.Equal(6, _warehouse.StorageReservationsByMission[mission.MissionId].CellId);
    }

    /// <summary>
    /// What the WCS refuses before asking the warehouse anything: a door its door map does not have, the
    /// door B of a cell (owner's answer 7 of 13/10), a cell of another storage than the one chosen, a cell
    /// chosen on a device that is not a storage, and a cell of a storage another relocator serves.
    /// </summary>
    [Theory]
    [InlineData("S2", "Z9", MissionCellRefusal.UnknownDoor)]
    [InlineData("S2", "", MissionCellRefusal.UnknownDoor)]
    [InlineData("S2", "A6B", MissionCellRefusal.NotADoorA)]
    [InlineData("S2", "A7", MissionCellRefusal.NotInTheChoice)]
    [InlineData("X", "A6", MissionCellRefusal.NotInTheChoice)]
    [InlineData("R2", "A6", MissionCellRefusal.NotInTheChoice)]
    [InlineData("R", "A9", MissionCellRefusal.NotInTheChoice)]
    public async Task TheWcsRefusesAPreciseCellBeforeAskingTheWarehouse(string chosen, string door, MissionCellRefusal expected)
    {
        var mission = await AMissionOnCBoundForX();

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant[chosen].ItemGuid,
            MissionGatewayChoice.Keep, MissionDestinationOptions.Cell(door));

        Assert.Equal(MissionDestinationOutcome.CellRefused, result.Outcome);
        Assert.Equal(expected, result.CellRefusal);
        Assert.False(string.IsNullOrEmpty(result.CellRefusalReason));
        Assert.Empty(_warehouse.Calls);
        Assert.Equal(_plant["X"].ItemGuid, mission.DestinationItemId);
        Assert.Equal(["C->R", "R->X"], StepsOf(mission));
    }

    // ---------------------------------------------------------------------------------------
    // The candidate cells (decision 6, answer 6)
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The candidates in S2: the warehouse is asked with what the storage flow would pass to reserve (the
    /// load unit, the warehouse of S2's cell, R as the relocator, the search of every day) and answers its
    /// first cells; those that are not in S2 by the WCS's door map are left out, in the warehouse's order.
    /// For R, the cells of both its storages.
    /// </summary>
    [Theory]
    [InlineData("S2", new[] { "A6" })]
    [InlineData("R", new[] { "A7", "A6" })]
    public async Task TheCandidatesAreTheWarehousesInTheChoice(string chosen, string[] expected)
    {
        var mission = await AMissionOnCBoundForX();
        _warehouse.StorageCellCandidates[LoadUnitId] = [Candidate(7, "A7"), Candidate(6, "A6"), Candidate(99, "ZZ")];

        var answer = await MissionGateway.StorageCellCandidatesAsync(mission.MissionId, _plant[chosen].ItemGuid);

        Assert.Equal(StorageCellCandidatesOutcome.Listed, answer.Outcome);
        Assert.Equal(expected, answer.Cells.Select(cell => cell.DoorCode));
        var request = Assert.Single(_warehouse.StorageCellCandidateRequests);
        Assert.Equal(LoadUnitId, request.LoadUnitId);
        Assert.Equal(1, request.WarehouseId);
        Assert.Empty(request.ExcludedCellIds);
        Assert.Equal([_plant["R"].ItemGuid], request.ReachableDeviceIds);
        Assert.Equal(StorageCellSearchMode.FreeAutomaticOperator, request.CellSearchMode);
        Assert.Equal(["C->R", "R->X"], StepsOf(mission));
    }

    /// <summary>The refusals of the candidate list: no cells on a device that has none, and the reasons of the storage flow.</summary>
    [Fact]
    public async Task TheCandidatesAreRefusedWhereThereIsNothingToAsk()
    {
        var mission = await AMissionOnCBoundForX();

        Assert.Equal(StorageCellCandidatesOutcome.MissionNotFound,
            (await MissionGateway.StorageCellCandidatesAsync(Guid.NewGuid(), _plant["S2"].ItemGuid)).Outcome);
        Assert.Equal(StorageCellCandidatesOutcome.DeviceNotFound,
            (await MissionGateway.StorageCellCandidatesAsync(mission.MissionId, Guid.NewGuid())).Outcome);
        Assert.Equal(StorageCellCandidatesOutcome.NotAStorage,
            (await MissionGateway.StorageCellCandidatesAsync(mission.MissionId, _plant["X"].ItemGuid)).Outcome);

        _warehouse.Fault = operation => operation == nameof(IWarehouse.ListStorageCellCandidatesAsync)
            ? new WarehouseUnavailableException(operation)
            : null;
        Assert.Equal(StorageCellCandidatesOutcome.WarehouseUnavailable,
            (await MissionGateway.StorageCellCandidatesAsync(mission.MissionId, _plant["S2"].ItemGuid)).Outcome);

        mission.LuId = 0;
        Assert.Equal(StorageCellCandidatesOutcome.NoStorageCell,
            (await MissionGateway.StorageCellCandidatesAsync(mission.MissionId, _plant["S2"].ItemGuid)).Outcome);
    }

    // ---------------------------------------------------------------------------------------
    // Scene
    // ---------------------------------------------------------------------------------------

    /// <summary>The plant of the remarks, and on C the mission of the load unit 42 bound for X with the route C->R->X.</summary>
    private async Task<PathMission> AMissionOnCBoundForX()
    {
        foreach (var name in new[] { "S2", "S3", "S4" }) _plant.Device(name, DeviceTypes.Storage);
        foreach (var name in new[] { "C", "R", "R2", "X", "Y" }) _plant.Device(name);
        _plant.Link("C", "R");
        _plant.Link("R", "X");
        _plant.Link("R", "Y");
        _plant.Link("R", "S2");
        _plant.Link("R", "S3");
        _plant.Link("R2", "S4");
        _plant["S2"].RelocateItemId = _plant["R"].ItemGuid;
        _plant["S3"].RelocateItemId = _plant["R"].ItemGuid;
        _plant["S4"].RelocateItemId = _plant["R2"].ItemGuid;
        _plant["R"].LinkedCellId = 6;

        await PlantPersistence.SaveDoorMappingsAsync(_plant["S2"].ItemGuid,
        [
            new DoorMapping { ItemId = _plant["S2"].ItemGuid, DoorCode = "A5", CellId = 5, Side = "A" },
            new DoorMapping { ItemId = _plant["S2"].ItemGuid, DoorCode = "A6", CellId = 6, Side = "A" },
            new DoorMapping { ItemId = _plant["S2"].ItemGuid, DoorCode = "A6B", CellId = 6, Side = "B" }
        ]);
        await PlantPersistence.SaveDoorMappingsAsync(_plant["S3"].ItemGuid,
            [new DoorMapping { ItemId = _plant["S3"].ItemGuid, DoorCode = "A7", CellId = 7, Side = "A" }]);
        await PlantPersistence.SaveDoorMappingsAsync(_plant["S4"].ItemGuid,
            [new DoorMapping { ItemId = _plant["S4"].ItemGuid, DoorCode = "A9", CellId = 9, Side = "A" }]);
        foreach (var (cell, door) in new[] { (5m, "A5"), (6m, "A6"), (7m, "A7"), (9m, "A9") })
        {
            _warehouse.Cells[cell] = new CellDescription(cell, AreaId: 1, WarehouseId: 1, Depth: 1, Inverted: false,
                CellLockState.None, AllowedLoadUnitTypeId: null, GroupId: null, GroupOrder: 0, door, DoorCodeB: string.Empty,
                DoorCount: 1, MultiDepthStorage: false);
            var digit = (int)cell;
            _warehouse.CellPartitionMaps[cell] = new CellPartitionMap(cell, $"00{digit}1", $"00{digit}2",
                AsiDoorB: string.Empty, SubitemDoorB: string.Empty, [new CellPartition($"00{digit}3", -1)]);
        }

        var mission = new PathMission
        {
            LuId = LoadUnitId,
            PositionItemId = _plant["C"].ItemGuid,
            MissionType = MissionTypes.MovingToItem,
            OperationType = OperationTypes.Outbound
        };
        ActiveMissions.Instance.Add(mission, buildRoute: false);
        await mission.PositionWork;
        mission.DestinationItemId = _plant["X"].ItemGuid;
        _plant.PlannedStep(mission, "C", "R");
        _plant.PlannedStep(mission, "R", "X");
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        _warehouse.Calls.Clear();
        return mission;
    }

    /// <summary>
    /// The mission of <see cref="AMissionOnCBoundForX"/> sent by the operator to the cell 6 of S2 (door A6):
    /// bound for S2 through R, holding A6 with its reservation linked; the cells 5 and 6 are reservable,
    /// and the registries of the warehouse's calls and releases start again.
    /// </summary>
    private async Task<PathMission> AMissionBoundForS2HoldingA6()
    {
        var mission = await AMissionOnCBoundForX();
        _warehouse.ReservableStorageCells["A5"] = new StorageReservation(Guid.Empty,
            StorageReservationOutcome.CellFound, 5, "A5", Partition: "0053", StorageDepth: 150, NeedsCompaction: false);
        _warehouse.ReservableStorageCells["A6"] = new StorageReservation(Guid.Empty,
            StorageReservationOutcome.CellFound, 6, "A6", Partition: "0063", StorageDepth: 150, NeedsCompaction: false);
        Assert.Equal(MissionDestinationOutcome.Changed,
            (await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S2"].ItemGuid,
                MissionGatewayChoice.Keep, MissionDestinationOptions.Cell("A6"))).Outcome);
        Assert.Equal("A6", mission.StorageDoorCode);
        Assert.Equal(6, _warehouse.StorageReservationsByMission[mission.MissionId].CellId);
        _warehouse.Calls.Clear();
        _warehouse.ReleasedStorageReservations.Clear();
        return mission;
    }

    /// <summary>The operator sends the mission to R with the choice; the registry of the warehouse's calls starts again.</summary>
    private async Task ToR(PathMission mission, RelocatorArrival arrival)
    {
        Assert.Equal(MissionDestinationOutcome.Changed,
            (await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["R"].ItemGuid,
                MissionGatewayChoice.Keep, MissionDestinationOptions.Relocator(arrival))).Outcome);
        Assert.Equal(arrival, mission.RelocatorArrival);
        _warehouse.Calls.Clear();
    }

    /// <summary>The step C->R done: it leaves the route, and the load unit is on R, with its background work done.</summary>
    private async Task TheLoadUnitArrivesOnR(PathMission mission)
    {
        foreach (var step in mission.MissionSteps.ToList()) _plant["C"].RemoveStep(step);
        mission.PositionItemId = _plant["R"].ItemGuid;
        await mission.PositionWork.WaitAsync(TimeSpan.FromSeconds(30));
        await PendingMissionChanges.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Empty(mission.MissionSteps);
        _warehouse.Calls.Clear();
        _warehouse.StorageRequests.Clear();
    }

    /// <summary>The cell 7 of S3 (door A7) that the warehouse answers for the load unit.</summary>
    private void TheWarehouseAnswersCell7() =>
        _warehouse.StorageReservations[LoadUnitId] = new StorageReservation(Guid.Empty,
            StorageReservationOutcome.CellFound, 7, "A7", Partition: "0073", StorageDepth: 150, NeedsCompaction: false);

    private static StorageCellCandidate Candidate(decimal cellId, string doorCode) =>
        new(cellId, doorCode, AreaId: 1, AreaDescription: "Area 1", Column: 1, Floor: 1, Description: null, LoadUnitsInCell: 0);

    private async Task<short> RelocatorArrivalInTheRow(PathMission mission)
    {
        await using var context = _database.CreateContext();
        return context.Missions.Single(m => m.MissionId == mission.MissionId).RelocatorArrival;
    }

    private static async Task TheRouteLoopLooksForTheCell(PathMission mission)
    {
        Assert.NotNull(mission.PositionItem);
        var method = typeof(Navigator).GetMethod("StartCellSearch", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        method!.Invoke(null, [mission, mission.PositionItem]);

        if (Navigator.CellSearchInFlight(mission.MissionId) is { } search)
            await search.WaitAsync(TimeSpan.FromSeconds(30));
    }

    /// <summary>The mission's steps as «from->to», in alphabetical order.</summary>
    private string[] StepsOf(PathMission mission) =>
        [.. mission.MissionSteps.Select(s => $"{_plant.NameOf(s.FromNodeId)}->{_plant.NameOf(s.ToNodeId)}").Order()];
}
