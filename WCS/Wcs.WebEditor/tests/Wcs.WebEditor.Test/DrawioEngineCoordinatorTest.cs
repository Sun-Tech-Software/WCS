using System.Net;
using Wcs.Api.Client;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Drawio.Coordination;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

public sealed class DrawioEngineCoordinatorTest : IDisposable
{
    private readonly DrawioWriteHandler _host = new();
    private readonly DrawioPlantWriteGate _gate = new();
    private string? _permitRefusal;
    private string? _unsaved;
    private NavigatorStatusTypes? _status = NavigatorStatusTypes.Stop;
    private bool _live = true;
    private readonly CancellationTokenSource _pageGone = new();

    /// <summary>What the page's own GET /status would read now: null leaves the shown status as it is.</summary>
    private NavigatorStatusTypes? _hostSays;
    private int _statusReads;
    private DrawioEngineCoordinator? _engine;

    /// <summary>Each time the start gave the edit permit back: the gate's owner at that moment.</summary>
    private readonly List<DrawioPlantWriter> _permitReleases = [];

    /// <summary>When set, the permit's release waits for it: the gate must still be taken meanwhile.</summary>
    private TaskCompletionSource? _permitReleaseGate;

    private Task ReleasePermit()
    {
        _permitReleases.Add(_gate.Current);
        return _permitReleaseGate?.Task ?? Task.CompletedTask;
    }

    private DrawioEngineCoordinator Coordinator(TimeSpan? giveUpAfter = null, Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeProvider? time = null)
    {
        var client = new WcsClient(new WcsClientOptions { BaseAddress = new Uri("http://localhost:5580"), ApiKey = "k" }, _host);
        // The page's GET shows what it reads, and the page then tells the coordinator: the same here.
        _engine = new DrawioEngineCoordinator(_gate, () => client,
            () => Task.FromResult<DrawioSaid?>(_permitRefusal), () => Task.FromResult<DrawioSaid?>(_unsaved), ReleasePermit, () => _status,
            () =>
            {
                Interlocked.Increment(ref _statusReads);
                if (_hostSays is { } said)
                {
                    _status = said;
                    _engine!.EngineStatusArrived(said);
                }

                return Task.CompletedTask;
            },
            () => _live, _pageGone.Token, TimeSpan.FromMilliseconds(20), giveUpAfter ?? TimeSpan.FromSeconds(30), delay, time);
        return _engine;
    }

    /// <summary>A clock that moves only when the test moves it: the bound is reached when the test says so.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    /// <summary>A command left pending by a test is followed until the bound: the page going away ends that.</summary>
    public void Dispose() => _pageGone.Cancel();

    private static async Task Until(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, what);
            await Task.Delay(10);
        }
    }

    // Command.StatusTypes of the engine (Wcs.Engine/src/Items/Command.cs).
    private static CommandDto Command(long id, CommandTypes type, short status, string? error) =>
        new(id, DateTime.UtcNow, (int)type, null, null, null, status,
            error, null, null, 0, null, null, 0m, null, 0m, null, null, null, true, 0);

    [Fact]
    public async Task AStartWithNothingInTheWaySendsRun()
    {
        var engine = Coordinator();

        await engine.StartAsync();

        Assert.Equal((int)CommandTypes.SetNavigatorRun, Assert.Single(_host.Commands).CommandType);
        Assert.Equal("Starting the engine...", engine.State.Pending);
        Assert.Null(engine.State.Error);
    }

    [Fact]
    public async Task AnotherWriteInProgressHoldsTheStartBack()
    {
        Assert.True(_gate.TryEnter(DrawioPlantWriter.Drawing, out var lease));
        using var _ = lease;
        var engine = Coordinator();

        await engine.StartAsync();

        Assert.Empty(_host.Commands);
        Assert.Contains("NOT started", engine.State.Error);
    }

    [Fact]
    public async Task SomebodyElsesPermitHoldsTheStartBack()
    {
        _permitRefusal = "Read only: somebody else has been editing the plant since 14:32:10. Nothing was written.";
        var engine = Coordinator();

        await engine.StartAsync();

        Assert.Empty(_host.Commands);
        Assert.Contains("14:32:10", engine.State.Error);
    }

    [Fact]
    public async Task UnsavedWorkHoldsTheStartBack()
    {
        _unsaved = "The drawing holds work that is not saved yet (1 deleted device(s)): ... the engine was NOT started.";
        var engine = Coordinator();

        await engine.StartAsync();

        Assert.Empty(_host.Commands);
        Assert.Equal(_unsaved, engine.State.Error);
    }

    /// <summary>
    /// The engine reaches Run well after the POST (it opens the PLC connections first): until then the
    /// gate stays taken, and a save of this page that would straddle the switch to Run is refused.
    /// </summary>
    [Fact]
    public async Task WhileTheStartIsPendingTheGateStaysTaken()
    {
        var engine = Coordinator();

        await engine.StartAsync();

        Assert.Equal("Starting the engine...", engine.State.Pending);
        Assert.Equal(DrawioPlantWriter.Engine, _gate.Current);
        Assert.False(_gate.TryEnter(DrawioPlantWriter.Drawing, out _));
        Assert.Empty(_permitReleases);
    }

    [Fact]
    public async Task RunConfirmedFreesTheGateAndGivesThePermitBack()
    {
        var engine = Coordinator();
        await engine.StartAsync();

        engine.EngineStatusArrived(NavigatorStatusTypes.Run);
        await engine.Released.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(DrawioPlantWriter.Nobody, _gate.Current);
        Assert.Equal([DrawioPlantWriter.Engine], _permitReleases);
    }

    [Theory]
    [InlineData((short)99)]
    [InlineData((short)3)]
    public async Task AStartTheEngineDidNotCarryOutFreesTheGateAndGivesThePermitBack(short status)
    {
        var engine = Coordinator();
        await engine.StartAsync();

        engine.CommandArrived(Command(1, CommandTypes.SetNavigatorRun, status, "not carried out"));
        await engine.Released.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(DrawioPlantWriter.Nobody, _gate.Current);
        Assert.Equal([DrawioPlantWriter.Engine], _permitReleases);
    }

    [Fact]
    public async Task APostThatFailedFreesTheGateAndGivesThePermitBack()
    {
        _host.CommandRefusal = HttpStatusCode.InternalServerError;
        var engine = Coordinator();

        await engine.StartAsync();
        await engine.Released.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(DrawioPlantWriter.Nobody, _gate.Current);
        Assert.Equal([DrawioPlantWriter.Engine], _permitReleases);
    }

    [Fact]
    public async Task AStartRefusedForUnsavedWorkGivesThePermitItTookBack()
    {
        _unsaved = "The drawing holds work that is not saved yet: the engine was NOT started.";
        var engine = Coordinator();

        await engine.StartAsync();
        await engine.Released.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(DrawioPlantWriter.Nobody, _gate.Current);
        Assert.Equal([DrawioPlantWriter.Engine], _permitReleases);
    }

    /// <summary>
    /// The permit goes back while the gate is still taken, and the gate is freed only once it has:
    /// a write of this page that started in between would have its own permit released under it.
    /// </summary>
    [Fact]
    public async Task ThePermitGoesBackBeforeTheGateIsFree()
    {
        _permitReleaseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = Coordinator();
        await engine.StartAsync();

        engine.EngineStatusArrived(NavigatorStatusTypes.Run);
        Assert.Equal([DrawioPlantWriter.Engine], _permitReleases);
        Assert.Equal(DrawioPlantWriter.Engine, _gate.Current);

        _permitReleaseGate.SetResult();
        await engine.Released.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DrawioPlantWriter.Nobody, _gate.Current);
    }

    /// <summary>The page goes while the start is pending: the gate is free at once; the permit is the page's own to release.</summary>
    [Fact]
    public async Task APageThatGoesFreesTheGate()
    {
        var engine = Coordinator();
        await engine.StartAsync();

        _pageGone.Cancel();

        Assert.Equal(DrawioPlantWriter.Nobody, _gate.Current);
        Assert.Empty(_permitReleases);
    }

    /// <summary>A Stop sent while the start is pending (the engine neither stopped nor running) ends the start's hold too.</summary>
    [Fact]
    public async Task AStopSentWhileTheStartIsPendingFreesTheGate()
    {
        var engine = Coordinator();
        await engine.StartAsync();
        _status = NavigatorStatusTypes.Recover;

        await engine.StopAsync();
        await engine.Released.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("Stopping the engine...", engine.State.Pending);
        Assert.Equal(DrawioPlantWriter.Nobody, _gate.Current);
        Assert.Single(_permitReleases);
    }

    [Fact]
    public async Task ARefusedCommandIsSaidAndNothingIsPending()
    {
        _host.CommandRefusal = HttpStatusCode.InternalServerError;
        var engine = Coordinator();

        await engine.StartAsync();

        Assert.Null(engine.State.Pending);
        // Row 37 of the queue, extended to the whole page on 10/10: the why in the page's words, the
        // host's answer behind the line's "?".
        Assert.Equal("The engine was NOT started because the host answered with an error.", engine.State.Error);
        Assert.Equal("the host answered 500 with an empty body", engine.State.ErrorDetail);
    }

    /// <summary>A Stop whose POST gets no answer in time: the same one sentence, the failure behind its "?".</summary>
    [Fact]
    public async Task AStopWithNoAnswerIsOneSentenceWithTheFailureBehindIt()
    {
        _status = NavigatorStatusTypes.Run;
        _host.TimeoutRoute = "command-post";
        var engine = Coordinator();

        await engine.StopAsync();

        Assert.Null(engine.State.Pending);
        Assert.Equal("The engine was NOT stopped because the host did not answer in time.", engine.State.Error);
        Assert.Equal("TaskCanceledException: La richiesta su 'command-post' e' scaduta senza risposta (prova).", engine.State.ErrorDetail);
    }

    [Fact]
    public async Task ThePendingStartEndsWhenTheEngineSaysRun()
    {
        var engine = Coordinator();
        await engine.StartAsync();

        engine.EngineStatusArrived(NavigatorStatusTypes.Run);

        Assert.Null(engine.State.Pending);
    }

    [Fact]
    public async Task ACommandTheEngineFailedEndsThePendingStartWithItsReason()
    {
        var engine = Coordinator();
        await engine.StartAsync();

        engine.CommandArrived(Command(1, CommandTypes.SetNavigatorRun, 99, "the diagram is not loaded"));

        Assert.Null(engine.State.Pending);
        Assert.Contains("the diagram is not loaded", engine.State.Error);
    }

    /// <summary>
    /// The two channels - the POST's own answer, and CommandChanged on the live feed - have no order:
    /// the engine can fail the command before the POST has told this page its id. That failure is
    /// this command's, and it must not be lost: nothing else would ever end "Starting the engine...".
    /// </summary>
    [Fact]
    public async Task AFailureThatArrivesBeforeThePostAnsweredStillEndsThePendingStart()
    {
        _host.CommandPostGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = Coordinator();

        var start = engine.StartAsync();
        await _host.CommandPostGateReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // The fake host answers the id of the command's position: this is the first.
        engine.CommandArrived(Command(1, CommandTypes.SetNavigatorRun, 99, "the diagram is not loaded"));
        _host.CommandPostGate.SetResult();
        await start;

        Assert.Null(engine.State.Pending);
        Assert.Contains("the diagram is not loaded", engine.State.Error);
    }

    /// <summary>
    /// The engine ends a start it does not carry out with a Warning (HandleSetNavigatorRun, when the
    /// engine is in neither Stop nor Pause): no status change follows, and without this the switch
    /// would stay on "Starting the engine..." for ever.
    /// </summary>
    [Fact]
    public async Task ACommandTheEngineDidNotCarryOutEndsThePendingStartWithItsReason()
    {
        var engine = Coordinator();
        await engine.StartAsync();

        engine.CommandArrived(Command(1, CommandTypes.SetNavigatorRun, 3, "the navigator is not stopped"));

        Assert.Null(engine.State.Pending);
        Assert.Contains("the navigator is not stopped", engine.State.Error);
    }

    [Fact]
    public async Task AnotherCommandsOutcomeDoesNotEndThePendingStart()
    {
        var engine = Coordinator();
        await engine.StartAsync();

        engine.CommandArrived(Command(7, CommandTypes.SetNavigatorRun, 99, "somebody else's"));

        Assert.Equal("Starting the engine...", engine.State.Pending);
        Assert.Null(engine.State.Error);
    }

    [Fact]
    public async Task ThePendingStopEndsWhenTheEngineSaysStop()
    {
        _status = NavigatorStatusTypes.Run;
        var engine = Coordinator();
        await engine.StopAsync();
        Assert.Equal("Stopping the engine...", engine.State.Pending);

        engine.EngineStatusArrived(NavigatorStatusTypes.Stop);

        Assert.Null(engine.State.Pending);
        Assert.Null(engine.State.Error);
    }

    /// <summary>
    /// The host's Run can come before the POST's own answer: nothing is left pending, and the late
    /// answer does not make the command pending again - an outcome for it no longer says anything,
    /// and nothing follows it until the bound to say later that the host "has not confirmed yet".
    /// </summary>
    [Fact]
    public async Task AStatusReachedWhileThePostIsOnItsWayLeavesNothingPending()
    {
        _host.CommandPostGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _live = false;
        var engine = Coordinator(giveUpAfter: TimeSpan.FromMilliseconds(100));

        var start = engine.StartAsync();
        await _host.CommandPostGateReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        engine.EngineStatusArrived(NavigatorStatusTypes.Run);
        _host.CommandPostGate.SetResult();
        await start;
        engine.CommandArrived(Command(1, CommandTypes.SetNavigatorRun, 99, "late"));
        await Task.Delay(300);

        Assert.Null(engine.State.Pending);
        Assert.Null(engine.State.Error);
    }

    /// <summary>
    /// With no live channel nobody tells the page the engine's new status: the page reads it again
    /// by itself until it comes.
    /// </summary>
    [Fact]
    public async Task WithoutALiveFeedTheStatusIsReadAgainUntilItArrives()
    {
        _live = false;
        var engine = Coordinator();
        await engine.StartAsync();
        Assert.Equal("Starting the engine...", engine.State.Pending);

        _hostSays = NavigatorStatusTypes.Run;

        await Until(() => engine.State.Pending is null, "The pending start never ended without a live feed.");
        Assert.Null(engine.State.Error);
    }

    [Fact]
    public async Task WithoutALiveFeedACommandTheEngineFailedIsReadByItsId()
    {
        _live = false;
        _host.CommandReads[1] = Command(1, CommandTypes.SetNavigatorRun, 99, "the diagram is not loaded");
        var engine = Coordinator();

        await engine.StartAsync();

        await Until(() => engine.State.Pending is null, "The failed command was never read.");
        Assert.Contains("the diagram is not loaded", engine.State.Error);
    }

    [Fact]
    public async Task ALiveFeedIsNotPolled()
    {
        var engine = Coordinator();
        await engine.StartAsync();

        await Task.Delay(200);

        Assert.Equal(0, _statusReads);
        Assert.Equal("Starting the engine...", engine.State.Pending);
    }

    /// <summary>
    /// The switch is never left disabled for good: past the bound, the page says the host has not
    /// confirmed yet - a start can merely be slow, so no "reload the page". Deterministic: the clock
    /// moves only when the test moves it, and the loop is held in its waits. One round inside the
    /// bound reads once; the bound reached, the next round gives up without reading again.
    /// </summary>
    [Fact]
    public async Task AnUnconfirmedCommandIsGivenUpOnAfterTheBound()
    {
        _live = false;
        var clock = new ManualClock();
        var engine = Coordinator(delay: HeldDelay, time: clock);
        await engine.StartAsync();

        (await NextDelayAsync()).SetResult();
        var second = await NextDelayAsync();
        Assert.Equal(1, _statusReads);
        Assert.Equal("Starting the engine...", engine.State.Pending);

        clock.Advance(TimeSpan.FromSeconds(30));
        second.SetResult();
        await engine.Following.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(engine.State.Pending);
        Assert.Equal(
            "The host has not confirmed yet that the engine started (no answer within 30 seconds): it may still be on its way. The engine status shown here is the last one known, and it updates by itself when the host confirms.",
            engine.State.Error);
        Assert.Equal(1, _statusReads);
    }

    /// <summary>The bound gives up on the start: the gate is free and the permit given back, whatever comes later.</summary>
    [Fact]
    public async Task TheGiveUpFreesTheGateAndGivesThePermitBack()
    {
        var clock = new ManualClock();
        var engine = Coordinator(delay: HeldDelay, time: clock);
        await engine.StartAsync();
        clock.Advance(TimeSpan.FromSeconds(30));

        (await NextDelayAsync()).SetResult();
        await engine.Following.WaitAsync(TimeSpan.FromSeconds(5));
        await engine.Released.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(engine.State.Pending);
        Assert.Equal(DrawioPlantWriter.Nobody, _gate.Current);
        Assert.Equal([DrawioPlantWriter.Engine], _permitReleases);
    }

    /// <summary>
    /// The feed is Live and the engine ends the command without changing status (Done, as a
    /// StartNavigator that returns silently would): nothing would end the wait but the bound.
    /// </summary>
    [Fact]
    public async Task TheBoundAlsoEndsTheWaitWithALiveFeed()
    {
        var engine = Coordinator(giveUpAfter: TimeSpan.FromMilliseconds(200));
        await engine.StartAsync();
        engine.CommandArrived(Command(1, CommandTypes.SetNavigatorRun, 2, null));

        await Until(() => engine.State.Pending is null, "The pending start was never given up on with a Live feed.");
        Assert.Contains("has not confirmed yet", engine.State.Error);
        Assert.Equal(0, _statusReads);
    }

    /// <summary>The status comes after the bound: "the host has not confirmed yet" is no longer true, and goes.</summary>
    [Fact]
    public async Task AStatusThatComesAfterTheBoundClearsTheWarning()
    {
        var engine = Coordinator(giveUpAfter: TimeSpan.FromMilliseconds(100));
        await engine.StartAsync();
        await Until(() => engine.State.Error is not null, "The bound never fired.");

        engine.EngineStatusArrived(NavigatorStatusTypes.Run);

        Assert.Null(engine.State.Error);
        Assert.Null(engine.State.Pending);
    }

    /// <summary>The engine's reason comes after the bound: it replaces "the host has not confirmed yet".</summary>
    [Fact]
    public async Task AFailureThatComesAfterTheBoundIsSaid()
    {
        var engine = Coordinator(giveUpAfter: TimeSpan.FromMilliseconds(100));
        await engine.StartAsync();
        await Until(() => engine.State.Error is not null, "The bound never fired.");

        engine.CommandArrived(Command(1, CommandTypes.SetNavigatorRun, 99, "the diagram is not loaded"));

        Assert.Contains("the diagram is not loaded", engine.State.Error);
    }

    /// <summary>A new press forgets what the bound gave up on: a late Run does not clear the new press's own refusal.</summary>
    [Fact]
    public async Task ANewPressForgetsWhatTheBoundGaveUpOn()
    {
        var engine = Coordinator(giveUpAfter: TimeSpan.FromMilliseconds(100));
        await engine.StartAsync();
        await Until(() => engine.State.Error is not null, "The bound never fired.");
        _permitRefusal = "Read only: somebody else has been editing the plant since 14:32:10. Nothing was written.";
        await engine.StartAsync();

        engine.EngineStatusArrived(NavigatorStatusTypes.Run);

        Assert.Contains("14:32:10", engine.State.Error);
    }

    /// <summary>
    /// The page goes while the loop waits between two rounds: nothing more is read. Deterministic:
    /// the loop is held in its wait (the test's delay), the page goes, and only THEN is the wait
    /// released - as a wait that ended just as the page went would be. The loop's end is awaited,
    /// not guessed with a sleep.
    /// </summary>
    [Fact]
    public async Task APageThatGoesStopsTheReads()
    {
        _live = false;
        var engine = Coordinator(delay: HeldDelay);
        await engine.StartAsync();
        (await NextDelayAsync()).SetResult();
        var second = await NextDelayAsync();
        // Held in the second wait: the first round has read the status and the command, once each.
        Assert.Equal(1, _statusReads);
        Assert.Equal(1, CommandReads(1));

        _pageGone.Cancel();
        second.TrySetResult();
        await engine.Following.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, _statusReads);
        Assert.Equal(1, CommandReads(1));
    }

    /// <summary>
    /// Once a newer command is pending, the loop that followed the older one reads nothing more and
    /// ends. Deterministic: the older loop is held in its first wait while the newer command is sent,
    /// then released, and its end is awaited.
    /// </summary>
    [Fact]
    public async Task ASecondCommandLeavesTheOlderLoopWithNothingToDo()
    {
        _live = false;
        var engine = Coordinator(delay: HeldDelay);
        await engine.StartAsync();
        var olderWait = await NextDelayAsync();
        var olderLoop = engine.Following;

        engine.EngineStatusArrived(NavigatorStatusTypes.Run);
        _status = NavigatorStatusTypes.Run;
        await engine.StopAsync();
        await NextDelayAsync();
        olderWait.SetResult();
        await olderLoop.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, _statusReads);
        Assert.Equal(0, CommandReads(1));
        Assert.Equal("Stopping the engine...", engine.State.Pending);
    }

    /// <summary>
    /// A Stop press that finds the engine already stopped sends nothing - and it is still a new press:
    /// the "has not confirmed yet" of the start before it is not left on the switch.
    /// </summary>
    [Fact]
    public async Task AStopThatFindsTheEngineStoppedClearsWhatTheBoundGaveUpOn()
    {
        var engine = Coordinator(giveUpAfter: TimeSpan.FromMilliseconds(100));
        await engine.StartAsync();
        await Until(() => engine.State.Error is not null, "The bound never fired.");

        await engine.StopAsync();

        Assert.Null(engine.State.Error);
        Assert.Single(_host.Commands);
    }

    private readonly List<TaskCompletionSource> _delays = [];
    private readonly SemaphoreSlim _delayCalls = new(0);

    /// <summary>The loop's wait, held until the test releases it; the page going away ends it, as it ends Task.Delay.</summary>
    private Task HeldDelay(TimeSpan _, CancellationToken token)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_delays) _delays.Add(gate);
        _delayCalls.Release();
        return gate.Task.WaitAsync(token);
    }

    /// <summary>Waits until the loop enters its next wait, and returns what releases it.</summary>
    private async Task<TaskCompletionSource> NextDelayAsync()
    {
        Assert.True(await _delayCalls.WaitAsync(TimeSpan.FromSeconds(5)), "The loop never got to its next wait.");
        lock (_delays) return _delays[^1];
    }

    private int CommandReads(long id) =>
        _host.Requests.Count(request => request.StartsWith("GET ") && request.EndsWith($"/commands/{id}"));

    /// <summary>The operator left the page while the start was being checked: no command goes out after that.</summary>
    [Fact]
    public async Task APageLeftDuringTheChecksSendsNothing()
    {
        var engine = new DrawioEngineCoordinator(_gate,
            () => new WcsClient(new WcsClientOptions { BaseAddress = new Uri("http://localhost:5580"), ApiKey = "k" }, _host),
            () => Task.FromResult<DrawioSaid?>(null),
            () =>
            {
                _pageGone.Cancel();
                return Task.FromResult<DrawioSaid?>(null);
            },
            ReleasePermit,
            () => _status, () => Task.CompletedTask, () => true, _pageGone.Token,
            TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(30));

        await engine.StartAsync();

        Assert.Empty(_host.Commands);
        Assert.Equal(DrawioPlantWriter.Nobody, _gate.Current);
    }

    [Fact]
    public async Task AStopSendsStopWithoutAPermit()
    {
        _status = NavigatorStatusTypes.Run;
        _permitRefusal = "somebody else";
        var engine = Coordinator();

        await engine.StopAsync();

        Assert.Equal((int)CommandTypes.SetNavigatorStop, Assert.Single(_host.Commands).CommandType);
    }

    [Theory]
    [InlineData(NavigatorStatusTypes.Stop)]
    [InlineData(NavigatorStatusTypes.Pause)]
    public async Task AStoppedEngineIsNotStoppedAgain(NavigatorStatusTypes status)
    {
        _status = status;
        var engine = Coordinator();

        await engine.StopAsync();

        Assert.Empty(_host.Commands);
    }
}
