using Xunit;
using Xunit.Abstractions;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// Final review, I4 - row 8 of the parity table measured on a device READ FROM THE PLANT, which is
/// every device an operator finds on the drawing when the page opens. <c>DrawioShapesPanelTest</c>
/// measures the same gestures on a device DROPPED in this session, whose cell carries its type
/// (<c>wcsType</c>) and whose copy is therefore created; a plant device's cell does not carry it
/// (<c>DrawioDocument.Build</c> writes none), and these two tests say what follows from that.
/// </summary>
/// <remarks>
/// They pin down the behaviour as it is, including the part that is a loss (the cut): if the
/// product changes, they go red and row 8 of the table has to be measured again.
/// </remarks>
public sealed class DrawioCopyCutOfAPlantDeviceTest(DrawioEditorFixture fixture, ITestOutputHelper output)
    : IClassFixture<DrawioEditorFixture>
{
    private const string SeedHolder = "browser-test-copy-cut-of-a-plant-device";

    /// <summary>
    /// Ctrl+C / Ctrl+V on a plant device: the copy is a shape marked as a device, NOT created (it does
    /// not say its type) and refused by name at the save; the plant keeps its two devices and its
    /// junction.
    /// </summary>
    [Fact]
    public async Task ACopyOfAPlantDeviceIsNotCreatedAndThePlantIsUnchanged()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder, withJunction: true);
        var before = await client.GetPlantGraphAsync();

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);

        await editor.SelectDevicesAsync(PlantSeed.DraggedName);
        await editor.ArmThePasteProbeAsync();
        await editor.CopyAsync();
        await editor.PasteAsync();
        await editor.WaitForMarkedCellsAsync(3);
        await editor.WaitForTheCreationReportAsync();

        var copy = Assert.Single(await editor.MarkedCellIdsAsync(), id => !Guid.TryParse(id, out _));
        output.WriteLine("creation report: " + await editor.CreationReportAsync());
        Assert.Contains(await editor.NotCreatedAsync(), line => line.Contains(copy, StringComparison.Ordinal) && line.Contains("wcsType", StringComparison.Ordinal));
        Assert.Equal(before.Items.Count, (await client.GetPlantGraphAsync()).Items.Count);

        await editor.SaveAsync();
        output.WriteLine("save report: " + await editor.ReportAsync());
        var afterSave = await client.GetPlantGraphAsync();

        Assert.Contains(await editor.RefusalsAsync(), line => line.Contains(copy, StringComparison.Ordinal) && line.Contains("does not know", StringComparison.Ordinal));
        Assert.Equal(before.Items.Select(item => item.ItemId).Order(), afterSave.Items.Select(item => item.ItemId).Order());
        Assert.Equal(before.Junctions.Count, afterSave.Junctions.Count);
    }

    /// <summary>
    /// Owner's decision of 27/9: a cut on a selection that holds a plant device does NOTHING, and the
    /// page says so - it used to lose the device at the next save (Ctrl+X, Ctrl+V, Save deleted it with
    /// its junction, measured in the final review). Ctrl+X here, draw.io's own keyboard road through
    /// its clipboard element (EditorUi.installNativeClipboardHandler / copyCells): the model, its undo
    /// history and the host are unchanged, the notice is on the page, and a save deletes nothing.
    /// </summary>
    [Fact]
    public async Task CtrlXOnAPlantDeviceCutsNothingAndTheSaveDeletesNothing()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder, withJunction: true);
        var before = await client.GetPlantGraphAsync();

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);
        await editor.WaitForTheDeviceMenuAsync(PlantSeed.DraggedId.ToString(), isDevice: true, running: false);
        output.WriteLine("where Cut is offered: " + await editor.WhereCutIsOfferedAsync());

        await editor.SelectDevicesAsync(PlantSeed.DraggedName);
        var modelBefore = await editor.ModelCellsAndUndoAsync();
        await editor.CutAsync();

        await editor.WaitForTextAsync("#drawio-cut-refused", "A plant device cannot be cut: drag it to move it, or delete it with Delete.");
        output.WriteLine("notice: " + await editor.TextOfAsync("#drawio-cut-refused"));
        Assert.Equal(modelBefore, await editor.ModelCellsAndUndoAsync());
        Assert.True(await editor.HasCellAsync(PlantSeed.DraggedId.ToString()));

        await editor.SaveAsync();
        output.WriteLine("save report: " + await editor.ReportAsync());
        Assert.DoesNotContain(await editor.WrittenAsync(), line => line.Contains("deletion", StringComparison.Ordinal));
        var afterSave = await client.GetPlantGraphAsync();
        Assert.Equal(before.Items.Select(item => item.ItemId).Order(), afterSave.Items.Select(item => item.ItemId).Order());
        Assert.Equal(before.Junctions.Select(j => j.JunctionId), afterSave.Junctions.Select(j => j.JunctionId));
    }

    /// <summary>The same refusal from draw.io's right-click menu, whose "Cut" is its 'cut' action.</summary>
    [Fact]
    public async Task CutFromTheRightClickMenuOnAPlantDeviceCutsNothing()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder, withJunction: true);
        var before = await client.GetPlantGraphAsync();

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForTheDeviceMenuAsync(PlantSeed.DraggedId.ToString(), isDevice: true, running: false);
        await editor.RightClickOnDeviceAsync(PlantSeed.DraggedName);
        var modelBefore = await editor.ModelCellsAndUndoAsync();

        await editor.ClickTheMenuEntryAsync("Cut");

        await editor.WaitForTextAsync("#drawio-cut-refused", "A plant device cannot be cut");
        Assert.Equal(modelBefore, await editor.ModelCellsAndUndoAsync());
        Assert.True(await editor.HasCellAsync(PlantSeed.DraggedId.ToString()));
        Assert.Equal(before.Items.Count, (await client.GetPlantGraphAsync()).Items.Count);
    }

    /// <summary>A shape that is not a device - a note of the free drawing - still cuts, with Ctrl+X, as draw.io does.</summary>
    [Fact]
    public async Task APlainShapeStillCuts()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);
        const string note = "note-cut";
        await PlantSeed.SeedNoteAsync(client, SeedHolder, note, "A note to cut");

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForTheDeviceMenuAsync(PlantSeed.DraggedId.ToString(), isDevice: true, running: false);
        // A right-click selects the note and gives the canvas the keyboard; the menu is closed again.
        await editor.RightClickOnCellAsync(note);
        await editor.CloseTheContextMenuAsync();
        Assert.Equal([note], await editor.SelectedCellIdsAsync());

        await editor.CutAsync();

        // The note is gone: that alone says the guard let the cut through, because a refused cut
        // returns before any cell is removed. No "no notice" check (confirmation review, M-D): the
        // notice travels to the page asynchronously, and reading its absence right away proves nothing.
        Assert.False(await editor.HasCellAsync(note));
    }

    /// <summary>
    /// Confirmation review, M-A: before the page has said which cells are devices (right after an
    /// attach), or for a device it has not said yet, the cell's own mark (wcs="device", which
    /// DrawioDocument writes on every device cell) is enough to refuse the cut.
    /// </summary>
    [Fact]
    public async Task ACellMarkedAsADeviceIsNotCutEvenBeforeThePageSaysWhichCellsAreDevices()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForTheDeviceMenuAsync(PlantSeed.DraggedId.ToString(), isDevice: true, running: false);
        await editor.ForgetWhichCellsAreDevicesAsync();
        await editor.WaitForTheDeviceMenuAsync(PlantSeed.DraggedId.ToString(), isDevice: false, running: false);

        await editor.SelectDevicesAsync(PlantSeed.DraggedName);
        var modelBefore = await editor.ModelCellsAndUndoAsync();
        await editor.CutAsync();

        await editor.WaitForTextAsync("#drawio-cut-refused", "A plant device cannot be cut");
        Assert.Equal(modelBefore, await editor.ModelCellsAndUndoAsync());
        Assert.True(await editor.HasCellAsync(PlantSeed.DraggedId.ToString()));
    }
}
