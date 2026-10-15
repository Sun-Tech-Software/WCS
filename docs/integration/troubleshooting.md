# Troubleshooting

Symptoms, what they mean and what to do. The messages are quoted as the host writes them (most are
in Italian), with a translation. For the first run of the quick start see also
[its own troubleshooting](../quick-start.md#troubleshooting-the-first-run).

## The host does not start

| Message (host console or Event Log) | Cause | Fix |
|---|---|---|
| `Wcs:ApiKey manca o e' quella di esempio: senza chiave l'host non parte.` ("missing or the sample one") | `Wcs:ApiKey` empty or the placeholder `cambiami-prima-di-installare` | set `Wcs__ApiKey` or `appsettings.Secrets.json` |
| the same, although `appsettings.Secrets.json` with the key is next to `Wcs.Host.exe` | the host was started from a console in another folder: it reads its settings files from the working directory | start it from its folder (`cd C:\Wcs; .\Wcs.Host.exe`) ([content root](../reference/configuration.md)) |
| `Wcs:ReadOnlyApiKey deve essere diversa da Wcs:ApiKey.` | the two keys are equal | choose a different read-only key, or remove it |
| `Wms:ApiKey manca o è quella di esempio: ...` | `Wms:ApiKey` empty or the placeholder | set `Wms__ApiKey` to the key your adapter expects |
| `Wms:ApiAddress manca: ...` / `Wms:ApiAddress non è un indirizzo http assoluto: "..."` | missing, or not `http(s)://host:port` | an origin without path, e.g. `http://localhost:5142` |
| `Wms:TimeoutSeconds è 2: il minimo è 5 secondi, ...` | below 5 | 5 or more (30 by default) |
| `Wcs:HistoryMonthsToKeep vale 0: il minimo è 1 (il mese corrente). ...` | below 1 | 1 or more |
| `Endpoint HTTP remoto non consentito: http://0.0.0.0:5580. Configura HTTPS oppure, soltanto su una LAN d'impianto segregata, imposta Wcs__AllowInsecureRemoteHttp=true.` | a plain HTTP endpoint that is not the loopback | HTTPS, or loopback, or (segregated network only) `Wcs:AllowInsecureRemoteHttp` |
| `Failed to bind to address http://127.0.0.1:5580: address already in use.` | another process has the port | stop it, or set `Kestrel__Endpoints__Http__Url` to another port |
| `Wcs:ConnectionFactory = "...": the type was not found. ...` (and the other loader messages) | the driver named in `Wcs:ConnectionFactory` does not load | fix the name, copy the DLL next to `Wcs.Host.exe`, or set `none` ([choosing the driver](connecting-machines.md#choosing-the-driver)) |
| `Impossibile ricavare l'indirizzo a cui l'Editor web deve collegarsi per parlare all'host: ...` | `Kestrel:Endpoints:Http:Url` (or `Https:Url`) is not `scheme://host:port` | write it in that form |

## The host runs but the engine is not loaded

`GET /api/v1/status` answers `"engineLoadError": "..."` and the other routes answer `503` with
`"unavailable":"engineNotLoaded"`. The host log has
`Motore non caricato: {reason}. Nuovo tentativo fra 30 s; ...` ("engine not loaded; new attempt in
30 s"). The host retries every `Wcs:EngineLoadRetrySeconds`; the reason says what failed (for example
the data folder cannot be created or written). The engine does not need the WMS to load: a host
whose WMS is down loads its engine and reports the WMS failures later, when it calls it.

## Talking to the WMS

| Symptom | Cause | Fix |
|---|---|---|
| host log, every turn in Run: `Error during the ProcessoAssegnazioni process, Description: Il gestionale non ha risposto a «GetPendingExtractionWork».` and `Error On Navigator->BuildLUExtraction Description: Il gestionale non ha risposto a «GetPendingNoteWork».` ("the WMS did not answer") | the adapter is down, or `Wms:ApiAddress` is wrong, or it refuses the key, or it answers a 4xx/5xx or a body the WCS cannot read | call the same URL with the same key: `Invoke-RestMethod http://localhost:5142/api/wcs/warehouse/extractions/pending-work -Headers @{ 'X-Api-Key' = '...' }` |
| `GET /api/v1/wms/cells` answers `503`, `"unavailable":"warehouseUnreachable"` | same | same |
| a load unit stays on the infeed device, no `POST reservations` in the adapter's log | the device does not ask for a cell (`lookWhereAllocateLoadUnit` 0), or the description of the load unit failed | check the device configuration; check `GET load-units/{id}/description` |
| `POST reservations` answers `outcome` 6 and the load unit waits | no free cell reachable by the `ReachableDeviceIds` the WCS sent | check that your cells carry the plant device ids the WCS uses (the relocator, the storage device) |
| the load unit reaches the conveyor before the relocator and stops there | the relocator has `motionType` 0 (MoveTo): it must be 1 (GetFrom) | [plant modelling](plant-modelling.md#storage-relocators-and-cells) |
| the relocator does not store and the host log says `... Description: To ASI Destination is Nothing` | no field address for the store: `recordStorageLength` 0 on the relocator, the door code unknown to the WCS's door mappings, or no partition at the expected offset (`-1` for FixedLength) in `cells/{id}/partitions` | fix the relocator, the door mappings or the partition map |
| `extractions/pending-work` offers a row, the WCS never reports it `Extracted` and the order never closes | the WMS looks rows up by header: the `Extracted` outcome comes with `extractionHeaderId` 0 | find rows by `extractionRowId` ([contract](wms-contract.md#an-order-is-extracted)) |
| the WCS reports a row `NotReachable` (2) on every turn | the load unit is not in a cell within `bufferAreaIds` (not stored yet, in another area) | as you decide: keep it, or cancel the row |
| host log: `Transizione WMS {id} (Allocate, UDC 5001) RIFIUTATA DEFINITIVAMENTE e parcheggiata: ...` ("refused for good and parked") | the adapter answered a 4xx (other than 408/429) to a fact from a durable queue | fix the adapter. **There is no API to inspect, retry or discard a parked fact**, and a restart does not release it: it stays in `wcs.db` (table `PendingWarehouseTransitions`) and holds back the later facts about the same load unit, and a parked release every later allocation; the device holding the load unit goes to Warning. Keep the host running, do not edit `wcs.db`, keep the log line, and open an issue or contact Sun Tech ([parked facts](../known-limitations.md#parked-facts-towards-the-wms)). Answer 5xx, not 4xx, for temporary problems |
| host log: `... la restituzione della cella {id} ... e' stata RIFIUTATA DEFINITIVAMENTE dal gestionale e resta parcheggiata ...` ("the give-back of the cell was refused for good and stays parked; free the cell by hand") | the adapter answered a 4xx to `reservations/release-load-unit-cell` | fix the adapter, then free the cell in the WMS. The parked row holds nothing else back ([parked facts](../known-limitations.md#parked-facts-towards-the-wms)) |
| host log: `... la riapertura dell'ordine ... e' stata RIFIUTATA DEFINITIVAMENTE dal gestionale e resta parcheggiata ...` ("reopening the order was refused for good; put the order right in the WMS by hand") | the adapter answered a 4xx to `assignments/picked-extraction-became-deposit` | fix the adapter, then put the order right in the WMS. The parked row holds nothing else back |
| the adapter sees the same `load-units/allocate` twice | at-least-once delivery: an answer was lost or slow | keep receipts per `OperationId` ([receipts](wms-contract.md#facts-durable-queues-and-receipts)) |
| the adapter returns `400` to requests the WCS sends | casing or number format | accept PascalCase and camelCase, and ids like `5001.0`, in bodies and paths ([what is on the wire](wms-contract.md#what-is-on-the-wire)) |

## Commands and the plant

| Symptom | Cause | Fix |
|---|---|---|
| `503`, `Il motore è in Stop: il comando 3002 richiede Run` | the engine is not in Run | send 1001 and wait for `navStatus` 1 |
| `400`, `Tipo di comando sconosciuto: ...` | not a command code | [command reference](../reference/commands.md) |
| a command stays at `status` 0 | the engine is not loaded, or not running its loop | check `/status` |
| a command ends with `status` 99 and `Target device not found for ItemCommandId '...'` | wrong `itemId` | use the device's `itemId` from `GET /api/v1/plant/items` |
| command 3002 on a device that already holds a load unit ends Done and nothing happens | by design: the device is taken | unload it first (2004) |
| the operators' log says `Workstation requested: Unload Lu LU005001 on Item 1001.0001.0001` after a command 3002 | the log text of command 3002 uses the wording of an unload; nothing is unloaded | none: a known wording defect of that log line |
| `423`, `Nessuno tiene il permesso di modifica` / `Permesso di modifica di qualcun altro` | a plant write without the edit lease, or with someone else's | `POST /api/v1/plant/edit-lock`, then `X-Edit-Holder` ([edit lease](host-api.md#the-edit-lease-423)) |
| `409`, `Il motore non è fermo` | a plant write while the engine is in Run (or a Stop-only write in Pause) | command 1002, wait for `navStatus` 0 |
| `409`, `Impossibile sostituire il grafo con lavoro pendente` | `PUT /plant/graph` with active missions or queued extractions | let them finish; to start from scratch in a test setup, stop the host and delete its data folder |
| `400` on `PUT /plant/graph` with a reason such as `Due giunzioni collegano ... nello stesso verso` | the file fails validation | fix the file ([import and export](plant-modelling.md#import-and-export)) |
| command 1001 ends with status 99 and `Run refused: ... device(s) of connection type Connected have no connection ...`; the engine stays in Stop and the devices named go OffLine (status 6) | Connected devices without their connection: no field driver, no connection id, or no connection built for the id (plant imported without reload, no driver settings row, or the factory returned `null`) | install the driver or fix the connection, reload the plant (1003 in Stop), put the devices online (2003), then 1001 ([connecting machines](connecting-machines.md#when-connections-are-built)) |
| the operators' log says `...; the device is served by the SIMULATOR (setting Wcs:AllowSimulatorForMissingConnections)` | the explicit fallback is on, and a Connected device's connection was not built | as above; and turn the setting off outside a test installation |
| the host does not start: `Wcs:AllowSimulatorForMissingConnections is true in the Production environment (or in one with no name): the host does not start. ...` | the fallback setting is on in Production | remove it, or set it to `false` |
| a device stays in Warning (status 89) | the last movement failed (an error code, a timeout, a connection error) | read the mission's description and the log; fix the cause; retry with command 2001 |

## The web editor

| Symptom | Cause | Fix |
|---|---|---|
| `/editor` loads a blank page; the host log has `L'Editor web NON funzionera': ...` | the host runs outside Development from a `dotnet build` output | `--environment Development` (or `ASPNETCORE_ENVIRONMENT=Development`) for a development run; `dotnet publish` for an installation |
| the key page says `Key rejected by the host` | wrong key | the host's `Wcs:ApiKey` (or the read-only key, which cannot write) |
| a published host's `/editor` loads a blank page, with no warning in the log; `/_framework/blazor.web.js` answers `200` with an empty body | the host was started from a console in another folder: it serves `wwwroot` from the working directory | start it from its folder (`cd C:\Wcs; .\Wcs.Host.exe`) |
| the key page says `The host is not answering: check that it is running and reachable` | the editor cannot call the host at `Kestrel:Endpoints:Http:Url` | check that address from the server itself |
| the list of cells to link a device to is empty | the adapter answers `cells?areaType=2&automaticOnly=true` (or 3) with no cell | put the bay cells in automatic areas of type 2 or 3 ([contract](wms-contract.md#the-web-editors-two-routes)) |

## Where to look

1. `GET /api/v1/status`: is the engine loaded, which state.
2. The host console or Event Log: start-up, driver, failures towards the WMS.
3. `GET /api/v1/log?from=...&to=...`: what the engine did and why it refused.
4. `GET /api/v1/missions` and `GET /api/v1/plant/items/status`: where the load units are now.
5. Your adapter's own log of the requests it received (the sample keeps the last ones in
   `GET /sample-admin/state`, `recentCalls`).
6. The [monitoring scripts](monitoring-and-alerting.md): one run of `Test-WcsHealth.ps1`,
   `Test-WcsStorage.ps1` and `Test-WcsEvents.ps1` lists devices in Error, Warning or OffLine, parked
   facts, facts waiting for the WMS, disk space, the newest backup and the alarm texts in the log.
