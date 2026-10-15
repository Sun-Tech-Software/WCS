using Wcs.Api.Client;
using Wcs.Api.Contracts.Dtos;
using Xunit;
using Xunit.Abstractions;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// Compito 3, row 60, against a real test host: a mission moved from the page's missions list onto
/// another device ARRIVES ON THE HOST - read back with the client, not from the page - and, since the
/// missions plan of 30/9, its load unit moves on the canvas without a reload (queue item 31). And the
/// change of destination (task 6): set from the list, with its new route on the canvas at the hover
/// with the engine stopped, and a gateway that blocks the way cleared from the refusal's own button.
/// </summary>
/// <remarks>
/// <para>
/// <b>How the test host comes to hold a mission.</b> No route of the host creates one directly: the
/// missions come from the warehouse or from the engine. The engine's own <c>LoadLu</c> command (3002)
/// builds a mission on the device it names - but only with the engine in Run
/// (<c>CommandGateway.RequiresRunningEngine</c>, and LoadLu itself). So the seed starts the engine with
/// the client, loads the mission, and STOPS the engine again before the page opens: neither the move
/// nor the change of destination needs a running engine (<c>MissionGateway</c>), and a stopped one does
/// not start steps that would make the host refuse them (a step in flight). With no load unit
/// (<c>LuId</c> 0) <c>LoadLu</c> asks nothing of the warehouse; with one it asks for its description
/// (<c>DescribeLoadUnitAsync</c>), which is why the fixture's warehouse knows exactly one
/// (<see cref="DrawioMissionsFixture"/>) and answers nothing else.
/// </para>
/// <para>
/// <b>Queue item 31 needs that load unit.</b> A load unit arriving on a device linked to a warehouse
/// cell is registered there (<c>PathMission.AllocateOnItem</c>, in the background work of the move);
/// with the warehouse not answering that call fails, and until the fix the announcement of the two
/// devices came after it and was lost - the canvas kept the load unit where it was. A mission without
/// a load unit never calls the warehouse there (<c>AllocateOnItem</c> returns at once on <c>LuId</c>
/// 0), and would prove nothing about it: hence the load unit, the cell linked to the device it is
/// moved onto, and the check that the warehouse was really asked.
/// </para>
/// </remarks>
public sealed class DrawioMissionsBrowserTest(DrawioMissionsFixture fixture, ITestOutputHelper output) : IClassFixture<DrawioMissionsFixture>
{
    private const string SeedHolder = "browser-test-missions";

    /// <summary>The warehouse cell linked to the device the mission is moved onto: any number, the fake warehouse answers 503 to it.</summary>
    private const decimal UntouchedCell = 900301m;

    private static readonly Guid GatewayId = new("a1b2c3d4-0000-4000-8000-000000000009");
    private const string GatewayName = "GatewayDevice";

    [Fact]
    public async Task AMissionMovedFromTheListIsWhereTheHostSaysItIs()
    {
        output.WriteLine(fixture.Host.Declaration);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        void Stage(string reached) => output.WriteLine($"STAGE {clock.Elapsed.TotalSeconds,6:0.00} s: {reached}");
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder, untouchedLinkedCellId: UntouchedCell);
        Stage("plant seeded");

        var mission = await LoadAMissionOnTheDraggedDeviceAsync(client, DrawioMissionsFixture.LoadUnitId);
        Stage($"mission {mission.MissionId} with load unit {mission.LoadUnitCode} on {PlantSeed.DraggedName}");
        Assert.Equal(DrawioMissionsFixture.LoadUnitCode, mission.LoadUnitCode);

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForTheLoadMarkAsync(PlantSeed.DraggedId.ToString(), shown: true);
        await editor.WaitForTheLoadMarkAsync(PlantSeed.UntouchedId.ToString(), shown: false);
        Stage("page open, the load unit drawn on the dragged device");

        await editor.ClickAsync("#drawio-mission-list-toggle");
        var row = $"#drawio-mission-list-table tr[data-mission-id='{mission.MissionId}']";
        await editor.WaitForTextAsync($"{row} td[data-column='PositionItemId']", PlantSeed.DraggedName);
        await editor.ClickAsync($"{row} td[data-column='PositionItemId']");
        await editor.WaitForAttributeAsync(row, "aria-selected", "true");

        await editor.FillAsync("#drawio-mission-move-search", "Untouched");
        await editor.SelectOptionAsync("#drawio-mission-move-target", PlantSeed.UntouchedId.ToString());
        await editor.WaitForTextAsync("#drawio-mission-move-target-chosen", $"Target: {PlantSeed.UntouchedName}");
        await editor.ClickAsync("#drawio-mission-move-button");
        await editor.WaitForTextAsync("#drawio-mission-move-question", $"to {PlantSeed.UntouchedName}?");
        await editor.ClickAsync("#drawio-mission-move-confirm");
        await editor.WaitForTextAsync("#drawio-mission-move-outcome", $"was moved onto {PlantSeed.UntouchedName}: the host took it.");
        Stage("move confirmed by the host");

        Assert.Equal(PlantSeed.UntouchedId, (await client.GetMissionAsync(mission.MissionId))?.PositionItemId);
        await editor.WaitForTextAsync($"{row} td[data-column='PositionItemId']", PlantSeed.UntouchedName);
        Stage("row follows the host");

        // Queue item 31: the load unit moves on the canvas, with no reload, while the warehouse did
        // not take the registration on the device's cell - asked, and refused.
        await editor.WaitForTheLoadMarkAsync(PlantSeed.UntouchedId.ToString(), shown: true);
        await editor.WaitForTheLoadMarkAsync(PlantSeed.DraggedId.ToString(), shown: false);
        // The engine announces the two devices BEFORE it asks the warehouse (PathMission.PositionWork):
        // the mark on the canvas does not mean the request has gone yet, so it is waited for.
        await fixture.Warehouse.Requested("/api/wcs/warehouse/load-units/allocate").WaitAsync(TimeSpan.FromSeconds(30));
        Stage("load unit moved on the canvas, the warehouse refused its cell");
    }

    /// <summary>
    /// Missions plan of 30/9, task 6: a destination set from the list goes to the host with its new
    /// route at once, the engine STOPPED; the hover on the device the load unit is on - the operator's
    /// own mouse, on, with the engine stopped - draws that route, read from the host.
    /// </summary>
    [Fact]
    public async Task ADestinationSetFromTheListShowsItsNewRouteOnHoverWithTheEngineStopped()
    {
        output.WriteLine(fixture.Host.Declaration);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        void Stage(string reached) => output.WriteLine($"STAGE {clock.Elapsed.TotalSeconds,6:0.00} s: {reached}");
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder, withJunction: true);
        var mission = await LoadAMissionOnTheDraggedDeviceAsync(client, loadUnitId: 0);
        Assert.Empty(await client.GetMissionStepsAsync(mission.MissionId));
        Stage($"mission {mission.MissionId} on {PlantSeed.DraggedName}, no route");

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForTheLoadMarkAsync(PlantSeed.DraggedId.ToString(), shown: true);
        await ChooseTheMissionAndTheUntouchedDeviceAsync(editor, mission);

        await editor.ClickAsync("#drawio-mission-destination-button");
        await editor.WaitForTextAsync("#drawio-mission-destination-question", $"Send the mission {mission.MissionId} to {PlantSeed.UntouchedName}?");
        await editor.WaitForTextAsync("#drawio-mission-destination-gateway-now", "Gateway: none");
        await editor.ClickAsync("#drawio-mission-destination-confirm");
        await editor.WaitForTextAsync("#drawio-mission-move-outcome", $"is now bound for {PlantSeed.UntouchedName}: the host took it.");
        Stage("destination set by the host");

        Assert.Equal(PlantSeed.UntouchedId, (await client.GetMissionAsync(mission.MissionId))?.DestinationItemId);
        var step = Assert.Single(await client.GetMissionStepsAsync(mission.MissionId));
        Assert.Equal((PlantSeed.DraggedId, PlantSeed.UntouchedId), (step.FromNodeId, step.ToNodeId));
        Assert.Equal(NavigatorStatusTypes.Stop, (await client.GetStatusAsync()).NavStatus);
        await editor.WaitForTextAsync(
            $"#drawio-mission-list-table tr[data-mission-id='{mission.MissionId}'] td[data-column='DestinationItemId']", PlantSeed.UntouchedName);
        Stage("route built at once with the engine stopped, the row reads it");

        // The window is out of the way of the mouse, which goes onto the device itself.
        await editor.CloseTheWindowAsync("drawio-mission-list");
        await editor.HoverOverDeviceAsync(PlantSeed.DraggedName);
        await editor.WaitForTheRouteOnHoverAsync();
        Stage("route drawn on hover");
    }

    /// <summary>
    /// The owner's decision on the gateway (30/9, evening): a gateway added in the question that blocks
    /// the way is refused in words that say a route exists without it, nothing changes on the host, and
    /// the refusal's own button sends the change again with the gateway cleared - which goes through.
    /// </summary>
    [Fact]
    public async Task AGatewayThatBlocksTheWayIsClearedFromTheRefusalAndTheDestinationIsSet()
    {
        output.WriteLine(fixture.Host.Declaration);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        void Stage(string reached) => output.WriteLine($"STAGE {clock.Elapsed.TotalSeconds,6:0.00} s: {reached}");
        using var client = fixture.CreateClient();
        await PlantSeed.ResetAsync(client, SeedHolder, withJunction: true);
        // Not joined to anything: no way passes through it.
        await PlantSeed.AddDeviceAsync(client, SeedHolder, GatewayId, GatewayName, DeviceTypes.Automation,
            new PlantItemGeometryDto(GatewayId, X: 900, Y: 150, Width: 100, Height: 50, Rotation: 0));
        var mission = await LoadAMissionOnTheDraggedDeviceAsync(client, loadUnitId: 0);
        Stage($"mission {mission.MissionId} on {PlantSeed.DraggedName}, {GatewayName} isolated");

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await ChooseTheMissionAndTheUntouchedDeviceAsync(editor, mission);

        await editor.ClickAsync("#drawio-mission-destination-button");
        await editor.ClickAsync("#drawio-mission-destination-gateway-add");
        await editor.FillAsync("#drawio-mission-destination-gateway-search", "Gateway");
        await editor.SelectOptionAsync("#drawio-mission-destination-gateway-target", GatewayId.ToString());
        await editor.ClickAsync("#drawio-mission-destination-confirm");
        await editor.WaitForTextAsync("#drawio-mission-move-outcome",
            $"was NOT sent to {PlantSeed.UntouchedName}: there is no route from {PlantSeed.DraggedName} to {PlantSeed.UntouchedName} " +
            $"passing through {GatewayName}; without the gateway there is one. It still has no destination.");
        Stage("refused, the gateway named");

        var refused = await client.GetMissionAsync(mission.MissionId);
        Assert.NotNull(refused);
        Assert.True(refused.DestinationItemId is null || refused.DestinationItemId == Guid.Empty, $"The destination changed: {refused.DestinationItemId}.");
        Assert.True(refused.GatewayItemId is null || refused.GatewayItemId == Guid.Empty, $"The gateway was written: {refused.GatewayItemId}.");

        await editor.ClickAsync("#drawio-mission-destination-clear-gateway");
        await editor.WaitForTextAsync("#drawio-mission-move-outcome",
            $"is now bound for {PlantSeed.UntouchedName}, with no gateway: the host took it.");
        Stage("set with the gateway cleared");

        // On the host, Clear and Keep would leave the same mission here (the blocking gateway was only
        // proposed, never written): that the page sent Clear is pinned by the bUnit test
        // ARouteTheGatewayBlocksOffersToClearTheGatewayAndThatSetsTheDestination, which reads the request.
        var set = await client.GetMissionAsync(mission.MissionId);
        Assert.NotNull(set);
        Assert.Equal(PlantSeed.UntouchedId, set.DestinationItemId);
        Assert.True(set.GatewayItemId is null || set.GatewayItemId == Guid.Empty, $"The gateway is still there: {set.GatewayItemId}.");
    }

    /// <summary>
    /// Item 54, decision 5 of the owner of 13/10: towards a relocator - the untouched device, which the
    /// warehouse names as its relocator - the page asks "Deposit it, or only bring it there?", and
    /// "only bring it there" ARRIVES ON THE HOST with the change: read back with the client.
    /// </summary>
    [Fact]
    public async Task ARelocatorAsTheDestinationAsksWhetherToDepositAndTheChoiceReachesTheHost()
    {
        output.WriteLine(fixture.Host.Declaration);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        void Stage(string reached) => output.WriteLine($"STAGE {clock.Elapsed.TotalSeconds,6:0.00} s: {reached}");
        using var client = fixture.CreateClient();
        var mission = await SeedAWarehouseRelocatedByTheUntouchedDeviceAsync(client, loadUnitId: 0);
        Stage($"mission {mission.MissionId} on {PlantSeed.DraggedName}, {StorageName} relocated by {PlantSeed.UntouchedName}");

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await ChooseTheMissionAndTheUntouchedDeviceAsync(editor, mission);

        await editor.ClickAsync("#drawio-mission-destination-button");
        await editor.WaitForTextAsync("#drawio-mission-destination-relocator-question", "Deposit it, or only bring it there?");
        await editor.ClickAsync("#drawio-mission-destination-relocator-stay");
        await editor.WaitForTextAsync("#drawio-mission-destination-question",
            $"Send the mission {mission.MissionId} to {PlantSeed.UntouchedName}? It stays at {PlantSeed.UntouchedName}: nothing else is done with it.");
        await editor.ClickAsync("#drawio-mission-destination-confirm");
        await editor.WaitForTextAsync("#drawio-mission-move-outcome", $"is now bound for {PlantSeed.UntouchedName}, where it stays: the host took it.");
        Stage("only bring it there, taken by the host");

        var set = await client.GetMissionAsync(mission.MissionId);
        Assert.NotNull(set);
        Assert.Equal((PlantSeed.UntouchedId, MissionRelocatorArrival.Stay), (set.DestinationItemId, set.RelocatorArrival));
        await editor.WaitForTextAsync(
            $"#drawio-mission-list-table tr[data-mission-id='{mission.MissionId}'] td[data-column='RelocatorArrival']", "Stay");
    }

    /// <summary>
    /// Item 54, decision 6 and answer 6: "A cell I choose" reads the cells the warehouse system proposes
    /// - through the host, from the fake warehouse - and the one picked from the list is reserved by the
    /// warehouse system and becomes the mission's: the destination is its warehouse and the door is its
    /// own, read back with the client, and the reservation went out with that door.
    /// </summary>
    [Fact]
    public async Task ACellPickedFromTheWarehouseSystemsListIsReservedAndTheMissionGoesThere()
    {
        output.WriteLine(fixture.Host.Declaration);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        void Stage(string reached) => output.WriteLine($"STAGE {clock.Elapsed.TotalSeconds,6:0.00} s: {reached}");
        using var client = fixture.CreateClient();
        var mission = await SeedAWarehouseRelocatedByTheUntouchedDeviceAsync(client, DrawioMissionsFixture.LoadUnitId);
        TheWarehouseKnowsTheCellsAndReserves(ListedDoor, ListedCell);
        var bodiesBefore = fixture.Warehouse.RequestBodies.Count;
        Stage($"mission {mission.MissionId} with load unit {mission.LoadUnitCode} on {PlantSeed.DraggedName}");

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await ChooseTheMissionAndTheStorageAsync(editor, mission);

        await editor.ClickAsync("#drawio-mission-destination-button");
        await editor.ClickAsync("#drawio-mission-destination-cell-precise");
        await editor.WaitForTextAsync("#drawio-mission-destination-cell-listed",
            $"The warehouse system proposes 1 cell for {DrawioMissionsFixture.LoadUnitCode} in {StorageName}");
        Stage("the warehouse system's cells listed (the unmapped one left out by the WCS)");
        await editor.SelectOptionAsync("#drawio-mission-destination-cell-list", ListedDoor);
        await editor.WaitForTextAsync("#drawio-mission-destination-question",
            $"Send {DrawioMissionsFixture.LoadUnitCode} into the cell {ListedDoor} of {StorageName}?");
        await editor.ClickAsync("#drawio-mission-destination-confirm");
        await editor.WaitForTextAsync("#drawio-mission-move-outcome",
            $"{DrawioMissionsFixture.LoadUnitCode} is now bound for the cell {ListedDoor} in {StorageName}: the host took it.");
        Stage("the cell reserved, the change taken by the host");

        await TheMissionIsBoundForTheCellAsync(client, mission, ListedDoor, bodiesBefore);
    }

    /// <summary>
    /// Item 54, decision 6: a cell that is not in the list is written by its door A code in the field,
    /// and it is reserved and becomes the mission's as one picked from the list.
    /// </summary>
    [Fact]
    public async Task ACellWrittenByItsCodeIsReservedAndTheMissionGoesThere()
    {
        output.WriteLine(fixture.Host.Declaration);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        void Stage(string reached) => output.WriteLine($"STAGE {clock.Elapsed.TotalSeconds,6:0.00} s: {reached}");
        using var client = fixture.CreateClient();
        var mission = await SeedAWarehouseRelocatedByTheUntouchedDeviceAsync(client, DrawioMissionsFixture.LoadUnitId);
        TheWarehouseKnowsTheCellsAndReserves(WrittenDoor, WrittenCell);
        var bodiesBefore = fixture.Warehouse.RequestBodies.Count;
        Stage($"mission {mission.MissionId} with load unit {mission.LoadUnitCode} on {PlantSeed.DraggedName}");

        await using var editor = await DrawioEditorPage.OpenAsync(fixture.Browser, fixture.Host);
        await editor.WaitForDeviceAsync(PlantSeed.DraggedName);
        await ChooseTheMissionAndTheStorageAsync(editor, mission);

        await editor.ClickAsync("#drawio-mission-destination-button");
        await editor.ClickAsync("#drawio-mission-destination-cell-precise");
        await editor.WaitForTextAsync("#drawio-mission-destination-cell-listed", "The warehouse system proposes 1 cell");
        await editor.FillAsync("#drawio-mission-destination-cell-code", WrittenDoor);
        await editor.WaitForTextAsync("#drawio-mission-destination-question",
            $"Send {DrawioMissionsFixture.LoadUnitCode} into the cell {WrittenDoor} of {StorageName}?");
        await editor.ClickAsync("#drawio-mission-destination-confirm");
        await editor.WaitForTextAsync("#drawio-mission-move-outcome",
            $"{DrawioMissionsFixture.LoadUnitCode} is now bound for the cell {WrittenDoor} in {StorageName}: the host took it.");
        Stage("the written cell reserved, the change taken by the host");

        await TheMissionIsBoundForTheCellAsync(client, mission, WrittenDoor, bodiesBefore);
    }

    /// <summary>The warehouse the item 54 tests add, relocated by the untouched device, with two doors A mapped in the WCS.</summary>
    private static readonly Guid StorageId = new("a1b2c3d4-0000-4000-8000-000000000010");
    private const string StorageName = "StorageDevice";

    /// <summary>The cell the warehouse system lists (mapped), and the one written by hand (mapped, not listed).</summary>
    private const string ListedDoor = "S1A";
    private const decimal ListedCell = 900501m;
    private const string WrittenDoor = "S3A";
    private const decimal WrittenCell = 900503m;

    /// <summary>A cell the warehouse system lists too, whose door the WCS does not map: the host leaves it out of the list.</summary>
    private const string UnmappedDoor = "S2A";

    /// <summary>The warehouse system's identifier of the warehouse of the cells.</summary>
    private const decimal WarehouseOfTheCells = 7m;

    /// <summary>
    /// The plant of the item 54 tests: the seed with its junction, a Storage device that names the
    /// untouched device as its relocator (so the untouched device is a relocator) joined to it, its two
    /// doors A in the WCS's door map; then the mission on the dragged
    /// device, with the engine left stopped.
    /// </summary>
    private async Task<MissionDto> SeedAWarehouseRelocatedByTheUntouchedDeviceAsync(WcsClient client, decimal loadUnitId)
    {
        await PlantSeed.ResetAsync(client, SeedHolder, withJunction: true);
        await PlantSeed.AddDeviceAsync(client, SeedHolder, StorageId, StorageName, DeviceTypes.Storage,
            new PlantItemGeometryDto(StorageId, X: 900, Y: 420, Width: 100, Height: 50, Rotation: 0));
        var storage = await client.GetItemConfigurationAsync(StorageId)
                      ?? throw new InvalidOperationException($"The host does not have the {StorageName} just added.");
        storage.RelocateItemId = PlantSeed.UntouchedId;
        await PlantSeed.SaveConfigurationAsync(client, SeedHolder, storage);
        await PlantSeed.AddJunctionAsync(client, SeedHolder, PlantSeed.UntouchedId, StorageId);
        await PlantSeed.ReplaceDoorMappingsAsync(client, SeedHolder,
        [
            new DoorMappingDto(0, StorageId, ListedDoor, ListedCell, "A", 0, false),
            new DoorMappingDto(0, StorageId, WrittenDoor, WrittenCell, "A", 0, false),
        ]);
        return await LoadAMissionOnTheDraggedDeviceAsync(client, loadUnitId);
    }

    /// <summary>
    /// What the fake warehouse answers for the precise cell (item 54), in the boundary's own shape: the
    /// two cells of the door map described (their warehouse, their doors) with their field addresses, the
    /// list of the candidates - the listed cell and one the WCS does not map - and the reservation of
    /// <paramref name="door"/>. A cell's identifier is written by the engine with the scale it has, so
    /// both writings are answered.
    /// </summary>
    private void TheWarehouseKnowsTheCellsAndReserves(string door, decimal cellId)
    {
        const string boundary = "/api/wcs/warehouse";
        foreach (var (cell, cellDoor) in new[] { (ListedCell, ListedDoor), (WrittenCell, WrittenDoor) })
        {
            foreach (var written in new[] { cell.ToString("0", System.Globalization.CultureInfo.InvariantCulture), cell.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) })
            {
                fixture.Warehouse.Answer("GET", $"{boundary}/cells/{written}", System.Text.Json.JsonSerializer.Serialize(new
                {
                    cellId = cell, areaId = 1m, warehouseId = WarehouseOfTheCells, depth = 1, inverted = false, locked = 0,
                    allowedLoadUnitTypeId = (decimal?)null, groupId = (decimal?)null, groupOrder = 0,
                    doorCodeA = cellDoor, doorCodeB = "", doorCount = 1, multiDepthStorage = false,
                }));
                fixture.Warehouse.Answer("GET", $"{boundary}/cells/{written}/partitions", System.Text.Json.JsonSerializer.Serialize(new
                {
                    cellId = cell, asiDoorA = "S", subitemDoorA = cellDoor, asiDoorB = "", subitemDoorB = "", partitions = Array.Empty<object>(),
                }));
            }
        }

        // The automatic areas of the warehouse the search goes through (LocalQueries.GetAutomaticAreasByWarehouseId).
        foreach (var written in new[] { "7", "7.0" })
            fixture.Warehouse.Answer("GET", $"{boundary}/warehouses/{written}/topology", System.Text.Json.JsonSerializer.Serialize(new
            {
                warehouseId = WarehouseOfTheCells,
                automaticAreaIds = new[] { 1m },
                cellToArea = new Dictionary<string, decimal> { ["900501"] = 1m, ["900502"] = 1m, ["900503"] = 1m },
            }));

        fixture.Warehouse.Answer("POST", $"{boundary}/reservations/candidates", System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new { cellId = ListedCell, doorCode = ListedDoor, areaId = 1m, areaDescription = "Automatic", column = 3, floor = 1, description = (string?)null, loadUnitsInCell = 0 },
            new { cellId = 900502m, doorCode = UnmappedDoor, areaId = 1m, areaDescription = "Automatic", column = 3, floor = 2, description = (string?)null, loadUnitsInCell = 0 },
        }));
        fixture.Warehouse.Answer("POST", $"{boundary}/reservations/cell", System.Text.Json.JsonSerializer.Serialize(new
        {
            outcome = 0,
            reservation = new { missionId = Guid.Empty, outcome = 0, cellId, doorCode = door, partition = (string?)null, storageDepth = 0, needsCompaction = false },
            reason = (string?)null,
        }));
    }

    /// <summary>
    /// The mission as the host has it: bound for the warehouse of the cell, with its door; and the
    /// reservation went out with it - the one request of this test, after the <paramref name="bodiesBefore"/>
    /// bodies of those before it on the same fake.
    /// </summary>
    private async Task TheMissionIsBoundForTheCellAsync(WcsClient client, MissionDto mission, string door, int bodiesBefore)
    {
        var set = await client.GetMissionAsync(mission.MissionId);
        Assert.NotNull(set);
        Assert.Equal((StorageId, door), (set.DestinationItemId, set.StorageDoorCode));
        var reserved = Assert.Single(fixture.Warehouse.RequestBodies.Skip(bodiesBefore), request => request.Path == "/api/wcs/warehouse/reservations/cell");
        Assert.Contains($"\"DoorCode\":\"{door}\"", reserved.Body);
        Assert.Equal(NavigatorStatusTypes.Stop, (await client.GetStatusAsync()).NavStatus);
    }

    /// <summary>Opens the list, chooses the mission's row and picks the warehouse as the target.</summary>
    private static async Task ChooseTheMissionAndTheStorageAsync(DrawioEditorPage editor, MissionDto mission)
    {
        await editor.ClickAsync("#drawio-mission-list-toggle");
        var row = $"#drawio-mission-list-table tr[data-mission-id='{mission.MissionId}']";
        await editor.WaitForTextAsync($"{row} td[data-column='PositionItemId']", PlantSeed.DraggedName);
        await editor.ClickAsync($"{row} td[data-column='PositionItemId']");
        await editor.WaitForAttributeAsync(row, "aria-selected", "true");
        await editor.FillAsync("#drawio-mission-move-search", "Storage");
        await editor.SelectOptionAsync("#drawio-mission-move-target", StorageId.ToString());
        await editor.WaitForTextAsync("#drawio-mission-move-target-chosen", $"Target: {StorageName}");
    }

    /// <summary>Opens the list, chooses the mission's row and picks the untouched device as the target.</summary>
    private static async Task ChooseTheMissionAndTheUntouchedDeviceAsync(DrawioEditorPage editor, MissionDto mission)
    {
        await editor.ClickAsync("#drawio-mission-list-toggle");
        var row = $"#drawio-mission-list-table tr[data-mission-id='{mission.MissionId}']";
        await editor.WaitForTextAsync($"{row} td[data-column='PositionItemId']", PlantSeed.DraggedName);
        await editor.ClickAsync($"{row} td[data-column='PositionItemId']");
        await editor.WaitForAttributeAsync(row, "aria-selected", "true");
        await editor.FillAsync("#drawio-mission-move-search", "Untouched");
        await editor.SelectOptionAsync("#drawio-mission-move-target", PlantSeed.UntouchedId.ToString());
        await editor.WaitForTextAsync("#drawio-mission-move-target-chosen", $"Target: {PlantSeed.UntouchedName}");
    }

    /// <summary>
    /// Starts the engine, loads a mission on the dragged device - with the load unit given, or none -
    /// stops the engine again whatever happened, and hands the mission back as the host has it.
    /// </summary>
    private async Task<MissionDto> LoadAMissionOnTheDraggedDeviceAsync(WcsClient client, decimal loadUnitId)
    {
        MissionDto mission;
        try
        {
            mission = await LoadAMissionOnAsync(client, PlantSeed.DraggedId, loadUnitId);
        }
        finally
        {
            await TestHostEngine.LeaveStoppedAsync(client, output);
        }

        Assert.Equal(PlantSeed.DraggedId, (await client.GetMissionAsync(mission.MissionId))?.PositionItemId);
        return mission;
    }

    /// <summary>Starts the engine, loads a mission on <paramref name="itemId"/>, and hands it back as the host has it.</summary>
    private static async Task<MissionDto> LoadAMissionOnAsync(WcsClient client, Guid itemId, decimal loadUnitId)
    {
        var run = await client.EnqueueCommandAsync(new CommandRequest((int)CommandTypes.SetNavigatorRun));
        if (!await TestHostEngine.WithinAsync(async () => (await client.GetStatusAsync()).NavStatus == NavigatorStatusTypes.Run))
            throw new InvalidOperationException(
                $"The test host's engine did not reach Run within {TestHostEngine.Ceiling.TotalSeconds:0} seconds (command {run.Id}: {await DescribeAsync(client, run.Id)}).");

        var load = await client.EnqueueCommandAsync(new CommandRequest((int)CommandTypes.LoadLu, itemId,
            LoadUnitId: loadUnitId == 0 ? null : loadUnitId));
        MissionDto? loaded = null;
        if (!await TestHostEngine.WithinAsync(async () => (loaded = (await client.GetMissionsAsync(itemId: itemId)).FirstOrDefault()) is not null))
            throw new InvalidOperationException(
                $"No mission appeared on the device within {TestHostEngine.Ceiling.TotalSeconds:0} seconds (LoadLu command {load.Id}: {await DescribeAsync(client, load.Id)}).");

        return loaded!;
    }

    private static async Task<string> DescribeAsync(WcsClient client, long commandId) =>
        await client.GetCommandAsync(commandId) is { } command
            ? $"status {command.Status}, \"{command.ErrorDescription}\""
            : "not found";
}
