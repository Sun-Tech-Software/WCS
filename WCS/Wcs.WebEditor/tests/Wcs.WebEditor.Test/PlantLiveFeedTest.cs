using Wcs.Api.Client;
using Wcs.Api.Contracts;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Live;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

public sealed class PlantLiveFeedTest
{
    private static readonly Guid A = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");

    private static ItemStatus Status(Guid id, DeviceStatusTypes status) =>
        new(id, "", "", "", "A", status, LuLoadStatusTypes.None, "", 0m, Guid.Empty, Guid.Empty);

    private static (PlantLiveFeed Feed, FakePlantEventSource Source, ManualTimeProvider Clock, DrawioWriteHandler Host) Feed()
    {
        var host = new DrawioWriteHandler { NavStatus = NavigatorStatusTypes.Stop };
        var client = new WcsClient(new WcsClientOptions { BaseAddress = new Uri("http://localhost:5580"), ApiKey = "k" }, host);
        var source = new FakePlantEventSource();
        var clock = new ManualTimeProvider();
        return (new PlantLiveFeed(client, source, _ => DeviceTypes.Automation, clock), source, clock, host);
    }

    [Fact]
    public async Task TheStartReadsTheSnapshotAndGoesLive()
    {
        var (feed, _, _, host) = Feed();
        host.ItemStatuses = [Status(A, DeviceStatusTypes.Error)];

        await feed.StartAsync();

        Assert.Equal(LiveConnection.Live, feed.Connection);
        Assert.Equal(NavigatorStatusTypes.Stop, feed.NavStatus);
        Assert.Equal(A, Assert.Single(feed.TakeFrame().Devices).ItemId);
    }

    [Fact]
    public async Task ManyEventsMakeOneFramePerInterval()
    {
        var (feed, source, clock, _) = Feed();
        await feed.StartAsync();
        clock.FireAll();
        var frames = 0;
        feed.FrameReady += () => frames++;

        for (var i = 0; i < 50; i++)
            source.RaiseItemStatus(Status(A, i % 2 == 0 ? DeviceStatusTypes.Moving : DeviceStatusTypes.Busy));

        Assert.Equal(0, frames);
        Assert.Equal(1, clock.PendingTimers);
        clock.FireAll();
        Assert.Equal(1, frames);
        Assert.Equal("#ffffff", Assert.Single(feed.TakeFrame().Devices).StatusColor);
    }

    [Fact]
    public async Task NoFrameWhenNothingChanged()
    {
        var (feed, _, clock, _) = Feed();
        await feed.StartAsync();
        clock.FireAll();
        var frames = 0;
        feed.FrameReady += () => frames++;

        clock.FireAll();

        Assert.Equal(0, frames);
    }

    [Fact]
    public async Task TheEngineStatusFollowsTheEvents()
    {
        var (feed, source, _, host) = Feed();
        await feed.StartAsync();
        var told = 0;
        feed.StatusChanged += () => told++;
        // The host that sends the event answers the same status to the snapshot the event triggers.
        host.NavStatus = NavigatorStatusTypes.Run;

        source.RaiseNavigator(new EngineStatus(NavigatorStatusTypes.Run, DefragmentationTypes.None, "t", DateTime.UtcNow));
        await feed.Settled;

        Assert.Equal(NavigatorStatusTypes.Run, feed.NavStatus);
        Assert.Equal(1, told);
    }

    [Fact]
    public async Task AReconnectingChannelIsInterruptedAndItsReconnectionReadsTheSnapshotAgain()
    {
        var (feed, source, clock, host) = Feed();
        await feed.StartAsync();
        clock.FireAll();
        source.RaiseItemStatus(Status(A, DeviceStatusTypes.Error));

        source.RaiseReconnecting();
        Assert.Equal(LiveConnection.Interrupted, feed.Connection);
        // SignalR is retrying by itself: the feed arms nothing of its own.
        Assert.Equal(1, clock.PendingTimers); // the flush of the event above, and nothing else
        clock.FireAll();
        Assert.Equal(1, source.StartCalls);

        // What went by while the channel was down: the device is no longer in error, the engine runs.
        host.ItemStatuses = [];
        host.NavStatus = NavigatorStatusTypes.Run;
        source.RaiseReconnected();
        await feed.Settled;

        Assert.Equal(LiveConnection.Live, feed.Connection);
        Assert.Equal(NavigatorStatusTypes.Run, feed.NavStatus);
        Assert.Empty(feed.TakeFrame().Devices);
        Assert.Equal(1, source.StartCalls);
    }

    [Fact]
    public async Task AFailedSnapshotIsRetriedByTheClockWithoutRestartingTheChannel()
    {
        var (feed, source, clock, host) = Feed();
        host.BrokenReads.Add("status");

        await feed.StartAsync();
        Assert.Equal(LiveConnection.Interrupted, feed.Connection);

        // Still broken: still interrupted, and the channel - which is up - is not started again.
        clock.FireAll();
        await feed.Settled;
        Assert.Equal(LiveConnection.Interrupted, feed.Connection);
        Assert.Equal(1, source.StartCalls);

        host.BrokenReads.Remove("status");
        host.ItemStatuses = [Status(A, DeviceStatusTypes.Error)];
        clock.FireAll();
        await feed.Settled;

        Assert.Equal(LiveConnection.Live, feed.Connection);
        Assert.Equal(1, source.StartCalls);
        Assert.Equal(A, Assert.Single(feed.TakeFrame().Devices).ItemId);
    }

    [Fact]
    public async Task ATerminalCloseIsRetriedByTheClockWithANewStart()
    {
        var (feed, source, clock, host) = Feed();
        await feed.StartAsync();
        clock.FireAll();
        host.ItemStatuses = [Status(A, DeviceStatusTypes.Error)];

        source.RaiseClosed();
        Assert.Equal(LiveConnection.Interrupted, feed.Connection);
        Assert.Equal(1, clock.PendingTimers);

        clock.FireAll();
        await feed.Settled;

        Assert.Equal(2, source.StartCalls);
        Assert.Equal(LiveConnection.Live, feed.Connection);
        Assert.Equal(A, Assert.Single(feed.TakeFrame().Devices).ItemId);
    }

    [Fact]
    public async Task NavigatorStatusChangedReadsTheSnapshotAgain()
    {
        var (feed, source, clock, host) = Feed();
        await feed.StartAsync();
        clock.FireAll();
        var frames = 0;
        feed.FrameReady += () => frames++;
        // The host's queue dropped the event that put A in error: it says so with NavigatorStatusChanged.
        host.ItemStatuses = [Status(A, DeviceStatusTypes.Error)];

        source.RaiseNavigator(new EngineStatus(NavigatorStatusTypes.Stop, DefragmentationTypes.None, "t", DateTime.UtcNow));
        await feed.Settled;
        clock.FireAll();

        Assert.Equal(1, frames);
        Assert.Equal(A, Assert.Single(feed.TakeFrame().Devices).ItemId);
    }

    /// <summary>
    /// The engine event says Run while a snapshot already asked for the engine's status is sitting
    /// at the gate: that answer was ISSUED before the event and carries the old Stop, and it must
    /// not overwrite NavStatus with it, nor announce that stale Stop on StatusChanged - a sequence
    /// number (or equivalent) must say "a more recent engine event already arrived" and make the
    /// snapshot skip its own, older, engine field. The devices/connection side of the snapshot is
    /// unaffected: only the engine status is protected against landing after its own event (compito
    /// 26/9, punto 1).
    /// </summary>
    [Fact]
    public async Task ANavigatorEventDuringASnapshotDoesNotOverwriteItWithTheStaleEngineAnswer()
    {
        var (feed, source, _, host) = Feed(); // host.NavStatus = Stop
        host.EngineStatusGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var start = feed.StartAsync();
        await host.EngineStatusGateReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var announcedTheStaleStop = false;
        feed.StatusChanged += () =>
        {
            if (feed.NavStatus == NavigatorStatusTypes.Stop) announcedTheStaleStop = true;
        };
        // The engine really changed by now (the event is genuine), but the snapshot's own answer -
        // already issued and frozen at the gate - still carries the OLD Stop it was issued with.
        host.NavStatus = NavigatorStatusTypes.Run;
        source.RaiseNavigator(new EngineStatus(NavigatorStatusTypes.Run, DefragmentationTypes.None, "t", DateTime.UtcNow));
        host.EngineStatusGate.SetResult();

        await start.WaitAsync(TimeSpan.FromSeconds(10));
        await feed.Settled.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(NavigatorStatusTypes.Run, feed.NavStatus);
        Assert.False(announcedTheStaleStop, "StatusChanged announced the stale Stop from the snapshot in flight.");
    }

    [Fact]
    public async Task RequestsDuringASnapshotInFlightMakeExactlyOneMore()
    {
        var (feed, source, _, host) = Feed();
        host.ItemStatusGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.NavStatus = NavigatorStatusTypes.Run;

        var start = feed.StartAsync();
        await host.ItemStatusGateReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var run = new EngineStatus(NavigatorStatusTypes.Run, DefragmentationTypes.None, "t", DateTime.UtcNow);
        source.RaiseNavigator(run);
        source.RaiseNavigator(run);
        Assert.Equal(1, host.ItemStatusRequests);

        host.ItemStatusGate.SetResult();
        await start.WaitAsync(TimeSpan.FromSeconds(10));
        await feed.Settled.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, host.ItemStatusRequests);
        Assert.Equal(LiveConnection.Live, feed.Connection);
    }

    [Fact]
    public async Task AnEventDuringTheSnapshotIsReplayedOnTopOfIt()
    {
        var (feed, source, clock, host) = Feed();
        // The host's snapshot still has A's old status: the answer in flight predates the event.
        host.ItemStatuses = [Status(A, DeviceStatusTypes.Error)];
        host.ItemStatusGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var start = feed.StartAsync();
        await host.ItemStatusGateReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        source.RaiseItemStatus(Status(A, DeviceStatusTypes.Busy));
        host.ItemStatusGate.SetResult();
        await start.WaitAsync(TimeSpan.FromSeconds(10));
        await feed.Settled.WaitAsync(TimeSpan.FromSeconds(10));
        var frames = 0;
        feed.FrameReady += () => frames++;
        clock.FireAll();

        Assert.Equal(LiveConnection.Live, feed.Connection);
        Assert.Equal(1, frames);
        Assert.Equal(DeviceStatusTypes.Busy, feed.StatusOf(A)!.Status);
        Assert.Equal("#ffffff", Assert.Single(feed.TakeFrame().Devices).StatusColor);
    }

    [Fact]
    public async Task AnEventDuringAFailedSnapshotIsNotLost()
    {
        var (feed, source, clock, host) = Feed();
        await feed.StartAsync();
        clock.FireAll();
        host.ItemStatusGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.BrokenReads.Add("status");

        source.RaiseReconnecting();
        source.RaiseReconnected(); // the snapshot of the reconnection, held at the gate and then failing
        await host.ItemStatusGateReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        source.RaiseItemStatus(Status(A, DeviceStatusTypes.Busy));
        host.ItemStatusGate.SetResult();
        await feed.Settled.WaitAsync(TimeSpan.FromSeconds(10));
        var frames = 0;
        feed.FrameReady += () => frames++;
        clock.FireAll(); // the frame, and the snapshot's retry - which fails again
        await feed.Settled.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(LiveConnection.Interrupted, feed.Connection);
        Assert.Equal(1, frames);
        Assert.Equal(DeviceStatusTypes.Busy, feed.StatusOf(A)!.Status);
        Assert.Equal(1, source.StartCalls);
    }

    [Fact]
    public async Task ASnapshotThatLandsWhileReconnectingDoesNotDeclareLive()
    {
        var (feed, source, clock, host) = Feed();
        await feed.StartAsync();
        clock.FireAll();
        host.ItemStatusGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        source.RaiseNavigator(new EngineStatus(NavigatorStatusTypes.Stop, DefragmentationTypes.None, "t", DateTime.UtcNow));
        await host.ItemStatusGateReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        source.RaiseReconnecting();
        host.ItemStatuses = [Status(A, DeviceStatusTypes.Error)];
        host.ItemStatusGate.SetResult();
        await feed.Settled.WaitAsync(TimeSpan.FromSeconds(10));

        // The snapshot is kept, but the channel is still down: not live.
        Assert.Equal(LiveConnection.Interrupted, feed.Connection);
        Assert.Equal(DeviceStatusTypes.Error, feed.StatusOf(A)!.Status);

        source.RaiseReconnected();
        await feed.Settled.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(LiveConnection.Live, feed.Connection);
    }

    [Fact]
    public async Task ASubscriberThatThrowsDoesNotFreezeTheFeed()
    {
        var (feed, source, clock, host) = Feed();
        host.ItemStatusGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var throws = true;
        feed.StatusChanged += () =>
        {
            if (!throws || feed.Connection != LiveConnection.Live) return;
            throws = false;
            throw new InvalidOperationException("a page handler that fails (test)");
        };

        var start = feed.StartAsync();
        await host.ItemStatusGateReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // One more snapshot is asked for while the first is in flight: the throw comes with that
        // request still pending, when the flag is the only thing that says "a snapshot is running".
        source.RaiseNavigator(new EngineStatus(NavigatorStatusTypes.Stop, DefragmentationTypes.None, "t", DateTime.UtcNow));
        host.ItemStatusGate.SetResult();
        var thrown = await Record.ExceptionAsync(() => start.WaitAsync(TimeSpan.FromSeconds(10)));
        await Record.ExceptionAsync(() => feed.Settled.WaitAsync(TimeSpan.FromSeconds(10)));

        // Events are applied again, not held for a snapshot that is no longer running.
        source.RaiseItemStatus(Status(A, DeviceStatusTypes.Busy));
        Assert.Equal(DeviceStatusTypes.Busy, feed.StatusOf(A)?.Status);
        Assert.Equal(LiveConnection.Interrupted, feed.Connection);

        // And a later snapshot runs: the retry of the failed one.
        clock.FireAll();
        await feed.Settled.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, host.ItemStatusRequests);
        Assert.Equal(LiveConnection.Live, feed.Connection);
        Assert.Null(thrown);
    }

    [Fact]
    public async Task AFrameSubscriberThatThrowsDoesNotEscapeTheTimer()
    {
        // In the host, Flush runs on a timer thread of the process that also runs the engine: an
        // exception escaping it is unhandled and ends that process. ManualTimeProvider.FireAll calls
        // the callback on this thread, so an escape would surface here as a throw.
        var (feed, source, clock, _) = Feed();
        await feed.StartAsync();
        clock.FireAll();
        var frames = 0;
        var throws = true;
        feed.FrameReady += () =>
        {
            frames++;
            if (!throws) return;
            throws = false;
            throw new InvalidOperationException("a page handler that fails (test)");
        };

        source.RaiseItemStatus(Status(A, DeviceStatusTypes.Error));
        var escaped = Record.Exception(clock.FireAll);

        // And the feed goes on: the next change makes the next frame.
        source.RaiseItemStatus(Status(A, DeviceStatusTypes.Busy));
        Assert.Equal(1, clock.PendingTimers);
        clock.FireAll();

        Assert.Null(escaped);
        Assert.Equal(2, frames);
        Assert.Equal("#ffffff", Assert.Single(feed.TakeFrame().Devices).StatusColor);
    }

    [Fact]
    public async Task AfterDisposalNothingMovesAndNothingThrows()
    {
        var (feed, source, clock, _) = Feed();
        source.FailStart = 1;
        await feed.StartAsync(); // interrupted, with a start retry armed
        Assert.Equal(1, clock.PendingTimers);
        var frames = 0;
        var told = 0;
        var commands = 0;
        feed.FrameReady += () => frames++;
        feed.StatusChanged += () => told++;
        feed.CommandChanged += _ => commands++;

        await feed.DisposeAsync();
        Assert.Equal(0, clock.PendingTimers);
        clock.FireAll();
        source.RaiseItemStatus(Status(A, DeviceStatusTypes.Error));
        source.RaiseNavigator(new EngineStatus(NavigatorStatusTypes.Run, DefragmentationTypes.None, "t", DateTime.UtcNow));
        source.RaiseCommand(new CommandDto(9, DateTime.UtcNow, 2001, A, null, null, 2, null, null, null, 0, null,
            null, 0m, null, 0m, null, null, null, true, 0));
        source.RaiseReconnecting();
        source.RaiseReconnected();
        source.RaiseClosed();
        clock.FireAll();
        await feed.Settled;

        Assert.Equal(1, source.StartCalls);
        Assert.Equal(0, frames);
        Assert.Equal(0, told);
        Assert.Equal(0, commands);
        Assert.Equal(0, clock.PendingTimers);
    }

    [Fact]
    public async Task AFailedStartIsInterruptedAndRetriedByTheClock()
    {
        var (feed, source, clock, _) = Feed();
        source.FailStart = 1;

        await feed.StartAsync();
        Assert.Equal(LiveConnection.Interrupted, feed.Connection);

        clock.FireAll();
        await feed.Settled;

        Assert.Equal(2, source.StartCalls);
        Assert.Equal(LiveConnection.Live, feed.Connection);
    }

    [Fact]
    public async Task AnUnreadableSnapshotIsInterruptedNotThrown()
    {
        var (feed, _, _, host) = Feed();
        host.BrokenReads.Add("status");

        await feed.StartAsync();

        Assert.Equal(LiveConnection.Interrupted, feed.Connection);
    }

    [Fact]
    public async Task CommandsArePassedOn()
    {
        var (feed, source, _, _) = Feed();
        await feed.StartAsync();
        CommandDto? seen = null;
        feed.CommandChanged += command => seen = command;

        source.RaiseCommand(new CommandDto(9, DateTime.UtcNow, 2001, A, null, null, 2, null, null, null, 0, null,
            null, 0m, null, 0m, null, null, null, true, 0));

        Assert.Equal(9, seen!.Id);
    }

    [Fact]
    public async Task DisposalClosesTheChannelAndStopsTheTimers()
    {
        var (feed, source, clock, _) = Feed();
        await feed.StartAsync();
        source.RaiseItemStatus(Status(A, DeviceStatusTypes.Error));

        await feed.DisposeAsync();

        Assert.True(source.Disposed);
        Assert.Equal(0, clock.PendingTimers);
    }

    // ---- Compito 3, row 60: the missions ---------------------------------------------------------

    /// <summary>A mission event is handed on as it came, the mission and what happened to it.</summary>
    [Fact]
    public async Task MissionEventsArePassedOn()
    {
        var (feed, source, _, _) = Feed();
        await feed.StartAsync();
        (MissionDto Mission, MissionChange Change)? seen = null;
        feed.MissionChanged += (mission, change) => seen = (mission, change);
        var moved = TestMissions.Mission(Guid.NewGuid(), A, "LU-1");

        source.RaiseMission(moved, MissionChange.PositionChanged);

        Assert.Equal((moved, MissionChange.PositionChanged), seen);
    }

    /// <summary>
    /// Whoever reads missions with a GET is told to read again whenever events may have gone by
    /// unseen: at the channel's opening (a read made before it is up missed whatever happened in
    /// between), at every reconnection, after a terminal close once the channel is started again, and
    /// at the host's own resync signal (NavigatorStatusChanged, sent also when its event queue dropped
    /// some) - the same moments the feed reads its own snapshot.
    /// </summary>
    [Fact]
    public async Task ReadersAreToldToReadAgainWheneverEventsMayHaveGoneByUnseen()
    {
        var (feed, source, clock, _) = Feed();
        var resyncs = 0;
        feed.Resynced += () => resyncs++;

        await feed.StartAsync();
        Assert.Equal(1, resyncs);

        source.RaiseReconnecting();
        Assert.Equal(1, resyncs);
        source.RaiseReconnected();
        await feed.Settled;
        Assert.Equal(2, resyncs);

        source.RaiseNavigator(new EngineStatus(NavigatorStatusTypes.Stop, DefragmentationTypes.None, "t", DateTime.UtcNow));
        await feed.Settled;
        Assert.Equal(3, resyncs);

        source.RaiseClosed();
        Assert.Equal(3, resyncs);
        clock.FireAll();
        await feed.Settled;
        Assert.Equal(4, resyncs);
    }

    /// <summary>A start that fails is no opening: nobody is told to read against a channel that is not there.</summary>
    [Fact]
    public async Task AFailedStartTellsNobodyToReadAgain()
    {
        var (feed, source, _, _) = Feed();
        source.FailStart = 1;
        var resyncs = 0;
        feed.Resynced += () => resyncs++;

        await feed.StartAsync();

        Assert.Equal(0, resyncs);
    }

    /// <summary>
    /// A subscriber of the two mission events that throws does not reach the channel, and does not
    /// keep the feed from its own work: the snapshot the same reconnection asked for is still read.
    /// </summary>
    [Fact]
    public async Task AMissionSubscriberThatThrowsReachesNeitherTheChannelNorTheSnapshot()
    {
        var (feed, source, _, host) = Feed();
        feed.MissionChanged += (_, _) => throw new InvalidOperationException("subscriber (test)");
        feed.Resynced += () => throw new InvalidOperationException("subscriber (test)");

        await feed.StartAsync();
        source.RaiseMission(TestMissions.Mission(Guid.NewGuid(), A), MissionChange.Started);
        source.RaiseReconnecting();
        host.ItemStatuses = [Status(A, DeviceStatusTypes.Error)];
        source.RaiseReconnected();
        await feed.Settled;

        Assert.Equal(LiveConnection.Live, feed.Connection);
        Assert.Equal(A, Assert.Single(feed.TakeFrame().Devices).ItemId);
    }

    /// <summary>After disposal the mission events stop too.</summary>
    [Fact]
    public async Task AfterDisposalNoMissionEventIsPassedOn()
    {
        var (feed, source, _, _) = Feed();
        await feed.StartAsync();
        var seen = 0;
        feed.MissionChanged += (_, _) => seen++;
        feed.Resynced += () => seen++;

        await feed.DisposeAsync();
        source.RaiseMission(TestMissions.Mission(Guid.NewGuid(), A), MissionChange.Started);
        source.RaiseReconnected();

        Assert.Equal(0, seen);
    }

    /// <summary>Fix round 1: a subscriber that throws is still kept away from the channel, but not in silence - the feed's log says which event and what was thrown.</summary>
    [Fact]
    public async Task AMissionSubscriberThatThrowsIsLogged()
    {
        var host = new DrawioWriteHandler { NavStatus = NavigatorStatusTypes.Stop };
        var client = new WcsClient(new WcsClientOptions { BaseAddress = new Uri("http://localhost:5580"), ApiKey = "k" }, host);
        var source = new FakePlantEventSource();
        var log = new RecordingLogger();
        var feed = new PlantLiveFeed(client, source, _ => DeviceTypes.Automation, new ManualTimeProvider(), log);
        feed.MissionChanged += (_, _) => throw new InvalidOperationException("mission subscriber (test)");
        feed.Resynced += () => throw new InvalidOperationException("resync subscriber (test)");

        await feed.StartAsync();
        source.RaiseMission(TestMissions.Mission(Guid.NewGuid(), A), MissionChange.Started);

        Assert.Contains(log.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning
            && e.Message.Contains(nameof(PlantLiveFeed.Resynced)) && e.Exception?.Message == "resync subscriber (test)");
        Assert.Contains(log.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning
            && e.Message.Contains(nameof(PlantLiveFeed.MissionChanged)) && e.Exception?.Message == "mission subscriber (test)");
    }
}
