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
/// <b>The third question, and the one nothing was asking: no device the plant holds is deleted
/// unless the drawing asked for it - and when it is deleted, the save says so.</b>
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> The two invariants that came before it are written, both of them, about
/// LINES: one protects the operator's drawing, the other the plant's junctions. A DEVICE that goes
/// off the plant is seen by neither, and a device is worth more than a line - it carries its
/// configuration, its door map, and every junction hanging from it goes with it. The hole was
/// found by measuring, after the second batch, and this is the net for it.
/// </para>
/// <para>
/// <b>The same two rules as the other two</b>, which are the reason those two hold:
/// </para>
/// <para>
/// 1. <b>The subjects come from an input</b> - <c>current.Items</c>, the plant snapshot this test
/// builds and hands to <c>Compute</c> - and never from <c>delta.Deleted</c>, which is the answer of
/// the code under test. A delta that stops reporting a deletion does not make this test ask fewer
/// questions.
/// </para>
/// <para>
/// 2. <b>What is expected is COMPUTED</b> from the same document, read independently with
/// XDocument: the drawing asks for a device to go when it no longer carries a cell for it, under
/// ANY of the identifiers that cell can wear - the plant's, or the one draw.io gave it when it was
/// dropped in this session. Anything else and the deletion had no reason the drawing gave.
/// </para>
/// <para>
/// <b>Its own table, and not a dimension bolted onto the lines'.</b> The facts a device is judged
/// on are different ones - the mark, whether its cell is still there, which identity it wears, and
/// what another hand did to it meanwhile - and crossing them into the 3840 roads of the lines would
/// have multiplied a table that answers a different question.
/// </para>
/// </remarks>
public sealed class DrawioDeviceInvariantTest(ITestOutputHelper output)
{
    /// <summary>A device nobody touches, so that an empty plant cannot make this test green by accident.</summary>
    private static readonly Guid Untouched = Guid.Parse("11111111-1111-1111-1111-111111111111");

    /// <summary>The subject device.</summary>
    private static readonly Guid S = Guid.Parse("22222222-2222-2222-2222-222222222222");

    /// <summary>The identifier draw.io gave the subject's cell when it was dropped in this session.</summary>
    private const string DroppedCellId = "appena-lasciato-cadere";

    private const string SubjectName = "Il soggetto";

    private static readonly Dictionary<string, Guid> NoJunctionMap = [];
    private static readonly Dictionary<string, Guid> NoDeviceMap = [];

    /// <summary>What the drawing says the subject's cell is.</summary>
    private enum Mark
    {
        /// <summary>Ours, as <c>DrawioDocument.Build</c> wrote it.</summary>
        Ours,

        /// <summary>The mark taken off by hand: the cell is a plain shape of the operator's.</summary>
        Removed,

        /// <summary>A mark of ours spelled some other way.</summary>
        Mangled,

        /// <summary>A mark of a category this reader does not have.</summary>
        Foreign,
    }

    /// <summary>Whether the drawing still carries the cell at all.</summary>
    private enum Cell { OnTheCanvas, DeletedByTheOperator }

    /// <summary>Which identity the cell wears, which is the difference a reload makes.</summary>
    private enum Session
    {
        /// <summary>After a reload: the cell carries the plant's identifier.</summary>
        AfterAReload,

        /// <summary>Dropped in this session: the cell carries draw.io's, and only the map ties them.</summary>
        DroppedInThisSession,
    }

    /// <summary>What another operator did to the device while this drawing was open.</summary>
    private enum Meanwhile { NobodyTouchedIt, AnotherHandDeletedIt, AnotherHandRenamedIt }

    private sealed record Road(Mark Mark, Cell Cell, Session Session, Meanwhile Meanwhile)
    {
        public override string ToString() => $"mark={Mark} cell={Cell} session={Session} meanwhile={Meanwhile}";
    }

    private static IEnumerable<Road> AllRoads() =>
        from mark in Enum.GetValues<Mark>()
        from cell in Enum.GetValues<Cell>()
        from session in Enum.GetValues<Session>()
        from meanwhile in Enum.GetValues<Meanwhile>()
        select new Road(mark, cell, session, meanwhile);

    private static int Roads => Enum.GetValues<Mark>().Length * Enum.GetValues<Cell>().Length
        * Enum.GetValues<Session>().Length * Enum.GetValues<Meanwhile>().Length;

    [Fact]
    public void NoDeviceOfThePlantIsDeletedUnlessTheDrawingAsksAndTheSaveSaysSo()
    {
        var broken = new List<string>();
        var interrogated = 0;
        var deleted = 0;
        var takenByAnother = 0;

        foreach (var road in AllRoads())
        {
            var opened = PlantOf();
            var current = CurrentOf(road, opened);
            var xml = DrawingOf(road, opened);
            var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, current, NoJunctionMap, DeviceMapOf(road));
            var afterTheSave = PlantAfterTheSave(current, delta);

            // THE SUBJECTS ARE THE PLANT'S - the snapshot the page was OPENED with, and not the one
            // it is being saved against.
            //
            // It was 'current' until somebody counted: on the road where another operator removes
            // the device, 'current' does not carry it, so the road was walked with ONE subject
            // instead of two and the net was green on it for the wrong reason - the very shape it
            // was built to stop. 'opened' is what the page drew, so every device the operator can
            // see is asked about, whatever happened to it since.
            foreach (var item in opened.Items)
            {
                interrogated++;
                if (afterTheSave.Items.Any(alive => alive.ItemId == item.ItemId)) continue;

                var names = NamesOf(road, item.ItemId);
                var said = names.SelectMany(name => SentencesAbout(delta, name)).Distinct(StringComparer.Ordinal).ToList();

                // SOMEBODY ELSE TOOK IT, and this save is not what removed it. The shape goes off
                // the operator's canvas all the same - the plant is the truth, and since the owner's
                // decision of 27/9 at THIS save - so what the invariant asks of that road is the
                // other half of its sentence: that the save says so by name. Counted and printed, so
                // that a road that stops reaching it is visible.
                if (!current.Items.Any(now => now.ItemId == item.ItemId))
                {
                    takenByAnother++;
                    if (said.Count == 0)
                        broken.Add($"{road}: UN ALTRO HA TOLTO IL DISPOSITIVO E IL SALVATAGGIO NON HA DETTO NIENTE" +
                            $" (identificativi {string.Join(" / ", names)})");
                    continue;
                }

                deleted++;
                var stillDrawn = names.Any(name => TheDocumentCarries(xml, name));

                // The writes block names every device deletion the save asks for - "the deletion of
                // device <name>", DrawioEditor.razor - so a deletion that is in delta.Deleted is
                // already declared.
                //
                // WHICH MAKES THIS HALF AN IDENTITY HERE, and it is said instead of left to be
                // found: this test replays the host from delta.Deleted, so "the plant lost it",
                // "the save asked for it" and "the writes name it" are one fact seen three times.
                // The half that carries this net is the FIRST one - a deletion the drawing did not
                // ask for - and what can still come apart is the chain from that list to the line
                // the operator reads, which lives in a browser test and not here.
                var amongTheWrites = delta.Deleted.Contains(item.ItemId);

                if (stillDrawn)
                    broken.Add($"{road}: THE PLANT LOST A DEVICE THE DRAWING DID NOT ASK TO LOSE - il disegno porta ancora" +
                        $" la sua cella ({string.Join(" / ", names)}); il salvataggio ha detto: {Said(said)}");

                if (said.Count == 0 && !amongTheWrites)
                    broken.Add($"{road}: IL DISPOSITIVO E' STATO CANCELLATO E IL SALVATAGGIO NON HA DETTO NIENTE" +
                        $" (identificativi {string.Join(" / ", names)})");
            }

            // AND THE ROUND IS STABLE, which is the only place from which a device coming BACK can
            // be seen. The two halves above ask what the plant lost; a device that the save leaves
            // in the operator's free drawing when the plant no longer holds it is not lost - it is
            // worse: at the next save nobody recognizes that cell, and the device is CREATED again,
            // undoing in silence the deletion another operator meant. Measured: without this the
            // repair of the device arm had no net at all, and the mutation that takes it away was
            // green.
            var reopened = DrawioDocument.Build(afterTheSave, delta.DecorationXml);
            var again = DrawioDelta.Compute(DrawioReader.Read(reopened), afterTheSave, afterTheSave,
                NoJunctionMap, NoDeviceMap);

            if (again.NewDevices.Count != 0 || again.Deleted.Count != 0)
                broken.Add($"{road}: IL SALVATAGGIO DOPO, SENZA CHE NESSUNO TOCCHI NIENTE, RISCRIVE I DISPOSITIVI" +
                    $" (nuovi={again.NewDevices.Count}, cancellati={again.Deleted.Count})");
        }

        output.WriteLine($"Strade percorse: {Roads}. Dispositivi dell'impianto interrogati: {interrogated}," +
            $" di cui cancellati da questo salvataggio: {deleted}," +
            $" e tolti da un altro operatore: {takenByAnother}.");
        foreach (var line in broken) output.WriteLine(line);

        Assert.True(broken.Count == 0,
            $"{broken.Count} dispositivi persi o persi in silenzio:{Environment.NewLine}{string.Join(Environment.NewLine, broken)}");
        // A net that watched no device die would be green for the wrong reason, and so would one
        // that watched them all die.
        // ESATTI, e non "piu' di zero": una guardia larga e' il secondo modo per far smettere di
        // chiedere a una rete - si toglie una clausola ai soggetti, si interrogano meno cose e la
        // guardia non se ne accorge. Questi numeri vengono dalla tabella e cambiano solo se cambia
        // la tabella; se cambiano, si rimisurano e si riscrivono qui.
        Assert.True(interrogated == Roads * 2,
            $"Su {Roads} strade i dispositivi interrogati sono {interrogated} invece di {Roads * 2}:" +
            " la tabella non interroga piu' i due dispositivi che dichiara di avere su ogni strada.");
        Assert.True(deleted == 8 && takenByAnother == 16,
            $"Cancellati {deleted} (attesi 8) e tolti da un altro {takenByAnother} (attesi 16):" +
            " la tabella non esercita piu' le strade che dichiara.");
    }

    // ---- what a road is made of -------------------------------------------------------------

    private static PlantGraph PlantOf() => new(
        [Device(Untouched, "Intatto", 100, 100), Device(S, SubjectName, 500, 100)], [], [Type()]);

    /// <summary>The plant as it is NOW: what another operator did to the subject while the drawing was open.</summary>
    private static PlantGraph CurrentOf(Road road, PlantGraph opened) => road.Meanwhile switch
    {
        Meanwhile.AnotherHandDeletedIt =>
            new PlantGraph(opened.Items.Where(item => item.ItemId != S).ToList(), opened.Junctions, opened.Types),
        Meanwhile.AnotherHandRenamedIt =>
            new PlantGraph(opened.Items.Select(item => item.ItemId == S ? item with { Name = "Rinominato da un altro" } : item).ToList(),
                opened.Junctions, opened.Types),
        _ => opened,
    };

    private static IReadOnlyDictionary<string, Guid> DeviceMapOf(Road road) =>
        road.Session is Session.DroppedInThisSession
            ? new Dictionary<string, Guid>(StringComparer.Ordinal) { [DroppedCellId] = S }
            : NoDeviceMap;

    /// <summary>
    /// Every identifier the drawing can carry this device under - the plant's, and the one draw.io
    /// gave the cell when it was dropped in this session.
    /// </summary>
    /// <remarks>
    /// <b>Read BACKWARDS out of the map this test hands to Compute</b>, and not written out again
    /// from the road: a second copy of the rule is a copy that can drift, and this one decides
    /// whether the net asks its question at all. Measured, 2026-09-20: with the product's repair
    /// undone AND this reading narrowed to the plant's identifier alone, the whole suite is GREEN
    /// while a device goes off the plant - the exact shape this series of nets exists to stop,
    /// arriving inside a net.
    /// </remarks>
    private static string[] NamesOf(Road road, Guid itemId) =>
        DeviceMapOf(road).Where(pair => pair.Value == itemId).Select(pair => pair.Key)
            .Prepend(itemId.ToString()).ToArray();

    /// <summary>The drawing the page was opened with, with the subject's cell in the shape this road wants.</summary>
    private static string DrawingOf(Road road, PlantGraph plant)
    {
        var document = DrawioDocument.Build(plant, null);
        var written = Regex.Match(document, $"""<object id="{S}".*?</object>""", RegexOptions.Singleline);
        Assert.True(written.Success, $"Il disegno di partenza non porta il dispositivo {S}: la tabella non misura quello che dichiara.");

        var cell = written.Value;
        document = document.Remove(written.Index, written.Length);
        if (road.Cell is Cell.DeletedByTheOperator) return document;

        var id = road.Session is Session.DroppedInThisSession ? DroppedCellId : S.ToString();
        cell = cell.Replace($"""<object id="{S}" """, $"""<object id="{id}" """, StringComparison.Ordinal);

        cell = road.Mark switch
        {
            // The mark taken off by hand: what is left is the plain cell, and the identifier moves
            // onto it - which is what draw.io hands back when somebody strips the wrapper.
            Mark.Removed => Regex.Replace(cell, """^<object [^>]*>""", string.Empty)
                .Replace("</object>", string.Empty, StringComparison.Ordinal)
                .Replace("<mxCell ", $"""<mxCell id="{id}" """, StringComparison.Ordinal),
            Mark.Mangled => cell.Replace("wcs=\"device\"", "wcs=\"dispositivo\"", StringComparison.Ordinal),
            Mark.Foreign => cell.Replace("wcs=\"device\"", "wcs=\"door\"", StringComparison.Ordinal),
            _ => cell,
        };

        return document.Replace("</root>", cell + "</root>", StringComparison.Ordinal);
    }

    /// <summary>The host played back on the plant as it is NOW: the devices the save deleted are gone.</summary>
    private static PlantGraph PlantAfterTheSave(PlantGraph now, DrawioDeltaResult delta) =>
        new(now.Items.Where(item => !delta.Deleted.Contains(item.ItemId)).ToList(), now.Junctions, now.Types);

    /// <summary>Whether the document carries a cell under this identifier, read with XDocument and nothing else.</summary>
    private static bool TheDocumentCarries(string documentXml, string id) =>
        XDocument.Parse(documentXml).Descendants("root").First().Elements()
            .Any(element => string.Equals((string?)element.Attribute("id")
                ?? (string?)element.Element("mxCell")?.Attribute("id"), id, StringComparison.Ordinal));

    /// <remarks>
    /// A device somebody else deleted whose cell the drawing still carries is said by the PAGE, once
    /// the canvas has answered whether its cell went (owner's decision of 27/9, coda voce 7): the
    /// delta hands it over in <see cref="DrawioDeltaResult.GoneFromThePlant"/>, and the line is
    /// <see cref="DrawioDeviceGone.Sentence"/> - called here, not rewritten, in BOTH of its outcomes.
    /// <para>
    /// There is no filter on what the sentence says, and that is said rather than faked: a filter
    /// on the device's name, which stood here, could not fail - <c>Sentence</c> opens with that name
    /// by construction. What this half really asks is the one question the delta answers: was the
    /// device HANDED OVER. A record handed over is a line the page prints (whatever the canvas
    /// answers - <see cref="DrawioEditorDeviceGoneTest"/> reads it on the page); a device neither
    /// handed over nor named in a refusal or a notice is the silence this net exists for.
    /// </para>
    /// </remarks>
    private static IEnumerable<string> SentencesAbout(DrawioDeltaResult delta, string id) =>
        delta.Refusals.Where(r => r.Id.Contains(id, StringComparison.Ordinal)).Select(r => r.Reason)
            .Concat(delta.Notices.Where(n => n.Id.Contains(id, StringComparison.Ordinal)).Select(n => n.Message))
            .Concat(delta.GoneFromThePlant
                .Where(gone => gone.ItemId.ToString() == id || gone.DeviceCellIds.Contains(id, StringComparer.Ordinal))
                .SelectMany(gone => new[] { gone.Sentence(removedFromTheDrawing: true), gone.Sentence(removedFromTheDrawing: false) }));

    private static string Said(IReadOnlyList<string> said) => said.Count == 0 ? "NIENTE" : string.Join(" | ", said);

    private static PlantItemDto Device(Guid id, string name, double x, double y) =>
        new(id, name, DeviceTypes.Automation, null, null, null, default, default, 0, null,
            0, 0, 0, 0, default!, x, y, 120, 60, 0);

    private static DeviceTypeDto Type() => new("automation", 0, "Automation",
        [new ShapePartDto("M 4 4 H 96 V 96 H 4 Z", "#334155", Filled: false, Rigid: false)],
        [new DevicePortDto("in", 4, 50, "in"), new DevicePortDto("out", 96, 50, "out")],
        new ShapePointDto(50, 50), new ShapePointDto(88, 60));
}
