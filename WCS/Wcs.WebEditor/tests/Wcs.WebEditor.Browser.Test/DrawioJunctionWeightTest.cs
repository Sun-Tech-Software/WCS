using Xunit;
using Xunit.Abstractions;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// Row 20 of the parity table, with draw.io really running: the operator changes the weight of an
/// arrow the plant already knows, saves, and the plant takes it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it has to be here and not only in bUnit.</b> The bUnit tests hand the page a document
/// WE wrote: they prove the comparison and the write, and they cannot prove the one thing in the
/// middle - that an attribute changed inside draw.io's own model comes back out in the XML the
/// bridge hands us. That is a claim about draw.io, and a claim about draw.io is measured in a
/// browser or it is not measured.
/// </para>
/// <para>
/// <b>The gesture is checked before the plant is.</b> The attribute is read back out of the model
/// after being set: if mxGraph had not taken it, the failure has to say "the gesture did not take",
/// not "the plant did not change" - two different faults, repaired in two different places.
/// </para>
/// <para>
/// <b>The other half of the row is here too.</b> Opening the drawing and saving it with nobody
/// touching anything must not move a weight - not on 99, and not on the in-between numbers a
/// migrated plant can carry. The second measurement is the one that would have caught the silent
/// demotion the two disagreeing copies of the rule used to produce.
/// </para>
/// </remarks>
public sealed class DrawioJunctionWeightTest(DrawioEditorFixture fixture, ITestOutputHelper output) : IClassFixture<DrawioEditorFixture>
{
    /// <summary>Who seeds the plant: a lock holder of its own, released at once, as in the other browser tests.</summary>
    private const string SeedHolder = "browser-test-weight-seed";

    /// <summary>The attribute the drawing carries the weight in, and the two values it takes. Same spelling as <c>DrawioDocument</c>, on purpose: no reference to the product's assembly exists here.</summary>
    private const string PriorityAttribute = "wcsPriority";
    private const string High = "alta";
    private const string Low = "bassa";

    private void DeclareBinary() => output.WriteLine(fixture.Host.Declaration);

    /// <summary>
    /// The gesture itself: the seeded junction is low (99), the operator makes the arrow "alta", and
    /// the plant ends up at 0 - read back from the HOST, not from the canvas, because the canvas
    /// showing a thick red arrow is the part nobody doubted.
    /// </summary>
    [Fact]
    public async Task ChangingAnArrowsWeightInDrawioWritesItOnThePlant()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        var seeded = await PlantSeed.ResetAsync(client, SeedHolder, withJunction: true);
        Assert.NotNull(seeded);
        Assert.Equal(99, seeded.Priority);

        await using (var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host))
        {
            await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
            await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);

            // The cell of a junction carries the plant's own identifier (DrawioDocument.Build): it
            // is what makes the arrow findable in the model without asking the canvas anything.
            var inTheModel = await editor.SetCellAttributeAsync(seeded.JunctionId.ToString(), PriorityAttribute, High);
            Assert.Equal(High, inTheModel);

            await editor.SaveAsync();
            Assert.Null(await editor.SaveErrorAsync());

            // The report names the arrow and says which way it went: a write nobody can read about
            // is half of the same silence row 20 exists to end.
            var written = await editor.WrittenAsync();
            Assert.Contains(written, line => line.Contains("priority", StringComparison.OrdinalIgnoreCase)
                && line.Contains(seeded.JunctionId.ToString(), StringComparison.OrdinalIgnoreCase));
        }

        var junctions = await client.GetJunctionsAsync();
        var after = junctions.FirstOrDefault(j => j.JunctionId == seeded.JunctionId);
        Assert.True(after is not null,
            $"La giunzione {seeded.JunctionId} non e' piu' sull'impianto: il salvataggio l'ha portata via invece di cambiarle la priorita'.");
        Assert.Equal(0, after!.Priority);
    }

    /// <summary>
    /// The round with no gesture at all, on a junction at an <b>in-between</b> priority - 50, which
    /// is neither of the two numbers we ever write. The drawing goes out, comes back, and the plant
    /// must be untouched.
    /// <para>
    /// This is the browser's copy of the net against the trap. While the drawing decided "high or
    /// low" with one rule (<c>priority == 0</c>) and the round trip read it back with another
    /// (<c>FromPriority</c>), this arrow went out drawn low and came back read as a demand to lower
    /// it, against a plant that the comparison called high: a 99 written silently over a 50 nobody
    /// had touched. With one rule on both halves there is nothing to write.
    /// </para>
    /// </summary>
    [Fact]
    public async Task OpeningAndSavingLeavesAnInBetweenWeightExactlyWhereItWas()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        var seeded = await PlantSeed.ResetAsync(client, SeedHolder, withJunction: true);
        Assert.NotNull(seeded);

        // A priority a migrated plant can really carry, and that we never write ourselves.
        await PlantSeed.SetJunctionPriorityAsync(client, SeedHolder, seeded.JunctionId, 50);

        await using (var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host))
        {
            await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
            await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);

            await editor.SaveAsync();
            Assert.Null(await editor.SaveErrorAsync());

            // Not a single line of the report may be about a priority: not a write, not a refusal,
            // not a notice. Nobody touched that arrow.
            //
            // Review finding 3: DoesNotContain on its own would pass just as well if the report
            // were never rendered at all - ReportAsync answers "(nessun verbale)" when
            // #drawio-save-report is missing, and that sentinel contains no "priority" either. The
            // line below pins down that the report DID render before the negative assertion is
            // allowed to mean anything.
            var report = await editor.ReportAsync();
            Assert.NotEqual("(nessun verbale)", report);
            Assert.DoesNotContain("priority", report, StringComparison.OrdinalIgnoreCase);
        }

        var after = (await client.GetJunctionsAsync()).FirstOrDefault(j => j.JunctionId == seeded.JunctionId);
        Assert.True(after is not null,
            $"La giunzione {seeded.JunctionId} non e' piu' sull'impianto dopo un salvataggio senza nessun gesto.");
        Assert.Equal(50, after!.Priority);
    }

    /// <summary>
    /// The half of the drawing this change is paid for: an arrow at an in-between priority is drawn
    /// <b>high</b> - thick and red - because that is what the engine does with it. Measured on the
    /// XML draw.io hands back, which is the drawing as it really is, not as we composed it.
    /// </summary>
    [Fact]
    public async Task AnInBetweenWeightIsDrawnHigh()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        var seeded = await PlantSeed.ResetAsync(client, SeedHolder, withJunction: true);
        Assert.NotNull(seeded);
        await PlantSeed.SetJunctionPriorityAsync(client, SeedHolder, seeded.JunctionId, 50);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);
        await editor.RecordReturnedDrawingAsync();

        await editor.SaveAsync();

        var drawing = await editor.LastReturnedDrawingAsync();
        Assert.Contains($"{PriorityAttribute}=\"{High}\"", drawing);
        Assert.DoesNotContain($"{PriorityAttribute}=\"{Low}\"", drawing);
    }
}
