using System.Reflection;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Drawio;
using Wcs.WebEditor.Model;
using Wcs.WebEditor.Pages;
using Wcs.WebEditor.Session;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

/// <summary>
/// Queue item 13: the save as the bridge makes it since the drawing stopped crossing the circuit in
/// one message - a number, and the page taking the drawing under it as a stream
/// (<see cref="DrawioDrawingTransfer"/>). The browser test <c>DrawioLargeDrawingTest</c> measures it
/// on a real plant; here, what the page does with what the stream brings, and with what it cannot.
/// </summary>
public sealed class DrawioEditorLargeDrawingTest : PlantCanvasBunitContext
{
    private const string BridgePath = "./_content/Wcs.WebEditor/drawio-bridge.js";

    private static readonly Guid A = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid B = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static DeviceTypeDto Type() => new("automation", 0, "Automation", [new ShapePartDto("M 4 4 H 96 V 96 H 4 Z", "#334155", Filled: false, Rigid: false)],
        [new DevicePortDto("in", 4, 50, "in"), new DevicePortDto("out", 96, 50, "out")],
        new ShapePointDto(50, 50), new ShapePointDto(88, 60));

    private static PlantItemDto Item(Guid id, double x, double y) =>
        new(id, id.ToString()[..4], DeviceTypes.Automation, null, null, null, default, default, 0, null,
            0, 0, 0, 0, default!, x, y, 130, 184, 0)
        {
            GeometryVersion = id
        };

    private static DrawioWriteHandler HostWithTwoDevices()
    {
        var handler = new DrawioWriteHandler { Items = [Item(A, 10, 20), Item(B, 500, 20)], Types = [Type()] };
        handler.Configs[A] = new PlantItemConfigDto { ItemId = A, Name = "1111", DeviceType = 0 };
        handler.Configs[B] = new PlantItemConfigDto { ItemId = B, Name = "2222", DeviceType = 0 };
        return handler;
    }

    /// <summary>The bridge calls <c>OnDrawioSaved</c> with the drawing's NUMBER: exactly one door answers to that name, and it takes a number.</summary>
    [Fact]
    public void TheBridgesOnDrawioSavedTakesTheDrawingsNumber()
    {
        var door = Assert.Single(typeof(DrawioEditor).GetMethods(),
            method => method.GetCustomAttribute<JSInvokableAttribute>() is { } invokable
                      && (invokable.Identifier ?? method.Name) == "OnDrawioSaved");

        Assert.Equal([typeof(long)], door.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.Equal(typeof(Task<bool>), door.ReturnType);
    }

    [Fact]
    public async Task ADrawingTakenAsAStreamIsSaved()
    {
        var handler = HostWithTwoDevices();
        var bridge = JSInterop.SetupModule(BridgePath);
        var moved = DrawioDocument.Build(new PlantGraph([Item(A, 99, 77), Item(B, 500, 20)], [], [Type()]), null);
        var stream = new TestJSStreamReference(moved);
        bridge.Setup<IJSStreamReference?>("handedOverDrawing", _ => true).SetResult(stream);
        var cut = await RenderWithAsync(handler);

        Assert.True(await cut.InvokeAsync(() => cut.Instance.OnDrawioHandedTheDrawingOver(7)));

        Assert.Equal(new object?[] { 7L }, Assert.Single(bridge.Invocations["handedOverDrawing"]).Arguments);
        Assert.True(stream.Disposed);
        var written = Assert.Single(handler.GeometryWrites);
        Assert.Equal(A, written.ItemId);
        Assert.Contains("Save succeeded", cut.Find("#drawio-save-summary").TextContent);
    }

    /// <summary>
    /// A drawing over the page's ceiling is not taken: the save round ends at once with the reason on
    /// the page, nothing written, the round counted, and the answer false - "Leave site?" goes on asking.
    /// </summary>
    [Fact]
    public async Task ADrawingOverThePagesCeilingIsRefusedWithTheReasonOnThePage()
    {
        var handler = HostWithTwoDevices();
        var bridge = JSInterop.SetupModule(BridgePath);
        var stream = new TestJSStreamReference([], declaredLength: DrawioDrawingTransfer.MaxBytes + 1);
        bridge.Setup<IJSStreamReference?>("handedOverDrawing", _ => true).SetResult(stream);
        var cut = await RenderWithAsync(handler);
        var round = cut.Find("#drawio-save-round").TextContent;

        Assert.False(await cut.InvokeAsync(() => cut.Instance.OnDrawioHandedTheDrawingOver(3)));

        var error = cut.Find("#drawio-save-error").TextContent;
        Assert.Contains("did not reach the page, and nothing was written", error, StringComparison.Ordinal);
        Assert.Contains("over the 16 MB", error, StringComparison.Ordinal);
        Assert.Equal(0, stream.Opened);
        Assert.Empty(handler.GeometryWrites);
        Assert.Empty(handler.Writes);
        Assert.NotEqual(round, cut.Find("#drawio-save-round").TextContent);
    }

    /// <summary>
    /// A circuit that drops while the drawing is being taken is said the same way, not left as a round
    /// that never ends: why in the page's words, and what the circuit threw behind the line's "?" (row
    /// 37 of the queue, extended to the whole page on 10/10).
    /// </summary>
    [Fact]
    public async Task ADrawingTheBrowserDoesNotHandOverIsSaidOnThePage()
    {
        var handler = HostWithTwoDevices();
        var bridge = JSInterop.SetupModule(BridgePath);
        bridge.Setup<IJSStreamReference?>("handedOverDrawing", _ => true)
            .SetException(new JSDisconnectedException("the circuit is gone (test)"));
        var cut = await RenderWithAsync(handler);

        Assert.False(await cut.InvokeAsync(() => cut.Instance.OnDrawioHandedTheDrawingOver(1)));

        Assert.Equal("The drawing did not reach the page, and nothing was written: the browser did not hand the drawing over, because the connection to the browser was lost.",
            cut.Find("#drawio-save-error").TextContent);
        Assert.Equal("JSDisconnectedException: the circuit is gone (test)", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-save-error"));
        Assert.Empty(handler.Writes);
    }

    /// <summary>
    /// Disposing the stream after the read is a round trip that fails once the circuit has dropped:
    /// the drawing was read, and the save goes through on it.
    /// </summary>
    [Fact]
    public async Task ADisposeThatFailsAfterTheReadDoesNotStopTheSave()
    {
        var handler = HostWithTwoDevices();
        var bridge = JSInterop.SetupModule(BridgePath);
        var moved = DrawioDocument.Build(new PlantGraph([Item(A, 99, 77), Item(B, 500, 20)], [], [Type()]), null);
        bridge.Setup<IJSStreamReference?>("handedOverDrawing", _ => true).SetResult(new TestJSStreamReference(
            System.Text.Encoding.UTF8.GetBytes(moved), disposeFailure: new JSDisconnectedException("the circuit is gone (test)")));
        var cut = await RenderWithAsync(handler);

        Assert.True(await cut.InvokeAsync(() => cut.Instance.OnDrawioHandedTheDrawingOver(5)));
        Assert.Equal(A, Assert.Single(handler.GeometryWrites).ItemId);
    }

    /// <summary>
    /// No bytes under the number: the bridge no longer holds that drawing (it was let go at a detach),
    /// or draw.io handed over nothing at all. Said as that, not as a drawing that is "not XML".
    /// </summary>
    [Fact]
    public async Task NoDrawingUnderTheNumberIsSaidAsSuch()
    {
        var handler = HostWithTwoDevices();
        var bridge = JSInterop.SetupModule(BridgePath);
        bridge.Setup<IJSStreamReference?>("handedOverDrawing", _ => true).SetResult(new TestJSStreamReference([]));
        var cut = await RenderWithAsync(handler);

        Assert.False(await cut.InvokeAsync(() => cut.Instance.OnDrawioHandedTheDrawingOver(9)));

        var error = cut.Find("#drawio-save-error").TextContent;
        Assert.Contains("no drawing under the number", error, StringComparison.Ordinal);
        Assert.DoesNotContain("..", error, StringComparison.Ordinal);
        Assert.Empty(handler.Writes);
    }

    private async Task<IRenderedComponent<DrawioEditor>> RenderWithAsync(DrawioWriteHandler handler)
    {
        var state = new EditorSessionState(new Uri("http://localhost:5580"), handler);
        state.UseKey("test-key");
        Services.AddSingleton(state);
        var cut = Render<DrawioEditor>();
        await cut.InvokeAsync(() => cut.Instance.OnDrawioReady());
        return cut;
    }
}
