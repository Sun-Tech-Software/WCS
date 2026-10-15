using Wcs.Api.Client;
using Wcs.Api.Contracts.Dtos;
using Xunit;
using static Wcs.Common.Enums;
using Xunit.Abstractions;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// <b>The two gestures in which a connection left the plant without a word</b>, measured against
/// the real host and with the plant read back from it - not from the canvas.
/// </summary>
/// <remarks>
/// <para>
/// They are the two defects the generative nets isolated as ONE shape: an answer worked out
/// against the wrong thing. <see cref="AJunctionAnotherHandDeletedIsNotLostInSilence"/> is the
/// plant changing underneath while the page is open - which is not a laboratory case, because the
/// edit permit lasts sixty seconds and is taken only while writing, so between two saves a minute
/// apart somebody else legitimately holds it. <see cref="TheHeadTakenOffAnArrowOfThisSessionIsAnnounced"/>
/// is the arrow drawn in THIS session, whose junction the plant knows under an identifier the
/// canvas does not carry.
/// </para>
/// <para>
/// <b>What each one asserts is the SENTENCE, not the outcome.</b> In both, losing the connection
/// is a legitimate outcome - another operator removed it; the head that said "connection" is gone.
/// What was the defect is that nobody was told, and a report that stays quiet about a connection
/// leaving the plant is the loss this series has paid for four times.
/// </para>
/// </remarks>
public sealed class DrawioThePlantChangedUnderneathTest(DrawioEditorFixture fixture, ITestOutputHelper output)
    : IClassFixture<DrawioEditorFixture>
{
    private const string SeedHolder = "browser-test-plant-changed-underneath";
    private const string OtherOperator = "browser-test-the-other-operator";

    private static readonly Guid SourceId = new("a1b2c3d4-0000-4000-8000-000000000201");
    private static readonly Guid TargetId = new("a1b2c3d4-0000-4000-8000-000000000202");

    private const string SourceName = "UnderneathSource";
    private const string TargetName = "UnderneathTarget";

    /// <summary>
    /// Upright and far apart, for the reason <c>DrawioArrowByHandTest</c> writes down: the press
    /// has to land on the shape's own border, and for a rotated shape the box the browser hands
    /// back is the envelope of the rotation. The pair the usual seeding leaves is rotated.
    /// </summary>
    private static readonly PlantItemGeometryDto SourceGeometry =
        new(SourceId, X: 160, Y: 900, Width: 120, Height: 60, Rotation: 0);

    private static readonly PlantItemGeometryDto TargetGeometry =
        new(TargetId, X: 560, Y: 900, Width: 120, Height: 60, Rotation: 0);

    private void DeclareBinary() => output.WriteLine(fixture.Host.Declaration);

    /// <summary>
    /// <b>Another hand deletes the connection while this drawing is open.</b> The plant is the
    /// truth, so the line goes off the canvas at the next opening - and the save has to say so by
    /// name, because the operator is looking at a line that is about to stop existing.
    /// </summary>
    [Fact]
    public async Task AJunctionAnotherHandDeletedIsNotLostInSilence()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await SeedTheUprightPairAsync(client);
        var junction = await JoinThemAsync(client);
        output.WriteLine($"Giunzione seminata: {junction.JunctionId}, {junction.SourceItemId} -> {junction.DestinationItemId}.");

        await using (var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host))
        {
            await editor.WaitForDeviceAsync(SourceName);
            await editor.WaitForDeviceAsync(TargetName);

            var drawn = await editor.EdgesInTheDrawingAsync();
            foreach (var edge in drawn) output.WriteLine($"Sulla tela all'apertura: {edge}.");
            Assert.Contains(drawn, edge => edge.Id == junction.JunctionId.ToString());

            // L'ALTRO OPERATORE, mentre questa pagina e' aperta. Il permesso di scrittura si prende
            // solo durante un salvataggio, quindi qui e' libero e quest'altra sessione lo ottiene
            // esattamente come la otterrebbe una persona su un'altra macchina.
            await DeleteItFromSomewhereElseAsync(client, junction.JunctionId);
            Assert.Empty(await client.GetJunctionsAsync());
            output.WriteLine("L'altro operatore ha cancellato la giunzione. GIUNZIONI SULL'IMPIANTO ADESSO: 0.");

            await editor.SaveAsync();
            Assert.Null(await editor.SaveErrorAsync());
            var report = await editor.ReportAsync();
            output.WriteLine($"Verbale del salvataggio:{Environment.NewLine}{report}");

            var refusals = await editor.RefusalsAsync();
            var notices = await editor.NoticesAsync();
            output.WriteLine($"RIFIUTI: {refusals.Count} | VOCI: {notices.Count}");

            Assert.True(
                refusals.Concat(notices).Any(line => line.Contains(junction.JunctionId.ToString(), StringComparison.OrdinalIgnoreCase)),
                $"L'impianto ha perso la giunzione {junction.JunctionId} mentre il disegno era aperto, la riga sparisce dalla tela" +
                $" dell'operatore, e il verbale non la nomina.{Environment.NewLine}Il verbale diceva:{Environment.NewLine}{report}");
        }

        // E LA TELA LO CONFERMA: la riga non c'e' piu'. Non e' il difetto - l'impianto e' la
        // verita' e un altro l'ha tolta - e' la ragione per cui il silenzio di sopra costa.
        await PlantSeed.WaitForTheEditPermitToBeFreeAsync(client, SeedHolder);
        await using var reopened = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await reopened.WaitForDeviceAsync(SourceName);
        var afterwards = await reopened.EdgesInTheDrawingAsync();
        foreach (var edge in afterwards) output.WriteLine($"Alla riapertura: {edge}.");
        Assert.DoesNotContain(afterwards, edge => edge.Id == junction.JunctionId.ToString());
    }

    /// <summary>
    /// <b>The arrow drawn in THIS session is closed into a loop, before the page has been
    /// rebuilt.</b> An arrow from a device to itself is not a connection, so the junction it had
    /// goes - and AFTER a reload the save says so by name and even keeps the junction. Before a
    /// reload the canvas still carries draw.io's identifier for the arrow while the plant carries
    /// the host's, and the same gesture took the junction off the plant with nothing written
    /// anywhere.
    /// </summary>
    /// <remarks>
    /// The loop, and not the head taken off, and the difference is itself a measure: with the head
    /// removed BETWEEN TWO DEVICES the head rule fires and the save announces the deletion by name
    /// even before a reload (measured on the real binary: 0 refusals, 1 notice). The gestures that
    /// leave the two ends no longer two DIFFERENT devices - the loop, an end let go on a note, an
    /// end detached - are the ones nobody was told about.
    /// </remarks>
    [Fact]
    public async Task TheLoopClosedOnAnArrowOfThisSessionIsAnnounced()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await SeedTheUprightPairAsync(client);
        Assert.Empty(await client.GetJunctionsAsync());

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(SourceName);
        await editor.WaitForDeviceAsync(TargetName);

        // Tirata con il mouse: la cella la fabbrica draw.io, quindi non porta nessun marchio e il
        // suo identificativo e' quello di draw.io. E' l'unica via che riempie la mappa di sessione.
        var pulled = await editor.DragAnArrowByHandAsync(SourceName, TargetName, DrawioArrowLanding.OnThePort);
        output.WriteLine($"Il gesto: {pulled.Account}.");
        foreach (var edge in pulled.NewEdges) output.WriteLine($"Freccia nata: {edge}.");
        Assert.False(pulled.SourceShapeMoved,
            $"La pressione sul bordo ha SPOSTATO la sagoma invece di agganciare una freccia: {pulled.Account}.");
        var arrow = Assert.Single(pulled.NewEdges);
        Assert.True(arrow.Source == SourceId.ToString() && arrow.Target == TargetId.ToString(),
            $"La freccia nata dal gesto non collega i due dispositivi: {arrow}.");
        var arrowId = arrow.Id;
        output.WriteLine($"Freccia tirata a mano: cella {arrowId}.");

        await editor.SaveAsync();
        Assert.Null(await editor.SaveErrorAsync());
        output.WriteLine($"Verbale del primo salvataggio:{Environment.NewLine}{await editor.ReportAsync()}");

        var born = Assert.Single(await client.GetJunctionsAsync());
        output.WriteLine($"Giunzione nata in questa sessione: {born.JunctionId} (la cella resta {arrowId}).");

        // SENZA RICARICARE LA PAGINA: e' la meta' della condizione che conta. La cella porta ancora
        // l'identificativo di draw.io, e solo la mappa della sessione sa che quella freccia e'
        // quella giunzione.
        var ends = await editor.HookOneEndOntoAsync(arrowId, SourceId.ToString());
        output.WriteLine($"Capi dopo il riaggancio: {ends[0]} / {ends[1]}.");
        Assert.Equal(SourceId.ToString(), ends[1]);

        await editor.SaveAsync();
        Assert.Null(await editor.SaveErrorAsync());
        var report = await editor.ReportAsync();
        output.WriteLine($"Verbale del secondo salvataggio:{Environment.NewLine}{report}");

        var refusals = await editor.RefusalsAsync();
        var notices = await editor.NoticesAsync();
        output.WriteLine($"RIFIUTI: {refusals.Count} | VOCI: {notices.Count}");
        output.WriteLine($"GIUNZIONI SULL'IMPIANTO ADESSO: {(await client.GetJunctionsAsync()).Count}");

        Assert.True(
            refusals.Concat(notices).Any(line =>
                line.Contains(born.JunctionId.ToString(), StringComparison.OrdinalIgnoreCase)
                || line.Contains(arrowId, StringComparison.OrdinalIgnoreCase)),
            $"La freccia di questa sessione e' stata chiusa a cappio, la giunzione {born.JunctionId} e' uscita" +
            $" dall'impianto, e il verbale non nomina ne' la giunzione ne' la cella {arrowId}." +
            $"{Environment.NewLine}Il verbale diceva:{Environment.NewLine}{report}");
    }

    /// <summary>
    /// <b>Un altro toglie la connessione, e la freccia che l'operatore ha tirato resta sua: al
    /// salvataggio dopo la rifa'.</b> Decisione del proprietario del 20/9 - niente esce dal disegno
    /// di qualcuno per la decisione di un altro, e fra due dispositivi una freccia con la punta
    /// <i>e'</i> un collegamento.
    /// </summary>
    /// <remarks>
    /// <b>Quello che misura sono le DUE VOCI a un salvataggio di distanza.</b> La prima diceva che
    /// la riga stava per uscire dal disegno e che per ricollegare bisognava tirare una freccia
    /// nuova; poi il salvataggio dopo la connessione la rifaceva da se'. Le due si contraddicevano,
    /// e chi legge un verbale che si smentisce da solo smette di leggerlo.
    /// </remarks>
    [Fact]
    public async Task TheOperatorsArrowStaysAndTheNextSaveConnectsAgain()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await SeedTheUprightPairAsync(client);
        Assert.Empty(await client.GetJunctionsAsync());

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(SourceName);
        await editor.WaitForDeviceAsync(TargetName);

        var pulled = await editor.DragAnArrowByHandAsync(SourceName, TargetName, DrawioArrowLanding.OnThePort);
        Assert.False(pulled.SourceShapeMoved, $"Il gesto ha spostato la sagoma: {pulled.Account}.");
        var arrow = Assert.Single(pulled.NewEdges);
        output.WriteLine($"Freccia tirata a mano: cella {arrow.Id}.");

        await editor.SaveAsync();
        Assert.Null(await editor.SaveErrorAsync());
        var born = Assert.Single(await client.GetJunctionsAsync());
        output.WriteLine($"Giunzione nata: {born.JunctionId}.");

        // L'ALTRO OPERATORE, mentre questa pagina e' aperta. Si aspetta che il permesso torni
        // libero, ed e' esattamente la premessa di questo scenario: dura sessanta secondi e si
        // prende solo mentre si salva, quindi fra due salvataggi distanti piu' di un minuto un
        // altro lo ottiene legittimamente. Senza questa attesa la cancellazione prende un 423 e la
        // prova misurerebbe il permesso invece del disegno.
        await PlantSeed.WaitForTheEditPermitToBeFreeAsync(client, OtherOperator);
        await DeleteItFromSomewhereElseAsync(client, born.JunctionId);
        Assert.Empty(await client.GetJunctionsAsync());
        output.WriteLine("L'altro operatore l'ha cancellata. GIUNZIONI SULL'IMPIANTO ADESSO: 0.");

        // --- PRIMO SALVATAGGIO DOPO ---
        await editor.SaveAsync();
        Assert.Null(await editor.SaveErrorAsync());
        var firstReport = await editor.ReportAsync();
        output.WriteLine($"VERBALE DEL PRIMO SALVATAGGIO:{Environment.NewLine}{firstReport}");
        var firstNotices = await editor.NoticesAsync();
        foreach (var line in firstNotices) output.WriteLine($"VOCE 1: {line}");

        Assert.True(firstNotices.Any(line =>
                line.Contains("still on your canvas", StringComparison.Ordinal)
                && line.Contains("The next save makes the connection again", StringComparison.Ordinal)),
            $"La prima voce non dice all'operatore che la sua freccia resta e che il salvataggio dopo rifa' la" +
            $" connessione: le due voci a un salvataggio di distanza si contraddicono.{Environment.NewLine}{firstReport}");

        Assert.False(firstNotices.Any(line => line.Contains("goes off your drawing", StringComparison.Ordinal)),
            $"La prima voce dice ancora che la riga esce dal disegno, e la freccia dell'operatore invece resta." +
            $"{Environment.NewLine}{firstReport}");

        // E LA FRECCIA E' ANCORA SULLA TELA, che e' la decisione per intero.
        Assert.Contains(await editor.EdgesInTheDrawingAsync(), edge => edge.Id == arrow.Id);
        Assert.Empty(await client.GetJunctionsAsync());

        // --- SECONDO SALVATAGGIO, senza toccare niente ---
        await editor.SaveAsync();
        Assert.Null(await editor.SaveErrorAsync());
        var secondReport = await editor.ReportAsync();
        output.WriteLine($"VERBALE DEL SECONDO SALVATAGGIO:{Environment.NewLine}{secondReport}");
        foreach (var line in await editor.WrittenAsync()) output.WriteLine($"SCRITTO 2: {line}");
        foreach (var line in await editor.NoticesAsync()) output.WriteLine($"VOCE 2: {line}");

        var again = await client.GetJunctionsAsync();
        output.WriteLine($"GIUNZIONI SULL'IMPIANTO DOPO IL SECONDO SALVATAGGIO: {again.Count}.");
        var remade = Assert.Single(again);
        Assert.Equal(SourceId, remade.SourceItemId);
        Assert.Equal(TargetId, remade.DestinationItemId);
    }

    /// <summary>
    /// <b>Un altro operatore cancella un DISPOSITIVO mentre la pagina e' aperta.</b> La sagoma esce
    /// dalla tela - l'impianto e' la verita' - e con lei se ne vanno le connessioni che ci pendevano:
    /// il verbale deve dire tutte e due le cose, per nome.
    /// </summary>
    /// <remarks>
    /// E' la quarta cosa che nessuna rete guardava: il lotto 1 aveva messo il confronto fra le due
    /// fotografie sul braccio delle GIUNZIONI e mai su quello dei dispositivi. Un dispositivo che
    /// sparisce vale piu' di una linea - si porta via la sua configurazione, la sua mappa delle
    /// porte e ogni collegamento che ci pende.
    /// </remarks>
    [Fact]
    public async Task ADeviceAnotherHandDeletedIsNotLostInSilence()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await SeedTheUprightPairAsync(client);
        var junction = await JoinThemAsync(client);
        output.WriteLine($"Giunzione seminata: {junction.JunctionId}.");

        await using (var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host))
        {
            await editor.WaitForDeviceAsync(SourceName);
            await editor.WaitForDeviceAsync(TargetName);

            // L'ALTRO OPERATORE: cancella il dispositivo di partenza, e la giunzione gli va dietro.
            await client.AcquireEditLockAsync(OtherOperator);
            client.EditHolder = OtherOperator;
            try { await client.DeleteItemAsync(SourceId); }
            finally { client.EditHolder = null; await client.ReleaseEditLockAsync(OtherOperator); }

            output.WriteLine($"L'altro operatore ha cancellato {SourceName}." +
                $" DISPOSITIVI: {(await client.GetPlantGraphAsync()).Items.Count} | GIUNZIONI: {(await client.GetJunctionsAsync()).Count}");

            await editor.SaveAsync();
            Assert.Null(await editor.SaveErrorAsync());
            var report = await editor.ReportAsync();
            output.WriteLine($"Verbale:{Environment.NewLine}{report}");

            var notices = await editor.NoticesAsync();
            foreach (var line in notices) output.WriteLine($"VOCE: {line}");

            Assert.True(notices.Any(line => line.Contains(SourceName, StringComparison.Ordinal)
                    && line.Contains("deleted on the host by somebody else", StringComparison.Ordinal)),
                $"Il dispositivo {SourceName} e' uscito dall'impianto mentre il disegno era aperto e il verbale non lo" +
                $" nomina: la sagoma sparisce dalla tela in silenzio.{Environment.NewLine}{report}");

            Assert.True(notices.Any(line => line.Contains(SourceName, StringComparison.Ordinal)
                    && line.Contains("hanging from it went with it", StringComparison.Ordinal)),
                $"Il verbale non dice che il collegamento e' andato via con il dispositivo." +
                $"{Environment.NewLine}{report}");
        }

        // E LA TELA LO CONFERMA.
        await PlantSeed.WaitForTheEditPermitToBeFreeAsync(client, SeedHolder);
        await using var reopened = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await reopened.WaitForDeviceAsync(TargetName);
        var marked = await reopened.MarkedCellIdsAsync();
        foreach (var id in marked) output.WriteLine($"Alla riapertura, dispositivo: {id}.");
        Assert.DoesNotContain(SourceId.ToString(), marked);
    }

    /// <summary>
    /// <b>The same gesture, saved TWICE: the device leaves the drawing at the FIRST save</b> - the
    /// owner's decision of 27/9 (coda dei lavori voce 7), after this very test had measured the
    /// opposite on 2026-09-20: the cell stayed, and from the second save it was an orphan - a cell
    /// marked as a device the plant does not know, refused at every save until the operator deleted it
    /// by hand.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What it measures, in the real page against the real host: after ONE save the device's cell and
    /// the arrow of its junction are off the canvas and the report names the device; that removal is
    /// NOT in draw.io's undo history, so draw.io's own undo does not bring it back; and the second
    /// save has nothing left to say about it.
    /// </para>
    /// <para>
    /// The "Leave site?" half of the same removal is its sister,
    /// <see cref="TakingADeletedDeviceOffTheCanvasDoesNotAskBeforeLeaving"/>: a close ends the page,
    /// and this one needs the page for a second save.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ADeviceAnotherHandDeletedLeavesTheDrawingAtTheFirstSave()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await SeedTheUprightPairAsync(client);
        var junction = await JoinThemAsync(client);

        await using (var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host))
        {
            await editor.WaitForDeviceAsync(SourceName);
            await editor.WaitForDeviceAsync(TargetName);
            Assert.True(await editor.HasCellAsync(junction.JunctionId.ToString()),
                $"La freccia della giunzione {junction.JunctionId} non e' sulla tela all'apertura: la prova non misura quello che dichiara.");

            // A GESTURE OF THE OPERATOR'S before the save, so that the undo below has something of
            // his to undo (fix round, MINOR 4): an empty history would let draw.io's undo do nothing
            // and prove nothing about the removal.
            var targetAtOpening = await editor.CellPositionAsync(TargetId.ToString());
            await editor.DragDeviceAsync(TargetName, 0, -80);
            var targetMoved = await editor.CellPositionAsync(TargetId.ToString());
            Assert.True(Math.Abs(targetMoved[1] - targetAtOpening[1]) > 1,
                $"Il trascinamento non ha spostato {TargetName} nel modello di draw.io ({targetAtOpening[1]:0.#} -> {targetMoved[1]:0.#}).");

            await DeleteTheSourceFromSomewhereElseAsync(client);

            var undoBefore = await editor.UndoDepthAsync();
            Assert.True(undoBefore[0] >= 1,
                $"Il trascinamento non ha messo niente nella pila dell'annulla ([{undoBefore[0]},{undoBefore[1]}]): l'annulla qui sotto non proverebbe niente.");
            await editor.SaveAsync();
            Assert.Null(await editor.SaveErrorAsync());
            var firstReport = await editor.ReportAsync();
            output.WriteLine($"PRIMO SALVATAGGIO:{Environment.NewLine}{firstReport}");

            // OFF THE CANVAS, the device and the arrow of the junction that went with it - and
            // nothing else: the other device is still there.
            Assert.False(await editor.HasCellAsync(SourceId.ToString()),
                $"Dopo il primo salvataggio la sagoma di {SourceName}, che un altro ha cancellato dall'impianto, e' ancora sulla tela." +
                $"{Environment.NewLine}{firstReport}");
            Assert.False(await editor.HasCellAsync(junction.JunctionId.ToString()),
                $"La freccia della giunzione {junction.JunctionId}, andata via con {SourceName}, e' ancora sulla tela.{Environment.NewLine}{firstReport}");
            Assert.True(await editor.HasCellAsync(TargetId.ToString()), $"Con {SourceName} e' sparito anche {TargetName}.");

            var notices = await editor.NoticesAsync();
            Assert.True(notices.Any(line => line.Contains(SourceName, StringComparison.Ordinal)
                    && line.Contains("removed from the drawing too", StringComparison.Ordinal)),
                $"Il verbale non dice che {SourceName} e' uscito anche dal disegno.{Environment.NewLine}{firstReport}");

            // NOT IN THE UNDO HISTORY: a Ctrl+Z that brought the cell back would bring back the
            // orphan this removes. Read twice - the depth, and draw.io's own undo called for real.
            var undoAfter = await editor.UndoDepthAsync();
            output.WriteLine($"Pila dell'annulla: prima [{undoBefore[0]},{undoBefore[1]}], dopo [{undoAfter[0]},{undoAfter[1]}].");
            output.WriteLine(string.Join(Environment.NewLine, await editor.UndoStackAsync()));
            Assert.Equal(undoBefore, undoAfter);

            // ONE undo: it takes back the operator's own gesture - the target goes back where it
            // was - and does NOT bring back the device the save removed.
            output.WriteLine($"L'annulla di draw.io: {await editor.InvokeActionAsync("undo")}.");
            var targetAfterUndo = await editor.CellPositionAsync(TargetId.ToString());
            Assert.True(Math.Abs(targetAfterUndo[1] - targetAtOpening[1]) < 1,
                $"Un annulla non ha rimesso {TargetName} dov'era: e' a {targetAfterUndo[1]:0.#}, era a {targetAtOpening[1]:0.#} prima del" +
                $" trascinamento e a {targetMoved[1]:0.#} dopo. L'annulla e' andato altrove.");
            Assert.False(await editor.HasCellAsync(SourceId.ToString()),
                $"L'annulla di draw.io ha rimesso sulla tela la sagoma di {SourceName}, un dispositivo che l'impianto non ha piu'.");

            // AND AGAIN, with nothing touched in between: nothing left to say about it.
            await editor.SaveAsync();
            Assert.Null(await editor.SaveErrorAsync());
            var secondReport = await editor.ReportAsync();
            output.WriteLine($"SECONDO SALVATAGGIO:{Environment.NewLine}{secondReport}");
            var said = (await editor.RefusalsAsync()).Concat(await editor.NoticesAsync()).ToList();
            Assert.False(said.Any(line => line.Contains(SourceName, StringComparison.Ordinal)
                    || line.Contains(SourceId.ToString(), StringComparison.OrdinalIgnoreCase)
                    || line.Contains(junction.JunctionId.ToString(), StringComparison.OrdinalIgnoreCase)),
                $"Il secondo salvataggio parla ancora di {SourceName} o della sua giunzione.{Environment.NewLine}{secondReport}");
            Assert.Empty(await editor.RefusalsAsync());

            var after = await client.GetPlantGraphAsync();
            Assert.DoesNotContain(after.Items, item => item.ItemId == SourceId);
            Assert.Contains(after.Items, item => item.ItemId == TargetId);
            output.WriteLine($"IMPIANTO: {after.Items.Count} dispositivi, {after.Junctions.Count} giunzioni.");
        }

        // AND AT THE NEXT OPENING it is not there either: nothing of it went into the decoration.
        await PlantSeed.WaitForTheEditPermitToBeFreeAsync(client, SeedHolder);
        await using var reopened = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await reopened.WaitForDeviceAsync(TargetName);

        var marked = await reopened.MarkedCellIdsAsync();
        var labels = await reopened.LabelsInTheDrawingAsync();
        output.WriteLine($"ALLA RIAPERTURA - marchiate: {string.Join(", ", marked)}");
        output.WriteLine($"ALLA RIAPERTURA - etichette: {string.Join(", ", labels)}");
        Assert.DoesNotContain(SourceId.ToString(), marked);
        Assert.DoesNotContain(SourceName, labels);
        Assert.DoesNotContain(await reopened.EdgesInTheDrawingAsync(), edge => edge.Id == junction.JunctionId.ToString());
    }

    /// <summary>
    /// <b>A free line of the operator's hanging from a device somebody else deleted</b> (queue item
    /// 12): the page takes the device off the canvas and disconnects the line - one end stays where
    /// the device was - but the decoration the SAME save writes to the host is the drawing as it was
    /// BEFORE that removal. What is stored, and what the next opening makes of it, is what this
    /// measures, against the real host.
    /// </summary>
    [Fact]
    public async Task AFreeLineOnADeviceAnotherHandDeletedIsStoredLooseAndIsStillDrawnAtTheNextOpening()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await SeedTheUprightPairAsync(client);

        string line;
        string note;
        await using (var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host))
        {
            await editor.WaitForDeviceAsync(SourceName);
            await editor.WaitForDeviceAsync(TargetName);
            note = await editor.PutAFreeNoteOnTheCanvasAsync("the operator's note");
            line = await editor.DrawAFreeLineAsync(note, SourceId.ToString());
            output.WriteLine($"Linea dell'operatore {line}, dalla nota {note} a {SourceName}: {string.Join(" | ", await editor.EdgeEndsAsync(line))}");

            await DeleteTheSourceFromSomewhereElseAsync(client);

            await editor.SaveAsync();
            Assert.Null(await editor.SaveErrorAsync());
            output.WriteLine($"Verbale:{Environment.NewLine}{await editor.ReportAsync()}");
            Assert.False(await editor.HasCellAsync(SourceId.ToString()),
                $"La sagoma di {SourceName} e' ancora sulla tela: la prova non misura quello che dichiara.");
            var onTheCanvas = await editor.EdgeEndsAsync(line);
            output.WriteLine($"Sulla tela dopo il salvataggio: {string.Join(" | ", onTheCanvas)}");
            Assert.Equal("(none)", onTheCanvas[1]);
        }

        // WHAT THE HOST HOLDS: the line, and none of its ends on the device that is gone.
        var stored = await client.GetPlantDecorationAsync();
        var element = System.Text.RegularExpressions.Regex.Match(stored, $"<mxCell id=\"{System.Text.RegularExpressions.Regex.Escape(line)}\"[^>]*>");
        output.WriteLine($"Nella decorazione dell'host: {(element.Success ? element.Value : "(la linea non c'e')")}");
        output.WriteLine($"Decorazione intera: {stored}");
        Assert.True(element.Success, $"La linea {line} dell'operatore non e' nella decorazione dell'host.{Environment.NewLine}{stored}");
        Assert.DoesNotContain(SourceId.ToString(), element.Value, StringComparison.OrdinalIgnoreCase);

        // AND THE NEXT OPENING DRAWS IT, loose at the end where the device was.
        await PlantSeed.WaitForTheEditPermitToBeFreeAsync(client, SeedHolder);
        await using var reopened = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await reopened.WaitForDeviceAsync(TargetName);
        var atTheNextOpening = await reopened.EdgeEndsAsync(line);
        output.WriteLine($"Alla riapertura: {string.Join(" | ", atTheNextOpening)}");
        // Only the end on the device lets go: the one on the operator's note is still hooked to it.
        Assert.Equal(note, atTheNextOpening[0]);
        Assert.Equal("drawn", atTheNextOpening[^1]);
    }

    /// <summary>
    /// <b>A free line of the operator's hooked onto an ARROW that leaves with a device somebody else
    /// deleted</b> (queue item 18): line on line. The page takes the arrow off the canvas with the
    /// device, and draw.io disconnects the line there; what is measured is what the same save stores
    /// on the host, and what the next opening makes of it.
    /// </summary>
    [Fact]
    public async Task AFreeLineOnAnArrowThatLeavesWithADeviceAnotherHandDeletedIsStillDrawnAtTheNextOpening()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await SeedTheUprightPairAsync(client);
        var junction = await JoinThemAsync(client);
        var arrow = junction.JunctionId.ToString();

        string line;
        string note;
        string[] onTheCanvas;
        await using (var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host))
        {
            await editor.WaitForDeviceAsync(SourceName);
            await editor.WaitForDeviceAsync(TargetName);
            Assert.True(await editor.HasCellAsync(arrow), $"La freccia della giunzione {arrow} non e' sulla tela all'apertura.");
            note = await editor.PutAFreeNoteOnTheCanvasAsync("the operator's note");
            line = await editor.DrawAFreeLineAsync(note, arrow);
            output.WriteLine($"Linea dell'operatore {line}, dalla nota {note} alla freccia {arrow}: {string.Join(" | ", await editor.EdgeEndsAsync(line))}");
            output.WriteLine($"Punti della linea sulla tela: {string.Join(" ; ", await editor.EdgePointsAsync(line))}");
            output.WriteLine($"Punti della freccia sulla tela: {string.Join(" ; ", await editor.EdgePointsAsync(arrow))}");
            Assert.Equal(arrow, (await editor.EdgeEndsAsync(line))[1]);

            await DeleteTheSourceFromSomewhereElseAsync(client);

            await editor.SaveAsync();
            Assert.Null(await editor.SaveErrorAsync());
            output.WriteLine($"Verbale:{Environment.NewLine}{await editor.ReportAsync()}");
            Assert.False(await editor.HasCellAsync(arrow),
                $"La freccia {arrow} e' ancora sulla tela: la prova non misura quello che dichiara.");
            onTheCanvas = await editor.EdgeEndsAsync(line);
            output.WriteLine($"Sulla tela dopo il salvataggio: {string.Join(" | ", onTheCanvas)}");
            Assert.Equal("(none)", onTheCanvas[1]);
        }

        var stored = await client.GetPlantDecorationAsync();
        var element = System.Text.RegularExpressions.Regex.Match(stored,
            $"<mxCell id=\"{System.Text.RegularExpressions.Regex.Escape(line)}\".*?</mxCell>",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        output.WriteLine($"Nella decorazione dell'host: {(element.Success ? element.Value : "(la linea non c'e')")}");
        Assert.True(element.Success, $"La linea {line} dell'operatore non e' nella decorazione dell'host.{Environment.NewLine}{stored}");

        await PlantSeed.WaitForTheEditPermitToBeFreeAsync(client, SeedHolder);
        await using var reopened = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await reopened.WaitForDeviceAsync(TargetName);
        var atTheNextOpening = await reopened.EdgeEndsAsync(line);
        output.WriteLine($"Alla riapertura: {string.Join(" | ", atTheNextOpening)}");
        output.WriteLine($"Punti alla riapertura: {string.Join(" ; ", await reopened.EdgePointsAsync(line))}");

        // Nothing stored hangs from the arrow that is gone, the end on the note is still hooked, and
        // the line is drawn.
        Assert.DoesNotContain(arrow, element.Value, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(note, atTheNextOpening[0]);
        Assert.Equal("drawn", atTheNextOpening[^1]);
        // And where the canvas left it: the middle of the arrow, which for a straight arrow the
        // stored copy works out to the same point.
        Assert.Equal(onTheCanvas[3], atTheNextOpening[3]);
    }

    /// <summary>
    /// <b>The operator takes a device's shape off with the Delete key, and somebody else deletes the
    /// device</b> (queue item 16). draw.io's Delete takes the shape and NOT its connections, so the
    /// arrow of the junction is left on the canvas, loose where the device was: the save's line for
    /// the device says what happens to it, and there is no line telling him to draw a new arrow
    /// between the two devices - one of them no longer exists.
    /// </summary>
    [Fact]
    public async Task TheArrowADeleteLeftLooseIsSaidInTheDevicesLineAndGoesAtTheNextOpening()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await SeedTheUprightPairAsync(client);
        var junction = await JoinThemAsync(client);
        var arrow = junction.JunctionId.ToString();

        await using (var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host))
        {
            await editor.WaitForDeviceAsync(SourceName);
            await editor.WaitForDeviceAsync(TargetName);

            var box = await editor.DeviceBoxAsync(SourceName);
            await editor.ClickAtAsync((float)(box.X + box.Width / 2), (float)(box.Y + box.Height / 2));
            Assert.Equal(1, await editor.SelectCellAsync(SourceId.ToString()));
            await editor.PressDeleteAsync();
            Assert.False(await editor.HasCellAsync(SourceId.ToString()), $"Il Canc non ha tolto la sagoma di {SourceName}.");

            // MEASURED: the arrow is still there, with its end where the device was.
            var ends = await editor.EdgeEndsAsync(arrow);
            output.WriteLine($"Dopo il Canc sulla sagoma di {SourceName}: freccia {arrow} = {string.Join(" | ", ends)}");
            Assert.True(await editor.HasCellAsync(arrow), $"Il Canc ha portato via anche la freccia {arrow}: la prova non misura il caso che dichiara.");
            Assert.Equal("(none)", ends[0]);

            await DeleteTheSourceFromSomewhereElseAsync(client);

            await editor.SaveAsync();
            Assert.Null(await editor.SaveErrorAsync());
            var report = await editor.ReportAsync();
            output.WriteLine($"Verbale:{Environment.NewLine}{report}");
            var said = (await editor.RefusalsAsync()).Concat(await editor.NoticesAsync()).ToList();
            foreach (var line in said) output.WriteLine($"RIGA: {line}");

            Assert.Contains(said, line => line.Contains(SourceName, StringComparison.Ordinal)
                && line.Contains("An arrow of that connection is still on your canvas: it goes off your drawing at the next opening.", StringComparison.Ordinal));
            Assert.DoesNotContain(said, line => line.Contains("draw a new arrow from one to the other", StringComparison.Ordinal));
        }

        // AND THE PROMISE IS KEPT: the arrow is not there at the next opening.
        await PlantSeed.WaitForTheEditPermitToBeFreeAsync(client, SeedHolder);
        await using var reopened = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await reopened.WaitForDeviceAsync(TargetName);
        Assert.False(await reopened.HasCellAsync(arrow), $"Alla riapertura la freccia {arrow} e' ancora sulla tela.");
    }

    /// <summary>
    /// <b>A free line of the operator's hooked onto an arrow whose connection somebody else removed
    /// between two devices that are both still there</b> (queue item 19). The arrow is ours, so it
    /// stays on the canvas for now and goes off the drawing at the next opening; what is measured
    /// is what the save stores for the line, and what the next opening makes of it.
    /// </summary>
    [Fact]
    public async Task AFreeLineOnAnArrowWhoseConnectionAnotherHandRemovedIsStillDrawnAtTheNextOpening()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await SeedTheUprightPairAsync(client);
        var junction = await JoinThemAsync(client);
        var arrow = junction.JunctionId.ToString();

        string line;
        string note;
        string[] whereItMeetsTheArrow;
        await using (var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host))
        {
            await editor.WaitForDeviceAsync(SourceName);
            await editor.WaitForDeviceAsync(TargetName);
            Assert.True(await editor.HasCellAsync(arrow), $"La freccia della giunzione {arrow} non e' sulla tela all'apertura.");
            note = await editor.PutAFreeNoteOnTheCanvasAsync("the operator's note");
            line = await editor.DrawAFreeLineAsync(note, arrow);
            output.WriteLine($"Linea dell'operatore {line}, dalla nota {note} alla freccia {arrow}: {string.Join(" | ", await editor.EdgeEndsAsync(line))}");
            output.WriteLine($"Punti della freccia sulla tela: {string.Join(" ; ", await editor.EdgePointsAsync(arrow))}");

            await PlantSeed.WaitForTheEditPermitToBeFreeAsync(client, OtherOperator);
            await DeleteItFromSomewhereElseAsync(client, junction.JunctionId);
            Assert.Empty(await client.GetJunctionsAsync());

            await editor.SaveAsync();
            Assert.Null(await editor.SaveErrorAsync());
            output.WriteLine($"Verbale:{Environment.NewLine}{await editor.ReportAsync()}");

            // The arrow is still on the canvas, and the line still hangs from it: the case declared.
            Assert.True(await editor.HasCellAsync(arrow), $"La freccia {arrow} non e' piu' sulla tela: la prova non misura quello che dichiara.");
            Assert.Equal(arrow, (await editor.EdgeEndsAsync(line))[1]);
            whereItMeetsTheArrow = await editor.EdgePointsAsync(line);
            output.WriteLine($"Punti della linea sulla tela dopo il salvataggio: {string.Join(" ; ", whereItMeetsTheArrow)}");
        }

        var stored = await client.GetPlantDecorationAsync();
        var element = System.Text.RegularExpressions.Regex.Match(stored,
            $"<mxCell id=\"{System.Text.RegularExpressions.Regex.Escape(line)}\".*?</mxCell>",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        output.WriteLine($"Nella decorazione dell'host: {(element.Success ? element.Value : "(la linea non c'e')")}");
        Assert.True(element.Success, $"La linea {line} dell'operatore non e' nella decorazione dell'host.{Environment.NewLine}{stored}");

        await PlantSeed.WaitForTheEditPermitToBeFreeAsync(client, SeedHolder);
        await using var reopened = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await reopened.WaitForDeviceAsync(TargetName);
        Assert.False(await reopened.HasCellAsync(arrow), $"Alla riapertura la freccia {arrow} e' ancora sulla tela.");
        var atTheNextOpening = await reopened.EdgeEndsAsync(line);
        output.WriteLine($"Alla riapertura: {string.Join(" | ", atTheNextOpening)}");
        output.WriteLine($"Punti alla riapertura: {string.Join(" ; ", await reopened.EdgePointsAsync(line))}");

        // Nothing stored hangs from the arrow that goes, the end on the note is still hooked, the
        // line is drawn - and its loose end is where it met the arrow on the canvas: the middle of
        // the arrow.
        Assert.DoesNotContain(arrow, element.Value, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(note, atTheNextOpening[0]);
        Assert.Equal("drawn", atTheNextOpening[^1]);
        Assert.Equal("420,930", whereItMeetsTheArrow[^1]);
        Assert.Equal("420,930", atTheNextOpening[3]);
    }

    /// <summary>
    /// <b>The same line on the arrow a plain Delete left loose</b> (queue items 16 and 19): the
    /// operator takes the device's shape off with Delete, the arrow stays on the canvas with that end
    /// in the air, he hooks a free line onto it, and somebody else deletes the device. The arrow goes
    /// off the drawing at the next opening; the line must not go with it.
    /// </summary>
    [Fact]
    public async Task AFreeLineOnTheArrowADeleteLeftLooseIsStillDrawnAtTheNextOpening()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await SeedTheUprightPairAsync(client);
        var junction = await JoinThemAsync(client);
        var arrow = junction.JunctionId.ToString();

        string line;
        string note;
        string[] whereItMeetsTheArrow;
        await using (var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host))
        {
            await editor.WaitForDeviceAsync(SourceName);
            await editor.WaitForDeviceAsync(TargetName);

            var box = await editor.DeviceBoxAsync(SourceName);
            await editor.ClickAtAsync((float)(box.X + box.Width / 2), (float)(box.Y + box.Height / 2));
            Assert.Equal(1, await editor.SelectCellAsync(SourceId.ToString()));
            await editor.PressDeleteAsync();
            Assert.False(await editor.HasCellAsync(SourceId.ToString()), $"Il Canc non ha tolto la sagoma di {SourceName}.");
            var arrowEnds = await editor.EdgeEndsAsync(arrow);
            output.WriteLine($"Dopo il Canc: freccia {arrow} = {string.Join(" | ", arrowEnds)}");
            output.WriteLine($"Punti della freccia sulla tela: {string.Join(" ; ", await editor.EdgePointsAsync(arrow))}");
            Assert.Equal("(none)", arrowEnds[0]);

            note = await editor.PutAFreeNoteOnTheCanvasAsync("the operator's note");
            line = await editor.DrawAFreeLineAsync(note, arrow);
            output.WriteLine($"Linea dell'operatore {line}, dalla nota {note} alla freccia {arrow}: {string.Join(" | ", await editor.EdgeEndsAsync(line))}");

            await DeleteTheSourceFromSomewhereElseAsync(client);

            await editor.SaveAsync();
            Assert.Null(await editor.SaveErrorAsync());
            output.WriteLine($"Verbale:{Environment.NewLine}{await editor.ReportAsync()}");

            Assert.True(await editor.HasCellAsync(arrow), $"La freccia {arrow} non e' piu' sulla tela: la prova non misura quello che dichiara.");
            Assert.Equal(arrow, (await editor.EdgeEndsAsync(line))[1]);
            whereItMeetsTheArrow = await editor.EdgePointsAsync(line);
            output.WriteLine($"Punti della linea sulla tela dopo il salvataggio: {string.Join(" ; ", whereItMeetsTheArrow)}");
        }

        var stored = await client.GetPlantDecorationAsync();
        var element = System.Text.RegularExpressions.Regex.Match(stored,
            $"<mxCell id=\"{System.Text.RegularExpressions.Regex.Escape(line)}\".*?</mxCell>",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        output.WriteLine($"Nella decorazione dell'host: {(element.Success ? element.Value : "(la linea non c'e')")}");
        Assert.True(element.Success, $"La linea {line} dell'operatore non e' nella decorazione dell'host.{Environment.NewLine}{stored}");

        await PlantSeed.WaitForTheEditPermitToBeFreeAsync(client, SeedHolder);
        await using var reopened = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await reopened.WaitForDeviceAsync(TargetName);
        Assert.False(await reopened.HasCellAsync(arrow), $"Alla riapertura la freccia {arrow} e' ancora sulla tela.");
        var atTheNextOpening = await reopened.EdgeEndsAsync(line);
        output.WriteLine($"Alla riapertura: {string.Join(" | ", atTheNextOpening)}");
        output.WriteLine($"Punti alla riapertura: {string.Join(" ; ", await reopened.EdgePointsAsync(line))}");

        Assert.DoesNotContain(arrow, element.Value, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(note, atTheNextOpening[0]);
        Assert.Equal("drawn", atTheNextOpening[^1]);
        // The arrow runs from where the Delete left its end (275,930) to the target's port
        // (565,930): the line met it, and lets go of it, in its middle.
        Assert.Equal("420,930", whereItMeetsTheArrow[^1]);
        Assert.Equal("420,930", atTheNextOpening[3]);
    }

    /// <summary>
    /// <b>Taking a deleted device off the canvas is not work the operator could lose</b>: the canvas
    /// is being put back in line with the plant, so closing the tab right after that save must not
    /// ask "Leave site?".
    /// </summary>
    /// <remarks>
    /// The save itself puts the mark down - it wrote everything the drawing asked, and a notice
    /// does not stand in the way - but it counts the changes handed over BEFORE the removal, which
    /// comes later in the same round. So a removal counted as an edit of the operator's would be one
    /// change past the count, and the close would ask.
    /// </remarks>
    [Fact]
    public async Task TakingADeletedDeviceOffTheCanvasDoesNotAskBeforeLeaving()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await SeedTheUprightPairAsync(client);
        await JoinThemAsync(client);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(SourceName);
        await editor.WaitForDeviceAsync(TargetName);

        await DeleteTheSourceFromSomewhereElseAsync(client);

        await editor.SaveAsync();
        Assert.Null(await editor.SaveErrorAsync());
        output.WriteLine($"Verbale:{Environment.NewLine}{await editor.ReportAsync()}");
        Assert.False(await editor.HasCellAsync(SourceId.ToString()),
            $"La sagoma di {SourceName} e' ancora sulla tela: la chiusura qui sotto non misurerebbe la sua rimozione.");

        var closing = await editor.CloseTheTabAsync();
        output.WriteLine($"Chiusura: {closing}");
        Assert.False(closing.Asked,
            $"Chiudendo la scheda dopo il salvataggio che ha tolto {SourceName} dalla tela il browser ha chiesto \"Leave site?\"" +
            $" ({closing.DialogType}): la rimozione e' stata contata come lavoro dell'operatore, e non c'era niente da perdere.");
    }

    /// <summary>The other operator, on another machine: deletes the source device, and the host takes its junction with it.</summary>
    private async Task DeleteTheSourceFromSomewhereElseAsync(WcsClient client)
    {
        await PlantSeed.WaitForTheEditPermitToBeFreeAsync(client, OtherOperator);
        await client.AcquireEditLockAsync(OtherOperator);
        client.EditHolder = OtherOperator;
        try { await client.DeleteItemAsync(SourceId); }
        finally { client.EditHolder = null; await client.ReleaseEditLockAsync(OtherOperator); }

        var now = await client.GetPlantGraphAsync();
        output.WriteLine($"L'altro operatore ha cancellato {SourceName}. DISPOSITIVI: {now.Items.Count} | GIUNZIONI: {now.Junctions.Count}");
        Assert.DoesNotContain(now.Items, item => item.ItemId == SourceId);
        Assert.Empty(now.Junctions);
    }

    /// <summary>
    /// The plant these two work on: the usual reset, which empties it, and then the two upright
    /// devices the seeding does not carry.
    /// </summary>
    private static async Task SeedTheUprightPairAsync(WcsClient client)
    {
        await PlantSeed.ResetAsync(client, SeedHolder);
        await PlantSeed.AddDeviceAsync(client, SeedHolder, SourceId, SourceName, DeviceTypes.Automation, SourceGeometry);
        await PlantSeed.AddDeviceAsync(client, SeedHolder, TargetId, TargetName, DeviceTypes.Automation, TargetGeometry);
    }

    /// <summary>The connection the first of the two starts from, written before the page is opened.</summary>
    private static async Task<JunctionDto> JoinThemAsync(WcsClient client)
    {
        await client.AcquireEditLockAsync(SeedHolder);
        client.EditHolder = SeedHolder;
        try
        {
            return await client.AddJunctionAsync(new JunctionRequest(SourceId, TargetId, 99));
        }
        finally
        {
            client.EditHolder = null;
            await client.ReleaseEditLockAsync(SeedHolder);
        }
    }

    /// <summary>
    /// Somebody else, on another machine: takes the edit permit, removes the junction, gives the
    /// permit back. It is what the permit is for, and this is the only way to measure what the
    /// save does when the plant it was opened from is no longer the plant it is writing to.
    /// </summary>
    private static async Task DeleteItFromSomewhereElseAsync(WcsClient client, Guid junctionId)
    {
        await client.AcquireEditLockAsync(OtherOperator);
        client.EditHolder = OtherOperator;
        try
        {
            await client.DeleteJunctionAsync(junctionId);
        }
        finally
        {
            client.EditHolder = null;
            await client.ReleaseEditLockAsync(OtherOperator);
        }
    }
}
