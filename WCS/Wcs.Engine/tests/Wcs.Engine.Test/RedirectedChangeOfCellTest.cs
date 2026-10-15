using System.Net;
using Wcs.Engine.Api;
using Wcs.Engine.Collections;
using Wcs.Engine.Items;
using Wcs.Engine.ORM;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Wcs.Engine.Test.Routing;
using Wcs.Wms.Contracts;
using Wcs.Wms.Contracts.Test;
using Xunit;
using static Wcs.Common.Enums;
using PathMission = Wcs.Engine.Items.PathMission;

namespace Wcs.Engine.Test;

/// <summary>
/// Item 54, task 3 (decision 3 and answers 1 and 2 of the owner of 13/10): a change of cell and a
/// defragmentation are redirected like every other mission. The operator's change of destination no
/// longer answers «not redirectable»: the mission takes the type of where it now goes (an extraction
/// towards a device, towards a storage a change of cell with the load unit still in its cell and a
/// deposit out of it), the route is the probed one written by the commit, and the cell it was going to
/// goes into the durable queue of the cells to give back, with the mission's row; the dispatcher of the
/// master loop gives it back by load unit and cell, retries until the warehouse answers, and frees the
/// door of the WCS's door map.
/// </summary>
/// <remarks>
/// The navigator is in <c>Stop</c> (the missions are added in <c>Recover</c>), the WCS's database is in
/// memory with no queued writer, the warehouse is the fake one, and no timed wait: the brake of the
/// dispatcher has a hand clock.
/// </remarks>
public sealed class RedirectedChangeOfCellTest : IDisposable
{
    private const decimal LoadUnitId = 42;
    private const decimal PickUpCellId = 5;
    private const string PickUpDoorCode = "A5";
    private const decimal StorageCellId = 6;
    private const string StorageDoorCode = "A6";
    private const decimal OtherCellId = 7;
    private const string OtherDoorCode = "A7";

    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly QueuedDatabaseWriter<WcsWrite>? _previousWriter = Navigator.StateWriter;
    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly RoutingTestPlant _plant = new();
    private readonly FakeWarehouse _warehouse = new();
    private readonly ManualClock _clock = new();

    public RedirectedChangeOfCellTest()
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
    // The operator's change of destination
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Answer 2 of the owner of 13/10: a change of cell or a defragmentation from the cell 5 of S towards
    /// the cell 6 of S2, sent by the operator to the device Y, becomes an extraction towards Y. The
    /// pick-up door stays (with the load unit in its cell it serves the pick-up), the storage door goes,
    /// and the route is the probed one: S->R->Y from the cell, C->R->Y from the conveyor C. The cell 6 goes
    /// into the durable queue of the cells to give back, in the same write as the mission's row; nothing
    /// is given back during the change. Before item 54 the change answered «not redirectable» and nothing
    /// moved.
    /// </summary>
    [Theory]
    [InlineData(MissionTypes.ChangeStoreLocation, true)]
    [InlineData(MissionTypes.ChangeStoreLocation, false)]
    [InlineData(MissionTypes.DefragmentCell, true)]
    [InlineData(MissionTypes.DefragmentCell, false)]
    public async Task SentToADeviceItBecomesAnExtractionAndItsCellIsQueuedToGoBack(MissionTypes type, bool inItsCell)
    {
        var mission = await AChangeOfCellFromS5ToS2Cell6(type, inItsCell);

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["Y"].ItemGuid);

        Assert.Equal(MissionDestinationOutcome.Changed, result.Outcome);
        Assert.Equal(_plant["Y"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.OperatorDestination);
        Assert.Equal(MissionTypes.Extraction, mission.MissionType);
        Assert.Equal(PickUpDoorCode, mission.PickUpDoorCode);
        Assert.Null(mission.StorageDoorCode);
        Assert.Equal(inItsCell ? ["R->Y", "S->R"] : ["C->R", "R->Y"], StepsOf(mission));

        var queued = Assert.Single(await CellGiveBackOutbox.PendingAsync(10));
        Assert.Equal(mission.MissionId, queued.MissionId);
        Assert.Equal(LoadUnitId, queued.LoadUnitId);
        Assert.Equal(StorageCellId, queued.CellId);
        Assert.Equal(StorageDoorCode, queued.DoorCode);
        Assert.Equal("KazamControl.Process", queued.UserId);
        Assert.Equal(
            $"The operator sent the mission to {_plant["Y"].ItemGuid}: the cell of the door A6 it was going to goes back to the warehouse",
            queued.Reason);
        Assert.NotEqual(Guid.Empty, queued.OperationId);
        Assert.False(queued.Parked);
        Assert.Empty(_warehouse.ReleasedLoadUnitCells);

        await using var context = _database.CreateContext();
        var row = context.Missions.Single(m => m.MissionId == mission.MissionId);
        Assert.Equal((short)MissionTypes.Extraction, row.MissionType);
        Assert.Null(row.StorageDoorCode);
    }

    /// <summary>
    /// Decision 3 and answer 1: the same missions sent by the operator to the storage S3 take the cell 7
    /// the warehouse reserves there. With the load unit still in its cell the mission is a change of cell
    /// towards A7, with the pick-up door A5 and the relocator R, and the route S->R->S3; out of its cell, on
    /// the conveyor C, it is a deposit towards A7 with no pick-up door, and the route C->R->S3. The cell 6
    /// goes into the queue to be given back. No report of an extraction leaving its order: a change of
    /// cell has none.
    /// </summary>
    [Theory]
    [InlineData(MissionTypes.ChangeStoreLocation, true)]
    [InlineData(MissionTypes.ChangeStoreLocation, false)]
    [InlineData(MissionTypes.DefragmentCell, true)]
    [InlineData(MissionTypes.DefragmentCell, false)]
    public async Task SentToAnotherStorageItTakesTheNewCellAndItsOldCellIsQueuedToGoBack(MissionTypes type, bool inItsCell)
    {
        var mission = await AChangeOfCellFromS5ToS2Cell6(type, inItsCell);
        TheWarehouseAnswers(OtherCellId, OtherDoorCode);

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S3"].ItemGuid);

        Assert.Equal(MissionDestinationOutcome.Changed, result.Outcome);
        Assert.Equal(_plant["S3"].ItemGuid, mission.DestinationItemId);
        Assert.Equal(OtherDoorCode, mission.StorageDoorCode);
        if (inItsCell)
        {
            Assert.Equal(MissionTypes.ChangeStoreLocation, mission.MissionType);
            Assert.Equal(PickUpDoorCode, mission.PickUpDoorCode);
            Assert.Equal(_plant["R"].ItemGuid, mission.RelocateItemId);
            Assert.Equal(["R->S3", "S->R"], StepsOf(mission));
        }
        else
        {
            Assert.Equal(MissionTypes.Storing, mission.MissionType);
            Assert.Null(mission.PickUpDoorCode);
            Assert.Equal(["C->R", "R->S3"], StepsOf(mission));
        }

        var queued = Assert.Single(await CellGiveBackOutbox.PendingAsync(10));
        Assert.Equal((mission.MissionId, LoadUnitId, StorageCellId, StorageDoorCode),
            (queued.MissionId, queued.LoadUnitId, queued.CellId, queued.DoorCode));
        Assert.Empty(_warehouse.MissionsLeftAssignmentUnserved);
        Assert.Empty(_warehouse.LoadUnitsLeftExtractionQueue);
        Assert.Empty(_warehouse.ReleasedLoadUnitCells);
    }

    /// <summary>
    /// Una missione di cambio cella nata nel motore puo' tenere la vecchia cella soltanto per UDC,
    /// senza il legame per missione. La prenotazione nuova la sostituisce anche in questo caso: se il
    /// commit perde la corsa, porta e rotta vecchie non possono restare e la restituzione della vecchia
    /// cella deve essere durevole. Vale sia per la scelta ordinaria sia per la cella precisa.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ALateCommitLossAfterReplacingAnUnlinkedCellQueuesItsDurableGiveBack(bool precise)
    {
        var mission = await AChangeOfCellFromS5ToS2Cell6(MissionTypes.ChangeStoreLocation, inItsCell: true);
        await TheDoorA6IsLockedForTheDeposit();
        _warehouse.LoadUnitCellsHeld[LoadUnitId] = StorageCellId;
        var newReservation = new StorageReservation(Guid.Empty, StorageReservationOutcome.CellFound,
            OtherCellId, OtherDoorCode, Partition: "0013", StorageDepth: 150, NeedsCompaction: false);
        if (precise)
            _warehouse.ReservableStorageCells[OtherDoorCode] = newReservation;
        else
            _warehouse.StorageReservations[LoadUnitId] = newReservation;

        mission.CommitRoutePauseForTesting = () =>
        {
            mission.CommitRoutePauseForTesting = null;
            Assert.True(mission.RemoveTheRouteSeenWithoutAStepToDo(mission.RouteVersion));
        };

        var result = precise
            ? await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S3"].ItemGuid,
                MissionGatewayChoice.Keep, MissionDestinationOptions.Cell(OtherDoorCode))
            : await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S3"].ItemGuid);

        Assert.Equal(MissionDestinationOutcome.StepInFlight, result.Outcome);
        Assert.True(result.LostTheCell);
        Assert.True(mission.StorageCellLost);
        Assert.Null(mission.StorageDoorCode);
        Assert.Empty(mission.MissionSteps);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
        Assert.False(_warehouse.LoadUnitCellsHeld.ContainsKey(LoadUnitId));
        Assert.False(_warehouse.StorageReservationsByMission.ContainsKey(mission.MissionId));

        var queued = Assert.Single(await CellGiveBackOutbox.PendingAsync(10));
        Assert.Equal((mission.MissionId, LoadUnitId, StorageCellId, StorageDoorCode),
            (queued.MissionId, queued.LoadUnitId, queued.CellId, queued.DoorCode));

        await new PendingCellGiveBackDispatcher(_clock).TryDispatchAsync();

        Assert.Empty(await CellGiveBackOutbox.PendingAsync(10));
        Assert.Equal((short)StatoOperazioneCella.Nessuna, await TheDoorA6Operation());
        Assert.False(_warehouse.StorageReservationsByMission.ContainsKey(mission.MissionId));
    }

    /// <summary>
    /// The operator sends a change of cell back to the storage it was going to, and the warehouse gives it
    /// the same cell 6: the mission keeps it, and nothing is queued to be given back - the queue would
    /// release the cell the mission goes to.
    /// </summary>
    [Fact]
    public async Task SentAgainToItsStorageWithTheSameCellNothingIsQueued()
    {
        var mission = await AChangeOfCellFromS5ToS2Cell6(MissionTypes.ChangeStoreLocation, inItsCell: true);
        TheWarehouseAnswers(StorageCellId, StorageDoorCode);

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S2"].ItemGuid);

        Assert.Equal(MissionDestinationOutcome.Changed, result.Outcome);
        Assert.Equal(StorageDoorCode, mission.StorageDoorCode);
        Assert.Equal(MissionTypes.ChangeStoreLocation, mission.MissionType);
        Assert.Empty(await CellGiveBackOutbox.PendingAsync(10));
    }

    /// <summary>
    /// A change that is refused (no route towards the device Z, which nothing reaches) leaves the change
    /// of cell as it was, and queues nothing.
    /// </summary>
    [Fact]
    public async Task ARefusedChangeQueuesNothing()
    {
        var mission = await AChangeOfCellFromS5ToS2Cell6(MissionTypes.ChangeStoreLocation, inItsCell: true);
        _plant.Device("Z");

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["Z"].ItemGuid);

        Assert.Equal(MissionDestinationOutcome.NoRoute, result.Outcome);
        Assert.Equal(MissionTypes.ChangeStoreLocation, mission.MissionType);
        Assert.Equal(StorageDoorCode, mission.StorageDoorCode);
        Assert.Empty(await CellGiveBackOutbox.PendingAsync(10));
    }

    // ---------------------------------------------------------------------------------------
    // The move
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Decision 3: a change of cell moved by hand out of its cell, onto the conveyor C (no storing mode,
    /// no fixed destination), has the route at once, like every mission the navigator sends straight:
    /// out of its cell the doors make it the deposit it physically is (decision 2), towards its cell 6 of
    /// S2, which it keeps. Before item 54 the move left a change of cell in the queue of the route loop.
    /// </summary>
    [Fact]
    public async Task AChangeOfCellMovedOutOfItsCellHasItsRouteAtOnceAsADeposit()
    {
        var mission = await AChangeOfCellFromS5ToS2Cell6(MissionTypes.ChangeStoreLocation, inItsCell: true);

        Assert.Equal(MissionMoveOutcome.Moved, MissionGateway.Move(mission.MissionId, _plant["C"].ItemGuid));

        Assert.Equal(MissionTypes.Storing, mission.MissionType);
        Assert.Null(mission.PickUpDoorCode);
        Assert.Equal(StorageDoorCode, mission.StorageDoorCode);
        Assert.Equal(_plant["S2"].ItemGuid, mission.DestinationItemId);
        Assert.Equal(["C->R", "R->S2"], StepsOf(mission));
        Assert.Null(RoutesToBuild.Instance[mission.MissionId]);
        await mission.PositionWork;
        Assert.Empty(_warehouse.Calls);
        Assert.Empty(await CellGiveBackOutbox.PendingAsync(10));
    }

    // ---------------------------------------------------------------------------------------
    // The dispatcher
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Answer 1: the dispatcher gives the cell back by load unit and cell, with the fields of the row; the
    /// warehouse, which held the cell 6 for the load unit 42 without a mission's link (the obstacle, the
    /// reload), releases it; the door of the cell in the WCS's door map, locked for the deposit, is freed;
    /// and the row leaves the queue.
    /// </summary>
    [Fact]
    public async Task TheCellGoesBackByLoadUnitAndCellAndItsDoorIsFreed()
    {
        await TheDoorA6IsLockedForTheDeposit();
        _warehouse.LoadUnitCellsHeld[LoadUnitId] = StorageCellId;
        var queued = await Queue(AGiveBack());

        await new PendingCellGiveBackDispatcher(_clock).TryDispatchAsync();

        Assert.Equal(new ReleaseLoadUnitCellRequest(queued.OperationId, queued.MissionId, LoadUnitId, StorageCellId,
            "KazamControl.Process", "redirected"), Assert.Single(_warehouse.ReleasedLoadUnitCells));
        Assert.Empty(_warehouse.LoadUnitCellsHeld);
        Assert.Empty(await AllRows());
        Assert.Equal((short)StatoOperazioneCella.Nessuna, await TheDoorA6Operation());
    }

    /// <summary>
    /// A cell the warehouse no longer holds for the load unit (replaced by the new reservation) is an
    /// answer, not a failure: the row leaves the queue and the door is freed all the same.
    /// </summary>
    [Fact]
    public async Task ACellNoLongerHeldStillLeavesTheQueueAndFreesTheDoor()
    {
        await TheDoorA6IsLockedForTheDeposit();
        await Queue(AGiveBack());

        await new PendingCellGiveBackDispatcher(_clock).TryDispatchAsync();

        Assert.Single(_warehouse.ReleasedLoadUnitCells);
        Assert.Empty(await AllRows());
        Assert.Equal((short)StatoOperazioneCella.Nessuna, await TheDoorA6Operation());
    }

    /// <summary>
    /// A cell another operation still holds (the warehouse says a deposit is under way there) keeps the
    /// lock of its door: the release of the load unit's reservation does not free a door someone uses.
    /// </summary>
    [Fact]
    public async Task ADoorAnotherOperationHoldsStaysLocked()
    {
        await TheDoorA6IsLockedForTheDeposit();
        _warehouse.CellBusyStates[StorageCellId] = new CellBusyState(Extraction: false, PickUp: false, Storing: true);
        await Queue(AGiveBack());

        await new PendingCellGiveBackDispatcher(_clock).TryDispatchAsync();

        Assert.Single(_warehouse.ReleasedLoadUnitCells);
        Assert.Empty(await AllRows());
        Assert.Equal((short)StatoOperazioneCella.Deposito, await TheDoorA6Operation());
    }

    /// <summary>
    /// With the warehouse mute the row stays, with the attempt counted, and the brake does not send it
    /// again before its time; after it the dispatcher tries again, and the warehouse that answers takes
    /// it out. Retried until it succeeds.
    /// </summary>
    [Fact]
    public async Task WithTheWarehouseMuteTheRowStaysAndIsTriedAgain()
    {
        await TheDoorA6IsLockedForTheDeposit();
        _warehouse.LoadUnitCellsHeld[LoadUnitId] = StorageCellId;
        await Queue(AGiveBack());
        _warehouse.Unavailable = true;
        var dispatcher = new PendingCellGiveBackDispatcher(_clock);

        await dispatcher.TryDispatchAsync();

        var kept = Assert.Single(await AllRows());
        Assert.Equal(1, kept.AttemptCount);
        Assert.NotNull(kept.LastError);
        Assert.False(kept.Parked);
        Assert.Equal((short)StatoOperazioneCella.Deposito, await TheDoorA6Operation());

        await dispatcher.TryDispatchAsync();
        Assert.Single(_warehouse.Calls, nameof(IWarehouse.ReleaseLoadUnitCellAsync));

        _warehouse.Unavailable = false;
        _clock.Advance(TimeSpan.FromSeconds(2));
        await dispatcher.TryDispatchAsync();

        Assert.Single(_warehouse.ReleasedLoadUnitCells);
        Assert.Empty(_warehouse.LoadUnitCellsHeld);
        Assert.Empty(await AllRows());
        Assert.Equal((short)StatoOperazioneCella.Nessuna, await TheDoorA6Operation());
    }

    /// <summary>
    /// A warehouse that refuses for good (a 4xx) parks the row with the reason, and the next row is sent.
    /// </summary>
    [Fact]
    public async Task ARejectedRowIsParkedAndTheNextOneIsSent()
    {
        var first = await Queue(AGiveBack());
        var second = await Queue(AGiveBack(cellId: OtherCellId, doorCode: OtherDoorCode));
        _warehouse.Fault = operation => operation == nameof(IWarehouse.ReleaseLoadUnitCellAsync) &&
                                        _warehouse.ReleasedLoadUnitCells.Count == 0 &&
                                        _warehouse.Calls.Count(call => call == operation) == 1
            ? new WarehouseUnavailableException(operation, new HttpRequestException("no", null, HttpStatusCode.Conflict))
            : null;

        await new PendingCellGiveBackDispatcher(_clock).TryDispatchAsync();

        var parked = Assert.Single(await AllRows());
        Assert.Equal(first.Id, parked.Id);
        Assert.True(parked.Parked);
        Assert.Equal(1, parked.AttemptCount);
        Assert.Equal(second.OperationId, Assert.Single(_warehouse.ReleasedLoadUnitCells).OperationId);
    }

    /// <summary>
    /// A cell an active mission of the same load unit goes to again (the operator sent it back to the
    /// same storage and the warehouse gave it the same cell) is not given back: the row is dropped without
    /// calling the warehouse, and the door stays locked for the deposit.
    /// </summary>
    [Fact]
    public async Task ACellAMissionGoesToAgainIsNotGivenBack()
    {
        var mission = await AChangeOfCellFromS5ToS2Cell6(MissionTypes.ChangeStoreLocation, inItsCell: true);
        await TheDoorA6IsLockedForTheDeposit();
        await Queue(AGiveBack());
        Assert.Equal(StorageDoorCode, mission.StorageDoorCode);
        _warehouse.Calls.Clear();

        await new PendingCellGiveBackDispatcher(_clock).TryDispatchAsync();

        Assert.Empty(await AllRows());
        Assert.Empty(_warehouse.Calls);
        Assert.Equal((short)StatoOperazioneCella.Deposito, await TheDoorA6Operation());
    }

    /// <summary>
    /// Fix round 1, finding 1: the change of cell redirected to Y left the cell 6 in the queue, and the
    /// operator sends the mission back to S2, where the warehouse reserves the cell 6 again. The
    /// dispatcher turns while the change has reserved and not yet written the door (the pause of the
    /// commit): it does not give the cell back - the gateway holds the claim of the load unit from before
    /// the reservation to after the commit - and the change takes the row out of the queue, so no later
    /// turn gives back the cell the mission now goes to. Before the fix the dispatcher saw no mission
    /// going to the cell 6, released it by load unit and cell (in the fake also the mission's new
    /// reservation) and freed its door.
    /// </summary>
    [Fact]
    public async Task ACellReservedAgainWhileItsRowIsPendingIsNotGivenBack()
    {
        var mission = await AChangeOfCellFromS5ToS2Cell6(MissionTypes.ChangeStoreLocation, inItsCell: true);
        Assert.Equal(MissionDestinationOutcome.Changed,
            (await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["Y"].ItemGuid)).Outcome);
        Assert.Single(await CellGiveBackOutbox.PendingAsync(10));
        await TheDoorA6IsLockedForTheDeposit();
        TheWarehouseAnswers(StorageCellId, StorageDoorCode);
        var dispatcher = new PendingCellGiveBackDispatcher(_clock);
        var turnedDuringTheCommit = false;
        mission.CommitRoutePauseForTesting = () =>
        {
            mission.CommitRoutePauseForTesting = null;
            turnedDuringTheCommit = true;
            dispatcher.TryDispatchAsync().GetAwaiter().GetResult();
        };

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S2"].ItemGuid);

        Assert.True(turnedDuringTheCommit);
        Assert.Equal(MissionDestinationOutcome.Changed, result.Outcome);
        Assert.Equal(StorageDoorCode, mission.StorageDoorCode);
        Assert.Empty(_warehouse.ReleasedLoadUnitCells);
        Assert.Equal(StorageCellId, _warehouse.StorageReservationsByMission[mission.MissionId].CellId);
        Assert.Equal((short)StatoOperazioneCella.Deposito, await TheDoorA6Operation());
        Assert.Empty(await AllRows());
    }

    /// <summary>
    /// Fix round 2, finding 1: the other order of the same race - the dispatcher looks first. The change
    /// of cell redirected to Y left the cell 6 in the queue; the dispatcher has looked at the row (no
    /// mission goes to the cell 6) and is releasing it when the operator sends the mission back to S2,
    /// where the warehouse gives it the cell 6 again. The change waits for the claim of the load unit,
    /// which the dispatcher keeps across the release, so it reserves only after the release: the cell
    /// the mission now goes to is not the one given back. A dispatcher that kept the claim only for its
    /// look, or a gateway that did not wait for it, would let the change reserve during the release,
    /// and the release by load unit and cell (in the fake also by mission) would free it.
    /// </summary>
    [Fact]
    public async Task AChangeBackStartedDuringTheReleaseWaitsForIt()
    {
        var mission = await AChangeOfCellFromS5ToS2Cell6(MissionTypes.ChangeStoreLocation, inItsCell: true);
        Assert.Equal(MissionDestinationOutcome.Changed,
            (await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["Y"].ItemGuid)).Outcome);
        Assert.Single(await CellGiveBackOutbox.PendingAsync(10));
        Assert.False(_warehouse.StorageReservationsByMission.ContainsKey(mission.MissionId));
        await TheDoorA6IsLockedForTheDeposit();
        TheWarehouseAnswers(StorageCellId, StorageDoorCode);

        var waitingForTheClaim = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<MissionDestinationResult>? change = null;
        bool? reservedDuringTheRelease = null;
        _warehouse.Fault = operation =>
        {
            if (operation != nameof(IWarehouse.ReleaseLoadUnitCellAsync) || change != null) return null;

            // The operator's change back to S2, not awaited: either it waits for the claim, or it runs
            // to its end while the release is under way.
            change = Task.Run(() => MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S2"].ItemGuid));
            Task.WhenAny(change, waitingForTheClaim.Task).GetAwaiter().GetResult();
            reservedDuringTheRelease = _warehouse.StorageReservationsByMission.ContainsKey(mission.MissionId);
            return null;
        };
        LoadUnitCellClaims.WaitingForTesting = loadUnitId =>
        {
            if (loadUnitId == LoadUnitId) waitingForTheClaim.TrySetResult();
        };
        try
        {
            await new PendingCellGiveBackDispatcher(_clock).TryDispatchAsync();
            Assert.NotNull(change);
            var result = await change;

            Assert.False(reservedDuringTheRelease);
            Assert.True(waitingForTheClaim.Task.IsCompleted);
            Assert.Equal(MissionDestinationOutcome.Changed, result.Outcome);
            Assert.Equal(StorageDoorCode, mission.StorageDoorCode);
            Assert.Equal(StorageCellId, Assert.Single(_warehouse.ReleasedLoadUnitCells).CellId);
            Assert.Equal(StorageCellId, _warehouse.StorageReservationsByMission[mission.MissionId].CellId);
            // The release came first, when the warehouse held nothing for the cell 6, so its door was
            // freed; the operator's change does not lock the door (the managers that create a deposit do).
            Assert.Equal((short)StatoOperazioneCella.Nessuna, await TheDoorA6Operation());
            Assert.Empty(await AllRows());
        }
        finally
        {
            LoadUnitCellClaims.WaitingForTesting = null;
        }
    }

    /// <summary>
    /// Fix round 1, finding 1: a row taken out of the queue after the dispatcher read its batch (by the
    /// change that reserved its cell again, see above) is not given back: the dispatcher reads the row
    /// again under the claim of its load unit. Here the change is played by the release of the row before
    /// it, which deletes the second one; no mission of the load unit is active, so only the second look
    /// keeps the cell.
    /// </summary>
    [Fact]
    public async Task ARowTakenOutAfterTheBatchWasReadIsNotGivenBack()
    {
        var firstRow = AGiveBack(cellId: OtherCellId, doorCode: OtherDoorCode);
        firstRow.LoadUnitId = 43;
        var first = await Queue(firstRow);
        var second = await Queue(AGiveBack());
        _warehouse.Fault = operation =>
        {
            if (operation == nameof(IWarehouse.ReleaseLoadUnitCellAsync) && _warehouse.ReleasedLoadUnitCells.Count == 0)
                CellGiveBackOutbox.DeleteAsync(second.Id).GetAwaiter().GetResult();
            return null;
        };

        await new PendingCellGiveBackDispatcher(_clock).TryDispatchAsync();

        Assert.Equal(first.OperationId, Assert.Single(_warehouse.ReleasedLoadUnitCells).OperationId);
        Assert.Empty(await AllRows());
    }

    /// <summary>
    /// The master loop delivers the queue on every turn, also with the navigator stopped, like the fact of
    /// item 45; and a row written before a restart is still there for it.
    /// </summary>
    [Fact]
    public async Task TheMasterLoopDeliversTheQueue()
    {
        _warehouse.LoadUnitCellsHeld[LoadUnitId] = StorageCellId;
        await Queue(AGiveBack());
        Navigator.CellGiveBackDispatcher.Reset();
        Navigator.NavStatus = NavigatorStatusTypes.Stop;

        await Navigator.MasterProcess();

        Assert.Single(_warehouse.ReleasedLoadUnitCells);
        Assert.Empty(await AllRows());
    }

    // ---------------------------------------------------------------------------------------
    // Scene
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The storage S with the door A5 on the cell 5, S2 with A6 on the cell 6, S3 with A7 on the cell 7,
    /// all relocated by R; S->R, R->X, R->Y, R->S2, R->S3, and the conveyor C->R. A mission of the given
    /// type for the load unit 42, from the cell 5 towards the cell 6 of S2 (pick-up door A5, storage door
    /// A6, relocator R): with <paramref name="inItsCell"/> the load unit is still in its cell (no position,
    /// the route S->R->S2), otherwise on C (the route C->R->S2). The cell 7 has its field address. The
    /// navigator is stopped.
    /// </summary>
    private async Task<PathMission> AChangeOfCellFromS5ToS2Cell6(MissionTypes type, bool inItsCell)
    {
        foreach (var name in new[] { "S", "S2", "S3" }) _plant.Device(name, DeviceTypes.Storage);
        foreach (var name in new[] { "R", "X", "Y", "C" }) _plant.Device(name);
        _plant.Link("S", "R");
        _plant.Link("R", "X");
        _plant.Link("R", "Y");
        _plant.Link("R", "S2");
        _plant.Link("R", "S3");
        _plant.Link("C", "R");
        foreach (var name in new[] { "S", "S2", "S3" }) _plant[name].RelocateItemId = _plant["R"].ItemGuid;

        await PlantPersistence.SaveDoorMappingsAsync(_plant["S"].ItemGuid,
            [new DoorMapping { ItemId = _plant["S"].ItemGuid, DoorCode = PickUpDoorCode, CellId = PickUpCellId, Side = "A" }]);
        await PlantPersistence.SaveDoorMappingsAsync(_plant["S2"].ItemGuid,
            [new DoorMapping { ItemId = _plant["S2"].ItemGuid, DoorCode = StorageDoorCode, CellId = StorageCellId, Side = "A" }]);
        await PlantPersistence.SaveDoorMappingsAsync(_plant["S3"].ItemGuid,
            [new DoorMapping { ItemId = _plant["S3"].ItemGuid, DoorCode = OtherDoorCode, CellId = OtherCellId, Side = "A" }]);
        _warehouse.Cells[PickUpCellId] = Cell(PickUpCellId, PickUpDoorCode);
        _warehouse.Cells[StorageCellId] = Cell(StorageCellId, StorageDoorCode);
        _warehouse.Cells[OtherCellId] = Cell(OtherCellId, OtherDoorCode);
        _warehouse.CellPartitionMaps[StorageCellId] = new CellPartitionMap(StorageCellId, "0061", "0062",
            AsiDoorB: string.Empty, SubitemDoorB: string.Empty, [new CellPartition("0063", -1)]);
        _warehouse.CellPartitionMaps[OtherCellId] = new CellPartitionMap(OtherCellId, "0071", "0072",
            AsiDoorB: string.Empty, SubitemDoorB: string.Empty, [new CellPartition("0073", -1)]);

        var mission = new PathMission
        {
            LuId = LoadUnitId,
            PickUpDoorCode = PickUpDoorCode,
            StorageDoorCode = StorageDoorCode,
            SourceItemId = _plant["S"].ItemGuid,
            RelocateItemId = _plant["R"].ItemGuid,
            DestinationItemId = _plant["S2"].ItemGuid,
            MissionType = type
        };
        if (!inItsCell) mission.PositionItemId = _plant["C"].ItemGuid;

        Assert.Equal(NavigatorStatusTypes.Recover, Navigator.NavStatus);
        ActiveMissions.Instance.Add(mission, buildRoute: false);
        Assert.Same(mission, ActiveMissions.Instance[mission.MissionId]);
        await mission.PositionWork;

        _plant.PlannedStep(mission, inItsCell ? "S" : "C", "R");
        _plant.PlannedStep(mission, "R", "S2");
        Assert.Equal(type, mission.MissionType);
        Assert.Equal(inItsCell, mission.LoadUnitIsInItsStorageCell());
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        _warehouse.Calls.Clear();
        return mission;
    }

    /// <summary>The cell the warehouse answers for the load unit, with the partition 0013 and the depth 150.</summary>
    private void TheWarehouseAnswers(decimal cellId, string doorCode) =>
        _warehouse.StorageReservations[LoadUnitId] = new StorageReservation(Guid.Empty,
            StorageReservationOutcome.CellFound, cellId, doorCode, Partition: "0013", StorageDepth: 150,
            NeedsCompaction: false);

    /// <summary>The door A6 of the cell 6, in the WCS's door map, locked for a deposit (the obstacle's change of cell).</summary>
    private async Task TheDoorA6IsLockedForTheDeposit()
    {
        var storage = _plant.DeviceNames.Contains("S2") ? _plant["S2"] : _plant.Device("S2", DeviceTypes.Storage);
        await PlantPersistence.SaveDoorMappingsAsync(storage.ItemGuid,
            [new DoorMapping { ItemId = storage.ItemGuid, DoorCode = StorageDoorCode, CellId = StorageCellId, Side = "A" }]);
        await DbInfo.SetOperationLockingCell(StorageCellId, StatoOperazioneCella.Deposito);
        Assert.Equal((short)StatoOperazioneCella.Deposito, await TheDoorA6Operation());
    }

    private async Task<short> TheDoorA6Operation()
    {
        await using var context = _database.CreateContext();
        return context.DoorMappings.Single(m => m.DoorCode == StorageDoorCode).OperationActive;
    }

    private static PendingCellGiveBack AGiveBack(decimal cellId = StorageCellId, string doorCode = StorageDoorCode) => new()
    {
        OperationId = Guid.NewGuid(),
        MissionId = new Guid("4b0e5a1c-0054-4000-8000-000000000003"),
        LoadUnitId = LoadUnitId,
        CellId = cellId,
        DoorCode = doorCode,
        UserId = "KazamControl.Process",
        Reason = "redirected",
        CreatedAtUtc = new DateTimeOffset(2026, 10, 13, 9, 0, 0, TimeSpan.Zero)
    };

    private static async Task<PendingCellGiveBack> Queue(PendingCellGiveBack giveBack)
    {
        await ExtractionQueue.WriteAsync(context =>
        {
            PendingCellGiveBackStore.Add(context, giveBack);
            return Task.CompletedTask;
        });
        return giveBack;
    }

    private async Task<List<PendingCellGiveBack>> AllRows()
    {
        await using var context = _database.CreateContext();
        return [.. context.PendingCellGiveBacks.OrderBy(row => row.Id)];
    }

    /// <summary>The mission's steps as «from->to», in alphabetical order.</summary>
    private string[] StepsOf(PathMission mission) =>
        [.. mission.MissionSteps.Select(s => $"{_plant.NameOf(s.FromNodeId)}->{_plant.NameOf(s.ToNodeId)}").Order()];

    private static CellDescription Cell(decimal cellId, string doorCode) =>
        new(cellId, AreaId: 1, WarehouseId: 1, Depth: 1, Inverted: false, CellLockState.None,
            AllowedLoadUnitTypeId: null, GroupId: null, GroupOrder: 0, doorCode, DoorCodeB: string.Empty,
            DoorCount: 1, MultiDepthStorage: false);
}
