using Xunit;
using Xunit.Abstractions;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// <b>Review finding 1, in a real browser</b>: an arrow drawn onto a device that was dropped a
/// moment ago becomes a junction on the plant - read back from the HOST, not from the canvas.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it has to be here and not only in bUnit.</b> The defect lives in the seam between two
/// batches, and the seam is a session map that only the running page holds: the cell the drop
/// leaves behind carries draw.io's own identifier, the device carries the plant's, and the two
/// become one string nowhere except in that map. A bUnit test hands the delta a map WE wrote; here
/// the map is the one the page really built while a shape was really dragged, and the arrow's ends
/// are the identifiers draw.io really chose.
/// </para>
/// <para>
/// <b>What it measured before the repair</b>: nothing was written and the save refused the arrow
/// with "la freccia non collega due dispositivi noti all'impianto: non e' stata creata, resta un
/// disegno" - on a plant where both devices were perfectly well known. On a plant built from
/// scratch, which is the beta tester's own first hour, that meant no junction at all could be drawn
/// until the page was reloaded.
/// </para>
/// <para>
/// <b>The gesture is checked before the plant is.</b> The drop is checked by the creation report
/// and by the plant growing by one, and the arrow by the identifier draw.io hands back: if either
/// had not taken, the failure has to say "the gesture did not take" and not "the plant did not
/// change" - two different faults, repaired in two different places.
/// </para>
/// </remarks>
public sealed class DrawioJunctionToADroppedDeviceTest(DrawioEditorFixture fixture, ITestOutputHelper output)
    : IClassFixture<DrawioEditorFixture>
{
    /// <summary>Who seeds the plant: a lock holder of its own, released at once, as in the other browser tests.</summary>
    private const string SeedHolder = "browser-test-junction-drop-seed";

    private void DeclareBinary() => output.WriteLine(fixture.Host.Declaration);

    [Fact]
    public async Task AnArrowOntoADeviceJustDroppedBecomesAJunctionOnThePlant()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);
        var before = await client.GetPlantGraphAsync();
        // The plant starts with no junction at all: without this line the assertion at the bottom
        // could be reading a junction the seeding had put there.
        Assert.Empty(await client.GetJunctionsAsync());

        await using (var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host))
        {
            await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
            await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);

            await editor.DragEntryOntoTheCanvasAsync(0);
            await editor.WaitForTheCreationReportAsync();
            var created = await editor.CreatedAsync();
            Assert.True(created.Count == 1,
                $"Il rilascio non ha dichiarato la nascita di un dispositivo solo. Il verbale diceva:{Environment.NewLine}{await editor.CreationReportAsync()}");

            // The cell the drop left behind: the one marked device cell whose identifier is NOT of
            // the plant's own kind. That is the whole fact the defect turned on, and it is asked of
            // the model rather than assumed.
            var marked = await editor.MarkedCellIdsAsync();
            output.WriteLine($"Celle marchiate nel modello: {string.Join(", ", marked)}.");
            var dropped = Assert.Single(marked, id => !Guid.TryParse(id, out _));

            var arrow = await editor.DrawAnArrowAsync(PlantSeed.DraggedId.ToString(), dropped);
            output.WriteLine($"Freccia disegnata: {arrow}, da {PlantSeed.DraggedId} a {dropped}.");

            await editor.SaveAsync();
            Assert.Null(await editor.SaveErrorAsync());

            // The report whole in the message: "there was no line" without saying what there was
            // starts whoever reads it back from zero.
            var refusals = await editor.RefusalsAsync();
            Assert.True(refusals.Count == 0,
                $"Il salvataggio ha rifiutato qualcosa. Il verbale diceva:{Environment.NewLine}{await editor.ReportAsync()}");
        }

        var after = await client.GetPlantGraphAsync();
        var born = Assert.Single(after.Items, item => before.Items.All(was => was.ItemId != item.ItemId));

        var junction = Assert.Single(await client.GetJunctionsAsync());
        output.WriteLine($"Giunzione sull'impianto: {junction.JunctionId}, {junction.SourceItemId} -> {junction.DestinationItemId}, priorita' {junction.Priority}.");
        Assert.Equal(PlantSeed.DraggedId, junction.SourceItemId);
        Assert.Equal(born.ItemId, junction.DestinationItemId);
    }
}
