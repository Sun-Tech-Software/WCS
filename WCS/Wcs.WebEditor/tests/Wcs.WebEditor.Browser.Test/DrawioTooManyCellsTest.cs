using System.Globalization;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// Queue item 15: the two messages from the browser that grow with the cells of a gesture - the cells
/// it added (<c>OnDrawioCellsAdded</c>) and the cells selected (<c>OnDrawioSelectionChanged</c>) - have
/// a ceiling in the bridge (drawio-bridge.js, <c>largestGestureMessage</c>, 300 KB). Over it the bridge
/// sends the page the count instead, and the page says it; under it nothing changes.
/// </summary>
/// <remarks>
/// <para>
/// The gesture is a real Ctrl+V of plain shapes off the system clipboard: draw.io adds them in one
/// edit and selects them all, so one keystroke makes both messages. 14,000 shapes are over the ceiling
/// in both (measured before the fix: the two crossed together in a frame of 1,968,421 bytes, and the
/// host refused it - "The maximum message size of 327680B was exceeded" - and closed the connection);
/// 2,000 are under it in both.
/// </para>
/// <para>
/// Plain shapes and not copies of devices: whether the page could create a device out of a cell is
/// not what is measured here, how many cells it is told of is.
/// </para>
/// </remarks>
public sealed class DrawioTooManyCellsTest(DrawioEditorFixture fixture, ITestOutputHelper output) : IClassFixture<DrawioEditorFixture>
{
    private const string SeedHolder = "browser-test-too-many-cells";

    /// <summary>The circuit's ceiling on one message from the browser (<c>WebEditorExtensions.CircuitMessageCeiling</c>).</summary>
    private const int CircuitCeiling = 320 * 1024;

    [Fact]
    public async Task AGestureOverTheCeilingIsSaidAsItsCountAndTheCircuitLives()
    {
        const int Pasted = 14_000;
        await using var editor = await OpenOnTheSeedAsync();

        await PasteAsync(editor, Pasted);

        const string TheLine = "Too many cells to follow (14000): the cells just added are not known to this page, and none of them became a device.";
        await editor.WaitForTextAsync("#drawio-too-many-added-notice-text", TheLine);
        Assert.Equal(TheLine, await editor.TextOfAsync("#drawio-too-many-added-notice-text"));
        await editor.WaitForSelectionLineAsync("Selected: 14000 cells, too many to follow: the panel shows none of them.");
        WriteTheLargeFrames(editor);

        // The circuit lived through it: nothing over its ceiling left the browser, the connection never
        // dropped, and the page still hears draw.io - a click on a device is the panel's again.
        Assert.DoesNotContain(editor.CircuitFrames(), frame => frame.FromTheBrowser && frame.Bytes > CircuitCeiling);
        Assert.Null(await editor.TextOfAsync("#drawio-reconnected-notice"));
        await editor.SelectDevicesAsync(PlantSeed.DraggedName);
        await editor.WaitForSelectionLineAsync($"Selected: {PlantSeed.DraggedName}");
        Assert.Null(await editor.TextOfAsync("#drawio-reconnected-notice"));
        Assert.DoesNotContain("maximum message size", fixture.Host.LogTail(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGestureUnderTheCeilingCrossesWhole()
    {
        const int Pasted = 2_000;
        await using var editor = await OpenOnTheSeedAsync();

        await PasteAsync(editor, Pasted);

        // The selection crossed whole: the page counts the cells it was told of.
        await editor.WaitForSelectionLineAsync("Selected: 2000 cells (0 devices of the plant).");
        WriteTheLargeFrames(editor);

        // And so did the additions: one message from the browser carries them, larger than any warning
        // could be (measured: 277,117 bytes together with the selection, which alone is 55,027), and no
        // warning was sent.
        var frames = editor.CircuitFrames().Where(frame => frame.FromTheBrowser).ToList();
        Assert.Contains(frames, frame => frame.Words.Contains("OnDrawioCellsAdded", StringComparison.Ordinal) && frame.Bytes > 200_000);
        Assert.DoesNotContain(frames, frame => frame.Words.Contains("OnDrawioTooManyCells", StringComparison.Ordinal));
        Assert.Null(await editor.TextOfAsync("#drawio-too-many-added-notice"));
    }

    private async Task<DrawioEditorPage> OpenOnTheSeedAsync()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);
        return editor;
    }

    /// <summary>The operator's Ctrl+V of <paramref name="count"/> plain shapes, until draw.io's model holds them all.</summary>
    private static async Task PasteAsync(DrawioEditorPage editor, int count)
    {
        var before = await editor.CellCountAsync();
        await editor.PutOnTheSystemClipboardAsync(PlainShapes(count));
        await editor.ClickEmptyCanvasAsync(PlantSeed.DraggedName, PlantSeed.UntouchedName);
        await editor.PasteAsync();
        await editor.WaitForTheCellCountAsync(before + count);
    }

    /// <summary>
    /// <paramref name="count"/> small squares in a grid below the seed's two devices (the lower one ends
    /// at y = 501), as draw.io puts a drawing on the clipboard.
    /// </summary>
    private static string PlainShapes(int count)
    {
        var xml = new StringBuilder("<mxGraphModel><root><mxCell id=\"0\"/><mxCell id=\"1\" parent=\"0\"/>");
        for (var i = 0; i < count; i++)
        {
            var x = 40 + i % 120 * 14;
            var y = 700 + i / 120 * 14;
            xml.Append(CultureInfo.InvariantCulture,
                $"<mxCell id=\"plain-{i}\" value=\"\" style=\"rounded=0;\" vertex=\"1\" parent=\"1\"><mxGeometry x=\"{x}\" y=\"{y}\" width=\"10\" height=\"10\" as=\"geometry\"/></mxCell>");
        }
        return xml.Append("</root></mxGraphModel>").ToString();
    }

    private void WriteTheLargeFrames(DrawioEditorPage editor)
    {
        foreach (var frame in editor.CircuitFrames().Where(frame => frame.Bytes >= 2 * 1024).OrderByDescending(frame => frame.Bytes))
            output.WriteLine(frame.ToString());
    }
}
