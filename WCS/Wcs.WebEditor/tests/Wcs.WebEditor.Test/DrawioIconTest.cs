using Bunit;
using Wcs.WebEditor.Drawio.Components;
using Xunit;

namespace Wcs.WebEditor.Test;

/// <summary>
/// The icons of the draw.io page (passata grafica, 2026-09-29): one small SVG of our own per name,
/// decorative - the control around it carries the words - and drawn with the text's colour. A name
/// the component does not know breaks the render instead of drawing nothing: an icon missing from
/// a button would otherwise pass every test and show an empty square.
/// </summary>
public sealed class DrawioIconTest : BunitContext
{
    [Theory]
    [InlineData("plant-file")] [InlineData("operations")] [InlineData("devices")] [InlineData("junctions")]
    [InlineData("missions")] [InlineData("plc")] [InlineData("checks")] [InlineData("rulers")]
    [InlineData("help")] [InlineData("close")] [InlineData("filter")] [InlineData("play")]
    [InlineData("stop")] [InlineData("live")] [InlineData("lock")] [InlineData("details")]
    [InlineData("pause")] [InlineData("export")] [InlineData("import")]
    public void EveryNamedIconIsADecorativeSvgWithAPath(string name)
    {
        var svg = Render<DrawioIcon>(p => p.Add(i => i.Name, name)).Find("svg");
        Assert.Equal("true", svg.GetAttribute("aria-hidden"));
        Assert.NotEmpty(svg.QuerySelectorAll("path, circle, rect, line, polyline"));
        // Which icon it is, for the tests of the controls that wear one.
        Assert.Equal(name, svg.GetAttribute("data-icon"));
    }

    [Fact]
    public void AnUnknownNameBreaksInsteadOfDrawingNothing() =>
        Assert.Throws<ArgumentException>(() => Render<DrawioIcon>(p => p.Add(i => i.Name, "no-such-icon")));
}
