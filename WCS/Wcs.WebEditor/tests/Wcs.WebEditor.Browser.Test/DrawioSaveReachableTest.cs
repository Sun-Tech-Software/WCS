using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// Queue item 8: draw.io's own Save button stays in reach of the mouse whatever sections of the strip
/// are open, in the smallest window the Editor supports and in the one every other test uses - and a
/// save made through it with the device list open reaches the plant. Nothing else of the page moves
/// for it: the page never scrolls, and the drawing keeps all the room the window leaves it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The fix</b> is in the bridge (<c>keepTheSaveButtonClear</c> in drawio-bridge.js, which says
/// why a floor under the drawing with the page scrolling was measured and set aside): draw.io's
/// windows are placed just under the button, and the page's layout does not change at all.
/// </para>
/// <para>
/// <b>What covered it.</b> draw.io builds its Format window at the top right of its frame, 60 px
/// down and 600 px tall (<c>EditorUi.initMinimalTheme</c> in the shipped app.min.js, when the frame
/// is at least 1000 px wide), right under the Save button horizontally. Whenever the frame is
/// shorter than that window's bottom, draw.io pushes the window UP to stay inside - onto the button -
/// and nothing ever brings it back down, not even shutting the section again. Measured on the code
/// before this item: at 1600x1000 a section that left the frame shorter than about 640 px
/// (Operations, or a list at its ceiling: 389-447 px) put the Format window's title bar under the
/// button's middle; at 1366x768 the frame was 557-592 px tall with NO section open (557 with this
/// test's plant, whose devices without a shape add a strip), and the button was covered from the
/// first second.
/// </para>
/// <para>
/// <b>Why 1366x768 is the smallest window supported.</b> It is the smallest common laptop screen,
/// and draw.io opens its Format window by itself only when its frame is at least 1000 px wide -
/// that is, from a window of about 1320 px (the properties column takes 320). A narrower window
/// gets no Format window unless the operator opens it, and the bridge keeps that one clear too -
/// measured once, not by this test: at 1280x720, the Format window opened through its own action and
/// then the device list opened, the window went to 47 px down instead of 0.
/// </para>
/// <para>
/// <b>Since the floating windows (spec 2026-09-29)</b> the premise this test was written on - an open
/// section takes its height from the drawing, and draw.io's Format window, pushed up by the shorter
/// frame, covers the button - no longer holds: the sections open in windows over the canvas, any
/// number at a time, and the frame keeps its place and its height. What stays true, and is what this
/// test measures now: with the windows opened one after the other and all six open together, draw.io's
/// frame neither moves nor shrinks by a pixel; the button stays reachable, covered neither by draw.io's
/// own windows (the bridge's fix above still matters at 1366x768, where the frame is short with nothing
/// open) nor by the page's windows where they open by default; and the save through it still reaches
/// the plant.
/// </para>
/// <para>
/// <b>What is read.</b> What lies under the middle of the button, asked of draw.io's document and of
/// the page's (<see cref="DrawioEditorPage.SaveButtonReachAsync"/>) - the same hit test Playwright's
/// click makes, but told with the name of what covers it instead of as a click that timed out. Then
/// the click itself, by <see cref="DrawioEditorPage.SaveAsync"/>, and the plant read back.
/// </para>
/// </remarks>
public sealed class DrawioSaveReachableTest(DrawioEditorFixture fixture, ITestOutputHelper output) : IClassFixture<DrawioEditorFixture>
{
    private const string SeedHolder = "browser-test-save-reachable";

    /// <summary>Devices drawn next to the two of the seed, with a junction each way between neighbours.</summary>
    private const int DrawnDevices = 6;

    /// <summary>
    /// Devices of the plant with no shape: rows of the device list and nothing of the drawing (see
    /// <see cref="PlantSeed.AddManyDevicesAsync"/> for why the rows do not come from drawn devices).
    /// With the others, 38 rows over four pages. The device window is shortened so even its first
    /// ten-row page scrolls inside the grid and must not reach the page.
    /// </summary>
    private const int ShapelessDevices = 30;

    private const int AllDevices = 2 + DrawnDevices + ShapelessDevices;

    /// <summary>
    /// Every window of the bar, by its button and its window, in the bar's order: the plant checks - a
    /// window since the passata grafica (29/9) - and the six sections.
    /// </summary>
    private static readonly (string Name, string Toggle, string Window)[] Sections =
    [
        ("Plant checks", "#drawio-checks-toggle", "drawio-checks"),
        ("Plant file", "#drawio-plant-file-toggle", "drawio-plant-file"),
        ("Operations", "#drawio-operations-toggle", "drawio-operations"),
        ("Devices", "#drawio-device-list-toggle", "drawio-device-list"),
        ("Junctions", "#drawio-junction-list-toggle", "drawio-junction-list"),
        ("Missions", "#drawio-mission-list-toggle", "drawio-mission-list"),
        ("PLC connections", "#drawio-plc-connections-toggle", "drawio-plc-connections"),
    ];

    [Theory]
    [InlineData(1366, 768)]
    [InlineData(1600, 1000)]
    public async Task DrawiosSaveButtonStaysReachableWithTheWindowsOpenAndSaves(int width, int height)
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);
        await PlantSeed.AddManyDevicesAsync(client, SeedHolder, DrawnDevices, ShapelessDevices);

        await using var editor = await DrawioEditorPage.OpenInAWindowOfAsync(fixture.Browser, fixture.Host, width, height);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);

        var seen = new List<(string State, DrawioSaveButtonReach Reach)> { ("no section open", await editor.SaveButtonReachAsync()) };

        // Some work to save: the real gesture, with no section open. On the device of the seed that
        // no junction touches (an arrow crossing a shape's middle takes the press) and that lies clear
        // of draw.io's Shapes window at the left and of its Format window at the right.
        var (before, after) = await editor.DragDeviceAsync(PlantSeed.UntouchedName, 40, 30);
        Assert.True(after.X > before.X && after.Y > before.Y,
            $"The drag did not move the shape on the canvas: it was at ({before.X:0.#}, {before.Y:0.#}), it is at " +
            $"({after.X:0.#}, {after.Y:0.#}).");

        // One after the other, each left open: at the end all seven are open together, as they may be.
        var opened = new List<string>();
        foreach (var (name, toggle, window) in Sections)
        {
            await editor.ClickAsync(toggle);
            await editor.WaitForTheSectionAsync(window, open: true);
            await WaitForTheSectionsRowsAsync(editor, window);
            if (window == "drawio-device-list")
            {
                // Selection follows the dragged UntouchedDevice onto page four. Explicitly choose
                // a full page before testing overflow, rather than assuming the selection's page.
                await editor.ClickAsync("#drawio-device-list button[aria-label='Page 1']");
                await editor.WaitForTextAsync("#drawio-device-list-page-summary", $"1–10 of {AllDevices}");
                await editor.DragAsync("#drawio-device-list .drawio-window-grip", 0, -160);
            }
            opened.Add(name);
            seen.Add(($"{string.Join(" + ", opened)} open", await editor.SaveButtonReachAsync()));
        }
        Assert.Equal(Sections.Select(section => section.Window), seen[^1].Reach.OpenWindows);

        // The paged list's grid, rather than its fixed tools/pager container, owns the overflow.
        await editor.WaitForTextAsync("#drawio-device-list-page-summary", $"1–10 of {AllDevices}");
        Assert.True(await editor.WindowContentOutgrowsItAsync("drawio-device-list", ".drawio-data-grid-scroll"),
            $"{width}x{height}: the shortened device grid does not scroll its ten visible rows.");
        Assert.False(await editor.WindowContentOutgrowsItAsync("drawio-device-list"));

        // Shut from the one in front: the button of a window open behind another brings it to the
        // front instead of shutting it (the owner's decision D1 of 29/9), and they were opened in the
        // strip's order, each in front of the one before.
        foreach (var (_, toggle, window) in Enumerable.Reverse(Sections))
        {
            await editor.ClickAsync(toggle);
            await editor.WaitForTheSectionAsync(window, open: false);
        }

        // Shut again: draw.io never moves a window back down once it has pushed it up, so the
        // button has to be free here as well, not only while the windows were open.
        seen.Add(("every window shut again", await editor.SaveButtonReachAsync()));

        // The device list open for the save.
        await editor.ClickAsync("#drawio-device-list-toggle");
        await editor.WaitForTheSectionAsync("drawio-device-list", open: true);
        await WaitForTheSectionsRowsAsync(editor, "drawio-device-list");
        seen.Add(("Devices open, for the save", await editor.SaveButtonReachAsync()));

        var table = new StringBuilder();
        foreach (var (state, reach) in seen) table.AppendLine($"{width}x{height}, {state}: {reach}");
        output.WriteLine(table.ToString());

        var covered = seen.Where(s => !s.Reach.Reachable).Select(s => s.State).ToList();
        Assert.True(covered.Count == 0,
            $"{width}x{height}: draw.io's Save button is covered with {string.Join(", ", covered)}.{Environment.NewLine}{table}");

        // Nothing else moved for it. The page is the one it was: it never scrolls, and the drawing
        // takes all the room the window leaves it (measured: a floor under the drawing that made the
        // page scroll instead also made the first press on the drawing scroll the page under the
        // hand, 44 px at 1366x768 - the device dragged went 44 px further than the mouse).
        var reshaped = seen.Where(s => s.Reach.PageScrollHeight > s.Reach.WindowHeight + 1
                                       || Math.Abs(s.Reach.FrameHeight - s.Reach.WindowRoom) > 1)
            .Select(s => s.State).ToList();
        Assert.True(reshaped.Count == 0,
            $"{width}x{height}: the page scrolls, or the drawing does not take the room the window leaves it, with " +
            $"{string.Join(", ", reshaped)}.{Environment.NewLine}{table}");

        // And the page's windows took nothing from the drawing: draw.io's frame is where it was, as tall
        // as it was, with any of them open, with all of them, and once they are shut again.
        var moved = seen.Where(s => s.Reach.FrameTop != seen[0].Reach.FrameTop || s.Reach.FrameHeight != seen[0].Reach.FrameHeight)
            .Select(s => s.State).ToList();
        Assert.True(moved.Count == 0,
            $"{width}x{height}: draw.io's frame moved or changed height with {string.Join(", ", moved)}.{Environment.NewLine}{table}");

        // And draw.io's own windows are all still there, each inside draw.io's frame: kept clear of
        // the button, not closed and not pushed out of sight. Two pixels of slack at the bottom:
        // draw.io's own shrink fits a window's style height to the frame and leaves its 1 px border
        // on each side outside (measured on the code before this item: the Shapes window 433 px in a
        // frame of 431).
        var windowsAtFirst = seen[0].Reach.DrawioWindows.Select(w => w.Title).OrderBy(t => t).ToList();
        foreach (var (state, reach) in seen)
        {
            Assert.Equal(windowsAtFirst, reach.DrawioWindows.Select(w => w.Title).OrderBy(t => t).ToList());
            Assert.All(reach.DrawioWindows, w => Assert.True(w.Y >= 0 && w.Y + w.Height <= reach.FrameHeight + 2,
                $"{width}x{height}, {state}: draw.io's window {w} is not inside its frame, {reach.FrameHeight} px tall."));
        }

        await editor.SaveAsync();
        Assert.Null(await editor.SaveErrorAsync());
        var written = await PlantSeed.ReadGeometryAsync(client, PlantSeed.UntouchedId);
        var seeded = PlantSeed.UntouchedGeometry;
        Assert.True(written.X > seeded.X && written.Y > seeded.Y,
            $"The save through draw.io's button did not write the move: the device is at ({written.X}, {written.Y}), " +
            $"seeded at ({seeded.X}, {seeded.Y}). {await editor.ReportAsync()}");

        // What the bridge put on draw.io's windows goes with it when the page detaches it.
        Assert.False(await editor.DetachTheBridgeAndSayWhetherItsWindowHookStayedAsync(),
            "The bridge's hold on draw.io's windows (mxWindow.prototype.fit) is still there after detach.");
    }

    /// <summary>
    /// A list is measured once its rows are there: its count line names every device (or junction)
    /// of the plant. The other sections have nothing to read from the host before they show.
    /// </summary>
    private static Task WaitForTheSectionsRowsAsync(DrawioEditorPage editor, string window) => window switch
    {
        "drawio-device-list" => editor.WaitForTextAsync("#drawio-device-list-count", $"{AllDevices} devices."),
        "drawio-junction-list" => editor.WaitForTextAsync("#drawio-junction-list-count", $"{2 * (DrawnDevices - 1)} junctions."),
        _ => Task.CompletedTask,
    };
}
