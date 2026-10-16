# Reference: the WCS -> WMS routes

The 45 routes the WCS calls on your WMS. They are the 45 methods of
[`IWarehouse`](../../WCS/Wcs.Wms.Contracts/src/IWarehouse.cs); the paths are the constants of
[`WarehouseRoutes`](../../WCS/Wcs.Wms.Contracts/src/WarehouseRoutes.cs) and the bodies are the records
of the [`Dto`](../../WCS/Wcs.Wms.Contracts/src/Dto) folder. The client that sends them is
[`HttpWarehouse`](../../WCS/Wcs.Wms.Client/src/HttpWarehouse.cs). How to read the columns, and the
rules every route follows, are in [the WMS contract](../integration/wms-contract.md); examples of
every request and answer are there too.

Every path is relative to `{Wms:ApiAddress}/api/wcs/warehouse/`, and every request carries
`X-Api-Key: {Wms:ApiKey}`.

- **Body**: the request record, and the casing of its property names on the wire (*Pascal*:
  `{"LoadUnitId":5001}`, *camel*: `{"loadUnitId":5001}`). Answers are read case-insensitively.
- **404**: what a 404 means. *none* = "there is none", the WCS reads it as an empty answer;
  *failure* = the WCS treats it like any other error ([`WarehouseUnavailableException`](../../WCS/Wcs.Wms.Contracts/src/WarehouseUnavailableException.cs)).
- **Queue**: *durable* = sent from a durable queue of the WCS, at least once, with an `OperationId`
  the WMS keeps a receipt for; *durable, no id* = durable queue, idempotent by content.

## Cells

| # | Method | Path | `IWarehouse` method | Body | Answer | 404 | Called when |
|---|---|---|---|---|---|---|---|
| 1 | GET | `cells/{cellId}` | `DescribeCellAsync` | - | `CellDescription` | failure | a mission stores into or picks from a cell; a load unit arrives on a device linked to a cell |
| 27 | GET | `cells/{cellId}/partitions` | `GetCellPartitionMapAsync` | - | `CellPartitionMap` | failure | before a store or a pick: the field address of the cell's door (ASI, subitem, partition) |
| 28 | GET | `doors/{doorCode}/cells` | `DescribeCellsByDoorAsync` | - | `CellDescription[]` | failure | the WCS resolves a door code it holds (a door mapping, a command) to a cell |
| 29 | POST | `cells/lock` | `SetCellLockStateAsync` | `SetCellLockStateRequest` (camel) | 2xx, body ignored | failure | the machine refused to store into the cell with error code 67: the WCS marks it `Full` (3) |
| 30 | GET | `cells/{cellId}/content` | `GetCellContentAsync` | - | `decimal[]` | failure | the load units physically in a cell, in position order |
| 31 | GET | `cells/{cellId}/picking-buffer-areas` | `GetPickingBufferAreasAsync` | - | `decimal[]` | failure | where to look for a load unit to bring to a bay cell (priority order) |
| 32 | GET | `cells/{cellId}/positions` | `GetCellPositionsAsync` | - | `CellPosition[]` | failure | command 3005 (empty a cell physically): which positions the cell holds |
| 33 | POST | `cells/clear-positions` | `ClearCellPositionsAsync` | `ClearCellPositionsRequest` (camel) | 2xx, body ignored | failure | command 3005: the cell was emptied on the field, every position goes |
| 34 | POST | `cells/busy-state` | `GetCellBusyStateAsync` | `CellBusyStateRequest` (Pascal) | `CellBusyState` | failure | before storing into or picking from a cell |
| 35 | POST | `cells/storing-constraints` | `GetCellStoringConstraintsAsync` | `CellStoringConstraintsRequest` (Pascal) | `CellStoringConstraints` | failure | at what depth a load unit would be stored; `0` = no room |
| 23 | GET | `warehouses/{warehouseId}/topology` | `GetAreaTopologyAsync` | - | `AreaTopology` | failure | storage searches: automatic areas and the area of each cell |
| 41 | GET | `areas/{areaId}/defragmentation` | `GetAreaDefragmentationVerdictAsync` | query `?loadUnitTypeId=` | `AreaDefragmentationVerdict` | failure | after a cell is filled, with defragmentation enabled: does the area need compacting? |
| 42 | GET | `cells` | `GetCellsForEditorAsync` | query `?areaType=&automaticOnly=` | `EditorCellRow[]` | failure | the web editor lists cells to link (host route `GET /api/v1/wms/cells`) |
| 43 | GET | `configuration-check` | `GetConfigurationWarningsAsync` | query `?mode=` | `ConfigurationWarning[]` | failure | the web editor's configuration check (host route `GET /api/v1/wms/configuration-check`) |

## Storage reservations

| # | Method | Path | `IWarehouse` method | Body | Answer | 404 | Called when |
|---|---|---|---|---|---|---|---|
| 2 | POST | `reservations` | `ReserveStorageAsync` | `ReserveStorageRequest` (Pascal) | `StorageReservation` | failure | a load unit is on a device whose `LookWhereAllocateLoadUnit` asks for a storage cell. **Writes** when `AssignPosition` is true |
| 3 | GET | `reservations/{missionId}` | `GetStorageReservationAsync` | - | `StorageReservation` | none | a mission checks it still holds its cell. **Asking renews the reservation** |
| 4 | POST | `reservations/release` | `ReleaseStorageReservationAsync` | `ReleaseReservationRequest` (camel) | 2xx, body ignored | failure | the mission is stored, closed or cancelled; idempotent |
| 5 | POST | `reservations/release-load-unit-cell` | `ReleaseLoadUnitCellAsync` | `ReleaseLoadUnitCellRequest` (Pascal) | `LoadUnitCellReleaseOutcome` (a number) | failure | a change of cell or a defragmentation redirected by the operator gives its cell back. Queue: **durable** |
| 6 | POST | `reservations/candidates` | `ListStorageCellCandidatesAsync` | `StorageCellCandidatesRequest` (Pascal) | `StorageCellCandidate[]` (at most 20) | failure | an operator lists the cells a load unit could go to (host `GET /api/v1/missions/{id}/storage-cells`) |
| 7 | POST | `reservations/cell` | `ReserveStorageCellAsync` | `ReserveStorageCellRequest` (Pascal) | `StorageCellReservationResult` | failure | an operator sends a mission to a precise cell (host `PUT /api/v1/missions/{id}/destination` with `cellDoorCode`) |
| 44 | POST | `reservations/cell/check` | `CheckStorageCellAsync` | `ReserveStorageCellRequest` (Pascal) | `StorageCellReservationResult`, without the reservation | **optional**: 404 or 501, and the WCS goes on as before | before route 7, when the operator sends an extraction still in its cell to a precise cell: would the warehouse reserve it? Writes nothing |

## Load units

| # | Method | Path | `IWarehouse` method | Body | Answer | 404 | Called when |
|---|---|---|---|---|---|---|---|
| 36 | GET | `load-units/{loadUnitId}/description` | `DescribeLoadUnitAsync` | - | `LoadUnitDescription` | none | the most frequent call: command 3002, a load unit reported by the field, a device asking the data of its load unit |
| 37 | GET | `load-unit-types/{loadUnitTypeId}/handling-mode` | `GetLoadUnitTypeHandlingModeAsync` | - | `LoadUnitTypeHandlingMode` | none | missions built from a load unit type rather than a load unit: commands 1010 (test route), 3005 and 3007 |
| 38 | GET | `load-units/{loadUnitId}/assignment-state` | `GetLoadUnitAssignmentStateAsync` | - | `LoadUnitAssignmentState` | failure | before a manual move: a load unit assigned to a shipping note does not move by hand |
| 39 | GET | `load-units/by-code/{code}/stored` | `IsLoadUnitStoredAsync` | - | `bool` | failure | a load unit read by its code on the field: is it already stored? (never sent with an empty code) |
| 40 | GET | `load-units/by-code/{code}` | `ResolveLoadUnitByCodeAsync` | - | `LoadUnitIdentity` | none | the field reported a new load unit by its code (never sent with an empty code) |
| 8 | GET | `load-units/{loadUnitId}/location` | `LocateLoadUnitAsync` | - | `LoadUnitLocation` | none | where a load unit is, and at what depth |
| 24 | POST | `load-units/find` | `FindLoadUnitAsync` | `FindLoadUnitRequest` (Pascal) | `LoadUnitSearchResult` | none | an extraction row or a command looks for its load unit within areas or cells |
| 25 | POST | `load-units/find-by-reference` | `FindLoadUnitByReferenceAsync` | `FindLoadUnitByReferenceRequest` (Pascal) | `ReferenceSearchResult` | failure | command 3008: which load unit carries this article, reachable from this bay |
| 26 | POST | `load-units/pickup-door` | `ChoosePickUpDoorAsync` | `ChoosePickUpDoorRequest` (Pascal) | `PickUpDoorChoice` | failure | which door to pick a load unit from, and what is in front of it |
| 21 | POST | `load-units/allocate` | `AllocateLoadUnitAsync` | `AllocateLoadUnitRequest` (camel) | 2xx, body ignored | failure | **fact**: the load unit is now physically in a cell (stored, or arrived on a device linked to a cell). Queue: **durable** when stored; sent directly, with an empty `operationId`, for a linked cell |
| 22 | POST | `load-units/release` | `ReleaseLoadUnitAsync` | `ReleaseLoadUnitRequest` (camel) | 2xx, body ignored | failure | **fact**: the load unit left the automation (unloaded from a device). Queue: **durable** |

## Work the WCS pulls, and the facts it reports

| # | Method | Path | `IWarehouse` method | Body | Answer | 404 | Called when |
|---|---|---|---|---|---|---|---|
| 13 | GET | `extractions/pending-work` | `GetPendingExtractionWorkAsync` | - | `PendingExtraction[]` | failure | every turn of the master loop, in Run |
| 14 | POST | `extractions/paused-among` | `GetPausedExtractionRowsAsync` | `PausedAmongRequest` (Pascal) | `decimal[]` | failure | queue cleanup: which of these rows are no longer in progress (never sent with an empty list) |
| 15 | POST | `extractions/outcomes` | `ReportExtractionOutcomesAsync` | `ReportExtractionOutcomesRequest` (camel) | 2xx, body ignored | failure | after serving an order in a turn (with its header id, possibly with no row), and per row with header `0`; the `Extracted` outcome comes from a queue: **durable, no id** |
| 12 | POST | `load-units/left-extraction-queue` | `ReportLoadUnitLeftExtractionQueueAsync` | `LoadUnitLeftExtractionQueueRequest` (camel) | 2xx, body ignored | failure | **fact**: a row left the WCS's extraction queue (removed by hand, change of cell) |
| 20 | POST | `assignments/picked-extraction-became-deposit` | `ReportPickedExtractionBecameDepositAsync` | `PickedExtractionBecameDepositRequest` (Pascal) | `PickedExtractionBecameDepositResult` | failure | **fact**: an extraction already picked was sent back to storage. Queue: **durable** |
| 9 | GET | `notes/pending-work` | `GetPendingNoteWorkAsync` | query `?limitOneShippingNotePerBuffer=` | `PendingNote[]` | failure | every turn of the master loop, in Run |
| 11 | POST | `notes/paused-among` | `GetPausedNoteAssignmentsAsync` | `PausedAmongRequest` (Pascal) | `decimal[]` | failure | queue cleanup for shipping-note assignments (never sent with an empty list) |
| 45 | POST | `notes/missing-among` | `GetMissingNoteAssignmentsAsync` | `PausedAmongRequest` (Pascal) | `decimal[]`: the ids that no longer exist | **optional**: 404 or 501, and the WCS goes on as before | the same queue cleanup, before route 11: the rows of a deleted assignment leave the queue (never sent with an empty list) |
| 10 | POST | `notes/reject-assignment` | `RejectNoteAssignmentAsync` | `RejectNoteAssignmentRequest` (camel) | 2xx, body ignored | failure | **fact**: the WCS will not serve this note assignment, with the reason |
| 16 | POST | `assignments/mission-left-unserved` | `ReportMissionLeftAssignmentUnservedAsync` | `MissionLeftAssignmentUnservedRequest` (camel) | 2xx, body ignored | failure | **fact**: a mission left the engine without serving its assignment |
| 17 | POST | `assignments/mission-served` | `ReportMissionServedAssignmentAsync` | `MissionServedAssignmentRequest` (camel) | 2xx, body ignored | failure | **fact**: the load unit reached the workstation, the assignment is served |
| 18 | POST | `assignments/queued-extraction-unservable` | `ReportUnservableQueuedExtractionAsync` | `UnservableQueuedExtractionRequest` (camel) | 2xx, body ignored | failure | **fact**: a queued row cannot be served |
| 19 | POST | `assignments/confirm-served` | `ConfirmServedAssignmentsAsync` | `ConfirmServedAssignmentsRequest` (camel) | 2xx, body ignored | failure | the load unit of an assignment arrived: confirm the served rows (idempotent; the WCS does not retry it) |

## Enumerations on the wire

Enumerations travel as numbers.

| Enum | Values |
|---|---|
| `CellDoor` | 0 A, 1 B |
| `CellLockState` | 0 None, 1 Loading, 2 Unloading, 3 Full |
| `CellLockFilter` | 0 Any, 1 Loading, 2 Unloading, 3 Locked, 4 Unlocked, 5 FreeForLoading, 6 FreeForUnloading |
| `LoadUnitLockFilter` | 0 Any, 1 Locked, 2 Unlocked |
| `CellFullnessPreference` | 0 Fullest, 1 Emptiest |
| `StorageAreaSelectionMode` | 0 All, 1 First |
| `StorageCellSearchMode` | 0 All, 1 FreeAutomaticOperator, 2 ConnectedAutomaticOperator, 3 CellsReachableByAutomaticOperator |
| `StorageSearchOperatorType` | 0 Manual, 1 Automatic |
| `StorageReservationOutcome` | 0 CellFound, 1 LoadUnitCodeRequired, 2 LoadUnitIdRequired, 3 WarehouseRequired, 4 LoadUnitCodeNotFound, 5 LoadUnitHasNoReferences, 6 NoFreeCells, 7 NoDoorSelected, 8 NoCellSelected, 9 SelectedDoorNotFound, 10 LoadUnitPositioned, 11 PositionUpdateFailed |
| `StorageCellReservationOutcome` | 0 Reserved, 1 UnknownDoor, 2 NotADoorA, 3 CellNotAvailable, 4 UnknownLoadUnit |
| `LoadUnitCellReleaseOutcome` | 0 Released, 1 NotHeld |
| `ExtractionRowStatus` | 0 InExtraction, 1 Extracted, 2 NotReachable, 3 InOtherExtraction, 4 LoadUnitLocked |
| `PickedExtractionReopenOutcome` | 0 Reopened, 1 NothingToReopen, 2 OrderAlreadyClosed |
| `ReferenceSearchOutcome` | 0 Found, 1 NoPickingAreas, 2 NotFound |
