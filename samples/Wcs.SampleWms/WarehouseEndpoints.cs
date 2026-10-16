using Wcs.Wms.Contracts;

namespace Wcs.SampleWms;

/// <summary>
/// The 45 routes of the WCS -> WMS contract, under <c>/api/wcs/warehouse</c>, each one mapped to the
/// <see cref="SampleWarehouse"/> method that answers it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The routes and the bodies come from <c>Wcs.Wms.Contracts</c></b>: <see cref="WarehouseRoutes"/>
/// for the paths, the records in its <c>Dto</c> folder for requests and answers. A WMS not written in
/// .NET implements the same paths and the same JSON; the rules it must follow are these:
/// </para>
/// <list type="bullet">
/// <item>every request carries the <c>X-Api-Key</c> header (<see cref="ApiKeyFilter"/>): a missing or
/// wrong key is 401, whatever the body;</item>
/// <item>the WCS reads answers case-insensitively, camelCase or PascalCase, and sends some requests in
/// PascalCase and some in camelCase: accept both (ASP.NET Core does by default);</item>
/// <item>enums travel as numbers, ids as JSON numbers, mission ids and operation ids as GUID strings;</item>
/// <item><b>404 means "there is none" only on six readings</b> - <c>reservations/{missionId}</c>,
/// <c>load-units/{id}/location</c>, <c>load-units/find</c>, <c>load-units/{id}/description</c>,
/// <c>load-unit-types/{id}/handling-mode</c>, <c>load-units/by-code/{code}</c>. On every other
/// reading a 404, like any status other than 2xx, a timeout or a body that does not read, is a
/// failure: the WCS gets no answer and does not use one;</item>
/// <item><b>on the writes the WCS retries, every 4xx except 408 and 429 is a permanent refusal - 404
/// included</b>, and the retries stop: <c>load-units/allocate</c> and <c>load-units/release</c> are parked in the
/// WCS's queue with the reason, and hold back the later facts about the same load unit (a parked
/// release also every later allocation) until a person resolves them;
/// <c>reservations/release-load-unit-cell</c> and <c>assignments/picked-extraction-became-deposit</c>
/// are parked for a person; a refused <c>extractions/outcomes</c> row is logged and dropped from the
/// queue, and so is a refused <c>cells/lock</c>. Use a 4xx only for requests that can never succeed
/// (an unknown load unit), never for a temporary problem - a load unit not synchronised yet, a route
/// not mapped yet: answer those with a 5xx (or 408/429), which the WCS retries.</item>
/// </list>
/// </remarks>
public static class WarehouseEndpoints
{
    public static RouteGroupBuilder MapWarehouseContract(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup(WarehouseRoutes.Prefix).WithTags("WCS -> WMS contract");

        MapCells(api);
        MapReservations(api);
        MapLoadUnits(api);
        MapWork(api);
        return api;
    }

    // ---------------------------------------------------------------------------------------------
    // Cells
    // ---------------------------------------------------------------------------------------------

    private static void MapCells(RouteGroupBuilder api)
    {
        // 1. The description of a cell. The WCS reads it on the storing and picking paths (area, lock,
        // doors); an unknown cell is a failure for the WCS, because it only asks about cells it was told about.
        api.MapGet(WarehouseRoutes.Cell, (decimal cellId, SampleWarehouse wms) =>
            wms.DescribeCell(cellId) is { } cell ? Results.Ok(cell) : CellNotFound(cellId));

        // 28. The cells behind a door code (normally one). An unknown door is an empty list, not a 404.
        api.MapGet(WarehouseRoutes.CellsByDoor, (string doorCode, SampleWarehouse wms) => Results.Ok(wms.DescribeCellsByDoor(doorCode)));

        // 27. The field address of the cell's doors and its depth positions: what the WCS sends to the
        // crane to store into the cell or to pick from it.
        api.MapGet(WarehouseRoutes.CellPartitionMap, (decimal cellId, SampleWarehouse wms) =>
            wms.GetCellPartitionMap(cellId) is { } map ? Results.Ok(map) : CellNotFound(cellId));

        // 29. Lock a cell by its door A. The WCS writes "full" (3) when the machine refuses to store in it,
        // and the cell is no longer proposed for storage until someone unlocks it.
        // An unknown door is 404, a permanent refusal: the WCS logs it and does not retry.
        api.MapPost(WarehouseRoutes.CellLock, (SetCellLockStateRequest request, SampleWarehouse wms) =>
            wms.SetCellLockState(request.DoorCode, request.LockState)
                ? Results.Ok()
                : Results.Problem($"No cell has the door A {request.DoorCode}.", statusCode: StatusCodes.Status404NotFound));

        // 30. The load units physically in the cell, without the reserved positions.
        api.MapGet(WarehouseRoutes.CellContent, (decimal cellId, SampleWarehouse wms) => Results.Ok(wms.GetCellContent(cellId)));

        // 31. The storage areas a bay is served from, in order of priority. [] is an answer.
        api.MapGet(WarehouseRoutes.PickingBufferAreas, (decimal cellId, SampleWarehouse wms) => Results.Ok(wms.GetPickingBufferAreas(cellId)));

        // 32. Every position of the cell, with which ones are only reserved.
        api.MapGet(WarehouseRoutes.CellPositions, (decimal cellId, SampleWarehouse wms) => Results.Ok(wms.GetCellPositions(cellId)));

        // 33. The cell is empty on the field: all its positions go.
        api.MapPost(WarehouseRoutes.ClearCellPositions, (ClearCellPositionsRequest request, SampleWarehouse wms) =>
        {
            wms.ClearCellPositions(request.CellId);
            return Results.Ok();
        });

        // 34. Why the cell is busy (extraction, pick-up, storing). A reading sent as POST.
        api.MapPost(WarehouseRoutes.CellBusyState, (CellBusyStateRequest request, SampleWarehouse wms) =>
            Results.Ok(wms.GetCellBusyState(request.CellId, request.LoadUnitIdToExclude)));

        // 35. At what depth a load unit would be stored in the cell; 0 = no room. A reading sent as POST.
        api.MapPost(WarehouseRoutes.CellStoringConstraints, (CellStoringConstraintsRequest request, SampleWarehouse wms) =>
            Results.Ok(wms.GetCellStoringConstraints(request.CellId, request.LoadUnitIdToExclude)));

        // 23. The automatic areas of a warehouse and the area of each cell. Changes rarely.
        api.MapGet(WarehouseRoutes.AreaTopology, (decimal warehouseId, SampleWarehouse wms) => Results.Ok(wms.GetAreaTopology(warehouseId)));

        // 41. Does the area need compacting for this type of load unit? The policy stays in the WMS.
        api.MapGet(WarehouseRoutes.AreaDefragmentation, (decimal areaId, decimal loadUnitTypeId, SampleWarehouse wms) =>
            Results.Ok(wms.GetAreaDefragmentationVerdict(areaId, loadUnitTypeId)));

        // 42. The cell list of the WCS editor (only the editor asks, through the host's /api/v1/wms/cells).
        // For a linked device it asks automaticOnly=true with area type 2 (Loading, ...) or 3 (Buffer, ...).
        api.MapGet(WarehouseRoutes.EditorCells, (short? areaType, bool? automaticOnly, SampleWarehouse wms) =>
            Results.Ok(wms.GetCellsForEditor(areaType, automaticOnly ?? false)));

        // 43. The WMS's own configuration check, shown by the WCS editor. No row = all fine.
        api.MapGet(WarehouseRoutes.ConfigurationCheck, (int? mode, SampleWarehouse wms) => Results.Ok(wms.GetConfigurationWarnings(mode ?? 0)));
    }

    // ---------------------------------------------------------------------------------------------
    // Storage reservations
    // ---------------------------------------------------------------------------------------------

    private static void MapReservations(RouteGroupBuilder api)
    {
        // 2. Choose (and, with AssignPosition, reserve) a storage cell for a load unit. Called when a
        // load unit reaches a device configured to look for a cell (LookWhereAllocateLoadUnit), e.g. the
        // infeed. "No free cell" is the outcome 6 in the body, with 200.
        api.MapPost(WarehouseRoutes.Reservations, (ReserveStorageRequest request, SampleWarehouse wms) =>
            Results.Ok(wms.ReserveStorage(request)));

        // 3. The reservation a mission holds; asking renews it. 404 = none (the WCS reserves again).
        api.MapGet(WarehouseRoutes.Reservation, (Guid missionId, SampleWarehouse wms) =>
            wms.GetStorageReservation(missionId) is { } reservation
                ? Results.Ok(reservation)
                : Results.Problem($"Mission {missionId} holds no reservation.", statusCode: StatusCodes.Status404NotFound));

        // 4. The mission lets its reservation go. Idempotent.
        api.MapPost(WarehouseRoutes.ReleaseReservation, (ReleaseReservationRequest request, SampleWarehouse wms) =>
        {
            wms.ReleaseStorageReservation(request.MissionId);
            return Results.Ok();
        });

        // 5. The WCS gives back a cell it reserved for a load unit. From a durable queue: receipt per OperationId.
        api.MapPost(WarehouseRoutes.ReleaseLoadUnitCell, (ReleaseLoadUnitCellRequest request, SampleWarehouse wms, Receipts receipts) =>
            receipts.Once(request.OperationId, WarehouseRoutes.ReleaseLoadUnitCell, request, () => Results.Ok(wms.ReleaseLoadUnitCell(request))));

        // 6. Candidate cells for the operator to choose from (editor). A reading sent as POST.
        api.MapPost(WarehouseRoutes.StorageCellCandidates, (StorageCellCandidatesRequest request, SampleWarehouse wms) =>
            Results.Ok(wms.ListStorageCellCandidates(request)));

        // 7. Reserve the precise cell the operator chose. Refusals are outcomes in the body.
        api.MapPost(WarehouseRoutes.ReserveStorageCell, (ReserveStorageCellRequest request, SampleWarehouse wms) =>
            Results.Ok(wms.ReserveStorageCell(request)));
        api.MapPost(WarehouseRoutes.CheckStorageCell, (ReserveStorageCellRequest request, SampleWarehouse wms) =>
            Results.Ok(wms.CheckStorageCell(request)));
    }

    // ---------------------------------------------------------------------------------------------
    // Load units
    // ---------------------------------------------------------------------------------------------

    private static void MapLoadUnits(RouteGroupBuilder api)
    {
        // 36. Code, type, handling mode and dimensions: the WCS's most frequent call (command 3002
        // LoadLu asks it before creating the mission). 404 = no such load unit.
        api.MapGet(WarehouseRoutes.LoadUnit, (decimal loadUnitId, SampleWarehouse wms) =>
            wms.DescribeLoadUnit(loadUnitId) is { } description ? Results.Ok(description) : LoadUnitNotFound(loadUnitId));

        // 37. The handling mode of a type of load unit. 404 = no such type.
        api.MapGet(WarehouseRoutes.LoadUnitTypeHandlingMode, (decimal loadUnitTypeId, SampleWarehouse wms) =>
            wms.GetLoadUnitTypeHandlingMode(loadUnitTypeId) is { } mode
                ? Results.Ok(mode)
                : Results.Problem($"Load unit type {loadUnitTypeId} is unknown.", statusCode: StatusCodes.Status404NotFound));

        // 40. The load unit with this label (a code read on the field). 404 = no such code.
        api.MapGet(WarehouseRoutes.LoadUnitByCode, (string code, SampleWarehouse wms) =>
            wms.ResolveLoadUnitByCode(code) is { } identity
                ? Results.Ok(identity)
                : Results.Problem($"No load unit has the code {code}.", statusCode: StatusCodes.Status404NotFound));

        // 39. Is the load unit with this label already stored? true/false in the body.
        api.MapGet(WarehouseRoutes.LoadUnitStored, (string code, SampleWarehouse wms) => Results.Ok(wms.IsLoadUnitStored(code)));

        // 8. Where the load unit physically is. 404 = nowhere.
        api.MapGet(WarehouseRoutes.LoadUnitLocation, (decimal loadUnitId, SampleWarehouse wms) =>
            wms.LocateLoadUnit(loadUnitId) is { } location
                ? Results.Ok(location)
                : Results.Problem($"Load unit {loadUnitId} is not in any cell.", statusCode: StatusCodes.Status404NotFound));

        // 38. Is the load unit committed to a shipping note? Always 200.
        api.MapGet(WarehouseRoutes.LoadUnitAssignmentState, (decimal loadUnitId, SampleWarehouse wms) =>
            Results.Ok(wms.GetLoadUnitAssignmentState(loadUnitId)));

        // 24. Where the load unit is, within limits. 404 = not found within those limits.
        api.MapPost(WarehouseRoutes.FindLoadUnit, (FindLoadUnitRequest request, SampleWarehouse wms) =>
            wms.FindLoadUnit(request) is { } found
                ? Results.Ok(found)
                : Results.Problem($"Load unit {request.LoadUnitId} not found within the limits asked.", statusCode: StatusCodes.Status404NotFound));

        // 25. Which load unit carries this article, reachable from this bay. The outcome is in the body.
        api.MapPost(WarehouseRoutes.FindLoadUnitByReference, (FindLoadUnitByReferenceRequest request, SampleWarehouse wms) =>
            Results.Ok(wms.FindLoadUnitByReference(request)));

        // 26. From which door to pick the load unit, and the load units in front of it.
        api.MapPost(WarehouseRoutes.PickUpDoor, (ChoosePickUpDoorRequest request, SampleWarehouse wms) =>
            wms.ChoosePickUpDoor(request.LoadUnitId, request.CellId) is { } choice ? Results.Ok(choice) : CellNotFound(request.CellId));

        // 21. Fact: the load unit is now physically in this cell (stored, or arrived on a device linked
        // to a cell). From a durable queue: receipt per OperationId. 400 = can never be applied. With a
        // StorageDoorCode the cell is the door's, and CellId is ignored.
        api.MapPost(WarehouseRoutes.AllocateLoadUnit, (AllocateLoadUnitRequest request, SampleWarehouse wms, Receipts receipts) =>
            receipts.Once(request.OperationId, WarehouseRoutes.AllocateLoadUnit, request, () =>
                wms.AllocateLoadUnit(request) is { } refusal ? Results.Problem(refusal, statusCode: StatusCodes.Status400BadRequest) : Results.Ok()));

        // 22. Fact: the load unit left the automation. From a durable queue: receipt per OperationId.
        api.MapPost(WarehouseRoutes.ReleaseLoadUnit, (ReleaseLoadUnitRequest request, SampleWarehouse wms, Receipts receipts) =>
            receipts.Once(request.OperationId, WarehouseRoutes.ReleaseLoadUnit, request, () =>
                wms.ReleaseLoadUnit(request) is { } refusal ? Results.Problem(refusal, statusCode: StatusCodes.Status400BadRequest) : Results.Ok()));
    }

    // ---------------------------------------------------------------------------------------------
    // Work the WCS pulls, and the facts it reports
    // ---------------------------------------------------------------------------------------------

    private static void MapWork(RouteGroupBuilder api)
    {
        // 13. Extraction orders ready for the automation, asked on every turn of the WCS master loop.
        api.MapGet(WarehouseRoutes.PendingExtractionWork, (SampleWarehouse wms) => Results.Ok(wms.GetPendingExtractionWork()));

        // 15. What happened to extraction rows: the rows of one order after a turn of the master loop,
        // or (header 0, from a durable queue) the rows a mission has just picked. The WMS closes the
        // order itself.
        api.MapPost(WarehouseRoutes.ExtractionOutcomes, (ReportExtractionOutcomesRequest request, SampleWarehouse wms) =>
        {
            wms.ReportExtractionOutcomes(request);
            return Results.Ok();
        });

        // 14. Which of these extraction rows are no longer in progress (the WCS drops them from its queue).
        api.MapPost(WarehouseRoutes.PausedExtractionRows, (PausedAmongRequest request, SampleWarehouse wms) =>
            Results.Ok(wms.GetPausedExtractionRows(request.Ids)));

        // 12. Fact: the load unit left the WCS's extraction queue.
        api.MapPost(WarehouseRoutes.LoadUnitLeftExtractionQueue, (LoadUnitLeftExtractionQueueRequest request, SampleWarehouse wms) =>
        {
            wms.ReportLoadUnitLeftExtractionQueue(request);
            return Results.Ok();
        });

        // 20. Fact: an extraction already picked became a deposit. From a durable queue: receipt per OperationId.
        api.MapPost(WarehouseRoutes.PickedExtractionBecameDeposit,
            (PickedExtractionBecameDepositRequest request, SampleWarehouse wms, Receipts receipts) =>
                receipts.Once(request.OperationId, WarehouseRoutes.PickedExtractionBecameDeposit, request, () =>
                    Results.Ok(wms.ReportPickedExtractionBecameDeposit(request))));

        // 9. Shipping-note assignments ready for the automation, asked on every turn. Empty in the sample.
        api.MapGet(WarehouseRoutes.PendingNoteWork, (bool? limitOneShippingNotePerBuffer, SampleWarehouse wms) =>
            Results.Ok(wms.GetPendingNoteWork(limitOneShippingNotePerBuffer ?? false)));

        // 11. Which of these note assignments are no longer in progress.
        api.MapPost(WarehouseRoutes.PausedNoteAssignments, (PausedAmongRequest request, SampleWarehouse wms) =>
            Results.Ok(wms.GetPausedNoteAssignments(request.Ids)));
        api.MapPost(WarehouseRoutes.MissingNoteAssignments, (PausedAmongRequest request, SampleWarehouse wms) =>
            Results.Ok(wms.GetMissingNoteAssignments(request.Ids)));

        // 10, 16, 17, 18, 19. Facts about shipping-note assignments: the WCS rejected one, a mission left
        // it unserved, a mission served it, a queued row cannot be served, the served rows are confirmed.
        api.MapPost(WarehouseRoutes.RejectNoteAssignment, (RejectNoteAssignmentRequest request, SampleWarehouse wms) =>
            Fact(wms, WarehouseRoutes.RejectNoteAssignment, $"assignment {request.AssignmentId} rejected: {request.Reason}"));

        api.MapPost(WarehouseRoutes.MissionLeftAssignmentUnserved, (MissionLeftAssignmentUnservedRequest request, SampleWarehouse wms) =>
            Fact(wms, WarehouseRoutes.MissionLeftAssignmentUnserved,
                $"mission {request.MissionId} left assignment {request.AssignmentId} unserved: {request.Reason}"));

        api.MapPost(WarehouseRoutes.MissionServedAssignment, (MissionServedAssignmentRequest request, SampleWarehouse wms) =>
            Fact(wms, WarehouseRoutes.MissionServedAssignment,
                $"mission {request.MissionId} served assignment {request.AssignmentGuid}: {request.Reason}"));

        api.MapPost(WarehouseRoutes.UnservableQueuedExtraction, (UnservableQueuedExtractionRequest request, SampleWarehouse wms) =>
            Fact(wms, WarehouseRoutes.UnservableQueuedExtraction,
                $"queued extraction {request.ExtractionQueueId} of load unit {request.LoadUnitId} cannot be served: {request.Reason}"));

        api.MapPost(WarehouseRoutes.ConfirmServedAssignments, (ConfirmServedAssignmentsRequest request, SampleWarehouse wms) =>
            Fact(wms, WarehouseRoutes.ConfirmServedAssignments, $"assignment {request.AssignmentId} confirmed by {request.UserId}"));
    }

    private static IResult Fact(SampleWarehouse wms, string route, FormattableString what)
    {
        wms.RecordFact(route, what);
        return Results.Ok();
    }

    private static IResult CellNotFound(decimal cellId) =>
        Results.Problem($"Cell {cellId} is unknown.", statusCode: StatusCodes.Status404NotFound);

    private static IResult LoadUnitNotFound(decimal loadUnitId) =>
        Results.Problem($"Load unit {loadUnitId} is unknown.", statusCode: StatusCodes.Status404NotFound);
}
