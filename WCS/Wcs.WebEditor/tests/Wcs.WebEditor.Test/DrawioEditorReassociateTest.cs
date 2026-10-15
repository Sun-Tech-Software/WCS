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
/// <b>Row 15 on the page: pointing a shape at a device by hand, and what the next save then does
/// with it.</b>
/// </summary>
/// <remarks>
/// <para>
/// The gesture exists because this branch still has roads that leave a marked cell the plant does
/// not know on the canvas - measured in a real browser by
/// <c>DrawioACellCanStillLoseItsDeviceTest</c>: a drawing file dropped on the canvas, the drawing
/// carried away with Ctrl+A/Ctrl+C and brought back with Ctrl+V, and a device somebody else deletes
/// while the shape is on screen. All three end with a shape refused at every save, and the only
/// remedy the page had was "delete it and drag it again".
/// </para>
/// <para>
/// <b>What the page really records is a PAIR</b>, in the same session map a device born from a drop
/// lives in - the cell keeps draw.io's identifier. So the last test here is the one that matters:
/// the save writes that shape's geometry to that device, down the road a dropped device already
/// goes down, and no new kind of cell reaches the drawing.
/// </para>
/// <para>
/// bUnit runs no JavaScript: the selection and the save come in through the <c>[JSInvokable]</c>
/// doors, and the bridge's <c>markedCells</c> is planned. What it cannot answer - whether draw.io
/// really hands those marks back - is measured in a real browser by
/// <c>DrawioReassociateTest</c>.
/// </para>
/// </remarks>
public sealed class DrawioEditorReassociateTest : PlantCanvasBunitContext
{
    /// <summary>The device the plant draws: it has a box and a type the registry declares.</summary>
    private static readonly Guid Drawn = Guid.Parse("11111111-1111-1111-1111-111111111111");

    /// <summary>The device the plant holds and the drawing does NOT show: no box at all, which is what <c>DrawioDocument.WhyNotDrawn</c> asks first. It is the one this gesture can be aimed at.</summary>
    private static readonly Guid Undrawn = Guid.Parse("22222222-2222-2222-2222-222222222222");

    /// <summary>The identifier draw.io gave the orphan shape: not a guid, exactly like the one a dropped shape carries.</summary>
    private const string OrphanCell = "drawio-orphan-1";

    private static DeviceTypeDto Type() => new(
        "automation", (short)DeviceTypes.Automation, "Automation",
        [new ShapePartDto("M 4 4 H 96 V 96 H 4 Z", "#334155", Filled: false, Rigid: false)],
        [new DevicePortDto("in", 4, 50, "in"), new DevicePortDto("out", 96, 50, "out")],
        new ShapePointDto(50, 50), new ShapePointDto(88, 60));

    private static PlantItemDto Item(Guid id, string name, double width) =>
        new(id, name, DeviceTypes.Automation, null, null, null, default, default, 0, null,
            0, 0, 0, 0, default!, width > 0 ? 10 : null, width > 0 ? 20 : null, width, width > 0 ? 184 : 0, 0);

    private static PlantItemConfigDto Config(Guid id, string name) =>
        new() { ItemId = id, Name = name, DeviceType = (short)DeviceTypes.Automation };

    private static DrawioWriteHandler Host()
    {
        var handler = new DrawioWriteHandler
        {
            Items = [Item(Drawn, "Conveyor", 130), Item(Undrawn, "NoBox", 0)],
            Types = [Type()],
        };
        handler.Configs[Drawn] = Config(Drawn, "Conveyor");
        handler.Configs[Undrawn] = Config(Undrawn, "NoBox");
        return handler;
    }

    private static PlantGraph GraphOf(DrawioWriteHandler handler) =>
        new([.. handler.Items], [.. handler.Junctions], [.. handler.Types]);

    /// <summary>
    /// The orphan shape as the drawing carries it: a marked device cell, on the canvas, with an
    /// identifier the plant knows nothing about. It is written out by hand here because that is
    /// exactly how it arrives in the product - out of a dropped file or a paste, which is XML
    /// nobody of ours wrote.
    /// </summary>
    private const string OrphanXml =
        $"""<object id="{OrphanCell}" label="ForeignDevice" wcs="device"><mxCell style="rounded=0;" vertex="1" parent="{DrawioDocument.DefaultLayerId}"><mxGeometry x="900" y="700" width="120" height="60" as="geometry"/></mxCell></object>""";

    /// <summary>What the canvas answers when it is asked what it carries: the seeded device, and the orphan.</summary>
    private static DrawioMarkedCell[] CanvasWithTheOrphan() =>
    [
        new(Drawn.ToString(), CellReassociation.DeviceMark),
        new(OrphanCell, CellReassociation.DeviceMark),
    ];

    private BunitJSModuleInterop _bridge = null!;

    private async Task<IRenderedComponent<DrawioEditor>> RenderWithAsync(
        DrawioWriteHandler handler, DrawioMarkedCell[]? canvas, Exception? renameFailure = null)
    {
        _bridge = JSInterop.SetupModule("./_content/Wcs.WebEditor/drawio-bridge.js");
        // Planned even when this test does not look at it: without a plan bUnit's Loose mode answers
        // default(bool), that is "the canvas did not take the label", and every outcome would carry
        // a failure half nobody asked for.
        if (renameFailure is null) _bridge.Setup<bool>("renameCell").SetResult(true);
        else _bridge.Setup<bool>("renameCell", _ => true).SetException(renameFailure);
        if (canvas is not null) _bridge.Setup<DrawioMarkedCell[]>("markedCells").SetResult(canvas);

        var state = new EditorSessionState(new Uri("http://localhost:5580"), handler);
        state.UseKey("test-key");
        Services.AddSingleton(state);
        var cut = Render<DrawioEditor>();
        await cut.InvokeAsync(() => cut.Instance.OnDrawioReady());
        return cut;
    }

    private static Task SelectAsync(IRenderedComponent<DrawioEditor> cut, params string[] cells) =>
        cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged(cells));

    private static async Task ReassociateAsync(IRenderedComponent<DrawioEditor> cut, string typed)
    {
        cut.Find("#drawio-reassociate-id").Input(typed);
        await cut.Find("#drawio-reassociate-confirm").ClickAsync(new());
    }

    private static string Outcome(IRenderedComponent<DrawioEditor> cut) =>
        cut.Find("#drawio-reassociate-outcome").TextContent;

    private static Task SaveAsync(IRenderedComponent<DrawioEditor> cut, string xml) =>
        cut.InvokeAsync(() => cut.Instance.OnDrawioSaved(xml));

    private static string[] LinesOf(IRenderedComponent<DrawioEditor> cut, string cssClass) =>
        [.. cut.FindAll($"li.{cssClass}").Select(node => node.TextContent)];

    // ---------------------------------------------------------------- when the box is there

    /// <summary>
    /// The box is for one kind of shape and shows up for no other: a device of the plant gets its
    /// properties, a shape the drawing does not mark gets the old hint, and only the marked cell
    /// the plant does not know gets the box.
    /// </summary>
    [Fact]
    public async Task TheBoxShowsUpOnlyForAMarkedCellThePlantDoesNotKnow()
    {
        var cut = await RenderWithAsync(Host(), CanvasWithTheOrphan());

        await SelectAsync(cut, Drawn.ToString());
        Assert.Empty(cut.FindAll("#drawio-reassociate"));
        Assert.NotEmpty(cut.FindAll("#drawio-property-panel"));

        // A cell the canvas does not carry a mark for: free drawing, and the column says what it
        // has always said.
        await SelectAsync(cut, "a-free-note");
        Assert.Empty(cut.FindAll("#drawio-reassociate"));
        Assert.NotEmpty(cut.FindAll("#drawio-property-hint"));

        await SelectAsync(cut, OrphanCell);
        Assert.NotEmpty(cut.FindAll("#drawio-reassociate"));
        Assert.Contains(OrphanCell, cut.Find("#drawio-reassociate-subject").TextContent);
        Assert.Empty(cut.FindAll("#drawio-property-hint"));
    }

    /// <summary>
    /// Two cells selected is not this gesture either: the box is about ONE shape, and a page that
    /// showed it over a selection of five would be pointing whichever of them came first.
    /// </summary>
    [Fact]
    public async Task TwoCellsSelectedShowNoBox()
    {
        var cut = await RenderWithAsync(Host(), CanvasWithTheOrphan());

        await SelectAsync(cut, OrphanCell, Drawn.ToString());

        Assert.Empty(cut.FindAll("#drawio-reassociate"));
    }

    /// <summary>
    /// A drawing that could not be asked what it carries shows no box at all - rather than one that
    /// would refuse every gesture made in it.
    /// </summary>
    [Fact]
    public async Task ADrawingThatCannotBeAskedShowsNoBox()
    {
        var cut = await RenderWithAsync(Host(), canvas: null);

        await SelectAsync(cut, OrphanCell);

        Assert.Empty(cut.FindAll("#drawio-reassociate"));
    }

    /// <summary>
    /// The identifiers that can be typed in are on screen: the devices of the plant no cell of the
    /// canvas stands for. Without them the box asks for a guid with nowhere to read one.
    /// </summary>
    [Fact]
    public async Task TheBoxListsTheDevicesWithoutAShape()
    {
        var cut = await RenderWithAsync(Host(), CanvasWithTheOrphan());

        await SelectAsync(cut, OrphanCell);

        var candidate = Assert.Single(LinesOf(cut, "drawio-reassociate-candidate"));
        Assert.Contains("NoBox", candidate);
        Assert.Contains(Undrawn.ToString(), candidate);
        // And the one that IS drawn is not offered: pointing a second shape at it would send two
        // geometries for one device at the next save.
        Assert.DoesNotContain(Drawn.ToString(), candidate);
        Assert.Contains("1 device", cut.Find("#drawio-reassociate-candidates-summary").TextContent);
    }

    // ---------------------------------------------------------------- the refusals, on the page

    /// <summary>
    /// <b>The loaded gun, at the page's own door.</b> An identifier the host does not answer for is
    /// refused in words, and NOTHING is bound: the save that follows still refuses the shape as a
    /// cell the plant does not know, which is the only way to see that nothing was written.
    /// </summary>
    [Fact]
    public async Task AnIdentifierThePlantDoesNotKnowIsRefusedAndTheSaveStillDoesNotKnowTheShape()
    {
        var handler = Host();
        var cut = await RenderWithAsync(handler, CanvasWithTheOrphan());
        await SelectAsync(cut, OrphanCell);

        var unknown = Guid.Parse("99999999-9999-9999-9999-999999999999");
        await ReassociateAsync(cut, unknown.ToString());

        Assert.Contains(unknown.ToString(), Outcome(cut));
        Assert.Contains("Nothing was written", Outcome(cut));
        // The box is still there, with the shape still an orphan.
        Assert.NotEmpty(cut.FindAll("#drawio-reassociate"));

        await SaveAsync(cut, DrawioDocument.Build(GraphOf(handler), OrphanXml));

        Assert.Empty(handler.GeometryWrites);
        var refused = Assert.Single(LinesOf(cut, "drawio-refusal"));
        Assert.Contains(OrphanCell, refused);
        Assert.Contains("the plant does not know", refused);
    }

    /// <summary>
    /// The empty box: the WinForms Editor's "Reset" half, which on this branch has nothing left to
    /// clear. It is refused in words instead of doing nothing, so that whoever has that gesture in
    /// their hands is told rather than left guessing.
    /// </summary>
    [Fact]
    public async Task TheEmptyBoxSaysThereIsNothingToClear()
    {
        var cut = await RenderWithAsync(Host(), CanvasWithTheOrphan());
        await SelectAsync(cut, OrphanCell);

        await ReassociateAsync(cut, "   ");

        Assert.Contains("does not take one away", Outcome(cut));
        Assert.NotEmpty(cut.FindAll("#drawio-reassociate"));
    }

    /// <summary>
    /// What the last gesture answered belongs to the shape it was about: selecting another one
    /// takes the line away, instead of leaving a refusal standing next to a different shape.
    /// </summary>
    [Fact]
    public async Task TheOutcomeGoesAwayWithTheSelection()
    {
        var cut = await RenderWithAsync(Host(), CanvasWithTheOrphan());
        await SelectAsync(cut, OrphanCell);
        await ReassociateAsync(cut, "not an identifier");
        Assert.NotEmpty(cut.FindAll("#drawio-reassociate-outcome"));

        await SelectAsync(cut, Drawn.ToString());

        Assert.Empty(cut.FindAll("#drawio-reassociate-outcome"));
    }

    // ---------------------------------------------------------------- the gesture that goes through

    /// <summary>
    /// <b>The whole point of the row.</b> The shape is pointed at the device with no box; the next
    /// save writes THAT shape's position and size to THAT device, and says so by name. Before this,
    /// the same save refused the shape and the device stayed undrawn for ever.
    /// </summary>
    [Fact]
    public async Task PointingTheShapeAtADeviceMakesTheNextSaveWriteItsGeometryThere()
    {
        var handler = Host();
        var cut = await RenderWithAsync(handler, CanvasWithTheOrphan());
        await SelectAsync(cut, OrphanCell);

        await ReassociateAsync(cut, Undrawn.ToString());

        Assert.Contains("NoBox", Outcome(cut));
        Assert.Contains("Nothing has reached the plant yet", Outcome(cut));
        // The shape is a device of the plant from this instant, for this page too: the box has gone
        // and the properties of the device it now stands for are in its place. It is the same state
        // a dropped shape is in, and nothing else.
        Assert.Empty(cut.FindAll("#drawio-reassociate"));
        Assert.Equal("Selected: NoBox", cut.Find("#drawio-selection").TextContent);

        await SaveAsync(cut, DrawioDocument.Build(GraphOf(handler), OrphanXml));

        var geometry = Assert.Single(handler.GeometryWrites);
        Assert.Equal(Undrawn, geometry.ItemId);
        Assert.Equal(900, geometry.X!.Value, precision: 2);
        Assert.Equal(700, geometry.Y!.Value, precision: 2);
        Assert.Equal(120, geometry.Width!.Value, precision: 2);
        Assert.Equal(60, geometry.Height!.Value, precision: 2);
        // And no refusal about that cell any more: it is not "a cell the plant does not know".
        Assert.Empty(LinesOf(cut, "drawio-refusal"));
    }

    /// <summary>
    /// Row 37 of the queue: the host cannot be asked whether it has the device typed in. The outcome
    /// says why in words, and what the client said is behind its "?"; nothing is bound.
    /// </summary>
    [Fact]
    public async Task AHostThatCannotBeAskedIsSaidInWordsWithWhatCameBackBehindTheQuestionMark()
    {
        var handler = Host();
        handler.SingleItemFailure = new HttpRequestException("the connection was reset (test)");
        var cut = await RenderWithAsync(handler, CanvasWithTheOrphan());
        await SelectAsync(cut, OrphanCell);

        await ReassociateAsync(cut, Undrawn.ToString());

        Assert.Equal(
            "The host could not be asked whether it has a device 22222222-2222-2222-2222-222222222222 because the connection to the host failed, so nothing was written.",
            Outcome(cut));
        Assert.Equal(
            "HttpRequestException: the connection was reset (test)",
            DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-reassociate-outcome"));
        Assert.NotEmpty(cut.FindAll("#drawio-reassociate"));
    }

    /// <summary>
    /// Row 37 of the queue: the shape is pointed at the device, and the drawing cannot be told its new
    /// label. The outcome says so in words; what the drawing's script threw is behind its "?".
    /// </summary>
    [Fact]
    public async Task ALabelTheDrawingCannotTakeIsSaidWithWhatTheScriptThrewBehindTheQuestionMark()
    {
        var cut = await RenderWithAsync(Host(), CanvasWithTheOrphan(), new Microsoft.JSInterop.JSException("the canvas is gone (test)"));
        await SelectAsync(cut, OrphanCell);

        await ReassociateAsync(cut, Undrawn.ToString());

        Assert.EndsWith(
            "After that, the drawing draws it from the plant at every opening. " +
            "The drawing could not be told, so the shape still carries its old label until the page is reloaded.",
            Outcome(cut));
        Assert.Equal("the canvas is gone (test)", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-reassociate-outcome"));
    }

    /// <summary>
    /// The shape stops calling itself by the name of a device it is not: the canvas is asked for
    /// the new label, with the CELL's identifier - the same road the panel's rename takes.
    /// </summary>
    [Fact]
    public async Task TheShapeTakesTheDeviceNameOnTheCanvas()
    {
        var cut = await RenderWithAsync(Host(), CanvasWithTheOrphan());
        await SelectAsync(cut, OrphanCell);

        await ReassociateAsync(cut, Undrawn.ToString());

        var rename = Assert.Single(_bridge.Invocations["renameCell"]);
        Assert.Equal(OrphanCell, rename.Arguments[0]);
        Assert.Equal("NoBox", rename.Arguments[1]);
        // MUST FIX of the final review (row 15 vs. row 46): the fourth argument is alreadyOnThePlant,
        // and it must be FALSE here - nothing has reached the plant yet (WhatItMeans says so), so
        // this model edit is real unsaved work and must not be suppressed as an echo of a write
        // already there. See RenameCellAsync in DrawioEditor.razor and renameCell in
        // drawio-bridge.js.
        Assert.Equal(false, rename.Arguments[3]);
        Assert.Equal(4, rename.Arguments.Count);
    }

    /// <summary>
    /// A device already drawn on this canvas is refused at the page's door too, and the save that
    /// follows proves nothing was bound: one geometry write, for the device that really moved, and
    /// the orphan still refused by name.
    /// </summary>
    [Fact]
    public async Task ADeviceAlreadyDrawnIsRefusedAndNothingIsBound()
    {
        var handler = Host();
        var cut = await RenderWithAsync(handler, CanvasWithTheOrphan());
        await SelectAsync(cut, OrphanCell);

        await ReassociateAsync(cut, Drawn.ToString());

        Assert.Contains("already drawn on this canvas", Outcome(cut));
        Assert.Contains(Drawn.ToString(), Outcome(cut));

        await SaveAsync(cut, DrawioDocument.Build(GraphOf(handler), OrphanXml));

        Assert.Empty(handler.GeometryWrites);
        var refused = Assert.Single(LinesOf(cut, "drawio-refusal"));
        Assert.Contains(OrphanCell, refused);
    }
}
