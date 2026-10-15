using Xunit;
using Xunit.Abstractions;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// Queue item 14: the page's circuit connection drops and comes back - the same circuit - and the page
/// says so. A save draw.io handed over while it was down never reached the page: Blazor neither sent
/// its call again nor settled it, and until this item the page came back alive with nothing said and
/// the save simply not there. Now a line for the drop, and one for the save, with what to do.
/// </summary>
/// <remarks>
/// The connection is dropped as Blazor's own tests drop it, and its reconnection held back while the
/// save is handed over (<see cref="DrawioEditorPage.DropTheConnectionAsync"/>): handed over in a race
/// with a reconnection on localhost, the save would sometimes reach the page and sometimes not.
/// </remarks>
public sealed class DrawioReconnectionTest(DrawioEditorFixture fixture, ITestOutputHelper output) : IClassFixture<DrawioEditorFixture>
{
    private const string SeedHolder = "browser-test-reconnection";

    private const string SaveLine = "The connection dropped during the save; nothing was written, press Save again.";

    private const string DropLine =
        "The connection to the host dropped and came back: what you did in that moment may not have reached the page.";

    private const string MayHaveBeenWrittenLine =
        "The connection dropped while the plant was being saved: the save may have been written. Reopen the plant to see what it holds.";

    [Fact]
    public async Task ASaveHandedOverWhileTheConnectionIsDownIsSaidLost()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);
        // No drop yet: neither line.
        Assert.Null(await editor.TextOfAsync("#drawio-reconnected-notice-text"));
        Assert.Null(await editor.TextOfAsync("#drawio-save-lost-notice-text"));
        var round = await editor.SaveRoundAsync();

        await editor.DropTheConnectionAsync(editor.HandOverASaveAsDrawioDoesAsync);

        await editor.WaitForTextAsync("#drawio-save-lost-notice-text", SaveLine);
        await editor.WaitForTextAsync("#drawio-reconnected-notice-text", DropLine);
        // Task 12: never reached the page, so nothing may have been written either.
        Assert.Null(await editor.TextOfAsync("#drawio-save-unknown-notice-text"));
        // What the lines say is what happened: the save never reached the page - no round ran. Its
        // call never settles, and the bridge held its whole drawing for the rest of the session until
        // the final review (C18): the page now tells it to let go.
        Assert.Equal(round, await editor.SaveRoundAsync());
        await editor.WaitUntilTheBridgeHoldsNoSaveAsync();

        // Save again, with draw.io's own button, on the circuit that came back: the save's line goes,
        // the drop's stays until its X.
        await editor.SaveAsync();
        Assert.Null(await editor.SaveErrorAsync());
        Assert.Null(await editor.TextOfAsync("#drawio-save-lost-notice-text"));
        Assert.Equal(DropLine, await editor.TextOfAsync("#drawio-reconnected-notice-text"));
    }

    /// <summary>
    /// Task 12: the save reached the page and its round went to the host, and the drop took its answer
    /// on the way back to the browser. The page says the save may have been written; "Leave site?" stays
    /// on, because the bridge never heard that the drawing is on the plant; and the plant read again -
    /// the page reopened - puts it down.
    /// </summary>
    [Fact]
    public async Task ASaveWhoseAnswerTheDropTookMayHaveBeenWrittenAndTheCloseAsksUntilThePlantIsReopened()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);
        var (before, after) = await editor.DragDeviceAsync(PlantSeed.DraggedName, 160, 100);
        Assert.True(after.X > before.X && after.Y > before.Y,
            $"The drag did not move the shape: ({before.X:0.#}, {before.Y:0.#}) -> ({after.X:0.#}, {after.Y:0.#}); there is no work to save.");

        await editor.LoseTheNextSaveAnswerInADropAsync();
        await editor.SaveAsync();

        // The round wrote everything: had its answer reached the bridge, the mark would be down.
        Assert.True(await editor.TheLostSaveAnswerAsync());
        Assert.Null(await editor.SaveErrorAsync());
        await editor.WaitForTextAsync("#drawio-save-unknown-notice-text", MayHaveBeenWrittenLine);
        await editor.WaitForTextAsync("#drawio-reconnected-notice-text", DropLine);
        Assert.Null(await editor.TextOfAsync("#drawio-save-lost-notice-text"));

        var closing = await editor.CloseTheTabAsync();
        output.WriteLine($"Closing after the save whose answer was lost: {closing}");
        Assert.True(closing.Asked, "The close did not ask \"Leave site?\" after a save whose outcome never came back to the browser.");
        Assert.Equal("beforeunload", closing.DialogType);
        Assert.True(closing.StillOpen, "The question was refused and the page went away all the same.");

        await editor.ReopenThePlantAsync(fixture.Host.ApiKey);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);
        Assert.Null(await editor.TextOfAsync("#drawio-save-unknown-notice-text"));

        var reopened = await editor.CloseTheTabAsync();
        output.WriteLine($"Closing after the plant was reopened: {reopened}");
        Assert.False(reopened.Asked, $"The plant was reopened and the close still asked \"Leave site?\" ({reopened.DialogType}).");
    }

    [Fact]
    public async Task ADropWithNoSaveInItIsSaidAsADrop()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);
        Assert.Null(await editor.TextOfAsync("#drawio-reconnected-notice-text"));

        await editor.DropTheConnectionAsync(() => Task.CompletedTask);

        await editor.WaitForTextAsync("#drawio-reconnected-notice-text", DropLine);
        Assert.Null(await editor.TextOfAsync("#drawio-save-lost-notice-text"));
        Assert.Empty(await editor.SavesTheBridgeHoldsAsync());
    }
}
