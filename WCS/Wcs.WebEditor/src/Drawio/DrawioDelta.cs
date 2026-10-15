using System.Globalization;
using System.Xml.Linq;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Drawing;
using Wcs.WebEditor.Editing;
using Wcs.WebEditor.Model;

namespace Wcs.WebEditor.Drawio;

/// <summary>Qualcosa che il disegno chiedeva e che NON e' stato scritto, con il nome di chi e il perche'.</summary>
public sealed record DrawioRefusal(string Id, string Reason)
{
    /// <summary>
    /// Quello che e' tornato da una richiesta fallita di cui il perche' parla - la risposta dell'host
    /// cosi' come e' arrivata, o tipo e messaggio del guasto - dietro la «?» della riga (voce 37 della
    /// coda, estesa a tutta la pagina il 10/10); null se la riga non parla di una richiesta fallita.
    /// </summary>
    public string? Detail { get; init; }
}

/// <summary>
/// Qualcosa che il disegno chiedeva, che <b>e' stato</b> scritto, ma non esattamente come chi
/// disegnava potrebbe aspettarsi: una scelta che il codice ha dovuto fare al posto suo (la
/// priorita' di una freccia nuova, che il disegno non dichiara) o un rimedio che cambia
/// l'identita' di quello che tocca (una giunzione riagganciata, rifatta come cancellazione piu'
/// creazione). Non e' un rifiuto - la differenza conta per chi legge - ma tacerlo sarebbe
/// esattamente il difetto D203: una scelta presa in silenzio al posto dell'operatore.
/// </summary>
public sealed record DrawioNotice(string Id, string Message)
{
    /// <summary>Come <see cref="DrawioRefusal.Detail"/>: quello che e' tornato, dietro la «?» della riga.</summary>
    public string? Detail { get; init; }
}

/// <summary>
/// Una giunzione da creare: gli estremi e la priorita' intera che l'host si aspetta
/// (<c>JunctionRequest</c>). <paramref name="Id"/> e' l'identificativo della CELLA del disegno, non
/// della giunzione - quello lo assegna l'host alla creazione - e serve solo a nominare la freccia
/// nelle voci e nei rifiuti che la riguardano.
/// </summary>
public sealed record DrawioNewJunction(string Id, Guid Source, Guid Destination, int Priority);

/// <summary>
/// The weight of a junction the plant ALREADY has, changed by hand on the drawing: the plant's
/// junction and the priority it has to end up at.
/// </summary>
/// <remarks>
/// <paramref name="JunctionId"/> is the PLANT's identifier, not the drawn cell's - unlike
/// <see cref="DrawioNewJunction"/>, which names a cell precisely because its junction does not exist
/// yet. Here the junction exists, it is what has to be written
/// (<c>PUT /plant/junctions/{id}</c>, the only route that really changes a priority - a second
/// <c>POST</c> on the same pair does not, see <c>AddJunctionCommand</c>), and the drawn identifier
/// would be the wrong name for an arrow the operator recognizes by the two devices it joins.
/// <para>
/// The priority the junction had is deliberately NOT carried here, although the comparison had it in
/// hand. Whoever writes needs it - for the report's "from low to high", and for the undo of
/// <c>SetJunctionPriorityCommand</c> - but it needs the one the PLANT is holding when the write goes
/// out, not the one this delta read at opening: they can differ, and then the two copies say
/// different things and whoever writes has to choose which to believe. It is the same lesson as
/// <c>DrawioChange</c> in the remarks of <see cref="DrawioDeltaResult"/>: one fact, one copy, held
/// where it is true.
/// </para>
/// </remarks>
public sealed record DrawioJunctionPriority(Guid JunctionId, int NewPriority);

/// <summary>
/// Che cosa scrivere, che cosa rifiutare, che cosa dichiarare.
/// </summary>
/// <remarks>
/// <paramref name="Moves"/> porta direttamente le geometrie da scrivere, non un record che
/// ripeterebbe l'identificativo accanto a loro: <c>PlantItemGeometryDto.ItemId</c> e' gia'
/// l'identificativo del dispositivo, ed e' quello che l'host legge. Il record
/// <c>DrawioChange(Guid ItemId, PlantItemGeometryDto Geometry)</c> del compito 4 portava lo stesso
/// dato due volte (punto e del compito 7b): due copie che nessuno teneva allineate, e chi scrive
/// doveva scegliere quale credere. Tolta quella del record, resta quella che l'host usa davvero.
/// <para>
/// <paramref name="DecorationXml"/> is <b>not</b> the reader's decoration verbatim, and it is
/// where this file writes the operator's free drawing instead of only reading it - in TWO ways. An
/// unmarked arrow the reader proposed as a junction and this delta did not write goes back into it:
/// see the round at the end of <c>Compute</c> for why the answer can only be given here. And a line
/// that stays with an end on a device cell the page takes off the canvas is rewritten there, that
/// end let go and left where the device was (<c>LetGoOfTheCellsThatLeave</c>, queue item 12) - or,
/// for an end on the ARROW of one of its junctions, where the line met that arrow (queue item 18).
/// </para>
/// </remarks>
public sealed record DrawioDeltaResult(
    IReadOnlyList<PlantItemGeometryDto> Moves,
    IReadOnlyList<Guid> Deleted,
    IReadOnlyList<DrawioCell> NewDevices,
    IReadOnlyList<DrawioNewJunction> NewJunctions,
    IReadOnlyList<Guid> DeletedJunctions,
    IReadOnlyList<DrawioJunctionPriority> JunctionPriorities,
    IReadOnlyList<DrawioRefusal> Refusals,
    IReadOnlyList<DrawioNotice> Notices,
    string DecorationXml)
{
    /// <summary>
    /// The devices somebody else deleted from the plant whose cells this drawing still carries, with
    /// the cells the page takes off the canvas for them at this save (coda dei lavori voce 7). Nothing
    /// else of this result speaks of them: their line waits for the canvas's answer - see
    /// <see cref="DrawioDeviceGone"/>.
    /// </summary>
    public IReadOnlyList<DrawioDeviceGone> GoneFromThePlant { get; init; } = [];
}

/// <summary>
/// Che cosa si scrive e che cosa si rifiuta (decisione 3.12). Il confronto e' a tre: il disegno che
/// torna, l'impianto com'era quando il disegno e' stato APERTO, e l'impianto com'e' ADESSO. Un
/// dispositivo che e' cambiato da entrambe le parti non si scrive e viene nominato: rifiutare tutto
/// in blocco farebbe perdere lavoro che nessun altro aveva toccato.
/// </summary>
/// <remarks>
/// Quattro regole tengono in piedi il resto, e ognuna esiste perche' la via comoda fa sparire
/// qualcosa in silenzio.
/// <para>
/// <b>Il disegno non parla lo stesso linguaggio dell'impianto.</b> L'impianto memorizza l'inviluppo
/// della sagoma GIA' ruotata (<c>X</c>/<c>Y</c>/<c>Width</c>/<c>Height</c> di
/// <c>PlantItemDto</c>); draw.io ruota da se', e <see cref="DrawioDocument"/> gli scrive quindi la
/// dimensione PROPRIA del dispositivo centrata sul centro del mondo
/// (<see cref="NodePlacement.OwnBox"/>). A 35 gradi un dispositivo 130x184 nell'impianto e' meno di
/// 3 unita' di larghezza nel disegno: confrontare i due riquadri numero per numero direbbe
/// "spostato" per ogni dispositivo ruotato, e riscrivere i numeri letti cosi' come sono
/// schiaccerebbe l'impianto intero senza un errore e senza un rifiuto. Percio' qui il confronto si
/// fa nello spazio del DISEGNO - il riquadro letto contro quello che
/// <see cref="DrawioDocument"/> aveva scritto per lo stesso dispositivo - e la geometria da
/// scrivere si riporta nello spazio dell'IMPIANTO con <see cref="NodePlacement.EnvelopeBox"/>,
/// l'inverso esatto.
/// </para>
/// <para>
/// <b>"Manca dal disegno" non vuol dire "cancellato".</b> Un dispositivo non collocato, o di un
/// tipo che il registro non dichiara, nel disegno non c'e' mai arrivato: si chiede a
/// <see cref="DrawioDocument.IsDrawn(PlantGraph, PlantItemDto)"/> - la stessa domanda che decide
/// l'andata - prima di leggere un'assenza come una cancellazione.
/// </para>
/// <para>
/// <b>Ogni cella che non sappiamo trattare esce con un nome.</b> Le celle marchiate che il lettore
/// non riconosce (<see cref="DrawioContents.Unknown"/>), le frecce senza agganci, i dispositivi
/// spariti dall'host, le giunzioni riagganciate altrove, le celle specchiate (dove l'impianto non
/// ha dove tenere lo specchio): nessuna viene ignorata in silenzio. Chi salva deve poter leggere
/// che cosa non e' passato.
/// </para>
/// <para>
/// <b>A cell that is not on the canvas is refused, not composed.</b> In the mxGraph format the
/// geometry of a cell inside a group is relative TO THE GROUP. Measured on 2026-09-16 in a real
/// browser: two devices grouped with Ctrl+G and dragged by +180/+110 pixels came back relative to
/// the group - the first child with no <c>x</c> and no <c>y</c> at all - and read as absolute they
/// were written to the plant at (-6.96, -25.98) and (413.05, 244.02), moving the opposite way from
/// the gesture and off the plant, while the report said "Save succeeded: 2 writes". So a marked cell
/// whose <c>parent</c> is not <see cref="DrawioDocument.DefaultLayerId"/> is refused by name.
/// <b>Composing</b> the geometry - adding the group's origin - is deliberately NOT done here: a
/// group can be rotated, and then the composition is an affine transform and not a sum. That is a
/// piece of work of its own; what had to go today is writing wrong numbers while declaring success.
/// The refusal names the remedy that IS measured: dissolve the group (Ctrl+Shift+U) before saving,
/// which puts the cells back on the default layer with absolute geometry
/// (<c>DrawioGroupedCellsTest.MovingAGroupAndThenUngroupingItWritesBothNewPositions</c>).
/// </para>
/// <para>
/// <b>Non si cancella mai cio' che il disegno porta ancora.</b> La difesa di ultima istanza: se
/// l'identificativo di un dispositivo o di una giunzione compare da qualche parte nell'XML tornato
/// indietro - anche solo come decorazione, perche' qualcuno gli ha storpiato o tolto il marchio -
/// la cancellazione diventa un rifiuto nominato. Vale piu' di qualunque caso particolare, perche'
/// copre anche le vie che non abbiamo previsto, ed e' irreversibile cio' che protegge.
/// </para>
/// </remarks>
public static class DrawioDelta
{
    /// <summary>
    /// Quanto si tollera confrontando l'impianto com'era con l'impianto com'e': qui non c'e' nessun
    /// giro attraverso il disegno e nessun arrotondamento di scrittura, i numeri arrivano
    /// dall'host da entrambe le parti. Resta la tolleranza stretta del brief.
    /// </summary>
    private const double PlantTolerance = 0.01;

    /// <summary>
    /// Quanto si tollera confrontando gli agganci alle porte (punto d). Sono frazioni fra 0 e 1, e
    /// <see cref="DrawioDocument"/> le scrive con lo stesso "0.##" di ogni altro numero: 0.005 al
    /// piu' di arrotondamento. Una porta sta al centro (0.5) o su un bordo (0.04, 0.96): due porte
    /// diverse distano molto piu' di questa soglia, e un'inezia sotto di lei non e' un gesto.
    /// </summary>
    private const double AnchorTolerance = 0.01;

    /// <param name="plantJunctionByDrawnEdge">
    /// Le giunzioni nate in QUESTA sessione: l'identificativo che draw.io ha dato alla freccia ->
    /// quello che l'host ha assegnato alla giunzione creandola (<c>JunctionRequest</c> non ne porta
    /// uno). E' l'unico disallineamento di identita' che esista: <see cref="DrawioDocument.Build"/>
    /// scrive ogni giunzione con il proprio <c>JunctionId</c>, quindi dopo un ricaricamento della
    /// pagina la tela porta gia' gli identificativi dell'impianto. Senza questa mappa la freccia
    /// appena creata non si riconosce al salvataggio successivo: la giunzione finisce fra le
    /// cancellazioni e la freccia riparte come nuova - l'identita' ruota a ogni salvataggio, e ogni
    /// giro e' una finestra in cui un guasto la lascia cancellata e non ricreata.
    /// <para>
    /// Entra come PARAMETRO e non come stato di questa classe: il delta e' una funzione, e chi lo
    /// chiama tiene la mappa quanto la sessione (si veda <c>DrawioEditor</c>).
    /// </para>
    /// </param>
    /// <param name="plantItemByDrawnCell">
    /// The devices born from a DROP in this session: the identifier draw.io gave the cell -> the one
    /// the plant gave the device when it was created.
    /// <para>
    /// It is the twin of <paramref name="plantJunctionByDrawnEdge"/> and it exists for the same
    /// reason: <c>DrawioDocument.Build</c> writes every device with its own <c>ItemId</c>, so a
    /// canvas born from a seeding already carries the plant's identifiers - but a cell just dropped
    /// does not, and its identifier is draw.io's, chosen before anybody knew there would be a
    /// device. Without this map the next save would not recognize that cell: the device would end up
    /// among the deletions and the cell among the new ones, that is deleted and refused in the same
    /// breath.
    /// </para>
    /// <para>
    /// The cell is deliberately NOT rewritten with the plant's identifier - that road was closed
    /// with three rounds of defects behind it (see <c>DrawioEditor.SeedDrawioAsync</c>): nothing on
    /// this page sends a document back to draw.io after a write.
    /// </para>
    /// </param>
    public static DrawioDeltaResult Compute(DrawioContents drawn, PlantGraph opened, PlantGraph current,
        IReadOnlyDictionary<string, Guid> plantJunctionByDrawnEdge,
        IReadOnlyDictionary<string, Guid> plantItemByDrawnCell)
    {
        var openedById = opened.Items.ToDictionary(i => i.ItemId);
        var currentById = current.Items.ToDictionary(i => i.ItemId);

        // WHAT ANOTHER HAND TOOK FROM UNDER THIS DRAWING: the junctions the page was opened with
        // that the plant DOES NOT HOLD ANY MORE. Read once, here, because it is the only reason
        // this function takes two snapshots at all - and until somebody compared them the two were
        // one.
        //
        // IT IS NOT A LABORATORY CASE: the edit permit lasts sixty seconds and on /editor/drawio is
        // taken ONLY while saving, so between two saves more than a minute apart another operator
        // legitimately holds it and changes the plant. It is the scenario the permit exists for.
        //
        // Three things follow, and all three are written in terms of this ONE set instead of each
        // working it out again: what is no longer there is not deleted, a duplicate is not refused
        // by naming a junction that does not exist, and above all NOTHING IS PASSED OVER IN
        // SILENCE - the line goes off the operator's canvas and he has to know why.
        var takenAwayByAnotherHand = opened.Junctions
            .Where(junction => current.Junctions.All(now => now.JunctionId != junction.JunctionId))
            .Select(junction => junction.JunctionId)
            .ToHashSet();

        // AND THE SAME FOR THE DEVICES, which the first batch wrote only for the junctions: the arm
        // of ThePlantDrawsItItself that answers about a device went on saying "the plant will draw
        // it" about a device another operator had already removed, so the cell went off the canvas
        // with nobody saying why - and a device takes its configuration, its door map and every
        // junction hanging from it with it.
        var devicesTakenAwayByAnotherHand = opened.Items
            .Where(item => !currentById.ContainsKey(item.ItemId))
            .Select(item => item.ItemId)
            .ToHashSet();

        var moves = new List<PlantItemGeometryDto>();
        var newDevices = new List<DrawioCell>();
        var refusals = new List<DrawioRefusal>();
        var notices = new List<DrawioNotice>();
        var seen = new HashSet<Guid>();
        var drawnJunctionIds = new HashSet<Guid>();

        // La difesa di ultima istanza contro la cancellazione silenziosa, e la ragione per cui sta
        // qui e non dentro un caso particolare: TUTTE le vie per cui una cella nostra smette di
        // essere riconosciuta come tale - il marchio storpiato, il marchio TOLTO a mano (si veda
        // piu' sotto per quale strada ci arriva), un marchio di una categoria futura - fanno arrivare il
        // dispositivo al giro delle cancellazioni come se l'operatore l'avesse cancellato, mentre
        // lui lo vede benissimo sulla tela. Col marchio tolto non resta nemmeno un rifiuto: la
        // cella scende nella decorazione e il delta non la vede affatto. La regola generale -
        // NON SI CANCELLA MAI un dispositivo il cui identificativo compare ancora da qualche parte
        // nell'XML tornato indietro - copre anche le forme che non abbiamo previsto, e vale piu' di
        // qualunque elenco di casi: una cancellazione proposta per un identificativo che il disegno
        // porta ancora diventa un rifiuto nominato. E' il difetto D203 nella sua forma peggiore -
        // un dato che sparisce senza che nessuno lo dica - e qui e' irreversibile.
        //
        // Punto c del compito 7b: il rifiuto dice anche DOVE. Un identificativo rimasto dentro una
        // nota dell'operatore rende quel dispositivo non cancellabile finche' la nota resta li', e
        // fino a questa correzione il messaggio non diceva dove guardare: chi legge si trovava
        // davanti a un rifiuto che non sapeva come togliere. Sapere che l'identificativo e' "nella
        // decorazione" o "in una cella con marchio 'devise'" e' la differenza fra un rimedio di un
        // minuto e un dispositivo bloccato per sempre.
        string? WhereInTheDrawing(Guid id)
        {
            var text = id.ToString();
            // Prima le celle marchiate, che sono il caso piu' preciso: la cella e' ancora una cella
            // nostra storpiata, non una nota qualunque.
            foreach (var cell in drawn.Unknown)
                if (cell.Xml.Contains(text, StringComparison.OrdinalIgnoreCase))
                    return $"it is inside a cell marked '{cell.Mark}' (cell {cell.Id})";

            return drawn.DecorationXml.Contains(text, StringComparison.OrdinalIgnoreCase)
                ? "it is in the decoration, the operator's free drawing (a note, a box, an unmarked shape)"
                : null;
        }

        // THE SECOND IDENTITY, which the last-resort defence did not know about. The question
        // WhereInTheDrawing asks - "does the drawing still carry this thing?" - is the right one,
        // and it asks it with the PLANT'S name, which DrawioDocument.Build writes onto the cell at
        // every reload. Before a reload that name is nowhere in the drawing: the arrow the operator
        // pulled in this session carries the identifier draw.io gave it, and only
        // plantJunctionByDrawnEdge knows the two are one thing. So the defence never fired, and the
        // junction left the plant in silence - measured against the real binary, "RIFIUTI: 0 |
        // VOCI: 0".
        //
        // It is the same shape as the set further up: a right question asked of the wrong thing. It
        // lives HERE and not inside WhereInTheDrawing because the two are not the same question:
        // that one looks for the identifier left SOMEWHERE ELSE (a note, a mangled mark), this one
        // looks for THE CELL ITSELF, which the reader could not recognize. Merging them would let
        // the defence win where the drawing HAS asked for the deletion - see the head guard in the
        // deletion round.
        string? WhereTheArrowOfThisSessionIs(Guid junctionId)
        {
            foreach (var pair in plantJunctionByDrawnEdge)
            {
                if (pair.Value != junctionId) continue;

                foreach (var cell in drawn.Unknown)
                    if (cell.Xml.Contains(pair.Key, StringComparison.OrdinalIgnoreCase))
                        return $"the arrow you drew in this session is inside a cell marked '{cell.Mark}' (cell {cell.Id})";

                if (drawn.DecorationXml.Contains(pair.Key, StringComparison.OrdinalIgnoreCase))
                    return $"the arrow you drew in this session is still on the canvas (cell {pair.Key})";
            }

            return null;
        }

        // AND THE SAME FOR A DEVICE, which is the twin hole and the worse one. A device dropped in
        // this session carries draw.io's identifier on its cell until the page is rebuilt, exactly
        // like an arrow; strip our mark off it by hand and the cell falls into the decoration, the
        // deletion round finds nothing under the plant's identifier, and the device goes off the
        // plant with NOTHING said - measured, 0 refusals and 0 notices, while after a reload the
        // same gesture is refused by name.
        //
        // Worse than the arrow's, because a device takes its configuration, its door map and every
        // junction hanging from it away with it, and because nothing in the two invariants written
        // about LINES was ever going to see it: it took a net of its own
        // (DrawioDeviceInvariantTest).
        string? WhereTheDeviceOfThisSessionIs(Guid itemId)
        {
            foreach (var pair in plantItemByDrawnCell)
            {
                if (pair.Value != itemId) continue;

                foreach (var cell in drawn.Unknown)
                    if (cell.Xml.Contains(pair.Key, StringComparison.OrdinalIgnoreCase))
                        return $"the device you dropped in this session is inside a cell marked '{cell.Mark}' (cell {cell.Id})";

                if (drawn.DecorationXml.Contains(pair.Key, StringComparison.OrdinalIgnoreCase))
                    return $"the device you dropped in this session is still on the canvas (cell {pair.Key})";
            }

            return null;
        }

        // The cell of the decoration that carries this identifier, or null when the identifier is
        // merely MENTIONED somewhere - inside a note's text, say. The difference decides whether
        // there is anything to take back: a note that names a junction is not that junction's cell,
        // and removing it would delete the operator's note.
        string? TheCellCarrying(string id) => drawn.CellsLeftInTheDrawing
            .Any(cell => string.Equals(cell.Id, id, StringComparison.Ordinal)) ? id : null;

        // THE CELL OF THE FREE DRAWING THE PLANT IS ABOUT TO REDRAW THIS THING ON, under either of
        // the two names it can answer to: its own, which DrawioDocument.Build writes at every
        // reload, or draw.io's, for an arrow pulled or a device dropped in THIS session. Null when
        // the decoration carries no such cell.
        //
        // IT DOES NOT TAKE THE CELL AWAY - that is decided once, for the whole drawing, where the
        // decoration is put together and every deletion is known. What this one decides is WHAT THE
        // OPERATOR IS TOLD, and it names the cell so that the sentence can be checked against the
        // save that follows. If the two ever drift apart, the net that reads the report's promises
        // is what says so; it has already caught this pair once.
        //
        // WHICH OF THE TWO NAMES IT IS DOES NOT MATTER, and that is the owner's decision of
        // 2026-09-20. The cell left behind is not the operator's: it is ours with its mark taken
        // off - Ctrl+M, the row's bin, Apply - and the plant is about to draw that junction, or
        // that device, again. Under the plant's own name the two would collide on one identifier;
        // under draw.io's they would stand side by side, one shape over the other, which on the
        // screen is the same thing and in the drawing is worse, because nothing can tell them
        // apart by name. The gesture has to do THE SAME THING BEFORE AND AFTER A RELOAD, and after
        // a reload the two names coincide and the cell goes.
        //
        // Measured before the decision: 640 roads of the drawing's net carried two lines for one
        // arrow, and the device's half of it was carried by no net at all - the two ids differ, so
        // the check on repeated identifiers could not see it.
        string? TheCellThePlantWillRedraw(Guid plantIdentifier, IReadOnlyDictionary<string, Guid> bornInThisSession)
        {
            if (TheCellCarrying(plantIdentifier.ToString()) is { } itsOwnName) return itsOwnName;

            foreach (var pair in bornInThisSession)
                if (pair.Value == plantIdentifier && TheCellCarrying(pair.Key) is { } drawiosName)
                    return drawiosName;

            return null;
        }

        bool StillInTheDrawing(Guid id) => WhereInTheDrawing(id) is not null;

        // Quale giunzione dell'impianto sia quella che il disegno chiama cosi': UN SOLO POSTO che
        // risponde alla domanda, perche' i tre giri che se la pongono - il riconoscimento, il
        // controllo del doppione e la difesa di ultima istanza - devono rispondersi allo stesso
        // modo. Due vie: il marchio dell'impianto, che il documento scrive su ogni giunzione
        // (DrawioDocument.Build), e la mappa della sessione per le frecce nate qui, che quel
        // marchio non l'hanno ancora ricevuto.
        //
        // In both cases the question goes to 'opened', the snapshot the drawing was built from,
        // because what is being asked is WHAT THIS CELL CAME FROM - and that is a fact about the
        // page as it was opened. A junction somebody else deleted in the meantime is still in
        // 'opened' and is still recognized here, which is on purpose: it is how the save knows the
        // cell is not a brand new arrow to be created. What must not happen to it - being deleted,
        // blocking a duplicate, or going by in silence - is decided by takenAwayByAnotherHand, the
        // one set at the top of this method that compares the two snapshots.
        //
        // Le due vie NON sono intercambiabili, e il ritorno lo dice: FromOurMark distingue la
        // freccia che l'andata ha scritto da quella che l'operatore ha tirato in questa sessione.
        // Solo la prima porta gli agganci che DrawioDocument.Build le ha dato, e solo su di lei ha
        // senso chiedersi se siano cambiati (si veda il punto d, piu' sotto).
        (Guid Id, bool FromOurMark)? PlantJunctionOf(string drawnId)
        {
            if (Guid.TryParse(drawnId, out var marked) && opened.Junctions.Any(j => j.JunctionId == marked))
                return (marked, true);

            return plantJunctionByDrawnEdge.TryGetValue(drawnId, out var mapped)
                && opened.Junctions.Any(j => j.JunctionId == mapped)
                ? (mapped, false)
                : null;
        }

        // Which device of the plant the drawing calls by this name: ONE place that answers, for the
        // same reason as PlantJunctionOf just above - the three rounds that ask it (the recognition,
        // the refusal of a grouped cell, and the last-resort defence over a mangled mark) have to
        // answer it the same way. Two roads: the identifier the outward trip wrote on the cell
        // (DrawioDocument.Build writes the ItemId as the cell's id), and the session map for the
        // cells dropped here, which never received that identifier.
        //
        // Unlike the junctions', the return does not say WHICH of the two roads answered: for a
        // device nothing downstream needs to know. The junctions' does, because only an arrow the
        // outward trip drew carries the port anchors it wrote.
        //
        // Both roads ask 'opened' whether that device is still there: a pair left over - the device
        // deleted in the meantime, by somebody else - recognizes nothing, instead of making the
        // drawing speak of a device that is gone.
        Guid? PlantItemOf(string drawnId)
        {
            if (Guid.TryParse(drawnId, out var marked) && openedById.ContainsKey(marked)) return marked;
            return plantItemByDrawnCell.TryGetValue(drawnId, out var mapped) && openedById.ContainsKey(mapped)
                ? mapped
                : null;
        }

        // Which device of the plant an END of an arrow points at. The end of an arrow is a CELL
        // identifier - draw.io writes the id of the cell in source/target - so the question is the
        // same one PlantItemOf answers, and it goes through the same place: that is review finding
        // 1, where the two ends were read with Guid.TryParse instead and a device born from a drop
        // was unreachable by any arrow.
        //
        // Null is not just "we do not know it": draw.io takes the attribute AWAY when the operator
        // detaches an arrow from a device, so a missing end is a real state of the drawing and has
        // to count as "not a device of ours" rather than throw.
        Guid? EndOf(string? drawnId) => drawnId is null ? null : PlantItemOf(drawnId);

        // Whether the drawn arrow still hooks the same two devices the plant holds for this
        // junction. Both ends go through EndOf, for the reason written there: an end that is not a
        // device of the plant's at all - the arrow detached, or pulled onto a cell nobody knows -
        // counts as different.
        bool SameEnds(DrawioCell edge, JunctionDto before) =>
            EndOf(edge.Source) == before.SourceItemId && EndOf(edge.Target) == before.DestinationItemId;

        // WHERE THE DRAWING SAYS THIS ARROW GOES, which is not always where its two attributes
        // say. The head is what shows the direction (the owner's rule of 2026-09-19), so an arrow
        // whose head sits at the START joins its target to its source and not the other way
        // about.
        //
        // ONLY A MARKED CELL IS SWAPPED HERE, and the guard is load-bearing: on an UNMARKED arrow
        // DrawioReader.JoinsTwoDevices has already applied the same rule and handed the ends back
        // from the tail to the head, so swapping again would put them back the way the drag went -
        // silently, and on exactly the gesture the previous batch had just made work. On a cell of
        // OURS the reader deliberately does not swap: the direction of a junction is the plant's,
        // and only this file knows what the plant holds.
        //
        // The head of a marked cell is read in two places in this file, and NEITHER decides
        // whether the line is a connection - that decision is the reader's, once, for marked and
        // unmarked alike (DrawioReader.TheDrawingDoesNotSayConnection). Here it decides WHICH WAY
        // the connection goes; further down it decides how the notice is WORDED. The claim that
        // used to stand here - that this was the one place reading the head of such a cell - was
        // false when it was written, and MINORE 1 of the second review counted three.
        //
        // Review finding 2: until this existed, moving the head turned the connection round in the
        // session that had drawn the arrow - by this very road, through SameEnds - and did nothing
        // at all after a reload, where the operator was told instead to delete the arrow and draw
        // one the other way. One gesture, two opposite outcomes, and the difference was only
        // whether the page had been reloaded.
        static DrawioCell AsDrawn(DrawioCell edge) =>
            edge.Mark is not null && DrawioReader.HeadsOf(edge.Style) == DrawioArrowHeads.AtStart
                ? edge with { Source = edge.Target, Target = edge.Source }
                : edge;

        // The lines that had a junction and no longer point one way: the arrow, the junction that
        // goes with it, and which of the two shapes the gesture took. Collected instead of spoken
        // at once, because what is said names the OUTCOME - the deletion - and the deletion round
        // has not run yet where the first of them is found. Review finding 1: the deletion was
        // already happening, and the line on the same report said the opposite.
        var unhookedByTheHead = new List<(string EdgeId, Guid JunctionId, DrawioArrowHeads Heads)>();

        // A DEVICE SOMEBODY ELSE DELETED LEAVES THE DRAWING AT THIS SAVE (owner's decision of 27/9,
        // coda dei lavori voce 7): its cell, and the arrows of the junctions the host took away
        // with it (PlantStore.DeleteItemAsync). Until then the cell stayed, and from the next save -
        // 'opened' no longer carrying the device - it was a marked cell nobody knew, refused at
        // every save until the operator deleted it by hand.
        //
        // WHICH CELLS: only the ones this save ATTRIBUTES to that device, through the same two roads
        // every other round of this file uses - PlantItemOf for the device's own cell, PlantJunctionOf
        // for an arrow (the plant's name after a reload, draw.io's for one of this session). A note
        // beside it, an orphan the plant never knew, the operator's own free line: none of them is
        // the device's, and none of them goes. An arrow of this session that the save had made into
        // one of those junctions DOES go, although it carries no mark: it is that junction's arrow,
        // and with one of its devices gone it can never connect anything again.
        //
        // THE PAGE TAKES THEM OFF, not this function - see DrawioDeviceGone for why the words wait
        // for the canvas's answer. What is decided here is that nothing ELSE is said about them:
        // no refusal of the device's move, no notice about it or its junctions, no line about its
        // arrows, and no copy of any of them in the decoration.
        var goneCellsOf = new Dictionary<Guid, List<string>>();
        foreach (var cell in drawn.Devices)
            if (PlantItemOf(cell.Id) is { } goneId && devicesTakenAwayByAnotherHand.Contains(goneId))
            {
                if (!goneCellsOf.TryGetValue(goneId, out var cells)) goneCellsOf[goneId] = cells = [];
                cells.Add(cell.Id);
            }

        var junctionsGoneWithThem = opened.Junctions
            .Where(junction => takenAwayByAnotherHand.Contains(junction.JunctionId)
                && (goneCellsOf.ContainsKey(junction.SourceItemId) || goneCellsOf.ContainsKey(junction.DestinationItemId)))
            .ToDictionary(junction => junction.JunctionId);

        // A device of the plant that is still there - the question every drawn end of an arrow is
        // asked below: known to the drawing (EndOf) and not among the ones somebody else deleted.
        bool Living(Guid? device) => device is { } id && !devicesTakenAwayByAnotherHand.Contains(id);

        // Every cell of the drawing that is not a device or a mangled mark, once: the arrows the
        // reader made into junctions, and what it took out of or left in the decoration.
        var edgeCells = drawn.Junctions.Concat(drawn.CellsTakenOutOfTheDrawing).Concat(drawn.CellsLeftInTheDrawing)
            .DistinctBy(cell => cell.Id, StringComparer.Ordinal)
            .ToList();

        var goneArrowsOf = new Dictionary<Guid, List<string>>();
        foreach (var cell in edgeCells)
        {
            if (PlantJunctionOf(cell.Id) is not { } itsJunction
                || !junctionsGoneWithThem.TryGetValue(itsJunction.Id, out var junction)) continue;

            // BY WHERE IT IS DRAWN, and not only by whose junction it was (fix round, IMPORTANT 1).
            // An arrow of that junction the operator re-hooked in this drawing between two devices
            // that are still THERE is no longer the deleted device's: it is a connection he drew,
            // and the re-hook round of the junctions writes it. Taking it off the canvas dropped
            // that connection in silence.
            var asDrawn = AsDrawn(cell);
            var arrowFrom = EndOf(asDrawn.Source);
            var arrowTo = EndOf(asDrawn.Target);
            if (Living(arrowFrom) && Living(arrowTo) && arrowFrom != arrowTo) continue;

            foreach (var end in new[] { junction.SourceItemId, junction.DestinationItemId }.Distinct())
            {
                if (!goneCellsOf.ContainsKey(end)) continue;
                if (!goneArrowsOf.TryGetValue(end, out var arrows)) goneArrowsOf[end] = arrows = [];
                arrows.Add(cell.Id);
            }
        }

        var leavingTheCanvas = goneCellsOf.Values.SelectMany(cells => cells)
            .Concat(goneArrowsOf.Values.SelectMany(arrows => arrows))
            .ToHashSet(StringComparer.Ordinal);

        // THE LINES THAT STAY, hanging from a cell that leaves (fix round, MINOR 3): the operator's
        // own line to the device, or a junction still on the plant that he re-hooked onto it. The
        // bridge disconnects them - one end stays where the device was - and the device's line
        // counts them, so that a dangling line on the canvas is not a mystery.
        var looseLinesOf = new Dictionary<Guid, int>();
        foreach (var (goneId, cells) in goneCellsOf)
            looseLinesOf[goneId] = edgeCells.Count(edge => !leavingTheCanvas.Contains(edge.Id)
                && ((edge.Source is { } from && cells.Contains(from)) || (edge.Target is { } to && cells.Contains(to))));

        // Where each of those device cells was: the centre of its box, where the stored copy of a
        // line that stays puts the end that hung from it (see the decoration at the end of Compute).
        var whereTheCellWas = drawn.Devices
            .Where(cell => leavingTheCanvas.Contains(cell.Id))
            .DistinctBy(cell => cell.Id, StringComparer.Ordinal)
            .ToDictionary(cell => cell.Id, cell => (X: cell.X + cell.Width / 2, Y: cell.Y + cell.Height / 2), StringComparer.Ordinal);

        // AND WHERE EACH OF THEIR ARROWS WAS (queue item 18): a line of the operator's can hang from
        // an arrow too - line on line - and the arrow leaves with the device. Measured on the real
        // binary: the stored copy kept its hook on the arrow, and at the next opening the line was in
        // the model but not drawn, exactly like item 12's line on the device. Where it lets go is
        // where the line met the arrow, and that is draw.io's own point, measured: a line hooked to
        // an edge meets it in the middle of the edge's box (arrow from 275,930 to 565,930, the line's
        // end at 420,930), and when the page takes the arrow off, draw.io leaves the end right there.
        // So the stored copy and the canvas agree, which item 12's device centre could not do. The
        // box is worked out from what the drawing stores - the two ends where the arrow's port
        // anchors put them on the two device cells, turned with their rotation, plus any waypoint -
        // and not from the route draw.io draws between them: an orthogonal route that has to go
        // round a device can reach outside those points, and there the middle is an approximation.
        var devicesDrawn = drawn.Devices.DistinctBy(cell => cell.Id, StringComparer.Ordinal)
            .ToDictionary(cell => cell.Id, StringComparer.Ordinal);
        foreach (var (goneId, arrows) in goneArrowsOf)
            foreach (var arrowId in arrows)
            {
                if (whereTheCellWas.ContainsKey(arrowId)) continue;
                var arrow = edgeCells.First(cell => cell.Id == arrowId);
                whereTheCellWas[arrowId] = MiddleOf(arrow)
                    // Nothing to work it out from - an end that is neither on a device nor a stored
                    // point: the device's own centre, item 12's answer, is still a place on the canvas.
                    ?? goneCellsOf[goneId].Select(cell => whereTheCellWas[cell]).First();
            }

        (double X, double Y)? MiddleOf(DrawioCell arrow)
        {
            var owner = XElement.Parse(arrow.Xml);
            if ((owner.Name == "mxCell" ? owner : owner.Element("mxCell")) is not { } mxCell) return null;
            var geometry = mxCell.Element("mxGeometry");

            // The cell's own source/target as written, not DrawioCell's: for an unmarked arrow the
            // reader swaps those to follow the head, and the port anchors belong to the written ends.
            if (EndAt((string?)mxCell.Attribute("source"), "exit", "sourcePoint") is not { } from
                || EndAt((string?)mxCell.Attribute("target"), "entry", "targetPoint") is not { } to) return null;

            var points = new List<(double X, double Y)> { from, to };
            foreach (var point in geometry?.Element("Array")?.Elements("mxPoint") ?? [])
                if (Coordinate(point, "x") is { } x && Coordinate(point, "y") is { } y) points.Add((x, y));

            return ((points.Min(p => p.X) + points.Max(p => p.X)) / 2, (points.Min(p => p.Y) + points.Max(p => p.Y)) / 2);

            (double X, double Y)? EndAt(string? cellId, string anchor, string pointName)
            {
                if (cellId is not null && devicesDrawn.TryGetValue(cellId, out var device))
                {
                    // Without an anchor draw.io floats the end on the perimeter; the centre is the
                    // nearest thing this side of the view can say.
                    var fx = DrawioReader.TryStyleNumber(arrow.Style, anchor + "X", out var ax) ? ax : 0.5;
                    var fy = DrawioReader.TryStyleNumber(arrow.Style, anchor + "Y", out var ay) ? ay : 0.5;
                    var dx = DrawioReader.TryStyleNumber(arrow.Style, anchor + "Dx", out var ox) ? ox : 0;
                    var dy = DrawioReader.TryStyleNumber(arrow.Style, anchor + "Dy", out var oy) ? oy : 0;
                    var (cx, cy) = (device.X + device.Width / 2, device.Y + device.Height / 2);
                    var (px, py) = (device.X + fx * device.Width + dx - cx, device.Y + fy * device.Height + dy - cy);
                    var turn = device.Rotation * Math.PI / 180;
                    var (cos, sin) = (Math.Cos(turn), Math.Sin(turn));
                    // Rounded to what the stored copy writes, so that a quarter turn is not 1e-15 off.
                    return (Math.Round(cx + px * cos - py * sin, 3), Math.Round(cy + px * sin + py * cos, 3));
                }

                return geometry?.Elements("mxPoint").FirstOrDefault(p => (string?)p.Attribute("as") == pointName) is { } stored
                    && Coordinate(stored, "x") is { } sx && Coordinate(stored, "y") is { } sy
                    ? (sx, sy)
                    : null;
            }

            static double? Coordinate(XElement point, string axis) =>
                point.Attribute(axis) is null ? 0
                : double.TryParse((string?)point.Attribute(axis), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
        }

        // THE CELLS THE PLANT DRAWS AGAIN UNDER ANOTHER NAME (final review, C7): a device dropped in
        // this session and an arrow the plant made into a junction in an earlier save keep draw.io's
        // identifier on the canvas - the session maps know which device and which junction they are -
        // but DrawioDocument.Build draws them at the next opening under the plant's own identifier.
        // A line of the operator's hooked onto one of them is stored hooked onto the plant's name,
        // which is the cell it will meet at the next opening; under draw.io's it was in the model and
        // not drawn, item 12's mechanism. Filled at the end of Compute, where the deletions are known.
        var redrawnUnder = new Dictionary<string, string>(StringComparer.Ordinal);

        // The cell as it goes into the decoration: untouched, unless one of its ends hangs from a
        // device cell, or an arrow, that leaves the canvas or goes at the next opening - then that
        // end lets go of it and stays where it was (whereTheCellWas) - or from a cell the plant draws
        // again under another identifier, whose name the end then takes (redrawnUnder).
        string LetGoOfTheCellsThatLeave(DrawioCell cell)
        {
            var sourceGoes = cell.Source is { } source && whereTheCellWas.ContainsKey(source);
            var targetGoes = cell.Target is { } target && whereTheCellWas.ContainsKey(target);
            var renamed = (cell.Source is { } from && redrawnUnder.ContainsKey(from))
                || (cell.Target is { } to && redrawnUnder.ContainsKey(to));
            if (!sourceGoes && !targetGoes && !renamed) return cell.Xml;

            var owner = XElement.Parse(cell.Xml);
            if ((owner.Name == "mxCell" ? owner : owner.Element("mxCell")) is not { } mxCell) return cell.Xml;

            if (sourceGoes) LetGo("source", "sourcePoint", whereTheCellWas[cell.Source!]);
            if (targetGoes) LetGo("target", "targetPoint", whereTheCellWas[cell.Target!]);
            // The ends as written, not DrawioCell's: the reader may have swapped those to follow a head.
            foreach (var end in (string[])["source", "target"])
                if ((string?)mxCell.Attribute(end) is { } hooked && redrawnUnder.TryGetValue(hooked, out var plantsName))
                    mxCell.SetAttributeValue(end, plantsName);
            return owner.ToString();

            void LetGo(string end, string point, (double X, double Y) at)
            {
                var geometry = mxCell.Element("mxGeometry");
                if (geometry is null)
                {
                    geometry = new XElement("mxGeometry", new XAttribute("relative", "1"), new XAttribute("as", "geometry"));
                    mxCell.Add(geometry);
                }

                mxCell.Attribute(end)?.Remove();
                geometry.Elements("mxPoint").Where(p => (string?)p.Attribute("as") == point).Remove();
                geometry.Add(new XElement("mxPoint",
                    new XAttribute("x", at.X.ToString("0.###", CultureInfo.InvariantCulture)),
                    new XAttribute("y", at.Y.ToString("0.###", CultureInfo.InvariantCulture)),
                    new XAttribute("as", point)));
            }
        }

        // The arrows of those junctions whose weight the operator had changed in this drawing
        // (fix round, MINOR 2): filled by the junctions' round, which skips them, and said in the
        // device's line - they used to get "its priority was not changed" of their own.
        var reweightedArrows = new HashSet<string>(StringComparer.Ordinal);

        // Whose drawn cell the operator had changed - moved, resized, rotated, mirrored, grouped:
        // filled by the rounds below, which see those gestures, and said in the device's line.
        var goneAfterAChange = new HashSet<Guid>();

        // Le celle con un marchio nostro che non riconosciamo. Il lettore le ha messe da parte
        // apposta per non farle scivolare nella decorazione; qui ognuna esce con il proprio nome.
        // Ignorarle rimetterebbe il silenzio dove il compito 3 l'aveva tolto.
        foreach (var cell in drawn.Unknown)
        {
            refusals.Add(new DrawioRefusal(cell.Id,
                $"mark '{cell.Mark}' not recognized: the cell is neither a device nor a junction, it was not written"));

            // Vale anche per una freccia nata in questa sessione, che di suo non porta nessun
            // identificativo dell'impianto: senza la mappa, storpiarle il marchio la farebbe
            // arrivare alle cancellazioni come una giunzione sparita dal disegno, mentre
            // l'operatore la vede benissimo sulla tela.
            //
            // COME un marchio storpiato arriva fin qui, misurato e non dedotto: NON da
            // "Extras > Edit Diagram", che questo draw.io incorporato non ha - nessuna barra dei
            // menu, la parola "Extras" non compare nel suo documento, e l'azione non dichiara
            // nessuna scorciatoia (DrawioCarryingTheDrawingAwayTest). Il disegno esce di qui con
            // Ctrl+A e Ctrl+C - va negli appunti come XML di draw.io, misurato - si modifica
            // altrove, e un Ctrl+V del testo di un file .drawio lo riporta dentro
            // (DrawioOnePageTest). Trascinare un file sulla tela fa lo stesso.
            if (PlantJunctionOf(cell.Id) is { } mangledJunction) drawnJunctionIds.Add(mangledJunction.Id);

            // And the same for a DEVICE born from a drop in this session, which carries no plant
            // identifier of its own either: without the map, mangling its mark would bring it to the
            // deletion round as a device gone from the drawing, while the operator can see it
            // perfectly well on the canvas.
            if (PlantItemOf(cell.Id) is { } mangledItem) seen.Add(mangledItem);

            // Il disegno PORTAVA questa cella: dirlo ai due giri delle cancellazioni e' cio' che
            // impedisce al rifiuto qui sopra di diventare anche una cancellazione. L'identificativo
            // va in entrambi gli insiemi perche' da qui non sappiamo se sia di un dispositivo o di
            // una giunzione, e metterlo in quello sbagliato non toglie niente a nessuno.
            if (!Guid.TryParse(cell.Id, out var mangledId)) continue;
            seen.Add(mangledId);
            drawnJunctionIds.Add(mangledId);
        }

        // THE MIRROR, declared because it cannot be written. draw.io mirrors a cell by putting
        // flipH=1 or flipV=1 in its style; the plant has nowhere to keep it - of a device it holds
        // position, size and rotation (PlantItemGeometryDto), of a junction the two ends and the
        // priority (JunctionDto) - so there is nothing to write. Writing is impossible; KEEPING
        // QUIET is not, and that is the whole point: at the next opening DrawioDocument.Build
        // rewrites the style of every device and of every junction from the plant, without any
        // flip, so the mirroring disappears with nobody saying a word. It is D203 inside the
        // drawing: a gesture of the operator's that has no effect and no line saying so.
        //
        // The precedent followed is the port anchor of a junction (the SameEnds branch further
        // down): the rest of the cell goes on being written exactly as before, and only the part
        // the plant cannot hold comes out as a refusal WITH A NAME. Refusing the whole cell would
        // cost the operator the move he made in the same gesture, and this file's rule is that a
        // refusal must not eat what nobody else had touched.
        //
        // ONE line per cell, not one per flip: an operator who mirrors both ways has made one
        // gesture, and two lines saying the same thing would be the place where the real refusals
        // hide (the same reason the new junctions' anchors notice is grouped).
        //
        // DEVICES AND JUNCTIONS ONLY. A mirrored cell with no mark of ours is decoration, and there
        // the mirroring is not lost at all: the decoration goes back to draw.io as the drawer wrote
        // it (DecorationXml - the reader copies the whole element, style included - and Build
        // appends it as it is), so declaring anything about it would be declaring a loss that does
        // not happen. A cell with a mark we do not recognize is already refused whole just above,
        // and it is neither a device nor a junction: a second line about its mirroring would say
        // nothing more.
        //
        // AND ONLY ABOUT A CELL THE PLANT ALREADY HOLDS. The tail of the sentence - "at the next
        // opening the shape comes back as it was" - is a promise about the plant redrawing the
        // cell, and the plant can only redraw a cell it has. A copy pasted in draw.io keeps our
        // wcs="device" mark with a brand new identifier: PlantItemOf answers null, it goes to
        // NewDevices a few lines below and comes out with "the save does not create it... It was
        // not written" - so at the next opening that cell is GONE, not "as it was". Two lines on
        // screen, one of them saying the opposite of what happens, is worse than one: the cell is
        // already refused whole, and its mirroring is a detail of something that was never written.
        // (Rilievo 2 of the review.)
        //
        // THE REMEDY IS NAMED because this refusal is not a one-off: the document does not come
        // back to the canvas after a save (DrawioEditor.SeedDrawioAsync), so the cell stays
        // mirrored and the very same line arrives again at every save of the session until the
        // operator undoes it.
        //
        // BOTH LINES NAME EDIT STYLE, and the device's used to name something else: "Arrange > Flip
        // Horizontal / Flip Vertical", which does exist in a full draw.io and does NOT exist in
        // this page. Measured on 2026-09-19, in the real page, on a real device: this Editor opens
        // draw.io with ui=min, and there ui.format is null with ui.formatContainer empty - the
        // Format panel is never built, so there is no Arrange tab to reach; ui.actions.actions.flipH
        // and .flipV are undefined - the two actions do not exist at all; and the right-click menu
        // on one device offers To Front, To Back, Cut, Copy, Duplicate, Delete, Lock/Unlock and
        // Edit, with no Flip anywhere. The advice was impossible to follow, which on this branch is
        // the second-worst defect after a silent loss. What IS there, measured on the same device in
        // the same page: Ctrl+E opens Edit Style with the cell's style in it, taking flipH out and
        // pressing Apply leaves the style without it, and putting it back in puts the mirror back -
        // so the way out is real in both directions. It is the same way out the arrow's own
        // refusal - the loop right after this one - has always named, and for the arrow it is also
        // the ONLY one: on an edge Graph.prototype.flipCells calls flipEdgePoints and never touches
        // flipH/flipV, so even in a draw.io that had the Flip buttons they would not clear an
        // arrow's flag.
        //
        // THE ARROW KEEPS ITS TAIL, and the reason is that for an arrow the promise holds in every
        // case but one. An arrow the plant knows stays in the plant (same ends, or re-hooked, or
        // re-hooked and refused), and an arrow it does not know is CREATED by this very save unless
        // Writable says no - in all of those the plant redraws it at the next opening. The one case
        // left is a brand new arrow that Writable refuses ("la freccia ... non e' stata creata,
        // resta un disegno"), which does disappear; it is not guarded here because that would mean
        // asking Writable twice, and because reaching it needs flipH/flipV written INTO an arrow's
        // style by hand AND ends the delta cannot
        // resolve, at the same time. ("By hand" is the right word: this page has no Flip at all, and
        // even where draw.io has one it does not touch those two keys on an edge - see above.)
        foreach (var cell in drawn.Devices)
        {
            if (MirrorOf(cell) is not { } mirror) continue;
            if (PlantItemOf(cell.Id) is not { } mirroredId) continue;
            // A device somebody else deleted: its cell leaves the drawing, and "at the next opening
            // the shape comes back as it was" would be false - the mirroring is among the changes
            // its line says are lost.
            if (goneCellsOf.ContainsKey(mirroredId))
            {
                goneAfterAChange.Add(mirroredId);
                continue;
            }
            // The indexer and not TryGetValue: PlantItemOf answers an identifier only after having
            // found it in openedById itself (both of its returns go through ContainsKey), so a miss
            // here is not a case, it is a contradiction - and a silent "" would hide it.
            var named = $"{openedById[mirroredId].Name}: ";
            refusals.Add(new DrawioRefusal(cell.Id,
                $"{named}the cell is {mirror}, and the plant does not store a mirror - of a device it holds position, size and rotation, nothing else: the mirroring was not written, and at the next opening the shape comes back as it was. This line comes back at every save until flipH/flipV is taken out of the cell's style (select it and press Ctrl+E, Edit Style)."));
        }

        foreach (var edge in drawn.Junctions)
        {
            if (MirrorOf(edge) is not { } mirror) continue;
            if (leavingTheCanvas.Contains(edge.Id)) continue;
            refusals.Add(new DrawioRefusal(edge.Id,
                $"the arrow is {mirror}, and the plant does not store a mirror - of a junction it holds the two ends and the priority, nothing else: the mirroring was not written, and at the next opening the drawing redraws the arrow as it was. This line comes back at every save until flipH/flipV is taken out of the arrow's style (select it and press Ctrl+E, Edit Style)."));
        }

        foreach (var cell in drawn.Devices)
        {
            // Not on the canvas: the geometry that follows is expressed in another frame of
            // reference (see the class remarks). Refused by name, before anything else reads a
            // single one of its numbers.
            if (!OnTheCanvas(cell))
            {
                // Asked through PlantItemOf and not through Guid.TryParse: a device dropped in this
                // session carries draw.io's identifier, which is not a guid at all, so the plain
                // parse would have called it unknown - and an unknown cell is not put into 'seen',
                // which is to say the refusal below would ALSO have deleted it.
                var grouped = PlantItemOf(cell.Id);

                // The drawing DID carry this device. Saying so to the deletion round below is what
                // keeps this refusal from ALSO being a deletion - the worst outcome this file has,
                // and one this branch has already produced once.
                if (grouped is { } groupedId) seen.Add(groupedId);

                // Grouped, and deleted by somebody else: the cell leaves the drawing all the same -
                // taking a cell out of a group is a removal like any other - and "it was not
                // deleted, dissolve the group" would be advice about a device that is gone.
                if (grouped is { } goneGroupedId && goneCellsOf.ContainsKey(goneGroupedId))
                {
                    goneAfterAChange.Add(goneGroupedId);
                    continue;
                }

                var groupedName = grouped is { } namedId && openedById.TryGetValue(namedId, out var groupedItem)
                    ? $"{groupedItem.Name}: " : "";
                refusals.Add(new DrawioRefusal(cell.Id,
                    $"{groupedName}the cell is inside a group, not directly on the drawing's canvas (parent=\"{cell.Parent ?? "(none)"}\"), and inside a group draw.io writes the geometry RELATIVE to the group instead of to the canvas: written as it is, the device would land somewhere else entirely. It was not written, and it was not deleted. To move it, dissolve the group (Ctrl+Shift+U) before saving."));
                continue;
            }

            if (PlantItemOf(cell.Id) is not { } id)
            {
                // A marked cell the plant does not know. Until the drop created the device, this was
                // where EVERY dragged shape ended up; now it is the leftover of a creation that did
                // NOT happen (no permit, the engine running, the host refusing) or of a cell written
                // by hand into the XML. The save does not create it - one creation, one road - it
                // names it, and whoever writes says why (see DrawioEditor.WriteToPlantAsync).
                newDevices.Add(cell);
                continue;
            }

            var before = openedById[id];

            // Anche se questo dispositivo finira' rifiutato, il disegno lo conteneva: senza questa
            // riga prima di ogni uscita, un rifiuto si trasformerebbe in una cancellazione.
            seen.Add(id);

            // The drawing's box against the plant's, by the ONE rule the duplication shares
            // (DrawnGeometry.Judge): unchanged, refused at 45 degrees, or the geometry to write.
            var verdict = DrawnGeometry.Judge(before, cell.X, cell.Y, cell.Width, cell.Height, cell.Rotation);

            // Deleted on the host by somebody else: nothing to write, and it is not refused here
            // either - its cell leaves the drawing and its line, which the page prints, says the
            // move is lost. Asked BEFORE "unchanged", because the device goes in both cases.
            if (goneCellsOf.ContainsKey(id))
            {
                if (!verdict.Unchanged) goneAfterAChange.Add(id);
                continue;
            }

            if (verdict.Unchanged) continue;

            // Not among the devices somebody else deleted (the question just above), and drawn:
            // 'current' holds it.
            var now = currentById[id];

            if (Changed(before, now))
            {
                refusals.Add(new DrawioRefusal(cell.Id,
                    $"{before.Name}: changed on the host as well while you were drawing, not overwritten"));
                continue;
            }

            if (verdict.Refusal is { } refused)
            {
                refusals.Add(new DrawioRefusal(cell.Id, refused switch
                {
                    DrawnGeometryRefusal.ResizedAtDegenerateRotation =>
                        $"{before.Name}: resized at a rotation of 45 degrees, where the drawing does not carry the device's own size: not written back",
                    DrawnGeometryRefusal.RotatedAwayFromDegenerateRotation =>
                        $"{before.Name}: rotated away from a rotation of 45 degrees, where the drawing does not carry the device's own size: not written back",
                    _ =>
                        $"{before.Name}: turned to 45 degrees, and the plant cannot represent a device at 45 degrees - its own size would be lost: not written back",
                }));
                continue;
            }

            moves.Add(verdict.Geometry!);
        }

        var deleted = new List<Guid>();
        foreach (var item in opened.Items)
        {
            if (seen.Contains(item.ItemId)) continue;
            // Mai arrivato nel disegno: la sua assenza non dice niente. Leggerla come una
            // cancellazione svuoterebbe l'impianto dei dispositivi non collocati al primo
            // salvataggio, senza che nessuno li avesse toccati.
            if (!DrawioDocument.IsDrawn(opened, item)) continue;
            // Gia' sparito dall'host: non c'e' niente da cancellare e niente da perdere.
            if (!currentById.TryGetValue(item.ItemId, out var now)) continue;

            // La difesa di ultima istanza: il disegno porta ancora questo identificativo, solo non
            // piu' come cella nostra. Si veda il commento in testa a Compute.
            //
            // UNDER EITHER OF ITS TWO IDENTITIES, for the reason written at
            // WhereTheDeviceOfThisSessionIs: before a reload the plant's identifier is nowhere in
            // the drawing, and asking only for it let a device go off the plant in silence.
            if ((WhereInTheDrawing(item.ItemId) ?? WhereTheDeviceOfThisSessionIs(item.ItemId)) is { } whereItem)
            {
                // AND WHEN WHAT CARRIES IT IS THE CELL ITSELF, the cell is taken back - the same
                // one property as for a junction, and for the same reason: the plant is about to
                // redraw this device, so leaving the stripped cell where it is puts two shapes on
                // the canvas for one device. On screen they are one shape, and the operator who
                // moves it moves one of the two.
                //
                // UNDER EITHER NAME, like the junction's and by the same decision of 2026-09-20: a
                // device dropped in this session carries draw.io's identifier, so the two shapes do
                // not even share an identifier - which is why the check on repeated identifiers
                // could not see this half, and for a while nobody was asking about it. It is asked
                // now, by THING and not by name: one thing of the plant's, one element on the
                // canvas (DrawioDrawingInvariantTest).
                //
                // Three gestures reach it - Ctrl+M, the row's bin, Apply - and it cost 1920 roads
                // of the drawing's net under the plant's name, and 1920 more under draw.io's.
                //
                // AND THE CELL IS NAMED, which is what lets this sentence be checked at all. It
                // was the one line of the report with nothing watching it: the net that reads the
                // promises walked the lines, so a sentence addressed to a DEVICE was collected by
                // nobody, declared nowhere and skipped without a word.
                if (TheCellThePlantWillRedraw(item.ItemId, plantItemByDrawnCell) is { } itsCell)
                {
                    refusals.Add(new DrawioRefusal(item.ItemId.ToString(),
                        $"{item.Name}: the mark was taken off this cell (cell {itsCell}), and the plant still holds the device it was drawn for: the device was not deleted, the cell is not free drawing, and at the next opening it comes back as a device of ours"));
                }
                else
                {
                    refusals.Add(new DrawioRefusal(item.ItemId.ToString(),
                        $"{item.Name}: the drawing still carries this device, but not as a device of ours - {whereItem}: it was not deleted"));
                }

                continue;
            }

            // La guardia anti-deriva vale anche - soprattutto - per la cancellazione: sovrascrivere
            // lo spostamento di un altro si vede e si rifa', cancellare il dispositivo che un altro
            // stava sistemando no. Ed e' per questo che qui la domanda e' PIU' LARGA che per le
            // scritture: chi ha rinominato un dispositivo, gli ha cambiato tipo o gli ha rifatto le
            // mappature ci stava lavorando quanto chi l'ha spostato, ma non ha mosso niente e un
            // confronto di geometrie non lo vedrebbe.
            if (ChangedForDeletion(item, now))
            {
                refusals.Add(new DrawioRefusal(item.ItemId.ToString(),
                    $"{item.Name}: changed on the host as well while you were drawing, not deleted"));
                continue;
            }

            deleted.Add(item.ItemId);
        }

        // Gli identificativi che il disegno riconosce ancora COME SE STESSI, calcolati una volta
        // sola su tutto l'elenco (non dentro il giro qui sotto): serve al controllo del doppione,
        // che deve sapere se una giunzione di 'opened' e' ancora quella che era anche quando la sua
        // freccia compare PIU' AVANTI nell'elenco - l'ordine delle celle nell'XML non e'
        // significativo, e un insieme costruito a meta' strada direbbe "sparita" una giunzione che
        // il disegno porta ancora, solo perche' non l'abbiamo ancora incontrata.
        //
        // "Come SE STESSA" comprende gli ESTREMI, non solo l'identificativo (punto b del compito
        // 7b): da quando una freccia riagganciata altrove diventa una cancellazione piu' una
        // creazione, la giunzione di prima sta per sparire dall'impianto e non deve piu' bloccare
        // niente. Senza il confronto degli estremi qui, un operatore che riaggancia A-B su A-C e
        // ridisegna una freccia fra A e B si vedrebbe rifiutare la seconda con "esiste gia'" -
        // mentre la giunzione che "esiste gia'" e' proprio quella che stiamo cancellando. E' lo
        // stesso difetto che l'esclusione qui sotto aveva gia' dovuto togliere una volta.
        //
        // "Come SE STESSA" comprende anche le frecce che si riconoscono attraverso la mappa della
        // sessione (si veda PlantJunctionOf): una giunzione creata poco fa non e' avviata a
        // nessuna cancellazione, e deve continuare a impedire un secondo arco fra gli stessi due
        // dispositivi - che sul disegno si sovrappone al primo, dove nessuno lo vedrebbe.
        //
        // "As ITSELF" covers the HEAD too, and it is covered by the list this runs over rather
        // than by a test of its own: a line whose head has been taken off, or which has one at
        // each end, is not in drawn.Junctions at all - the reader keeps it out, for a marked cell
        // and an unmarked one alike (DrawioReader.TheDrawingDoesNotSayConnection). Its junction
        // is on its way out, so it blocks nothing, which is what the re-hooked one already does.
        // Without that, taking the head off an arrow and drawing a new one between the same two
        // devices would see the second refused with "it already exists" while the first vanished
        // from the plant.
        //
        // The head also says WHICH WAY the drawn arrow goes (AsDrawn), and that is why AsDrawn is
        // applied here: a junction the head turns round is about to be redone the other way, so
        // it is not itself any more and blocks nothing either.
        var selfRecognizedJunctionIds = drawn.Junctions
            .Select(e => (Edge: AsDrawn(e), Plant: PlantJunctionOf(e.Id)))
            .Where(pair => pair.Plant is { } known
                && opened.Junctions.FirstOrDefault(j => j.JunctionId == known.Id) is { } before
                && SameEnds(pair.Edge, before))
            .Select(pair => pair.Plant!.Value.Id)
            .ToHashSet();

        // Una giunzione di 'opened' blocca ancora un doppione solo se il disegno la porta in
        // qualche forma: come se stessa, come marchio storpiato (StillInTheDrawing), o perche' non
        // era mai stata disegnata (IsDrawn falso - un legame che l'host tiene ma che il disegno non
        // mostra resta un legame vero, e continua a impedire un secondo arco fra gli stessi due
        // dispositivi). Quella che il disegno NON porta piu' e' gia' avviata alla cancellazione (si
        // veda il giro su DeletedJunctions piu' sotto): il gesto naturale dell'operatore dopo il
        // rifiuto "giunzione riagganciata" e' proprio cancellare quella freccia e ridisegnarne una
        // fra gli stessi due dispositivi, e senza questa esclusione il controllo del doppione la
        // rifiutava con "esiste gia'" mentre l'originale spariva dall'impianto senza essere
        // ricreata - un rifiuto che dice l'opposto di quello che sta accadendo.
        bool StillBlocksADuplicate(JunctionDto junction) =>
            !DrawioDocument.IsDrawn(opened, junction)
            || selfRecognizedJunctionIds.Contains(junction.JunctionId)
            || StillInTheDrawing(junction.JunctionId);

        var newJunctions = new List<DrawioNewJunction>();
        var junctionPriorities = new List<DrawioJunctionPriority>();

        // Le tre domande che una freccia deve superare per diventare una giunzione sull'impianto,
        // in UN SOLO POSTO: le passa sia una freccia nuova, sia una freccia riagganciata che qui
        // viene rifatta (punto b, sotto). Due copie divergerebbero, e la riagganciata verrebbe
        // cancellata per una regola che l'altra non applica - cioe' cancellata senza essere
        // ricreata, la perdita peggiore possibile in questo file. La frase esce corta perche' i due
        // chiamanti la incastonano in due frasi diverse: l'operatore deve leggere il proprio gesto,
        // non la regola interna.
        bool Writable(DrawioCell edge, out Guid source, out Guid target, out string reason)
        {
            source = target = Guid.Empty;

            // Decisione 3.3: giunzione solo se aggancia due porte. La domanda si fa con la stessa
            // analisi dello stile che il lettore gia' porta (DrawioReader.TryStyleNumber, usata
            // poco piu' sotto da AnchorsMoved per la STESSA domanda): una ricerca di sottostringa
            // non e' la stessa cosa - aggancia anche una chiave che FINISCE per "exitX=", e
            // accetterebbe come giunzione una freccia che alle porte non e' agganciata affatto.
            if (!DrawioReader.TryStyleNumber(edge.Style, "exitX", out _)
                || !DrawioReader.TryStyleNumber(edge.Style, "entryX", out _))
            {
                reason = "does not hook two ports";
                return false;
            }

            // Both ends have to be devices the plant already knows. An arrow pulled onto a copy -
            // still a device to be configured, with no identifier of ours - has nothing to join
            // yet: it is said, not thrown away.
            //
            // Asked through PlantItemOf and NOT through Guid.TryParse, and this is review finding
            // 1. THE END OF AN ARROW IS A CELL IDENTIFIER, exactly like the identifier of a device
            // cell: for a device the seeding drew it is the guid, for one dropped in this session
            // it is draw.io's own and the guid lives only in the session map. Reading it as a guid
            // was assuming the two are the same string - true before the drop existed, false since.
            // Measured: an arrow from a seeded device to a dropped one came back with NewJunctions
            // empty and the refusal below, so on a plant built from scratch - every device dropped
            // in this session, which is the beta tester's own gesture - NO junction could be drawn
            // until the page was reloaded. And the refusal sent the operator looking for a fault
            // that was not there: it said the plant did not know those devices, while it did.
            if (EndOf(edge.Source) is not { } drawnSource || EndOf(edge.Target) is not { } drawnTarget)
            {
                reason = "does not join two devices the plant knows";
                return false;
            }

            // A DEVICE JOINED TO ITSELF IS NOT A CONNECTION, and the plant says so before we do:
            // POST /plant/junctions answers 400 - "Una giunzione non puo' avere lo stesso
            // dispositivo come origine e destinazione" (PlantEndpoints), and PUT /plant/graph
            // defends the same invariant (GraphEndpoints.Validate). The unmarked road already
            // turned it down in the reader, where BetweenTwoDevices asks for two DIFFERENT
            // devices; asking here is what makes the two roads agree, for the arrow drawn by hand
            // and for the junction of ours whose end the operator dropped back onto its own shape.
            //
            // MEASURED, and it is why this is a refusal and not a tidy-up: without it, dropping
            // both ends of a junction onto one device deleted the junction and then failed to
            // create the loop - the host refused it - and the save stopped with "the plant was
            // left halfway". Written and NOT written in the same report: the connection gone,
            // nothing in its place. Deleting without recreating is the worst thing this file can
            // do, and its own rule about re-hooking (below) exists to prevent exactly that; the
            // loop was the one road that reached it without being asked.
            if (drawnSource == drawnTarget)
            {
                reason = "joins a device to itself, and a connection joins two different devices";
                return false;
            }

            // A DEVICE SOMEBODY ELSE DELETED IS NOT AN END (fix round, IMPORTANT 2). EndOf answers
            // from 'opened', where it still is, so the arrow used to be proposed: the host refused
            // the creation and the save stopped halfway - and for a junction re-hooked onto it, the
            // plan was "delete it, create the new one", so the deletion went through and the
            // connection was lost. Refused by name here, before anything is written; a re-hooked
            // junction then keeps the ends it had.
            foreach (var end in new[] { drawnSource, drawnTarget })
            {
                if (!devicesTakenAwayByAnotherHand.Contains(end)) continue;
                reason = $"joins {openedById[end].Name}, which somebody else deleted from the plant while you were drawing";
                return false;
            }

            source = drawnSource;
            target = drawnTarget;

            // Una freccia COPIATA porta un identificativo nuovo ma gli stessi estremi e gli stessi
            // agganci dell'originale: proposta come nuova, chi scrive creerebbe una seconda
            // giunzione fra gli stessi due dispositivi, e sul disegno le due si sovrappongono -
            // nessuno vedrebbe il doppione. Si guarda anche fra quelle appena proposte: due copie
            // dentro lo stesso disegno sono lo stesso caso.
            var candidateSource = source;
            var candidateTarget = target;
            if (opened.Junctions.Any(j => j.SourceItemId == candidateSource && j.DestinationItemId == candidateTarget
                    && !takenAwayByAnotherHand.Contains(j.JunctionId)
                    && StillBlocksADuplicate(j))
                || newJunctions.Any(n => n.Source == candidateSource && n.Destination == candidateTarget))
            {
                reason = "doubles a junction that already exists between these two devices";
                return false;
            }

            reason = "";
            return true;
        }

        // A JUNCTION IS NOT REFUSED FOR BEING IN A GROUP, and the asymmetry with the device loop
        // above is deliberate. MEASURED on 2026-09-16, in a real browser: grouping two devices with
        // Ctrl+G puts the arrow between them in the group as well - it came back with
        // parent="d7n2k0YsZD8LkEQScBf0-1", the group's own id, exactly like the two devices. But
        // the defect that makes a grouped DEVICE unwritable does not reach it: what makes a device
        // wrong is its geometry, which inside a group is relative to the group, and of an edge this
        // delta reads no geometry at all - only the identifier, the ends (source/target, which are
        // cell identifiers and do not move with the parent) and the port anchors in the style. In
        // that same measurement, with the remedy still off, the grouped arrow was written to the
        // plant no differently from an ungrouped one.
        //
        // So refusing it would be a refusal with no defect behind it - and not a harmless one: the
        // operator who re-hooks an arrow inside a group would be told no for a reason that is not
        // true. If one day an edge's mxGeometry starts being read here (waypoints, a label's offset:
        // both ARE relative to the parent), this is the comment that says the question has to be
        // asked again.
        // The junctions another hand had removed whose arrow the operator re-hooked, and which this
        // save creates again between the new ends (queue item 11, fix round): their own line says
        // so, and the other-hand line further down - "this save did not touch it, and the line goes
        // off your drawing" - would contradict it under the same identifier.
        var reHookedAfterAnotherHand = new HashSet<Guid>();

        foreach (var raw in drawn.Junctions)
        {
            // An arrow of a junction that went with a device somebody else deleted: it leaves the
            // drawing, and nothing about its ports or its ends is worth a line of its own. A weight
            // the operator changed on it is counted, and the device's line says it was not written.
            if (leavingTheCanvas.Contains(raw.Id))
            {
                if (PlantJunctionOf(raw.Id) is { } goneJunction
                    && opened.Junctions.FirstOrDefault(j => j.JunctionId == goneJunction.Id) is { } goneBefore
                    && WeightOf(raw) is { } goneWeight && goneWeight != JunctionWeights.FromPriority(goneBefore.Priority))
                    reweightedArrows.Add(raw.Id);
                continue;
            }

            // THE HEAD OF A JUNCTION OF OURS IS READ TOO, and it is read HERE because this is the
            // only place that knows the plant. On an arrow with no mark the reader has already
            // applied the owner's rule of 2026-09-19 and swapped the ends; on one of ours it hands
            // them as written, and AsDrawn is what applies the same rule to it - the head says
            // which way the connection goes, whichever cell carries it.
            //
            // It used to be read only to produce a LINE - "the plant still joins the two devices
            // the way round it did... delete this arrow and draw one the other way" - and that
            // line was true only after a reload. In the session that drew the arrow the same
            // gesture already turned the junction round, all by itself, through SameEnds below.
            // One gesture, one outcome, in both conditions: review findings 1 and 2.
            var edge = AsDrawn(raw);

            // L'identita' della freccia: il marchio dell'impianto, oppure la mappa della sessione
            // per una giunzione nata qui (si veda PlantJunctionOf).
            if (PlantJunctionOf(edge.Id) is { } known
                && opened.Junctions.FirstOrDefault(j => j.JunctionId == known.Id) is { } before)
            {
                // THE HEAD RULE WAS NOT APPLIED TO THIS LINE, AND IT IS SAID. Third review, minor
                // finding 1: an arrow of ours whose head the operator took off went on being a
                // junction, in silence, when one of its ends was not a cell the drawing marks
                // wcs="device" - a neighbour's mark mangled, a shape deleted, an end dropped on a
                // note. The outcome (the plant keeps what it has) is the prudent one and is not
                // changed here; what was missing is the sentence, and without it two drawings
                // that look the same on screen behave differently with nothing to tell them
                // apart.
                //
                // THE CONDITION IS EXACT, not a guess. A cell reaching this loop with no single
                // head can only be a MARKED one whose ends are not two DIFFERENT devices of the
                // drawing: the reader diverts every other line without one direction into
                // UndirectedLinesBetweenDevices before this loop sees it
                // (DrawioReader.TheDrawingDoesNotSayConnection), and an unmarked cell with no
                // single head is never promoted at all. So "the head was not read" is the whole
                // of what is left, and that is what the line says.
                //
                // AND IT COVERS BOTH WAYS THAT CAN HAPPEN - an end that is not a device cell, and
                // two ends on the SAME device - because the sentence names the condition the
                // reader actually asks (BetweenTwoDevices) instead of the one case that was
                // measured. Written the narrow way first, it was a false sentence on the loop,
                // and the enumeration test caught it before the commit.
                if (DrawioReader.HeadsOf(edge.Style) is DrawioArrowHeads.None or DrawioArrowHeads.Both)
                {
                    notices.Add(new DrawioNotice(edge.Id,
                        "this line does not point one way, and between two devices that would mean it is no longer a connection - but its two ends are not two DIFFERENT cells this drawing marks as devices of ours, so the head was not read at all and the rule was not applied. What happens to the junction is decided by the ends alone: look at the cells the two ends hang from."));
                }

                // NO HEAD, NO CONNECTION is not asked here any more, and that is the repair of
                // the second review's two GRAVE findings. It used to be asked HERE, in this
                // branch and only in this branch, so a marked cell the plant does NOT hold never
                // met it: it became a junction again, head or no head, and the save after the one
                // that deleted the junction put it straight back. The question now belongs to the
                // reader, which asks it of every line between two devices before this loop sees
                // one (DrawioReader.TheDrawingDoesNotSayConnection), so a line without a head is
                // not in drawn.Junctions at all and the junction the plant holds for it is taken
                // away by the deletion round below - the same road a re-hooked junction travels.
                // What is SAID about it is said once, at the bottom, where both roads meet.
                //
                // WHY THE DELETION and not the keeping, which was the other way to make the two
                // conditions agree: the owner's rule of 2026-09-19 says the head is what makes a
                // line between two devices a connection, so a line that has lost it is no longer
                // a connection - and the alternative would leave the operator with a plain line
                // on the canvas and a link the engine still routes through, which is the silent
                // half of the very defect that rule closes.

                // Riconoscere l'identificativo non basta: gli ESTREMI disegnati possono non essere
                // piu' quelli dell'impianto. L'operatore stacca la freccia da B e la attacca a C,
                // salva, e senza guardare gli estremi l'impianto tiene A-B - nessuna modifica,
                // nessun rifiuto, nessuna riga nel risultato: il gesto evapora, che e' D203 dentro
                // il giro delle giunzioni.
                if (SameEnds(edge, before))
                {
                    drawnJunctionIds.Add(known.Id);
                    // Punto d del compito 7b. Stessi estremi, ma la freccia puo' essere stata
                    // spostata su un'ALTRA PORTA degli stessi due dispositivi - o staccata dalle
                    // porte del tutto. L'impianto non sa memorizzarlo: JunctionDto porta origine,
                    // destinazione e priorita', non la porta (e cambiarlo tocca il contratto, non
                    // questo compito). Scrivere niente e' l'unica cosa che si puo' fare; TACERLO
                    // no, perche' il disegno mostrerebbe un aggancio che l'impianto non ha e che
                    // sparira' alla prima riapertura.
                    //
                    // Ma SOLO per una freccia che porta il nostro marchio (FromOurMark): l'aggancio
                    // canonico con cui si confronta - DrawioDocument.Anchors, le porte del registro
                    // - esiste unicamente su una freccia uscita da DrawioDocument.Build. La freccia
                    // nata in questa sessione l'operatore l'ha tirata a mano, agganciandola a uno
                    // dei punti di connessione che draw.io offre ({0, .25, .5, .75, 1}), fra i
                    // quali le porte del registro non ci sono: l'impianto non ha mai conosciuto una
                    // porta per lei, quindi non c'e' nessuno spostamento da dichiarare. Confrontare
                    // lo stesso significava accusare l'operatore, a ogni salvataggio e su una tela
                    // che non aveva toccato.
                    if (known.FromOurMark && AnchorsMoved(edge, DrawioDocument.Anchors(opened, before)))
                    {
                        refusals.Add(new DrawioRefusal(edge.Id,
                            "the arrow has been hooked onto other ports of the same two devices: the plant does not store the port of a junction, the hooking was not written"));
                    }

                    // Row 20 of the parity table. Same ends, but the operator can have changed the
                    // arrow's wcsPriority by hand (draw.io's "Edit Data" shows it): until this
                    // comparison existed, edge.Priority was never even looked at on this branch -
                    // the gesture evaporated, with nothing written and nothing said. That is D203
                    // in the one place of the junction round that had no answer for it.
                    //
                    // The comparison is between the two WEIGHTS - the drawn one and the one the
                    // plant's number means - and NOT between two numbers, and it goes through
                    // JunctionWeights.FromPriority, the very same function DrawioDocument.Build
                    // used to decide what to write on the arrow. That is what makes it unable to
                    // destroy anything: a junction the operator never touched comes back declaring
                    // exactly the weight we drew it with, so it compares equal whatever number the
                    // plant carries. With the other rule that used to live in DrawioDocument
                    // (priority == 0) a junction at 50 was drawn "bassa" and read as high here:
                    // reopened and saved without a finger laid on it, it would have been written
                    // back to 99 - a silent demotion produced by the disagreement between two
                    // copies of one rule.
                    switch (WeightOf(edge))
                    {
                        // Declared, and different from what the plant means: this is the gesture.
                        case { } drawnWeight when drawnWeight != JunctionWeights.FromPriority(before.Priority):
                            junctionPriorities.Add(new DrawioJunctionPriority(known.Id, drawnWeight.ToPriority()));
                            break;

                        // Declared and in agreement: nothing to write. This is the ordinary case -
                        // every arrow of every save that nobody touched goes through here.
                        case { }:
                            break;

                        // Not an order, and not a mistake either: the arrow carries no wcsPriority
                        // at all. That is what an arrow the OPERATOR drew in this session looks
                        // like - we never wrote the attribute on it - and an absence cannot mean
                        // "make it low": it would demote, at the next save, every junction created
                        // by hand. Silence in, silence out.
                        case null when edge.Priority is null:
                            break;

                        // A value we do not know (wcsPriority="media", typed by hand). It does NOT
                        // become "bassa": on a junction that already exists that would be a
                        // demotion nobody asked for - which is exactly what PriorityOf does for a
                        // junction being BORN, and rightly so, because there a value has to be
                        // chosen and low is the plant's default. Here there is already a value on
                        // the plant, and leaving it alone is the choice that loses nothing. But it
                        // is SAID, naming the arrow and the value: otherwise the operator walks
                        // away believing he changed something.
                        default:
                            // Rilievo 4 della revisione: this used to name before.Priority - the
                            // number the plant had when the drawing was OPENED - while the
                            // "written" line the page builds at save time names the number the
                            // plant holds NOW (see DrawioEditor.razor, around the JunctionPriorities
                            // loop). Two messages about the same arrow, from two different
                            // snapshots. current is the closer of the two to "now": it is read
                            // again right before the delta is computed, after opened. A junction
                            // gone from current (deleted meanwhile) falls back to before.Priority -
                            // the last number we actually know.
                            var nowPriority = current.Junctions.FirstOrDefault(j => j.JunctionId == known.Id)?.Priority
                                ?? before.Priority;
                            notices.Add(new DrawioNotice(edge.Id,
                                $"priority '{edge.Priority}' not recognized (expected values: '{DrawioDocument.HighPriorityValue}', '{DrawioDocument.LowPriorityValue}'): the junction keeps the priority it already has on the plant ({nowPriority}), it was not changed"));
                            break;
                    }

                    continue;
                }

                // Punto b del compito 7b: la giunzione riagganciata si RIFA' - cancellazione della
                // vecchia piu' creazione della nuova - invece di essere respinta, cosi' il gesto
                // dell'operatore ha effetto. La via e' quella gia' provata del "cancella la freccia
                // e ridisegnala": non si aggiunge 'known' a drawnJunctionIds, quindi il giro delle
                // cancellazioni piu' sotto porta via la vecchia, e la freccia prosegue qui come se
                // fosse nuova. Ma SOLO se la freccia nuova e' davvero scrivibile: se non lo e', la
                // vecchia resta dov'e' (drawnJunctionIds) e si dichiara il rifiuto - cancellare
                // senza ricreare sarebbe scrivere meta' gesto, il modo peggiore di perdere un dato.
                //
                // A JUNCTION ANOTHER HAND ALREADY TOOK (queue item 11) goes down the same road with
                // other words: there is nothing for this save to delete - the deletion round leaves
                // alone what the plant no longer holds - so "redone as a deletion plus a creation"
                // was false, and so was "the plant still holds the ends it had" on a refusal.
                var takenByAnotherHand = takenAwayByAnotherHand.Contains(known.Id);
                if (!Writable(edge, out var moved, out var movedTo, out var why))
                {
                    drawnJunctionIds.Add(known.Id);
                    refusals.Add(new DrawioRefusal(edge.Id, takenByAnotherHand
                        ? $"the junction has been re-hooked, or detached, in the drawing, but the new arrow {why}: it was not written, and the plant does not hold the junction any more either - somebody else removed it while you were drawing"
                        : $"the junction has been re-hooked, or detached, in the drawing, but the new arrow {why}: the plant still holds the ends it had, it was not written back"));
                    continue;
                }

                // The number the junction keeps is read from CURRENT and not from 'before', the
                // same way the SameEnds branch above answers the same question: see
                // PriorityOfRedone.
                var priorityNow = current.Junctions.FirstOrDefault(j => j.JunctionId == known.Id)?.Priority
                    ?? before.Priority;
                newJunctions.Add(new DrawioNewJunction(edge.Id, moved, movedTo,
                    PriorityOfRedone(edge, before, priorityNow, notices)));
                // L'identificativo cambia: la giunzione nuova lo riceve dall'host alla creazione
                // (JunctionRequest non ne porta uno). Non e' un rifiuto, ma chi tenesse altrove il
                // vecchio JunctionId deve poterlo sapere - la stessa dichiarazione che
                // DeleteItemCommand fa per le giunzioni che il suo annulla rimette.
                if (takenByAnotherHand) reHookedAfterAnotherHand.Add(known.Id);
                notices.Add(new DrawioNotice(edge.Id, takenByAnotherHand
                    ? "junction re-hooked: somebody else had already removed it from the plant while you were drawing, so this save deleted nothing - it created the connection you drew, with a new identifier"
                    : "junction re-hooked: redone as a deletion plus a creation, with a new identifier"));
                continue;
            }

            if (!Writable(edge, out var source, out var target, out var reason))
            {
                refusals.Add(new DrawioRefusal(edge.Id, $"the arrow {reason}: it was not created, it stays a drawing"));
                continue;
            }

            newJunctions.Add(new DrawioNewJunction(edge.Id, source, target, PriorityOf(edge, notices)));
        }

        // L'altra meta' del punto d, e quella comune: non lo spostamento di una freccia che
        // l'impianto gia' conosce, ma l'AGGANCIO INIZIALE di una freccia nuova. L'operatore la tira
        // sul lato che vuole; JunctionRequest porta origine, destinazione e priorita' e basta,
        // quindi la porta non arriva sull'impianto e alla riapertura DrawioDocument.Build riscrive
        // la freccia sulle porte del registro: salta altrove. E' parola per parola la ragione per
        // cui il punto d non tace - il disegno mostrerebbe un aggancio che l'impianto non ha e che
        // sparira' alla prima riapertura - quindi si dichiara anche qui.
        //
        // Non e' un rifiuto: la giunzione si crea davvero. E' una voce, come quella della priorita'
        // (si veda PriorityOf), e vale per OGNI giunzione nuova, perche' draw.io non offre affatto
        // i punti delle nostre porte (si veda AnchorTolerance).
        //
        // UNA riga per tutte, non una per freccia. La condizione e' identica per ogni freccia nuova,
        // quindi ripeterla non aggiunge niente a chi legge: chi salva dieci frecce si troverebbe
        // dieci righe uguali, e il verbale diventerebbe il posto dove i rifiuti veri si nascondono
        // invece di quello dove si trovano. Raggruppata non vuol dire pero' anonima: gli
        // identificativi delle frecce restano tutti, perche' "nominando chi" e' la regola di questo
        // file.
        if (newJunctions.Count > 0)
        {
            notices.Add(new DrawioNotice(string.Join(", ", newJunctions.Select(j => j.Id)),
                newJunctions.Count == 1
                    ? "new junction: the hooking you drew is not stored - the plant does not hold the port of a junction - and at the next opening the drawing hooks the arrow back onto the registry's ports"
                    : $"{newJunctions.Count} new junctions: the hooking you drew is not stored - the plant does not hold the port of a junction - and at the next opening the drawing hooks the arrows back onto the registry's ports"));
        }

        // THE HEAD TAKEN OFF IS A DELETION THE DRAWING ASKED FOR, and the last-resort defence does
        // not undo it. The owner's rule of 2026-09-19 says that between two devices it is the head
        // that says "connection": take it off and the junction dies, while the cell stays perfectly
        // well on the canvas. Without this distinction, widening the defence to the session's names
        // would keep it alive - that is, make the two conditions disagree again, the other way
        // round.
        //
        // WHICH ONES THEY ARE is worked out HERE and no longer at the bottom, because the deletion
        // happens a few lines below and an answer that arrives after the fact is not an answer. The
        // round over the lines with no head, further down, READS this list instead of asking the
        // question again: two copies of one question is exactly how this family of defects kept
        // coming back.
        foreach (var line in drawn.UndirectedLinesBetweenDevices)
            if (PlantJunctionOf(line.Id) is { } heldByThePlant)
                unhookedByTheHead.Add((line.Id, heldByThePlant.Id, DrawioReader.HeadsOf(line.Style)));

        var theHeadAsksForThisDeletion = unhookedByTheHead.Select(pair => pair.JunctionId).ToHashSet();

        var deletedJunctions = new List<Guid>();
        foreach (var junction in opened.Junctions)
        {
            // WHAT IS NO LONGER THERE IS NOT DELETED. Somebody else took it away while this page
            // was open: this save is not deleting it, and putting it among the deletions would
            // have the operator read a gesture of his own where there was somebody else's. What he
            // is owed is the notice, and it is written further down.
            if (takenAwayByAnotherHand.Contains(junction.JunctionId)) continue;

            if (drawnJunctionIds.Contains(junction.JunctionId)) continue;
            // Stessa domanda dei dispositivi: una giunzione che il disegno non ha mai portato non
            // e' stata cancellata da nessuno.
            if (!DrawioDocument.IsDrawn(opened, junction)) continue;

            // E la stessa difesa di ultima istanza: se il disegno porta ancora l'identificativo,
            // ma non piu' come giunzione nostra, non e' stata cancellata.
            //
            // AND WHEN WHAT CARRIES IT IS THE CELL ITSELF, the cell is taken back. For a long time
            // this was called a choice between losing the operator's cell and losing the junction,
            // and it was a false dilemma: that is not a line of the operator's, it is OUR cell with
            // its mark taken off - Ctrl+M, the row's bin, Apply, three gestures - and the plant
            // knows which cell it drew that junction on. Knowing that, nothing has to be lost: the
            // junction stays, the cell stops being free drawing, and at the next opening
            // DrawioDocument.Build draws it again with its mark. Leaving it in the decoration as
            // well is what put two elements under one identifier on the canvas.
            //
            // WHICH IS WHY THE CELL IS NAMED IN THE SENTENCE: what the operator did has an effect,
            // and it is not the effect he was after.
            if (WhereInTheDrawing(junction.JunctionId) is { } whereJunction)
            {
                // THE CELL IS NAMED, like its twin below and the session's arrow further down.
                // Usually it is this junction's own identifier - after a reload the cell carries
                // the name the plant gave it - but not always, and where the two differ a sentence
                // that names neither cannot be checked at all.
                if (TheCellThePlantWillRedraw(junction.JunctionId, plantJunctionByDrawnEdge) is { } itsCell)
                {
                    refusals.Add(new DrawioRefusal(junction.JunctionId.ToString(),
                        $"the mark was taken off this cell (cell {itsCell}), and the plant still holds the junction it was drawn for: the junction was not deleted, the cell is not free drawing, and at the next opening it comes back as a junction of ours"));
                }
                else
                {
                    refusals.Add(new DrawioRefusal(junction.JunctionId.ToString(),
                        $"the drawing still carries this junction, but not as a junction of ours - {whereJunction}: it was not deleted"));
                }

                continue;
            }

            // AND THE SAME DEFENCE WITH THE SECOND IDENTITY: the arrow this session drew is still
            // on the canvas, under draw.io's name for it.
            //
            // WITH THE HEAD GUARD, and that is the whole difference between the two identities.
            // With the plant's name the defence always wins, because that identifier turns up
            // SOMEWHERE ELSE - in a note, in a mangled mark - and is not a gesture on the line
            // itself. With the session's name what is found is THE CELL ITSELF, which stays on the
            // canvas even when the operator's gesture asks for the deletion: taking the head off
            // between two devices is exactly that gesture (the owner's rule of 2026-09-19).
            // Without the guard the two conditions would disagree again, the other way round.
            if (!theHeadAsksForThisDeletion.Contains(junction.JunctionId)
                && WhereTheArrowOfThisSessionIs(junction.JunctionId) is { } whereArrow)
            {
                // AND THE CELL IS TAKEN BACK HERE TOO - owner's decision of 2026-09-20, and the
                // reason that carried it is the rule this series has ratified four times: THE
                // GESTURE DOES THE SAME THING BEFORE AND AFTER A RELOAD. After a reload the cell
                // carries the plant's own name, the redraw takes its place and one line is left on
                // the canvas; before a reload it carries draw.io's, so leaving it there left TWO
                // lines for one arrow - the plant's, redrawn under its own name, and his. On the
                // screen nothing changes: the plant draws the same arrow between the same two
                // devices. And the cell is not the operator's: it is ours, without its mark.
                //
                // See TheCellThePlantWillRedraw, which is the ONE property - not "which branch of
                // the round we are in", which is where this went wrong once already.
                if (TheCellThePlantWillRedraw(junction.JunctionId, plantJunctionByDrawnEdge) is { } hisCell)
                {
                    // THE CELL IS NAMED, and not only because the operator has to know which one it
                    // is: a sentence that names it is a sentence that can be CHECKED - the net that
                    // reads the report's promises reads the name out of the line itself and goes to
                    // look for that cell on the canvas of the next opening. Written "(cell x)", the
                    // way the rest of the report names a cell, because the name is draw.io's here
                    // and is not an identifier of the plant's: no rule about its shape holds.
                    refusals.Add(new DrawioRefusal(junction.JunctionId.ToString(),
                        $"the arrow you drew in this session is no longer a connection of ours, and the plant still holds the junction it was drawn for: the junction was not deleted, the cell of your arrow (cell {hisCell}) is not free drawing, and at the next opening it comes back as a junction of ours"));
                }
                else
                {
                    refusals.Add(new DrawioRefusal(junction.JunctionId.ToString(),
                        $"the drawing still carries this junction, but not as a junction of ours - {whereArrow}: it was not deleted"));
                }

                continue;
            }

            deletedJunctions.Add(junction.JunctionId);
        }

        // AND THE SENTENCE THAT WAS MISSING ALTOGETHER. It is not a deletion of this save's, so it
        // is not among the deletions; but the line goes off the operator's canvas at the next
        // opening, and until now it went without one line of the report saying so - measured
        // against the real binary, "GIUNZIONI SULL'IMPIANTO ADESSO: 0, RIFIUTI: 0, VOCI: 0".
        //
        // Only of the ones the page had DRAWN: a junction the drawing did not show goes off nobody's
        // canvas, and a notice about it would be noise.
        // THE DEVICES FIRST, because a device going is the bigger news: every connection hanging
        // from it went with it, and the junction notices below would otherwise read as four
        // separate mysteries.
        // WHAT THIS SENTENCE USED TO PROMISE, and why it does not any more: it said "the shape goes
        // off your drawing at the next opening". Measured by the review of the row 15 batch, that is
        // true after ONE save and false after TWO - save again without reopening and the cell, now
        // a marked cell the plant does not know, comes back into the decoration by the drawing's
        // invariant. It is the same defect, with the same shape, that the junction arm of this file
        // was already corrected for: a promise the next save does not keep.
        //
        // The sentence now says only what holds in both cases. The difference between the two was
        // measured on the real binary (DrawioThePlantChangedUnderneathTest) and the owner decided it
        // on 27/9: a device whose cell the drawing still carries leaves the drawing at THIS save,
        // with its arrows, and its line is the page's (see goneCellsOf) - so it is skipped here.
        //
        // TWO STORIES are left for this sentence (queue item 10), and it told only the second one.
        // Most often the operator had removed the device's shape himself: nothing of it is on the
        // canvas, and "the shape on your canvas" named a shape that is not there. Less often a cell
        // of that device IS still on the canvas but not as a device of ours - its mark mangled, or
        // taken off by hand - which goneCellsOf does not take away, and there the old words hold.
        // Asked of EVERY cell the reader read, through PlantItemOf, so that both names count - the
        // plant's and draw.io's for a device dropped in this session - and a note that merely
        // mentions the identifier does not.
        foreach (var item in opened.Items)
        {
            if (!devicesTakenAwayByAnotherHand.Contains(item.ItemId)) continue;
            if (!DrawioDocument.IsDrawn(opened, item)) continue;
            if (goneCellsOf.ContainsKey(item.ItemId)) continue;

            var hanging = opened.Junctions
                .Count(junction => junction.SourceItemId == item.ItemId || junction.DestinationItemId == item.ItemId);
            var withItsConnections = hanging == 0
                ? string.Empty
                : $", and the {(hanging == 1 ? "connection" : "connections")} hanging from it went with it";
            var stillOnTheCanvas = drawn.CellsTakenOutOfTheDrawing.Concat(drawn.CellsLeftInTheDrawing)
                .Any(cell => PlantItemOf(cell.Id) == item.ItemId);

            notices.Add(new DrawioNotice(item.ItemId.ToString(), (stillOnTheCanvas
                ? $"{item.Name}: somebody else removed this device from the plant while you had the drawing open" +
                  withItsConnections + ": this save did not touch it, and the shape on your canvas is no longer the plant's."
                : $"{item.Name}: deleted on the host by somebody else while you were drawing" +
                  withItsConnections + "; you had removed it from the drawing too - nothing to do.")
                + WhatIsLeftOfItsConnections(item.ItemId, hanging)));
        }

        // THE CELLS OF THOSE CONNECTIONS STILL ON THE CANVAS (queue item 16), said in the device's
        // line because the junction no longer has one of its own - see the junctions' loop below.
        // Most often there are none: the operator took the device off with its arrows. But a plain
        // Delete in draw.io takes the shape and NOT its connections (the "delete" action calls
        // deleteCells without edges; Ctrl+Delete is "deleteAll"), so the arrow is left on the canvas
        // loose at the end where the device was - and the junction's line used to tell him to draw a
        // new arrow between two devices, one of which no longer exists.
        //
        // What happens to each cell is what the decoration below does with it, asked the same way:
        // a cell of OURS goes with the junction another hand removed (AnotherHandTookItAndTheCellWasOurs),
        // anything else stays as the operator's own drawing. An arrow this save wrote as a connection
        // - re-hooked between two living devices - is not that connection's any more, and its own
        // line says what it became.
        string WhatIsLeftOfItsConnections(Guid itemId, int hanging)
        {
            var theirCells = edgeCells
                .Where(cell => PlantJunctionOf(cell.Id) is { } its
                    && takenAwayByAnotherHand.Contains(its.Id)
                    && opened.Junctions.Any(j => j.JunctionId == its.Id && (j.SourceItemId == itemId || j.DestinationItemId == itemId)))
                .Where(cell => !leavingTheCanvas.Contains(cell.Id))
                .Where(cell => newJunctions.All(written => written.Id != cell.Id))
                .ToList();
            var leaving = theirCells.Count(ACellOfOurs);
            var staying = theirCells.Count - leaving;
            var which = hanging == 1 ? "that connection" : "those connections";

            return (leaving switch
                {
                    0 => string.Empty,
                    1 => $" An arrow of {which} is still on your canvas: it goes off your drawing at the next opening.",
                    _ => $" {leaving} arrows of {which} are still on your canvas: they go off your drawing at the next opening.",
                })
                + (staying switch
                {
                    0 => string.Empty,
                    1 => $" A line you have for {which} is still on your canvas: it stays there, as your free drawing.",
                    _ => $" {staying} lines you have for {which} are still on your canvas: they stay there, as your free drawing.",
                });
        }

        foreach (var junction in opened.Junctions)
        {
            if (!takenAwayByAnotherHand.Contains(junction.JunctionId)) continue;
            if (!DrawioDocument.IsDrawn(opened, junction)) continue;
            // It went with a device whose cell leaves the drawing now: that device's line names
            // the connections hanging from it, and its arrows leave with it.
            if (junctionsGoneWithThem.ContainsKey(junction.JunctionId)) continue;
            // Its arrow was re-hooked and this save creates the connection it now draws: that line
            // is the one that tells it (fix round of queue item 11).
            if (reHookedAfterAnotherHand.Contains(junction.JunctionId)) continue;
            // It went with a device somebody else deleted whose shape the operator had already taken
            // off (queue item 16): that device's line, above, counts it and says what is left of its
            // cells. Its own words - "the line goes off your drawing at the next opening. To join the
            // two devices again, draw a new arrow" - were false twice: the line was usually gone
            // already, and one of the two devices does not exist any more.
            if (devicesTakenAwayByAnotherHand.Contains(junction.SourceItemId)
                || devicesTakenAwayByAnotherHand.Contains(junction.DestinationItemId)) continue;

            // THREE STORIES, because there are three, and a sentence that tells the wrong one is
            // worse than no sentence: it is a promise the next save does not keep.
            //
            // It told two until somebody read them a save apart. The promise "the next save makes
            // the connection again" was being given to ANY cell that was not ours - and a cell with
            // a mark this drawing cannot read is not ours, by the rule of the second batch, but it
            // is not going to reconnect anything either: the reader keeps it out of the junctions
            // and the save refuses it in the same report that promised.
            //
            // So the question is not "whose cell is it" but "what will happen to it", and it is
            // asked of the reader's own division of the drawing: a cell that WILL make the
            // connection again is one the reader promoted to a junction with no mark on it at all.
            // AND "WILL" MEANS WILL, which is where the promise was being given to a cell that
            // cannot keep it. Being a line the reader promoted is not enough: an arrow that does
            // not hook two ports is turned down at the next save exactly as it would be at this
            // one, and the report was telling the operator the opposite.
            //
            // WHO DECIDES IS Writable, ASKED ONCE. Not a second copy of its rule written into the
            // report - that copy would drift from the rule it describes, which is how this family
            // of defects has come back - and not the refusals already written either: a cell the
            // plant still recognizes never reaches those three questions at this save, so its
            // silence says nothing about the next one. The three questions are what the next save
            // will ask, so they are the ones to ask now.
            var itWillConnectAgain = drawn.Junctions
                .Any(edge => edge.Mark is null
                    && PlantJunctionOf(edge.Id) is { } his && his.Id == junction.JunctionId
                    && Writable(AsDrawn(edge), out _, out _, out _));

            // AND THE CELLS THE READER LEFT IN THE DECORATION COUNT TOO. They were missing, and
            // with them the commonest cell of all: the operator's own unmarked line, which the
            // reader never takes out because it never claimed to be ours. It stays exactly where he
            // drew it - and it was being told that it was about to go off his drawing.
            var hisCellIsStillThere = drawn.CellsTakenOutOfTheDrawing.Concat(drawn.CellsLeftInTheDrawing)
                .Any(edge => !ACellOfOurs(edge)
                    && PlantJunctionOf(edge.Id) is { } kept && kept.Id == junction.JunctionId);

            notices.Add(new DrawioNotice(junction.JunctionId.ToString(),
                itWillConnectAgain
                    ? "somebody else removed this connection from the plant while you had the drawing open, and the arrow you drew for it is still on your canvas: this save did not touch the plant, and it stays your drawing. The next save makes the connection again - between two devices an arrow with a head is a connection - and whoever removed it finds out at his own next save."
                    : hisCellIsStillThere
                        ? "somebody else removed this connection from the plant while you had the drawing open, and the cell you have for it is not going to connect them again - this save has told you why, on its own line: it stays your free drawing. To join the two devices, draw a new arrow from one to the other and save."
                        : "somebody else removed this connection from the plant while you had the drawing open: this save did not touch it, and the line goes off your drawing at the next opening. To join the two devices again, draw a new arrow from one to the other and save."));
        }

        // The other half of the missed aim, and the one the reader could only carry as a fact: the
        // arrow let go BESIDE the second device instead of on it, hooked to one device and to
        // nothing. The one let go in the MIDDLE of it is refused by name a few lines above; this
        // one used to produce nothing at all, and it is at least as likely a slip.
        //
        // A NOTICE and not a refusal, and the difference is not cosmetic. Nothing was refused: the
        // arrow never was a candidate - it joins one device, and the plant has no such connection
        // to hold - so calling it a refusal would put it among the lines where the operator goes
        // looking for what the plant would not take. It is a drawing, it stays a drawing, and it is
        // said so that nobody walks away believing he connected something.
        //
        // These arrows are IN the decoration already: the reader leaves them there and only stops
        // throwing away the fact. Nothing is written about them, here or anywhere.
        foreach (var arrow in drawn.ArrowsWithAnEndInTheAir)
        {
            // An arrow of a junction that went with a device somebody else deleted leaves the
            // drawing: "it stays a drawing" would be false.
            if (leavingTheCanvas.Contains(arrow.Id)) continue;

            // UNLESS THE PLANT ALREADY HOLDS THE CONNECTION THIS ARROW MADE, which is a different
            // piece of news and not a variant of the same one. The operator pulled this arrow in
            // this session, the save before turned it into a junction, and now he has let one end
            // go: the junction is NOT deleted - the deletion round refuses that by name - and since
            // the plant draws that connection again at the next opening, the cell does not stay
            // either (owner's decision of 2026-09-20).
            //
            // So the line below said the opposite of what happens, twice over: a junction WAS
            // created, and it does not stay a drawing. Measured: 48 roads, by the net that reads
            // what the report promises - and it is the same fact the deletion round states under
            // the junction's name, said here under the cell's, which is the name the operator can
            // see.
            if (PlantJunctionOf(arrow.Id) is { } alreadyHeld
                && !deletedJunctions.Contains(alreadyHeld.Id)
                && !takenAwayByAnotherHand.Contains(alreadyHeld.Id))
            {
                // THE CONNECTION IS NAMED, for the same reason the deletion round names the cell:
                // a sentence that names what it speaks of is a sentence that can be checked. Here
                // the two names may or may not be the same one - the plant's, after a reload;
                // draw.io's on the cell and the plant's on the connection, before one - and the
                // sentence is true either way.
                notices.Add(new DrawioNotice(arrow.Id,
                    $"one end of this arrow hangs from a device and the other from nothing, and the plant still holds the connection it made ({alreadyHeld.Id}): the connection was not removed, this cell is not free drawing, and at the next opening the plant draws that connection again between the two devices it joined - to take it away, delete the line and save."));
                continue;
            }

            notices.Add(new DrawioNotice(arrow.Id,
                "one end of this arrow hangs from a device and the other from nothing: it joins no two devices, so no junction was created and it stays a drawing - to connect two devices, let go of the arrow on a connection point of the second one"));
        }

        // THE LINE BETWEEN TWO DEVICES THAT DOES NOT POINT ANYWHERE, which is the owner's rule of
        // 2026-09-19 arriving where the operator can read it: between two devices one may draw only
        // lines WITH a head, the head says which way the connection goes, and a two-way connection
        // is two lines with a head, one each way.
        //
        // It is SAID and not swallowed, and that is the whole of the change here. Before the rule
        // asked for a head, a decorative line between two devices became a junction of the plant -
        // no refusal, and no way to keep it as a drawing. Now it is not a junction; if nothing were
        // said it would be the other half of the same fault, the operator drawing a line between
        // two devices, reading "save succeeded" and having connected nothing.
        //
        // A NOTICE, for the reason the loop above writes out: nothing was refused, because a line
        // with no direction never was a candidate. And these lines are IN the decoration - the
        // reader leaves them there and only stops throwing away the fact - so nothing is at stake
        // for the drawing itself.
        //
        // THE WAY OUT NAMED IS ONE THAT WORKS, and it is named the way the mirror's refusal names
        // it: Ctrl+E, Edit Style. That road is measured - a browser test drives that very dialog -
        // and this message is not free to send the operator looking for a panel nobody has checked
        // is reachable in this embedded draw.io.
        foreach (var line in drawn.UndirectedLinesBetweenDevices)
        {
            // UNLESS THE PLANT HOLDS A JUNCTION FOR IT, which is review finding 1 arriving from
            // the other side. This is the arrow the operator drew and saved in this session and
            // has now taken the head off: the reader cannot tell it from a decorative line - it
            // carries no mark, because the document never goes back to draw.io after a save - and
            // the sentence below was therefore said about a line that HAD a junction, while the
            // same save was deleting it. "No junction was created and it stays a drawing" was
            // false word for word, and it is the family of defect this branch has already paid
            // for twice. The junction is deleted just above, by the round that does not find it in
            // drawnJunctionIds; what is said about it is said once, below.
            //
            // THIS IS ALSO WHERE THE MARKED CELLS ARRIVE, since the second review's two GRAVE
            // findings moved the head question into the reader: the cell of a reloaded page whose
            // head has been taken off comes here too, and the two conditions read the same from
            // here on. The plant answers the same question for both - through PlantJunctionOf,
            // which knows the mark road and the session road - so a line that HAD a junction gets
            // the deletion and its notice, and one the plant knows nothing about gets the notice
            // below, which is true of it word for word.
            // Collected FURTHER UP, before the deletion round: what is left here is only the
            // notice for the lines the plant knows nothing about.
            if (PlantJunctionOf(line.Id) is not null) continue;

            notices.Add(new DrawioNotice(line.Id, DrawioReader.HeadsOf(line.Style) == DrawioArrowHeads.Both
                ? "this line joins two devices and has a head at BOTH ends: a connection goes one way, and a connection both ways is two connections, one line with its head on each device. No junction was created and it stays a drawing - to connect the two devices, take one of the two heads off (select the line and press Ctrl+E, Edit Style, then put startArrow=none or endArrow=none in its style) and draw a second line the other way if both directions are wanted."
                : "this line joins two devices and has no arrowhead: between two devices it is the head that says 'connection', and which way it points is the way the connection goes. No junction was created and it stays a drawing - to connect the two devices, draw an arrow from one to the other, or give this line a head (select it and press Ctrl+E, Edit Style, then put endArrow=classic in its style)."));
        }

        // THE HEAD TAKEN OFF A LINE THAT HAD A JUNCTION, said ONCE and said AFTER the fact. The
        // two roads that fill this list - the marked cell of a reloaded page and the unmarked
        // arrow of this session - are the two conditions review finding 1 measured doing opposite
        // things; from here on they read the same, because the line is written in one place and
        // the outcome it names is the deletion that has already been decided just above.
        //
        // NOTHING IS SAID WHEN THE JUNCTION WAS NOT DELETED, and it is the honest half of the
        // repair. The deletion round can turn into a refusal - the identifier is still somewhere
        // in the drawing, so the junction stays - and that refusal names the junction and says
        // why. A line here promising a deletion that did not happen would be this very defect with
        // the sign flipped, which is the whole reason this list exists instead of a notice written
        // where the head is read.
        foreach (var (edgeId, junctionId, heads) in unhookedByTheHead)
        {
            if (!deletedJunctions.Contains(junctionId)) continue;

            notices.Add(new DrawioNotice(edgeId, heads == DrawioArrowHeads.Both
                ? $"this line has been given a head at BOTH ends, and a connection goes one way: the junction it joined the two devices with ({junctionId}) has been DELETED from the plant. To join them again, take one of the two heads off (select the line and press Ctrl+E, Edit Style, then put startArrow=none or endArrow=none in its style) and save; a connection both ways is two lines, one head each way."
                : $"the arrowhead has been taken off this line, and between two devices it is the head that says 'connection': the junction it joined the two devices with ({junctionId}) has been DELETED from the plant. To join them again, give the line a head (select it and press Ctrl+E, Edit Style, then put endArrow=classic in its style) and save, or draw a new arrow from one device to the other."));
        }

        // THE INVARIANT OF THE OPERATOR'S DRAWING, and it is written here, once, because this is
        // the third round of this batch spent repairing one of its cases:
        //
        //     EVERY LINE THE READER TOOK OUT OF THE DECORATION, AND THAT THE PLANT DOES NOT HOLD
        //     WHEN THIS SAVE IS DONE, GOES BACK INTO THE DRAWING - EXACTLY ONCE.
        //
        // It has TWO halves and they are held together on purpose, because each one alone is a
        // defect:
        //
        // - NOTHING VANISHES. A line the plant does not hold is the operator's drawing, and every
        //   refusal and every notice this file writes about one says so in those words - "it was
        //   not created, it stays a drawing". Until the line was in one place, the sentence was
        //   true for some cells and false for others, and the operator could not tell which: he
        //   read that his line stayed and found an empty canvas at the next opening.
        // - NOTHING IS DOUBLED. A line the plant DOES hold is redrawn from the plant by
        //   DrawioDocument.Build at the next opening, so a copy in the decoration would put two
        //   lines on top of each other between the same two devices - one of them invisible, and
        //   invisible is how a wrong plant gets saved. One fact, one copy.
        //
        // WHY AN INVARIANT AND NOT A THIRD CASE. The three defects this file has paid for are the
        // same defect: an arrow let go in the middle of a shape (refused, then deleted from the
        // canvas), a marked line whose head was taken off (deleted with nothing said), and a
        // marked line with an end detached (refused with "it stays a drawing", then gone). Each
        // was repaired by adding a list, and the next category of cell fell between the lists.
        // The two that were here - one asking "is it unmarked?" and one asking "is it a line of
        // ours between two devices?" - were two spellings of one question, and a cell could be in
        // neither. There is one round now, and the question it asks is not about the cell's mark
        // or its shape: it is about the PLANT, which is the only thing that decides whether
        // anybody else is going to draw that line.
        //
        // WHO SAYS WHAT WAS TAKEN OUT: the reader, and by SUBTRACTION rather than by a list of
        // arms (DrawioContents.CellsTakenOutOfTheDrawing). That is what makes this round cover a
        // category nobody has invented yet - it is in the list the moment the reader stops leaving
        // it in the decoration.
        //
        // WHY THE MARK GOES BACK WITH THE CELL. What returns is the element exactly as it came,
        // mark included: the cell keeps its identifier, the next opening reads it again the same
        // way, puts it back here again and says the same thing - the round trip is stable - and
        // the way out the notice names ("give the line a head and save") works straight from that
        // cell, which is born as a junction again because the plant holds no junction under its
        // identifier. Stripping the mark would mean rewriting the operator's cell, which is the
        // one thing this round must not do.
        var writtenEdgeIds = newJunctions.Select(j => j.Id).ToHashSet(StringComparer.Ordinal);

        // Whether the plant holds a cell under THIS identifier once the save is done - that is,
        // whether DrawioDocument.Build is about to draw it again at the next opening. It is the
        // "nothing is doubled" half, and it is asked by identity because that is what a doubled
        // cell is: two elements with one id.
        //
        // The three roads, and none of them is about the mark:
        // - the line was written as a junction in this very save. The plant gives the new junction
        //   an identifier of its own, so what comes back at the next opening is not this cell -
        //   but it IS this line, in the same place between the same two devices, and a copy here
        //   would draw it twice;
        // - the plant holds a junction under this identifier - by our mark or by the session's map
        //   (PlantJunctionOf) - and this save has not deleted it. A deletion REFUSED counts as
        //   held, which is the case that taught this half: the identifier is still somewhere in
        //   the drawing, so the plant keeps the junction and redraws the line itself;
        // - the plant holds a DEVICE under this identifier and this save has not deleted it. A
        //   line cannot be a device, but a cell carrying a device's identifier and a mark we
        //   cannot read can be a line (a mangled mark on a cell somebody pasted), and handing it
        //   back beside the device Build redraws would be two elements with one id - the drawing
        //   would not load the way either of them was meant to.
        bool ThePlantDrawsItItself(DrawioCell edge) =>
            writtenEdgeIds.Contains(edge.Id)
            || (PlantJunctionOf(edge.Id) is { } held && !deletedJunctions.Contains(held.Id)
                && !takenAwayByAnotherHand.Contains(held.Id))
            || (PlantItemOf(edge.Id) is { } item && !deleted.Contains(item));

        // WHOSE CELL IS IT. The plant writes wcs="device" and wcs="junction" on the cells it draws
        // itself; everything else on the canvas is the operator's - a mark we cannot read included,
        // because a mark we cannot read is not ours.
        static bool ACellOfOurs(DrawioCell edge) => edge.Mark is "device" or "junction";

        // SOMEBODY ELSE REMOVED THE JUNCTION THIS CELL CARRIED, and what happens to the cell then
        // is decided by whose it is - owner's decision of 2026-09-20.
        //
        // A cell of OURS was the plant's own drawing: it goes with the junction it drew, and
        // putting it back would leave on the canvas a line that claims to be a connection of the
        // plant's while the plant holds nothing. The OPERATOR'S own arrow stays where he drew it -
        // nothing leaves somebody's drawing because of somebody else's decision - and at the next
        // save it makes the connection again, because between two devices an arrow with a head IS
        // a connection. Whoever deleted it finds out at his own next save.
        bool AnotherHandTookItAndTheCellWasOurs(DrawioCell edge) =>
            ACellOfOurs(edge)
            && PlantJunctionOf(edge.Id) is { } ours
            && takenAwayByAnotherHand.Contains(ours.Id);

        // EXACTLY ONCE, and DistinctBy is what says it rather than a comment: a drawing that
        // carried two elements with one identifier - which only hand-written XML can produce -
        // would otherwise come back with two, and the next opening would not be able to tell them
        // apart either.
        var backToTheDrawing = drawn.CellsTakenOutOfTheDrawing
            .Where(edge => !ThePlantDrawsItItself(edge))
            .Where(edge => !AnotherHandTookItAndTheCellWasOurs(edge))
            // Nor an arrow leaving the canvas with a device somebody else deleted: the page takes it
            // off, and a copy here would draw it again at the next opening.
            .Where(edge => !leavingTheCanvas.Contains(edge.Id))
            .DistinctBy(edge => edge.Id, StringComparer.Ordinal)
            .ToList();

        // AND THE ARROWS OF OURS THAT GO AT THE NEXT OPENING (queue item 19): an arrow whose
        // connection another hand removed between two devices that are both still there, or the
        // arrow a plain Delete left loose at a device somebody else then deleted (queue item 16).
        // It stays on the canvas for now, and a line of the operator's can hang from it - but it is
        // not in the copy stored here (AnotherHandTookItAndTheCellWasOurs) and the plant does not
        // draw it either. Measured on the real binary: the stored line kept target= on that arrow,
        // and at the next opening it was in the model but not drawn, item 12's mechanism. So the
        // line lets go of it as item 18's does, in the middle of the arrow, which is where the
        // canvas has it meet the arrow now. An arrow with nothing to work its middle out from - an
        // end on something that is neither a device nor a stored point - keeps the hook: there is
        // no place to put the end.
        foreach (var arrow in drawn.CellsTakenOutOfTheDrawing
                     .Where(AnotherHandTookItAndTheCellWasOurs)
                     .Where(edge => !ThePlantDrawsItItself(edge))
                     .Where(edge => !leavingTheCanvas.Contains(edge.Id)))
            if (!whereTheCellWas.ContainsKey(arrow.Id) && MiddleOf(arrow) is { } middle)
                whereTheCellWas[arrow.Id] = middle;

        // AND THE ARROWS THIS SAVE WRITES AS NEW JUNCTIONS (final review, C7, the residual of queue
        // item 19): the plant draws each one again at the next opening, but under the identifier the
        // host gives the new junction, which is not known until the write is done - and a line hooked
        // onto the arrow by its present name would hang from nothing there. It lets go in the middle
        // of the arrow, as item 19's does; from the next save of this session the session map knows
        // the junction, and the line is stored hooked onto it again (redrawnUnder, below).
        foreach (var arrow in edgeCells.Where(edge => writtenEdgeIds.Contains(edge.Id)).DistinctBy(edge => edge.Id, StringComparer.Ordinal))
            if (!whereTheCellWas.ContainsKey(arrow.Id) && MiddleOf(arrow) is { } middle)
                whereTheCellWas[arrow.Id] = middle;

        // The names the plant draws them under, for the cells the session maps know and the plant
        // keeps: not one this save deletes, nor one another hand took (those leave, or go back as the
        // operator's drawing), nor one written anew just above.
        foreach (var device in drawn.Devices.Where(cell => !leavingTheCanvas.Contains(cell.Id)))
            if (PlantItemOf(device.Id) is { } item && !deleted.Contains(item)
                && !string.Equals(device.Id, item.ToString(), StringComparison.OrdinalIgnoreCase))
                redrawnUnder.TryAdd(device.Id, item.ToString());
        foreach (var arrow in drawn.Junctions.Where(edge => !leavingTheCanvas.Contains(edge.Id) && !writtenEdgeIds.Contains(edge.Id)))
            if (PlantJunctionOf(arrow.Id) is { FromOurMark: false } held
                && !deletedJunctions.Contains(held.Id) && !takenAwayByAnotherHand.Contains(held.Id))
                redrawnUnder.TryAdd(arrow.Id, held.Id.ToString());

        // Appended, not rebuilt: what goes back is the cell exactly as it came, style, geometry and
        // ends included, because the reader kept the whole element. An arrow re-drawn from a few
        // fields would come back to the operator different from the one he left.
        // REBUILT FROM THE CELLS and no longer copied as one string, because one of them may have
        // to come out. The order is the reader's, which is the drawing's.
        //
        // AND WHAT COMES OUT IS DECIDED BY THE SAME QUESTION the cells the reader took out are
        // decided by: does the plant draw this itself? A cell of the free drawing that the plant is
        // about to redraw - ours with its mark taken off, under its name or under draw.io's - would
        // otherwise stand beside the redraw, and the operator would have two things where he has
        // one. The question is asked HERE, where the deletions are all known, and it is asked ONCE
        // for the whole drawing: the two halves used to answer it in two places, and they drifted.
        //
        // AND A LINE THAT STAYS LETS GO OF A DEVICE CELL THAT LEAVES (queue item 12). The page takes
        // that cell off the canvas AFTER this decoration was worked out, and draw.io disconnects the
        // operator's lines hanging from it there - but the copy stored on the host is this one, and
        // it still named the cell. Measured on the real binary: at the next opening draw.io drops a
        // hook to a cell that does not exist, the end is left with neither a cell nor a point, and
        // the line is in the model but not drawn at all (DrawioThePlantChangedUnderneathTest). So the
        // stored line lets go the same way: no hook, and a loose end at the centre of the device's
        // box - draw.io's own choice when it disconnects a line from a cell it has not drawn. The
        // canvas puts the end on the device's border, where the line met it; the next save from
        // that canvas stores its point instead. A line hooked onto one of the ARROWS that leave
        // (queue item 18) lets go the same way, in the middle of the arrow, where the canvas puts it
        // too (see whereTheCellWas) - and so does one hooked onto an arrow of ours that goes at the
        // next opening (queue item 19, just above).
        var decorationXml = string.Concat(drawn.CellsLeftInTheDrawing
                .Where(cell => !ThePlantDrawsItItself(cell))
                .Where(cell => !leavingTheCanvas.Contains(cell.Id))
                .Select(LetGoOfTheCellsThatLeave))
            + string.Concat(backToTheDrawing.Select(LetGoOfTheCellsThatLeave));

        // The devices somebody else deleted, in the plant's order, each with what the page takes
        // off the canvas for it and what its line has to say.
        var goneFromThePlant = opened.Items
            .Where(item => goneCellsOf.ContainsKey(item.ItemId))
            .Select(item => new DrawioDeviceGone(
                item.ItemId,
                item.Name is { Length: > 0 } name ? name : item.ItemId.ToString(),
                goneCellsOf[item.ItemId],
                goneArrowsOf.TryGetValue(item.ItemId, out var arrows) ? arrows : [],
                opened.Junctions.Count(junction => junction.SourceItemId == item.ItemId || junction.DestinationItemId == item.ItemId),
                goneAfterAChange.Contains(item.ItemId),
                goneArrowsOf.TryGetValue(item.ItemId, out var itsArrows) ? itsArrows.Count(reweightedArrows.Contains) : 0,
                looseLinesOf.GetValueOrDefault(item.ItemId)))
            .ToList();

        return new DrawioDeltaResult(moves, deleted, newDevices, newJunctions, deletedJunctions,
            junctionPriorities, refusals, notices, decorationXml)
        {
            GoneFromThePlant = goneFromThePlant,
        };
    }

    /// <summary>
    /// Whether this cell still sits directly on the canvas - that is, on the default layer
    /// <see cref="DrawioDocument.Build"/> put every cell of ours on. Only there does the geometry
    /// that comes back speak the plant's coordinates; see the class remarks.
    /// </summary>
    /// <remarks>
    /// A cell with <b>no</b> <c>parent</c> at all counts as not on the canvas. It is not a case
    /// draw.io produces - every cell under <c>&lt;root&gt;</c> carries one - but if it ever arrives
    /// we cannot say where that cell lives, and "we do not know" must fall towards the refusal:
    /// a wrong refusal costs a gesture, a wrong write costs the plant's coordinates.
    /// </remarks>
    private static bool OnTheCanvas(DrawioCell cell) => cell.Parent == DrawioDocument.DefaultLayerId;

    /// <summary>
    /// How this cell is mirrored, said in words, or <c>null</c> when it is not mirrored at all.
    /// </summary>
    /// <remarks>
    /// The two flips are answered in ONE sentence on purpose: mirroring both ways is one gesture of
    /// the operator's, and the refusal that quotes this has to be one line. The draw.io key is named
    /// alongside the word because that is what the operator finds if he opens the cell's style, and
    /// because "mirrored" alone would not tell him which of the two he did.
    /// </remarks>
    private static string? MirrorOf(DrawioCell cell) => (cell.FlipH, cell.FlipV) switch
    {
        (true, true) => "mirrored both ways (flipH and flipV in its style)",
        (true, false) => "mirrored horizontally (flipH in its style)",
        (false, true) => "mirrored vertically (flipV in its style)",
        _ => null,
    };

    /// <summary>
    /// La priorita' con cui nasce una giunzione disegnata (punto a del compito 7b), letta
    /// dall'attributo nostro <c>wcsPriority</c> - un DATO sulla freccia, non il suo aspetto: si
    /// veda <see cref="DrawioDocument.PriorityAttribute"/>.
    /// <para>
    /// Una freccia NUOVA, tirata dall'operatore, non porta quell'attributo: vale la predefinita
    /// dell'impianto, <b>bassa</b>, cioe' il numero 99 (nel WCS alta = 0 e bassa = 99, si veda
    /// <see cref="JunctionWeights"/>) - e la cosa si <b>dichiara</b> invece di sceglierla in
    /// silenzio. Non e' un rifiuto: la giunzione si crea, e' solo che la priorita' l'abbiamo scelta
    /// noi al posto suo, e chi salva deve poterlo leggere per andare a cambiarla se non e' quella
    /// che voleva.
    /// </para>
    /// </summary>
    private static int PriorityOf(DrawioCell edge, List<DrawioNotice> notices)
    {
        if (WeightOf(edge) is { } weight) return weight.ToPriority();

        notices.Add(new DrawioNotice(edge.Id, edge.Priority is null
            ? $"the arrow declares no priority: the junction is born at low priority ({JunctionWeights.LowPriority})"
            : $"priority '{edge.Priority}' not recognized (expected values: '{DrawioDocument.HighPriorityValue}', '{DrawioDocument.LowPriorityValue}'): the junction is born at low priority ({JunctionWeights.LowPriority})"));
        return JunctionWeights.LowPriority;
    }

    /// <summary>
    /// The priority a junction being <b>REDONE</b> is born at - the one re-hooked elsewhere, and
    /// the one whose head has been moved. It is not <see cref="PriorityOf"/>: there a junction is
    /// born out of nothing and a number has to be chosen, here one already exists, and throwing
    /// it away would be a demotion nobody asked for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MEDIO 1 of the second review, measured: turning a junction round took a priority of 50 or
    /// of 98 down to 0, with nothing said. The drawing cannot carry 50 - the outward trip writes
    /// the two WEIGHTS and nothing else (<see cref="DrawioDocument.Build"/>: everything under 99
    /// is drawn "alta"), so reading that word back as a number gave 0 for a junction nobody had
    /// touched.
    /// </para>
    /// <para>
    /// So the question asked is the <c>SameEnds</c> branch's, and deliberately the same one:
    /// between two WEIGHTS, not between two numbers. A drawn weight that AGREES with what the
    /// plant's number means is no gesture at all, so the number travels through untouched -
    /// 50 stays 50, 98 stays 98, 99 stays 99. Only a weight that DISAGREES is somebody changing
    /// something, and only then is a number chosen. An arrow that declares no priority at all -
    /// the one the operator pulled in this session, which never received the attribute - says
    /// nothing, and silence keeps what there was.
    /// </para>
    /// <para>
    /// A value nobody recognizes keeps it too, and is <b>said</b>, exactly as on the
    /// <c>SameEnds</c> branch: there is a number on the plant already, leaving it alone loses
    /// nothing, and the operator has to know his word was not understood.
    /// </para>
    /// <para>
    /// <b>TWO snapshots, and each question is asked of the right one</b> - third review, minor
    /// finding 2, where this function read <paramref name="before"/> for both and the
    /// <c>SameEnds</c> branch twenty lines up had already moved one of them to <c>current</c>.
    /// Whether the operator ASKED for something is asked of <paramref name="before"/>: the weight
    /// on the arrow is the one the drawing was built with when it was opened, and comparing it
    /// against a number somebody else changed since would read a gesture nobody made. What the
    /// junction KEEPS when nothing was asked is <paramref name="priorityNow"/>: writing
    /// <paramref name="before"/> back would silently undo the change that other person did make,
    /// which is the whole reason the number is carried across a re-hooking at all.
    /// </para>
    /// </remarks>
    /// <param name="priorityNow">
    /// The number the plant holds for this junction NOW - read from <c>current</c>, and falling
    /// back to <paramref name="before"/> when the junction has gone from it, which is the last
    /// number we actually know.
    /// </param>
    private static int PriorityOfRedone(DrawioCell edge, JunctionDto before, int priorityNow,
        List<DrawioNotice> notices)
    {
        switch (WeightOf(edge))
        {
            // Declared, and different from what the plant's number means: this is the gesture.
            case { } drawnWeight when drawnWeight != JunctionWeights.FromPriority(before.Priority):
                return drawnWeight.ToPriority();

            // Declared and in agreement, or not declared at all: nothing was asked for, so the
            // junction keeps the number it had - which is the only way an intermediate priority
            // can survive a round trip through a drawing that can only say "alta" or "bassa".
            case { }:
            case null when edge.Priority is null:
                return priorityNow;

            default:
                notices.Add(new DrawioNotice(edge.Id,
                    $"priority '{edge.Priority}' not recognized (expected values: '{DrawioDocument.HighPriorityValue}', '{DrawioDocument.LowPriorityValue}'): the junction is redone with the priority it already had ({priorityNow}), it was not changed"));
                return priorityNow;
        }
    }

    /// <summary>
    /// The weight the drawing declares for an arrow - or <c>null</c> when it declares none we can
    /// read, which covers BOTH the missing attribute and a value nobody recognizes.
    /// </summary>
    /// <remarks>
    /// One place reads the attribute; two places decide what to do about it, and they decide
    /// DIFFERENTLY on purpose. For a junction being born (<see cref="PriorityOf"/>) a number has to
    /// be chosen anyway, and the plant's default - low - is chosen and declared. For a junction that
    /// already exists - the <c>SameEnds</c> branch of <see cref="Compute"/>, and
    /// <see cref="PriorityOfRedone"/> for the one that is redone - there is already a number on the
    /// plant, and the same choice would be a demotion nobody asked for. Merging the
    /// two policies into one function would have to pick one of the two, and either pick is a defect
    /// for the other caller; splitting the READING is what keeps the two answers from drifting apart
    /// on what "alta" even means.
    /// </remarks>
    private static JunctionWeight? WeightOf(DrawioCell edge)
    {
        if (string.Equals(edge.Priority, DrawioDocument.HighPriorityValue, StringComparison.OrdinalIgnoreCase))
            return JunctionWeight.High;
        if (string.Equals(edge.Priority, DrawioDocument.LowPriorityValue, StringComparison.OrdinalIgnoreCase))
            return JunctionWeight.Low;
        return null;
    }

    /// <summary>
    /// Se gli agganci alle porte scritti nello stile della freccia non sono piu' quelli che
    /// <see cref="DrawioDocument"/> aveva scritto per questa giunzione (punto d del compito 7b).
    /// Un aggancio MANCANTE conta come spostato: <c>exitX=0</c> e' il bordo sinistro, <c>exitX</c>
    /// assente e' una freccia che non aggancia piu' niente - due fatti diversi che
    /// <see cref="DrawioReader.TryStyleNumber"/> distingue apposta.
    /// </summary>
    /// <param name="expected">
    /// Nullo quando <see cref="DrawioDocument.Anchors"/> non sa dire dove la freccia dovrebbe
    /// agganciarsi (un dispositivo o un tipo che il grafo non ha): allora non c'e' niente con cui
    /// confrontare, e non si dichiara uno spostamento che non si e' misurato.
    /// </param>
    private static bool AnchorsMoved(DrawioCell edge, (double ExitX, double ExitY, double EntryX, double EntryY)? expected)
    {
        if (expected is not { } anchors) return false;

        return Moved("exitX", anchors.ExitX) || Moved("exitY", anchors.ExitY)
            || Moved("entryX", anchors.EntryX) || Moved("entryY", anchors.EntryY);

        bool Moved(string key, double written) =>
            !DrawioReader.TryStyleNumber(edge.Style, key, out var drawnValue)
            || Math.Abs(drawnValue - written) > AnchorTolerance;
    }


    /// <summary>
    /// Se l'host ha cambiato questo dispositivo in QUALUNQUE modo mentre il disegno era aperto, non
    /// solo spostandolo. La domanda larga serve alla sola cancellazione, che e' irreversibile:
    /// sovrascrivere lo spostamento di un altro si vede subito e si rifa', ma un dispositivo
    /// cancellato mentre un altro gli stava rifacendo le mappature non torna indietro. Per le
    /// SCRITTURE resta giusto il confronto stretto (<see cref="Changed"/>): il delta scrive solo
    /// X/Y/W/H/rotazione, e rifiutare uno spostamento perche' qualcuno ha cambiato un nome farebbe
    /// perdere lavoro senza proteggere niente.
    /// <para>
    /// Il confronto e' sul record intero meno due cose. La geometria esce perche' l'ha gia'
    /// guardata <see cref="Changed"/> con la sua tolleranza, e confrontare due <c>double</c> per
    /// uguaglianza esatta direbbe "cambiato" per un'inezia. <c>Live</c> esce perche' non e' una
    /// modifica di nessuno: e' lo stato dal vivo del dispositivo in campo, che cambia da solo
    /// mentre l'impianto lavora - tenerlo dentro renderebbe irrifiutabile ogni cancellazione di un
    /// dispositivo acceso. Tutto il resto entra, comprese le colonne che il DTO non ha ancora: e'
    /// il verso giusto in cui sbagliare, perche' l'errore diventa un rifiuto di troppo e non una
    /// cancellazione di troppo.
    /// </para>
    /// </summary>
    private static bool ChangedForDeletion(PlantItemDto before, PlantItemDto now) =>
        Changed(before, now) || WithoutGeometryAndLiveState(before) != WithoutGeometryAndLiveState(now);

    private static PlantItemDto WithoutGeometryAndLiveState(PlantItemDto item) =>
        item with { X = null, Y = null, Width = null, Height = null, Rotation = null, Live = default! };

    /// <summary>Se l'host ha spostato, ridimensionato o ruotato questo dispositivo mentre il disegno era aperto.</summary>
    private static bool Changed(PlantItemDto before, PlantItemDto now) =>
        Math.Abs((before.X ?? 0) - (now.X ?? 0)) > PlantTolerance ||
        Math.Abs((before.Y ?? 0) - (now.Y ?? 0)) > PlantTolerance ||
        Math.Abs((before.Width ?? 0) - (now.Width ?? 0)) > PlantTolerance ||
        Math.Abs((before.Height ?? 0) - (now.Height ?? 0)) > PlantTolerance ||
        Math.Abs((before.Rotation ?? 0) - (now.Rotation ?? 0)) > PlantTolerance;
}
