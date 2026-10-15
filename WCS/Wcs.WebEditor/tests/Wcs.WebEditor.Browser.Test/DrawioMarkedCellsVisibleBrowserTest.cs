using Wcs.Api.Contracts.Dtos;
using Xunit;
using Xunit.Abstractions;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// Row 37 of the parity inventory against a real draw.io: the plant carries no visibility flag at
/// all - <c>DrawioDocument.Build</c> draws every device it places and every junction whose two ends
/// are drawn (<c>DrawioDocument.WhyNotDrawn</c>), and neither ever writes <c>visible="0"</c> - so on
/// a plant with junctions and a warehouse, every marked cell comes up visible: in draw.io's own
/// model AND on screen. This is the one thing bUnit cannot measure, because it runs no JavaScript
/// and mxGraph's rendering only exists in a real browser.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both questions are asked, not just one.</b> <c>MarkedCellVisibilityAsync</c> reads a cell's
/// OWN model flag (<c>model.isVisible</c>) and whether it is actually RENDERED on screen, and the
/// two are not the same question: mxGraph's rendering walks the parent chain, so a cell can answer
/// <c>modelVisible=true</c> and still be off the screen because an ANCESTOR - the layer it hangs
/// from - is hidden. A test that asked only the model's own flag would call a cell visible while
/// the operator sees nothing; see the class remark on <see cref="DrawioEditorPage.MarkedCellVisibilityAsync"/>.
/// </para>
/// <para>
/// <b>This test does not cover draw.io's own Layers window</b> (Ctrl+Shift+L, reachable even with
/// <c>ui=min</c> - the same way the Birdview of row 34 is). Measured separately, by hand, on
/// 2026-09-25: hiding the default layer through it took every marked cell off the screen at once
/// while each one's own <c>model.isVisible</c> stayed true, and nothing on this page noticed or
/// offered a way back short of the same Ctrl+Shift+L. That was a capability of draw.io's, not
/// something this branch's data model could produce or prevent - which is why the parity inventory's
/// row 37 recorded it as an open question for the owner rather than as closed.
/// <b>CLOSED the same day</b>: the owner's decision was to close the window itself
/// (<c>drawio-bridge.js</c>'s <c>closeTheLayersWindow</c>, installed alongside <c>keepOnePageOnly</c>),
/// not to teach the page about layer visibility. See <c>DrawioLayersWindowClosedTest</c> for the
/// browser measurement against the closed window (Ctrl+Shift+L no longer opens it, and every marked
/// cell stays rendered) and the inventory's "Cose che non tornano", voce e), for the decision itself.
/// </para>
/// </remarks>
public sealed class DrawioMarkedCellsVisibleBrowserTest(DrawioEditorFixture fixture, ITestOutputHelper output)
    : IClassFixture<DrawioEditorFixture>
{
    private const string SeedHolder = "browser-test-marked-cells-visible";
    private const string WarehouseName = "Row37Warehouse";

    private static readonly Guid WarehouseId = new("a1b2c3d4-0000-4000-8000-0000000000a1");

    private static readonly PlantItemGeometryDto WarehouseGeometry =
        new(WarehouseId, X: 900, Y: 500, Width: 120, Height: 90, Rotation: 0);

    [Fact]
    public async Task EveryMarkedCellIsVisibleOnAPlantWithJunctionsAndAWarehouse()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        var junction = await PlantSeed.ResetAsync(client, SeedHolder, withJunction: true);
        Assert.NotNull(junction);
        await PlantSeed.AddDeviceAsync(client, SeedHolder, WarehouseId, WarehouseName, DeviceTypes.Storage, WarehouseGeometry);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);
        await editor.WaitForDeviceAsync(WarehouseName);

        var cells = await editor.MarkedCellVisibilityAsync();
        output.WriteLine("Marked cells: " + string.Join(", ", cells.Select(c => c.ToString())));

        // Three devices and one junction, no fewer: a check that quietly found zero junctions would
        // pass this assertion for the wrong reason (see MEMORY: "zero non e' una prova").
        Assert.Equal(3, cells.Count(c => c.Mark == "device"));
        Assert.Equal(1, cells.Count(c => c.Mark == "junction"));

        foreach (var cell in cells)
        {
            Assert.True(cell.ModelVisible, $"{cell} should be visible in draw.io's own model");
            Assert.True(cell.Rendered, $"{cell} should be rendered on screen");
        }
    }
}
