# Machine-readable contracts

The two HTTP contracts of the WCS as OpenAPI 3.1 documents, generated from the code and checked
against it on every build. Use them to generate a client or a server stub, to validate your own
messages, or to see at a glance what changed between two versions of the WCS.

| File | What it describes | Who implements it |
|---|---|---|
| [`wcs-host.openapi.json`](wcs-host.openapi.json) | the host API: every operation under `/api/v1` | the WCS; your systems call it ([guide](../integration/host-api.md)) |
| [`wms-contract.openapi.json`](wms-contract.openapi.json) | the WMS contract: the 45 routes under `/api/wcs/warehouse/` | your WMS, or an adapter in front of it; the WCS calls it ([guide](../integration/wms-contract.md)) |
| [`examples/`](examples) | JSON request and answer bodies, validated against the two documents | |

The guides explain when and why each operation is used; these files are the exact shapes. When the
two disagree, the files win: they come from the code.

## Versions

Each document has a contract version in `info.version`, independent of the version of the WCS:

| Document | Version constant |
|---|---|
| host API | `HostApiContract.Version` in [`WCS/Wcs.Api.Contracts`](../../WCS/Wcs.Api.Contracts/src/HostApiContract.cs) |
| WMS contract | `WarehouseContract.Version` in [`WCS/Wcs.Wms.Contracts`](../../WCS/Wcs.Wms.Contracts/src/WarehouseContract.cs) |

The version is not sent on the wire. It follows semantic versioning, **seen from the other side**:

- the **major** part changes when a client of the host API, or a WMS that implements the WMS
  contract, would not survive the change without being modified;
- the **minor** part changes for anything else: a new optional field, a new host operation, a
  description.

The `v1` in the host's `/api/v1` prefix is a route, not this version: a new major contract version
does not move the routes by itself.

### What counts as breaking

The check classifies every difference from the side of the party that is not the WCS. The two
documents say which side that is in `x-wcs-provider`: `wcs` for the host API (the other party is a
client: it calls, writes requests, reads answers), `wms` for the WMS contract (the other party is the
server: it serves, reads requests, writes answers).

| Change | Host API (client) | WMS contract (WMS) |
|---|---|---|
| operation removed | breaking | compatible: the WCS no longer calls it |
| operation added | compatible | **breaking**: an existing WMS does not serve it |
| request field added | breaking if required | compatible |
| request field removed (a casing change is one) | compatible | breaking |
| answer field removed | breaking | compatible |
| answer field becomes required | compatible | breaking |
| field type changed | breaking | breaking |
| enumeration value added | breaking in answers | breaking in requests |
| enumeration value removed | breaking in requests | breaking in answers |
| a success status added or removed | breaking | breaking |
| the meaning of a 404 (`x-wcs-not-found`), the request casing | | breaking |
| the edit lease or the engine state an operation needs | breaking | |
| descriptions, problem titles, examples | compatible | compatible |

## How they are generated

[`tools/Wcs.ContractCheck`](../../tools/Wcs.ContractCheck) writes both files. Do not edit them by
hand: change the code, then run

```powershell
dotnet run --project tools/Wcs.ContractCheck/src -- update
```

- **Host API.** The tool starts the real host in memory (engine off, Development environment) and
  reads the OpenAPI document ASP.NET Core serves at `/openapi/v1.json`. It then adds what the
  framework cannot see: the contract version, the two keys as security schemes, the `X-Edit-Holder`
  header and the `423` of the routes that need the edit lease (from the endpoints' metadata), the
  `503` of the engine gate, the `409` of the routes that need the engine stopped
  (`x-wcs-engine-state`), and the real answers of the endpoints that return `IResult`, whose bodies
  and statuses the framework cannot infer. Those answers are a table written from the endpoint code
  ([`HostApiAnswers.cs`](../../tools/Wcs.ContractCheck/src/HostApiAnswers.cs)); the tool refuses an
  endpoint whose answers the framework cannot see and the table does not describe, a test asks a
  running host for every problem title the table promises, and another holds its engine states
  against the [reference](../reference/host-api.md).
- **WMS contract.** The tool calls every method of `IWarehouse` on the real client,
  `HttpWarehouse`, against a recording HTTP handler, and writes down what went on the wire: method,
  path, query, body and its casing. It answers each call with `500` and then with `404`, and records
  whether the client read the `404` as "none" or as a failure. The schemas come from .NET's
  `JsonSchemaExporter` with the serializer options the client really uses. The only knowledge added
  by hand is which routes are sent from a durable queue of `wcs.db`, named with `nameof` of the
  database's tables.

## How they are checked

```powershell
dotnet run --project tools/Wcs.ContractCheck/src -- check
```

regenerates both documents and fails (exit code 1) if they differ from the committed ones, listing
every difference as breaking or compatible, and validates every example against the regenerated
documents. A contract therefore changes only on purpose, with `update`, which itself refuses (exit
code 2) a breaking change unless the major part of the version was raised.

In CI, against the branch a pull request goes into (fetched, so that `origin/$BASE_BRANCH` exists):

```bash
mkdir -p baseline
for f in wcs-host.openapi.json wms-contract.openapi.json; do
  git show "origin/$BASE_BRANCH:guide/contracts/$f" > "baseline/$f" 2>/dev/null || rm -f "baseline/$f"
done
dotnet run --project tools/Wcs.ContractCheck/src -c Release -- check --baseline baseline
```

`--baseline` adds a second rule: the committed documents may break the baseline only with a higher
major version. In the public repository the folder is `docs/contracts`; the tool finds either, and
`--contracts <folder>` names another. The same check runs as a test,
`ContractsAreUpToDateTest` in `tools/Wcs.ContractCheck/tests`.

## What the documents say about the wire

These are the behaviours a WMS author trips on, and where each is in the WMS document.

- **Two casings in requests.** The client writes the bodies of the POSTs that read an answer with
  `JsonSerializer.Serialize` (PascalCase: `{"LoadUnitId":5001}`) and those whose answer it ignores
  with `JsonContent` (camelCase: `{"loadUnitId":5001}`). Each operation says which in
  `x-wcs-request-casing`, each request schema in `x-wcs-casing`; a schema used both ways would get a
  `.PascalCase` suffix. Read property names case-insensitively. Answers are read case-insensitively,
  and numbers in answers may also be strings.
- **Decimal identifiers.** Ids of load units, cells, areas, warehouses and rows are `format: decimal`:
  the same id comes as `5001` or `5001.0`, in bodies and in path segments. Compare them as numbers.
- **404.** `x-wcs-not-found: none` marks the six readings where a `404` means "there is none"; they
  also have a `404` answer. Everywhere else a `404` is a failure.
- **Refusals and failures.** Every operation has a `4XX` answer (a permanent refusal: every 4xx
  except 408 and 429) and a `5XX` answer (a temporary failure, retried or asked again).
- **Receipts.** `x-wcs-operation-id: true` marks the requests with an `OperationId`: keep a receipt
  per id, answer a repeated request as the first time, and answer `409` to the same id with a
  different content (the operation has a `409` answer). The empty GUID is not from a queue.
- **Durable queues.** `x-wcs-durable-queue` names the table of `wcs.db` the request is sent from, at
  least once; the operation's description says how it is retried and what a refusal does.
- **Requests that never reach the wire.** The descriptions say when the WCS answers itself (an
  empty list, an empty code) instead of calling.

And in the host document:

- **Keys.** Two security schemes on the same `X-Api-Key` header: `fullKey` for every operation,
  `readOnlyKey` for `GET` only; every write has a `403` answer for the read-only key.
- **Problems.** Every refusal is `application/problem+json`. `x-wcs-problem-titles` lists, per answer,
  the titles the host writes: they are in Italian, as the host writes them. Program against the
  status and, for `503`, the `unavailable` extension (`engineNotLoaded` or `warehouseUnreachable`).
- **409, 423, 503.** `409` on the plant writes that need the engine stopped (`x-wcs-engine-state`)
  and on the conflicts of each operation; `423` on the writes that need the edit lease
  (`x-wcs-edit-lease`); `503` on every operation except `GET /status` and the two lease routes while
  the engine is not loaded, and on those that need the WMS when it does not answer.

The SignalR hub (`/hub/plant`) is not an HTTP contract and is not in these files: see
[the host API guide](../integration/host-api.md#the-signalr-hub).

## Examples

`examples/host` and `examples/wms` hold one file per body, named after the operation:

- `{operationId}.request.json`: the request body;
- `{operationId}.response.json`: the successful answer (the first 2xx with a body);
- `{operationId}.response.{status}.json`: the answer with that status, a problem for instance.

They cover the storing cycle (describe the load unit, reserve, allocate, release the reservation),
the extraction cycle (pending work, find, report the outcome, paused rows, served, release), the
receipts (`ReleaseLoadUnitCell`, `ReportPickedExtractionBecameDeposit`) and the host's commands,
lease and errors. Most were captured on the wire between a host and the sample WMS, and they keep
the wire's form (`5001.0`). `check` and the tests validate every one against the schemas, and a test
shows that the same body in the other casing is refused.
