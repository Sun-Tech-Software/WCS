using System.Net;
using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Api.Client;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Drawio.Components;
using Wcs.WebEditor.Drawio.Coordination;
using Wcs.WebEditor.Live;
using Wcs.WebEditor.Session;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

public sealed class DrawioDeviceCommandsTest : PlantCanvasBunitContext
{
    private static readonly Guid A = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
    private readonly DrawioWriteHandler _host = new();
    private readonly FakePlantEventSource _source = new();
    private readonly PlantLiveFeed _feed;

    public DrawioDeviceCommandsTest()
    {
        var state = new EditorSessionState(new Uri("http://localhost:5580"), _host);
        state.UseKey("test-key");
        Services.AddSingleton(state);
        _feed = new PlantLiveFeed(state.Client, _source, _ => DeviceTypes.Automation, new ManualTimeProvider());
    }

    private IRenderedComponent<DrawioDeviceCommands> Render(DeviceStatusTypes status = DeviceStatusTypes.Ready) =>
        Render<DrawioDeviceCommands>(parameters => parameters
            .Add(p => p.ItemId, A)
            .Add(p => p.DeviceName, "Shuttle 1")
            .Add(p => p.Status, status)
            .Add(p => p.Feed, _feed));

    [Theory]
    [InlineData("#drawio-command-execute-step", CommandTypes.ItemExecuteStep)]
    [InlineData("#drawio-command-unload-lu", CommandTypes.ItemUnloadLu)]
    [InlineData("#drawio-command-offline", CommandTypes.ItemSetOffLine)]
    [InlineData("#drawio-command-step-done", CommandTypes.ItemStepDone)]
    public void EachButtonSendsItsCommandForThisDevice(string button, CommandTypes expected)
    {
        var cut = Render();

        cut.Find(button).Click();

        cut.WaitForAssertion(() =>
        {
            var sent = Assert.Single(_host.Commands);
            Assert.Equal((int)expected, sent.CommandType);
            Assert.Equal(A, sent.ItemId);
        });
    }

    [Fact]
    public void AnOfflineDeviceIsSetReady()
    {
        var cut = Render(DeviceStatusTypes.OffLine);

        Assert.Equal("Set Ready", cut.Find("#drawio-command-offline").TextContent.Trim());
        cut.Find("#drawio-command-offline").Click();

        cut.WaitForAssertion(() => Assert.Equal((int)CommandTypes.ItemSetOnLine, Assert.Single(_host.Commands).CommandType));
    }

    [Fact]
    public void AnyOtherStatusIsSetOffLine() =>
        Assert.Equal("Set Off Line", Render(DeviceStatusTypes.Error).Find("#drawio-command-offline").TextContent.Trim());

    [Fact]
    public void AQueuedCommandSaysItsNumber()
    {
        var cut = Render();

        cut.Find("#drawio-command-execute-step").Click();

        cut.WaitForAssertion(() => Assert.Contains("queued for Shuttle 1 as command 1", cut.Find("#drawio-command-outcome").TextContent));
    }

    private const string Unfollowable = "the outcome cannot be followed while the live status is not updated";

    /// <summary>With the live status Live, the line waits for the engine's outcome.</summary>
    [Fact]
    public async Task WithALiveStatusTheLineWaitsForTheEngine()
    {
        await _feed.StartAsync();
        Assert.Equal(LiveConnection.Live, _feed.Connection);
        var cut = Render();

        cut.Find("#drawio-command-execute-step").Click();

        cut.WaitForAssertion(() => Assert.Equal("Execute Step queued for Shuttle 1 as command 1: waiting for the engine.",
            cut.Find("#drawio-command-outcome").TextContent.Trim()));
    }

    /// <summary>
    /// The live status is not being updated (the channel is not open): the outcome can come only on
    /// it, so the line says it cannot be followed instead of waiting for ever.
    /// </summary>
    [Fact]
    public void WithoutALiveStatusTheLineSaysTheOutcomeCannotBeFollowed()
    {
        Assert.NotEqual(LiveConnection.Live, _feed.Connection);
        var cut = Render();

        cut.Find("#drawio-command-execute-step").Click();

        cut.WaitForAssertion(() =>
        {
            var said = cut.Find("#drawio-command-outcome").TextContent;
            Assert.Contains("queued for Shuttle 1 as command 1", said);
            Assert.Contains(Unfollowable, said);
            Assert.DoesNotContain("waiting for the engine", said);
        });
    }

    [Fact]
    public void WithNoChannelAtAllTheLineSaysTheOutcomeCannotBeFollowed()
    {
        var cut = Render<DrawioDeviceCommands>(parameters => parameters
            .Add(p => p.ItemId, A)
            .Add(p => p.DeviceName, "Shuttle 1")
            .Add(p => p.Status, DeviceStatusTypes.Ready)
            .Add(p => p.Feed, (PlantLiveFeed?)null));

        cut.Find("#drawio-command-execute-step").Click();

        cut.WaitForAssertion(() => Assert.Contains(Unfollowable, cut.Find("#drawio-command-outcome").TextContent));
    }

    /// <summary>The channel goes down while the line waits for the engine: from then on the line says so.</summary>
    [Fact]
    public async Task AChannelThatGoesDownWhileWaitingIsSaidOnTheLine()
    {
        await _feed.StartAsync();
        var cut = Render();
        cut.Find("#drawio-command-execute-step").Click();
        cut.WaitForAssertion(() => Assert.Contains("waiting for the engine", cut.Find("#drawio-command-outcome").TextContent));

        _source.RaiseReconnecting();

        cut.WaitForAssertion(() =>
        {
            var said = cut.Find("#drawio-command-outcome").TextContent;
            Assert.Contains("command 1", said);
            Assert.Contains(Unfollowable, said);
        });
    }

    /// <summary>An outcome already told is not overwritten when the channel goes down afterwards.</summary>
    [Fact]
    public async Task AnOutcomeAlreadyToldStaysWhenTheChannelGoesDown()
    {
        await _feed.StartAsync();
        var cut = Render();
        cut.Find("#drawio-command-execute-step").Click();
        cut.WaitForAssertion(() => Assert.Contains("waiting for the engine", cut.Find("#drawio-command-outcome").TextContent));
        _source.RaiseCommand(new CommandDto(1, DateTime.UtcNow, (int)CommandTypes.ItemExecuteStep, A, null, null, (short)2,
            null, null, null, 0, null, null, 0m, null, 0m, null, null, null, true, 0));
        cut.WaitForAssertion(() => Assert.Contains("was executed", cut.Find("#drawio-command-outcome").TextContent));

        _source.RaiseReconnecting();
        await cut.InvokeAsync(() => { });

        Assert.Contains("was executed", cut.Find("#drawio-command-outcome").TextContent);
    }

    /// <summary>
    /// The channel comes back Live with the command already Done on the host: the line stops saying
    /// it cannot be followed and shows the outcome, read with <c>WcsClient.GetCommandAsync</c>.
    /// </summary>
    [Fact]
    public async Task AChannelThatReturnsWithTheCommandAlreadyDoneShowsItAsExecuted()
    {
        await _feed.StartAsync();
        var cut = Render();
        cut.Find("#drawio-command-execute-step").Click();
        cut.WaitForAssertion(() => Assert.Contains("waiting for the engine", cut.Find("#drawio-command-outcome").TextContent));

        _source.RaiseReconnecting();
        cut.WaitForAssertion(() => Assert.Contains(Unfollowable, cut.Find("#drawio-command-outcome").TextContent));

        _host.CommandReads[1] = new CommandDto(1, DateTime.UtcNow, (int)CommandTypes.ItemExecuteStep, A, null, null, (short)2,
            null, null, null, 0, null, null, 0m, null, 0m, null, null, null, true, 0);
        _source.RaiseReconnected();
        await _feed.Settled;

        cut.WaitForAssertion(() => Assert.Contains("was executed", cut.Find("#drawio-command-outcome").TextContent));
    }

    /// <summary>
    /// The channel comes back Live with the command still to do on the host: the line goes back to
    /// "waiting for the engine" - not to "was executed" - and a later CommandChanged still closes it.
    /// </summary>
    [Fact]
    public async Task AChannelThatReturnsWithTheCommandStillRunningGoesBackToWaiting()
    {
        await _feed.StartAsync();
        var cut = Render();
        cut.Find("#drawio-command-execute-step").Click();
        cut.WaitForAssertion(() => Assert.Contains("waiting for the engine", cut.Find("#drawio-command-outcome").TextContent));

        _source.RaiseReconnecting();
        cut.WaitForAssertion(() => Assert.Contains(Unfollowable, cut.Find("#drawio-command-outcome").TextContent));

        // Not in _host.CommandReads: the fake host answers "still to do" (status 0), like a command
        // not yet finished.
        _source.RaiseReconnected();
        await _feed.Settled;

        cut.WaitForAssertion(() => Assert.Equal("Execute Step queued for Shuttle 1 as command 1: waiting for the engine.",
            cut.Find("#drawio-command-outcome").TextContent.Trim()));

        _source.RaiseCommand(new CommandDto(1, DateTime.UtcNow, (int)CommandTypes.ItemExecuteStep, A, null, null, (short)2,
            null, null, null, 0, null, null, 0m, null, 0m, null, null, null, true, 0));

        cut.WaitForAssertion(() => Assert.Contains("was executed", cut.Find("#drawio-command-outcome").TextContent));
    }

    /// <summary>
    /// The resume read (<c>ResumeFollowingAsync</c>) is still in flight - its own answer, "still to
    /// do", predates a live <c>CommandChanged</c> Done that arrives WHILE it waits - and it must not
    /// undo what the live event already said when it lands (fix round 1, reviewer's point 1a).
    /// Awaits <see cref="DrawioDeviceCommands.Resumed"/> (fix round 2, reviewer's point 2) instead of
    /// a fixed delay: a deterministic handle on the resume loop's own task.
    /// </summary>
    [Fact]
    public async Task ALiveCommandChangedDuringTheResumeReadIsNotOverwrittenByTheStaleAnswer()
    {
        await _feed.StartAsync();
        var cut = Render();
        cut.Find("#drawio-command-execute-step").Click();
        cut.WaitForAssertion(() => Assert.Contains("waiting for the engine", cut.Find("#drawio-command-outcome").TextContent));

        _source.RaiseReconnecting();
        cut.WaitForAssertion(() => Assert.Contains(Unfollowable, cut.Find("#drawio-command-outcome").TextContent));

        _host.CommandReadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _source.RaiseReconnected();
        await _host.CommandReadGateReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var resumed = cut.Instance.Resumed;

        _source.RaiseCommand(new CommandDto(1, DateTime.UtcNow, (int)CommandTypes.ItemExecuteStep, A, null, null, (short)2,
            null, null, null, 0, null, null, 0m, null, 0m, null, null, null, true, 0));
        cut.WaitForAssertion(() => Assert.Contains("was executed", cut.Find("#drawio-command-outcome").TextContent));

        // The resume read lands NOW, with the stale "still to do" (status 0) it was issued with.
        _host.CommandReadGate.SetResult();
        await resumed.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains("was executed", cut.Find("#drawio-command-outcome").TextContent);
    }

    /// <summary>
    /// The channel drops again WHILE the resume read is still in flight: when that (now moot) read
    /// lands, the line must keep saying "cannot be followed" rather than move to "waiting for the
    /// engine" while the channel is down (fix round 1, reviewer's point 1b) - and the NEXT time the
    /// channel comes back, it resumes again.
    /// </summary>
    [Fact]
    public async Task AChannelThatDropsDuringTheResumeReadKeepsSayingCannotBeFollowed()
    {
        await _feed.StartAsync();
        var cut = Render();
        cut.Find("#drawio-command-execute-step").Click();
        cut.WaitForAssertion(() => Assert.Contains("waiting for the engine", cut.Find("#drawio-command-outcome").TextContent));

        _source.RaiseReconnecting();
        cut.WaitForAssertion(() => Assert.Contains(Unfollowable, cut.Find("#drawio-command-outcome").TextContent));

        _host.CommandReadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _source.RaiseReconnected();
        await _host.CommandReadGateReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var resumed = cut.Instance.Resumed;

        _source.RaiseReconnecting();
        _host.CommandReadGate.SetResult();
        await resumed.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains(Unfollowable, cut.Find("#drawio-command-outcome").TextContent);

        _host.CommandReads[1] = new CommandDto(1, DateTime.UtcNow, (int)CommandTypes.ItemExecuteStep, A, null, null, (short)2,
            null, null, null, 0, null, null, 0m, null, 0m, null, null, null, true, 0);
        _source.RaiseReconnected();
        await _feed.Settled;
        await cut.Instance.Resumed.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains("was executed", cut.Find("#drawio-command-outcome").TextContent);
    }

    /// <summary>
    /// A brand new send starts WHILE the stale resume's read for the OLD command is still out: that
    /// answer, when it lands, must not touch the NEW command's line - neither its text nor its id
    /// (fix round 1, reviewer's point 1c; already guarded since round 1's <c>_lastId != id</c> check).
    /// Command 1's read is frozen at Done, not the default "still to do" (fix round 3, reviewer's
    /// point 2): a "still to do" answer, applied blindly, would call <c>SayWaiting</c>, which reads
    /// the CURRENT <c>_lastLabel</c>/<c>_lastId</c> fields - already command 2's by the time it lands -
    /// and so would print the SAME text this test asserts even with the guard removed. A Done answer
    /// for command 1 instead calls <c>OutcomeFor</c> with the read's OWN <see cref="CommandDto.Id"/>
    /// (1, not 2) baked into the text, so a blind apply is caught for what it is.
    /// </summary>
    [Fact]
    public async Task ANewSendDuringTheResumeReadIsNotOverwrittenByTheStaleAnswer()
    {
        await _feed.StartAsync();
        var cut = Render();
        cut.Find("#drawio-command-execute-step").Click();
        cut.WaitForAssertion(() => Assert.Contains("command 1", cut.Find("#drawio-command-outcome").TextContent));

        _source.RaiseReconnecting();
        cut.WaitForAssertion(() => Assert.Contains(Unfollowable, cut.Find("#drawio-command-outcome").TextContent));

        _host.CommandReadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _host.CommandReads[1] = new CommandDto(1, DateTime.UtcNow, (int)CommandTypes.ItemExecuteStep, A, null, null, (short)2,
            null, null, null, 0, null, null, 0m, null, 0m, null, null, null, true, 0);
        _source.RaiseReconnected();
        await _host.CommandReadGateReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var resumed = cut.Instance.Resumed;

        // A brand new send, for a DIFFERENT command, starts while command 1's stale resume read is
        // still out.
        cut.Find("#drawio-command-unload-lu").Click();
        cut.WaitForAssertion(() => Assert.Equal("Unload LU queued for Shuttle 1 as command 2: waiting for the engine.",
            cut.Find("#drawio-command-outcome").TextContent.Trim()));

        // Command 1's stale read lands now, with Done: it must not touch command 2's line.
        _host.CommandReadGate.SetResult();
        await resumed.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("Unload LU queued for Shuttle 1 as command 2: waiting for the engine.",
            cut.Find("#drawio-command-outcome").TextContent.Trim());
    }

    /// <summary>
    /// A resume asked for WHILE one is already in flight folds into it instead of being dropped (fix
    /// round 2, reviewer's point 1): read R1 for command 1 goes out with the engine "running"; the
    /// channel drops again and the engine finishes DURING that outage - its Done is never delivered,
    /// because the channel is down - then the channel comes back. Before this fix that reconnection
    /// found a resume already "in flight" (<c>_resuming</c>) and did nothing; R1 then landed with its
    /// own frozen "running" and the line was stuck on "waiting for the engine" for good. Now the
    /// second reconnection folds into R1's loop, which discards R1's stale answer and reads again once
    /// R1 lands - landing on the real, current Done.
    /// </summary>
    [Fact]
    public async Task AResumeRequestedWhileOneIsInFlightIsFoldedInsteadOfDropped()
    {
        await _feed.StartAsync();
        var cut = Render();
        cut.Find("#drawio-command-execute-step").Click();
        cut.WaitForAssertion(() => Assert.Contains("waiting for the engine", cut.Find("#drawio-command-outcome").TextContent));

        _source.RaiseReconnecting();
        cut.WaitForAssertion(() => Assert.Contains(Unfollowable, cut.Find("#drawio-command-outcome").TextContent));

        _host.CommandReadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // R1's answer will be FROZEN at "running" (status 1) the moment it reaches the gate.
        _host.CommandReads[1] = new CommandDto(1, DateTime.UtcNow, (int)CommandTypes.ItemExecuteStep, A, null, null, (short)1,
            null, null, null, 0, null, null, 0m, null, 0m, null, null, null, true, 0);
        _source.RaiseReconnected();
        await _host.CommandReadGateReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var resumed = cut.Instance.Resumed;

        // The channel drops again WHILE R1 is still out, and the engine finishes DURING the outage:
        // the live CommandChanged for it never arrives (the channel is down), so only a fresh read
        // will ever see it.
        _source.RaiseReconnecting();
        _host.CommandReads[1] = new CommandDto(1, DateTime.UtcNow, (int)CommandTypes.ItemExecuteStep, A, null, null, (short)2,
            null, null, null, 0, null, null, 0m, null, 0m, null, null, null, true, 0);
        _source.RaiseReconnected();
        await _feed.Settled;
        // The fold decision (_resumeAgain = true) happens inside THIS reconnection's own dispatch.
        await cut.Instance.Resumed.WaitAsync(TimeSpan.FromSeconds(10));

        // R1 lands now, with the stale "running" it was frozen with when it was issued.
        _host.CommandReadGate.SetResult();
        await resumed.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains("was executed", cut.Find("#drawio-command-outcome").TextContent);
    }

    /// <summary>
    /// A fold can be for a DIFFERENT line than the one the in-flight read was about: command 1's read
    /// is still out when a new send (command 2) takes over, and command 2's OWN line goes unfollowable
    /// in its turn before the channel comes back. The fold this reconnection records is for command 2,
    /// not command 1 - and command 1's loop, landing after, must carry it over onto command 2's id
    /// instead of returning at its own stale id and silently discarding the fold (fix round 3,
    /// reviewer's point 3).
    /// </summary>
    [Fact]
    public async Task AFoldForADifferentLineAdoptsItsNewId()
    {
        await _feed.StartAsync();
        var cut = Render();
        cut.Find("#drawio-command-execute-step").Click();
        cut.WaitForAssertion(() => Assert.Contains("command 1", cut.Find("#drawio-command-outcome").TextContent));

        _source.RaiseReconnecting();
        cut.WaitForAssertion(() => Assert.Contains(Unfollowable, cut.Find("#drawio-command-outcome").TextContent));

        _host.CommandReadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _source.RaiseReconnected();
        await _host.CommandReadGateReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var resumed = cut.Instance.Resumed;

        // A new send, for a different command, while command 1's read is still out: the feed is
        // already Live, so it waits for the engine rather than saying "cannot be followed".
        cut.Find("#drawio-command-unload-lu").Click();
        cut.WaitForAssertion(() => Assert.Equal("Unload LU queued for Shuttle 1 as command 2: waiting for the engine.",
            cut.Find("#drawio-command-outcome").TextContent.Trim()));

        // Command 2's line becomes unfollowable in its own turn, and the channel returns WHILE
        // command 1's read is still the one out.
        _source.RaiseReconnecting();
        await cut.Instance.Resumed.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(Unfollowable, cut.Find("#drawio-command-outcome").TextContent);

        _host.CommandReads[2] = new CommandDto(2, DateTime.UtcNow, (int)CommandTypes.ItemUnloadLu, A, null, null, (short)2,
            null, null, null, 0, null, null, 0m, null, 0m, null, null, null, true, 0);
        _source.RaiseReconnected();
        await _feed.Settled;
        // The fold decision - for command 2, not command 1 - happens inside THIS dispatch.
        await cut.Instance.Resumed.WaitAsync(TimeSpan.FromSeconds(10));

        // Command 1's stale read lands now: the fold must carry over to command 2's id, not vanish.
        _host.CommandReadGate.SetResult();
        await resumed.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains("Unload LU for Shuttle 1 (command 2) was executed.", cut.Find("#drawio-command-outcome").TextContent);
    }

    /// <summary>A resume read that fails (host down, bad answer) does not throw and leaves the line exactly as it was.</summary>
    [Fact]
    public async Task AFailedResumeReadLeavesTheLineAsItWas()
    {
        await _feed.StartAsync();
        var cut = Render();
        cut.Find("#drawio-command-execute-step").Click();
        cut.WaitForAssertion(() => Assert.Contains("waiting for the engine", cut.Find("#drawio-command-outcome").TextContent));

        _source.RaiseReconnecting();
        cut.WaitForAssertion(() => Assert.Contains(Unfollowable, cut.Find("#drawio-command-outcome").TextContent));

        _host.FailRoute = "command-read";
        _source.RaiseReconnected();
        await _feed.Settled;
        // The resume, not only the feed: without this the assert could run before a resume that
        // mishandled the failed read had acted, and pass on the wrong code.
        await cut.Instance.Resumed.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains(Unfollowable, cut.Find("#drawio-command-outcome").TextContent);
    }

    [Fact]
    public void ARefusalSaysNothingWasSent()
    {
        _host.CommandRefusal = HttpStatusCode.Conflict;
        var cut = Render();

        cut.Find("#drawio-command-unload-lu").Click();

        cut.WaitForAssertion(() => Assert.Contains("was NOT sent", cut.Find("#drawio-command-outcome").TextContent));
    }

    /// <summary>
    /// Final review, I1: the three ways a send can fail are told apart, for the button and for the
    /// menu alike (one follower). A 4xx is the host refusing - NOT sent; a 200 whose body cannot be
    /// read means the command WAS queued (WcsClient reads the body only after a 2xx), so its id and
    /// outcome are unknown; no answer at all, or a 5xx, means it is NOT known whether it was queued.
    /// Saying "NOT sent" in the last two would invite a second, real command. Row 37 of the queue (the
    /// owner's answer of 9/10): one sentence, and what came back - the host's answer as it came, or
    /// the failure's type and message - behind the line's "?", where the sentence used to quote it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AHostRefusalSaysNotSentWithTheHostsAnswerBehindTheQuestionMark(bool viaMenu)
    {
        _host.FailRoute = "command-post";
        _host.FailureStatus = HttpStatusCode.Conflict;
        _host.FailureBody = JsonSerializer.Serialize(new { title = "Il motore non è fermo", status = 409 });

        var (outcome, detail) = SendExecuteStep(viaMenu);

        Assert.Equal("Execute Step was NOT sent to Shuttle 1 because the host refused it.", outcome);
        Assert.Equal("the host answered 409: {\"title\":\"Il motore non \\u00E8 fermo\",\"status\":409}", detail);
    }

    /// <summary>
    /// Final review, M9 (C1): the 503 of the engine not loaded is the filter of the "engine" group
    /// turning the POST away before the endpoint runs - as certain a "no" as a 4xx, and not one of the
    /// 5xx after which it is not known whether the command was queued. Said as NOT sent, with why.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnEngineNotLoadedSaysNotSentBecauseTheEngineIsNotReady(bool viaMenu)
    {
        const string answer = "{\"title\":\"Il motore non è caricato\",\"status\":503,\"unavailable\":\"engineNotLoaded\"}";
        _host.FailRoute = "command-post";
        _host.FailureStatus = HttpStatusCode.ServiceUnavailable;
        _host.FailureBody = answer;

        var (outcome, detail) = SendExecuteStep(viaMenu);

        Assert.Equal("Execute Step was NOT sent to Shuttle 1 because the engine is not ready (it is not loaded on the host).", outcome);
        Assert.Equal("the host answered 503: " + answer, detail);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnUnreadableAnswerSaysTheCommandWasQueuedButCannotBeFollowed(bool viaMenu)
    {
        _host.GarbledRoute = "command-post";

        var (outcome, detail) = SendExecuteStep(viaMenu);

        Assert.Equal(
            "Execute Step was queued for Shuttle 1, but the host's answer could not be read: its id is not known and its outcome cannot be followed here - check before sending it again.",
            outcome);
        Assert.StartsWith("JsonException: ", detail);
    }

    [Theory]
    [InlineData("timeout", false, "the host did not answer in time", "TaskCanceledException: ")]
    [InlineData("transport", false, "the connection to the host failed", "HttpRequestException: ")]
    [InlineData("500", false, "the host answered with an error", "the host answered 500 with an empty body")]
    [InlineData("timeout", true, "the host did not answer in time", "TaskCanceledException: ")]
    [InlineData("transport", true, "the connection to the host failed", "HttpRequestException: ")]
    [InlineData("500", true, "the host answered with an error", "the host answered 500 with an empty body")]
    public void NoAnswerOrA5xxSaysItIsNotKnownWhetherItWasQueued(string failure, bool viaMenu, string why, string detailStart)
    {
        switch (failure)
        {
            case "timeout": _host.TimeoutRoute = "command-post"; break;
            case "transport": _host.HttpFailureRoute = "command-post"; break;
            default:
                _host.FailRoute = "command-post";
                _host.FailureStatus = HttpStatusCode.InternalServerError;
                break;
        }

        var (outcome, detail) = SendExecuteStep(viaMenu);

        Assert.Equal($"It is NOT known whether Execute Step was queued for Shuttle 1, because {why} - check before sending it again.", outcome);
        Assert.StartsWith(detailStart, detail);
    }

    /// <summary>
    /// Execute Step, by the button or by the menu, and the outcome line once the send has ended: its
    /// sentence, and what is behind its "?" (row 37 of the queue).
    /// </summary>
    private (string Outcome, string Detail) SendExecuteStep(bool viaMenu)
    {
        var cut = viaMenu ? RenderWithMenuRequest(new DrawioMenuCommand(A, DrawioDeviceCommand.ExecuteStep)) : Render();
        if (!viaMenu) cut.Find("#drawio-command-execute-step").Click();
        cut.WaitForAssertion(() => Assert.DoesNotContain("Sending", cut.Find("#drawio-command-outcome").TextContent));
        return (cut.Find("#drawio-command-outcome").TextContent.Trim(),
            DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-command-outcome"));
    }

    [Theory]
    [InlineData((short)2, "was executed")]
    [InlineData((short)99, "FAILED: no load unit on board")]
    public async Task TheEnginesOutcomeFollows(short status, string expected)
    {
        await _feed.StartAsync();
        var cut = Render();
        cut.Find("#drawio-command-execute-step").Click();
        cut.WaitForAssertion(() => Assert.Contains("command 1", cut.Find("#drawio-command-outcome").TextContent));

        _source.RaiseCommand(new CommandDto(1, DateTime.UtcNow, (int)CommandTypes.ItemExecuteStep, A, null, null, status,
            status == 99 ? "no load unit on board" : null, null, null, 0, null, null, 0m, null, 0m, null, null, null, true, 0));

        cut.WaitForAssertion(() => Assert.Contains(expected, cut.Find("#drawio-command-outcome").TextContent));
    }

    [Fact]
    public async Task SomebodyElsesCommandChangesNothingHere()
    {
        await _feed.StartAsync();
        var cut = Render();
        cut.Find("#drawio-command-execute-step").Click();
        cut.WaitForAssertion(() => Assert.Contains("command 1", cut.Find("#drawio-command-outcome").TextContent));

        _source.RaiseCommand(new CommandDto(42, DateTime.UtcNow, 2001, A, null, null, 99, "not ours", null, null, 0,
            null, null, 0m, null, 0m, null, null, null, true, 0));

        Assert.DoesNotContain("not ours", cut.Find("#drawio-command-outcome").TextContent);
    }

    /// <summary>
    /// The POST's own response and CommandChanged on the live feed have no ordering guarantee: the
    /// engine can announce the outcome of a command before EnqueueCommandAsync returns with its id.
    /// Without remembering that event, the line would be stuck at "queued ... waiting for the
    /// engine" for ever - OnCommandChanged discards every event whose id it does not know yet.
    /// </summary>
    [Fact]
    public async Task ALiveOutcomeThatArrivesBeforeThePostAnswerIsNotLost()
    {
        await _feed.StartAsync();
        _host.CommandPostGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cut = Render();

        var sending = cut.Find("#drawio-command-execute-step").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        await _host.CommandPostGateReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(sending.IsCompleted, "The send did not stay inside its own POST: the race was never staged.");

        _source.RaiseCommand(new CommandDto(1, DateTime.UtcNow, (int)CommandTypes.ItemExecuteStep, A, null, null, (short)2,
            null, null, null, 0, null, null, 0m, null, 0m, null, null, null, true, 0));

        _host.CommandPostGate.SetResult();
        await sending;

        cut.WaitForAssertion(() => Assert.Contains("was executed", cut.Find("#drawio-command-outcome").TextContent));
    }

    private IRenderedComponent<DrawioDeviceCommands> RenderWithMenuRequest(DrawioMenuCommand request) =>
        Render<DrawioDeviceCommands>(parameters => parameters
            .Add(p => p.ItemId, A)
            .Add(p => p.DeviceName, "Shuttle 1")
            .Add(p => p.Status, DeviceStatusTypes.Ready)
            .Add(p => p.Feed, _feed)
            .Add(p => p.MenuRequest, request));

    /// <summary>
    /// Compito 5, row 63: a command chosen from draw.io's right-click menu is sent by this component
    /// exactly as its own button would send it - the same follower, the same outcome line - and taken
    /// once.
    /// </summary>
    [Fact]
    public void AMenuRequestIsSentAsItsButtonWouldSendIt()
    {
        var request = new DrawioMenuCommand(A, DrawioDeviceCommand.UnloadLu);

        var cut = RenderWithMenuRequest(request);

        cut.WaitForAssertion(() =>
        {
            var sent = Assert.Single(_host.Commands);
            Assert.Equal((int)CommandTypes.ItemUnloadLu, sent.CommandType);
            Assert.Equal(A, sent.ItemId);
            Assert.Contains("Unload LU queued for Shuttle 1 as command 1", cut.Find("#drawio-command-outcome").TextContent);
        });
        Assert.True(request.Taken);

        cut.Render();
        Assert.Single(_host.Commands);
    }

    /// <summary>The menu offered "Set Off Line" and that is what goes out, even if the panel's own button now reads otherwise.</summary>
    [Fact]
    public void AMenuRequestSendsTheCommandTheMenuNamed()
    {
        var cut = Render<DrawioDeviceCommands>(parameters => parameters
            .Add(p => p.ItemId, A)
            .Add(p => p.DeviceName, "Shuttle 1")
            .Add(p => p.Status, DeviceStatusTypes.OffLine)
            .Add(p => p.Feed, _feed)
            .Add(p => p.MenuRequest, new DrawioMenuCommand(A, DrawioDeviceCommand.SetOffLine)));

        cut.WaitForAssertion(() => Assert.Equal((int)CommandTypes.ItemSetOffLine, Assert.Single(_host.Commands).CommandType));
    }

    [Fact]
    public void AMenuRequestForAnotherDeviceIsLeftAlone()
    {
        var request = new DrawioMenuCommand(Guid.NewGuid(), DrawioDeviceCommand.ExecuteStep);

        RenderWithMenuRequest(request);

        Assert.Empty(_host.Commands);
        Assert.False(request.Taken);
    }

    /// <summary>
    /// A menu command arriving while the panel's previous command is still on its way: the button is
    /// disabled then, and the menu is not a way round it - nothing is sent, and the panel says so
    /// instead of dropping the choice in silence.
    /// </summary>
    [Fact]
    public async Task AMenuRequestWhileACommandIsBeingSentIsRefusedAndSaid()
    {
        _host.CommandPostGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cut = Render();
        var sending = cut.Find("#drawio-command-execute-step").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        await _host.CommandPostGateReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cut.Render(parameters => parameters.Add(p => p.MenuRequest, new DrawioMenuCommand(A, DrawioDeviceCommand.StepDone)));

        cut.WaitForAssertion(() => Assert.Equal(
            "Step Done from the menu was NOT sent to Shuttle 1: the previous command is still being sent. Wait for its outcome here, then choose it again.",
            cut.Find("#drawio-command-menu-refused").TextContent.Trim()));
        _host.CommandPostGate.SetResult();
        await sending;
        Assert.Single(_host.Commands);
    }

    /// <summary>
    /// Fix round 1 of compito 5: the menu's send starts from OnParametersSet and nobody awaits it, so
    /// a failure the follower does not recognise (anything but a transport, host or JSON failure)
    /// would be lost there - the line stuck on "Sending..." for ever. It is caught and said on the
    /// outcome line instead: whether the host queued the command is not known.
    /// </summary>
    [Fact]
    public async Task AnUnexpectedFailureOfAMenuSendIsSaidOnTheOutcomeLine()
    {
        _host.CommandPostGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cut = RenderWithMenuRequest(new DrawioMenuCommand(A, DrawioDeviceCommand.ExecuteStep));
        await _host.CommandPostGateReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        _host.CommandPostGate.SetException(new InvalidOperationException("broken on purpose (test)"));

        cut.WaitForAssertion(() => Assert.Equal(
            "Execute Step for Shuttle 1: the send failed in a way this page does not know how to tell apart, and whether the host queued it is NOT known - look at the device before sending it again.",
            cut.Find("#drawio-command-outcome").TextContent.Trim()));
        Assert.Equal("InvalidOperationException: broken on purpose (test)", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-command-outcome"));
        Assert.Null(cut.Find("#drawio-command-execute-step").GetAttribute("disabled"));
    }

    /// <summary>The same failure from the button is said the same way: one path, one outcome line.</summary>
    [Fact]
    public async Task AnUnexpectedFailureOfAButtonSendIsSaidOnTheOutcomeLine()
    {
        _host.CommandPostGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cut = Render();
        var sending = cut.Find("#drawio-command-unload-lu").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        await _host.CommandPostGateReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        _host.CommandPostGate.SetException(new InvalidOperationException("broken on purpose (test)"));
        await sending;

        cut.WaitForAssertion(() => Assert.Equal(
            "Unload LU for Shuttle 1: the send failed in a way this page does not know how to tell apart, and whether the host queued it is NOT known - look at the device before sending it again.",
            cut.Find("#drawio-command-outcome").TextContent.Trim()));
        Assert.Equal("InvalidOperationException: broken on purpose (test)", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-command-outcome"));
    }
}
