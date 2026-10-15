using Xunit;
using Xunit.Abstractions;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// The question a properties panel living NEXT TO the iframe has to answer before it can show
/// anything: <b>which device has the operator selected inside draw.io?</b>
/// </summary>
/// <remarks>
/// <para>
/// <b>The embedding protocol does not say.</b> The nineteen JSON events draw.io posts to its parent
/// (<c>init</c>, <c>load</c>, <c>save</c>, <c>export</c>, <c>configure</c>, <c>merge</c>,
/// <c>prompt</c>, <c>template</c>, <c>draft</c>, <c>exit</c>, <c>resize</c>, <c>scrollWheel</c>,
/// <c>shortcut</c>, <c>textContent</c>, <c>openLink</c>, <c>getDiff</c>, <c>patch</c>,
/// <c>resetDiff</c>, <c>ready</c>, <c>remoteInvoke</c>) carry no selection. This test measures the
/// way round it, and the way round is NOT a trick: the iframe is served by our own host, so it is
/// same origin, and draw.io publishes its own <c>EditorUi</c> to whoever asks through the entry
/// point it opens for plugins - <c>window.Draw.loadPlugin(fn)</c>.
/// </para>
/// <para>
/// <b>Measured, 2026-09-17.</b> The parent reaches <c>ui.editor.graph</c>, subscribes to
/// <c>mxGraphSelectionModel</c>'s <c>CHANGE</c>, and gets the cell the operator clicked. The cell's
/// identifier is the plant item's guid, because <c>DrawioDocument</c> writes it as the
/// <c>&lt;object id&gt;</c> - so there is nothing to look up and nothing to match by name.
/// </para>
/// <para>
/// <b>Why it is a test and not a note.</b> Everything here belongs to draw.io, not to us: an
/// upgrade of the bundle that stopped offering <c>window.Draw</c>, or that stopped carrying our
/// identifier onto the cell, would take the panel's ground away. That has to be read here, in red,
/// rather than found out with the panel half written.
/// </para>
/// </remarks>
public sealed class DrawioSelectionReachTest(DrawioEditorFixture fixture, ITestOutputHelper output) : IClassFixture<DrawioEditorFixture>
{
    private const string SeedHolder = "browser-test-selection-reach";

    [Fact]
    public async Task ClickingADeviceInDrawioTellsTheParentPageWhichPlantItemItIs()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);

        // Subscribed while draw.io is still loading, which is the moment a panel would have to do it
        // too: Draw.loadPlugin holds the callback until the app is up, and calls it at once when it
        // already is. Measured: at the 'init' event of the embedding protocol it already answers.
        await editor.WatchSelectionAsync();

        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);

        // Nothing clicked yet: the subscription must not invent a selection.
        Assert.Empty(await editor.SelectedCellsAsync());

        await editor.SelectDevicesAsync(PlantSeed.DraggedName);
        var one = await editor.SelectedCellsAsync();
        var picked = Assert.Single(one);
        Assert.Equal(PlantSeed.DraggedId.ToString(), picked.Id);
        Assert.Equal(PlantSeed.DraggedName, picked.Label);
        Assert.Equal("device", picked.Wcs);

        // Two devices: the panel has to be able to tell "one device" from "several", and the order is
        // the order of the clicks.
        await editor.SelectDevicesAsync(PlantSeed.DraggedName, PlantSeed.UntouchedName);
        var two = await editor.SelectedCellsAsync();
        Assert.Equal(2, two.Count);
        Assert.Equal(
            [PlantSeed.DraggedId.ToString(), PlantSeed.UntouchedId.ToString()],
            two.Select(c => c.Id).ToArray());
        Assert.Equal<string?[]>(
            [PlantSeed.DraggedName, PlantSeed.UntouchedName],
            two.Select(c => c.Label).ToArray());
    }
}
