using Wcs.Api.Contracts.Dtos;
using Xunit;
using Xunit.Abstractions;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// The whole round of row 47 against a real host: the click, the permit, the unsaved-work check on
/// the real draw.io (the first real use of the bridge's <c>currentDrawing</c>), the command, and the
/// host's event coming back through the page's own channel. It is also the only test that proves the
/// page's SignalR connection to its own host works.
/// </summary>
public sealed class DrawioEngineSwitchBrowserTest(DrawioEditorFixture fixture, ITestOutputHelper output) : IClassFixture<DrawioEditorFixture>
{
    private const string SeedHolder = "browser-test-engine-switch";

    [Fact]
    public async Task TheEngineStartsAndStopsFromTheDrawing()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        try
        {
            await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
            await editor.WaitForDeviceAsync(PlantSeed.DraggedName);

            // The drawing is untouched: the unsaved-work check reads it off draw.io and finds nothing
            // to write. A refusal would be said on the switch, and the wait below reports the page.
            await editor.ClickAsync("#drawio-engine-start");
            await editor.ClickAsync("#drawio-engine-confirm");
            await editor.WaitForTextAsync("#drawio-engine-state", "Engine: Run");
            await editor.WaitForTextAsync("#drawio-engine-banner", "engine is running");
            Assert.Equal(NavigatorStatusTypes.Run, (await client.GetStatusAsync()).NavStatus);

            await editor.ClickAsync("#drawio-engine-stop");
            await editor.ClickAsync("#drawio-engine-confirm");
            await editor.WaitForTextAsync("#drawio-engine-state", "Engine: Stop");
            Assert.Equal(NavigatorStatusTypes.Stop, (await client.GetStatusAsync()).NavStatus);
        }
        finally
        {
            await TestHostEngine.LeaveStoppedAsync(client, output);
        }
    }
}
