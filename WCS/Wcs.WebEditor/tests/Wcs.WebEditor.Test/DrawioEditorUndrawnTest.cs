using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Model;
using Wcs.WebEditor.Pages;
using Wcs.WebEditor.Session;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

/// <summary>
/// What the plant has and the drawing does not show, said on the draw.io page (row 16 of the parity
/// table).
/// </summary>
/// <remarks>
/// <para>
/// The rule itself - which devices are left out and why, and how many arrows go with them - is
/// measured by <c>DrawioDocumentTest</c>, where it lives. What is measured HERE is what this page
/// does with it: that the box appears at all, where it stands, what its one visible line says, and -
/// the half that decides whether the box can ever tell a lie - <b>when it is worked out again</b>.
/// </para>
/// <para>
/// The case this batch exists for is the device of type 99 (<c>DeviceTypes.None</c>): placed, sized,
/// even joined to another device, and until this batch it was on the plant and nowhere on this page.
/// </para>
/// </remarks>
public sealed class DrawioEditorUndrawnTest : PlantCanvasBunitContext
{
    private static readonly Guid Drawn = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Ghost = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Boxless = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Junction = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid SecondDrawn = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly Guid SecondJunction = Guid.Parse("88888888-8888-8888-8888-888888888888");

    private const string DrawnName = "DRAWN-DEVICE";
    private const string SecondDrawnName = "DRAWN-DEVICE-TWO";
    private const string GhostName = "GHOST-DEVICE";
    private const string BoxlessName = "BOXLESS-DEVICE";

    private static DeviceTypeDto Type() => new("automation", (short)DeviceTypes.Automation, "Automation",
        [new ShapePartDto("M 4 4 H 96 V 96 H 4 Z", "#334155", Filled: false, Rigid: false)],
        [new DevicePortDto("in", 4, 50, "in"), new DevicePortDto("out", 96, 50, "out")],
        new ShapePointDto(50, 50), new ShapePointDto(88, 60));

    private static PlantItemDto Item(Guid id, string name, DeviceTypes type, double x) =>
        new(id, name, type, null, null, null, default, default, 0, null,
            0, 0, 0, 0, default!, x, 20, 130, 184, 0);

    /// <summary>A device the plant never gave a box to: the other reason the drawing cannot show one.</summary>
    private static PlantItemDto Unplaced(Guid id, string name) =>
        new(id, name, DeviceTypes.Automation, null, null, null, default, default, 0, null,
            0, 0, 0, 0, default!, X: null, Y: null, Width: null, Height: null, Rotation: null);

    private static PlantItemConfigDto Config(Guid id, string name, DeviceTypes type) =>
        new() { ItemId = id, Name = name, DeviceType = (short)type };

    /// <summary>A plant whose every device IS drawn: the normal case, and the anchor of the test that looks for the box's absence.</summary>
    private static DrawioWriteHandler HostWithNothingHidden()
    {
        var handler = new DrawioWriteHandler { Items = [Item(Drawn, DrawnName, DeviceTypes.Automation, 10)], Types = [Type()] };
        handler.Configs[Drawn] = Config(Drawn, DrawnName, DeviceTypes.Automation);
        return handler;
    }

    /// <summary>
    /// A plant with one device of type 99 joined to a drawn one: the case that disappeared whole -
    /// the device AND its arrow.
    /// </summary>
    private static DrawioWriteHandler HostWithAGhost()
    {
        var handler = new DrawioWriteHandler
        {
            Items = [Item(Drawn, DrawnName, DeviceTypes.Automation, 10), Item(Ghost, GhostName, DeviceTypes.None, 500)],
            Junctions = [new JunctionDto(Junction, Drawn, Ghost, 99)],
            Types = [Type()],
        };
        handler.Configs[Drawn] = Config(Drawn, DrawnName, DeviceTypes.Automation);
        handler.Configs[Ghost] = Config(Ghost, GhostName, DeviceTypes.None);
        return handler;
    }

    private async Task<IRenderedComponent<DrawioEditor>> RenderWithAsync(DrawioWriteHandler handler)
    {
        var bridge = JSInterop.SetupModule("./_content/Wcs.WebEditor/drawio-bridge.js");
        // Planned even where no test expects a rename: in loose mode an unplanned call answers
        // false, which this page reads as "the canvas did not take the name" and declares.
        bridge.Setup<bool>("renameCell", _ => true).SetResult(true);

        var state = new EditorSessionState(new Uri("http://localhost:5580"), handler);
        state.UseKey("test-key");
        Services.AddSingleton(state);
        var cut = Render<DrawioEditor>();
        await cut.InvokeAsync(() => cut.Instance.OnDrawioReady());
        return cut;
    }

    private static string? SummaryOrNull(IRenderedComponent<DrawioEditor> cut) =>
        cut.FindAll("#drawio-undrawn-summary") is [var node, ..] ? node.TextContent : null;

    private static string Summary(IRenderedComponent<DrawioEditor> cut) =>
        SummaryOrNull(cut) ?? throw new Xunit.Sdk.XunitException("The page said nothing about what the drawing does not show.");

    private static string[] Lines(IRenderedComponent<DrawioEditor> cut) =>
        [.. cut.FindAll("li.drawio-undrawn-device").Select(node => node.TextContent)];

    /// <summary>
    /// THE CASE OF THE BATCH: a device of type 99, on the plant, joined to another, and this page
    /// used to say nothing at all about it. Now it is named, with its identifier, with the number of
    /// its type, and with the arrow that went away with it.
    /// </summary>
    /// <remarks>
    /// The identifier is asserted as well as the name, and it is not a detail: this device has no
    /// shape to click, so the only way to reach it is to go and look for it by identifier on the
    /// host or in the WinForms Editor.
    /// </remarks>
    [Fact]
    public async Task ADeviceOfATypeNobodyDeclaresIsNamedWithItsTypeAndWithTheArrowThatWentWithIt()
    {
        var cut = await RenderWithAsync(HostWithAGhost());

        var summary = Summary(cut);
        Assert.Contains("1 device of the plant is not in the drawing", summary);
        Assert.Contains("1 junction goes with them", summary);

        var line = Assert.Single(Lines(cut));
        Assert.Contains(GhostName, line);
        Assert.Contains(Ghost.ToString(), line);
        Assert.Contains("its device type is 99", line);
        // The remedy has to be the right one: a box would change nothing here, and saying so is the
        // difference between this line and the boxless one.
        Assert.Contains("A box would not help", line);
        Assert.DoesNotContain("its size is", line);
        // And the device that IS drawn is not in the list: a box that named everything would be read
        // once and never again.
        Assert.DoesNotContain(DrawnName, line);

        Assert.Contains("one of them is not drawn", cut.Find("#drawio-undrawn-junctions").TextContent);
    }

    /// <summary>
    /// The other reason, with the remedy that belongs to it - and with the sentence that keeps this
    /// list from being read as a second, separate fault: a device the plant gave no position to is
    /// already in "Check Junctions" as "not placed".
    /// </summary>
    [Fact]
    public async Task ADeviceWithNoBoxIsNamedWithTheOtherReasonAndWithItsOwnRemedy()
    {
        var handler = HostWithNothingHidden();
        handler.Items.Add(Unplaced(Boxless, BoxlessName));
        handler.Configs[Boxless] = Config(Boxless, BoxlessName, DeviceTypes.Automation);

        var cut = await RenderWithAsync(handler);

        var line = Assert.Single(Lines(cut));
        Assert.Contains(BoxlessName, line);
        Assert.Contains("its size is not recorded at all", line);
        Assert.Contains("Give it a box and the drawing shows it", line);
        Assert.Contains("\"Check Junctions\" calls \"not placed\"", line);
        Assert.DoesNotContain("device type", line);
        // No arrow was lost, so the line about the arrows is not there at all: "0 junctions" is a
        // clause that costs a reading and says nothing.
        Assert.DoesNotContain("junction", Summary(cut));
        Assert.Empty(cut.FindAll("#drawio-undrawn-junctions"));
    }

    /// <summary>
    /// THE DEVICE THAT SITS BETWEEN THE TWO LISTS, and the line this page got wrong the first time
    /// it was written: a device with a real position and a size of <c>0 x 0</c>. The drawing cannot
    /// show it - <c>DrawioDocument</c> asks for a WIDTH - and "Check Junctions" does NOT call it
    /// "not placed", because that check asks <c>PlantGraph.IsPlaced</c>, which reads the position
    /// alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured in the real page on 2026-09-19 before this test existed: a device seeded at
    /// (800, 150) with width 0 came out of the box saying it was among the unplaced ones, and it was
    /// not. It is the family this branch keeps having to take back - a sentence that sends its
    /// reader to a list the thing is not in - so the fact now has a test of its own.
    /// </para>
    /// <para>
    /// The two definitions are NOT unified here on purpose: they answer two different questions, and
    /// <c>JunctionChecks</c> is a shared component this batch may not touch. What is measured is that
    /// the page says which of the two the reader is looking at.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ADeviceWithAPositionAndNoSizeIsNamedAndSaidNotToBeAmongTheUnplacedOnes()
    {
        var handler = HostWithNothingHidden();
        handler.Items.Add(Item(Boxless, BoxlessName, DeviceTypes.Automation, 800) with { Width = 0, Height = 0 });
        handler.Configs[Boxless] = Config(Boxless, BoxlessName, DeviceTypes.Automation);

        var cut = await RenderWithAsync(handler);

        var line = Assert.Single(Lines(cut));
        Assert.Contains(BoxlessName, line);
        Assert.Contains("its size is 0 x 0", line);
        Assert.Contains("it does have a position, so it is NOT among the ones \"Check Junctions\" calls \"not placed\"", line);
    }

    /// <summary>
    /// ZERO IS THE NORMAL CASE AND IT IS SAID WITH SILENCE: a plant whose every device is drawn gets
    /// no box at all - not a line reading "0 devices not shown", which at every opening is the noise
    /// that teaches people to stop reading the strip.
    /// </summary>
    /// <remarks>
    /// <b>The anchor is the point of this test.</b> Looking for the absence of an element proves
    /// nothing on its own - a page that failed to render, or an identifier renamed, would pass it
    /// just as well. Two things hold it up. The render is asked for something that MUST be there
    /// (the checks box, which stands in the same strip), so a page that did not render goes red;
    /// and the SAME method is run over a plant that does have something hidden, so a rule that
    /// stopped finding anything at all goes red there. A <c>[Theory]</c> and not two halves of one
    /// test because bUnit will not render twice in one context.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheBoxIsNotThereAtAllWhenEveryDeviceIsDrawnAndIsThereWhenOneIsNot(bool hidden)
    {
        var cut = await RenderWithAsync(hidden ? HostWithAGhost() : HostWithNothingHidden());

        // The page really rendered its strip: without this, "the box is absent" would also be the
        // answer of a page that rendered nothing.
        Assert.Single(cut.FindAll("#drawio-checks"));

        if (hidden)
        {
            Assert.Single(cut.FindAll("#drawio-undrawn"));
            Assert.Contains("not in the drawing", Summary(cut));
        }
        else
        {
            Assert.Empty(cut.FindAll("#drawio-undrawn"));
            Assert.Null(SummaryOrNull(cut));
        }
    }

    /// <summary>
    /// Where the list stands, which is a decision and not an accident: since the passata grafica
    /// (29/9) in the window behind the "Details" of its line in the row of outcomes under the bar -
    /// until then a box in the band above the drawing - and NOT in the dock on the right.
    /// </summary>
    /// <remarks>
    /// The dock is driven by the SELECTION and speaks about one device - and these devices are
    /// exactly the ones that can never be selected, because there is no shape to click. A box that
    /// grew in there would both take room from the sixty properties and read as if it spoke about
    /// whatever is selected. Asserting the sentences alone would leave that free to happen.
    /// </remarks>
    [Fact]
    public async Task TheListStandsInTheWindowOfItsLineInTheRowOfOutcomesAndNotInTheConfigurationDock()
    {
        var cut = await RenderWithAsync(HostWithAGhost());

        Assert.Single(cut.FindAll("#drawio-undrawn"));
        var window = cut.Find("#drawio-undrawn").Closest(".drawio-window");
        Assert.Equal("drawio-undrawn-details", window?.Id);
        Assert.Equal("drawio-notices", window?.ParentElement?.Id);
        Assert.Empty(cut.FindAll("#drawio-property-dock #drawio-undrawn"));
        Assert.Empty(cut.FindAll("#drawio-undrawn #drawio-property-dock"));

        var scope = cut.Find("#drawio-undrawn-scope").TextContent;
        // The two facts whoever reads the list has to have before the names: nothing is being lost,
        // and there is no shape to go and click.
        Assert.Contains("the save leaves these devices alone", scope);
        Assert.Contains("There is no shape to click", scope);
    }

    /// <summary>
    /// WHEN IT IS WORKED OUT AGAIN, measured instead of promised. The plant moves under this page -
    /// here through the properties panel, which offers "None" in the <c>DeviceType</c> list - and the
    /// box follows it within the same session, without a reload.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the test that would have gone red on the shape this batch did NOT build: a list
    /// computed once at the seeding and kept in a field. It would have stayed empty here, on a plant
    /// that had just grown a device the drawing cannot show, and the page would have said nothing -
    /// which is the silence the batch exists to close, put back one layer up.
    /// </para>
    /// <para>
    /// The first half is the anchor: before the write the box is not there at all, so the appearance
    /// below is this gesture's doing and not the starting state.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task GivingADrawnDeviceTheTypeNoneMakesTheBoxAppearWithoutReloadingThePage()
    {
        var handler = HostWithNothingHidden();
        var cut = await RenderWithAsync(handler);
        Assert.Empty(cut.FindAll("#drawio-undrawn"));

        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([Drawn.ToString()]));
        Assert.NotNull(cut.Find("#drawio-property-panel"));
        cut.Find($"#drawio-property-panel div.property-panel-field[data-property='{nameof(PlantItemConfigDto.DeviceType)}'] select")
            .Change(((short)DeviceTypes.None).ToString());
        await cut.Find("#property-panel-save-button").ClickAsync(new MouseEventArgs());

        // The write really went through: without this the box's appearance could be the page
        // reacting to something else entirely.
        Assert.Equal((short)DeviceTypes.None, Assert.Single(handler.ConfigWrites).DeviceType);

        var line = Assert.Single(Lines(cut));
        Assert.Contains(DrawnName, line);
        Assert.Contains("its device type is 99", line);
        // And the sentence that keeps the box from lying about the canvas: the shape of that device
        // is still on screen, because the drawing goes out once.
        Assert.Contains("stays on the canvas until you open the page again", cut.Find("#drawio-undrawn-scope").TextContent);
    }

    /// <summary>
    /// The summary line is the one thing readable with the box SHUT, so it is the one thing that has
    /// to count properly: one of each in the singular, two of each in the plural. A strip that says
    /// "1 devices" reads as a page that does not look at what it prints.
    /// </summary>
    /// <remarks>
    /// A <c>[Theory]</c> for the same reason as the test above: bUnit renders once per context, and
    /// the two counts have to be measured on two different plants.
    /// </remarks>
    [Theory]
    [InlineData(1, "1 device of the plant is not in the drawing", "and 1 junction goes with them")]
    [InlineData(2, "2 devices of the plant are not in the drawing", "and 2 junctions go with them")]
    public async Task TheSummaryLineCountsDevicesAndArrowsAndTellsSingularFromPlural(
        int ghosts, string expectedDevices, string expectedJunctions)
    {
        var handler = HostWithAGhost();
        if (ghosts == 2)
        {
            var secondGhost = Guid.Parse("55555555-5555-5555-5555-555555555555");
            handler.Items.Add(Item(secondGhost, "GHOST-TWO", DeviceTypes.None, 900));
            handler.Configs[secondGhost] = Config(secondGhost, "GHOST-TWO", DeviceTypes.None);
            handler.Junctions.Add(new JunctionDto(Guid.Parse("66666666-6666-6666-6666-666666666666"), Drawn, secondGhost, 99));
        }

        var cut = await RenderWithAsync(handler);

        Assert.Contains(expectedDevices, Summary(cut));
        Assert.Contains(expectedJunctions, Summary(cut));
        // The names are there too, one line each: the count in the summary is a promise about what
        // opening the box shows.
        Assert.Equal(ghosts, Lines(cut).Length);
    }

    /// <summary>
    /// HALF A BOX IS NOT NO BOX. A device whose plant row carries one of the two measures and not
    /// the other is undrawn all the same - <c>WhyNotDrawn</c> asks the WIDTH - but its size is not
    /// "not recorded at all": one of the two numbers IS on the row, and a line that denied it would
    /// send its reader to look for two empty columns when only one of them is empty.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The mixed pair is a state the plant really reaches</b>, and it is not an accident of the
    /// DTO's two independent <c>double?</c>. The legacy migration produces it on purpose:
    /// <c>Wcs.Engine/src/Persistence/DiagramGeometryReader.ReadGeometry</c> keeps a
    /// <c>BoundingRect</c> from which it read one, two or three of x/y/width/height and reports it
    /// through <c>DiagramGeometryResult.PartiallyPlaced</c> ("una posizione a meta' e' comunque piu'
    /// utile di nessuna"), and <c>PlantStore.SetGeometryAsync</c> writes the five columns one by one
    /// from whatever that geometry holds. So an old drawing with a rect missing its width lands in
    /// <c>PlantItems</c> exactly like this - and <c>PUT /plant/items/{id}/geometry</c> takes the
    /// four numbers as they come too, without asking for all four.
    /// </para>
    /// <para>
    /// <b>Both mixed pairs are measured</b>, because both are branches of the sentence and a branch
    /// nothing reads is a branch that can start lying without anybody noticing - which is exactly
    /// how this one was found. The second case is a width of ZERO with no height: a recorded width
    /// still keeps the device off the drawing (<c>WhyNotDrawn</c> asks for <c>Width is &gt; 0</c>),
    /// so it reaches this line with one number on the row just like the first.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(null, 30.0, "a height of 30 and no width")]
    [InlineData(0.0, null, "a width of 0 and no height")]
    public async Task ADeviceWithOnlyOneOfItsTwoMeasuresIsNotSaidToHaveNoSizeRecordedAtAll(
        double? width, double? height, string expected)
    {
        var handler = HostWithNothingHidden();
        handler.Items.Add(Item(Boxless, BoxlessName, DeviceTypes.Automation, 800) with { Width = width, Height = height });
        handler.Configs[Boxless] = Config(Boxless, BoxlessName, DeviceTypes.Automation);

        var cut = await RenderWithAsync(handler);

        var line = Assert.Single(Lines(cut));
        Assert.Contains(BoxlessName, line);
        // The half that IS there is named, and the half that is missing is named as the missing one.
        Assert.Contains(expected, line);
        // The sentence this test exists to forbid: it declares both numbers absent while one is there.
        Assert.DoesNotContain("not recorded at all", line);
    }

    /// <summary>
    /// BOTH CAUSES ON ONE DEVICE, which is the case the remedy was split in two for - and the line
    /// the operator reads for it had no test of its own until now. A device with no box AND of a
    /// type nobody declares is not repaired by giving it a box, and the sentence says so: it names
    /// the two facts and then asks for both things at once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why it matters here and not only in <c>DrawioDocumentTest</c>.</b> That suite proves the
    /// two FLAGS come back together
    /// (<c>ADeviceThatIsBothBoxlessAndOfAnUnknownTypeCarriesBothReasons</c>); what nothing read was
    /// the line built from them, which is the whole of what reaches the operator. The remedy this
    /// checks is the very repair the batch that introduced the box was written for: each cause
    /// carrying its own remedy printed two promises, each true alone and false beside the other.
    /// </para>
    /// <para>
    /// <b>Two arrows and not one</b>, on purpose: the junctions' line has a singular branch and a
    /// plural one, and only the singular one was ever read. Both arrows are counted because both
    /// have a drawn device at one end and this one at the other.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ADeviceWithBothCausesAsksForBothAtOnceAndTakesTwoArrowsWithIt()
    {
        var handler = HostWithNothingHidden();
        handler.Items.Add(Item(SecondDrawn, SecondDrawnName, DeviceTypes.Automation, 300));
        handler.Configs[SecondDrawn] = Config(SecondDrawn, SecondDrawnName, DeviceTypes.Automation);
        handler.Items.Add(Unplaced(Ghost, GhostName) with { DeviceType = DeviceTypes.None });
        handler.Configs[Ghost] = Config(Ghost, GhostName, DeviceTypes.None);
        handler.Junctions.Add(new JunctionDto(Junction, Drawn, Ghost, 99));
        handler.Junctions.Add(new JunctionDto(SecondJunction, SecondDrawn, Ghost, 99));

        var cut = await RenderWithAsync(handler);

        // The plural branch of the summary, and of the junctions' line below it: one device, two arrows.
        var summary = Summary(cut);
        Assert.Contains("1 device of the plant is not in the drawing", summary);
        Assert.Contains("and 2 junctions go with them", summary);
        Assert.Contains("2 junctions of the plant join two devices of which at least one is not drawn",
            cut.Find("#drawio-undrawn-junctions").TextContent);

        var line = Assert.Single(Lines(cut));
        // Both facts, not one: a line that named the box alone would send its reader to place a
        // device that would still not show up afterwards.
        Assert.Contains("its size is not recorded at all", line);
        Assert.Contains("its device type is 99", line);
        // THE REMEDY OF BOTH CAUSES TOGETHER - the branch that nothing read.
        Assert.Contains("It needs both before any drawing can show it: a box, and a type the registry declares.", line);
        // And neither of the two single-cause remedies, each of which would be a promise this
        // device does not get by keeping it.
        Assert.DoesNotContain("Give it a box and the drawing shows it", line);
        Assert.DoesNotContain("A box would not help", line);
    }

    /// <summary>
    /// A device the plant has no name for is still named as something, and above all still carries
    /// its identifier - which on this list is not a nicety but the only way back to the device,
    /// since it has no shape to click.
    /// </summary>
    /// <remarks>
    /// The empty name is a state of the plant and not a shape of the test: <c>PlantItems.Name</c> is
    /// a nullable column (<c>WcsDbContext</c> gives it a length and no <c>IsRequired</c>), and the
    /// devices on this list are exactly the unfinished ones - the WinForms Editor creates a device
    /// and lets its type, and its name, be filled in afterwards. A line reading "( ... ):" with a
    /// hole where the name goes would read as a page that lost something.
    /// </remarks>
    [Fact]
    public async Task ADeviceThePlantHasNoNameForIsStillListedWithItsIdentifier()
    {
        var handler = HostWithNothingHidden();
        handler.Items.Add(Unplaced(Ghost, GhostName) with { Name = null });
        handler.Configs[Ghost] = Config(Ghost, GhostName, DeviceTypes.Automation);

        var cut = await RenderWithAsync(handler);

        var line = Assert.Single(Lines(cut));
        Assert.StartsWith($"(no name) ({Ghost})", line);
        Assert.Contains("its size is not recorded at all", line);
    }
}
