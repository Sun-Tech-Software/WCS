using Wcs.Engine.Api;
using Wcs.Engine.Collections;
using Wcs.Engine.Items;
using Wcs.Engine.Persistence;
using Wcs.Wms.Contracts;
using Wcs.Wms.Contracts.Test;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// Lo spostamento di una missione da fuori (<see cref="MissionGateway.Move"/>): la strada che
/// rimpiazza l'assegnazione diretta sulla griglia dell'Editor di prima dell'estrazione.
/// </summary>
/// <remarks>
/// Lo spostamento si chiede a motore fermo (<c>Stop</c>): in <c>Recover</c> e' rifiutato dall'8/10
/// (<see cref="MissionMoveRouteTest.InRecoverTheMoveIsRefusedAndNothingChanges"/>). Fino ad allora la
/// classe stava tutta in Recover, lo stato in cui l'ORM esce prima di scrivere; adesso le scritture
/// della posizione vanno in un database del WCS in memoria, e il gestionale e' quello finto. Le
/// missioni si aggiungono ancora in Recover, perche' <c>ActiveMissions.Add</c> esce subito in Stop,
/// e il lavoro di sfondo della posizione (<c>PathMission.PositionWork</c>) si aspetta prima di
/// chiudere il database.
/// </remarks>
public sealed class MissionGatewayMoveTest : IAsyncLifetime
{
    private readonly NavigatorStatusTypes _previousStatus = Navigator.NavStatus;
    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly List<AutomationDevice> _devices = [];
    private readonly List<PathMission> _missions = [];

    public MissionGatewayMoveTest()
    {
        Navigator.Store = _database;
        Navigator.Warehouse = new FakeWarehouse();
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        ActiveMissions.Instance.RemoveAll(_ => true);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await PendingMissionChanges.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));
        PendingMissionChanges.Reset();
        await Task.WhenAll(_missions.Select(mission => mission.PositionWork));
        ActiveMissions.Instance.RemoveAll(_ => true);
        MissionSteps.Instance.RemoveAll(_ => true);
        RoutesToBuild.Instance.Items = new();
        foreach (var device in _devices) PathDevices.Instance.Remove(device.ItemGuid);
        Navigator.NavStatus = _previousStatus;
        Navigator.Warehouse = _previousWarehouse;
        Navigator.Store = null;
        _database.Dispose();
    }

    private static MissionStep StepFrom(PathMission mission, AutomationDevice from, AutomationDevice to)
    {
        var step = new MissionStep { StepId = Guid.NewGuid(), MissionId = mission.MissionId, FromNodeId = from.ItemGuid, ToNodeId = to.ItemGuid };
        MissionSteps.Instance.Add(step);
        return step;
    }

    private AutomationDevice Device()
    {
        var device = TestDevices.WithRow(Guid.NewGuid(), d => d.DeviceType = DeviceTypes.Automation);
        PathDevices.Instance.Add(device);
        _devices.Add(device);
        return device;
    }

    /// <summary>
    /// Una missione su <paramref name="device"/>, aggiunta in Recover con il primo posizionamento
    /// finito; poi il motore torna fermo, lo stato in cui la prova chiede lo spostamento.
    /// </summary>
    private async Task<PathMission> MissionOn(AutomationDevice device)
    {
        Navigator.NavStatus = NavigatorStatusTypes.Recover;
        var mission = new PathMission { PositionItemId = device.ItemGuid };
        _missions.Add(mission);
        ActiveMissions.Instance.Add(mission, buildRoute: false);
        await mission.PositionWork;
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        return mission;
    }

    [Fact]
    public async Task TheMissionMovesAndTheLoadFollowsIt()
    {
        var from = Device();
        var to = Device();
        var mission = await MissionOn(from);

        Assert.Equal(MissionMoveOutcome.Moved, MissionGateway.Move(mission.MissionId, to.ItemGuid));

        Assert.Equal(to.ItemGuid, mission.PositionItemId);
        Assert.Null(from.LoadedLu);
        Assert.Same(mission, to.LoadedLu);
    }

    [Fact]
    public async Task TheOldRouteIsDiscardedAndTheRebuildIsQueued()
    {
        var from = Device();
        var next = Device();
        var to = Device();
        var mission = await MissionOn(from);
        var step = StepFrom(mission, from, next);

        Assert.Equal(MissionMoveOutcome.Moved, MissionGateway.Move(mission.MissionId, to.ItemGuid));

        // Visto in sala prove l'11/9: senza questo la missione teneva gli step del posto vecchio
        // finche' l'operatore non premeva «esegui passo», che e' cio' che riaccoda la costruzione.
        Assert.DoesNotContain(step, MissionSteps.Instance.List);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
    }

    /// <summary>
    /// A step of the mission is in flight: the move waits for its end (item 54, decision 4), and nothing
    /// is touched meanwhile - the position, the step, its token. It was a refusal until then.
    /// </summary>
    [Fact]
    public async Task AStepInFlightMakesTheMoveWait()
    {
        var from = Device();
        var next = Device();
        var to = Device();
        var mission = await MissionOn(from);
        var step = StepFrom(mission, from, next);
        from.RunningStepId = step.StepId;
        from.Status = DeviceStatusTypes.Moving;

        Assert.Equal(MissionMoveOutcome.Pending, MissionGateway.Move(mission.MissionId, to.ItemGuid));
        await PendingMissionChanges.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(PendingMissionChange.Position(to.ItemGuid), MissionGateway.PendingChangeOf(mission.MissionId));
        Assert.Equal(from.ItemGuid, mission.PositionItemId);
        Assert.Contains(step, MissionSteps.Instance.List);
        Assert.Equal(step.StepId, from.RunningStepId);
        Assert.Null(RoutesToBuild.Instance[mission.MissionId]);
    }

    [Fact]
    public async Task ADeviceThatAlreadyCarriesAnotherMissionRefusesTheMove()
    {
        var from = Device();
        var to = Device();
        var mission = await MissionOn(from);
        var occupant = await MissionOn(to);

        Assert.Equal(MissionMoveOutcome.DeviceOccupied, MissionGateway.Move(mission.MissionId, to.ItemGuid));

        Assert.Equal(from.ItemGuid, mission.PositionItemId);
        Assert.Same(occupant, to.LoadedLu);
    }

    [Fact]
    public async Task AMoveOntoTheSameDeviceIsNotAConflict()
    {
        var device = Device();
        var mission = await MissionOn(device);

        Assert.Equal(MissionMoveOutcome.Moved, MissionGateway.Move(mission.MissionId, device.ItemGuid));
        Assert.Same(mission, device.LoadedLu);
    }

    [Fact]
    public async Task AnUnknownDeviceOrMissionIsReported()
    {
        var device = Device();
        var mission = await MissionOn(device);

        Assert.Equal(MissionMoveOutcome.DeviceNotFound, MissionGateway.Move(mission.MissionId, Guid.NewGuid()));
        Assert.Equal(device.ItemGuid, mission.PositionItemId);
        Assert.Equal(MissionMoveOutcome.MissionNotFound, MissionGateway.Move(Guid.NewGuid(), device.ItemGuid));
    }
}
