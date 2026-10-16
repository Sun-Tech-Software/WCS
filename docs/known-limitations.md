# Known limitations

This repository is at version **0.1.0**, the first release after the public preview. The engine behind
it has run real plants, but this public form of it (the host, the web editor, the contracts, the sample
WMS and the guides) is new, and before 1.0 a minor version may change it in incompatible ways
([versioning and compatibility](versioning-and-compatibility.md)). This page lists what the WCS does not
do, or does only partly, today. Each point was checked against the code of this repository.

Three ways of using the WCS appear throughout these guides:

- **demo**: one machine, the sample WMS, the loopback driver, nothing physical;
- **test bench (commissioning)**: a real WMS adapter and, step by step, real machines, with people
  watching the plant and nobody depending on it yet;
- **production**: the plant works on it.

Most limits below matter only for production; the table says where.

## Summary

| Limit | Matters for | More below |
|---|---|---|
| A fact the WMS refused for good is parked, and there is no API to inspect, retry or discard it | test bench, production | [Parked facts](#parked-facts-towards-the-wms) |
| One shared full key and one optional read-only key: no per-operator identity, no roles | production | [Authentication and identity](#authentication-and-identity) |
| A key change takes effect only at the next start of the host | production | [Authentication and identity](#authentication-and-identity) |
| Importing a plant does not rebuild the driver connections: Run is refused until command 1003 | test bench, production | [Plant import and connections](#plant-import-and-connections) |
| No driver for real machines is included | test bench, production | [Drivers](#drivers) |
| Windows 11 x64 is verified; Windows Server, Linux and ARM64 are not | all | [Platform](#platform) |
| No published hardware requirements; `wcs.db` keeps every command and grows without bound | production | [Sizing and growth](#sizing-and-growth) |
| Little is observable from outside: parked facts and queue backlogs only through `Wcs.DbTool verify`, no write-queue counters in the API | test bench, production | [Monitoring](#monitoring) |
| The web editor needs the Development environment or a published host | all | [The web editor](#the-web-editor) |
| One host per plant: no clustering, no failover | production | [Availability](#availability) |
| Host messages and logs are partly in Italian | all | [Language](#language) |
| Source-only: no NuGet packages, the libraries are referenced from a clone or submodule at a tag | all | [Distribution](#distribution) |
| `Wcs.Viewer` is experimental: no sample, no tests, outside the compatibility rules | all | [Wcs.Viewer](#wcsviewer) |

## Parked facts towards the WMS

The WCS reports physical facts to the WMS through durable queues in `wcs.db`
([facts, durable queues and receipts](integration/wms-contract.md#facts-durable-queues-and-receipts)).
A temporary failure (no answer, a timeout, a 5xx, a 408 or 429) is retried for ever, with a growing
pause. A **permanent refusal** (any other 4xx; for the load-unit transitions also a stored payload the WCS
cannot read back) is **parked**:
the row stays in its queue with its reason and is never sent again, not even after a restart.

Three queues park:

| Queue (table in `wcs.db`) | Route | What a parked row holds back | Host log line (Error) |
|---|---|---|---|
| `PendingWarehouseTransitions` | `load-units/allocate`, `load-units/release` | every later fact about the same load unit; a parked **release** also every later **allocation** of any load unit. The mission is not closed, and the device holding the load unit goes to Warning | `Transizione WMS {operationId} ({Kind}, UDC {id}) RIFIUTATA DEFINITIVAMENTE e parcheggiata: ...` |
| `PendingCellGiveBacks` | `reservations/release-load-unit-cell` | nothing else; the cell stays reserved in the WMS | `... la restituzione della cella {id} ... e' stata RIFIUTATA DEFINITIVAMENTE dal gestionale e resta parcheggiata ...` |
| `PendingPickedExtractionReports` | `assignments/picked-extraction-became-deposit` | nothing else; the order is not reopened in the WMS | `... la riapertura dell'ordine ... e' stata RIFIUTATA DEFINITIVAMENTE dal gestionale e resta parcheggiata ...` |

**What is missing.** There is no API, command or editor page to list parked rows, see their error and
payload, retry them after the cause is fixed, or discard them. A parked `load-units/allocate` or
`load-units/release` therefore stays until someone intervenes, and while it stays it keeps the load
units that depend on it waiting.

**What to do today** (the supported procedure until an administration flow exists):

1. Keep the host running and the data folder untouched. Do **not** edit or delete rows in `wcs.db`
   by hand: the queues are read in order and depend on each other, and a hand-edited row can let a
   later fact reach the WMS before the one it depends on.
2. Copy the log line: it carries the operation id, the kind, the load unit and the reason the WMS gave.
3. Fix the cause on the WMS side (usually the adapter answering 4xx to something it should accept, or
   4xx for a temporary problem, which must be 5xx).
4. For a parked cell give-back, free the cell in the WMS; for a parked picked-extraction report, put
   the order right in the WMS. These two hold nothing else back in the WCS.
5. For a parked `load-units/allocate` or `load-units/release`, open an issue on the repository with
   the log line (without customer data or keys), or contact Sun Tech
   (<https://www.suntechonline.it>) for a plant under a support agreement. Restarting the host does
   not release a parked row.

To avoid parking in the first place: answer `4xx` only for a request that can never succeed, `5xx` for
anything that may work later, and keep a receipt per `OperationId` so that a repeated request gets the
first answer again ([the WMS contract](integration/wms-contract.md#facts-durable-queues-and-receipts)).
On a test bench, check the host log for `RIFIUTATA DEFINITIVAMENTE` after every test run. The log
line is written once, when the fact is parked; the count of parked rows that `Wcs.DbTool verify`
prints still shows it after a restart. Both are checked by the
[monitoring scripts](integration/monitoring-and-alerting.md).

## Authentication and identity

- The host knows **two static keys**: `Wcs:ApiKey` (everything) and, optionally,
  `Wcs:ReadOnlyApiKey` (`GET` requests and the event hub only). Every program and every person using
  the same key is the same caller to the host.
- **No per-operator identity.** The web editor asks for a key, not for a user; its edit lease is held
  by a random id of the browser session. The `userId` and `workstationId` fields of a command are
  written by the caller and are not verified. The operators' log says what happened, not who did it.
- **No roles** beyond full and read-only: whoever has the full key can start and stop the engine,
  send any command and replace the plant.
- **Key rotation needs a restart.** `appsettings.Secrets.json` is reloaded by the configuration when it
  changes, but the host reads its own keys and the WMS key once, at start: a new key is accepted, and
  the old one refused, only after the host is restarted.
- Failed authentications are answered `401`, but not logged, and there is no rate limiting or lockout.

See [security and threat model](integration/security-and-threat-model.md).

## Plant import and connections

`PUT /api/v1/plant/graph` replaces the plant in `wcs.db` but does not build the driver connections. After
an import that adds devices of connection type Connected, or driver settings, reload the plant with
command 1003 (engine in Stop) or restart the host; until then Run is refused and the devices without a
connection are set OffLine
([when connections are built](integration/connecting-machines.md#when-connections-are-built)).

## Drivers

The only driver in this repository is the **loopback driver**, which simulates the machines in the
process (connection type Test). Sun Tech's driver for its PLCs, and the protocol it speaks, are
proprietary and not included; they are available from Sun Tech. Any other driver is to be written
against `IConnection` and `IConnectionFactory`
([connecting machines](integration/connecting-machines.md)). The driver contract has no method to halt
a movement already sent to a machine.

The loopback driver never raises the field events (`NewLuOnAsi`, `LuUnloadedFromAsi`,
`AsiRequestsLuInfo`, `AsiEvent`): with it, load units enter with command 3002 and leave with command
2004. What it proves is that the WCS's logic runs; it proves nothing about real machines or about
safety ([SAFETY.md](../SAFETY.md)).

## Platform

The WCS is built and tested on Windows 11 x64 with the .NET 10 SDK. The host targets `net10.0` and its
path has no Windows-only call, but Windows Server, Linux and ARM64 have not been verified, and Sun Tech
has not yet published an official support matrix. The host runs as a Windows service with
`UseWindowsService`; its warnings and errors also go to the Windows Event Log. Component by component,
with the evidence: [supported platforms](integration/supported-platforms.md).

## Sizing and growth

- **No hardware requirements are published**: no configuration has been measured for this release.
  Size from a capacity test on your plant ([performance and sizing](integration/performance-and-sizing.md)).
- **`wcs.db` grows without bound**: no code deletes command rows, so every command ever queued stays
  in it. Watch its growth ([monitoring](integration/monitoring-and-alerting.md)).
- **The PLC log is pruned by the driver, not by the host**: the host keeps two monthly files, and rows
  inside the current one are deleted only when the field driver asks for it
  (`ClearMessagesOlderThanAsync`). A driver that never asks keeps a month of messages per file.

## Monitoring

The host API has no health route beyond `GET /api/v1/status`. Parked facts and the backlog of the
durable queues towards the WMS can be read only with `Wcs.DbTool verify`; the engine's counters of
dropped or failed database writes are not exposed; the SignalR hub is not a persistent health check.
What can be watched today, and scripts for it: [monitoring and alerting](integration/monitoring-and-alerting.md).

## The web editor

The web editor's static files are served next to the executable only by a published host
(`dotnet publish`). A host built with `dotnet build` serves them only in the Development environment;
elsewhere the `/editor` page stays blank and the host logs a warning at start
([deployment](integration/deployment-and-operations.md#publish)). The editor's pages load without a key
and do nothing until a key is entered.

## Availability

The engine keeps its state in memory and in SQLite files under one data folder: run **one host per
plant**. There is no clustering, replication or automatic failover. Plan backups and a tested restore
([backup, restore and disaster recovery](integration/backup-restore-and-disaster-recovery.md)).

## Language

Many host messages (error titles and details, log lines) are in Italian, and so are many comments in
the engine and the host, and the historical notes. The guides quote the messages and translate them.
Program against status codes, not texts.

## Distribution

The WCS is distributed as source. No NuGet package of the contracts and clients
(`Wcs.Wms.Contracts`, `Wcs.Api.Contracts`, `Wcs.Api.Client`, `Wcs.Common`, `Wcs.Connection.Interface`)
is published yet; they will come with a later release. Until then a WMS adapter, a client program or a
driver references the projects from a clone of this repository, or a git submodule, checked out at a
release tag ([README](../README.md#using-the-libraries-from-your-code)). The releases carry the
published host, `Wcs.DbTool` and the sample WMS, framework-dependent and built on Windows
([release process](release-process.md)).

## Wcs.Viewer

`WCS/Wcs.Viewer`, a library that keeps a read-only copy of the engine state from the hub, is
**experimental**. Its WinForms test bench is not published, so it has no tests and no sample here; no
guide relies on it, and it is outside the [compatibility rules](versioning-and-compatibility.md): it
may change or be removed in any release. To follow the engine from your code, use `Wcs.Api.Client`
and its `PlantEventsConnection` ([the host API](integration/host-api.md#the-net-client-wcsapiclient)).

## Smaller points

- Command 3001 (`MoveLuAutomatic`) is reserved: it is accepted and marked Done without doing anything.
- The operators' log records command 3002 with the wording of an unload; nothing is unloaded.
- `Wcs:Engine:DiscardLuManagedInWarehouse` is read at start and used by nothing.
- The edit lease lasts 60 seconds and is renewed by every authorised write; a single write that takes
  longer than that (a very slow disk), or a browser tab closed while a save is in flight, can let a
  second holder write at the same time.
- The OpenAPI document (`/openapi/v1.json`) is served without a key in every environment except
  Production.
- The sample WMS keeps everything in memory and has a public key: it is for the demo and for tests,
  never for a plant.
