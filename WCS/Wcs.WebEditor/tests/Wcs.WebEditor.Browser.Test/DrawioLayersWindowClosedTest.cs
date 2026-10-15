using Xunit;
using Xunit.Abstractions;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// Row 37 of the parity inventory, closed on 2026-09-25 (owner's decision, "Cose che non tornano"
/// voce e). draw.io's own Layers window is the one place the default layer (<c>mxCell id="1"</c>,
/// <c>DrawioDocument.DefaultLayerId</c> - the layer every device and every junction hangs from) can
/// be switched off or locked from the canvas, and it was reachable even with <c>ui=min</c> through
/// Ctrl+Shift+L - measured before this batch in <c>DrawioMarkedCellsVisibleBrowserTest</c>'s own
/// remark. See <c>drawio-bridge.js</c>'s <c>closeTheLayersWindow</c> for the routes measured in the
/// bundle and why the fix is a no-op rather than a throw (the one respect in which it differs from
/// <c>keepOnePageOnly</c>'s own <c>insertPage</c>/<c>duplicatePage</c> block:
/// <c>EditorUi.prototype.showLayersDialog</c> calls the same action's <c>funct()</c> directly, with
/// no <c>try</c> of its own around it, from the tail of every file load).
/// </summary>
public sealed class DrawioLayersWindowClosedTest(DrawioEditorFixture fixture, ITestOutputHelper output) : IClassFixture<DrawioEditorFixture>
{
    private const string SeedHolder = "browser-test-layers-window-closed";

    private void DeclareBinary() => output.WriteLine(fixture.Host.Declaration);

    /// <summary>
    /// THE REAL GESTURE, Ctrl+Shift+L, on a real keyboard event through draw.io's own key handler:
    /// <c>bindAction</c>'s wrapper asks the action's <c>isEnabled()</c> BEFORE calling <c>funct</c>
    /// (measured in <c>app.min.js</c>), so this is what proves the shortcut itself is closed - not
    /// merely that a direct call is safe. The window stays closed, and every marked cell stays
    /// exactly as visible as <c>DrawioMarkedCellsVisibleBrowserTest</c> measures it without the
    /// shortcut ever pressed: both the model's own flag and what is actually RENDERED, because
    /// opening the window is the only way an operator reaches the eye icon that would turn either of
    /// them off (see that test's own remark, and <c>DrawioEditorPage.MarkedCellVisibilityAsync</c>'s
    /// for why the two are asked separately).
    /// </summary>
    [Fact]
    public async Task CtrlShiftLDoesNotOpenTheWindowAndEveryMarkedCellStaysVisible()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        var junction = await PlantSeed.ResetAsync(client, SeedHolder, withJunction: true);
        Assert.NotNull(junction);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);

        Assert.False(await editor.LayersWindowIsOpenAsync(),
            "The window is already open before the shortcut was even pressed: this test would not be measuring the shortcut.");

        // A real click first, so the keystroke below lands on draw.io's own canvas and not on some
        // other focused element of the page - the same setup DrawioGroupedCellsTest uses before
        // GroupSelectionAsync/UngroupSelectionAsync.
        await editor.SelectDevicesAsync(PlantSeed.DraggedName);
        await editor.PressLayersShortcutAsync();

        Assert.False(await editor.LayersWindowIsOpenAsync(),
            "Ctrl+Shift+L opened draw.io's own Layers window: from there the default layer can be switched off or locked for the rest of the session, with nothing on this page saying a word about it.");

        var cells = await editor.MarkedCellVisibilityAsync();
        output.WriteLine("Marked cells: " + string.Join(", ", cells.Select(c => c.ToString())));

        // Two devices and one junction, no fewer: a check that quietly found zero would pass this
        // assertion for the wrong reason (MEMORY: "zero non e' una prova").
        Assert.Equal(2, cells.Count(c => c.Mark == "device"));
        Assert.Equal(1, cells.Count(c => c.Mark == "junction"));

        foreach (var cell in cells)
        {
            Assert.True(cell.ModelVisible, $"{cell} should be visible in draw.io's own model");
            Assert.True(cell.Rendered, $"{cell} should be rendered on screen");
        }
    }

    /// <summary>
    /// The action itself, read the way draw.io's own <c>showLayersDialog</c> would see it: disabled
    /// for the shortcut and an ordinary menu click (<c>isEnabled()</c> false), and INERT for the
    /// direct call <c>showLayersDialog</c> makes - it does not throw, and it does not create the
    /// window either. Both matter: <c>isEnabled()</c> alone would leave that second road open (it is
    /// never asked there), and a <c>funct</c> left throwing would take the rest of every file load
    /// down with it the day this plant's own document ever carried more than one layer on its page -
    /// see <c>closeTheLayersWindow</c>'s own comment for why that is not merely theoretical.
    /// </summary>
    [Fact]
    public async Task TheLayersActionIsDisabledAndItsFunctionIsInert()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);

        var state = await editor.LayersActionStateAsync();
        output.WriteLine(state);

        Assert.Contains("isEnabled=false", state);
        Assert.Contains("threw=(none)", state);
        Assert.Contains("windowBefore=false", state);
        Assert.Contains("windowAfter=false", state);
    }

    /// <summary>
    /// Fix round 1 (reviewer finding 1 - important, reproduced live). MEASURED:
    /// <c>EditorUi.prototype.init</c> calls <c>this.restoreVisibleWindows()</c> SYNCHRONOUSLY while
    /// the Editor is being built - well before draw.io's own 'init' message, the event this whole
    /// bridge waits for before touching anything, ever fires - and that method reopens the Layers
    /// window BY ITSELF whenever <c>mxSettings.getWindowState("layers").visible</c> is true in the
    /// shared <c>.drawio-config</c> browser entry (a session before this fix existed, or any other
    /// tool sharing the origin, could have left it there). The disabled Ctrl+Shift+L cannot even
    /// close what already opened before it existed to be pressed. Seeded here exactly the way an
    /// earlier session's own <c>setVisible(true)</c> would have left it (<c>DrawioRulerSwitchBrowserTest</c>'s
    /// own seeding pattern for the same shared entry, extended with <c>windowStates</c>).
    /// </summary>
    [Fact]
    public async Task ASavedOpenLayersWindowStateIsHiddenAtStartup()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        var junction = await PlantSeed.ResetAsync(client, SeedHolder, withJunction: true);
        Assert.NotNull(junction);

        const string seed =
            "window.localStorage.setItem('.drawio-config', JSON.stringify(" +
            "{configVersion: null, windowStates: {layers: {visible: true, x: 100, y: 100, w: 212, h: 200}}, version: 18}));";

        await using var editor = await DrawioEditorPage.OpenWithInitScriptAsync(fixture.Browser, fixture.Host, seed);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);

        Assert.False(await editor.LayersWindowIsOpenAsync(),
            "draw.io's own restoreVisibleWindows reopened the Layers window from the shared .drawio-config entry, before this bridge ever got a chance to close it.");

        var cells = await editor.MarkedCellVisibilityAsync();
        Assert.Equal(2, cells.Count(c => c.Mark == "device"));
        Assert.Equal(1, cells.Count(c => c.Mark == "junction"));
        foreach (var cell in cells)
        {
            Assert.True(cell.ModelVisible, $"{cell} should be visible in draw.io's own model");
            Assert.True(cell.Rendered, $"{cell} should be rendered on screen");
        }
    }
}
