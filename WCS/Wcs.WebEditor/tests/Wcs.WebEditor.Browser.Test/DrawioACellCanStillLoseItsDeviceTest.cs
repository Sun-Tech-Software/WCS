using Xunit;
using Xunit.Abstractions;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// <b>Row 15, the question that decides whether the tool has a job: can a shape on this canvas
/// still end up without a device?</b> Measured, with a real gesture, in a real browser.
/// </summary>
/// <remarks>
/// <para>
/// <b>The answer is yes, and this is the road.</b> The branch has closed, one by one, the ways a
/// cell of OURS lost its link - the mark taken off with Edit Data, the mark mangled, the device
/// deleted from the canvas and pasted back - and what is left open is the other direction: a
/// drawing that comes in FROM OUTSIDE carrying <c>wcs="device"</c>. A drawing file dropped on the
/// canvas is one gesture, and it is the one measured here; a Ctrl+A / Ctrl+C in this Editor and a
/// Ctrl+V back into it is the other, measured on 2026-09-20 with the same outcome (two marked
/// cells, two "NOT created", two refusals at the save) and not kept as a test of its own because
/// the road past the first cell is the same one.
/// </para>
/// <para>
/// <b>Why the cell cannot become a device</b>, which is the half that makes this permanent: the
/// device type travels in <c>wcsType</c>, and <see cref="Wcs.WebEditor.Drawio.DrawioLibrary"/>
/// writes it - <c>DrawioDocument.Build</c> does NOT. So every device cell of a drawing this Editor
/// itself produced comes back in without a type, is refused at the drop by name, and stays on the
/// canvas as a shape marked as a device that the plant does not know. <b>It survives the save and
/// the reopening</b>: the reader takes it out of the decoration, the delta hands it back because
/// the plant does not draw it, and the next opening puts it on the canvas again - refused, once
/// more, at every save from now on.
/// </para>
/// <para>
/// The remedy the page named was "delete the shape and drag it again", which loses the shape. Row
/// 15 is the other one: point it at a device (<see cref="DrawioReassociateTest"/>).
/// </para>
/// </remarks>
public sealed class DrawioACellCanStillLoseItsDeviceTest(DrawioEditorFixture fixture, ITestOutputHelper output)
    : IClassFixture<DrawioEditorFixture>
{
    private const string SeedHolder = "browser-test-a-cell-can-still-lose-its-device";

    /// <summary>The label the dropped file gives its device cell: unlike anything the plant or this Editor writes, so finding it on the canvas can mean nothing else.</summary>
    private const string ForeignName = "DeviceFromAnotherPlant";

    /// <summary>
    /// The file dropped on the canvas. The identifier inside it is deliberately a guid the plant
    /// does not have - the shape of a drawing exported from ANOTHER plant - although draw.io gives
    /// the imported cell an identifier of its own anyway: the measurement below says which of the
    /// two the canvas ends up with, instead of this test assuming it.
    /// </summary>
    private static string TheForeignDrawing() =>
        """
        <mxfile host="another-plant"><diagram id="p1" name="Page-1"><mxGraphModel><root>
        <mxCell id="0"/><mxCell id="1" parent="0"/>
        <object id="a1b2c3d4-0000-4000-8000-0000000009f1" label="DeviceFromAnotherPlant" wcs="device"><mxCell style="rounded=0;whiteSpace=wrap;html=1;" vertex="1" parent="1"><mxGeometry x="900" y="700" width="120" height="60" as="geometry"/></mxCell></object>
        </root></mxGraphModel></diagram></mxfile>
        """;

    /// <summary>
    /// <b>The measurement of row 15's first question.</b> A drawing file dropped on the canvas
    /// leaves a shape marked as a device that the plant does not know; the drop says why it was not
    /// created, the save refuses it by name, and it is <b>still there at the next opening</b>.
    /// </summary>
    [Fact]
    public async Task ADroppedDrawingFileLeavesAShapeMarkedAsADeviceThatThePlantDoesNotKnow()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);
        var before = await client.GetPlantGraphAsync();

        string orphan;
        await using (var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host))
        {
            await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
            await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);

            output.WriteLine(await editor.DropAFileOnTheCanvasAsync("another-plant.drawio", TheForeignDrawing(), withCtrl: false));

            // The shape really arrived, and it really carries our mark: without this the two
            // refusals below would be about a cell nobody has seen.
            await editor.WaitForMarkedCellsAsync(3);
            await editor.WaitForTheLabelInTheDrawingAsync(ForeignName);
            orphan = Assert.Single(await editor.MarkedCellIdsAsync(), id => !Guid.TryParse(id, out _));
            output.WriteLine($"La cella arrivata dal file porta l'identificativo \"{orphan}\": draw.io le da' il suo, " +
                "quello scritto nel file non sopravvive all'importazione.");

            // THE DROP said why, and the reason is the one that makes this road permanent: the type
            // travels in wcsType, and a drawing of ours carries none.
            await editor.WaitForTheCreationReportAsync();
            var notCreated = Assert.Single(await editor.NotCreatedAsync());
            output.WriteLine($"NON CREATA: {notCreated}");
            Assert.Contains(orphan, notCreated);
            Assert.Contains("wcsType", notCreated);
            Assert.Empty(await editor.CreatedAsync());

            // And the plant did not grow: a shape that looks like a device, and no device.
            Assert.Equal(before.Items.Count, (await client.GetPlantGraphAsync()).Items.Count);

            await editor.SaveAsync();
            Assert.Null(await editor.SaveErrorAsync());
            var report = await editor.ReportAsync();
            output.WriteLine($"VERBALE:{Environment.NewLine}{report}");

            var refusal = Assert.Single(await editor.RefusalsAsync());
            Assert.Contains(orphan, refusal);
            Assert.Contains("the plant does not know", refusal);
            Assert.Equal(before.Items.Count, (await client.GetPlantGraphAsync()).Items.Count);
        }

        // --- AND IT IS STILL THERE AT THE NEXT OPENING, which is the whole finding: the shape is
        // not a mishap that the reload tidies up, it is a state of the drawing that stays until
        // somebody does something about it.
        await PlantSeed.WaitForTheEditPermitToBeFreeAsync(client, SeedHolder);
        await using var reopened = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await reopened.WaitForDeviceAsync(PlantSeed.DraggedName);
        await reopened.WaitForTheLabelInTheDrawingAsync(ForeignName);

        var marked = await reopened.MarkedCellIdsAsync();
        foreach (var id in marked) output.WriteLine($"Alla riapertura, cella marchiata: {id}.");
        Assert.Contains(orphan, marked);
    }
}
