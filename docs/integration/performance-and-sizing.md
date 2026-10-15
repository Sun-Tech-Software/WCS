# Performance and sizing

What drives the CPU, memory and disk a WCS host needs, how to measure it on your plant, and how to
estimate disk space and retention. **This page gives no minimum or recommended hardware**: no
configuration has been measured for this release, and a number written here without a measurement
behind it would be a guess. What it gives instead is the protocol that produces those numbers, and the
list of what Sun Tech has to measure and fill in before it can publish an official sizing matrix.

How each statement on this page is known:

- **from the code**: read in this repository, with the file named;
- **observed**: seen on a local test installation (Windows 11 x64, sample WMS, sample plant, loopback
  driver), not representative of a plant;
- **declared by Sun Tech**: Sun Tech's statement, not verified here;
- **not yet verified**: nobody has measured it for this release;
- **prudential recommendation**: advice, not a measured requirement.

## Demo, test bench, production

| | Demo | Test bench | Production |
|---|---|---|---|
| What runs | host, sample WMS, loopback driver, one machine | host, your WMS adapter, the field driver, machines brought up one by one | host, WMS, driver, the whole plant |
| What its load says about production | nothing: no field traffic, an in-memory WMS, a handful of devices | the field driver and the WMS calls on real hardware, at partial load | it is the load |
| Where to size | never from here | the capacity test below, at the plant's target throughput | the measurements of the capacity test, kept up to date with monitoring |

The figures shown on this page for the demo are there to show the order of magnitude of a near-empty
installation and what grows; they are not sizing data.

**Declared by Sun Tech:** the engine has run 50 plants, with up to 386 machines at the same time. It
is a statement about field use, not a benchmark of this release and not a limit: nobody has measured
this release at that size, and the hardware those plants ran on is not documented here.

## What drives CPU and memory

| Factor | How it acts | Known from |
|---|---|---|
| Engine loops | three loops run as long as the engine is loaded: `BuildRoutePath` with a 10 ms pause between turns, `UpdateStatus` every 1000 ms, and the master loop every `Wcs:Engine:MasterProcessOnEvery` ms (1000 by default). The pause comes after each turn, so a turn's duration adds to the period. The route loop turns even with nothing to route | code: [`Navigator.cs`](../../WCS/Wcs.Engine/src/Navigator.cs) (`LoadNavigatorData`, `BackgroundLoop`) |
| Devices and junctions | the plant graph, the devices' state and their step logic live in memory; routes are recomputed at every hop on that graph | code; cost per device not yet verified |
| Active missions | each mission keeps its steps in memory and is persisted to `wcs.db` at every change; routing work grows with the missions waiting for a route | code; cost per mission not yet verified |
| Field message rate | each message from a PLC is handled by the driver and the engine, and is written to the PLC log if the driver logs it | code (the driver decides what to log); rate on a real plant **not yet verified**, a comment in [`PlantQueries.cs`](../../WCS/Wcs.Engine/src/Api/PlantQueries.cs) records that it still has to be measured with PLCs connected |
| WMS calls | in Run, each master turn asks the WMS for pending work twice (`extractions/pending-work`, `notes/pending-work`): about two calls a second with the default period; then calls driven by the load units (descriptions, reservations, allocations, releases) and retries of the durable queues (2 s growing to 30 s). A call waits up to `Wms:TimeoutSeconds` (30 s) | code: `MasterProcess` in [`Navigator.cs`](../../WCS/Wcs.Engine/src/Navigator.cs); [configuration](../reference/configuration.md) |
| Clients of the API and the hub | each SignalR client receives every change; the host queues the events for the hub in one queue of 10,000 and drops the oldest when it cannot send them fast enough, then tells the clients to reload the full state | code: [`PlantEventRelay.cs`](../../WCS/Wcs.Host/src/Engine/PlantEventRelay.cs) |
| Web editor sessions | the editor is Blazor Server: each open editor tab is a circuit with its own state in the host, plus the plant drawing loaded in the browser (draw.io). Large drawings cost memory on both sides; the request limit `Wcs:MaxRequestBodyMegabytes` is 256 MB because a legacy drawing of a test plant weighed 48 MB as JSON | code: [`Program.cs`](../../WCS/Wcs.Host/src/Program.cs); memory per circuit not yet verified |
| Logging | HttpClient logs every WMS call at Information unless `Logging:LogLevel:System.Net.Http.HttpClient` is `Warning`; `Wcs:Engine:ActivateDebugLog` adds the engine's information lines | code; [configuration](../reference/configuration.md) |
| The field driver | its own threads, sockets, buffers and native libraries | the driver's vendor |

The browser that runs the web editor needs its own resources for draw.io and the drawing; it is the
operator's workstation, not the server, unless the editor is opened on the server itself.

## What drives disk

### The databases

All in `Wcs:DatabaseDirectory`, SQLite in WAL mode
([data files](deployment-and-operations.md#data-files)):

| File | What makes it grow | Retention | Known from |
|---|---|---|---|
| `wcs.db` | the plant, missions, the extraction queue, the durable queues towards the WMS, and **one row per command**: no code path deletes command rows, so the file grows with every command queued (`POST /api/v1/commands`, the web editor, `DELETE /api/v1/extraction-queue/{id}`) | none: permanent | code: [`CommandStore.cs`](../../WCS/Wcs.Engine/src/Persistence/CommandStore.cs) has no delete |
| `wcs-history-YYYY-MM.db` | handled load units and the operators' log (`GET /api/v1/log`): lines per movement, command, refusal and connection problem, and the driver's own lines | `Wcs:HistoryMonthsToKeep` files, the current month included (12 by default); older files are deleted at start and at the change of month | code: [`WcsHistoryDatabase.cs`](../../WCS/Wcs.Engine/src/Persistence/WcsHistoryDatabase.cs) |
| `wcs-plc-YYYY-MM.db` | the messages the field driver chooses to record (`WriteMessageAsync`) | 2 monthly files, fixed (not a setting). Inside the current file, rows are pruned only when the driver calls `ClearMessagesOlderThanAsync(days)`: Sun Tech's driver passes 7 days; a driver that never calls it keeps every message until its month file is deleted | code: [`WcsPlcLogDatabase.cs`](../../WCS/Wcs.Engine/src/Persistence/WcsPlcLogDatabase.cs), [`PlcMessageLog.cs`](../../WCS/Wcs.Engine/src/ORM/PlcMessageLog.cs); the 7 days of Sun Tech's driver are declared in that code, the driver itself is not here |
| `-wal`, `-shm` | the recent commits, until a checkpoint | emptied at a clean stop and at the month's rotation; SQLite also checkpoints as it goes | [how the host writes its databases](backup-restore-and-disaster-recovery.md#how-the-host-writes-its-databases) |

SQLite does not give space back to the file system when rows are deleted: a file keeps its largest
size and reuses its free pages. A pruned PLC log file therefore stays at the size of its busiest
window, and only the deletion of a whole monthly file frees disk.

The loopback driver records no messages, so on a demo the PLC log stays nearly empty: a demo says
nothing about PLC log growth.

**Observed on the demo** (sample plant, two pallets stored, after a clean stop):
`wcs.db` 196 KB, the month's history file 68 KB, the PLC log file 36 KB
([backup guide](backup-restore-and-disaster-recovery.md#how-the-host-writes-its-databases)). Not
sizing data.

The engine writes the history and the PLC log through in-memory queues of up to 50,000 writes each
that **drop the oldest writes when full**; the state of `wcs.db` goes through a queue of 5,000 that
does not drop (code: [`Navigator.cs`](../../WCS/Wcs.Engine/src/Navigator.cs),
[`QueuedDatabaseWriter.cs`](../../WCS/Wcs.Engine/src/Persistence/QueuedDatabaseWriter.cs)). On storage
too slow for the write rate, log lines are lost before the plant state is. The engine counts the
dropped writes, but no host route exposes the counters in this version.

### Other disk users

| What | Size driver | Retention |
|---|---|---|
| Backups (`Wcs.DbTool backup`) | about the size of the databases at the time of the backup, per generation | your backup script (the [example](backup-restore-and-disaster-recovery.md#scheduling-it) keeps two on the machine) and your off-machine copies |
| `<data folder>.replaced-<time>` folders left by `Wcs.DbTool restore` | the databases they replaced | until someone deletes them |
| The published folder of the previous version, kept to roll back | the binaries (tens of MB) | until the next upgrade |
| Host log | the console is not written to disk by the host; as a service, warnings and errors go to the Windows Application event log, whose size and retention are Windows settings | Windows |
| Monitoring state files ([monitoring](monitoring-and-alerting.md)) | one CSV line per run | grow until pruned |

### Antivirus and slow storage

- **Antivirus**: real-time scanning of every write to `*.db`, `*.db-wal` and `*.db-shm` adds latency to
  each commit and checkpoint. Whether to exclude the data folder from real-time scanning is a decision
  for the plant's IT security; if it is not excluded, run the capacity test with the antivirus as it
  will be in production. (Prudential recommendation.)
- **Slow or shared storage**: network shares, thin-provisioned virtual disks shared with other loads,
  and USB disks have not been tested. SQLite in WAL mode needs a local file system that supports its
  shared-memory file. At a clean stop each writer gets 10 seconds to drain before the host logs
  `... arresto scaduto con N scritture ancora in coda` ("stop timed out with N writes still queued")
  (code). The edit lease of the web editor (60 s) can be outlived by a single write on a very slow disk
  ([known limitations](../known-limitations.md#smaller-points)).

## Estimating disk and retention

Measure on the test bench or the first weeks of production, at the plant's normal throughput, with
the monitoring storage check recording sizes once an hour (`Test-WcsStorage.ps1 -StateFile`). The
parameters the installer measures:

| Symbol | What | How to measure |
|---|---|---|
| `W` | `wcs.db` growth per day | the `growth` line of `Test-WcsStorage.ps1` over at least a week |
| `W0` | size of `wcs.db` today | the `files` line |
| `H` | size of the busiest monthly history file | a full month of `wcs-history-YYYY-MM.db`, or the daily growth over a representative week x 31 |
| `P` | size of the busiest monthly PLC log file | as `H`, for `wcs-plc-YYYY-MM.db`, with the field driver connected |
| `M` | `Wcs:HistoryMonthsToKeep` | the configuration |
| `L` | largest `-wal` seen | the `files` line, at the busiest hour |
| `G` | backup generations kept on the machine | your backup script |
| `Y` | horizon of the estimate, in days (for example until the next planned review) | your choice |
| `F` | free-space margin you want to keep | your IT policy |

Then:

```text
databases(Y) = (W0 + W x Y) + M x H + 2 x P + 3 x L
backups(Y)   = G x (W0 + W x Y + M x H + 2 x P)  # a backup copies wcs.db and every monthly file, without -wal
disk needed  = databases(Y) + backups(Y) + previous version folder + F
```

Notes on the formula:

- `M x H` assumes every kept month is as busy as the busiest; replace it with the sum of the measured
  months once you have them.
- `2 x P` holds because exactly two PLC log files are kept; with a driver that prunes after `d` days,
  `P` is roughly the busiest `d` days of messages, not a month's.
- A backup copies **all** the monthly history files that exist, so with `M = 12` and several local
  generations the backups can be larger than the live data: keep that in mind when choosing `G`.
- `wcs.db` grows without bound in this version (commands are never deleted): re-run the estimate
  periodically, and alert on growth with `-MaxGrowthMBPerDay`.

Retention choices and their cost:

- Raising `Wcs:HistoryMonthsToKeep` raises `M`; lowering it deletes history files at the next start
  ([configuration](../reference/configuration.md)), and the history queries answer `400` for months
  no longer kept.
- The PLC log's two months and the driver's pruning window are not settings of the host.

## Measuring a real plant

A methodology for the test bench, before production, with the real WMS adapter, the field driver and
the real machines. It is the only source of sizing numbers for your plant.

1. **Fix the workload**: the plant's target throughput (load units stored and extracted per hour), the
   peak hour, the number of devices, the number of operator screens and editor sessions open at the
   same time, and the WMS behaviour (its normal latency). These come from the plant's specification,
   not from the WCS.
2. **Fix the installation**: the server (CPU model and count, RAM, disk type), the OS and its
   version, the .NET runtime version, the antivirus configuration, the WCS version, the driver
   version, the host settings (`MasterProcessOnEvery`, logging levels). Write them down: a result
   without them cannot be compared.
3. **Warm up**: start the host, load the plant, put the engine in Run, and let it run 30 minutes at
   normal load before measuring.
4. **Run the scenarios**, each long enough to see trends (a prudential minimum is one hour per
   scenario, and 24 hours for the steady-state one):
   - steady state at normal throughput;
   - the peak hour sustained;
   - WMS slow (adapter answering near `Wms:TimeoutSeconds`) and WMS down for 10 minutes, then back;
   - field driver connection lost and restored on one device and on a whole PLC;
   - an online backup (`Wcs.DbTool backup`) during the peak;
   - editor sessions open during the peak, including an import of the plant in Stop afterwards;
   - a restart of the host service at normal load, measuring the time until `GET /api/v1/status` shows
     the engine loaded and missions resume.
5. **Record the metrics** below during every scenario.
6. **Judge against the pass criteria** agreed beforehand.
7. **Keep the raw data** with the installation description: they are what a sizing statement rests
   on.

### Metrics to record

| Metric | Source |
|---|---|
| CPU of `Wcs.Host` (% of the machine, and per core) | Windows Performance Monitor, `Process(Wcs.Host)\% Processor Time` |
| Memory of `Wcs.Host`: working set and private bytes, over time | Performance Monitor, `Process(Wcs.Host)\Working Set`, `Private Bytes` |
| Thread and handle counts of `Wcs.Host` | Performance Monitor, `Process(Wcs.Host)\Thread Count`, `Handle Count` |
| .NET runtime counters: GC heap size, GC pauses, thread-pool queue length | `dotnet-counters monitor --name Wcs.Host System.Runtime` (a Microsoft tool you install) |
| Disk: latency of writes on the data volume, free space | Performance Monitor, `LogicalDisk(<volume>)\Avg. Disk sec/Write`, `% Free Space` |
| Database and `-wal` sizes, growth per day | `Test-WcsStorage.ps1 -StateFile` |
| API latency: `GET /api/v1/status` and `GET /api/v1/plant/items/status` | timed calls from the monitoring machine (`Measure-Command`) |
| Throughput: load units handled per hour | `GET /api/v1/handled-load-units/summary?groupBy=day` and `GET /api/v1/handled-load-units` |
| Mission times between devices | `GET /api/v1/log/transit-times` |
| Command latency: from `POST /api/v1/commands` to status Done | the command's own record (`GET /api/v1/commands/{id}`) |
| WMS call rate and latency, failures | your adapter's request log; the host log lines `Il gestionale non ha risposto` |
| Field message rate | the driver's own counters, or rows per minute in `GET /api/v1/history/plc-messages` if the driver logs every message |
| Alarms raised | the [monitoring scripts](monitoring-and-alerting.md) run every minute |

### Pass criteria

Agree them before the test, with numbers from the plant's specification. A template:

- the target throughput is sustained through the peak scenario;
- no CRITICAL from the monitoring scripts during the steady-state and peak scenarios; WARNINGs only
  where the scenario provokes them (WMS down, connection lost), and they clear by themselves after the
  cause is removed;
- after warm-up, the host's private bytes show no steady upward trend over the 24-hour run;
- CPU of `Wcs.Host` leaves the headroom the plant's IT policy requires, at the peak;
- disk write latency and `-wal` sizes stay flat; database growth matches the estimate above;
- the WMS's answers stay well within `Wms:TimeoutSeconds` at the peak;
- no fact is parked, and the durable queues drain after the WMS comes back;
- the online backup completes and verifies during the peak;
- after the restart, the engine is loaded and missions resume within the time the plant accepts.

### Capacity-test checklist

- [ ] Workload and installation written down (steps 1 and 2)
- [ ] Monitoring scripts scheduled every minute, their output kept
- [ ] Performance Monitor data collector set recording the counters above
- [ ] Sizes recorded hourly (`Test-WcsStorage.ps1 -StateFile`)
- [ ] Each scenario of step 4 run, with start and end times noted
- [ ] Pass criteria judged, failures explained
- [ ] Disk estimate computed with the measured `W`, `H`, `P`, `L`
- [ ] Raw data and the installation description archived with the WCS and driver versions

## What Sun Tech must fill in before an official matrix

Before minimum and recommended configurations can be published, Sun Tech has to measure, with the
protocol above, and record:

- [ ] reference installations: plant size (devices, junctions, peak missions), throughput, server
      hardware, OS, WCS and driver versions;
- [ ] CPU and memory of the host per device count and per active missions, from at least a small, a
      medium and a large reference plant;
- [ ] the field message rate of its driver at each of those plants, and the PLC log growth per day;
- [ ] `wcs.db` and history growth per day at each of them;
- [ ] the effect of the web editor sessions and SignalR clients on memory;
- [ ] the minimum disk performance (write latency) under which the host works without dropping log
      writes;
- [ ] which of those installations ran on virtual machines, and with what storage;
- [ ] whether the 50 plants / 386 machines statement can be backed by such records, and for which
      versions of the engine.

Until then: size from your own capacity test, and treat any figure without a measurement behind it as
unverified.
