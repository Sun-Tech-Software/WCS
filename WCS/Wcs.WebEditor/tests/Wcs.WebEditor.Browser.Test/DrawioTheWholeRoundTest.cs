using Xunit;
using Xunit.Abstractions;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// <b>The whole round an operator makes, in one go</b>: connect two devices, configure one, save,
/// export the plant to a file, import that file back, and reopen. Every other browser test in this
/// project measures one gesture in isolation; this one is the only place where the gestures meet.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a round and not five tests.</b> Four separate reviews of this branch found their defects
/// in the SEAMS - a junction that dies at the second save, a message that contradicts the save a
/// minute later, a cell that survives one round and not two. None of those is visible to a test
/// that does one gesture against a fresh plant, because each needs the state the previous gesture
/// left. This test keeps the state and walks through it.
/// </para>
/// <para>
/// <b>It narrates.</b> Everything the operator would read - the save report, the import dialog's
/// sentence about the drawing, what is on the canvas at the reopening - is written to the test
/// output, so a failure says what a person would have seen and not only which assertion broke.
/// </para>
/// </remarks>
public sealed class DrawioTheWholeRoundTest(DrawioEditorFixture fixture, ITestOutputHelper output)
    : IClassFixture<DrawioEditorFixture>
{
    private static readonly string SeedHolder = $"round-{Guid.NewGuid():N}";

    [Fact]
    public async Task TheWholeRoundLeavesThePlantAndTheDrawingSayingTheSameThing()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        var exported = Path.Combine(Path.GetTempPath(), $"round-{Guid.NewGuid():N}");
        Directory.CreateDirectory(exported);
        string file;

        await using (var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host))
        {
            await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
            await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);
            output.WriteLine($"APERTURA - sul disegno: {string.Join(", ", await editor.LabelsInTheDrawingAsync())}");

            // 1. COLLEGARE. The arrow is drawn between the two seeded devices, whose cell ids are
            // their plant identifiers - this is the gesture the owner's rule of 2026-09-19 is about.
            var arrow = await editor.DrawAnArrowAsync(PlantSeed.DraggedId.ToString(), PlantSeed.UntouchedId.ToString());
            output.WriteLine($"COLLEGATO - la freccia disegnata e' la cella {arrow}");

            // 2. CONFIGURARE, on the same round: the panel writes straight to the host, and it must
            // not disturb the arrow waiting to be saved. That interference is the seam.
            await editor.SelectCellAsync(PlantSeed.DraggedId.ToString());
            await editor.TypePropertyAsync("Name", "RoundDevice");
            await editor.ConfirmPropertiesAsync();
            await editor.WaitForPropertiesWrittenAsync("RoundDevice");
            output.WriteLine("CONFIGURATO - il dispositivo si chiama adesso RoundDevice");

            // 3. SALVARE. The junction is born here, and the report is what the operator reads.
            await editor.SaveAsync();
            output.WriteLine($"VERBALE DEL SALVATAGGIO:{Environment.NewLine}{await editor.ReportAsync()}");
            Assert.Null(await editor.SaveErrorAsync());
            Assert.Empty(await editor.RefusalsAsync());

            var afterSave = await client.GetPlantGraphAsync();
            Assert.Single(afterSave.Junctions);
            Assert.Contains(afterSave.Items, item => item.Name == "RoundDevice");
            output.WriteLine($"IMPIANTO - {afterSave.Items.Count} dispositivi, {afterSave.Junctions.Count} giunzione");

            // 4. ESPORTARE. Since 2026-09-20 the file is a complete backup (owner's decision), so
            // the free drawing has to be inside it - measured below by reading the file, not by
            // trusting the button.
            // The box has to be opened first, and that is part of the gesture rather than an
            // accident of the test: the two plant-file commands live behind a closed <details>
            // BECAUSE one of them replaces the whole plant, and a bare "Import" sitting next to a
            // harmless check would be an invitation to a click nobody meant. Measured the hard way -
            // the first run of this round pressed Export with the box shut and waited 60 s for a
            // download that could not start.
            await editor.OpenThePlantFileSectionAsync();
            file = await editor.ExportToAFileAsync(exported);
            output.WriteLine($"ESPORTATO in {Path.GetFileName(file)}, {new FileInfo(file).Length} byte");
        }

        var onDisk = await File.ReadAllTextAsync(file);
        Assert.Contains("\"decoration\"", onDisk, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RoundDevice", onDisk, StringComparison.Ordinal);
        output.WriteLine("IL FILE PORTA il campo della decorazione e il nome nuovo del dispositivo.");

        // 5. IMPORTARE quel file, e 6. RIAPRIRE: the import ends with a full page reload, so what
        // comes after is the reopening as well.
        await PlantSeed.WaitForTheEditPermitToBeFreeAsync(client, SeedHolder);
        await using (var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host))
        {
            await editor.WaitForDeviceAsync("RoundDevice");
            await editor.OpenThePlantFileSectionAsync();
            await editor.ChooseTheFileToImportAsync(file);
            output.WriteLine($"LA FINESTRA DELL'IMPORTAZIONE DICE DEL DISEGNO: {await editor.WhatTheImportSaysAboutTheDrawingAsync()}");
            // THREE MINUTES, and the number is measured rather than chosen. This step takes about a
            // second when the round runs alone; in a full suite run it went past the usual 60 s
            // ceiling in two runs out of three, always here - the import takes the whole page down
            // and brings it back up, and by then the machine is carrying several hosts and several
            // Chromiums. The plant being replaced has four devices, so it is the machine that is
            // slow and not the product, and a round that fails two times out of three would teach
            // this suite to ignore its own reds.
            await editor.ConfirmTheImportAndWaitForTheReloadAsync(fixture.Host.ApiKey, ceiling: 180_000);

            await editor.WaitForDeviceAsync("RoundDevice");
            var labels = await editor.LabelsInTheDrawingAsync();
            var edges = await editor.EdgesInTheDrawingAsync();
            output.WriteLine($"RIAPERTURA - sul disegno: {string.Join(", ", labels)}; archi: {edges.Length}");

            // THE POINT OF THE WHOLE ROUND: plant and drawing say the same thing at the end of it.
            var afterImport = await client.GetPlantGraphAsync();
            Assert.Contains(afterImport.Items, item => item.Name == "RoundDevice");
            Assert.Single(afterImport.Junctions);
            Assert.Contains("RoundDevice", labels);
            Assert.Single(edges);
            output.WriteLine($"IMPIANTO DOPO L'IMPORTAZIONE - {afterImport.Items.Count} dispositivi, " +
                $"{afterImport.Junctions.Count} giunzione: il disegno e l'impianto dicono la stessa cosa.");
        }

        Directory.Delete(exported, recursive: true);
    }
}
