using System.Text.RegularExpressions;
using System.Xml.Linq;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Drawing;
using Wcs.WebEditor.Drawio;
using Wcs.WebEditor.Model;
using Xunit;
using Xunit.Abstractions;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

/// <summary>
/// <b>The invariant of the operator's drawing, walked down every road instead of sampled on a
/// case.</b> Every line the reader takes out of the decoration, and that the plant does not hold
/// when the save is done, is back in the drawing at the next opening - exactly once; and every
/// line the plant DOES hold is not in the decoration at all.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a net of a different kind.</b> The same defect has come back three times - an arrow let
/// go in the middle of a shape, a marked line whose head was taken off, a marked line with an end
/// detached - and each time a test was added for the case that had just been measured. Every one
/// of those tests was green while the next category of cell was being lost, because a test written
/// for a case cannot fail on a case nobody has thought of. This one is not written for a case.
/// </para>
/// <para>
/// <b>How it is built, in four parts.</b>
/// </para>
/// <para>
/// 1. <b>The roads are generated, not listed.</b> The subject cell is the cross product of the
/// facts the reader and the delta actually key on - the mark, where the two ends land, where the
/// heads are, whether the ports are hooked, what the plant held when the page was opened, whether
/// another hand changed the plant meanwhile, and whether the session still carries the maps of
/// what was born before a reload - which is <see cref="Roads"/> drawings, each one read, saved,
/// and reopened.
/// </para>
/// <para>
/// 2. <b>What is expected is COMPUTED, and the subjects come from the DOCUMENT WE HAND IN.</b>
/// The list of cells to answer for is read straight off the input XML by
/// <see cref="LinesTheDocumentCarries"/> - every element of <c>&lt;root&gt;</c> whose
/// <c>&lt;mxCell&gt;</c> is an edge (<c>edge="1"</c>, or a <c>source</c>, or a <c>target</c>) or
/// that the drawing marks <c>wcs="junction"</c>. That reading does not go through
/// <c>DrawioReader</c>, it does not call <c>DrawioCell.IsALine</c>, and it is deliberately WIDER
/// than both.
/// </para>
/// <para>
/// It used to be <c>DrawioContents.CellsTakenOutOfTheDrawing</c>, which is <b>the answer of the
/// code under test</b>, and that is the shape of a test that recomputes what it is checking: a
/// defect that NARROWS that answer does not make the test fail, it makes the test stop asking.
/// Measured: restricting <c>IsALine</c> left the whole suite green while a marked line with both
/// its ends detached was refused with <i>"it stays a drawing"</i> and vanished from the drawing.
/// With the subjects read from the document, that same cell is interrogated anyway - it is in the
/// document - and, found neither in the plant nor in the decoration, the assertion falls.
/// </para>
/// <para>
/// The question itself is unchanged: when the save is done, either the plant holds the line, or it
/// is in the drawing - never both, never neither - and the next opening carries it exactly once.
/// Whether the plant took the line is still read off the delta's own result, because that is the
/// outcome under judgement and not the list of who gets judged.
/// </para>
/// <para>
/// 3. <b>The symmetric question, which protects the PLANT</b>
/// (<see cref="NoJunctionOfThePlantIsDeletedUnlessTheDrawingAsksAndTheSaveSaysSo"/>). Part 2 asks
/// whether the operator's drawing lost a cell; it is satisfied by a save that leaves every cell
/// exactly where it was and takes the junction underneath one of them off the plant. So the same
/// two rules are applied from the other end: the subjects are the junctions of the PLANT we hand
/// in, and what the drawing asks for is computed from the same document, read the same
/// independent way.
/// </para>
/// <para>
/// 4. <b>An inventory of the sentences</b> (<see cref="EveryOutcomeAnEdgeCanMeetIsDeclared"/>).
/// Part 2 could stay quiet about a new road that happens to satisfy the invariant, and quiet is
/// what let a defect through twice. So every refusal and every notice the subject cell collects,
/// with the identifiers rubbed out, has to be one of the sentences declared here - and every
/// sentence declared here has to be met. A new category of cell changes that set in one direction
/// or the other, and this test goes red until somebody comes here and says what it is.
/// </para>
/// <para>
/// <b>What it does NOT cover, said instead of implied.</b> The cross product varies the facts
/// listed above and nothing else: a cell inside a group, a second page, a device cell (this
/// invariant is written about LINES), and a mark spelled some other way than the one mangled mark
/// here are outside it. A road that needs one of those facts is reached by none of the four.
/// </para>
/// <para>
/// And one limit of part 2's QUESTION rather than of the table, because it is the difference
/// between a hole named and a hole hidden: its subjects are the cells the DOCUMENT carries, and a
/// save can leave every one of them where it is and still take a junction off the plant. Part 2
/// is satisfied and the plant is one connection short. That is not a hole part 2 can be widened
/// to cover - it is a different sentence - and it is what <b>part 3</b> was added for.
/// </para>
/// </remarks>
public sealed class DrawioDrawingInvariantTest(ITestOutputHelper output)
{
    private static readonly Guid A = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid B = Guid.Parse("22222222-2222-2222-2222-222222222222");

    /// <summary>The subject cell's identifier, a guid so that the plant can hold a junction under it.</summary>
    private static readonly Guid S = Guid.Parse("55555555-5555-5555-5555-555555555555");

    /// <summary>A junction of the plant's between the same two devices that is NOT the subject cell.</summary>
    private static readonly Guid Other = Guid.Parse("66666666-6666-6666-6666-666666666666");

    /// <summary>
    /// The junction the host gave the subject arrow when it was created IN THIS SESSION. The
    /// canvas has not been rebuilt since, so the cell still carries draw.io's identifier
    /// (<see cref="S"/>) and only the session map ties the two together.
    /// </summary>
    private static readonly Guid SessionJunction = Guid.Parse("88888888-8888-8888-8888-888888888888");

    /// <summary>A third device, which on <see cref="Session.BornInThisSession"/> is the one dropped in this session.</summary>
    private static readonly Guid Dropped = Guid.Parse("99999999-9999-9999-9999-999999999999");

    /// <summary>The identifier draw.io gave the dropped device's cell, before anybody knew it would be a device.</summary>
    private const string DroppedCellId = "appena-lasciato-cadere";

    private static readonly Dictionary<string, Guid> NoJunctionMap = [];
    private static readonly Dictionary<string, Guid> NoDeviceMap = [];

    private const string FirstNote = "nota-1";
    private const string SecondNote = "nota-2";

    /// <summary>What the drawing says the subject cell is.</summary>
    private enum Mark { None, Junction, Mangled }

    /// <summary>What the subject cell is, and where its two ends land.</summary>
    /// <remarks>
    /// <para>
    /// <see cref="TwoDevicesWithoutTheEdgeAttribute"/> and <see cref="NotALineAtAll"/> are the two
    /// sides of <c>DrawioCell.IsALine</c>, which the reader's own comment calls a deliberate
    /// widening ("a hand-written cell carrying source and target and no <c>edge="1"</c> is already
    /// treated as a line everywhere else here"). A claim about behaviour the table never walks is
    /// a claim nothing defends: both restrictions of that line were green on the whole suite.
    /// </para>
    /// <para>
    /// <see cref="TheCellIsNotInTheDrawing"/> is the plainest gesture of all - the operator
    /// selected the line and pressed Delete - and it is the one way a junction of the plant's is
    /// SUPPOSED to die. A table that never walks it can only ever measure deletions that should
    /// not have happened, never the ones that should.
    /// </para>
    /// </remarks>
    private enum Ends
    {
        TwoDevices, TwoDevicesTheOtherWayRound, TheSameDeviceTwice, ADeviceAndANote, OneEndDetached,
        BothEndsDetached, TwoNotes,

        /// <summary>Two devices, <c>source</c> and <c>target</c>, and no <c>edge="1"</c>: the hand-written line.</summary>
        TwoDevicesWithoutTheEdgeAttribute,

        /// <summary>A shape: no ends and no <c>edge="1"</c>. Marked <c>wcs="junction"</c> it is a cell that says it is ours and is not a line.</summary>
        NotALineAtAll,

        /// <summary>No subject cell at all: the operator deleted the line. The rest of the drawing is as it was.</summary>
        TheCellIsNotInTheDrawing,
    }

    /// <summary>Where its heads are, which between two devices is the whole of what the drawing says.</summary>
    private enum Heads { None, AtEnd, AtStart, Both }

    /// <summary>Whether its style hooks the two ports a junction of the plant's is written with.</summary>
    private enum Ports { Hooked, NotHooked }

    /// <summary>What the plant holds when the drawing is opened.</summary>
    private enum Plant { Nothing, AJunctionUnderThisIdentifier, AJunctionUnderThisIdentifierAndItsMirror, AnotherJunctionBetweenTheSameTwo }

    /// <summary>
    /// What the plant looks like NOW, as against the snapshot the page was opened with. The delta
    /// takes both, and the invariant it writes of itself is about what the plant does not hold
    /// <i>when this save is done</i> - so a table that never varies the second one leaves the half
    /// of that sentence untested.
    /// </summary>
    private enum Meanwhile
    {
        /// <summary>Nobody touched it: <c>current</c> is <c>opened</c>, which is every road this table used to walk.</summary>
        NobodyTouchedIt,

        /// <summary>Another hand deleted the junctions while this drawing was open.</summary>
        AnotherHandDeletedTheJunctions,
    }

    /// <summary>
    /// Whether the session still carries what was born in it before the page was rebuilt. It is
    /// the difference between "before a reload" and "after one", and it is the condition the last
    /// rounds of defects lived in.
    /// </summary>
    private enum Session
    {
        /// <summary>After a reload: both maps empty, every cell carries the plant's own identifier.</summary>
        AfterAReload,

        /// <summary>
        /// Before a reload: the subject arrow got a junction in this session and the canvas still
        /// carries draw.io's identifier for it, and a device was dropped in this session and its
        /// cell carries draw.io's identifier too. Both session maps are non-empty.
        /// </summary>
        BornInThisSession,
    }

    /// <summary>
    /// What the drawing says about a DEVICE's cell - the third device's, the one no arrow of this
    /// table hangs from.
    /// </summary>
    /// <remarks>
    /// It exists because widening the subjects to the device cells was not enough: they were all
    /// marked exactly as <c>DrawioDocument.Build</c> writes them, so the net walked 3840 roads with
    /// nothing to find and was green for the wrong reason. A subject nobody puts in trouble is a
    /// subject nobody is asking about.
    /// </remarks>
    private enum Shape
    {
        /// <summary>As Build wrote it: <c>&lt;object wcs="device"&gt;</c>.</summary>
        AsBuilt,

        /// <summary>The mark taken off by hand - Ctrl+M, the row's bin, Apply - and the cell is a plain shape.</summary>
        ItsMarkTakenOff,
    }

    private sealed record Road(Mark Mark, Ends Ends, Heads Heads, Ports Ports, Plant Plant, Meanwhile Meanwhile, Session Session, Shape Shape)
    {
        public override string ToString() =>
            $"mark={Mark} ends={Ends} heads={Heads} ports={Ports} plant={Plant} meanwhile={Meanwhile} session={Session} shape={Shape}";
    }

    private static IEnumerable<Road> AllRoads() =>
        from mark in Enum.GetValues<Mark>()
        from ends in Enum.GetValues<Ends>()
        from heads in Enum.GetValues<Heads>()
        from ports in Enum.GetValues<Ports>()
        from plant in Enum.GetValues<Plant>()
        from meanwhile in Enum.GetValues<Meanwhile>()
        from session in Enum.GetValues<Session>()
        from shape in Enum.GetValues<Shape>()
        select new Road(mark, ends, heads, ports, plant, meanwhile, session, shape);

    private static int Roads => Enum.GetValues<Mark>().Length * Enum.GetValues<Ends>().Length
        * Enum.GetValues<Heads>().Length * Enum.GetValues<Ports>().Length * Enum.GetValues<Plant>().Length
        * Enum.GetValues<Meanwhile>().Length * Enum.GetValues<Session>().Length * Enum.GetValues<Shape>().Length;

    /// <summary>
    /// <b>Part 2: every line the reader took out comes back exactly once, unless the plant took
    /// it.</b> One test over all the roads rather than one test per road: what a reader needs from
    /// a red here is the whole list of roads that broke, not the first of them.
    /// </summary>
    [Fact]
    public void EveryLineTheReaderTakesOutOfTheDrawingComesBackUnlessThePlantHoldsIt()
    {
        var broken = new List<Break>();
        var covered = 0;
        var takenByAnother = 0;

        foreach (var road in AllRoads())
        {
            var opened = PlantOf(road);
            var current = CurrentOf(road, opened);
            var xml = DrawingOf(road, opened);
            var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, current, JunctionMapOf(road), DeviceMapOf(road));

            // The host applies the writes to the plant as it is NOW, not to the snapshot the page
            // was opened with: that is the whole point of handing Compute the two of them.
            var afterTheSave = PlantAfterTheSave(current, delta);
            var reopened = DrawioDocument.Build(afterTheSave, delta.DecorationXml);
            var idsAtTheNextOpening = ElementIdsOf(reopened);
            var decoration = IdsOfTheDecoration(delta.DecorationXml);

            // AND THE DOCUMENT ITSELF IS CHECKED, because it is built with DrawioDocument.Build -
            // the product. Reading the subjects off it independently is only half the job: a Build
            // that stops writing a junction's cell takes that subject out of the list without a
            // word, and the net goes quiet exactly as it did when the subjects were the reader's
            // answer. So what the document MUST carry is worked out from the two inputs - the road
            // and the plant - and compared.
            //
            // It closes the coverage guard too: a subject reading that narrows - dropping the
            // wcs="junction" clause, say - interrogates fewer cells and this comparison says so by
            // name, where a "more than N cells" guard let it pass.
            var subjects = LinesTheDocumentCarries(xml);
            var mustCarry = TheSubjectsThisRoadMustHave(road, opened);
            if (!subjects.Order(StringComparer.Ordinal).SequenceEqual(mustCarry.Order(StringComparer.Ordinal), StringComparer.Ordinal))
                broken.Add(new Break("the document does not carry the cells this road is made of",
                    $"portate: {string.Join(" / ", subjects.Order(StringComparer.Ordinal))}",
                    road, "il documento in ingresso", "-",
                    $"attese: {string.Join(" / ", mustCarry.Order(StringComparer.Ordinal))}"));

            foreach (var subject in subjects)
            {
                covered++;
                var times = idsAtTheNextOpening.Count(id => string.Equals(id, subject, StringComparison.Ordinal));

                if (delta.NewJunctions.FirstOrDefault(n => n.Id == subject) is { } written)
                {
                    // THE PLANT TOOK IT. The cell itself goes, because the plant gives the junction
                    // an identifier of its own - but the LINE has to be there once, drawn by the
                    // plant between the two devices the delta named, or the operator's arrow was
                    // written and then lost from his canvas.
                    if (times != 0)
                        broken.Add(new Break("the plant took the cell AND the drawing kept it: it is on the canvas twice",
                            $"alla riapertura: {times} volte", road, WhoIs(subject), Said(delta, subject),
                            $"nata come {written.Source} -> {written.Destination}"));

                    var fromThePlant = EdgesOf(reopened)
                        .Count(e => e.Source == written.Source.ToString() && e.Target == written.Destination.ToString());
                    if (fromThePlant != 1)
                        broken.Add(new Break("the plant took the cell and the next opening does not draw that connection exactly once",
                            $"disegnata dall'impianto: {fromThePlant} volte", road, WhoIs(subject), Said(delta, subject),
                            $"nata come {written.Source} -> {written.Destination}"));
                    continue;
                }

                // THE NAMES THIS LINE CAN ANSWER TO. Usually one, its own - Build writes every
                // junction with its JunctionId. An arrow born in THIS session answers to two: the
                // canvas still carries draw.io's, the plant holds the host's, and the session map
                // is the one identity mismatch there is. Read here from the map the TEST hands in,
                // so the subject list and its identity both stay independent of the code under test.
                var names = JunctionMapOf(road).TryGetValue(subject, out var mapped) && mapped.ToString() != subject
                    ? new[] { subject, mapped.ToString() }
                    : DeviceMapOf(road).TryGetValue(subject, out var dropped) && dropped.ToString() != subject
                        ? [subject, dropped.ToString()]
                        : [subject];
                bool IsThisLine(string id) => names.Any(name => string.Equals(id, name, StringComparison.OrdinalIgnoreCase));

                // THE PLANT HOLDS IT, whichever kind of thing it is: a junction or a device. The
                // invariant's sentence does not distinguish them - "either the plant draws it, or
                // it is the operator's drawing" - and neither does this.
                var onThePlant = afterTheSave.Junctions.Any(j => IsThisLine(j.JunctionId.ToString()))
                    || afterTheSave.Items.Any(i => IsThisLine(i.ItemId.ToString()));
                var heldWhenOpened = opened.Junctions.Any(j => IsThisLine(j.JunctionId.ToString()))
                    || opened.Items.Any(i => IsThisLine(i.ItemId.ToString()));
                var inTheDrawing = decoration.Contains(subject, StringComparer.Ordinal);

                // WHAT THE SAVE SAID IS LOOKED FOR UNDER EVERY NAME THIS LINE ANSWERS TO, and not
                // only under the cell's: the save names a junction by the identifier the PLANT
                // gave it, which for an arrow born in this session is not the one on the canvas.
                // Asking under one name made a save that spoke read as a save that stayed quiet -
                // the same shape of defect this whole net was built to stop.
                var said = names.SelectMany(name => SentencesAbout(delta, name)).Distinct(StringComparer.Ordinal).ToList();

                // AND SOMEBODY ELSE MAY HAVE TAKEN IT. The plant held this line when the page was
                // opened and does not hold it now, and this save is not what removed it: another
                // operator did, while the drawing was open. The plant is the truth, so the line
                // goes off the canvas - putting it back would undo a deletion somebody meant - and
                // what the invariant asks of that road is the OTHER half of its sentence: that the
                // save says so by name.
                //
                // It is a clause and not an exemption, and the difference is that it bites: a
                // silent removal falls straight through to the three tests below and is red. It is
                // counted and printed, too, so that a road that stops reaching it is visible.
                var takenAwayByAnotherHand = heldWhenOpened
                    && !current.Junctions.Any(j => IsThisLine(j.JunctionId.ToString()))
                    && !current.Items.Any(i => IsThisLine(i.ItemId.ToString()))
                    && !delta.DeletedJunctions.Any(id => IsThisLine(id.ToString()))
                    && !delta.Deleted.Any(id => IsThisLine(id.ToString()));

                // The state the three facts leave the line in, printed with every red so that two
                // roads that end the same way are one category and two that do not are two.
                var onTheCanvas = idsAtTheNextOpening.Count(IsThisLine);
                var state = $"l'impianto la teneva all'apertura: {Yes(heldWhenOpened)}," +
                    $" la tiene finito il salvataggio: {Yes(onThePlant)}," +
                    $" in decorazione: {Yes(inTheDrawing)}," +
                    $" alla riapertura: {onTheCanvas} volte";

                if (takenAwayByAnotherHand && said.Count != 0)
                {
                    // WHICH of the two outcomes is right depends on WHOSE CELL IT IS, and the
                    // clause asks for the right one instead of accepting both.
                    //
                    // It accepted both until somebody read it: the comment said "a marked cell of
                    // the plant's goes off the canvas, an unmarked line of the operator's stays in
                    // his decoration", and the code below it said neither - so an unmarked line
                    // that vanished passed here, on the one road where nothing else was looking.
                    // A clause that names two outcomes and checks for neither is this net's own
                    // failure mode, arriving inside the net.
                    //
                    // The mark is read from the DOCUMENT, by the same independent reading as the
                    // subjects: the plant's own cells are the ones it wrote wcs="junction" or
                    // wcs="device" on, and everything else on the canvas is the operator's - a
                    // mark we cannot read included, because a mark we cannot read is not ours.
                    var thePlantsOwnCell = TheMarkTheDocumentGives(xml, subject) is "junction" or "device";

                    if (thePlantsOwnCell && onTheCanvas != 0)
                        broken.Add(new Break(
                            "somebody else removed this connection, and the cell the PLANT drew is still on the canvas",
                            state, road, WhoIs(subject), string.Join(" | ", said),
                            $"identificativi: {string.Join(" / ", names)}"));
                    else if (!thePlantsOwnCell && !inTheDrawing)
                        broken.Add(new Break(
                            "somebody else removed this connection, and the OPERATOR'S own line went with it",
                            state, road, WhoIs(subject), string.Join(" | ", said),
                            $"identificativi: {string.Join(" / ", names)}"));
                    else takenByAnother++;

                    continue;
                }

                // NEITHER, OR BOTH - the invariant said in its own words, before its consequence is
                // counted, so that a red names which half of the sentence broke.
                if (!onThePlant && !inTheDrawing)
                    broken.Add(new Break("NEITHER: the plant does not hold the line and the drawing does not carry it",
                        state, road, WhoIs(subject), said.Count == 0 ? "NIENTE" : string.Join(" | ", said),
                        $"identificativi: {string.Join(" / ", names)}"));
                else if (onThePlant && inTheDrawing)
                    broken.Add(new Break("BOTH: the plant holds the line and the drawing carries it too",
                        state, road, WhoIs(subject), said.Count == 0 ? "NIENTE" : string.Join(" | ", said),
                        $"identificativi: {string.Join(" / ", names)}"));

                // AND THE CONSEQUENCE: whichever of the two holds it, the next opening carries the
                // line once - under whichever of its names applies. This is the half that has been
                // broken three times.
                if (onTheCanvas != 1)
                    broken.Add(new Break($"the next opening carries the line {onTheCanvas} times, not once",
                        state, road, WhoIs(subject), said.Count == 0 ? "NIENTE" : string.Join(" | ", said),
                        $"identificativi: {string.Join(" / ", names)}"));
            }

            // The other half, asked of the whole drawing and not only of the subject: two elements
            // with one identifier is a drawing draw.io cannot tell apart either.
            //
            // AND NOW WITHOUT AN ESCAPE. It used to have one: when the operator stripped our mark
            // off a junction's cell, the plant kept the junction and the decoration kept his cell,
            // so the canvas carried two elements under one identifier - and that was called the
            // owner's choice between losing the cell and losing the junction. It was a false
            // dilemma: the cell was never his, and the plant knows which cell it drew that junction
            // on. The save takes it back out of the decoration and says so, so there is no
            // duplicate left to excuse. EVERY duplicate is a red.
            foreach (var group in idsAtTheNextOpening
                         .GroupBy(id => id, StringComparer.Ordinal).Where(g => g.Count() > 1))
                broken.Add(new Break($"the next opening carries {group.Count()} elements with one identifier",
                    $"alla riapertura: {group.Count()} volte", road, WhoIs(group.Key), Said(delta, group.Key),
                    $"identificativo {group.Key}"));

            // AND THE SAME QUESTION ASKED BY THING AND NOT BY NAME, which is the half the check
            // above cannot reach. A device dropped - or an arrow pulled - in THIS session carries
            // draw.io's identifier while the plant holds it under its own: two shapes for one
            // device, one over the other on the screen, and NOT ONE IDENTIFIER REPEATED. The check
            // above was written when the two names always coincided, and its comment said the
            // duplicate "is taken by the check on repeated identifiers" - true where they coincide
            // and false where they do not, which is the shape of mistake this series keeps meeting.
            //
            // One thing of the plant's, one element on the canvas: the names it can answer to come
            // from the session maps the TEST hands in, so the question stays independent of the
            // code under test.
            foreach (var (what, names) in afterTheSave.Junctions
                         .Select(j => (What: $"la giuntura {j.JunctionId}", Id: j.JunctionId.ToString()))
                         .Concat(afterTheSave.Items.Select(i => (What: $"il dispositivo {i.ItemId}", Id: i.ItemId.ToString())))
                         .Select(thing => (thing.What, Names: new[] { thing.Id }
                             .Concat(JunctionMapOf(road).Where(pair => pair.Value.ToString() == thing.Id).Select(pair => pair.Key))
                             .Concat(DeviceMapOf(road).Where(pair => pair.Value.ToString() == thing.Id).Select(pair => pair.Key))
                             .ToList())))
            {
                var times = idsAtTheNextOpening.Count(id => names.Contains(id, StringComparer.OrdinalIgnoreCase));
                if (times > 1)
                    broken.Add(new Break($"the next opening carries {times} elements for ONE thing of the plant's",
                        $"alla riapertura: {times} volte", road, what,
                        string.Join(" | ", names.SelectMany(name => SentencesAbout(delta, name)).Distinct(StringComparer.Ordinal).DefaultIfEmpty("NIENTE")),
                        $"identificativi: {string.Join(" / ", names)}"));
            }

            // AND THE ROUND IS STABLE: saving again, with nothing touched, writes nothing and hands
            // back the same drawing. An invariant that held once and oscillated afterwards would be
            // the same loss one save later. The second save is AFTER a reload by construction - the
            // page has just been rebuilt - so the session maps are empty and the two snapshots are
            // the same one.
            var again = DrawioDelta.Compute(DrawioReader.Read(reopened), afterTheSave, afterTheSave, NoJunctionMap, NoDeviceMap);

            // UNLESS THE FIRST SAVE SAID THE SECOND ONE WOULD WRITE, and this is the owner's
            // decision of 2026-09-20 arriving in the net. Another operator removed the connection
            // while the drawing was open; the arrow the operator drew for it stays on his canvas -
            // nothing leaves somebody's drawing because of somebody else's decision - so the next
            // save makes the connection again, because between two devices an arrow with a head is
            // a connection.
            //
            // IT HANGS ON THE SENTENCE, which is what stops it from being an exemption: the round
            // is allowed to write again only where the operator was TOLD it would, and only a
            // creation - a deletion, or a device written, is a break whatever was said. A save that
            // does this quietly is red exactly as before.
            var theFirstSaveAnnouncedIt = delta.Notices.Any(notice =>
                notice.Message.Contains("The next save makes the connection again", StringComparison.Ordinal));
            var onlyWhatWasAnnounced = theFirstSaveAnnouncedIt
                && again.DeletedJunctions.Count == 0 && again.Deleted.Count == 0;

            if ((again.NewJunctions.Count != 0 || again.DeletedJunctions.Count != 0 || again.Deleted.Count != 0)
                && !onlyWhatWasAnnounced)
                broken.Add(new Break("the save after it, with nothing touched, wrote again",
                    $"new={again.NewJunctions.Count} del={again.DeletedJunctions.Count} devices={again.Deleted.Count}",
                    road, "il secondo salvataggio", Said(again, S.ToString()), "niente e' stato toccato fra i due"));

            var openedAThirdTime = DrawioDocument.Build(PlantAfterTheSave(afterTheSave, again), again.DecorationXml);
            if (!string.Equals(openedAThirdTime, reopened, StringComparison.Ordinal) && !onlyWhatWasAnnounced)
                broken.Add(new Break("the drawing does not come back the same twice running", "il secondo e il terzo giro differiscono",
                    road, "il disegno intero", Said(again, S.ToString()), Difference(reopened, openedAThirdTime)));
        }

        output.WriteLine($"Strade percorse: {Roads}. Celle del documento interrogate: {covered}." +
            $" Tolte da un altro operatore, e il salvataggio l'ha detto: {takenByAnother}." +
            $" NESSUNA ESENZIONE: quella del marchio tolto a mano e' chiusa, non nascosta.");

        // Grouped by WHAT HAPPENED, to WHOM, and WHAT THE SAVE SAID - not by road: with the
        // subjects read from the document a single defect shows up on hundreds of roads, and what
        // a reader needs is the categories, which roads reach each one, and one example; not a
        // list he cannot read to the end. The count is the whole count.
        var categories = broken
            .GroupBy(b => $"{b.Shape}{Environment.NewLine}      soggetto: {b.Who}{Environment.NewLine}      stato: {b.State}" +
                $"{Environment.NewLine}      il salvataggio ha detto: {b.Said}", StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .Select(g => $"  [{g.Count()}] {g.Key}{Environment.NewLine}      strade: {Which(g.Select(b => b.Road))}" +
                $"{Environment.NewLine}      per esempio: {g.First().Road}{Environment.NewLine}      {g.First().Detail}")
            .ToList();

        foreach (var line in categories) output.WriteLine(line);

        Assert.True(broken.Count == 0,
            $"{broken.Count} rotture dell'invariante del disegno, in {categories.Count} categorie:" +
            $"{Environment.NewLine}{string.Join(Environment.NewLine, categories)}");
        // A net that walked no cell would be green for the wrong reason: this is the count of
        // subjects actually handed to the assertion above. At least one per road, because every
        // road carries a subject cell or a junction of the plant's, and usually both.
        // ESATTE, e non "piu' di una per strada": una guardia larga e' il secondo modo per far
        // smettere di chiedere a una rete - si toglie una clausola ai soggetti, si interroga meno e
        // la guardia non se ne accorge. Misurato: tolta la clausola del marchio, le celle
        // interrogate scendevano da 5120 a 4992 e la suite restava verde.
        Assert.True(covered == 29440,
            $"Le celle del documento interrogate sono {covered} invece di 29440: la tabella interroga meno di quello" +
            " che dichiara. Se e' la tabella a essere cambiata, si rimisura e si riscrive questo numero.");
    }

    /// <summary>
    /// <b>The other half of the same sentence, and the one nothing was asking: no junction the
    /// plant holds is deleted unless the drawing asked for it - and when it is deleted, the save
    /// says so.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why it has to be its own question.</b> The invariant above protects the OPERATOR'S
    /// DRAWING: every cell he can see is still there when the page is opened again. Nothing
    /// protected the PLANT. The two are not the same sum from different ends - a save can leave
    /// the operator's cell exactly where it was, satisfy that invariant to the letter, and quietly
    /// take the junction underneath it off the plant. That is measured, it is what the third round
    /// of review reported as its first finding, and it is why the drawing invariant's own remarks
    /// name it as a hole instead of leaving it to the green.
    /// </para>
    /// <para>
    /// <b>The subjects come from the PLANT WE HAND IN</b> - <c>current.Junctions</c>, the snapshot
    /// this test builds in <see cref="PlantOf"/> and <see cref="CurrentOf"/> - and not from
    /// <c>delta.DeletedJunctions</c>, which is the answer of the code under test. A delta that
    /// stops reporting a deletion does not make this test ask fewer questions: the junction was on
    /// the plant we handed in, so it is asked about, and the plant played back afterwards either
    /// still holds it or does not.
    /// </para>
    /// <para>
    /// <b>And what is expected is COMPUTED</b>, from the same document, read the same independent
    /// way: <see cref="WhatTheDrawingAsksFor"/> looks up the junction's cell by identifier and
    /// answers with XDocument and two style keys, never through <c>DrawioReader</c>. A deletion is
    /// allowed when the drawing no longer carries the cell, when the cell no longer carries
    /// exactly one arrowhead, or when its ends are no longer the junction's two devices. It is a
    /// PERMISSION and not a decision: the test never says a junction must die, only that one that
    /// died without any of those three facts died for no reason the drawing gave.
    /// </para>
    /// <para>
    /// <b>Both halves, because the formulation has two.</b> "Not deleted unless asked" and "if
    /// deleted, said" fail separately and are reported separately: a deletion the drawing asked
    /// for and nobody announced is the shape every measured loss of this series has had.
    /// </para>
    /// </remarks>
    [Fact]
    public void NoJunctionOfThePlantIsDeletedUnlessTheDrawingAsksAndTheSaveSaysSo()
    {
        var broken = new List<Break>();
        var interrogated = 0;
        var deleted = 0;

        foreach (var road in AllRoads())
        {
            var opened = PlantOf(road);
            var current = CurrentOf(road, opened);
            var xml = DrawingOf(road, opened);
            var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, current, JunctionMapOf(road), DeviceMapOf(road));
            var afterTheSave = PlantAfterTheSave(current, delta);

            // THE SUBJECTS ARE THE PLANT'S, taken from the snapshot this test handed to Compute.
            foreach (var junction in current.Junctions)
            {
                interrogated++;
                if (afterTheSave.Junctions.Any(j => j.JunctionId == junction.JunctionId)) continue;

                deleted++;
                var cellId = TheCellCarrying(road, junction.JunctionId);
                var asked = WhatTheDrawingAsksFor(TheDrawingsCell(xml, cellId), junction);
                var said = SentencesAbout(delta, junction.JunctionId.ToString())
                    .Concat(SentencesAbout(delta, cellId)).Distinct(StringComparer.Ordinal).ToList();

                // AND THE WRITES BLOCK COUNTS AS SAYING IT, which is the owner's decision of
                // 2026-09-20: the report declares every write to the plant by name, so a deletion
                // the save ASKED FOR is already there - "Written: the deletion of junction <id>",
                // DrawioEditor.razor. Deleting a line is a deliberate gesture: what is owed is not
                // an extra warning, it is that the result be visible.
                //
                // AT THIS LEVEL THAT HALF IS AN IDENTITY, and it is said instead of left to be
                // found: this test replays the host from delta.DeletedJunctions, so "the plant lost
                // it" and "the save asked for it" cannot come apart here. What CAN come apart is
                // the chain from that list to a line the operator reads, and until this batch
                // nothing measured it at all - no test in the repository mentioned that sentence.
                // DrawioTheDeletedLineIsDeclaredTest does, in a browser, against the real host.
                var amongTheWrites = delta.DeletedJunctions.Contains(junction.JunctionId);

                if (asked is null)
                    broken.Add(new Break("the plant lost a junction THE DRAWING DID NOT ASK TO LOSE",
                        $"il disegno porta ancora la stessa linea, con gli stessi capi e una punta sola;" +
                        $" il salvataggio ha parlato: {Yes(said.Count != 0)}",
                        road, $"la giunzione {junction.SourceItemId} -> {junction.DestinationItemId}",
                        said.Count == 0 ? "NIENTE" : string.Join(" | ", said),
                        $"giunzione {junction.JunctionId}, cella {cellId}"));

                if (said.Count == 0 && !amongTheWrites)
                    broken.Add(new Break("the junction was deleted and THE SAVE SAID NOTHING about it",
                        $"il disegno lo aveva chiesto: {Yes(asked is not null)}" +
                        (asked is null ? string.Empty : $" ({asked})"),
                        road, $"la giunzione {junction.SourceItemId} -> {junction.DestinationItemId}",
                        "NIENTE",
                        $"giunzione {junction.JunctionId}, cella {cellId}"));
            }
        }

        output.WriteLine($"Strade percorse: {Roads}. Giunzioni dell'impianto interrogate: {interrogated}," +
            $" di cui cancellate da questo salvataggio: {deleted}.");

        var categories = broken
            .GroupBy(b => $"{b.Shape}{Environment.NewLine}      soggetto: {b.Who}{Environment.NewLine}      stato: {b.State}" +
                $"{Environment.NewLine}      il salvataggio ha detto: {b.Said}", StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .Select(g => $"  [{g.Count()}] {g.Key}{Environment.NewLine}      strade: {Which(g.Select(b => b.Road))}" +
                $"{Environment.NewLine}      per esempio: {g.First().Road}{Environment.NewLine}      {g.First().Detail}")
            .ToList();

        foreach (var line in categories) output.WriteLine(line);

        Assert.True(broken.Count == 0,
            $"{broken.Count} giunzioni perse o perse in silenzio, in {categories.Count} categorie:" +
            $"{Environment.NewLine}{string.Join(Environment.NewLine, categories)}");
        // A net that watched no junction die would be green for the wrong reason, and so would one
        // that watched them all die: both halves need roads that reach them.
        Assert.True(deleted > 100 && deleted < interrogated,
            $"Su {interrogated} giunzioni interrogate ne sono state cancellate {deleted}: la tabella non esercita piu' le due meta' che dichiara.");
    }

    /// <summary>
    /// <b>Which cell of the drawing carries this junction.</b> Normally the junction's own
    /// identifier, because <c>DrawioDocument.Build</c> writes it as the cell's id; for a junction
    /// born in this session it is draw.io's identifier for the arrow, which the session map -
    /// written by this test, read here backwards - ties to it.
    /// </summary>
    private static string TheCellCarrying(Road road, Guid junctionId) =>
        JunctionMapOf(road).FirstOrDefault(pair => pair.Value == junctionId).Key ?? junctionId.ToString();

    /// <summary>What one cell of the input document says, read with XDocument and nothing else.</summary>
    private sealed record CellOfTheDocument(string? Source, string? Target, string Style);

    private static CellOfTheDocument? TheDrawingsCell(string documentXml, string id) =>
        XDocument.Parse(documentXml).Descendants("root").First().Elements()
            .Select(element => (Wrapper: element, Cell: element.Name == "mxCell" ? element : element.Element("mxCell")))
            .Where(pair => pair.Cell is not null)
            .Where(pair => string.Equals((string?)pair.Wrapper.Attribute("id") ?? (string?)pair.Cell!.Attribute("id"),
                id, StringComparison.Ordinal))
            .Select(pair => new CellOfTheDocument((string?)pair.Cell!.Attribute("source"),
                (string?)pair.Cell.Attribute("target"), (string?)pair.Cell.Attribute("style") ?? string.Empty))
            .FirstOrDefault();

    /// <summary>
    /// Where a style puts its arrowheads, read here from the two keys rather than asked of
    /// <c>DrawioReader.HeadsOf</c>: the expectation of a test cannot be computed by the code the
    /// test is watching. draw.io's defaults are a head where the line ends and none where it
    /// starts (<c>mxConstants</c>: <c>endArrow=classic</c>, <c>startArrow=none</c>), so an absent
    /// key is not an absent head.
    /// </summary>
    private static (bool AtStart, bool AtEnd) HeadsInTheStyle(string style)
    {
        string? Value(string key) => style.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(pair => pair.Length == 2 && string.Equals(pair[0].Trim(), key, StringComparison.Ordinal))
            .Select(pair => pair[1].Trim())
            .LastOrDefault();

        return (!string.Equals(Value("startArrow") ?? "none", "none", StringComparison.OrdinalIgnoreCase),
            !string.Equals(Value("endArrow") ?? "classic", "none", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// <b>What the drawing asks of this junction</b>, in words, or <c>null</c> when it asks
    /// nothing - the line is still there, still between the junction's two devices, still with one
    /// head. It is a permission and not a decision: see the test's remarks.
    /// </summary>
    private static string? WhatTheDrawingAsksFor(CellOfTheDocument? cell, JunctionDto junction)
    {
        if (cell is null) return "il disegno non porta piu' la sua cella";

        var (atStart, atEnd) = HeadsInTheStyle(cell.Style);
        if (atStart == atEnd)
            return atEnd ? "la linea ha una punta a tutti e due i capi" : "la linea non ha nessuna punta";

        // A head at the START is a connection running the other way: what the DRAWING says the two
        // ends are, which is the fact the ends have to be compared as.
        var (from, to) = atStart ? (cell.Target, cell.Source) : (cell.Source, cell.Target);
        if (!string.Equals(from, junction.SourceItemId.ToString(), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(to, junction.DestinationItemId.ToString(), StringComparison.OrdinalIgnoreCase))
            return $"i capi non sono piu' i suoi: {from ?? "(niente)"} -> {to ?? "(niente)"}";

        return null;
    }

    /// <summary>
    /// <b>Part 4: the inventory of what a line can be told.</b> Every sentence the subject cell
    /// collects across all the roads is one of these, and every one of these is met. A category of
    /// cell added tomorrow says something new, or stops something old from being reachable, and
    /// either way this goes red until it is declared here.
    /// </summary>
    [Fact]
    public void EveryOutcomeAnEdgeCanMeetIsDeclared()
    {
        // Declared, one line each, with the identifiers rubbed out. They are the product's own
        // words, copied from what the save says and not paraphrased: a paraphrase would make this
        // gate green on a message that changed.
        string[] declared = [.. TheSentences.Select(promise => promise.Sentence)];

        var met = new Dictionary<string, string>(StringComparer.Ordinal);
        var undeclared = new List<string>();

        foreach (var road in AllRoads())
        {
            var opened = PlantOf(road);
            var delta = DrawioDelta.Compute(DrawioReader.Read(DrawingOf(road, opened)), opened, CurrentOf(road, opened), JunctionMapOf(road), DeviceMapOf(road));
            // UNDER BOTH ITS NAMES, for the reason part 2 gives at the same place: the save names a
            // junction by the identifier the PLANT gave it, which for an arrow born in this session
            // is not the one on the canvas. Asked under the cell's name alone, a sentence the
            // product does say reads here as a sentence nobody reaches.
            var names = new List<string> { S.ToString() };
            if (JunctionMapOf(road).TryGetValue(S.ToString(), out var mapped)) names.Add(mapped.ToString());

            // AND THE DEVICES, which is the roads this inventory was missing and the reason the
            // device's own sentences had nobody watching them. The subject of this net used to be
            // the LINE, so a sentence the save addressed to a device was collected by nobody: not
            // declared here, therefore not recognized in part 5, therefore skipped without a word.
            // A sentence with no predicate is the thing the fourth net exists to forbid.
            foreach (var item in opened.Items)
            {
                names.Add(item.ItemId.ToString());
                foreach (var pair in DeviceMapOf(road).Where(pair => pair.Value == item.ItemId))
                    names.Add(pair.Key);
            }

            foreach (var sentence in names.SelectMany(name => SentencesAbout(delta, name)).Distinct(StringComparer.Ordinal))
            {
                if (declared.Contains(sentence, StringComparer.Ordinal)) met.TryAdd(sentence, road.ToString());
                else undeclared.Add($"{road}: {sentence}");
            }
        }

        foreach (var (sentence, road) in met.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            output.WriteLine($"[{road}] {sentence}");

        Assert.True(undeclared.Count == 0,
            $"Una linea puo' ricevere una risposta che l'inventario non dichiara - e' una categoria nuova:" +
            $"{Environment.NewLine}{string.Join(Environment.NewLine, undeclared.Distinct())}");

        var neverMet = declared.Where(d => !met.ContainsKey(d)).ToList();
        Assert.True(neverMet.Count == 0,
            $"L'inventario dichiara risposte che nessuna strada raggiunge piu': o la tabella ha smesso di coprirle, o il prodotto ha smesso di darle." +
            $"{Environment.NewLine}{string.Join(Environment.NewLine, neverMet)}");
    }


    /// <summary>
    /// <b>Part 5: what the report promises, the save that follows does.</b> The three nets before
    /// this one look at THINGS - the operator's lines, the plant's junctions, the plant's devices.
    /// This one looks at the WORDS, and three of the four defects of this series arrived as a
    /// sentence that said the opposite of what happened.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The same two rules.</b> The subjects come from an input - the sentences the save produced,
    /// which are its output for the first question and the INPUT for this one, the way the host's
    /// plant is the input of the second net. What is expected is computed: beside every sentence of
    /// the inventory there is a predicate over the save that follows, and the predicate asks an
    /// OBSERVABLE FACT - the plant afterwards, the canvas afterwards - never the reasoning that
    /// produced it. A predicate that rewrites the product's rule is blind by construction.
    /// </para>
    /// <para>
    /// <b>Two halves.</b> A promise not kept is red. And a sentence declared with NEITHER a
    /// predicate NOR a reason is red too, because that is the cheap way to make this net stop
    /// asking: add a sentence, leave it bare, and nobody checks it.
    /// </para>
    /// <para>
    /// <b>And the way this net could go quiet: a product that stops talking.</b> If the subjects are
    /// the sentences the product emits, a save that emits none hands this test nothing to check and
    /// it passes. It is the exact shape this series has already paid for twice, so it is closed
    /// twice over: the count of promises checked is EXACT and named here, and part 4 already fails
    /// when a declared sentence stops being reached by any road. A product that goes mute fails
    /// both.
    /// </para>
    /// </remarks>
    [Fact]
    public void WhatTheReportPromisesTheNextSaveDoes()
    {
        var broken = new List<Break>();
        var checked_ = 0;
        var withoutAPredicate = 0;

        // The sentences that carry neither a predicate nor a reason: the first half of the second
        // rule, and it is asked before a single road is walked.
        var bare = TheSentences.Where(promise => promise.Kept is null && promise.WhyNot.Length == 0).ToList();
        Assert.True(bare.Count == 0,
            $"{bare.Count} frasi dell'inventario non hanno ne' un predicato ne' una ragione per non averlo:" +
            $" nessuno controlla che siano mantenute.{Environment.NewLine}{string.Join(Environment.NewLine, bare.Select(p => p.Sentence))}");

        foreach (var road in AllRoads())
        {
            var opened = PlantOf(road);
            var current = CurrentOf(road, opened);
            var xml = DrawingOf(road, opened);
            var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, current, JunctionMapOf(road), DeviceMapOf(road));
            var afterTheSave = PlantAfterTheSave(current, delta);
            var reopened = DrawioDocument.Build(afterTheSave, delta.DecorationXml);
            var again = DrawioDelta.Compute(DrawioReader.Read(reopened), afterTheSave, afterTheSave, NoJunctionMap, NoDeviceMap);

            // EVERY sentence the save produced, about anybody: this is not the subject cell's net,
            // it is the report's.
            var said = delta.Refusals.Select(r => (r.Id, Raw: r.Reason))
                .Concat(delta.Notices.Select(n => (n.Id, Raw: n.Message)));

            foreach (var (id, raw) in said)
            {
                var sentence = Anonymous(raw);
                if (TheSentences.FirstOrDefault(promise => promise.Sentence == sentence) is not { } promised) continue;
                if (promised.Kept is null) { withoutAPredicate++; continue; }

                checked_++;
                var what = new WhatFollowed(id, raw, current, afterTheSave, reopened, again)
                {
                    // The cells the save wrote a junction from, under the plant's name when the
                    // session map knows it: what the save DID, read off its own write list.
                    WrittenFromTheCells = [.. delta.NewJunctions.Select(born =>
                        JunctionMapOf(road).TryGetValue(born.Id, out var itsJunction) ? itsJunction.ToString() : born.Id)],
                };
                if (!promised.Kept(what))
                    broken.Add(new Break("THE REPORT PROMISED SOMETHING THE NEXT SAVE DID NOT DO",
                        $"di: {id}", road, "una frase del verbale", sentence,
                        $"giunzioni dopo: {afterTheSave.Junctions.Count}, create al giro dopo: {again.NewJunctions.Count}," +
                        $" la cella alla riapertura: {ElementIdsOf(reopened).Count(drawn => drawn == id)} volte"));
            }
        }

        output.WriteLine($"Strade percorse: {Roads}. Promesse verificate: {checked_}." +
            $" Frasi senza predicato, e con la ragione scritta: {withoutAPredicate}.");

        var categories = broken
            .GroupBy(b => $"{b.Shape}{Environment.NewLine}      la frase: {b.Said}", StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .Select(g => $"  [{g.Count()}] {g.Key}{Environment.NewLine}      strade: {Which(g.Select(b => b.Road))}" +
                $"{Environment.NewLine}      per esempio: {g.First().Road}{Environment.NewLine}      {g.First().Detail}")
            .ToList();

        foreach (var line in categories) output.WriteLine(line);

        Assert.True(broken.Count == 0,
            $"{broken.Count} promesse che il salvataggio dopo non ha mantenuto, in {categories.Count} categorie:" +
            $"{Environment.NewLine}{string.Join(Environment.NewLine, categories)}");

        // IL PRODOTTO CHE SMETTE DI PARLARE, chiuso qui e in parte 4. Un numero esatto, e non "piu'
        // di zero": se il prodotto emette meno frasi, questa rete verifica meno promesse e il
        // numero lo dice. Se e' la tabella a essere cambiata, si rimisura e si riscrive.
        // 14460 until the fix round of queue item 11: 72 roads - a junction another hand removed,
        // re-hooked, and written again by this save - lost a line about the same junction that
        // contradicted the re-hook's own ("this save did not touch it"), and with it one promise each.
        Assert.True(checked_ == 14388,
            $"Le promesse verificate sono {checked_} invece di 14388: il verbale dice meno di quello che diceva," +
            " e una rete che ha per soggetti le parole di un prodotto muto non interroga nessuno.");
    }

    /// <summary>
    /// The case measured in a browser against the real host, kept by name because it is the third
    /// return of one defect: a cell of ours the plant does not hold, with one end detached. The
    /// save refuses it with <i>"it was not created, it stays a drawing"</i>, and the drawing it
    /// promises has to be there when the page is opened again.
    /// </summary>
    [Fact]
    public void AMarkedLineWithAnEndDetachedIsStillOnTheCanvasAtTheNextOpening()
    {
        var road = new Road(Mark.Junction, Ends.OneEndDetached, Heads.None, Ports.Hooked, Plant.Nothing, Meanwhile.NobodyTouchedIt, Session.AfterAReload, Shape.AsBuilt);
        var opened = PlantOf(road);
        var delta = DrawioDelta.Compute(DrawioReader.Read(DrawingOf(road, opened)), opened, opened, NoJunctionMap, NoDeviceMap);

        Assert.Contains(delta.Refusals, r => r.Id == S.ToString() && r.Reason.Contains("it stays a drawing"));
        Assert.Single(ElementIdsOf(DrawioDocument.Build(opened, delta.DecorationXml)), id => id == S.ToString());
    }

    /// <summary>
    /// The same cell with its two ends on the same device: not written, and the drawing keeps it.
    /// Its other half - what happens to the junction the plant already held - is
    /// <c>DrawioDeltaTest</c>'s.
    /// </summary>
    [Fact]
    public void AMarkedLineWithBothEndsOnOneDeviceIsStillOnTheCanvasAtTheNextOpening()
    {
        var road = new Road(Mark.Junction, Ends.TheSameDeviceTwice, Heads.None, Ports.Hooked, Plant.Nothing, Meanwhile.NobodyTouchedIt, Session.AfterAReload, Shape.AsBuilt);
        var opened = PlantOf(road);
        var delta = DrawioDelta.Compute(DrawioReader.Read(DrawingOf(road, opened)), opened, opened, NoJunctionMap, NoDeviceMap);

        Assert.Empty(delta.NewJunctions);
        Assert.Single(ElementIdsOf(DrawioDocument.Build(opened, delta.DecorationXml)), id => id == S.ToString());
    }

    /// <summary>
    /// And the other half of the invariant, which is what stops "hand everything back" from being
    /// a repair: a line the plant DOES hold is not in the decoration, or the canvas would carry it
    /// twice, one copy over the other.
    /// </summary>
    [Fact]
    public void ALineThePlantHoldsIsNotHandedBackToTheDrawing()
    {
        var road = new Road(Mark.Junction, Ends.TwoDevices, Heads.AtEnd, Ports.Hooked, Plant.AJunctionUnderThisIdentifier, Meanwhile.NobodyTouchedIt, Session.AfterAReload, Shape.AsBuilt);
        var opened = PlantOf(road);
        var delta = DrawioDelta.Compute(DrawioReader.Read(DrawingOf(road, opened)), opened, opened, NoJunctionMap, NoDeviceMap);

        Assert.Empty(delta.DeletedJunctions);
        Assert.DoesNotContain(S.ToString(), delta.DecorationXml);
        Assert.Single(ElementIdsOf(DrawioDocument.Build(opened, delta.DecorationXml)), id => id == S.ToString());
    }

    // ---- what a road is made of -------------------------------------------------------------

    /// <summary>The plant as the snapshot the page was OPENED with.</summary>
    private static PlantGraph PlantOf(Road road)
    {
        List<JunctionDto> junctions = road.Plant switch
        {
            Plant.AJunctionUnderThisIdentifier => [new JunctionDto(S, A, B, 99)],
            Plant.AJunctionUnderThisIdentifierAndItsMirror => [new JunctionDto(S, A, B, 99), new JunctionDto(Other, B, A, 99)],
            Plant.AnotherJunctionBetweenTheSameTwo => [new JunctionDto(Other, A, B, 99)],
            _ => [],
        };

        // The junction the host created for the subject arrow earlier in this session. The plant
        // holds it; the canvas does not carry it under this identifier, because the page has not
        // been rebuilt - see DrawingOf.
        //
        // AND THE SUBJECT'S CELL THEN CARRIES ONE IDENTITY, not two. A cell cannot be both a
        // junction's cell written by a rebuild AND an arrow drawn in this session whose identifier
        // is draw.io's: the session map is keyed on the identifier draw.io chose for an arrow
        // nobody had seen before. Leaving both in would hand the delta a drawing no gesture can
        // produce, and the reds that came out of it would be reds nobody can repair - which is
        // worse than a hole, because a hole at least does not send anybody anywhere.
        if (road.Session is Session.BornInThisSession)
        {
            junctions.RemoveAll(junction => junction.JunctionId == S);
            junctions.Add(new JunctionDto(SessionJunction, A, B, 99));
        }

        return new PlantGraph([Device(A, 100, 100), Device(B, 500, 100), Device(Dropped, 900, 100)], junctions, [Type()]);
    }

    /// <summary>
    /// The plant as it is NOW, which is the other snapshot <c>Compute</c> takes. Deleting the
    /// junctions is "somebody else worked on the plant while this drawing was open", and it is the
    /// condition the invariant's own words are about ("what the plant does not hold when THIS save
    /// is done").
    /// </summary>
    private static PlantGraph CurrentOf(Road road, PlantGraph opened) =>
        road.Meanwhile is Meanwhile.AnotherHandDeletedTheJunctions
            ? new PlantGraph(opened.Items, [], opened.Types)
            : opened;

    /// <summary>The junctions born in this session: draw.io's identifier for the arrow -> the plant's for the junction.</summary>
    private static IReadOnlyDictionary<string, Guid> JunctionMapOf(Road road) =>
        road.Session is Session.BornInThisSession
            ? new Dictionary<string, Guid>(StringComparer.Ordinal) { [S.ToString()] = SessionJunction }
            : NoJunctionMap;

    /// <summary>The devices dropped in this session, the twin map: draw.io's identifier for the cell -> the plant's for the device.</summary>
    private static IReadOnlyDictionary<string, Guid> DeviceMapOf(Road road) =>
        road.Session is Session.BornInThisSession
            ? new Dictionary<string, Guid>(StringComparer.Ordinal) { [DroppedCellId] = Dropped }
            : NoDeviceMap;

    /// <summary>
    /// The drawing the plant would be opened with, plus two notes of the operator's and the
    /// subject cell. Upright devices: this test measures what survives the round trip, and a
    /// rotation would add the two-decimal geometry question on top of it.
    /// </summary>
    private static string DrawingOf(Road road, PlantGraph plant)
    {
        var notes = $"""<mxCell id="{FirstNote}" value="una nota" style="shape=note;" vertex="1" parent="1"><mxGeometry x="40" y="600" width="220" height="60" as="geometry"/></mxCell>"""
            + $"""<mxCell id="{SecondNote}" value="un'altra nota" style="shape=note;" vertex="1" parent="1"><mxGeometry x="320" y="600" width="220" height="60" as="geometry"/></mxCell>""";

        var (source, target) = road.Ends switch
        {
            Ends.TwoDevices => (A.ToString(), B.ToString()),
            Ends.TwoDevicesTheOtherWayRound => (B.ToString(), A.ToString()),
            Ends.TheSameDeviceTwice => (A.ToString(), A.ToString()),
            Ends.ADeviceAndANote => (A.ToString(), FirstNote),
            Ends.OneEndDetached => (A.ToString(), null),
            Ends.TwoDevicesWithoutTheEdgeAttribute => (A.ToString(), B.ToString()),
            Ends.TwoNotes => (FirstNote, SecondNote),
            _ => (null, (string?)null),
        };

        var heads = road.Heads switch
        {
            Heads.None => "endArrow=none;",
            Heads.AtEnd => "endArrow=block;",
            Heads.AtStart => "endArrow=none;startArrow=block;",
            _ => "endArrow=block;startArrow=block;",
        };

        var ports = road.Ports == Ports.Hooked
            ? "exitX=0.96;exitY=0.5;exitDx=0;exitDy=0;entryX=0.04;entryY=0.5;entryDx=0;entryDy=0;"
            : string.Empty;

        var ends = (source is null ? "" : $" source=\"{source}\"") + (target is null ? "" : $" target=\"{target}\"");
        var inner = road.Ends switch
        {
            // A shape, not a line: no ends and no edge="1". Marked wcs="junction" it is a cell of
            // ours that is not an edge - what a Ctrl+V of hand-edited XML produces.
            Ends.NotALineAtAll =>
                $"""<mxCell style="{heads}{ports}rounded=0;html=1;" vertex="1" parent="1"><mxGeometry x="200" y="400" width="80" height="40" as="geometry"/></mxCell>""",
            // A line draw.io did not write: the ends are there, the attribute is not.
            Ends.TwoDevicesWithoutTheEdgeAttribute =>
                $"""<mxCell style="{heads}{ports}edgeStyle=none;rounded=0;html=1;" parent="1"{ends}><mxGeometry relative="1" as="geometry"/></mxCell>""",
            _ => $"""<mxCell style="{heads}{ports}edgeStyle=none;rounded=0;html=1;" edge="1" parent="1"{ends}><mxGeometry relative="1" as="geometry"/></mxCell>""",
        };

        var subject = road.Mark switch
        {
            Mark.None => inner.Replace("<mxCell ", $"""<mxCell id="{S}" """, StringComparison.Ordinal),
            Mark.Junction => $"""<object id="{S}" label="" wcs="junction">{inner}</object>""",
            _ => $"""<object id="{S}" label="" wcs="giunzione">{inner}</object>""",
        };

        // WHEN THE PLANT HOLDS A JUNCTION UNDER THE SUBJECT'S IDENTIFIER, the subject IS that
        // junction's cell: the page was opened, DrawioDocument.Build drew the junction, and the
        // operator changed it. So the cell Build wrote is taken out and the subject put in its
        // place - appending a second one would hand the reader a drawing with two elements under
        // one identifier, which is not a road of the product's, it is a broken table.
        var document = DrawioDocument.Build(plant, notes);
        if (plant.Junctions.Any(junction => junction.JunctionId == S))
        {
            var written = Regex.Match(document, $"""<object id="{S}".*?</object>""", RegexOptions.Singleline);
            Assert.True(written.Success, $"Il disegno di partenza non porta la giunzione {S}: la tabella non misura quello che dichiara.");
            document = document.Remove(written.Index, written.Length);
        }

        // BEFORE A RELOAD. The junction the host gave the subject arrow in this session is in the
        // plant, so Build draws it - but on the operator's canvas that arrow is the subject cell,
        // under draw.io's identifier, and only the session map knows the two are one. And the
        // device dropped in this session still carries draw.io's identifier for the same reason.
        if (road.Session is Session.BornInThisSession)
        {
            var born = Regex.Match(document, $"""<object id="{SessionJunction}".*?</object>""", RegexOptions.Singleline);
            Assert.True(born.Success, $"Il disegno di partenza non porta la giunzione di sessione {SessionJunction}: la tabella non misura quello che dichiara.");
            document = document.Remove(born.Index, born.Length);

            var renamed = document.Replace($"""<object id="{Dropped}" """, $"""<object id="{DroppedCellId}" """, StringComparison.Ordinal);
            Assert.NotEqual(document, renamed);
            document = renamed;
        }

        // THE MARK TAKEN OFF A DEVICE'S CELL, which is three gestures in draw.io's own Edit Data
        // (Ctrl+M, the row's bin, Apply - measured on the real binary). What is left is the plain
        // <mxCell> carrying our identifier, exactly as the drawing hands it back.
        if (road.Shape is Shape.ItsMarkTakenOff)
        {
            var deviceId = road.Session is Session.BornInThisSession ? DroppedCellId : Dropped.ToString();
            var wrapped = Regex.Match(document, $"""<object id="{deviceId}".*?</object>""", RegexOptions.Singleline);
            Assert.True(wrapped.Success, $"Il disegno di partenza non porta la cella del dispositivo {deviceId}.");

            var bare = wrapped.Value[wrapped.Value.IndexOf("<mxCell", StringComparison.Ordinal)
                    ..wrapped.Value.LastIndexOf("</object>", StringComparison.Ordinal)]
                .Replace("<mxCell ", $"""<mxCell id="{deviceId}" """, StringComparison.Ordinal);

            document = document.Remove(wrapped.Index, wrapped.Length).Insert(wrapped.Index, bare);
        }

        // THE OPERATOR DELETED THE LINE: nothing is put back. Everything else about the drawing is
        // what it was, which is what makes the deletion the only thing the save can be answering.
        return road.Ends is Ends.TheCellIsNotInTheDrawing
            ? document
            : document.Replace("</root>", subject + "</root>", StringComparison.Ordinal);
    }

    /// <summary>
    /// The host, played back: the junctions the save deleted are gone, the ones it asked for are
    /// there with an identifier of the plant's own choosing, and the priorities it changed are
    /// changed. It is the only way to ask what the NEXT opening draws.
    /// <para>
    /// It is played back on the plant as it is NOW and not on the snapshot the page was opened
    /// with, because that is what the host does: a junction another hand deleted meanwhile is not
    /// brought back to life by this save.
    /// </para>
    /// </summary>
    private static PlantGraph PlantAfterTheSave(PlantGraph now, DrawioDeltaResult delta)
    {
        var junctions = now.Junctions.Where(j => !delta.DeletedJunctions.Contains(j.JunctionId)).ToList();
        for (var i = 0; i < delta.NewJunctions.Count; i++)
        {
            var born = delta.NewJunctions[i];
            junctions.Add(new JunctionDto(Guid.Parse($"77777777-7777-7777-7777-7777777777{i:00}"),
                born.Source, born.Destination, born.Priority));
        }

        for (var i = 0; i < junctions.Count; i++)
            if (delta.JunctionPriorities.FirstOrDefault(p => p.JunctionId == junctions[i].JunctionId) is { } changed)
                junctions[i] = junctions[i] with { Priority = changed.NewPriority };

        return new PlantGraph(
            now.Items.Where(item => !delta.Deleted.Contains(item.ItemId)).ToList(),
            junctions,
            now.Types);
    }


    /// <summary>
    /// <b>Che cosa il disegno e l'impianto sono diventati dopo il salvataggio da cui la frase e'
    /// uscita, e dopo quello che l'ha seguito.</b> Sono i soli fatti che un predicato puo'
    /// guardare: l'impianto dopo, la tela dopo, il giro dopo.
    /// </summary>
    private sealed record WhatFollowed(
        string Id, string Raw, PlantGraph Current, PlantGraph AfterTheSave,
        string Reopened, DrawioDeltaResult Again)
    {
        /// <summary>The cells this save wrote a junction from, each under the plant's name when it has one.</summary>
        public IReadOnlyList<string> WrittenFromTheCells { get; init; } = [];
    }

    /// <summary>
    /// <b>Una frase del verbale e la cosa che promette</b>, scritta come predicato sul giro dopo.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Kept"/> nullo vuol dire che la frase <b>non promette un fatto</b>, e allora
    /// <see cref="WhyNot"/> dice perche' - e la rete lo dichiara invece di lasciarlo implicito. Una
    /// frase senza predicato E senza ragione e' rossa: e' il modo in cui questa rete potrebbe
    /// smettere di chiedere, e si chiude cosi'.
    /// </para>
    /// <para>
    /// <b>Il predicato chiede un FATTO OSSERVABILE</b> - l'impianto tiene o non tiene, la tela porta
    /// o non porta - e mai il calcolo che lo produce. Un predicato che rifa' la regola del prodotto
    /// e' cieco per costruzione, ed e' la forma che questa serie ha gia' pagato.
    /// </para>
    /// </remarks>
    private sealed record Promise(string Sentence, Func<WhatFollowed, bool>? Kept, string WhyNot = "");

    /// <summary>Se la tela, alla prossima apertura, porta questo identificativo.</summary>
    private static bool OnTheCanvas(WhatFollowed what, string id) =>
        ElementIdsOf(what.Reopened).Any(drawn => string.Equals(drawn, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Se l'impianto, finito il salvataggio, tiene una giunzione sotto questo identificativo.</summary>
    private static bool ThePlantHolds(WhatFollowed what, string id) =>
        what.AfterTheSave.Junctions.Any(j => string.Equals(j.JunctionId.ToString(), id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Se l'impianto, finito il salvataggio, tiene un dispositivo sotto questo identificativo.</summary>
    private static bool ThePlantHoldsTheDevice(WhatFollowed what, string id) =>
        what.AfterTheSave.Items.Any(item => string.Equals(item.ItemId.ToString(), id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// <b>«Torna come una cosa nostra»</b>: l'impianto la tiene, e la prossima apertura la porta UNA
    /// volta sola e con il marchio nostro. Un posto solo, perche' quattro frasi del verbale
    /// promettono questo e quattro copie del predicato divergerebbero alla prima che cambia.
    /// </summary>
    /// <remarks>
    /// Il marchio non e' un dettaglio: una cella che torna SENZA marchio e' disegno libero, e la
    /// frase prometteva il contrario. Per questo e' un parametro e non una costante - giuntura o
    /// dispositivo - invece di due predicati che si somigliano.
    /// </remarks>
    private static bool ComesBackAsOurs(WhatFollowed what, string id, string mark) =>
        (mark == "junction" ? ThePlantHolds(what, id) : ThePlantHoldsTheDevice(what, id))
        && ElementIdsOf(what.Reopened).Count(drawn => string.Equals(drawn, id, StringComparison.OrdinalIgnoreCase)) == 1
        && TheMarkTheDocumentGives(what.Reopened, id) == mark;

    /// <summary>
    /// <b>«Questa cella non e' piu' disegno libero»</b>, che ha due esiti e non uno - ed e' la
    /// distinzione che il proprietario ha deciso il 20/9/2026. Se la cella porta il nome
    /// dell'impianto, il ridisegno PRENDE IL SUO POSTO e l'identificativo resta sulla tela una volta
    /// sola; se porta quello di draw.io - una freccia tirata o un dispositivo lasciato cadere in
    /// questa sessione - il ridisegno arriva sotto un altro nome e la cella dev'essere sparita, o
    /// sarebbero due cose per una sola.
    /// </summary>
    private static bool TheCellIsNotFreeDrawingAnyMore(WhatFollowed what, string cellId, string theThingThePlantHolds) =>
        string.Equals(cellId, theThingThePlantHolds, StringComparison.OrdinalIgnoreCase) || !OnTheCanvas(what, cellId);

    /// <summary>Se nessuna giunzione che l'impianto teneva adesso e' uscita con questo salvataggio.</summary>
    private static bool NothingWasTakenOffThePlant(WhatFollowed what) =>
        what.Current.Junctions.All(had => what.AfterTheSave.Junctions.Any(now => now.JunctionId == had.JunctionId));

    /// <summary>
    /// Whether this save wrote a junction out of the cell of the thing the sentence is about (fix
    /// round of queue item 11): a line that says "this save did not touch it" must not stand beside it.
    /// </summary>
    private static bool ThisSaveWroteAJunctionFromItsCell(WhatFollowed what) =>
        what.WrittenFromTheCells.Contains(what.Id, StringComparer.OrdinalIgnoreCase);

    /// <summary>Se l'impianto, finito il salvataggio, tiene una giunzione che prima non teneva.</summary>
    private static bool SomethingWasWritten(WhatFollowed what) =>
        what.AfterTheSave.Junctions.Any(now => what.Current.Junctions.All(had => had.JunctionId != now.JunctionId));

    /// <summary>
    /// A re-hook refused on a junction another hand had removed (queue item 11): nothing written,
    /// nothing taken away, and the plant does not hold a junction under the name the line is said to.
    /// </summary>
    private static bool TheReHookAnotherHandHadTakenWasNotWritten(WhatFollowed what) =>
        !SomethingWasWritten(what) && NothingWasTakenOffThePlant(what) && !ThePlantHolds(what, what.Id);

    /// <summary>
    /// <b>La cella che la frase stessa nomina</b>, scritta "(cell x)" come il resto del verbale
    /// nomina una cella. Letta cosi' e non con la regola degli identificativi: il nome di una cella
    /// e' quello che le da' il DISEGNO - dell'impianto dopo un ricaricamento, di draw.io prima - e
    /// quello di draw.io non e' un GUID (misurato sul binario vero: <c>yzN2LFKUDLUVZ7upwRUZ-1</c>).
    /// </summary>
    private static string? TheCellNamedIn(string sentence) =>
        Regex.Match(sentence, @"\(cell ([^)]+)\)") is { Success: true } named ? named.Groups[1].Value : null;

    /// <summary>Gli identificativi che la frase stessa nomina, letti dal testo: un fatto, non una regola.</summary>
    private static IEnumerable<string> TheIdentifiersNamedIn(string sentence) =>
        Regex.Matches(sentence, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")
            .Select(match => match.Value);

    /// <summary>
    /// <b>L'inventario delle frasi, e accanto a ognuna cio' che promette.</b> Un posto solo: la
    /// parte 4 legge le frasi, la parte 5 i predicati, e due elenchi divergerebbero al primo che
    /// tocca una frase.
    /// </summary>
    private static readonly Promise[] TheSentences =
    [
        new("somebody else removed this connection from the plant while you had the drawing open: this save did not touch it, and the line goes off your drawing at the next opening. To join the two devices again, draw a new arrow from one to the other and save.",
            // "THIS SAVE DID NOT TOUCH IT" IS ASKED TOO, and not only "the line goes": the review of
            // queue item 11 found this line said beside a re-hook of the same junction that the save
            // DID write, and the id alone could not see it - the old cell goes either way.
            what => !OnTheCanvas(what, what.Id) && !ThisSaveWroteAJunctionFromItsCell(what)),

        new("somebody else removed this connection from the plant while you had the drawing open, and the arrow you drew for it is still on your canvas: this save did not touch the plant, and it stays your drawing. The next save makes the connection again - between two devices an arrow with a head is a connection - and whoever removed it finds out at his own next save.",
            what => what.Again.NewJunctions.Count > 0 && !ThisSaveWroteAJunctionFromItsCell(what)),

        new("somebody else removed this connection from the plant while you had the drawing open, and the cell you have for it is not going to connect them again - this save has told you why, on its own line: it stays your free drawing. To join the two devices, draw a new arrow from one to the other and save.",
            what => what.Again.NewJunctions.Count == 0 && !ThisSaveWroteAJunctionFromItsCell(what)),

        new("the arrow you drew in this session is no longer a connection of ours, and the plant still holds the junction it was drawn for: the junction was not deleted, the cell of your arrow (cell <id>) is not free drawing, and at the next opening it comes back as a junction of ours",
            // AND THE CELL THE SENTENCE NAMES IS GONE, read out of the line itself: that is the
            // half the owner decided on 2026-09-20, and it is the half a predicate on the
            // junction's own identifier cannot see - the cell carries draw.io's name, so the canvas
            // can hold it beside the redraw without ever repeating an identifier.
            // LA CELLA DEV'ESSERE NOMINATA, cosi' che una frase che smettesse di nominarla faccia
            // rosso qui invece di rendere vuoto il predicato.
            what => ComesBackAsOurs(what, what.Id, "junction")
                && TheCellNamedIn(what.Raw) is { } hisCell
                && TheCellIsNotFreeDrawingAnyMore(what, hisCell, what.Id)),

        new("the mark was taken off this cell (cell <id>), and the plant still holds the junction it was drawn for: the junction was not deleted, the cell is not free drawing, and at the next opening it comes back as a junction of ours",
            what => ComesBackAsOurs(what, what.Id, "junction")
                && TheCellNamedIn(what.Raw) is { } strippedCell
                && TheCellIsNotFreeDrawingAnyMore(what, strippedCell, what.Id)),

        // LA FRASE CHE NON AVEVA NESSUN PREDICATO, ed e' la gemella di quella sopra detta a un
        // dispositivo. Non la sorvegliava nessuno per una ragione sola: questa rete raccoglieva le
        // frasi dette alla LINEA, e una riga indirizzata a un dispositivo non era ne' dichiarata
        // qui ne' riconosciuta dalla parte 5, che la saltava senza dire niente. Le strade c'erano
        // gia' tutte - 3840, meta' della tabella, e su ognuna il verbale diceva questa frase - e
        // mancavano i SOGGETTI.
        new("<device>: the mark was taken off this cell (cell <id>), and the plant still holds the device it was drawn for: the device was not deleted, the cell is not free drawing, and at the next opening it comes back as a device of ours",
            what => ComesBackAsOurs(what, what.Id, "device")
                && TheCellNamedIn(what.Raw) is { } strippedShape
                && TheCellIsNotFreeDrawingAnyMore(what, strippedShape, what.Id)),

        // Here the subject is THE CELL and the sentence names THE CONNECTION, which after a reload
        // is the same identifier and before one is not: the predicate asks both, of the right one.
        new("one end of this arrow hangs from a device and the other from nothing, and the plant still holds the connection it made (<id>): the connection was not removed, this cell is not free drawing, and at the next opening the plant draws that connection again between the two devices it joined - to take it away, delete the line and save.",
            what => TheIdentifiersNamedIn(what.Raw).ToList() is { Count: 1 } connection
                && ComesBackAsOurs(what, connection[0], "junction")
                && TheCellIsNotFreeDrawingAnyMore(what, what.Id, connection[0])),

        new("the arrow does not hook two ports: it was not created, it stays a drawing",
            what => OnTheCanvas(what, what.Id)),

        new("the arrow joins a device to itself, and a connection joins two different devices: it was not created, it stays a drawing",
            what => OnTheCanvas(what, what.Id)),

        new("the junction has been re-hooked, or detached, in the drawing, but the new arrow joins a device to itself, and a connection joins two different devices: the plant still holds the ends it had, it was not written back",
            NothingWasTakenOffThePlant),

        new("this line does not point one way, and between two devices that would mean it is no longer a connection - but its two ends are not two DIFFERENT cells this drawing marks as devices of ours, so the head was not read at all and the rule was not applied. What happens to the junction is decided by the ends alone: look at the cells the two ends hang from.",
            null, "non promette un fatto: spiega COME si e' deciso, e rimanda a un'altra riga del verbale per l'esito. Un predicato qui sarebbe la regola della punta riscritta."),

        new("the arrow does not join two devices the plant knows: it was not created, it stays a drawing",
            what => OnTheCanvas(what, what.Id)),

        new("the arrow doubles a junction that already exists between these two devices: it was not created, it stays a drawing",
            what => OnTheCanvas(what, what.Id)),

        new("the junction has been re-hooked, or detached, in the drawing, but the new arrow does not hook two ports: the plant still holds the ends it had, it was not written back",
            NothingWasTakenOffThePlant),

        new("the junction has been re-hooked, or detached, in the drawing, but the new arrow does not join two devices the plant knows: the plant still holds the ends it had, it was not written back",
            NothingWasTakenOffThePlant),

        new("the junction has been re-hooked, or detached, in the drawing, but the new arrow doubles a junction that already exists between these two devices: the plant still holds the ends it had, it was not written back",
            NothingWasTakenOffThePlant),

        new("mark '<mark>' not recognized: the cell is neither a device nor a junction, it was not written",
            what => ThePlantHolds(what, what.Id) == what.Current.Junctions.Any(j => string.Equals(j.JunctionId.ToString(), what.Id, StringComparison.OrdinalIgnoreCase))),

        new("the arrow has been hooked onto other ports of the same two devices: the plant does not store the port of a junction, the hooking was not written",
            what => NothingWasTakenOffThePlant(what) && !SomethingWasWritten(what)),

        new("junction re-hooked: redone as a deletion plus a creation, with a new identifier",
            SomethingWasWritten),

        // Queue item 11: the same re-hook of a junction ANOTHER HAND had already removed. Nothing
        // was there for this save to delete, and the line says so: the promise is a junction
        // written and none taken away.
        new("junction re-hooked: somebody else had already removed it from the plant while you were drawing, so this save deleted nothing - it created the connection you drew, with a new identifier",
            what => SomethingWasWritten(what) && NothingWasTakenOffThePlant(what)),

        // And refused: the old words promised "the plant still holds the ends it had", which
        // NothingWasTakenOffThePlant kept vacuously - the junction was not in the plant to take.
        // These promise the opposite, and it is asked: nothing written, and the plant does not
        // hold the junction under the name the line is said to.
        new("the junction has been re-hooked, or detached, in the drawing, but the new arrow does not hook two ports: it was not written, and the plant does not hold the junction any more either - somebody else removed it while you were drawing",
            TheReHookAnotherHandHadTakenWasNotWritten),

        new("the junction has been re-hooked, or detached, in the drawing, but the new arrow does not join two devices the plant knows: it was not written, and the plant does not hold the junction any more either - somebody else removed it while you were drawing",
            TheReHookAnotherHandHadTakenWasNotWritten),

        new("the junction has been re-hooked, or detached, in the drawing, but the new arrow joins a device to itself, and a connection joins two different devices: it was not written, and the plant does not hold the junction any more either - somebody else removed it while you were drawing",
            TheReHookAnotherHandHadTakenWasNotWritten),

        new("new junction: the hooking you drew is not stored - the plant does not hold the port of a junction - and at the next opening the drawing hooks the arrow back onto the registry's ports",
            what => what.Reopened.Contains("exitX=0.96", StringComparison.Ordinal)),

        new("the arrow declares no priority: the junction is born at low priority (<n>)",
            SomethingWasWritten),

        new("one end of this arrow hangs from a device and the other from nothing: it joins no two devices, so no junction was created and it stays a drawing - to connect two devices, let go of the arrow on a connection point of the second one",
            what => OnTheCanvas(what, what.Id)),

        new("this line joins two devices and has no arrowhead: between two devices it is the head that says 'connection', and which way it points is the way the connection goes. No junction was created and it stays a drawing - to connect the two devices, draw an arrow from one to the other, or give this line a head (select it and press Ctrl+E, Edit Style, then put endArrow=classic in its style).",
            what => OnTheCanvas(what, what.Id)),

        new("this line joins two devices and has a head at BOTH ends: a connection goes one way, and a connection both ways is two connections, one line with its head on each device. No junction was created and it stays a drawing - to connect the two devices, take one of the two heads off (select the line and press Ctrl+E, Edit Style, then put startArrow=none or endArrow=none in its style) and draw a second line the other way if both directions are wanted.",
            what => OnTheCanvas(what, what.Id)),

        new("the arrowhead has been taken off this line, and between two devices it is the head that says 'connection': the junction it joined the two devices with (<id>) has been DELETED from the plant. To join them again, give the line a head (select it and press Ctrl+E, Edit Style, then put endArrow=classic in its style) and save, or draw a new arrow from one device to the other.",
            what => TheIdentifiersNamedIn(what.Raw).All(named => !ThePlantHolds(what, named))),

        new("this line has been given a head at BOTH ends, and a connection goes one way: the junction it joined the two devices with (<id>) has been DELETED from the plant. To join them again, take one of the two heads off (select the line and press Ctrl+E, Edit Style, then put startArrow=none or endArrow=none in its style) and save; a connection both ways is two lines, one head each way.",
            what => TheIdentifiersNamedIn(what.Raw).All(named => !ThePlantHolds(what, named))),
    ];

    /// <summary>One broken road, kept whole so that the reds can be grouped by what happened and not by where.</summary>
    private sealed record Break(string Shape, string State, Road Road, string Who, string Said, string Detail);

    /// <summary>A fact in one word, so that a state reads at a glance.</summary>
    private static string Yes(bool fact) => fact ? "si'" : "no";

    /// <summary>Who the subject is, in words, so that two roads that lost the same thing read the same.</summary>
    private static string WhoIs(string id) =>
        id == S.ToString() ? "la cella del disegno (il soggetto)"
        : id == Other.ToString() ? "una giunzione dell'impianto (non il soggetto)"
        : id == SessionJunction.ToString() ? "la giunzione nata in questa sessione"
        : id.StartsWith("77777777", StringComparison.Ordinal) ? "una giunzione nata da questo salvataggio"
        : id;

    /// <summary>
    /// Which roads reach a category: for every dimension, the values that appear - and "tutte"
    /// when they all do. A category that a whole dimension does not narrow is a category that
    /// dimension has nothing to do with, and that is the fact a reader is after.
    /// </summary>
    private static string Which(IEnumerable<Road> roads)
    {
        var all = roads.ToList();
        string Values<T>(Func<Road, T> of) where T : struct, Enum
        {
            var seen = all.Select(of).Distinct().ToList();
            return seen.Count == Enum.GetValues<T>().Length ? "tutte" : string.Join("/", seen);
        }

        return $"mark={Values(r => r.Mark)} ends={Values(r => r.Ends)} heads={Values(r => r.Heads)}" +
            $" ports={Values(r => r.Ports)} plant={Values(r => r.Plant)} meanwhile={Values(r => r.Meanwhile)} session={Values(r => r.Session)}";
    }

    /// <summary>Where two drawings stop being the same, with a window around it: a whole document twice is unreadable.</summary>
    private static string Difference(string one, string other)
    {
        var at = 0;
        while (at < one.Length && at < other.Length && one[at] == other[at]) at++;
        string Window(string text) => at >= text.Length ? "(finito)"
            : text.Substring(at, Math.Min(220, text.Length - at));
        return $"divergono al carattere {at}:{Environment.NewLine}        secondo giro: {Window(one)}{Environment.NewLine}        terzo giro:   {Window(other)}";
    }

    /// <summary>
    /// <b>The subjects of the invariant, read off the document we are about to hand in.</b> A cell
    /// is one of them when its <c>&lt;mxCell&gt;</c> says it is an edge - <c>edge="1"</c>, or a
    /// <c>source</c>, or a <c>target</c> - or when the drawing marks it <c>wcs="junction"</c>,
    /// which is a cell claiming to be a connection of ours whatever else it is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this reading and not the reader's.</b> It parses the XML here, with XDocument and
    /// three attribute names, and touches no line of <c>Wcs.WebEditor.Drawio</c>: not
    /// <c>DrawioReader</c>, not <c>DrawioCell.IsALine</c>, not <c>DrawioContents</c>. A defect in
    /// any of them cannot take a cell off this list, which is the whole point - the list of who
    /// gets questioned must not be written by the one being questioned.
    /// </para>
    /// <para>
    /// <b>And deliberately WIDER than <c>IsALine</c>.</b> The mark is enough on its own, so a cell
    /// the drawing calls a junction of ours and that is not an edge at all is asked the same
    /// question as the rest. A net the size of the classification could never see a category the
    /// classification drops.
    /// </para>
    /// <para>
    /// <b>And the DEVICE cells too, which nothing was asking about.</b> The three nets of this
    /// series were written about lines - this one and the second about lines, the third about the
    /// plant's devices and not about their CELLS - so the drawing could carry a device's shape
    /// twice and no question was put to it. It is not a hypothetical: strip our mark off a device's
    /// cell and the save refuses the deletion, the plant keeps the device and redraws its shape,
    /// and the operator's stripped cell stays in the decoration under the same identifier. That is
    /// "both" in the invariant's own words, and the invariant is the right question already - only
    /// nobody was asking it of these cells.
    /// </para>
    /// </remarks>
    private static List<string> LinesTheDocumentCarries(string documentXml) =>
        XDocument.Parse(documentXml).Descendants("root").First().Elements()
            .Select(element => (Wrapper: element, Cell: element.Name == "mxCell" ? element : element.Element("mxCell")))
            .Where(pair => pair.Cell is not null)
            .Where(pair => pair.Cell!.Attribute("edge")?.Value == "1"
                || pair.Cell.Attribute("source") is not null
                || pair.Cell.Attribute("target") is not null
                || (string?)pair.Wrapper.Attribute("wcs") is "junction" or "device")
            .Select(pair => (string?)pair.Wrapper.Attribute("id") ?? (string?)pair.Cell!.Attribute("id") ?? string.Empty)
            .Where(id => id.Length != 0 && id is not ("0" or "1"))
            .ToList();

    /// <summary>
    /// What the document marks this cell with, or <c>null</c> when it marks it with nothing. Read
    /// the same independent way as the subjects, with XDocument and one attribute name: whose cell
    /// it is has to be a fact of the drawing we handed in, not an answer of the reader's.
    /// </summary>
    private static string? TheMarkTheDocumentGives(string documentXml, string id) =>
        XDocument.Parse(documentXml).Descendants("root").First().Elements()
            .Where(element => string.Equals((string?)element.Attribute("id")
                ?? (string?)element.Element("mxCell")?.Attribute("id"), id, StringComparison.Ordinal))
            .Select(element => (string?)element.Attribute("wcs"))
            .FirstOrDefault();

    /// <summary>
    /// <b>Le celle che il documento di questa strada DEVE portare</b>, ricavate dai due ingressi -
    /// la strada e l'impianto - e non dal documento.
    /// </summary>
    /// <remarks>
    /// E' la rete attorno a <see cref="DrawingOf"/>, che il documento lo costruisce con
    /// <c>DrawioDocument.Build</c>: il prodotto. Senza questo confronto un <c>Build</c> che smette
    /// di scrivere la cella di una giunzione toglie quel soggetto dall'elenco senza una parola, e
    /// la rete tace - la stessa forma che questa serie ha gia' pagato due volte.
    /// </remarks>
    private static List<string> TheSubjectsThisRoadMustHave(Road road, PlantGraph opened)
    {
        var expected = new List<string>();

        // Le giunzioni che l'impianto tiene, come Build le disegna - meno le due che DrawingOf
        // toglie: quella sotto l'identificativo del soggetto, al cui posto va il soggetto, e quella
        // nata in questa sessione, che prima di un ricaricamento sulla tela non c'e'.
        foreach (var junction in opened.Junctions)
        {
            if (junction.JunctionId == S || junction.JunctionId == SessionJunction) continue;
            expected.Add(junction.JunctionId.ToString());
        }

        // I dispositivi, che Build disegna tutti e tre - e quello lasciato cadere in questa
        // sessione porta l'identificativo di draw.io.
        //
        // MENO QUELLO A CUI IL MARCHIO E' STATO TOLTO: senza marchio quella cella non dichiara piu'
        // di essere nostra e non e' un arco, quindi la lettura dei soggetti non la conta - ed e'
        // giusto cosi', perche' quella lettura chiede al DISEGNO che cosa dice di se'. Il doppione
        // che quella cella produce lo prende il controllo sugli identificativi ripetuti, che guarda
        // il disegno intero e non i soli soggetti.
        foreach (var item in opened.Items)
        {
            if (item.ItemId == Dropped && road.Shape is Shape.ItsMarkTakenOff) continue;
            expected.Add(item.ItemId == Dropped && road.Session is Session.BornInThisSession
                ? DroppedCellId
                : item.ItemId.ToString());
        }

        // E il soggetto, quando il disegno lo porta: una cella marchiata conta per il marchio, una
        // non marchiata solo se e' un arco.
        if (road.Ends is not Ends.TheCellIsNotInTheDrawing
            && (road.Mark is Mark.Junction || road.Ends is not Ends.NotALineAtAll))
            expected.Add(S.ToString());

        return expected;
    }

    /// <summary>The identifiers the decoration carries - the operator's free drawing, handed back by the save.</summary>
    private static List<string> IdsOfTheDecoration(string decorationXml) =>
        XDocument.Parse($"<root>{decorationXml}</root>").Root!.Elements()
            .Select(element => (string?)element.Attribute("id")
                ?? (string?)element.Element("mxCell")?.Attribute("id") ?? string.Empty)
            .Where(id => id.Length != 0)
            .ToList();

    /// <summary>Everything the save said about one cell, on one line, for a red that has to be read.</summary>
    private static string Said(DrawioDeltaResult delta, string id) =>
        SentencesAbout(delta, id).ToList() is { Count: > 0 } sentences ? string.Join(" | ", sentences) : "NIENTE";

    /// <summary>What the save said about one cell, with the identifiers rubbed out so that two roads that meet the same answer read the same.</summary>
    private static IEnumerable<string> SentencesAbout(DrawioDeltaResult delta, string id) =>
        delta.Refusals.Where(r => r.Id.Contains(id, StringComparison.Ordinal)).Select(r => Anonymous(r.Reason))
            .Concat(delta.Notices.Where(n => n.Id.Contains(id, StringComparison.Ordinal)).Select(n => Anonymous(n.Message)));

    private static string Anonymous(string sentence) =>
        TheNamesOfTheDevices.Aggregate(
            Regex.Replace(
                Regex.Replace(Regex.Replace(sentence, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", "<id>"),
                    @"low priority \(\d+\)", "low priority (<n>)")
                    .Replace("mark 'giunzione'", "mark '<mark>'", StringComparison.Ordinal),
                // IL NOME DI UNA CELLA E' UN IDENTIFICATIVO ANCHE LUI, e cancellarlo con la regola
                // dei GUID non basta: il nome che draw.io da' a una cella nata in questa sessione
                // non ha quella forma - ne' nella tabella ne' sul binario vero. Due frasi uguali
                // che nominano due celle diverse sono una frase sola.
                @"\(cell [^)]+\)", "(cell <id>)"),
            (sofar, name) => sofar.Replace($"{name}: ", "<device>: ", StringComparison.Ordinal));

    /// <summary>
    /// I nomi che la tabella da' ai suoi dispositivi, cancellati come gli identificativi: il
    /// verbale apre con il nome del dispositivo la riga che lo riguarda, e senza questo una stessa
    /// frase su due dispositivi sarebbero due frasi. Dopo gli identificativi, che sono spariti
    /// prima: quello che resta e' un nome.
    /// </summary>
    private static readonly string[] TheNamesOfTheDevices =
        [A.ToString()[..4], B.ToString()[..4], Dropped.ToString()[..4]];

    /// <summary>The identifier of every cell of a drawing, read the way <c>DrawioReader</c> reads it.</summary>
    private static List<string> ElementIdsOf(string xml) =>
        XDocument.Parse(xml).Descendants("root").First().Elements()
            .Select(element => (string?)element.Attribute("id")
                ?? (string?)element.Element("mxCell")?.Attribute("id") ?? string.Empty)
            .Where(id => id is not ("0" or "1"))
            .ToList();

    private static List<(string? Source, string? Target)> EdgesOf(string xml) =>
        XDocument.Parse(xml).Descendants("root").First().Elements()
            .Select(element => element.Name == "object" ? element.Element("mxCell") : element)
            .Where(cell => cell is not null && (cell.Attribute("source") is not null || cell.Attribute("target") is not null))
            .Select(cell => ((string?)cell!.Attribute("source"), (string?)cell.Attribute("target")))
            .ToList();

    private static PlantItemDto Device(Guid id, double x, double y) =>
        new(id, id.ToString()[..4], DeviceTypes.Automation, null, null, null, default, default, 0, null,
            0, 0, 0, 0, default!, x, y, 120, 60, 0);

    private static DeviceTypeDto Type() => new("automation", 0, "Automation",
        [new ShapePartDto("M 4 4 H 96 V 96 H 4 Z", "#334155", Filled: false, Rigid: false)],
        [new DevicePortDto("in", 4, 50, "in"), new DevicePortDto("out", 96, 50, "out")],
        new ShapePointDto(50, 50), new ShapePointDto(88, 60));
}
