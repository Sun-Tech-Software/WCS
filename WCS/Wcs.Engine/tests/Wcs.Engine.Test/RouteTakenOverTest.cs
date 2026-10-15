using System.Reflection;
using Wcs.Engine.Collections;
using Wcs.Engine.Items;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Wcs.Engine.Test.Routing;
using Wcs.Wms.Contracts;
using Wcs.Wms.Contracts.Test;
using Xunit;
using static Wcs.Common.Enums;
using RouteBuildStatusTypes = Wcs.Engine.Items.AutomationDevice.RouteBuildStatusTypes;

namespace Wcs.Engine.Test;

/// <summary>
/// Chi chiama <see cref="PathMission.BuildPathToDestination"/> e poi guarda gli step non legge una
/// rotta persa nel confronto della versione (voce 41, disegno C) come «nessun percorso»: la missione e'
/// gia' in coda al giro delle rotte con lo stato di chi ha vinto, e non e' un fallimento (decisione 1
/// del proprietario del 10/10). Rilievo 1 del secondo giro del compito 3: la ricerca della cella del
/// navigatore mandava allo scarto un'UDC con la cella appena prenotata, e la rotta dritta o verso la
/// destinazione fissa contava un fallimento nel freno. Dalla risposta 6 del proprietario dell'11/10 la
/// ricerca della cella che perde restituisce pero' la cella presa, ne toglie la porta e conta il fallimento
/// nel freno: una missione che perde sempre non rifa' la ricerca, con la sua domanda al gestionale, a
/// ogni giro.
/// </summary>
/// <remarks>
/// <para>
/// Il navigatore sta in <c>Stop</c>; le missioni si aggiungono in Recover, perche'
/// <c>ActiveMissions.Add</c> in Stop esce subito, come in <see cref="OperatorDestinationTest"/>. Mai
/// in Recover durante la prova: il setter della destinazione salta il suo blocco e
/// <c>PathBuilder</c> i filtri.
/// </para>
/// <para>
/// Nessuna attesa a tempo: la costruzione si ferma nella cucitura
/// (<see cref="PathMission.RouteBuildPauseForTesting"/>) con la rotta calcolata e non scritta, l'altro
/// scrittore cambia la destinazione senza scrivere una rotta (come il ciclo del gruppo che scrive la
/// porta e la destinazione del suo selettore e aspetta poi le porte dal database), e la prova lascia
/// andare la costruzione. I tetti di 30 s fanno fallire invece di appendere.
/// </para>
/// </remarks>
public sealed class RouteTakenOverTest : IDisposable
{
    private const decimal LinkedCellId = 7;
    private const decimal FoundCellId = 8;
    private const string FoundDoorCode = "A8";
    private const decimal LoadUnitId = 42;
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);

    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly QueuedDatabaseWriter<WcsWrite>? _previousWriter = Navigator.StateWriter;
    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly RoutingTestPlant _plant = new();
    private readonly FakeWarehouse _warehouse = new();
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private PathMission? _mission;

    public RouteTakenOverTest()
    {
        Navigator.Store = _database;
        Navigator.StateWriter = null;
        Navigator.Warehouse = _warehouse;
        RoutesToBuild.Instance.Items = new();
    }

    public void Dispose()
    {
        _released.TrySetResult();
        Api.MissionGateway.WaitingForThePickUpHandOffForTesting = null;
        if (_mission != null) Navigator.ForgetRouteRetries(_mission.MissionId);
        RoutesToBuild.Instance.Items = new();
        _plant.Dispose();
        Navigator.Warehouse = _previousWarehouse;
        Navigator.StateWriter = _previousWriter;
        Navigator.Store = null;
        _database.Dispose();
    }

    /// <summary>
    /// La ricerca della cella del navigatore (P in <c>BuildRouteToStore</c>, una cella qualunque) prenota
    /// la cella 8 di S8, scrive porta A8 e destinazione S8 e calcola la rotta; prima che la scriva un
    /// altro manda la missione verso X. La rotta non si scrive, e la ricerca lo dice: la missione e' gia'
    /// in coda al giro delle rotte. Niente scarto, niente causa di una cella mancante. Chi ha perso
    /// restituisce la cella 8 al gestionale, toglie la porta A8 che aveva scritto e conta il fallimento nel
    /// freno (risposta 6 del proprietario dell'11/10). Prima la ricerca leggeva la rotta mancante come
    /// «nessun dispositivo sulla porta» e mandava l'UDC al punto di scarto R, con la cella 8 ancora
    /// prenotata; e dopo il compito 3 della voce 41 teneva la cella e la porta, senza contare niente.
    /// </summary>
    [Fact]
    public async Task AStoreSearchWhoseRouteIsTakenOverGivesBackItsCellAndIsNotRejected()
    {
        await APositionThatStoresInAnyCellWithARejectionPoint();
        var mission = await MissionOn("P");
        _warehouse.StorageReservations[mission.LuId] = new StorageReservation(mission.MissionId,
            StorageReservationOutcome.CellFound, FoundCellId, FoundDoorCode, Partition: "0013", StorageDepth: 150,
            NeedsCompaction: false);
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        mission.RouteBuildPauseForTesting = Pause;

        var search = Navigator.FindWhereStoreLu(mission, _plant["P"], LookWhereAllocateLuTypes.BuildRouteToStore);
        await _reached.Task.WaitAsync(Ceiling);
        Assert.Equal(_plant["S8"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.UnlessChosenByOperator(() => mission.DestinationItemId = _plant["X"].ItemGuid));
        _released.TrySetResult();
        var outcome = await search.WaitAsync(Ceiling);

        Assert.Equal(RouteBuildStatusTypes.TakenOver, outcome);
        Assert.Equal(_plant["X"].ItemGuid, mission.DestinationItemId);
        Assert.Equal(MissionTypes.MovingToItem, mission.MissionType);
        Assert.Empty(mission.MissionSteps);
        Assert.NotEqual("No item linked to storage door cell", mission.StatusDescription);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
        Assert.False(_warehouse.StorageReservationsByMission.ContainsKey(mission.MissionId));
        Assert.Equal([mission.MissionId], _warehouse.ReleasedStorageReservations);
        Assert.True(string.IsNullOrEmpty(mission.StorageDoorCode));
        Assert.False(mission.StorageCellReserved);
        Assert.True(string.IsNullOrEmpty(mission.DestinationAsi));
        Assert.Equal(1, Navigator.RoutePathRetries.Failures(mission.MissionId));
    }

    /// <summary>
    /// Item 54, first fix round of task 1: the cell search of an entry point (P in <c>BuildRouteToStore</c>)
    /// for an extraction already picked up (out of its cell, on P) takes cell 8 of S8 and writes door A8;
    /// the rebuild makes the extraction a deposit only after the hand-off of the pick-up, and while it
    /// waits another sends the mission towards X. Nothing is converted and no route is written: the
    /// mission is already back in the route loop, and the search says so as for a route taken over
    /// (it gives back cell 8 and its door and counts the failure in the brake). Before the fix the
    /// rebuild answered «nothing built» after queuing the mission, and the search read it as «no
    /// device on the door»: Found false, the load unit sent to the rejection point R, with the mission
    /// queued in the route loop as well and the brake not counted. The same answer comes from the
    /// hand-off that does not let go in its thirty seconds (the other branch of the same return).
    /// </summary>
    [Fact]
    public async Task AStoreSearchWhoseExtractionChangesWhileWaitingForTheHandOffIsAlreadyQueued()
    {
        await APositionThatStoresInAnyCellWithARejectionPoint();
        var mission = await MissionOn("P");
        mission.MissionType = MissionTypes.Extraction;
        mission.SourceItemId = _plant["S8"].ItemGuid;
        mission.PickUpDoorCode = "A9";
        Assert.False(mission.LoadUnitIsInItsStorageCell());
        _warehouse.StorageReservations[mission.LuId] = new StorageReservation(mission.MissionId,
            StorageReservationOutcome.CellFound, FoundCellId, FoundDoorCode, Partition: "0013", StorageDepth: 150,
            NeedsCompaction: false);
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        var waited = 0;
        Api.MissionGateway.WaitingForThePickUpHandOffForTesting = waiting =>
        {
            if (!ReferenceEquals(waiting, mission)) return;
            waited++;
            Assert.Equal(FoundDoorCode, mission.StorageDoorCode);
            Assert.True(mission.UnlessChosenByOperator(() => mission.DestinationItemId = _plant["X"].ItemGuid));
        };

        var outcome = await Navigator.FindWhereStoreLu(mission, _plant["P"], LookWhereAllocateLuTypes.BuildRouteToStore)
            .WaitAsync(Ceiling);

        Assert.Equal(1, waited);
        Assert.Equal(RouteBuildStatusTypes.TakenOver, outcome);
        Assert.Equal(_plant["X"].ItemGuid, mission.DestinationItemId);
        Assert.NotEqual(MissionTypes.Storing, mission.MissionType);
        Assert.NotEqual(MissionTypes.Rejection, mission.MissionType);
        Assert.Empty(mission.MissionSteps);
        Assert.NotEqual("No item linked to storage door cell", mission.StatusDescription);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
        Assert.Equal([mission.MissionId], _warehouse.ReleasedStorageReservations);
        Assert.True(string.IsNullOrEmpty(mission.StorageDoorCode));
        Assert.Equal(1, Navigator.RoutePathRetries.Failures(mission.MissionId));
    }

    /// <summary>
    /// La stessa ricerca, ma chi vince e' uno spostamento: l'UDC passa su X e la destinazione resta S8.
    /// Chi ha perso toglie anche la porta A8, l'indirizzo e il segno della cella sua, che nessun altro ha
    /// toccato, e restituisce la cella 8 (risposta 6 del proprietario dell'11/10): la missione va verso S8
    /// senza una cella, e il giro gliene cerca una. Prima restava verso S8 con la porta A8 della cella
    /// presa da chi aveva perso.
    /// </summary>
    [Fact]
    public async Task AStoreSearchLostToAMoveGivesBackItsCellAndItsDoor()
    {
        await APositionThatStoresInAnyCellWithARejectionPoint();
        var mission = await MissionOn("P");
        _warehouse.StorageReservations[mission.LuId] = new StorageReservation(mission.MissionId,
            StorageReservationOutcome.CellFound, FoundCellId, FoundDoorCode, Partition: "0013", StorageDepth: 150,
            NeedsCompaction: false);
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        mission.RouteBuildPauseForTesting = Pause;

        var search = Navigator.FindWhereStoreLu(mission, _plant["P"], LookWhereAllocateLuTypes.BuildRouteToStore);
        await _reached.Task.WaitAsync(Ceiling);
        Assert.Equal(FoundDoorCode, mission.StorageDoorCode);
        Assert.Equal(PathMission.RouteWriteOutcome.Written, mission.MoveTo(_plant["X"].ItemGuid));
        _released.TrySetResult();
        var outcome = await search.WaitAsync(Ceiling);

        Assert.Equal(RouteBuildStatusTypes.TakenOver, outcome);
        Assert.Equal(_plant["X"].ItemGuid, mission.PositionItemId);
        Assert.Equal(_plant["S8"].ItemGuid, mission.DestinationItemId);
        Assert.True(string.IsNullOrEmpty(mission.StorageDoorCode));
        Assert.False(mission.StorageCellReserved);
        Assert.True(string.IsNullOrEmpty(mission.DestinationAsi));
        Assert.Equal([mission.MissionId], _warehouse.ReleasedStorageReservations);
        Assert.Equal(1, Navigator.RoutePathRetries.Failures(mission.MissionId));
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
    }

    /// <summary>
    /// Due ricerche della stessa missione corrono insieme (quella del giro delle rotte e quella di un
    /// dispositivo: si contano, non si escludono) e tengono la stessa cella 8, con la stessa porta A8. La
    /// prima si ferma con la rotta calcolata; la seconda scrive A8 e la sua rotta P->Q->S8, e vince. Chi
    /// ha perso non distingue la porta dalla sua: la rotta di chi ha vinto va alla porta, e porta, segno,
    /// indirizzo e cella restano; il fallimento si conta lo stesso. Prima chi perdeva toglieva A8 e
    /// restituiva la cella 8, e la missione restava con la rotta verso S8 senza porta e senza cella (primo
    /// giro di correzioni del compito 8 della voce 41).
    /// </summary>
    [Fact]
    public async Task AStoreSearchLostToARouteThroughTheSameDoorKeepsTheDoorAndTheCell()
    {
        await APositionThatStoresInAnyCellWithARejectionPoint();
        var mission = await MissionOn("P");
        _warehouse.StorageReservations[mission.LuId] = new StorageReservation(mission.MissionId,
            StorageReservationOutcome.CellFound, FoundCellId, FoundDoorCode, Partition: "0013", StorageDepth: 150,
            NeedsCompaction: false);
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        mission.RouteBuildPauseForTesting = Pause;

        var search = Navigator.FindWhereStoreLu(mission, _plant["P"], LookWhereAllocateLuTypes.BuildRouteToStore);
        await _reached.Task.WaitAsync(Ceiling);
        Assert.Equal(FoundDoorCode, mission.StorageDoorCode);
        mission.RouteBuildPauseForTesting = null;
        Assert.False(await mission.BuildPathToDestination().WaitAsync(Ceiling));
        Assert.Contains(mission.MissionSteps, step => step.ToNodeId == _plant["S8"].ItemGuid);
        _released.TrySetResult();
        var outcome = await search.WaitAsync(Ceiling);

        Assert.Equal(RouteBuildStatusTypes.TakenOver, outcome);
        Assert.Equal(FoundDoorCode, mission.StorageDoorCode);
        Assert.True(mission.StorageCellReserved);
        Assert.False(mission.StorageCellLost);
        Assert.Equal("0013", mission.DestinationPartition);
        Assert.Equal(_plant["S8"].ItemGuid, mission.DestinationItemId);
        Assert.Contains(mission.MissionSteps, step => step.ToNodeId == _plant["S8"].ItemGuid);
        Assert.True(_warehouse.StorageReservationsByMission.ContainsKey(mission.MissionId));
        Assert.Empty(_warehouse.ReleasedStorageReservations);
        Assert.Equal(1, Navigator.RoutePathRetries.Failures(mission.MissionId));
    }

    /// <summary>
    /// Il rigetto dal campo della missione su P trova lo step P->X in volo e si rimanda; lo step finisce
    /// (qui senza muovere l'UDC), e la ricerca della cella di P prende la cella 8, scrive A8 e calcola la
    /// rotta P->Q->S8. La rotta non si scrive, perche' la missione aspetta il rigetto che il giro rifa', e
    /// la cella torna al gestionale con la sua porta; ma nessuno ha perso niente: il freno non conta, il
    /// giro non salta la missione per l'attesa, e la riga dice il perche' vero. Poi il giro rifa' il
    /// rigetto verso R. Fino al secondo giro di correzioni del compito 8 della voce 41 la guardia
    /// rispondeva come chi ha perso il confronto della versione: la ricerca contava un fallimento nel freno,
    /// il giro saltava la missione per l'attesa, rimandando proprio il rigetto, e la riga diceva che la
    /// rotta l'aveva cambiata un altro.
    /// </summary>
    [Fact]
    public async Task AStoreSearchWhileThePostponedFieldRejectionWaitsCountsNoFailure()
    {
        await APositionThatStoresInAnyCellWithARejectionPoint();
        _plant["P"].RejectItemDestinationIdGetLuInfo = _plant["R"].ItemGuid;
        var mission = await MissionOn("P");
        _warehouse.StorageReservations[mission.LuId] = new StorageReservation(mission.MissionId,
            StorageReservationOutcome.CellFound, FoundCellId, FoundDoorCode, Partition: "0013", StorageDepth: 150,
            NeedsCompaction: false);
        Navigator.NavStatus = NavigatorStatusTypes.Stop;

        var stepPx = _plant.PlannedStep(mission, "P", "X");
        _plant["P"].RunningStepId = stepPx.StepId;
        Assert.False(await mission.RejectTo(PathMission.RejectionTypes.NoLuDataFromPlc).WaitAsync(Ceiling));
        _plant["P"].RunningStepId = Guid.Empty;
        MissionSteps.Instance.Remove(stepPx);
        Assert.Null(mission.StepInFlight());
        RoutesToBuild.Instance.Remove(mission.MissionId);

        // Le righe della ricerca sono informazioni: si scrivono con la diagnostica accesa, e la console
        // le riceve prima che la chiamata torni (RecordingConsole).
        var previousDebugLog = Navigator.ActivateDebugLog;
        Navigator.ActivateDebugLog = true;
        RouteBuildStatusTypes outcome;
        IReadOnlyList<string> lines;
        var console = new RecordingConsole();

        try
        {
            outcome = await Navigator.FindWhereStoreLu(mission, _plant["P"], LookWhereAllocateLuTypes.BuildRouteToStore)
                .WaitAsync(Ceiling);
        }
        finally
        {
            lines = console.Lines;
            console.Dispose();
            Navigator.ActivateDebugLog = previousDebugLog;
        }

        Assert.Equal(RouteBuildStatusTypes.TakenOver, outcome);
        Assert.Empty(mission.MissionSteps);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
        Assert.Equal([mission.MissionId], _warehouse.ReleasedStorageReservations);
        Assert.True(string.IsNullOrEmpty(mission.StorageDoorCode));
        Assert.Equal(0, Navigator.RoutePathRetries.Failures(mission.MissionId));
        Assert.False(Navigator.RoutePathRetries.IsWaiting(mission.MissionId));
        var ours = lines.Where(line => line.Contains(mission.MissionId.ToString())).ToList();
        Assert.Contains(ours, line => line.Contains("la missione aspetta il rigetto dal campo") &&
                                      line.Contains("la cella torna al gestionale"));
        Assert.DoesNotContain(ours, line => line.Contains("l'ha cambiata un altro"));

        StartCellSearch(mission, _plant["P"]);
        await (Navigator.CellSearchInFlight(mission.MissionId) ?? Task.CompletedTask).WaitAsync(Ceiling);

        Assert.Equal(MissionTypes.Rejection, mission.MissionType);
        Assert.Equal(_plant["R"].ItemGuid, mission.DestinationItemId);
        Assert.Contains(mission.MissionSteps, step => step.ToNodeId == _plant["R"].ItemGuid);
    }

    /// <summary>
    /// La ricerca perde contro lo spostamento su X, che lascia la destinazione S8: chi ha perso toglie la
    /// porta A8 e restituisce la cella 8, e la missione ha perso la cella in S8 (StorageCellLost). Una rotta
    /// costruita dopo (come quella che lo spostamento costruisce subito, X->Q->S8) non si scrive: senza
    /// porta e senza cella non si va nel magazzino nudo, e un'altra cella la prenota il navigatore. Prima la
    /// rotta X->Q->S8 si scriveva, e l'UDC andava in S8 senza un posto (primo giro di correzioni del compito
    /// 8 della voce 41).
    /// </summary>
    [Fact]
    public async Task AStoreSearchLostToAMoveLeavesNoRouteToTheBareStore()
    {
        var mission = await AStoreSearchLostToAMoveFromXLinkedToQ();

        Assert.True(string.IsNullOrEmpty(mission.StorageDoorCode));
        Assert.True(mission.StorageCellLost);
        Assert.False(await mission.BuildPathToDestination().WaitAsync(Ceiling));
        Assert.Empty(mission.MissionSteps);
    }

    /// <summary>
    /// Lo stesso spostamento, ma la rotta X->Q->S8 si calcola con la porta A8 ancora scritta, e si scrive
    /// dopo che chi ha perso l'ha tolta: tolta la porta, la versione della rotta e' salita, e la rotta non
    /// si scrive (risponde come chi ha perso, e la missione torna al giro). Prima si scriveva verso S8,
    /// senza porta e senza cella (primo giro di correzioni del compito 8 della voce 41).
    /// </summary>
    [Fact]
    public async Task ARouteComputedWithTheDoorOfALostSearchIsNotWritten()
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool>? building = null;
        PathMission mission;

        try
        {
            mission = await AStoreSearchLostToAMoveFromXLinkedToQ(moved =>
            {
                moved.RouteBuildPauseForTesting = () =>
                {
                    reached.TrySetResult();
                    return released.Task;
                };
                building = moved.BuildPathToDestination();
                return reached.Task.WaitAsync(Ceiling);
            });
        }
        finally
        {
            released.TrySetResult();
        }

        Assert.NotNull(building);
        Assert.True(await building!.WaitAsync(Ceiling));
        Assert.Empty(mission.MissionSteps);
        Assert.True(string.IsNullOrEmpty(mission.StorageDoorCode));
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
    }

    /// <summary>
    /// La scena delle due prove qui sopra: X e' collegato a Q, la ricerca della cella (P in
    /// <c>BuildRouteToStore</c>) prende la cella 8 e scrive A8, e prima che scriva la rotta l'UDC passa su
    /// X (<see cref="PathMission.MoveTo"/>). Con la ricerca ancora ferma gira
    /// <paramref name="whileTheSearchWaits"/>; poi la ricerca riparte e perde.
    /// </summary>
    private async Task<PathMission> AStoreSearchLostToAMoveFromXLinkedToQ(
        Func<PathMission, Task>? whileTheSearchWaits = null)
    {
        await APositionThatStoresInAnyCellWithARejectionPoint();
        _plant.Link("X", "Q");
        var mission = await MissionOn("P");
        _warehouse.StorageReservations[mission.LuId] = new StorageReservation(mission.MissionId,
            StorageReservationOutcome.CellFound, FoundCellId, FoundDoorCode, Partition: "0013", StorageDepth: 150,
            NeedsCompaction: false);
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        mission.RouteBuildPauseForTesting = Pause;

        var search = Navigator.FindWhereStoreLu(mission, _plant["P"], LookWhereAllocateLuTypes.BuildRouteToStore);
        await _reached.Task.WaitAsync(Ceiling);
        Assert.Equal(FoundDoorCode, mission.StorageDoorCode);
        Assert.Equal(PathMission.RouteWriteOutcome.Written, mission.MoveTo(_plant["X"].ItemGuid));
        if (whileTheSearchWaits != null) await whileTheSearchWaits(mission);
        _released.TrySetResult();

        Assert.Equal(RouteBuildStatusTypes.TakenOver, await search.WaitAsync(Ceiling));
        Assert.Equal(_plant["S8"].ItemGuid, mission.DestinationItemId);
        Assert.Equal([mission.MissionId], _warehouse.ReleasedStorageReservations);
        return mission;
    }

    /// <summary>
    /// Il giro delle rotte costruisce la rotta dritta di una missione su P (nessuna modalita' di
    /// deposito, nessuna destinazione fissa) verso Y; prima che la scriva un altro manda la missione
    /// verso H. La missione e' gia' di nuovo in coda, e il freno non conta un fallimento; non e' neanche
    /// una riuscita, e il fallimento che aveva gia' resta. Prima la rotta mancante era «nessun
    /// percorso», e la missione aspettava un passo del freno in piu' per una rotta che c'era.
    /// </summary>
    [Fact]
    public async Task AStraightRouteTakenOverInTheRouteLoopCountsNoFailure()
    {
        foreach (var name in new[] { "P", "Y", "H" }) _plant.Device(name);
        _plant.Link("P", "Y");
        _plant.Link("P", "H");
        var mission = await MissionOn("P");
        mission.DestinationItemId = _plant["Y"].ItemGuid;
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        Assert.Equal(1, Navigator.RoutePathRetries.RegisterFailure(mission.MissionId));
        mission.RouteBuildPauseForTesting = Pause;

        StartCellSearch(mission, _plant["P"]);
        await _reached.Task.WaitAsync(Ceiling);
        mission.DestinationItemId = _plant["H"].ItemGuid;
        _released.TrySetResult();
        await (Navigator.CellSearchInFlight(mission.MissionId) ?? Task.CompletedTask).WaitAsync(Ceiling);

        Assert.Equal(1, Navigator.RoutePathRetries.Failures(mission.MissionId));
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
        Assert.Equal(_plant["H"].ItemGuid, mission.DestinationItemId);
        Assert.Empty(mission.MissionSteps);
    }

    /// <summary>
    /// P ha la destinazione fissa Y: la ricerca scrive Y e calcola la rotta P->Y; prima che la scriva un
    /// altro manda la missione verso H. La ricerca risponde che la rotta l'ha presa un altro, non che
    /// non c'e': prima rispondeva «non disponibile», e il giro delle rotte contava un fallimento. Senza
    /// modalita' di deposito, e con <c>CheckOnly</c>, che prima chiede al gestionale se c'e' un posto
    /// (la cella 8 di S8) e poi va alla destinazione fissa.
    /// </summary>
    [Theory]
    [InlineData(LookWhereAllocateLuTypes.No)]
    [InlineData(LookWhereAllocateLuTypes.CheckOnly)]
    public async Task AFixedDestinationRouteTakenOverIsNotReportedAsMissing(LookWhereAllocateLuTypes mode)
    {
        foreach (var name in new[] { "P", "Y", "H" }) _plant.Device(name);
        var storage = _plant.Device("S8", DeviceTypes.Storage);
        _plant.Link("P", "Y");
        _plant.Link("P", "H");
        _plant.Link("P", "S8");
        _plant["P"].DestinationItemId = _plant["Y"].ItemGuid;
        _plant["P"].LinkedCellId = LinkedCellId;
        _plant["P"].LookWhereAllocateLu = mode;
        await PlantPersistence.SaveDoorMappingsAsync(storage.ItemGuid,
            [new DoorMapping { ItemId = storage.ItemGuid, DoorCode = FoundDoorCode, CellId = FoundCellId, Side = "A" }]);
        _warehouse.Cells[LinkedCellId] = Cell(LinkedCellId, "A7");
        _warehouse.Cells[FoundCellId] = Cell(FoundCellId, FoundDoorCode);
        var mission = await MissionOn("P");
        _warehouse.StorageReservations[mission.LuId] = new StorageReservation(mission.MissionId,
            StorageReservationOutcome.CellFound, FoundCellId, FoundDoorCode, Partition: "0013", StorageDepth: 150,
            NeedsCompaction: false);
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        mission.RouteBuildPauseForTesting = Pause;

        var search = Navigator.FindWhereStoreLu(mission, _plant["P"], mode);
        await _reached.Task.WaitAsync(Ceiling);
        Assert.Equal(_plant["Y"].ItemGuid, mission.DestinationItemId);
        mission.DestinationItemId = _plant["H"].ItemGuid;
        _released.TrySetResult();
        var outcome = await search.WaitAsync(Ceiling);

        Assert.Equal(RouteBuildStatusTypes.TakenOver, outcome);
        Assert.Equal(_plant["H"].ItemGuid, mission.DestinationItemId);
        Assert.Empty(mission.MissionSteps);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
    }

    // ---------------------------------------------------------------------------------------
    // Scene
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// La cucitura della costruzione: il primo che ci passa si ferma finche' la prova non lo lascia
    /// andare; dopo, passano tutti.
    /// </summary>
    private Task Pause()
    {
        _reached.TrySetResult();
        return _released.Task;
    }

    /// <summary>
    /// P collegato a X, al punto di scarto R e a Q, ricollocatore del magazzino S8; P cerca una cella
    /// qualunque (non e' collegato al magazzino) con la sua cella collegata 7, e la porta A8 e' della
    /// cella 8 di S8.
    /// </summary>
    private async Task APositionThatStoresInAnyCellWithARejectionPoint()
    {
        var position = _plant.Device("P");
        _plant.Device("X");
        _plant.Device("R");
        var relocator = _plant.Device("Q");
        var storage = _plant.Device("S8", DeviceTypes.Storage);
        _plant.Link("P", "X");
        _plant.Link("P", "R");
        _plant.Link("P", "Q");
        _plant.Link("Q", "S8");

        storage.RelocateItemId = relocator.ItemGuid;
        position.LinkedCellId = LinkedCellId;
        position.LookWhereAllocateLu = LookWhereAllocateLuTypes.BuildRouteToStore;
        position.RejectItemDestinationAllocateLu = _plant["R"].ItemGuid;
        Assert.False(position.IsLinkedToStorage);

        await PlantPersistence.SaveDoorMappingsAsync(storage.ItemGuid,
            [new DoorMapping { ItemId = storage.ItemGuid, DoorCode = FoundDoorCode, CellId = FoundCellId, Side = "A" }]);

        _warehouse.Cells[LinkedCellId] = Cell(LinkedCellId, "A7");
        _warehouse.Cells[FoundCellId] = Cell(FoundCellId, FoundDoorCode);
        _warehouse.CellPartitionMaps[FoundCellId] = new CellPartitionMap(FoundCellId, "0011", "0012",
            AsiDoorB: string.Empty, SubitemDoorB: string.Empty, [new CellPartition("0013", 0)]);
    }

    /// <summary>
    /// Una missione a bordo di <paramref name="position"/>, aggiunta in Recover (in Stop
    /// <c>ActiveMissions.Add</c> esce subito), con il primo posizionamento gia' finito.
    /// </summary>
    private async Task<PathMission> MissionOn(string position)
    {
        Assert.Equal(NavigatorStatusTypes.Recover, Navigator.NavStatus);

        var mission = new PathMission
        {
            PositionItemId = _plant[position].ItemGuid,
            LuId = LoadUnitId,
            MissionType = MissionTypes.MovingToItem,
            OperationType = OperationTypes.Outbound
        };
        ActiveMissions.Instance.Add(mission, buildRoute: false);
        Assert.Same(mission, _plant[position].LoadedLu);
        await mission.PositionWork;
        _mission = mission;
        return mission;
    }

    /// <summary>
    /// Il giro delle rotte avvia la ricerca della cella della missione (<c>StartCellSearch</c>, come la
    /// avvia <c>BuildRoutePath</c>), che gira in sottofondo.
    /// </summary>
    private static void StartCellSearch(PathMission mission, AutomationDevice position)
    {
        var method = typeof(Navigator).GetMethod("StartCellSearch", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        method!.Invoke(null, [mission, position]);
    }

    private static CellDescription Cell(decimal cellId, string doorCode) =>
        new(cellId, AreaId: 1, WarehouseId: 1, Depth: 1, Inverted: false, CellLockState.None,
            AllowedLoadUnitTypeId: null, GroupId: null, GroupOrder: 0, doorCode, DoorCodeB: string.Empty,
            DoorCount: 1, MultiDepthStorage: false);
}
