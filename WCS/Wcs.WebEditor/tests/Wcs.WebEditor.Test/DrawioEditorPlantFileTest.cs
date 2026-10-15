using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Pages;
using Wcs.WebEditor.Session;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

/// <summary>
/// The plant's file (export and import, rows 43 and 44) as a section of the page's strip, the
/// owner's decision of 27/9: until then it sat in a box of its own in the band above, next to the
/// checks. Measured through the WHOLE page, because what changed is where the page puts it and who
/// opens it - the component itself (<c>ImportExport</c>) is unchanged, and its own suites
/// (<c>ImportExportTest</c>, <c>DrawioEditorImportTest</c>) keep measuring what it does.
/// </summary>
/// <remarks>
/// <b>A pending import is never lost to its window being shut.</b> The confirmation is a dialog of the
/// component, which stays rendered while the window is shut (spec 2026-09-29: a shut window is hidden,
/// not removed): shutting the window hides the dialog with it, the file chosen stays in hand, and the
/// button of the strip says that an import is waiting - opening the window again brings the same
/// confirmation back. Nothing is written without "Confirm", and nothing is thrown away without
/// "Cancel". Since the windows, opening ANOTHER section no longer shuts this one: the window is shut
/// by its own button (or its X).
/// </remarks>
public sealed class DrawioEditorPlantFileTest : PlantCanvasBunitContext
{
    private static readonly Guid A = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid B = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private const string PlantFile = "drawio-plant-file";

    private static PlantItemDto Item(Guid id, double x, double y) =>
        new(id, id.ToString()[..4], DeviceTypes.Automation, null, null, null, default, default, 0, null,
            0, 0, 0, 0, default!, x, y, 130, 184, 0);

    private readonly DrawioWriteHandler _host = new() { Items = [Item(A, 10, 20), Item(B, 500, 20)] };

    public DrawioEditorPlantFileTest()
    {
        _host.Configs[A] = new PlantItemConfigDto { ItemId = A, Name = "A", DeviceType = (short)DeviceTypes.Automation, MaxUnloadWeight = 900 };
        _host.Configs[B] = new PlantItemConfigDto { ItemId = B, Name = "B", DeviceType = (short)DeviceTypes.Automation, MaxUnloadWeight = 100 };
    }

    private async Task<IRenderedComponent<DrawioEditor>> RenderPageAsync()
    {
        // The drawing's answers the device list needs to write a cell (DrawioEditorDeviceListTest's own).
        var bridge = JSInterop.SetupModule("./_content/Wcs.WebEditor/drawio-bridge.js");
        bridge.Setup<string?>("selectCell", _ => true).SetResult("ok");
        bridge.Setup<bool>("renameCell", _ => true).SetResult(true);
        var state = new EditorSessionState(new Uri("http://localhost:5580"), _host);
        state.UseKey("test-key");
        Services.AddSingleton(state);
        var cut = Render<DrawioEditor>();
        await cut.InvokeAsync(() => cut.Instance.OnDrawioReady());
        return cut;
    }

    /// <summary>
    /// Finds and triggers in ONE step on the renderer's dispatcher: the page keeps rendering on its
    /// own, and an element found on one render and triggered after the next one has lost its handler
    /// (the same helper as DrawioEditorDeviceListTest's).
    /// </summary>
    private static void Act(IRenderedComponent<DrawioEditor> cut, Action gesture) =>
        cut.InvokeAsync(gesture).GetAwaiter().GetResult();

    /// <summary>The section's button in the strip: it opens the section's window, or shuts it if it is open.</summary>
    private static void Press(IRenderedComponent<DrawioEditor> cut, string section) =>
        Act(cut, () => cut.Find($"#{section}-toggle").Click());

    private static bool IsOpen(IRenderedComponent<DrawioEditor> cut, string section) =>
        !cut.Find($"#{section}").HasAttribute("hidden");

    /// <summary>
    /// What the plant file's button in the strip says: its name, or what the waiting import is doing.
    /// Since the passata grafica (2026-09-29) the button shows its name, and with an import waiting a
    /// "!" and the sentence behind a tip: so the sentence is read from the tip when there is one, and
    /// the name from the label otherwise - the whole of the button's text would glue the three
    /// together. (That the "!" is there with the tip is DrawioTopBarTest's.)
    /// </summary>
    private static string ButtonText(IRenderedComponent<DrawioEditor> cut)
    {
        var button = cut.Find($"#{PlantFile}-toggle");
        return (button.QuerySelector(".drawio-tip") ?? button.QuerySelector(".drawio-toggle-label"))!.TextContent.Trim();
    }

    private static void Choose(IRenderedComponent<DrawioEditor> cut, InputFileContent file) =>
        Act(cut, () => cut.FindComponent<InputFile>().UploadFiles(file));

    private static InputFileContent NativeFile(params Guid[] itemIds) =>
        InputFileContent.CreateFromBinary(
            JsonSerializer.SerializeToUtf8Bytes(new PlantGraphDto(
                [.. itemIds.Select(id => new PlantItemConfigDto { ItemId = id })], [], [], [], null)),
            "impianto.json");

    /// <summary>
    /// The section is the FIRST of the strip - the plant as a whole, before what is done on it - and
    /// the page is born with its window shut, like every other section. The band above keeps only the
    /// status lines and the checks: the file is nowhere else on the page.
    /// </summary>
    [Fact]
    public async Task ThePlantFileIsTheFirstSectionOfTheStripAndIsBornShut()
    {
        var cut = await RenderPageAsync();

        var buttons = cut.FindAll(".drawio-sections > .drawio-section-toggle");
        Assert.Equal($"{PlantFile}-toggle", buttons[0].Id);
        Assert.Equal("drawio-operations-toggle", buttons[1].Id);
        // The window each of those buttons opens, next to it: the bar also holds the plant checks'
        // window, after their own button, before the sections (passata grafica, 2026-09-29).
        var windows = cut.FindAll(".drawio-sections > .drawio-section-toggle + .drawio-window");
        Assert.Equal(PlantFile, windows[0].Id);
        Assert.Equal("drawio-operations", windows[1].Id);
        Assert.Single(cut.FindAll($"#{PlantFile}"));
        Assert.False(IsOpen(cut, PlantFile));
        Assert.Equal("Plant file", ButtonText(cut));
        // The commands are there while the section is shut (the body stays rendered, as Operations'):
        // what is hidden is hidden by the browser, not taken off the page.
        Assert.Single(cut.FindAll(".drawio-sections #plant-import-input"));
        Assert.Single(cut.FindAll("#plant-import-input"));
    }

    /// <summary>
    /// The same gesture as the other sections, and now independent of them: its button opens its
    /// window next to whatever is open, opening another leaves it open, and only its own button (or
    /// its X) shuts it.
    /// </summary>
    [Fact]
    public async Task ThePlantFileOpensAndShutsOnItsOwnWhateverElseIsOpen()
    {
        var cut = await RenderPageAsync();

        Press(cut, "drawio-operations");
        Assert.True(IsOpen(cut, "drawio-operations"));

        Press(cut, PlantFile);
        Assert.True(IsOpen(cut, PlantFile));
        Assert.True(IsOpen(cut, "drawio-operations"));

        Press(cut, "drawio-junction-list");
        Assert.True(IsOpen(cut, "drawio-junction-list"));
        Assert.True(IsOpen(cut, PlantFile));

        Press(cut, PlantFile);
        Assert.False(IsOpen(cut, PlantFile));
        Assert.True(IsOpen(cut, "drawio-operations"));
        Assert.True(IsOpen(cut, "drawio-junction-list"));

        Press(cut, PlantFile);
        Assert.True(IsOpen(cut, PlantFile));
        Act(cut, () => cut.Find($"#{PlantFile}-close").Click());
        Assert.False(IsOpen(cut, PlantFile));
    }

    [Fact]
    public async Task PlantFileGroupsItsCommandsIntoExportAndImportModules()
    {
        var cut = await RenderPageAsync();
        Press(cut, PlantFile);

        var modules = cut.FindAll("#drawio-plant-file .drawio-operational-card");
        Assert.Equal(["plant-export-module", "plant-import-module"], modules.Select(module => module.Id));
        Assert.NotNull(cut.Find("#plant-export-module #plant-export-button"));
        Assert.NotNull(cut.Find("#plant-import-module #plant-import-button"));
        Assert.NotNull(cut.Find("#plant-import-module #plant-import-input"));
        Assert.Contains("is-info", modules[0].ClassList);
        Assert.Contains("is-warning", modules[1].ClassList);
        Assert.Empty(cut.FindAll("#drawio-plant-file-status"));
    }

    /// <summary>
    /// The decision this task had to make. A file chosen and waiting for "Confirm" survives the
    /// operator shutting the plant file's window: the confirmation is hidden with the window, the
    /// button of the strip says that an import is waiting, nothing is written meanwhile, and opening
    /// the window again brings the same confirmation back - which confirms the file that was chosen.
    /// </summary>
    [Fact]
    public async Task APendingImportSurvivesItsWindowBeingShutAndTheButtonSaysSo()
    {
        var cut = await RenderPageAsync();
        Press(cut, PlantFile);
        Choose(cut, NativeFile(A));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("#plant-import-confirm")));

        var status = cut.Find("#plant-import-module #drawio-plant-file-status");
        Assert.Equal("status", status.GetAttribute("role"));
        Assert.Equal("polite", status.GetAttribute("aria-live"));
        Assert.Contains("impianto.json is waiting for confirmation", status.TextContent);
        Assert.NotNull(cut.Find("#plant-import-cancel-button"));
        Assert.NotNull(cut.Find("#drawio-plant-file-toggle .drawio-badge"));

        Press(cut, PlantFile);

        Assert.False(IsOpen(cut, PlantFile));
        Assert.Equal("Plant file (an import is waiting for confirmation: \"impianto.json\")", ButtonText(cut));
        var dialog = Assert.Single(cut.FindAll("#plant-import-confirm"));
        Assert.Equal(PlantFile, dialog.Closest(".drawio-window")?.Id);
        Assert.Empty(_host.GraphPuts);
        Assert.Equal(0, _host.EditLockRequests);

        Press(cut, PlantFile);
        Assert.True(IsOpen(cut, PlantFile));
        Assert.Contains("impianto.json", cut.Find("#plant-import-confirm .plant-import-title").TextContent);
        Act(cut, () => cut.Find("#plant-import-confirm-button").Click());

        var imported = Assert.Single(_host.GraphPuts);
        Assert.Equal(A, Assert.Single(imported.Items).ItemId);
    }

    /// <summary>The summary says "waiting" only while something waits: cancelling gives it back its plain name.</summary>
    [Fact]
    public async Task CancellingThePendingImportGivesTheButtonBackItsName()
    {
        var cut = await RenderPageAsync();
        Press(cut, PlantFile);
        Choose(cut, NativeFile(A));
        cut.WaitForAssertion(() => Assert.StartsWith("Plant file (an import is waiting", ButtonText(cut)));

        Act(cut, () => cut.Find("#plant-import-cancel-button").Click());

        Assert.Empty(cut.FindAll("#plant-import-confirm"));
        Assert.Empty(cut.FindAll("#drawio-plant-file-status"));
        Assert.Equal("Plant file", ButtonText(cut));
        Assert.Empty(_host.GraphPuts);
    }

    // ---- Fix round of 27/9 ------------------------------------------------------------------

    /// <summary>
    /// IMPORTANT 1 of the review: a confirmation that waits in its shut section while the operator
    /// changes the plant through the page is compared again at once - the plant the page holds
    /// moved - and the line says so while the section is still shut; reopened, it shows the new
    /// comparison and says that nothing was written. One "Confirm" then writes, because what it
    /// shows is the plant as it is.
    /// </summary>
    [Fact]
    public async Task APlantChangedThroughThePageWhileTheImportWaitsShutIsComparedAgain()
    {
        var cut = await RenderPageAsync();
        Press(cut, PlantFile);
        Choose(cut, NativeFile(A));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("#plant-import-confirm")));
        Assert.Empty(cut.FindAll("#plant-import-plant-changed"));

        // The window is shut, the device list opened, and a cell of it is written.
        Press(cut, PlantFile);
        Press(cut, "drawio-device-list");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll($"#drawio-device-list-table tr[data-item-id='{B}'] td[data-property='MaxUnloadWeight']")));
        Act(cut, () => cut.Find($"#drawio-device-list-table tr[data-item-id='{B}'] td[data-property='MaxUnloadWeight']").DoubleClick());
        Act(cut, () => cut.Find("#drawio-device-list-editor-value").Input("250"));
        Act(cut, () => cut.Find("#drawio-device-list-editor-write").Click());
        cut.WaitForAssertion(() => Assert.Single(_host.ConfigWrites));

        cut.WaitForAssertion(() => Assert.Equal(
            "Plant file (the plant changed: the import of \"impianto.json\" has to be confirmed again)", ButtonText(cut)));
        Assert.Contains("impianto.json was compared again because the plant changed", cut.Find("#drawio-plant-file-status").TextContent);
        Assert.Empty(_host.GraphPuts);

        Press(cut, PlantFile);
        cut.WaitForAssertion(() => Assert.Contains("Nothing was written", cut.Find("#plant-import-plant-changed").TextContent));
        Act(cut, () => cut.Find("#plant-import-confirm-button").Click());

        cut.WaitForAssertion(() => Assert.Single(_host.GraphPuts));
    }

    /// <summary>
    /// Reopening the section compares again even when the page's own plant did not move: the plant
    /// can change elsewhere (another client, the engine's missions) and the page would not know.
    /// </summary>
    [Fact]
    public async Task ReopeningTheWindowComparesThePendingImportAgainWithThePlantOnTheHost()
    {
        var cut = await RenderPageAsync();
        Press(cut, PlantFile);
        Choose(cut, NativeFile(A, B));
        cut.WaitForAssertion(() => Assert.StartsWith("0 devices", cut.Find("#plant-import-lost-items").TextContent.Trim()));
        Press(cut, PlantFile);

        // Another client adds a device: this page is told nothing.
        var c = Guid.Parse("33333333-3333-3333-3333-333333333333");
        _host.Items.Add(Item(c, 900, 20));
        _host.Configs[c] = new PlantItemConfigDto { ItemId = c, Name = "C" };

        Press(cut, PlantFile);

        cut.WaitForAssertion(() => Assert.StartsWith("1 device", cut.Find("#plant-import-lost-items").TextContent.Trim()));
        Assert.Single(cut.FindAll("#plant-import-plant-changed"));
        Assert.Empty(_host.GraphPuts);
    }

    /// <summary>MINOR 4 of the review: an import being written while its section is shut is said on the line.</summary>
    [Fact]
    public async Task WhileShutTheButtonSaysAnImportIsBeingWritten()
    {
        var cut = await RenderPageAsync();
        Press(cut, PlantFile);
        Choose(cut, NativeFile(A));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("#plant-import-confirm")));

        _host.GraphPutGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = cut.InvokeAsync(() => cut.Find("#plant-import-confirm-button").Click());
        await _host.GraphPutGateReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Press(cut, PlantFile);

        Assert.Equal("Plant file (the import of \"impianto.json\" is being written...)", ButtonText(cut));
        Assert.Contains("Importing impianto.json", cut.Find("#drawio-plant-file-status").TextContent);

        _host.GraphPutGate!.SetResult();
        await held;
    }

    /// <summary>MINOR 4 of the review: a refused import, its section shut, sends the operator back to read why.</summary>
    [Fact]
    public async Task WhileShutTheButtonSaysAnImportWasNotWritten()
    {
        _host.FailRoute = "graph";
        _host.FailureStatus = System.Net.HttpStatusCode.Conflict;
        var cut = await RenderPageAsync();
        Press(cut, PlantFile);
        Choose(cut, NativeFile(A));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("#plant-import-confirm")));
        Act(cut, () => cut.Find("#plant-import-confirm-button").Click());
        cut.WaitForAssertion(() => Assert.Contains("Import refused", cut.Find("#plant-io-error").TextContent));

        Press(cut, PlantFile);

        Assert.Equal("Plant file (the import of \"impianto.json\" was not written - open to see why)", ButtonText(cut));
        Assert.Contains("impianto.json was not imported", cut.Find("#drawio-plant-file-status").TextContent);
    }

    /// <summary>
    /// Second fix round, C: a comparison made again on reopening that cannot read the plant has its
    /// own line - no "Confirm" was pressed, so it is not "not written".
    /// </summary>
    [Fact]
    public async Task WhileShutTheButtonSaysTheImportCouldNotBeComparedAgain()
    {
        var cut = await RenderPageAsync();
        Press(cut, PlantFile);
        Choose(cut, NativeFile(A));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("#plant-import-confirm")));
        Press(cut, PlantFile);

        _host.BrokenReads.Add("missions");
        Press(cut, PlantFile);
        cut.WaitForAssertion(() => Assert.Contains("could not be read again", cut.Find("#plant-io-error").TextContent));
        Press(cut, PlantFile);

        Assert.Equal(
            "Plant file (the import of \"impianto.json\" could not be compared again with the plant - open to see why)",
            ButtonText(cut));
        Assert.Contains("impianto.json could not be compared again", cut.Find("#drawio-plant-file-status").TextContent);
        Assert.Empty(_host.GraphPuts);
    }
}
