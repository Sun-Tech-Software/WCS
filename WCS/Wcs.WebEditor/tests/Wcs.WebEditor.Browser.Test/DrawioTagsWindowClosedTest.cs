using Xunit;
using Xunit.Abstractions;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// Row 37 of the parity inventory, closed on 2026-09-25 alongside the Layers window (owner's second
/// decision the same day: "nothing in the plant Editor may make a device vanish from view"). draw.io's
/// Tags window lets a tag be marked HIDDEN, and <c>Graph.prototype.isCellVisible</c> is overridden in
/// the bundle to also ask <c>isAllTagsHidden</c> - a marked cell whose every tag is hidden through
/// this window is not rendered, the same loss as a hidden layer, reachable with <c>ui=min</c> through
/// Ctrl+K. See <c>drawio-bridge.js</c>'s <c>closeTheTagsWindow</c> for the routes measured in the
/// bundle - modelled on <c>closeTheLayersWindow</c>, same install point, same inert-not-throwing
/// shape, but with the window itself living on <c>ui.menus.tagsWindow</c> rather than
/// <c>ui.actions.tagsWindow</c> (measured against <c>EditorUi.prototype.getWindows</c>).
/// </summary>
/// <remarks>
/// <para>
/// The Tags window can ALSO rename or delete a tag across the whole plant, and whether that deserves
/// its own treatment is still open (owner's own word, <c>closeTheTagsWindow</c>'s comment) - this
/// suite only measures the one road closed so far, the one to HIDING a cell.
/// </para>
/// <para>
/// <b>Fix round 2: closing this window is the FIRST line, not the only one.</b> Edit Link
/// (Alt+Shift+L) reaches the SAME <c>hiddenTags</c> this window's eye icon sets, through draw.io's
/// own custom-action mini-language, without opening this window at all - see
/// <c>drawio-bridge.js</c>'s <c>guardMarkedCellsVisible</c>, the downstream guard that holds
/// regardless of which route a hidden tag came from. This suite still measures the window's own
/// closure on its own terms (the shortcut, the action's state, the startup race) - it does not
/// re-measure the guard, which <c>DrawioMarkedCellsVisibleGuardBrowserTest</c> does directly.
/// </para>
/// </remarks>
public sealed class DrawioTagsWindowClosedTest(DrawioEditorFixture fixture, ITestOutputHelper output) : IClassFixture<DrawioEditorFixture>
{
    private const string SeedHolder = "browser-test-tags-window-closed";

    private void DeclareBinary() => output.WriteLine(fixture.Host.Declaration);

    /// <summary>
    /// THE REAL GESTURE, Ctrl+K, through draw.io's own key handler
    /// (<c>this.keyHandler.bindAction(75, true, "tags")</c>, measured in <c>app.min.js</c>): the
    /// window stays closed, and every marked cell stays visible - both the model's own flag and what
    /// is actually RENDERED, the same two questions <c>DrawioLayersWindowClosedTest</c> asks. The
    /// window's own eye icon is not the only door to a hidden tag any more (fix round 2: Edit Link
    /// reaches <c>hiddenTags</c> without it, see <c>DrawioMarkedCellsVisibleGuardBrowserTest</c>) -
    /// what this test measures is that the window itself stays shut, the belt this batch put on
    /// first.
    /// </summary>
    [Fact]
    public async Task CtrlKDoesNotOpenTheWindowAndEveryMarkedCellStaysVisible()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        var junction = await PlantSeed.ResetAsync(client, SeedHolder, withJunction: true);
        Assert.NotNull(junction);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);

        Assert.False(await editor.TagsWindowIsOpenAsync(),
            "The window is already open before the shortcut was even pressed: this test would not be measuring the shortcut.");

        await editor.SelectDevicesAsync(PlantSeed.DraggedName);
        await editor.PressTagsShortcutAsync();

        Assert.False(await editor.TagsWindowIsOpenAsync(),
            "Ctrl+K opened draw.io's own Tags window: from there a tag can be hidden, taking every marked cell that carries it off the canvas with nothing on this page saying a word about it.");

        var cells = await editor.MarkedCellVisibilityAsync();
        output.WriteLine("Marked cells: " + string.Join(", ", cells.Select(c => c.ToString())));

        Assert.Equal(2, cells.Count(c => c.Mark == "device"));
        Assert.Equal(1, cells.Count(c => c.Mark == "junction"));

        foreach (var cell in cells)
        {
            Assert.True(cell.ModelVisible, $"{cell} should be visible in draw.io's own model");
            Assert.True(cell.Rendered, $"{cell} should be rendered on screen");
        }
    }

    /// <summary>
    /// The action itself, read the same way <c>DrawioLayersWindowClosedTest.TheLayersActionIsDisabledAndItsFunctionIsInert</c>
    /// reads "layers": disabled for the shortcut, and INERT for a direct call (the shape
    /// <c>restoreVisibleWindows</c> would make on its own, bypassing <c>isEnabled()</c>).
    /// </summary>
    [Fact]
    public async Task TheTagsActionIsDisabledAndItsFunctionIsInert()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);

        var state = await editor.TagsActionStateAsync();
        output.WriteLine(state);

        Assert.Contains("isEnabled=false", state);
        Assert.Contains("threw=(none)", state);
        Assert.Contains("windowBefore=false", state);
        Assert.Contains("windowAfter=false", state);
    }

    /// <summary>
    /// Fix round 1 (reviewer finding 1, extended to Tags by the owner's second decision):
    /// <c>restoreVisibleWindows</c>' own map lists "tags" beside "layers" - the SAME startup race
    /// (the window reopened from the shared <c>.drawio-config</c> entry before this bridge ever runs)
    /// applies to it too, and needs the SAME fix (hide it if it is already open).
    /// </summary>
    [Fact]
    public async Task ASavedOpenTagsWindowStateIsHiddenAtStartup()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        var junction = await PlantSeed.ResetAsync(client, SeedHolder, withJunction: true);
        Assert.NotNull(junction);

        const string seed =
            "window.localStorage.setItem('.drawio-config', JSON.stringify(" +
            "{configVersion: null, windowStates: {tags: {visible: true, x: 100, y: 100, w: 212, h: 200}}, version: 18}));";

        await using var editor = await DrawioEditorPage.OpenWithInitScriptAsync(fixture.Browser, fixture.Host, seed);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);

        Assert.False(await editor.TagsWindowIsOpenAsync(),
            "draw.io's own restoreVisibleWindows reopened the Tags window from the shared .drawio-config entry, before this bridge ever got a chance to close it.");

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
