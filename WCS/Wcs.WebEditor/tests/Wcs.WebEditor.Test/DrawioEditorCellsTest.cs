using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Drawio;
using Wcs.WebEditor.Pages;
using Wcs.WebEditor.Session;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

/// <summary>
/// The warehouse cells in the draw.io dock: the door map of a Storage device
/// (<c>DoorMappings.razor</c>) and the single cell of an Automation/Buffer/Loading one
/// (<c>LinkedCells.razor</c>), both used as they are and both fed by this page.
/// </summary>
/// <remarks>
/// <para>
/// The entry point is the same as the other bUnit tests of this page: the <c>[JSInvokable]</c>
/// selection method called straight on the rendered instance. What these tests measure is not
/// "the write went out" - it is that <b>nothing is lost without being said</b>: a refusal names the
/// gesture that is really holding the plant, a read that did not come back takes the panel away
/// instead of showing an empty one whose "Save" would wipe the host's rows, and a late answer
/// belongs to the click it was started by.
/// </para>
/// <para>
/// The browser test (<c>DrawioCellsTest</c>) is the one that proves the round trip against a real
/// host; these cover what costs too much to stage there.
/// </para>
/// </remarks>
public sealed class DrawioEditorCellsTest : PlantCanvasBunitContext
{
    private static readonly Guid StorageId = Guid.Parse("11111111-0000-4000-8000-000000000001");
    private static readonly Guid AutomationId = Guid.Parse("11111111-0000-4000-8000-000000000002");
    private static readonly Guid GroupId = Guid.Parse("11111111-0000-4000-8000-000000000003");

    /// <summary>A second Storage device, with no door mapped: the race between two devices of the SAME kind needs one, and it is the only way the mappings can be told apart on screen.</summary>
    private static readonly Guid StorageTwoId = Guid.Parse("11111111-0000-4000-8000-000000000004");

    private const string StorageName = "1S01.0001.0001";
    private const string StorageTwoName = "1S01.0001.0002";
    private const string AutomationName = "IN01.0001.0001";
    private const string GroupName = "1G01.0001.0001";

    /// <summary>The cell the seeded door map already points at: what proves the panel shows the HOST's rows and not an empty working copy.</summary>
    private const decimal MappedCellId = 101m;

    private const decimal OtherCellId = 202m;
    private const string MappedDoorA = "DOOR-101-A";
    private const string OtherDoorA = "DOOR-202-A";

    /// <summary>A cell the warehouse only ever offers to an <c>automaticOnly</c> question: what makes an answer meant for an Automation device recognisable inside a Storage device's tables.</summary>
    private const decimal AutomaticOnlyCellId = 303m;

    private const string AutomaticOnlyDoorA = "DOOR-303-A";

    /// <summary>
    /// A value <c>DoorMappings</c> cannot produce: it builds every row with
    /// <c>OperationActive = 0</c>. A panel whose door map came back carrying this after a write is a
    /// panel that reread the host instead of keeping what it sent.
    /// </summary>
    private const short HostHeldOperationActive = 7;

    private static PlantItemDto Item(Guid id, string name, DeviceTypes type, double x) =>
        new(id, name, type, null, null, null, default, default, 0, null, 0, 0, 0, 0, default!, x, 200, 130, 184, 0);

    private static PlantItemConfigDto Config(Guid id, string name, DeviceTypes type, decimal linkedCellId = 0m) => new()
    {
        ItemId = id,
        Name = name,
        Asi = name,
        DeviceType = (short)type,
        ConnectionType = (short)ConnectionTypes.Test,
        LinkedCellId = linkedCellId,
    };

    private static DeviceTypeDto Type(string key, DeviceTypes type, string title) => new(
        key, (short)type, title,
        [new ShapePartDto("M 4 4 H 96 V 96 H 4 Z", "#334155", Filled: false, Rigid: false)], [],
        new ShapePointDto(50, 50), new ShapePointDto(88, 60));

    private static WmsCell Cell(decimal cellId, string? doorA, string? doorB) =>
        new(cellId, "WH1", "AREA1", 3, 2, doorA, doorB, $"cell {cellId}");

    /// <summary>
    /// A plant of three devices - one of each kind the cells section can meet - with two candidate
    /// warehouse cells and one door already mapped on the Storage device.
    /// </summary>
    private static DrawioWriteHandler HostWithCells(decimal automationLinkedCellId = 0m)
    {
        var handler = new DrawioWriteHandler
        {
            Items =
            [
                Item(StorageId, StorageName, DeviceTypes.Storage, 100),
                Item(AutomationId, AutomationName, DeviceTypes.Automation, 400),
                Item(GroupId, GroupName, DeviceTypes.GroupOfAutomationDevices, 700),
                Item(StorageTwoId, StorageTwoName, DeviceTypes.Storage, 1000),
            ],
            Types =
            [
                Type("storage", DeviceTypes.Storage, "Storage"),
                Type("automation", DeviceTypes.Automation, "Automation"),
                Type("group-automation", DeviceTypes.GroupOfAutomationDevices, "Group Automation"),
            ],
        };
        handler.Configs[StorageId] = Config(StorageId, StorageName, DeviceTypes.Storage);
        handler.Configs[AutomationId] = Config(AutomationId, AutomationName, DeviceTypes.Automation, automationLinkedCellId);
        handler.Configs[GroupId] = Config(GroupId, GroupName, DeviceTypes.GroupOfAutomationDevices);
        handler.Configs[StorageTwoId] = Config(StorageTwoId, StorageTwoName, DeviceTypes.Storage);
        handler.WmsCells.AddRange([Cell(MappedCellId, MappedDoorA, "DOOR-101-B"), Cell(OtherCellId, OtherDoorA, null)]);
        // Already on the host, and with a value the panel could not have invented: see
        // HostHeldOperationActive.
        handler.DoorMappings.Add(new DoorMappingDto(1, StorageId, MappedDoorA, MappedCellId, "A", HostHeldOperationActive, false));
        return handler;
    }

    private IRenderedComponent<DrawioEditor> RenderWith(DrawioWriteHandler handler)
    {
        var state = new EditorSessionState(new Uri("http://localhost:5580"), handler);
        state.UseKey("test-key");
        Services.AddSingleton(state);
        return Render<DrawioEditor>();
    }

    private static Task SelectAsync(IRenderedComponent<DrawioEditor> cut, Guid itemId) =>
        cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([itemId.ToString()]));

    private static string TextOf(IRenderedComponent<DrawioEditor> cut, string selector) =>
        cut.Find(selector).TextContent;

    /// <summary>The identifiers of the cells the door table of one side is offering, in order.</summary>
    private static string[] DoorRows(IRenderedComponent<DrawioEditor> cut, string side) =>
        [.. cut.FindAll($"fieldset.wms-cells-column[data-side='{side}'] tbody tr").Select(row => row.GetAttribute("data-cell-id")!)];

    private static bool DoorIsTicked(IRenderedComponent<DrawioEditor> cut, string side, decimal cellId) =>
        cut.Find($"#door-{side.ToLowerInvariant()}-{cellId}").HasAttribute("checked");

    // ---- What the dock shows ---------------------------------------------------------------------

    /// <summary>
    /// A Storage device gets the door map, filled from the host: both candidate cells offered on
    /// door A, and the one the plant really has mapped already ticked.
    /// </summary>
    /// <remarks>
    /// The tick is the half that could not be faked by a panel that simply rendered the warehouse's
    /// list: it comes from <c>GET /plant/door-mappings</c>, filtered down to this device by the page
    /// because the host's route has no device in it.
    /// </remarks>
    [Fact]
    public async Task AStorageDeviceGetsTheDoorMapWithWhatThePlantAlreadyHasTicked()
    {
        var cut = RenderWith(HostWithCells());

        await SelectAsync(cut, StorageId);

        Assert.Equal([MappedCellId.ToString(), OtherCellId.ToString()], DoorRows(cut, "A"));
        Assert.True(DoorIsTicked(cut, "A", MappedCellId), "The door the plant has mapped is not ticked: the panel is not showing the host's door map.");
        Assert.False(DoorIsTicked(cut, "A", OtherCellId), "A door nobody mapped is ticked.");
        // Only cell 101 has a door B, so the other table offers that one alone: the component's own
        // filter, exercised through this page rather than assumed.
        Assert.Equal([MappedCellId.ToString()], DoorRows(cut, "B"));
        // The line the operator reads before opening anything: the fact, not "there is something here".
        Assert.Contains("1 door mapped", TextOf(cut, "#drawio-cells-summary"));
    }

    /// <summary>
    /// An Automation device gets the single-cell panel instead, and it says which cell the plant has
    /// connected - the value coming from the configuration, where the engine really reads it.
    /// </summary>
    [Fact]
    public async Task AnAutomationDeviceGetsTheSingleCellPanelNamingTheCellThePlantHas()
    {
        var cut = RenderWith(HostWithCells(automationLinkedCellId: OtherCellId));

        await SelectAsync(cut, AutomationId);

        Assert.Empty(cut.FindAll("fieldset.wms-cells-column"));
        Assert.Contains($"Linked cell: {OtherCellId}", TextOf(cut, "#drawio-cells-details"));
        Assert.Contains($"cell {OtherCellId} connected", TextOf(cut, "#drawio-cells-summary"));
        Assert.NotNull(cut.Find("#linked-cells-link-button"));
    }

    /// <summary>
    /// A device of a kind that has no cells at all is <b>told so</b>. The WinForms Editor's own
    /// switch lets these types fall through without doing anything, and a dock that simply showed
    /// nothing would read as "the cells did not load" - which is a different fact.
    /// </summary>
    [Fact]
    public async Task ADeviceWithNoCellsSaysSoInsteadOfShowingAnEmptyPanel()
    {
        var handler = HostWithCells();
        var cut = RenderWith(handler);

        await SelectAsync(cut, GroupId);

        Assert.Contains("no warehouse cells to connect", TextOf(cut, "#drawio-cells-none"));
        Assert.Empty(cut.FindAll("#drawio-cells-details"));
        Assert.Empty(cut.FindAll("#drawio-cells-error"));
        // And the sixty properties are still there: the cells section replaced none of them.
        Assert.NotEmpty(cut.FindAll("#drawio-property-panel fieldset.property-panel-group"));
        // Nothing was asked of the warehouse for a device that has none: the reads are not started
        // at all, rather than started and thrown away. Counted on the fake host, not deduced.
        Assert.Equal(0, handler.DoorMappingReads);
        Assert.Equal(0, handler.WmsCellReads);
        Assert.Equal(0, handler.AutomaticCellReads);
    }

    /// <summary>
    /// The automatic cells are read for a device that has somewhere to show them, and <b>not</b> for
    /// a Storage device, whose panel has no parameter for them - one read per click that would go
    /// out and be dropped on the floor. It is the one place this page deliberately departs from
    /// <c>PlantCanvas</c>, which reads all three whatever the type.
    /// </summary>
    [Fact]
    public async Task TheAutomaticCellsAreReadOnlyForTheDeviceThatCanShowThem()
    {
        var handler = HostWithCells();
        var cut = RenderWith(handler);

        await SelectAsync(cut, StorageId);
        Assert.Equal(1, handler.DoorMappingReads);
        Assert.Equal(1, handler.WmsCellReads);
        Assert.Equal(0, handler.AutomaticCellReads);

        await SelectAsync(cut, AutomationId);
        Assert.Equal(2, handler.DoorMappingReads);
        Assert.Equal(2, handler.WmsCellReads);
        Assert.Equal(1, handler.AutomaticCellReads);
    }

    /// <summary>
    /// The sixty properties stay reachable with the cells above them, and the cells are NOT inside
    /// the component that renders them: the two live side by side in the same column.
    /// </summary>
    /// <remarks>
    /// Anchored on both halves on purpose. A cells section that had eaten the properties would pass
    /// an assertion that only looked at the cells, and a properties panel that had pushed the cells
    /// out of the markup would pass one that only looked at the fields.
    /// </remarks>
    [Fact]
    public async Task TheCellsAndTheSixtyPropertiesAreBothInTheDock()
    {
        var cut = RenderWith(HostWithCells());

        await SelectAsync(cut, StorageId);

        Assert.NotNull(cut.Find("#drawio-cells-details"));
        var groups = cut.FindAll("#drawio-property-panel fieldset.property-panel-group");
        Assert.True(groups.Count > 2, $"The properties panel is showing {groups.Count} categories: the cells took its place.");
        Assert.NotNull(cut.Find("#property-panel-save-button"));
    }

    // ---- The late answer -------------------------------------------------------------------------

    /// <summary>
    /// <b>The race, measured, one click further than batch B's.</b> The cells are two or three more
    /// HTTP calls after the configuration, so the window in which a click can overtake them is
    /// WIDER, not narrower: the answer for the first device is held on a
    /// <see cref="TaskCompletionSource"/> and let go only after the second device is already on
    /// screen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order is imposed, not hoped for: the first device's <c>GET /wms/cells</c> is held on a
    /// gate and let go only after the second device is already on screen. Without the generation
    /// check in <c>RefreshSelectedCellsAsync</c> this test goes red on the door tables - measured by
    /// taking the check out: the Automation device's automatic-only list walked into the Storage
    /// device's two tables, which then offered a cell the Storage device cannot be mapped to.
    /// </para>
    /// <para>
    /// <b>The two devices must ask the warehouse DIFFERENT questions, or this test cannot fail.</b>
    /// The first version of it had the fake answer the same list to everybody, and it stayed green
    /// with the guard removed: the late answer replaced the list with an identical one and there was
    /// nothing on screen to tell. Hence <c>WmsCellsFor</c>, which answers the filters the page really
    /// sends - the area type and <c>automaticOnly</c>, which a Storage device and an Automation
    /// device do not send the same way.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ALateCellsAnswerDoesNotShowTheWrongDevicesCells()
    {
        var handler = HostWithCells();
        // The warehouse answers the two devices differently, as a warehouse does: the automatic-only
        // question gets a cell no Storage device would ever be offered.
        handler.WmsCellsFor = (_, automaticOnly) => automaticOnly
            ? [Cell(AutomaticOnlyCellId, AutomaticOnlyDoorA, null)]
            : [Cell(MappedCellId, MappedDoorA, "DOOR-101-B"), Cell(OtherCellId, OtherDoorA, null)];

        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.WmsCellsGate = held;
        var cut = RenderWith(handler);

        // First click on the Automation device: its configuration arrives, and then its cells read
        // is held. Not awaited - that is the point.
        var readingTheAutomation = SelectAsync(cut, AutomationId);
        await handler.WmsCellsGateReached.Task;
        Assert.False(readingTheAutomation.IsCompleted, "The Automation device's cells read did not stay suspended: the race was never staged.");
        cut.WaitForAssertion(() =>
        {
            Assert.Contains(AutomationName, TextOf(cut, "#drawio-property-subject"));
            Assert.NotNull(cut.Find("#drawio-cells-loading"));
        });

        // Second click, answered at once: from here on the dock is about the Storage device. The
        // gate is taken away first, or this read would be held at the same door.
        handler.WmsCellsGate = null;
        await SelectAsync(cut, StorageId);

        // And now the Automation device's cells arrive, out of order.
        held.SetResult();
        await readingTheAutomation;

        // The heading follows the selection, the KIND of panel follows the device type, and the rows
        // follow the answer that belongs to it. Three different failures, and the third is the one
        // the late answer produces.
        Assert.Contains(StorageName, TextOf(cut, "#drawio-property-subject"));
        Assert.Empty(cut.FindAll("#linked-cells-link-button"));
        Assert.Equal([MappedCellId.ToString(), OtherCellId.ToString()], DoorRows(cut, "A"));
        Assert.DoesNotContain(AutomaticOnlyCellId.ToString(), DoorRows(cut, "A"));
    }

    /// <summary>
    /// The same race between two devices of the SAME kind, where what is at stake is not the panel
    /// but its contents: the door map. Two Storage devices ask the warehouse the same question, so
    /// only the mappings can tell them apart - and a late answer would tick a door the device on
    /// screen has not got.
    /// </summary>
    /// <remarks>
    /// This is the half the test above cannot reach: there the panel changes kind, here it does not,
    /// and the only visible difference is which rows are ticked. Without the generation check it
    /// goes red on the tick - measured.
    /// </remarks>
    [Fact]
    public async Task ALateDoorMapDoesNotTickADoorTheDeviceOnScreenHasNot()
    {
        var handler = HostWithCells();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.WmsCellsGate = held;
        var cut = RenderWith(handler);

        // The first Storage device has a door mapped; the second has none.
        var readingTheFirst = SelectAsync(cut, StorageId);
        await handler.WmsCellsGateReached.Task;
        Assert.False(readingTheFirst.IsCompleted, "The first device's cells read did not stay suspended: the race was never staged.");

        handler.WmsCellsGate = null;
        await SelectAsync(cut, StorageTwoId);
        Assert.Contains("none mapped yet", TextOf(cut, "#drawio-cells-summary"));

        held.SetResult();
        await readingTheFirst;

        Assert.Contains(StorageTwoName, TextOf(cut, "#drawio-property-subject"));
        Assert.False(DoorIsTicked(cut, "A", MappedCellId),
            "A door of the device selected BEFORE is ticked on the device selected now: the late answer walked into the panel.");
        Assert.Contains("none mapped yet", TextOf(cut, "#drawio-cells-summary"));
        // And the component really is about the device on screen, so its "Save" would write there.
        Assert.Equal(StorageTwoId, cut.FindComponent<Wcs.WebEditor.Wms.DoorMappings>().Instance.ItemId);
    }

    /// <summary>
    /// <b>The other half of the same mechanism, and the silent loss it used to cost.</b> The
    /// selection is reported AGAIN for the device already on screen while its cells are still on
    /// their way: nothing is read and nothing is taken away, so the read in flight is still the
    /// right one and must be allowed to land.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Until this was measured, <c>RefreshSelectedConfigAsync</c> raised the generation on that path
    /// too - the path that starts no read at all - and the answer already travelling came back to
    /// find a number it could no longer claim, so it threw itself away. What was left was the
    /// <c>default:</c> branch of the markup, that is the line "Reading the warehouse cells from the
    /// host...", <b>with nobody reading and nobody ever going to</b>: the sixty properties stayed
    /// perfectly in place, so there was nothing on screen to say anything had gone wrong.
    /// </para>
    /// <para>
    /// draw.io really does report the same selection twice: <c>selectCellsForEvent</c> has no
    /// "already selected" guard (<c>selectCellForEvent</c> does), so a rubber-band selection or
    /// Ctrl+A landing on the device already selected sends the event again - and
    /// <c>DrainCreationsAsync</c> calls this method again for the same device too.
    /// </para>
    /// <para>
    /// The counter is asserted as well, and it is what makes the two halves impossible to confuse:
    /// the panel could also fill up because a SECOND read was started, which is not what this is
    /// about.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task SelectingTheSameDeviceAgainDoesNotStrandTheCellsReadAlreadyOnItsWay()
    {
        var handler = HostWithCells();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.WmsCellsGate = held;
        var cut = RenderWith(handler);

        // The configuration has arrived and is on screen; the cells are still out there.
        var reading = SelectAsync(cut, StorageId);
        await handler.WmsCellsGateReached.Task;
        Assert.False(reading.IsCompleted, "The cells read did not stay suspended: the race was never staged.");
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#drawio-cells-loading")));

        // The same device is reported selected a second time. The gate is taken away first so that
        // a second read - if one were started, which is the other half of this test - would answer
        // rather than hang.
        handler.WmsCellsGate = null;
        await SelectAsync(cut, StorageId);

        held.SetResult();
        await reading;

        Assert.Equal(1, handler.WmsCellReads);
        Assert.Empty(cut.FindAll("#drawio-cells-loading"));
        Assert.NotNull(cut.Find("#drawio-cells-details"));
        Assert.Contains("1 door mapped", TextOf(cut, "#drawio-cells-summary"));
        Assert.True(DoorIsTicked(cut, "A", MappedCellId), "The door map the host holds never reached the panel.");
    }

    /// <summary>
    /// <b>The same race one gesture later, on the reread that follows a write.</b> The door map read
    /// back after a write is held while the selection moves to another device, and it must not walk
    /// into the panel that is now on screen.
    /// </summary>
    /// <remarks>
    /// The guard this measures is <c>if (_configItemId != itemId) return;</c> in
    /// <c>RereadDoorMappingsAsync</c>. It was there and it was right, and <b>no test could see it</b>:
    /// the review replaced it with <c>_ = itemId;</c> and all 933 stayed green. The two devices are
    /// both Storage on purpose - the panel does not change shape between them, so the only thing
    /// that can tell the answers apart is which doors are ticked, which is exactly what a late
    /// reread would get wrong.
    /// </remarks>
    [Fact]
    public async Task ALateDoorMapRereadDoesNotWalkIntoTheDeviceNowOnScreen()
    {
        var handler = HostWithCells();
        var cut = RenderWith(handler);
        await SelectAsync(cut, StorageId);

        // Two doors on the first device, so the wrong answer would be unmistakable on the second,
        // which has none.
        await cut.Find($"#door-a-{OtherCellId}").ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = true });

        // Armed now and not earlier: the panel's own first read must get through, and this gate
        // holds the first read it meets.
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.DoorMappingReadGate = held;

        var writing = cut.Find("#door-mappings-save-button").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        await handler.DoorMappingReadGateReached.Task;
        Assert.False(writing.IsCompleted, "The write did not stay inside its reread: the race was never staged.");
        Assert.Single(handler.DoorMappingWrites);

        // The operator has moved on to the other Storage device, which has no door mapped.
        await SelectAsync(cut, StorageTwoId);
        Assert.Contains("none mapped yet", TextOf(cut, "#drawio-cells-summary"));

        held.SetResult();
        await writing;

        Assert.Contains("none mapped yet", TextOf(cut, "#drawio-cells-summary"));
        Assert.False(DoorIsTicked(cut, "A", MappedCellId),
            "A door of the device written BEFORE is ticked on the device now on screen: the late reread walked into the panel.");
        Assert.False(DoorIsTicked(cut, "A", OtherCellId),
            "A door of the device written BEFORE is ticked on the device now on screen: the late reread walked into the panel.");
        // And the component would write THERE: what is on screen is not a picture, it is the "how it
        // was" of the next write.
        Assert.Empty(cut.FindComponent<Wcs.WebEditor.Wms.DoorMappings>().Instance.CurrentMappings);
    }

    // ---- The reads that fail, one at a time ------------------------------------------------------

    /// <summary>
    /// The door map fails while the warehouse cells arrive. The panel does <b>not</b> appear with an
    /// empty door map: that would offer a "Save" which, pressed, would replace the rows the host
    /// really holds with nothing - a silent loss dressed as a confirmation. What failed is named,
    /// and what the host answered to it is behind the "?" next to the sentence.
    /// </summary>
    [Fact]
    public async Task WhenTheDoorMapCannotBeReadThePanelIsNotShownWithAnEmptyOne()
    {
        var handler = HostWithCells();
        handler.BrokenReads.Add("door-mappings");
        var cut = RenderWith(handler);

        await SelectAsync(cut, StorageId);

        var said = TextOf(cut, "#drawio-cells-error");
        Assert.Contains("the door map of this device", said);
        // And it does NOT accuse the read that came back fine: the two are named separately for
        // exactly this reason.
        Assert.DoesNotContain("the warehouse's cell list", said);
        // Row 29 of the queue, final round: the door map's part of the sentence is plain words too -
        // nothing of the host's answer in it, its why in the page's words, one sentence. Until then
        // only the cell list's part was ever held to that.
        Assert.Equal(
            "The cells of this device are not shown: the door map of this device could not be read because the host answered with an error; nothing here has been changed.",
            said);
        // Row 29 of the queue took the host's answer out of the sentence: it must be behind the "?",
        // and only the door map's (BrokenReads answers it with an empty 500). Until the second review
        // of row 29 only the cell list's answer was ever looked for there: the door map's line could
        // go, and the host's answer with it, with every test green.
        var detail = cut.Find("#drawio-cells-error-detail");
        Assert.Equal(["The door map of this device: the host answered 500 with an empty body"], detail.TextContent.Split('\n'));
        Assert.NotNull(detail.Closest("#drawio-cells-error-help-body"));
        Assert.Empty(cut.FindAll("#drawio-cells-details"));
        Assert.Empty(cut.FindAll("#door-mappings-save-button"));
        // And the properties are untouched by a failure that is not theirs.
        Assert.NotEmpty(cut.FindAll("#drawio-property-panel fieldset.property-panel-group"));
        Assert.Empty(cut.FindAll("#drawio-property-error"));
    }

    /// <summary>
    /// A 503 on the cell list's route that does not say which 503 it is is a failure of the host's
    /// like any other, said rather than shown as "this device has no cells", with the host's own
    /// answer behind the "?" since row 29 of the queue.
    /// </summary>
    /// <remarks>
    /// Row 29 of the queue, final round: <c>BrokenReads</c> answers this 503 with an EMPTY body, which
    /// the real host never does (<c>EngineExceptionHandler</c> always writes its problem document), and
    /// the "?" says the body was empty. Until row 34 the page took any 503 on this route for the
    /// warehouse; since then the host says which of its two 503s it is in a field
    /// (<c>ServiceUnavailableProblems.ReasonExtension</c>), and the page reads that and nothing else -
    /// an empty body has no field, so the warehouse is not blamed (the warehouse's own 503 is
    /// <see cref="WhenTheWarehouseDoesNotAnswerTheSentenceIsPlainAndTheHostsAnswerIsBehindTheQuestionMark"/>).
    /// </remarks>
    [Fact]
    public async Task AServiceUnavailableThatDoesNotSayWhichIsNotBlamedOnTheWarehouse()
    {
        var handler = HostWithCells();
        handler.BrokenReads.Add("wms-cells");
        var cut = RenderWith(handler);

        await SelectAsync(cut, StorageId);

        var said = TextOf(cut, "#drawio-cells-error");
        Assert.Contains("the warehouse's cell list", said);
        Assert.DoesNotContain("the door map of this device", said);
        Assert.Equal(
            "The cells of this device are not shown: the warehouse's cell list could not be read because the host answered with an error; nothing here has been changed.",
            said);
        var detail = cut.Find("#drawio-cells-error-detail");
        Assert.Equal(["The warehouse's cell list: the host answered 503 with an empty body"], detail.TextContent.Split('\n'));
        Assert.NotNull(detail.Closest("#drawio-cells-error-help-body"));
        Assert.True(cut.Find("#drawio-cells-error-help-body").HasAttribute("hidden"), "The host's answer is open before its \"?\" is pressed.");
        Assert.Empty(cut.FindAll("#drawio-cells-details"));
    }

    /// <summary>
    /// Row 29 of the queue: the warehouse that does not answer, with the answer the real host gives -
    /// the problem document <c>EngineExceptionHandler</c> writes for a
    /// <c>WarehouseUnavailableException</c>, copied from the screenshot of the passata grafica - is
    /// said in ONE plain sentence: which read failed, why in words, and that nothing was changed. The
    /// host's answer as it came, JSON and trace id included, is behind the "?" next to the sentence,
    /// shut, instead of in the middle of it, where the screenshot had it.
    /// </summary>
    [Fact]
    public async Task WhenTheWarehouseDoesNotAnswerTheSentenceIsPlainAndTheHostsAnswerIsBehindTheQuestionMark()
    {
        const string traceId = "00-e911bddb96fbe320fcd5ff0ac98e63b0-0b2cb1b20a6cd7d1-00";
        const string answer =
            "{\"type\":\"https://tools.ietf.org/html/rfc9110#section-15.6.4\",\"title\":\"Il gestionale non \u00e8 raggiungibile dal motore\"," +
            "\"status\":503,\"detail\":\"Il gestionale non ha risposto a \u00abGetCellsForEditor\u00bb.\",\"unavailable\":\"warehouseUnreachable\",\"traceId\":\"" + traceId + "\"}";
        var handler = HostWithCells();
        handler.FailRoute = "wms-cells";
        handler.FailureStatus = System.Net.HttpStatusCode.ServiceUnavailable;
        handler.FailureBody = answer;
        var cut = RenderWith(handler);

        await SelectAsync(cut, StorageId);

        var said = TextOf(cut, "#drawio-cells-error");
        // One sentence, word for word (final round of row 29: it was two, "... from the engine." and
        // "Nothing here has been changed.", and a StartsWith and an EndsWith let anything in between).
        Assert.Equal(
            "The cells of this device are not shown: the warehouse's cell list could not be read because the warehouse is not reachable from the engine; nothing here has been changed.",
            said);
        // Nothing of the host's answer is in the sentence: not the JSON, not its words, not the status.
        Assert.DoesNotContain("{", said);
        Assert.DoesNotContain(traceId, said);
        Assert.DoesNotContain("Il gestionale", said);
        Assert.DoesNotContain("503", said);

        // It is behind the "?", whole, and shut until the "?" is pressed.
        var detail = cut.Find("#drawio-cells-error-detail");
        Assert.Contains(answer, detail.TextContent);
        Assert.NotNull(detail.Closest("#drawio-cells-error-help-body"));
        Assert.Null(cut.Find("#drawio-cells-error").Closest("#drawio-cells-error-help-body"));
        Assert.True(cut.Find("#drawio-cells-error-help-body").HasAttribute("hidden"), "The host's answer is open before its \"?\" is pressed.");
        await cut.Find("#drawio-cells-error-help").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        Assert.False(cut.Find("#drawio-cells-error-help-body").HasAttribute("hidden"), "The \"?\" did not open the host's answer.");
        Assert.Empty(cut.FindAll("#drawio-cells-details"));
    }

    /// <summary>
    /// Row 34 of the queue, the case it names: the engine stops being loaded between the configuration
    /// read and the cells read (a graph replaced by another session that fails to reload), and the cell
    /// list's route answers the 503 of the engine not loaded - the host's own problem document, with the
    /// field that says which 503 it is. The sentence says the engine is not ready, not "the warehouse is
    /// not reachable", which sent the operator to the wrong system; the host's answer is behind the "?".
    /// </summary>
    [Fact]
    public async Task AnEngineNotLoadedBetweenTheTwoReadsIsNotBlamedOnTheWarehouse()
    {
        const string answer =
            "{\"type\":\"https://tools.ietf.org/html/rfc9110#section-15.6.4\",\"title\":\"Il motore non \u00e8 caricato\"," +
            "\"status\":503,\"detail\":\"Il grafo non si carica\",\"unavailable\":\"engineNotLoaded\"," +
            "\"traceId\":\"00-5c1e7f0d7a2b4e9a8f3c6d2b1a0e9f8c-1a2b3c4d5e6f7a8b-00\"}";
        var handler = HostWithCells();
        handler.FailRoute = "wms-cells";
        handler.FailureStatus = System.Net.HttpStatusCode.ServiceUnavailable;
        handler.FailureBody = answer;
        var cut = RenderWith(handler);

        await SelectAsync(cut, StorageId);

        Assert.Equal(
            "The cells of this device are not shown: the warehouse's cell list could not be read because the engine is not ready (it is not loaded on the host); nothing here has been changed.",
            TextOf(cut, "#drawio-cells-error"));
        var detail = cut.Find("#drawio-cells-error-detail");
        Assert.Equal(["The warehouse's cell list: the host answered 503: " + answer], detail.TextContent.Split('\n'));
        Assert.Empty(cut.FindAll("#drawio-cells-details"));
    }

    /// <summary>
    /// Final review, C3 and C10: row 34's case as it really happens. The engine stops being loaded,
    /// and the filter of the "engine" group answers its 503 on EVERY route behind it - the door map's
    /// read as well as the cell list's. Both reads are said, each with its own why, read from the field:
    /// the door map's said "the host answered with an error" before row 34, and nothing checked it since.
    /// </summary>
    [Fact]
    public async Task AnEngineNotLoadedOnBothReadsIsSaidForEachOfThem()
    {
        const string answer =
            "{\"type\":\"https://tools.ietf.org/html/rfc9110#section-15.6.4\",\"title\":\"Il motore non è caricato\"," +
            "\"status\":503,\"detail\":\"Il grafo non si carica\",\"unavailable\":\"engineNotLoaded\"}";
        var handler = HostWithCells();
        var cut = RenderWith(handler);
        handler.EngineNotLoadedAnswer = answer;

        await SelectAsync(cut, StorageId);

        Assert.Equal(
            "The cells of this device are not shown: the door map of this device could not be read because the engine is not ready (it is not loaded on the host); " +
            "the warehouse's cell list could not be read because the engine is not ready (it is not loaded on the host); nothing here has been changed.",
            TextOf(cut, "#drawio-cells-error"));
        Assert.Equal(
            ["The door map of this device: the host answered 503: " + answer, "The warehouse's cell list: the host answered 503: " + answer],
            cut.Find("#drawio-cells-error-detail").TextContent.Split('\n'));
        Assert.Empty(cut.FindAll("#drawio-cells-details"));
    }

    /// <summary>
    /// Row 29 of the queue, the other side of the why: only a 503 that says so in its field
    /// (<c>unavailable=warehouseUnreachable</c>, row 34) is the warehouse the engine cannot reach - an
    /// empty 503 is the host answering with an error, see above. Any other failure of the same read - a 500 of the host
    /// itself, no answer within the client's timeout, the transport cut before an answer (the WCS
    /// host down, not the warehouse) - gets its own why, and what came back, or what the failure
    /// said, is behind the "?". "The warehouse is not reachable" on any of these would send the
    /// operator to the wrong system. Until the first review of row 29 nothing failed this read with
    /// anything but a 503: a why said for every failure, or for every refusal of the host whatever
    /// its status, passed all the tests. Until the final round these were named with no why at all,
    /// and a timeout, which had the client's own words on screen before row 29, had none.
    /// </summary>
    [Theory]
    [InlineData("500", "the host answered with an error")]
    [InlineData("timeout", "the host did not answer in time")]
    [InlineData("transport", "the connection to the host failed")]
    public async Task ACellListFailureThatIsNotTheWarehousesDoesNotBlameTheWarehouse(string failure, string why)
    {
        const string answer =
            "{\"type\":\"https://tools.ietf.org/html/rfc9110#section-15.6.1\",\"title\":\"An error occurred while processing your request.\"," +
            "\"status\":500,\"traceId\":\"00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-00\"}";
        var handler = HostWithCells();
        // What the detail must hold: the host's answer as it came, or the words the handler's failure
        // carries (DrawioWriteHandler.Failed), which no answer replaces.
        string whatCameBack;
        switch (failure)
        {
            case "timeout":
                handler.TimeoutRoute = "wms-cells";
                whatCameBack = "scaduta senza risposta";
                break;
            case "transport":
                handler.HttpFailureRoute = "wms-cells";
                whatCameBack = "Il trasporto su 'wms-cells' e' fallito";
                break;
            default:
                handler.FailRoute = "wms-cells";
                handler.FailureStatus = System.Net.HttpStatusCode.InternalServerError;
                handler.FailureBody = answer;
                whatCameBack = answer;
                break;
        }
        var cut = RenderWith(handler);

        await SelectAsync(cut, StorageId);

        Assert.Equal(
            $"The cells of this device are not shown: the warehouse's cell list could not be read because {why}; nothing here has been changed.",
            TextOf(cut, "#drawio-cells-error"));
        var detail = cut.Find("#drawio-cells-error-detail");
        Assert.Contains(whatCameBack, detail.TextContent);
        Assert.NotNull(detail.Closest("#drawio-cells-error-help-body"));
        Assert.Empty(cut.FindAll("#drawio-cells-details"));
    }

    /// <summary>
    /// Both of them fail: the message names both, rather than the first one and silence about the
    /// second - which is what a single <c>Task.WhenAll</c> inside one try would have produced. And
    /// the "?" holds both answers, not the last one.
    /// </summary>
    [Fact]
    public async Task WhenBothReadsFailBothAreNamed()
    {
        // The warehouse's own 503 as the host writes it, with the field that says so (row 34 of the
        // queue): an empty 503 is no longer the warehouse, and with it both whys would be the same.
        const string warehouseAnswer =
            "{\"title\":\"Il gestionale non \u00e8 raggiungibile dal motore\",\"status\":503,\"unavailable\":\"warehouseUnreachable\"}";
        var handler = HostWithCells();
        handler.BrokenReads.Add("door-mappings");
        handler.FailRoute = "wms-cells";
        handler.FailureStatus = System.Net.HttpStatusCode.ServiceUnavailable;
        handler.FailureBody = warehouseAnswer;
        var cut = RenderWith(handler);

        await SelectAsync(cut, StorageId);

        var said = TextOf(cut, "#drawio-cells-error");
        Assert.Contains("the door map of this device", said);
        Assert.Contains("the warehouse's cell list", said);
        // Row 29 of the queue, final round: each read with its own why, apart from the other's. The
        // door map is the WCS's own store: "... could not be read and the warehouse's cell list could
        // not be read, because the warehouse is not reachable from the engine", as it was, blamed the
        // warehouse for it too. And one sentence, word for word.
        Assert.Equal(
            "The cells of this device are not shown: the door map of this device could not be read because the host answered with an error; " +
            "the warehouse's cell list could not be read because the warehouse is not reachable from the engine; nothing here has been changed.",
            said);
        // Row 29 of the queue: one line per read, in the sentence's order - the detail is shown with
        // white-space: pre-line, so a line break is what keeps the two answers apart on screen.
        // BrokenReads answers the door map with an empty 500; the cell list gets the host's 503.
        var detail = cut.Find("#drawio-cells-error-detail");
        Assert.Equal(
            ["The door map of this device: the host answered 500 with an empty body",
             "The warehouse's cell list: the host answered 503: " + warehouseAnswer],
            detail.TextContent.Split('\n'));
        Assert.NotNull(detail.Closest("#drawio-cells-error-help-body"));
    }

    /// <summary>
    /// The third read is the one the panel can do without: the automatic cells are a read-only line
    /// inside <c>LinkedCells</c>, whose parameter is nullable for exactly this. Its failure costs
    /// that line and <b>is said</b> - the panel itself stays, because nothing a write is built from
    /// is missing. Row 37 of the queue: said as the cells are (row 29), the why in words and the
    /// host's answer behind the note's "?".
    /// </summary>
    [Fact]
    public async Task WhenTheAutomaticCellsCannotBeReadThePanelStaysAndTheMissingLineIsNamed()
    {
        var handler = HostWithCells(automationLinkedCellId: MappedCellId);
        handler.BrokenReads.Add("automatic-cells");
        var cut = RenderWith(handler);

        await SelectAsync(cut, AutomationId);

        Assert.NotNull(cut.Find("#drawio-cells-details"));
        Assert.NotNull(cut.Find("#linked-cells-link-button"));
        Assert.Equal(
            "The automatic cells of this device could not be read because the host answered with an error: the line about them is missing, the rest of this panel is not.",
            TextOf(cut, "#drawio-cells-notice"));
        Assert.Equal("the host answered 500 with an empty body", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-cells-notice"));
        Assert.Empty(cut.FindAll("#drawio-cells-error"));
        // Null is not an empty list: the component must not be showing "none" for a read that never
        // came back.
        Assert.Empty(cut.FindAll("div.wms-automatic-cells"));
    }

    // ---- The writes ------------------------------------------------------------------------------

    /// <summary>
    /// The door map really goes out, as one write of the whole wanted set, and what comes back onto
    /// the screen is <b>the host's</b> answer and not what was sent.
    /// </summary>
    /// <remarks>
    /// <b>The reread is the point of this test, not the write.</b> <c>DoorMappings</c> builds every
    /// row with <c>OperationActive = 0</c> and <c>StoringLocked = false</c> - it has no way of
    /// knowing either - while the host preserves both for rows that match one it already had. The
    /// seeded row carries <see cref="HostHeldOperationActive"/>, a value the panel cannot produce,
    /// so a page that kept what it sent would leave a zero here and this assertion would fail.
    /// </remarks>
    [Fact]
    public async Task ConfirmingTheDoorMapWritesItAndThenRereadsWhatTheHostHolds()
    {
        var handler = HostWithCells();
        var cut = RenderWith(handler);
        await SelectAsync(cut, StorageId);

        // The operator adds the second cell's door A to the one already mapped.
        await cut.Find($"#door-a-{OtherCellId}").ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = true });
        await cut.Find("#door-mappings-save-button").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        var sent = Assert.Single(handler.DoorMappingWrites);
        Assert.Equal([MappedDoorA, OtherDoorA], sent.Select(row => row.DoorCode ?? "").Order(StringComparer.Ordinal).ToArray());
        Assert.All(sent, row => Assert.Equal(StorageId, row.ItemId));
        // What the component sent carried zero, by construction - it cannot know either state: this
        // is what makes the last assertion able to fail.
        Assert.All(sent, row => Assert.Equal((short)0, row.OperationActive));

        Assert.Contains("door map", TextOf(cut, "#drawio-cells-written"));
        Assert.Empty(cut.FindAll("#drawio-cells-write-error"));
        Assert.Equal(2, handler.DoorMappings.Count(row => row.ItemId == StorageId));

        // The door map the panel is holding now, and it pins BOTH mutations of this line:
        //  - two rows, so the picture is not the pre-write snapshot (which had one) left untouched;
        //  - and the row that was already there still carries the host's own OperationActive, so
        //    the picture is not the rows that went out either (they carried zero, asserted above).
        // Either one alone leaves the other change invisible - measured: taking the reread out
        // entirely kept this test green until the count was asserted too.
        var onScreen = cut.FindComponent<Wcs.WebEditor.Wms.DoorMappings>().Instance.CurrentMappings;
        Assert.Equal(2, onScreen.Count);
        var reread = Assert.Single(onScreen, row => row.DoorCode == MappedDoorA);
        Assert.Equal(HostHeldOperationActive, reread.OperationActive);
        Assert.Contains(onScreen, row => row.DoorCode == OtherDoorA);
    }

    /// <summary>
    /// "Clear" is its own gesture and its own route (a DELETE), not a confirmation of an empty
    /// selection - which the host's PUT would treat as a no-op and leave the rows where they are.
    /// </summary>
    [Fact]
    public async Task UnmappingTheDoorsGoesOutAsADeleteAndEmptiesThemOnTheHost()
    {
        var handler = HostWithCells();
        var cut = RenderWith(handler);
        await SelectAsync(cut, StorageId);

        await cut.Find("#door-mappings-clear-button").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Equal(StorageId, Assert.Single(handler.DoorMappingClears));
        Assert.Empty(handler.DoorMappingWrites);
        Assert.DoesNotContain(handler.DoorMappings, row => row.ItemId == StorageId);
        Assert.Contains("no doors mapped", TextOf(cut, "#drawio-cells-written"));
    }

    /// <summary>
    /// Connecting a cell writes BOTH halves - the configuration's <c>LinkedCellId</c>, which is what
    /// the engine reads, and the door map row - and the configuration goes FIRST, which is the order
    /// <see cref="Wcs.WebEditor.Editing.LinkBufferCellCommand"/> exists to impose.
    /// </summary>
    /// <remarks>
    /// The properties panel is then reread, and that half is asserted too: <c>LinkedCellId</c> is one
    /// of the sixty fields, so a panel left holding the old value would write the link back out the
    /// next time its "Save" was pressed. The outcome line says that the reread costs whatever was
    /// typed and not confirmed, because it does.
    /// </remarks>
    [Fact]
    public async Task ConnectingACellWritesTheConfigurationFirstAndThenTheDoorMap()
    {
        var handler = HostWithCells();
        var cut = RenderWith(handler);
        await SelectAsync(cut, AutomationId);

        await cut.Find($"#linked-cell-{MappedCellId}").ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = true });
        await cut.Find("#linked-cells-link-button").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Equal(["config", "door-link"], handler.Writes.Where(w => w is "config" or "door-link").ToArray());
        Assert.Equal(MappedCellId, Assert.Single(handler.ConfigWrites).LinkedCellId);
        var link = Assert.Single(handler.CellLinks);
        Assert.Equal(AutomationId, link.ItemId);
        Assert.Equal(MappedDoorA, link.DoorCode);

        var said = TextOf(cut, "#drawio-cells-written");
        Assert.Contains($"cell {MappedCellId}", said);
        Assert.Contains("not confirmed is gone", said);

        // The sixty fields now carry the cell the plant has: the reread reached them.
        Assert.Equal(MappedCellId.ToString(), ValueOf(cut, nameof(PlantItemConfigDto.LinkedCellId)));
    }

    /// <summary>
    /// Row 37 of the queue: the cell is connected, and the reread of the configuration that follows
    /// gets no answer. The note says so in words - the client's timeout, not its Italian text - and
    /// what the client said is behind the note's "?".
    /// </summary>
    [Fact]
    public async Task AConfigurationThatCannotBeReadBackAfterConnectingACellIsSaidInWords()
    {
        var handler = HostWithCells();
        var cut = RenderWith(handler);
        await SelectAsync(cut, AutomationId);
        handler.TimeoutRoute = "config-get";

        await cut.Find($"#linked-cell-{MappedCellId}").ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = true });
        await cut.Find("#linked-cells-link-button").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Equal(MappedCellId, Assert.Single(handler.ConfigWrites).LinkedCellId);
        Assert.Equal(
            "The configuration could not be read back from the host after the write because the host did not answer in time, " +
            "so the fields below still show the cell this device had BEFORE: select the device again before confirming them, or they would write that old cell back.",
            TextOf(cut, "#drawio-cells-notice"));
        Assert.Equal(
            "TaskCanceledException: La richiesta su 'config-get' e' scaduta senza risposta (prova).",
            DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-cells-notice"));
    }

    /// <summary>
    /// Row 37 of the queue: the door map's write fails, and whether it went through is not known. The
    /// outcome says why in words; the host's answer is behind its "?", where the sentence used to
    /// end with the client's "L'API del motore ha risposto 500: ".
    /// </summary>
    [Fact]
    public async Task ADoorMapWriteThatFailsIsSaidInWordsWithTheHostsAnswerBehindTheQuestionMark()
    {
        var handler = HostWithCells();
        var cut = RenderWith(handler);
        await SelectAsync(cut, StorageId);
        handler.FailRoute = "door-put";

        await cut.Find($"#door-a-{OtherCellId}").ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = true });
        await cut.Find("#door-mappings-save-button").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Equal(
            "the door map failed because the host answered with an error, and whether it went through is NOT known - " +
            "the request may have reached the host anyway, so check the device before trying again.",
            TextOf(cut, "#drawio-cells-write-error"));
        Assert.Equal("the host answered 500 with an empty body", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-cells-write-error"));
    }

    /// <summary>
    /// Final review, M9 (C1, C16): a refusal the host's answer makes certain - a 409, a 423, the 503 of
    /// the engine not loaded, which the filter of the "engine" group answers before the endpoint runs -
    /// wrote nothing of THAT request; but connecting a cell makes several, and the configuration before
    /// the door map's link went through. The line says that, and not a refusal that "may have reached
    /// the host anyway".
    /// </summary>
    [Theory]
    [InlineData(System.Net.HttpStatusCode.Conflict, "{\"title\":\"Il motore non è fermo\",\"status\":409}", "the plant's state does not allow it now")]
    [InlineData(System.Net.HttpStatusCode.Locked, "{\"title\":\"Permesso di modifica di qualcun altro\",\"status\":423}", "the edit permit is not (any longer) this session's")]
    [InlineData(System.Net.HttpStatusCode.ServiceUnavailable, "{\"title\":\"Il motore non è caricato\",\"status\":503,\"unavailable\":\"engineNotLoaded\"}", "the engine is not ready (it is not loaded on the host)")]
    public async Task ARefusalOfOneOfTheCellsRequestsSaysThatTheOnesBeforeItWentThrough(System.Net.HttpStatusCode status, string answer, string meaning)
    {
        var handler = HostWithCells();
        var cut = RenderWith(handler);
        await SelectAsync(cut, AutomationId);
        handler.FailRoute = "door-link";
        handler.FailureStatus = status;
        handler.FailureBody = answer;

        await cut.Find($"#linked-cell-{MappedCellId}").ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = true });
        await cut.Find("#linked-cells-link-button").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Equal(MappedCellId, Assert.Single(handler.ConfigWrites).LinkedCellId);
        Assert.Empty(handler.CellLinks);
        Assert.Equal(
            $"connecting the cell stopped because the host refused one of its requests - {meaning}: that request wrote nothing, " +
            "but the ones before it, if there were any, went through, so check the device before trying again.",
            TextOf(cut, "#drawio-cells-write-error"));
        Assert.Equal($"the host answered {(int)status}: {answer}", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-cells-write-error"));
    }

    /// <summary>
    /// Final review, C11: the cell is connected, and the host answers the reading back after it that it
    /// does not have the device - the commands' own <c>InvalidOperationException</c>. The line says so in
    /// words, and the command's own text is behind its "?".
    /// </summary>
    [Fact]
    public async Task ADeviceTheHostNoLongerHasAfterConnectingACellIsSaidAsWrittenNotReadBack()
    {
        var handler = HostWithCells();
        var cut = RenderWith(handler);
        await SelectAsync(cut, AutomationId);
        handler.HideSingleItemReads = true;

        await cut.Find($"#linked-cell-{MappedCellId}").ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = true });
        await cut.Find("#linked-cells-link-button").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Single(handler.CellLinks);
        Assert.Equal(
            "The cells were written, but the device could not be read back afterwards because the host does not have it any more: reload the page before changing anything else.",
            TextOf(cut, "#drawio-cells-write-error"));
        Assert.StartsWith("InvalidOperationException: ", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-cells-write-error"));
    }

    /// <summary>Disconnecting is the mirror: <c>LinkedCellId</c> back to zero - the plant's own way of saying "none" - and the door map emptied.</summary>
    [Fact]
    public async Task DisconnectingTheCellPutsTheConfigurationBackToZeroAndEmptiesTheDoorMap()
    {
        var handler = HostWithCells(automationLinkedCellId: MappedCellId);
        handler.DoorMappings.Add(new DoorMappingDto(2, AutomationId, MappedDoorA, MappedCellId, "A", 0, false));
        var cut = RenderWith(handler);
        await SelectAsync(cut, AutomationId);

        await cut.Find("#linked-cells-unlink-button").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Equal(0m, Assert.Single(handler.ConfigWrites).LinkedCellId);
        Assert.Equal(AutomationId, Assert.Single(handler.DoorMappingClears));
        Assert.DoesNotContain(handler.DoorMappings, row => row.ItemId == AutomationId);
        Assert.Equal("0", ValueOf(cut, nameof(PlantItemConfigDto.LinkedCellId)));
    }

    // ---- The refusals ----------------------------------------------------------------------------

    /// <summary>
    /// Somebody else holds the edit permit: nothing is written, the refusal names the reason, and
    /// what the operator had selected in the panel is <b>still selected</b> - a refusal costs a
    /// retry, not the work.
    /// </summary>
    [Fact]
    public async Task WithoutThePermitNothingIsWrittenAndNothingIsLost()
    {
        var handler = HostWithCells();
        handler.GrantEditLock = false;
        var cut = RenderWith(handler);
        await SelectAsync(cut, StorageId);

        await cut.Find($"#door-a-{OtherCellId}").ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = true });
        await cut.Find("#door-mappings-save-button").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Empty(handler.DoorMappingWrites);
        Assert.Empty(handler.DoorMappingClears);
        Assert.Contains("Read only", TextOf(cut, "#drawio-cells-write-error"));
        Assert.Contains("somebody else", TextOf(cut, "#drawio-cells-write-error"));
        // The tick the operator made is still there: confirming again once the permit frees up costs
        // one click, not the whole selection.
        Assert.True(DoorIsTicked(cut, "A", OtherCellId), "The refusal took the operator's own tick away.");
    }

    /// <summary>
    /// Row 37 of the queue (the owner's answer of 9/10): the permit that could not be asked for is said
    /// on the cells' line with its why, and what came back is behind the line's "?".
    /// </summary>
    [Fact]
    public async Task APermitThatCannotBeAskedForIsSaidWithTheFailureBehindTheQuestionMark()
    {
        var handler = HostWithCells();
        var cut = RenderWith(handler);
        await SelectAsync(cut, StorageId);
        handler.HttpFailureRoute = "edit-lock";

        await cut.Find($"#door-a-{OtherCellId}").ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = true });
        await cut.Find("#door-mappings-save-button").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Empty(handler.DoorMappingWrites);
        Assert.Equal("The edit permit could not be asked for because the connection to the host failed: nothing was written.",
            TextOf(cut, "#drawio-cells-write-error").Trim());
        Assert.StartsWith("HttpRequestException: ", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-cells-write-error"));
        Assert.True(DoorIsTicked(cut, "A", OtherCellId), "The refusal took the operator's own tick away.");
    }

    /// <summary>With the engine running the plant is read only, and the refusal says which of the two reasons it is.</summary>
    [Fact]
    public async Task WithTheEngineRunningNothingIsWrittenAndTheReasonIsTheEngine()
    {
        var handler = HostWithCells();
        handler.NavStatus = NavigatorStatusTypes.Run;
        var cut = RenderWith(handler);
        await SelectAsync(cut, StorageId);

        await cut.Find("#door-mappings-clear-button").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Empty(handler.DoorMappingClears);
        Assert.Contains("the engine is running", TextOf(cut, "#drawio-cells-write-error"));
    }

    /// <summary>
    /// <b>The gate, and the name it gives.</b> A properties confirmation arriving while the cells are
    /// being written is refused - and the refusal says the CELLS are writing, not "this panel is
    /// already writing", which is what a fifth writer folded into the panel's own state would have
    /// produced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The overlap is staged, not hoped for: the cells write is held on the door map's own PUT -
    /// that is, past the permit and inside the write - and the properties' "Save" is pressed while
    /// it hangs there. Without the gate the two plans interleave; with a gate that did not say WHO
    /// holds it, the message would send whoever read it looking at the sixty fields for a
    /// confirmation nobody made.
    /// </para>
    /// <para>
    /// And the other half is asserted as well: nothing of the properties went out, so the refusal is
    /// not merely a truthful message about a write that happened anyway.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task APropertyWriteDuringACellWriteIsRefusedByNameInsteadOfBeingLost()
    {
        var handler = HostWithCells();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.DoorMappingGate = held;
        var cut = RenderWith(handler);
        await SelectAsync(cut, StorageId);

        await cut.Find($"#door-a-{OtherCellId}").ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = true });
        var cellWrite = cut.Find("#door-mappings-save-button").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        await handler.DoorMappingGateReached.Task;
        Assert.False(cellWrite.IsCompleted, "The cells write did not stay inside its own PUT: the overlap was never staged.");

        await cut.Find("#property-panel-save-button").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        var refusal = TextOf(cut, "#drawio-property-write-error");
        Assert.Contains("warehouse cells", refusal);
        Assert.DoesNotContain("This panel is already writing", refusal);
        Assert.DoesNotContain("The drawing is being saved", refusal);
        Assert.Empty(handler.ConfigWrites);

        held.SetResult();
        await cellWrite;
        Assert.Single(handler.DoorMappingWrites);
    }

    /// <summary>
    /// The mirror: a cells gesture arriving while the properties are being written is refused, and
    /// names the properties panel.
    /// </summary>
    [Fact]
    public async Task ACellWriteDuringAPropertyWriteIsRefusedByNameInsteadOfBeingLost()
    {
        var handler = HostWithCells();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.SingleItemGate = held;
        var cut = RenderWith(handler);
        await SelectAsync(cut, StorageId);

        // The properties write hangs on the reread EditItemConfigCommand does after its PUT.
        var propertyWrite = cut.Find("#property-panel-save-button").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        await handler.SingleItemGateReached.Task;
        Assert.False(propertyWrite.IsCompleted, "The properties write did not stay inside its reread: the overlap was never staged.");

        await cut.Find("#door-mappings-clear-button").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        var refusal = TextOf(cut, "#drawio-cells-write-error");
        Assert.Contains("properties panel", refusal);
        Assert.Empty(handler.DoorMappingClears);

        held.SetResult();
        await propertyWrite;
    }

    /// <summary>
    /// A confirmation dispatched against a render the page has already moved past - the selection
    /// changed while the click was travelling - writes nothing and <b>says so</b>. It is reachable:
    /// the callback handed to the component outlives the render that made it.
    /// </summary>
    /// <remarks>
    /// The way in is the callback the component was handed, kept exactly as a click already on its
    /// way from the browser keeps it - bUnit cannot click a button that is no longer in the DOM, and
    /// after the selection moves this one is not. Same shape as
    /// <c>DrawioEditorWriteTest.AConfirmationThatArrivesAfterThePanelIsGoneIsSaidInsteadOfDroppedInSilence</c>.
    /// </remarks>
    [Fact]
    public async Task AGestureThatArrivesAfterTheSelectionMovedOnIsSaidInsteadOfWritingOnTheWrongDevice()
    {
        var handler = HostWithCells();
        var cut = RenderWith(handler);
        await SelectAsync(cut, StorageId);

        // The callback the door panel was given, while the dock is still about the Storage device.
        var confirm = cut.FindComponent<Wcs.WebEditor.Wms.DoorMappings>().Instance.OnSave;
        var wanted = new List<DoorMappingDto>
        {
            new(0, StorageId, OtherDoorA, OtherCellId, "A", 0, false),
        };

        // And the selection moves on before it is delivered.
        await SelectAsync(cut, AutomationId);
        Assert.Empty(cut.FindAll("#door-mappings-save-button"));

        await cut.InvokeAsync(() => confirm.InvokeAsync(wanted));

        Assert.Empty(handler.DoorMappingWrites);
        Assert.Empty(handler.DoorMappingClears);
        Assert.Contains("no longer showing", TextOf(cut, "#drawio-cells-write-error"));
    }

    /// <summary>
    /// The outcome of a cells gesture belongs to the device it was about: selecting another one
    /// takes it away rather than carrying it over, where it would claim something about a device
    /// nobody wrote to.
    /// </summary>
    [Fact]
    public async Task TheOutcomeOfACellsGestureDoesNotFollowTheSelectionToAnotherDevice()
    {
        var handler = HostWithCells();
        var cut = RenderWith(handler);
        await SelectAsync(cut, StorageId);

        await cut.Find("#door-mappings-clear-button").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        Assert.NotNull(cut.Find("#drawio-cells-written"));

        await SelectAsync(cut, AutomationId);

        Assert.Empty(cut.FindAll("#drawio-cells-written"));
        Assert.Empty(cut.FindAll("#drawio-cells-write-error"));
    }

    /// <summary>
    /// <b>The device vanished, and the host refuses.</b> This test used to assert the opposite, and
    /// it was right at the time: none of the door-map routes looked at <c>PlantItems</c>, the table
    /// had no foreign key towards it, and all three answered <c>204</c> - so the write went through
    /// and left orphan rows behind. Since the 404 batch they answer <c>404</c> on a device that is
    /// not there, and the page says the gesture wrote NOTHING instead of declaring it written.
    /// </summary>
    /// <remarks>
    /// The assertion that carries the batch is not the message: it is
    /// <c>handler.DoorMappings</c>, which must still hold the rows of the vanished device untouched.
    /// A page that showed a refusal while the host had quietly rewritten the map would pass on the
    /// message alone.
    /// </remarks>
    [Fact]
    public async Task WhenTheDeviceIsGoneTheDoorMapWriteIsRefusedAndNothingIsWritten()
    {
        var handler = HostWithCells();
        var cut = RenderWith(handler);
        await SelectAsync(cut, StorageId);

        var before = handler.DoorMappings.Where(m => m.ItemId == StorageId).ToList();
        Assert.NotEmpty(before); // altrimenti "niente e' cambiato" non direbbe niente

        // Somebody else deletes the device while the panel is open.
        handler.Items.RemoveAll(item => item.ItemId == StorageId);
        handler.Configs.Remove(StorageId);

        await cut.Find("#door-mappings-clear-button").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        // La richiesta e' partita davvero - il rifiuto arriva dall'host, non da una guardia locale
        // che avrebbe potuto tenersela per se'.
        Assert.Equal(StorageId, Assert.Single(handler.DoorMappingClears));

        // E non ha cambiato niente: le righe del dispositivo sparito sono ancora quelle.
        Assert.Equal(before, handler.DoorMappings.Where(m => m.ItemId == StorageId).ToList());

        Assert.Empty(cut.FindAll("#drawio-cells-written"));
        var refusal = TextOf(cut, "#drawio-cells-write-error");
        Assert.Contains("wrote NOTHING", refusal, StringComparison.Ordinal);
        Assert.Contains("does not have this device any more", refusal, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>A gesture that really went through goes on saying so, even when the device disappears
    /// before it can be read back.</b> The line "Written: ... is connected to cell X on the plant"
    /// is the only record the operator has that the link happened - and it used to be swept away by
    /// the very reread that came to check on it.
    /// </summary>
    /// <remarks>
    /// The road in is <c>RereadAfterCellLinkAsync</c>: when the configuration comes back null - the
    /// device deleted AGAIN between the write and the reread - it used to call
    /// <c>ForgetSelectedCells()</c>, which clears the outcome fields along with the panel's own. All
    /// that was left was "The configuration could not be read...", which says nothing about the
    /// write and reads like a gesture that failed. It did not fail: it is on the plant, and the
    /// door-map row proves it.
    /// </remarks>
    [Fact]
    public async Task ALinkThatWentThroughKeepsSayingSoWhenTheDeviceVanishesBeforeTheReread()
    {
        var handler = HostWithCells();
        var cut = RenderWith(handler);
        await SelectAsync(cut, AutomationId);

        await cut.Find($"#linked-cell-{MappedCellId}").ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = true });

        // Held on the door-map reread, which is AFTER the command - so both writes and the command's
        // own reread of the device have already gone through - and BEFORE the configuration reread.
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.DoorMappingReadGate = held;
        var linking = cut.Find("#linked-cells-link-button").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        await handler.DoorMappingReadGateReached.Task;
        Assert.False(linking.IsCompleted, "The link did not stay inside its reread: the case was never staged.");
        Assert.Single(handler.CellLinks);

        // And in that window somebody deletes the device again - from the plant, not only its
        // configuration row: since the owner's decision of 27/9 (D3) this fake answers a device's
        // configuration for every device of Items, as the real host does.
        handler.Items.RemoveAll(item => item.ItemId == AutomationId);
        handler.Configs.Remove(AutomationId);

        held.SetResult();
        await linking;

        // What the host now answers about the device is said...
        Assert.Contains("is not (any more) on the host", TextOf(cut, "#drawio-property-error"));
        // ...and what was written is STILL said, because it was written.
        Assert.Contains($"cell {MappedCellId}", TextOf(cut, "#drawio-cells-written"));
        Assert.Empty(cut.FindAll("#drawio-cells-write-error"));
    }

    /// <summary>
    /// The configuration read back after a link can be a device of another <b>kind</b> - somebody
    /// else changed its type meanwhile - and the panel then changes shape while the lists in it were
    /// read for the kind it used to be. It is taken away and said, rather than redrawn from lists
    /// that do not belong to it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rare, and not impossible: <c>SelectedCellsKind</c> is worked out from
    /// <c>_selectedConfig.DeviceType</c> at every render, and <c>RereadAfterCellLinkAsync</c> is the
    /// one place on this page that REASSIGNS that configuration - the properties panel's own
    /// confirmation deliberately does not.
    /// </para>
    /// <para>
    /// What it would cost is not cosmetic: the warehouse list in hand was read with
    /// <c>automaticOnly=true</c> for an Automation device, and drawn as a Storage device's door
    /// tables it would offer, for mapping, cells the warehouse never said were candidates for that
    /// area - and "Save" would write them.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ARereadThatChangesTheDeviceKindTakesTheCellsAwayInsteadOfRedrawingTheOldLists()
    {
        var handler = HostWithCells();
        var cut = RenderWith(handler);
        await SelectAsync(cut, AutomationId);
        Assert.NotNull(cut.Find("#linked-cells-link-button"));

        await cut.Find($"#linked-cell-{MappedCellId}").ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = true });

        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.DoorMappingReadGate = held;
        var linking = cut.Find("#linked-cells-link-button").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        await handler.DoorMappingReadGateReached.Task;

        // Another editor turns it into a Storage device while the reread is on its way.
        handler.Configs[AutomationId] = Config(AutomationId, AutomationName, DeviceTypes.Storage, MappedCellId);

        held.SetResult();
        await linking;

        // No panel of either kind, and above all no door tables built out of an automatic-only list.
        Assert.Empty(cut.FindAll("#drawio-cells-details"));
        Assert.Empty(cut.FindAll("#door-mappings-save-button"));
        Assert.Empty(cut.FindAll("#linked-cells-link-button"));
        // Said, and not left on "Reading the warehouse cells from the host...", which would be the
        // same lie this batch has just taken out of the other path.
        Assert.Empty(cut.FindAll("#drawio-cells-loading"));
        Assert.Contains("no longer the kind", TextOf(cut, "#drawio-cells-error"));
        // And the write is still declared, because it went through.
        Assert.Contains($"cell {MappedCellId}", TextOf(cut, "#drawio-cells-written"));
    }

    // ---- What no filter used to name -------------------------------------------------------------

    /// <summary>
    /// <b>A 200 carrying something that is not JSON</b> - an intermediary answering with its own
    /// login page, which this project has already met (<c>GarbledRoute</c>). <c>WcsClient</c> does
    /// not wrap the <c>JsonException</c> that <c>ReadFromJsonAsync</c> then throws, and the cells
    /// read is awaited OUTSIDE any try of <c>RefreshSelectedConfigAsync</c>: the exception used to
    /// climb all the way back to the JS bridge, where a rejected promise takes the circuit down and
    /// nobody sees a message at all.
    /// </summary>
    /// <remarks>
    /// <b>This test fails by THROWING if the belt is not there</b>, because <c>SelectAsync</c> awaits
    /// the <c>[JSInvokable]</c> method itself - so "no exception leaves this method" is measured
    /// here and not declared in a comment. Same shape as
    /// <c>DrawioEditorCreateTest.AnAnswerThatIsNotJsonOnThePermitNamesEveryCellInstead</c>.
    /// </remarks>
    [Fact]
    public async Task AnAnswerThatIsNotJsonOnTheCellsReadIsSaidInsteadOfReachingTheBridge()
    {
        var handler = HostWithCells();
        handler.GarbledRoute = "wms-cells";
        var cut = RenderWith(handler);

        await SelectAsync(cut, StorageId);

        Assert.Contains("unforeseen", TextOf(cut, "#drawio-cells-error"));
        Assert.Empty(cut.FindAll("#drawio-cells-loading"));
        // The sixty properties came back fine and are untouched by a failure that is not theirs.
        Assert.NotEmpty(cut.FindAll("#drawio-property-panel fieldset.property-panel-group"));
    }

    /// <summary>
    /// Row 29 of the queue, on the same element's other failure: the unforeseen one is one plain
    /// sentence too, and what the exception said - here the JSON reader's words about a login page -
    /// is behind the "?", with its type, instead of tacked onto "Nothing here has been changed".
    /// </summary>
    /// <remarks>
    /// Final round of row 29: the sentence is held word for word (a StartsWith and an EndsWith let
    /// anything in between, the reader's words included), and the "?" is held to the reader's own
    /// words and not only to its type. The words are System.Text.Json's for the login page's first
    /// character, measured on this page.
    /// </remarks>
    [Fact]
    public async Task AnUnforeseenFailureOfTheCellsReadKeepsItsOwnWordsBehindTheQuestionMark()
    {
        var handler = HostWithCells();
        handler.GarbledRoute = "wms-cells";
        var cut = RenderWith(handler);

        await SelectAsync(cut, StorageId);

        Assert.Equal(
            "The cells of this device are not shown: reading them stopped on an unforeseen failure; nothing here has been changed.",
            TextOf(cut, "#drawio-cells-error"));
        var detail = cut.Find("#drawio-cells-error-detail");
        Assert.Contains("JsonException: '<' is an invalid start of a value.", detail.TextContent);
        Assert.NotNull(detail.Closest("#drawio-cells-error-help-body"));
    }

    /// <summary>
    /// The same answer on the reread that follows a write, which is reached from a component's event
    /// callback rather than from the bridge - and where an exception would take the circuit down
    /// just the same, over a write that had already gone through. Row 37 of the queue: said in words -
    /// it is an answer that could not be read, no longer "an unforeseen failure" -, and what the parser
    /// said is behind the note's "?".
    /// </summary>
    [Fact]
    public async Task AnAnswerThatIsNotJsonOnTheRereadIsSaidWithoutTakingTheWriteWithIt()
    {
        var handler = HostWithCells();
        var cut = RenderWith(handler);
        await SelectAsync(cut, StorageId);

        handler.GarbledRoute = "door-mappings";
        await cut.Find("#door-mappings-clear-button").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Equal(StorageId, Assert.Single(handler.DoorMappingClears));
        Assert.Contains("no doors mapped", TextOf(cut, "#drawio-cells-written"));
        Assert.Equal(
            "The door map on screen could not be read back from the host after the write because the host's answer could not be read, " +
            "so what is above is how it was BEFORE: select the device again to see what the plant now holds.",
            TextOf(cut, "#drawio-cells-notice"));
        var detail = cut.Find("#drawio-cells-notice-detail");
        Assert.StartsWith("JsonException: '<' is an invalid start of a value.", detail.TextContent);
        Assert.NotNull(detail.Closest("#drawio-cells-notice-help-body"));
    }

    private static string? ValueOf(IRenderedComponent<DrawioEditor> cut, string propertyName) =>
        cut.Find($"#drawio-property-panel div.property-panel-field[data-property='{propertyName}'] input").GetAttribute("value");

    // ---- The fourteen crossed refusals ----------------------------------------------------------
    // Four gestures write to this plant - the drawing's save, the properties panel, the birth of a
    // dropped device and these warehouse cells - and each of them refuses the other three BY NAME.
    // The final review found the code sound and the NET full of holes: it mutated the Creating
    // branch of the cells' refusal so that it accused the wrong gesture, and the whole suite stayed
    // green. A refusal naming the wrong gesture sends whoever reads it looking at a part of the
    // screen where nothing is happening, which is the family of defect this page spends itself on.
    //
    // What holds each writer still, and why that point:
    //   Drawing  - EditLockGate, the permit request: _plantWriter is raised BEFORE it is asked for
    //              (that ordering is itself a repair, see DrawioEditor._plantWriter), so the save
    //              is really holding the plant while it hangs there. The gate is consumed by the
    //              first request, so the gesture that follows can still ask for its own permit and
    //              be refused by the PAGE, which is what is being measured.
    //   Panel    - SingleItemGate, the reread EditItemConfigCommand does after its PUT.
    //   Cells    - DoorMappingGate, the door map's own PUT.
    //   Creating - ItemPostGate, the POST that gives a dropped shape its device.
    // Every one of them checks that the first gesture really is suspended before the second starts:
    // a hold that was never reached would make all of these green while measuring nothing.

    /// <summary>
    /// Waits for a hold to be REACHED, with a ceiling. A gesture that never gets that far would
    /// otherwise hang the whole suite for ever instead of saying which hold was never reached - and
    /// a suite that hangs is a suite nobody runs. Measured while writing these: a save that stops
    /// before asking for the permit leaves that wait pending, and the run had to be killed.
    /// </summary>
    private static async Task Reached(TaskCompletionSource gate, string what)
    {
        if (await Task.WhenAny(gate.Task, Task.Delay(TimeSpan.FromSeconds(10))) != gate.Task)
            throw new Xunit.Sdk.XunitException($"The gesture never reached {what}: the overlap was never staged, and nothing was measured.");
    }

    /// <summary>The plant's own drawing, so that a save gets past "this is not the plant's drawing" and reaches the permit.</summary>
    private static string DrawingOf(DrawioWriteHandler handler) =>
        DrawioDocument.Build(new Wcs.WebEditor.Model.PlantGraph([.. handler.Items], [], [.. handler.Types]), null);

    /// <summary>
    /// The cell a drop leaves on the canvas, built out of <c>DrawioLibrary</c>'s own entry the way
    /// <c>DrawioEditorCreateTest.Dropped</c> does: the mark and the type attribute are read from the
    /// XML draw.io is really given, not written by hand here.
    /// </summary>
    private static DrawioAddedCell Dropped(string cellId)
    {
        var automation = Type("automation", DeviceTypes.Automation, "Automation");
        var entries = System.Text.Json.JsonDocument.Parse(DrawioLibrary.Build([automation]));
        var xml = entries.RootElement[0].GetProperty("entries")[0].GetProperty("libs")[0]
            .GetProperty("data")[0].GetProperty("xml").GetString()!;
        var cell = Assert.Single(DrawioReader.Read(xml).Devices);
        var declaredType = (string?)System.Xml.Linq.XDocument.Parse(xml).Descendants("object").Single()
            .Attribute(DrawioLibrary.TypeAttribute);
        return new DrawioAddedCell(cellId, cell.Mark, declaredType,
            DrawioDocument.DefaultLayerId, 900, 40, cell.Width, cell.Height);
    }

    /// <summary>
    /// Holds the drawing's save on its permit request and gives back the suspended save.
    /// </summary>
    /// <remarks>
    /// <b>The seeding first, and it is not a formality</b>: a save whose drawing was never sent out
    /// is refused before it asks for anything ("The drawing never received the plant"), so the hold
    /// was never reached and the two tests that use it hung until the ceiling in
    /// <see cref="Reached"/> caught them. Measured while writing them.
    /// </remarks>
    private static async Task<Task> ADrawingSaveHeldAsync(DrawioWriteHandler handler, IRenderedComponent<DrawioEditor> cut)
    {
        await cut.InvokeAsync(() => cut.Instance.OnDrawioReady());
        handler.EditLockGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saving = cut.InvokeAsync(() => cut.Instance.OnDrawioSaved(DrawingOf(handler)));
        await Reached(handler.EditLockGateReached, "the permit request of the drawing's save");
        Assert.False(saving.IsCompleted, "The drawing's save did not stay suspended: the overlap was never staged.");
        return saving;
    }

    /// <summary>Holds the birth of a dropped device on its POST and gives back the suspended creation.</summary>
    private static async Task<Task> ADropHeldAsync(DrawioWriteHandler handler, IRenderedComponent<DrawioEditor> cut)
    {
        handler.ItemPostGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var creating = cut.InvokeAsync(() => cut.Instance.OnDrawioCellsAdded([Dropped("drawio-1")]));
        await Reached(handler.ItemPostGateReached, "the POST that creates the dropped device");
        Assert.False(creating.IsCompleted, "The creation did not stay inside its own POST: the overlap was never staged.");
        return creating;
    }

    /// <summary>Holds a cells write on the door map's own PUT and gives back the suspended write.</summary>
    private static async Task<Task> ACellWriteHeldAsync(DrawioWriteHandler handler, IRenderedComponent<DrawioEditor> cut)
    {
        handler.DoorMappingGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await cut.Find($"#door-a-{OtherCellId}").ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = true });
        var writing = cut.Find("#door-mappings-save-button").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        await Reached(handler.DoorMappingGateReached, "the PUT of the door map");
        Assert.False(writing.IsCompleted, "The cells write did not stay inside its own PUT: the overlap was never staged.");
        return writing;
    }

    /// <summary>Holds a properties write on the reread it does after its PUT and gives back the suspended write.</summary>
    private static async Task<Task> APropertyWriteHeldAsync(DrawioWriteHandler handler, IRenderedComponent<DrawioEditor> cut)
    {
        handler.SingleItemGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writing = cut.Find("#property-panel-save-button").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        await Reached(handler.SingleItemGateReached, "the reread the properties write does after its PUT");
        Assert.False(writing.IsCompleted, "The properties write did not stay inside its reread: the overlap was never staged.");
        return writing;
    }

    /// <summary>A drawing save arriving while the warehouse cells are being written names the CELLS.</summary>
    [Fact]
    public async Task ADrawingSaveDuringACellWriteIsRefusedByName()
    {
        var handler = HostWithCells();
        var cut = RenderWith(handler);
        await SelectAsync(cut, StorageId);
        var held = await ACellWriteHeldAsync(handler, cut);

        await cut.InvokeAsync(() => cut.Instance.OnDrawioSaved(DrawingOf(handler)));

        var refusal = TextOf(cut, "#drawio-save-error");
        Assert.Contains("warehouse cells", refusal);
        Assert.DoesNotContain("properties panel", refusal);
        Assert.DoesNotContain("dropped on the drawing", refusal);
        Assert.Empty(handler.GeometryWrites);

        handler.DoorMappingGate!.SetResult();
        await held;
    }

    /// <summary>A shape dropped while the warehouse cells are being written names the CELLS, and no device is born.</summary>
    [Fact]
    public async Task ADropDuringACellWriteIsRefusedByName()
    {
        var handler = HostWithCells();
        var cut = RenderWith(handler);
        await SelectAsync(cut, StorageId);
        var held = await ACellWriteHeldAsync(handler, cut);

        await cut.InvokeAsync(() => cut.Instance.OnDrawioCellsAdded([Dropped("drawio-1")]));

        var refusal = TextOf(cut, "#drawio-creation-error");
        Assert.Contains("warehouse cells", refusal);
        Assert.DoesNotContain("properties panel", refusal);
        Assert.Empty(handler.ItemCreations);

        handler.DoorMappingGate!.SetResult();
        await held;
    }

    /// <summary>A shape dropped while the properties panel is writing names the PANEL, and no device is born.</summary>
    [Fact]
    public async Task ADropDuringAPropertyWriteIsRefusedByName()
    {
        var handler = HostWithCells();
        var cut = RenderWith(handler);
        await SelectAsync(cut, StorageId);
        var held = await APropertyWriteHeldAsync(handler, cut);

        await cut.InvokeAsync(() => cut.Instance.OnDrawioCellsAdded([Dropped("drawio-1")]));

        var refusal = TextOf(cut, "#drawio-creation-error");
        Assert.Contains("properties panel", refusal);
        Assert.DoesNotContain("warehouse cells", refusal);
        Assert.Empty(handler.ItemCreations);

        handler.SingleItemGate!.SetResult();
        await held;
    }

    /// <summary>
    /// A properties confirmation arriving while a dropped device is being created names the DROP.
    /// </summary>
    [Fact]
    public async Task APropertyWriteDuringADropIsRefusedByName()
    {
        var handler = HostWithCells();
        var cut = RenderWith(handler);
        await SelectAsync(cut, StorageId);
        var held = await ADropHeldAsync(handler, cut);

        await cut.Find("#property-panel-save-button").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        var refusal = TextOf(cut, "#drawio-property-write-error");
        Assert.Contains("dropped on the drawing", refusal);
        Assert.DoesNotContain("warehouse cells", refusal);
        Assert.DoesNotContain("This panel is already writing", refusal);
        Assert.Empty(handler.ConfigWrites);

        handler.ItemPostGate!.SetResult();
        await held;
    }

    /// <summary>
    /// <b>The one the review mutated.</b> A warehouse-cells gesture arriving while a dropped device
    /// is being created names the DROP - and with that branch made to accuse the properties panel
    /// instead, the whole suite used to stay green.
    /// </summary>
    [Fact]
    public async Task ACellWriteDuringADropIsRefusedByName()
    {
        var handler = HostWithCells();
        var cut = RenderWith(handler);
        await SelectAsync(cut, StorageId);
        var held = await ADropHeldAsync(handler, cut);

        await cut.Find("#door-mappings-clear-button").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        var refusal = TextOf(cut, "#drawio-cells-write-error");
        Assert.Contains("dropped on the drawing", refusal);
        Assert.DoesNotContain("properties panel", refusal);
        Assert.DoesNotContain("The drawing is being saved", refusal);
        Assert.Empty(handler.DoorMappingClears);

        handler.ItemPostGate!.SetResult();
        await held;
    }

    /// <summary>A drawing save arriving while a dropped device is being created names the DROP.</summary>
    [Fact]
    public async Task ADrawingSaveDuringADropIsRefusedByName()
    {
        var handler = HostWithCells();
        var cut = RenderWith(handler);
        await SelectAsync(cut, StorageId);
        var held = await ADropHeldAsync(handler, cut);

        await cut.InvokeAsync(() => cut.Instance.OnDrawioSaved(DrawingOf(handler)));

        var refusal = TextOf(cut, "#drawio-save-error");
        Assert.Contains("dropped on the drawing", refusal);
        Assert.DoesNotContain("warehouse cells", refusal);
        Assert.Empty(handler.GeometryWrites);

        handler.ItemPostGate!.SetResult();
        await held;
    }

    /// <summary>A warehouse-cells gesture arriving while the drawing is being saved names the SAVE.</summary>
    [Fact]
    public async Task ACellWriteDuringADrawingSaveIsRefusedByName()
    {
        var handler = HostWithCells();
        var cut = RenderWith(handler);
        await SelectAsync(cut, StorageId);
        var held = await ADrawingSaveHeldAsync(handler, cut);

        await cut.Find("#door-mappings-clear-button").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        var refusal = TextOf(cut, "#drawio-cells-write-error");
        Assert.Contains("drawing is being saved", refusal);
        Assert.DoesNotContain("properties panel", refusal);
        Assert.Empty(handler.DoorMappingClears);

        handler.EditLockGate!.SetResult();
        await held;
    }

    /// <summary>
    /// The self-refusals, which are the other two of the fourteen: a second cells gesture and a
    /// second save name THEMSELVES, and not one of the other three gestures.
    /// </summary>
    [Fact]
    public async Task ASecondCellWriteDuringACellWriteNamesTheCellsThemselves()
    {
        var handler = HostWithCells();
        var cut = RenderWith(handler);
        await SelectAsync(cut, StorageId);
        var held = await ACellWriteHeldAsync(handler, cut);

        await cut.Find("#door-mappings-clear-button").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        var refusal = TextOf(cut, "#drawio-cells-write-error");
        Assert.Contains("cells of this device are already being written", refusal);
        Assert.DoesNotContain("properties panel", refusal);
        Assert.DoesNotContain("The drawing is being saved", refusal);
        Assert.Empty(handler.DoorMappingClears);

        handler.DoorMappingGate!.SetResult();
        await held;
    }

    /// <summary>The save's own self-refusal: a second save says the first one is still on its way.</summary>
    [Fact]
    public async Task ASecondDrawingSaveDuringADrawingSaveNamesTheSaveAlreadyOnItsWay()
    {
        var handler = HostWithCells();
        var cut = RenderWith(handler);
        await SelectAsync(cut, StorageId);
        var held = await ADrawingSaveHeldAsync(handler, cut);

        await cut.InvokeAsync(() => cut.Instance.OnDrawioSaved(DrawingOf(handler)));

        var refusal = TextOf(cut, "#drawio-save-error");
        Assert.Contains("already being saved", refusal);
        Assert.DoesNotContain("properties panel", refusal);
        Assert.DoesNotContain("warehouse cells", refusal);

        handler.EditLockGate!.SetResult();
        await held;
    }
}
