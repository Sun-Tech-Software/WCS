using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Pages;
using Wcs.WebEditor.Session;
using Xunit;

namespace Wcs.WebEditor.Test;

/// <summary>
/// Queue item 14: a call the browser sends while the circuit's connection is down is lost - Blazor
/// neither sends it again nor settles it - and the page came back alive without saying so. Now every
/// drop the circuit comes back from gets a line, and a save whose call the drop took gets its own: the
/// bridge still holds its number (<c>savesHandedOver</c>), and the page never received it. The drop is
/// driven through the session's real circuit handler (<see cref="EditorCircuitConnection"/>), down and
/// then up, as Blazor calls it; the browser test <c>DrawioReconnectionTest</c> drops a real connection.
/// </summary>
public sealed class DrawioEditorReconnectionTest : PlantCanvasBunitContext
{
    private const string BridgePath = "./_content/Wcs.WebEditor/drawio-bridge.js";

    private const string SaveLine = "The connection dropped during the save; nothing was written, press Save again.";

    private const string DropLine =
        "The connection to the host dropped and came back: what you did in that moment may not have reached the page.";

    private static readonly Guid A = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private EditorSessionState _state = null!;
    private BunitJSModuleInterop _bridge = null!;
    private EditorCircuitConnection? _connection;

    [Fact]
    public async Task ADropThatTookASaveSaysSoAndThatItDropped()
    {
        var cut = await RenderAsync();
        _bridge.Setup<long[]>("savesHandedOver").SetResult([4L]);

        await DropAndComeBackAsync();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(SaveLine, cut.Find("#drawio-save-lost-notice-text").TextContent);
            Assert.Equal(DropLine, cut.Find("#drawio-reconnected-notice-text").TextContent);
        });
        // Task 12: a save that never reached the page wrote nothing, and is not said as one that may have.
        Assert.Empty(cut.FindAll("#drawio-save-unknown-notice-text"));
        // Final review, C18: nobody will ask for that drawing again, and the bridge is told to let go.
        Assert.Equal([4L], (long[])Assert.Single(_bridge.Invocations["letGoOfSaves"]).Arguments[0]!);
    }

    [Fact]
    public async Task ADropWithNoSaveInItSaysOnlyThatItDropped()
    {
        var cut = await RenderAsync();
        _bridge.Setup<long[]>("savesHandedOver").SetResult([]);

        await DropAndComeBackAsync();

        cut.WaitForAssertion(() => Assert.Equal(DropLine, cut.Find("#drawio-reconnected-notice-text").TextContent));
        // The bridge WAS asked: no save line because it holds nothing, not because nobody looked.
        Assert.Single(_bridge.Invocations["savesHandedOver"]);
        Assert.Empty(cut.FindAll("#drawio-save-lost-notice-text"));
    }

    [Fact]
    public async Task WithoutADropThereIsNoLine()
    {
        var cut = await RenderAsync();
        _bridge.Setup<long[]>("savesHandedOver").SetResult([4L]);

        // Blazor calls the handler's up at the circuit's first connection too: that is no drop.
        await new EditorCircuitConnection(_state).OnConnectionUpAsync(null!, CancellationToken.None);
        cut.Render();

        Assert.Empty(_bridge.Invocations["savesHandedOver"]);
        Assert.Empty(cut.FindAll("#drawio-reconnected-notice-text"));
        Assert.Empty(cut.FindAll("#drawio-save-lost-notice-text"));
    }

    /// <summary>
    /// A save whose call reached the page before the drop is not lost: its round runs (here it is still
    /// taking the drawing) and says by itself what it wrote. The bridge holds its number all the same.
    /// </summary>
    [Fact]
    public async Task ASaveThatReachedThePageIsNotSaidLost()
    {
        var cut = await RenderAsync();
        _bridge.Setup<IJSStreamReference?>("handedOverDrawing", _ => true);
        _bridge.Setup<long[]>("savesHandedOver").SetResult([4L]);
        var round = cut.InvokeAsync(() => cut.Instance.OnDrawioHandedTheDrawingOver(4));

        await DropAndComeBackAsync();

        cut.WaitForAssertion(() => Assert.Equal(DropLine, cut.Find("#drawio-reconnected-notice-text").TextContent));
        Assert.Single(_bridge.Invocations["savesHandedOver"]);
        Assert.Empty(cut.FindAll("#drawio-save-lost-notice-text"));
        Assert.False(round.IsCompleted);
        // Final review, C18: its round is still taking the drawing, which the bridge keeps.
        Assert.Empty(_bridge.Invocations["letGoOfSaves"]);
    }

    /// <summary>
    /// Final review, C18: a save whose round has ended, and whose answer the drop lost on its way back to
    /// the bridge - its call there never settles, so the bridge still holds its number. The page will
    /// not ask for that drawing again, and the bridge is told to let go of it; the save is not said lost.
    /// </summary>
    [Fact]
    public async Task ASaveWhoseRoundIsOverIsLetGoOfAtTheNextDrop()
    {
        var cut = await RenderAsync();
        _bridge.Setup<IJSStreamReference?>("handedOverDrawing", _ => true).SetResult(new TestJSStreamReference([]));
        await cut.InvokeAsync(() => cut.Instance.OnDrawioHandedTheDrawingOver(4));
        _bridge.Setup<long[]>("savesHandedOver").SetResult([4L]);

        await DropAndComeBackAsync();

        cut.WaitForAssertion(() => Assert.Equal(DropLine, cut.Find("#drawio-reconnected-notice-text").TextContent));
        Assert.Empty(cut.FindAll("#drawio-save-lost-notice-text"));
        Assert.Equal([4L], (long[])Assert.Single(_bridge.Invocations["letGoOfSaves"]).Arguments[0]!);
    }

    /// <summary>Each drop is a new outcome: closed with its X, the line comes back at the next one.</summary>
    [Fact]
    public async Task EveryDropIsSaidAgain()
    {
        var cut = await RenderAsync();
        _bridge.Setup<long[]>("savesHandedOver").SetResult([]);
        await DropAndComeBackAsync();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("#drawio-reconnected-notice-text")));

        cut.Find("#drawio-reconnected-dismiss").Click();
        Assert.Empty(cut.FindAll("#drawio-reconnected-notice-text"));

        await DropAndComeBackAsync();
        cut.WaitForAssertion(() => Assert.Equal(DropLine, cut.Find("#drawio-reconnected-notice-text").TextContent));
    }

    /// <summary>The save's line says "press Save again": a save that reaches the page takes it away.</summary>
    [Fact]
    public async Task TheNextSaveTakesTheSaveLineAway()
    {
        var cut = await RenderAsync();
        _bridge.Setup<IJSStreamReference?>("handedOverDrawing", _ => true);
        _bridge.Setup<long[]>("savesHandedOver").SetResult([4L]);
        await DropAndComeBackAsync();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("#drawio-save-lost-notice-text")));

        _ = cut.InvokeAsync(() => cut.Instance.OnDrawioHandedTheDrawingOver(5));

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#drawio-save-lost-notice-text")));
        Assert.Single(cut.FindAll("#drawio-reconnected-notice-text"));
    }

    /// <summary>
    /// A lost save's call never settles. Said at the drop that took it; a later drop with no save in it
    /// is a drop, not that save lost again.
    /// </summary>
    [Fact]
    public async Task ASaveLostInAnEarlierDropIsNotSaidLostAgain()
    {
        var cut = await RenderAsync();
        _bridge.Setup<IJSStreamReference?>("handedOverDrawing", _ => true);
        _bridge.Setup<long[]>("savesHandedOver").SetResult([4L]);
        await DropAndComeBackAsync();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("#drawio-save-lost-notice-text")));
        _ = cut.InvokeAsync(() => cut.Instance.OnDrawioHandedTheDrawingOver(5));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#drawio-save-lost-notice-text")));
        // The first drop's line closed: its coming back is the second drop said, after the bridge was asked.
        cut.Find("#drawio-reconnected-dismiss").Click();

        await DropAndComeBackAsync();

        cut.WaitForAssertion(() => Assert.Equal(DropLine, cut.Find("#drawio-reconnected-notice-text").TextContent));
        Assert.Equal(2, _bridge.Invocations["savesHandedOver"].Count);
        Assert.Empty(cut.FindAll("#drawio-save-lost-notice-text"));
    }

    /// <summary>
    /// Task 12: a save that reached the page and whose round ended, with its answer lost on the way back
    /// to the browser - the bridge still holds its number when the connection comes back. The round
    /// went to the host: what it wrote is not known from the browser, and the page says so.
    /// </summary>
    [Fact]
    public async Task ASaveWhoseAnswerTheDropTookMayHaveBeenWritten()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderAsync(handler);
        _bridge.Setup<IJSStreamReference?>("handedOverDrawing", _ => true).SetResult(new TestJSStreamReference(MovedDrawing()));
        Assert.True(await cut.InvokeAsync(() => cut.Instance.OnDrawioHandedTheDrawingOver(7)));
        Assert.Equal(A, Assert.Single(handler.GeometryWrites).ItemId);
        _bridge.Setup<long[]>("savesHandedOver").SetResult([7L]);

        await DropAndComeBackAsync();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(MayHaveBeenWrittenLine, cut.Find("#drawio-save-unknown-notice-text").TextContent);
            Assert.Equal(DropLine, cut.Find("#drawio-reconnected-notice-text").TextContent);
        });
        Assert.Empty(cut.FindAll("#drawio-save-lost-notice-text"));
    }

    /// <summary>
    /// Final fix round, C1: a save whose round is still running when the connection comes back is not an
    /// unknown outcome. Its answer reaches the bridge on the connection that came back, and its report
    /// says what it wrote: no "may have been written" line, and a round that wrote everything says so.
    /// </summary>
    [Fact]
    public async Task ASaveStillRunningWhenTheConnectionCameBackIsNotSaidAndAnswersWhatItWrote()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderAsync(handler);
        var drawing = _bridge.Setup<IJSStreamReference?>("handedOverDrawing", _ => true);
        _bridge.Setup<long[]>("savesHandedOver").SetResult([7L]);
        var round = cut.InvokeAsync(() => cut.Instance.OnDrawioHandedTheDrawingOver(7));

        await DropAndComeBackAsync();
        cut.WaitForAssertion(() => Assert.Equal(DropLine, cut.Find("#drawio-reconnected-notice-text").TextContent));
        Assert.Single(_bridge.Invocations["savesHandedOver"]);
        Assert.False(round.IsCompleted);

        drawing.SetResult(new TestJSStreamReference(MovedDrawing()));

        Assert.True(await round);
        Assert.Equal(A, Assert.Single(handler.GeometryWrites).ItemId);
        Assert.Contains("Save succeeded", cut.Find("#drawio-save-summary").TextContent);
        Assert.Empty(cut.FindAll("#drawio-save-unknown-notice-text"));
    }

    /// <summary>
    /// Final fix round, C1: a round that ends while the page is asking the bridge what it holds ends on
    /// the connection that came back, and its answer gets there: it is not said as one that may have
    /// been written, even though the bridge still listed it.
    /// </summary>
    [Fact]
    public async Task ASaveThatEndsWhileTheBridgeIsAskedIsNotSaid()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderAsync(handler);
        var drawing = _bridge.Setup<IJSStreamReference?>("handedOverDrawing", _ => true);
        var asked = _bridge.Setup<long[]>("savesHandedOver");
        var round = cut.InvokeAsync(() => cut.Instance.OnDrawioHandedTheDrawingOver(7));

        await DropAndComeBackAsync();
        cut.WaitForAssertion(() => Assert.Single(_bridge.Invocations["savesHandedOver"]));
        drawing.SetResult(new TestJSStreamReference(MovedDrawing()));
        Assert.True(await round);
        asked.SetResult([7L]);

        cut.WaitForAssertion(() => Assert.Equal(DropLine, cut.Find("#drawio-reconnected-notice-text").TextContent));
        Assert.Empty(cut.FindAll("#drawio-save-unknown-notice-text"));
    }

    /// <summary>
    /// Final fix round, C1: a round the page knows wrote nothing - here its drawing did not arrive - and
    /// whose answer the drop took is no save that may have been written: no line says so, and nothing
    /// was written on the host.
    /// </summary>
    [Fact]
    public async Task ASaveThatWroteNothingIsNotSaidAsMayHaveBeenWritten()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderAsync(handler);
        _bridge.Setup<IJSStreamReference?>("handedOverDrawing", _ => true).SetResult(null);
        Assert.False(await cut.InvokeAsync(() => cut.Instance.OnDrawioHandedTheDrawingOver(7)));
        _bridge.Setup<long[]>("savesHandedOver").SetResult([7L]);

        await DropAndComeBackAsync();

        cut.WaitForAssertion(() => Assert.Equal(DropLine, cut.Find("#drawio-reconnected-notice-text").TextContent));
        Assert.Empty(handler.GeometryWrites);
        Assert.Empty(cut.FindAll("#drawio-save-unknown-notice-text"));
        Assert.Empty(cut.FindAll("#drawio-save-lost-notice-text"));
        // Its round is over: the bridge is told to let go of it all the same.
        Assert.Equal([7L], (long[])Assert.Single(_bridge.Invocations["letGoOfSaves"]).Arguments[0]!);
    }

    /// <summary>Task 12: said at the drop that crossed the save, and not again at a later drop that finds the bridge still holding it.</summary>
    [Fact]
    public async Task ASaveThatMayHaveBeenWrittenIsSaidOnce()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderAsync(handler);
        _bridge.Setup<IJSStreamReference?>("handedOverDrawing", _ => true).SetResult(new TestJSStreamReference(MovedDrawing()));
        await cut.InvokeAsync(() => cut.Instance.OnDrawioHandedTheDrawingOver(7));
        _bridge.Setup<long[]>("savesHandedOver").SetResult([7L]);
        await DropAndComeBackAsync();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("#drawio-save-unknown-notice-text")));
        cut.Find("#drawio-save-unknown-dismiss").Click();
        cut.Find("#drawio-reconnected-dismiss").Click();

        await DropAndComeBackAsync();

        cut.WaitForAssertion(() => Assert.Equal(DropLine, cut.Find("#drawio-reconnected-notice-text").TextContent));
        Assert.Equal(2, _bridge.Invocations["savesHandedOver"].Count);
        Assert.Empty(cut.FindAll("#drawio-save-unknown-notice-text"));
    }

    /// <summary>Task 12: a save that reaches the page after the line makes it stale, as it does the line of a lost save.</summary>
    [Fact]
    public async Task TheNextSaveTakesTheMayHaveBeenWrittenLineAway()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderAsync(handler);
        _bridge.Setup<IJSStreamReference?>("handedOverDrawing", _ => true).SetResult(new TestJSStreamReference(MovedDrawing()));
        await cut.InvokeAsync(() => cut.Instance.OnDrawioHandedTheDrawingOver(7));
        _bridge.Setup<long[]>("savesHandedOver").SetResult([7L]);
        await DropAndComeBackAsync();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("#drawio-save-unknown-notice-text")));

        await cut.InvokeAsync(() => cut.Instance.OnDrawioHandedTheDrawingOver(8));

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#drawio-save-unknown-notice-text")));
        Assert.Single(cut.FindAll("#drawio-reconnected-notice-text"));
    }

    private const string MayHaveBeenWrittenLine =
        "The connection dropped while the plant was being saved: the save may have been written. Reopen the plant to see what it holds.";

    private static readonly Guid B = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static DeviceTypeDto TwoPortType() => new("automation", 0, "Automation",
        [new ShapePartDto("M 4 4 H 96 V 96 H 4 Z", "#334155", Filled: false, Rigid: false)],
        [new DevicePortDto("in", 4, 50, "in"), new DevicePortDto("out", 96, 50, "out")],
        new ShapePointDto(50, 50), new ShapePointDto(88, 60));

    private static PlantItemDto Item(Guid id, double x, double y) =>
        new(id, id.ToString()[..4], Wcs.Common.Enums.DeviceTypes.Automation, null, null, null, default, default, 0, null,
            0, 0, 0, 0, default!, x, y, 130, 184, 0)
        {
            GeometryVersion = id,
        };

    /// <summary>The host of <c>DrawioEditorLargeDrawingTest</c>: two devices, on which a save that moved one writes everything.</summary>
    private static DrawioWriteHandler HostWithTwoDevices()
    {
        var handler = new DrawioWriteHandler { Items = [Item(A, 10, 20), Item(B, 500, 20)], Types = [TwoPortType()] };
        handler.Configs[A] = new PlantItemConfigDto { ItemId = A, Name = "1111", DeviceType = 0 };
        handler.Configs[B] = new PlantItemConfigDto { ItemId = B, Name = "2222", DeviceType = 0 };
        return handler;
    }

    /// <summary>The drawing draw.io hands over after device A was moved.</summary>
    private static byte[] MovedDrawing() => System.Text.Encoding.UTF8.GetBytes(
        Wcs.WebEditor.Drawio.DrawioDocument.Build(
            new Wcs.WebEditor.Model.PlantGraph([Item(A, 99, 77), Item(B, 500, 20)], [], [TwoPortType()]), null));

    private async Task<IRenderedComponent<DrawioEditor>> RenderAsync(DrawioWriteHandler? host = null)
    {
        var handler = host ?? new DrawioWriteHandler
        {
            Items =
            [
                new PlantItemDto(A, "1111", Wcs.Common.Enums.DeviceTypes.Automation, null, null, null, default, default, 0, null,
                    0, 0, 0, 0, default!, 10, 20, 130, 184, 0),
            ],
            Types =
            [
                new DeviceTypeDto("automation", 0, "Automation", [new ShapePartDto("M 4 4 H 96 V 96 H 4 Z", "#334155", Filled: false, Rigid: false)],
                    [], new ShapePointDto(50, 50), new ShapePointDto(88, 60)),
            ],
        };
        _bridge = JSInterop.SetupModule(BridgePath);
        _state = new EditorSessionState(new Uri("http://localhost:5580"), handler);
        _state.UseKey("test-key");
        Services.AddSingleton(_state);
        var cut = Render<DrawioEditor>();
        await cut.InvokeAsync(() => cut.Instance.OnDrawioReady());
        return cut;
    }

    /// <summary>The circuit's connection drops and comes back, told as Blazor tells it: down, then up.</summary>
    private async Task DropAndComeBackAsync()
    {
        _connection ??= new EditorCircuitConnection(_state);
        await _connection.OnConnectionDownAsync(null!, CancellationToken.None);
        await _connection.OnConnectionUpAsync(null!, CancellationToken.None);
    }
}
