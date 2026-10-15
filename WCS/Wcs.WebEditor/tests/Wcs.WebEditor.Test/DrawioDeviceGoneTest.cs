using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Drawio;
using Wcs.WebEditor.Model;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

/// <summary>
/// <b>A device somebody else deleted leaves the drawing at the first save</b> - the owner's decision
/// of 27/9, coda dei lavori voce 7. What the DELTA decides: which cells of the canvas are that
/// device's and its junctions', and nothing else; and what the report says about it, in the words
/// the page will print once the canvas has answered.
/// </summary>
/// <remarks>
/// Before the decision the save only said so ("deleted on the host while you were drawing, not
/// written back" for a moved one, a notice for an untouched one), the cell stayed, and from the next
/// save it was an orphan every save refused - measured on the real binary by
/// <c>DrawioThePlantChangedUnderneathTest</c>, which pinned it as it was.
/// </remarks>
public sealed class DrawioDeviceGoneTest
{
    private static readonly Guid A = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid B = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid J = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static readonly Dictionary<string, Guid> NoJunctionMap = [];
    private static readonly Dictionary<string, Guid> NoDeviceMap = [];

    private static DeviceTypeDto Type() => new("automation", 0, "Automation",
        [new ShapePartDto("M 4 4 H 96 V 96 H 4 Z", "#334155", Filled: false, Rigid: false)],
        [new DevicePortDto("in", 4, 50, "in"), new DevicePortDto("out", 96, 50, "out")],
        new ShapePointDto(50, 50), new ShapePointDto(88, 60));

    private static PlantItemDto Item(Guid id, string name, double x, double y) =>
        new(id, name, DeviceTypes.Automation, null, null, null, default, default, 0, null,
            0, 0, 0, 0, default!, x, y, 120, 60, 0);

    /// <summary>A and B joined by J: the page is opened on this.</summary>
    private static PlantGraph Opened() =>
        new([Item(A, "Gone", 100, 100), Item(B, "Stays", 500, 100)], [new JunctionDto(J, A, B, 99)], [Type()]);

    /// <summary>What another operator left: A deleted, and J with it - the host takes a device's junctions away with it (PlantStore.DeleteItemAsync).</summary>
    private static PlantGraph AfterTheOtherOperator() =>
        new([Item(B, "Stays", 500, 100)], [], [Type()]);

    [Fact]
    public void AnUntouchedDeviceDeletedOnTheHostIsHandedOverWithTheArrowOfItsJunction()
    {
        var opened = Opened();
        var drawn = DrawioReader.Read(DrawioDocument.Build(opened, null));

        var delta = DrawioDelta.Compute(drawn, opened, AfterTheOtherOperator(), NoJunctionMap, NoDeviceMap);

        var gone = Assert.Single(delta.GoneFromThePlant);
        Assert.Equal(A, gone.ItemId);
        Assert.Equal("Gone", gone.Name);
        Assert.Equal([A.ToString()], gone.DeviceCellIds);
        Assert.Equal([J.ToString()], gone.ArrowCellIds);
        Assert.Equal(1, gone.Connections);
        Assert.False(gone.ChangedInThisDrawing);

        // The page says it, once the canvas has answered: the delta says nothing of its own about
        // the device OR about its junction, and writes nothing.
        Assert.DoesNotContain(delta.Refusals, r => r.Id.Contains(A.ToString()) || r.Reason.Contains("Gone"));
        Assert.DoesNotContain(delta.Notices, n => n.Id.Contains(A.ToString()) || n.Id.Contains(J.ToString()));
        Assert.Empty(delta.Deleted);
        Assert.Empty(delta.DeletedJunctions);
        Assert.Empty(delta.Moves);
        Assert.DoesNotContain(A.ToString(), delta.DecorationXml);
        Assert.DoesNotContain(J.ToString(), delta.DecorationXml);
    }

    [Fact]
    public void ADeviceMovedHereAndDeletedOnTheHostIsHandedOverAsChangedAndNotRefusedByTheDelta()
    {
        var opened = Opened();
        var moved = new PlantGraph([Item(A, "Gone", 180, 140), Item(B, "Stays", 500, 100)], opened.Junctions, [Type()]);
        var drawn = DrawioReader.Read(DrawioDocument.Build(moved, null));

        var delta = DrawioDelta.Compute(drawn, opened, AfterTheOtherOperator(), NoJunctionMap, NoDeviceMap);

        var gone = Assert.Single(delta.GoneFromThePlant);
        Assert.True(gone.ChangedInThisDrawing);
        Assert.DoesNotContain(delta.Refusals, r => r.Reason.Contains("deleted on the host", StringComparison.Ordinal));
        Assert.Empty(delta.Moves);
    }

    [Fact]
    public void ADeviceDroppedInThisSessionAndDeletedOnTheHostIsHandedOverUnderDrawiosName()
    {
        var opened = Opened();
        var xml = DrawioDocument.Build(opened, null)
            .Replace($"""<object id="{A}" """, """<object id="drawio-7" """, StringComparison.Ordinal)
            .Replace($"""source="{A}" """, """source="drawio-7" """, StringComparison.Ordinal);
        Assert.Contains("drawio-7", xml);

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, AfterTheOtherOperator(), NoJunctionMap,
            new Dictionary<string, Guid>(StringComparer.Ordinal) { ["drawio-7"] = A });

        var gone = Assert.Single(delta.GoneFromThePlant);
        Assert.Equal(["drawio-7"], gone.DeviceCellIds);
        Assert.Equal([J.ToString()], gone.ArrowCellIds);
        Assert.DoesNotContain("drawio-7", delta.DecorationXml);
    }

    /// <summary>
    /// The arrow the operator pulled in THIS session and the save made into J: unmarked, under
    /// draw.io's identifier, known only through the session map. It is J's arrow, J went with the
    /// device, and an arrow of a device that no longer exists can never connect anything again - so it
    /// goes with it, and it does not go back into the decoration either (the next opening would draw
    /// it again from there).
    /// </summary>
    [Fact]
    public void TheArrowOfThisSessionThatBecameItsJunctionIsHandedOverAndLeavesTheDecoration()
    {
        var opened = Opened();
        var withoutTheSeededArrow = new PlantGraph(opened.Items, [], [Type()]);
        var xml = DrawioDocument.Build(withoutTheSeededArrow, null).Replace("</root>",
            $"""<mxCell id="drawio-arrow-9" style="edgeStyle=orthogonalEdgeStyle;rounded=0;html=1;exitX=1;exitY=0.5;exitDx=0;exitDy=0;entryX=0;entryY=0.5;entryDx=0;entryDy=0;" edge="1" parent="1" source="{A}" target="{B}"><mxGeometry relative="1" as="geometry"/></mxCell></root>""",
            StringComparison.Ordinal);

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, AfterTheOtherOperator(),
            new Dictionary<string, Guid>(StringComparer.Ordinal) { ["drawio-arrow-9"] = J }, NoDeviceMap);

        var gone = Assert.Single(delta.GoneFromThePlant);
        Assert.Equal(["drawio-arrow-9"], gone.ArrowCellIds);
        Assert.DoesNotContain("drawio-arrow-9", delta.DecorationXml);
        Assert.Empty(delta.NewJunctions);
        Assert.DoesNotContain(delta.Notices, n => n.Id.Contains("drawio-arrow-9") || n.Id.Contains(J.ToString()));
        Assert.DoesNotContain(delta.Refusals, r => r.Id.Contains("drawio-arrow-9"));
    }

    /// <summary>
    /// Only what the save attributes to the deleted device: the operator's note beside it, an
    /// orphan device cell the plant never knew, and the device that is still there stay where they
    /// are.
    /// </summary>
    [Fact]
    public void NothingTheSaveDoesNotAttributeToTheDeletedDeviceIsHandedOver()
    {
        var opened = Opened();
        var orphan = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var xml = DrawioDocument.Build(opened, null).Replace("</root>",
            $"""<mxCell id="note-1" value="near {A}" style="text;html=1;" vertex="1" parent="1"><mxGeometry x="100" y="300" width="80" height="20" as="geometry"/></mxCell>"""
            + $"""<object id="{orphan}" label="" wcs="device"><mxCell style="x" vertex="1" parent="1"><mxGeometry x="300" y="300" width="80" height="80" as="geometry"/></mxCell></object></root>""",
            StringComparison.Ordinal);

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, AfterTheOtherOperator(), NoJunctionMap, NoDeviceMap);

        var gone = Assert.Single(delta.GoneFromThePlant);
        Assert.Equal([A.ToString(), J.ToString()], gone.DeviceCellIds.Concat(gone.ArrowCellIds));
        Assert.Contains("note-1", delta.DecorationXml);
    }

    /// <summary>
    /// A device somebody else deleted whose cell the operator had ALREADY deleted: nothing is on the
    /// canvas to take away, so nothing is handed over, and the save's line says he had removed it too.
    /// </summary>
    [Fact]
    public void ADeviceDeletedOnTheHostWhoseCellIsNoLongerOnTheCanvasIsNotHandedOver()
    {
        var opened = Opened();
        var drawnWithoutIt = new PlantGraph([Item(B, "Stays", 500, 100)], [], [Type()]);
        var drawn = DrawioReader.Read(DrawioDocument.Build(drawnWithoutIt, null));

        var delta = DrawioDelta.Compute(drawn, opened, AfterTheOtherOperator(), NoJunctionMap, NoDeviceMap);

        Assert.Empty(delta.GoneFromThePlant);
        // Queue item 10: the line used to end "the shape on your canvas is no longer the plant's",
        // and there is no shape on the canvas - the operator had removed it himself.
        var notice = Assert.Single(delta.Notices, n => n.Id == A.ToString());
        Assert.Equal(
            "Gone: deleted on the host by somebody else while you were drawing, and the connection hanging from it went with it; you had removed it from the drawing too - nothing to do.",
            notice.Message);

        // Queue item 16: the junction that went with it has no line of its own any more. It used to
        // say "the line goes off your drawing at the next opening. To join the two devices again,
        // draw a new arrow from one to the other" - the line was already gone, and one of the two
        // devices no longer exists. The device's line, which counts the connection, is the one.
        Assert.DoesNotContain(delta.Notices, n => n.Id == J.ToString());
        Assert.Single(delta.Notices);
    }

    /// <summary>
    /// Queue item 16, the commonest way there: the operator removed the device's shape with a plain
    /// Delete, which in draw.io takes the cell and not its connections (the "delete" action calls
    /// deleteCells without edges; Ctrl+Delete is "deleteAll"). The arrow of the junction is left on
    /// the canvas, loose at the end where the device was. The junction's own line used to tell him
    /// to draw a new arrow between the two devices, and one of them no longer exists; the device's
    /// line says what happens to the arrow instead - it is ours, and it goes at the next opening.
    /// </summary>
    [Fact]
    public void TheArrowLeftLooseByADeleteOfTheDeviceIsSaidInTheDevicesLineAndNotOnALineOfItsOwn()
    {
        var opened = Opened();
        var xml = WithoutTheDevicesShape(DrawioDocument.Build(opened, null));
        Assert.Equal(1, CountOf(xml, $"""source="{A}" """));
        xml = xml.Replace($"""source="{A}" """, string.Empty, StringComparison.Ordinal)
            .Replace("""<mxGeometry relative="1" as="geometry"/>""",
                """<mxGeometry relative="1" as="geometry"><mxPoint x="220" y="130" as="sourcePoint"/></mxGeometry>""",
                StringComparison.Ordinal);

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, AfterTheOtherOperator(), NoJunctionMap, NoDeviceMap);

        Assert.Empty(delta.GoneFromThePlant);
        Assert.DoesNotContain(delta.Notices, n => n.Id == J.ToString());
        Assert.Equal(
            "Gone: deleted on the host by somebody else while you were drawing, and the connection hanging from it went with it; you had removed it from the drawing too - nothing to do."
            + " An arrow of that connection is still on your canvas: it goes off your drawing at the next opening.",
            Assert.Single(delta.Notices, n => n.Id == A.ToString()).Message);
        // And the promise holds: the arrow is not in the decoration this save stores.
        Assert.DoesNotContain(J.ToString(), delta.DecorationXml);
        Assert.Empty(delta.NewJunctions);
        Assert.Empty(delta.DeletedJunctions);
    }

    /// <summary>
    /// The same, with the arrow's mark taken off: the cell is the operator's own line now, and it
    /// stays on his canvas - so the device's line says that instead.
    /// </summary>
    [Fact]
    public void AnUnmarkedLineOfThatConnectionLeftOnTheCanvasIsSaidToStayAsFreeDrawing()
    {
        var opened = Opened();
        var xml = WithoutTheDevicesShape(DrawioDocument.Build(opened, null));
        var arrow = System.Text.RegularExpressions.Regex.Match(xml, $"""<object id="{J}".*?</object>""", System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.True(arrow.Success);
        xml = xml.Remove(arrow.Index, arrow.Length).Replace("</root>",
            $"""<mxCell id="{J}" style="endArrow=classic;html=1;" edge="1" parent="1" target="{B}"><mxGeometry relative="1" as="geometry"><mxPoint x="220" y="130" as="sourcePoint"/></mxGeometry></mxCell></root>""",
            StringComparison.Ordinal);

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, AfterTheOtherOperator(), NoJunctionMap, NoDeviceMap);

        // The line about the arrow let go in the air stays - it is about the cell, and it is true.
        Assert.DoesNotContain(delta.Notices, n => n.Id == J.ToString() && n.Message.Contains("somebody else removed this connection", StringComparison.Ordinal));
        Assert.Contains(delta.Notices, n => n.Id == J.ToString()
            && n.Message.StartsWith("one end of this arrow hangs from a device and the other from nothing", StringComparison.Ordinal));
        Assert.Equal(
            "Gone: deleted on the host by somebody else while you were drawing, and the connection hanging from it went with it; you had removed it from the drawing too - nothing to do."
            + " A line you have for that connection is still on your canvas: it stays there, as your free drawing.",
            Assert.Single(delta.Notices, n => n.Id == A.ToString()).Message);
        Assert.Contains(J.ToString(), delta.DecorationXml);
    }

    /// <summary>
    /// The arrow of that connection re-hooked, in this drawing, between two devices that are still
    /// there: this save writes it as a connection of its own (the re-hook line says so), so it is not
    /// an arrow "of that connection" any more and the device's line does not count it.
    /// </summary>
    [Fact]
    public void AnArrowOfThatConnectionReHookedBetweenTwoLivingDevicesIsNotCountedInTheDevicesLine()
    {
        var c = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var opened = new PlantGraph([Item(A, "Gone", 100, 100), Item(B, "Stays", 500, 100), Item(c, "Third", 100, 400)],
            [new JunctionDto(J, A, B, 99)], [Type()]);
        var current = new PlantGraph([Item(B, "Stays", 500, 100), Item(c, "Third", 100, 400)], [], [Type()]);
        var xml = WithoutTheDevicesShape(DrawioDocument.Build(opened, null));
        Assert.Equal(1, CountOf(xml, $"""source="{A}" """));
        xml = xml.Replace($"""source="{A}" """, $"""source="{c}" """, StringComparison.Ordinal);

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, current, NoJunctionMap, NoDeviceMap);

        var redone = Assert.Single(delta.NewJunctions);
        Assert.Equal((J.ToString(), c, B), (redone.Id, redone.Source, redone.Destination));
        Assert.Equal(
            "Gone: deleted on the host by somebody else while you were drawing, and the connection hanging from it went with it; you had removed it from the drawing too - nothing to do.",
            Assert.Single(delta.Notices, n => n.Id == A.ToString()).Message);
    }

    /// <summary>
    /// Two connections hanging from the device, both arrows left loose on the canvas: counted, once,
    /// in the device's line.
    /// </summary>
    [Fact]
    public void TwoArrowsLeftLooseAreCountedInTheDevicesLine()
    {
        var j2 = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var opened = new PlantGraph([Item(A, "Gone", 100, 100), Item(B, "Stays", 500, 100)],
            [new JunctionDto(J, A, B, 99), new JunctionDto(j2, B, A, 99)], [Type()]);
        var xml = WithoutTheDevicesShape(DrawioDocument.Build(opened, null));
        Assert.Equal(1, CountOf(xml, $"""source="{A}" """));
        Assert.Equal(1, CountOf(xml, $"""target="{A}">"""));
        xml = xml.Replace($"""source="{A}" """, string.Empty, StringComparison.Ordinal)
            .Replace($"""target="{A}">""", ">", StringComparison.Ordinal);

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, AfterTheOtherOperator(), NoJunctionMap, NoDeviceMap);

        Assert.DoesNotContain(delta.Notices, n => n.Id == J.ToString() || n.Id == j2.ToString());
        Assert.Equal(
            "Gone: deleted on the host by somebody else while you were drawing, and the connections hanging from it went with it; you had removed it from the drawing too - nothing to do."
            + " 2 arrows of those connections are still on your canvas: they go off your drawing at the next opening.",
            Assert.Single(delta.Notices, n => n.Id == A.ToString()).Message);
    }

    /// <summary>
    /// A junction another hand removed between two devices that are both still there keeps its own
    /// line, word for word: queue item 16 is only about a junction with an end among the devices
    /// somebody else deleted.
    /// </summary>
    [Fact]
    public void AJunctionAnotherHandRemovedBetweenTwoLivingDevicesKeepsItsOwnLine()
    {
        var opened = Opened();
        var drawn = DrawioReader.Read(DrawioDocument.Build(new PlantGraph(opened.Items, [], [Type()]), null));

        var delta = DrawioDelta.Compute(drawn, opened, new PlantGraph(opened.Items, [], [Type()]), NoJunctionMap, NoDeviceMap);

        Assert.Equal(
            "somebody else removed this connection from the plant while you had the drawing open: this save did not touch it, and the line goes off your drawing at the next opening. To join the two devices again, draw a new arrow from one to the other and save.",
            Assert.Single(delta.Notices, n => n.Id == J.ToString()).Message);
    }

    /// <summary>The drawing without the deleted device's shape: what the operator's own Delete leaves.</summary>
    private static string WithoutTheDevicesShape(string xml)
    {
        var shape = System.Text.RegularExpressions.Regex.Match(xml, $"""<object id="{A}".*?</object>""", System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.True(shape.Success, "The drawing does not carry the device's shape: the test does not measure what it says.");
        return xml.Remove(shape.Index, shape.Length);
    }

    /// <summary>
    /// The other story the same line tells, and the one its old words were true for: the device's
    /// cell IS still on the canvas, but not as a device of ours - its mark mangled - so the page
    /// does not take it away (it is not the device's recognized cell) and the shape the operator
    /// sees is no longer the plant's.
    /// </summary>
    [Fact]
    public void ADeviceDeletedOnTheHostWhoseCellIsStillOnTheCanvasUnderAMangledMarkSaysTheShapeIsNoLongerThePlants()
    {
        var opened = Opened();
        var xml = DrawioDocument.Build(opened, null);
        var start = xml.IndexOf($"""<object id="{A}" """, StringComparison.Ordinal);
        var end = xml.IndexOf('>', start);
        Assert.Contains("wcs=\"device\"", xml[start..end]);
        xml = xml[..start] + xml[start..end].Replace("wcs=\"device\"", "wcs=\"devise\"", StringComparison.Ordinal) + xml[end..];

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, AfterTheOtherOperator(), NoJunctionMap, NoDeviceMap);

        Assert.Empty(delta.GoneFromThePlant);
        var notice = Assert.Single(delta.Notices, n => n.Id == A.ToString());
        Assert.Equal(
            "Gone: somebody else removed this device from the plant while you had the drawing open, and the connection hanging from it went with it: this save did not touch it, and the shape on your canvas is no longer the plant's."
            + " An arrow of that connection is still on your canvas: it goes off your drawing at the next opening.",
            notice.Message);
        // Queue item 16: nor a line of its own for the junction, which told him to draw a new arrow
        // between the two devices - and the plant has only one of them.
        Assert.DoesNotContain(delta.Notices, n => n.Id == J.ToString());
        Assert.DoesNotContain(J.ToString(), delta.DecorationXml);
    }

    /// <summary>
    /// Queue item 12: the operator's own line hanging from the deleted device. The page takes the
    /// device off the canvas and draw.io disconnects the line there - its end stays where the device
    /// was - but the decoration the SAME save stores was worked out before that removal, and it
    /// stored the line still hooked to the device's identifier. Measured on the real binary
    /// (<c>DrawioThePlantChangedUnderneathTest</c>): at the next opening draw.io drops the hook to a
    /// cell that does not exist, the end is left with no cell and no point, and the line is in the
    /// model but not drawn at all. So the stored line lets go of the device the same way the canvas
    /// does: no <c>target</c>, and a loose end at the device's centre - draw.io's own choice when it
    /// disconnects a line from a cell it has not drawn.
    /// </summary>
    [Fact]
    public void TheOperatorsLineOnTheDeletedDeviceIsStoredLooseWhereTheDeviceWasNotHookedToIt()
    {
        var opened = Opened();
        var xml = DrawioDocument.Build(opened, null).Replace("</root>",
            """<mxCell id="note-1" value="a note" style="shape=note;" vertex="1" parent="1"><mxGeometry x="100" y="400" width="80" height="40" as="geometry"/></mxCell>"""
            + $"""<mxCell id="line-1" value="" style="endArrow=none;html=1;" edge="1" parent="1" source="note-1" target="{A}"><mxGeometry relative="1" as="geometry"/></mxCell></root>""",
            StringComparison.Ordinal);
        var device = Assert.Single(DrawioReader.Read(xml).Devices, cell => cell.Id == A.ToString());
        Assert.Equal((100d, 100d, 120d, 60d), (device.X, device.Y, device.Width, device.Height));

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, AfterTheOtherOperator(), NoJunctionMap, NoDeviceMap);

        Assert.Equal(1, Assert.Single(delta.GoneFromThePlant).LooseLines);
        var stored = System.Xml.Linq.XElement.Parse("<root>" + delta.DecorationXml + "</root>");
        var line = Assert.Single(stored.Descendants("mxCell"), cell => (string?)cell.Attribute("id") == "line-1");
        Assert.Equal("note-1", (string?)line.Attribute("source"));
        Assert.Null(line.Attribute("target"));
        Assert.DoesNotContain(A.ToString(), delta.DecorationXml);
        var loose = Assert.Single(line.Element("mxGeometry")!.Elements("mxPoint"), point => (string?)point.Attribute("as") == "targetPoint");
        Assert.Equal(("160", "130"), ((string?)loose.Attribute("x"), (string?)loose.Attribute("y")));
        // Nothing else of the line changes: its style, its value and the note it starts from.
        Assert.Equal("endArrow=none;html=1;", (string?)line.Attribute("style"));
        Assert.Contains("note-1", delta.DecorationXml);
    }

    /// <summary>
    /// Queue item 18: the operator's line hooked onto the ARROW of the junction that leaves with the
    /// device (line on line). Measured on the real binary before the remedy
    /// (<c>DrawioThePlantChangedUnderneathTest</c>): the stored copy kept <c>target</c> on the arrow,
    /// and at the next opening the line was in the model but not drawn. It lets go the way the
    /// canvas does: no hook, and the loose end where the line met the arrow - the middle of the
    /// arrow's box, which for a line on a line is draw.io's own point (measured: arrow from 275,930 to
    /// 565,930, the line's end at 420,930 before and after the page takes the arrow off).
    /// </summary>
    [Fact]
    public void TheOperatorsLineOnTheArrowThatLeavesIsStoredLooseInTheMiddleOfTheArrow()
    {
        var opened = Opened();
        var xml = DrawioDocument.Build(opened, null).Replace("</root>",
            """<mxCell id="note-1" value="a note" style="shape=note;" vertex="1" parent="1"><mxGeometry x="100" y="400" width="80" height="40" as="geometry"/></mxCell>"""
            + $"""<mxCell id="line-1" value="" style="endArrow=none;html=1;" edge="1" parent="1" source="note-1" target="{J}"><mxGeometry relative="1" as="geometry"/></mxCell></root>""",
            StringComparison.Ordinal);

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, AfterTheOtherOperator(), NoJunctionMap, NoDeviceMap);

        Assert.Equal([J.ToString()], Assert.Single(delta.GoneFromThePlant).ArrowCellIds);
        var stored = System.Xml.Linq.XElement.Parse("<root>" + delta.DecorationXml + "</root>");
        var line = Assert.Single(stored.Descendants("mxCell"), cell => (string?)cell.Attribute("id") == "line-1");
        Assert.Equal("note-1", (string?)line.Attribute("source"));
        Assert.Null(line.Attribute("target"));
        Assert.DoesNotContain(J.ToString(), delta.DecorationXml);
        // The arrow runs from A's out port (96,50 of 120x60 at 100,100: 215.2,130) to B's in port
        // (4,50 at 500,100: 504.8,130): its middle is 360,130.
        var loose = Assert.Single(line.Element("mxGeometry")!.Elements("mxPoint"), point => (string?)point.Attribute("as") == "targetPoint");
        Assert.Equal(("360", "130"), ((string?)loose.Attribute("x"), (string?)loose.Attribute("y")));
        Assert.Equal("endArrow=none;html=1;", (string?)line.Attribute("style"));
    }

    /// <summary>
    /// The same with the deleted device ROTATED a quarter turn: its out port turns with it, as
    /// draw.io turns a connection point with its shape, and the middle of the arrow moves with it.
    /// </summary>
    [Fact]
    public void TheMiddleOfTheArrowFollowsTheRotationOfTheDeviceItLeavesFrom()
    {
        var turned = Item(A, "Gone", 100, 100) with { Rotation = 90 };
        var opened = new PlantGraph([turned, Item(B, "Stays", 500, 100)], [new JunctionDto(J, A, B, 99)], [Type()]);
        var xml = DrawioDocument.Build(opened, null).Replace("</root>",
            """<mxCell id="note-1" value="a note" style="shape=note;" vertex="1" parent="1"><mxGeometry x="100" y="400" width="80" height="40" as="geometry"/></mxCell>"""
            + $"""<mxCell id="line-1" value="" style="endArrow=none;html=1;" edge="1" parent="1" source="{J}" target="note-1"><mxGeometry relative="1" as="geometry"/></mxCell></root>""",
            StringComparison.Ordinal);
        // The plant's box is the envelope of the turned shape: the cell's own box is 60 by 120,
        // centred on the same point (160,130).
        var own = Assert.Single(DrawioReader.Read(xml).Devices, device => device.Id == A.ToString());
        Assert.Equal((130d, 70d, 60d, 120d, 90d), (own.X, own.Y, own.Width, own.Height, own.Rotation));

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, AfterTheOtherOperator(), NoJunctionMap, NoDeviceMap);

        var stored = System.Xml.Linq.XElement.Parse("<root>" + delta.DecorationXml + "</root>");
        var line = Assert.Single(stored.Descendants("mxCell"), cell => (string?)cell.Attribute("id") == "line-1");
        Assert.Null(line.Attribute("source"));
        Assert.Equal("note-1", (string?)line.Attribute("target"));
        // A's out port, at 96% of its own 60 width, is 27.6 to the right of the centre; turned
        // clockwise a quarter it is 27.6 below it: 160,157.6. With B's in port at 504.8,130 the
        // arrow's box is 160..504.8 by 130..157.6: middle 332.4,143.8.
        var loose = Assert.Single(line.Element("mxGeometry")!.Elements("mxPoint"), point => (string?)point.Attribute("as") == "sourcePoint");
        Assert.Equal(("332.4", "143.8"), ((string?)loose.Attribute("x"), (string?)loose.Attribute("y")));
    }

    /// <summary>
    /// Queue item 19: the operator's line hooked onto the arrow of a junction another hand removed
    /// between two devices that are both STILL THERE. The arrow is ours, so it is not in the stored
    /// copy and goes off the drawing at the next opening (AnotherHandTookItAndTheCellWasOurs) - and,
    /// measured on the real binary before the remedy, the stored line kept <c>target</c> on it and
    /// was not drawn at the next opening. It lets go as item 18's does, in the middle of the arrow.
    /// </summary>
    [Fact]
    public void TheOperatorsLineOnAnArrowWhoseJunctionAnotherHandRemovedIsStoredLooseInItsMiddle()
    {
        var opened = Opened();
        var bothStillThere = new PlantGraph(opened.Items, [], [Type()]);
        var xml = DrawioDocument.Build(opened, null).Replace("</root>",
            """<mxCell id="note-1" value="a note" style="shape=note;" vertex="1" parent="1"><mxGeometry x="100" y="400" width="80" height="40" as="geometry"/></mxCell>"""
            + $"""<mxCell id="line-1" value="" style="endArrow=none;html=1;" edge="1" parent="1" source="note-1" target="{J}"><mxGeometry relative="1" as="geometry"/></mxCell></root>""",
            StringComparison.Ordinal);

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, bothStillThere, NoJunctionMap, NoDeviceMap);

        Assert.Empty(delta.GoneFromThePlant);
        Assert.Empty(delta.NewJunctions);
        var stored = System.Xml.Linq.XElement.Parse("<root>" + delta.DecorationXml + "</root>");
        var line = Assert.Single(stored.Descendants("mxCell"), cell => (string?)cell.Attribute("id") == "line-1");
        Assert.Equal("note-1", (string?)line.Attribute("source"));
        Assert.Null(line.Attribute("target"));
        Assert.DoesNotContain(J.ToString(), delta.DecorationXml);
        // From A's out port (215.2,130) to B's in port (504.8,130): the middle is 360,130.
        var loose = Assert.Single(line.Element("mxGeometry")!.Elements("mxPoint"), point => (string?)point.Attribute("as") == "targetPoint");
        Assert.Equal(("360", "130"), ((string?)loose.Attribute("x"), (string?)loose.Attribute("y")));
        Assert.Equal("endArrow=none;html=1;", (string?)line.Attribute("style"));
    }

    /// <summary>
    /// Queue item 19, the other road: the arrow a plain Delete left loose (queue item 16). The
    /// operator took A's shape off with Delete - draw.io leaves the arrow, its source end a point
    /// where it met A - hooked a line onto it, and somebody else deleted A. The arrow goes at the next
    /// opening; the line lets go of it in the middle of what is left of it, the stored point to B's
    /// port.
    /// </summary>
    [Fact]
    public void TheOperatorsLineOnTheArrowADeleteLeftLooseIsStoredLooseInItsMiddle()
    {
        var opened = Opened();
        var xml = DrawioDocument.Build(opened, null);
        var start = xml.IndexOf($"""<object id="{A}" """, StringComparison.Ordinal);
        var end = xml.IndexOf("</object>", start, StringComparison.Ordinal) + "</object>".Length;
        xml = xml[..start] + xml[end..];
        Assert.DoesNotContain($"""<object id="{A}" """, xml);
        Assert.Equal(1, CountOf(xml, $"""source="{A}" """));
        xml = xml
            .Replace($"""source="{A}" """, string.Empty, StringComparison.Ordinal)
            .Replace("""<mxGeometry relative="1" as="geometry"/></mxCell></object>""",
                """<mxGeometry relative="1" as="geometry"><mxPoint x="215.2" y="130" as="sourcePoint"/></mxGeometry></mxCell></object>""",
                StringComparison.Ordinal)
            .Replace("</root>",
                """<mxCell id="note-1" value="a note" style="shape=note;" vertex="1" parent="1"><mxGeometry x="100" y="400" width="80" height="40" as="geometry"/></mxCell>"""
                + $"""<mxCell id="line-1" value="" style="endArrow=none;html=1;" edge="1" parent="1" source="note-1" target="{J}"><mxGeometry relative="1" as="geometry"/></mxCell></root>""",
                StringComparison.Ordinal);
        Assert.Equal(1, CountOf(xml, "as=\"sourcePoint\""));

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, AfterTheOtherOperator(), NoJunctionMap, NoDeviceMap);

        Assert.Empty(delta.GoneFromThePlant);
        var stored = System.Xml.Linq.XElement.Parse("<root>" + delta.DecorationXml + "</root>");
        var line = Assert.Single(stored.Descendants("mxCell"), cell => (string?)cell.Attribute("id") == "line-1");
        Assert.Equal("note-1", (string?)line.Attribute("source"));
        Assert.Null(line.Attribute("target"));
        Assert.DoesNotContain(J.ToString(), delta.DecorationXml);
        // From the stored point (215.2,130) to B's in port (504.8,130): the middle is 360,130.
        var loose = Assert.Single(line.Element("mxGeometry")!.Elements("mxPoint"), point => (string?)point.Attribute("as") == "targetPoint");
        Assert.Equal(("360", "130"), ((string?)loose.Attribute("x"), (string?)loose.Attribute("y")));
    }

    [Fact]
    public void RemovedAndUntouchedIsANoticeThatSaysItLeftTheDrawingWithItsArrow()
    {
        var gone = new DrawioDeviceGone(A, "Gone", [A.ToString()], [J.ToString()], Connections: 1, ChangedInThisDrawing: false);

        Assert.False(gone.IsRefusal(removedFromTheDrawing: true));
        Assert.Equal(
            "Gone: deleted on the host by somebody else while you were drawing, and the connection hanging from it went with it - removed from the drawing too, together with its arrow.",
            gone.Sentence(removedFromTheDrawing: true));
    }

    /// <summary>
    /// Fix round, MINOR 1: the operator's changes to a device that no longer exists are lost, and
    /// nothing is left for him to do or to lose - so it is a NOTICE, and the round can count as
    /// complete (the "Leave site?" mark goes down) when everything else was written.
    /// </summary>
    [Fact]
    public void RemovedAfterTheOperatorChangedItIsANoticeThatSaysTheChangesAreLost()
    {
        var gone = new DrawioDeviceGone(A, "Gone", [A.ToString()], [], Connections: 0, ChangedInThisDrawing: true);

        Assert.False(gone.IsRefusal(removedFromTheDrawing: true));
        Assert.Equal(
            "Gone: deleted on the host by somebody else while you were drawing - removed from the drawing too. What you changed on it in this drawing is lost, because the device no longer exists.",
            gone.Sentence(removedFromTheDrawing: true));
    }

    [Fact]
    public void ARePrioritisedArrowAndTheLooseLinesAreSaidInTheDevicesLine()
    {
        var gone = new DrawioDeviceGone(A, "Gone", [A.ToString()], [J.ToString()], Connections: 1, ChangedInThisDrawing: false,
            ReweightedArrows: 1, LooseLines: 2);

        Assert.False(gone.IsRefusal(removedFromTheDrawing: true));
        Assert.Equal(
            "Gone: deleted on the host by somebody else while you were drawing, and the connection hanging from it went with it - removed from the drawing too, together with its arrow."
            + " The priority you changed on its arrow was not written: that connection no longer exists."
            + " 2 lines of yours that hung from it stay on the canvas, loose at one end.",
            gone.Sentence(removedFromTheDrawing: true));
    }

    /// <summary>
    /// Fix round, IMPORTANT 1: the arrow of a junction of the deleted device that the operator had
    /// re-hooked, in this drawing, between two devices that still EXIST is not that device's any
    /// more - it is a connection he drew. It stays, and the re-hook path writes it.
    /// </summary>
    [Fact]
    public void AnArrowOfItsJunctionReHookedBetweenTwoLivingDevicesIsWrittenNotRemoved()
    {
        var c = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var opened = new PlantGraph([Item(A, "Gone", 100, 100), Item(B, "Stays", 500, 100), Item(c, "Third", 100, 400)],
            [new JunctionDto(J, A, B, 99)], [Type()]);
        var current = new PlantGraph([Item(B, "Stays", 500, 100), Item(c, "Third", 100, 400)], [], [Type()]);
        var xml = DrawioDocument.Build(opened, null);
        Assert.Equal(1, CountOf(xml, $"""source="{A}" """));
        xml = xml.Replace($"""source="{A}" """, $"""source="{c}" """, StringComparison.Ordinal);

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, current, NoJunctionMap, NoDeviceMap);

        var gone = Assert.Single(delta.GoneFromThePlant);
        Assert.Empty(gone.ArrowCellIds);
        var redone = Assert.Single(delta.NewJunctions);
        Assert.Equal((c, B), (redone.Source, redone.Destination));
        Assert.Equal(J.ToString(), redone.Id);
    }

    /// <summary>
    /// Fix round, IMPORTANT 2: a new arrow to a device somebody else deleted is refused BY NAME
    /// before any write - it used to be proposed, and the host's refusal stopped the save halfway.
    /// </summary>
    [Fact]
    public void ANewArrowToTheDeletedDeviceIsRefusedByNameAndNotProposed()
    {
        var opened = new PlantGraph([Item(A, "Gone", 100, 100), Item(B, "Stays", 500, 100)], [], [Type()]);
        var current = new PlantGraph([Item(B, "Stays", 500, 100)], [], [Type()]);
        var xml = DrawioDocument.Build(opened, null).Replace("</root>",
            $"""<mxCell id="drawio-5" style="edgeStyle=orthogonalEdgeStyle;rounded=0;html=1;exitX=1;exitY=0.5;exitDx=0;exitDy=0;entryX=0;entryY=0.5;entryDx=0;entryDy=0;" edge="1" parent="1" source="{B}" target="{A}"><mxGeometry relative="1" as="geometry"/></mxCell></root>""",
            StringComparison.Ordinal);

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, current, NoJunctionMap, NoDeviceMap);

        Assert.Empty(delta.NewJunctions);
        var refusal = Assert.Single(delta.Refusals, r => r.Id == "drawio-5");
        Assert.Contains("joins Gone, which somebody else deleted from the plant while you were drawing", refusal.Reason);
        // It is the operator's own line: it stays, and the device's line counts it as loose.
        Assert.Contains("drawio-5", delta.DecorationXml);
        Assert.Equal(1, Assert.Single(delta.GoneFromThePlant).LooseLines);
    }

    /// <summary>
    /// Fix round, IMPORTANT 2, the worse half: a junction that still exists (B -> C), re-hooked
    /// onto the deleted device, used to be planned as "delete it and create B -> Gone" - the
    /// deletion went through, the creation was refused, and the connection was lost. The re-hook is
    /// refused by name instead, and the junction stays.
    /// </summary>
    [Fact]
    public void ALivingJunctionReHookedOntoTheDeletedDeviceIsNotDeleted()
    {
        var c = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var j2 = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var opened = new PlantGraph([Item(A, "Gone", 100, 100), Item(B, "Stays", 500, 100), Item(c, "Third", 100, 400)],
            [new JunctionDto(j2, B, c, 99)], [Type()]);
        var current = new PlantGraph([Item(B, "Stays", 500, 100), Item(c, "Third", 100, 400)],
            [new JunctionDto(j2, B, c, 99)], [Type()]);
        var xml = DrawioDocument.Build(opened, null);
        Assert.Equal(1, CountOf(xml, $"""target="{c}">"""));
        xml = xml.Replace($"""target="{c}">""", $"""target="{A}">""", StringComparison.Ordinal);

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, current, NoJunctionMap, NoDeviceMap);

        Assert.DoesNotContain(j2, delta.DeletedJunctions);
        Assert.Empty(delta.NewJunctions);
        var refusal = Assert.Single(delta.Refusals, r => r.Id == j2.ToString());
        Assert.Contains("joins Gone, which somebody else deleted from the plant while you were drawing", refusal.Reason);
        Assert.Equal(1, Assert.Single(delta.GoneFromThePlant).LooseLines);
    }

    /// <summary>
    /// Fix round, MINOR 2: the priority changed on the arrow of a junction that went with the device
    /// used to get its own line ("junction already gone ... its priority was not changed"); now the
    /// arrow leaves with the device, and the device's line has to say it.
    /// </summary>
    [Fact]
    public void APriorityChangedOnTheArrowOfItsJunctionIsCounted()
    {
        var opened = Opened();
        var xml = DrawioDocument.Build(opened, null);
        var low = $"""{DrawioDocument.PriorityAttribute}="{DrawioDocument.LowPriorityValue}">""";
        Assert.Equal(1, CountOf(xml, low));
        xml = xml.Replace(low, $"""{DrawioDocument.PriorityAttribute}="{DrawioDocument.HighPriorityValue}">""", StringComparison.Ordinal);

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, AfterTheOtherOperator(), NoJunctionMap, NoDeviceMap);

        var gone = Assert.Single(delta.GoneFromThePlant);
        Assert.Equal([J.ToString()], gone.ArrowCellIds);
        Assert.Equal(1, gone.ReweightedArrows);
        Assert.Empty(delta.JunctionPriorities);
    }

    private static int CountOf(string text, string what)
    {
        var count = 0;
        for (var at = text.IndexOf(what, StringComparison.Ordinal); at >= 0; at = text.IndexOf(what, at + 1, StringComparison.Ordinal))
            count++;
        return count;
    }

    [Fact]
    public void NotRemovedIsARefusalThatSaysDeleteItByHand()
    {
        var gone = new DrawioDeviceGone(A, "Gone", [A.ToString()], ["x", "y"], Connections: 2, ChangedInThisDrawing: false);

        Assert.True(gone.IsRefusal(removedFromTheDrawing: false));
        Assert.Equal(
            "Gone: deleted on the host by somebody else while you were drawing, and the 2 connections hanging from it went with it - it could not be removed from the drawing: delete it by hand, with its 2 arrows. Until then every save refuses its shape as a device the plant does not know.",
            gone.Sentence(removedFromTheDrawing: false));
    }
}
