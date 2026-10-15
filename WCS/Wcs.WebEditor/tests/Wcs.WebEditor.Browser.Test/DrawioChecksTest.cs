using Wcs.Api.Contracts.Dtos;
using Xunit;
using Xunit.Abstractions;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// The two plant checks on <c>/editor/drawio</c>, judged the way they will be used: a plant with a
/// problem, the check that finds it, and a click that takes the operator to the device it is about.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the test the batch is worth</b>, and above all it is the one that MEASURES the
/// decision the batch is shaped by: that reaching a problem selects the cell instead of colouring
/// it, and that a selection does <b>not</b> go into the history Ctrl+Z walks. That claim was read
/// out of the bundle first (<c>Editor.prototype.createUndoManager</c> subscribes the model and the
/// view, and <c>mxGraphSelectionModel</c> fires its undoable edit on itself), but reading is not
/// measuring - and this branch has already had an exclusive claim about that very bundle turn out
/// false in front of everybody. Here the undo manager's own two numbers are read before and after a
/// real selection, and then Ctrl+Z is pressed for real to see what it undoes.
/// </para>
/// <para>
/// <b>The counterfactual was measured too</b>, on 2026-09-18, with a throwaway probe against this
/// very host and this very bundle, and then deleted: on a freshly opened plant
/// <c>graph.setCellStyles('strokeColor', '#dc2626', [cell])</c> - which is what "colour the
/// offending cells" means - took the undo manager from <c>[0, 0]</c> to <c>[1, 1]</c>, one
/// SIGNIFICANT entry. A selection through the product's own road, measured by the second test below
/// in the same conditions, leaves the pair exactly where it was. The batch's shape is therefore not
/// a precaution: the road not taken really does cost the operator a press.
/// </para>
/// <para>
/// Nothing here exists in bUnit: draw.io, its selection model, its undo manager, the bridge's
/// <c>selectCell</c> and the Blazor circuit that carries the click. <c>DrawioEditorChecksTest</c>
/// measures what the page does with the answers; this one measures that the answers are real.
/// </para>
/// </remarks>
public sealed class DrawioChecksTest(DrawioEditorFixture fixture, ITestOutputHelper output) : IClassFixture<DrawioEditorFixture>
{
    private const string SeedHolder = "browser-test-checks";

    /// <summary>
    /// A device seeded with a configuration and <b>no geometry</b>: the plant really holds it, and
    /// <c>PlantGraph.IsPlaced</c> says no, so <c>DrawioDocument</c> draws no cell for it. It is the
    /// shape of a warning with nothing on the canvas to go to, and it is a state a real plant reaches
    /// (a device created and never placed).
    /// </summary>
    private static readonly Guid UnplacedId = new("a1b2c3d4-0000-4000-8000-00000000000f");

    private const string UnplacedName = "UnplacedDevice";

    /// <summary>
    /// How far the device is dragged before the selection, in screen pixels. Big enough that the
    /// move is unmistakable when it is undone and unmistakable when it is not.
    /// </summary>
    private const double DragBy = 120;

    /// <summary>
    /// <b>The plant has a problem, the check finds it, and getting to it lands on the cell.</b> Both
    /// seeded devices are isolated - the seed draws no junction - and the page offers a way to each.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three things are asserted about one click, and they are three different facts: draw.io's own
    /// selection (read out of its graph, not out of the page), the page's selection line (which
    /// travels back through the bridge, so it says the round trip closed), and the properties panel
    /// (which is the reason selecting is the right gesture - the operator lands where the fixing is
    /// done).
    /// </para>
    /// <para>
    /// And the device that CANNOT be reached is part of the same test rather than a second one: the
    /// two answers come from the same real bridge, and a page that said "selected" for both would be
    /// the silent loss this Editor exists not to make.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheJunctionCheckFindsTheLooseDevicesAndGettingToOneSelectsItsCell()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);
        await SeedAnUnplacedDeviceAsync(client);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);

        await editor.OpenTheChecksAsync();

        // Nothing has run by itself: opening the box is not pressing a button. Without this the whole
        // test would be green on a page that ran the check at load and never listened to the button.
        Assert.Null(await editor.JunctionCheckResultAsync());

        await editor.RunTheJunctionCheckAsync();

        // The panel names the problems - all three devices are isolated, and the unplaced one is
        // named twice (isolated AND unplaced).
        await editor.WaitForCheckTargetAsync(PlantSeed.UntouchedName);
        var warnings = await editor.JunctionWarningsAsync();
        Assert.Contains(warnings, line => line.Contains(PlantSeed.DraggedName) && line.Contains("no junction"));
        Assert.Contains(warnings, line => line.Contains(UnplacedName) && line.Contains("not placed"));

        var targets = await editor.CheckTargetsAsync();
        Assert.Equal(3, targets.Count);

        // THE CLICK. It lands on the cell inside draw.io...
        await editor.GoToTheDeviceAsync(PlantSeed.UntouchedName);
        await editor.WaitForGoToOutcomeAsync("selected in the drawing");
        Assert.Equal([PlantSeed.UntouchedId.ToString()], await editor.SelectedInTheDrawingAsync());
        Assert.Equal("drawio-checks-goto-reached", await editor.GoToOutcomeToneAsync());

        // ...the page hears it back through the bridge...
        await editor.WaitForSelectionLineAsync($"Selected: {PlantSeed.UntouchedName}");

        // ...and the properties of that device are on screen, which is the point of selecting rather
        // than colouring: the operator lands where the fixing is done.
        await editor.WaitForPropertyAsync("Name", PlantSeed.UntouchedName);

        // And the one there is nothing to select for is SAID, not silently ignored - and it is said
        // in the colour of a miss. The COLOUR is the second claim, and it is the one that only this
        // sequence can judge: the line goes red only if the success just measured above has been
        // taken back. A mutation that deleted that reset left the whole suite of the day green - 955
        // in memory and the two browser tests there were then - because the words were read here and
        // the class was not.
        await editor.GoToTheDeviceAsync(UnplacedName);
        await editor.WaitForGoToOutcomeAsync("no cell in the drawing");
        var missed = await editor.GoToOutcomeAsync();
        Assert.Contains("on the plant either way", missed);
        Assert.Equal("drawio-checks-goto-missed", await editor.GoToOutcomeToneAsync());
        // The previous selection is not claimed to have changed: draw.io still has the cell that
        // really was selected.
        Assert.Equal([PlantSeed.UntouchedId.ToString()], await editor.SelectedInTheDrawingAsync());
    }

    /// <summary>
    /// <b>"Check Configuration" really goes out to the host, and whatever comes back is SAID.</b>
    /// The button, the Blazor circuit, the client and the host's own route, end to end - the one road
    /// the fake handler of <c>DrawioEditorChecksTest</c> cannot stand in for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What it asserts is what holds on any machine: an outcome appears - the rows, "verificata con
    /// successo", or the failure line - and the summary line stops saying that the check is running.
    /// It deliberately does <b>not</b> assert WHICH answer: whether the host this suite starts has a
    /// warehouse behind it is a property of the machine, and a test that demanded one of the two
    /// would be green here and red elsewhere for a reason that has nothing to do with the Editor.
    /// </para>
    /// <para>
    /// It can fail, and it was measured failing: the host's route was made to answer a shape the
    /// client cannot read, and this test went red on "the configuration check never said anything at
    /// all" while every bUnit test - which answers that route with a fake of its own - stayed green.
    /// That gap is what this test is for. It would go red the same way on a button wired to nothing,
    /// on a route that answers nothing at all, and on a summary line left saying "the configuration
    /// check is running" after the answer has arrived.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheConfigurationCheckReallyAsksTheHostAndTheAnswerReachesTheScreen()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);

        await editor.OpenTheChecksAsync();
        // Nothing has been asked of the host yet: opening the box runs neither of the two checks.
        Assert.Null(await editor.ConfigCheckOutcomeAsync());

        await editor.RunTheConfigurationCheckAsync();

        var outcome = await editor.WaitForAConfigurationCheckOutcomeAsync();
        // Written to the log and not asserted: which of the two answers came back depends on the
        // machine, and whoever reads a failure of this test needs to see which one it was.
        output.WriteLine($"The configuration check answered: {outcome}");
        await editor.WaitForTheSummaryToRestAsync();
        Assert.Equal("Plant checks", await editor.ChecksSummaryAsync());
    }

    /// <summary>
    /// A configuration check that fails says so in a red box, and the box fits inside the checks'
    /// window (passata grafica, fix round of task 4). The window's first line lays the two buttons side
    /// by side and each result across the whole width under them; the red box - a padding and a border
    /// round its text - came out 22 px wider than the window's content and could not shrink: a scrollbar
    /// across the window, and the box's right edge cut off. The failure is certain here: this suite's
    /// host has no warehouse behind it (WcsHostProcess points <c>Wms:ApiAddress</c> at an address that
    /// answers nothing), and the configuration check is the warehouse's procedure.
    /// </summary>
    [Fact]
    public async Task AFailedConfigurationCheckFitsInsideItsWindow()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.OpenTheChecksAsync();
        await editor.RunTheConfigurationCheckAsync();

        var outcome = await editor.WaitForAConfigurationCheckOutcomeAsync();
        output.WriteLine($"The configuration check answered: {outcome}");
        Assert.True(await editor.TextOfAsync("#config-check-error") is not null,
            $"The configuration check did not fail, and this test measures its failure's box. It answered: {outcome}");

        var panel = await editor.BoxOfAsync("#drawio-checks .checks-panel");
        var error = await editor.BoxOfAsync("#config-check-error");
        Assert.True(error[0] + error[2] <= panel[0] + panel[2] + 0.5,
            FormattableString.Invariant(
                $"The failure's box ends at x {error[0] + error[2]:0.##}, past the right edge of the checks' panel at {panel[0] + panel[2]:0.##}."));
        var across = await editor.OverflowAcrossAsync("#drawio-checks > .drawio-window-content");
        Assert.True(across <= 0, $"The checks' window scrolls across: what it holds runs {across} px past its right edge.");
    }

    /// <summary>
    /// The "Go to" buttons the junction check offers wear the page's one look, like the two checks'
    /// buttons above them in the same window (passata grafica, second fix round of task 4): corners of
    /// 6 px, the strong border, white, grey under the mouse and a darker grey pressed. Until then a rule
    /// of their own (<c>.drawio-checks-target</c>) gave them the old corners of 3 px, the light border
    /// and a background of their own - and that background, weighing more than the page's hover and
    /// pressed rules, kept them the same colour whatever the mouse did. The check's buttons are measured
    /// too, against the same palette, so a failure says which of the two drifted. Every press is let go
    /// elsewhere, so none of them is a click: at the end nothing has been gone to - read once the page
    /// has rendered a gesture made after them (a window opened by its button), so that a click on its
    /// way through the circuit would be on screen by then.
    /// </summary>
    [Fact]
    public async Task TheGoToButtonsWearThePagesLookLikeTheChecksButtons()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);
        await editor.OpenTheChecksAsync();
        await editor.RunTheJunctionCheckAsync();
        await editor.WaitForCheckTargetAsync(PlantSeed.UntouchedName);

        const string windowName = "#drawio-checks .drawio-window-name";
        DrawioLook.AssertThePagesPlainButton(await editor.ButtonLookAsync("#check-junctions-button", windowName), output);
        DrawioLook.AssertThePagesPlainButton(await editor.ButtonLookAsync(
            $"button.drawio-checks-target[data-item-id='{PlantSeed.UntouchedId}']", windowName), output);

        // None of the presses was a click: nothing was gone to. Read after a gesture the page must render
        // - it takes the circuit's messages in order, so by then a click would be on screen too.
        await editor.OpenWindowAsync("drawio-mission-list");
        Assert.Null(await editor.GoToOutcomeAsync());
    }

    /// <summary>
    /// <b>THE MEASUREMENT THE BATCH RESTS ON.</b> Getting to a problem selects a cell, and a
    /// selection does not enter draw.io's undo history: the operator's own gesture is still what one
    /// Ctrl+Z undoes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is measured twice over, because the two measurements fail for different reasons. First the
    /// undo manager's own numbers - <c>indexOfNextAdd</c> and the length of its history - which say
    /// whether anything was ADDED at all; a selection that went in would move them. Then the press
    /// itself, which is what the operator actually does: after the selection, one Ctrl+Z must put the
    /// dragged device back where it was. A selection sitting in the history would eat that press, and
    /// the device would stay where the drag left it.
    /// </para>
    /// <para>
    /// The drag before it is not scenery: without a gesture in the history there would be nothing for
    /// a press to be stolen from, and both halves would be green on a page that had never selected
    /// anything at all. The test therefore asserts first that the drag really moved the cell and
    /// really put an entry in the history.
    /// </para>
    /// <para>
    /// The position is read out of draw.io's MODEL and not off the screen: the canvas can scroll
    /// under a selection (<c>scrollCellToVisible</c> is exactly what the bridge asks for), so a
    /// comparison of screen boxes would report a move that is only the viewport's.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ReachingAProblemSelectsTheCellWithoutCostingTheOperatorAnUndo()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);

        var dragged = PlantSeed.DraggedId.ToString();
        var before = await editor.CellPositionAsync(dragged);
        var emptyHistory = await editor.UndoDepthAsync();

        // The operator's own gesture: one drag, one undoable edit.
        await editor.DragDeviceAsync(PlantSeed.DraggedName, DragBy, 0);
        var moved = await editor.CellPositionAsync(dragged);
        Assert.True(Math.Abs(moved[0] - before[0]) > 1,
            $"The drag did not move the cell in draw.io's model ({before[0]:0.#} -> {moved[0]:0.#}): there is no gesture for a selection to steal a press from.");

        var afterDrag = await editor.UndoDepthAsync();
        Assert.True(afterDrag[0] > emptyHistory[0],
            $"The drag put nothing in draw.io's undo history (indexOfNextAdd {emptyHistory[0]} -> {afterDrag[0]}): the measurement below would be green whatever the selection did.");

        // THE SELECTION, through the product's own road: the check, and a click on the problem.
        await editor.OpenTheChecksAsync();
        await editor.RunTheJunctionCheckAsync();
        await editor.WaitForCheckTargetAsync(PlantSeed.UntouchedName);
        await editor.GoToTheDeviceAsync(PlantSeed.UntouchedName);
        await editor.WaitForGoToOutcomeAsync("selected in the drawing");
        Assert.Equal([PlantSeed.UntouchedId.ToString()], await editor.SelectedInTheDrawingAsync());

        // FIRST MEASUREMENT: the history has not grown, and nothing has been added on top of the drag.
        var afterSelection = await editor.UndoDepthAsync();
        output.WriteLine($"undo stack: empty=[{emptyHistory[0]},{emptyHistory[1]}] afterDrag=[{afterDrag[0]},{afterDrag[1]}] afterSelection=[{afterSelection[0]},{afterSelection[1]}]");
        output.WriteLine(string.Join(Environment.NewLine, await editor.UndoStackAsync()));
        Assert.Equal(afterDrag, afterSelection);

        // SECOND MEASUREMENT, and it is the one the operator feels: ONE press puts the drag back.
        await editor.UndoAsync();
        var undone = await WaitForThePositionAsync(editor, dragged, before[0]);
        Assert.True(Math.Abs(undone - before[0]) < 1,
            $"One Ctrl+Z after the selection did not put the dragged device back: it is at {undone:0.#}, it was at {before[0]:0.#} before the drag and at {moved[0]:0.#} after it. The press went somewhere else.");
    }

    /// <summary>
    /// Waits for draw.io's model to carry the position an undo is meant to restore, and says where it
    /// really was if it never does.
    /// </summary>
    /// <remarks>
    /// Waiting is not optional and it is not a hedge: <c>mxUndoManager.undo</c> runs on the key press
    /// and mxGraph consolidates the model afterwards, so reading straight after the press reads the
    /// state before it. The condition is the position, not a delay, and the ceiling is only the point
    /// at which a press that did nothing becomes a failure that can be read.
    /// </remarks>
    private static async Task<double> WaitForThePositionAsync(DrawioEditorPage editor, string cellId, double expected)
    {
        var giveUpAt = DateTime.UtcNow.AddSeconds(10);
        double last;
        while (true)
        {
            last = (await editor.CellPositionAsync(cellId))[0];
            if (Math.Abs(last - expected) < 1) return last;
            if (DateTime.UtcNow > giveUpAt) return last;
            await Task.Delay(100);
        }
    }

    /// <summary>
    /// Puts a device on the plant with a configuration and no geometry. <c>PlantSeed.AddDeviceAsync</c>
    /// cannot: it writes a geometry, which is exactly what this one must not have.
    /// </summary>
    private static async Task SeedAnUnplacedDeviceAsync(Wcs.Api.Client.WcsClient client)
    {
        await PlantSeed.WaitForTheEditPermitToBeFreeAsync(client, SeedHolder);
        await client.AcquireEditLockAsync(SeedHolder);
        client.EditHolder = SeedHolder;
        try
        {
            await client.CreateItemAsync(new PlantItemConfigDto
            {
                ItemId = UnplacedId,
                Name = UnplacedName,
                Asi = UnplacedName,
                DeviceType = (short)DeviceTypes.Automation,
                ConnectionType = (short)ConnectionTypes.Test,
            });
        }
        finally
        {
            client.EditHolder = null;
            await client.ReleaseEditLockAsync(SeedHolder);
        }
    }
}
