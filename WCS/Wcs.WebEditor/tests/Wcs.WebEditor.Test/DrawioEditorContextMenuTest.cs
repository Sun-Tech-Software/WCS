using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Live;
using Wcs.WebEditor.Pages;
using Wcs.WebEditor.Session;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

/// <summary>
/// Compito 5, row 63: our entries in draw.io's right-click menu on a device. The menu itself is
/// built by drawio-bridge.js, which bUnit does not run (the browser suite has DrawioContextMenuTest
/// for that): what is proven here is the page's half - what it tells the bridge about which cells
/// are devices and whether the engine runs, and what an entry the operator chose does, which is
/// always one of the properties panel's own actions.
/// </summary>
public sealed class DrawioEditorContextMenuTest : PlantCanvasBunitContext
{
    private const string BridgeModule = "./_content/Wcs.WebEditor/drawio-bridge.js";
    private const string LiveModule = "./_content/Wcs.WebEditor/drawio-live.js";
    private static readonly Guid A = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002");
    private static readonly TimeSpan HangCeiling = TimeSpan.FromSeconds(10);

    private readonly FakePlantEventSource _source = new();
    private readonly DrawioWriteHandler _host = new();
    private readonly BunitJSModuleInterop _bridge;

    /// <summary>Every setDeviceMenu the page made, as (running, devices, offline), recorded when the bridge is called.</summary>
    private readonly List<(bool Running, string[] Devices, string[] Offline)> _menus = [];
    private readonly List<string[]> _reveals = [];
    private TaskCompletionSource<(bool Running, string[] Devices, string[] Offline)>? _menuWanted;
    private Func<(bool Running, string[] Devices, string[] Offline), bool> _menuTest = _ => false;
    private readonly TaskCompletionSource<string[]> _revealed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public DrawioEditorContextMenuTest()
    {
        var live = JSInterop.SetupModule(LiveModule);
        live.Setup<string>("render", _ => true).SetResult("drawn");
        _bridge = JSInterop.SetupModule(BridgeModule);
        _bridge.SetupVoid("setDeviceMenu", call =>
        {
            var state = ((bool)call.Arguments[0]!, (string[])call.Arguments[1]!, (string[])call.Arguments[2]!);
            _menus.Add(state);
            if (_menuTest(state)) _menuWanted?.TrySetResult(state);
            return true;
        }).SetVoidResult();
        // The bridge answers with the target it found: the first of the list here, as for a panel
        // that has it on screen.
        foreach (var first in new[] { "drawio-property-panel", "drawio-cells-details", "drawio-mission-steps" })
        {
            _bridge.Setup<string?>("revealInPanel", call =>
            {
                if (call.Arguments[0] is not string[] targets || targets[0] != first) return false;
                _reveals.Add(targets);
                _revealed.TrySetResult(targets);
                return true;
            }).SetResult(first);
        }

        _host.Items =
        [
            Device(A, "A", 10),
            Device(B, "B", 200),
        ];
        _host.Configs[A] = new PlantItemConfigDto { ItemId = A, Name = "A", DeviceType = 0 };
        _host.Configs[B] = new PlantItemConfigDto { ItemId = B, Name = "B", DeviceType = 0 };
    }

    private static PlantItemDto Device(Guid id, string name, double x) =>
        new(id, name, DeviceTypes.Automation, null, null, null, default, default, 0, null, 0, 0, 0, 0, default!, x, 10, 100, 100, 0);

    private static ItemStatus Status(Guid id, DeviceStatusTypes status) =>
        new(id, "", "", "", "A", status, LuLoadStatusTypes.None, "", 0m, Guid.Empty, Guid.Empty);

    private static EngineStatus Engine(NavigatorStatusTypes status) =>
        new(status, DefragmentationTypes.None, "t", DateTime.UtcNow);

    private IRenderedComponent<DrawioEditor> Render()
    {
        var state = new EditorSessionState(new Uri("http://localhost:5580"), _host) { EventSourceForTesting = () => _source };
        state.UseKey("test-key");
        Services.AddSingleton(state);
        return Render<DrawioEditor>();
    }

    /// <summary>The first setDeviceMenu - already made, or still to come - that passes <paramref name="test"/>.</summary>
    private async Task<(bool Running, string[] Devices, string[] Offline)> MenuAsync(
        IRenderedComponent<DrawioEditor> cut, Func<(bool Running, string[] Devices, string[] Offline), bool> test)
    {
        var wanted = new TaskCompletionSource<(bool, string[], string[])>(TaskCreationOptions.RunContinuationsAsynchronously);
        await cut.InvokeAsync(() =>
        {
            _menuTest = test;
            _menuWanted = wanted;
            foreach (var made in _menus.Where(test)) wanted.TrySetResult(made);
        });
        return await wanted.Task.WaitAsync(HangCeiling);
    }

    private async Task<IRenderedComponent<DrawioEditor>> RenderWithASelectedDevice(Guid selected)
    {
        var cut = Render();
        await cut.InvokeAsync(() => cut.Instance.OnDrawioReady());
        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([selected.ToString()]));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#drawio-property-panel")));
        return cut;
    }

    private void RunTheEngine(IRenderedComponent<DrawioEditor> cut)
    {
        _host.NavStatus = NavigatorStatusTypes.Run;
        _source.RaiseNavigator(Engine(NavigatorStatusTypes.Run));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("#drawio-commands")));
    }

    [Fact]
    public async Task ThePageTellsTheBridgeWhichCellsAreDevicesAndThatTheEngineIsStopped()
    {
        var cut = Render();
        await cut.InvokeAsync(() => cut.Instance.OnDrawioReady());

        var menu = await MenuAsync(cut, state => state.Devices.Length > 0);

        Assert.False(menu.Running);
        Assert.Contains(A.ToString(), menu.Devices);
        Assert.Contains(B.ToString(), menu.Devices);
        Assert.Empty(menu.Offline);
    }

    /// <summary>
    /// The plant arrives - and the page renders it - while attach is still on its way: that render
    /// may not tell the bridge anything (nothing goes to the bridge before attach), so the page tells
    /// it once attach has gone through, without waiting for a render that an untouched page may never
    /// make.
    /// </summary>
    [Fact]
    public async Task APlantThatArrivedDuringAttachIsToldOnceAttachIsThrough()
    {
        // Held unanswered: attach is on its way for as long as this test says.
        var attach = _bridge.SetupVoid("attach", _ => true);
        _host.ItemStatusGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cut = Render();
        // The live snapshot is asked for only at the end of loading the plant.
        await _host.ItemStatusGateReached.Task.WaitAsync(HangCeiling);
        await cut.InvokeAsync(() => { });
        Assert.Single(await cut.InvokeAsync(() => attach.Invocations.ToList()));
        Assert.Empty(await cut.InvokeAsync(() => _menus.ToList()));

        await cut.InvokeAsync(() => attach.SetVoidResult());

        var menu = await MenuAsync(cut, state => state.Devices.Length > 0);
        Assert.Contains(A.ToString(), menu.Devices);
        _host.ItemStatusGate.SetResult();
    }

    /// <summary>
    /// Fix round 1: bringing the panel into view goes through the bridge too, so it waits for attach
    /// like every other call - an entry settled while attach is still on its way is brought into
    /// view once attach is through, not before.
    /// </summary>
    [Fact]
    public async Task ThePanelIsBroughtIntoViewThroughTheBridgeOnlyAfterAttach()
    {
        var attach = _bridge.SetupVoid("attach", _ => true);
        var cut = Render();
        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([A.ToString()]));
        // Settled: the configuration and the warehouse cells are in, which is when the reveal runs.
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#drawio-cells-details, #drawio-cells-none, #drawio-cells-error")));

        await cut.InvokeAsync(() => cut.Instance.OnDrawioMenuEntry(A.ToString(), "properties"));
        await cut.InvokeAsync(() => { });

        Assert.Single(await cut.InvokeAsync(() => attach.Invocations.ToList()));
        Assert.Empty(await cut.InvokeAsync(() => _reveals.ToList()));

        await cut.InvokeAsync(() => attach.SetVoidResult());

        Assert.Equal(["drawio-property-panel"], await _revealed.Task.WaitAsync(HangCeiling));
    }

    /// <summary>The same rule as the panel's commands: offered only with the engine running, as the page shows it.</summary>
    [Fact]
    public async Task TheBridgeIsToldWhenTheEngineRuns()
    {
        var cut = Render();
        await cut.InvokeAsync(() => cut.Instance.OnDrawioReady());
        await MenuAsync(cut, state => state.Devices.Length > 0 && !state.Running);

        _host.NavStatus = NavigatorStatusTypes.Run;
        _source.RaiseNavigator(Engine(NavigatorStatusTypes.Run));

        var running = await MenuAsync(cut, state => state.Running);
        Assert.Contains(A.ToString(), running.Devices);
    }

    /// <summary>An offline device is offered "Set Ready", as the panel's own button says it - and it follows the live status even when that device is not selected.</summary>
    [Fact]
    public async Task AnOffLineDeviceIsSaidToTheBridge()
    {
        _host.ItemStatuses = [Status(A, DeviceStatusTypes.Ready)];
        var cut = Render();
        await cut.InvokeAsync(() => cut.Instance.OnDrawioReady());
        await MenuAsync(cut, state => state.Devices.Length > 0);

        _source.RaiseItemStatus(Status(A, DeviceStatusTypes.OffLine));

        var menu = await MenuAsync(cut, state => state.Offline.Contains(A.ToString()));
        Assert.DoesNotContain(B.ToString(), menu.Offline);
    }

    [Fact]
    public async Task PropertiesStillBringsThePropertyPanelIntoView()
    {
        var cut = await RenderWithASelectedDevice(A);

        await cut.InvokeAsync(() => cut.Instance.OnDrawioMenuEntry(A.ToString(), "properties"));

        Assert.Equal(["drawio-property-panel"], await _revealed.Task.WaitAsync(HangCeiling));
        Assert.Empty(cut.FindAll("#drawio-menu-notice"));
    }

    /// <summary>
    /// Mission Steps and Linked Storage Cells are information windows of their own: the menu opens
    /// either one for the device it names, without putting their contents back in Properties. Both
    /// may stay open together, and leaving that device shuts them so they cannot silently show stale
    /// information under another selection.
    /// </summary>
    [Fact]
    public async Task TheTwoInformationEntriesOpenDedicatedWindowsForTheirDevice()
    {
        var cut = await RenderWithASelectedDevice(A);
        var steps = cut.Find("#drawio-mission-steps-window");
        var cells = cut.Find("#drawio-linked-storage-cells-window");
        Assert.True(steps.HasAttribute("hidden"));
        Assert.True(cells.HasAttribute("hidden"));

        await cut.InvokeAsync(() => cut.Instance.OnDrawioMenuEntry(A.ToString(), "mission-steps"));

        cut.WaitForAssertion(() => Assert.False(cut.Find("#drawio-mission-steps-window").HasAttribute("hidden")));
        Assert.Equal("Mission steps: A", cut.Find("#drawio-mission-steps-window").GetAttribute("aria-label"));
        Assert.Null(cut.Find("#drawio-mission-steps").Closest("#drawio-property-dock"));

        await cut.InvokeAsync(() => cut.Instance.OnDrawioMenuEntry(A.ToString(), "linked-storage-cells"));

        cut.WaitForAssertion(() => Assert.False(cut.Find("#drawio-linked-storage-cells-window").HasAttribute("hidden")));
        Assert.False(cut.Find("#drawio-mission-steps-window").HasAttribute("hidden"));
        Assert.Equal("Linked storage cells: A", cut.Find("#drawio-linked-storage-cells-window").GetAttribute("aria-label"));
        Assert.Null(cut.Find("#drawio-cells").Closest("#drawio-property-dock"));
        Assert.Empty(_reveals);

        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([B.ToString()]));

        cut.WaitForAssertion(() => Assert.True(cut.Find("#drawio-mission-steps-window").HasAttribute("hidden")));
        Assert.True(cut.Find("#drawio-linked-storage-cells-window").HasAttribute("hidden"));
    }

    /// <summary>
    /// Row 37 of the queue: the page's script throws instead of bringing the part into view. The
    /// line says so in words, and what the script threw is behind its "?", not in the sentence.
    /// </summary>
    [Fact]
    public async Task AScriptThatFailsToBringThePanelIntoViewIsSaidWithWhatItThrewBehindTheQuestionMark()
    {
        // Planned after the constructor's answer for the same target: the latest plan is the one taken.
        _bridge.Setup<string?>("revealInPanel", call => call.Arguments[0] is string[] targets && targets[0] == "drawio-property-panel")
            .SetException(new Microsoft.JSInterop.JSException("the panel is not there (test)"));
        var cut = await RenderWithASelectedDevice(A);

        await cut.InvokeAsync(() => cut.Instance.OnDrawioMenuEntry(A.ToString(), "properties"));

        cut.WaitForAssertion(() => Assert.Equal(
            "The properties of A could not be brought into view because the page's script failed: they are in the column on the right.",
            cut.Find("#drawio-menu-notice").TextContent));
        Assert.Equal("JSException: the panel is not there (test)", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-menu-notice"));
    }

    /// <summary>
    /// Final review, M7: the call to the page's script ran out of time, or the connection to the browser
    /// went - the script never answered, and "the page's script failed" was false for both. The line says
    /// which, and what came back is behind its "?".
    /// </summary>
    [Theory]
    [InlineData("timeout", "the page's script did not answer in time", "TaskCanceledException: the call ran out of time (test)")]
    [InlineData("disconnected", "the connection to the browser was lost", "JSDisconnectedException: the circuit is gone (test)")]
    public async Task AScriptThatNeverAnsweredIsNotSaidToHaveFailed(string failure, string why, string detail)
    {
        Exception thrown = failure == "timeout"
            ? new TaskCanceledException("the call ran out of time (test)")
            : new Microsoft.JSInterop.JSDisconnectedException("the circuit is gone (test)");
        _bridge.Setup<string?>("revealInPanel", call => call.Arguments[0] is string[] targets && targets[0] == "drawio-property-panel")
            .SetException(thrown);
        var cut = await RenderWithASelectedDevice(A);

        await cut.InvokeAsync(() => cut.Instance.OnDrawioMenuEntry(A.ToString(), "properties"));

        cut.WaitForAssertion(() => Assert.Equal(
            $"The properties of A could not be brought into view because {why}: they are in the column on the right.",
            cut.Find("#drawio-menu-notice").TextContent));
        Assert.Equal(detail, DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-menu-notice"));
    }

    [Theory]
    [InlineData("execute-step", CommandTypes.ItemExecuteStep, "Execute Step")]
    [InlineData("unload-lu", CommandTypes.ItemUnloadLu, "Unload LU")]
    [InlineData("set-off-line", CommandTypes.ItemSetOffLine, "Set Off Line")]
    [InlineData("set-ready", CommandTypes.ItemSetOnLine, "Set Ready")]
    [InlineData("step-done", CommandTypes.ItemStepDone, "Step Done")]
    public async Task ACommandEntryIsSentByThePanelsOwnCommands(string entry, CommandTypes expected, string label)
    {
        var cut = await RenderWithASelectedDevice(A);
        RunTheEngine(cut);

        await cut.InvokeAsync(() => cut.Instance.OnDrawioMenuEntry(A.ToString(), entry));

        cut.WaitForAssertion(() =>
        {
            var sent = Assert.Single(_host.Commands);
            Assert.Equal((int)expected, sent.CommandType);
            Assert.Equal(A, sent.ItemId);
            // The panel's own outcome line: the same follower as its buttons.
            Assert.Contains($"{label} queued for A as command 1", cut.Find("#drawio-command-outcome").TextContent);
        });
    }

    /// <summary>
    /// The request is taken once: a later render of the same panel - another device selected and
    /// then this one again, which is a new commands component for it - does not send it again.
    /// </summary>
    [Fact]
    public async Task ACommandFromTheMenuIsSentOnceOnly()
    {
        var cut = await RenderWithASelectedDevice(A);
        RunTheEngine(cut);
        await cut.InvokeAsync(() => cut.Instance.OnDrawioMenuEntry(A.ToString(), "execute-step"));
        cut.WaitForAssertion(() => Assert.Contains("command 1", cut.Find("#drawio-command-outcome").TextContent));

        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([B.ToString()]));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#drawio-command-outcome")));
        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([A.ToString()]));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("#drawio-commands")));

        Assert.Single(_host.Commands);
    }

    /// <summary>
    /// Fix round 1: a command chosen from the menu and not yet taken by the panel when the engine
    /// stops must not wait for the next Run - the panel's commands component for that device would
    /// be born again then and send it, long after the operator chose it. ForgetTheMenuCommandUnlessRunning
    /// is the only guard for that window.
    /// </summary>
    /// <remarks>
    /// <b>How the window is staged, and why so.</b> In a real page it opens only when the render that
    /// follows the menu entry is deferred (Blazor Server's RemoteRenderer holds renders back while the
    /// browser has not acknowledged enough batches); bUnit renders inside the same call, so
    /// <c>OnDrawioMenuEntry</c> cannot leave the request untaken. The state that deferral produces is
    /// therefore set directly - the request in the page's field, with no render - and the engine's
    /// Stop is raised in the SAME dispatcher call: the feed raises StatusChanged synchronously and the
    /// page's handler runs inline on the dispatcher, so no render can slip in between and take the
    /// request.
    /// </remarks>
    [Fact]
    public async Task ACommandNotYetTakenWhenTheEngineStopsIsNotSentAtTheNextRun()
    {
        var cut = await RenderWithASelectedDevice(A);
        RunTheEngine(cut);
        var pending = typeof(DrawioEditor).GetField("_menuCommand",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var request = new Wcs.WebEditor.Drawio.Coordination.DrawioMenuCommand(A, Wcs.WebEditor.Drawio.Coordination.DrawioDeviceCommand.ExecuteStep);

        _host.NavStatus = NavigatorStatusTypes.Stop;
        await cut.InvokeAsync(() =>
        {
            pending.SetValue(cut.Instance, request);
            _source.RaiseNavigator(Engine(NavigatorStatusTypes.Stop));
        });
        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll("#drawio-commands"));
            Assert.Equal("Execute Step was NOT sent to A: the engine stopped running before the panel could send it.",
                cut.Find("#drawio-menu-notice").TextContent.Trim());
        });
        Assert.False(request.Taken);

        RunTheEngine(cut);

        Assert.False(request.Taken);
        Assert.Empty(_host.Commands);
    }

    /// <summary>The engine stopped between the menu and the click: nothing is sent, and the page says so.</summary>
    [Fact]
    public async Task ACommandEntryWithTheEngineStoppedSendsNothing()
    {
        var cut = await RenderWithASelectedDevice(A);

        await cut.InvokeAsync(() => cut.Instance.OnDrawioMenuEntry(A.ToString(), "execute-step"));

        cut.WaitForAssertion(() =>
            Assert.Equal("Execute Step was NOT sent to A: the engine is not running, and the device commands are offered only while it runs.",
                cut.Find("#drawio-menu-notice").TextContent.Trim()));
        Assert.Empty(_host.Commands);
    }

    /// <summary>A cell the page does not know as a device - a note, an arrow - gets nothing done, and the page says so.</summary>
    [Fact]
    public async Task AnEntryOnACellThatIsNotADeviceDoesNothing()
    {
        var cut = await RenderWithASelectedDevice(A);
        RunTheEngine(cut);

        await cut.InvokeAsync(() => cut.Instance.OnDrawioMenuEntry("note-1", "execute-step"));
        await cut.InvokeAsync(() => cut.Instance.OnDrawioMenuEntry("note-1", "properties"));

        cut.WaitForAssertion(() => Assert.Contains("is not a device of the plant", cut.Find("#drawio-menu-notice").TextContent));
        Assert.Empty(_host.Commands);
        Assert.Empty(_reveals);
    }

    /// <summary>
    /// The entry arrives for a device the panel is not showing (the selection that the menu made
    /// never reached the page): nothing is sent to a device the operator is not looking at.
    /// </summary>
    [Fact]
    public async Task ACommandForADeviceThePanelIsNotShowingIsNotSent()
    {
        var cut = await RenderWithASelectedDevice(A);
        RunTheEngine(cut);

        await cut.InvokeAsync(() => cut.Instance.OnDrawioMenuEntry(B.ToString(), "execute-step"));

        cut.WaitForAssertion(() => Assert.Contains("NOT sent to B", cut.Find("#drawio-menu-notice").TextContent));
        Assert.Empty(_host.Commands);
    }

    /// <summary>
    /// Row 8, the owner's decision of 27/9: the bridge refused a cut of a device (drawio-bridge.js,
    /// guardTheCut) and the page says so in plain words, naming the device, until the selection moves.
    /// </summary>
    [Fact]
    public async Task ARefusedCutIsSaidOnThePageUntilTheSelectionMoves()
    {
        var cut = await RenderWithASelectedDevice(A);

        await cut.InvokeAsync(() => cut.Instance.OnDrawioCutRefused([A.ToString()]));

        cut.WaitForAssertion(() => Assert.Equal(
            "A plant device cannot be cut: drag it to move it, or delete it with Delete. Nothing was cut (A).",
            cut.Find("#drawio-cut-refused").TextContent.Trim()));

        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([B.ToString()]));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#drawio-cut-refused")));
    }

    /// <summary>
    /// Last cleanup: the bridge also refuses a cell that only carries the device mark - a shape the
    /// plant does not know (a refused drop, an orphan). It is not "a plant device", and the line says
    /// what it is.
    /// </summary>
    [Fact]
    public async Task ARefusedCutOfAMarkedShapeThePlantDoesNotKnowSaysWhatItIs()
    {
        var cut = await RenderWithASelectedDevice(A);

        await cut.InvokeAsync(() => cut.Instance.OnDrawioCutRefused(["orphan-cell"]));

        cut.WaitForAssertion(() => Assert.Equal(
            "A device shape cannot be cut: it is marked as a device, and the plant does not know it - delete it with Delete, or point it at a device from the properties column. Nothing was cut (orphan-cell).",
            cut.Find("#drawio-cut-refused").TextContent.Trim()));
    }
}
