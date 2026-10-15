using Xunit;
using Xunit.Abstractions;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// Row 37 of the parity inventory, fix round 2 (widened fix round 3): the DOWNSTREAM guard
/// (<c>drawio-bridge.js</c>'s <c>guardMarkedCellsVisible</c>), the primary defence behind the two
/// closed windows (<c>DrawioLayersWindowClosedTest</c>, <c>DrawioTagsWindowClosedTest</c>) rather
/// than a third one. Closing routes to hiding a cell one at a time turned out to be whack-a-mole - a
/// THIRD route, Edit Link's custom actions, reaches the very same two levers (a cell's own model
/// visibility, and <c>hiddenTags</c>) without opening either window at all. This suite does not go
/// through Edit Link itself (that would only prove ONE more route closed, the thing the owner asked
/// not to keep doing): it drives the two levers DIRECTLY, the way ANY route - closed, or not yet
/// found - would have to, and measures that a marked cell survives them regardless.
/// </summary>
/// <remarks>
/// <para>
/// Tests (a)-(d) are fix round 2's own four. (d) is the one that keeps (a)-(c) honest: a guard that
/// made EVERY cell visible unconditionally would pass them for the wrong reason (MEMORY: "zero non
/// e' una prova" - a check that cannot fail proves nothing). (d) hides an UNMARKED cell - the free
/// decoration this Editor's own save leaves entirely to draw.io - and expects it to STAY hidden.
/// </para>
/// <para>
/// <b>Fix round 3 found (a)-(d) were not enough.</b> <c>mxGraphView.prototype.validateCell</c> ANDs a
/// cell's own answer with what its PARENT already answered, and JavaScript's <c>&amp;&amp;</c> SHORT-CIRCUITS:
/// once a parent has answered "hidden", the child's own <c>isCellVisible</c> is never even CALLED.
/// Reproduced live: a device grouped with another (Ctrl+G, an UNMARKED cell draw.io itself creates
/// as their new parent) vanished when that GROUP was hidden or tag-hidden, because the guard was
/// never asked about the device - only about the group, which round 2's version did not know to
/// answer for (it only special-cased the one default layer). Widened to answer true for any cell
/// with a marked DESCENDANT anywhere under it, not only the default layer - tests (e)-(g) measure
/// this against a group specifically, the shape that actually broke; (h) is a one-line check on the
/// second belt.
/// </para>
/// <para>
/// <b>Fix round 4 found the WIDENING itself was not enough either.</b> Recomputing the ancestor set
/// on every model edit is right, but recomputing is not the same as REDRAWING: mxGraph's own change
/// listener (installed inside the graph's constructor, before this bridge ever attaches) runs BEFORE
/// this bridge's own, so it validates the view with the STALE set every time - reproduced live with
/// group / hide / ungroup / undo, where the devices stayed off-screen after the undo until something
/// else forced a repaint. (i) is that exact sequence, with no manual refresh anywhere in the test:
/// the bridge's own listener has to notice the set changed and force the redraw itself. (ii) is the
/// same defect from a single atomic edit instead of a sequence of gestures - one edit that both
/// creates a hidden group and moves a device into it.
/// </para>
/// </remarks>
public sealed class DrawioMarkedCellsVisibleGuardBrowserTest(DrawioEditorFixture fixture, ITestOutputHelper output) : IClassFixture<DrawioEditorFixture>
{
    private const string SeedHolder = "browser-test-marked-cells-visible-guard";

    private void DeclareBinary() => output.WriteLine(fixture.Host.Declaration);

    /// <summary>
    /// (a) A marked device hidden through <c>model.setVisible</c> DIRECTLY - the lever
    /// <c>{"hide":{"cells":[...]}}</c> (<c>setCellsVisible</c>, ultimately the same model call) and
    /// the (now closed) Layers window's own eye icon both pull - stays RENDERED, because
    /// <c>graph.isCellVisible</c> answers true for a marked cell no matter what the model itself
    /// says.
    /// </summary>
    [Fact]
    public async Task AMarkedCellHiddenThroughModelVisibilityStaysRendered()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        // The drawn cell's id IS the plant item's guid (DrawioDocument.Build writes it as the
        // <object id>) - measured against the marked-cell output every other test in this batch
        // prints (e.g. "a1b2c3d4-0000-4000-8000-000000000001 (device)").
        var deviceCellId = PlantSeed.DraggedId.ToString();
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);

        Assert.True(await editor.CellRenderedAsync(deviceCellId), "The device should start out rendered: this test would not be measuring the guard otherwise.");

        await editor.SetCellModelVisibleAsync(deviceCellId, false);

        Assert.True(await editor.CellRenderedAsync(deviceCellId),
            "A marked cell hidden through model.setVisible directly stopped being rendered: the guard did not hold against the model's own flag.");
    }

    /// <summary>
    /// (b) A marked device carrying a tag, with that tag hidden through <c>graph.setHiddenTags</c>
    /// DIRECTLY - the lever Edit Link's <c>{"tags":{"hidden":[...]}}</c> pulls, and the one the
    /// (now closed) Tags window's eye icon used to pull - stays RENDERED, because
    /// <c>Graph.prototype.isCellVisible</c>'s own <c>isAllTagsHidden</c> half is short-circuited by
    /// the guard before it is ever asked.
    /// </summary>
    [Fact]
    public async Task AMarkedCellWithAHiddenTagStaysRendered()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        var deviceCellId = PlantSeed.DraggedId.ToString();
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);

        await editor.AddTagToCellAsync(deviceCellId, "row37");
        Assert.True(await editor.CellRenderedAsync(deviceCellId), "The device should still be rendered right after tagging it: this test would not be measuring the hidden tag otherwise.");

        await editor.SetHiddenTagsAsync("row37");

        Assert.True(await editor.CellRenderedAsync(deviceCellId),
            "A marked cell whose only tag was hidden through graph.setHiddenTags directly stopped being rendered: the guard did not hold against a hidden tag.");
    }

    /// <summary>
    /// (c) The default layer itself (<c>mxCell id="1"</c>, <c>DrawioDocument.DefaultLayerId</c>) set
    /// invisible through <c>model.setVisible</c> DIRECTLY - the very call the parity inventory's own
    /// measurements make, and the one the (now closed) Layers window's eye icon on the layer row
    /// makes - and every marked cell stays RENDERED, because <c>mxGraphView.prototype.validateCell</c>
    /// ANDs a cell's own answer with its PARENT's, and the guard answers "visible" for the layer
    /// itself too - not only for what hangs from it.
    /// </summary>
    [Fact]
    public async Task TheDefaultLayerSetInvisibleLeavesEveryMarkedCellRendered()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        var junction = await PlantSeed.ResetAsync(client, SeedHolder, withJunction: true);
        Assert.NotNull(junction);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);

        await editor.SetCellModelVisibleAsync("1", false);

        var cells = await editor.MarkedCellVisibilityAsync();
        output.WriteLine("Marked cells after hiding the default layer: " + string.Join(", ", cells.Select(c => c.ToString())));

        Assert.Equal(2, cells.Count(c => c.Mark == "device"));
        Assert.Equal(1, cells.Count(c => c.Mark == "junction"));
        foreach (var cell in cells)
            Assert.True(cell.Rendered, $"{cell} should still be rendered with the default layer hidden.");
    }

    /// <summary>
    /// (d) The one that keeps (a)-(c) honest: a FREE NOTE - decoration this Editor's own save leaves
    /// entirely to draw.io's own rules, carrying no <c>wcs</c> mark - hidden through
    /// <c>model.setVisible</c> DIRECTLY stays HIDDEN. The guard reaches exactly two things (the
    /// default layer, and marked cells); a note the operator hid on purpose is that operator's own
    /// choice to make and undo, not this batch's to overrule.
    /// </summary>
    [Fact]
    public async Task AnUnmarkedDecorationCellHiddenThroughModelVisibilityStaysHidden()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);

        var noteCellId = await editor.PutAFreeNoteOnTheCanvasAsync("a note the operator drew by hand");
        Assert.True(await editor.CellRenderedAsync(noteCellId), "The note should start out rendered: this test would not be measuring anything otherwise.");

        await editor.SetCellModelVisibleAsync(noteCellId, false);

        Assert.False(await editor.CellRenderedAsync(noteCellId),
            "An unmarked decoration cell hidden through model.setVisible stayed rendered: the guard reached further than the default layer and marked cells, which it must not.");
    }

    /// <summary>
    /// (e) THE SHAPE THAT ACTUALLY BROKE round 2's guard: two marked devices grouped with Ctrl+G -
    /// draw.io's own gesture, which creates a new UNMARKED cell as their parent - with the GROUP
    /// (not the device) hidden through <c>model.setVisible</c> directly. Before the widening this
    /// vanished the devices: <c>validateCell</c> asked the group's own visibility, got "hidden", and
    /// never asked the device's at all. After it, <c>visibilityAncestorIds</c> answers "visible" for
    /// the group too, because it has a marked descendant.
    /// </summary>
    [Fact]
    public async Task ADeviceInAHiddenUnmarkedGroupStaysRendered()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        var draggedId = PlantSeed.DraggedId.ToString();
        var untouchedId = PlantSeed.UntouchedId.ToString();
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);

        await editor.SelectCellsAsync(draggedId, untouchedId);
        await editor.GroupSelectionAsync();
        var groupId = await editor.CellParentAsync(draggedId);
        Assert.NotNull(groupId);
        Assert.NotEqual("1", groupId); // a real group, not left directly on the default layer

        Assert.True(await editor.CellRenderedAsync(draggedId), "The device should still be rendered right after grouping it: this test would not be measuring the hidden group otherwise.");

        await editor.SetCellModelVisibleAsync(groupId!, false);

        Assert.True(await editor.CellRenderedAsync(draggedId),
            "A device inside a hidden UNMARKED group stopped being rendered: the guard only reached the device itself, not its container - the exact gap fix round 3 closed.");
    }

    /// <summary>
    /// (f) The same group, hidden through a TAG on the group instead of <c>model.setVisible</c> -
    /// the same lever Edit Link's <c>{"tags":{"hidden":[...]}}</c> pulls, now aimed at a container
    /// instead of the cell itself.
    /// </summary>
    [Fact]
    public async Task ADeviceInAGroupWithAHiddenTagOnTheGroupStaysRendered()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        var draggedId = PlantSeed.DraggedId.ToString();
        var untouchedId = PlantSeed.UntouchedId.ToString();
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);

        await editor.SelectCellsAsync(draggedId, untouchedId);
        await editor.GroupSelectionAsync();
        var groupId = await editor.CellParentAsync(draggedId);
        Assert.NotNull(groupId);

        await editor.AddTagToCellAsync(groupId!, "row37group");
        Assert.True(await editor.CellRenderedAsync(draggedId), "The device should still be rendered right after tagging its group: this test would not be measuring the hidden tag otherwise.");

        await editor.SetHiddenTagsAsync("row37group");

        Assert.True(await editor.CellRenderedAsync(draggedId),
            "A device inside a group whose OWN tag was hidden stopped being rendered: the guard did not hold against a hidden tag on a container.");
    }

    /// <summary>
    /// (g) The one that keeps (e) and (f) honest, the same way (d) keeps (a)-(c) honest: two
    /// UNMARKED notes grouped together (no plant cell anywhere in that group), with the group hidden
    /// through <c>model.setVisible</c>. The notes must STAY hidden - a guard that answered "visible"
    /// for every group, marked descendant or not, would pass (e) and (f) for the wrong reason.
    /// </summary>
    [Fact]
    public async Task AnUnmarkedNoteInAHiddenUnmarkedGroupWithNoPlantCellsStaysHidden()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);

        var note1 = await editor.PutAFreeNoteOnTheCanvasAsync("a note, one of two");
        var note2 = await editor.PutAFreeNoteOnTheCanvasAsync("a note, the other of two");

        await editor.SelectCellsAsync(note1, note2);
        await editor.GroupSelectionAsync();
        var groupId = await editor.CellParentAsync(note1);
        Assert.NotNull(groupId);
        Assert.Equal(groupId, await editor.CellParentAsync(note2));

        Assert.True(await editor.CellRenderedAsync(note1), "The note should still be rendered right after grouping it: this test would not be measuring the hidden group otherwise.");

        await editor.SetCellModelVisibleAsync(groupId!, false);

        Assert.False(await editor.CellRenderedAsync(note1),
            "A note inside a hidden group with NO plant cells stayed rendered: the guard reached a group that has no reason to be protected, which it must not.");
    }

    /// <summary>
    /// (h) The second belt's own one-line test: calls <c>graph.customLinkClicked</c> DIRECTLY (not by
    /// clicking a rendered link's on-canvas hint - there is none here, since no cell in this test
    /// carries an actual "data:action/json" link; see
    /// <see cref="DrawioEditorPage.InvokeHideCustomLinkAsync"/>) with a "hide" custom-action link
    /// aimed at a marked cell. It does nothing and leaves <c>model.isVisible</c> true -
    /// <c>ignoreCustomActionLinks</c> made <c>handleCustomLink</c> a no-op, so
    /// <c>Graph.prototype.executeCustomActions</c>' own <c>setCellsVisible</c> call is never reached.
    /// </summary>
    [Fact]
    public async Task ACustomActionHideLinkOnAMarkedCellDoesNothing()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        var deviceCellId = PlantSeed.DraggedId.ToString();
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);

        var stillVisible = await editor.InvokeHideCustomLinkAsync(deviceCellId);

        Assert.True(stillVisible,
            "A \"hide\" custom-action link clicked on a marked cell changed its model.isVisible flag: the second belt (ignoreCustomActionLinks) did not hold.");
    }

    /// <summary>
    /// (i) Fix round 4, THE SEQUENCE THAT ACTUALLY BROKE: group two devices, hide the group, ungroup
    /// (the group is removed, devices reparented back to the default layer), undo the ungroup (Ctrl+Z
    /// restores the group - still hidden - and reparents the devices back into it). With NO manual
    /// refresh anywhere in this test: the devices must be rendered right after the undo, because the
    /// bridge's own CHANGE listener must have redrawn the view itself once it noticed the ancestor set
    /// had changed back. Before the fix, mxGraph's OWN change listener (registered first, inside the
    /// graph's constructor) had already validated the view with the STALE set by the time this
    /// bridge's listener got to recompute it - the recompute happened, but nothing repainted with it.
    /// </summary>
    [Fact]
    public async Task GroupHideUngroupThenUndoLeavesDevicesRenderedWithNoManualRefresh()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        var draggedId = PlantSeed.DraggedId.ToString();
        var untouchedId = PlantSeed.UntouchedId.ToString();
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);

        await editor.SelectCellsAsync(draggedId, untouchedId);
        await editor.GroupSelectionAsync();
        var groupId = await editor.CellParentAsync(draggedId);
        Assert.NotNull(groupId);

        await editor.SetCellModelVisibleAsync(groupId!, false);
        Assert.True(await editor.CellRenderedAsync(draggedId), "The device should still be rendered right after hiding the group: this test would not be measuring the sequence below otherwise.");

        await editor.SelectCellsAsync(groupId!);
        await editor.UngroupSelectionAsync();
        var parentAfterUngroup = await editor.CellParentAsync(draggedId);
        Assert.NotEqual(groupId, parentAfterUngroup); // the group is gone, the device is back on the default layer

        await editor.UndoAsync();

        Assert.True(await editor.CellRenderedAsync(draggedId),
            "After group / hide / ungroup / undo, with no manual refresh, the device was not rendered: the bridge's own change listener did not redraw the view once the ancestor set changed back.");
        Assert.True(await editor.CellRenderedAsync(untouchedId),
            "After group / hide / ungroup / undo, with no manual refresh, the other device was not rendered either.");
    }

    /// <summary>
    /// (ii) Fix round 4: ONE atomic edit that both creates a hidden group AND moves a device into it
    /// (<see cref="DrawioEditorPage.GroupAndHideInOneEditAsync"/> - <c>graph.groupCells</c> wrapped in
    /// an outer <c>beginUpdate/endUpdate</c> around <c>model.setVisible(group, false)</c>, so only ONE
    /// <c>mxEvent.CHANGE</c> fires). With NO manual refresh: the device must be rendered right after,
    /// because the redraw has to happen inside the SAME change handling this one edit triggers - there
    /// is no second edit here for a later CHANGE to catch up on.
    /// </summary>
    [Fact]
    public async Task OneEditThatAddsAHiddenGroupContainingADeviceLeavesItRenderedWithNoManualRefresh()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        var draggedId = PlantSeed.DraggedId.ToString();
        var untouchedId = PlantSeed.UntouchedId.ToString();
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);

        // Two cells, not one: graph.groupCells is a no-op for a single cell (see the helper's own
        // remark) - grouping both seeded devices together is what makes this a real group.
        var groupId = await editor.GroupAndHideInOneEditAsync(draggedId, untouchedId);
        Assert.Equal(groupId, await editor.CellParentAsync(draggedId));
        Assert.Equal(groupId, await editor.CellParentAsync(untouchedId));

        Assert.True(await editor.CellRenderedAsync(draggedId),
            "A single edit that both created a hidden group and moved devices into it left a device unrendered: the redraw did not happen within the same change the guard's ancestor set was updated for.");
        Assert.True(await editor.CellRenderedAsync(untouchedId),
            "A single edit that both created a hidden group and moved devices into it left the other device unrendered either.");
    }
}
