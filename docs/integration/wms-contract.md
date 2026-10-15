# The WMS contract

The WCS asks your WMS questions and tells it facts through **43 HTTP routes** that your WMS (or an
adapter in front of it) serves. This page is the contract: when each route is called, what goes on
the wire, what the WCS does with each answer, and what it does when the answer does not come.
The one-line-per-route table is in the [reference](../reference/wms-routes.md); a working
implementation of all 43 is [`samples/Wcs.SampleWms`](../../samples/Wcs.SampleWms/README.md). The
same contract in machine-readable form, generated from the client and versioned
(`WarehouseContract.Version`), is [`wms-contract.openapi.json`](../contracts/wms-contract.openapi.json),
with validated [examples](../contracts/README.md#examples).

Every example on this page was captured on the wire between a WCS host and the sample WMS (through a
logging proxy) or sent to the sample WMS in the form the WCS sends it; only `traceId` values and
`Date` headers were left out.

- [Transport](#transport)
- [What is on the wire](#what-is-on-the-wire)
- [Answers, refusals and failures](#answers-refusals-and-failures)
- [Facts, durable queues and receipts](#facts-durable-queues-and-receipts)
- [A load unit enters: description and storing](#a-load-unit-enters-description-and-storing)
- [Reading cells](#reading-cells)
- [An order is extracted](#an-order-is-extracted)
- [A load unit leaves](#a-load-unit-leaves)
- [Operator decisions](#operator-decisions)
- [Shipping notes](#shipping-notes)
- [Load units by code, and other readings](#load-units-by-code-and-other-readings)
- [The web editor's two routes](#the-web-editors-two-routes)

## Transport

| | |
|---|---|
| Base address | the host setting `Wms:ApiAddress`, an origin without path, e.g. `http://localhost:5142` |
| Path prefix | `/api/wcs/warehouse/` (`WarehouseRoutes.Prefix`) |
| Authentication | header `X-Api-Key: {Wms:ApiKey}` on every request; nothing else |
| Timeout | `Wms:TimeoutSeconds`, 30 by default |
| Bodies | `Content-Type: application/json; charset=utf-8` |
| Who calls | the engine, from its master loop (in Run, once a second by default) and from the devices' work; two routes only from the web editor |

The WCS does not authenticate *you*: there is no callback from the WMS to the WCS in this contract.
When your WMS wants the WCS to do something it uses the [host API](host-api.md).

A request as it leaves the WCS:

```http
GET /api/wcs/warehouse/load-units/5001.0/description HTTP/1.1
Host: localhost:5142
X-Api-Key: sample-wms-key
```

```http
HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8

{"loadUnitId":5001,"code":"LU005001","loadUnitTypeId":1,"handlingMode":0,"tare":25,"height":1000,"width":800,"depth":1200,"weight":300}
```

Some requests also carry a W3C `traceparent` header, added by .NET's HTTP client; you may ignore it
or use it to correlate logs.

## What is on the wire

The [WMS contract document](../contracts/wms-contract.openapi.json) states each of these rules per
operation: `x-wcs-request-casing`, `format: decimal`, `x-wcs-not-found`, `x-wcs-operation-id` and
`x-wcs-durable-queue` ([how to read them](../contracts/README.md#what-the-documents-say-about-the-wire)).

**Identifiers are decimals, and may come with a decimal part.** Load unit, cell, area, warehouse and
row ids are .NET `decimal` in the contract. The WCS writes them as it holds them, so the same load
unit can appear as `5001` or `5001.0`, **in bodies and in path segments**:

```text
GET  /api/wcs/warehouse/load-units/5001.0/description
GET  /api/wcs/warehouse/cells/101.0/partitions
GET  /api/wcs/warehouse/warehouses/1/topology
POST /api/wcs/warehouse/load-units/allocate   {"loadUnitId":5001.0,"cellId":101,...}
```

Parse ids as numbers (or decimals), never compare them as text. Mission ids and operation ids are
GUID strings.

**Enumerations are numbers**: `"lockState":3`, `"status":1`. The values are in the
[reference](../reference/wms-routes.md#enumerations-on-the-wire).

**Requests come in two casings.** Answers are read case-insensitively, so answer in either.
Requests are written by two different serializers of the WCS's client:

| Casing | Routes |
|---|---|
| PascalCase (`{"LoadUnitId":5001}`) | `reservations`, `reservations/release-load-unit-cell`, `reservations/candidates`, `reservations/cell`, `notes/paused-among`, `extractions/paused-among`, `assignments/picked-extraction-became-deposit`, `load-units/find`, `load-units/find-by-reference`, `load-units/pickup-door`, `cells/busy-state`, `cells/storing-constraints` (the POSTs that return a body) |
| camelCase (`{"loadUnitId":5001}`) | `reservations/release`, `notes/reject-assignment`, `load-units/left-extraction-queue`, `extractions/outcomes`, `assignments/mission-left-unserved`, `assignments/mission-served`, `assignments/queued-extraction-unservable`, `assignments/confirm-served`, `load-units/allocate`, `load-units/release`, `cells/lock`, `cells/clear-positions` (the POSTs whose answer body is ignored) |

ASP.NET Core minimal APIs and MVC bind both casings out of the box. In another stack, read
property names case-insensitively.

**Fixed identities.** Where a request names who asked, the WCS's own requests say
`"RequestedByWorkstationId":"KazamControl"`, `"workstationId":"KazamControl"` and
`"requestedByUserId":"KazamControl.Process"`. These values are constants of the engine.

**Requests that never reach the wire.** `notes/paused-among` and `extractions/paused-among` are not
sent with an empty list (the answer is the empty list); `load-units/by-code/{code}` and
`load-units/by-code/{code}/stored` are not sent with an empty code (the answers are "none" and
`false`).

## Answers, refusals and failures

In the [contract document](../contracts/wms-contract.openapi.json) every operation has `4XX` and `5XX`
answers, and the six readings below also a `404` (`x-wcs-not-found: none`).

| Your answer | What the WCS does |
|---|---|
| `2xx` with the expected body | uses it |
| `2xx` on a route whose body is ignored (marked "2xx, body ignored" in the [reference](../reference/wms-routes.md)) | done; the body is not read |
| `404` on one of the six readings that allow it: `reservations/{missionId}`, `load-units/{id}/location`, `load-units/find`, `load-units/{id}/description`, `load-unit-types/{id}/handling-mode`, `load-units/by-code/{code}` | reads it as "there is none" |
| `404` anywhere else, any other `4xx` or `5xx`, a timeout, a dropped connection, a body that is not the expected JSON, a `null` body | a failure: the client raises [`WarehouseUnavailableException`](../../WCS/Wcs.Wms.Contracts/src/WarehouseUnavailableException.cs) |

What a failure does depends on where it happens. A reading that fails leaves the work where it is:
the mission waits and the engine asks again later (the device's next attempt, or the next turn of
the master loop). The engine does not stop and does not lose state; failures go to the host log. A
fact sent from a durable queue is retried (next section).

**A 4xx is a refusal, not an outage.** `WarehouseUnavailableException.IsPermanentRejection` is true
for every 4xx except 408 and 429, `404` included. The durable queues stop retrying a refused request
and park it. So:

- answer `4xx` only for a request that can **never** succeed (an unknown load unit, an invalid body);
- answer `5xx` (or `408`/`429`) for anything temporary: your database is down, a load unit is not
  synchronised yet, a route is not implemented yet. The WCS will try again.

The sample WMS answers refusals with `application/problem+json`; the WCS does not read the body of
a refusal, it only logs it.

```http
POST /api/wcs/warehouse/load-units/allocate HTTP/1.1
Content-Type: application/json; charset=utf-8
X-Api-Key: sample-wms-key

{"loadUnitId":9999,"cellId":101,"storageDoorCode":"A0101","resetAssignment":true,"manual":false,"storageDepth":0,"detectedDepth":0,"requestedByUserId":"KazamControl.Process","operationId":"aaaaaaaa-0000-4000-8000-000000000003"}
```

```http
HTTP/1.1 400 Bad Request
Content-Type: application/problem+json

{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"Bad Request","status":400,"detail":"load unit 9999 is unknown"}
```

A missing or wrong key is yours to refuse with `401`; the WCS treats it as a refusal too:

```http
HTTP/1.1 401 Unauthorized
Content-Type: application/problem+json

{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.2","title":"Unauthorized","status":401,"detail":"Missing or wrong X-Api-Key header."}
```

When the WMS cannot be reached, the host's own routes that need it answer `503` with
`"unavailable": "warehouseUnreachable"` (see [host API](host-api.md#503-two-gates)).

## Facts, durable queues and receipts

In the [contract document](../contracts/wms-contract.openapi.json), `x-wcs-durable-queue` names the
queue of each route and `x-wcs-operation-id` the routes with a receipt, which also have a `409` answer.

Most POSTs are **facts**: the WCS reports something that already happened on the field. Your WMS
records it; it cannot veto it. Four kinds of fact are first written in a table of `wcs.db` and sent
from there by the master loop, so they survive a restart of the WCS and a WMS that is down:

| Durable queue (`wcs.db` table) | Route | `OperationId` | On a permanent refusal (4xx) |
|---|---|---|---|
| `PendingWarehouseTransitions` | `load-units/allocate`, `load-units/release` | yes | the row is parked with its reason; it holds back the later facts about the same load unit, and a parked release also every later allocation, until a person resolves it |
| `PendingExtractionOutcomes` | `extractions/outcomes` (the `Extracted` outcome) | no: idempotent by content | the batch is resent row by row; a row refused alone is logged as an error and dropped |
| `PendingPickedExtractionReports` | `assignments/picked-extraction-became-deposit` | yes | parked |
| `PendingCellGiveBacks` | `reservations/release-load-unit-cell` | yes | parked |

Retries: after the first failure the queue waits 2 s, then 4 s, 6 s, ... up to 30 s between
attempts, and never gives up ([`RetryBackoff.cs`](../../WCS/Wcs.Engine/src/RetryBackoff.cs)). The
queues are drained in every engine state, Stop included. Delivery is **at least once**: if the
answer is lost, the same request arrives again.

Two more lists in `wcs.db` are retried less: `CellsToMarkFull` (the `cells/lock` after an error 67
that the WMS did not receive, retried in Run until it answers; a 4xx drops it with a warning) and
`ReservationsToGiveBack` (the reservations of missions closed while the engine reloaded, released
once at the next Run; a failure leaves them to your expiry).

**Keep a receipt per `OperationId`.** For the routes with an `OperationId`:

- same id, same content: answer what you answered the first time, **without applying it again**;
- same id, different content: `409 Conflict`. It is a bug on one side, and the WCS parks the row;
- `operationId` `00000000-0000-0000-0000-000000000000`: not from a queue (the WCS sends
  `load-units/allocate` directly, with an empty id, when a load unit arrives on a device linked to
  a cell); no receipt.

"Same content" means the same values, not the same text: `5005` and `5005.0` are the same id. The
sample keeps its receipts in memory ([`Receipts.cs`](../../samples/Wcs.SampleWms/Receipts.cs)); a
real WMS writes the receipt in the same transaction as the change it records.

```http
POST /api/wcs/warehouse/reservations/release-load-unit-cell HTTP/1.1
Content-Type: application/json; charset=utf-8
X-Api-Key: sample-wms-key

{"OperationId":"aaaaaaaa-0000-4000-8000-000000000001","MissionId":"22222222-2222-4222-8222-222222222222","LoadUnitId":5005.0,"CellId":102.0,"UserId":"KazamControl.Process","Reason":"change of cell redirected by the operator"}
```

```http
HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8

0
```

The same request again answers `0` again, without releasing anything. The same `OperationId` with
`"Reason":"change of cell something else"`:

```http
HTTP/1.1 409 Conflict
Content-Type: application/problem+json

{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.10","title":"Same OperationId, different request","status":409,"detail":"Operation aaaaaaaa-0000-4000-8000-000000000001 was already received with a different content. The first one was applied; this one is refused."}
```

## A load unit enters: description and storing

This is what crossed the wire when command 3002 put pallet 5001 on the infeed bay IN01 of the
[sample plant](../../samples/plant/sample-plant.json), in order. IN01 is linked to the WMS cell 800
and asks for a storage cell (`LookWhereAllocateLoadUnit` = 2). The sequence is drawn in the
[overview](overview-and-boundary.md#storing-a-load-unit).

**1. What is this load unit?** `GET load-units/{loadUnitId}/description` (route 36):

```http
GET /api/wcs/warehouse/load-units/5001.0/description HTTP/1.1
X-Api-Key: sample-wms-key
```

```http
HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8

{"loadUnitId":5001,"code":"LU005001","loadUnitTypeId":1,"handlingMode":0,"tare":25,"height":1000,"width":800,"depth":1200,"weight":300}
```

`404` means "no such load unit": command 3002 then creates a mission without code or type. The
dimensions are what the WCS sends a device that asks for them (`AsiRequestsLuInfo`).

**2. The load unit is on a device linked to a cell.** `POST load-units/allocate` (route 21), sent
directly, with an empty operation id, `manual` true for a command and `storageDepth` -1, and then
`GET cells/{cellId}` (route 1), in this order: the cell read follows the allocate within a few
milliseconds, so validate the cell inside your allocate, not on a read you expect before it:

```http
POST /api/wcs/warehouse/load-units/allocate HTTP/1.1
Content-Type: application/json; charset=utf-8
X-Api-Key: sample-wms-key

{"loadUnitId":5001.0,"cellId":800.0,"storageDoorCode":null,"resetAssignment":false,"manual":true,"storageDepth":-1,"detectedDepth":0,"requestedByUserId":"KazamControl.Process","operationId":"00000000-0000-0000-0000-000000000000"}
```

```http
HTTP/1.1 200 OK
Content-Length: 0
```

```http
GET /api/wcs/warehouse/cells/800.0 HTTP/1.1
X-Api-Key: sample-wms-key
```

```http
HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8

{"cellId":800,"areaId":20,"warehouseId":1,"depth":1200,"inverted":false,"locked":0,"allowedLoadUnitTypeId":null,"groupId":null,"groupOrder":0,"doorCodeA":"BAY-IN01","doorCodeB":"","doorCount":1,"multiDepthStorage":false}
```

`storageDoorCode` null means "an operator cell" (a bay); with a door code, the door names the cell
and `cellId` is ignored. `resetAssignment` true means "first delete the load unit's previous
shipping-note assignment"; it does not mean "drop its reservations".

**3. Which warehouse, which areas.** `GET warehouses/{warehouseId}/topology` (route 23). The
warehouse is the one of the cell linked to the device where the load unit entered:

```http
HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8

{"warehouseId":1,"automaticAreaIds":[10],"cellToArea":{"101":10,"102":10,"103":10,"104":10,"105":10,"106":10,"800":20,"900":30}}
```

The WCS asks it on every storage search: answer an unknown warehouse with an empty topology rather
than a 404.

**4. Does the mission already hold a cell?** `GET reservations/{missionId}` (route 3). The first
time, no:

```http
GET /api/wcs/warehouse/reservations/cc9d3865-9c4c-4b3c-8610-48074197d392 HTTP/1.1
X-Api-Key: sample-wms-key
```

```http
HTTP/1.1 404 Not Found
Content-Type: application/problem+json

{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.5","title":"Not Found","status":404,"detail":"Mission cc9d3865-9c4c-4b3c-8610-48074197d392 holds no reservation."}
```

**5. Reserve a cell.** `POST reservations` (route 2). **You choose the cell.** The WCS tells you the
load unit, the warehouse, the areas it may search, the cells to exclude, and the plant devices
through which the load unit can reach a cell (`ReachableDeviceIds`: here the stacker crane SC01).
With `AssignPosition` true you **write** a reserved position for the load unit in that cell, owned
by the mission:

```http
POST /api/wcs/warehouse/reservations HTTP/1.1
Content-Type: application/json; charset=utf-8
X-Api-Key: sample-wms-key

{"MissionId":"cc9d3865-9c4c-4b3c-8610-48074197d392","LoadUnitId":5001.0,"WarehouseId":1,"SearchAreaIds":[10],"ExcludedCellIds":[],"ReachableDeviceIds":["5a000000-0000-4000-8000-000000000004"],"AreaSelectionMode":0,"CellSearchMode":1,"CellFullnessPreference":0,"SearchOperatorType":1,"AssignPosition":true,"VerifyPosition":false,"RequestedByWorkstationId":"KazamControl","RequestedByUserId":"KazamControl.Process"}
```

```http
HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8

{"missionId":"cc9d3865-9c4c-4b3c-8610-48074197d392","outcome":0,"cellId":101,"doorCode":"A0101","partition":"0000","storageDepth":1200,"needsCompaction":false}
```

**"No free cell" is an outcome, not an error**: `200` with `outcome` 6 (`NoFreeCells`). Here the
reachable device is one no cell knows:

```http
HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8

{"missionId":"22222222-2222-4222-8222-222222222222","outcome":6,"cellId":0,"doorCode":"","partition":null,"storageDepth":0,"needsCompaction":false}
```

The load unit then waits on its device and the WCS tries again later, unless the device (or its
group) has a rejection point for this case (`RejectItemWhenNoAllocation`): then it goes there. One reserved position per load unit: a new reservation replaces
the previous one.

**6. Where exactly.** `GET cells/{cellId}` and `GET cells/{cellId}/partitions` (route 27). The
partition map is where a WMS cell becomes an address the machines understand: for each door, the
ASI and subitem of the position the relocator goes to, and the cell's depth positions:

```http
HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8

{"cellId":101,"asiDoorA":"3001","subitemDoorA":"0101","asiDoorB":"","subitemDoorB":"","partitions":[{"name":"0000","offset":-1}]}
```

The WCS picks the partition whose `offset` equals the storing depth, or `-1` when the device stores
at a fixed depth (`RecordStorageLength` = FixedLength): a cell with one position lists one partition
at offset -1. The relocator SC01 then received a movement to `3001` / `0101` / `0000`.

**7. The mission is still alive.** While the load unit travels, the WCS asks
`GET reservations/{missionId}` again: **asking renews the reservation**. Expire the reservations
nobody asks about (the sample frees them after 10 minutes): it is how a reservation left by a WCS
that stopped is given back.

```http
HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8

{"missionId":"cc9d3865-9c4c-4b3c-8610-48074197d392","outcome":0,"cellId":101,"doorCode":"A0101","partition":"0000","storageDepth":1200,"needsCompaction":false}
```

A `404` here means the reservation was taken away (another flow used the cell): the WCS reserves
again.

**8. Stored.** When the relocator reports the store done, the WCS resolves the door
(`GET doors/{doorCode}/cells`, route 28), then sends the fact from its durable queue, with an
`OperationId`, and releases the reservation (route 4):

```http
POST /api/wcs/warehouse/load-units/allocate HTTP/1.1
Content-Type: application/json; charset=utf-8
X-Api-Key: sample-wms-key

{"loadUnitId":5001.0,"cellId":101,"storageDoorCode":"A0101","resetAssignment":true,"manual":false,"storageDepth":0,"detectedDepth":0,"requestedByUserId":"KazamControl.Process","operationId":"2a1d3a6f-ebbc-4ce7-a003-e2b4e8ce22a2"}
```

```http
POST /api/wcs/warehouse/reservations/release HTTP/1.1
Content-Type: application/json; charset=utf-8
X-Api-Key: sample-wms-key

{"missionId":"cc9d3865-9c4c-4b3c-8610-48074197d392"}
```

Both answered `200` with an empty body. `reservations/release` is idempotent: the WCS calls it for
every mission that closes or is cancelled, whether it held a cell or not.

After this the WMS says where the load unit is (`GET load-units/{id}/location`, route 8):

```http
HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8

{"loadUnitId":5001,"cellId":101,"storingDepth":1200}
```

and `404` when it is in no cell.

**If the machine refuses to store.** When the field driver reports error 67 on a store, the WCS
marks the cell full in the WMS (`POST cells/lock`, route 29, door A only) and asks for another cell,
excluding the refused one:

```http
POST /api/wcs/warehouse/cells/lock HTTP/1.1
Content-Type: application/json; charset=utf-8
X-Api-Key: sample-wms-key

{"doorCode":"A0302","lockState":3}
```

A refused lock (any 4xx) is logged and not retried; the WCS still excludes the cell. What the
engine does for each error code is in [connecting machines](connecting-machines.md#completion-codes-the-engine-reacts-to).

## Reading cells

Readings the engine makes around stores and picks. All are `200` with a body, and an unknown cell is
a failure (answer `404` if you like: the WCS treats it as a failure here).

The example answers are one moment of the sample WMS: load unit 5004 is on its way to cell 101, which
holds its reserved position, and cell 103 is free. Once 5004 is stored, `content` lists 5004
and its position has `reserved` false: in the sample a load unit is never both physical and reserved
in the same cell.

| Route | Example answer (sample WMS) | Meaning |
|---|---|---|
| `GET cells/101/content` (30) | `[]` | the load units physically in the cell, in position order; reserved positions excluded |
| `GET cells/101/positions` (32) | `[{"loadUnitId":5004.0,"reserved":true}]` | every position, reserved ones included |
| `POST cells/busy-state` (34) `{"CellId":101.0,"LoadUnitIdToExclude":0}` | `{"extraction":false,"pickUp":false,"storing":true}` | why the cell is busy: a load unit of it in an open extraction, a pick for a shipping note, another load unit with a reserved position |
| `POST cells/storing-constraints` (35) `{"CellId":101.0,"Door":0,"LoadUnitDepth":1200,"LoadUnitIdToExclude":0}` | `{"theoreticalStoreDepth":0}` | at what depth a load unit would be stored from that door; **0 = no room** |
| same, cell 103 | `{"theoreticalStoreDepth":1200}` | |
| `GET doors/A0101/cells` (28) | `[{"cellId":101,"areaId":10,...,"doorCodeA":"A0101",...}]` | the cells behind a door code; unknown door = `[]` |
| `GET cells/900/picking-buffer-areas` (31) | `[10]` | the storage areas a bay cell is served from, in priority order; `[]` is an answer |
| `GET areas/10/defragmentation?loadUnitTypeId=1` (41) | `{"areaId":10,"loadUnitTypeId":1,"needed":false}` | does the area need compacting for this type? the policy stays in the WMS |
| `POST cells/clear-positions` (33) `{"cellId":106}` | `200` | command 3005 emptied the cell on the field: delete every position of it |

## An order is extracted

The WCS does not receive orders: it **pulls** them. On every turn of its master loop in Run it asks
`GET extractions/pending-work` (route 13) and `GET notes/pending-work` (route 9). Answer with the
orders that are ready for the automation, already decided on your side:

```http
GET /api/wcs/warehouse/extractions/pending-work HTTP/1.1
X-Api-Key: sample-wms-key
```

```http
HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8

[{"extractionHeaderId":1,"pickingCellId":900,"bufferAreaIds":[10],"rows":[{"extractionRowId":1,"loadUnitId":5001,"loadUnitLocked":false,"processingId":1}]}]
```

`pickingCellId` is the bay cell the load units go to (the WCS finds the device linked to it);
`bufferAreaIds` are the areas to look in. Keep offering a row until the WCS reports it `Extracted`.

For each row the WCS looks for the load unit (`POST load-units/find`, route 24) and chooses the door
to pick it from (`POST load-units/pickup-door`, route 26):

```http
POST /api/wcs/warehouse/load-units/find HTTP/1.1
Content-Type: application/json; charset=utf-8
X-Api-Key: sample-wms-key

{"LoadUnitId":5001,"AllowedAreaIds":[10],"AllowedCellIds":null,"LoadUnitLock":2,"CellLock":6}
```

```http
HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8

{"loadUnitId":5001,"cellId":101}
```

```http
POST /api/wcs/warehouse/load-units/pickup-door HTTP/1.1
Content-Type: application/json; charset=utf-8
X-Api-Key: sample-wms-key

{"LoadUnitId":5001,"CellId":101}
```

```http
HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8

{"door":0,"doorCode":"A0101","obstacleCount":0,"obstacleLoadUnitIds":[]}
```

`load-units/find` answers `404` when the load unit is not within the limits asked (`LoadUnitLock` 2 =
unlocked load units only, `CellLock` 6 = cells free for unloading).

Then the WCS reports what it did with each row of the header, **after every turn**
(`POST extractions/outcomes`, route 15):

```http
POST /api/wcs/warehouse/extractions/outcomes HTTP/1.1
Content-Type: application/json; charset=utf-8
X-Api-Key: sample-wms-key

{"extractionHeaderId":1,"workstationId":"KazamControl","outcomes":[{"extractionRowId":1,"status":0}]}
```

| `status` | Meaning | What the WMS does |
|---|---|---|
| 0 `InExtraction` | a mission is on it | mark the row in progress; keep offering it |
| 1 `Extracted` | the load unit left its cell | mark the row done; close the order when nothing is left |
| 2 `NotReachable` | not found within the limits (not stored yet, in an area the bay is not served from) | keep the row; it will be offered again |
| 3 `InOtherExtraction` | the load unit is already in another extraction | as you decide |
| 4 `LoadUnitLocked` | the load unit is locked | as you decide |

In the captured run the order was created while pallet 5001 was still travelling to its cell: the
first four turns reported `"status":2` (`NotReachable`), the fifth `0`.

Three things about outcomes that are easy to get wrong:

1. **The header id can be 0.** The `Extracted` outcome comes from a durable queue, keyed by row, with
   `"extractionHeaderId":0`. Find rows by `extractionRowId`, never by header:

   ```json
   {"extractionHeaderId":0,"workstationId":"KazamControl","outcomes":[{"extractionRowId":1.0,"status":1}]}
   ```

2. **`Extracted` is sent when the relocator picks the load unit from its cell**, not when it reaches
   the bay. When it arrives on the bay device, linked to cell 900, the WCS reports
   `load-units/allocate` on that cell (with an empty operation id).
3. **An empty `outcomes` list is legitimate**: it is how the WCS lets you close an order whose rows are
   all already resolved.

Closing the order is yours: there is no route for it.

While rows are queued the WCS asks which of them are no longer in progress
(`POST extractions/paused-among`, route 14) and drops those from its queue. A set: answer the ids
that are paused (or unknown), `[]` if none:

```http
POST /api/wcs/warehouse/extractions/paused-among HTTP/1.1
Content-Type: application/json; charset=utf-8
X-Api-Key: sample-wms-key

{"Ids":[1.0,77.0]}
```

```http
HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8

[77.0]
```

When a row leaves the WCS's queue otherwise (an operator removes it, a change of cell), the WCS
reports `POST load-units/left-extraction-queue` (route 12), camelCase, `assignmentId` 0 when the row
served no note assignment:

```json
{"loadUnitId":5004.0,"assignmentId":0}
```

If an extraction already picked is sent back to storage by an operator, the WCS reports it once no
mission of its own carries the load unit any more (`POST assignments/picked-extraction-became-deposit`, route 20, durable,
with `OperationId`); the outcome is in the body:

```http
POST /api/wcs/warehouse/assignments/picked-extraction-became-deposit HTTP/1.1
Content-Type: application/json; charset=utf-8
X-Api-Key: sample-wms-key

{"OperationId":"aaaaaaaa-0000-4000-8000-000000000004","MissionId":"33333333-3333-4333-8333-333333333333","LoadUnitId":5004.0,"AssignmentId":0,"AssignmentGuid":"00000000-0000-0000-0000-000000000000","ExtractionRowId":1.0,"UserId":"KazamControl.Process","Reason":"sent back to storage by the operator"}
```

```http
HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8

{"assignment":1,"extractionRow":0,"detail":null}
```

Each of `assignment` and `extractionRow` is `0` Reopened, `1` NothingToReopen or `2`
OrderAlreadyClosed; both are required in the answer.

## A load unit leaves

Command 2004 on a device (or a field event) takes the load unit off the automation. The fact comes
from the durable queue (`POST load-units/release`, route 22): free its assignment, delete its
physical position, mark it shipped.

```http
POST /api/wcs/warehouse/load-units/release HTTP/1.1
Content-Type: application/json; charset=utf-8
X-Api-Key: sample-wms-key

{"loadUnitId":5001.0,"manual":true,"requestedByUserId":"KazamControl.Process","operationId":"fbd154fd-c5b6-41dc-997f-d48ccfb5e49f"}
```

```http
HTTP/1.1 200 OK
Content-Length: 0
```

## Operator decisions

Routes the WCS calls when a person, in the web editor or through the host API, changes a mission's
destination.

`POST reservations/candidates` (route 6): the cells a load unit could go to, at most 20
(`StorageCellCandidatesRequest.Limit`), nothing reserved:

```http
POST /api/wcs/warehouse/reservations/candidates HTTP/1.1
Content-Type: application/json; charset=utf-8
X-Api-Key: sample-wms-key

{"LoadUnitId":5005.0,"WarehouseId":1,"SearchAreaIds":[10],"ExcludedCellIds":[],"ReachableDeviceIds":["5a000000-0000-4000-8000-000000000004"],"CellSearchMode":1}
```

```http
HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8

[{"cellId":102,"doorCode":"A0102","areaId":10,"areaDescription":"Rack A (automatic)","column":1,"floor":2,"description":"Rack A, column 1, floor 2","loadUnitsInCell":0},{"cellId":103,"doorCode":"A0201","areaId":10,"areaDescription":"Rack A (automatic)","column":2,"floor":1,"description":"Rack A, column 2, floor 1","loadUnitsInCell":0}]
```

(The sample answered five cells; two are shown.)

`POST reservations/cell` (route 7): reserve the precise cell, named by its door A code. Refusals are
outcomes in the body:

```http
POST /api/wcs/warehouse/reservations/cell HTTP/1.1
Content-Type: application/json; charset=utf-8
X-Api-Key: sample-wms-key

{"MissionId":"22222222-2222-4222-8222-222222222222","LoadUnitId":5005.0,"DoorCode":"A0102","RequestedByWorkstationId":"KazamControl","RequestedByUserId":"KazamControl.Process"}
```

```http
HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8

{"outcome":0,"reservation":{"missionId":"22222222-2222-4222-8222-222222222222","outcome":0,"cellId":102,"doorCode":"A0102","partition":"0000","storageDepth":1200,"needsCompaction":false},"reason":null}
```

The same request for door `A0101`, while pallet 5004 holds cell 101, is refused with outcome `3`
(`CellNotAvailable`):

```http
HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8

{"outcome":3,"reservation":null,"reason":"cell 101 is taken, locked or not a storage cell"}
```

`outcome` is required in the answer. When the change is undone, the WCS gives the cell back through
its durable queue (`reservations/release-load-unit-cell`, route 5, shown above): answer `0`
(Released) or `1` (NotHeld, the cell was not held for that load unit any more); both are answers.

## Shipping notes

The second kind of pulled work: load units assigned to a buffer for a shipping note. The sample WMS
has none (`notes/pending-work` answers `[]`). The routes:

`GET notes/pending-work?limitOneShippingNotePerBuffer=false` (route 9), every turn in Run: the
assignments ready for the automation, `PendingNote[]`:

```json
[{"assignmentId":7,"assignmentGuid":"55555555-5555-4555-8555-555555555555","loadUnitId":5003,"bufferCellId":900,"bufferAreaIds":[10],"destinationItemId":null,"workstationId":"WS1","processingId":3}]
```

The query parameter is the host setting `Wcs:Engine:LimitOneShippingNotePerBuffer`, for your WMS to
apply. Then the facts, all camelCase, all answered `2xx` with the body ignored:

| Route | Body (as the WCS sends it) | Meaning |
|---|---|---|
| `notes/reject-assignment` (10) | `{"assignmentId":7.0,"loadUnitId":5003.0,"reason":"load unit not reachable from the buffer"}` | the WCS will not serve it: delete the assignment, log the reason |
| `assignments/mission-left-unserved` (16) | `{"missionId":"44444444-...","loadUnitId":5003.0,"assignmentGuid":"00000000-...","assignmentId":7.0,"reason":"mission removed"}` | a mission left the engine without serving it |
| `assignments/mission-served` (17) | `{"missionId":"44444444-...","loadUnitId":5003.0,"assignmentGuid":"55555555-...","reason":"load unit reached the workstation"}` | served |
| `assignments/queued-extraction-unservable` (18) | `{"extractionQueueId":12.0,"loadUnitId":5003.0,"assignmentGuid":"55555555-...","reason":"load unit in another extraction"}` | a queued row cannot be served |
| `assignments/confirm-served` (19) | `{"assignmentId":7.0,"userId":"KazamControl.Process"}` | confirm the served rows; idempotent, and the WCS does not retry it |

`POST notes/paused-among` (route 11), PascalCase `{"Ids":[7.0]}`, works like
`extractions/paused-among`.

## Load units by code, and other readings

When the field reports a new load unit by its label, the WCS resolves the code
(`GET load-units/by-code/{code}`, route 40; `404` = no such code) and then asks the description:

```http
GET /api/wcs/warehouse/load-units/by-code/LU005003 HTTP/1.1
X-Api-Key: sample-wms-key
```

```http
HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8

{"loadUnitId":5003}
```

| Route | Example answer | Meaning |
|---|---|---|
| `GET load-units/by-code/LU005004/stored` (39) | `true` | is the load unit with this label already in a cell? |
| `GET load-units/5004/assignment-state` (38) | `{"loadUnitId":5004,"assignedToNote":false}` | is it committed to a shipping note? always `200`, also for an unknown load unit |
| `GET load-unit-types/1/handling-mode` (37) | `{"loadUnitTypeId":1,"handlingMode":0}` | `404` = no such type |
| `POST load-units/find-by-reference` (25) `{"ReferenceId":"ART-BLUE","BufferCellId":900}` | `{"outcome":0,"loadUnitId":5004,"cellId":101}` | command 3008: a load unit with this article reachable from this bay; `outcome` 2 = NotFound, 1 = NoPickingAreas |

## The web editor's two routes

Two routes are called only when someone uses the web editor, through the host
(`GET /api/v1/wms/cells`, `GET /api/v1/wms/configuration-check`).

`GET cells?areaType=&automaticOnly=` (route 42) lists the cells someone can link a device to. With
no `areaType` the editor wants the storage areas (types 0 and 1), for the doors of a Storage device.
For the cell of any other device it asks `automaticOnly=true` and area type **2** for Automation,
Loading and Circular Distributor devices, **3** for Buffer and LoadingAndBuffer devices. Put each
bay cell in an automatic area of the type its device asks for, or the editor's list opens empty:

```http
GET /api/wcs/warehouse/cells?areaType=2&automaticOnly=true HTTP/1.1
X-Api-Key: sample-wms-key
```

```http
HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8

[{"cellId":800,"warehouseDescription":"Sample warehouse","areaDescription":"Infeed bays","column":0,"floor":0,"doorCodeA":"BAY-IN01","doorCodeB":null,"description":"Infeed bay IN01"}]
```

Note that the WCS sends `automaticOnly=false` explicitly when the editor asks without it:
`GET cells?automaticOnly=false`.

`GET configuration-check?mode=0` (route 43): what does not add up in your data, as free columns the
editor shows. `[]` = all fine. A row is `{"columns": {"Problem": "...", "DoorCode": "...", "Cells":
"..."}}` (the column names are yours).
