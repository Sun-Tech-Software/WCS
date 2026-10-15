using Log.Interface;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wcs.Connection.Interface;
using Wcs.Engine;
using Wcs.Engine.Api;
using Wcs.Engine.Collections;
using Wcs.Engine.Persistence;
using Wcs.Host.Engine;
using Wcs.Host.Hubs;
using Wcs.Api.Contracts;
using Wcs.Wms.Client;
using Xunit;

namespace Wcs.Host.Test;

/// <summary>
/// The host picks the field driver from <c>Wcs:ConnectionFactory</c> and hands it to the engine at
/// startup, before the <c>StartEngine</c> guard; a name that cannot be loaded stops the host.
/// </summary>
/// <remarks>
/// The services are built by hand with the engine off (<c>StartEngine</c> false), as in
/// <c>EngineWarehouseWiringTest</c>: what is looked at is the wiring, and the statics it touches
/// (<c>Navigator</c>, <c>DeviceConnections</c>) are put back by <see cref="Dispose"/>. In the
/// collection "host" because those statics are shared with the in-memory host.
/// </remarks>
[Collection("host")]
public sealed class ConnectionFactoryWiringTest(HostFixture host) : IDisposable
{
    private readonly IConnectionFactory? _previousFactory = DeviceConnections.ConnectionFactory;
    private readonly IEngineEventSink? _previousSink = Navigator.EventSink;
    private readonly Action<Exception>? _previousErrorLog = Navigator.HostErrorLog;
    private readonly IWriter? _previousEngineLog = Navigator.EventLogWriter;
    private readonly Action<string>? _previousHistoryNotice = WcsHistoryDatabase.Notice;
    private readonly Action<string>? _previousPlcLogNotice = WcsPlcLogDatabase.Notice;
    private readonly WcsPlcLogDatabase? _previousPlcLog = Navigator.PlcLog;
    private readonly QueuedDatabaseWriter<PlcLogWrite>? _previousPlcLogWriter = Navigator.PlcLogWriter;

    public void Dispose()
    {
        DeviceConnections.UseConnectionFactory(_previousFactory);
        Navigator.EventSink = _previousSink;
        Navigator.HostErrorLog = _previousErrorLog;
        Navigator.SetEventLogWriter(_previousEngineLog);
        WcsHistoryDatabase.Notice = _previousHistoryNotice;
        WcsPlcLogDatabase.Notice = _previousPlcLogNotice;
        Navigator.PlcLog = _previousPlcLog;
        Navigator.PlcLogWriter = _previousPlcLogWriter;
    }

    /// <summary>
    /// The configured type is built and installed, and it has the engine services before the
    /// engine starts.
    /// </summary>
    [Fact]
    public async Task TheConfiguredFactoryIsInstalledWithTheEngineServices()
    {
        var service = CreateService(typeof(RecordingFactory).AssemblyQualifiedName);

        await service.StartAsync(default);

        var factory = Assert.IsType<RecordingFactory>(DeviceConnections.ConnectionFactory);
        Assert.NotNull(factory.Services);
    }

    /// <summary>
    /// The services the host hands the driver write the message log into the engine's driver
    /// message database (<c>wcs-plc-YYYY-MM.db</c>).
    /// </summary>
    [Fact]
    public async Task TheServicesHandedToTheDriverWriteToThePlcMessageDatabase()
    {
        using var plcLog = WcsPlcLogDatabase.InMemory();
        Navigator.PlcLogWriter = null;
        Navigator.PlcLog = plcLog;
        var service = CreateService(typeof(RecordingFactory).AssemblyQualifiedName);
        await service.StartAsync(default);
        var factory = Assert.IsType<RecordingFactory>(DeviceConnections.ConnectionFactory);

        await factory.Services!.WriteMessageAsync(new MessageLogRecord(DateTime.Now, 42m, 1, 8, "SYS1", 2, 3, 4, 5,
            6, 7, false, "", "0100", "0002", "0003", 32, 4711, "<Sent />"));

        await using var context = plcLog.CreateContext();
        var row = Assert.Single(context.PlcMessages);
        Assert.Equal((42m, "0100", 4711, "<Sent />"), (row.PlcId, row.Asi, row.MessageId, row.Xml));
    }

    /// <summary>
    /// An empty key or <c>none</c>: no field driver, the loopback driver only, and the host says
    /// in its log that the setting turned it off.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("none")]
    public async Task AnEmptyKeyLeavesOnlyTheLoopbackDriverAndSaysSo(string key)
    {
        DeviceConnections.UseConnectionFactory(new RecordingFactory());
        var log = new RecordingLogger();
        var service = CreateService(key, log);

        await service.StartAsync(default);

        Assert.Null(DeviceConnections.ConnectionFactory);
        var warning = Assert.Single(log.Entries, e => e.Message.StartsWith("No field driver"));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Equal(
            "No field driver (Wcs:ConnectionFactory turns it off): only the loopback driver is available, " +
            "and devices with connection type Connected stay without connection (OffLine), and Run is refused while the plant has any",
            warning.Message);
    }

    /// <summary>
    /// No key in a build without a default driver (the public build): no field driver, and the
    /// host says in its log that the key is missing and the build has none. The default is read
    /// from the driver contract assembly, which carries none (the test assembly does), so this
    /// runs in every build.
    /// </summary>
    [Fact]
    public async Task NoKeyInABuildWithoutADefaultLeavesOnlyTheLoopbackDriverAndSaysSo()
    {
        DeviceConnections.UseConnectionFactory(new RecordingFactory());
        var log = new RecordingLogger();
        var service = CreateService(null, log, typeof(IConnectionFactory).Assembly);

        await service.StartAsync(default);

        Assert.Null(DeviceConnections.ConnectionFactory);
        var warning = Assert.Single(log.Entries, e => e.Message.StartsWith("No field driver"));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Equal(
            "No field driver (Wcs:ConnectionFactory is not set and this build has no default): only the loopback " +
            "driver is available, and devices with connection type Connected stay without connection (OffLine), and Run is refused while the plant has any",
            warning.Message);
    }

    /// <summary>
    /// No key at all: the default of the build. A build that ships a driver names it in the host
    /// assembly (the driver's own tests pin which one); a build
    /// without one has no field driver.
    /// </summary>
    [Fact]
    public async Task NoKeyInstallsTheDefaultOfTheBuild()
    {
        var expected = ConnectionFactoryLoader.ResolveTypeName(null, typeof(EngineHostedService).Assembly);
        var service = CreateService(null);

        await service.StartAsync(default);

        var installed = DeviceConnections.ConnectionFactory?.GetType();
        Assert.Equal(expected, installed == null ? null : $"{installed.FullName}, {installed.Assembly.GetName().Name}");
    }

    /// <summary>The in-memory host of the collection is wired the same way: no key, the build default.</summary>
    [Fact]
    public void TheRunningHostInstalledTheDefaultOfTheBuild()
    {
        host.CreateAuthenticatedClient();

        var expected = ConnectionFactoryLoader.ResolveTypeName(null, typeof(EngineHostedService).Assembly);
        var installed = DeviceConnections.ConnectionFactory?.GetType();
        Assert.Equal(expected, installed == null ? null : $"{installed.FullName}, {installed.Assembly.GetName().Name}");
    }

    /// <summary>
    /// A name that does not load stops the host, and the message names the key, the value and how
    /// to turn the driver off.
    /// </summary>
    [Theory]
    [InlineData("Nowhere.Factory, Nowhere.Assembly", "the type was not found")]
    [InlineData("Wcs.Host.Test.ConnectionFactoryWiringTest+NotAFactory, Wcs.Host.Test", "does not implement Wcs.Connection.Interface.IConnectionFactory")]
    [InlineData("Wcs.Host.Test.ConnectionFactoryWiringTest+FactoryWithArguments, Wcs.Host.Test", "needs a public parameterless constructor")]
    public async Task ANameThatDoesNotLoadStopsTheHost(string key, string reason)
    {
        var service = CreateService(key);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(default));

        Assert.StartsWith($"Wcs:ConnectionFactory = \"{key}\": ", error.Message);
        Assert.Contains(reason, error.Message);
        Assert.Contains("Wcs__ConnectionFactory", error.Message);
        Assert.Contains("\"none\"", error.Message);
    }

    private static EngineHostedService CreateService(string? connectionFactory, ILogger<EngineHostedService>? logger = null,
        System.Reflection.Assembly? defaultDriverSource = null)
        => new(Options.Create(new WcsHostOptions { ApiKey = "test-key", StartEngine = false, ConnectionFactory = connectionFactory }),
            Options.Create(new WmsOptions()), new NoClients(),
            new PlantEventRelay(new SilentHub(), NullLogger<PlantEventRelay>.Instance),
            logger ?? NullLogger<EngineHostedService>.Instance)
        {
            DefaultDriverSource = defaultDriverSource ?? typeof(EngineHostedService).Assembly
        };

    public sealed class RecordingFactory : IConnectionFactory
    {
        public IConnectionServices? Services { get; private set; }
        public void Attach(IConnectionServices services) => Services = services;
        public IConnection? Create(ConnectionSettings settings) => null;
    }

    public sealed class NotAFactory;

    public sealed class FactoryWithArguments(int unused) : IConnectionFactory
    {
        public int Unused => unused;
        public void Attach(IConnectionServices services) { }
        public IConnection? Create(ConnectionSettings settings) => null;
    }

    private sealed class RecordingLogger : ILogger<EngineHostedService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class NoClients : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new NotSupportedException("the engine is off in these tests");
    }

    private sealed class SilentHub : IHubContext<PlantHub, IPlantEvents>
    {
        public IHubClients<IPlantEvents> Clients => throw new NotSupportedException("the wiring publishes no event");
        public IGroupManager Groups => throw new NotSupportedException("the wiring publishes no event");
    }
}
