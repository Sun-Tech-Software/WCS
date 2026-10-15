using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace Wcs.WebEditor.Drawio;

/// <summary>
/// Una cella del disegno, letta cosi' com'e': il lettore non giudica, divide soltanto.
/// <para>
/// <paramref name="Priority"/> e' l'attributo nostro <c>wcsPriority</c> dell'&lt;object&gt;
/// (decisione 3.14: la priorita' di una giunzione la porta un DATO, non il suo aspetto - lo
/// spessore e il colore restano, ma sono una conseguenza, non la fonte). Nullo quando la cella non
/// lo porta: e' il caso di una freccia appena tirata dall'operatore, che chi legge deve poter
/// distinguere da una che dichiara davvero "bassa" - si veda <c>DrawioDelta</c>, che nel primo caso
/// lo dichiara invece di scegliere in silenzio.
/// </para>
/// </summary>
/// <param name="Parent">
/// The cell this one hangs from in the mxGraph model, read raw from the <c>parent</c> attribute of
/// the <c>&lt;mxCell&gt;</c>, and null when the cell does not carry one.
/// <para>
/// It is here because <b>the geometry below means nothing without it</b>. In the mxGraph format the
/// geometry of a cell is expressed relative to its parent: on draw.io's default layer
/// (<see cref="DrawioDocument.DefaultLayerId"/>, which is what <see cref="DrawioDocument.Build"/>
/// writes) that is the canvas, so the numbers are the ones the plant speaks - but inside a group it
/// is the group's own box, and the same numbers mean somewhere else entirely. Measured on
/// 2026-09-16 in a real browser: two devices grouped with Ctrl+G and dragged came back with the
/// first child carrying <c>&lt;mxGeometry width="120" height="60"/&gt;</c>, no <c>x</c> and no
/// <c>y</c> at all. Read as absolute, that device lands on the canvas origin.
/// </para>
/// <para>
/// The reader does not judge it - it does not judge anything - it only stops throwing the fact
/// away. Who decides is <c>DrawioDelta</c>, which refuses a marked cell that is not directly on the
/// canvas instead of writing numbers from another frame of reference.
/// </para>
/// </param>
/// <param name="FlipH">
/// Whether the cell is mirrored left to right. draw.io mirrors a cell by writing
/// <c>flipH=1</c> into its style (the key is mxGraph's own, <c>mxConstants.STYLE_FLIPH</c> in the
/// bundled <c>mxgraph/src/util/mxConstants.js</c>), and <paramref name="FlipV"/> is the same fact
/// top to bottom.
/// <para>
/// It is here because until this reader carried it, <b>no point of the product read it at all</b>:
/// an operator could mirror a device, save, and have nothing happen and nothing said. The reader
/// does not judge it - it does not judge anything - it only stops throwing the fact away. Who
/// decides is <c>DrawioDelta</c>, which refuses the mirroring by name because the plant has
/// nowhere to store it (<c>PlantItemGeometryDto</c> holds position, size and rotation).
/// </para>
/// <para>
/// Read as a NUMBER of the style and not as a substring: <c>flipH=0</c> and no <c>flipH</c> at all
/// both mean "not mirrored", and a substring search would also match a key that merely ends in
/// <c>flipH</c>. The same distinction the port anchors need (see
/// <see cref="DrawioReader.TryStyleNumber"/>).
/// </para>
/// </param>
/// <param name="FlipV">The vertical half of <paramref name="FlipH"/>: draw.io's <c>flipV=1</c>.</param>
public readonly record struct DrawioCell(
    string Id, string? Mark, string? Priority, string Style, string? Parent,
    double X, double Y, double Width, double Height, double Rotation,
    bool FlipH, bool FlipV,
    string? Source, string? Target, string Xml);

/// <summary>
/// Where a line carries its arrowheads - which, between two devices, is the whole of what the
/// drawing says: the owner decided on 2026-09-19 that only lines <b>with a head</b> may be drawn
/// between two devices, that the head is there to show which way the connection goes, and that a
/// two-way connection is two lines with a head, one each way.
/// </summary>
/// <remarks>
/// <see cref="Both"/> is not "bidirectional" and is not treated as such anywhere: it is a drawing
/// the owner's rule has no meaning for, and it is said instead of being guessed at. How a style
/// gets read into these four is <see cref="DrawioReader.HeadsOf"/>.
/// </remarks>
public enum DrawioArrowHeads
{
    /// <summary>No head at either end: a plain line, which between two devices connects nothing.</summary>
    None,

    /// <summary>A head where the line ends, which is where draw.io puts it: source to target.</summary>
    AtEnd,

    /// <summary>A head where the line starts: the connection runs the other way, target to source.</summary>
    AtStart,

    /// <summary>A head at each end. Which way it goes is not said, so nothing is read from it.</summary>
    Both,
}

/// <summary>
/// Le categorie in cui il disegno che torna si divide. Sono QUATTRO, non tre: oltre ai
/// dispositivi, alle giunzioni e alla decorazione c'e' <see cref="Unknown"/> - le celle che
/// portano un marchio nostro che non riconosciamo.
/// <para>
/// Perche' esiste (rilievo di revisione del compito 3). Prima, una cella con un marchio
/// malformato - <c>wcs="devise"</c>, <c>wcs=""</c>, <c>wcs="Device"</c> con la maiuscola, o il
/// marchio di una categoria futura - cadeva nel ramo predefinito e finiva nella decorazione
/// PORTANDOSI DIETRO il proprio <c>wcs=</c>. Un dispositivo sarebbe sparito in silenzio dalle liste
/// gestite, e chi reinnesta la decorazione tale e quale se lo sarebbe ritrovato dentro come
/// disegno. Il caso e' reale: l'XML si modifica a mano fuori di qui e rientra con un Ctrl+V, o
/// trascinando un file sulla tela - non da "Extras &gt; Edit Diagram", che questo draw.io
/// incorporato non ha (si veda <c>DrawioCarryingTheDrawingAwayTest</c>).
/// Una cella marchiata che non riconosciamo non e' decorazione: e' un errore da dichiarare.
/// </para>
/// </summary>
/// <param name="Diagrams">
/// How many <c>&lt;diagram&gt;</c> the drawing that came back carries, that is <b>how many
/// pages</b> it has. The reader does not judge it - it does not judge anything - it only stops
/// choosing in silence.
/// <para>
/// It is here because <c>Descendants("root").FirstOrDefault()</c> below takes the FIRST root it
/// finds and says nothing about the others. With two pages that is page one, and since the
/// decoration is rewritten from what was read, everything the operator drew on page two is thrown
/// away at the next opening without a word - the exact shape of the loss this branch produces most
/// often. Measured in a real browser on 2026-09-17 with
/// <c>ui.actions.get('insertPage').funct()</c>: <c>ui.pages.length</c> becomes 2 and draw.io hands
/// back <c>&lt;mxfile host="127.0.0.1" pages="2"&gt;</c> with two <c>&lt;diagram&gt;</c>.
/// </para>
/// <para>
/// <b>The counting is on <c>&lt;diagram&gt;</c> and not on the <c>pages</c> attribute</b>: that
/// attribute is absent when there is a single page (measured), so a reader that trusted it would
/// call a two-page drawing "one page" the day draw.io stopped writing it. It is not a page count
/// either: <b>zero</b> means the document does not carry <c>&lt;diagram&gt;</c> at all - a naked
/// <c>&lt;mxGraphModel&gt;</c>, which is what <see cref="DrawioDocument.Build"/> itself writes -
/// and that is one canvas that does not say so, not an absence of pages. Who decides is the page
/// (<c>DrawioEditor.OnDrawioSaved</c>), and what it refuses is more than one.
/// </para>
/// </param>
/// <param name="Junctions">
/// The arrows the drawing proposes as junctions: the ones carrying <c>wcs="junction"</c>, written
/// by <see cref="DrawioDocument.Build"/>, <b>and the ones the operator pulled by hand between two
/// devices with a single head</b>, which carry no mark at all because draw.io builds their cell -
/// see <c>JoinsTwoDevices</c>. Proposed, not written: what becomes of them is
/// <c>DrawioDelta</c>'s decision.
/// <para>
/// <b>A marked arrow between two devices is here only if it still points one way</b>, exactly
/// like an unmarked one: the mark says the cell was ours, not that the drawing still calls it a
/// connection. One whose head has been taken off, or which has one at each end, is in
/// <paramref name="UndirectedLinesBetweenDevices"/> instead. Until the second review's two GRAVE
/// findings the mark walked past that question, and the same line got two different answers
/// depending on what the plant happened to hold.
/// </para>
/// <para>
/// <b><see cref="DrawioCell.Source"/> and <see cref="DrawioCell.Target"/> are the ends OF THE
/// JUNCTION here, not necessarily the cell's own <c>source</c>/<c>target</c> attributes.</b> On an
/// unmarked arrow the way it goes is the way its head points (the owner's rule of 2026-09-19: the
/// head is there to show the direction), so an arrow whose head is at the START
/// (<c>startArrow</c>) arrives here with its two ends swapped - from the tail to the head, which is
/// what the operator is looking at. A marked arrow arrives with them as they are written: the
/// direction of a junction of ours is the plant's, and <c>DrawioDelta</c> compares the two ends
/// against it to notice a re-hooking. <see cref="DrawioCell.Xml"/> is the original element either
/// way, because that is what goes back into the drawing when the junction is not written.
/// </para>
/// </param>
/// <param name="ArrowsWithAnEndInTheAir">
/// Unmarked arrows that hang from a device cell at <b>one</b> end and from <b>nothing at all</b> at
/// the other: the operator aimed at the second device and let go beside it.
/// <para>
/// <b>They are also in <paramref name="DecorationXml"/>, and that is not an oversight.</b> They are
/// decoration - an arrow that joins one device and nothing is not a junction and never becomes one
/// - and this list takes nothing out of the drawing: it is a FACT the reader stops throwing away,
/// like <paramref name="Diagrams"/>. Whoever reads it must not re-inject it, or the arrow ends up
/// on the canvas twice.
/// </para>
/// <para>
/// It exists because that gesture used to pass in silence, and silence is what this branch pays
/// for. An arrow let go in the MIDDLE of the target shape is refused by name (see
/// <c>DrawioDelta</c>); an arrow let go BESIDE it - at least as likely, and the same mistake - said
/// nothing at all. Who speaks is <c>DrawioDelta</c>, with a notice and not a refusal: nothing was
/// refused, because the drawing never asked for anything.
/// </para>
/// <para>
/// <b>The other end has to be on NOTHING</b>, not merely on something that is not a device. An
/// arrow from a device to a note, or to a box, is a drawing somebody made on purpose, and a line
/// about it at every save would be a nag, not a warning. An arrow with both ends in the air joins
/// no device either, and belongs to nobody's gesture towards a device: it is not here.
/// </para>
/// <para>
/// <b>It has to be an ARROW, and that is the open question answered.</b> This list used to be
/// blind to the style, so a line the operator drew ON PURPOSE from a device out into empty space -
/// a leader to a caption, drawn with <c>endArrow=none</c> - was counted here and got the notice at
/// every save. The owner answered on 2026-09-19, for that question and for
/// <c>JoinsTwoDevices</c> being too wide, that <b>the head is what means "connection"</b>. So a
/// line with no head is not a gesture towards a device at all: it is a drawing, it says nothing,
/// and nothing is said about it. What stays here is the arrow WITH a head that reached nothing -
/// a connection that did not come off, which is the thing worth a word.
/// </para>
/// </param>
/// <param name="UndirectedLinesBetweenDevices">
/// Lines whose two ends are two different device cells and which <b>do not point one way</b>: no
/// arrowhead at all, or an arrowhead at both ends. <b>Marked <c>wcs="junction"</c> or not</b> -
/// the question is about the drawing, so it is asked of both, once (see
/// <c>TheDrawingDoesNotSayConnection</c>).
/// <para>
/// <b>The UNMARKED ones are in <paramref name="DecorationXml"/></b>, like
/// <paramref name="ArrowsWithAnEndInTheAir"/> and for the same reason: for them this list takes
/// nothing out of the drawing, it is a FACT the reader stops throwing away, and whoever reads it
/// must not re-inject it.
/// </para>
/// <para>
/// <b>A MARKED one is not</b>, and the asymmetry is the division of labour this file lives by.
/// The cell was ours; whether it goes back to the operator's drawing depends on whether the
/// plant is about to redraw it - which this file cannot know. Putting it in the decoration here
/// would give the canvas two copies of the same line every time the plant keeps the junction
/// (the deletion refused, for one), and that is the one thing the decoration must never do.
/// <c>DrawioDelta</c> hands it back, after it has asked the plant. <c>Mark</c> is what tells the
/// two apart.
/// </para>
/// <para>
/// They exist because of the owner's rule of 2026-09-19: between two devices one may draw
/// <b>only</b> lines with a head, the head says which way the connection goes, and a two-way
/// connection is two arrows. So a line without a head between two devices is not decoration
/// somebody is entitled to - it is a gesture that means nothing here - and one with two heads asks
/// for something that is drawn differently. Neither becomes a junction, both stay in the drawing,
/// and <c>DrawioDelta</c> says so by name: the alternative was the rule that took them all, which
/// turned a decorative line into a connection of the plant without a word.
/// </para>
/// </param>
/// <param name="CellsLeftInTheDrawing">
/// The other half of the same subtraction: every cell this reader DID append to
/// <paramref name="DecorationXml"/>, in the drawing's own order, so that whoever knows the plant
/// can take one back out.
/// <para>
/// It exists because one of them can turn out not to be the operator's at all: a cell of ours
/// whose mark somebody took off keeps our identifier, and the plant knows which junction it drew
/// on it. <c>DrawioDelta</c> takes that one out and lets the plant redraw it marked, instead of
/// leaving the canvas with two elements under one identifier. The reader cannot make that call -
/// it does not know the plant - so it hands over the list and says nothing about it.
/// </para>
/// </param>
/// <param name="CellsTakenOutOfTheDrawing">
/// Every CELL this reader did <b>not</b> leave in <paramref name="DecorationXml"/> - whatever
/// category it put it in, and whatever mark it carries. It is the subject of the invariant
/// <c>DrawioDelta</c> holds, and it exists because that invariant needs to be asked of cells
/// nobody has thought of yet.
/// <para>
/// <b>Computed by SUBTRACTION, and the subtraction has ONE term.</b> It is every cell read, minus
/// the ones appended to the decoration. So a category added to <see cref="Read"/> tomorrow - a new
/// mark, a new arm of the switch - is in here the moment it stops appending the element, with
/// nobody having to remember to add it. A list written out by hand would be the third copy of the
/// same rule, and the two copies that already existed (<c>DrawioDelta</c>'s two rounds) are exactly
/// what let a marked line with a detached end fall between them and vanish from the operator's
/// canvas.
/// </para>
/// <para>
/// <b>IT USED TO ASK "is it a line" AS WELL, and that second term was a defect.</b> A cell the
/// drawing marks <c>wcs="junction"</c> and that is not an edge at all - a vertex somebody pasted
/// from hand-edited XML - was taken out of the decoration by the arm above, refused by the save
/// with <i>"it stays a drawing"</i>, and then left out of this list because it was not a line: so
/// nobody handed it back, and it was gone from the canvas at the next opening. Measured on 32
/// roads of the generative net, and it is the GRAVE finding of the second review.
/// </para>
/// <para>
/// One term is also the only form this subtraction can have without narrowing again: the question
/// "did I leave it in the operator's drawing?" is the one the decoration itself answers, and it
/// cannot be restricted by a category nobody has thought of. <c>DrawioCell.IsALine</c> existed for
/// the second term and went with it.
/// </para>
/// <para>
/// <b>Being in here does NOT mean the cell goes back to the drawing.</b> The reader cannot know
/// that - it does not know the plant. What it says is "I took this out, so somebody has to answer
/// for it", and the answer is <c>DrawioDelta</c>'s: a line the plant holds is redrawn from the
/// plant and must NOT come back, or the canvas would carry it twice.
/// </para>
/// </param>
public sealed record DrawioContents(
    IReadOnlyList<DrawioCell> Devices,
    IReadOnlyList<DrawioCell> Junctions,
    IReadOnlyList<DrawioCell> Unknown,
    string DecorationXml,
    int Diagrams,
    IReadOnlyList<DrawioCell> ArrowsWithAnEndInTheAir,
    IReadOnlyList<DrawioCell> UndirectedLinesBetweenDevices,
    IReadOnlyList<DrawioCell> CellsTakenOutOfTheDrawing,
    IReadOnlyList<DrawioCell> CellsLeftInTheDrawing);

/// <summary>
/// Il ritorno del ponte: legge l'XML che draw.io restituisce e lo divide in dispositivi nostri,
/// giunzioni nostre e decorazione dell'operatore. Non scrive niente: il delta e la guardia
/// anti-deriva stanno nel compito dopo (<see cref="DrawioDelta"/>).
/// <para>
/// <b>What it decides is which of the four categories every cell falls into</b>, and for almost
/// all of them the answer is the mark and nothing else. The one the mark cannot divide is the
/// arrow: draw.io builds the cell of an arrow pulled by hand and puts no mark on it, so that
/// decision lives in <see cref="Read"/>'s call to <c>JoinsTwoDevices</c>, which has written above
/// it why it could not live anywhere else.
/// </para>
/// <para>
/// <b>And that decision is not only about what a junction is - it is also about what leaves the
/// operator's drawing</b>, which is the second thing this class used to claim it did not do. Until
/// the arrow pulled by hand, only cells carrying a mark of ours could leave the decoration, so the
/// division here could lose nothing: what the operator owns was never touched. An unmarked arrow
/// promoted to a junction leaves it, and the reader cannot know whether the plant will take it -
/// it does not know the plant. So the division made here is <b>provisional for that one cell</b>:
/// <c>DrawioDelta</c> hands the arrow back to the decoration when it does not write it, and that
/// is the half of the rule this file cannot hold. Whoever changes <c>JoinsTwoDevices</c> changes
/// both halves.
/// </para>
/// </summary>
public static class DrawioReader
{
    public static DrawioContents Read(string xml)
    {
        var document = XDocument.Parse(xml);
        // Counted BEFORE the first root is picked, and kept whatever else happens: a drawing with
        // no root at all still has to be able to say how many pages it had.
        var diagrams = document.Descendants("diagram").Count();
        var root = document.Descendants("root").FirstOrDefault();
        if (root is null) return new DrawioContents([], [], [], string.Empty, diagrams, [], [], [], []);

        var devices = new List<DrawioCell>();
        var junctions = new List<DrawioCell>();
        var unknown = new List<DrawioCell>();
        var arrowsInTheAir = new List<DrawioCell>();
        var undirectedLines = new List<DrawioCell>();
        var decoration = new StringBuilder();
        // The cells that went into it, kept alongside the text: see DrawioContents.CellsLeftInTheDrawing.
        var leftCells = new List<DrawioCell>();
        // The identifiers appended to the decoration, kept so that what was NOT appended can be
        // worked out by subtraction: see DrawioContents.CellsTakenOutOfTheDrawing.
        var leftInTheDecoration = new HashSet<string>(StringComparer.Ordinal);

        // TWO PASSES, and the drawing's own order is why. An arrow that joins two devices is read
        // as a junction whatever mark it carries (see JoinsTwoDevices), and to answer that about an
        // arrow one has to know what the two cells at its ends are - which the drawing is under no
        // obligation to have said yet. draw.io writes its vertices before its edges today; a reader
        // that leaned on that would turn an arrow into decoration the day it stopped, and the loss
        // would be silent.
        var read = new List<(XElement Element, DrawioCell Cell, string? Mark)>();
        foreach (var element in root.Elements())
        {
            var mark = element.Name == "object" ? (string?)element.Attribute("wcs") : null;
            var cell = element.Name == "object" ? element.Element("mxCell") : element;
            if (cell is null) continue;

            var id = (string?)element.Attribute("id") ?? (string?)cell.Attribute("id") ?? string.Empty;
            if (id is "0" or "1") continue;

            read.Add((element, ToCell(id, mark, cell, element), mark));
        }

        var deviceCells = read
            .Where(entry => entry.Mark == "device")
            .Select(entry => entry.Cell.Id)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var (element, cell, mark) in read)
        {
            switch (mark)
            {
                case "device": devices.Add(cell); break;

                // A CELL OF OURS THE DRAWING NO LONGER CALLS A CONNECTION, and this arm is the
                // owner's rule of 2026-09-19 asked of a MARKED cell - in the one place where it
                // is also asked of an unmarked one.
                //
                // It was not asked here before, and that is where the two GRAVE findings of the
                // second review came from. The rule lived in ONE branch of DrawioDelta - the
                // branch that recognizes a junction the plant still holds - so a marked cell the
                // plant does NOT hold fell straight through and was born as a junction, head or
                // no head. The operator took the head off, the junction was deleted and the
                // report told him so; the very next save, with nothing touched, put a junction
                // back between the two devices under a line that had no head at all. Two
                // findings, one rule written in one place too few - and no mutation could reach
                // it, because the branch that lacked the rule had no line to mutate.
                //
                // WHY HERE and not there. "Does this drawing say connection?" is a question about
                // the drawing and about nothing else, and dividing the drawing up is this file's
                // whole job. What is DONE about the answer still needs the plant - the junction
                // dies, or nothing was there to die - and that stays DrawioDelta's, which reads
                // this list and asks the plant about each line.
                case "junction" when TheDrawingDoesNotSayConnection(cell, deviceCells):
                    undirectedLines.Add(cell);
                    break;
                case "junction": junctions.Add(cell); break;
                // Marchio nostro che non riconosciamo: NON e' decorazione. Finirebbe nel disegno
                // libero portandosi dietro il proprio wcs=, e un dispositivo sparirebbe in silenzio.
                case not null: unknown.Add(cell); break;
                // No mark, but it joins two devices with a single head: this is the arrow the
                // operator pulled with the mouse, and it carries no mark because draw.io builds
                // its cell. The ends come from the HEAD, not from the cell's attributes. See
                // JoinsTwoDevices for what keeps the operator's free drawing safe.
                case null when JoinsTwoDevices(cell, deviceCells) is { } ends:
                    junctions.Add(cell with { Source = ends.Source, Target = ends.Target });
                    break;
                default:
                    decoration.Append(element);
                    leftInTheDecoration.Add(cell.Id);
                    leftCells.Add(cell);
                    // Decoration all the same - the cell is appended above and nothing is taken out
                    // of the operator's drawing - but the fact is kept instead of thrown away. Only
                    // the unmarked ones reach this arm, so the mark is not asked for again.
                    //
                    // Two facts, and they cannot both be true of one cell: an arrow aimed at a
                    // device and let go beside it (ArrowsWithAnEndInTheAir) has one end on nothing,
                    // a line that does not point one way (UndirectedLinesBetweenDevices) has both
                    // ends on devices.
                    //
                    // The second goes through TheDrawingDoesNotSayConnection, the SAME call the
                    // marked arm makes: one spelling of the rule for the two kinds of cell. On a
                    // cell reaching this arm it can only answer yes - the case above has already
                    // turned it down, and the only thing that case turns down is a line with no
                    // head or with one at each end - so the call costs nothing and buys the one
                    // thing that matters here: the marked road and the unmarked road cannot drift
                    // apart, because there is only one road.
                    if (OneEndOnADeviceAndTheOtherInTheAir(cell, deviceCells)) arrowsInTheAir.Add(cell);
                    if (TheDrawingDoesNotSayConnection(cell, deviceCells)) undirectedLines.Add(cell);
                    break;
            }
        }

        // What this reader took out of the operator's drawing, worked out by subtraction and not
        // by listing the arms above: see DrawioContents.CellsTakenOutOfTheDrawing for why that is
        // the whole point of it.
        var takenOut = read
            .Select(entry => entry.Cell)
            .Where(cell => !leftInTheDecoration.Contains(cell.Id))
            .ToList();

        return new DrawioContents(
            devices, junctions, unknown, decoration.ToString(), diagrams, arrowsInTheAir, undirectedLines,
            takenOut, leftCells);
    }

    /// <summary>
    /// Whether this unmarked cell is an arrow hooked to a device at one end and to <b>nothing</b>
    /// at the other - the gesture of somebody who aimed at the second device and let go beside it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A cell that is not an edge has no end at all and cannot get in: both <c>source</c> and
    /// <c>target</c> are null on a vertex, and each branch here needs one of them to be there.
    /// </para>
    /// <para>
    /// <b>The free end must be absent, not merely "not a device".</b> An arrow from a device to a
    /// note or to a box has both ends on cells: it is a drawing somebody made on purpose, and
    /// saying something about it at every save would teach the operator to stop reading the report.
    /// An arrow with BOTH ends in the air is not here either - nobody was aiming at a device.
    /// </para>
    /// <para>
    /// <b>And it must be an arrow</b>, that is, it must carry a head somewhere: a plain line from a
    /// device out into empty space is a leader to a caption, not a connection that missed. Which
    /// head, and at which end, does not matter here - both a head at the tip and a head at the
    /// start are somebody drawing a connection - so the question is only whether there is one at
    /// all.
    /// </para>
    /// </remarks>
    private static bool OneEndOnADeviceAndTheOtherInTheAir(DrawioCell cell, HashSet<string> deviceCells) =>
        HeadsOf(cell.Style) != DrawioArrowHeads.None
        && ((cell.Target is null && cell.Source is { } source && deviceCells.Contains(source))
            || (cell.Source is null && cell.Target is { } target && deviceCells.Contains(target)));

    /// <summary>
    /// The two ends of the junction an unmarked cell proposes - <b>an arrow between two different
    /// device cells, carrying one head</b> - or <c>null</c> when it proposes none. The head is
    /// what says "connection", and which way it points is the direction, so the pair comes back
    /// <b>from the tail to the head</b> and not in the order the cell's attributes are written in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the mark cannot be asked for here.</b> draw.io builds the cell of an arrow the
    /// operator pulls, and it builds it with no value at all - measured in a real browser on
    /// 2026-09-19, the cell came back as a bare <c>&lt;mxCell edge="1"&gt;</c> hooked to both
    /// devices, <c>exitX=1;exitY=0.5</c> and <c>entryX=0;entryY=0.5</c> in its style, and no
    /// <c>&lt;object&gt;</c> around it. <c>wcs="junction"</c> is written by
    /// <see cref="DrawioDocument.Build"/> and by nobody else, so a rule that asks for it says
    /// "decoration" to every arrow a beta tester will ever draw - and says it in silence, because
    /// decoration is written back without a word. That was measured too: the save reported
    /// "1 writes, the drawing's decoration" and the plant stayed at zero junctions.
    /// </para>
    /// <para>
    /// <b>What keeps the operator's own drawing safe.</b> The two ends must be cells this same
    /// drawing marks <c>wcs="device"</c> - not cells of any kind. An arrow to a note, to a box, to
    /// a shape drawn by hand, or between two free shapes, has at least one end that is not a device
    /// and stays decoration exactly as before. The ends are read as the cell identifiers they are,
    /// which covers a device dropped in this session as well as a seeded one: the drop leaves a
    /// cell marked <c>wcs="device"</c> carrying draw.io's own identifier, and it is that identifier
    /// the arrow hangs from.
    /// </para>
    /// <para>
    /// <b>Two DIFFERENT devices</b>, so an arrow a device makes back onto itself is not promoted:
    /// it joins one device, not two, and the plant has no such connection to hold. It stays
    /// decoration, which is what it is today.
    /// </para>
    /// <para>
    /// <b>AND ONE HEAD, which is the owner's decision of 2026-09-19</b>: between two devices one
    /// may draw only lines with a head, the head is there to show which way the connection goes,
    /// and a two-way connection is two lines with a head, one each way. So the head does two jobs
    /// here and this function does both with it - it is what separates a connection from a
    /// drawing, and it is what the direction is read from. Before that answer the rule asked only
    /// for the two ends, and a decorative line between two devices became a connection of the
    /// plant with no refusal and no way to keep it as a drawing (measured by the reviewer of the
    /// previous batch). What it leaves out now is said, not swallowed: see
    /// <see cref="DrawioContents.UndirectedLinesBetweenDevices"/>.
    /// </para>
    /// <para>
    /// <b>The head at the START reverses the pair, it does not disqualify it.</b> draw.io lets the
    /// operator move the head to the other end - with Edit Style (Ctrl+E), which is the road this
    /// embedded editor is measured to offer and the one the browser test takes - and until this
    /// rule nobody read it: the junction was born pointing the way the drag had gone,
    /// which for a reversed arrow is the opposite of what is on the screen, and the engine's routes
    /// follow the junction, not the picture. The cell's own <c>source</c> and <c>target</c> are
    /// where draw.io hung the two ends; from here on it is the head that says which of them the
    /// junction starts at.
    /// </para>
    /// <para>
    /// <b>What this does NOT decide</b>, and the division is the same as everywhere else in this
    /// file: whether the junction can be written. Whether the arrow hooks two ports, whether the
    /// two devices are known to the plant, whether it doubles one that already exists, and what
    /// priority it is born at are all <c>DrawioDelta</c>'s, which was written for an arrow pulled
    /// by hand and until now was never handed one.
    /// </para>
    /// </remarks>
    private static (string Source, string Target)? JoinsTwoDevices(DrawioCell cell, HashSet<string> deviceCells)
    {
        if (!BetweenTwoDevices(cell, deviceCells)) return null;
        return HeadsOf(cell.Style) switch
        {
            DrawioArrowHeads.AtEnd => (cell.Source!, cell.Target!),
            DrawioArrowHeads.AtStart => (cell.Target!, cell.Source!),
            // No head at all, or one at each end: the drawing does not say which way, and there is
            // no direction to invent. Said by DrawioDelta, kept in the decoration by the caller.
            _ => null,
        };
    }

    /// <summary>
    /// Whether the two ends of this cell are two <b>different</b> cells this same drawing marks
    /// <c>wcs="device"</c>. It is the half of <see cref="JoinsTwoDevices"/> that has nothing to do
    /// with the head, and it is asked on its own for the lines that have no head to read.
    /// </summary>
    private static bool BetweenTwoDevices(DrawioCell cell, HashSet<string> deviceCells) =>
        cell.Source is { } source && cell.Target is { } target
        && !string.Equals(source, target, StringComparison.Ordinal)
        && deviceCells.Contains(source) && deviceCells.Contains(target);

    /// <summary>
    /// Whether the drawing <b>refuses to call this line a connection</b>: its two ends are two
    /// different devices, and it does not point one way - no head at all, or a head at each end.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ONE spelling of the owner's rule of 2026-09-19, for a marked cell and an unmarked one
    /// alike.</b> It is <see cref="JoinsTwoDevices"/> asked backwards, and it is asked THROUGH
    /// that function instead of reading the head again: whatever counts as "points one way"
    /// there counts as "does not" here, and no future change to one can leave the other behind.
    /// That is the whole point of it existing at all - the two GRAVE findings of the second
    /// review were one rule living in one of the two roads that needed it.
    /// </para>
    /// <para>
    /// <b>Only between two DEVICES</b>, because that is the whole of the rule: outside that the
    /// head means nothing here. A marked arrow hanging from a note, or from a device whose shape
    /// has gone, is not turned down for its head - it goes on as a junction the drawing proposes,
    /// and <c>DrawioDelta</c> refuses it by name for its ENDS, which is true whatever head it
    /// carries.
    /// </para>
    /// </remarks>
    private static bool TheDrawingDoesNotSayConnection(DrawioCell cell, HashSet<string> deviceCells) =>
        BetweenTwoDevices(cell, deviceCells) && JoinsTwoDevices(cell, deviceCells) is null;

    /// <summary>
    /// Where a cell's style puts its arrowheads. Public because <c>DrawioDelta</c> has to name the
    /// two cases it speaks about - no head, and a head at each end - and because a junction of
    /// ours whose head somebody moved is the same question asked of a marked cell.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An absent <c>endArrow</c> means there IS a head, and that is measured, not assumed.</b>
    /// draw.io's default edge style carries <c>endArrow=classic</c>
    /// (<c>wwwroot/drawio/styles/default.xml</c>, entry <c>defaultEdge</c>; mxGraph's own fallback
    /// is the same, <c>mxStylesheet.createDefaultEdgeStyle</c>), and the cell draw.io builds for an
    /// arrow pulled with the mouse carries no <c>endArrow</c> of its own at all - so a rule that
    /// asked for the key to be there would refuse every arrow an operator will ever draw. An absent
    /// <c>startArrow</c>, on the other hand, means no head: nothing in that stylesheet puts one
    /// there.
    /// </para>
    /// <para>
    /// <b>What counts as "no head" is <c>none</c> and the empty value</b>, which is how mxGraph
    /// reads it too: <c>mxConnector.createMarker</c> asks for the marker named by the style and
    /// draws nothing when there is no marker by that name. A name we do not know - somebody's typo
    /// in Edit Style - is read here as a head while draw.io would draw none.
    /// </para>
    /// <para>
    /// <b>That is one of TWO places where this reading and the picture can disagree</b>, and both
    /// take a style typed by hand to reach. The other is a style that BEGINS with a semicolon:
    /// <c>mxStylesheet.getCellStyle</c> (<c>mxgraph/src/view/mxStylesheet.js:212-222</c>) throws
    /// the whole default away when it does - <c>style = new Object()</c> - so
    /// <c>;edgeStyle=orthogonalEdgeStyle;html=1;…</c> is drawn with no head at all, while here the
    /// key is merely absent and absent means "there is one". Measured on the real code by the
    /// reviewer of this batch: <c>HeadsOf(";edgeStyle=orthogonalEdgeStyle;html=1;")</c> answers
    /// <see cref="DrawioArrowHeads.AtEnd"/> and the junction is born, with a bare line on screen.
    /// The two are named together because a comment that claimed there was only one was the thing
    /// that stopped the second from being looked for.
    /// </para>
    /// <para>
    /// <b>The LAST occurrence of the key wins</b>, because that is what mxGraph does: it parses the
    /// style into a dictionary, so a second <c>endArrow=</c> overwrites the first.
    /// </para>
    /// </remarks>
    public static DrawioArrowHeads HeadsOf(string style) =>
        (Head(style, "startArrow", whenAbsent: false), Head(style, "endArrow", whenAbsent: true)) switch
        {
            (false, true) => DrawioArrowHeads.AtEnd,
            (true, false) => DrawioArrowHeads.AtStart,
            (true, true) => DrawioArrowHeads.Both,
            _ => DrawioArrowHeads.None,
        };

    private static bool Head(string style, string key, bool whenAbsent) =>
        StyleValue(style, key) is { } value
            ? !string.IsNullOrWhiteSpace(value) && !string.Equals(value.Trim(), "none", StringComparison.Ordinal)
            : whenAbsent;

    /// <summary>
    /// The value a style gives a key, as text, or <c>null</c> when the key is not there at all -
    /// the distinction <see cref="TryStyleNumber"/> makes for numbers, for the keys whose value is
    /// a name.
    /// </summary>
    private static string? StyleValue(string style, string key)
    {
        string? found = null;
        foreach (var part in style.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0 || part[..eq] != key) continue;
            found = part[(eq + 1)..];
        }

        return found;
    }

    private static DrawioCell ToCell(string id, string? mark, XElement cell, XElement owner)
    {
        var style = (string?)cell.Attribute("style") ?? string.Empty;
        var geometry = cell.Element("mxGeometry");
        // La priorita' sta sull'<object> (owner), non sull'<mxCell>: e' un attributo nostro, e
        // draw.io conserva gli attributi dell'<object> anche quando l'operatore sposta o riaggancia
        // la freccia. Su una cella che non e' un <object> (nessun marchio) non c'e' mai.
        var priority = owner.Name == "object" ? (string?)owner.Attribute(DrawioDocument.PriorityAttribute) : null;
        return new DrawioCell(
            id, mark, priority, style, (string?)cell.Attribute("parent"),
            Number(geometry, "x"), Number(geometry, "y"),
            Number(geometry, "width"), Number(geometry, "height"),
            StyleNumber(style, "rotation"),
            StyleFlag(style, "flipH"), StyleFlag(style, "flipV"),
            (string?)cell.Attribute("source"), (string?)cell.Attribute("target"),
            owner.ToString())
        {
        };
    }

    private static double Number(XElement? geometry, string name) =>
        geometry is not null && double.TryParse((string?)geometry.Attribute(name),
            NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static double StyleNumber(string style, string key) =>
        TryStyleNumber(style, key, out var value) ? value : 0;

    /// <summary>
    /// Un numero dello stile, distinguendo "assente" da "zero" - cosa che
    /// <see cref="StyleNumber"/> non fa, e che per la rotazione non serve (nessuna rotazione e'
    /// uguale a zero gradi) ma per un aggancio di porta si': <c>exitX=0</c> e' l'aggancio al bordo
    /// sinistro, <c>exitX</c> mancante e' una freccia staccata dalla porta. Pubblico perche' il
    /// delta deve poter fare quella distinzione (si veda il controllo degli agganci in
    /// <c>DrawioDelta</c>) senza riscriversi la stessa analisi dello stile.
    /// </summary>
    public static bool TryStyleNumber(string style, string key, out double value)
    {
        foreach (var part in style.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0 || part[..eq] != key) continue;
            if (double.TryParse(part[(eq + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return true;
        }

        value = 0;
        return false;
    }

    /// <summary>
    /// A switch of the style: present and not zero. Absent and <c>=0</c> are read the same way -
    /// "off" - which is what a flag means here and is not a guess about how draw.io writes the
    /// style: whichever of the two it produces when the mirroring is taken away again, the answer
    /// is the same.
    /// </summary>
    private static bool StyleFlag(string style, string key) =>
        TryStyleNumber(style, key, out var value) && value != 0;
}
