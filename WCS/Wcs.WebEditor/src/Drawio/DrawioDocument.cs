using System.Globalization;
using System.Net;
using System.Text;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Drawing;
using Wcs.WebEditor.Editing;
using Wcs.WebEditor.Model;

namespace Wcs.WebEditor.Drawio;

/// <summary>
/// A device the plant has and the drawing does not show, with the reason - or both reasons, since
/// the two can hold together.
/// </summary>
/// <param name="Item">The device itself, so whoever shows it can name it as the plant names it.</param>
/// <param name="NoBox">
/// It has no width to draw: a device the plant never gave a size to, or one whose size is zero.
/// <para>
/// <b>What it lacks is a BOX, which is not a position.</b> Everywhere else in this Editor "placed"
/// means X and Y (<c>PlantGraph.IsPlaced</c>, which is what "Check Junctions" reads to call a
/// device <i>non collocato</i>), and a device that has both of them and a width of zero is undrawn
/// just the same - measured in the real page on 2026-09-19. So the remedy is not "place it".
/// </para>
/// <para>
/// <b>And giving it a box is enough only when this is its one reason.</b> A device that also
/// carries <c>TypeNotInTheRegistry</c> gets nothing from a box: that is why the two flags are
/// asked both, and why the page works one remedy out of the pair instead of writing one inside
/// each reason.
/// </para>
/// </param>
/// <param name="TypeNotInTheRegistry">
/// Its <c>DeviceType</c> is not among the types the engine declares, so there is no shape to draw it
/// with. <c>DeviceTypes.None</c> (99) is the value a device gets when nobody has chosen its type:
/// the WinForms Editor creates a device without one and lets it be picked afterwards in the property
/// grid. Neither a box nor a position makes such a device appear - the type has to change.
/// </param>
public sealed record UndrawnDevice(PlantItemDto Item, bool NoBox, bool TypeNotInTheRegistry);

/// <summary>Why a junction of the plant is not in the drawing.</summary>
public enum UndrawnJunctionReason
{
    /// <summary>One of its two ends is not a device of the plant at all: what "Check Junctions" calls a dangling junction.</summary>
    EndNotOnThePlant,

    /// <summary>Both its ends are devices of the plant, and at least one of them is not drawn - so the arrow goes away with it.</summary>
    EndNotDrawn,
}

/// <summary>
/// What the plant carries and the drawing cannot show: see
/// <see cref="DrawioDocument.NotDrawn(PlantGraph)"/>, which is the only thing that builds one.
/// </summary>
/// <param name="Devices">Every device left out of the drawing, with its reason.</param>
/// <param name="Junctions">How many junctions go away with those devices (<see cref="UndrawnJunctionReason.EndNotDrawn"/>).</param>
public sealed record UndrawnPlant(IReadOnlyList<UndrawnDevice> Devices, int Junctions)
{
    /// <summary>
    /// Whether there is anything to say at all. It reads the devices and not the junctions, and that
    /// is not a shortcut: a junction is counted only when both its ends are devices of the plant and
    /// one of them is undrawn, so a count above zero always has a device in the list beside it -
    /// <c>NoJunctionIsCountedWithoutTheDeviceThatTookItAway</c> is what holds the two together.
    /// </summary>
    public bool Anything => Devices.Count > 0;
}

/// <summary>
/// L'andata del ponte: dall'impianto al disegno mxGraph che draw.io carica.
/// <para>
/// Dispositivi e giunzioni viaggiano dentro un &lt;object&gt; marchiato (<c>wcs="device"</c> /
/// <c>wcs="junction"</c>) con il nostro identificativo come id della cella: e' cio' che li tiene
/// "noti e gestiti" mentre il resto del disegno resta libero (decisione 3.3 e sezione 2 del
/// progetto). La decorazione, se c'e', viene innestata tale e quale: non la interpretiamo.
/// </para>
/// </summary>
public static class DrawioDocument
{
    /// <summary>
    /// L'attributo nostro che porta la priorita' di una giunzione sull'&lt;object&gt; marchiato
    /// (decisione 3.14, 2026-09-15): <b>un dato, non un aspetto</b>. Lo spessore e il colore dello
    /// stile restano - servono a chi guarda - ma sono una conseguenza, non la fonte: dedurre la
    /// priorita' dallo stile al ritorno vorrebbe dire perderla ogni volta che l'operatore cambia il
    /// colore di una freccia, e riscriverla ogni volta che la copia. draw.io conserva gli attributi
    /// dell'&lt;object&gt; attraverso spostamenti, copie e riagganci, e li mostra in "Edit Data".
    /// </summary>
    public const string PriorityAttribute = "wcsPriority";

    /// <summary>
    /// The cell every device and every junction of ours hangs from: draw.io's default layer, the one
    /// whose coordinate frame <b>is</b> the canvas. It is written by <see cref="Build"/> and read
    /// back by <c>DrawioDelta</c>, which is the whole reason it is a constant and not a literal in
    /// two places: in the mxGraph format a cell's geometry is relative to its parent, so "is this
    /// cell still on the layer we put it on?" is exactly the question that decides whether the
    /// numbers coming back mean what the plant thinks they mean.
    /// </summary>
    public const string DefaultLayerId = "1";

    /// <summary>
    /// The value of <see cref="PriorityAttribute"/> for a HIGH-weight junction - which in the WCS
    /// is a LOW number, and not only the number 0 we write ourselves: every priority below 99 is
    /// high, because the engine explores it first. The single rule is
    /// <c>JunctionWeights.FromPriority</c>; this constant is only its spelling on the drawing.
    /// </summary>
    public const string HighPriorityValue = "alta";

    /// <summary>
    /// The value of <see cref="PriorityAttribute"/> for a LOW-weight junction: 99 - the value every
    /// junction of the real plant carries today - and anything above it.
    /// </summary>
    public const string LowPriorityValue = "bassa";

    private static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    private static string E(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);

    /// <summary>
    /// The one &lt;object&gt; a placed device is drawn as - its guid as the cell's id, the wcs="device"
    /// mark, its name as the label, the type's style with its rotation, its own box centred where the
    /// plant has it. <see cref="Build"/> writes one per drawn device; the duplication (row 63, fix
    /// round 2 of compito 5) hands the same one to the bridge for the device it has just created, so a
    /// duplicate is drawn exactly as the next opening will draw it.
    /// </summary>
    internal static string DeviceCellXml(DeviceTypeDto type, PlantItemDto item)
    {
        var rotation = item.Rotation ?? 0;

        // LA TRAPPOLA, gia' pagata tre volte in questa tappa. item.Width/Height sono l'AABB
        // della sagoma GIA' RUOTATA (il BoundingRect che Syncfusion serializza), non la
        // dimensione propria del dispositivo: si veda il commento in testa a NodePlacement.
        // Passarli cosi' come riquadro di mxGeometry E mettere rotation= nello stile farebbe
        // ruotare la sagoma una SECONDA volta: draw.io disegnerebbe un inviluppo piu' grande e
        // deformato, e sposterebbe anche gli agganci delle porte, che sono frazioni di quel
        // riquadro. La composizione giusta e' la stessa che la tela SVG usa in
        // PlantCanvas.Transform: dimensione PROPRIA da LocalSize, riquadro centrato sul centro
        // del mondo da WorldCenter, e la rotazione lasciata allo stile.
        var (boxX, boxY, own0Width, own0Height) =
            NodePlacement.OwnBox(item.X ?? 0, item.Y ?? 0, item.Width ?? 0, item.Height ?? 0, rotation);

        // The style is not written here: DrawioShape.DeviceStyle produces it, the very same
        // one the shape panel gives its own entries. Only the rotation is appended here, the
        // one thing a placed device has and a panel entry has not.
        // The own dimensions just recovered travel in the style as well: they are what draw.io
        // stretches the image onto, so they are what the shape's rigid parts (the gear) have to
        // be put back in proportion against - see RigidPartFit.
        var style = DrawioShape.DeviceStyle(type, own0Width, own0Height) + $"rotation={N(rotation)};";
        return $"""<object id="{item.ItemId}" label="{E(item.Name)}" wcs="device"><mxCell style="{E(style)}" vertex="1" parent="{DefaultLayerId}"><mxGeometry x="{N(boxX)}" y="{N(boxY)}" width="{N(own0Width)}" height="{N(own0Height)}" as="geometry"/></mxCell></object>""";
    }

    public static string Build(PlantGraph graph, string? decorationXml)
    {
        // La mappa tipo-per-dispositivo esiste gia' su PlantGraph (stessa chiave DeviceTypes, stesso
        // cast dal NumericCode del registro): ricostruirla qui con una chiave diversa (short) non
        // combacerebbe piu' con PlantItemDto.DeviceType, che e' l'enum.
        var typeByCode = graph.TypesByDeviceType;
        var sb = new StringBuilder();
        sb.Append("""<mxGraphModel dx="1400" dy="900" grid="1" gridSize="10" guides="1" tooltips="1" connect="1" arrows="1" fold="0" page="0"><root><mxCell id="0"/><mxCell id="1" parent="0"/>""");

        foreach (var item in graph.Items.Where(i => IsDrawn(graph, i)))
            sb.Append(DeviceCellXml(typeByCode[item.DeviceType], item));

        foreach (var junction in graph.Junctions.Where(j => IsDrawn(graph, j)))
        {
            // IsDrawn ha gia' chiesto che i due dispositivi ci siano e che i loro tipi siano nel
            // registro: qui Anchors non puo' tornare nullo.
            var anchors = Anchors(graph, junction)!.Value;
            // One rule for "is this junction high or low", and it lives in JunctionWeights: a
            // priority the plant carries is HIGH when it is explored before the others, that is
            // when it is lower than 99 (RouteGraph orders the edges leaving a device by ASCENDING
            // priority - WCS/Wcs.Engine/src/Routing/RouteGraph.cs:42). This used to read
            // "junction.Priority == 0" instead, which agreed with FromPriority on 0 and on 99 and
            // disagreed on every value in between: a junction at 50 was drawn thin and dark while
            // the engine took it first, and the round trip - which reads wcsPriority back through
            // the SAME function - would then have seen a change the operator never made.
            var high = JunctionWeights.FromPriority(junction.Priority) == JunctionWeight.High;
            var style = "edgeStyle=none;rounded=0;html=1;endArrow=block;endFill=1;"
                      + $"exitX={N(anchors.ExitX)};exitY={N(anchors.ExitY)};exitDx=0;exitDy=0;exitPerimeter=0;"
                      + $"entryX={N(anchors.EntryX)};entryY={N(anchors.EntryY)};entryDx=0;entryDy=0;entryPerimeter=0;"
                      + $"strokeWidth={(high ? "4" : "1.5")};strokeColor={(high ? "#b91c1c" : "#1f2933")};";
            // La priorita' viaggia come DATO accanto al marchio (decisione 3.14), oltre a restare
            // visibile nello stile qui sopra: si veda PriorityAttribute per la ragione.
            var priority = high ? HighPriorityValue : LowPriorityValue;
            sb.Append($"""<object id="{junction.JunctionId}" label="" wcs="junction" {PriorityAttribute}="{priority}"><mxCell style="{E(style)}" edge="1" parent="{DefaultLayerId}" source="{junction.SourceItemId}" target="{junction.DestinationItemId}"><mxGeometry relative="1" as="geometry"/></mxCell></object>""");
        }

        if (!string.IsNullOrWhiteSpace(decorationXml)) sb.Append(decorationXml);

        sb.Append("</root></mxGraphModel>");
        return sb.ToString();
    }

    /// <summary>
    /// Vero se questo dispositivo finisce davvero nel disegno: ha un riquadro da collocare e il suo
    /// tipo e' nel registro che il motore dichiara.
    /// <para>
    /// Esiste come predicato pubblico, e non come un <c>Where</c> scritto dentro
    /// <see cref="Build"/>, perche' il ritorno (<c>DrawioDelta</c>) deve porsi la stessa domanda con
    /// la stessa risposta: un dispositivo che il disegno non ha MAI portato torna dal disegno
    /// mancante esattamente come uno che l'operatore ha cancellato. Chiedersi "manca dal disegno?"
    /// senza chiedersi prima "poteva esserci?" cancellerebbe dall'impianto ogni dispositivo non
    /// collocato, che nessuno aveva toccato.
    /// </para>
    /// <para>
    /// <b>It is a reading of <see cref="WhyNotDrawn(PlantGraph, PlantItemDto)"/>, not a second copy
    /// of the rule</b>: the page has to NAME what the drawing leaves out, and a predicate that
    /// answered yes or no beside a separate method that answered why would be two spellings of one
    /// question, and the first drift between them would give the operator a list that does not
    /// match the drawing he is looking at.
    /// </para>
    /// </summary>
    public static bool IsDrawn(PlantGraph graph, PlantItemDto item) => WhyNotDrawn(graph, item) is null;

    /// <summary>
    /// Vero se questa giunzione finisce davvero nel disegno: entrambi i dispositivi che tocca
    /// esistono e sono a loro volta disegnati. Un arco verso una cella che draw.io non ha e' un
    /// riferimento rotto, e - come per i dispositivi - il ritorno deve poter distinguere una
    /// giunzione che l'operatore ha cancellato da una che il disegno non ha mai portato.
    /// </summary>
    public static bool IsDrawn(PlantGraph graph, JunctionDto junction) => WhyNotDrawn(graph, junction) is null;

    /// <summary>
    /// Why <see cref="Build"/> leaves this device out of the drawing, or <c>null</c> when it does
    /// not leave it out. The two reasons are asked BOTH, never one after the other: a device can be
    /// without a box AND of a type nobody declares, and a list that named only the first would send
    /// whoever reads it to place a device that would still not show up afterwards.
    /// </summary>
    /// <remarks>
    /// This is the one place the rule is written. <see cref="IsDrawn(PlantGraph, PlantItemDto)"/>
    /// reads it, <see cref="Build"/> goes through that, and the notice the page shows counts what
    /// comes back from here - so the drawing and the list of what is missing from it cannot disagree.
    /// The rule itself is <see cref="WhatKeepsItOffTheDrawing"/>, which the import dialog asks too.
    /// </remarks>
    public static UndrawnDevice? WhyNotDrawn(PlantGraph graph, PlantItemDto item)
    {
        var (noBox, typeNotInTheRegistry) = WhatKeepsItOffTheDrawing(item.Width, item.DeviceType, graph.TypesByDeviceType);
        return noBox || typeNotInTheRegistry ? new UndrawnDevice(item, noBox, typeNotInTheRegistry) : null;
    }

    /// <summary>
    /// <b>The rule</b> of whether a device is drawn, on the two facts it is made of: its width and its
    /// type against the registry. Both false: it is drawn. Written ONCE, for <see cref="WhyNotDrawn(PlantGraph, PlantItemDto)"/>
    /// and for whoever holds the plant in another shape - the import dialog holds the host's
    /// <c>PlantGraphDto</c>, where the box is in a separate list - because two copies of one rule
    /// drift (last fix round of queue item 17).
    /// </summary>
    public static (bool NoBox, bool TypeNotInTheRegistry) WhatKeepsItOffTheDrawing(
        double? width, Wcs.Common.Enums.DeviceTypes deviceType, IReadOnlyDictionary<Wcs.Common.Enums.DeviceTypes, DeviceTypeDto> typesByDeviceType) =>
        (width is not > 0, !typesByDeviceType.ContainsKey(deviceType));

    /// <summary>
    /// Why <see cref="Build"/> leaves this junction out of the drawing, or <c>null</c> when it does
    /// not leave it out.
    /// </summary>
    /// <remarks>
    /// The two reasons are kept apart because they are two different faults with two different
    /// owners. <see cref="UndrawnJunctionReason.EndNotOnThePlant"/> is a junction whose end is not a
    /// device at all, which is what <c>JunctionChecks</c> calls a "giunzione scucita" and already
    /// names; <see cref="UndrawnJunctionReason.EndNotDrawn"/> is a junction whose two ends are both
    /// real devices and at least one of them is not drawn - the arrow that goes away WITH a device
    /// the drawing cannot show, which is what this batch counts.
    /// </remarks>
    public static UndrawnJunctionReason? WhyNotDrawn(PlantGraph graph, JunctionDto junction)
    {
        var source = graph.Items.FirstOrDefault(i => i.ItemId == junction.SourceItemId);
        var destination = graph.Items.FirstOrDefault(i => i.ItemId == junction.DestinationItemId);
        if (source is null || destination is null) return UndrawnJunctionReason.EndNotOnThePlant;
        return WhyNotDrawn(graph, source) is not null || WhyNotDrawn(graph, destination) is not null
            ? UndrawnJunctionReason.EndNotDrawn
            : null;
    }

    /// <summary>
    /// What the plant has and <see cref="Build"/> cannot put in the drawing: every device left out,
    /// with its reason, and how many junctions go away with them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The junctions counted are only those of
    /// <see cref="UndrawnJunctionReason.EndNotDrawn"/></b> - both ends are devices of the plant and
    /// at least one of them is not drawn. A junction hanging off an identifier the plant does not
    /// have is a different fault, it is "Check Junctions" that names it, and counting it here would
    /// put a number in front of the operator that no name in the list explains.
    /// </para>
    /// <para>
    /// It is worked out from the graph it is handed, every time: the caller decides what "now"
    /// means. Nothing is remembered between two calls, so a list computed on a plant that has since
    /// moved cannot survive it.
    /// </para>
    /// </remarks>
    public static UndrawnPlant NotDrawn(PlantGraph graph)
    {
        List<UndrawnDevice> devices = [];
        foreach (var item in graph.Items)
            if (WhyNotDrawn(graph, item) is { } undrawn)
                devices.Add(undrawn);

        var junctions = graph.Junctions.Count(
            junction => WhyNotDrawn(graph, junction) == UndrawnJunctionReason.EndNotDrawn);

        return new UndrawnPlant(devices, junctions);
    }

    /// <summary>
    /// Dove la freccia di questa giunzione si aggancia ai due dispositivi, in frazioni del loro
    /// riquadro (0..1, il formato che draw.io vuole in <c>exitX</c>/<c>entryX</c>) - nullo se uno
    /// dei due dispositivi, o il suo tipo, non e' nel grafo.
    /// <para>
    /// Esiste come metodo pubblico, e non come quattro righe dentro <see cref="Build"/>, perche' il
    /// ritorno deve poter chiedersi <b>la stessa cosa con la stessa risposta</b>: il delta confronta
    /// gli agganci letti dal disegno con quelli che l'andata aveva scritto per decidere se
    /// l'operatore abbia spostato la freccia su un'altra porta degli stessi due dispositivi - un
    /// gesto che l'impianto non sa memorizzare (<c>JunctionDto</c> non porta la porta) e che percio'
    /// va dichiarato invece di sparire. Due copie della stessa formula direbbero "cambiato" al primo
    /// scostamento fra loro.
    /// </para>
    /// </summary>
    public static (double ExitX, double ExitY, double EntryX, double EntryY)? Anchors(PlantGraph graph, JunctionDto junction)
    {
        var source = graph.Items.FirstOrDefault(i => i.ItemId == junction.SourceItemId);
        var destination = graph.Items.FirstOrDefault(i => i.ItemId == junction.DestinationItemId);
        if (source is null || destination is null) return null;
        if (!graph.TypesByDeviceType.TryGetValue(source.DeviceType, out var sourceType)) return null;
        if (!graph.TypesByDeviceType.TryGetValue(destination.DeviceType, out var destinationType)) return null;

        var outPort = Port(sourceType, "out");
        var inPort = Port(destinationType, "in");
        return (outPort.X / 100, outPort.Y / 100, inPort.X / 100, inPort.Y / 100);
    }

    private static DevicePortDto Port(DeviceTypeDto type, string direction) =>
        type.Ports.FirstOrDefault(p => p.Direction == direction)
        ?? type.Ports.FirstOrDefault(p => p.Direction == "both")
        ?? new DevicePortDto("centro", 50, 50, direction);
}
