using Wcs.Api.Contracts.Dtos;
using Xunit;
using Xunit.Abstractions;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// The warehouse cells in draw.io's column, judged the way they will be used: whoever configures a
/// plant clicks a Storage device, <b>maps a door</b> and finds it on the plant; clicks an Automation
/// device, <b>connects a cell</b> and finds it there.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the test the batch is worth.</b> Nothing it goes through exists in bUnit: draw.io, its
/// selection model, the bridge, the Blazor circuit, three real HTTP reads against a real host - one
/// of which leaves the WCS and reaches a warehouse (<see cref="StubWarehouse"/>), because the cells
/// are not a fact the WCS holds - the two shared components rendered into a 320 px column, and two
/// writes that end up in the host's database.
/// </para>
/// <para>
/// <b>Reading it back is not decoration.</b> Every write is read back <b>from the host with the API
/// client</b>, not from the page: a column that declares "written" without having written is exactly
/// the defect this branch produces most often, and no reading of the screen would see it.
/// </para>
/// </remarks>
public sealed class DrawioCellsTest(DrawioCellsFixture fixture, ITestOutputHelper output) : IClassFixture<DrawioCellsFixture>
{
    private const string SeedHolder = "browser-test-cells";

    private static readonly Guid StorageId = new("a1b2c3d4-0000-4000-8000-00000000000a");
    private const string StorageName = "StorageDevice";

    private static readonly PlantItemGeometryDto StorageGeometry =
        new(StorageId, X: 980, Y: 160, Width: 120, Height: 90, Rotation: 0);

    /// <summary>
    /// The whole round trip on a Storage device: its dedicated window reports the current mapping,
    /// a mapped door reaches the plant, and the sixty properties remain in their own panel.
    /// </summary>
    [Fact]
    public async Task MappingADoorOnAStorageDeviceReachesThePlant()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);
        await PlantSeed.AddDeviceAsync(client, SeedHolder, StorageId, StorageName, DeviceTypes.Storage, StorageGeometry);

        // The starting point, measured and not assumed: on the plant this device has no door mapped.
        // Without this line the test could not tell a mapping that arrived from one already there.
        Assert.DoesNotContain(await client.GetDoorMappingsAsync(), mapping => mapping.ItemId == StorageId);

        await using var editor = await DrawioCellsPageAsync();
        await editor.WaitForDeviceAsync(StorageName);
        await editor.SelectDevicesAsync(StorageName);
        await editor.WaitForPropertyAsync("Name", StorageName);

        // The sixty properties stay in their dock: the cells no longer consume their vertical room.
        var groups = await editor.PropertyGroupCountAsync();
        Assert.True(groups > 2, $"The column shows {groups} property groups: the cells have taken their place.");

        await editor.OpenTheCellsAsync(StorageName);
        await editor.WaitForCellsSummaryAsync("none mapped yet");
        await editor.LookAtItAsync("celle-magazzino");

        // The generic window can normally shrink to 280x160. This workspace has two grids and fixed
        // actions, so its own minimum must keep rows and commands reachable without resizing again.
        await editor.PlaceWindowAsync("drawio-linked-storage-cells-window", 16, 180, 280, 160);
        var compact = await editor.CellsWindowUsabilityAsync();
        Assert.True(compact[0] >= 440, $"The cells window shrank to {compact[0]:0.#} px wide.");
        Assert.True(compact[1] >= 420, $"The cells window shrank to {compact[1]:0.#} px high.");
        Assert.True(compact[2] >= 70, $"A cells grid collapsed to {compact[2]:0.#} px high.");
        Assert.True(compact[3] <= 1, $"The cells actions run {compact[3]:0.#} px below the visible window content.");

        // The table's seven columns do not always fit in the resized window, and HOW they do not fit is the difference
        // between "reachable by scrolling" and "cut away". MEASURED from the browser, because it was
        // seen broken in a screenshot and no test had noticed.
        var reach = await editor.CellsTableReachAsync();
        Assert.True(reach.Overflow <= 1, $"The cells table runs {reach.Overflow:0.#} px past the column's right edge: the last columns are cut away.");
        Assert.Equal("auto", reach.OverflowX);
        Assert.True(reach.HasMoreToScroll, "The table has nothing to scroll to: either it all fits (and then this measurement says nothing any more) or it did not render.");

        await editor.TickDoorAsync("A", DrawioCellsFixture.StorageCellId);
        await editor.ConfirmDoorMappingsAsync();
        await editor.WaitForCellsWrittenAsync(StorageName);
        Assert.Null(await editor.CellsWriteErrorAsync());

        // The real proof: read back FROM THE HOST, not from the page.
        var onThePlant = (await client.GetDoorMappingsAsync()).Where(mapping => mapping.ItemId == StorageId).ToList();
        var written = Assert.Single(onThePlant);
        Assert.Equal(DrawioCellsFixture.StorageDoorA, written.DoorCode);
        Assert.Equal(DrawioCellsFixture.StorageCellId, written.CellId);
        Assert.Equal("A", written.Side);

        // And the line on screen followed the plant instead of staying on "none".
        await editor.WaitForCellsSummaryAsync("1 door mapped");

        // The WCS really did ask the warehouse for the list, and with no area filter, the way
        // FrmSTORAGE_ITEM_LINK does: had it asked filtered, a Storage device would see only part of
        // the cells and no assertion on the screen would notice.
        var asked = Assert.Single(fixture.Warehouse.RequestedPaths, path => path.Contains("automaticOnly=false"));
        Assert.DoesNotContain("areaType=", asked);
    }

    /// <summary>
    /// The whole round trip on an Automation device: the connected cell reaches both the door map
    /// and the configuration, which is the field the engine really reads.
    /// </summary>
    /// <remarks>
    /// Both halves are read back from the host, and they are two different failures: a door-map row
    /// without <c>LinkedCellId</c> is the silent mismatch <c>LinkBufferCellCommand</c> exists to
    /// prevent, and it would not show by looking at the panel alone.
    /// </remarks>
    [Fact]
    public async Task ConnectingACellOnAnAutomationDeviceReachesBothHalvesOfThePlant()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        var before = await client.GetItemConfigurationAsync(PlantSeed.DraggedId);
        Assert.NotNull(before);
        Assert.Equal(0m, before.LinkedCellId);

        await using var editor = await DrawioCellsPageAsync();
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.SelectDevicesAsync(PlantSeed.DraggedName);
        await editor.WaitForPropertyAsync("Name", PlantSeed.DraggedName);
        await editor.OpenTheCellsAsync(PlantSeed.DraggedName);
        await editor.WaitForCellsSummaryAsync("none connected");
        await editor.LookAtItAsync("celle-collegate");
        await editor.ChooseCellToLinkAsync(DrawioCellsFixture.AutomationCellId);
        await editor.ConfirmCellLinkAsync();
        await editor.WaitForCellsWrittenAsync(PlantSeed.DraggedName);
        Assert.Null(await editor.CellsWriteErrorAsync());

        var after = await client.GetItemConfigurationAsync(PlantSeed.DraggedId);
        Assert.NotNull(after);
        Assert.Equal(DrawioCellsFixture.AutomationCellId, after.LinkedCellId);

        var mapping = Assert.Single(await client.GetDoorMappingsAsync(), row => row.ItemId == PlantSeed.DraggedId);
        Assert.Equal(DrawioCellsFixture.AutomationDoorA, mapping.DoorCode);
        Assert.Equal(DrawioCellsFixture.AutomationCellId, mapping.CellId);

        // The window followed the plant: its summary names the cell, and the sixty fields carry
        // the same number - that is, the reread reached the properties panel too, which would
        // otherwise write the old zero back at the next confirmation.
        await editor.WaitForCellsSummaryAsync($"cell {DrawioCellsFixture.AutomationCellId} connected");
        await editor.WaitForPropertyNumberAsync(nameof(PlantItemConfigDto.LinkedCellId), DrawioCellsFixture.AutomationCellId);

        // And the question the WCS asked the warehouse for this kind of device is the other one:
        // filtered by area and automatic only, like FrmAUTOMATIC_ITEM_LINK.
        Assert.Contains(fixture.Warehouse.RequestedPaths, path => path.Contains("areaType=2") && path.Contains("automaticOnly=true"));
    }

    /// <summary>
    /// Opens the page waiting for the edit permit to be free: every test of this class seeds with
    /// the permit and releases it, but the PREVIOUS test's page gives it back when its Blazor circuit
    /// is torn down, which is not when the browser closes the tab - see
    /// <see cref="PlantSeed.WaitForTheEditPermitToBeFreeAsync"/>.
    /// </summary>
    private async Task<DrawioEditorPage> DrawioCellsPageAsync()
    {
        using var client = fixture.CreateClient();
        await PlantSeed.WaitForTheEditPermitToBeFreeAsync(client, SeedHolder + "-handover");
        return await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
    }
}
