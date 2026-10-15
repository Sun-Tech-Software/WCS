# Backup, restore and disaster recovery

What the WCS host keeps on disk, how to copy it so that the copy is usable, how to put it back, how to
upgrade and roll back, and what to check before a restored WCS moves anything. The databases are
copied with `Wcs.DbTool`, a console tool in this repository
([`WCS/tools/Wcs.DbTool`](../../WCS/tools/Wcs.DbTool)), or by hand with the host stopped.

Every command on this page was run on 2026-10-15 on a test installation: the host published from this
repository, the [sample WMS](../../samples/README.md) and the sample plant, on a test bench, with the
host on port 5591 and the sample WMS on 5592 (`$wcs` and `$wms` hold their key headers, as in the
[quick start](../quick-start.md#7-start-the-engine)). The installation was laid out as `<root>\Wcs`
(the published host, the role of `C:\Wcs`; its data in `<root>\Wcs\Data`), `<root>\DbTool` (the
tool) and `<root>\Backups`, and the commands ran from `<root>`. In the outputs the long scratch path
is shortened to `<root>`, and one JSON file is trimmed where marked; nothing else is changed.

## What to back up

| What | Where | Why |
|---|---|---|
| `wcs.db` | `Wcs:DatabaseDirectory` | the plant (devices, junctions, door mappings, drawing), driver settings, commands, missions, the extraction queue and the **durable queues of facts not yet delivered to the WMS**. Without it the WCS does not know its plant |
| `wcs-history-YYYY-MM.db` | same folder, one per month | handled load units and the operators' log, `Wcs:HistoryMonthsToKeep` months (12 by default) |
| `wcs-plc-YYYY-MM.db` | same folder, one per month | the field driver's message log, 2 months |
| their `-wal` and `-shm` | same folder | only in a copy made with the host stopped, and only if they are there: see [below](#how-the-host-writes-its-databases) |
| `appsettings.Production.json` | next to `Wcs.Host.exe` | your settings. The published `appsettings.json` comes back with the binaries |
| `appsettings.Secrets.json`, or the machine-level variables `Wcs__*`, `Wms__*` | next to `Wcs.Host.exe`, or the machine | the keys. **A backup that holds them is a secret**: store it accordingly, or keep the keys in your password vault instead |
| the HTTPS certificate (`.pfx`), if any | where `Kestrel:Endpoints:Https:Certificate:Path` says | |
| the field driver's DLLs, if any | next to `Wcs.Host.exe` | |
| the published folder of the version in service | `C:\Wcs`, without `Data` | to [roll back](#upgrade-and-rollback) |
| the service definition | `sc.exe qc Wcs.Host` | name, account, start type |
| optionally, the plant as JSON | `GET /api/v1/plant/graph` | a second, version-independent copy of the plant alone ([deployment](deployment-and-operations.md#back-up)) |

`Wcs:DatabaseDirectory` is `Data` by default. A relative value is taken from the folder of the
executable (`AppContext.BaseDirectory`), not from the working directory, so a service, a console
started elsewhere and a console started in `C:\Wcs` all use `C:\Wcs\Data`. The host logs the folder
at every start: `Motore caricato da C:\Wcs\Data` ("engine loaded from"). In production, consider an
absolute folder outside the binaries (for example `C:\WcsData`): then publishing or rolling back a
version never touches the data.

Not needed: `wwwroot` and the DLLs (they come with the binaries), the logs (the Windows Event Log keeps
its own), the browser sessions of the web editor (people reconnect).

## How the host writes its databases

All three kinds of file are SQLite databases in **WAL mode**, with `synchronous = NORMAL`
([`WcsDatabase.cs`](../../WCS/Wcs.Engine/src/Persistence/WcsDatabase.cs),
[`WcsHistoryDatabase.cs`](../../WCS/Wcs.Engine/src/Persistence/WcsHistoryDatabase.cs),
[`WcsPlcLogDatabase.cs`](../../WCS/Wcs.Engine/src/Persistence/WcsPlcLogDatabase.cs)):

- a commit is written to the file's `-wal`, and reaches the `.db` only at a checkpoint;
- the host keeps its connections open (pooled) for as long as it runs, so nothing in SQLite
  guarantees a checkpoint before the process ends. The host checkpoints by itself
  (`PRAGMA wal_checkpoint(TRUNCATE)`) when it **stops cleanly**, and for a monthly file when the month
  rotates;
- `synchronous = NORMAL`: a power cut can lose the last commits, never the consistency of the file.

On the test installation, with the engine running and two pallets stored, everything was still in
the `-wal` files; the `.db` files held one page each:

```text
Name                        Length
----                        ------
wcs-history-2026-10.db        4096
wcs-history-2026-10.db-shm   32768
wcs-history-2026-10.db-wal  148352
wcs-plc-2026-10.db            4096
wcs-plc-2026-10.db-shm       32768
wcs-plc-2026-10.db-wal       61832
wcs.db                        4096
wcs.db-shm                   32768
wcs.db-wal                 1170112
```

After a clean stop (Ctrl+C on a console host, which runs the same shutdown as `Stop-Service`; it took
1.3 s) only the `.db` files were left, complete:

```text
Name                   Length
----                   ------
wcs-history-2026-10.db  69632
wcs-plc-2026-10.db      36864
wcs.db                 200704
```

After a hard stop (`Stop-Process -Force`, as a crash would do) the last commits were again in the
`-wal` files:

```text
Name                       Length
----                       ------
wcs-history-2026-10.db      69632
wcs-history-2026-10.db-shm  32768
wcs-history-2026-10.db-wal 107152
wcs-plc-2026-10.db          36864
wcs-plc-2026-10.db-shm      32768
wcs-plc-2026-10.db-wal       8272
wcs.db                     200704
wcs.db-shm                  32768
wcs.db-wal                 144232
```

Three rules follow:

1. **A `.db` copied without its `-wal` can be silently incomplete.** From the folder above, `wcs.db`
   alone passes the integrity check but has lost the mission of pallet 5004, created a few seconds
   before the crash (`missions 0`); copied with its `-wal` it has it (`missions 1`):

   ```text
   PS> .\DbTool\Wcs.DbTool.exe verify .\Backups\wcs-db-only\wcs.db
     wcs.db                   ok       schema current (20261013182943_AddMissionRelocatorArrival)
                              contents: plant items 7, missions 0, extraction queue 0, transitions to the WMS 0, of which parked 0, extraction outcomes to send 0, picked-extraction reports 0, of which parked 0, cell give-backs 0, of which parked 0
   PS> .\DbTool\Wcs.DbTool.exe verify .\Backups\wcs-crash-1\Data\wcs.db
     wcs.db                   ok       schema current (20261013182943_AddMissionRelocatorArrival)
                              contents: plant items 7, missions 1, extraction queue 0, transitions to the WMS 0, of which parked 0, extraction outcomes to send 0, picked-extraction reports 0, of which parked 0, cell give-backs 0, of which parked 0
   ```

2. **A `-wal` belongs to its own `.db` and to no other.** Never leave the `-wal` of one copy next to
   the `.db` of another (for example after putting a backup's `wcs.db` into a folder that still has
   the old `wcs.db-wal`): SQLite would apply it to the wrong file. Restore moves them together.
3. **A file copied while the host writes it is not a backup.** With the host running, use the tool
   (procedure B), which reads through SQLite.

## Install the tool

`Wcs.DbTool` uses the SQLite Online Backup API (`SqliteConnection.BackupDatabase`) and knows the
schema migrations of its own build. Publish it with each version of the host, into a folder of its
own, and keep it with that version's binaries:

```powershell
dotnet publish WCS\tools\Wcs.DbTool\Wcs.DbTool.csproj -c Release -o <root>\DbTool
```

```text
PS> .\DbTool\Wcs.DbTool.exe
Wcs.DbTool - backup, verify and restore the databases of the WCS host

  backup  --data <data folder> --out <new or empty folder>
          Online backup with the SQLite Online Backup API: the host may keep running.
  verify  <database file | data folder | backup folder>
          PRAGMA integrity_check and the schema against this build; on a backup folder also
          the SHA-256 of every file against wcs-backup.json. Never writes a database.
  restore --from <backup folder> --data <data folder> [--assume-stopped]
          Only with the host stopped: refuses a backup that does not verify and a data folder
          whose databases are open; moves the replaced files to <data folder>.replaced-<time>.
          --assume-stopped: needed outside Windows, where the open-file check is not available.

Exit code: 0 done, 1 refused or failed (see the output), 2 wrong arguments.
```

Run it as an account that can **write** the data folder (the service account, or an administrator):
reading a database in WAL mode needs its `-shm`, which SQLite creates next to it.

## Procedure A: with the service stopped

For a maintenance window, before an upgrade, or when you do not want the tool.

1. Stop the engine (command 1002) and wait for the missions in progress to end, if you can.
2. Stop the service: `Stop-Service Wcs.Host`. Check that `Wcs.Host.exe` is gone
   (`Get-Process Wcs.Host` finds nothing): the copy is valid only once the process has exited.
3. Copy the **whole** data folder, every file in it. After a clean stop it holds only `.db` files; if
   there are `-wal`/`-shm` files (the host did not stop cleanly), copy them too, together with their
   `.db`.
4. Copy the configuration files listed [above](#what-to-back-up).
5. Start the service again.

On the test installation, after the clean stop shown above:

```text
PS> New-Item -ItemType Directory .\Backups\wcs-stopped-1 | Out-Null
PS> Copy-Item .\Wcs\Data .\Backups\wcs-stopped-1\Data -Recurse
PS> .\DbTool\Wcs.DbTool.exe verify .\Backups\wcs-stopped-1\Data
Verify <root>\Backups\wcs-stopped-1\Data
  wcs.db                   ok       schema current (20261013182943_AddMissionRelocatorArrival)
                           contents: plant items 7, missions 0, extraction queue 0, transitions to the WMS 0, of which parked 0, extraction outcomes to send 0, picked-extraction reports 0, of which parked 0, cell give-backs 0, of which parked 0
  wcs-history-2026-10.db   ok       schema current (20260912162923_PlcMessagesOut)
  wcs-plc-2026-10.db       ok       schema current (20260912162138_InitialPlcLog)
All verified.
```

`verify` opens the copies read-only and never writes a database, but on a database in WAL mode SQLite
creates its `-shm` and an empty `-wal` beside it, as any reader does: after the command the copy
also holds `wcs.db-shm` (32768 bytes) and `wcs.db-wal` (0 bytes), and the same for the monthly
files. They are harmless.

The service stop and start were not run on the test bench (no service installed there): the host ran
from a console and was stopped with Ctrl+C, which is the shutdown `Stop-Service` triggers.

A folder copied this way is restored by copying it back, whole, into an empty data folder with the
service stopped. `Wcs.DbTool restore` takes only folders written by `Wcs.DbTool backup`; you can also
make one from a stopped data folder: the tool works with the host stopped as well.

## Procedure B: online, with Wcs.DbTool

With the host running and the engine in Run. The output folder must be new or empty:

```text
PS> .\DbTool\Wcs.DbTool.exe backup --data .\Wcs\Data --out .\Backups\wcs-online-2
Backup of <root>\Wcs\Data
     into <root>\Backups\wcs-online-2
  wcs.db                     196.0 KB    665 ms  ok       schema current (20261013182943_AddMissionRelocatorArrival)
                           contents: plant items 7, missions 1, extraction queue 0, transitions to the WMS 0, of which parked 0, extraction outcomes to send 0, picked-extraction reports 0, of which parked 0, cell give-backs 0, of which parked 0
  wcs-history-2026-10.db      68.0 KB     74 ms  ok       schema current (20260912162923_PlcMessagesOut)
  wcs-plc-2026-10.db          36.0 KB     99 ms  ok       schema current (20260912162138_InitialPlcLog)
Backup complete: 3 file(s), listed in wcs-backup.json.
```

The mission in the backup was pallet 5003 travelling to door `A0201` at that moment. What you get:

- **each file is a consistent snapshot** of its database: the tool copies every page under one read
  transaction, which in WAL mode does not stop the host's writers. The test
  `ABackupTakenWhileAWriterKeepsCommittingIsAConsistentSnapshot`
  ([`DbToolTest.cs`](../../WCS/tools/Wcs.DbTool/tests/Wcs.DbTool.Test/DbToolTest.cs)) copies a 20 MB
  `wcs.db` while another connection commits continuously, and checks an invariant that every commit
  keeps;
- the files are copied one after the other, so `wcs.db` and the monthly files are snapshots of
  instants a few milliseconds apart. The history and the PLC log are append-only journals that no row
  of `wcs.db` points into: the difference costs at most the last lines of a log;
- each copy is **one self-contained file** (rollback-journal mode, no `-wal`): the host puts it back
  in WAL mode when it opens it;
- each copy is **verified** (integrity and schema, as `verify` below), and listed with its size,
  SHA-256 and last migration in `wcs-backup.json`. Exit code 1 if any file failed;
- the source files are opened read-only and are not changed.

```text
PS> Get-Content .\Backups\wcs-online-1\wcs-backup.json
{
  "format": 1,
  "createdUtc": "2026-10-15T10:16:39.5922908Z",
  "source": "<root>\\Wcs\\Data",
  "machine": "SLEDGEHAMMER",
  "files": [
    {
      "name": "wcs.db",
      "kind": "Operational",
      "bytes": 200704,
      "sha256": "6b158843482b0ab08d9b1270c00ef6c16d1f2b6a47fbb72f7255c72698d0468b",
      "lastMigration": "20261013182943_AddMissionRelocatorArrival"
    },
    ...
  ]
}
```

(The first file shown; the two monthly files follow in the same form.)

### Scheduling it

Run the backup from Windows Task Scheduler (or your backup agent's pre-job hook) as an account that
can write the data folder, into a folder named by the time, then copy that folder **off the machine**
and prune old generations. The script, run three times on the test installation keeping two
generations:

```powershell
$stamp = Get-Date -Format yyyyMMdd-HHmmss
.\DbTool\Wcs.DbTool.exe backup --data .\Wcs\Data --out ".\Scheduled\$stamp" | Select-Object -Last 1
if ($LASTEXITCODE -ne 0) { throw "WCS backup failed (exit code $LASTEXITCODE)" }
Get-ChildItem .\Scheduled -Directory | Sort-Object Name -Descending | Select-Object -Skip 2 | Remove-Item -Recurse
```

```text
Backup complete: 3 file(s), listed in wcs-backup.json.
Backup complete: 3 file(s), listed in wcs-backup.json.
Backup complete: 3 file(s), listed in wcs-backup.json.

Name
----
20261015-122141
20261015-122143
```

A failed backup must reach a person: make the task's failure (the `throw`, exit code 1) raise an
alert, not only a line in a log nobody reads.

## Verify

`verify` takes a backup folder, a single database file, or a data folder (also a live one). It runs
`PRAGMA integrity_check`, compares the applied schema migrations with those of its own build, and,
on a backup folder, checks every file against the SHA-256 in `wcs-backup.json`.

| Result | Meaning |
|---|---|
| `ok  schema current` | sound, and at the schema of this build |
| `ok  schema older: N migration(s) pending` | sound, written by an earlier version; the host migrates `wcs.db` and the current month's files when it opens them, and reads past months as they are |
| `FAILED  schema newer than this build` | written by a later version: these binaries must not open it (see [rollback](#upgrade-and-rollback)) |
| `FAILED  integrity_check: ...` or `SQLite error ...` | the file is damaged |
| `FAILED  sha256 differs from the manifest` | the file changed after the backup |

For `wcs.db` it also prints what the file holds, for the checks after a restore: plant items,
missions, the extraction queue, and the durable queues towards the WMS with their **parked** rows
(facts the WMS refused for good, waiting for a person, see
[troubleshooting](troubleshooting.md)).

A copy of the online backup, with one page of `wcs.db` overwritten as a bad sector would:

```text
PS> .\DbTool\Wcs.DbTool.exe verify .\Backups\damaged
Verify <root>\Backups\damaged
  wcs-backup.json: 3 file(s), taken 2026-10-15 10:18:05 UTC from <root>\Wcs\Data on SLEDGEHAMMER
  wcs.db                   FAILED   sha256 differs from the manifest: the file changed after the backup
  wcs.db                   FAILED   SQLite error 11: SQLite Error 11: 'database disk image is malformed'.
  wcs-history-2026-10.db   ok       schema current (20260912162923_PlcMessagesOut)
  wcs-plc-2026-10.db       ok       schema current (20260912162138_InitialPlcLog)
Verification FAILED: see the lines above.
```

Exit code 1. Verify every backup where it is stored, not only where it was taken: the SHA-256 check
catches a copy damaged on the way.

## Restore

When: the data disk is lost or damaged, the data is wrong (a mistaken plant import, a corrupted
file), or an upgrade must be [rolled back](#upgrade-and-rollback).

1. Stop the engine (command 1002) if the host still runs, and stop the service.
2. Verify the backup you want (above).
3. Restore it. The tool refuses while any database of the data folder is open:

   ```text
   PS> .\DbTool\Wcs.DbTool.exe restore --from .\Backups\wcs-online-1 --data .\Wcs\Data
   Restore <root>\Backups\wcs-online-1
      into <root>\Wcs\Data
     wcs-backup.json: 3 file(s), taken 2026-10-15 10:16:39 UTC from <root>\Wcs\Data on SLEDGEHAMMER
     wcs.db                   ok       schema current (20261013182943_AddMissionRelocatorArrival)
                              contents: plant items 7, missions 0, extraction queue 0, transitions to the WMS 0, of which parked 0, extraction outcomes to send 0, picked-extraction reports 0, of which parked 0, cell give-backs 0, of which parked 0
     wcs-history-2026-10.db   ok       schema current (20260912162923_PlcMessagesOut)
     wcs-plc-2026-10.db       ok       schema current (20260912162138_InitialPlcLog)
   Refused: in use by another process (stop the host first): wcs-history-2026-10.db, wcs-history-2026-10.db-shm, wcs-history-2026-10.db-wal, wcs-plc-2026-10.db, wcs-plc-2026-10.db-shm, wcs-plc-2026-10.db-wal, wcs.db, wcs.db-shm, wcs.db-wal; nothing was changed
   ```

   With the host stopped (here after the hard stop shown above):

   ```text
   PS> .\DbTool\Wcs.DbTool.exe restore --from .\Backups\wcs-online-2 --data .\Wcs\Data
   Restore <root>\Backups\wcs-online-2
      into <root>\Wcs\Data
     wcs-backup.json: 3 file(s), taken 2026-10-15 10:18:05 UTC from <root>\Wcs\Data on SLEDGEHAMMER
     wcs.db                   ok       schema current (20261013182943_AddMissionRelocatorArrival)
                              contents: plant items 7, missions 1, extraction queue 0, transitions to the WMS 0, of which parked 0, extraction outcomes to send 0, picked-extraction reports 0, of which parked 0, cell give-backs 0, of which parked 0
     wcs-history-2026-10.db   ok       schema current (20260912162923_PlcMessagesOut)
     wcs-plc-2026-10.db       ok       schema current (20260912162138_InitialPlcLog)
   Restored 3 file(s), taken 2026-10-15 10:18:05 UTC.
   The 9 file(s) replaced are kept, unchanged, in <root>\Wcs\Data.replaced-20261015-121919
   ```

   Every WCS database of the folder **and its `-wal`/`-shm`** is moved, unchanged, into
   `<data>.replaced-<time>`, also monthly files the backup does not have; other files of the folder
   are left where they are. The backup's files are copied in and checked against their SHA-256. If
   anything fails half-way, the folder is put back as it was. The replaced files are your way back
   if the restore was a mistake: delete them only once the restored WCS is accepted.
4. On a new machine, first install the same version of the host, its configuration, the driver and
   the service; then restore; then start.
5. Start the service, and go through the [checklist](#after-a-restore-the-checklist) before Run.

The open-file check works on Windows, where SQLite's open handles block an exclusive open. Elsewhere
(Linux is not verified for the WCS) the tool refuses unless you stop the host and pass
`--assume-stopped`.

A restore also brings back months of history beyond `Wcs:HistoryMonthsToKeep`, if the backup has them:
the host deletes them at its next start.

### Restoring is going back in time

The restored WCS knows the plant as it was **at the backup**. The field and the WMS did not go back:
pallets have moved, facts have been delivered. On the test installation, after the restore above,
the WCS believed pallet 5003 was on CV02 heading for `A0201`, while the WMS had it stored for
seconds already:

```text
PS> (Invoke-RestMethod http://127.0.0.1:5591/api/v1/missions -Headers $wcs) | Select-Object loadUnitId, positionItemId, storageDoorCode, status
loadUnitId positionItemId                       storageDoorCode status
---------- --------------                       --------------- ------
   5003,00 5a000000-0000-4000-8000-000000000003 A0201                0

PS> Invoke-RestMethod http://localhost:5592/api/wcs/warehouse/load-units/5003/location -Headers $wms | ConvertTo-Json -Compress
{"loadUnitId":5003,"cellId":103,"storingDepth":1200}
```

Every load unit that moved since the backup must be reconciled between the WCS, the field and the
WMS **before Run**. Beware of the commands that look like a clean-up: **1004 `RemoveAllLu` reports
`load-units/release` to the WMS** for each load unit it removes. Run on the restored test host, it
cleared the stale mission and made the sample WMS forget pallet 5003, which was in cell 103:

```text
route              what
-----              ----
load-units/release load unit 5003 left the automation
```

and 2004 `ItemUnloadLu` reports the same release for its device ([commands](../reference/commands.md)).
Neither is a way to make the WCS forget something the WMS still holds. For a load unit that is on
another device than the WCS believes, `PUT /api/v1/missions/{id}/position` tells the engine where it
really is ([host API](../reference/host-api.md)). Decide each load unit with the people who run the
WMS. The facts still in the restored durable queues are sent again: delivery
is at least once, and your WMS's receipts per `OperationId` make the repeats harmless
([facts and receipts](wms-contract.md#facts-durable-queues-and-receipts)).

## After a restore: the checklist

Start with `Wcs:AutoRun` false (the default), so that the engine stays in Stop.

- [ ] The host starts and the engine loads: `GET /api/v1/status` answers `"engineLoadError":null`,
      `"navStatus":0` (Stop), and the log says `Motore caricato da <your data folder>`.
- [ ] The plant is there: `GET /api/v1/plant/items/status` lists your devices (7 on the sample plant),
      and the web editor shows the drawing.
- [ ] The missions (`GET /api/v1/missions`) and the load units on the devices match the field and the
      WMS; the differences are resolved ([above](#restoring-is-going-back-in-time)).
- [ ] The extraction queue (`GET /api/v1/extraction-queue`) is what the WMS expects.
- [ ] The parked rows are reviewed: `Wcs.DbTool verify` on the data folder prints them
      (`of which parked`); see [troubleshooting](troubleshooting.md) for what they mean.
- [ ] The devices are online and their connections built: `status` 6 is OffLine; Run is refused
      while a Connected device lacks its connection
      ([connecting machines](connecting-machines.md#when-connections-are-built)).
- [ ] Only then Run (command 1001), with someone watching the plant.

## Integrity checks and the periodic restore test

A backup nobody has restored is a hope. Besides the `verify` that every backup runs:

- **verify again where the backup is stored** (the SHA-256 check sees a copy damaged in transit or at
  rest);
- **restore the latest backup on a test bench, regularly** (monthly is a common choice), into a
  scratch folder, with a host that **cannot reach the production WMS or the PLCs**: `Wms:ApiAddress`
  pointing where nothing answers, `Wcs:ConnectionFactory` set to `none`, `Wcs:AutoRun` false, its
  own port and data folder. A host started on a copy of production with the real addresses would
  resend the facts of the durable queues to the real WMS and open the real PLC connections.

The test as run on the test bench, from `<root>`:

```powershell
$latest = Get-ChildItem .\Scheduled -Directory | Sort-Object Name -Descending | Select-Object -First 1
.\DbTool\Wcs.DbTool.exe restore --from $latest.FullName --data .\RestoreTest\Data
$env:Wcs__DatabaseDirectory = "$PWD\RestoreTest\Data"
$env:Wcs__ConnectionFactory = 'none'
$env:Wcs__AutoRun = 'false'
$env:Wcs__ApiKey = 'restore-test-key'; $env:Wms__ApiKey = 'restore-test-unused'
$env:Wms__ApiAddress = 'http://127.0.0.1:9'
$env:Kestrel__Endpoints__Http__Url = 'http://127.0.0.1:5591'
$testHost = Start-Process .\Wcs\Wcs.Host.exe -WorkingDirectory .\Wcs -WindowStyle Hidden -PassThru -RedirectStandardOutput .\restore-test.log
Start-Sleep 5
Invoke-RestMethod http://127.0.0.1:5591/api/v1/status -Headers @{ 'X-Api-Key' = 'restore-test-key' } | ConvertTo-Json -Compress
(Invoke-RestMethod http://127.0.0.1:5591/api/v1/plant/items/status -Headers @{ 'X-Api-Key' = 'restore-test-key' }).Count
Get-Content .\restore-test.log -TotalCount 4
Stop-Process $testHost
```

```text
Restore <root>\Scheduled\20261015-122143
   into <root>\RestoreTest\Data
  wcs-backup.json: 3 file(s), taken 2026-10-15 10:21:43 UTC from <root>\Wcs\Data on SLEDGEHAMMER
  wcs.db                   ok       schema current (20261013182943_AddMissionRelocatorArrival)
                           contents: plant items 7, missions 0, extraction queue 0, transitions to the WMS 0, of which parked 0, extraction outcomes to send 0, picked-extraction reports 0, of which parked 0, cell give-backs 0, of which parked 0
  wcs-history-2026-10.db   ok       schema current (20260912162923_PlcMessagesOut)
  wcs-plc-2026-10.db       ok       schema current (20260912162138_InitialPlcLog)
Restored 3 file(s), taken 2026-10-15 10:21:43 UTC.
The data folder had no WCS database: nothing replaced.
{"navStatus":0,"defragmentation":0,"version":"0.1.0.0","utcNow":"2026-10-15T10:26:48.9302781Z","historyFrom":"2026-10-01","engineLoadError":null}
7
warn: Wcs.Host.Engine.EngineHostedService[0]
      No field driver (Wcs:ConnectionFactory turns it off): only the loopback driver is available, and devices with connection type Connected stay without connection (OffLine), and Run is refused while the plant has any
info: Wcs.Host.Engine.EngineHostedService[0]
      Motore caricato da <root>\RestoreTest\Data
```

The environment variables apply to that PowerShell session only: close it afterwards. `Stop-Process`
is a hard stop, harmless on a throwaway copy.

The engine loads without a WMS. Before stopping the test host, go through the checks of the
[checklist](#after-a-restore-the-checklist) that do not need the plant (status, plant, missions,
queues), and write down how long it all took.

## Upgrade and rollback

### How the schema moves

The host migrates each database when it opens it for writing: `wcs.db` at the engine's load
(`WcsDatabase.Open` calls EF Core's `Migrate()`), and the current month's history and PLC-log files
when it creates or opens them. Past months are opened read-only and are **not** migrated. Migrations
only go forward: there is no down-migration, nothing runs one, and a database migrated by a newer
version is not guaranteed to work with the older binaries. The host does not log the migrations;
`verify` shows where a file stands.

A `wcs.db` two migrations behind (made with `dotnet ef database update` on this repository), opened by
the current host:

```text
PS> .\DbTool\Wcs.DbTool.exe verify .\UpgradeData\wcs.db
  wcs.db                   ok       schema older: 2 migration(s) pending, applied when the host opens it for writing (last applied 20261011153636_AddPendingPickedExtractionReports)
                           contents: plant items 0, missions 0, extraction queue 0, transitions to the WMS 0, of which parked 0, extraction outcomes to send 0, picked-extraction reports 0, of which parked 0
```

(started and stopped the host on that folder)

```text
PS> .\DbTool\Wcs.DbTool.exe verify .\UpgradeData\wcs.db
  wcs.db                   ok       schema current (20261013182943_AddMissionRelocatorArrival)
                           contents: plant items 0, missions 0, extraction queue 0, transitions to the WMS 0, of which parked 0, extraction outcomes to send 0, picked-extraction reports 0, of which parked 0, cell give-backs 0, of which parked 0
```

### Upgrade

1. Read the release notes of the new version.
2. Stop the engine (command 1002), ideally with no mission in progress, and stop the service.
3. Back up with the tool (it works with the host stopped too) into a folder named after the version
   you are leaving, for example `before-upgrade-<version>`, and keep a copy of the published folder
   of that version, its `Wcs.DbTool` included.
4. Publish the new version ([deployment](deployment-and-operations.md#upgrade)), and its `Wcs.DbTool`.
5. Start the service; check `GET /api/v1/status` and the log; `verify` the data folder with the new
   tool: `schema current`.
6. Go through the [checklist](#after-a-restore-the-checklist) before Run.
7. Keep the pre-upgrade backup and the previous binaries until the new version is accepted.

### Rollback

1. Stop the engine and the service.
2. Put back the previous version's published folder.
3. With the **previous version's** `Wcs.DbTool`, restore the pre-upgrade backup. The migrated files go
   aside into `<data>.replaced-<time>`.
4. Start the service and go through the [checklist](#after-a-restore-the-checklist): whatever the WCS
   did between the upgrade and the rollback is not in the restored files, and must be reconciled as
   after any restore.

The rollback of the test above:

```text
PS> .\DbTool\Wcs.DbTool.exe restore --from .\Backups\before-upgrade --data .\UpgradeData
Restore <root>\Backups\before-upgrade
   into <root>\UpgradeData
  wcs-backup.json: 1 file(s), taken 2026-10-15 10:20:53 UTC from <root>\UpgradeData on SLEDGEHAMMER
  wcs.db                   ok       schema older: 2 migration(s) pending, applied when the host opens it for writing (last applied 20261011153636_AddPendingPickedExtractionReports)
                           contents: plant items 0, missions 0, extraction queue 0, transitions to the WMS 0, of which parked 0, extraction outcomes to send 0, picked-extraction reports 0, of which parked 0
Restored 1 file(s), taken 2026-10-15 10:20:53 UTC.
The 5 file(s) replaced are kept, unchanged, in <root>\UpgradeData.replaced-20261015-122356
```

(Only one version of the host exists in this repository, so the test restored the older file with the
current tool; the previous binaries were not started.)

Why the previous version's tool: a tool refuses a backup whose schema is newer than its own build
(`FAILED schema newer than this build`, and `restore` stops), which is what keeps a migrated file
away from older binaries. The test `RestoreRefusesABackupWrittenByALaterVersion` holds that.

## RPO, RTO and retention

These are your decisions: they depend on the plant, its shifts, and what your WMS can rebuild. What the
WCS does to them:

**RPO (how much you can lose).** With procedure B it is the interval between backups, plus the time
before the copy leaves the machine; with procedure A alone it is the interval between maintenance
windows. Everything that happened after the backup is lost from the WCS and must be reconciled by
hand: each load unit moved, each mission, each fact delivered. Choose the interval by how much of
that your operators can rebuild in an acceptable time. The databases are small (about 300 KB on the
test plant; the tool prints size and milliseconds per file, measure yours) and the online backup does
not stop the plant, so frequent backups are cheap.

**RTO (how long until the plant runs again).** The restore itself takes seconds (on the test bench the
engine was loaded within five seconds of the restore command). The time goes into noticing the failure,
getting a machine with the right binaries and configuration, and above all the reconciliation of the
load units in flight and the checklist. The periodic restore test measures the first part; your
procedures for the reconciliation decide the rest.

**Retention.** Keep several recent generations (a mistake is often noticed late), some older ones
(daily, weekly), and the pre-upgrade backups until each upgrade is accepted. A backup holds what is
in the data folder: up to `Wcs:HistoryMonthsToKeep` months of history and two months of PLC log. To
keep history longer, archive each past month's `wcs-history-YYYY-MM.db` once: after the month
rotates the host does not write it any more and has checkpointed it, so the `.db` alone is complete.
Backups contain the operators' log, and the keys if you include `appsettings.Secrets.json`: protect
them like the server.

## Test bench and production

| | Test bench | Production |
|---|---|---|
| backup | procedure A or B, whenever useful | procedure B on a schedule, with alerting, copied off the machine; procedure A before upgrades |
| keys in the backup | test keys, no harm | keep them out of the backup or encrypt it |
| restore | any time | after a decision; then the full checklist and the reconciliation with the WMS |
| restore test | the place where it runs: isolated from the production WMS and PLCs | never on the production host |
| upgrade | try the new version on a restored copy of production first | after the test bench, with the pre-upgrade backup and the previous binaries at hand |
| commands 1004 / 2004 after a restore | harmless against the sample WMS | they report releases to your WMS |
