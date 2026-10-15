using Xunit;
using Xunit.Abstractions;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// draw.io reloaded inside the SAME <c>&lt;iframe&gt;</c> element is a new draw.io, and the bridge
/// has to follow it there: selection, additions, <c>liveTarget()</c> and <c>currentDrawing()</c> all
/// hang off the one subscription <c>watchSelection</c> makes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Measured, 2026-09-24, and it overturned what the bridge assumed.</b> After
/// <c>contentWindow.location.reload()</c> on the same element, draw.io builds a new <c>EditorUi</c>
/// and a new graph - but <c>iframe.contentWindow</c> is the SAME WindowProxy object it was before.
/// The bridge used to key its subscription on that object, took the new draw.io's <c>init</c> for
/// the old one's, and stayed on the graph of the document that had gone: the page kept naming
/// whatever was selected before the reload, a click in the new drawing reached nobody, and
/// <c>drawio-live.js</c> kept drawing on a torn-down graph. The document is what a reload renews,
/// so that is what the subscription is keyed on now.
/// </para>
/// <para>
/// <b>Nothing in the product reloads the iframe alone today</b> - <c>attach</c> sets its
/// <c>src</c> once per circuit - which is exactly why this is a test: the day a retry path or a
/// draw.io upgrade does it, the panels must not go quietly deaf.
/// </para>
/// </remarks>
public sealed class DrawioSameFrameReloadTest(DrawioEditorFixture fixture, ITestOutputHelper output) : IClassFixture<DrawioEditorFixture>
{
    private const string SeedHolder = "browser-test-same-frame-reload";

    private const string NothingSelected = "Nothing selected.";

    [Fact]
    public async Task ReloadingDrawioInTheSameFrameMovesTheBridgeOntoTheNewDrawio()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);

        // Something selected BEFORE the reload, so that a line which never hears from the new
        // draw.io stays visibly stale instead of looking like a correct "nothing selected".
        await editor.SelectDevicesAsync(PlantSeed.DraggedName);
        await editor.WaitForSelectionLineAsync($"Selected: {PlantSeed.DraggedName}");

        await editor.ReloadDrawioInTheSameFrameAsync();
        // The page seeds the new draw.io from its 'init', like the first one: the plant is drawn again.
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);

        var held = await editor.WhatTheBridgeHoldsAfterTheReloadAsync();
        output.WriteLine(held.ToString());

        // The reload is the one this test means: same element, same WindowProxy, new document, and
        // a draw.io that really started over. Without these the assertions below would prove nothing.
        Assert.True(held.SameFrame, $"the iframe element was replaced, which is not the reload under test: {held}");
        Assert.True(held.SameWindow, $"the WindowProxy changed, which is not the reload under test: {held}");
        Assert.True(held.NewDocument, $"the iframe still holds the document from before the reload: {held}");
        Assert.True(held.DrawioHasANewGraph, $"draw.io did not start over with a new graph: {held}");

        // The bridge followed.
        Assert.False(held.BridgeStillHoldsTheOldGraph, $"the bridge is still on the graph of the reloaded-away document: {held}");
        Assert.True(held.BridgeHoldsTheNewGraph, $"the bridge is not on the graph draw.io hands out now: {held}");

        // And the page hears the new draw.io: first what it has selected - nothing, as it was just
        // born - then a real click in it.
        await editor.WaitForSelectionLineAsync(NothingSelected);
        await editor.SelectDevicesAsync(PlantSeed.UntouchedName);
        await editor.WaitForSelectionLineAsync($"Selected: {PlantSeed.UntouchedName}");
    }
}
