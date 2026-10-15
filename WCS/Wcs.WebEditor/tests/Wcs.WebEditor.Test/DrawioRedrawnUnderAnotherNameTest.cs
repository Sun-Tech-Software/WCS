using System.Xml.Linq;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Drawio;
using Wcs.WebEditor.Model;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

/// <summary>
/// Final review, C7 (a residual of queue item 19): an operator's line hooked onto a cell that the plant
/// draws again at the next opening under ANOTHER identifier than the one it has on the canvas. The
/// stored copy of the line used to keep the canvas's identifier, and at the next opening it hung from a
/// cell that is not there - in the model and not drawn, item 12's mechanism. Two cases: the identifier is
/// not known yet - an arrow this save writes as a new junction, which the host names - and the line lets
/// go in the middle of the arrow, as item 19's does; or it is known - the session maps say which device
/// and which junction a cell of this session is - and the line is stored hooked onto it.
/// </summary>
public sealed class DrawioRedrawnUnderAnotherNameTest
{
    private static readonly Guid A = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid B = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid C = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid J = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static readonly Dictionary<string, Guid> NoJunctionMap = [];
    private static readonly Dictionary<string, Guid> NoDeviceMap = [];

    private const string Note =
        """<mxCell id="note-1" value="a note" style="shape=note;" vertex="1" parent="1"><mxGeometry x="100" y="400" width="80" height="40" as="geometry"/></mxCell>""";

    private static DeviceTypeDto Type() => new("automation", 0, "Automation",
        [new ShapePartDto("M 4 4 H 96 V 96 H 4 Z", "#334155", Filled: false, Rigid: false)],
        [new DevicePortDto("in", 4, 50, "in"), new DevicePortDto("out", 96, 50, "out")],
        new ShapePointDto(50, 50), new ShapePointDto(88, 60));

    private static PlantItemDto Item(Guid id, string name, double x, double y) =>
        new(id, name, DeviceTypes.Automation, null, null, null, default, default, 0, null,
            0, 0, 0, 0, default!, x, y, 120, 60, 0);

    private static string Line(string target) =>
        $"""<mxCell id="line-1" value="" style="endArrow=none;html=1;" edge="1" parent="1" source="note-1" target="{target}"><mxGeometry relative="1" as="geometry"/></mxCell>""";

    private static XElement StoredLine(DrawioDeltaResult delta) =>
        Assert.Single(XElement.Parse("<root>" + delta.DecorationXml + "</root>").Descendants("mxCell"),
            cell => (string?)cell.Attribute("id") == "line-1");

    private static (string? X, string? Y) LooseEnd(XElement line, string point)
    {
        var loose = Assert.Single(line.Element("mxGeometry")!.Elements("mxPoint"), p => (string?)p.Attribute("as") == point);
        return ((string?)loose.Attribute("x"), (string?)loose.Attribute("y"));
    }

    /// <summary>
    /// An arrow drawn in this session between A and B, never saved: this save writes it as a new
    /// junction, and the host gives that junction an identifier nobody knows yet. The line lets go of
    /// it in the middle of the arrow, where the canvas has the two meet.
    /// </summary>
    [Fact]
    public void ALineOnAnArrowThisSaveWritesAsANewJunctionIsStoredLooseInItsMiddle()
    {
        var plant = new PlantGraph([Item(A, "A", 100, 100), Item(B, "B", 500, 100)], [], [Type()]);
        var xml = DrawioDocument.Build(plant, null).Replace("</root>",
            $"""<mxCell id="drawio-arrow-9" style="edgeStyle=orthogonalEdgeStyle;rounded=0;html=1;exitX=1;exitY=0.5;exitDx=0;exitDy=0;entryX=0;entryY=0.5;entryDx=0;entryDy=0;" edge="1" parent="1" source="{A}" target="{B}"><mxGeometry relative="1" as="geometry"/></mxCell>"""
            + Note + Line("drawio-arrow-9") + "</root>",
            StringComparison.Ordinal);

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), plant, plant, NoJunctionMap, NoDeviceMap);

        Assert.Equal("drawio-arrow-9", Assert.Single(delta.NewJunctions).Id);
        var line = StoredLine(delta);
        Assert.Equal("note-1", (string?)line.Attribute("source"));
        Assert.Null(line.Attribute("target"));
        Assert.DoesNotContain("drawio-arrow-9", delta.DecorationXml);
        // From A's right side (220,130) to B's left side (500,130): the middle is 360,130.
        Assert.Equal(("360", "130"), LooseEnd(line, "targetPoint"));
        Assert.Equal("endArrow=none;html=1;", (string?)line.Attribute("style"));
    }

    /// <summary>
    /// The case the review named: the arrow of J, whose junction another hand removed, re-hooked by
    /// the operator from B to C - this save creates the connection again, with a new identifier
    /// (queue item 11). The line hooked onto J lets go in the middle of the arrow as it is now.
    /// </summary>
    [Fact]
    public void ALineOnAnArrowReHookedAfterAnotherHandRemovedItsJunctionIsStoredLooseInItsMiddle()
    {
        var opened = new PlantGraph([Item(A, "A", 100, 100), Item(B, "B", 500, 100), Item(C, "C", 500, 300)],
            [new JunctionDto(J, A, B, 99)], [Type()]);
        var withoutJ = new PlantGraph(opened.Items, [], [Type()]);
        var built = DrawioDocument.Build(opened, null);
        Assert.Equal(1, Count(built, $"""target="{B}">"""));
        var xml = built.Replace($"""target="{B}">""", $"""target="{C}">""", StringComparison.Ordinal)
            .Replace("</root>", Note + Line(J.ToString()) + "</root>", StringComparison.Ordinal);

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, withoutJ, NoJunctionMap, NoDeviceMap);

        var written = Assert.Single(delta.NewJunctions);
        Assert.Equal((J.ToString(), A, C), (written.Id, written.Source, written.Destination));
        var line = StoredLine(delta);
        Assert.Null(line.Attribute("target"));
        Assert.DoesNotContain(J.ToString(), delta.DecorationXml);
        // From A's out port (96,50 of 120x60 at 100,100: 215.2,130) to C's in port (4,50 at 500,300:
        // 504.8,330): the middle is 360,230.
        Assert.Equal(("360", "230"), LooseEnd(line, "targetPoint"));
    }

    /// <summary>
    /// An arrow an earlier save of this session made into J: on the canvas it is still draw.io's
    /// "drawio-arrow-9", the session map says it is J, and the next opening draws it as J. The line
    /// hooked onto it is stored hooked onto J, and nothing else of it changes.
    /// </summary>
    [Fact]
    public void ALineOnAnArrowAnEarlierSaveMadeIntoAJunctionIsStoredHookedOntoTheJunction()
    {
        var plant = new PlantGraph([Item(A, "A", 100, 100), Item(B, "B", 500, 100)], [new JunctionDto(J, A, B, 99)], [Type()]);
        var withoutTheSeededArrow = new PlantGraph(plant.Items, [], [Type()]);
        var xml = DrawioDocument.Build(withoutTheSeededArrow, null).Replace("</root>",
            $"""<mxCell id="drawio-arrow-9" style="edgeStyle=orthogonalEdgeStyle;rounded=0;html=1;exitX=1;exitY=0.5;exitDx=0;exitDy=0;entryX=0;entryY=0.5;entryDx=0;entryDy=0;" edge="1" parent="1" source="{A}" target="{B}"><mxGeometry relative="1" as="geometry"/></mxCell>"""
            + Note + Line("drawio-arrow-9") + "</root>",
            StringComparison.Ordinal);

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), plant, plant,
            new Dictionary<string, Guid>(StringComparer.Ordinal) { ["drawio-arrow-9"] = J }, NoDeviceMap);

        Assert.Empty(delta.NewJunctions);
        Assert.Empty(delta.DeletedJunctions);
        var line = StoredLine(delta);
        Assert.Equal(("note-1", J.ToString()), ((string?)line.Attribute("source"), (string?)line.Attribute("target")));
        Assert.Empty(line.Element("mxGeometry")!.Elements("mxPoint"));
        Assert.Equal("endArrow=none;html=1;", (string?)line.Attribute("style"));
        Assert.DoesNotContain("drawio-arrow-9", delta.DecorationXml);
    }

    /// <summary>
    /// A device dropped in this session: its cell is draw.io's "drawio-7", the session map says it is A,
    /// and the next opening draws it as A. The line hooked onto it is stored hooked onto A.
    /// </summary>
    [Fact]
    public void ALineOnADeviceDroppedInThisSessionIsStoredHookedOntoTheDevice()
    {
        var plant = new PlantGraph([Item(A, "A", 100, 100), Item(B, "B", 500, 100)], [], [Type()]);
        var xml = DrawioDocument.Build(plant, null)
            .Replace($"""<object id="{A}" """, """<object id="drawio-7" """, StringComparison.Ordinal)
            .Replace("</root>", Note + Line("drawio-7") + "</root>", StringComparison.Ordinal);
        Assert.DoesNotContain($"""id="{A}" """, xml);

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), plant, plant, NoJunctionMap,
            new Dictionary<string, Guid>(StringComparer.Ordinal) { ["drawio-7"] = A });

        Assert.Empty(delta.Deleted);
        var line = StoredLine(delta);
        Assert.Equal(("note-1", A.ToString()), ((string?)line.Attribute("source"), (string?)line.Attribute("target")));
        Assert.Empty(line.Element("mxGeometry")!.Elements("mxPoint"));
        Assert.DoesNotContain("drawio-7", delta.DecorationXml);
    }

    private static int Count(string text, string what)
    {
        var count = 0;
        for (var at = text.IndexOf(what, StringComparison.Ordinal); at >= 0; at = text.IndexOf(what, at + what.Length, StringComparison.Ordinal)) count++;
        return count;
    }
}
