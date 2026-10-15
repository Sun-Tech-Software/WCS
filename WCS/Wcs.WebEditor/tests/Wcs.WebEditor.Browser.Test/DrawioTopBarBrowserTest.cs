using Xunit;
using Xunit.Abstractions;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// The page's one bar (passata grafica, 2026-09-29) as a sighted operator gets it, in a real browser
/// against a real host: the names of its buttons on a narrow window, where their words are not shown;
/// its one line while something is on its way in it - the engine's pending line, the configuration
/// check running; the engine's refusal, whole, on a line of its own under it; and the one coloured
/// button while the engine asks. bUnit lays nothing out and draws no CSS: whether a tip opens under
/// the mouse, whether the bar wraps onto a second line, whether a sentence is cut short and what
/// colour a button is painted, only a browser can say.
/// </summary>
/// <remarks>
/// The two sizes of the spec's "one line" are 1366x768 and 1920x1080; the one-line tests run at the
/// narrower, where the bar with every word is closest to the edge (the engine in Run and the "Read
/// only" pill left some 60 px, measured on 29/9) - and the one with what is on its way in the bar at
/// 900 px too (row 30 of the queue), where the bar has its icons alone.
/// </remarks>
public sealed class DrawioTopBarBrowserTest(DrawioEditorFixture fixture, ITestOutputHelper output) : IClassFixture<DrawioEditorFixture>
{
    private const string SeedHolder = "browser-test-top-bar";

    /// <summary>Somebody else editing the plant: the holder of the edit permit a refused start runs into.</summary>
    private const string SomebodyElse = "browser-test-top-bar-somebody-else";

    private const double SamePixel = DrawioLook.SamePixel;

    /// <summary>
    /// On a narrow window (1280 px: a 1920 laptop at 150%) the buttons lose their words - and the
    /// name moves into the tip: with the mouse on a button, its name is shown under it; with the mouse
    /// elsewhere, nothing. Before the fix round of 29/9 the name was only in the aria-label, which a
    /// sighted operator never sees: seven bare icons, and nothing on hover.
    /// </summary>
    [Fact]
    public async Task OnANarrowWindowEachButtonShowsItsNameUnderTheMouse()
    {
        await using var editor = await OpenAsync(1280, 800);

        foreach (var (id, name) in DrawioLook.WindowButtons)
        {
            var hovered = await editor.NameUnderTheMouseAsync(id);
            output.WriteLine($"Under the mouse: {hovered}");
            Assert.True(hovered.Found, $"The bar has no button {id}.");
            Assert.True(hovered.Label is null, $"On a 1280 px window the word of {id} is still shown: {hovered}.");
            Assert.True(hovered.GeneratedText == name, $"With the mouse on it, {id} does not show \"{name}\": {hovered}.");

            // Not blind: the name is not shown all along.
            await editor.PutTheMouseOnAsync("#drawio-plant-name");
            var away = await editor.NameOnScreenAsync(id);
            Assert.True(away.Generated is null, $"With the mouse elsewhere, {id} still shows its name: {away}.");
        }
    }

    /// <summary>
    /// The engine's refusal - here a start refused because somebody else holds the edit permit, a
    /// sentence of some 115 characters - is on screen whole at 1366x768, on a line of its own right
    /// under the bar, with nothing covering it - not even a window dragged up across its start as far as
    /// windows go; the bar is on its one line, at rest and with the refusal said. The review of 29/9:
    /// cut short with an ellipsis at 20rem on the bar's line (the first fix round), most refusals lost
    /// what counts, which they say at their end ("... so the engine was NOT started. Save first, then
    /// start it.") - on screen only under the mouse, in a tip. The test reads the layout, not the words:
    /// whatever the refusal, its box must hold all of its text.
    /// </summary>
    [Fact]
    public async Task TheEnginesRefusalIsOnScreenWholeOnALineOfItsOwnUnderTheBar()
    {
        using var client = fixture.CreateClient();
        await using var editor = await OpenAsync(1366, 768);
        var idle = await editor.BarHeightAsync();
        DrawioLook.AssertOneLine(await editor.BarLineAsync(), "At rest at 1366x768");

        await PlantSeed.WaitForTheEditPermitToBeFreeAsync(client, SomebodyElse);
        var lease = await client.AcquireEditLockAsync(SomebodyElse);
        Assert.True(lease.IsMine, "The edit permit could not be taken for somebody else: the refusal this test is about would not come.");
        try
        {
            await editor.ClickAsync("#drawio-engine-start");
            await editor.ClickAsync("#drawio-engine-confirm");
            await editor.WaitForTextAsync("#drawio-engine-error", "somebody else has been editing the plant");
            var sentence = (await editor.TextContentOfAsync("#drawio-engine-error"))!;
            Assert.StartsWith("The engine was NOT started.", sentence);

            // A window dragged up and to the left as far as it goes: across the refusal's start, where the
            // mouse is sent to reach it, it is kept below the line only by the floor.
            await editor.OpenWindowAsync("drawio-device-list");
            await editor.DragTheWindowUpAsync("drawio-device-list", 0);
            var placed = await editor.TheStripAndItsWindowsAsync("drawio-device-list");
            var refused = await editor.BarHeightAsync();
            var line = await editor.TextLineAsync("#drawio-engine-error");
            output.WriteLine(FormattableString.Invariant($"The bar: {idle:0.##} px high at rest, {refused:0.##} px with the refusal."));
            output.WriteLine(line.ToString());
            output.WriteLine(placed.ToString());
            var window = placed.Windows.Single();
            Assert.True(window.Open && window.Top <= placed.Floor + SamePixel && window.Left <= line.Left + 40 && line.Left + 40 <= window.Left + window.Width,
                $"The test is blind: the Devices window does not lie across the refusal's start as close as it may come ({window}; {line}).");

            Assert.True(line.Shown && !line.InTheBar, $"The engine's refusal is not a line of its own on screen: {line}.");
            Assert.True(Math.Abs(line.Top - line.BarBottom) <= SamePixel, $"The engine's refusal is not right under the bar: {line}.");
            Assert.True(line.OverflowX <= SamePixel && line.OverflowY <= SamePixel && line.Left >= 0 && line.Right <= line.ViewportWidth + SamePixel,
                $"The engine's refusal is not whole on screen - \"{sentence}\": {line}.");
            Assert.True(line.Reached, $"Something covers the engine's refusal: {line}.");
            Assert.True(Math.Abs(refused - idle) <= SamePixel,
                FormattableString.Invariant($"With the engine's refusal said the bar is {refused:0.##} px high, at rest {idle:0.##}: it went onto a second line."));
            DrawioLook.AssertOneLine(await editor.BarLineAsync(), "With the engine's refusal said");
            Assert.Equal(NavigatorStatusTypes.Stop, (await client.GetStatusAsync()).NavStatus);
        }
        finally
        {
            await client.ReleaseEditLockAsync(SomebodyElse);
            await TestHostEngine.LeaveStoppedAsync(client, output);
        }
    }

    /// <summary>
    /// While the engine asks, the one coloured button on the bar is the one that acts - "Start engine"
    /// in blue, "Stop engine" in red, in the box - and the button that asked is not coloured, with the
    /// mouse on it or elsewhere (the spec: one blue button per view). Until the review of 29/9 it kept
    /// its colour: two blue buttons, one above the other, the top one taking the question back and the
    /// bottom one starting the engine (two red ones for the stop). The colours are read as the browser
    /// paints them (<see cref="DrawioEditorPage.ColouredButtonsOfTheBarAsync"/>), and seen at rest as
    /// well - the gesture's own button, the only coloured one - so that the reading is not blind.
    /// </summary>
    [Fact]
    public async Task WhileTheEngineAsksTheOnlyColouredButtonIsTheOneThatActs()
    {
        using var client = fixture.CreateClient();
        await using var editor = await OpenAsync(1366, 768);
        try
        {
            await editor.PutTheMouseOnAsync("#drawio-plant-name");
            Assert.Equal(["drawio-engine-start"], await editor.ColouredButtonsOfTheBarAsync());
            var startAtRest = await editor.BoxOfAsync("#drawio-engine-start");
            var liveAtRest = await editor.BoxOfAsync("#drawio-live-status");

            await editor.ClickAsync("#drawio-engine-start");
            await editor.WaitForAttributeAsync("#drawio-engine-start", "aria-expanded", "true");
            await AssertOnlyTheYesIsColouredAsync(editor, "#drawio-engine-start");
            // Start is the page's plain button while it asks (owner's decision, 30/9): white, grey
            // under the mouse and pressed, text of the normal weight - not the pressed grey and the
            // bold it was drawn with until then. Yet it keeps its width, and nothing after it in the
            // bar moves when the box opens: the bold copy of its words holds their width, and says
            // nothing to a screen reader - the button is still named "Start the engine", exactly.
            DrawioLook.AssertThePagesPlainButton(await editor.ButtonLookAsync("#drawio-engine-start", "#drawio-plant-name"), output);
            var startAsking = await editor.BoxOfAsync("#drawio-engine-start");
            var liveAsking = await editor.BoxOfAsync("#drawio-live-status");
            Assert.True(Math.Abs(startAsking[2] - startAtRest[2]) <= SamePixel && Math.Abs(liveAsking[0] - liveAtRest[0]) <= SamePixel,
                FormattableString.Invariant($"The box open, \"Start the engine\" is {startAsking[2]:0.##} px wide (at rest {startAtRest[2]:0.##}) and the live link's pill starts at x {liveAsking[0]:0.##} (at rest {liveAtRest[0]:0.##}): the bar moved."));
            Assert.Equal(["drawio-engine-start"], await editor.ButtonsNamedAsync("Start the engine"));
            Assert.True(await editor.IsShownAsync("#drawio-engine-confirm-box"),
                "The press on \"Start the engine\", let go elsewhere, took the question back: the box is gone.");

            await editor.ClickAsync("#drawio-engine-confirm");
            await editor.WaitForTextAsync("#drawio-engine-state", "Engine: Run");
            await editor.WaitForTheEngineToOfferAsync("drawio-engine-stop");
            await editor.PutTheMouseOnAsync("#drawio-plant-name");
            Assert.Equal(["drawio-engine-stop"], await editor.ColouredButtonsOfTheBarAsync());

            await editor.ClickAsync("#drawio-engine-stop");
            await editor.WaitForAttributeAsync("#drawio-engine-stop", "aria-expanded", "true");
            await AssertOnlyTheYesIsColouredAsync(editor, "#drawio-engine-stop");

            await editor.ClickAsync("#drawio-engine-cancel");
        }
        finally
        {
            await TestHostEngine.LeaveStoppedAsync(client, output);
        }
    }

    /// <summary>
    /// The box open: the Yes is the one coloured button, with the mouse elsewhere and with the mouse on
    /// the button that asked. Stop keeps its red class while it asks, and a coloured button's hover rule
    /// weighs more than a plain one's; Start, a plain button while it asks, has the plain hover.
    /// </summary>
    private async Task AssertOnlyTheYesIsColouredAsync(DrawioEditorPage editor, string asker)
    {
        await editor.PutTheMouseOnAsync("#drawio-plant-name");
        var away = await editor.ColouredButtonsOfTheBarAsync();
        output.WriteLine($"The box open, the mouse elsewhere: coloured {string.Join(", ", away)}.");
        Assert.Equal(["drawio-engine-confirm"], away);

        await editor.PutTheMouseOnAsync(asker);
        var hovered = await editor.ColouredButtonsOfTheBarAsync();
        output.WriteLine($"The box open, the mouse on {asker}: coloured {string.Join(", ", hovered)}.");
        Assert.Equal(["drawio-engine-confirm"], hovered);
    }

    /// <summary>
    /// At 1366x768 the bar stays on its one line through everything that is on its way in it: the
    /// start ("Checking that the engine can be started...", then "Starting the engine..."), the
    /// configuration check running with the engine in Run (the checks' words grow by a clause), and the
    /// stop ("Stopping the engine...", next to the "Read only" pill). Every such moment is written down
    /// by the page's own MutationObserver with the bar's height at that moment, however short it lasts.
    /// Until the fix round of 29/9 the bar with every word had room only for the resting states, and
    /// wrapped - moving the drawing and the windows' floor down and up again - on each of these.
    /// </summary>
    [Fact]
    public Task WhatIsOnItsWayInTheBarLeavesItOnOneLine() => TheBarStaysOnOneLineThroughWhatIsOnItsWayAsync(1366, 768);

    /// <summary>
    /// At 900 px too (row 30 of the queue, the owner's decision of 1/10) the bar stays on its one line
    /// through the same moments, the engine's pending line among them. With the buttons' words gone it
    /// needed 1101 px at its busiest (measured on 30/9), so for as long as a command of the engine was on
    /// its way it went onto a second line, moving the drawing down and up again.
    /// </summary>
    [Fact]
    public Task AtNineHundredPixelsWhatIsOnItsWayInTheBarLeavesItOnOneLine() => TheBarStaysOnOneLineThroughWhatIsOnItsWayAsync(900, 768);

    /// <summary>
    /// The start, the configuration check running with the engine in Run, and the stop, on a window of
    /// <paramref name="width"/> by <paramref name="height"/>: each moment with something on its way in
    /// the bar is written down by the page's own MutationObserver with the bar's height at that moment,
    /// however short it lasts, and every one of them must be the height of the bar at rest.
    /// </summary>
    private async Task TheBarStaysOnOneLineThroughWhatIsOnItsWayAsync(int width, int height)
    {
        var size = FormattableString.Invariant($"{width}x{height}");
        using var client = fixture.CreateClient();
        await using var editor = await OpenAsync(width, height);
        var idle = await editor.BarHeightAsync();
        // The height every moment is held to is one line's: read off the layout, not only compared.
        DrawioLook.AssertOneLine(await editor.BarLineAsync(), $"At rest at {size}");
        await editor.WatchTheBarWhileSomethingIsOnItsWayAsync();

        try
        {
            await editor.ClickAsync("#drawio-engine-start");
            await editor.ClickAsync("#drawio-engine-confirm");
            await editor.WaitForTextAsync("#drawio-engine-state", "Engine: Run");
            await editor.WaitForTextAsync("#drawio-engine-banner", "engine is running");
            var running = await editor.BarHeightAsync();
            Assert.True(Math.Abs(running - idle) <= SamePixel,
                FormattableString.Invariant($"At {size}, with the engine running the bar is {running:0.##} px high, stopped {idle:0.##}."));

            await editor.OpenTheChecksAsync();
            await editor.RunTheConfigurationCheckAsync();
            await editor.WaitForAConfigurationCheckOutcomeAsync();
            await editor.WaitForTheSummaryToRestAsync();

            await editor.ClickAsync("#drawio-engine-stop");
            await editor.ClickAsync("#drawio-engine-confirm");
            await editor.WaitForTextAsync("#drawio-engine-state", "Engine: Stop");
        }
        finally
        {
            await TestHostEngine.LeaveStoppedAsync(client, output);
        }

        var moments = await editor.BarMomentsAsync();
        foreach (var moment in moments) output.WriteLine($"{moment}");
        output.WriteLine(FormattableString.Invariant($"The bar at rest at {size}: {idle:0.##} px high."));

        // Not blind: each of the three was seen at least once.
        Assert.Contains(moments, m => m.Pending is not null && m.Engine == "Engine: Stop");
        Assert.Contains(moments, m => m.CheckRunning && m.Engine == "Engine: Run");
        Assert.Contains(moments, m => m.Pending is not null && m.Engine == "Engine: Run");
        var wrapped = moments.Where(m => Math.Abs(m.Height - idle) > SamePixel).ToArray();
        Assert.True(wrapped.Length == 0,
            FormattableString.Invariant($"At {size} the bar, {idle:0.##} px high at rest, was not on one line at these moments: {string.Join(" ", wrapped.Select(m => m.ToString()))}"));
    }

    /// <summary>
    /// On a wide window, while the configuration check runs, the checks' button keeps its words - there
    /// is room for them - cut short at 12rem, and the mouse on the button shows them whole in its tip, as
    /// on a narrow window it shows the name. Until the final fix round that tip was drawn only where the
    /// words were not shown at all, and at 1760 px and wider the rest of them could not be read. The
    /// running check is staged on the button (StageTheRunningCheckAsync says why).
    /// </summary>
    [Fact]
    public async Task OnAWideWindowTheRunningChecksWordsCutShortAreWholeUnderTheMouse()
    {
        const string Running = "Plant checks - the configuration check is running...";
        await using var editor = await OpenAsync(1920, 1080);
        await editor.StageTheRunningCheckAsync();

        await editor.PutTheMouseOnAsync("#drawio-plant-name");
        var away = await editor.NameOnScreenAsync("drawio-checks-toggle");
        output.WriteLine($"With the mouse elsewhere: {away}");
        Assert.True(away.Label is not null && await editor.IsCutShortAsync("#drawio-checks-summary"),
            $"The test is blind: at 1920 px the running check's words are not shown cut short: {away}.");
        Assert.True(away.Generated is null, $"With the mouse elsewhere the checks' button shows a tip: {away}.");

        var hovered = await editor.NameUnderTheMouseAsync("drawio-checks-toggle");
        output.WriteLine($"Under the mouse: {hovered}");
        Assert.True(hovered.GeneratedText == Running, $"With the mouse on it, the checks' button does not show \"{Running}\": {hovered}.");
    }

    /// <summary>A fresh seed and the page open on it in a window of this size, with the seed's device drawn.</summary>
    private async Task<DrawioEditorPage> OpenAsync(int width, int height)
    {
        output.WriteLine(fixture.Host.Declaration);
        using (var client = fixture.CreateClient())
            await PlantSeed.ResetAsync(client, SeedHolder);

        var editor = await DrawioEditorPage.OpenInAWindowOfAsync(fixture.Browser, fixture.Host, width, height);
        try
        {
            await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
            return editor;
        }
        catch
        {
            await editor.DisposeAsync();
            throw;
        }
    }
}
