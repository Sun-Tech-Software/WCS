using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Live;
using Wcs.WebEditor.Pages;
using Wcs.WebEditor.Session;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

public sealed class DrawioEditorLiveTest : PlantCanvasBunitContext
{
    private const string LiveModule = "./_content/Wcs.WebEditor/drawio-live.js";
    private static readonly Guid A = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002");
    private static readonly Guid C = Guid.Parse("cccccccc-0000-4000-8000-000000000003");

    private readonly FakePlantEventSource _source = new();
    private readonly DrawioWriteHandler _host = new();
    private readonly BunitJSModuleInterop _live;

    public DrawioEditorLiveTest()
    {
        _live = JSInterop.SetupModule(LiveModule);
        _live.Setup<string>("render", _ => true).SetResult("drawn");
    }

    private IRenderedComponent<DrawioEditor> Render()
    {
        var state = new EditorSessionState(new Uri("http://localhost:5580"), _host) { EventSourceForTesting = () => _source };
        state.UseKey("test-key");
        Services.AddSingleton(state);
        return Render<DrawioEditor>();
    }

    /// <summary>
    /// Renders the page and waits until the feed's first snapshot is on the canvas, recognised by
    /// <paramref name="shown"/> - a device the host's snapshot shows - drawn. An empty status line
    /// or a missing banner before that would prove nothing: the page renders without either before
    /// the feed even exists, and an event raised before the feed listens reaches nobody.
    /// </summary>
    private async Task<IRenderedComponent<DrawioEditor>> RenderLive(Guid shown)
    {
        var cut = Render();
        await cut.InvokeAsync(() => cut.Instance.OnDrawioReady());
        await UntilDrawn(cut, shown);
        return cut;
    }

    private bool Drawn(Guid itemId) =>
        _live.Invocations["render"].Any(call => ((string)call.Arguments[0]!).Contains(itemId.ToString()));

    /// <summary>
    /// Waits for a frame that carries <paramref name="itemId"/>. Not WaitForAssertion: that one looks
    /// again only when the page renders, and a frame sent to the canvas is no render of the page.
    /// The invocations are read on the renderer's dispatcher, where the page records them.
    /// </summary>
    private async Task UntilDrawn(IRenderedComponent<DrawioEditor> cut, Guid itemId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!await cut.InvokeAsync(() => Drawn(itemId)))
        {
            Assert.True(DateTime.UtcNow < deadline, $"No frame for {itemId} reached the canvas.");
            await Task.Delay(20);
        }
    }

    private static ItemStatus Status(Guid id, DeviceStatusTypes status, bool loaded = false) =>
        new(id, "", "", "", "A", status, loaded ? LuLoadStatusTypes.Loaded : LuLoadStatusTypes.None,
            loaded ? "LU-7" : "", loaded ? 7m : 0m, Guid.Empty, Guid.Empty);

    [Fact]
    public void AnInterruptedChannelIsSaidOnThePage()
    {
        _source.FailStart = int.MaxValue;

        var cut = Render();

        cut.WaitForAssertion(() =>
            Assert.Contains("Live status is not being updated", TheLiveLinksTip(cut, "Offline", "drawio-pill-bad")));
    }

    [Fact]
    public async Task ALiveChannelSaysNothing()
    {
        _host.ItemStatuses = [Status(B, DeviceStatusTypes.Error)];

        var cut = await RenderLive(B);

        cut.WaitForAssertion(() => AssertTheLiveLinkSaysNothingIsWrong(cut));
    }

    [Fact]
    public async Task TheReadOnlyBannerFollowsTheEngineLive()
    {
        _host.ItemStatuses = [Status(B, DeviceStatusTypes.Error)];
        var cut = await RenderLive(B);
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#drawio-engine-banner")));

        // NavigatorStatusChanged also makes the feed read the snapshot again: the host that sends the
        // event answers the same status to it, as the real one does.
        _host.NavStatus = NavigatorStatusTypes.Run;
        _source.RaiseNavigator(new EngineStatus(NavigatorStatusTypes.Run, DefragmentationTypes.None, "t", DateTime.UtcNow));

        cut.WaitForAssertion(() => Assert.Contains("engine is running", cut.Find("#drawio-engine-banner").TextContent));
    }

    /// <summary>
    /// A device dropped in this session lives on the canvas under draw.io's own cell id until the
    /// page is reloaded, not under its plant identifier: its live status must be sent to THAT cell.
    /// A frame that spoke of the plant identifier would colour nothing, and say nothing about it.
    /// </summary>
    [Fact]
    public async Task ADeviceStatusReachesTheCanvasWithItsCell()
    {
        _host.Types = [Automation()];
        _host.ItemStatuses = [Status(B, DeviceStatusTypes.Error)];
        var cut = await RenderLive(B);
        await cut.InvokeAsync(() => cut.Instance.OnDrawioCellsAdded([Dropped(Automation(), "drawio-1", 100, 100)]));
        var created = Assert.Single(_host.ItemCreations).ItemId;

        _source.RaiseItemStatus(Status(created, DeviceStatusTypes.Error));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!await cut.InvokeAsync(() => _live.Invocations["render"]
                   .Any(call => ((string)call.Arguments[0]!).Contains("\"cellId\":\"drawio-1\""))))
        {
            Assert.True(DateTime.UtcNow < deadline, "No frame carried the cell the device was dropped as.");
            await Task.Delay(20);
        }

        Assert.DoesNotContain(await cut.InvokeAsync(() => _live.Invocations["render"].ToList()),
            call => ((string)call.Arguments[0]!).Contains(created.ToString()));
    }

    /// <summary>
    /// Missions plan of 30/9, owner's decision 3: the route on hover is on also with the engine
    /// stopped - it only reads the route, and the operator's change of destination builds one at
    /// once, to be seen. On from the opening in Stop, and never switched off by a Run or a Stop. Until
    /// then it was on only in Run (spec, row 53), and this test said so.
    /// </summary>
    [Fact]
    public async Task TheHoverIsOnAlsoWithTheEngineStopped()
    {
        _host.ItemStatuses = [Status(B, DeviceStatusTypes.Error)];
        var cut = await RenderLive(B);
        await UntilLastHover(cut);

        _host.NavStatus = NavigatorStatusTypes.Run;
        _source.RaiseNavigator(new EngineStatus(NavigatorStatusTypes.Run, DefragmentationTypes.None, "t", DateTime.UtcNow));
        cut.WaitForAssertion(() => Assert.Contains("engine is running", cut.Find("#drawio-engine-banner").TextContent));

        _host.NavStatus = NavigatorStatusTypes.Stop;
        _source.RaiseNavigator(new EngineStatus(NavigatorStatusTypes.Stop, DefragmentationTypes.None, "t", DateTime.UtcNow));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#drawio-engine-banner")));

        Assert.All(await cut.InvokeAsync(() => _live.Invocations["setHover"].ToList()), call => Assert.Equal(true, call.Arguments[0]));
    }

    /// <summary>
    /// Compito 4, fix round 3: the page's own last read of the engine said Run, the engine has
    /// stopped since and the page heard it on the feed only; the permit request for the start is then
    /// lost. The reason given is the permit - not "already running" from the old read, which
    /// EnsureWritableAsync used to leave standing when it returned before reading the status.
    /// </summary>
    [Fact]
    public async Task AStartWhosePermitRequestIsLostSaysSoNotAlreadyRunning()
    {
        _host.ItemStatuses = [Status(B, DeviceStatusTypes.Error)];
        _host.NavStatus = NavigatorStatusTypes.Run;
        var cut = await RenderLive(B);

        _host.NavStatus = NavigatorStatusTypes.Stop;
        _source.RaiseNavigator(Engine(NavigatorStatusTypes.Stop));
        cut.WaitForAssertion(() => cut.Find("#drawio-engine-start"));
        _host.HttpFailureRoute = "edit-lock";

        cut.Find("#drawio-engine-start").Click();
        cut.Find("#drawio-engine-confirm").Click();

        cut.WaitForAssertion(() => Assert.Contains("The edit permit could not be asked for", cut.Find("#drawio-engine-error").TextContent));
        Assert.DoesNotContain("already running", cut.Find("#drawio-engine-error").TextContent);
        Assert.Empty(_host.Commands);
    }

    /// <summary>
    /// A Stop that arrives while setHover(true) is still on its way, with the engine running. Until the
    /// missions plan of 30/9 the send in flight picked it up once answered and switched the hover off
    /// (row 53); since owner's decision 3 the Stop leaves it on - nothing switches it off.
    /// </summary>
    [Fact]
    public async Task AStopDuringTheSendOfTheHoverLeavesTheHoverOn()
    {
        // 0: setHover(true) fails, at the opening; 1: the send again at the next frame is held. The send
        // held is the frame's, which the test does not await: bUnit refuses an invocation left unanswered
        // under a call the test awaits (the opening's), as a hang.
        var mode = 0;
        var heldOnReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _live.SetupVoid("setHover", call => mode == 0 && call.Arguments[0] is true)
            .SetException(new JSException("the canvas did not take it (test)"));
        var heldOn = _live.SetupVoid("setHover", call =>
        {
            if (mode != 1 || call.Arguments[0] is not true) return false;
            heldOnReached.TrySetResult();
            return true;
        });
        _host.ItemStatuses = [Status(B, DeviceStatusTypes.Error)];
        _host.NavStatus = NavigatorStatusTypes.Run;
        var cut = await RenderLive(B);
        cut.WaitForAssertion(() => Assert.Contains("route on hover", TheLiveLinksTip(cut, "Live", "drawio-pill-warn")));

        mode = 1;
        _source.RaiseItemStatus(Status(A, DeviceStatusTypes.Error));
        await heldOnReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The page takes the Stop (its banner goes) while setHover(true) is still unanswered.
        _host.NavStatus = NavigatorStatusTypes.Stop;
        _source.RaiseNavigator(new EngineStatus(NavigatorStatusTypes.Stop, DefragmentationTypes.None, "t", DateTime.UtcNow));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#drawio-engine-banner")));

        await cut.InvokeAsync(() => heldOn.SetVoidResult());

        // The send in flight is answered: the line of the failure goes, and nothing was sent but "on".
        cut.WaitForAssertion(() => AssertTheLiveLinkSaysNothingIsWrong(cut));
        Assert.All(await cut.InvokeAsync(() => _live.Invocations["setHover"].ToList()), call => Assert.Equal(true, call.Arguments[0]));
    }

    /// <summary>
    /// The page reads the engine's status by itself before every write, and that GET can land after
    /// a newer live event: it must not repaint the banner the live feed is keeping.
    /// </summary>
    [Fact]
    public async Task AStaleReadOfTheEngineDoesNotRepaintTheLiveBanner()
    {
        _host.ItemStatuses = [Status(B, DeviceStatusTypes.Error)];
        var cut = await RenderLive(B);
        var engineReads = EngineReads();

        _host.NavStatus = NavigatorStatusTypes.Run;
        _source.RaiseNavigator(new EngineStatus(NavigatorStatusTypes.Run, DefragmentationTypes.None, "t", DateTime.UtcNow));
        cut.WaitForAssertion(() => Assert.Contains("engine is running", cut.Find("#drawio-engine-banner").TextContent));
        // The feed's own reload has been answered Run: only from here on is the host's Stop stale.
        cut.WaitForAssertion(() => Assert.True(EngineReads() > engineReads));

        _host.NavStatus = NavigatorStatusTypes.Stop;
        var beforeTheWrite = EngineReads();
        await cut.InvokeAsync(() => cut.Instance.OnDrawioSaved(
            Wcs.WebEditor.Drawio.DrawioDocument.Build(new Wcs.WebEditor.Model.PlantGraph(_host.Items, _host.Junctions, _host.Types), null)));
        await cut.InvokeAsync(() => { });
        await cut.InvokeAsync(() => { });

        Assert.True(EngineReads() > beforeTheWrite, "The write did not read the engine's status: this test would prove nothing.");
        Assert.Contains("engine is running", cut.Find("#drawio-engine-banner").TextContent);

        // Nor does a read that fails: "not known" is the page's own, the feed still knows.
        _host.BrokenReads.Add("status");
        var beforeTheFailedRead = _host.Requests.Count;
        await cut.InvokeAsync(() => cut.Instance.OnDrawioSaved(
            Wcs.WebEditor.Drawio.DrawioDocument.Build(new Wcs.WebEditor.Model.PlantGraph(_host.Items, _host.Junctions, _host.Types), null)));
        await cut.InvokeAsync(() => { });
        await cut.InvokeAsync(() => { });

        Assert.True(_host.Requests.Skip(beforeTheFailedRead).Any(request => request.StartsWith("GET ")
            && request.EndsWith("/status") && !request.EndsWith("/items/status")), "The second write did not read the engine's status.");
        Assert.Contains("engine is running", cut.Find("#drawio-engine-banner").TextContent);
    }

    /// <summary>
    /// Not only a JS failure: whatever goes wrong while a frame is built or drawn is said on the page.
    /// The first frame goes from the seeding, where an exception would have failed OnDrawioReady.
    /// </summary>
    [Fact]
    public async Task AFrameThatCannotBeDrawnIsSaidOnThePage()
    {
        _live.Setup<string>("render", _ => true).SetException(new InvalidOperationException("not a JS failure (test)"));
        _host.ItemStatuses = [Status(B, DeviceStatusTypes.Error)];
        var cut = Render();

        Assert.Null(await Record.ExceptionAsync(() => cut.InvokeAsync(() => cut.Instance.OnDrawioReady())));

        // The browser console knows nothing of a failure on this side: what was thrown is in the tip,
        // apart from the sentence (row 37 of the queue, extended to the whole page on 10/10).
        cut.WaitForAssertion(() =>
        {
            TheLiveLinksTip(cut, "Live off", "drawio-pill-warn");
            Assert.Equal("Live status cannot be drawn on this canvas: the drawing and the save work as before.", TheLiveLinksSentence(cut));
            Assert.Equal("InvalidOperationException: not a JS failure (test)", TheLiveLinksDetail(cut));
        });
    }

    /// <summary>
    /// The live link is up and nothing about it is wrong. Until the passata grafica (2026-09-29) the
    /// page said so by rendering nothing at all; since then it is the bar's quiet pill - the word
    /// "Live" and nothing else, in the neutral tone, with no sentence behind a tip. Any of the lines
    /// the page has for a link that is not well (connecting, interrupted, not drawn, a hover that did
    /// not go through) is a different word, a coloured tone and a tip.
    /// </summary>
    private static void AssertTheLiveLinkSaysNothingIsWrong(IRenderedComponent<DrawioEditor> cut)
    {
        var pill = cut.Find("#drawio-live-status");
        Assert.Equal("Live", pill.TextContent.Trim());
        Assert.Contains("drawio-pill-neutral", pill.ClassList);
        Assert.Empty(pill.QuerySelectorAll(".drawio-tip"));
    }

    /// <summary>
    /// The live link is not well, as the bar says it: <paramref name="word"/> on the pill, in
    /// <paramref name="tone"/>, and the page's sentence behind its tip - returned, for the caller to read
    /// its words. The sentence alone is no proof of what the operator sees: it is in the page whether or
    /// not it is on show, and since the passata grafica it is not - the word and the tone are (review of
    /// 29/9: a calm "Live" over an interrupted link kept every test that read only the sentence green).
    /// </summary>
    private static string TheLiveLinksTip(IRenderedComponent<DrawioEditor> cut, string word, string tone)
    {
        var pill = cut.Find("#drawio-live-status");
        Assert.Equal(word, DrawioTopBarTest.WordOnThePill(pill));
        Assert.Contains(tone, pill.ClassList);
        return Assert.Single(pill.QuerySelectorAll(".drawio-tip")).TextContent;
    }

    /// <summary>The pill's sentence alone, without what was thrown under it.</summary>
    private static string TheLiveLinksSentence(IRenderedComponent<DrawioEditor> cut) =>
        cut.Find("#drawio-live-status-sentence").TextContent;

    /// <summary>What a failure on this side threw, under the pill's sentence in its tip; null when there is nothing.</summary>
    private static string? TheLiveLinksDetail(IRenderedComponent<DrawioEditor> cut) =>
        cut.FindAll("#drawio-live-status-detail") is [var detail] ? detail.TextContent : null;

    private static EngineStatus Engine(NavigatorStatusTypes status) =>
        new(status, DefragmentationTypes.None, "t", DateTime.UtcNow);

    /// <summary>
    /// A hover toggle that fails is not a drawing that fails: it has its own line, with honest words
    /// (no "the browser console says why" for a failure that is not the browser's), and the next
    /// good frame does not take it away - only a hover toggle that goes through does.
    /// </summary>
    [Fact]
    public async Task AHoverThatCannotBeSwitchedIsSaidUntilItIs()
    {
        // 0: setHover fails; 1: it is held unanswered (the send again at the next frame is on its
        // way, as it would be in a browser); 2: it goes through. Since the missions plan of 30/9 the
        // hover is only ever switched on, at the opening, with the engine stopped: the failure is
        // there from the start, with no Run to wait for.
        var mode = 0;
        _live.SetupVoid("setHover", _ => mode == 0)
            .SetException(new InvalidOperationException("not a JS failure (test)"));
        var held = _live.SetupVoid("setHover", _ => mode == 1);
        _live.SetupVoid("setHover", _ => mode == 2).SetVoidResult();
        _host.ItemStatuses = [Status(B, DeviceStatusTypes.Error)];
        var cut = await RenderLive(B);

        cut.WaitForAssertion(() =>
        {
            TheLiveLinksTip(cut, "Live", "drawio-pill-warn");
            Assert.Equal("The route on hover cannot be switched on on this page: the colours, the drawing and the save work as before.", TheLiveLinksSentence(cut));
            Assert.Equal("InvalidOperationException: not a JS failure (test)", TheLiveLinksDetail(cut));
        });

        // A good frame is drawn, and the hover is sent again with it - still unanswered: nothing
        // has gone through, and the line stays.
        mode = 1;
        _source.RaiseItemStatus(Status(A, DeviceStatusTypes.Error));
        await UntilDrawn(cut, A);
        await cut.InvokeAsync(() => { });
        Assert.Contains("route on hover", TheLiveLinksTip(cut, "Live", "drawio-pill-warn"));

        // The hover goes through: the line goes.
        mode = 2;
        await cut.InvokeAsync(() => held.SetVoidResult());
        cut.WaitForAssertion(() => AssertTheLiveLinkSaysNothingIsWrong(cut));
        await UntilLastHover(cut);
    }

    /// <summary>
    /// A setHover that fails is sent again at the next frame: the engine stays stopped, so no change
    /// of its status gives it another chance, and the hover would stay off for as long as the engine
    /// stays put. The line of the failure goes only when a send goes through, so it going is the
    /// proof that the send was made again.
    /// </summary>
    [Fact]
    public async Task AFailedHoverSendIsSentAgainAtTheNextFrame()
    {
        var failHover = true;
        _live.SetupVoid("setHover", _ => failHover)
            .SetException(new JSException("the canvas did not take it (test)"));
        _live.SetupVoid("setHover", _ => !failHover).SetVoidResult();
        _host.ItemStatuses = [Status(B, DeviceStatusTypes.Error)];
        // Switched on at the opening, engine stopped (missions plan of 30/9): it fails from the start.
        var cut = await RenderLive(B);
        await UntilLastHover(cut);
        cut.WaitForAssertion(() => Assert.Contains("browser console", TheLiveLinksTip(cut, "Live", "drawio-pill-warn")));
        // The browser's own failure: its console has it, and the tip has nothing under the sentence.
        Assert.Null(TheLiveLinksDetail(cut));

        failHover = false;
        _source.RaiseItemStatus(Status(A, DeviceStatusTypes.Error));

        cut.WaitForAssertion(() => AssertTheLiveLinkSaysNothingIsWrong(cut), TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// The page loaded with the engine stopped, the feed said Run, then the channel fell: the banner
    /// keeps the last known state (the feed's Run), not the page's load-time read. And a fresh read
    /// of the engine made while the channel is down IS newer than that, and wins. The hover, which
    /// followed the same state until the missions plan of 30/9, is on through all of it.
    /// </summary>
    [Fact]
    public async Task AnInterruptedChannelKeepsTheLastKnownEngineStatus()
    {
        _host.ItemStatuses = [Status(B, DeviceStatusTypes.Error)];
        var cut = await RenderLive(B);

        _host.NavStatus = NavigatorStatusTypes.Run;
        _source.RaiseNavigator(Engine(NavigatorStatusTypes.Run));
        cut.WaitForAssertion(() => Assert.Contains("engine is running", cut.Find("#drawio-engine-banner").TextContent));
        await UntilLastHover(cut);

        _source.RaiseReconnecting();
        cut.WaitForAssertion(() => Assert.Contains("not being updated", TheLiveLinksTip(cut, "Offline", "drawio-pill-bad")));
        await cut.InvokeAsync(() => { });
        Assert.Contains("engine is running", cut.Find("#drawio-engine-banner").TextContent);
        Assert.True(await cut.InvokeAsync(() => _live.Invocations["setHover"][^1].Arguments[0] is true),
            "The hover was switched off when the channel fell.");

        // A write reads the engine again while the channel is down: that read is the newest.
        _host.NavStatus = NavigatorStatusTypes.Stop;
        await cut.InvokeAsync(() => cut.Instance.OnDrawioSaved(
            Wcs.WebEditor.Drawio.DrawioDocument.Build(new Wcs.WebEditor.Model.PlantGraph(_host.Items, _host.Junctions, _host.Types), null)));

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#drawio-engine-banner")));
        Assert.All(await cut.InvokeAsync(() => _live.Invocations["setHover"].ToList()), call => Assert.Equal(true, call.Arguments[0]));
    }

    /// <summary>
    /// The live module's forget, before a seeding, failing in a way that is not a JS failure: the
    /// plant still goes to draw.io - the overlays are secondary, the drawing is not.
    /// </summary>
    [Fact]
    public async Task AForgetThatThrowsDoesNotStopTheSeeding()
    {
        _live.SetupVoid("forget").SetException(new InvalidOperationException("not a JS failure (test)"));
        _host.ItemStatuses = [Status(B, DeviceStatusTypes.Error)];
        var cut = Render();
        // The plant is loaded (the feed is asked for its snapshot only after it): OnDrawioReady seeds.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (_host.ItemStatusRequests == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "The page never got to the end of loading the plant.");
            await Task.Delay(20);
        }

        Assert.Null(await Record.ExceptionAsync(() => cut.InvokeAsync(() => cut.Instance.OnDrawioReady())));

        Assert.Contains(JSInterop.Invocations, call => call.Identifier == "load");
    }

    /// <summary>
    /// A channel that cannot even be built is said on the page, not left unsaid: here, the bar's pill
    /// and the sentence behind its tip; the red line of the row of outcomes, which the operator sees
    /// without opening anything, is DrawioNoticesTest.ALiveChannelThatCannotBeOpenedIsARedLineThatStays.
    /// </summary>
    [Fact]
    public void AChannelThatCannotBeOpenedIsSaidOnThePage()
    {
        var state = new EditorSessionState(new Uri("http://localhost:5580"), _host)
        {
            EventSourceForTesting = () => throw new InvalidOperationException("the channel refused its options (test)"),
        };
        state.UseKey("test-key");
        Services.AddSingleton(state);

        var cut = Render<DrawioEditor>();

        cut.WaitForAssertion(() =>
        {
            TheLiveLinksTip(cut, "Live off", "drawio-pill-bad");
            // The page's own sentence in English, and the exception's text - in whatever language it
            // comes - apart from it, under it in the tip (row 37 of the queue, extended on 10/10).
            Assert.Equal("Live status is not available on this page: the channel to the host could not be opened. The drawing and the save work as before; reload the page to try again.",
                TheLiveLinksSentence(cut));
            Assert.Equal("InvalidOperationException: the channel refused its options (test)", TheLiveLinksDetail(cut));
        });
    }

    private int EngineReads() =>
        _host.Requests.Count(request => request.StartsWith("GET ") && request.EndsWith("/status")
                                        && !request.EndsWith("/items/status"));

    /// <summary>Waits until the page has sent setHover, switching the hover on (it is never switched off).</summary>
    private async Task UntilLastHover(IRenderedComponent<DrawioEditor> cut)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (await cut.InvokeAsync(() => _live.Invocations["setHover"] is { Count: > 0 } calls
                   ? calls[^1].Arguments[0] as bool?
                   : null) != true)
        {
            Assert.True(DateTime.UtcNow < deadline, "The page has not switched the hover on.");
            await Task.Yield();
        }
    }

    // The same type and the same drop as DrawioEditorCreateTest's (Registry()[0] and Dropped): the
    // cell is read out of the device section's own entry, not written by hand.
    private static DeviceTypeDto Automation() =>
        new("automation", (short)DeviceTypes.Automation, "Automation",
            [new ShapePartDto("M 4 4 H 96 V 96 H 4 Z", "#334155", Filled: false, Rigid: false)],
            [new DevicePortDto("in", 4, 50, "in"), new DevicePortDto("out", 96, 50, "out")],
            new ShapePointDto(50, 50), new ShapePointDto(88, 60));

    private static Wcs.WebEditor.Drawio.DrawioAddedCell Dropped(DeviceTypeDto type, string cellId, double x, double y)
    {
        var entries = System.Text.Json.JsonDocument.Parse(Wcs.WebEditor.Drawio.DrawioLibrary.Build([type]));
        var xml = entries.RootElement[0].GetProperty("entries")[0].GetProperty("libs")[0]
            .GetProperty("data")[0].GetProperty("xml").GetString()!;
        var cell = Assert.Single(Wcs.WebEditor.Drawio.DrawioReader.Read(xml).Devices);
        var declared = (string?)System.Xml.Linq.XDocument.Parse(xml).Descendants("object").Single()
            .Attribute(Wcs.WebEditor.Drawio.DrawioLibrary.TypeAttribute);
        return new Wcs.WebEditor.Drawio.DrawioAddedCell(cellId, cell.Mark, declared,
            Wcs.WebEditor.Drawio.DrawioDocument.DefaultLayerId, x, y, cell.Width, cell.Height);
    }

    /// <summary>
    /// draw.io starts only when its configuration answers its 'configure', and the bridge makes the
    /// promise behind that answer in <c>attach</c>: a <c>configure</c> that reaches the bridge BEFORE
    /// <c>attach</c> resolves nothing, the promise <c>attach</c> makes after it is never resolved,
    /// and draw.io waits for ever with an empty canvas (MEASURED in the browser suite: "the device
    /// never appeared on the canvas", intermittent). The page sends the configuration as soon as it
    /// has both the plant and the bridge module - so nothing may await between knowing the bridge
    /// module and calling <c>attach</c>. Here the plant arrives exactly while the live module's
    /// import is still on its way, which is the window an import placed there would open.
    /// <para>
    /// Queue item 27: the order is read off the bridge module's own calls as they happen
    /// (<see cref="InOrder"/>), not off <c>JSInterop.Invocations</c>. That one is keyed by
    /// identifier: it lists every <c>attach</c> together where the FIRST one was made, and a
    /// window's <c>attach</c> comes before any <c>configure</c> - so the bridge's own late
    /// <c>attach</c> always read as earlier than the configure it followed, and the test was green
    /// with the live import moved between the bridge's import and its attach.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheConfigurationNeverReachesTheBridgeBeforeAttach()
    {
        _host.ItemsGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _host.ItemStatusGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var liveImport = new PendingImport(invocation =>
            invocation.Identifier == "import" && invocation.Arguments is [string path] && path == LiveModule);
        JSInterop.AddInvocationHandler(liveImport);
        var bridgeCalls = new List<string>();
        var bridge = JSInterop.SetupModule(BridgeModule);
        bridge.AddInvocationHandler(new InOrder(
            invocation => invocation.Identifier is "attach" or "configure", bridgeCalls));

        var cut = Render();
        await _host.ItemsGateReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // The plant lands while the live module is still being imported, and the page goes all the
        // way through LoadGraphAsync: the live feed's snapshot is asked for only after the
        // configuration has been sent (or found nobody to send it to).
        _host.ItemsGate.SetResult();
        await _host.ItemStatusGateReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Not blind: the import held is really the live module's, and it is still on its way.
        Assert.Equal(1, await cut.InvokeAsync(() => liveImport.Invocations.Count));

        await cut.InvokeAsync(() => liveImport.SetException(new JSException("the live module is late (test)")));
        _host.ItemStatusGate.SetResult();

        cut.WaitForAssertion(() =>
        {
            lock (bridgeCalls) Assert.Equal(["attach", "configure"], bridgeCalls);
        });
    }

    /// <summary>
    /// Answers the bridge's void calls, writing their names down in the order they are made - the
    /// order <c>JSInterop.Invocations</c>, grouped by identifier, does not keep.
    /// </summary>
    private sealed class InOrder(InvocationMatcher matcher, List<string> calls)
        : JSRuntimeInvocationHandler(matcher, isCatchAllHandler: false)
    {
        protected override Task<Microsoft.JSInterop.Infrastructure.IJSVoidResult> HandleAsync(JSRuntimeInvocation invocation)
        {
            lock (calls) calls.Add(invocation.Identifier);
            SetVoidResult();
            return base.HandleAsync(invocation);
        }
    }

    private const string BridgeModule = "./_content/Wcs.WebEditor/drawio-bridge.js";

    /// <summary>
    /// A call on the floating windows' module (drawio-windows.js), not on the bridge: since the
    /// sections are windows (spec 2026-09-29) the page renders six DrawioWindow, each of which calls
    /// its own <c>attach</c> - with a place, not an address, as the third argument - <c>show</c> and
    /// <c>shut</c>, and the page reads and writes the memory of the open ones and asks which is in front. JSInterop.Invocations lists every
    /// module's calls together: left in, a window's <c>attach</c> would pass for the bridge's.
    /// </summary>
    private static bool OfTheWindows(JSRuntimeInvocation invocation) =>
        invocation.Identifier is "show" or "shut" or "openedLastTime" or "rememberOpen" or "bringToFrontIfBehind"
        || invocation.Arguments.Count == 3 && invocation.Arguments[2] is Wcs.WebEditor.Drawio.Components.DrawioWindowPlace;

    /// <summary>
    /// Fix round 2. A real defect this task introduced, caught by a real reader against a real
    /// browser: row 33's own preference read (<c>drawio-bridge.js</c>'s <c>loadRulerPreference</c>)
    /// was placed BETWEEN the bridge module's import and its <c>attach()</c> call - exactly the
    /// window <see cref="DrawioEditor.OnAfterRenderAsync"/>'s own comment forbids ("nothing may
    /// await until attach is called"). A <c>configure</c> that lands in that gap - sent by
    /// <c>LoadGraphAsync</c>'s own concurrent <c>SendLibraryAsync</c>, the instant it has both the
    /// plant and the bridge module - resolves nothing: <c>attach()</c> has not made ITS promise yet,
    /// the answer draw.io is waiting on never arrives, and draw.io sits on an empty canvas forever
    /// (MEASURED against a real host: <c>DrawioArrowByHandTest.TakingTheHeadOffAnArrowAlreadySavedDeletesItsJunctionAndThePageSaysSo</c>
    /// went red on exactly this, "the device never appeared on the canvas").
    /// <para>
    /// <b>Deliberately not about the ruler read by name.</b> It asserts the invariant the whole
    /// method exists to protect - <c>attach</c> is the FIRST call this page ever makes on the bridge
    /// module, before anything else - so that ANY future bridge call placed in that same window goes
    /// red here, not only the one this round closes. That is narrower than "any await": an await that
    /// never results in a call on this module (an HTTP request, say) would not move
    /// <c>identifiers[0]</c> and would not be caught here.
    /// </para>
    /// <para>
    /// <b>Why <see cref="TheConfigurationNeverReachesTheBridgeBeforeAttach"/> could not have caught
    /// this.</b> That test's own race is built entirely around the LIVE module's import - the await
    /// that runs BEFORE the bridge's own import even starts - and by the time it lets that import
    /// resolve, it has already waited for <c>ItemStatusRequests</c> to move, which only happens
    /// AFTER <c>SeedDrawioAsync</c>: <c>LoadGraphAsync</c> has by then run to completion, including
    /// its own (no-op, <c>_bridgeModule</c> still null at the time) call to <c>SendLibraryAsync</c>.
    /// There is no racer left to land in a window that opens LATER, inside the bridge's own
    /// import-to-attach sequence - which is exactly where this round's defect lived.
    /// </para>
    /// <para>
    /// <b>How this test reaches that window instead.</b> The plant's own list is gated
    /// (<c>ItemsGate</c>), so <c>LoadGraphAsync</c> stays blocked, ready to race, for as long as the
    /// test wants. It is released the instant the bridge module's import is seen to have happened
    /// but <c>attach</c> has not - the exact window - so that if anything in there still awaits,
    /// <c>LoadGraphAsync</c>'s freshly-unblocked continuation gets a real chance to reach its own
    /// <c>SendLibraryAsync</c> before <c>attach</c> runs. A fixed <c>OnAfterRenderAsync</c> crosses
    /// that window in one synchronous step with nothing to catch, so the polling loop below is
    /// expected to miss it entirely on green code - that is the passing outcome, not a failure of
    /// the test, which is why the gate is released unconditionally once the deadline passes too.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AttachIsTheFirstCallEverMadeOnTheBridgeModule()
    {
        _host.ItemsGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cut = Render();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            var (imported, attached) = await cut.InvokeAsync(() => (
                JSInterop.Invocations.Any(i => i.Identifier == "import" && i.Arguments is [string p] && p == BridgeModule),
                JSInterop.Invocations.Any(i => i.Identifier == "attach")));
            if (attached || imported) break;
            await Task.Delay(1);
        }
        _host.ItemsGate.TrySetResult();

        cut.WaitForAssertion(() =>
        {
            var identifiers = JSInterop.Invocations
                .Where(invocation => invocation.Identifier != "import" && !OfTheWindows(invocation))
                .Select(invocation => invocation.Identifier)
                .ToList();
            Assert.True(identifiers.Count > 0, "the bridge module was never called at all");
            Assert.Equal("attach", identifiers[0]);
        }, TimeSpan.FromSeconds(5));
    }

    private sealed class PendingImport(InvocationMatcher matcher)
        : JSRuntimeInvocationHandler<IJSObjectReference>(matcher, isCatchAllHandler: false);

    [Fact]
    public async Task ALiveEventAfterThePageIsGoneDoesNothingAndDoesNotThrow()
    {
        _host.ItemStatuses = [Status(B, DeviceStatusTypes.Error)];
        var cut = await RenderLive(B);
        var page = cut.Instance;

        await DisposeComponentsAsync();
        var frames = await cut.InvokeAsync(() => _live.Invocations["render"].Count);

        // The feed can raise one just after its DisposeAsync has returned (a Flush that had already
        // read "not disposed"): the page's handlers get it after the page is gone. They are private,
        // and the feed that would call them is disposed of, so they are called by name.
        foreach (var handler in new[] { "OnLiveFrameReady", "OnLiveStatusChanged" })
        {
            var method = typeof(DrawioEditor).GetMethod(handler,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            Assert.Null(Record.Exception(() => method.Invoke(page, null)));
        }

        // Whatever the handlers queued on the dispatcher has run by the time this does.
        await cut.InvokeAsync(() => { });
        await cut.InvokeAsync(() => { });
        Assert.Equal(frames, await cut.InvokeAsync(() => _live.Invocations["render"].Count));
    }

    /// <summary>
    /// Leaving the page: the live module is cleared BEFORE the feed is stopped, so its hover callbacks
    /// stop first; and a clear that fails with anything - not only a JS error - still lets the bridge
    /// detach and the edit permit go.
    /// </summary>
    [Fact]
    public async Task LeavingThePageClearsTheLiveModuleFirstAndAFailedClearStillDetachesTheBridge()
    {
        _host.ItemStatuses = [Status(B, DeviceStatusTypes.Error)];
        var bridge = JSInterop.SetupModule("./_content/Wcs.WebEditor/drawio-bridge.js");
        bool? feedStoppedAtClear = null;
        _live.SetupVoid("clear", _ =>
        {
            feedStoppedAtClear = _source.Disposed;
            return true;
        }).SetException(new InvalidOperationException("the live module is gone (test)"));
        var cut = await RenderLive(B);
        // A write took the permit: the page gives it back when it goes.
        await cut.InvokeAsync(() => cut.Instance.OnDrawioSaved(
            Wcs.WebEditor.Drawio.DrawioDocument.Build(new Wcs.WebEditor.Model.PlantGraph([.. _host.Items], [], [.. _host.Types]), null)));

        await DisposeComponentsAsync();

        Assert.False(feedStoppedAtClear, "The feed was stopped before the live module was cleared.");
        Assert.True(_source.Disposed);
        Assert.Single(await cut.InvokeAsync(() => bridge.Invocations["detach"].ToList()));
        Assert.Equal(1, EditPermitReleases());
    }

    [Fact]
    public async Task HoveringALoadedDeviceDrawsItsRoute()
    {
        var there = Guid.NewGuid();
        var mission = Guid.NewGuid();
        _host.ItemStatuses = [Status(A, DeviceStatusTypes.Ready, loaded: true)];
        _host.Items = [new PlantItemDto(A, "A", DeviceTypes.Automation, null, null, null, default, default, 0, null, 0, 0, 0, 0, default!, 10, 10, 100, 100, 0)];
        _host.Missions = [new MissionDto(mission, 7m, "LU-7", A, null, there, DateTime.UtcNow, null, null, null, default, null, null,
            default, null, default, 0m, 0m, 0m, null, 0, null, null, null, null, null, null, null, 0m, null, 0m, false, 0m, false, false)];
        // Before the snapshot lands the device is not yet known to be loaded, and the hover would
        // rightly draw nothing: its load unit on the canvas says the snapshot is in.
        var cut = await RenderLive(A);

        await cut.InvokeAsync(() => cut.Instance.OnLiveHover(A.ToString()));

        var call = Assert.Single(_live.Invocations["showRoute"]);
        Assert.Equal(A.ToString(), call.Arguments[0]);
        Assert.Equal(there.ToString(), call.Arguments[2]);
    }

    /// <summary>
    /// Missions plan of 30/9, task 6: with the engine STOPPED, a destination set from the Missions
    /// window is on the canvas at once - the host builds the new route at once, and the hover, on also
    /// in Stop, reads it from the device the load unit is on: the next step (C) and the new
    /// destination (B).
    /// </summary>
    [Fact]
    public async Task WithTheEngineStoppedTheHoverDrawsTheRouteJustSetFromTheMissionsList()
    {
        var mission = Guid.Parse("11111111-0000-4000-8000-000000000001");
        _host.Items = [Device(A, "Shuttle", 10), Device(C, "Lift", 300), Device(B, "Bay", 600)];
        _host.Junctions = [new JunctionDto(Guid.NewGuid(), A, C, 99), new JunctionDto(Guid.NewGuid(), C, B, 99)];
        _host.ItemStatuses = [Status(A, DeviceStatusTypes.Ready, loaded: true)];
        _host.Missions = [TestMissions.Mission(mission, A, "LU-7", loadUnitId: 7m)];
        var cut = await RenderLive(A);
        await UntilLastHover(cut);

        await OpenTheMissionsAndChooseAsync(cut, mission, B);
        await cut.InvokeAsync(() => cut.Find("#drawio-mission-destination-button").Click());
        await cut.InvokeAsync(() => cut.Find("#drawio-mission-destination-confirm").Click());
        cut.WaitForAssertion(() => Assert.Equal("LU-7 is now bound for Bay: the host took it.", cut.Find("#drawio-mission-move-outcome").TextContent));

        await cut.InvokeAsync(() => cut.Instance.OnLiveHover(A.ToString()));

        var call = Assert.Single(await cut.InvokeAsync(() => _live.Invocations["showRoute"].ToList()));
        Assert.Equal([A.ToString(), C.ToString(), B.ToString()], call.Arguments);
    }

    /// <summary>
    /// The same after a move, with the engine stopped: the host builds the route from the new device
    /// at once (the navigator would go straight to the destination), the engine announces the two
    /// devices (queue item 31), and the hover on the new device draws the route from there. Here the
    /// route after the move is the fake's (DrawioWriteHandler mirrors MissionGateway.Move) and the
    /// announcement is raised by the test: what this layer proves is that the page reads the route
    /// again at every hover, with nothing kept from before the move. The engine's half is in
    /// MissionMoveRouteTest, and the whole way in the browser test.
    /// </summary>
    [Fact]
    public async Task WithTheEngineStoppedTheHoverDrawsTheRouteFromWhereAMissionWasMoved()
    {
        var mission = Guid.Parse("11111111-0000-4000-8000-000000000001");
        _host.Items = [Device(A, "Shuttle", 10), Device(C, "Lift", 300), Device(B, "Bay", 600)];
        _host.Junctions = [new JunctionDto(Guid.NewGuid(), C, B, 99)];
        _host.ItemStatuses = [Status(A, DeviceStatusTypes.Ready, loaded: true)];
        _host.Missions = [TestMissions.Mission(mission, A, "LU-7", loadUnitId: 7m, destination: B)];
        var cut = await RenderLive(A);
        await UntilLastHover(cut);

        await OpenTheMissionsAndChooseAsync(cut, mission, C);
        await cut.InvokeAsync(() => cut.Find("#drawio-mission-move-button").Click());
        await cut.InvokeAsync(() => cut.Find("#drawio-mission-move-confirm").Click());
        cut.WaitForAssertion(() => Assert.Contains("was moved onto Lift: the host took it.", cut.Find("#drawio-mission-move-outcome").TextContent));
        _source.RaiseItemStatus(Status(A, DeviceStatusTypes.Ready));
        _source.RaiseItemStatus(Status(C, DeviceStatusTypes.Ready, loaded: true));
        await UntilDrawn(cut, C);

        await cut.InvokeAsync(() => cut.Instance.OnLiveHover(C.ToString()));

        var call = Assert.Single(await cut.InvokeAsync(() => _live.Invocations["showRoute"].ToList()));
        Assert.Equal([C.ToString(), B.ToString(), B.ToString()], call.Arguments);
    }

    /// <summary>Opens the Missions window, chooses the mission's row and picks <paramref name="target"/>, each gesture on the renderer's dispatcher.</summary>
    private static async Task OpenTheMissionsAndChooseAsync(IRenderedComponent<DrawioEditor> cut, Guid mission, Guid target)
    {
        await cut.InvokeAsync(() => cut.Find("#drawio-mission-list-toggle").Click());
        cut.WaitForAssertion(() => cut.Find($"#drawio-mission-list-table tr[data-mission-id='{mission}']"));
        await cut.InvokeAsync(() => cut.Find($"#drawio-mission-list-table tr[data-mission-id='{mission}']").Click());
        await cut.InvokeAsync(() => cut.Find("#drawio-mission-move-target").Change(target.ToString()));
    }

    /// <summary>
    /// The device commands (row 54) show only with the engine running, as in the WinForms: DeviceOf
    /// parses a cell id as a Guid before falling back to the drawn-cell map, so the device's own
    /// identifier stands for its cell here - no drop onto the canvas is needed to select it.
    /// </summary>
    [Fact]
    public async Task TheCommandsShowOnlyWithTheEngineRunning()
    {
        _host.Items = [new PlantItemDto(A, "A", DeviceTypes.Automation, null, null, null, default, default, 0, null, 0, 0, 0, 0, default!, 10, 10, 100, 100, 0)];
        _host.Configs[A] = new PlantItemConfigDto { ItemId = A, Name = "A", DeviceType = 0 };
        var cut = Render();
        await cut.InvokeAsync(() => cut.Instance.OnDrawioReady());
        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([A.ToString()]));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#drawio-property-panel")));
        Assert.Empty(cut.FindAll("#drawio-commands"));

        // NavigatorStatusChanged also makes the feed read the snapshot again (see PlantLiveFeed.OnNavigator),
        // and the host that sends the event answers the same status to it, as the real one does - the
        // fake host's own NavStatus has to agree, or that reread flips the shown status back to Stop.
        _host.NavStatus = NavigatorStatusTypes.Run;
        _source.RaiseNavigator(new EngineStatus(NavigatorStatusTypes.Run, DefragmentationTypes.None, "t", DateTime.UtcNow));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("#drawio-commands")));
    }

    /// <summary>
    /// StatusOf is read at DeviceCommands' render time, not stored: RefreshLiveAsync must re-render
    /// the page after every live frame (while a device is selected), or "Set Ready"/"Set Off Line"
    /// would stay one frame behind - the button would not follow a device that goes Offline live.
    /// </summary>
    [Fact]
    public async Task TheOfflineButtonFollowsTheDevicesLiveStatus()
    {
        _host.Items = [new PlantItemDto(A, "A", DeviceTypes.Automation, null, null, null, default, default, 0, null, 0, 0, 0, 0, default!, 10, 10, 100, 100, 0)];
        _host.Configs[A] = new PlantItemConfigDto { ItemId = A, Name = "A", DeviceType = 0 };
        _host.ItemStatuses = [Status(A, DeviceStatusTypes.Ready)];
        var cut = Render();
        await cut.InvokeAsync(() => cut.Instance.OnDrawioReady());
        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([A.ToString()]));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#drawio-property-panel")));

        _host.NavStatus = NavigatorStatusTypes.Run;
        _source.RaiseNavigator(new EngineStatus(NavigatorStatusTypes.Run, DefragmentationTypes.None, "t", DateTime.UtcNow));
        cut.WaitForAssertion(() => Assert.Equal("Set Off Line", cut.Find("#drawio-command-offline").TextContent.Trim()));

        _source.RaiseItemStatus(Status(A, DeviceStatusTypes.OffLine));

        cut.WaitForAssertion(() => Assert.Equal("Set Ready", cut.Find("#drawio-command-offline").TextContent.Trim()));
    }

    /// <summary>
    /// DeviceCommands keys the component on the selected device's id (DrawioEditor.razor,
    /// builder.SetKey(itemId)): switching the selection must not carry a device's own outcome line
    /// over to another device's panel, and a live event for the FIRST device's command must not
    /// touch the SECOND device's panel either - it is a fresh component, not the same one reused
    /// with new parameters.
    /// </summary>
    [Fact]
    public async Task SelectingAnotherDeviceLeavesNoOutcomeBehind()
    {
        _host.Items =
        [
            new PlantItemDto(A, "A", DeviceTypes.Automation, null, null, null, default, default, 0, null, 0, 0, 0, 0, default!, 10, 10, 100, 100, 0),
            new PlantItemDto(B, "B", DeviceTypes.Automation, null, null, null, default, default, 0, null, 0, 0, 0, 0, default!, 200, 10, 100, 100, 0),
        ];
        _host.Configs[A] = new PlantItemConfigDto { ItemId = A, Name = "A", DeviceType = 0 };
        _host.Configs[B] = new PlantItemConfigDto { ItemId = B, Name = "B", DeviceType = 0 };
        var cut = Render();
        await cut.InvokeAsync(() => cut.Instance.OnDrawioReady());
        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([A.ToString()]));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#drawio-property-panel")));

        _host.NavStatus = NavigatorStatusTypes.Run;
        _source.RaiseNavigator(new EngineStatus(NavigatorStatusTypes.Run, DefragmentationTypes.None, "t", DateTime.UtcNow));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("#drawio-commands")));

        cut.Find("#drawio-command-execute-step").Click();
        cut.WaitForAssertion(() => Assert.Contains("command 1", cut.Find("#drawio-command-outcome").TextContent));

        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([B.ToString()]));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("#drawio-commands")));
        Assert.Empty(cut.FindAll("#drawio-command-outcome"));

        _source.RaiseCommand(new CommandDto(1, DateTime.UtcNow, (int)CommandTypes.ItemExecuteStep, A, null, null, (short)2,
            null, null, null, 0, null, null, 0m, null, 0m, null, null, null, true, 0));

        Assert.Empty(cut.FindAll("#drawio-command-outcome"));
    }

    private static PlantItemDto Device(Guid id, string name, double x) =>
        new(id, name, DeviceTypes.Automation, null, null, null, default, default, 0, null, 0, 0, 0, 0, default!, x, 10, 100, 100, 0);

    /// <summary>
    /// Renders the page with <paramref name="drawing"/> as what draw.io hands over to
    /// <c>currentDrawing</c>, and waits for the plant to be on the canvas (a live frame for A drawn:
    /// the page sends frames only once the document has reached draw.io) and for the switch.
    /// </summary>
    private Task<(IRenderedComponent<DrawioEditor> Page, BunitJSModuleInterop Bridge)> RenderWithDrawing(
        Func<string> drawing) => RenderWithDrawing(() => new TestJSStreamReference(drawing()));

    /// <summary>
    /// The same, with what the bridge's <c>currentDrawing</c> answers as Blazor hands it to the page:
    /// the drawing's bytes as a stream (queue item 13).
    /// </summary>
    private async Task<(IRenderedComponent<DrawioEditor> Page, BunitJSModuleInterop Bridge)> RenderWithDrawing(
        Func<IJSStreamReference> drawing)
    {
        _host.Types = [Automation()];
        // In Error: a device with nothing to show (Ready, no load unit) is not in the frame at all.
        _host.ItemStatuses = [Status(A, DeviceStatusTypes.Error)];
        var bridge = JSInterop.SetupModule("./_content/Wcs.WebEditor/drawio-bridge.js");
        bridge.Setup<IJSStreamReference?>("currentDrawing").SetResult(drawing());
        var cut = await RenderLive(A);
        cut.WaitForAssertion(() => cut.Find("#drawio-engine-start"));
        return (cut, bridge);
    }

    /// <summary>
    /// The drawing handed back exactly as the page built it for the plant: nothing to write, so the
    /// start goes through - on the real comparison (DrawioDelta.Compute), the decoration included,
    /// which the save always rewrites and this check does not compare. Then the host's event ends
    /// "Starting the engine..." and the switch offers Stop.
    /// </summary>
    [Fact]
    public async Task AnUntouchedDrawingLetsTheEngineStart()
    {
        _host.Items = [Device(A, "A", 10)];
        var (cut, bridge) = await RenderWithDrawing(() =>
            Wcs.WebEditor.Drawio.DrawioDocument.Build(
                new Wcs.WebEditor.Model.PlantGraph([.. _host.Items], [.. _host.Junctions], [.. _host.Types]),
                // A note the plant does not hold (its decoration is empty): the save would rewrite
                // the decoration, and that is no reason to keep the engine stopped.
                """<mxCell id="note-1" value="a note" style="text;html=1;" vertex="1" parent="1"><mxGeometry x="400" y="400" width="80" height="20" as="geometry"/></mxCell>"""));

        cut.Find("#drawio-engine-start").Click();
        cut.Find("#drawio-engine-confirm").Click();

        cut.WaitForAssertion(() => Assert.Equal((int)CommandTypes.SetNavigatorRun, Assert.Single(_host.Commands).CommandType));
        Assert.Single(await cut.InvokeAsync(() => bridge.Invocations["currentDrawing"].ToList()));
        cut.WaitForAssertion(() => Assert.Equal("Starting the engine...", cut.Find("#drawio-engine-pending").TextContent.Trim()));

        _host.NavStatus = NavigatorStatusTypes.Run;
        _source.RaiseNavigator(Engine(NavigatorStatusTypes.Run));

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll("#drawio-engine-pending"));
            Assert.Contains("Engine: Run", cut.Find("#drawio-engine-state").TextContent);
            Assert.Single(cut.FindAll("#drawio-engine-stop"));
        });
    }

    /// <summary>
    /// Queue item 13, fix round: disposing the drawing's stream after the read is a round trip that
    /// fails once the circuit has dropped. The drawing was read: the start goes on, and nothing is
    /// thrown out of the switch's callback, where it would take the circuit down.
    /// </summary>
    [Fact]
    public async Task ADisposeThatFailsAfterTheReadStillLetsTheEngineStart()
    {
        _host.Items = [Device(A, "A", 10)];
        var (cut, _) = await RenderWithDrawing(() => new TestJSStreamReference(
            System.Text.Encoding.UTF8.GetBytes(Wcs.WebEditor.Drawio.DrawioDocument.Build(
                new Wcs.WebEditor.Model.PlantGraph([.. _host.Items], [.. _host.Junctions], [.. _host.Types]), null)),
            disposeFailure: new JSDisconnectedException("the circuit is gone (test)")));

        cut.Find("#drawio-engine-start").Click();
        cut.Find("#drawio-engine-confirm").Click();

        cut.WaitForAssertion(() => Assert.Equal((int)CommandTypes.SetNavigatorRun, Assert.Single(_host.Commands).CommandType));
    }

    /// <summary>
    /// Queue item 13: a drawing too large for the page to take keeps the engine stopped, and the page
    /// says why - it cannot know whether the drawing holds unsaved work. Not a start on a guess, and
    /// not a circuit that drops with the drawing in one message.
    /// </summary>
    [Fact]
    public async Task ADrawingTooLargeToTakeKeepsTheEngineStopped()
    {
        _host.Items = [Device(A, "A", 10)];
        var (cut, _) = await RenderWithDrawing(
            () => new TestJSStreamReference([], declaredLength: Wcs.WebEditor.Drawio.DrawioDrawingTransfer.MaxBytes + 1));

        cut.Find("#drawio-engine-start").Click();
        cut.Find("#drawio-engine-confirm").Click();

        cut.WaitForAssertion(() =>
        {
            var error = cut.Find("#drawio-engine-error").TextContent;
            Assert.Contains("over the 16 MB", error, StringComparison.Ordinal);
            Assert.Contains("the engine was NOT started", error, StringComparison.Ordinal);
        });
        Assert.Empty(_host.Commands);
    }

    // Row 37 of the queue, extended to the whole page on 10/10: the check that keeps the engine
    // stopped says why it cannot know in the page's words, and what came back - the parser's, the
    // browser's or the host's own words - is behind the "?" of the engine's line, not in it.

    [Fact]
    public async Task ADrawingThatIsNotXmlKeepsTheEngineStoppedWithTheParsersWordsBehindTheLine()
    {
        _host.Items = [Device(A, "A", 10)];
        var (cut, _) = await RenderWithDrawing(() => "<not-closed");

        cut.Find("#drawio-engine-start").Click();
        cut.Find("#drawio-engine-confirm").Click();

        cut.WaitForAssertion(() => Assert.Equal(
            "It cannot be known whether the drawing holds unsaved work (the drawing that came back from draw.io is not readable as XML), so the engine was NOT started. Reload the page.",
            cut.Find("#drawio-engine-error").TextContent));
        Assert.StartsWith("XmlException: ", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-engine-error"));
        Assert.Empty(_host.Commands);
    }

    [Fact]
    public async Task ADrawingTheBrowserDoesNotHandOverKeepsTheEngineStoppedWithItsFailureBehindTheLine()
    {
        _host.Items = [Device(A, "A", 10)];
        _host.Types = [Automation()];
        _host.ItemStatuses = [Status(A, DeviceStatusTypes.Error)];
        var bridge = JSInterop.SetupModule("./_content/Wcs.WebEditor/drawio-bridge.js");
        bridge.Setup<IJSStreamReference?>("currentDrawing").SetException(new JSDisconnectedException("il circuito non c'e' piu' (prova)"));
        var cut = await RenderLive(A);
        cut.WaitForAssertion(() => cut.Find("#drawio-engine-start"));

        cut.Find("#drawio-engine-start").Click();
        cut.Find("#drawio-engine-confirm").Click();

        cut.WaitForAssertion(() => Assert.Equal(
            "It cannot be known whether the drawing holds unsaved work (the browser did not hand the drawing over, because the connection to the browser was lost), so the engine was NOT started. Reload the page.",
            cut.Find("#drawio-engine-error").TextContent));
        Assert.Equal("JSDisconnectedException: il circuito non c'e' piu' (prova)", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-engine-error"));
        Assert.Empty(_host.Commands);
    }

    [Fact]
    public async Task APlantThatCannotBeReadKeepsTheEngineStoppedWithTheHostsAnswerBehindTheLine()
    {
        _host.Items = [Device(A, "A", 10)];
        var (cut, _) = await RenderWithDrawing(() =>
            Wcs.WebEditor.Drawio.DrawioDocument.Build(
                new Wcs.WebEditor.Model.PlantGraph([.. _host.Items], [.. _host.Junctions], [.. _host.Types]), null));
        _host.FailRoute = "items";

        cut.Find("#drawio-engine-start").Click();
        cut.Find("#drawio-engine-confirm").Click();

        cut.WaitForAssertion(() => Assert.Equal(
            "It cannot be known whether the drawing holds unsaved work (the plant could not be read from the host because the host answered with an error), so the engine was NOT started. Reload the page.",
            cut.Find("#drawio-engine-error").TextContent));
        Assert.Equal("the host answered 500 with an empty body", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-engine-error"));
        Assert.Empty(_host.Commands);
    }

    /// <summary>
    /// Two devices, one of them deleted on the canvas and not saved. Two and not one: a drawing that
    /// comes back with NONE of the plant's cells is refused as a whole by the save ("contains none of
    /// the ... plant cells"), and "save first" would then be advice the save does not take.
    /// </summary>
    [Fact]
    public async Task ADeletedDeviceNotYetSavedHoldsTheEngineBack()
    {
        _host.Items = [Device(A, "A", 10), Device(B, "B", 200)];
        var (cut, _) = await RenderWithDrawing(() =>
            Wcs.WebEditor.Drawio.DrawioDocument.Build(
                new Wcs.WebEditor.Model.PlantGraph([.. _host.Items.Where(item => item.ItemId == A)], [], [.. _host.Types]), null));

        cut.Find("#drawio-engine-start").Click();
        cut.Find("#drawio-engine-confirm").Click();

        cut.WaitForAssertion(() => Assert.Contains("1 deleted device(s)", cut.Find("#drawio-engine-error").TextContent));
        Assert.Empty(_host.Commands);
    }

    /// <summary>
    /// Renders the page with NO live channel (it could not be opened) and <paramref name="drawing"/>
    /// as what draw.io hands over; waits until the plant document has gone to draw.io - the page
    /// records it as sent in the same dispatcher turn - and for the switch.
    /// </summary>
    private async Task<IRenderedComponent<DrawioEditor>> RenderWithoutLiveChannel(Func<string> drawing)
    {
        _host.Types = [Automation()];
        var bridge = JSInterop.SetupModule("./_content/Wcs.WebEditor/drawio-bridge.js");
        bridge.Setup<IJSStreamReference?>("currentDrawing").SetResult(new TestJSStreamReference(drawing()));
        var state = new EditorSessionState(new Uri("http://localhost:5580"), _host)
        {
            EventSourceForTesting = () => throw new InvalidOperationException("no channel (test)"),
        };
        state.UseKey("test-key");
        Services.AddSingleton(state);
        var cut = Render<DrawioEditor>();
        await cut.InvokeAsync(() => cut.Instance.OnDrawioReady());

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!await cut.InvokeAsync(() => bridge.Invocations["load"].Count > 0))
        {
            Assert.True(DateTime.UtcNow < deadline, "The plant never went to draw.io.");
            await Task.Delay(20);
        }

        cut.WaitForAssertion(() => cut.Find("#drawio-engine-start"));
        return cut;
    }

    /// <summary>
    /// No live channel: nobody tells the page that the engine started. The page reads the status
    /// again by itself, and "Starting the engine..." ends when the host says Run.
    /// </summary>
    [Fact]
    public async Task WithoutALiveChannelTheStartEndsWhenTheHostSaysRun()
    {
        _host.Items = [Device(A, "A", 10)];
        var cut = await RenderWithoutLiveChannel(() =>
            Wcs.WebEditor.Drawio.DrawioDocument.Build(new Wcs.WebEditor.Model.PlantGraph([.. _host.Items], [], [.. _host.Types]), null));

        cut.Find("#drawio-engine-start").Click();
        cut.Find("#drawio-engine-confirm").Click();
        cut.WaitForAssertion(() => Assert.Single(_host.Commands));
        _host.NavStatus = NavigatorStatusTypes.Run;

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll("#drawio-engine-pending"));
            Assert.Contains("Engine: Run", cut.Find("#drawio-engine-state").TextContent);
        }, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WithoutALiveChannelAFailedStartIsReadByItsId()
    {
        _host.Items = [Device(A, "A", 10)];
        _host.CommandReads[1] = new CommandDto(1, DateTime.UtcNow, (int)CommandTypes.SetNavigatorRun, null, null, null, (short)99,
            "the diagram is not loaded (test)", null, null, 0, null, null, 0m, null, 0m, null, null, null, true, 0);
        var cut = await RenderWithoutLiveChannel(() =>
            Wcs.WebEditor.Drawio.DrawioDocument.Build(new Wcs.WebEditor.Model.PlantGraph([.. _host.Items], [], [.. _host.Types]), null));

        cut.Find("#drawio-engine-start").Click();
        cut.Find("#drawio-engine-confirm").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll("#drawio-engine-pending"));
            Assert.Contains("the diagram is not loaded (test)", cut.Find("#drawio-engine-error").TextContent);
        }, TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// The page still shows Stop (the live event has not come yet), but the permit check's own read of
    /// the host says Run: the refusal says so, instead of "NOT started ... the engine is running".
    /// </summary>
    [Fact]
    public async Task AnEngineAlreadyRunningIsSaidAsSuch()
    {
        _host.Items = [Device(A, "A", 10)];
        var (cut, _) = await RenderWithDrawing(() =>
            Wcs.WebEditor.Drawio.DrawioDocument.Build(new Wcs.WebEditor.Model.PlantGraph([.. _host.Items], [], [.. _host.Types]), null));
        _host.NavStatus = NavigatorStatusTypes.Run;

        cut.Find("#drawio-engine-start").Click();
        cut.Find("#drawio-engine-confirm").Click();

        cut.WaitForAssertion(() => Assert.Equal("The engine is already running (the host says so): nothing was sent.",
            cut.Find("#drawio-engine-error").TextContent.Trim()));
        Assert.Empty(_host.Commands);
    }

    /// <summary>
    /// Confirmation review, M-C: the permit check's own read says Recover or Dispose. The start is
    /// refused (CanEdit), in words for a START - not the write's "write then".
    /// </summary>
    [Theory]
    [InlineData(NavigatorStatusTypes.Recover, "The engine was NOT started: it is recovering - loading the plant - and stops by itself when it has finished: start it then.")]
    [InlineData(NavigatorStatusTypes.Dispose, "The engine was NOT started: it is shutting down.")]
    public async Task AStartRefusedInRecoverOrDisposeIsWordedForTheStart(NavigatorStatusTypes state, string expected)
    {
        _host.Items = [Device(A, "A", 10)];
        var (cut, _) = await RenderWithDrawing(() =>
            Wcs.WebEditor.Drawio.DrawioDocument.Build(new Wcs.WebEditor.Model.PlantGraph([.. _host.Items], [], [.. _host.Types]), null));
        _host.NavStatus = state;

        cut.Find("#drawio-engine-start").Click();
        cut.Find("#drawio-engine-confirm").Click();

        cut.WaitForAssertion(() => Assert.Equal(expected, cut.Find("#drawio-engine-error").TextContent.Trim()));
        Assert.Empty(_host.Commands);
    }

    /// <summary>
    /// A drawing that holds none of the plant's cells is refused as a whole by the save: the switch
    /// says to reload, not to "save first" - a save would not take it.
    /// </summary>
    [Fact]
    public async Task ADrawingWithNoneOfThePlantsCellsAsksForAReload()
    {
        _host.Items = [Device(A, "A", 10)];
        var (cut, _) = await RenderWithDrawing(() =>
            Wcs.WebEditor.Drawio.DrawioDocument.Build(new Wcs.WebEditor.Model.PlantGraph([], [], [.. _host.Types]), null));

        cut.Find("#drawio-engine-start").Click();
        cut.Find("#drawio-engine-confirm").Click();

        cut.WaitForAssertion(() =>
        {
            var said = cut.Find("#drawio-engine-error").TextContent;
            Assert.Contains("none of the 1 plant cells", said);
            Assert.Contains("Reload the page", said);
            Assert.DoesNotContain("Save first", said);
        });
        Assert.Empty(_host.Commands);
    }

    /// <summary>
    /// The engine fails the start: its CommandChanged, on the page's live channel, ends "Starting the
    /// engine..." with the engine's own reason.
    /// </summary>
    [Fact]
    public async Task AStartTheEngineFailedIsSaidOnThePage()
    {
        _host.Items = [Device(A, "A", 10)];
        var (cut, _) = await RenderWithDrawing(() =>
            Wcs.WebEditor.Drawio.DrawioDocument.Build(
                new Wcs.WebEditor.Model.PlantGraph([.. _host.Items], [], [.. _host.Types]), null));

        cut.Find("#drawio-engine-start").Click();
        cut.Find("#drawio-engine-confirm").Click();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("#drawio-engine-pending")));

        _source.RaiseCommand(new CommandDto(1, DateTime.UtcNow, (int)CommandTypes.SetNavigatorRun, null, null, null, (short)99,
            "the diagram is not loaded (test)", null, null, 0, null, null, 0m, null, 0m, null, null, null, true, 0));

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll("#drawio-engine-pending"));
            Assert.Contains("the diagram is not loaded (test)", cut.Find("#drawio-engine-error").TextContent);
        });
    }

    private int EditPermitReleases() =>
        _host.Requests.Count(request => request.StartsWith("DELETE ") && request.EndsWith("/edit-lock"));

    /// <summary>Starts the engine from an untouched drawing of A, and waits for "Starting the engine...".</summary>
    private async Task<IRenderedComponent<DrawioEditor>> StartPendingAsync()
    {
        _host.Items = [Device(A, "A", 10)];
        var (cut, _) = await RenderWithDrawing(() =>
            Wcs.WebEditor.Drawio.DrawioDocument.Build(new Wcs.WebEditor.Model.PlantGraph([.. _host.Items], [], [.. _host.Types]), null));
        cut.Find("#drawio-engine-start").Click();
        cut.Find("#drawio-engine-confirm").Click();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("#drawio-engine-pending")));
        return cut;
    }

    private Task SaveMovedAsync(IRenderedComponent<DrawioEditor> cut) =>
        cut.InvokeAsync(() => cut.Instance.OnDrawioSaved(Wcs.WebEditor.Drawio.DrawioDocument.Build(
            new Wcs.WebEditor.Model.PlantGraph([Device(A, "A", 300)], [], [.. _host.Types]), null)));

    /// <summary>
    /// The engine reaches Run well after the POST (it opens the PLC connections first). While the start
    /// is pending, a save of this page - of a drawing moved after the unsaved-work check - is refused by
    /// the write gate and writes nothing: it cannot straddle the switch to Run.
    /// </summary>
    [Fact]
    public async Task WhileTheStartIsPendingASaveIsRefusedAndWritesNothing()
    {
        var cut = await StartPendingAsync();

        await SaveMovedAsync(cut);

        cut.WaitForAssertion(() => Assert.Equal(
            Wcs.WebEditor.Drawio.Coordination.DrawioWriteRefusal.ForDrawing(Wcs.WebEditor.Drawio.Coordination.DrawioPlantWriter.Engine),
            cut.Find("#drawio-save-error").TextContent.Trim()));
        Assert.Empty(_host.GeometryWrites);
        Assert.Empty(_host.Writes);
    }

    /// <summary>
    /// The start failed: the gate is free again and the save writes, and the permit the start took has
    /// been given back - other pages are not told for the rest of the minute that somebody is editing.
    /// </summary>
    [Fact]
    public async Task AfterAFailedStartTheSaveWritesAgainAndThePermitWasGivenBack()
    {
        var cut = await StartPendingAsync();
        Assert.Equal(0, EditPermitReleases());

        _source.RaiseCommand(new CommandDto(1, DateTime.UtcNow, (int)CommandTypes.SetNavigatorRun, null, null, null, (short)99,
            "the diagram is not loaded (test)", null, null, 0, null, null, 0m, null, 0m, null, null, null, true, 0));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#drawio-engine-pending")));
        cut.WaitForAssertion(() => Assert.Equal(1, EditPermitReleases()));

        await SaveMovedAsync(cut);

        cut.WaitForAssertion(() => Assert.Single(_host.GeometryWrites));
    }

    /// <summary>The engine runs: the start is over, and the permit it took is given back once.</summary>
    [Fact]
    public async Task OnceTheEngineRunsThePermitTheStartTookIsGivenBack()
    {
        var cut = await StartPendingAsync();
        Assert.Equal(0, EditPermitReleases());

        _host.NavStatus = NavigatorStatusTypes.Run;
        _source.RaiseNavigator(Engine(NavigatorStatusTypes.Run));

        cut.WaitForAssertion(() => Assert.Contains("Engine: Run", cut.Find("#drawio-engine-state").TextContent));
        cut.WaitForAssertion(() => Assert.Equal(1, EditPermitReleases()));
    }
}
