using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Drawio;
using Wcs.WebEditor.Model;
using Wcs.WebEditor.Pages;
using Wcs.WebEditor.Session;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

/// <summary>
/// <b>A device somebody else deleted leaves the drawing at the first save</b> (owner's decision of
/// 27/9, coda dei lavori voce 7), at the level of the page: what it asks of the bridge, and what the
/// report says once the bridge has answered.
/// </summary>
/// <remarks>
/// bUnit runs no JavaScript: what the bridge DOES with the cells - out of the model, out of the undo
/// history, without lighting "Leave site?" - is measured in a real browser by
/// <c>DrawioThePlantChangedUnderneathTest</c>. Here the bridge's answer is planned, and what is
/// measured is that the page asks for exactly the device's cells, and says what the answer was.
/// </remarks>
public sealed class DrawioEditorDeviceGoneTest : PlantCanvasBunitContext
{
    private const string BridgeModule = "./_content/Wcs.WebEditor/drawio-bridge.js";
    private const string Remove = "removeDeletedDeviceCells";

    private static readonly Guid A = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid B = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid J = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static DeviceTypeDto Type() => new("automation", 0, "Automation", [new ShapePartDto("M 4 4 H 96 V 96 H 4 Z", "#334155", Filled: false, Rigid: false)],
        [new DevicePortDto("in", 4, 50, "in"), new DevicePortDto("out", 96, 50, "out")],
        new ShapePointDto(50, 50), new ShapePointDto(88, 60));

    private static PlantItemDto Item(Guid id, string name, double x, double y) =>
        new(id, name, DeviceTypes.Automation, null, null, null, default, default, 0, null,
            0, 0, 0, 0, default!, x, y, 130, 184, 0)
        {
            GeometryVersion = id
        };

    private static DrawioWriteHandler Host()
    {
        var handler = new DrawioWriteHandler
        {
            Items = [Item(A, "Gone", 10, 20), Item(B, "Stays", 500, 20)],
            Junctions = [new JunctionDto(J, A, B, 99)],
            Types = [Type()],
        };
        handler.Configs[A] = new PlantItemConfigDto { ItemId = A, Name = "Gone", DeviceType = 0 };
        handler.Configs[B] = new PlantItemConfigDto { ItemId = B, Name = "Stays", DeviceType = 0 };
        return handler;
    }

    /// <summary>Another operator, on another machine: A goes, and the host takes J with it.</summary>
    private static void TheOtherOperatorDeletesA(DrawioWriteHandler handler)
    {
        handler.Items.RemoveAll(item => item.ItemId == A);
        handler.Junctions.RemoveAll(junction => junction.SourceItemId == A || junction.DestinationItemId == A);
        handler.Configs.Remove(A);
    }

    private BunitJSModuleInterop PlanTheBridge(string answer)
    {
        var module = JSInterop.SetupModule(BridgeModule);
        module.Setup<string?>(Remove, _ => true).SetResult(answer);
        return module;
    }

    private async Task<IRenderedComponent<DrawioEditor>> RenderAsync(DrawioWriteHandler handler)
    {
        var state = new EditorSessionState(new Uri("http://localhost:5580"), handler);
        state.UseKey("test-key");
        Services.AddSingleton(state);
        var cut = Render<DrawioEditor>();
        await cut.InvokeAsync(() => cut.Instance.OnDrawioReady());
        return cut;
    }

    private static string[] LinesOf(IRenderedComponent<DrawioEditor> cut, string cssClass) =>
        [.. cut.FindAll($"li.{cssClass}").Select(node => node.TextContent)];

    private static string[] CellsAskedFor(BunitJSModuleInterop module)
    {
        var asked = Assert.Single(module.Invocations[Remove]);
        var cells = Assert.IsAssignableFrom<IEnumerable<string>>(Assert.Single(asked.Arguments));
        return [.. cells];
    }

    [Fact]
    public async Task TheSaveAsksTheBridgeToTakeOffTheDeviceAndItsArrowAndSaysSo()
    {
        var module = PlanTheBridge("ok");
        var handler = Host();
        var cut = await RenderAsync(handler);
        var drawn = DrawioDocument.Build(new PlantGraph([.. handler.Items], [.. handler.Junctions], [Type()]), null);
        TheOtherOperatorDeletesA(handler);

        var wroteEverything = await cut.InvokeAsync(() => cut.Instance.OnDrawioSaved(drawn));

        Assert.Equal([A.ToString(), J.ToString()], CellsAskedFor(module));
        var notice = Assert.Single(LinesOf(cut, "drawio-notice"), line => line.Contains("Gone", StringComparison.Ordinal));
        Assert.Contains("Gone: deleted on the host by somebody else while you were drawing, and the connection hanging from it went with it - removed from the drawing too, together with its arrow.", notice);
        Assert.DoesNotContain(LinesOf(cut, "drawio-refusal"), line => line.Contains("Gone", StringComparison.Ordinal));
        // Nothing of the drawing was left out: the canvas is the plant again, and the round says so -
        // which is what lets the bridge put the "Leave site?" mark down.
        Assert.True(wroteEverything);
        Assert.DoesNotContain("item-post", handler.Writes);
        Assert.DoesNotContain("geometry", handler.Writes);
    }

    [Fact]
    public async Task ACanvasThatDidNotTakeThemOffIsSaidAndTheRoundIsNotFinished()
    {
        var module = PlanTheBridge("failed");
        var handler = Host();
        var cut = await RenderAsync(handler);
        var drawn = DrawioDocument.Build(new PlantGraph([.. handler.Items], [.. handler.Junctions], [Type()]), null);
        TheOtherOperatorDeletesA(handler);

        var wroteEverything = await cut.InvokeAsync(() => cut.Instance.OnDrawioSaved(drawn));

        Assert.Equal([A.ToString(), J.ToString()], CellsAskedFor(module));
        var refusal = Assert.Single(LinesOf(cut, "drawio-refusal"), line => line.Contains("Gone", StringComparison.Ordinal));
        Assert.Contains("it could not be removed from the drawing: delete it by hand, with its arrow.", refusal);
        Assert.DoesNotContain(LinesOf(cut, "drawio-notice"), line => line.Contains("Gone", StringComparison.Ordinal));
        Assert.False(wroteEverything);
    }

    [Fact]
    public async Task AMoveOfTheDeviceIsSaidLostAndTheDeviceStillLeavesTheDrawing()
    {
        var module = PlanTheBridge("ok");
        var handler = Host();
        var cut = await RenderAsync(handler);
        var moved = new PlantGraph([Item(A, "Gone", 99, 77), Item(B, "Stays", 500, 20)], [.. handler.Junctions], [Type()]);
        TheOtherOperatorDeletesA(handler);

        var wroteEverything = await cut.InvokeAsync(() => cut.Instance.OnDrawioSaved(DrawioDocument.Build(moved, null)));

        Assert.Equal([A.ToString(), J.ToString()], CellsAskedFor(module));
        // A notice, not a refusal (fix round, MINOR 1): nothing is left for the operator to do or to
        // lose, so the round is complete and "Leave site?" can go down.
        var notice = Assert.Single(LinesOf(cut, "drawio-notice"), line => line.Contains("Gone", StringComparison.Ordinal));
        Assert.Contains("removed from the drawing too, together with its arrow. What you changed on it in this drawing is lost, because the device no longer exists.", notice);
        Assert.DoesNotContain(LinesOf(cut, "drawio-refusal"), line => line.Contains("Gone", StringComparison.Ordinal));
        Assert.DoesNotContain("geometry", handler.Writes);
        Assert.True(wroteEverything);
    }

    [Fact]
    public async Task ASaveWithNoDeviceDeletedElsewhereAsksTheBridgeNothing()
    {
        var module = PlanTheBridge("ok");
        var handler = Host();
        var cut = await RenderAsync(handler);
        var drawn = DrawioDocument.Build(new PlantGraph([.. handler.Items], [.. handler.Junctions], [Type()]), null);

        Assert.True(await cut.InvokeAsync(() => cut.Instance.OnDrawioSaved(drawn)));

        Assert.Empty(module.Invocations[Remove]);
    }
}
