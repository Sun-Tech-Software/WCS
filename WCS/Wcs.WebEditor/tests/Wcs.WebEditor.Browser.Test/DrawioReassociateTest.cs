using Xunit;
using Xunit.Abstractions;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// <b>Row 15 walked in a real browser: a shape the plant does not know, pointed at a device by
/// hand.</b>
/// </summary>
/// <remarks>
/// <para>
/// The shape is made the way the product really makes one - a drawing file dropped on the canvas,
/// the road measured in <see cref="DrawioACellCanStillLoseItsDeviceTest"/> - not by writing a cell
/// into draw.io's model from here: the whole question this row answers is what happens to a shape
/// that arrived from outside.
/// </para>
/// <para>
/// <b>Both refusals are walked as well</b>, and in the same test as the gesture that goes through.
/// It is one setup and three answers, and keeping them together is what shows that the refusals are
/// refusals of THIS gesture and not of a page that never works: the third one, with nothing changed
/// but the identifier typed, writes the geometry to the plant.
/// </para>
/// </remarks>
public sealed class DrawioReassociateTest(DrawioEditorFixture fixture, ITestOutputHelper output)
    : IClassFixture<DrawioEditorFixture>
{
    private const string SeedHolder = "browser-test-reassociate";

    private const string ForeignName = "DeviceFromAnotherPlant";

    private static string TheForeignDrawing() =>
        """
        <mxfile host="another-plant"><diagram id="p1" name="Page-1"><mxGraphModel><root>
        <mxCell id="0"/><mxCell id="1" parent="0"/>
        <object id="a1b2c3d4-0000-4000-8000-0000000009f2" label="DeviceFromAnotherPlant" wcs="device"><mxCell style="rounded=0;whiteSpace=wrap;html=1;" vertex="1" parent="1"><mxGeometry x="900" y="700" width="120" height="60" as="geometry"/></mxCell></object>
        </root></mxGraphModel></diagram></mxfile>
        """;

    [Fact]
    public async Task AShapeThePlantDoesNotKnowIsPointedAtADeviceAndTheSaveWritesItsGeometryThere()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);
        var seeded = PlantSeed.GeometryOf(await client.GetPlantGraphAsync(), PlantSeed.UntouchedId);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);

        output.WriteLine(await editor.DropAFileOnTheCanvasAsync("another-plant.drawio", TheForeignDrawing(), withCtrl: false));
        await editor.WaitForMarkedCellsAsync(3);
        await editor.WaitForTheLabelInTheDrawingAsync(ForeignName);
        var orphan = Assert.Single(await editor.MarkedCellIdsAsync(), id => !Guid.TryParse(id, out _));
        output.WriteLine($"Sagoma orfana: {orphan}.");

        // --- THE BOX IS THERE, for this shape and because of what it is.
        Assert.Equal(1, await editor.SelectCellAsync(orphan));
        await editor.WaitForTheReassociationBoxAsync();

        // Nothing of this plant is without a shape yet: the plant draws both its devices, and the
        // box says so instead of offering identifiers that would all be refused. Waited for and not
        // merely read, for the reason written over WaitForTheReassociationCandidatesAsync.
        await editor.WaitForTheReassociationCandidatesAsync("No device");
        output.WriteLine($"CANDIDATI: {await editor.ReassociationCandidatesAsync()}");

        // --- REFUSAL 1: an identifier the plant does not know. It is the loaded gun of this row,
        // and what has to be measured is that the page says no and writes nothing.
        var unknown = Guid.Parse("99999999-9999-9999-9999-999999999999");
        await editor.ReassociateAsync(unknown.ToString());
        var refusedUnknown = await editor.WaitForTheReassociationOutcomeAsync();
        output.WriteLine($"ESITO 1: {refusedUnknown}");
        Assert.Contains(unknown.ToString(), refusedUnknown);
        Assert.Contains("Nothing was written", refusedUnknown);
        Assert.False(await editor.TheReassociationWentThroughAsync());
        // Still an orphan: the box has not gone away.
        Assert.True(await editor.TheReassociationBoxIsThereAsync());

        // --- REFUSAL 2: a device that IS drawn on this canvas. Two shapes for one device would
        // send two geometries for it at the next save.
        await editor.ReassociateAsync(PlantSeed.UntouchedId.ToString());
        var refusedTaken = await editor.WaitForTheReassociationOutcomeAsync();
        output.WriteLine($"ESITO 2: {refusedTaken}");
        Assert.Contains("already drawn on this canvas", refusedTaken);
        Assert.Contains(PlantSeed.UntouchedId.ToString(), refusedTaken);
        Assert.False(await editor.TheReassociationWentThroughAsync());

        // --- AND NOW THE DEVICE REALLY IS WITHOUT A SHAPE: the operator deletes its cell, which is
        // one of the ways a plant ends up with a device nothing on the canvas stands for.
        Assert.Equal(0, await editor.DeleteTheCellAsync(PlantSeed.UntouchedId.ToString()));
        Assert.Equal(1, await editor.SelectCellAsync(orphan));
        await editor.WaitForTheReassociationBoxAsync();
        // The box was ALREADY on screen before the delete, so waiting for it says nothing: what has
        // to be waited for is the page having read the canvas again. Measured - see
        // WaitForTheReassociationCandidatesAsync, which this line exists because of.
        await editor.WaitForTheReassociationCandidatesAsync("1 device");
        await editor.OpenTheReassociationCandidatesAsync();
        var candidate = Assert.Single(await editor.ReassociationCandidateLinesAsync());
        output.WriteLine($"CANDIDATO: {candidate}");
        Assert.Contains(PlantSeed.UntouchedName, candidate);
        Assert.Contains(PlantSeed.UntouchedId.ToString(), candidate);

        // --- THE GESTURE.
        await editor.ReassociateAsync(PlantSeed.UntouchedId.ToString());
        var done = await editor.WaitForTheReassociationOutcomeAsync();
        output.WriteLine($"ESITO 3: {done}");
        Assert.True(await editor.TheReassociationWentThroughAsync());
        Assert.Contains(PlantSeed.UntouchedName, done);
        Assert.Contains("Nothing has reached the plant yet", done);

        // The shape stops calling itself by the name of a device it is not.
        await editor.WaitForCellLabelAsync(orphan, PlantSeed.UntouchedName);

        // Nothing has reached the plant, exactly as the outcome says.
        var stillSeeded = PlantSeed.GeometryOf(await client.GetPlantGraphAsync(), PlantSeed.UntouchedId);
        Assert.Equal(seeded.X, stillSeeded.X);

        // --- AND THE SAVE.
        var box = await editor.CellGeometryAsync(orphan);
        output.WriteLine($"Riquadro della sagoma: x={box[0]}, y={box[1]}, w={box[2]}, h={box[3]}.");

        await editor.SaveAsync();
        Assert.Null(await editor.SaveErrorAsync());
        output.WriteLine($"VERBALE:{Environment.NewLine}{await editor.ReportAsync()}");

        var after = await client.GetPlantGraphAsync();
        // The device is still on the plant: the cell that used to carry its guid is gone from the
        // canvas, and without the pair this save would have DELETED it.
        Assert.Contains(after.Items, item => item.ItemId == PlantSeed.UntouchedId);

        var written = PlantSeed.GeometryOf(after, PlantSeed.UntouchedId);
        output.WriteLine($"Geometria dell'impianto: x={written.X}, y={written.Y}, w={written.Width}, h={written.Height}.");
        Assert.Equal(box[0], written.X!.Value, precision: 2);
        Assert.Equal(box[1], written.Y!.Value, precision: 2);
        Assert.Equal(box[2], written.Width!.Value, precision: 2);
        Assert.Equal(box[3], written.Height!.Value, precision: 2);

        // And the shape is not refused any more: it is not "a cell the plant does not know".
        Assert.DoesNotContain(await editor.RefusalsAsync(), line => line.Contains(orphan, StringComparison.Ordinal));
    }
}
