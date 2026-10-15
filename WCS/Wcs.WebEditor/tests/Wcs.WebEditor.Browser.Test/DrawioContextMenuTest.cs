using Wcs.Api.Contracts.Dtos;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// Compito 5, row 63: our entries in draw.io's own right-click menu on a device, measured in the real
/// draw.io this page hosts - which entries a device gets with the engine stopped and running, that
/// nothing else gets them, that an entry does the panel's own action (Properties selects the device
/// and brings the panel into view; a command reaches the host through the panel's commands), and that
/// the hook comes off at detach. The page's half of the same rules is in the bUnit suite
/// (DrawioEditorContextMenuTest, DrawioDeviceCommandsTest).
/// </summary>
public sealed class DrawioContextMenuTest(DrawioEditorFixture fixture, ITestOutputHelper output) : IClassFixture<DrawioEditorFixture>
{
    private const string SeedHolder = "browser-test-context-menu";

    private static readonly string[] PanelEntries = ["Properties", "Linked Storage Cells", "Mission Steps", "Duplicate Device"];

    private static readonly string[] CommandEntries = ["Execute Step", "Unload LU", "Set Off Line", "Set Ready", "Step Done"];

    /// <summary>
    /// The entries of the right-click menu, as the operator reads them (TheContextMenuAsync's
    /// "- label" lines) - of its FIRST block only: an mxPopupMenu is a div of class mxPopupMenu with a
    /// table of the same class inside it (mxClient.js), so every menu is listed twice, measured.
    /// </summary>
    private static List<string> EntriesOf(string menu)
    {
        var entries = new List<string>();
        var blocks = 0;
        foreach (var line in menu.Split('\n').Select(line => line.Trim()))
        {
            if (line.StartsWith("menu ", StringComparison.Ordinal) && ++blocks > 1) break;
            if (line.StartsWith("- ", StringComparison.Ordinal)) entries.Add(line[2..]);
        }

        return entries;
    }

    [Fact]
    public async Task WithTheEngineStoppedADeviceGetsThePanelsEntriesAndNoCommand()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForTheDeviceMenuAsync(PlantSeed.DraggedId.ToString(), isDevice: true, running: false);

        await editor.RightClickOnDeviceAsync(PlantSeed.DraggedName);
        var menu = await editor.TheContextMenuAsync();
        output.WriteLine($"Right-click on {PlantSeed.DraggedName}, engine stopped:{Environment.NewLine}{menu}");
        var entries = EntriesOf(menu);

        foreach (var entry in PanelEntries) Assert.Contains(entry, entries);
        foreach (var entry in CommandEntries) Assert.DoesNotContain(entry, entries);
        foreach (var entry in PanelEntries)
            AssertMenuIcon(await editor.ContextMenuEntryIconAsync(entry));
        // Ours are ADDED above draw.io's own entries, which stay: draw.io's "Duplicate" is the one
        // looked for. It is NOT row 63's duplicate - that is ours, "Duplicate Device", the panel's
        // plant-level one (WithDuplicateDeviceTheHostHasANewDeviceWithTheCopiedConfiguration below).
        // draw.io's own only copies the shape: on a device loaded from the plant - written without
        // wcsType (DrawioDocument.Build) - the clone is refused by the creation ("does not say which
        // device type it is") and by every save as a marked shape the plant does not know; measured
        // in fix round 1 of compito 5, the plant kept its two devices.
        Assert.Contains("Duplicate", entries);
        // Ours come first, above draw.io's own.
        Assert.Equal("Properties", entries[0]);
    }

    /// <summary>
    /// With the engine running the panel's four commands are there too, and one of them, chosen,
    /// reaches the host through the panel's own commands: its outcome line names the command the host
    /// queued, and the host has that command, for that device.
    /// </summary>
    [Fact]
    public async Task WithTheEngineRunningADeviceGetsTheCommandsAndOneReachesTheHost()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        try
        {
            await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
            await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
            await editor.ClickAsync("#drawio-engine-start");
            await editor.ClickAsync("#drawio-engine-confirm");
            await editor.WaitForTextAsync("#drawio-engine-state", "Engine: Run");
            await editor.WaitForTheDeviceMenuAsync(PlantSeed.DraggedId.ToString(), isDevice: true, running: true);

            await editor.RightClickOnDeviceAsync(PlantSeed.DraggedName);
            var menu = await editor.TheContextMenuAsync();
            output.WriteLine($"Right-click on {PlantSeed.DraggedName}, engine running:{Environment.NewLine}{menu}");
            var entries = EntriesOf(menu);
            foreach (var entry in PanelEntries) Assert.Contains(entry, entries);
            foreach (var entry in new[] { "Execute Step", "Unload LU", "Step Done" }) Assert.Contains(entry, entries);
            // One of the two, as the panel's own button says it for the device's live status.
            Assert.Single(entries, entry => entry is "Set Off Line" or "Set Ready");
            foreach (var entry in PanelEntries.Concat(entries.Where(entry => CommandEntries.Contains(entry))))
                AssertMenuIcon(await editor.ContextMenuEntryIconAsync(entry));

            await editor.ClickTheMenuEntryAsync("Execute Step");

            var queued = new Regex($@"Execute Step (queued for {PlantSeed.DraggedName} as command (\d+)|for {PlantSeed.DraggedName} \(command (\d+)\))");
            await editor.WaitForTextAsync("#drawio-command-outcome", queued);
            var outcome = (await editor.TextOfAsync("#drawio-command-outcome"))!;
            output.WriteLine($"The panel's outcome line: {outcome}");
            var match = queued.Match(outcome);
            var id = long.Parse(match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value);
            var command = await client.GetCommandAsync(id);
            Assert.NotNull(command);
            Assert.Equal((int)CommandTypes.ItemExecuteStep, command!.CommandType);
            Assert.Equal(PlantSeed.DraggedId, command.ItemId);
        }
        finally
        {
            await TestHostEngine.LeaveStoppedAsync(client, output);
        }
    }

    private static void AssertMenuIcon(DrawioContextMenuIcon icon)
    {
        Assert.StartsWith("data:image/svg+xml", icon.Source);
        Assert.Equal("", icon.Alt);
        Assert.Equal("true", icon.AriaHidden);
        Assert.Equal("presentation", icon.Role);
        Assert.True(icon.Complete);
        Assert.Equal(16, icon.NaturalWidth);
        Assert.Equal(16, icon.NaturalHeight);
    }

    /// <summary>
    /// Properties makes the device the only selection - another device was selected before - and
    /// brings the panel into view with the focus on it; the two information entries open their own
    /// windows and can remain visible together.
    /// Nothing of it enters draw.io's model: the drawing and its undo history are the same after.
    /// </summary>
    [Fact]
    public async Task PropertiesSelectsTheDeviceAndBringsThePanelIntoViewAndTouchesNothingInTheDrawing()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);
        await editor.WaitForTheDeviceMenuAsync(PlantSeed.DraggedId.ToString(), isDevice: true, running: false);
        // Both selected: a right-click on one member of a multi-selection keeps it whole, so the menu
        // is NOT already about the device alone - the entry has to make it so.
        await editor.SelectDevicesAsync(PlantSeed.UntouchedName, PlantSeed.DraggedName);
        var before = await editor.ModelAndUndoAsync();

        await editor.RightClickOnDeviceAsync(PlantSeed.DraggedName);
        await editor.ClickTheMenuEntryAsync("Properties");

        await editor.WaitForTheFocusOnAsync("drawio-property-panel");
        Assert.Equal([PlantSeed.DraggedId.ToString()], await editor.SelectedCellIdsAsync());
        Assert.Contains(PlantSeed.DraggedName, await editor.PropertyPanelNoticeAsync());

        await editor.OpenContextWindowAsync(PlantSeed.DraggedName, "Linked Storage Cells", "drawio-linked-storage-cells-window");
        Assert.Contains(PlantSeed.DraggedName, await editor.TextOfAsync("#drawio-linked-storage-cells-window > .drawio-window-title"));
        await editor.MoveWindowAwayFromDeviceAsync("drawio-linked-storage-cells-window", PlantSeed.DraggedName);

        await editor.OpenContextWindowAsync(PlantSeed.DraggedName, "Mission Steps", "drawio-mission-steps-window");
        Assert.Contains(PlantSeed.DraggedName, await editor.TextOfAsync("#drawio-mission-steps-window > .drawio-window-title"));
        await editor.WaitForTheWindowShownAsync("drawio-linked-storage-cells-window");

        // Choosing the entry again is an activation even when its window is already in front: it
        // must take focus back from draw.io, not silently leave the keyboard in the canvas.
        await editor.MoveWindowAwayFromDeviceAsync("drawio-mission-steps-window", PlantSeed.DraggedName);
        await editor.OpenContextWindowAsync(PlantSeed.DraggedName, "Mission Steps", "drawio-mission-steps-window");

        await editor.CloseTheWindowAsync("drawio-mission-steps-window");
        await editor.WaitForTheFocusOnAsync("drawio-frame");

        var after = await editor.ModelAndUndoAsync();
        Assert.Equal(before[1], after[1]);
        Assert.Equal(before[0], after[0]);
        Assert.Null(await editor.TextOfAsync("#drawio-menu-notice"));
    }

    /// <summary>
    /// A junction's arrow, a note drawn by hand and a shape marked as a device that the plant does not
    /// know get none of our entries - only draw.io's own. The device in the same page does, which is
    /// what makes the three "none" answers mean something.
    /// </summary>
    [Fact]
    public async Task NoEntriesOnAJunctionANoteOrAShapeThePlantDoesNotKnow()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        var junction = await PlantSeed.ResetAsync(client, SeedHolder, withJunction: true);
        const string note = "note-context-menu";
        const string orphan = "orphan-context-menu";
        await PlantSeed.SeedNoteAsync(client, SeedHolder, note, "A note");
        await PlantSeed.SeedOrphanDeviceCellAsync(client, SeedHolder, orphan, "Not on the plant");

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForTheDeviceMenuAsync(PlantSeed.DraggedId.ToString(), isDevice: true, running: false);
        await editor.WaitForTheDeviceMenuAsync(orphan, isDevice: false, running: false);

        await editor.RightClickOnDeviceAsync(PlantSeed.DraggedName);
        var onTheDevice = EntriesOf(await editor.TheContextMenuAsync());
        Assert.Contains("Properties", onTheDevice);

        foreach (var cellId in new[] { junction!.JunctionId.ToString(), note, orphan })
        {
            // Closed first: a right-click that lands on the menu still open over the canvas would
            // press one of ITS entries instead of opening a new menu.
            await editor.CloseTheContextMenuAsync();
            await editor.RightClickOnCellAsync(cellId);
            // The right-click really landed on that cell: draw.io selects what it opens a menu on.
            Assert.Equal([cellId], await editor.SelectedCellIdsAsync());
            var menu = await editor.TheContextMenuAsync();
            output.WriteLine($"Right-click on {cellId}:{Environment.NewLine}{menu}");
            var entries = EntriesOf(menu);
            Assert.NotEmpty(entries);
            foreach (var entry in PanelEntries.Concat(CommandEntries)) Assert.DoesNotContain(entry, entries);
        }
    }

    /// <summary>What the bridge put into draw.io's menu it takes away at detach: the menu is draw.io's own again.</summary>
    [Fact]
    public async Task TheMenuHookComesOffAtDetach()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForTheDeviceMenuAsync(PlantSeed.DraggedId.ToString(), isDevice: true, running: false);

        Assert.False(await editor.DetachTheBridgeAndSayWhetherItsPopupHookStayedAsync(),
            "After detach, draw.io's ui.menus still carries the createPopupMenu the bridge had put there.");
        output.WriteLine($"After detach the bridge holds: {await editor.TheDeviceMenuStateAsync()}");

        await editor.RightClickOnDeviceAsync(PlantSeed.DraggedName);
        var entries = EntriesOf(await editor.TheContextMenuAsync());
        Assert.Contains("Duplicate", entries);
        Assert.DoesNotContain("Properties", entries);
    }

    /// <summary>
    /// Row 63's duplicate, the plant-level one (compito 5 fix round 2): right-click on a device loaded
    /// from the plant, "Duplicate Device", the panel's question answered - and the HOST has a new
    /// device of the same type whose configuration is the source's but for the identifier, the name,
    /// the ASI ("XXXX") and the linked warehouse cell (none), placed 100 to the right and 100 below
    /// the source as it is drawn. The drawing shows it, selected,
    /// and holds work the close asks about, as after a device dropped and created. The plant is put
    /// back to the seed afterwards.
    /// </summary>
    [Fact]
    public async Task WithDuplicateDeviceTheHostHasANewDeviceWithTheCopiedConfiguration()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);
        var seeded = (await client.GetItemConfigurationAsync(PlantSeed.DraggedId))!;
        seeded.Asi = "ASI-7";
        seeded.Partition = "P9";
        seeded.TimeOutPlcMoving = 4321;
        seeded.AutoExecuteStep = true;
        seeded.RelocateItemId = PlantSeed.UntouchedId;
        seeded.LinkedCellId = 12m;
        await PlantSeed.SaveConfigurationAsync(client, SeedHolder, seeded);
        var source = (await client.GetItemConfigurationAsync(PlantSeed.DraggedId))!;
        var sourcePlace = (await client.GetItemAsync(PlantSeed.DraggedId))!;

        try
        {
            await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
            await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
            await editor.WaitForTheDeviceMenuAsync(PlantSeed.DraggedId.ToString(), isDevice: true, running: false);

            await editor.RightClickOnDeviceAsync(PlantSeed.DraggedName);
            await editor.ClickTheMenuEntryAsync("Duplicate Device");
            await editor.WaitForTheFocusOnAsync("drawio-duplicate-confirmation");
            var question = (await editor.TextOfAsync("#drawio-duplicate-question"))!;
            output.WriteLine($"The question: {question}");
            var name = new Regex("named \"([^\"]+)\"").Match(question).Groups[1].Value;
            Assert.StartsWith($"Duplicate {PlantSeed.DraggedName} as a new device named", question);

            await editor.ClickAsync("#drawio-duplicate-confirm");
            await editor.WaitForTextAsync("#drawio-duplicate-report", "is drawn on the canvas and selected");
            var report = (await editor.TextOfAsync("#drawio-duplicate-report"))!;
            output.WriteLine($"The report: {report}");
            // Placed next to the source AS DRAWN (fix round 3): the drawing answered where it is.
            Assert.Contains($"below where {PlantSeed.DraggedName} is drawn now.", report);
            Assert.Contains("the linked warehouse cell (12 -> none: link it from the panel if needed)", report);

            // Read back FROM THE HOST.
            var items = await client.GetItemsAsync();
            var created = Assert.Single(items, item => item.ItemId != PlantSeed.DraggedId && item.ItemId != PlantSeed.UntouchedId);
            Assert.Equal(name, created.Name);
            Assert.Equal(sourcePlace.DeviceType, created.DeviceType);
            // The source is drawn where the plant has it: through the save's own rule
            // (DrawnGeometry.Judge, fix round 4) that is the plant's geometry EXACTLY, moved by 100 -
            // no drift from the drawing's two decimals, at the seed's 30 degrees.
            Assert.Equal(sourcePlace.X + 100, created.X);
            Assert.Equal(sourcePlace.Y + 100, created.Y);
            Assert.Equal(sourcePlace.Width, created.Width);
            Assert.Equal(sourcePlace.Height, created.Height);
            Assert.Equal(sourcePlace.Rotation, created.Rotation);
            var copy = (await client.GetItemConfigurationAsync(created.ItemId))!;
            Assert.Equal("XXXX", copy.Asi);
            // Owner's decision (fix round 3): no linked warehouse cell on the duplicate.
            Assert.Equal(0m, copy.LinkedCellId);
            Assert.Equal(name, copy.Name);
            foreach (var property in typeof(PlantItemConfigDto).GetProperties().Where(p => p.CanWrite
                         && p.Name is not (nameof(PlantItemConfigDto.ItemId) or nameof(PlantItemConfigDto.Name) or nameof(PlantItemConfigDto.Asi)
                             or nameof(PlantItemConfigDto.LinkedCellId))))
                Assert.True(Equals(property.GetValue(source), property.GetValue(copy)),
                    $"{property.Name}: the source has {property.GetValue(source)} on the host, the duplicate {property.GetValue(copy)}.");

            // The drawing: the new cell, under its own identifier, drawn and selected.
            await editor.WaitForDeviceAsync(name);
            Assert.Equal([created.ItemId.ToString()], await editor.SelectedCellIdsAsync());

            AssertAsked(await editor.CloseTheTabAsync());
        }
        finally
        {
            await PlantSeed.ResetAsync(client, SeedHolder);
        }
    }

    /// <summary>
    /// Final review, M1: the cell is added and then a listener of draw.io's model throws inside the
    /// same update. The cell IS on the drawing, so the bridge answers that it is drawn (not selected),
    /// not that it failed - and the page keeps it: on the canvas, on the host, and said.
    /// </summary>
    [Fact]
    public async Task ADuplicateAddedBeforeAListenerThrewIsSaidAsDrawn()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        try
        {
            await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
            await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
            await editor.WaitForTheDeviceMenuAsync(PlantSeed.DraggedId.ToString(), isDevice: true, running: false);
            await editor.RightClickOnDeviceAsync(PlantSeed.DraggedName);
            await editor.ClickTheMenuEntryAsync("Duplicate Device");
            await editor.WaitForTheFocusOnAsync("drawio-duplicate-confirmation");
            await editor.MakeTheNextModelChangeThrowInAListenerAsync();

            await editor.ClickAsync("#drawio-duplicate-confirm");

            await editor.WaitForTextAsync("#drawio-duplicate-report", "is drawn on the canvas, but it was not selected");
            var created = Assert.Single(await client.GetItemsAsync(),
                item => item.ItemId != PlantSeed.DraggedId && item.ItemId != PlantSeed.UntouchedId);
            await editor.WaitForDeviceAsync(created.Name!);
        }
        finally
        {
            await PlantSeed.ResetAsync(client, SeedHolder);
        }
    }

    /// <summary>
    /// Fix round 4, MINOR 1: the cell is added and then its selection fails - the bridge answers that
    /// it IS drawn, not that it failed, and the page keeps it: on the canvas, on the host, and said.
    /// The selection is broken on purpose, only after the question is on screen, so the right-click
    /// that asked it still worked.
    /// </summary>
    [Fact]
    public async Task ADuplicateDrawnButNotSelectableIsSaidAndStays()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        try
        {
            await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
            await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
            await editor.WaitForTheDeviceMenuAsync(PlantSeed.DraggedId.ToString(), isDevice: true, running: false);
            await editor.RightClickOnDeviceAsync(PlantSeed.DraggedName);
            await editor.ClickTheMenuEntryAsync("Duplicate Device");
            await editor.WaitForTheFocusOnAsync("drawio-duplicate-confirmation");
            await editor.MakeSelectingACellFailAsync();

            await editor.ClickAsync("#drawio-duplicate-confirm");

            await editor.WaitForTextAsync("#drawio-duplicate-report", "is drawn on the canvas, but it was not selected (draw.io could not select it");
            var created = Assert.Single(await client.GetItemsAsync(),
                item => item.ItemId != PlantSeed.DraggedId && item.ItemId != PlantSeed.UntouchedId);
            await editor.WaitForDeviceAsync(created.Name!);
        }
        finally
        {
            await PlantSeed.ResetAsync(client, SeedHolder);
        }
    }

    /// <summary>
    /// Queue item 24, final review: the copy is drawn and selected, and then bringing it into view
    /// fails - centring it reads the page's windows and boxes and moves draw.io's view. That is not a
    /// selection that failed: the bridge answers 'ok', the page says the copy is drawn and selected,
    /// and it is the selected cell, its properties on the panel. Inside the selection's try, the
    /// centring's failure was answered 'ok-not-selected', and the page told the operator to click a
    /// copy draw.io had already selected. Broken on purpose after the question is on screen, as the
    /// selection above.
    /// </summary>
    [Fact]
    public async Task ADuplicateThatCannotBeBroughtIntoViewIsStillSaidSelected()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        try
        {
            await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
            await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
            await editor.WaitForTheDeviceMenuAsync(PlantSeed.DraggedId.ToString(), isDevice: true, running: false);
            await editor.RightClickOnDeviceAsync(PlantSeed.DraggedName);
            await editor.ClickTheMenuEntryAsync("Duplicate Device");
            await editor.WaitForTheFocusOnAsync("drawio-duplicate-confirmation");
            await editor.MakeBringingACellIntoViewFailAsync();

            await editor.ClickAsync("#drawio-duplicate-confirm");

            await editor.WaitForTextAsync("#drawio-duplicate-report", "is drawn on the canvas and selected");
            var created = Assert.Single(await client.GetItemsAsync(),
                item => item.ItemId != PlantSeed.DraggedId && item.ItemId != PlantSeed.UntouchedId);
            Assert.Equal([created.ItemId.ToString()], await editor.SelectedCellIdsAsync());
        }
        finally
        {
            await PlantSeed.ResetAsync(client, SeedHolder);
        }
    }

    private static void AssertAsked(DrawioTabClosing closing) =>
        Assert.True(closing.Asked, "The close did not ask: the duplicate's cell is work on the drawing, as a device dropped and created is.");
}
