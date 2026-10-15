using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Pages;
using Wcs.WebEditor.Session;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

/// <summary>
/// Compito 1, riga 50, il GIRO VERO attraverso la pagina: <c>DrawioEditor.ModeThroughTheGateAsync</c>
/// (il cancello di scrittura, il permesso preso al momento) e <c>ReloadPlantItemsAsync</c> (la
/// rilettura dopo l'accettazione) non sono esercitati da nessuna prova di <c>DrawioOperationsTest</c>,
/// che passa alla sezione un <c>WriteThroughThePage</c> finto: quella suite prova la sezione, questa
/// prova che la pagina la collega davvero al cancello e all'host, come <c>DrawioEditorLiveTest</c> fa
/// per l'interruttore del motore (riga 47).
/// </summary>
public sealed class DrawioEditorModeSwitchTest : PlantCanvasBunitContext
{
    private static readonly Guid A = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
    private readonly DrawioWriteHandler _host = new();

    public DrawioEditorModeSwitchTest()
    {
        _host.Items = [new PlantItemDto(A, "Shuttle 1", DeviceTypes.Automation, null, null, null, ConnectionTypes.Test,
            default, 0, null, 0, 0, 0, 0, default!, 10, 10, 100, 100, 0)];
    }

    private IRenderedComponent<DrawioEditor> RenderAuthenticated()
    {
        var state = new EditorSessionState(new Uri("http://localhost:5580"), _host);
        state.UseKey("test-key");
        Services.AddSingleton(state);
        var cut = Render<DrawioEditor>();
        // The button exists (disabled) even before the plant has loaded - DrawioOperations renders
        // unconditionally inside DrawioEditor's shell, Graph null and all. bUnit's Click() does not
        // respect the disabled attribute the way a real browser does, so waiting for the element
        // alone would let a test interact with the page before LoadGraphAsync has actually landed.
        // Waiting for "not disabled" is waiting for the real condition: the plant is in.
        cut.WaitForAssertion(() => Assert.False(cut.Find("#drawio-mode-toggle").HasAttribute("disabled")));
        return cut;
    }

    /// <summary>The write goes through the REAL write gate and takes the REAL edit permit - not a fake WriteThroughThePage.</summary>
    [Fact]
    public void ConfirmingWritesThroughTheRealGateAndPermit()
    {
        var cut = RenderAuthenticated();

        cut.Find("#drawio-mode-toggle").Click();
        cut.Find("#drawio-mode-confirm").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(ConnectionTypes.Connected, Assert.Single(_host.ConnectionTypeWrites));
            Assert.True(_host.EditLockRequests > 0);
        });
    }

    /// <summary>
    /// "The label changes only when the host has accepted, and then rereads the plant" (compito 1's
    /// own words): after acceptance the page issues a FRESH <c>GET /plant/items</c> (ReloadPlantItemsAsync),
    /// and the switch's own label reflects whatever THAT reread says - not a value remembered locally.
    /// </summary>
    [Fact]
    public void AfterAcceptanceThePageRereadsThePlantAndTheLabelFollowsIt()
    {
        var cut = RenderAuthenticated();
        var itemsRequestsBefore = _host.Requests.Count(r => r.StartsWith("GET ") && r.EndsWith("/plant/items"));

        // The reread is set up to answer something DIFFERENT from what SendAsync itself would have
        // remembered: if the label came from a locally-kept value instead of the reread, it would
        // still show Connected once the host route flips every item over, which is exactly what the
        // REAL host does (SetConnectionTypeForAllAsync writes every item) - so this alone would not
        // catch a page that skipped the reread. What it DOES catch is the reread never happening at
        // all: the count of GET /plant/items below.
        cut.Find("#drawio-mode-toggle").Click();
        cut.Find("#drawio-mode-confirm").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("Mode: Connected", cut.Find("#drawio-mode-state").TextContent.Trim());
            Assert.True(_host.Requests.Count(r => r.StartsWith("GET ") && r.EndsWith("/plant/items")) > itemsRequestsBefore,
                "No fresh GET /plant/items followed the accepted write: the plant was not reread. Requests: " +
                string.Join(", ", _host.Requests));
        });
    }

    /// <summary>
    /// While the engine is Run the PAGE's own EnsureWritableAsync already refuses every write of
    /// this page before any route-specific check is even reached (DrawioEngineCoordinator's own
    /// remarks: "starting the engine turns every page read-only") - so this is the refusal an
    /// operator actually sees, and it is verified against the host code that a 409
    /// (PlantQueries.SetConnectionTypeForAllAsync/RequireNotRunning) sits BEHIND it as a second line
    /// of defence, exercised by DrawioOperationsTest.ARefusalBecauseTheEngineIsRunningIsShown against
    /// the fake host directly (bypassing this page's own gate, which a real browser session never
    /// could) since this page's guard makes the host's 409 otherwise unreachable from here.
    /// </summary>
    /// <summary>
    /// Owner's decision of 27/9 (D1): the connection mode is switched only with the engine STOPPED,
    /// not merely "not running" - the page refuses in every other state before writing, and says why
    /// for the state it is in, like the PLC connections (row 62).
    /// </summary>
    [Theory]
    [InlineData(NavigatorStatusTypes.Run, "running")]
    [InlineData(NavigatorStatusTypes.Pause, "paused")]
    [InlineData(NavigatorStatusTypes.Recover, "recovering")]
    [InlineData(NavigatorStatusTypes.Dispose, "shutting down")]
    public void RefusedUnlessTheEngineIsStoppedWritesNothingAndSaysWhy(NavigatorStatusTypes state, string words)
    {
        _host.NavStatus = state;
        var cut = RenderAuthenticated();

        cut.Find("#drawio-mode-toggle").Click();
        cut.Find("#drawio-mode-confirm").Click();

        cut.WaitForAssertion(() => Assert.StartsWith(
            $"Connection mode was NOT written: the engine is {words}, and the connection mode is switched only with the engine stopped - ",
            cut.Find("#drawio-mode-outcome").TextContent));
        Assert.Contains("Nothing was written.", cut.Find("#drawio-mode-outcome").TextContent);
        Assert.Empty(_host.ConnectionTypeWrites);
    }

    /// <summary>
    /// Compito 4, fix round 2: the permit's answer unreadable (a JsonException, which EnsureWritableAsync
    /// lets through) is a refusal known before anything was sent - the shared gate helper
    /// (DrawioPlantWriteGate.WriteThroughAsync) says "nothing was written", not "not known".
    /// </summary>
    [Fact]
    public void AnUnreadablePermitAnswerIsARefusalAndNothingIsWritten()
    {
        var cut = RenderAuthenticated();
        _host.GarbledRoute = "edit-lock";

        cut.Find("#drawio-mode-toggle").Click();
        cut.Find("#drawio-mode-confirm").Click();

        // Row 37 of the queue (the owner's answer of 9/10): one sentence with the why, and what came
        // back behind the line's "?" - the same gate's words as in the properties panel.
        cut.WaitForAssertion(() => Assert.Equal(
            "The edit permit could not be asked for because the host's answer could not be read: nothing was written.",
            cut.Find("#drawio-mode-outcome").TextContent.Trim()));
        Assert.StartsWith("JsonException: ", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-mode-outcome"));
        Assert.Empty(_host.ConnectionTypeWrites);
    }

    /// <summary>
    /// Row 37 of the queue (the owner's answer of 9/10): the permit's request lost on the way is said
    /// with its why, and the failure is behind the line's "?" - not in the sentence, where
    /// <c>EnsureWritableAsync</c> used to put its message.
    /// </summary>
    [Fact]
    public void APermitRequestLostOnTheWayIsSaidWithTheFailureBehindTheQuestionMark()
    {
        var cut = RenderAuthenticated();
        _host.HttpFailureRoute = "edit-lock";

        cut.Find("#drawio-mode-toggle").Click();
        cut.Find("#drawio-mode-confirm").Click();

        cut.WaitForAssertion(() => Assert.Equal(
            "Connection mode was NOT written. The edit permit could not be asked for because the connection to the host failed: nothing was written.",
            cut.Find("#drawio-mode-outcome").TextContent.Trim()));
        Assert.StartsWith("HttpRequestException: ", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-mode-outcome"));
        Assert.Empty(_host.ConnectionTypeWrites);
    }

    /// <summary>
    /// Fix round 1, CRITICAL 1. Before the fix: a device selected before the plant-wide mode switch
    /// kept the properties panel's STALE ConnectionType in <c>_selectedConfig</c> - the panel's OWN
    /// select for that field still showed "Test" after the switch, and a save with no other change
    /// sent that stale value straight back, silently undoing the switch on this one device. The fix
    /// (<c>DrawioSelectionCoordinator.ForceRefreshSelectedConfigAsync</c>, called from
    /// <c>DrawioEditor.ReloadPlantItemsAsync</c>) rereads the SELECTED device's own configuration
    /// alongside the plant, even though the selection itself never moved.
    /// </summary>
    [Fact]
    public async Task SwitchingModeUpdatesTheSelectedDevicesPanelSoASaveDoesNotRevertIt()
    {
        _host.Configs[A] = new PlantItemConfigDto
        {
            ItemId = A,
            Name = "Shuttle 1",
            DeviceType = (short)DeviceTypes.Automation,
            ConnectionType = (short)ConnectionTypes.Test,
        };
        var cut = RenderAuthenticated();

        // Select the device on the canvas - the same event the drawio bridge delivers. An Automation
        // device has a cells panel (CellPanelKind.LinkedCell): its three reads follow the config's and
        // land renders of their own, so the pipeline is waited for until it has settled, as in the two
        // tests below.
        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([A.ToString()]));
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("0", cut.Find("div.property-panel-field[data-property='ConnectionType'] select").GetAttribute("value"));
            Assert.Empty(cut.FindAll("#drawio-cells-loading"));
        });

        // Switch the whole plant's mode: the device currently selected is one of the ones affected.
        // Found and clicked in ONE turn of the renderer's dispatcher, as in the two tests below: a
        // render landing between a Find and its Click retires the handler the click carries. Found
        // and clicked apart, this line was red 2 runs in 5 of the whole suite (final review of rows
        // 22-29): the toggle's click lost, and no "#drawio-mode-confirm" to find.
        await cut.InvokeAsync(() => cut.Find("#drawio-mode-toggle").Click());
        await cut.InvokeAsync(() => cut.Find("#drawio-mode-confirm").Click());
        cut.WaitForAssertion(() => Assert.Equal(ConnectionTypes.Connected, Assert.Single(_host.ConnectionTypeWrites)));

        // The panel's OWN field must show the NEW value now - proof _selectedConfig was reread, not
        // left stale, even though the selection never moved. The forced reread reads the cells again
        // too: settled before the save is pressed, for the same reason.
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("1", cut.Find("div.property-panel-field[data-property='ConnectionType'] select").GetAttribute("value"));
            Assert.Empty(cut.FindAll("#drawio-cells-loading"));
        });

        // Saving now, with NOTHING else touched, must carry the NEW ConnectionType - the concrete
        // failure this bug produced: any save on this panel silently reverted the plant-wide switch.
        await cut.InvokeAsync(() => cut.Find("#property-panel-save-button").Click());

        cut.WaitForAssertion(() =>
        {
            var written = Assert.Single(_host.ConfigWrites);
            Assert.Equal((short)ConnectionTypes.Connected, written.ConnectionType);
        });
    }

    /// <summary>
    /// Fix round 2, IMPORTANT 1: the forced reread above discards whatever the operator had typed
    /// into the panel and not yet confirmed - right, but silent about it before this fix. Types into
    /// the Name field (never touched by the mode switch itself, so its host value is the one thing
    /// that PROVES a real reread happened and not just a coincidence of ConnectionType matching),
    /// switches the mode, and checks both that row 50's own outcome says so and that the field itself
    /// shows the host's value, not what was typed.
    /// </summary>
    [Fact]
    public async Task SwitchingModeAfterTypingIntoThePanelDiscardsItAndSaysSo()
    {
        _host.Configs[A] = new PlantItemConfigDto
        {
            ItemId = A,
            Name = "Shuttle 1",
            DeviceType = (short)DeviceTypes.Automation,
            ConnectionType = (short)ConnectionTypes.Test,
        };
        var cut = RenderAuthenticated();

        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([A.ToString()]));
        // Automation devices get CellPanelKind.LinkedCell (CellLinking.KindFor), not None: the
        // selection's pipeline reads the WMS cells AFTER the config, and the panel shows
        // "#drawio-cells-loading" until that second read lands too - typing into the Name field
        // before this whole pipeline has settled races a render this test does not control. Waited
        // for explicitly, not folded into the retried block below: a retry that keeps re-typing
        // while a loading placeholder is still swapping the surrounding markup in and out is racing
        // the same way, just with extra steps.
        cut.WaitForAssertion(() =>
        {
            Assert.NotEmpty(cut.FindAll("div.property-panel-field[data-property='Name'] input"));
            Assert.Empty(cut.FindAll("#drawio-cells-loading"));
        });
        // Typed and reasserted as ONE retried block, on top of the settle-wait above: some run of
        // this same pipeline still lands a render after the loading placeholder is gone (measured -
        // the settle-wait alone left roughly one run in five red), and retrying the type itself is
        // what survives whichever render that turns out to be.
        cut.WaitForAssertion(() =>
        {
            cut.Find("div.property-panel-field[data-property='Name'] input").Change("Typed but never confirmed");
            Assert.Equal("Typed but never confirmed",
                cut.Find("div.property-panel-field[data-property='Name'] input").GetAttribute("value"));
        });

        // Found and clicked in ONE turn of the renderer's dispatcher (compito 2, fix round 1): the
        // panel's pipeline can still land a render between a Find and its Click, and the click then
        // carries a handler id that render retired - measured on the commit before this one, 3 runs
        // in 15 of the whole suite red here, on the confirm button not being there.
        await cut.InvokeAsync(() => cut.Find("#drawio-mode-toggle").Click());
        await cut.InvokeAsync(() => cut.Find("#drawio-mode-confirm").Click());

        cut.WaitForAssertion(() => Assert.Contains(
            "The properties below have been read again from the host, so anything typed into them and not confirmed is gone.",
            cut.Find("#drawio-mode-outcome").TextContent));
        Assert.Equal("Shuttle 1", cut.Find("div.property-panel-field[data-property='Name'] input").GetAttribute("value"));
    }

    /// <summary>
    /// Final review, M2: the same gesture when the plant reread after the write FAILS. The page
    /// still clears and rereads the selected device's panel (ReloadPlantItemsAsync's finally), so
    /// what was typed is gone here too - and the line says so next to "WAS written".
    /// </summary>
    [Fact]
    public async Task AFailedPlantRereadStillSaysWhatWasTypedIsGone()
    {
        _host.Configs[A] = new PlantItemConfigDto
        {
            ItemId = A,
            Name = "Shuttle 1",
            DeviceType = (short)DeviceTypes.Automation,
            ConnectionType = (short)ConnectionTypes.Test,
        };
        var cut = RenderAuthenticated();

        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([A.ToString()]));
        cut.WaitForAssertion(() =>
        {
            Assert.NotEmpty(cut.FindAll("div.property-panel-field[data-property='Name'] input"));
            Assert.Empty(cut.FindAll("#drawio-cells-loading"));
        });
        cut.WaitForAssertion(() =>
        {
            cut.Find("div.property-panel-field[data-property='Name'] input").Change("Typed but never confirmed");
            Assert.Equal("Typed but never confirmed",
                cut.Find("div.property-panel-field[data-property='Name'] input").GetAttribute("value"));
        });

        // The next read of the plant's devices - the reread after the write - answers 500.
        _host.FailRoute = "items";
        await cut.InvokeAsync(() => cut.Find("#drawio-mode-toggle").Click());
        await cut.InvokeAsync(() => cut.Find("#drawio-mode-confirm").Click());

        cut.WaitForAssertion(() =>
        {
            var outcome = cut.Find("#drawio-mode-outcome").TextContent;
            Assert.Contains("The connection mode WAS written to Connected, but the plant could not be read again afterwards", outcome);
            Assert.Contains("anything typed into them and not confirmed is gone.", outcome);
        });
        Assert.Equal(ConnectionTypes.Connected, Assert.Single(_host.ConnectionTypeWrites));
        Assert.Equal("Shuttle 1", cut.Find("div.property-panel-field[data-property='Name'] input").GetAttribute("value"));
    }
}
