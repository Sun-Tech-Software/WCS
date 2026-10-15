using Xunit;
using Xunit.Abstractions;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// Compito 2, rows 58 and 59, against a real test host and a real draw.io: a cell of the device
/// list changed and confirmed ARRIVES ON THE PLANT - read back from the host, not from the page -
/// and a row of either list selects its device or its arrow on the drawing.
/// </summary>
/// <remarks>
/// What bUnit cannot measure is exactly this chain: the double-click that opens a cell, the write
/// through the real edit permit and the real route, and draw.io really selecting what the row names
/// (the mark on the row comes back only after draw.io has told the page over the circuit).
/// </remarks>
public sealed class DrawioListsBrowserTest(DrawioEditorFixture fixture, ITestOutputHelper output) : IClassFixture<DrawioEditorFixture>
{
    private const string SeedHolder = "browser-test-lists";

    [Fact]
    public async Task ACellOfTheDeviceListArrivesOnThePlantAndTheRowsSelectOnTheDrawing()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        var junction = await PlantSeed.ResetAsync(client, SeedHolder, withJunction: true)
            ?? throw new InvalidOperationException("The seed did not hand the junction back.");

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);

        // Row 58: one cell of one device, written through the list.
        var before = await client.GetItemConfigurationAsync(PlantSeed.UntouchedId)
            ?? throw new InvalidOperationException("The seeded device has no configuration on the host.");
        var wanted = before.MaxUnloadWeight + 4321;
        var cell = $"#drawio-device-list-table tr[data-item-id='{PlantSeed.UntouchedId}'] td[data-property='MaxUnloadWeight']";

        await editor.ClickAsync("#drawio-device-list-toggle");
        await editor.DoubleClickAsync(cell);
        await editor.FillAsync("#drawio-device-list-editor-value", wanted.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await editor.ClickAsync("#drawio-device-list-editor-write");
        await editor.WaitForTextAsync("#drawio-device-list-outcome", "Written:");

        var after = await client.GetItemConfigurationAsync(PlantSeed.UntouchedId);
        Assert.Equal(wanted, after?.MaxUnloadWeight);
        // Only that cell: the device's name - another column of the same row - is what it was.
        Assert.Equal(before.Name, after?.Name);
        await editor.WaitForTextAsync(cell, wanted.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // Choosing a row selects that device on the drawing.
        await editor.ClickAsync($"#drawio-device-list-table tr[data-item-id='{PlantSeed.DraggedId}'] td[data-property='Name']");
        await editor.WaitForSelectionLineAsync($"Selected: {PlantSeed.DraggedName}");
        await editor.WaitForAttributeAsync($"#drawio-device-list-table tr[data-item-id='{PlantSeed.DraggedId}']", "aria-selected", "true");

        // Row 59: choosing a junction's row selects its arrow on the drawing.
        await editor.ClickAsync("#drawio-junction-list-toggle");
        var row = $"#drawio-junction-list-table tr[data-junction-id='{junction.JunctionId}']";
        await editor.ClickAsync($"{row} td[data-column='Source']");
        await editor.WaitForAttributeAsync(row, "aria-selected", "true");
        Assert.Equal([junction.JunctionId.ToString()], await editor.SelectedCellIdsAsync());
    }
}
