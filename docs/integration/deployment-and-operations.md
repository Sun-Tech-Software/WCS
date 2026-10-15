# Deployment and operations

How to install the WCS host on a plant server, where it keeps its data, how to read its logs, and how
to upgrade and secure it. Backup, restore, rollback and disaster recovery have their own page:
[backup, restore and disaster recovery](backup-restore-and-disaster-recovery.md); watching it has
[monitoring and alerting](monitoring-and-alerting.md). Windows 11 x64 is the platform the WCS is built
and tested on; the host targets `net10.0` and has no Windows-only call in its path, but Windows Server,
Linux and ARM64 have not been verified ([supported platforms](supported-platforms.md)). No hardware
requirements are published: size the server with a capacity test
([performance and sizing](performance-and-sizing.md)).

## Publish

Install with `dotnet publish`, not with the output of `dotnet build`: only a published host carries
the web editor's static files (`wwwroot/_framework`, `_content`) next to the executable.

```powershell
dotnet publish WCS\Wcs.Host\src\Wcs.Host.csproj -c Release -o C:\Wcs
```

The folder then holds `Wcs.Host.exe`, its DLLs, `appsettings.json` and `wwwroot`. A host started
outside the Development environment without `wwwroot/_framework` logs this warning at start, and its
`/editor` page stays blank (the API is not affected):

```text
L'Editor web NON funzionera': accanto all'eseguibile (...) non c'e' wwwroot/_framework, quindi le risorse statiche di Blazor non sono servibili. ...
```

("The web editor will NOT work: next to the executable there is no wwwroot/_framework ...; install
the host with dotnet publish, not with dotnet build".)

A field driver goes next to `Wcs.Host.exe` too
([choosing the driver](connecting-machines.md#choosing-the-driver)).

To try the published host from a console before installing the service, start it **from its
folder**:

```powershell
cd C:\Wcs
.\Wcs.Host.exe
```

From a console the host reads its `appsettings*.json` files and serves `wwwroot` from the current
working directory, not from the folder of the executable. Started from elsewhere
(`C:\Wcs\Wcs.Host.exe` typed in another folder) it exits with `Wcs:ApiKey manca o e' quella di esempio`
although `appsettings.Secrets.json` is in `C:\Wcs`, and with the keys in environment variables it
starts but its `/editor` page stays blank, with no warning
([content root](../reference/configuration.md)). A service always uses the folder of the executable.

## Configure

Keep your settings out of the published `appsettings.json`: a later `dotnet publish` into the same
folder overwrites it. Use, next to the executable (and start a console host from that folder, see
above):

- `appsettings.Secrets.json` for the keys (optional, never tracked; the host reads the keys at start,
  so a changed key takes effect at the next start, see
  [rotating keys](security-and-threat-model.md#rotating-and-revoking-keys));
- `appsettings.Production.json` for the rest (the host runs in the Production environment unless
  `ASPNETCORE_ENVIRONMENT` says otherwise);
- or machine-level environment variables (`Wcs__ApiKey`, `Wms__ApiAddress`, ...).

The minimum: `Wcs:ApiKey`, `Wms:ApiAddress`, `Wms:ApiKey`. Every key is in the
[configuration reference](../reference/configuration.md), with a complete example.

## Run as a Windows service

The host calls `UseWindowsService` with the service name `Wcs.Host`, so the same executable runs from
a console and as a service. From an elevated PowerShell:

```powershell
New-Service -Name Wcs.Host -BinaryPathName 'C:\Wcs\Wcs.Host.exe' `
            -DisplayName 'WCS Host' -StartupType Automatic
Start-Service Wcs.Host
```

or with `sc.exe` (the space after `binPath=` is required):

```cmd
sc create Wcs.Host binPath= "C:\Wcs\Wcs.Host.exe" start= auto DisplayName= "WCS Host"
sc start Wcs.Host
```

To remove it: `Stop-Service Wcs.Host; sc.exe delete Wcs.Host`.

A service starts in `System32`, not in its folder: the settings files and `wwwroot` are still read
from the executable's folder (a service's content root), and so is a relative
`Wcs:DatabaseDirectory` (`Data`).
The service account must be able to write the data folder and reach the WMS adapter over HTTP; it
needs nothing else (no database server).

Check it is up:

```powershell
Invoke-RestMethod http://127.0.0.1:5580/api/v1/status -Headers @{ 'X-Api-Key' = '<your key>' }
```

The engine starts in Stop (unless `Wcs:AutoRun` is true): start it with command 1001 from your
system, from the web editor, or with `Wcs:AutoRun`. Run is refused while a device of connection type
Connected lacks its connection (no field driver, no connection id, or no connection built for it):
the engine stays in Stop and the refusal lists the devices
([connecting machines](connecting-machines.md#when-connections-are-built)).

## Network

| Setting | Default | |
|---|---|---|
| `Kestrel:Endpoints:Http:Url` | `http://127.0.0.1:5580` | loopback only |

To serve other machines, listen on an address they can reach. A plain `http://` endpoint on anything
but the loopback **stops the host at start** unless you set `Wcs:AllowInsecureRemoteHttp` to `true`
(then every start logs a `Critical` line): with HTTP, whoever can read the traffic can read the key
and command the plant. Prefer HTTPS, with the standard Kestrel configuration:

```json
{
  "Kestrel": {
    "Endpoints": {
      "Https": {
        "Url": "https://wcs.plant.local:5581",
        "Certificate": { "Path": "C:\\Wcs\\wcs.pfx", "Password": "..." }
      }
    }
  }
}
```

in `appsettings.Production.json`. That file adds the `Https` endpoint; it cannot remove the `Http`
one, which comes from the published `appsettings.json`, so the host keeps listening on
`http://127.0.0.1:5580` as well. Leave it there: it is on the loopback, so only programs on the
same server reach it, and the web editor uses it. The editor runs inside the host and calls the host
back on `Kestrel:Endpoints:Http:Url` (or `Https:Url` when there is no `Http`); a wildcard host there
(`0.0.0.0`, `*`, `+`, `::`) becomes `localhost`. To move it to another loopback port, set
`Kestrel:Endpoints:Http:Url` in `appsettings.Production.json`. To drop it, you would have to delete
it from `appsettings.json`, and do it again after every `dotnet publish` into that folder.

The WCS calls your WMS adapter at `Wms:ApiAddress`; that connection is plain HTTP or HTTPS as the
address says.

## Data files

Everything the WCS keeps is in `Wcs:DatabaseDirectory` (`Data` next to the executable by default),
in SQLite files in WAL mode (each with its `-wal` and `-shm` companions while open; while the host
runs, the recent commits are only in the `-wal`, see
[how the host writes its databases](backup-restore-and-disaster-recovery.md#how-the-host-writes-its-databases)):

| File | Contents | Retention |
|---|---|---|
| `wcs.db` | the plant (devices, junctions, door mappings, positions, decoration), driver settings and their status, commands, missions, extraction queue, the durable queues towards the WMS | permanent |
| `wcs-history-YYYY-MM.db` | one per month: handled load units, the operators' log (`GET /api/v1/log`) | `Wcs:HistoryMonthsToKeep` months, 12 by default |
| `wcs-plc-YYYY-MM.db` | one per month: the field driver's message log (`GET /api/v1/history/plc-messages`) | 2 months |

What makes each file grow, and how to estimate the disk they need with their retention:
[performance and sizing](performance-and-sizing.md#what-drives-disk).

**Schema migrations run by themselves**: `wcs.db` and the current month's files are migrated to the
current schema when the host opens them (past months are read as they are). There is no migration
step to run at an upgrade, and no way back: a rollback restores the backup taken before the upgrade
([upgrade and rollback](backup-restore-and-disaster-recovery.md#upgrade-and-rollback)). (Developers
who change the schema use the `dotnet-ef` tool pinned in [`WCS/dotnet-tools.json`](../../WCS/dotnet-tools.json).)

## Logs

| Where | What |
|---|---|
| console (and the Windows Event Log, warnings and errors) | ASP.NET Core logging: start-up, the driver line, engine errors and warnings, failures of calls to the WMS. Levels in `Logging:LogLevel` |
| `GET /api/v1/log` (history files) | the operators' log: what the engine did with each load unit, commands requested, connection problems, the driver's own lines. In English and Italian |
| `GET /api/v1/history/plc-messages` | the messages the field driver chose to record |

Lines worth knowing (most host messages are in Italian):

| Line | Meaning |
|---|---|
| `Field driver: ...` / `No field driver (...)` | which driver is installed |
| `Motore caricato da C:\Wcs\Data` | the engine is loaded, from that folder |
| `Motore non caricato: {reason}. Nuovo tentativo fra 30 s; ...` | the engine could not be loaded; retried every `Wcs:EngineLoadRetrySeconds` |
| `Motore non avviato: Wcs:StartEngine è falso` | the API runs without the engine |
| `Error during the ProcessoAssegnazioni process, Description: Il gestionale non ha risposto a «GetPendingExtractionWork».` | the WMS did not answer the pull of pending work (once a turn while it is down) |
| `Transizione WMS {id} (Allocate, UDC 5001) RIFIUTATA DEFINITIVAMENTE e parcheggiata: ...` | a durable fact refused for good by the WMS and parked (see [facts and receipts](wms-contract.md#facts-durable-queues-and-receipts)) |

Which of these lines raise an alarm, where the Event Log puts them, and scripts that look for them:
[monitoring and alerting](monitoring-and-alerting.md).

`Wcs:Engine:ActivateDebugLog` true adds the engine's information lines to the host log.
`Logging:LogLevel:System.Net.Http.HttpClient` set to `Warning` removes the per-request lines of the
calls to the WMS.

## Upgrade

1. Stop the engine (command 1002) and the service (`Stop-Service Wcs.Host`).
2. Back up the data folder with `Wcs.DbTool`, and keep the published folder of the version you are
   leaving ([upgrade](backup-restore-and-disaster-recovery.md#upgrade)).
3. Publish the new version into the folder (`dotnet publish ... -o C:\Wcs`), or copy it over. Your
   `appsettings.Secrets.json` and `appsettings.Production.json` are not touched; the published
   `appsettings.json` is replaced.
4. Copy the field driver again if it has a new version.
5. Start the service. The databases are migrated when opened; check `GET /api/v1/status` and the log.

The durable queues in `wcs.db` survive the upgrade: facts not yet delivered to the WMS are delivered
after the start. To go back, see [rollback](backup-restore-and-disaster-recovery.md#rollback).

## Back up

Backing up, restoring and recovering a host are described in
[backup, restore and disaster recovery](backup-restore-and-disaster-recovery.md). In short:

- **The data folder**: with `Wcs.DbTool backup` while the host runs, or by copying the whole folder
  with the service stopped. Never copy the database files of a running host. Both procedures, the
  restore and the checks after it are in
  [backup, restore and disaster recovery](backup-restore-and-disaster-recovery.md).
- **The plant**, at any time: `GET /api/v1/plant/graph` returns the whole plant as one JSON file
  (the web editor's "Export plant"); `PUT /api/v1/plant/graph` restores it on an engine in Stop.

```powershell
Invoke-RestMethod http://127.0.0.1:5580/api/v1/plant/graph -Headers @{ 'X-Api-Key' = '<your key>' } |
    ConvertTo-Json -Depth 10 | Set-Content -Encoding utf8 "plant-$(Get-Date -Format yyyyMMdd).json"
```

## Security checklist

The short list for every installation. The reasoning, the network flows and the key rotation
procedure are in [security and threat model](security-and-threat-model.md); the full list for a
production plant is the [production readiness checklist](production-readiness-checklist.md).

- [ ] `Wcs:ApiKey` and `Wms:ApiKey` are long random values, kept in `appsettings.Secrets.json` or
      environment variables, never in a tracked file. The host refuses the placeholder.
- [ ] Monitoring and viewers use `Wcs:ReadOnlyApiKey` (GET and hub only).
- [ ] Keys travel only in headers (`X-Api-Key`; the hub refuses `access_token` in the URL).
- [ ] The host listens on the loopback, or on HTTPS; `Wcs:AllowInsecureRemoteHttp` only on a
      segregated plant network.
- [ ] The OpenAPI document is served only outside Production; the production host runs in Production.
- [ ] `Wcs:AllowSimulatorForMissingConnections` is not set (the host refuses to start with it in
      Production): a Connected device without its connection goes OffLine instead of being simulated.
- [ ] The web editor's pages load without a key, but the editor does nothing until a key is entered,
      and then acts with that key's rights.
- [ ] The WMS adapter checks `X-Api-Key` and listens where only the WCS host can reach it.
- [ ] The sample WMS (public key `sample-wms-key`) is not installed in production.
