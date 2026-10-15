using Wcs.Api.Contracts.Dtos;
using Xunit;
using Xunit.Abstractions;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// <b>Half the trade of a graph editor, measured with the mouse</b>: the operator pulls an arrow
/// from one device to another and the plant ends up with a junction - read back from the HOST, not
/// from the canvas.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this was a hole in the net.</b> Every other test of a junction being born - the fourteen
/// in <c>DrawioDeltaTest</c> and <see cref="DrawioJunctionToADroppedDeviceTest"/> - hands the
/// drawing an edge that already carries <c>wcs="junction"</c>, because it is built either as XML we
/// wrote or through <see cref="DrawioEditorPage.DrawAnArrowAsync"/>, which calls
/// <c>graph.insertEdge</c> with the mark already in. That mark is written by OUR document and by
/// nobody else: none of those tests can say what the cell draw.io itself makes out of a drag of the
/// mouse looks like. This one makes draw.io build the cell.
/// </para>
/// <para>
/// <b>The gesture is checked before the plant is.</b> The arrow is read back out of draw.io's model
/// first - one new edge, hanging from the two devices - so that a drag which did not take is
/// reported as a drag that did not take, and not as a plant that did not change. Two different
/// faults, repaired in two different places.
/// </para>
/// <para>
/// <b>The devices are not rotated, and the seeded pair is.</b> The press has to land on the shape's
/// own border, and for a rotated shape what the browser hands back as a box is the envelope of the
/// rotation, whose right-hand edge is nowhere near the shape. The two devices this test seeds are
/// therefore upright: it is not a friendlier case, it is the only one in which "press on the edge"
/// means what it says.
/// </para>
/// </remarks>
public sealed class DrawioArrowByHandTest(DrawioEditorFixture fixture, ITestOutputHelper output)
    : IClassFixture<DrawioEditorFixture>
{
    /// <summary>Who seeds the plant: a lock holder of its own, released at once, as in the other browser tests.</summary>
    private const string SeedHolder = "browser-test-arrow-by-hand-seed";

    private static readonly Guid SourceId = new("a1b2c3d4-0000-4000-8000-000000000101");
    private static readonly Guid TargetId = new("a1b2c3d4-0000-4000-8000-000000000102");

    private const string SourceName = "HandArrowSource";
    private const string TargetName = "HandArrowTarget";

    /// <summary>
    /// Upright, of the same size, and side by side with a wide gap: the gap is what makes the drag
    /// a drag - with the shapes touching, the pointer would be over the target from the first pixel
    /// and the gesture would prove nothing about travelling there.
    /// </summary>
    private static readonly PlantItemGeometryDto SourceGeometry =
        new(SourceId, X: 160, Y: 700, Width: 120, Height: 60, Rotation: 0);

    private static readonly PlantItemGeometryDto TargetGeometry =
        new(TargetId, X: 560, Y: 700, Width: 120, Height: 60, Rotation: 0);

    private void DeclareBinary() => output.WriteLine(fixture.Host.Declaration);

    /// <summary>
    /// The gesture with the arrow hooked to a port at both ends: press on the source's right-hand
    /// connection point, let go on the target's left-hand one. It is the one the drawing's own
    /// junctions are drawn with, and the one the save's rule about ports is written for.
    /// </summary>
    [Fact]
    public async Task AnArrowPulledByHandOntoAPortBecomesAJunctionOnThePlant()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await SeedTheUprightPairAsync(client);
        // Without this line the assertion at the bottom could be reading a junction that was
        // already there before anybody drew anything.
        Assert.Empty(await client.GetJunctionsAsync());

        await using (var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host))
        {
            await editor.WaitForDeviceAsync(SourceName);
            await editor.WaitForDeviceAsync(TargetName);

            var pulled = await editor.DragAnArrowByHandAsync(SourceName, TargetName, DrawioArrowLanding.OnThePort);
            var arrow = TheOneArrowOf(pulled, SourceId.ToString(), TargetId.ToString());

            // LA PUNTA C'E' SEMPRE? La cella che il gesto fabbrica non scrive endArrow affatto, e
            // da sola quella stringa non risponde: la punta viene dallo stile predefinito degli
            // archi. Qui si chiede a draw.io lo stile RISOLTO, quello con cui dipinge - se un
            // giorno il tema predefinito smettesse di mettercela, ogni freccia tirata a mano
            // diventerebbe una linea senza punta e questa riga lo direbbe, invece di lasciare che
            // il salvataggio dichiari "nessuna giunzione" senza che nessuno capisca perche'.
            var heads = await editor.HeadsDrawioResolvesAsync(arrow.Id);
            output.WriteLine($"Punte risolte da draw.io: endArrow={heads[0]}, startArrow={heads[1]}.");
            Assert.True(heads[0] is not ("(absent)" or "none"),
                $"La freccia tirata con il mouse non ha nessuna punta in fondo (endArrow={heads[0]}): " +
                "il gesto non produce piu' un collegamento, e la regola della punta non ha niente da leggere.");
            Assert.True(heads[1] is "(absent)" or "none",
                $"La freccia tirata con il mouse nasce con una punta anche all'INIZIO (startArrow={heads[1]}): " +
                "per la regola della punta sarebbe una linea a due teste, e non diventerebbe una giunzione.");

            await editor.SaveAsync();
            Assert.Null(await editor.SaveErrorAsync());
            output.WriteLine($"Verbale del salvataggio:{Environment.NewLine}{await editor.ReportAsync()}");

            var refusals = await editor.RefusalsAsync();
            Assert.True(refusals.Count == 0,
                $"Il salvataggio ha rifiutato qualcosa. La freccia tirata a mano era {arrow}.{Environment.NewLine}" +
                $"Il verbale diceva:{Environment.NewLine}{await editor.ReportAsync()}");

            // SI SALVA DUE VOLTE, e la seconda e' quella che puo' fare danno in silenzio. Il
            // disegno non torna mai a draw.io dopo un salvataggio, quindi la freccia resta sulla
            // tela com'era - senza marchio - e al secondo giro si ripresenta identica. Se nessuno
            // la riconoscesse, l'impianto si troverebbe DUE giunzioni fra gli stessi due
            // dispositivi, sovrapposte sul disegno: nessuno le vedrebbe, ed e' il caso peggiore di
            // tutta questa riparazione.
            //
            // E FINO AL RILIEVO 4 DELLA REVISIONE QUEL SECONDO SALVATAGGIO NON ARRIVAVA. Misura
            // del revisore: SaveAsync aspettava che il verbale ESISTESSE, e dal secondo in poi il
            // verbale del primo era ancora li', quindi la chiamata tornava all'istante - il
            // verbale letto subito dopo era identico byte per byte a quello del primo e l'impianto
            // non era cambiato. L'Assert.Single in fondo, l'unico controllo che un doppione lo
            // vedrebbe davvero, era quindi una corsa e non una rete. Adesso SaveAsync aspetta un
            // verbale DIVERSO da quello di prima del clic.
            await editor.SaveAsync();
            Assert.Null(await editor.SaveErrorAsync());
            output.WriteLine($"Verbale del secondo salvataggio:{Environment.NewLine}{await editor.ReportAsync()}");
            var again = await editor.RefusalsAsync();
            Assert.True(again.Count == 0,
                $"Il secondo salvataggio, senza che nessuno abbia toccato niente, ha rifiutato qualcosa." +
                $"{Environment.NewLine}Il verbale diceva:{Environment.NewLine}{await editor.ReportAsync()}");
        }

        var junction = Assert.Single(await client.GetJunctionsAsync());
        output.WriteLine(
            $"Giunzione sull'impianto: {junction.JunctionId}, {junction.SourceItemId} -> " +
            $"{junction.DestinationItemId}, priorita' {junction.Priority}.");
        Assert.Equal(SourceId, junction.SourceItemId);
        Assert.Equal(TargetId, junction.DestinationItemId);

        // RIAPRIRE E' DOVE UN DOPPIONE SI VEDREBBE. La freccia tirata a mano non e' piu' nella
        // decorazione - il lettore l'ha letta come giunzione, quindi il disegno libero non se la
        // porta piu' dietro - e la giunzione dell'impianto viene ridisegnata dal documento con il
        // proprio identificativo. Se una delle due meta' sbagliasse, la tela porterebbe DUE frecce
        // sovrapposte fra gli stessi due dispositivi: sullo schermo si vedrebbe una riga sola, e
        // l'operatore ne cancellerebbe una credendo di averle tolte tutte.
        await PlantSeed.WaitForTheEditPermitToBeFreeAsync(client, SeedHolder);
        await using (var reopened = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host))
        {
            await reopened.WaitForDeviceAsync(SourceName);
            var edges = await reopened.EdgesInTheDrawingAsync();
            foreach (var edge in edges) output.WriteLine($"Alla riapertura: {edge}.");
            var drawn = Assert.Single(edges);
            Assert.Equal(junction.JunctionId.ToString(), drawn.Id);
            Assert.Equal("junction", drawn.Wcs);
        }
    }

    /// <summary>
    /// The same gesture let go in the MIDDLE of the target instead of on its connection point: the
    /// arrow hangs from the whole cell and from no port. The plant does not take it - a junction is
    /// born only of an arrow hooked to two ports (decision 3.3) - and what this measures is that
    /// the operator is TOLD, by a line that names the arrow.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is the likelier of the two gestures for somebody who has never used the Editor, so
    /// silence here would be the same loss with a different shape: the arrow on the canvas, the
    /// save saying "succeeded", and nothing joined.
    /// </para>
    /// <para>
    /// <b>And the refusal is held to its word.</b> It says "it stays a drawing", so this test reads
    /// the plant's decoration back from the HOST and requires the arrow to be in it. Until this
    /// line existed the test was asymmetric with its sister
    /// <see cref="AnArrowPulledByHandOntoAFreeShapeStaysADrawing"/>, which does look at the
    /// decoration: it watched the refusal and not the drawing, and a refused arrow was being
    /// deleted from the operator's canvas while the message promised the opposite. A test that
    /// checks the words and not the thing the words promise is the hole this repair came through.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnArrowPulledByHandOntoNoPortIsRefusedInWords()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await SeedTheUprightPairAsync(client);
        Assert.Empty(await client.GetJunctionsAsync());

        string arrowId;
        await using (var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host))
        {
            await editor.WaitForDeviceAsync(SourceName);
            await editor.WaitForDeviceAsync(TargetName);

            var pulled = await editor.DragAnArrowByHandAsync(SourceName, TargetName, DrawioArrowLanding.OnTheMiddle);
            var arrow = TheOneArrowOf(pulled, SourceId.ToString(), TargetId.ToString());
            arrowId = arrow.Id;

            await editor.SaveAsync();
            Assert.Null(await editor.SaveErrorAsync());
            output.WriteLine($"Verbale del salvataggio:{Environment.NewLine}{await editor.ReportAsync()}");

            var refusals = await editor.RefusalsAsync();
            Assert.True(refusals.Any(line => line.Contains(arrow.Id, StringComparison.Ordinal)),
                $"Il salvataggio non ha detto niente della freccia {arrow.Id}, che non e' diventata una giunzione: " +
                $"la perdita e' silenziosa.{Environment.NewLine}Il verbale diceva:{Environment.NewLine}" +
                await editor.ReportAsync());
        }

        Assert.Empty(await client.GetJunctionsAsync());

        // «Resta un disegno»: letto dall'host, non dalla tela. Se questa riga e' rossa, il
        // salvataggio ha cancellato il disegno dell'operatore dicendogli che lo teneva.
        var decoration = await client.GetPlantDecorationAsync();
        Assert.True(decoration.Contains($"\"{arrowId}\"", StringComparison.Ordinal),
            $"Il rifiuto ha detto che la freccia {arrowId} «resta un disegno», ma nella decorazione " +
            $"dell'impianto non c'e': alla riapertura l'operatore non la ritrova.{Environment.NewLine}{decoration}");

        // E la riapertura e' dove l'operatore se ne accorgerebbe: la freccia deve tornare sulla
        // tela. Non e' un doppione del controllo sopra - li' si guarda che cosa l'impianto tiene,
        // qui che cosa il documento rimette davanti agli occhi di chi disegna.
        await PlantSeed.WaitForTheEditPermitToBeFreeAsync(client, SeedHolder);
        await using var reopened = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await reopened.WaitForDeviceAsync(SourceName);
        var edges = await reopened.EdgesInTheDrawingAsync();
        foreach (var edge in edges) output.WriteLine($"Alla riapertura: {edge}.");
        var drawn = Assert.Single(edges);
        Assert.Equal(arrowId, drawn.Id);
        Assert.Null(drawn.Wcs);
    }

    /// <summary>
    /// The third landing of the same drag, and the last silence of this family: the arrow let go
    /// <b>beside</b> the target shape instead of on it. One end hangs from a device, the other from
    /// nothing. No junction is born - there is nothing to join - and what this measures is that the
    /// operator is <b>told</b>, by a line that names the arrow, and that his drawing is still there
    /// afterwards.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A NOTICE, not a refusal: nothing was refused, because an arrow hooked to one device never
    /// was a candidate for a junction. The test asks for it where the page really puts it, and also
    /// asks that no refusal turned up - a line in the wrong list would teach the operator that the
    /// refusals are full of things that are not refusals.
    /// </para>
    /// <para>
    /// The gesture is measured before the report is, like its two sisters: a drop that draw.io
    /// turned into something else - a shape picker, an arrow hooked to the target anyway - is
    /// reported as a gesture that is not the one this test believes in, and not as a page that said
    /// nothing.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnArrowPulledByHandBesideTheShapeIsSaidToJoinNothing()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await SeedTheUprightPairAsync(client);
        Assert.Empty(await client.GetJunctionsAsync());

        string arrowId;
        await using (var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host))
        {
            await editor.WaitForDeviceAsync(SourceName);
            await editor.WaitForDeviceAsync(TargetName);

            var pulled = await editor.DragAnArrowByHandAsync(SourceName, TargetName, DrawioArrowLanding.BesideTheShape);
            output.WriteLine($"Il gesto: {pulled.Account}.");
            foreach (var edge in pulled.NewEdges) output.WriteLine($"Freccia nata: {edge}.");

            Assert.False(pulled.SourceShapeMoved,
                $"La pressione sul bordo ha SPOSTATO la sagoma invece di agganciare una freccia: {pulled.Account}.");
            Assert.True(pulled.NewEdges.Count == 1,
                $"Il trascinamento ha lasciato {pulled.NewEdges.Count} frecce nel disegno invece di una: {pulled.Account}.");

            var arrow = pulled.NewEdges[0];
            arrowId = arrow.Id;
            Assert.True(arrow.Source == SourceId.ToString() && arrow.Target is null,
                $"Il rilascio accanto alla sagoma non ha lasciato una freccia con un capo per aria: {arrow}. " +
                "Il gesto non e' quello che questa prova crede di fare.");

            await editor.SaveAsync();
            Assert.Null(await editor.SaveErrorAsync());
            output.WriteLine($"Verbale del salvataggio:{Environment.NewLine}{await editor.ReportAsync()}");

            var notices = await editor.NoticesAsync();
            Assert.True(notices.Any(line => line.Contains(arrow.Id, StringComparison.Ordinal)),
                $"Il salvataggio non ha detto niente della freccia {arrow.Id}, che ha un capo per aria e non " +
                $"collega niente.{Environment.NewLine}Il verbale diceva:{Environment.NewLine}" +
                await editor.ReportAsync());

            var refusals = await editor.RefusalsAsync();
            Assert.True(refusals.All(line => !line.Contains(arrow.Id, StringComparison.Ordinal)),
                $"La freccia {arrow.Id} e' finita fra i RIFIUTI: non e' stato rifiutato niente, e i rifiuti " +
                $"devono restare il posto dove si trovano quelli veri.{Environment.NewLine}" +
                await editor.ReportAsync());
        }

        Assert.Empty(await client.GetJunctionsAsync());

        // E il disegno resta: qui non c'e' nessuna promozione a giunzione, quindi la freccia non
        // e' mai uscita dalla decorazione - ma e' la stessa famiglia, e si misura invece di
        // crederlo.
        var decoration = await client.GetPlantDecorationAsync();
        Assert.True(decoration.Contains($"\"{arrowId}\"", StringComparison.Ordinal),
            $"La freccia {arrowId} con un capo per aria non e' nella decorazione dell'impianto: " +
            $"il disegno dell'operatore e' sparito.{Environment.NewLine}{decoration}");
    }

    /// <summary>
    /// An arrow pulled onto a shape that is NOT a device of ours - the operator's own note - must
    /// stay a drawing: no junction on the plant, and the arrow still in the plant's decoration when
    /// the save is over, because that is where the drawing the operator owns is kept.
    /// </summary>
    /// <remarks>
    /// This is the half that a repair could quietly break, and it is measured in the same class as
    /// the one that has to work: the two answers are one decision, and a decision tested in one
    /// direction only is a decision half measured.
    /// </remarks>
    [Fact]
    public async Task AnArrowPulledByHandOntoAFreeShapeStaysADrawing()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await SeedTheUprightPairAsync(client);
        Assert.Empty(await client.GetJunctionsAsync());

        string note;
        string arrowId;
        await using (var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host))
        {
            await editor.WaitForDeviceAsync(SourceName);
            await editor.WaitForDeviceAsync(TargetName);

            note = await editor.PutAFreeNoteOnTheCanvasAsync("una nota dell'operatore");
            var pulled = await editor.DragAnArrowByHandOntoCellAsync(SourceName, note, DrawioArrowLanding.OnThePort);
            var arrow = TheOneArrowOf(pulled, SourceId.ToString(), note);
            arrowId = arrow.Id;

            await editor.SaveAsync();
            Assert.Null(await editor.SaveErrorAsync());
            output.WriteLine($"Verbale del salvataggio:{Environment.NewLine}{await editor.ReportAsync()}");
        }

        Assert.Empty(await client.GetJunctionsAsync());

        // The decoration is what the plant keeps of the drawing nobody else owns: an arrow that
        // vanished from it would be the operator's own work lost in silence, which is worse than a
        // junction that never got created.
        var decoration = await client.GetPlantDecorationAsync();
        Assert.True(decoration.Contains($"\"{note}\"", StringComparison.Ordinal),
            $"La nota {note} non e' nella decorazione dell'impianto dopo il salvataggio: " +
            $"il disegno dell'operatore e' sparito.{Environment.NewLine}{decoration}");
        Assert.True(decoration.Contains($"\"{arrowId}\"", StringComparison.Ordinal),
            $"La freccia {arrowId} verso la nota non e' nella decorazione dell'impianto dopo il salvataggio: " +
            $"il disegno dell'operatore e' sparito.{Environment.NewLine}{decoration}");
    }

    /// <summary>
    /// The owner's rule of 2026-09-19, measured where the operator lives: a line <b>with no
    /// head</b> between two devices joins nothing. No junction is born, the page says so by name,
    /// and <b>the line is still on the canvas when the drawing is opened again</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The head is taken off through draw.io's own Edit Style dialog</b>, after the arrow has
    /// been pulled with the mouse: the cell is draw.io's, the style change is draw.io's, and what
    /// the save reads is what draw.io stored. The Format panel's arrow picker writes the same key
    /// (<c>graph.setCellStyles('endArrow', …)</c>, measured in the bundled <c>app.min.js</c>); this
    /// road is the one already proven to work inside this embedded editor.
    /// </para>
    /// <para>
    /// <b>The drawing must not lose the line</b>, and that is asked twice - of the plant and of the
    /// canvas at the next opening - because it is exactly the regression the previous batch paid
    /// for: a cell that left the decoration before anybody had decided to write it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ALineWithNoHeadBetweenTwoDevicesIsNotAJunctionAndThePageSaysSo()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await SeedTheUprightPairAsync(client);
        Assert.Empty(await client.GetJunctionsAsync());

        string lineId;
        await using (var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host))
        {
            await editor.WaitForDeviceAsync(SourceName);
            await editor.WaitForDeviceAsync(TargetName);

            var pulled = await editor.DragAnArrowByHandAsync(SourceName, TargetName, DrawioArrowLanding.OnThePort);
            var arrow = TheOneArrowOf(pulled, SourceId.ToString(), TargetId.ToString());
            lineId = arrow.Id;

            var held = await editor.ChangeTheStyleWithEditStyleAsync(lineId, "endArrow=none;" + arrow.Style);
            var heads = await editor.HeadsDrawioResolvesAsync(lineId);
            output.WriteLine($"Stile dopo Edit Style: {held}");
            output.WriteLine($"Punte risolte da draw.io: endArrow={heads[0]}, startArrow={heads[1]}.");
            // NELLO STILE RISOLTO "(absent)" vuol dire NESSUNA PUNTA, al contrario di quello che
            // vuol dire nello stile della cella: risolvendo, mxGraph CANCELLA la chiave che vale
            // "none", quindi mxConnector non trova nessun marcatore da disegnare. Misurato, non
            // dedotto - si veda HeadsDrawioResolvesAsync.
            Assert.Equal("(absent)", heads[0]);
            Assert.Equal("(absent)", heads[1]);

            await editor.SaveAsync();
            Assert.Null(await editor.SaveErrorAsync());
            output.WriteLine($"Verbale del salvataggio:{Environment.NewLine}{await editor.ReportAsync()}");

            var notices = await editor.NoticesAsync();
            Assert.True(notices.Any(line => line.Contains(lineId, StringComparison.Ordinal)),
                $"Il salvataggio non ha detto niente del tratto {lineId}, che fra due dispositivi non collega " +
                $"niente: la perdita e' silenziosa.{Environment.NewLine}Il verbale diceva:{Environment.NewLine}" +
                await editor.ReportAsync());

            var refusals = await editor.RefusalsAsync();
            Assert.True(refusals.All(line => !line.Contains(lineId, StringComparison.Ordinal)),
                $"Il tratto {lineId} e' finito fra i RIFIUTI: non e' stato rifiutato niente, e i rifiuti devono " +
                $"restare il posto dove si trovano quelli veri.{Environment.NewLine}" +
                await editor.ReportAsync());
        }

        Assert.Empty(await client.GetJunctionsAsync());

        var decoration = await client.GetPlantDecorationAsync();
        Assert.True(decoration.Contains($"\"{lineId}\"", StringComparison.Ordinal),
            $"Il tratto {lineId} non e' nella decorazione dell'impianto: il disegno dell'operatore e' sparito." +
            $"{Environment.NewLine}{decoration}");

        await PlantSeed.WaitForTheEditPermitToBeFreeAsync(client, SeedHolder);
        await using var reopened = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await reopened.WaitForDeviceAsync(SourceName);
        var edges = await reopened.EdgesInTheDrawingAsync();
        foreach (var edge in edges) output.WriteLine($"Alla riapertura: {edge}.");
        var drawn = Assert.Single(edges);
        Assert.Equal(lineId, drawn.Id);
        Assert.Null(drawn.Wcs);
    }

    /// <summary>
    /// The other half of the same rule: <b>the head shows the direction</b>. The arrow is pulled
    /// from the source to the target with the mouse and then its head is moved to the other end
    /// with Edit Style - so what is on the screen points back at the device the drag started from -
    /// and the junction the plant ends up with runs <b>that</b> way.
    /// </summary>
    /// <remarks>
    /// Before the head was read, this gesture made a junction pointing the way the drag had gone,
    /// with nothing said: a link the operator would have seen one way round while the engine routed
    /// the other. The assertion is on the plant, read back from the HOST, because that is where the
    /// engine will read it.
    /// </remarks>
    [Fact]
    public async Task AnArrowWhoseHeadWasMovedToTheOtherEndJoinsTheDevicesTheOtherWayRound()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await SeedTheUprightPairAsync(client);
        Assert.Empty(await client.GetJunctionsAsync());

        await using (var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host))
        {
            await editor.WaitForDeviceAsync(SourceName);
            await editor.WaitForDeviceAsync(TargetName);

            var pulled = await editor.DragAnArrowByHandAsync(SourceName, TargetName, DrawioArrowLanding.OnThePort);
            var arrow = TheOneArrowOf(pulled, SourceId.ToString(), TargetId.ToString());

            var held = await editor.ChangeTheStyleWithEditStyleAsync(
                arrow.Id, "endArrow=none;startArrow=classic;startFill=1;" + arrow.Style);
            var heads = await editor.HeadsDrawioResolvesAsync(arrow.Id);
            output.WriteLine($"Stile dopo Edit Style: {held}");
            output.WriteLine($"Punte risolte da draw.io: endArrow={heads[0]}, startArrow={heads[1]}.");
            // La punta e' passata dall'altra parte: in fondo non c'e' piu' niente da disegnare
            // ("(absent)" nello stile risolto, si veda HeadsDrawioResolvesAsync), all'inizio c'e'.
            Assert.Equal("(absent)", heads[0]);
            Assert.Equal("classic", heads[1]);

            await editor.SaveAsync();
            Assert.Null(await editor.SaveErrorAsync());
            output.WriteLine($"Verbale del salvataggio:{Environment.NewLine}{await editor.ReportAsync()}");

            var refusals = await editor.RefusalsAsync();
            Assert.True(refusals.Count == 0,
                $"Il salvataggio ha rifiutato qualcosa.{Environment.NewLine}{await editor.ReportAsync()}");
        }

        var junction = Assert.Single(await client.GetJunctionsAsync());
        output.WriteLine($"Giunzione sull'impianto: {junction.SourceItemId} -> {junction.DestinationItemId}.");
        Assert.Equal(TargetId, junction.SourceItemId);
        Assert.Equal(SourceId, junction.DestinationItemId);
    }

    /// <summary>
    /// The half of the owner's sentence that says how a two-way connection is drawn: <b>two lines
    /// with a head, one each way</b>. Both are pulled with the mouse, in the same save, and the
    /// plant ends up with two junctions, one per direction - the duplicate check does not take the
    /// second for a copy of the first.
    /// </summary>
    [Fact]
    public async Task TwoArrowsDrawnTheOppositeWayJoinTheDevicesBothWays()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await SeedTheUprightPairAsync(client);
        Assert.Empty(await client.GetJunctionsAsync());

        await using (var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host))
        {
            await editor.WaitForDeviceAsync(SourceName);
            await editor.WaitForDeviceAsync(TargetName);

            var there = await editor.DragAnArrowByHandAsync(SourceName, TargetName, DrawioArrowLanding.OnThePort);
            TheOneArrowOf(there, SourceId.ToString(), TargetId.ToString());
            // The way back is the same gesture mirrored: the press lands on the RIGHT-hand border
            // of the far device and the drop on the near one's left-hand connection point, so the
            // drag travels leftwards across the gap the two seeded devices leave between them.
            var back = await editor.DragAnArrowByHandAsync(TargetName, SourceName, DrawioArrowLanding.OnThePort);
            TheOneArrowOf(back, TargetId.ToString(), SourceId.ToString());

            await editor.SaveAsync();
            Assert.Null(await editor.SaveErrorAsync());
            output.WriteLine($"Verbale del salvataggio:{Environment.NewLine}{await editor.ReportAsync()}");

            var refusals = await editor.RefusalsAsync();
            Assert.True(refusals.Count == 0,
                $"Il salvataggio ha rifiutato qualcosa: la seconda freccia e' stata presa per un doppione della " +
                $"prima?{Environment.NewLine}{await editor.ReportAsync()}");
        }

        var junctions = await client.GetJunctionsAsync();
        foreach (var j in junctions) output.WriteLine($"Giunzione: {j.SourceItemId} -> {j.DestinationItemId}.");
        Assert.Equal(2, junctions.Count);
        Assert.Single(junctions, j => j.SourceItemId == SourceId && j.DestinationItemId == TargetId);
        Assert.Single(junctions, j => j.SourceItemId == TargetId && j.DestinationItemId == SourceId);
    }

    /// <summary>
    /// <b>Taking the head off an arrow that is ALREADY a junction deletes that junction</b>, in the
    /// session that drew it - no reload - and the page says so by name. Measured against the host
    /// at both ends: one junction before the gesture, none after.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Review finding 1, the condition that used to be silent. The deletion was already happening
    /// here before this test existed; what the operator read on the same line was <i>"No junction
    /// was created and it stays a drawing"</i>, which is the opposite of what the save had just
    /// done, and no test of either suite touched a second save with the head taken off.
    /// </para>
    /// <para>
    /// <b>It needs the repaired <see cref="DrawioEditorPage.SaveAsync"/></b>, and that is not an
    /// aside: with the old wait - the report merely EXISTING - the second save returned instantly
    /// on the first save's report, and this test would have read the first outcome and passed
    /// whatever the second save did.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TakingTheHeadOffAnArrowAlreadySavedDeletesItsJunctionAndThePageSaysSo()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await SeedTheUprightPairAsync(client);
        Assert.Empty(await client.GetJunctionsAsync());

        string lineId;
        await using (var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host))
        {
            await editor.WaitForDeviceAsync(SourceName);
            await editor.WaitForDeviceAsync(TargetName);

            var pulled = await editor.DragAnArrowByHandAsync(SourceName, TargetName, DrawioArrowLanding.OnThePort);
            var arrow = TheOneArrowOf(pulled, SourceId.ToString(), TargetId.ToString());
            lineId = arrow.Id;

            await editor.SaveAsync();
            Assert.Null(await editor.SaveErrorAsync());
            output.WriteLine($"Primo salvataggio:{Environment.NewLine}{await editor.ReportAsync()}");

            // La premessa, letta dall'HOST: senza questa riga il controllo in fondo potrebbe essere
            // verde perche' la giunzione non e' mai nata, non perche' e' stata cancellata.
            var born = Assert.Single(await client.GetJunctionsAsync());
            output.WriteLine($"Giunzione nata: {born.JunctionId}, {born.SourceItemId} -> {born.DestinationItemId}.");

            var held = await editor.ChangeTheStyleWithEditStyleAsync(lineId, "endArrow=none;" + arrow.Style);
            var heads = await editor.HeadsDrawioResolvesAsync(lineId);
            output.WriteLine($"Stile dopo Edit Style: {held}");
            output.WriteLine($"Punte risolte da draw.io: endArrow={heads[0]}, startArrow={heads[1]}.");
            Assert.Equal("(absent)", heads[0]);
            Assert.Equal("(absent)", heads[1]);

            await editor.SaveAsync();
            Assert.Null(await editor.SaveErrorAsync());
            output.WriteLine($"Secondo salvataggio:{Environment.NewLine}{await editor.ReportAsync()}");

            var notices = await editor.NoticesAsync();
            Assert.True(notices.Any(line => line.Contains(lineId, StringComparison.Ordinal)
                    && line.Contains("has been DELETED from the plant", StringComparison.Ordinal)),
                $"Il salvataggio non ha detto che la giunzione del tratto {lineId} e' stata cancellata." +
                $"{Environment.NewLine}Il verbale diceva:{Environment.NewLine}{await editor.ReportAsync()}");
            Assert.True(notices.All(line => !line.Contains("No junction was created", StringComparison.Ordinal)),
                $"Il verbale dice ancora «No junction was created and it stays a drawing» per una cella che " +
                $"una giunzione ce l'aveva.{Environment.NewLine}{await editor.ReportAsync()}");
        }

        Assert.Empty(await client.GetJunctionsAsync());

        // E il disegno dell'operatore resta: la cella e' sua - draw.io l'ha fabbricata, nessun
        // marchio nostro - e a sparire e' solo la giunzione dell'impianto.
        var decoration = await client.GetPlantDecorationAsync();
        Assert.True(decoration.Contains($"\"{lineId}\"", StringComparison.Ordinal),
            $"Il tratto {lineId} non e' nella decorazione dell'impianto: il disegno dell'operatore e' sparito." +
            $"{Environment.NewLine}{decoration}");
    }

    /// <summary>
    /// The same gesture <b>after a reload</b>, where the arrow is a cell of ours carrying the
    /// plant's identifier: the junction goes, and the page says the same thing. Until this was
    /// repaired the two conditions did the opposite of each other - here the junction survived,
    /// and the line told the operator to delete the arrow to disconnect the devices.
    /// </summary>
    [Fact]
    public async Task TakingTheHeadOffAJunctionOfThePlantDeletesItAfterAReloadToo()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await SeedTheUprightPairAsync(client);
        Assert.Empty(await client.GetJunctionsAsync());

        await using (var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host))
        {
            await editor.WaitForDeviceAsync(SourceName);
            await editor.WaitForDeviceAsync(TargetName);
            var pulled = await editor.DragAnArrowByHandAsync(SourceName, TargetName, DrawioArrowLanding.OnThePort);
            TheOneArrowOf(pulled, SourceId.ToString(), TargetId.ToString());
            await editor.SaveAsync();
            Assert.Null(await editor.SaveErrorAsync());
        }

        var junction = Assert.Single(await client.GetJunctionsAsync());
        output.WriteLine($"Giunzione nata: {junction.JunctionId}.");

        await PlantSeed.WaitForTheEditPermitToBeFreeAsync(client, SeedHolder);
        await using (var reopened = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host))
        {
            await reopened.WaitForDeviceAsync(SourceName);
            var drawn = Assert.Single(await reopened.EdgesInTheDrawingAsync());
            output.WriteLine($"Alla riapertura: {drawn}.");
            // La premessa della seconda condizione: dopo il ricaricamento la freccia e' una cella
            // NOSTRA, con l'identificativo dell'impianto. Se non lo fosse, questa prova starebbe
            // misurando di nuovo la prima condizione senza dirlo.
            Assert.Equal(junction.JunctionId.ToString(), drawn.Id);
            Assert.Equal("junction", drawn.Wcs);

            // La punta si TOGLIE, non si antepone: lo stile che il documento scrive porta gia'
            // endArrow=block, e per mxGraph vince l'ULTIMA occorrenza della chiave - un
            // "endArrow=none;" davanti non toglierebbe niente.
            var without = drawn.Style.Replace("endArrow=block", "endArrow=none", StringComparison.Ordinal);
            Assert.NotEqual(drawn.Style, without);

            var held = await reopened.ChangeTheStyleWithEditStyleAsync(drawn.Id, without);
            var heads = await reopened.HeadsDrawioResolvesAsync(drawn.Id);
            output.WriteLine($"Stile dopo Edit Style: {held}");
            output.WriteLine($"Punte risolte da draw.io: endArrow={heads[0]}, startArrow={heads[1]}.");
            Assert.Equal("(absent)", heads[0]);
            Assert.Equal("(absent)", heads[1]);

            await reopened.SaveAsync();
            Assert.Null(await reopened.SaveErrorAsync());
            output.WriteLine($"Salvataggio dopo il ricaricamento:{Environment.NewLine}{await reopened.ReportAsync()}");

            var notices = await reopened.NoticesAsync();
            Assert.True(notices.Any(line => line.Contains(junction.JunctionId.ToString(), StringComparison.Ordinal)
                    && line.Contains("has been DELETED from the plant", StringComparison.Ordinal)),
                $"Il salvataggio non ha detto che la giunzione {junction.JunctionId} e' stata cancellata." +
                $"{Environment.NewLine}Il verbale diceva:{Environment.NewLine}{await reopened.ReportAsync()}");
        }

        Assert.Empty(await client.GetJunctionsAsync());
    }

    /// <summary>
    /// <b>The save AFTER the one that deleted the junction does not bring it back.</b> Same page,
    /// same canvas, nothing touched between the two clicks - and the plant stays empty.
    /// </summary>
    /// <remarks>
    /// <para>
    /// GRAVE 1 of the second review, measured there against the real host: the second save wrote
    /// <i>"junction 8253ac51-...: 0101 -&gt; 0102"</i> back onto the plant while the line on the
    /// canvas still had no head at all, denying word for word the report of the first save ("to
    /// join them again, give the line a head and save"). It took no double click: the marked cell
    /// stays on the canvas after a save, so moving a device and saving did it too.
    /// </para>
    /// <para>
    /// <b>It is the same defect as GRAVE 2</b> - a cell marked <c>wcs="junction"</c> that the
    /// plant does not hold used to become a junction, head or no head - and the reason both are
    /// measured is that neither could be reached by mutating any line of the repair: the rule was
    /// written in one branch of two, and the branch that lacked it had no line to mutate.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheSaveAfterTheHeadWasTakenOffDoesNotBringTheJunctionBack()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await SeedTheUprightPairAsync(client);
        Assert.Empty(await client.GetJunctionsAsync());

        await using (var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host))
        {
            await editor.WaitForDeviceAsync(SourceName);
            await editor.WaitForDeviceAsync(TargetName);
            var pulled = await editor.DragAnArrowByHandAsync(SourceName, TargetName, DrawioArrowLanding.OnThePort);
            TheOneArrowOf(pulled, SourceId.ToString(), TargetId.ToString());
            await editor.SaveAsync();
            Assert.Null(await editor.SaveErrorAsync());
        }

        var junction = Assert.Single(await client.GetJunctionsAsync());
        output.WriteLine($"Giunzione nata: {junction.JunctionId}.");

        await PlantSeed.WaitForTheEditPermitToBeFreeAsync(client, SeedHolder);
        await using (var reopened = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host))
        {
            await reopened.WaitForDeviceAsync(SourceName);
            var drawn = Assert.Single(await reopened.EdgesInTheDrawingAsync());
            // La premessa: dopo il ricaricamento la freccia e' una cella NOSTRA. E' la condizione
            // in cui il difetto si misura, perche' e' il marchio a scavalcare la regola.
            Assert.Equal(junction.JunctionId.ToString(), drawn.Id);
            Assert.Equal("junction", drawn.Wcs);

            var without = drawn.Style.Replace("endArrow=block", "endArrow=none", StringComparison.Ordinal);
            Assert.NotEqual(drawn.Style, without);
            await reopened.ChangeTheStyleWithEditStyleAsync(drawn.Id, without);
            var heads = await reopened.HeadsDrawioResolvesAsync(drawn.Id);
            output.WriteLine($"Punte risolte da draw.io: endArrow={heads[0]}, startArrow={heads[1]}.");
            Assert.Equal("(absent)", heads[0]);
            Assert.Equal("(absent)", heads[1]);

            await reopened.SaveAsync();
            Assert.Null(await reopened.SaveErrorAsync());
            output.WriteLine($"PRIMO salvataggio:{Environment.NewLine}{await reopened.ReportAsync()}");
            // La premessa del controllo vero: senza questa riga il verde in fondo potrebbe voler
            // dire che la giunzione non e' mai stata cancellata.
            Assert.Empty(await client.GetJunctionsAsync());

            // E adesso il gesto del rilievo: NIENTE. Solo il secondo clic su Salva.
            await reopened.SaveAsync();
            Assert.Null(await reopened.SaveErrorAsync());
            output.WriteLine($"SECONDO salvataggio, niente toccato:{Environment.NewLine}{await reopened.ReportAsync()}");

            // La punta non e' tornata: se lo fosse, una giunzione nuova sarebbe legittima e questa
            // prova starebbe misurando un'altra cosa.
            var stillBare = await reopened.HeadsDrawioResolvesAsync(drawn.Id);
            Assert.Equal("(absent)", stillBare[0]);
            Assert.Equal("(absent)", stillBare[1]);

            var written = await reopened.WrittenAsync();
            Assert.True(written.All(line => !line.Contains("junction", StringComparison.OrdinalIgnoreCase)),
                $"Il secondo salvataggio ha toccato una giunzione su una linea senza punta." +
                $"{Environment.NewLine}{await reopened.ReportAsync()}");
        }

        var after = await client.GetJunctionsAsync();
        foreach (var j in after) output.WriteLine($"Giunzione rimasta: {j.JunctionId}, {j.SourceItemId} -> {j.DestinationItemId}.");
        Assert.Empty(after);
    }

    /// <summary>
    /// <b>Moving the head of a junction of the plant's turns the connection round</b>, after a
    /// reload just as inside the session that drew it. The plant is read back from the HOST: the
    /// two ends have swapped, and the identifier is a new one because the junction is redone as a
    /// deletion plus a creation.
    /// </summary>
    /// <remarks>
    /// Review finding 2. In this condition the gesture used to do nothing at all, with a line
    /// telling the operator to delete the arrow and draw one the other way - advice that was true
    /// only here, because in the session that drew the arrow moving the head was already enough.
    /// </remarks>
    [Fact]
    public async Task MovingTheHeadOfAJunctionOfThePlantTurnsItRoundAfterAReloadToo()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await SeedTheUprightPairAsync(client);
        Assert.Empty(await client.GetJunctionsAsync());

        await using (var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host))
        {
            await editor.WaitForDeviceAsync(SourceName);
            await editor.WaitForDeviceAsync(TargetName);
            var pulled = await editor.DragAnArrowByHandAsync(SourceName, TargetName, DrawioArrowLanding.OnThePort);
            TheOneArrowOf(pulled, SourceId.ToString(), TargetId.ToString());
            await editor.SaveAsync();
            Assert.Null(await editor.SaveErrorAsync());
        }

        var before = Assert.Single(await client.GetJunctionsAsync());
        output.WriteLine($"Giunzione nata: {before.JunctionId}, {before.SourceItemId} -> {before.DestinationItemId}.");
        Assert.Equal(SourceId, before.SourceItemId);

        await PlantSeed.WaitForTheEditPermitToBeFreeAsync(client, SeedHolder);
        await using (var reopened = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host))
        {
            await reopened.WaitForDeviceAsync(SourceName);
            var drawn = Assert.Single(await reopened.EdgesInTheDrawingAsync());
            output.WriteLine($"Alla riapertura: {drawn}.");
            Assert.Equal(before.JunctionId.ToString(), drawn.Id);
            Assert.Equal("junction", drawn.Wcs);

            var moved = drawn.Style.Replace("endArrow=block", "endArrow=none;startArrow=block", StringComparison.Ordinal);
            Assert.NotEqual(drawn.Style, moved);

            var held = await reopened.ChangeTheStyleWithEditStyleAsync(drawn.Id, moved);
            var heads = await reopened.HeadsDrawioResolvesAsync(drawn.Id);
            output.WriteLine($"Stile dopo Edit Style: {held}");
            output.WriteLine($"Punte risolte da draw.io: endArrow={heads[0]}, startArrow={heads[1]}.");
            Assert.Equal("(absent)", heads[0]);
            Assert.Equal("block", heads[1]);

            await reopened.SaveAsync();
            Assert.Null(await reopened.SaveErrorAsync());
            output.WriteLine($"Salvataggio dopo il ricaricamento:{Environment.NewLine}{await reopened.ReportAsync()}");

            var notices = await reopened.NoticesAsync();
            Assert.True(notices.Any(line => line.Contains("re-hooked", StringComparison.Ordinal)
                    && line.Contains("new identifier", StringComparison.Ordinal)),
                $"Il salvataggio non ha dichiarato il riaggancio con il cambio di identificativo." +
                $"{Environment.NewLine}Il verbale diceva:{Environment.NewLine}{await reopened.ReportAsync()}");
            Assert.True(notices.All(line => !line.Contains("delete this arrow and draw one the other way", StringComparison.Ordinal)),
                $"Il verbale dice ancora che per rovesciare il collegamento bisogna cancellare la freccia." +
                $"{Environment.NewLine}{await reopened.ReportAsync()}");
        }

        var after = Assert.Single(await client.GetJunctionsAsync());
        output.WriteLine($"Giunzione dopo: {after.JunctionId}, {after.SourceItemId} -> {after.DestinationItemId}.");
        Assert.Equal(TargetId, after.SourceItemId);
        Assert.Equal(SourceId, after.DestinationItemId);
        Assert.NotEqual(before.JunctionId, after.JunctionId);
    }

    /// <summary>
    /// <b>The line the operator got back does not vanish at the gesture after.</b> The head is
    /// taken off - the junction dies and the line stays his drawing - and then ONE END of that
    /// line is detached. The save refuses it with <i>"it was not created, it stays a drawing"</i>,
    /// and this measures whether the drawing it promises is there when the page is opened again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The third return of one defect</b>, measured by the third review against the real host:
    /// the canvas came back with zero edges while the report said the line stayed. The two rounds
    /// that decided what goes back to the drawing asked two different questions - "is the cell
    /// unmarked?" and "is it a line of ours between two devices?" - and this cell was neither, so
    /// it fell between them. It is reached by the gesture immediately after the one the batch
    /// itself teaches: the notice tells the operator the line is his and that he may give it a
    /// head; if instead he moves it, detaches it, or hooks it somewhere else, he used to lose it.
    /// </para>
    /// <para>
    /// <b>Why the full round and not the delta.</b> What the save hands back is a string; what the
    /// operator sees is what draw.io draws from it at the next opening. The two have already
    /// disagreed once on this branch, so the assertion is on the reopened canvas, read out of
    /// draw.io's own model, with the plant read back from the host in between.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ALineWhoseEndIsDetachedAfterTheHeadWasTakenOffIsStillOnTheCanvas()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await SeedTheUprightPairAsync(client);
        Assert.Empty(await client.GetJunctionsAsync());

        await using (var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host))
        {
            await editor.WaitForDeviceAsync(SourceName);
            await editor.WaitForDeviceAsync(TargetName);
            var pulled = await editor.DragAnArrowByHandAsync(SourceName, TargetName, DrawioArrowLanding.OnThePort);
            TheOneArrowOf(pulled, SourceId.ToString(), TargetId.ToString());
            await editor.SaveAsync();
            Assert.Null(await editor.SaveErrorAsync());
        }

        var junction = Assert.Single(await client.GetJunctionsAsync());
        output.WriteLine($"Giunzione nata: {junction.JunctionId}.");

        // Primo giro: si toglie la punta. La giunzione muore e la linea torna all'operatore - e'
        // il rimedio che il lotto scorso ha chiuso, ed e' la PREMESSA di questa prova.
        await PlantSeed.WaitForTheEditPermitToBeFreeAsync(client, SeedHolder);
        await using (var reopened = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host))
        {
            await reopened.WaitForDeviceAsync(SourceName);
            var drawn = Assert.Single(await reopened.EdgesInTheDrawingAsync());
            Assert.Equal(junction.JunctionId.ToString(), drawn.Id);
            Assert.Equal("junction", drawn.Wcs);

            var without = drawn.Style.Replace("endArrow=block", "endArrow=none", StringComparison.Ordinal);
            Assert.NotEqual(drawn.Style, without);
            await reopened.ChangeTheStyleWithEditStyleAsync(drawn.Id, without);
            await reopened.SaveAsync();
            Assert.Null(await reopened.SaveErrorAsync());
            output.WriteLine($"Salvataggio con la punta tolta:{Environment.NewLine}{await reopened.ReportAsync()}");
        }

        Assert.Empty(await client.GetJunctionsAsync());

        // Secondo giro: la linea e' tornata nel disegno, e adesso se ne stacca un capo.
        await PlantSeed.WaitForTheEditPermitToBeFreeAsync(client, SeedHolder);
        await using (var reopened = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host))
        {
            await reopened.WaitForDeviceAsync(SourceName);
            var back = Assert.Single(await reopened.EdgesInTheDrawingAsync());
            output.WriteLine($"La linea restituita: {back}.");
            Assert.Equal(junction.JunctionId.ToString(), back.Id);

            var ends = await reopened.TakeOneEndOffAsync(back.Id);
            output.WriteLine($"Capi dopo lo stacco: source={ends[0]}, target={ends[1]}.");
            Assert.Equal("(none)", ends[1]);

            await reopened.SaveAsync();
            Assert.Null(await reopened.SaveErrorAsync());
            output.WriteLine($"Salvataggio con il capo staccato:{Environment.NewLine}{await reopened.ReportAsync()}");

            // Il verbale PROMETTE che la linea resta un disegno: se un giorno smettesse di
            // prometterlo, questa prova starebbe misurando un'altra cosa e deve dirlo.
            var refusals = await reopened.RefusalsAsync();
            Assert.True(refusals.Any(line => line.Contains("it stays a drawing", StringComparison.Ordinal)),
                $"Il salvataggio non ha promesso che la linea resta un disegno." +
                $"{Environment.NewLine}{await reopened.ReportAsync()}");
        }

        // E il giro completo: quello che l'operatore vede riaprendo la pagina.
        await PlantSeed.WaitForTheEditPermitToBeFreeAsync(client, SeedHolder);
        await using (var finalOpening = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host))
        {
            await finalOpening.WaitForDeviceAsync(SourceName);
            var edges = await finalOpening.EdgesInTheDrawingAsync();
            foreach (var edge in edges) output.WriteLine($"Alla riapertura finale: {edge}.");
            var kept = Assert.Single(edges);
            Assert.Equal(junction.JunctionId.ToString(), kept.Id);
            Assert.Equal(SourceId.ToString(), kept.Source);
            Assert.Null(kept.Target);
        }

        Assert.Empty(await client.GetJunctionsAsync());
    }

    /// <summary>
    /// <b>Both ends of a junction dropped onto the same device does not cost the operator the
    /// junction.</b> The plant refuses a device joined to itself, so the arrow cannot be written
    /// that way - and what must not happen is the half gesture: the old junction deleted and the
    /// new one refused by the host afterwards.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The third review found the delta proposing that write (<c>new=1 del=1</c>) in bUnit, where
    /// there is no host to say no. Against the real host there is one:
    /// <c>POST /plant/junctions</c> answers 400 - <i>"Una giunzione non puo' avere lo stesso
    /// dispositivo come origine e destinazione"</i> - and the deletion is planned BEFORE the
    /// creation, so the junction was gone and nothing took its place. That is the loss this
    /// file's own rule about re-hooking exists to prevent, arriving through the one road the rule
    /// did not check.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task HookingBothEndsOntoOneDeviceDoesNotCostTheJunction()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await SeedTheUprightPairAsync(client);
        Assert.Empty(await client.GetJunctionsAsync());

        await using (var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host))
        {
            await editor.WaitForDeviceAsync(SourceName);
            await editor.WaitForDeviceAsync(TargetName);
            var pulled = await editor.DragAnArrowByHandAsync(SourceName, TargetName, DrawioArrowLanding.OnThePort);
            TheOneArrowOf(pulled, SourceId.ToString(), TargetId.ToString());
            await editor.SaveAsync();
            Assert.Null(await editor.SaveErrorAsync());
        }

        var junction = Assert.Single(await client.GetJunctionsAsync());
        output.WriteLine($"Giunzione nata: {junction.JunctionId}, {junction.SourceItemId} -> {junction.DestinationItemId}.");

        await PlantSeed.WaitForTheEditPermitToBeFreeAsync(client, SeedHolder);
        await using (var reopened = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host))
        {
            await reopened.WaitForDeviceAsync(SourceName);
            var drawn = Assert.Single(await reopened.EdgesInTheDrawingAsync());
            Assert.Equal(junction.JunctionId.ToString(), drawn.Id);

            var ends = await reopened.HookOneEndOntoAsync(drawn.Id, SourceId.ToString());
            output.WriteLine($"Capi dopo il riaggancio: source={ends[0]}, target={ends[1]}.");
            Assert.Equal(SourceId.ToString(), ends[0]);
            Assert.Equal(SourceId.ToString(), ends[1]);

            await reopened.SaveAsync();
            output.WriteLine($"Salvataggio con i due capi sullo stesso dispositivo:{Environment.NewLine}{await reopened.ReportAsync()}");

            var refusals = await reopened.RefusalsAsync();
            Assert.True(refusals.Any(line => line.Contains("itself", StringComparison.Ordinal)),
                $"Il salvataggio non ha nominato il cappio." +
                $"{Environment.NewLine}{await reopened.ReportAsync()}");
        }

        // Quello che conta: la giunzione dell'operatore e' ancora sull'impianto, letta dall'host.
        var after = await client.GetJunctionsAsync();
        foreach (var j in after) output.WriteLine($"Giunzione rimasta: {j.JunctionId}, {j.SourceItemId} -> {j.DestinationItemId}.");
        var kept = Assert.Single(after);
        Assert.Equal(junction.JunctionId, kept.JunctionId);
        Assert.Equal(SourceId, kept.SourceItemId);
        Assert.Equal(TargetId, kept.DestinationItemId);
    }

    /// <summary>
    /// The one arrow the gesture left behind, hanging from these two cells - or a failure that says
    /// which of the several things that can go wrong did.
    /// </summary>
    private DrawioEdge TheOneArrowOf(DrawioHandDrawnArrow pulled, string source, string target)
    {
        output.WriteLine($"Il gesto: {pulled.Account}.");
        foreach (var edge in pulled.NewEdges) output.WriteLine($"Freccia nata: {edge}.");

        Assert.False(pulled.SourceShapeMoved,
            $"La pressione sul bordo ha SPOSTATO la sagoma invece di agganciare una freccia: {pulled.Account}. " +
            "Non e' l'impianto a non essere cambiato, e' il gesto a non essere quello che questa prova crede.");
        Assert.True(pulled.NewEdges.Count == 1,
            $"Il trascinamento ha lasciato {pulled.NewEdges.Count} frecce nel disegno invece di una: " +
            $"{pulled.Account}. Il gesto non e' quello che questa prova crede di fare.");

        var arrow = pulled.NewEdges[0];
        Assert.True(arrow.Source == source && arrow.Target == target,
            $"La freccia nata dal gesto non collega le due celle: {arrow}. Attese {source} -> {target}. " +
            "Il trascinamento l'ha lasciata appesa nel vuoto, e quello che il salvataggio ne fara' non " +
            "risponderebbe alla domanda.");
        return arrow;
    }

    /// <summary>
    /// <b>Lo stesso gesto lascia la stessa tela prima e dopo un ricaricamento</b>, misurato sul
    /// binario vero invece che dichiarato.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Il gesto e' uno solo: si stacca un capo di una freccia che l'impianto tiene come giunzione.
    /// Le due condizioni sono l'identificativo che la cella porta. <b>Dopo</b> un ricaricamento la
    /// cella e' quella che <c>DrawioDocument.Build</c> ha disegnato e porta il nome dell'impianto;
    /// <b>prima</b>, la freccia e' quella che l'operatore ha tirato in questa sessione e porta
    /// quello che draw.io le ha dato - la pagina fra i due salvataggi non viene ricostruita.
    /// </para>
    /// <para>
    /// <b>Che cosa misura.</b> Fino al 20/9/2026 le due condizioni finivano diverse: dopo un
    /// ricaricamento il ridisegno dell'impianto prendeva il posto della cella e restava UNA riga,
    /// prima restavano DUE - la giunzione ridisegnata sotto il nome dell'impianto e la cella
    /// dell'operatore sotto quello di draw.io, una sopra l'altra e senza nessun identificativo
    /// ripetuto. La decisione del proprietario e' che la cella spogliata si toglie, e la ragione e'
    /// esattamente questa prova: il gesto deve fare la stessa cosa nelle due condizioni.
    /// </para>
    /// <para>
    /// <b>Letto dalla tela riaperta, non dal verbale.</b> Quello che il salvataggio promette lo
    /// sorveglia la rete delle parole; quello che l'operatore VEDE e' il disegno che draw.io
    /// costruisce all'apertura dopo, e le due cose hanno gia' avuto ragione l'una contro l'altra.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheSameGestureLeavesTheSameCanvasBeforeAndAfterAReload()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await SeedTheUprightPairAsync(client);
        Assert.Empty(await client.GetJunctionsAsync());

        // PRIMA DI UN RICARICAMENTO, e la pagina non viene riaperta fra i due salvataggi: la
        // freccia resta quella che l'operatore ha in mano, sotto il nome di draw.io.
        string pulledCellId;
        await using (var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host))
        {
            await editor.WaitForDeviceAsync(SourceName);
            await editor.WaitForDeviceAsync(TargetName);
            var pulled = await editor.DragAnArrowByHandAsync(SourceName, TargetName, DrawioArrowLanding.OnThePort);
            pulledCellId = TheOneArrowOf(pulled, SourceId.ToString(), TargetId.ToString()).Id;

            await editor.SaveAsync();
            Assert.Null(await editor.SaveErrorAsync());

            var ends = await editor.TakeOneEndOffAsync(pulledCellId);
            output.WriteLine($"Capi dopo lo stacco, cella {pulledCellId}: source={ends[0]}, target={ends[1]}.");
            Assert.Equal("(none)", ends[1]);

            await editor.SaveAsync();
            Assert.Null(await editor.SaveErrorAsync());
            output.WriteLine($"Salvataggio PRIMA di un ricaricamento:{Environment.NewLine}{await editor.ReportAsync()}");
        }

        var junction = Assert.Single(await client.GetJunctionsAsync());
        output.WriteLine($"La giunzione e' rimasta sull'impianto: {junction.JunctionId}.");
        Assert.NotEqual(junction.JunctionId.ToString(), pulledCellId);

        var beforeAReload = await WhatTheOperatorSeesAsync(client, "PRIMA di un ricaricamento");

        // DOPO UN RICARICAMENTO: la stessa giunzione, disegnata adesso dall'impianto e sotto il suo
        // nome, e lo stesso gesto.
        await PlantSeed.WaitForTheEditPermitToBeFreeAsync(client, SeedHolder);
        await using (var reopened = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host))
        {
            await reopened.WaitForDeviceAsync(SourceName);
            var drawn = Assert.Single(await reopened.EdgesInTheDrawingAsync());
            Assert.Equal(junction.JunctionId.ToString(), drawn.Id);
            Assert.Equal("junction", drawn.Wcs);

            var ends = await reopened.TakeOneEndOffAsync(drawn.Id);
            output.WriteLine($"Capi dopo lo stacco, cella {drawn.Id}: source={ends[0]}, target={ends[1]}.");
            Assert.Equal("(none)", ends[1]);

            await reopened.SaveAsync();
            Assert.Null(await reopened.SaveErrorAsync());
            output.WriteLine($"Salvataggio DOPO un ricaricamento:{Environment.NewLine}{await reopened.ReportAsync()}");
        }

        Assert.Single(await client.GetJunctionsAsync());
        var afterAReload = await WhatTheOperatorSeesAsync(client, "DOPO un ricaricamento");

        // LA MISURA. Non "una riga sola" due volte, che due difetti diversi potrebbero soddisfare:
        // la stessa riga, con lo stesso marchio e gli stessi capi.
        Assert.True(beforeAReload == afterAReload,
            $"Lo stesso gesto lascia due tele diverse.{Environment.NewLine}" +
            $"prima di un ricaricamento: {beforeAReload}{Environment.NewLine}" +
            $"dopo un ricaricamento:     {afterAReload}");

        Assert.Equal($"1 linee: junction {SourceId} -> {TargetId}", afterAReload);
    }

    /// <summary>
    /// Quello che l'operatore vede riaprendo la pagina, in una riga: quante linee, con che marchio
    /// e fra quali capi. L'identificativo della cella non c'e' dentro apposta - non e' una cosa che
    /// si veda - e viene scritto a parte nell'uscita.
    /// </summary>
    private async Task<string> WhatTheOperatorSeesAsync(Wcs.Api.Client.WcsClient client, string when)
    {
        await PlantSeed.WaitForTheEditPermitToBeFreeAsync(client, SeedHolder);
        await using var opening = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await opening.WaitForDeviceAsync(SourceName);

        var edges = await opening.EdgesInTheDrawingAsync();
        foreach (var edge in edges) output.WriteLine($"{when}, sulla tela: {edge}.");

        var lines = edges
            .Select(edge => $"{edge.Wcs ?? "(nessun marchio)"} {edge.Source ?? "(niente)"} -> {edge.Target ?? "(niente)"}")
            .Order(StringComparer.Ordinal);

        var seen = $"{edges.Length} linee: {string.Join(" | ", lines)}";
        output.WriteLine($"Quello che l'operatore vede {when}: {seen}");
        return seen;
    }

    /// <summary>
    /// The plant this class works on: the usual reset - which empties it - and then the two upright
    /// devices, which the seeding does not carry.
    /// </summary>
    private static async Task SeedTheUprightPairAsync(Wcs.Api.Client.WcsClient client)
    {
        await PlantSeed.ResetAsync(client, SeedHolder);
        await PlantSeed.AddDeviceAsync(client, SeedHolder, SourceId, SourceName, DeviceTypes.Automation, SourceGeometry);
        await PlantSeed.AddDeviceAsync(client, SeedHolder, TargetId, TargetName, DeviceTypes.Automation, TargetGeometry);
    }
}
