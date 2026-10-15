using System.Globalization;
using System.Text;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Drawing;
using Wcs.WebEditor.Drawio;
using Wcs.WebEditor.Model;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

public sealed class DrawioDocumentTest
{
    private static DeviceTypeDto Automation() => new(
        "automation", 0, "Automation",
        [new ShapePartDto("M 4 4 H 96 V 96 H 4 Z M 20 4 V 96 M 36 4 V 96 M 52 4 V 96 M 68 4 V 96 M 84 4 V 96", "#334155", Filled: false, Rigid: false)],
        [new DevicePortDto("in", 4, 50, "in"), new DevicePortDto("out", 96, 50, "out")],
        new ShapePointDto(50, 50), new ShapePointDto(88, 60));

    /// <summary>
    /// Il payload base64 va SUBITO dopo la virgola, senza ";base64": il punto e virgola e' il
    /// separatore degli stili di draw.io e spezzerebbe la regola in due. Con la codifica sbagliata
    /// draw.io disegna il proprio ripiego - un rettangolo - senza dire niente: e' il difetto che ha
    /// fatto sembrare "trasformati in rettangoli" i dispositivi durante la prova del 2026-09-15.
    /// </summary>
    [Fact]
    public void ShapeTravelsAsBase64RightAfterTheCommaWithoutTheBase64Marker()
    {
        // Square footprint: this test is about how the payload is encoded, not about the fit to
        // the footprint - and on a square one there is no fit to speak of anyway.
        var stile = DrawioShape.ImageStyle(Automation(), 100, 100);

        Assert.StartsWith("image=data:image/svg+xml,", stile);
        Assert.DoesNotContain(";base64", stile);

        var payload = stile["image=data:image/svg+xml,".Length..];
        var svg = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
        Assert.Contains("M 20 4 V 96", svg);   // il tracciato vero del registro, non un ripiego
        Assert.Contains("#334155", svg);       // il colore del tipo
    }

    /// <summary>
    /// Il meccanismo che rende i dispositivi "noti e gestiti": ogni cella nostra viaggia dentro un
    /// &lt;object&gt; con il marchio wcs= e il NOSTRO identificativo come id. Al ritorno e' l'unico
    /// modo per distinguere cio' che e' nostro da cio' che l'operatore ha aggiunto.
    /// </summary>
    [Fact]
    public void DevicesAndJunctionsCarryOurMarkAndOurIds()
    {
        var itemId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var otherId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var junctionId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var graph = new PlantGraph(
            [Item(itemId, "LF02.0001.0001", 100, 200, 130, 184, 35), Item(otherId, "LF03.0001.0001", 400, 200, 130, 184, 0)],
            [new JunctionDto(junctionId, itemId, otherId, 99)],
            [Automation()]);

        var xml = DrawioDocument.Build(graph, decorationXml: null);

        Assert.Contains($"""<object id="{itemId}" label="LF02.0001.0001" wcs="device">""", xml);
        // La giunzione porta anche la propria priorita' come DATO accanto al marchio (decisione
        // 3.14): "bassa" per il 99 di questa giunzione - si veda DrawioDocument.PriorityAttribute.
        Assert.Contains($"""<object id="{junctionId}" label="" wcs="junction" wcsPriority="bassa">""", xml);
        // La porta "out" sta a x=96 su 100: in draw.io le ancore sono frazioni di 0..1.
        Assert.Contains("exitX=0.96;exitY=0.5;", xml);
        Assert.Contains("entryX=0.04;entryY=0.5;", xml);
        // La rotazione vera dell'impianto, non arrotondata a zero.
        Assert.Contains("rotation=35", xml);
    }

    /// <summary>
    /// Il riquadro che va in mxGeometry e' la dimensione PROPRIA del dispositivo, non l'AABB gia'
    /// ruotato: quello lo ruoterebbe draw.io una seconda volta, ingrandendo la sagoma e spostando
    /// gli agganci delle porte, che sono frazioni del riquadro. E' la trappola che questa tappa ha
    /// gia' pagato tre volte, e senza questa prova nessuno se ne accorgerebbe - la prova sopra che
    /// guarda solo "rotation=35" passa sia col riquadro giusto sia con quello sbagliato.
    /// </summary>
    [Fact]
    public void TheBoxIsTheDevicesOwnSizeCentredOnItsWorldCentreNotTheRotatedEnvelope()
    {
        var itemId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        // Dimensione propria 80x20 a 35 gradi: l'inviluppo che l'impianto memorizza (il BoundingRect
        // gia' ruotato che Syncfusion serializza, si veda NodePlacement) e' un valore diverso, non
        // 80x20 - e' proprio quel valore diverso che DrawioDocument riceve come item.Width/Height.
        var (envelopeWidth, envelopeHeight) = NodePlacement.Envelope(80, 20, 35);
        var graph = new PlantGraph(
            [Item(itemId, "A", 100, 200, envelopeWidth, envelopeHeight, 35)], [], [Automation()]);

        var xml = DrawioDocument.Build(graph, decorationXml: null);

        var (own0Width, own0Height) = NodePlacement.LocalSize(envelopeWidth, envelopeHeight, 35);
        var (centerX, centerY) = NodePlacement.WorldCenter(100, 200, envelopeWidth, envelopeHeight);
        Assert.Contains($"width=\"{own0Width.ToString("0.##", CultureInfo.InvariantCulture)}\"", xml);
        Assert.Contains($"height=\"{own0Height.ToString("0.##", CultureInfo.InvariantCulture)}\"", xml);
        Assert.Contains($"x=\"{(centerX - own0Width / 2).ToString("0.##", CultureInfo.InvariantCulture)}\"", xml);
        // Il riquadro NON e' l'inviluppo memorizzato: se DrawioDocument scrivesse ancora
        // item.Width/Height cosi' come sono, questo valore comparirebbe - la prova sopra su
        // "rotation=35" non se ne accorgerebbe mai, perche' passa in entrambi i casi.
        Assert.DoesNotContain($"width=\"{envelopeWidth.ToString("0.##", CultureInfo.InvariantCulture)}\"", xml);
    }

    /// <summary>
    /// Una giunzione si disegna solo se ENTRAMBI i dispositivi che tocca finiscono nel disegno. Un
    /// arco verso una cella che draw.io non ha e' un riferimento rotto nel modello mxGraph; e al
    /// ritorno il delta deve poter distinguere una giunzione che l'operatore ha cancellato da una
    /// che il disegno non ha mai portato - <c>IsDrawn</c> e' la stessa domanda per l'andata e per il
    /// ritorno, ed e' l'unico comportamento che il compito del delta ha cambiato qui dentro.
    /// </summary>
    [Fact]
    public void AJunctionTowardsADeviceTheDrawingDoesNotCarryIsNotWritten()
    {
        var placedId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var unplacedId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var junctionId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var junction = new JunctionDto(junctionId, placedId, unplacedId, 99);
        // Il secondo dispositivo non e' collocato (nessun riquadro): Build non lo disegna.
        var graph = new PlantGraph(
            [Item(placedId, "A", 100, 200, 130, 184, 0), Item(unplacedId, "B", 0, 0, 0, 0, 0)],
            [junction], [Automation()]);

        var xml = DrawioDocument.Build(graph, decorationXml: null);

        Assert.False(DrawioDocument.IsDrawn(graph, junction));
        Assert.DoesNotContain($"id=\"{junctionId}\"", xml);
        Assert.DoesNotContain($"id=\"{unplacedId}\"", xml);

        // Controllo in positivo: con tutt'e due collocati la stessa giunzione si disegna.
        var both = new PlantGraph(
            [Item(placedId, "A", 100, 200, 130, 184, 0), Item(unplacedId, "B", 400, 200, 130, 184, 0)],
            [junction], [Automation()]);
        Assert.True(DrawioDocument.IsDrawn(both, junction));
        Assert.Contains($"id=\"{junctionId}\"", DrawioDocument.Build(both, decorationXml: null));
    }

    /// <summary>
    /// A junction at an in-between priority - 50, neither the 0 we write for "alta" nor the 99 we
    /// write for "bassa" - is drawn HIGH, because that is what the engine does with it:
    /// <c>RouteGraph</c> orders the edges leaving a device by ASCENDING priority
    /// (<c>WCS/Wcs.Engine/src/Routing/RouteGraph.cs:42</c>), so a 50 is explored before every 99 on
    /// the plant. One rule, <c>JunctionWeights.FromPriority</c>, and this is the test that
    /// holds the drawing to it: the earlier <c>priority == 0</c> here agreed with it on 0 and on 99
    /// - the only two values the rest of the suite ever used - and disagreed on everything in
    /// between, drawing thin and dark an arrow the engine takes first.
    /// <para>
    /// All three consequences are checked, because they are three separate writes off the same
    /// question and only the attribute is read back by <c>DrawioDelta</c>: the data
    /// (<c>wcsPriority</c>), the thickness and the colour.
    /// </para>
    /// </summary>
    [Fact]
    public void AJunctionAtAnInBetweenPriorityIsDrawnHighBecauseTheEngineExploresItFirst()
    {
        var sourceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var targetId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var junctionId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var graph = new PlantGraph(
            [Item(sourceId, "A", 100, 200, 130, 184, 0), Item(targetId, "B", 400, 200, 130, 184, 0)],
            [new JunctionDto(junctionId, sourceId, targetId, 50)], [Automation()]);

        var xml = DrawioDocument.Build(graph, decorationXml: null);

        Assert.Contains($"""<object id="{junctionId}" label="" wcs="junction" wcsPriority="alta">""", xml);
        var style = xml[xml.IndexOf($"id=\"{junctionId}\"", StringComparison.Ordinal)..];
        Assert.Contains("strokeWidth=4;", style);
        Assert.Contains("strokeColor=#b91c1c;", style);
    }

    /// <summary>
    /// The premise the mirroring refusal rests on, measured instead of assumed: a cell WE write is
    /// never mirrored, so a mirror that comes back can only be the operator's gesture. If the
    /// outward trip ever started writing a flip - to draw something the plant does hold - the
    /// refusal would start accusing the operator of a gesture he never made, at every save and on a
    /// drawing he had not touched. That is the shape of the defect the port anchors had.
    /// <para>
    /// Asked of the READER and not of the text: the device's style carries a base64 image, where the
    /// five letters of a flip could turn up by coincidence, and a test that failed for that reason
    /// would be failing for something that has nothing to do with what it claims to measure.
    /// </para>
    /// </summary>
    [Fact]
    public void NothingTheDocumentWritesIsEverMirrored()
    {
        var sourceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var targetId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var junctionId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var graph = new PlantGraph(
            [Item(sourceId, "A", 100, 200, 130, 184, 35), Item(targetId, "B", 400, 200, 130, 184, 0)],
            [new JunctionDto(junctionId, sourceId, targetId, 0)], [Automation()]);

        var contents = DrawioReader.Read(DrawioDocument.Build(graph, decorationXml: null));

        // Not a vacuous "nothing is mirrored because nothing is there": the drawing has to carry the
        // two devices and the junction first.
        Assert.Equal(2, contents.Devices.Count);
        Assert.Single(contents.Junctions);
        Assert.All(contents.Devices.Concat(contents.Junctions), cell =>
        {
            Assert.False(cell.FlipH);
            Assert.False(cell.FlipV);
        });
    }

    // ---- What the plant has and the drawing does not show (row 16 of the parity table) ------------

    /// <summary>
    /// THE CASE THAT USED TO DISAPPEAR WITHOUT A WORD: a device with a perfectly good position and
    /// size whose <c>DeviceType</c> is not among the ones the engine's registry declares -
    /// <c>DeviceTypes.None</c> (99), the value a device carries while nobody has chosen its type,
    /// which is how the WinForms Editor creates one (row 2 of the parity table).
    /// <para>
    /// The first two assertions are the red half and they are asserted on purpose: the drawing
    /// really does leave the device out, and its junction with it. Without them the rest would be a
    /// test of a list, not of a loss.
    /// </para>
    /// </summary>
    [Fact]
    public void ADeviceOfATypeTheRegistryDoesNotDeclareIsLeftOutOfTheDrawingAndNamedWithItsType()
    {
        var drawnId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var ghostId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var junctionId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var graph = new PlantGraph(
            [Item(drawnId, "DRAWN", 100, 200, 130, 184, 0), Typed(ghostId, "GHOST", DeviceTypes.None, 400, 200)],
            [new JunctionDto(junctionId, drawnId, ghostId, 99)],
            [Automation()]);

        var drawing = DrawioReader.Read(DrawioDocument.Build(graph, decorationXml: null));
        Assert.Equal([drawnId.ToString()], drawing.Devices.Select(cell => cell.Id));
        Assert.Empty(drawing.Junctions);

        var undrawn = DrawioDocument.NotDrawn(graph);

        Assert.True(undrawn.Anything);
        var device = Assert.Single(undrawn.Devices);
        Assert.Equal(ghostId, device.Item.ItemId);
        Assert.True(device.TypeNotInTheRegistry);
        // And NOT the other reason: this device has a box, and telling the operator to place it
        // would send him to do something that would change nothing.
        Assert.False(device.NoBox);
        // The arrow goes away with it, and it is counted.
        Assert.Equal(1, undrawn.Junctions);
    }

    /// <summary>
    /// The other reason, and the one that CAN be put right by placing the device: no size at all.
    /// The two are kept apart because they cost different gestures - see the list's own lines in
    /// <c>DrawioEditor</c>.
    /// </summary>
    [Fact]
    public void ADeviceWithNoBoxIsLeftOutOfTheDrawingForTheOtherReason()
    {
        var drawnId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var nowhereId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var graph = new PlantGraph(
            [Item(drawnId, "DRAWN", 100, 200, 130, 184, 0), Boxless(nowhereId, "NOWHERE")],
            [], [Automation()]);

        Assert.Equal([drawnId.ToString()],
            DrawioReader.Read(DrawioDocument.Build(graph, decorationXml: null)).Devices.Select(cell => cell.Id));

        var device = Assert.Single(DrawioDocument.NotDrawn(graph).Devices);

        Assert.Equal(nowhereId, device.Item.ItemId);
        Assert.True(device.NoBox);
        Assert.False(device.TypeNotInTheRegistry);
    }

    /// <summary>
    /// Both reasons at once, which is a real state - a device created and never typed nor placed -
    /// and the one a rule written as an <c>if/else</c> would have got wrong: it would have named the
    /// box and kept quiet about the type, and whoever placed the device would have found it still
    /// invisible with nothing left to read.
    /// </summary>
    [Fact]
    public void ADeviceThatIsBothBoxlessAndOfAnUnknownTypeCarriesBothReasons()
    {
        var lostId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var graph = new PlantGraph(
            [Item(Guid.Parse("11111111-1111-1111-1111-111111111111"), "DRAWN", 100, 200, 130, 184, 0),
             Boxless(lostId, "LOST") with { DeviceType = DeviceTypes.None }],
            [], [Automation()]);

        var device = Assert.Single(DrawioDocument.NotDrawn(graph).Devices);

        Assert.Equal(lostId, device.Item.ItemId);
        Assert.True(device.NoBox);
        Assert.True(device.TypeNotInTheRegistry);
    }

    /// <summary>
    /// The normal case: nothing is missing, so there is nothing to say. <b>A test that looks for the
    /// absence of something has to be able to fail</b>, so the same plant is asked twice - once as
    /// it is, and once with one device's type changed to a code nobody declares. If the rule ever
    /// stopped finding anything at all, the second half goes red.
    /// </summary>
    [Fact]
    public void APlantWhoseDevicesAreAllDrawnHasNothingToDeclareAndTheSamePlantWithOneBadTypeHas()
    {
        var firstId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var secondId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var junctionId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        PlantItemDto[] items = [Item(firstId, "A", 100, 200, 130, 184, 0), Item(secondId, "B", 400, 200, 130, 184, 0)];
        JunctionDto[] junctions = [new JunctionDto(junctionId, firstId, secondId, 99)];
        var whole = new PlantGraph(items, junctions, [Automation()]);

        // Not a vacuous green: the plant really carries the two devices and the arrow, and the
        // drawing really carries all three.
        var drawing = DrawioReader.Read(DrawioDocument.Build(whole, decorationXml: null));
        Assert.Equal(2, drawing.Devices.Count);
        Assert.Single(drawing.Junctions);

        var nothing = DrawioDocument.NotDrawn(whole);
        Assert.False(nothing.Anything);
        Assert.Empty(nothing.Devices);
        Assert.Equal(0, nothing.Junctions);

        var broken = new PlantGraph([items[0], items[1] with { DeviceType = DeviceTypes.None }], junctions, [Automation()]);
        var something = DrawioDocument.NotDrawn(broken);
        Assert.True(something.Anything);
        Assert.Equal(secondId, Assert.Single(something.Devices).Item.ItemId);
        Assert.Equal(1, something.Junctions);
    }

    /// <summary>
    /// The invariant the summary line leans on: a junction is counted only when a device of the list
    /// took it away. A junction hanging off an identifier the plant does not have is not drawn
    /// either - but it is a fault of its own, "Check Junctions" is what names it, and counting it
    /// here would put a number in front of the operator with no name beside it to explain it.
    /// </summary>
    [Fact]
    public void NoJunctionIsCountedWithoutTheDeviceThatTookItAway()
    {
        var firstId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var secondId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var strangerId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var graph = new PlantGraph(
            [Item(firstId, "A", 100, 200, 130, 184, 0), Item(secondId, "B", 400, 200, 130, 184, 0)],
            [new JunctionDto(Guid.Parse("33333333-3333-3333-3333-333333333333"), firstId, strangerId, 99)],
            [Automation()]);

        // The dangling junction really is out of the drawing - otherwise this would be measuring
        // nothing.
        Assert.Empty(DrawioReader.Read(DrawioDocument.Build(graph, decorationXml: null)).Junctions);
        Assert.Equal(UndrawnJunctionReason.EndNotOnThePlant,
            DrawioDocument.WhyNotDrawn(graph, graph.Junctions[0]));

        var undrawn = DrawioDocument.NotDrawn(graph);

        Assert.Empty(undrawn.Devices);
        Assert.False(undrawn.Anything);
        Assert.Equal(0, undrawn.Junctions);
    }

    /// <summary>
    /// THE ONE THAT CATCHES A DRIFT: what the drawing leaves out and what the list names are the
    /// same set, device by device, on a plant that carries every case at once. The two answers come
    /// from one rule today (<c>IsDrawn</c> reads <c>WhyNotDrawn</c>), and this is what would go red
    /// if somebody ever wrote the rule twice - the operator would then be reading a list that does
    /// not match the drawing under his eyes, which is worse than the silence this batch closed.
    /// </summary>
    [Fact]
    public void WhatTheDrawingLeavesOutIsExactlyWhatTheListNames()
    {
        var drawnId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var ghostId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var boxlessId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var bothId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var graph = new PlantGraph(
            [Item(drawnId, "DRAWN", 100, 200, 130, 184, 0),
             Typed(ghostId, "GHOST", DeviceTypes.None, 400, 200),
             Boxless(boxlessId, "BOXLESS"),
             Boxless(bothId, "BOTH") with { DeviceType = DeviceTypes.None }],
            [], [Automation()]);

        var inTheDrawing = DrawioReader.Read(DrawioDocument.Build(graph, decorationXml: null))
            .Devices.Select(cell => cell.Id).ToHashSet();
        var named = DrawioDocument.NotDrawn(graph).Devices.Select(device => device.Item.ItemId.ToString()).ToHashSet();

        // Neither set is empty, so neither half of the equality below can pass by being vacuous.
        Assert.Equal([drawnId.ToString()], inTheDrawing);
        Assert.Equal(3, named.Count);
        // Every device of the plant is in exactly one of the two, and in one of them it is.
        Assert.Empty(inTheDrawing.Intersect(named));
        Assert.Equal(
            graph.Items.Select(item => item.ItemId.ToString()).ToHashSet(),
            inTheDrawing.Union(named).ToHashSet());
    }

    // PlantItemDto e' un record posizionale senza costruttore vuoto: Asi/Partition/Subitem,
    // ConnectionType/MotionType, i contatori di cella e Live sono tutti obbligatori, non hanno un
    // valore di scorta. Lo stesso schema usato dagli altri test del progetto (si veda
    // GroupTransformTest.AnUnplacedDeviceAddedToTheSelectionDoesNotMoveTheGroupRect): default per i
    // due enum, zero per i contatori decimali/interi, null per il ricollocamento, default! per Live -
    // solo geometria e rotazione contano per questa prova.
    private static PlantItemDto Item(Guid id, string name, double x, double y, double w, double h, double rot) =>
        new(id, name, DeviceTypes.Automation, null, null, null, default, default, 0, null, 0, 0, 0, 0, default!,
            x, y, w, h, rot);

    /// <summary>A device placed and sized like any other, of a type the caller chooses: for the tests about a type the registry does not declare.</summary>
    private static PlantItemDto Typed(Guid id, string name, DeviceTypes type, double x, double y) =>
        Item(id, name, x, y, 130, 184, 0) with { DeviceType = type };

    /// <summary>A device the plant never gave a box to: X/Y/Width/Height all null, which is the state of a device created without a geometry.</summary>
    private static PlantItemDto Boxless(Guid id, string name) =>
        Item(id, name, 0, 0, 0, 0, 0) with { X = null, Y = null, Width = null, Height = null, Rotation = null };
}
