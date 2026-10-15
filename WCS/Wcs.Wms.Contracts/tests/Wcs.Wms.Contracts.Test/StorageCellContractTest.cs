using System.Text.Json;
using Wcs.Wms.Contracts;
using Xunit;

namespace Wcs.Wms.Contracts.Test;

/// <summary>
/// Item 54, task 5: the shape on the wire of the candidate cells and of the reservation of a precise
/// cell, and the fake warehouse that answers them in the engine's tests.
/// </summary>
public class StorageCellContractTest
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static readonly Guid Mission = Guid.Parse("aaaaaaaa-1111-2222-3333-444444444444");

    private static StorageReservation Cell6 =>
        new(Guid.Empty, StorageReservationOutcome.CellFound, 6m, "3B02.0363.0001", "0000", 1200, false);

    [Fact]
    public void TheRequestsRoundTripThroughJson()
    {
        var candidates = new StorageCellCandidatesRequest(4711m, 3m, [10m, 11m], [369m],
            [Guid.Parse("bbbbbbbb-1111-2222-3333-444444444444")], StorageCellSearchMode.FreeAutomaticOperator);
        var reserve = new ReserveStorageCellRequest(Mission, 4711m, "3B02.0363.0001", "WCS", "KazamControl.Process");

        var candidatesCopy = JsonSerializer.Deserialize<StorageCellCandidatesRequest>(JsonSerializer.Serialize(candidates))!;
        var reserveCopy = JsonSerializer.Deserialize<ReserveStorageCellRequest>(JsonSerializer.Serialize(reserve));

        Assert.Equal(4711m, candidatesCopy.LoadUnitId);
        Assert.Equal(3m, candidatesCopy.WarehouseId);
        Assert.Equal([10m, 11m], candidatesCopy.SearchAreaIds);
        Assert.Equal([369m], candidatesCopy.ExcludedCellIds);
        Assert.Equal([Guid.Parse("bbbbbbbb-1111-2222-3333-444444444444")], candidatesCopy.ReachableDeviceIds);
        Assert.Equal(StorageCellSearchMode.FreeAutomaticOperator, candidatesCopy.CellSearchMode);
        Assert.Equal(reserve, reserveCopy);
    }

    /// <summary>The limit is the owner's answer 6 of 13/10: the first 20 of the search of every day.</summary>
    [Fact]
    public void TheCandidatesAreTheFirstTwenty() => Assert.Equal(20, StorageCellCandidatesRequest.Limit);

    /// <summary>
    /// The outcomes travel as their written numbers: a reordering of the names would change the meaning
    /// of an answer of the warehouse without changing its shape.
    /// </summary>
    [Fact]
    public void TheReservationOutcomesTravelAsTheirWrittenNumbers()
    {
        var result = JsonSerializer.Deserialize<StorageCellReservationResult>(
            """{"outcome":2,"reason":"porta B"}""", Web);

        Assert.Equal(new StorageCellReservationResult(StorageCellReservationOutcome.NotADoorA, null, "porta B"), result);
        Assert.Equal(0, (int)StorageCellReservationOutcome.Reserved);
        Assert.Equal(1, (int)StorageCellReservationOutcome.UnknownDoor);
        Assert.Equal(2, (int)StorageCellReservationOutcome.NotADoorA);
        Assert.Equal(3, (int)StorageCellReservationOutcome.CellNotAvailable);
        Assert.Equal(4, (int)StorageCellReservationOutcome.UnknownLoadUnit);
        Assert.Equal(0, (int)LoadUnitCellReleaseOutcome.Released);
        Assert.Equal(1, (int)LoadUnitCellReleaseOutcome.NotHeld);
    }

    /// <summary>
    /// A missing outcome does not read as 0, that is «reserved»: a 200 with an empty body would make the
    /// engine send the load unit to a cell nobody reserved.
    /// </summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("""{"reason":"x"}""")]
    public void AReservationResultWithoutItsOutcomeDoesNotRead(string json) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<StorageCellReservationResult>(json, Web));

    [Fact]
    public async Task TheFakeAnswersTheFirstTwentyCandidatesOfTheLoadUnitAndKeepsTheRequest()
    {
        var warehouse = new FakeWarehouse();
        warehouse.StorageCellCandidates[4711m] =
            [.. Enumerable.Range(1, 25).Select(i => new StorageCellCandidate(i, $"D{i}", 10m, "A", i, 1, null, 0))];
        var request = new StorageCellCandidatesRequest(4711m, 3m, [10m], [], [], StorageCellSearchMode.All);

        var cells = await warehouse.ListStorageCellCandidatesAsync(request);
        var none = await warehouse.ListStorageCellCandidatesAsync(request with { LoadUnitId = 99m });

        Assert.Equal(Enumerable.Range(1, 20).Select(i => (decimal)i), cells.Select(c => c.CellId));
        Assert.Empty(none);
        Assert.Equal([request, request with { LoadUnitId = 99m }], warehouse.StorageCellCandidateRequests);
        Assert.Equal(2, warehouse.Calls.Count(c => c == nameof(IWarehouse.ListStorageCellCandidatesAsync)));
    }

    /// <summary>
    /// A cell the fake can reserve becomes the mission's reservation, as with the warehouse, and the give
    /// back by load unit and cell releases it.
    /// </summary>
    [Fact]
    public async Task TheFakeReservesAKnownDoorForTheMission()
    {
        var warehouse = new FakeWarehouse();
        warehouse.ReservableStorageCells["3B02.0363.0001"] = Cell6;

        var result = await warehouse.ReserveStorageCellAsync(
            new ReserveStorageCellRequest(Mission, 4711m, "3B02.0363.0001", "WCS", "KazamControl.Process"));

        Assert.Equal(StorageCellReservationOutcome.Reserved, result.Outcome);
        Assert.Equal(Cell6 with { MissionId = Mission }, result.Reservation);
        Assert.Null(result.Reason);
        Assert.Equal(Cell6 with { MissionId = Mission }, await warehouse.GetStorageReservationAsync(Mission));
        Assert.Single(warehouse.StorageCellReservationRequests);

        var released = await warehouse.ReleaseLoadUnitCellAsync(
            new ReleaseLoadUnitCellRequest(Guid.NewGuid(), Mission, 4711m, 6m, "KazamControl.Process", "redirected"));

        Assert.Equal(LoadUnitCellReleaseOutcome.Released, released);
        Assert.Null(await warehouse.GetStorageReservationAsync(Mission));
    }

    /// <summary>Without a mission the cell is held by the load unit alone, as the obstacle's reservation.</summary>
    [Fact]
    public async Task TheFakeHoldsACellReservedWithoutAMissionForTheLoadUnit()
    {
        var warehouse = new FakeWarehouse();
        warehouse.ReservableStorageCells["3B02.0363.0001"] = Cell6;

        var result = await warehouse.ReserveStorageCellAsync(
            new ReserveStorageCellRequest(Guid.Empty, 4711m, "3B02.0363.0001", "WCS", "KazamControl.Process"));

        Assert.Equal(StorageCellReservationOutcome.Reserved, result.Outcome);
        Assert.Equal(6m, warehouse.LoadUnitCellsHeld[4711m]);
        Assert.Empty(warehouse.StorageReservationsByMission);
    }

    [Fact]
    public async Task TheFakeRefusesAnUnknownDoorAndHoldsNothing()
    {
        var warehouse = new FakeWarehouse();
        warehouse.ReservableStorageCells["3B02.0363.0001"] = Cell6;

        var result = await warehouse.ReserveStorageCellAsync(
            new ReserveStorageCellRequest(Mission, 4711m, "3B02.0999.0001", "WCS", "KazamControl.Process"));

        Assert.Equal(StorageCellReservationOutcome.UnknownDoor, result.Outcome);
        Assert.Null(result.Reservation);
        Assert.Equal("no cell has the door 3B02.0999.0001", result.Reason);
        Assert.Empty(warehouse.StorageReservationsByMission);
        Assert.Empty(warehouse.LoadUnitCellsHeld);
    }

    /// <summary>A test can answer a refusal of its own (door B, cell not available) with the answer function.</summary>
    [Fact]
    public async Task TheFakeAnswersWhatTheTestSays()
    {
        var warehouse = new FakeWarehouse
        {
            ReserveStorageCellAnswer = _ => new StorageCellReservationResult(StorageCellReservationOutcome.NotADoorA, null, "porta B")
        };
        warehouse.ReservableStorageCells["3B02.0363.0001"] = Cell6;

        var result = await warehouse.ReserveStorageCellAsync(
            new ReserveStorageCellRequest(Mission, 4711m, "3B02.0363.0001", "WCS", "KazamControl.Process"));

        Assert.Equal(new StorageCellReservationResult(StorageCellReservationOutcome.NotADoorA, null, "porta B"), result);
        Assert.Empty(warehouse.StorageReservationsByMission);
    }
}
