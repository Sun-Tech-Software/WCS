# Wcs.SampleWms

A WMS that answers the WCS: the 43 routes of the WCS -> WMS contract, over an in-memory warehouse.
Run it to see the WCS store and extract load units (the walk-through is in [`../README.md`](../README.md));
read it to write the adapter that puts your own WMS behind the same routes.

```powershell
dotnet run --project samples/Wcs.SampleWms                          # http://localhost:5142, key sample-wms-key
dotnet run --project samples/Wcs.SampleWms --urls http://localhost:6142
```

| Setting (`appsettings.json`) | Default | Meaning |
|---|---|---|
| `Urls` | `http://localhost:5142` | Where it listens; the WCS host's `Wms:ApiAddress` must point here. Also `--urls` or the `URLS` environment variable. |
| `SampleWms:ApiKey` | `sample-wms-key` | The key the WCS sends in `X-Api-Key` (the host's `Wms:ApiKey`). Public: change it before listening on anything but localhost. |
| `SampleWms:ReservationLifetimeMinutes` | `10` | A storage reservation nobody asks about for this long is an orphan, and its cell is freed. |

## The files

| File | What it holds |
|---|---|
| `WarehouseEndpoints.cs` | The 43 routes, each mapped to one method of `SampleWarehouse`, with what it means for the WCS. Start here. |
| `SampleWarehouse.cs` | The warehouse and its rules: cells, load units, positions, reservations, extraction orders. **The class to replace** with queries and updates on your WMS's database. |
| `Receipts.cs` | One receipt per `OperationId` for the four writes the WCS sends from a durable queue. |
| `ApiKeyFilter.cs` | The `X-Api-Key` check (401 otherwise). |
| `Model.cs`, `SampleData.cs` | The data and the initial warehouse: six cells `A0101`-`A0302` (ids 101-106) served by the crane `SC01`, the bay cells 800 (infeed, area type 2) and 900 (outfeed, area type 3), pallets 5001-5006. |
| `AdminEndpoints.cs` | `/sample-admin`: the sample's own routes, **not part of the contract** (below). |

The routes and the request/response records come from `WCS/Wcs.Wms.Contracts` (`WarehouseRoutes`
and the `Dto` folder), the same assembly the WCS's client is built from. A WMS in another language
implements the same paths and JSON; `WarehouseEndpoints.cs` lists the rules (404 means "none" only on
six readings; on a write, a 4xx other than 408 and 429 - 404 included - is a permanent refusal that the
WCS never retries; both casings accepted; enums as numbers).

## What a WMS must do for the WCS

**Know where the plant touches its cells.** For every cell door the WCS can reach, the WMS keeps the
door code, the field address of the door (ASI and subitem: what the crane is told) and the plant
devices through which the cell is reached. The WCS's door mappings say the same from its side (door
code -> cell -> device). `SampleData.cs` and `../plant/sample-plant.json` are the two halves of one
building, and `SamplePlantAgreesWithTheSampleWmsTest` checks that they still match.

**Answer the editor's cell list with the right area types.** When someone links a bay or another
device to a WMS cell in the WCS editor, the editor calls `GET cells?areaType=&automaticOnly=true`:
area type 2 for an Automation, Loading or Circular Distributor device, area type 3 for a Buffer or
LoadingAndBuffer device, and only cells of automatic areas. Put each bay cell in an automatic area
of the type its device asks for, or the editor's panel opens empty. In the sample the infeed bay
IN01 (Loading) has cell 800 in area 20 (type 2), the outfeed bay OUT01 (Buffer) has cell 900 in area
30 (type 3). The storage doors are listed with no area type: the storage areas.

**Choose cells, and hold them for a mission.** `POST reservations` picks a free cell reachable by the
devices the WCS names and, with `assignPosition`, writes a reserved position owned by the mission.
`GET reservations/{missionId}` returns it and renews it; `POST reservations/release` frees it. "No
free cell" is outcome 6 in a 200, not an error.

**Record the facts.** `POST load-units/allocate` (the load unit is now physically in a cell: stored,
or arrived on a device linked to a cell such as a bay) and `POST load-units/release` (it left the
automation) are facts the WCS has already acted on. They arrive from a durable queue, at least once:
keep a receipt per `OperationId` and answer a repetition without applying it again (compare the
values, not the JSON text: `5003` and `5003.0` are the same id); the same id with a different content
is 409, which the WCS parks for a person. Two fields of the allocation are easy to misread:
`storageDoorCode`, when set, names the cell and `cellId` is then ignored (key the storage on the door
code, even if today the WCS sends a matching `cellId`); `resetAssignment` means "first delete the
load unit's previous shipping-note assignment", not "drop its cell reservations" - a reservation in
another cell belongs to a mission, and the WCS gives it back itself.

**Offer work and close it.** On every turn of its master loop (about once a second) the WCS asks
`GET extractions/pending-work` and `GET notes/pending-work`. It queues what it can serve and reports
each row with `POST extractions/outcomes`: `InExtraction` while a mission is on it, `Extracted` when
the load unit leaves its cell, `NotReachable`, `InOtherExtraction` or `LoadUnitLocked` when it cannot
serve it. **The "extracted" report comes from a durable queue with `extractionHeaderId` 0**: find the
rows by their own id. The WMS closes an order itself when nothing is left to extract.

**Answer the readings.** Cell descriptions, partitions (field addresses), positions, busy state,
storing depth, load unit descriptions, locations: see the comments in `WarehouseEndpoints.cs`.

## What the sample simplifies

- Every cell holds one load unit at one depth: its only partition is `0000` at offset -1 (what the
  WCS asks for a crane with a fixed storing depth), and "storing constraints" answer the cell's
  depth (1200) when it is free, 0 when it is taken.
- There are no shipping notes: `notes/pending-work` is always empty, and the five facts about note
  assignments are written in the journal and change nothing.
- A row the WCS cannot serve stays offered: nobody cancels it.
- An order closes as soon as every row is reported `Extracted`, and the WCS reports that when the crane
  picks the load unit from its cell, not when the load unit reaches the bay. So if an operator then
  sends the load unit back to storage, `assignments/picked-extraction-became-deposit` finds the order
  closed and answers `OrderAlreadyClosed`: the pallet is back in a cell and the order stays served.
  A real WMS would keep the order open until the load unit reaches the bay (`load-units/allocate` on
  the bay cell, 900 here), and until then reopen the row when told the extraction became a deposit.
- Everything is lost when the process stops.

## The admin routes (not part of the contract)

They stand in for the screens and the ERP of a real WMS, and need the same `X-Api-Key`.

| Route | What it does |
|---|---|
| `GET /sample-admin/state` | Cells with their positions, load units and where they are, reservations, extraction orders with the state of each row, the journal of the facts the WCS reported, and the last requests received (`recentCalls`). |
| `POST /sample-admin/extraction-orders` | Body `{ "loadUnitIds": [5001], "bayCellId": 900 }` (`bayCellId` optional, 900 by default). Creates an order the WCS picks up on its next turn. 201, or 400 with the reason (unknown load unit, already in an open order, not a bay). |
