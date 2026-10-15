# Reference: the host API

Every operation the WCS host serves under `/api/v1`, 66 in all, plus the SignalR hub. The route
constants are in [`ApiRoutes`](../../WCS/Wcs.Api.Contracts/src/ApiRoutes.cs), the bodies in
[`Wcs.Api.Contracts/src/Dtos`](../../WCS/Wcs.Api.Contracts/src/Dtos), the mappings in
[`Wcs.Host/src/Endpoints`](../../WCS/Wcs.Host/src/Endpoints). The same list, with schemas, is the
OpenAPI document at `/openapi/v1.json`, served when the host does not run in the Production
environment. How to use them, with examples, is in [the host API guide](../integration/host-api.md).

Columns:

- **Lease**: *yes* = the request must carry `X-Edit-Holder` with the holder of the edit lease, or it
  is refused with `423` ([edit lease](../integration/host-api.md#the-edit-lease-423)).
- **Engine**: the engine state the route needs, or `409` is returned. *Stop* = only in Stop;
  *not Run* = refused in Run, Recover and Dispose, allowed in Stop and Pause.
- Every route needs `X-Api-Key`. A read-only key (`Wcs:ReadOnlyApiKey`) may call `GET`
  routes and the hub; anything else is `403`. No route answers `HEAD`: the key check lets it
  through and the host answers `404`, so a monitoring probe uses `GET /api/v1/status`.
- Every route except `GET /status` and the two edit-lease routes answers `503` while the engine is
  not loaded (`"unavailable": "engineNotLoaded"`).

## Status and edit lease

| Method | Path | Lease | Engine | Notes |
|---|---|---|---|---|
| GET | `/api/v1/status` | | | `EngineStatus`: `navStatus`, `defragmentation`, `version`, `utcNow`, `historyFrom`, `engineLoadError`. Always answers. |
| POST | `/api/v1/plant/edit-lock` | | | Body `{"holder": "..."}`. `200` granted or renewed (`isMine: true`), `409` held by someone else (`isMine: false`), `400` without holder. Lease of 60 s. |
| DELETE | `/api/v1/plant/edit-lock` | | | Query `holder` (required). `204`; releasing a lease you do not hold does nothing. |

## Commands

| Method | Path | Lease | Engine | Notes |
|---|---|---|---|---|
| POST | `/api/v1/commands` | | see [commands](commands.md#rules-the-host-applies-before-queueing) | `CommandRequest` -> `201` `CommandDto`, `Location: /api/v1/commands/{id}` |
| GET | `/api/v1/commands` | | | Query `status`, `workstationId`, `userId`, `from`, `to`, `itemId`, `showStatus`, `minType`, `maxType`, `excludeUserId`, `take` (200 by default, 1 to 1000). Newest first. |
| GET | `/api/v1/commands/{id}` | | | `CommandDto` or `404` |
| PUT | `/api/v1/commands/{id}` | | | `CommandUpdate` (`status`, `errorDescription`, `info`, `showStatus`) -> `204`, `404`, or `400` (unknown status, or a started command sent back) |

## Plant

| Method | Path | Lease | Engine | Notes |
|---|---|---|---|---|
| GET | `/api/v1/plant/items` | | | `PlantItemDto[]`; query `deviceType` (repeatable) |
| POST | `/api/v1/plant/items` | yes | Stop | `PlantItemConfigDto` -> `201`; `400` empty `itemId`; `409` id exists |
| DELETE | `/api/v1/plant/items` | yes | not Run | removes every device |
| GET | `/api/v1/plant/items/status` | | | `ItemStatus[]`: live status and load unit of every device |
| GET | `/api/v1/plant/device-types` | | | `DeviceTypeDto[]`: code, label, shape, ports (used by the editor) |
| GET | `/api/v1/plant/items/config` | | | `PlantItemConfigDto[]` from `wcs.db` |
| GET | `/api/v1/plant/items/{id}` | | | `PlantItemDto` or `404` |
| DELETE | `/api/v1/plant/items/{id}` | yes | not Run | `204` or `404`; also removes its junctions |
| GET | `/api/v1/plant/items/{id}/config` | | | `PlantItemConfigDto` or `404` |
| PUT | `/api/v1/plant/items/{id}/config` | yes | not Run; Stop if `connectionType` or `deviceConnectionId` change | `204`, `400` id mismatch, `404` unknown device |
| PUT | `/api/v1/plant/items/{id}/geometry` | yes | not Run | `PlantItemGeometryUpdateDto` with `expectedGeometryVersion` -> `200` new version, `409` `PlantItemGeometryConflictDto` |
| PUT | `/api/v1/plant/items/geometry` | yes | not Run | the same for several devices at once, all or none |
| POST | `/api/v1/plant/items/connection-type` | yes | Stop | `{"connectionType": 0}` sets every device to Test (0) or Connected (1) |
| POST | `/api/v1/plant/items/keep` | yes | not Run | body: the ids to keep; removes every other device |
| GET | `/api/v1/plant/junctions` | | | `JunctionDto[]` |
| POST | `/api/v1/plant/junctions` | yes | not Run | `JunctionRequest` -> `201` `JunctionDto`; `400` for empty, equal or unknown ends |
| PUT | `/api/v1/plant/junctions/{id}` | yes | not Run | `JunctionRequest`: only `priority` is applied -> `204` or `404` |
| DELETE | `/api/v1/plant/junctions/{id}` | yes | not Run | `204` or `404` |
| GET | `/api/v1/plant/door-mappings` | | | `DoorMappingDto[]` |
| PUT | `/api/v1/plant/door-mappings` | yes | not Run | replaces all door mappings; `404` lists unknown devices |
| POST | `/api/v1/plant/door-mappings/link` | yes | not Run | `DoorMappingLink` (`itemId`, `doorCode`, `cellId`): links a device to a WMS cell |
| DELETE | `/api/v1/plant/door-mappings/{itemId}` | yes | not Run | removes the door mappings of a device |
| GET | `/api/v1/plant/automatic-cells` | | | the cells served by a storage device with a relocator, from the door mappings (`AutomaticCell[]`: `cellId`, `itemId`, `relocatorItemId`) |
| GET | `/api/v1/plant/decoration` | | | `PlantDecorationDto`: free texts and boxes of the drawing |
| PUT | `/api/v1/plant/decoration` | yes | not Run | |
| GET | `/api/v1/plant/diagram` | | | the legacy drawing, or `404` |
| PUT | `/api/v1/plant/diagram` | yes | | legacy: `405` once the geometry has been migrated, which the host does at its first start |
| GET | `/api/v1/plant/graph` | | | `PlantGraphDto`: the whole plant, the export file |
| PUT | `/api/v1/plant/graph` | yes | Stop, no active mission and empty extraction queue | replaces the whole plant -> `204`; `400` invalid file; `409` not stopped or work pending |
| POST | `/api/v1/plant/graph/legacy-geometry` | | | reads positions from an old drawing in the body; writes nothing |

## Missions and extraction queue

| Method | Path | Lease | Engine | Notes |
|---|---|---|---|---|
| GET | `/api/v1/missions` | | | active missions, `MissionDto[]`; query `udc`, `itemId`, `type` |
| GET | `/api/v1/missions/exists` | | | query `udc` (required), `status` (repeatable) -> `bool` |
| GET | `/api/v1/missions/holds` | | | query `udc` (required), `excludeAssignmentId` -> `bool` |
| GET | `/api/v1/missions/steps` | | | every step of every active mission |
| GET | `/api/v1/missions/{id}` | | | `MissionDto` or `404` |
| GET | `/api/v1/missions/{id}/steps` | | | `MissionStepDto[]` or `404` |
| PUT | `/api/v1/missions/{id}/position` | | | `{"itemId": "..."}`: tells the engine where the load unit really is. `204`, `202` (at the end of the movement), `404`, `400`, `409` device occupied, `503` in Recover |
| PUT | `/api/v1/missions/{id}/destination` | | | `MissionDestinationUpdate`: new destination device, optional gateway, relocator arrival or precise cell (`cellDoorCode`) |
| GET | `/api/v1/missions/{id}/storage-cells` | | | query `itemId` (required): candidate cells from the WMS (`reservations/candidates`); `503` WMS unreachable |
| GET | `/api/v1/extraction-queue` | | | `ExtractionQueueItemDto[]`; query `processed`, `udc`, `take` (500) |
| GET | `/api/v1/extraction-queue/contains` | | | query `udc` (with `excludeRow`, `excludeAssignmentId`) or `cell` -> `bool`; `400` without either |
| GET | `/api/v1/extraction-queue/cells` | | | cells with a queued row |
| GET | `/api/v1/extraction-queue/load-units` | | | load units with a queued row |
| GET | `/api/v1/extraction-queue/{id}` | | | `ExtractionQueueItemDto` or `404` |
| PATCH | `/api/v1/extraction-queue/{id}/priority` | | | `{"priority": 5}` -> `204` or `404` |
| DELETE | `/api/v1/extraction-queue/{id}` | | | queues command 4001 for the row's load unit -> `202` with the `CommandDto`; `404` unknown row. Unlike `POST /commands`, no Run check here |

## History and log

| Method | Path | Lease | Engine | Notes |
|---|---|---|---|---|
| GET | `/api/v1/handled-load-units` | | | query `from`, `to` (required), `type`, `udc`, `process`, `take` (1000) |
| GET | `/api/v1/handled-load-units/summary` | | | query `from`, `to` (required), `groupBy` = `day` (default), `week`, `month`, `year` |
| GET | `/api/v1/handled-load-units/exists` | | | query `process`, `loadUnitProcess` or `noteHeader` -> `bool` |
| GET | `/api/v1/log` | | | the navigator log (`LogEntryDto[]`, newest first); query `from`, `to` (required), `itemId`, `missionId`, `take` (1000) |
| POST | `/api/v1/log` | | | `LogEntryRequest`: append a line -> `202` |
| GET | `/api/v1/log/transit-times` | | | query `from`, `to`, `fromAsi`, `toAsi` (required) |
| GET | `/api/v1/history/plc-messages` | | | the driver message log; query `from`, `to` (required), `take` (1000) |

`from` and `to` outside the months the history keeps answer `400`.

## Driver settings

| Method | Path | Lease | Engine | Notes |
|---|---|---|---|---|
| GET | `/api/v1/plc/connections` | | | the driver settings rows (`PlcConnectionDto[]`) |
| PUT | `/api/v1/plc/connections` | yes | Stop | replaces the rows; `409` with `plcConnectionsInUse` if a removed row is still used by devices |
| GET | `/api/v1/plc/connections/{id}/status` | | | what the driver last persisted (`PlcConnectionStatus`) or `404` |

## WMS pass-through (used by the web editor)

| Method | Path | Lease | Engine | Notes |
|---|---|---|---|---|
| GET | `/api/v1/wms/cells` | | | forwards to the WMS route `cells`; query `areaType`, `automaticOnly`; `503` WMS unreachable |
| GET | `/api/v1/wms/configuration-check` | | | forwards to the WMS route `configuration-check`; query `mode` |
| GET | `/api/v1/wms/cell-search-context` | | | what a WMS cell search may need from the WCS in one read: automatic cells with their lock, relocator status, queued cells and load units |

## SignalR hub

| Path | Auth | Server-to-client messages (`IPlantEvents`) |
|---|---|---|
| `/hub/plant` | `X-Api-Key` header only (full or read-only key); `access_token` in the query is refused with `401` | `ItemStatusChanged(ItemStatus)`, `NavigatorStatusChanged(EngineStatus)`, `MissionChanged(MissionDto, MissionChange)`, `ExtractionQueueChanged(ExtractionQueueItemDto, QueueChange)`, `CommandChanged(CommandDto)`, `Watchdog(DateTime)` |

`MissionChange`: 0 Started, 1 StepChanged, 2 Completed, 3 Removed, 4 PositionChanged.
`QueueChange`: 0 Added, 1 Updated, 2 Removed.

## Enumerations in the host API

| Enum | Values |
|---|---|
| `NavigatorStatusTypes` (`navStatus`) | 0 Stop, 1 Run, 2 Recover, 3 Dispose, 4 Pause |
| `DeviceStatusTypes` (`status` of a device) | 0 Ready, 1 Moving, 2 Loading, 3 Unloading, 4 Busy, 5 Recovering, 6 OffLine, 7 Locked, 89 Warning, 99 Error |
| `LuLoadStatusTypes` (`luLoadStatus`) | 0 None, 1 Loaded |
| `MissionStatusTypes` | 0 Run, 1 Stop, 2 Done, 99 Error |
| `MissionTypes` | 0 Storing, 1 Extraction, 2 ChangeStoreLocation, 5 MovingToItem, 6 StoringByPickUp, 7 Rejection, 9 PickUpAndStoreAgain, 10 DefragmentCell, 99 None |
| `OperationTypes` | 0 Inbound, 1 Outbound, 3 Compacting, 99 None |
| `DefragmentationTypes` | 0 None, 1 Disabled, 2 Enabled |
| `ConnectionTypes` | 0 Test (loopback driver), 1 Connected (field driver) |
