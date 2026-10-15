# Production readiness checklist

A checklist for moving a WCS installation from the **test bench** (commissioning: a real WMS adapter
and machines being brought up, people watching, nobody depending on the plant yet) to **production**
(the plant works on it). The **demo** (sample WMS, loopback driver, one machine) needs none of this
and must never become a production installation by growing into one.

Each point says how to check it. The "Test bench" column says what is acceptable before production;
the "Production" column what must be true from the first day of production. Nothing here replaces the
plant's own risk assessment, its acceptance tests or the requirements in [SAFETY.md](../../SAFETY.md).

## 1. Safety

| | Test bench | Production | How to check |
|---|---|---|---|
| The plant's risk assessment covers the machines driven through the WCS, independently of it | in progress | done and signed by whoever is responsible for the machines | the plant's documentation |
| Emergency stops, safety PLCs, guards and interlocks work without the WCS host, the network and the driver | verified before any machine moves under WCS control | verified, and re-verified after changes | test with the host stopped and with the network cable pulled |
| Operating procedures for manual commands (2001, 2004, 2007, 1004, 1005, 3003-3006) and for stop and recovery exist and are known to operators | drafted | approved and trained | the procedures; [commands](../reference/commands.md) |
| FAT and SAT done; simulation (loopback driver) was not counted as either | FAT done or planned | FAT and SAT passed | acceptance records |

## 2. Field driver and connections

| | Test bench | Production | How to check |
|---|---|---|---|
| A driver for the real machines is installed, named in `Wcs:ConnectionFactory` and its version recorded | yes | yes | host log at start: `Field driver: ...` |
| The driver was tested on these machines: every movement type, every completion code the engine reacts to, connection loss and reconnection | in progress | done | test records; [completion codes](connecting-machines.md#completion-codes-the-engine-reacts-to) |
| Every device that moves a real machine has connection type Connected, a driver settings row, and its connection is built | yes | yes | command 1001 is not refused; no device OffLine with `OpenConnection: ... WITHOUT CONNECTION` in the log |
| `Wcs:AllowSimulatorForMissingConnections` is off | allowed only while a driver is missing, with nobody depending on the simulated devices | off (the host refuses to start with it in Production) | the settings files and environment; no `...served by the SIMULATOR` line in the log |
| After every plant import (`PUT /api/v1/plant/graph`), the plant was reloaded with command 1003 or the host restarted | yes | yes | [when connections are built](connecting-machines.md#when-connections-are-built) |
| No device of connection type Test is left in the plant where a real machine stands | allowed | none | `GET /api/v1/plant/items`, `connectionType` |

## 3. Host installation

| | Test bench | Production | How to check |
|---|---|---|---|
| The host was installed with `dotnet publish`, and runs as a Windows service | either | yes | [deployment](deployment-and-operations.md) |
| `ASPNETCORE_ENVIRONMENT` is Production (or unset) | any | Production | `/openapi/v1.json` is not served |
| The sample WMS is not installed | allowed | not installed | |
| One host per plant, one data folder per host | yes | yes | |
| The version (commit) of the WCS and of the driver in use is recorded | yes | yes | your change log |
| The server's OS and architecture were run on the test bench before production (only Windows 11 x64 is tested for this release) | yes | yes | [supported platforms](supported-platforms.md) |
| A capacity test ran on the production hardware at the plant's peak throughput, and the disk was sized from its measurements | planned | passed | [performance and sizing](performance-and-sizing.md#measuring-a-real-plant) |

## 4. Keys

| | Test bench | Production | How to check |
|---|---|---|---|
| `Wcs:ApiKey`, `Wcs:ReadOnlyApiKey`, `Wms:ApiKey` are long random values, different from the test bench ones | test values | production values, generated for this plant | |
| Keys are in `appsettings.Secrets.json` or machine environment variables, not in `appsettings.json` | yes | yes | |
| The secrets file is readable only by the service account and administrators | recommended | yes | file permissions |
| Monitoring and screens use the read-only key; the full key is held by a known, short list of systems and people | recommended | yes | |
| The rotation procedure is written and has been tried once | - | yes | [rotating keys](security-and-threat-model.md#rotating-and-revoking-keys) |

## 5. Network and transport

| | Test bench | Production | How to check |
|---|---|---|---|
| The host, the WMS adapter and the PLCs are on a plant network segregated from the office network and the Internet | recommended | yes | network diagram, firewall rules |
| The host is reached on the loopback, through a reverse proxy with TLS, or over Kestrel HTTPS | as needed | yes | `Kestrel:Endpoints`; [transport](security-and-threat-model.md#transport-http-https-and-a-reverse-proxy) |
| `Wcs:AllowInsecureRemoteHttp` is off, or on only on a segregated LAN with the risk accepted in writing | as needed | off, or documented | no `Critical` transport line in the log |
| The WMS adapter accepts only the WCS's key and is reachable only by the WCS host | yes | yes | |
| No port of the host is reachable from the Internet | yes | yes | external scan |

## 6. Backups and recovery

| | Test bench | Production | How to check |
|---|---|---|---|
| The data folder is backed up, and a restore has been **tested** on another machine | recommended | yes | [backup, restore and disaster recovery](backup-restore-and-disaster-recovery.md) |
| The plant is exported (`GET /api/v1/plant/graph`) after every change | yes | yes | |
| The procedure to rebuild the host on a new server (install, settings, keys, driver, data) is written | - | yes | |
| The upgrade procedure (stop engine, stop service, back up, publish, start, check) is written | - | yes | [upgrade](deployment-and-operations.md#upgrade) |

## 7. Monitoring

| | Test bench | Production | How to check |
|---|---|---|---|
| Someone watches the host log (console or Event Log) for errors | during tests | yes, with alerts | `Test-WcsEvents.ps1` ([monitoring](monitoring-and-alerting.md#test-wcseventsps1)) |
| **Parked facts** towards the WMS: an alert on the log text `RIFIUTATA DEFINITIVAMENTE` and on the count of parked rows, and a person who follows the procedure | check after every test run | alert and procedure in place | [parked facts](../known-limitations.md#parked-facts-towards-the-wms); `Test-WcsEvents.ps1`, `Test-WcsStorage.ps1 -DbToolPath` |
| `GET /api/v1/status` is polled (read-only key): engine loaded, state, no `engineLoadError` | recommended | yes | `Test-WcsHealth.ps1` ([monitoring](monitoring-and-alerting.md#test-wcshealthps1)) |
| Devices in Warning (89) or OffLine (6) are seen and handled | yes | yes | `GET /api/v1/plant/items/status`, the hub; `Test-WcsHealth.ps1` |
| Disk space of the data folder, and the growth of the databases, are watched | - | yes | `Test-WcsStorage.ps1 -StateFile` ([monitoring](monitoring-and-alerting.md#test-wcsstorageps1)) |
| The newest backup is recent and verifies | - | yes | `Test-WcsStorage.ps1 -BackupRoot -DbToolPath` |
| WMS calls failing (`Il gestionale non ha risposto`) raise an alert | recommended | yes | [troubleshooting](troubleshooting.md#talking-to-the-wms); `Test-WcsEvents.ps1` |
| Unexpected restarts of the service raise an alert | - | yes | `Test-WcsEvents.ps1 -ServiceName` |
| Every alarm has a person and a procedure | - | yes | |

## 8. Known limits accepted

| | Test bench | Production |
|---|---|---|
| The [known limitations](../known-limitations.md) were read, and each one that matters for the plant has an owner and a workaround (shared keys and no per-operator identity, no API for parked facts, preview status, Linux not verified) | yes | yes |

## Sign-off

Production starts when every line in the "Production" column holds, and the people responsible for the
plant's safety, its IT network and its operation have agreed. The WCS repository does not define who
those people are: that belongs to the plant's organisation.
