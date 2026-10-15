using Xunit;
using Xunit.Abstractions;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// What the tests of the page's look (passata grafica, 2026-09-29) read the same way: the bar's buttons
/// that open a window with the names an operator reads on them, what one line of the bar is, and the
/// palette's colours as the browser computes them. One copy, for the classes that each had their own
/// (DrawioLookBrowserTest and DrawioTopBarBrowserTest, DrawioChecksTest and DrawioImportExportTest)
/// until the final fix round - and the two copies of the plain button's check had already drifted.
/// </summary>
public static class DrawioLook
{
    /// <summary>Below this many px two edges count as the same edge: the browser's coordinates are fractional.</summary>
    public const double SamePixel = 0.5;

    /// <summary>
    /// How far apart the vertical middles of the bar's children may be and still be one line. The
    /// middles, not the tops: the children are centred on the line and are not all as tall - measured
    /// on 29/9, the title's text starts 4.2 px below the buttons on a bar that is one line (a pill 3 px,
    /// the separator 2 px), while every middle is the same to the hundredth. A second line is a whole
    /// button (28 px) and the gap lower.
    /// </summary>
    public const double OneLine = 4;

    /// <summary>The bar's buttons that open a window, in the bar's order, with the names an operator reads on them.</summary>
    public static readonly (string Id, string Name)[] WindowButtons =
    [
        ("drawio-checks-toggle", "Plant checks"),
        ("drawio-plant-file-toggle", "Plant file"),
        ("drawio-operations-toggle", "Operations"),
        ("drawio-device-list-toggle", "Devices"),
        ("drawio-junction-list-toggle", "Junctions"),
        ("drawio-mission-list-toggle", "Missions"),
        ("drawio-plc-connections-toggle", "PLC connections"),
    ];

    /// <summary>
    /// The shown children of the bar are one line, whatever the bar's height at rest was: their middles
    /// are on one height, and none is taller than one line of the bar - the Rulers switch, an icon button
    /// of a fixed 28 px that nothing can wrap. The reference is not the bar's tallest button: its only
    /// buttons are the ones that open a window, and one whose word ran onto a second line inside it
    /// would be the reference it is measured against. A piece whose words run onto a second line of its
    /// own - as the engine's refusal did inside the bar in a first draft of compito 2 - stays centred on
    /// the line, and only its height tells.
    /// </summary>
    public static void AssertOneLine(DrawioBarLine line, string when)
    {
        static double Middle(DrawioBarChild c) => (c.Top + c.Bottom) / 2;
        Assert.True(line.Children.Length > 0, $"{when}: the bar shows nothing ({line}).");
        var highest = line.Children.MinBy(Middle)!;
        var lowest = line.Children.MaxBy(Middle)!;
        Assert.True(Middle(lowest) - Middle(highest) <= OneLine,
            FormattableString.Invariant($"{when}: the bar is not one line - the middle of {lowest} is {Middle(lowest) - Middle(highest):0.##} px below that of {highest} ({line})."));

        var rulers = line.Children.SingleOrDefault(c => c.Key == "drawio-ruler-switch")
            ?? throw new Xunit.Sdk.XunitException($"{when}: the bar shows no Rulers switch, the height one line of it is measured by ({line}).");
        var oneLine = rulers.Bottom - rulers.Top;
        var tallest = line.Children.MaxBy(c => c.Bottom - c.Top)!;
        Assert.True(tallest.Bottom - tallest.Top <= oneLine + SamePixel,
            FormattableString.Invariant($"{when}: the bar is not one line - {tallest} is {tallest.Bottom - tallest.Top:0.##} px tall, one line of the bar (the Rulers switch) {oneLine:0.##} ({line})."));
    }

    // The palette's colours as the browser computes them (wcs-webeditor.css, :root).
    public const string Surface = "rgb(255, 255, 255)";        // --editor-surface
    public const string StrongBorder = "rgb(161, 159, 157)";   // --editor-border-strong
    public const string Grey = "rgb(243, 242, 241)";           // --editor-ribbon-bg, a button under the mouse
    public const string Veil = "rgb(250, 249, 248)";           // --editor-bg, every other row of a table
    // Every other row of a paged grid (Devices, Junctions): --editor-ribbon-bg, rgb(243, 242, 241),
    // mixed 52% with transparent - the same channels over 255, at an alpha of 0.52.
    public const string ZebraStripe = "color(srgb 0.952941 0.94902 0.945098 / 0.52)";
    public const string PressedGrey = "rgb(237, 235, 233)";    // --editor-pressed-bg
    public const string Text = "rgb(32, 31, 30)";              // --editor-text
    public const string Blue = "rgb(0, 120, 212)";             // --editor-accent
    public const string BlueHover = "rgb(16, 110, 190)";       // --editor-accent-hover
    public const string BluePressed = "rgb(0, 90, 158)";       // --editor-accent-active
    public const string LightBlue = "rgb(222, 236, 249)";      // --editor-accent-bg, a button lit
    public const string Red = "rgb(185, 28, 28)";              // --editor-danger
    public const string RedHover = "rgb(153, 27, 27)";         // --editor-danger-hover
    public const string RedPressed = "rgb(127, 29, 29)";       // --editor-danger-active
    public const string White = "rgb(255, 255, 255)";          // --editor-text-on-accent

    /// <summary>
    /// The page's plain button, as the browser paints it at rest, under the mouse and pressed: corners of
    /// 6 px, the strong border, white, grey under the mouse and a darker grey pressed, in the text's colour
    /// and its normal weight (the coloured buttons' text is bold).
    /// </summary>
    public static void AssertThePagesPlainButton(DrawioButtonLook look, ITestOutputHelper output)
    {
        output.WriteLine(look.ToString());
        Assert.True(look is { Corners: "6px", Border: StrongBorder, Weight: "400", Rest: Surface, RestText: Text, Hover: Grey, Pressed: PressedGrey, PressedText: Text },
            $"Not the page's plain button: {look}");
    }
}
