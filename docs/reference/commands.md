# Reference: command codes

Commands are how a person or another system asks the engine to *do* something: start, stop, put a
load unit on a device, take it off, move it. You queue one with `POST /api/v1/commands`
([host API](../integration/host-api.md#commands)); the engine picks up the queued commands on every
turn of its master loop (every `Wcs:Engine:MasterProcessOnEvery` milliseconds, 1000 by default) and
writes their outcome back on the same record.

```http
POST /api/v1/commands HTTP/1.1
Host: 127.0.0.1:5580
X-Api-Key: my-wcs-key
Content-Type: application/json

{ "commandType": 3002, "itemId": "5a000000-0000-4000-8000-000000000001", "loadUnitId": 5001 }
```

```http
HTTP/1.1 201 Created
Content-Type: application/json; charset=utf-8
Location: /api/v1/commands/2

{"id":2,"date":"2026-10-15T04:12:24.2148172+02:00","commandType":3002,"itemId":"5a000000-0000-4000-8000-000000000001","loadUnitId":5001,"storageDoorCode":null,"status":0,"errorDescription":null,"assignmentGuid":null,"info":null,"showStatus":0,"workstationId":null,"processId":null,"bufferCellId":0,"pickUpDoorCode":null,"loadUnitTypeId":0,"userId":null,"destinationItemId":null,"referenceId":null,"fromApi":true,"apiStatus":0}
```

The code list is `Command.CommandTypes` in
[`Command.cs`](../../WCS/Wcs.Engine/src/Items/Command.cs) (the same values are in
`Enums.CommandTypes` of [`Wcs.Common`](../../WCS/Wcs.Common/src/Enums.cs)); the dispatcher is
[`ComandToDo.cs`](../../WCS/Wcs.Engine/src/Collections/ComandToDo.cs).

## Rules the host applies before queueing

| Check | Answer |
|---|---|
| `commandType` is not one of the codes below | `400`, detail `Tipo di comando sconosciuto: 1234` ("unknown command type") |
| `status` is not `0` (to do) or `2` (already done, a trace) | `400` |
| code is 2000 or more, except 4002, 4004, 4005, 4006, and the engine is not in Run | `503`, detail `Il motore è in Stop: il comando 3002 richiede Run` ("the engine is in Stop: command 3002 needs Run") |

Codes below 2000 are accepted in any engine state. A command that is accepted is not necessarily
executed: the engine still checks its own conditions when it runs it, and writes the result in
`status` and `errorDescription`.

## Command fields

The fields of `CommandRequest` ([`CommandDto.cs`](../../WCS/Wcs.Api.Contracts/src/Dtos/CommandDto.cs))
and the name the engine gives them:

| JSON field | Engine field | Meaning |
|---|---|---|
| `commandType` | `CommandType` | the code |
| `itemId` | `ItemCommandId` | the device the command is about (a plant item id, a GUID) |
| `loadUnitId` | `LuId` | the load unit (the WMS's id) |
| `destinationItemId` | `ItemDestinationId` | the device to go to |
| `storageDoorCode` | `StorageDoorCode` | a cell door code: the target cell, or the cell to empty |
| `pickUpDoorCode` | `PickUpDoorCode` | a cell door code: the cell to pick from |
| `bufferCellId` | `BufferCellId` | a bay (buffer) cell |
| `loadUnitTypeId` | `LuTypeId` | a load unit type |
| `referenceId` | `ReferenceId` | an article (reference) code |
| `workstationId`, `userId` | | who asked, written in the log |
| `processId`, `assignmentGuid`, `info` | | carried and stored |
| `status` | | `0` to execute, `2` for a trace that is already done |
| `date` | | when it happened; now when omitted |

## Command statuses

| `status` | Name | Meaning |
|---|---|---|
| 0 | ToDo | queued; the engine executes commands with status 0 |
| 1 | Running | being executed |
| 2 | Done | executed (also: accepted with nothing to do) |
| 3 | Warning | not executed because the state did not allow it (e.g. 1001 when already running); `errorDescription` says why |
| 99 | Error | failed; `errorDescription` says why |

`PUT /api/v1/commands/{id}` can change status, error, info and show status, but never moves a
command that has started back to 0 (the engine would run it again).

## The codes

"Run" means: the host refuses the command with `503` unless the engine is in Run.

### Engine (1001-1010)

| Code | Name | Run | Fields | What the engine does |
|---|---|---|---|---|
| 1001 | `SetNavigatorRun` | no | - | From Stop or Pause: starts the engine (from Stop it also opens the driver connections). Otherwise status 3. |
| 1002 | `SetNavigatorStop` | no | - | From Run: stops the engine and closes the driver connections. Otherwise status 3. |
| 1003 | `ReloadDiagram` | no | - | In Stop or Pause: reloads the plant from `wcs.db` and **rebuilds the driver connections** (needed after a plant import that changes connections, see [connecting machines](../integration/connecting-machines.md#when-connections-are-built)). Otherwise status 3. |
| 1004 | `RemoveAllLu` | no | - | Unloads every load unit from every device and removes every active mission. |
| 1005 | `SetStatusReadyAllItems` | no | - | Puts every device in Ready. |
| 1007 | `ExecuteSteps` | no | - | Asks every device to execute its next step. Ignored when repeated within 10 seconds. |
| 1008 | `SetDefragmentationEnabled` | no | - | Enables defragmentation (if it was disabled). |
| 1009 | `SetDefragmentationDisabled` | no | - | Disables defragmentation (if it was enabled). |
| 1010 | `TestRoute` | no, but the engine refuses it unless in Run | `itemId` (source), `destinationItemId`, `loadUnitTypeId` | Builds a mission with no load unit from the source to the destination, to try a route. Asks the WMS the handling mode of the type (`load-unit-types/{id}/handling-mode`). |

### Devices (2001-2009)

All need `itemId`. A device that does not exist: status 99, `Target device not found for ItemCommandId '...'`.

| Code | Name | Run | What the engine does |
|---|---|---|---|
| 2001 | `ItemExecuteStep` | yes | Executes the next step of the device (the way to retry after a device went to Warning). |
| 2002 | `ItemSetOffLine` | yes | Puts the device OffLine: routes avoid it. |
| 2003 | `ItemSetOnLine` | yes | Puts the device back online. |
| 2004 | `ItemUnloadLu` | yes | The load unit on the device leaves the automation: the WCS reports `load-units/release` to the WMS. Refused (status 99) when `Wcs:Engine:DisableUnloadLu` is true, when the device is busy or neither Ready nor Warning, or when it holds no load unit. |
| 2007 | `ItemStepDone` | yes | Marks the running step of the device as done, by hand. |
| 2008 | `ItemRejectLu` | yes | Sends the load units on the device to the rejection point. |
| 2009 | `ItemFindStoringLocation` | yes | Searches a storage cell for the load units on the device. Status 99 if it holds none. |

### Load units (3001-3008)

| Code | Name | Run | Fields | What the engine does |
|---|---|---|---|---|
| 3001 | `MoveLuAutomatic` | yes | - | **Reserved.** Accepted and marked Done without doing anything. Do not use it. |
| 3002 | `LoadLu` | yes | `itemId`, `loadUnitId` | A load unit appears on the device: the engine asks the WMS its description (`load-units/{id}/description`) and creates a mission at the device. What happens next depends on the device (`LookWhereAllocateLoadUnit`, `BuildDestinationItemId`, ...). On a device that already holds a load unit the command is marked Done and does nothing. |
| 3003 | `MoveLuManual` | yes | `loadUnitId`, `storageDoorCode` (target), `bufferCellId`, `workstationId` | Moves a load unit by hand towards the device that serves the door code. |
| 3004 | `EmptyCellLogically` | yes | `storageDoorCode`, `loadUnitId`, `bufferCellId`, `workstationId` | Empties a cell logically. |
| 3005 | `EmptyCellPisically` (sic) | yes | `storageDoorCode`, `pickUpDoorCode`, `loadUnitTypeId`, `workstationId` | Empties a cell physically: the relocator picks what is in it. Uses `cells/{id}/positions` and `cells/clear-positions`. |
| 3006 | `ChangeLUsStoreLocation` | yes | `pickUpDoorCode` (from), `storageDoorCode` (to) | Moves the load units of a cell to another cell. |
| 3007 | `MoveLuToItem` | yes | `itemId` (where the load unit is), `destinationItemId`, `loadUnitId` | Moves a load unit to a device. Refused if the WMS does not know the load unit. |
| 3008 | `MoveReferenceManual` | yes | `itemId`, `referenceId`, `storageDoorCode` | Finds a load unit carrying the article (`load-units/find-by-reference`) and brings it out. |

### Queue and traces (4001-4006)

| Code | Name | Run | Fields | What the engine does |
|---|---|---|---|---|
| 4001 | `DeleteLuFromExtractionQueue` | yes | `loadUnitId` | Removes the load unit's row from the extraction queue and reports `load-units/left-extraction-queue`. `DELETE /api/v1/extraction-queue/{id}` queues this command for you. |
| 4002 | `ModifyRushMoverStatus` | no | - | A trace written by an operator client: send it with `status` 2. The engine does not execute it. |
| 4004 | `ChangeCellLockStatus` | no | - | A trace, as 4002. |
| 4005 | `AddOrModifyLuPosition` | no | - | A trace, as 4002. |
| 4006 | `ModifyDatabaseCellContents` | no | - | A trace, as 4002. |
