# The host API

How your systems drive the WCS and watch it: the HTTP API under `/api/v1`, the SignalR hub at
`/hub/plant`, and the .NET client that wraps both. Every operation is listed in the
[reference](../reference/host-api.md), and described with its schemas, keys and error answers in the
versioned OpenAPI document [`wcs-host.openapi.json`](../contracts/wcs-host.openapi.json)
([contracts](../contracts/README.md)); this page explains the rules they share, with examples
captured from a running host (key `my-wcs-key`, read-only key `my-readonly-key`; `Date`, `Server`
and `traceId` left out).

- [Keys](#keys)
- [Errors are problem details](#errors-are-problem-details)
- [Status](#status)
- [Commands](#commands)
- [Reading the plant and the missions](#reading-the-plant-and-the-missions)
- [Changing the plant](#changing-the-plant)
- [The edit lease: 423](#the-edit-lease-423)
- [The engine must be stopped: 409](#the-engine-must-be-stopped-409)
- [503: two gates](#503-two-gates)
- [The SignalR hub](#the-signalr-hub)
- [The .NET client: Wcs.Api.Client](#the-net-client-wcsapiclient)
- [OpenAPI](#openapi)

## Keys

Every request sends a key in the `X-Api-Key` header; there is no other way (the hub too, see below).

| Key | Setting | Allows |
|---|---|---|
| full | `Wcs:ApiKey` (required) | everything |
| read-only | `Wcs:ReadOnlyApiKey` (optional) | `GET` and the hub; anything else is `403` |

No route answers `HEAD` (it passes the key check, then gets `404`): probe with `GET /api/v1/status`.

The comparison takes the same time whatever the key sent. No key, or a wrong one:

```http
GET /api/v1/status HTTP/1.1
Host: 127.0.0.1:5580
```

```http
HTTP/1.1 401 Unauthorized
Content-Type: application/problem+json

{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.2","title":"Chiave assente o sbagliata","status":401}
```

("Key missing or wrong".) The read-only key on a write:

```http
POST /api/v1/commands HTTP/1.1
X-Api-Key: my-readonly-key
Content-Type: application/json

{ "commandType": 1001 }
```

```http
HTTP/1.1 403 Forbidden
Content-Type: application/problem+json

{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.4","title":"Chiave priva del permesso di scrittura","status":403}
```

("Key without write permission".) Without a key the host serves only `/openapi`, the web editor's
pages and their static files; the editor itself asks for a key before it does anything
([quick start](../quick-start.md#6-open-the-web-editor)).

## Errors are problem details

The [host API document](../contracts/wcs-host.openapi.json) lists the problem answers of every
operation, the titles each can carry (`x-wcs-problem-titles`), the routes that need the edit lease
(`x-wcs-edit-lease`, `423`) and those that need the engine stopped (`x-wcs-engine-state`, `409`).

Every refusal is `application/problem+json` (RFC 9457) with `title`, `status` and usually `detail`.
**The texts are in Italian**: the host was written for Italian operators. Program against the status
code (and, for `503`, the `unavailable` field), not against the text. The texts this guide shows are
translated next to them.

A body the host cannot read (not JSON, or a field of the wrong type) gets `400` with the title
`Richiesta non leggibile` ("request not readable") and the parser's reason in `detail`, for example
`Failed to read parameter "CommandRequest request" from the request body as JSON.`

## Status

`GET /api/v1/status` always answers, even when the engine could not be loaded:

```http
GET /api/v1/status HTTP/1.1
X-Api-Key: my-wcs-key
```

```http
HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8

{"navStatus":1,"defragmentation":0,"version":"0.1.0.0","utcNow":"2026-10-15T02:12:23.7207534Z","historyFrom":"2026-10-01","engineLoadError":null}
```

`navStatus`: 0 Stop, 1 Run, 2 Recover (loading), 3 Dispose (shutting down), 4 Pause.
`engineLoadError` is the reason the engine is not loaded (the host retries every
`Wcs:EngineLoadRetrySeconds`), `null` when it is. `historyFrom` is the first month the history can
answer.

## Commands

A command asks the engine to do something; it is queued and executed on the next turn of the master
loop. The codes and their fields are in the [command reference](../reference/commands.md).

```http
POST /api/v1/commands HTTP/1.1
X-Api-Key: my-wcs-key
Content-Type: application/json

{ "commandType": 1001 }
```

```http
HTTP/1.1 201 Created
Content-Type: application/json; charset=utf-8
Location: /api/v1/commands/1

{"id":1,"date":"2026-10-15T04:12:20.7137258+02:00","commandType":1001,"itemId":null,"loadUnitId":null,"storageDoorCode":null,"status":0,"errorDescription":null,"assignmentGuid":null,"info":null,"showStatus":0,"workstationId":null,"processId":null,"bufferCellId":0,"pickUpDoorCode":null,"loadUnitTypeId":0,"userId":null,"destinationItemId":null,"referenceId":null,"fromApi":true,"apiStatus":0}
```

`201` means queued, not done. Read the outcome with `GET /api/v1/commands/{id}` (or the hub's
`CommandChanged`): `status` 2 is done, 3 is a warning (nothing done, `errorDescription` says why),
99 is an error.

```http
GET /api/v1/commands?take=5 HTTP/1.1
X-Api-Key: my-wcs-key
```

```http
HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8

[{"id":2,"date":"2026-10-15T04:12:24.2148172","commandType":3002,"itemId":"5a000000-0000-4000-8000-000000000001","loadUnitId":5001.0,"storageDoorCode":null,"status":2,"errorDescription":"","assignmentGuid":null,"info":null,"showStatus":0,"workstationId":null,"processId":null,"bufferCellId":0.0,"pickUpDoorCode":null,"loadUnitTypeId":0.0,"userId":null,"destinationItemId":null,"referenceId":"","fromApi":true,"apiStatus":0},{"id":1,"date":"2026-10-15T04:12:20.7137258","commandType":1001,"itemId":null,"loadUnitId":null,"storageDoorCode":null,"status":2,"errorDescription":"","assignmentGuid":null,"info":null,"showStatus":0,"workstationId":null,"processId":null,"bufferCellId":0.0,"pickUpDoorCode":null,"loadUnitTypeId":0.0,"userId":null,"destinationItemId":null,"referenceId":"","fromApi":true,"apiStatus":0}]
```

Refusals before queueing:

| Case | Answer |
|---|---|
| unknown `commandType` | `400`, `"detail":"Tipo di comando sconosciuto: 1234"` |
| `status` other than 0 or 2 | `400` |
| code 2000 or more (except the operator traces 4002-4006) while the engine is not in Run | `503`, `"detail":"Il motore è in Stop: il comando 3002 richiede Run"` ("the engine is in Stop: command 3002 needs Run") |

```http
HTTP/1.1 503 Service Unavailable
Content-Type: application/problem+json

{"type":"https://tools.ietf.org/html/rfc9110#section-15.6.4","title":"Service Unavailable","status":503,"detail":"Il motore è in Stop: il comando 3002 richiede Run"}
```

After `1001` the engine reaches Run on its next turn: wait for `navStatus` 1 before sending the
commands that need it.

## Reading the plant and the missions

The live state of every device, `GET /api/v1/plant/items/status` (one entry shown):

```json
[{"itemId":"5a000000-0000-4000-8000-000000000007","asi":"1005","partition":"0001","subitem":"0001","name":"OUT01","status":0,"luLoadStatus":1,"loadedLuCode":"LU005001","loadedLuId":5001.0,"runningStepId":"00000000-0000-0000-0000-000000000000","waitingStepId":"00000000-0000-0000-0000-000000000000"}]
```

`status` is the device status (0 Ready ... 6 OffLine, 89 Warning, 99 Error), `luLoadStatus` 1 means
it holds a load unit.

The active missions, `GET /api/v1/missions` (right after command 3002 on IN01):

```json
[{"missionId":"cc9d3865-9c4c-4b3c-8610-48074197d392","loadUnitId":5001.0,"loadUnitCode":"LU005001","positionItemId":"5a000000-0000-4000-8000-000000000001","sourceItemId":"5a000000-0000-4000-8000-000000000001","destinationItemId":null,"date":"2026-10-15T04:12:24.5720816+02:00","statusDescription":"","storageDoorCode":"","pickUpDoorCode":"","status":1,"commandId":2,"relocateItemId":null,"missionType":5,"movingDescription":"","operationType":0,"bufferCellId":0,"extractionQueueId":0,"loadUnitTypeId":1,"groupedMovementId":null,"handlingMode":0,"gatewayItemId":null,"sourceAsi":null,"sourceSubitem":null,"sourcePartition":null,"destinationAsi":null,"destinationSubitem":null,"destinationPartition":null,"noteAssignmentId":0,"assignmentGuid":null,"noteProcessingId":0,"activeFilter":false,"tare":25,"reallocatedDestination":false,"operatorDestination":false,"pendingChange":null,"pendingChangeItemId":null,"relocatorArrival":null}]
```

A mission disappears from this list when it ends (the load unit stored, or unloaded from the plant).
What happened stays in the operators' log, `GET /api/v1/log?from=...&to=...` (newest first; one
entry shown):

```json
{"id":6,"date":"2026-10-15T04:12:28.7394406","itemId":"5a000000-0000-4000-8000-000000000004","asi":"2001","partition":"0001","subitem":"0001","status":4,"description":"Stored LU LU005001 from 2001.0001.0001 to 3001.0001.0001","destinationItemId":"5a000000-0000-4000-8000-000000000005","missionId":"cc9d3865-9c4c-4b3c-8610-48074197d392","missionStart":"2026-10-15T04:12:28.2256271","missionEnd":"2026-10-15T04:12:28.7388404","missionStatus":1,"runningItemId":"5a000000-0000-4000-8000-000000000004"}
```

and in the handled load units, `GET /api/v1/handled-load-units?from=...&to=...`:

```json
[{"id":2,"missionId":"6936037d-fa39-49df-91fa-e84fd4cee2f9","type":2,"date":"2026-10-15T04:12:35.2768257","cellId":900.0,"loadUnitCode":"LU005001","processId":null,"resultDescription":null,"loadUnitProcessId":null,"noteHeaderId":null,"extractionHeaderId":null,"loadUnitId":5001.0},{"id":1,"missionId":"cc9d3865-9c4c-4b3c-8610-48074197d392","type":1,"date":"2026-10-15T04:12:24.6208063","cellId":800.0,"loadUnitCode":"LU005001","processId":null,"resultDescription":null,"loadUnitProcessId":null,"noteHeaderId":null,"extractionHeaderId":null,"loadUnitId":5001.0}]
```

`from` and `to` are local times (`2026-10-15T03:12:38`); the log `status` is a log level (1 error,
2 warning, 4 information).

## Changing the plant

The plant is changed through the web editor or the `/plant/...` routes. Two gates protect every
write: the edit lease (`423`) and the engine state (`409`). The whole plant is exported with
`GET /api/v1/plant/graph` and replaced with `PUT /api/v1/plant/graph`; see
[plant modelling](plant-modelling.md#import-and-export).

## The edit lease: 423

One writer at a time. Every route that changes the plant (all writes under `/plant/`, except the two
lease routes and `POST /plant/graph/legacy-geometry`, plus `PUT /plc/connections`) requires the
header `X-Edit-Holder` naming the current holder of the lease. Without it:

```http
PUT /api/v1/plant/graph HTTP/1.1
X-Api-Key: my-wcs-key
Content-Type: application/json

{ "items": [ ... ] }
```

```http
HTTP/1.1 423 Locked
Content-Type: application/problem+json

{"title":"Nessuno tiene il permesso di modifica","status":423,"detail":"Questa scrittura cambia la forma dell'impianto ed esige il permesso di modifica, che al momento non tiene nessuno. Chiedilo con POST /plant/edit-lock prima di riprovare."}
```

("Nobody holds the edit permission ... ask for it with POST /plant/edit-lock".) Take the lease:

```http
POST /api/v1/plant/edit-lock HTTP/1.1
X-Api-Key: my-wcs-key
Content-Type: application/json

{ "holder": "erp-integration" }
```

```http
HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8

{"holder":"erp-integration","sinceLocal":"2026-10-15T04:12:19.0996716+02:00","expiresLocal":"2026-10-15T04:13:19.0996716+02:00","isMine":true}
```

The lease lasts 60 seconds; asking again, or any write that passes the gate, renews it. Someone else
asking while you hold it gets `409` with your name:

```http
HTTP/1.1 409 Conflict
Content-Type: application/json; charset=utf-8

{"holder":"erp-integration","sinceLocal":"2026-10-15T04:12:19.0996716+02:00","expiresLocal":"2026-10-15T04:13:19.0996716+02:00","isMine":false}
```

Write with the header, then give the lease back:

```http
PUT /api/v1/plant/graph HTTP/1.1
X-Api-Key: my-wcs-key
X-Edit-Holder: erp-integration
Content-Type: application/json

{ "items": [ ... ], "geometries": [ ... ], "junctions": [ ... ], "doorMappings": [ ... ] }
```

```http
HTTP/1.1 204 No Content
```

```http
DELETE /api/v1/plant/edit-lock?holder=erp-integration HTTP/1.1
X-Api-Key: my-wcs-key
```

```http
HTTP/1.1 204 No Content
```

The web editor takes the same lease when it writes: a script and an open editor can refuse each
other with `409` on the lease route. The lease is a coordination tool, not authentication.

## The engine must be stopped: 409

Most plant writes are refused while the engine is in Run, Recover or Dispose; some (creating a
device, changing a connection, importing a plant, the driver settings) only work in Stop. The
[reference](../reference/host-api.md) says which. With the lease held, in Run:

```http
HTTP/1.1 409 Conflict
Content-Type: application/problem+json

{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.10","title":"Il motore non è fermo","status":409,"detail":"sostituzione dell'impianto: il motore è in Run, e un impianto si importa solo a motore fermo (Stop): i dispositivi nuovi nascono senza connessione, e il motore gliela assegna solo quando apre le connessioni: la ripresa da Pause non le riapre"}
```

("The engine is not stopped. Replacing the plant: the engine is in Run, and a plant is imported only
with the engine stopped ...".) Stop it with command 1002 and wait for `navStatus` 0.

`PUT /api/v1/plant/graph` is also refused with `409` while there are active missions or rows in the
extraction queue (title `Impossibile sostituire il grafo con lavoro pendente`, "cannot replace the
graph with pending work").

## 503: two gates

| `unavailable` | When | What to do |
|---|---|---|
| `engineNotLoaded` | every route except `/status` and the lease routes, while the engine is not loaded (title `Il motore non è caricato`) | read `engineLoadError` in `/status`; the host retries the load by itself |
| `warehouseUnreachable` | a route that needs the WMS (`/wms/...`, `/missions/{id}/storage-cells`, ...) and the WMS did not answer | check your adapter |

With the sample WMS stopped:

```http
GET /api/v1/wms/cells?areaType=2&automaticOnly=true HTTP/1.1
X-Api-Key: my-wcs-key
```

```http
HTTP/1.1 503 Service Unavailable
Content-Type: application/problem+json

{"type":"https://tools.ietf.org/html/rfc9110#section-15.6.4","title":"Il gestionale non è raggiungibile dal motore","status":503,"detail":"Il gestionale non ha risposto a «GetCellsForEditor».","unavailable":"warehouseUnreachable"}
```

("The WMS cannot be reached by the engine. The WMS did not answer GetCellsForEditor".) A third `503`,
without `unavailable`, is the command refusal shown above.

## The SignalR hub

`/hub/plant` pushes changes as they happen. It is server-to-client only
([`IPlantEvents`](../../WCS/Wcs.Api.Contracts/src/IPlantEvents.cs)):

| Message | Arguments | When |
|---|---|---|
| `ItemStatusChanged` | `ItemStatus` | a device changes status or load |
| `NavigatorStatusChanged` | `EngineStatus` | the engine changes state; also when the host's event queue dropped events: reload the full state |
| `MissionChanged` | `MissionDto`, `MissionChange` (0 Started, 1 StepChanged, 2 Completed, 3 Removed, 4 PositionChanged) | a mission starts, moves, ends |
| `ExtractionQueueChanged` | `ExtractionQueueItemDto`, `QueueChange` (0 Added, 1 Updated, 2 Removed) | the extraction queue changes |
| `CommandChanged` | `CommandDto` | a command changes status |
| `Watchdog` | `DateTime` (UTC) | every second: the host is alive |

The rule for a client: read the full state with `GET` first, then apply the changes; after every
reconnection, read it again.

**The key goes in the `X-Api-Key` header, also for the hub.** The negotiation request:

```http
POST /hub/plant/negotiate?negotiateVersion=1 HTTP/1.1
X-Api-Key: my-wcs-key
```

```http
HTTP/1.1 200 OK
Content-Type: application/json

{"negotiateVersion":1,"connectionId":"FmAOYPqOrNjCNwg4LDFJPQ","connectionToken":"qZzNNo4pB8umwnM8nt2cUQ","availableTransports":[{"transport":"WebSockets","transferFormats":["Text","Binary"]},{"transport":"ServerSentEvents","transferFormats":["Text"]},{"transport":"LongPolling","transferFormats":["Text","Binary"]}]}
```

The read-only key is accepted too. The `access_token` query parameter, which SignalR's JavaScript
client uses because a browser cannot set headers on a WebSocket, is refused, and the host says why:

```http
HTTP/1.1 401 Unauthorized
Content-Type: application/problem+json

{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.2","title":"Chiave nell'URL non più accettata","status":401,"detail":"Il parametro access_token non è più supportato: la chiave va mandata nell'header X-Api-Key. Aggiorna il client insieme all'host."}
```

So a browser page cannot connect to the hub directly: connect from your server (with the .NET client
or any SignalR client that can set headers) and relay to your pages.

## The .NET client: Wcs.Api.Client

[`Wcs.Api.Client`](../../WCS/Wcs.Api.Client/src) has one method per route (`WcsClient`) and wraps
the hub (`PlantEventsConnection`, with automatic reconnection that never gives up). Reference the
project from a console project next to your clone of this repository. Create it first:

```powershell
dotnet new console -n WcsClientDemo -f net10.0
```

then add the reference to `WcsClientDemo.csproj` (adjust the relative path):

```xml
<ItemGroup>
  <ProjectReference Include="..\WCS\WCS\Wcs.Api.Client\src\Wcs.Api.Client.csproj" />
</ItemGroup>
```

Here `..\WCS` is the clone, next to the `WcsClientDemo` folder.

**The preview is source-only: `Wcs.Api.Client` is not on NuGet yet**, and packages will come with a
later release. Clone the repository **at the release tag** of the host you talk to
(`git clone --branch v0.1.0-preview.1 https://github.com/Sun-Tech-Software/WCS.git WCS`), or add it to
your repository as a git submodule pinned to that tag, and reference the project from there
([README](../../README.md#using-the-libraries-from-your-code)). To upgrade, move the clone or the
submodule to the new tag and read [what may change](../versioning-and-compatibility.md#host-api-and-net-client).

This program puts pallet 5002 on IN01 of the sample plant and prints the mission events until it is
stored. Replace the contents of `WcsClientDemo\Program.cs` with it, then `dotnet run` (it compiles
against this repository and was run against the quick-start setup):

```csharp
using Wcs.Api.Client;
using Wcs.Api.Contracts;
using Wcs.Api.Contracts.Dtos;
using static Wcs.Common.Enums;

var options = new WcsClientOptions
{
    BaseAddress = new Uri(args.Length > 0 ? args[0] : "http://127.0.0.1:5580"),   // origin only, no path
    ApiKey = Environment.GetEnvironmentVariable("WCS_API_KEY") ?? "my-wcs-key"
};

using var wcs = new WcsClient(options);
await using var events = new PlantEventsConnection(options);

var stored = new TaskCompletionSource();
events.MissionChanged += (mission, change) =>
{
    Console.WriteLine($"{change,-15} LU {mission.LoadUnitId:0} {mission.MissionType,-12} at {mission.PositionItemId}");
    // The engine removes a mission when its load unit is stored.
    if (change is MissionChange.Completed or MissionChange.Removed) stored.TrySetResult();
};
await events.StartAsync();

var status = await wcs.GetStatusAsync();
Console.WriteLine($"Engine {status.NavStatus}, version {status.Version}");
if (status.NavStatus != NavigatorStatusTypes.Run)
{
    Console.WriteLine("Start the engine first (command 1001).");
    return;
}

try
{
    var command = await wcs.EnqueueCommandAsync(new CommandRequest(
        CommandType: 3002,                                                  // LoadLu
        ItemId: Guid.Parse("5a000000-0000-4000-8000-000000000001"),         // IN01
        LoadUnitId: 5002));
    Console.WriteLine($"Command {command.Id} queued");
}
catch (WcsApiException ex)
{
    Console.WriteLine($"Refused: {(int)ex.StatusCode} {ex.Body}");
    return;
}

await stored.Task.WaitAsync(TimeSpan.FromMinutes(1));
```

Its output:

```text
Engine Run, version 0.1.0.0
Command 5 queued
Started         LU 5002 None         at 
PositionChanged LU 5002 MovingToItem at 5a000000-0000-4000-8000-000000000001
PositionChanged LU 5002 MovingToItem at 5a000000-0000-4000-8000-000000000002
PositionChanged LU 5002 MovingToItem at 5a000000-0000-4000-8000-000000000003
PositionChanged LU 5002 MovingToItem at 5a000000-0000-4000-8000-000000000004
PositionChanged LU 5002 MovingToItem at 5a000000-0000-4000-8000-000000000005
Removed         LU 5002 MovingToItem at 5a000000-0000-4000-8000-000000000005
```

Things to know about the client:

- `WcsClientOptions.BaseAddress` must be an origin without path: the client appends `/api/v1/` and
  `/hub/plant` itself.
- A non-success status raises `WcsApiException` with `StatusCode` and the raw `Body`;
  `GeometryConflictException` (a `409` on a geometry write) carries the conflicting device ids.
  The `Get...Async` methods that can find nothing return `null` on `404`.
- `EditHolder`: set it after `AcquireEditLockAsync`, and every request carries `X-Edit-Holder`.
- `PlantEventsConnection` raises `Reconnecting`, `Reconnected` and `Closed`; reload the state on
  `Reconnected`.
- `WcsClient.Dispose` disposes the `HttpMessageHandler` you pass to its constructor.

## OpenAPI

When the host does not run in the `Production` environment it serves the OpenAPI document of every
route at `/openapi/v1.json`, without a key. In Production it is not mapped.

To generate a client in another language, prefer the committed
[`wcs-host.openapi.json`](../contracts/wcs-host.openapi.json): the same document completed with what
ASP.NET Core cannot see (the two keys, the edit lease, the real answers and problem titles of every
operation) and versioned (`HostApiContract.Version`); CI fails when the code and the file disagree
([contracts](../contracts/README.md)).
