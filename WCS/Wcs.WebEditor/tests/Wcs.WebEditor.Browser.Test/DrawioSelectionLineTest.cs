using Xunit;
using Xunit.Abstractions;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// Batch A of the properties panel, judged the way it will be used: the operator clicks a device
/// inside draw.io and <b>the page names it</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the test that is worth the batch</b>, and it is here rather than in bUnit because
/// nothing in the chain it exercises exists in bUnit: draw.io, its selection model, the plugin entry
/// point the bridge subscribes through, the postMessage handshake and the Blazor circuit that
/// carries the identifiers back. The bUnit tests measure what the page does with identifiers it is
/// handed; this one measures that identifiers are handed to it at all, by a real click.
/// </para>
/// <para>
/// <b>The deselection is not an extra.</b> A line that names the right device and then keeps naming
/// it after the selection is gone is the defect this branch produces most often - something on
/// screen describing a state that is no longer there - and it would pass every test that only ever
/// selects.
/// </para>
/// <para>
/// Different from <see cref="DrawioSelectionReachTest"/>, which subscribes from the TEST and proves
/// that draw.io still offers the way in. That one guards the ground; this one guards the product
/// standing on it, and they fail for different reasons: draw.io changing, against our bridge or our
/// page changing.
/// </para>
/// </remarks>
public sealed class DrawioSelectionLineTest(DrawioEditorFixture fixture, ITestOutputHelper output) : IClassFixture<DrawioEditorFixture>
{
    private const string SeedHolder = "browser-test-selection-line";

    private const string NothingSelected = "Nothing selected.";

    [Fact]
    public async Task ClickingADeviceMakesThePageNameItAndDeselectingTakesTheNameAway()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);

        // Nobody has clicked, and the page already says so: the bridge subscribes at draw.io's
        // 'init' and reports what is selected right then, which is nothing. It is a fact, not a
        // default - before the bridge answers the page shows no line at all.
        await editor.WaitForSelectionLineAsync(NothingSelected);

        // One device: the name, and the guid nowhere near it.
        await editor.SelectDevicesAsync(PlantSeed.DraggedName);
        await editor.WaitForSelectionLineAsync($"Selected: {PlantSeed.DraggedName}");
        var named = await editor.SelectionLineAsync();
        Assert.DoesNotContain(PlantSeed.DraggedId.ToString(), named);

        // Two devices: counted, not guessed. Naming the first would read exactly like the line
        // above, and would be false.
        await editor.SelectDevicesAsync(PlantSeed.DraggedName, PlantSeed.UntouchedName);
        await editor.WaitForSelectionLineAsync("Selected: 2 cells (2 devices of the plant).");

        // And the gesture that matters: a real click on the empty canvas, after which no name may
        // stay on screen.
        await editor.ClickEmptyCanvasAsync(PlantSeed.DraggedName, PlantSeed.UntouchedName);
        await editor.WaitForSelectionLineAsync(NothingSelected);

        var emptied = await editor.SelectionLineAsync();
        Assert.DoesNotContain(PlantSeed.DraggedName, emptied);
        Assert.DoesNotContain(PlantSeed.UntouchedName, emptied);
    }
}
