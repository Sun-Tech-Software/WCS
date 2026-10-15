# Monitoring and alerting

What to watch on a WCS installation, where each signal can be read today, and three PowerShell
scripts that turn those signals into alarms for a scheduler or a monitoring system. The scripts are in
[`tools/monitoring`](../../tools/monitoring); they are not in the release archives, so take them from
the source of the same release tag.

Every output on this page comes from a run on 2026-10-15 on a test installation: a host built from
this repository on Windows 11 x64, listening on a free port (`5795`) with its own data folder, the
[sample WMS](../../samples/README.md) and the sample plant. Long scratch paths are shortened to
`<root>`; numbers are formatted in the culture of that machine (decimal comma). The same scripts run
in the automated test `MonitoringScriptsTest` of
[`samples/Wcs.SampleWms.Test`](../../samples/Wcs.SampleWms.Test/MonitoringScriptsTest.cs), against a
host process it starts.

## What to watch

| Signal | Where it can be read today | Script and check | Level |
|---|---|---|---|
| The host process or service is running | the operating system | `Test-WcsHealth.ps1 -ProcessName` / `-ServiceName` | CRITICAL when absent |
| The host answers, the engine is loaded, in the expected state | `GET /api/v1/status` (`navStatus`, `engineLoadError`) | `Test-WcsHealth.ps1`, `status` | CRITICAL |
| Devices in Error, Warning or OffLine | `GET /api/v1/plant/items/status` (`status` 99, 89, 6) | `Test-WcsHealth.ps1`, `devices` | CRITICAL for Error, WARNING for Warning, WARNING or CRITICAL for OffLine |
| Driver connections | `GET /api/v1/plant/items` (`connectionType`, `deviceConnectionId`), `GET /api/v1/plc/connections`, `GET /api/v1/plc/connections/{id}/status`; a device whose connection is missing goes OffLine | `Test-WcsHealth.ps1`, `connections` and `devices` | CRITICAL for a Connected device pointing to no row; the persisted status is reported, not judged |
| WMS communication errors | host log: `Il gestionale non ha risposto` ("the WMS did not answer"); `GET /api/v1/wms/configuration-check` answers `503` when the WMS is unreachable | `Test-WcsEvents.ps1`, `wms`; `Test-WcsHealth.ps1 -ProbeWms` | WARNING (log), CRITICAL (probe) |
| Facts parked towards the WMS | host log `RIFIUTATA DEFINITIVAMENTE`, once; the count of parked rows from `Wcs.DbTool verify`; a load unit transition parked also puts the device holding the load unit in Warning | `Test-WcsEvents.ps1`, `parked`; `Test-WcsStorage.ps1`, `parked` | CRITICAL |
| Facts waiting to be delivered to the WMS | the counts of the durable queues printed by `Wcs.DbTool verify` | `Test-WcsStorage.ps1`, `queues` | WARNING above a threshold you set |
| Free disk space | the volume of the data folder | `Test-WcsStorage.ps1`, `disk` | WARNING, CRITICAL |
| Database growth | the size of `wcs.db` and of the monthly files over time | `Test-WcsStorage.ps1 -StateFile`, `growth` | WARNING above a threshold you set |
| Backup taken, and intact | the newest folder written by `Wcs.DbTool backup`; `Wcs.DbTool verify` on it | `Test-WcsStorage.ps1 -BackupRoot -DbToolPath`, `backup` | CRITICAL when missing, old or failing |
| Errors in the Windows event log | Application log, the host's events | `Test-WcsEvents.ps1 -EventLog` | WARNING, or the level of a known text |
| Unexpected restarts | Service Control Manager events 7031 and 7034; Application Error 1000 and .NET Runtime 1026 for `Wcs.Host.exe`; the process start time | `Test-WcsEvents.ps1 -ServiceName`; `Test-WcsHealth.ps1 -ProcessName -MinUptimeMinutes` | CRITICAL (events), WARNING (uptime) |

Not observable from outside in this version: the counters of the engine's database write queues
(writes dropped or failed), the length of the field driver's queues, and the progress of the engine
loops. A parked fact cannot be inspected, retried or discarded through the API
([known limitations](../known-limitations.md#parked-facts-towards-the-wms)).

## The scripts

| Script | Runs where | Reads |
|---|---|---|
| [`Test-WcsHealth.ps1`](../../tools/monitoring/Test-WcsHealth.ps1) | anywhere that reaches the host | the host API (GET only) and, optionally, the local process or service |
| [`Test-WcsStorage.ps1`](../../tools/monitoring/Test-WcsStorage.ps1) | on the host's machine | the data folder, the backup folder, `Wcs.DbTool verify` (read-only) |
| [`Test-WcsEvents.ps1`](../../tools/monitoring/Test-WcsEvents.ps1) | on the host's machine | a log file, the Windows event log |

Common rules ([`WcsMonitoring.Common.ps1`](../../tools/monitoring/WcsMonitoring.Common.ps1)):

- **PowerShell 7** (`pwsh`), Windows or another system; the event log and service checks are Windows
  only and answer UNKNOWN elsewhere.
- **One line per finding** (`LEVEL check: message`), then a summary line; nothing else on the output.
- **Exit code** of the worst finding:

  | Code | Level | Meaning |
  |---|---|---|
  | 0 | OK | every check passed |
  | 1 | WARNING | something needs a look; nothing is down |
  | 2 | CRITICAL | something is down or wrong and needs a person now |
  | 3 | UNKNOWN | a check could not run: missing key, wrong argument, tool not found |

  CRITICAL wins over WARNING, WARNING over UNKNOWN. These are the codes Nagios-style systems expect.
- **The API key** comes from an environment variable, `WCS_MONITOR_API_KEY` by default
  (`-ApiKeyVariable` names another). It is sent only in the `X-Api-Key` header and never printed, not
  even when it is refused. Give the scripts the **read-only key** (`Wcs:ReadOnlyApiKey`): they only
  send `GET` requests. Never pass a key on a command line, where other users of the machine can read it
  in the process list.
- **The host URL** is `-BaseUrl` (default `http://127.0.0.1:5580`); every HTTP call has a timeout
  (`-TimeoutSeconds`, 10 by default).
- **Thresholds** have defaults that are starting points, not recommendations: set them from your
  plant's measurements ([performance and sizing](performance-and-sizing.md)).

## Test-WcsHealth.ps1

```powershell
$env:WCS_MONITOR_API_KEY = '<read-only key>'
pwsh -NoProfile -File tools\monitoring\Test-WcsHealth.ps1 -BaseUrl http://127.0.0.1:5795 -ExpectedEngineState Stop -ProcessName Wcs.Host
```

```text
OK       process: 1 process(es) Wcs.Host, the newest started 2026-10-15 15:05:50 (up 0 min)
OK       status: engine Stop, version 0.1.0.0
OK       devices: 7 device(s), none in Error, Warning or OffLine
OK       connections: 0 Connected device(s), 0 driver settings row(s)
Test-WcsHealth OK (4 OK; exit code 0)
```

`-ExpectedEngineState` is `Run` by default, because a production plant works in Run: the same host,
checked without it, is CRITICAL:

```text
CRITICAL status: engine in Stop, expected Run
OK       devices: 7 device(s), none in Error, Warning or OffLine
OK       connections: 0 Connected device(s), 0 driver settings row(s)
Test-WcsHealth CRITICAL (1 CRITICAL, 2 OK; exit code 2)
```

With the engine in Run and the outfeed device put OffLine (command 2002), and the WMS probe on:

```text
OK       status: engine Run, version 0.1.0.0
WARNING  devices: 1 OffLine: OUT01
OK       connections: 0 Connected device(s), 0 driver settings row(s)
OK       wms: the WMS answered the configuration check (0 finding(s) about its data)
Test-WcsHealth WARNING (3 OK, 1 WARNING; exit code 1)
```

`-IgnoreDevice OUT01` leaves out a device kept OffLine on purpose (`devices: 6 device(s), none in
Error, Warning or OffLine (1 ignored)`), and `-OffLineLevel CRITICAL` makes OffLine an alarm.

A wrong key, no key, a host that does not answer:

```text
CRITICAL status: GET /api/v1/status answered 401: the key was refused (wrong key, or the host was restarted with another one) (title=Chiave assente o sbagliata) [http://127.0.0.1:5795]
Test-WcsHealth CRITICAL (1 CRITICAL; exit code 2)

UNKNOWN  api-key: the environment variable WCS_MONITOR_API_KEY is not set: put the host's read-only key (Wcs:ReadOnlyApiKey) in it
Test-WcsHealth UNKNOWN (1 UNKNOWN; exit code 3)

CRITICAL status: GET /api/v1/status failed: no answer within 2 s [http://10.255.255.1:5580]
Test-WcsHealth CRITICAL (1 CRITICAL; exit code 2)
```

What each check means:

- **status**: `GET /api/v1/status` is the only route that answers while the engine is not loaded.
  A non-empty `engineLoadError` is CRITICAL: the host is up, the engine is not, and the host retries
  every `Wcs:EngineLoadRetrySeconds` ([troubleshooting](troubleshooting.md#the-host-runs-but-the-engine-is-not-loaded)).
  Recover is a short state while the engine loads (WARNING). The host's clock is compared with the
  monitoring machine's (`-MaxClockSkewSeconds`, 120): a skewed clock makes logs and history disagree.
- **devices**: Error (99) means the last command to the device did not go through; Warning (89) that
  its last movement failed, and it is retried with command 2001 once the cause is fixed; OffLine (6)
  that routes avoid it, by an operator's choice (2002) or because a Connected device has no driver
  connection ([connecting machines](connecting-machines.md#when-connections-are-built)).
- **connections**: every device of connection type Connected must point to a driver settings row.
  For each row, the status the driver last persisted is printed as it is (`opProgress`, the channel
  states): what the values mean belongs to the driver, so the script does not judge them. The
  loopback driver persists none.
- **wms** (`-ProbeWms`): the host calls the WMS's `configuration-check` route. It answers `503` with
  `"unavailable":"warehouseUnreachable"` when the WMS cannot be reached. It costs your WMS a check of
  its configuration: run it every few minutes, not every few seconds.

## Test-WcsStorage.ps1

```powershell
pwsh -NoProfile -File tools\monitoring\Test-WcsStorage.ps1 -DataDirectory <root>\Data `
    -BackupRoot <root>\Backups -DbToolPath <root>\DbTool\Wcs.DbTool.exe -StateFile <root>\sizes.csv
```

```text
WARNING  disk: 120,5 GB free of 952,5 GB (12,6%) on C:\
OK       files: wcs.db 4,2 MB; history 0,1 MB in 1 monthly file(s); PLC log 0,0 MB in 1 monthly file(s)
OK       growth: first measurements recorded in <root>\sizes.csv; growth is reported once they span an hour
OK       backup: 20261015-150658: taken 2026-10-15 13:06 UTC (0,5 h ago), 3 file(s)
OK       backup: Wcs.DbTool verify 20261015-150658: every file verified
OK       parked: no parked fact in the live wcs.db
OK       queues: 0 fact(s) waiting in the durable queues towards the WMS in the live wcs.db: transitions to the WMS 0, extraction outcomes to send 0, picked-extraction reports 0, cell give-backs 0
Test-WcsStorage WARNING (6 OK, 1 WARNING; exit code 1)
```

(The disk of the test machine was below the default warning threshold of 20% free. `wcs.db` counts
its `-wal`: the host had been stopped with `Stop-Process`, so the last commits were still there.)

- **disk**: free space on the volume of the data folder; `-WarnFreePercent` (20),
  `-CriticalFreePercent` (10), and optionally `-CriticalFreeGB`.
- **files**: sizes of `wcs.db` and of the monthly files, `-wal` included; a `-wal` above `-MaxWalMB`
  (1024) is WARNING: something keeps the log from being checkpointed.
- **growth** (`-StateFile`): appends one CSV line per run and reports the growth per day over the
  lines in the file, and when the volume would fill at that rate. `-MaxGrowthMBPerDay` turns it into a
  WARNING. Prune the file yourself (it keeps everything).
- **backup** (`-BackupRoot`): the newest folder under it that holds a `wcs-backup.json`; older than
  `-MaxBackupAgeHours` (26) or absent is CRITICAL. With `-DbToolPath`, `Wcs.DbTool verify` runs on it
  (integrity, schema, and the SHA-256 of every file against the manifest): a failure is CRITICAL. A
  backup task that fails leaves no new folder, so it shows up here as an old backup.
- **parked** and **queues** (`-DbToolPath`): `Wcs.DbTool verify` on the live `wcs.db`. It opens the
  file read-only through SQLite, like the online backup does, and prints the content of the durable
  queues towards the WMS; the script sums the parked rows (CRITICAL when any) and the rows waiting
  (WARNING above `-MaxQueuedFacts`, report only by default). `-ParkedFrom Backup` takes the counts from
  the newest backup instead, without touching the live database. Run the script as an account that can
  write the data folder: SQLite readers need the `-shm` file
  ([install the tool](backup-restore-and-disaster-recovery.md#install-the-tool)).

What a parked fact looks like, taken from a copy of a backup in which a cell give-back had been marked
parked for the test (the copy no longer matches its manifest, hence the first CRITICAL):

```text
CRITICAL backup: Wcs.DbTool verify 20260101-000000 failed (exit code 1): wcs.db FAILED sha256 differs from the manifest: the file changed after the backup
CRITICAL parked: 1 fact(s) towards the WMS parked in the backup 20260101-000000 (refused for good; they hold back later facts): follow the parked-facts procedure in known-limitations.md, do not edit wcs.db
OK       queues: 1 fact(s) waiting in the durable queues towards the WMS in the backup 20260101-000000: transitions to the WMS 0, extraction outcomes to send 0, picked-extraction reports 0, cell give-backs 1
```

That row was written into a **copy**, by the test, to see the alarm. Never edit the live `wcs.db`: the
procedure for a parked fact is in [known limitations](../known-limitations.md#parked-facts-towards-the-wms).

## Test-WcsEvents.ps1

The host writes its warnings and errors through ASP.NET Core logging: to the console, and on Windows
also to the Application event log.

**Where `RIFIUTATA DEFINITIVAMENTE` is written.** When the WMS refuses a fact for good, the engine
writes one line at level Error (`Navigator.WriteLog`, in
[`PendingWarehouseTransitionDispatcher.cs`](../../WCS/Wcs.Engine/src/PendingWarehouseTransitionDispatcher.cs),
[`PendingCellGiveBackDispatcher.cs`](../../WCS/Wcs.Engine/src/PendingCellGiveBackDispatcher.cs) and
[`PendingPickedExtractionReportDispatcher.cs`](../../WCS/Wcs.Engine/src/PendingPickedExtractionReportDispatcher.cs)).
The line goes to the host's console and, through the host's logger (category
`Wcs.Host.Engine.EngineHostedService`), to the event log. It is **not** in the operators' log
(`GET /api/v1/log`), and it is written **once**, when the fact is parked: after a restart the log no
longer shows it, the parked row stays. That is why the count from `Wcs.DbTool verify`
(`Test-WcsStorage.ps1`) is the alarm that survives restarts, and the log text is the one that comes
first.

**Which event source.** Observed on the test machine, with the host started from a console: the
events are in the Application log with source **`.NET Runtime`**, event id 1000, and the message
starts with `Category: <logger> EventId: 0`. Other .NET programs use that source too, so the script
keeps only the events whose category starts with `Wcs.`. A host running as a Windows service may
register a source of its own (by default the application name, `Wcs.Host`); the script reads that
source as well. **Not verified**: no service was installed on the test machine. Check on your server:

```powershell
Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = (Get-Date).AddHours(-1) } |
    Where-Object Message -match 'Category: Wcs\.' | Select-Object -First 3 ProviderName, Id, TimeCreated
```

From the event log, with the sample WMS stopped for a minute while the engine was in Run:

```powershell
pwsh -NoProfile -File tools\monitoring\Test-WcsEvents.ps1 -EventLog -SinceMinutes 30 -ServiceName Wcs.Host -Examples 1
```

```text
WARNING  wms: 35 failed call(s) to the WMS, in the Application event log, last 30 min (Wcs.Host, .NET Runtime). E.g. "Category: Wcs.Host.Engine.EngineHostedService EventId: 0 Error during the ProcessoAssegnazioni process, Description: Il gestionale non ha risposto a «GetPendingExtractionWork». StackTrace: at Wcs.Wms...."
OK       restarts: no unexpected stop of Wcs.Host in the last 30 min
Test-WcsEvents WARNING (1 OK, 1 WARNING; exit code 1)
```

From a log file (a console host whose output is redirected to a file), remembering how far it read:

```powershell
pwsh -NoProfile -File tools\monitoring\Test-WcsEvents.ps1 -LogFile <root>\logs\host.log -StateFile <root>\events.state.json
```

The first run reads the whole file, the next ones only the lines added since (from the start again if
the file was replaced). The console shows most engine lines twice, once as written by the engine and
once through the logger, so the counts from a log file are counts of lines, not of events.

What it looks for:

| Text (as the host writes it) | Level | Meaning |
|---|---|---|
| `RIFIUTATA DEFINITIVAMENTE` | CRITICAL | a fact refused for good by the WMS and parked |
| `Motore non caricato` | CRITICAL | the engine could not be loaded ("engine not loaded"); the host retries |
| `Il gestionale non ha risposto` | WARNING | a call to the WMS failed |
| `WITHOUT CONNECTION` | WARNING | a Connected device has no driver connection |
| `served by the SIMULATOR` | WARNING | a Connected device runs on the simulator (`Wcs:AllowSimulatorForMissingConnections`) |
| any other event at level Error or Critical (event log), or `fail:`/`crit:` line (log file) | WARNING | counted, with an example |

**Restarts** (`-ServiceName`, Windows): Service Control Manager events 7031 and 7034 ("the service
terminated unexpectedly") for that service, and Application Error 1000 and .NET Runtime 1026 for
`Wcs.Host.exe`, within `-SinceMinutes`. A planned restart produces none of these. **Not verified**
beyond the query running and finding nothing: no service was installed, and nothing crashed, on the
test machine. A restart can also be seen as a short uptime (`Test-WcsHealth.ps1 -ProcessName Wcs.Host
-MinUptimeMinutes 10`) and as the line `Motore caricato da ...` ("engine loaded from") in the log.

## SignalR is not a health check

The hub (`/hub/plant`) sends `Watchdog` every second and `NavigatorStatusChanged` when the engine
changes state ([the host API](host-api.md#the-signalr-hub)). It is useful for screens, and a missing
watchdog is a hint, but it is **not a persistent health check**:

- it says the host process and its event relay are alive, not that the engine loops are turning, that
  the WMS answers, or that no fact is parked;
- a connection that drops is gone until the client reconnects, and what happened meanwhile is not
  replayed: the client must read the full state again;
- the host drops the oldest events when it cannot send them fast enough, and then asks the clients to
  reload;
- nothing records the history of what a hub client saw.

Monitor with the scripts (or the same GET requests from your own system); use the hub for live views.

## Scheduling

On the host's Windows server, with Task Scheduler, run the scripts every minute (health, events) and
every hour or after the backup (storage), as a dedicated account:

1. Create a local account for monitoring, allowed to write the data folder only if it runs
   `Test-WcsStorage.ps1 -DbToolPath`, and allowed to read the event log.
2. Give that account the read-only key in a user environment variable, so that only it can read it:
   log in as that account and run `[Environment]::SetEnvironmentVariable('WCS_MONITOR_API_KEY', '<read-only key>', 'User')`.
3. Create the tasks with `pwsh.exe -NoProfile -NonInteractive -File <folder>\Test-WcsHealth.ps1
   -ServiceName Wcs.Host` (and the others), "run whether the user is logged on or not".
4. Send the alarm where people see it: most monitoring agents (Nagios, Icinga, Zabbix, PRTG, Checkmk)
   can run a command and read its exit code and first lines; or let the task's failure (any non-zero
   exit code) raise a notification of your monitoring system.

Every alarm needs a person and a procedure: parked facts in particular
([known limitations](../known-limitations.md#parked-facts-towards-the-wms)). The list for production
is in the [production readiness checklist](production-readiness-checklist.md#7-monitoring).
