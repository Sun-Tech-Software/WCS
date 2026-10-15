using System.Text.Json;
using System.Text.Json.Nodes;
using Wcs.Api.Client;
using Xunit;
using Xunit.Abstractions;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// The plant's file on <c>/editor/drawio</c> (rows 43 and 44 of the parity table), end to end on a
/// real host and in a real browser: the file the browser really downloads, handed back to the
/// import picker, confirmed, and the plant read again from the host afterwards.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it cannot be a bUnit test.</b> Two of the three things this batch had to get right only
/// exist outside bUnit: the download the operator ends up with on his disk (a JS module and a real
/// <c>&lt;a download&gt;</c>), and the FULL page reload a confirmed import performs - which takes
/// the draw.io iframe down with it, kills the Blazor circuit, and has to come back up with the
/// imported plant drawn on the canvas.
/// </para>
/// <para>
/// <b>The free drawing is the point of the round trip.</b> The owner decided on 2026-09-19 that the
/// file is a complete backup, so the decoration travels in it - and until then it did not: whoever
/// drew by hand on draw.io exported a file without his own drawing and nobody told him. Here the
/// note is drawn through draw.io, saved, exported, WIPED from the plant, and has to come back from
/// the file.
/// </para>
/// </remarks>
public sealed class DrawioImportExportTest(DrawioEditorFixture fixture, ITestOutputHelper output) : IClassFixture<DrawioEditorFixture>
{
    private const string SeedHolder = "browser-test-import-seed";

    /// <summary>
    /// A free box of the operator's, written the way draw.io itself writes one. It is <b>not</b> a
    /// hand-waved fragment: the decoration is appended straight inside the model's &lt;root&gt;
    /// (DrawioDocument.Build), so a cell without <c>parent</c> and without its geometry makes
    /// mxGraph fail to decode the WHOLE document - measured: draw.io then drew nothing at all, not
    /// even the devices, and the failure looked like "the plant never reached the canvas".
    /// </summary>
    private const string TheOperatorsOwnBox =
        "<mxCell id=\"disegnato-prima\" value=\"un riquadro dell'operatore\" style=\"rounded=0;whiteSpace=wrap;html=1;\" " +
        "vertex=\"1\" parent=\"1\"><mxGeometry x=\"40\" y=\"600\" width=\"160\" height=\"60\" as=\"geometry\"/></mxCell>";

    private void DeclareBinary() => output.WriteLine(fixture.Host.Declaration);

    private static string ATempDirectory() =>
        Path.Combine(Path.GetTempPath(), "wcs-drawio-import", Guid.NewGuid().ToString("N"));

    /// <summary>
    /// The whole loop, the way the brief asks for it: draw something free, save, export, wipe the
    /// drawing, import the file back, and open the page again. The free drawing must be the FILE'S.
    /// </summary>
    [Fact]
    public async Task AnExportedFileBringsTheFreeDrawingBackAfterItHasBeenWiped()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);
        var directory = ATempDirectory();

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);

        // 1. The operator's own drawing, made through draw.io and written to the plant by a save.
        var note = await editor.PutAFreeNoteOnTheCanvasAsync("una nota da riportare indietro");
        await editor.SaveAsync();
        Assert.Null(await editor.SaveErrorAsync());
        var drawnOnThePlant = await client.GetPlantDecorationAsync();
        Assert.True(drawnOnThePlant.Contains($"\"{note}\"", StringComparison.Ordinal),
            $"La nota {note} non e' finita nella decorazione: il giro parte gia' da uno stato che non e' quello " +
            $"che si vuole provare.{Environment.NewLine}{drawnOnThePlant}");

        // 2. The export, through the browser's own download.
        await editor.OpenThePlantFileSectionAsync();
        var file = await editor.ExportToAFileAsync(directory);
        var exported = await File.ReadAllTextAsync(file);
        output.WriteLine($"File esportato: {file} ({exported.Length} caratteri)");
        // The identifier alone, without the quotes the decoration puts around it: inside the JSON
        // of the export the decoration is a STRING, so its own quotes come through escaped - and a
        // check written with raw quotes looks for something that cannot be there.
        Assert.True(exported.Contains(note, StringComparison.Ordinal),
            $"Il file esportato non porta la nota {note}: chi esporta un impianto disegnato a mano si " +
            $"porterebbe via un file senza il proprio disegno.{Environment.NewLine}{exported[..Math.Min(2000, exported.Length)]}");

        // 3. The drawing is taken away - which is what makes the next step a restore instead of a
        //    write that could not be told from doing nothing at all. Through the CANVAS, with the
        //    gesture the operator makes (select the note, press Canc, save), and not with a
        //    SetPlantDecoration from the API: this page holds the edit permit for sixty seconds
        //    after the save above, so an API write from here would have to sit and wait for that
        //    lease to run out - measured, it cost 59.7 s of a 60 s ceiling, a green a hair away
        //    from a red for a reason that has nothing to do with what is being proved.
        Assert.Equal(0, await editor.DeleteTheCellAsync(note));
        await editor.SaveAsync();
        Assert.Null(await editor.SaveErrorAsync());
        var wiped = await client.GetPlantDecorationAsync();
        Assert.False(wiped.Contains(note, StringComparison.Ordinal),
            $"La nota {note} e' ancora nella decorazione dopo essere stata cancellata e salvata: " +
            $"il passo che deve svuotare il disegno non l'ha svuotato.{Environment.NewLine}{wiped}");

        // 4. The file goes back in. The dialog has to SAY that this file speaks about the drawing
        //    before anything is written: the operator cannot tell one kind of file from the other
        //    by looking at its name.
        await editor.ChooseTheFileToImportAsync(file);
        var said = await editor.WhatTheImportSaysAboutTheDrawingAsync();
        Assert.Contains("carries the free drawing", said);
        output.WriteLine($"La conferma dice del disegno: {said}");

        await editor.ConfirmTheImportAndWaitForTheReloadAsync(fixture.Host.ApiKey);

        // 5. The plant, read from the host and not from the canvas.
        var afterwards = await client.GetPlantDecorationAsync();
        Assert.True(afterwards.Contains($"\"{note}\"", StringComparison.Ordinal),
            $"Dopo l'importazione la nota {note} non e' tornata nella decorazione: il file non ha " +
            $"ripristinato il disegno libero.{Environment.NewLine}{afterwards}");

        // 6. And the page really came back up with it: the reload rebuilt the document from the
        //    imported plant, the bridge re-attached, and the drawing is on the canvas.
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForTheLabelInTheDrawingAsync("una nota da riportare indietro");
    }

    /// <summary>
    /// The twin, and the case a beta tester really has: a file exported before the drawing
    /// travelled in it. The dialog says it does not speak about the drawing, and after the import
    /// the drawing on the plant is still the one that was there - nobody's work is thrown away
    /// because his file is from yesterday.
    /// <para>
    /// It is also the end-to-end answer to the permit question: this import is fired with
    /// <b>no save at all</b> before it on this page, which is the state in which nobody holds the
    /// permit - measured against this same host - and in which <c>PUT /plant/graph</c> used to come
    /// back 423.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AFileWithoutTheDrawingLeavesTheDrawingAloneAndStillImportsRightAfterOpening()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);
        var directory = ATempDirectory();

        // The plant carries a drawing that the file about to be imported knows nothing about.
        await PlantSeed.WaitForTheEditPermitToBeFreeAsync(client, SeedHolder);
        client.EditHolder = SeedHolder;
        await client.AcquireEditLockAsync(SeedHolder);
        await client.SetPlantDecorationAsync(TheOperatorsOwnBox);
        await client.ReleaseEditLockAsync(SeedHolder);
        client.EditHolder = null;

        // A file the way the export made it BEFORE 2026-09-19: the same body, with the fifth field
        // taken out - not set to null, absent. Built from a real export so that everything else in
        // it is exactly what this host produces.
        var exported = JsonSerializer.Serialize(await client.GetPlantGraphAsync());
        var stripped = JsonNode.Parse(exported)!.AsObject();
        Assert.True(stripped.Remove("Decoration"), $"L'esportazione non portava il campo Decoration: {exported[..Math.Min(500, exported.Length)]}");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "impianto-di-ieri.json");
        await File.WriteAllTextAsync(file, stripped.ToJsonString());

        var itemsBefore = (await client.GetPlantGraphAsync()).Items.Select(i => i.ItemId).OrderBy(i => i).ToList();

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);

        // No save, no other write: straight to the import, which is the state the permit question
        // was about.
        await editor.OpenThePlantFileSectionAsync();
        await editor.ChooseTheFileToImportAsync(file);
        var said = await editor.WhatTheImportSaysAboutTheDrawingAsync();
        Assert.Contains("says nothing", said);
        output.WriteLine($"La conferma dice del disegno: {said}");

        await editor.ConfirmTheImportAndWaitForTheReloadAsync(fixture.Host.ApiKey);

        Assert.Null(await editor.ImportErrorAsync());
        Assert.Equal(TheOperatorsOwnBox,
            await client.GetPlantDecorationAsync());

        // The identifiers survive the round trip - which is what keeps every marked cell of the
        // drawing pointing at a device that still exists.
        var itemsAfter = (await client.GetPlantGraphAsync()).Items.Select(i => i.ItemId).OrderBy(i => i).ToList();
        Assert.Equal(itemsBefore, itemsAfter);

        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);
    }

    /// <summary>
    /// An old WinForms drawing (<c>.edd</c>) picked right after the page opened - nobody holds the
    /// edit permit - shows the confirmation with the report of what was read, and once confirmed
    /// the device sits where the old drawing put it. Picking the file only READS it: the host parses
    /// the drawing and writes nothing, so the pick must not depend on a permit this page takes only
    /// when it writes.
    /// </summary>
    [Fact]
    public async Task AnOldDrawingPickedRightAfterOpeningShowsTheConfirmationAndMovesTheDevice()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);
        await PlantSeed.WaitForTheEditPermitToBeFreeAsync(client, SeedHolder);
        var directory = ATempDirectory();
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "vecchio-disegno.edd");
        await File.WriteAllBytesAsync(file, OldDrawing(PlantSeed.DraggedId, x: 321, y: 234, width: 120, height: 80));

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);

        // No save, no other write: straight to the pick.
        await editor.OpenThePlantFileSectionAsync();
        try
        {
            await editor.ChooseTheFileToImportAsync(file);
        }
        catch
        {
            output.WriteLine($"Quello che la pagina ha detto invece della conferma: {await editor.ImportErrorAsync() ?? "(niente)"}");
            throw;
        }
        var dialog = await editor.TheImportDialogAsync();
        output.WriteLine($"La conferma: {dialog}");
        Assert.Contains("Read from the old drawing: 1 device placed", dialog);

        await editor.ConfirmTheImportAndWaitForTheReloadAsync(fixture.Host.ApiKey);

        Assert.Null(await editor.ImportErrorAsync());
        var moved = await PlantSeed.ReadGeometryAsync(client, PlantSeed.DraggedId);
        Assert.Equal(321d, moved.X);
        Assert.Equal(234d, moved.Y);
        // The other device is not named by the drawing, so it keeps its position.
        var untouched = await PlantSeed.ReadGeometryAsync(client, PlantSeed.UntouchedId);
        Assert.Equal(PlantSeed.UntouchedGeometry.X, untouched.X);
        Assert.Equal(PlantSeed.UntouchedGeometry.Y, untouched.Y);
    }

    /// <summary>
    /// The owner's decision of 27/9 moved the plant's file into the strip of sections, and since 29/9
    /// each section opens in a floating window that is HIDDEN when shut, not removed - so the import's
    /// confirmation, a dialog inside the window, waits hidden with it. What this measures is what only
    /// a real browser can say: that a confirmation left waiting inside the shut window is really
    /// HIDDEN (it is a fixed dialog with a backdrop over the whole window, and the hidden window has to
    /// take both away), that the page under it can be clicked again, that the button of the strip says
    /// an import is waiting, and that opening the window again brings the same confirmation back, still
    /// cancellable. The window is shut with Esc, the focus being in it - the one gesture that reaches it:
    /// while the confirmation is up, its backdrop takes every click, on the window's X and on the
    /// strip's button too. (Before the windows it was shut by opening another section from the
    /// keyboard: opening another section no longer shuts it.)
    /// </summary>
    [Fact]
    public async Task APendingImportIsHiddenWithItsWindowAndComesBackWhenItIsOpenedAgain()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);
        var directory = ATempDirectory();
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "impianto.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(await client.GetPlantGraphAsync()));

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);

        await editor.OpenThePlantFileSectionAsync();
        await editor.ChooseTheFileToImportAsync(file);
        Assert.True(await editor.IsTheImportConfirmationVisibleAsync(),
            "The import's confirmation is in the page but not on screen, with its window open.");

        await editor.ShutTheWindowWithEscapeAsync("drawio-plant-file");

        Assert.False(await editor.IsTheImportConfirmationVisibleAsync(),
            "The import's confirmation is still on screen with its window shut.");
        Assert.Equal("Plant file (an import is waiting for confirmation: \"impianto.json\")",
            await editor.PlantFileSummaryAsync());
        // The backdrop went with the dialog: a click reaches the page again. Were it still spread over
        // the window, this click would land on it - and cancel the import.
        await editor.OpenASectionAsync("drawio-junction-list");

        // The sentence is on screen for whoever looks for it (passata grafica, 29/9: the button shows its
        // name and a "!", the sentence is behind the tip): not while the mouse and the focus are
        // elsewhere, and under the mouse on the button.
        Assert.Null(await editor.TipOnScreenAsync("#drawio-plant-file-toggle"));
        Assert.Equal("Plant file (an import is waiting for confirmation: \"impianto.json\")",
            await editor.TipUnderTheMouseAsync("#drawio-plant-file-toggle"));

        await editor.OpenThePlantFileSectionAsync();
        Assert.True(await editor.IsTheImportConfirmationVisibleAsync(),
            "The window was opened again and the import's confirmation did not come back on screen.");
        Assert.Contains("impianto.json", await editor.TheImportDialogAsync());

        await editor.CancelTheImportAsync();
        Assert.Equal("Plant file", await editor.PlantFileSummaryAsync());
        Assert.Null(await editor.ImportErrorAsync());
    }

    /// <summary>
    /// The confirmation replaces the whole plant, so while it waits it must cover the whole page -
    /// every window included - and take the operator's clicks for itself. Since 29/9 it lives inside
    /// the Plant file window, and a fixed window with a z-index is a stacking context: its 950/951
    /// counted only inside that window, and another window brought to the front (here Operations,
    /// opened from the keyboard - the backdrop takes every click, the keyboard is how an operator
    /// gets past it) came above the backdrop and the dialog, its buttons live while the replace was
    /// waiting. Before the import waits, Operations is moved with the operator's real title-bar
    /// gesture to the place where the confirmation's buttons will be: the test says so before it
    /// looks at what the buttons' centres hit.
    /// After Cancel the Plant file window must stack by the module's value again, not stay lifted.
    /// </summary>
    [Fact]
    public async Task AWindowBroughtToTheFrontFromTheKeyboardStaysUnderAPendingImport()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);
        var directory = ATempDirectory();
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "impianto.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(await client.GetPlantGraphAsync()));

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);

        // Put the compact Operations window where the later, viewport-centred confirmation's
        // buttons will be, while the page is still reachable by the mouse. The pending import's
        // full-screen backdrop intentionally prevents moving any other window afterwards.
        await editor.OpenWindowAsync("drawio-operations");
        await editor.DragTheWindowToAsync("drawio-operations", 800, 600);
        await editor.ClickAsync("#drawio-operations-close");

        await editor.OpenThePlantFileSectionAsync();
        await editor.ChooseTheFileToImportAsync(file);
        Assert.True(await editor.IsTheImportConfirmationVisibleAsync(),
            "The import's confirmation is in the page but not on screen, with its window open.");

        var presses = await editor.OpenASectionFromTheKeyboardAsync("drawio-operations");
        output.WriteLine($"Operations opened from the keyboard after {presses} presses of Tab.");
        output.WriteLine($"Plant file window: {await editor.WindowStackingAsync("drawio-plant-file")}; " +
                         $"Operations window: {await editor.WindowStackingAsync("drawio-operations")}");

        var hits = await editor.WhatTheImportButtonsHitAsync("drawio-operations");
        foreach (var hit in hits) output.WriteLine(hit.ToString());
        Assert.Equal(2, hits.Count);
        Assert.All(hits, hit => Assert.True(hit.UnderTheOtherWindow,
            $"The test is blind: the Operations window does not lie over the confirmation's button - {hit}"));
        Assert.All(hits, hit => Assert.True(hit.InTheDialog,
            $"A window brought to the front covers the pending import's confirmation: {hit}"));

        await editor.CancelTheImportAsync();
        var plantFile = await editor.WindowStackingAsync("drawio-plant-file");
        Assert.True(plantFile.Inline == plantFile.Computed,
            $"With no import pending the Plant file window must stack by the module's value again: {plantFile}");
        Assert.Null(await editor.ImportErrorAsync());
    }

    /// <summary>
    /// The rule above holds for a window taken out into a separate browser window too: the editor's
    /// backdrop cannot cover it, so while an import waits for its confirmation the separate window
    /// takes nothing - not a click on Operations' tabs, not a key - and says where the confirmation
    /// is. The events a forwarded click would have produced travel before the import's Cancel on the
    /// same circuit, so once the Cancel is rendered they would be rendered too. And the Plant file
    /// window, which holds the confirmation, cannot be taken out of the editor while it waits.
    /// </summary>
    [Fact]
    public async Task ADetachedWindowTakesNothingWhileAnImportWaitsAndThePlantFileWindowStaysInTheEditor()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);
        var directory = ATempDirectory();
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "impianto.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(await client.GetPlantGraphAsync()));

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.OpenWindowAsync("drawio-operations");
        var popup = await editor.DetachWindowAsync("drawio-operations");

        await editor.OpenThePlantFileSectionAsync();
        await editor.ChooseTheFileToImportAsync(file);
        Assert.True(await editor.IsTheImportConfirmationVisibleAsync(),
            "The import's confirmation is in the page but not on screen, with its window open.");

        // A real click where Remove all LU's tab is, and Enter on the connection mode's switch.
        var tab = await popup.Locator("#drawio-operations-tab-remove-all").BoundingBoxAsync()
            ?? throw new InvalidOperationException("The Remove all LU tab has no box in the separate window.");
        await popup.Mouse.ClickAsync(tab.X + tab.Width / 2, tab.Y + tab.Height / 2);
        await popup.Locator("#drawio-mode-toggle").FocusAsync();
        await popup.Keyboard.PressAsync("Enter");

        await editor.CancelTheImportAsync();
        await popup.Locator(".drawio-popout-blocker").WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Hidden });
        Assert.Equal("true", await popup.Locator("#drawio-operations-tab-mode").GetAttributeAsync("aria-selected"));
        Assert.Equal("false", await popup.Locator("#drawio-operations-tab-remove-all").GetAttributeAsync("aria-selected"));
        Assert.Equal(0, await popup.Locator("#drawio-mode-question").CountAsync());

        // Waiting again - another file: the input already holds the first one, and choosing it again
        // is no change - the separate window says so, and the Plant file window cannot leave.
        var again = Path.Combine(directory, "impianto-2.json");
        File.Copy(file, again);
        await editor.ChooseTheFileToImportAsync(again);
        var cover = popup.Locator(".drawio-popout-blocker");
        await cover.WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Visible });
        Assert.Contains("import is waiting for confirmation in the editor window", await cover.InnerTextAsync());
        await editor.PressOnAsync("#drawio-plant-file-popout", "Enter");
        Assert.Contains("import is waiting for confirmation", await editor.PopoutStatusAsync("drawio-plant-file"));
        Assert.Equal(1, await editor.ElementCountAsync("#drawio-plant-file"));
        Assert.True(await editor.IsTheImportConfirmationVisibleAsync(),
            "Pressing the Plant file window's pop-out took the import's confirmation off the editor.");

        await editor.CancelTheImportAsync();
        await cover.WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Hidden });
        Assert.Null(await editor.ImportErrorAsync());
    }

    /// <summary>
    /// The plant file's buttons and the import's confirmation wear the page's one look (passata grafica,
    /// fix round of task 4): Export, Import and Cancel are the page's plain buttons - corners of 6 px,
    /// grey under the mouse, a darker grey pressed - Confirm, which replaces the whole plant, is the red
    /// one, and the confirmation's box has the corners of the page's other boxes over the page (the
    /// engine's confirmation, the notes behind a "?"). Until then Confirm was white with a blue border,
    /// Cancel and Confirm did not answer the mouse at all (a rule of their own outweighed the page's
    /// states), and Export and Import kept the old ribbon's corners of 3 px and turned solid blue while
    /// pressed. Read off what the browser paints; every press is let go elsewhere, so none of them is a
    /// click - and the confirmation is still waiting at the end, for Cancel.
    /// </summary>
    [Fact]
    public async Task ThePlantFilesButtonsAndTheImportsConfirmationWearThePagesLook()
    {
        DeclareBinary();
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);
        var directory = ATempDirectory();
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "impianto.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(await client.GetPlantGraphAsync()));

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.OpenThePlantFileSectionAsync();

        const string windowName = "#drawio-plant-file .drawio-window-name";
        DrawioLook.AssertThePagesPlainButton(await editor.ButtonLookAsync("#plant-export-button", windowName), output);
        DrawioLook.AssertThePagesPlainButton(await editor.ButtonLookAsync("#plant-import-button", windowName), output);

        await editor.ChooseTheFileToImportAsync(file);
        const string dialogTitle = "#plant-import-confirm .plant-import-title";
        DrawioLook.AssertThePagesPlainButton(await editor.ButtonLookAsync("#plant-import-cancel-button", dialogTitle), output);
        var confirm = await editor.ButtonLookAsync("#plant-import-confirm-button", dialogTitle);
        output.WriteLine(confirm.ToString());
        Assert.True(confirm is { Corners: "6px", Rest: DrawioLook.Red, RestText: DrawioLook.White, Hover: DrawioLook.RedHover, Pressed: DrawioLook.RedPressed, PressedText: DrawioLook.White },
            $"The import's Confirm is not the page's red button: {confirm}");
        var dialogCorners = await editor.CornersOfAsync("#plant-import-confirm");
        Assert.True(dialogCorners == "8px", $"The import's confirmation has corners of {dialogCorners}, not the 8 px of the page's boxes over the page.");

        // None of the presses was a click. The dialog still on screen says little by itself - a click on
        // its way through the circuit would not be on screen yet. What says it is Cancel: pressed now, it
        // is taken after any click made before it, and it finds its button and ends the confirmation only
        // if no earlier click on Cancel ended it already, and on the page it was opened on only if no
        // click on Confirm replaced the plant and reloaded the page.
        Assert.True(await editor.IsTheImportConfirmationVisibleAsync(), "The import's confirmation went away while its buttons were being looked at.");
        await editor.CancelTheImportAsync();
        Assert.Equal("Plant file", await editor.PlantFileSummaryAsync());
        Assert.Null(await editor.ImportErrorAsync());
    }

    /// <summary>
    /// A minimal drawing of the WinForms Editor with one device, in the shape
    /// <c>DiagramGeometryReader</c> reads (the same one the host's <c>GraphEndpointsTest</c> uses):
    /// the DataContract namespace, <c>OrchestraType=ITEM</c>, the device's Guid in <c>Tag</c>, and a
    /// <c>BoundingRect</c> with its lower-case children in alphabetical order.
    /// </summary>
    private static byte[] OldDrawing(Guid itemId, double x, double y, double width, double height)
    {
        const string ns = "http://schemas.datacontract.org/2004/07/Syncfusion.Windows.Forms.Diagram";
        const string arraysNs = "http://schemas.microsoft.com/2003/10/Serialization/Arrays";
        var i = System.Globalization.CultureInfo.InvariantCulture;
        return System.Text.Encoding.UTF8.GetBytes(
            $"""
             <DiagramDocumentWrapper xmlns="{ns}" xmlns:i="http://www.w3.org/2001/XMLSchema-instance"
                 xmlns:z="http://schemas.microsoft.com/2003/10/Serialization/" xmlns:a="{arraysNs}">
               <Model><Nodes>
                 <Node>
                   <PropertyBag>
                     <a:KeyValueOfstringanyType>
                       <a:Key>OrchestraType</a:Key>
                       <a:Value i:type="a:string">ITEM</a:Value>
                     </a:KeyValueOfstringanyType>
                   </PropertyBag>
                   <Tag>{itemId}</Tag>
                   <BoundingRect xmlns:a="http://schemas.datacontract.org/2004/07/System.Drawing"><a:height>{height.ToString(i)}</a:height><a:width>{width.ToString(i)}</a:width><a:x>{x.ToString(i)}</a:x><a:y>{y.ToString(i)}</a:y></BoundingRect>
                 </Node>
               </Nodes></Model>
             </DiagramDocumentWrapper>
             """);
    }
}
