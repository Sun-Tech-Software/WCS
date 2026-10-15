using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Live;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

public sealed class PlantLiveStateTest
{
    private static readonly Guid A = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002");
    private static readonly Guid C = Guid.Parse("cccccccc-0000-4000-8000-000000000003");
    private static readonly Guid Step = Guid.Parse("dddddddd-0000-4000-8000-000000000004");

    private static ItemStatus Status(Guid id, DeviceStatusTypes status = DeviceStatusTypes.Ready,
        bool loaded = false, Guid running = default, Guid waiting = default) =>
        new(id, "", "", "", id.ToString()[..4], status,
            loaded ? LuLoadStatusTypes.Loaded : LuLoadStatusTypes.None, loaded ? "LU-7" : "", loaded ? 7m : 0m,
            running, waiting);

    private static PlantLiveState State(params (Guid Id, DeviceTypes Type)[] types)
    {
        var byId = types.ToDictionary(t => t.Id, t => t.Type);
        return new PlantLiveState(id => byId.TryGetValue(id, out var type) ? type : null);
    }

    [Theory]
    [InlineData(DeviceStatusTypes.Error, "#ff0000")]
    [InlineData(DeviceStatusTypes.Moving, "#ffa500")]
    [InlineData(DeviceStatusTypes.Loading, "#0000ff")]
    [InlineData(DeviceStatusTypes.Unloading, "#00ffff")]
    [InlineData(DeviceStatusTypes.OffLine, "#ee82ee")]
    [InlineData(DeviceStatusTypes.Locked, "#808080")]
    [InlineData(DeviceStatusTypes.Warning, "#ffff00")]
    [InlineData(DeviceStatusTypes.Busy, "#ffffff")]
    [InlineData(DeviceStatusTypes.Recovering, "#8b4513")]
    public void EveryStatusButReadyHasItsColour(DeviceStatusTypes status, string colour) =>
        Assert.Equal(colour, PlantLiveState.ColorOf(status));

    [Fact]
    public void ReadyHasNoIndicator() => Assert.Null(PlantLiveState.ColorOf(DeviceStatusTypes.Ready));

    [Fact]
    public void EveryDeclaredStatusIsAnsweredOneWayOrTheOther()
    {
        // The subjects come from the enum, not from the switch under test: a status added to the
        // engine and forgotten here would fall into the "unknown" name and be seen.
        foreach (var status in Enum.GetValues<DeviceStatusTypes>())
            Assert.DoesNotContain("Status ", PlantLiveState.NameOf(status));
    }

    [Fact]
    public void AReadyEmptyDeviceIsNotSentAtAll()
    {
        var state = State();
        state.Reset([Status(A), Status(B, DeviceStatusTypes.Error)]);

        var device = Assert.Single(state.Devices());
        Assert.Equal(B, device.ItemId);
        Assert.Equal("#ff0000", device.StatusColor);
        Assert.Equal("In Error", device.StatusName);
    }

    [Fact]
    public void ALoadedReadyDeviceIsSentForItsLoadUnitOnly()
    {
        var state = State();
        state.Reset([Status(A, loaded: true)]);

        var device = Assert.Single(state.Devices());
        Assert.Null(device.StatusColor);
        Assert.Equal("LU-7 (7)", device.LoadUnit);
    }

    [Fact]
    public void AnEventReplacesTheDevicesPreviousStatus()
    {
        var state = State();
        state.Reset([Status(A, DeviceStatusTypes.Error)]);

        state.Apply(Status(A));

        Assert.Empty(state.Devices());
        Assert.Equal(DeviceStatusTypes.Ready, state.StatusOf(A)!.Status);
    }

    [Fact]
    public void ASnapshotThrowsAwayWhatTheEventsHadSaid()
    {
        var state = State();
        state.Apply(Status(A, DeviceStatusTypes.Error));

        state.Reset([Status(B, DeviceStatusTypes.Moving)]);

        Assert.Null(state.StatusOf(A));
        Assert.Equal(B, Assert.Single(state.Devices()).ItemId);
    }

    [Fact]
    public void TheMovementGoesFromTheDeviceRunningTheStepToTheOneWaitingForIt()
    {
        var state = State((A, DeviceTypes.Automation), (B, DeviceTypes.Automation));
        state.Reset([Status(A, running: Step), Status(B, waiting: Step)]);

        Assert.Equal(new LiveMotion(A, B), Assert.Single(state.Motions()));
    }

    [Fact]
    public void NoMovementWhenNobodyWaitsForTheStep()
    {
        var state = State((A, DeviceTypes.Automation));
        state.Reset([Status(A, running: Step)]);

        Assert.Empty(state.Motions());
    }

    [Fact]
    public void NoMovementWhenTheStepEnds()
    {
        var state = State((A, DeviceTypes.Automation), (B, DeviceTypes.Automation));
        state.Reset([Status(A, running: Step), Status(B, waiting: Step)]);

        state.Apply(Status(A));

        Assert.Empty(state.Motions());
    }

    [Fact]
    public void NoMovementOntoItself()
    {
        var state = State((A, DeviceTypes.Automation));
        state.Reset([Status(A, running: Step, waiting: Step)]);

        Assert.Empty(state.Motions());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NoMovementWhenEitherEndIsAWarehouse(bool fromIsTheWarehouse)
    {
        var state = State(
            (A, fromIsTheWarehouse ? DeviceTypes.Storage : DeviceTypes.Automation),
            (B, fromIsTheWarehouse ? DeviceTypes.Automation : DeviceTypes.Storage),
            (C, DeviceTypes.Automation));
        state.Reset([Status(A, running: Step), Status(B, waiting: Step)]);

        Assert.Empty(state.Motions());
    }
}
