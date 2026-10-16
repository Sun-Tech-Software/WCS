# Writing a WMS adapter

An *adapter* is the HTTP service that answers the [45 routes](../reference/wms-routes.md) on behalf
of your WMS. It can be part of your WMS or a separate service in front of its database. This page
takes you from the sample WMS to a real adapter, in the order that gets a load unit stored first.

Read [the WMS contract](wms-contract.md) alongside: it shows every request and answer on the wire.

## 1. Start from the sample

[`samples/Wcs.SampleWms`](../../samples/Wcs.SampleWms/README.md) answers all 45 routes from an
in-memory warehouse, and the WCS stores and extracts load units against it (that is the
[quick start](../quick-start.md)). Its files map one to one onto what you write:

| Sample file | What it is | In your adapter |
|---|---|---|
| [`WarehouseEndpoints.cs`](../../samples/Wcs.SampleWms/WarehouseEndpoints.cs) | the 45 routes, each mapped to one method, with what it means | keep as it is |
| [`ApiKeyFilter.cs`](../../samples/Wcs.SampleWms/ApiKeyFilter.cs) | the `X-Api-Key` check, before any body is bound | keep, read the key from your secret store |
| [`Receipts.cs`](../../samples/Wcs.SampleWms/Receipts.cs) | one receipt per `OperationId` | replace the dictionary with a table, written in the same transaction as the change |
| [`SampleWarehouse.cs`](../../samples/Wcs.SampleWms/SampleWarehouse.cs) | the warehouse and its rules | **replace** with queries and updates on your database |
| [`Model.cs`](../../samples/Wcs.SampleWms/Model.cs), [`SampleData.cs`](../../samples/Wcs.SampleWms/SampleData.cs) | the data | your tables |
| [`AdminEndpoints.cs`](../../samples/Wcs.SampleWms/AdminEndpoints.cs) | `/sample-admin`, not part of the contract | drop: your screens and ERP create orders |

In .NET, reference [`Wcs.Wms.Contracts`](../../WCS/Wcs.Wms.Contracts/src) (one project, no
dependencies) for the route constants and the request and answer records, as the sample does:

```xml
<ItemGroup>
  <ProjectReference Include="..\..\WCS\Wcs.Wms.Contracts\src\Wcs.Wms.Contracts.csproj" />
</ItemGroup>
```

**The WCS is source-only: `Wcs.Wms.Contracts` is not on NuGet yet**, and packages will come with a
later release. Reference the project from a copy of this repository **at a release tag** (the version
your WCS host runs), never at a moving branch: a clone checked out at the tag
(`git clone --branch v0.1.0 https://github.com/Sun-Tech-Software/WCS.git`) or a git
submodule of your repository pinned to the tag, with the `ProjectReference` path adjusted to where it
lies ([README](../../README.md#using-the-libraries-from-your-code)). Moving to a new version is moving
the clone or the submodule to the new tag, after reading what changed in the
[WMS contract](../versioning-and-compatibility.md#wms-contract).

In another language, implement the same paths and JSON: the records of
[`Wcs.Wms.Contracts/src/Dto`](../../WCS/Wcs.Wms.Contracts/src/Dto) are the schema, the
[reference](../reference/wms-routes.md) lists them route by route, and the rules on casing, ids and
status codes are in [what is on the wire](wms-contract.md#what-is-on-the-wire).

The host of the sample is three lines of setup and two calls (from
[`SampleWmsApp.cs`](../../samples/Wcs.SampleWms/SampleWmsApp.cs)):

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<ApiKeyFilter>();
builder.Services.AddProblemDetails();
// ... your warehouse service and your receipts

var app = builder.Build();
app.UseApiKey();               // X-Api-Key on /api/wcs/warehouse, before any binding (401 otherwise)
app.MapWarehouseContract();    // the 45 routes
app.Run();
```

and a route is one line that calls your code (from
[`WarehouseEndpoints.cs`](../../samples/Wcs.SampleWms/WarehouseEndpoints.cs)):

```csharp
// 36. Code, type, handling mode and dimensions. 404 = no such load unit.
api.MapGet(WarehouseRoutes.LoadUnit, (decimal loadUnitId, SampleWarehouse wms) =>
    wms.DescribeLoadUnit(loadUnitId) is { } description ? Results.Ok(description) : LoadUnitNotFound(loadUnitId));

// 21. Fact: the load unit is now physically in this cell. Receipt per OperationId; 400 = can never be applied.
api.MapPost(WarehouseRoutes.AllocateLoadUnit, (AllocateLoadUnitRequest request, SampleWarehouse wms, Receipts receipts) =>
    receipts.Once(request.OperationId, WarehouseRoutes.AllocateLoadUnit, request, () =>
        wms.AllocateLoadUnit(request) is { } refusal ? Results.Problem(refusal, statusCode: StatusCodes.Status400BadRequest) : Results.Ok()));
```

`decimal loadUnitId` binds both `5001` and `5001.0` from the path.

## 2. The data your WMS must hold for the WCS

Most of it you already have. What is specific to the WCS:

| Data | Why | In the sample |
|---|---|---|
| For each cell **door** (A, and B if the cell has two): its door code | the WCS names cells by door code in reservations, stores and commands; `doors/{doorCode}/cells` must find it | `DoorCodeA` `A0101` ... |
| For each door: the **field address** of the position the relocator goes to (ASI and subitem) | `cells/{id}/partitions` (`asiDoorA`, `subitemDoorA`, ...): the address the WCS sends the machine | ASI `3001`, subitem `0101` = column 01, floor 01 |
| For each cell: its **depth positions** (partitions with an offset) | a fixed-depth relocator stores at offset `-1`; a deep cell lists one partition per depth | one partition `0000` at offset `-1` |
| For each cell: the **plant devices** through which it is reached (GUIDs from the WCS) | `reservations` filters cells by `ReachableDeviceIds` | ST01 and SC01 |
| The **bay cells** linked to plant devices, in automatic areas of type 2 (infeed and other automation devices) or 3 (outfeed, buffers) | the WCS reports a load unit arriving on a linked device as allocated in that cell; the web editor lists them by area type | cells 800 and 900 |
| Reserved positions **owned by a mission**, with the time they were last asked about | reservations live as long as the WCS asks for them; orphans must expire | `ReservationLifetimeMinutes` = 10 |
| Receipts by `OperationId` | the durable queues deliver at least once | `Receipts` |

The device GUIDs and door codes are the same on both sides: the WCS's door mappings say "door code
-> cell -> device", your cells say "door code -> field address, devices". Read the device ids from
the WCS (`GET /api/v1/plant/items`, or the web editor) and store them next to your cells.
[`SamplePlantAgreesWithTheSampleWmsTest`](../../samples/Wcs.SampleWms.Test/SamplePlantAgreesWithTheSampleWmsTest.cs)
checks that the sample's two halves still agree; write the same check for your plant.

## 3. Implement in this order

**Storing a load unit** (enough for command 3002 on an infeed bay):

| # | Route | Notes |
|---|---|---|
| 36 | `GET load-units/{id}/description` | `404` for an unknown load unit |
| 1 | `GET cells/{id}` | also for the bay cells |
| 21 | `POST load-units/allocate` | with a door code, the door names the cell and `cellId` is ignored; without, `cellId` is the cell (a bay). Receipt. |
| 23 | `GET warehouses/{id}/topology` | empty topology for an unknown warehouse, not `404` |
| 2 | `POST reservations` | choose a free cell reachable by `ReachableDeviceIds`, in `SearchAreaIds`, not in `ExcludedCellIds`; with `AssignPosition`, write a reserved position owned by `MissionId`; no cell = `outcome` 6 in a `200` |
| 3 | `GET reservations/{missionId}` | renew; `404` when the mission holds nothing |
| 4 | `POST reservations/release` | idempotent |
| 27 | `GET cells/{id}/partitions` | the field address |
| 28 | `GET doors/{doorCode}/cells` | `[]` for an unknown door |
| 34, 35 | `POST cells/busy-state`, `POST cells/storing-constraints` | `theoreticalStoreDepth` 0 = no room |
| 8 | `GET load-units/{id}/location` | `404` when in no cell |

**Extracting** (pending orders):

| # | Route | Notes |
|---|---|---|
| 13 | `GET extractions/pending-work` | orders ready for the automation; keep offering a row until it is `Extracted` |
| 24 | `POST load-units/find` | `404` = not within the limits |
| 26 | `POST load-units/pickup-door` | door and obstacles |
| 15 | `POST extractions/outcomes` | rows by `extractionRowId`; **header can be 0**; close the order yourself |
| 14 | `POST extractions/paused-among` | ids no longer in progress |
| 22 | `POST load-units/release` | the load unit left; receipt |
| 12 | `POST load-units/left-extraction-queue` | the reserved position goes |

**Then the rest**: shipping notes (9, 10, 11, 16-19) if you have them (answer `[]` to
`notes/pending-work` otherwise); operator decisions (5, 6, 7, 20); readings for commands (25, 29-33,
37-41); the two editor routes (42, 43). The check of a precise cell (44) and the missing note
assignments (45) are optional: without them, answer `404` or `501` and the WCS behaves as it did before
the routes existed.

A route you have not implemented yet must answer a 5xx such as **`503`** (or `501`), not `404` or any
other 4xx: the WCS retries every 5xx (and `408`, `429`), and parks or drops any other 4xx ([answers, refusals and failures](wms-contract.md#answers-refusals-and-failures)).

## 4. Receipts in a real database

The rule: same `OperationId` and same values -> the first answer, nothing applied; same id and
different values -> `409`; empty id -> no receipt. A possible shape (adapt to your database):

```sql
CREATE TABLE WcsReceipts (
    OperationId  UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    Route        VARCHAR(100)     NOT NULL,
    RequestJson  NVARCHAR(MAX)    NOT NULL,   -- normalised: compare values, not text
    AnswerStatus INT              NOT NULL,
    AnswerBody   NVARCHAR(MAX)    NULL,
    ReceivedAt   DATETIME2        NOT NULL
);
```

Insert the receipt in the same transaction that applies the change; on a duplicate key, read the
stored receipt and compare. Compare values: `5005` and `5005.0` are the same id, and the WCS sends
the same fact with the same values each time. Only a successful answer becomes a receipt: a refusal
applied nothing, and a corrected request with the same id must be able to go through. The sample
does exactly this in memory:

```csharp
public IResult Once<TRequest>(Guid operationId, string route, TRequest request, Func<IResult> apply)
    where TRequest : notnull
{
    if (operationId == Guid.Empty) return apply();

    lock (_gate)
    {
        if (_receipts.TryGetValue(operationId, out var receipt))
        {
            return receipt.Route == route && receipt.Request.Equals(request)
                ? receipt.Answer
                : Results.Problem(
                    $"Operation {operationId} was already received with a different content. The first one was applied; " +
                    "this one is refused.",
                    statusCode: StatusCodes.Status409Conflict, title: "Same OperationId, different request");
        }

        var answer = apply();
        if (answer is IStatusCodeHttpResult { StatusCode: >= 200 and < 300 })
            _receipts[operationId] = new Receipt(route, request, answer);
        return answer;
    }
}
```

## 5. Test it with the WCS's own client

The WCS calls your adapter through [`HttpWarehouse`](../../WCS/Wcs.Wms.Client/src/HttpWarehouse.cs).
Call your adapter through the same class in a test: what your test reads is what the WCS reads,
casing and status codes included. From
[`SampleWarehouseContractTest.cs`](../../samples/Wcs.SampleWms.Test/SampleWarehouseContractTest.cs):

```csharp
using Wcs.Wms.Client;
using Wcs.Wms.Contracts;

var wms = new HttpWarehouse(
    new HttpClient { BaseAddress = new Uri("http://localhost:5142"), Timeout = TimeSpan.FromSeconds(30) },
    new HttpWarehouseOptions { ApiKey = "sample-wms-key" });

Assert.Equal(new LoadUnitDescription(5001, "LU005001", 1, 0, 25, 1000, 800, 1200, 300),
    await wms.DescribeLoadUnitAsync(5001));
var map = await wms.GetCellPartitionMapAsync(101);
Assert.Equal(("3001", "0101", ""), (map.AsiDoorA, map.SubitemDoorA, map.AsiDoorB));
Assert.Null(await wms.LocateLoadUnitAsync(5002));        // 404 -> null on this route

// A wrong key: the client raises WarehouseUnavailableException with the 401 inside.
var stranger = new HttpWarehouse(new HttpClient { BaseAddress = new Uri("http://localhost:5142") },
    new HttpWarehouseOptions { ApiKey = "not-the-key" });
var refused = await Assert.ThrowsAsync<WarehouseUnavailableException>(() => stranger.DescribeCellAsync(101));
Assert.True(refused.IsPermanentRejection);
```

A test project needs a reference to
[`Wcs.Wms.Client`](../../WCS/Wcs.Wms.Client/src/Wcs.Wms.Client.csproj) (which brings
`Wcs.Wms.Contracts`). The sample's test `EveryOperationOfTheContractAnswers` calls all 45 operations
this way; copy it and change the expected values to your data.

Then point a WCS host at your adapter and repeat the quick start with your plant:

```powershell
$env:Wms__ApiAddress = 'http://wms-adapter.plant.local:8080'   # origin only
$env:Wms__ApiKey = 'the-key-your-adapter-expects'
```

[`QuickStartEndToEndTest`](../../samples/Wcs.SampleWms.Test/QuickStartEndToEndTest.cs) does the
quick start automatically (it starts a host process on a free port, loads the plant, stores and
extracts a pallet, and checks each step through `HttpWarehouse`): it is a template for an end-to-end
test of your adapter.

To see what your adapter answers by hand:

```powershell
$wms = @{ 'X-Api-Key' = 'sample-wms-key' }
Invoke-RestMethod http://localhost:5142/api/wcs/warehouse/load-units/5001.0/description -Headers $wms
Invoke-RestMethod -Method Post http://localhost:5142/api/wcs/warehouse/reservations -Headers $wms -ContentType application/json `
  -Body '{"MissionId":"11111111-1111-4111-8111-111111111111","LoadUnitId":5004.0,"WarehouseId":1,"SearchAreaIds":[10],"ExcludedCellIds":[],"ReachableDeviceIds":["5a000000-0000-4000-8000-000000000004"],"AreaSelectionMode":0,"CellSearchMode":1,"CellFullnessPreference":0,"SearchOperatorType":1,"AssignPosition":true,"VerifyPosition":false,"RequestedByWorkstationId":"KazamControl","RequestedByUserId":"KazamControl.Process"}'
```

## Checklist

- [ ] Every route checks `X-Api-Key` before reading the body; a wrong key is `401`.
- [ ] Ids are parsed as numbers, in paths too (`load-units/5001.0/description`).
- [ ] Request property names are read case-insensitively (PascalCase and camelCase both arrive).
- [ ] Enums are numbers.
- [ ] `404` only on the six readings that allow it; `[]` for empty lists; outcomes in the body where
      the contract says so (`reservations` outcome 6, `reservations/cell`, `find-by-reference`, ...).
- [ ] 4xx only for requests that can never succeed; 5xx for anything temporary or not implemented.
- [ ] Receipts per `OperationId` on `load-units/allocate`, `load-units/release`,
      `reservations/release-load-unit-cell`, `assignments/picked-extraction-became-deposit`; `409` on
      the same id with different values.
- [ ] `load-units/allocate` with a door code keys on the door code; without, on `cellId`.
- [ ] Reservations: one reserved position per load unit, owned by the mission, renewed by
      `GET reservations/{missionId}`, expired when nobody asks.
- [ ] `extractions/outcomes`: rows found by `extractionRowId` (header can be 0); the WMS closes the
      order; an empty list is legal.
- [ ] Pending work answers only rows that are ready; a row is offered until it is `Extracted`.
- [ ] Bay cells in automatic areas of type 2 or 3; door codes unique; partitions with the offsets
      your relocators use.
- [ ] The device GUIDs in your cells match the WCS plant (a check like
      `SamplePlantAgreesWithTheSampleWmsTest`).
- [ ] Answers within `Wms:TimeoutSeconds` (30 s by default): `pending-work` is asked about once a
      second in Run.
