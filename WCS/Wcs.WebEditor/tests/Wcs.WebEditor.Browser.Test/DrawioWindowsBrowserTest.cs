using Microsoft.Playwright;
using Wcs.Api.Contracts.Dtos;
using Xunit;
using Xunit.Abstractions;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// The floating windows of the draw.io page (spec 2026-09-29), against a real test host and a real
/// draw.io: the gestures with the real mouse - moving by the title bar across draw.io's iframe,
/// resizing by the grip - the memory of place, size and order across a reload, a smaller browser, the
/// window touched last in front, the strip's button of a window behind another, Esc and the focus,
/// a row of a list window and a device just duplicated reaching the drawing where no window - and no
/// panel of draw.io - covers it, the view left still when the copy is born where it can be seen
/// whole - in the largest free part that holds it, even when a larger one is too thin, centred in what
/// can be seen of it, and held up by the box it covers on the screen, turned and at the zoom of the
/// moment - and the bar with its buttons and the lines under it that no window ever covers.
/// </summary>
/// <remarks>
/// <para>
/// What bUnit cannot measure is all of drawio-windows.js: bUnit renders no layout and runs no
/// JavaScript, so the component tests stop at "the module was called with these arguments". Esc is
/// here for the same reason: the module catches it, not Blazor.
/// </para>
/// <para>
/// <b>Every test starts from a browser with no memory of the windows.</b> The fixture's browser is
/// shared, but <see cref="DrawioEditorPage.OpenAsync(Microsoft.Playwright.IBrowser, WcsHostProcess)"/>
/// opens a new browser context for every test, and a context's localStorage starts empty and dies
/// with it. <see cref="OpenAsync"/> asserts it rather than trusting it: a test that inherited the
/// windows of another would read their places instead of the cascade.
/// </para>
/// <para>
/// Not here: that opening the windows leaves draw.io's frame where it was. DrawioSaveReachableTest
/// already opens all seven, one after the other, and checks the frame's top and height at every step.
/// </para>
/// </remarks>
public sealed class DrawioWindowsBrowserTest(DrawioEditorFixture fixture, ITestOutputHelper output) : IClassFixture<DrawioEditorFixture>
{
    private const string SeedHolder = "browser-test-windows";

    /// <summary>Below this many px two edges count as the same edge: the browser's coordinates are fractional.</summary>
    private const double SamePixel = 0.5;

    /// <summary>The device of <c>OpenAsync(withAnUndrawnDevice: true)</c>, which the drawing cannot show.</summary>
    private static readonly Guid UndrawnId = new("a1b2c3d4-0000-4000-8000-0000000000b1");

    /// <summary>
    /// The seven windows of the bar, in the order of the page's cascade: the six sections in the strip's
    /// order, and the plant checks - a window since the passata grafica (29/9), seventh in the cascade
    /// although their button comes first in the bar.
    /// </summary>
    private static readonly string[] Sections =
    [
        "drawio-plant-file",
        "drawio-operations",
        "drawio-device-list",
        "drawio-junction-list",
        "drawio-mission-list",
        "drawio-plc-connections",
        "drawio-checks",
    ];

    /// <summary>
    /// The buttons of the bar with the engine stopped, in the page's order: the engine's gesture, the
    /// plant checks' and the six sections' - every one of them must stay within reach.
    /// </summary>
    private static readonly string[] BarButtons =
    [
        "drawio-engine-start",
        "drawio-checks-toggle",
        "drawio-plant-file-toggle",
        "drawio-operations-toggle",
        "drawio-device-list-toggle",
        "drawio-junction-list-toggle",
        "drawio-mission-list-toggle",
        "drawio-plc-connections-toggle",
    ];

    [Theory]
    [InlineData("drawio-operations", 560, 320)]
    [InlineData("drawio-plc-connections", 980, 560)]
    public async Task OperationalWindowsOpenAtTheirUsefulDefaults(string windowId, double width, double height)
    {
        await using var editor = await OpenAsync(window: (1600, 1000));
        await editor.OpenWindowAsync(windowId);
        var box = await editor.BoxOfAsync($"#{windowId}");
        Assert.InRange(Math.Abs(box[2] - width), 0, 1);
        if (windowId == "drawio-operations")
        {
            // Auto-sized: as tall as its content needs, up to the tab's ceiling - with this much room
            // the whole content is shown, nothing of it scrolls.
            Assert.InRange(box[3], 160, height);
            Assert.False((await editor.OperationalWindowLayoutAsync(windowId)).ContentScrolls,
                $"With a 1600x1000 browser the auto-sized Operations window ({box[3]:0}px) does not show its whole content.");
        }
        else
            Assert.InRange(Math.Abs(box[3] - height), 0, 1);
    }

    /// <summary>
    /// The spec of 14/10: each tab of Operations has a preferred size, clamped to the visible editor
    /// area, and "the content area scrolls if the available screen is smaller than the tab's preferred
    /// size". Test Route asks for 680 x 500; a 760 x 420 browser has less room than that under the
    /// floor, so the window stops at the browser's bottom edge and its content scrolls inside it.
    /// </summary>
    [Fact]
    public async Task TheAutoSizedOperationsWindowScrollsItsContentWhenTheBrowserIsTooSmall()
    {
        await using var editor = await OpenAsync(window: (760, 420));
        await editor.OpenWindowAsync("drawio-operations");
        await editor.ClickAsync("#drawio-operations-tab-test-route");
        await editor.WaitForShownAsync("#drawio-test-route", "Test Route tab panel");
        var floor = (await editor.TheStripAndItsWindowsAsync()).Floor;
        Assert.True(420 - floor < 500, FormattableString.Invariant($"The test is blind: with the floor at {floor:0.##} a 760x420 browser has room for Test Route's 500px."));

        await editor.WaitForTheBottomEdgeAtAsync("#drawio-operations", 420);
        var layout = await editor.OperationalWindowLayoutAsync("drawio-operations");
        Assert.True(layout.ContentScrolls, "Capped by the browser, the Operations window does not let its content scroll.");
        Assert.Equal("auto", layout.ContentOverflowY);
        Assert.True(layout.ControlsFitHorizontally);
    }

    /// <summary>
    /// A detached window is a real browser window: the keys do in it what they always do, and the
    /// page hears what they did. The LU type is emptied and typed key by key - not filled - and the
    /// page's own answer proves each value reached it: "Not a valid number." comes for the empty field
    /// and goes for 17. Enter and Space on a button, and the arrows of the tab list, are the keyboard's
    /// clicks and the tabs' own keys.
    /// </summary>
    [Fact]
    public async Task ADetachedOperationsWindowStaysInteractiveAndCanReturnToTheEditor()
    {
        await using var editor = await OpenAsync(window: (1400, 900));
        await editor.OpenWindowAsync("drawio-operations");

        var popup = await editor.DetachWindowAsync("drawio-operations");
        Assert.Equal(0, await editor.ElementCountAsync("#drawio-operations"));
        Assert.Equal(1, await popup.Locator("#drawio-operations").CountAsync());
        Assert.Equal("true", await popup.Locator("#drawio-operations-popout").GetAttributeAsync("aria-pressed"));
        Assert.Equal(1, await popup.Locator("#drawio-operations [role='tabpanel']")
            .EvaluateAllAsync<int>("panels => panels.filter(panel => panel.checkVisibility()).length"));

        await popup.Locator("#drawio-operations-tab-test-route").ClickAsync();
        var luType = popup.Locator("#drawio-test-route-lu-type");
        await luType.ClickAsync();
        await popup.Keyboard.PressAsync("Control+A");
        await popup.Keyboard.PressAsync("Backspace");
        await popup.Locator("#drawio-test-route-lu-type-error").WaitForAsync(new() { State = WaitForSelectorState.Visible });
        await popup.Keyboard.TypeAsync("17");
        await popup.Locator("#drawio-test-route-lu-type-error").WaitForAsync(new() { State = WaitForSelectorState.Detached });
        // The error goes already with the "1", a valid number: the value is read with a retrying
        // expectation, or it can be read while the page is still taking the "7" (CI, 16/10).
        await Microsoft.Playwright.Assertions.Expect(luType).ToHaveValueAsync("17");

        // The tabs' arrows: from Test Route to Remove all LU, the page selects it and moves the focus.
        await popup.Locator("#drawio-operations-tab-test-route").FocusAsync();
        await popup.Keyboard.PressAsync("ArrowRight");
        await Microsoft.Playwright.Assertions.Expect(popup.Locator("#drawio-operations-tab-remove-all"))
            .ToHaveAttributeAsync("aria-selected", "true");
        await Microsoft.Playwright.Assertions.Expect(popup.Locator("#drawio-operations-tab-remove-all")).ToBeFocusedAsync();
        await popup.Locator("#drawio-remove-all-lu").WaitForAsync(new() { State = WaitForSelectorState.Visible });

        // Enter on a button asks the question, Space on Cancel takes it away.
        await popup.Locator("#drawio-remove-all-lu-button").FocusAsync();
        await popup.Keyboard.PressAsync("Enter");
        await popup.Locator("#drawio-remove-all-lu-question").WaitForAsync(new() { State = WaitForSelectorState.Visible });
        await popup.Locator("#drawio-remove-all-lu-cancel").FocusAsync();
        await popup.Keyboard.PressAsync("Space");
        await popup.Locator("#drawio-remove-all-lu-question").WaitForAsync(new() { State = WaitForSelectorState.Detached });

        // Back on Test Route, the value typed is the page's: switching tabs discarded nothing.
        await popup.Locator("#drawio-operations-tab-test-route").ClickAsync();
        await popup.Locator("#drawio-test-route").WaitForAsync(new() { State = WaitForSelectorState.Visible });
        Assert.Equal("17", await luType.InputValueAsync());
        Assert.Equal(0, await popup.Locator("#drawio-test-route-lu-type-error").CountAsync());

        await popup.Locator("#drawio-operations-popout").ClickAsync();
        await editor.WaitForDetachedWindowBackAsync("drawio-operations");
        await WaitForCloseAsync(popup);
        Assert.Equal(1, await editor.ElementCountAsync("#drawio-operations"));
    }

    /// <summary>
    /// The Devices list opens a cell's editor on a double click, and the editor's checkbox is a click
    /// too: in a detached window both must reach the page. The number is typed key by key, written,
    /// and read back from the host; the click on the checkbox must reach the editor's document - where
    /// Blazor hears the clicks - before its change, and the flag written is read back as well.
    /// </summary>
    [Fact]
    public async Task ADetachedDevicesWindowEditsACellOnADoubleClick()
    {
        await using var editor = await OpenAsync(window: (1400, 900));
        using var client = fixture.CreateClient();
        var before = await client.GetItemConfigurationAsync(PlantSeed.UntouchedId)
            ?? throw new InvalidOperationException("The seeded device has no configuration on the host.");
        await editor.OpenWindowAsync("drawio-device-list");
        await editor.WaitForDataGridAsync("drawio-device-list");
        var popup = await editor.DetachWindowAsync("drawio-device-list");
        var row = $"#drawio-device-list-table tr[data-item-id='{PlantSeed.UntouchedId}']";

        var wanted = before.MaxUnloadWeight + 4321;
        var wantedText = wanted.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await popup.Locator($"{row} td[data-property='MaxUnloadWeight']").DblClickAsync();
        var value = popup.Locator("#drawio-device-list-editor-value");
        await value.WaitForAsync(new() { State = WaitForSelectorState.Visible });
        await value.ClickAsync();
        await popup.Keyboard.PressAsync("Control+A");
        await popup.Keyboard.TypeAsync(wantedText);
        await Microsoft.Playwright.Assertions.Expect(popup.Locator("#drawio-device-list-editor-preview")).ToContainTextAsync(wantedText);
        await popup.Locator("#drawio-device-list-editor-write").ClickAsync();
        await Microsoft.Playwright.Assertions.Expect(popup.Locator("#drawio-device-list-outcome")).ToContainTextAsync("Written:");
        Assert.Equal(wanted, (await client.GetItemConfigurationAsync(PlantSeed.UntouchedId))?.MaxUnloadWeight);

        await editor.RecordTheClicksOfTheEditorsDocumentAsync();
        await popup.Locator($"{row} td[data-property='AutoExecuteStep']").DblClickAsync();
        var check = popup.Locator("#drawio-device-list-editor-value");
        await check.WaitForAsync(new() { State = WaitForSelectorState.Visible });
        Assert.Equal(before.AutoExecuteStep, await check.IsCheckedAsync());
        await check.ClickAsync();
        Assert.Contains("drawio-device-list-editor-value", await editor.TheClicksOfTheEditorsDocumentAsync());
        await popup.Locator("#drawio-device-list-editor-write").ClickAsync();
        await Microsoft.Playwright.Assertions.Expect(popup.Locator($"{row} td[data-property='AutoExecuteStep']"))
            .ToHaveTextAsync(before.AutoExecuteStep ? "No" : "Yes");
        Assert.Equal(!before.AutoExecuteStep, (await client.GetItemConfigurationAsync(PlantSeed.UntouchedId))?.AutoExecuteStep);
    }

    /// <summary>
    /// A click in a detached list must leave the list where the operator scrolled it: the row chosen
    /// is selected by the page (its mark comes back over the circuit), and the grid's scroll offsets
    /// are what they were before the click.
    /// </summary>
    [Fact]
    public async Task AClickInADetachedListKeepsItsScrollPosition()
    {
        await using var editor = await OpenAsync(window: (1400, 900));
        await editor.OpenWindowAsync("drawio-device-list");
        await editor.WaitForDataGridAsync("drawio-device-list");
        var popup = await editor.DetachWindowAsync("drawio-device-list");
        await popup.SetViewportSizeAsync(520, 200);

        var scrolled = await popup.EvaluateAsync<double[]>(
            """
            () => {
              const grid = document.querySelector('#drawio-device-list .drawio-data-grid-scroll');
              grid.scrollLeft = 240;
              grid.scrollTop = grid.scrollHeight;
              return [grid.scrollLeft, grid.scrollTop];
            }
            """);
        Assert.True(scrolled[0] > 0 && scrolled[1] > 0, $"The test is blind: the detached Devices grid did not scroll ({scrolled[0]}, {scrolled[1]}).");

        // The middle of the last row's part on screen: a click that needs no scrolling to land.
        var point = await popup.EvaluateAsync<double[]>(
            """
            () => {
              const grid = document.querySelector('#drawio-device-list .drawio-data-grid-scroll').getBoundingClientRect();
              const rows = [...document.querySelectorAll('#drawio-device-list-table tbody tr')];
              const row = rows[rows.length - 1].getBoundingClientRect();
              return [grid.left + grid.width / 2, (Math.max(row.top, grid.top) + Math.min(row.bottom, grid.bottom)) / 2];
            }
            """);
        await popup.Mouse.ClickAsync((float)point[0], (float)point[1]);
        await Microsoft.Playwright.Assertions.Expect(popup.Locator("#drawio-device-list-table tbody tr:last-child"))
            .ToHaveAttributeAsync("aria-selected", "true");

        var after = await popup.EvaluateAsync<double[]>(
            "() => { const grid = document.querySelector('#drawio-device-list .drawio-data-grid-scroll'); return [grid.scrollLeft, grid.scrollTop]; }");
        Assert.Equal(scrolled, after);
    }

    /// <summary>
    /// The size of a detached Operations window is the operator's once it is open: the page renders
    /// it again and again (every key, every live frame), and only another tab - another preferred
    /// size - may resize the browser window. Counted on the separate window's own resizeTo.
    /// </summary>
    [Fact]
    public async Task ADetachedAutoSizedWindowIsResizedOnlyWhenItsTabChanges()
    {
        await using var editor = await OpenAsync(window: (1400, 900));
        await editor.OpenWindowAsync("drawio-operations");
        var popup = await editor.DetachWindowAsync("drawio-operations");
        await popup.EvaluateAsync(
            "() => { window.__wcsResizes = 0; const original = window.resizeTo.bind(window); window.resizeTo = (w, h) => { window.__wcsResizes++; original(w, h); }; }");

        await popup.Locator("#drawio-operations-tab-test-route").ClickAsync();
        await popup.Locator("#drawio-test-route").WaitForAsync(new() { State = WaitForSelectorState.Visible });
        await popup.Locator("#drawio-operations-tab-mode").ClickAsync();
        await popup.Locator("#drawio-mode").WaitForAsync(new() { State = WaitForSelectorState.Visible });
        Assert.Equal(2, await popup.EvaluateAsync<int>("() => window.__wcsResizes"));

        // Two more renders of the same tab: the question, then its Cancel.
        await popup.Locator("#drawio-mode-toggle").ClickAsync();
        await popup.Locator("#drawio-mode-question").WaitForAsync(new() { State = WaitForSelectorState.Visible });
        await popup.Locator("#drawio-mode-cancel").ClickAsync();
        await popup.Locator("#drawio-mode-question").WaitForAsync(new() { State = WaitForSelectorState.Detached });
        Assert.Equal(2, await popup.EvaluateAsync<int>("() => window.__wcsResizes"));
    }

    /// <summary>
    /// A narrow separate window still shows its title bar's buttons: the minimum sizes that keep a
    /// window usable in the editor (PLC connections: 440 x 420) are not the browser window's, and what
    /// does not fit scrolls inside.
    /// </summary>
    [Fact]
    public async Task ANarrowDetachedWindowKeepsItsTitleBarButtonsOnScreen()
    {
        await using var editor = await OpenAsync(window: (1400, 900));
        await editor.OpenWindowAsync("drawio-plc-connections");
        var popup = await editor.DetachWindowAsync("drawio-plc-connections");
        await popup.SetViewportSizeAsync(360, 300);

        foreach (var button in new[] { "#drawio-plc-connections-popout", "#drawio-plc-connections-close" })
        {
            var edges = await popup.Locator(button).EvaluateAsync<double[]>(
                "b => { const r = b.getBoundingClientRect(); return [r.left, r.top, r.right, r.bottom, window.innerWidth, window.innerHeight]; }");
            Assert.True(edges[0] >= 0 && edges[1] >= 0 && edges[2] <= edges[4] + SamePixel && edges[3] <= edges[5] + SamePixel,
                FormattableString.Invariant($"{button} is at {edges[0]:0.#},{edges[1]:0.#}-{edges[2]:0.#},{edges[3]:0.#} in a {edges[4]:0}x{edges[5]:0} separate window."));
        }
        await popup.Locator("#drawio-plc-connections-close").ClickAsync();
        await editor.WaitForTheWindowShutAsync("drawio-plc-connections");
    }

    /// <summary>
    /// The "?" of a detached window opens its note inside the separate window - placed with that
    /// window's size, not the editor's - and a press elsewhere in the separate window shuts it. The
    /// press is one whose default is cancelled, so the focus does not move: only the press itself can
    /// shut the note (drawio-help.js).
    /// </summary>
    [Fact]
    public async Task TheQuestionMarkOfADetachedWindowPlacesItsNoteInsideItAndAPressElsewhereShutsIt()
    {
        await using var editor = await OpenAsync(window: (1400, 900));
        await editor.OpenWindowAsync("drawio-device-list");
        var popup = await editor.DetachWindowAsync("drawio-device-list");
        // Smaller than the note: placed by the editor's 1400x900 it would run past both edges.
        await popup.SetViewportSizeAsync(300, 140);

        await popup.Locator("#drawio-device-list-help").ClickAsync();
        var note = popup.Locator("#drawio-device-list-help-body");
        await note.WaitForAsync(new() { State = WaitForSelectorState.Visible });
        var edges = await note.EvaluateAsync<double[]>(
            "n => { const r = n.getBoundingClientRect(); return [r.left, r.top, r.right, r.bottom, window.innerWidth, window.innerHeight]; }");
        Assert.True(edges[0] >= 0 && edges[1] >= 0 && edges[2] <= edges[4] + SamePixel && edges[3] <= edges[5] + SamePixel,
            FormattableString.Invariant($"The note is at {edges[0]:0.#},{edges[1]:0.#}-{edges[2]:0.#},{edges[3]:0.#} in a {edges[4]:0}x{edges[5]:0} separate window."));

        await popup.Locator("#drawio-device-list .drawio-window-content").DispatchEventAsync("pointerdown");
        await note.WaitForAsync(new() { State = WaitForSelectorState.Hidden });
    }

    [Fact]
    public async Task ClosingTheSeparateBrowserWindowReturnsTheLiveWindow()
    {
        await using var editor = await OpenAsync(window: (1400, 900));
        await editor.OpenWindowAsync("drawio-device-list");
        var popup = await editor.DetachWindowAsync("drawio-device-list");

        await popup.CloseAsync();
        await editor.WaitForDetachedWindowBackAsync("drawio-device-list");

        Assert.Equal(1, await editor.ElementCountAsync("#drawio-device-list"));
        Assert.True(await editor.IsTheSectionOpenAsync("drawio-device-list"));
    }

    [Fact]
    public async Task ClosingFromInsideADetachedWindowClosesItAndReturnsTheHiddenDom()
    {
        await using var editor = await OpenAsync(window: (1400, 900));
        await editor.OpenWindowAsync("drawio-junction-list");
        var popup = await editor.DetachWindowAsync("drawio-junction-list");

        await popup.Locator("#drawio-junction-list-close").ClickAsync();
        await editor.WaitForDetachedWindowBackAsync("drawio-junction-list");
        // The live DOM comes back at once, in the click's own handler; the window is shut by the page
        // a round trip later (the X's click in the editor goes to Blazor over the circuit), and the
        // separate window's close reaches the test as an event of its own. Both are waited for.
        await WaitForCloseAsync(popup);
        await editor.WaitForTheWindowShutAsync("drawio-junction-list");

        Assert.True(popup.IsClosed);
        Assert.False(await editor.IsTheSectionOpenAsync("drawio-junction-list"));
    }

    [Fact]
    public async Task ABlockedSeparateWindowLeavesThePopupAndItsStateInTheEditor()
    {
        await using var editor = await OpenAsync(window: (1400, 900));
        await editor.OpenWindowAsync("drawio-operations");
        await editor.ClickAsync("#drawio-operations-tab-remove-all");
        await editor.ClickAsync("#drawio-remove-all-lu-button");
        await editor.WaitForShownAsync("#drawio-remove-all-lu-question", "the Remove all LU question");
        await editor.BlockSeparateWindowsAsync();

        await editor.ClickAsync("#drawio-operations-popout");

        Assert.Equal(1, await editor.ElementCountAsync("#drawio-operations"));
        Assert.True(await editor.IsTheSectionOpenAsync("drawio-operations"));
        Assert.Contains("Allow pop-ups", await editor.PopoutStatusAsync("drawio-operations"));
        Assert.Equal(1, await editor.ElementCountAsync("#drawio-remove-all-lu-question"));
    }

    [Fact]
    public async Task AFileChosenInTheDetachedPlantWindowReachesTheImportHandler()
    {
        await using var editor = await OpenAsync(window: (1400, 900));
        await editor.OpenWindowAsync("drawio-plant-file");
        var popup = await editor.DetachWindowAsync("drawio-plant-file");

        await popup.Locator("#plant-import-input").SetInputFilesAsync(new Microsoft.Playwright.FilePayload
        {
            Name = "not-a-plant.json",
            MimeType = "application/json",
            Buffer = "this is not JSON"u8.ToArray(),
        });

        await editor.WaitForDetachedWindowBackAsync("drawio-plant-file");
        await editor.WaitForVisibleAsync("#plant-io-error");

        Assert.True(popup.IsClosed);
        Assert.NotEmpty((await editor.ImportErrorAsync())!.Trim());
        Assert.Equal(1, await editor.ElementCountAsync("#drawio-plant-file"));
    }

    [Theory]
    [InlineData(1600, 1000, 2)]
    [InlineData(760, 600, 1)]
    public async Task PlantFileCardsKeepTheirControlsReachable(int width, int height, int columns)
    {
        await using var editor = await OpenAsync(window: (width, height));
        await editor.OpenWindowAsync("drawio-plant-file");
        var layout = await editor.OperationalWindowLayoutAsync("drawio-plant-file",
            cardsSelector: ".drawio-plant-file-modules");
        Assert.Equal(columns, layout.CardColumns);
        Assert.True(layout.ControlsFitHorizontally);
        Assert.Equal(0, await editor.ElementCountAsync("#drawio-plant-file > .drawio-window-grip"));
        var box = await editor.BoxOfAsync("#drawio-plant-file");
        Assert.InRange(box[2], Math.Min(680, width) - 1, Math.Min(680, width) + 1);
        if (height >= 800) Assert.True(box[3] < 460, $"Plant File kept {box[3]:0}px of height for content that needs less than its old 460px default.");
        await editor.LookAtItAsync($"plant-file-{width}x{height}");
    }

    [Fact]
    public async Task DecimalPropertiesTruncateAndTwoNativeIncrementsKeepKeyboardFocus()
    {
        await using var editor = await OpenAsync();
        await editor.SelectDevicesAsync(PlantSeed.DraggedName);
        await editor.WaitForPropertyAsync("Name", PlantSeed.DraggedName);
        await editor.TypePropertyAsync("LinkedCellId", "7.9");
        await editor.WaitForPropertyAsync("LinkedCellId", "7");
        await editor.ClickAsync("[data-property='LinkedCellId'] input");
        await editor.PressAsync("ArrowUp");
        await editor.WaitForPropertyAsync("LinkedCellId", "8");
        Assert.Equal("LinkedCellId", await editor.FocusedPropertyAsync());
        await editor.PressAsync("ArrowUp");
        await editor.WaitForPropertyAsync("LinkedCellId", "9");
        Assert.Equal("LinkedCellId", await editor.FocusedPropertyAsync());
        await editor.TypePropertyAsync("DeviceConnectionId", "-12.7");
        await editor.WaitForPropertyAsync("DeviceConnectionId", "-12");
        await editor.TypePropertyAsync("CellToDefragmentId", "3.99");
        await editor.WaitForPropertyAsync("CellToDefragmentId", "3");
        await editor.ConfirmPropertiesAsync();
        await editor.WaitForPropertiesWrittenAsync(PlantSeed.DraggedName);
        using var client = fixture.CreateClient();
        var saved = await client.GetItemConfigurationAsync(PlantSeed.DraggedId);
        Assert.Equal(9m, saved!.LinkedCellId);
        Assert.Equal(-12m, saved.DeviceConnectionId);
        Assert.Equal(3m, saved.CellToDefragmentId);
    }

    /// <summary>
    /// With nothing remembered, a window opens where the page's cascade puts it: Missions is the fifth
    /// window of the strip, DrawioWindowPlace.Cascade(4, 1020, 620) - 40 + 32 x 4 = 168 from the left,
    /// 140 + 32 x 4 = 268 from the top, 1020 x 620 - brought below the floor if the floor is lower than
    /// that, and no taller than the room under the floor. The numbers are the page's literal ones, the
    /// floor is measured; nothing here is the module's own arithmetic.
    /// </summary>
    [Fact]
    public async Task WithNothingRememberedTheWindowOpensWhereTheCascadePutsIt()
    {
        await using var editor = await OpenAsync();
        await editor.OpenWindowAsync("drawio-mission-list");
        var floor = (await editor.TheStripAndItsWindowsAsync()).Floor;
        var box = await editor.BoxOfAsync("#drawio-mission-list");

        double[] expected = [168, Math.Max(268, floor), 1020, Math.Min(620, 1000 - floor)];
        Assert.True(Enumerable.Range(0, 4).All(i => Math.Abs(box[i] - expected[i]) <= SamePixel),
            FormattableString.Invariant($"With the floor at {floor:0.##}, the Missions window was to open at {Describe(expected)}: it is at {Describe(box)}."));
    }

    [Fact]
    public async Task MissionsKeepTheirPagerAndToolsReachableAndExtendedScrollsInsideTheWindow()
    {
        await using var editor = await OpenAsync();
        await editor.OpenWindowAsync("drawio-mission-list");
        await editor.WaitForTextAsync("#drawio-mission-list-page-summary", "No missions");

        var compact = await editor.MissionLayoutAsync();
        Assert.Equal(10, compact.VisibleColumns);
        Assert.Equal("scroll", compact.GridOverflowY);
        Assert.Equal("sticky", compact.HeaderPosition);
        Assert.True(compact.PagerInside);
        Assert.True(compact.ToolsInside);

        await editor.ClickAsync("#drawio-mission-list-view-extended");
        await editor.WaitForAttributeAsync("#drawio-mission-list-view-extended", "aria-pressed", "true");
        var extended = await editor.MissionLayoutAsync();
        Assert.Equal(typeof(MissionDto).GetConstructors().Single().GetParameters().Length, extended.VisibleColumns);
        Assert.True(extended.HorizontalOverflow);
        Assert.True(extended.PagerInside);

        await editor.SetViewportAsync(760, 600);
        Assert.True((await editor.MissionLayoutAsync()).ToolsInside);
    }

    [Fact]
    public async Task DevicesAndJunctionsKeepTheirToolsPagerAndRowsInsideTheWindow()
    {
        // The seed's two devices and its one junction: every row of each list is shown.
        await using var editor = await OpenAsync(withAJunction: true);

        foreach (var (window, rows) in new[] { ("drawio-device-list", 2), ("drawio-junction-list", 1) })
        {
            await editor.OpenWindowAsync(window);
            await editor.WaitForDataGridAsync(window);

            var layout = await editor.DataGridLayoutAsync(window);
            Assert.Equal(rows, layout.VisibleRows);
            Assert.Equal("scroll", layout.GridOverflowY);
            Assert.Equal("sticky", layout.HeaderPosition);
            Assert.True(layout.PagerInside);
            Assert.True(layout.ToolsInside);
        }

        Assert.True((await editor.DataGridLayoutAsync("drawio-device-list")).HorizontalOverflow);
    }

    /// <summary>
    /// The owner's decision of 30/9: the plant checks' window opens at 480 x 220, in its place of the
    /// cascade - the seventh, 40 + 32 x 6 = 232 from the left and 140 + 32 x 6 = 332 from the top. At
    /// rest it holds the row of the two checks' buttons, and a few results under it; at 640 x 440, as it
    /// opened until then, it was mostly empty (and at 1366 x 768 it was pushed up to 328, 332 + 440
    /// being past the bottom). At both sizes of the spec the room under the floor is taller than 220,
    /// so the size is the page's literal one; the top is still brought below the floor if the floor is
    /// lower than 332. The numbers are written here, not read from the page's table of places.
    /// </summary>
    [Theory]
    [InlineData(1366, 768)]
    [InlineData(1920, 1080)]
    public async Task WithNothingRememberedThePlantChecksOpenAt480x220InTheirPlaceOfTheCascade(int width, int height)
    {
        await using var editor = await OpenAsync(window: (width, height));
        await editor.OpenWindowAsync("drawio-checks");
        var floor = (await editor.TheStripAndItsWindowsAsync()).Floor;
        var box = await editor.BoxOfAsync("#drawio-checks");

        double[] expected = [232, Math.Max(332, floor), 480, 220];
        Assert.True(Enumerable.Range(0, 4).All(i => Math.Abs(box[i] - expected[i]) <= SamePixel),
            FormattableString.Invariant($"At {width}x{height}, with the floor at {floor:0.##}, the plant checks' window was to open at {Describe(expected)}: it is at {Describe(box)}."));
    }

    [Fact]
    public async Task TheTitleBarMovesTheWindowAcrossTheCanvas()
    {
        await using var editor = await OpenAsync();
        await editor.OpenWindowAsync("drawio-device-list");
        var frame = await editor.BoxOfAsync("#drawio-frame");
        var before = await editor.BoxOfAsync("#drawio-device-list");
        var title = await editor.BoxOfAsync("#drawio-device-list .drawio-window-title");
        var dx = frame[0] + frame[2] / 2 - before[0];
        const double dy = 120;

        // ONE move, from the middle of the title bar to a point over draw.io's iframe and out of the
        // window: the only move event lands where the page's own elements are not. With the pointer
        // captured it still goes to the title bar; without, the page never sees it (a gesture made
        // in small steps would not say so - the window follows the pointer and the pointer never
        // leaves the title bar). The point is checked first, so that the test cannot go blind.
        var endX = title[0] + title[2] / 2 + dx;
        var endY = title[1] + title[3] / 2 + dy;
        Assert.True(Inside(frame, endX, endY) && !Inside(before, endX, endY),
            FormattableString.Invariant($"The gesture would end at {endX:0},{endY:0}, ") +
            $"which is not over draw.io's frame ({Describe(frame)}) out of the window ({Describe(before)}): it would not cross the iframe.");
        await editor.DragAsync("#drawio-device-list .drawio-window-title", dx, dy, steps: 1);

        var after = await editor.BoxOfAsync("#drawio-device-list");
        Assert.InRange(after[0] - before[0], dx - 2, dx + 2);
        Assert.InRange(after[1] - before[1], dy - 2, dy + 2);
    }

    [Fact]
    public async Task TheGripResizesDownToTheSmallestUsableSizeAndNoFurther()
    {
        await using var editor = await OpenAsync();
        await editor.OpenWindowAsync("drawio-junction-list");
        await editor.DragAsync("#drawio-junction-list .drawio-window-grip", -2000, -2000);
        var box = await editor.BoxOfAsync("#drawio-junction-list");
        Assert.Equal(280, box[2], 0);
        Assert.Equal(160, box[3], 0);
        // And what the module itself wrote: the stylesheet's min-width and min-height alone would give
        // the box above even if the module let the grip go smaller.
        Assert.Equal(["280px", "160px"], await editor.InlineSizeOfAsync("drawio-junction-list"));
    }

    [Fact]
    public async Task AResizeNeverMovesTheWindowAndStopsAtTheEdgesOfTheBrowser()
    {
        await using var editor = await OpenAsync();
        await editor.SetViewportAsync(1600, 1000);
        await editor.OpenWindowAsync("drawio-junction-list");
        await editor.DragAsync("#drawio-junction-list .drawio-window-title", 400, 100);
        var placed = await editor.BoxOfAsync("#drawio-junction-list");
        Assert.True(placed[0] > 400 && placed[0] + placed[2] < 1600 && placed[1] + placed[3] < 1000,
            $"The window is at {Describe(placed)}: its left edge is not well inside the browser, or it already touches an edge.");

        // Far past the right and bottom edges of the browser.
        await editor.DragAsync("#drawio-junction-list .drawio-window-grip", 3000, 3000);
        var resized = await editor.BoxOfAsync("#drawio-junction-list");
        var what = $"The window was at {Describe(placed)} and is at {Describe(resized)} after the grip went past the edges of a 1600x1000 browser.";
        Assert.True(Math.Abs(resized[0] - placed[0]) <= SamePixel && Math.Abs(resized[1] - placed[1]) <= SamePixel,
            $"{what} Its left and top edges moved.");
        Assert.True(Math.Abs(resized[0] + resized[2] - 1600) <= SamePixel && Math.Abs(resized[1] + resized[3] - 1000) <= SamePixel,
            $"{what} Its right and bottom edges did not stop at the browser's.");
    }

    [Fact]
    public async Task PlaceAndSizeSurviveAReload()
    {
        await using var editor = await OpenAsync();
        await editor.OpenWindowAsync("drawio-mission-list");
        var cascade = await editor.BoxOfAsync("#drawio-mission-list");
        // Moved and made smaller: each gesture writes the place at its end, the second one over the first.
        await editor.DragAsync("#drawio-mission-list .drawio-window-title", 150, 60);
        await editor.DragAsync("#drawio-mission-list .drawio-window-grip", -100, -80);
        var moved = await editor.BoxOfAsync("#drawio-mission-list");
        var expected = new[] { cascade[0] + 150, cascade[1] + 60, cascade[2] - 100, cascade[3] - 80 };
        Assert.True(Enumerable.Range(0, 4).All(i => Math.Abs(moved[i] - expected[i]) <= SamePixel),
            $"The window at {Describe(cascade)} was to be moved by 150,60 and made 100x80 smaller: it is at {Describe(moved)}.");

        await editor.ReloadAndPassTheKeyGateAsync(fixture.Host.ApiKey);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        // Opened again by the memory of the open windows, not by a click.
        await editor.WaitForTheWindowShownAsync("drawio-mission-list");
        var reloaded = await editor.BoxOfAsync("#drawio-mission-list");
        Assert.True(Enumerable.Range(0, 4).All(i => Math.Abs(reloaded[i] - moved[i]) <= SamePixel),
            $"The window was at {Describe(moved)} before the reload and is at {Describe(reloaded)} after it.");
    }

    [Fact]
    public async Task AWindowLeftOutsideASmallerBrowserComesBackInside()
    {
        await using var editor = await OpenAsync();
        await editor.OpenWindowAsync("drawio-plc-connections");
        await editor.DragAsync("#drawio-plc-connections .drawio-window-title", 900, 500);
        var before = await editor.BoxOfAsync("#drawio-plc-connections");
        Assert.True(before[0] + before[2] > 900 && before[1] + before[3] > 600,
            $"The window is at {Describe(before)}: it would already be inside a 900x600 browser.");

        await editor.SetViewportAsync(900, 600);
        var box = await editor.BoxOfAsync("#drawio-plc-connections");
        Assert.True(box[0] >= 0 && box[1] >= 0 && box[0] + box[2] <= 900 && box[1] + box[3] <= 600,
            $"The window is at {Describe(box)}, outside a 900x600 viewport.");
    }

    [Fact]
    public async Task TheWindowTouchedLastIsInFront()
    {
        await using var editor = await OpenAsync();
        await editor.OpenWindowAsync("drawio-device-list");
        await editor.OpenWindowAsync("drawio-mission-list");
        var devicesBefore = await editor.WindowStackingAsync("drawio-device-list");
        var missionsBefore = await editor.WindowStackingAsync("drawio-mission-list");
        Assert.True(int.Parse(missionsBefore.Computed) > int.Parse(devicesBefore.Computed),
            $"Missions, opened last, is at {missionsBefore} and Devices at {devicesBefore}: the click below would prove nothing.");

        await editor.ClickAsync("#drawio-device-list .drawio-window-title");
        var devices = await editor.WindowStackingAsync("drawio-device-list");
        var missions = await editor.WindowStackingAsync("drawio-mission-list");
        Assert.True(int.Parse(devices.Computed) > int.Parse(missions.Computed),
            $"Devices, touched last, is at {devices} and Missions at {missions}.");
    }

    [Fact]
    public async Task ARowOfTheDeviceWindowSelectsTheDeviceOnTheDrawing()
    {
        await using var editor = await OpenAsync();
        await editor.OpenWindowAsync("drawio-device-list");
        await editor.ChooseDeviceListRowAsync(PlantSeed.DraggedName);
        Assert.Equal([PlantSeed.DraggedId.ToString()], await editor.SelectedCellIdsAsync());
    }

    /// <summary>
    /// The owner's decision D2 (29/9): the device a row brings into view is centred in the part of the
    /// canvas the windows leave free, not under the window the row is in. Devices opens where the
    /// cascade puts it, over the middle of the canvas - where the drawing used to centre it.
    /// </summary>
    [Fact]
    public async Task ARowOfTheDeviceWindowBringsTheDeviceWhereNoWindowCoversIt()
    {
        await using var editor = await OpenAsync();
        await editor.OpenWindowAsync("drawio-device-list");
        var window = await editor.BoxOfAsync("#drawio-device-list");
        var frame = await editor.BoxOfAsync("#drawio-frame");
        // Not blind: centred in the whole canvas, the device would be under the window.
        Assert.True(Inside(window, frame[0] + frame[2] / 2, frame[1] + frame[3] / 2),
            $"The Devices window ({Describe(window)}) does not cover the middle of draw.io's frame ({Describe(frame)}): this test would not tell the free part of the canvas from the whole.");

        await editor.ChooseDeviceListRowAsync(PlantSeed.DraggedName);

        await AssertTheCellIsWhereNoWindowCoversItAsync(editor, PlantSeed.DraggedId.ToString(),
            $"The device, with the Devices window at {Describe(window)}");
    }

    /// <summary>
    /// The cell is where the operator sees it: inside draw.io's frame, and at its centre the browser
    /// hits the frame - not a window of the page - and, inside the frame, the drawing's area - not
    /// draw.io's toolbar or one of its panels, which are in the frame too (final review of rows 22-29:
    /// the frame's box alone took them for the drawing). One check for "go to" (D2) and for Duplicate
    /// (queue item 24), which bring a cell into view by the same function. A cell turned a quarter is
    /// painted standing - its unturned box's width runs down - and that is the box held to the frame.
    /// Returns the cell's unturned box.
    /// </summary>
    private async Task<double[]> AssertTheCellIsWhereNoWindowCoversItAsync(DrawioEditorPage editor, string cellId, string what, bool turnedAQuarter = false)
    {
        var frame = await editor.BoxOfAsync("#drawio-frame");
        var cell = await editor.CellBoxOnScreenAsync(cellId);
        var x = cell[0] + cell[2] / 2;
        var y = cell[1] + cell[3] / 2;
        var painted = turnedAQuarter ? new[] { x - cell[3] / 2, y - cell[2] / 2, cell[3], cell[2] } : cell;
        var hit = await editor.WhatIsHitAtAsync(x, y);
        var said = FormattableString.Invariant(
            $"{what}: its cell is painted at {Describe(painted)}, draw.io's frame at {Describe(frame)}, and at the cell's centre ({x:0},{y:0}) the browser hits {hit}");
        output.WriteLine(said);
        Assert.True(painted[0] >= frame[0] && painted[1] >= frame[1] && painted[0] + painted[2] <= frame[0] + frame[2] && painted[1] + painted[3] <= frame[1] + frame[3],
            $"{said}: the cell is not inside the frame.");
        Assert.True(hit.TheFrame && !hit.InAWindow && hit.OnTheCanvas, $"{said}: not the drawing.");
        return cell;
    }

    /// <summary>
    /// Queue item 24: the copy "Duplicate Device" draws is brought into view as a row's device is (D2,
    /// above) - centred in the part of the canvas the windows leave free. Merely scrolled into view,
    /// it stayed where it was born whenever that was inside the canvas: under the window, if one was
    /// there. The question is asked from the right-click menu on the device where the drawing opens,
    /// and the Devices window opened at its default place before the answer: opened first, it would
    /// cover the device the right-click has to reach.
    /// </summary>
    [Fact]
    public async Task ADuplicatedDeviceIsBroughtWhereNoWindowCoversIt()
    {
        await using var editor = await OpenAsync();
        using var client = fixture.CreateClient();
        try
        {
            await editor.WaitForTheDeviceMenuAsync(PlantSeed.DraggedId.ToString(), isDevice: true, running: false);
            await editor.RightClickOnDeviceAsync(PlantSeed.DraggedName);
            await editor.ClickTheMenuEntryAsync("Duplicate Device");
            await editor.WaitForTheFocusOnAsync("drawio-duplicate-confirmation");

            await editor.OpenWindowAsync("drawio-device-list");
            var window = await editor.BoxOfAsync("#drawio-device-list");
            var frame = await editor.BoxOfAsync("#drawio-frame");
            Assert.True(Inside(window, frame[0] + frame[2] / 2, frame[1] + frame[3] / 2),
                $"The Devices window ({Describe(window)}) does not cover the middle of draw.io's frame ({Describe(frame)}): it is not at its default place.");
            // Not blind: the copy is born 100 to the right of the source and 100 below, as the
            // question says; left there, with the drawing as it is now, it would be under the window.
            var source = await editor.CellBoxOnScreenAsync(PlantSeed.DraggedId.ToString());
            var offset = 100 * await editor.ViewScaleAsync();
            var bornX = source[0] + source[2] / 2 + offset;
            var bornY = source[1] + source[3] / 2 + offset;
            var bornHit = await editor.WhatIsHitAtAsync(bornX, bornY);
            // Under the Devices window itself, the one still open when the copy is looked for at the
            // end - not under any window that might have been open while the question was asked.
            Assert.True(bornHit.Window == "drawio-device-list",
                FormattableString.Invariant($"The source is at {Describe(source)} and the Devices window at {Describe(window)}: where the copy is born ({bornX:0},{bornY:0}) the browser hits {bornHit}, not the Devices window - this test would not tell a copy brought into the free part of the canvas from one left where it was born."));

            await editor.ClickAsync("#drawio-duplicate-confirm");
            await editor.WaitForTextAsync("#drawio-duplicate-report", "is drawn on the canvas and selected");

            var created = Assert.Single(await client.GetItemsAsync(),
                item => item.ItemId != PlantSeed.DraggedId && item.ItemId != PlantSeed.UntouchedId);
            await AssertTheCellIsWhereNoWindowCoversItAsync(editor, created.ItemId.ToString(),
                $"The copy, with the Devices window at {Describe(window)}");
        }
        finally
        {
            await PlantSeed.ResetAsync(client, SeedHolder);
        }
    }

    /// <summary>
    /// Queue item 33, owner's decision 1/10: a copy born where the operator already sees it whole -
    /// inside the canvas, under no window of the page and no panel of draw.io - stays where it is born,
    /// 100 to the right of the source and 100 below, and the view does not move. Since queue item 24
    /// the view moved at every Duplicate, centring the copy on the canvas.
    /// </summary>
    [Fact]
    public async Task ADuplicatedDeviceBornInSightLeavesTheViewWhereItIs()
    {
        await using var editor = await OpenAsync();
        using var client = fixture.CreateClient();
        try
        {
            await editor.WaitForTheDeviceMenuAsync(PlantSeed.DraggedId.ToString(), isDevice: true, running: false);
            await editor.RightClickOnDeviceAsync(PlantSeed.DraggedName);
            await editor.ClickTheMenuEntryAsync("Duplicate Device");
            await editor.WaitForTheFocusOnAsync("drawio-duplicate-confirmation");

            var view = await editor.ViewPositionAsync();
            var canvas = await editor.CanvasBoxOnScreenAsync();
            var source = await editor.CellBoxOnScreenAsync(PlantSeed.DraggedId.ToString());
            var offset = 100 * await editor.ViewScaleAsync();
            double[] born = [source[0] + offset, source[1] + offset, source[2], source[3]];
            var staged = $"The canvas is at {Describe(canvas)}, the source at {Describe(source)}, the copy is born at {Describe(born)}";
            output.WriteLine(staged);
            // Not blind: born whole inside the canvas, nothing over it - no panel of draw.io, and the
            // browser hits the drawing at its centre and at its corners - and far from where centring
            // would take it: since queue item 36 the middle of the largest part of the canvas draw.io's
            // panels leave free, which is not the middle of the canvas (final review, M1).
            Assert.True(born[0] >= canvas[0] && born[1] >= canvas[1] && born[0] + born[2] <= canvas[0] + canvas[2] && born[1] + born[3] <= canvas[1] + canvas[3],
                $"{staged}: the copy is not born inside the canvas.");
            foreach (var panel in await editor.DrawioPanelBoxesOnScreenAsync())
                Assert.False(Overlap(panel, born), $"{staged}: draw.io's panel at {Describe(panel)} covers part of it.");
            foreach (var (x, y) in new[] { (born[0] + born[2] / 2, born[1] + born[3] / 2), (born[0] + 1, born[1] + 1), (born[0] + born[2] - 1, born[1] + 1), (born[0] + 1, born[1] + born[3] - 1), (born[0] + born[2] - 1, born[1] + born[3] - 1) })
            {
                var hit = await editor.WhatIsHitAtAsync(x, y);
                Assert.True(hit.TheFrame && !hit.InAWindow && hit.OnTheCanvas,
                    FormattableString.Invariant($"{staged}: at ({x:0},{y:0}) the browser hits {hit}, not the drawing."));
            }
            var panels = await editor.DrawioPanelBoxesOnScreenAsync();
            var free = await editor.LargestFreeRectangleAsync(EdgesOf(canvas), [.. panels.Select(EdgesOf)], born[2], born[3]);
            Assert.True(free is not null, $"{staged}: draw.io's panels leave no part of the canvas that holds the copy.");
            var fromTheMiddle = Math.Max(Math.Abs(born[0] + born[2] / 2 - (free![0] + free[2]) / 2), Math.Abs(born[1] + born[3] / 2 - (free[1] + free[3]) / 2));
            Assert.True(fromTheMiddle > 50,
                FormattableString.Invariant($"{staged}: it is born {fromTheMiddle:0.##} px from the middle of the part of the canvas draw.io's panels leave free (left {free[0]:0}, top {free[1]:0}, right {free[2]:0}, bottom {free[3]:0}) - this test would not tell a copy left where it is born from one centred."));

            await editor.ClickAsync("#drawio-duplicate-confirm");
            await editor.WaitForTextAsync("#drawio-duplicate-report", "is drawn on the canvas and selected");

            var created = Assert.Single(await client.GetItemsAsync(),
                item => item.ItemId != PlantSeed.DraggedId && item.ItemId != PlantSeed.UntouchedId);
            var after = await editor.ViewPositionAsync();
            var copy = await editor.CellBoxOnScreenAsync(created.ItemId.ToString());
            Assert.True(view.SequenceEqual(after),
                $"{staged}: the view moved from {string.Join(", ", view)} to {string.Join(", ", after)} (scroll left and top, translate x and y), and the copy is at {Describe(copy)}.");
            Assert.True(Math.Abs(copy[0] - born[0]) <= SamePixel && Math.Abs(copy[1] - born[1]) <= SamePixel,
                $"{staged}: the copy is at {Describe(copy)}.");
        }
        finally
        {
            await PlantSeed.ResetAsync(client, SeedHolder);
        }
    }

    /// <summary>
    /// Queue items 33 and 36: a copy born under one of draw.io's panels - here its format panel, with
    /// no window of the page open - cannot be seen where it is, so the view moves and the copy is
    /// centred where nothing covers it, draw.io's panels included. The canvas is scrolled after the
    /// question and before the answer, so that the copy is born under the middle of the format panel:
    /// scrolled first, the source would be under the panel too, out of the right-click's reach.
    /// </summary>
    [Fact]
    public async Task ADuplicatedDeviceBornUnderDrawiosFormatPanelIsBroughtWhereNothingCoversIt()
    {
        await using var editor = await OpenAsync();
        using var client = fixture.CreateClient();
        try
        {
            await editor.WaitForTheDeviceMenuAsync(PlantSeed.DraggedId.ToString(), isDevice: true, running: false);
            await editor.RightClickOnDeviceAsync(PlantSeed.DraggedName);
            await editor.ClickTheMenuEntryAsync("Duplicate Device");
            await editor.WaitForTheFocusOnAsync("drawio-duplicate-confirmation");

            var format = await TheFormatPanelAsync(editor);
            var source = await editor.CellBoxOnScreenAsync(PlantSeed.DraggedId.ToString());
            var offset = 100 * await editor.ViewScaleAsync();
            var bornX = source[0] + source[2] / 2 + offset;
            var bornY = source[1] + source[3] / 2 + offset;
            var scrolled = await editor.ScrollTheCanvasByAsync(bornX - (format[0] + format[2] / 2), bornY - (format[1] + format[3] / 2));
            source = await editor.CellBoxOnScreenAsync(PlantSeed.DraggedId.ToString());
            bornX = source[0] + source[2] / 2 + offset;
            bornY = source[1] + source[3] / 2 + offset;
            var staged = FormattableString.Invariant(
                $"draw.io's format panel is at {Describe(format)}, the canvas scrolled by {scrolled[0]:0.##},{scrolled[1]:0.##}, the source at {Describe(source)}, the copy born with its centre at ({bornX:0},{bornY:0})");
            output.WriteLine(staged);
            // Not blind: where the copy is born the browser hits draw.io's frame, in no window of the
            // page, and inside it the format panel - not the drawing.
            var bornHit = await editor.WhatIsHitAtAsync(bornX, bornY);
            Assert.True(Inside(format, bornX, bornY) && bornHit.TheFrame && !bornHit.InAWindow && !bornHit.OnTheCanvas,
                $"{staged}: there the browser hits {bornHit} - this test would not tell a copy brought out from under the panel from one left where it was born.");

            await editor.ClickAsync("#drawio-duplicate-confirm");
            await editor.WaitForTextAsync("#drawio-duplicate-report", "is drawn on the canvas and selected");

            var created = Assert.Single(await client.GetItemsAsync(),
                item => item.ItemId != PlantSeed.DraggedId && item.ItemId != PlantSeed.UntouchedId);
            var copy = await AssertTheCellIsWhereNoWindowCoversItAsync(editor, created.ItemId.ToString(), $"The copy, {staged}");
            foreach (var panel in await editor.DrawioPanelBoxesOnScreenAsync())
                Assert.False(Overlap(panel, copy), $"{staged}: the copy is at {Describe(copy)}, under draw.io's panel at {Describe(panel)}.");
        }
        finally
        {
            await PlantSeed.ResetAsync(client, SeedHolder);
        }
    }

    /// <summary>
    /// Queue item 33, owner's decision 1/10: a copy born off the screen - here below the canvas's foot,
    /// under nothing - cannot be seen either, so the view moves and the copy is brought whole inside
    /// the canvas, where nothing covers it. Staged as the test above: the canvas is scrolled after the
    /// question, so that the copy's centre is born 60 px below the canvas's foot and 640 from the
    /// page's left edge, clear of draw.io's panels.
    /// </summary>
    [Fact]
    public async Task ADuplicatedDeviceBornOffTheScreenIsBroughtIntoTheCanvas()
    {
        const double bornAtX = 640, belowTheFoot = 60;
        await using var editor = await OpenAsync();
        using var client = fixture.CreateClient();
        try
        {
            await editor.WaitForTheDeviceMenuAsync(PlantSeed.DraggedId.ToString(), isDevice: true, running: false);
            await editor.RightClickOnDeviceAsync(PlantSeed.DraggedName);
            await editor.ClickTheMenuEntryAsync("Duplicate Device");
            await editor.WaitForTheFocusOnAsync("drawio-duplicate-confirmation");

            var canvas = await editor.CanvasBoxOnScreenAsync();
            var source = await editor.CellBoxOnScreenAsync(PlantSeed.DraggedId.ToString());
            var offset = 100 * await editor.ViewScaleAsync();
            var scrolled = await editor.ScrollTheCanvasByAsync(
                source[0] + source[2] / 2 + offset - bornAtX, source[1] + source[3] / 2 + offset - (canvas[1] + canvas[3] + belowTheFoot));
            source = await editor.CellBoxOnScreenAsync(PlantSeed.DraggedId.ToString());
            double[] born = [source[0] + offset, source[1] + offset, source[2], source[3]];
            var staged = FormattableString.Invariant(
                $"The canvas is at {Describe(canvas)}, scrolled by {scrolled[0]:0.##},{scrolled[1]:0.##}, the source at {Describe(source)}, the copy born at {Describe(born)}");
            output.WriteLine(staged);
            // Not blind: born wholly below the canvas's foot, and right of draw.io's shapes panel and
            // left of its format panel - off the screen and under nothing.
            Assert.True(born[1] >= canvas[1] + canvas[3],
                $"{staged}: the copy is not born below the canvas's foot.");
            foreach (var panel in await editor.DrawioPanelBoxesOnScreenAsync())
                Assert.False(born[0] < panel[0] + panel[2] && born[0] + born[2] > panel[0],
                    $"{staged}: draw.io's panel at {Describe(panel)} is above where the copy is born - this test would not tell off the screen from under a panel.");

            await editor.ClickAsync("#drawio-duplicate-confirm");
            await editor.WaitForTextAsync("#drawio-duplicate-report", "is drawn on the canvas and selected");

            var created = Assert.Single(await client.GetItemsAsync(),
                item => item.ItemId != PlantSeed.DraggedId && item.ItemId != PlantSeed.UntouchedId);
            var copy = await AssertTheCellIsWhereNoWindowCoversItAsync(editor, created.ItemId.ToString(), $"The copy, {staged}");
            Assert.True(copy[0] >= canvas[0] && copy[1] >= canvas[1] && copy[0] + copy[2] <= canvas[0] + canvas[2] && copy[1] + copy[3] <= canvas[1] + canvas[3],
                $"{staged}: the copy is at {Describe(copy)}, not whole inside the canvas.");
        }
        finally
        {
            await PlantSeed.ResetAsync(client, SeedHolder);
        }
    }

    /// <summary>
    /// Queue item 36, owner's decision 1/10: "go to" centres the device in the part of the canvas that
    /// the page's windows AND draw.io's panels leave free. The Devices window is put over the canvas
    /// from its left edge to x 900, top to bottom: of what it leaves, the part right of it is mostly
    /// under draw.io's format panel. Centred in the canvas less the Devices window alone, as before,
    /// the device landed under the format panel.
    /// </summary>
    [Fact]
    public async Task ARowOfTheDeviceWindowBringsTheDeviceOutFromUnderDrawiosFormatPanel()
    {
        const double windowRight = 900, browserBottom = 1000;
        await using var editor = await OpenAsync();
        var frame = await editor.BoxOfAsync("#drawio-frame");
        await editor.OpenWindowAsync("drawio-device-list");
        var window = await editor.PlaceWindowAsync("drawio-device-list", 0, frame[1], windowRight, browserBottom - frame[1]);
        var canvas = await editor.CanvasBoxOnScreenAsync();
        var format = await TheFormatPanelAsync(editor);
        var staged = $"The canvas is at {Describe(canvas)}, the Devices window at {Describe(window)}, draw.io's format panel at {Describe(format)}";
        output.WriteLine(staged);
        // Not blind: the Devices window covers the canvas from its left edge, top to bottom, and the
        // middle of what it leaves - where "go to" centred the device before - is under the format
        // panel.
        Assert.True(window[0] <= Math.Max(canvas[0], 0) + SamePixel && window[1] <= canvas[1] && window[1] + window[3] >= canvas[1] + canvas[3],
            $"{staged}: the Devices window does not cover the canvas from its left edge, top to bottom.");
        var oldX = (window[0] + window[2] + canvas[0] + canvas[2]) / 2;
        var oldY = canvas[1] + canvas[3] / 2;
        var oldHit = await editor.WhatIsHitAtAsync(oldX, oldY);
        Assert.True(Inside(format, oldX, oldY) && !oldHit.OnTheCanvas,
            FormattableString.Invariant($"{staged}: at the middle of the part the Devices window leaves ({oldX:0},{oldY:0}) the browser hits {oldHit} - this test would not tell the format panel taken out of the free part from left in it."));

        await editor.ChooseDeviceListRowAsync(PlantSeed.DraggedName);

        var cell = await AssertTheCellIsWhereNoWindowCoversItAsync(editor, PlantSeed.DraggedId.ToString(), staged);
        foreach (var panel in await editor.DrawioPanelBoxesOnScreenAsync())
            Assert.False(Overlap(panel, cell), $"{staged}: the device is at {Describe(cell)}, under draw.io's panel at {Describe(panel)}.");
    }

    /// <summary>
    /// draw.io's format panel on the screen: of its panels (<see cref="DrawioEditorPage.DrawioPanelBoxesOnScreenAsync"/>),
    /// the one at the right edge of draw.io's frame - measured at 1038,101 242x602 in the wide window.
    /// </summary>
    private static async Task<double[]> TheFormatPanelAsync(DrawioEditorPage editor)
    {
        var frame = await editor.BoxOfAsync("#drawio-frame");
        var panels = await editor.DrawioPanelBoxesOnScreenAsync();
        return Assert.Single(panels, p => Math.Abs(p[0] + p[2] - (frame[0] + frame[2])) <= 2);
    }

    private static bool Overlap(double[] a, double[] b) =>
        a[0] < b[0] + b[2] && a[0] + a[2] > b[0] && a[1] < b[1] + b[3] && a[1] + a[3] > b[1];

    /// <summary>A box as <c>[x, y, width, height]</c> written as its edges, <c>[left, top, right, bottom]</c>, the form <see cref="DrawioEditorPage.LargestFreeRectangleAsync"/> takes.</summary>
    private static double[] EdgesOf(double[] box) => [box[0], box[1], box[0] + box[2], box[1] + box[3]];

    /// <summary>
    /// Queue item 25: the free part of the canvas a cell is centred in is the largest free rectangle
    /// AMONG THOSE THAT HOLD THE CELL - not the largest of all, which may be a band too thin for it
    /// while a smaller corner would hold it. The bridge's choice is a pure function of boxes, so it is
    /// asked here on boxes written by hand, one case per point: a canvas 1000 x 800, the windows over
    /// it, a cell 120 x 60, and the rectangle expected, read off the drawing of each case rather than
    /// worked out by the bridge's arithmetic. The browser is only where the module runs.
    /// </summary>
    [Fact]
    public async Task TheFreePartChosenIsTheLargestThatHoldsTheCell()
    {
        double[] canvas = [0, 0, 1000, 800];
        (string Case, double[][] Covers, double Width, double Height, double[]? Expected)[] cases =
        [
            ("the largest free area is a band 40 high across the top, a 200 x 150 corner holds the cell",
                [[0, 40, 1000, 650], [200, 650, 1000, 800]], 120, 60, [0, 650, 200, 800]),
            ("the largest free area is a column 50 wide down the left, a 200 x 150 corner holds the cell",
                [[50, 0, 1000, 650], [200, 650, 1000, 800]], 120, 60, [0, 650, 200, 800]),
            ("a band 40 high, and two corners that hold the cell: 150 x 150 on the left, 220 x 150 on the right",
                [[0, 40, 1000, 650], [150, 650, 780, 800]], 120, 60, [780, 650, 1000, 800]),
            ("as the first, with a small window at the top whose edges at 50 and 60 cross the corner's rows: the corner is still taken whole",
                [[0, 40, 1000, 650], [200, 650, 1000, 800], [50, 0, 60, 20]], 120, 60, [0, 650, 200, 800]),
            ("a corner exactly as big as the cell holds it",
                [[0, 40, 1000, 740], [120, 740, 1000, 800]], 120, 60, [0, 740, 120, 800]),
            ("two halves hold the cell, 1000 x 250 above and 1000 x 350 below: the larger, as before",
                [[0, 250, 1000, 450]], 120, 60, [0, 450, 1000, 800]),
            ("only a band 40 high is free: nothing holds the cell",
                [[0, 40, 1000, 800]], 120, 60, null),
        ];

        await using var editor = await OpenAsync();
        var wrong = new List<string>();
        foreach (var (name, covers, width, height, expected) in cases)
        {
            var chosen = await editor.LargestFreeRectangleAsync(canvas, covers, width, height);
            var same = expected is null ? chosen is null : chosen is not null && expected.SequenceEqual(chosen);
            if (!same) wrong.Add($"{name}: expected {Edges(expected)}, the bridge chose {Edges(chosen)}");
        }
        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));

        static string Edges(double[]? r) => r is null ? "none" : FormattableString.Invariant($"left {r[0]}, top {r[1]}, right {r[2]}, bottom {r[3]}");
    }

    /// <summary>
    /// Queue item 25, on the page: two windows staged so that the largest part of the canvas they leave
    /// free is a band across the top, thinner than the device, and the only part that holds the device
    /// is a smaller corner at the bottom left. "Go to" from the device list centres the device in the
    /// corner. Before, it took the band, found it too thin and centred the device in the whole canvas:
    /// under the Devices window. The windows are put in place with the mouse, and every fact the test
    /// rests on is measured before the row is chosen.
    /// </summary>
    [Fact]
    public async Task ARowOfTheDeviceWindowBringsTheDeviceIntoASmallerFreeAreaThatHoldsItWhenTheLargestIsTooThin()
    {
        // The band 40 px high, the corner 240 x 150, both windows reaching 1300 - past the canvas's
        // right edge in the wide browser of OpenAsync (1600 x 1000), the lower one down to its bottom.
        const double band = 40, cornerWidth = 240, cornerHeight = 150, windowsRight = 1300, browserBottom = 1000;
        await using var editor = await OpenAsync();
        var canvas = await editor.CanvasBoxOnScreenAsync();
        var canvasRight = canvas[0] + canvas[2];
        var canvasBottom = canvas[1] + canvas[3];
        var before = await editor.CellBoxOnScreenAsync(PlantSeed.DraggedId.ToString());

        await editor.OpenWindowAsync("drawio-mission-list");
        var lowerLeft = canvas[0] + cornerWidth;
        var lowerTop = canvasBottom - cornerHeight;
        var lower = await editor.PlaceWindowAsync("drawio-mission-list", lowerLeft, lowerTop, windowsRight - lowerLeft, browserBottom - lowerTop);
        await editor.OpenWindowAsync("drawio-device-list");
        var upperTop = canvas[1] + band;
        var upper = await editor.PlaceWindowAsync("drawio-device-list", 0, upperTop, windowsRight, lowerTop - upperTop);

        // Not blind. The windows cover the canvas but for a band above the Devices window (the whole
        // canvas across), a corner left of the Missions window and under the Devices one, and whatever
        // of the canvas lies left of the browser's edge, where no window can go.
        var staged = $"The canvas is at {Describe(canvas)}, the device at {Describe(before)}, the Devices window at {Describe(upper)}, the Missions window at {Describe(lower)}";
        Assert.True(upper[0] <= Math.Max(canvas[0], 0) + SamePixel && upper[0] + upper[2] >= canvasRight
                && lower[0] + lower[2] >= canvasRight && lower[1] + lower[3] >= canvasBottom
                && lower[1] <= upper[1] + upper[3] + SamePixel && upper[1] > canvas[1],
            $"{staged}: the two windows do not cover the canvas as staged.");
        // The canvas sticks out of draw.io's frame on the left (measured: 10 px), where nothing can be
        // seen: the part of the corner that can be seen starts at the frame's edge.
        var frame = await editor.BoxOfAsync("#drawio-frame");
        Assert.True(canvas[0] < frame[0] - 5 && frame[0] >= 0,
            $"{staged}, draw.io's frame at {Describe(frame)}: the canvas does not stick out of the frame on the left, and this test would not tell the corner's part that can be seen from the whole.");
        // The band is thinner than the device's shorter side, so it holds the device at no rotation;
        // the corner's part that can be seen is wider and taller than the device's diagonal, so it holds
        // it at any (the seed's device is turned 30 degrees).
        var bandHeight = upper[1] - canvas[1];
        var corner = new[] { lower[0] - frame[0], canvasBottom - (upper[1] + upper[3]) };
        var diagonal = Math.Sqrt(before[2] * before[2] + before[3] * before[3]);
        Assert.True(bandHeight < before[3] && corner[0] >= diagonal && corner[1] >= diagonal && frame[2] * bandHeight > corner[0] * corner[1],
            FormattableString.Invariant($"{staged}: the band above the Devices window ({frame[2]:0.##} x {bandHeight:0.##}) is not both larger than the corner and thinner than the device, or the corner ({corner[0]:0.##} x {corner[1]:0.##}) does not hold the device's diagonal ({diagonal:0.##})."));
        var middle = await editor.WhatIsHitAtAsync(canvas[0] + canvas[2] / 2, canvas[1] + canvas[3] / 2);
        Assert.True(middle.InAWindow,
            $"{staged}: at the middle of the canvas the browser hits {middle}, not a window - this test would not tell the corner from the plain centre.");
        // And the device is under a window where it is, once the windows are in place: a "go to" that
        // did nothing must not pass because of where the seed happens to put the device.
        var where = await editor.WhatIsHitAtAsync(before[0] + before[2] / 2, before[1] + before[3] / 2);
        Assert.True(where.InAWindow,
            $"{staged}: at the device's centre the browser hits {where}, not a window - this test would not tell a device brought into the corner from one left where it was.");

        await editor.ChooseDeviceListRowAsync(PlantSeed.DraggedName);

        var cell = await AssertTheCellIsWhereNoWindowCoversItAsync(editor, PlantSeed.DraggedId.ToString(), staged);
        var x = cell[0] + cell[2] / 2;
        // In the corner, and centred across the part of it that can be SEEN: from the frame's left edge
        // to the Missions window's - both measured on the page. Centred across the whole corner, the
        // 10 px nobody sees included, it was 5 px to the left (final review of rows 22-29). Up and down,
        // between the Devices window and the frame's foot. mxGraph's scroll lands to within its own
        // 2 px.
        var seenCentre = (frame[0] + lower[0]) / 2;
        Assert.True(Math.Abs(x - seenCentre) <= 2.5 && cell[1] >= upper[1] + upper[3] - 2 && cell[1] + cell[3] <= frame[1] + frame[3] + 2,
            FormattableString.Invariant($"{staged}, draw.io's frame at {Describe(frame)}, and the device's cell is at {Describe(cell)} after \"go to\": it is not centred ({x:0.##} against {seenCentre:0.##}) in the corner's part that can be seen, left of the Missions window and under the Devices one."));
    }

    /// <summary>
    /// Queue item 25, final review of rows 22-29: "holds the cell" is said of the box the cell COVERS
    /// on the screen - at the zoom of the moment, and turned as mxGraph paints a turned device. The
    /// seed's device (120 x 60 drawing units) turned a quarter and seen at half size stands 30 px wide
    /// and 60 tall: a column 45 px wide between the Devices and the Missions windows, the one part of
    /// the canvas they leave free, holds it. Held up by its unturned box (60 x 30 on the screen) or by
    /// its turned box in drawing units (60 x 120), nothing would hold it, and "go to" would centre it
    /// on the plain centre of the canvas - under the Missions window.
    /// </summary>
    /// <remarks>
    /// The column is left of the middle and not at an edge of the canvas: draw.io's own panels lie over
    /// its drawing's area at the edges (measured: at the right edge, x 1259, draw.io's format panel's
    /// div.geCollapsibleTitle), and the free part of the canvas is the one the page's windows leave
    /// (owner's decision D2), not draw.io's - this test is about the size of the cell, not about them.
    /// </remarks>
    [Fact]
    public async Task ARowOfTheDeviceWindowBringsATurnedDeviceAtHalfSizeIntoAColumnThatHoldsWhatIsPainted()
    {
        const double columnLeft = 400, column = 45, windowsRight = 1300, browserBottom = 1000;
        await using var editor = await OpenAsync();
        var cellId = PlantSeed.DraggedId.ToString();
        await editor.SetCellRotationAsync(cellId, 90);
        Assert.Equal(0.5, await editor.ZoomToAsync(0.5));
        var frame = await editor.BoxOfAsync("#drawio-frame");
        var before = await editor.CellBoxOnScreenAsync(cellId);

        await editor.OpenWindowAsync("drawio-mission-list");
        var right = await editor.PlaceWindowAsync("drawio-mission-list", columnLeft + column, frame[1], windowsRight - columnLeft - column, browserBottom - frame[1]);
        await editor.OpenWindowAsync("drawio-device-list");
        var left = await editor.PlaceWindowAsync("drawio-device-list", 0, frame[1], columnLeft, browserBottom - frame[1]);
        var canvas = await editor.CanvasBoxOnScreenAsync();

        // Not blind. The windows cover the canvas from top to bottom but for the column between them;
        // the column holds the device standing on the screen (its unturned box's height across, its
        // width down) and does not hold it lying (its unturned width across) - nor standing at full
        // size, whose width across is that same unturned width.
        var staged = $"The canvas is at {Describe(canvas)}, draw.io's frame at {Describe(frame)}, the device's unturned box at {Describe(before)}, the Devices window at {Describe(left)}, the Missions window at {Describe(right)}";
        var free = right[0] - (left[0] + left[2]);
        Assert.True(left[0] <= Math.Max(frame[0], 0) + SamePixel && right[0] + right[2] >= canvas[0] + canvas[2]
                && left[1] <= canvas[1] && right[1] <= canvas[1]
                && left[1] + left[3] >= canvas[1] + canvas[3] && right[1] + right[3] >= canvas[1] + canvas[3],
            $"{staged}: the two windows do not cover the canvas from top to bottom, but for a column between them.");
        Assert.True(free < before[2] && free >= before[3] + 5 && canvas[3] >= before[2] + 5,
            FormattableString.Invariant($"{staged}: the column left free ({free:0.##} px) does not hold the device standing ({before[3]:0.##} x {before[2]:0.##}) while being narrower than it lying ({before[2]:0.##})."));
        var middle = await editor.WhatIsHitAtAsync(canvas[0] + canvas[2] / 2, canvas[1] + canvas[3] / 2);
        Assert.True(middle.InAWindow,
            $"{staged}: at the middle of the canvas the browser hits {middle}, not a window - this test would not tell the column from the plain centre.");
        var where = await editor.WhatIsHitAtAsync(before[0] + before[2] / 2, before[1] + before[3] / 2);
        Assert.True(where.InAWindow,
            $"{staged}: at the device's centre the browser hits {where}, not a window - this test would not tell a device brought into the column from one left where it was.");

        await editor.ChooseDeviceListRowAsync(PlantSeed.DraggedName);

        var cell = await AssertTheCellIsWhereNoWindowCoversItAsync(editor, cellId, staged, turnedAQuarter: true);
        var x = cell[0] + cell[2] / 2;
        var columnCentre = (left[0] + left[2] + right[0]) / 2;
        Assert.True(Math.Abs(x - columnCentre) <= 2.5,
            FormattableString.Invariant($"{staged}, and the device's cell is at {Describe(cell)} after \"go to\": it is not centred in the column ({x:0.##} against {columnCentre:0.##})."));
    }

    /// <summary>
    /// The owner's decision D1 (29/9): the strip's button of a window open behind another brings it to
    /// the front and leaves it open; pressed again, with the window now in front, it shuts it.
    /// </summary>
    [Fact]
    public async Task TheButtonOfAWindowBehindAnotherBringsItToTheFrontAndThenShutsIt()
    {
        await using var editor = await OpenAsync();
        await editor.OpenWindowAsync("drawio-device-list");
        await editor.OpenWindowAsync("drawio-mission-list");
        var devicesBefore = await editor.WindowStackingAsync("drawio-device-list");
        var missionsBefore = await editor.WindowStackingAsync("drawio-mission-list");
        Assert.True(int.Parse(missionsBefore.Computed) > int.Parse(devicesBefore.Computed),
            $"Missions, opened last, is at {missionsBefore} and Devices at {devicesBefore}: Devices is not behind it.");

        await editor.PressTheButtonOfAnOpenWindowAsync("drawio-device-list");
        var devices = await editor.WindowStackingAsync("drawio-device-list");
        var missions = await editor.WindowStackingAsync("drawio-mission-list");
        Assert.True(await editor.IsTheSectionOpenAsync("drawio-device-list"),
            "The button of the Devices window, open behind Missions, shut it instead of bringing it to the front.");
        Assert.True(int.Parse(devices.Computed) > int.Parse(missions.Computed),
            $"After its button, Devices is at {devices} and Missions at {missions}.");

        await editor.PressTheButtonOfAnOpenWindowAsync("drawio-device-list");
        Assert.False(await editor.IsTheSectionOpenAsync("drawio-device-list"),
            "The button of the Devices window, in front, did not shut it.");
        Assert.True(await editor.IsTheSectionOpenAsync("drawio-mission-list"), "Shutting Devices shut Missions too.");
    }

    /// <summary>
    /// A window shut by its X or by Esc gives the keyboard focus back to its button in the strip. Esc
    /// is the windows module's, not Blazor's: it presses the X of the window that has the focus.
    /// </summary>
    [Fact]
    public async Task TheXAndEscShutTheWindowAndGiveTheFocusBackToItsButton()
    {
        await using var editor = await OpenAsync();
        await editor.OpenWindowAsync("drawio-device-list");
        await editor.CloseTheWindowAsync("drawio-device-list");
        await editor.WaitForTheFocusOnAsync("drawio-device-list-toggle");

        await editor.OpenWindowAsync("drawio-mission-list");
        await editor.ShutTheWindowWithEscapeAsync("drawio-mission-list");
        await editor.WaitForTheFocusOnAsync("drawio-mission-list-toggle");
    }

    /// <summary>
    /// Esc typed in a field of a window is the field's (a search box clears itself, an editor gives
    /// up): the window stays open. Another window is then opened, and the page renders the gestures in
    /// the order they came, so by the time that one is open a close caused by the Esc would be too.
    /// </summary>
    [Fact]
    public async Task EscTypedInAFieldOfTheWindowLeavesTheWindowOpen()
    {
        await using var editor = await OpenAsync();
        await editor.OpenWindowAsync("drawio-device-list");
        await editor.FocusAsync("#drawio-device-list-filter");
        await editor.PressAsync("Escape");

        await editor.OpenWindowAsync("drawio-junction-list");
        Assert.True(await editor.IsTheSectionOpenAsync("drawio-device-list"),
            "Esc typed in the device list's filter shut the Devices window.");
    }

    /// <summary>
    /// The module's show() gives an opened window the focus - unless the operator has already put it
    /// inside the window: the render un-hides the window a round trip before show() arrives, and on a
    /// slow link a click in its filter can come in between, which show() must not undo. Staged by
    /// calling show() once more on the open window with the focus in its filter, from the page and on
    /// the module the page imported (the helper checks it reached the instance that knows the window).
    /// This is show()'s way for a window the operator opens: the memory was empty when the page loaded
    /// (OpenAsync asserts it), so the page opened nothing again, and the opening of Devices would have
    /// ended any reopening anyway (the page's rememberOpen). The page opening again the windows of the
    /// last visit is the next test.
    /// </summary>
    [Fact]
    public async Task AShowThatFindsTheFocusAlreadyInTheWindowLeavesItWhereItIs()
    {
        await using var editor = await OpenAsync();
        await editor.OpenWindowAsync("drawio-device-list");
        await editor.FocusAsync("#drawio-device-list-filter");
        await editor.WaitForTheFocusOnAsync("drawio-device-list-filter");

        var shown = await editor.ShowTheWindowAgainAsync("drawio-device-list");
        output.WriteLine(shown.ToString());

        // Not blind: the call reached the module that knows the window - it brought it back inside
        // the browser.
        Assert.True(shown.Left >= 0,
            $"show() left the window outside the browser: the call did not reach the module that attached it ({shown}).");
        Assert.True(shown.FocusedId == "drawio-device-list-filter",
            $"show() took the focus out of the filter it had been put in: {shown}.");
    }

    /// <summary>
    /// The same race while the page opens again the windows of the last visit, which it shows one
    /// after the other: a focus the operator has put in one of them stays where it is, and that window
    /// ends up in front of the others - the field is not left under another window, taking the keys
    /// there. Staged with Devices and Junctions, which open overlapping, Junctions in front: the focus
    /// put in Devices' filter between the show() of Junctions and that of Devices (the window shown is
    /// not the one the reopening puts in front), in Junctions' filter before the show() of Devices (the
    /// one in front, another window shown), and in Devices' filter before any show() (a window of the
    /// reopening not shown yet, whose show() comes last). Not blind: the reopening went by the memory
    /// of both windows, Junctions in front; show() reached the module that knows them (it brought each
    /// back inside the browser); and the other window's box holds the middle of the field, so that in
    /// front it would hide it.
    /// </summary>
    [Theory]
    [InlineData("drawio-junction-list", "drawio-device-list-filter", "drawio-device-list")]
    [InlineData("drawio-junction-list", "drawio-junction-list-filter", "drawio-device-list")]
    [InlineData("", "drawio-device-list-filter", "drawio-junction-list drawio-device-list")]
    public async Task AReopeningLeavesTheFocusWhereTheOperatorPutItAndItsWindowInFront(string shownBefore, string field, string shownAfter)
    {
        await using var editor = await OpenAsync();
        await editor.OpenWindowAsync("drawio-device-list");
        await editor.OpenWindowAsync("drawio-junction-list");

        var reopening = await editor.ReopenTheOpenWindowsAsync(Ids(shownBefore), field, Ids(shownAfter));
        output.WriteLine(reopening.ToString());

        Assert.Equal(["drawio-device-list", "drawio-junction-list"], reopening.Order);
        Assert.True(reopening.Outside.Length == 0,
            $"show() left windows outside the browser: the calls did not reach the module that attached them ({reopening}).");
        Assert.True(reopening.WindowsOverTheField.Length > 0,
            $"No other window's box holds the middle of {field}: no window could hide it, in front or not ({reopening}).");
        Assert.True(reopening.FocusedId == field, $"show() took the focus out of {field}: {reopening}.");
        Assert.True(reopening.FieldOnTop, $"The focus stayed in {field}, under another window: {reopening}.");

        static string[] Ids(string ids) => ids.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// The windows open at the end of the visit open again stacked as they were: the one in front in
    /// front again, with the focus - not the last one of the strip.
    /// </summary>
    [Fact]
    public async Task TheWindowsOpenAgainAfterAReloadInTheOrderTheyWereStacked()
    {
        await using var editor = await OpenAsync();
        await editor.OpenWindowAsync("drawio-device-list");
        await editor.OpenWindowAsync("drawio-mission-list");
        // Devices, earlier in the strip, is brought in front of Missions by a touch.
        await editor.ClickAsync("#drawio-device-list .drawio-window-title");
        var devicesBefore = await editor.WindowStackingAsync("drawio-device-list");
        var missionsBefore = await editor.WindowStackingAsync("drawio-mission-list");
        Assert.True(int.Parse(devicesBefore.Computed) > int.Parse(missionsBefore.Computed),
            $"Before the reload Devices is at {devicesBefore} and Missions at {missionsBefore}: Devices is not in front.");

        await editor.ReloadAndPassTheKeyGateAsync(fixture.Host.ApiKey);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForTheWindowShownAsync("drawio-device-list");
        await editor.WaitForTheWindowShownAsync("drawio-mission-list");
        await editor.WaitForTheFocusInTheWindowAsync("drawio-device-list");
        var devices = await editor.WindowStackingAsync("drawio-device-list");
        var missions = await editor.WindowStackingAsync("drawio-mission-list");
        Assert.True(int.Parse(devices.Computed) > int.Parse(missions.Computed),
            $"After the reload Devices is at {devices} and Missions at {missions}.");
    }

    /// <summary>
    /// Queue item 26: at an opening with a window remembered open, draw.io takes the keyboard focus into
    /// its frame when it puts the plant on the canvas, and the bridge gives it back to the window at
    /// draw.io's 'load' - unless the operator has pressed on the canvas in between: that focus is the
    /// operator's, and stays in draw.io. The gap is held open by a condition, not by time: draw.io's
    /// 'init' is held until the window is shown with the focus, so the load goes out with the focus in
    /// the window, and its 'load' is held until the press (or none) has been made. Not blind: before the
    /// release the focus is on draw.io's frame - draw.io has taken it - so the window can have it only
    /// if the bridge gives it back.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AtAnOpeningThePressOnTheCanvasBeforeDrawiosLoadKeepsTheFocusInDrawio(bool pressed)
    {
        await using var editor = await OpenAsync();
        await editor.OpenWindowAsync("drawio-device-list");

        await editor.HoldDrawiosAnswersFromTheNextOpeningAsync("init", "load");
        await editor.ReloadAndPassTheKeyGateAsync(fixture.Host.ApiKey);
        await editor.WaitForDrawiosAnswerHeldAsync("init");
        await editor.WaitForTheWindowShownAsync("drawio-device-list");
        await editor.WaitForTheFocusInTheWindowAsync("drawio-device-list");
        await editor.ReleaseDrawiosAnswerAsync("init");

        await editor.WaitForDrawiosAnswerHeldAsync("load");
        Assert.True(await editor.TheFocusIsInDrawiosFrameAsync(),
            $"draw.io has put the plant on the canvas and the focus is not in its frame but on {await editor.FocusedElementAsync()}: there is no focus taken to give back.");
        if (pressed) await editor.ClickTheCanvasWhereNoWindowCoversItAsync();

        var focus = await editor.ReleaseDrawiosAnswerAsync("load");
        output.WriteLine($"Pressed on the canvas: {pressed}; the focus after 'load': {focus}.");
        if (pressed)
            Assert.True(focus.OnTheFrame, $"The press on the canvas before 'load' was overridden: the focus went to {focus}.");
        else
            Assert.True(focus.Window == "drawio-device-list", $"With no press, 'load' did not give the focus back to the window: it is on {focus}.");
    }

    /// <summary>
    /// The windows open where the cascade puts them, one of them is dragged up against the floor, and
    /// then the row of outcomes UNDER the bar gets a line by a real gesture: a save from draw.io's own
    /// button, whose line comes into the row. The bar neither moves nor changes size - the test says so -
    /// so it is the row's growth the windows module has to notice: the floor goes down, and every window
    /// goes down with it - none covers a button of the bar, the Rulers switch or the new line. Until the
    /// passata grafica's compito 3 (29/9) what grew was a band ABOVE the bar - the box about the devices
    /// the drawing does not show, opened by its line - and the bar went down with it; that box is a line
    /// of this row now, and its list a window. The bar growing by itself is the next test: a different
    /// element of the column, and a different entry of the module's observer.
    /// </summary>
    [Fact]
    public async Task NoWindowCoversTheRowUnderTheBarWhenALineComesIntoIt()
    {
        await using var editor = await OpenAsync();
        foreach (var id in Sections) await editor.OpenWindowAsync(id);
        // Plant file, the first of the cascade, against the floor: a floor coming down by one line only is
        // in its way. Straight up, so that it stays clear of draw.io's Save button at the right.
        await editor.DragAsync("#drawio-plant-file .drawio-window-title", 0, -400);
        var before = await editor.TheStripAndItsWindowsAsync(Sections);
        Assert.Empty(await editor.OutcomeLinesAsync());

        await editor.SaveAsync();
        var lines = await editor.OutcomeLinesAsync();
        var after = await editor.TheStripAndItsWindowsAsync(Sections);
        output.WriteLine($"Before the save: {before}");
        output.WriteLine($"After the save, with the row saying {string.Join(" / ", lines)}: {after}");

        // What grew is the row under the bar: the bar stayed where it was, as high as it was.
        Assert.Equal(["drawio-save-notice"], lines);
        Assert.True(Math.Abs(after.StripBottom - before.StripBottom) <= SamePixel && Math.Abs(after.StripHeight - before.StripHeight) <= SamePixel,
            FormattableString.Invariant($"The bar moved or changed size when a line came into the row under it: before {before}; after {after}."));
        Assert.True(after.Floor > before.Floor + SamePixel,
            FormattableString.Invariant($"The floor did not go down when a line came into the row: before {before.Floor:0.##}, after {after.Floor:0.##}."));
        AssertTheWindowsWentDownWithTheFloor(before, after);
    }

    /// <summary>
    /// A report's window - the list of the devices the drawing does not show, behind the "Details" of its
    /// line in the row - belongs to the same column as the sections' windows: dragged up as far as it
    /// goes, across the column of its own line's "Details" (at the right end of the line), it stops at
    /// the floor, below the bar AND below the row with its own line, whose "Details" can still be reached.
    /// </summary>
    [Fact]
    public async Task AReportsWindowDraggedUpStopsBelowTheRowOfOutcomes()
    {
        await using var editor = await OpenAsync(withAnUndrawnDevice: true);
        await editor.OpenTheUndrawnBoxAsync();
        var toggle = await editor.BoxOfAsync("#drawio-undrawn-details-toggle");
        var opened = await editor.BoxOfAsync("#drawio-undrawn-details");
        await editor.DragAsync("#drawio-undrawn-details .drawio-window-title",
            toggle[0] + toggle[2] / 2 - (opened[0] + opened[2] / 2), -400);

        var after = await editor.TheStripAndItsWindowsAsync("drawio-undrawn-details");
        var window = after.Windows.Single();
        var line = await editor.BoxOfAsync("#drawio-undrawn-notice");
        var details = await editor.HitAtTheMiddleOfAsync("#drawio-undrawn-details-toggle");
        output.WriteLine(FormattableString.Invariant($"{after}; the line at {line[1]:0.##}-{line[1] + line[3]:0.##}; {details}"));

        Assert.True(window.Open && Math.Abs(window.Top - after.Floor) <= SamePixel,
            FormattableString.Invariant($"The window dragged 400 px up is {window}; the floor (the top of the drawing's area) is at {after.Floor:0.##}."));
        // Not blind: the window lies across the column of "Details", so only the floor keeps it off.
        Assert.True(window.Left <= details.X && details.X <= window.Left + window.Width,
            FormattableString.Invariant($"The window {window} does not lie across the column of the line's \"Details\" at x {details.X:0.##}."));
        Assert.True(after.Floor >= line[1] + line[3] - SamePixel && after.Floor >= after.StripBottom,
            FormattableString.Invariant($"The floor at {after.Floor:0.##} is above the line of the row (bottom {line[1] + line[3]:0.##}) or the bar (bottom {after.StripBottom:0.##})."));
        Assert.True(details.HitsIt, $"The line's \"Details\" is not reachable: {details}.");
        Assert.All(after.Buttons, b => Assert.True(b.HitsIt, $"The button {b.Id} is not reachable: {b}."));
    }

    /// <summary>
    /// A report's window shut while its line is behind "+N more" gives the keyboard's focus to the row of
    /// outcomes: the focus goes back to the button that opens the window again, and that line's "Details"
    /// is hidden - a hidden button takes no focus, and it fell to the page's body. Three outcomes: the
    /// devices the drawing does not show, a save and a refused cut, the newest on top - the first one's
    /// line is the one "+1 more" hides.
    /// <para>
    /// "Show fewer" hands the focus on to "+1 more" a round trip after the row is back on screen
    /// (DrawioNotices's OnAfterRenderAsync, once the browser has acknowledged the render), and the test
    /// waits for it before it puts the focus in the window. Until 9/10 it did not, and once in four runs
    /// of the class (queue item 38) the hand-off landed after the Esc: the focus was then outside the
    /// window, and shut() rightly left it where it was - on "+1 more". Seen on 9/10: the focus nowhere
    /// when the test had read "+1 more", and on it 0.7 ms later. Since 10/10 a late hand-off no longer
    /// takes a focus that is not lost (<see cref="TheRowHandsTheFocusOnOnlyIfItWasLost"/>); the wait stays.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AReportsWindowShutWhileItsLineIsBehindPlusMoreGivesTheFocusToTheRow()
    {
        await using var editor = await OpenAsync(withAnUndrawnDevice: true);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);
        await editor.SaveAsync();
        await editor.SelectDevicesAsync(PlantSeed.DraggedName);
        await editor.CutAsync();
        await editor.WaitForTextAsync("#drawio-cut-refused", "A plant device cannot be cut");
        Assert.Equal(["drawio-cut-notice", "drawio-save-notice"], await editor.OutcomeLinesAsync());

        await editor.ClickAsync("#drawio-notices-more");
        await editor.WaitForShownAsync("#drawio-undrawn-notice", "the line of the devices not in the drawing");
        await editor.OpenTheUndrawnBoxAsync();
        await editor.ClickAsync("#drawio-notices-more");
        await editor.WaitForTextAsync("#drawio-notices-more", "+1 more");
        // The text comes with the render, the focus a round trip later: moved into the window before it
        // lands, the focus would be taken from under the Esc.
        await editor.WaitForTheFocusOnAsync("drawio-notices-more");
        Assert.False(await editor.IsShownAsync("#drawio-undrawn-details-toggle"), "The test is blind: the line's \"Details\" is on screen.");

        await editor.FocusAsync("#drawio-undrawn-details");
        await editor.ShutTheWindowWithEscapeAsync("drawio-undrawn-details");
        await editor.WaitForTheFocusOnAsync("drawio-notices");
    }

    /// <summary>
    /// Queue item 38: "+N more", "Show fewer" and the X of a line hand the keyboard's focus on - to the
    /// button in its new place, or to the row - a round trip after the render (DrawioNotices's
    /// OnAfterRenderAsync), and only if it was lost meanwhile. Pressed with the mouse, the button had the
    /// focus and the render took it away with it: the focus goes where the hand-off sends it. Pressed while
    /// the operator puts the focus in the device list's filter before the page's answer can arrive, the
    /// focus stays in the filter.
    /// <para>
    /// Not blind: the second press from script is sent after the browser has acknowledged the render of
    /// the first, which is what starts the first one's hand-off, and the host's messages arrive in order -
    /// once the second render is on screen, the first hand-off has run. The windows module is already
    /// imported by then (the first, mouse, press's hand-off went through it), so that hand-off is one
    /// message. Before 10/10 it took the focus from the filter (seen on 10/10 with the check taken out:
    /// the focus was on "Show fewer").
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheRowHandsTheFocusOnOnlyIfItWasLost()
    {
        output.WriteLine(fixture.Host.Declaration);
        await using var editor = await OpenAsync(withAnUndrawnDevice: true);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);
        await editor.SaveAsync();
        await editor.SelectDevicesAsync(PlantSeed.DraggedName);
        await editor.CutAsync();
        await editor.WaitForTextAsync("#drawio-cut-refused", "A plant device cannot be cut");
        Assert.Equal(["drawio-cut-notice", "drawio-save-notice"], await editor.OutcomeLinesAsync());
        await editor.OpenWindowAsync("drawio-device-list");

        // Lost: "+1 more" pressed with the mouse goes with its line, "Show fewer" takes the focus. The
        // text first: until the render the focus is on "+1 more", which has the same id.
        await editor.ClickAsync("#drawio-notices-more");
        await editor.WaitForTextAsync("#drawio-notices-more", "Show fewer");
        await editor.WaitForTheFocusOnAsync("drawio-notices-more");

        // Kept: "Show fewer" pressed, and the focus put in the filter before the answer.
        await editor.PressFromScriptAsync("#drawio-notices-more", thenFocus: "#drawio-device-list-filter");
        await editor.WaitForTextAsync("#drawio-notices-more", "+1 more");
        await editor.PressFromScriptAsync("#drawio-notices-more");
        await editor.WaitForTextAsync("#drawio-notices-more", "Show fewer");
        Assert.Equal("drawio-device-list-filter", await editor.FocusedElementIdAsync());

        // Lost again: the X pressed with the mouse goes with its line, the row takes the focus.
        await editor.ClickAsync("#drawio-cut-dismiss");
        await editor.WaitForTheFocusOnAsync("drawio-notices");
        Assert.Equal(["drawio-save-notice", "drawio-undrawn-notice"], await editor.OutcomeLinesAsync());
    }

    /// <summary>
    /// The windows open where the cascade puts them, and then the bar itself grows, as when it wraps
    /// onto a second line - by a height chosen to bring the floor past the tops of the first windows of
    /// the cascade: every window goes down with the floor.
    /// </summary>
    [Fact]
    public async Task NoWindowCoversTheStripWhenTheBarGrows()
    {
        await using var editor = await OpenAsync();
        foreach (var id in Sections) await editor.OpenWindowAsync(id);
        var before = await editor.TheStripAndItsWindowsAsync(Sections);

        await editor.GrowTheBarByAsync(150);
        var after = await editor.TheStripAndItsWindowsAsync(Sections);
        output.WriteLine($"Before the bar grew: {before}");
        output.WriteLine($"After the bar grew: {after}");

        AssertTheWindowsWentDownWithTheFloor(before, after);
    }

    /// <summary>
    /// After the floor went down: it was in the way of at least one window where it opened (or the test
    /// would see nothing), it is below the bar and the Rulers switch in it, every window is below it, and
    /// every button of the bar - the engine's, the plant checks' and the six sections' - and the Rulers
    /// switch can still be reached.
    /// </summary>
    private static void AssertTheWindowsWentDownWithTheFloor(DrawioStripAndWindows before, DrawioStripAndWindows after)
    {
        // Not blind: at least one window, where it opened, is now above the floor.
        Assert.True(before.Windows.Any(w => w.Top < after.Floor - SamePixel),
            FormattableString.Invariant($"The floor went down only to {after.Floor:0.##}: no window was in its way ({before})."));
        // The floor is below the bar, the Rulers switch in it included.
        Assert.True(after.Floor >= after.StripBottom && after.Floor >= after.Rulers.Y, after.ToString());
        Assert.All(after.Windows, w => Assert.True(w.Open && w.Top >= after.Floor - SamePixel,
            FormattableString.Invariant($"{w} is above the floor (the top of the drawing's area) at {after.Floor:0.##}.")));
        Assert.Equal(BarButtons, after.Buttons.Select(b => b.Id));
        Assert.All(after.Buttons, b => Assert.True(b.HitsIt, $"The button {b.Id} is not reachable: {b}."));
        Assert.True(after.Rulers.HitsIt, $"The Rulers switch is not reachable: {after.Rulers}.");
    }

    /// <summary>
    /// Dragged up and to the left as far as it goes, the window stops at the floor - the top of the
    /// drawing's area - in the corner where it lies over the Rulers switch's column, and the switch and
    /// every button of the bar are still reached. Until the passata grafica (29/9) the Rulers switch was
    /// a strip of its own under the bar, and this test also told the floor from the bar's bottom edge;
    /// the switch is in the bar since, and with nothing under the bar here the two are one. The row of
    /// outcomes under the bar tells them apart: AReportsWindowDraggedUpStopsBelowTheRowOfOutcomes and
    /// NoWindowCoversTheRowUnderTheBarWhenALineComesIntoIt.
    /// </summary>
    [Fact]
    public async Task ATitleBarDraggedUpAndLeftStopsAtTheFloorAcrossTheRulersColumn()
    {
        await using var editor = await OpenAsync();
        await editor.OpenWindowAsync("drawio-device-list");
        var before = await editor.BoxOfAsync("#drawio-device-list");
        await editor.DragAsync("#drawio-device-list .drawio-window-title", -400, -400);

        var after = await editor.TheStripAndItsWindowsAsync("drawio-device-list");
        var window = after.Windows.Single();
        Assert.True(Math.Abs(window.Top - after.Floor) <= SamePixel && Math.Abs(window.Left) <= SamePixel,
            FormattableString.Invariant($"The window was at {Describe(before)}, was dragged 400 px up and to the left, and is {window}; ") +
            FormattableString.Invariant($"the floor (the top of the drawing's area) is at {after.Floor:0.##}, the strip's bottom edge at {after.StripBottom:0.##}."));
        // Not blind: the window's columns are the Rulers switch's, so only its top keeps it off the switch.
        Assert.True(window.Left <= after.Rulers.X && after.Rulers.X <= window.Left + window.Width,
            $"The window {window} does not lie across the Rulers switch's column ({after.Rulers}).");
        Assert.True(after.Rulers.HitsIt, $"The Rulers switch is not reachable: {after}.");
        Assert.All(after.Buttons, b => Assert.True(b.HitsIt, $"The button {b.Id} is not reachable: {b}."));
    }

    /// <summary>
    /// The page's column taller than the browser - several reports open above the strip - makes the
    /// page itself scroll, and the scroll moves the floor with nothing changing size. A window opened
    /// with the page scrolled to the bottom sits at the floor of that moment; scrolled back up, the
    /// floor goes down and the window must go down with it, or it covers the page's controls.
    /// </summary>
    [Fact]
    public async Task ScrollingThePageBackUpTakesTheWindowsDownWithTheFloor()
    {
        await using var editor = await OpenAsync();
        var scrollable = await editor.OverflowThePageByAsync(300);
        Assert.True(scrollable >= 250,
            FormattableString.Invariant($"The page scrolls by {scrollable:0.##} px only: the column did not become taller than the browser."));

        await editor.ScrollThePageToAsync(scrollable);
        await editor.OpenWindowAsync("drawio-device-list");
        var low = await editor.TheStripAndItsWindowsAsync("drawio-device-list");
        Assert.True(Math.Abs(low.Windows.Single().Top - low.Floor) <= SamePixel,
            $"Opened with the page scrolled to the bottom, the window is not at the floor: {low}.");

        await editor.ScrollThePageToAsync(scrollable - 100);
        var after = await editor.TheStripAndItsWindowsAsync("drawio-device-list");
        Assert.True(after.Floor - low.Floor >= 100 - SamePixel,
            $"Scrolled back up by 100 px, the floor moved from {low.Floor} to {after.Floor} only.");
        var window = after.Windows.Single();
        Assert.True(window.Top >= after.Floor - SamePixel,
            FormattableString.Invariant($"Scrolled back up by 100 px, {window} is above the floor at {after.Floor:0.##}: {after}."));
    }

    /// <summary>
    /// A fresh seed and the page open on it, with the seed's device drawn - and no memory of the
    /// windows in this browser (see the class remarks). <paramref name="withAnUndrawnDevice"/> adds
    /// a device the drawing cannot show (placed and sized, of type 99 as in DrawioUndrawnTest): the page
    /// then has a line about it in the row under the bar, and its list in a window behind "Details".
    /// <paramref name="window"/> is the size of the browser's window; without it, the wide window of
    /// <see cref="DrawioEditorPage.OpenAsync(Microsoft.Playwright.IBrowser, WcsHostProcess)"/>.
    /// </summary>
    private async Task<DrawioEditorPage> OpenAsync(bool withAnUndrawnDevice = false, (int Width, int Height)? window = null,
        bool withAJunction = false)
    {
        output.WriteLine(fixture.Host.Declaration);
        using (var client = fixture.CreateClient())
        {
            await PlantSeed.ResetAsync(client, SeedHolder, withJunction: withAJunction);
            if (withAnUndrawnDevice)
                await PlantSeed.AddDeviceAsync(client, SeedHolder, UndrawnId, "UndrawnDevice", DeviceTypes.None,
                    new PlantItemGeometryDto(UndrawnId, X: 900, Y: 150, Width: 120, Height: 60, Rotation: 0));
        }

        var editor = window is { } size
            ? await DrawioEditorPage.OpenInAWindowOfAsync(fixture.Browser, fixture.Host, size.Width, size.Height)
            : await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        try
        {
            await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
            var memory = await editor.WindowMemoryAsync();
            Assert.True(memory.Length == 0,
                $"The browser opened with the memory of windows moved or opened before this test: {string.Join(", ", memory)}.");
            return editor;
        }
        catch
        {
            await editor.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Waits for the separate browser window's close to reach the test: Playwright learns it from an
    /// event of its own, which may come after the page has already seen the window go. The ceiling is
    /// the page's: past it the window never closed.
    /// </summary>
    private static Task WaitForCloseAsync(IPage popup)
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        popup.Close += (_, _) => closed.TrySetResult();
        if (popup.IsClosed) closed.TrySetResult();
        return closed.Task.WaitAsync(TimeSpan.FromSeconds(60));
    }

    private static bool Inside(double[] box, double x, double y) =>
        x > box[0] && x < box[0] + box[2] && y > box[1] && y < box[1] + box[3];

    private static string Describe(double[] box) =>
        FormattableString.Invariant($"{box[0]:0.##},{box[1]:0.##} {box[2]:0.##}x{box[3]:0.##}");
}
