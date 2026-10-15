using Log.Interface;
using Microsoft.EntityFrameworkCore;
using Wcs.Connection.Interface;
using Wcs.Connection.Interface.CommandData;
using Wcs.Engine.Api;
using Wcs.Engine.Collections;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Wcs.Engine.Test.Routing;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// The field driver is plugged in through <see cref="IConnectionFactory"/>: the engine builds the
/// connections of the configured rows through the installed factory. A device configured for a
/// real connection that does not get one is never served by the loopback driver unless
/// <c>Wcs:AllowSimulatorForMissingConnections</c> says so: it goes OffLine, the log says why, and
/// Run is refused while it lacks its connection.
/// </summary>
public sealed class ConnectionFactoryTest : IDisposable
{
    private readonly RoutingTestPlant _plant = new();
    private readonly WcsHistoryDatabase? _previousHistory = Navigator.History;
    private readonly QueuedDatabaseWriter<HistoryWrite>? _previousHistoryWriter = Navigator.HistoryWriter;
    private readonly WcsDatabase? _previousStore = Navigator.Store;
    private readonly WcsHistoryDatabase _history = WcsHistoryDatabase.InMemory();
    private readonly NavigatorStatusTypes _previousStatus = Navigator.NavStatus;

    public ConnectionFactoryTest()
    {
        // The navigator log written straight to an in-memory history: no queue, nothing to wait for.
        Navigator.HistoryWriter = null;
        Navigator.History = _history;
    }

    public void Dispose()
    {
        DeviceConnections.CloseConnection();
        DeviceConnections.Clear();
        Navigator.NavStatus = _previousStatus;
        _plant.Dispose();
        Navigator.Store = _previousStore;
        Navigator.History = _previousHistory;
        Navigator.HistoryWriter = _previousHistoryWriter;
        _history.Dispose();
    }

    /// <summary>
    /// No driver: a <c>Connected</c> device is not served by the simulator (the engine would believe
    /// it talks to the field), it stays without connection, goes OffLine and the navigator log says
    /// why, as an error. A <c>Test</c> device next to it still gets the loopback driver.
    /// </summary>
    [Fact]
    public async Task WithoutADriverAConnectedDeviceGetsNoConnectionGoesOffLineAndTheLogSaysWhy()
    {
        using var installed = ConnectionFactoryScope.Install(null);
        var connected = _plant.Device("C");
        connected.ConnectionType = ConnectionTypes.Connected;
        connected.DeviceConnectionId = 3;
        var loopback = _plant.Device("T");
        loopback.ConnectionType = ConnectionTypes.Test;

        DeviceConnections.OpenConnection();

        Assert.Null(connected.DeviceConnection);
        Assert.Equal(DeviceStatusTypes.OffLine, connected.Status);
        Assert.IsType<Wcs.Connection.Test.Connection>(loopback.DeviceConnection);
        Assert.Equal(DeviceStatusTypes.Ready, loopback.Status);

        await using var context = _history.CreateContext();
        var line = Assert.Single(await context.NavigatorLog.Where(l => l.ItemId == connected.ItemGuid).ToListAsync());
        Assert.Equal((short)LogEntryType.Error, line.Status);
        Assert.Equal(
            $"OpenConnection: {DeviceConnections.DeviceLabel(connected)}: connection type Connected, but no field driver is configured (setting Wcs:ConnectionFactory); " +
            "only the loopback driver (connection type Test) is available; the device stays WITHOUT CONNECTION and goes OffLine",
            line.Description);
    }

    /// <summary>
    /// With a driver: the engine hands it the services first, then asks one connection per row the
    /// devices point to, with the row's settings; the device gets that connection, managed and opened.
    /// </summary>
    [Fact]
    public async Task TheInstalledFactoryBuildsTheConnectionOfEachConfiguredRow()
    {
        Navigator.Store = WcsDatabase.InMemory();
        await using (var context = Navigator.Store.CreateContext())
        {
            context.PlcConnections.Add(new PlcConnection
            {
                Id = 7, PlcAddress = "10.0.0.7", PcmPort = 2000, LocalAddress = "10.0.0.1", LocalPort = 2001,
                OpProgress = 55
            });
            await context.SaveChangesAsync();
        }

        var built = new FakeConnection(7);
        var factory = new RecordingConnectionFactory { Build = settings => settings.Id == 7 ? built : null };
        using var installed = ConnectionFactoryScope.Install(factory);
        var device = _plant.Device("C");
        device.ConnectionType = ConnectionTypes.Connected;
        device.DeviceConnectionId = 7;

        DeviceConnections.Initialize();
        DeviceConnections.OpenConnection();

        Assert.Equal(["Attach", "Attach", "Create:7"], factory.Calls);
        Assert.Equal(new ConnectionSettings(7, "10.0.0.7", 2000, "10.0.0.1", 2001, 55u), Assert.Single(factory.Created));
        Assert.Same(built, DeviceConnections.Instance[7]);
        Assert.Same(built, device.DeviceConnection);
        Assert.True(built.Managed);
        Assert.Equal(1, built.Opened);
        Assert.NotEqual(DeviceStatusTypes.OffLine, device.Status);
    }

    /// <summary>
    /// A row the driver cannot use (it answers <c>null</c>) gives no connection, and a
    /// <c>Connected</c> device that points to it is NOT served by the simulator: the engine would
    /// believe it drives the machine while nothing moves. It stays without connection, goes OffLine,
    /// and the navigator log has an error naming the device and the missing connection id.
    /// </summary>
    [Fact]
    public async Task ARowTheDriverRefusesLeavesTheDeviceWithoutConnectionOffLineWithAnError()
    {
        Navigator.Store = WcsDatabase.InMemory();
        await using (var context = Navigator.Store.CreateContext())
        {
            context.PlcConnections.Add(new PlcConnection { Id = 8 });
            await context.SaveChangesAsync();
        }

        var factory = new RecordingConnectionFactory();
        using var installed = ConnectionFactoryScope.Install(factory);
        var device = _plant.Device("C");
        device.ConnectionType = ConnectionTypes.Connected;
        device.DeviceConnectionId = 8;

        DeviceConnections.Initialize();
        DeviceConnections.OpenConnection();

        Assert.Equal(new ConnectionSettings(8, null, 0, null, 0, 0u), Assert.Single(factory.Created));
        Assert.Null(DeviceConnections.Instance[8]);
        Assert.Null(device.DeviceConnection);
        Assert.Equal(DeviceStatusTypes.OffLine, device.Status);

        await using var history = _history.CreateContext();
        var line = Assert.Single(await history.NavigatorLog.Where(l => l.ItemId == device.ItemGuid).ToListAsync());
        Assert.Equal((short)LogEntryType.Error, line.Status);
        Assert.Contains(DeviceConnections.DeviceLabel(device), line.Description);
        Assert.Contains("connection with id 8", line.Description);
        Assert.Contains("WITHOUT CONNECTION", line.Description);
    }

    /// <summary>
    /// Only with <c>Wcs:AllowSimulatorForMissingConnections</c> does the old behaviour come back:
    /// the device is served by the simulator, with a warning in the navigator log.
    /// </summary>
    [Fact]
    public async Task WithTheExplicitSettingAMissingConnectionFallsBackToTheSimulatorWithAWarning()
    {
        using var allowed = SimulatorFallbackScope.Allow(true);
        using var installed = ConnectionFactoryScope.Install(new RecordingConnectionFactory());
        var device = _plant.Device("C");
        device.ConnectionType = ConnectionTypes.Connected;
        device.DeviceConnectionId = 8;

        DeviceConnections.OpenConnection();

        Assert.IsType<Wcs.Connection.Test.Connection>(device.DeviceConnection);
        Assert.NotEqual(DeviceStatusTypes.OffLine, device.Status);

        await using var history = _history.CreateContext();
        var line = Assert.Single(await history.NavigatorLog.Where(l => l.ItemId == device.ItemGuid).ToListAsync());
        Assert.Equal((short)LogEntryType.Warning, line.Status);
        Assert.Contains("SIMULATOR", line.Description);
    }

    /// <summary>
    /// A <c>Connected</c> device without a connection id is left without connection like the
    /// others, and goes OffLine too: it used to keep its status, as if it could be driven.
    /// </summary>
    [Fact]
    public async Task AConnectedDeviceWithoutAConnectionIdGoesOffLineWithAnError()
    {
        using var installed = ConnectionFactoryScope.Install(new RecordingConnectionFactory());
        var device = _plant.Device("C");
        device.ConnectionType = ConnectionTypes.Connected;
        device.DeviceConnectionId = 0;

        DeviceConnections.OpenConnection();

        Assert.Null(device.DeviceConnection);
        Assert.Equal(DeviceStatusTypes.OffLine, device.Status);
        await using var history = _history.CreateContext();
        var line = Assert.Single(await history.NavigatorLog.Where(l => l.ItemId == device.ItemGuid).ToListAsync());
        Assert.Equal((short)LogEntryType.Error, line.Status);
    }

    /// <summary>
    /// Run is refused while a <c>Connected</c> device lacks its connection: the engine stays in
    /// Stop, nothing is opened, the refusal names every such device with its reason, and the
    /// devices go OffLine with their error line.
    /// </summary>
    [Fact]
    public async Task RunIsRefusedWhileAConnectedDeviceLacksItsConnection()
    {
        using var installed = ConnectionFactoryScope.Install(new RecordingConnectionFactory());
        var missing = _plant.Device("C");
        missing.ConnectionType = ConnectionTypes.Connected;
        missing.DeviceConnectionId = 8;
        var noId = _plant.Device("Z");
        noId.ConnectionType = ConnectionTypes.Connected;
        noId.DeviceConnectionId = 0;
        var loopback = _plant.Device("T");
        loopback.ConnectionType = ConnectionTypes.Test;
        Navigator.NavStatus = NavigatorStatusTypes.Stop;

        var refused = await Assert.ThrowsAsync<EngineRunRefusedException>(ExecutableCommands.StartNavigator);

        Assert.Equal(NavigatorStatusTypes.Stop, Navigator.NavStatus);
        Assert.Contains(DeviceConnections.DeviceLabel(missing), refused.Message);
        Assert.Contains("connection with id 8", refused.Message);
        Assert.Contains(DeviceConnections.DeviceLabel(noId), refused.Message);
        Assert.DoesNotContain(DeviceConnections.DeviceLabel(loopback), refused.Message);
        Assert.Contains("Wcs:AllowSimulatorForMissingConnections", refused.Message);
        Assert.Null(missing.DeviceConnection);
        Assert.Null(loopback.DeviceConnection);
        Assert.Equal(DeviceStatusTypes.OffLine, missing.Status);
        Assert.Equal(DeviceStatusTypes.OffLine, noId.Status);

        await using var history = _history.CreateContext();
        var line = Assert.Single(await history.NavigatorLog.Where(l => l.ItemId == missing.ItemGuid).ToListAsync());
        Assert.Equal((short)LogEntryType.Error, line.Status);
    }

    /// <summary>
    /// With the explicit setting Run is not refused for a missing connection: a warning says that
    /// devices will be served by the simulator.
    /// </summary>
    [Fact]
    public async Task WithTheExplicitSettingRunIsNotRefusedAndAWarningSaysSo()
    {
        using var allowed = SimulatorFallbackScope.Allow(true);
        using var installed = ConnectionFactoryScope.Install(new RecordingConnectionFactory());
        var missing = _plant.Device("C");
        missing.ConnectionType = ConnectionTypes.Connected;
        missing.DeviceConnectionId = 8;
        Navigator.NavStatus = NavigatorStatusTypes.Stop;

        await ExecutableCommands.StartNavigator();

        await using var history = _history.CreateContext();
        var warning = Assert.Single(await history.NavigatorLog
            .Where(l => l.Status == (short)LogEntryType.Warning && l.Description!.Contains("Wcs:AllowSimulatorForMissingConnections"))
            .ToListAsync());
        Assert.Contains(DeviceConnections.DeviceLabel(missing), warning.Description);
    }

    /// <summary>
    /// A driver log line reaches the navigator log with its address, item and level.
    /// </summary>
    [Fact]
    public async Task ADriverLogLineReachesTheNavigatorLog()
    {
        var factory = new RecordingConnectionFactory();
        using var installed = ConnectionFactoryScope.Install(factory);
        var item = Guid.NewGuid();

        await factory.Services!.WriteLogAsync(new DriverLogRecord(item, "0100", "0002", "0003",
            (byte)LogEntryType.Error, "PLC 10.0.0.7 not answering"));

        await using var context = _history.CreateContext();
        var line = Assert.Single(await context.NavigatorLog.Where(l => l.ItemId == item).ToListAsync());
        Assert.Equal(("0100", "0002", "0003"), (line.Asi, line.Subitem, line.Partition));
        Assert.Equal((short)LogEntryType.Error, line.Status);
        Assert.Equal("PLC 10.0.0.7 not answering", line.Description);
    }

    /// <summary>A connection that talks to nobody and counts how many times it was opened.</summary>
    private sealed class FakeConnection(decimal id) : IConnection
    {
        public decimal Id => id;
        public bool Managed { get; set; }
        public int Opened { get; private set; }

#pragma warning disable CS0067
        public event Handlers.AsiEventEventHandler? AsiEvent;
        public event Handlers.AsiMovementIsCompletedEventHandler? AsiMovementIsCompleted;
        public event Handlers.AsiRequestsLuInfoEventHandler? AsiRequestsLuInfo;
        public event Handlers.AsiSendLuDataEventHandler? AsiSendLuData;
        public event Handlers.LuUnloadedFromAsiEventHandler? LuUnloadedFromAsi;
        public event Handlers.NewLuOnAsiEventHandler? NewLuOnAsi;
#pragma warning restore CS0067

        public void CloseConnection() { }
        public void Dispose() { }

        public void MoveLuFromAsiToAsi(RunAsi sourceItem, DataAsi actionItemPerformer, DataAsi fromAsi, DataAsi toAsi,
            Guid stepGuid, decimal luId, int luHandlingMode = 0) { }

        public void MoveLuFromAsiToAsi(RunAsi sourceItem, DataAsi actionItemPerformer, Guid groupedMovementId,
            List<MovementLuDataCommand> loadUnitsToMove, int luHandlingMode = 0) { }

        public void OpenConnection() => Opened++;
        public void RequestLuDataFromAsi(RunAsi item) { }
        public void SendCommandToAsi(RunAsi destinationItem, int commandCode, List<string?> parameters) { }
        public void SendLuDataToAsi(RunAsi item, LuInfoDataCommand value) { }
        public void ShowMessageToAsi(RunAsi destinationItem, string message) { }
    }
}
