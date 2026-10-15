using System.Text.RegularExpressions;
using Wcs.Api.Client;
using Wcs.Api.Contracts.Dtos;
using Xunit;
using Xunit.Abstractions;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// Task 1, row 55: a test route queued from the page's own Operations section against a real test
/// host, its outcome followed to the end over the live channel - the twin of
/// <see cref="DrawioEngineSwitchBrowserTest"/> for this section, and the only browser test this task
/// asks for (rows 55-57: the other two commands share the same DrawioCommandFollower, and the bUnit
/// suite already follows each of them one by one).
/// </summary>
/// <remarks>
/// Final review, I3: the host of this class points at a warehouse that ANSWERS
/// (<see cref="DrawioCellsFixture"/>'s <see cref="StubWarehouse"/>), not at the default address that
/// does not. Test Route asks the warehouse for the handling mode of the load unit type while the
/// command is Running (<c>ExecutableCommands.TestRoute</c>); against an address that does not answer
/// the outcome waited on the network, and the test went red now and then with the route still "being
/// executed". The stub answers that route at once with 503, so the outcome is the same every run:
/// FAILED, the warehouse unavailable.
/// </remarks>
public sealed class DrawioOperationsBrowserTest(DrawioCellsFixture fixture, ITestOutputHelper output) : IClassFixture<DrawioCellsFixture>
{
    private const string SeedHolder = "browser-test-operations";

    [Theory]
    [InlineData(1600, 1000)]
    [InlineData(760, 600)]
    public async Task OperationTabsKeepTheirControlsReachableWithoutAManualResizeGrip(int width, int height)
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);
        await using var editor = await DrawioEditorPage.OpenInAWindowOfAsync(fixture.Browser, fixture.Host, width, height);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.OpenWindowAsync("drawio-operations");
        var layout = await editor.OperationalWindowLayoutAsync("drawio-operations");
        Assert.True(layout.ControlsFitHorizontally);
        Assert.Equal("auto", layout.ContentOverflowY);
        Assert.Equal(0, await editor.ElementCountAsync("#drawio-operations > .drawio-window-grip"));
        Assert.Equal(1, await editor.ElementCountAsync("#drawio-operations [role='tabpanel']:not([hidden])"));
        await editor.LookAtItAsync($"operations-{width}x{height}");

        await editor.ClickAsync("#drawio-operations-tab-remove-all");
        await editor.WaitForShownAsync("#drawio-remove-all-lu", "Remove all LU tab panel");
        await editor.ClickAsync("#drawio-remove-all-lu-button");
        await editor.WaitForShownAsync("#drawio-remove-all-lu-cancel", "remove all confirmation");
        Assert.True((await editor.OperationalWindowLayoutAsync("drawio-operations")).ControlsFitHorizontally);
        await editor.ClickAsync("#drawio-remove-all-lu-cancel");
        await editor.ClickAsync("#drawio-operations-tab-set-ready");
        await editor.WaitForShownAsync("#drawio-set-all-ready", "Set all Ready tab panel");
        await editor.ClickAsync("#drawio-set-all-ready-button");
        await editor.WaitForShownAsync("#drawio-set-all-ready-cancel", "set ready confirmation");
        await editor.ClickAsync("#drawio-set-all-ready-cancel");
        await editor.ClickAsync("#drawio-operations-tab-mode");
        await editor.WaitForShownAsync("#drawio-mode", "Connection mode tab panel");
        await editor.ClickAsync("#drawio-mode-toggle");
        await editor.WaitForShownAsync("#drawio-mode-cancel", "connection mode confirmation");
        await editor.ClickAsync("#drawio-mode-cancel");
        Assert.True((await editor.OperationalWindowLayoutAsync("drawio-operations")).ControlsFitHorizontally);
    }

    [Fact]
    public async Task TestRouteTabGrowsTheWindowWithoutMovingItsTopLeftCorner()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);
        await using var editor = await DrawioEditorPage.OpenInAWindowOfAsync(fixture.Browser, fixture.Host, 1600, 1000);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.OpenWindowAsync("drawio-operations");
        var mode = await editor.BoxOfAsync("#drawio-operations");

        await editor.ClickAsync("#drawio-operations-tab-test-route");
        await editor.WaitForShownAsync("#drawio-test-route", "Test Route tab panel");
        await editor.WaitForBoxLargerThanAsync("#drawio-operations", mode[2], mode[3]);
        var route = await editor.BoxOfAsync("#drawio-operations");

        Assert.InRange(Math.Abs(route[0] - mode[0]), 0, 1);
        Assert.InRange(Math.Abs(route[1] - mode[1]), 0, 1);
        Assert.True(route[2] > mode[2], $"Test Route width {route[2]:0}px did not grow from Connection mode's {mode[2]:0}px.");
        Assert.True(route[3] > mode[3], $"Test Route height {route[3]:0}px did not grow from Connection mode's {mode[3]:0}px.");
        Assert.True((await editor.OperationalWindowLayoutAsync("drawio-operations")).ControlsFitHorizontally);
    }

    [Fact]
    public async Task ATestRouteIsQueuedAndItsOutcomeComesBack()
    {
        output.WriteLine(fixture.Host.Declaration);
        // Fix round 1 of compito 2: the time of each stage, so that a red says where it stopped (one
        // run went red at 34 s against the usual ~19-30 s, with no message read). The waits themselves
        // are unchanged.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        void Stage(string reached) => output.WriteLine($"STAGE {clock.Elapsed.TotalSeconds,6:0.00} s: {reached}");
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);
        Stage("plant seeded");

        try
        {
            await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
            Stage("page open");
            await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
            Stage("device ready on the drawing");

            // Test Route needs the engine running (WinForms parity: btnRunTestRoute_ItemClick checks
            // NavStatus == Run), so the engine is started first - the same round as
            // DrawioEngineSwitchBrowserTest, through the real permit and the real command.
            await editor.ClickAsync("#drawio-engine-start");
            await editor.ClickAsync("#drawio-engine-confirm");
            await editor.WaitForTextAsync("#drawio-engine-state", "Engine: Run");
            Stage("Engine: Run reached");

            await editor.ClickAsync("#drawio-operations-toggle");
            await editor.ClickAsync("#drawio-operations-tab-test-route");
            await editor.WaitForShownAsync("#drawio-test-route", "Test Route tab panel");

            // Fix round 1, IMPORTANT 2: source and destination come from the device SELECTED ON THE
            // DRAWING (the real cmbORDINE rule, reproduced from the canvas instead of a device
            // dialog), not from a filterable list - select each device with a real mouse click, then
            // press its own "use selected as ..." button.
            await editor.SelectDevicesAsync(PlantSeed.DraggedName);
            await editor.ClickAsync("#drawio-test-route-use-source");
            await editor.SelectDevicesAsync(PlantSeed.UntouchedName);
            await editor.ClickAsync("#drawio-test-route-use-destination");
            await editor.FillAsync("#drawio-test-route-lu-type", "1");

            await editor.ClickAsync("#drawio-test-route-run");
            // Final review: the outcome line's own history, printed once the outcome is read - it says
            // where a slow outcome spent its time: before the engine took the command ("waiting for the
            // engine"), inside it ("is being executed"), or on a live channel that could not follow it.
            await editor.WatchTextsAsync("#drawio-test-route-outcome");
            await editor.ClickAsync("#drawio-test-route-confirm");
            Stage("test route confirmed (queued)");

            // A FINAL outcome, and one known in advance (final review, I3): the stub warehouse
            // answers the handling-mode question with 503 at once, so the engine closes the command
            // in Error - "FAILED" (DrawioCommandFollower.OutcomeFor), never the transient "is being
            // executed". Seeing it proves the round trip - queued over HTTP, settled by the engine,
            // told back over the page's own live channel. A wait that expires asks the host what it
            // holds, to tell a lost event from a slow engine.
            try
            {
                await WaitForTheFinalOutcomeAsync(editor, client);
            }
            finally
            {
                output.WriteLine("OUTCOME LINE: " + await editor.WatchedTextsAsync("#drawio-test-route-outcome"));
            }
            Stage("final outcome read");
            Assert.Contains(fixture.Warehouse.RequestedPaths, path => path.Contains("/handling-mode", StringComparison.Ordinal));
        }
        finally
        {
            Stage("cleanup: stopping the engine");
            await TestHostEngine.LeaveStoppedAsync(client, output);
            Stage("cleanup: stop reached (or given up, see above)");
        }
    }

    /// <summary>
    /// Waits for "FAILED" on the outcome line. If it never comes, the host is asked for the Test Route
    /// commands it holds: one already final while the page still shows it Running is a LOST EVENT
    /// (the page's defect), one still Running on the host is slowness (the engine's) - and the red
    /// says which, instead of only that the time ran out.
    /// </summary>
    private static async Task WaitForTheFinalOutcomeAsync(DrawioEditorPage editor, WcsClient client)
    {
        try
        {
            await editor.WaitForTextAsync("#drawio-test-route-outcome", new Regex("FAILED"));
        }
        catch (InvalidOperationException waited)
        {
            var onTheHost = await client.GetCommandsAsync(minType: (int)CommandTypes.TestRoute, maxType: (int)CommandTypes.TestRoute);
            var described = onTheHost.Count == 0
                ? "the host holds NO Test Route command"
                : string.Join("; ", onTheHost.Select(command => $"command {command.Id}: status {command.Status} ({command.ErrorDescription ?? "no description"})"));
            var final = onTheHost.Any(command => command.Status is 2 or 3 or 99);
            throw new InvalidOperationException(
                (final
                    ? "LOST EVENT: the host already holds a final status for the Test Route, and the page never showed it. "
                    : "SLOW: the host itself does not hold a final status for the Test Route yet. ") +
                $"On the host: {described}.", waited);
        }
    }
}
