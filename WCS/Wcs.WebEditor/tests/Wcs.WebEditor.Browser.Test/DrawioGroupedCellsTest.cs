using System.Xml.Linq;
using Wcs.Api.Contracts.Dtos;
using Xunit;
using Xunit.Abstractions;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// The gesture nobody wrote the bridge for: an operator selects two devices, presses Ctrl+G to
/// move them together, drags the group, and saves.
/// </summary>
/// <remarks>
/// <para>
/// In the mxGraph format the geometry of a cell inside a group is expressed <b>relative to the
/// group</b>, not to the canvas. <c>DrawioReader</c> used never to look at the <c>parent</c>
/// attribute of a cell, so the plant was written with numbers from another frame of reference -
/// silently. That was a claim about draw.io's behaviour, and this class exists to measure it in a
/// real browser instead of deducing it from the reader.
/// </para>
/// <para>
/// <b>MEASURED, 2026-09-16, and it is true.</b> draw.io does not flatten the coordinates: the group
/// cell comes back with the group's own box, and the children come back relative to it - the first
/// device with <c>&lt;mxGeometry width="120" height="60"/&gt;</c>, no <c>x</c> and no <c>y</c> at
/// all. Read as absolute, two devices seeded at (200, 150) and (620, 420) and dragged by +180/+110
/// pixels were written to the plant at (-6.96, -25.98) and (413.05, 244.02) - the opposite way from
/// the gesture, the first one off the plant in negative coordinates - and the report said
/// <c>Save succeeded: 2 writes</c>.
/// </para>
/// <para>
/// <b>What the remedy is, and what it is not.</b> The grouped cell is now <b>refused by name</b>
/// (see <c>DrawioDelta</c>): nothing is written and nothing is deleted, and the refusal tells the
/// operator how to get what they wanted. The geometry is deliberately NOT composed - a group can be
/// rotated, and then composing is an affine transform and not a sum - so
/// <see cref="MovingAGroupOfTwoDevicesIsRefusedAndLeavesThePlantWhereItWas"/> asserts that the plant
/// does <b>not</b> move. The other measurement, the one that ungroups first, is the way out the
/// refusal advises, and it must stay <b>green</b>: if it ever stopped working, the advice would
/// become false.
/// </para>
/// <para>
/// <b>The measurement checks its own gesture.</b> Ctrl+G could do nothing at all, and then these
/// tests would be an ordinary single-device drag wearing a group's name - green, and proving
/// nothing. So the grouping is proved twice over: the device nobody dragged has to move too, and
/// the drawing draw.io hands back has to carry a <c>parent</c> that is not the default layer.
/// </para>
/// <para>
/// <b>There is a junction in the plant</b> these two measurements work on, which the other browser
/// tests do not seed. It is not decoration: a refusal that were also a deletion is the worst defect
/// this bridge can have, and a device does not fall alone - the host takes its junctions away with
/// it (a single DELETE, see <c>PlantStore.DeleteItemAsync</c>). The junction is the witness that
/// says whether the refusal left the plant whole.
/// </para>
/// </remarks>
public sealed class DrawioGroupedCellsTest(DrawioEditorFixture fixture, ITestOutputHelper output) : IClassFixture<DrawioEditorFixture>
{
    /// <summary>Who seeds the plant: a lock holder of its own, released at once, as in the other browser tests.</summary>
    private const string SeedHolder = "browser-test-group-seed";

    /// <summary>
    /// The drag offset, in screen pixels, down and to the right. The direction is half of the
    /// assertion: the defect under measurement would send both devices back towards the origin,
    /// that is up and to the left.
    /// </summary>
    private const double DragX = 180;
    private const double DragY = 110;

    /// <summary>
    /// How far the plant must have moved at the very least for the drag to count as having
    /// happened. Deliberately low: the zoom draw.io lays the drawing out at is not ours to choose.
    /// </summary>
    private const double MinimumPlantMove = 20;

    /// <summary>
    /// How far the measured move may sit from the expected one, in plant units. draw.io snaps a
    /// drag to its own grid (<c>gridSize="10"</c> in the document we build), so a move can land up
    /// to about ten units off the pixels the mouse travelled.
    /// </summary>
    private const double SnapRoom = 20;

    /// <summary>
    /// How far apart two numbers may be and still count as the same. The group translates both
    /// devices rigidly, so their deltas are the same number down to the two decimals the drawing
    /// format carries.
    /// </summary>
    private const double SameUnit = 0.05;

    /// <summary>The words the refusal must carry: the way out it advises is the whole point of it.</summary>
    private const string TheWayOut = "Ctrl+Shift+U";

    private void DeclareBinary() => output.WriteLine(fixture.Host.Declaration);

    /// <summary>
    /// Two devices grouped and moved together. The plant must be left <b>exactly where it was</b>,
    /// and both devices must be named in a refusal that says how to get the move through.
    /// </summary>
    /// <remarks>
    /// This measurement used to be the red one, skipped while the owner chose the remedy: it
    /// asserted that the move arrived on the plant, and it failed on the defect it was written to
    /// find. The remedy chosen was to refuse, not to compose, so the assertion is turned around: the
    /// gesture must write <b>nothing</b>, and must not stay quiet about it. Everything up to the
    /// save is unchanged, including the two proofs that Ctrl+G really grouped.
    /// </remarks>
    [Fact]
    public Task MovingAGroupOfTwoDevicesIsRefusedAndLeavesThePlantWhereItWas() => MeasureAsync(ungroupBeforeSaving: false);

    /// <summary>
    /// The same gesture, undone before saving: the group is dissolved with Ctrl+Shift+U. It is a
    /// separate measurement because it behaves differently - and measured, it is <b>green</b>:
    /// dissolving the group puts the cells back on the default layer with absolute geometry, and
    /// the plant gets the move the operator made. It is the way out the refusal of
    /// <see cref="MovingAGroupOfTwoDevicesIsRefusedAndLeavesThePlantWhereItWas"/> advises, so it is
    /// not a nicety that it stays green: the day it stops, the advice we give the operator becomes
    /// false.
    /// </summary>
    [Fact]
    public Task MovingAGroupAndThenUngroupingItWritesBothNewPositions() => MeasureAsync(ungroupBeforeSaving: true);

    private async Task MeasureAsync(bool ungroupBeforeSaving)
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        var seededJunction = await PlantSeed.ResetAsync(client, SeedHolder, withJunction: true);
        Assert.NotNull(seededJunction);

        await using (var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host))
        {
            await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
            await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);
            await editor.RecordReturnedDrawingAsync();

            var firstOnScreen = await editor.DeviceBoxAsync(PlantSeed.DraggedName);
            var secondOnScreen = await editor.DeviceBoxAsync(PlantSeed.UntouchedName);

            // The zoom is measured, not assumed: draw.io lays the drawing out at whatever scale it
            // likes, and without the scale there is no expected number to report - only "it moved".
            // The two seeded devices are a ruler of known length: 408.58 plant units between centres.
            var plantSpan = CentreX(PlantSeed.UntouchedGeometry) - CentreX(PlantSeed.DraggedGeometry);
            var screenSpan = (secondOnScreen.X + secondOnScreen.Width / 2) - (firstOnScreen.X + firstOnScreen.Width / 2);
            var scale = screenSpan / plantSpan;
            output.WriteLine($"Zoom misurato: {screenSpan:0.##} pixel fra i centri per {plantSpan:0.##} unita' d'impianto = {scale:0.####}.");

            await editor.SelectDevicesAsync(PlantSeed.DraggedName, PlantSeed.UntouchedName);
            await editor.GroupSelectionAsync();

            var (before, after) = await editor.DragDeviceAsync(PlantSeed.DraggedName, DragX, DragY);
            Assert.True(after.X > before.X && after.Y > before.Y,
                $"Il trascinamento non ha mosso la sagoma sulla tela: era a ({before.X:0.#}, {before.Y:0.#}), " +
                $"e' a ({after.X:0.#}, {after.Y:0.#}). Il gesto non e' arrivato a draw.io.");

            // The first proof that Ctrl+G did something: the device nobody touched moved with it. If it
            // stayed put, this is a plain single-device drag and the measurement is about nothing.
            var secondAfter = await editor.DeviceBoxAsync(PlantSeed.UntouchedName);
            Assert.True(secondAfter.X > secondOnScreen.X + 1 && secondAfter.Y > secondOnScreen.Y + 1,
                $"Il dispositivo \"{PlantSeed.UntouchedName}\" non si e' mosso con il gruppo: era a " +
                $"({secondOnScreen.X:0.#}, {secondOnScreen.Y:0.#}), e' a ({secondAfter.X:0.#}, {secondAfter.Y:0.#}). " +
                "Ctrl+G non ha raggruppato, quindi questa misura non sta guardando un gruppo.");

            if (ungroupBeforeSaving) await editor.UngroupSelectionAsync();

            await editor.SaveAsync();

            var drawing = await editor.LastReturnedDrawingAsync();
            output.WriteLine($"XML della cella \"{PlantSeed.DraggedName}\" come draw.io la restituisce:{Environment.NewLine}{CellXml(drawing, PlantSeed.DraggedName)}");
            output.WriteLine($"XML della cella \"{PlantSeed.UntouchedName}\":{Environment.NewLine}{CellXml(drawing, PlantSeed.UntouchedName)}");
            output.WriteLine($"XML della giunzione seminata:{Environment.NewLine}{JunctionXml(drawing, seededJunction.JunctionId)}");
            output.WriteLine($"Celle non marchiate nel disegno di ritorno (il gruppo, se c'e'):{Environment.NewLine}{UnmarkedCells(drawing)}");

            // La domanda 2 del brief, misurata invece che dedotta: raggruppando i due dispositivi,
            // draw.io porta dentro il gruppo anche la freccia che li collega? Qui si LEGGE, e il
            // numero finisce nel verbale della prova.
            output.WriteLine($"parent della freccia della giunzione: \"{JunctionParentOf(drawing, seededJunction.JunctionId) ?? "(nessuno)"}\".");

            // The second proof, and the one that names the mechanism. Grouped, the cells must come back
            // under a parent of their own - otherwise there was no group in the drawing at all, and the
            // measurement is looking at something else. Ungrouped, they must come back on the default
            // layer: that IS what dissolving a group means, and it is the premise this green
            // measurement exists to watch.
            var parent = ParentOf(drawing, PlantSeed.DraggedName);
            if (ungroupBeforeSaving)
                Assert.True(parent == "1",
                    $"Sciolto il gruppo, la cella \"{PlantSeed.DraggedName}\" torna con parent=\"{parent ?? "(nessuno)"}\" " +
                    "invece che sul livello predefinito: Ctrl+Shift+U non ha sciolto niente, oppure draw.io ha cambiato " +
                    "il modo in cui scioglie un gruppo - e allora questa geometria non e' piu' assoluta.");
            else
                Assert.True(parent is not null && parent != "1",
                    $"La cella \"{PlantSeed.DraggedName}\" torna con parent=\"{parent ?? "(nessuno)"}\": nel disegno " +
                    "non c'e' nessun gruppo, quindi questa misura non sta guardando quello che crede.");

            var error = await editor.SaveErrorAsync();
            Assert.True(error is null, $"Il salvataggio ha dichiarato un errore: {error}{Environment.NewLine}" +
                $"Verbale intero:{Environment.NewLine}{await editor.ReportAsync()}");
            output.WriteLine($"Verbale del salvataggio:{Environment.NewLine}{await editor.ReportAsync()}");

            var graph = await client.GetPlantGraphAsync();
            var movedFirst = PlantSeed.GeometryOf(graph, PlantSeed.DraggedId);
            var movedSecond = PlantSeed.GeometryOf(graph, PlantSeed.UntouchedId);
            Report(PlantSeed.DraggedName, PlantSeed.DraggedGeometry, movedFirst);
            Report(PlantSeed.UntouchedName, PlantSeed.UntouchedGeometry, movedSecond);

            // La difesa di ultima istanza, provata da tutte e due le parti del ramo: un dispositivo
            // rifiutato non e' un dispositivo cancellato, e con lui non se ne va la sua giunzione.
            // GeometryOf qui sopra solleva gia' se un dispositivo e' sparito; la giunzione la si
            // chiede esplicitamente, perche' e' quella che sparirebbe in silenzio dentro la
            // cancellazione di un dispositivo.
            Assert.True(graph.Junctions.Any(j => j.JunctionId == seededJunction.JunctionId),
                $"La giunzione {seededJunction.JunctionId} non e' piu' sull'impianto: il salvataggio si e' portato via " +
                "qualcosa che nessuno aveva cancellato - e una giunzione sparisce dentro la cancellazione del " +
                $"dispositivo che tocca. Giunzioni rimaste: {graph.Junctions.Count}.");

            if (ungroupBeforeSaving)
                await AssertTheMoveArrivedAsync(editor, scale, movedFirst, movedSecond);
            else
                await AssertTheGestureWasRefusedAsync(editor, movedFirst, movedSecond);
        }

        if (ungroupBeforeSaving) return;

        // La domanda 1 del brief, misurata: il gruppo e' una cella NON marchiata, quindi scende
        // nella decorazione e il salvataggio la scrive sull'impianto. Che cosa accade al
        // salvataggio dopo? Si riapre la pagina - e' li' che la decorazione torna sulla tela,
        // portandosi dietro il gruppo ormai senza figli - e si salva senza toccare niente.
        await MeasureTheSaveAfterTheGroupWentIntoTheDecorationAsync(client, seededJunction.JunctionId);
    }

    /// <summary>
    /// Il gesto rifiutato: sull'impianto non si muove <b>niente</b>, e tutti e due i dispositivi
    /// escono nominati in un rifiuto che dice come ottenere lo spostamento.
    /// </summary>
    private async Task AssertTheGestureWasRefusedAsync(
        DrawioEditorPage editor, PlantItemGeometryDto movedFirst, PlantItemGeometryDto movedSecond)
    {
        AssertStayedPut(PlantSeed.DraggedName, PlantSeed.DraggedGeometry, movedFirst);
        AssertStayedPut(PlantSeed.UntouchedName, PlantSeed.UntouchedGeometry, movedSecond);

        var refusals = await editor.RefusalsAsync();
        AssertRefused(refusals, PlantSeed.DraggedId, PlantSeed.DraggedName);
        AssertRefused(refusals, PlantSeed.UntouchedId, PlantSeed.UntouchedName);

        // E il verbale non deve dire il contrario di cio' che e' accaduto: era esattamente questo -
        // "Written: the move of 2 devices" su un impianto scritto male - il difetto misurato.
        var written = await editor.WrittenAsync();
        Assert.DoesNotContain(written, line => line.Contains("move", StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertRefused(IReadOnlyList<string> refusals, Guid id, string name)
    {
        var line = refusals.FirstOrDefault(r => r.Contains(id.ToString(), StringComparison.OrdinalIgnoreCase));
        Assert.True(line is not null,
            $"Nessun rifiuto nomina il dispositivo \"{name}\" ({id}): il suo spostamento non e' stato scritto - " +
            "e non doveva esserlo - ma nessuno lo ha detto, che e' il difetto D203 nella sua forma esatta." +
            $"{Environment.NewLine}Rifiuti presenti:{Environment.NewLine}{string.Join(Environment.NewLine, refusals)}");
        Assert.True(line!.Contains(TheWayOut, StringComparison.OrdinalIgnoreCase),
            $"Il rifiuto di \"{name}\" non dice come ottenere lo spostamento (manca \"{TheWayOut}\"): un rifiuto che " +
            $"non porta la via d'uscita lascia l'operatore fermo.{Environment.NewLine}{line}");
    }

    /// <summary>
    /// Il gesto scritto: la via d'uscita che il rifiuto consiglia deve funzionare davvero.
    /// </summary>
    private async Task AssertTheMoveArrivedAsync(
        DrawioEditorPage editor, double scale, PlantItemGeometryDto movedFirst, PlantItemGeometryDto movedSecond)
    {
        var expectedX = DragX / scale;
        var expectedY = DragY / scale;
        output.WriteLine(
            $"Spostamento atteso sull'impianto: {DragX:0} x {DragY:0} pixel a zoom {scale:0.####} = " +
            $"{expectedX:0.##} x {expectedY:0.##} unita'.");

        AssertMoved(PlantSeed.DraggedName, PlantSeed.DraggedGeometry, movedFirst, expectedX, expectedY);
        AssertMoved(PlantSeed.UntouchedName, PlantSeed.UntouchedGeometry, movedSecond, expectedX, expectedY);

        // A group translates rigidly: whatever the zoom turned out to be, the two devices moved by
        // the same amount. This is the assertion that survives a wrong guess about the zoom.
        AssertSame("lo spostamento in X dei due dispositivi del gruppo",
            Delta(PlantSeed.DraggedGeometry.X, movedFirst.X), Delta(PlantSeed.UntouchedGeometry.X, movedSecond.X));
        AssertSame("lo spostamento in Y dei due dispositivi del gruppo",
            Delta(PlantSeed.DraggedGeometry.Y, movedFirst.Y), Delta(PlantSeed.UntouchedGeometry.Y, movedSecond.Y));

        // Nessuno dei due dispositivi esce rifiutato: sciolto il gruppo, il gesto passa intero.
        // Non si pretende un elenco vuoto - una voce sulla freccia della giunzione sarebbe un altro
        // fatto, e questa prova non e' il posto dove sorvegliarlo - si pretende che i due
        // dispositivi non ci siano.
        var refusals = await editor.RefusalsAsync();
        Assert.DoesNotContain(refusals, r => r.Contains(PlantSeed.DraggedId.ToString(), StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(refusals, r => r.Contains(PlantSeed.UntouchedId.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Il salvataggio DOPO quello rifiutato, a pagina riaperta: la decorazione dell'impianto porta
    /// adesso la cella del gruppo, che il primo salvataggio ci ha scritto dentro e che il documento
    /// di andata reinnesta tale e quale - ormai senza figli, perche' i dispositivi
    /// <c>DrawioDocument.Build</c> li riscrive sul livello predefinito.
    /// </summary>
    /// <remarks>
    /// E' la domanda a cui bisognava rispondere misurando: se quel gruppo orfano facesse ricadere le
    /// celle dentro di se', il secondo salvataggio rifiuterebbe di nuovo e il dispositivo
    /// resterebbe bloccato per sempre - e allora il rifiuto non basterebbe, il gruppo andrebbe tenuto
    /// fuori anche dalla decorazione. Qui non si tocca niente e si salva: l'impianto deve restare
    /// dov'e', senza errori e senza nessun rifiuto sui due dispositivi.
    /// </remarks>
    private async Task MeasureTheSaveAfterTheGroupWentIntoTheDecorationAsync(Wcs.Api.Client.WcsClient client, Guid junctionId)
    {
        var decoration = await client.GetPlantDecorationAsync();
        output.WriteLine($"Decorazione scritta sull'impianto dal salvataggio rifiutato:{Environment.NewLine}{decoration}");

        // La pagina di prima rilascia il permesso quando il suo circuito si smonta, non quando la
        // scheda si chiude: senza questa attesa il salvataggio qui sotto direbbe "sola lettura".
        await PlantSeed.WaitForTheEditPermitToBeFreeAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);
        await editor.RecordReturnedDrawingAsync();
        await editor.SaveAsync();

        var drawing = await editor.LastReturnedDrawingAsync();
        output.WriteLine($"Secondo salvataggio - parent della cella \"{PlantSeed.DraggedName}\": " +
            $"\"{ParentOf(drawing, PlantSeed.DraggedName) ?? "(nessuno)"}\".");
        output.WriteLine($"Secondo salvataggio - verbale:{Environment.NewLine}{await editor.ReportAsync()}");

        var error = await editor.SaveErrorAsync();
        Assert.True(error is null, $"Il secondo salvataggio ha dichiarato un errore: {error}{Environment.NewLine}" +
            $"Verbale intero:{Environment.NewLine}{await editor.ReportAsync()}");

        var refusals = await editor.RefusalsAsync();
        Assert.DoesNotContain(refusals, r => r.Contains(PlantSeed.DraggedId.ToString(), StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(refusals, r => r.Contains(PlantSeed.UntouchedId.ToString(), StringComparison.OrdinalIgnoreCase));

        var graph = await client.GetPlantGraphAsync();
        AssertStayedPut(PlantSeed.DraggedName, PlantSeed.DraggedGeometry, PlantSeed.GeometryOf(graph, PlantSeed.DraggedId));
        AssertStayedPut(PlantSeed.UntouchedName, PlantSeed.UntouchedGeometry, PlantSeed.GeometryOf(graph, PlantSeed.UntouchedId));
        Assert.True(graph.Junctions.Any(j => j.JunctionId == junctionId),
            $"La giunzione {junctionId} non e' piu' sull'impianto dopo il secondo salvataggio.");
    }

    private void Report(string what, PlantItemGeometryDto seeded, PlantItemGeometryDto now) => output.WriteLine(
        $"{what}: prima ({Show(seeded.X)}, {Show(seeded.Y)}) {Show(seeded.Width)}x{Show(seeded.Height)} a {Show(seeded.Rotation)} gradi; " +
        $"dopo ({Show(now.X)}, {Show(now.Y)}) {Show(now.Width)}x{Show(now.Height)} a {Show(now.Rotation)} gradi; " +
        $"scarto ({Delta(seeded.X, now.X):0.##}, {Delta(seeded.Y, now.Y):0.##}).");

    /// <summary>
    /// Il dispositivo non si e' mosso di niente: <b>tutti e cinque</b> i numeri che l'impianto
    /// memorizza. Un rifiuto che lasciasse passare una deformazione sarebbe un rifiuto a meta'.
    /// </summary>
    private static void AssertStayedPut(string what, PlantItemGeometryDto seeded, PlantItemGeometryDto now)
    {
        AssertSame($"la X di \"{what}\"", seeded.X, now.X);
        AssertSame($"la Y di \"{what}\"", seeded.Y, now.Y);
        AssertSame($"la larghezza di \"{what}\"", seeded.Width, now.Width);
        AssertSame($"l'altezza di \"{what}\"", seeded.Height, now.Height);
        AssertSame($"la rotazione di \"{what}\"", seeded.Rotation, now.Rotation);
    }

    private static void AssertMoved(string what, PlantItemGeometryDto seeded, PlantItemGeometryDto now, double expectedX, double expectedY)
    {
        var dx = Delta(seeded.X, now.X);
        var dy = Delta(seeded.Y, now.Y);

        Assert.True(dx >= MinimumPlantMove,
            $"La X di \"{what}\" e' {Show(now.X)}: era {Show(seeded.X)}, e il gruppo e' stato trascinato verso destra. " +
            $"Lo scarto e' {dx:0.##} unita'.");
        Assert.True(dy >= MinimumPlantMove,
            $"La Y di \"{what}\" e' {Show(now.Y)}: era {Show(seeded.Y)}, e il gruppo e' stato trascinato verso il basso. " +
            $"Lo scarto e' {dy:0.##} unita'.");

        Assert.True(Math.Abs(dx - expectedX) <= SnapRoom,
            $"La X di \"{what}\" si e' spostata di {dx:0.##} unita' invece di {expectedX:0.##}: e' piu' di quanto " +
            $"l'aggancio alla griglia di draw.io possa spiegare ({SnapRoom:0} unita').");
        Assert.True(Math.Abs(dy - expectedY) <= SnapRoom,
            $"La Y di \"{what}\" si e' spostata di {dy:0.##} unita' invece di {expectedY:0.##}: e' piu' di quanto " +
            $"l'aggancio alla griglia di draw.io possa spiegare ({SnapRoom:0} unita').");

        // The trap this sub-project has paid for four times: a move that carries a deformation with
        // it. The plant's box is the envelope of the already-rotated shape.
        AssertSame($"la larghezza di \"{what}\"", seeded.Width, now.Width);
        AssertSame($"l'altezza di \"{what}\"", seeded.Height, now.Height);
        AssertSame($"la rotazione di \"{what}\"", seeded.Rotation, now.Rotation);
    }

    private static void AssertSame(string what, double? expected, double? actual)
    {
        Assert.True(actual.HasValue, $"{what}: non c'e' piu' nessun valore. Doveva essere {Show(expected)}.");
        Assert.True(expected.HasValue, $"{what}: la prova non sa che cosa aspettarsi (valore di partenza nullo).");
        Assert.True(Math.Abs(expected!.Value - actual!.Value) <= SameUnit,
            $"{what}: {Show(actual)} invece di {Show(expected)}.");
    }

    private static void AssertSame(string what, double expected, double actual) =>
        Assert.True(Math.Abs(expected - actual) <= SameUnit, $"{what}: {actual:0.##} da una parte e {expected:0.##} dall'altra.");

    /// <summary>The XML of the marked cell carrying this label, exactly as draw.io handed it back.</summary>
    private static string CellXml(string drawing, string label) =>
        MarkedCell(drawing, label)?.ToString() ?? $"(nessuna cella con etichetta \"{label}\" nel disegno di ritorno)";

    /// <summary>The XML of the marked cell carrying this identifier - the arrow of a junction, here.</summary>
    private static string JunctionXml(string drawing, Guid id) =>
        CellWithId(drawing, id)?.ToString() ?? $"(nessuna cella con id \"{id}\" nel disegno di ritorno)";

    /// <summary>The <c>parent</c> of the <c>mxCell</c> inside the marked cell carrying this label.</summary>
    private static string? ParentOf(string drawing, string label) =>
        (string?)MarkedCell(drawing, label)?.Element("mxCell")?.Attribute("parent");

    private static string? JunctionParentOf(string drawing, Guid id) =>
        (string?)CellWithId(drawing, id)?.Element("mxCell")?.Attribute("parent");

    private static XElement? MarkedCell(string drawing, string label) =>
        Root(drawing)?.Elements("object").FirstOrDefault(o => (string?)o.Attribute("label") == label);

    private static XElement? CellWithId(string drawing, Guid id) =>
        Root(drawing)?.Elements("object").FirstOrDefault(o => (string?)o.Attribute("id") == id.ToString());

    /// <summary>
    /// Everything in the returned drawing that is not one of ours: the group cell, if draw.io made
    /// one, lives here - and so does whatever else the reader would treat as decoration.
    /// </summary>
    private static string UnmarkedCells(string drawing)
    {
        var root = Root(drawing);
        if (root is null) return "(nessun <root> nel disegno di ritorno)";
        var cells = root.Elements()
            .Where(e => e.Name != "object")
            .Where(e => (string?)e.Attribute("id") is not ("0" or "1"))
            .Select(e => e.ToString())
            .ToList();
        return cells.Count == 0 ? "(nessuna)" : string.Join(Environment.NewLine, cells);
    }

    private static XElement? Root(string drawing)
    {
        try
        {
            return XDocument.Parse(drawing).Descendants("root").FirstOrDefault();
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    private static double CentreX(PlantItemGeometryDto geometry) => (geometry.X ?? 0) + (geometry.Width ?? 0) / 2;

    private static double Delta(double? from, double? to) => (to ?? 0) - (from ?? 0);

    private static string Show(double? value) => value?.ToString("0.##") ?? "(nessuno)";
}
