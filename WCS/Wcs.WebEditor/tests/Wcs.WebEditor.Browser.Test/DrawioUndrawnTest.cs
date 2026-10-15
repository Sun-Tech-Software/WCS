using Wcs.Api.Client;
using Wcs.Api.Contracts.Dtos;
using Xunit;
using Xunit.Abstractions;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// What the plant has and the drawing does not show (row 16 of the parity table), judged in a real
/// browser against a real host: the device really is on the plant, draw.io really does not draw it,
/// and the page really says so.
/// </summary>
/// <remarks>
/// <para>
/// <b>What bUnit cannot answer, and this can.</b> <c>DrawioEditorUndrawnTest</c> measures what the
/// page renders from the rule; nothing there knows whether draw.io, given that document, really ends
/// up with one cell and not two. Here the canvas's own model is read (<c>MarkedCellIdsAsync</c>) on
/// the same plant the box is talking about, so the box's claim and the canvas are checked against
/// each other rather than each against itself.
/// </para>
/// <para>
/// <b>The plant needs a seed of its own</b>: <c>PlantSeed.ResetAsync</c> lays down two devices that
/// are both placed and both of a type the registry declares, so on the plant the other classes use
/// there is nothing at all to declare. The hole is latent, so it has to be dug on purpose.
/// </para>
/// </remarks>
public sealed class DrawioUndrawnTest(DrawioEditorFixture fixture, ITestOutputHelper output) : IClassFixture<DrawioEditorFixture>
{
    private const string SeedHolder = "browser-test-undrawn";

    /// <summary>
    /// A device placed, sized, and of type 99 (<c>DeviceTypes.None</c>) - the value a device carries
    /// while nobody has chosen its type. Everything about it is fine except the one thing the drawing
    /// needs.
    /// </summary>
    private static readonly Guid GhostId = new("a1b2c3d4-0000-4000-8000-0000000000a1");

    private const string GhostName = "GhostDevice";

    private static readonly PlantItemGeometryDto GhostGeometry =
        new(GhostId, X: 900, Y: 150, Width: 120, Height: 60, Rotation: 0);

    /// <summary>
    /// <b>The device is on the plant, the canvas does not carry it, and the page names it.</b> Three
    /// facts read from three different places, which is the whole point: the plant from the host, the
    /// cells from draw.io's own model, and the sentence from the page.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A junction is seeded too, from one of the two drawn devices TO the ghost: an arrow that
    /// reaches an undrawn device is not drawn either, and without the count the operator would see a
    /// plant poorer than the one he has with nothing saying why.
    /// </para>
    /// <para>
    /// The count of cells is asserted as an exact number and not as "the ghost is missing": a canvas
    /// that had lost every device would satisfy the weaker claim, and this test would then be green
    /// on a page that shows nothing at all.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ADeviceOfTypeNinetyNineIsOnThePlantIsNotOnTheCanvasAndThePageNamesIt()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);
        await SeedTheGhostAsync(client);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);

        // The plant really holds three devices and one junction - the fact the rest of this test is
        // about, read from the host and not assumed from the seeding code.
        var items = await client.GetItemsAsync();
        Assert.Equal(3, items.Count);
        Assert.Contains(items, item => item.ItemId == GhostId && item.DeviceType == DeviceTypes.None);
        Assert.Single(await client.GetJunctionsAsync());

        // And the canvas carries TWO of them. Read out of draw.io's own model: this is the half no
        // bUnit test can reach.
        var onTheCanvas = await editor.MarkedCellIdsAsync();
        Assert.Equal(2, onTheCanvas.Length);
        Assert.DoesNotContain(GhostId.ToString(), onTheCanvas);

        // The page says so, with the box shut: one line, which is what the strip costs when nothing
        // is opened.
        var summary = await editor.UndrawnSummaryAsync();
        Assert.NotNull(summary);
        Assert.Contains("1 device of the plant is not in the drawing", summary);
        Assert.Contains("1 junction goes with them", summary);

        await editor.OpenTheUndrawnBoxAsync();

        var line = Assert.Single(await editor.UndrawnDeviceLinesAsync());
        Assert.Contains(GhostName, line);
        Assert.Contains(GhostId.ToString(), line);
        Assert.Contains("its device type is 99", line);
        Assert.NotNull(await editor.UndrawnJunctionsLineAsync());
    }

    /// <summary>
    /// The normal case in the real page: a plant whose every device is drawn gets no box at all, not
    /// a line saying zero.
    /// </summary>
    /// <remarks>
    /// <b>It can fail, and that is why it is written this way.</b> The absence is asked for only
    /// after the two seeded devices are really on the canvas, so a page that failed to open, or a
    /// drawing that never arrived, goes red before the absence is even looked at - and the test above
    /// is the other end: the same page, on a plant with a ghost, must show the box.
    /// </remarks>
    [Fact]
    public async Task APlantWhoseDevicesAreAllDrawnShowsNoBoxAtAll()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);
        Assert.Equal(2, (await editor.MarkedCellIdsAsync()).Length);

        Assert.Equal(0, await editor.UndrawnBoxCountAsync());
        Assert.Null(await editor.UndrawnSummaryAsync());
    }

    /// <summary>
    /// Adds the ghost and joins it to one of the seeded devices, with the edit permit taken and
    /// given back the way <see cref="PlantSeed"/> does it - left with whoever seeds, a save from the
    /// page would find the plant taken.
    /// </summary>
    private static async Task SeedTheGhostAsync(WcsClient client)
    {
        await PlantSeed.AddDeviceAsync(client, SeedHolder, GhostId, GhostName, DeviceTypes.None, GhostGeometry);

        await PlantSeed.WaitForTheEditPermitToBeFreeAsync(client, SeedHolder);
        await client.AcquireEditLockAsync(SeedHolder);
        client.EditHolder = SeedHolder;
        try
        {
            await client.AddJunctionAsync(new JunctionRequest(PlantSeed.DraggedId, GhostId, 99));
        }
        finally
        {
            client.EditHolder = null;
            await client.ReleaseEditLockAsync(SeedHolder);
        }
    }
}
