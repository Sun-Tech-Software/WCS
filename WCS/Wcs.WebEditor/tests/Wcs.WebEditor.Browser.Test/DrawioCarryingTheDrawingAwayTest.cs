using Xunit;
using Xunit.Abstractions;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// <b>The road the page tells the operator to take when the drawing is about to be lost - walked.</b>
/// Two messages of <c>DrawioEditor</c> say how to carry the free drawing away before a reload
/// destroys it, and two refusals of <c>DrawioDelta</c> say how to take a mirroring out of a cell's
/// style. Both remedies are gestures, and a gesture is measured in a browser or it is not measured.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> Review finding 3 was the FOURTH time on this branch that a message
/// written for the operator named a place that is not there: the two messages sent them to
/// "Extras &gt; Edit Diagram", and this embedded draw.io has no menu bar at all - the word "Extras"
/// is nowhere in its document. It was the most expensive of the four, because those two messages
/// are the only point at which this page can make somebody lose work. The other three times, the
/// remedy proposed in its place was wrong as well.
/// </para>
/// <para>
/// <b>So the road is walked rather than looked up.</b> Asserting that an action object exists, or
/// that it declares a shortcut, is the same kind of claim that produced the defect: what is
/// measured here is a click, two keystrokes and what really landed on the system clipboard.
/// </para>
/// </remarks>
public sealed class DrawioCarryingTheDrawingAwayTest(DrawioEditorFixture fixture, ITestOutputHelper output)
    : IClassFixture<DrawioEditorFixture>
{
    /// <summary>Who seeds the plant: a lock holder of its own, released at once, as in the other browser tests.</summary>
    private const string SeedHolder = "browser-test-carry-away-seed";

    /// <summary>
    /// The note's text. It is deliberately unlike anything the page or the plant writes: a marker
    /// that could be on the clipboard for no other reason than that this gesture put it there.
    /// </summary>
    private const string NoteText = "PROMEMORIA DELL'OPERATORE 19/9";

    private void DeclareBinary() => output.WriteLine(fixture.Host.Declaration);

    private async Task ArmClipboardProbeAsync(DrawioEditorPage editor)
    {
        try
        {
            await editor.ArmThePasteProbeAsync();
        }
        catch (Exception ex)
        {
            // Anche l'installazione della diagnostica e' osservazione: non decide l'esito del gesto.
            output.WriteLine($"La sonda degli appunti non si e' lasciata armare: {ex}");
        }
    }

    private async Task WriteClipboardProbeAsync(DrawioEditorPage editor)
    {
        try
        {
            output.WriteLine(await editor.ThePasteProbeAsync());
        }
        catch (Exception ex)
        {
            // La diagnostica non deve mai sostituire l'errore che stava cercando di raccontare.
            output.WriteLine($"La sonda degli appunti non si e' lasciata leggere: {ex}");
        }
    }

    /// <summary>
    /// <b>Review finding 3, the positive half.</b> The free drawing really can be carried out of
    /// draw.io with Ctrl+A and Ctrl+C: a note the plant knows nothing about ends up on the system
    /// clipboard, inside the XML another draw.io takes back with Ctrl+V.
    /// </summary>
    /// <remarks>
    /// The note is the point. Everything else on the canvas is on the plant already and would come
    /// back at the next opening; what the two messages are about is the drawing that is written
    /// nowhere, and a clipboard carrying only the devices would be a road that does not lead where
    /// the message says.
    /// </remarks>
    [Fact]
    public async Task TheFreeDrawingReallyGoesToTheClipboardWithCtrlAAndCtrlC()
    {
        for (var iteration = 1; iteration <= 20; iteration++)
            await CarryDrawingAwayOnceAsync(iteration);
    }

    private async Task CarryDrawingAwayOnceAsync(int iteration)
    {
        output.WriteLine($"Carry-away iteration {iteration}/20");
        DeclareBinary();
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);

        var note = await editor.PutAFreeNoteOnTheCanvasAsync(NoteText);
        output.WriteLine($"Nota libera sulla tela: cella {note}.");

        // Una caduta isolata di questa prova non si e' ripresentata: la sonda ascolta soltanto e,
        // se torna, separa un tasto perso da un evento copy vuoto e da un clipboard sovrascritto.
        await ArmClipboardProbeAsync(editor);

        (int selected, string clipboard) result;
        try
        {
            const int expected = 3;
            result = await editor.SelectEverythingAndCopyAsync(expected);
        }
        catch
        {
            await WriteClipboardProbeAsync(editor);
            throw;
        }

        var (selected, clipboard) = result;
        output.WriteLine($"Ctrl+A ha selezionato {selected} celle; gli appunti portano {clipboard.Length} caratteri.");
        output.WriteLine(
            $"Contenuto riconoscibile: XML={clipboard.Contains("<mxGraphModel>", StringComparison.Ordinal)}, " +
            $"nota={clipboard.Contains(NoteText, StringComparison.Ordinal)}, " +
            $"dispositivo={clipboard.Contains(PlantSeed.DraggedName, StringComparison.Ordinal)}. " +
            $"Inizio: {clipboard[..Math.Min(clipboard.Length, 300)]}");
        await WriteClipboardProbeAsync(editor);

        // Ctrl+A really took everything: the two seeded devices and the note. Without this the
        // assertion below could be green on a selection of one cell that happened to be the note.
        Assert.Equal(3, selected);

        Assert.Contains("<mxGraphModel>", clipboard, StringComparison.Ordinal);
        Assert.Contains(NoteText, clipboard, StringComparison.Ordinal);
        Assert.Contains(PlantSeed.DraggedName, clipboard, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClipboardReplacementWaitFailsWhenTheMarkerIsUnchanged()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await ArmClipboardProbeAsync(editor);
        const string marker = "unchanged-marker";
        await editor.PutOnTheSystemClipboardAsync(marker);
        Assert.Equal(marker, await editor.ReadSystemClipboardTextForTestAsync());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            editor.WaitForClipboardReplacementForTestAsync(marker, 250));

        Assert.Contains(marker, error.Message);
        Assert.Contains("paste probe", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <b>Review finding 3, the negative half</b>, and it is not a formality: it is the guard that
    /// makes a message naming "Extras" go red instead of shipping. The menu bar is not there, the
    /// word is nowhere in draw.io's document, and "Edit Diagram" declares no shortcut - so it can be
    /// reached by no gesture at all. Ctrl+A, Ctrl+C and Ctrl+E, which the messages do name, declare
    /// theirs.
    /// </summary>
    [Fact]
    public async Task ExtrasEditDiagramIsReachableByNoGestureAndTheRoadsThatAreDeclareTheirShortcut()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);

        var roads = await editor.TheRoadsOutOfTheDrawingAsync();
        foreach (var road in roads) output.WriteLine(road);

        // Eight facts are asked for: fewer lines than that means one of them stopped being asked,
        // not that it stopped being true.
        Assert.Equal(8, roads.Length);

        Assert.Contains("menubar=null", roads);
        // Review finding 5: row 39 of the parity table said "draw.io's format panel does the same
        // thing". With ui=min that panel is never built at all, and a table that promises it would
        // have the beta's reader looking for a column that is not on the screen.
        Assert.Contains("format=null", roads);
        Assert.Contains("the word Extras in the document=false", roads);
        Assert.Contains("editDiagram=(none)", roads);
        Assert.Contains("export=(none)", roads);

        Assert.Contains("selectAll=Ctrl+A", roads);
        Assert.Contains("copy=Ctrl+C", roads);
        Assert.Contains("editStyle=Ctrl+E", roads);
    }

    /// <summary>
    /// <b>Ctrl+E as a GESTURE</b>, which is the fourth hole the review left named: the two mirror
    /// refusals of <c>DrawioDelta</c> tell the operator to select the cell and press Ctrl+E to take
    /// <c>flipH</c> out of its style, and until now that remedy was asserted only as a string inside
    /// a message - the same shape of assertion that review finding 3 has just disproved.
    /// </summary>
    /// <remarks>
    /// The dialog is not merely open: it carries <b>this</b> cell's style, read back out of the
    /// model. A dialog with somebody else's style in it would be a remedy that edits the wrong cell,
    /// and "a dialog appeared" would have been green for it.
    /// </remarks>
    [Fact]
    public async Task CtrlEOpensEditStyleOnTheSelectedCellWithItsOwnStyleInIt()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);

        // The click comes FIRST and lands on nothing: it is what puts the keyboard focus inside the
        // iframe, and clicking after the selection would take the selection away again.
        await editor.ClickEmptyCanvasAsync(PlantSeed.DraggedName, PlantSeed.UntouchedName);

        var cellId = PlantSeed.DraggedId.ToString();
        Assert.Equal(1, await editor.SelectCellAsync(cellId));

        await editor.PressEditStyleShortcutAsync();

        var shown = await editor.TheTextInTheDialogAsync();
        var held = await editor.CellStyleAsync(cellId);
        output.WriteLine($"Riquadro aperto da Ctrl+E: \"{await editor.TheDialogOverTheCanvasAsync()}\".");
        output.WriteLine($"Stile mostrato ({shown.Length} caratteri) contro stile nel modello ({held.Length}).");

        Assert.Contains("Edit Style", await editor.TheDialogOverTheCanvasAsync(), StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(held), "La cella non porta nessuno stile: la prova non misurerebbe niente.");
        Assert.Equal(held, shown);

        await editor.CloseEveryDialogAsync();
    }
}
