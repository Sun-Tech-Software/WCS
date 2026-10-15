using System.Text.Json;
using Wcs.Api.Contracts.Dtos;
using Xunit;
using Xunit.Abstractions;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// The shape of the page after the passata grafica (2026-09-29), in a real browser against a real
/// host: the bar on one line at the spec's two sizes - at rest, and with the engine running and a mark
/// on every button that can wear one - and on a narrow window (where its buttons keep their icons and
/// their names), the drawing taller than before, the engine started and stopped from the box that hangs
/// from its gesture, the "?" of a window opened and shut by the operator's gestures and its note inside
/// the browser's window wherever the "?" is, the states the page's controls show under the mouse and
/// the keyboard, and every control of the bar within reach with all seven windows open. bUnit lays
/// nothing out and runs no JavaScript: whether a row wraps, how tall the drawing is, whether a note
/// shuts under a click on draw.io's frame or under a press that moves no focus, where it opens, what
/// colour a pressed button is and what the mouse hits, only a browser can say.
/// </summary>
/// <remarks>
/// The drawing's heights before the passata are literals measured on the product of <c>1b30ef0f</c>
/// (compito 1, step 1: host of the worktree, the seed of <see cref="PlantSeed"/>, nothing open, the
/// live link up, the strip settled), not anything worked out from the page's rules.
/// </remarks>
public sealed class DrawioLookBrowserTest(DrawioEditorFixture fixture, ITestOutputHelper output) : IClassFixture<DrawioEditorFixture>
{
    private const string SeedHolder = "browser-test-look";

    private const double SamePixel = DrawioLook.SamePixel;

    /// <summary>The device of <c>OpenAsync(withAnUndrawnDevice: true)</c>, which the drawing cannot show.</summary>
    private static readonly Guid UndrawnId = new("a1b2c3d4-0000-4000-8000-0000000000c1");

    /// <summary>
    /// What the spec puts in the one bar, by what names each piece there (its id, or its first class):
    /// the title, the engine, the live link, the plant checks, the Rulers, the separator and the six
    /// buttons of the windows. Read by the one-line tests so that they cannot pass on a bar that holds
    /// less - the six buttons alone were one line before the passata as well.
    /// </summary>
    private static readonly string[] PiecesOfTheBar =
    [
        "drawio-plant-name",
        "drawio-engine-switch",
        "drawio-live-status",
        "drawio-ruler-switch",
        "drawio-bar-separator",
        .. DrawioLook.WindowButtons.Select(b => b.Id),
    ];

    /// <summary>The seven windows the bar's buttons open: the plant checks' and the six sections'.</summary>
    private static readonly string[] Windows = [.. DrawioLook.WindowButtons.Select(b => b.Id[..^"-toggle".Length])];

    /// <summary>
    /// At the spec's two sizes, at rest - the plant drawn, the live link up - every child of the bar
    /// that is shown (its windows left out) is on one line: the title, the engine, the live link, the
    /// plant checks, the Rulers and the six buttons of the windows, with every word - each button that
    /// opens a window shows its word (<c>.drawio-toggle-label</c> seen - laid out, not transparent, not
    /// hidden - and some width to it). The words go only on a narrow window: hiding them is the easy way
    /// to one line, and the line alone would not see it.
    /// </summary>
    [Theory]
    [InlineData(1366, 768)]
    [InlineData(1920, 1080)]
    public async Task TheBarIsOneLine(int width, int height)
    {
        await using var editor = await OpenAsync(width, height);

        var line = await editor.BarLineAsync();
        var names = await editor.BarNamesAsync();
        output.WriteLine(line.ToString());
        output.WriteLine(names.ToString());
        AssertOneLineHoldingThePiecesOfTheBar(line, $"At {width}x{height}");
        AssertEveryWordIsSeen(names, $"At {width}x{height}");
    }

    /// <summary>
    /// The bar with the most it holds at rest, on one line with every word at 1366 px, and at 1340 px,
    /// the narrowest window that shows the words: the engine in Run ("Read only"), the plant checks' count,
    /// the "!" of an import waiting on Plant file, the dot of PLC connections changed and not saved - and
    /// the live link's widest word, "Connecting", staged on its pill (the host's channel cannot be made to
    /// reconnect on cue; DrawioTopBarTest proves the pill's words and tones). Until the final fix round the
    /// marks followed the words, 14 to 22 px each, and no rule of the bar's widths counted them: this bar
    /// needed 1395.5 px, two rows at 1366 (measured, second fix round of task 2). Every mark is on screen
    /// on its button's icon. And the "Read only" pill says why, with its tip, under the mouse and on the
    /// keyboard's focus alike - its sentence is on screen nowhere else.
    /// </summary>
    [Fact]
    public async Task WithTheEngineRunningAndAMarkOnEveryButtonTheBarIsOneLineWithEveryWord()
    {
        using var client = fixture.CreateClient();
        await using var editor = await OpenAsync(1366, 768);
        var directory = Path.Combine(Path.GetTempPath(), "wcs-look-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "impianto.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(await client.GetPlantGraphAsync()));
        try
        {
            await editor.ClickAsync("#drawio-engine-start");
            await editor.ClickAsync("#drawio-engine-confirm");
            await editor.WaitForTextAsync("#drawio-engine-state", "Engine: Run");
            await editor.WaitForShownAsync("#drawio-engine-banner", "the \"Read only\" pill");

            // The seed's devices are joined by no junction: the check counts them.
            await editor.OpenTheChecksAsync();
            await editor.RunTheJunctionCheckAsync();
            await editor.WaitForShownAsync("#drawio-checks-count", "the plant checks' count");
            await editor.CloseTheWindowAsync("drawio-checks");

            await editor.OpenWindowAsync("drawio-plc-connections");
            await editor.ClickAsync("#drawio-plc-add");
            await editor.WaitForShownAsync("#drawio-plc-connections-toggle > .drawio-badge-dot", "the dot of the PLC connections changed");
            await editor.CloseTheWindowAsync("drawio-plc-connections");

            // An import chosen and not confirmed; its window shut with Esc, the one gesture that reaches it
            // while its confirmation's backdrop takes every click (DrawioImportExportTest).
            await editor.OpenThePlantFileSectionAsync();
            await editor.ChooseTheFileToImportAsync(file);
            await editor.ShutTheWindowWithEscapeAsync("drawio-plant-file");

            foreach (var (mark, button) in new[]
                     {
                         ("#drawio-checks-count", "drawio-checks-toggle"),
                         ("#drawio-plant-file-toggle > .drawio-badge", "drawio-plant-file-toggle"),
                         ("#drawio-plc-connections-toggle > .drawio-badge", "drawio-plc-connections-toggle"),
                     })
            {
                Assert.True(await editor.IsShownAsync(mark), $"The mark {mark} is not on screen.");
                var box = await editor.BoxOfAsync(mark);
                var icon = await editor.BoxOfAsync($"#{button} > svg.drawio-icon");
                Assert.True(box[0] < icon[0] + icon[2] && icon[0] < box[0] + box[2] && box[1] < icon[1] + icon[3] && icon[1] < box[1] + box[3],
                    FormattableString.Invariant($"The mark {mark} at {box[0]:0.##},{box[1]:0.##} is not on the icon of #{button} at {icon[0]:0.##},{icon[1]:0.##}: it takes room of its own in the bar."));
            }

            await AssertOneLineWithEveryWordAsync(editor, "At 1366x768 with the engine running and every mark");

            // "Read only" says why - under the mouse, and on the keyboard's focus with the mouse elsewhere.
            const string ReadOnly = "Read only: the engine is running - you can draw, but the save will write nothing until it is stopped.";
            await editor.PutTheMouseOnAsync("#drawio-plant-name");
            Assert.Null(await editor.TipOnScreenAsync("#drawio-engine-banner"));
            Assert.Equal(ReadOnly, await editor.TipUnderTheMouseAsync("#drawio-engine-banner"));
            await editor.PutTheMouseOnAsync("#drawio-plant-name");
            // The engine's gesture is the last thing Tab reaches before it: the quiet "Live" is no stop of Tab.
            await editor.TabFromAsync("#drawio-engine-stop", "drawio-engine-banner");
            Assert.Equal(ReadOnly, await editor.TipOnScreenAsync("#drawio-engine-banner"));

            await editor.StageTheLiveLinkAsync("Connecting", "drawio-pill-warn");
            await AssertOneLineWithEveryWordAsync(editor, "At 1366x768 with the engine running, every mark and \"Connecting\"");
            await editor.SetViewportAsync(1340, 768);
            await AssertOneLineWithEveryWordAsync(editor, "At 1340x768 with the engine running, every mark and \"Connecting\"");
        }
        finally
        {
            await TestHostEngine.LeaveStoppedAsync(client, output);
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// On a window 900 px wide the bar is still one line: the buttons of the windows have given up
    /// their words (every <c>.drawio-toggle-label</c> is <c>display: none</c>), keep their icon on screen,
    /// and keep a name, the one a screen reader says and the tip shows (every <c>-toggle</c> of the bar
    /// has an aria-label that begins with it).
    /// </summary>
    [Fact]
    public async Task OnANarrowWindowTheBarIsOneLineOfButtonsThatKeepTheirIconsAndTheirNames()
    {
        await using var editor = await OpenAsync(900, 768);

        var line = await editor.BarLineAsync();
        var names = await editor.BarNamesAsync();
        output.WriteLine(line.ToString());
        output.WriteLine(names.ToString());

        Assert.Equal(DrawioLook.WindowButtons.Select(b => b.Id), names.Toggles.Select(t => t.Id));
        foreach (var (id, name) in DrawioLook.WindowButtons)
        {
            var toggle = names.Toggles.Single(t => t.Id == id);
            Assert.True(toggle.AriaLabel is { } label && label.StartsWith(name, StringComparison.Ordinal),
                $"On a 900 px window {toggle}: the name \"{name}\" is not there for a screen reader, nor for its tip.");
            Assert.True(toggle.IconSeen && toggle.IconWidth > 0, $"On a 900 px window {toggle}: the button has lost its icon too.");
        }

        // Not blind: every button of a window has its word, and none of them is laid out.
        Assert.Equal(DrawioLook.WindowButtons.Select(b => b.Id).Order(), names.Labels.Select(l => l.Owner).Order());
        Assert.All(names.Labels, l => Assert.True(l.Display == "none", $"On a 900 px window {l}: the word is still laid out."));

        // Last: with the words laid out the bar would wrap too, and this would hide which went first.
        AssertOneLineHoldingThePiecesOfTheBar(line, "At 900x768");
    }

    /// <summary>
    /// At the spec's two sizes, at rest, draw.io's frame is taller than it was before the passata,
    /// when a line of the selection, the plant checks, the engine, the strip of the sections and the
    /// Rulers were stacked above it (compito 1, step 1: 591.875 and 903.875 px).
    /// </summary>
    [Theory]
    [InlineData(1366, 768, 591.875)]
    [InlineData(1920, 1080, 903.875)]
    public async Task TheDrawingIsTallerThanBeforeTheVisualPass(int width, int height, double before)
    {
        await using var editor = await OpenAsync(width, height);

        var frame = await editor.BoxOfAsync("#drawio-frame");
        output.WriteLine(FormattableString.Invariant(
            $"At {width}x{height} draw.io's frame is at top {frame[1]:0.###}, {frame[3]:0.###} px high; before the passata {before:0.###} px high."));
        Assert.True(frame[3] > before,
            FormattableString.Invariant($"At {width}x{height} draw.io's frame is {frame[3]:0.###} px high: not taller than the {before:0.###} px before the passata."));
    }

    /// <summary>
    /// The engine's pill and its one gesture: "Start the engine" opens the box under it, the box is on
    /// screen with its Yes within reach, and the Yes - "Start engine" - starts the host's engine; then
    /// "Stop the engine", the same box, and "Stop engine" stops it. The host's own status says so, not
    /// only the pill. <see cref="DrawioEngineSwitchBrowserTest"/> makes the same round from the page's
    /// ids; here the question is that the Yes is in the box and the box is where the mouse can press it -
    /// with a window dragged up under it as far as windows go: the box hangs over the drawing, where the
    /// windows are, and only its place above them keeps its Yes within reach. After the Yes, the box is
    /// gone.
    /// </summary>
    [Fact]
    public async Task ConfirmInTheEnginesBoxStartsAndStopsTheEngine()
    {
        using var client = fixture.CreateClient();
        await using var editor = await OpenAsync(1366, 768);
        try
        {
            await editor.OpenWindowAsync("drawio-operations");
            var start = await editor.BoxOfAsync("#drawio-engine-start");
            await editor.DragTheWindowUpAsync("drawio-operations", Math.Max(0, start[0] - 40));

            await ConfirmFromTheBoxAsync(editor, "drawio-engine-start");
            await editor.WaitForTextAsync("#drawio-engine-state", "Engine: Run");
            Assert.Equal(NavigatorStatusTypes.Run, (await client.GetStatusAsync()).NavStatus);
            Assert.False(await editor.IsShownAsync("#drawio-engine-confirm-box"), "The engine's box is still on screen after its Yes started the engine.");

            await editor.WaitForTheEngineToOfferAsync("drawio-engine-stop");
            await ConfirmFromTheBoxAsync(editor, "drawio-engine-stop");
            await editor.WaitForTextAsync("#drawio-engine-state", "Engine: Stop");
            Assert.Equal(NavigatorStatusTypes.Stop, (await client.GetStatusAsync()).NavStatus);
            Assert.False(await editor.IsShownAsync("#drawio-engine-confirm-box"), "The engine's box is still on screen after its Yes stopped the engine.");
        }
        finally
        {
            await TestHostEngine.LeaveStoppedAsync(client, output);
        }
    }

    /// <summary>
    /// The engine's gesture <paramref name="asker"/> pressed, its box on screen with the Yes in it and
    /// within reach - over the Operations window, which lies under it - and the Yes pressed.
    /// </summary>
    private async Task ConfirmFromTheBoxAsync(DrawioEditorPage editor, string asker)
    {
        await editor.ClickAsync($"#{asker}");
        await editor.WaitForAttributeAsync($"#{asker}", "aria-expanded", "true");
        Assert.True(await editor.IsShownAsync("#drawio-engine-confirm-box #drawio-engine-confirm"),
            $"After {asker} the engine's Yes is not on screen in its box (#drawio-engine-confirm-box).");
        var yes = await editor.HitAtTheMiddleOfAsync("#drawio-engine-confirm");
        var window = await editor.BoxOfAsync("#drawio-operations");
        output.WriteLine(FormattableString.Invariant($"After {asker}: {yes}; the Operations window at {window[0]:0.##},{window[1]:0.##} {window[2]:0.##}x{window[3]:0.##}"));
        Assert.True(window[0] <= yes.X && yes.X <= window[0] + window[2] && window[1] <= yes.Y && yes.Y <= window[1] + window[3],
            $"The test is blind: the Operations window does not lie under the engine's Yes ({yes}).");
        Assert.True(yes.HitsIt, $"After {asker} the engine's Yes cannot be reached: {yes}.");
        await editor.ClickAsync("#drawio-engine-confirm");
    }

    /// <summary>
    /// The "?" of the Devices window: a click on it opens its note, with the window's explanation on
    /// screen; a press on the note's own text leaves it open, and a second click on the "?" shuts it; a
    /// click outside shuts it - on draw.io's shapes panel, on the window's own title bar, on the
    /// drawing - and so does the focus leaving it by Tab, with nothing pressed, and Esc, with the focus in
    /// the note. The note sees a press outside itself (draw.io cancels a press on its shapes panel, the
    /// windows module the press a drag begins with: neither moves the focus, and the focus still on the
    /// "?" afterwards says the test made that press), and that sight must not take in the note's own
    /// presses: one on its text would shut it under the hand selecting its words, one on the "?" would
    /// shut it and the click that follows open it again. Those two are read once the page has rendered a
    /// gesture that came after them, so the note cannot be caught on the way. The drawing is draw.io's
    /// frame, where no click ever reaches the page. The window stays open through all of them: the
    /// note's Esc is the note's (the next one is the window's), and the focus goes back to the "?" from
    /// inside the note, which the Esc hides.
    /// </summary>
    [Fact]
    public async Task TheQuestionMarkOfDevicesOpensOnAClickAndShutsOnAClickOutsideOnTabAndWithEsc()
    {
        const string Help = "drawio-device-list-help";
        const string Table = "drawio-device-list-table";
        await using var editor = await OpenAsync(1366, 768);
        await editor.OpenWindowAsync("drawio-device-list");
        await editor.WaitForTheHelpAsync(Help, open: false, "when the Devices window opened");

        await editor.ClickAsync($"#{Help}");
        await editor.WaitForTheHelpAsync(Help, open: true, "after a click on it");
        Assert.True(await editor.IsShownAsync($"#{Help}-body .drawio-device-list-scope"),
            "The note of the Devices window's \"?\" is open without the window's explanation on screen.");
        // Every press below is for the module that watches the note, which the first opening fetches
        // and waits for: without it, a press is only what the rest of the page makes of it. From here
        // on it watches the same note - the window, and the "?" in it, stay open to the end.
        await editor.WaitForTheNoteToBeWatchedAsync(Help);

        // The note's own presses. Nothing is waited for after them: what they must not do is read once
        // the page has rendered a sort of the list that came after them - no press, no move of the
        // focus - and by then a shut under the press on the text, or an opening again after the click
        // on the "?", would be on screen too.
        var text = await editor.ClickTheNoteOfAsync(Help);
        output.WriteLine($"Clicked on {text}, then {await editor.SortTheTableWithoutAPressAsync(Table)}");
        var afterText = await editor.HelpStateAsync(Help);
        Assert.True(afterText.Expanded == "true" && afterText.NoteShown, $"A click on {text} did not leave the note open: {afterText}.");
        Assert.True(await editor.FocusedElementIdAsync() == $"{Help}-body",
            $"The click on {text} moved the focus to {await editor.FocusedElementAsync()}, not into the note: it is not the press on the note this step is about.");

        await editor.ClickAsync($"#{Help}");
        output.WriteLine($"Clicked on the \"?\" with its note open, then {await editor.SortTheTableWithoutAPressAsync(Table)}");
        var afterHelp = await editor.HelpStateAsync(Help);
        Assert.True(afterHelp.Expanded == "false" && !afterHelp.NoteShown, $"A second click on the \"?\" did not leave its note shut: {afterHelp}.");
        Assert.True(await editor.FocusedElementIdAsync() == Help,
            $"The second click on the \"?\" moved the focus to {await editor.FocusedElementAsync()}: it did not land on the \"?\".");

        await editor.ClickAsync($"#{Help}");
        await editor.WaitForTheHelpAsync(Help, open: true, "after a third click on it");
        var panel = await editor.ClickTheShapesPanelWhereNothingCoversItAsync();
        output.WriteLine($"Clicked on {panel}");
        await editor.WaitForTheHelpAsync(Help, open: false, $"after a click on {panel}");
        Assert.True(await editor.FocusedElementIdAsync() == Help,
            $"The click on {panel} moved the focus to {await editor.FocusedElementAsync()}: it is not the press that moves no focus this step is about.");
        Assert.True(await editor.IsTheSectionOpenAsync("drawio-device-list"), "A click on draw.io's shapes panel that shut the note shut the Devices window too.");

        await editor.ClickAsync($"#{Help}");
        await editor.WaitForTheHelpAsync(Help, open: true, "after a fourth click on it");
        var title = await editor.ClickTheTitleBarAsync("drawio-device-list");
        output.WriteLine($"Clicked on {title}");
        await editor.WaitForTheHelpAsync(Help, open: false, $"after a click on {title}");
        Assert.True(await editor.FocusedElementIdAsync() == Help,
            $"The click on {title} moved the focus to {await editor.FocusedElementAsync()}: it is not the press that moves no focus this step is about.");
        Assert.True(await editor.IsTheSectionOpenAsync("drawio-device-list"), "A click on the Devices window's title bar that shut the note shut the window too.");

        await editor.ClickAsync($"#{Help}");
        await editor.WaitForTheHelpAsync(Help, open: true, "after a fifth click on it");
        var hit = await editor.ClickTheDrawingWhereNothingCoversItAsync(PlantSeed.DraggedName, PlantSeed.UntouchedName);
        output.WriteLine($"Clicked on {hit}");
        await editor.WaitForTheHelpAsync(Help, open: false, $"after a click on {hit}");
        Assert.True(await editor.IsTheSectionOpenAsync("drawio-device-list"), "A click on the drawing that shut the note shut the Devices window too.");

        // Tab from the "?" goes on to the window's pop-out command - the note is not in the order of
        // Tab - and nothing is pressed anywhere: only the focus leaving the note's box can shut it.
        await editor.ClickAsync($"#{Help}");
        await editor.WaitForTheHelpAsync(Help, open: true, "after a sixth click on it");
        await editor.PressAsync("Tab");
        await editor.WaitForTheHelpAsync(Help, open: false, "after Tab took the focus away from it");
        Assert.True(await editor.FocusedElementIdAsync() == "drawio-device-list-popout",
            $"Tab from the \"?\" moved the focus to {await editor.FocusedElementAsync()}, not to the window's pop-out command: it is not the focus leaving the note this step is about.");
        Assert.True(await editor.IsTheSectionOpenAsync("drawio-device-list"), "The Tab that shut the note shut the Devices window too.");

        // Esc with the focus in the note: the note goes, and the focus with it unless it is given back.
        await editor.ClickAsync($"#{Help}");
        await editor.WaitForTheHelpAsync(Help, open: true, "after a seventh click on it");
        output.WriteLine($"Clicked on {await editor.ClickTheNoteOfAsync(Help)}");
        await editor.WaitForTheFocusOnAsync($"{Help}-body");
        await editor.PressAsync("Escape");
        await editor.WaitForTheHelpAsync(Help, open: false, "after Esc");
        await editor.WaitForTheFocusOnAsync(Help);
        Assert.True(await editor.IsTheSectionOpenAsync("drawio-device-list"), "The Esc that shut the note shut the Devices window too.");
    }

    /// <summary>
    /// The note of a "?" opens inside the browser's window wherever the "?" is, whole, and nothing cuts
    /// it: the Devices window dragged to the browser's left edge (the note hangs from the "?" towards
    /// the left, and its start was off the screen), the plant checks' window dragged to the bottom (their
    /// note is two long paragraphs, and went past the bottom), the window of the devices the drawing does
    /// not show - its "?" in its title bar, as every window's - and the properties panel's heading, whose
    /// column clips what goes past its edges. It opens there from the first time: the first "?" is
    /// pressed with the module that places the notes held back, as a slow link holds it, and meanwhile
    /// its note is not on screen where nothing placed it. And [hidden] hides whatever it is on: every
    /// shut window and note of the page is off screen, whatever the display the page's rules give it.
    /// </summary>
    [Fact]
    public async Task TheNoteOfAQuestionMarkOpensInsideTheBrowsersWindowWhereverTheQuestionMarkIs()
    {
        await using var editor = await OpenAsync(1366, 768, withAnUndrawnDevice: true);
        Assert.Empty(await editor.HiddenButShownAsync());

        // Narrowed to its least and taken to the left edge: its "?" is some 220 px from the edge, and the
        // note, up to 28rem wide, hangs from the "?" towards the left.
        await editor.OpenWindowAsync("drawio-device-list");
        await editor.DragAsync("#drawio-device-list .drawio-window-grip", -800, 0);
        await editor.DragTheWindowUpAsync("drawio-device-list", 0);
        var devices = await editor.BoxOfAsync("#drawio-device-list");
        Assert.True(devices[0] <= SamePixel && devices[2] <= 300, FormattableString.Invariant($"The test is blind: the Devices window is at {devices[0]:0.##}, {devices[2]:0.##} px wide."));
        // The first "?" of the page, pressed while the module that places its note is held back as a
        // slow link would hold it: until the module watches the note, nothing moves it from where the
        // stylesheet hangs it - here its start past the left edge - and it must not be on screen there
        // meanwhile (seen on 30/9: in each of the three runs of the whole suite of the final check,
        // the measure right after the opening caught it there, before the module had placed it).
        const string DevicesHelp = "drawio-device-list-help";
        const string AtTheLeftEdge = "the Devices window, narrowed, at the left edge";
        await editor.ClickTheHelpWithItsModuleHeldBackAsync(DevicesHelp, async () =>
        {
            var held = await editor.NoteOnScreenAsync(DevicesHelp);
            output.WriteLine($"In {AtTheLeftEdge}, the module held back: {held}");
            Assert.True(!held.Shown || (held.Inside && held.StartReached && held.EndReached),
                $"In {AtTheLeftEdge}, with the module that places the note held back, {held}: on screen where nothing placed it.");
        });
        await AssertTheOpenNoteIsInsideAsync(editor, DevicesHelp, AtTheLeftEdge);
        await editor.CloseTheWindowAsync("drawio-device-list");

        // Lowered to its least and taken to the bottom: under its "?" the long note has no room.
        await editor.OpenWindowAsync("drawio-checks");
        await editor.DragAsync("#drawio-checks .drawio-window-grip", 0, -800);
        await editor.DragAsync("#drawio-checks .drawio-window-name", -600, 800);
        var checks = await editor.BoxOfAsync("#drawio-checks");
        Assert.True(checks[1] + checks[3] >= 768 - SamePixel && checks[3] <= 200,
            FormattableString.Invariant($"The test is blind: the plant checks' window is {checks[3]:0.##} px high and ends at {checks[1] + checks[3]:0.##}, not low at the bottom."));
        await AssertTheNoteOpensInsideAsync(editor, "drawio-checks-help", "the plant checks' window, lowered, at the bottom");
        await editor.CloseTheWindowAsync("drawio-checks");

        await editor.OpenTheUndrawnBoxAsync();
        await AssertTheNoteOpensInsideAsync(editor, "drawio-undrawn-details-help", "the window of the devices not in the drawing");
        await editor.CloseTheWindowAsync("drawio-undrawn-details");

        await editor.SelectDevicesAsync(PlantSeed.DraggedName);
        await editor.WaitForShownAsync("#drawio-property-help", "the \"?\" of the properties panel's heading");
        await AssertTheNoteOpensInsideAsync(editor, "drawio-property-help", "the properties panel's heading");

        Assert.Empty(await editor.HiddenButShownAsync());
    }

    /// <summary>The "?" <paramref name="helpId"/> clicked, its note open, whole inside the browser's window and hit at both ends of its first line; then Esc shuts it.</summary>
    private async Task AssertTheNoteOpensInsideAsync(DrawioEditorPage editor, string helpId, string where)
    {
        await editor.ClickAsync($"#{helpId}");
        await AssertTheOpenNoteIsInsideAsync(editor, helpId, where);
    }

    /// <summary>The note of the "?" <paramref name="helpId"/>, just clicked, open, whole inside the browser's window and hit at both ends of its first line; then Esc shuts it.</summary>
    private async Task AssertTheOpenNoteIsInsideAsync(DrawioEditorPage editor, string helpId, string where)
    {
        await editor.WaitForTheHelpAsync(helpId, open: true, $"after a click on it, in {where}");
        var note = await editor.NoteOnScreenAsync(helpId);
        output.WriteLine($"In {where}: {note}");
        Assert.True(note.Inside && note.StartReached && note.EndReached, $"In {where}, {note}.");
        await editor.PressAsync("Escape");
        await editor.WaitForTheHelpAsync(helpId, open: false, "after Esc");
    }

    /// <summary>
    /// What the page's controls show under the mouse (passata grafica, final fix round): pressed, a
    /// button of the bar lit because its window is open shows the press - its lit colour outweighed it -
    /// and so do the Rulers switch (a label, which the page's rule for pressed buttons does not reach),
    /// in the text's colour as the bar's other buttons, and the "?" of a window (an icon button, whose own
    /// transparent background outweighed it); none of the presses is a click, all are let go elsewhere.
    /// Every other row of the Devices list is veiled, and the mouse on it shows a darker grey - it showed
    /// the veil's own colour, nothing. The window's count stands right after its name ("Devices 2", the
    /// spec's example), not at the far end of the title bar. And "Details" is lit while its window is
    /// open, as every other button of the page that opens something.
    /// </summary>
    [Fact]
    public async Task TheBarsButtonsTheQuestionMarkAndTheListsRowsShowTheMouse()
    {
        await using var editor = await OpenAsync(1366, 768, withAnUndrawnDevice: true);
        await editor.OpenWindowAsync("drawio-device-list");

        var lit = await editor.ButtonLookAsync("#drawio-device-list-toggle", "#drawio-plant-name");
        output.WriteLine(lit.ToString());
        Assert.True(lit is { Rest: DrawioLook.LightBlue, Pressed: DrawioLook.PressedGrey }, $"The lit button of an open window does not show the press: {lit}.");
        Assert.True(await editor.IsTheSectionOpenAsync("drawio-device-list"), "Looking at the lit button shut its window: a press was a click.");

        var rulers = await editor.ButtonLookAsync(".drawio-ruler-switch", "#drawio-plant-name");
        output.WriteLine(rulers.ToString());
        Assert.True(rulers is { RestText: DrawioLook.Text, Pressed: DrawioLook.PressedGrey }, $"The Rulers switch is not drawn as the bar's buttons, or does not show the press: {rulers}.");

        var help = await editor.ButtonLookAsync("#drawio-device-list-help", "#drawio-device-list .drawio-window-name");
        output.WriteLine(help.ToString());
        Assert.True(help is { Hover: DrawioLook.Grey, Pressed: DrawioLook.PressedGrey }, $"The \"?\" does not show the press: {help}.");

        const string EvenRow = "#drawio-device-list-table tbody tr:nth-child(2)";
        await editor.PutTheMouseOnAsync("#drawio-plant-name");
        // The paged grids of 14/10 stripe every other row with the ribbon's grey at 52% over the
        // window (wcs-webeditor.css, .drawio-data-table tbody tr:nth-child(even)) - no longer the opaque
        // veil of the unpaged table: the colour is the new design's, exactly.
        var restingRow = await editor.ComputedStyleAsync(EvenRow, "background-color");
        Assert.Equal(DrawioLook.ZebraStripe, restingRow);
        await editor.PutTheMouseOnAsync(EvenRow);
        Assert.Equal(DrawioLook.Grey, await editor.ComputedStyleAsync(EvenRow + " td:first-child", "background-color"));
        var hoveredRow = await editor.ComputedStyleAsync(EvenRow, "background-color");
        Assert.Equal(DrawioLook.Grey, hoveredRow);
        Assert.NotEqual(restingRow, hoveredRow);

        // The name's words, not its box: a box that grows to fill the title bar ends where the count starts.
        var name = await editor.TextBoxOfAsync("#drawio-device-list .drawio-window-name");
        var count = await editor.BoxOfAsync("#drawio-device-list .drawio-window-count");
        Assert.True(count[0] - (name[0] + name[2]) <= 8 + SamePixel,
            FormattableString.Invariant($"The count of the Devices window stands {count[0] - (name[0] + name[2]):0.##} px after its name, not right after it."));

        await editor.OpenTheUndrawnBoxAsync();
        Assert.Equal(DrawioLook.LightBlue, await editor.ComputedStyleAsync("#drawio-undrawn-details-toggle", "background-color"));
    }

    /// <summary>
    /// The properties panel and the plant file take the page's states too: the panel's "Save" is the
    /// page's blue button - darker under the mouse, darker still pressed (its own rules left it the hover's
    /// colour pressed) - a property's field on the focus has the accent's border and its ring, not a 1 px
    /// halo next to its grey border, the panel's part that scrolls leaves room for the keyboard's ring
    /// (2 px, 1 px off the control) round a control at its edge - it clipped it - and the Import button
    /// shows the keyboard's ring when Tab puts the focus on its file input, clipped to a pixel after it.
    /// </summary>
    [Fact]
    public async Task ThePanelsSaveItsFieldsAndTheImportShowTheMouseAndTheKeyboard()
    {
        await using var editor = await OpenAsync(1366, 768);
        await editor.SelectDevicesAsync(PlantSeed.DraggedName);
        await editor.WaitForShownAsync("#property-panel-save-button", "the properties panel's Save");
        await editor.ScrollIntoViewAsync("#property-panel-save-button");

        var save = await editor.ButtonLookAsync("#property-panel-save-button", "#drawio-plant-name");
        output.WriteLine(save.ToString());
        Assert.True(save is { Rest: DrawioLook.Blue, RestText: DrawioLook.White, Hover: DrawioLook.BlueHover, Pressed: DrawioLook.BluePressed },
            $"The properties panel's Save is not the page's blue button: {save}.");

        var scroll = await editor.BoxOfAsync(".drawio-property-scroll");
        var duplicate = await editor.BoxOfAsync("#drawio-duplicate-ask");
        Assert.True(duplicate[0] - scroll[0] >= 3 - SamePixel,
            FormattableString.Invariant($"The panel's Duplicate stands {duplicate[0] - scroll[0]:0.##} px from the left edge of the part that scrolls: its ring (3 px outside it) is cut."));

        const string Field = "#drawio-property-panel [data-property='Name'] input";
        await editor.FocusAsync(Field);
        Assert.Equal(DrawioLook.Blue, await editor.ComputedStyleAsync(Field, "border-top-color"));

        await editor.OpenThePlantFileSectionAsync();
        await editor.TabFromAsync("#plant-export-button", "plant-import-input");
        Assert.Equal("solid", await editor.ComputedStyleAsync("#plant-import-button", "outline-style"));
        Assert.Equal(DrawioLook.Blue, await editor.ComputedStyleAsync("#plant-import-button", "outline-color"));
    }

    /// <summary>
    /// All seven windows open and each dragged as far up as it goes, spread across the width of the
    /// bar, so that every control of the bar has a window in its column that only the floor keeps off
    /// it: the browser hits each control at its middle - the engine's pill and its button, the live
    /// link, the plant checks, the Rulers, the six buttons of the windows.
    /// </summary>
    [Fact]
    public async Task WithTheSevenWindowsOpenEveryControlOfTheBarIsWithinReach()
    {
        const int Width = 1366;
        const int Widest = 760; // Devices, Missions and PLC connections, as the page's cascade opens them
        await using var editor = await OpenAsync(Width, 768);
        // All seven open first, while nothing lies over the bar: in the order of the page's cascade each
        // one opens in front of those before it, a step lower and to the right, its title bar clear.
        // Then from the one in front back, each is dragged up and to the right of those still to be
        // taken, so that what has been dragged never lies over their title bars.
        string[] cascade = ["drawio-plant-file", "drawio-operations", "drawio-device-list", "drawio-junction-list", "drawio-mission-list", "drawio-plc-connections", "drawio-checks"];
        Assert.Equal(Windows.Order(), cascade.Order());
        foreach (var id in cascade) await editor.OpenWindowAsync(id);
        for (var i = cascade.Length - 1; i >= 0; i--)
            await editor.DragTheWindowUpAsync(cascade[i], i * (Width - Widest) / (cascade.Length - 1.0));

        var placed = await editor.TheStripAndItsWindowsAsync(Windows);
        var controls = await editor.BarControlsHitAsync();
        output.WriteLine(placed.ToString());
        output.WriteLine(string.Join("; ", controls.Select(c => FormattableString.Invariant($"{c} at {c.X:0},{c.Y:0}"))));

        // Not blind: the controls are all there, each one has a window in its column, and every window
        // went up at least as far as the floor - no lower, where it could not reach the bar anyway.
        string[] expected = ["drawio-engine-state", "drawio-engine-start", "drawio-live-status", "drawio-ruler-switch", .. DrawioLook.WindowButtons.Select(b => b.Id)];
        Assert.All(expected, id => Assert.True(controls.Any(c => c.Id == id), $"The bar shows no control {id}."));
        Assert.All(placed.Windows, w => Assert.True(w.Open && w.Top <= placed.Floor + SamePixel,
            FormattableString.Invariant($"{w} did not go up as far as the floor at {placed.Floor:0.##}.")));
        Assert.All(controls, c => Assert.True(placed.Windows.Any(w => w.Left <= c.X && c.X <= w.Left + w.Width),
            $"No window lies in the column of {c.Id}: nothing could cover it, and its reach proves nothing."));

        Assert.All(controls, c => Assert.True(c.HitsIt, $"With the seven windows open, the bar's {c}."));
    }

    /// <summary>The bar one line with every word: its pieces on one line, and each button of a window showing its word.</summary>
    private async Task AssertOneLineWithEveryWordAsync(DrawioEditorPage editor, string when)
    {
        var line = await editor.BarLineAsync();
        var names = await editor.BarNamesAsync();
        output.WriteLine($"{when}: {line}");
        output.WriteLine($"{when}: {names}");
        AssertOneLineHoldingThePiecesOfTheBar(line, when);
        AssertEveryWordIsSeen(names, when);
    }

    /// <summary>Every button of a window shows its word: seen - laid out, not transparent, not hidden - with some width to it.</summary>
    private static void AssertEveryWordIsSeen(DrawioBarNames names, string when)
    {
        Assert.Equal(DrawioLook.WindowButtons.Select(b => b.Id).Order(), names.Labels.Select(l => l.Owner).Order());
        Assert.All(names.Labels, l => Assert.True(l.Display != "none" && l.Seen && l.Width > 1,
            $"{when} {l}: the button shows its icon without its word."));
    }

    /// <summary>
    /// The shown children of the bar are one line (<see cref="DrawioLook.AssertOneLine"/>), and among them
    /// are all the pieces the spec puts in it.
    /// </summary>
    private static void AssertOneLineHoldingThePiecesOfTheBar(DrawioBarLine line, string when)
    {
        var keys = line.Children.Select(c => c.Key).ToArray();
        Assert.All(PiecesOfTheBar, piece => Assert.True(keys.Contains(piece),
            $"{when} the bar does not show {piece}: {line}."));
        DrawioLook.AssertOneLine(line, when);
    }

    /// <summary>
    /// A fresh seed and the page open on it in a window of this size, at rest: the seed's devices drawn,
    /// the live link up, the bar settled. <paramref name="withAnUndrawnDevice"/> adds a device the drawing
    /// cannot show (as DrawioWindowsBrowserTest does): the page then has a line about it in the row of
    /// outcomes, and its list in a window behind "Details".
    /// </summary>
    private async Task<DrawioEditorPage> OpenAsync(int width, int height, bool withAnUndrawnDevice = false)
    {
        output.WriteLine(fixture.Host.Declaration);
        using (var client = fixture.CreateClient())
        {
            await PlantSeed.ResetAsync(client, SeedHolder);
            if (withAnUndrawnDevice)
                await PlantSeed.AddDeviceAsync(client, SeedHolder, UndrawnId, "UndrawnDevice", DeviceTypes.None,
                    new PlantItemGeometryDto(UndrawnId, X: 900, Y: 150, Width: 120, Height: 60, Rotation: 0));
        }

        var editor = await DrawioEditorPage.OpenInAWindowOfAsync(fixture.Browser, fixture.Host, width, height);
        try
        {
            await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
            await editor.WaitForDeviceAsync(PlantSeed.UntouchedName);
            await editor.WaitForTheLiveLinkAsync();
            await editor.SetViewportAsync(width, height);
            return editor;
        }
        catch
        {
            await editor.DisposeAsync();
            throw;
        }
    }
}
