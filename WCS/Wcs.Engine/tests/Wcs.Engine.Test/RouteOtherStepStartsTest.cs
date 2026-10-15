using Wcs.Connection.Interface;
using Wcs.Connection.Interface.CommandData;
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
/// La corsa 5 della ricognizione negli altri avvii di uno step (voce 41, compito 6, decisione del
/// proprietario dell'11/10; una specifica interna, non pubblicata):
/// il <c>GetFrom</c>, il prelievo dalla cella, il deposito in cella, il movimento di gruppo e i
/// movimenti singoli del gruppo. Ognuno prende il posto (o sceglie lo step) e solo dopo scrive il
/// gettone; in mezzo una ricostruzione della rotta toglieva lo step, e il comando partiva lo stesso per
/// uno step che non c'era piu' (o il gruppo lo rimetteva in <c>MissionSteps</c> accanto alla rotta
/// nuova). Ora il gettone si prende come nel movimento singolo (<c>PathMission.TryTakeTheStepToken</c>):
/// la rotta e' cambiata, l'avvio rinuncia, restituisce il posto e non manda niente; e lo step della
/// rotta nuova parte al giro dopo.
/// </summary>
/// <remarks>
/// <para>
/// Il modello e' <see cref="RouteStepStartTest"/>. Il motore sta in <c>Stop</c>, e in <c>Run</c> solo
/// per l'ingresso fra le attive, con <c>AutoExecuteStep</c> spento su tutti i dispositivi. Mai in
/// Recover. Gli avvii si chiamano a mano; il campo registra i comandi, cosi' un comando partito si
/// vede.
/// </para>
/// <para>
/// Nessuna attesa a tempo: l'avvio si ferma nella sua cucitura (<c>StepStartPauseForTesting</c> del
/// dispositivo che avvia) e li', su un altro thread, gira la ricostruzione vera
/// (<c>BuildPathToDestination</c>), che la prova aspetta finita. La cucitura non asserisce: alcuni
/// avvii catturano le eccezioni del loro corpo, e un'asserzione caduta li' sparirebbe in una riga di
/// registro. Scrive quello che ha visto, e la prova lo guarda dopo. I tetti di 30 s fanno fallire
/// invece di appendere, e non decidono niente.
/// </para>
/// </remarks>
public sealed class RouteOtherStepStartsTest : IDisposable
{
    private const decimal LoadUnitId = 42;
    private const decimal PickUpCellId = 5;
    private const string PickUpDoorCode = "A5";
    private const decimal StorageCellId = 6;
    private const string StorageDoorCode = "A6";
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);

    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly QueuedDatabaseWriter<WcsWrite>? _previousWriter = Navigator.StateWriter;
    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly RoutingTestPlant _plant = new(NavigatorStatusTypes.Stop);
    private readonly FakeWarehouse _warehouse = new();

    public RouteOtherStepStartsTest()
    {
        Navigator.Store = _database;
        Navigator.StateWriter = null;
        Navigator.Warehouse = _warehouse;
        RoutesToBuild.Instance.Items = new();
    }

    public void Dispose()
    {
        RoutesToBuild.Instance.Items = new();
        _plant.Dispose();
        Navigator.Warehouse = _previousWarehouse;
        Navigator.StateWriter = _previousWriter;
        Navigator.Store = null;
        _database.Dispose();
    }

    /// <summary>
    /// Il <c>GetFrom</c>: R riceve da S, prende il posto su se stesso, mette S in <c>Moving</c> e solo
    /// dopo scrive il gettone su S. In mezzo la rotta si ricostruisce. Il comando non parte, S torna
    /// <c>Ready</c> senza gettone, il posto su R e' restituito e R torna <c>Ready</c>; poi R riceve con lo
    /// step nuovo. Prima il comando partiva per lo step tolto.
    /// </summary>
    [Fact]
    public async Task AGetFromDoesNotSetOffAStepRemovedBetweenTheSeatAndTheToken()
    {
        var mission = await AMissionOn("S", "R");
        _plant["R"].MotionType = MotionTypes.GetFrom;
        var field = new RecordingConnection();
        _plant["R"].DeviceConnection = field;
        var removed = _plant.PlannedStep(mission, "S", "R");
        var gateway = mission.Gateway;

        var pause = RebuildInThePauseOf(_plant["R"], mission,
            () => _plant["R"].IsWaitingForStep(removed.StepId) && _plant["S"].Status == DeviceStatusTypes.Moving);

        Assert.NotNull(_plant["R"].StepExecutor);
        _plant["R"].StepExecutor!.StartReceivingLu(_plant["S"]);

        pause.AssertSeen();
        Assert.Empty(field.Movements);
        Assert.Null(removed.RunningItem);
        Assert.Equal(DeviceStatusTypes.Ready, _plant["S"].Status);
        Assert.Equal(Guid.Empty, _plant["S"].RunningStepId);
        Assert.Equal(Guid.Empty, _plant["R"].WaitingStepId);
        Assert.Equal(DeviceStatusTypes.Ready, _plant["R"].Status);
        Assert.Equal(gateway, mission.Gateway);
        var rebuilt = TheRebuiltRouteIs(mission, "S->R");

        _plant["R"].StepExecutor!.StartReceivingLu(_plant["S"]);

        Assert.Equal([rebuilt.StepId], field.Movements);
        Assert.Equal(rebuilt.StepId, _plant["S"].RunningStepId);
        Assert.Equal(_plant["R"].ItemGuid, mission.Gateway);
        Assert.True(_plant["R"].IsWaitingForStep(rebuilt.StepId));
        ChangeDestinationStepStartsTest.NoStepStartIsLeftCounted(mission);
    }

    /// <summary>
    /// Il <c>GetFrom</c> che rinuncia al gettone non lascia niente dell'avvio (risposta 4 del proprietario
    /// dell'11/10): la missione era in <c>Stop</c> con il movimento di gruppo G, e resta cosi'. Prima la
    /// rinuncia la lasciava in <c>Run</c> (scritto sul database) con il movimento di gruppo azzerato.
    /// </summary>
    [Fact]
    public async Task ARenouncedGetFromLeavesTheMissionAsItWas()
    {
        var mission = await AMissionOn("S", "R");
        _plant["R"].MotionType = MotionTypes.GetFrom;
        _plant["R"].DeviceConnection = new RecordingConnection();
        var removed = _plant.PlannedStep(mission, "S", "R");
        var group = Guid.NewGuid();
        mission.Status = MissionStatusTypes.Stop;
        mission.GroupedMovementId = group;

        var pause = RebuildInThePauseOf(_plant["R"], mission,
            () => _plant["R"].IsWaitingForStep(removed.StepId) && _plant["S"].Status == DeviceStatusTypes.Moving);

        _plant["R"].StepExecutor!.StartReceivingLu(_plant["S"]);

        pause.AssertSeen();
        Assert.Null(removed.RunningItem);
        Assert.Equal(MissionStatusTypes.Stop, mission.Status);
        Assert.Equal(group, mission.GroupedMovementId);
    }

    /// <summary>
    /// Il prelievo dalla cella: R preleva da S, legge l'indirizzo della cella dal gestionale, prende il
    /// posto su se stesso e solo dopo scrive il gettone e <c>Loading</c>. In mezzo la rotta si
    /// ricostruisce. Il prelievo non parte, il posto e' restituito; poi R preleva con lo step nuovo.
    /// Prima il comando partiva per lo step tolto, e R restava in <c>Loading</c>.
    /// </summary>
    [Fact]
    public async Task APickUpDoesNotSetOffAStepRemovedBetweenTheSeatAndTheToken()
    {
        var mission = await AMissionToBePickedFromS();
        var field = new RecordingConnection();
        _plant["R"].DeviceConnection = field;
        var removed = _plant.PlannedStep(mission, "S", "R");
        var statusBefore = _plant["R"].Status;

        var pause = RebuildInThePauseOf(_plant["R"], mission, () => _plant["R"].IsWaitingForStep(removed.StepId));

        Assert.NotNull(_plant["R"].StepExecutor);
        await _plant["R"].StepExecutor!.StartPickUpLu(mission).WaitAsync(Ceiling);

        pause.AssertSeen();
        Assert.Empty(field.Movements);
        Assert.Null(removed.RunningItem);
        Assert.Equal(statusBefore, _plant["R"].Status);
        Assert.Equal(Guid.Empty, _plant["R"].RunningStepId);
        Assert.Equal(Guid.Empty, _plant["R"].WaitingStepId);
        TheRebuiltRouteIs(mission, "R->Y", "S->R");
        var pickUp = mission.MissionSteps.Single(step => step.ToNodeId == _plant["R"].ItemGuid);
        Assert.NotEqual(removed.StepId, pickUp.StepId);

        await _plant["R"].StepExecutor!.StartPickUpLu(mission).WaitAsync(Ceiling);

        Assert.Equal([pickUp.StepId], field.Movements);
        Assert.Equal(pickUp.StepId, _plant["R"].RunningStepId);
        Assert.Equal(DeviceStatusTypes.Loading, _plant["R"].Status);
        ChangeDestinationStepStartsTest.NoStepStartIsLeftCounted(mission);
    }

    /// <summary>
    /// Il deposito in cella: A deposita nel magazzino SA, legge l'indirizzo della cella dal gestionale e
    /// solo dopo scrive il gettone e <c>Unloading</c>. In mezzo la rotta si ricostruisce. Il deposito
    /// non parte; poi A deposita con lo step nuovo. Prima il comando partiva per lo step tolto, e A
    /// restava in <c>Unloading</c>.
    /// </summary>
    [Fact]
    public async Task ADepositDoesNotSetOffAStepRemovedBetweenTheSeatAndTheToken()
    {
        var mission = await AMissionOnAReadyToDepositInSA();
        var field = new RecordingConnection();
        _plant["A"].DeviceConnection = field;
        var removed = _plant.PlannedStep(mission, "A", "SA");
        Assert.Same(removed, mission.StepToDo);
        var statusBefore = _plant["A"].Status;

        var pause = RebuildInThePauseOf(_plant["A"], mission, () => true);

        Assert.NotNull(_plant["A"].StorageManager);
        await _plant["A"].StorageManager!.StartStoreLu().WaitAsync(Ceiling);

        pause.AssertSeen();
        Assert.Empty(field.Movements);
        Assert.Null(removed.RunningItem);
        Assert.Equal(statusBefore, _plant["A"].Status);
        Assert.Equal(Guid.Empty, _plant["A"].RunningStepId);
        var rebuilt = TheRebuiltRouteIs(mission, "A->SA");

        await _plant["A"].StorageManager!.StartStoreLu().WaitAsync(Ceiling);

        Assert.Equal([rebuilt.StepId], field.Movements);
        Assert.Equal(rebuilt.StepId, _plant["A"].RunningStepId);
        Assert.Equal(DeviceStatusTypes.Unloading, _plant["A"].Status);
        ChangeDestinationStepStartsTest.NoStepStartIsLeftCounted(mission);
    }

    /// <summary>
    /// Il deposito verso un dispositivo che non e' un magazzino (A->B, sulla rotta verso SA): A prende il
    /// posto su B per lo step A->B, e prima del gettone la rotta si ricostruisce, con uno step A->B nuovo.
    /// Il deposito non parte, e il posto su B torna libero: quello preso, per lo step tolto. Poi A
    /// deposita con lo step nuovo, e prende il posto per lui. Prima il deposito prendeva il posto per lo
    /// step letto alla prenotazione e lo restituiva per quello riletto dopo, lo step nuovo: il posto
    /// dello step tolto restava preso, e lo step nuovo non partiva (rilievo M112 della revisione finale
    /// della voce 41).
    /// </summary>
    [Fact]
    public async Task ADepositToADeviceGivesBackTheSeatItTookForTheRemovedStep()
    {
        var mission = await AMissionOnAReadyToDepositInSA(through: "B");
        var field = new RecordingConnection();
        _plant["A"].DeviceConnection = field;
        var removed = _plant.PlannedStep(mission, "A", "B");
        _plant.PlannedStep(mission, "B", "SA");
        Assert.Same(removed, mission.StepToDo);

        var pause = RebuildInThePauseOf(_plant["A"], mission, () => _plant["B"].IsWaitingForStep(removed.StepId));

        Assert.NotNull(_plant["A"].StorageManager);
        await _plant["A"].StorageManager!.StartStoreLu().WaitAsync(Ceiling);

        pause.AssertSeen();
        Assert.Empty(field.Movements);
        Assert.Null(removed.RunningItem);
        Assert.Equal(Guid.Empty, _plant["A"].RunningStepId);
        Assert.Equal(Guid.Empty, _plant["B"].WaitingStepId);
        var rebuilt = TheRebuiltRouteIs(mission, "A->B", "B->SA");

        await _plant["A"].StorageManager!.StartStoreLu().WaitAsync(Ceiling);

        Assert.Equal([rebuilt.StepId], field.Movements);
        Assert.Equal(rebuilt.StepId, _plant["A"].RunningStepId);
        Assert.True(_plant["B"].IsWaitingForStep(rebuilt.StepId));
        ChangeDestinationStepStartsTest.NoStepStartIsLeftCounted(mission);
    }

    /// <summary>
    /// Il movimento di gruppo: G ha scelto lo step M1->N, preso il posto su N e il suo gettone, e sta per
    /// scrivere il gettone sullo step. In mezzo la rotta si ricostruisce. Lo step tolto non torna in
    /// <c>MissionSteps</c>, nessun comando parte, il posto su N e' restituito e G torna <c>Ready</c>
    /// senza gettone; poi G muove lo step nuovo. Prima <c>ConfigureStep</c> rimetteva lo step tolto
    /// accanto alla rotta nuova, e il comando partiva per lui.
    /// </summary>
    [Fact]
    public async Task AGroupedMoveDoesNotSetOffAStepRemovedBetweenTheSeatAndTheToken()
    {
        var mission = await AMissionOnAMemberOfG();
        var field = new RecordingConnection();
        _plant["G"].DeviceConnection = field;
        var removed = _plant.PlannedStep(mission, "M1", "N");

        var pause = RebuildInThePauseOf(_plant["G"], mission, () => _plant["N"].IsWaitingForStep(removed.StepId));

        Assert.NotNull(_plant["G"].StepExecutor);
        await _plant["G"].StepExecutor!.ExecuteGroupedMOVE_LU_TO([removed]).WaitAsync(Ceiling);

        pause.AssertSeen();
        Assert.Empty(field.GroupedMovements);
        Assert.Null(removed.RunningItem);
        Assert.Equal(Guid.Empty, removed.GroupedMovementId);
        Assert.Equal(Guid.Empty, mission.GroupedMovementId);
        Assert.Equal(Guid.Empty, _plant["G"].RunningStepId);
        Assert.Equal(DeviceStatusTypes.Ready, _plant["G"].Status);
        Assert.Equal(Guid.Empty, _plant["N"].WaitingStepId);
        var rebuilt = TheRebuiltRouteIs(mission, "M1->N");

        await _plant["G"].StepExecutor!.ExecuteGroupedMOVE_LU_TO([rebuilt]).WaitAsync(Ceiling);

        var movement = Assert.Single(field.GroupedMovements);
        Assert.NotEqual(Guid.Empty, movement);
        Assert.Equal(movement, rebuilt.GroupedMovementId);
        Assert.Equal(movement, _plant["G"].RunningStepId);
        Assert.True(_plant["N"].IsWaitingForStep(rebuilt.StepId));
        ChangeDestinationStepStartsTest.NoStepStartIsLeftCounted(mission);
    }

    /// <summary>
    /// Lo stesso movimento di gruppo con due missioni: G muove M1->N e M2->N2, e la rotta della prima si
    /// ricostruisce prima del suo gettone. Lo step tolto resta fuori e il suo posto su N si restituisce
    /// subito; l'altro parte, con il posto su N2 e il gettone del gruppo. Senza la restituzione il posto
    /// su N restava preso per uno step senza comando, che nessun completamento del gruppo avrebbe sciolto.
    /// </summary>
    [Fact]
    public async Task AGroupedMoveGivesBackTheSeatOfTheRemovedStepAndMovesTheOthers()
    {
        var first = await AMissionOnAMemberOfG();
        var second = await AnotherMissionOnAMemberOfG("M2", "N2");
        var field = new RecordingConnection();
        _plant["G"].DeviceConnection = field;
        var removed = _plant.PlannedStep(first, "M1", "N");
        var moved = _plant.PlannedStep(second, "M2", "N2");

        var pause = RebuildInThePauseOf(_plant["G"], first,
            () => _plant["N"].IsWaitingForStep(removed.StepId) && _plant["N2"].IsWaitingForStep(moved.StepId));

        Assert.NotNull(_plant["G"].StepExecutor);
        await _plant["G"].StepExecutor!.ExecuteGroupedMOVE_LU_TO([removed, moved]).WaitAsync(Ceiling);

        pause.AssertSeen();
        var movement = Assert.Single(field.GroupedMovements);
        Assert.Equal(movement, moved.GroupedMovementId);
        Assert.Equal(movement, _plant["G"].RunningStepId);
        Assert.True(_plant["N2"].IsWaitingForStep(moved.StepId));
        Assert.Equal(Guid.Empty, removed.GroupedMovementId);
        Assert.Equal(Guid.Empty, _plant["N"].WaitingStepId);
        TheRebuiltRouteIs(first, "M1->N");
        ChangeDestinationStepStartsTest.NoStepStartIsLeftCounted(second);
    }

    /// <summary>
    /// Senza nessuno in mezzo, due step della stessa missione nello stesso movimento di gruppo prendono
    /// tutti e due il gettone del gruppo: il gettone del primo fa salire la versione della rotta, e il
    /// secondo confronta la versione dopo quel gettone, non quella letta prima del giro.
    /// </summary>
    [Fact]
    public async Task TwoStepsOfTheSameMissionInAGroupedMoveBothTakeTheGroupsToken()
    {
        var mission = await AMissionOnAMemberOfG();
        _plant.Device("X");
        _plant.Link("N", "X");
        _plant["X"].Status = DeviceStatusTypes.Ready;
        var field = new RecordingConnection();
        _plant["G"].DeviceConnection = field;
        var first = _plant.PlannedStep(mission, "M1", "N");
        var second = _plant.PlannedStep(mission, "N", "X");

        Assert.NotNull(_plant["G"].StepExecutor);
        await _plant["G"].StepExecutor!.ExecuteGroupedMOVE_LU_TO([first, second]).WaitAsync(Ceiling);

        var movement = Assert.Single(field.GroupedMovements);
        Assert.NotEqual(Guid.Empty, movement);
        Assert.Equal(movement, first.GroupedMovementId);
        Assert.Equal(movement, second.GroupedMovementId);
        Assert.True(_plant["X"].IsWaitingForStep(second.StepId));
        ChangeDestinationStepStartsTest.NoStepStartIsLeftCounted(mission);
    }

    /// <summary>
    /// I movimenti singoli del gruppo: G ha scelto lo step M1->N e sta per scriverci il suo gettone e
    /// mettersi in <c>Moving</c>. In mezzo la rotta si ricostruisce. Lo step tolto non torna in
    /// <c>MissionSteps</c>, G non si muove, M1 non avvia niente e N non da' il posto; poi G avvia lo step
    /// nuovo. Prima <c>ProcessSingleStep</c> rimetteva lo step tolto accanto alla rotta nuova e il
    /// gruppo si metteva in movimento per lui.
    /// </summary>
    [Fact]
    public async Task AGroupsSingleMoveDoesNotSetOffAStepRemovedBeforeTheGroupsToken()
    {
        var mission = await AMissionOnAMemberOfG();
        var field = new RecordingConnection();
        _plant["M1"].DeviceConnection = field;
        var removed = _plant.PlannedStep(mission, "M1", "N");
        var groupStatus = _plant["G"].Status;
        Assert.NotEqual(DeviceStatusTypes.Moving, groupStatus);

        var pause = RebuildInThePauseOf(_plant["G"], mission, () => true);

        Assert.NotNull(_plant["G"].StepExecutor);
        await _plant["G"].StepExecutor!.ExecuteSingleMOVE_LU_TO([removed]).WaitAsync(Ceiling);

        pause.AssertSeen();
        Assert.Empty(field.Movements);
        Assert.Null(removed.RunningItem);
        Assert.Equal(Guid.Empty, removed.GroupedMovementId);
        Assert.Equal(groupStatus, _plant["G"].Status);
        Assert.Equal(Guid.Empty, _plant["M1"].RunningStepId);
        Assert.Equal(Guid.Empty, _plant["N"].WaitingStepId);
        var rebuilt = TheRebuiltRouteIs(mission, "M1->N");

        await _plant["G"].StepExecutor!.ExecuteSingleMOVE_LU_TO([rebuilt]).WaitAsync(Ceiling);

        Assert.Equal([rebuilt.StepId], field.Movements);
        Assert.Equal(DeviceStatusTypes.Moving, _plant["G"].Status);
        Assert.Equal(rebuilt.StepId, _plant["M1"].RunningStepId);
        Assert.True(_plant["N"].IsWaitingForStep(rebuilt.StepId));
        ChangeDestinationStepStartsTest.NoStepStartIsLeftCounted(mission);
    }

    /// <summary>
    /// I movimenti singoli del gruppo scrivono il gettone del gruppo sullo step e sulla missione, e il
    /// gruppo lo tiene: G avvia lo step M1->N, e lo step, la missione e G portano lo stesso identificativo,
    /// non vuoto. Prima lo step e la missione lo prendevano rileggendo il <c>RunningStepId</c> di G, che
    /// finche' nessuno step porta l'identificativo risponde vuoto (voce D166): tutti e tre restavano
    /// vuoti (rilievo M129 della revisione finale della voce 41).
    /// </summary>
    [Fact]
    public async Task AGroupsSingleMoveWritesTheGroupsTokenOnTheStepAndTheMission()
    {
        var mission = await AMissionOnAMemberOfG();
        var field = new RecordingConnection();
        _plant["M1"].DeviceConnection = field;
        var step = _plant.PlannedStep(mission, "M1", "N");

        Assert.NotNull(_plant["G"].StepExecutor);
        await _plant["G"].StepExecutor!.ExecuteSingleMOVE_LU_TO([step]).WaitAsync(Ceiling);

        Assert.Equal([step.StepId], field.Movements);
        Assert.NotEqual(Guid.Empty, step.GroupedMovementId);
        Assert.Equal(step.GroupedMovementId, mission.GroupedMovementId);
        Assert.Equal(step.GroupedMovementId, _plant["G"].RunningStepId);
        Assert.Equal(step.StepId, _plant["M1"].RunningStepId);
    }

    /// <summary>
    /// Il deposito di gruppo con la verifica delle celle (<c>LookWhereAllocateLu</c> diverso da <c>No</c>,
    /// cella non vuota): G ha scelto lo step M1->SA, e la verifica (<c>CheckCellStoreStatus</c> ->
    /// <c>VerifyWhereAllocateLuGrouped</c>) tiene la stessa cella ma riscrive la rotta della missione,
    /// con uno step nuovo uguale. Nessun altro scrive in mezzo: il movimento parte, con lo step della rotta
    /// riscritta, e la missione ha quello solo. Prima la versione della rotta si leggeva prima della
    /// verifica, e il gettone del gruppo rinunciava a ogni giro per la riscrittura del gruppo stesso: G
    /// tornava <c>Ready</c> senza comando, il giro dopo rifaceva lo stesso, e la missione non depositava
    /// mai (primo giro di correzioni del compito 6).
    /// </summary>
    [Fact]
    public async Task AGroupedDepositWhoseCellCheckRewritesTheRouteStillSetsOff()
    {
        var mission = await AMissionOnAMemberOfGToDepositInSA();
        var field = new RecordingConnection();
        _plant["G"].DeviceConnection = field;
        var chosen = _plant.PlannedStep(mission, "M1", "SA");

        Assert.NotNull(_plant["G"].StepExecutor);
        await _plant["G"].StepExecutor!.ExecuteGroupedMOVE_LU_TO([chosen]).WaitAsync(Ceiling);

        Assert.Contains(nameof(IWarehouse.ReserveStorageAsync), _warehouse.Calls);
        var movement = Assert.Single(field.GroupedMovements);
        Assert.NotEqual(Guid.Empty, movement);
        var step = Assert.Single(mission.MissionSteps);
        Assert.NotEqual(chosen.StepId, step.StepId);
        Assert.Equal("M1->SA", $"{_plant.NameOf(step.FromNodeId)}->{_plant.NameOf(step.ToNodeId)}");
        Assert.Equal(movement, step.GroupedMovementId);
        Assert.Equal(movement, mission.GroupedMovementId);
        Assert.Equal(movement, _plant["G"].RunningStepId);
        Assert.Equal(StorageDoorCode, mission.StorageDoorCode);
        ChangeDestinationStepStartsTest.NoStepStartIsLeftCounted(mission);
    }

    /// <summary>
    /// Lo stesso con una seconda missione, sul membro M2, che va nella stessa cella passando da N: il suo
    /// step M2->N non va in un magazzino, e G ci ha preso il posto. La verifica riscrive la rotta di tutte
    /// e due; il movimento parte con gli step nuovi, e il posto su N passa allo step nuovo di M2->N. Senza,
    /// il posto restava allo step scartato, appeso, e lo step nuovo partiva verso un N che non lo aspettava.
    /// </summary>
    [Fact]
    public async Task AGroupedMoveWhoseCellCheckRewritesTheRouteHandsTheSeatToTheNewStep()
    {
        var first = await AMissionOnAMemberOfGToDepositInSA();
        var second = await AMissionOnM2ToSAThroughN();
        var field = new RecordingConnection();
        _plant["G"].DeviceConnection = field;
        var chosenFirst = _plant.PlannedStep(first, "M1", "SA");
        var chosenSecond = _plant.PlannedStep(second, "M2", "N");
        _plant.PlannedStep(second, "N", "SA");

        Assert.NotNull(_plant["G"].StepExecutor);
        await _plant["G"].StepExecutor!.ExecuteGroupedMOVE_LU_TO([chosenFirst, chosenSecond]).WaitAsync(Ceiling);

        var movement = Assert.Single(field.GroupedMovements);
        Assert.NotEqual(Guid.Empty, movement);
        var stepFirst = Assert.Single(first.MissionSteps);
        Assert.NotEqual(chosenFirst.StepId, stepFirst.StepId);
        Assert.Equal(movement, stepFirst.GroupedMovementId);
        var stepSecond = second.MissionSteps.Single(s => s.FromNodeId == _plant["M2"].ItemGuid);
        Assert.NotEqual(chosenSecond.StepId, stepSecond.StepId);
        Assert.Equal("N", _plant.NameOf(stepSecond.ToNodeId));
        Assert.Equal(movement, stepSecond.GroupedMovementId);
        Assert.Equal(stepSecond.StepId, _plant["N"].WaitingStepId);
        Assert.Equal(movement, _plant["G"].RunningStepId);
        ChangeDestinationStepStartsTest.NoStepStartIsLeftCounted(first);
        ChangeDestinationStepStartsTest.NoStepStartIsLeftCounted(second);
    }

    // ---------------------------------------------------------------------------------------
    // La rinuncia chiede di rieseguire chi avvia (risposta 4 del proprietario dell'11/10)
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Il prelievo che rinuncia al gettone chiede a R di rieseguire appena e' libero, come il movimento
    /// singolo (<c>RouteStepStartTest.ARenouncedStartWakesTheDeviceThatStartsTheNewRoute</c>). In esercizio
    /// il prelievo gira dentro l'esecuzione di R, e chi ha riscritto la rotta richiama R mentre e' ancora
    /// li': e' rifiutato. Fino al giro finale delle correzioni della voce 41 la rinuncia restituiva il
    /// posto e usciva, e l'UDC aspettava il prossimo richiamo di R, che il motore non fa da solo.
    /// </summary>
    [Fact]
    public async Task ARenouncedPickUpAsksThePickerToRunAgain()
    {
        var mission = await AMissionToBePickedFromS();
        _plant["R"].DeviceConnection = new RecordingConnection();
        var removed = _plant.PlannedStep(mission, "S", "R");
        var pause = RebuildInThePauseOf(_plant["R"], mission, () => _plant["R"].IsWaitingForStep(removed.StepId));

        await TheStarterIsAskedToRunAgain(_plant["R"], () => _plant["R"].StepExecutor!.StartPickUpLu(mission));

        pause.AssertSeen();
        Assert.Null(removed.RunningItem);
    }

    /// <summary>Lo stesso per il deposito in cella: A rinuncia al gettone e chiede di rieseguire.</summary>
    [Fact]
    public async Task ARenouncedDepositAsksTheDepositorToRunAgain()
    {
        var mission = await AMissionOnAReadyToDepositInSA();
        _plant["A"].DeviceConnection = new RecordingConnection();
        var removed = _plant.PlannedStep(mission, "A", "SA");
        var pause = RebuildInThePauseOf(_plant["A"], mission, () => true);

        await TheStarterIsAskedToRunAgain(_plant["A"], () => _plant["A"].StorageManager!.StartStoreLu());

        pause.AssertSeen();
        Assert.Null(removed.RunningItem);
    }

    /// <summary>
    /// Il deposito che al gettone non trova piu' a bordo la missione letta prima dello step (spostata su B
    /// mentre il deposito prendeva il posto) restituisce il posto e chiede ad A di rieseguire, come la
    /// rinuncia al gettone: quello che A ha a bordo adesso aspetta un'esecuzione, e nessun altro la chiede.
    /// </summary>
    [Fact]
    public async Task ADepositThatNoLongerFindsItsMissionOnBoardAsksTheDepositorToRunAgain()
    {
        var mission = await AMissionOnAReadyToDepositInSA(through: "B");
        _plant["A"].DeviceConnection = new RecordingConnection();
        var removed = _plant.PlannedStep(mission, "A", "B");
        _plant.PlannedStep(mission, "B", "SA");
        bool? seatTaken = null;
        PathMission.RouteWriteOutcome? moved = null;
        _plant["A"].StepStartPauseForTesting = () =>
        {
            _plant["A"].StepStartPauseForTesting = null;
            seatTaken = _plant["B"].IsWaitingForStep(removed.StepId);
            moved = mission.MoveTo(_plant["B"].ItemGuid);
        };

        await TheStarterIsAskedToRunAgain(_plant["A"], () => _plant["A"].StorageManager!.StartStoreLu());
        await mission.PositionWork;

        Assert.True(seatTaken);
        Assert.Equal(PathMission.RouteWriteOutcome.Written, moved);
        Assert.Null(_plant["A"].LoadedLu);
        Assert.Null(removed.RunningItem);
        Assert.Equal(Guid.Empty, _plant["A"].RunningStepId);
        Assert.False(_plant["B"].IsWaitingForStep(removed.StepId));
    }

    /// <summary>Lo stesso per il movimento di gruppo: G rinuncia al gettone dello step e chiede di rieseguire.</summary>
    [Fact]
    public async Task ARenouncedGroupedMoveAsksTheGroupToRunAgain()
    {
        var mission = await AMissionOnAMemberOfG();
        _plant["G"].DeviceConnection = new RecordingConnection();
        var removed = _plant.PlannedStep(mission, "M1", "N");
        var pause = RebuildInThePauseOf(_plant["G"], mission, () => _plant["N"].IsWaitingForStep(removed.StepId));

        await TheStarterIsAskedToRunAgain(_plant["G"],
            () => _plant["G"].StepExecutor!.ExecuteGroupedMOVE_LU_TO([removed]));

        pause.AssertSeen();
        Assert.Null(removed.RunningItem);
    }

    /// <summary>Lo stesso per i movimenti singoli del gruppo: G rinuncia al suo gettone e chiede di rieseguire.</summary>
    [Fact]
    public async Task ARenouncedSingleMoveOfTheGroupAsksTheGroupToRunAgain()
    {
        var mission = await AMissionOnAMemberOfG();
        _plant["M1"].DeviceConnection = new RecordingConnection();
        var removed = _plant.PlannedStep(mission, "M1", "N");
        var pause = RebuildInThePauseOf(_plant["G"], mission, () => true);

        await TheStarterIsAskedToRunAgain(_plant["G"],
            () => _plant["G"].StepExecutor!.ExecuteSingleMOVE_LU_TO([removed]));

        pause.AssertSeen();
        Assert.Null(removed.RunningItem);
    }

    /// <summary>
    /// L'avvio gira con il semaforo di <paramref name="starter"/> preso, come dentro la sua esecuzione: la
    /// richiesta di rieseguire resta li' (<c>ExecutionAskedWhenFreeForTesting</c>) e nessuna esecuzione
    /// parte sul pool. Prima dell'avvio non c'e', dopo si'.
    /// </summary>
    private static async Task TheStarterIsAskedToRunAgain(AutomationDevice starter, Func<Task> start)
    {
        Assert.True(starter.TryHoldExecutionForTesting());
        try
        {
            Assert.False(starter.ExecutionAskedWhenFreeForTesting);
            await start().WaitAsync(Ceiling);
            Assert.True(starter.ExecutionAskedWhenFreeForTesting, "chi avvia non ha chiesto di rieseguire");
        }
        finally
        {
            starter.ReleaseExecutionHoldForTesting();
        }
    }

    // ---------------------------------------------------------------------------------------
    // La pausa dell'avvio e la ricostruzione
    // ---------------------------------------------------------------------------------------

    /// <summary>Che cosa la cucitura ha visto, una volta sola: la prova lo guarda dopo l'avvio.</summary>
    private sealed class Pause
    {
        internal bool Reached;
        internal bool HeldAtThePause;
        internal bool? RebuildLost;
        internal Exception? Failure;

        internal void AssertSeen()
        {
            Assert.True(Reached, "l'avvio non e' arrivato alla sua cucitura");
            Assert.Null(Failure);
            Assert.True(HeldAtThePause, "alla cucitura l'avvio non aveva preso quello che prende prima del gettone");
            Assert.False(RebuildLost);
        }
    }

    /// <summary>
    /// Alla prima chiamata della cucitura di <paramref name="starter"/>, cioe' con il posto preso e il
    /// gettone non ancora scritto, la ricostruzione vera della rotta di <paramref name="mission"/> gira
    /// su un altro thread, e l'avvio la aspetta finita. Prima guarda <paramref name="heldAtThePause"/>:
    /// quello che l'avvio deve aver gia' preso li'.
    /// </summary>
    private static Pause RebuildInThePauseOf(AutomationDevice starter, PathMission mission, Func<bool> heldAtThePause)
    {
        var pause = new Pause();
        starter.StepStartPauseForTesting = () =>
        {
            starter.StepStartPauseForTesting = null;
            pause.Reached = true;
            try
            {
                pause.HeldAtThePause = heldAtThePause();
                pause.RebuildLost = Task.Run(() => mission.BuildPathToDestination()).WaitAsync(Ceiling)
                    .GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                pause.Failure = ex;
            }
        };
        return pause;
    }

    /// <summary>
    /// La rotta della missione e' quella ricostruita, «da->a» in ordine alfabetico, e non e' in volo;
    /// nessun avvio resta contato. Restituisce il primo step della rotta.
    /// </summary>
    private MissionStep TheRebuiltRouteIs(PathMission mission, params string[] steps)
    {
        Assert.Equal(steps,
            mission.MissionSteps.Select(s => $"{_plant.NameOf(s.FromNodeId)}->{_plant.NameOf(s.ToNodeId)}").Order());
        Assert.Null(mission.StepInFlight());
        ChangeDestinationStepStartsTest.NoStepStartIsLeftCounted(mission);
        return mission.StepToDo ?? mission.MissionSteps.First();
    }

    // ---------------------------------------------------------------------------------------
    // Scene
    // ---------------------------------------------------------------------------------------

    /// <summary>Entra fra le attive in Run, con <c>AutoExecuteStep</c> spento; poi il motore e' fermo.</summary>
    private async Task Enter(PathMission mission)
    {
        foreach (var name in _plant.DeviceNames) _plant[name].AutoExecuteStep = false;
        Navigator.NavStatus = NavigatorStatusTypes.Run;
        ActiveMissions.Instance.Add(mission, buildRoute: false);
        Assert.Same(mission, ActiveMissions.Instance[mission.MissionId]);
        await mission.PositionWork;
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        _warehouse.Calls.Clear();
    }

    /// <summary>
    /// <paramref name="position"/> collegato a <paramref name="destination"/>, tutti e due
    /// <c>Ready</c>; sul primo una missione verso il secondo, fra le attive e senza rotta.
    /// </summary>
    private async Task<PathMission> AMissionOn(string position, string destination)
    {
        _plant.Device(position);
        _plant.Device(destination);
        _plant.Link(position, destination);
        _plant[position].Status = DeviceStatusTypes.Ready;
        _plant[destination].Status = DeviceStatusTypes.Ready;

        var mission = new PathMission
        {
            PositionItemId = _plant[position].ItemGuid,
            DestinationItemId = _plant[destination].ItemGuid,
            LuId = LoadUnitId,
            MissionType = MissionTypes.MovingToItem
        };
        await mission.PositionWork;
        await Enter(mission);
        Assert.Same(mission, _plant[position].LoadedLu);
        return mission;
    }

    /// <summary>
    /// Il magazzino S, con la porta A5 sulla cella 5, collegato a R, che preleva; R collegato a Y. Una
    /// missione con l'UDC nella cella 5, senza posizione (la sorgente e' S), diretta a Y, senza rotta.
    /// </summary>
    private async Task<PathMission> AMissionToBePickedFromS()
    {
        var storage = _plant.Device("S", DeviceTypes.Storage);
        var picker = _plant.Device("R");
        _plant.Device("Y");
        _plant.Link("S", "R");
        _plant.Link("R", "Y");
        picker.RecordStorageLength = RecordStorageTypes.FromPlc;
        picker.Status = DeviceStatusTypes.Ready;
        _plant["Y"].Status = DeviceStatusTypes.Ready;

        await PlantPersistence.SaveDoorMappingsAsync(storage.ItemGuid,
            [new DoorMapping { ItemId = storage.ItemGuid, DoorCode = PickUpDoorCode, CellId = PickUpCellId, Side = "A" }]);
        _warehouse.Cells[PickUpCellId] = Cell(PickUpCellId, PickUpDoorCode);
        _warehouse.CellPartitionMaps[PickUpCellId] = new CellPartitionMap(PickUpCellId, "0051", "0052",
            AsiDoorB: string.Empty, SubitemDoorB: string.Empty, [new CellPartition("0053", -1)]);

        var mission = new PathMission
        {
            LuId = LoadUnitId,
            PickUpDoorCode = PickUpDoorCode,
            SourceItemId = storage.ItemGuid,
            DestinationItemId = _plant["Y"].ItemGuid,
            MissionType = MissionTypes.Extraction,
            OperationType = OperationTypes.Outbound
        };
        await Enter(mission);
        return mission;
    }

    /// <summary>
    /// A collegato al magazzino SA, con la porta A6 sulla cella 6 e il suo indirizzo di campo; su A una
    /// missione diretta a SA con la porta A6, fra le attive e senza rotta. <c>RecordStorageLength</c> e'
    /// <c>FromPlc</c>: la riverifica del deposito non chiede niente, e l'unica lettura dal gestionale e'
    /// quella dell'indirizzo (come in <see cref="StoreTokenTest"/>). Con <paramref name="through"/>
    /// A non e' collegato a SA: ci arriva passando da quel dispositivo, pronto (A->B->SA).
    /// </summary>
    private async Task<PathMission> AMissionOnAReadyToDepositInSA(string? through = null)
    {
        var device = _plant.Device("A");
        var storage = _plant.Device("SA", DeviceTypes.Storage);
        if (through == null)
        {
            _plant.Link("A", "SA");
        }
        else
        {
            _plant.Device(through).Status = DeviceStatusTypes.Ready;
            _plant.Link("A", through);
            _plant.Link(through, "SA");
        }
        device.RecordStorageLength = RecordStorageTypes.FromPlc;
        device.Status = DeviceStatusTypes.Ready;

        await PlantPersistence.SaveDoorMappingsAsync(storage.ItemGuid,
            [new DoorMapping { ItemId = storage.ItemGuid, DoorCode = StorageDoorCode, CellId = StorageCellId, Side = "A" }]);
        _warehouse.Cells[StorageCellId] = Cell(StorageCellId, StorageDoorCode);
        _warehouse.CellPartitionMaps[StorageCellId] = new CellPartitionMap(StorageCellId, "0061", "0062",
            AsiDoorB: string.Empty, SubitemDoorB: string.Empty, [new CellPartition("0063", -1)]);

        var mission = new PathMission
        {
            PositionItemId = device.ItemGuid,
            LuId = LoadUnitId,
            StorageDoorCode = StorageDoorCode,
            DestinationItemId = storage.ItemGuid
        };
        await mission.PositionWork;
        await Enter(mission);
        Assert.Same(mission, device.LoadedLu);
        return mission;
    }

    /// <summary>
    /// Il gruppo G con il membro M1, collegato a N; su M1 una missione diretta a N, fra le attive e
    /// senza rotta; M1, N e G sono pronti.
    /// </summary>
    private async Task<PathMission> AMissionOnAMemberOfG()
    {
        var group = _plant.Device("G", DeviceTypes.GroupOfAutomationDevices);
        var member = _plant.Device("M1");
        _plant.Device("N");
        _plant.Link("M1", "N");
        group.GroupItemId = 93;
        member.BelongingGroupId = group.GroupItemId;
        Assert.Same(group, member.BelongedGroup);
        member.Status = DeviceStatusTypes.Ready;
        _plant["N"].Status = DeviceStatusTypes.Ready;
        group.Status = DeviceStatusTypes.Ready;

        var mission = new PathMission
        {
            PositionItemId = member.ItemGuid,
            DestinationItemId = _plant["N"].ItemGuid,
            LuId = LoadUnitId,
            MissionType = MissionTypes.MovingToItem,
            OperationType = OperationTypes.Outbound
        };
        await mission.PositionWork;
        await Enter(mission);
        Assert.Same(mission, member.LoadedLu);
        return mission;
    }

    /// <summary>
    /// Un secondo membro <paramref name="member"/> del gruppo G di <see cref="AMissionOnAMemberOfG"/>,
    /// collegato a <paramref name="destination"/>; su di lui una missione verso quella, fra le attive e
    /// senza rotta.
    /// </summary>
    private async Task<PathMission> AnotherMissionOnAMemberOfG(string member, string destination)
    {
        var device = _plant.Device(member);
        _plant.Device(destination);
        _plant.Link(member, destination);
        device.BelongingGroupId = _plant["G"].GroupItemId;
        Assert.Same(_plant["G"], device.BelongedGroup);
        device.Status = DeviceStatusTypes.Ready;
        _plant[destination].Status = DeviceStatusTypes.Ready;

        var mission = new PathMission
        {
            PositionItemId = device.ItemGuid,
            DestinationItemId = _plant[destination].ItemGuid,
            LuId = LoadUnitId + 1,
            MissionType = MissionTypes.MovingToItem,
            OperationType = OperationTypes.Outbound
        };
        await mission.PositionWork;
        await Enter(mission);
        Assert.Same(mission, device.LoadedLu);
        return mission;
    }

    /// <summary>
    /// Il gruppo G (<c>LookWhereAllocateLu</c> <c>BuildRouteToStore</c>, ricollocatore del magazzino SA)
    /// con il membro M1, collegato a SA e con la cella 6 collegata; SA ha la porta A6 sulla cella 6, non
    /// vuota (c'e' l'UDC 7), cosi' che la verifica delle celle del gruppo chieda la cella al gestionale,
    /// che le risponde la stessa. Su M1 una missione della configurazione diretta a SA con la porta A6, fra
    /// le attive e senza rotta.
    /// </summary>
    private async Task<PathMission> AMissionOnAMemberOfGToDepositInSA()
    {
        var group = _plant.Device("G", DeviceTypes.GroupOfAutomationDevices);
        var member = _plant.Device("M1");
        var storage = _plant.Device("SA", DeviceTypes.Storage);
        _plant.Link("M1", "SA");
        group.GroupItemId = 93;
        group.RecordStorageLength = RecordStorageTypes.FromPlc;
        group.LookWhereAllocateLu = LookWhereAllocateLuTypes.BuildRouteToStore;
        member.BelongingGroupId = group.GroupItemId;
        Assert.Same(group, member.BelongedGroup);
        member.LinkedCellId = StorageCellId;
        storage.RelocateItemId = group.ItemGuid;
        member.Status = DeviceStatusTypes.Ready;
        group.Status = DeviceStatusTypes.Ready;

        await PlantPersistence.SaveDoorMappingsAsync(storage.ItemGuid,
            [new DoorMapping { ItemId = storage.ItemGuid, DoorCode = StorageDoorCode, CellId = StorageCellId, Side = "A" }]);
        _warehouse.Cells[StorageCellId] = Cell(StorageCellId, StorageDoorCode);
        _warehouse.CellContents[StorageCellId] = [7];
        _warehouse.CellPartitionMaps[StorageCellId] = new CellPartitionMap(StorageCellId, "0061", "0062",
            AsiDoorB: string.Empty, SubitemDoorB: string.Empty, [new CellPartition("0063", -1)]);
        _warehouse.StorageReservations[LoadUnitId] = new StorageReservation(Guid.Empty,
            StorageReservationOutcome.CellFound, StorageCellId, StorageDoorCode, Partition: "0063", StorageDepth: 150,
            NeedsCompaction: false);

        var mission = new PathMission
        {
            PositionItemId = member.ItemGuid,
            LuId = LoadUnitId,
            StorageDoorCode = StorageDoorCode,
            DestinationItemId = storage.ItemGuid,
            MissionType = MissionTypes.Storing,
            OperationType = OperationTypes.Inbound
        };
        await mission.PositionWork;
        await Enter(mission);
        Assert.Same(mission, member.LoadedLu);
        Assert.Contains(mission, group.LUsLoaded);
        return mission;
    }

    /// <summary>
    /// Il membro M2 del gruppo G di <see cref="AMissionOnAMemberOfGToDepositInSA"/>, con la cella 6
    /// collegata, e N, pronto, fra M2 e SA; su M2 una missione della configurazione (l'UDC 43) diretta a
    /// SA con la porta A6, che il gestionale le tiene, fra le attive e senza rotta.
    /// </summary>
    private async Task<PathMission> AMissionOnM2ToSAThroughN()
    {
        var member = _plant.Device("M2");
        _plant.Device("N");
        _plant.Link("M2", "N");
        _plant.Link("N", "SA");
        member.BelongingGroupId = _plant["G"].GroupItemId;
        Assert.Same(_plant["G"], member.BelongedGroup);
        member.LinkedCellId = StorageCellId;
        member.Status = DeviceStatusTypes.Ready;
        _plant["N"].Status = DeviceStatusTypes.Ready;
        _warehouse.StorageReservations[LoadUnitId + 1] = new StorageReservation(Guid.Empty,
            StorageReservationOutcome.CellFound, StorageCellId, StorageDoorCode, Partition: "0063", StorageDepth: 150,
            NeedsCompaction: false);

        var mission = new PathMission
        {
            PositionItemId = member.ItemGuid,
            LuId = LoadUnitId + 1,
            StorageDoorCode = StorageDoorCode,
            DestinationItemId = _plant["SA"].ItemGuid,
            MissionType = MissionTypes.Storing,
            OperationType = OperationTypes.Inbound
        };
        await mission.PositionWork;
        await Enter(mission);
        Assert.Same(mission, member.LoadedLu);
        return mission;
    }

    private static CellDescription Cell(decimal cellId, string doorCode) =>
        new(cellId, AreaId: 1, WarehouseId: 1, Depth: 1, Inverted: false, CellLockState.None,
            AllowedLoadUnitTypeId: null, GroupId: null, GroupOrder: 0, doorCode, DoorCodeB: string.Empty,
            DoorCount: 1, MultiDepthStorage: false);

    /// <summary>Il campo: non parla con nessun PLC, tiene i comandi ricevuti.</summary>
    private sealed class RecordingConnection : IConnection
    {
        /// <summary>Lo step di ogni comando di un movimento singolo, nell'ordine di arrivo.</summary>
        internal List<Guid> Movements { get; } = [];

        /// <summary>Il gettone di ogni comando di un movimento di gruppo, nell'ordine di arrivo.</summary>
        internal List<Guid> GroupedMovements { get; } = [];

        public decimal Id => 1;
        public bool Managed { get; set; }

        // Il motore non li solleva in queste prove: servono a soddisfare l'interfaccia.
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
            lock (Movements) Movements.Add(stepGuid);
        }

        public void MoveLuFromAsiToAsi(RunAsi sourceItem, DataAsi actionItemPerformer, Guid groupedMovementId,
            List<MovementLuDataCommand> loadUnitsToMove, int luHandlingMode = 0)
        {
            lock (GroupedMovements) GroupedMovements.Add(groupedMovementId);
        }

        public void OpenConnection() { }
        public void RequestLuDataFromAsi(RunAsi item) { }
        public void SendCommandToAsi(RunAsi destinationItem, int commandCode, List<string?> parameters) { }
        public void SendLuDataToAsi(RunAsi item, LuInfoDataCommand value) { }
        public void ShowMessageToAsi(RunAsi destinationItem, string message) { }
    }
}
