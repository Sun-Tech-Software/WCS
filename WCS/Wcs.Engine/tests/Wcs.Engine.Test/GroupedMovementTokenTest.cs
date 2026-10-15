using Wcs.Connection.Interface;
using Wcs.Connection.Interface.CommandData;
using Wcs.Connection.Interface.ConnectionData;
using Wcs.Connection.Interface.Events;
using Wcs.Engine.Collections;
using Wcs.Engine.Items;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Wcs.Engine.Test.Routing;
using Wcs.Wms.Contracts;
using Wcs.Wms.Contracts.Test;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// Voce D166: il gettone del movimento di gruppo si rileggeva vuoto. <c>ExecuteGroupedMOVE_LU_TO</c>
/// scriveva in <c>RunningStepId</c> un guid inventato, il getter lo scioglieva a <c>Empty</c>
/// perche' nessuno step lo portava come <c>StepId</c>, e cosi' <c>Empty</c> finiva sugli step del
/// gruppo e nel comando al PLC. Al ritorno <c>StepDoneGroup</c>, <c>StepErrorGroup</c> e
/// <c>StepTimeOutGroup</c> selezionavano gli step con <c>GroupedMovementId == Empty</c>: tutti gli
/// step senza gruppo dell'impianto, di cui rilasciavano le destinazioni.
/// </summary>
/// <remarks>
/// La scena e' quella di <see cref="GroupedMoveReservationTest"/> (un gruppo, uno step verso il
/// magazzino e uno verso un dispositivo), con in piu' un <b>estraneo</b>: uno step senza gruppo di
/// un altro mittente, con la destinazione prenotata per lui. Il campo e' una connessione finta che
/// ricorda il gettone ricevuto: e' quello che il PLC rimanda nel completamento.
/// </remarks>
public sealed class GroupedMovementTokenTest : IDisposable
{
    private const decimal CellId = 740;
    private const string StorageDoorCode = "P740A";

    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly RoutingTestPlant _plant = new();
    private readonly FakeWarehouse _warehouse = new();
    private readonly RecordingConnection _field = new();

    public GroupedMovementTokenTest()
    {
        Navigator.Store = _database;
        Navigator.Warehouse = _warehouse;
    }

    public void Dispose()
    {
        _plant.Dispose();
        Navigator.Warehouse = _previousWarehouse;
        Navigator.Store = null;
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    private static CellDescription Cell(decimal cellId, string doorCode) =>
        new(cellId, AreaId: 1, WarehouseId: 1, Depth: 1, Inverted: false, CellLockState.None,
            AllowedLoadUnitTypeId: null, GroupId: null, GroupOrder: 0, doorCode, DoorCodeB: string.Empty,
            DoorCount: 1, MultiDepthStorage: false);

    private static MissionStep Step(PathMission mission, AutomationDevice from, AutomationDevice to)
    {
        var step = new MissionStep
        {
            StepId = Guid.NewGuid(),
            MissionId = mission.MissionId,
            Date = DateTime.Now,
            FromNodeId = from.ItemGuid,
            ToNodeId = to.ItemGuid
        };
        MissionSteps.Instance.Add(step);
        return step;
    }

    /// <summary>
    /// Un gruppo con due step propri pronti a partire, come in
    /// <see cref="GroupedMoveReservationTest"/>: la cella non e' vuota, cosi' la verifica di
    /// allocazione si ferma prima e la sola lettura che resta e' l'indirizzo di campo.
    /// </summary>
    private async Task<(AutomationDevice Group, MissionStep ToStorage, MissionStep ToDestination)> AGroupedMoveAboutToStart()
    {
        var group = _plant.Device("G");
        var storage = _plant.Device("S", DeviceTypes.Storage);
        var destination = _plant.Device("D");

        _plant.Link("G", "S");
        _plant.Link("G", "D");

        group.RecordStorageLength = RecordStorageTypes.FromPlc;
        group.DeviceConnection = _field;

        await PlantPersistence.SaveDoorMappingsAsync(storage.ItemGuid,
            [new DoorMapping { ItemId = storage.ItemGuid, DoorCode = StorageDoorCode, CellId = CellId, Side = "A" }]);

        var storing = new PathMission { LuId = 42, StorageDoorCode = StorageDoorCode, DestinationItemId = storage.ItemGuid };
        var moving = new PathMission { LuId = 43, DestinationItemId = destination.ItemGuid };
        ActiveMissions.Instance.Add(storing, buildRoute: false);
        ActiveMissions.Instance.Add(moving, buildRoute: false);

        var toStorage = Step(storing, group, storage);
        var toDestination = Step(moving, group, destination);

        _warehouse.Cells[CellId] = Cell(CellId, StorageDoorCode);
        _warehouse.CellContents[CellId] = [7];
        _warehouse.CellPartitionMaps[CellId] = new CellPartitionMap(CellId, "0001", "0002",
            AsiDoorB: string.Empty, SubitemDoorB: string.Empty, [new CellPartition("0003", -1)]);

        return (group, toStorage, toDestination);
    }

    /// <summary>
    /// L'estraneo: uno step senza gruppo, di un altro mittente, verso una destinazione che lo
    /// aspetta. E' lo step che i gestori di gruppo non devono toccare.
    /// </summary>
    private (MissionStep Step, AutomationDevice Destination, PathMission Mission) AStrangerStepWithItsDestinationReserved()
    {
        var stranger = _plant.Device("X");
        var strangerDestination = _plant.Device("Y");
        _plant.Link("X", "Y");

        var mission = new PathMission { LuId = 44, PositionItemId = stranger.ItemGuid, DestinationItemId = strangerDestination.ItemGuid };
        ActiveMissions.Instance.Add(mission, buildRoute: false);

        var step = Step(mission, stranger, strangerDestination);
        Assert.True(strangerDestination.TryReserveForIncomingStep(step.StepId));
        Assert.Equal(Guid.Empty, step.GroupedMovementId);

        return (step, strangerDestination, mission);
    }

    /// <summary>
    /// Il gruppo che il campo vede come tale: il gestore di connessione instrada verso i gestori
    /// di gruppo solo i dispositivi di tipo gruppo (o con <c>GroupItemId</c>).
    /// </summary>
    private AutomationDevice AGroupDevice()
    {
        var group = _plant.Device("G", DeviceTypes.GroupOfAutomationDevices);
        group.DeviceConnection = _field;
        return group;
    }

    /// <summary>
    /// Il gettone che parte con il comando non e' vuoto, gli step del gruppo lo portano, e il
    /// dispositivo di gruppo lo rilegge: e' quello che il PLC rimandera' nel completamento.
    /// </summary>
    [Fact]
    public async Task TheTokenSentToThePlcIsNotEmptyAndIsCarriedByTheGroupSteps()
    {
        var (group, toStorage, toDestination) = await AGroupedMoveAboutToStart();

        Assert.NotNull(group.StepExecutor);
        await group.StepExecutor.ExecuteGroupedMOVE_LU_TO([toStorage, toDestination]);

        Assert.NotNull(_field.GroupedMovementId);
        var token = _field.GroupedMovementId!.Value;

        Assert.NotEqual(Guid.Empty, token);
        Assert.Equal(token, toStorage.GroupedMovementId);
        Assert.Equal(token, toDestination.GroupedMovementId);
        Assert.Equal(token, group.RunningStepId);
    }

    /// <summary>
    /// Il completamento del gruppo, con il gettone che il campo rimanda, lavora i due step del
    /// gruppo e lascia stare l'estraneo: la sua destinazione resta prenotata per lui, e lo step
    /// non e' ne' chiuso ne' tolto.
    /// </summary>
    [Fact]
    public async Task CompletingTheGroupWithTheTokenItSentLeavesAStepWithoutGroupAlone()
    {
        var (group, toStorage, toDestination) = await AGroupedMoveAboutToStart();
        var (strangerStep, strangerDestination, _) = AStrangerStepWithItsDestinationReserved();

        Assert.NotNull(group.StepExecutor);
        await group.StepExecutor.ExecuteGroupedMOVE_LU_TO([toStorage, toDestination]);

        Assert.NotNull(_field.GroupedMovementId);
        Assert.NotNull(group.GroupManager);
        await group.GroupManager.StepDoneGroup(_field.GroupedMovementId!.Value, StepTypes.Automatic);

        Assert.DoesNotContain(toStorage, MissionSteps.Instance.List);
        Assert.DoesNotContain(toDestination, MissionSteps.Instance.List);

        Assert.Contains(strangerStep, MissionSteps.Instance.List);
        Assert.NotEqual(ResultCodeTypes.Success, strangerStep.StepStatus);
        Assert.True(strangerDestination.IsWaitingForStep(strangerStep.StepId));
    }

    /// <summary>
    /// <c>Empty</c> non e' il gettone di nessun gruppo: un completamento con quel gettone non
    /// chiude nessuno step, e non tocca l'estraneo.
    /// </summary>
    [Fact]
    public async Task AnEmptyTokenClosesNoStep()
    {
        var group = AGroupDevice();
        var (strangerStep, strangerDestination, _) = AStrangerStepWithItsDestinationReserved();

        Assert.NotNull(group.GroupManager);
        await group.GroupManager.StepDoneGroup(Guid.Empty, StepTypes.Automatic);

        Assert.Contains(strangerStep, MissionSteps.Instance.List);
        Assert.NotEqual(ResultCodeTypes.Success, strangerStep.StepStatus);
        Assert.True(strangerDestination.IsWaitingForStep(strangerStep.StepId));
    }

    /// <summary>
    /// Un errore dal campo con gettone vuoto non marca in errore nessuno step, e non libera la
    /// destinazione dell'estraneo.
    /// </summary>
    [Fact]
    public async Task AnEmptyTokenFromTheFieldMarksNoStepInError()
    {
        var group = AGroupDevice();
        var (strangerStep, strangerDestination, strangerMission) = AStrangerStepWithItsDestinationReserved();

        await group.ConnectionAsiMovementIsCompleted(new AsiMovementIsCompletedEventArgs(
            group.ItemGuid, Guid.Empty, ResultCodeTypes.Error, 15, "Aisle not enabled", null));

        Assert.NotEqual(ResultCodeTypes.Error, strangerStep.StepStatus);
        Assert.NotEqual(MissionStatusTypes.Error, strangerMission.Status);
        Assert.True(strangerDestination.IsWaitingForStep(strangerStep.StepId));
    }

    /// <summary>
    /// Un timeout dal campo con gettone vuoto: come sopra.
    /// </summary>
    [Fact]
    public async Task AnEmptyTokenFromTheFieldTimesOutNoStep()
    {
        var group = AGroupDevice();
        var (strangerStep, strangerDestination, strangerMission) = AStrangerStepWithItsDestinationReserved();

        await group.ConnectionAsiMovementIsCompleted(new AsiMovementIsCompletedEventArgs(
            group.ItemGuid, Guid.Empty, ResultCodeTypes.TimeOut, 0, string.Empty, null));

        Assert.NotEqual(ResultCodeTypes.TimeOut, strangerStep.StepStatus);
        Assert.NotEqual(MissionStatusTypes.Error, strangerMission.Status);
        Assert.True(strangerDestination.IsWaitingForStep(strangerStep.StepId));
    }

    /// <summary>
    /// L'errore 137 passava da un gestore suo prima di <c>StepErrorGroup</c>, e su un gruppo senza
    /// UDC a bordo <b>toglieva</b> gli step selezionati: con il gettone vuoto toglieva dall'impianto
    /// tutti gli step senza gruppo. Dall'8/10 il 137 va dritto a <c>StepErrorGroup</c>, che con il
    /// gettone vuoto non seleziona niente: la prova resta, sulla stessa scena.
    /// </summary>
    [Fact]
    public async Task AnEmptyTokenWithError137RemovesNoStep()
    {
        var group = AGroupDevice();
        var (strangerStep, strangerDestination, _) = AStrangerStepWithItsDestinationReserved();

        await group.ConnectionAsiMovementIsCompleted(new AsiMovementIsCompletedEventArgs(
            group.ItemGuid, Guid.Empty, ResultCodeTypes.Error, 137, "Cell not free", null));

        Assert.Contains(strangerStep, MissionSteps.Instance.List);
        Assert.True(strangerDestination.IsWaitingForStep(strangerStep.StepId));
    }

    /// <summary>
    /// Un gruppo che configura i suoi step ma non costruisce nessun comando (lo step ha una
    /// missione ma nessuna destinazione) esce senza niente in volo, e deve uscire libero: gettone
    /// azzerato, non «impegnato». Con il gettone vivo (voce D166) gli step configurati lo
    /// portano, e senza questo azzeramento il gruppo restava Ready con un gettone che nessun
    /// completamento avrebbe mai sciolto: la rete degli appesi (<c>TryReleaseHungRunningStep</c>)
    /// cerca per <c>StepId</c> e non vede i gettoni di gruppo. Revisione del lotto F.
    /// </summary>
    [Fact]
    public async Task AGroupedMoveThatStartsNoCommandLeavesTheGroupFree()
    {
        var group = _plant.Device("G");
        group.DeviceConnection = _field;

        var mission = new PathMission { LuId = 45 };
        ActiveMissions.Instance.Add(mission, buildRoute: false);

        // Una destinazione che l'impianto non conosce: ConfigureStep scrive il gettone sullo
        // step, CreateMovementCommand non costruisce niente (DestinationItem nullo).
        var step = new MissionStep
        {
            StepId = Guid.NewGuid(),
            MissionId = mission.MissionId,
            Date = DateTime.Now,
            FromNodeId = group.ItemGuid,
            ToNodeId = Guid.NewGuid()
        };
        MissionSteps.Instance.Add(step);

        Assert.NotNull(group.StepExecutor);
        await group.StepExecutor.ExecuteGroupedMOVE_LU_TO([step]);

        Assert.Null(_field.GroupedMovementId);
        Assert.Equal(DeviceStatusTypes.Ready, group.Status);
        Assert.False(group.GroupExecuteStepRunning);

        Assert.Equal(Guid.Empty, group.RunningStepId);
        Assert.False(group.IsBusyWithCommand);
    }

    /// <summary>Il campo che accetta il comando e ricorda il gettone del movimento di gruppo.</summary>
    private sealed class RecordingConnection : IConnection
    {
        public Guid? GroupedMovementId { get; private set; }

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
            List<MovementLuDataCommand> loadUnitsToMove, int luHandlingMode = 0) => GroupedMovementId = groupedMovementId;
        public void OpenConnection() { }
        public void RequestLuDataFromAsi(RunAsi item) { }
        public void SendCommandToAsi(RunAsi destinationItem, int commandCode, List<string?> parameters) { }
        public void SendLuDataToAsi(RunAsi item, LuInfoDataCommand value) { }
        public void ShowMessageToAsi(RunAsi destinationItem, string message) { }
    }
}
