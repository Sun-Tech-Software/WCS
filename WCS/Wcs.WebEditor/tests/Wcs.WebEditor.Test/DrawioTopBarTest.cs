using System.Text.Json;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Drawio.Components;
using Wcs.WebEditor.Drawio.Coordination;
using Wcs.WebEditor.Live;
using Wcs.WebEditor.Pages;
using Wcs.WebEditor.Session;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

/// <summary>
/// The one bar of the draw.io page (passata grafica, 2026-09-29, the owner's direction A): every line
/// that used to sit above and below the strip of sections - the engine's switch, the live link, the
/// read-only banner, the plant checks, the Rulers switch - is now a control of the strip itself, in a
/// fixed order, before the six buttons of the windows. What moved keeps its id and its words: a
/// sentence that is no longer on show is still in the element with its id, behind a tip.
/// </summary>
public sealed class DrawioTopBarTest : PlantCanvasBunitContext
{
    private static readonly Guid A = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002");

    /// <summary>The six buttons of the windows, in the strip's order: the bar ends with them.</summary>
    private static readonly string[] SectionButtons =
    [
        "drawio-plant-file-toggle", "drawio-operations-toggle", "drawio-device-list-toggle",
        "drawio-junction-list-toggle", "drawio-mission-list-toggle", "drawio-plc-connections-toggle",
    ];

    /// <summary>What <see cref="BarSequence"/> writes for the line between the bar's own controls and the six buttons.</summary>
    private const string Separator = "(separator)";

    private readonly DrawioWriteHandler _host = new();
    private readonly FakePlantEventSource _source = new();

    public DrawioTopBarTest()
    {
        _host.Configs[A] = Config(A, "Shuttle 1");
        _host.Configs[B] = Config(B, "Shuttle 2");
    }

    private static PlantItemDto Item(Guid id, string name, double x) =>
        new(id, name, DeviceTypes.Automation, null, null, null, ConnectionTypes.Test,
            default, 0, null, 0, 0, 0, 0, default!, x, 10, 100, 100, 0);

    private static PlantItemConfigDto Config(Guid id, string name) => new()
    {
        ItemId = id,
        Name = name,
        DeviceType = (short)DeviceTypes.Automation,
        ConnectionType = (short)ConnectionTypes.Test,
    };

    /// <summary>The page on <see cref="_host"/>, once the plant is in (DrawioEditorModeSwitchTest's own wait).</summary>
    private IRenderedComponent<DrawioEditor> RenderPage()
    {
        var bridge = JSInterop.SetupModule("./_content/Wcs.WebEditor/drawio-bridge.js");
        bridge.Setup<string?>("selectCell", _ => true).SetResult("ok");
        bridge.Setup<bool>("renameCell", _ => true).SetResult(true);
        var state = new EditorSessionState(new Uri("http://localhost:5580"), _host) { EventSourceForTesting = () => _source };
        state.UseKey("test-key");
        Services.AddSingleton(state);
        var cut = Render<DrawioEditor>();
        cut.WaitForAssertion(() => Assert.False(cut.Find("#drawio-mode-toggle").HasAttribute("disabled")));
        return cut;
    }

    /// <summary>Finds and triggers in ONE step on the renderer's dispatcher (DrawioEditorDeviceListTest's own helper, and why).</summary>
    private static void Act(IRenderedComponent<DrawioEditor> cut, Action gesture) =>
        cut.InvokeAsync(gesture).GetAwaiter().GetResult();

    /// <summary>
    /// The children of the bar in the order they are in, as ids: each control by its own id, the
    /// Rulers switch by the id of its checkbox (the child is the label around it), the separator by
    /// <see cref="Separator"/>. The windows are left out: they are children of the bar only so that
    /// drawio-windows.js finds them there, and they float over the canvas.
    /// </summary>
    private static IReadOnlyList<string> BarSequence(IRenderedComponent<DrawioEditor> cut) =>
        [.. cut.Find(".drawio-sections").Children
            .Where(child => !child.ClassList.Contains("drawio-window"))
            .Select(NameOf)];

    private static string NameOf(IElement child)
    {
        if (!string.IsNullOrEmpty(child.Id)) return child.Id;
        if (child.QuerySelector("input[type=checkbox][id]") is { } box) return box.Id!;
        if (child.ClassList.Contains("drawio-bar-separator")) return Separator;
        return $"<{child.LocalName} class=\"{child.ClassName}\">";
    }

    [Fact]
    public void WithTheEngineStoppedTheBarIsTheTitleTheEngineTheLiveLinkTheChecksTheRulersAndTheSixButtons()
    {
        _host.Items = [Item(A, "Shuttle 1", 10), Item(B, "Shuttle 2", 300)];
        var cut = RenderPage();
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("Engine: Stop", cut.Find("#drawio-engine-state").TextContent.Trim());
            Assert.Single(cut.FindAll("#drawio-live-status"));
        });

        Assert.Equal(
            ["drawio-plant-name", "drawio-engine-switch", "drawio-live-status", "drawio-checks-toggle", "drawio-ruler-switch", Separator,
             .. SectionButtons],
            BarSequence(cut));
        Assert.Equal("WCS Editor", cut.Find("#drawio-plant-name").TextContent.Trim());
        // Stopped, the plant can be written: no read-only pill.
        Assert.Empty(cut.FindAll("#drawio-engine-banner"));
    }

    [Fact]
    public void WithTheEngineRunningTheReadOnlyPillComesAfterTheLiveLink()
    {
        _host.Items = [Item(A, "Shuttle 1", 10), Item(B, "Shuttle 2", 300)];
        _host.NavStatus = NavigatorStatusTypes.Run;
        var cut = RenderPage();
        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll("#drawio-live-status"));
            Assert.Single(cut.FindAll("#drawio-engine-banner"));
        });

        Assert.Equal(
            ["drawio-plant-name", "drawio-engine-switch", "drawio-live-status", "drawio-engine-banner", "drawio-checks-toggle",
             "drawio-ruler-switch", Separator, .. SectionButtons],
            BarSequence(cut));
    }

    /// <summary>
    /// The orange banner of the engine is now a pill that says "Read only" - that word on the bar, the
    /// padlock with it - and the sentence it used to say is still in it, word for word, behind its tip:
    /// a tip the pill opens (drawio-has-tip, without which the CSS never shows it), and the pill's
    /// description for a screen reader (aria-describedby names the tip: hidden, the tip is out of the
    /// accessibility tree, and the pill would say "Read only" and never why).
    /// </summary>
    [Fact]
    public void TheReadOnlyPillIsThereWithTheEngineRunningAndKeepsTheSentence()
    {
        _host.Items = [Item(A, "Shuttle 1", 10)];
        _host.NavStatus = NavigatorStatusTypes.Run;
        var cut = RenderPage();
        cut.WaitForAssertion(() => Assert.Equal("Engine: Run", cut.Find("#drawio-engine-state").TextContent.Trim()));

        var pill = cut.Find("#drawio-engine-banner");
        Assert.Equal(["drawio-pill-warn"], pill.ClassList.Where(name => name.StartsWith("drawio-pill-", StringComparison.Ordinal)));
        Assert.Contains("drawio-pill", pill.ClassList);
        Assert.Equal("Read only", WordOnThePill(pill));
        Assert.Equal("lock", pill.QuerySelector("svg.drawio-icon")?.GetAttribute("data-icon"));
        var tip = Assert.Single(pill.QuerySelectorAll(".drawio-tip"));
        Assert.Equal("Read only: the engine is running - you can draw, but the save will write nothing until it is stopped.",
            tip.TextContent.Trim());
        Assert.Contains("drawio-has-tip", pill.ClassList);
        Assert.False(string.IsNullOrEmpty(tip.Id), "The pill's tip has no id: nothing can name it as the pill's description.");
        Assert.Equal(tip.Id, pill.GetAttribute("aria-describedby"));
        Assert.Contains("drawio-pill-ok", cut.Find("#drawio-engine-state").ClassList);
    }

    // ---- The engine's pill and its confirmation --------------------------------------------------

    private int _starts;
    private int _stops;

    private IRenderedComponent<DrawioEngineSwitch> RenderSwitch(NavigatorStatusTypes? status) =>
        Render<DrawioEngineSwitch>(parameters => parameters
            .Add(p => p.Status, status)
            .Add(p => p.State, new DrawioEngineState(null, null))
            .Add(p => p.OnStart, () => _starts++)
            .Add(p => p.OnStop, () => _stops++));

    /// <summary>
    /// The engine's pill wears one tone and one icon, both saying its state: play running, the square
    /// stopped, two bars paused - and for a recovery, a shutdown or a state not known, which are none
    /// of the three, a plain dot in the pill's tone, not the square that would say "stopped".
    /// </summary>
    [Theory]
    [InlineData(NavigatorStatusTypes.Run, "drawio-pill-ok", "play")]
    [InlineData(NavigatorStatusTypes.Stop, "drawio-pill-neutral", "stop")]
    [InlineData(NavigatorStatusTypes.Pause, "drawio-pill-warn", "pause")]
    [InlineData(NavigatorStatusTypes.Recover, "drawio-pill-warn", "live")]
    [InlineData(NavigatorStatusTypes.Dispose, "drawio-pill-bad", "live")]
    [InlineData(null, "drawio-pill-warn", "live")]
    public void TheEnginesPillWearsTheToneAndTheIconOfItsState(NavigatorStatusTypes? status, string tone, string icon)
    {
        var pill = RenderSwitch(status).Find("#drawio-engine-state");

        Assert.Contains("drawio-pill", pill.ClassList);
        Assert.Equal([tone], pill.ClassList.Where(name => name.StartsWith("drawio-pill-", StringComparison.Ordinal)));
        Assert.Equal(icon, Assert.Single(pill.QuerySelectorAll("svg.drawio-icon")).GetAttribute("data-icon"));
    }

    [Fact]
    public void StopOpensABoxWithTheQuestionAndCancelShutsItWithoutStopping()
    {
        var cut = RenderSwitch(NavigatorStatusTypes.Run);
        Assert.Empty(cut.FindAll("#drawio-engine-confirm-box"));
        Assert.Contains("drawio-btn-danger", cut.Find("#drawio-engine-stop").ClassList);

        cut.Find("#drawio-engine-stop").Click();

        var box = cut.Find("#drawio-engine-confirm-box");
        Assert.Contains("drawio-popover", box.ClassList);
        Assert.Equal("Stop the engine? Every movement in progress stops.",
            box.QuerySelector(".drawio-engine-question")!.TextContent.Trim());
        Assert.NotNull(box.QuerySelector("#drawio-engine-confirm"));
        Assert.NotNull(box.QuerySelector("#drawio-engine-cancel"));
        Assert.Contains("drawio-btn-danger", box.QuerySelector("#drawio-engine-confirm")!.ClassList);
        Assert.Equal("true", cut.Find("#drawio-engine-stop").GetAttribute("aria-expanded"));
        // Stop keeps its red class while it asks: the owner's decision of 30/9 is about Start only.
        Assert.Equal(["drawio-btn-danger"], ColouredClassesOf(cut.Find("#drawio-engine-stop")));

        cut.Find("#drawio-engine-cancel").Click();

        Assert.Empty(cut.FindAll("#drawio-engine-confirm-box"));
        Assert.Equal("false", cut.Find("#drawio-engine-stop").GetAttribute("aria-expanded"));
        Assert.Equal(0, _stops);
        Assert.Equal(0, _starts);
        // Cancel went with the box, and the keyboard's focus with it: it goes back to the button that asked.
        Assert.Equal(ReferenceIn(cut.Instance, "_gesture").Id, LastFocused().Id);
    }

    /// <summary>The element the page last gave the keyboard's focus to (<c>ElementReference.FocusAsync</c>).</summary>
    private ElementReference LastFocused() =>
        Assert.IsType<ElementReference>(JSInterop.Invocations["Blazor._internal.domWrapper.focus"][^1].Arguments[0]);

    /// <summary>
    /// The element a component holds in one of its <c>@ref</c> fields. bUnit 2 does not write the
    /// reference's id on the rendered element (its <c>blazor:elementReference</c> is empty), so the
    /// element the page focused is told by the reference the component itself holds for it.
    /// </summary>
    private static ElementReference ReferenceIn(object component, string field) =>
        (ElementReference)component.GetType().GetField(field, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(component)!;

    /// <summary>
    /// Esc takes the question back, as Cancel and the button pressed again do, with the focus back on
    /// the button that asked; with no question asked it does nothing. The button names the box as what
    /// it controls only while there is one (aria-controls names an element, and a shut box is none).
    /// </summary>
    [Fact]
    public void EscTakesTheQuestionBackAndTheFocusGoesBackToTheButtonThatAsked()
    {
        var cut = RenderSwitch(NavigatorStatusTypes.Stop);
        Assert.False(cut.Find("#drawio-engine-start").HasAttribute("aria-controls"));

        cut.Find("#drawio-engine-start").Click();
        Assert.Equal("drawio-engine-confirm-box", cut.Find("#drawio-engine-start").GetAttribute("aria-controls"));
        Assert.NotNull(cut.Find("#drawio-engine-confirm-box"));

        cut.Find(".drawio-engine-gesture").KeyDown(new KeyboardEventArgs { Key = "Enter" });
        Assert.NotNull(cut.Find("#drawio-engine-confirm-box"));

        cut.Find(".drawio-engine-gesture").KeyDown(new KeyboardEventArgs { Key = "Escape" });

        Assert.Empty(cut.FindAll("#drawio-engine-confirm-box"));
        Assert.False(cut.Find("#drawio-engine-start").HasAttribute("aria-controls"));
        Assert.Equal("false", cut.Find("#drawio-engine-start").GetAttribute("aria-expanded"));
        Assert.Equal(ReferenceIn(cut.Instance, "_gesture").Id, LastFocused().Id);
        Assert.Equal(0, _starts);
    }

    [Fact]
    public void ConfirmInTheBoxStopsTheEngineOnce()
    {
        var cut = RenderSwitch(NavigatorStatusTypes.Run);

        cut.Find("#drawio-engine-stop").Click();
        cut.Find("#drawio-engine-confirm-box #drawio-engine-confirm").Click();

        Assert.Equal(1, _stops);
        Assert.Equal(0, _starts);
        Assert.Empty(cut.FindAll("#drawio-engine-confirm-box"));
        // The Yes went with the box: the focus goes to the pill, which says what the engine does next.
        Assert.Equal(ReferenceIn(cut.Instance, "_pill").Id, LastFocused().Id);
    }

    /// <summary>
    /// Start asks with its own question, and its Yes is the blue one. Pressed again while it asks, the
    /// button takes the question back, as Cancel does - and starts nothing.
    /// </summary>
    [Fact]
    public void StartAsksItsOwnQuestionAndPressedAgainTakesItBack()
    {
        var cut = RenderSwitch(NavigatorStatusTypes.Stop);
        Assert.Contains("drawio-btn-primary", cut.Find("#drawio-engine-start").ClassList);

        cut.Find("#drawio-engine-start").Click();

        var box = cut.Find("#drawio-engine-confirm-box");
        Assert.Equal("Start the engine? The plant becomes read only for everybody, and devices connected to a PLC start moving.",
            box.QuerySelector(".drawio-engine-question")!.TextContent.Trim());
        Assert.Contains("drawio-btn-primary", box.QuerySelector("#drawio-engine-confirm")!.ClassList);

        cut.Find("#drawio-engine-start").Click();

        Assert.Empty(cut.FindAll("#drawio-engine-confirm-box"));
        Assert.Equal(0, _starts);

        cut.Find("#drawio-engine-start").Click();
        cut.Find("#drawio-engine-confirm").Click();
        Assert.Equal(1, _starts);
        Assert.Equal(0, _stops);
    }

    /// <summary>
    /// The owner's decision of 30/9, one blue button while Start asks: with the box open, the
    /// button that asked wears no class at all - a plain button of the page - and the box's Start
    /// engine is the one blue button; after Cancel it is blue again, as at rest. How the plain button
    /// is painted is the browser's to say (DrawioTopBarBrowserTest). Stop is left as it was: it keeps
    /// its red class while it asks (StopOpensABoxWithTheQuestionAndCancelShutsItWithoutStopping), and
    /// the stylesheet draws it pressed and grey meanwhile.
    /// </summary>
    [Fact]
    public void WhileStartAsksItsButtonIsAPlainOneAndAfterCancelItIsBlueAgain()
    {
        var cut = RenderSwitch(NavigatorStatusTypes.Stop);
        Assert.Equal(["drawio-btn-primary"], cut.Find("#drawio-engine-start").ClassList);

        cut.Find("#drawio-engine-start").Click();

        var asking = cut.Find("#drawio-engine-start");
        Assert.Equal("true", asking.GetAttribute("aria-expanded"));
        Assert.False(asking.HasAttribute("class"), $"While it asks, Start wears class=\"{asking.GetAttribute("class")}\".");
        Assert.Equal(["drawio-btn-primary"], ColouredClassesOf(cut.Find("#drawio-engine-confirm-box #drawio-engine-confirm")));

        cut.Find("#drawio-engine-cancel").Click();

        Assert.Empty(cut.FindAll("#drawio-engine-confirm-box"));
        Assert.Equal(["drawio-btn-primary"], cut.Find("#drawio-engine-start").ClassList);
    }

    /// <summary>The page's two coloured classes a button wears: blue (drawio-btn-primary) and red (drawio-btn-danger).</summary>
    private static string[] ColouredClassesOf(IElement button) =>
        button.ClassList.Where(name => name is "drawio-btn-primary" or "drawio-btn-danger").ToArray();

    /// <summary>
    /// The engine's refusal is a line of its own right under the bar (review of 29/9): the whole
    /// sentence is the line's own text, behind no tip, and nothing of it is in the bar. On the bar's one
    /// line it was cut short, and most refusals say what counts at their end ("... so the engine was NOT
    /// started. Save first, then start it."). Staged as DrawioEditorLiveTest stages a lost request for
    /// the edit permit: the host's edit-lock route failing.
    /// </summary>
    [Fact]
    public void TheEnginesRefusalIsALineOfItsOwnUnderTheBar()
    {
        _host.Items = [Item(A, "Shuttle 1", 10)];
        var cut = RenderPage();
        Assert.Empty(cut.FindAll("#drawio-engine-error"));
        _host.HttpFailureRoute = "edit-lock";

        Act(cut, () => cut.Find("#drawio-engine-start").Click());
        Act(cut, () => cut.Find("#drawio-engine-confirm").Click());

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("#drawio-engine-error")));
        var line = cut.Find("#drawio-engine-error");
        // Row 37 of the queue (the owner's answer of 9/10): the permit that could not be asked for is
        // one sentence, and what came back is behind the "?" of the band - which is the line's row
        // (the sentence and its "?"), right after the bar.
        Assert.Equal("The engine was NOT started. The edit permit could not be asked for because the connection to the host failed: nothing was written.",
            line.TextContent.Trim());
        Assert.StartsWith("HttpRequestException: ", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-engine-error"));
        var band = Assert.Single(cut.FindAll(".drawio-engine-error"));
        Assert.NotNull(line.Closest(".drawio-engine-error"));
        Assert.Null(band.Closest(".drawio-sections"));
        Assert.True(band.PreviousElementSibling?.ClassList.Contains("drawio-sections") == true,
            $"The engine's refusal does not come right after the bar: it comes after <{band.PreviousElementSibling?.LocalName ?? "nothing"} class=\"{band.PreviousElementSibling?.ClassName}\">.");
        Assert.Empty(line.QuerySelectorAll(".drawio-tip"));
        Assert.Null(line.Closest(".drawio-has-tip"));
        Assert.DoesNotContain(line.TextContent.Trim(), cut.Find(".drawio-sections").TextContent);
    }

    // ---- The live link's pill ----------------------------------------------------------------------

    private const string HoverFailed = "Live status: the route on hover could not be switched (test).";

    /// <summary>
    /// The live link's pill as the operator sees it (review of 29/9): a word and a tone on the bar, the
    /// page's sentence behind the tip - never on the bar - and no tip at all when the link is up and
    /// well. The word is the pill's text without its tip. Where two things are wrong at once, the pill
    /// says the one the page's lines put first: an interrupted link with a hover that failed is a red
    /// "Offline", not an orange "Live" over colours that are the last ones received.
    /// </summary>
    [Theory]
    [InlineData(null, "no channel (test)", false, null, "Live off", "drawio-pill-bad", "Live status is not available on this page")]
    [InlineData(LiveConnection.Interrupted, null, false, null, "Offline", "drawio-pill-bad", "Live status is not being updated")]
    [InlineData(LiveConnection.Interrupted, null, true, HoverFailed, "Offline", "drawio-pill-bad", "Live status is not being updated")]
    [InlineData(LiveConnection.Connecting, null, false, null, "Connecting", "drawio-pill-warn", "Live status: connecting to the host...")]
    [InlineData(LiveConnection.Connecting, null, true, HoverFailed, "Connecting", "drawio-pill-warn", "Live status: connecting to the host...")]
    [InlineData(LiveConnection.Live, null, true, HoverFailed, "Live off", "drawio-pill-warn", "Live status cannot be drawn on this canvas")]
    [InlineData(LiveConnection.Live, null, false, HoverFailed, "Live", "drawio-pill-warn", HoverFailed)]
    [InlineData(LiveConnection.Live, null, false, null, "Live", "drawio-pill-neutral", null)]
    public void TheLiveLinksPillSaysAWordInTheToneOfWhatIsWrongAndKeepsTheSentenceBehindItsTip(
        LiveConnection? connection, string? unavailable, bool drawFailed, string? trouble, string word, string tone, string? sentence)
    {
        var pill = Render<DrawioLiveStatus>(parameters => parameters
                .Add(p => p.Connection, connection)
                .Add(p => p.Unavailable, unavailable)
                .Add(p => p.DrawFailed, drawFailed)
                .Add(p => p.Trouble, trouble))
            .Find("#drawio-live-status");

        Assert.Equal(word, WordOnThePill(pill));
        Assert.Equal([tone], pill.ClassList.Where(name => name.StartsWith("drawio-pill-", StringComparison.Ordinal)));
        var tips = pill.QuerySelectorAll(".drawio-tip");
        if (sentence is null)
        {
            Assert.Empty(tips);
            Assert.Contains("drawio-live-on", pill.ClassList);
        }
        else
        {
            var tip = Assert.Single(tips);
            Assert.Contains(sentence, tip.TextContent);
            Assert.Contains("drawio-has-tip", pill.ClassList);
            // The sentence is the pill's description for a screen reader: hidden, the tip is not read.
            Assert.False(string.IsNullOrEmpty(tip.Id), "The pill's tip has no id: nothing can name it as the pill's description.");
            Assert.Equal(tip.Id, pill.GetAttribute("aria-describedby"));
        }
    }

    /// <summary>
    /// What a pill of the bar says on the bar: its text without its tip - the sentence behind the tip
    /// is in the page all along, but not on show.
    /// </summary>
    internal static string WordOnThePill(IElement pill) =>
        string.Concat(pill.ChildNodes
            .Where(node => node is not IElement element || !element.ClassList.Contains("drawio-tip"))
            .Select(node => node.TextContent)).Trim();

    // ---- The plant checks --------------------------------------------------------------------------

    /// <summary>
    /// The plant checks open in a window of their own, from their button in the bar, and the button
    /// counts the problems the last checks found: the junction check's and the configuration check's
    /// together. Nothing has been checked when the page opens, so there is no number to show.
    /// </summary>
    [Fact]
    public async Task TheChecksOpenInAWindowAndTheirButtonCountsTheProblemsBothChecksFound()
    {
        // One placed device and no junction: the junction check finds it isolated, one problem.
        _host.Items = [Item(A, "Shuttle 1", 10)];
        var cut = RenderPage();

        Assert.True(cut.Find("#drawio-checks").HasAttribute("hidden"));
        Assert.Contains("drawio-window", cut.Find("#drawio-checks").ClassList);
        Assert.Equal("Plant checks", cut.Find("#drawio-checks-toggle #drawio-checks-summary").TextContent.Trim());
        Assert.Empty(cut.FindAll("#drawio-checks-count"));

        Act(cut, () => cut.Find("#drawio-checks-toggle").Click());
        Assert.False(cut.Find("#drawio-checks").HasAttribute("hidden"));
        Assert.Equal("true", cut.Find("#drawio-checks-toggle").GetAttribute("aria-pressed"));

        await cut.Find("#check-junctions-button").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => Assert.Equal("1", cut.Find("#drawio-checks-toggle #drawio-checks-count").TextContent.Trim()));

        // Two rows from the configuration check: three problems in all.
        _host.WmsConfigurationWarnings.Add(new WmsConfigurationWarning(new Dictionary<string, string?> { ["DESCRIZIONE"] = "cella orfana" }));
        _host.WmsConfigurationWarnings.Add(new WmsConfigurationWarning(new Dictionary<string, string?> { ["DESCRIZIONE"] = "cella doppia" }));
        await cut.Find("#check-config-button").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => Assert.Equal("3", cut.Find("#drawio-checks-count").TextContent.Trim()));
    }

    /// <summary>
    /// While the configuration check runs, the checks' button wears a blue dot (fix round of 29/9): where
    /// its words are not shown - a narrow window, or something else on its way in the bar - the dot is what
    /// says a check is running, and the CSS takes the dot as one of the things on their way. It goes when
    /// the check is over. Staged as DrawioEditorChecksTest stages the running check: the host's answer
    /// held on a gate, and the click's task not awaited while it is shut.
    /// </summary>
    [Fact]
    public async Task TheChecksButtonWearsADotWhileTheConfigurationCheckRuns()
    {
        _host.Items = [Item(A, "Shuttle 1", 10)];
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _host.WmsConfigurationCheckGate = held;
        var cut = RenderPage();
        Assert.Empty(cut.FindAll("#drawio-checks-toggle .drawio-badge-running"));

        var check = cut.Find("#check-config-button").ClickAsync(new MouseEventArgs());
        await _host.WmsConfigurationCheckGateReached.Task;
        Assert.False(check.IsCompleted, "The configuration check did not stay inside its own GET: the running check was never staged.");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("#drawio-checks-toggle .drawio-badge-running")));
        Assert.Contains("is running", cut.Find("#drawio-checks-summary").TextContent);

        held.SetResult();
        await check;
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#drawio-checks-toggle .drawio-badge-running")));
        Assert.DoesNotContain("is running", cut.Find("#drawio-checks-summary").TextContent);
    }

    // ---- What a shut window is waiting on ----------------------------------------------------------

    /// <summary>
    /// A file chosen for the import and not confirmed yet: the Plant file button wears a mark, and the
    /// sentence it used to show is still in it, behind its tip - a tip the button opens (drawio-has-tip:
    /// without it the CSS never shows the sentence, fix round of 29/9).
    /// </summary>
    [Fact]
    public async Task APendingImportMarksThePlantFileButtonAndKeepsItsSentence()
    {
        _host.Items = [Item(A, "Shuttle 1", 10)];
        var cut = RenderPage();
        await cut.InvokeAsync(() => cut.Instance.OnDrawioReady());
        Assert.Empty(cut.FindAll("#drawio-plant-file-toggle .drawio-badge"));

        Act(cut, () => cut.Find("#drawio-plant-file-toggle").Click());
        var file = InputFileContent.CreateFromBinary(
            JsonSerializer.SerializeToUtf8Bytes(new PlantGraphDto([Config(A, "Shuttle 1")], [], [], [], null)), "impianto.json");
        Act(cut, () => cut.FindComponent<InputFile>().UploadFiles(file));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("#plant-import-confirm")));

        var button = cut.Find("#drawio-plant-file-toggle");
        Assert.Equal("!", button.QuerySelector(".drawio-badge")!.TextContent.Trim());
        Assert.Contains("an import is waiting for confirmation", button.TextContent);
        Assert.Contains("an import is waiting for confirmation", button.QuerySelector(".drawio-tip")!.TextContent);
        Assert.Contains("drawio-has-tip", button.ClassList);
    }

    /// <summary>
    /// A change typed into the PLC grid and not saved: the button wears a dot, and says so behind its tip -
    /// a tip the button opens (drawio-has-tip, as Plant file's).
    /// </summary>
    [Fact]
    public void AnUnsavedPlcChangeMarksItsButtonAndKeepsItsSentence()
    {
        _host.Items = [Item(A, "Shuttle 1", 10)];
        _host.PlcConnections = [new PlcConnectionDto(1, "Line 1", "10.0.0.1", 2011, "10.0.0.2", 2012)];
        var cut = RenderPage();
        Assert.Empty(cut.FindAll("#drawio-plc-connections-toggle .drawio-badge"));

        Act(cut, () => cut.Find("#drawio-plc-connections-toggle").Click());
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("#drawio-plc-table tbody tr")));
        Act(cut, () => cut.Find("#drawio-plc-table tbody tr input[data-field='Description']").Input("typed"));

        cut.WaitForAssertion(() =>
        {
            var button = cut.Find("#drawio-plc-connections-toggle");
            // A dot, not the "!" of Plant file: nothing is wrong, something is waiting to be saved.
            var mark = Assert.Single(button.QuerySelectorAll(".drawio-badge"));
            Assert.Contains("drawio-badge-dot", mark.ClassList);
            Assert.Equal("", mark.TextContent.Trim());
            Assert.Contains("changed, not saved", button.TextContent);
            Assert.Contains("changed, not saved", button.QuerySelector(".drawio-tip")!.TextContent);
            Assert.Contains("drawio-has-tip", button.ClassList);
        });
    }
}
