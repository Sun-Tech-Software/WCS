using Bunit;
using Microsoft.AspNetCore.Components.Web;
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
/// The birth of a device from a DROP (owner's decision of 2026-09-17): a shape dragged out of the
/// device section becomes a real device on the plant the instant it touches the canvas, not at the
/// save.
/// </summary>
/// <remarks>
/// <para>
/// bUnit runs no JavaScript and simulates no drag: these tests come in through the
/// <c>[JSInvokable]</c> method <c>OnDrawioCellsAdded</c>, called on the instance already rendered -
/// the same door <c>DrawioEditorWriteTest</c> uses for <c>OnDrawioSaved</c>. What they cannot answer
/// is whether draw.io really calls it, and with what: that is measured in a real browser, by
/// <c>DrawioShapesPanelTest</c>, which drags an entry with the mouse and reads the device back off
/// the host.
/// </para>
/// <para>
/// The cells handed to that method are built with <see cref="Dropped"/>, from
/// <see cref="DrawioLibrary"/>'s own entry: the mark, the type attribute and the box are read out of
/// the very XML draw.io is given, not written by hand here. A hand-written cell would have let the
/// panel's format drift away from what this page expects without a single test going red.
/// </para>
/// </remarks>
public sealed class DrawioEditorCreateTest : PlantCanvasBunitContext
{
    private static readonly Guid A = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid B = Guid.Parse("22222222-2222-2222-2222-222222222222");

    /// <summary>
    /// Two types, and it matters that they are two: with one type only, "the device was created with
    /// the type the shape declared" would be true of any code at all, and the attribute could carry
    /// a constant without a test noticing.
    /// </summary>
    private static IReadOnlyList<DeviceTypeDto> Registry() =>
    [
        new("automation", (short)DeviceTypes.Automation, "Automation",
            [new ShapePartDto("M 4 4 H 96 V 96 H 4 Z", "#334155", Filled: false, Rigid: false)],
            [new DevicePortDto("in", 4, 50, "in"), new DevicePortDto("out", 96, 50, "out")],
            new ShapePointDto(50, 50), new ShapePointDto(88, 60)),
        new("buffer", (short)DeviceTypes.Buffer, "Buffer",
            [new ShapePartDto("M 4 30 H 96 V 70 H 4 Z", "#0f766e", Filled: false, Rigid: false)],
            [new DevicePortDto("in", 4, 50, "in"), new DevicePortDto("out", 96, 50, "out")],
            new ShapePointDto(50, 50), new ShapePointDto(88, 60)),
    ];

    private static PlantItemDto Item(Guid id, double x, double y) =>
        new(id, id.ToString()[..4], DeviceTypes.Automation, null, null, null, default, default, 0, null,
            0, 0, 0, 0, default!, x, y, 130, 184, 0);

    private static PlantItemConfigDto Config(Guid id) => new() { ItemId = id, Name = id.ToString()[..4], DeviceType = 0 };

    private static DrawioWriteHandler HostWithTwoDevices()
    {
        var handler = new DrawioWriteHandler
        {
            Items = [Item(A, 10, 20), Item(B, 500, 20)],
            Types = [.. Registry()],
        };
        handler.Configs[A] = Config(A);
        handler.Configs[B] = Config(B);
        return handler;
    }

    /// <summary>
    /// The bridge module as these tests plan it, kept so a test can read <b>what was asked of the
    /// canvas</b>. Set by <see cref="RenderWithAsync"/>, which is the only place that may plan it:
    /// the module is imported at the first render, and one planned afterwards would not replace it.
    /// </summary>
    private BunitJSModuleInterop _bridge = null!;

    /// <summary>
    /// Renders the page against <paramref name="handler"/>, with the bridge's <c>renameCell</c>
    /// planned: since the label at birth, <b>every</b> creation asks the canvas for the new label,
    /// so a test that did not plan it would be measuring the failure branch without meaning to
    /// (bUnit's <c>Loose</c> mode answers an unplanned call with <c>default(bool)</c>, that is
    /// "the canvas did not take it").
    /// </summary>
    /// <param name="canvasRefuses">
    /// The cell, if any, for which the canvas answers "no": the case in which the operator deleted
    /// the shape while the creation was in flight.
    /// </param>
    /// <param name="canvasThrowsFor">The cell, if any, for which the call itself fails.</param>
    /// <remarks>
    /// The three plans are matched on <b>disjoint</b> predicates - one cell each for the two
    /// failures, everything else for the success - so which one answers does not depend on the order
    /// bUnit consults them in. Two overlapping plans would make these tests depend on an internal of
    /// the test library instead of on the page.
    /// </remarks>
    private async Task<IRenderedComponent<DrawioEditor>> RenderWithAsync(
        DrawioWriteHandler handler, string? canvasRefuses = null, string? canvasThrowsFor = null)
    {
        _bridge = JSInterop.SetupModule("./_content/Wcs.WebEditor/drawio-bridge.js");
        _bridge.Setup<bool>("renameCell",
            call => CellOf(call) != canvasRefuses && CellOf(call) != canvasThrowsFor).SetResult(true);
        if (canvasRefuses is not null)
            _bridge.Setup<bool>("renameCell", call => CellOf(call) == canvasRefuses).SetResult(false);
        if (canvasThrowsFor is not null)
            _bridge.Setup<bool>("renameCell", call => CellOf(call) == canvasThrowsFor)
                .SetException(new Microsoft.JSInterop.JSException(CanvasFailure));

        var state = new EditorSessionState(new Uri("http://localhost:5580"), handler);
        state.UseKey("test-key");
        Services.AddSingleton(state);
        var cut = Render<DrawioEditor>();
        await cut.InvokeAsync(() => cut.Instance.OnDrawioReady());
        return cut;
    }

    /// <summary>What the failing call says, repeated in the assertions: the page has to carry it through to whoever reads the report.</summary>
    private const string CanvasFailure = "il ponte non ha potuto parlare con la tela (prova)";

    /// <summary>The cell a planned <c>renameCell</c> was asked about - its first argument.</summary>
    private static string? CellOf(JSRuntimeInvocation call) =>
        call.Arguments.Count > 0 ? call.Arguments[0] as string : null;

    /// <summary>What was asked of the canvas, in the order it was asked.</summary>
    private IReadOnlyList<JSRuntimeInvocation> Relabels() => [.. _bridge.Invocations["renameCell"]];

    private static string Cells(IEnumerable<JSRuntimeInvocation> calls) =>
        string.Join(" | ", calls.Select(call => call.Arguments[0]));

    private static string Labels(IEnumerable<JSRuntimeInvocation> calls) =>
        string.Join(" | ", calls.Select(call => call.Arguments[1]));

    /// <summary>
    /// The cell a drop of the entry of <paramref name="type"/> leaves on the canvas, <b>taken from
    /// the panel's own entry</b>: <see cref="DrawioLibrary.Build"/> is called for real, its XML is
    /// read with <see cref="DrawioReader"/>, and the mark and the type attribute come out of there.
    /// Only the identifier and the drop position are the test's, because those two are draw.io's at
    /// run time and no panel entry carries them.
    /// </summary>
    private static DrawioAddedCell Dropped(DeviceTypeDto type, string cellId, double x, double y)
    {
        var entries = System.Text.Json.JsonDocument.Parse(DrawioLibrary.Build([type]));
        var xml = entries.RootElement[0].GetProperty("entries")[0].GetProperty("libs")[0]
            .GetProperty("data")[0].GetProperty("xml").GetString()!;
        var cell = Assert.Single(DrawioReader.Read(xml).Devices);
        return new DrawioAddedCell(cellId, cell.Mark, TypeAttributeOf(xml),
            DrawioDocument.DefaultLayerId, x, y, cell.Width, cell.Height);
    }

    /// <summary>The type the panel entry declares, read off the entry's own XML: DrawioReader does not carry it (it is not a fact the SAVE needs).</summary>
    private static string? TypeAttributeOf(string entryXml) =>
        (string?)System.Xml.Linq.XDocument.Parse(entryXml).Descendants("object").Single()
            .Attribute(DrawioLibrary.TypeAttribute);

    private static async Task DropAsync(IRenderedComponent<DrawioEditor> cut, params DrawioAddedCell[] cells) =>
        await cut.InvokeAsync(() => cut.Instance.OnDrawioCellsAdded(cells));

    private static string[] LinesOf(IRenderedComponent<DrawioEditor> cut, string cssClass) =>
        [.. cut.FindAll($"li.{cssClass}").Select(node => node.TextContent)];

    private static string? TextOrNull(IRenderedComponent<DrawioEditor> cut, string selector) =>
        cut.FindAll(selector) is [var node, ..] ? node.TextContent : null;

    // ---------------------------------------------------------------- the trap that opens the batch

    /// <summary>
    /// <b>THE TRAP.</b> Opening a plant sends every one of its devices into draw.io's model at once:
    /// on the owner's plant that is fifty-one cells, and it happens again at every reopening. Zero
    /// of them must become a device.
    /// </summary>
    /// <remarks>
    /// <b>It is anchored, and that is the whole point of how it is written</b>: the cells handed to
    /// the page are read out of the document the page really sent to draw.io
    /// (<c>DrawioDocument.Build</c> on the same graph), and the test first asserts that there ARE
    /// some. A test that dropped nothing would pass with nothing written and would say nothing at
    /// all - which is exactly the shape of a test that cannot fail.
    /// </remarks>
    [Fact]
    public async Task OpeningAPlantWithDevicesCreatesNothing()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);

        // The very document the seeding sends, read back as draw.io would report its cells.
        var seeded = DrawioReader.Read(DrawioDocument.Build(GraphOf(handler), null)).Devices;
        Assert.Equal(2, seeded.Count);
        var arriving = seeded
            .Select(cell => new DrawioAddedCell(cell.Id, cell.Mark, null,
                DrawioDocument.DefaultLayerId, cell.X, cell.Y, cell.Width, cell.Height))
            .ToArray();

        await DropAsync(cut, arriving);

        Assert.Empty(handler.ItemCreations);
        Assert.Empty(handler.Writes);
        Assert.Equal(2, handler.Items.Count);
        // And not one line either way: a seeded device is not a failure, and fifty-one lines saying
        // so would bury the one line that means something.
        Assert.Empty(cut.FindAll("#drawio-creation-report"));
    }

    /// <summary>
    /// The same trap, with the grip <b>on the filter that really holds it</b>: the cells of the
    /// seeding, but each one also declaring its type.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The test above is faithful to what the seeding sends today, and that is its limit: today
    /// <c>DrawioDocument.Build</c> writes no <c>wcsType</c>, so those cells are stopped by the
    /// SECOND filter - "the shape does not say which type it is" - before the guid filter is even
    /// asked. Measured by the reviewer of this batch: disabling the guid filter left that test red
    /// at the report line and green at the writes. Its grip on the filter the batch calls decisive
    /// is therefore indirect, and whoever adds <c>wcsType</c> to the seeding one day would move it
    /// without noticing.
    /// </para>
    /// <para>
    /// Here the type IS declared and is one the registry knows, so the only thing left between
    /// these cells and fifty-one duplicates is the guid filter. The type is taken from
    /// <see cref="Registry"/> and not written by hand: a code the registry does not declare would
    /// send the cells back to the other filter and bring the blindness back.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ASeededCellThatAlsoDeclaresItsTypeIsStillNotCreated()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);

        var declared = Registry()[0].NumericCode.ToString();
        var seeded = DrawioReader.Read(DrawioDocument.Build(GraphOf(handler), null)).Devices;
        Assert.Equal(2, seeded.Count);
        var arriving = seeded
            .Select(cell => new DrawioAddedCell(cell.Id, cell.Mark, declared,
                DrawioDocument.DefaultLayerId, cell.X, cell.Y, cell.Width, cell.Height))
            .ToArray();
        // Anchored on the way in as well: these cells carry everything a drop carries - the mark and
        // a type the registry declares - so a green here means the guid filter stopped them, and
        // nothing else could have.
        Assert.All(arriving, cell => Assert.Equal(DeviceMark, cell.Mark));
        Assert.All(arriving, cell => Assert.Contains(handler.Types, type => type.NumericCode.ToString() == cell.Type));
        Assert.All(arriving, cell => Assert.True(Guid.TryParse(cell.Id, out _)));

        await DropAsync(cut, arriving);

        Assert.Empty(handler.ItemCreations);
        Assert.Empty(handler.Writes);
        Assert.Equal(2, handler.Items.Count);
        Assert.Empty(cut.FindAll("#drawio-creation-report"));
    }

    /// <summary>The mark the panel's entries carry, as <c>DrawioEditor</c> knows it: repeated here as a literal because this test is about what a cell really carries.</summary>
    private const string DeviceMark = "device";

    private static PlantGraph GraphOf(DrawioWriteHandler handler) =>
        new([.. handler.Items], [.. handler.Junctions], [.. handler.Types]);

    // ---------------------------------------------------------------- the birth

    /// <summary>
    /// The gesture the batch exists for: the shape lands, the device is on the plant - with the type
    /// the entry declared and at the point where it was let go - and the page says so.
    /// </summary>
    [Fact]
    public async Task ADroppedShapeBecomesADeviceOnThePlant()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);

        // The SECOND type of the registry, not the first: with the first, a code read from anywhere
        // else - or defaulted - would come out right by accident.
        await DropAsync(cut, Dropped(Registry()[1], "drawio-1", 640, 320));

        var config = Assert.Single(handler.ItemCreations);
        Assert.Equal((short)DeviceTypes.Buffer, config.DeviceType);
        // With a SUGGESTED name (owner's decision of 2026-09-17): the TYPE's label - the one the
        // registry declares for that entry, "Buffer" and not "Automation" - plus the first free
        // number. The plant's own two devices are called "1111" and "2222", so 1 is free.
        Assert.Equal("Buffer 1", config.Name);

        var geometry = Assert.Single(handler.GeometryWrites);
        Assert.Equal(config.ItemId, geometry.ItemId);
        Assert.Equal(640, geometry.X!.Value, precision: 2);
        Assert.Equal(320, geometry.Y!.Value, precision: 2);
        Assert.Equal(DrawioLibrary.ThumbSize, geometry.Width!.Value, precision: 2);

        // The plant really grew, and the page's own graph knows it: it is what the lists, the
        // selection line and the next save's delta all read.
        Assert.Equal(3, handler.Items.Count);
        var created = Assert.Single(LinesOf(cut, "drawio-created"));
        Assert.Contains("Buffer", created);
        Assert.Contains(config.ItemId.ToString(), created);
        // And the line says the NAME, word for word: it is the only place the operator can read
        // which of the shapes on the canvas is which device, and the old line - "it has NO NAME
        // yet" - would now be a sentence that declares something false.
        Assert.Contains("\"Buffer 1\"", created);
        Assert.DoesNotContain("NO NAME", created);
        Assert.Empty(LinesOf(cut, "drawio-not-created"));
    }

    /// <summary>
    /// The device just created is a device of the plant <b>for this page too</b>, immediately: the
    /// selection draw.io reports carries draw.io's own identifier, and without the session map the
    /// line above the drawing would call it "one cell that is not a device of the plant" and the
    /// properties panel would not open at all - created and out of reach.
    /// </summary>
    [Fact]
    public async Task TheDeviceJustCreatedIsSelectableAndHasAPanel()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);

        await DropAsync(cut, Dropped(Registry()[0], "drawio-1", 100, 100));
        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged(["drawio-1"]));

        var created = Assert.Single(handler.ItemCreations);
        // And it is named by its NAME, not by a guid: the line reads a device's name when it has
        // one, and since the suggested name a device born from a drop has one from its first
        // instant. Before it, this line read "Selected: ac087e39-..." - true, and unreadable.
        Assert.Equal("Automation 1", created.Name);
        Assert.Equal("Selected: Automation 1", cut.Find("#drawio-selection").TextContent);
        Assert.NotEmpty(cut.FindAll("#drawio-property-panel"));
    }

    // ------------------------------------------------- the LABEL, from the very first instant

    /// <summary>
    /// <b>The heart of this batch.</b> The shape that has just become a device carries the device's
    /// NAME, not the type's label, without waiting for a reload - by the same road the panel's
    /// rename uses, with the CELL's identifier.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Measured by the owner in a real browser before the repair</b>: just created, the shape read
    /// "Group Automation" (the type); reopening the page it read "Group Automation 1" (the name).
    /// The plant and the drawing said two different things, and the creation's line spent two
    /// sentences explaining the difference instead of closing it.
    /// </para>
    /// <para>
    /// <b>The device is a CREATED one, and that is the whole point of the assertions</b>: its cell
    /// carries draw.io's own identifier and the guid is on no cell of this canvas, so a page that
    /// handed the bridge the guid - the defect that got through two reviews on the rename - goes red
    /// on the <c>NotEqual</c> here rather than staying green because the two strings happen to be
    /// the same, as they are for a seeded device.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheShapeJustDroppedCarriesTheDeviceNameAtOnce()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);

        await DropAsync(cut, Dropped(Registry()[0], "drawio-1", 100, 100));

        var born = Assert.Single(handler.ItemCreations);
        Assert.Equal("Automation 1", born.Name);

        var asked = Assert.Single(Relabels());
        Assert.Equal("drawio-1", asked.Arguments[0]);
        Assert.NotEqual(born.ItemId.ToString(), (string?)asked.Arguments[0]);
        // The NAME the plant really took, not a string rewritten here: a test that recomputed
        // "Automation 1" would be blind to a page that labelled the shape with something else.
        Assert.Equal(born.Name, asked.Arguments[1]);

        // And the line no longer explains a difference that is not there any more. The two sentences
        // that went are named as text because that is what the owner reads.
        var created = Assert.Single(LinesOf(cut, "drawio-created"));
        Assert.Contains("\"Automation 1\"", created);
        Assert.DoesNotContain("has not been touched", created);
        Assert.DoesNotContain("next time this page is opened", created);
        // Nor does it say the drawing was left behind: it was not.
        Assert.DoesNotContain("did not take", created);
        Assert.Empty(LinesOf(cut, "drawio-not-created"));
    }

    /// <summary>
    /// <b>The label of a creation is asked for as part of the drop's own gesture</b>, which is what
    /// makes ONE Ctrl+Z undo the whole thing: the third argument to the bridge is the flag that
    /// turns that label into an undoable edit draw.io's undo walks THROUGH.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is here and not only in the browser because bUnit is the only net that sees WHAT the page
    /// asks for: what that flag then does inside draw.io is measured by
    /// <c>DrawioShapesPanelTest.UndoingADropLeavesTheDeviceOnThePlantAndTheNextSaveTakesItAway</c>,
    /// which really presses Ctrl+Z once. Neither half is enough on its own - this one would stay
    /// green if the bridge ignored the argument, and that one would not say who asked for it.
    /// </para>
    /// <para>
    /// <b>Anchored on every cell of a paste</b>, not on the first: a page that asked for it once and
    /// then forgot would leave nineteen shapes costing a press each.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task EveryLabelOfACreationIsAskedForAsOneUndoWithTheShape()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);

        DrawioAddedCell[] dropped =
        [
            Dropped(Registry()[0], "drawio-1", 100, 100),
            Dropped(Registry()[0], "drawio-2", 200, 100),
            Dropped(Registry()[1], "drawio-3", 300, 100),
        ];
        await DropAsync(cut, dropped);

        Assert.Equal(dropped.Length, handler.ItemCreations.Count);
        var asked = Relabels();
        Assert.Equal(dropped.Length, asked.Count);
        Assert.All(asked, call => Assert.Equal(3, call.Arguments.Count));
        Assert.All(asked, call => Assert.Equal(true, call.Arguments[2]));
    }

    /// <summary>
    /// <b>A paste of many cells in ONE call</b> - which is how draw.io delivers it - gives every
    /// shape its own label, not the same one over and over.
    /// </summary>
    /// <remarks>
    /// <b>It is anchored to the number of cells</b> at three points - the devices written, the calls
    /// to the canvas, and the number of DISTINCT labels - so a page that relabelled only the first,
    /// or all of them with one name, cannot come out green. The expected labels are the names the
    /// plant really took, read back off the handler: rewriting the rule here would make the test
    /// blind to the one thing it is for.
    /// </remarks>
    [Fact]
    public async Task EveryShapeOfAPasteCarriesItsOwnName()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);

        DrawioAddedCell[] pasted =
            [.. Enumerable.Range(1, 5).Select(n => Dropped(Registry()[0], $"drawio-{n}", 100 * n, 200))];
        await DropAsync(cut, pasted);

        Assert.Equal(pasted.Length, handler.ItemCreations.Count);
        var asked = Relabels();
        Assert.Equal(pasted.Length, asked.Count);
        Assert.Equal(string.Join(" | ", pasted.Select(cell => cell.Id)), Cells(asked));
        Assert.Equal(string.Join(" | ", handler.ItemCreations.Select(config => config.Name)), Labels(asked));
        Assert.Equal(pasted.Length, asked.Select(call => (string?)call.Arguments[1]).Distinct().Count());
        Assert.Empty(LinesOf(cut, "drawio-not-created"));
    }

    /// <summary>
    /// <b>The label that does not reach the canvas</b> - the operator deleted the shape while the
    /// creation was in flight. The device IS on the plant: it is said, it is not taken back, and the
    /// other shapes of the same paste get their own labels all the same.
    /// </summary>
    /// <remarks>
    /// <b>The line stays a "Created" one</b>, and that is the point: calling it NOT created would be
    /// the false statement this page keeps having to take back - the device is there, under that
    /// name, and whoever reads "not created" would drag the shape again and end up with two.
    /// </remarks>
    [Fact]
    public async Task ALabelThatDoesNotReachTheCanvasIsSaidAndTheOtherShapesGoOn()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler, canvasRefuses: "drawio-2");

        await DropAsync(cut,
            Dropped(Registry()[0], "drawio-1", 100, 100),
            Dropped(Registry()[0], "drawio-2", 200, 100),
            Dropped(Registry()[0], "drawio-3", 300, 100));

        // All three are on the plant, and all three shapes were asked for.
        Assert.Equal(3, handler.ItemCreations.Count);
        Assert.Equal(5, handler.Items.Count);
        Assert.Equal("drawio-1 | drawio-2 | drawio-3", Cells(Relabels()));

        var created = LinesOf(cut, "drawio-created");
        Assert.Equal(3, created.Length);
        Assert.Empty(LinesOf(cut, "drawio-not-created"));
        Assert.Null(TextOrNull(cut, "#drawio-creation-error"));

        // One line, and only one, says the drawing did not follow - and it is the middle one.
        Assert.Single(created, line => line.Contains("did not take that name", StringComparison.Ordinal));
        Assert.Contains("did not take that name", created[1], StringComparison.Ordinal);
        // And it still names the device and the name it got: that is what the operator needs in
        // order to find it, and it is the half that did succeed.
        Assert.Contains(handler.ItemCreations[1].ItemId.ToString(), created[1], StringComparison.Ordinal);
        Assert.Contains($"\"{handler.ItemCreations[1].Name}\"", created[1], StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The call to the canvas that FAILS</b>, rather than answering no. It costs one line and not
    /// the rest of the paste: before this, an exception from the bridge would have left the belt of
    /// <c>DrainCreationsAsync</c> refusing the shapes that had not had their turn - devices already
    /// on the plant, declared as never born.
    /// </summary>
    /// <remarks>
    /// It can fail: take the catch out of the relabel and the third shape below never gets its
    /// label, while the report blames the failure on a creation that went through.
    /// </remarks>
    [Fact]
    public async Task ACallToTheCanvasThatFailsCostsOneLineAndNotTheRestOfThePaste()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler, canvasThrowsFor: "drawio-2");

        await DropAsync(cut,
            Dropped(Registry()[0], "drawio-1", 100, 100),
            Dropped(Registry()[0], "drawio-2", 200, 100),
            Dropped(Registry()[0], "drawio-3", 300, 100));

        Assert.Equal(3, handler.ItemCreations.Count);
        Assert.Equal("drawio-1 | drawio-2 | drawio-3", Cells(Relabels()));

        var created = LinesOf(cut, "drawio-created");
        Assert.Equal(3, created.Length);
        Assert.Empty(LinesOf(cut, "drawio-not-created"));
        Assert.Null(TextOrNull(cut, "#drawio-creation-error"));

        // The failure is carried through: "the drawing could not be told" without saying what happened
        // sends whoever reads it back to the beginning. Why is in the sentence in the page's words, and
        // what the script threw, word for word, behind the line's "?" (row 37 of the queue, extended to
        // the whole page on 10/10).
        Assert.Single(created, line => line.Contains(CanvasFailure, StringComparison.Ordinal));
        Assert.EndsWith(" The drawing could not be told, because the page's script failed: the shape keeps the label it had until the page is reloaded.",
            cut.Find("#drawio-created-1").TextContent, StringComparison.Ordinal);
        Assert.Equal($"JSException: {CanvasFailure}", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-created-1"));
    }

    /// <summary>
    /// <b>The line whose label did not reach the drawing can be FOUND</b>, and that is the review's
    /// finding: the sentence about it is appended to a line with the same class and the same look as
    /// the other nineteen of a paste, so on screen it is indistinguishable. Whoever is about to save
    /// has to be able to see which one did not take its name without rereading twenty lines.
    /// </summary>
    /// <remarks>
    /// <b>It is anchored on both sides</b>: exactly one of the three lines carries the mark, so a
    /// page that marked them all - or none - goes red, and the marked one is the same line that
    /// carries the sentence. And it stays inside <c>li.drawio-created</c>: moving the failure in
    /// among the "NOT created" would be the false statement this page keeps having to take back.
    /// </remarks>
    [Fact]
    public async Task TheLineWhoseLabelMissedTheDrawingIsMarkedOutFromTheOthers()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler, canvasRefuses: "drawio-2");

        await DropAsync(cut,
            Dropped(Registry()[0], "drawio-1", 100, 100),
            Dropped(Registry()[0], "drawio-2", 200, 100),
            Dropped(Registry()[0], "drawio-3", 300, 100));

        Assert.Equal(3, LinesOf(cut, "drawio-created").Length);
        var marked = Assert.Single(LinesOf(cut, "drawio-created-label-missed"));
        Assert.Contains("did not take that name", marked, StringComparison.Ordinal);
        Assert.Contains(handler.ItemCreations[1].ItemId.ToString(), marked, StringComparison.Ordinal);
        // The mark is on a birth, not on a failure: the device is on the plant.
        Assert.Empty(LinesOf(cut, "drawio-not-created"));
        Assert.Equal(3, handler.ItemCreations.Count);
    }

    /// <summary>
    /// The same page after a relabel that went through must carry <b>no</b> mark at all: without
    /// this half, a page that marked every line would still pass the test above's "exactly one",
    /// only because one line in three happens to be one.
    /// </summary>
    [Fact]
    public async Task ALabelThatDidReachTheDrawingLeavesNoMarkOnItsLine()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);

        await DropAsync(cut,
            Dropped(Registry()[0], "drawio-1", 100, 100),
            Dropped(Registry()[0], "drawio-2", 200, 100));

        Assert.Equal(2, LinesOf(cut, "drawio-created").Length);
        Assert.Empty(LinesOf(cut, "drawio-created-label-missed"));
    }

    // ------------------------------------------------- and it can be RENAMED, drawing included

    /// <summary>
    /// <b>The defect the owner found, using the product.</b> A device born from a DROP is renamed in
    /// the panel, and the canvas has to take the new label: the identifier handed to the bridge must
    /// be the <b>cell's</b> - draw.io's own - because that is the only one
    /// <c>mxGraphModel.getCell</c> answers to. The device's guid is on no cell of this canvas.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It can fail, and before the repair it did</b>: the page handed the bridge the guid,
    /// <c>getCell</c> found nothing, and the page declared "the cell is no longer there" about a cell
    /// that had never left the canvas - the plant renamed, the drawing left behind, and a stated
    /// reason that was false.
    /// </para>
    /// <para>
    /// <b>Why two reviews went past it</b>: every rename test there was used a SEEDED device, whose
    /// cell id <i>is</i> its guid (<c>DrawioDocument</c> writes it as the <c>&lt;object id&gt;</c>).
    /// On a seeded device the wrong identifier and the right one are the same string, so no assertion
    /// could tell them apart. Hence the <c>NotEqual</c> against the guid below: here the two are
    /// different strings by construction, and a page that went on passing the guid goes red.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task RenamingADeviceBornFromADropReachesTheCanvasWithTheCellsOwnIdentifier()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);

        await DropAsync(cut, Dropped(Registry()[0], "drawio-1", 100, 100));
        var born = Assert.Single(handler.ItemCreations).ItemId;
        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged(["drawio-1"]));
        Assert.NotEmpty(cut.FindAll("#drawio-property-panel"));

        Type(cut, nameof(PlantItemConfigDto.Name), "nome-nuovo");
        await ConfirmAsync(cut);

        // The plant took it: that half was never the defect, and it must stay true. The rename goes
        // out on the PUT of the configuration, not on the POST that created the device: two routes
        // since the 404 batch, and the fake host keeps the two lists apart.
        Assert.Equal("nome-nuovo", handler.ConfigWrites[^1].Name);
        Assert.Equal(born, handler.ConfigWrites[^1].ItemId);

        // And the canvas was asked for the CELL. The guid is what draw.io's model does not know, and
        // asking for it is what made the page say something false. Two calls, not one, since the
        // label at birth: the drop labels the shape with the suggested name, the panel renames it.
        var asked = Relabels();
        Assert.Equal(2, asked.Count);
        Assert.Equal("drawio-1 | drawio-1", Cells(asked));
        Assert.Equal("Automation 1 | nome-nuovo", Labels(asked));
        Assert.NotEqual(born.ToString(), (string?)asked[^1].Arguments[0]);

        // And nothing is declared: the drawing is NOT behind, so the line saying it is would be
        // exactly the lie this test exists for.
        Assert.Empty(cut.FindAll("#drawio-property-write-error"));
    }

    /// <summary>
    /// The other side of the same translation, and the one that could rot quietly: a SEEDED device
    /// renamed while the session map already holds a dropped one must still be asked for by its own
    /// guid, which is its cell's id. A reverse lookup answering with whatever entry it found first
    /// would relabel the wrong shape, and the test above would stay green through it.
    /// </summary>
    [Fact]
    public async Task RenamingASeededDeviceStillAsksForItsOwnIdentifierAfterADrop()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);

        // A drop first, so the map is NOT empty: that is the state in which a translation has
        // something wrong to answer.
        await DropAsync(cut, Dropped(Registry()[0], "drawio-1", 100, 100));
        Assert.Single(handler.ItemCreations);

        // And now the seeded device, selected by the identifier the seeding wrote on its cell.
        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([A.ToString()]));
        Type(cut, nameof(PlantItemConfigDto.Name), "nome-del-seminato");
        await ConfirmAsync(cut);

        Assert.Equal(A, handler.ConfigWrites[^1].ItemId);
        var asked = Relabels();
        Assert.Equal(2, asked.Count);
        Assert.Equal($"drawio-1 | {A}", Cells(asked));
        Assert.Equal("Automation 1 | nome-del-seminato", Labels(asked));
        Assert.Empty(cut.FindAll("#drawio-property-write-error"));
    }

    /// <summary>
    /// <b>The second rename of the same dropped device</b>, which the previous batch left as an open
    /// reserve ("the browser test renames only once"). The session map is not touched by a rename,
    /// so the second one must reach the canvas exactly like the first - and with the CELL's
    /// identifier both times.
    /// </summary>
    /// <remarks>
    /// It can fail: a translation that consumed the pair, or a panel that lost its subject after a
    /// successful write, would leave the second rename going out with the guid again, or not going
    /// out at all. The count of the calls is asserted before their contents, so "it was never asked"
    /// fails by name instead of passing through <c>Single</c>.
    /// </remarks>
    [Fact]
    public async Task RenamingTheSameDroppedDeviceASecondTimeStillReachesTheCanvas()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);

        await DropAsync(cut, Dropped(Registry()[0], "drawio-1", 100, 100));
        var born = Assert.Single(handler.ItemCreations).ItemId;
        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged(["drawio-1"]));

        Type(cut, nameof(PlantItemConfigDto.Name), "primo-nome");
        await ConfirmAsync(cut);
        Type(cut, nameof(PlantItemConfigDto.Name), "secondo-nome");
        await ConfirmAsync(cut);

        var asked = Relabels();
        Assert.Equal(3, asked.Count);
        Assert.Equal("drawio-1 | drawio-1 | drawio-1", Cells(asked));
        Assert.Equal("Automation 1 | primo-nome | secondo-nome", Labels(asked));

        // And the plant took the second one, on the same device: a rename that reached the canvas
        // and not the plant would be the same lie the other way round.
        Assert.Equal(born, handler.ConfigWrites[^1].ItemId);
        Assert.Equal("secondo-nome", handler.ConfigWrites[^1].Name);
        Assert.Empty(cut.FindAll("#drawio-property-write-error"));
    }

    /// <summary>
    /// <b>A rename after a CUT AND PASTE</b>, the other reserve the previous batch left open. It is
    /// the gesture in which the identity really changes: the cut cell is gone and the pasted one is
    /// a different cell, which becomes a different device. Renaming it must reach the PASTED cell.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cut itself is invisible to this page - a removal reaches the plant only at the save - so
    /// what is simulated here is its visible half: a new cell arriving with an identifier of its own.
    /// The device it becomes is asserted to be a DIFFERENT one from the device of the cut cell,
    /// otherwise the rename below would be measuring the first device and would pass through a page
    /// that had mixed the two up.
    /// </para>
    /// <para>
    /// And the suggested name comes into it: the pasted device is "Automation 2", because
    /// "Automation 1" - the one that is on its way out - is still on the plant. That is right rather
    /// than unfortunate: until the save the two really do coexist.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task RenamingADeviceThatCameFromACutAndPasteReachesThePastedCell()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);

        await DropAsync(cut, Dropped(Registry()[0], "drawio-1", 100, 100));
        var first = Assert.Single(handler.ItemCreations).ItemId;

        // The paste: draw.io gives the pasted cell an identifier of its own, so the session map does
        // not recognize it and a second device is born.
        await DropAsync(cut, Dropped(Registry()[0], "drawio-2", 140, 140));
        Assert.Equal(2, handler.ItemCreations.Count);
        var pasted = handler.ItemCreations[^1].ItemId;
        Assert.NotEqual(first, pasted);
        Assert.Equal("Automation 2", handler.ItemCreations[^1].Name);

        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged(["drawio-2"]));
        Type(cut, nameof(PlantItemConfigDto.Name), "nome-dell-incollato");
        await ConfirmAsync(cut);

        // Three calls: one label at each of the two births, then the rename. The rename is the LAST,
        // and it is the pasted cell's.
        var asked = Relabels();
        Assert.Equal(3, asked.Count);
        Assert.Equal("drawio-1 | drawio-2 | drawio-2", Cells(asked));
        Assert.Equal("Automation 1 | Automation 2 | nome-dell-incollato", Labels(asked));
        Assert.NotEqual(pasted.ToString(), (string?)asked[^1].Arguments[0]);

        // The PASTED device, not the one on its way out.
        Assert.Equal(pasted, handler.ConfigWrites[^1].ItemId);
        Assert.Equal("nome-dell-incollato", handler.ConfigWrites[^1].Name);
        Assert.Empty(cut.FindAll("#drawio-property-write-error"));
    }

    private static void Type(IRenderedComponent<DrawioEditor> cut, string propertyName, string value) =>
        cut.Find($"#drawio-property-panel div.property-panel-field[data-property='{propertyName}'] input").Change(value);

    private static Task ConfirmAsync(IRenderedComponent<DrawioEditor> cut) =>
        cut.Find("#property-panel-save-button").ClickAsync(new MouseEventArgs());

    // ---------------------------------------------------------------- what stops a birth, and says so

    [Fact]
    public async Task WithoutTheEditPermitNothingIsCreatedAndItIsSaid()
    {
        var handler = HostWithTwoDevices();
        handler.GrantEditLock = false;
        var cut = await RenderWithAsync(handler);

        await DropAsync(cut, Dropped(Registry()[0], "drawio-1", 100, 100));

        Assert.Empty(handler.ItemCreations);
        Assert.Empty(handler.GeometryWrites);
        var error = cut.Find("#drawio-creation-error").TextContent;
        Assert.Contains("Nothing was created", error);
        Assert.Contains("somebody else", error);
        Assert.Contains("drawio-1", Assert.Single(LinesOf(cut, "drawio-not-created")));
    }

    /// <summary>
    /// Row 37 of the queue (the owner's answer of 9/10): a permit that could not be asked for is said
    /// in the creation's report as in the properties panel - with its why in the sentence, and what
    /// came back behind the line's "?", where the sentence used to quote the failure's message.
    /// </summary>
    [Fact]
    public async Task APermitThatCannotBeAskedForIsSaidWithTheFailureBehindTheQuestionMark()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);
        handler.HttpFailureRoute = "edit-lock";

        await DropAsync(cut, Dropped(Registry()[0], "drawio-1", 100, 100));

        Assert.Empty(handler.ItemCreations);
        Assert.Equal(
            "Nothing was created. The edit permit could not be asked for because the connection to the host failed: nothing was written. " +
            "The shapes are on the canvas and the plant is untouched: delete them and drag them again once this is sorted out.",
            cut.Find("#drawio-creation-error").TextContent.Trim());
        Assert.StartsWith("HttpRequestException: ", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-creation-error"));
    }

    /// <summary>
    /// Owner's decision of 27/9 on Pause: a device is created only with the engine stopped - in Pause
    /// it would be born without its connection, which resuming does not give it. The page refuses at the
    /// drop, before anything reaches the host, and says why.
    /// </summary>
    [Fact]
    public async Task WithTheEnginePausedNothingIsCreatedAndTheReasonIsSaid()
    {
        var handler = HostWithTwoDevices();
        handler.NavStatus = NavigatorStatusTypes.Pause;
        var cut = await RenderWithAsync(handler);

        await DropAsync(cut, Dropped(Registry()[0], "drawio-1", 100, 100));

        Assert.Empty(handler.ItemCreations);
        Assert.Contains(
            "The engine is paused, and a device is created only with the engine stopped: it would be born without its connection - the engine gives devices the simulator or the PLC only when it opens the connections, and resuming from Pause does not open them again.",
            cut.Find("#drawio-creation-error").TextContent);
        Assert.Contains("drawio-1", Assert.Single(LinesOf(cut, "drawio-not-created")));
    }

    [Fact]
    public async Task WithTheEngineRunningNothingIsCreatedAndItIsSaid()
    {
        var handler = HostWithTwoDevices();
        handler.NavStatus = NavigatorStatusTypes.Run;
        var cut = await RenderWithAsync(handler);

        await DropAsync(cut, Dropped(Registry()[0], "drawio-1", 100, 100));

        Assert.Empty(handler.ItemCreations);
        Assert.Contains("the engine is running", cut.Find("#drawio-creation-error").TextContent);
        Assert.Contains("drawio-1", Assert.Single(LinesOf(cut, "drawio-not-created")));
    }

    /// <summary>
    /// The host refuses the write: nothing is created, it is said, and - this is the part that
    /// matters beyond the message - the cell is NOT remembered as a device. At the next save it is a
    /// marked cell the plant does not know, which the save names and does NOT create: one creation,
    /// one road.
    /// </summary>
    [Fact]
    public async Task AHostThatRefusesLeavesTheCellADrawingAndTheSaveDoesNotCreateItEither()
    {
        var handler = HostWithTwoDevices();
        handler.FailRoute = "item-post";
        var cut = await RenderWithAsync(handler);

        await DropAsync(cut, Dropped(Registry()[0], "drawio-1", 100, 100));

        Assert.Equal(2, handler.Items.Count);
        Assert.Contains("drawio-1", Assert.Single(LinesOf(cut, "drawio-not-created")));

        // The save that follows: the drawing carries the plant plus the refused cell.
        var drawing = WithExtraCell(DrawioDocument.Build(GraphOf(handler), null),
            $"""<object id="drawio-1" label="Automation" wcs="device" {DrawioLibrary.TypeAttribute}="0"><mxCell style="whatever" vertex="1" parent="1"><mxGeometry x="100" y="100" width="80" height="80" as="geometry"/></mxCell></object>""");
        await cut.InvokeAsync(() => cut.Instance.OnDrawioSaved(drawing));

        // No device was born from the save, and the cell was named rather than dropped in silence.
        Assert.Equal(2, handler.Items.Count);
        var refusal = Assert.Single(LinesOf(cut, "drawio-refusal"));
        Assert.Contains("drawio-1", refusal);
        Assert.Contains("dropped on the canvas", refusal);
        // And it does not send anybody to /editor any more.
        Assert.DoesNotContain("/editor", refusal);
    }

    /// <summary>
    /// Confirmation review, M-E: a 409 on the creation that is not the engine in Run - here Recover -
    /// is never said as "the engine is in Run". Row 37 of the queue, extended to the whole page on
    /// 10/10: the line says what a 409 means on the route, and the host's own words are behind its "?"
    /// with the rest of the answer, not in the sentence.
    /// </summary>
    [Fact]
    public async Task AHostRefusalOfTheCreationIsSaidInTheHostsWords()
    {
        var handler = HostWithTwoDevices();
        handler.FailRoute = "item-post";
        handler.FailureStatus = System.Net.HttpStatusCode.Conflict;
        handler.FailureBody = System.Text.Json.JsonSerializer.Serialize(new
        {
            title = "Il motore non è fermo",
            status = 409,
            detail = "creazione del dispositivo: il motore è in Recover",
        });
        var cut = await RenderWithAsync(handler);

        await DropAsync(cut, Dropped(Registry()[0], "drawio-1", 100, 100));

        Assert.Single(LinesOf(cut, "drawio-not-created"));
        var line = cut.Find("#drawio-not-created-0").TextContent;
        Assert.StartsWith("NOT created: drawio-1: the device was not created, or not completely, because the host refused it - the plant's state does not allow it now: if any part of it got through it is ", line);
        Assert.DoesNotContain("in Run", line);
        Assert.DoesNotContain("Il motore", line);
        Assert.Equal(
            "the host answered 409: {\"title\":\"Il motore non \\u00E8 fermo\",\"status\":409,\"detail\":\"creazione del dispositivo: il motore \\u00E8 in Recover\"}",
            DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-not-created-0"));
    }

    private static string WithExtraCell(string document, string cellXml) =>
        document.Replace("</root>", cellXml + "</root>");

    /// <summary>
    /// A marked cell that does not say which type it is (written by hand with Extras &gt; Edit
    /// Diagram, or copied from somewhere that did not carry the attribute): not created, and the
    /// type is not guessed from how it looks - the style and the label are both the drawer's to
    /// change.
    /// </summary>
    [Fact]
    public async Task ACellThatDoesNotDeclareItsTypeIsNotCreated()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);

        var withoutType = Dropped(Registry()[0], "drawio-1", 100, 100) with { Type = null };
        await DropAsync(cut, withoutType);

        Assert.Empty(handler.ItemCreations);
        var line = Assert.Single(LinesOf(cut, "drawio-not-created"));
        Assert.Contains(DrawioLibrary.TypeAttribute, line);
    }

    /// <summary>A type the registry does not declare: the same answer, because a device of a type the engine does not know would be drawn nowhere.</summary>
    [Fact]
    public async Task ACellDeclaringATypeTheRegistryDoesNotHaveIsNotCreated()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);

        await DropAsync(cut, Dropped(Registry()[0], "drawio-1", 100, 100) with { Type = "99" });

        Assert.Empty(handler.ItemCreations);
        Assert.Contains("99", Assert.Single(LinesOf(cut, "drawio-not-created")));
    }

    /// <summary>
    /// Dropped INSIDE a group: there draw.io writes the position relative to the group, so the
    /// device would be born somewhere else entirely. It is the same fact <c>DrawioDelta</c> refuses
    /// a grouped cell's move for, measured in a real browser on 2026-09-16.
    /// </summary>
    [Fact]
    public async Task AShapeDroppedInsideAGroupIsNotCreated()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);

        await DropAsync(cut, Dropped(Registry()[0], "drawio-1", 100, 100) with { Parent = "gruppo-7" });

        Assert.Empty(handler.ItemCreations);
        Assert.Contains("group", Assert.Single(LinesOf(cut, "drawio-not-created")));
    }

    /// <summary>
    /// On a SECOND LAYER, which draw.io allows and which is not a group: the cell is refused all the
    /// same - the save holds the same single-layer rule and would reject its every move for the rest
    /// of its life - but the line must not name a group that is not there.
    /// </summary>
    /// <remarks>
    /// Measured in a real browser on 2026-09-17, with a second layer made the active one exactly as
    /// draw.io's own Layers window does it (<c>graph.setDefaultParent</c>): the dropped shape came
    /// back with <c>parent</c> = the layer's identifier and a box at x=460, next to two cells of
    /// layer 1 at x=460 - that is ABSOLUTE, not relative like a group's. The old line told whoever
    /// read it to "drop it on the canvas, outside any group", which on a second layer is advice
    /// about something that is not happening.
    /// </remarks>
    [Fact]
    public async Task AShapeOnASecondLayerIsRefusedWithoutBeingCalledAGroup()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);

        await DropAsync(cut, Dropped(Registry()[0], "drawio-1", 100, 100) with { Parent = "2" });

        Assert.Empty(handler.ItemCreations);
        var line = Assert.Single(LinesOf(cut, "drawio-not-created"));
        Assert.Contains("layer 1", line);
        Assert.Contains("\"2\"", line);
        // It may still SAY that a group is one of the two cases - it cannot know which - but it must
        // not assert it: the remedy of the old line was "outside any group", full stop.
        Assert.DoesNotContain("Drop it on the canvas, outside any group", line);
    }

    // ---------------------------------------------------------------- what the report survives

    /// <summary>
    /// A cell that asks for nothing - a note, a box, an ARROW just drawn - must not wipe the report
    /// of the gesture before it. The save's refusal points at that report in writing ("the reason is
    /// in the creation report at the top of the page"), and before this was fixed it pointed at an
    /// empty place: measured, and measured in a real browser too (a Ctrl+G was enough).
    /// </summary>
    [Fact]
    public async Task ACellThatAsksForNothingDoesNotWipeTheReportOfTheGestureBefore()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);
        handler.FailRoute = "item-post";

        await DropAsync(cut, Dropped(Registry()[0], "drawio-1", 100, 100));
        var failed = Assert.Single(LinesOf(cut, "drawio-not-created"));
        Assert.Contains("drawio-1", failed);

        // The most natural next gesture of all: an arrow.
        await DropAsync(cut, new DrawioAddedCell("freccia-1", null, null, DrawioDocument.DefaultLayerId, 10, 10, 120, 0));

        Assert.NotEmpty(cut.FindAll("#drawio-creation-report"));
        Assert.Equal(failed, Assert.Single(LinesOf(cut, "drawio-not-created")));
    }

    /// <summary>
    /// And the other half, without which the rule above would be "never clear anything": a gesture
    /// that DOES ask for a device replaces the report, instead of piling its lines onto the ones of
    /// a gesture nobody is looking at any more.
    /// </summary>
    [Fact]
    public async Task AGestureThatAsksForADeviceReplacesTheReport()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);
        handler.FailRoute = "item-post";

        await DropAsync(cut, Dropped(Registry()[0], "drawio-1", 100, 100));
        Assert.Single(LinesOf(cut, "drawio-not-created"));

        await DropAsync(cut, Dropped(Registry()[0], "drawio-2", 200, 100));

        // The refusal of the first gesture is gone, and what is there describes THIS one.
        Assert.Empty(LinesOf(cut, "drawio-not-created"));
        var born = Assert.Single(handler.ItemCreations);
        Assert.Contains(born.ItemId.ToString(), Assert.Single(LinesOf(cut, "drawio-created")));
    }

    // ------------------------------------------------------------- a cell with no identifier at all

    /// <summary>
    /// <b>A shape asking to become a device with an EMPTY identifier is refused by name</b> - twice,
    /// once for each such cell. The bridge falls back to <c>''</c> when a cell has no <c>id</c>, and
    /// it has really met a null one (on the temporary parent a paste arrives from). Nobody has shown
    /// a CHILD can arrive that way, and this is written for the day one does: taken in, the first
    /// would sit in the session map under <c>""</c> - a device no rename could ever reach again -
    /// and the second would overwrite it there, leaving one of the two with no shape and, on a later
    /// gesture, no line at all.
    /// </summary>
    /// <remarks>
    /// <b>Two empty cells and not one</b>, because one proves nothing about the shape of the loss:
    /// the whole point is that the pair used to end up sharing a single key. The third cell is the
    /// anchor - the gesture has to go on and really create it, or this test would also pass on a
    /// page that refused the whole batch.
    /// </remarks>
    [Fact]
    public async Task ACellWithNoIdentifierIsRefusedByNameAndSoIsTheSecondOne()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);

        await DropAsync(cut,
            Dropped(Registry()[0], "", 100, 100),
            Dropped(Registry()[0], "", 200, 100),
            Dropped(Registry()[0], "drawio-3", 300, 100));

        // One line each, and each one says WHY: a cell nobody can name is a cell nobody looks at.
        var refused = LinesOf(cut, "drawio-not-created");
        Assert.Equal(2, refused.Length);
        Assert.All(refused, line => Assert.Contains("WITHOUT AN IDENTIFIER", line, StringComparison.Ordinal));

        // And the plant saw exactly the one cell that could be tied back to a shape.
        Assert.Single(handler.ItemCreations);
        Assert.Equal(3, handler.Items.Count);
        Assert.Single(LinesOf(cut, "drawio-created"));
        Assert.Equal("drawio-3", Assert.Single(Relabels()).Arguments[0]);
    }

    // ---------------------------------------------------------------- an answer that is not JSON

    /// <summary>
    /// An intermediary answering 200 with an HTML page instead of JSON: <c>ReadFromJsonAsync</c>
    /// throws a <c>JsonException</c> that <c>WcsClient</c> does not wrap, and no filter on this page
    /// used to name it. The cell it hits gets a line - and <b>the rest of the queue is still
    /// served</b>, which is the part that used to be lost in silence: those cells had already been
    /// taken out of <c>_pendingCreations</c>, so the exception carried them away with it.
    /// </summary>
    [Fact]
    public async Task AnAnswerThatIsNotJsonCostsOneCellAndNotTheRestOfTheQueue()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);
        // Armed AFTER the opening, and on the reread of ONE device: it is the last of the three
        // calls CreateItemCommand makes, so the first cell's two writes have really gone through.
        handler.GarbledRoute = "item";

        await DropAsync(cut,
            Dropped(Registry()[0], "drawio-1", 100, 100),
            Dropped(Registry()[0], "drawio-2", 200, 100),
            Dropped(Registry()[0], "drawio-3", 300, 100));

        var refused = Assert.Single(LinesOf(cut, "drawio-not-created"));
        Assert.Contains("drawio-1", refused);
        Assert.Equal(2, LinesOf(cut, "drawio-created").Length);
        Assert.Equal(3, handler.ItemCreations.Count);
        Assert.Equal(5, handler.Items.Count);
    }

    /// <summary>
    /// The same answer on the PERMIT, which is asked for before any cell has had its turn: there the
    /// exception used to leave <c>OnDrawioCellsAdded</c> altogether - a rejected JS promise nobody
    /// awaits - and every cell of the gesture disappeared without a line. Now the page says it, and
    /// names them all.
    /// </summary>
    /// <remarks>
    /// The test would fail by THROWING if the belt were not there: <c>DropAsync</c> awaits the
    /// <c>[JSInvokable]</c> method, so "no exception leaves this method" is measured here rather
    /// than declared in a comment.
    /// </remarks>
    [Fact]
    public async Task AnAnswerThatIsNotJsonOnThePermitNamesEveryCellInstead()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);
        handler.GarbledRoute = "edit-lock";

        await DropAsync(cut,
            Dropped(Registry()[0], "drawio-1", 100, 100),
            Dropped(Registry()[0], "drawio-2", 200, 100));

        Assert.Empty(handler.ItemCreations);
        Assert.Equal(2, handler.Items.Count);
        var lines = LinesOf(cut, "drawio-not-created");
        Assert.Equal(2, lines.Length);
        Assert.Contains(lines, line => line.Contains("drawio-1"));
        Assert.Contains(lines, line => line.Contains("drawio-2"));
        Assert.NotNull(TextOrNull(cut, "#drawio-creation-error"));
    }

    /// <summary>
    /// The item was created, but its first authoritative read fails. Geometry must not be sent with
    /// an invented version: the server token is deliberately opaque and only that read can provide
    /// it. The failed drop remains visible in the report.
    /// </summary>
    [Fact]
    public async Task WhenTheCreatedItemCannotBeReadGeometryIsNotSentWithAnInventedVersion()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);
        handler.HideSingleItemReads = true;

        await DropAsync(cut, Dropped(Registry()[0], "drawio-1", 100, 100));

        Assert.Single(handler.ItemCreations);
        Assert.Empty(handler.GeometryWrites);
        var born = Assert.Single(handler.Items, item => item.ItemId != A && item.ItemId != B);
        Assert.NotEqual(Guid.Empty, born.GeometryVersion);

        var line = Assert.Single(LinesOf(cut, "drawio-not-created"));
        Assert.Contains(born.ItemId.ToString(), line);
    }

    /// <summary>
    /// A cell that already carries an identifier of the plant's own kind which the plant does not
    /// know - deleted by somebody else while the canvas still showed it, or typed in by hand. Not
    /// created: it would come back with a DIFFERENT identifier, and everything that pointed at the
    /// old one would go on pointing at nothing.
    /// </summary>
    [Fact]
    public async Task ACellCarryingAnUnknownPlantIdentifierIsNotCreated()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);

        var ghost = Guid.NewGuid().ToString();
        await DropAsync(cut, Dropped(Registry()[0], ghost, 100, 100));

        Assert.Empty(handler.ItemCreations);
        Assert.Contains(ghost, Assert.Single(LinesOf(cut, "drawio-not-created")));
    }

    /// <summary>A cell with no mark of ours - a note, a box, an arrow just drawn - asks for nothing, and nothing is said about it.</summary>
    [Fact]
    public async Task AnUnmarkedCellCreatesNothingAndIsNotEvenMentioned()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);

        await DropAsync(cut, new DrawioAddedCell("nota-1", null, null, DrawioDocument.DefaultLayerId, 10, 10, 120, 60));

        Assert.Empty(handler.ItemCreations);
        Assert.Empty(cut.FindAll("#drawio-creation-report"));
    }

    /// <summary>
    /// draw.io's redo (Ctrl+Y after an undo) puts the very same cell back into the model, with the
    /// same identifier, and the listener reports it again exactly as it reported the drop. The
    /// session map is what makes the second report a no-op instead of a second device.
    /// </summary>
    [Fact]
    public async Task TheSameCellReportedTwiceCreatesOneDeviceOnly()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);
        var cell = Dropped(Registry()[0], "drawio-1", 100, 100);

        await DropAsync(cut, cell);
        await DropAsync(cut, cell);

        Assert.Single(handler.ItemCreations);
        Assert.Equal(3, handler.Items.Count);
        // The second report says nothing, because there is nothing to say: the device is there.
        Assert.Empty(LinesOf(cut, "drawio-not-created"));
    }

    // ---------------------------------------------------------------- several at once

    /// <summary>
    /// A multiple paste: the bridge reports the whole committed edit in ONE call, so twenty cells
    /// arrive together. Twenty devices are born, the permit is asked for ONCE for the whole gesture,
    /// and not one of them is refused with a sentence about another.
    /// </summary>
    [Fact]
    public async Task TwentyCellsPastedAtOnceBecomeTwentyDevicesUnderOnePermit()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);
        var before = handler.EditLockRequests;

        var pasted = Enumerable.Range(0, 20)
            .Select(i => Dropped(Registry()[i % 2], $"drawio-{i}", 100 + 10 * i, 100))
            .ToArray();
        await DropAsync(cut, pasted);

        Assert.Equal(20, handler.ItemCreations.Count);
        Assert.Equal(20, handler.GeometryWrites.Count);
        Assert.Equal(22, handler.Items.Count);
        Assert.Equal(20, LinesOf(cut, "drawio-created").Length);
        Assert.Empty(LinesOf(cut, "drawio-not-created"));
        // One permit for the gesture, not twenty: see DrainCreationsAsync.
        Assert.Equal(before + 1, handler.EditLockRequests);
        // The twenty are twenty different devices, not the same one written twenty times.
        Assert.Equal(20, handler.ItemCreations.Select(c => c.ItemId).Distinct().Count());
    }

    /// <summary>
    /// Two drops one after the other, the second arriving while the first is still writing: this is
    /// the case the QUEUE exists for. Without it the second would find the gate closed and be refused
    /// - a shape on the canvas, no device on the plant, and nothing to do about it but delete the
    /// shape and drag it again.
    /// </summary>
    /// <remarks>
    /// The first creation is held on the reread <c>CreateItemCommand</c> ends with
    /// (<c>GET /plant/items/{id}</c>), which is inside the write and after the gate has been taken:
    /// the second call really does arrive in the middle, it is not a sequence dressed up as a race.
    /// </remarks>
    [Fact]
    public async Task ASecondDropArrivingDuringTheFirstIsQueuedAndNotRefused()
    {
        var handler = HostWithTwoDevices();
        handler.SingleItemGate = new TaskCompletionSource();
        var cut = await RenderWithAsync(handler);

        var first = cut.InvokeAsync(() => cut.Instance.OnDrawioCellsAdded([Dropped(Registry()[0], "drawio-1", 100, 100)]));
        await handler.SingleItemGateReached.Task;

        // The second one, while the first is demonstrably inside its own write.
        await DropAsync(cut, Dropped(Registry()[1], "drawio-2", 300, 100));

        handler.SingleItemGate.SetResult();
        await first;

        Assert.Equal(2, handler.ItemCreations.Count);
        Assert.Equal(4, handler.Items.Count);
        Assert.Equal(2, LinesOf(cut, "drawio-created").Length);
        Assert.Empty(LinesOf(cut, "drawio-not-created"));
        Assert.Null(TextOrNull(cut, "#drawio-creation-error"));
    }

    [Fact]
    public async Task CreationStateNamesTheWriterUntilTheCreationFinallyReleasesIt()
    {
        var handler = HostWithTwoDevices();
        handler.SingleItemGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cut = await RenderWithAsync(handler);

        var drop = DropAsync(cut, Dropped(Registry()[0], "new-cell", 70, 90));
        await handler.SingleItemGateReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("creating", cut.Find("#drawio-creation-state").TextContent.Trim());
        handler.SingleItemGate.SetResult();
        await drop;
        Assert.Equal("idle", cut.Find("#drawio-creation-state").TextContent.Trim());
    }

    [Fact]
    public async Task FailedCreationReturnsTheCreationStateToIdle()
    {
        var handler = HostWithTwoDevices();
        handler.SingleItemFailure = new InvalidOperationException("write failed");
        var cut = await RenderWithAsync(handler);

        await DropAsync(cut, Dropped(Registry()[0], "new-cell", 70, 90));

        Assert.Equal("idle", cut.Find("#drawio-creation-state").TextContent.Trim());
        Assert.Contains("write failed", cut.Find("#drawio-creation-report").TextContent);
    }

    // ---------------------------------------------------------------- the gate, and the other two writers

    /// <summary>
    /// A drawing save arriving while a creation is in flight is refused, and the refusal names the
    /// CREATION - not the other save and not the panel. The gate has had three states since batch C
    /// and a creation is the fourth: a refusal that named the wrong gesture would send whoever reads
    /// it looking for something that is not happening, which is the defect that made this field an
    /// enum in the first place.
    /// </summary>
    [Fact]
    public async Task ADrawingSaveArrivingDuringACreationIsRefusedNamingTheCreation()
    {
        var handler = HostWithTwoDevices();
        handler.SingleItemGate = new TaskCompletionSource();
        var cut = await RenderWithAsync(handler);

        var creating = cut.InvokeAsync(() => cut.Instance.OnDrawioCellsAdded([Dropped(Registry()[0], "drawio-1", 100, 100)]));
        await handler.SingleItemGateReached.Task;

        var moved = new PlantGraph([Item(A, 99, 77), Item(B, 500, 20)], [], [.. Registry()]);
        await cut.InvokeAsync(() => cut.Instance.OnDrawioSaved(DrawioDocument.Build(moved, null)));

        handler.SingleItemGate.SetResult();
        await creating;

        Assert.Contains("A device dropped on the drawing is being created", cut.Find("#drawio-save-error").TextContent);
        // And nothing of the drawing went out: the move is not among the writes.
        Assert.DoesNotContain(handler.GeometryWrites, g => g.ItemId == A);
    }

    /// <summary>
    /// The other direction: a drop arriving while the drawing's save is writing. Here the cells are
    /// refused rather than queued - it takes two gestures at once, which is a human-serial case -
    /// but every one of them gets a line, and the line names the save.
    /// </summary>
    [Fact]
    public async Task ADropArrivingDuringADrawingSaveIsRefusedNamingTheSave()
    {
        var handler = HostWithTwoDevices();
        handler.GeometryGate = new TaskCompletionSource();
        var cut = await RenderWithAsync(handler);

        var moved = new PlantGraph([Item(A, 99, 77), Item(B, 500, 20)], [], [.. Registry()]);
        var saving = cut.InvokeAsync(() => cut.Instance.OnDrawioSaved(DrawioDocument.Build(moved, null)));
        await handler.GeometryGateReached.Task;

        await DropAsync(cut, Dropped(Registry()[0], "drawio-1", 100, 100));

        handler.GeometryGate.SetResult();
        await saving;

        Assert.Empty(handler.ItemCreations);
        Assert.Contains("The drawing is being saved", cut.Find("#drawio-creation-error").TextContent);
        Assert.Contains("drawio-1", Assert.Single(LinesOf(cut, "drawio-not-created")));
    }

    // ---------------------------------------------------------------- what happens afterwards

    /// <summary>
    /// The save that follows a creation <b>recognizes the cell</b>: the device is not proposed for
    /// deletion (its cell carries draw.io's identifier, not the plant's) and the cell is not refused
    /// as new. Moving it writes a move, on the right device.
    /// </summary>
    [Fact]
    public async Task TheSaveAfterACreationRecognizesTheCellAndWritesItsMove()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);

        await DropAsync(cut, Dropped(Registry()[0], "drawio-1", 100, 100));
        var born = Assert.Single(handler.ItemCreations).ItemId;
        handler.GeometryWrites.Clear();

        // The drawing comes back with that cell moved by +40/+25, still under draw.io's identifier.
        var drawing = WithExtraCell(DrawioDocument.Build(GraphOf(handler), null),
            $"""<object id="drawio-1" label="Automation" wcs="device" {DrawioLibrary.TypeAttribute}="0"><mxCell style="x" vertex="1" parent="1"><mxGeometry x="140" y="125" width="{DrawioLibrary.ThumbSize}" height="{DrawioLibrary.ThumbSize}" as="geometry"/></mxCell></object>""");
        await cut.InvokeAsync(() => cut.Instance.OnDrawioSaved(drawing));

        var move = Assert.Single(handler.GeometryWrites);
        Assert.Equal(born, move.ItemId);
        Assert.Equal(140, move.X!.Value, precision: 2);
        Assert.Equal(125, move.Y!.Value, precision: 2);
        Assert.Empty(handler.ItemDeletes);
        Assert.Empty(LinesOf(cut, "drawio-refusal"));
    }

    /// <summary>
    /// <b>Ctrl+Z straight after a drop.</b> The shape leaves the canvas and the device stays on the
    /// plant - the owner accepted that cost knowing it - and the question this test answers is the
    /// one that decides whether the cost is silent: does the next save take it away through the
    /// deletion road that already exists? It does, and the report names it.
    /// </summary>
    /// <remarks>
    /// The line names the device by its SUGGESTED name, which is what makes it readable: before that
    /// name existed it carried the bare guid, and before <c>NameOf</c> existed it read "the deletion
    /// of device " with nothing after it at all - about the one device whose disappearance nobody
    /// could otherwise explain.
    /// </remarks>
    [Fact]
    public async Task UndoingADropLeavesTheDeviceOnThePlantAndTheNextSaveDeletesIt()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);

        await DropAsync(cut, Dropped(Registry()[0], "drawio-1", 100, 100));
        var born = Assert.Single(handler.ItemCreations).ItemId;
        Assert.Equal(3, handler.Items.Count);

        // Ctrl+Z: the cell is gone from the drawing, everything else is as it was. The drawing is
        // built from the plant WITHOUT the new device on purpose - the canvas never carried it under
        // the plant's identifier, it carried draw.io's, and the undo took that cell away.
        var canvasAfterTheUndo = new PlantGraph([Item(A, 10, 20), Item(B, 500, 20)], [], [.. Registry()]);
        await cut.InvokeAsync(() => cut.Instance.OnDrawioSaved(DrawioDocument.Build(canvasAfterTheUndo, null)));

        Assert.Equal(born, Assert.Single(handler.ItemDeletes));
        Assert.Equal(2, handler.Items.Count);
        // And the report NAMES it. The plan always ends with the decoration, so the deletion is one
        // line among two: it is picked by what it says, not by being the only one.
        var deletion = Assert.Single(LinesOf(cut, "drawio-written"),
            line => line.Contains("the deletion of device", StringComparison.Ordinal));
        // By its NAME since the suggested name: the report reads a device's name when it has one,
        // and the device that just vanished under an undo has one. Before it this line carried the
        // bare guid, which was the only thing there was to carry.
        Assert.Equal("Automation 1", Assert.Single(handler.ItemCreations).Name);
        Assert.Contains("Automation 1", deletion);
    }

    // ---------------------------------------------------------------- the suggested name

    /// <summary>
    /// <b>THE TRAP OF THIS BATCH, and it is measured and not reasoned about.</b> A multiple paste
    /// arrives as ONE call with all the cells in it, so nothing between one cell and the next tells
    /// the page that the plant has grown: twenty cells must take twenty DIFFERENT numbers, not
    /// twenty times "Automation 1".
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It can fail, and in two different ways.</b> Computing the name once for the whole batch
    /// gives twenty identical names; computing it per cell against a SNAPSHOT of the plant taken
    /// when the page opened gives twenty identical names as well. What makes it work is that
    /// <c>CreateItemCommand</c> rereads the device it has just written and puts it into the page's
    /// own <c>PlantGraph</c>, which is the collection the name is read off - and the fake host here
    /// really echoes the name back on that reread, exactly as <c>PlantQueries</c> does.
    /// </para>
    /// <para>
    /// <b>Anchored to the number of cells and to the two types</b>: the expected list is built from
    /// the same twenty, so a batch that created none, or five, cannot pass through. And the types
    /// alternate, so the third trap - a Buffer jumping to 2 because an Automation 1 exists - is
    /// measured here too, on ten numbers each instead of one.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TwentyCellsPastedAtOnceGetTwentyDifferentNames()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);

        var pasted = Enumerable.Range(0, 20)
            .Select(i => Dropped(Registry()[i % 2], $"drawio-{i}", 100 + 10 * i, 100))
            .ToArray();
        await DropAsync(cut, pasted);

        Assert.Equal(pasted.Length, handler.ItemCreations.Count);
        var names = handler.ItemCreations.Select(write => write.Name).ToList();
        Assert.Equal(pasted.Length, names.Distinct().Count());

        // Even index = the first type, odd = the second, in the order the cells were handed over.
        // Compared as one string on each side so that a failure prints the whole series: which
        // number repeated, and from where on, is the only thing worth reading here.
        Assert.Equal(
            string.Join(" | ", Enumerable.Range(1, pasted.Length / 2).Select(number => $"Automation {number}")),
            string.Join(" | ", names.Where((_, index) => index % 2 == 0)));
        Assert.Equal(
            string.Join(" | ", Enumerable.Range(1, pasted.Length / 2).Select(number => $"Buffer {number}")),
            string.Join(" | ", names.Where((_, index) => index % 2 == 1)));

        // And the PLANT really holds those names, not just the writes: it is what the lists show and
        // what the next suggestion will read. The two seeded devices are called "1111" and "2222",
        // with no space, so the filter takes the twenty and nothing else.
        Assert.Equal(
            string.Join(" | ", names.Order(StringComparer.Ordinal)),
            string.Join(" | ", handler.Items.Select(item => item.Name)
                .Where(name => name is not null && name.Contains(' '))
                .Order(StringComparer.Ordinal)));
    }

    /// <summary>
    /// <b>The second trap: two drops one after the other, two separate gestures.</b> The second must
    /// see the first one's name. It is the case in which a page holding a snapshot of the plant taken
    /// at the opening would call them both "Automation 1".
    /// </summary>
    [Fact]
    public async Task TwoDropsOneAfterTheOtherAreNumberedOneAndTwo()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);

        await DropAsync(cut, Dropped(Registry()[0], "drawio-1", 100, 100));
        await DropAsync(cut, Dropped(Registry()[0], "drawio-2", 300, 100));

        Assert.Equal(2, handler.ItemCreations.Count);
        Assert.Equal("Automation 1", handler.ItemCreations[0].Name);
        Assert.Equal("Automation 2", handler.ItemCreations[1].Name);
        Assert.Empty(LinesOf(cut, "drawio-not-created"));
    }

    /// <summary>
    /// The same thing through the QUEUE and not through two settled gestures: the second drop arrives
    /// while the first is still inside its own write. The plant has not grown yet when that cell is
    /// queued - it grows while it waits - so the number has to be read when the cell has its turn,
    /// not when it arrives.
    /// </summary>
    [Fact]
    public async Task ADropQueuedDuringAnotherStillGetsTheNextNumber()
    {
        var handler = HostWithTwoDevices();
        handler.SingleItemGate = new TaskCompletionSource();
        var cut = await RenderWithAsync(handler);

        var first = cut.InvokeAsync(() => cut.Instance.OnDrawioCellsAdded([Dropped(Registry()[0], "drawio-1", 100, 100)]));
        await handler.SingleItemGateReached.Task;
        await DropAsync(cut, Dropped(Registry()[0], "drawio-2", 300, 100));
        handler.SingleItemGate.SetResult();
        await first;

        Assert.Equal(2, handler.ItemCreations.Count);
        Assert.Equal("Automation 1", handler.ItemCreations[0].Name);
        Assert.Equal("Automation 2", handler.ItemCreations[1].Name);
    }

    /// <summary>
    /// <b>The collision.</b> "First free" means free on the PLANT: a plant that already carries
    /// "Automation 1" - and from today it can, because we are the ones writing those names - gets an
    /// "Automation 2".
    /// </summary>
    [Fact]
    public async Task ANameThePlantAlreadyCarriesIsNotHandedOutTwice()
    {
        var handler = HostWithTwoDevices();
        handler.Items[0] = handler.Items[0] with { Name = "Automation 1" };
        var cut = await RenderWithAsync(handler);

        await DropAsync(cut, Dropped(Registry()[0], "drawio-1", 100, 100));

        Assert.Equal("Automation 2", Assert.Single(handler.ItemCreations).Name);
    }

    /// <summary>
    /// The hole in the middle is filled, not jumped over: with "Automation 1" and "Automation 3" on
    /// the plant the next one is "Automation 2". A rule written as "the highest plus one" - which is
    /// the obvious way to write it - would answer 4, and this test is the only thing that would say
    /// so.
    /// </summary>
    [Fact]
    public async Task TheFirstFreeNumberIsTheFirstFreeOneAndNotTheNextAfterTheLast()
    {
        var handler = HostWithTwoDevices();
        handler.Items[0] = handler.Items[0] with { Name = "Automation 1" };
        handler.Items[1] = handler.Items[1] with { Name = "Automation 3" };
        var cut = await RenderWithAsync(handler);

        await DropAsync(cut, Dropped(Registry()[0], "drawio-1", 100, 100));

        Assert.Equal("Automation 2", Assert.Single(handler.ItemCreations).Name);
    }

    /// <summary>
    /// <b>The third trap: the types count separately.</b> A plant full of Automations does not push a
    /// Buffer past its own 1.
    /// </summary>
    [Fact]
    public async Task ADifferentTypeStartsFromItsOwnOne()
    {
        var handler = HostWithTwoDevices();
        handler.Items[0] = handler.Items[0] with { Name = "Automation 1" };
        handler.Items[1] = handler.Items[1] with { Name = "Automation 2" };
        var cut = await RenderWithAsync(handler);

        await DropAsync(cut, Dropped(Registry()[1], "drawio-1", 100, 100));

        Assert.Equal("Buffer 1", Assert.Single(handler.ItemCreations).Name);
    }

    /// <summary>
    /// <b>What the shape shows after a RELOAD</b>, which until today no test watched at all: the
    /// document this page sends to draw.io at a reopening must carry the new device's name as the
    /// cell's label. Before the suggested name it carried <c>label=""</c> - the shape came back
    /// MUTE, and nothing went red.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reopening is the page's own outward trip and not a document built by the test:
    /// <c>OnDrawioReady</c> is called a second time, which is what draw.io does when the iframe says
    /// <c>init</c> again, and the assertion reads the XML that really went out through <c>load</c>.
    /// A test calling <c>DrawioDocument.Build</c> itself would be measuring the builder, not the
    /// page.
    /// </para>
    /// <para>
    /// The cell is looked up <b>by the device's guid</b>, because at a reopening the device is seeded
    /// like every other one and its cell carries that guid - draw.io's own identifier lived only in
    /// the session that dropped it. And the lookup is anchored: an XML with no such cell fails by
    /// name instead of passing on an empty match.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AtTheNextOpeningTheShapeCarriesTheNameTheDeviceWasBornWith()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);

        await DropAsync(cut, Dropped(Registry()[1], "drawio-1", 640, 320));
        var born = Assert.Single(handler.ItemCreations).ItemId;

        // The reopening: draw.io says it is ready again and the page seeds the canvas from the plant
        // as it is now.
        await cut.InvokeAsync(() => cut.Instance.OnDrawioReady());

        var loads = JSInterop.Invocations.Where(call => call.Identifier == "load").ToList();
        Assert.Equal(2, loads.Count);
        var document = System.Xml.Linq.XDocument.Parse((string)loads[^1].Arguments[1]!);
        var cell = Assert.Single(document.Descendants("object"),
            node => (string?)node.Attribute("id") == born.ToString());
        Assert.Equal("Buffer 1", (string?)cell.Attribute("label"));
    }

    /// <summary>
    /// The bridge declaring it cannot hear the additions: it is SAID, and next to the drawing rather
    /// than covering it - drawing, saving and the properties panel all still work, only a dropped
    /// shape stays a drawing. A page that kept quiet would be read as "I dragged it wrong".
    /// </summary>
    [Fact]
    public async Task ABridgeThatCannotHearTheAdditionsIsDeclared()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);

        await cut.InvokeAsync(() => cut.Instance.OnDrawioCreationUnavailable());
        Assert.Contains("can no longer become a device", cut.Find("#drawio-creation-unavailable").TextContent);

        // And it is lowered again by a subscription that answers: leaving a declared failure next to
        // a device that has just been born would be a second way of lying.
        await DropAsync(cut, Dropped(Registry()[0], "drawio-1", 100, 100));
        Assert.Empty(cut.FindAll("#drawio-creation-unavailable"));
    }
}
