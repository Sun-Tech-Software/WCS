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

namespace Wcs.Engine.Test;

/// <summary>
/// Item 54, decision 4 of the owner of 13/10: a change of destination or a move asked while a step of the
/// mission is in flight is not refused any more. It waits (<see cref="PendingMissionChanges"/>), one per
/// mission and the last one wins, and it is applied as soon as the step ends, from where the step ends;
/// the step in flight wins, and the old route takes no other step meanwhile.
/// </summary>
/// <remarks>
/// The plant: P->A->B, the old route of the mission (bound for B, on P, with the step P->A in flight on
/// P), A->X, and Q and Z linked to nothing (Q for the moves, Z for a change without a route). The engine
/// is stopped (the missions are added in Recover); the WCS database is in memory, the warehouse is the
/// fake one. The end of a step is played as the engine plays it (<c>FinalizeExecutedStep</c>): the
/// arrival writes the position (<c>ThreadLoadLu</c>), and the step leaves the route through its source
/// (<c>AutomationDevice.RemoveStep</c>), in either order. No timed waits: the applications started so far
/// are awaited (<see cref="PendingMissionChanges.WhenIdleAsync"/>), with a ceiling that fails instead of
/// hanging.
/// </remarks>
public sealed class PendingMissionChangeTest : IDisposable
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);

    private sealed class Recorder : IEngineEventSink
    {
        private readonly Lock _lock = new();
        private readonly List<(EngineMissionChange Change, Guid MissionId)> _missions = [];

        public IReadOnlyList<(EngineMissionChange Change, Guid MissionId)> Missions
        {
            get
            {
                lock (_lock) return [.. _missions];
            }
        }

        public void MissionChanged(PathMission mission, EngineMissionChange change)
        {
            lock (_lock) _missions.Add((change, mission.MissionId));
        }

        public void ItemChanged(AutomationDevice device) { }
        public void NavigatorStatusChanged() { }
        public void ExtractionQueueChanged(ExtractionQueueItem item, EngineQueueChange change) { }
        public void CommandChanged(CommandRecord command) { }
    }

    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly QueuedDatabaseWriter<WcsWrite>? _previousWriter = Navigator.StateWriter;
    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly RoutingTestPlant _plant = new();
    private readonly FakeWarehouse _warehouse = new();
    private readonly Recorder _recorder = new();

    public PendingMissionChangeTest()
    {
        Navigator.Store = _database;
        Navigator.StateWriter = null;
        Navigator.Warehouse = _warehouse;
        RoutesToBuild.Instance.Items = new();
    }

    public void Dispose()
    {
        PendingMissionChanges.BeforeKeepingForTesting = null;
        Navigator.EventSink = null;
        RoutesToBuild.Instance.Items = new();
        _plant.Dispose();
        Navigator.Warehouse = _previousWarehouse;
        Navigator.StateWriter = _previousWriter;
        Navigator.Store = null;
        _database.Dispose();
    }

    // ---------------------------------------------------------------------------------------
    // The change of destination
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The everyday case: the operator sends the load unit to X while it moves from P to A. The answer is
    /// «applied at the end of the movement», nothing changes while the step flies, and the mission says it
    /// to the clients; when the step ends on A the change is applied from A: destination X, the operator's
    /// mark, the route A->X, and the step A->B of the old route gone.
    /// </summary>
    [Fact]
    public async Task AChangeAskedDuringAStepIsAppliedFromWhereTheStepEnds()
    {
        var (mission, flying, next) = await AMissionFlyingFromPToA();
        Navigator.EventSink = _recorder;

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["X"].ItemGuid);
        await Idle();

        Assert.Equal(MissionDestinationOutcome.Pending, result.Outcome);
        Assert.Equal(PendingMissionChange.Destination(_plant["X"].ItemGuid, MissionGatewayChoice.Keep),
            MissionGateway.PendingChangeOf(mission.MissionId));
        Assert.Contains((EngineMissionChange.StepChanged, mission.MissionId), _recorder.Missions);
        Assert.Equal(_plant["B"].ItemGuid, mission.DestinationItemId);
        Assert.False(mission.OperatorDestination);
        Assert.Equal(["P", "A", "B"], RouteOf(mission));
        Assert.Contains(flying, mission.MissionSteps);

        await TheStepArrives(mission, "A");
        Assert.Equal(_plant["B"].ItemGuid, mission.DestinationItemId);
        await TheStepLeavesTheRoute(flying);

        Assert.Null(MissionGateway.PendingChangeOf(mission.MissionId));
        Assert.Equal(_plant["X"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.OperatorDestination);
        Assert.Equal(["A", "X"], RouteOf(mission));
        Assert.DoesNotContain(next, MissionSteps.Instance.List);
    }

    /// <summary>
    /// Item 54, task 6: what goes with the change waits with it. The operator sends the load unit to the
    /// relocator X (it relocates the storage SX) «only to bring it there» while it moves from P to A: the
    /// pending change keeps the choice, and applied from A it writes it with the destination.
    /// </summary>
    [Fact]
    public async Task AChangeToARelocatorDuringAStepKeepsItsChoiceUntilItIsApplied()
    {
        var (mission, flying, _) = await AMissionFlyingFromPToA();
        var storage = _plant.Device("SX", DeviceTypes.Storage);
        storage.RelocateItemId = _plant["X"].ItemGuid;
        var options = MissionDestinationOptions.Relocator(RelocatorArrival.Stay);

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["X"].ItemGuid,
            MissionGatewayChoice.Keep, options);
        await Idle();

        Assert.Equal(MissionDestinationOutcome.Pending, result.Outcome);
        Assert.Equal(PendingMissionChange.Destination(_plant["X"].ItemGuid, MissionGatewayChoice.Keep, options),
            MissionGateway.PendingChangeOf(mission.MissionId));
        Assert.Equal(RelocatorArrival.None, mission.RelocatorArrival);

        await TheStepArrives(mission, "A");
        await TheStepLeavesTheRoute(flying);

        Assert.Null(MissionGateway.PendingChangeOf(mission.MissionId));
        Assert.Equal(_plant["X"].ItemGuid, mission.DestinationItemId);
        Assert.Equal(RelocatorArrival.Stay, mission.RelocatorArrival);
        Assert.Equal(["A", "X"], RouteOf(mission));
    }

    /// <summary>
    /// The step leaves the route before its arrival writes the position (the arrival runs on the thread
    /// pool, <c>ThreadLoadLu</c>): the load unit is not on P any more, and the change does not compute its
    /// route from there. It waits for the arrival, and is applied from A.
    /// </summary>
    [Fact]
    public async Task AStepThatLeavesTheRouteBeforeItsArrivalMakesTheChangeWaitForTheArrival()
    {
        var (mission, flying, _) = await AMissionFlyingFromPToA();

        Assert.Equal(MissionDestinationOutcome.Pending,
            (await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["X"].ItemGuid)).Outcome);
        await Idle();

        await TheStepLeavesTheRoute(flying);
        Assert.NotNull(MissionGateway.PendingChangeOf(mission.MissionId));
        Assert.Equal(_plant["B"].ItemGuid, mission.DestinationItemId);

        await TheStepArrives(mission, "A");

        Assert.Null(MissionGateway.PendingChangeOf(mission.MissionId));
        Assert.Equal(_plant["X"].ItemGuid, mission.DestinationItemId);
        Assert.Equal(["A", "X"], RouteOf(mission));
    }

    /// <summary>
    /// While the change waits, no step of the mission starts: the old route does not take one more step
    /// before it (<c>PathMission.TryBeginStepStart</c> answers no). Once it is applied the starts are free.
    /// </summary>
    [Fact]
    public async Task WhileTheChangeWaitsNoStepOfTheMissionStarts()
    {
        var (mission, flying, _) = await AMissionFlyingFromPToA();
        Assert.Equal(MissionDestinationOutcome.Pending,
            (await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["X"].ItemGuid)).Outcome);
        await Idle();

        Assert.False(mission.TryBeginStepStart());

        await TheStepArrives(mission, "A");
        await TheStepLeavesTheRoute(flying);

        Assert.True(mission.TryBeginStepStart());
        mission.EndStepStart();
    }

    /// <summary>
    /// From where the step ends there is no route to Z: the change is refused for good when it is applied.
    /// It is gone (the row no longer shows it), the mission goes on with its route, and its steps start again.
    /// </summary>
    [Fact]
    public async Task AChangeRefusedWhenItIsAppliedIsDroppedAndTheMissionGoesOn()
    {
        var (mission, flying, next) = await AMissionFlyingFromPToA();
        Assert.Equal(MissionDestinationOutcome.Pending,
            (await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["Z"].ItemGuid)).Outcome);
        await Idle();

        await TheStepArrives(mission, "A");
        await TheStepLeavesTheRoute(flying);

        Assert.Null(MissionGateway.PendingChangeOf(mission.MissionId));
        Assert.Equal(_plant["B"].ItemGuid, mission.DestinationItemId);
        Assert.False(mission.OperatorDestination);
        Assert.Equal([next], mission.MissionSteps);
        Assert.True(mission.TryBeginStepStart());
        mission.EndStepStart();
    }

    /// <summary>
    /// The step ends in error: its device goes into Warning, the step stays in the route, and nothing is in
    /// flight any more. Nobody announces it: the master loop sees it, in every state of the engine, and the
    /// change is applied from where the load unit stopped, P (route P->A->X).
    /// </summary>
    [Fact]
    public async Task AStepThatEndsInErrorLetsTheMasterLoopApplyTheChangeFromWhereTheLoadUnitStopped()
    {
        var (mission, _, _) = await AMissionFlyingFromPToA();
        Assert.Equal(MissionDestinationOutcome.Pending,
            (await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["X"].ItemGuid)).Outcome);
        await Idle();

        _plant["P"].Status = DeviceStatusTypes.Warning;
        await Navigator.MasterProcess();
        await Idle();

        Assert.Null(MissionGateway.PendingChangeOf(mission.MissionId));
        Assert.Equal(_plant["X"].ItemGuid, mission.DestinationItemId);
        Assert.Equal(_plant["P"].ItemGuid, mission.PositionItemId);
        Assert.Equal(["P", "A", "X"], RouteOf(mission));
    }

    /// <summary>
    /// The step left the route and its arrival never comes (an engine that lost it): the mission is not
    /// held for ever. The master loop counts its rounds, and at the fifth the change is applied anyway,
    /// from where the mission is.
    /// </summary>
    [Fact]
    public async Task AnArrivalThatNeverComesHoldsTheChangeForFiveRoundsOfTheMasterLoop()
    {
        var (mission, flying, _) = await AMissionFlyingFromPToA();
        Assert.Equal(MissionDestinationOutcome.Pending,
            (await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["X"].ItemGuid)).Outcome);
        await Idle();
        await TheStepLeavesTheRoute(flying);

        for (var round = 1; round < 5; round++)
        {
            PendingMissionChanges.SweepAll();
            await Idle();
            Assert.NotNull(MissionGateway.PendingChangeOf(mission.MissionId));
        }

        PendingMissionChanges.SweepAll();
        await Idle();

        Assert.Null(MissionGateway.PendingChangeOf(mission.MissionId));
        Assert.Equal(_plant["X"].ItemGuid, mission.DestinationItemId);
    }

    // ---------------------------------------------------------------------------------------
    // The move, and the last change that wins
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The operator moves the mission onto Q while it moves from P to A: the move waits, and when the step
    /// ends the mission is moved onto Q, the route it had dropped.
    /// </summary>
    [Fact]
    public async Task AMoveAskedDuringAStepIsAppliedWhenTheStepEnds()
    {
        var (mission, flying, next) = await AMissionFlyingFromPToA();

        Assert.Equal(MissionMoveOutcome.Pending, MissionGateway.Move(mission.MissionId, _plant["Q"].ItemGuid));
        await Idle();
        Assert.Equal(PendingMissionChange.Position(_plant["Q"].ItemGuid), MissionGateway.PendingChangeOf(mission.MissionId));
        Assert.Equal(_plant["P"].ItemGuid, mission.PositionItemId);

        await TheStepArrives(mission, "A");
        await TheStepLeavesTheRoute(flying);

        Assert.Null(MissionGateway.PendingChangeOf(mission.MissionId));
        Assert.Equal(_plant["Q"].ItemGuid, mission.PositionItemId);
        Assert.DoesNotContain(next, MissionSteps.Instance.List);
    }

    /// <summary>
    /// Two changes asked during the same step: the last one wins. A change of destination to X, then a
    /// move onto Q: at the end of the step the mission is on Q, and still bound for B.
    /// </summary>
    [Fact]
    public async Task TheLastChangeAskedDuringTheStepWins()
    {
        var (mission, flying, _) = await AMissionFlyingFromPToA();

        Assert.Equal(MissionDestinationOutcome.Pending,
            (await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["X"].ItemGuid)).Outcome);
        await Idle();
        Assert.Equal(MissionMoveOutcome.Pending, MissionGateway.Move(mission.MissionId, _plant["Q"].ItemGuid));
        await Idle();
        Assert.Equal(PendingMissionChange.Position(_plant["Q"].ItemGuid), MissionGateway.PendingChangeOf(mission.MissionId));

        await TheStepArrives(mission, "A");
        await TheStepLeavesTheRoute(flying);

        Assert.Equal(_plant["Q"].ItemGuid, mission.PositionItemId);
        Assert.Equal(_plant["B"].ItemGuid, mission.DestinationItemId);
        Assert.False(mission.OperatorDestination);
    }

    /// <summary>
    /// A change asked directly, with nothing in flight any more, is the operator's last word: it replaces
    /// the pending one, which is not applied after it. The step ended in error on P, and before the master
    /// loop looks the operator sends the load unit to A: the mission goes to A, and stays bound for A.
    /// </summary>
    [Fact]
    public async Task AChangeAskedDirectlyReplacesThePendingOne()
    {
        var (mission, _, _) = await AMissionFlyingFromPToA();
        Assert.Equal(MissionDestinationOutcome.Pending,
            (await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["X"].ItemGuid)).Outcome);
        await Idle();
        _plant["P"].Status = DeviceStatusTypes.Warning;

        Assert.Equal(MissionDestinationOutcome.Changed,
            (await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["A"].ItemGuid)).Outcome);
        Assert.Null(MissionGateway.PendingChangeOf(mission.MissionId));

        PendingMissionChanges.SweepAll();
        await Idle();

        Assert.Equal(_plant["A"].ItemGuid, mission.DestinationItemId);
        Assert.Equal(["P", "A"], RouteOf(mission));
    }

    // ---------------------------------------------------------------------------------------
    // The operator's newer request that has nothing to do now (fix round 1 of task 4)
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The operator chose B, sends the load unit to X during the step, and then asks for B again: B is
    /// already the destination they chose, with its route, and the answer is «unchanged» (204). It is their
    /// last word: the pending X is gone, and the end of the step does not send the mission to X.
    /// </summary>
    [Fact]
    public async Task AskingAgainForTheDestinationTheOperatorChoseDropsThePendingChange()
    {
        var (mission, flying, _) = await AMissionFlyingFromPToA();
        Assert.True(mission.SetOperatorDestination(_plant["B"].ItemGuid));
        Assert.Equal(MissionDestinationOutcome.Pending,
            (await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["X"].ItemGuid)).Outcome);
        await Idle();

        Assert.Equal(MissionDestinationOutcome.Unchanged,
            (await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["B"].ItemGuid)).Outcome);
        await Idle();
        Assert.Null(MissionGateway.PendingChangeOf(mission.MissionId));

        await TheStepArrives(mission, "A");
        await TheStepLeavesTheRoute(flying);

        Assert.Null(MissionGateway.PendingChangeOf(mission.MissionId));
        Assert.Equal(_plant["B"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.OperatorDestination);
        Assert.Equal(["A", "B"], RouteOf(mission));
    }

    /// <summary>
    /// The operator sends the load unit to X during the step P->A, and then back to P, the device it is
    /// leaving. P is not «already there»: the change is applied from where the step ends, A, and it waits
    /// like any other, as the last one asked. At the end of the step the mission goes back to P, not to X.
    /// </summary>
    [Fact]
    public async Task AChangeTowardsTheDeviceTheStepIsLeavingWaitsAsTheLastChange()
    {
        var (mission, flying, _) = await AMissionFlyingFromPToA();
        _plant.Link("A", "P");
        Assert.Equal(MissionDestinationOutcome.Pending,
            (await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["X"].ItemGuid)).Outcome);
        await Idle();

        Assert.Equal(MissionDestinationOutcome.Pending,
            (await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["P"].ItemGuid)).Outcome);
        await Idle();
        Assert.Equal(PendingMissionChange.Destination(_plant["P"].ItemGuid, MissionGatewayChoice.Keep),
            MissionGateway.PendingChangeOf(mission.MissionId));

        await TheStepArrives(mission, "A");
        await TheStepLeavesTheRoute(flying);

        Assert.Null(MissionGateway.PendingChangeOf(mission.MissionId));
        Assert.Equal(_plant["P"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.OperatorDestination);
        Assert.Equal(["A", "P"], RouteOf(mission));
    }

    /// <summary>
    /// Nothing in flight (the step P->A ended in error), but a device is starting a step of the mission
    /// when the operator sends it to P, where it stands: the load unit is about to leave P, so P is not
    /// «already there». The change waits for the start; the start ends without a step, the load unit is
    /// still on P, and then the change is refused for good: the mission stays bound for B.
    /// </summary>
    [Fact]
    public async Task AChangeTowardsTheDeviceAStepIsStartingFromWaitsForTheStart()
    {
        var (mission, _, _) = await AMissionFlyingFromPToA();
        _plant["P"].Status = DeviceStatusTypes.Warning;
        Assert.Null(mission.StepInFlight());
        Assert.True(mission.TryBeginStepStart());

        try
        {
            Assert.Equal(MissionDestinationOutcome.Pending,
                (await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["P"].ItemGuid)).Outcome);
            await Idle();
            Assert.Equal(PendingMissionChange.Destination(_plant["P"].ItemGuid, MissionGatewayChoice.Keep),
                MissionGateway.PendingChangeOf(mission.MissionId));
        }
        finally
        {
            mission.EndStepStart();
        }

        await Idle();
        Assert.Null(MissionGateway.PendingChangeOf(mission.MissionId));
        Assert.Equal(_plant["B"].ItemGuid, mission.DestinationItemId);
        Assert.False(mission.OperatorDestination);
    }

    /// <summary>
    /// The operator moves the mission onto Q during the step P->A, and then onto P, the device the step is
    /// leaving: that move waits too, as the last one asked. At the end of the step the mission is moved
    /// onto P, not onto Q.
    /// </summary>
    [Fact]
    public async Task AMoveOntoTheDeviceTheStepIsLeavingWaitsAsTheLastChange()
    {
        var (mission, flying, _) = await AMissionFlyingFromPToA();
        Assert.Equal(MissionMoveOutcome.Pending, MissionGateway.Move(mission.MissionId, _plant["Q"].ItemGuid));
        await Idle();

        Assert.Equal(MissionMoveOutcome.Pending, MissionGateway.Move(mission.MissionId, _plant["P"].ItemGuid));
        await Idle();
        Assert.Equal(PendingMissionChange.Position(_plant["P"].ItemGuid), MissionGateway.PendingChangeOf(mission.MissionId));

        await TheStepArrives(mission, "A");
        await TheStepLeavesTheRoute(flying);

        Assert.Null(MissionGateway.PendingChangeOf(mission.MissionId));
        Assert.Equal(_plant["P"].ItemGuid, mission.PositionItemId);
    }

    /// <summary>
    /// The step ended in error on P (nothing in flight, the master loop has not looked yet) with a move onto
    /// Q pending, and the operator moves the mission onto P, where it is: «moved» (204), their last word.
    /// The pending move is gone, and the master loop does not move the mission onto Q.
    /// </summary>
    [Fact]
    public async Task AMoveOntoWhereTheMissionIsDropsThePendingChange()
    {
        var (mission, _, _) = await AMissionFlyingFromPToA();
        Assert.Equal(MissionMoveOutcome.Pending, MissionGateway.Move(mission.MissionId, _plant["Q"].ItemGuid));
        await Idle();
        _plant["P"].Status = DeviceStatusTypes.Warning;

        Assert.Equal(MissionMoveOutcome.Moved, MissionGateway.Move(mission.MissionId, _plant["P"].ItemGuid));
        Assert.Null(MissionGateway.PendingChangeOf(mission.MissionId));

        PendingMissionChanges.SweepAll();
        await Idle();

        Assert.Null(MissionGateway.PendingChangeOf(mission.MissionId));
        Assert.Equal(_plant["P"].ItemGuid, mission.PositionItemId);
    }

    // ---------------------------------------------------------------------------------------
    // A refusal that passes by itself (fix round 1 of task 4)
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// When the step ends a cell search of the route loop holds the mission's claim
    /// (<c>Navigator.TryClaimCellSearch</c>): the application is refused with «cell search in flight», a
    /// refusal that passes by itself. The change is kept, and the mission keeps its destination; once the
    /// search has ended, the master loop applies it.
    /// </summary>
    [Fact]
    public async Task AChangeThatMeetsACellSearchWhenTheStepEndsWaitsForIt()
    {
        var (mission, flying, _) = await AMissionFlyingFromPToA();
        Assert.Equal(MissionDestinationOutcome.Pending,
            (await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["X"].ItemGuid)).Outcome);
        await Idle();

        var search = new TaskCompletionSource();
        Assert.True(Navigator.TryClaimCellSearch(mission.MissionId, search.Task));
        try
        {
            await TheStepArrives(mission, "A");
            await TheStepLeavesTheRoute(flying);

            Assert.Equal(PendingMissionChange.Destination(_plant["X"].ItemGuid, MissionGatewayChoice.Keep),
                MissionGateway.PendingChangeOf(mission.MissionId));
            Assert.Equal(_plant["B"].ItemGuid, mission.DestinationItemId);
            Assert.False(mission.OperatorDestination);
        }
        finally
        {
            Navigator.ReleaseCellSearchClaim(mission.MissionId, search.Task);
            search.TrySetResult();
        }

        PendingMissionChanges.SweepAll();
        await Idle();

        Assert.Null(MissionGateway.PendingChangeOf(mission.MissionId));
        Assert.Equal(_plant["X"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.OperatorDestination);
        Assert.Equal(["A", "X"], RouteOf(mission));
    }

    /// <summary>
    /// A pending destination is already being applied when the operator moves the mission. The move is
    /// the newer word and removes the pending change; a transient result from the old application must
    /// not put it back afterwards.
    /// </summary>
    [Fact]
    public async Task AMoveAskedWhileThePendingDestinationIsFinishingDoesNotLetTheOldDestinationComeBack()
    {
        var (mission, flying, _) = await AMissionFlyingFromPToA();
        Assert.Equal(MissionDestinationOutcome.Pending,
            (await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["X"].ItemGuid)).Outcome);
        await Idle();

        var search = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(Navigator.TryClaimCellSearch(mission.MissionId, search.Task));
        var oldChangeReachedItsTransientOutcome = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var letTheOldChangeFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PendingMissionChanges.BeforeKeepingForTesting = async () =>
        {
            oldChangeReachedItsTransientOutcome.TrySetResult();
            await letTheOldChangeFinish.Task;
        };

        try
        {
            mission.PositionItemId = _plant["A"].ItemGuid;
            await mission.PositionWork.WaitAsync(Ceiling);
            _plant["P"].RemoveStep(flying);
            _plant["P"].Status = DeviceStatusTypes.Ready;
            await oldChangeReachedItsTransientOutcome.Task.WaitAsync(Ceiling);

            Assert.Equal(MissionMoveOutcome.Moved, MissionGateway.Move(mission.MissionId, _plant["Q"].ItemGuid));
            Assert.Null(MissionGateway.PendingChangeOf(mission.MissionId));

            // Keep the unrelated cell search claimed: if the stale application comes back, it cannot
            // immediately consume itself in another attempt before this test observes it.
            letTheOldChangeFinish.TrySetResult();
            await Idle();

            Assert.Null(MissionGateway.PendingChangeOf(mission.MissionId));
            Assert.Equal(_plant["Q"].ItemGuid, mission.PositionItemId);
            Assert.Equal(_plant["B"].ItemGuid, mission.DestinationItemId);
            Assert.False(mission.OperatorDestination);
        }
        finally
        {
            letTheOldChangeFinish.TrySetResult();
            Navigator.ReleaseCellSearchClaim(mission.MissionId, search.Task);
            search.TrySetResult();
        }
    }

    // ---------------------------------------------------------------------------------------
    // Scene
    // ---------------------------------------------------------------------------------------

    /// <summary>The route of the mission, step after step from where it is, by the devices' names.</summary>
    private string[] RouteOf(PathMission mission)
    {
        var steps = mission.MissionSteps.ToList();
        var ordered = new List<MissionStep>();
        var at = mission.PositionItemId;
        while (steps.FirstOrDefault(step => step.FromNodeId == at) is { } step)
        {
            ordered.Add(step);
            steps.Remove(step);
            at = step.ToNodeId ?? Guid.Empty;
        }

        Assert.Empty(steps);
        return _plant.Names(ordered);
    }

    private static Task Idle() => PendingMissionChanges.WhenIdleAsync().WaitAsync(Ceiling);

    /// <summary>
    /// P->A->B, A->X, Q and Z alone; on P a mission bound for B with its route P->A, A->B, and the step
    /// P->A in flight on P (its token, P moving). The engine is stopped.
    /// </summary>
    private async Task<(PathMission Mission, MissionStep Flying, MissionStep Next)> AMissionFlyingFromPToA()
    {
        foreach (var name in new[] { "P", "A", "B", "X", "Q", "Z" }) _plant.Device(name);
        _plant.Chain("P", "A", "B");
        _plant.Link("A", "X");

        var mission = new PathMission
        {
            PositionItemId = _plant["P"].ItemGuid,
            LuId = 42,
            MissionType = MissionTypes.MovingToItem,
            OperationType = OperationTypes.Outbound
        };
        ActiveMissions.Instance.Add(mission, buildRoute: false);
        await mission.PositionWork;
        mission.DestinationItemId = _plant["B"].ItemGuid;
        var flying = _plant.PlannedStep(mission, "P", "A");
        var next = _plant.PlannedStep(mission, "A", "B");
        Navigator.NavStatus = NavigatorStatusTypes.Stop;

        _plant["P"].RunningStepId = flying.StepId;
        _plant["P"].Status = DeviceStatusTypes.Moving;
        Assert.Same(flying, mission.StepInFlight());
        return (mission, flying, next);
    }

    /// <summary>The arrival of the step, as <c>LoadLu</c> writes it: the position, and its background work.</summary>
    private async Task TheStepArrives(PathMission mission, string on)
    {
        mission.PositionItemId = _plant[on].ItemGuid;
        await mission.PositionWork.WaitAsync(Ceiling);
        await Idle();
    }

    /// <summary>The step leaves the route through its source, as <c>FinalizeStepProcessing</c> does, and the source is free.</summary>
    private async Task TheStepLeavesTheRoute(MissionStep step)
    {
        _plant["P"].RemoveStep(step);
        _plant["P"].Status = DeviceStatusTypes.Ready;
        await Idle();
    }
}
