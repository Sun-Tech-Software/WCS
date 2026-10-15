using Wcs.Api.Client;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Live;
using Wcs.WebEditor.Model;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

public sealed class LiveRouteReaderTest
{
    private static readonly Guid Here = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
    private static readonly Guid There = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002");
    private static readonly Guid Mission = Guid.Parse("eeeeeeee-0000-4000-8000-000000000005");

    private static MissionDto MissionAt(Guid position, Guid destination) =>
        new(Mission, 7m, "LU-7", position, null, destination, DateTime.UtcNow, null, null, null, default, null, null,
            default, null, default, 0m, 0m, 0m, null, 0, null, null, null, null, null, null, null, 0m, null, 0m,
            false, 0m, false, false);

    private static (LiveRouteReader Reader, DrawioWriteHandler Host) Reader()
    {
        var host = new DrawioWriteHandler();
        var client = new WcsClient(new WcsClientOptions { BaseAddress = new Uri("http://localhost:5580"), ApiKey = "k" }, host);
        return (new LiveRouteReader(() => client, () => new PlantGraph([], [], [])), host);
    }

    [Fact]
    public async Task ALoadedDeviceWithAMissionHasItsRoute()
    {
        var (reader, host) = Reader();
        host.Missions = [MissionAt(Here, There)];
        host.MissionSteps[Mission] =
            [new MissionStepDto(Guid.NewGuid(), Mission, Here, There, null, DateTime.UtcNow, DateTime.UtcNow, default, default, Guid.Empty, 0)];

        var route = await reader.ReadAsync(Here, loaded: true);

        Assert.Equal(new LiveRouteView(Here, There, There), route);
    }

    [Fact]
    public async Task AnEmptyDeviceIsNotEvenAsked()
    {
        var (reader, host) = Reader();
        host.Missions = [MissionAt(Here, There)];

        Assert.Null(await reader.ReadAsync(Here, loaded: false));
        Assert.DoesNotContain(host.Requests, request => request.Contains("/missions"));
    }

    [Fact]
    public async Task NoMissionHereNoRoute()
    {
        var (reader, host) = Reader();
        host.Missions = [MissionAt(There, Here)];

        Assert.Null(await reader.ReadAsync(Here, loaded: true));
    }

    [Fact]
    public async Task AnAnswerForAMouseThatHasMovedOnIsThrownAway()
    {
        var (reader, host) = Reader();
        host.Missions = [MissionAt(Here, There)];
        host.MissionsGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var late = reader.ReadAsync(Here, loaded: true);
        reader.Forget();
        host.MissionsGate.SetResult();

        Assert.Null(await late);
    }

    /// <summary>
    /// The mouse can move on between the two reads too: the missions answered in time, the steps
    /// did not. The version is checked again after the second read, and this is the test of that
    /// second check - the one above cannot see it, because its answer is already stale at the first.
    /// </summary>
    [Fact]
    public async Task AnAnswerWhoseStepsArriveAfterTheMouseHasMovedOnIsThrownAway()
    {
        var (reader, host) = Reader();
        host.Missions = [MissionAt(Here, There)];
        host.MissionSteps[Mission] =
            [new MissionStepDto(Guid.NewGuid(), Mission, Here, There, null, DateTime.UtcNow, DateTime.UtcNow, default, default, Guid.Empty, 0)];
        host.MissionStepsGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var late = reader.ReadAsync(Here, loaded: true);
        await host.MissionStepsGateReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        reader.Forget();
        host.MissionStepsGate.SetResult();

        Assert.Null(await late);
    }

    [Fact]
    public async Task AFailedReadDrawsNothingAndDoesNotThrow()
    {
        var (reader, host) = Reader();
        host.BrokenReads.Add("missions");

        Assert.Null(await reader.ReadAsync(Here, loaded: true));
    }
}
