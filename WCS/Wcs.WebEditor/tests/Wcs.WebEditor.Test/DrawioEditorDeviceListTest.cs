using System.Net;
using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Drawio.Components;
using Wcs.WebEditor.Drawio.Coordination;
using Wcs.WebEditor.Pages;
using Wcs.WebEditor.Properties;
using Wcs.WebEditor.Session;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

/// <summary>
/// Compito 2, row 58: the device list of the draw.io page, EDITABLE like the WinForms grid
/// (<c>FrmAUTOMATION_ITEMS_LIST</c>: every configuration column, each changed cell saves that
/// device). Measured through the WHOLE page on purpose - the write of a cell goes through the page's
/// own write gate, the edit permit taken at the moment and the same configuration command the
/// properties panel uses, and the coherence between the two (after a write from one, the other
/// rereads) only exists where both are wired together.
/// </summary>
public sealed class DrawioEditorDeviceListTest : PlantCanvasBunitContext
{
    private static readonly Guid A = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002");
    private static readonly Guid C = Guid.Parse("cccccccc-0000-4000-8000-000000000003");

    private readonly DrawioWriteHandler _host = new();
    private readonly FakePlantEventSource _source = new();
    private BunitJSModuleInterop _bridge = null!;

    public DrawioEditorDeviceListTest()
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

    /// <param name="selectCell">
    /// The drawing's answer to <c>selectCell</c>; null leaves every such call pending, for the
    /// test that needs a row's selection still on its way.
    /// </param>
    private IRenderedComponent<DrawioEditor> RenderPage(string? selectCell = "ok")
    {
        _bridge = JSInterop.SetupModule("./_content/Wcs.WebEditor/drawio-bridge.js");
        var selecting = _bridge.Setup<string?>("selectCell", _ => true);
        if (selectCell is not null) selecting.SetResult(selectCell);
        _bridge.Setup<bool>("renameCell", _ => true).SetResult(true);
        var state = new EditorSessionState(new Uri("http://localhost:5580"), _host) { EventSourceForTesting = () => _source };
        state.UseKey("test-key");
        Services.AddSingleton(state);
        var cut = Render<DrawioEditor>();
        // The plant is in once the mode switch is enabled (DrawioEditorModeSwitchTest's own wait).
        cut.WaitForAssertion(() => Assert.False(cut.Find("#drawio-mode-toggle").HasAttribute("disabled")));
        return cut;
    }

    private int ConfigListReads => _host.Requests.Count(r => r == "GET /api/v1/plant/items/config");

    /// <summary>The reads of every configuration the page makes by itself when it loads the plant: <c>PlantGraph.LoadAsync</c>'s, since queue item 21.</summary>
    private const int PlantLoadConfigReads = 1;

    /// <summary>
    /// Finds and triggers in ONE step on the renderer's dispatcher. The page keeps rendering on its
    /// own - the panel reads a device's warehouse cells after its configuration, the live channel
    /// opens - and an element found on one render and triggered after the next one has lost its
    /// handler (bUnit's UnknownEventHandlerIdException, measured on this suite: one run in five).
    /// </summary>
    private static void Act(IRenderedComponent<DrawioEditor> cut, Action gesture) =>
        cut.InvokeAsync(gesture).GetAwaiter().GetResult();

    /// <summary>
    /// Selects a device as the bridge reports it and waits for the panel to settle: the device is
    /// Automation, so after its configuration the panel reads the warehouse cells too, and typing
    /// into it before that second read has landed races a render (DrawioEditorModeSwitchTest's own
    /// finding).
    /// </summary>
    private static async Task SelectAndSettleAsync(IRenderedComponent<DrawioEditor> cut, Guid itemId)
    {
        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([itemId.ToString()]));
        cut.WaitForAssertion(() =>
        {
            Assert.NotEmpty(cut.FindAll("div.property-panel-field[data-property='Name'] input"));
            Assert.Empty(cut.FindAll("#drawio-cells-loading"));
        });
    }

    private static void TypeIntoThePanel(IRenderedComponent<DrawioEditor> cut, string property, string text) =>
        Act(cut, () => cut.Find($"div.property-panel-field[data-property='{property}'] input").Change(text));

    private static void SaveThePanel(IRenderedComponent<DrawioEditor> cut) =>
        Act(cut, () => cut.Find("#property-panel-save-button").Click());

    private void OpenTheList(IRenderedComponent<DrawioEditor> cut)
    {
        Act(cut, () => cut.Find("#drawio-device-list-toggle").Click());
        cut.WaitForAssertion(() => Assert.Equal(Math.Min(_host.Items.Count, 10), Rows(cut).Count));
    }

    private static IReadOnlyList<string> Rows(IRenderedComponent<DrawioEditor> cut) =>
        [.. cut.FindAll("#drawio-device-list-table tbody tr").Select(row => row.GetAttribute("data-item-id") ?? "")];

    private static AngleSharp.Dom.IElement Cell(IRenderedComponent<DrawioEditor> cut, Guid itemId, string property) =>
        cut.Find($"#drawio-device-list-table tr[data-item-id='{itemId}'] td[data-property='{property}']");

    private static string CellText(IRenderedComponent<DrawioEditor> cut, Guid itemId, string property) =>
        Cell(cut, itemId, property).TextContent.Trim();

    private static string Outcome(IRenderedComponent<DrawioEditor> cut) =>
        cut.Find("#drawio-device-list-outcome").TextContent;

    /// <summary>Opens the editor of one cell and types into it - a change that is NOT a confirmation yet.</summary>
    private static void Type(IRenderedComponent<DrawioEditor> cut, Guid itemId, string property, string text)
    {
        Act(cut, () => Cell(cut, itemId, property).DoubleClick());
        Act(cut, () => cut.Find("#drawio-device-list-editor-value").Input(text));
    }

    private static void Confirm(IRenderedComponent<DrawioEditor> cut) =>
        Act(cut, () => cut.Find("#drawio-device-list-editor-write").Click());

    // ---- Reading --------------------------------------------------------------------------------

    /// <summary>
    /// Nothing is read while the section is shut; opening it reads EVERY configuration with ONE
    /// request (a plant of a few hundred devices is not read one device at a time), and the columns
    /// are the configuration's own - the same fields the properties panel knows.
    /// </summary>
    /// <remarks>
    /// Since queue item 21 the page itself reads every configuration ONCE when it loads the plant
    /// (<c>PlantGraph.LoadAsync</c>, for the load unit sizes): that one read is the baseline, and
    /// "nothing while shut" is nothing on top of it.
    /// </remarks>
    [Fact]
    public void OpeningTheListReadsEveryConfigurationOnceAndShowsItsColumns()
    {
        var cut = RenderPage();
        Assert.Equal(PlantLoadConfigReads, ConfigListReads);

        OpenTheList(cut);

        Assert.Equal(PlantLoadConfigReads + 1, ConfigListReads);
        var headers = cut.FindAll("#drawio-device-list-table thead th").Select(th => th.GetAttribute("data-property")).ToHashSet();
        foreach (var field in PropertyModel.BuildGroups().SelectMany(group => group.Fields))
            Assert.Contains(field.PropertyName, headers);
        Assert.Equal("Shuttle 1", CellText(cut, A, "Name"));
        Assert.Equal("Automation", CellText(cut, A, "DeviceType"));
        Assert.Equal("900", CellText(cut, A, "MaxUnloadWeight"));
        Assert.Equal("100", CellText(cut, B, "MaxUnloadWeight"));
    }

    // ---- Writing a cell ---------------------------------------------------------------------------

    /// <summary>
    /// A changed cell writes nothing until it is confirmed; confirmed, it writes THAT device and no
    /// other, through the edit permit, with every other field as the host had it - and the row then
    /// shows what the host holds.
    /// </summary>
    [Fact]
    public void ACellChangedAndConfirmedWritesThatDeviceOnly()
    {
        var cut = RenderPage();
        OpenTheList(cut);

        Type(cut, A, "MaxUnloadWeight", "750");
        Assert.Empty(_host.ConfigWrites);

        Confirm(cut);

        cut.WaitForAssertion(() => Assert.Contains("Written", Outcome(cut)));
        var written = Assert.Single(_host.ConfigWrites);
        Assert.Equal(A, written.ItemId);
        Assert.Equal(750, written.MaxUnloadWeight);
        Assert.Equal("Shuttle 1", written.Name);
        Assert.Equal((short)ConnectionTypes.Test, written.ConnectionType);
        Assert.True(_host.EditLockRequests > 0, "The edit permit was never asked for.");
        cut.WaitForAssertion(() => Assert.Equal("750", CellText(cut, A, "MaxUnloadWeight")));
        Assert.Equal("100", CellText(cut, B, "MaxUnloadWeight"));
    }

    /// <summary>A new name written from the list reaches the drawing's label too, as a rename from the panel does.</summary>
    [Fact]
    public void ANameWrittenFromTheListRenamesTheShapeOnTheDrawing()
    {
        var cut = RenderPage();
        OpenTheList(cut);

        Type(cut, A, "Name", "Lift 7");
        Confirm(cut);

        cut.WaitForAssertion(() => Assert.Equal("Lift 7", Assert.Single(_host.ConfigWrites).Name));
        cut.WaitForAssertion(() => Assert.Contains(_bridge.Invocations["renameCell"],
            call => (call.Arguments[0] as string) == A.ToString() && (call.Arguments[1] as string) == "Lift 7"));
    }

    /// <summary>
    /// Row 37 of the queue (the owner's answer of 9/10): the label the drawing could not be told of is
    /// said in the list with the same words as in the properties panel - one sentence, and the
    /// drawing's own failure behind the line's "?", where the sentence used to quote it in brackets.
    /// </summary>
    [Fact]
    public void ANameTheDrawingCannotBeToldOfIsSaidWithTheDrawingsFailureBehindTheQuestionMark()
    {
        var cut = RenderPage();
        _bridge.Setup<bool>("renameCell", call => (call.Arguments[1] as string) == "Lift 7")
            .SetException(new JSException("the canvas did not take it (test)"));
        OpenTheList(cut);

        Type(cut, A, "Name", "Lift 7");
        Confirm(cut);

        cut.WaitForAssertion(() => Assert.Equal(
            "Written: the Name of Shuttle 1 (Shuttle 1 -> Lift 7) is on the plant. The new name is on the plant, but the drawing could not be told, so it still shows the old label until the page is reloaded.",
            Outcome(cut).Trim()));
        Assert.Equal("the canvas did not take it (test)", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-device-list-outcome"));
    }

    /// <summary>
    /// Refused by the page (the engine is running): nothing written, the row as it was, and the
    /// outcome line says why.
    /// </summary>
    [Fact]
    public void ARefusalLeavesTheRowAsItWasAndSaysWhy()
    {
        _host.NavStatus = NavigatorStatusTypes.Run;
        var cut = RenderPage();
        OpenTheList(cut);

        Type(cut, A, "MaxUnloadWeight", "750");
        Confirm(cut);

        cut.WaitForAssertion(() => Assert.Contains("Read only: the engine is running", Outcome(cut)));
        Assert.Empty(_host.ConfigWrites);
        Assert.Empty(cut.FindAll("#drawio-device-list-editor-value"));
        Assert.Equal("900", CellText(cut, A, "MaxUnloadWeight"));
    }

    /// <summary>
    /// Refused by the HOST itself (409 or 423 on the write, in the window after the page's own
    /// check): the row keeps what the plant holds, and the line says the host REFUSED it and what that
    /// means on the route - not that it is unknown whether it went through. The host's own words are
    /// behind the line's "?" with the rest of its answer, not in the sentence (row 37 of the queue,
    /// extended to the whole page on 10/10).
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Conflict, "the plant's state does not allow it now", "Il motore non \u00e8 fermo",
        "the host answered 409: {\"title\":\"Il motore non \\u00E8 fermo\",\"status\":409}")]
    [InlineData(HttpStatusCode.Locked, "the edit permit is not (any longer) this session's", "Permesso di modifica di qualcun altro",
        "the host answered 423: {\"title\":\"Permesso di modifica di qualcun altro\",\"status\":423}")]
    public void ARefusalByTheHostLeavesTheRowAsItWasAndSaysTheHostRefusedIt(HttpStatusCode status, string meaning, string hostWords, string answer)
    {
        var cut = RenderPage();
        OpenTheList(cut);
        _host.FailRoute = "config";
        _host.FailureStatus = status;
        _host.FailureBody = JsonSerializer.Serialize(new { title = hostWords, status = (int)status });

        Type(cut, A, "MaxUnloadWeight", "750");
        Confirm(cut);

        cut.WaitForAssertion(() => Assert.Contains("the host refused it", Outcome(cut)));
        Assert.Equal($"Not written - the MaxUnloadWeight of Shuttle 1 (900 -> 750): the host refused it - {meaning}. Nothing reached the plant; the row shows what the plant holds.",
            Outcome(cut));
        Assert.DoesNotContain(hostWords, Outcome(cut));
        Assert.Equal(answer, DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-device-list-outcome"));
        Assert.Equal(900, _host.Configs[A].MaxUnloadWeight);
        Assert.Equal("900", CellText(cut, A, "MaxUnloadWeight"));
    }

    /// <summary>
    /// The list is a writer of its own at the page's write gate: while the properties panel is
    /// writing, a cell is refused with the list's own sentence for that owner, and nothing of it
    /// reaches the host.
    /// </summary>
    [Fact]
    public async Task WhileThePanelIsWritingACellIsRefusedWithTheListsOwnSentence()
    {
        var cut = RenderPage();
        OpenTheList(cut);
        await SelectAndSettleAsync(cut, A);

        _host.SingleItemGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SaveThePanel(cut);
        await _host.SingleItemGateReached.Task;

        Type(cut, B, "MaxUnloadWeight", "150");
        Confirm(cut);

        cut.WaitForAssertion(() => Assert.Contains(DrawioWriteRefusal.ForDeviceList(DrawioPlantWriter.Panel), Outcome(cut)));
        Assert.DoesNotContain(_host.ConfigWrites, write => write.ItemId == B);
        _host.SingleItemGate.SetResult();
    }

    // ---- Columns that are lists of devices ----------------------------------------------------------

    /// <summary>
    /// The Guid columns the WinForms grid showed as a drop-down of devices
    /// (<c>Helpers.GetAutomationDeviceRepositoryItem</c>) are chosen from a list here too - by name,
    /// with a "none" - and the cell shows the device's name, not a bare identifier.
    /// </summary>
    [Fact]
    public void AGuidColumnIsChosenFromTheListOfDevices()
    {
        var cut = RenderPage();
        OpenTheList(cut);

        Act(cut, () => Cell(cut, A, "BuildDestinationItemId").DoubleClick());
        var select = cut.Find("select#drawio-device-list-editor-value");
        var options = select.QuerySelectorAll("option").Select(option => (option.GetAttribute("value"), option.TextContent.Trim())).ToList();
        Assert.Contains(("", "(none)"), options);
        Assert.Contains((A.ToString(), "Shuttle 1"), options);
        Assert.Contains((B.ToString(), "Shuttle 2"), options);

        Act(cut, () => cut.Find("select#drawio-device-list-editor-value").Change(B.ToString()));
        Confirm(cut);

        cut.WaitForAssertion(() => Assert.Equal(B, Assert.Single(_host.ConfigWrites).BuildDestinationItemId));
        cut.WaitForAssertion(() => Assert.Equal("Shuttle 2", CellText(cut, A, "BuildDestinationItemId")));
    }

    // ---- Filter, sort, selection ------------------------------------------------------------------

    [Fact]
    public void TheGridShowsTenDevicesAtATimeAndKeepsItsControlsOutsideTheScrollableRows()
    {
        for (var number = 3; number <= 23; number++)
        {
            var id = NumberedDevice(number);
            _host.Items.Add(Item(id, $"Device {number:00}", number * 100));
            _host.Configs[id] = Config(id, $"Device {number:00}", number);
        }

        var cut = RenderPage();
        OpenTheList(cut);

        Assert.Equal(10, Rows(cut).Count);
        Assert.Equal("1–10 of 23", cut.Find("#drawio-device-list-page-summary").TextContent.Trim());
        Assert.Contains("drawio-data-grid-scroll", cut.Find("#drawio-device-list-table").ParentElement!.ClassList);
        Assert.NotNull(cut.Find("#drawio-device-list-page-summary").Closest("footer"));

        Act(cut, () => cut.Find("#drawio-device-list-next").Click());

        Assert.Equal(10, Rows(cut).Count);
        Assert.Equal("11–20 of 23", cut.Find("#drawio-device-list-page-summary").TextContent.Trim());

        Act(cut, () => cut.Find("#drawio-device-list-next").Click());

        Assert.Equal(3, Rows(cut).Count);
        Assert.Equal("21–23 of 23", cut.Find("#drawio-device-list-page-summary").TextContent.Trim());
    }

    [Fact]
    public void FilteringAndSortingStartTheDeviceGridFromItsFirstPage()
    {
        for (var number = 3; number <= 13; number++)
        {
            var id = NumberedDevice(number);
            _host.Items.Add(Item(id, $"Device {number:00}", number * 100));
            _host.Configs[id] = Config(id, $"Device {number:00}", number);
        }

        var cut = RenderPage();
        OpenTheList(cut);
        Act(cut, () => cut.Find("#drawio-device-list-next").Click());

        Act(cut, () => cut.Find("#drawio-device-list-table th[data-property='Name']").Click());
        Assert.Equal("1–10 of 13", cut.Find("#drawio-device-list-page-summary").TextContent.Trim());

        Act(cut, () => cut.Find("#drawio-device-list-next").Click());
        Act(cut, () => cut.Find("#drawio-device-list-filter").Input("Shuttle 2"));

        Assert.Equal([B.ToString()], Rows(cut));
        Assert.Equal("1 of 1", cut.Find("#drawio-device-list-page-summary").TextContent.Trim());
        var filter = cut.Find("#drawio-device-list-filter");
        Assert.Equal("search", filter.ParentElement!.QuerySelector(".drawio-icon")!.GetAttribute("data-icon"));
        Assert.Equal("Filter devices", filter.GetAttribute("aria-label"));
    }

    [Fact]
    public async Task ADeviceSelectedOnTheDrawingBringsItsOffPageRowIntoView()
    {
        for (var number = 3; number <= 13; number++)
        {
            var id = NumberedDevice(number);
            _host.Items.Add(Item(id, $"Device {number:00}", number * 100));
            _host.Configs[id] = Config(id, $"Device {number:00}", number);
        }

        var cut = RenderPage();
        OpenTheList(cut);
        Assert.DoesNotContain(B.ToString(), Rows(cut));

        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([B.ToString()]));

        cut.WaitForAssertion(() => Assert.Equal("true",
            cut.Find($"#drawio-device-list-table tr[data-item-id='{B}']").GetAttribute("aria-selected")));
        Assert.Equal("11–13 of 13", cut.Find("#drawio-device-list-page-summary").TextContent.Trim());
    }

    [Fact]
    public void TheFilterKeepsOnlyTheRowsThatMatch()
    {
        var cut = RenderPage();
        OpenTheList(cut);

        Act(cut, () => cut.Find("#drawio-device-list-filter").Input("shuttle 2"));

        Assert.Equal([B.ToString()], Rows(cut));
        Assert.Equal(PlantLoadConfigReads + 1, ConfigListReads);
    }

    private static Guid NumberedDevice(int number) =>
        Guid.Parse($"dddddddd-0000-4000-8000-{number:000000000000}");

    /// <summary>
    /// Three devices, so that the three orders differ: by name (no column chosen) A, B, C; by the
    /// column ascending B, C, A; descending A, C, B. With two rows "descending" and "sort cleared"
    /// would be the same order, and a second press that cleared the sort would pass for a reversal.
    /// </summary>
    [Fact]
    public void AColumnHeaderSortsTheRowsAndPressedAgainReversesThem()
    {
        _host.Items.Add(Item(C, "Shuttle 3", 600));
        _host.Configs[C] = Config(C, "Shuttle 3", maxUnloadWeight: 500);
        var cut = RenderPage();
        OpenTheList(cut);
        Assert.Equal([A.ToString(), B.ToString(), C.ToString()], Rows(cut));

        Act(cut, () => cut.Find("#drawio-device-list-table th[data-property='MaxUnloadWeight']").Click());
        Assert.Equal([B.ToString(), C.ToString(), A.ToString()], Rows(cut));
        Assert.Equal("ascending", cut.Find("#drawio-device-list-table th[data-property='MaxUnloadWeight']").GetAttribute("aria-sort"));

        Act(cut, () => cut.Find("#drawio-device-list-table th[data-property='MaxUnloadWeight']").Click());
        Assert.Equal([A.ToString(), C.ToString(), B.ToString()], Rows(cut));
        Assert.Equal("descending", cut.Find("#drawio-device-list-table th[data-property='MaxUnloadWeight']").GetAttribute("aria-sort"));
    }

    /// <summary>Choosing a row selects that device on the drawing, and the row of the device selected there is marked.</summary>
    [Fact]
    public async Task ChoosingARowSelectsTheDeviceOnTheDrawing()
    {
        var cut = RenderPage();
        await cut.InvokeAsync(() => cut.Instance.OnDrawioReady());
        OpenTheList(cut);

        Act(cut, () => cut.Find($"#drawio-device-list-table tr[data-item-id='{B}']").Click());

        cut.WaitForAssertion(() => Assert.Contains(_bridge.Invocations["selectCell"], call => (call.Arguments[0] as string) == B.ToString()));
        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([B.ToString()]));
        cut.WaitForAssertion(() => Assert.Equal("true",
            cut.Find($"#drawio-device-list-table tr[data-item-id='{B}']").GetAttribute("aria-selected")));
        Assert.Equal("false", cut.Find($"#drawio-device-list-table tr[data-item-id='{A}']").GetAttribute("aria-selected"));
    }

    // ---- Coherence with the properties panel --------------------------------------------------------

    /// <summary>After a write from the properties panel the list reads the plant again, and the row shows the new value.</summary>
    [Fact]
    public async Task AWriteFromThePanelIsReadAgainByTheList()
    {
        var cut = RenderPage();
        OpenTheList(cut);
        await SelectAndSettleAsync(cut, A);
        // After the opening, not a number: since queue item 21 the plant's own load reads every
        // configuration too, and ">= 2" would already hold before the write.
        var afterOpening = ConfigListReads;

        TypeIntoThePanel(cut, "Name", "Renamed in the panel");
        SaveThePanel(cut);

        cut.WaitForAssertion(() => Assert.Equal("Renamed in the panel", CellText(cut, A, "Name")));
        Assert.True(ConfigListReads > afterOpening, "The list did not read the plant again after the panel's write.");
    }

    /// <summary>
    /// After a write from the list on the device the panel is showing, the panel reads it again -
    /// what was typed there and never confirmed is thrown away, and the list's outcome says so.
    /// </summary>
    [Fact]
    public async Task AWriteFromTheListRereadsThePanelOfThatDeviceAndSaysWhatWasThrownAway()
    {
        var cut = RenderPage();
        OpenTheList(cut);
        await SelectAndSettleAsync(cut, A);
        TypeIntoThePanel(cut, "Name", "Typed but never confirmed");

        Type(cut, A, "MaxUnloadWeight", "750");
        Confirm(cut);

        cut.WaitForAssertion(() => Assert.Contains(
            "The properties panel shows this device and has been read again from the host, so anything typed into it and not confirmed is gone.",
            Outcome(cut)));
        cut.WaitForAssertion(() => Assert.Equal("Shuttle 1",
            cut.Find("div.property-panel-field[data-property='Name'] input").GetAttribute("value")));
        cut.WaitForAssertion(() => Assert.Equal("750",
            cut.Find("div.property-panel-field[data-property='MaxUnloadWeight'] input").GetAttribute("value")));
    }

    /// <summary>A write from the list on ANOTHER device leaves the panel alone: nothing of it is thrown away, and nothing is said about it.</summary>
    [Fact]
    public async Task AWriteFromTheListOnAnotherDeviceLeavesThePanelAlone()
    {
        var cut = RenderPage();
        OpenTheList(cut);
        await SelectAndSettleAsync(cut, A);
        var panelReads = _host.Requests.Count(r => r == $"GET /api/v1/plant/items/{A}/config");

        Type(cut, B, "MaxUnloadWeight", "150");
        Confirm(cut);

        cut.WaitForAssertion(() => Assert.Contains("Written", Outcome(cut)));
        Assert.DoesNotContain("properties panel", Outcome(cut));
        Assert.Equal(panelReads, _host.Requests.Count(r => r == $"GET /api/v1/plant/items/{A}/config"));
    }

    /// <summary>
    /// A cell typed into and never confirmed, on the device the panel then writes: the list reads
    /// the plant again, the typed value is thrown away (the row changed under it), and the list says
    /// so with the codebase's own words.
    /// </summary>
    [Fact]
    public async Task ACellTypedButNotConfirmedIsThrownAwayAndSaidWhenThePanelWritesThatDevice()
    {
        var cut = RenderPage();
        OpenTheList(cut);
        await SelectAndSettleAsync(cut, A);

        Type(cut, A, "MaxUnloadWeight", "123");
        TypeIntoThePanel(cut, "Name", "Renamed in the panel");
        SaveThePanel(cut);

        cut.WaitForAssertion(() => Assert.Contains("not confirmed is gone", cut.Find("#drawio-device-list-discarded").TextContent));
        Assert.Empty(cut.FindAll("#drawio-device-list-editor-value"));
        Assert.DoesNotContain(_host.ConfigWrites, write => write.MaxUnloadWeight == 123);
    }

    /// <summary>A cell typed into on ANOTHER device survives the panel's write: the list reread did not change its row.</summary>
    [Fact]
    public async Task ACellTypedOnAnotherDeviceSurvivesThePanelsWrite()
    {
        var cut = RenderPage();
        OpenTheList(cut);
        await SelectAndSettleAsync(cut, A);

        Type(cut, B, "MaxUnloadWeight", "123");
        TypeIntoThePanel(cut, "Name", "Renamed in the panel");
        SaveThePanel(cut);

        cut.WaitForAssertion(() => Assert.Equal("Renamed in the panel", CellText(cut, A, "Name")));
        Assert.Equal("123", cut.Find("#drawio-device-list-editor-value").GetAttribute("value"));
        Assert.Empty(cut.FindAll("#drawio-device-list-discarded"));
    }

    /// <summary>
    /// The plant-wide connection mode switch (row 50) changes every device: the list reads the plant
    /// again, so a cell written afterwards carries the NEW mode and does not silently put the old one
    /// back (the defect compito 1 found in the panel, CRITICAL 1).
    /// </summary>
    [Fact]
    public void AfterTheModeSwitchACellWriteDoesNotPutTheOldModeBack()
    {
        var cut = RenderPage();
        OpenTheList(cut);

        Act(cut, () => cut.Find("#drawio-mode-toggle").Click());
        Act(cut, () => cut.Find("#drawio-mode-confirm").Click());
        cut.WaitForAssertion(() => Assert.Equal("Connected to PLC", CellText(cut, A, "ConnectionType")));

        Type(cut, A, "MaxUnloadWeight", "750");
        Confirm(cut);

        cut.WaitForAssertion(() => Assert.Equal((short)ConnectionTypes.Connected, Assert.Single(_host.ConfigWrites).ConnectionType));
    }

    // ---- Live status --------------------------------------------------------------------------------

    /// <summary>The list follows the live status of the devices, as the drawing does.</summary>
    [Fact]
    public void TheLiveStatusOfADeviceIsShownAndFollowed()
    {
        var cut = RenderPage();
        OpenTheList(cut);
        // The page opens its channel after the plant is in: an event raised before the feed listens
        // reaches nobody. Once the source has been started the feed is subscribed, and an event that
        // lands while its snapshot is still in flight is held and replayed on top of it.
        cut.WaitForAssertion(() => Assert.True(_source.StartCalls > 0));

        _source.RaiseItemStatus(new ItemStatus(A, "", "", "", "Shuttle 1", DeviceStatusTypes.Error, LuLoadStatusTypes.None,
            "", 0m, Guid.Empty, Guid.Empty));

        cut.WaitForAssertion(() => Assert.Equal("In Error", CellText(cut, A, "Status")));
    }

    [Fact]
    public async Task ALiveFilterThatShrinksAndExpandsStaysOnTheClampedPage()
    {
        var ids = new List<Guid> { A, B };
        for (var number = 3; number <= 13; number++)
        {
            var id = NumberedDevice(number);
            ids.Add(id);
            _host.Items.Add(Item(id, $"Device {number:00}", number * 100));
            _host.Configs[id] = Config(id, $"Device {number:00}", number);
        }

        var cut = RenderPage();
        OpenTheList(cut);
        foreach (var id in ids)
            await RaiseAndLetTheFrameLandAsync(cut, LiveStatus(id, DeviceStatusTypes.Error));
        Act(cut, () => cut.Find("#drawio-device-list-filter").Input("In Error"));
        Act(cut, () => cut.Find("#drawio-device-list-next").Click());
        Assert.Equal("11–13 of 13", cut.Find("#drawio-device-list-page-summary").TextContent.Trim());

        foreach (var id in ids.Skip(1))
            await RaiseAndLetTheFrameLandAsync(cut, LiveStatus(id, DeviceStatusTypes.Ready));
        Assert.Equal("1 of 1", cut.Find("#drawio-device-list-page-summary").TextContent.Trim());

        foreach (var id in ids.Skip(1))
            await RaiseAndLetTheFrameLandAsync(cut, LiveStatus(id, DeviceStatusTypes.Error));
        Assert.Equal("1–10 of 13", cut.Find("#drawio-device-list-page-summary").TextContent.Trim());
    }

    // ---- Fix round 1: what "Write" is about to send ---------------------------------------------------

    private static string Preview(IRenderedComponent<DrawioEditor> cut) =>
        cut.Find("#drawio-device-list-editor-preview").TextContent.Trim();

    /// <summary>
    /// Next to "Write" the list shows the change the press will send - the column's name, the value
    /// the host has and the one typed - and what is written is exactly that value (the owner's
    /// decision: one press, with the change in sight).
    /// </summary>
    [Fact]
    public void NextToWriteTheListShowsTheChangeThatIsThenWritten()
    {
        var cut = RenderPage();
        OpenTheList(cut);

        Type(cut, A, "MaxUnloadWeight", "750");

        Assert.Equal("MaxUnloadWeight: 900 \u2192 750", Preview(cut));
        Confirm(cut);
        cut.WaitForAssertion(() => Assert.Equal(750, Assert.Single(_host.ConfigWrites).MaxUnloadWeight));
    }

    /// <summary>A device column previews the devices by name - "(none)" for no device - and writes the device previewed.</summary>
    [Fact]
    public void ADeviceColumnPreviewsTheDevicesByNameAndWritesThePreviewedOne()
    {
        var cut = RenderPage();
        OpenTheList(cut);

        Act(cut, () => Cell(cut, A, "BuildDestinationItemId").DoubleClick());
        Act(cut, () => cut.Find("select#drawio-device-list-editor-value").Change(B.ToString()));

        Assert.Equal("IdDestinationItem: (none) \u2192 Shuttle 2", Preview(cut));
        Confirm(cut);
        cut.WaitForAssertion(() => Assert.Equal(B, Assert.Single(_host.ConfigWrites).BuildDestinationItemId));
    }

    // ---- Fix round 1: the list does no work it does not show ----------------------------------------

    private static IRenderedComponent<DrawioDeviceList> TheList(IRenderedComponent<DrawioEditor> cut) =>
        cut.FindComponent<DrawioDeviceList>();

    private static ItemStatus LiveStatus(Guid itemId, DeviceStatusTypes status, string asi = "", Guid runningStep = default) =>
        new(itemId, asi, "", "", "Shuttle 1", status, LuLoadStatusTypes.None, "", 0m, runningStep, Guid.Empty);

    /// <summary>
    /// Raises one live status and returns once the frame it makes has reached the list AND the work
    /// the list posted for it has run. The gate is the frame's own event: a handler of this test on
    /// the same FrameReady, subscribed after the list's - so it runs after the list has posted its
    /// work to the renderer - and then an empty turn of the renderer's dispatcher, which runs after
    /// everything posted before it. No timed wait decides anything (the timeout only stops a hang).
    /// </summary>
    private async Task RaiseAndLetTheFrameLandAsync(IRenderedComponent<DrawioEditor> cut, ItemStatus status)
    {
        cut.WaitForAssertion(() => Assert.True(_source.StartCalls > 0));
        var feed = TheList(cut).Instance.Feed ?? throw new InvalidOperationException("The list was never handed the live feed.");
        // The snapshot in flight would hold the event back: the frame would come, but later.
        await feed.Settled;
        var frame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Seen() => frame.TrySetResult();
        feed.FrameReady += Seen;
        try
        {
            _source.RaiseItemStatus(status);
            await frame.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            feed.FrameReady -= Seen;
        }

        await cut.InvokeAsync(() => { });
    }

    /// <summary>
    /// Shut, the list follows no live frame: a status that changes while it is shut does not render
    /// it. Opened again, it shows the status as it is now.
    /// </summary>
    [Fact]
    public async Task AFrameWhileTheListIsShutDoesNotRenderIt()
    {
        var cut = RenderPage();
        OpenTheList(cut);
        Act(cut, () => cut.Find("#drawio-device-list-toggle").Click());
        Assert.Empty(cut.FindAll("#drawio-device-list-table"));
        var renders = TheList(cut).RenderCount;

        await RaiseAndLetTheFrameLandAsync(cut, LiveStatus(A, DeviceStatusTypes.Error));

        Assert.Equal(renders, TheList(cut).RenderCount);
        Act(cut, () => cut.Find("#drawio-device-list-toggle").Click());
        cut.WaitForAssertion(() => Assert.Equal("In Error", CellText(cut, A, "Status")));
    }

    /// <summary>
    /// A frame that changes only fields of a device's live record the list does not show (here its
    /// ASI and the step it runs) does not render the list: the status and the load unit it shows are
    /// what they were.
    /// </summary>
    [Fact]
    public async Task AFrameThatChangesNothingTheListShowsDoesNotRenderIt()
    {
        var cut = RenderPage();
        OpenTheList(cut);
        await RaiseAndLetTheFrameLandAsync(cut, LiveStatus(A, DeviceStatusTypes.Error));
        Assert.Equal("In Error", CellText(cut, A, "Status"));
        var renders = TheList(cut).RenderCount;

        await RaiseAndLetTheFrameLandAsync(cut, LiveStatus(A, DeviceStatusTypes.Error, asi: "ASI-7", runningStep: Guid.NewGuid()));

        Assert.Equal(renders, TheList(cut).RenderCount);
    }

    /// <summary>A frame that changes a status the list shows renders it, with the new status.</summary>
    [Fact]
    public async Task AFrameThatChangesAStatusTheListShowsRendersIt()
    {
        var cut = RenderPage();
        OpenTheList(cut);
        var renders = TheList(cut).RenderCount;

        await RaiseAndLetTheFrameLandAsync(cut, LiveStatus(A, DeviceStatusTypes.Error));

        Assert.True(TheList(cut).RenderCount > renders, "The list was not rendered for a status it shows.");
        Assert.Equal("In Error", CellText(cut, A, "Status"));
    }

    /// <summary>
    /// A keystroke in the filter while a row's selection is still on its way to the drawing is
    /// applied. Around the pending selection the page renders and hands the list its parameters
    /// again while the list's own render is already queued: the "nothing changed" flag that leaves
    /// behind must not swallow the list's next render.
    /// </summary>
    [Fact]
    public async Task AFilterTypedWhileARowIsBeingSelectedIsApplied()
    {
        var cut = RenderPage(selectCell: null);
        await cut.InvokeAsync(() => cut.Instance.OnDrawioReady());
        OpenTheList(cut);

        Act(cut, () => cut.Find($"#drawio-device-list-table tr[data-item-id='{B}']").Click());
        cut.WaitForAssertion(() => Assert.Contains(_bridge.Invocations["selectCell"], call => (call.Arguments[0] as string) == B.ToString()));

        Act(cut, () => cut.Find("#drawio-device-list-filter").Input("shuttle 2"));

        Assert.Equal([B.ToString()], Rows(cut));
    }

    /// <summary>
    /// The defence at "Write": the plant moved on this page after the list was read, and the page has
    /// not rendered the list since - a write built on that row could put old values back. Nothing is
    /// written, the list reads again, and the cell (its row did not change) stays open for a second
    /// press, which writes.
    /// </summary>
    [Fact]
    public void AWriteOnARowOlderThanThePlantWritesNothingAndReadsAgain()
    {
        var cut = RenderPage();
        OpenTheList(cut);
        Type(cut, A, "MaxUnloadWeight", "750");
        var reads = ConfigListReads;
        var graph = TheList(cut).Instance.Graph ?? throw new InvalidOperationException("The list was never handed the plant.");

        // The plant moves and "Write" is pressed in the SAME turn of the renderer: the page has had no
        // chance to render the list (and so to read it again) in between.
        Act(cut, () =>
        {
            graph.AddItem(graph.Items.Single(item => item.ItemId == B));
            cut.Find("#drawio-device-list-editor-write").Click();
        });

        cut.WaitForAssertion(() => Assert.Contains("nothing was written", Outcome(cut)));
        cut.WaitForAssertion(() =>
        {
            Assert.Equal(reads + 1, ConfigListReads);
            Assert.Empty(cut.FindAll("#drawio-device-list-reading"));
        });
        Assert.Empty(_host.ConfigWrites);
        Assert.Equal("750", cut.Find("#drawio-device-list-editor-value").GetAttribute("value"));

        Confirm(cut);

        cut.WaitForAssertion(() => Assert.Equal(750, Assert.Single(_host.ConfigWrites).MaxUnloadWeight));
    }

    // ---- Fix round 1: outcomes that must not end the circuit ------------------------------------------

    /// <summary>
    /// The write went through and the reread after it came back unreadable (200 with a body that is
    /// not JSON): the list says it was written and could not be read back - and the page is still
    /// alive to say it (the exception used to escape the click handler and end the circuit).
    /// </summary>
    [Fact]
    public void AnUnreadableRereadAfterTheWriteIsSaidAsWrittenNotReadBack()
    {
        var cut = RenderPage();
        OpenTheList(cut);
        Type(cut, A, "MaxUnloadWeight", "750");
        _host.GarbledRoute = "item";

        Confirm(cut);

        cut.WaitForAssertion(() => Assert.Contains("could not be read back afterwards", Outcome(cut)));
        Assert.StartsWith("Written - the MaxUnloadWeight of Shuttle 1", Outcome(cut));
        Assert.Equal(750, Assert.Single(_host.ConfigWrites).MaxUnloadWeight);
    }

    /// <summary>The edit permit's answer came back unreadable: nothing was sent, and the list says so instead of ending the circuit.</summary>
    [Fact]
    public void AnUnreadablePermitAnswerWritesNothingAndSaysSo()
    {
        var cut = RenderPage();
        OpenTheList(cut);
        Type(cut, A, "MaxUnloadWeight", "750");
        _host.GarbledRoute = "edit-lock";

        Confirm(cut);

        // Row 37 of the queue (the owner's answer of 9/10): one sentence, and what came back behind
        // the line's "?" - as in the properties panel, where this refusal comes from the same gate.
        cut.WaitForAssertion(() => Assert.Equal(
            "Not written - the MaxUnloadWeight of Shuttle 1 (900 -> 750): The edit permit could not be asked for because the host's answer could not be read: nothing was written.",
            Outcome(cut).Trim()));
        Assert.StartsWith("JsonException: ", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-device-list-outcome"));
        Assert.Empty(_host.ConfigWrites);
    }

    /// <summary>
    /// Final review, C17: the list says the shared configuration write's failures as the properties
    /// panel does - the why in words, what came back behind the line's "?" - where it used to put the
    /// failure's message, the client's Italian with the host's problem document, in the sentence.
    /// Here the read just before writing gets no answer: nothing is written.
    /// </summary>
    [Fact]
    public void ADeviceThatCannotBeReadJustBeforeWritingIsSaidInWordsWithTheFailureBehindTheQuestionMark()
    {
        var cut = RenderPage();
        OpenTheList(cut);
        Type(cut, A, "MaxUnloadWeight", "750");
        _host.TimeoutRoute = "config-get";

        Confirm(cut);

        cut.WaitForAssertion(() => Assert.Equal(
            "Not written - the MaxUnloadWeight of Shuttle 1 (900 -> 750): the device's current configuration could not be read from the host just before writing " +
            "because the host did not answer in time, and without it this page cannot keep what others changed on it. Nothing reached the plant.",
            Outcome(cut).Trim()));
        Assert.StartsWith("TaskCanceledException: ", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-device-list-outcome"));
        Assert.Empty(_host.ConfigWrites);
    }

    /// <summary>
    /// Final review, C17 and M9: the write itself fails. A 500 leaves it unknown whether it went
    /// through; the 503 of the engine not loaded is a refusal of one of its two requests - the write, or
    /// the reading back after it - and the line says what each would mean. The host's answer is behind
    /// the "?" in both.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "",
        "It is NOT known whether the MaxUnloadWeight of Shuttle 1 (900 -> 750) went through, because the host answered with an error - the request may have reached the host anyway. " +
        "The row shows what the plant holds once the list is read again; check it before trying again.",
        "the host answered 500 with an empty body")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "{\"title\":\"Il motore non è caricato\",\"status\":503,\"unavailable\":\"engineNotLoaded\"}",
        "It is NOT known whether the MaxUnloadWeight of Shuttle 1 (900 -> 750) went through: the host refused one of its two requests - the engine is not ready (it is not loaded on the host) - " +
        "and if it was the write, nothing was written; if it was the reading back after it, it was. The row shows what the plant holds once the list is read again; check it before trying again.",
        "the host answered 503: {\"title\":\"Il motore non è caricato\",\"status\":503,\"unavailable\":\"engineNotLoaded\"}")]
    public void AWriteThatFailsIsSaidInWordsWithTheHostsAnswerBehindTheQuestionMark(HttpStatusCode status, string body, string said, string answer)
    {
        var cut = RenderPage();
        OpenTheList(cut);
        Type(cut, A, "MaxUnloadWeight", "750");
        _host.FailRoute = "config";
        _host.FailureStatus = status;
        _host.FailureBody = body;

        Confirm(cut);

        cut.WaitForAssertion(() => Assert.Equal(said, Outcome(cut).Trim()));
        Assert.Equal(answer, DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-device-list-outcome"));
    }

    /// <summary>
    /// Final review, C11 and C17: the write went through and the host answers the reading back that it
    /// does not have the device - the command's own <c>InvalidOperationException</c>, behind the "?".
    /// </summary>
    [Fact]
    public void ADeviceTheHostNoLongerHasAfterTheWriteIsSaidAsWrittenNotReadBack()
    {
        var cut = RenderPage();
        OpenTheList(cut);
        Type(cut, A, "MaxUnloadWeight", "750");
        _host.HideSingleItemReads = true;

        Confirm(cut);

        cut.WaitForAssertion(() => Assert.Equal(
            "Written - the MaxUnloadWeight of Shuttle 1 (900 -> 750) - but the device could not be read back afterwards because the host does not have it any more: " +
            "reload the page before changing anything else.",
            Outcome(cut).Trim()));
        Assert.StartsWith("InvalidOperationException: ", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-device-list-outcome"));
        Assert.Equal(750, Assert.Single(_host.ConfigWrites).MaxUnloadWeight);
    }

    /// <summary>
    /// A read the list cannot make sense of (the host names one device twice) still ENDS: "Reading"
    /// goes, the list says it could not be read, and "Read again" is there to press.
    /// </summary>
    [Fact]
    public void AReadThatFailsInAnUnforeseenWayStillEndsAndSaysSo()
    {
        _host.ConfigListAnswer = [Config(A, "Shuttle 1", 900), Config(A, "Shuttle 1", 900)];
        var cut = RenderPage();

        Act(cut, () => cut.Find("#drawio-device-list-toggle").Click());

        cut.WaitForAssertion(() => Assert.Equal("The devices could not be read from the host because an unforeseen failure stopped it.",
            cut.Find("#drawio-device-list-read-error").TextContent));
        Assert.Equal($"ArgumentException: An item with the same key has already been added. Key: {A}",
            DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-device-list-read-error"));
        Assert.Empty(cut.FindAll("#drawio-device-list-reading"));
        Assert.False(cut.Find("#drawio-device-list-reread").HasAttribute("disabled"));
    }

    /// <summary>
    /// A read the host fails says why in the page's words, at the first opening and when the list is
    /// read again, and the host's answer is behind the line's "?" (row 37 of the queue, extended to the
    /// whole page on 10/10) - where it used to quote the client's "L'API del motore ha risposto 500".
    /// </summary>
    [Fact]
    public void AReadTheHostFailsIsOneSentenceWithTheAnswerBehindTheQuestionMark()
    {
        // After the page has loaded the plant, which reads the same route.
        var cut = RenderPage();
        _host.BrokenReads.Add("configs");

        Act(cut, () => cut.Find("#drawio-device-list-toggle").Click());

        cut.WaitForAssertion(() => Assert.Equal("The devices could not be read from the host because the host answered with an error.",
            cut.Find("#drawio-device-list-read-error").TextContent));
        Assert.Equal("the host answered 500 with an empty body", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-device-list-read-error"));

        _host.BrokenReads.Remove("configs");
        Act(cut, () => cut.Find("#drawio-device-list-reread").Click());
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#drawio-device-list-read-error")));
        _host.BrokenReads.Add("configs");
        Act(cut, () => cut.Find("#drawio-device-list-reread").Click());

        cut.WaitForAssertion(() => Assert.Equal(
            "The devices could not be read again from the host because the host answered with an error: the rows below are how they were at the last read - press \"Read again\" before changing them.",
            cut.Find("#drawio-device-list-read-error").TextContent));
        Assert.Equal("the host answered 500 with an empty body", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-device-list-read-error"));
    }

    // ---- Fix round 1: lines that stop being true go ---------------------------------------------------

    /// <summary>
    /// The line that answers a row chosen ("... is selected in the drawing") stays while that device
    /// is the selection - which is also how the selection the row caused comes back - and goes once
    /// the selection is something else.
    /// </summary>
    [Fact]
    public async Task TheGoToLineGoesWhenTheSelectionMovesOn()
    {
        var cut = RenderPage();
        await cut.InvokeAsync(() => cut.Instance.OnDrawioReady());
        OpenTheList(cut);

        Act(cut, () => cut.Find($"#drawio-device-list-table tr[data-item-id='{B}']").Click());
        cut.WaitForAssertion(() => Assert.Contains("Shuttle 2 is selected in the drawing", cut.Find("#drawio-device-list-goto").TextContent));
        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([B.ToString()]));
        cut.WaitForAssertion(() => Assert.Contains("Shuttle 2 is selected in the drawing", cut.Find("#drawio-device-list-goto").TextContent));

        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([A.ToString()]));

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#drawio-device-list-goto")));
    }

    /// <summary>
    /// A row chosen while the drawing does not answer: why in the page's words, and what the page's
    /// script threw behind the line's "?" (row 37 of the queue, extended to the whole page on 10/10).
    /// </summary>
    [Fact]
    public async Task AGoToTheDrawingDoesNotAnswerKeepsTheFailureBehindTheQuestionMark()
    {
        var cut = RenderPage(selectCell: null);
        _bridge.Setup<string?>("selectCell", _ => true).SetException(new JSException("selectCell non ha risposto (prova)"));
        await cut.InvokeAsync(() => cut.Instance.OnDrawioReady());
        OpenTheList(cut);

        Act(cut, () => cut.Find($"#drawio-device-list-table tr[data-item-id='{B}']").Click());

        cut.WaitForAssertion(() => Assert.Equal("Shuttle 2: the drawing did not answer, because the page's script failed, so nothing was selected.",
            cut.Find("#drawio-device-list-goto").TextContent));
        Assert.Equal("JSException: selectCell non ha risposto (prova)", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-device-list-goto"));
    }

    /// <summary>The line that says a typed value was thrown away goes when the operator reads the plant again.</summary>
    [Fact]
    public async Task TheThrownAwayLineGoesWhenTheListIsReadAgain()
    {
        var cut = RenderPage();
        OpenTheList(cut);
        await SelectAndSettleAsync(cut, A);
        Type(cut, A, "MaxUnloadWeight", "123");
        TypeIntoThePanel(cut, "Name", "Renamed in the panel");
        SaveThePanel(cut);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#drawio-device-list-discarded")));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#drawio-device-list-reading")));

        Act(cut, () => cut.Find("#drawio-device-list-reread").Click());

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#drawio-device-list-discarded")));
    }

    // ---- Fix round 1, then the floating windows (spec 2026-09-29): several open at a time ----------------

    private static bool IsOpen(IRenderedComponent<DrawioEditor> cut, string section) =>
        !cut.Find($"#{section}").HasAttribute("hidden");

    /// <summary>
    /// Opening a section's window leaves the windows open before as they are - a window floats over
    /// the drawing and takes no height from it, so the old "one section open at a time" rule is gone -
    /// and the device list, open under another window, still shows its rows. Pressing an open one's
    /// button shuts that one only.
    /// </summary>
    [Fact]
    public void OpeningASectionLeavesTheOnesOpenBeforeOpen()
    {
        var cut = RenderPage();
        Assert.False(IsOpen(cut, "drawio-operations"));
        Assert.False(IsOpen(cut, "drawio-device-list"));
        Assert.False(IsOpen(cut, "drawio-junction-list"));

        Act(cut, () => cut.Find("#drawio-operations-toggle").Click());
        Assert.True(IsOpen(cut, "drawio-operations"));

        OpenTheList(cut);
        Assert.True(IsOpen(cut, "drawio-device-list"));
        Assert.True(IsOpen(cut, "drawio-operations"));

        Act(cut, () => cut.Find("#drawio-junction-list-toggle").Click());
        Assert.True(IsOpen(cut, "drawio-junction-list"));
        Assert.True(IsOpen(cut, "drawio-device-list"));
        Assert.True(IsOpen(cut, "drawio-operations"));
        Assert.Equal(_host.Items.Count, Rows(cut).Count);

        Act(cut, () => cut.Find("#drawio-junction-list-toggle").Click());
        Assert.False(IsOpen(cut, "drawio-junction-list"));
        Assert.True(IsOpen(cut, "drawio-device-list"));
        Assert.True(IsOpen(cut, "drawio-operations"));
    }

    // ---- Owner's decisions of 27/9 ---------------------------------------------------------------

    /// <summary>D1: in Pause a cell that changes a device's DeviceConnectionId is refused by the page, with why; nothing is sent.</summary>
    [Fact]
    public void InPauseADeviceConnectionCellIsRefusedAndSaysWhy()
    {
        var cut = RenderPage();
        OpenTheList(cut);
        _host.NavStatus = NavigatorStatusTypes.Pause;

        Type(cut, A, "DeviceConnectionId", "7");
        Confirm(cut);

        cut.WaitForAssertion(() => Assert.StartsWith("Not written - the DeviceConnectionId of Shuttle 1 (", Outcome(cut)));
        Assert.Contains("the engine is paused, and a device's ConnectionType and DeviceConnectionId are changed only with the engine stopped", Outcome(cut));
        Assert.Empty(_host.ConfigWrites);
    }

    /// <summary>D3: another client changed ANOTHER field of the device after the list read it - kept by the list's write.</summary>
    [Fact]
    public void AFieldAnotherClientChangedMeanwhileSurvivesTheListsWrite()
    {
        var cut = RenderPage();
        OpenTheList(cut);
        _host.Configs[A].Name = "Renamed elsewhere";

        Type(cut, A, "MaxUnloadWeight", "750");
        Confirm(cut);

        cut.WaitForAssertion(() => Assert.Contains("Written:", Outcome(cut)));
        var written = Assert.Single(_host.ConfigWrites);
        Assert.Equal(("Renamed elsewhere", 750), (written.Name, written.MaxUnloadWeight));
    }

    /// <summary>D3: the SAME field changed on the host meanwhile - refused, naming the field and both values; the row is read again.</summary>
    [Fact]
    public void TheSameFieldChangedMeanwhileOnTheHostIsRefusedAndNamed()
    {
        var cut = RenderPage();
        OpenTheList(cut);
        _host.Configs[A].MaxUnloadWeight = 800;

        Type(cut, A, "MaxUnloadWeight", "750");
        Confirm(cut);

        cut.WaitForAssertion(() => Assert.Equal(
            "Not written - the MaxUnloadWeight of Shuttle 1 (900 -> 750): MaxUnloadWeight was changed on the host since this list read it - the host now holds 800, and you wrote 750. " +
            "Nothing was written, so as not to overwrite it; the row shows what the plant holds now - write again if you still want your value.",
            Outcome(cut)));
        Assert.Empty(_host.ConfigWrites);
        cut.WaitForAssertion(() => Assert.Equal("800", CellText(cut, A, "MaxUnloadWeight")));
    }

    /// <summary>D3: the device gone by the time of the list's write - Gone, and nothing is sent.</summary>
    [Fact]
    public void ADeviceGoneBeforeTheListsWriteIsSaidGone()
    {
        var cut = RenderPage();
        OpenTheList(cut);
        _host.Items.RemoveAll(item => item.ItemId == A);
        _host.Configs.Remove(A);

        Type(cut, A, "MaxUnloadWeight", "750");
        Confirm(cut);

        cut.WaitForAssertion(() => Assert.Contains("the host does not have this device any more", Outcome(cut)));
        Assert.Empty(_host.ConfigWrites);
    }
}
