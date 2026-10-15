using System.Globalization;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// Row 35 of the parity inventory against a real draw.io: the indicator under the canvas follows the
/// pointer in PLANT units, it is empty off the canvas, and the conversion holds after a zoom - the
/// one thing bUnit cannot measure at all, because it runs no JavaScript and draw.io's own scale and
/// scroll only exist in a real browser.
/// </summary>
public sealed class DrawioPointerPositionBrowserTest(DrawioEditorFixture fixture, ITestOutputHelper output) : IClassFixture<DrawioEditorFixture>
{
    private const string SeedHolder = "browser-test-pointer-position";

    // PlantSeed.DraggedGeometry: X=200, Y=150, Width=133.92, Height=111.96, Rotation=30. The WORLD
    // CENTRE (NodePlacement.WorldCenter) is (200 + 133.92/2, 150 + 111.96/2) = (266.96, 205.98) -
    // and it is that centre, not the migrated box's corner, that DrawioDocument.Build keeps as the
    // drawn shape's own centre too (see the comment over watchPointer in drawio-bridge.js): rotating
    // the shape moves its box around that point, never the point itself. Hovering the middle of the
    // shape on screen is therefore expected to read close to this position, rotated 30 degrees or not.
    private const double ExpectedX = 266.96;
    private const double ExpectedY = 205.98;

    /// <summary>
    /// Fix round 1 (reviewer finding 2): tightened from 8 to 1, now that the indicator reads the RAW
    /// model point instead of draw.io's own grid-snapped one (see the block comment over
    /// <c>pointerGraph</c> in <c>drawio-bridge.js</c> for why the snap was there and why it is not any
    /// more). Declared, not guessed - MEASURED against this very host, repeatably (four runs in a
    /// row): both without a zoom and after three <c>zoomIn</c> steps the indicator read X=267 Y=206
    /// against an expected 266.96/205.98 - off by 0.04/0.02 plant units, identical at both scales.
    /// What is left at that size is not the conversion: it is <c>HoverOverDeviceAsync</c> itself
    /// aiming at the shape's on-screen BOUNDING BOX centre (a Playwright pixel reading, itself
    /// rounded) rather than at its mathematical one, plus this indicator's own display rounding
    /// (<c>N1</c>, one decimal). 1 plant unit covers that with room to spare and is still two orders
    /// of magnitude tighter than the half-grid-cell (5 units) a snapped reading would have cost.
    /// </summary>
    private const double Tolerance = 1;

    [Fact]
    public async Task HoveringADeviceShowsItsPlantPositionEvenAfterAZoomAndLeavingTheCanvasClearsIt()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);

        // Before any move at all: nothing has ever been read, the indicator starts empty.
        Assert.Equal("", await editor.PointerPositionTextAsync() ?? "");

        await editor.HoverOverDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForPointerPositionAsync(
            text => text.Length > 0,
            "the pointer position never showed anything after hovering the device");

        var (x, y) = ParsePosition(await editor.PointerPositionTextAsync() ?? "");
        output.WriteLine($"Read without zoom: X={x} Y={y} (expected {ExpectedX}/{ExpectedY}, tolerance {Tolerance}).");
        Assert.InRange(x, ExpectedX - Tolerance, ExpectedX + Tolerance);
        Assert.InRange(y, ExpectedY - Tolerance, ExpectedY + Tolerance);

        // Off the canvas: the indicator clears - row 35's own words, "empty when the pointer is not
        // on the canvas".
        await editor.MoveMouseOffTheCanvasContainerAsync();
        await editor.WaitForPointerPositionAsync(
            text => text.Length == 0,
            "the pointer position stayed shown after the pointer left the canvas");

        // The SAME plant position, read again after a zoom: what this proves is that the conversion
        // accounts for draw.io's scale, not only the identity case a zoom of 1 could not tell apart
        // from a conversion that ignores scale entirely.
        //
        // Selected FIRST, while it is still on screen at the un-zoomed scale: draw.io's own zoomIn
        // action zooms around the canvas's current centre, not around a chosen device, and after
        // three steps the device can end up outside the browser's own window - measured, the first
        // draft of this test hung waiting for a hover that a mouse move to an off-screen point never
        // produced. ScrollSelectionIntoViewAsync (scrollCellToVisible, the same call the bridge's own
        // selectCell makes) is what brings it back before the hover.
        await editor.SelectDevicesAsync(PlantSeed.DraggedName);
        await editor.InvokeActionAsync("zoomIn");
        await editor.InvokeActionAsync("zoomIn");
        await editor.InvokeActionAsync("zoomIn");
        await editor.ScrollSelectionIntoViewAsync();

        // WaitForDeviceAsync FIRST, and not straight into HoverOverDeviceAsync: the scroll above is
        // its own gesture inside draw.io, and a bounding box read the instant after can land in the
        // middle of the browser's own repaint - MEASURED, an earlier run of this test threw "the
        // shape is in the DOM but has no box on screen" right here. WaitForDeviceAsync waits for
        // Playwright's own "visible" state first; HoverOverDeviceAsync's own box read (SettledBoxAsync)
        // additionally waits for two consecutive reads of the device's box to agree before this test
        // ever moves the mouse to it - the zoom above is exactly the kind of reflow that box read has
        // to outlast, and it already does (DrawioEditorPage.SettledBoxAsync).
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);

        // Fix round 2, item 6(a): the indicator is cleared and confirmed EMPTY before hovering again.
        // SelectDevicesAsync's click above DID move the mouse - onto the device, at the OLD scale,
        // before any zoomIn - and none of InvokeActionAsync/ScrollSelectionIntoViewAsync move it
        // again, so the indicator is still carrying the NON-empty reading of that click (measured in
        // fix round 2). A bare "wait for non-empty" below would pass on that stale, pre-zoom reading
        // without ever proving a NEW one was produced at the zoomed scale. Clearing here first is what
        // makes the "non-empty" wait that follows an actual proof of a fresh, post-zoom read.
        await editor.MoveMouseOffTheCanvasContainerAsync();
        await editor.WaitForPointerPositionAsync(
            text => text.Length == 0,
            "the pointer position was not cleared before the post-zoom hover, so the reading that follows could not be proven fresh");

        await editor.HoverOverDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForPointerPositionAsync(
            text => text.Length > 0,
            "the pointer position never showed anything after hovering the device once zoomed");

        var (zx, zy) = ParsePosition(await editor.PointerPositionTextAsync() ?? "");
        output.WriteLine($"Read with zoom: X={zx} Y={zy} (expected {ExpectedX}/{ExpectedY}, tolerance {Tolerance}).");
        Assert.InRange(zx, ExpectedX - Tolerance, ExpectedX + Tolerance);
        Assert.InRange(zy, ExpectedY - Tolerance, ExpectedY + Tolerance);
    }

    private static (double X, double Y) ParsePosition(string text)
    {
        var match = Regex.Match(text, @"X:\s*(-?[\d.]+)\s+Y:\s*(-?[\d.]+)");
        if (!match.Success)
            throw new InvalidOperationException(
                $"the indicator's text is not in the expected format \"X: ... Y: ...\": \"{text}\"");
        return (
            double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
            double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
    }
}
