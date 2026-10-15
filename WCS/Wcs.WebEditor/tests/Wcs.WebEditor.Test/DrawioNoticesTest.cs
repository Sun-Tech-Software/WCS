using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Drawio;
using Wcs.WebEditor.Drawio.Components;
using Wcs.WebEditor.Drawio.Coordination;
using Wcs.WebEditor.Model;
using Wcs.WebEditor.Pages;
using Wcs.WebEditor.Session;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

/// <summary>
/// The row of outcomes under the bar (passata grafica, 2026-09-29): one line per outcome in its tone,
/// "Details" opening the whole report in a window, the X closing a transient outcome, at most two
/// lines shown with the newest on top - first the component alone, then each outcome as the page puts
/// it in the row, with the ids of before still where the tests and the browser's helpers read them.
/// </summary>
public sealed class DrawioNoticesTest : PlantCanvasBunitContext
{
    private const string BridgeModule = "./_content/Wcs.WebEditor/drawio-bridge.js";

    private static readonly Guid A = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid B = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Ghost = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid J = Guid.Parse("44444444-4444-4444-4444-444444444444");

    // ---- The row alone ------------------------------------------------------------------------------

    private static DrawioNoticeLine Notice(string id, DrawioNoticeTone tone = DrawioNoticeTone.Ok, bool dismissable = true,
        RenderFragment? details = null, long occurrence = 0) =>
        new(id, tone, $"The line of {id}.", dismissable, details) { Occurrence = occurrence };

    private IRenderedComponent<DrawioNotices> RenderRow(params DrawioNoticeLine[] notices) =>
        Render<DrawioNotices>(p => p.Add(n => n.Notices, notices));

    private static void RenderAgain(IRenderedComponent<DrawioNotices> cut, params DrawioNoticeLine[] notices) =>
        cut.Render(p => p.Add(n => n.Notices, notices));

    /// <summary>The ids of the lines on screen, top to bottom: hidden ones (behind "+N more") left out.</summary>
    private static string[] LinesShown(IRenderedComponent<DrawioNotices> cut) =>
        [.. cut.FindAll("#drawio-notices > .drawio-notices-line").Where(line => !line.HasAttribute("hidden")).Select(line => line.Id!)];

    private static string[] ToneClasses(IElement line) =>
        [.. line.ClassList.Where(name => name.StartsWith("drawio-notices-", StringComparison.Ordinal) && name != "drawio-notices-line")];

    [Theory]
    [InlineData(DrawioNoticeTone.Ok, "drawio-notices-ok")]
    [InlineData(DrawioNoticeTone.Warn, "drawio-notices-warn")]
    [InlineData(DrawioNoticeTone.Bad, "drawio-notices-bad")]
    public void EachOutcomeIsOneLineInItsTone(DrawioNoticeTone tone, string expected)
    {
        var cut = RenderRow(Notice("x", tone));

        var line = Assert.Single(cut.FindAll("#drawio-notices > .drawio-notices-line"));
        Assert.Equal("x-notice", line.Id);
        Assert.Equal(expected, Assert.Single(ToneClasses(line)));
        Assert.Equal("The line of x.", line.QuerySelector("p.drawio-notices-text")!.TextContent);
    }

    [Fact]
    public void DetailsOpensTheWholeReportInAWindowWhereItWasAlreadyThereShut()
    {
        RenderFragment report = builder => builder.AddMarkupContent(0, "<p id=\"x-report\">The whole report.</p>");
        var cut = RenderRow(Notice("x", details: report));

        // Shut, the report is on the page all the same, inside its window: read by id, it is found.
        var window = cut.Find("#x-details");
        Assert.Contains("drawio-window", window.ClassList);
        Assert.True(window.HasAttribute("hidden"));
        Assert.Equal("The whole report.", cut.Find("#x-details #x-report").TextContent);
        Assert.Equal("false", cut.Find("#x-details-toggle").GetAttribute("aria-expanded"));

        cut.Find("#x-details-toggle").Click();
        Assert.False(cut.Find("#x-details").HasAttribute("hidden"));
        Assert.Equal("true", cut.Find("#x-details-toggle").GetAttribute("aria-expanded"));

        cut.Find("#x-details-close").Click();
        Assert.True(cut.Find("#x-details").HasAttribute("hidden"));
    }

    [Fact]
    public void ALineWithNothingBehindItHasNoDetails()
    {
        var cut = RenderRow(Notice("x"));
        Assert.Empty(cut.FindAll("#x-details-toggle"));
        Assert.Empty(cut.FindAll("#x-details"));
    }

    [Fact]
    public void TheXClosesATransientOutcomeAndAPermanentOneHasNone()
    {
        RenderFragment report = builder => builder.AddMarkupContent(0, "<p id=\"t-report\">report</p>");
        var cut = RenderRow(Notice("t", dismissable: true, details: report), Notice("p", dismissable: false));

        Assert.Empty(cut.FindAll("#p-dismiss"));
        Assert.Equal("Dismiss", cut.Find("#t-dismiss").GetAttribute("aria-label"));

        cut.Find("#t-dismiss").Click();

        Assert.Empty(cut.FindAll("#t-notice"));
        // Its report goes with it: nothing is left open behind a line that is gone.
        Assert.Empty(cut.FindAll("#t-details"));
        Assert.Equal(["p-notice"], LinesShown(cut));
    }

    [Fact]
    public void ThreeOutcomesShowTwoLinesAndAPlusOneMoreThatOpensTheWholeList()
    {
        var cut = RenderRow(Notice("a"), Notice("b"), Notice("c"));

        Assert.Equal(["a-notice", "b-notice"], LinesShown(cut));
        // The third is on the page, hidden, with whatever id it carries.
        Assert.True(cut.Find("#c-notice").HasAttribute("hidden"));
        var more = cut.Find("#drawio-notices-more");
        Assert.Equal("+1 more", more.TextContent);
        Assert.Equal("false", more.GetAttribute("aria-expanded"));
        Assert.Equal("b-notice", more.Closest(".drawio-notices-line")!.Id);

        more.Click();
        Assert.Equal(["a-notice", "b-notice", "c-notice"], LinesShown(cut));
        Assert.Equal("Show fewer", cut.Find("#drawio-notices-more").TextContent);
        Assert.Equal("true", cut.Find("#drawio-notices-more").GetAttribute("aria-expanded"));

        cut.Find("#drawio-notices-more").Click();
        Assert.Equal(["a-notice", "b-notice"], LinesShown(cut));
    }

    [Fact]
    public void TwoOutcomesHaveNoPlusMore()
    {
        var cut = RenderRow(Notice("a"), Notice("b"));
        Assert.Equal(["a-notice", "b-notice"], LinesShown(cut));
        Assert.Empty(cut.FindAll("#drawio-notices-more"));
    }

    [Fact]
    public void TheNewestOutcomeIsOnTop()
    {
        var cut = RenderRow(Notice("a"));
        RenderAgain(cut, Notice("a"), Notice("b"));
        Assert.Equal(["b-notice", "a-notice"], LinesShown(cut));

        // The same outcome again (the same occurrence) keeps its place; a new occurrence of it is new.
        RenderAgain(cut, Notice("a"), Notice("b"));
        Assert.Equal(["b-notice", "a-notice"], LinesShown(cut));
        RenderAgain(cut, Notice("a", occurrence: 1), Notice("b"));
        Assert.Equal(["a-notice", "b-notice"], LinesShown(cut));

        // One that goes and comes back is new too.
        RenderAgain(cut, Notice("a", occurrence: 1));
        RenderAgain(cut, Notice("a", occurrence: 1), Notice("b"));
        Assert.Equal(["b-notice", "a-notice"], LinesShown(cut));
    }

    /// <summary>
    /// The windows of the reports stay in the page's order whatever the row's (an element moved in the
    /// page loses the keyboard's focus), and two of them do not open one exactly on the other.
    /// </summary>
    [Fact]
    public void TheReportsWindowsStayInThePagesOrderAndOpenApart()
    {
        RenderFragment report = builder => builder.AddMarkupContent(0, "<p>report</p>");
        var cut = RenderRow(Notice("a", details: report));
        RenderAgain(cut, Notice("a", details: report), Notice("b", details: report));

        Assert.Equal(["b-notice", "a-notice"], LinesShown(cut));
        Assert.Equal(["a-details", "b-details"], cut.FindAll("#drawio-notices > .drawio-window").Select(window => window.Id));
        var placeOf = JSInterop.Invocations
            .Where(call => call.Identifier == "attach" && call.Arguments.Count == 4 && call.Arguments[2] is DrawioWindowPlace)
            .ToDictionary(call => (string)call.Arguments[1]!, call => (DrawioWindowPlace)call.Arguments[2]!);
        Assert.Equal(["a-details", "b-details"], placeOf.Keys.Order());
        Assert.NotEqual(placeOf["a-details"], placeOf["b-details"]);
    }

    [Fact]
    public void OutcomesArrivingTogetherKeepThePagesOrder()
    {
        var cut = RenderRow(Notice("a"), Notice("b"));
        Assert.Equal(["a-notice", "b-notice"], LinesShown(cut));
    }

    /// <summary>
    /// Every line has a "Details" and an X of the same name: each is described by its own line's
    /// sentence (aria-describedby), so that a screen reader can tell whose it is.
    /// </summary>
    [Fact]
    public void TheButtonsOfALineAreDescribedByItsSentence()
    {
        RenderFragment report = builder => builder.AddMarkupContent(0, "<p>report</p>");
        var cut = RenderRow(Notice("a", details: report), Notice("b", details: report));

        foreach (var id in new[] { "a", "b" })
        {
            var sentence = cut.Find($"#{id}-notice > .drawio-notices-text");
            Assert.False(string.IsNullOrEmpty(sentence.Id), $"The sentence of {id} has no id: nothing can name it.");
            Assert.Equal(sentence.Id, cut.Find($"#{id}-details-toggle").GetAttribute("aria-describedby"));
            Assert.Equal(sentence.Id, cut.Find($"#{id}-dismiss").GetAttribute("aria-describedby"));
        }

        Assert.NotEqual(cut.Find("#a-dismiss").GetAttribute("aria-describedby"), cut.Find("#b-dismiss").GetAttribute("aria-describedby"));
    }

    /// <summary>
    /// The element the row last handed the keyboard's focus on to - through the windows module's
    /// focusIfLost, which takes it only if it was lost meanwhile (queue item 38) - and how many hand-offs
    /// the row has asked for so far.
    /// </summary>
    private static (ElementReference Element, int HandOffs) LastHandedTo(BunitJSModuleInterop windows)
    {
        var handOffs = windows.Invocations["focusIfLost"];
        return (Assert.IsType<ElementReference>(handOffs[^1].Arguments[0]), handOffs.Count);
    }

    /// <summary>
    /// The element a component holds in one of its <c>@ref</c> fields. bUnit 2 does not write the
    /// reference's id on the rendered element (its <c>blazor:elementReference</c> is empty), so the
    /// element the page focused is told by the reference the component itself holds for it.
    /// </summary>
    private static ElementReference ReferenceIn(object component, string field) =>
        (ElementReference)component.GetType().GetField(field, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(component)!;

    /// <summary>
    /// A button the gesture takes away does not drop the keyboard's focus on the page's body: "+N more"
    /// becomes "Show fewer" on the last line - another element on another line - and the focus follows
    /// it there, and back; the X goes with its line, and the focus goes to the row that holds the others.
    /// Each hand-off goes through the windows module's focusIfLost, never straight to the element: it
    /// lands a round trip after the render, and a focus the operator has put elsewhere meanwhile stays
    /// there (queue item 38; the browser's side is DrawioWindowsBrowserTest's).
    /// </summary>
    [Fact]
    public void PlusMoreAndShowFewerTakeTheFocusWhereTheyGoAndTheXToTheRow()
    {
        var windows = JSInterop.SetupModule(DrawioWindow.ModulePath);
        windows.SetupVoid("focusIfLost", _ => true).SetVoidResult();
        var cut = RenderRow(Notice("a"), Notice("b"), Notice("c"));

        cut.Find("#drawio-notices-more").Click();
        Assert.Equal("c-notice", cut.Find("#drawio-notices-more").Closest(".drawio-notices-line")!.Id);
        var handedTo = LastHandedTo(windows);
        Assert.Equal((ReferenceIn(cut.Instance, "_more").Id, 1), (handedTo.Element.Id, handedTo.HandOffs));

        cut.Find("#drawio-notices-more").Click();
        Assert.Equal("b-notice", cut.Find("#drawio-notices-more").Closest(".drawio-notices-line")!.Id);
        handedTo = LastHandedTo(windows);
        Assert.Equal((ReferenceIn(cut.Instance, "_more").Id, 2), (handedTo.Element.Id, handedTo.HandOffs));

        cut.Find("#a-dismiss").Click();
        handedTo = LastHandedTo(windows);
        Assert.Equal((ReferenceIn(cut.Instance, "_row").Id, 3), (handedTo.Element.Id, handedTo.HandOffs));
        Assert.Equal("-1", cut.Find("#drawio-notices").GetAttribute("tabindex"));
        Assert.Empty(JSInterop.Invocations["Blazor._internal.domWrapper.focus"]);
    }

    /// <summary>
    /// "Details" on a report's window that is open but behind another window brings it to the front and
    /// leaves it open, as the bar's buttons do (the owner's decision D1); on the one in front of the open
    /// windows it shuts it. Which is in front is the windows module's to say (bringToFrontIfBehind).
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void DetailsOnAnOpenWindowBringsItToTheFrontIfItIsBehindAndShutsItIfNot(bool behind, bool shut)
    {
        var windows = JSInterop.SetupModule(DrawioWindow.ModulePath);
        windows.Setup<bool>("bringToFrontIfBehind", _ => true).SetResult(behind);
        RenderFragment report = builder => builder.AddMarkupContent(0, "<p>report</p>");
        var cut = RenderRow(Notice("x", details: report));

        cut.Find("#x-details-toggle").Click();
        Assert.False(cut.Find("#x-details").HasAttribute("hidden"));
        Assert.Empty(windows.Invocations["bringToFrontIfBehind"]);

        cut.Find("#x-details-toggle").Click();

        Assert.Equal("x-details", Assert.Single(windows.Invocations["bringToFrontIfBehind"]).Arguments[0]);
        Assert.Equal(shut, cut.Find("#x-details").HasAttribute("hidden"));
        Assert.Equal(shut ? "false" : "true", cut.Find("#x-details-toggle").GetAttribute("aria-expanded"));
    }

    [Fact]
    public void AnOutcomeClosedWithItsXStaysClosedUntilANewOccurrenceOfIt()
    {
        var cut = RenderRow(Notice("a"), Notice("b"));
        cut.Find("#a-dismiss").Click();

        RenderAgain(cut, Notice("a"), Notice("b"));
        Assert.Equal(["b-notice"], LinesShown(cut));

        RenderAgain(cut, Notice("a", occurrence: 1), Notice("b"));
        Assert.Equal(["a-notice", "b-notice"], LinesShown(cut));
    }

    // ---- What the reports say in the row ------------------------------------------------------------

    private static DrawioSaveReportState Save(string? error = null, string? summary = null, string[]? unconfirmed = null,
        string[]? notWritten = null, DrawioRefusal[]? refusals = null, DrawioNotice[]? notices = null) =>
        new(error, summary, [], unconfirmed ?? [], notWritten ?? [], refusals ?? [], notices ?? []);

    public static TheoryData<string, DrawioSaveReportState, DrawioNoticeTone, string> SaveOutcomes => new()
    {
        { "all written", Save(summary: "Save succeeded: 2 writes."), DrawioNoticeTone.Ok, "Save succeeded: 2 writes." },
        { "nothing to write", Save(summary: "Save: there was nothing to write."), DrawioNoticeTone.Ok, "Save: there was nothing to write." },
        { "a choice made for whoever draws", Save(summary: "Save succeeded: 1 write.", notices: [new DrawioNotice("cell-2", "priority 1")]),
            DrawioNoticeTone.Warn, "Save succeeded: 1 write." },
        { "a write with no answer", Save(summary: "Save finished: 2 writes, 1 piece not confirmed - see below.", unconfirmed: ["the move"]),
            DrawioNoticeTone.Warn, "Save finished: 2 writes, 1 piece not confirmed." },
        { "a refusal", Save(summary: "Save finished: 0 writes, 1 piece not written - see below.", refusals: [new DrawioRefusal("cell-1", "no")]),
            DrawioNoticeTone.Bad, "Save finished: 0 writes, 1 piece not written." },
        { "a piece not written", Save(summary: "Save finished: 1 write, 1 piece not written - see below.", notWritten: ["the arrow"]),
            DrawioNoticeTone.Bad, "Save finished: 1 write, 1 piece not written." },
        { "the round stopped", Save(error: "The plant is not loaded yet: the drawing was not written. Try again in a moment."),
            DrawioNoticeTone.Bad, "The plant is not loaded yet: the drawing was not written. Try again in a moment." },
        // Red and orange in one round (DrawioGeometryCoordinatorTest makes such rounds): red wins.
        { "not written and not confirmed", Save(summary: "Save finished: 4 writes, 2 pieces not written and 1 piece not confirmed - see below.",
                notWritten: ["the arrow", "the move"], unconfirmed: ["the name"]),
            DrawioNoticeTone.Bad, "Save finished: 4 writes, 2 pieces not written and 1 piece not confirmed." },
        { "refused and a choice made for whoever draws", Save(summary: "Save finished: 1 write, 1 piece not written - see below.",
                refusals: [new DrawioRefusal("cell-1", "no")], notices: [new DrawioNotice("cell-2", "priority 1")]),
            DrawioNoticeTone.Bad, "Save finished: 1 write, 1 piece not written." },
    };

    [Theory]
    [MemberData(nameof(SaveOutcomes))]
    public void TheSavesLineIsItsSummaryOrItsErrorInTheToneOfTheRound(string what, DrawioSaveReportState state, DrawioNoticeTone tone, string line)
    {
        var notice = DrawioSaveReport.NoticeOf(state, round: 7);

        Assert.NotNull(notice);
        Assert.Equal(tone, notice.Tone);
        Assert.Equal(line, notice.Line);
        Assert.True(notice.Dismissable, what);
        Assert.Equal(7, notice.Occurrence);
        Assert.NotNull(notice.Details);
    }

    [Fact]
    public void ASaveThatLeftNothingToSayHasNoLine() => Assert.Null(DrawioSaveReport.NoticeOf(Save(), round: 1));

    private static DrawioCreationReportState Creation(string? error = null, CreationLine[]? created = null, string[]? notCreated = null,
        bool unavailable = false, int notKnown = 0) =>
        new(error, created ?? [], [.. (notCreated ?? []).Select(line => new DrawioSaid(line))], unavailable, "A shape dropped on the drawing can no longer become a device.", Creating: false)
        {
            NotKnown = notKnown,
        };

    /// <summary>A line of the report for a shape whose request got no answer: its device may be on the plant all the same.</summary>
    private const string NoAnswer = "drawio-2: the request got no answer and whether the device was created is NOT KNOWN.";

    private static CreationLine Born(bool named = true) => new("Automation (x) at (1, 2), named \"Automation 1\".", named);

    public static TheoryData<string, DrawioCreationReportState, DrawioNoticeTone, string> CreationOutcomes => new()
    {
        { "one born", Creation(created: [Born()]), DrawioNoticeTone.Ok, "Created 1 device from the shapes dropped on the drawing." },
        { "two born", Creation(created: [Born(), Born()]), DrawioNoticeTone.Ok, "Created 2 devices from the shapes dropped on the drawing." },
        { "one born without its name", Creation(created: [Born(named: false)]), DrawioNoticeTone.Warn,
            "Created 1 device from the shapes dropped on the drawing; the drawing did not take its name." },
        { "one of two without its name", Creation(created: [Born(), Born(named: false)]), DrawioNoticeTone.Warn,
            "Created 2 devices from the shapes dropped on the drawing; the drawing did not take the name of 1 of them." },
        { "one born, one not", Creation(created: [Born()], notCreated: ["drawio-2: no size."]), DrawioNoticeTone.Bad,
            "Created 1 device from the shapes dropped on the drawing; 1 shape was NOT created." },
        { "one born, two not", Creation(created: [Born()], notCreated: ["drawio-2: no size.", "drawio-3: no size."]), DrawioNoticeTone.Bad,
            "Created 1 device from the shapes dropped on the drawing; 2 shapes were NOT created." },
        { "two born, neither named", Creation(created: [Born(named: false), Born(named: false)]), DrawioNoticeTone.Warn,
            "Created 2 devices from the shapes dropped on the drawing; the drawing did not take their names." },
        { "none born", Creation(notCreated: ["drawio-1: no size.", "drawio-2: no size."]), DrawioNoticeTone.Bad,
            "2 shapes dropped on the drawing were NOT created." },
        // A request with no answer: NOT KNOWN whether the device was born - orange, as the save's unconfirmed
        // pieces are, and the line says what to do before dragging the shape again.
        { "one born, one not known", Creation(created: [Born()], notCreated: [NoAnswer], notKnown: 1), DrawioNoticeTone.Warn,
            "Created 1 device from the shapes dropped on the drawing; for 1 shape it is NOT KNOWN whether its device was created - look before dragging it again." },
        { "two not known", Creation(notCreated: [NoAnswer, NoAnswer], notKnown: 2), DrawioNoticeTone.Warn,
            "For 2 shapes dropped on the drawing it is NOT KNOWN whether their devices were created - look before dragging them again." },
        // Red and orange together: red wins.
        { "one not, one not known", Creation(notCreated: ["drawio-1: no size.", NoAnswer], notKnown: 1), DrawioNoticeTone.Bad,
            "1 shape dropped on the drawing was NOT created; for 1 shape more it is NOT KNOWN whether its device was created - look before dragging it again." },
        { "refused whole", Creation(error: "The plant is not loaded yet: nothing was created.", notCreated: ["drawio-1: the plant was not loaded."]),
            DrawioNoticeTone.Bad, "The plant is not loaded yet: nothing was created." },
    };

    [Theory]
    [MemberData(nameof(CreationOutcomes))]
    public void TheCreationsLineCountsWhatTheDropCameToInItsTone(string what, DrawioCreationReportState state, DrawioNoticeTone tone, string line)
    {
        var notice = DrawioCreationReport.NoticeOf(state, report: 3);

        Assert.NotNull(notice);
        Assert.Equal(tone, notice.Tone);
        Assert.Equal(line, notice.Line);
        Assert.True(notice.Dismissable, what);
        Assert.NotNull(notice.Details);
    }

    /// <summary>
    /// The same report saying the same is the same outcome - its X holds from one render to the next -
    /// and the next report is a new one even in the same words.
    /// </summary>
    [Fact]
    public void TheSameCreationReportIsTheSameOutcomeAndTheNextReportANewOne()
    {
        var said = Creation(created: [Born()], notCreated: ["drawio-2: no size."]);
        var saidAgain = Creation(created: [Born()], notCreated: ["drawio-2: no size."]);

        Assert.Equal(DrawioCreationReport.NoticeOf(said, report: 3)!.Occurrence, DrawioCreationReport.NoticeOf(saidAgain, report: 3)!.Occurrence);
        Assert.NotEqual(DrawioCreationReport.NoticeOf(said, report: 3)!.Occurrence, DrawioCreationReport.NoticeOf(said, report: 4)!.Occurrence);
    }

    /// <summary>
    /// A drop drains its shapes one by one and says so after each, and shapes dropped meanwhile join
    /// its report: the same report, grown. Each way it grows is something new to say, and a new
    /// outcome - one the operator closed after the first device comes back.
    /// </summary>
    public static TheoryData<string, DrawioCreationReportState, DrawioCreationReportState> CreationGrowth => new()
    {
        { "a device more", Creation(created: [Born()]), Creation(created: [Born(), Born()]) },
        { "a name the drawing did not take", Creation(created: [Born()]), Creation(created: [Born(named: false)]) },
        { "a shape NOT created", Creation(created: [Born()]), Creation(created: [Born()], notCreated: ["drawio-2: whether the device was created is NOT KNOWN."]) },
        { "the error that stopped the drop", Creation(created: [Born()], notCreated: ["drawio-2: no size."]),
            Creation(error: "The creation stopped on an unforeseen failure.", created: [Born()], notCreated: ["drawio-2: no size."]) },
    };

    [Theory]
    [MemberData(nameof(CreationGrowth))]
    public void ACreationReportThatGrewIsANewOutcome(string what, DrawioCreationReportState before, DrawioCreationReportState after)
    {
        var closed = DrawioCreationReport.NoticeOf(before, report: 3)!;
        var grown = DrawioCreationReport.NoticeOf(after, report: 3)!;

        Assert.True(closed.Occurrence != grown.Occurrence, $"{what}: the report grew and is still outcome {grown.Occurrence}.");
    }

    /// <summary>
    /// The undrawn devices' line lasts as long as they do, and the same count is the same outcome - it
    /// keeps its place in the row. A count that changes is news, a new outcome: it goes on top, out from
    /// behind "+N more" if that is where newer outcomes had pushed it.
    /// </summary>
    [Fact]
    public void TheUndrawnDevicesLineIsANewOutcomeWhenTheirCountChanges()
    {
        var one = HostWithAGhost();
        var two = HostWithAGhost();
        var ghost2 = Guid.Parse("55555555-5555-5555-5555-555555555555");
        two.Items.Add(Item(ghost2, DeviceTypes.None, 1200));

        var before = DrawioUndrawnReport.NoticeOf(GraphOf(one))!;
        Assert.Equal(before.Occurrence, DrawioUndrawnReport.NoticeOf(GraphOf(HostWithAGhost()))!.Occurrence);
        var after = DrawioUndrawnReport.NoticeOf(GraphOf(two))!;
        Assert.NotEqual(before.Occurrence, after.Occurrence);

        // The undrawn line first, then two newer outcomes that push it behind "+N more".
        var cut = RenderRow(before);
        RenderAgain(cut, before, Notice("save"));
        RenderAgain(cut, before, Notice("save"), Notice("cut"));
        Assert.True(cut.Find("#drawio-undrawn-notice").HasAttribute("hidden"), "The test is blind: the undrawn line was not behind \"+N more\".");

        RenderAgain(cut, before, Notice("save"), Notice("cut"));
        Assert.True(cut.Find("#drawio-undrawn-notice").HasAttribute("hidden"), "The same count brought the undrawn line back on top.");

        RenderAgain(cut, after, Notice("save"), Notice("cut"));
        Assert.Equal("drawio-undrawn-notice", LinesShown(cut)[0]);
    }

    [Fact]
    public void ADropThatLeftNothingToSayHasNoLineAndTheDeafBridgeHasItsOwn()
    {
        Assert.Null(DrawioCreationReport.NoticeOf(Creation(), report: 1));
        Assert.Null(DrawioCreationReport.UnavailableNoticeOf(Creation()));

        var deaf = DrawioCreationReport.UnavailableNoticeOf(Creation(unavailable: true));
        Assert.NotNull(deaf);
        Assert.Equal(DrawioNoticeTone.Bad, deaf.Tone);
        Assert.False(deaf.Dismissable);
        Assert.Null(deaf.Details);
        Assert.Equal("A shape dropped on the drawing can no longer become a device.", deaf.Line);
    }

    // ---- The row on the page ------------------------------------------------------------------------

    private static DeviceTypeDto Type() => new("automation", (short)DeviceTypes.Automation, "Automation",
        [new ShapePartDto("M 4 4 H 96 V 96 H 4 Z", "#334155", Filled: false, Rigid: false)],
        [new DevicePortDto("in", 4, 50, "in"), new DevicePortDto("out", 96, 50, "out")],
        new ShapePointDto(50, 50), new ShapePointDto(88, 60));

    private static PlantItemDto Item(Guid id, DeviceTypes type, double x) =>
        new(id, id.ToString()[..4], type, null, null, null, default, default, 0, null,
            0, 0, 0, 0, default!, x, 20, 130, 184, 0)
        {
            GeometryVersion = id,
        };

    private static DrawioWriteHandler HostWithTwoDevices()
    {
        var handler = new DrawioWriteHandler { Items = [Item(A, DeviceTypes.Automation, 10), Item(B, DeviceTypes.Automation, 500)], Types = [Type()] };
        foreach (var item in handler.Items)
            handler.Configs[item.ItemId] = new PlantItemConfigDto { ItemId = item.ItemId, Name = item.Name, DeviceType = (short)item.DeviceType };
        return handler;
    }

    /// <summary>Two drawn devices, and a third of type 99 joined to the first: the drawing cannot show it, nor the arrow.</summary>
    private static DrawioWriteHandler HostWithAGhost()
    {
        var handler = HostWithTwoDevices();
        handler.Items.Add(Item(Ghost, DeviceTypes.None, 900));
        handler.Configs[Ghost] = new PlantItemConfigDto { ItemId = Ghost, Name = "GHOST", DeviceType = (short)DeviceTypes.None };
        handler.Junctions.Add(new JunctionDto(J, A, Ghost, 99));
        return handler;
    }

    private IRenderedComponent<DrawioEditor> RenderPage(DrawioWriteHandler handler)
    {
        var state = new EditorSessionState(new Uri("http://localhost:5580"), handler);
        state.UseKey("test-key");
        Services.AddSingleton(state);
        return Render<DrawioEditor>();
    }

    private async Task<IRenderedComponent<DrawioEditor>> RenderSeededAsync(DrawioWriteHandler handler)
    {
        var cut = RenderPage(handler);
        await cut.InvokeAsync(() => cut.Instance.OnDrawioReady());
        return cut;
    }

    private static PlantGraph GraphOf(DrawioWriteHandler handler) =>
        new([.. handler.Items], [.. handler.Junctions], [.. handler.Types]);

    /// <summary>The line of the row with this id: in the row, and nowhere else.</summary>
    private static IElement LineOf(IRenderedComponent<DrawioEditor> cut, string id)
    {
        var line = cut.Find($"#{id}-notice");
        Assert.Equal("drawio-notices", line.ParentElement?.Id);
        return line;
    }

    [Fact]
    public void TheRowIsUnderTheBarAndAboveTheDrawing()
    {
        var cut = RenderPage(HostWithTwoDevices());

        var row = cut.Find("#drawio-notices");
        Assert.True(row.PreviousElementSibling?.ClassList.Contains("drawio-sections") == true,
            $"The row does not come right after the bar: it comes after <{row.PreviousElementSibling?.LocalName} class=\"{row.PreviousElementSibling?.ClassName}\">.");
        Assert.True(row.NextElementSibling?.ClassList.Contains("drawio-body") == true,
            $"The row is not right above the drawing: <{row.NextElementSibling?.LocalName} class=\"{row.NextElementSibling?.ClassName}\"> is.");
    }

    [Fact]
    public async Task AfterASaveTheRowSaysItsSummaryAndTheWholeReportIsStillThereById()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderSeededAsync(handler);
        Assert.Empty(cut.FindAll("#drawio-save-notice"));

        await cut.InvokeAsync(() => cut.Instance.OnDrawioSaved(DrawioDocument.Build(GraphOf(handler), null)));

        var line = LineOf(cut, "drawio-save");
        Assert.Equal(["drawio-notices-ok"], ToneClasses(line));
        Assert.Equal("Save succeeded: 1 write.", line.QuerySelector(".drawio-notices-text")!.TextContent);
        Assert.NotNull(line.QuerySelector("#drawio-save-dismiss"));

        // The report as it always was, whole, in the window behind "Details" - shut, and found by its ids all the same.
        var window = cut.Find("#drawio-save-details");
        Assert.True(window.HasAttribute("hidden"));
        Assert.Equal("Save report", window.GetAttribute("aria-label"));
        Assert.Equal("drawio-save-details", cut.Find("#drawio-save-report").Closest(".drawio-window")?.Id);
        Assert.Equal("Save succeeded: 1 write.", cut.Find("#drawio-save-summary").TextContent);

        cut.Find("#drawio-save-details-toggle").Click();
        Assert.False(cut.Find("#drawio-save-details").HasAttribute("hidden"));
    }

    [Fact]
    public async Task TheSavesXClosesItsLineAndTheNextSaveBringsItBack()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderSeededAsync(handler);
        var drawn = DrawioDocument.Build(GraphOf(handler), null);
        await cut.InvokeAsync(() => cut.Instance.OnDrawioSaved(drawn));

        cut.Find("#drawio-save-dismiss").Click();
        Assert.Empty(cut.FindAll("#drawio-save-notice"));

        // The next save in the same words brings the line back. (It empties its report while it asks the
        // host, and the row forgets the line then: this cannot tell a new round from a line that came back -
        // ASecondSaveRefusedInTheSameWordsIsSaidAgainAfterTheX is where the round alone brings it back.)
        await cut.InvokeAsync(() => cut.Instance.OnDrawioSaved(drawn));
        Assert.Equal("Save succeeded: 1 write.", LineOf(cut, "drawio-save").QuerySelector(".drawio-notices-text")!.TextContent);
    }

    /// <summary>
    /// Two saves refused at the gate, one after the other, end in the same words - and with no render
    /// between them in which the line would go away: the round alone tells the second from the first,
    /// and the second is said again after the X closed the first. (A save that gets as far as asking
    /// the host empties its report, and the row, while it asks: TheSavesXClosesItsLine... cannot tell.)
    /// </summary>
    [Fact]
    public async Task ASecondSaveRefusedInTheSameWordsIsSaidAgainAfterTheX()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderSeededAsync(handler);
        var drawn = DrawioDocument.Build(GraphOf(handler), null);
        var gate = Assert.IsType<DrawioPlantWriteGate>(typeof(DrawioEditor)
            .GetField("_writeGate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(cut.Instance));
        DrawioPlantWriteLease? creating = null;
        await cut.InvokeAsync(() => Assert.True(gate.TryEnter(DrawioPlantWriter.Creating, out creating)));
        try
        {
            await cut.InvokeAsync(() => cut.Instance.OnDrawioSaved(drawn));
            var first = LineOf(cut, "drawio-save").QuerySelector(".drawio-notices-text")!.TextContent;
            Assert.Equal(cut.Find("#drawio-save-error").TextContent, first);
            cut.Find("#drawio-save-dismiss").Click();
            Assert.Empty(cut.FindAll("#drawio-save-notice"));

            await cut.InvokeAsync(() => cut.Instance.OnDrawioSaved(drawn));
            Assert.Equal(first, LineOf(cut, "drawio-save").QuerySelector(".drawio-notices-text")!.TextContent);
        }
        finally
        {
            await cut.InvokeAsync(() => creating?.Dispose());
        }
    }

    /// <summary>
    /// The drawing out of reach is a red line that stays, with no X and nothing behind it - and with no
    /// "Loading the plant..." next to it while the plant is still being read: the plant would never be
    /// drawn. The plant's read is held, so that the loading line would be there but for the bridge.
    /// </summary>
    [Fact]
    public void TheDrawingOutOfReachIsARedLineThatStays()
    {
        var bridge = JSInterop.SetupModule(BridgeModule);
        bridge.SetupVoid("attach", _ => true).SetException(new JSDisconnectedException("circuit closing (test)"));
        var handler = HostWithTwoDevices();
        handler.ItemsGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var cut = RenderPage(handler);

        var line = LineOf(cut, "drawio-bridge");
        Assert.Equal(["drawio-notices-bad"], ToneClasses(line));
        var sentence = line.QuerySelector("p.drawio-bridge-error");
        Assert.NotNull(sentence);
        Assert.Equal("The bridge to draw.io loaded but did not answer: the plant cannot be shown. Reload the page.", sentence.TextContent);
        Assert.Null(line.QuerySelector("#drawio-bridge-dismiss"));
        Assert.Null(line.QuerySelector("#drawio-bridge-details-toggle"));
        // And no "Loading the plant..." next to it, though the plant is still being read.
        Assert.False(handler.ItemsGate.Task.IsCompleted, "The test is blind: the plant's read is not held.");
        Assert.Empty(cut.FindAll("p.editor-loading"));
        handler.ItemsGate.SetResult();
    }

    /// <summary>
    /// A live channel that cannot even be built is the one trouble of the live link that asks the
    /// operator to act - the page never tries again, only a reload does - so it is not only the bar's
    /// red "Live off", whose sentence shows only under the mouse or the keyboard's focus: it is a red
    /// line of the row that stays, the pill's sentence whole, its end included (spec, the bar, point 3:
    /// "in the row of outcomes if the operator must do something"). Before the passata grafica it was a
    /// line across the page; after task 2 it was the pill's tip alone.
    /// </summary>
    [Fact]
    public void ALiveChannelThatCannotBeOpenedIsARedLineThatStays()
    {
        // Since row 37 was extended to the whole page (10/10), what was thrown is behind the line's "?",
        // and in the pill's tip under the sentence, not in parentheses at its end.
        const string sentence = "Live status is not available on this page: the channel to the host could not be opened. " +
                                "The drawing and the save work as before; reload the page to try again.";
        var state = new EditorSessionState(new Uri("http://localhost:5580"), HostWithTwoDevices())
        {
            EventSourceForTesting = () => throw new InvalidOperationException("the channel refused its options (test)"),
        };
        state.UseKey("test-key");
        Services.AddSingleton(state);

        var cut = Render<DrawioEditor>();

        cut.WaitForAssertion(() => LineOf(cut, "drawio-live"));
        var line = LineOf(cut, "drawio-live");
        Assert.Equal(["drawio-notices-bad"], ToneClasses(line));
        Assert.Equal(sentence, line.QuerySelector(".drawio-notices-text")!.TextContent);
        Assert.Equal("InvalidOperationException: the channel refused its options (test)",
            DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-live-notice-text"));
        Assert.Null(line.QuerySelector("#drawio-live-dismiss"));
        Assert.Null(line.QuerySelector("#drawio-live-details-toggle"));
        // The pill keeps its word and the same sentence behind its tip, under the id it always had.
        var pill = cut.Find("#drawio-live-status");
        Assert.Equal("Live off", DrawioTopBarTest.WordOnThePill(pill));
        Assert.Equal(sentence, Assert.Single(pill.QuerySelectorAll("#drawio-live-status-sentence")).TextContent);
    }

    /// <summary>
    /// The decoration that could not be read is an orange line that stays, and the line is the whole
    /// warning - its last sentence, reload now before drawing, is the only one that says what to do,
    /// and a line cut before it (the first version of the row) left that behind "Details". The sentence
    /// keeps the id it had before the row; no "Details" is behind it. Why the read failed is in the
    /// sentence in the page's words, and the host's answer behind the "?" after it (row 37 of the queue,
    /// extended to the whole page on 10/10).
    /// </summary>
    [Fact]
    public async Task TheDecorationSaysTheWholeWarningAndWhatToDo()
    {
        var handler = HostWithTwoDevices();
        handler.BrokenReads.Add("decoration");
        var cut = await RenderSeededAsync(handler);

        var line = LineOf(cut, "drawio-decoration");
        Assert.Equal(["drawio-notices-warn"], ToneClasses(line));
        Assert.Null(line.QuerySelector("#drawio-decoration-dismiss"));
        Assert.Null(line.QuerySelector("#drawio-decoration-details-toggle"));
        var sentence = line.QuerySelector(".drawio-notices-text")!;
        Assert.Equal("drawio-decoration-banner", sentence.Id);
        Assert.Equal("Decoration: the plant's decoration could not be read because the host answered with an error: the drawing starts without it, and so as not to erase it, it will not be rewritten for this whole session. " +
                     "To try again, reload the page NOW - before you draw, while reloading costs nothing.", sentence.TextContent);
        Assert.Equal("the host answered 500 with an empty body", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-decoration-banner"));
    }

    [Fact]
    public async Task TheUndrawnDevicesAreAnOrangeLineThatStaysAndTheirListIsBehindDetails()
    {
        var cut = await RenderSeededAsync(HostWithAGhost());

        var line = LineOf(cut, "drawio-undrawn");
        Assert.Equal(["drawio-notices-warn"], ToneClasses(line));
        Assert.Null(line.QuerySelector("#drawio-undrawn-dismiss"));
        // The summary of before, under its id, without "- open for the names and the reason".
        Assert.Equal("1 device of the plant is not in the drawing, and 1 junction goes with them.",
            line.QuerySelector("#drawio-undrawn-summary")!.TextContent);

        Assert.Equal("Devices not in the drawing", cut.Find("#drawio-undrawn-details").GetAttribute("aria-label"));
        Assert.Equal("drawio-undrawn-details", cut.Find("#drawio-undrawn").Closest(".drawio-window")?.Id);
        Assert.Contains(Ghost.ToString(), Assert.Single(cut.FindAll("#drawio-undrawn li.drawio-undrawn-device")).TextContent);
        Assert.NotNull(cut.Find("#drawio-undrawn #drawio-undrawn-junctions"));
        // The paragraph that says what the list is about, behind the "?" of the window's title bar as in
        // every other window - word for word the one of 1b30ef0f, before the passata grafica.
        var help = cut.Find("#drawio-undrawn-details > .drawio-window-title #drawio-undrawn-details-help");
        Assert.Equal("drawio-undrawn-details-help-body", help.GetAttribute("aria-controls"));
        Assert.True(cut.Find("#drawio-undrawn-details-help-body").HasAttribute("hidden"));
        var scope = cut.Find("#drawio-undrawn-details-help-body > #drawio-undrawn-scope");
        Assert.Equal(UndrawnScope, System.Text.RegularExpressions.Regex.Replace(scope.TextContent, @"\s+", " ").Trim());
        Assert.Null(cut.Find("#drawio-undrawn").QuerySelector(".drawio-help-wrap"));
    }

    /// <summary>The paragraph about the undrawn devices as it was in 1b30ef0f, spaces made single.</summary>
    private const string UndrawnScope =
        "The drawing is built from the plant: a device with no size, or of a type the engine's registry does not declare, gets no shape - and the arrows that reach it go with it. Nothing has been lost: the save leaves these devices alone, it does not read their absence from the drawing as a deletion. There is no shape to click either, so a click on the drawing cannot open the column on the right on them. This list is worked out again from the plant as this page has it, so it follows the plant - and that means a device that becomes undrawn while you work (giving it the DeviceType \"None\" in the panel does it) shows up here at once, while its shape stays on the canvas until you open the page again.";

    [Fact]
    public async Task ARefusedCutIsARedLineWithAnXThatComesBackAtTheNextRefusal()
    {
        var cut = await RenderSeededAsync(HostWithTwoDevices());

        await cut.InvokeAsync(() => cut.Instance.OnDrawioCutRefused(["orphan-cell"]));

        var line = LineOf(cut, "drawio-cut");
        Assert.Equal(["drawio-notices-bad"], ToneClasses(line));
        var sentence = line.QuerySelector("p#drawio-cut-refused")!;
        Assert.Equal("status", sentence.GetAttribute("role"));
        Assert.StartsWith("A device shape cannot be cut:", sentence.TextContent);

        cut.Find("#drawio-cut-dismiss").Click();
        Assert.Empty(cut.FindAll("#drawio-cut-refused"));

        // The same selection, the same words, a second Ctrl+X: said again.
        await cut.InvokeAsync(() => cut.Instance.OnDrawioCutRefused(["orphan-cell"]));
        Assert.StartsWith("A device shape cannot be cut:", LineOf(cut, "drawio-cut").QuerySelector("#drawio-cut-refused")!.TextContent);
    }

    /// <summary>
    /// Queue item 15: a gesture brought more cells than one message of the circuit can describe, and the
    /// bridge sent their count instead - an orange line with its X, said again by the next such gesture
    /// even in the same words.
    /// </summary>
    [Fact]
    public async Task TooManyCellsAddedIsAnOrangeLineWithAnXThatComesBackAtTheNextGesture()
    {
        const string TheLine = "Too many cells to follow (14000): the cells just added are not known to this page, and none of them became a device.";
        var cut = await RenderSeededAsync(HostWithTwoDevices());
        Assert.Empty(cut.FindAll("#drawio-too-many-added-notice"));

        await cut.InvokeAsync(() => cut.Instance.OnDrawioTooManyCellsAdded(14000));

        var line = LineOf(cut, "drawio-too-many-added");
        Assert.Equal(["drawio-notices-warn"], ToneClasses(line));
        var sentence = line.QuerySelector("p#drawio-too-many-added-notice-text")!;
        Assert.Equal("status", sentence.GetAttribute("role"));
        Assert.Equal(TheLine, sentence.TextContent);

        cut.Find("#drawio-too-many-added-dismiss").Click();
        Assert.Empty(cut.FindAll("#drawio-too-many-added-notice"));

        await cut.InvokeAsync(() => cut.Instance.OnDrawioTooManyCellsAdded(14000));
        Assert.Equal(TheLine, LineOf(cut, "drawio-too-many-added").QuerySelector("p#drawio-too-many-added-notice-text")!.TextContent);
    }

    /// <summary>
    /// The last drop's line, with its report behind "Details"; its X closes it, and the next drop brings
    /// it back. (That drop takes the write gate, and the page renders the emptied report as it does: the
    /// row forgets the line, and the report's number is not what brings it back -
    /// ASecondDropRefusedInTheSameWords... is where it is.)
    /// </summary>
    [Fact]
    public async Task TheLastDropsReportIsALineAndTheNextDropBringsItBackAfterItsX()
    {
        var cut = await RenderSeededAsync(HostWithTwoDevices());
        // A device shape with no size: refused, and nothing is written.
        var sizeless = new DrawioAddedCell("drawio-1", "device", "0", DrawioDocument.DefaultLayerId, 100, 100, 0, 0);

        await cut.InvokeAsync(() => cut.Instance.OnDrawioCellsAdded([sizeless]));

        var line = LineOf(cut, "drawio-creation");
        Assert.Equal(["drawio-notices-bad"], ToneClasses(line));
        Assert.Equal("1 shape dropped on the drawing was NOT created.", line.QuerySelector(".drawio-notices-text")!.TextContent);
        Assert.Equal("drawio-creation-details", cut.Find("#drawio-creation-report").Closest(".drawio-window")?.Id);
        Assert.Single(cut.FindAll("#drawio-creation-report li.drawio-not-created"));

        cut.Find("#drawio-creation-dismiss").Click();
        Assert.Empty(cut.FindAll("#drawio-creation-notice"));

        await cut.InvokeAsync(() => cut.Instance.OnDrawioCellsAdded([sizeless]));
        Assert.Equal("1 shape dropped on the drawing was NOT created.",
            LineOf(cut, "drawio-creation").QuerySelector(".drawio-notices-text")!.TextContent);
    }

    /// <summary>A device shape as the shapes panel drops it: an Automation, with a box, on layer 1.</summary>
    private static DrawioAddedCell Shape(string cellId) =>
        new(cellId, "device", ((short)DeviceTypes.Automation).ToString(System.Globalization.CultureInfo.InvariantCulture),
            DrawioDocument.DefaultLayerId, 700, 40, 130, 184);

    /// <summary>
    /// Two drops refused at the gate, one after the other, end in the same words - and with no render
    /// between them in which the line would go away: the coordinator's report number alone tells the
    /// second from the first, and the second is said again after the X closed the first. The mirror of
    /// ASecondSaveRefusedInTheSameWordsIsSaidAgainAfterTheX.
    /// </summary>
    [Fact]
    public async Task ASecondDropRefusedInTheSameWordsIsSaidAgainAfterTheX()
    {
        var cut = await RenderSeededAsync(HostWithTwoDevices());
        var gate = Assert.IsType<DrawioPlantWriteGate>(typeof(DrawioEditor)
            .GetField("_writeGate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(cut.Instance));
        DrawioPlantWriteLease? saving = null;
        await cut.InvokeAsync(() => Assert.True(gate.TryEnter(DrawioPlantWriter.Drawing, out saving)));
        try
        {
            await cut.InvokeAsync(() => cut.Instance.OnDrawioCellsAdded([Shape("drawio-1")]));
            var first = LineOf(cut, "drawio-creation").QuerySelector(".drawio-notices-text")!.TextContent;
            Assert.Equal(cut.Find("#drawio-creation-error").TextContent, first);
            cut.Find("#drawio-creation-dismiss").Click();
            Assert.Empty(cut.FindAll("#drawio-creation-notice"));

            // The operator deletes the shape and drags another one, and the save is still running.
            await cut.InvokeAsync(() => cut.Instance.OnDrawioCellsAdded([Shape("drawio-2")]));
            Assert.Equal(first, LineOf(cut, "drawio-creation").QuerySelector(".drawio-notices-text")!.TextContent);
        }
        finally
        {
            await cut.InvokeAsync(() => saving?.Dispose());
        }
    }

    /// <summary>
    /// A drop drains its shapes one by one and says so after each: the same report, growing. The X
    /// pressed on the line of the first device closes what the report said until then - a shape whose
    /// request gets no answer after it comes back, on top of the line of the devices the drawing does
    /// not show (there before it), orange - NOT KNOWN, not "NOT created": dragged again, the shape could
    /// make a second device - with the whole report behind "Details" again.
    /// </summary>
    [Fact]
    public async Task ALineClosedWhileTheDropGoesOnComesBackWhenTheReportGrows()
    {
        var handler = HostWithAGhost();
        JSInterop.SetupModule(BridgeModule).Setup<bool>("renameCell", _ => true).SetResult(true);
        var cut = await RenderSeededAsync(handler);
        var firstPost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.ItemPostGate = firstPost;

        var dropping = cut.InvokeAsync(() => cut.Instance.OnDrawioCellsAdded([Shape("drawio-1"), Shape("drawio-2")]));
        await handler.ItemPostGateReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // The second shape's POST waits at a gate of its own, which fails once the X is pressed: a
        // request that went out and got no answer.
        var secondPost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.ItemPostGate = secondPost;
        firstPost.SetResult();

        cut.WaitForAssertion(() => Assert.Equal("Created 1 device from the shapes dropped on the drawing.",
            LineOf(cut, "drawio-creation").QuerySelector(".drawio-notices-text")!.TextContent), TimeSpan.FromSeconds(10));
        // Awaited: the drain still holds the dispatcher when the first line shows, and Click() would
        // return before the X is handled.
        await cut.Find("#drawio-creation-dismiss").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        Assert.Empty(cut.FindAll("#drawio-creation-notice"));
        Assert.Empty(cut.FindAll("#drawio-creation-report"));

        secondPost.SetException(new HttpRequestException("the connection dropped (test)"));
        await dropping.WaitAsync(TimeSpan.FromSeconds(10));

        var line = LineOf(cut, "drawio-creation");
        Assert.Equal(["drawio-notices-warn"], ToneClasses(line));
        Assert.Equal("Created 1 device from the shapes dropped on the drawing; for 1 shape it is NOT KNOWN whether its device was created - look before dragging it again.",
            line.QuerySelector(".drawio-notices-text")!.TextContent);
        Assert.Equal(["drawio-creation-notice", "drawio-undrawn-notice"], LinesShown(cut));
        Assert.Contains("NOT KNOWN", Assert.Single(cut.FindAll("#drawio-creation-report li.drawio-not-created")).TextContent);
    }

    /// <summary>The ids of the lines of the page's row on screen, top to bottom.</summary>
    private static string[] LinesShown(IRenderedComponent<DrawioEditor> cut) =>
        [.. cut.FindAll("#drawio-notices > .drawio-notices-line").Where(line => !line.HasAttribute("hidden")).Select(line => line.Id!)];

    [Fact]
    public async Task ABridgeThatCannotHearTheAdditionsIsARedLineThatStays()
    {
        var cut = await RenderSeededAsync(HostWithTwoDevices());

        await cut.InvokeAsync(() => cut.Instance.OnDrawioCreationUnavailable());

        var line = LineOf(cut, "drawio-creation-unavailable");
        Assert.Equal(["drawio-notices-bad"], ToneClasses(line));
        Assert.Null(line.QuerySelector("#drawio-creation-unavailable-dismiss"));
        Assert.StartsWith("A shape dropped on the drawing can no longer become a device", line.QuerySelector("#drawio-creation-unavailable")!.TextContent);
    }

    [Fact]
    public void TheHiddenCountersStayAboveTheBarOutOfTheRow()
    {
        var cut = RenderPage(HostWithTwoDevices());
        // The column, top to bottom, by id or first class: the counters are children of it, as the bar is.
        var column = cut.Find(".drawio-shell").Children.Select(child => child.Id is { Length: > 0 } id ? id : child.ClassList.FirstOrDefault() ?? child.LocalName).ToList();
        var bar = column.IndexOf("drawio-sections");
        Assert.True(bar >= 0, string.Join(", ", column));

        foreach (var id in new[] { "drawio-creation-state", "drawio-save-round", "drawio-reassociate-round" })
        {
            var counter = cut.Find($"#{id}");
            Assert.True(counter.HasAttribute("hidden") || counter.ClassList.Contains("visually-hidden"), $"#{id} is not hidden.");
            Assert.Null(counter.Closest("#drawio-notices"));
            Assert.True(column.IndexOf(id) is >= 0 and var at && at < bar, $"#{id} is not above the bar: {string.Join(", ", column)}.");
        }

        Assert.Equal("idle", cut.Find("#drawio-creation-state").TextContent.Trim());
    }

    [Fact]
    public async Task LoadingThePlantIsAQuietLineUnderTheBarUntilThePlantIsThere()
    {
        var handler = HostWithTwoDevices();
        handler.ItemsGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cut = RenderPage(handler);

        var loading = cut.Find("p.editor-loading");
        Assert.Equal("Loading the plant...", loading.TextContent);
        // Quiet: a child of the page's column, which is what the stylesheet gives the row's height and
        // the neutral grey to (.drawio-shell > .editor-loading), in none of the row's tones.
        Assert.True(loading.ParentElement?.ClassList.Contains("drawio-shell") == true, "The loading line is not a line of the page's column.");
        Assert.DoesNotContain(loading.ClassList, name => name.StartsWith("drawio-notices", StringComparison.Ordinal));
        Assert.True(loading.PreviousElementSibling?.ClassList.Contains("drawio-sections") == true,
            $"The loading line is not right under the bar: it comes after <{loading.PreviousElementSibling?.LocalName} class=\"{loading.PreviousElementSibling?.ClassName}\">.");
        Assert.Null(loading.Closest("#drawio-notices"));

        handler.ItemsGate.SetResult();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("p.editor-loading")));
    }
}
