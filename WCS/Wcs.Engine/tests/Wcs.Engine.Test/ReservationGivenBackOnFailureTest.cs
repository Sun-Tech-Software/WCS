using Wcs.Connection.Interface;
using Wcs.Connection.Interface.CommandData;
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
/// Il posto sulla destinazione si restituisce anche quando lo step e' sparito nel frattempo.
/// </summary>
/// <remarks>
/// <para>
/// Il <c>catch</c> di <c>StartStoreLu</c> restituiva la prenotazione solo come effetto collaterale
/// dell'azzeramento di <c>RunningStepId</c>: il setter cerca in <c>PathDevices</c> chi tiene il
/// posto per lo step che finisce. Ma il getter di <c>RunningStepId</c> risponde <c>Guid.Empty</c>
/// quando lo step non e' piu' in <c>MissionSteps</c>, e allora il setter esce subito
/// (<c>if (RunningStepId == value) return;</c>) senza cercare nessuno. Se fra la presa e il
/// <c>catch</c> un altro flusso ha chiamato <c>RemoveMissionSteps</c> sulla stessa missione —
/// <c>ResetLuProperties</c> lo fa — il posto restava preso, e una prenotazione appesa rende quel
/// dispositivo inaccettabile per chiunque altro.
/// </para>
/// <para>
/// La sparizione dello step non e' un'attesa: la mette in scena il campo finto, che toglie lo step
/// dalla collezione e poi rifiuta il comando, dentro la stessa chiamata.
/// </para>
/// </remarks>
public sealed class ReservationGivenBackOnFailureTest : IDisposable
{
    private const decimal CellId = 610;
    private const string StorageDoor = "S01A";
    private const decimal LoadUnitId = 42;

    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly FakeWarehouse _warehouse = new();
    private readonly RoutingTestPlant _plant = new(NavigatorStatusTypes.Run);

    public ReservationGivenBackOnFailureTest()
    {
        Navigator.Store = _database;
        Navigator.Warehouse = _warehouse;
    }

    public void Dispose()
    {
        MissionSteps.Instance.RemoveAll(_ => true);
        _plant.Dispose();
        Navigator.Warehouse = _previousWarehouse;
        Navigator.Store = null;
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Il comando al campo fallisce dopo che lo step e' sparito dalla collezione: la destinazione
    /// riavere il proprio posto lo stesso.
    /// </summary>
    [Fact]
    public async Task AStoreThatFailsAfterItsStepVanishedGivesTheReservationBack()
    {
        var (device, destination, step) = await APlantAboutToStore(removeTheStepBeforeFailing: true);

        Assert.NotNull(device.StorageManager);
        await device.StorageManager.StartStoreLu();

        Assert.Equal(Guid.Empty, destination.WaitingStepId);
        Assert.Equal(Guid.Empty, device.RunningStepId);
        Assert.Empty(MissionSteps.Instance.Where(x => x.StepId == step.StepId));
    }

    /// <summary>
    /// Lo stesso guasto con lo step ancora al suo posto: la prenotazione torna, come tornava gia'.
    /// </summary>
    [Fact]
    public async Task AStoreThatFailsGivesTheReservationBack()
    {
        var (device, destination, _) = await APlantAboutToStore(removeTheStepBeforeFailing: false);

        Assert.NotNull(device.StorageManager);
        await device.StorageManager.StartStoreLu();

        Assert.Equal(Guid.Empty, destination.WaitingStepId);
        Assert.Equal(Guid.Empty, device.RunningStepId);
    }

    /// <summary>
    /// La meta' che deve riuscire: senza questa, un deposito che non partisse mai passerebbe i due
    /// collaudi qui sopra. Il comando arriva al campo, e da quel momento gettone e prenotazione
    /// restano presi — c'e' un movimento in volo che li giustifica.
    /// </summary>
    [Fact]
    public async Task AStoreThatStartsKeepsTheTokenAndTheReservation()
    {
        var (device, destination, step) = await APlantAboutToStore(removeTheStepBeforeFailing: false,
            failing: false);

        Assert.NotNull(device.StorageManager);
        await device.StorageManager.StartStoreLu();

        Assert.Equal(step.StepId, device.RunningStepId);
        Assert.Equal(step.StepId, destination.WaitingStepId);
    }

    /// <summary>
    /// <c>RemoveStepsByGroupedMovementId</c> azzera il gettone <b>prima</b> di cancellare, come fa
    /// <c>RemoveStep</c> sedici righe sopra: cosi' il setter trova ancora il valore effettivo e
    /// libera il prenotato. Cancellando prima, il getter risponde gia' <c>Guid.Empty</c>, il
    /// confronto e' falso, l'assegnazione non avviene e la prenotazione resta addosso a nessuno.
    /// </summary>
    [Fact]
    public void RemovingTheStepsOfAGroupedMovementGivesTheReservationBack()
    {
        var source = _plant.Device("S");
        var destination = _plant.Device("D");

        var mission = new PathMission { PositionItemId = source.ItemGuid };
        ActiveMissions.Instance.Add(mission, buildRoute: false);
        Assert.Same(mission, source.LoadedLu);

        // Nel caso di gruppo il gettone del mittente E' l'identificativo del movimento raggruppato:
        // qui lo step ha la stessa chiave, che e' la sola forma in cui il ramo non e' inerte.
        var groupedMovementId = Guid.NewGuid();

        var step = new MissionStep
        {
            StepId = groupedMovementId,
            MissionId = mission.MissionId,
            Date = DateTime.Now,
            FromNodeId = source.ItemGuid,
            ToNodeId = destination.ItemGuid,
            GroupedMovementId = groupedMovementId
        };

        MissionSteps.Instance.Add(step);

        Assert.True(destination.TryReserveForIncomingStep(step.StepId));
        source.RunningStepId = step.StepId;

        source.RemoveStepsByGroupedMovementId(groupedMovementId);

        Assert.Equal(Guid.Empty, destination.WaitingStepId);
        Assert.Equal(Guid.Empty, source.RunningStepId);
        Assert.Empty(MissionSteps.Instance.Where(x => x.StepId == step.StepId));
    }

    private async Task<(AutomationDevice Device, AutomationDevice Destination, MissionStep Step)> APlantAboutToStore(
        bool removeTheStepBeforeFailing, bool failing = true)
    {
        var device = _plant.Device("R");
        var destination = _plant.Device("D");
        _plant.Link("R", "D");

        // Non ConvertDepthToPartition: cosi' VerifyStoreInfo conferma subito la destinazione e il
        // collaudo resta su quello che vuole provare, cioe' il gettone e il posto in errore.
        device.RecordStorageLength = RecordStorageTypes.FromPlc;
        device.DeviceConnection = new FieldDouble(failing, removeTheStepBeforeFailing);

        await PlantPersistence.SaveDoorMappingsAsync(destination.ItemGuid,
            [new DoorMapping { ItemId = destination.ItemGuid, DoorCode = StorageDoor, CellId = CellId, Side = "A" }]);

        _warehouse.Cells[CellId] = new CellDescription(CellId, AreaId: 1, WarehouseId: 1, Depth: 1, Inverted: false,
            CellLockState.None, AllowedLoadUnitTypeId: null, GroupId: null, GroupOrder: 0, StorageDoor,
            DoorCodeB: string.Empty, DoorCount: 1, MultiDepthStorage: false);
        _warehouse.CellPartitionMaps[CellId] = new CellPartitionMap(CellId, "0001", "0002", AsiDoorB: string.Empty,
            SubitemDoorB: string.Empty, [new CellPartition("0003", -1)]);

        var mission = new PathMission
        {
            PositionItemId = device.ItemGuid,
            LuId = LoadUnitId,
            StorageDoorCode = StorageDoor
        };

        ActiveMissions.Instance.Add(mission, buildRoute: false);

        // L'aggiunta alla collezione attiva esce subito se il navigatore non e' in marcia: senza
        // questa verifica il resto proverebbe il contrario di cio' che dice il nome.
        Assert.Same(mission, device.LoadedLu);

        var step = _plant.PlannedStep(mission, "R", "D");
        Assert.Same(step, mission.StepToDo);

        // La destinazione non si scrive sulla missione: il setter di DestinationItemId, con la
        // destinazione che non e' un polmone, azzera StorageDoorCode - e il codice porta e' cio' da
        // cui il deposito ricava l'indirizzo di campo. Chi serve al collaudo e' la destinazione
        // dello step, che e' quella che prende il posto.
        Assert.Equal(destination, mission.StepToDo?.DestinationItem);
        Assert.NotNull(await mission.GetAsiCellInfoAsync(mission.StorageDoorCode));

        return (device, destination, step);
    }

    /// <summary>
    /// Il campo nei test: puo' rifiutare il comando, e puo' far sparire lo step dalla collezione
    /// un istante prima di rifiutarlo — che e' il caso che il primo collaudo mette in scena.
    /// </summary>
    private sealed class FieldDouble(bool failing, bool removeTheStep) : IConnection
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

        public void MoveLuFromAsiToAsi(RunAsi sourceItem, DataAsi actionItemPerformer, DataAsi fromAsi, DataAsi toAsi,
            Guid stepGuid, decimal luId, int luHandlingMode = 0)
        {
            if (removeTheStep) MissionSteps.Instance.RemoveAll(x => x.StepId == stepGuid);
            if (failing) throw new InvalidOperationException("il PLC non ha accettato il deposito");
        }

        public void MoveLuFromAsiToAsi(RunAsi sourceItem, DataAsi actionItemPerformer, Guid groupedMovementId,
            List<MovementLuDataCommand> loadUnitsToMove, int luHandlingMode = 0) { }

        public void OpenConnection() { }
        public void RequestLuDataFromAsi(RunAsi item) { }
        public void SendCommandToAsi(RunAsi destinationItem, int commandCode, List<string?> parameters) { }
        public void SendLuDataToAsi(RunAsi item, LuInfoDataCommand value) { }
        public void ShowMessageToAsi(RunAsi destinationItem, string message) { }
    }
}
