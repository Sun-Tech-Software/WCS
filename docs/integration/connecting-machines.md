# Connecting machines

The engine never talks to a PLC itself. It sends commands to a **field driver** through the
`IConnection` interface and reacts to the events the driver raises. This page describes that
contract at the level of the interface, the loopback driver that ships with the WCS, and how to
write and plug in a driver of your own.

This repository contains:

- [`Wcs.Connection.Interface`](../../WCS/Wcs.Connection.Interface/src): the contract
  (`IConnection`, `IConnectionFactory`, `IConnectionServices` and their data types);
- [`Wcs.Connection.Test`](../../WCS/Wcs.Connection.Test/src/Connection.cs): the **loopback driver**,
  which simulates the PLCs in the process. Every device with connection type Test (0) uses it, and
  the whole WCS runs with it and no machine at all.

Sun Tech's own field driver, and the protocol it speaks with Sun Tech's PLCs, are proprietary and
not in this repository; they are available from Sun Tech (<https://suntechonline.it>). Any other
driver plugs in the same way as described below.

- [Choosing the driver](#choosing-the-driver)
- [Driver settings and devices](#driver-settings-and-devices)
- [When connections are built](#when-connections-are-built)
- [What the engine sends](#what-the-engine-sends)
- [What the engine expects back](#what-the-engine-expects-back)
- [Completion codes the engine reacts to](#completion-codes-the-engine-reacts-to)
- [Events from the field](#events-from-the-field)
- [What the engine offers the driver](#what-the-engine-offers-the-driver)
- [The loopback driver](#the-loopback-driver)
- [A driver of your own, step by step](#a-driver-of-your-own-step-by-step)

## Choosing the driver

The host loads one field driver, named by the setting `Wcs:ConnectionFactory`: the
assembly-qualified name of a class that implements `IConnectionFactory` and has a public
parameterless constructor ([`ConnectionFactoryLoader.cs`](../../WCS/Wcs.Host/src/Engine/ConnectionFactoryLoader.cs)).

| `Wcs:ConnectionFactory` | Result | Host log at start |
|---|---|---|
| absent (the default) | the default of the build: **none** in this repository | `warn`: `No field driver (Wcs:ConnectionFactory is not set and this build has no default): only the loopback driver is available, and devices with connection type Connected stay without connection (OffLine), and Run is refused while the plant has any` |
| `none` (any case) or empty | no field driver | `warn`: `No field driver (Wcs:ConnectionFactory turns it off): only the loopback driver is available, and devices with connection type Connected stay without connection (OffLine), and Run is refused while the plant has any` |
| `Acme.Wcs.Driver.AcmeConnectionFactory, Acme.Wcs.Driver` | that class, from `Acme.Wcs.Driver.dll` in the host folder | `info`: `Field driver: Acme.Wcs.Driver.AcmeConnectionFactory, Acme.Wcs.Driver` |

```powershell
$env:Wcs__ConnectionFactory = 'Acme.Wcs.Driver.AcmeConnectionFactory, Acme.Wcs.Driver'
```

or in `appsettings.json`:

```json
{ "Wcs": { "ConnectionFactory": "Acme.Wcs.Driver.AcmeConnectionFactory, Acme.Wcs.Driver" } }
```

Copy the driver's DLL (and its own dependencies, or its `.deps.json` with them) next to
`Wcs.Host.exe`. Dependencies are resolved from the driver's folder, but an assembly the host already
has is always the host's: the driver must not ship its own `Wcs.Connection.Interface.dll` or
`Wcs.Common.dll`, which would overwrite the host's copies when the driver's folder is copied next to
`Wcs.Host.exe`. Reference **both** with `Private=false`, as in the project below: `Wcs.Common` comes
in through `Wcs.Connection.Interface`, and without its own `Private=false` reference the build still
copies `Wcs.Common.dll` into the driver's `bin` folder. After `dotnet build`, the driver's output
folder holds only `Acme.Wcs.Driver.dll`, `.pdb` and `.deps.json`.

A name that does not load **stops the host**, even with `Wcs:StartEngine` false. The message names
the key, the value and what to do, for example:

```text
Wcs:ConnectionFactory = "Acme.Plc.Factory, Acme.Plc": the type was not found. Write it as "Namespace.TypeName, AssemblyName" and put AssemblyName.dll in the host folder (...). Set it in appsettings.json (Wcs:ConnectionFactory) or in the environment variable Wcs__ConnectionFactory; set it to "none" to run with the loopback driver only (connection type Test).
```

The other reasons are: the type does not implement `IConnectionFactory`, it has no public
parameterless constructor, its constructor threw, or the file is not a .NET assembly.

## Driver settings and devices

A driver has *connections*: one per PLC (or gateway) it talks to. They are described by the
**driver settings** rows, which the API names after the PLC (`/api/v1/plc/connections`,
`PlcConnectionDto`), and reach the driver as
[`ConnectionSettings`](../../WCS/Wcs.Connection.Interface/src/IConnectionServices.cs):

| `PlcConnectionDto` (API) | `ConnectionSettings` (driver) | |
|---|---|---|
| `id` | `Id` | devices point to it with `deviceConnectionId` |
| `description` | - | for people |
| `plcAddress` | `RemoteAddress` | the address of the field device |
| `pcmPort` | `RemotePort` | its port (0 when not set) |
| `localAddress` | `LocalAddress` | a local address to bind |
| `localPort` | `LocalPort` | a local port (0 when not set) |
| - | `OpProgress` | the message counter the driver last persisted (`WriteStatusAsync`) |

The meaning of every value belongs to the driver. A device uses the driver when its
`connectionType` is 1 (Connected) and its `deviceConnectionId` is a row id. In a plant file:

```json
{
  "items": [
    { "itemId": "5a000000-0000-4000-8000-000000000001", "name": "IN01", "deviceType": 1,
      "asi": "1001", "subitem": "0001", "partition": "0001",
      "connectionType": 1, "deviceConnectionId": 1, "autoExecuteStep": true,
      "lookWhereAllocateLoadUnit": 2, "linkedCellId": 800 }
  ],
  "plcConnections": [
    { "id": 1, "description": "Acme gateway", "plcAddress": "127.0.0.1", "pcmPort": 7190,
      "localAddress": null, "localPort": null }
  ]
}
```

`GET /api/v1/plc/connections` then answers:

```json
[{"id":1,"description":"Acme gateway","plcAddress":"127.0.0.1","pcmPort":7190,"localAddress":null,"localPort":null}]
```

`PUT /api/v1/plc/connections` replaces the rows (edit lease, engine in Stop; a row still used by
devices cannot be removed: `409` with `plcConnectionsInUse`).

## When connections are built

The engine drives a connection through this life cycle (documented on
[`IConnection`](../../WCS/Wcs.Connection.Interface/src/IConnection.cs)):

1. **Plant load** (host start, command 1003 in Stop or Pause): the previous connections are closed
   and disposed; the factory receives the engine services (`Attach`) and is asked for one connection
   per driver settings row that some device uses (`Create`). `Create` returning `null` means "these
   settings are not enough for me".
2. **Engine start** (command 1001 from Stop, not from Pause): for each Connected device the engine
   finds its connection by `Id`, sets `Managed` to true, subscribes to its six events and calls
   `OpenConnection`.
3. **Engine stop** (command 1002, also at host shutdown): unsubscribe, `CloseConnection`, `Managed`
   false. The same object is opened again at the next start. `Dispose` is called only at the next
   plant load.

**A Connected device never falls back to the simulator** unless you ask for it. When the engine is
started from Stop (command 1001, `Wcs:AutoRun`, or the restart after a command 1003 in Run), it first
checks every Connected device. A device lacks its connection when:

- no field driver is installed (`Wcs:ConnectionFactory` absent in this repository, or `none`);
- it has no connection id (`deviceConnectionId` 0);
- no connection was built for its id: there is no driver settings row with that id, `Create`
  returned `null` for the row, or the plant was imported after the last plant load (below).

If any device lacks its connection, **Run is refused** and the engine stays in Stop, with nothing
opened. Every such device goes **OffLine** (status 6) and the navigator log has an error line for it
that names the device and the reason, for example:

```text
OpenConnection: Shuttle 3 [1003.0001.0001, itemId 5a000000-0000-4000-8000-000000000003]: connection type Connected, but the connection with id 1 was not built (no driver settings row with that id, the driver refused the row, or the plant was imported without reloading it: command 1003 in Stop); the device stays WITHOUT CONNECTION and goes OffLine
```

Without a field driver the reason reads `connection type Connected, but no field driver is
configured (setting Wcs:ConnectionFactory); only the loopback driver (connection type Test) is
available`; without a connection id, `connection type Connected, but no connection id is set
(DeviceConnectionId)`.

Command 1001 ends with status 99 (Error), and its `errorDescription` is the refusal, listing every
device with its reason; the web editor's Run switch shows it after "The engine refused the command:":

```text
Run refused: 1 device(s) of connection type Connected have no connection, and the engine does not drive them with the simulator (setting Wcs:AllowSimulatorForMissingConnections is off). They were set OffLine. Shuttle 3 [1003.0001.0001, itemId 5a000000-0000-4000-8000-000000000003]: connection type Connected, but the connection with id 1 was not built (...). Fix the connections, reload the plant (command 1003), put the devices back online (command 2003) and start again.
```

With `Wcs:AutoRun` the same refusal is in the host log (`Motore caricato ma non avviato
(Wcs:AutoRun)`). OffLine is stored in `wcs.db`: once the connections are fixed and the plant reloaded,
put the devices back online with command 2003 (`ItemSetOnLine`).

**Importing a plant (`PUT /api/v1/plant/graph`) does not build connections.** After an import that
adds Connected devices or driver settings, reload the plant with command 1003 (in Stop) or restart
the host, then start the engine; otherwise Run is refused as above.

**The explicit fallback, for test installations only.** With `Wcs:AllowSimulatorForMissingConnections`
set to `true`, a device whose connection was **not built** (the third case above) is served by the
loopback driver instead, with a warning on the device in the navigator log
(`...; the device is served by the SIMULATOR (setting Wcs:AllowSimulatorForMissingConnections)`),
and Run goes ahead with a warning that lists the devices. Devices without a driver or without a
connection id still go OffLine. The host **refuses to start** with the setting on in the Production
environment ([configuration](../reference/configuration.md)). The connection type Test is not
affected by any of this: it is the loopback driver by design.

## What the engine sends

The command methods of `IConnection`. They must return at once; the outcome comes back later through
the events. The engine catches an exception thrown by a command method, logs it on the device and,
for a movement, treats the step as failed.

| Method | When | Parameters that matter |
|---|---|---|
| `MoveLuFromAsiToAsi(RunAsi sourceItem, DataAsi actionItemPerformer, DataAsi fromAsi, DataAsi toAsi, Guid stepGuid, decimal luId, int luHandlingMode)` | a device executes one step of a mission: one load unit from one position to another (a store into a cell and a pick from it are movements too: the cell's position comes from the WMS's partition map) | `sourceItem.ItemGuid` and `stepGuid` must come back in the completion; `sourceItem.TimeOutPeriod` is the device's `timeOutPlcMoving` (ms); `fromAsi`/`toAsi` are field addresses |
| `MoveLuFromAsiToAsi(RunAsi sourceItem, DataAsi actionItemPerformer, Guid groupedMovementId, List<MovementLuDataCommand> loadUnitsToMove, int luHandlingMode)` | a group of devices moves several load units in one command | `groupedMovementId` comes back as `StepGuid` |
| `RequestLuDataFromAsi(RunAsi item)` | a device with `getLoadUnitInfo` wants the code and dimensions of the load unit it holds | answer with `AsiSendLuData`; `item.TimeOutPeriod` is `timeOutGetLoadUnitInfo` |
| `SendLuDataToAsi(RunAsi item, LuInfoDataCommand value)` | the answer to an `AsiRequestsLuInfo` event, with the WMS's data of the load unit | `value.RequestId` is the request id of the event |
| `ShowMessageToAsi(RunAsi destinationItem, string message)` | a message for a device's panel | the engine currently always passes an empty string |
| `SendCommandToAsi(...)` | never called by the engine | may do nothing |
| `OpenConnection`, `CloseConnection`, `Dispose` | life cycle, above | `CloseConnection` must be harmless on a connection never opened; `OpenConnection` must work again after `CloseConnection` |

`DataAsi` is the three strings of a field address (`Asi`, `Subitem`, `Partition`). `RunAsi` adds the
device's `ItemGuid`, `TimeOutPeriod`, and three values only the loopback driver uses
(`DelayToStepDone`, `ResultType`, `ErrorResultCode`).

## What the engine expects back

1. **Exactly one `AsiMovementIsCompleted` per movement**, with `ItemGuid` = `sourceItem.ItemGuid` (the
   engine finds the device by it) and `StepGuid` = the `stepGuid` (or the `groupedMovementId`). A
   completion for a step the engine no longer has is dropped with a warning; a second completion for
   the same step is not expected.
2. **Timeouts are the driver's job.** The engine has no step timer: without an answer the device stays
   busy. Use `RunAsi.TimeOutPeriod` and answer `ResultCodeTypes.TimeOut` when it expires.
3. **A command that cannot be sent is answered**, with `ResultCodeTypes.ConnectionError`, not
   dropped.
4. **`MoveLuResult`**: either `null`, or with at least one entry in `Lu`. For a single device the engine
   reads `Lu[0].FinalPosPartition` as the stored depth (times 10) when the device has
   `recordStorageLength` 1 (FromPlc).
5. **Events may be raised from any thread**, but not from inside the command method that caused them:
   the loopback driver raises them from `Task.Run`.

`ResultCodeTypes` ([`Enums.cs`](../../WCS/Wcs.Common/src/Enums.cs)): 0 None (ignored), 1 Success,
2 TimeOut, 3 ConnectionError, 99 Error (with `ErrorCode`).

## Completion codes the engine reacts to

What the engine does with a completed movement (in
[`AutomationDeviceConnectionManager.cs`](../../WCS/Wcs.Engine/src/Items/Managers/AutomationDeviceConnectionManager.cs)).
For an `Error`, the engine writes `Code {ErrorCode} - {ErrorDescription}` on the mission and in the
navigator log, so put a readable text in `ErrorDescription`.

| Result | `ErrorCode` | What the engine does |
|---|---|---|
| `Success` | - | the step is done; the load unit is on the next device; the engine executes the next hop |
| `TimeOut`, `ConnectionError` | - | the step fails: the mission goes to Error with "Time Out" or "Connection" in its description, the device goes to **Warning**, the destination is freed. A person retries with command 2001 (`ItemExecuteStep`) |
| `Error` | **67** | a store into a cell was refused because the cell is occupied: the engine marks the cell full in the WMS (`POST cells/lock`, state 3, door A) and asks the WMS for another cell, excluding the refused one |
| `Error` | **85**, **133** | a store was refused (channel full, aisle full): as 67, without the lock |
| `Error` | 67, 85, 133, the same code a fourth time in a row on the same device | no new search: the device goes to Warning and waits for an operator |
| `Error` | 67, 85, 133 with no other cell | the load unit goes to the device's rejection point (`rejectItemWhenNoAllocation`), or the device waits in Warning |
| `Error` | **70**, **73** | during a physical cell emptying (command 3005): the emptying ends; otherwise as any other code |
| `Error` | **35** and any other code | the step fails: mission in Error, device in Warning, destination freed |

On a device that belongs to a group the same codes apply to the steps of the grouped movement.
A successful movement resets the count of refused stores; a timeout does not. Command 2001 also
resets it.

## Events from the field

| Event | How the engine finds the device | What it does |
|---|---|---|
| `NewLuOnAsi(NewLuOnAsiData)` | `Asi`, `Subitem`, `Partition` | a load unit appeared. With `LuCode`, the engine resolves it (`GET load-units/by-code/{code}`), then asks the description; with `LuId` it asks the description directly. It creates an inbound mission on the device, as command 3002 does. Ignored if the device already holds a load unit |
| `LuUnloadedFromAsi(LuUnloadedFromAsiData)` | address | the load unit was taken off the device, as with command 2004 |
| `AsiRequestsLuInfo(AsiRequestsLuInfoData)` | address | the device asks the data of its load unit: the engine asks the WMS and answers with `SendLuDataToAsi`, carrying back `RequestId` |
| `AsiSendLuData(ItemGuid, Result, AsiSendLuData)` | `ItemGuid` | the answer to `RequestLuDataFromAsi`. `DataConfirm` 0 rejects the load unit; `TimeOut`/`ConnectionError` puts its mission in Error |
| `AsiEvent(AsiEventData)` | address | the engine does nothing with it today |

## What the engine offers the driver

`IConnectionFactory.Attach` hands the driver an
[`IConnectionServices`](../../WCS/Wcs.Connection.Interface/src/IConnectionServices.cs):

| Member | What for |
|---|---|
| `int ReserveCommandId(decimal connectionId)` | the next command id of a connection; ids survive restarts (reserved in blocks in `wcs.db`) |
| `Task WriteStatusAsync(ConnectionStatusSnapshot)` | persist the connection status; read back by `GET /api/v1/plc/connections/{id}/status`; its `OpProgress` comes back in `ConnectionSettings` |
| `Task WriteLogAsync(DriverLogRecord)` | a line in the operators' log (`GET /api/v1/log`); `Status` is 1 error, 2 warning, 4 information |
| `Task WriteMessageAsync(MessageLogRecord)` | a message exchanged with the field, in the monthly driver message log (`GET /api/v1/history/plc-messages`) |
| `Task ClearMessagesOlderThanAsync(int days)` | prune that log |

## The loopback driver

[`Wcs.Connection.Test/src/Connection.cs`](../../WCS/Wcs.Connection.Test/src/Connection.cs) is the
smallest complete driver: no I/O, one instance (id 99) shared by every Test device. A movement
completes after the device's `delayBlind` milliseconds, with the device's simulated result, and the
destination as final position:

```csharp
public void MoveLuFromAsiToAsi(RunAsi sourceItem, DataAsi actionItemPerformer, DataAsi fromAsi, DataAsi toAsi,
    Guid stepGuid, decimal luId, int luHandlingMode = 0)
{
    var luData = new MoveLuDataResult
    {
        FinalPosAsi = toAsi.Asi,
        FinalPosSubitem = toAsi.Subitem,
        FinalPosPartition = toAsi.Partition
    };

    var resultData = new AsiMovementIsCompletedData { Lu = [luData] };

    Task.Run(async () =>
    {
        try
        {
            await Task.Delay(sourceItem.DelayToStepDone);
            AsiMovementIsCompleted?.Invoke(this,
                new AsiMovementIsCompletedEventArgs(sourceItem.ItemGuid, stepGuid, sourceItem.ResultType,
                    sourceItem.ResultType == ResultCodeTypes.Error ? sourceItem.ErrorResultCode : 0,
                    GetErrorDescription(sourceItem.ResultType == ResultCodeTypes.Error
                        ? sourceItem.ErrorResultCode
                        : 0), resultData));
        }
        catch (Exception ex)
        {
            ReportFault(nameof(MoveLuFromAsiToAsi), ex);
        }
    });
}
```

`RequestLuDataFromAsi` answers `DataConfirm` 1 and the code `0001` after 200 ms. The loopback driver
never raises `NewLuOnAsi`, `LuUnloadedFromAsi`, `AsiRequestsLuInfo` or `AsiEvent`: with it, load
units enter with command 3002 and leave with command 2004. The simulated result is `Success` unless
changed in the engine (the device's `MoveToResult` and `ErrorCodeResult`, which the host API does not
expose); the engine tests use it to exercise the error codes.

## A driver of your own, step by step

The following driver is complete and was run against the WCS: a host with
`Wcs:ConnectionFactory` set to it, the sample plant with every device Connected to one gateway, and a
fake gateway that answers every movement after 300 ms. The gateway's protocol is **invented for this
example** (lines of text over TCP); replace the transport with your PLC's and keep the rules.

**The project**, next to your clone of this repository:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <!-- The contract only. Private=false: the host already has Wcs.Connection.Interface.dll and
         Wcs.Common.dll, and the driver must use the host's copies, not ship its own.
         Wcs.Common is named here too: it comes in through Wcs.Connection.Interface, and a
         transitive reference is copied to the output even when the direct one is not. -->
    <ProjectReference Include="..\WCS\WCS\Wcs.Connection.Interface\src\Wcs.Connection.Interface.csproj">
      <Private>false</Private>
    </ProjectReference>
    <ProjectReference Include="..\WCS\WCS\Wcs.Common\src\Wcs.Common.csproj">
      <Private>false</Private>
    </ProjectReference>
  </ItemGroup>

</Project>
```

**The factory** (`AcmeConnectionFactory.cs`):

```csharp
using Wcs.Connection.Interface;

namespace Acme.Wcs.Driver;

/// <summary>
/// The plug point: the host builds this type by name (Wcs:ConnectionFactory) with its
/// parameterless constructor, calls Attach, then Create once per row of the driver settings.
/// </summary>
public sealed class AcmeConnectionFactory : IConnectionFactory
{
    private IConnectionServices? _services;

    public void Attach(IConnectionServices services) => _services = services;

    public IConnection? Create(ConnectionSettings settings)
    {
        // Not enough to reach the PLC: return null. The devices that point to this row get no
        // connection: they go OffLine, and Run is refused while they lack it.
        if (string.IsNullOrWhiteSpace(settings.RemoteAddress) || settings.RemotePort == 0)
            return null;

        return new AcmeConnection(settings, _services
            ?? throw new InvalidOperationException("Create called before Attach"));
    }
}
```

**The connection** (`AcmeConnection.cs`):

```csharp
using System.Collections.Concurrent;
using System.Net.Sockets;
using Wcs.Connection.Interface;
using Wcs.Connection.Interface.CommandData;
using Wcs.Connection.Interface.ConnectionData;
using Wcs.Connection.Interface.Events;
using static Wcs.Common.Enums;

namespace Acme.Wcs.Driver;

// One connection to an (invented) Acme PLC gateway that speaks lines of text over TCP:
//   driver -> PLC   MOVE <id> <fromAsi> <fromSubitem> <fromPartition> <toAsi> <toSubitem> <toPartition> <luId>
//   PLC -> driver   DONE <id> <code>          (0 = done, anything else = the error code)
//   PLC -> driver   NEWLU <asi> <subitem> <partition> <luCode>
//   PLC -> driver   GONE <asi> <subitem> <partition>
// Replace the transport with your PLC's; keep the rules on events, timeouts and identifiers.
public sealed class AcmeConnection(ConnectionSettings settings, IConnectionServices services) : IConnection
{
    // Rule: exactly one AsiMovementIsCompleted per move. Whoever removes the entry raises it.
    private readonly ConcurrentDictionary<int, PendingMove> _pending = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private CancellationTokenSource? _stop;
    private StreamWriter? _writer;

    private sealed record PendingMove(Guid ItemGuid, Guid StepGuid, decimal LuId, DataAsi To, Timer Timeout);

    public event Handlers.AsiEventEventHandler? AsiEvent;
    public event Handlers.AsiMovementIsCompletedEventHandler? AsiMovementIsCompleted;
    public event Handlers.AsiRequestsLuInfoEventHandler? AsiRequestsLuInfo;
    public event Handlers.AsiSendLuDataEventHandler? AsiSendLuData;
    public event Handlers.LuUnloadedFromAsiEventHandler? LuUnloadedFromAsi;
    public event Handlers.NewLuOnAsiEventHandler? NewLuOnAsi;

    public decimal Id => settings.Id;          // devices find their connection by this value
    public bool Managed { get; set; }          // set by the engine; starts false

    public void OpenConnection()
    {
        _stop = new CancellationTokenSource();
        _ = Task.Run(() => ReceiveLoopAsync(_stop.Token));
    }

    public void CloseConnection()
    {
        _stop?.Cancel();                        // harmless when never opened
        _stop = null;
        foreach (var id in _pending.Keys) Complete(id, ResultCodeTypes.ConnectionError, 0, "connection closed");
    }

    public void Dispose() => CloseConnection();

    public void MoveLuFromAsiToAsi(RunAsi sourceItem, DataAsi actionItemPerformer, DataAsi fromAsi, DataAsi toAsi,
        Guid stepGuid, decimal luId, int luHandlingMode = 0)
    {
        // Identifiers that survive a restart: the engine reserves them in blocks in wcs.db.
        var id = services.ReserveCommandId(Id);

        // The engine has no step timer: the timeout is the driver's job. TimeOutPeriod is the
        // device's TimeOutPlcMoving in milliseconds; 0 means "not configured" (60 s here).
        var timeout = sourceItem.TimeOutPeriod > 0 ? sourceItem.TimeOutPeriod : 60_000;
        var timer = new Timer(_ => Complete(id, ResultCodeTypes.TimeOut, 0, "no answer from the PLC"),
            null, Timeout.Infinite, Timeout.Infinite);
        _pending[id] = new PendingMove(sourceItem.ItemGuid, stepGuid, luId, toAsi, timer);
        timer.Change(timeout, Timeout.Infinite);

        _ = SendAsync(id, $"MOVE {id} {fromAsi.Asi} {fromAsi.Subitem} {fromAsi.Partition} " +
                          $"{toAsi.Asi} {toAsi.Subitem} {toAsi.Partition} {luId}");
    }

    public void MoveLuFromAsiToAsi(RunAsi sourceItem, DataAsi actionItemPerformer, Guid groupedMovementId,
        List<MovementLuDataCommand> loadUnitsToMove, int luHandlingMode = 0)
    {
        // This gateway has no grouped movement: answer (later, never inside the call) so that the
        // device does not wait forever.
        _ = Task.Run(() => RaiseCompleted(sourceItem.ItemGuid, groupedMovementId, ResultCodeTypes.Error, 0,
            "grouped movements are not supported by the Acme driver", null));
    }

    public void RequestLuDataFromAsi(RunAsi item) =>
        // No identification reader on this gateway: keep GetLoadUnitInfo off on its devices.
        _ = Task.Run(() => AsiSendLuData?.Invoke(this,
            new AsiSendLuDataEventArgs(item.ItemGuid, ResultCodeTypes.ConnectionError, null)));

    public void SendLuDataToAsi(RunAsi item, LuInfoDataCommand value) { }
    public void ShowMessageToAsi(RunAsi destinationItem, string message) { }
    public void SendCommandToAsi(RunAsi destinationItem, int commandCode, List<string?> parameters) { }

    private async Task SendAsync(int id, string line)
    {
        await Task.Yield();                      // never answer inside the engine's call
        try
        {
            await _writeGate.WaitAsync();
            try
            {
                if (_writer == null) throw new IOException("not connected");
                await _writer.WriteLineAsync(line);
            }
            finally { _writeGate.Release(); }
        }
        catch (Exception ex)
        {
            // A command that cannot be sent is answered, not dropped.
            Complete(id, ResultCodeTypes.ConnectionError, 0, ex.Message);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(settings.RemoteAddress!, settings.RemotePort, stop);
                using var stream = tcp.GetStream();
                using var reader = new StreamReader(stream);
                _writer = new StreamWriter(stream) { AutoFlush = true, NewLine = "\n" };
                await services.WriteLogAsync(new DriverLogRecord(Guid.Empty, settings.RemoteAddress!, "", "",
                    4 /* Information */, $"Acme connection {Id} open"));

                while (await reader.ReadLineAsync(stop) is { } line)
                    Dispatch(line.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception ex)
            {
                await services.WriteLogAsync(new DriverLogRecord(Guid.Empty, settings.RemoteAddress ?? "", "", "",
                    2 /* Warning */, $"Acme connection {Id}: {ex.Message}"));
            }

            _writer = null;
            foreach (var id in _pending.Keys) Complete(id, ResultCodeTypes.ConnectionError, 0, "connection lost");
            try { await Task.Delay(TimeSpan.FromSeconds(2), stop); } catch (OperationCanceledException) { }
        }
    }

    private void Dispatch(string[] f)
    {
        switch (f)
        {
            case ["DONE", var id, var code]:
                var errorCode = int.Parse(code);
                Complete(int.Parse(id), errorCode == 0 ? ResultCodeTypes.Success : ResultCodeTypes.Error, errorCode,
                    errorCode == 0 ? "" : $"Acme error {errorCode}");
                break;
            case ["NEWLU", var asi, var subitem, var partition, var luCode]:
                NewLuOnAsi?.Invoke(this, new NewLuOnAsiEventArgs(new NewLuOnAsiData
                    { Asi = asi, Subitem = subitem, Partition = partition, LuCode = luCode }));
                break;
            case ["GONE", var asi, var subitem, var partition]:
                LuUnloadedFromAsi?.Invoke(this, new LuUnloadedFromAsiEventArgs(new LuUnloadedFromAsiData
                    { Asi = asi, Subitem = subitem, Partition = partition }));
                break;
        }
    }

    private void Complete(int id, ResultCodeTypes result, int errorCode, string description)
    {
        if (!_pending.TryRemove(id, out var move)) return;   // already answered (DONE or timeout)
        move.Timeout.Dispose();
        // The final position goes back too: with RecordStorageLength = FromPlc the engine reads the
        // stored depth from FinalPosPartition.
        var final = new AsiMovementIsCompletedData
        {
            Lu = [new MoveLuDataResult { Id = (int)move.LuId, FinalPosAsi = move.To.Asi,
                                         FinalPosSubitem = move.To.Subitem, FinalPosPartition = move.To.Partition }]
        };
        RaiseCompleted(move.ItemGuid, move.StepGuid, result, errorCode, description, final);
    }

    private void RaiseCompleted(Guid itemGuid, Guid stepGuid, ResultCodeTypes result, int errorCode,
        string description, AsiMovementIsCompletedData? final) =>
        AsiMovementIsCompleted?.Invoke(this,
            new AsiMovementIsCompletedEventArgs(itemGuid, stepGuid, result, errorCode, description, final));
}
```

(The compiler warns that `AsiEvent` and `AsiRequestsLuInfo` are never raised: this gateway has no
such messages.)

**Plug it in.** Build it, copy `Acme.Wcs.Driver.dll` next to `Wcs.Host.exe`, set
`Wcs__ConnectionFactory`, start the host, load a plant whose devices are Connected to a driver
settings row with `plcAddress` and `pcmPort` (as in [driver settings](#driver-settings-and-devices)),
send command 1003 (the plant was imported: build the connections), then 1001.

What happened in the run that verified this page, when the fake gateway reported pallet `LU005003`
on IN01 (the gateway's log, `<` received from the driver, `>` sent to it):

```text
04:45:03.288 driver connected
04:45:07.667 > NEWLU 1001 0001 0001 LU005003
04:45:07.836 < MOVE 1 1001 0001 0001 1002 0001 0001 5003
04:45:08.143 > DONE 1 0
04:45:08.151 < MOVE 2 1002 0001 0001 1003 0001 0001 5003
04:45:08.461 > DONE 2 0
04:45:09.716 < MOVE 3 1003 0001 0001 2001 0001 0001 5003
04:45:10.028 > DONE 3 0
04:45:11.726 < MOVE 4 2001 0001 0001 3001 0101 0000 5003
04:45:12.029 > DONE 4 0
```

The engine resolved the code with the WMS, reserved cell 101 and moved the pallet hop by hop; the
last movement is the stacker crane storing into `3001` / `0101` / `0000`, the address of door A0101
from the WMS's partition map. The WMS then had the pallet in cell 101:

```json
{"loadUnitId":5003,"cellId":101,"storingDepth":1200}
```

and the navigator log had the driver's own line:

```json
{"id":10,"date":"2026-10-15T04:45:03.2858678","itemId":null,"asi":"127.0.0.1","partition":null,"subitem":null,"status":4,"description":"Acme connection 1 open","destinationItemId":null,"missionId":null,"missionStart":null,"missionEnd":null,"missionStatus":0,"runningItemId":null}
```

**Test it.** The engine's own tests use `IConnection` doubles assigned to a device
(`AutomationDevice.DeviceConnection`), for example
[`StepErrorCodeTest.cs`](../../WCS/Wcs.Engine/tests/Wcs.Engine.Test/StepErrorCodeTest.cs); for a
driver, a fake of your PLC on a local port, as above, lets you run the whole WCS against it.
