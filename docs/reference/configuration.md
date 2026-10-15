# Reference: configuration

The WCS host is an ASP.NET Core application. It reads its settings, in this order (the later one
wins):

1. `appsettings.json` in the content root ([the tracked one](../../WCS/Wcs.Host/src/appsettings.json));
2. `appsettings.{Environment}.json` in the content root, if present (none is shipped);
3. `appsettings.Secrets.json` next to `appsettings.json`, if present: optional, never tracked,
   reloaded when it changes ([`SecretsFileConfiguration.cs`](../../WCS/Wcs.Host/src/SecretsFileConfiguration.cs)),
   but the host reads `Wcs:ApiKey`, `Wcs:ReadOnlyApiKey` and `Wms:ApiKey` once, at start: a changed key
   takes effect at the next start ([rotating keys](../integration/security-and-threat-model.md#rotating-and-revoking-keys));
4. environment variables, with `__` in place of `:` (`Wcs__ApiKey`, `Wms__ApiAddress`, ...);
5. the command line (`--Wcs:StartEngine=false`).

The **content root** is where the three files are read from, and where `wwwroot` (the web editor's
files) is served from. The host does not set it, so it is the ASP.NET Core default:

- as a Windows service: the folder of the executable;
- from a console: the **current working directory**. Start a published host from its own folder
  (`cd C:\Wcs; .\Wcs.Host.exe`). Started from another folder, it does not see the
  `appsettings*.json` next to it (with the keys in `appsettings.Secrets.json` it exits with
  `Wcs:ApiKey manca o e' quella di esempio`), and even with the keys in environment variables its
  `/editor` page stays blank: `/_framework/blazor.web.js` answers `200` with an empty body, and no
  warning is logged;
- with `dotnet run --project WCS/Wcs.Host/src`: the project folder `WCS/Wcs.Host/src`, whatever the
  working directory.

Two things are always found next to the executable, whatever the content root: `Wcs:DatabaseDirectory`
when it is relative (`Data` by default), and the field driver's DLL
([`EngineHostedService.cs`](../../WCS/Wcs.Host/src/Engine/EngineHostedService.cs)).

## Section `Wcs`

| Key | Default | Required | What it does |
|---|---|---|---|
| `Wcs:ApiKey` | `cambiami-prima-di-installare` (a placeholder) | **yes** | The full key: every request to `/api/v1` and to the hub sends it in `X-Api-Key`. The host **does not start** while it is empty or the placeholder: `Wcs:ApiKey manca o e' quella di esempio: senza chiave l'host non parte.` |
| `Wcs:ReadOnlyApiKey` | empty | no | A second key that allows `GET` and the hub only (`403` on anything else; no route answers `HEAD`). If set, it must differ from `Wcs:ApiKey` and from the placeholder, or the host does not start. |
| `Wcs:AllowInsecureRemoteHttp` | `false` | no | A plain `http://` endpoint on anything but the loopback stops the host at start, unless this is `true`; then the host logs a `Critical` line at every start. Only for a segregated plant network. |
| `Wcs:DatabaseDirectory` | `Data` | no | Where `wcs.db`, `wcs-history-YYYY-MM.db` and `wcs-plc-YYYY-MM.db` live. Relative = under the executable's folder. |
| `Wcs:HistoryMonthsToKeep` | `12` | no | How many monthly history files are kept, the current one included; older ones are deleted at start and at the change of month. Less than 1 stops the host. |
| `Wcs:MaxRequestBodyMegabytes` | `256` | no | The largest request body Kestrel accepts (its own default is 30 MB). |
| `Wcs:ConnectionFactory` | absent | no | The field driver, as `"Namespace.TypeName, AssemblyName"`, loaded from the host folder. Absent: the default of the build (none in this repository). `none` (any case) or empty: no field driver. See [connecting machines](../integration/connecting-machines.md#choosing-the-driver). As an environment variable write `Wcs__ConnectionFactory=none`: on Windows an empty variable does not exist. |
| `Wcs:AllowSimulatorForMissingConnections` | `false` | no | `true` serves a device of connection type Connected whose connection was not built (no driver settings row with its id, a row the driver refused, a plant imported without reloading it) with the loopback driver, with a warning, and Run is not refused for it. `false`: such a device gets no connection and goes OffLine, and Run is refused while it lacks it. The host **does not start** with `true` in the Production environment. Only for a test installation. See [connecting machines](../integration/connecting-machines.md#when-connections-are-built). |
| `Wcs:StartEngine` | `true` | no | `false` serves the API without starting the engine (no database, no WMS). |
| `Wcs:AutoRun` | `false` | no | `true` puts the engine in Run right after it is loaded (as command 1001 would), opening the driver connections. Refused, as command 1001 is, while a Connected device lacks its connection: the engine stays in Stop and the host logs why. |
| `Wcs:EngineLoadRetrySeconds` | `30` | no | If loading the engine fails at start, the host stays up (`GET /api/v1/status` gives the reason in `engineLoadError`) and retries every this many seconds (minimum 1). |

## Section `Wcs:Engine`

Global options of the engine, copied to it at start.

| Key | Default | What it does |
|---|---|---|
| `Wcs:Engine:MasterProcessOnEvery` | `1000` | Period of the master loop, in milliseconds: queued commands, the four durable queues towards the WMS, and (in Run) the pull of `extractions/pending-work` and `notes/pending-work`. |
| `Wcs:Engine:DefaultRejectItem` | `00000000-0000-0000-0000-000000000000` | Where a rejection asked by a person (command 2008) sends the load units of a device that has no `RejectDestinationUserRequest` of its own. |
| `Wcs:Engine:NumCurrentStoringStep` | `0` | Used when a load unit may be stored in any available cell: with a value above 0 the WCS offers the WMS (`reachableDeviceIds`) only the storage devices whose relocator is not OffLine and has fewer than this many storing missions in progress; with 0, every storage device's relocator. |
| `Wcs:Engine:LockCellOnStoringError` | `false` | When the depth a load unit was stored at exceeds the cell's depth, `true` locks the cell for storing (quarantine) instead of only logging a suggestion to check it. |
| `Wcs:Engine:LimitOneShippingNotePerBuffer` | `false` | Not applied by the engine: sent to the WMS as `?limitOneShippingNotePerBuffer=` on `notes/pending-work`, for the WMS to apply. |
| `Wcs:Engine:DiscardLuManagedInWarehouse` | `false` | Read at start; nothing in the engine uses it today. |
| `Wcs:Engine:DisableUnloadLu` | `false` | `true` refuses command 2004 (unload a load unit from a device). |
| `Wcs:Engine:SwitchLuLengthVsLuWidth` | `false` | `true` swaps length and width in the load unit data a device sends (`AsiSendLuData`). |
| `Wcs:Engine:ActivateDebugLog` | `false` | `true` also writes the engine's information lines to the host log; warnings and errors are written anyway. |
| `Wcs:Engine:AutoUnloadDelayMilliseconds` | `3000` | How long a load unit stays on a device with `AutoUnloadLoadUnit` before it is unloaded automatically (0 = at once). |

## Section `Wms`

| Key | Default | Required | What it does |
|---|---|---|---|
| `Wms:ApiAddress` | `http://localhost:5142` | **yes** with `Wcs:StartEngine` | The origin of your WMS adapter: scheme, host and port, **no path** (the WCS appends `/api/wcs/warehouse/...`). Must be an absolute `http` or `https` address, or the host does not start. |
| `Wms:ApiKey` | `cambiami-prima-di-installare` (a placeholder) | **yes** with `Wcs:StartEngine` | Sent as `X-Api-Key` on every call to the WMS. The host does not start while it is empty or the placeholder. |
| `Wms:TimeoutSeconds` | `30` | no | The timeout of a single call to the WMS. Below 5 the host does not start. |

`Wms:ConnectionString`, found in old installations, is ignored.

## Kestrel, environment and logging

| Key | Default | What it does |
|---|---|---|
| `Kestrel:Endpoints:Http:Url` | `http://127.0.0.1:5580` | Where the host listens. The web editor also uses it (or `Kestrel:Endpoints:Https:Url`) to call the host back: it must read `scheme://host:port`, and a wildcard host (`0.0.0.0`, `*`, `+`, `::`) becomes `localhost`. |
| `ASPNETCORE_ENVIRONMENT` (environment variable) | `Production` | `Development` lets `dotnet run`/`dotnet build` output serve the web editor's files and maps `/openapi/v1.json`. The OpenAPI document is mapped in every environment except `Production`. |
| `Logging:LogLevel:Default` | `Information` | ASP.NET Core logging: console; on Windows also the Event Log. |
| `Logging:LogLevel:Microsoft.AspNetCore` | `Warning` | |
| `Logging:LogLevel:Microsoft.AspNetCore.SignalR.HubConnectionHandler` | `Debug` | Why SignalR closed a connection (also set under `Logging:EventLog:LogLevel`). |
| `Logging:LogLevel:System.Net.Http.HttpClient` | (inherits `Default`) | Set it to `Warning` to stop the per-call lines of the calls to the WMS (about two a second in Run). In PowerShell: `${env:Logging__LogLevel__System.Net.Http.HttpClient} = 'Warning'`. |

## A complete example

`appsettings.Secrets.json`, next to `Wcs.Host.exe` (a console host started from that folder, or a
service):

```json
{
  "Wcs": { "ApiKey": "a-long-random-key", "ReadOnlyApiKey": "another-long-random-key" },
  "Wms": { "ApiKey": "the-key-your-wms-adapter-expects" }
}
```

`appsettings.Production.json`, next to it:

```json
{
  "Wcs": { "DatabaseDirectory": "D:\\WcsData", "HistoryMonthsToKeep": 24 },
  "Wms": { "ApiAddress": "http://wms-adapter.plant.local:8080", "TimeoutSeconds": 15 },
  "Kestrel": { "Endpoints": { "Http": { "Url": "http://127.0.0.1:5580" } } }
}
```

The same with environment variables (PowerShell, for one console session):

```powershell
$env:Wcs__ApiKey = 'a-long-random-key'
$env:Wms__ApiKey = 'the-key-your-wms-adapter-expects'
$env:Wms__ApiAddress = 'http://wms-adapter.plant.local:8080'
$env:Wcs__DatabaseDirectory = 'D:\WcsData'
```

## The sample WMS (`samples/Wcs.SampleWms`)

| Key | Default | What it does |
|---|---|---|
| `Urls` | `http://localhost:5142` | Where it listens; also `--urls` or the `URLS` environment variable. A `Kestrel__Endpoints__Http__Url` variable in the same environment overrides it (it logs `Overriding address(es) ...`). |
| `SampleWms:ApiKey` | `sample-wms-key` | The key it expects in `X-Api-Key` (the host's `Wms:ApiKey`). Public: change it before listening on anything but localhost. |
| `SampleWms:ReservationLifetimeMinutes` | `10` | A storage reservation nobody asked about for this long is freed. |
