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
/// The real write from the drawing (task 7b): <c>DrawioEditor.OnDrawioSaved</c> is the only place in
/// this stage that touches the plant's data.
/// </summary>
/// <remarks>
/// <para>
/// bUnit does not run JavaScript, does no hit-testing and does not simulate a drag: the test enters
/// through the <c>[JSInvokable]</c> method <c>OnDrawioSaved</c>, called directly on the instance
/// already rendered - the same pattern as <c>CanvasSizeTest</c> with <c>OnCanvasSizeChanged</c> and
/// <c>DrawioEditorPageTest</c> with <c>OnDrawioReady</c>. The "operator's" drawing is built with
/// <c>DrawioDocument.Build</c> on a graph that already carries the gesture: it is the real format,
/// not a hand-written XML that might not look like it.
/// </para>
/// <para>
/// What these tests measure is not only "the write went out": it is that <b>nothing is lost in
/// silence</b> (defect D203). That is why every test also looks at the report on screen - one line
/// for everything not written, with the name of which - and the failure one looks first of all that
/// the save STOPPED.
/// </para>
/// </remarks>
public sealed class DrawioEditorWriteTest : PlantCanvasBunitContext
{
    private static readonly Guid A = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid B = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid J = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static DeviceTypeDto Type() => new("automation", 0, "Automation", [new ShapePartDto("M 4 4 H 96 V 96 H 4 Z", "#334155", Filled: false, Rigid: false)],
        [new DevicePortDto("in", 4, 50, "in"), new DevicePortDto("out", 96, 50, "out")],
        new ShapePointDto(50, 50), new ShapePointDto(88, 60));

    private static PlantItemDto Item(Guid id, double x, double y) =>
        new(id, id.ToString()[..4], DeviceTypes.Automation, null, null, null, default, default, 0, null,
            0, 0, 0, 0, default!, x, y, 130, 184, 0)
        {
            GeometryVersion = id
        };

    private static PlantItemConfigDto Config(Guid id) => new() { ItemId = id, Name = id.ToString()[..4], DeviceType = 0 };

    /// <summary>A fake host with two devices and no junctions: the starting point of almost every test here.</summary>
    private static DrawioWriteHandler HostWithTwoDevices()
    {
        var handler = new DrawioWriteHandler
        {
            Items = [Item(A, 10, 20), Item(B, 500, 20)],
            Types = [Type()],
        };
        handler.Configs[A] = Config(A);
        handler.Configs[B] = Config(B);
        return handler;
    }

    private static PlantGraph GraphOf(DrawioWriteHandler handler) =>
        new([.. handler.Items], [.. handler.Junctions], [.. handler.Types]);

    /// <summary>
    /// Renders the page <b>and makes it receive 'init' from draw.io</b>, that is, makes it really
    /// seed the drawing: it is the precondition of every save, not a detail. A drawing that never
    /// received the plant comes back without any of our cells, and taking it at face value would
    /// erase the whole plant - see
    /// <see cref="ASaveFromADrawingThatWasNeverSeededWritesNothingAtAll"/>.
    /// </summary>
    private async Task<IRenderedComponent<DrawioEditor>> RenderWithAsync(DrawioWriteHandler handler)
    {
        var cut = RenderWithoutSeeding(handler);
        await cut.InvokeAsync(() => cut.Instance.OnDrawioReady());
        return cut;
    }

    /// <summary>The render alone, without draw.io's init: the drawing never received the plant.</summary>
    private IRenderedComponent<DrawioEditor> RenderWithoutSeeding(DrawioWriteHandler handler)
    {
        var state = new EditorSessionState(new Uri("http://localhost:5580"), handler);
        state.UseKey("test-key");
        Services.AddSingleton(state);
        return Render<DrawioEditor>();
    }

    private static async Task SaveAsync(IRenderedComponent<DrawioEditor> cut, string xml) =>
        await cut.InvokeAsync(() => cut.Instance.OnDrawioSaved(xml));

    private static string[] LinesOf(IRenderedComponent<DrawioEditor> cut, string cssClass) =>
        [.. cut.FindAll($"li.{cssClass}").Select(node => node.TextContent)];

    /// <summary>
    /// <b>The page counts the save rounds, and two rounds that end the same way are still two.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// MEDIO 2 of the second review, at the level where the counter lives. The browser suite used
    /// to wait for the save report's TEXT to change, and the reviewer measured the cost: the new
    /// head test was red in 2 runs out of 7, and when it fell the second save's report was
    /// identical to the first's word for word - so every assertion after it was reading the first
    /// save's outcome. The first line below is the premise the whole thing rests on, and it is
    /// ASSERTED rather than assumed: two saves really can produce the same report.
    /// </para>
    /// <para>
    /// <c>#drawio-save-round</c> is outside the report's <c>@if</c> so that it is always there:
    /// a watcher that finds the element missing and then present has learned nothing about
    /// whether a save finished, and that is the one mechanism that produces exactly the failure
    /// the reviewer saw.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TwoSavesThatEndTheSameWayAreCountedAsTwo()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);
        var drawn = DrawioDocument.Build(GraphOf(handler), null);

        await SaveAsync(cut, drawn);
        var firstRound = cut.Find("#drawio-save-round").TextContent;
        var firstReport = cut.Find("#drawio-save-report").TextContent;

        await SaveAsync(cut, drawn);

        // La premessa, misurata e non data per scontata: i due verbali sono uguali. Se un giorno
        // smettessero di esserlo questa prova smetterebbe di provare qualcosa, e questa riga lo
        // dice invece di lasciarla verde.
        Assert.Equal(firstReport, cut.Find("#drawio-save-report").TextContent);
        Assert.NotEqual(firstRound, cut.Find("#drawio-save-round").TextContent);
    }

    /// <summary>
    /// Il giro RIFIUTATO conta lo stesso: il salvataggio non ha scritto niente perche' stava
    /// scrivendo qualcun altro, ma il clic ha avuto risposta, e chi aspetta deve poterlo sapere
    /// invece di restare fermo su un giro che non arrivera' mai.
    /// </summary>
    [Fact]
    public async Task ASaveRefusedBecauseSomebodyElseIsWritingStillCountsAsARound()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);
        var before = cut.Find("#drawio-save-round").TextContent;

        // Il primo salvataggio si ferma DENTRO, sulla prima scrittura del piano: e' lo stesso
        // fermo di APanelWriteDuringADrawingSaveIsRefusedInsteadOfBeingLost, non un'attesa a tempo.
        handler.GeometryGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var moved = DrawioDocument.Build(new PlantGraph([Item(A, 99, 77), Item(B, 500, 20)], [], [Type()]), null);
        var saving = cut.InvokeAsync(() => cut.Instance.OnDrawioSaved(moved));
        await handler.GeometryGateReached.Task;

        // E il secondo arriva mentre il primo e' li': la pagina lo rifiuta senza scrivere niente.
        await SaveAsync(cut, moved);
        var afterTheRefusal = cut.Find("#drawio-save-round").TextContent;
        Assert.Contains("already being saved", cut.Find("#drawio-save-error").TextContent, StringComparison.Ordinal);

        handler.GeometryGate.SetResult();
        await saving;

        // Il giro rifiutato ha contato: chi aspettava il proprio clic ha avuto risposta invece di
        // restare fermo su un giro che quel clic non produrra' mai.
        Assert.NotEqual(before, afterTheRefusal);
        // E quello vero ha contato a sua volta, dopo.
        Assert.NotEqual(afterTheRefusal, cut.Find("#drawio-save-round").TextContent);
    }

    /// <summary>
    /// The move goes through the command that already exists (<c>SetGeometriesCommand</c>), that is,
    /// through the geometry route: the device reaches the host where the operator left it, and the
    /// other one - that nobody touched - is not rewritten.
    /// </summary>
    [Fact]
    public async Task AMovedDeviceIsWrittenThroughTheGeometryCommand()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);

        // The operator moves A and leaves B where it was.
        var drawn = new PlantGraph([Item(A, 99, 77), Item(B, 500, 20)], [], [Type()]);
        await SaveAsync(cut, DrawioDocument.Build(drawn, null));

        var written = Assert.Single(handler.GeometryWrites);
        Assert.Equal(A, written.ItemId);
        Assert.Equal(99, written.X!.Value, precision: 2);
        Assert.Equal(77, written.Y!.Value, precision: 2);
        Assert.Empty(cut.FindAll("li.drawio-refusal"));
        Assert.Contains("Save succeeded", cut.Find("#drawio-save-summary").TextContent);
    }

    /// <summary>
    /// The deletion goes through <c>DeleteItemCommand</c>: a single DELETE, and before it the read
    /// of the configuration that makes the undo possible.
    /// </summary>
    [Fact]
    public async Task ADeletedDeviceIsWrittenThroughTheDeleteCommand()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);

        // B is no longer in the drawing that comes back.
        var drawn = new PlantGraph([Item(A, 10, 20)], [], [Type()]);
        await SaveAsync(cut, DrawioDocument.Build(drawn, null));

        Assert.Equal(B, Assert.Single(handler.ItemDeletes));
        Assert.Empty(handler.GeometryWrites);
        Assert.Empty(cut.FindAll("li.drawio-refusal"));
    }

    /// <summary>The deleted junction goes through <c>DeleteJunctionCommand</c>, the new one through <c>AddJunctionCommand</c>.</summary>
    [Fact]
    public async Task JunctionsGoThroughTheirOwnCommands()
    {
        var handler = HostWithTwoDevices();
        handler.Junctions = [new JunctionDto(J, A, B, 99)];
        var cut = await RenderWithAsync(handler);

        // The junction disappears from the drawing: it is a deletion.
        var drawn = new PlantGraph([Item(A, 10, 20), Item(B, 500, 20)], [], [Type()]);
        await SaveAsync(cut, DrawioDocument.Build(drawn, null));

        Assert.Equal(J, Assert.Single(handler.JunctionDeletes));
        Assert.Empty(handler.JunctionPosts);
    }

    /// <summary>
    /// Row 20: the weight changed by hand on an arrow the plant already knows reaches the plant, and
    /// by the ONE route that changes it - <c>PUT /plant/junctions/{id}</c>, through
    /// <c>SetJunctionPriorityCommand</c>, which is undoable. A second <c>POST</c> on the same pair
    /// would come back "created" and leave the priority exactly where it was (see the remarks of
    /// <c>AddJunctionCommand</c>): the report would say written and nothing would have changed.
    /// <para>
    /// And it is <b>named in the report</b>. A write nobody can read about is half of the same
    /// silence: whoever saves has to be able to see that the weight moved, on which arrow, and which
    /// way - so the line carries both numbers, not just the new one.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheWeightChangedOnAKnownArrowIsWrittenThroughThePriorityCommandAndNamed()
    {
        var handler = HostWithTwoDevices();
        handler.Junctions = [new JunctionDto(J, A, B, 99)];
        var cut = await RenderWithAsync(handler);

        // The drawing comes back with the arrow's own attribute changed by hand - draw.io's "Edit
        // Data" is where the operator does it - and nothing else touched.
        var xml = DrawioDocument.Build(GraphOf(handler), null)
            .Replace("wcsPriority=\"bassa\"", "wcsPriority=\"alta\"");
        Assert.Contains("wcsPriority=\"alta\"", xml);

        await SaveAsync(cut, xml);

        var (junctionId, priority) = Assert.Single(handler.JunctionPriorityWrites);
        Assert.Equal(J, junctionId);
        Assert.Equal(0, priority);
        // The route, not just the effect: a POST would have been reported as a success and changed
        // nothing on the plant.
        Assert.Empty(handler.JunctionPosts);
        Assert.Empty(handler.JunctionDeletes);

        var written = Assert.Single(LinesOf(cut, "drawio-written"), line => line.Contains("priority"));
        Assert.Contains(J.ToString(), written);
        Assert.Contains("from low (99)", written);
        Assert.Contains("to high (0)", written);
    }

    /// <summary>
    /// The junction the gesture was aimed at is no longer on the host - somebody else deleted it
    /// while the drawing was going on. Nothing is written, and that is right; what must not happen
    /// is the save closing with "nothing to write" while the operator did move that weight. The same
    /// answer a deletion with nothing left to delete already gives, for the same reason.
    /// </summary>
    [Fact]
    public async Task AWeightChangedOnAJunctionGoneFromTheHostIsNotWrittenAndItIsSaid()
    {
        var handler = HostWithTwoDevices();
        handler.Junctions = [new JunctionDto(J, A, B, 99)];
        var cut = await RenderWithAsync(handler);

        var xml = DrawioDocument.Build(GraphOf(handler), null)
            .Replace("wcsPriority=\"bassa\"", "wcsPriority=\"alta\"");

        // Gone from the host between the opening and the save: the page re-reads the plant before
        // planning, so the junction is not in the graph the plan is built against.
        handler.Junctions.Clear();

        await SaveAsync(cut, xml);

        Assert.Empty(handler.JunctionPriorityWrites);
        var notice = Assert.Single(LinesOf(cut, "drawio-notice"), line => line.Contains("priority"));
        Assert.Contains(J.ToString(), notice);
    }

    /// <summary>
    /// <b>Without the permit no write goes out</b>, and it is said before trying: the drawing is not
    /// a shortcut for stepping over the edit permit the SVG canvas respects. The test looks first of
    /// all that the host received NOTHING - a correct message after a write that already went out
    /// would be the worst of both worlds.
    /// </summary>
    [Fact]
    public async Task WithoutTheEditPermitNothingIsWrittenAndItIsSaid()
    {
        var handler = HostWithTwoDevices();
        handler.GrantEditLock = false;
        var cut = await RenderWithAsync(handler);

        var drawn = new PlantGraph([Item(A, 99, 77), Item(B, 500, 20)], [], [Type()]);
        await SaveAsync(cut, DrawioDocument.Build(drawn, null));

        Assert.Empty(handler.Writes);
        var error = cut.Find("#drawio-save-error").TextContent;
        Assert.Contains("Read only", error);
        Assert.Contains("14:32:10", error);   // since when, as Editor.razor's banner does
    }

    /// <summary>
    /// The other half of the same guard: the engine running. Two different causes of read only, two
    /// different sentences - whoever reads must know they have to stop the engine, not go looking
    /// for who holds the permit.
    /// </summary>
    [Fact]
    public async Task WithTheEngineRunningNothingIsWrittenAndItIsSaid()
    {
        var handler = HostWithTwoDevices();
        handler.NavStatus = NavigatorStatusTypes.Run;
        var cut = await RenderWithAsync(handler);

        var drawn = new PlantGraph([Item(A, 99, 77), Item(B, 500, 20)], [], [Type()]);
        await SaveAsync(cut, DrawioDocument.Build(drawn, null));

        Assert.Empty(handler.Writes);
        Assert.Contains("the engine is running", cut.Find("#drawio-save-error").TextContent);
    }

    /// <summary>
    /// <b>Every refusal is visible, and named</b>: two refusals make two distinct lines, not one line
    /// that counts them. It is the point of the whole task - whoever drew must know WHICH device was
    /// not written and why, not that "some changes" did not go through.
    /// </summary>
    [Fact]
    public async Task EveryRefusalGetsItsOwnNamedLine()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);

        // Two arrows that hook no port: two refusals, one per arrow.
        var xml = DrawioDocument.Build(GraphOf(handler), null).Replace("</root>",
            $"""<object id="freccia-1" label="" wcs="junction"><mxCell style="endArrow=block;" edge="1" parent="1" source="{A}" target="{B}"><mxGeometry relative="1" as="geometry"/></mxCell></object>"""
            + $"""<object id="freccia-2" label="" wcs="junction"><mxCell style="endArrow=block;" edge="1" parent="1" source="{B}" target="{A}"><mxGeometry relative="1" as="geometry"/></mxCell></object></root>""");

        await SaveAsync(cut, xml);

        var refusals = LinesOf(cut, "drawio-refusal");
        Assert.Equal(2, refusals.Length);
        Assert.Contains(refusals, line => line.StartsWith("freccia-1:"));
        Assert.Contains(refusals, line => line.StartsWith("freccia-2:"));
        Assert.Empty(handler.JunctionPosts);
    }

    /// <summary>The operator's decoration is saved along with the rest: it is the free drawing no other piece of the plant keeps.</summary>
    [Fact]
    public async Task TheDecorationIsSavedWithTheRest()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);

        var xml = DrawioDocument.Build(GraphOf(handler), null).Replace("</root>",
            """<mxCell id="nota-1" value="Zona di carico" style="text;html=1;" vertex="1" parent="1"><mxGeometry x="900" y="900" width="320" height="40" as="geometry"/></mxCell></root>""");

        await SaveAsync(cut, xml);

        Assert.Contains("Zona di carico", Assert.Single(handler.DecorationWrites));
    }

    /// <summary>
    /// The opposite direction, and the defect there would be without it: if the decoration could not
    /// be READ at opening, the drawing does not carry it, and rewriting it would erase it. It is not
    /// rewritten, and the reason is said - the rest of the save goes through all the same.
    /// </summary>
    [Fact]
    public async Task ADecorationThatCouldNotBeReadIsNotRewrittenOverTheStoredOne()
    {
        var handler = HostWithTwoDevices();
        handler.Decoration = "<mxCell id=\"nota-vecchia\" value=\"Zona A\"/>";
        handler.BrokenReads.Add("decoration");
        var cut = await RenderWithAsync(handler);

        var drawn = new PlantGraph([Item(A, 99, 77), Item(B, 500, 20)], [], [Type()]);
        await SaveAsync(cut, DrawioDocument.Build(drawn, null));

        Assert.Empty(handler.DecorationWrites);
        Assert.Equal("<mxCell id=\"nota-vecchia\" value=\"Zona A\"/>", handler.Decoration);
        Assert.Contains(LinesOf(cut, "drawio-refusal"), line => line.Contains("decoration"));
        // The move, which has nothing to do with the decoration, went through all the same.
        Assert.Single(handler.GeometryWrites);
    }

    /// <summary>
    /// The opening warning announces what will really happen. It used to announce a retry at save
    /// time, and that retry was an erasure: a warning that promises more than the code does is how
    /// whoever draws stops being on guard. Now it says the decoration will not be rewritten for the
    /// whole session, and it points at the only remedy that loses nothing - reloading right away,
    /// before there is anything on the canvas to lose.
    /// <para>
    /// <b>This test looks at the TEXT only</b>, and on its own it defends nothing: put the retry back
    /// into the source and it stays green while the warning goes back to lying. The half that
    /// measures the behaviour - that at save time there really is no retry - is
    /// <see cref="ADecorationThatCouldNotBeReadAtOpeningIsNotRewrittenForTheRestOfTheSession"/>, and
    /// the two are to be kept together.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheOpeningNoticeSaysTheDecorationWillNotBeRewrittenForTheWholeSession()
    {
        var handler = HostWithTwoDevices();
        handler.BrokenReads.Add("decoration");

        var cut = await RenderWithAsync(handler);

        var notice = cut.Find("#drawio-decoration-banner").TextContent;
        Assert.Contains("for this whole session", notice);
        // No retry promised, because at save time there will be none. The stem "retr" and not the
        // whole word: it has to catch "retry", "retries", "retried" and "retrying" alike. The
        // warning's own way out - reloading the page - deliberately says "try again", which is a
        // different gesture and a different word.
        Assert.DoesNotContain("retr", notice);
    }

    /// <summary>
    /// The decoration declaration holds for the whole SESSION, so it must stay on screen even after
    /// a save that stops before the plan. The measured case: decoration unreadable at opening, the
    /// operator draws, the engine starts in the meantime, and the save stops on the permit - before
    /// reaching the decoration's refusal, which is inside the plan.
    /// <para>
    /// While the declaration lived in <c>_notices</c>, the report cleared it at the top of every save
    /// and nobody put it back there: what was left on screen was the engine line alone, and the only
    /// warning about an irreversible condition disappeared at the very moment whoever reads decides
    /// whether to try again later.
    /// </para>
    /// <para>
    /// The test does not look at a selector but at the whole PAGE, before and after: that way it
    /// measures the declaration disappearing, not the name of the box it lives in.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheDecorationDeclarationSurvivesASaveThatStopsBeforeThePlan()
    {
        var handler = HostWithTwoDevices();
        handler.BrokenReads.Add("decoration");
        handler.NavStatus = NavigatorStatusTypes.Run;
        var cut = await RenderWithAsync(handler);

        Assert.Contains("for this whole session", cut.Markup);

        await SaveAsync(cut, DrawioDocument.Build(GraphOf(handler), null));

        // The save stopped before the plan: nothing written, and the reason on screen.
        Assert.Empty(handler.Writes);
        Assert.Contains("the engine is running", cut.Find("#drawio-save-error").TextContent);

        // And the condition that lasts the whole session is still declared.
        Assert.Contains("for this whole session", cut.Markup);
    }

    /// <summary>
    /// The window in which the decoration has <b>not yet</b> arrived and has not failed: the drawing
    /// can go out inside it (the plant is already loaded, and draw.io can say 'init' at any moment),
    /// and a save landing there finds the same condition as a failed read - the decoration is not
    /// rewritten - but <b>not</b> the reason, which nobody has measured yet. The refusal used to say
    /// "had not been read at opening ()": an empty pair of brackets claiming a failure that never
    /// happened. Where the reason is missing something else is said, instead of pretending to have
    /// one.
    /// </summary>
    [Fact]
    public async Task ASaveInsideTheDecorationReadWindowDoesNotClaimAnUnmeasuredFailure()
    {
        var handler = HostWithTwoDevices();
        handler.Decoration = "<mxCell id=\"nota-vecchia\" value=\"Zona A\"/>";
        handler.DecorationGate = new TaskCompletionSource();

        // The render stops INSIDE the decoration read; the iframe is already there, so draw.io can
        // seed and the operator can save right here.
        var cut = RenderWithoutSeeding(handler);
        await cut.InvokeAsync(() => cut.Instance.OnDrawioReady());

        await SaveAsync(cut, DrawioDocument.Build(GraphOf(handler), null));

        // The decoration is not rewritten: the plant's one is still where it was.
        Assert.Empty(handler.DecorationWrites);
        var refusal = Assert.Single(LinesOf(cut, "drawio-refusal"), line => line.Contains("decoration"));
        Assert.DoesNotContain("()", refusal);
        Assert.Contains("had not arrived", refusal);

        handler.DecorationGate.SetResult();
    }

    /// <summary>
    /// The read that failed at opening is <b>not retried</b> at save time: the decoration is not
    /// rewritten for the whole rest of the session.
    /// <para>
    /// The retry looked like a remedy and was an erasure. The host answering again does not put the
    /// decoration back on the CANVAS: the drawing had gone out without it, and the document never
    /// comes back after a save, so what the canvas brings back is this session's free drawing alone.
    /// Writing it means writing it OVER what the plant was holding, and "Zona A" disappears with
    /// nobody saying so.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ADecorationThatCouldNotBeReadAtOpeningIsNotRewrittenForTheRestOfTheSession()
    {
        var handler = HostWithTwoDevices();
        handler.Decoration = "<mxCell id=\"nota-vecchia\" value=\"Zona A\"/>";
        handler.BrokenReads.Add("decoration");
        var cut = await RenderWithAsync(handler);

        // The host starts answering again between the opening and the save: it is the case in which
        // the retry fired, and it is the case in which it erased.
        handler.BrokenReads.Remove("decoration");

        var xml = DrawioDocument.Build(GraphOf(handler), null).Replace("</root>",
            """<mxCell id="nota-1" value="Zona di carico" style="text;html=1;" vertex="1" parent="1"><mxGeometry x="900" y="900" width="320" height="40" as="geometry"/></mxCell></root>""");
        await SaveAsync(cut, xml);

        // No write of the decoration, and the plant's note is still where it was.
        Assert.Empty(handler.DecorationWrites);
        Assert.Equal("<mxCell id=\"nota-vecchia\" value=\"Zona A\"/>", handler.Decoration);

        // And the refusal says it, and names it: this session's free drawing is not on the plant.
        var refusal = Assert.Single(LinesOf(cut, "drawio-refusal"), line => line.Contains("decoration"));
        Assert.Contains("press Ctrl+A", refusal);

        // Why the read failed, in the page's words, and the host's answer behind the line's "?" - not
        // in parentheses in the middle of it (row 37 of the queue, extended to the whole page on 10/10).
        var sentence = Assert.Single(cut.FindAll("li.drawio-refusal > span"), span => span.TextContent.StartsWith("decoration: "));
        Assert.StartsWith("decoration: the plant's decoration had not been read at opening because the host answered with an error, so the drawing does not carry it: ",
            sentence.TextContent);
        Assert.NotNull(sentence.Id);
        Assert.Equal("the host answered 500 with an empty body", DrawioFailureLineAssert.BehindTheQuestionMark(cut, sentence.Id));
    }

    /// <summary>
    /// The refusal's message does not advise a <b>destructive</b> gesture. "Reload the page and save
    /// again" was exactly that: the notes drawn in this session are on no plant, and a reload loses
    /// them forever. The message says instead what is lost and how to carry the drawing away.
    /// </summary>
    [Fact]
    public async Task WhenTheDecorationCouldNotBeReadTheMessageDoesNotAdviseReloading()
    {
        var handler = HostWithTwoDevices();
        handler.BrokenReads.Add("decoration");
        var cut = await RenderWithAsync(handler);

        await SaveAsync(cut, DrawioDocument.Build(GraphOf(handler), null));

        var refusal = Assert.Single(LinesOf(cut, "drawio-refusal"), line => line.Contains("decoration"));
        // What is lost, and how to carry it away before losing it.
        Assert.Contains("is not on the plant", refusal);
        Assert.Contains("press Ctrl+A", refusal);
        // The destructive gesture is not advised. It guards "save again" and not the whole sentence
        // the Italian version used to carry: translating that sentence word for word would have left
        // a guard over a wording the product has never said in English - green forever, and no red
        // would ever report it. The message does name reloading, but as a DANGER ("before closing or
        // reloading the page"), so a guard on "reload" alone could not tell the two apart.
        Assert.DoesNotContain("save again", refusal);
    }

    /// <summary>
    /// Point a of the task, seen from where it counts: the junction that really reaches the host. A
    /// new arrow without our attribute is born at 99 (low), and the choice shows up in the report -
    /// it is not a refusal, it is a notice, but it is not a silence.
    /// </summary>
    [Fact]
    public async Task ANewArrowWithoutPriorityIsCreatedAtNinetyNineAndTheChoiceIsDeclared()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);

        var xml = DrawioDocument.Build(GraphOf(handler), null).Replace("</root>",
            $"""<object id="freccia-1" label="" wcs="junction"><mxCell style="exitX=0.96;exitY=0.5;entryX=0.04;entryY=0.5;" edge="1" parent="1" source="{A}" target="{B}"><mxGeometry relative="1" as="geometry"/></mxCell></object></root>""");

        await SaveAsync(cut, xml);

        var created = Assert.Single(handler.JunctionPosts);
        Assert.Equal(A, created.SourceItemId);
        Assert.Equal(B, created.DestinationItemId);
        Assert.Equal(99, created.Priority);
        // The notice is written by DrawioDelta, not by the page, and since review finding 4 its
        // lines are in English like everything else on the screen: the operator no longer reads
        // half a report in one language and half in the other.
        Assert.Contains(LinesOf(cut, "drawio-notice"), line => line.Contains("low priority"));
    }

    /// <summary>
    /// And the counter-intuitive direction: "alta" is worth ZERO, the lowest number - <c>RouteGraph</c>
    /// explores by increasing priority. The attribute value stays Italian because it is data that
    /// travels in the drawing (<c>DrawioDocument.HighPriorityValue</c>), not text on screen.
    /// </summary>
    [Fact]
    public async Task AnArrowThatDeclaresHighPriorityIsCreatedAtZero()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);

        var xml = DrawioDocument.Build(GraphOf(handler), null).Replace("</root>",
            $"""<object id="freccia-1" label="" wcs="junction" wcsPriority="alta"><mxCell style="exitX=0.96;exitY=0.5;entryX=0.04;entryY=0.5;" edge="1" parent="1" source="{A}" target="{B}"><mxGeometry relative="1" as="geometry"/></mxCell></object></root>""");

        await SaveAsync(cut, xml);

        Assert.Equal(0, Assert.Single(handler.JunctionPosts).Priority);
    }

    /// <summary>
    /// The worst case, and the reason the report exists: a write fails halfway through the round.
    /// The save <b>stops</b> - it does not carry on with the rest, which would write against a plant
    /// in a state that is no longer the computed one - and declares what went through and what did
    /// not, line by line.
    /// </summary>
    [Fact]
    public async Task AFailedWriteStopsTheSaveAndDeclaresWhatWentThroughAndWhatDidNot()
    {
        var handler = HostWithTwoDevices();
        handler.FailRoute = "junction-post";   // the first step (the geometry) goes through, this one does not
        var cut = await RenderWithAsync(handler);

        var drawn = new PlantGraph([Item(A, 99, 77), Item(B, 500, 20)], [], [Type()]);
        var xml = DrawioDocument.Build(drawn, null).Replace("</root>",
            $"""<object id="freccia-1" label="" wcs="junction"><mxCell style="exitX=0.96;exitY=0.5;entryX=0.04;entryY=0.5;" edge="1" parent="1" source="{A}" target="{B}"><mxGeometry relative="1" as="geometry"/></mxCell></object></root>""");

        await SaveAsync(cut, xml);

        // The move went through; the junction did not, and the decoration - which came after - did
        // not either: the round stopped, it did not carry on skipping the failed step.
        Assert.Single(handler.GeometryWrites);
        Assert.Empty(handler.DecorationWrites);
        Assert.Contains("Save interrupted", cut.Find("#drawio-save-error").TextContent);
        Assert.Contains(LinesOf(cut, "drawio-written"), line => line.Contains("the move of"));
        var notWritten = LinesOf(cut, "drawio-not-written");
        Assert.Contains(notWritten, line => line.Contains("the new junction"));
        Assert.Contains(notWritten, line => line.Contains("decoration"));
    }

    [Fact]
    public async Task AGeometryConflictStopsLaterWritesAndAsksForReloadInsteadOfCallingTheEngineRunning()
    {
        var handler = HostWithTwoDevices();
        var original = handler.Items.Single(item => item.ItemId == A);
        handler.GeometryConflictItemIds = [A];
        var cut = await RenderWithAsync(handler);

        var moved = new PlantGraph([Item(A, 99, 77), Item(B, 500, 20)], [], [Type()]);
        var xml = DrawioDocument.Build(moved, null).Replace("</root>",
            $"""<object id="freccia-1" label="" wcs="junction"><mxCell style="exitX=0.96;exitY=0.5;entryX=0.04;entryY=0.5;" edge="1" parent="1" source="{A}" target="{B}"><mxGeometry relative="1" as="geometry"/></mxCell></object></root>""");

        await SaveAsync(cut, xml);

        Assert.Empty(handler.GeometryWrites);
        Assert.Empty(handler.JunctionPosts);
        Assert.Empty(handler.DecorationWrites);

        var error = cut.Find("#drawio-save-error").TextContent;
        Assert.Contains("geometry changed", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reload", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no automatic retry", error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("engine is in Run", error, StringComparison.OrdinalIgnoreCase);

        var pageGraph = (PlantGraph)typeof(DrawioEditor)
            .GetField("_graph", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(cut.Instance)!;
        var unchanged = pageGraph.Items.Single(item => item.ItemId == A);
        Assert.Equal(original.X, unchanged.X);
        Assert.Equal(original.GeometryVersion, unchanged.GeometryVersion);

        var notWritten = LinesOf(cut, "drawio-not-written");
        Assert.Contains(notWritten, line => line.Contains("the move of"));
        Assert.Contains(notWritten, line => line.Contains("the new junction"));
        Assert.Contains(notWritten, line => line.Contains("decoration"));
        Assert.Empty(LinesOf(cut, "drawio-written"));
    }

    /// <summary>
    /// The <b>second</b> message of this page that can make somebody lose work, and until this test
    /// existed it had no guard at all: the save stopped halfway, and the plant cannot be read again
    /// either. The page has no state to compute a further save from, so it says the page has to be
    /// reloaded - and in the same breath says what a reload throws away and how to carry it off
    /// first.
    /// </summary>
    /// <remarks>
    /// Review finding 2 of the batch that repaired the hand-drawn arrow. The other message of this
    /// pair - the decoration's refusal - is guarded twice
    /// (<see cref="ADecorationThatCouldNotBeReadAtOpeningIsNotRewrittenForTheRestOfTheSession"/>
    /// and <see cref="WhenTheDecorationCouldNotBeReadTheMessageDoesNotAdviseReloading"/>), and the
    /// report of that batch claimed both were protected. They were not: putting
    /// "Extras &gt; Edit Diagram" back HERE alone left the whole suite green, which is exactly how
    /// a remedy that points nowhere had already reached the operator four times on this branch.
    /// The guard is on the road that was MEASURED as a gesture, and on the danger being named.
    /// </remarks>
    [Fact]
    public async Task WhenThePlantCannotBeReadAfterAnInterruptedSaveTheWayOutIsOneThatWorks()
    {
        var handler = HostWithTwoDevices();
        // The geometry goes through, the junction does not: the plant is left halfway, which is
        // the only state this message is written for.
        handler.FailRoute = "junction-post";
        var cut = await RenderWithAsync(handler);

        // And the plant goes unreadable from the first write on - not from the start, or the save
        // would have stopped before the plan and never reached this moment.
        handler.BrokenReads.Add("items-after-the-first-write");

        var drawn = new PlantGraph([Item(A, 99, 77), Item(B, 500, 20)], [], [Type()]);
        var xml = DrawioDocument.Build(drawn, null).Replace("</root>",
            $"""<object id="freccia-1" label="" wcs="junction"><mxCell style="exitX=0.96;exitY=0.5;entryX=0.04;entryY=0.5;" edge="1" parent="1" source="{A}" target="{B}"><mxGeometry relative="1" as="geometry"/></mxCell></object></root>""");
        await SaveAsync(cut, xml);

        Assert.Contains("Save interrupted", cut.Find("#drawio-save-error").TextContent);
        var notice = Assert.Single(LinesOf(cut, "drawio-notice"), line => line.StartsWith("plant:"));

        // What is lost, and the way out that was measured in a real browser.
        Assert.Contains("the page has to be reloaded", notice);
        Assert.Contains("press Ctrl+A", notice);
        // And the menu that this embedded draw.io does not have is not named again: ui=min leaves
        // no menu bar, and the word "Extras" is not in its document at all
        // (DrawioCarryingTheDrawingAwayTest).
        Assert.DoesNotContain("Extras", notice);
    }

    /// <summary>
    /// The ORDER of the writes, not only their number. A save that writes the right things in the
    /// wrong order - the new junction before the deletions - would create it against a plant about
    /// to change, and the device's deletion would carry it away an instant later with nobody saying
    /// so. New junctions go last, after the deletions, so they are born against the plant as it will
    /// really be; the decoration closes.
    /// </summary>
    [Fact]
    public async Task TheWritesGoOutInTheDeclaredOrder()
    {
        var c = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var handler = HostWithTwoDevices();
        handler.Items.Add(Item(c, 900, 20));
        handler.Configs[c] = Config(c);
        handler.Junctions = [new JunctionDto(J, A, B, 99)];
        var cut = await RenderWithAsync(handler);

        // The gesture: A moved, C deleted, junction J deleted and redrawn between the same two
        // devices (new identifier: to the delta it is a new arrow).
        var drawn = new PlantGraph([Item(A, 99, 77), Item(B, 500, 20)], [], [Type()]);
        var xml = DrawioDocument.Build(drawn, null).Replace("</root>",
            $"""<object id="freccia-1" label="" wcs="junction"><mxCell style="exitX=0.96;exitY=0.5;entryX=0.04;entryY=0.5;" edge="1" parent="1" source="{A}" target="{B}"><mxGeometry relative="1" as="geometry"/></mxCell></object></root>""");

        await SaveAsync(cut, xml);

        Assert.Equal(["geometry", "junction-delete", "item-delete", "junction-post", "decoration"], handler.Writes);
    }

    /// <summary>
    /// A new device is not born from the drawing (decision 3.1: a copy is a device still to be
    /// configured) - but it does not disappear silently: it is named, with the cell it came from.
    /// </summary>
    [Fact]
    public async Task ACopiedDeviceIsNotCreatedButItIsNamed()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);

        var xml = DrawioDocument.Build(GraphOf(handler), null).Replace("</root>",
            """<object id="copia-1" label="copia" wcs="device"><mxCell style="shape=image;" vertex="1" parent="1"><mxGeometry x="700" y="700" width="130" height="184" as="geometry"/></mxCell></object></root>""");

        await SaveAsync(cut, xml);

        Assert.Contains(LinesOf(cut, "drawio-refusal"), line => line.StartsWith("copia-1:"));
        // The test's name says "not created": that is measured, not deduced from the refusal. The
        // only write that must have reached the host is the decoration, which has nothing to do with
        // the copy - no configuration, no geometry, no new device.
        Assert.Equal(["decoration"], handler.Writes);
    }

    // ------------------------------------------------------------------------------------------
    // One page, and a second one that cannot pass in silence.
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Wraps a drawing the way draw.io really hands it back. <b>Measured</b> in a real browser on
    /// 2026-09-17: with a single page what comes out of the save is
    /// <c>&lt;mxfile host="127.0.0.1"&gt;&lt;diagram id="..." name="Page-1"&gt;</c> around the
    /// model - <b>not</b> the naked <c>&lt;mxGraphModel&gt;</c> that <c>DrawioDocument.Build</c>
    /// writes on the way out, and that every other test in this class hands to the save.
    /// </summary>
    private static string AsDrawioReturnsIt(params string[] pages) =>
        $"""<mxfile host="127.0.0.1"{(pages.Length > 1 ? $" pages=\"{pages.Length}\"" : string.Empty)}>"""
        + string.Concat(pages.Select((page, index) => $"""<diagram id="pag-{index + 1}" name="Page-{index + 1}">{page}</diagram>"""))
        + "</mxfile>";

    /// <summary>The empty canvas of a page nobody has drawn on yet.</summary>
    private const string BlankPage = """<mxGraphModel><root><mxCell id="0"/><mxCell id="1" parent="0"/></root></mxGraphModel>""";

    /// <summary>
    /// The shape that really arrives, with one page: it saves exactly as it did before. It is the
    /// net under the whole batch - the counting of the pages must not cost the normal round - and it
    /// is also the first test here to hand the save the <b>measured</b> shape instead of the one we
    /// write ourselves on the way out.
    /// </summary>
    [Fact]
    public async Task TheOnePageShapeDrawioReallyHandsBackIsSavedExactlyAsBefore()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);

        var moved = new PlantGraph([Item(A, 99, 77), Item(B, 500, 20)], [], [Type()]);
        await SaveAsync(cut, AsDrawioReturnsIt(DrawioDocument.Build(moved, null)));

        var written = Assert.Single(handler.GeometryWrites);
        Assert.Equal(A, written.ItemId);
        Assert.Equal(99, written.X!.Value, precision: 2);
        Assert.Equal(77, written.Y!.Value, precision: 2);
        Assert.Contains("Save succeeded", cut.Find("#drawio-save-summary").TextContent);
        Assert.Empty(cut.FindAll("#drawio-save-error"));
        Assert.Empty(cut.FindAll("li.drawio-refusal"));
    }

    /// <summary>
    /// <b>Two pages stop the save whole.</b> The first page carries a real gesture - a device moved
    /// - so without this refusal the save would write, and the test would be measuring nothing.
    /// <para>
    /// What is measured is the WRITES, not the message: a refusal that shows a line and writes
    /// anyway is the same loss wearing an apology. The second page carries a note of the operator's,
    /// which is what would have been thrown away in silence - the reader takes the first
    /// <c>&lt;root&gt;</c> and the decoration is rewritten from it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ADrawingThatComesBackWithTwoPagesWritesNothingAtAllAndSaysWhy()
    {
        var handler = HostWithTwoDevices();
        handler.Decoration = "<mxCell id=\"nota-vecchia\" value=\"Zona A\"/>";
        var cut = await RenderWithAsync(handler);

        var moved = new PlantGraph([Item(A, 99, 77), Item(B, 500, 20)], [], [Type()]);
        const string SecondPage = """<mxGraphModel><root><mxCell id="0"/><mxCell id="1" parent="0"/><mxCell id="nota-2" value="Drawn on page two" style="text;" vertex="1" parent="1"><mxGeometry x="10" y="10" width="80" height="20" as="geometry"/></mxCell></root></mxGraphModel>""";

        await SaveAsync(cut, AsDrawioReturnsIt(DrawioDocument.Build(moved, null), SecondPage));

        // Not one write towards the host, of any kind - the decoration included, which is the one
        // that would have carried page two's note away for good.
        Assert.Empty(handler.Writes);
        Assert.Empty(handler.GeometryWrites);
        Assert.Empty(handler.DecorationWrites);
        Assert.Equal("<mxCell id=\"nota-vecchia\" value=\"Zona A\"/>", handler.Decoration);

        var error = cut.Find("#drawio-save-error").TextContent;
        Assert.Contains("2 pages", error);
        Assert.Contains("Nothing at all was written", error);
    }

    /// <summary>
    /// The refusal of the two pages comes <b>before</b> the blanket refusal about our cells, and
    /// this is the case that tells them apart: the plant is drawn on page one and the operator
    /// worked on page two, so the drawing that comes back does carry our cells - but if the pages
    /// were counted after, a plant whose devices had all been dragged onto the second page would be
    /// refused with the wrong words ("it contains none of the plant cells... delete them one by one
    /// from /editor"), which is neither what happened nor advice that helps.
    /// </summary>
    [Fact]
    public async Task TwoPagesAreNamedForWhatTheyAreAndNotMistakenForAnEmptiedPlant()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);

        // Page one empty, everything of ours on page two: the case that would otherwise read as
        // "the drawing contains none of the plant cells".
        await SaveAsync(cut, AsDrawioReturnsIt(BlankPage, DrawioDocument.Build(GraphOf(handler), null)));

        Assert.Empty(handler.Writes);
        var error = cut.Find("#drawio-save-error").TextContent;
        Assert.Contains("2 pages", error);
        Assert.DoesNotContain("delete the devices one by one", error);
    }

    // ------------------------------------------------------------------------------------------
    // The drawing that never received the plant (the review's critical finding).
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>The case that erases everything.</b> If draw.io says 'init' before the plant has arrived -
    /// at opening there are five HTTP reads with a 30 second timeout each, the race is real - the
    /// canvas stays empty and looks like an empty plant. A save from there sends back a model with
    /// none of our cells: the delta would propose deleting EVERY device and EVERY junction, and the
    /// empty decoration would be rewritten over the stored one. All declared line by line in the
    /// report, and all irreversible.
    /// <para>
    /// <b>Nothing</b> is written: not one refusal per device (which would already be an extremely
    /// long report nobody would go searching through), a blanket refusal.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ASaveFromADrawingThatWasNeverSeededWritesNothingAtAll()
    {
        var handler = HostWithTwoDevices();
        handler.Junctions = [new JunctionDto(J, A, B, 99)];
        handler.Decoration = "<mxCell id=\"nota-vecchia\" value=\"Zona A\"/>";
        // No 'init': the document never went out towards draw.io.
        var cut = RenderWithoutSeeding(handler);

        // What draw.io returns when it never received anything: its own empty canvas.
        await SaveAsync(cut, """<mxGraphModel><root><mxCell id="0"/><mxCell id="1" parent="0"/></root></mxGraphModel>""");

        Assert.Empty(handler.Writes);
        Assert.Empty(handler.ItemDeletes);
        Assert.Empty(handler.JunctionDeletes);
        Assert.Empty(handler.DecorationWrites);
        Assert.Equal("<mxCell id=\"nota-vecchia\" value=\"Zona A\"/>", handler.Decoration);
        Assert.Contains("Nothing was written", cut.Find("#drawio-save-error").TextContent);
    }

    /// <summary>
    /// The second half of the same net, and it holds even when the document <b>had</b> gone out: a
    /// drawing that comes back without any of our cells, against a plant that had some, is not a
    /// plant emptied by the operator - it is a drawing that does not contain the plant. No point in
    /// the code, before this correction, compared how many of our cells the drawing brought back
    /// with how many it contained.
    /// </summary>
    [Fact]
    public async Task ADrawingThatComesBackWithoutAnyOfOurCellsIsRefusedInBlock()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);

        // The document went out (init received), but what comes back carries no cell of ours: only
        // a note of the operator's.
        await SaveAsync(cut, """<mxGraphModel><root><mxCell id="0"/><mxCell id="1" parent="0"/><mxCell id="nota-1" value="Zona" style="text;" vertex="1" parent="1"><mxGeometry x="10" y="10" width="80" height="20" as="geometry"/></mxCell></root></mxGraphModel>""");

        Assert.Empty(handler.Writes);
        Assert.Contains("Nothing was written", cut.Find("#drawio-save-error").TextContent);
    }

    /// <summary>
    /// The "zero marked cells" net just tested above also makes the LEGITIMATE gesture of deleting
    /// every device from the drawing impossible - on a small plant doing it in one go is realistic -
    /// and for that case "reload the page" (the only advice before) is the WRONG one: reloading does
    /// not help whoever really wanted to empty the plant. The message cannot tell the two cases
    /// apart, so it has to name both remedies instead of giving only one that for the other case
    /// would be false.
    /// <para>
    /// The second remedy CHANGED with the batch that hid <c>/editor</c>, and this test changed with
    /// it instead of being deleted. It used to assert that the message sent the operator to
    /// <c>/editor</c>; that address no longer opens the old canvas, so the advice had become one
    /// nobody could follow - the exact defect a review already charged this branch with. What the
    /// message must name now is what this page can really do, and the assertion is on that: the
    /// refusal fires only when NOT ONE marked cell comes back, so the way through is to delete in
    /// more than one go. Keeping the old assertion would have left a test defending a sentence that
    /// does not exist; deleting it would have left the message unguarded.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ADrawingThatComesBackEmptyNamesAWayThroughThatWorksNotOnlyReloading()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);

        await SaveAsync(cut, """<mxGraphModel><root><mxCell id="0"/><mxCell id="1" parent="0"/></root></mxGraphModel>""");

        var error = cut.Find("#drawio-save-error").TextContent;
        Assert.Contains("Nothing was written", error);
        // The remedy for the failure (already tested above) stays, but it is no longer the only one:
        // whoever really wanted to delete everything must read how to do it for real.
        Assert.Contains("reload the page", error);
        Assert.Contains("at least one", error);
        // And it must not send him to the old canvas any more: that address redirects to this very
        // page, so the advice would go in a circle.
        Assert.DoesNotContain("/editor", error);
    }

    /// <summary>
    /// The other half of the remedy: the document declares itself "seeded" <b>only</b> after the call
    /// to draw.io has succeeded. A <c>load</c> that throws (iframe already gone, circuit closing)
    /// used to leave a raised field and an empty drawing: what is measured here is that the next save
    /// writes nothing, and that the page says so instead of keeping quiet.
    /// </summary>
    [Fact]
    public async Task ADocumentThatCouldNotBeLoadedIntoDrawioDoesNotCountAsSeeded()
    {
        var module = JSInterop.SetupModule("./_content/Wcs.WebEditor/drawio-bridge.js");
        module.SetupVoid("attach", _ => true).SetVoidResult();
        module.SetupVoid("load", _ => true).SetException(new Microsoft.JSInterop.JSException("iframe gone (test)"));

        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);

        await SaveAsync(cut, """<mxGraphModel><root><mxCell id="0"/><mxCell id="1" parent="0"/></root></mxGraphModel>""");

        Assert.Empty(handler.Writes);
        Assert.Contains("Nothing was written", cut.Find("#drawio-save-error").TextContent);
        // And the bridge's failure does not stay mute: it is defect D200 applied to the outward leg.
        Assert.NotEmpty(cut.FindAll("p.drawio-bridge-error"));
    }

    /// <summary>
    /// <c>_documentSent</c> was never lowered again. Here the FIRST seeding really succeeds (it is
    /// the one <see cref="RenderWithAsync"/> does), then the iframe reloads and sends a second
    /// 'init' - the real case, not a theoretical one: draw.io does it again every time its own
    /// content reloads. The RESEEDING that follows that second 'init' fails (circuit closing, iframe
    /// gone: the same failure as the test above, but here on a seeding that follows one that already
    /// succeeded). Without the remedy the field stayed raised from the first seeding, and the first
    /// of the two questions of the blanket refusal (see <c>WriteToPlantAsync</c>) no longer fired: a
    /// save from there would have written against the empty canvas draw.io really holds, not against
    /// the drawing the page believes it sent.
    /// </summary>
    [Fact]
    public async Task AFailedReseedAfterAnIframeReloadIsNotTreatedAsStillSeeded()
    {
        var module = JSInterop.SetupModule("./_content/Wcs.WebEditor/drawio-bridge.js");
        module.SetupVoid("attach", _ => true).SetVoidResult();
        var load = module.SetupVoid("load", _ => true);
        load.SetVoidResult();

        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);   // the first seeding succeeds, with 'load' configured above

        // The second 'init': the reseeding that follows it fails.
        load.SetException(new Microsoft.JSInterop.JSException("iframe reloaded (test)"));
        await cut.InvokeAsync(() => cut.Instance.OnDrawioReady());

        var drawn = new PlantGraph([Item(A, 99, 77), Item(B, 500, 20)], [], [Type()]);
        await SaveAsync(cut, DrawioDocument.Build(drawn, null));

        Assert.Empty(handler.Writes);
        Assert.Contains("never received the plant", cut.Find("#drawio-save-error").TextContent);
    }

    /// <summary>
    /// The real race, in the direction the defect describes: draw.io says 'init' <b>before</b> the
    /// plant has arrived. The document must go out all the same, as soon as the graph is there -
    /// which of the two conditions arrives last does not matter.
    /// <para>
    /// The gate on the devices GET keeps the plant read in flight (the same pattern as
    /// <c>GeometryCountingHandler</c>'s <c>ResponseGate</c>): it opens only once the click's
    /// <c>Task</c> - held, not let go - is ready to be awaited.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheDocumentGoesOutAlsoWhenTheGraphArrivesAfterDrawioSaidItWasReady()
    {
        var handler = HostWithTwoDevices();
        handler.ItemsGate = new TaskCompletionSource();

        // Without a key the page shows <Key/>: the plant is loaded from the click, not from the
        // render, and that way the moment when the read is in flight is in the test's hands.
        var state = new EditorSessionState(new Uri("http://localhost:5580"), handler);
        Services.AddSingleton(state);
        var cut = Render<DrawioEditor>();

        await cut.Find("input[type=password]").InputAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "test-key" });
        var accepting = cut.Find("button").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        // The plant read is suspended; the iframe, on the other hand, is already rendered and
        // attached, and it is exactly from here that draw.io sends its own 'init'.
        await cut.InvokeAsync(() => cut.Instance.OnDrawioReady());
        Assert.DoesNotContain(JSInterop.Invocations, i => i.Identifier == "load");

        handler.ItemsGate.SetResult();
        await accepting;

        var call = Assert.Single(JSInterop.Invocations, i => i.Identifier == "load");
        Assert.Contains(A.ToString(), Assert.IsType<string>(call.Arguments[1]));
    }

    // ------------------------------------------------------------------------------------------
    // The report that must not lie.
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// A write that <b>times out</b> is not a write that was not made: the client's timeout is 30
    /// seconds, and a deletion the host performs in 31 has gone through on the plant while whoever
    /// reads the report finds it among the "NOT written". A third outcome is needed - <b>not
    /// confirmed</b> - because "not written" is a claim, and on a cancellation it cannot be made.
    /// </summary>
    [Fact]
    public async Task AWriteThatTimedOutIsDeclaredUnconfirmedNotUnwritten()
    {
        var handler = HostWithTwoDevices();
        handler.TimeoutRoute = "item-delete";
        var cut = await RenderWithAsync(handler);

        // A moved (goes through) and B deleted (times out with no answer).
        var drawn = new PlantGraph([Item(A, 99, 77)], [], [Type()]);
        await SaveAsync(cut, DrawioDocument.Build(drawn, null));

        Assert.Contains(LinesOf(cut, "drawio-written"), line => line.Contains("the move of"));
        var unconfirmed = Assert.Single(LinesOf(cut, "drawio-unconfirmed"));
        Assert.Contains("the deletion of device", unconfirmed);
        // The decoration, which came after, is the one that really did not go out: "NOT written" is
        // said only of what was not even attempted.
        Assert.Contains(LinesOf(cut, "drawio-not-written"), line => line.Contains("decoration"));
        Assert.DoesNotContain(LinesOf(cut, "drawio-not-written"), line => line.Contains("the deletion of device"));
    }

    /// <summary>
    /// "Not confirmed" used to cover cancellation only
    /// (<see cref="OperationCanceledException"/>). A connection that breaks AFTER the request went
    /// out (reset, or a middlebox closing instead of answering with a 502 or a 504) is the same
    /// uncertainty - the transport failed, it is not that the host said no - and
    /// <see cref="HttpClient"/> turns it into an <see cref="HttpRequestException"/>, not into a
    /// cancellation exception. Before the correction it still ended up among the "NOT written": the
    /// same unmeasured claim already rejected for cancellation, by a narrower path.
    /// </summary>
    [Fact]
    public async Task AConnectionResetAfterTheRequestStartedIsDeclaredUnconfirmedNotUnwritten()
    {
        var handler = HostWithTwoDevices();
        handler.HttpFailureRoute = "item-delete";
        var cut = await RenderWithAsync(handler);

        // A moved (goes through) and B deleted (the connection breaks after the request went out).
        var drawn = new PlantGraph([Item(A, 99, 77)], [], [Type()]);
        await SaveAsync(cut, DrawioDocument.Build(drawn, null));

        Assert.Contains(LinesOf(cut, "drawio-written"), line => line.Contains("the move of"));
        var unconfirmed = Assert.Single(LinesOf(cut, "drawio-unconfirmed"));
        Assert.Contains("the deletion of device", unconfirmed);
        // The decoration, which came after, is the one that really did not go out.
        Assert.Contains(LinesOf(cut, "drawio-not-written"), line => line.Contains("decoration"));
        Assert.DoesNotContain(LinesOf(cut, "drawio-not-written"), line => line.Contains("the deletion of device"));
    }

    /// <summary>
    /// The other direction, and the limit of the uncertainty: an <see cref="HttpRequestException"/>
    /// that says it never managed to <b>establish</b> the connection (host down, connection refused,
    /// a name that does not resolve) is a step that never went out - "the host may have executed it"
    /// would be an invented uncertainty, and whoever reads the report would go and check on the
    /// plant for a write that does not exist. On net10
    /// <c>HttpRequestException.HttpRequestError</c> tells the two cases apart, and the report keeps
    /// them apart.
    /// </summary>
    [Fact]
    public async Task AConnectionThatWasNeverEstablishedIsDeclaredUnwrittenNotUnconfirmed()
    {
        var handler = HostWithTwoDevices();
        handler.HttpFailureRoute = "item-delete";
        handler.HttpFailureKind = HttpRequestError.ConnectionError;
        var cut = await RenderWithAsync(handler);

        var drawn = new PlantGraph([Item(A, 99, 77)], [], [Type()]);
        await SaveAsync(cut, DrawioDocument.Build(drawn, null));

        Assert.Empty(LinesOf(cut, "drawio-unconfirmed"));
        Assert.Contains(LinesOf(cut, "drawio-not-written"), line => line.Contains("the deletion of device"));
        Assert.DoesNotContain("NOT KNOWN", cut.Find("#drawio-save-error").TextContent);
    }

    /// <summary>
    /// The report is built from what the host <b>answered</b>, not from what was asked for. The host
    /// collapses onto the (source, destination) pair and returns the existing junction <b>without
    /// touching its priority</b>: an arrow drawn "alta" on a pair somebody else had already
    /// connected in the meantime is written as 99, and saying "written, high priority (0)" would be
    /// a false account - worse than no account, because whoever reads it stops going to look.
    /// </summary>
    [Fact]
    public async Task AJunctionTheHostCollapsedOntoAnExistingPairIsReportedAsTheHostAnsweredIt()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);

        // While the drawing was going on, somebody else created the A -> B junction at low priority:
        // the opened drawing does not carry it, the duplicate check looks only at the opened one,
        // and the POST goes out.
        handler.Junctions.Add(new JunctionDto(J, A, B, 99));

        var xml = DrawioDocument.Build(GraphOf(HostWithTwoDevices()), null).Replace("</root>",
            $"""<object id="freccia-1" label="" wcs="junction" wcsPriority="alta"><mxCell style="exitX=0.96;exitY=0.5;entryX=0.04;entryY=0.5;" edge="1" parent="1" source="{A}" target="{B}"><mxGeometry relative="1" as="geometry"/></mxCell></object></root>""");

        await SaveAsync(cut, xml);

        var written = Assert.Single(LinesOf(cut, "drawio-written"), line => line.Contains("junction"));
        Assert.Contains("99", written);
        Assert.DoesNotContain("high priority (0)", written);
        Assert.Contains(J.ToString(), written);
    }

    /// <summary>
    /// The same collapse, seen from the round's answer (row 46, fix round 1): the junction is on the
    /// plant but the priority the drawing asks for is NOT, so the round has not written everything.
    /// It answers false - the "Leave site?" mark stays on - the report says the priority was not
    /// written and why, and the NEXT save writes it: the drawn arrow is now tied to that junction.
    /// </summary>
    [Fact]
    public async Task APriorityTheHostDidNotWriteLeavesTheRoundUnfinishedAndTheNextSaveWritesIt()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);
        handler.Junctions.Add(new JunctionDto(J, A, B, 99));

        var xml = DrawioDocument.Build(GraphOf(HostWithTwoDevices()), null).Replace("</root>",
            $"""<object id="freccia-1" label="" wcs="junction" wcsPriority="alta"><mxCell style="exitX=0.96;exitY=0.5;entryX=0.04;entryY=0.5;" edge="1" parent="1" source="{A}" target="{B}"><mxGeometry relative="1" as="geometry"/></mxCell></object></root>""");

        Assert.False(await cut.InvokeAsync(() => cut.Instance.OnDrawioSaved(xml)));

        // What the host answered is still told as written: the junction is there.
        var written = Assert.Single(LinesOf(cut, "drawio-written"), line => line.Contains("junction"));
        Assert.Contains(J.ToString(), written);
        Assert.Contains("low priority (99)", written);
        // And what did not reach the plant is told as NOT written, with the reason.
        var missing = Assert.Single(LinesOf(cut, "drawio-not-written"));
        Assert.Contains(J.ToString(), missing);
        Assert.Contains("high priority (0)", missing);
        Assert.Contains("already existed", missing);
        Assert.Empty(handler.JunctionPriorityWrites);

        Assert.True(await cut.InvokeAsync(() => cut.Instance.OnDrawioSaved(xml)));
        Assert.Equal((J, 0), Assert.Single(handler.JunctionPriorityWrites));
        Assert.Empty(LinesOf(cut, "drawio-not-written"));
    }

    /// <summary>
    /// The SAME account, on a priority that is neither of the two we write: the host collapsed onto
    /// a pair whose junction sits at 50. The report has to call that HIGH, because that is what the
    /// engine does with it (<c>RouteGraph</c> explores it before every 99) and it is what the
    /// drawing itself now says - the arrow is drawn thick and red. The page had a third copy of the
    /// rule, <c>priority == HighPriority</c>, which agreed with the one true rule
    /// (<c>JunctionWeights.FromPriority</c>) only on 0 and on 99: it would have told the operator
    /// "low priority (50)" about an arrow the canvas beside it draws high, and about a junction the
    /// engine takes first.
    /// </summary>
    [Fact]
    public async Task ThePriorityInTheReportIsNamedByTheOneRuleNotByAThirdCopyOfIt()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);

        // Somebody else connected A -> B in the meantime, at an in-between priority: the POST goes
        // out all the same and the host answers with THIS junction, untouched.
        handler.Junctions.Add(new JunctionDto(J, A, B, 50));

        var xml = DrawioDocument.Build(GraphOf(HostWithTwoDevices()), null).Replace("</root>",
            $"""<object id="freccia-1" label="" wcs="junction" wcsPriority="alta"><mxCell style="exitX=0.96;exitY=0.5;entryX=0.04;entryY=0.5;" edge="1" parent="1" source="{A}" target="{B}"><mxGeometry relative="1" as="geometry"/></mxCell></object></root>""");

        // 50 IS high, which is what the arrow asks for: by the one rule the drawn priority is on
        // the plant, so the round wrote everything (fix round 2 of row 46: a comparison of NUMBERS
        // called it "NOT written", kept the round unfinished, and the next save wrote nothing).
        Assert.True(await cut.InvokeAsync(() => cut.Instance.OnDrawioSaved(xml)));

        var written = Assert.Single(LinesOf(cut, "drawio-written"), line => line.Contains("junction"));
        Assert.Contains("high priority (50)", written);
        Assert.DoesNotContain("low priority (50)", written);
        Assert.Empty(LinesOf(cut, "drawio-not-written"));
    }

    /// <summary>
    /// This comment used to say that two slow steps are enough to fall out of the lease BETWEEN TWO
    /// WRITES, but that was never true - <c>EditPermissionFilter</c> renews the expiry on EVERY
    /// authorized write, before it begins. The real window sits between the permit check and the
    /// FIRST write: the configuration reads <c>PlanAsync</c> does for every deleted device renew
    /// nothing, and with many deletions they can on their own exceed the lease's 60 seconds. The
    /// permit is renewed <b>during</b> the plan all the same - it closes the same window at one
    /// fiftieth of the calls compared with a single renewal before the loop, and it is the cadence
    /// this test can measure without a fake clock - and if it is no longer ours the save stops there,
    /// with the report saying so instead of a write crashing into the 423.
    /// </summary>
    [Fact]
    public async Task ThePermitIsRenewedDuringThePlanAndItsLossStopsTheSave()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);

        // The permit stays ours for the save's request and for the first renewal, then it passes to
        // somebody else: the second step of the plan must not go out.
        handler.RevokeEditLockAfter = 2;

        var drawn = new PlantGraph([Item(A, 99, 77), Item(B, 500, 20)], [], [Type()]);
        await SaveAsync(cut, DrawioDocument.Build(drawn, null));

        Assert.True(handler.EditLockRequests > 1, "the permit was not renewed during the plan");
        Assert.Single(handler.GeometryWrites);
        Assert.Empty(handler.DecorationWrites);
        Assert.Contains("permit", cut.Find("#drawio-save-error").TextContent);
        Assert.Contains(LinesOf(cut, "drawio-not-written"), line => line.Contains("decoration"));
    }

    /// <summary>
    /// After a successful save draw.io receives no document. The canvas is where the work not yet
    /// written lives - a note just drawn, a cell the save refused, a selection, an undo still
    /// possible - and rewriting it whole throws that away: it is the very gesture this page exists
    /// not to make. The identity of the junctions just created, which was the reason for sending it
    /// back, is resolved in the delta with the session's map (see
    /// <see cref="AJunctionCreatedAtTheFirstSaveIsRecognizedAtTheSecond"/>).
    /// </summary>
    [Fact]
    public async Task AfterASuccessfulSaveNoDocumentGoesBackToDrawio()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);

        var xml = DrawioDocument.Build(GraphOf(handler), null).Replace("</root>",
            $"""<object id="freccia-1" label="" wcs="junction"><mxCell style="exitX=0.96;exitY=0.5;entryX=0.04;entryY=0.5;" edge="1" parent="1" source="{A}" target="{B}"><mxGeometry relative="1" as="geometry"/></mxCell></object></root>""");

        await SaveAsync(cut, xml);

        Assert.Single(handler.Junctions);
        Assert.Contains("Save succeeded", cut.Find("#drawio-save-summary").TextContent);
        // One only: the seeding at opening. After the save, nothing.
        var load = Assert.Single(JSInterop.Invocations, i => i.Identifier == "load");
        Assert.DoesNotContain("freccia-1", Assert.IsType<string>(load.Arguments[1]));
    }

    /// <summary>
    /// The defect that sending the document back meant to close, closed where it really sits: in the
    /// delta. The junction created at the first save carries on the canvas the identifier draw.io
    /// gave it, while the plant holds the host's one; at the second save it must be
    /// <b>recognized</b> - not deleted and recreated. Every round of deletion plus creation is a
    /// window in which a failure halfway leaves it deleted and not recreated.
    /// </summary>
    [Fact]
    public async Task AJunctionCreatedAtTheFirstSaveIsRecognizedAtTheSecond()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);

        // The operator's canvas: the plant seeded at opening, plus the arrow they have just drawn.
        // It does not change between the two saves - whoever touches nothing else and saves again
        // sends exactly the same XML, freccia-1 included.
        var xml = DrawioDocument.Build(GraphOf(handler), null).Replace("</root>",
            $"""<object id="freccia-1" label="" wcs="junction"><mxCell style="exitX=0.96;exitY=0.5;entryX=0.04;entryY=0.5;" edge="1" parent="1" source="{A}" target="{B}"><mxGeometry relative="1" as="geometry"/></mxCell></object></root>""");

        await SaveAsync(cut, xml);
        var created = Assert.Single(handler.Junctions);

        await SaveAsync(cut, xml);

        // Same junction, same identifier: no deletion, no second POST.
        Assert.Equal(created.JunctionId, Assert.Single(handler.Junctions).JunctionId);
        Assert.Empty(handler.JunctionDeletes);
        Assert.Single(handler.JunctionPosts);
        Assert.Empty(cut.FindAll("li.drawio-refusal"));
        Assert.Contains("Save succeeded", cut.Find("#drawio-save-summary").TextContent);
    }

    /// <summary>
    /// The same thing with the anchors draw.io <b>really</b> produces, and not with those
    /// <c>DrawioDocument</c> writes.
    /// <para>
    /// draw.io offers as connection points only the fractions {0, .25, .5, .75, 1}
    /// (<c>mxRectangleShape.prototype.constraints</c>, which <c>mxImageShape</c> - our devices'
    /// shape - inherits): the registry's ports, here 0.96 and 0.04, are not among them, and an arrow
    /// drawn by hand will never hook onto them. The port check compared the arrow born in the
    /// session with the registry's ones all the same, and at every save - identical canvas, no
    /// gesture - accused the operator of having moved it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AJunctionCreatedInThisSessionIsNotAccusedOfMovingPortsAtEveryLaterSave()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);

        // The anchors are the ones draw.io writes when the operator attaches the arrow to the right
        // side of A and the left side of B: exitX=1 and entryX=0, not the ports 0.96/0.04.
        var xml = DrawioDocument.Build(GraphOf(handler), null).Replace("</root>",
            $"""<object id="freccia-1" label="" wcs="junction"><mxCell style="exitX=1;exitY=0.5;entryX=0;entryY=0.5;" edge="1" parent="1" source="{A}" target="{B}"><mxGeometry relative="1" as="geometry"/></mxCell></object></root>""");

        await SaveAsync(cut, xml);
        var created = Assert.Single(handler.Junctions);
        Assert.Empty(cut.FindAll("li.drawio-refusal"));

        // Second save: same canvas, no gesture in between.
        await SaveAsync(cut, xml);

        Assert.Equal(created.JunctionId, Assert.Single(handler.Junctions).JunctionId);
        Assert.Empty(handler.JunctionDeletes);
        Assert.Single(handler.JunctionPosts);
        Assert.Empty(cut.FindAll("li.drawio-refusal"));
        Assert.Contains("Save succeeded", cut.Find("#drawio-save-summary").TextContent);
    }

    /// <summary>
    /// The other half of why the document never comes back: a cell the save <b>refused</b> must stay
    /// where it is, on the canvas, because that is where the operator fixes it. Sending the document
    /// back erased it - a document rebuilt from the plant cannot contain a cell that never reached
    /// the plant - and the report named them one by one while they were disappearing from the
    /// canvas: whoever read "it was not written" had nothing left to put right.
    /// </summary>
    [Fact]
    public async Task ARefusedCellIsLeftOnTheCanvas()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);

        // A copy: marked as a device, with an identifier the plant does not know.
        var xml = DrawioDocument.Build(GraphOf(handler), null).Replace("</root>",
            """<object id="copia-1" label="copia" wcs="device"><mxCell style="shape=image;" vertex="1" parent="1"><mxGeometry x="700" y="700" width="130" height="184" as="geometry"/></mxCell></object></root>""");

        await SaveAsync(cut, xml);

        var refusal = Assert.Single(LinesOf(cut, "drawio-refusal"), line => line.StartsWith("copia-1:"));
        // AND THE REMEDY IT NAMES IS REACHABLE, which is the half a refusal is worth. Until row 15
        // the only remedy named was "delete the shape and drag it again", and for the commonest way
        // a cell like this one arrives - a .drawio file dropped on the canvas, or a paste, carrying
        // a device that already exists - that gesture is the wrong one: it makes a SECOND device
        // and leaves the first undrawn. Pinned because this sentence has no other guard, and on
        // this branch a message nobody measured stayed false through four occurrences.
        Assert.Contains("point it at a device", refusal, StringComparison.Ordinal);
        // No document came back: the copy is still on the canvas, where whoever is drawing either
        // configures it or deletes it.
        Assert.Single(JSInterop.Invocations, i => i.Identifier == "load");
    }

    /// <summary>
    /// Deleting a device also takes its junctions away on the host (a single DELETE, see
    /// <c>PlantStore.DeleteItemAsync</c>). The ones the drawing did not show - another end not
    /// placed, or of a type outside the registry - used to disappear with no line of the report
    /// naming them: it is defect D203 in its exact form.
    /// </summary>
    [Fact]
    public async Task TheJunctionsCarriedAwayByADeviceDeletionAreNamed()
    {
        var hidden = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var handler = HostWithTwoDevices();
        // A device that never reaches the drawing (no box: DrawioDocument.IsDrawn is false),
        // connected to B by a junction that is therefore not drawn either.
        handler.Items.Add(new PlantItemDto(hidden, "nascosto", DeviceTypes.Automation, null, null, null,
            default, default, 0, null, 0, 0, 0, 0, default!, null, null, null, null, null));
        handler.Junctions = [new JunctionDto(J, hidden, B, 99)];
        var cut = await RenderWithAsync(handler);

        // B disappears from the drawing: the host will also delete the hidden junction.
        var drawn = new PlantGraph([Item(A, 10, 20)], [], [Type()]);
        await SaveAsync(cut, DrawioDocument.Build(drawn, null));

        Assert.Equal(B, Assert.Single(handler.ItemDeletes));
        // Named in the step's line, not in a notice written while the plan was being built: that way
        // the sentence follows the outcome, and an interrupted save does not declare "carried away"
        // a junction that stayed where it was.
        var line = Assert.Single(LinesOf(cut, "drawio-written"), l => l.Contains("the deletion of device"));
        Assert.Contains("also carries away with it", line);
        Assert.Contains(J.ToString(), line);
    }

    /// <summary>
    /// The engine status is read at opening since the page started writing: whoever opens the
    /// drawing with the engine running must know it <b>before</b> spending half an hour drawing, not
    /// at the save. Before this correction that read was shown to nobody, and the comment declared a
    /// benefit that did not exist.
    /// </summary>
    [Fact]
    public async Task WithTheEngineRunningTheDrawingSaysSoBeforeAnyoneDraws()
    {
        var handler = HostWithTwoDevices();
        handler.NavStatus = NavigatorStatusTypes.Run;

        var cut = await RenderWithAsync(handler);

        Assert.Contains("the engine is running", cut.Find("#drawio-engine-banner").TextContent);
    }

    /// <summary>
    /// The moves report names the devices. "The move of 12 devices", after a failure, does not tell
    /// whoever reads which twelve went through.
    /// </summary>
    [Fact]
    public async Task TheMovedDevicesAreNamedInTheReport()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);

        var drawn = new PlantGraph([Item(A, 99, 77), Item(B, 700, 300)], [], [Type()]);
        await SaveAsync(cut, DrawioDocument.Build(drawn, null));

        var line = Assert.Single(LinesOf(cut, "drawio-written"), l => l.Contains("the move of"));
        // The names the host carries for the two devices (see Item): not a count.
        Assert.Contains(A.ToString()[..4], line);
        Assert.Contains(B.ToString()[..4], line);
    }

    /// <summary>
    /// "Does it hook two ports?" is asked once only, and with the style parsing the reader already
    /// does (<c>DrawioReader.TryStyleNumber</c>): a substring search also matches a key that
    /// <b>ends</b> in <c>exitX=</c> and would make a junction be born from an arrow that is not
    /// hooked to the ports at all.
    /// </summary>
    [Fact]
    public async Task AnArrowWhoseStyleOnlyContainsAKeyEndingInExitXIsNotAJunction()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);

        var xml = DrawioDocument.Build(GraphOf(handler), null).Replace("</root>",
            $"""<object id="freccia-1" label="" wcs="junction"><mxCell style="myexitX=0.96;entryX=0.04;entryY=0.5;" edge="1" parent="1" source="{A}" target="{B}"><mxGeometry relative="1" as="geometry"/></mxCell></object></root>""");

        await SaveAsync(cut, xml);

        // The reason comes from DrawioDelta, not from the page: the guard is on the delta's own
        // words, so this line goes red if that sentence changes there.
        Assert.Empty(handler.JunctionPosts);
        Assert.Contains(LinesOf(cut, "drawio-refusal"), line => line.StartsWith("freccia-1:") && line.Contains("two ports"));
    }

    // ---------------------------------------------------------------------------------------------
    // Batch C of the properties panel: the panel WRITES. Same page, second gesture that touches the
    // plant - and the first thing these tests ask is not "does it write?" but "what happens to the
    // operator's typing when it does not?".
    // ---------------------------------------------------------------------------------------------

    /// <summary>The property the panel tests type into, by the name the contract gives it.</summary>
    private const string NameProperty = nameof(PlantItemConfigDto.Name);

    /// <summary>
    /// Renders the page, makes draw.io say 'init', and then says that <paramref name="itemId"/> is
    /// the selected cell: the panel is on screen with that device's configuration in it, which is
    /// the starting point of every test below.
    /// </summary>
    private async Task<IRenderedComponent<DrawioEditor>> RenderWithPanelOnAsync(DrawioWriteHandler handler, Guid itemId)
    {
        var cut = await RenderWithAsync(handler);
        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([itemId.ToString()]));
        // The panel really is there: without this the tests below would type into nothing and read
        // their own absence as a green.
        Assert.NotNull(cut.Find("#drawio-property-panel"));
        return cut;
    }

    /// <summary>
    /// Plans <c>renameCell</c> on the bridge module and hands the module back, so a test can read
    /// WHAT was asked of the canvas. It has to be called before the render - the module is imported
    /// at the first one - and it is called even by the tests that expect no rename at all: in this
    /// project's loose mode an unplanned call would answer <c>false</c> by itself, that is "the
    /// canvas did not take it", and the page would then declare a failure nobody staged.
    /// </summary>
    private BunitJSModuleInterop PlanRenameOnCanvas(bool taken = true)
    {
        var module = JSInterop.SetupModule("./_content/Wcs.WebEditor/drawio-bridge.js");
        // The matcher is "this identifier, whatever the arguments": Setup<bool>("renameCell") alone
        // plans the call with NO arguments, and the page's call - which carries two - would fall
        // through it to the loose fallback and come back false, that is "the canvas did not take
        // it". Measured: every rename test declared a failure the test had never staged.
        module.Setup<bool>("renameCell", _ => true).SetResult(taken);
        return module;
    }

    private static void Type(IRenderedComponent<DrawioEditor> cut, string propertyName, string value) =>
        cut.Find($"#drawio-property-panel div.property-panel-field[data-property='{propertyName}'] input").Change(value);

    private static string? Typed(IRenderedComponent<DrawioEditor> cut, string propertyName) =>
        cut.Find($"#drawio-property-panel div.property-panel-field[data-property='{propertyName}'] input").GetAttribute("value");

    private static Task ConfirmAsync(IRenderedComponent<DrawioEditor> cut) =>
        cut.Find("#property-panel-save-button").ClickAsync(new MouseEventArgs());

    private static string? WriteErrorOf(IRenderedComponent<DrawioEditor> cut) =>
        cut.FindAll("#drawio-property-write-error").SingleOrDefault()?.TextContent;

    private static string? WrittenNoteOf(IRenderedComponent<DrawioEditor> cut) =>
        cut.FindAll("#drawio-property-written").SingleOrDefault()?.TextContent;

    /// <summary>
    /// The batch: a value changed in the panel and confirmed reaches the plant, by the same road
    /// <c>PlantCanvas</c> takes for the same gesture - <c>EditItemConfigCommand</c>, one PUT on the
    /// configuration route.
    /// </summary>
    /// <remarks>
    /// The body of the PUT is asserted and not only its existence: a save that wrote the
    /// configuration it had READ - instead of the one the operator typed - would pass any test that
    /// only counted the calls.
    /// </remarks>
    [Fact]
    public async Task AValueConfirmedInThePanelReachesThePlantThroughTheConfigCommand()
    {
        var handler = HostWithTwoDevices();
        PlanRenameOnCanvas();
        var cut = await RenderWithPanelOnAsync(handler, A);

        Type(cut, nameof(PlantItemConfigDto.DelayBlind), "4321");
        await ConfirmAsync(cut);

        var written = Assert.Single(handler.ConfigWrites);
        Assert.Equal(A, written.ItemId);
        Assert.Equal(4321, written.DelayBlind);
        Assert.Null(WriteErrorOf(cut));
        Assert.Contains("Written", WrittenNoteOf(cut)!);
        // The drawing's save report is NOT where this is said: the two gestures are different, and a
        // panel line among the drawing's would name a save nobody made.
        Assert.Empty(cut.FindAll("#drawio-save-report"));
    }

    /// <summary>
    /// <b>The promise of this batch</b>: a refused write costs a retry, never the typing. The permit
    /// is somebody else's, nothing goes out - and the value the operator entered is still in the
    /// field, ready to be confirmed again when the permit frees up.
    /// </summary>
    /// <remarks>
    /// The sentence is the drawing save's own, because the check is the same one
    /// (<c>EnsureWritableAsync</c>): same refusal, same 423 from the host, same wording, two places
    /// that tell it.
    /// </remarks>
    [Fact]
    public async Task WithoutThePermitNothingIsWrittenAndTheTypedValueStays()
    {
        var handler = HostWithTwoDevices();
        handler.GrantEditLock = false;
        PlanRenameOnCanvas();
        var cut = await RenderWithPanelOnAsync(handler, A);

        Type(cut, NameProperty, "nome-nuovo");
        await ConfirmAsync(cut);

        Assert.Empty(handler.ConfigWrites);
        Assert.Contains("somebody else has been editing the plant", WriteErrorOf(cut)!);
        Assert.Null(WrittenNoteOf(cut));
        Assert.Equal("nome-nuovo", Typed(cut, NameProperty));
    }

    /// <summary>The same, through the other door of the same check: the engine is running, the host would refuse, and this page stops before asking.</summary>
    [Fact]
    public async Task WithTheEngineRunningThePanelWritesNothingAndKeepsTheTypedValue()
    {
        var handler = HostWithTwoDevices();
        handler.NavStatus = NavigatorStatusTypes.Run;
        PlanRenameOnCanvas();
        var cut = await RenderWithPanelOnAsync(handler, A);

        Type(cut, NameProperty, "nome-nuovo");
        await ConfirmAsync(cut);

        Assert.Empty(handler.ConfigWrites);
        Assert.Contains("the engine is running", WriteErrorOf(cut)!);
        Assert.Equal("nome-nuovo", Typed(cut, NameProperty));
    }

    /// <summary>
    /// The host refuses the write itself. Two things are asserted, and the second is the one that
    /// matters: the failure is <b>declared</b> (defect D200 - a panel that silently does nothing
    /// teaches the operator that the button is broken), and it is declared <b>honestly</b>. The
    /// command makes two calls, the PUT and the reread, and from out here it is not known which one
    /// fell over: the message says so instead of claiming the write did not happen.
    /// </summary>
    [Fact]
    public async Task AWriteTheHostRefusesIsDeclaredWithoutClaimingMoreThanIsKnown()
    {
        var handler = HostWithTwoDevices();
        handler.FailRoute = "config";
        PlanRenameOnCanvas();
        var cut = await RenderWithPanelOnAsync(handler, A);

        Type(cut, NameProperty, "nome-nuovo");
        await ConfirmAsync(cut);

        Assert.Empty(handler.ConfigWrites);
        // Row 37 of the queue: the why in words, and the host's answer behind the "?" - FailRoute's
        // 500 has an empty body - instead of the client's "L'API del motore ha risposto 500: " in the sentence.
        Assert.Equal(
            "Writing the configuration failed because the host answered with an error, and whether it went through is NOT known - " +
            "the request may have reached the host anyway; the values are still here, so check the device before confirming again.",
            WriteErrorOf(cut));
        Assert.Equal("the host answered 500 with an empty body", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-property-write-error"));
        Assert.Equal("nome-nuovo", Typed(cut, NameProperty));
    }

    /// <summary>
    /// Final review, M9 (C1): the 503 of the engine not loaded is the filter of the "engine" group
    /// turning a request away before the endpoint runs - certain that THAT request wrote nothing. But
    /// the write is two requests, the PUT and the reading back, and from out here it is not known which
    /// one was turned away: the line says both outcomes, and no longer a refusal that "may have reached
    /// the host anyway".
    /// </summary>
    [Fact]
    public async Task AnEngineNotLoadedOnTheWriteSaysWhatEachOfItsTwoRequestsWouldMean()
    {
        const string answer = "{\"title\":\"Il motore non è caricato\",\"status\":503,\"unavailable\":\"engineNotLoaded\"}";
        var handler = HostWithTwoDevices();
        handler.FailRoute = "config";
        handler.FailureStatus = System.Net.HttpStatusCode.ServiceUnavailable;
        handler.FailureBody = answer;
        PlanRenameOnCanvas();
        var cut = await RenderWithPanelOnAsync(handler, A);

        Type(cut, NameProperty, "nome-nuovo");
        await ConfirmAsync(cut);

        Assert.Empty(handler.ConfigWrites);
        Assert.Equal(
            "Writing the configuration stopped because the host refused one of its two requests - the engine is not ready (it is not loaded on the host): " +
            "if it was the write, nothing was written; if it was the reading back after it, the configuration was written. " +
            "The values are still here, so check the device before confirming again.",
            WriteErrorOf(cut));
        Assert.Equal("the host answered 503: " + answer, DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-property-write-error"));
        Assert.Equal("nome-nuovo", Typed(cut, NameProperty));
    }

    /// <summary>
    /// Final review, C11: the PUT went through and the host answers the reading back after it that it
    /// does not have the device (<c>EditItemConfigCommand</c>'s own <c>InvalidOperationException</c>).
    /// Said in words, with the command's own text behind the "?".
    /// </summary>
    [Fact]
    public async Task ADeviceTheHostNoLongerHasAfterThePanelsWriteIsSaidAsWrittenNotReadBack()
    {
        var handler = HostWithTwoDevices();
        PlanRenameOnCanvas();
        var cut = await RenderWithPanelOnAsync(handler, A);
        handler.HideSingleItemReads = true;

        Type(cut, nameof(PlantItemConfigDto.DelayBlind), "4321");
        await ConfirmAsync(cut);

        Assert.Equal(4321, Assert.Single(handler.ConfigWrites).DelayBlind);
        Assert.Equal(
            "The configuration was written, but the device could not be read back afterwards because the host does not have it any more: reload the page before changing anything else.",
            WriteErrorOf(cut));
        Assert.StartsWith("InvalidOperationException: ", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-property-write-error"));
    }

    /// <summary>
    /// Fix round 1 of compito 2: a DEFINITE refusal by the host (409 or 423 on the PUT, in the window
    /// after the page's own check) is not "it is not known whether it went through" - the host said
    /// no, nothing was written. The panel says the host refused it and why; the typing stays. Row 37
    /// of the queue: the host's own words are behind the line's "?", with the rest of its answer, and
    /// no longer in the sentence.
    /// </summary>
    [Theory]
    [InlineData(System.Net.HttpStatusCode.Conflict, "the plant's state does not allow it now", "Engine busy (test)",
        "the host answered 409: {\"title\":\"Engine busy (test)\",\"status\":409}")]
    [InlineData(System.Net.HttpStatusCode.Locked, "the edit permit is not (any longer) this session's", "Permesso di modifica di qualcun altro",
        "the host answered 423: {\"title\":\"Permesso di modifica di qualcun altro\",\"status\":423}")]
    public async Task ADefiniteRefusalByTheHostIsSaidAsRefusedNotAsUnknown(System.Net.HttpStatusCode status, string meaning, string hostWords, string answer)
    {
        var handler = HostWithTwoDevices();
        handler.FailRoute = "config";
        handler.FailureStatus = status;
        handler.FailureBody = System.Text.Json.JsonSerializer.Serialize(new { title = hostWords, status = (int)status });
        PlanRenameOnCanvas();
        var cut = await RenderWithPanelOnAsync(handler, A);

        Type(cut, NameProperty, "nome-nuovo");
        await ConfirmAsync(cut);

        var refusal = WriteErrorOf(cut)!;
        Assert.Equal($"The configuration was NOT written because the host refused it - {meaning}: nothing reached the plant, and the values are still here.", refusal);
        Assert.DoesNotContain(hostWords, refusal);
        Assert.Equal(answer, DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-property-write-error"));
        Assert.Empty(handler.ConfigWrites);
        Assert.Equal("nome-nuovo", Typed(cut, NameProperty));
    }

    /// <summary>
    /// Fix round 1 of compito 2: the PUT went through and the reread after it came back unreadable
    /// (200 with a body that is not JSON). The panel says it was written and could not be read back -
    /// the JsonException used to escape the click handler and end the circuit.
    /// </summary>
    [Fact]
    public async Task AnUnreadableRereadAfterThePanelsWriteIsSaidAsWrittenNotReadBack()
    {
        var handler = HostWithTwoDevices();
        PlanRenameOnCanvas();
        var cut = await RenderWithPanelOnAsync(handler, A);
        handler.GarbledRoute = "item";

        Type(cut, nameof(PlantItemConfigDto.DelayBlind), "4321");
        await ConfirmAsync(cut);

        Assert.Equal(4321, Assert.Single(handler.ConfigWrites).DelayBlind);
        // Row 37 of the queue: what the parser said is behind the "?", not in the sentence.
        Assert.Equal(
            "The configuration was written, but the device could not be read back afterwards because the host's answer could not be read: reload the page before changing anything else.",
            WriteErrorOf(cut));
        Assert.StartsWith("JsonException: '<' is an invalid start of a value.",
            DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-property-write-error"));
    }

    /// <summary>
    /// Row 37 of the queue: the read of the device that comes just before the write (D3) fails, and
    /// nothing is written. The sentence says why in the page's words - here the host's 503 that says
    /// the engine is not loaded, in the field the host writes for it (row 34) - and the host's answer
    /// is behind the "?", where the sentence used to carry it whole.
    /// </summary>
    [Fact]
    public async Task ADeviceThatCannotBeReadJustBeforeWritingIsSaidInWordsWithTheHostsAnswerBehindTheQuestionMark()
    {
        const string answer = "{\"title\":\"Il motore non è caricato\",\"status\":503,\"unavailable\":\"engineNotLoaded\"}";
        var handler = HostWithTwoDevices();
        PlanRenameOnCanvas();
        var cut = await RenderWithPanelOnAsync(handler, A);
        handler.FailRoute = "config-get";
        handler.FailureStatus = System.Net.HttpStatusCode.ServiceUnavailable;
        handler.FailureBody = answer;

        Type(cut, NameProperty, "nome-nuovo");
        await ConfirmAsync(cut);

        Assert.Empty(handler.ConfigWrites);
        Assert.Equal(
            "The configuration was NOT written: the device's current configuration could not be read from the host just before writing " +
            "because the engine is not ready (it is not loaded on the host), and without it this page cannot keep what others changed on it - " +
            "nothing reached the plant, and the values are still here.",
            WriteErrorOf(cut));
        Assert.Equal("the host answered 503: " + answer, DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-property-write-error"));
        Assert.Equal("nome-nuovo", Typed(cut, NameProperty));
    }

    /// <summary>
    /// <b>The device was deleted while the panel was open.</b> The host answers <c>404</c> and the
    /// page says the configuration was NOT written - not "it is not known whether it went through",
    /// which is what every other refusal gets and would be false here.
    /// </summary>
    /// <remarks>
    /// This is the outcome the 404 batch created. Before it, <c>PUT .../config</c> was an upsert: the
    /// device came back to life carrying the values on screen, the panel said "written", and nobody
    /// could tell that from an ordinary edit. The assertion that carries the test is not the wording
    /// but <c>handler.Configs</c>, which must stay without that device: a page that showed the
    /// refusal while the host had quietly recreated it would pass on the message alone.
    /// </remarks>
    [Fact]
    public async Task AWriteOnADeviceDeletedMeanwhileSaysNothingWasWrittenAndDoesNotBringItBack()
    {
        var handler = HostWithTwoDevices();
        PlanRenameOnCanvas();
        var cut = await RenderWithPanelOnAsync(handler, A);

        // Somebody else deletes the device between the selection and the confirmation.
        handler.Items.RemoveAll(item => item.ItemId == A);
        handler.Configs.Remove(A);

        Type(cut, NameProperty, "nome-nuovo");
        await ConfirmAsync(cut);

        var refusal = WriteErrorOf(cut)!;
        Assert.Contains("NOT written", refusal, StringComparison.Ordinal);
        Assert.Contains("does not have this device any more", refusal, StringComparison.Ordinal);
        Assert.DoesNotContain("NOT known", refusal, StringComparison.Ordinal);

        Assert.DoesNotContain(A, handler.Configs.Keys);       // non e' rinato
        Assert.Empty(handler.ItemCreations);                  // e non da nessuna strada
        Assert.Equal("nome-nuovo", Typed(cut, NameProperty)); // i valori restano sotto mano
    }

    /// <summary>
    /// After a RENAME the canvas is realigned, and only then: the plant has taken the new name
    /// before draw.io is told. A canvas relabelled first would show a name the plant does not have,
    /// which is the same lie as a panel showing another device's values.
    /// </summary>
    /// <remarks>
    /// The identifier handed to the bridge is the device's, which is also the cell's id
    /// (<c>DrawioDocument</c> writes it as the <c>&lt;object id&gt;</c>), and the label is the name
    /// that was WRITTEN, not the one that was typed - they are the same here, and they must be.
    /// </remarks>
    [Fact]
    public async Task ARenameConfirmedInThePanelIsSentToTheCanvasAfterTheWrite()
    {
        var handler = HostWithTwoDevices();
        var module = PlanRenameOnCanvas();
        var cut = await RenderWithPanelOnAsync(handler, A);

        Type(cut, NameProperty, "nome-nuovo");
        await ConfirmAsync(cut);

        Assert.Equal("nome-nuovo", Assert.Single(handler.ConfigWrites).Name);
        var asked = Assert.Single(module.Invocations["renameCell"]);
        Assert.Equal(A.ToString(), asked.Arguments[0]);
        Assert.Equal("nome-nuovo", asked.Arguments[1]);
        // THE THIRD ARGUMENT IS ALWAYS FALSE HERE - not the creation's flag. That flag makes the
        // label an undoable edit draw.io's undo walks THROUGH and on into whatever lies underneath -
        // right for a creation, where what lies underneath is the drop itself, and wrong here, where
        // the operator typed in this panel and what lies underneath is some unrelated gesture that
        // one Ctrl+Z would then undo as well.
        Assert.Equal(false, asked.Arguments[2]);
        // THE FOURTH ARGUMENT IS TRUE: this rename runs AFTER the config write above went through
        // (the method's own summary - "the canvas is realigned, and only then"), so the plant
        // already has the new name and the model edit below is an echo, not unsaved work - it must
        // not light the "changed since last save" mark (row 46's review finding on row 15: see
        // RenameConfigOnCanvasAsync, which passes alreadyOnThePlant: true for exactly this reason).
        Assert.Equal(true, asked.Arguments[3]);
        Assert.Equal(4, asked.Arguments.Count);
        Assert.Null(WriteErrorOf(cut));
    }

    /// <summary>
    /// The order, measured from the other side: a write that does NOT go through must leave the
    /// canvas alone. Without this the drawing would take a name the plant refused.
    /// </summary>
    [Fact]
    public async Task ARefusedRenameNeverReachesTheCanvas()
    {
        var handler = HostWithTwoDevices();
        handler.GrantEditLock = false;
        var module = PlanRenameOnCanvas();
        var cut = await RenderWithPanelOnAsync(handler, A);

        Type(cut, NameProperty, "nome-nuovo");
        await ConfirmAsync(cut);

        Assert.Empty(module.Invocations["renameCell"]);
    }

    /// <summary>
    /// Nothing is sent to the canvas when nothing was renamed either: the realignment exists for the
    /// name draw.io draws, and the other fifty-nine properties are not on the drawing at all.
    /// </summary>
    [Fact]
    public async Task AChangeThatIsNotARenameLeavesTheCanvasAlone()
    {
        var handler = HostWithTwoDevices();
        var module = PlanRenameOnCanvas();
        var cut = await RenderWithPanelOnAsync(handler, A);

        Type(cut, nameof(PlantItemConfigDto.DelayBlind), "4321");
        await ConfirmAsync(cut);

        Assert.Single(handler.ConfigWrites);
        Assert.Empty(module.Invocations["renameCell"]);
    }

    /// <summary>
    /// The canvas does not take the new label - the cell was deleted from the drawing while the
    /// write was on its way, or the selection could not be followed at all. The write went through
    /// all the same, so it is not called a failure: it is said that the drawing is now behind, which
    /// is the fact whoever is looking at the canvas needs (defect D200).
    /// </summary>
    [Fact]
    public async Task ACanvasThatDoesNotTakeTheNewLabelIsSaidInsteadOfKeptQuiet()
    {
        var handler = HostWithTwoDevices();
        PlanRenameOnCanvas(taken: false);
        var cut = await RenderWithPanelOnAsync(handler, A);

        Type(cut, NameProperty, "nome-nuovo");
        await ConfirmAsync(cut);

        // Written: that half succeeded and must not be denied.
        Assert.Equal("nome-nuovo", Assert.Single(handler.ConfigWrites).Name);
        Assert.Contains("Written", WrittenNoteOf(cut)!);
        Assert.Contains("still shows the old label", WriteErrorOf(cut)!);
    }

    /// <summary>
    /// Row 37 of the queue (the owner's answer of 9/10): a drawing that could not be told of the new
    /// name at all (the bridge failed) is said in one sentence, and the bridge's own failure is behind
    /// the line's "?" - the sentence used to carry it in brackets. The device list says it the same way
    /// (DrawioEditorDeviceListTest).
    /// </summary>
    [Fact]
    public async Task ADrawingThatCannotBeToldOfTheNewNameIsSaidWithItsFailureBehindTheQuestionMark()
    {
        var handler = HostWithTwoDevices();
        var module = PlanRenameOnCanvas();
        module.Setup<bool>("renameCell", call => (call.Arguments[1] as string) == "nome-nuovo")
            .SetException(new Microsoft.JSInterop.JSException("the canvas did not take it (test)"));
        var cut = await RenderWithPanelOnAsync(handler, A);

        Type(cut, NameProperty, "nome-nuovo");
        await ConfirmAsync(cut);

        Assert.Equal("nome-nuovo", Assert.Single(handler.ConfigWrites).Name);
        Assert.Equal("The new name is on the plant, but the drawing could not be told, so it still shows the old label until the page is reloaded.",
            WriteErrorOf(cut)!.Trim());
        Assert.Equal("the canvas did not take it (test)", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-property-write-error"));
    }

    /// <summary>
    /// Row 37 of the queue (the owner's answer of 9/10): the edit permit that could not be asked for
    /// is said in the panel with its why, and what came back is behind the line's "?" - where the
    /// sentence used to end on the failure's message. The save, the device list, the PLC connections,
    /// the connection mode, the engine's start and the creation say it the same way.
    /// </summary>
    [Fact]
    public async Task APermitThatCannotBeAskedForIsSaidInThePanelWithTheFailureBehindTheQuestionMark()
    {
        var handler = HostWithTwoDevices();
        PlanRenameOnCanvas();
        var cut = await RenderWithPanelOnAsync(handler, A);
        handler.HttpFailureRoute = "edit-lock";

        Type(cut, NameProperty, "nome-nuovo");
        await ConfirmAsync(cut);

        Assert.Empty(handler.ConfigWrites);
        Assert.Equal("The edit permit could not be asked for because the connection to the host failed: nothing was written.",
            WriteErrorOf(cut)!.Trim());
        Assert.StartsWith("HttpRequestException: ", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-property-write-error"));
        Assert.Equal("nome-nuovo", Typed(cut, NameProperty));
    }

    /// <summary>The same permit, asked for by a save of the drawing: its report's error line says it the same way.</summary>
    [Fact]
    public async Task APermitThatCannotBeAskedForIsSaidInTheSavesReportWithTheFailureBehindTheQuestionMark()
    {
        var handler = HostWithTwoDevices();
        var cut = await RenderWithAsync(handler);
        handler.HttpFailureRoute = "edit-lock";

        var drawn = new PlantGraph([Item(A, 99, 77), Item(B, 500, 20)], [], [Type()]);
        await SaveAsync(cut, DrawioDocument.Build(drawn, null));

        Assert.Empty(handler.Writes);
        Assert.Equal("The edit permit could not be asked for because the connection to the host failed: nothing was written.",
            cut.Find("#drawio-save-error").TextContent.Trim());
        Assert.StartsWith("HttpRequestException: ", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-save-error"));
    }

    /// <summary>
    /// <b>The next round after a rename.</b> The label lives in the drawing too, and the question is
    /// whether the save that follows sees a difference there: it does not. <c>DrawioReader</c> never
    /// reads the label - the identifier on the <c>&lt;object&gt;</c> is what says which device a cell
    /// is - so a drawing that still carries the OLD label produces neither a write nor a refusal.
    /// </summary>
    /// <remarks>
    /// The drawing is built from the plant as it was BEFORE the rename on purpose: that is exactly
    /// what comes back from a canvas whose relabelling failed, and it is the harshest form of the
    /// question.
    /// </remarks>
    [Fact]
    public async Task ARenameFromThePanelChangesNothingInTheNextSave()
    {
        var handler = HostWithTwoDevices();
        PlanRenameOnCanvas();
        var drawnWithTheOldLabels = DrawioDocument.Build(GraphOf(handler), null);
        var cut = await RenderWithPanelOnAsync(handler, A);

        Type(cut, NameProperty, "nome-nuovo");
        await ConfirmAsync(cut);
        Assert.Single(handler.ConfigWrites);

        await SaveAsync(cut, drawnWithTheOldLabels);

        Assert.Empty(handler.GeometryWrites);
        Assert.Empty(handler.ItemDeletes);
        Assert.Empty(handler.JunctionPosts);
        Assert.Empty(handler.JunctionDeletes);
        Assert.Empty(LinesOf(cut, "drawio-refusal"));
        // The one write is the decoration, which every save of this page makes once the decoration
        // was read at opening (see the plan): nothing about the device, which is the point.
        Assert.Contains("Save succeeded: 1 write", cut.Find("#drawio-save-summary").TextContent);
    }

    /// <summary>
    /// The sharp half of the same question, and the one that says why the page's own graph has to
    /// follow the write. A deletion from the drawing is refused when the device looks changed on the
    /// host as well (<c>DrawioDelta.ChangedForDeletion</c> compares the whole device, NAME included,
    /// not just its geometry) - so a rename this page made itself, if it did not reach the graph the
    /// save compares against, would come back as "somebody else changed it" and block a legitimate
    /// deletion.
    /// </summary>
    [Fact]
    public async Task ADeviceRenamedFromThePanelCanStillBeDeletedByTheNextSave()
    {
        var handler = HostWithTwoDevices();
        PlanRenameOnCanvas();
        var cut = await RenderWithPanelOnAsync(handler, A);

        Type(cut, NameProperty, "nome-nuovo");
        await ConfirmAsync(cut);

        // A is no longer in the drawing that comes back.
        await SaveAsync(cut, DrawioDocument.Build(new PlantGraph([Item(B, 500, 20)], [], [Type()]), null));

        Assert.Equal(A, Assert.Single(handler.ItemDeletes));
        Assert.Empty(LinesOf(cut, "drawio-refusal"));
    }

    /// <summary>
    /// <b>The overlap between the two writing gestures of this page, staged rather than waited for.</b>
    /// The drawing's save is held inside, past the permit and on the reread of the plant, and the
    /// panel is confirmed while it sits there. The panel write is refused and says so; nothing of
    /// what was typed is lost, and it goes through once the save is over.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What the interleaving really cost, measured by taking the gate out:</b> the configuration
    /// was written and the host held the new name, while the page's own line named the device
    /// <c>Selected: 1111</c> - the name it had before - with nothing on screen saying so. The cause
    /// and the shape of the window are in the remarks of <c>DrawioEditor._plantWriter</c>.
    /// </para>
    /// <para>
    /// <b>Where the save is held matters, and it was got wrong once.</b> Holding it on the reread of
    /// the plant proves nothing: a panel write slipping in there lands INSIDE that read, and the
    /// save closes on a snapshot that already carries it - measured, green, and blind. The window is
    /// the plan, so the hold is on the first write of it.
    /// </para>
    /// <para>
    /// The hold is not a sleep: <c>ItemsGateReached</c> completes when the save is really inside the
    /// gate, so the panel is confirmed at a point the test KNOWS the save has reached.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task APanelWriteDuringADrawingSaveIsRefusedInsteadOfBeingLost()
    {
        var handler = HostWithTwoDevices();
        PlanRenameOnCanvas();
        var cut = await RenderWithPanelOnAsync(handler, A);

        // The operator moves A on the canvas and saves the drawing; the save is held INSIDE its
        // plan, on the first write - that is, after it has read the plant again. The point matters:
        // held on the reread instead, a panel write slipping in would still end up inside that
        // reread and nothing would go wrong (measured, and it is why this gate is on the write).
        handler.GeometryGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drawn = DrawioDocument.Build(new PlantGraph([Item(A, 99, 77), Item(B, 500, 20)], [], [Type()]), null);
        var saving = cut.InvokeAsync(() => cut.Instance.OnDrawioSaved(drawn));
        await handler.GeometryGateReached.Task;
        Assert.False(saving.IsCompleted, "The drawing's save did not stay suspended: the overlap was never staged.");

        // And now the panel, in the middle of it.
        Type(cut, NameProperty, "nome-nuovo");
        await ConfirmAsync(cut);

        Assert.Empty(handler.ConfigWrites);
        Assert.Contains("drawing is being saved", WriteErrorOf(cut)!);
        Assert.Equal("nome-nuovo", Typed(cut, NameProperty));

        // The save finishes, and only then does the same click write.
        handler.GeometryGate.SetResult();
        await saving;
        Assert.Single(handler.GeometryWrites);

        await ConfirmAsync(cut);
        Assert.Equal("nome-nuovo", Assert.Single(handler.ConfigWrites).Name);
        Assert.Null(WriteErrorOf(cut));

        // And the page names the device as the plant does: the check that the interleaving failed.
        Assert.Equal("Selected: nome-nuovo", cut.Find("#drawio-selection").TextContent);
    }

    /// <summary>
    /// The same gate from the other side: draw.io saves while the panel's write is on its way. The
    /// drawing is not written and it is not lost either - it stays on the canvas, which is where the
    /// work not yet written lives on this page.
    /// </summary>
    [Fact]
    public async Task ADrawingSaveDuringAPanelWriteIsRefusedInsteadOfInterleaving()
    {
        var handler = HostWithTwoDevices();
        PlanRenameOnCanvas();
        var cut = await RenderWithPanelOnAsync(handler, A);

        // The panel's write is held on its reread of the device: EditItemConfigCommand reads the
        // item back after the PUT, and it is that GET - one device, not the list - that this gate
        // holds. The PUT has therefore already gone through when the drawing's save arrives, which
        // is the worst moment for it to arrive.
        handler.SingleItemGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Type(cut, NameProperty, "nome-nuovo");
        var confirming = ConfirmAsync(cut);
        await handler.SingleItemGateReached.Task;
        Assert.False(confirming.IsCompleted, "The panel's write did not stay suspended: the overlap was never staged.");

        await SaveAsync(cut, DrawioDocument.Build(new PlantGraph([Item(A, 99, 77), Item(B, 500, 20)], [], [Type()]), null));

        Assert.Empty(handler.GeometryWrites);
        Assert.Contains("properties panel is being written", cut.Find("#drawio-save-error").TextContent);

        handler.SingleItemGate.SetResult();
        await confirming;
        Assert.Equal("nome-nuovo", Assert.Single(handler.ConfigWrites).Name);
    }

    /// <summary>
    /// <b>The window the gate did not cover</b>, and the one the operator's own gesture opens: the
    /// panel is held INSIDE <c>EnsureWritableAsync</c> - that is, between the check of the mutual
    /// exclusion and the moment it used to be switched on - and the drawing's save arrives right
    /// there. It found the gate open, closed it behind itself and went through: two plans
    /// interleaved, in silence.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written against the code of the day and watched to fail on it, which is the only way a test
    /// of a race means anything. What it read then, word for word: one geometry written by a save
    /// that should have been refused, the configuration written as well, and
    /// <c>Selected: 1111</c> on the page's own line while the host held <c>nome-nuovo</c>.
    /// </para>
    /// <para>
    /// <b>The hold is on the permit and not on the engine status</b>, the second of the two calls
    /// <c>EnsureWritableAsync</c> makes: the earlier point is the harsher one, because everything
    /// after it was still outside the gate. The gate in the fake host is armed for ONE request, so
    /// the save that arrives next asks for its permit without being held too - otherwise the test
    /// would be measuring the fake host instead of the page.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ADrawingSaveArrivingWhileThePanelAsksForThePermitIsRefusedInsteadOfInterleaving()
    {
        var handler = HostWithTwoDevices();
        PlanRenameOnCanvas();
        var cut = await RenderWithPanelOnAsync(handler, A);

        // The panel is confirmed and stays suspended on the permit request, which is where the old
        // code had not switched the gate on yet.
        handler.EditLockGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Type(cut, NameProperty, "nome-nuovo");
        var confirming = ConfirmAsync(cut);
        await handler.EditLockGateReached.Task;
        Assert.False(confirming.IsCompleted, "The panel's write did not stay suspended on the permit: the overlap was never staged.");

        // draw.io saves the drawing exactly there, with A moved.
        await SaveAsync(cut, DrawioDocument.Build(new PlantGraph([Item(A, 99, 77), Item(B, 500, 20)], [], [Type()]), null));

        Assert.Empty(handler.GeometryWrites);
        Assert.Contains("properties panel is being written", cut.Find("#drawio-save-error").TextContent);

        handler.EditLockGate.SetResult();
        await confirming;

        Assert.Equal("nome-nuovo", Assert.Single(handler.ConfigWrites).Name);
        Assert.Null(WriteErrorOf(cut));
        // The page names the device as the plant does: the symptom the interleaving left behind.
        Assert.Equal("Selected: nome-nuovo", cut.Find("#drawio-selection").TextContent);
    }

    /// <summary>
    /// <b>The gate says who holds it.</b> Two presses of the panel's button in a row: the second one
    /// is refused, and the refusal names THIS panel - not the drawing's save, which is what a gate
    /// that only knew "somebody is writing" used to claim while no drawing was being saved at all.
    /// </summary>
    /// <remarks>
    /// The two lines that stay on screen together are asserted as a pair, because that is how they
    /// are read: one says the first confirmation reached the plant, the other says the second press
    /// wrote nothing of its own. Before, they contradicted each other - "written" next to a "NOT
    /// written" about a save nobody had made.
    /// </remarks>
    [Fact]
    public async Task ASecondPressOfThePanelsButtonIsRefusedInItsOwnName()
    {
        var handler = HostWithTwoDevices();
        PlanRenameOnCanvas();
        var cut = await RenderWithPanelOnAsync(handler, A);

        handler.SingleItemGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Type(cut, NameProperty, "nome-nuovo");
        var confirming = ConfirmAsync(cut);
        await handler.SingleItemGateReached.Task;

        await ConfirmAsync(cut);

        Assert.Contains("This panel is already writing", WriteErrorOf(cut)!);
        Assert.DoesNotContain("drawing is being saved", WriteErrorOf(cut)!);

        handler.SingleItemGate.SetResult();
        await confirming;

        // One write for two presses, and the two lines on screen are both true of it.
        Assert.Equal("nome-nuovo", Assert.Single(handler.ConfigWrites).Name);
        Assert.Contains("Written", WrittenNoteOf(cut)!);
        Assert.Contains("This panel is already writing", WriteErrorOf(cut)!);
    }

    /// <summary>
    /// <b>The successful path costs the typing no more than the refused one does.</b> The write is
    /// held on its reread - four round trips of window - and the operator goes on typing while it is
    /// in flight: what was confirmed reaches the plant, and what is being typed is still in the
    /// field afterwards.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The page used to hand <c>PropertyPanel</c> the snapshot it had just written as the new
    /// <c>Config</c>. Being a different object, that resyncs the panel's working copy, and everything
    /// entered in the meantime went with it - measured on this very staging: the field held
    /// <c>nome-nuovissimo</c> during the write and the old name after it, with nothing said. The same
    /// silent loss the refusal path was built to avoid, left open on the other branch.
    /// </para>
    /// <para>
    /// Both facts are asserted, because either alone would pass on a page that is wrong the other
    /// way: a panel that never wrote anything would keep the typing just as well.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task WhatIsTypedWhileTheWriteIsOnItsWayIsStillThereWhenItSucceeds()
    {
        var handler = HostWithTwoDevices();
        PlanRenameOnCanvas();
        var cut = await RenderWithPanelOnAsync(handler, A);

        handler.SingleItemGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Type(cut, NameProperty, "nome-nuovo");
        var confirming = ConfirmAsync(cut);
        await handler.SingleItemGateReached.Task;

        // The fields stay live while the write is on its way - nothing switches them off - so this
        // is what an operator who keeps working does.
        Type(cut, NameProperty, "nome-nuovissimo");

        handler.SingleItemGate.SetResult();
        await confirming;

        // What was confirmed is what went out: the later typing was not smuggled into the write.
        Assert.Equal("nome-nuovo", Assert.Single(handler.ConfigWrites).Name);
        Assert.Contains("Written", WrittenNoteOf(cut)!);
        // And it is still in the field, ready for the next confirmation.
        Assert.Equal("nome-nuovissimo", Typed(cut, NameProperty));
    }

    /// <summary>
    /// The confirmation arrives when the page is no longer showing that configuration at all, and it
    /// is <b>said</b> instead of being dropped without a word.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It is reachable, and this is the shape it has.</b> The panel is only rendered while a
    /// configuration is on screen, so the way in is a click dispatched against a render that the
    /// page has since moved past: the selection changes, <c>RefreshSelectedConfigAsync</c> empties
    /// the panel and then awaits the host, and a click already on its way from the browser is
    /// dispatched at that await, to the callback the panel handed out before. That is what this test
    /// does - it keeps the callback, changes the selection, and invokes it - because bUnit cannot
    /// click a button that is no longer in the DOM.
    /// </para>
    /// <para>
    /// The gesture must not write: the values in hand are a device's this page is not showing any
    /// more, and the "how it was" they would be written against is gone.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AConfirmationThatArrivesAfterThePanelIsGoneIsSaidInsteadOfDroppedInSilence()
    {
        var handler = HostWithTwoDevices();
        PlanRenameOnCanvas();
        var cut = await RenderWithPanelOnAsync(handler, A);

        // The callback the panel was given, kept exactly as a click already on its way keeps it.
        var confirm = cut.FindComponent<Wcs.WebEditor.Properties.PropertyPanel>().Instance.OnSave;
        var typed = new PlantItemConfigDto { ItemId = A, Name = "nome-nuovo", DeviceType = 0 };

        // And the selection moves on: the panel is not about A any more.
        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([]));
        Assert.Empty(cut.FindAll("#drawio-property-panel"));

        await cut.InvokeAsync(() => confirm.InvokeAsync(typed));

        Assert.Empty(handler.ConfigWrites);
        Assert.Contains("no longer showing", WriteErrorOf(cut)!);
    }

    // ---- Owner's decisions of 27/9 ---------------------------------------------------------------

    /// <summary>D2: in Recover and Dispose the page is read only too (CanEdit), as the host is - nothing is sent.</summary>
    [Theory]
    [InlineData(NavigatorStatusTypes.Recover, "recovering")]
    [InlineData(NavigatorStatusTypes.Dispose, "shutting down")]
    public async Task InRecoverAndDisposeThePanelWritesNothingAndSaysWhy(NavigatorStatusTypes state, string words)
    {
        var handler = HostWithTwoDevices();
        PlanRenameOnCanvas();
        var cut = await RenderWithPanelOnAsync(handler, A);
        handler.NavStatus = state;

        Type(cut, NameProperty, "nome-nuovo");
        await ConfirmAsync(cut);

        Assert.Contains($"Read only: the engine is {words}", WriteErrorOf(cut)!);
        Assert.Empty(handler.ConfigWrites);
        Assert.Equal("nome-nuovo", Typed(cut, NameProperty));
    }

    /// <summary>
    /// D1: a device's DeviceConnectionId (like its ConnectionType) is changed only with the engine
    /// stopped - in Pause the page refuses it before writing, and says why.
    /// </summary>
    [Fact]
    public async Task InPauseAConnectionChangeFromThePanelIsRefusedAndSaysWhy()
    {
        var handler = HostWithTwoDevices();
        PlanRenameOnCanvas();
        var cut = await RenderWithPanelOnAsync(handler, A);
        handler.NavStatus = NavigatorStatusTypes.Pause;

        Type(cut, nameof(PlantItemConfigDto.DeviceConnectionId), "7");
        await ConfirmAsync(cut);

        var refusal = WriteErrorOf(cut)!;
        Assert.StartsWith("The configuration was NOT written: the engine is paused, and a device's ConnectionType and DeviceConnectionId are changed only with the engine stopped", refusal);
        Assert.Contains("resuming from Pause does not reopen them", refusal);
        Assert.Empty(handler.ConfigWrites);
    }

    /// <summary>D1: in Pause every OTHER field of the panel is still written (the 15/9 rule for Pause stands).</summary>
    [Fact]
    public async Task InPauseAnotherFieldFromThePanelIsWritten()
    {
        var handler = HostWithTwoDevices();
        PlanRenameOnCanvas();
        var cut = await RenderWithPanelOnAsync(handler, A);
        handler.NavStatus = NavigatorStatusTypes.Pause;

        Type(cut, NameProperty, "nome-nuovo");
        await ConfirmAsync(cut);

        Assert.Equal("nome-nuovo", Assert.Single(handler.ConfigWrites).Name);
    }

    /// <summary>
    /// D3, fetch-then-apply: another client changed ANOTHER field of the device after the panel read
    /// it. The write reads the device again inside the gate and sends only what the operator changed
    /// on top of it: the other client's value survives.
    /// </summary>
    [Fact]
    public async Task AFieldAnotherClientChangedMeanwhileSurvivesThePanelsWrite()
    {
        var handler = HostWithTwoDevices();
        PlanRenameOnCanvas();
        var cut = await RenderWithPanelOnAsync(handler, A);
        handler.Configs[A].MaxUnloadWeight = 777;

        Type(cut, NameProperty, "nome-nuovo");
        await ConfirmAsync(cut);

        var written = Assert.Single(handler.ConfigWrites);
        Assert.Equal(("nome-nuovo", 777), (written.Name, written.MaxUnloadWeight));
        Assert.Null(WriteErrorOf(cut));
    }

    /// <summary>
    /// Confirmation review, I-A: TWO confirmations from the same panel read. Another client changes
    /// MaxUnloadWeight after the panel read it; the operator changes Name and confirms (the write keeps
    /// the other client's value), then changes Subitem and confirms again. The panel's working copy
    /// still holds the OLD MaxUnloadWeight it read - the operator never touched it - and the second
    /// write must not send it back: the other client's value survives both writes.
    /// </summary>
    [Fact]
    public async Task ASecondConfirmationDoesNotRevertAFieldAnotherClientChanged()
    {
        var handler = HostWithTwoDevices();
        PlanRenameOnCanvas();
        var cut = await RenderWithPanelOnAsync(handler, A);
        handler.Configs[A].MaxUnloadWeight = 777;

        Type(cut, NameProperty, "nome-nuovo");
        await ConfirmAsync(cut);
        Type(cut, nameof(PlantItemConfigDto.Subitem), "S2");
        await ConfirmAsync(cut);

        Assert.Equal(2, handler.ConfigWrites.Count);
        var second = handler.ConfigWrites[1];
        Assert.Equal(("nome-nuovo", "S2", 777), (second.Name, second.Subitem, second.MaxUnloadWeight));
        Assert.Equal(777, handler.Configs[A].MaxUnloadWeight);
        Assert.Null(WriteErrorOf(cut));
    }

    /// <summary>
    /// Last cleanup, I-A's other half: the operator writes Name, then puts Name back to the value the
    /// panel READ and confirms again. That is a change against what this panel wrote, so it is sent -
    /// and no conflict, since the host still holds what this panel wrote. A basis "simplified" back to
    /// the read would see no change at all and send the host's (the first write's) Name instead.
    /// </summary>
    [Fact]
    public async Task PuttingAWrittenFieldBackToItsReadValueIsSentWithoutAConflict()
    {
        var handler = HostWithTwoDevices();
        PlanRenameOnCanvas();
        var cut = await RenderWithPanelOnAsync(handler, A);
        var readName = handler.Configs[A].Name!;

        Type(cut, NameProperty, "nome-nuovo");
        await ConfirmAsync(cut);
        Type(cut, NameProperty, readName);
        await ConfirmAsync(cut);

        Assert.Equal(2, handler.ConfigWrites.Count);
        Assert.Equal(readName, handler.ConfigWrites[1].Name);
        Assert.Equal(readName, handler.Configs[A].Name);
        Assert.Null(WriteErrorOf(cut));
    }

    /// <summary>
    /// D3: the SAME field was changed on the host since the panel read it. Not overwritten in silence:
    /// refused, naming the field, the host's value and the operator's, and the panel read again.
    /// </summary>
    [Fact]
    public async Task TheSameFieldChangedMeanwhileOnTheHostIsRefusedNamedAndReadAgain()
    {
        var handler = HostWithTwoDevices();
        PlanRenameOnCanvas();
        var cut = await RenderWithPanelOnAsync(handler, A);
        handler.Configs[A].Name = "cambiato-altrove";

        Type(cut, NameProperty, "nome-nuovo");
        await ConfirmAsync(cut);

        cut.WaitForAssertion(() => Assert.Equal("cambiato-altrove", Typed(cut, NameProperty)));
        Assert.Equal(
            "The configuration was NOT written: Name was changed on the host since this panel read it - the host now holds \"cambiato-altrove\", and you wrote \"nome-nuovo\". " +
            "Nothing was written, so as not to overwrite it; the panel has been read again from the host (anything typed into it and not confirmed is gone) - confirm again if you still want your value.",
            WriteErrorOf(cut));
        Assert.Empty(handler.ConfigWrites);
    }

    /// <summary>D3: the device gone by the time of the write - the read inside the gate says so, and nothing is even sent.</summary>
    [Fact]
    public async Task ADeviceGoneBeforeTheWriteIsSaidGoneAndNothingIsSent()
    {
        var handler = HostWithTwoDevices();
        PlanRenameOnCanvas();
        var cut = await RenderWithPanelOnAsync(handler, A);
        handler.Items.RemoveAll(item => item.ItemId == A);
        handler.Configs.Remove(A);

        Type(cut, NameProperty, "nome-nuovo");
        await ConfirmAsync(cut);

        Assert.Contains("does not have this device any more", WriteErrorOf(cut)!);
        Assert.Empty(handler.ConfigWrites);
    }
}
