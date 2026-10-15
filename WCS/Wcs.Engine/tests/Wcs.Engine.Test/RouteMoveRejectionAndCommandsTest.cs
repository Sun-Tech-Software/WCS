using Wcs.Engine.Api;
using Wcs.Engine.Collections;
using Wcs.Engine.Items;
using Wcs.Engine.Persistence;
using Wcs.Engine.Test.Routing;
using Wcs.Wms.Contracts;
using Wcs.Wms.Contracts.Test;
using Xunit;
using static Wcs.Common.Enums;
using static Wcs.Engine.Items.PathMission;

namespace Wcs.Engine.Test;

/// <summary>
/// Lo spostamento, i rigetti e i comandi scrivono la rotta con la primitiva (voce 41, compito 4,
/// disegno C): la corsa 3 della ricognizione (lo spostamento insieme al commit di un cambio), la corsa
/// 6 (la rimozione della strategia <c>Standard</c> dopo una costruzione), la corsa 7 (il rigetto di
/// gruppo con uno step in volo), e le scritture del rigetto e del comando «sposta l'UDC» che si
/// spezzavano in due (una specifica interna, non pubblicata).
/// </summary>
/// <remarks>
/// <para>
/// Il motore sta in <c>Stop</c>, e in <c>Run</c> solo dove la via provata lo chiede (l'ingresso fra le
/// attive, l'esecuzione di un dispositivo, il comando), con <c>AutoExecuteStep</c> spento su tutti i
/// dispositivi perche' il passaggio in Run li sveglia. Mai in Recover: il setter della destinazione
/// salta il suo blocco e <c>PathBuilder</c> i filtri.
/// </para>
/// <para>
/// Nessuna attesa a tempo: lo scrittore provato si ferma in una cucitura, e l'altro scrittore scrive li'
/// (sullo stesso thread, o su un thread suo che la prova aspetta fermo sul lucchetto o finito). I tetti
/// di 30 s fanno fallire invece di appendere, e non decidono niente. La coerenza della rotta e' quella
/// della ricognizione (§1): parte dalla posizione, gli step vanno in fila senza due con la stessa
/// sorgente, e finiscono sulla destinazione; si guarda la proprieta', non si ricalcola la rotta.
/// </para>
/// </remarks>
public sealed class RouteMoveRejectionAndCommandsTest : IDisposable
{
    private const decimal LoadUnitId = 42;
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);

    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly QueuedDatabaseWriter<WcsWrite>? _previousWriter = Navigator.StateWriter;
    private readonly FakeWarehouse _warehouse = new();
    private readonly RoutingTestPlant _plant = new(NavigatorStatusTypes.Stop);

    public RouteMoveRejectionAndCommandsTest()
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

    // ---------------------------------------------------------------------------------------
    // Corsa 3: lo spostamento
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Corsa 3. L'operatore cambia la destinazione dell'UDC su P verso X; il commit ha gia' visto la
    /// missione su P, e prima che scriva un altro operatore la sposta su Q. Lo spostamento vince
    /// (decisione 2 del 10/10): la missione e' su Q senza la rotta vecchia, e in coda al giro delle
    /// rotte, perche' il posto delle ricerche lo teneva il cambio. Il commit trova la versione cambiata
    /// e non scrive: niente rotta da P, la destinazione resta A e il cambio e' rifiutato. Prima il commit
    /// scriveva la rotta P->X con la missione gia' su Q.
    /// </summary>
    [Fact]
    public async Task AMoveDuringTheCommitOfAChangeLeavesNoRouteFromTheOldPosition()
    {
        foreach (var name in new[] { "P", "A", "X", "Q" }) _plant.Device(name);
        _plant.Link("P", "A");
        _plant.Link("P", "X");
        _plant.Link("Q", "A");
        var mission = await MissionOn("P", towards: "A");
        _plant.PlannedStep(mission, "P", "A");

        MissionMoveOutcome? moved = null;
        mission.CommitRoutePauseForTesting = () =>
        {
            mission.CommitRoutePauseForTesting = null;
            moved = MissionGateway.Move(mission.MissionId, _plant["Q"].ItemGuid);
        };

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["X"].ItemGuid);

        Assert.Equal(MissionMoveOutcome.Moved, moved);
        Assert.Equal(MissionDestinationOutcome.StepInFlight, result.Outcome);
        Assert.Equal(_plant["Q"].ItemGuid, mission.PositionItemId);
        Assert.Equal(_plant["A"].ItemGuid, mission.DestinationItemId);
        Assert.False(mission.OperatorDestination);
        Assert.DoesNotContain(mission.MissionSteps, s => s.FromNodeId == _plant["P"].ItemGuid);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
    }

    /// <summary>
    /// Lo spostamento su Q ha tolto la rotta e non ha ancora cambiato la posizione; uno scrittore che
    /// legge la versione adesso, con la missione ancora su P, scrive la rotta P->A. La rimozione e il
    /// cambio di posizione stanno insieme sotto il lucchetto della rotta: lo scrittore aspetta, trova la
    /// versione cambiata e non scrive, e la missione su Q ha la rotta che lo spostamento le costruisce da
    /// Q. Prima lo scrittore passava in mezzo, e la missione restava su Q con la rotta P->A, che lo
    /// spostamento teneva perche' una rotta c'era.
    /// </summary>
    [Fact]
    public async Task ARouteWrittenWhileTheMoveIsUnderWayIsNotLeftFromTheOldPosition()
    {
        foreach (var name in new[] { "P", "A", "Q" }) _plant.Device(name);
        _plant.Link("P", "A");
        _plant.Link("Q", "A");
        var mission = await MissionOn("P", towards: "A");
        _plant.PlannedStep(mission, "P", "A");

        BackgroundWriter? writer = null;
        mission.MoveWritePauseForTesting = () =>
        {
            mission.MoveWritePauseForTesting = null;
            var fromP = Step(mission, "P", "A");
            writer = new BackgroundWriter(() => mission.TryWriteRoute(mission.RouteVersion, [fromP]));
            writer.WaitUntilBlockedOrFinished();
        };

        var outcome = MissionGateway.Move(mission.MissionId, _plant["Q"].ItemGuid);

        Assert.NotNull(writer);
        Assert.Equal(RouteWriteOutcome.Lost, await writer!.Finished.WaitAsync(Ceiling));
        Assert.Equal(MissionMoveOutcome.Moved, outcome);
        Assert.DoesNotContain(mission.MissionSteps, s => s.FromNodeId == _plant["P"].ItemGuid);
        AssertCoherent(mission, "Q", "A");
    }

    /// <summary>
    /// Il comando «sposta l'UDC su Q» per una missione che c'e' gia' toglie la rotta e cambia la
    /// posizione come lo spostamento dell'operatore, insieme: uno scrittore che legge la versione in
    /// mezzo, con la missione ancora su P, non scrive la sua rotta P->A, e la missione resta su Q senza
    /// rotte da P. Prima la rotta P->A restava alla missione spostata su Q.
    /// </summary>
    [Fact]
    public async Task ARouteWrittenWhileTheMoveCommandIsUnderWayIsNotLeftFromTheOldPosition()
    {
        foreach (var name in new[] { "P", "A", "Q" }) _plant.Device(name);
        _plant.Link("P", "A");
        _plant.Link("Q", "A");
        var mission = await MissionOn("P", towards: "A");
        _plant.PlannedStep(mission, "P", "A");
        _plant["P"].Status = DeviceStatusTypes.Ready;
        _plant["Q"].Status = DeviceStatusTypes.Ready;
        _warehouse.LoadUnits[LoadUnitId] = new LoadUnitDescription(LoadUnitId, "UDC-42", LoadUnitTypeId: 1,
            HandlingMode: 0, Tare: 0, Height: 0, Width: 0, Depth: 0, Weight: 0);

        // Il comando chiama ExecuteStep(1000) sui due dispositivi e non lo aspetta: la pausa la tiene la
        // prova, e dopo il ritorno a Stop le chiamate escono senza eseguire. Le tiene BackgroundWork, e la
        // prova aspetta che siano uscite prima di smontare l'impianto (rilievo M62 della revisione finale).
        var pauses = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        foreach (var name in new[] { "P", "Q" })
            _plant[name].ExecutionPauseForTesting = (_, token) => pauses.Task.WaitAsync(token);

        BackgroundWriter? writer = null;
        mission.MoveWritePauseForTesting = () =>
        {
            mission.MoveWritePauseForTesting = null;
            var fromP = Step(mission, "P", "A");
            writer = new BackgroundWriter(() => mission.TryWriteRoute(mission.RouteVersion, [fromP]));
            writer.WaitUntilBlockedOrFinished();
        };

        var command = new Command
        {
            CommandType = Command.CommandTypes.MoveLuToItem,
            LuId = LoadUnitId,
            ItemCommandId = _plant["Q"].ItemGuid
        };

        Navigator.NavStatus = NavigatorStatusTypes.Run;
        try
        {
            await ExecutableCommands.ManageMoveLuToItem(command, string.Empty).WaitAsync(Ceiling);
        }
        finally
        {
            Navigator.NavStatus = NavigatorStatusTypes.Stop;
            pauses.TrySetResult();
        }

        Assert.True(BackgroundWork.WaitUntilIdle(Ceiling), "le esecuzioni chieste dal comando non sono uscite");
        Assert.Equal(Command.StatusTypes.Done, command.Status);
        Assert.NotNull(writer);
        Assert.Equal(RouteWriteOutcome.Lost, await writer!.Finished.WaitAsync(Ceiling));
        Assert.Equal(_plant["Q"].ItemGuid, mission.PositionItemId);
        Assert.DoesNotContain(mission.MissionSteps, s => s.FromNodeId == _plant["P"].ItemGuid);
    }

    // ---------------------------------------------------------------------------------------
    // Corsa 6: la rimozione della strategia Standard
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Corsa 6. Q esegue e vede la sua UDC senza uno step da fare; prima che Q tolga la rotta, un
    /// costruttore scrive la rotta Q->X. Q la trova cambiata e la lascia, e non rimette la missione in
    /// coda: la rotta c'e'. Prima Q toglieva la rotta appena scritta e rimetteva la missione in coda, e
    /// il calcolo andava rifatto.
    /// </summary>
    [Fact]
    public async Task ARouteWrittenAfterTheDeviceSawNoStepToDoIsKept()
    {
        foreach (var name in new[] { "Q", "X" }) _plant.Device(name);
        _plant.Link("Q", "X");
        var mission = await MissionOn("Q", towards: "X");
        Assert.Same(mission, _plant["Q"].LoadedLu);
        Assert.Null(mission.StepToDo);

        var written = Step(mission, "Q", "X");
        _plant["Q"].NoStepToDoPauseForTesting = () =>
        {
            _plant["Q"].NoStepToDoPauseForTesting = null;
            mission.MissionSteps = [written];
        };

        Navigator.NavStatus = NavigatorStatusTypes.Run;
        try
        {
            await _plant["Q"].ExecuteStep().WaitAsync(Ceiling);
        }
        finally
        {
            Navigator.NavStatus = NavigatorStatusTypes.Stop;
        }

        Assert.Contains(written, mission.MissionSteps);
        Assert.Null(RoutesToBuild.Instance[mission.MissionId]);
    }

    /// <summary>
    /// Q vede la sua UDC senza uno step da fare; prima che Q tolga la rotta, la destinazione cambia in Y
    /// e nessuno scrive una rotta. La versione e' salita, quindi Q non toglie niente; ma la missione non
    /// ha rotta, e torna al giro delle rotte. Prima Q leggeva ogni versione cambiata come «la rotta l'ha
    /// scritta un altro»: la missione restava senza rotta e fuori dalla coda fino al prossimo
    /// <c>ExecuteStep</c> di Q (revisione finale della voce 41).
    /// </summary>
    [Fact]
    public async Task AVersionRaisedWithoutARouteAfterTheDeviceSawNoStepToDoQueuesTheMission()
    {
        foreach (var name in new[] { "Q", "X", "Y" }) _plant.Device(name);
        _plant.Link("Q", "X");
        _plant.Link("Q", "Y");
        var mission = await MissionOn("Q", towards: "X");
        Assert.Same(mission, _plant["Q"].LoadedLu);
        Assert.Null(mission.StepToDo);

        _plant["Q"].NoStepToDoPauseForTesting = () =>
        {
            _plant["Q"].NoStepToDoPauseForTesting = null;
            mission.DestinationItemId = _plant["Y"].ItemGuid;
        };

        Navigator.NavStatus = NavigatorStatusTypes.Run;
        try
        {
            await _plant["Q"].ExecuteStep().WaitAsync(Ceiling);
        }
        finally
        {
            Navigator.NavStatus = NavigatorStatusTypes.Stop;
        }

        Assert.Null(_plant["Q"].NoStepToDoPauseForTesting);
        Assert.Equal(_plant["Y"].ItemGuid, mission.DestinationItemId);
        Assert.Empty(mission.MissionSteps);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
    }

    /// <summary>
    /// Q vede la sua UDC senza uno step da fare; prima che Q tolga la rotta, quella missione passa su R e
    /// su Q arriva un'altra missione, con la sua rotta Q->X e la versione della rotta uguale a quella che
    /// Q aveva letto per la prima. Q non tocca la rotta della missione che non ha visto: la rotta Q->X
    /// resta. Senza lo sguardo a chi e' a bordo, Q confrontava la versione letta sulla prima con quella
    /// della seconda, e toglieva la rotta della seconda (rilievo M63 della revisione finale della voce 41).
    /// </summary>
    [Fact]
    public async Task AnotherMissionLoadedMeanwhileKeepsItsRoute()
    {
        foreach (var name in new[] { "Q", "X", "R", "Z" }) _plant.Device(name);
        _plant.Link("Q", "X");
        _plant.Link("Q", "R");
        _plant.Link("Z", "Q");
        var mission = await MissionOn("Q", towards: "X");
        var other = await MissionOn("Z", towards: "X", luId: LoadUnitId + 1);
        Assert.Same(mission, _plant["Q"].LoadedLu);
        Assert.Null(mission.StepToDo);

        // La versione della prima missione ben piu' alta di quella della seconda, che la cucitura porta
        // allo stesso valore.
        for (var i = 0; i < 50; i++) mission.MissionSteps = [];

        var otherStep = Step(other, "Q", "X");
        var sameVersion = false;
        var otherOnBoard = false;
        _plant["Q"].NoStepToDoPauseForTesting = () =>
        {
            // La cucitura non asserisce (ExecuteStep cattura le eccezioni): scrive quello che ha visto.
            _plant["Q"].NoStepToDoPauseForTesting = null;
            var observed = mission.RouteVersion;
            mission.PositionItemId = _plant["R"].ItemGuid;
            other.PositionItemId = _plant["Q"].ItemGuid;
            other.PositionWork.WaitAsync(Ceiling).GetAwaiter().GetResult();

            while (other.RouteVersion < observed - 1) other.MissionSteps = [];
            other.MissionSteps = [otherStep];

            sameVersion = other.RouteVersion == observed;
            otherOnBoard = ReferenceEquals(_plant["Q"].LoadedLu, other);
        };

        Navigator.NavStatus = NavigatorStatusTypes.Run;
        try
        {
            await _plant["Q"].ExecuteStep().WaitAsync(Ceiling);
        }
        finally
        {
            Navigator.NavStatus = NavigatorStatusTypes.Stop;
        }

        await mission.PositionWork.WaitAsync(Ceiling);
        Assert.Null(_plant["Q"].NoStepToDoPauseForTesting);
        Assert.True(sameVersion, "la cucitura non ha portato la seconda missione alla versione letta per la prima");
        Assert.True(otherOnBoard, "la seconda missione non e' a bordo di Q");
        Assert.Contains(otherStep, other.MissionSteps);
    }

    // ---------------------------------------------------------------------------------------
    // Corsa 7: il rigetto di gruppo
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Corsa 7. L'operatore rigetta le UDC del gruppo di P mentre lo step P->A della sua UDC e' in volo.
    /// Il rigetto di quella UDC si rimanda, come quello di <c>RejectTo</c>: lo step resta, con il
    /// gettone di P, e la destinazione e il tipo restano com'erano. Prima il rigetto di gruppo toglieva
    /// gli step prima di guardare, e cancellava lo step in volo (la decisione del 3/9 lo vieta).
    /// </summary>
    [Fact]
    public async Task AGroupRejectionLeavesAStepInFlightAlone()
    {
        foreach (var name in new[] { "P", "A", "R" }) _plant.Device(name);
        var group = _plant.Device("GP", DeviceTypes.GroupOfAutomationDevices);
        group.GroupItemId = 91;
        _plant["P"].BelongingGroupId = group.GroupItemId;
        Assert.Same(group, _plant["P"].BelongedGroup);
        _plant["P"].RejectDestinationUserRequest = _plant["R"].ItemGuid;
        _plant.Link("P", "A");
        _plant.Link("P", "R");
        var mission = await MissionOn("P", towards: "A");
        mission.Gateway = _plant["A"].ItemGuid;
        mission.DestinationAsi = "0042";
        var step = _plant.PlannedStep(mission, "P", "A");
        _plant["P"].RunningStepId = step.StepId;
        _plant["P"].Status = DeviceStatusTypes.Moving;
        Assert.Same(step, mission.StepInFlight());

        await group.RebuildGroupRejection().WaitAsync(Ceiling);

        Assert.Same(step, Assert.Single(mission.MissionSteps));
        Assert.Equal(step.StepId, _plant["P"].RunningStepId);
        Assert.Equal(_plant["A"].ItemGuid, mission.DestinationItemId);
        Assert.Equal(MissionTypes.MovingToItem, mission.MissionType);
        Assert.Equal(_plant["A"].ItemGuid, mission.Gateway);
        Assert.Equal("0042", mission.DestinationAsi);
    }

    /// <summary>
    /// L'operatore rigetta le UDC del gruppo di P, e nessuno step e' in volo: il rigetto di gruppo passa
    /// lo sguardo e chiama <c>RejectTo</c>. Mentre il rigetto legge la porta del punto di scarto, un altro
    /// scrittore scrive la rotta P->A e P ne avvia lo step. <c>RejectTo</c> si rimanda, e la missione
    /// resta com'era, gateway, indirizzi e filtro compresi. Prima il rigetto di gruppo li toglieva prima
    /// di chiamare <c>RejectTo</c>, e un rigetto rimandato li lasciava tolti a una missione con lo step in
    /// volo (rilievi M71 e M76 della revisione finale della voce 41).
    /// </summary>
    [Fact]
    public async Task AGroupRejectionPostponedDuringItsWaitsLeavesTheMissionAlone()
    {
        foreach (var name in new[] { "P", "A", "R" }) _plant.Device(name);
        var group = _plant.Device("GP", DeviceTypes.GroupOfAutomationDevices);
        group.GroupItemId = 92;
        _plant["P"].BelongingGroupId = group.GroupItemId;
        Assert.Same(group, _plant["P"].BelongedGroup);
        _plant["P"].RejectDestinationUserRequest = _plant["R"].ItemGuid;
        _plant.Link("P", "A");
        _plant.Link("P", "R");
        var mission = await MissionOn("P", towards: "A");
        mission.Gateway = _plant["A"].ItemGuid;
        mission.DestinationAsi = "0042";
        mission.ActiveFilter = true;
        Assert.Null(mission.StepInFlight());
        using var pause = new PauseOnce();
        mission.RejectionPauseForTesting = pause.Pause;

        var rejecting = group.RebuildGroupRejection();
        await pause.Reached.WaitAsync(Ceiling);

        var step = Step(mission, "P", "A");
        mission.MissionSteps = [step];
        _plant["P"].RunningStepId = step.StepId;
        Assert.Same(step, mission.StepInFlight());

        pause.Release();
        await rejecting.WaitAsync(Ceiling);

        Assert.Same(step, Assert.Single(mission.MissionSteps));
        Assert.Equal(_plant["A"].ItemGuid, mission.DestinationItemId);
        Assert.Equal(MissionTypes.MovingToItem, mission.MissionType);
        Assert.Equal(_plant["A"].ItemGuid, mission.Gateway);
        Assert.Equal("0042", mission.DestinationAsi);
        Assert.True(mission.ActiveFilter);
    }

    // ---------------------------------------------------------------------------------------
    // Il rigetto
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Il rigetto dell'operatore ha letto la porta del punto di scarto R e non ha ancora scritto la
    /// destinazione; intanto un altro scrittore scrive la rotta P->A e P avvia lo step. Lo sguardo allo
    /// step in volo si rifa' insieme alla scrittura: il rigetto si rimanda, come quando lo step era gia' in
    /// volo all'inizio, e la missione resta intera verso A, con lo step e il gettone di P. Prima la
    /// destinazione R la scartava il setter (lo step in volo), la rotta restava verso A, e la missione
    /// diventava un rigetto verso A.
    /// </summary>
    [Fact]
    public async Task ARejectionDuringWhichAStepStartsIsPostponed()
    {
        var (mission, step, rejected) =
            await ARejectionDuringWhichAStepStarts(RejectionTypes.DefaultNavigatorRejectionItem);

        Assert.False(rejected);
        Assert.Equal(MissionTypes.MovingToItem, mission.MissionType);
        AssertCoherent(mission, "P", "A");
        Assert.Same(step, Assert.Single(mission.MissionSteps));
        Assert.Equal(step.StepId, _plant["P"].RunningStepId);
    }

    /// <summary>
    /// L'operatore rigetta l'UDC su P, che ha la rotta P->A e aspetta che A si liberi. Mentre il rigetto
    /// chiude il comando e legge la porta, A si libera e P avvia lo step che trova. La rotta P->A il
    /// rigetto l'ha gia' tolta prima delle attese, come prima della voce 41: P non ha niente da avviare, e
    /// la missione e' un rigetto intero verso R. Tolta solo sotto il lucchetto, dopo le attese, P avviava
    /// P->A, e il rigetto si rimandava con il comando gia' chiuso: nessuno lo richiedeva, e l'UDC andava ad
    /// A (primo giro di correzioni del compito 4). Lo stesso il rigetto dall'evento del campo, che la
    /// rotta la toglieva prima di leggere la porta: tolta solo sotto il lucchetto, P avviava P->A durante
    /// la lettura, e il rigetto, che allora gli step in volo non li guardava, toglieva lo step in volo e
    /// liberava A a meta' movimento (secondo giro di correzioni del compito 4).
    /// </summary>
    [Theory]
    [InlineData(RejectionTypes.DefaultNavigatorRejectionItem)]
    [InlineData(RejectionTypes.NoLuDataFromPlc)]
    public async Task TheMissionsOwnRouteDoesNotStartWhileTheRejectionWaits(RejectionTypes type)
    {
        var mission = await AMissionOnPWithTheRejectionPointR();
        var pending = _plant.PlannedStep(mission, "P", "A");
        Assert.Null(mission.StepInFlight());
        using var pause = new PauseOnce();
        mission.RejectionPauseForTesting = pause.Pause;

        var rejecting = mission.RejectTo(type);
        await pause.Reached.WaitAsync(Ceiling);

        // A si libera: P avvia lo step della missione che parte da lui, se c'e' ancora.
        var started = 0;
        foreach (var step in mission.MissionSteps.Where(s => s.FromNodeId == _plant["P"].ItemGuid))
        {
            _plant["P"].RunningStepId = step.StepId;
            started++;
        }

        pause.Release();

        Assert.Equal(0, started);
        Assert.True(await rejecting.WaitAsync(Ceiling));
        Assert.Equal(MissionTypes.Rejection, mission.MissionType);
        AssertCoherent(mission, "P", "R");
        Assert.DoesNotContain(pending, mission.MissionSteps);
    }

    /// <summary>
    /// Lo stesso con il rigetto dall'evento del campo sui dati dell'UDC: lo step in volo vince anche su di
    /// lui (risposta 1 del proprietario dell'11/10, come la decisione del 3/9). Il rigetto si rimanda, la
    /// missione resta intera verso A con lo step e il gettone di P, e torna al giro delle rotte, che lo
    /// rifa' quando lo step e' finito. Prima questo rigetto gli step in volo non li guardava: toglieva lo
    /// step partito, liberando P a meta' movimento, e la missione diventava un rigetto verso R.
    /// </summary>
    [Fact]
    public async Task AFieldRejectionDuringWhichAStepStartsLeavesTheStepInFlight()
    {
        var (mission, step, rejected) = await ARejectionDuringWhichAStepStarts(RejectionTypes.NoLuDataFromPlc);

        Assert.False(rejected);
        Assert.Equal(MissionTypes.MovingToItem, mission.MissionType);
        AssertCoherent(mission, "P", "A");
        Assert.Same(step, Assert.Single(mission.MissionSteps));
        Assert.Equal(step.StepId, _plant["P"].RunningStepId);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
    }

    /// <summary>
    /// Il rigetto dal campo arriva con lo step P->A gia' in volo: non lo toglie, ne' all'ingresso ne'
    /// dopo, e si rimanda; la missione resta verso A con lo step e il gettone di P, ed e' in coda al giro
    /// delle rotte. Prima toglieva la rotta all'ingresso senza guardare, e lo step in volo con lei.
    /// </summary>
    [Fact]
    public async Task AFieldRejectionWithAStepAlreadyInFlightLeavesItAlone()
    {
        var mission = await AMissionOnPWithTheRejectionPointR();
        var step = _plant.PlannedStep(mission, "P", "A");
        _plant["P"].RunningStepId = step.StepId;
        Assert.Equal(step.StepId, _plant["P"].RunningStepId);

        Assert.False(await mission.RejectTo(RejectionTypes.NoLuDataFromPlc).WaitAsync(Ceiling));

        Assert.Equal(MissionTypes.MovingToItem, mission.MissionType);
        AssertCoherent(mission, "P", "A");
        Assert.Same(step, Assert.Single(mission.MissionSteps));
        Assert.Equal(step.StepId, _plant["P"].RunningStepId);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
    }

    /// <summary>
    /// Il rigetto dal campo rimandato per lo step P->A in volo lo rifa' il giro delle rotte quando lo step
    /// e' finito, dalla posizione nuova: l'UDC e' arrivata su A, che manda i dati dell'UDC sbagliati al suo
    /// punto di scarto R2, e la missione diventa un rigetto verso R2, con la rotta A->R2. Prima del rigetto
    /// rimandato nessuno lo rifaceva.
    /// </summary>
    [Fact]
    public async Task ThePostponedFieldRejectionIsRedoneByTheRouteLoopWhenTheStepIsOver()
    {
        var mission = await AMissionOnPWithTheRejectionPointR();
        _plant.Device("R2");
        _plant.Link("A", "R2");
        _plant["A"].RejectItemDestinationIdGetLuInfo = _plant["R2"].ItemGuid;
        var step = _plant.PlannedStep(mission, "P", "A");
        _plant["P"].RunningStepId = step.StepId;
        Assert.False(await mission.RejectTo(RejectionTypes.NoLuDataFromPlc).WaitAsync(Ceiling));

        // Lo step finisce: il gettone si libera, lo step esce dalla rotta e l'UDC e' su A.
        _plant["P"].RunningStepId = Guid.Empty;
        MissionSteps.Instance.Remove(step);
        mission.PositionItemId = _plant["A"].ItemGuid;
        await mission.PositionWork.WaitAsync(Ceiling);
        Assert.Null(mission.StepInFlight());

        StartCellSearch(mission, _plant["A"]);
        await (Navigator.CellSearchInFlight(mission.MissionId) ?? Task.CompletedTask).WaitAsync(Ceiling);

        Assert.Equal(MissionTypes.Rejection, mission.MissionType);
        AssertCoherent(mission, "A", "R2");
    }

    /// <summary>
    /// La missione va da P a B passando per A (rotta P->A->B), e il rigetto dal campo trova lo step P->A
    /// in volo: gia' partito quando arriva, o partito mentre il rigetto legge la porta. Il rigetto si
    /// rimanda e della rotta resta solo lo step in volo, con il gettone di P: A->B e' tolto, e quando l'UDC
    /// arriva su A nessun dispositivo la manda avanti verso B prima che il giro rifaccia il rigetto. Prima
    /// la rotta restava intera, A avviava A->B appena finito P->A, il giro trovava sempre uno step in volo,
    /// e l'UDC senza i dati del PLC arrivava a B (primo giro di correzioni del compito 8 della voce 41).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APostponedFieldRejectionLeavesOnlyTheStepInFlight(bool startsDuringTheWait)
    {
        var mission = await AMissionOnPTowardsBThroughA();
        MissionStep stepPa;

        if (startsDuringTheWait)
        {
            using var pause = new PauseOnce();
            mission.RejectionPauseForTesting = pause.Pause;
            var rejecting = mission.RejectTo(RejectionTypes.NoLuDataFromPlc);
            await pause.Reached.WaitAsync(Ceiling);

            stepPa = Step(mission, "P", "A");
            mission.MissionSteps = [stepPa, Step(mission, "A", "B")];
            _plant["P"].RunningStepId = stepPa.StepId;
            Assert.Same(stepPa, mission.StepInFlight());

            pause.Release();
            Assert.False(await rejecting.WaitAsync(Ceiling));
        }
        else
        {
            stepPa = _plant.PlannedStep(mission, "P", "A");
            _plant.PlannedStep(mission, "A", "B");
            _plant["P"].RunningStepId = stepPa.StepId;
            Assert.Equal(2, mission.MissionSteps.Count);
            Assert.False(await mission.RejectTo(RejectionTypes.NoLuDataFromPlc).WaitAsync(Ceiling));
        }

        Assert.Same(stepPa, Assert.Single(mission.MissionSteps));
        Assert.Equal(stepPa.StepId, _plant["P"].RunningStepId);
        Assert.Equal(_plant["B"].ItemGuid, mission.DestinationItemId);
        Assert.Equal(MissionTypes.MovingToItem, mission.MissionType);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
    }

    /// <summary>
    /// Lo stesso rigetto rimandato, e lo step P->A finisce: l'UDC e' su A, verso B. Una rotta calcolata da
    /// chiunque altro (qui la costruzione che fa un dispositivo, <c>BuildPathToDestination</c>) non si
    /// scrive finche' il giro delle rotte non ha rifatto il rigetto: risponde come chi ha perso, e la
    /// missione resta senza rotta e in coda. Poi il giro rifa' il rigetto da A, verso il suo punto di
    /// scarto R2. Prima la rotta A->B si scriveva, il dispositivo la avviava, e il rigetto scivolava di
    /// nuovo dietro uno step in volo (primo giro di correzioni del compito 8 della voce 41).
    /// </summary>
    [Fact]
    public async Task NoRouteIsWrittenWhileThePostponedFieldRejectionWaitsForTheRouteLoop()
    {
        var mission = await AMissionOnPTowardsBThroughA();
        _plant.Device("R2");
        _plant.Link("A", "R2");
        _plant["A"].RejectItemDestinationIdGetLuInfo = _plant["R2"].ItemGuid;
        var stepPa = _plant.PlannedStep(mission, "P", "A");
        _plant.PlannedStep(mission, "A", "B");
        _plant["P"].RunningStepId = stepPa.StepId;
        Assert.False(await mission.RejectTo(RejectionTypes.NoLuDataFromPlc).WaitAsync(Ceiling));

        // Lo step finisce: il gettone si libera, lo step esce dalla rotta e l'UDC e' su A.
        _plant["P"].RunningStepId = Guid.Empty;
        MissionSteps.Instance.Remove(stepPa);
        mission.PositionItemId = _plant["A"].ItemGuid;
        await mission.PositionWork.WaitAsync(Ceiling);
        Assert.Null(mission.StepInFlight());

        Assert.True(await mission.BuildPathToDestination().WaitAsync(Ceiling));
        Assert.Empty(mission.MissionSteps);
        Assert.Equal(_plant["B"].ItemGuid, mission.DestinationItemId);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);

        StartCellSearch(mission, _plant["A"]);
        await (Navigator.CellSearchInFlight(mission.MissionId) ?? Task.CompletedTask).WaitAsync(Ceiling);

        Assert.Equal(MissionTypes.Rejection, mission.MissionType);
        AssertCoherent(mission, "A", "R2");
    }

    /// <summary>
    /// Lo stesso rigetto rimandato, con lo step P->A finito e l'UDC su A: l'operatore manda la missione
    /// verso B, che le aveva dato la configurazione. Il commit non scrive, perche' la missione aspetta il
    /// rigetto che il giro rifa', e il rifiuto dice proprio questo. Fino al secondo giro di correzioni del
    /// compito 8 della voce 41 la guardia rispondeva come chi ha perso il confronto della versione, e
    /// l'operatore leggeva che la rotta era cambiata mentre il cambio era in corso: nessuno l'aveva
    /// cambiata.
    /// </summary>
    [Fact]
    public async Task AChangeWhileThePostponedFieldRejectionWaitsSaysSo()
    {
        var mission = await AMissionOnAWaitingForThePostponedFieldRejection();
        MissionDestinationResult result;
        IReadOnlyList<string> lines;

        // La riga del rifiuto e' un avviso: la console la riceve prima che il cambio torni.
        using (var console = new RecordingConsole())
        {
            result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["B"].ItemGuid)
                .WaitAsync(Ceiling);
            lines = console.Lines;
        }

        Assert.Equal(MissionDestinationOutcome.StepInFlight, result.Outcome);
        Assert.Empty(mission.MissionSteps);
        Assert.False(mission.OperatorDestination);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
        var refusal = Assert.Single(lines, line => line.Contains(mission.MissionId.ToString()) &&
                                                   line.Contains("destinazione non cambiata"));
        Assert.Contains("la missione aspetta il rigetto dal campo", refusal);
        Assert.DoesNotContain("la rotta della missione e' cambiata", refusal);
    }

    /// <summary>
    /// La scena delle prove del rigetto rimandato qui sopra: la missione su P va verso B per A (P->A->B),
    /// e il rigetto dal campo trova lo step P->A in volo e si rimanda; poi lo step finisce, e l'UDC e' su
    /// A senza rotta, con il segno del rigetto da rifare e in coda al giro delle rotte. Da A il punto di
    /// scarto per i dati dell'UDC e' R2.
    /// </summary>
    private async Task<PathMission> AMissionOnAWaitingForThePostponedFieldRejection()
    {
        var mission = await AMissionOnPTowardsBThroughA();
        _plant.Device("R2");
        _plant.Link("A", "R2");
        _plant["A"].RejectItemDestinationIdGetLuInfo = _plant["R2"].ItemGuid;
        var stepPa = _plant.PlannedStep(mission, "P", "A");
        _plant.PlannedStep(mission, "A", "B");
        _plant["P"].RunningStepId = stepPa.StepId;
        Assert.False(await mission.RejectTo(RejectionTypes.NoLuDataFromPlc).WaitAsync(Ceiling));

        _plant["P"].RunningStepId = Guid.Empty;
        MissionSteps.Instance.Remove(stepPa);
        mission.PositionItemId = _plant["A"].ItemGuid;
        await mission.PositionWork.WaitAsync(Ceiling);
        Assert.Null(mission.StepInFlight());
        Assert.Empty(mission.MissionSteps);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
        return mission;
    }

    /// <summary>
    /// Il comando «sposta l'UDC su Q» per una missione con lo step P->A in volo: lo step in volo vince
    /// (risposta 1 del proprietario dell'11/10). Il comando si chiude in errore con la riga, e la missione
    /// resta su P verso A, con lo step e il gettone di P; l'operatore lo rimanda quando il movimento e'
    /// finito. Prima il comando toglieva lo step in volo e spostava la missione su Q.
    /// </summary>
    [Fact]
    public async Task AMoveCommandLeavesAStepInFlightAlone()
    {
        foreach (var name in new[] { "P", "A", "Q" }) _plant.Device(name);
        _plant.Link("P", "A");
        _plant.Link("Q", "A");
        var mission = await MissionOn("P", towards: "A");
        var step = _plant.PlannedStep(mission, "P", "A");
        _plant["P"].Status = DeviceStatusTypes.Ready;
        _plant["Q"].Status = DeviceStatusTypes.Ready;
        _plant["P"].RunningStepId = step.StepId;
        Assert.Equal(step.StepId, _plant["P"].RunningStepId);
        _warehouse.LoadUnits[LoadUnitId] = new LoadUnitDescription(LoadUnitId, "UDC-42", LoadUnitTypeId: 1,
            HandlingMode: 0, Tare: 0, Height: 0, Width: 0, Depth: 0, Weight: 0);

        var command = new Command
        {
            CommandType = Command.CommandTypes.MoveLuToItem,
            LuId = LoadUnitId,
            ItemCommandId = _plant["Q"].ItemGuid
        };

        Navigator.NavStatus = NavigatorStatusTypes.Run;
        try
        {
            await ExecutableCommands.ManageMoveLuToItem(command, string.Empty).WaitAsync(Ceiling);
        }
        finally
        {
            Navigator.NavStatus = NavigatorStatusTypes.Stop;
        }

        Assert.Equal(Command.StatusTypes.Error, command.Status);
        Assert.Contains("in flight", command.ErrorDescription);
        AssertCoherent(mission, "P", "A");
        Assert.Same(step, Assert.Single(mission.MissionSteps));
        Assert.Equal(step.StepId, _plant["P"].RunningStepId);
    }

    /// <summary>
    /// Il rigetto della configurazione (quello che cede alla scelta dell'operatore) ha letto la porta del
    /// punto di scarto R; intanto l'operatore manda l'UDC verso X e il suo commit scrive la rotta P->X. Il
    /// rigetto non si fa, e la missione resta come l'ha lasciata l'operatore: destinazione X con il
    /// segno, e la rotta P->X intera. Prima il rigetto toglieva la rotta e poi guardava il segno: la
    /// missione restava verso X senza rotta, con l'esito «rifiutato» (revisione finale della voce 41).
    /// </summary>
    [Fact]
    public async Task AnOperatorChoiceDuringTheRejectionKeepsItsRoute()
    {
        var mission = await AMissionOnPWithTheRejectionPointR();
        using var pause = new PauseOnce();
        mission.RejectionPauseForTesting = pause.Pause;

        var rejecting = mission.RejectTo(RejectionTypes.DefaultNavigatorRejectionItem, unlessChosenByOperator: true);
        await pause.Reached.WaitAsync(Ceiling);

        Assert.True(mission.SetOperatorDestination(_plant["X"].ItemGuid));
        var chosen = Step(mission, "P", "X");
        mission.MissionSteps = [chosen];

        pause.Release();

        Assert.False(await rejecting.WaitAsync(Ceiling));
        Assert.True(mission.OperatorDestination);
        Assert.Equal(MissionTypes.MovingToItem, mission.MissionType);
        AssertCoherent(mission, "P", "X");
        Assert.Same(chosen, Assert.Single(mission.MissionSteps));
    }

    /// <summary>
    /// Il rigetto scrive la destinazione R e calcola la rotta P->R; prima che la scriva, un altro
    /// scrittore manda la missione verso X e le scrive la rotta P->X. Il rigetto perde il confronto: non
    /// e' un rigetto, il tipo resta com'era, e la missione, con la destinazione e la rotta di chi ha
    /// vinto, e' in coda al giro delle rotte. Prima il rigetto contava gli step che trovava, quelli
    /// dell'altro, e faceva della missione verso X un rigetto.
    /// </summary>
    [Theory]
    [InlineData(RejectionTypes.DefaultNavigatorRejectionItem)]
    [InlineData(RejectionTypes.NoLuDataFromPlc)]
    public async Task ARejectionWhoseRouteIsTakenOverIsNotARejection(RejectionTypes type)
    {
        var mission = await AMissionOnPWithTheRejectionPointR();
        using var pause = new PauseOnce();
        mission.RouteBuildPauseForTesting = pause.Pause;

        var rejecting = mission.RejectToAsync(type);
        await pause.Reached.WaitAsync(Ceiling);
        Assert.Equal(_plant["R"].ItemGuid, mission.DestinationItemId);

        mission.DestinationItemId = _plant["X"].ItemGuid;
        mission.MissionSteps = [Step(mission, "P", "X")];

        pause.Release();

        Assert.Equal(RejectionOutcome.TakenOver, await rejecting.WaitAsync(Ceiling));
        Assert.Equal(MissionTypes.MovingToItem, mission.MissionType);
        AssertCoherent(mission, "P", "X");
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
    }

    /// <summary>
    /// Il rigetto scrive la destinazione R e calcola la rotta P->R; prima che la scriva, l'operatore sposta
    /// l'UDC su Q. Il rigetto perde la costruzione, ma la destinazione e' ancora R: si completa dalla
    /// posizione nuova (risposta 5 del proprietario dell'11/10). La missione e' un rigetto verso R, in coda
    /// al giro delle rotte, che le costruisce la rotta Q->R anche se Q, di suo, la manderebbe alla sua
    /// destinazione fissa X. Prima il rigetto rispondeva falso e restava a meta': destinazione R, tipo di
    /// prima, e il giro la mandava dove diceva Q.
    /// </summary>
    [Theory]
    [InlineData(RejectionTypes.DefaultNavigatorRejectionItem)]
    [InlineData(RejectionTypes.NoLuDataFromPlc)]
    public async Task ARejectionWhoseRouteIsLostToAMoveIsCompletedFromTheNewPosition(RejectionTypes type)
    {
        var mission = await AMissionOnPWithTheRejectionPointR();
        _plant.Device("Q");
        _plant.Link("Q", "R");
        _plant.Link("Q", "X");
        _plant["Q"].DestinationItemId = _plant["X"].ItemGuid;
        using var pause = new PauseOnce();
        mission.RouteBuildPauseForTesting = pause.Pause;

        var rejecting = mission.RejectToAsync(type);
        await pause.Reached.WaitAsync(Ceiling);
        Assert.Equal(_plant["R"].ItemGuid, mission.DestinationItemId);

        Assert.Equal(MissionMoveOutcome.Moved, MissionGateway.Move(mission.MissionId, _plant["Q"].ItemGuid));

        pause.Release();

        Assert.Equal(RejectionOutcome.Rejected, await rejecting.WaitAsync(Ceiling));
        Assert.Equal(MissionTypes.Rejection, mission.MissionType);
        Assert.Equal(_plant["R"].ItemGuid, mission.DestinationItemId);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);

        StartCellSearch(mission, _plant["Q"]);
        await (Navigator.CellSearchInFlight(mission.MissionId) ?? Task.CompletedTask).WaitAsync(Ceiling);

        Assert.Equal(MissionTypes.Rejection, mission.MissionType);
        AssertCoherent(mission, "Q", "R");
    }

    /// <summary>
    /// Il rigetto dal campo rimandato, e il giro che lo rifa' si guasta a meta' (qui un'eccezione nella
    /// cucitura del rigetto, dopo la lettura della porta e prima della scrittura). Il rigetto non si perde:
    /// il segno torna, e il giro dopo lo rifa' da A verso R2. Fino al giro finale delle correzioni della
    /// voce 41 il segno si toglieva prima del rigetto e non tornava: il giro dopo non aveva niente da
    /// rifare, e l'UDC senza i dati del PLC andava avanti verso B.
    /// </summary>
    [Fact]
    public async Task APostponedFieldRejectionThatFailsInTheRouteLoopIsNotLost()
    {
        var mission = await AMissionOnAWaitingForThePostponedFieldRejection();
        mission.RejectionPauseForTesting = () => Task.FromException(new InvalidOperationException("guasto della prova"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => mission.RedoInTheRouteLoopAsync().WaitAsync(Ceiling));
        mission.RejectionPauseForTesting = null;

        Assert.Equal(AutomationDevice.RouteBuildStatusTypes.Available,
            await mission.RedoInTheRouteLoopAsync().WaitAsync(Ceiling));
        Assert.Equal(MissionTypes.Rejection, mission.MissionType);
        AssertCoherent(mission, "A", "R2");
    }

    /// <summary>
    /// Il rigetto dal campo rimandato che il giro rifa' non ripete la sua prima parte: l'avviso «LU
    /// rejected because ...» sul registro del navigatore l'ha scritto la prima chiamata, e il giro che
    /// porta l'UDC da A allo scarto R2 non ne scrive un altro (ne' chiude di nuovo il comando). Fino al
    /// giro finale delle correzioni della voce 41 ogni rinvio rifaceva il rigetto dall'inizio.
    /// </summary>
    [Fact]
    public async Task APostponedFieldRejectionRedoneByTheRouteLoopDoesNotRepeatItsFirstPart()
    {
        var mission = await AMissionOnAWaitingForThePostponedFieldRejection();
        using var history = WcsHistoryDatabase.InMemory();
        var previousHistory = Navigator.History;
        Navigator.History = history;
        try
        {
            Assert.Equal(AutomationDevice.RouteBuildStatusTypes.Available,
                await mission.RedoInTheRouteLoopAsync().WaitAsync(Ceiling));
        }
        finally
        {
            Navigator.History = previousHistory;
        }

        Assert.Equal(MissionTypes.Rejection, mission.MissionType);
        AssertCoherent(mission, "A", "R2");
        using var context = history.CreateContext();
        Assert.Equal(0, context.NavigatorLog.Count(e => e.Description!.Contains("rejected because")));
    }

    /// <summary>
    /// Il rigetto dell'addetto (<c>RejectedFromAttendant</c>) rimandato per lo step P->A in volo; l'UDC
    /// arriva su A, che non ha un punto di scarto per i dati dell'UDC. Il giro rifa' il rigetto con lo
    /// stesso motivo, non si puo' fare da A, e un avviso lo dice, con il motivo: la missione va avanti verso
    /// B. Fino al giro finale delle correzioni della voce 41 il giro lo rifaceva sempre come
    /// <c>NoLuDataFromPlc</c>, e il rigetto caduto lasciava solo la riga della prima chiamata, scritta da P.
    /// </summary>
    [Fact]
    public async Task APostponedRejectionFromTheAttendantThatCannotBeRedoneSaysSo()
    {
        var mission = await AMissionOnPTowardsBThroughA();
        var stepPa = _plant.PlannedStep(mission, "P", "A");
        _plant.PlannedStep(mission, "A", "B");
        _plant["P"].RunningStepId = stepPa.StepId;
        Assert.Equal(RejectionOutcome.Postponed,
            await mission.RejectToAsync(RejectionTypes.RejectedFromAttendant).WaitAsync(Ceiling));

        _plant["P"].RunningStepId = Guid.Empty;
        MissionSteps.Instance.Remove(stepPa);
        mission.PositionItemId = _plant["A"].ItemGuid;
        await mission.PositionWork.WaitAsync(Ceiling);
        Assert.Null(mission.StepInFlight());

        AutomationDevice.RouteBuildStatusTypes? redone;
        IReadOnlyList<string> lines;
        using (var console = new RecordingConsole())
        {
            redone = await mission.RedoInTheRouteLoopAsync().WaitAsync(Ceiling);
            lines = console.Lines;
        }

        Assert.Null(redone);
        Assert.Contains(lines, line => line.Contains("RejectedFromAttendant") && line.Contains("non fatto"));
        Assert.Equal(_plant["B"].ItemGuid, mission.DestinationItemId);
        Assert.NotEqual(MissionTypes.Rejection, mission.MissionType);
    }

    // ---------------------------------------------------------------------------------------
    // Il distributore circolare senza la scelta dell'operatore
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// L'UDC su P non ha i dati del PLC mentre lo step P->D e' gia' in volo, e D e' un distributore
    /// circolare: il rigetto dal campo si rimanda. Lo step finisce e l'UDC e' su D. Il distributore non le
    /// scrive la prima uscita libera (E1) ne' lo step verso di lei: la missione aspetta il rigetto che il
    /// giro rifa', e il giro lo rifa' da D, verso il suo punto di scarto R2. Fino al giro finale delle
    /// correzioni della voce 41 il distributore scriveva destinazione e step a mano, senza guardare il
    /// segno del rigetto rimandato: l'UDC usciva da E1 e il rigetto scivolava di un altro passo.
    /// </summary>
    [Fact]
    public async Task ACircularDistributorDoesNotWriteItsExitWhileThePostponedFieldRejectionWaits()
    {
        var mission = await AMissionOnPTowardsFThroughTheDistributorD();
        _plant.Device("R2").PriorityInRoutePath = 2;
        _plant.Link("D", "R2");
        _plant["D"].RejectItemDestinationIdGetLuInfo = _plant["R2"].ItemGuid;
        var stepPd = _plant.PlannedStep(mission, "P", "D");
        _plant.PlannedStep(mission, "D", "E1");
        _plant["P"].RunningStepId = stepPd.StepId;
        Assert.False(await mission.RejectTo(RejectionTypes.NoLuDataFromPlc).WaitAsync(Ceiling));

        _plant["P"].RunningStepId = Guid.Empty;
        MissionSteps.Instance.Remove(stepPd);
        mission.PositionItemId = _plant["D"].ItemGuid;
        await mission.PositionWork.WaitAsync(Ceiling);
        Assert.Same(mission, _plant["D"].LoadedLu);

        Assert.NotNull(_plant["D"].StepExecutor);
        Assert.False(_plant["D"].StepExecutor!.ProcessCircularDistributionLu());

        Assert.Empty(mission.MissionSteps);
        Assert.Equal(_plant["F"].ItemGuid, mission.DestinationItemId);
        ChangeDestinationStepStartsTest.NoStepStartIsLeftCounted(mission);

        StartCellSearch(mission, _plant["D"]);
        await (Navigator.CellSearchInFlight(mission.MissionId) ?? Task.CompletedTask).WaitAsync(Ceiling);

        Assert.Equal(MissionTypes.Rejection, mission.MissionType);
        AssertCoherent(mission, "D", "R2");
    }

    /// <summary>
    /// L'UDC senza scelta va gia' verso E1, la prima uscita libera, con la rotta D->E1. Uno scrittore legge la
    /// versione della rotta e la calcola (qui la rotta D->X, che non porta alla destinazione); intanto il
    /// distributore riscrive destinazione e step verso E1. La scrittura del distributore fa salire la
    /// versione: lo scrittore trova la versione cambiata e non scrive sopra la decisione del distributore.
    /// Fino al giro finale delle correzioni della voce 41 il distributore toglieva e rimetteva lo step a
    /// mano, senza la versione (con la stessa destinazione nemmeno il setter la faceva salire), e la rotta
    /// calcolata prima si scriveva: la missione restava verso E1 con la rotta D->X.
    /// </summary>
    [Fact]
    public async Task ARouteComputedBeforeTheDistributorWritesItsExitIsNotWritten()
    {
        var mission = await AMissionOnPTowardsFThroughTheDistributorD(onD: true);
        _plant.Device("X").PriorityInRoutePath = 2;
        _plant.Link("D", "X");
        mission.DestinationItemId = _plant["E1"].ItemGuid;
        _plant.PlannedStep(mission, "D", "E1");
        var seen = mission.RouteVersion;
        var computedBefore = Step(mission, "D", "X");

        Assert.NotNull(_plant["D"].StepExecutor);
        Assert.True(_plant["D"].StepExecutor!.ProcessCircularDistributionLu());

        Assert.Equal(RouteWriteOutcome.Lost, mission.TryWriteRoute(seen, [computedBefore]));
        AssertCoherent(mission, "D", "E1");
        Assert.Single(mission.MissionSteps);
        ChangeDestinationStepStartsTest.NoStepStartIsLeftCounted(mission);
    }

    // ---------------------------------------------------------------------------------------
    // Scene
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Il rigetto si ferma con la porta del punto di scarto letta e la destinazione non ancora scritta;
    /// intanto un altro scrittore scrive la rotta P->A e P ne avvia lo step (il gettone di P, che e'
    /// <c>Ready</c>: in volo). Poi il rigetto riparte, e la prova ne aspetta la fine.
    /// </summary>
    private async Task<(PathMission Mission, MissionStep Step, bool Rejected)> ARejectionDuringWhichAStepStarts(
        RejectionTypes type)
    {
        var mission = await AMissionOnPWithTheRejectionPointR();
        using var pause = new PauseOnce();
        mission.RejectionPauseForTesting = pause.Pause;

        var rejecting = mission.RejectTo(type);
        await pause.Reached.WaitAsync(Ceiling);

        var step = Step(mission, "P", "A");
        mission.MissionSteps = [step];
        _plant["P"].RunningStepId = step.StepId;
        Assert.Same(step, mission.StepInFlight());

        pause.Release();
        return (mission, step, await rejecting.WaitAsync(Ceiling));
    }

    /// <summary>
    /// P collegato ad A, R e X; R e' il punto di scarto di P, sia per il rigetto dell'operatore sia per
    /// quello dall'evento del campo. Su P una missione verso A, fra le attive e senza rotta.
    /// </summary>
    private async Task<PathMission> AMissionOnPWithTheRejectionPointR()
    {
        foreach (var name in new[] { "P", "A", "R", "X" }) _plant.Device(name);
        _plant.Link("P", "A");
        _plant.Link("P", "R");
        _plant.Link("P", "X");
        _plant["P"].RejectDestinationUserRequest = _plant["R"].ItemGuid;
        _plant["P"].RejectItemDestinationIdGetLuInfo = _plant["R"].ItemGuid;
        return await MissionOn("P", towards: "A");
    }

    /// <summary>
    /// Come <see cref="AMissionOnPWithTheRejectionPointR"/>, con B dopo A: la missione su P va verso B,
    /// fra le attive e senza rotta.
    /// </summary>
    private async Task<PathMission> AMissionOnPTowardsBThroughA()
    {
        foreach (var name in new[] { "P", "A", "B", "R", "X" }) _plant.Device(name);
        _plant.Link("P", "A");
        _plant.Link("A", "B");
        _plant.Link("P", "R");
        _plant.Link("P", "X");
        _plant["P"].RejectDestinationUserRequest = _plant["R"].ItemGuid;
        _plant["P"].RejectItemDestinationIdGetLuInfo = _plant["R"].ItemGuid;
        return await MissionOn("P", towards: "B");
    }

    /// <summary>
    /// P collegato al distributore circolare D, D alla sua uscita E1 (priorita' 1), E1 a F; da P il punto
    /// di scarto per i dati dell'UDC e' R. La missione va verso F, fra le attive e senza rotta, su P o, con
    /// <paramref name="onD"/>, gia' su D.
    /// </summary>
    private async Task<PathMission> AMissionOnPTowardsFThroughTheDistributorD(bool onD = false)
    {
        foreach (var name in new[] { "P", "E1", "F", "R" }) _plant.Device(name);
        _plant.Device("D", DeviceTypes.CircularDistributor);
        _plant["E1"].PriorityInRoutePath = 1;
        _plant.Link("P", "D");
        _plant.Link("D", "E1");
        _plant.Link("E1", "F");
        _plant.Link("P", "R");
        _plant["P"].RejectItemDestinationIdGetLuInfo = _plant["R"].ItemGuid;
        return await MissionOn(onD ? "D" : "P", towards: "F");
    }

    /// <summary>
    /// Una missione su <paramref name="on"/> verso <paramref name="towards"/>, fra le attive e senza
    /// rotta; il motore e' fermo. L'ingresso si fa in Run, con <c>AutoExecuteStep</c> spento.
    /// </summary>
    private async Task<PathMission> MissionOn(string on, string towards, decimal luId = LoadUnitId)
    {
        var mission = new PathMission
        {
            PositionItemId = _plant[on].ItemGuid,
            DestinationItemId = _plant[towards].ItemGuid,
            LuId = luId,
            MissionType = MissionTypes.MovingToItem
        };
        await mission.PositionWork;

        foreach (var name in _plant.DeviceNames) _plant[name].AutoExecuteStep = false;
        Navigator.NavStatus = NavigatorStatusTypes.Run;
        ActiveMissions.Instance.Add(mission, buildRoute: false);
        Assert.Same(mission, ActiveMissions.Instance[mission.MissionId]);
        await mission.PositionWork;
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        return mission;
    }

    /// <summary>
    /// Il giro delle rotte avvia la ricerca della missione (<c>StartCellSearch</c>, come la avvia
    /// <c>BuildRoutePath</c>), che gira in sottofondo.
    /// </summary>
    private static void StartCellSearch(PathMission mission, AutomationDevice position)
    {
        var method = typeof(Navigator).GetMethod("StartCellSearch",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        method!.Invoke(null, [mission, position]);
    }

    /// <summary>Uno step della missione da <paramref name="from"/> a <paramref name="to"/>, non ancora nella collezione.</summary>
    private MissionStep Step(PathMission mission, string from, string to) => new()
    {
        StepId = Guid.NewGuid(),
        MissionId = mission.MissionId,
        Date = DateTime.Now,
        FromNodeId = _plant[from].ItemGuid,
        ToNodeId = _plant[to].ItemGuid,
        FromGroupRoutePath = _plant[from].GroupRoutePathId
    };

    /// <summary>
    /// La rotta e' coerente (§1 della ricognizione): parte dalla posizione, gli step vanno in fila senza
    /// due con la stessa sorgente, e l'ultimo arriva sulla destinazione, che e' quella attesa.
    /// </summary>
    private void AssertCoherent(PathMission mission, string from, string to)
    {
        Assert.Equal(_plant[from].ItemGuid, mission.PositionItemId);
        Assert.Equal(_plant[to].ItemGuid, mission.DestinationItemId);

        var steps = mission.MissionSteps;
        Assert.NotEmpty(steps);
        Assert.Equal(steps.Count, steps.Select(s => s.FromNodeId).Distinct().Count());

        Guid? at = mission.PositionItemId;
        for (var i = 0; i < steps.Count; i++)
        {
            var next = Assert.Single(steps, s => s.FromNodeId == at);
            at = next.ToNodeId;
        }

        Assert.Equal(mission.DestinationItemId, at);
    }

    /// <summary>
    /// La cucitura che ferma il primo che ci passa finche' la prova non lo lascia andare; gli altri
    /// passano.
    /// </summary>
    private sealed class PauseOnce : IDisposable
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _taken;

        public Task Reached => _reached.Task;

        public Task Pause()
        {
            if (Interlocked.Exchange(ref _taken, 1) != 0) return Task.CompletedTask;
            _reached.TrySetResult();
            return _released.Task;
        }

        public void Release() => _released.TrySetResult();

        public void Dispose() => _released.TrySetResult();
    }

    /// <summary>
    /// Uno scrittore della rotta sul suo thread, di sfondo: se una prova rossa lo lascia fermo, il
    /// processo delle prove esce lo stesso.
    /// </summary>
    private sealed class BackgroundWriter
    {
        private readonly Thread _thread;
        private readonly TaskCompletionSource<RouteWriteOutcome> _finished =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BackgroundWriter(Func<RouteWriteOutcome> write)
        {
            _thread = new Thread(() =>
            {
                try
                {
                    _finished.TrySetResult(write());
                }
                catch (Exception ex)
                {
                    _finished.TrySetException(ex);
                }
            }) { IsBackground = true };
            _thread.Start();
        }

        public Task<RouteWriteOutcome> Finished => _finished.Task;

        /// <summary>
        /// Aspetta che lo scrittore sia fermo (sul lucchetto della rotta) o che abbia finito: e' la
        /// condizione, non un tempo.
        /// </summary>
        /// <remarks>
        /// Lo stato <c>WaitSleepJoin</c> non dice su che cosa il thread e' fermo: anche l'avvio del
        /// thread, il lucchetto del registro o un'altra attesa prima del lucchetto della rotta lo fanno
        /// scattare, e lo scrittore legge la versione sul suo thread. Se scatta prima, lo scrittore legge
        /// la versione nuova e scrive: un rosso spurio che gli stati finali prendono, non un verde cieco
        /// (revisione finale della voce 41).
        /// </remarks>
        public void WaitUntilBlockedOrFinished() =>
            SpinWait.SpinUntil(() => Finished.IsCompleted || (_thread.ThreadState & ThreadState.WaitSleepJoin) != 0);
    }
}
