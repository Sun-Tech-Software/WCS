using Microsoft.AspNetCore.SignalR.Client;
using Wcs.Api.Contracts;
using Wcs.Api.Contracts.Dtos;
using Wcs.Engine;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Host.Test;

[Collection("host")]
public sealed class PlantHubTest(HostFixture host)
{
    private HubConnection Connect(string apiKey = HostFixture.ApiKey) => new HubConnectionBuilder()
        .WithUrl(host.Server.BaseAddress + ApiRoutes.Hub.TrimStart('/'), options =>
        {
            options.Headers[ApiRoutes.ApiKeyHeader] = apiKey;
            options.HttpMessageHandlerFactory = _ => host.Server.CreateHandler();
        })
        .Build();

    [Fact]
    public async Task AStatusChangeInTheEngineArrivesAsAMessage()
    {
        await using var connection = Connect();
        var received = new TaskCompletionSource<EngineStatus>();
        connection.On<EngineStatus>(nameof(IPlantEvents.NavigatorStatusChanged), s => received.TrySetResult(s));
        await connection.StartAsync();

        Navigator.NavStatus = NavigatorStatusTypes.Pause;
        try
        {
            var status = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(NavigatorStatusTypes.Pause, status.NavStatus);
        }
        finally { Navigator.NavStatus = NavigatorStatusTypes.Run; }
    }

    [Fact]
    public async Task TheReadOnlyKeyReceivesHubEvents()
    {
        await using var connection = Connect(HostFixture.ReadOnlyApiKey);
        var received = new TaskCompletionSource<EngineStatus>();
        connection.On<EngineStatus>(nameof(IPlantEvents.NavigatorStatusChanged), s => received.TrySetResult(s));
        await connection.StartAsync();

        Navigator.NavStatus = NavigatorStatusTypes.Pause;
        try
        {
            var status = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(NavigatorStatusTypes.Pause, status.NavStatus);
        }
        finally { Navigator.NavStatus = NavigatorStatusTypes.Run; }
    }

    [Fact]
    public async Task TheWatchdogTicksWithinThreeSeconds()
    {
        await using var connection = Connect();
        var received = new TaskCompletionSource<DateTime>();
        connection.On<DateTime>(nameof(IPlantEvents.Watchdog), t => received.TrySetResult(t));
        await connection.StartAsync();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task WithoutTheKeyTheHubRefusesTheConnection()
    {
        await using var connection = new HubConnectionBuilder()
            .WithUrl(host.Server.BaseAddress + ApiRoutes.Hub.TrimStart('/'), o => o.HttpMessageHandlerFactory = _ => host.Server.CreateHandler())
            .Build();
        var refused = await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync());
        // Non un guasto qualsiasi: il rifiuto della chiave, cioe' 401.
        Assert.Contains("401", refused.Message);
    }

    [Fact]
    public async Task AQueryTokenDoesNotAuthenticateTheHub()
    {
        await using var connection = new HubConnectionBuilder()
            .WithUrl(host.Server.BaseAddress + ApiRoutes.Hub.TrimStart('/') +
                     "?access_token=" + HostFixture.ApiKey,
                options => options.HttpMessageHandlerFactory = _ => host.Server.CreateHandler())
            .Build();
        var refusal = await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync());
        Assert.Contains("401", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheChangeEnumsMatchMemberByMember()
    {
        Assert.Equal(Enum.GetNames<Wcs.Engine.Api.EngineMissionChange>(), Enum.GetNames<MissionChange>());
        Assert.Equal(Enum.GetNames<Wcs.Engine.Api.EngineQueueChange>(), Enum.GetNames<QueueChange>());
    }
}
