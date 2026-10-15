using Xunit;
using Xunit.Abstractions;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// Row 33 of the parity inventory against a real draw.io: the switch really turns draw.io's OWN
/// ruler elements on and off (not a mark of ours - <c>div.geRuler</c>, measured in app.min.js's
/// <c>mxRuler</c> constructor), and the choice survives a reload of the page - row 33's own words,
/// "the value is remembered across sessions, default off".
/// </summary>
public sealed class DrawioRulerSwitchBrowserTest(DrawioEditorFixture fixture, ITestOutputHelper output) : IClassFixture<DrawioEditorFixture>
{
    private const string SeedHolder = "browser-test-ruler-switch";

    [Fact]
    public async Task TheSwitchTurnsDrawiosOwnRulersOnAndOffAndTheChoiceSurvivesAReload()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);

        // Default: off, and draw.io has never built the elements at all - not merely hidden them.
        Assert.False(await editor.RulerSwitchCheckedAsync());
        Assert.Equal(0, await editor.RulerElementCountAsync());

        await editor.ClickAsync("#drawio-ruler-switch");
        await editor.WaitForRulerElementCountAsync(2);
        Assert.True(await editor.RulerSwitchCheckedAsync());

        await editor.ClickAsync("#drawio-ruler-switch");
        await editor.WaitForRulerElementCountAsync(0);
        Assert.False(await editor.RulerSwitchCheckedAsync());

        // On again, then a RELOAD - a new Blazor circuit, the key asked for again, but the SAME
        // browser context: row 33's memory is the browser's own localStorage, which a reload of one
        // of its tabs does not clear.
        await editor.ClickAsync("#drawio-ruler-switch");
        await editor.WaitForRulerElementCountAsync(2);

        await editor.ReloadAndPassTheKeyGateAsync(fixture.Host.ApiKey);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);

        Assert.True(await editor.RulerSwitchCheckedAsync());
        // Row 33's own second sentence: "if at startup the memory says on, they turn on as soon as
        // draw.io is ready" - not something the operator has to click again.
        await editor.WaitForRulerElementCountAsync(2);
    }

    /// <summary>
    /// Fix round 1 (reviewer finding 1): draw.io builds its own ruler at ITS OWN startup, from its
    /// own shared memory (<c>mxSettings</c>, the <c>.drawio-config</c> localStorage entry -
    /// MEASURED in app.min.js, EditorUi's constructor reads <c>mxSettings.isRulerOn()</c> before this
    /// bridge's 'init' ever fires) - not only from this page's own remembered choice. A session with
    /// nothing of ITS OWN remembered, opened where that shared entry already says rulers on (an
    /// earlier session's own draw.io save, or another tool entirely), must still end up with rulers
    /// off and the switch saying off: the old code only ever asked draw.io to turn rulers ON, never
    /// to turn an unwanted ON back off.
    /// </summary>
    [Fact]
    public async Task WhenDrawiosOwnMemorySaysRulersAreOnTheSwitchReconcilesThemOff()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        // configVersion:null matches Editor.configVersion's own default - MEASURED in app.min.js:
        // this bundle never sets Editor.config (no "configuration" URL parameter), so
        // Editor.configVersion stays null, and mxSettings.parse resets to ITS OWN defaults
        // (isRulerOn false) on any other mismatch - which would seed nothing this test could measure.
        const string seed =
            "window.localStorage.setItem('.drawio-config', JSON.stringify(" +
            "{configVersion: null, isRulerOn: true, version: 18}));";

        await using var editor = await DrawioEditorPage.OpenWithInitScriptAsync(fixture.Browser, fixture.Host, seed);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);

        // Our OWN memory (wcs.webeditor.drawio-rulers) was never seeded: absent, so the switch shows
        // off from the moment it renders - before draw.io has even answered 'init'.
        Assert.False(await editor.RulerSwitchCheckedAsync());

        // And draw.io's own rulers - already built from its own shared memory by the time the plant
        // is on screen - are told off: the reconciliation this fix round adds.
        await editor.WaitForRulerElementCountAsync(0);
    }
}
