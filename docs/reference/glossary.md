# Glossary

The words of the WCS, and the names they have in the code and on the wire. Many identifiers come
from the Italian original of the product: they are listed here too.

| Term | Meaning | In the code / on the wire |
|---|---|---|
| **WCS** | Warehouse Control System: knows the plant and moves load units through it. | this repository |
| **WMS** | Warehouse Management System: knows goods, cells, orders and stock. Your system. | the other side of `IWarehouse`; "gestionale" in Italian comments |
| **Engine** | The part of the WCS that holds the plant, the missions and the device states and computes routes. Also "navigator". | `Wcs.Engine`, the static class `Navigator` |
| **Host** | The executable that runs the engine, serves the HTTP API, the SignalR hub and the web editor. | `Wcs.Host` |
| **Engine state** | Stop, Run, Recover (loading), Dispose (shutting down), Pause. Only Run moves load units and pulls work. | `navStatus`, `NavigatorStatusTypes` |
| **Master loop** | The engine's periodic turn (1 s by default): commands, durable queues, and in Run the pull of pending work. | `Navigator.MasterProcess`, `Wcs:Engine:MasterProcessOnEvery` |
| **Plant** | The devices and the arrows between them. Data, not code. | `PlantGraphDto`; "impianto" |
| **Device**, **item** | A node of the plant: a conveyor, a crane, a bay, a rack. | `PlantItemConfigDto`, `AutomationDevice`, `itemId` |
| **Device type** | Automation 0, Loading 1, Buffer 2, Storage 3, CircularDistributor 4, GroupOfAutomationDevices 5, LoadingAndBuffer 6. | `deviceType`, `DeviceTypes` |
| **Junction** | A directed arrow from one device to another: a load unit can go that way. | `JunctionDto`, `sourceItemId` -> `destinationItemId` |
| **Field address**, **ASI / SUBITEM / PARTITION** | The three strings that name a position for the machines: the device, a sub-position, a partition. The driver receives them in every command; events from the field carry them. | `DataAsi`, `asi`, `subitem`, `partition` |
| **Load unit (LU)** | A pallet, a box, a tray: what moves. Identified by the WMS's id and by a code (label). | `loadUnitId`, `LuId`, `code`; "UDC" in Italian identifiers (`udc` in some query strings) |
| **Mission** | The engine's record of one load unit going somewhere. Recomputed at every hop. | `PathMission`, `MissionDto` |
| **Step** | One hop of a mission: one movement from a device to the next, run by one device. | `MissionStep`, `stepGuid` |
| **Relocator** | The device that stores into and picks from a storage device's cells: a stacker crane, a shuttle. | `relocateItemId` on the storage device |
| **Cell** | A storage location in the WMS. | `cellId` |
| **Door**, **door code** | The side of a cell a relocator reaches it from (A or B), and the WMS's code for it. The WCS knows cells by their door codes. | `doorCodeA`, `doorCodeB`, `storageDoorCode`, `pickUpDoorCode` |
| **Door mapping** | The WCS's record that a door code belongs to a cell and is served by a device. | `DoorMappingDto`, `PUT /api/v1/plant/door-mappings` |
| **Partition (of a cell)** | A depth position inside a cell, with its offset. | `CellPartitionMap.partitions` |
| **Linked cell** | The WMS cell a device stands for (typically a bay): when a load unit arrives on the device, the WCS reports it in that cell. | `linkedCellId` |
| **Bay**, **buffer** | Where load units enter (infeed, Loading) or are delivered (outfeed, Buffer). "Buffer cell" is the WMS cell of a bay. | `bufferCellId`, `pickingCellId` |
| **Reservation** | A storage cell the WMS holds for a mission; renewed every time the WCS asks for it. | `reservations`, `StorageReservation` |
| **Extraction order** | WMS work: bring these load units to this bay. Pulled by the WCS. | `extractions/pending-work`, `PendingExtraction` |
| **Shipping note assignment** | WMS work tied to a shipping note: a load unit assigned to a buffer. Pulled by the WCS. | `notes/pending-work`, `PendingNote`; "nota", "assegnazione" |
| **Extraction queue** | The WCS's own queue of load units to extract, built from the pulled work. | `/api/v1/extraction-queue` |
| **Fact** | Something the WCS reports after it happened (stored, left, picked); the WMS must accept it. | `load-units/allocate`, `load-units/release`, ... |
| **Durable queue (outbox)** | A table in `wcs.db` where the WCS writes a fact before sending it, and from which it retries until the WMS accepts it. | `PendingWarehouseTransitions` and three more; dispatchers `Pending*Dispatcher` |
| **OperationId** | The id of a fact sent from a durable queue; the WMS keeps a receipt for it. | `operationId` |
| **Parked** | A queued fact the WMS refused for good (a 4xx other than 408/429): kept with its reason, not retried. | `PendingWarehouseTransitionStore.ParkedUntil` |
| **Command** | A request to the engine to do something (start, load a load unit, unload, ...). | `POST /api/v1/commands`, [codes](commands.md) |
| **Edit lease** | The 60-second permission to change the plant; one holder at a time. | `POST /api/v1/plant/edit-lock`, `X-Edit-Holder` |
| **Field driver** | The code that talks to the PLCs of a plant: implements `IConnection` and `IConnectionFactory`. | `Wcs:ConnectionFactory` |
| **Loopback driver** | The driver that simulates the PLCs in the process: every move succeeds after `DelayBlind` ms. | `Wcs.Connection.Test`, connection type Test (0) |
| **Connection type** | Test (0): the device is driven by the loopback driver. Connected (1): by the field driver, through the connection `DeviceConnectionId`. | `connectionType`, `deviceConnectionId` |
| **Driver settings** | The rows that describe the field driver's connections (address, port, ...). The API names them after the PLC. | `/api/v1/plc/connections`, `ConnectionSettings` |
| **Navigator log** | The operators' log of what the engine did, kept in the history database. | `GET /api/v1/log` |
| **Workstation** | Who asked: an operator station. The WCS's own requests to the WMS carry `KazamControl`, and the user `KazamControl.Process`. | `workstationId`, `requestedByWorkstationId` |
| **Kazam** | Sun Tech's WMS, from which the WCS was extracted. Some fixed values on the wire still carry its name. | |
