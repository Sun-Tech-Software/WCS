# Modelling a plant

The plant is data: devices, arrows between them, and the links between devices and the cells of
your WMS. The engine builds a graph from it and computes routes on it at every hop. This page
explains what you must describe and which properties matter, using the
[sample plant](../../samples/plant/sample-plant.json) as the running example.

```text
IN01 --> CV01 --> CV02 --> SC01 <--> ST01 (rack A: cells 101-106, doors A0101 ... A0302)
                             |
                             v
                           CV03 --> OUT01
```

## Creating a plant

A new host starts with an empty plant. There are two ways to give it one, and both write the same
data to `wcs.db`:

- **Draw it in the [web editor](#the-web-editor)** at `/editor`: a device shape dropped on the
  drawing becomes a device, an arrow between two devices becomes a junction, and the editor's
  windows set the properties of each device and link the bays to the cells of your WMS.
- **Send it as one JSON document** with `PUT /api/v1/plant/graph`
  ([import and export](#import-and-export)), as
  [`load-sample-plant.ps1`](../../samples/plant/load-sample-plant.ps1) does with the sample plant.
  This is the way to generate a plant from your own data, or to copy one from a host to another
  (`GET /api/v1/plant/graph` on the first, `PUT` on the second).

The two mix: load a file, then refine it in the editor, or the other way round.

## Devices

A device (an *item*) is a node of the graph: something a load unit can be on. Its configuration is a
`PlantItemConfigDto` ([`PlantItemConfigDto.cs`](../../WCS/Wcs.Api.Contracts/src/Dtos/PlantItemConfigDto.cs),
60 properties, each with a description); in JSON the property names are camelCase.

| `deviceType` | Name | What it is |
|---|---|---|
| 0 | Automation | a conveyor, a lift, a shuttle, a stacker crane: anything that moves load units |
| 1 | Loading | an infeed bay, where load units enter |
| 2 | Buffer | an outfeed bay (picking station, shipping bay), where load units are delivered |
| 3 | Storage | a rack of WMS cells. It has no connection and is always Ready; it is served by a relocator |
| 4 | CircularDistributor | a circular distributor |
| 5 | GroupOfAutomationDevices | a group of devices that move several load units together |
| 6 | LoadingAndBuffer | a bay that is both infeed and outfeed |
| 99 | None | |

`GET /api/v1/plant/device-types` lists them with the shape the web editor draws.

The identity and the field address of a device:

| Property | Example (IN01) | Meaning |
|---|---|---|
| `itemId` | `5a000000-0000-4000-8000-000000000001` | a GUID you choose; stable: your WMS stores it next to its cells, commands use it |
| `name` | `IN01` | what operators read |
| `asi`, `subitem`, `partition` | `1001`, `0001`, `0001` | the field address: what the driver sends the machine, and how an event from the field finds the device. Keep the three strings together unique: nothing checks it, and an event finds the first device with that address |
| `connectionType` | `0` | 0 Test: driven by the loopback driver; 1 Connected: by the field driver |
| `deviceConnectionId` | `0` | with `connectionType` 1, the driver settings row (`/api/v1/plc/connections`) the device uses |
| `delayBlind` | `500` | with the loopback driver, how many milliseconds a movement takes |
| `timeOutPlcMoving`, `timeOutGetLoadUnitInfo` | `0` | handed to the driver with every command (`RunAsi.TimeOutPeriod`, milliseconds); what a driver does with them is up to it |
| `autoExecuteStep` | `true` | the device executes its next step by itself; with `false` it waits for command 2001 |

## Junctions

A junction is a directed arrow: a load unit on the source device can go to the destination device.

```json
{ "junctionId": "5a000000-0000-4000-9000-000000000003", "sourceItemId": "5a000000-0000-4000-8000-000000000003",
  "destinationItemId": "5a000000-0000-4000-8000-000000000004", "priority": 0 }
```

- One junction per ordered pair: `A -> B` and `B -> A` are two junctions (the sample has
  `SC01 -> ST01` to store and `ST01 -> SC01` to pick).
- The route search ([`RouteSearch.cs`](../../WCS/Wcs.Engine/src/Routing/RouteSearch.cs)) picks the
  route with the **fewest hops**; between equal routes, the one with fewer queued steps on its
  devices. `priority` only orders the outgoing junctions of a device, that is which of two equal
  routes is found first.
- A device's status decides whether a route may pass: Ready, Busy, Recovering and Locked devices are
  passable, Moving, Loading and Unloading ones depending on their queue; OffLine, Warning and Error
  ones are avoided (unless the search is told to consider them available).
- The route is recomputed at every hop, so a device that goes OffLine half-way is avoided by the
  next hop.
- `groupRoutePathId` gives a device a *level* (above 0): a route never comes back to a level it has
  left. Use it on multi-level plants, so that a route does not go down and up again.

## Storage, relocators and cells

A rack is a Storage device (type 3) with a junction to and from the device that serves it, its
**relocator** (a stacker crane, a shuttle), named in `relocateItemId`:

```json
{ "itemId": "5a000000-0000-4000-8000-000000000005", "name": "ST01", "deviceType": 3,
  "asi": "3001", "subitem": "0001", "partition": "0001", "connectionType": 0, "delayBlind": 0,
  "autoExecuteStep": false, "relocateItemId": "5a000000-0000-4000-8000-000000000004" }
```

The relocator itself:

```json
{ "itemId": "5a000000-0000-4000-8000-000000000004", "name": "SC01", "deviceType": 0,
  "asi": "2001", "subitem": "0001", "partition": "0001", "connectionType": 0, "delayBlind": 500,
  "autoExecuteStep": true, "recordStorageLength": 2, "storagePartition": "0000", "motionType": 1 }
```

Two properties are not optional on a relocator, and getting them wrong does not produce an error
message, only a load unit that stops:

| Property | Value | Why |
|---|---|---|
| `motionType` | **1 GetFrom** | the relocator executes the movement that brings a load unit onto itself (it picks it from the conveyor). With 0 MoveTo the conveyor before it would be the one to execute, and the engine does not put a load unit into a device linked to a storage that way: the load unit waits on the conveyor |
| `recordStorageLength` | **not 0 (None)** | how the storing depth is recorded: 1 FromPlc (the driver reports it in `FinalPosPartition`, read as a number times 10), 2 FixedLength (one depth per cell: the partition at offset `-1` of the cell's partition map), 3 ConvertDepthToPartition (the partition at the offset equal to the storing depth), 4 CalculatedStorageDepth. With None the engine finds no address to store to or pick from |

With FixedLength or ConvertDepthToPartition also set `storagePartition` (and
`storagePartitionReversed` for cells marked `inverted` by the WMS) to the partition name your cells
use: grouped movements send it to the machine.

### Door mappings: how the WCS knows a cell

The WCS does not hold the cells: your WMS does. What the WCS holds, for each cell door its storage
devices reach, is a **door mapping**: door code -> cell id -> device.

```json
{ "id": 0, "itemId": "5a000000-0000-4000-8000-000000000005", "doorCode": "A0101", "cellId": 101,
  "side": "A", "operationActive": 0, "storingLocked": false }
```

Your WMS holds the other half: for door `A0101`, the field address (`asiDoorA` `3001`,
`subitemDoorA` `0101`) and the partitions of the cell, answered by `GET cells/{id}/partitions`. When
the relocator stores into cell 101 the engine sends it to `3001` / `0101` / `0000`. The two halves
must agree: the [adapter guide](writing-a-wms-adapter.md#2-the-data-your-wms-must-hold-for-the-wcs)
says how to check.

`storingLocked` is set by the engine when it takes a door out of use (and by
`Wcs:Engine:LockCellOnStoringError`); `PUT /api/v1/plant/door-mappings` replaces all the door
mappings.

### Linked cells: bays

A bay is a device that stands for a WMS cell. Set `linkedCellId` to the cell:

```json
{ "itemId": "5a000000-0000-4000-8000-000000000001", "name": "IN01", "deviceType": 1,
  "asi": "1001", "subitem": "0001", "partition": "0001", "connectionType": 0, "delayBlind": 500,
  "autoExecuteStep": true, "lookWhereAllocateLoadUnit": 2, "linkedCellId": 800 }
```

When a load unit arrives on IN01 the WCS reports it allocated in cell 800 (`load-units/allocate`
without a door code), and when it needs a storage cell for it, it searches the warehouse of cell
800. The outfeed bay OUT01 is linked to cell 900: extraction orders name 900 as their
`pickingCellId`, and the WCS delivers to the device linked to it. The sample also gives each bay a
door mapping (`BAY-IN01`, `BAY-OUT01`) to its cell. In the web editor, the cells a device can be
linked to come from the WMS route `cells?areaType=` (type 2 for automation, loading and circular
distributor devices, 3 for buffers).

## What happens when a load unit arrives on a device

| Property | Values | Effect |
|---|---|---|
| `lookWhereAllocateLoadUnit` | 0 No; 1 CheckOnly; 2 BuildRouteToStore; 3 BuildRouteToStoreByIdLuType; 4 BuildRouteToDifferentStoreLocation; 5 BuildRouteToStoreForcing | whether the device asks the WMS for a storage cell (`POST reservations`) and builds the route to it. The sample's infeed uses 2: "build the route to the store if it has not already been calculated, otherwise reject" |
| `rejectItemWhenNoAllocation` | a device id | where the load unit goes when the WMS has no cell (`outcome` 6); without it, the load unit waits and the WCS asks again |
| `buildDestinationItemId`, `buildDestinationItemId2` | device ids | send the load unit to that device, if it has no destination yet |
| `getLoadUnitInfo` | bool | ask the field for the load unit's data (code, dimensions) when it arrives (`RequestLuDataFromAsi`); `triggerItemGetLoadUnitInfo` picks another device to ask, `recordLoadUnitInfo` registers the load unit from the answer, `rejectItemWhenInfoError` is where it goes when the answer is an error |
| `autoUnloadLoadUnit` | bool | the load unit is unloaded automatically on arrival, after `Wcs:Engine:AutoUnloadDelayMilliseconds` |
| `respectQueue` | bool | the device serves its queued steps in order |
| `rejectDestinationUserRequest` | a device id | where command 2008 sends the load units of this device (otherwise `Wcs:Engine:DefaultRejectItem`) |

Groups (`groupItemId`, `belongingGroupId`, `groupLevelId`, `orderNumberInGroup`, the `group...`
flags) and defragmentation (`enableDefragmentation`, `cellToDefragmentId`) are described property by
property in [`PlantItemConfigDto.cs`](../../WCS/Wcs.Api.Contracts/src/Dtos/PlantItemConfigDto.cs);
the sample plant uses neither.

## The web editor

The host serves a plant editor at `/editor` (draw.io inside a Blazor page). It asks for the API key
first ([quick start](../quick-start.md#6-open-the-web-editor)), then shows the plant: devices with
their live status and load units, junctions, and windows for the device list, the properties of the
selected device, missions, commands, linked cells and the driver settings. The **Plant file** window
exports the plant ("Export plant") and imports one ("Import plant", "Choose file": `.json` plant
files, and `.edd`/`.xml` drawings of the old editor), with a comparison to confirm before it
replaces anything.

The editor writes through the same API as everyone else, with the edit lease (it takes it while it
writes) and the same engine-state rules: most changes need the engine in Stop or Pause.

## Import and export

The whole plant is one JSON document, a `PlantGraphDto`
([`PlantGraphDto.cs`](../../WCS/Wcs.Api.Contracts/src/Dtos/PlantGraphDto.cs)):

```json
{
  "items": [ { "itemId": "...", "name": "IN01", "deviceType": 1, "...": "..." } ],
  "geometries": [ { "itemId": "...", "x": 40, "y": 200, "width": 80, "height": 60, "rotation": 0 } ],
  "junctions": [ { "junctionId": "...", "sourceItemId": "...", "destinationItemId": "...", "priority": 0 } ],
  "doorMappings": [ { "id": 0, "itemId": "...", "doorCode": "A0101", "cellId": 101, "side": "A", "operationActive": 0, "storingLocked": false } ],
  "decoration": null,
  "plcConnections": null
}
```

- `GET /api/v1/plant/graph` exports it (every property of every device, with defaults filled in);
  keep it as a backup.
- `PUT /api/v1/plant/graph` replaces the whole plant: devices, positions, junctions, door mappings,
  the decoration and, when `plcConnections` is present, the driver settings (absent: kept). It needs
  the edit lease, the engine in Stop, no active mission and an empty extraction queue.
  [`load-sample-plant.ps1`](../../samples/plant/load-sample-plant.ps1) does exactly this.
- Properties you omit take their defaults (`false`, `0`, `null`): the sample file lists only what
  differs.
- The host validates the file and answers `400` with the reason: empty or repeated device ids,
  repeated junction ids, junctions with an empty, equal or unknown end, two junctions in the same
  direction between the same devices, door mappings or positions of unknown devices, a device with
  two positions, driver settings ids not above zero or repeated, a device pointing to a driver
  settings row the file does not have.
- **Driver connections are built when the plant is loaded**, not when it is imported: after importing
  a plant whose devices use the field driver, reload it (command 1003 in Stop) or restart the host
  ([connecting machines](connecting-machines.md#when-connections-are-built)).

To build a plant by hand, start from `sample-plant.json`: choose new GUIDs, give every device a unique
field address, draw the junctions both ways where a device both stores and picks, link the bays to
their cells, map the storage doors, and set the relocators as above.
