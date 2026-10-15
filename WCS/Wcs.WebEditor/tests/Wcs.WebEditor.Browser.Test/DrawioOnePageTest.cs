using Xunit;
using Xunit.Abstractions;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// The Editor draws the plant on ONE page, and a second one cannot pass in silence (owner's
/// decision of 2026-09-17).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it is a loss and not a preference.</b> draw.io accepts a second page, and from that
/// moment it hands back <c>&lt;mxfile pages="2"&gt;</c> with two <c>&lt;diagram&gt;</c>. Our reader
/// takes the first <c>&lt;root&gt;</c> it finds - page one - and the decoration is rewritten from
/// what was read, so everything drawn on page two is thrown away at the next opening with nobody
/// saying a word. <b>Measured</b> by the owner in a real browser before this batch was written.
/// </para>
/// <para>
/// <b>The two halves are measured separately, and the second is the one that counts.</b> The first
/// takes the roads away (the bridge's <c>keepOnePageOnly</c>); the second makes sure that a second
/// page, however it got there, <b>stops the save</b>. Only the second is a guarantee: the block is
/// our code running inside somebody else's application - it can fail to install, and a draw.io
/// upgrade can open a road nobody has measured - which is why the second measurement here makes its
/// page through draw.io's own function, behind the block's back, instead of through a road the
/// block is supposed to have shut.
/// </para>
/// <para>
/// <b>What the measurements are anchored to.</b> <c>ui.pages.length</c>, and the plant read back
/// off the host. Not a missing "+" and not a class name: a test that looked for a button that is no
/// longer drawn would go green on its own the day draw.io renamed it, which is the shape of the
/// blind test this branch keeps producing.
/// </para>
/// </remarks>
public sealed class DrawioOnePageTest(DrawioEditorFixture fixture, ITestOutputHelper output) : IClassFixture<DrawioEditorFixture>
{
    /// <summary>Who seeds the plant: a lock holder of its own, released at once, as in the other browser tests.</summary>
    private const string SeedHolder = "browser-test-one-page-seed";

    /// <summary>The drag, in screen pixels: enough for the save to have something real to write if the refusal did not stop it.</summary>
    private const double DragX = 160;
    private const double DragY = 100;

    /// <summary>How far two plant coordinates may sit and still count as the same place.</summary>
    private const double SamePlace = 0.5;

    private void DeclareBinary() => output.WriteLine(fixture.Host.Declaration);

    /// <summary>
    /// Every road to a second page, walked one after the other on a real draw.io: none of them adds
    /// one. The three page counts after each attempt are the assertion - the roads' own refusals
    /// are only written down, because one of them (deleting the only page) is swallowed by draw.io
    /// itself and never reaches us as a throw.
    /// <para>
    /// The Editor has to be <b>whole</b> afterwards, and that is the other half of this
    /// measurement: the devices are still drawn, and a save still writes. A block that also broke
    /// the drawing would pass a test that only counted pages.
    /// </para>
    /// </summary>
    [Fact]
    public async Task NoRoadInTheEditorAddsASecondPage()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);

        Assert.Equal(1, await editor.PagesInTheDrawingAsync());

        // The bar with the "+" and the per-tab context menu: hidden, and present - "absent" would
        // mean the bridge hid something that is not there any more.
        Assert.Equal("none/0", await editor.TheTabBarAsync());

        // THE EDITOR IS WHOLE WITH THE BLOCK IN PLACE, and it is measured BEFORE the roads are
        // walked: draw.io answers a refusal of its own with a modal dialog, so a drag attempted
        // after them would be measuring whether the dialogs were closed, not whether the canvas
        // still works.
        var (before, after) = await editor.DragDeviceAsync(PlantSeed.DraggedName, DragX, DragY);
        Assert.True(Math.Abs(after.X - before.X) > 1,
            "Il dispositivo non si e' mosso sullo schermo: dopo il blocco delle pagine la tela non risponde piu' al mouse.");

        await editor.SaveAsync();
        Assert.Null(await editor.SaveErrorAsync());

        var moved = await PlantSeed.ReadGeometryAsync(client, PlantSeed.DraggedId);
        Assert.True(Math.Abs(moved.X!.Value - PlantSeed.DraggedGeometry.X!.Value) > SamePlace,
            $"L'impianto non ha preso lo spostamento (X e' rimasta {moved.X}): con il blocco delle pagine il salvataggio non scrive piu'.");

        var roads = await editor.TryEveryRoadToASecondPageAsync();
        foreach (var road in roads) output.WriteLine(road);

        // Ten roads are walked: fewer lines than that means one of them stopped being walked, not
        // that it stopped working. Three of the ten - importXml twice and pasteXml - were added on
        // 2026-09-18, when they were found to make pages without ever touching insertPage; the
        // tenth, <c>setFileData</c>, was added later the same day, when the review measured that the
        // very message the bridge's own <c>load()</c> posts took the drawing from one page to two
        // with everything above already shut.
        Assert.Equal(10, roads.Length);
        foreach (var road in roads)
            Assert.True(road.Contains("before=1 after=1"),
                $"Una strada verso una seconda pagina e' rimasta aperta: {road}");

        Assert.Equal(1, await editor.PagesInTheDrawingAsync());
    }

    /// <summary>
    /// A second page made behind the block's back <b>stops the save whole</b>: the plant does not
    /// move, not even for the device that was really dragged on page one, and the page says why.
    /// <para>
    /// <b>Why the page is made through draw.io's own prototype and not through a gesture.</b> The
    /// reason written here used to be "it stands for Extras &gt; Edit Diagram, the road that is left",
    /// and that was false twice over: the same commit measured that road NOT to add pages
    /// (<c>editor.setGraphXml</c> with a two-page <c>&lt;mxfile&gt;</c> leaves the count at one), and
    /// this embedded draw.io has no menu bar at all to reach it from (<c>ui.menubar</c> is null,
    /// measured 2026-09-18). The real reason is smaller and harder: the bridge's block is our code
    /// running inside somebody else's application. It can fail to install, and then it only writes a
    /// line in the console; and the list of roads it shuts is a list of what somebody thought to
    /// try. On 2026-09-18 one that nobody had tried - a plain Ctrl+V of the text of a
    /// <c>.drawio</c> file - was found to take the drawing from one page to three <b>with that block
    /// in place</b>. This measurement is the one that did not care: it makes the page the way the
    /// bridge cannot stop, and asks the save.
    /// </para>
    /// <para>
    /// The drag is what makes this measurement mean something: without it the save would have
    /// nothing to write, and a plant that did not change would prove nothing. Here the plant
    /// <b>would</b> have changed, and the number read back off the host is the proof that it did
    /// not.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ASecondPageMadeByHandStopsTheSaveAndLeavesThePlantWhereItWas()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);

        // A real gesture first: this is what the save would write.
        var (before, after) = await editor.DragDeviceAsync(PlantSeed.DraggedName, DragX, DragY);
        Assert.True(Math.Abs(after.X - before.X) > 1,
            "Il dispositivo non si e' mosso sullo schermo: senza il gesto il salvataggio non avrebbe niente da scrivere e la prova non misurerebbe niente.");

        Assert.Equal(2, await editor.MakeASecondPageBehindTheBlocksBackAsync());

        await editor.RecordReturnedDrawingAsync();
        await editor.SaveAsync();

        // What draw.io really hands back with two pages, read off the wire and not assumed.
        var drawing = await editor.LastReturnedDrawingAsync();
        output.WriteLine($"Il disegno tornato porta {CountOf(drawing, "<diagram ")} <diagram>.");
        Assert.Equal(2, CountOf(drawing, "<diagram "));

        var error = await editor.SaveErrorAsync();
        Assert.NotNull(error);
        output.WriteLine(error);
        Assert.Contains("2 pages", error);
        Assert.Contains("Nothing at all was written", error);

        // THE ASSERTION THAT COUNTS: the plant, read back off the host. The drawn device is exactly
        // where it was seeded, so nothing of the drag went through.
        var dragged = await PlantSeed.ReadGeometryAsync(client, PlantSeed.DraggedId);
        Unmoved("il dispositivo trascinato, X", PlantSeed.DraggedGeometry.X, dragged.X);
        Unmoved("il dispositivo trascinato, Y", PlantSeed.DraggedGeometry.Y, dragged.Y);

        var untouched = await PlantSeed.ReadGeometryAsync(client, PlantSeed.UntouchedId);
        Unmoved("il dispositivo fermo, X", PlantSeed.UntouchedGeometry.X, untouched.X);
        Unmoved("il dispositivo fermo, Y", PlantSeed.UntouchedGeometry.Y, untouched.Y);
    }

    /// <summary>
    /// <b>A real Ctrl+V of the text of a two-page <c>.drawio</c> file</b>: no page is made, the first
    /// page's shape does arrive, the second page's does not, and draw.io says out loud what was left
    /// out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the road that was open behind the block for a whole batch</b>, and it is the
    /// commonest gesture there is. Measured on 2026-09-18 before it was closed, with the system
    /// clipboard and the real keys: <c>pages 1 -&gt; 3</c>, and draw.io moved the canvas to page two
    /// (<c>currentPage index 1</c>) - with the tab bar hidden, nothing on the screen said so. What
    /// was drawn there would have been thrown away at the next opening.
    /// </para>
    /// <para>
    /// <b>The three assertions are not interchangeable.</b> "One page" alone would also pass if the
    /// paste had been refused whole, which is not what was chosen; "the first page arrived" alone
    /// would pass while the second page was quietly merged in; and the dialog is the declaration
    /// itself, which is the half of this batch that the silence would eat.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ARealCtrlVOfAManyPageFileBringsOnlyItsFirstPageAndSaysWhatWasLeftOut()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);
        Assert.Equal(1, await editor.PagesInTheDrawingAsync());

        await editor.PutOnTheSystemClipboardAsync(TwoPages);
        await editor.ClickEmptyCanvasAsync(PlantSeed.DraggedName, PlantSeed.UntouchedName);
        await editor.PasteAsync();

        // The far-side anchor first: without it the two assertions below would be happy with a paste
        // that never happened at all.
        await editor.WaitForTheLabelInTheDrawingAsync(OnTheFirstPage);

        Assert.Equal(1, await editor.PagesInTheDrawingAsync());

        var labels = await editor.LabelsInTheDrawingAsync();
        output.WriteLine($"Il disegno porta: {string.Join(", ", labels)}");
        Assert.DoesNotContain(OnTheSecondPage, labels);

        var dialog = await editor.WaitForTheDialogOverTheCanvasAsync();
        output.WriteLine(dialog);
        Assert.Contains("2 pages", dialog);
        Assert.Contains("left out", dialog);
        await editor.CloseEveryDialogAsync();

        // And the Editor is whole afterwards: a save still writes the plant, so what came in did not
        // poison the drawing.
        var (before, after) = await editor.DragDeviceAsync(PlantSeed.DraggedName, DragX, DragY);
        Assert.True(Math.Abs(after.X - before.X) > 1,
            "Il dispositivo non si e' mosso sullo schermo: dopo l'incollaggio la tela non risponde piu' al mouse.");
        await editor.SaveAsync();
        Assert.Null(await editor.SaveErrorAsync());

        var moved = await PlantSeed.ReadGeometryAsync(client, PlantSeed.DraggedId);
        Assert.True(Math.Abs(moved.X!.Value - PlantSeed.DraggedGeometry.X!.Value) > SamePlace,
            $"L'impianto non ha preso lo spostamento (X e' rimasta {moved.X}): dopo l'incollaggio il salvataggio non scrive piu'.");
    }

    /// <summary>
    /// <b>The same Ctrl+V with THREE pages in it</b>: still one page, still only the first page's
    /// shape, and the dialog says three.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why three and not two, which is the whole reason this test exists.</b> Every other
    /// document in this file carries exactly two <c>&lt;diagram&gt;</c>, and two cannot tell a
    /// correct cut from a broken one: the bridge takes the pages away walking the collection
    /// BACKWARDS, because <c>getElementsByTagName</c> hands back a live one, and with two pages a
    /// forward loop removes the single page it has to remove and looks perfect. <b>Measured</b> on
    /// 2026-09-18 with the loop turned forwards: a real Ctrl+V of a three-page file took the drawing
    /// from one page to <b>four</b> - the defect this batch was called in to close, reopened - while
    /// the other four tests of this file stayed <b>green</b>. This test is the one that goes red
    /// there, and it failed on exactly that count.
    /// </para>
    /// <para>
    /// The three assertions are the same three as the two-page one and for the same reasons: the
    /// page count, the shape that must arrive against the two that must not, and the declaration.
    /// The count of pages in the dialog is read as well, because "only the first page came in" and
    /// "it said how many were left out" are different promises and only the second one is the half
    /// this batch owes the operator.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ARealCtrlVOfAThreePageFileBringsOnlyItsFirstPage()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);
        Assert.Equal(1, await editor.PagesInTheDrawingAsync());

        await editor.PutOnTheSystemClipboardAsync(ThreePages);
        await editor.ClickEmptyCanvasAsync(PlantSeed.DraggedName, PlantSeed.UntouchedName);
        await editor.PasteAsync();

        await editor.WaitForTheLabelInTheDrawingAsync(FirstOfThree);

        Assert.Equal(1, await editor.PagesInTheDrawingAsync());

        var labels = await editor.LabelsInTheDrawingAsync();
        output.WriteLine($"Il disegno porta: {string.Join(", ", labels)}");
        Assert.DoesNotContain(SecondOfThree, labels);
        Assert.DoesNotContain(ThirdOfThree, labels);

        var dialog = await editor.WaitForTheDialogOverTheCanvasAsync();
        output.WriteLine(dialog);
        Assert.Contains("3 pages", dialog);
        Assert.Contains("the other 2 were left out", dialog);
        await editor.CloseEveryDialogAsync();

        // E l'Editor e' intero: il salvataggio scrive ancora l'impianto.
        var (before, after) = await editor.DragDeviceAsync(PlantSeed.DraggedName, DragX, DragY);
        Assert.True(Math.Abs(after.X - before.X) > 1,
            "Il dispositivo non si e' mosso sullo schermo: dopo l'incollaggio a tre pagine la tela non risponde piu' al mouse.");
        await editor.SaveAsync();
        Assert.Null(await editor.SaveErrorAsync());

        var moved = await PlantSeed.ReadGeometryAsync(client, PlantSeed.DraggedId);
        Assert.True(Math.Abs(moved.X!.Value - PlantSeed.DraggedGeometry.X!.Value) > SamePlace,
            $"L'impianto non ha preso lo spostamento (X e' rimasta {moved.X}): dopo l'incollaggio a tre pagine il salvataggio non scrive piu'.");
    }

    /// <summary>
    /// <b>A file dropped on the canvas</b> cannot make a page either - not a many-page one, and not a
    /// one-page one dropped with Ctrl held, which was a road of its own that nobody had named. A
    /// one-page file dropped plainly goes on arriving <b>without a word</b>, because nothing was left
    /// out of it.
    /// </summary>
    /// <remarks>
    /// Measured on 2026-09-18 before the block was written: the many-page file took pages from 1 to
    /// 3, and the one-page file with Ctrl from 1 to 2 - <c>EditorUi.importFile</c> hands
    /// <c>mxEvent.isControlDown(evt)</c> to <c>importXml</c> as <c>addNewPage</c>. The third case is
    /// the net against a block that has become noisy: an Editor that warned about every dropped file
    /// would teach the operator to click the warning away without reading it.
    /// </remarks>
    [Fact]
    public async Task AFileDroppedOnTheCanvasCannotMakeAPageEither()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        Assert.Equal(1, await editor.PagesInTheDrawingAsync());

        output.WriteLine(await editor.DropAFileOnTheCanvasAsync("two-pages.drawio", TwoPages, withCtrl: false));
        await editor.WaitForTheLabelInTheDrawingAsync(OnTheFirstPage);
        Assert.Equal(1, await editor.PagesInTheDrawingAsync());
        Assert.DoesNotContain(OnTheSecondPage, await editor.LabelsInTheDrawingAsync());

        var dialog = await editor.WaitForTheDialogOverTheCanvasAsync();
        output.WriteLine(dialog);
        Assert.Contains("2 pages", dialog);
        Assert.Contains("left out", dialog);
        Assert.Equal(0, await editor.CloseEveryDialogAsync());

        // A ONE-page file dropped with Ctrl held: draw.io would have given it a page of its own.
        output.WriteLine(await editor.DropAFileOnTheCanvasAsync("one-page.drawio", OnePage, withCtrl: true));
        await editor.WaitForTheLabelInTheDrawingAsync(AloneOnItsPage);
        Assert.Equal(1, await editor.PagesInTheDrawingAsync());

        var told = await editor.WaitForTheDialogOverTheCanvasAsync();
        output.WriteLine(told);
        Assert.Contains("THIS page", told);
        Assert.Equal(0, await editor.CloseEveryDialogAsync());

        // And the ordinary case, which must go on being ordinary: a one-page file, dropped plainly,
        // arrives and NOBODY is interrupted.
        output.WriteLine(await editor.DropAFileOnTheCanvasAsync("one-page-again.drawio", SecondCopy, withCtrl: false));
        await editor.WaitForTheLabelInTheDrawingAsync(TheQuietOne);
        Assert.Equal(1, await editor.PagesInTheDrawingAsync());
        Assert.Equal("no dialog", await editor.TheDialogOverTheCanvasAsync());
    }

    /// <summary>The label of the cell that lives on page one of <see cref="TwoPages"/>: what must arrive.</summary>
    private const string OnTheFirstPage = "OnTheFirstPage";

    /// <summary>The label of the cell that lives on page two: what must NOT arrive, on any page.</summary>
    private const string OnTheSecondPage = "OnTheSecondPage";

    private const string AloneOnItsPage = "AloneOnItsPage";
    private const string TheQuietOne = "TheQuietOne";

    /// <summary>The three labels of <see cref="ThreePages"/>: one that must arrive, two that must not.</summary>
    private const string FirstOfThree = "FirstOfThree";
    private const string SecondOfThree = "SecondOfThree";
    private const string ThirdOfThree = "ThirdOfThree";

    /// <summary>
    /// The text of a <b>three</b>-page <c>.drawio</c> file. Three is the smallest number that can
    /// tell the bridge's backwards cut from a forward one - see
    /// <see cref="ARealCtrlVOfAThreePageFileBringsOnlyItsFirstPage"/> - and the three cells are
    /// named apart so that "the second one came in on page one" cannot hide inside "there is one
    /// page".
    /// </summary>
    private static readonly string ThreePages =
        "<mxfile host=\"127.0.0.1\" pages=\"3\">"
        + PageOfThree("threeA", FirstOfThree)
        + PageOfThree("threeB", SecondOfThree)
        + PageOfThree("threeC", ThirdOfThree)
        + "</mxfile>";

    private static string PageOfThree(string id, string label) =>
        $"<diagram id=\"{id}\" name=\"Page-{id}\"><mxGraphModel><root><mxCell id=\"0\"/><mxCell id=\"1\" parent=\"0\"/>"
        + $"<mxCell id=\"cell-{id}\" value=\"{label}\" style=\"rounded=0;\" vertex=\"1\" parent=\"1\">"
        + "<mxGeometry x=\"40\" y=\"40\" width=\"120\" height=\"60\" as=\"geometry\"/></mxCell>"
        + "</root></mxGraphModel></diagram>";

    /// <summary>
    /// The text of a two-page <c>.drawio</c> file, which is what lands on the clipboard when somebody
    /// opens one in an editor and copies it, and what a dropped file carries. The two cells are
    /// named apart on purpose: that is how "only the first page came in" is told from "everything
    /// came in on one page".
    /// </summary>
    private const string TwoPages =
        "<mxfile host=\"127.0.0.1\" pages=\"2\">"
        + "<diagram id=\"first\" name=\"Page-1\"><mxGraphModel><root><mxCell id=\"0\"/><mxCell id=\"1\" parent=\"0\"/>"
        + "<mxCell id=\"cell-first\" value=\"" + OnTheFirstPage + "\" style=\"rounded=0;\" vertex=\"1\" parent=\"1\">"
        + "<mxGeometry x=\"40\" y=\"40\" width=\"120\" height=\"60\" as=\"geometry\"/></mxCell>"
        + "</root></mxGraphModel></diagram>"
        + "<diagram id=\"second\" name=\"Page-2\"><mxGraphModel><root><mxCell id=\"0\"/><mxCell id=\"1\" parent=\"0\"/>"
        + "<mxCell id=\"cell-second\" value=\"" + OnTheSecondPage + "\" style=\"rounded=0;\" vertex=\"1\" parent=\"1\">"
        + "<mxGeometry x=\"40\" y=\"40\" width=\"120\" height=\"60\" as=\"geometry\"/></mxCell>"
        + "</root></mxGraphModel></diagram></mxfile>";

    private static readonly string OnePage = OnePageCarrying(AloneOnItsPage, "alone");
    private static readonly string SecondCopy = OnePageCarrying(TheQuietOne, "quiet");

    private static string OnePageCarrying(string label, string id) =>
        "<mxfile host=\"127.0.0.1\">"
        + $"<diagram id=\"{id}\" name=\"Page-1\"><mxGraphModel><root><mxCell id=\"0\"/><mxCell id=\"1\" parent=\"0\"/>"
        + $"<mxCell id=\"cell-{id}\" value=\"{label}\" style=\"rounded=0;\" vertex=\"1\" parent=\"1\">"
        + "<mxGeometry x=\"40\" y=\"40\" width=\"120\" height=\"60\" as=\"geometry\"/></mxCell>"
        + "</root></mxGraphModel></diagram></mxfile>";

    private static void Unmoved(string what, double? seeded, double? now) =>
        Assert.True(seeded.HasValue && now.HasValue && Math.Abs(seeded.Value - now.Value) <= SamePlace,
            $"{what}: seminato a {seeded:0.##}, adesso {now:0.##}. Il salvataggio ha scritto sull'impianto pur avendo rifiutato il disegno.");

    private static int CountOf(string text, string what)
    {
        var found = 0;
        for (var at = text.IndexOf(what, StringComparison.Ordinal); at >= 0; at = text.IndexOf(what, at + 1, StringComparison.Ordinal)) found++;
        return found;
    }
}
