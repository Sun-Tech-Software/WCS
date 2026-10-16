using System.Net;
using System.Net.Http.Json;
using Wcs.Wms.Client;
using Wcs.Wms.Contracts;
using Xunit;

namespace Wcs.SampleWms.Test;

/// <summary>
/// The sample WMS called through <see cref="HttpWarehouse"/>, the client the WCS itself uses: what the
/// WCS reads is what these tests read. One server per test, so every test starts from the sample's
/// initial warehouse (six empty cells, pallets 5001-5006 outside).
/// </summary>
public sealed class SampleWarehouseContractTest : IAsyncLifetime
{
    private static readonly Guid StackerCrane = SampleData.StackerCrane;

    private SampleWmsServer _server = null!;
    private HttpWarehouse _wms = null!;

    public async Task InitializeAsync()
    {
        _server = await SampleWmsServer.StartAsync();
        _wms = _server.CreateWarehouseClient();
    }

    public async Task DisposeAsync() => await _server.DisposeAsync();

    [Fact]
    public async Task AWrongKeyIsRefusedWith401()
    {
        var stranger = _server.CreateWarehouseClient("not-the-key");

        var refused = await Assert.ThrowsAsync<WarehouseUnavailableException>(() => stranger.DescribeCellAsync(101));

        Assert.Equal(HttpStatusCode.Unauthorized, Assert.IsType<HttpRequestException>(refused.InnerException).StatusCode);
    }

    /// <summary>
    /// Every one of the 45 operations of the contract answers through the WCS's client, with the
    /// sample's initial warehouse: the routes are mapped, the bodies bind (the client sends some of them
    /// in PascalCase, some in camelCase) and the answers read.
    /// </summary>
    [Fact]
    public async Task EveryOperationOfTheContractAnswers()
    {
        var mission = Guid.NewGuid();

        // Cells.
        Assert.Equal(new CellDescription(101, 10, 1, 1200, false, CellLockState.None, null, null, 0, "A0101", "", 1, false),
            await _wms.DescribeCellAsync(101));
        Assert.Equal(101, Assert.Single(await _wms.DescribeCellsByDoorAsync("A0101")).CellId);
        var map = await _wms.GetCellPartitionMapAsync(101);
        Assert.Equal(("3001", "0101", ""), (map.AsiDoorA, map.SubitemDoorA, map.AsiDoorB));
        Assert.Equal(new CellPartition("0000", -1), Assert.Single(map.Partitions));
        Assert.Empty(await _wms.GetCellContentAsync(101));
        Assert.Equal([10m], await _wms.GetPickingBufferAreasAsync(900));
        Assert.Empty(await _wms.GetCellPositionsAsync(101));
        Assert.Equal(new CellBusyState(false, false, false), await _wms.GetCellBusyStateAsync(101));
        Assert.Equal(1200, (await _wms.GetCellStoringConstraintsAsync(101, CellDoor.A, 0, 0)).TheoreticalStoreDepth);
        var topology = await _wms.GetAreaTopologyAsync(1);
        Assert.Equal([10m], topology.AutomaticAreaIds);
        Assert.Equal(8, topology.CellToArea.Count);
        Assert.False((await _wms.GetAreaDefragmentationVerdictAsync(10, 1)).Needed);
        Assert.Equal(6, (await _wms.GetCellsForEditorAsync(null, automaticOnly: true)).Count);
        // What the editor asks for a Loading device (IN01) and for a Buffer device (OUT01).
        Assert.Equal([800m], (await _wms.GetCellsForEditorAsync(2, automaticOnly: true)).Select(c => c.CellId));
        Assert.Equal([900m], (await _wms.GetCellsForEditorAsync(3, automaticOnly: true)).Select(c => c.CellId));
        Assert.Empty(await _wms.GetConfigurationWarningsAsync(0));
        await _wms.SetCellLockStateAsync("A0302", CellLockState.None);
        await _wms.ClearCellPositionsAsync(106);

        // Load units.
        Assert.Equal(new LoadUnitDescription(5001, "LU005001", 1, 0, 25, 1000, 800, 1200, 300), await _wms.DescribeLoadUnitAsync(5001));
        Assert.Equal(new LoadUnitTypeHandlingMode(1, 0), await _wms.GetLoadUnitTypeHandlingModeAsync(1));
        Assert.Equal(new LoadUnitIdentity(5002), await _wms.ResolveLoadUnitByCodeAsync("LU005002"));
        Assert.False(await _wms.IsLoadUnitStoredAsync("LU005002"));
        Assert.Null(await _wms.LocateLoadUnitAsync(5002));
        Assert.False((await _wms.GetLoadUnitAssignmentStateAsync(5002)).AssignedToNote);
        Assert.Null(await _wms.FindLoadUnitAsync(new FindLoadUnitRequest(5002, null, null, LoadUnitLockFilter.Any, CellLockFilter.Any)));
        Assert.Equal(ReferenceSearchOutcome.NotFound, (await _wms.FindLoadUnitByReferenceAsync(new FindLoadUnitByReferenceRequest("ART-RED", 900))).Outcome);
        var door = await _wms.ChoosePickUpDoorAsync(5002, 101);
        Assert.Equal((CellDoor.A, "A0101", 0), (door.Door, door.DoorCode, door.ObstacleCount));
        Assert.Empty(door.ObstacleLoadUnitIds);

        // Reservations.
        var reservation = await _wms.ReserveStorageAsync(Storage(mission, 5002));
        Assert.Equal(new StorageReservation(mission, StorageReservationOutcome.CellFound, 101, "A0101", "0000", 1200, false), reservation);
        Assert.Equal(reservation, await _wms.GetStorageReservationAsync(mission));
        Assert.Equal(LoadUnitCellReleaseOutcome.NotHeld, await _wms.ReleaseLoadUnitCellAsync(
            new ReleaseLoadUnitCellRequest(Guid.NewGuid(), Guid.Empty, 5002, 102, "test", "nothing held there")));
        Assert.Equal(5, (await _wms.ListStorageCellCandidatesAsync(Candidates(5003))).Count);
        Assert.Equal(StorageCellReservationOutcome.UnknownDoor,
            (await _wms.ReserveStorageCellAsync(new ReserveStorageCellRequest(Guid.Empty, 5003, "Z9999", "ws", "user"))).Outcome);
        Assert.Equal(StorageCellReservationOutcome.UnknownDoor,
            (await _wms.CheckStorageCellAsync(new ReserveStorageCellRequest(Guid.Empty, 5003, "Z9999", "ws", "user")))!.Outcome);
        await _wms.ReleaseStorageReservationAsync(mission);

        // Facts and pending work.
        await _wms.AllocateLoadUnitAsync(new AllocateLoadUnitRequest(5002, 101, "A0101", false, false, 0, 0, "test", Guid.NewGuid()));
        await _wms.ReleaseLoadUnitAsync(new ReleaseLoadUnitRequest(5002, false, "test", Guid.NewGuid()));
        Assert.Empty(await _wms.GetPendingNoteWorkAsync(false));
        await _wms.RejectNoteAssignmentAsync(new RejectNoteAssignmentRequest(7, 5002, "not reachable"));
        Assert.Equal([7m], await _wms.GetPausedNoteAssignmentsAsync([7]));
        Assert.Equal([7m], await _wms.GetMissingNoteAssignmentsAsync([7]));
        await _wms.ReportLoadUnitLeftExtractionQueueAsync(new LoadUnitLeftExtractionQueueRequest(5002, 0));
        Assert.Empty(await _wms.GetPendingExtractionWorkAsync());
        Assert.Equal([3m], await _wms.GetPausedExtractionRowsAsync([3]));
        await _wms.ReportExtractionOutcomesAsync(new ReportExtractionOutcomesRequest(0, "ws", []));
        await _wms.ReportMissionLeftAssignmentUnservedAsync(new MissionLeftAssignmentUnservedRequest(mission, 5002, Guid.NewGuid(), 7, "closed"));
        await _wms.ReportMissionServedAssignmentAsync(new MissionServedAssignmentRequest(mission, 5002, Guid.NewGuid(), "arrived"));
        await _wms.ReportUnservableQueuedExtractionAsync(new UnservableQueuedExtractionRequest(9, 5002, Guid.NewGuid(), "busy"));
        await _wms.ConfirmServedAssignmentsAsync(new ConfirmServedAssignmentsRequest(7, "user"));
        Assert.Equal(new PickedExtractionBecameDepositResult(PickedExtractionReopenOutcome.NothingToReopen, PickedExtractionReopenOutcome.NothingToReopen, null),
            await _wms.ReportPickedExtractionBecameDepositAsync(
                new PickedExtractionBecameDepositRequest(Guid.NewGuid(), mission, 5002, 0, Guid.Empty, 0, "user", "sent to storage")));
    }

    /// <summary>
    /// Item 55: the check of a precise cell answers what the reservation would, and holds nothing: the
    /// cell checked is still a candidate, and the reservation that follows takes it.
    /// </summary>
    [Fact]
    public async Task TheCheckOfAPreciseCellHoldsNothing()
    {
        var door = (await _wms.ListStorageCellCandidatesAsync(Candidates(5003)))[0].DoorCode;
        var request = new ReserveStorageCellRequest(Guid.NewGuid(), 5003, door, "ws", "user");

        var check = await _wms.CheckStorageCellAsync(request);

        Assert.Equal(StorageCellReservationOutcome.Reserved, check!.Outcome);
        Assert.Null(check.Reservation);
        Assert.Contains(await _wms.ListStorageCellCandidatesAsync(Candidates(5003)), c => c.DoorCode == door);
        Assert.Equal(StorageCellReservationOutcome.Reserved, (await _wms.ReserveStorageCellAsync(request)).Outcome);
        await _wms.ReleaseStorageReservationAsync(request.MissionId);
    }

    /// <summary>
    /// A storage reservation belongs to its mission until the mission lets it go; the cell is not
    /// offered to anyone else meanwhile.
    /// </summary>
    [Fact]
    public async Task AReservedCellIsTheMissionsUntilItIsReleased()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        Assert.Equal(101, (await _wms.ReserveStorageAsync(Storage(first, 5001))).CellId);
        Assert.Equal(102, (await _wms.ReserveStorageAsync(Storage(second, 5002))).CellId);
        Assert.Equal([new CellPosition(5001, Reserved: true)], await _wms.GetCellPositionsAsync(101));
        Assert.True((await _wms.GetCellBusyStateAsync(101)).Storing);

        await _wms.ReleaseStorageReservationAsync(first);

        Assert.Null(await _wms.GetStorageReservationAsync(first));
        Assert.Empty(await _wms.GetCellPositionsAsync(101));
        Assert.Equal(101, (await _wms.ReserveStorageAsync(Storage(Guid.NewGuid(), 5003))).CellId);
    }

    /// <summary>
    /// The WCS may send an id as 5002.0 (a decimal with a scale): the journal writes 5002, on a
    /// machine of any culture (it read "5002,0" on an Italian one).
    /// </summary>
    [Fact]
    public async Task TheJournalWritesIdsAsPlainNumbers()
    {
        var mission = Guid.NewGuid();

        await _wms.ReserveStorageAsync(Storage(mission, 5002.0m));

        Assert.Equal($"cell 101 (door A0101) reserved for load unit 5002, mission {mission}",
            _server.Warehouse.Snapshot().Journal[^1].What);
    }

    [Fact]
    public async Task NoFreeCellIsAnOutcomeNotAFailure()
    {
        var request = Storage(Guid.NewGuid(), 5001) with { ExcludedCellIds = [101, 102, 103, 104, 105, 106] };

        var reservation = await _wms.ReserveStorageAsync(request);

        Assert.Equal(StorageReservationOutcome.NoFreeCells, reservation.Outcome);
        Assert.Equal(0, reservation.CellId);
    }

    /// <summary>
    /// The WCS asks for a cell reachable by its crane: a cell is reachable through the plant devices
    /// the WMS stores next to it, so naming another device finds nothing.
    /// </summary>
    [Fact]
    public async Task OnlyCellsReachableByTheNamedDevicesAreReserved()
    {
        var request = Storage(Guid.NewGuid(), 5001) with { ReachableDeviceIds = [Guid.NewGuid()] };

        Assert.Equal(StorageReservationOutcome.NoFreeCells, (await _wms.ReserveStorageAsync(request)).Outcome);
    }

    /// <summary>
    /// A durable queue of the WCS can send the same allocation twice (an answer lost on the network):
    /// the second is answered without being applied again. The same OperationId with a different content
    /// is refused with 409, which the WCS reads as a permanent refusal and parks.
    /// </summary>
    [Fact]
    public async Task TheSameOperationIdIsAppliedOnceAndADifferentContentIs409()
    {
        var operation = Guid.NewGuid();
        var allocation = new AllocateLoadUnitRequest(5001, 101, "A0101", false, false, 0, 0, "test", operation);

        await _wms.AllocateLoadUnitAsync(allocation);
        await _wms.AllocateLoadUnitAsync(new AllocateLoadUnitRequest(5001, 102, "A0102", false, false, 0, 0, "test", Guid.NewGuid()));
        await _wms.AllocateLoadUnitAsync(allocation);

        // The repetition did not move the pallet back to 101.
        Assert.Equal(new LoadUnitLocation(5001, 102, 1200), await _wms.LocateLoadUnitAsync(5001));

        var refused = await Assert.ThrowsAsync<WarehouseUnavailableException>(() =>
            _wms.AllocateLoadUnitAsync(allocation with { CellId = 103, StorageDoorCode = "A0201" }));

        Assert.True(refused.IsPermanentRejection);
        Assert.Equal(HttpStatusCode.Conflict, Assert.IsType<HttpRequestException>(refused.InnerException).StatusCode);
        Assert.Equal(new LoadUnitLocation(5001, 102, 1200), await _wms.LocateLoadUnitAsync(5001));
    }

    /// <summary>
    /// A storage names its cell by the door code: the contract says the WMS reads the cell from
    /// <c>StorageDoorCode</c> and ignores <c>CellId</c> when the code is there. The two disagree here,
    /// and the pallet goes where the door is.
    /// </summary>
    [Fact]
    public async Task AStorageIsKeyedOnTheDoorCodeNotOnTheCellId()
    {
        await _wms.AllocateLoadUnitAsync(new AllocateLoadUnitRequest(5001, 102, "A0101", true, false, 0, 0, "test", Guid.NewGuid()));

        Assert.Equal(new LoadUnitLocation(5001, 101, 1200), await _wms.LocateLoadUnitAsync(5001));
        Assert.Empty(await _wms.GetCellContentAsync(102));
    }

    /// <summary>A door code no cell has is refused with 400 (and parked by the WCS), whatever the CellId says.</summary>
    [Fact]
    public async Task AStorageThroughAnUnknownDoorIsRefused()
    {
        var refused = await Assert.ThrowsAsync<WarehouseUnavailableException>(() =>
            _wms.AllocateLoadUnitAsync(new AllocateLoadUnitRequest(5001, 101, "Z9999", true, false, 0, 0, "test", Guid.NewGuid())));

        Assert.True(refused.IsPermanentRejection);
        Assert.Null(await _wms.LocateLoadUnitAsync(5001));
    }

    /// <summary>
    /// <c>ResetAssignment</c> is about the shipping-note assignment of the load unit, not about cell
    /// reservations: a storage elsewhere does not take away the cell a mission reserved. The WCS gives
    /// that cell back itself (<c>reservations/release-load-unit-cell</c> or <c>reservations/release</c>).
    /// </summary>
    [Fact]
    public async Task ResetAssignmentDoesNotDropAReservationInAnotherCell()
    {
        var mission = Guid.NewGuid();
        Assert.Equal(101, (await _wms.ReserveStorageAsync(Storage(mission, 5002))).CellId);

        await _wms.AllocateLoadUnitAsync(new AllocateLoadUnitRequest(5002, 102, "A0102", true, false, 0, 0, "test", Guid.NewGuid()));

        Assert.Equal(new LoadUnitLocation(5002, 102, 1200), await _wms.LocateLoadUnitAsync(5002));
        Assert.Equal(101, (await _wms.GetStorageReservationAsync(mission))?.CellId);
    }

    /// <summary>
    /// A repetition is the same request, not the same text: an id written <c>5003.0</c> instead of
    /// <c>5003</c>, or a property in camelCase instead of PascalCase, is the same allocation and gets
    /// the first answer, not 409 (which the WCS would read as a permanent refusal and park).
    /// </summary>
    [Fact]
    public async Task TheSameOperationIdWithTheSameValuesWrittenDifferentlyIsARepetition()
    {
        using var client = _server.CreateAdminClient();
        var route = $"{WarehouseRoutes.Prefix}/{WarehouseRoutes.AllocateLoadUnit}";
        const string operation = "6f9619ff-8b86-d011-b42d-00cf4fc964ff";

        using var first = await client.PostAsync(route, new StringContent(
            $$"""{"LoadUnitId":5003,"CellId":103,"StorageDoorCode":"A0201","RequestedByUserId":"test","OperationId":"{{operation}}"}""",
            System.Text.Encoding.UTF8, "application/json"));
        using var camelCase = await client.PostAsync(route, new StringContent(
            $$"""{"loadUnitId":5003,"cellId":103,"storageDoorCode":"A0201","requestedByUserId":"test","operationId":"{{operation}}"}""",
            System.Text.Encoding.UTF8, "application/json"));
        using var otherScale = await client.PostAsync(route, new StringContent(
            $$"""{"LoadUnitId":5003.0,"CellId":103.00,"StorageDoorCode":"A0201","RequestedByUserId":"test","OperationId":"{{operation}}"}""",
            System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK), (first.StatusCode, camelCase.StatusCode, otherScale.StatusCode));
        Assert.Equal(new LoadUnitLocation(5003, 103, 1200), await _wms.LocateLoadUnitAsync(5003));
    }

    /// <summary>
    /// The key is checked before anything else: a request without it is 401 even when its body would
    /// not bind (it used to be 400, because the check ran after the binding).
    /// </summary>
    [Fact]
    public async Task AMissingKeyIs401EvenWithAnUnreadableBody()
    {
        using var anonymous = new HttpClient { BaseAddress = _server.BaseAddress };

        using var response = await anonymous.PostAsync($"{WarehouseRoutes.Prefix}/{WarehouseRoutes.AllocateLoadUnit}",
            new StringContent("not json", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>An allocation that can never be applied is refused with 400: the WCS parks it instead of retrying forever.</summary>
    [Fact]
    public async Task AnAllocationOfAnUnknownLoadUnitIsAPermanentRefusal()
    {
        var refused = await Assert.ThrowsAsync<WarehouseUnavailableException>(() =>
            _wms.AllocateLoadUnitAsync(new AllocateLoadUnitRequest(9999, 101, "A0101", false, false, 0, 0, "test", Guid.NewGuid())));

        Assert.True(refused.IsPermanentRejection);
        Assert.Equal(HttpStatusCode.BadRequest, Assert.IsType<HttpRequestException>(refused.InnerException).StatusCode);
    }

    /// <summary>
    /// An extraction order created with the admin route is pending work until its row is reported
    /// extracted - by row id, with header 0, as the WCS's durable queue reports it - and then the order
    /// is closed and its rows are no longer in progress.
    /// </summary>
    [Fact]
    public async Task AnExtractionOrderIsPendingUntilItsRowIsReportedExtracted()
    {
        await _wms.AllocateLoadUnitAsync(new AllocateLoadUnitRequest(5004, 104, "A0202", false, false, 0, 0, "test", Guid.NewGuid()));
        using var admin = _server.CreateAdminClient();
        using var created = await admin.PostAsJsonAsync("/sample-admin/extraction-orders", new { loadUnitIds = new[] { 5004m } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var pending = Assert.Single(await _wms.GetPendingExtractionWorkAsync());
        Assert.Equal((1m, 900m), (pending.ExtractionHeaderId, pending.PickingCellId));
        Assert.Equal([10m], pending.BufferAreaIds);
        Assert.Equal(new PendingExtractionRow(1, 5004, false, 1), Assert.Single(pending.Rows));
        Assert.True((await _wms.GetCellBusyStateAsync(104)).Extraction);
        Assert.Equal(new LoadUnitSearchResult(5004, 104), await _wms.FindLoadUnitAsync(
            new FindLoadUnitRequest(5004, pending.BufferAreaIds, null, LoadUnitLockFilter.Unlocked, CellLockFilter.FreeForUnloading)));
        Assert.Empty(await _wms.GetPausedExtractionRowsAsync([1]));

        await _wms.ReportExtractionOutcomesAsync(new ReportExtractionOutcomesRequest(0, "ws", [new ExtractionRowOutcome(1, ExtractionRowStatus.Extracted)]));

        Assert.Empty(await _wms.GetPendingExtractionWorkAsync());
        Assert.Equal([1m], await _wms.GetPausedExtractionRowsAsync([1]));
        Assert.True(Assert.Single(_server.Warehouse.Snapshot().ExtractionOrders).Closed);
    }

    /// <summary>The load unit that leaves the automation has no place any more, and is shipped.</summary>
    [Fact]
    public async Task AReleasedLoadUnitIsShippedAndNowhere()
    {
        await _wms.AllocateLoadUnitAsync(new AllocateLoadUnitRequest(5006, 106, "A0302", false, false, 0, 0, "test", Guid.NewGuid()));
        Assert.True(await _wms.IsLoadUnitStoredAsync("LU005006"));

        await _wms.ReleaseLoadUnitAsync(new ReleaseLoadUnitRequest(5006, false, "test", Guid.NewGuid()));

        Assert.Null(await _wms.LocateLoadUnitAsync(5006));
        Assert.False(await _wms.IsLoadUnitStoredAsync("LU005006"));
        Assert.Empty(await _wms.GetCellPositionsAsync(106));
        Assert.True(_server.Warehouse.Snapshot().LoadUnits.Single(l => l.LoadUnitId == 5006).Shipped);
    }

    [Fact]
    public async Task AnExtractionOrderForAnUnknownLoadUnitIsRefused()
    {
        using var admin = _server.CreateAdminClient();

        using var refused = await admin.PostAsJsonAsync("/sample-admin/extraction-orders", new { loadUnitIds = new[] { 9999m } });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Empty(_server.Warehouse.Snapshot().ExtractionOrders);
    }

    /// <summary>The admin routes are the sample's own, and they need the key like the contract.</summary>
    [Fact]
    public async Task TheAdminRoutesNeedTheKey()
    {
        using var anonymous = new HttpClient { BaseAddress = _server.BaseAddress };

        using var response = await anonymous.GetAsync("/sample-admin/state");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static ReserveStorageRequest Storage(Guid missionId, decimal loadUnitId) =>
        new(missionId, loadUnitId, SampleData.WarehouseId, [SampleData.StorageAreaId], [], [StackerCrane],
            StorageAreaSelectionMode.All, StorageCellSearchMode.FreeAutomaticOperator, CellFullnessPreference.Fullest,
            StorageSearchOperatorType.Automatic, AssignPosition: true, VerifyPosition: false, "ws", "user");

    private static StorageCellCandidatesRequest Candidates(decimal loadUnitId) =>
        new(loadUnitId, SampleData.WarehouseId, [SampleData.StorageAreaId], [], [StackerCrane], StorageCellSearchMode.FreeAutomaticOperator);
}
