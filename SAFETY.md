# Safety

**Read this before connecting the WCS to any machine.**

The Sun Tech WCS decides *where* load units go and asks a field driver to move them. It is plant
control software, not safety software. Anyone who installs it on a plant with real machines is
responsible for the safety of that plant, as with any other control software.

## The WCS is not a functional-safety system

- The WCS is not a safety-related control system in the sense of the functional-safety standards
  (for example IEC 61508, IEC 62061 or ISO 13849) and must not be used as one. It provides **no
  safety function**.
- It does **not replace** safety PLCs, emergency stops, guards, light curtains, safety interlocks,
  safe speed or safe torque functions, or any other protective measure.
- **Machine safety must be independent of the WCS.** People must be protected with the WCS host
  stopped, crashed, disconnected, misconfigured or sending wrong commands; with the network down; with
  the field driver faulty; and with the WMS sending wrong data. Nothing the WCS does, or fails to do,
  may be able to put a person at risk.
- Stopping the engine (command 1002) stops the WCS from sending new commands and closes the driver
  connections; it is **not an emergency stop**. The driver contract has no method to halt a movement
  already sent to a machine: what a machine does when the connection closes, or when the WCS stops
  talking to it, is decided by the driver and the machine's own control, and must be safe there.

## Every plant needs its own risk assessment and commissioning

- Each installation needs a **risk assessment** of its machines, carried out by whoever is responsible
  for them under the rules that apply where the plant is (in the European Union, for example, the
  Machinery Directive or Regulation), covering the machines as they are driven through the WCS.
- Each installation needs **commissioning**: the driver tested on the actual machines, every device of
  the plant model checked against the machine it stands for, every route tried, with people at a safe
  distance and the safety functions verified first.
- **Simulation does not replace FAT and SAT.** The factory acceptance test and the site acceptance
  test are done with the real machines, the real driver and the real WMS adapter. Runs on the loopback
  driver, or with simulated devices, count as neither.

## The loopback driver proves nothing about safety or real machines

This repository ships only the **loopback driver** (connection type Test), which simulates the machines
inside the host process: a movement "completes" after a delay, and nothing moves. A plant that works
on the loopback driver shows that the plant model, the routes and the WMS integration are consistent.
It says nothing about the behaviour of real machines, their timing, their failures or their safety.

Sun Tech's driver for its PLCs is proprietary and not included. A driver written by anyone else is
part of the plant's control system and must be developed, tested and assessed as such
([connecting machines](docs/integration/connecting-machines.md)).

## Connected devices without a connection fail closed

A device that stands for a real machine has connection type **Connected**. When the engine is started
from Stop it checks every Connected device. If a device has no connection (no field driver installed,
no connection id, or no connection built for its id) the engine **refuses Run**, stays in Stop, sets
those devices OffLine and says which and why. A Connected device is never silently served by the
simulator.

The setting `Wcs:AllowSimulatorForMissingConnections` makes an exception for test installations: a
Connected device whose connection was not built is then served by the simulator, with a warning, and
Run goes ahead. With it, the WCS believes it drives a machine while nothing is commanded to move.
**The host refuses to start with this setting on in the Production environment, or in an environment with no name.** Use it only on a
test bench, while a driver is missing, with nobody relying on those devices; turn it off as soon as the
driver is there.

After importing a plant (`PUT /api/v1/plant/graph`) the driver connections are not rebuilt until the
plant is reloaded (command 1003) or the host restarted; until then Run is refused for the Connected
devices without a connection.

## Manual commands need operating procedures

Several commands change what the WCS believes about the physical plant, or make machines move, on a
person's request: for example 1004 (remove every load unit and mission), 1005 (put every device in
Ready), 2001 (execute the next step: the way to retry after a failure), 2003 (put a device back
online), 2004 (unload a load unit), 2007 (mark a running step done by hand), 3003 to 3006 (manual
moves, logical and physical emptying of cells, change of cell)
([commands](docs/reference/commands.md)).

Used without looking at the plant, they can make the WCS's picture differ from reality, and then the
next movement is planned on a wrong picture. Each plant needs written **operating procedures** that
say who may send which command, in which conditions, after which checks on the field, and how the
result is verified. The host does not identify the person who sends a command
([known limitations](docs/known-limitations.md#authentication-and-identity)): the procedures, and who
holds the full key, are what control it.

## Stop and recovery procedures must be defined

Each plant needs defined and trained procedures for at least:

- stopping the plant normally, and what the WCS does and does not do at a stop;
- an emergency stop, which belongs to the machines' safety system and not to the WCS, and how the WCS
  is brought back in line afterwards (load units moved by hand, devices put back online, steps retried);
- a device that goes to Warning or OffLine, and a movement that ends with an error code or a timeout;
- loss of the network, of the WMS, of the field driver or of the host server, and the restart;
- facts refused for good by the WMS ([parked facts](docs/known-limitations.md#parked-facts-towards-the-wms));
- restoring the host from a backup
  ([backup, restore and disaster recovery](docs/integration/backup-restore-and-disaster-recovery.md)).

## Demo, test bench and production

| | Demo | Test bench (commissioning) | Production |
|---|---|---|---|
| Machines | none: loopback driver | real machines, brought up step by step | real machines |
| Safety | not applicable | safety functions verified **before** the first movement under WCS control | risk assessment done, FAT and SAT passed, procedures in place |
| `Wcs:AllowSimulatorForMissingConnections` | as needed | only while a driver is missing | refused by the host |

The [production readiness checklist](docs/integration/production-readiness-checklist.md) lists what
must be true before a plant depends on the WCS.

## No warranty

The WCS is distributed under the [Apache License 2.0](LICENSE), "AS IS", without warranties or
conditions of any kind (sections 7 and 8 of the licence). This file describes how the software
behaves and what an installation needs; it does not change those terms.
