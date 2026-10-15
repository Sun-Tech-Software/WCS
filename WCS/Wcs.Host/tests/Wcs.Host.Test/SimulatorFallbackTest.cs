using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wcs.Api.Contracts;
using Wcs.Api.Contracts.Dtos;
using Wcs.Connection.Interface;
using Wcs.Engine;
using Wcs.Engine.Collections;
using Wcs.Engine.Persistence;
using Wcs.Host.Engine;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Host.Test;

/// <summary>
/// A device of connection type Connected whose connection is missing is never served by the
/// simulator unless <c>Wcs:AllowSimulatorForMissingConnections</c> says so, and that setting is
/// refused in Production. Run is refused while such a device exists, and the refusal is the outcome
/// of command 1001 that the API gives back (and the web Editor's Run switch shows).
/// </summary>
[Collection("host")]
public sealed class SimulatorFallbackTest(HostFixture host)
{
    [Fact]
    public void AProductionHostDoesNotStartWithTheSimulatorFallbackOn()
    {
        using var production = new SimulatorFallbackHost("Production");

        var failure = Assert.Throws<InvalidOperationException>(() => production.CreateClient());

        Assert.Contains(SimulatorFallbackPolicy.SettingKey, failure.Message, StringComparison.Ordinal);
        Assert.Contains("Production", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An environment with no name - ASPNETCORE_ENVIRONMENT set to an empty string, or to blanks -
    /// is not a test installation: it is refused like Production. Before, only the exact name
    /// Production was refused, and a published host started with an empty name accepted the simulator.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnUnnamedEnvironmentIsRefusedLikeProduction(string name)
    {
        var failure = Assert.Throws<InvalidOperationException>(
            () => SimulatorFallbackPolicy.Enforce(true, new Environment(name), new CapturingLogger()));

        Assert.Contains(SimulatorFallbackPolicy.SettingKey, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OutsideProductionTheFallbackIsAcceptedWithAWarning()
    {
        var logger = new CapturingLogger();

        SimulatorFallbackPolicy.Enforce(true, new Environment("Development"), logger);
        SimulatorFallbackPolicy.Enforce(false, new Environment("Production"), logger);

        var warning = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains(SimulatorFallbackPolicy.SettingKey, warning.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Command 1001 with a Connected device whose connection was not built: the command ends in
    /// error (99) with the refusal, naming the device, and the engine stays in Stop.
    /// </summary>
    [Fact]
    public async Task RunIsRefusedThroughTheApiWhileAConnectedDeviceLacksItsConnection()
    {
        // The host first: starting, it installs its field driver, which is what is put back at the end.
        var client = host.CreateAuthenticatedClient();
        var previousStore = Navigator.Store;
        var previousWriter = Navigator.StateWriter;
        var previousStatus = Navigator.NavStatus;
        var previousFactory = DeviceConnections.ConnectionFactory;
        using var store = WcsDatabase.InMemory();
        // The engine's state writer, as in operation: the command's updates land in order.
        var writer = new QueuedDatabaseWriter<WcsWrite>(new WcsBatchExecutor(store).ExecuteAsync,
            new QueuedDatabaseWriterOptions
                { DropOldestWhenFull = false, MaxBatchSize = 100, FlushInterval = TimeSpan.FromMilliseconds(10) });
        Navigator.Store = store;
        Navigator.StateWriter = writer;
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        DeviceConnections.UseConnectionFactory(new NothingFactory());
        var device = TestDevices.WithRow(Guid.NewGuid(), d =>
        {
            d.Name = "Shuttle under test";
            d.DeviceType = DeviceTypes.Automation;
            d.ConnectionType = ConnectionTypes.Connected;
            d.DeviceConnectionId = 41;
        });
        PathDevices.Instance.Add(device);
        try
        {
            var posted = await client.PostAsJsonAsync(ApiRoutes.Prefix + ApiRoutes.Commands,
                new CommandRequest((int)CommandTypes.SetNavigatorRun));
            Assert.Equal(HttpStatusCode.Created, posted.StatusCode);
            var queued = (await posted.Content.ReadFromJsonAsync<CommandDto>())!;

            await CommandToDo.Instance.Execute();
            await writer.FlushAsync();

            var outcome = (await client.GetFromJsonAsync<CommandDto>($"{ApiRoutes.Prefix}{ApiRoutes.Commands}/{queued.Id}"))!;
            Assert.Equal(99, outcome.Status);
            Assert.StartsWith("Run refused", outcome.ErrorDescription);
            Assert.Contains("Shuttle under test", outcome.ErrorDescription);
            Assert.Contains("connection with id 41", outcome.ErrorDescription);
            Assert.Equal(NavigatorStatusTypes.Stop, Navigator.NavStatus);
            Assert.Equal(DeviceStatusTypes.OffLine, device.Status);
        }
        finally
        {
            PathDevices.Instance.Remove(device);
            DeviceConnections.UseConnectionFactory(previousFactory);
            Navigator.NavStatus = previousStatus;
            Navigator.StateWriter = previousWriter;
            await writer.StopAsync();
            Navigator.Store = previousStore;
        }
    }

    private sealed class NothingFactory : IConnectionFactory
    {
        public void Attach(IConnectionServices services) { }
        public IConnection? Create(ConnectionSettings settings) => null;
    }

    private sealed class SimulatorFallbackHost(string environment) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Wcs:ApiKey"] = HostFixture.ApiKey,
                    ["Wcs:StartEngine"] = "false",
                    [SimulatorFallbackPolicy.SettingKey] = "true"
                }));
        }
    }

    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Wcs.Host";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }
}
