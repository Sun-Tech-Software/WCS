# Samples

Two things to run the WCS end to end on one machine, with no PLC and no real WMS:

| Folder | What it is |
|---|---|
| [`Wcs.SampleWms`](Wcs.SampleWms/README.md) | A sample WMS: an ASP.NET Core minimal API that answers the WCS -> WMS contract (`/api/wcs/warehouse/...`) from an in-memory warehouse. It is also the template of a real adapter in front of your WMS. |
| [`plant`](plant) | `sample-plant.json`, a small plant in the WCS's own import format (`PlantGraphDto`), and `load-sample-plant.ps1`, which loads it into a running WCS host. |
| `Wcs.SampleWms.Test` | The tests of the sample, among them `QuickStartEndToEndTest`, which runs the walk-through below automatically. |

## The sample plant

```
IN01 --> CV01 --> CV02 --> SC01 <--> ST01 (rack A: cells 101-106, doors A0101 ... A0302)
                             |
                             v
                           CV03 --> OUT01
```

| Device | Type | What it does |
|---|---|---|
| `IN01` | Loading (1) | Infeed bay. `LookWhereAllocateLoadUnit` = 2: a load unit put here asks the WMS for a storage cell. Linked to the WMS cell 800, which tells the WCS which warehouse to store in. |
| `CV01`, `CV02`, `CV03` | Automation (0) | Conveyors. |
| `SC01` | Automation (0) | Stacker crane. `MotionType` = GetFrom (it picks the load unit from the conveyor itself), `RecordStorageLength` = FixedLength (it stores at the cell's only position). |
| `ST01` | Storage (3) | The rack, served by `SC01` (`RelocateItemId`). Its door mappings link the six door codes to the WMS cells 101-106. |
| `OUT01` | Buffer (2) | Outfeed bay. Linked to the WMS cell 900, where extraction orders deliver. |

Every device has `ConnectionType` = 0 (Test): it runs on the loopback driver, which completes each
move after the device's `DelayBlind` (500 ms). Nothing on the network but the WCS and the WMS.

## Walk-through (Windows, PowerShell, .NET 10 SDK)

Ports used: 5142 for the sample WMS and 5580 for the WCS host, their defaults. If one of them is
taken on your machine, see "Other ports" below.

**1. Start the sample WMS** (first terminal, from the repository root):

```powershell
dotnet run --project samples/Wcs.SampleWms
```

It listens on `http://localhost:5142` and expects the key `sample-wms-key` (from its
`appsettings.json`).

**2. Start the WCS host** (second terminal). The host refuses to start with the placeholder keys of
its `appsettings.json`, so give it its own key and the sample WMS's key:

```powershell
$env:Wcs__ApiKey = 'my-wcs-key'          # the key clients use to call the WCS
$env:Wms__ApiKey = 'sample-wms-key'      # the key the WCS sends to the WMS
${env:Logging__LogLevel__System.Net.Http.HttpClient} = 'Warning'   # optional: one log line per WMS call otherwise
dotnet run --project WCS/Wcs.Host/src
```

The host listens on `http://127.0.0.1:5580`, keeps its data in `Data` next to its executable
(`wcs.db` and the history), and starts with the engine stopped and an empty plant.

**3. Load the sample plant** (third terminal):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ./samples/plant/load-sample-plant.ps1 -ApiKey my-wcs-key
```

`-ExecutionPolicy Bypass` applies to this one run: Windows PowerShell refuses every script by default
("running scripts is disabled on this system"), and PowerShell 7 refuses one downloaded from the
internet unless it is signed. With PowerShell 7 installed, `pwsh` instead of `powershell` works the same.

```text
Plant loaded into http://127.0.0.1:5580 : 7 devices, 7 junctions, 8 door mappings.
```

**4. Start the engine** (command 1001, Run):

```powershell
$wcs = @{ 'X-Api-Key' = 'my-wcs-key' }
$wms = @{ 'X-Api-Key' = 'sample-wms-key' }
Invoke-RestMethod -Method Post http://127.0.0.1:5580/api/v1/commands -Headers $wcs -ContentType application/json -Body '{ "commandType": 1001 }'
```

The command is queued and the engine takes it on its next turn. Wait until the status says Run
(`navStatus` 1; 0 is Stop):

```powershell
Invoke-RestMethod http://127.0.0.1:5580/api/v1/status -Headers $wcs
```

From then on the WCS asks the WMS for pending work about once a second, and accepts the commands
that move load units (they are refused with 503 while the engine is stopped).

**5. Put pallet 5001 on the infeed** (command 3002, LoadLu, on `IN01`):

```powershell
Invoke-RestMethod -Method Post http://127.0.0.1:5580/api/v1/commands -Headers $wcs -ContentType application/json `
  -Body '{ "commandType": 3002, "itemId": "5a000000-0000-4000-8000-000000000001", "loadUnitId": 5001 }'
```

The WCS asks the WMS what 5001 is (`GET load-units/5001/description`), asks for a cell
(`POST reservations`: the WMS answers cell 101, door `A0101`), carries the pallet
IN01 -> CV01 -> CV02 -> SC01 -> ST01, and reports it stored (`POST load-units/allocate`). A few
seconds later:

```powershell
Invoke-RestMethod http://localhost:5142/api/wcs/warehouse/load-units/5001/location -Headers $wms
```

```text
loadUnitId cellId storingDepth
---------- ------ ------------
      5001    101         1200
```

**6. Ask for it back**: create an extraction order in the WMS, as its users would. This route is the
sample's own (`/sample-admin`), not part of the contract:

```powershell
Invoke-RestMethod -Method Post http://localhost:5142/sample-admin/extraction-orders -Headers $wms -ContentType application/json `
  -Body '{ "loadUnitIds": [ 5001 ] }'
```

On its next turn the WCS reads the order (`GET extractions/pending-work`), finds the pallet in cell
101 (`POST load-units/find`), and the crane brings it ST01 -> SC01 -> CV03 -> OUT01. When the
crane picks it the WCS reports the row extracted (`POST extractions/outcomes`) and the WMS closes
the order; when it reaches OUT01 the WCS reports it in the bay cell 900 (`POST load-units/allocate`).

```powershell
(Invoke-RestMethod http://127.0.0.1:5580/api/v1/plant/items/status -Headers $wcs) | Select-Object name, luLoadStatus, loadedLuCode
```

**7. Take it off the bay** (command 2004, ItemUnloadLu, on `OUT01`):

```powershell
Invoke-RestMethod -Method Post http://127.0.0.1:5580/api/v1/commands -Headers $wcs -ContentType application/json `
  -Body '{ "commandType": 2004, "itemId": "5a000000-0000-4000-8000-000000000007" }'
```

The WCS reports that the pallet left the automation (`POST load-units/release`) and the WMS marks
it shipped.

**What the WMS saw**: everything the sample holds, the journal of the facts the WCS reported, and
the last requests it received:

```powershell
Invoke-RestMethod http://localhost:5142/sample-admin/state -Headers $wms | ConvertTo-Json -Depth 6
```

The web editor of the host is at `http://127.0.0.1:5580/editor` when the host runs in the
Development environment (`$env:ASPNETCORE_ENVIRONMENT = 'Development'` before `dotnet run`) or is
installed with `dotnet publish`.

### Other ports

```powershell
dotnet run --project samples/Wcs.SampleWms --urls http://localhost:6142     # the sample WMS
$env:Wms__ApiAddress = 'http://localhost:6142'                               # the WCS host then calls it there
$env:Kestrel__Endpoints__Http__Url = 'http://127.0.0.1:6580'                 # and listens on 6580
powershell -NoProfile -ExecutionPolicy Bypass -File ./samples/plant/load-sample-plant.ps1 -HostUrl http://127.0.0.1:6580 -ApiKey my-wcs-key
```

### Starting again

The sample WMS keeps everything in memory: restarting it brings back the six empty cells. The WCS
host keeps its plant and missions in `wcs.db`: stop the engine (command 1002) and load the plant
again, or delete the `Data` folder of the host while it is stopped.

## The same walk-through, as a test

```powershell
dotnet test samples/Wcs.SampleWms.Test --filter QuickStartEndToEndTest
```

It starts the sample WMS and a WCS host on free ports, with a temporary data folder, runs the
script and the commands above, and checks each step through the contract.
