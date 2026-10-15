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
/// Il deposito non parte mentre la destinazione viene riscelta.
/// <para>
/// <c>VerifyStoreInfo</c> scopre che la cella scelta non va piu' bene - occupata da un'estrazione,
/// bloccata al deposito, o senza quota - e fa ricercare la destinazione. Finche' quella ricerca era
/// un <c>Task</c> scartato, <c>StartStoreLu</c> proseguiva: leggeva la terna di destinazione mentre
/// l'altro flusso la riscriveva e spediva al campo un comando il cui identificativo di passo poteva
/// essere gia' stato azzerato. L'unita' di carico si muoveva davvero e il passo non si chiudeva piu'.
/// </para>
/// <para>
/// I due tempi non si discriminano con un'attesa: il gestionale finto si ferma dentro
/// <c>ReserveStorageAsync</c> e riparte quando il test lo dice
/// (<see cref="FakeWarehouse.ReserveStorageAsked"/> e <see cref="FakeWarehouse.ReserveStorageGate"/>).
/// </para>
/// </summary>
public sealed class StoreDestinationRaceTest : IDisposable
{
    private const decimal OldCellId = 7;
    private const decimal NewCellId = 8;
    private const string OldDoorCode = "A7";
    private const string NewDoorCode = "A8";
    private const decimal LoadUnitId = 42;

    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly RoutingTestPlant _plant = new();
    private readonly FakeWarehouse _warehouse = new();

    public StoreDestinationRaceTest()
    {
        Navigator.Store = _database;
        Navigator.Warehouse = _warehouse;

        // Le aree automatiche del magazzino (LocalQueries.GetAutomaticAreasByWarehouseId) le chiede
        // il gestionale finto, che senza una topologia preparata risponde un magazzino senza aree:
        // l'elenco delle aree non e' un ingresso di questo collaudo - la cella nuova arriva
        // comunque da ReserveStorageAsync. Dal compito 10 questa lettura non tocca piu' SQL Server,
        // quindi qui non c'e' piu' nessuna connessione che possa partire per sbaglio.
    }

    public void Dispose()
    {
        MissionSteps.Instance.RemoveAll(_ => true);
        _plant.Dispose();
        Navigator.Warehouse = _previousWarehouse;
        Navigator.Store = null;
        _database.Dispose();
    }

    /// <summary>
    /// Il campo nei test: non parla con nessun PLC, tiene i comandi ricevuti.
    /// </summary>
    private sealed class RecordingConnection : IConnection
    {
        internal List<(DataAsi ToAsi, Guid StepId)> Movements { get; } = [];

        public decimal Id => 1;
        public bool Managed { get; set; }

        // Il motore non li solleva in questo test: servono a soddisfare l'interfaccia.
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
            Guid stepGuid, decimal luId, int luHandlingMode = 0) => Movements.Add((toAsi, stepGuid));

        public void MoveLuFromAsiToAsi(RunAsi sourceItem, DataAsi actionItemPerformer, Guid groupedMovementId,
            List<MovementLuDataCommand> loadUnitsToMove, int luHandlingMode = 0) { }

        public void OpenConnection() { }
        public void RequestLuDataFromAsi(RunAsi item) { }
        public void SendCommandToAsi(RunAsi destinationItem, int commandCode, List<string?> parameters) { }
        public void SendLuDataToAsi(RunAsi item, LuInfoDataCommand value) { }
        public void ShowMessageToAsi(RunAsi destinationItem, string message) { }
    }

    private static CellDescription Cell(decimal cellId, string doorCode) =>
        new(cellId, AreaId: 1, WarehouseId: 1, Depth: 1, Inverted: false, CellLockState.None,
            AllowedLoadUnitTypeId: null, GroupId: null, GroupOrder: 0, doorCode, DoorCodeB: string.Empty,
            DoorCount: 1, MultiDepthStorage: false);

    private static CellPartitionMap PartitionMap(decimal cellId, string asi, string subitem, string partition) =>
        new(cellId, asi, subitem, AsiDoorB: string.Empty, SubitemDoorB: string.Empty,
            [new CellPartition(partition, 0)]);

    /// <summary>
    /// Un ricollocatore che serve due celle di deposito: la vecchia, che la missione ha in mano, e la
    /// nuova, che il gestionale rispondera' alla ricerca.
    /// </summary>
    private async Task<(AutomationDevice Relocator, PathMission Mission, RecordingConnection Connection)> APlantAboutToStore()
    {
        var relocator = _plant.Device("R");
        var oldStorage = _plant.Device("S7", DeviceTypes.Storage);
        var newStorage = _plant.Device("S8", DeviceTypes.Storage);

        _plant.Link("R", "S7");
        _plant.Link("R", "S8");

        oldStorage.RelocateItemId = relocator.ItemGuid;
        newStorage.RelocateItemId = relocator.ItemGuid;

        var connection = new RecordingConnection();

        relocator.RecordStorageLength = RecordStorageTypes.ConvertDepthToPartition;
        relocator.LinkedCellId = OldCellId;
        relocator.DeviceConnection = connection;

        await PlantPersistence.SaveDoorMappingsAsync(oldStorage.ItemGuid,
            [new DoorMapping { ItemId = oldStorage.ItemGuid, DoorCode = OldDoorCode, CellId = OldCellId, Side = "A" }]);
        await PlantPersistence.SaveDoorMappingsAsync(newStorage.ItemGuid,
            [new DoorMapping { ItemId = newStorage.ItemGuid, DoorCode = NewDoorCode, CellId = NewCellId, Side = "A" }]);

        var mission = new PathMission
        {
            PositionItemId = relocator.ItemGuid,
            LuId = LoadUnitId,
            StorageDoorCode = OldDoorCode,
            DestinationItemId = oldStorage.ItemGuid,
            DestinationAsi = "0001",
            DestinationSubitem = "0002",
            DestinationPartition = "0003"
        };

        ActiveMissions.Instance.Add(mission, buildRoute: false);

        // L'aggiunta alla collezione attiva esce subito se il navigatore non e' in marcia: senza
        // questa verifica il resto del test proverebbe il contrario di cio' che dice il nome.
        Assert.Same(mission, relocator.LoadedLu);

        MissionSteps.Instance.Add(new MissionStep
        {
            StepId = Guid.NewGuid(),
            MissionId = mission.MissionId,
            Date = DateTime.Now,
            FromNodeId = relocator.ItemGuid,
            ToNodeId = oldStorage.ItemGuid
        });

        Assert.NotNull(mission.StepToDo);

        _warehouse.Cells[OldCellId] = Cell(OldCellId, OldDoorCode);
        _warehouse.Cells[NewCellId] = Cell(NewCellId, NewDoorCode);
        _warehouse.CellPartitionMaps[OldCellId] = PartitionMap(OldCellId, "0001", "0002", "0003");
        _warehouse.CellPartitionMaps[NewCellId] = PartitionMap(NewCellId, "0011", "0012", "0013");

        // E' il ramo che fa ricercare la destinazione: la cella vecchia e' impegnata da un'estrazione.
        _warehouse.CellBusyStates[OldCellId] = new CellBusyState(Extraction: true, PickUp: false, Storing: false);
        _warehouse.CellStoringConstraintsByCell[OldCellId] = new CellStoringConstraints(150);
        _warehouse.CellStoringConstraintsByCell[NewCellId] = new CellStoringConstraints(150);

        _warehouse.StorageReservations[LoadUnitId] = new StorageReservation(mission.MissionId,
            StorageReservationOutcome.CellFound, NewCellId, NewDoorCode, Partition: "0013", StorageDepth: 150,
            NeedsCompaction: false);

        return (relocator, mission, connection);
    }

    /// <summary>
    /// Fa partire il deposito e lascia la ricerca ferma dentro il gestionale finche' non e' arrivata
    /// davvero: e' la finestra in cui il codice rotto spedisce il comando.
    /// </summary>
    private async Task StoreWhileTheSearchIsPending(AutomationDevice relocator)
    {
        _warehouse.ReserveStorageGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.NotNull(relocator.StorageManager);
        var storing = relocator.StorageManager.StartStoreLu();

        // Il tetto di tempo non discrimina nulla: fa fallire invece di appendere il collaudo se la
        // ricerca non arriva mai al gestionale.
        await _warehouse.ReserveStorageAsked.WaitAsync(TimeSpan.FromSeconds(30));

        _warehouse.ReserveStorageGate.SetResult();

        await storing;
    }

    /// <summary>
    /// Il comando al campo non parte finche' la destinazione non e' decisa: chi ha scoperto che la
    /// cella non va piu' bene rinuncia al giro, non deposita in quella vecchia.
    /// </summary>
    [Fact]
    public async Task NoMovementIsSentWhileTheDestinationIsBeingChosen()
    {
        var (relocator, mission, connection) = await APlantAboutToStore();

        await StoreWhileTheSearchIsPending(relocator);

        Assert.Empty(connection.Movements);

        // La ricerca e' finita prima che StartStoreLu ritornasse: la destinazione e' quella nuova e
        // il dispositivo non ha nessun comando in volo.
        Assert.Equal(NewDoorCode, mission.StorageDoorCode);
        Assert.Equal(Guid.Empty, relocator.RunningStepId);
    }

    /// <summary>
    /// L'altra meta' della regola: quando la cella va ancora bene il deposito parte come prima, con
    /// l'indirizzo di quella cella e l'identificativo del passo da eseguire. Senza questo, un
    /// «non parte mai» passerebbe il collaudo qui sopra.
    /// </summary>
    [Fact]
    public async Task TheStoreIsSentWhenTheCellIsStillGood()
    {
        var (relocator, mission, connection) = await APlantAboutToStore();

        _warehouse.CellBusyStates[OldCellId] = new CellBusyState(Extraction: false, PickUp: false, Storing: false);

        var stepToDo = mission.StepToDo;
        Assert.NotNull(stepToDo);

        Assert.NotNull(relocator.StorageManager);
        await relocator.StorageManager.StartStoreLu();

        var movement = Assert.Single(connection.Movements);

        Assert.Equal(("0001", "0002", "0003"),
            (movement.ToAsi.Asi, movement.ToAsi.Subitem, movement.ToAsi.Partition));
        Assert.Equal(stepToDo.StepId, movement.StepId);
        Assert.DoesNotContain(nameof(IWarehouse.ReserveStorageAsync), _warehouse.Calls);
    }

    /// <summary>
    /// La cella vecchia si blocca al deposito perche' la ricerca non la riproponga, e si sblocca
    /// quando la ricerca e' finita. Non e' «come la variante di gruppo»: quella sblocca fuori da un
    /// <c>finally</c>, quindi un'eccezione le lascia le porte bloccate, e blocca senza guardare se
    /// il blocco c'era gia' (voce D123 del registro in <c>WCS/doc/debito-tecnico.md</c>).
    /// </summary>
    [Fact]
    public async Task TheOldCellIsNotLeftLockedForStoring()
    {
        var (relocator, _, _) = await APlantAboutToStore();

        _warehouse.ReserveStorageGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.NotNull(relocator.StorageManager);
        var storing = relocator.StorageManager.StartStoreLu();

        await _warehouse.ReserveStorageAsked.WaitAsync(TimeSpan.FromSeconds(30));

        // Mentre la ricerca e' in corso il blocco c'e' davvero: senza questa verifica il controllo
        // qui sotto passerebbe anche se nessuno avesse mai bloccato la cella.
        Assert.True(await ORM.DbInfo.GetCellLockedForStoring(OldCellId));

        _warehouse.ReserveStorageGate.SetResult();

        await storing;

        Assert.False(await ORM.DbInfo.GetCellLockedForStoring(OldCellId));
    }

    /// <summary>
    /// Una cella gia' bloccata al deposito da un altro percorso resta bloccata: la ricerca toglie
    /// solo il blocco che ha messo lei.
    /// <para>
    /// Il flag non ha un proprietario - e' un booleano sulle righe della porta - e il secondo
    /// scrittore ha un'intenzione diversa: <c>PathMission.AllocateOnStorage</c>, sotto l'opzione
    /// d'impianto <c>LockCellOnStoringError</c>, mette la cella <b>in quarantena</b> perche' la
    /// quota di deposito rilevata supera la profondita' della cella. E' un fatto fisico, e riguarda
    /// la porta della cella che questa stessa missione ha in mano: se poi la missione arriva alla
    /// ricerca di un'altra cella, uno sblocco incondizionato rimetterebbe in servizio una cella
    /// messa fuori servizio, in silenzio.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ACellLockedByAnotherPathStaysLocked()
    {
        var (relocator, _, _) = await APlantAboutToStore();

        // La quarantena, messa prima che la ricerca cominci.
        await ORM.DbInfo.SetCellLockedForStoring(OldDoorCode);
        Assert.True(await ORM.DbInfo.GetCellLockedForStoring(OldCellId));

        Assert.NotNull(relocator.StorageManager);
        await relocator.StorageManager.StartStoreLu();

        // La ricerca e' davvero avvenuta: senza questo, il controllo qui sotto passerebbe anche se
        // FindNewStoreLocation non fosse mai partita.
        Assert.Contains(nameof(IWarehouse.ReserveStorageAsync), _warehouse.Calls);

        Assert.True(await ORM.DbInfo.GetCellLockedForStoring(OldCellId));
    }

    /// <summary>
    /// Il magazzino e' pieno: la ricerca non trova nessuna cella. Era il ramo che nessun collaudo
    /// esercitava.
    /// </summary>
    private void AFullWarehouse(PathMission mission) =>
        _warehouse.StorageReservations[LoadUnitId] = new StorageReservation(mission.MissionId,
            StorageReservationOutcome.NoFreeCells, CellId: 0, DoorCode: string.Empty, Partition: null,
            StorageDepth: 0, NeedsCompaction: false);

    /// <summary>
    /// Quando non c'e' posto il deposito non parte e la missione tiene la cella vecchia: e' il ramo
    /// «la ricerca non ha trovato niente», che nessun collaudo copriva.
    /// </summary>
    [Fact]
    public async Task NothingIsSentWhenNoCellIsAvailable()
    {
        var (relocator, mission, connection) = await APlantAboutToStore();

        AFullWarehouse(mission);

        Assert.NotNull(relocator.StorageManager);
        await relocator.StorageManager.StartStoreLu();

        // La ricerca c'e' stata davvero, e non ha prodotto una destinazione nuova.
        Assert.Contains(nameof(IWarehouse.ReserveStorageAsync), _warehouse.Calls);
        Assert.Equal(OldDoorCode, mission.StorageDoorCode);

        Assert.Empty(connection.Movements);
        Assert.Equal(Guid.Empty, relocator.RunningStepId);

        // Il blocco di comodo non resta addosso alla cella vecchia nemmeno su questo percorso.
        Assert.False(await ORM.DbInfo.GetCellLockedForStoring(OldCellId));
    }

    /// <summary>
    /// La ricerca che non ha trovato niente non si ripete al giro dopo. Il richiamo ordinario del
    /// dispositivo ripassa ogni due secondi, e senza freno ogni passata rifaceva tre letture del
    /// confine, il blocco, il rilascio della prenotazione, la riprenotazione e lo sblocco -
    /// indefinitamente, perche' magazzino pieno e' una condizione ordinaria e non un guasto.
    /// </summary>
    [Fact]
    public async Task TheSearchIsNotRepeatedOnTheNextTurn()
    {
        var (relocator, mission, _) = await APlantAboutToStore();

        AFullWarehouse(mission);

        Assert.NotNull(relocator.StorageManager);
        await relocator.StorageManager.StartStoreLu();

        var callsAfterTheFirstTurn = _warehouse.Calls.Count;
        Assert.Equal(1, _warehouse.Calls.Count(x => x == nameof(IWarehouse.ReserveStorageAsync)));

        await relocator.StorageManager.StartStoreLu();

        // Nessuna chiamata al confine nel secondo giro: ne' la ricerca, ne' le letture che la
        // precedono.
        Assert.Equal(callsAfterTheFirstTurn, _warehouse.Calls.Count);
    }

    /// <summary>
    /// Il freno e' un'attesa, non una rinuncia: passata l'attesa la ricerca riparte. Senza questo,
    /// un «non riprova mai piu'» passerebbe il collaudo qui sopra.
    /// </summary>
    [Fact]
    public async Task TheSearchStartsAgainOnceTheWaitIsOver()
    {
        var (relocator, mission, _) = await APlantAboutToStore();

        AFullWarehouse(mission);

        var clock = new ManualClock();
        Assert.NotNull(relocator.StorageManager);
        relocator.StorageManager.StoreSearchRetries = new RetryBackoff(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30), clock);

        await relocator.StorageManager.StartStoreLu();
        await relocator.StorageManager.StartStoreLu();

        Assert.Equal(1, _warehouse.Calls.Count(x => x == nameof(IWarehouse.ReserveStorageAsync)));

        clock.Advance(TimeSpan.FromSeconds(2));

        await relocator.StorageManager.StartStoreLu();

        Assert.Equal(2, _warehouse.Calls.Count(x => x == nameof(IWarehouse.ReserveStorageAsync)));
    }

    /// <summary>
    /// Trovata una cella, il freno si dimentica: la missione dopo ricomincia dall'attesa piu' corta
    /// invece di ereditare i tentativi di quella di prima.
    /// </summary>
    [Fact]
    public async Task TheBrakeIsReleasedWhenACellIsFound()
    {
        var (relocator, mission, _) = await APlantAboutToStore();

        AFullWarehouse(mission);

        Assert.NotNull(relocator.StorageManager);
        await relocator.StorageManager.StartStoreLu();

        Assert.True(relocator.StorageManager.StoreSearchRetries.IsWaiting(mission.MissionId));

        // Il magazzino torna ad avere posto.
        _warehouse.StorageReservations[LoadUnitId] = new StorageReservation(mission.MissionId,
            StorageReservationOutcome.CellFound, NewCellId, NewDoorCode, Partition: "0013", StorageDepth: 150,
            NeedsCompaction: false);

        relocator.StorageManager.StoreSearchRetries.Forget(mission.MissionId);

        await relocator.StorageManager.StartStoreLu();

        Assert.Equal(NewDoorCode, mission.StorageDoorCode);
        Assert.False(relocator.StorageManager.StoreSearchRetries.IsWaiting(mission.MissionId));
    }
}
