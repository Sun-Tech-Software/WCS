using System.Text.RegularExpressions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Drawio.Components;
using Wcs.WebEditor.Pages;
using Wcs.WebEditor.Session;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

/// <summary>
/// The strip of the draw.io page since the sections became floating windows (spec 2026-09-29): one
/// button per section, any number of windows open at a time, a list that reads only while its window
/// is open, and the windows open at the end of the last visit opened again. Measured through the WHOLE
/// page, because the set of open windows and its memory are the page's; the window itself is
/// DrawioWindowTest's.
/// </summary>
public sealed class DrawioEditorWindowsTest : PlantCanvasBunitContext
{
    private static readonly Guid A = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002");

    private static readonly string[] Sections =
    [
        "drawio-plant-file", "drawio-operations", "drawio-device-list",
        "drawio-junction-list", "drawio-mission-list", "drawio-plc-connections",
    ];

    private readonly DrawioWriteHandler _host = new();
    private readonly FakePlantEventSource _source = new();
    private BunitJSModuleInterop _windows = null!;

    public DrawioEditorWindowsTest()
    {
        _host.Items =
        [
            Item(A, "Shuttle 1", 10),
            Item(B, "Shuttle 2", 300),
        ];
        _host.Configs[A] = Config(A, "Shuttle 1", maxUnloadWeight: 900);
        _host.Configs[B] = Config(B, "Shuttle 2", maxUnloadWeight: 100);
    }

    private static PlantItemDto Item(Guid id, string name, double x) =>
        new(id, name, DeviceTypes.Automation, null, null, null, ConnectionTypes.Test,
            default, 0, null, 0, 0, 0, 0, default!, x, 10, 100, 100, 0);

    private static PlantItemConfigDto Config(Guid id, string name, int maxUnloadWeight) => new()
    {
        ItemId = id,
        Name = name,
        DeviceType = (short)DeviceTypes.Automation,
        ConnectionType = (short)ConnectionTypes.Test,
        MaxUnloadWeight = maxUnloadWeight,
    };

    /// <param name="openedLastTime">
    /// What the windows module answers to <c>openedLastTime()</c>, the memory of this browser; null
    /// leaves the call unplanned, and the loose module answers null - a memory with nothing in it.
    /// </param>
    private IRenderedComponent<DrawioEditor> RenderEditor(string[]? openedLastTime = null)
    {
        var bridge = JSInterop.SetupModule("./_content/Wcs.WebEditor/drawio-bridge.js");
        bridge.Setup<string?>("selectCell", _ => true).SetResult("ok");
        bridge.Setup<bool>("renameCell", _ => true).SetResult(true);
        _windows = JSInterop.SetupModule(DrawioWindow.ModulePath);
        if (openedLastTime is not null) _windows.Setup<string[]>("openedLastTime").SetResult(openedLastTime);
        var state = new EditorSessionState(new Uri("http://localhost:5580"), _host) { EventSourceForTesting = () => _source };
        state.UseKey("test-key");
        Services.AddSingleton(state);
        var cut = Render<DrawioEditor>();
        // The plant is in once the mode switch is enabled (DrawioEditorModeSwitchTest's own wait).
        cut.WaitForAssertion(() => Assert.False(cut.Find("#drawio-mode-toggle").HasAttribute("disabled")));
        return cut;
    }

    /// <summary>The reads of every configuration the page makes by itself when it loads the plant: <c>PlantGraph.LoadAsync</c>'s, since queue item 21.</summary>
    private const int PlantLoadConfigReads = 1;

    /// <summary>
    /// The reads of every configuration (GET /plant/items/config) on top of the one the page makes
    /// when it loads the plant: the device list's. Counted against that known one and not from a
    /// snapshot taken after the load - a list that read while shut would read DURING the load, and a
    /// snapshot would swallow that read.
    /// </summary>
    private int ConfigurationListReads =>
        _host.Requests.Count(r => r == "GET /api/v1/plant/items/config") - PlantLoadConfigReads;

    /// <summary>Finds and triggers in ONE step on the renderer's dispatcher (DrawioEditorDeviceListTest's own helper, and why).</summary>
    private static void Act(IRenderedComponent<DrawioEditor> cut, Action gesture) =>
        cut.InvokeAsync(gesture).GetAwaiter().GetResult();

    [Fact]
    public void TheStripHasOneButtonPerSectionAndNoWindowIsOpenAtFirst()
    {
        var cut = RenderEditor();

        Assert.Equal(Sections.Select(id => $"{id}-toggle"),
            cut.FindAll(".drawio-sections > .drawio-section-toggle").Select(button => button.Id));
        foreach (var id in Sections)
        {
            Assert.Equal("false", cut.Find($"#{id}-toggle").GetAttribute("aria-pressed"));
            Assert.True(cut.Find($"#{id}").HasAttribute("hidden"), $"{id} is open at first.");
        }
    }

    [Fact]
    public void TwoWindowsStayOpenTogetherAndClosingOneLeavesTheOther()
    {
        var cut = RenderEditor();
        Act(cut, () => cut.Find("#drawio-device-list-toggle").Click());
        Act(cut, () => cut.Find("#drawio-mission-list-toggle").Click());
        Assert.False(cut.Find("#drawio-device-list").HasAttribute("hidden"));
        Assert.False(cut.Find("#drawio-mission-list").HasAttribute("hidden"));
        Assert.Equal("true", cut.Find("#drawio-device-list-toggle").GetAttribute("aria-pressed"));
        Assert.Equal("true", cut.Find("#drawio-mission-list-toggle").GetAttribute("aria-pressed"));

        Act(cut, () => cut.Find("#drawio-device-list-close").Click());
        Assert.True(cut.Find("#drawio-device-list").HasAttribute("hidden"));
        Assert.Equal("false", cut.Find("#drawio-device-list-toggle").GetAttribute("aria-pressed"));
        Assert.False(cut.Find("#drawio-mission-list").HasAttribute("hidden"));
    }

    /// <summary>
    /// With no windows module to say otherwise (bUnit's loose module answers false to
    /// bringToFrontIfBehind), an open window is the one in front, and its button shuts it.
    /// </summary>
    [Fact]
    public void TheButtonOfAnOpenWindowClosesIt()
    {
        var cut = RenderEditor();
        Act(cut, () => cut.Find("#drawio-junction-list-toggle").Click());
        Assert.False(cut.Find("#drawio-junction-list").HasAttribute("hidden"));

        Act(cut, () => cut.Find("#drawio-junction-list-toggle").Click());
        Assert.True(cut.Find("#drawio-junction-list").HasAttribute("hidden"));
    }

    /// <summary>
    /// The owner's decision D1 (29/9): the button of a window open BEHIND another brings it to the
    /// front. Which one is in front is the windows module's to say: it is asked, with the window's id,
    /// and when it answers that it brought the window to the front the window stays open. (That the
    /// module answers true for a covered window and false for the one in front, and what the operator
    /// then sees, is DrawioWindowsBrowserTest's.)
    /// </summary>
    [Fact]
    public void TheButtonOfAWindowTheModuleBringsToTheFrontLeavesItOpen()
    {
        var cut = RenderEditor();
        _windows.Setup<bool>("bringToFrontIfBehind", _ => true).SetResult(true);
        Act(cut, () => cut.Find("#drawio-junction-list-toggle").Click());
        Assert.False(cut.Find("#drawio-junction-list").HasAttribute("hidden"));
        // Opening asks nothing: the window is shown, and show() puts it in front anyway.
        Assert.Empty(_windows.Invocations["bringToFrontIfBehind"]);

        Act(cut, () => cut.Find("#drawio-junction-list-toggle").Click());

        Assert.Equal("drawio-junction-list", Assert.Single(_windows.Invocations["bringToFrontIfBehind"]).Arguments.Single());
        Assert.False(cut.Find("#drawio-junction-list").HasAttribute("hidden"));
        Assert.Equal("true", cut.Find("#drawio-junction-list-toggle").GetAttribute("aria-pressed"));
    }

    /// <summary>
    /// The X shuts, it does not toggle: the second press of a fast double click - which can reach the
    /// X before the render that hides it - leaves the window shut, and does not write the memory again.
    /// </summary>
    [Fact]
    public void ASecondPressOnTheXLeavesTheWindowShut()
    {
        var cut = RenderEditor();
        Act(cut, () => cut.Find("#drawio-device-list-toggle").Click());
        Assert.False(cut.Find("#drawio-device-list").HasAttribute("hidden"));

        Act(cut, () => cut.Find("#drawio-device-list-close").Click());
        Act(cut, () => cut.Find("#drawio-device-list-close").Click());

        Assert.True(cut.Find("#drawio-device-list").HasAttribute("hidden"));
        Assert.Equal("false", cut.Find("#drawio-device-list-toggle").GetAttribute("aria-pressed"));
        Assert.Equal(2, _windows.Invocations["rememberOpen"].Count);
    }

    [Fact]
    public void TheDeviceListReadsTheConfigurationsOnlyOnceItsWindowIsOpen()
    {
        var cut = RenderEditor();
        Assert.Equal(0, ConfigurationListReads);

        Act(cut, () => cut.Find("#drawio-device-list-toggle").Click());

        cut.WaitForAssertion(() => Assert.Equal(1, ConfigurationListReads));
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("#drawio-device-list-table tbody tr").Count));
    }

    [Fact]
    public void TheWindowsOpenLastTimeAreOpenedAgain()
    {
        // The windows module answers openedLastTime(): the page opens those, and only those.
        var cut = RenderEditor(openedLastTime: ["drawio-mission-list", "drawio-plc-connections"]);

        cut.WaitForAssertion(() =>
        {
            Assert.False(cut.Find("#drawio-mission-list").HasAttribute("hidden"));
            Assert.False(cut.Find("#drawio-plc-connections").HasAttribute("hidden"));
        });
        foreach (var id in Sections.Except(["drawio-mission-list", "drawio-plc-connections"]))
            Assert.True(cut.Find($"#{id}").HasAttribute("hidden"), $"{id} was not open last time, and it is open now.");
    }

    /// <summary>
    /// Every opening and every closing - from the strip's button or from the window's X - writes down
    /// the windows open NOW, in the order of the strip: the memory the next visit reads.
    /// </summary>
    [Fact]
    public void EveryOpeningAndClosingRemembersTheWindowsOpenNow()
    {
        var cut = RenderEditor();
        Act(cut, () => cut.Find("#drawio-mission-list-toggle").Click());
        Act(cut, () => cut.Find("#drawio-operations-toggle").Click());
        Act(cut, () => cut.Find("#drawio-mission-list-close").Click());

        var remembered = _windows.Invocations["rememberOpen"].Select(call => (string[])call.Arguments[0]!).ToList();
        Assert.Equal(3, remembered.Count);
        Assert.Equal(["drawio-mission-list"], remembered[0]);
        Assert.Equal(["drawio-operations", "drawio-mission-list"], remembered[1]);
        Assert.Equal(["drawio-operations"], remembered[2]);
    }

    // ---- The title bars and the first lines of the windows (passata grafica, 2026-09-29) ---------

    // The paragraphs each window opened with until the passata grafica, copied from the sources
    // before they moved behind the "?" - word for word, their line breaks read as the browser reads
    // them: one space.
    private const string PlantFileWords = "The export reads the plant from the host - every device with its configuration, the junctions, the door maps, the positions, the PLC connections and the free drawing added by hand - into one file. The import replaces the whole plant with a file's, all or nothing, and cannot be undone: it asks for the edit permit at the confirmation, and says first what it is about to lose. It goes through only with the engine stopped (Stop), and not while the plant has missions or rows in the extraction queue. A file exported before 2026-09-19 does not carry the free drawing and leaves it alone - the confirmation says which kind you chose. A file waiting for its confirmation keeps waiting while this section is shut.";
    private const string DevicesWords = "Every device of the plant with its configuration, as the host has it. Click a row to select the device on the drawing. Double-click a cell to change it and press \"Write\": the edit permit is asked for then, and only that device is written.";
    private const string JunctionsWords = "Every junction of the plant as it was last read - saved arrows only, not the ones drawn since. Read-only, as in the WinForms list: click a row to select its arrow on the drawing.";
    private const string MissionsWords = "Every active mission, as the host has it, followed live. Click a row to select on the drawing the device the mission is on, and to choose that mission to move it onto another device or to set its destination.";
    private const string PlcWords = "The PLC connections, as the host has them. Change, add and remove rows here, then press \"Save\": the whole list replaces the host's, after a question that says what changes. Saving asks for the edit permit and needs the engine stopped. The engine builds its PLC connections when the plant is loaded (host restart or route-diagram reload), not when it is started: a changed address or port takes effect only then, and a connection still used by devices cannot be removed - their commands to that PLC would fail.";
    private const string ChecksWords = "These two checks are about the WHOLE plant, never about the device selected in the drawing, and neither of them writes anything: they work with the engine running too. \"Check Junctions\" reads the plant as this page has it - what you have drawn and not saved yet is not in it, so a junction you have just traced counts only after the save. Creating a device - which is what dropping a shape on the canvas does - changes the plant these results were computed on, and the results go with it: they are dropped rather than left standing next to a plant that has moved under them. A save does the same, and for the same reason: it reads the plant again, so from then on the results speak about a plant that is no longer the one in front of you. Press the check again.";
    private const string GoToWords = "An isolated device shows up below as a button. One click asks draw.io to select it and bring it into view, with its properties in the column on the right, and the page says what came back: a device that is also unplaced, or one whose shape has been deleted from the canvas, has no cell to land on, and that is said instead of a click that appears to do nothing. A dangling junction is not offered below at all - it is named above and nowhere else - and there would be nothing on the canvas to select for it either.";

    /// <summary>
    /// Each section's long explanation is behind the "?" of its window's title bar
    /// (<c>#&lt;window&gt;-help</c>), shut until the "?" is pressed, with the id or the class it had on
    /// screen, once in the page, and with the words it had (the literals above). Operations had no
    /// paragraph, and has none to hide.
    /// </summary>
    [Theory]
    [InlineData("drawio-plant-file", "#drawio-plant-file-scope", PlantFileWords)]
    [InlineData("drawio-device-list", ".drawio-device-list-scope", DevicesWords)]
    [InlineData("drawio-junction-list", ".drawio-junction-list-scope", JunctionsWords)]
    [InlineData("drawio-mission-list", ".drawio-mission-list-scope", MissionsWords)]
    [InlineData("drawio-plc-connections", ".drawio-plc-connections-scope", PlcWords)]
    [InlineData("drawio-checks", "#drawio-checks-scope", ChecksWords)]
    [InlineData("drawio-checks", "#drawio-checks-goto-hint", GoToWords)]
    public void EachWindowsQuestionMarkOpensItsParagraphOfTodayWordForWord(string window, string paragraph, string words)
    {
        var cut = RenderEditor();
        Act(cut, () => cut.Find($"#{window}-toggle").Click());
        cut.WaitForAssertion(() => Assert.False(cut.Find($"#{window}").HasAttribute("hidden")));

        Assert.NotNull(cut.Find($"#{window} > .drawio-window-title").QuerySelector($"button#{window}-help"));
        Assert.True(cut.Find($"#{window}-help-body").HasAttribute("hidden"), $"The note of {window} is open before its \"?\" is pressed.");
        Act(cut, () => cut.Find($"#{window}-help").Click());
        Assert.False(cut.Find($"#{window}-help-body").HasAttribute("hidden"), $"The \"?\" of {window} did not open its note.");

        var text = Assert.Single(cut.FindAll(paragraph));
        Assert.NotNull(text.Closest($"#{window}-help-body"));
        Assert.Equal(words, Regex.Replace(text.TextContent, @"\s+", " ").Trim());
    }

    /// <summary>
    /// Operations names the device selected on the drawing, which the page hands it: until the final
    /// fix round of the passata grafica it said "SelectionState.ConfigSubjectName" - the page passed the
    /// words of the expression instead of its value (an attribute without its @).
    /// </summary>
    [Fact]
    public async Task OperationsNamesTheDeviceSelectedOnTheDrawing()
    {
        var cut = RenderEditor();
        Act(cut, () => cut.Find("#drawio-operations-toggle").Click());
        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([A.ToString()]));

        cut.WaitForAssertion(() => Assert.Equal("Selected on the drawing: Shuttle 1",
            Regex.Replace(cut.Find("#drawio-test-route-selected").TextContent, @"\s+", " ").Trim()));
    }

    [Fact]
    public void OperationsHasNoQuestionMark()
    {
        var cut = RenderEditor();
        Act(cut, () => cut.Find("#drawio-operations-toggle").Click());
        Assert.Empty(cut.FindAll("#drawio-operations-help"));
    }

    /// <summary>
    /// A list's title bar counts the rows the list shows - the filter narrows it with them, in each of
    /// the three lists that have one (each counts by an expression of its own) - and the windows that
    /// are not lists count nothing. Four different numbers, so that no count can be another list's. The
    /// PLC connections count the connections the grid will save, as its own line does: a row removed
    /// stays on screen, struck, until the save, and is no longer counted.
    /// </summary>
    [Fact]
    public void EachListCountsInItsTitleBarTheRowsItShows()
    {
        _host.Junctions = [new JunctionDto(Guid.NewGuid(), A, B, 1)];
        _host.Missions =
        [
            TestMissions.Mission(Guid.NewGuid(), A, "LU-001"),
            TestMissions.Mission(Guid.NewGuid(), A, "LU-002"),
            TestMissions.Mission(Guid.NewGuid(), B, "LU-003"),
        ];
        _host.PlcConnections =
        [
            new(1, "Line 1", "10.0.0.1", 2011, "10.0.0.2", 2012),
            new(2, "Line 2", "10.0.0.3", 2021, "10.0.0.4", 2022),
            new(3, "Line 3", "10.0.0.5", 2031, "10.0.0.6", 2032),
            new(4, "Line 4", "10.0.0.7", 2041, "10.0.0.8", 2042),
        ];
        var cut = RenderEditor();
        foreach (var id in Sections) Act(cut, () => cut.Find($"#{id}-toggle").Click());
        Act(cut, () => cut.Find("#drawio-checks-toggle").Click());

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("2", CountOf(cut, "drawio-device-list"));
            Assert.Equal("1", CountOf(cut, "drawio-junction-list"));
            Assert.Equal("3", CountOf(cut, "drawio-mission-list"));
            Assert.Equal("4", CountOf(cut, "drawio-plc-connections"));
        });
        foreach (var id in new[] { "drawio-plant-file", "drawio-operations", "drawio-checks" })
            Assert.Null(CountOf(cut, id));

        Act(cut, () => cut.Find("#drawio-device-list-filter").Input("Shuttle 2"));
        cut.WaitForAssertion(() => Assert.Equal("1", CountOf(cut, "drawio-device-list")));
        Assert.Equal("1 of 2 devices.", cut.Find("#drawio-device-list-count").TextContent);

        Act(cut, () => cut.Find("#drawio-junction-list-filter").Input("no such junction"));
        cut.WaitForAssertion(() => Assert.Equal("0", CountOf(cut, "drawio-junction-list")));
        Assert.Empty(cut.FindAll("#drawio-junction-list-table tbody tr"));

        Act(cut, () => cut.Find("#drawio-mission-list-filter").Input("LU-003"));
        cut.WaitForAssertion(() => Assert.Equal("1", CountOf(cut, "drawio-mission-list")));
        Assert.Single(cut.FindAll("#drawio-mission-list-table tbody tr"));

        Act(cut, () => cut.Find("#drawio-plc-filter").Input("Line 1"));
        Assert.Equal("4", CountOf(cut, "drawio-plc-connections"));
        Assert.Equal("1 shown · 4 total · 4 active", cut.Find("#drawio-plc-count").TextContent.Trim());
        Act(cut, () => cut.Find("#drawio-plc-table tbody tr:first-child button.drawio-plc-remove").Click());
        cut.WaitForAssertion(() => Assert.Equal("3", CountOf(cut, "drawio-plc-connections")));
        Assert.Single(cut.FindAll("#drawio-plc-table tbody tr"));
        Assert.NotEmpty(cut.FindAll("#drawio-plc-table button.drawio-plc-restore"));
        Assert.Equal("1 shown · 4 total · 3 active", cut.Find("#drawio-plc-count").TextContent.Trim());
        Act(cut, () => cut.Find("#drawio-plc-filter").Input(""));
        Assert.Equal(4, cut.FindAll("#drawio-plc-table tbody tr").Count);
        Assert.Equal("4 shown · 4 total · 3 active", cut.Find("#drawio-plc-count").TextContent.Trim());
    }

    /// <summary>
    /// A list that has not read its rows yet counts nothing in its title bar - no "0" that would say it
    /// has none - and counts them once they are read.
    /// </summary>
    [Fact]
    public async Task AListCountsNothingInItsTitleBarUntilItHasReadItsRows()
    {
        _host.Missions = [TestMissions.Mission(Guid.NewGuid(), A, "LU-001")];
        _host.MissionsGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cut = RenderEditor();
        Act(cut, () => cut.Find("#drawio-mission-list-toggle").Click());
        cut.WaitForAssertion(() => Assert.False(cut.Find("#drawio-mission-list").HasAttribute("hidden")));
        await _host.MissionsGateReached.Task;

        Assert.Null(CountOf(cut, "drawio-mission-list"));

        _host.MissionsGate.SetResult();
        cut.WaitForAssertion(() => Assert.Equal("1", CountOf(cut, "drawio-mission-list")));
    }

    /// <summary>
    /// The first line of a list's window is its toolbar: the free-text search first, with its
    /// magnifier inside it, the buttons after. Each title bar wears the icon
    /// of its button in the bar. And the colours: Write
    /// (the device list's cell being changed) and Save (the PLC grid) are the one blue button of their
    /// window, Remove All LU is red.
    /// </summary>
    [Fact]
    public void EachWindowsFirstLineIsItsToolbarAndItsTitleBarWearsItsButtonsIcon()
    {
        var cut = RenderEditor();
        foreach (var id in Sections) Act(cut, () => cut.Find($"#{id}-toggle").Click());
        Act(cut, () => cut.Find("#drawio-checks-toggle").Click());
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("#drawio-device-list-table tbody tr").Count));
        cut.WaitForAssertion(() => Assert.NotNull(CountOf(cut, "drawio-plc-connections")));

        foreach (var (window, filter, icon) in new[]
                 {
                     ("drawio-device-list", "drawio-device-list-filter", "search"),
                     ("drawio-junction-list", "drawio-junction-list-filter", "search"),
                     ("drawio-mission-list", "drawio-mission-list-filter", "search"),
                 })
        {
            var tools = FirstLineOf(cut, window);
            Assert.Contains("drawio-device-list-tools", tools.ClassList);
            var first = tools.FirstElementChild!;
            Assert.Contains("drawio-filter", first.ClassList);
            var drawn = first.QuerySelector("svg");
            Assert.True(drawn is not null, $"The filter of {window} has no icon.");
            drawn!.MarkupMatches(Render<DrawioIcon>(p => p.Add(component => component.Name, icon)).Markup);
            Assert.NotNull(first.QuerySelector($"input#{filter}[type=search]"));
        }
        Assert.Contains("drawio-device-list-tools", FirstLineOf(cut, "drawio-plc-connections").ClassList);
        Assert.Equal("checks-panel", FirstLineOf(cut, "drawio-checks").ClassName);
        Assert.Equal("drawio-plant-file-commands", FirstLineOf(cut, "drawio-plant-file").Id);

        foreach (var id in Sections.Append("drawio-checks"))
            cut.Find($"#{id} > .drawio-window-title > svg").MarkupMatches(cut.Find($"#{id}-toggle > svg").OuterHtml);

        Assert.Contains("drawio-btn-primary", cut.Find("#drawio-plc-save").ClassList);
        Assert.Contains("drawio-btn-danger", cut.Find("#drawio-remove-all-lu-button").ClassList);
        Act(cut, () => cut.Find($"#drawio-device-list-table tr[data-item-id='{A}'] td[data-property='MaxUnloadWeight']").DoubleClick());
        Assert.Contains("drawio-btn-primary", cut.Find("#drawio-device-list-editor-write").ClassList);
        foreach (var id in Sections.Append("drawio-checks"))
            Assert.True(cut.FindAll($"#{id} .drawio-btn-primary").Count <= 1, $"{id} has more than one blue button.");
    }

    private static string? CountOf(IRenderedComponent<DrawioEditor> cut, string window) =>
        cut.Find($"#{window} > .drawio-window-title").QuerySelector(".drawio-window-count")?.TextContent;

    /// <summary>The first element of a window's content, down through the section's own wrapper when it has one.</summary>
    private static AngleSharp.Dom.IElement FirstLineOf(IRenderedComponent<DrawioEditor> cut, string window)
    {
        var first = cut.Find($"#{window} > .drawio-window-content").FirstElementChild!;
        return first.ClassList.Any(name => name.EndsWith("-body", StringComparison.Ordinal)) ? first.FirstElementChild! : first;
    }
}
