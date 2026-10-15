using System.ComponentModel;
using Wcs.Api.Contracts.Dtos;
using Xunit;
using Xunit.Abstractions;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// Batches B and C of the properties panel, judged the way they will be used: the operator clicks a
/// device inside draw.io and <b>the panel next to the drawing shows that device's configuration</b>
/// - and, since batch C, lets it be changed.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the test that is worth the batch.</b> None of the chain it exercises exists in bUnit:
/// draw.io, its selection model, the plugin entry point the bridge subscribes through, the Blazor
/// circuit that carries the identifiers back, the real HTTP read of the configuration from a real
/// host, and the sixty fields the shared <c>PropertyPanel</c> renders out of the contract. The bUnit
/// tests measure what the page does with identifiers and answers it is handed; this one measures
/// that a click produces a panel with the plant's own values in it.
/// </para>
/// <para>
/// <b>The disappearances are not an extra.</b> A panel that shows the right sixty values and then
/// keeps showing them when the selection is no longer that device is the defect this branch produces
/// most often, and it would pass every test that only ever selects. Hence the two gestures at the
/// end: a second device selected alongside the first, and a real click on the empty canvas.
/// </para>
/// <para>
/// Different from <see cref="DrawioSelectionLineTest"/>, which guards the LINE above the drawing:
/// that one keeps telling the truth in exactly the cases where this panel says nothing at all, and
/// the two fail for different reasons.
/// </para>
/// </remarks>
public sealed class DrawioPropertyPanelTest(DrawioEditorFixture fixture, ITestOutputHelper output) : IClassFixture<DrawioEditorFixture>
{
    private const string SeedHolder = "browser-test-property-panel";

    /// <summary>Somebody else holding the edit permit: the host then answers 409 to the page, which is the refusal batch C has to survive without losing the operator's typing.</summary>
    private const string OtherHolder = "browser-test-somebody-else";

    [Fact]
    public async Task OneArrowRightImmediatelyShrinksADockClampedByTheViewport()
    {
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);
        await using var editor = await DrawioEditorPage.OpenInAWindowOfAsync(fixture.Browser, fixture.Host, 1366, 768);
        await editor.SetPropertyDockMemoryAsync("560");
        await editor.ReloadAndPassTheKeyGateAsync(fixture.Host.ApiKey);
        await editor.WaitForPropertyDockWidthAsync(560);
        await editor.SetViewportAsync(900, 768);
        await editor.WaitForPropertyDockWidthAsync(420);

        await editor.FocusPropertyDockResizerAsync();
        await editor.PressAsync("ArrowRight");

        await editor.WaitForPropertyDockWidthAsync(404);
        Assert.Equal("404", await editor.PropertyDockMemoryAsync());
        await editor.SetViewportAsync(1366, 768);
        await editor.WaitForPropertyDockWidthAsync(404);
    }

    [Fact]
    public async Task TheDockResizesByPointerAndKeyboardRemembersItsWidthAndResets()
    {
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);
        await using var editor = await DrawioEditorPage.OpenInAWindowOfAsync(fixture.Browser, fixture.Host, 1366, 768);
        Assert.InRange(Math.Abs(await editor.PropertyDockWidthAsync() - 320), 0, 1);
        await editor.DragPropertyDockByAsync(160);
        await editor.WaitForPropertyDockWidthAsync(480);
        Assert.InRange(Math.Abs(double.Parse((await editor.PropertyDockMemoryAsync())!, System.Globalization.CultureInfo.InvariantCulture) - 480), 0, 1);
        await editor.ReloadAndPassTheKeyGateAsync(fixture.Host.ApiKey);
        await editor.WaitForPropertyDockWidthAsync(480);
        await editor.FocusPropertyDockResizerAsync();
        await editor.PressAsync("ArrowLeft");
        await editor.WaitForPropertyDockWidthAsync(496);
        await editor.PressAsync("End");
        await editor.WaitForPropertyDockWidthAsync(560);
        await editor.SetViewportAsync(900, 768);
        await editor.WaitForPropertyDockWidthAsync(420);
        await editor.SetViewportAsync(1366, 768);
        await editor.WaitForPropertyDockWidthAsync(560);
        await editor.DoubleClickPropertyDockResizerAsync();
        await editor.WaitForPropertyDockWidthAsync(320);
        await editor.SetPropertyDockMemoryAsync("900");
        await editor.SetViewportAsync(700, 768);
        await editor.ReloadAndPassTheKeyGateAsync(fixture.Host.ApiKey);
        await editor.WaitForPropertyDockWidthAsync(320);
        await editor.SetPropertyDockMemoryAsync("wide");
        await editor.SetViewportAsync(1366, 768);
        await editor.ReloadAndPassTheKeyGateAsync(fixture.Host.ApiKey);
        await editor.WaitForPropertyDockWidthAsync(320);
    }

    [Fact]
    public async Task StorageDenialKeepsTheDockAndPropertyFieldsUsable()
    {
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);
        await using var editor = await DrawioEditorPage.OpenWithInitScriptAsync(fixture.Browser, fixture.Host,
            // The dock runs in the top frame. Applying this to draw.io's independent iframe also
            // breaks its vendor plugin loader before selection reaches the property panel.
            "if (window === window.top) { Storage.prototype.getItem = () => { throw new DOMException('storage denied'); }; Storage.prototype.setItem = () => { throw new DOMException('storage denied'); }; }");
        Assert.True(await editor.PropertyDockStorageIsDeniedAsync());
        await editor.WaitForPropertyDockWidthAsync(320);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.SelectDevicesAsync(PlantSeed.DraggedName);
        await editor.WaitForPropertyAsync("Name", PlantSeed.DraggedName);
        Assert.Contains("Name", await editor.WriteablePropertyFieldsAsync());
        await editor.TypePropertyAsync("Name", "StillEditable");
        await editor.WaitForPropertyAsync("Name", "StillEditable");
        await editor.FocusPropertyDockResizerAsync();
        await editor.PressAsync("ArrowLeft");
        await editor.WaitForPropertyDockWidthAsync(336);
    }

    [Theory]
    [InlineData("100", 320)]
    [InlineData("900", 560)]
    [InlineData("NaN", 320)]
    [InlineData("wide", 320)]
    public async Task AStoredDockWidthIsClampedOrRejected(string stored, double expected)
    {
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);
        await using var editor = await DrawioEditorPage.OpenInAWindowOfAsync(fixture.Browser, fixture.Host, 1366, 768);
        await editor.SetPropertyDockMemoryAsync(stored);
        await editor.ReloadAndPassTheKeyGateAsync(fixture.Host.ApiKey);
        await editor.WaitForPropertyDockWidthAsync(expected);
        Assert.InRange(Math.Abs(await editor.PropertyDockWidthAsync() - expected), 0, 1);
    }

    [Fact]
    public async Task ThePropertyDescriptionStaysAtTheBottomAndFollowsKeyboardFocus()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenInAWindowOfAsync(fixture.Browser, fixture.Host, 1366, 768);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.SelectDevicesAsync(PlantSeed.DraggedName);
        await editor.WaitForPropertyAsync("Name", PlantSeed.DraggedName);

        Assert.Equal("Select a property to see what it controls.",
            (await editor.TextOfAsync("#drawio-property-description-body"))?.Trim());

        await editor.ClickAsync("[data-property='Name'] input");
        await editor.WaitForTextAsync("#drawio-property-description-title", "Name");
        var expected = TypeDescriptor.GetProperties(typeof(PlantItemConfigDto))[nameof(PlantItemConfigDto.Name)]!.Description;
        Assert.Equal(expected, (await editor.TextOfAsync("#drawio-property-description-body"))?.Trim());

        await editor.PressAsync("Tab");
        var focusedProperty = await editor.FocusedPropertyAsync();
        Assert.NotNull(focusedProperty);
        Assert.NotEqual(nameof(PlantItemConfigDto.Name), focusedProperty);
        var focused = TypeDescriptor.GetProperties(typeof(PlantItemConfigDto))[focusedProperty!]!;
        await editor.WaitForTextAsync("#drawio-property-description-title", focused.DisplayName);
        Assert.Equal(focused.Description, (await editor.TextOfAsync("#drawio-property-description-body"))?.Trim());

        var layout = await editor.PropertyDescriptionLayoutAfterScrollingAsync();
        output.WriteLine($"Property description layout: {string.Join(", ", layout)}");
        Assert.True(layout[3] > 0, $"The properties did not scroll: {string.Join(", ", layout)}");
        Assert.True(Math.Abs(layout[0] - layout[4]) <= 1,
            $"The footer moved with the scrolling fields: {string.Join(", ", layout)}");
        Assert.True(layout[1] <= layout[2] + 1,
            $"The footer falls outside the dock: {string.Join(", ", layout)}");
    }

    [Fact]
    public async Task ClickingADeviceShowsItsConfigurationReadOnlyAndLeavingItTakesThePanelAway()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);

        // Nobody has clicked: no panel at all - not an empty one, which would read as "this plant
        // has nothing to configure".
        await editor.WaitForSelectionLineAsync("Nothing selected.");
        Assert.Null(await editor.PropertyPanelNoticeAsync());

        // One device: its configuration, read from the host. Name AND Asi, because they are two
        // different properties of two different categories - a panel that filled every field from
        // the plant item it already had would get Name right and Asi wrong.
        await editor.SelectDevicesAsync(PlantSeed.DraggedName);
        await editor.WaitForPropertyAsync("Name", PlantSeed.DraggedName);
        await editor.WaitForPropertyAsync("Asi", PlantSeed.DraggedName);

        // And the sixty properties really arrive grouped as the contract declares them: this is the
        // shared component doing the work, not a handful of fields written into this page.
        var categories = await editor.PropertyCategoriesAsync();
        Assert.Contains("Basic Option", categories);
        Assert.Contains("Device Option", categories);
        Assert.True(
            categories.Count > 2,
            $"The panel shows {categories.Count} categories ({string.Join(", ", categories)}): it is not showing the contract.");

        // Since batch C the panel is WRITABLE, and both halves are checked: the note names its
        // subject and says when the permit is asked for, and the fields really are live in the DOM -
        // measured field by field, not deduced from the parameter the page hands the component.
        var notice = await editor.PropertyPanelNoticeAsync();
        Assert.NotNull(notice);
        Assert.Contains(PlantSeed.DraggedName, notice);
        Assert.Contains("\"Save\"", notice);
        var open = await editor.WriteablePropertyFieldsAsync();
        Assert.Contains("Name", open);
        Assert.Contains("save", open);

        // Another device: the panel follows the selection instead of staying on the first one.
        await editor.SelectDevicesAsync(PlantSeed.UntouchedName);
        await editor.WaitForPropertyAsync("Name", PlantSeed.UntouchedName);
        Assert.Contains(PlantSeed.UntouchedName, (await editor.PropertyPanelNoticeAsync())!);

        // Two devices: the panel shows one at a time, so it shows none - and the line above goes on
        // saying what is really selected.
        await editor.SelectDevicesAsync(PlantSeed.DraggedName, PlantSeed.UntouchedName);
        await editor.WaitForNoPropertyPanelAsync();
        await editor.WaitForSelectionLineAsync("Selected: 2 cells (2 devices of the plant).");

        // And the gesture that matters: a real click on the empty canvas, after which no device's
        // properties may stay on screen.
        await editor.ClickEmptyCanvasAsync(PlantSeed.DraggedName, PlantSeed.UntouchedName);
        await editor.WaitForNoPropertyPanelAsync();
        await editor.WaitForSelectionLineAsync("Nothing selected.");
    }

    /// <summary>
    /// The panel's actions are one row of buttons (passata grafica, fix round of task 4): with the engine
    /// running a device has "Duplicate device" and the four commands, side by side in a row that wraps -
    /// "Duplicate device" and the first command on the same line, the five on two lines of the column,
    /// and nothing else taking a line of the row (the commands' "Commands" heading is there for a screen
    /// reader, not on a line of its own). The first look at it, at this very size, had four lines:
    /// Duplicate alone, a "COMMANDS" line, and the four commands on two more. bUnit sees the order of the
    /// actions; whether they are a row is the browser's to say.
    /// </summary>
    [Fact]
    public async Task WithTheEngineRunningThePanelsActionsAreOneRowOfButtons()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        try
        {
            await using var editor = await DrawioEditorPage.OpenInAWindowOfAsync(fixture.Browser, fixture.Host, 1366, 768);
            await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
            await editor.ClickAsync("#drawio-engine-start");
            await editor.ClickAsync("#drawio-engine-confirm");
            await editor.WaitForTextAsync("#drawio-engine-state", "Engine: Run");

            await editor.SelectDevicesAsync(PlantSeed.DraggedName);
            await editor.WaitForTextAsync("#drawio-command-step-done", "Step Done");

            var row = await editor.PanelActionsAsync();
            output.WriteLine($"The panel's actions: {row}");
            Assert.True(row.Found, "The panel has no row of actions (#drawio-property-panel > .drawio-property-actions).");
            Assert.Equal(
                ["drawio-duplicate-ask", "drawio-command-execute-step", "drawio-command-unload-lu", "drawio-command-offline", "drawio-command-step-done"],
                row.Buttons.Select(button => button.Id));
            Assert.True(Math.Abs(row.Buttons[0].Top - row.Buttons[1].Top) <= 1,
                $"\"Duplicate device\" and the first command are not on one line: {row}");
            Assert.True(row.Lines <= 2, $"The five buttons take {row.Lines} lines of the column, not a row that wraps once: {row}");
            // Nothing but the buttons takes room in the row: its top is the first line's, its bottom the
            // last button's. A heading on a line of its own, above the commands, would push them down.
            Assert.True(Math.Abs(row.Top - row.Buttons.Min(button => button.Top)) <= 1
                        && Math.Abs(row.Bottom - row.Buttons.Max(button => button.Bottom)) <= 1,
                $"Something other than the buttons takes a line of the row: {row}");
        }
        finally
        {
            await TestHostEngine.LeaveStoppedAsync(client, output);
        }
    }

    /// <summary>
    /// <b>The test that is worth batch C</b>: a value changed in the panel and confirmed is on the
    /// PLANT, read back from the host with the real client and not from the page that wrote it - and
    /// a rename reaches the drawing, so the canvas and the plant go on saying the same thing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// None of this chain exists in bUnit: draw.io's selection, the Blazor circuit, the permit asked
    /// for at the confirmation against a real host, the PUT on the configuration route, and - the
    /// half no unit test can reach - draw.io redrawing the label of a cell it holds in its own
    /// model, inside an iframe, on a document nobody sends again after the seeding.
    /// </para>
    /// <para>
    /// The read-back is the point. A page that showed the new values without writing them, or that
    /// wrote them to the wrong device, would pass anything that only looked at the screen.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AValueChangedInThePanelIsOnThePlantAndARenameReachesTheDrawing()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);

        await editor.SelectDevicesAsync(PlantSeed.DraggedName);
        await editor.WaitForPropertyAsync("Name", PlantSeed.DraggedName);

        // THE PROMISE OF THIS BATCH, against a real host and a real 409: the permit is somebody
        // else's, the write is refused - and what was typed is STILL in the field. Nothing on the
        // plant and nothing on the drawing changed either.
        const string refused = "NeverReachesThePlant";
        await client.AcquireEditLockAsync(OtherHolder);
        await editor.TypePropertyAsync("Name", refused);
        await editor.ConfirmPropertiesAsync();
        await editor.WaitForPropertyWriteErrorAsync("has been editing the plant");
        await editor.WaitForPropertyAsync("Name", refused);
        Assert.Equal(PlantSeed.DraggedName, (await client.GetItemConfigurationAsync(PlantSeed.DraggedId))!.Name);
        await editor.WaitForSelectionLineAsync($"Selected: {PlantSeed.DraggedName}");

        // The permit goes back, and the panel starts again from the name the plant really has.
        await client.ReleaseEditLockAsync(OtherHolder);
        await editor.TypePropertyAsync("Name", PlantSeed.DraggedName);

        // A property that is NOT the name and is not on the drawing at all: it proves the write goes
        // through the configuration and not through some shortcut of the canvas. The value is one no
        // default would produce.
        const string delayBlind = "4321";
        await editor.TypePropertyAsync("DelayBlind", delayBlind);
        await editor.ConfirmPropertiesAsync();
        await editor.WaitForPropertiesWrittenAsync(PlantSeed.DraggedName);
        Assert.Null(await editor.PropertyWriteErrorAsync());

        // And the line is DRAWN as the stylesheet says it is - measured in the browser, because a
        // rule that never reaches the page is invisible to everything else here: a comment closed
        // halfway had already made this whole block invalid once, and the answer to the button went
        // back to being squeezable with no separator above it.
        var (borderTop, flexShrink) = await editor.PropertyOutcomeStyleAsync();
        Assert.Equal("1px", borderTop);
        Assert.Equal("0", flexShrink);

        var onThePlant = await client.GetItemConfigurationAsync(PlantSeed.DraggedId);
        Assert.NotNull(onThePlant);
        Assert.Equal(int.Parse(delayBlind), onThePlant.DelayBlind);
        // And the device nobody touched was not rewritten along with it.
        var witness = await client.GetItemConfigurationAsync(PlantSeed.UntouchedId);
        Assert.NotNull(witness);
        Assert.Equal(PlantSeed.UntouchedName, witness.Name);

        // Now the rename, which is the one change the drawing has to follow.
        const string renamed = "RenamedDevice";
        await editor.TypePropertyAsync("Name", renamed);
        await editor.ConfirmPropertiesAsync();
        await editor.WaitForPropertiesWrittenAsync(renamed);
        Assert.Null(await editor.PropertyWriteErrorAsync());

        Assert.Equal(renamed, (await client.GetItemConfigurationAsync(PlantSeed.DraggedId))!.Name);

        // The canvas: the shape is found by the label draw.io DRAWS, so this waits for the new label
        // to really be on the drawing - not for a field on our side of the page.
        await editor.WaitForDeviceAsync(renamed);
        // And the line above the drawing, which reads the plant the page holds: it follows too, or
        // the page would be naming a device by a name the plant no longer has.
        await editor.WaitForSelectionLineAsync($"Selected: {renamed}");
    }

    /// <summary>
    /// Row 29 of the queue, against the real host: this fixture's host has a warehouse address nobody
    /// answers at (<see cref="WcsHostProcess"/>), so the cells of the Automation device come back as the
    /// host's own 503 - the problem document <c>EngineExceptionHandler</c> writes, not a copy of it, as
    /// the bUnit test has. The panel says it in one plain sentence; the "?" at its right opens the
    /// host's answer, trace id included, whole inside the browser's window, with none of its text
    /// running past the note.
    /// </summary>
    [Fact]
    public async Task AWarehouseThatDoesNotAnswerIsOnePlainSentenceWithTheHostsAnswerBehindItsQuestionMark()
    {
        output.WriteLine(fixture.Host.Declaration);
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder);

        await using var editor = await DrawioEditorPage.OpenInAWindowOfAsync(fixture.Browser, fixture.Host, 1366, 768);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.OpenTheCellsAsync(PlantSeed.DraggedName);
        await editor.WaitForShownAsync("#drawio-cells-error", "the sentence of the cells that could not be read");

        var said = await editor.TextContentOfAsync("#drawio-cells-error");
        output.WriteLine($"The sentence: {said}");
        Assert.NotNull(said);
        Assert.Equal(
            "The cells of this device are not shown: the warehouse's cell list could not be read because the warehouse is not reachable from the engine; nothing here has been changed.",
            said);
        Assert.DoesNotContain("{", said);
        Assert.DoesNotContain("503", said);
        await editor.LookAtItAsync("voce-29-cells-error-sentence");

        await editor.ClickAsync("#drawio-cells-error-help");
        await editor.WaitForTheHelpAsync("drawio-cells-error-help", open: true, "after a click on it");
        var answer = await editor.TextContentOfAsync("#drawio-cells-error-detail");
        output.WriteLine($"Behind the \"?\": {answer}");
        Assert.NotNull(answer);
        Assert.Contains("the host answered 503: {", answer);
        Assert.Contains("\"traceId\":", answer);
        var note = await editor.NoteOnScreenAsync("drawio-cells-error-help");
        output.WriteLine(note.ToString());
        Assert.True(note.Inside && note.StartReached && note.EndReached, $"The host's answer is not whole on screen: {note}.");
        Assert.False(await editor.IsCutShortAsync("#drawio-cells-error-detail"),
            $"The host's answer runs past its note by {await editor.OverflowAcrossAsync("#drawio-cells-error-detail")} px.");
        // Two reads that fail give two lines, joined with a line break (the bUnit tests hold the page to
        // that): the stylesheet must keep them two on screen. Against this host the door map reads fine,
        // so the two lines are put in a copy of the note's paragraph, beside it (final review of row 29:
        // nothing measured the white-space that keeps them apart).
        Assert.Equal(2, await editor.LinesShownForAsync("#drawio-cells-error-detail",
            "The door map of this device: the host answered 500 with an empty body\nThe warehouse's cell list: the host answered 503 with an empty body"));
        await editor.LookAtItAsync("voce-29-cells-error-note");
    }
}
