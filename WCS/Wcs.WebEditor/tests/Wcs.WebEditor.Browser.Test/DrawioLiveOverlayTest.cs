using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// The rule the whole live status rests on: what is live is drawn OVER the canvas, never INTO the
/// drawing. If a mark or an arrow ever became part of the model, the save would write it or the
/// drift nets would take it away - and neither would say a word.
/// </summary>
/// <remarks>
/// Queue item 21: the load unit and the status dot are no longer cell overlays but shapes of the
/// module's own in the view's overlay pane (the load unit a box of drawing units centred in the
/// device, the dot inside its top right corner), both turned by the device's true rotation and both
/// letting the mouse through; what they say is in the device's tooltip.
/// </remarks>
public sealed class DrawioLiveOverlayTest(DrawioEditorFixture fixture, ITestOutputHelper output) : IClassFixture<DrawioEditorFixture>
{
    private const string SeedHolder = "browser-test-live-overlay";

    /// <summary>
    /// How far the painted marks may be from where plain geometry puts them, in screen pixels:
    /// mxSvgCanvas2D writes every coordinate and the centre of every rotation with two decimals
    /// (<c>format</c>), so a correct mark is within a few hundredths. Half a pixel is far below what
    /// the defects these tests look for cost - half the box, or the whole height of the device.
    /// </summary>
    private const double Slack = 0.5;

    /// <summary>A device of the frame as the page sends it (<c>LiveFrameJson</c>), with the size of its load unit in drawing units.</summary>
    private sealed record Device(string CellId, string? Color, string StatusName, string? LoadUnit, int LoadUnitWidth = 40, int LoadUnitHeight = 30);

    private sealed record Motion(string From, string To);

    private static readonly JsonSerializerOptions FrameJson = new(JsonSerializerDefaults.Web);

    private static string Frame(Device[] devices, params Motion[] motions) =>
        JsonSerializer.Serialize(new { devices, motions }, FrameJson);

    private static string DraggedId => PlantSeed.DraggedId.ToString();

    private static string UntouchedId => PlantSeed.UntouchedId.ToString();

    private static Device LoadedAndInError(string cellId, int width = 40, int height = 30, string loadUnit = "LU-7 (7)") =>
        new(cellId, "#ff0000", "In Error", loadUnit, width, height);

    [Fact]
    public async Task LiveColoursAndArrowsNeverEnterTheDrawing()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);
        var before = await editor.ModelAndUndoAsync();

        var frame = Frame(
            [LoadedAndInError(DraggedId), new Device(UntouchedId, "#ffa500", "Moving", null)],
            new Motion(DraggedId, UntouchedId));

        // Read BEFORE rendering, so the assertion below is the exact increase this frame caused -
        // not merely "at least" something, which would stay green even if drawDevices() silently
        // skipped a mark it should have drawn.
        var beforeLive = await editor.LiveObjectsAsync();
        Assert.Equal("drawn", await editor.RenderLiveFrameAsync(frame));

        var live = await editor.LiveObjectsAsync();
        // Three marks: the dragged device's load unit and status dot, the untouched one's dot.
        Assert.Equal(beforeLive[0] + 3, live[0]);
        // MEASURED, not the 1 fix round 1's finding assumed: a single mxConnector with
        // endArrow='block' paints THREE <path> elements in the overlay pane for the ONE motion this
        // frame asks for (the shaft plus the arrowhead marker's own paths), not one.
        Assert.Equal(beforeLive[1] + 3, live[1]);
        // Queue item 21: not one cell overlay any more - an overlay lives ON the cell, and that is
        // how a Ctrl+D copy used to carry it.
        Assert.Equal(beforeLive[2], live[2]);

        var after = await editor.ModelAndUndoAsync();
        Assert.Equal(before[0], after[0]);
        Assert.Equal(before[1], after[1]);

        await editor.RecordReturnedDrawingAsync();
        await editor.SaveAsync();
        Assert.Null(await editor.SaveErrorAsync());

        // NOT Assert.Empty(WrittenAsync()): an empty round is never a save with no writes at all -
        // it always makes exactly ONE, the drawing's decoration, which DrawioEditor rewrites on
        // every successful save regardless of what is on the canvas (measured and already relied
        // on by DrawioRoundTripTest.SavingWithoutTouchingAnythingLeavesThePlantWhereItWas). The
        // invariant THIS test is about is narrower and still holds: nothing born of the live frame
        // - no device, no colour, no status name - reached the write list. The model-XML equality
        // just above and the DoesNotContain below are the actual proof of that; checking the one
        // known "decoration" line for the device names too would be a tautology - that fixed string
        // never contains them by construction, and the check could not fail.
        var written = await editor.WrittenAsync();
        var line = Assert.Single(written);
        Assert.Contains("decoration", line, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("Status: In Error", await editor.LastReturnedDrawingAsync());
        Assert.DoesNotContain("data-wcs-live", await editor.LastReturnedDrawingAsync());
    }

    [Fact]
    public async Task AnEmptyFrameTakesEverythingAway()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);

        Assert.Equal("drawn", await editor.RenderLiveFrameAsync(Frame([LoadedAndInError(DraggedId)], new Motion(DraggedId, UntouchedId))));

        var firstFrame = await editor.LiveObjectsAsync();
        Assert.Equal(2, firstFrame[0]);
        Assert.True(firstFrame[1] >= 1,
            "The movement arrow is not in the overlay pane after the first frame: the frame was not drawn, and this test would prove nothing about it being taken away.");

        Assert.Equal("drawn", await editor.RenderLiveFrameAsync("""{"devices":[],"motions":[]}"""));

        Assert.Equal(0, (await editor.LiveObjectsAsync())[0]);
        Assert.True(await editor.MovementArrowsHiddenAsync(),
            "The movement arrow is still visible after an empty frame took the motion away: drawArrows() hides it instead of destroying it, so 'hidden' - not 'gone' - is the actual contract.");
    }

    /// <summary>
    /// Fix round 1, finding 8, kept for the shapes of queue item 21: Ctrl+D clones a cell through
    /// mxCell.clone(), and the copy must not come out showing the original's live status. With cell
    /// overlays it did (mxCell.clone copies 'overlays'); the marks are now keyed by the cell id the
    /// frame names, so the copy - a new id - has none. What this still catches is a module that
    /// found its devices by anything the copy shares with the original.
    /// </summary>
    [Fact]
    public async Task DuplicatingADeviceDoesNotCopyItsLiveOverlays()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);

        Assert.Equal("drawn", await editor.RenderLiveFrameAsync(Frame([LoadedAndInError(DraggedId)])));
        Assert.Equal(2, await editor.LiveMarkCountOfCellAsync(DraggedId));

        await editor.SelectDevicesAsync(PlantSeed.DraggedName);
        Assert.Equal("called", await editor.InvokeActionAsync("duplicate"));

        var selected = await editor.SelectedCellIdsAsync();
        var clonedId = Assert.Single(selected);
        Assert.NotEqual(DraggedId, clonedId);

        Assert.Equal(0, await editor.LiveMarkCountOfCellAsync(clonedId));
        Assert.Equal(0, await editor.OverlayCountOfCellAsync(clonedId));
        // The original must still have its own two marks: the copy's birth must not wipe everyone's.
        Assert.Equal(2, await editor.LiveMarkCountOfCellAsync(DraggedId));
    }

    /// <summary>
    /// How long drawio-live.js keeps a hover after the mouse has left its device (<c>hideDelay</c>).
    /// Written here on purpose, not read from the module: the test below proves on a stopped clock
    /// that the hover ends exactly this long after leaving, not sooner - with a grace of zero every
    /// late route would be refused just the same, and the refusal alone would not notice.
    /// </summary>
    private const int GraceWindow = 300;

    /// <summary>
    /// Fix round 1, findings 2, 3 and 5. A route asked for while the mouse was on the device is
    /// drawn; the SAME device asked for again a moment after the mouse left for empty canvas - still
    /// inside the grace window - is refused, because by then "leaving" is true even though
    /// hoveredCell has not been cleared yet. Past the window hover ends on its own. Throughout, the
    /// stub .NET reference always REJECTS (standing in for a disposed circuit): finding 2's
    /// .catch(() =&gt; {}) is what keeps that from ever becoming an unhandled rejection on the page.
    /// </summary>
    /// <remarks>
    /// Queue item 9: the page runs on Playwright's clock, stopped from just before the mouse leaves
    /// until the test moves it on. On the browser's own clock the leave, the late route and the count
    /// of the arrows all had to land inside the real 300 ms of the grace window, and when the window
    /// closed first the arrows were gone for the grace timer's own reason ("6 expected, 0 found"):
    /// the test went red without the product having refused or allowed anything.
    /// </remarks>
    [Fact]
    public async Task LeavingForEmptyCanvasRefusesALateRouteForTheOldDevice()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenWithAClockOfItsOwnAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);
        var draggedId = PlantSeed.DraggedId.ToString();
        var untouchedId = PlantSeed.UntouchedId.ToString();

        Assert.Equal("drawn", await editor.RenderLiveFrameAsync(JsonSerializer.Serialize(new
        {
            devices = Array.Empty<object>(),
            motions = Array.Empty<object>(),
        })));

        await editor.EnableLiveHoverAsync();
        await editor.HoverOverDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForHoverCallAsync(draggedId);

        var beforeRoute = await editor.LiveObjectsAsync();
        await editor.ShowLiveRouteAsync(draggedId, untouchedId, untouchedId);
        var withRoute = await editor.LiveObjectsAsync();
        Assert.True(withRoute[1] > beforeRoute[1],
            "showRoute while still hovering the origin device drew no arrow: this test's baseline is wrong, and it would prove nothing below.");

        // From here the grace timer cannot fire on its own: every step below lands inside the
        // window however slow the machine is, and only RunTheClockForAsync closes it.
        await editor.StopTheClockAsync();

        // Leave for empty canvas (still inside the container: mxGraph's own mouseMove starts the
        // grace timer here, not the container's mouseleave - that is finding 4's own test). The
        // SAME device asked for again, with a DIFFERENT route (no "next" hop this time) so that a
        // wrongly-allowed late call would visibly change the arrow count: refused, it must not.
        await editor.ClickEmptyCanvasAsync(PlantSeed.DraggedName, PlantSeed.UntouchedName);
        await editor.ShowLiveRouteAsync(draggedId, null, untouchedId);
        var afterLateRoute = await editor.LiveObjectsAsync();
        Assert.Equal(withRoute[1], afterLateRoute[1]);

        // A moment short of the window the hover still holds; at the window it ends on its own.
        await editor.RunTheClockForAsync(GraceWindow - 1);
        Assert.False(await HoverHasEndedAsync(editor), $"The hover ended before the grace window of {GraceWindow} ms was over.");
        Assert.Equal(withRoute[1], (await editor.LiveObjectsAsync())[1]);

        await editor.RunTheClockForAsync(1);
        Assert.True(await HoverHasEndedAsync(editor), $"The hover did not end when the grace window of {GraceWindow} ms was over.");
        Assert.Equal(0, (await editor.LiveObjectsAsync())[1]);

        await editor.ResumeTheClockAsync();
        Assert.Equal(0, await editor.UnhandledRejectionCountAsync());
    }

    /// <summary>Whether the stub has heard OnLiveHover(null) - the hover's end - read once, without waiting.</summary>
    private static async Task<bool> HoverHasEndedAsync(DrawioEditorPage editor) =>
        JsonSerializer.Deserialize<JsonElement[][]>(await editor.HoverCallsAsync())!
            .Any(call => call[0].GetString() == "OnLiveHover" && call[1].ValueKind == JsonValueKind.Null);

    /// <summary>
    /// Fix round 1, finding 4. Leaving the device STRAIGHT for the shapes panel beside the canvas -
    /// never crossing empty canvas first - never fires mxGraph's own mouseMove again, so only the
    /// container's own mouseleave can start the grace timer. Reused stub also proves finding 2 here:
    /// no unhandled rejection even though every OnLiveHover call is refused by the .NET side.
    /// </summary>
    [Fact]
    public async Task LeavingTheCanvasContainerDirectlyStillEndsHover()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        var draggedId = PlantSeed.DraggedId.ToString();

        Assert.Equal("drawn", await editor.RenderLiveFrameAsync(JsonSerializer.Serialize(new
        {
            devices = Array.Empty<object>(),
            motions = Array.Empty<object>(),
        })));

        await editor.EnableLiveHoverAsync();
        await editor.HoverOverDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForHoverCallAsync(draggedId);

        await editor.MoveMouseOffTheCanvasContainerAsync();
        // This test measures THAT hover ends when the mouse leaves the container directly, not how
        // fast. Fix round 2 raised this wait from a 2000 ms ceiling after it went red once in a
        // three-run browser suite; the final review (I3) removed the test's own poll and ceiling
        // altogether - the page's WaitForHoverCallAsync is Playwright's wait, with the harness's
        // ceiling - after the sister test above went red on the very same 2000 ms.
        await editor.WaitForHoverCallAsync(null);

        Assert.Equal(0, await editor.UnhandledRejectionCountAsync());
    }

    /// <summary>
    /// Fix round 1, finding 6. Forces the FIRST addListener call inside adopt() to throw, simulating
    /// draw.io failing midway through the handshake. Before the fix, graph/win were assigned before
    /// the listeners, so a render() straight after a failed one would see graph === target.graph and
    /// never retry adopt() at all - drawDevices()/drawArrows() would still run fine on the graph
    /// reference alone (so 'drawn' on its own does NOT discriminate the bug - MEASURED: a version
    /// with the ordering reverted stayed green on that assertion), but the mouse listener that
    /// makes hover work would never have been installed. Hover after the retry is the proof.
    /// </summary>
    [Fact]
    public async Task AFailedAdoptionIsRetriedCleanly()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);

        // On a fresh adoption: since Task 4 the page has already adopted the graph with its own
        // first frame, and the helper says why that matters.
        var frame = JsonSerializer.Serialize(new { devices = Array.Empty<object>(), motions = Array.Empty<object>() });
        Assert.Equal("failed", await editor.RenderLiveFrameOnABrokenAdoptionAsync(frame));

        // Unbroken now (the helper restores the original after firing once): a
        // render right after the failure has to adopt cleanly, not stay stuck believing a
        // half-installed graph is the one it already has.
        Assert.Equal("drawn", await editor.RenderLiveFrameAsync(frame));

        // The real proof: the mouse listener adopt() installs is what makes hover work at all. If
        // the retry skipped adopt() (graph === target.graph already, wrongly), hovering would never
        // call back.
        await editor.EnableLiveHoverAsync();
        await editor.HoverOverDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForHoverCallAsync(PlantSeed.DraggedId.ToString());
    }

    /// <summary>
    /// Fix round 1, finding 7. Breaks a motion arrow's own destroy() and calls clear(): the module
    /// must swallow that internal failure - console.error and move on - never let it escape to
    /// whoever called clear(), the way render()'s own try/catch already worked before this round.
    /// </summary>
    [Fact]
    public async Task ClearNeverThrowsTowardsItsCaller()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);

        Assert.Equal("drawn", await editor.RenderLiveFrameAsync(JsonSerializer.Serialize(new
        {
            devices = Array.Empty<object>(),
            motions = new[] { new { from = PlantSeed.DraggedId.ToString(), to = PlantSeed.UntouchedId.ToString() } },
        })));

        Assert.False(await editor.ClearThrowsWhenAShapeIsBrokenAsync(),
            "clear() let an internal exception escape to its caller instead of swallowing it.");
    }

    /// <summary>
    /// Fix round 2, finding 1, kept for queue item 21: draw.io pushes overlays of its OWN onto cells
    /// - Graph.setCellWarning here, a note's link icon or a comment marker in the product - and
    /// nothing the live module does may take them off. The guard that used to strip a clone's copied
    /// overlays is gone with the overlays themselves; what is left to prove is that every path that
    /// makes, moves or takes away the marks leaves draw.io's own where they are: the device carries
    /// the warning AND live marks, a real model edit redraws them, an empty frame takes them away,
    /// then forget() and clear().
    /// </summary>
    [Fact]
    public async Task DrawioOwnOverlaysAreLeftAlone()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);

        await editor.SetCellWarningAsync(UntouchedId, "a warning of draw.io's own");
        Assert.Equal(1, await editor.OverlayCountOfCellAsync(UntouchedId));

        Assert.Equal("drawn", await editor.RenderLiveFrameAsync(Frame([LoadedAndInError(UntouchedId)])));
        Assert.Equal(2, await editor.LiveMarkCountOfCellAsync(UntouchedId));
        Assert.Equal(1, await editor.OverlayCountOfCellAsync(UntouchedId));

        // A real model edit: the marks are placed again on the model's own CHANGE event, and
        // setCellWarning alone does not fire it (it fires ADD_OVERLAY on the GRAPH, not a model change).
        await editor.PutAFreeNoteOnTheCanvasAsync("queue item 21: trigger a model change");
        Assert.Equal(1, await editor.OverlayCountOfCellAsync(UntouchedId));

        Assert.Equal("drawn", await editor.RenderLiveFrameAsync("""{"devices":[],"motions":[]}"""));
        Assert.Equal(0, await editor.LiveMarkCountOfCellAsync(UntouchedId));
        Assert.Equal(1, await editor.OverlayCountOfCellAsync(UntouchedId));

        Assert.Equal("drawn", await editor.RenderLiveFrameAsync(Frame([LoadedAndInError(UntouchedId)])));
        await editor.CallTheLiveModuleAsync("forget");
        Assert.Equal(1, await editor.OverlayCountOfCellAsync(UntouchedId));

        Assert.Equal("drawn", await editor.RenderLiveFrameAsync(Frame([LoadedAndInError(UntouchedId)])));
        await editor.CallTheLiveModuleAsync("clear");
        Assert.Equal(1, await editor.OverlayCountOfCellAsync(UntouchedId));
    }

    /// <summary>
    /// A cell replaced by a new object with the SAME id (as a re-seed whose forget did not go through
    /// leaves it), and moved while at it: the marks must be on the NEW cell's box - at once, from the
    /// model edit itself, and again after the same frame is drawn once more. A module that kept the
    /// cell object it first found would go on drawing on a cell that is no longer in the drawing.
    /// </summary>
    [Fact]
    public async Task ACellReplacedUnderTheSameIdGetsItsOverlaysBack()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        var frame = Frame([LoadedAndInError(DraggedId)]);
        Assert.Equal("drawn", await editor.RenderLiveFrameAsync(frame));
        Assert.Equal(2, await editor.LiveMarkCountOfCellAsync(DraggedId));

        Assert.True(await editor.ReplaceCellKeepingItsIdAsync(DraggedId, dx: 200, dy: 100), "The cell was not replaced by a new object with the same id.");
        AssertTheLoadUnitIsCentredOnTheCell(await editor.LiveMarksOfCellAsync(DraggedId));

        Assert.Equal("drawn", await editor.RenderLiveFrameAsync(frame));
        AssertTheLoadUnitIsCentredOnTheCell(await editor.LiveMarksOfCellAsync(DraggedId));

        void AssertTheLoadUnitIsCentredOnTheCell(string json)
        {
            output.WriteLine(json);
            var (cell, marks) = Read(json);
            Assert.Equal(2, marks.Count(mark => mark.Visible));
            var load = Assert.Single(marks, mark => mark.Kind == "load");
            var centre = load.Centre;
            Assert.InRange(centre.X, cell.Centre.X - Slack, cell.Centre.X + Slack);
            Assert.InRange(centre.Y, cell.Centre.Y - Slack, cell.Centre.Y + Slack);
        }
    }

    /// <summary>
    /// Queue item 21, owner's decision 1: the load unit is a box of its configured size in DRAWING
    /// units - so it grows and shrinks with the zoom - centred in the device and turned with it. The
    /// seed's dragged device is 120 x 60 turned by 30 degrees; the load unit asked for is 40 x 30,
    /// which fits. The expected corners are plain geometry of the cell: its centre, and the four
    /// corners of a 40 x 30 box times the scale turned by the cell's own rotation.
    /// </summary>
    [Fact]
    public async Task TheLoadUnitIsABoxOfItsConfiguredSizeCentredAndTurnedWithTheDevice()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        Assert.Equal("drawn", await editor.RenderLiveFrameAsync(Frame([LoadedAndInError(DraggedId, 40, 30)])));

        foreach (var scale in new[] { 0.25, 1.0, 2.0 })
        {
            Assert.Equal(scale, await editor.ZoomToAsync(scale));
            var json = await editor.LiveMarksOfCellAsync(DraggedId);
            output.WriteLine($"scale {scale}: {json}");
            var (cell, marks) = Read(json);
            Assert.Equal(30, cell.Rotation);
            AssertTheSeedsDevice(cell);

            var load = Assert.Single(marks, mark => mark.Kind == "load");
            Assert.True(load.Visible, $"The load unit is hidden at scale {scale}.");
            AssertCorners(load, cell.Turned(-20 * scale, -15 * scale), cell.Turned(20 * scale, -15 * scale),
                cell.Turned(20 * scale, 15 * scale), cell.Turned(-20 * scale, 15 * scale));
        }
    }

    /// <summary>
    /// Queue item 21, owner's decision 1: a load unit larger than its device is scaled down, the same
    /// in both directions, until it fits inside the device's own unturned box. 300 x 100 on the
    /// seed's 120 x 60: the width is the tighter side (120 / 300 = 0.4), so the box is 120 x 40 -
    /// as wide as the device and not as tall - turned with it.
    /// </summary>
    [Fact]
    public async Task ALoadUnitLargerThanItsDeviceIsScaledDownToFitInsideIt()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        Assert.Equal("drawn", await editor.RenderLiveFrameAsync(Frame([LoadedAndInError(DraggedId, 300, 100)])));

        foreach (var scale in new[] { 0.25, 1.0 })
        {
            Assert.Equal(scale, await editor.ZoomToAsync(scale));
            var json = await editor.LiveMarksOfCellAsync(DraggedId);
            output.WriteLine($"scale {scale}: {json}");
            var (cell, marks) = Read(json);
            AssertTheSeedsDevice(cell);

            var load = Assert.Single(marks, mark => mark.Kind == "load");
            AssertCorners(load, cell.Turned(-60 * scale, -20 * scale), cell.Turned(60 * scale, -20 * scale),
                cell.Turned(60 * scale, 20 * scale), cell.Turned(-60 * scale, 20 * scale));
        }
    }

    /// <summary>
    /// Queue item 21, owner's decision 4: the status dot keeps its 14 px on screen at any zoom, lies
    /// ENTIRELY inside the device and sits in its top right corner - the device's own, turned with
    /// it. At the seed's 30 degrees and at -180, the rotation real plant devices carry: there
    /// mxGraph's overlays, turned by mod(rotation, 90), put the dot at the screen's top right, which
    /// is the device's bottom left. Every check is in the cell's own unturned frame: each corner of
    /// the dot's box brought back by the cell's rotation around its centre.
    /// </summary>
    [Fact]
    public async Task TheStatusDotIsInsideTheTopRightCornerAndTurnsWithTheDevice()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        Assert.Equal("drawn", await editor.RenderLiveFrameAsync(Frame([LoadedAndInError(DraggedId)])));

        foreach (var rotation in new[] { 30.0, -180.0 })
        {
            if (rotation != 30) await editor.SetCellRotationAsync(DraggedId, rotation);
            foreach (var scale in new[] { 0.25, 1.0, 2.0 })
            {
                Assert.Equal(scale, await editor.ZoomToAsync(scale));
                var json = await editor.LiveMarksOfCellAsync(DraggedId);
                output.WriteLine($"rotation {rotation}, scale {scale}: {json}");
                var (cell, marks) = Read(json);
                Assert.Equal(rotation, cell.Rotation);

                var dot = Assert.Single(marks, mark => mark.Kind == "status");
                Assert.True(dot.Visible, $"The status dot is hidden at rotation {rotation}, scale {scale}.");
                // 14 px on screen, whatever the scale.
                Assert.InRange(Distance(dot.Corners[0], dot.Corners[1]), 14 - Slack, 14 + Slack);
                Assert.InRange(Distance(dot.Corners[1], dot.Corners[2]), 14 - Slack, 14 + Slack);

                AssertTheDotIsInsideTheTopRightCorner(cell, dot, Slack, $"At rotation {rotation}, scale {scale}");

                // And turned with the device: its top edge runs along the device's own.
                var edge = Math.Atan2(dot.Corners[1].Y - dot.Corners[0].Y, dot.Corners[1].X - dot.Corners[0].X) * 180 / Math.PI;
                Assert.InRange(AngleBetween(edge, rotation), 0, 1);
            }
        }
    }

    /// <summary>
    /// Queue item 21, owner's decision 2: the load unit covers the middle of its device, and the
    /// mouse goes through it. A real mouse on the middle of a loaded device starts the live hover
    /// route for THAT device, and a real click there selects it - Playwright's mouse, not a call into
    /// draw.io, since what is being proved is where the browser delivers the events.
    /// </summary>
    [Fact]
    public async Task AClickInTheMiddleOfALoadedDeviceSelectsIt()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        Assert.Equal("drawn", await editor.RenderLiveFrameAsync(Frame([LoadedAndInError(DraggedId, 100, 50)])));
        // The premise: the load unit is drawn and covers the middle of the device, where the click goes.
        var (cell, marks) = Read(await editor.LiveMarksOfCellAsync(DraggedId));
        var load = Assert.Single(marks, mark => mark.Kind == "load" && mark.Visible);
        Assert.InRange(load.Centre.X, cell.Centre.X - Slack, cell.Centre.X + Slack);
        Assert.InRange(load.Centre.Y, cell.Centre.Y - Slack, cell.Centre.Y + Slack);
        Assert.Empty(await editor.SelectedCellIdsAsync());

        await editor.EnableLiveHoverAsync();
        await editor.HoverTheMiddleOfCellAsync(DraggedId);
        await editor.WaitForHoverCallAsync(DraggedId);

        await editor.ClickTheMiddleOfCellAsync(DraggedId);

        Assert.Equal([DraggedId], await editor.SelectedCellIdsAsync());
    }

    /// <summary>
    /// Queue item 22: while a device is dragged, mxGraph paints a PREVIEW of it under the mouse and
    /// leaves the model and the cell's view state where they were until the drop
    /// (mxGraphHandler.updateLivePreview paints, then resetPreviewStates puts the state back). The
    /// marks follow the preview, as the WinForms' load unit did, being part of its device (owner's
    /// decision of 30/9): a real mouse pressed on the device and moved in several steps, the canvas
    /// read BEFORE the button is let go - the load unit centred on the device as painted and the dot
    /// in its top right corner, far from the old place - then, after the drop, both on the place the
    /// device landed on.
    /// </summary>
    [Fact]
    public async Task TheLiveMarksFollowADeviceDraggedWithTheMouseAndLandWithIt()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        Assert.Equal("drawn", await editor.RenderLiveFrameAsync(Frame([LoadedAndInError(DraggedId)])));
        var (start, startMarks) = Read(await editor.LiveMarksOfCellAsync(DraggedId));
        var dotPlace = DotPlace(start, Assert.Single(startMarks, mark => mark.Kind == "status" && mark.Visible));

        var box = await editor.CellBoxOnScreenAsync(DraggedId);
        var (x, y) = (box[0] + box[2] / 2, box[1] + box[3] / 2);
        await editor.PressTheMouseAtAsync(x, y);
        await editor.MoveTheMouseToAsync(x + 80, y + 40, steps: 8);
        await editor.MoveTheMouseToAsync(x + DragX, y + DragY, steps: 8);

        var (preview, _) = await AssertTheMarksAreOnThePaintedDeviceAsync(editor, "in the middle of the drag", dotPlace);
        // The premise: the preview really is somewhere else - otherwise "on the painted device" would
        // also be "on the old place", and the test would prove nothing.
        Assert.True(Distance(preview.Centre, start.Centre) > 100,
            $"The device is painted at ({preview.Centre.X:0.#}, {preview.Centre.Y:0.#}), not away from where it was " +
            $"({start.Centre.X:0.#}, {start.Centre.Y:0.#}): mxGraph did not start moving it, and this test would prove nothing.");

        // A live frame in the middle of the gesture - a running plant sends one at every live event -
        // places the marks again without a paint of the device: they stay on the preview.
        Assert.Equal("drawn", await editor.RenderLiveFrameAsync(Frame([LoadedAndInError(DraggedId)])));
        var (afterFrame, stateAfterFrame) = await AssertTheMarksAreOnThePaintedDeviceAsync(editor, "after a live frame in the middle of the drag", dotPlace);
        // The premise of that check: the device is still painted at the preview while its view state
        // is still at the old place, so the frame placed the marks from where the device is PAINTED.
        // A frame that repainted the device would have put it back at its state, and the check above
        // would then hold with the painted box never read.
        Assert.True(Distance(afterFrame.Centre, preview.Centre) <= PreviewSlack && Distance(stateAfterFrame.Centre, start.Centre) <= PreviewSlack,
            $"After the live frame the device is painted at ({afterFrame.Centre.X:0.#}, {afterFrame.Centre.Y:0.#}) and its state is at " +
            $"({stateAfterFrame.Centre.X:0.#}, {stateAfterFrame.Centre.Y:0.#}); the preview was at ({preview.Centre.X:0.#}, {preview.Centre.Y:0.#}) " +
            $"and the old place at ({start.Centre.X:0.#}, {start.Centre.Y:0.#}): the frame did not place the marks from the painted box.");

        await editor.ReleaseTheMouseAsync();

        var json = await editor.LiveMarksOfCellAsync(DraggedId);
        output.WriteLine($"after the drop: {json}");
        var (dropped, marks) = Read(json);
        // The drop landed where the preview was: the model now says what the preview showed.
        Assert.InRange(dropped.Centre.X, preview.Centre.X - PreviewSlack, preview.Centre.X + PreviewSlack);
        Assert.InRange(dropped.Centre.Y, preview.Centre.Y - PreviewSlack, preview.Centre.Y + PreviewSlack);
        var load = Assert.Single(marks, mark => mark.Kind == "load" && mark.Visible);
        Assert.InRange(load.Centre.X, dropped.Centre.X - Slack, dropped.Centre.X + Slack);
        Assert.InRange(load.Centre.Y, dropped.Centre.Y - Slack, dropped.Centre.Y + Slack);
        AssertTheDotAt(dropped, Assert.Single(marks, mark => mark.Kind == "status" && mark.Visible), dotPlace, Slack, "after the drop");
    }

    /// <summary>
    /// Queue item 22: a drag given up with Esc puts the device back where it was - mxGraph's own
    /// reset repaints it from its state (mxGraphHandler.reset, resetLivePreview) - and the marks go
    /// back with it while the button is still down. Letting go afterwards moves nothing, and the
    /// model and draw.io's undo are exactly as before the gesture: the marks followed the preview
    /// without writing anything anywhere.
    /// </summary>
    [Fact]
    public async Task EscapeInTheMiddleOfADragPutsTheLiveMarksBackWithTheDevice()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        Assert.Equal("drawn", await editor.RenderLiveFrameAsync(Frame([LoadedAndInError(DraggedId)])));
        var (start, startMarks) = Read(await editor.LiveMarksOfCellAsync(DraggedId));
        var dotPlace = DotPlace(start, Assert.Single(startMarks, mark => mark.Kind == "status" && mark.Visible));
        var before = await editor.ModelCellsAndUndoAsync();

        var box = await editor.CellBoxOnScreenAsync(DraggedId);
        var (x, y) = (box[0] + box[2] / 2, box[1] + box[3] / 2);
        await editor.PressTheMouseAtAsync(x, y);
        await editor.MoveTheMouseToAsync(x + 80, y + 40, steps: 8);
        await editor.MoveTheMouseToAsync(x + DragX, y + DragY, steps: 8);
        var (preview, _) = await AssertTheMarksAreOnThePaintedDeviceAsync(editor, "in the middle of the drag", dotPlace);
        Assert.True(Distance(preview.Centre, start.Centre) > 100,
            "The premise: mxGraph did not start moving the device, and giving the drag up would prove nothing.");

        await editor.PressAsync("Escape");

        // The premise of "back": Esc did give the drag up, and the device is painted where it was.
        var (back, _) = await AssertTheMarksAreOnThePaintedDeviceAsync(editor, "after Esc, the button still down", dotPlace);
        Assert.True(Distance(back.Centre, start.Centre) < PreviewSlack,
            $"After Esc the device is still painted at ({back.Centre.X:0.#}, {back.Centre.Y:0.#}), not back at " +
            $"({start.Centre.X:0.#}, {start.Centre.Y:0.#}): draw.io did not give the drag up.");

        await editor.ReleaseTheMouseAsync();

        var json = await editor.LiveMarksOfCellAsync(DraggedId);
        output.WriteLine($"after letting go: {json}");
        var (cell, marks) = Read(json);
        Assert.InRange(cell.Centre.X, start.Centre.X - Slack, start.Centre.X + Slack);
        Assert.InRange(cell.Centre.Y, start.Centre.Y - Slack, start.Centre.Y + Slack);
        var load = Assert.Single(marks, mark => mark.Kind == "load" && mark.Visible);
        Assert.InRange(load.Centre.X, start.Centre.X - Slack, start.Centre.X + Slack);
        Assert.InRange(load.Centre.Y, start.Centre.Y - Slack, start.Centre.Y + Slack);

        var after = await editor.ModelCellsAndUndoAsync();
        Assert.Equal(before[0], after[0]);
        Assert.Equal(before[1], after[1]);
    }

    /// <summary>
    /// Queue item 22, the resize: mxVertexHandler paints the device at its new size under the mouse
    /// (livePreview, which draw.io turns on) and puts the state back after painting. A load unit of
    /// 300 x 100 is larger than the seed's 120 x 60 device and wider in proportion, so it is drawn as
    /// wide as the device (queue item 21, owner's decision 1): growing the device by its bottom right
    /// handle grows the box with it while the button is down, centred on the device as painted, and
    /// leaves it on the new size after the drop. The dot keeps its place at the device's top right
    /// corner as painted - which moves by the whole pull: the old corner, where a dot left behind would
    /// be, is still inside the device and in its top right quarter.
    /// </summary>
    [Fact]
    public async Task TheLiveMarksFollowADeviceResizedWithTheMouse()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        Assert.Equal("drawn", await editor.RenderLiveFrameAsync(Frame([LoadedAndInError(DraggedId, 300, 100)])));
        await editor.ClickTheMiddleOfCellAsync(DraggedId);
        Assert.Equal([DraggedId], await editor.SelectedCellIdsAsync());

        var (start, startMarks) = Read(await editor.LiveMarksOfCellAsync(DraggedId));
        var dotPlace = DotPlace(start, Assert.Single(startMarks, mark => mark.Kind == "status" && mark.Visible));
        await PullTheBottomRightHandleAsync(editor, start);

        var (preview, _) = await AssertTheMarksAreOnThePaintedDeviceAsync(editor, "in the middle of the resize", dotPlace);
        Assert.True(preview.Width - start.Width > 20 * start.Scale,
            $"The device is painted {preview.Width:0.#} px wide, it was {start.Width:0.#}: mxGraph did not start resizing it, and this test would prove nothing.");
        Assert.True(preview.Width / preview.Height < 3,
            $"The premise of 'as wide as the device': the device as painted ({preview.Width:0.#} x {preview.Height:0.#}) is no longer narrower in proportion than the 300 x 100 load unit.");
        var (_, marks) = Read(await editor.LiveMarksOfCellAsync(DraggedId));
        var load = Assert.Single(marks, mark => mark.Kind == "load" && mark.Visible);
        Assert.InRange(Distance(load.Corners[0], load.Corners[1]), preview.Width - PreviewSlack, preview.Width + PreviewSlack);

        await editor.ReleaseTheMouseAsync();

        var json = await editor.LiveMarksOfCellAsync(DraggedId);
        output.WriteLine($"after the drop: {json}");
        var (resized, after) = Read(json);
        Assert.InRange(resized.Width, preview.Width - PreviewSlack, preview.Width + PreviewSlack);
        var landed = Assert.Single(after, mark => mark.Kind == "load" && mark.Visible);
        Assert.InRange(landed.Centre.X, resized.Centre.X - Slack, resized.Centre.X + Slack);
        Assert.InRange(landed.Centre.Y, resized.Centre.Y - Slack, resized.Centre.Y + Slack);
        Assert.InRange(Distance(landed.Corners[0], landed.Corners[1]), resized.Width - Slack, resized.Width + Slack);
        AssertTheDotAt(resized, Assert.Single(after, mark => mark.Kind == "status" && mark.Visible), dotPlace, Slack, "after the drop");
    }

    /// <summary>
    /// Queue item 22, a resize given up with Esc. It comes back by another road than a drag's:
    /// mxVertexHandler's own escape handler repaints the device from its state, put back already, with
    /// a forced paint (cellRenderer.redraw(state, true)) - a drag's Esc goes through
    /// mxGraphHandler.reset, resetLivePreview and an unforced validate. The marks go back with the
    /// device while the button is still down - the load unit as wide as the device was and centred on
    /// it, the dot at its corner - and letting go afterwards changes nothing: the model and draw.io's
    /// undo are exactly as before the gesture.
    /// </summary>
    [Fact]
    public async Task EscapeInTheMiddleOfAResizePutsTheLiveMarksBackWithTheDevice()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        Assert.Equal("drawn", await editor.RenderLiveFrameAsync(Frame([LoadedAndInError(DraggedId, 300, 100)])));
        await editor.ClickTheMiddleOfCellAsync(DraggedId);
        Assert.Equal([DraggedId], await editor.SelectedCellIdsAsync());

        var (start, startMarks) = Read(await editor.LiveMarksOfCellAsync(DraggedId));
        var dotPlace = DotPlace(start, Assert.Single(startMarks, mark => mark.Kind == "status" && mark.Visible));
        var before = await editor.ModelCellsAndUndoAsync();
        await PullTheBottomRightHandleAsync(editor, start);

        var (preview, _) = await AssertTheMarksAreOnThePaintedDeviceAsync(editor, "in the middle of the resize", dotPlace);
        Assert.True(preview.Width - start.Width > 20 * start.Scale,
            $"The device is painted {preview.Width:0.#} px wide, it was {start.Width:0.#}: mxGraph did not start resizing it, and giving the resize up would prove nothing.");

        await editor.PressAsync("Escape");

        // The premise of "back": Esc did give the resize up, and the device is painted as it was.
        var (back, _) = await AssertTheMarksAreOnThePaintedDeviceAsync(editor, "after Esc, the button still down", dotPlace);
        Assert.True(Math.Abs(back.Width - start.Width) <= PreviewSlack && Distance(back.Centre, start.Centre) <= PreviewSlack,
            $"After Esc the device is still painted {back.Width:0.#} px wide centred at ({back.Centre.X:0.#}, {back.Centre.Y:0.#}), not {start.Width:0.#} px " +
            $"at ({start.Centre.X:0.#}, {start.Centre.Y:0.#}): draw.io did not give the resize up.");
        var (_, marks) = Read(await editor.LiveMarksOfCellAsync(DraggedId));
        var load = Assert.Single(marks, mark => mark.Kind == "load" && mark.Visible);
        Assert.InRange(Distance(load.Corners[0], load.Corners[1]), back.Width - PreviewSlack, back.Width + PreviewSlack);

        await editor.ReleaseTheMouseAsync();

        var json = await editor.LiveMarksOfCellAsync(DraggedId);
        output.WriteLine($"after letting go: {json}");
        var (cell, after) = Read(json);
        Assert.InRange(cell.Width, start.Width - Slack, start.Width + Slack);
        Assert.InRange(cell.Centre.X, start.Centre.X - Slack, start.Centre.X + Slack);
        Assert.InRange(cell.Centre.Y, start.Centre.Y - Slack, start.Centre.Y + Slack);
        var landed = Assert.Single(after, mark => mark.Kind == "load" && mark.Visible);
        Assert.InRange(landed.Centre.X, start.Centre.X - Slack, start.Centre.X + Slack);
        Assert.InRange(landed.Centre.Y, start.Centre.Y - Slack, start.Centre.Y + Slack);
        Assert.InRange(Distance(landed.Corners[0], landed.Corners[1]), start.Width - Slack, start.Width + Slack);

        var unchanged = await editor.ModelCellsAndUndoAsync();
        Assert.Equal(before[0], unchanged[0]);
        Assert.Equal(before[1], unchanged[1]);
    }

    /// <summary>
    /// Presses the selected device's bottom right handle - on the device's own bottom right corner,
    /// turned with it - and pulls it outwards along the device's own axes, 50 x 30 drawing units, in
    /// two stepped moves, leaving the button down.
    /// </summary>
    private static async Task PullTheBottomRightHandleAsync(DrawioEditorPage editor, Cell start)
    {
        var box = await editor.CellBoxOnScreenAsync(DraggedId);
        // A point of the view's panes on the page: the cell's box is the same box in both.
        (double X, double Y) OnThePage((double X, double Y) p) => (box[0] + p.X - start.X, box[1] + p.Y - start.Y);

        var corner = OnThePage(start.Turned(start.Width / 2, start.Height / 2));
        var pulled = OnThePage(start.Turned(start.Width / 2 + 50 * start.Scale, start.Height / 2 + 30 * start.Scale));
        await editor.PressTheMouseAtAsync(corner.X, corner.Y);
        await editor.MoveTheMouseToAsync((corner.X + pulled.X) / 2, (corner.Y + pulled.Y) / 2, steps: 6);
        await editor.MoveTheMouseToAsync(pulled.X, pulled.Y, steps: 6);
    }

    /// <summary>
    /// Queue item 22: the marks follow every paint of their device's shape by the graph's cell
    /// renderer - and draw.io's print (mxPrintPreview, Ctrl+P) paints the same cells through that same
    /// renderer, as does mxGraph's export of the view (mxUtils.getViewXml, used here): temporary
    /// states at a scale and a translation of their own, onto panes of their own and with the view's
    /// events off. Nothing places the marks again afterwards, so a module that followed those paints
    /// would leave them wherever the export put the device, on the canvas the operator is looking at.
    /// </summary>
    [Fact]
    public async Task PaintingTheDrawingElsewhereLeavesTheMarksOnTheCanvas()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        Assert.Equal("drawn", await editor.RenderLiveFrameAsync(Frame([LoadedAndInError(DraggedId)])));
        var (start, startMarks) = Read(await editor.LiveMarksOfCellAsync(DraggedId));
        var dotPlace = DotPlace(start, Assert.Single(startMarks, mark => mark.Kind == "status" && mark.Visible));

        var elsewhere = await editor.PaintTheDrawingElsewhereAsync(DraggedId, 2);
        output.WriteLine($"painted elsewhere: {elsewhere}");
        using (var document = JsonDocument.Parse(elsewhere))
        {
            // The premise: the renderer did paint the device, somewhere other than on the canvas.
            Assert.True(document.RootElement.GetProperty("paints").GetInt32() > 0, "The export did not paint the device through the renderer: this test would prove nothing.");
            var last = document.RootElement.GetProperty("last");
            Assert.True(Math.Abs(last.GetProperty("width").GetDouble() - start.Width) > 10,
                $"The export painted the device {last.GetProperty("width").GetDouble():0.#} px wide, as on the canvas ({start.Width:0.#}): not somewhere else.");
        }

        var json = await editor.LiveMarksOfCellAsync(DraggedId);
        output.WriteLine($"after: {json}");
        var (cell, marks) = Read(json);
        var load = Assert.Single(marks, mark => mark.Kind == "load" && mark.Visible);
        Assert.InRange(load.Centre.X, cell.Centre.X - Slack, cell.Centre.X + Slack);
        Assert.InRange(load.Centre.Y, cell.Centre.Y - Slack, cell.Centre.Y + Slack);
        AssertTheDotAt(cell, Assert.Single(marks, mark => mark.Kind == "status" && mark.Visible), dotPlace, Slack, "after the export");
    }

    /// <summary>
    /// Queue item 32 (owner's decision of 1/10): the red arrow of a step follows the PREVIEW of a
    /// dragged device as the load unit and the dot do (queue item 22), instead of staying on the old
    /// centre until the drop. A real mouse pressed on the device that runs the step and moved in
    /// several steps: before the button is let go the arrow starts from the centre of the device as
    /// painted, far from the old one, and still points at the device it goes to; after the drop it
    /// starts from where the device landed.
    /// </summary>
    [Fact]
    public async Task TheMotionArrowFollowsADeviceDraggedWithTheMouseAndLandsWithIt()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);
        Assert.Equal("drawn", await editor.RenderLiveFrameAsync(Frame(
            [LoadedAndInError(DraggedId), new Device(UntouchedId, "#ffa500", "Moving", null)],
            new Motion(DraggedId, UntouchedId))));
        var (start, _) = Read(await editor.LiveMarksOfCellAsync(DraggedId));
        var (destination, _) = Read(await editor.LiveMarksOfCellAsync(UntouchedId));
        AssertTheArrow(await TheArrowAsync(editor, MotionColour, "before the drag"), MotionColour, start.Centre, destination.Centre, "before the drag, the motion arrow");

        var box = await editor.CellBoxOnScreenAsync(DraggedId);
        var (x, y) = (box[0] + box[2] / 2, box[1] + box[3] / 2);
        await editor.PressTheMouseAtAsync(x, y);
        await editor.MoveTheMouseToAsync(x + 80, y + 40, steps: 8);
        await editor.MoveTheMouseToAsync(x + DragX, y + DragY, steps: 8);

        var preview = await ThePaintedDeviceAsync(editor, start.Scale, "in the middle of the drag");
        Assert.True(Distance(preview.Centre, start.Centre) > 100,
            $"The device is painted at ({preview.Centre.X:0.#}, {preview.Centre.Y:0.#}), not away from where it was " +
            $"({start.Centre.X:0.#}, {start.Centre.Y:0.#}): mxGraph did not start moving it, and this test would prove nothing.");
        AssertTheArrow(await TheArrowAsync(editor, MotionColour, "in the middle of the drag"), MotionColour, preview.Centre, destination.Centre, "in the middle of the drag, the motion arrow");

        await editor.ReleaseTheMouseAsync();

        var (dropped, _) = Read(await editor.LiveMarksOfCellAsync(DraggedId));
        // The premise of "landed": the drop put the device where the preview was.
        Assert.True(Distance(dropped.Centre, preview.Centre) <= PreviewSlack,
            $"The device landed at ({dropped.Centre.X:0.#}, {dropped.Centre.Y:0.#}), not where the preview was ({preview.Centre.X:0.#}, {preview.Centre.Y:0.#}).");
        AssertTheArrow(await TheArrowAsync(editor, MotionColour, "after the drop"), MotionColour, dropped.Centre, destination.Centre, "after the drop, the motion arrow");
    }

    /// <summary>
    /// Queue item 32 (owner's decision of 1/10): the route shown on hover - the orange arrow to the
    /// final destination and the yellow one to the next step - follows the preview of the hovered
    /// device while it is dragged, and goes back with it to the old centre when the drag is given up
    /// with Esc (mxGraph's reset repaints the device from its state, queue item 22): with the button
    /// still down, and after it is let go.
    /// </summary>
    /// <remarks>
    /// The owner's answer of 9/10: the hover stays alive while its device is dragged, although mxGraph
    /// takes the mouse off the dragged shape and nothing is under the pointer any more. The page runs
    /// on Playwright's clock, stopped from before the press: once the device is in the middle of the
    /// drag the clock runs through a whole grace window, and only then are the hover and the arrows
    /// read - a hover that had started leaving has ended by then whatever the speed of the machine,
    /// and on the browser's own clock this test raced the 300 ms instead (task 1, first attempt).
    /// </remarks>
    [Fact]
    public async Task TheRouteOnHoverFollowsADeviceDraggedWithTheMouseAndGoesBackOnEscape()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenWithAClockOfItsOwnAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);
        Assert.Equal("drawn", await editor.RenderLiveFrameAsync(Frame([])));
        await editor.EnableLiveHoverAsync();
        await editor.HoverTheMiddleOfCellAsync(DraggedId);
        await editor.WaitForHoverCallAsync(DraggedId);
        await editor.ShowLiveRouteAsync(DraggedId, UntouchedId, UntouchedId);

        var (start, _) = Read(await editor.LiveMarksOfCellAsync(DraggedId));
        var (destination, _) = Read(await editor.LiveMarksOfCellAsync(UntouchedId));
        foreach (var colour in RouteColours)
        {
            AssertTheArrow(await TheArrowAsync(editor, colour, "before the drag"), colour, start.Centre, destination.Centre, $"before the drag, the {colour} route arrow");
        }

        var box = await editor.CellBoxOnScreenAsync(DraggedId);
        var (x, y) = (box[0] + box[2] / 2, box[1] + box[3] / 2);
        await editor.StopTheClockAsync();
        await editor.PressTheMouseAtAsync(x, y);
        await editor.MoveTheMouseToAsync(x + 80, y + 40, steps: 8);
        await editor.MoveTheMouseToAsync(x + DragX, y + DragY, steps: 8);
        await editor.RunTheClockForAsync(GraceWindow);
        await editor.ResumeTheClockAsync();

        var preview = await ThePaintedDeviceAsync(editor, start.Scale, "in the middle of the drag");
        Assert.True(Distance(preview.Centre, start.Centre) > 100,
            "The premise: mxGraph did not start moving the device, and this test would prove nothing.");
        Assert.False(await HoverHasEndedAsync(editor),
            $"The hover ended in the middle of the drag, a grace window of {GraceWindow} ms after the device left the mouse: {await editor.HoverCallsAsync()}");
        foreach (var colour in RouteColours)
        {
            AssertTheArrow(await TheArrowAsync(editor, colour, "in the middle of the drag"), colour, preview.Centre, destination.Centre, $"in the middle of the drag, the {colour} route arrow");
        }

        await editor.PressAsync("Escape");

        // The premise of "back": Esc did give the drag up, and the device is painted where it was.
        var back = await ThePaintedDeviceAsync(editor, start.Scale, "after Esc, the button still down");
        Assert.True(Distance(back.Centre, start.Centre) <= PreviewSlack,
            $"After Esc the device is still painted at ({back.Centre.X:0.#}, {back.Centre.Y:0.#}), not back at " +
            $"({start.Centre.X:0.#}, {start.Centre.Y:0.#}): draw.io did not give the drag up.");
        foreach (var colour in RouteColours)
        {
            AssertTheArrow(await TheArrowAsync(editor, colour, "after Esc, the button still down"), colour, start.Centre, destination.Centre, $"after Esc, the button still down, the {colour} route arrow");
        }

        await editor.ReleaseTheMouseAsync();

        foreach (var colour in RouteColours)
        {
            AssertTheArrow(await TheArrowAsync(editor, colour, "after letting go"), colour, start.Centre, destination.Centre, $"after letting go, the {colour} route arrow");
        }
    }

    /// <summary>
    /// Queue item 32 (owner's answer of 9/10): the hover kept alive through a drag must still end when
    /// the drag ends off the canvas. The device is dragged straight off draw.io's container onto the
    /// shapes panel - the container's one mouseleave comes while the hover is kept, and no other comes
    /// after it - and the button is let go there: the hover ends a grace window later, as any leaving
    /// does (fix round 1, finding 4). On Playwright's clock, stopped from before the press: the hover
    /// holds through a whole grace window with the button down, and ends within one after it is let go.
    /// </summary>
    [Fact]
    public async Task TheHoverOfADeviceDraggedOffTheCanvasEndsWhenTheButtonIsLetGoThere()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenWithAClockOfItsOwnAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);
        Assert.Equal("drawn", await editor.RenderLiveFrameAsync(Frame([])));
        await editor.EnableLiveHoverAsync();
        await editor.HoverTheMiddleOfCellAsync(DraggedId);
        await editor.WaitForHoverCallAsync(DraggedId);

        var box = await editor.CellBoxOnScreenAsync(DraggedId);
        var (x, y) = (box[0] + box[2] / 2, box[1] + box[3] / 2);
        await editor.StopTheClockAsync();
        await editor.PressTheMouseAtAsync(x, y);
        await editor.MoveTheMouseToAsync(x + 80, y + 40, steps: 8);
        await editor.MoveMouseOffTheCanvasContainerAsync();
        await editor.RunTheClockForAsync(GraceWindow);
        Assert.False(await HoverHasEndedAsync(editor),
            $"The hover ended while its device was still being dragged, off the canvas: {await editor.HoverCallsAsync()}");

        await editor.ReleaseTheMouseAsync();
        await editor.RunTheClockForAsync(GraceWindow);
        Assert.True(await HoverHasEndedAsync(editor),
            $"The hover did not end a grace window of {GraceWindow} ms after the device was let go off the canvas: {await editor.HoverCallsAsync()}");

        await editor.ResumeTheClockAsync();
        Assert.Equal(0, await editor.UnhandledRejectionCountAsync());
    }

    /// <summary>The colour of the motion arrow of a step, as the live module paints it.</summary>
    private const string MotionColour = "#ff0000";

    /// <summary>The colours of the route on hover: to the final destination, to the next step.</summary>
    private static readonly string[] RouteColours = ["#ff8c00", "#ffd700"];

    /// <summary>The one visible arrow of this colour on the canvas, the start and the end of its shaft (<see cref="DrawioEditorPage.LiveArrowsAsync"/>).</summary>
    private async Task<((double X, double Y) Start, (double X, double Y) End)> TheArrowAsync(DrawioEditorPage editor, string colour, string when)
    {
        var json = await editor.LiveArrowsAsync(colour);
        output.WriteLine($"{when}: {colour} arrows {json}");
        using var document = JsonDocument.Parse(json);
        var arrow = Assert.Single(document.RootElement.EnumerateArray());
        var start = arrow.GetProperty("start");
        var end = arrow.GetProperty("end");
        return ((start[0].GetDouble(), start[1].GetDouble()), (end[0].GetDouble(), end[1].GetDouble()));
    }

    /// <summary>The dragged device as painted right now (<see cref="ReadPainted"/>).</summary>
    private async Task<Cell> ThePaintedDeviceAsync(DrawioEditorPage editor, double scale, string when)
    {
        var painted = await editor.PaintedDeviceOfCellAsync(DraggedId);
        output.WriteLine($"{when}: painted {painted}");
        return ReadPainted(painted, scale);
    }

    /// <summary>
    /// How far short of the centre it points at the shaft of an arrow ends, in screen pixels, by
    /// colour: mxConnector stops the line where the head begins (endArrow=block, endSize=6), and the
    /// head grows with the stroke - measured at scale 1, 11.7 px for the motion arrow (stroke 3) and
    /// 14.5 px for the two of the route (stroke 4).
    /// </summary>
    private static readonly Dictionary<string, double> HeadRoom = new()
    {
        [MotionColour] = 11.7,
        ["#ff8c00"] = 14.5,
        ["#ffd700"] = 14.5,
    };

    /// <summary>
    /// The shaft of an arrow of <paramref name="colour"/> starts within <see cref="PreviewSlack"/> of
    /// <paramref name="from"/> and points at <paramref name="to"/>: the centre lies on the line of the
    /// shaft, and the shaft ends short of it by the head (<see cref="HeadRoom"/>).
    /// </summary>
    private static void AssertTheArrow(((double X, double Y) Start, (double X, double Y) End) arrow, string colour,
        (double X, double Y) from, (double X, double Y) to, string what)
    {
        Assert.True(Distance(arrow.Start, from) <= PreviewSlack,
            $"{what} starts at ({arrow.Start.X:0.#}, {arrow.Start.Y:0.#}), {Distance(arrow.Start, from):0.#} px from ({from.X:0.#}, {from.Y:0.#}).");
        var length = Distance(arrow.Start, arrow.End);
        var offLine = Math.Abs((arrow.End.X - arrow.Start.X) * (to.Y - arrow.Start.Y) - (arrow.End.Y - arrow.Start.Y) * (to.X - arrow.Start.X)) / length;
        var shortBy = Distance(arrow.End, to);
        Assert.True(offLine <= PreviewSlack && Math.Abs(shortBy - HeadRoom[colour]) <= PreviewSlack && length < Distance(arrow.Start, to),
            $"{what} ends at ({arrow.End.X:0.#}, {arrow.End.Y:0.#}), {shortBy:0.#} px from ({to.X:0.#}, {to.Y:0.#}) " +
            $"and {offLine:0.#} px off the line to it: it does not point at it, {HeadRoom[colour]} px short.");
    }

    /// <summary>Where the drag tests take the device, in screen pixels from where it was pressed: onto empty canvas, inside the browser's window.</summary>
    private const double DragX = 160;

    private const double DragY = 90;

    /// <summary>
    /// How far a mark may be from the device as PAINTED, in screen pixels. Measured at scale 1: the
    /// load unit's centre and the device image's agree to a hundredth of a pixel, mid-drag and
    /// mid-resize. The two are painted by different shapes, though, and each may carry mxShape's half
    /// pixel for crisp edges (getSvgScreenOffset, which depends on the stroke width at the scale):
    /// the margin is queue item 20's pixel and a half, the "pixel or two" of the queue item, and far
    /// below what the defect costs - the marks left behind at the old place, 188 px away after this
    /// drag, 31 px (and 53 px too narrow) after this resize.
    /// </summary>
    private const double PreviewSlack = 1.5;

    /// <summary>
    /// Queue item 22: reads the device as painted and its marks, and asserts the load unit centred on
    /// the device as painted and the dot inside its top right corner, at <paramref name="dotPlace"/>
    /// from its edges (<see cref="DotPlace"/>, measured before the gesture) - painted geometry on both
    /// sides, no view state. Returns the device as painted and the cell's view state.
    /// </summary>
    private async Task<(Cell Painted, Cell State)> AssertTheMarksAreOnThePaintedDeviceAsync(
        DrawioEditorPage editor, string when, (double FromRight, double FromTop) dotPlace)
    {
        var painted = await editor.PaintedDeviceOfCellAsync(DraggedId);
        var json = await editor.LiveMarksOfCellAsync(DraggedId);
        output.WriteLine($"{when}: painted {painted}, marks {json}");
        var (cell, marks) = Read(json);
        var device = ReadPainted(painted, cell.Scale);

        var load = Assert.Single(marks, mark => mark.Kind == "load" && mark.Visible);
        Assert.True(Distance(load.Centre, device.Centre) <= PreviewSlack,
            $"{when}: the load unit is centred at ({load.Centre.X:0.#}, {load.Centre.Y:0.#}), the device is painted " +
            $"centred at ({device.Centre.X:0.#}, {device.Centre.Y:0.#}) - the cell's state says ({cell.Centre.X:0.#}, {cell.Centre.Y:0.#}).");
        AssertTheDotAt(device, Assert.Single(marks, mark => mark.Kind == "status" && mark.Visible), dotPlace, PreviewSlack, when);
        return (device, cell);
    }

    /// <summary>
    /// The status dot entirely inside the device and in its top right quarter, in the device's own
    /// unturned frame (y grows downwards). At 0.25 the seed's 60-unit side is 15 px on screen, one more
    /// than the dot: there it can only be centred on that side, which is why the Y bound is "not below
    /// the middle".
    /// </summary>
    private static void AssertTheDotIsInsideTheTopRightCorner(Cell device, Mark dot, double slack, string when)
    {
        foreach (var corner in dot.Corners)
        {
            var local = device.Local(corner);
            Assert.True(Math.Abs(local.X) <= device.Width / 2 + slack && Math.Abs(local.Y) <= device.Height / 2 + slack,
                $"{when}: a corner of the status dot, ({corner.X:0.#}, {corner.Y:0.#}), is outside the device.");
        }
        var centre = device.Local(dot.Centre);
        Assert.True(centre.X > 0, $"{when}: the dot is on the device's left half (x {centre.X:0.##} from its centre).");
        Assert.True(centre.Y <= slack, $"{when}: the dot is on the device's bottom half (y {centre.Y:0.##} from its centre).");
    }

    /// <summary>
    /// Queue item 22: where the status dot sits in its device - how far its centre is from the
    /// device's right edge and from its top edge, in the device's own unturned frame. The dot has a
    /// fixed size and a fixed inset on screen (queue item 20), so on a device as large as the seed's
    /// this does not change with a move or a resize: measured before a gesture, held to during it.
    /// "Inside the top right quarter" alone cannot tell a dot that followed a resize from one left at
    /// the corner of before, which a grown device still has inside its top right quarter.
    /// </summary>
    private static (double FromRight, double FromTop) DotPlace(Cell device, Mark dot)
    {
        var centre = device.Local(dot.Centre);
        return (device.Width / 2 - centre.X, centre.Y + device.Height / 2);
    }

    /// <summary>The dot inside the device's top right corner (<see cref="AssertTheDotIsInsideTheTopRightCorner"/>) and at <paramref name="place"/> from its edges (<see cref="DotPlace"/>).</summary>
    private static void AssertTheDotAt(Cell device, Mark dot, (double FromRight, double FromTop) place, double slack, string when)
    {
        AssertTheDotIsInsideTheTopRightCorner(device, dot, slack, when);
        var at = DotPlace(device, dot);
        Assert.True(Math.Abs(at.FromRight - place.FromRight) <= slack && Math.Abs(at.FromTop - place.FromTop) <= slack,
            $"{when}: the status dot is {at.FromRight:0.#} px from the device's right edge and {at.FromTop:0.#} px from its top, " +
            $"it was {place.FromRight:0.#} and {place.FromTop:0.#} before the gesture.");
    }

    /// <summary>
    /// Queue item 21, owner's decisions 3 and 4: with marks that never take the mouse, what they say
    /// is in the device's own tooltip - the load unit as the page names it and the status - above
    /// whatever draw.io shows for the device anyway, which must still be there.
    /// </summary>
    [Fact]
    public async Task TheDeviceTooltipNamesTheLoadUnitAndTheStatus()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        Assert.Equal("drawn", await editor.RenderLiveFrameAsync(Frame([LoadedAndInError(DraggedId, loadUnit: "UC256355 (867294)")])));

        await editor.HoverTheMiddleOfCellAsync(DraggedId);
        var tooltip = await editor.WaitForTheTooltipAsync("Load unit UC256355 (867294)", "Status: In Error");
        output.WriteLine($"tooltip: {tooltip}");

        var drawios = await editor.DrawioOwnTooltipTextAsync(DraggedId);
        output.WriteLine($"draw.io's own: {drawios}");
        Assert.Contains(Squeezed(drawios), Squeezed(tooltip));
    }

    /// <summary>
    /// Fix round comments of the module, for queue item 21's marks and tooltip hook and queue item
    /// 22's renderer hook: forget() takes every mark away; clear() takes the marks AND both hooks away
    /// and lets go of the graph - also when every shape's own destroy() throws, and without throwing
    /// towards its caller.
    /// </summary>
    [Fact]
    public async Task ForgetAndClearLeaveNothingBehind()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        var frame = Frame([LoadedAndInError(DraggedId), new Device(UntouchedId, "#ffa500", "Moving", null)]);

        Assert.Equal("drawn", await editor.RenderLiveFrameAsync(frame));
        Assert.Equal(3, (await editor.LiveObjectsAsync())[0]);
        Assert.True(await editor.TheGraphHasATooltipHookAsync(), "The premise: adopting the graph puts the tooltip hook on it.");
        Assert.True(await editor.TheGraphHasARendererHookAsync(), "The premise: adopting the graph puts the renderer hook on it.");
        await editor.CallTheLiveModuleAsync("forget");
        Assert.Equal(0, (await editor.LiveObjectsAsync())[0]);

        Assert.Equal("drawn", await editor.RenderLiveFrameAsync(frame));
        Assert.Equal(3, (await editor.LiveObjectsAsync())[0]);
        Assert.False(await editor.CallThrowsWhenShapesAreBrokenAsync("clear"), "clear() let a broken shape's failure escape to its caller.");
        Assert.Equal(0, (await editor.LiveObjectsAsync())[0]);
        Assert.False(await editor.TheGraphHasATooltipHookAsync(), "clear() left the tooltip hook on the graph.");
        Assert.False(await editor.TheGraphHasARendererHookAsync(), "clear() left the renderer hook on the graph.");
    }

    /// <summary>
    /// Fix round 1, finding 6 and fix round 2, finding 3, for queue item 21's tooltip hook and queue
    /// item 22's renderer hook: an adoption that fails AFTER both hooks went on (the container's own
    /// listener is the last thing adopt() installs) takes them off again with everything else, and
    /// the next render adopts cleanly and puts them back. (Named AFailedAdoptionLeavesNoTooltipHookBehind
    /// until queue item 22 gave it the renderer's hook to look after too.)
    /// </summary>
    [Fact]
    public async Task AFailedAdoptionLeavesNoHookBehind()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        var frame = Frame([LoadedAndInError(DraggedId)]);

        Assert.Equal("failed", await editor.RenderLiveFrameOnALateBrokenAdoptionAsync(frame));
        Assert.False(await editor.TheGraphHasATooltipHookAsync(), "A failed adoption left the tooltip hook on the graph.");
        Assert.False(await editor.TheGraphHasARendererHookAsync(), "A failed adoption left the renderer hook on the graph.");

        Assert.Equal("drawn", await editor.RenderLiveFrameAsync(frame));
        Assert.True(await editor.TheGraphHasATooltipHookAsync(), "The adoption after the failed one did not put the tooltip hook on.");
        Assert.True(await editor.TheGraphHasARendererHookAsync(), "The adoption after the failed one did not put the renderer hook on.");
    }

    // ---- Reading the marks ----------------------------------------------------------------------

    /// <summary>The cell's unrotated box on screen, its rotation in degrees and the view's scale, with the plain geometry the tests need.</summary>
    private sealed record Cell(double X, double Y, double Width, double Height, double Rotation, double Scale)
    {
        public (double X, double Y) Centre => (X + Width / 2, Y + Height / 2);

        private double Angle => Rotation * Math.PI / 180;

        /// <summary>A point given in the cell's own unturned frame (from its centre, y downwards), turned with the cell - where it is on screen.</summary>
        public (double X, double Y) Turned(double x, double y) =>
            (Centre.X + x * Math.Cos(Angle) - y * Math.Sin(Angle), Centre.Y + x * Math.Sin(Angle) + y * Math.Cos(Angle));

        /// <summary>A point on screen, brought back into the cell's own unturned frame.</summary>
        public (double X, double Y) Local((double X, double Y) p)
        {
            var (dx, dy) = (p.X - Centre.X, p.Y - Centre.Y);
            return (dx * Math.Cos(Angle) + dy * Math.Sin(Angle), -dx * Math.Sin(Angle) + dy * Math.Cos(Angle));
        }
    }

    /// <summary>One mark as painted: its kind, whether it shows, and its box's corners top left, top right, bottom right, bottom left.</summary>
    private sealed record Mark(string Kind, bool Visible, (double X, double Y)[] Corners)
    {
        public (double X, double Y) Centre => (Corners.Average(c => c.X), Corners.Average(c => c.Y));
    }

    private static (Cell Cell, Mark[] Marks) Read(string json)
    {
        using var document = JsonDocument.Parse(json);
        var c = document.RootElement.GetProperty("cell");
        var cell = new Cell(c.GetProperty("x").GetDouble(), c.GetProperty("y").GetDouble(), c.GetProperty("width").GetDouble(),
            c.GetProperty("height").GetDouble(), c.GetProperty("rotation").GetDouble(), c.GetProperty("scale").GetDouble());
        var marks = document.RootElement.GetProperty("marks").EnumerateArray().Select(m => new Mark(
            m.GetProperty("kind").GetString()!,
            m.GetProperty("visible").GetBoolean(),
            [.. m.GetProperty("corners").EnumerateArray().Select(p => (p[0].GetDouble(), p[1].GetDouble()))])).ToArray();
        return (cell, marks);
    }

    /// <summary>
    /// Queue item 22: the device as PAINTED (<see cref="DrawioEditorPage.PaintedDeviceOfCellAsync"/>)
    /// as a <see cref="Cell"/> - its unturned box rebuilt from the four painted corners, its rotation
    /// the direction of its top edge, the view's scale as given.
    /// </summary>
    private static Cell ReadPainted(string json, double scale)
    {
        using var document = JsonDocument.Parse(json);
        var corners = document.RootElement.GetProperty("corners").EnumerateArray()
            .Select(p => (X: p[0].GetDouble(), Y: p[1].GetDouble())).ToArray();
        var (x, y) = (corners.Average(c => c.X), corners.Average(c => c.Y));
        var width = Distance(corners[0], corners[1]);
        var height = Distance(corners[1], corners[2]);
        var rotation = Math.Atan2(corners[1].Y - corners[0].Y, corners[1].X - corners[0].X) * 180 / Math.PI;
        return new Cell(x - width / 2, y - height / 2, width, height, rotation, scale);
    }

    /// <summary>The premise of the size tests: the seed's dragged device is 120 x 60 drawing units.</summary>
    private static void AssertTheSeedsDevice(Cell cell)
    {
        Assert.InRange(cell.Width / cell.Scale, 120 - 0.01, 120 + 0.01);
        Assert.InRange(cell.Height / cell.Scale, 60 - 0.01, 60 + 0.01);
    }

    private static void AssertCorners(Mark mark, params (double X, double Y)[] expected)
    {
        Assert.Equal(4, mark.Corners.Length);
        for (var i = 0; i < 4; i++)
        {
            Assert.InRange(mark.Corners[i].X, expected[i].X - Slack, expected[i].X + Slack);
            Assert.InRange(mark.Corners[i].Y, expected[i].Y - Slack, expected[i].Y + Slack);
        }
    }

    private static double Distance((double X, double Y) a, (double X, double Y) b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>How far apart two directions are, in degrees, whatever turn each is written with (-180 and 180 are the same).</summary>
    private static double AngleBetween(double a, double b)
    {
        var d = ((a - b) % 360 + 360) % 360;
        return Math.Min(d, 360 - d);
    }

    private static string Squeezed(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
