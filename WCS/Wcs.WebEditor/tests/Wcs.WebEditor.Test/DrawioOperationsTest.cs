using System.Net;
using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Drawio.Components;
using Wcs.WebEditor.Drawio.Coordination;
using Wcs.WebEditor.Live;
using Wcs.WebEditor.Model;
using Wcs.WebEditor.Session;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

/// <summary>
/// Task 1 (parity table rows 50, 55, 56, 57): the "Operations" section of the draw.io page. Row 50
/// (connection mode) uses this suite's own fake write-through-the-gate (<see cref="WriteThroughThePage"/>,
/// styled like ImportExportTest); rows 55-57 are plain commands like DrawioDeviceCommandsTest, and
/// indeed share the same DrawioCommandFollower.
/// </summary>
public sealed class DrawioOperationsTest : PlantCanvasBunitContext
{
    private static readonly Guid A = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
    private static readonly Guid B = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000002");

    private readonly DrawioWriteHandler _host = new();
    private readonly FakePlantEventSource _source = new();
    private readonly PlantLiveFeed _feed;
    private int _writeGateCalls;
    private int _modeWrittenCalls;

    public DrawioOperationsTest()
    {
        var state = new EditorSessionState(new Uri("http://localhost:5580"), _host);
        state.UseKey("test-key");
        Services.AddSingleton(state);
        _feed = new PlantLiveFeed(state.Client, _source, _ => DeviceTypes.Automation, new ManualTimeProvider());

        _host.Items =
        [
            new PlantItemDto(A, "Shuttle 1", DeviceTypes.Automation, null, null, null, ConnectionTypes.Test,
                default, 0, null, 0, 0, 0, 0, default!, 10, 10, 100, 100, 0),
            new PlantItemDto(B, "Shuttle 2", DeviceTypes.Automation, null, null, null, ConnectionTypes.Test,
                default, 0, null, 0, 0, 0, 0, default!, 200, 10, 100, 100, 0),
        ];
    }

    private static PlantGraph Graph() => new(
        [
            new PlantItemDto(A, "Shuttle 1", DeviceTypes.Automation, null, null, null, ConnectionTypes.Test,
                default, 0, null, 0, 0, 0, 0, default!, 10, 10, 100, 100, 0),
            new PlantItemDto(B, "Shuttle 2", DeviceTypes.Automation, null, null, null, ConnectionTypes.Test,
                default, 0, null, 0, 0, 0, 0, default!, 200, 10, 100, 100, 0),
        ], [], []);

    /// <summary>The page's own write-through-the-gate delegate, faked exactly like ImportExportTest's own: writes straight through, and counts calls.</summary>
    private Task<DrawioSaid?> WriteThroughThePage(Func<Task> write) => WriteThroughThePageAsync(write, null);

    private async Task<DrawioSaid?> WriteThroughThePageAsync(Func<Task> write, string? refusal)
    {
        _writeGateCalls++;
        if (refusal is not null) return refusal;
        await write();
        return null;
    }

    private IRenderedComponent<DrawioOperations> Render(PlantGraph? graph = null, NavigatorStatusTypes? navStatus = NavigatorStatusTypes.Run,
        Func<Func<Task>, Task<DrawioSaid?>>? writeThroughThePage = null, bool withFeed = true, Guid? selectedItemId = null, string? selectedItemName = null,
        Func<Task<DrawioModeReread>>? onModeWritten = null) =>
        Render<DrawioOperations>(parameters => parameters
            .Add(p => p.Graph, graph ?? Graph())
            .Add(p => p.Feed, withFeed ? _feed : null)
            .Add(p => p.NavigatorStatus, navStatus)
            .Add(p => p.SelectedItemId, selectedItemId)
            .Add(p => p.SelectedItemName, selectedItemName)
            .Add(p => p.WriteThroughThePage, writeThroughThePage ?? WriteThroughThePage)
            .Add(p => p.OnModeWritten, onModeWritten ?? (() =>
            {
                _modeWrittenCalls++;
                return Task.FromResult(new DrawioModeReread(false, null));
            })));

    [Fact]
    public void OperationsStartsOnConnectionModeWithFourAccessibleTabsAndNoResizeGrip()
    {
        var cut = Render(navStatus: NavigatorStatusTypes.Stop);
        var tabs = cut.FindAll("#drawio-operations-tabs > [role='tab']");

        Assert.Equal(["Connection mode", "Test Route", "Remove all LU", "Set all Ready"],
            tabs.Select(tab => tab.TextContent.Trim()));
        Assert.Equal(["true", "false", "false", "false"], tabs.Select(tab => tab.GetAttribute("aria-selected")));
        Assert.Equal(["0", "-1", "-1", "-1"], tabs.Select(tab => tab.GetAttribute("tabindex")));
        Assert.Equal("drawio-mode", tabs[0].GetAttribute("aria-controls"));
        Assert.Empty(cut.FindAll("#drawio-operations > .drawio-window-grip"));
        Assert.Single(cut.FindAll("#drawio-operations [role='tabpanel']:not([hidden])"));
        Assert.Equal("drawio-mode", cut.Find("#drawio-operations [role='tabpanel']:not([hidden])").Id);
        Assert.Equal(new DrawioWindowSize(560, 320), cut.FindComponent<DrawioWindow>().Instance.PreferredSize);
    }

    [Fact]
    public void ClickingTabsChangesThePanelAndItsPreferredSizeWithoutDiscardingPanelState()
    {
        var cut = Render(navStatus: NavigatorStatusTypes.Stop);

        cut.Find("#drawio-operations-tab-remove-all").Click();
        Assert.Equal("drawio-remove-all-lu", cut.Find("#drawio-operations [role='tabpanel']:not([hidden])").Id);
        Assert.Equal(new DrawioWindowSize(600, 360), cut.FindComponent<DrawioWindow>().Instance.PreferredSize);
        cut.Find("#drawio-remove-all-lu-button").Click();
        Assert.NotEmpty(cut.FindAll("#drawio-remove-all-lu-question"));

        cut.Find("#drawio-operations-tab-mode").Click();
        cut.Find("#drawio-operations-tab-remove-all").Click();

        Assert.NotEmpty(cut.FindAll("#drawio-remove-all-lu-question"));
    }

    [Theory]
    [InlineData("ArrowRight", "drawio-operations-tab-test-route", "drawio-test-route", 680, 500)]
    [InlineData("ArrowLeft", "drawio-operations-tab-set-ready", "drawio-set-all-ready", 560, 320)]
    [InlineData("End", "drawio-operations-tab-set-ready", "drawio-set-all-ready", 560, 320)]
    [InlineData("Home", "drawio-operations-tab-mode", "drawio-mode", 560, 320)]
    public void TabKeyboardNavigationSelectsAndSizesTheDestination(string key, string selectedId, string panelId, int width, int height)
    {
        var cut = Render(navStatus: NavigatorStatusTypes.Stop);

        cut.Find("#drawio-operations-tab-mode").KeyDown(new KeyboardEventArgs { Key = key });

        Assert.Equal("true", cut.Find($"#{selectedId}").GetAttribute("aria-selected"));
        Assert.Equal("0", cut.Find($"#{selectedId}").GetAttribute("tabindex"));
        Assert.Equal(panelId, cut.Find("#drawio-operations [role='tabpanel']:not([hidden])").Id);
        Assert.Equal(new DrawioWindowSize(width, height), cut.FindComponent<DrawioWindow>().Instance.PreferredSize);
    }

    /// <summary>
    /// The focus that follows a tab chosen from the keyboard is an interop call like any other: when the
    /// browser cannot take it (the element out of reach, the circuit going), the tab is selected all the
    /// same and the exception does not leave the handler - an unhandled one ends the circuit.
    /// </summary>
    [Theory]
    [InlineData(typeof(Microsoft.JSInterop.JSException))]
    [InlineData(typeof(Microsoft.JSInterop.JSDisconnectedException))]
    public void AFocusTheBrowserCannotTakeLeavesTheTabSelectedAndThrowsNothing(Type failure)
    {
        var cut = Render(navStatus: NavigatorStatusTypes.Stop);
        JSInterop.SetupVoid("Blazor._internal.domWrapper.focus", _ => true)
            .SetException((Exception)Activator.CreateInstance(failure, "focus failed")!);

        cut.Find("#drawio-operations-tab-mode").KeyDown(new KeyboardEventArgs { Key = "ArrowRight" });

        Assert.Equal("true", cut.Find("#drawio-operations-tab-test-route").GetAttribute("aria-selected"));
        Assert.Equal("drawio-test-route", cut.Find("#drawio-operations [role='tabpanel']:not([hidden])").Id);
    }

    [Fact]
    public void TheFourOperationsAreFunctionalCardsWithoutLosingTheirControls()
    {
        var cut = Render(navStatus: NavigatorStatusTypes.Stop);
        var cards = cut.FindAll(".drawio-operations-grid > .drawio-operational-card");

        Assert.Equal(
            ["drawio-mode", "drawio-test-route", "drawio-remove-all-lu", "drawio-set-all-ready"],
            cards.Select(card => card.Id));
        Assert.Contains("is-info", cards[0].ClassList);
        Assert.Contains("is-info", cards[1].ClassList);
        Assert.Contains("is-danger", cards[2].ClassList);
        Assert.Contains("is-safe", cards[3].ClassList);
        Assert.NotNull(cut.Find("#drawio-mode-toggle"));
        Assert.NotNull(cut.Find("#drawio-test-route-run"));
        Assert.NotNull(cut.Find("#drawio-remove-all-lu-button"));
        Assert.NotNull(cut.Find("#drawio-set-all-ready-button"));
        Assert.Equal("any", cut.Find("#drawio-test-route-lu-type").GetAttribute("step"));
    }

    // ---- Row 50: connection mode ----------------------------------------------------------

    [Fact]
    public void TheCurrentModeIsShown()
    {
        var cut = Render();

        Assert.Equal("Mode: Test", cut.Find("#drawio-mode-state").TextContent.Trim());
    }

    [Fact]
    public void SwitchingModeAsksFirst()
    {
        var cut = Render();

        cut.Find("#drawio-mode-toggle").Click();

        Assert.Empty(_host.ConnectionTypeWrites);
        Assert.NotEmpty(cut.FindAll("#drawio-mode-question"));
    }

    [Fact]
    public void TheConfirmationCanBeTakenBack()
    {
        var cut = Render();

        cut.Find("#drawio-mode-toggle").Click();
        cut.Find("#drawio-mode-cancel").Click();

        Assert.Empty(cut.FindAll("#drawio-mode-question"));
        Assert.Empty(_host.ConnectionTypeWrites);
    }

    /// <summary>The write goes through the page's own gate (WriteThroughThePage), not straight to the client.</summary>
    [Fact]
    public void ConfirmingSwitchesTheModeThroughThePagesGate()
    {
        var cut = Render();

        cut.Find("#drawio-mode-toggle").Click();
        cut.Find("#drawio-mode-confirm").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(1, _writeGateCalls);
            Assert.Equal(ConnectionTypes.Connected, Assert.Single(_host.ConnectionTypeWrites));
        });
    }

    /// <summary>
    /// Fix round 1, MINOR 11 / fix round 2, MINOR 5 (renamed to match what it actually asserts - the
    /// previous name, "the label changes only after acceptance and rereads the plant", promised a
    /// claim about the LABEL that this fake cannot make good on: it never rewrites Graph, so the
    /// label stays "Mode: Test" throughout, on purpose - see the assertion below and
    /// DrawioEditorModeSwitchTest for the label actually following a REAL reread). What this test
    /// verifies: the reread callback (OnModeWritten) runs exactly once, and the outcome line reports
    /// acceptance.
    /// </summary>
    [Fact]
    public void ConfirmingCallsTheRereadOnceAndTheOutcomeSaysAccepted()
    {
        var cut = Render();
        Assert.Equal("Mode: Test", cut.Find("#drawio-mode-state").TextContent.Trim());
        cut.Find("#drawio-mode-toggle").Click();

        cut.Find("#drawio-mode-confirm").Click();

        cut.WaitForAssertion(() => Assert.Equal(1, _modeWrittenCalls));
        cut.WaitForAssertion(() => Assert.Contains("set to Connected", cut.Find("#drawio-mode-outcome").TextContent));
        // This fake's Graph is never actually rewritten by OnModeWritten (unlike the real page's -
        // DrawioEditorModeSwitchTest proves the real one flips), so the label staying "Mode: Test"
        // here is the correct assertion for THIS test: it is testing that the outcome line and the
        // reread call both happened, not that the label followed a reread this fake does not perform.
        Assert.Equal("Mode: Test", cut.Find("#drawio-mode-state").TextContent.Trim());
    }

    /// <summary>
    /// Fix round 2, IMPORTANT 1: when the reread (OnModeWritten) answers true - a device's own panel
    /// WAS cleared and reread - the outcome line adds the same discard warning the cell-linking
    /// gestures already give (DrawioSelectionCoordinator.LinkCellCoreAsync/UnlinkCellCoreAsync), not
    /// silence about something the operator may have half-typed into that panel.
    /// </summary>
    [Fact]
    public void ConfirmingWhenAPanelWasRereadAddsTheDiscardWarning()
    {
        var cut = Render(onModeWritten: () => Task.FromResult(new DrawioModeReread(true, null)));

        cut.Find("#drawio-mode-toggle").Click();
        cut.Find("#drawio-mode-confirm").Click();

        cut.WaitForAssertion(() => Assert.Contains(
            "The properties below have been read again from the host, so anything typed into them and not confirmed is gone.",
            cut.Find("#drawio-mode-outcome").TextContent));
    }

    /// <summary>Mirror of the test above: no device was selected (OnModeWritten answers false), so no panel was touched and the warning does not appear.</summary>
    [Fact]
    public void ConfirmingWithoutASelectedDeviceAddsNoDiscardWarning()
    {
        var cut = Render(onModeWritten: () => Task.FromResult(new DrawioModeReread(false, null)));

        cut.Find("#drawio-mode-toggle").Click();
        cut.Find("#drawio-mode-confirm").Click();

        cut.WaitForAssertion(() => Assert.Contains("set to Connected", cut.Find("#drawio-mode-outcome").TextContent));
        Assert.DoesNotContain("read again from the host", cut.Find("#drawio-mode-outcome").TextContent);
    }

    /// <summary>Fix round 1, MINOR 11: a refusal must leave the label exactly where it was - untouched, like the write itself.</summary>
    [Fact]
    public void TheLabelIsUnchangedAfterARefusal()
    {
        var cut = Render(writeThroughThePage: write => WriteThroughThePageAsync(write, "somebody else is writing"));

        cut.Find("#drawio-mode-toggle").Click();
        cut.Find("#drawio-mode-confirm").Click();

        cut.WaitForAssertion(() => Assert.Contains("somebody else is writing", cut.Find("#drawio-mode-outcome").TextContent));
        Assert.Equal("Mode: Test", cut.Find("#drawio-mode-state").TextContent.Trim());
    }

    /// <summary>A refusal from the page's gate (permit denied, another writer) shows the reason and writes nothing.</summary>
    [Fact]
    public void ARefusalFromTheGateWritesNothing()
    {
        var cut = Render(writeThroughThePage: write => WriteThroughThePageAsync(write, "somebody else is writing"));

        cut.Find("#drawio-mode-toggle").Click();
        cut.Find("#drawio-mode-confirm").Click();

        cut.WaitForAssertion(() => Assert.Equal("somebody else is writing", cut.Find("#drawio-mode-outcome").TextContent));
        Assert.Empty(_host.ConnectionTypeWrites);
        Assert.Equal(0, _modeWrittenCalls);
    }

    /// <summary>
    /// Fix round 1, IMPORTANT 4: while the engine is Run, the page's OWN read-only gate
    /// (EnsureWritableAsync) refuses before this route's specific 409 is ever reached - proven end to
    /// end against the real page in DrawioEditorModeSwitchTest. Against THIS fake gate (which just
    /// writes straight through, unlike the real one), the write reaches the fake host, whose
    /// connection-type route answers 409 when NavStatus is Run exactly like
    /// PlantQueries.SetConnectionTypeForAllAsync/RequireNotRunning - and THAT is what this test
    /// measures: the clean-refusal classification of the write's own exception.
    /// </summary>
    [Fact]
    public void ARefusalBecauseTheEngineIsRunningIsShown()
    {
        _host.NavStatus = NavigatorStatusTypes.Run;
        var cut = Render();

        cut.Find("#drawio-mode-toggle").Click();
        cut.Find("#drawio-mode-confirm").Click();

        // Not the client's raw "L'API del motore ha risposto 409: {...}" text (final review), and not
        // the host's own words in the sentence either: they are behind its "?" with the rest of the
        // answer (row 37 of the queue, extended to the whole page on 10/10).
        cut.WaitForAssertion(() => Assert.Equal(
            "Connection mode was NOT written: the host refused it - the plant's state does not allow it now.",
            cut.Find("#drawio-mode-outcome").TextContent));
        Assert.Equal("the host answered 409: {\"title\":\"Il motore non \\u00E8 fermo\"}",
            DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-mode-outcome"));
        Assert.Empty(_host.ConnectionTypeWrites);
    }

    /// <summary>Fix round 1, IMPORTANT 4: a transport failure on the write itself is an UNKNOWN outcome, not a clean "NOT written" - the request may have reached the host anyway.</summary>
    [Fact]
    public void ATransportFailureOnTheWriteIsAnUnknownOutcome()
    {
        _host.HttpFailureRoute = "connection-type";
        var cut = Render();

        cut.Find("#drawio-mode-toggle").Click();
        cut.Find("#drawio-mode-confirm").Click();

        cut.WaitForAssertion(() => Assert.Contains("not known whether the connection mode was written", cut.Find("#drawio-mode-outcome").TextContent));
        // The why in the page's words, the transport's own message behind the line's "?" (row 37 of
        // the queue, extended to the whole page on 10/10).
        Assert.Equal("It is not known whether the connection mode was written, because the connection to the host failed: check the plant before trying again.",
            cut.Find("#drawio-mode-outcome").TextContent);
        Assert.Equal("HttpRequestException: Il trasporto su 'connection-type' e' fallito (Unknown) (prova).",
            DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-mode-outcome"));
    }

    /// <summary>Fix round 1, IMPORTANT 4: a write that goes through but whose reread then fails is reported as WRITTEN, not refused.</summary>
    [Fact]
    public void AWriteThatSucceedsButCannotBeRereadSaysSoAndNotNOTWritten()
    {
        // The write itself goes through the ordinary fake gate; the reread (OnModeWritten, invoked by
        // DrawioOperations AFTER the write already succeeded) is what is made to fail here - the only
        // way to land the failure exactly where a real reread (PlantGraph.LoadAsync) could land one.
        var cut = Render(onModeWritten: () => throw new HttpRequestException("reread failed"));

        cut.Find("#drawio-mode-toggle").Click();
        cut.Find("#drawio-mode-confirm").Click();

        cut.WaitForAssertion(() =>
        {
            var outcome = cut.Find("#drawio-mode-outcome").TextContent;
            Assert.Contains("WAS written", outcome);
            Assert.Contains("could not be read again", outcome);
            Assert.DoesNotContain("NOT written", outcome);
        });
        Assert.Equal(ConnectionTypes.Connected, Assert.Single(_host.ConnectionTypeWrites));
    }

    /// <summary>
    /// Fix round 1, IMPORTANT 4: JsonException used to escape ConfirmModeAsync's catch entirely (only
    /// HttpRequestException/WcsApiException/TaskCanceledException were caught) - it is now treated
    /// the same as a transport failure. Thrown straight from the fake gate: the real route this write
    /// calls (POST /plant/items/connection-type) answers 204 with no body on success and so never
    /// actually triggers a JsonException of its own (confirmed against WcsClient.EnsureAsync, which
    /// parses nothing on success) - the reachable case is a REREAD that returns garbled JSON
    /// (PlantGraph.LoadAsync's GetItemsAsync, tested via onModeWritten in the two tests above and
    /// below), and this test only needs to prove the exception TYPE is caught at all, wherever it
    /// happens to come from.
    /// </summary>
    [Fact]
    public void AJsonExceptionOnTheWriteIsAnUnknownOutcomeToo()
    {
        var cut = Render(writeThroughThePage: _ => throw new JsonException("garbled response"));

        cut.Find("#drawio-mode-toggle").Click();
        cut.Find("#drawio-mode-confirm").Click();

        cut.WaitForAssertion(() => Assert.Contains("not known whether the connection mode was written", cut.Find("#drawio-mode-outcome").TextContent));
    }

    /// <summary>The reread-side twin of the test above: a JsonException from OnModeWritten is reported as written, not refused - same as any other caught exception type there.</summary>
    [Fact]
    public void AJsonExceptionOnTheRereadSaysWrittenNotRefused()
    {
        var cut = Render(onModeWritten: () => throw new JsonException("garbled response"));

        cut.Find("#drawio-mode-toggle").Click();
        cut.Find("#drawio-mode-confirm").Click();

        cut.WaitForAssertion(() =>
        {
            var outcome = cut.Find("#drawio-mode-outcome").TextContent;
            Assert.Contains("WAS written", outcome);
            Assert.DoesNotContain("NOT written", outcome);
        });
    }

    /// <summary>
    /// Final review, M2: the plant reread after the write failed, and the panel of the selected
    /// device was still cleared and read again (the page does that in a finally) - so whatever was
    /// typed there is gone, and the line says it on this path too, not only on the successful one.
    /// </summary>
    [Fact]
    public void AFailedRereadThatStillClearedThePanelSaysWhatWasTypedIsGone()
    {
        var cut = Render(onModeWritten: () => Task.FromResult(new DrawioModeReread(true, new HttpRequestException("reread failed"))));

        cut.Find("#drawio-mode-toggle").Click();
        cut.Find("#drawio-mode-confirm").Click();

        cut.WaitForAssertion(() => Assert.Equal(
            "The connection mode WAS written to Connected, but the plant could not be read again afterwards because the connection to the host failed: reload the page before doing anything else. " +
            "The properties below were cleared to be read again from the host, so anything typed into them and not confirmed is gone.",
            cut.Find("#drawio-mode-outcome").TextContent));
        Assert.Equal("HttpRequestException: reread failed", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-mode-outcome"));
    }

    /// <summary>The same failed reread with no device selected: no panel was touched, and the line does not claim one was.</summary>
    [Fact]
    public void AFailedRereadWithoutASelectedDeviceSaysNothingAboutThePanel()
    {
        var cut = Render(onModeWritten: () => Task.FromResult(new DrawioModeReread(false, new HttpRequestException("reread failed"))));

        cut.Find("#drawio-mode-toggle").Click();
        cut.Find("#drawio-mode-confirm").Click();

        cut.WaitForAssertion(() => Assert.Contains("WAS written", cut.Find("#drawio-mode-outcome").TextContent));
        Assert.DoesNotContain("typed", cut.Find("#drawio-mode-outcome").TextContent);
    }

    /// <summary>
    /// Final review, M3: without the page's way through its write gate and edit permit the section
    /// does not write straight to the host - nothing is written, and it says so.
    /// </summary>
    [Fact]
    public void WithoutThePagesWayThroughTheGateTheModeIsNotWritten()
    {
        var cut = Render<DrawioOperations>(parameters => parameters
            .Add(p => p.Graph, Graph())
            .Add(p => p.Feed, _feed)
            .Add(p => p.NavigatorStatus, NavigatorStatusTypes.Stop)
            .Add(p => p.OnModeWritten, () =>
            {
                _modeWrittenCalls++;
                return Task.FromResult(new DrawioModeReread(false, null));
            }));

        cut.Find("#drawio-mode-toggle").Click();
        cut.Find("#drawio-mode-confirm").Click();

        cut.WaitForAssertion(() => Assert.Equal(
            "Connection mode was NOT written: this section was not given the page's way through its write gate and edit permit, and it does not write around them.",
            cut.Find("#drawio-mode-outcome").TextContent));
        Assert.Empty(_host.ConnectionTypeWrites);
        Assert.Equal(0, _modeWrittenCalls);
    }

    [Fact]
    public void WithoutAPlantTheToggleIsDisabled()
    {
        var cut = Render<DrawioOperations>(parameters => parameters
            .Add(p => p.Graph, (PlantGraph?)null)
            .Add(p => p.Feed, _feed)
            .Add(p => p.NavigatorStatus, NavigatorStatusTypes.Run)
            .Add(p => p.WriteThroughThePage, WriteThroughThePage)
            .Add(p => p.OnModeWritten, () =>
            {
                _modeWrittenCalls++;
                return Task.FromResult(new DrawioModeReread(false, null));
            }));

        Assert.True(cut.Find("#drawio-mode-toggle").HasAttribute("disabled"));
    }

    // ---- Row 55: Test Route -----------------------------------------------------------------

    [Fact]
    public void TheRunButtonIsDisabledUntilSourceAndDestinationAreChosen()
    {
        var cut = Render();

        Assert.True(cut.Find("#drawio-test-route-run").HasAttribute("disabled"));
    }

    [Fact]
    public void TheRunButtonIsDisabledUnlessTheEngineIsRunning()
    {
        var cut = Render(navStatus: NavigatorStatusTypes.Stop);
        ChooseSourceAndDestination(cut);

        Assert.True(cut.Find("#drawio-test-route-run").HasAttribute("disabled"));
    }

    /// <summary>
    /// Fix round 1, IMPORTANT 2: source and destination come from the device SELECTED ON THE DRAWING
    /// (SelectedItemId), through "use selected as source/destination" - not a filterable list, which
    /// was this section's own first (and wrong) reading of the WinForms cmbORDINE. Simulated here by
    /// re-rendering with a different SelectedItemId between the two clicks, exactly as a real
    /// selection change on the canvas would arrive as a new parameter.
    /// </summary>
    private static void ChooseSourceAndDestination(IRenderedComponent<DrawioOperations> cut)
    {
        cut.Render(p => p.Add(x => x.SelectedItemId, A).Add(x => x.SelectedItemName, "Shuttle 1"));
        cut.Find("#drawio-test-route-use-source").Click();
        cut.Render(p => p.Add(x => x.SelectedItemId, B).Add(x => x.SelectedItemName, "Shuttle 2"));
        cut.Find("#drawio-test-route-use-destination").Click();
    }

    [Fact]
    public void ChoosingSourceAndDestinationShowsThem()
    {
        var cut = Render();

        ChooseSourceAndDestination(cut);

        Assert.Equal("Source: Shuttle 1", cut.Find("#drawio-test-route-source-chosen").TextContent.Trim());
        Assert.Equal("Destination: Shuttle 2", cut.Find("#drawio-test-route-destination-chosen").TextContent.Trim());
    }

    [Fact]
    public void TheUseButtonsAreDisabledWithoutACanvasSelection()
    {
        var cut = Render();

        Assert.True(cut.Find("#drawio-test-route-use-source").HasAttribute("disabled"));
        Assert.True(cut.Find("#drawio-test-route-use-destination").HasAttribute("disabled"));
    }

    /// <summary>
    /// Fix round 1, IMPORTANT 2: the same device used as BOTH ends clears the opposite role - the
    /// rule DiagramHandler.EditAutomationDevice applies from cmbORDINE on the WinForms device dialog
    /// (case 1/2 there), reproduced here from the canvas selection instead.
    /// </summary>
    [Fact]
    public void UsingTheSameDeviceForTheOtherRoleClearsIt()
    {
        var cut = Render(selectedItemId: A, selectedItemName: "Shuttle 1");
        cut.Find("#drawio-test-route-use-source").Click();
        Assert.Equal("Source: Shuttle 1", cut.Find("#drawio-test-route-source-chosen").TextContent.Trim());

        // The SAME device, still selected, used as destination now: it takes over there, and the
        // source it used to hold is cleared - never left pointing at the same device as both ends.
        cut.Find("#drawio-test-route-use-destination").Click();

        Assert.Equal("Destination: Shuttle 1", cut.Find("#drawio-test-route-destination-chosen").TextContent.Trim());
        Assert.Equal("Source: (none chosen)", cut.Find("#drawio-test-route-source-chosen").TextContent.Trim());
        Assert.True(cut.Find("#drawio-test-route-run").HasAttribute("disabled"));
    }

    [Fact]
    public void RunningTheTestRouteAsksFirst()
    {
        var cut = Render();
        ChooseSourceAndDestination(cut);

        cut.Find("#drawio-test-route-run").Click();

        Assert.Empty(_host.Commands);
        Assert.NotEmpty(cut.FindAll("#drawio-test-route-question"));
    }

    [Fact]
    public void ConfirmingSendsTheTestRouteCommandWithSourceDestinationAndLuType()
    {
        var cut = Render();
        ChooseSourceAndDestination(cut);
        cut.Find("#drawio-test-route-lu-type").Input("7");

        cut.Find("#drawio-test-route-run").Click();
        cut.Find("#drawio-test-route-confirm").Click();

        cut.WaitForAssertion(() =>
        {
            var sent = Assert.Single(_host.Commands);
            Assert.Equal((int)CommandTypes.TestRoute, sent.CommandType);
            Assert.Equal(A, sent.ItemId);
            Assert.Equal(B, sent.DestinationItemId);
            Assert.Equal(7m, sent.LoadUnitTypeId);
        });
    }

    /// <summary>Fix round 1, MINOR 9: "1.5" parses to one-and-a-half regardless of the server's current culture - InvariantCulture, a period always.</summary>
    [Fact]
    public void TheLuIdTypeAcceptsADecimalPointRegardlessOfCulture()
    {
        var cut = Render();
        ChooseSourceAndDestination(cut);
        cut.Find("#drawio-test-route-lu-type").Input("1.5");

        cut.Find("#drawio-test-route-run").Click();
        cut.Find("#drawio-test-route-confirm").Click();

        cut.WaitForAssertion(() => Assert.Equal(1.5m, Assert.Single(_host.Commands).LoadUnitTypeId));
    }

    /// <summary>Fix round 1, MINOR 9: invalid text is said to be invalid, not silently treated as zero - and blocks the run.</summary>
    [Fact]
    public void AnInvalidLuIdTypeIsSaidInvalidAndBlocksTheRun()
    {
        var cut = Render();
        ChooseSourceAndDestination(cut);

        cut.Find("#drawio-test-route-lu-type").Input("not a number");

        Assert.NotEmpty(cut.FindAll("#drawio-test-route-lu-type-error"));
        Assert.True(cut.Find("#drawio-test-route-run").HasAttribute("disabled"));
    }

    [Fact]
    public async Task TheTestRouteOutcomeFollows()
    {
        await _feed.StartAsync();
        var cut = Render();
        ChooseSourceAndDestination(cut);
        cut.Find("#drawio-test-route-run").Click();
        cut.Find("#drawio-test-route-confirm").Click();
        cut.WaitForAssertion(() => Assert.Contains("command 1", cut.Find("#drawio-test-route-outcome").TextContent));

        _source.RaiseCommand(new CommandDto(1, DateTime.UtcNow, (int)CommandTypes.TestRoute, A, null, null, (short)2,
            null, null, null, 0, null, null, 0m, null, 0m, null, B, null, true, 0));

        cut.WaitForAssertion(() => Assert.Contains("was executed", cut.Find("#drawio-test-route-outcome").TextContent));
    }

    /// <summary>
    /// Fix round 1, IMPORTANT 5: the Subject is frozen at the moment of the send - changing the
    /// pickers AFTERWARDS (a new selection on the canvas, "use selected" pressed again) must not
    /// relabel a route already sent and still being followed.
    /// </summary>
    [Fact]
    public async Task TheOutcomeKeepsNamingTheRouteActuallySentEvenIfThePickersChangeAfterwards()
    {
        await _feed.StartAsync();
        var cut = Render();
        ChooseSourceAndDestination(cut);
        cut.Find("#drawio-test-route-run").Click();
        cut.Find("#drawio-test-route-confirm").Click();
        cut.WaitForAssertion(() => Assert.Contains("Shuttle 1 -> Shuttle 2", cut.Find("#drawio-test-route-outcome").TextContent));

        // The pickers move on to a THIRD device for the source, while command 1 is still "waiting for
        // the engine": Subject itself may still get reassigned by a parameter set from the parent
        // (OnParametersSet runs on ANY of this page's frequent re-renders, not only ones about Test
        // Route) - what must NOT happen is the NEXT outcome text produced from it (below) naming the
        // new choice instead of the route actually sent.
        var c = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000003");
        _host.Items.Add(new PlantItemDto(c, "Shuttle 3", DeviceTypes.Automation, null, null, null, ConnectionTypes.Test,
            default, 0, null, 0, 0, 0, 0, default!, 400, 10, 100, 100, 0));
        var graphWithC = new PlantGraph([.. _host.Items], [], []);
        cut.Render(p => p
            .Add(x => x.Graph, graphWithC)
            .Add(x => x.SelectedItemId, c).Add(x => x.SelectedItemName, "Shuttle 3"));
        cut.Find("#drawio-test-route-use-source").Click();
        // A parameter set from the parent AFTER the picker's own new choice is already in place -
        // OnParametersSet only runs on a parameter change from the parent, never on the plain local
        // click just above, so this second Render is what actually reaches the mislabelling window.
        cut.Render(p => p.Add(x => x.Graph, graphWithC).Add(x => x.SelectedItemId, c).Add(x => x.SelectedItemName, "Shuttle 3"));

        // The live outcome for command 1 arrives NOW: this is what actually reads Subject to build
        // its text (OutcomeFor), so it is the only way to observe whether Subject was corrupted.
        _source.RaiseCommand(new CommandDto(1, DateTime.UtcNow, (int)CommandTypes.TestRoute, A, null, null, (short)2,
            null, null, null, 0, null, null, 0m, null, 0m, null, B, null, true, 0));

        cut.WaitForAssertion(() => Assert.Contains("was executed", cut.Find("#drawio-test-route-outcome").TextContent));
        Assert.Contains("Shuttle 1 -> Shuttle 2", cut.Find("#drawio-test-route-outcome").TextContent);
        Assert.DoesNotContain("Shuttle 3", cut.Find("#drawio-test-route-outcome").TextContent);
    }

    [Fact]
    public void ARefusedTestRouteSaysNothingWasSent()
    {
        _host.CommandRefusal = HttpStatusCode.Conflict;
        var cut = Render();
        ChooseSourceAndDestination(cut);

        cut.Find("#drawio-test-route-run").Click();
        cut.Find("#drawio-test-route-confirm").Click();

        cut.WaitForAssertion(() => Assert.Contains("was NOT sent", cut.Find("#drawio-test-route-outcome").TextContent));
    }

    /// <summary>
    /// Final review, I1, on Test Route - where a wrong "NOT sent" costs the most: a second press
    /// queues a second mission. Only the host's own refusal (4xx) is "NOT sent"; an unreadable 200 is
    /// "queued"; no answer, or a 5xx, is "NOT known" (DrawioDeviceCommandsTest has the same three).
    /// Row 37 of the queue (the owner's answer of 9/10): one sentence, and what came back behind the
    /// line's "?" - here as in the properties panel, the follower is the same.
    /// </summary>
    [Theory]
    [InlineData("409", "Test Route was NOT sent to Shuttle 1 -> Shuttle 2 because the host refused it.",
        "the host answered 409: {\"title\":\"Il motore non \\u00E8 in Run\",\"status\":409}")]
    [InlineData("garbled", "Test Route was queued for Shuttle 1 -> Shuttle 2, but the host's answer could not be read: its id is not known and its outcome cannot be followed here - check before sending it again.",
        "JsonException: ")]
    [InlineData("timeout", "It is NOT known whether Test Route was queued for Shuttle 1 -> Shuttle 2, because the host did not answer in time - check before sending it again.",
        "TaskCanceledException: ")]
    [InlineData("transport", "It is NOT known whether Test Route was queued for Shuttle 1 -> Shuttle 2, because the connection to the host failed - check before sending it again.",
        "HttpRequestException: ")]
    [InlineData("503", "It is NOT known whether Test Route was queued for Shuttle 1 -> Shuttle 2, because the host answered with an error - check before sending it again.",
        "the host answered 503 with an empty body")]
    public void AFailedTestRouteSaysWhatIsKnownAboutIt(string failure, string expected, string detailStart)
    {
        switch (failure)
        {
            case "409":
                _host.FailRoute = "command-post";
                _host.FailureStatus = HttpStatusCode.Conflict;
                _host.FailureBody = JsonSerializer.Serialize(new { title = "Il motore non è in Run", status = 409 });
                break;
            case "garbled": _host.GarbledRoute = "command-post"; break;
            case "timeout": _host.TimeoutRoute = "command-post"; break;
            case "transport": _host.HttpFailureRoute = "command-post"; break;
            default:
                _host.FailRoute = "command-post";
                _host.FailureStatus = HttpStatusCode.ServiceUnavailable;
                break;
        }

        var cut = Render();
        ChooseSourceAndDestination(cut);
        cut.Find("#drawio-test-route-run").Click();
        cut.Find("#drawio-test-route-confirm").Click();

        cut.WaitForAssertion(() => Assert.DoesNotContain("Sending", cut.Find("#drawio-test-route-outcome").TextContent));
        Assert.Equal(expected, cut.Find("#drawio-test-route-outcome").TextContent.Trim());
        Assert.StartsWith(detailStart, DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-test-route-outcome"));
    }

    /// <summary>Fix round 1, IMPORTANT 3: the button itself is disabled while its own send is in flight, on top of the follower's own re-entrancy guard (DrawioCommandFollowerTest).</summary>
    [Fact]
    public void TheRunButtonIsDisabledWhileSending()
    {
        _host.CommandPostGate = new TaskCompletionSource();
        var cut = Render();
        ChooseSourceAndDestination(cut);
        cut.Find("#drawio-test-route-run").Click();
        cut.Find("#drawio-test-route-confirm").Click();

        cut.WaitForAssertion(() => Assert.True(cut.Find("#drawio-test-route-run").HasAttribute("disabled")));
    }

    // ---- Row 56: Remove All LU ---------------------------------------------------------------

    /// <summary>
    /// Rendered with the engine Stopped (owner's decision, 27/9: Remove All LU is now enabled only in
    /// Stop or Pause, like the WinForms - <see cref="RemoveAllLuIsDisabledInEveryStateButStopAndPause"/> etc. below cover the
    /// gate itself). This test used to run under the default Render()'s NavigatorStatus (Run), which
    /// encoded the OLD, unconditional behaviour; it is pinned to Stop here instead of relying on the
    /// default so it keeps testing "asks first, then sends" regardless of what the default becomes.
    /// </summary>
    [Fact]
    public void RemoveAllLuAsksFirstThenSendsTheCommand()
    {
        var cut = Render(navStatus: NavigatorStatusTypes.Stop);

        cut.Find("#drawio-remove-all-lu-button").Click();
        Assert.Empty(_host.Commands);
        // Its Yes removes every load unit of the plant: red, as the button that asked (passata grafica).
        Assert.Contains("drawio-btn-danger", cut.Find("#drawio-remove-all-lu-confirm").ClassList);

        cut.Find("#drawio-remove-all-lu-confirm").Click();

        cut.WaitForAssertion(() => Assert.Equal((int)CommandTypes.RemoveAllLu, Assert.Single(_host.Commands).CommandType));
    }

    [Fact]
    public async Task RemoveAllLuOutcomeFollows()
    {
        await _feed.StartAsync();
        var cut = Render(navStatus: NavigatorStatusTypes.Stop);
        cut.Find("#drawio-remove-all-lu-button").Click();
        cut.Find("#drawio-remove-all-lu-confirm").Click();
        cut.WaitForAssertion(() => Assert.Contains("command 1", cut.Find("#drawio-remove-all-lu-outcome").TextContent));

        _source.RaiseCommand(new CommandDto(1, DateTime.UtcNow, (int)CommandTypes.RemoveAllLu, null, null, null, (short)2,
            null, null, null, 0, null, null, 0m, null, 0m, null, null, null, true, 0));

        cut.WaitForAssertion(() => Assert.Contains("was executed", cut.Find("#drawio-remove-all-lu-outcome").TextContent));
    }

    [Fact]
    public void WithoutAnyDeviceRemoveAllLuIsDisabled()
    {
        var cut = Render(graph: new PlantGraph([], [], []), navStatus: NavigatorStatusTypes.Stop);

        Assert.True(cut.Find("#drawio-remove-all-lu-button").HasAttribute("disabled"));
    }

    /// <summary>Fix round 1, IMPORTANT 3: disabled while its own send is in flight.</summary>
    [Fact]
    public void TheRemoveAllLuButtonIsDisabledWhileSending()
    {
        _host.CommandPostGate = new TaskCompletionSource();
        var cut = Render(navStatus: NavigatorStatusTypes.Stop);
        cut.Find("#drawio-remove-all-lu-button").Click();
        cut.Find("#drawio-remove-all-lu-confirm").Click();

        cut.WaitForAssertion(() => Assert.True(cut.Find("#drawio-remove-all-lu-button").HasAttribute("disabled")));
    }

    /// <summary>
    /// Owner's decision (27/9), row 56: enabled only with the engine in Stop or Pause, like the WinForms
    /// button (<c>DiagramHandler.Private.cs:1117,1120</c>). Disabled in every other state, with the
    /// reason next to the button.
    /// </summary>
    [Theory]
    [InlineData(NavigatorStatusTypes.Run)]
    [InlineData(NavigatorStatusTypes.Recover)]
    [InlineData(NavigatorStatusTypes.Dispose)]
    [InlineData(null)]
    public void RemoveAllLuIsDisabledInEveryStateButStopAndPause(NavigatorStatusTypes? navStatus)
    {
        var cut = Render(navStatus: navStatus);

        Assert.True(cut.Find("#drawio-remove-all-lu-button").HasAttribute("disabled"));
        Assert.Contains("Available with the engine stopped or paused", cut.Find("#drawio-remove-all-lu-reason").TextContent);
    }

    [Theory]
    [InlineData(NavigatorStatusTypes.Stop)]
    [InlineData(NavigatorStatusTypes.Pause)]
    public void RemoveAllLuIsEnabledInStopAndInPause(NavigatorStatusTypes navStatus)
    {
        var cut = Render(navStatus: navStatus);

        Assert.False(cut.Find("#drawio-remove-all-lu-button").HasAttribute("disabled"));
        Assert.Empty(cut.FindAll("#drawio-remove-all-lu-reason"));
    }

    /// <summary>
    /// Owner's decision (27/9): the confirmation can stay open across a render in which the engine
    /// leaves Stop/Pause (started from elsewhere while the operator was still looking at the question).
    /// Pressing "Yes" then must not send anything, and must say why - re-checked at that moment, not
    /// only through the button's own (bypassed, here) disabled attribute.
    /// </summary>
    [Fact]
    public void IfTheEngineStartsWhileConfirmingRemoveAllLuYesSendsNothing()
    {
        var cut = Render(navStatus: NavigatorStatusTypes.Stop);
        cut.Find("#drawio-remove-all-lu-button").Click();

        cut.Render(parameters => parameters.Add(p => p.NavigatorStatus, NavigatorStatusTypes.Run));

        cut.Find("#drawio-remove-all-lu-confirm").Click();

        Assert.Empty(_host.Commands);
        Assert.Contains("was NOT sent", cut.Find("#drawio-remove-all-lu-outcome").TextContent);
    }

    /// <summary>
    /// Row 37 of the queue (the owner's answer of 9/10): Remove All LU's outcome is the same follower's
    /// line as the panel's commands, and says a failure the same way - one sentence, and the host's
    /// answer as it came behind the line's "?", where the sentence used to quote its words.
    /// </summary>
    [Fact]
    public void ARemoveAllLuTheHostRefusesIsSaidInOneSentenceWithTheHostsAnswerBehindTheQuestionMark()
    {
        _host.FailRoute = "command-post";
        _host.FailureStatus = HttpStatusCode.Conflict;
        _host.FailureBody = JsonSerializer.Serialize(new { title = "Il motore non è fermo", status = 409 });
        var cut = Render(navStatus: NavigatorStatusTypes.Stop);

        cut.Find("#drawio-remove-all-lu-button").Click();
        cut.Find("#drawio-remove-all-lu-confirm").Click();

        cut.WaitForAssertion(() => Assert.Equal("Remove All LU was NOT sent to the plant because the host refused it.",
            cut.Find("#drawio-remove-all-lu-outcome").TextContent.Trim()));
        Assert.Equal("the host answered 409: {\"title\":\"Il motore non \\u00E8 fermo\",\"status\":409}",
            DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-remove-all-lu-outcome"));
    }

    // ---- Row 57: Set All Ready ----------------------------------------------------------------

    /// <summary>The same for Set All Ready, with no answer at all: the why in the sentence, the failure behind the "?".</summary>
    [Fact]
    public void ASetAllReadyThatGetsNoAnswerIsSaidInOneSentenceWithTheFailureBehindTheQuestionMark()
    {
        _host.HttpFailureRoute = "command-post";
        var cut = Render();

        cut.Find("#drawio-set-all-ready-button").Click();
        cut.Find("#drawio-set-all-ready-confirm").Click();

        cut.WaitForAssertion(() => Assert.Equal(
            "It is NOT known whether Set All Ready was queued for the plant, because the connection to the host failed - check before sending it again.",
            cut.Find("#drawio-set-all-ready-outcome").TextContent.Trim()));
        Assert.StartsWith("HttpRequestException: ", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-set-all-ready-outcome"));
    }

    [Fact]
    public void SetAllReadyAsksFirstThenSendsTheCommand()
    {
        var cut = Render();

        cut.Find("#drawio-set-all-ready-button").Click();
        Assert.Empty(_host.Commands);

        cut.Find("#drawio-set-all-ready-confirm").Click();

        cut.WaitForAssertion(() => Assert.Equal((int)CommandTypes.SetStatusReadyAllItems, Assert.Single(_host.Commands).CommandType));
    }

    [Fact]
    public async Task SetAllReadyOutcomeFollows()
    {
        await _feed.StartAsync();
        var cut = Render();
        cut.Find("#drawio-set-all-ready-button").Click();
        cut.Find("#drawio-set-all-ready-confirm").Click();
        cut.WaitForAssertion(() => Assert.Contains("command 1", cut.Find("#drawio-set-all-ready-outcome").TextContent));

        _source.RaiseCommand(new CommandDto(1, DateTime.UtcNow, (int)CommandTypes.SetStatusReadyAllItems, null, null, null, (short)2,
            null, null, null, 0, null, null, 0m, null, 0m, null, null, null, true, 0));

        cut.WaitForAssertion(() => Assert.Contains("was executed", cut.Find("#drawio-set-all-ready-outcome").TextContent));
    }

    [Fact]
    public async Task TheTwoOperationsFollowIndependently()
    {
        await _feed.StartAsync();
        var cut = Render(navStatus: NavigatorStatusTypes.Stop);
        cut.Find("#drawio-remove-all-lu-button").Click();
        cut.Find("#drawio-remove-all-lu-confirm").Click();
        cut.WaitForAssertion(() => Assert.Contains("command 1", cut.Find("#drawio-remove-all-lu-outcome").TextContent));

        cut.Find("#drawio-set-all-ready-button").Click();
        cut.Find("#drawio-set-all-ready-confirm").Click();
        cut.WaitForAssertion(() => Assert.Contains("command 2", cut.Find("#drawio-set-all-ready-outcome").TextContent));

        Assert.Contains("command 1", cut.Find("#drawio-remove-all-lu-outcome").TextContent);
    }
}
