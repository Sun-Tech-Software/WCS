using Wcs.Connection.Interface;
using Wcs.Connection.Interface.CommandData;
using Wcs.Connection.Interface.ConnectionData;
using Wcs.Connection.Interface.Events;
using Wcs.Engine.Collections;
using Wcs.Engine.Items;
using Wcs.Engine.Test.Routing;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// A PLC result belongs only to the step GUID it supplies: an old or foreign result must not be
/// applied to the step currently in flight on the device.
/// </summary>
public sealed class PlcStepCorrelationTest : IDisposable
{
    private readonly RoutingTestPlant _plant = new(NavigatorStatusTypes.Run);

    public void Dispose()
    {
        _plant.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public Task AnUnknownSuccessDoesNotCompleteTheRunningStep() =>
        AnUnknownResultDoesNotChangeTheRunningStep(ResultCodeTypes.Success);

    [Fact]
    public Task AnUnknownErrorDoesNotFailTheRunningStep() =>
        AnUnknownResultDoesNotChangeTheRunningStep(ResultCodeTypes.Error);

    [Fact]
    public Task AnUnknownTimeoutDoesNotTimeOutTheRunningStep() =>
        AnUnknownResultDoesNotChangeTheRunningStep(ResultCodeTypes.TimeOut);

    private async Task AnUnknownResultDoesNotChangeTheRunningStep(ResultCodeTypes result)
    {
        var (source, destination, step, mission) = AStartedSingleMovement();
        var rejectedStepId = Guid.NewGuid();
        using var log = new RecordingLogWriter();

        await source.ConnectionAsiMovementIsCompleted(new AsiMovementIsCompletedEventArgs(
            source.ItemGuid, rejectedStepId, result, 15, "Aisle not enabled", null));

        Assert.Contains(step, MissionSteps.Instance.List);
        Assert.Equal(ResultCodeTypes.None, step.StepStatus);
        Assert.Equal(MissionStatusTypes.Run, mission.Status);
        Assert.True(destination.IsWaitingForStep(step.StepId));
        Assert.Equal(step.StepId, source.RunningStepId);
        Assert.Equal(DeviceStatusTypes.Moving, source.Status);
        Assert.Equal(DeviceStatusTypes.Busy, destination.Status);

        await log.Wait((description, level) =>
                level == Log.Interface.LogEntryType.Warning &&
                description.Contains(source.ItemGuid.ToString()) &&
                description.Contains(rejectedStepId.ToString()) &&
                description.Contains(result.ToString(), StringComparison.OrdinalIgnoreCase))
            .WaitAsync(TimeSpan.FromSeconds(10));
    }

    private (AutomationDevice Source, AutomationDevice Destination, MissionStep Step, PathMission Mission)
        AStartedSingleMovement()
    {
        var source = _plant.Device("S");
        var destination = _plant.Device("D");
        _plant.Link("S", "D");

        source.DeviceConnection = new AcceptingConnection();

        var mission = new PathMission
        {
            PositionItemId = source.ItemGuid,
            DestinationItemId = destination.ItemGuid,
            LuId = 11
        };
        ActiveMissions.Instance.Add(mission, buildRoute: false);

        var step = _plant.PlannedStep(mission, "S", "D");
        source.Status = DeviceStatusTypes.Ready;
        destination.Status = DeviceStatusTypes.Ready;

        source.StartPutToMOVE_LU();

        Assert.Equal(step.StepId, source.RunningStepId);
        Assert.True(destination.IsWaitingForStep(step.StepId));

        return (source, destination, step, mission);
    }

    private sealed class AcceptingConnection : IConnection
    {
        public decimal Id => 1;
        public bool Managed { get; set; }

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
        public void MoveLuFromAsiToAsi(RunAsi sourceItem, DataAsi actionItemPerformer, DataAsi fromAsi,
            DataAsi toAsi, Guid stepGuid, decimal luId, int luHandlingMode = 0) { }
        public void MoveLuFromAsiToAsi(RunAsi sourceItem, DataAsi actionItemPerformer, Guid groupedMovementId,
            List<MovementLuDataCommand> loadUnitsToMove, int luHandlingMode = 0) { }
        public void OpenConnection() { }
        public void RequestLuDataFromAsi(RunAsi item) { }
        public void SendCommandToAsi(RunAsi destinationItem, int commandCode, List<string?> parameters) { }
        public void SendLuDataToAsi(RunAsi item, LuInfoDataCommand value) { }
        public void ShowMessageToAsi(RunAsi destinationItem, string message) { }
    }
}
