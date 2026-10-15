# Sun Tech WCS documentation

These guides are for a developer at a company that has its own **warehouse management system (WMS)**
and wants to put this **warehouse control system (WCS)** under it: the WCS knows the plant (conveyors,
cranes, bays, racks) and moves load units through it; your WMS keeps deciding what goes where.

Everything here was checked against the code of this repository, and the examples (commands, HTTP
requests and answers, configuration, C#) were run.

This is an **initial public preview**: read [Known limitations](known-limitations.md) before
planning a production installation. Its first version, `0.1.0-preview.1`, is **source-only**: no
NuGet packages yet; your code references the libraries from a clone or a submodule at a release tag
([README](../README.md#using-the-libraries-from-your-code)). **The WCS is not a safety system**: read
[SAFETY.md](../SAFETY.md) before connecting it to machines.

## Where to start

| You want to | Read |
|---|---|
| see the WCS store and extract a pallet on your machine, in fifteen minutes | [Quick start](quick-start.md) |
| understand what the WCS does and what stays in your WMS | [Overview and boundary](integration/overview-and-boundary.md) |
| connect your WMS | [The WMS contract](integration/wms-contract.md), then [Writing a WMS adapter](integration/writing-a-wms-adapter.md) |
| drive the WCS from your systems (ERP, MES, screens) and watch it | [The host API](integration/host-api.md) |
| describe your plant | [Modelling a plant](integration/plant-modelling.md) |
| connect real machines | [Connecting machines](integration/connecting-machines.md) |
| understand how the engine reasons, or write the driver of a new machine | [How the WCS thinks](how-the-wcs-thinks.md) |
| install and run it | [Deployment and operations](integration/deployment-and-operations.md) |
| know which operating systems, architectures and browsers it was tested on | [Supported platforms](integration/supported-platforms.md) |
| size the server and its disk, and test capacity on your plant | [Performance and sizing](integration/performance-and-sizing.md) |
| watch it and raise alarms (health, devices, WMS errors, parked facts, disk, backups) | [Monitoring and alerting](integration/monitoring-and-alerting.md) |
| back it up, restore it, upgrade and roll back | [Backup, restore and disaster recovery](integration/backup-restore-and-disaster-recovery.md) |
| find out why something does not work | [Troubleshooting](integration/troubleshooting.md) |
| know what the WCS does not do yet | [Known limitations](known-limitations.md) |
| know what a version promises, and what would break your adapter, client or driver | [Versioning and compatibility](versioning-and-compatibility.md) |
| get a release, check it, upgrade to it | [Release process](release-process.md) |
| know what is still open before the first public release | [Publication checklist](publication-checklist.md) |
| secure it: network, keys, TLS, audit | [Security and threat model](integration/security-and-threat-model.md) |
| go from the test bench to production | [Production readiness checklist](integration/production-readiness-checklist.md) |

### A reading path for an integration

1. [Quick start](quick-start.md): run it once, end to end, with the sample WMS.
2. [Overview and boundary](integration/overview-and-boundary.md): who owns what, the pull model, the
   two sequences (storing, extracting).
3. [The WMS contract](integration/wms-contract.md): the 43 routes your WMS serves, on the wire.
4. [Writing a WMS adapter](integration/writing-a-wms-adapter.md): from the sample WMS to yours, and how
   to test it with the WCS's own client.
5. [Modelling a plant](integration/plant-modelling.md): your devices, and how they meet your cells.
6. [The host API](integration/host-api.md): commands, reads and events from your side.
7. [Deployment and operations](integration/deployment-and-operations.md), then
   [backup, restore and disaster recovery](integration/backup-restore-and-disaster-recovery.md),
   [monitoring and alerting](integration/monitoring-and-alerting.md),
   [Security and threat model](integration/security-and-threat-model.md), and, for the server,
   [supported platforms](integration/supported-platforms.md) and
   [performance and sizing](integration/performance-and-sizing.md).
8. [Known limitations](known-limitations.md) and the
   [production readiness checklist](integration/production-readiness-checklist.md) before a plant
   depends on it; the machine-safety rules are in [SAFETY.md](../SAFETY.md).
9. [Versioning and compatibility](versioning-and-compatibility.md) and the
   [release process](release-process.md): which version to build against, and how to upgrade.

## Reference

| | |
|---|---|
| [WMS routes](reference/wms-routes.md) | the 43 routes the WCS calls, one line each, and the enumerations on the wire |
| [Host API](reference/host-api.md) | the 66 operations of `/api/v1` and the SignalR hub |
| [Commands](reference/commands.md) | the command codes (`POST /api/v1/commands`) |
| [Configuration](reference/configuration.md) | every setting, its default, whether it is required |
| [Glossary](reference/glossary.md) | the words of the WCS and their names in the code |
| [Contracts](contracts/README.md) | the host API and the WMS contract as OpenAPI 3.1 documents generated from the code, their versions, and validated JSON examples |

## What is in this repository

| Folder | What it is |
|---|---|
| [`WCS/Wcs.Engine`](../WCS/Wcs.Engine) | the engine: plant graph, missions, device states, routes, persistence on SQLite |
| [`WCS/Wcs.Host`](../WCS/Wcs.Host) | the executable: hosts the engine, serves the HTTP API, the SignalR hub and the web editor |
| [`WCS/Wcs.WebEditor`](../WCS/Wcs.WebEditor) | the web editor (Blazor and draw.io), served at `/editor` |
| [`WCS/Wcs.Wms.Contracts`](../WCS/Wcs.Wms.Contracts), [`WCS/Wcs.Wms.Client`](../WCS/Wcs.Wms.Client) | the contract the WCS declares towards the WMS (`IWarehouse`) and its HTTP client |
| [`WCS/Wcs.Api.Contracts`](../WCS/Wcs.Api.Contracts), [`WCS/Wcs.Api.Client`](../WCS/Wcs.Api.Client) | the host API's routes and bodies, and its typed .NET client |
| [`WCS/Wcs.Connection.Interface`](../WCS/Wcs.Connection.Interface) | the contract towards the machines (`IConnection`, `IConnectionFactory`) |
| [`WCS/Wcs.Connection.Test`](../WCS/Wcs.Connection.Test) | the loopback driver: simulated PLCs |
| [`WCS/tools/Wcs.DbTool`](../WCS/tools/Wcs.DbTool) | backup, verification and restore of the host's databases ([guide](integration/backup-restore-and-disaster-recovery.md)) |
| [`samples/`](../samples/README.md) | a runnable sample WMS, a sample plant and their tests |
| [`tools/monitoring`](../tools/monitoring) | PowerShell scripts that check a running installation and exit with an alarm code ([guide](integration/monitoring-and-alerting.md)) |

Not here: Sun Tech's own driver for its PLCs and the protocol it speaks, which are proprietary and
available from Sun Tech (<https://suntechonline.it>). The WCS runs without them on the loopback
driver, and a driver of your own plugs in through `IConnectionFactory`
([connecting machines](integration/connecting-machines.md)).

## Conventions in these guides

- Ports: the sample WMS on `http://localhost:5142`, the WCS host on `http://127.0.0.1:5580` (their
  defaults). Keys: `my-wcs-key` for the host (`Wcs:ApiKey`), `sample-wms-key` for the sample WMS
  (`Wms:ApiKey` on the host side).
- Commands are PowerShell; `curl.exe` (part of Windows) shows raw JSON.
- HTTP examples leave out the `Date` and `Server` headers and the `traceId` of error bodies.
- **The host's messages are partly in Italian**: error titles and details, and many log lines. The
  guides quote them as they are and translate them. Program against status codes, not texts.
- Identifiers in the code and on the wire sometimes keep Italian or historical names (`udc` for load
  unit, `plc` for the driver settings, `KazamControl` as the WCS's own workstation name): the
  [glossary](reference/glossary.md) lists them.
- Sun Tech's internal design notes, written in Italian, are not part of these guides. The public
  repository keeps them for reference under `docs/history/`, as they were written: they describe the
  system as it was then and are not kept up to date.
