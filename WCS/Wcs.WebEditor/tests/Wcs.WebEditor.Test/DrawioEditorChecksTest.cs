using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Drawio;
using Wcs.WebEditor.Model;
using Wcs.WebEditor.Pages;
using Wcs.WebEditor.Session;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

/// <summary>
/// The two plant checks on the draw.io page (rows 23 and 26): "Check Junctions", which reads the
/// plant this page holds, and "Check Configuration", which asks the host.
/// </summary>
/// <remarks>
/// <para>
/// <c>ChecksPanel</c> and <c>JunctionChecks</c> are used as they are and are not touched: what they
/// do on their own is measured by <c>ChecksTest</c> and <c>JunctionChecksTest</c>, which this batch
/// leaves alone. What is measured HERE is everything this page adds around them - where the panel
/// is, that reaching a problem lands on the cell, that a problem with nothing to land on is SAID,
/// and that the graph the junction check reads is the plant as it is now and not as it was at
/// opening.
/// </para>
/// <para>
/// The way in is the same as the other bUnit tests of this page: the <c>[JSInvokable]</c> methods
/// called straight on the rendered instance. What bUnit cannot answer - whether a selection really
/// lands on a cell inside draw.io, and whether it costs a Ctrl+Z - is measured in a real browser by
/// <c>DrawioChecksTest</c>.
/// </para>
/// </remarks>
public sealed class DrawioEditorChecksTest : PlantCanvasBunitContext
{
    private static readonly Guid A = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid B = Guid.Parse("22222222-2222-2222-2222-222222222222");

    /// <summary>The device with no position: on the plant, named by the check, and drawn nowhere.</summary>
    private static readonly Guid Nowhere = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private const string NameA = "AAAA";
    private const string NameB = "BBBB";
    private const string NameNowhere = "NOWHERE";

    /// <summary>What the planned bridge answers unless a test plans something else: the cell was selected.</summary>
    private const string Selected = "ok";

    private static DeviceTypeDto Type() => new("automation", (short)DeviceTypes.Automation, "Automation",
        [new ShapePartDto("M 4 4 H 96 V 96 H 4 Z", "#334155", Filled: false, Rigid: false)],
        [new DevicePortDto("in", 4, 50, "in"), new DevicePortDto("out", 96, 50, "out")],
        new ShapePointDto(50, 50), new ShapePointDto(88, 60));

    /// <summary>A device with a box - drawn, therefore reachable on the canvas.</summary>
    private static PlantItemDto Item(Guid id, string name, double x) =>
        new(id, name, DeviceTypes.Automation, null, null, null, default, default, 0, null,
            0, 0, 0, 0, default!, x, 20, 130, 184, 0);

    /// <summary>
    /// A device with NO position: <c>PlantGraph.IsPlaced</c> says false, so <c>DrawioDocument</c>
    /// draws no cell for it at all. It is the shape of "the check names a problem there is nothing
    /// to go to for", and it is a real plant's state - a device created without a geometry.
    /// </summary>
    private static PlantItemDto Unplaced(Guid id, string name) =>
        new(id, name, DeviceTypes.Automation, null, null, null, default, default, 0, null,
            0, 0, 0, 0, default!, X: null, Y: null, Width: null, Height: null, Rotation: null);

    private static PlantItemConfigDto Config(Guid id, string name) =>
        new() { ItemId = id, Name = name, DeviceType = (short)DeviceTypes.Automation };

    /// <summary>Two placed devices and NO junction: both of them are isolated, which is what gives this suite two targets to go to instead of one.</summary>
    private static DrawioWriteHandler HostWithTwoLooseDevices()
    {
        var handler = new DrawioWriteHandler { Items = [Item(A, NameA, 10), Item(B, NameB, 500)], Types = [Type()] };
        handler.Configs[A] = Config(A, NameA);
        handler.Configs[B] = Config(B, NameB);
        return handler;
    }

    private static PlantGraph GraphOf(DrawioWriteHandler handler) =>
        new([.. handler.Items], [.. handler.Junctions], [.. handler.Types]);

    /// <summary>The bridge as these tests plan it, kept so that a test can read what was asked of the canvas.</summary>
    private BunitJSModuleInterop _bridge = null!;

    /// <summary>
    /// Renders the page with the bridge's <c>selectCell</c> planned to answer <paramref name="answer"/>,
    /// and lets draw.io's 'init' through so the drawing is really seeded.
    /// </summary>
    /// <remarks>
    /// The answer is planned on purpose even when it is the ordinary one: bUnit's <c>Loose</c> mode
    /// answers an unplanned call with <c>default(string)</c>, that is null, and a test that forgot to
    /// plan it would be measuring the "answer this page does not know how to read" branch without
    /// meaning to. One test plans nothing precisely to measure that branch.
    /// <para>
    /// <paramref name="answerByDevice"/> is for the tests that need TWO different answers in one run
    /// - a "go to" that lands and then one that misses. Each handler is keyed on the cell asked for,
    /// so which of them replies does not depend on the order bUnit happens to try them in, and every
    /// test using it asserts the outcome text as well, which says which one really did.
    /// </para>
    /// </remarks>
    private async Task<IRenderedComponent<DrawioEditor>> RenderWithAsync(
        DrawioWriteHandler handler, string? answer = Selected,
        IReadOnlyDictionary<Guid, string>? answerByDevice = null)
    {
        _bridge = JSInterop.SetupModule("./_content/Wcs.WebEditor/drawio-bridge.js");
        foreach (var (itemId, planned) in answerByDevice ?? new Dictionary<Guid, string>())
        {
            // The cell of a device the plant handed over IS its guid: see DrawnCellOf, and the
            // assertion on CellsAskedFor in GettingToAnIsolatedDeviceSelectsItsCellInTheDrawing.
            var cellId = itemId.ToString();
            _bridge.Setup<string?>("selectCell", call => call.Arguments.Count > 0 && (call.Arguments[0] as string) == cellId)
                .SetResult(planned);
        }

        if (answer is not null) _bridge.Setup<string?>("selectCell", _ => true).SetResult(answer);
        // Every creation asks the canvas for the new label: unplanned it answers false, which is a
        // branch these tests are not about.
        _bridge.Setup<bool>("renameCell", _ => true).SetResult(true);

        var state = new EditorSessionState(new Uri("http://localhost:5580"), handler);
        state.UseKey("test-key");
        Services.AddSingleton(state);
        var cut = Render<DrawioEditor>();
        await cut.InvokeAsync(() => cut.Instance.OnDrawioReady());
        return cut;
    }

    private static Task RunJunctionCheckAsync(IRenderedComponent<DrawioEditor> cut) =>
        cut.Find("#check-junctions-button").ClickAsync(new MouseEventArgs());

    private static Task RunConfigCheckAsync(IRenderedComponent<DrawioEditor> cut) =>
        cut.Find("#check-config-button").ClickAsync(new MouseEventArgs());

    /// <summary>The labels of the "go to" buttons the page is offering, in the order it draws them.</summary>
    private static string[] Targets(IRenderedComponent<DrawioEditor> cut) =>
        [.. cut.FindAll("button.drawio-checks-target").Select(node => node.TextContent.Trim())];

    /// <summary>The identifiers those buttons carry: what the page is offering to go to, by identity rather than by label.</summary>
    private static string[] TargetItemIds(IRenderedComponent<DrawioEditor> cut) =>
        [.. cut.FindAll("button.drawio-checks-target").Select(node => node.GetAttribute("data-item-id") ?? string.Empty)];

    /// <summary>
    /// Which of the two colours the outcome line is wearing. The line's WORDS and its COLOUR are two
    /// different claims, and a miss painted green says two opposite things at once.
    /// </summary>
    private static string? OutcomeTone(IRenderedComponent<DrawioEditor> cut) =>
        cut.FindAll("#drawio-checks-goto-outcome") is [var node, ..] ? node.GetAttribute("class") : null;

    private static Task GoToAsync(IRenderedComponent<DrawioEditor> cut, Guid itemId) =>
        cut.Find($"button.drawio-checks-target[data-item-id='{itemId}']").ClickAsync(new MouseEventArgs());

    private static string? OutcomeOrNull(IRenderedComponent<DrawioEditor> cut) =>
        cut.FindAll("#drawio-checks-goto-outcome") is [var node, ..] ? node.TextContent : null;

    private static string Outcome(IRenderedComponent<DrawioEditor> cut) =>
        OutcomeOrNull(cut) ?? throw new Xunit.Sdk.XunitException("The page said nothing at all about the last \"go to\".");

    /// <summary>The cells <c>selectCell</c> was asked about, in order.</summary>
    private IReadOnlyList<string?> CellsAskedFor() =>
        [.. _bridge.Invocations["selectCell"].Select(call => call.Arguments.Count > 0 ? call.Arguments[0] as string : null)];

    // ---- Where the panel is ----------------------------------------------------------------------

    /// <summary>
    /// The checks are in the strip above the drawing and <b>not inside the dock</b>, and the page
    /// says out loud that they are about the whole plant.
    /// </summary>
    /// <remarks>
    /// The structural half is the one that could not be read off the screen: the dock is 320 px of
    /// column driven by the SELECTION, and a checks panel that grew inside it would take room from
    /// the sixty properties and the warehouse cells while reading as if it spoke about the selected
    /// device. Asserting the two sentences alone would leave that free to happen.
    /// </remarks>
    [Fact]
    public async Task TheChecksStandOutsideTheConfigurationDockAndSayTheyAreAboutTheWholePlant()
    {
        var cut = await RenderWithAsync(HostWithTwoLooseDevices());

        Assert.Single(cut.FindAll("#drawio-checks"));
        Assert.Empty(cut.FindAll("#drawio-property-dock #drawio-checks"));
        Assert.Empty(cut.FindAll("#drawio-checks #drawio-property-dock"));

        var scope = cut.Find("#drawio-checks-scope").TextContent;
        Assert.Contains("WHOLE plant", scope);
        Assert.Contains("never about the device selected", scope);
        // The freshness caveat, which is the one that stops the check from telling a lie: the plant
        // this page holds is not the canvas.
        Assert.Contains("not saved", scope);
    }

    /// <summary>
    /// The two commands are there, and pressing neither of them happens by itself: opening the page
    /// shows no result, and nothing has been asked of the host.
    /// </summary>
    /// <remarks>
    /// The second half is what a "no result on screen" assertion alone would miss: a panel that ran
    /// the configuration check on mount would show nothing until the answer came back, and this test
    /// would still be green while every opening of the Editor hit the WMS.
    /// </remarks>
    [Fact]
    public async Task NeitherCheckRunsByItselfWhenThePageOpens()
    {
        var handler = HostWithTwoLooseDevices();
        var cut = await RenderWithAsync(handler);

        Assert.Single(cut.FindAll("#check-junctions-button"));
        Assert.Single(cut.FindAll("#check-config-button"));
        Assert.Empty(cut.FindAll("#junction-check-result"));
        Assert.Empty(cut.FindAll("#config-check-result"));
        Assert.Empty(cut.FindAll("#config-check-error"));
        Assert.Empty(handler.WmsConfigurationCheckRequests);
        Assert.Empty(Targets(cut));
        Assert.Null(OutcomeOrNull(cut));
    }

    // ---- Getting to the problem ------------------------------------------------------------------

    /// <summary>
    /// <b>The test this batch is worth in bUnit.</b> The junction check names the isolated devices,
    /// and getting to one SELECTS its cell in the drawing - it does not colour it.
    /// </summary>
    /// <remarks>
    /// The cell asked for is asserted, not merely that something was asked: with the wrong
    /// identifier draw.io would find no cell and the operator would be told the device is not drawn,
    /// about a shape that is right there in front of them.
    /// </remarks>
    [Fact]
    public async Task GettingToAnIsolatedDeviceSelectsItsCellInTheDrawing()
    {
        var cut = await RenderWithAsync(HostWithTwoLooseDevices());

        await RunJunctionCheckAsync(cut);

        Assert.Equal([$"Go to {NameA}", $"Go to {NameB}"], Targets(cut));
        Assert.Null(OutcomeOrNull(cut));

        await GoToAsync(cut, B);

        Assert.Equal([B.ToString()], CellsAskedFor());
        var outcome = Outcome(cut);
        Assert.Contains(NameB, outcome);
        Assert.Contains("selected in the drawing", outcome);
        Assert.Equal("drawio-checks-goto-reached", cut.Find("#drawio-checks-goto-outcome").GetAttribute("class"));
    }

    /// <summary>
    /// A "go to" while the drawing does not answer: why in the page's words, in the colour of a miss,
    /// and what the page's script threw behind the line's "?" (row 37 of the queue, extended to the
    /// whole page on 10/10).
    /// </summary>
    [Fact]
    public async Task AGoToTheDrawingDoesNotAnswerKeepsTheFailureBehindTheQuestionMark()
    {
        var cut = await RenderWithAsync(HostWithTwoLooseDevices(), answer: null);
        _bridge.Setup<string?>("selectCell", _ => true).SetException(new Microsoft.JSInterop.JSException("selectCell non ha risposto (prova)"));

        await RunJunctionCheckAsync(cut);
        await GoToAsync(cut, B);

        Assert.Equal($"{NameB}: the drawing did not answer, because the page's script failed, so nothing was selected.", cut.Find("#drawio-checks-goto-outcome").TextContent);
        Assert.Equal("drawio-checks-goto-missed", cut.Find("#drawio-checks-goto-outcome").GetAttribute("class"));
        Assert.Equal("JSException: selectCell non ha risposto (prova)", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-checks-goto-outcome"));
    }

    /// <summary>
    /// A check that finds nothing offers nothing to go to, and the panel says "nessun problema"
    /// instead of an empty box.
    /// </summary>
    [Fact]
    public async Task APlantWithNoProblemsOffersNothingToGoToAndSaysSo()
    {
        var handler = HostWithTwoLooseDevices();
        handler.Junctions.Add(new JunctionDto(Guid.NewGuid(), A, B, 99));
        var cut = await RenderWithAsync(handler);

        await RunJunctionCheckAsync(cut);

        Assert.Empty(Targets(cut));
        Assert.Contains("No problem found", cut.Find("#junction-check-result").TextContent);
        Assert.Empty(CellsAskedFor());
    }

    /// <summary>
    /// <b>The problem with nothing to select.</b> An UNPLACED device is isolated too, so the check
    /// names it and the page offers to go to it - but no cell is drawn for it, and what comes back is
    /// said in words instead of a click that appears to do nothing.
    /// </summary>
    /// <remarks>
    /// This is the shape the brief asks about: a warning whose target cannot be reached. The page
    /// does not hide the button - hiding it would leave the operator clicking the panel's line and
    /// wondering - it presses on and reports what the canvas answered.
    /// </remarks>
    [Fact]
    public async Task ADeviceWithNoCellInTheDrawingIsSaidInsteadOfSilentlyDoingNothing()
    {
        var handler = HostWithTwoLooseDevices();
        handler.Items.Add(Unplaced(Nowhere, NameNowhere));
        var cut = await RenderWithAsync(handler, answer: "no-cell");

        await RunJunctionCheckAsync(cut);

        // The panel names it twice - isolated AND unplaced - and the page offers to go to it once.
        Assert.Contains($"Go to {NameNowhere}", Targets(cut));

        await GoToAsync(cut, Nowhere);

        var outcome = Outcome(cut);
        Assert.Contains(NameNowhere, outcome);
        Assert.Contains("no cell in the drawing", outcome);
        Assert.Contains("on the plant either way", outcome);
        Assert.Equal("drawio-checks-goto-missed", OutcomeTone(cut));
    }

    /// <summary>
    /// <b>What can be reached and what cannot is said next to the list, and the list agrees with the
    /// saying.</b> A dangling junction is named above and offered nowhere; an isolated device that is
    /// also unplaced IS offered, and its button answers "no cell" instead of doing nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why every claim in that sentence is measured here rather than merely quoted.</b> The
    /// sentence this test guards used to say that an unplaced device was "named above and nowhere
    /// else" - with its own "Go to" button twenty pixels underneath - and THIS test held it in place
    /// with a substring assertion on the very words that were false. Words asserted against words
    /// cannot catch that. So each half of the hint is checked against what the page really offers, in
    /// the same run and on the same plant: a sentence and a page that drift apart can no longer both
    /// stay green.
    /// </para>
    /// <para>
    /// The plant carries all three kinds of warning on purpose. Without the dangling junction there
    /// would be nothing to say "is offered nowhere" about, and without the unplaced device the half
    /// that was wrong would not be exercised at all.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task WhatCanBeReachedAndWhatCannotIsSaidNextToTheListAndTheListAgrees()
    {
        var handler = HostWithTwoLooseDevices();
        // A junction towards a device the plant does not have: a dangling one, whose TargetId is the
        // JUNCTION's - nothing on the canvas joins two devices when one of them is not there.
        var dangling = new JunctionDto(Guid.NewGuid(), A, Guid.NewGuid(), 99);
        handler.Junctions.Add(dangling);
        handler.Items.Add(Unplaced(Nowhere, NameNowhere));
        var cut = await RenderWithAsync(handler, answer: null,
            answerByDevice: new Dictionary<Guid, string> { [B] = "ok", [Nowhere] = "no-cell" });

        await RunJunctionCheckAsync(cut);

        // All three kinds really are on screen: otherwise "what cannot be reached" would be said
        // about cases this run never produced.
        var named = cut.Find("#junction-check-result").TextContent;
        Assert.Contains("Junction", named);             // dangling
        Assert.Contains("no junction", named);          // isolated
        Assert.Contains("not placed", named);           // unplaced

        var hint = cut.Find("#drawio-checks-goto-hint").TextContent;

        // 1. The dangling junction: named above and nowhere else. Said - and measured, by identity.
        Assert.Contains("A dangling junction is not offered below at all", hint);
        Assert.Contains("nowhere else", hint);
        Assert.DoesNotContain(TargetItemIds(cut), id => id == dangling.JunctionId.ToString());

        // 2. The unplaced device: offered below, and its click answered in words. This is the half
        // the old sentence had backwards.
        Assert.Contains("also unplaced", hint);
        Assert.Contains("no cell to land on", hint);
        Assert.Contains($"Go to {NameNowhere}", Targets(cut));
        await GoToAsync(cut, Nowhere);
        Assert.Contains("no cell in the drawing", Outcome(cut));

        // 3. And a device that IS drawn is really selected, which is the first half of the sentence.
        Assert.Contains("One click asks draw.io to select it", hint);
        await GoToAsync(cut, B);
        Assert.Contains("selected in the drawing", Outcome(cut));
    }

    /// <summary>
    /// <b>A "go to" that misses after one that landed is not painted as a success.</b> The line's
    /// words and its colour are two claims, and "nothing was selected" in the green of a success says
    /// two opposite things at once.
    /// </summary>
    /// <remarks>
    /// The sequence is the whole test, and one click would not do: what is measured is that the
    /// second outcome does not inherit the first one's colour. With only a miss the field would still
    /// be at the value it is born with, and the assertion would pass on a page that resets nothing -
    /// which is exactly what a mutation showed, deleting the reset at the head of
    /// <c>GoToDeviceAsync</c> and leaving the whole suite of the day green: 955 in memory and the two
    /// browser tests there were then.
    /// </remarks>
    [Fact]
    public async Task AGoToThatMissesAfterOneThatLandedIsNotPaintedAsASuccess()
    {
        var handler = HostWithTwoLooseDevices();
        handler.Items.Add(Unplaced(Nowhere, NameNowhere));
        var cut = await RenderWithAsync(handler, answer: null,
            answerByDevice: new Dictionary<Guid, string> { [A] = "ok", [Nowhere] = "no-cell" });

        await RunJunctionCheckAsync(cut);

        await GoToAsync(cut, A);
        Assert.Contains("selected in the drawing", Outcome(cut));
        Assert.Equal("drawio-checks-goto-reached", OutcomeTone(cut));

        await GoToAsync(cut, Nowhere);
        Assert.Contains("no cell in the drawing", Outcome(cut));
        Assert.Equal("drawio-checks-goto-missed", OutcomeTone(cut));
    }

    /// <summary>
    /// <b>Running the check again takes away the answer to the click on the OLD list.</b> An outcome
    /// belongs to the list that produced it: left standing under a list computed a moment ago it
    /// would read as the answer to a click nobody has made yet.
    /// </summary>
    /// <remarks>
    /// "Check, go to, check again" is the sequence, and nothing else in this suite staged it. The
    /// plant does not change between the two runs, so <c>ChecksPanel</c> invalidates nothing and this
    /// page's own reset is the only thing that can clear the line - measured by mutation: with that
    /// reset turned into a no-op, all 955 tests stayed green. The list is asserted to still be there
    /// afterwards, because "the line is gone" would also be true of a page on which everything had
    /// gone.
    /// </remarks>
    [Fact]
    public async Task RunningTheCheckAgainTakesAwayTheAnswerToTheClickOnTheOldList()
    {
        var cut = await RenderWithAsync(HostWithTwoLooseDevices());

        await RunJunctionCheckAsync(cut);
        await GoToAsync(cut, A);
        Assert.Contains("selected in the drawing", Outcome(cut));

        await RunJunctionCheckAsync(cut);

        Assert.Equal(2, Targets(cut).Length);
        Assert.Null(OutcomeOrNull(cut));
    }

    /// <summary>
    /// An answer this page does not know how to read is <b>not</b> taken for a success: the click
    /// says that it is not known whether the cell was selected.
    /// </summary>
    /// <remarks>
    /// It is reachable rather than theoretical, and the way it is staged is the proof: the bridge is
    /// a module loaded at run time, and an unplanned call answers null - exactly what a bridge that
    /// lost this export, or a page and a bridge out of step, would produce. Before the default
    /// branch, a null answer fell through every case and the page said nothing at all: a click that
    /// did nothing, in silence.
    /// </remarks>
    [Fact]
    public async Task AnAnswerThePageDoesNotKnowIsNotTakenForASelection()
    {
        var cut = await RenderWithAsync(HostWithTwoLooseDevices(), answer: null);

        await RunJunctionCheckAsync(cut);
        await GoToAsync(cut, A);

        var outcome = Outcome(cut);
        Assert.Contains("NOT known", outcome);
        Assert.Equal("drawio-checks-goto-missed", cut.Find("#drawio-checks-goto-outcome").GetAttribute("class"));
    }

    /// <summary>
    /// A bridge that never loaded does not make the button do nothing: the click says the drawing
    /// cannot be reached at all.
    /// </summary>
    /// <remarks>
    /// Staged by rendering with no module planned and the import made to fail, which is what happens
    /// when the file is missing or the circuit is already closing - the same case
    /// <c>DrawioEditorPageTest</c> stages for the seeding.
    /// </remarks>
    [Fact]
    public async Task WithNoBridgeAtAllTheClickSaysTheDrawingCannotBeReached()
    {
        var handler = HostWithTwoLooseDevices();
        var state = new EditorSessionState(new Uri("http://localhost:5580"), handler);
        state.UseKey("test-key");
        Services.AddSingleton(state);
        var failure = new ImportThatFails(call => call.Identifier == "import");
        failure.SetException(new Microsoft.JSInterop.JSException("module not found (test)"));
        JSInterop.AddInvocationHandler(failure);

        var cut = Render<DrawioEditor>();
        await RunJunctionCheckAsync(cut);
        await GoToAsync(cut, A);

        var outcome = Outcome(cut);
        Assert.Contains("bridge to draw.io did not load", outcome);
        Assert.Equal("drawio-checks-goto-missed", cut.Find("#drawio-checks-goto-outcome").GetAttribute("class"));
    }

    /// <summary>
    /// An <c>import()</c> that fails, the way <c>DrawioEditorPageTest.ImportFailureHandler</c> stages
    /// it: bUnit refuses to plan an import through <c>Setup&lt;IJSObjectReference&gt;</c>, and the
    /// only public way left is this minimal subclass of its own handler. A copy rather than a share
    /// because the other one is private to that class, and this is three lines.
    /// </summary>
    private sealed class ImportThatFails(Bunit.InvocationMatcher matcher)
        : Bunit.JSRuntimeInvocationHandler<Microsoft.JSInterop.IJSObjectReference>(matcher, isCatchAllHandler: false);

    // ---- Is the graph the check reads up to date? ------------------------------------------------

    /// <summary>
    /// <b>After a CREATION.</b> A device born from a drop is in the plant this page holds at once, so
    /// the very next junction check sees it - isolated, which it is, having no junction yet.
    /// </summary>
    /// <remarks>
    /// It is the first of the two questions the brief asks to MEASURE rather than deduce: a check
    /// running on a stale graph tells lies, and a device created a second ago and absent from the
    /// check would be the loudest one.
    /// <para>
    /// <b>And getting to it asks for the CELL's identifier, not the device's</b>, which is the half no
    /// test on a seeded plant could see: for a seeded device the two strings are the same. A device
    /// born from a drop keeps draw.io's own identifier until the page is reloaded, and asking for the
    /// guid would find no cell and tell the operator the shape is not drawn - while it is right there
    /// under the mouse. The same defect the rename hit on 2026-09-17, in its own place.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheJunctionCheckSeesADeviceCreatedByADropAndReachesItByItsCell()
    {
        var handler = HostWithTwoLooseDevices();
        var cut = await RenderWithAsync(handler);

        await cut.InvokeAsync(() => cut.Instance.OnDrawioCellsAdded([Dropped("dropped-1", 900, 40)]));

        // The device really was created: without this the assertions below would be measuring a drop
        // that did nothing.
        var born = Assert.Single(handler.ItemCreations);
        Assert.Equal(3, handler.Items.Count);

        await RunJunctionCheckAsync(cut);

        Assert.Contains($"Go to {born.Name}", Targets(cut));

        await GoToAsync(cut, born.ItemId);

        Assert.Equal(["dropped-1"], CellsAskedFor());
        Assert.Contains("selected in the drawing", Outcome(cut));
    }

    /// <summary>
    /// <b>After a SAVE.</b> The operator draws the junction the plant was missing and saves; the next
    /// junction check finds nothing isolated, because the page's plant is the one the save left
    /// behind.
    /// </summary>
    /// <remarks>
    /// The anchor is the check run BEFORE the save in the same test: it lists both devices. Without
    /// it, "the list is empty" would also be what a check that never ran produced.
    /// </remarks>
    [Fact]
    public async Task TheJunctionCheckSeesAJunctionWrittenByASaveWithoutAnyReload()
    {
        var handler = HostWithTwoLooseDevices();
        var cut = await RenderWithAsync(handler);

        await RunJunctionCheckAsync(cut);
        Assert.Equal([$"Go to {NameA}", $"Go to {NameB}"], Targets(cut));

        // The operator joins the two devices and saves.
        var drawn = new PlantGraph([Item(A, NameA, 10), Item(B, NameB, 500)],
            [new JunctionDto(Guid.NewGuid(), A, B, 99)], [Type()]);
        await cut.InvokeAsync(() => cut.Instance.OnDrawioSaved(DrawioDocument.Build(drawn, null)));
        Assert.Single(handler.JunctionPosts);

        await RunJunctionCheckAsync(cut);

        Assert.Empty(Targets(cut));
        Assert.Contains("No problem found", cut.Find("#junction-check-result").TextContent);
    }

    /// <summary>
    /// <b>Review finding 2.</b> The same save as above, and this time the check is <b>not</b>
    /// pressed again: the result of a check the save has just contradicted must not still be on the
    /// screen. Its three buttons said "these two devices are isolated" about a plant on which they
    /// are now joined.
    /// <para>
    /// The test above measures the other half - that a fresh check sees the junction - and it kept
    /// the stale result hidden precisely because it pressed the button again. The two belong
    /// together: without the one above, clearing the list would also be what a page that had
    /// stopped checking at all would show.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Who holds the invalidation is <c>ChecksPanel</c></b>, and it holds it by comparing the
    /// graph's <c>TopologyRevision</c> - which works because the graph is one object mutated in
    /// place. The page used to REPLACE the instance at the end of a save, and the number on the new
    /// one starts again from one: whether the comparison noticed anything depended on how many
    /// topology mutations the two graphs happened to have gone through. See <c>PlantGraph.Adopt</c>.
    /// </para>
    /// <para>
    /// <b>The drop is what makes this measurement mean something</b>, and it is measured rather than
    /// decorative. Without it the page's graph and the graph the save builds both end up at the same
    /// number by accident - one read plus one junction on either side - and the stale result is
    /// cleared by a coincidence rather than by a rule: the test would be green on the broken page.
    /// The drop is also the reviewer's own scenario, where the three buttons that stayed on screen
    /// were "Go to AAAA", "Go to BBBB" and "Go to Automation 1".
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ASaveThatJoinsTheTwoDevicesTakesAwayTheCheckResultItHasJustContradicted()
    {
        var handler = HostWithTwoLooseDevices();
        var cut = await RenderWithAsync(handler);

        await cut.InvokeAsync(() => cut.Instance.OnDrawioCellsAdded([Dropped("dropped-1", 900, 40)]));
        var born = Assert.Single(handler.ItemCreations);

        await RunJunctionCheckAsync(cut);
        Assert.Equal([$"Go to {NameA}", $"Go to {NameB}", $"Go to {born.Name}"], Targets(cut));

        // And the page SAYS it before it happens, in the same test as the behaviour, so that the
        // sentence and the thing it describes cannot drift apart - the rule the drop's half of this
        // paragraph already follows.
        Assert.Contains("A save does the same", cut.Find("#drawio-checks-scope").TextContent);

        // The operator joins the two seeded devices and saves. The drawing carries the dropped cell
        // too, or the save would propose to delete the device it stands for.
        var drawn = new PlantGraph([Item(A, NameA, 10), Item(B, NameB, 500)],
            [new JunctionDto(Guid.NewGuid(), A, B, 99)], [Type()]);
        var xml = DrawioDocument.Build(drawn, null).Replace("</root>",
            """<object id="dropped-1" label="" wcs="device"><mxCell style="x" vertex="1" parent="1"><mxGeometry x="900" y="40" width="100" height="100" as="geometry"/></mxCell></object></root>""");
        await cut.InvokeAsync(() => cut.Instance.OnDrawioSaved(xml));
        Assert.Single(handler.JunctionPosts);

        // NOT pressing the check again: this line is the whole measurement.
        Assert.Empty(Targets(cut));
        Assert.Empty(cut.FindAll("#junction-check-result"));
    }

    /// <summary>
    /// The other half of review finding 2, and the one that would have hurt more: the save that
    /// breaks HALF WAY. The plant is then in a state nobody computed, the page reads it again
    /// (<c>ReloadAfterFailureAsync</c>), and a check result left standing next to it would be
    /// speaking about a plant that no longer exists - with the operator reading it while trying to
    /// work out what the interrupted save did.
    /// </summary>
    [Fact]
    public async Task ASaveThatBreaksHalfWayTakesAwayTheCheckResultAsWell()
    {
        var handler = HostWithTwoLooseDevices();
        // The first step of the plan - the move - goes through, the junction does not: the plant is
        // left halfway and the page has to read it again.
        handler.FailRoute = "junction-post";
        var cut = await RenderWithAsync(handler);

        await RunJunctionCheckAsync(cut);
        Assert.Equal(2, Targets(cut).Length);

        var drawn = new PlantGraph([Item(A, NameA, 99), Item(B, NameB, 500)],
            [new JunctionDto(Guid.NewGuid(), A, B, 99)], [Type()]);
        await cut.InvokeAsync(() => cut.Instance.OnDrawioSaved(DrawioDocument.Build(drawn, null)));

        // The save really did break, and it really did write the step before: without these two the
        // test could be green on a save that never started.
        Assert.Contains("Save interrupted", cut.Find("#drawio-save-error").TextContent);
        Assert.Single(handler.GeometryWrites);

        Assert.Empty(Targets(cut));
        Assert.Empty(cut.FindAll("#junction-check-result"));
    }

    /// <summary>
    /// A list of "go to" buttons never outlives the check that produced it: the moment the plant's
    /// topology moves, the buttons go.
    /// </summary>
    /// <remarks>
    /// <b>What it measures is the outcome, and WHO holds it is worth knowing</b>: it is
    /// <c>ChecksPanel</c>, which clears its own result - and the set it handed this page - the moment
    /// the graph's topology revision moves under it. A second guard of this page's own was built and
    /// then deleted, because this very test stayed green without it: see the remarks of
    /// <c>DrawioEditor.IsolatedDevicesToVisit</c>. So this test does NOT prove a net of ours; it
    /// proves that the buttons go, which is what the operator sees, and it goes red the day that
    /// stops being true for any reason at all.
    /// </remarks>
    [Fact]
    public async Task TheGoToButtonsDoNotOutliveTheCheckThatProducedThem()
    {
        var handler = HostWithTwoLooseDevices();
        var cut = await RenderWithAsync(handler);

        await RunJunctionCheckAsync(cut);
        Assert.Equal(2, Targets(cut).Length);

        // And the page SAYS it before it happens - in the same test as the behaviour, so that the
        // sentence and the thing it describes cannot drift apart. Dropping a shape on the canvas is
        // the commonest gesture there is on this page, and results that vanish with no reason given
        // are the family of defect this branch produces most.
        var scope = cut.Find("#drawio-checks-scope").TextContent;
        Assert.Contains("Creating a device", scope);
        Assert.Contains("the results go with it", scope);

        // A creation moves the topology: a device is born.
        await cut.InvokeAsync(() => cut.Instance.OnDrawioCellsAdded([Dropped("dropped-2", 900, 40)]));

        Assert.Empty(Targets(cut));
        Assert.Empty(cut.FindAll("#junction-check-result"));
    }

    // ---- With the engine running -----------------------------------------------------------------

    /// <summary>
    /// <b>The checks READ.</b> With the engine in Run the page writes nothing - and both checks run
    /// all the same, and the cell can still be reached. Looking without touching is exactly what one
    /// wants then.
    /// </summary>
    /// <remarks>
    /// The banner is asserted first: without it this test would be green on a page that simply never
    /// read the engine's status, and it would prove nothing about the guard it is here for.
    /// </remarks>
    [Fact]
    public async Task WithTheEngineRunningBothChecksStillWorkAndTheCellCanStillBeReached()
    {
        var handler = HostWithTwoLooseDevices();
        handler.NavStatus = NavigatorStatusTypes.Run;
        var cut = await RenderWithAsync(handler);

        Assert.Contains("the engine is running", cut.Find("#drawio-engine-banner").TextContent);

        await RunJunctionCheckAsync(cut);
        Assert.Equal(2, Targets(cut).Length);

        await GoToAsync(cut, A);
        Assert.Equal([A.ToString()], CellsAskedFor());
        Assert.Contains("selected in the drawing", Outcome(cut));

        await RunConfigCheckAsync(cut);
        Assert.Single(handler.WmsConfigurationCheckRequests);
        Assert.Contains("checked successfully", cut.Find("#config-check-result").TextContent);

        // And nothing was written to the plant by any of it.
        Assert.Empty(handler.Writes);
        Assert.Equal(0, handler.EditLockRequests);
    }

    // ---- The configuration check -----------------------------------------------------------------

    /// <summary>
    /// The configuration check really goes to the host's route, and the rows it hands back reach the
    /// screen as a table.
    /// </summary>
    [Fact]
    public async Task TheConfigurationCheckAsksTheHostAndShowsWhatItAnswered()
    {
        var handler = HostWithTwoLooseDevices();
        handler.WmsConfigurationWarnings.Add(new WmsConfigurationWarning(
            new Dictionary<string, string?> { ["DESC_MAGAZZINO"] = "MAG1", ["DESCRIZIONE"] = "cella orfana" }));
        var cut = await RenderWithAsync(handler);

        await RunConfigCheckAsync(cut);

        Assert.Equal("/api/v1/wms/configuration-check?mode=0", Assert.Single(handler.WmsConfigurationCheckRequests));
        var table = cut.Find("#config-check-table").TextContent;
        Assert.Contains("MAG1", table);
        Assert.Contains("cella orfana", table);
    }

    /// <summary>
    /// A configuration check that FAILS is shown as a failed check and not as "no problems": the
    /// difference is the whole reason <c>ChecksPanel</c> keeps the two states apart, and this page
    /// must not flatten it.
    /// </summary>
    [Fact]
    public async Task AConfigurationCheckThatFailsIsShownAsAFailureAndNotAsNoProblems()
    {
        var handler = HostWithTwoLooseDevices();
        handler.BrokenReads.Add("configuration-check");
        var cut = await RenderWithAsync(handler);

        await RunConfigCheckAsync(cut);

        Assert.Contains("check failed", cut.Find("#config-check-error").TextContent);
        Assert.Empty(cut.FindAll("#config-check-result"));
        // And the summary line is not left saying the check is still working.
        Assert.DoesNotContain("is running", cut.Find("#drawio-checks-summary").TextContent);
    }

    /// <summary>
    /// <b>While the host is thinking, the box says so on its summary line</b> - the line that can be
    /// read with the box SHUT, which is where the operator will be: the panel's own button greys
    /// itself out, and a greyed button inside a shut box is visible to nobody.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Staged with the host's answer held on a gate, which is the same shape the write tests of this
    /// page use for their overlaps. The click's task is deliberately not awaited while the gate is
    /// shut: that is the only moment at which "it is working" is a fact, and asserting before and
    /// after it would measure nothing at all.
    /// </para>
    /// <para>
    /// The assertion that the click has NOT completed is not decoration: without it a gate that
    /// failed to hold - a route renamed, a handler branch moved - would let this test pass while
    /// measuring a check that had already finished.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheSummaryLineSaysTheConfigurationCheckIsWorkingWhileTheHostIsThinking()
    {
        var handler = HostWithTwoLooseDevices();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.WmsConfigurationCheckGate = held;
        var cut = await RenderWithAsync(handler);

        Assert.DoesNotContain("is running", cut.Find("#drawio-checks-summary").TextContent);

        var check = RunConfigCheckAsync(cut);
        await handler.WmsConfigurationCheckGateReached.Task;
        Assert.False(check.IsCompleted, "The configuration check did not stay inside its own GET: the window was never staged.");

        cut.WaitForAssertion(() => Assert.Contains("is running", cut.Find("#drawio-checks-summary").TextContent));

        held.SetResult();
        await check;

        cut.WaitForAssertion(() => Assert.DoesNotContain("is running", cut.Find("#drawio-checks-summary").TextContent));
        Assert.Contains("checked successfully", cut.Find("#config-check-result").TextContent);
    }

    // ---- The count on the checks' button of the bar ------------------------------------------------

    /// <summary>
    /// The number on the checks' button (passata grafica) goes down as well as up: a drop that moves
    /// the plant takes the junction check's result away - the panel says so ("the results go with
    /// it") - and the number goes with it, back to no mark at all. Without ChecksPanel reporting the
    /// drop (OnProblemCountChanged) the bar went on saying "2 problems" on the button of a window whose
    /// list was gone.
    /// </summary>
    [Fact]
    public async Task TheCountOnTheChecksButtonGoesWhenAMoveOfThePlantTakesTheResultAway()
    {
        var handler = HostWithTwoLooseDevices();
        var cut = await RenderWithAsync(handler);
        await RunJunctionCheckAsync(cut);
        cut.WaitForAssertion(() => Assert.Equal("2", cut.Find("#drawio-checks-toggle #drawio-checks-count").TextContent.Trim()));
        Assert.Equal("Plant checks, 2 problems", cut.Find("#drawio-checks-toggle").GetAttribute("aria-label"));

        await cut.InvokeAsync(() => cut.Instance.OnDrawioCellsAdded([Dropped("dropped-1", 900, 40)]));
        Assert.Single(handler.ItemCreations);

        Assert.Empty(cut.FindAll("#junction-check-result"));
        Assert.Empty(cut.FindAll("#drawio-checks-count"));
        Assert.Equal("Plant checks", cut.Find("#drawio-checks-toggle").GetAttribute("aria-label"));
    }

    /// <summary>
    /// The configuration check run again drops its old rows as it starts - they are no longer anybody's
    /// answer - and the number on the checks' button drops them too while it runs: the junction
    /// check's two, not five. Its answer brings the rows, and the number, back.
    /// </summary>
    [Fact]
    public async Task TheCountOnTheChecksButtonLeavesOutTheConfigurationsRowsWhileItRunsAgain()
    {
        var handler = HostWithTwoLooseDevices();
        handler.WmsConfigurationWarnings.Add(new WmsConfigurationWarning(new Dictionary<string, string?> { ["DESCRIZIONE"] = "cella orfana" }));
        handler.WmsConfigurationWarnings.Add(new WmsConfigurationWarning(new Dictionary<string, string?> { ["DESCRIZIONE"] = "cella doppia" }));
        handler.WmsConfigurationWarnings.Add(new WmsConfigurationWarning(new Dictionary<string, string?> { ["DESCRIZIONE"] = "porta senza cella" }));
        var cut = await RenderWithAsync(handler);
        await RunJunctionCheckAsync(cut);
        await RunConfigCheckAsync(cut);
        cut.WaitForAssertion(() => Assert.Equal("5", cut.Find("#drawio-checks-count").TextContent.Trim()));

        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.WmsConfigurationCheckGate = held;
        var again = RunConfigCheckAsync(cut);
        await handler.WmsConfigurationCheckGateReached.Task;
        Assert.False(again.IsCompleted, "The configuration check did not stay inside its own GET: the running check was never staged.");
        cut.WaitForAssertion(() => Assert.Equal("2", cut.Find("#drawio-checks-count").TextContent.Trim()));

        held.SetResult();
        await again;
        cut.WaitForAssertion(() => Assert.Equal("5", cut.Find("#drawio-checks-count").TextContent.Trim()));
    }

    /// <summary>
    /// The cell a drop leaves on the canvas, built from <c>DrawioLibrary</c>'s own entry the way
    /// <c>DrawioEditorCreateTest.Dropped</c> does: the mark and the type attribute are read out of the
    /// XML draw.io is really given, not written by hand here.
    /// </summary>
    private static DrawioAddedCell Dropped(string cellId, double x, double y)
    {
        var entries = System.Text.Json.JsonDocument.Parse(DrawioLibrary.Build([Type()]));
        var xml = entries.RootElement[0].GetProperty("entries")[0].GetProperty("libs")[0]
            .GetProperty("data")[0].GetProperty("xml").GetString()!;
        var cell = Assert.Single(DrawioReader.Read(xml).Devices);
        var declaredType = (string?)System.Xml.Linq.XDocument.Parse(xml).Descendants("object").Single()
            .Attribute(DrawioLibrary.TypeAttribute);
        return new DrawioAddedCell(cellId, cell.Mark, declaredType,
            DrawioDocument.DefaultLayerId, x, y, cell.Width, cell.Height);
    }
}
