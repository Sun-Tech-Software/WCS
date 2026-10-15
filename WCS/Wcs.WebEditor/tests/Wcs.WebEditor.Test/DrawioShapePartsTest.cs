using System.Text;
using System.Text.RegularExpressions;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Drawio;
using Xunit;

namespace Wcs.WebEditor.Test;

/// <summary>
/// How a type made of several parts becomes one draw.io image. The registry hands over an ordered
/// list of parts - the supplied elements are a frame, a filled gear and coloured arrows above it -
/// and this is where that list turns into <c>&lt;path&gt;</c> elements.
/// </summary>
/// <remarks>
/// Nothing here counts the seven real types or names their paths: that belongs to
/// <c>Wcs.Host.Test.DeviceShapeGoldenTest</c>, the only project that can see the registry and the
/// drawing side at once. These tests fix the RULE, on types invented for the purpose, so the rule
/// keeps holding when the real shapes are replaced.
/// </remarks>
public sealed class DrawioShapePartsTest
{
    private const string Prefix = "image=data:image/svg+xml,";

    /// <summary>
    /// The image on a SQUARE footprint, where a rigid part and an elastic one are drawn exactly
    /// alike: it is what every test about colour, order and fill wants, because none of them is
    /// about the footprint. The ones that are say their own size.
    /// </summary>
    private static string Svg(DeviceTypeDto type) => Svg(type, 100, 100);

    private static string Svg(DeviceTypeDto type, double width, double height)
    {
        var style = DrawioShape.ImageStyle(type, width, height);
        Assert.StartsWith(Prefix, style);
        return Encoding.UTF8.GetString(Convert.FromBase64String(style[Prefix.Length..]));
    }

    private static DeviceTypeDto Type(params ShapePartDto[] parts) => new(
        "composito", 42, "Composito", parts, [new DevicePortDto("in", 4, 50, "in")],
        new ShapePointDto(50, 50), new ShapePointDto(50, 50));

    private static List<string> Attribute(string svg, string name) =>
        Regex.Matches(svg, name + "=\"([^\"]*)\"").Select(m => m.Groups[1].Value).ToList();

    /// <summary>
    /// One path per part, in the declared order. The order is the drawing order: in SVG the last
    /// element wins where two overlap, so a part declared later is a part drawn on top. In the first
    /// version of the supplied shapes the arrow was declared before the gear and disappeared under
    /// it.
    /// </summary>
    [Fact]
    public void EveryPartBecomesOnePathInTheOrderItWasDeclared()
    {
        var svg = Svg(Type(
            new ShapePartDto("M 4 4 H 96 V 96 H 4 Z", "#334155", Filled: false, Rigid: false),
            new ShapePartDto("M 30 30 H 70 V 70 H 30 Z", "#1d4ed8", Filled: true, Rigid: false),
            new ShapePartDto("M 40 50 H 60", "#b91c1c", Filled: false, Rigid: false)));

        Assert.Equal(
            new[] { "M 4 4 H 96 V 96 H 4 Z", "M 30 30 H 70 V 70 H 30 Z", "M 40 50 H 60" },
            Attribute(svg, "d"));
    }

    /// <summary>
    /// A filled part is painted with its own colour; the outlined FIRST part - the body - keeps the
    /// white this surface has always painted, see <c>DrawioShape.OutlineFill</c> for why the canvas
    /// chooses otherwise. The stroke is the part's colour either way: a filled part still has its
    /// outline.
    /// </summary>
    [Fact]
    public void AFilledPartIsPaintedWithItsOwnColourAndAnOutlinedOneIsNot()
    {
        var svg = Svg(Type(
            new ShapePartDto("M 4 4 H 96 V 96 H 4 Z", "#334155", Filled: false, Rigid: false),
            new ShapePartDto("M 30 30 H 70 V 70 H 30 Z", "#1d4ed8", Filled: true, Rigid: false)));

        Assert.Equal(new[] { "#ffffff", "#1d4ed8" }, Attribute(svg, "fill"));
        Assert.Equal(new[] { "#334155", "#1d4ed8" }, Attribute(svg, "stroke"));
    }

    /// <summary>
    /// The white belongs to the body, not to "being outlined": an outlined part ABOVE the body is
    /// not painted at all, the same answer the canvas gives.
    /// <para>
    /// Measured in Chromium on the image this very method produces, before the composite shapes
    /// were drawn: with the white given to every outlined part, an outlined arrow declared over a
    /// filled gear turned the gear white underneath it - a gear tooth at (25,50) read
    /// <c>#ffffff</c> instead of <c>#334155</c>. Same shape, same code path, white only on the
    /// first part: <c>#334155</c>. That is what this test holds.
    /// </para>
    /// </summary>
    [Fact]
    public void AnOutlinedPartAboveTheBodyIsNotPaintedSoItCannotRubOutWhatIsUnderIt()
    {
        var svg = Svg(Type(
            new ShapePartDto("M 4 4 H 96 V 96 H 4 Z", "#334155", Filled: false, Rigid: false),
            new ShapePartDto("M 30 30 H 70 V 70 H 30 Z", "#334155", Filled: true, Rigid: false),
            new ShapePartDto("M 20 45 H 80 V 55 H 20 Z", "#15956b", Filled: false, Rigid: false)));

        Assert.Equal(new[] { "#ffffff", "#334155", "none" }, Attribute(svg, "fill"));
    }

    /// <summary>
    /// Each part keeps its own colour: two parts of one type are not repainted in a single colour
    /// the way they would have been when the colour belonged to the type.
    /// </summary>
    [Fact]
    public void PartsOfTheSameTypeKeepTheirOwnColours()
    {
        var svg = Svg(Type(
            new ShapePartDto("M 4 4 H 96 V 96 H 4 Z", "#334155", Filled: false, Rigid: false),
            new ShapePartDto("M 40 50 H 60", "#b91c1c", Filled: false, Rigid: false)));

        Assert.Equal(new[] { "#334155", "#b91c1c" }, Attribute(svg, "stroke"));
    }

    /// <summary>
    /// The panel entry and the device already on the canvas share one style, and that promise must
    /// survive a type with more than one part: a producer that stopped after the first part would
    /// still look right to a comparison that never sees a second one.
    /// </summary>
    [Fact]
    public void TheWholeDeviceStyleCarriesTheImageOfAllTheParts()
    {
        var type = Type(
            new ShapePartDto("M 4 4 H 96 V 96 H 4 Z", "#334155", Filled: false, Rigid: false),
            new ShapePartDto("M 40 50 H 60", "#b91c1c", Filled: false, Rigid: false));

        Assert.Contains(DrawioShape.ImageStyle(type, 122, 191), DrawioShape.DeviceStyle(type, 122, 191));
        Assert.Contains("M 40 50 H 60", Svg(type));
    }

    private static readonly ShapePartDto ElasticBody = new("M 4 4 H 96 V 96 H 4 Z", "#334155", Filled: false, Rigid: false);
    private static readonly ShapePartDto RigidGear = new("M 30 30 H 70 V 70 H 30 Z", "#1d4ed8", Filled: true, Rigid: true);

    /// <summary>
    /// The one thing draw.io does with the footprint. The image is stretched onto the cell
    /// (<c>imageAspect=0</c>), and on the real plant the cell is 122x191: an elastic part takes the
    /// stretch, which is what a frame wants, and a rigid one is shrunk inside the unit box first by
    /// exactly as much as the stretch will pull it back - so it comes out in proportion. Only the
    /// rigid part carries the attribute; the body must not be touched, or the frame would stop
    /// reaching the sides of its own device.
    /// </summary>
    [Fact]
    public void OnlyARigidPartIsFittedBackIntoProportionAndTheElasticOneIsLeftAlone()
    {
        var svg = Svg(Type(ElasticBody, RigidGear), 122, 191);

        // One transform, on the second path: the count is the assertion. Attribute() reads them in
        // document order, and an empty list would mean the body took it instead.
        Assert.Equal(new[] { "translate(50,50) scale(1,0.63874) translate(-50,-50)" }, Attribute(svg, "transform"));
        Assert.EndsWith("""<path d="M 30 30 H 70 V 70 H 30 Z" transform="translate(50,50) scale(1,0.63874) translate(-50,-50)" fill="#1d4ed8" stroke="#1d4ed8" stroke-width="3" stroke-linejoin="round"/></svg>""", svg);
    }

    /// <summary>
    /// The same on a WIDE device, so that "min of the two" is read as such and not as "the height":
    /// a rule that only ever meets tall devices is a rule that can be written backwards and still
    /// look right. 191x122 is the very same device of the test above, turned a quarter.
    /// </summary>
    [Fact]
    public void OnAWideDeviceItIsTheWIDTHThatIsGivenBack()
    {
        var svg = Svg(Type(ElasticBody, RigidGear), 191, 122);

        Assert.Equal(new[] { "translate(50,50) scale(0.63874,1) translate(-50,-50)" }, Attribute(svg, "transform"));
    }

    /// <summary>
    /// <b>The verification that makes rigid parts safe.</b> On a square footprint elastic and rigid
    /// coincide, so the image must be what it was before rigid parts existed - not "equivalent",
    /// the same bytes. Compared against the same type with every part declared elastic, which is
    /// literally the old behaviour of every part: the day the transform is written unconditionally,
    /// or the ratio is taken the wrong way round, this is the test that says so.
    /// </summary>
    [Fact]
    public void OnASquareFootprintARigidPartIsTheSameImageAsAnElasticOne()
    {
        var rigid = Type(ElasticBody, RigidGear);
        var elastic = Type(ElasticBody, RigidGear with { Rigid = false });

        foreach (var side in new double[] { 1, 80, 100, 191 })
            Assert.Equal(Svg(elastic, side, side), Svg(rigid, side, side));

        Assert.DoesNotContain("transform", Svg(rigid, 191, 191));
    }

    /// <summary>
    /// <b>The two rules about the first part are two rules.</b> "Is the first part" means "stands on
    /// the sheet", and that is what earns the sheet's white when the part is outlined
    /// (<c>DrawioShape.OutlineFill</c>); <see cref="ShapePartDto.Rigid"/> means "does not stretch
    /// with the footprint". On the seven types of today they agree part for part - the body is both
    /// the first and the only elastic one - so nothing made of those seven could tell
    /// <c>part.Rigid</c> apart from <c>index != 0</c>. This type can: its FIRST part is rigid and
    /// its second is elastic, which is the combination the seven do not have. <b>Measured</b>, with
    /// <c>index != 0</c> written in place of the flag in <c>DrawioShape</c>: this is the only red of
    /// this project (776 of 777 still green) and the golden FILE stays green - the seven shapes come
    /// out unchanged, which is exactly why they cannot tell the two rules apart. The one other red
    /// in the repository is <c>DeviceShapeGoldenTest</c>'s square check, and only through its closing
    /// "and on the real footprint it must differ" line.
    /// </summary>
    [Fact]
    public void TheFitReadsTheFlagAndTheSheetWhiteReadsThePosition()
    {
        var svg = Svg(Type(
            new ShapePartDto("M 30 30 H 70 V 70 H 30 Z", "#334155", Filled: false, Rigid: true),
            new ShapePartDto("M 4 4 H 96 V 96 H 4 Z", "#1d4ed8", Filled: true, Rigid: false)),
            122, 191);

        // The fit follows the flag: it lands on the FIRST part, the only rigid one here.
        Assert.Equal(new[] { "translate(50,50) scale(1,0.63874) translate(-50,-50)" }, Attribute(svg, "transform"));
        Assert.StartsWith("""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100"><path d="M 30 30 H 70 V 70 H 30 Z" transform=""", svg);

        // The sheet white follows the position: it lands on the first part because it is first,
        // not because it is rigid - and the second one, being filled, keeps its own colour.
        Assert.Equal(new[] { "#ffffff", "#1d4ed8" }, Attribute(svg, "fill"));
    }

    /// <summary>
    /// A device with no width or no height - a partial geometry, which the reader admits - must not
    /// produce a NaN inside the image. It falls back to the elastic drawing, the way a shape with
    /// nothing to be in proportion to has to.
    /// </summary>
    [Fact]
    public void ADegenerateFootprintFallsBackToTheElasticDrawing()
    {
        var type = Type(ElasticBody, RigidGear);

        Assert.Equal(Svg(type, 100, 100), Svg(type, 0, 191));
        Assert.Equal(Svg(type, 100, 100), Svg(type, 122, 0));
    }

    /// <summary>
    /// The OTHER half of the degenerate case, and the one that must NOT fall back. The same partial
    /// geometry reaches this class as a zero side at the axial rotations and as a NEGATIVE one at
    /// every other angle, because that is what inverting <c>NodePlacement.LocalSize</c> gives: a
    /// negative own size is a device drawn MIRRORED, not a device with nothing to be in proportion
    /// to. The sign belongs to the outer scale - the one that does the mirroring - so
    /// <c>RigidPartFit</c> takes it off with <c>Math.Abs</c> before forming the ratio, and the gear
    /// of a mirrored device is fitted exactly like the gear of the device it mirrors.
    /// <para>
    /// Without that <c>Math.Abs</c> the smaller of the two sides would be whichever one is
    /// negative, whatever its length, and the fit would come out inverted - a gear mirrored a
    /// second time, on top of the mirroring the outer scale already does.
    /// </para>
    /// </summary>
    [Fact]
    public void AMirroredFootprintIsFittedLikeTheOneItMirrors()
    {
        var type = Type(ElasticBody, RigidGear);

        Assert.Equal(Svg(type, 122, 191), Svg(type, -122, 191));
        Assert.Equal(Svg(type, 122, 191), Svg(type, 122, -191));
        Assert.Equal(Svg(type, 122, 191), Svg(type, -122, -191));
    }
}
