using Wcs.Api.Contracts.Dtos;
using Xunit;
using Xunit.Abstractions;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// Compito 4, row 62, against a real test host: a list of PLC connections changed in the page's grid
/// - a row changed, a row removed, a row added - and saved through the question, ARRIVES ON THE HOST,
/// read back with the client and not from the page. The host's connections are put back as they were
/// afterwards, whatever happened.
/// </summary>
/// <remarks>
/// What bUnit cannot measure is this chain: typing into the real boxes, the real edit permit, the real
/// <c>PUT /plc/connections</c> and <c>PlcSettingsFacade.ReplaceAsync</c> writing the host's database.
/// </remarks>
public sealed class DrawioPlcConnectionsBrowserTest(DrawioEditorFixture fixture, ITestOutputHelper output) : IClassFixture<DrawioEditorFixture>
{
    private const string SeedHolder = "browser-test-plc-connections";

    private const string Rows = "#drawio-plc-table tbody tr";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlcChromeAndLongConfirmationRemainReachableAtTheAllowedMinimum(bool minimum)
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);
        var original = await client.GetPlcConnectionsAsync();
        try
        {
            await PlantSeed.ReplacePlcConnectionsAsync(client, SeedHolder,
                Enumerable.Range(1, 200).Select(id => new PlcConnectionDto(90000000000000L + id,
                    $"Line {id}", "10.1.1.1", 2011, "10.1.1.2", 2012)).ToArray());
            await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
            await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
            await editor.OpenWindowAsync("drawio-plc-connections");
            await editor.WaitForTextAsync("#drawio-plc-page-summary", "1–25 of 200");
            // Real input events on all eight pages create a 200-Id confirmation without
            // replacing the component state or staging a synthetic message in the DOM.
            for (var page = 0; page < 8; page++)
            {
                for (var row = 1; row <= 25; row++)
                    await editor.FillAsync($"{Rows}:nth-child({row}) input[data-field='Description']", $"Changed {page * 25 + row}");
                if (page < 7)
                {
                    await editor.ClickAsync("#drawio-plc-next");
                    await editor.WaitForTextAsync("#drawio-plc-page-summary", $"{(page + 1) * 25 + 1}–{(page + 2) * 25} of 200");
                }
            }
            if (minimum)
                await editor.DragAsync("#drawio-plc-connections .drawio-window-grip", -2000, -2000);
            var before = await PlcLayoutAsync(editor);
            Assert.True(before.ToolsInside, "Toolbar is clipped at the permitted minimum.");
            Assert.True(before.PagerInside, "Pager is clipped at the permitted minimum.");
            Assert.True(before.GridHeight >= 60, $"Only {before.GridHeight}px remain for the grid.");
            output.WriteLine($"Measured PLC window: {before.Width}x{before.Height}; grid {before.GridHeight}px.");
            await PlantSeed.ReplacePlcConnectionsAsync(client, SeedHolder,
                Enumerable.Range(1, 200).Select(id => new PlcConnectionDto(90000000000000L + id,
                    $"Changed elsewhere {id}", "10.1.1.1", 2011, "10.1.1.2", 2012)).ToArray());
            await editor.ClickAsync("#drawio-plc-reread");
            await editor.WaitForTextAsync("#drawio-plc-conflict", "200 modified");
            var conflict = await PlcLayoutAsync(editor);
            Assert.True(conflict.ToolsInside);
            Assert.True(conflict.PagerInside);
            Assert.True(conflict.GridHeight >= 60);
            await editor.ScrollIntoViewAsync("#drawio-plc-keep-mine");
            Assert.True(await editor.IsInsideRegionAsync("#drawio-plc-keep-mine", "#drawio-plc-connections"));
            await editor.ScrollIntoViewAsync("#drawio-plc-take-host");
            Assert.True(await editor.IsInsideRegionAsync("#drawio-plc-take-host", "#drawio-plc-connections"));
            await editor.ClickAsync("#drawio-plc-keep-mine");
            await editor.ClickAsync("#drawio-plc-save");
            await editor.WaitForTextAsync("#drawio-plc-confirmation", "200 modified");
            var confirmation = await PlcLayoutAsync(editor);
            Assert.True(confirmation.ToolsInside, "Toolbar escaped during the long confirmation.");
            Assert.True(confirmation.PagerInside, "Pager escaped during the long confirmation.");
            Assert.True(confirmation.GridHeight >= 60);
            Assert.True(await editor.IsInsideRegionAsync("#drawio-plc-confirmation", "#drawio-plc-connections")
                || await editor.IsInsideRegionAsync(".drawio-plc-messages", "#drawio-plc-connections"));
            await editor.ScrollIntoViewAsync("#drawio-plc-confirm");
            Assert.True(await editor.IsInsideRegionAsync("#drawio-plc-confirm", "#drawio-plc-connections"));
            await editor.ScrollIntoViewAsync("#drawio-plc-cancel");
            Assert.True(await editor.IsInsideRegionAsync("#drawio-plc-cancel", "#drawio-plc-connections"));
            Assert.True(await editor.IsInsideRegionAsync("#drawio-plc-cancel", ".drawio-plc-messages"));
            Assert.True((await PlcLayoutAsync(editor)).PagerInside);
            await editor.ClickAsync("#drawio-plc-cancel");
            Assert.Equal("Changed 176", await editor.InputValueOfAsync($"{Rows}:first-child input[data-field='Description']"));
        }
        finally
        {
            await PlantSeed.ReplacePlcConnectionsAsync(client, SeedHolder, original);
        }
    }

    [Fact]
    public async Task AddingTheTwentyFifthRowScrollsItsIdIntoViewAndFocusesIt()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);
        var original = await client.GetPlcConnectionsAsync();
        try
        {
            await PlantSeed.ReplacePlcConnectionsAsync(client, SeedHolder,
                Enumerable.Range(901, 24).Select(id => new PlcConnectionDto(id,
                    $"Line {id}", "10.1.1.1", 2011, "10.1.1.2", 2012)).ToArray());
            await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
            await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
            await editor.OpenWindowAsync("drawio-plc-connections");
            await editor.WaitForTextAsync("#drawio-plc-page-summary", "1–24 of 24");
            await editor.FillAsync($"{Rows}:first-child input[data-field='Description']", "Pending edit");
            await editor.ClickAsync("#drawio-plc-add");
            await editor.WaitForTextAsync("#drawio-plc-page-summary", "1–25 of 25");
            const string id = "#drawio-plc-table tbody tr:last-child input[data-field='Id']";
            // Measure before Fill/Click can scroll the last row on the test's behalf.
            await editor.WaitForFocusedPlcIdAsync();
            Assert.True(await editor.IsInsideRegionAsync(id, ".drawio-plc-connections .drawio-list-scroll"),
                "The new row remained below the grid viewport.");
            Assert.True(await editor.IsInsideRegionAsync(id, ".drawio-plc-connections .drawio-list-scroll", focused: true));
            await editor.FillAsync(id, "925");
            Assert.Equal("925", await editor.InputValueOfAsync(id));
            await editor.FillAsync("#drawio-plc-filter", "Pending edit");
            await editor.WaitForTextAsync("#drawio-plc-page-summary", "1 of 1");
            Assert.Equal("Pending edit", await editor.InputValueOfAsync($"{Rows}:first-child input[data-field='Description']"));
            await editor.FillAsync("#drawio-plc-filter", "");
            await editor.WaitForTextAsync("#drawio-plc-count", "25 shown · 25 total · 25 active");
        }
        finally
        {
            await PlantSeed.ReplacePlcConnectionsAsync(client, SeedHolder, original);
        }
    }

    [Theory]
    [InlineData(1600, 1000)]
    [InlineData(760, 600)]
    public async Task PlcToolsAndPagerStayInsideWhileAllEightColumnsScroll(int width, int height)
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);
        var original = await client.GetPlcConnectionsAsync();
        try
        {
            await PlantSeed.ReplacePlcConnectionsAsync(client, SeedHolder,
                Enumerable.Range(901, 52).Select(id => new PlcConnectionDto(id,
                    $"Line {id}", "10.1.1.1", 2011, "10.1.1.2", 2012)).ToArray());
            await using var editor = await DrawioEditorPage.OpenInAWindowOfAsync(fixture.Browser, fixture.Host, width, height);
            await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
            await editor.OpenWindowAsync("drawio-plc-connections");
            await editor.WaitForTextAsync("#drawio-plc-page-summary", "1–25 of 52");
            var layout = await PlcLayoutAsync(editor);
            Assert.True(layout.ToolsInside);
            Assert.True(layout.PagerInside);
            Assert.Equal("hidden", layout.ContentOverflowY);
            Assert.Equal("scroll", layout.GridOverflowY);
            Assert.Equal("auto", layout.GridOverflowX);
            Assert.Equal("sticky", layout.HeaderPosition);
            if (width == 760) Assert.True(layout.HorizontalOverflow);
            Assert.True(layout.GridHeight > 40);
            Assert.Equal(["Id", "Description", "PLC address", "PCM port", "Local address", "Local port", "Check", ""], layout.Columns);
            await editor.LookAtItAsync($"plc-{width}x{height}");

            await editor.FillAsync($"{Rows}:first-child input[data-field='Description']", "Edited on page one");
            await editor.ClickAsync("#drawio-plc-next");
            await editor.WaitForTextAsync("#drawio-plc-page-summary", "26–50 of 52");
            await editor.ClickAsync("#drawio-plc-last");
            await editor.WaitForTextAsync("#drawio-plc-page-summary", "51–52 of 52");
            await editor.FillAsync("#drawio-plc-filter", "952");
            await editor.WaitForTextAsync("#drawio-plc-page-summary", "1 of 1");
            await editor.FillAsync("#drawio-plc-filter", "");
            await editor.WaitForTextAsync("#drawio-plc-page-summary", "1–25 of 52");
            Assert.Equal("Edited on page one", await editor.InputValueOfAsync($"{Rows}:first-child input[data-field='Description']"));
            await editor.ClickAsync("#drawio-plc-save");
            await editor.WaitForShownAsync("#drawio-plc-confirmation", "PLC confirmation");
            Assert.True((await PlcLayoutAsync(editor)).PagerInside);
            await editor.LookAtItAsync($"plc-confirmation-{width}x{height}");
            await editor.ClickAsync("#drawio-plc-cancel");
            Assert.Equal("Edited on page one", await editor.InputValueOfAsync($"{Rows}:first-child input[data-field='Description']"));
            const string otherHolder = "browser-test-plc-layout-other";
            await client.AcquireEditLockAsync(otherHolder);
            try
            {
                await editor.ClickAsync("#drawio-plc-save");
                await editor.ClickAsync("#drawio-plc-confirm");
                await editor.WaitForTextAsync("#drawio-plc-outcome", "has been editing the plant");
                Assert.True((await PlcLayoutAsync(editor)).PagerInside);
                Assert.Equal("Edited on page one", await editor.InputValueOfAsync($"{Rows}:first-child input[data-field='Description']"));
            }
            finally
            {
                await client.ReleaseEditLockAsync(otherHolder);
            }
            await editor.DragAsync("#drawio-plc-connections .drawio-window-grip", -240, -100);
            await editor.ClickAsync("#drawio-plc-save");
            await editor.WaitForShownAsync("#drawio-plc-confirmation", "PLC confirmation in resized window");
            var smaller = await PlcLayoutAsync(editor);
            Assert.True(smaller.ToolsInside);
            Assert.True(smaller.PagerInside);
            Assert.True(smaller.GridHeight > 40);
            await editor.ClickAsync("#drawio-plc-cancel");
        }
        finally
        {
            await PlantSeed.ReplacePlcConnectionsAsync(client, SeedHolder, original);
        }
    }

    private static Task<DrawioOperationalWindowLayout> PlcLayoutAsync(DrawioEditorPage editor) =>
        editor.OperationalWindowLayoutAsync("drawio-plc-connections", ".drawio-device-list-tools",
            ".drawio-list-scroll", ".drawio-plc-pager");

    [Fact]
    public async Task AChangedListSavedFromThePageIsWhatTheHostHolds()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);
        var asTheyWere = await client.GetPlcConnectionsAsync();

        try
        {
            // A known starting list, written with the client - with the permit, which the route
            // requires since Task 4b, taken and given back before the page asks for it.
            await PlantSeed.ReplacePlcConnectionsAsync(client, SeedHolder,
            [
                new PlcConnectionDto(901, "Line A", "10.1.1.1", 2011, "10.1.1.2", 2012),
                new PlcConnectionDto(902, "Line B", "10.1.2.1", 2021, "10.1.2.2", 2022),
            ]);

            await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
            await editor.WaitForDeviceAsync(PlantSeed.DraggedName);

            await editor.ClickAsync("#drawio-plc-connections-toggle");
            await editor.WaitForTextAsync("#drawio-plc-count", "2 shown · 2 total · 2 active");

            // A numeric error must be painted on even rows as well: zebra striping must not
            // outweigh the error state. bUnit cannot evaluate this CSS cascade.
            await editor.FillAsync($"{Rows}:nth-child(2) input[data-field='Id']", "-1");
            await editor.WaitForTextAsync($"{Rows}:nth-child(2) td[data-field='Problem']", "Id greater than zero");
            Assert.Equal("rgb(253, 236, 234)",
                await editor.ComputedStyleAsync($"{Rows}:nth-child(2)", "background-color"));
            // The first cell has its own zebra rule. It must let the row's error colour show
            // through too, rather than paint a grey patch over that colour.
            Assert.Equal("rgba(0, 0, 0, 0)",
                await editor.ComputedStyleAsync($"{Rows}:nth-child(2) td:first-child", "background-color"));
            await editor.FillAsync($"{Rows}:nth-child(2) input[data-field='Id']", "902");
            await editor.WaitForShownAsync($"{Rows}:nth-child(2)[aria-invalid='false']", "the corrected PLC row");
            Assert.NotEqual("rgba(0, 0, 0, 0)",
                await editor.ComputedStyleAsync($"{Rows}:nth-child(2) td:first-child", "background-color"));

            // Each gesture that changes the rows is waited for on the count before the next one picks a
            // row by position: a box found before the page has rendered the gesture is the wrong row.
            await editor.FillAsync($"{Rows}:nth-child(2) input[data-field='Description']", "Line B renamed");
            // Removed rows stay on screen, struck, until the save: the count is of the rows still in the list.
            await editor.ClickAsync($"{Rows}:nth-child(1) button.drawio-plc-remove");
            await editor.WaitForTextAsync("#drawio-plc-count", "2 shown · 2 total · 1 active");
            await editor.ClickAsync("#drawio-plc-add");
            await editor.WaitForTextAsync("#drawio-plc-count", "3 shown · 3 total · 2 active");
            await editor.FillAsync($"{Rows}:last-child input[data-field='Id']", "903");
            await editor.FillAsync($"{Rows}:last-child input[data-field='Description']", "Line C");

            // Changed and not saved: the button of the bar wears a dot, and says so under the mouse
            // (passata grafica, 29/9: the words that used to follow its name are behind its tip).
            Assert.Null(await editor.TipOnScreenAsync("#drawio-plc-connections-toggle"));
            Assert.Equal("PLC connections (changed, not saved)", await editor.TipUnderTheMouseAsync("#drawio-plc-connections-toggle"));

            await editor.ClickAsync("#drawio-plc-save");
            await editor.WaitForTextAsync("#drawio-plc-confirmation", "1 added (903), 1 removed (901), 1 modified (902)");
            await editor.ClickAsync("#drawio-plc-confirm");
            await editor.WaitForTextAsync("#drawio-plc-outcome", "Saved to the host");

            var onTheHost = await client.GetPlcConnectionsAsync();
            Assert.Equal(
            [
                new PlcConnectionDto(902, "Line B renamed", "10.1.2.1", 2021, "10.1.2.2", 2022),
                // A row added in the grid starts from the WinForms' defaults (AddingNew).
                new PlcConnectionDto(903, "Line C", "127.0.0.1", 2011, "127.0.0.1", 2012),
            ], onTheHost);
            await editor.WaitForTextAsync("#drawio-plc-count", "2 shown · 2 total · 2 active");
        }
        finally
        {
            // Put back BY VALUE, not as they were in every respect: PlcSettingsStore.ReplaceConnectionsAsync
            // deletes the rows this test left (902, 903) and re-creates, with LastCommandId at zero, every
            // connection the test's own first write replaced - so on the test host their command-identifier
            // counters restart. Harmless here (the test host's engine never talks to a real PLC), and the
            // only restore the route offers.
            try
            {
                await PlantSeed.ReplacePlcConnectionsAsync(client, SeedHolder, asTheyWere);
            }
            catch (Exception ex)
            {
                output.WriteLine($"CLEANUP FAILED: the host's PLC connections may be left changed ({ex.GetType().Name}: {ex.Message}).");
            }
        }
    }
}
