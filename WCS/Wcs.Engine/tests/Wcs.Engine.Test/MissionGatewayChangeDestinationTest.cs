using System.Collections.Concurrent;
using System.Reflection;
using Log.Interface;
using Wcs.Connection.Interface.Events;
using Wcs.Engine.Api;
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
/// Il cambio di destinazione di una missione da fuori (<see cref="MissionGateway.ChangeDestinationAsync"/>,
/// compito 3 del piano delle missioni): prima si calcola, poi si cambia; un rifiuto lascia tutto
/// com'era; verso un magazzino la cella la sceglie il gestionale.
/// </summary>
/// <remarks>
/// <para>
/// Il navigatore sta in <c>Stop</c>, e in <c>Run</c> solo per il risveglio del dispositivo di
/// posizione, mai in <c>Recover</c>: in Recover il setter della destinazione salta il suo blocco e
/// <c>PathBuilder</c> i filtri, cioe' i rami da provare. Le missioni si aggiungono in Recover perche'
/// <c>ActiveMissions.Add</c> esce subito in Stop, poi si passa a Stop.
/// </para>
/// <para>
/// L'impianto della maggior parte delle prove: P al centro, con la rotta vecchia P->A; P->X diretto,
/// P->H->X attraverso H, P->G->Y attraverso G; Z non collegato a niente. Il database del WCS e' in
/// memoria e senza scrittore in coda, il gestionale e' quello finto. Nessuna attesa a tempo: il
/// gestionale si ferma sul suo cancello (<see cref="FakeWarehouse.ReserveStorageGate"/>), e il
/// risveglio in Run si aspetta dal suo annuncio, con un tetto che fa fallire invece di appendere.
/// </para>
/// </remarks>
public sealed class MissionGatewayChangeDestinationTest : IDisposable
{
    private const decimal LoadUnitId = 42;
    private const decimal OldCellId = 7;
    private const string OldDoorCode = "A7";
    private const decimal NewCellId = 8;
    private const string NewDoorCode = "A8";
    private const decimal OtherCellId = 9;
    private const string OtherDoorCode = "A9";

    /// <summary>
    /// Gli eventi del motore, in ordine di arrivo. Arrivano anche dal thread pool, quindi si scrive
    /// sotto lock; il posto in attesa si legge al momento dell'annuncio, come fa l'host.
    /// </summary>
    private sealed class Recorder : IEngineEventSink
    {
        private readonly Lock _lock = new();
        private readonly List<(string Kind, Guid Id, Guid WaitingStepId)> _events = [];
        private readonly List<(Func<bool> Condition, TaskCompletionSource Reached)> _waiters = [];

        public IReadOnlyList<(string Kind, Guid Id, Guid WaitingStepId)> Events
        {
            get
            {
                lock (_lock) return [.. _events];
            }
        }

        /// <summary>Si completa al primo annuncio di un dispositivo dopo il quale la condizione vale.</summary>
        public Task Until(Func<bool> condition)
        {
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_lock) _waiters.Add((condition, reached));
            if (condition()) reached.TrySetResult();
            return reached.Task;
        }

        public void ItemChanged(AutomationDevice device)
        {
            (Func<bool> Condition, TaskCompletionSource Reached)[] waiters;
            lock (_lock)
            {
                _events.Add(("item", device.ItemGuid, device.WaitingStepId));
                waiters = [.. _waiters];
            }

            foreach (var (condition, reached) in waiters)
                if (condition()) reached.TrySetResult();
        }

        public void MissionChanged(PathMission mission, EngineMissionChange change)
        {
            lock (_lock) _events.Add(($"mission:{change}", mission.MissionId, Guid.Empty));
        }

        public void NavigatorStatusChanged() { }
        public void ExtractionQueueChanged(ExtractionQueueItem item, EngineQueueChange change) { }
        public void CommandChanged(CommandRecord command) { }
    }

    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly QueuedDatabaseWriter<WcsWrite>? _previousWriter = Navigator.StateWriter;
    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly RoutingTestPlant _plant = new();
    private readonly FakeWarehouse _warehouse = new();
    private readonly Recorder _recorder = new();

    public MissionGatewayChangeDestinationTest()
    {
        Navigator.Store = _database;
        Navigator.StateWriter = null;
        Navigator.Warehouse = _warehouse;
        RoutesToBuild.Instance.Items = new();
    }

    public void Dispose()
    {
        Navigator.EventSink = null;
        MissionGateway.WaitingForThePickUpHandOffForTesting = null;
        _warehouse.ReserveStorageGate?.TrySetResult();
        _warehouse.ConfirmServedAssignmentsGate?.TrySetResult();
        RoutesToBuild.Instance.Items = new();
        _plant.Dispose();
        Navigator.Warehouse = _previousWarehouse;
        Navigator.StateWriter = _previousWriter;
        Navigator.Store = null;
        _database.Dispose();
    }

    // ---------------------------------------------------------------------------------------
    // Verso un dispositivo
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Il caso di tutti i giorni, a motore fermo: destinazione, rotta e segno cambiano subito, la
    /// rotta vecchia non c'e' piu', e la missione si annuncia con i suoi step nuovi. Lo step nuovo non
    /// parte finche' il motore non va in Run: P, che si avvia da se', non ha il gettone, e X non tiene
    /// il posto.
    /// </summary>
    [Fact]
    public async Task TowardsAReachableDeviceTheDestinationTheRouteAndTheMarkChangeAtOnce()
    {
        var (mission, oldStep) = await AMissionOnPBoundForA();
        Navigator.EventSink = _recorder;

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["X"].ItemGuid);

        Assert.Equal(new MissionDestinationResult(MissionDestinationOutcome.Changed), result);
        Assert.Equal(_plant["X"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.OperatorDestination);
        Assert.Equal(["P", "X"], RouteOf(mission));
        Assert.DoesNotContain(oldStep, MissionSteps.Instance.List);
        Assert.Equal(Guid.Empty, mission.Gateway);
        Assert.Contains(("mission:StepChanged", mission.MissionId, Guid.Empty), _recorder.Events);
        Assert.True(_plant["P"].AutoExecuteStep);
        Assert.Equal(Guid.Empty, _plant["P"].RunningStepId);
        Assert.Equal(Guid.Empty, _plant["X"].WaitingStepId);
    }

    /// <summary>
    /// Il primo step della rotta vecchia era finito male su P (Warning: non in volo) e A gli teneva
    /// il posto. Il gettone di P si azzera con il suo annuncio, quello di A torna libero e A si
    /// annuncia: il setter del gettone annuncia solo chi lo teneva, non chi teneva il posto.
    /// </summary>
    [Fact]
    public async Task TheOldRoutesSeatIsGivenBackAndItsDeviceAnnounced()
    {
        var (mission, oldStep) = await AMissionOnPBoundForA();
        _plant["P"].RunningStepId = oldStep.StepId;
        _plant["P"].Status = DeviceStatusTypes.Warning;
        Assert.True(_plant["A"].TryReserveForIncomingStep(oldStep.StepId));
        Navigator.EventSink = _recorder;

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["X"].ItemGuid);

        Assert.Equal(MissionDestinationOutcome.Changed, result.Outcome);
        Assert.Equal(Guid.Empty, _plant["P"].RunningStepId);
        Assert.Equal(Guid.Empty, _plant["A"].WaitingStepId);
        Assert.Contains(("item", _plant["A"].ItemGuid, Guid.Empty), _recorder.Events);
    }

    /// <summary>
    /// A teneva il posto per lo step vecchio senza che P lo stesse eseguendo (una prenotazione
    /// appesa): la rimozione degli step non lo restituisce, perche' passa dal gettone di P. Lo
    /// restituisce il cambio, subito: a motore fermo la rete di <c>ExecuteStep</c> non arriva.
    /// </summary>
    [Fact]
    public async Task AHungSeatOfTheOldRouteIsGivenBackToo()
    {
        var (mission, oldStep) = await AMissionOnPBoundForA();
        Assert.True(_plant["A"].TryReserveForIncomingStep(oldStep.StepId));
        Assert.Equal(Guid.Empty, _plant["P"].RunningStepId);
        Navigator.EventSink = _recorder;

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["X"].ItemGuid);

        Assert.Equal(MissionDestinationOutcome.Changed, result.Outcome);
        Assert.Equal(Guid.Empty, _plant["A"].WaitingStepId);
        Assert.Contains(("item", _plant["A"].ItemGuid, Guid.Empty), _recorder.Events);
    }

    /// <summary>
    /// Verso Z, che non e' collegato a niente: nessun percorso, e niente cambia - destinazione,
    /// segno, rotta (gli stessi step), il posto che A tiene, nessun annuncio, nessun gestionale.
    /// </summary>
    [Fact]
    public async Task TowardsAnUnreachableDeviceNothingChanges()
    {
        var (mission, oldStep) = await AMissionOnPBoundForA();
        Assert.True(_plant["A"].TryReserveForIncomingStep(oldStep.StepId));
        Navigator.EventSink = _recorder;

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["Z"].ItemGuid);

        Assert.Equal(new MissionDestinationResult(MissionDestinationOutcome.NoRoute), result);
        AssertStillBoundForA(mission, oldStep);
        Assert.Equal(oldStep.StepId, _plant["A"].WaitingStepId);
        Assert.Empty(_recorder.Events);
        Assert.Empty(_warehouse.Calls);
    }

    /// <summary>La destinazione scelta e' la posizione: l'UDC e' gia' li'.</summary>
    [Fact]
    public async Task TheDeviceTheMissionIsOnIsAlreadyThere()
    {
        var (mission, oldStep) = await AMissionOnPBoundForA();

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["P"].ItemGuid);

        Assert.Equal(MissionDestinationOutcome.AlreadyThere, result.Outcome);
        AssertStillBoundForA(mission, oldStep);
    }

    /// <summary>La destinazione scelta e' il gruppo di cui la posizione fa parte: gia' li' anche questo.</summary>
    [Fact]
    public async Task TheGroupOfThePositionIsAlreadyThere()
    {
        var (mission, oldStep) = await AMissionOnPBoundForA(positionInAGroup: true);

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["GP"].ItemGuid);

        Assert.Equal(MissionDestinationOutcome.AlreadyThere, result.Outcome);
        AssertStillBoundForA(mission, oldStep);
    }

    /// <summary>
    /// Lo step della missione e' in volo su P: the change waits for its end (item 54, decision 4; it was
    /// a refusal until then), nothing changes meanwhile, e il gettone resta.
    /// </summary>
    [Fact]
    public async Task AStepInFlightMakesTheChangeWait()
    {
        var (mission, oldStep) = await AMissionOnPBoundForA();
        _plant["P"].RunningStepId = oldStep.StepId;
        _plant["P"].Status = DeviceStatusTypes.Moving;

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["X"].ItemGuid);
        await PendingMissionChanges.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(MissionDestinationOutcome.Pending, result.Outcome);
        Assert.Equal(PendingMissionChange.Destination(_plant["X"].ItemGuid, MissionGatewayChoice.Keep),
            MissionGateway.PendingChangeOf(mission.MissionId));
        AssertStillBoundForA(mission, oldStep);
        Assert.Equal(oldStep.StepId, _plant["P"].RunningStepId);
    }

    /// <summary>
    /// La corsa 4 della ricognizione della voce 41, con il semaforo: P sta eseguendo (il suo semaforo di
    /// esecuzione e' preso, come dentro <c>ExecuteStep</c>) mentre l'operatore cambia la destinazione.
    /// Il commit non prende piu' il semaforo e passa: destinazione X con il segno, rotta P->X, lo step
    /// vecchio tolto. Gli step che P potrebbe avviare li ferma il cambio cominciato, e la rimozione della
    /// strategia <c>Standard</c> confronta la versione della rotta. Prima il cambio era rifiutato
    /// perche' «P sta eseguendo».
    /// </summary>
    [Fact]
    public async Task APositionThatIsExecutingDoesNotRefuseTheChange()
    {
        var (mission, oldStep) = await AMissionOnPBoundForA();
        Assert.True(_plant["P"].TryHoldExecutionForTesting());

        MissionDestinationResult result;
        try
        {
            result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["X"].ItemGuid);
        }
        finally
        {
            _plant["P"].ReleaseExecutionHoldForTesting();
        }

        Assert.Equal(new MissionDestinationResult(MissionDestinationOutcome.Changed), result);
        Assert.Equal(_plant["X"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.OperatorDestination);
        Assert.Equal(["P", "X"], RouteOf(mission));
        Assert.DoesNotContain(oldStep, MissionSteps.Instance.List);
    }

    /// <summary>
    /// La corsa 4 della ricognizione della voce 41: P ha appena chiuso uno step e chiama
    /// <c>ExecuteStep(500)</c>; mentre dura la pausa l'operatore cambia la destinazione. La pausa non
    /// tiene il semaforo, quindi il commit passa e la chiamata in pausa, finita la pausa a motore
    /// fermo, non esegue. Prima la pausa si faceva col semaforo preso, e il cambio era rifiutato
    /// perche' «P sta eseguendo» mentre P dormiva soltanto. La pausa la trattiene la prova, senza
    /// contare sul tempo.
    /// </summary>
    [Fact]
    public async Task APositionPausingBeforeItsStepDoesNotRefuseTheChange()
    {
        var (mission, oldStep) = await AMissionOnPBoundForA();
        var pauseStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pauseOver = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _plant["P"].ExecutionPauseForTesting = (_, token) =>
        {
            pauseStarted.TrySetResult();
            return pauseOver.Task.WaitAsync(token);
        };

        // Il passaggio in Run sveglia i dispositivi con AutoExecuteStep, e P partirebbe da solo per
        // la rotta vecchia (P->A) mentre il cambio arriva: AutoExecuteStep resta spento, e la chiamata
        // in pausa la fa la prova. La marcia serve solo a far entrare la chiamata: dove stava la pausa
        // prima, ci si arrivava solo in Run.
        foreach (var name in _plant.DeviceNames) _plant[name].AutoExecuteStep = false;
        Navigator.NavStatus = NavigatorStatusTypes.Run;
        var pausing = _plant["P"].ExecuteStep(500);
        await pauseStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Navigator.NavStatus = NavigatorStatusTypes.Stop;

        try
        {
            var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["X"].ItemGuid);

            Assert.Equal(new MissionDestinationResult(MissionDestinationOutcome.Changed), result);
            Assert.Equal(_plant["X"].ItemGuid, mission.DestinationItemId);
            Assert.Equal(["P", "X"], RouteOf(mission));
            Assert.DoesNotContain(oldStep, MissionSteps.Instance.List);
        }
        finally
        {
            pauseOver.TrySetResult();
        }

        Assert.False(await pausing.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(Guid.Empty, _plant["P"].RunningStepId);
    }

    /// <summary>
    /// Item 54, decision 3 and answer 2 of the owner of 13/10: a defragmentation and a change of cell are
    /// redirected like every other mission. Sent to X, a device that is not a storage, they become an
    /// extraction with the probed route P->X. Until item 54 they answered «not redirectable», because
    /// they build their route their own way (the cell they go to and its give-back:
    /// <see cref="RedirectedChangeOfCellTest"/>).
    /// </summary>
    [Theory]
    [InlineData(MissionTypes.DefragmentCell)]
    [InlineData(MissionTypes.ChangeStoreLocation)]
    public async Task DefragmentationAndChangeOfCellAreRedirectedAsExtractions(MissionTypes type)
    {
        var (mission, oldStep) = await AMissionOnPBoundForA(type);

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["X"].ItemGuid);

        Assert.Equal(new MissionDestinationResult(MissionDestinationOutcome.Changed), result);
        Assert.Equal(_plant["X"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.OperatorDestination);
        Assert.Equal(MissionTypes.Extraction, mission.MissionType);
        Assert.Equal(["P", "X"], RouteOf(mission));
        Assert.DoesNotContain(oldStep, MissionSteps.Instance.List);
    }

    [Fact]
    public async Task AnUnknownMissionOrDeviceIsReported()
    {
        var (mission, oldStep) = await AMissionOnPBoundForA();

        Assert.Equal(MissionDestinationOutcome.MissionNotFound,
            (await MissionGateway.ChangeDestinationAsync(Guid.NewGuid(), _plant["X"].ItemGuid)).Outcome);
        Assert.Equal(MissionDestinationOutcome.DeviceNotFound,
            (await MissionGateway.ChangeDestinationAsync(mission.MissionId, Guid.NewGuid())).Outcome);
        AssertStillBoundForA(mission, oldStep);
    }

    /// <summary>La stessa scelta una seconda volta: gia' cosi', e la rotta resta quella (gli stessi step).</summary>
    [Fact]
    public async Task TheSameChoiceAgainIsUnchanged()
    {
        var (mission, _) = await AMissionOnPBoundForA();
        Assert.Equal(MissionDestinationOutcome.Changed,
            (await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["X"].ItemGuid)).Outcome);
        var step = Assert.Single(mission.MissionSteps);

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["X"].ItemGuid);

        Assert.Equal(MissionDestinationOutcome.Unchanged, result.Outcome);
        Assert.Same(step, Assert.Single(mission.MissionSteps));
        Assert.True(mission.OperatorDestination);
    }

    /// <summary>
    /// La stessa destinazione scelta di nuovo con un altro gateway non e' «gia' cosi'»: e' un cambio,
    /// e la rotta passa dal gateway nuovo.
    /// </summary>
    [Fact]
    public async Task TheSameDestinationWithAnotherGatewayIsAChange()
    {
        var (mission, _) = await AMissionOnPBoundForA();
        Assert.Equal(MissionDestinationOutcome.Changed,
            (await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["X"].ItemGuid)).Outcome);

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["X"].ItemGuid,
            MissionGatewayChoice.Set(_plant["H"].ItemGuid));

        Assert.Equal(MissionDestinationOutcome.Changed, result.Outcome);
        Assert.Equal(["P", "H", "X"], RouteOf(mission));
        Assert.Equal(_plant["H"].ItemGuid, mission.Gateway);
    }

    /// <summary>
    /// La stessa scelta di nuovo, quando la missione ha perso la rotta (tolta da chi la ricostruisce):
    /// non e' «gia' cosi'», e la rotta si ricalcola.
    /// </summary>
    [Fact]
    public async Task TheSameChoiceAgainWithoutARouteIsAChange()
    {
        var (mission, _) = await AMissionOnPBoundForA();
        Assert.Equal(MissionDestinationOutcome.Changed,
            (await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["X"].ItemGuid)).Outcome);
        mission.RemoveMissionSteps();
        Assert.True(mission.OperatorDestination);

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["X"].ItemGuid);

        Assert.Equal(MissionDestinationOutcome.Changed, result.Outcome);
        Assert.Equal(["P", "X"], RouteOf(mission));
    }

    /// <summary>
    /// La destinazione che la missione ha gia', messa dal motore: sceglierla e' un cambio, perche' da
    /// adesso e' dell'operatore (la configurazione non la riscrive piu'), e la rotta si ricalcola.
    /// </summary>
    [Fact]
    public async Task TheDestinationTheEngineChoseBecomesTheOperatorsChoice()
    {
        var (mission, oldStep) = await AMissionOnPBoundForA();

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["A"].ItemGuid);

        Assert.Equal(MissionDestinationOutcome.Changed, result.Outcome);
        Assert.True(mission.OperatorDestination);
        Assert.Equal(_plant["A"].ItemGuid, mission.DestinationItemId);
        Assert.Equal(["P", "A"], RouteOf(mission));
        Assert.DoesNotContain(oldStep, MissionSteps.Instance.List);
    }

    /// <summary>Una missione senza tipo diventa uno spostamento, come quando la rotta la costruisce il motore.</summary>
    [Fact]
    public async Task AMissionWithoutATypeBecomesAMovement()
    {
        var (mission, _) = await AMissionOnPBoundForA(MissionTypes.None);

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["X"].ItemGuid);

        Assert.Equal(MissionDestinationOutcome.Changed, result.Outcome);
        Assert.Equal(MissionTypes.MovingToItem, mission.MissionType);
    }

    // ---------------------------------------------------------------------------------------
    // Il gateway
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Tenere il gateway H: verso X la rotta passa da H anche se quella diretta P->X c'e' ed e' piu'
    /// corta, e H resta.
    /// </summary>
    [Fact]
    public async Task KeepingTheGatewayRoutesThroughIt()
    {
        var (mission, _) = await AMissionOnPBoundForA();
        mission.Gateway = _plant["H"].ItemGuid;

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["X"].ItemGuid,
            MissionGatewayChoice.Keep);

        Assert.Equal(MissionDestinationOutcome.Changed, result.Outcome);
        Assert.Equal(["P", "H", "X"], RouteOf(mission));
        Assert.Equal(_plant["H"].ItemGuid, mission.Gateway);
    }

    /// <summary>Togliere il gateway G: verso X la rotta e' diretta, e il gateway non c'e' piu'.</summary>
    [Fact]
    public async Task ClearingTheGatewayRoutesWithoutIt()
    {
        var (mission, _) = await AMissionOnPBoundForA();
        mission.Gateway = _plant["G"].ItemGuid;

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["X"].ItemGuid,
            MissionGatewayChoice.Clear);

        Assert.Equal(MissionDestinationOutcome.Changed, result.Outcome);
        Assert.Equal(["P", "X"], RouteOf(mission));
        Assert.Equal(Guid.Empty, mission.Gateway);
    }

    /// <summary>Cambiare il gateway G in H: verso X la rotta passa da H, e il gateway e' H.</summary>
    [Fact]
    public async Task ChangingTheGatewayRoutesThroughTheNewOne()
    {
        var (mission, _) = await AMissionOnPBoundForA();
        mission.Gateway = _plant["G"].ItemGuid;

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["X"].ItemGuid,
            MissionGatewayChoice.Set(_plant["H"].ItemGuid));

        Assert.Equal(MissionDestinationOutcome.Changed, result.Outcome);
        Assert.Equal(["P", "H", "X"], RouteOf(mission));
        Assert.Equal(_plant["H"].ItemGuid, mission.Gateway);
    }

    /// <summary>
    /// Aggiungere il gateway H a una missione che non ne ha: verso X la rotta passa da H anche se
    /// quella diretta e' piu' corta, e il gateway e' H.
    /// </summary>
    [Fact]
    public async Task AddingAGatewayRoutesThroughIt()
    {
        var (mission, _) = await AMissionOnPBoundForA();

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["X"].ItemGuid,
            MissionGatewayChoice.Set(_plant["H"].ItemGuid));

        Assert.Equal(MissionDestinationOutcome.Changed, result.Outcome);
        Assert.Equal(["P", "H", "X"], RouteOf(mission));
        Assert.Equal(_plant["H"].ItemGuid, mission.Gateway);
    }

    /// <summary>
    /// Il gateway G che la missione tiene impedisce di arrivare a X, e senza di lui il percorso c'e':
    /// l'esito lo dice, e niente cambia, il gateway compreso.
    /// </summary>
    [Fact]
    public async Task AGatewayThatBlocksIsReportedWithTheRouteThatExistsWithoutIt()
    {
        var (mission, oldStep) = await AMissionOnPBoundForA();
        mission.Gateway = _plant["G"].ItemGuid;

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["X"].ItemGuid);

        Assert.Equal(new MissionDestinationResult(MissionDestinationOutcome.NoRoute, _plant["G"].ItemGuid,
            WouldRouteWithoutGateway: true), result);
        AssertStillBoundForA(mission, oldStep, gateway: "G");
    }

    /// <summary>Verso Z non c'e' percorso con G e nemmeno senza: l'esito dice G, e che senza non cambierebbe.</summary>
    [Fact]
    public async Task AGatewayThatBlocksWhereThereIsNoRouteAnywayIsReportedAsSuch()
    {
        var (mission, oldStep) = await AMissionOnPBoundForA();
        mission.Gateway = _plant["G"].ItemGuid;

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["Z"].ItemGuid);

        Assert.Equal(new MissionDestinationResult(MissionDestinationOutcome.NoRoute, _plant["G"].ItemGuid,
            WouldRouteWithoutGateway: false), result);
        AssertStillBoundForA(mission, oldStep, gateway: "G");
    }

    /// <summary>
    /// Il gateway scelto (G, aggiunto) impedisce di arrivare a X: il rifiuto lo dice, e sulla missione
    /// non si scrive - il gateway si scrive solo con il resto del cambio.
    /// </summary>
    [Fact]
    public async Task AChosenGatewayThatBlocksIsNotWritten()
    {
        var (mission, oldStep) = await AMissionOnPBoundForA();

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["X"].ItemGuid,
            MissionGatewayChoice.Set(_plant["G"].ItemGuid));

        Assert.Equal(new MissionDestinationResult(MissionDestinationOutcome.NoRoute, _plant["G"].ItemGuid,
            WouldRouteWithoutGateway: true), result);
        AssertStillBoundForA(mission, oldStep);
    }

    [Fact]
    public async Task AnUnknownGatewayIsReported()
    {
        var (mission, oldStep) = await AMissionOnPBoundForA();

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["X"].ItemGuid,
            MissionGatewayChoice.Set(Guid.NewGuid()));

        Assert.Equal(MissionDestinationOutcome.GatewayNotFound, result.Outcome);
        AssertStillBoundForA(mission, oldStep);
    }

    /// <summary>
    /// Il gateway scelto e' la posizione stessa: e' gia' raggiunto, non obbliga a niente e non si
    /// scrive - scritto, non cadrebbe piu' (il setter della posizione lo azzera solo all'arrivo).
    /// </summary>
    [Fact]
    public async Task AGatewayTheMissionAlreadyReachedIsNotWritten()
    {
        var (mission, _) = await AMissionOnPBoundForA();

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["X"].ItemGuid,
            MissionGatewayChoice.Set(_plant["P"].ItemGuid));

        Assert.Equal(MissionDestinationOutcome.Changed, result.Outcome);
        Assert.Equal(["P", "X"], RouteOf(mission));
        Assert.Equal(Guid.Empty, mission.Gateway);
    }

    // ---------------------------------------------------------------------------------------
    // Le ricerche della cella
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Il giro delle rotte ha una ricerca della cella in volo per la missione (ferma al gestionale):
    /// il cambio e' rifiutato, e la ricerca finisce per conto suo.
    /// </summary>
    /// <summary>
    /// Il posto delle ricerche della missione e' preso, senza che nessuna ricerca sia dentro la
    /// missione (lo tiene uno spostamento che costruisce la rotta, o il giro delle rotte prima della
    /// ricerca): il cambio non comincia, e la missione resta com'e'. E' la sola guardia che lo dice:
    /// la missione non conta ricerche ne' avvii.
    /// </summary>
    [Fact]
    public async Task ATakenSearchPlaceAloneRefusesTheChange()
    {
        var (mission, oldStep) = await AMissionOnPBoundForA();
        var claim = new TaskCompletionSource();
        Assert.True(Navigator.TryClaimCellSearch(mission.MissionId, claim.Task));

        try
        {
            var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["X"].ItemGuid);

            Assert.Equal(MissionDestinationOutcome.CellSearchInFlight, result.Outcome);
            AssertStillBoundForA(mission, oldStep);
            Assert.Same(claim.Task, Navigator.CellSearchInFlight(mission.MissionId));
        }
        finally
        {
            Navigator.ReleaseCellSearchClaim(mission.MissionId, claim.Task);
            claim.TrySetResult();
        }
    }

    [Fact]
    public async Task ACellSearchOfTheRouteLoopInFlightRefusesTheChange()
    {
        var mission = await AMissionOnPInTheStorageScene(LookWhereAllocateLuTypes.BuildRouteToStore, boundForX: false);
        TheWarehouseAnswers(NewCellId, NewDoorCode);
        _warehouse.ReserveStorageGate = new TaskCompletionSource();
        StartCellSearch(mission, _plant["P"]);
        await _warehouse.ReserveStorageAsked.WaitAsync(TimeSpan.FromSeconds(30));
        var search = Navigator.CellSearchInFlight(mission.MissionId);
        Assert.NotNull(search);

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["X"].ItemGuid);

        Assert.Equal(MissionDestinationOutcome.CellSearchInFlight, result.Outcome);
        Assert.False(mission.OperatorDestination);
        Assert.Same(search, Navigator.CellSearchInFlight(mission.MissionId));

        _warehouse.ReserveStorageGate.SetResult();
        await search.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(_plant["S8"].ItemGuid, mission.DestinationItemId);
    }

    /// <summary>
    /// Una ricerca della cella dal thread di un dispositivo (fuori dal giro delle rotte, quindi non
    /// in <c>Navigator.CellSearchInFlight</c>) e' dal gestionale per la missione: rifiutato anche
    /// questo, perche' la prenotazione e' una per missione e i due se la toglierebbero a vicenda.
    /// </summary>
    [Fact]
    public async Task ACellSearchOfADeviceInFlightRefusesTheChange()
    {
        var mission = await AMissionOnPInTheStorageScene(LookWhereAllocateLuTypes.BuildRouteToStore, boundForX: false);
        TheWarehouseAnswers(NewCellId, NewDoorCode);
        _warehouse.ReserveStorageGate = new TaskCompletionSource();
        var search = mission.GetStoreToWarehouse(PathMission.StoringSelectionTypes.StoreInLinkedCell,
            keepExistingReservation: true);
        await _warehouse.ReserveStorageAsked.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Null(Navigator.CellSearchInFlight(mission.MissionId));

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["X"].ItemGuid);

        Assert.Equal(MissionDestinationOutcome.CellSearchInFlight, result.Outcome);
        Assert.False(mission.OperatorDestination);

        _warehouse.ReserveStorageGate.SetResult();
        Assert.True(await search.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(_plant["S8"].ItemGuid, mission.DestinationItemId);
    }

    /// <summary>
    /// Mentre un cambio verso un magazzino aspetta il gestionale, il giro delle rotte arriva alla
    /// missione: non avvia una ricerca, la rimette in coda, come per una ricerca gia' in volo.
    /// </summary>
    [Fact]
    public async Task WhileAChangeIsInProgressTheRouteLoopStartsNoCellSearch()
    {
        var mission = await AMissionOnPInTheStorageScene(LookWhereAllocateLuTypes.BuildRouteToStore, boundForX: false);
        TheWarehouseAnswers(NewCellId, NewDoorCode);
        _warehouse.ReserveStorageGate = new TaskCompletionSource();
        var change = MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid);
        await _warehouse.ReserveStorageAsked.WaitAsync(TimeSpan.FromSeconds(30));

        StartCellSearch(mission, _plant["P"]);

        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);

        _warehouse.ReserveStorageGate.SetResult();
        Assert.Equal(MissionDestinationOutcome.Changed, (await change.WaitAsync(TimeSpan.FromSeconds(30))).Outcome);
        Assert.Equal(2, _warehouse.Calls.Count(x => x == nameof(IWarehouse.ReserveStorageAsync)));
    }

    /// <summary>
    /// Mentre un cambio verso S8 aspetta il gestionale, una ricerca della cella arriva all'imbuto
    /// (<c>GetStoreToWarehouse</c>, chiamata qui direttamente): aspetta la fine del cambio prima di
    /// chiamare il gestionale, poi trova la scelta dell'operatore e non prenota niente. Il gestionale
    /// ha visto solo le due domande del cambio, e tiene la sua cella. In campo le ricerche dei
    /// dispositivi non ci arrivano cosi': partono dentro un avvio contato, e con il cambio cominciato
    /// non partono. Questa e' la rete dell'imbuto per chi ci arrivasse senza contarsi.
    /// </summary>
    [Fact]
    public async Task ACellSearchOfADeviceArrivingDuringTheChangeWaitsAndLeavesTheOperatorsCell()
    {
        var mission = await AMissionOnPInTheStorageScene(LookWhereAllocateLuTypes.BuildRouteToStore, boundForX: false);
        TheWarehouseAnswers(NewCellId, NewDoorCode);
        _warehouse.ReserveStorageGate = new TaskCompletionSource();
        var change = MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid);
        await _warehouse.ReserveStorageAsked.WaitAsync(TimeSpan.FromSeconds(30));

        var search = mission.GetStoreToWarehouse(PathMission.StoringSelectionTypes.StoreInLinkedCell,
            keepExistingReservation: true);
        _warehouse.ReserveStorageGate.SetResult();

        Assert.Equal(MissionDestinationOutcome.Changed, (await change.WaitAsync(TimeSpan.FromSeconds(30))).Outcome);
        Assert.False(await search.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(2, _warehouse.Calls.Count(x => x == nameof(IWarehouse.ReserveStorageAsync)));
        Assert.DoesNotContain(nameof(IWarehouse.GetStorageReservationAsync), _warehouse.Calls);
        Assert.Equal(NewCellId, _warehouse.StorageReservationsByMission[mission.MissionId].CellId);
        Assert.Equal(_plant["S8"].ItemGuid, mission.DestinationItemId);
        Assert.Equal(NewDoorCode, mission.StorageDoorCode);
        Assert.True(mission.OperatorDestination);
    }

    // ---------------------------------------------------------------------------------------
    // Verso un magazzino
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Verso il magazzino S8 il gestionale prenota la cella 8: porta, indirizzo di campo,
    /// partizione e profondita' sulla missione, destinazione S8 con il segno, rotta P->S8.
    /// </summary>
    [Fact]
    public async Task TowardsAStorageTheCellIsReservedAndTheRouteGoesToItsDoor()
    {
        var mission = await AMissionOnPInTheStorageScene();
        TheWarehouseAnswers(NewCellId, NewDoorCode);

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid);

        Assert.Equal(new MissionDestinationResult(MissionDestinationOutcome.Changed), result);
        Assert.Equal(NewCellId, _warehouse.StorageReservationsByMission[mission.MissionId].CellId);
        Assert.Equal(_plant["S8"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.OperatorDestination);
        Assert.Equal(NewDoorCode, mission.StorageDoorCode);
        Assert.Equal("0011", mission.DestinationAsi);
        Assert.Equal("0012", mission.DestinationSubitem);
        Assert.Equal("0013", mission.DestinationPartition);
        Assert.Equal(150, mission.StorageDepth);
        Assert.Equal(MissionStatusTypes.Run, mission.Status);
        Assert.Equal(["P", "S8"], RouteOf(mission));

        // Dove si cerca: nel magazzino 1 del gestionale (quello della cella 8, letto dalla porta A8
        // di S8), raggiungibile dal solo ricollocatore del magazzino scelto, P.
        Assert.All(_warehouse.StorageRequests, request =>
        {
            Assert.Equal(1, request.WarehouseId);
            Assert.Equal([_plant["P"].ItemGuid], request.ReachableDeviceIds);
        });
    }

    /// <summary>
    /// Il gestionale non risponde alla domanda: il cambio e' rifiutato e resta tutto com'era,
    /// compresa la prenotazione della cella 7 che la missione teneva per S7. Il gestionale tace solo
    /// sulla domanda, e il rilascio funzionerebbe: se il cambio lo chiedesse, la prenotazione sparirebbe.
    /// </summary>
    [Fact]
    public async Task AWarehouseThatDoesNotAnswerLeavesEverythingAsItWas()
    {
        var mission = await AMissionOnPInTheStorageScene();
        var oldStep = TheMissionIsBoundForS7WithCell7(mission);
        _warehouse.UnavailableOperations.Add(nameof(IWarehouse.ReserveStorageAsync));

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid);

        Assert.Equal(MissionDestinationOutcome.WarehouseUnavailable, result.Outcome);
        AssertStillBoundForS7WithCell7(mission, oldStep);
    }

    /// <summary>
    /// Il gestionale non ha celle in S8: rifiutato senza toccare la prenotazione che la missione
    /// teneva - la domanda e' una sola, quella senza prenotare.
    /// </summary>
    [Fact]
    public async Task AWarehouseWithoutCellsLeavesTheOldReservation()
    {
        var mission = await AMissionOnPInTheStorageScene();
        var oldStep = TheMissionIsBoundForS7WithCell7(mission);
        _warehouse.StorageReservations[LoadUnitId] = new StorageReservation(mission.MissionId,
            StorageReservationOutcome.NoFreeCells, 0, string.Empty, string.Empty, 0, NeedsCompaction: false);

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid);

        Assert.Equal(new MissionDestinationResult(MissionDestinationOutcome.NoStorageCell), result);
        Assert.Equal(1, _warehouse.Calls.Count(x => x == nameof(IWarehouse.ReserveStorageAsync)));
        AssertStillBoundForS7WithCell7(mission, oldStep);
    }

    /// <summary>
    /// La prenotazione non ha risposta (il gestionale cade a meta'): la cella puo' essere stata
    /// scritta con la risposta persa, quindi si restituisce lo stesso, e il cambio e' rifiutato.
    /// </summary>
    [Fact]
    public async Task AReservationWithoutAnAnswerIsGivenBack()
    {
        var mission = await AMissionOnPInTheStorageScene();
        TheWarehouseAnswers(NewCellId, NewDoorCode);
        var reservations = 0;
        _warehouse.Before = operation =>
        {
            if (operation == nameof(IWarehouse.ReserveStorageAsync) && ++reservations == 2)
                throw new WarehouseUnavailableException(operation);
            return Task.CompletedTask;
        };

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid);

        Assert.Equal(MissionDestinationOutcome.WarehouseUnavailable, result.Outcome);
        Assert.Equal(2, reservations);
        Assert.Equal([mission.MissionId, mission.MissionId], _warehouse.ReleasedStorageReservations);
        Assert.Equal(_plant["X"].ItemGuid, mission.DestinationItemId);
        Assert.False(mission.OperatorDestination);
    }

    /// <summary>
    /// La domanda propone la cella 8 (porta A8), ma la prenotazione prende la 10 (porta B8, l'altra di
    /// S8), e il suo indirizzo di campo non si legge (il gestionale cade sulla cella 10): la cella
    /// torna al gestionale, e il cambio e' rifiutato. L'indirizzo della cella proposta si legge prima
    /// di prenotare; dopo si legge solo quello di una porta prenotata diversa.
    /// </summary>
    [Fact]
    public async Task ACellWhoseAddressCannotBeReadIsGivenBack()
    {
        var mission = await AMissionOnPInTheStorageScene();
        await S8HasASecondDoorB8OnCell10();
        TheWarehouseAnswers(NewCellId, NewDoorCode);
        var reservations = 0;
        _warehouse.Before = operation =>
        {
            if (operation == nameof(IWarehouse.ReserveStorageAsync) && ++reservations == 2)
            {
                TheWarehouseAnswers(10, "B8");
                _warehouse.UnavailableOnCells.Add(10);
            }

            return Task.CompletedTask;
        };

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid);

        Assert.Equal(MissionDestinationOutcome.WarehouseUnavailable, result.Outcome);
        Assert.Equal(2, reservations);
        Assert.False(_warehouse.StorageReservationsByMission.ContainsKey(mission.MissionId));
        Assert.Equal(nameof(IWarehouse.ReleaseStorageReservationAsync), _warehouse.Calls[^1]);
        Assert.Equal(_plant["X"].ItemGuid, mission.DestinationItemId);
        Assert.False(mission.OperatorDestination);
    }

    /// <summary>
    /// La missione va in S7 con la cella 7, e l'indirizzo di campo della cella 8 proposta in S8 non
    /// si legge (il gestionale cade sulla mappa delle partizioni): il cambio e' rifiutato prima di
    /// prenotare, e la cella 7 resta della missione. Letto dopo la prenotazione, lo stesso guasto
    /// faceva perdere la cella 7.
    /// </summary>
    [Fact]
    public async Task ACellWhoseAddressCannotBeReadIsRefusedBeforeReserving()
    {
        var mission = await AMissionOnPInTheStorageScene();
        var oldStep = TheMissionIsBoundForS7WithCell7(mission);
        _warehouse.UnavailableOperations.Add(nameof(IWarehouse.GetCellPartitionMapAsync));

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid);

        Assert.Equal(MissionDestinationOutcome.WarehouseUnavailable, result.Outcome);
        Assert.Contains(nameof(IWarehouse.GetCellPartitionMapAsync), _warehouse.Calls);
        Assert.Equal(1, _warehouse.Calls.Count(x => x == nameof(IWarehouse.ReserveStorageAsync)));
        AssertStillBoundForS7WithCell7(mission, oldStep);
    }

    /// <summary>
    /// La missione va in S7 con la cella 7, e il gestionale propone una cella fuori dal magazzino
    /// scelto (la 9 di S9, invece che in S8): il cambio e' rifiutato prima di prenotare, e la cella 7
    /// resta della missione. La domanda e la prenotazione sono la stessa richiesta: prenotare e poi
    /// guardare la porta faceva perdere la cella 7 a ogni tentativo.
    /// </summary>
    [Fact]
    public async Task ACellOutsideTheChosenStorageIsRefusedBeforeReserving()
    {
        var mission = await AMissionOnPInTheStorageScene();
        var oldStep = TheMissionIsBoundForS7WithCell7(mission);
        TheWarehouseAnswers(OtherCellId, OtherDoorCode);

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid);

        Assert.Equal(MissionDestinationOutcome.NoStorageCell, result.Outcome);
        Assert.Equal(1, _warehouse.Calls.Count(x => x == nameof(IWarehouse.ReserveStorageAsync)));
        AssertStillBoundForS7WithCell7(mission, oldStep);
    }

    /// <summary>
    /// La missione va in S7 con la cella 7; il magazzino scelto e' il gruppo GS, con S8 raggiungibile
    /// e S6 no, e il gestionale propone la cella 6 di S6: nessun percorso verso la sua porta, e il
    /// rifiuto viene prima della prenotazione, con la cella 7 intatta.
    /// </summary>
    [Fact]
    public async Task AProposedCellWithoutARouteToItsDoorIsRefusedBeforeReserving()
    {
        const decimal unreachableCellId = 6;
        const string unreachableDoorCode = "A6";
        var mission = await AMissionOnPInTheStorageScene(storagesInAGroup: true);
        var unreachable = _plant.Device("S6", DeviceTypes.Storage);
        unreachable.BelongingGroupId = _plant["GS"].GroupItemId;
        Assert.Same(_plant["GS"], unreachable.BelongedGroup);
        await PlantPersistence.SaveDoorMappingsAsync(unreachable.ItemGuid,
            [new DoorMapping { ItemId = unreachable.ItemGuid, DoorCode = unreachableDoorCode, CellId = unreachableCellId, Side = "A" }]);
        _warehouse.Cells[unreachableCellId] = Cell(unreachableCellId, unreachableDoorCode);
        var oldStep = TheMissionIsBoundForS7WithCell7(mission);
        TheWarehouseAnswers(unreachableCellId, unreachableDoorCode);

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["GS"].ItemGuid);

        Assert.Equal(new MissionDestinationResult(MissionDestinationOutcome.NoRoute), result);
        Assert.Equal(1, _warehouse.Calls.Count(x => x == nameof(IWarehouse.ReserveStorageAsync)));
        AssertStillBoundForS7WithCell7(mission, oldStep);
    }

    /// <summary>
    /// La domanda senza prenotare propone la cella 8 di S8, ma fra la domanda e la prenotazione il
    /// gestionale cambia risposta e prenota la 9 di S9: la porta prenotata si riguarda, la cella 9
    /// torna al gestionale e il cambio e' rifiutato, con la missione com'era.
    /// </summary>
    [Fact]
    public async Task ACellReservedOutsideTheChosenStorageIsGivenBack()
    {
        var mission = await AMissionOnPInTheStorageScene();
        var oldStep = _plant.PlannedStep(mission, "P", "X");
        var doorCode = mission.StorageDoorCode;
        TheWarehouseAnswers(NewCellId, NewDoorCode);
        var reservations = 0;
        _warehouse.Before = operation =>
        {
            if (operation == nameof(IWarehouse.ReserveStorageAsync) && ++reservations == 2)
                TheWarehouseAnswers(OtherCellId, OtherDoorCode);
            return Task.CompletedTask;
        };

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid);

        Assert.Equal(MissionDestinationOutcome.NoStorageCell, result.Outcome);
        Assert.Equal(2, reservations);
        Assert.False(_warehouse.StorageReservationsByMission.ContainsKey(mission.MissionId));
        Assert.Equal(nameof(IWarehouse.ReleaseStorageReservationAsync), _warehouse.Calls[^1]);
        Assert.Equal(_plant["X"].ItemGuid, mission.DestinationItemId);
        Assert.False(mission.OperatorDestination);
        Assert.Equal(doorCode, mission.StorageDoorCode);
        Assert.Same(oldStep, Assert.Single(mission.MissionSteps));
    }

    /// <summary>
    /// Verso il magazzino S5, che ha le sue celle ma non e' collegato: nessun percorso, e il
    /// gestionale non e' stato chiamato.
    /// </summary>
    [Fact]
    public async Task NoRouteTowardsTheStorageAsksNothingOfTheWarehouse()
    {
        var mission = await AMissionOnPInTheStorageScene();
        await PlantPersistence.SaveDoorMappingsAsync(_plant["S5"].ItemGuid,
            [new DoorMapping { ItemId = _plant["S5"].ItemGuid, DoorCode = "A5", CellId = 5, Side = "A" }]);
        _warehouse.Cells[5] = Cell(5, "A5");
        TheWarehouseAnswers(NewCellId, NewDoorCode);

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S5"].ItemGuid);

        Assert.Equal(new MissionDestinationResult(MissionDestinationOutcome.NoRoute), result);
        Assert.Empty(_warehouse.Calls);
        Assert.False(mission.OperatorDestination);
    }

    /// <summary>
    /// La missione andava in S7 con la cella 7: cambiando destinazione verso X la cella 7 torna al
    /// gestionale, e la porta della cella non resta sulla missione.
    /// </summary>
    [Fact]
    public async Task LeavingAStorageGivesBackItsReservation()
    {
        var mission = await AMissionOnPInTheStorageScene();
        TheMissionIsBoundForS7WithCell7(mission);

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["X"].ItemGuid);

        Assert.Equal(MissionDestinationOutcome.Changed, result.Outcome);
        Assert.Equal([mission.MissionId], _warehouse.ReleasedStorageReservations);
        Assert.False(_warehouse.StorageReservationsByMission.ContainsKey(mission.MissionId));
        Assert.Null(mission.StorageDoorCode);
        Assert.Equal(["P", "X"], RouteOf(mission));
    }

    /// <summary>
    /// Un gruppo di magazzini scelto vale come un magazzino: la cella e' in uno dei suoi (S8), e la
    /// missione ci va con la porta, invece di arrivare in un magazzino senza un posto.
    /// </summary>
    [Fact]
    public async Task AGroupOfStoragesIsAStorage()
    {
        var mission = await AMissionOnPInTheStorageScene(storagesInAGroup: true);
        TheWarehouseAnswers(NewCellId, NewDoorCode);

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["GS"].ItemGuid);

        Assert.Equal(MissionDestinationOutcome.Changed, result.Outcome);
        Assert.Equal(NewCellId, _warehouse.StorageReservationsByMission[mission.MissionId].CellId);
        Assert.Equal(_plant["S8"].ItemGuid, mission.DestinationItemId);
        Assert.Equal(NewDoorCode, mission.StorageDoorCode);
        Assert.Equal(["P", "S8"], RouteOf(mission));
    }

    /// <summary>
    /// L'operatore ha mandato la missione in S8, con la cella 8; la cella non va piu' bene (con il
    /// segno il motore non ne cerca un'altra, e l'UDC aspetta), e l'operatore sceglie di nuovo S8. Non
    /// e' «gia' cosi'»: il gestionale e' interrogato di nuovo, con la cella 8 esclusa dalla domanda e
    /// dalla prenotazione, e la missione va verso la cella 10 (porta B8 di S8) che prenota.
    /// </summary>
    [Fact]
    public async Task ChoosingTheSameStorageAgainAsksForAnotherCell()
    {
        var mission = await AMissionOnPInTheStorageScene();
        await S8HasASecondDoorB8OnCell10();
        TheWarehouseAnswers(NewCellId, NewDoorCode);
        Assert.Equal(MissionDestinationOutcome.Changed,
            (await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid)).Outcome);
        Assert.Equal(NewDoorCode, mission.StorageDoorCode);
        Assert.All(_warehouse.StorageRequests, request => Assert.Empty(request.ExcludedCellIds));
        _warehouse.StorageRequests.Clear();
        TheWarehouseAnswers(10, "B8");

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid);

        Assert.Equal(new MissionDestinationResult(MissionDestinationOutcome.Changed), result);
        Assert.Equal(2, _warehouse.StorageRequests.Count);
        Assert.All(_warehouse.StorageRequests, request => Assert.Equal([NewCellId], request.ExcludedCellIds));
        Assert.Equal(10, _warehouse.StorageReservationsByMission[mission.MissionId].CellId);
        Assert.Equal("B8", mission.StorageDoorCode);
        Assert.Equal("0031", mission.DestinationAsi);
        Assert.Equal(_plant["S8"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.OperatorDestination);
        Assert.Equal(["P", "S8"], RouteOf(mission));
    }

    /// <summary>
    /// L'operatore ha mandato la missione in S8, con la cella 8, e sceglie di nuovo S8 per averne
    /// un'altra, ma il gestionale non ne ha un'altra libera: il rifiuto viene dalla domanda senza
    /// prenotare, dice che e' un'altra cella a mancare (la pagina lo distingue da «nessuna cella
    /// libera»), e la cella 8 resta della missione, con la porta e la rotta.
    /// </summary>
    [Fact]
    public async Task ChoosingTheSameStorageAgainWithoutAnotherCellKeepsTheOneItHas()
    {
        var mission = await AMissionOnPInTheStorageScene();
        TheWarehouseAnswers(NewCellId, NewDoorCode);
        Assert.Equal(MissionDestinationOutcome.Changed,
            (await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid)).Outcome);
        var step = Assert.Single(mission.MissionSteps);
        _warehouse.Calls.Clear();
        _warehouse.StorageReservations[LoadUnitId] = new StorageReservation(Guid.Empty,
            StorageReservationOutcome.NoFreeCells, 0, string.Empty, string.Empty, 0, NeedsCompaction: false);

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid);

        Assert.Equal(new MissionDestinationResult(MissionDestinationOutcome.NoStorageCell, NoOtherFreeCell: true), result);
        Assert.Equal(1, _warehouse.Calls.Count(x => x == nameof(IWarehouse.ReserveStorageAsync)));
        Assert.Equal(NewCellId, _warehouse.StorageReservationsByMission[mission.MissionId].CellId);
        Assert.Equal(NewDoorCode, mission.StorageDoorCode);
        Assert.True(mission.OperatorDestination);
        Assert.Same(step, Assert.Single(mission.MissionSteps));
    }

    /// <summary>
    /// L'operatore ha mandato la missione in S8, con la cella 8, e poi sceglie S7: la cella 8 non e'
    /// di S7, e la domanda non esclude niente.
    /// </summary>
    [Fact]
    public async Task ChoosingAnotherStorageExcludesNoCell()
    {
        var mission = await AMissionOnPInTheStorageScene();
        TheWarehouseAnswers(NewCellId, NewDoorCode);
        Assert.Equal(MissionDestinationOutcome.Changed,
            (await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid)).Outcome);
        _warehouse.StorageRequests.Clear();
        TheWarehouseAnswers(OldCellId, OldDoorCode);
        _warehouse.CellPartitionMaps[OldCellId] = new CellPartitionMap(OldCellId, "0001", "0002",
            AsiDoorB: string.Empty, SubitemDoorB: string.Empty, [new CellPartition("0003", -1)]);

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S7"].ItemGuid);

        Assert.Equal(new MissionDestinationResult(MissionDestinationOutcome.Changed), result);
        Assert.Equal(2, _warehouse.StorageRequests.Count);
        Assert.All(_warehouse.StorageRequests, request => Assert.Empty(request.ExcludedCellIds));
        Assert.Equal(OldDoorCode, mission.StorageDoorCode);
    }

    /// <summary>
    /// La missione va in S7 con la cella 7 per scelta del motore, e l'operatore sceglie S7: la
    /// destinazione diventa sua, e la cella 7 non e' esclusa - scegliere la destinazione che il motore
    /// aveva scelto non e' chiedere un'altra cella.
    /// </summary>
    [Fact]
    public async Task ChoosingTheStorageTheEngineChoseDoesNotExcludeItsCell()
    {
        var mission = await AMissionOnPInTheStorageScene();
        TheMissionIsBoundForS7WithCell7(mission);
        TheWarehouseAnswers(OldCellId, OldDoorCode);
        _warehouse.CellPartitionMaps[OldCellId] = new CellPartitionMap(OldCellId, "0001", "0002",
            AsiDoorB: string.Empty, SubitemDoorB: string.Empty, [new CellPartition("0003", -1)]);

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S7"].ItemGuid);

        Assert.Equal(new MissionDestinationResult(MissionDestinationOutcome.Changed), result);
        Assert.Equal(2, _warehouse.StorageRequests.Count);
        Assert.All(_warehouse.StorageRequests, request => Assert.Empty(request.ExcludedCellIds));
        Assert.Equal(OldCellId, _warehouse.StorageReservationsByMission[mission.MissionId].CellId);
        Assert.Equal(OldDoorCode, mission.StorageDoorCode);
        Assert.True(mission.OperatorDestination);
    }

    // ---------------------------------------------------------------------------------------
    // Cambiare solo il gateway tiene la cella (decisione 6 del proprietario del 2/10)
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// L'operatore ha mandato la missione in S8 (cella 8, porta A8), e sceglie di nuovo S8 con il
    /// gateway Q: e' cambiare solo il gateway, non chiedere un'altra cella. Il gestionale non e'
    /// interrogato, la cella 8 resta con porta, indirizzo, profondita' e prenotazione, e si rifa'
    /// solo la rotta, attraverso Q. Prima il gestionale era interrogato con la cella 8 esclusa, e la
    /// missione passava alla cella 10 (porta B8) che proponeva. La riga del cambio dice che la cella
    /// resta (voce 51: prima diceva solo destinazione, gateway e passi, come per una cella nuova).
    /// </summary>
    [Fact]
    public async Task ChangingOnlyTheGatewayOfTheSameStorageKeepsTheCell()
    {
        var mission = await AMissionTheOperatorSentToS8();
        await S8HasASecondDoorB8OnCell10();
        TheWarehouseAnswers(10, "B8");

        var previousDebugLog = Navigator.ActivateDebugLog;
        Navigator.ActivateDebugLog = true;
        MissionDestinationResult result;
        IReadOnlyList<string> lines;
        try
        {
            using var console = new RecordingConsole();
            result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid,
                MissionGatewayChoice.Set(_plant["Q"].ItemGuid));
            lines = console.Lines;
        }
        finally
        {
            Navigator.ActivateDebugLog = previousDebugLog;
        }

        var changed = Assert.Single(lines, line => line.Contains($"Missione {mission.MissionId}: destinazione cambiata"));
        Assert.EndsWith(": la missione tiene la cella della porta A8, e si rifa' solo la rotta", changed);

        Assert.Equal(new MissionDestinationResult(MissionDestinationOutcome.Changed), result);
        Assert.Empty(_warehouse.Calls);
        Assert.Empty(_warehouse.StorageRequests);
        Assert.Equal(8, _warehouse.StorageReservationsByMission[mission.MissionId].CellId);
        Assert.Equal("A8", mission.StorageDoorCode);
        Assert.Equal("0011", mission.DestinationAsi);
        Assert.Equal("0012", mission.DestinationSubitem);
        Assert.Equal("0013", mission.DestinationPartition);
        Assert.Equal(150, mission.StorageDepth);
        Assert.True(mission.StorageCellReserved);
        Assert.Equal(_plant["S8"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.OperatorDestination);
        Assert.Equal(_plant["Q"].ItemGuid, mission.Gateway);
        Assert.Equal(["P", "Q", "S8"], RouteOf(mission));
    }

    /// <summary>
    /// La missione dell'operatore verso S8 ha perso la cella (porta, indirizzo e rotta tolti), e
    /// l'operatore sceglie di nuovo S8 cambiando solo il gateway: senza porta non c'e' una cella da tenere,
    /// e il cambio fa il flusso intero, con la prenotazione della cella 10 (porta B8) che il gestionale
    /// propone, attraverso Q. Nessuna prova guardava il cambio del solo gateway senza porta (revisione
    /// finale del primo giro della voce 39, C21).
    /// </summary>
    [Fact]
    public async Task ChangingOnlyTheGatewayWithoutADoorAsksTheWarehouseForACell()
    {
        var mission = await AMissionTheOperatorSentToS8();
        await S8HasASecondDoorB8OnCell10();
        TheWarehouseAnswers(10, "B8");
        Assert.True(mission.DropStorageCell());
        Assert.Null(mission.StorageDoorCode);

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid,
            MissionGatewayChoice.Set(_plant["Q"].ItemGuid));

        Assert.Equal(new MissionDestinationResult(MissionDestinationOutcome.Changed), result);
        Assert.Contains(nameof(IWarehouse.ReserveStorageAsync), _warehouse.Calls);
        Assert.Equal("B8", mission.StorageDoorCode);
        Assert.True(mission.StorageCellReserved);
        Assert.False(mission.StorageCellLost);
        Assert.Equal(_plant["Q"].ItemGuid, mission.Gateway);
    }

    /// <summary>
    /// L'operatore ha mandato la missione in S8 attraverso Q (la rotta P->Q->S8, vista qui una volta),
    /// e sceglie di nuovo S8 togliendo il gateway: la cella 8 resta senza chiedere niente al
    /// gestionale, e la rotta torna diretta.
    /// </summary>
    [Fact]
    public async Task ClearingTheGatewayOfTheSameStorageKeepsTheCell()
    {
        var mission = await AMissionOnPInTheStorageScene();
        TheWarehouseAnswers(NewCellId, NewDoorCode);
        Assert.Equal(MissionDestinationOutcome.Changed, (await MissionGateway.ChangeDestinationAsync(mission.MissionId,
            _plant["S8"].ItemGuid, MissionGatewayChoice.Set(_plant["Q"].ItemGuid))).Outcome);
        Assert.Equal(["P", "Q", "S8"], RouteOf(mission));
        Assert.Equal("A8", mission.StorageDoorCode);
        await S8HasASecondDoorB8OnCell10();
        TheWarehouseAnswers(10, "B8");
        _warehouse.Calls.Clear();
        _warehouse.StorageRequests.Clear();

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid,
            MissionGatewayChoice.Clear);

        Assert.Equal(new MissionDestinationResult(MissionDestinationOutcome.Changed), result);
        Assert.Empty(_warehouse.Calls);
        Assert.Equal(8, _warehouse.StorageReservationsByMission[mission.MissionId].CellId);
        Assert.Equal("A8", mission.StorageDoorCode);
        Assert.Equal("0011", mission.DestinationAsi);
        Assert.Equal(Guid.Empty, mission.Gateway);
        Assert.True(mission.OperatorDestination);
        Assert.Equal(["P", "S8"], RouteOf(mission));
    }

    /// <summary>
    /// L'operatore ha mandato la missione in S8 attraverso Q, e sceglie di nuovo S8 con Q: il gateway
    /// resta quello, e scegliere di nuovo il magazzino e' chiedere un'altra cella, con la cella 8
    /// esclusa dalla domanda - la regola di sempre, che il gateway nominato di nuovo non cambia.
    /// </summary>
    [Fact]
    public async Task NamingTheSameGatewayAgainStillAsksForAnotherCell()
    {
        var mission = await AMissionOnPInTheStorageScene();
        TheWarehouseAnswers(NewCellId, NewDoorCode);
        Assert.Equal(MissionDestinationOutcome.Changed, (await MissionGateway.ChangeDestinationAsync(mission.MissionId,
            _plant["S8"].ItemGuid, MissionGatewayChoice.Set(_plant["Q"].ItemGuid))).Outcome);
        await S8HasASecondDoorB8OnCell10();
        TheWarehouseAnswers(10, "B8");
        _warehouse.StorageRequests.Clear();

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid,
            MissionGatewayChoice.Set(_plant["Q"].ItemGuid));

        Assert.Equal(new MissionDestinationResult(MissionDestinationOutcome.Changed), result);
        Assert.Equal(2, _warehouse.StorageRequests.Count);
        Assert.All(_warehouse.StorageRequests, request => Assert.Equal([8m], request.ExcludedCellIds));
        Assert.Equal("B8", mission.StorageDoorCode);
        Assert.Equal(_plant["Q"].ItemGuid, mission.Gateway);
        Assert.Equal(["P", "Q", "S8"], RouteOf(mission));
    }

    /// <summary>
    /// La missione va in S8 con la cella 8 per scelta del motore, con una porta che non e' una
    /// prenotazione sua (scritta direttamente, come un comando), e l'operatore sceglie S8 con il
    /// gateway Q: anche senza il segno cambiare solo il gateway tiene la cella. Niente al gestionale,
    /// la destinazione diventa dell'operatore, e la porta resta quella che era - anche non sua: il
    /// commit che la tiene non la fa passare per una prenotazione, e il deposito non chiede.
    /// </summary>
    [Fact]
    public async Task WithoutTheMarkChangingOnlyTheGatewayKeepsTheCellAsItWas()
    {
        var mission = await AMissionOnPInTheStorageScene();
        mission.DestinationItemId = _plant["S8"].ItemGuid;
        mission.StorageDoorCode = "A8";
        mission.DestinationAsi = "0011";
        mission.DestinationSubitem = "0012";
        mission.DestinationPartition = "0013";
        mission.StorageDepth = 150;
        _plant.PlannedStep(mission, "P", "S8");
        Assert.False(mission.OperatorDestination);
        Assert.False(mission.StorageCellReserved);
        await S8HasASecondDoorB8OnCell10();
        TheWarehouseAnswers(10, "B8");
        _warehouse.Calls.Clear();

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid,
            MissionGatewayChoice.Set(_plant["Q"].ItemGuid));

        Assert.Equal(new MissionDestinationResult(MissionDestinationOutcome.Changed), result);
        Assert.Empty(_warehouse.Calls);
        Assert.Equal("A8", mission.StorageDoorCode);
        Assert.Equal("0011", mission.DestinationAsi);
        Assert.Equal(150, mission.StorageDepth);
        Assert.False(mission.StorageCellReserved);
        Assert.True(mission.OperatorDestination);
        Assert.Equal(_plant["Q"].ItemGuid, mission.Gateway);
        Assert.Equal(["P", "Q", "S8"], RouteOf(mission));
    }

    /// <summary>
    /// Un gruppo di magazzini scelto di nuovo, con la cella in uno dei suoi (S8), e un gateway nuovo:
    /// la cella resta, e la destinazione resta il magazzino della porta.
    /// </summary>
    [Fact]
    public async Task ChangingOnlyTheGatewayOfAGroupOfStoragesKeepsTheCellInIt()
    {
        var mission = await AMissionOnPInTheStorageScene(storagesInAGroup: true);
        TheWarehouseAnswers(NewCellId, NewDoorCode);
        Assert.Equal(MissionDestinationOutcome.Changed,
            (await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["GS"].ItemGuid)).Outcome);
        Assert.Equal("A8", mission.StorageDoorCode);
        await S8HasASecondDoorB8OnCell10();
        TheWarehouseAnswers(10, "B8");
        _warehouse.Calls.Clear();

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["GS"].ItemGuid,
            MissionGatewayChoice.Set(_plant["Q"].ItemGuid));

        Assert.Equal(new MissionDestinationResult(MissionDestinationOutcome.Changed), result);
        Assert.Empty(_warehouse.Calls);
        Assert.Equal("A8", mission.StorageDoorCode);
        Assert.Equal(_plant["S8"].ItemGuid, mission.DestinationItemId);
        Assert.Equal(["P", "Q", "S8"], RouteOf(mission));
    }

    /// <summary>
    /// S8 e S9 nello stesso gruppo; la missione ha la cella 8 in S8, attraverso Q, e l'operatore
    /// sceglie S9 togliendo il gateway: S9 non e' il magazzino della cella, anche se e' un suo
    /// fratello nel gruppo, e la cella si chiede al gestionale in S9 - non si tiene la 8.
    /// </summary>
    [Fact]
    public async Task ASiblingStorageOfTheGroupIsNotTheSameStorage()
    {
        var mission = await AMissionOnPInTheStorageScene(storagesInAGroup: true);
        _plant["S9"].BelongingGroupId = _plant["GS"].GroupItemId;
        Assert.Same(_plant["GS"], _plant["S9"].BelongedGroup);
        TheWarehouseAnswers(NewCellId, NewDoorCode);
        Assert.Equal(MissionDestinationOutcome.Changed, (await MissionGateway.ChangeDestinationAsync(mission.MissionId,
            _plant["S8"].ItemGuid, MissionGatewayChoice.Set(_plant["Q"].ItemGuid))).Outcome);
        Assert.Equal("A8", mission.StorageDoorCode);
        TheWarehouseAnswers(OtherCellId, OtherDoorCode);
        _warehouse.Calls.Clear();

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S9"].ItemGuid,
            MissionGatewayChoice.Clear);

        Assert.Equal(new MissionDestinationResult(MissionDestinationOutcome.Changed), result);
        Assert.Contains(nameof(IWarehouse.ReserveStorageAsync), _warehouse.Calls);
        Assert.Equal(9, _warehouse.StorageReservationsByMission[mission.MissionId].CellId);
        Assert.Equal("A9", mission.StorageDoorCode);
        Assert.Equal(_plant["S9"].ItemGuid, mission.DestinationItemId);
        Assert.Equal(Guid.Empty, mission.Gateway);
        Assert.Equal(["P", "S9"], RouteOf(mission));
    }

    /// <summary>
    /// L'operatore ha mandato la missione in S8, e sceglie di nuovo S8 con il gateway X, da cui S8 non
    /// si raggiunge: il rifiuto dice X e che senza di lui il percorso c'e', il gestionale non e'
    /// interrogato, e cella, rotta e gateway restano.
    /// </summary>
    [Fact]
    public async Task ANewGatewayThatDoesNotReachTheCellIsReportedAndTheCellStays()
    {
        var mission = await AMissionTheOperatorSentToS8();
        var step = Assert.Single(mission.MissionSteps);

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid,
            MissionGatewayChoice.Set(_plant["X"].ItemGuid));

        Assert.Equal(new MissionDestinationResult(MissionDestinationOutcome.NoRoute, _plant["X"].ItemGuid,
            WouldRouteWithoutGateway: true), result);
        Assert.Empty(_warehouse.Calls);
        Assert.Equal("A8", mission.StorageDoorCode);
        Assert.Equal(Guid.Empty, mission.Gateway);
        Assert.Same(step, Assert.Single(mission.MissionSteps));
    }

    /// <summary>
    /// Mentre la prenotazione verso S8 aspetta il gestionale, la missione viene spostata su Q (che
    /// arriva anch'esso a S8): il commit trova la missione altrove, rifiuta come per uno step in volo,
    /// e la cella prenotata torna al gestionale.
    /// </summary>
    [Fact]
    public async Task AMissionMovedWhileTheWarehouseAnswersIsNotChangedAndTheCellGoesBack()
    {
        var mission = await AMissionOnPInTheStorageScene();
        TheWarehouseAnswers(NewCellId, NewDoorCode);
        var reservations = 0;
        _warehouse.Before = async operation =>
        {
            if (operation != nameof(IWarehouse.ReserveStorageAsync) || ++reservations != 2) return;

            Assert.Equal(MissionMoveOutcome.Moved, MissionGateway.Move(mission.MissionId, _plant["Q"].ItemGuid));
            await mission.PositionWork;
        };

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid);

        Assert.Equal(MissionDestinationOutcome.StepInFlight, result.Outcome);
        Assert.Equal(2, reservations);
        Assert.False(_warehouse.StorageReservationsByMission.ContainsKey(mission.MissionId));
        Assert.Equal(_plant["X"].ItemGuid, mission.DestinationItemId);
        Assert.False(mission.OperatorDestination);
    }

    /// <summary>
    /// La missione va in S7 con la cella 7, e mentre la domanda verso S8 aspetta il gestionale viene
    /// spostata su Q: il cambio e' rifiutato prima di prenotare, e la cella 7 resta della missione.
    /// </summary>
    [Fact]
    public async Task AMissionMovedWhileTheWarehouseAnswersTheQuestionKeepsItsCell()
    {
        var mission = await AMissionOnPInTheStorageScene();
        TheMissionIsBoundForS7WithCell7(mission);
        _warehouse.ReserveStorageGate = new TaskCompletionSource();
        var change = MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid);
        await _warehouse.ReserveStorageAsked.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(MissionMoveOutcome.Moved, MissionGateway.Move(mission.MissionId, _plant["Q"].ItemGuid));
        await mission.PositionWork;
        _warehouse.ReserveStorageGate.SetResult();

        Assert.Equal(MissionDestinationOutcome.StepInFlight, (await change.WaitAsync(TimeSpan.FromSeconds(30))).Outcome);
        Assert.Equal(1, _warehouse.Calls.Count(x => x == nameof(IWarehouse.ReserveStorageAsync)));
        Assert.Equal(OldCellId, _warehouse.StorageReservationsByMission[mission.MissionId].CellId);
        Assert.Equal(OldDoorCode, mission.StorageDoorCode);
        Assert.Equal(_plant["S7"].ItemGuid, mission.DestinationItemId);
    }

    /// <summary>
    /// La missione va in S7 con la cella 7; S8 e S6 sono nel gruppo GS, e verso S8 il gestionale
    /// propone la cella 6 di S6. Mentre si legge l'indirizzo di campo della cella 6, dopo la domanda e
    /// prima della prenotazione, la missione viene spostata su Q: il cambio e' rifiutato prima di
    /// prenotare, e la cella 7 resta della missione. Senza riguardare prima di prenotare, la
    /// prenotazione lasciava la 7 e il commit rifiutava dopo.
    /// </summary>
    [Fact]
    public async Task AMissionMovedWhileTheProposedCellIsReadKeepsItsCell()
    {
        var mission = await AMissionOnPInTheStorageScene(storagesInAGroup: true);
        await S6IsAStorageOfGSWithCell6();
        _plant.Link("P", "S6");
        _warehouse.CellPartitionMaps[6] = new CellPartitionMap(6, "0061", "0062",
            AsiDoorB: string.Empty, SubitemDoorB: string.Empty, [new CellPartition("0063", -1)]);
        TheMissionIsBoundForS7WithCell7(mission);
        TheWarehouseAnswers(6, "A6");
        var moved = false;
        _warehouse.Before = async operation =>
        {
            if (operation != nameof(IWarehouse.DescribeCellAsync) || moved ||
                !_warehouse.Calls.Contains(nameof(IWarehouse.ReserveStorageAsync)))
                return;

            moved = true;
            Assert.Equal(MissionMoveOutcome.Moved, MissionGateway.Move(mission.MissionId, _plant["Q"].ItemGuid));
            await mission.PositionWork;
        };

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid);

        Assert.True(moved);
        Assert.Equal(MissionDestinationOutcome.StepInFlight, result.Outcome);
        Assert.Equal(1, _warehouse.Calls.Count(x => x == nameof(IWarehouse.ReserveStorageAsync)));
        Assert.Equal(OldCellId, _warehouse.StorageReservationsByMission[mission.MissionId].CellId);
        Assert.Equal(OldDoorCode, mission.StorageDoorCode);
    }

    /// <summary>
    /// Mentre la prenotazione verso S8 aspetta il gestionale, la missione viene tolta: il commit non
    /// la trova piu' fra le attive, non la cambia, e la cella prenotata torna al gestionale. La
    /// missione sparisce durante la prenotazione, dopo le guardie che la precedono, cosi' che a
    /// fermarla sia proprio il commit.
    /// </summary>
    [Fact]
    public async Task AMissionRemovedWhileTheWarehouseAnswersIsNotChangedAndTheCellGoesBack()
    {
        var mission = await AMissionOnPInTheStorageScene();
        TheWarehouseAnswers(NewCellId, NewDoorCode);
        var reservations = 0;
        _warehouse.Before = async operation =>
        {
            if (operation != nameof(IWarehouse.ReserveStorageAsync) || ++reservations != 2) return;

            // Senza restituire la prenotazione: e' il cambio, non la rimozione, che deve restituirla.
            Assert.True(await ActiveMissions.Instance.Remove(mission, releaseStorageReservation: false));
        };

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid);

        Assert.Equal(2, reservations);
        Assert.Equal(MissionDestinationOutcome.MissionNotFound, result.Outcome);
        Assert.False(_warehouse.StorageReservationsByMission.ContainsKey(mission.MissionId));
        Assert.Equal(_plant["X"].ItemGuid, mission.DestinationItemId);
        Assert.False(mission.OperatorDestination);
        Assert.Empty(mission.MissionSteps);
    }

    /// <summary>
    /// Mentre la domanda verso S8 aspetta il gestionale, la missione viene tolta: il cambio e'
    /// rifiutato prima di prenotare, e il gestionale non ha prenotato niente.
    /// </summary>
    [Fact]
    public async Task AMissionRemovedWhileTheWarehouseAnswersTheQuestionIsNotReservedFor()
    {
        var mission = await AMissionOnPInTheStorageScene();
        TheWarehouseAnswers(NewCellId, NewDoorCode);
        _warehouse.ReserveStorageGate = new TaskCompletionSource();
        var change = MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid);
        await _warehouse.ReserveStorageAsked.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(await ActiveMissions.Instance.Remove(mission));
        _warehouse.ReserveStorageGate.SetResult();

        Assert.Equal(MissionDestinationOutcome.MissionNotFound, (await change.WaitAsync(TimeSpan.FromSeconds(30))).Outcome);
        Assert.Equal(1, _warehouse.Calls.Count(x => x == nameof(IWarehouse.ReserveStorageAsync)));
        Assert.False(_warehouse.StorageReservationsByMission.ContainsKey(mission.MissionId));
        Assert.Equal(_plant["X"].ItemGuid, mission.DestinationItemId);
    }

    /// <summary>Uno step in volo e un magazzino scelto: the change waits (item 54, decision 4) without asking the warehouse anything.</summary>
    [Fact]
    public async Task AStepInFlightMakesAStorageWaitWithoutAskingTheWarehouse()
    {
        var mission = await AMissionOnPInTheStorageScene();
        var step = _plant.PlannedStep(mission, "P", "X");
        _plant["P"].RunningStepId = step.StepId;
        _plant["P"].Status = DeviceStatusTypes.Moving;
        TheWarehouseAnswers(NewCellId, NewDoorCode);

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid);
        await PendingMissionChanges.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(MissionDestinationOutcome.Pending, result.Outcome);
        Assert.Equal(PendingMissionChange.Destination(_plant["S8"].ItemGuid, MissionGatewayChoice.Keep),
            MissionGateway.PendingChangeOf(mission.MissionId));
        Assert.Empty(_warehouse.Calls);
        Assert.Same(step, Assert.Single(mission.MissionSteps));
        Assert.Equal(step.StepId, _plant["P"].RunningStepId);
    }

    /// <summary>
    /// Item 54, fix round 3 of task 4: the change towards S8 waits for the step P->X, and during the same
    /// step the field rejection arrives and is postponed (the step in flight wins). The rejection, which
    /// only the route loop redoes, wins over the change asked before it: the pending change is dropped at
    /// once. When the step ends (in error: the master loop sees it, with the engine stopped) nothing asks
    /// the warehouse for a cell, round after round. Before the fix the change stayed, and every round of
    /// the master loop reserved a cell, met the rejection at the commit (a refusal taken as one that
    /// passes by itself), gave the cell back and waited again, for ever with the engine stopped.
    /// </summary>
    [Fact]
    public async Task APendingChangeMetByAPostponedFieldRejectionIsDroppedAndNeverAsksTheWarehouse()
    {
        var mission = await AMissionOnPInTheStorageScene();
        var step = AStepPxInFlightWithARejectionPointOnP(mission);
        TheWarehouseAnswers(NewCellId, NewDoorCode);

        Assert.Equal(MissionDestinationOutcome.Pending,
            (await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid)).Outcome);
        await PendingMissionChanges.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(await mission.RejectTo(PathMission.RejectionTypes.NoLuDataFromPlc).WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.Null(MissionGateway.PendingChangeOf(mission.MissionId));

        await TheStepPxEndsInErrorAndTheMasterLoopGoesRoundTwice(mission);

        Assert.Equal(0, _warehouse.Calls.Count(x => x == nameof(IWarehouse.ReserveStorageAsync)));
        Assert.Null(MissionGateway.PendingChangeOf(mission.MissionId));
        Assert.Equal(_plant["X"].ItemGuid, mission.DestinationItemId);
        Assert.False(mission.OperatorDestination);
        Assert.Same(step, Assert.Single(mission.MissionSteps));
    }

    /// <summary>
    /// The field rejection arrives first, during the step P->X, and is postponed; then the operator asks
    /// S8. The change is refused at once, as before item 54 (the mission waits for the rejection that the
    /// route loop redoes): it does not become a pending change that would meet the rejection when the step
    /// ends, or be applied on top of the rejection route in Run.
    /// </summary>
    [Fact]
    public async Task AChangeAskedWhileAPostponedFieldRejectionWaitsIsRefusedAtOnce()
    {
        var mission = await AMissionOnPInTheStorageScene();
        var step = AStepPxInFlightWithARejectionPointOnP(mission);
        TheWarehouseAnswers(NewCellId, NewDoorCode);
        Assert.False(await mission.RejectTo(PathMission.RejectionTypes.NoLuDataFromPlc).WaitAsync(TimeSpan.FromSeconds(30)));

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid);
        await PendingMissionChanges.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(MissionDestinationOutcome.StepInFlight, result.Outcome);
        Assert.Null(MissionGateway.PendingChangeOf(mission.MissionId));
        Assert.Empty(_warehouse.Calls);
        Assert.Same(step, Assert.Single(mission.MissionSteps));
        Assert.Equal(_plant["X"].ItemGuid, mission.DestinationItemId);
    }

    /// <summary>
    /// The net under the two above: a pending change that, when it is applied, finds the mission waiting
    /// for a postponed field rejection (recorded in the instant between the look of the request and the
    /// postponement) is refused for good before asking the warehouse anything, and is gone. The master
    /// loop does not try it again.
    /// </summary>
    [Fact]
    public async Task APendingChangeThatFindsAPostponedFieldRejectionWhenItIsAppliedIsRefusedForGood()
    {
        var mission = await AMissionOnPInTheStorageScene();
        var step = AStepPxInFlightWithARejectionPointOnP(mission);
        TheWarehouseAnswers(NewCellId, NewDoorCode);
        Assert.False(await mission.RejectTo(PathMission.RejectionTypes.NoLuDataFromPlc).WaitAsync(TimeSpan.FromSeconds(30)));
        PendingMissionChanges.Defer(mission, PendingMissionChange.Destination(_plant["S8"].ItemGuid, MissionGatewayChoice.Keep),
            step, applying: null);
        await PendingMissionChanges.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(MissionGateway.PendingChangeOf(mission.MissionId));

        await TheStepPxEndsInErrorAndTheMasterLoopGoesRoundTwice(mission);

        Assert.Equal(0, _warehouse.Calls.Count(x => x == nameof(IWarehouse.ReserveStorageAsync)));
        Assert.Null(MissionGateway.PendingChangeOf(mission.MissionId));
        Assert.Equal(_plant["X"].ItemGuid, mission.DestinationItemId);
        Assert.False(mission.OperatorDestination);
    }

    /// <summary>The step P->X of the mission in flight on P, and Q the rejection point of P for the load unit's data.</summary>
    private MissionStep AStepPxInFlightWithARejectionPointOnP(PathMission mission)
    {
        _plant["P"].RejectItemDestinationIdGetLuInfo = _plant["Q"].ItemGuid;
        var step = _plant.PlannedStep(mission, "P", "X");
        _plant["P"].RunningStepId = step.StepId;
        _plant["P"].Status = DeviceStatusTypes.Moving;
        Assert.Same(step, mission.StepInFlight());
        return step;
    }

    /// <summary>
    /// The step P->X ends in error (P in Warning: nothing in flight, the step stays in the route), and the
    /// master loop goes round twice with the engine stopped.
    /// </summary>
    private static async Task TheStepPxEndsInErrorAndTheMasterLoopGoesRoundTwice(PathMission mission)
    {
        PathDevices.Instance[mission.PositionItemId]!.Status = DeviceStatusTypes.Warning;
        Assert.Null(mission.StepInFlight());
        Assert.Equal(NavigatorStatusTypes.Stop, Navigator.NavStatus);

        for (var round = 0; round < 2; round++)
        {
            PendingMissionChanges.SweepAll();
            await PendingMissionChanges.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    /// <summary>
    /// Uno step risulta partito mentre il gestionale risponde alla prenotazione verso S8: in campo non
    /// succede (ogni avvio si conta, e con il cambio cominciato rinuncia), e qui i gettoni si scrivono
    /// a mano. E' la rete del commit (<c>StepInFlight</c>): lo trova in volo, non tocca la rotta, e
    /// la cella prenotata torna al gestionale.
    /// </summary>
    [Fact]
    public async Task AStepThatSetsOffWhileTheWarehouseAnswersRefusesTheChangeAndTheCellGoesBack()
    {
        var mission = await AMissionOnPInTheStorageScene();
        var step = _plant.PlannedStep(mission, "P", "X");
        TheWarehouseAnswers(NewCellId, NewDoorCode);
        var reservations = 0;
        _warehouse.Before = operation =>
        {
            if (operation == nameof(IWarehouse.ReserveStorageAsync) && ++reservations == 2)
            {
                _plant["P"].RunningStepId = step.StepId;
                _plant["P"].Status = DeviceStatusTypes.Moving;
            }

            return Task.CompletedTask;
        };

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid);

        Assert.Equal(MissionDestinationOutcome.StepInFlight, result.Outcome);
        Assert.Equal(2, reservations);
        Assert.False(_warehouse.StorageReservationsByMission.ContainsKey(mission.MissionId));
        Assert.Same(step, Assert.Single(mission.MissionSteps));
        Assert.Equal(step.StepId, _plant["P"].RunningStepId);
        Assert.Equal(_plant["X"].ItemGuid, mission.DestinationItemId);
        Assert.False(mission.OperatorDestination);
    }

    /// <summary>
    /// La missione va in S7 con la cella 7, e lo step P->S7 parte mentre la domanda verso S8 aspetta
    /// il gestionale (un avvio che non si e' contato, scritto a mano): il cambio e' rifiutato prima di
    /// prenotare, e la cella 7 resta della missione, con la rotta e lo step in volo.
    /// </summary>
    [Fact]
    public async Task AStepThatSetsOffWhileTheWarehouseAnswersTheQuestionKeepsTheCell()
    {
        var mission = await AMissionOnPInTheStorageScene();
        var oldStep = TheMissionIsBoundForS7WithCell7(mission);
        _warehouse.ReserveStorageGate = new TaskCompletionSource();
        var change = MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid);
        await _warehouse.ReserveStorageAsked.WaitAsync(TimeSpan.FromSeconds(30));

        _plant["P"].RunningStepId = oldStep.StepId;
        _plant["P"].Status = DeviceStatusTypes.Moving;
        _warehouse.ReserveStorageGate.SetResult();

        Assert.Equal(MissionDestinationOutcome.StepInFlight, (await change.WaitAsync(TimeSpan.FromSeconds(30))).Outcome);
        Assert.Equal(1, _warehouse.Calls.Count(x => x == nameof(IWarehouse.ReserveStorageAsync)));
        Assert.Equal(oldStep.StepId, _plant["P"].RunningStepId);
        AssertStillBoundForS7WithCell7(mission, oldStep);
    }

    // ---------------------------------------------------------------------------------------
    // La cella persa: il cambio rifiutato dopo la prenotazione (decisioni dell'1/10)
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// La missione va in S7 con la cella 7, e la prenotazione verso S8 non ha risposta: la cella 7
    /// l'ha gia' lasciata, e la missione non ci va piu'. Porta, indirizzo e rotta tolti, la
    /// destinazione resta S7, e la missione e' in coda al navigatore, che le prenota una cella in S7
    /// con il flusso del cambio. Prima andava verso la porta A7, che il gestionale poteva dare a
    /// un'altra UDC; e dopo il primo rimedio il navigatore faceva il giro della configurazione di P
    /// (modalita' No, nessuna destinazione fissa), cioe' la rotta verso il magazzino nudo S7, senza
    /// porta e senza niente prenotato.
    /// </summary>
    [Fact]
    public async Task AReservationWithoutAnAnswerLosesTheCellTheMissionHeld()
    {
        var mission = await AMissionOnPInTheStorageScene();
        var oldStep = TheMissionHoldsCell7WithItsAddress(mission);
        TheReservationAnswers(() => throw new WarehouseUnavailableException(nameof(IWarehouse.ReserveStorageAsync)));

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid);

        Assert.Equal(MissionDestinationOutcome.WarehouseUnavailable, result.Outcome);
        Assert.True(result.LostTheCell);
        AssertTheCellIsLostAndTheMissionIsBackWithTheNavigator(mission, oldStep);
        await TheNavigatorReservesACellInS7Again(mission);
    }

    /// <summary>La stessa cosa per una prenotazione che, dopo la domanda, non trova piu' celle.</summary>
    [Fact]
    public async Task AReservationThatFindsNoCellLosesTheCellTheMissionHeld()
    {
        var mission = await AMissionOnPInTheStorageScene();
        var oldStep = TheMissionHoldsCell7WithItsAddress(mission);
        TheReservationAnswers(() => _warehouse.StorageReservations[LoadUnitId] = new StorageReservation(Guid.Empty,
            StorageReservationOutcome.NoFreeCells, 0, string.Empty, string.Empty, 0, NeedsCompaction: false));

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid);

        Assert.Equal(MissionDestinationOutcome.NoStorageCell, result.Outcome);
        AssertTheCellIsLostAndTheMissionIsBackWithTheNavigator(mission, oldStep);
        await TheNavigatorReservesACellInS7Again(mission);
    }

    /// <summary>La stessa cosa per una prenotazione che prende una cella fuori dal magazzino scelto (la 9 di S9).</summary>
    [Fact]
    public async Task AReservedCellOutsideTheChosenStorageLosesTheCellTheMissionHeld()
    {
        var mission = await AMissionOnPInTheStorageScene();
        var oldStep = TheMissionHoldsCell7WithItsAddress(mission);
        TheReservationAnswers(() => TheWarehouseAnswers(OtherCellId, OtherDoorCode));

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid);

        Assert.Equal(MissionDestinationOutcome.NoStorageCell, result.Outcome);
        AssertTheCellIsLostAndTheMissionIsBackWithTheNavigator(mission, oldStep);
        await TheNavigatorReservesACellInS7Again(mission);
    }

    /// <summary>
    /// La stessa cosa per una prenotazione che prende una porta senza percorso: il magazzino scelto e'
    /// il gruppo GS, la domanda propone la cella 8 di S8, e la prenotazione la 6 di S6, che da P non si
    /// raggiunge.
    /// </summary>
    [Fact]
    public async Task AReservedCellWithoutARouteLosesTheCellTheMissionHeld()
    {
        var mission = await AMissionOnPInTheStorageScene(storagesInAGroup: true);
        await S6IsAStorageOfGSWithCell6();
        var oldStep = TheMissionHoldsCell7WithItsAddress(mission);
        TheReservationAnswers(() => TheWarehouseAnswers(6, "A6"));

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["GS"].ItemGuid);

        Assert.Equal(MissionDestinationOutcome.NoRoute, result.Outcome);
        AssertTheCellIsLostAndTheMissionIsBackWithTheNavigator(mission, oldStep);
        await TheNavigatorReservesACellInS7Again(mission);
    }

    /// <summary>
    /// La stessa cosa per una prenotazione che prende l'altra porta di S8, B8, il cui indirizzo di
    /// campo non si legge.
    /// </summary>
    [Fact]
    public async Task AReservedCellWhoseAddressCannotBeReadLosesTheCellTheMissionHeld()
    {
        var mission = await AMissionOnPInTheStorageScene();
        await S8HasASecondDoorB8OnCell10();
        var oldStep = TheMissionHoldsCell7WithItsAddress(mission);
        TheReservationAnswers(() =>
        {
            TheWarehouseAnswers(10, "B8");
            _warehouse.UnavailableOnCells.Add(10);
        });

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid);

        Assert.Equal(MissionDestinationOutcome.WarehouseUnavailable, result.Outcome);
        AssertTheCellIsLostAndTheMissionIsBackWithTheNavigator(mission, oldStep);
        await TheNavigatorReservesACellInS7Again(mission);
    }

    /// <summary>
    /// La stessa cosa per un commit rifiutato: dopo la prenotazione, quando il commit ha gia' letto la
    /// versione della rotta e guardato la posizione, la strategia <c>Standard</c> di P, che non ha visto
    /// uno step da fare, toglie la rotta (la versione sale), e il commit perde il confronto. Fino al
    /// compito 5 della voce 41 questa rimozione la escludeva il semaforo di P, e il rifiuto veniva dal
    /// semaforo preso.
    /// </summary>
    [Fact]
    public async Task ACommitRefusedAfterTheReservationLosesTheCellTheMissionHeld()
    {
        var mission = await AMissionOnPInTheStorageScene();
        var oldStep = TheMissionHoldsCell7WithItsAddress(mission);
        mission.CommitRoutePauseForTesting = () =>
        {
            mission.CommitRoutePauseForTesting = null;
            Assert.True(mission.RemoveTheRouteSeenWithoutAStepToDo(mission.RouteVersion));
        };

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid);

        Assert.Equal(MissionDestinationOutcome.StepInFlight, result.Outcome);
        AssertTheCellIsLostAndTheMissionIsBackWithTheNavigator(mission, oldStep);
        await TheNavigatorReservesACellInS7Again(mission);
    }

    /// <summary>
    /// La corsa 4 della ricognizione della voce 41, con il flusso del magazzino: mentre il gestionale
    /// prenota la cella 8 in S8, P comincia un'esecuzione (il suo semaforo preso, per esempio
    /// l'<c>ExecuteStep(500)</c> dopo un arrivo). Il commit non prende il semaforo: il cambio passa, la
    /// missione ha la cella 8 con la porta A8, l'indirizzo e la rotta P->S8, e non perde niente. Prima il
    /// commit era rifiutato perche' «P sta eseguendo», e la missione perdeva la cella 7 che teneva.
    /// </summary>
    [Fact]
    public async Task APositionThatStartsExecutingDuringTheReservationDoesNotCostTheCell()
    {
        var mission = await AMissionOnPInTheStorageScene();
        var oldStep = TheMissionHoldsCell7WithItsAddress(mission);
        TheWarehouseAnswers(NewCellId, NewDoorCode);
        var held = false;
        TheReservationAnswers(() => held = _plant["P"].TryHoldExecutionForTesting());

        MissionDestinationResult result;
        try
        {
            result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid);
        }
        finally
        {
            if (held) _plant["P"].ReleaseExecutionHoldForTesting();
        }

        Assert.True(held);
        Assert.Equal(new MissionDestinationResult(MissionDestinationOutcome.Changed), result);
        Assert.Equal(NewCellId, _warehouse.StorageReservationsByMission[mission.MissionId].CellId);
        Assert.Equal(_plant["S8"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.OperatorDestination);
        Assert.False(mission.StorageCellLost);
        Assert.Equal(NewDoorCode, mission.StorageDoorCode);
        Assert.Equal("0011", mission.DestinationAsi);
        Assert.Equal(["P", "S8"], RouteOf(mission));
        Assert.DoesNotContain(oldStep, MissionSteps.Instance.List);
        Assert.Null(RoutesToBuild.Instance[mission.MissionId]);
    }

    /// <summary>
    /// La stessa cosa per una missione spostata su Q mentre il gestionale prenota: lo spostamento
    /// toglie la rotta, il cambio rifiutato toglie porta e indirizzo.
    /// </summary>
    [Fact]
    public async Task AMissionMovedDuringTheReservationLosesTheCellItHeld()
    {
        var mission = await AMissionOnPInTheStorageScene();
        var oldStep = TheMissionHoldsCell7WithItsAddress(mission);
        var reservations = 0;
        _warehouse.Before = async operation =>
        {
            if (operation != nameof(IWarehouse.ReserveStorageAsync) || ++reservations != 2) return;

            Assert.Equal(MissionMoveOutcome.Moved, MissionGateway.Move(mission.MissionId, _plant["Q"].ItemGuid));
            await mission.PositionWork;
        };

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid);

        Assert.Equal(MissionDestinationOutcome.StepInFlight, result.Outcome);
        Assert.Equal(_plant["Q"].ItemGuid, mission.PositionItemId);
        AssertTheCellIsLostAndTheMissionIsBackWithTheNavigator(mission, oldStep);
        await FromQTheNavigatorFindsNoRouteToS7AndAsksNothing(mission);
    }

    /// <summary>
    /// La stessa cosa per un errore imprevisto dopo la prenotazione (alla lettura della cella 10, la
    /// porta B8 che la prenotazione ha preso): l'errore risale, la cella 10 torna al gestionale, e la
    /// missione non va verso la 7.
    /// </summary>
    [Fact]
    public async Task AnUnexpectedErrorAfterTheReservationLosesTheCellTheMissionHeld()
    {
        var mission = await AMissionOnPInTheStorageScene();
        await S8HasASecondDoorB8OnCell10();
        var oldStep = TheMissionHoldsCell7WithItsAddress(mission);
        var reservations = 0;
        _warehouse.Before = operation =>
        {
            if (operation == nameof(IWarehouse.ReserveStorageAsync) && ++reservations == 2)
                TheWarehouseAnswers(10, "B8");
            else if (operation == nameof(IWarehouse.DescribeCellAsync) && reservations == 2)
                throw new InvalidOperationException("guasto di prova");

            return Task.CompletedTask;
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid));

        AssertTheCellIsLostAndTheMissionIsBackWithTheNavigator(mission, oldStep);
        ChangeDestinationStepStartsTest.NoStepStartIsLeftCounted(mission);
        await TheNavigatorReservesACellInS7Again(mission);
    }

    /// <summary>
    /// La missione va in S7 verso la porta A7, ma il gestionale non le lega nessuna prenotazione (la
    /// porta l'ha avuta per un'altra via, come il ritorno nella cella da cui l'UDC e' uscita): il
    /// cambio rifiutato dopo la prenotazione non le toglie niente, perche' il rilascio non ha toccato
    /// quella cella. Porta, rotta e coda restano come prima.
    /// </summary>
    [Fact]
    public async Task ADoorTheMissionDoesNotHoldByReservationIsNotDropped()
    {
        var mission = await AMissionOnPInTheStorageScene();
        var oldStep = TheMissionHoldsCell7WithItsAddress(mission);
        _warehouse.StorageReservationsByMission.Remove(mission.MissionId);
        TheReservationAnswers(() => throw new WarehouseUnavailableException(nameof(IWarehouse.ReserveStorageAsync)));

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid);

        Assert.Equal(MissionDestinationOutcome.WarehouseUnavailable, result.Outcome);
        Assert.Equal(OldDoorCode, mission.StorageDoorCode);
        Assert.Equal("0001", mission.DestinationAsi);
        Assert.Same(oldStep, Assert.Single(mission.MissionSteps));
        Assert.Null(RoutesToBuild.Instance[mission.MissionId]);
    }

    // ---------------------------------------------------------------------------------------
    // La cella persa: il navigatore la rifa' per la scelta dell'operatore
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// L'operatore manda la missione in S8 (cella 8), poi in S9, e la prenotazione verso S9 non ha
    /// risposta: la missione resta dell'operatore verso S8, senza porta ne' rotta, in coda. Il giro
    /// delle rotte del navigatore le prenota una cella in S8 con il flusso del cambio (domanda,
    /// prenotazione, porta in S8, rotta) e rifa' la rotta verso la sua porta. Prima nessuno la
    /// rimetteva in coda, e il ricalcolo per una scelta dell'operatore non chiedeva niente al
    /// gestionale.
    /// </summary>
    [Fact]
    public async Task AfterARefusedChangeTheNavigatorReservesACellInTheStorageTheOperatorChose()
    {
        var mission = await AMissionTheOperatorSentToS8();
        TheWarehouseAnswers(OtherCellId, OtherDoorCode);
        TheReservationAnswers(() => throw new WarehouseUnavailableException(nameof(IWarehouse.ReserveStorageAsync)));

        Assert.Equal(MissionDestinationOutcome.WarehouseUnavailable,
            (await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S9"].ItemGuid)).Outcome);
        Assert.Null(mission.StorageDoorCode);
        Assert.Empty(mission.MissionSteps);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
        Assert.Equal(_plant["S8"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.OperatorDestination);
        Assert.True(mission.StorageCellLost);
        _warehouse.Before = null;
        _warehouse.StorageRequests.Clear();
        TheWarehouseAnswers(NewCellId, NewDoorCode);

        await TheRouteLoopLooksForTheCell(mission);

        Assert.Equal(2, _warehouse.StorageRequests.Count);
        Assert.False(_warehouse.StorageRequests[0].AssignPosition);
        Assert.True(_warehouse.StorageRequests[1].AssignPosition);
        Assert.Equal(NewCellId, _warehouse.StorageReservationsByMission[mission.MissionId].CellId);
        Assert.Equal(NewDoorCode, mission.StorageDoorCode);
        Assert.Equal("0011", mission.DestinationAsi);
        Assert.Equal(_plant["S8"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.OperatorDestination);
        Assert.False(mission.StorageCellLost);
        Assert.Equal(["P", "S8"], RouteOf(mission));
    }

    /// <summary>
    /// La missione dell'operatore va in S8 con la cella 8 e la rotta, e il gestionale non le tiene
    /// piu' la cella (revocata): il giro delle rotte se ne accorge chiedendo, lascia la porta A8 e
    /// le prenota un'altra cella di S8, la 10 della porta B8, con la rotta verso di lei.
    /// </summary>
    [Fact]
    public async Task ACellTheWarehouseNoLongerHoldsIsReplacedByTheNavigator()
    {
        var mission = await AMissionTheOperatorSentToS8();
        var oldStep = Assert.Single(mission.MissionSteps);
        await S8HasASecondDoorB8OnCell10();
        _warehouse.RevokeStorageReservation(mission.MissionId);
        TheWarehouseAnswers(10, "B8");

        await TheRouteLoopLooksForTheCell(mission);

        Assert.Equal(10, _warehouse.StorageReservationsByMission[mission.MissionId].CellId);
        Assert.Equal("B8", mission.StorageDoorCode);
        Assert.Equal("0031", mission.DestinationAsi);
        Assert.True(mission.OperatorDestination);
        Assert.Equal(["P", "S8"], RouteOf(mission));
        Assert.DoesNotContain(oldStep, MissionSteps.Instance.List);
    }

    /// <summary>
    /// La missione dell'operatore va in S8 con la cella 8, che il gestionale le tiene: il giro delle
    /// rotte chiede soltanto (la domanda rinnova la prenotazione), e porta e rotta restano.
    /// </summary>
    [Fact]
    public async Task ACellTheWarehouseStillHoldsIsLeftAlone()
    {
        var mission = await AMissionTheOperatorSentToS8();
        var step = Assert.Single(mission.MissionSteps);

        await TheRouteLoopLooksForTheCell(mission);

        Assert.Equal([nameof(IWarehouse.GetStorageReservationAsync)], _warehouse.Calls);
        Assert.Equal(NewDoorCode, mission.StorageDoorCode);
        Assert.Same(step, Assert.Single(mission.MissionSteps));
    }

    /// <summary>
    /// Il gestionale tiene per la missione una cella, ma non quella della sua porta (la 9 invece della
    /// 8 di A8): la cella della porta non e' sua, e il navigatore la sostituisce come una cella persa,
    /// invece di confermarla.
    /// </summary>
    [Fact]
    public async Task ACellTheWarehouseHoldsThatIsNotTheDoorsIsReplaced()
    {
        var mission = await AMissionTheOperatorSentToS8();
        _warehouse.StorageReservationsByMission[mission.MissionId] = new StorageReservation(mission.MissionId,
            StorageReservationOutcome.CellFound, OtherCellId, OtherDoorCode, Partition: "0023", StorageDepth: 150,
            NeedsCompaction: false);
        await S8HasASecondDoorB8OnCell10();
        TheWarehouseAnswers(10, "B8");

        await TheRouteLoopLooksForTheCell(mission);

        Assert.Contains(nameof(IWarehouse.ReserveStorageAsync), _warehouse.Calls);
        Assert.Equal("B8", mission.StorageDoorCode);
        Assert.Equal(10, _warehouse.StorageReservationsByMission[mission.MissionId].CellId);
    }

    /// <summary>
    /// Il gestionale non risponde alla domanda sulla cella: non si sa se e' ancora della missione, e
    /// la porta e la rotta restano, come prima del compito. Ma la cella non e' confermata: il giro
    /// delle rotte rimette la missione in coda (la domanda si rifa' con il freno), e il deposito da P
    /// non parte. Prima il giro rispondeva «niente da fare» e svegliava P, il cui deposito senza
    /// conferma rimetteva la missione in coda: un giro continuo di domande a un gestionale che non
    /// risponde.
    /// </summary>
    [Fact]
    public async Task WhenTheWarehouseDoesNotSayTheCellStays()
    {
        var previousDebugLog = Navigator.ActivateDebugLog;
        using var log = new RecordingLogWriter();
        Navigator.ActivateDebugLog = true;

        try
        {
            await WhenTheWarehouseDoesNotSayTheCellStaysCore(log);
        }
        finally
        {
            Navigator.ActivateDebugLog = previousDebugLog;
        }
    }

    private async Task WhenTheWarehouseDoesNotSayTheCellStaysCore(RecordingLogWriter log)
    {
        var mission = await AMissionTheOperatorSentToS8();
        var step = Assert.Single(mission.MissionSteps);
        _plant["P"].RecordStorageLength = RecordStorageTypes.FromPlc;
        _warehouse.UnavailableOperations.Add(nameof(IWarehouse.GetStorageReservationAsync));

        await TheRouteLoopLooksForTheCell(mission);

        // La strada e' quella di «non si sa», non un'eccezione sfuggita e raccolta dal giro, che
        // lascerebbe porta, rotta e coda come qui.
        await log.Wait((line, _) => line.Contains(mission.MissionId.ToString()) &&
                                    line.Contains("non si sa se la cella della porta"))
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.DoesNotContain(nameof(IWarehouse.ReserveStorageAsync), _warehouse.Calls);
        Assert.Equal(NewDoorCode, mission.StorageDoorCode);
        Assert.Same(step, Assert.Single(mission.MissionSteps));
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);

        Assert.NotNull(_plant["P"].StorageManager);
        await _plant["P"].StorageManager!.StartStoreLu();

        Assert.Equal(Guid.Empty, _plant["P"].RunningStepId);
    }

    /// <summary>
    /// Lo stesso senza un gestionale configurato: la cella non si puo' confermare, porta e rotta
    /// restano, e la missione torna in coda invece di far partire il giro di risvegli.
    /// </summary>
    [Fact]
    public async Task WithoutAWarehouseTheCellStaysUnconfirmed()
    {
        var mission = await AMissionTheOperatorSentToS8();
        var step = Assert.Single(mission.MissionSteps);
        Navigator.Warehouse = null;

        await TheRouteLoopLooksForTheCell(mission);

        Assert.Equal(NewDoorCode, mission.StorageDoorCode);
        Assert.Same(step, Assert.Single(mission.MissionSteps));
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
    }

    /// <summary>
    /// La cella persa di una destinazione della configurazione, e la missione che intanto ha di nuovo
    /// una porta in S7 che il gestionale le tiene (la cella 7, prenotata per un'altra via): il giro
    /// delle rotte chiede soltanto, vede che la cella e' sua, e la cella persa cade; niente si
    /// riprenota, e la missione torna a fare il giro della configurazione.
    /// </summary>
    [Fact]
    public async Task ACellTheMissionHoldsAgainEndsTheLostCell()
    {
        var mission = await AMissionOnPInTheStorageScene();
        TheMissionHoldsCell7WithItsAddress(mission);
        TheReservationAnswers(() => throw new WarehouseUnavailableException(nameof(IWarehouse.ReserveStorageAsync)));
        Assert.Equal(MissionDestinationOutcome.WarehouseUnavailable,
            (await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid)).Outcome);
        Assert.True(mission.StorageCellLost);
        _warehouse.Before = null;
        mission.StorageDoorCode = OldDoorCode;
        _warehouse.StorageReservationsByMission[mission.MissionId] = new StorageReservation(mission.MissionId,
            StorageReservationOutcome.CellFound, OldCellId, OldDoorCode, Partition: "0003", StorageDepth: 150,
            NeedsCompaction: false);
        _warehouse.Calls.Clear();

        await TheRouteLoopLooksForTheCell(mission);

        Assert.Equal([nameof(IWarehouse.GetStorageReservationAsync)], _warehouse.Calls);
        Assert.False(mission.StorageCellLost);
        Assert.Equal(OldDoorCode, mission.StorageDoorCode);
        Assert.Equal(_plant["S7"].ItemGuid, mission.DestinationItemId);
    }

    /// <summary>
    /// La cella persa vale per il magazzino della destinazione: se la destinazione cambia per
    /// un'altra via (un rifiuto, un distributore circolare), cade come il segno dell'operatore.
    /// </summary>
    [Fact]
    public async Task ADestinationChangedAnotherWayEndsTheLostCell()
    {
        var mission = await AMissionOnPInTheStorageScene();
        TheMissionHoldsCell7WithItsAddress(mission);
        TheReservationAnswers(() => throw new WarehouseUnavailableException(nameof(IWarehouse.ReserveStorageAsync)));
        Assert.Equal(MissionDestinationOutcome.WarehouseUnavailable,
            (await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid)).Outcome);
        Assert.True(mission.StorageCellLost);

        mission.DestinationItemId = _plant["X"].ItemGuid;

        Assert.False(mission.StorageCellLost);
    }

    /// <summary>
    /// La cella dell'operatore e' persa e S8 non ne ha un'altra: niente porta, niente rotta (non verso
    /// il magazzino nudo), e la missione torna in coda per il prossimo giro.
    /// </summary>
    [Fact]
    public async Task WithoutACellTheNavigatorLeavesTheMissionWithoutARoute()
    {
        var mission = await AMissionTheOperatorSentToS8();
        _warehouse.RevokeStorageReservation(mission.MissionId);
        _warehouse.StorageReservations[LoadUnitId] = new StorageReservation(Guid.Empty,
            StorageReservationOutcome.NoFreeCells, 0, string.Empty, string.Empty, 0, NeedsCompaction: false);

        await TheRouteLoopLooksForTheCell(mission);

        Assert.Null(mission.StorageDoorCode);
        Assert.Empty(mission.MissionSteps);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
        Assert.Equal(_plant["S8"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.OperatorDestination);
    }

    /// <summary>
    /// Mentre il navigatore prenota la cella nuova (il gestionale risponde alla domanda), nessun
    /// dispositivo comincia a lavorare sulla missione: la missione e' ferma come per un cambio
    /// dell'operatore, e alla fine non resta ferma.
    /// </summary>
    [Fact]
    public async Task WhileTheNavigatorReservesTheMissionStaysStill()
    {
        var mission = await AMissionTheOperatorSentToS8();
        _warehouse.RevokeStorageReservation(mission.MissionId);

        // ReserveStorageAsked si e' gia' completato con il cambio della scena: la domanda del
        // navigatore si ferma e si annuncia qui.
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource();
        _warehouse.Before = operation =>
        {
            if (operation != nameof(IWarehouse.ReserveStorageAsync)) return Task.CompletedTask;

            asked.TrySetResult();
            return answer.Task;
        };

        StartCellSearch(mission, _plant["P"]);
        var search = Navigator.CellSearchInFlight(mission.MissionId);
        Assert.NotNull(search);
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(30));

        try
        {
            Assert.False(mission.TryBeginStepStart());
        }
        finally
        {
            answer.SetResult();
        }

        await search.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(NewDoorCode, mission.StorageDoorCode);
        ChangeDestinationStepStartsTest.NoStepStartIsLeftCounted(mission);
    }

    /// <summary>
    /// Una missione dell'operatore verso S8 senza porta, come la lascia la cella persa: chi ricostruisce
    /// la rotta fuori dal giro delle rotte (un dispositivo, la ricarica) non la manda nel magazzino nudo,
    /// dove arriverebbe senza un posto. La cella la prenota il navigatore.
    /// </summary>
    [Fact]
    public async Task AnOperatorsStorageWithoutADoorGetsNoRouteToTheBareStorage()
    {
        var mission = await AMissionOnPInTheStorageScene();
        Assert.True(mission.SetOperatorDestination(_plant["S8"].ItemGuid));
        Assert.True(string.IsNullOrEmpty(mission.StorageDoorCode));

        var outcome = await Navigator.FindWhereStoreLu(mission, _plant["P"], _plant["P"].LookWhereAllocateLu);

        Assert.Equal(AutomationDevice.RouteBuildStatusTypes.NotAvailable, outcome);
        Assert.Empty(mission.MissionSteps);
    }

    /// <summary>
    /// La cella persa di una destinazione della configurazione (S7, senza segno), e la configurazione
    /// di P che la ricostruirebbe a modo suo: la destinazione fissa X (che riscriverebbe la
    /// destinazione: un cambio rifiutato finiva con un'altra destinazione), una modalita' di deposito
    /// (che prenoterebbe la cella collegata di P, ovunque sia), il controllo delle celle, o la
    /// modalita' <c>No</c> senza destinazione fissa (la rotta verso il magazzino nudo S7). Nessuna
    /// tocca la missione: niente rotta, niente prenotazione, la destinazione resta S7 e la cella resta
    /// persa, da rifare per il navigatore con il flusso del cambio.
    /// </summary>
    [Theory]
    [InlineData(LookWhereAllocateLuTypes.No, false)]
    [InlineData(LookWhereAllocateLuTypes.No, true)]
    [InlineData(LookWhereAllocateLuTypes.CheckOnly, false)]
    [InlineData(LookWhereAllocateLuTypes.BuildRouteToStore, false)]
    [InlineData(LookWhereAllocateLuTypes.BuildRouteToDifferentStoreLocation, false)]
    public async Task TheConfigurationDoesNotRedoALostCell(LookWhereAllocateLuTypes mode, bool fixedDestination)
    {
        var mission = await AMissionOnPInTheStorageScene(mode);
        if (fixedDestination) _plant["P"].DestinationItemId = _plant["X"].ItemGuid;
        TheMissionHoldsCell7WithItsAddress(mission);
        TheReservationAnswers(() => throw new WarehouseUnavailableException(nameof(IWarehouse.ReserveStorageAsync)));
        Assert.Equal(MissionDestinationOutcome.WarehouseUnavailable,
            (await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid)).Outcome);
        Assert.True(mission.StorageCellLost);
        _warehouse.Before = null;
        _warehouse.Calls.Clear();
        TheWarehouseAnswers(NewCellId, NewDoorCode);

        var outcome = await Navigator.FindWhereStoreLu(mission, _plant["P"], _plant["P"].LookWhereAllocateLu);

        Assert.Equal(AutomationDevice.RouteBuildStatusTypes.NotAvailable, outcome);
        Assert.DoesNotContain(nameof(IWarehouse.ReserveStorageAsync), _warehouse.Calls);
        Assert.False(_warehouse.StorageReservationsByMission.ContainsKey(mission.MissionId));
        Assert.Empty(mission.MissionSteps);
        Assert.Null(mission.StorageDoorCode);
        Assert.Equal(_plant["S7"].ItemGuid, mission.DestinationItemId);
        Assert.False(mission.OperatorDestination);
        Assert.True(mission.StorageCellLost);
    }

    /// <summary>
    /// La cella persa di una destinazione della configurazione, poi il riavvio del motore: la
    /// missione rinasce dalla sua riga con la cella persa, la ricostruzione della rotta della
    /// ricarica non la manda nel magazzino nudo S7, e il giro delle rotte le prenota la cella in S7
    /// con il flusso del cambio. Senza la riga, la ricarica la trattava come una missione della
    /// configurazione verso S7 senza porta: la rotta P->S7, e niente prenotato.
    /// </summary>
    [Fact]
    public async Task AfterARestartTheLostCellIsStillTheNavigatorsToRedo()
    {
        var mission = await AMissionOnPInTheStorageScene();
        TheMissionHoldsCell7WithItsAddress(mission);
        TheReservationAnswers(() => throw new WarehouseUnavailableException(nameof(IWarehouse.ReserveStorageAsync)));
        Assert.Equal(MissionDestinationOutcome.WarehouseUnavailable,
            (await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid)).Outcome);
        Assert.True(mission.StorageCellLost);

        // Il riavvio: le collezioni di esecuzione si svuotano, i dispositivi restano (PathDevices
        // non si svuota alla ricarica), e le missioni rinascono dalle righe.
        ActiveMissions.Instance.RemoveAll(_ => true);
        MissionSteps.Instance.RemoveAll(_ => true);
        RoutesToBuild.Instance.Items = new();
        Navigator.NavStatus = NavigatorStatusTypes.Recover;

        await ORM.DbInfo.RecoverFailure();

        var reloaded = ActiveMissions.Instance[mission.MissionId];
        Assert.NotNull(reloaded);
        Assert.NotSame(mission, reloaded);
        await reloaded.PositionWork;
        await reloaded.RouteWork.WaitAsync(TimeSpan.FromSeconds(30));
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        Assert.True(reloaded.StorageCellLost);
        Assert.Equal(_plant["S7"].ItemGuid, reloaded.DestinationItemId);
        Assert.Null(reloaded.StorageDoorCode);

        // La ricostruzione che fa la ricarica (ActiveMissions.Add, in un lavoro che non si puo'
        // attendere), rifatta qui: nessuna rotta verso il magazzino nudo.
        await reloaded.BuildPathToDestination();
        Assert.Empty(reloaded.MissionSteps);

        await TheNavigatorReservesACellInS7Again(reloaded);
    }

    // ---------------------------------------------------------------------------------------
    // La cella persa di una missione della configurazione, e il magazzino senza celle
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Risposta 5 del proprietario del 2/10: la missione della configurazione ha perso la cella in S7,
    /// e S7 non ne ha un'altra. La cerca negli altri magazzini del ricollocatore che fa il deposito, P,
    /// con la ricerca che P fa per se' (le celle collegate a P, nel magazzino della sua cella collegata)
    /// ancorata a P anche quando la missione e' ancora sull'ingresso I: il gestionale propone la cella 8
    /// di S8, e la missione ci va, con la cella prenotata, porta, indirizzo e la rotta da dove e'. Prima
    /// restava senza rotta verso S7 e il navigatore riprovava con il freno; e da I la ricerca di P per
    /// se' non chiedeva niente, perche' I non ha una cella collegata.
    /// </summary>
    [Theory]
    [InlineData("P", new[] { "P", "S8" })]
    [InlineData("I", new[] { "I", "P", "S8" })]
    public async Task AConfigurationMissionWhoseStorageIsFullGoesToAnotherStorageOfItsRelocator(string on, string[] route)
    {
        var mission = await AConfigurationMissionThatLostItsCellInS7(on);
        TheStorageIsFullAndThenTheWarehouseAnswers(() => TheWarehouseAnswers(NewCellId, NewDoorCode));

        await TheRouteLoopLooksForTheCell(mission);

        Assert.Equal([false, true], _warehouse.StorageRequests.Select(request => request.AssignPosition));
        Assert.Equal([_plant["P"].ItemGuid], _warehouse.StorageRequests[1].ReachableDeviceIds);
        Assert.Equal(StorageCellSearchMode.ConnectedAutomaticOperator, _warehouse.StorageRequests[1].CellSearchMode);
        Assert.Equal(NewCellId, _warehouse.StorageReservationsByMission[mission.MissionId].CellId);
        Assert.Equal(_plant["S8"].ItemGuid, mission.DestinationItemId);
        Assert.Equal(NewDoorCode, mission.StorageDoorCode);
        Assert.Equal("0011", mission.DestinationAsi);
        Assert.Equal("0012", mission.DestinationSubitem);
        Assert.False(mission.StorageCellLost);
        Assert.True(mission.StorageCellReserved);
        Assert.False(mission.OperatorDestination);
        Assert.Equal(MissionTypes.MovingToItem, mission.MissionType);
        Assert.Equal(route, RouteOf(mission));
        Assert.Null(RoutesToBuild.Instance[mission.MissionId]);
    }

    /// <summary>
    /// La stessa ricerca negli altri magazzini di P prenota la cella 8 di S8, scrive porta e destinazione
    /// e calcola la rotta P->S8; prima che la scriva un altro manda la missione verso X (voce 41, rilievo
    /// 1 del secondo giro del compito 3). La rotta non si scrive, e la missione resta come l'ha lasciata
    /// chi ha vinto, in coda al giro delle rotte. Chi ha perso restituisce la cella 8 al gestionale, ne
    /// toglie la porta e conta il fallimento nel freno, e la missione resta senza cella (risposta 6 del
    /// proprietario dell'11/10). Prima la rotta mancante era «la porta non va»: la cella tornava al
    /// gestionale e la missione tornava verso S7 con la cella persa, sopra la destinazione di chi aveva
    /// vinto; e dopo il compito 3 della voce 41 la cella 8 restava sua, senza contare niente.
    /// </summary>
    [Fact]
    public async Task AnotherStorageWhoseRouteIsTakenOverGivesBackTheCellAndKeepsWhatTheWinnerWrote()
    {
        var mission = await AConfigurationMissionThatLostItsCellInS7();
        TheStorageIsFullAndThenTheWarehouseAnswers(() => TheWarehouseAnswers(NewCellId, NewDoorCode));
        var failuresBefore = Navigator.RoutePathRetries.Failures(mission.MissionId);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int releasesAtThePause;
        mission.RouteBuildPauseForTesting = () =>
        {
            reached.TrySetResult();
            return released.Task;
        };

        try
        {
            StartCellSearch(mission, _plant["P"]);
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(_plant["S8"].ItemGuid, mission.DestinationItemId);
            Assert.Equal(NewCellId, _warehouse.StorageReservationsByMission[mission.MissionId].CellId);
            releasesAtThePause = _warehouse.ReleasedStorageReservations.Count;
            mission.DestinationItemId = _plant["X"].ItemGuid;
        }
        finally
        {
            released.TrySetResult();
        }

        await (Navigator.CellSearchInFlight(mission.MissionId) ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(_plant["X"].ItemGuid, mission.DestinationItemId);
        Assert.False(mission.StorageCellLost);
        Assert.True(string.IsNullOrEmpty(mission.StorageDoorCode));
        Assert.Empty(mission.MissionSteps);
        Assert.False(_warehouse.StorageReservationsByMission.ContainsKey(mission.MissionId));
        Assert.Equal(releasesAtThePause + 1, _warehouse.ReleasedStorageReservations.Count);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
        Assert.Equal(failuresBefore + 1, Navigator.RoutePathRetries.Failures(mission.MissionId));
        Assert.Null(mission.TakeStorageCellConfirmation());
    }

    /// <summary>
    /// La stessa ricerca negli altri magazzini di P, ma mentre il gestionale risponde il rigetto dal campo
    /// trova lo step P->X in volo e si rimanda; lo step finisce, e la ricerca prenota la cella 8 di S8 e
    /// calcola la rotta P->S8. La rotta non si scrive, perche' la missione aspetta il rigetto che il giro
    /// rifa': la cella 8 torna al gestionale, ma il freno non conta niente e la riga dice il perche' vero.
    /// Poi il giro rifa' il rigetto verso X. Fino al secondo giro di correzioni del compito 8 della voce 41
    /// la guardia rispondeva come chi ha perso il confronto della versione: un fallimento nel freno, e la
    /// riga diceva che la rotta l'aveva cambiata un altro.
    /// </summary>
    [Fact]
    public async Task AnotherStorageWhileThePostponedFieldRejectionWaitsCountsNoFailure()
    {
        var mission = await AConfigurationMissionThatLostItsCellInS7();
        _plant["P"].RejectItemDestinationIdGetLuInfo = _plant["X"].ItemGuid;
        TheStorageIsFullAndThenTheWarehouseAnswers();
        var calls = 0;
        var releasesAtTheSecondReservation = -1;
        _warehouse.Before = async operation =>
        {
            if (operation != nameof(IWarehouse.ReserveStorageAsync) || ++calls != 2)
                return;

            releasesAtTheSecondReservation = _warehouse.ReleasedStorageReservations.Count;
            TheWarehouseAnswers(NewCellId, NewDoorCode);
            var stepPx = _plant.PlannedStep(mission, "P", "X");
            _plant["P"].RunningStepId = stepPx.StepId;
            Assert.False(await mission.RejectTo(PathMission.RejectionTypes.NoLuDataFromPlc));
            _plant["P"].RunningStepId = Guid.Empty;
            MissionSteps.Instance.Remove(stepPx);
        };
        var failuresBefore = Navigator.RoutePathRetries.Failures(mission.MissionId);

        // La riga e' un'informazione: si scrive con la diagnostica accesa, e la console la riceve prima
        // che la ricerca finisca.
        var previousDebugLog = Navigator.ActivateDebugLog;
        Navigator.ActivateDebugLog = true;
        IReadOnlyList<string> lines;
        var console = new RecordingConsole();

        try
        {
            StartCellSearch(mission, _plant["P"]);
            await (Navigator.CellSearchInFlight(mission.MissionId) ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            lines = console.Lines;
            console.Dispose();
            Navigator.ActivateDebugLog = previousDebugLog;
        }

        Assert.Equal(2, calls);
        Assert.Empty(mission.MissionSteps);
        Assert.False(_warehouse.StorageReservationsByMission.ContainsKey(mission.MissionId));
        Assert.Equal(releasesAtTheSecondReservation + 1, _warehouse.ReleasedStorageReservations.Count);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
        Assert.Equal(failuresBefore, Navigator.RoutePathRetries.Failures(mission.MissionId));
        var ours = lines.Where(line => line.Contains(mission.MissionId.ToString())).ToList();
        Assert.Contains(ours, line => line.Contains("e' tornata al gestionale: la missione aspetta il rigetto dal campo"));
        Assert.DoesNotContain(ours, line => line.Contains("l'ha cambiata un altro"));

        _warehouse.Before = null;
        StartCellSearch(mission, _plant["P"]);
        await (Navigator.CellSearchInFlight(mission.MissionId) ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(MissionTypes.Rejection, mission.MissionType);
        Assert.Equal(_plant["X"].ItemGuid, mission.DestinationItemId);
    }

    /// <summary>
    /// Il magazzino della destinazione e gli altri di P non hanno celle, e P ha un punto di scarto R:
    /// la missione va allo scarto come nel flusso del deposito (<c>RejectTo</c> per mancanza di celle),
    /// con la causa sul comando, la rotta verso il punto di scarto e il tipo di un rigetto, e non torna
    /// in coda. Prima restava senza rotta verso S7, e il navigatore chiedeva di nuovo con il freno, per
    /// sempre. Con P in un gruppo che ha il suo punto di scarto, R2, vale quello del gruppo, come in
    /// <c>RejectTo</c>. La modalita' di deposito di P non conta: qui e' <c>No</c>, che di suo non
    /// rigetta mai.
    /// </summary>
    [Theory]
    [InlineData(false, "R")]
    [InlineData(true, "R2")]
    public async Task AConfigurationMissionWithoutACellAnywhereGoesToTheRejectionPoint(bool groupRejectionPoint,
        string rejectionPoint)
    {
        var mission = await AConfigurationMissionThatLostItsCellInS7();
        _plant.Device("R");
        _plant.Link("P", "R");
        _plant["P"].RejectItemDestinationAllocateLu = _plant["R"].ItemGuid;
        if (groupRejectionPoint)
        {
            var group = _plant.Device("GP", DeviceTypes.GroupOfAutomationDevices);
            group.GroupItemId = 91;
            _plant["P"].BelongingGroupId = group.GroupItemId;
            Assert.Same(group, _plant["P"].BelongedGroup);
            _plant.Device("R2");
            _plant.Link("P", "R2");
            group.RejectItemDestinationAllocateLu = _plant["R2"].ItemGuid;
        }

        var command = new Command { Status = Command.StatusTypes.Running };
        await ORM.CommandToDo.Update(command);
        mission.IdCommandToDo = command.IdCommandToDo;
        TheStorageIsFullAndThenTheWarehouseAnswers();

        await TheRouteLoopLooksForTheCell(mission);

        Assert.Equal([false, true], _warehouse.StorageRequests.Select(request => request.AssignPosition));
        Assert.False(_warehouse.StorageReservationsByMission.ContainsKey(mission.MissionId));
        Assert.Equal(_plant[rejectionPoint].ItemGuid, mission.DestinationItemId);
        Assert.Equal(MissionTypes.Rejection, mission.MissionType);
        Assert.False(mission.StorageCellLost);
        Assert.False(mission.OperatorDestination);
        Assert.Null(mission.StorageDoorCode);
        Assert.Equal(["P", rejectionPoint], RouteOf(mission));
        Assert.Null(RoutesToBuild.Instance[mission.MissionId]);
        await using var context = _database.CreateContext();
        var row = Assert.Single(context.Commands);
        Assert.Equal((short)Command.StatusTypes.Done, row.Status);
        Assert.Equal("Rejection Cause: No storage cell available", row.ErrorDescription);
    }

    /// <summary>
    /// La porta che la ricerca negli altri magazzini di P propone, A11 di S11 (un magazzino di P), da
    /// P non si raggiunge: la cella non e' trovata, e la missione va allo scarto, con la causa che la
    /// ricerca di P ha scritto, sulla missione e sul comando (revisione finale della voce 39: prima la
    /// causa generica di una cella che manca la copriva). Prima che la rotta si guardasse, la
    /// ricerca di P rispondeva «trovata» anche senza step, e la missione restava ferma verso S11 senza
    /// rotta, fuori dalla coda. La cella 11, che la ricerca aveva prenotato, torna al gestionale: prima
    /// del primo giro di correzioni restava della missione allo scarto, finche' scadeva.
    /// </summary>
    [Fact]
    public async Task AnotherStorageOfTheRelocatorWithoutARouteToItsDoorIsNotTaken()
    {
        var mission = await AConfigurationMissionThatLostItsCellInS7();
        await S11IsAStorageOfPWithoutARouteFromP();
        _plant.Device("R");
        _plant.Link("P", "R");
        _plant["P"].RejectItemDestinationAllocateLu = _plant["R"].ItemGuid;
        var command = new Command { Status = Command.StatusTypes.Running };
        await ORM.CommandToDo.Update(command);
        mission.IdCommandToDo = command.IdCommandToDo;
        TheStorageIsFullAndThenTheWarehouseAnswers(() => TheWarehouseAnswers(11, "A11"));

        await TheRouteLoopLooksForTheCell(mission);

        Assert.Equal([false, true], _warehouse.StorageRequests.Select(request => request.AssignPosition));
        Assert.False(_warehouse.StorageReservationsByMission.ContainsKey(mission.MissionId));
        Assert.Equal(_plant["R"].ItemGuid, mission.DestinationItemId);
        Assert.Equal(MissionTypes.Rejection, mission.MissionType);
        Assert.Equal(["P", "R"], RouteOf(mission));
        Assert.Null(RoutesToBuild.Instance[mission.MissionId]);
        Assert.Equal("No item linked to storage door cell", mission.StatusDescription);
        await using var context = _database.CreateContext();
        var row = Assert.Single(context.Commands);
        Assert.Equal((short)Command.StatusTypes.Done, row.Status);
        Assert.Equal("Rejection Cause: No item linked to storage door cell", row.ErrorDescription);
    }

    /// <summary>
    /// Nessuna cella da nessuna parte, e nessun punto di scarto configurato per P ne' per un suo gruppo:
    /// la missione aspetta e ritenta con il freno, verso S7 e con la cella persa, e il registro lo dice
    /// con un avviso. Senza guardare prima il punto di scarto, il rigetto non fatto rispondeva
    /// «rigettata» come nel flusso del deposito, e la missione restava ferma, senza rotta e fuori dalla
    /// coda.
    /// </summary>
    [Fact]
    public async Task WithoutARejectionPointTheConfigurationMissionWaitsUnderTheBrakeWithAWarning()
    {
        using var log = new RecordingLogWriter();
        var mission = await AConfigurationMissionThatLostItsCellInS7();
        TheStorageIsFullAndThenTheWarehouseAnswers();

        await TheRouteLoopLooksForTheCell(mission);

        Assert.Equal([false, true], _warehouse.StorageRequests.Select(request => request.AssignPosition));
        Assert.Equal(_plant["S7"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.StorageCellLost);
        Assert.Null(mission.StorageDoorCode);
        Assert.Empty(mission.MissionSteps);
        Assert.Equal(MissionTypes.MovingToItem, mission.MissionType);
        Assert.Equal("No storage cell available", mission.StatusDescription);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
        Assert.Equal(2, Navigator.RoutePathRetries.RegisterFailure(mission.MissionId));
        await log.Wait((line, level) =>
                level == LogEntryType.Warning &&
                line.Contains(mission.MissionId.ToString()) &&
                line.Contains("nessun punto di scarto configurato"))
            .WaitAsync(TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// La ricerca negli altri magazzini di P prenota una cella la cui porta la missione non puo'
    /// prendere - A11 di S11, un magazzino di P che da P non si raggiunge, o A12 di S12, il magazzino
    /// di un altro ricollocatore, Q - e non c'e' un punto di scarto (primo giro di correzioni). La
    /// porta si guarda prima di scrivere, quindi la missione non e' toccata: ne' la porta, ne'
    /// l'indirizzo, ne' la destinazione, ne' la cella persa. La cella presa torna al gestionale, la
    /// missione aspetta con il freno verso S7 con la cella persa, e al giro dopo, con la cella 7 di
    /// nuovo libera, la riprenota in S7. Prima restava verso S11 (o S12) con la porta e la cella di
    /// la', senza la cella persa: il giro dopo non riprovava piu' S7, e la missione restava per sempre
    /// verso un magazzino senza rotta, o di un altro ricollocatore.
    /// </summary>
    [Theory]
    [InlineData("S11")]
    [InlineData("S12")]
    public async Task AProposalTheMissionCannotTakeLeavesItWaitingForItsStorage(string proposed)
    {
        using var log = new RecordingLogWriter();
        var mission = await AConfigurationMissionThatLostItsCellInS7();
        if (proposed == "S11")
        {
            await S11IsAStorageOfPWithoutARouteFromP();
            TheStorageIsFullAndThenTheWarehouseAnswers(() => TheWarehouseAnswers(11, "A11"));
        }
        else
        {
            await S12IsAStorageOfQ();
            TheStorageIsFullAndThenTheWarehouseAnswers(() => TheWarehouseAnswers(12, "A12"));
        }

        var written = TheWritesOf(mission);

        await TheRouteLoopLooksForTheCell(mission);

        Assert.Equal([false, true], _warehouse.StorageRequests.Select(request => request.AssignPosition));
        Assert.DoesNotContain(written, name => name is nameof(PathMission.StorageDoorCode) or
            nameof(PathMission.DestinationAsi) or nameof(PathMission.DestinationItemId) or
            nameof(PathMission.StorageCellLost));
        Assert.False(_warehouse.StorageReservationsByMission.ContainsKey(mission.MissionId));
        Assert.Equal(_plant["S7"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.StorageCellLost);
        Assert.Null(mission.StorageDoorCode);
        Assert.Equal(string.Empty, mission.DestinationAsi);
        Assert.Empty(mission.MissionSteps);
        Assert.Equal(MissionTypes.MovingToItem, mission.MissionType);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
        Assert.Equal(2, Navigator.RoutePathRetries.RegisterFailure(mission.MissionId));
        await log.Wait((line, level) =>
                level == LogEntryType.Warning &&
                line.Contains(mission.MissionId.ToString()) &&
                line.Contains("nessun punto di scarto configurato"))
            .WaitAsync(TimeSpan.FromSeconds(30));

        await TheNavigatorReservesACellInS7Again(mission);
    }

    /// <summary>
    /// La ricerca negli altri magazzini di P prenota la cella 9 di S9, e il gestionale cade sulla
    /// lettura del suo indirizzo (primo giro di correzioni). Il gestionale che non risponde non e'
    /// «nessuna cella»: niente scarto, anche con il punto di scarto R. La cella 9 torna al gestionale,
    /// e la missione aspetta con il freno verso S7 con la cella persa, senza porta; al giro dopo, con il
    /// gestionale in piedi e la cella 7 libera, la riprenota in S7. Prima la porta A9 restava scritta
    /// sulla missione verso S7, con la cella 9 tenuta: il giro dopo la trovava «tenuta», confermava il
    /// deposito e costruiva la rotta verso S7 con la porta di S9.
    /// </summary>
    [Fact]
    public async Task AWarehouseThatFailsOnTheProposedCellLeavesTheMissionWaitingForItsStorage()
    {
        using var log = new RecordingLogWriter();
        var mission = await AConfigurationMissionThatLostItsCellInS7();
        _plant.Device("R");
        _plant.Link("P", "R");
        _plant["P"].RejectItemDestinationAllocateLu = _plant["R"].ItemGuid;
        _warehouse.UnavailableOnCells.Add(OtherCellId);
        TheStorageIsFullAndThenTheWarehouseAnswers(() => TheWarehouseAnswers(OtherCellId, OtherDoorCode));

        await TheRouteLoopLooksForTheCell(mission);

        Assert.Equal([false, true], _warehouse.StorageRequests.Select(request => request.AssignPosition));
        Assert.False(_warehouse.StorageReservationsByMission.ContainsKey(mission.MissionId));
        Assert.Equal(_plant["S7"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.StorageCellLost);
        Assert.Null(mission.StorageDoorCode);
        Assert.Empty(mission.MissionSteps);
        Assert.Equal(MissionTypes.MovingToItem, mission.MissionType);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
        Assert.Equal(2, Navigator.RoutePathRetries.RegisterFailure(mission.MissionId));
        await log.Wait((line, level) =>
                level == LogEntryType.Warning &&
                line.Contains(mission.MissionId.ToString()) &&
                line.Contains("il gestionale non risponde alla ricerca negli altri magazzini"))
            .WaitAsync(TimeSpan.FromSeconds(30));

        _warehouse.UnavailableOnCells.Clear();
        await TheNavigatorReservesACellInS7Again(mission);
    }

    /// <summary>
    /// La ricerca negli altri magazzini di P propone A8 di S8, che da P si raggiunge, e la missione la
    /// scrive; ma poi la cella non e' sua (primo giro di correzioni): fra la sonda e la costruzione la
    /// rotta sparisce - qui il gateway della missione diventa X, da cui S8 non si raggiunge, appena la
    /// destinazione diventa S8 - e la rotta costruita e' vuota; oppure, nello stesso punto, un errore
    /// imprevisto interrompe la ricerca. La missione torna verso S7 con la cella persa, senza porta,
    /// indirizzo, partizione, profondita' ne' rotta, la cella 8 torna al gestionale, e la missione
    /// aspetta con il freno. Prima restava verso S8 con la porta A8 e la cella 8, senza rotta e senza
    /// la cella persa.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AProposalWrittenAndThenNotTakenSendsTheMissionBackToItsStorage(bool anError)
    {
        var mission = await AConfigurationMissionThatLostItsCellInS7();
        TheStorageIsFullAndThenTheWarehouseAnswers(() => TheWarehouseAnswers(NewCellId, NewDoorCode));
        var s8 = _plant["S8"].ItemGuid;
        var x = _plant["X"].ItemGuid;
        mission.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(PathMission.DestinationItemId) || mission.DestinationItemId != s8) return;
            if (anError) throw new InvalidOperationException("rotto per la prova");
            mission.Gateway = x;
        };

        await TheRouteLoopLooksForTheCell(mission);

        Assert.Equal([false, true], _warehouse.StorageRequests.Select(request => request.AssignPosition));
        if (!anError) Assert.Equal(x, mission.Gateway);
        Assert.False(_warehouse.StorageReservationsByMission.ContainsKey(mission.MissionId));
        Assert.Equal(_plant["S7"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.StorageCellLost);
        Assert.Null(mission.StorageDoorCode);
        Assert.Equal(string.Empty, mission.DestinationAsi);
        Assert.Equal(string.Empty, mission.DestinationSubitem);
        Assert.Equal(string.Empty, mission.DestinationPartition);
        Assert.Equal(0, mission.StorageDepth);
        Assert.Empty(mission.MissionSteps);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
    }

    // ---------------------------------------------------------------------------------------
    // La cella persa a monte di un ricollocatore di gruppo (risposta 5 del proprietario dell'8/10)
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// L'esempio della revisione finale (D4): la missione e' sull'ingresso I, la sua destinazione S7 e'
    /// del gruppo G (membri P1 con S7, P2 con S8), e S7 e' pieno. La ricerca altrove prova la cella
    /// collegata di ogni membro a turno, nell'ordine dei membri: P1 (il magazzino 1 del gestionale, pieno),
    /// poi P2 (il magazzino 2, con la cella 8), e la missione va in S8 con la rotta dall'ingresso alla
    /// porta di S8, attraverso P2. Se il gruppo ha una sua cella collegata, la 10 del magazzino 3 (pieno),
    /// si prova per prima. Prima la ricerca si ancorava al gruppo, la cui cella collegata e' di solito 0:
    /// rispondeva «nessuna cella automatica» senza chiedere al gestionale.
    /// </summary>
    [Theory]
    [InlineData(false, new[] { 1, 2 })]
    [InlineData(true, new[] { 3, 1, 2 })]
    public async Task UpstreamOfAGroupTheLostCellIsLookedForWithEachMemberInTurn(bool groupLinkedCell, int[] warehouses)
    {
        var mission = await AConfigurationMissionUpstreamOfTheGroupThatLostItsCellInS7(
            groupLinkedCell: groupLinkedCell,
            answer: warehouse => warehouse == 2 ? Found(NewCellId, NewDoorCode) : NoFreeCells());

        await TheRouteLoopLooksForTheCell(mission);

        Assert.Equal([false, .. warehouses.Select(_ => true)], _warehouse.StorageRequests.Select(request => request.AssignPosition));
        Assert.Equal(warehouses.Select(id => (decimal)id), _warehouse.StorageRequests.Skip(1).Select(request => request.WarehouseId));
        Assert.All(_warehouse.StorageRequests.Skip(1), request => Assert.Equal([_plant["G"].ItemGuid], request.ReachableDeviceIds));
        Assert.Equal(NewCellId, _warehouse.StorageReservationsByMission[mission.MissionId].CellId);
        Assert.Equal(_plant["S8"].ItemGuid, mission.DestinationItemId);
        Assert.Equal(NewDoorCode, mission.StorageDoorCode);
        Assert.Equal("0011", mission.DestinationAsi);
        Assert.False(mission.StorageCellLost);
        Assert.True(mission.StorageCellReserved);
        Assert.False(mission.OperatorDestination);
        Assert.Equal(["I", "P2", "S8"], RouteOf(mission));
        Assert.Null(RoutesToBuild.Instance[mission.MissionId]);
    }

    /// <summary>
    /// La stessa scena, e nessun membro del gruppo trova posto: P1 e P2 rispondono «nessuna cella», e
    /// solo allora la missione va allo scarto R dell'ingresso, con la causa della mancanza di celle.
    /// Il gestionale non le tiene niente.
    /// </summary>
    [Fact]
    public async Task UpstreamOfAGroupTheRejectionComesOnlyWhenNoMemberHasRoom()
    {
        var mission = await AConfigurationMissionUpstreamOfTheGroupThatLostItsCellInS7(answer: _ => NoFreeCells());
        TheEntranceHasTheRejectionPointR();

        await TheRouteLoopLooksForTheCell(mission);

        Assert.Equal([false, true, true], _warehouse.StorageRequests.Select(request => request.AssignPosition));
        Assert.Equal([1m, 2m], _warehouse.StorageRequests.Skip(1).Select(request => request.WarehouseId));
        Assert.False(_warehouse.StorageReservationsByMission.ContainsKey(mission.MissionId));
        Assert.Equal(_plant["R"].ItemGuid, mission.DestinationItemId);
        Assert.Equal(MissionTypes.Rejection, mission.MissionType);
        Assert.True(string.IsNullOrEmpty(mission.StorageDoorCode));
        Assert.Equal(["I", "R"], RouteOf(mission));
        Assert.Equal("No storage cell available", mission.StatusDescription);
        Assert.Null(RoutesToBuild.Instance[mission.MissionId]);
    }

    /// <summary>
    /// La stessa scena: P1 non ha posto, e il gestionale da' a P2 la cella 9 di S9, un magazzino del
    /// gruppo che dall'ingresso non si raggiunge. Il tentativo di P2 ha prenotato e poi scartato: la
    /// cella 9 torna al gestionale, nessuna cella resta appesa alla missione, e la missione va allo
    /// scarto con la causa della porta che non si raggiunge.
    /// </summary>
    [Fact]
    public async Task UpstreamOfAGroupAMembersCellThatIsNotTakenIsGivenBack()
    {
        var mission = await AConfigurationMissionUpstreamOfTheGroupThatLostItsCellInS7(
            answer: warehouse => warehouse == 2 ? Found(OtherCellId, OtherDoorCode) : NoFreeCells());
        TheEntranceHasTheRejectionPointR();

        await TheRouteLoopLooksForTheCell(mission);

        Assert.Equal([false, true, true], _warehouse.StorageRequests.Select(request => request.AssignPosition));
        Assert.Contains(mission.MissionId, _warehouse.ReleasedStorageReservations);
        Assert.False(_warehouse.StorageReservationsByMission.ContainsKey(mission.MissionId));
        Assert.Equal(_plant["R"].ItemGuid, mission.DestinationItemId);
        Assert.Equal(MissionTypes.Rejection, mission.MissionType);
        Assert.True(string.IsNullOrEmpty(mission.StorageDoorCode));
        Assert.Equal(["I", "R"], RouteOf(mission));
        Assert.Equal("No item linked to storage door cell", mission.StatusDescription);
    }

    /// <summary>
    /// La stessa scena, con il primo turno che prenota e scarta: il gestionale da' a P1 la cella 9 di S9,
    /// che dall'ingresso non si raggiunge, e a P2 la cella 8 di S8. La cella 9 torna al gestionale, la
    /// missione torna com'era prima del turno di P2, e P2 vince: la missione va in S8, con la cella 8
    /// prenotata per lei e la rotta dall'ingresso attraverso P2. Le altre scene avevano davanti al
    /// vincitore solo membri senza posto, e il ripristino fra un turno scartato e il successivo non lo
    /// guardava nessuno.
    /// </summary>
    [Fact]
    public async Task UpstreamOfAGroupAMemberAfterACellThatWasNotTakenStillWins()
    {
        var mission = await AConfigurationMissionUpstreamOfTheGroupThatLostItsCellInS7(
            answer: warehouse => warehouse == 1 ? Found(OtherCellId, OtherDoorCode) : Found(NewCellId, NewDoorCode));
        TheEntranceHasTheRejectionPointR();

        await TheRouteLoopLooksForTheCell(mission);

        Assert.Equal([false, true, true], _warehouse.StorageRequests.Select(request => request.AssignPosition));
        Assert.Equal([1m, 2m], _warehouse.StorageRequests.Skip(1).Select(request => request.WarehouseId));
        Assert.Contains(mission.MissionId, _warehouse.ReleasedStorageReservations);
        Assert.Equal(NewCellId, _warehouse.StorageReservationsByMission[mission.MissionId].CellId);
        Assert.Equal(_plant["S8"].ItemGuid, mission.DestinationItemId);
        Assert.Equal(NewDoorCode, mission.StorageDoorCode);
        Assert.Equal("0011", mission.DestinationAsi);
        Assert.False(mission.StorageCellLost);
        Assert.True(mission.StorageCellReserved);
        Assert.NotEqual(MissionTypes.Rejection, mission.MissionType);
        Assert.Equal(["I", "P2", "S8"], RouteOf(mission));
        Assert.Null(RoutesToBuild.Instance[mission.MissionId]);
    }

    /// <summary>
    /// La missione e' gia' sul membro P1: la ricerca altrove resta quella di P1 per se', e P2 non si
    /// prova (risposta 5 dell'8/10: con la missione su un membro si usa il membro). Il magazzino 2
    /// avrebbe la cella 8, ma il gestionale e' interrogato solo con la cella collegata di P1, e la
    /// missione va allo scarto R di P1.
    /// </summary>
    [Fact]
    public async Task OnAMemberOfTheGroupOnlyThatMemberLooksElsewhere()
    {
        var mission = await AConfigurationMissionUpstreamOfTheGroupThatLostItsCellInS7(on: "P1",
            answer: warehouse => warehouse == 2 ? Found(NewCellId, NewDoorCode) : NoFreeCells());
        _plant.Device("R");
        _plant.Link("P1", "R");
        _plant["P1"].RejectItemDestinationAllocateLu = _plant["R"].ItemGuid;

        await TheRouteLoopLooksForTheCell(mission);

        Assert.Equal([false, true], _warehouse.StorageRequests.Select(request => request.AssignPosition));
        Assert.Equal([1m], _warehouse.StorageRequests.Skip(1).Select(request => request.WarehouseId));
        Assert.False(_warehouse.StorageReservationsByMission.ContainsKey(mission.MissionId));
        Assert.Equal(_plant["R"].ItemGuid, mission.DestinationItemId);
        Assert.Equal(["P1", "R"], RouteOf(mission));
    }

    /// <summary>
    /// La stessa scena per la missione dell'operatore, con il magazzino scelto da lui, S8, pieno, un
    /// punto di scarto su P e un altro magazzino di P, S9, che una cella l'avrebbe: la missione non va
    /// ne' altrove ne' allo scarto. Resta sua verso S8, senza porta e senza rotta, e aspetta con il
    /// freno; il gestionale e' interrogato solo per S8.
    /// </summary>
    [Fact]
    public async Task AnOperatorsMissionWhoseStorageIsFullGoesNeitherElsewhereNorToTheRejectionPoint()
    {
        var mission = await AMissionTheOperatorSentToS8();
        _plant.Device("R");
        _plant.Link("P", "R");
        _plant["P"].RejectItemDestinationAllocateLu = _plant["R"].ItemGuid;
        _warehouse.RevokeStorageReservation(mission.MissionId);
        RoutesToBuild.Instance.Remove(mission.MissionId);
        TheStorageIsFullAndThenTheWarehouseAnswers(() => TheWarehouseAnswers(OtherCellId, OtherDoorCode));

        await TheRouteLoopLooksForTheCell(mission);

        Assert.Equal([false], _warehouse.StorageRequests.Select(request => request.AssignPosition));
        Assert.Equal(_plant["S8"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.OperatorDestination);
        Assert.True(mission.StorageCellLost);
        Assert.Null(mission.StorageDoorCode);
        Assert.Empty(mission.MissionSteps);
        Assert.Equal(MissionTypes.MovingToItem, mission.MissionType);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);

        // Nemmeno la meta' della via della configurazione: niente causa dello scarto, e nessuna cella
        // restituita (revisione finale della voce 39: senza la guardia sul segno la prova restava
        // verde, perche' la ricerca altrove risponde «scelta dall'operatore» senza chiedere).
        Assert.NotEqual(PathMission.NoStorageCellAvailable, mission.StatusDescription);
        Assert.DoesNotContain(nameof(IWarehouse.ReleaseStorageReservationAsync), _warehouse.Calls);
    }

    /// <summary>
    /// Risposta 5 del proprietario del 2/10: il gestionale che non risponde lascia la missione sotto il
    /// freno, e non la manda allo scarto. Qui non risponde alla prima domanda, quella nel magazzino
    /// della destinazione, per la missione della configurazione che ha perso la cella in S7, con il
    /// punto di scarto R su P; poi torna, e non ha celle libere (un fermo breve). La missione non cerca
    /// altrove: resta verso S7 con la cella persa, non e' un rigetto, il suo comando non e' chiuso, ed
    /// e' di nuovo in coda con un fallimento contato. Fino alla revisione finale della voce 39 nessuna
    /// prova arrivava a questa domanda con il gestionale fermo (quella accanto lo fa cadere alla
    /// seconda ricerca), e trattato come «nessuna cella» il fermo mandava l'UDC in R.
    /// </summary>
    [Fact]
    public async Task AWarehouseThatDoesNotAnswerTheFirstQuestionLeavesTheMissionUnderTheBrake()
    {
        var mission = await AConfigurationMissionThatLostItsCellInS7();
        _plant.Device("R");
        _plant.Link("P", "R");
        _plant["P"].RejectItemDestinationAllocateLu = _plant["R"].ItemGuid;
        var command = new Command { Status = Command.StatusTypes.Running };
        await ORM.CommandToDo.Update(command);
        mission.IdCommandToDo = command.IdCommandToDo;
        TheStorageIsFullAndThenTheWarehouseAnswers();
        var reservations = 0;
        _warehouse.Before = operation =>
        {
            if (operation == nameof(IWarehouse.ReserveStorageAsync) && ++reservations == 1)
                throw new WarehouseUnavailableException(operation);
            return Task.CompletedTask;
        };

        await TheRouteLoopLooksForTheCell(mission);

        Assert.Equal(1, reservations);
        Assert.Equal(_plant["S7"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.StorageCellLost);
        Assert.NotEqual(MissionTypes.Rejection, mission.MissionType);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
        Assert.Equal(2, Navigator.RoutePathRetries.RegisterFailure(mission.MissionId));
        await using var context = _database.CreateContext();
        Assert.Equal((short)Command.StatusTypes.Running, Assert.Single(context.Commands).Status);
    }

    /// <summary>
    /// Senza un punto di scarto la missione aspetta con una riga di avviso (risposta 5 del 2/10), non
    /// una per giro del freno: tre giri senza cella scrivono l'avviso una volta sola, al primo
    /// fallimento; gli altri giri lo dicono come informazione. Fino alla revisione finale della voce 39
    /// ogni giro scriveva un avviso, circa 720 l'ora per missione con il freno al tetto.
    /// </summary>
    /// <remarks>
    /// <c>Navigator.WriteLog</c> manda la riga al registro da dentro un <c>Task.Run</c>: la prova
    /// leggeva <c>log.Lines</c> subito dopo il terzo giro, e sotto carico l'avviso del primo non era
    /// ancora arrivato (rossa nella suite intera dell'8/10, con le sole due righe della preparazione
    /// nel registro). Il livello lo dice il registro, e lo si aspetta; il conto lo fa la console, che
    /// e' sincrona e con la diagnostica spenta riceve solo avvisi ed errori (<see cref="RecordingConsole"/>).
    /// </remarks>
    [Fact]
    public async Task WithoutARejectionPointTheWarningIsWrittenOnceNotOnEveryTurn()
    {
        var previousDebugLog = Navigator.ActivateDebugLog;
        Navigator.ActivateDebugLog = false;
        try
        {
            using var log = new RecordingLogWriter();
            var mission = await AConfigurationMissionThatLostItsCellInS7();
            using var console = new RecordingConsole();

            for (var turn = 1; turn <= 3; turn++)
            {
                TheStorageIsFullAndThenTheWarehouseAnswers();
                RoutesToBuild.Instance.Remove(mission.MissionId);
                await TheRouteLoopLooksForTheCell(mission);
                Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
            }

            Assert.Equal(4, Navigator.RoutePathRetries.RegisterFailure(mission.MissionId));
            Assert.Single(console.Lines, line => line.Contains(mission.MissionId.ToString()) &&
                                                 line.Contains("nessun punto di scarto configurato"));
            await log.Wait((line, level) =>
                    level == LogEntryType.Warning &&
                    line.Contains(mission.MissionId.ToString()) &&
                    line.Contains("nessun punto di scarto configurato") &&
                    line.Contains("(fallimento 1)"))
                .WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            Navigator.ActivateDebugLog = previousDebugLog;
        }
    }

    /// <summary>
    /// Il punto di scarto R di P c'e', ma da P non si raggiunge (nessuna giunzione): e' come nessun
    /// punto di scarto. La missione aspetta con il freno verso S7 con la cella persa, il comando resta
    /// aperto e la riga lo dice. Fino alla revisione finale della voce 39 <c>RejectTo</c> chiudeva il
    /// comando come fatto e riscriveva la destinazione verso R prima di accorgersi che la rotta non
    /// c'era, e la missione restava ferma verso R, senza rotta e fuori dalla coda.
    /// </summary>
    [Fact]
    public async Task ARejectionPointThatCannotBeReachedIsLikeNone()
    {
        using var log = new RecordingLogWriter();
        var mission = await AConfigurationMissionThatLostItsCellInS7();
        _plant.Device("R");
        _plant["P"].RejectItemDestinationAllocateLu = _plant["R"].ItemGuid;
        var command = new Command { Status = Command.StatusTypes.Running };
        await ORM.CommandToDo.Update(command);
        mission.IdCommandToDo = command.IdCommandToDo;
        TheStorageIsFullAndThenTheWarehouseAnswers();

        await TheRouteLoopLooksForTheCell(mission);

        Assert.Equal([false, true], _warehouse.StorageRequests.Select(request => request.AssignPosition));
        Assert.Equal(_plant["S7"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.StorageCellLost);
        Assert.NotEqual(MissionTypes.Rejection, mission.MissionType);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
        await using var context = _database.CreateContext();
        Assert.Equal((short)Command.StatusTypes.Running, Assert.Single(context.Commands).Status);
        await log.Wait((line, level) =>
                level == LogEntryType.Warning &&
                line.Contains(mission.MissionId.ToString()) &&
                line.Contains("non c'e' un percorso verso il punto di scarto"))
            .WaitAsync(TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// La ricerca negli altri magazzini di P risponde una cella proprio in S7, la 7 della porta A7: la
    /// destinazione resta S7, e il setter della destinazione non abbassa la cella persa. La missione
    /// ha la cella, e la cella persa cade lo stesso: il giro dopo non la cercherebbe di nuovo. La cella
    /// appena prenotata e' confermata per il deposito da P, dove la missione e': il deposito non la chiede
    /// di nuovo (nessuna prova guardava la conferma su questa via, revisione finale del primo giro della
    /// voce 39, C21).
    /// </summary>
    [Fact]
    public async Task ACellFoundElsewhereInTheSameStorageIsNoLongerLost()
    {
        var mission = await AConfigurationMissionThatLostItsCellInS7();
        _warehouse.CellPartitionMaps[OldCellId] = new CellPartitionMap(OldCellId, "0001", "0002",
            AsiDoorB: string.Empty, SubitemDoorB: string.Empty, [new CellPartition("0003", -1)]);
        TheStorageIsFullAndThenTheWarehouseAnswers(() => TheWarehouseAnswers(OldCellId, OldDoorCode));

        await TheRouteLoopLooksForTheCell(mission);

        Assert.Equal([false, true], _warehouse.StorageRequests.Select(request => request.AssignPosition));
        Assert.Equal(OldCellId, _warehouse.StorageReservationsByMission[mission.MissionId].CellId);
        Assert.Equal(_plant["S7"].ItemGuid, mission.DestinationItemId);
        Assert.Equal(OldDoorCode, mission.StorageDoorCode);
        Assert.False(mission.StorageCellLost);
        Assert.Equal(["P", "S7"], RouteOf(mission));
        Assert.Null(RoutesToBuild.Instance[mission.MissionId]);
        Assert.Equal(_plant["P"].ItemGuid, mission.TakeStorageCellConfirmation());
    }

    /// <summary>
    /// La missione esce dal motore mentre il gestionale risponde alla ricerca negli altri magazzini di
    /// P (la tolgono dalla pagina: la rimozione non e' rifiutata per una ricerca in corso). Che la
    /// ricerca trovi la cella 8 di S8 o niente, con il punto di scarto R, la missione non si tocca
    /// piu': nessuna scrittura (ne rifarebbe la riga sul database del WCS), niente scarto, il comando
    /// resta com'e', e la cella presa torna al gestionale. Fino alla revisione finale della voce 39 la
    /// ricerca le scriveva porta e destinazione, la cella 8 restava prenotata per una missione che non
    /// c'era piu', e senza cella la causa e lo scarto la riscrivevano.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AMissionRemovedWhileTheSearchElsewhereAnswersIsNotWrittenAgain(bool aCellElsewhere)
    {
        var mission = await AConfigurationMissionThatLostItsCellInS7();
        _plant.Device("R");
        _plant.Link("P", "R");
        _plant["P"].RejectItemDestinationAllocateLu = _plant["R"].ItemGuid;
        var command = new Command { Status = Command.StatusTypes.Running };
        await ORM.CommandToDo.Update(command);
        mission.IdCommandToDo = command.IdCommandToDo;
        TheStorageIsFullAndThenTheWarehouseAnswers();
        var written = TheWritesOf(mission);
        var removed = false;
        var calls = 0;
        _warehouse.Before = async operation =>
        {
            if (operation != nameof(IWarehouse.ReserveStorageAsync) || ++calls != 2) return;

            if (aCellElsewhere) TheWarehouseAnswers(NewCellId, NewDoorCode);
            Assert.True(await ActiveMissions.Instance.Remove(mission, releaseStorageReservation: false));
            while (written.TryDequeue(out _)) { }
            removed = true;
        };

        await TheRouteLoopLooksForTheCell(mission);

        Assert.True(removed);
        Assert.Empty(written);
        Assert.False(_warehouse.StorageReservationsByMission.ContainsKey(mission.MissionId));
        Assert.Null(ActiveMissions.Instance[mission.MissionId]);
        Assert.NotEqual(MissionTypes.Rejection, mission.MissionType);
        await using var context = _database.CreateContext();
        Assert.Equal((short)Command.StatusTypes.Running, Assert.Single(context.Commands).Status);
    }

    /// <summary>
    /// Il gestionale non risponde se la cella 8 e' ancora della missione della configurazione che va in
    /// S8, mentre la missione e' ancora sull'ingresso I, a monte di P: il prossimo passo non e' il
    /// deposito, e lontano dal deposito non c'e' niente da aspettare. Il giro prosegue con la
    /// configurazione di I, come prima del controllo, e la rotta I->P->S8 resta; nessun fallimento
    /// contato, nessuna conferma scritta (il deposito la chiedera'). Fino alla revisione finale della
    /// voce 39 la missione aspettava sotto il freno ovunque fosse, e con il gestionale fermo il giro non
    /// rifaceva la rotta a nessuna missione con il segno della cella.
    /// </summary>
    [Fact]
    public async Task AwayFromTheDepositAWarehouseThatDoesNotAnswerHoldsNothingBack()
    {
        var (mission, _) = await AConfigurationMissionOnPWithCell8ReservedByTheNavigator();
        _plant.Device("I");
        _plant.Link("I", "P");
        Assert.Equal(MissionMoveOutcome.Moved, MissionGateway.Move(mission.MissionId, _plant["I"].ItemGuid));
        await mission.PositionWork;
        Assert.Equal(["I", "P", "S8"], RouteOf(mission));
        Assert.True(mission.StorageCellReserved);
        _warehouse.UnavailableOperations.Add(nameof(IWarehouse.GetStorageReservationAsync));
        RoutesToBuild.Instance.Remove(mission.MissionId);
        _warehouse.Calls.Clear();

        await TheRouteLoopLooksForTheCell(mission);

        Assert.Contains(nameof(IWarehouse.GetStorageReservationAsync), _warehouse.Calls);
        Assert.Null(RoutesToBuild.Instance[mission.MissionId]);
        Assert.Equal(["I", "P", "S8"], RouteOf(mission));
        Assert.Equal(NewDoorCode, mission.StorageDoorCode);
        Assert.Null(mission.TakeStorageCellConfirmation());
        Assert.Equal(1, Navigator.RoutePathRetries.RegisterFailure(mission.MissionId));
    }

    // ---------------------------------------------------------------------------------------
    // Il deposito di una missione dell'operatore aspetta la conferma della cella
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// La missione dell'operatore va in S8 con la cella 8, che il gestionale le tiene, e P - il
    /// ricollocatore - sta per depositarla. Il deposito non parte da solo: la missione va al
    /// navigatore, che chiede al gestionale (la domanda rinnova la prenotazione) e conferma la
    /// cella; allora il deposito parte, verso la porta A8, senza altre prenotazioni. Prima partiva
    /// senza chiedere niente a nessuno.
    /// </summary>
    [Fact]
    public async Task TheDepositOfAnOperatorsMissionWaitsForTheNavigatorToConfirmTheCell()
    {
        var mission = await AnOperatorsMissionOnPReadyToDepositInS8();
        var step = Assert.Single(mission.MissionSteps);

        await _plant["P"].StorageManager!.StartStoreLu();

        Assert.Equal(Guid.Empty, _plant["P"].RunningStepId);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
        Assert.DoesNotContain(nameof(IWarehouse.GetStorageReservationAsync), _warehouse.Calls);

        await TheRouteLoopLooksForTheCell(mission);

        Assert.Equal(1, _warehouse.Calls.Count(call => call == nameof(IWarehouse.GetStorageReservationAsync)));
        Assert.DoesNotContain(nameof(IWarehouse.ReserveStorageAsync), _warehouse.Calls);
        Assert.Same(step, Assert.Single(mission.MissionSteps));

        await _plant["P"].StorageManager!.StartStoreLu();

        Assert.Equal(step.StepId, _plant["P"].RunningStepId);
        Assert.Equal(NewDoorCode, mission.StorageDoorCode);
        Assert.Equal(NewCellId, _warehouse.StorageReservationsByMission[mission.MissionId].CellId);
    }

    /// <summary>
    /// La missione dell'operatore va in S8 con la cella 8 e la rotta P->S8, e mentre viaggiava il
    /// gestionale non gliel'ha piu' tenuta (scaduta, o revocata). Dal giro delle rotte non e' piu'
    /// passata - ha la sua rotta, e dopo un riavvio la rotta la costruisce la ricarica - e P sta per
    /// depositarla. Il deposito non parte: il navigatore chiede, lascia la porta A8 e le prenota
    /// un'altra cella di S8, la 10 della porta B8, con la rotta verso di lei; e il deposito parte
    /// verso B8. Prima partiva verso la cella 8, che il gestionale poteva aver dato a un'altra UDC.
    /// </summary>
    [Fact]
    public async Task ACellLostWhileTheMissionTravelsIsReplacedBeforeTheDeposit()
    {
        var mission = await AnOperatorsMissionOnPReadyToDepositInS8();
        var oldStep = Assert.Single(mission.MissionSteps);
        await S8HasASecondDoorB8OnCell10();
        _warehouse.RevokeStorageReservation(mission.MissionId);
        TheWarehouseAnswers(10, "B8");

        await _plant["P"].StorageManager!.StartStoreLu();

        Assert.Equal(Guid.Empty, _plant["P"].RunningStepId);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);

        await TheRouteLoopLooksForTheCell(mission);

        Assert.Equal(10, _warehouse.StorageReservationsByMission[mission.MissionId].CellId);
        Assert.Equal("B8", mission.StorageDoorCode);
        Assert.Equal("0031", mission.DestinationAsi);
        Assert.True(mission.OperatorDestination);
        Assert.DoesNotContain(oldStep, MissionSteps.Instance.List);
        var step = Assert.Single(mission.MissionSteps);

        await _plant["P"].StorageManager!.StartStoreLu();

        Assert.Equal(step.StepId, _plant["P"].RunningStepId);
        Assert.Equal("B8", mission.StorageDoorCode);
    }

    /// <summary>
    /// Una conferma vale per un deposito solo: il deposito parte con la conferma del navigatore, poi
    /// non va a buon fine (il campo lo rifiuta, e P torna libero con la missione a bordo e la stessa
    /// rotta). Il deposito dopo, che puo' venire molto piu' tardi, non parte con la conferma di prima:
    /// la missione torna al navigatore.
    /// </summary>
    [Fact]
    public async Task AConfirmationIsGoodForOneDepositOnly()
    {
        var mission = await AnOperatorsMissionOnPReadyToDepositInS8();
        var step = Assert.Single(mission.MissionSteps);
        await TheRouteLoopLooksForTheCell(mission);
        await _plant["P"].StorageManager!.StartStoreLu();
        Assert.Equal(step.StepId, _plant["P"].RunningStepId);

        _plant["P"].RunningStepId = Guid.Empty;
        _plant["P"].Status = DeviceStatusTypes.Ready;
        RoutesToBuild.Instance.Items = new();

        await _plant["P"].StorageManager!.StartStoreLu();

        Assert.Equal(Guid.Empty, _plant["P"].RunningStepId);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
    }

    /// <summary>
    /// Una conferma vale per la cella che il navigatore ha guardato: confermata la cella 8 su P,
    /// l'operatore manda la missione in S9 (la cella 9, porta A9). Il deposito verso A9 non parte con
    /// la conferma della 8, e la missione va al navigatore. Prima la conferma restava finche' un giro
    /// di deposito non la prendeva, anche dopo che la cella era cambiata.
    /// </summary>
    [Fact]
    public async Task AConfirmationIsNotGoodForAnotherCell()
    {
        var mission = await AnOperatorsMissionOnPReadyToDepositInS8();
        await TheRouteLoopLooksForTheCell(mission);
        TheWarehouseAnswers(OtherCellId, OtherDoorCode);
        Assert.Equal(MissionDestinationOutcome.Changed,
            (await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S9"].ItemGuid)).Outcome);
        Assert.Equal(OtherDoorCode, mission.StorageDoorCode);
        RoutesToBuild.Instance.Items = new();

        await _plant["P"].StorageManager!.StartStoreLu();

        Assert.Equal(Guid.Empty, _plant["P"].RunningStepId);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
    }

    /// <summary>
    /// Una conferma vale per la posizione in cui la missione era: confermata la cella su P, la
    /// missione e' spostata su Q, da cui la rotta Q->S8 la deposita; il deposito da Q non parte con
    /// la conferma di P, e la missione va al navigatore.
    /// </summary>
    [Fact]
    public async Task AConfirmationOnAnotherPositionDoesNotCount()
    {
        var mission = await AnOperatorsMissionOnPReadyToDepositInS8();
        await TheRouteLoopLooksForTheCell(mission);
        Assert.Equal(MissionMoveOutcome.Moved, MissionGateway.Move(mission.MissionId, _plant["Q"].ItemGuid));
        await mission.PositionWork;
        RoutesToBuild.Instance.Items = new();
        _plant["Q"].RecordStorageLength = RecordStorageTypes.FromPlc;
        _plant.PlannedStep(mission, "Q", "S8");
        Assert.True(mission.OperatorDestination);

        await _plant["Q"].StorageManager!.StartStoreLu();

        Assert.Equal(Guid.Empty, _plant["Q"].RunningStepId);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
    }

    /// <summary>
    /// La conferma cade con l'errore del dispositivo (risposta 6 del proprietario dell'8/10). Il
    /// navigatore conferma la cella 8 della missione dell'operatore su P; poi P va in errore e aspetta
    /// (l'errore 35, <c>StepError</c>) sul deposito, e l'operatore lo rimette in marcia. Il deposito
    /// non parte con la conferma di prima: la missione torna al navigatore, che richiede al gestionale.
    /// Senza l'errore lo stesso deposito parte con la conferma, verso la porta A8: la conferma c'era.
    /// Prima la conferma sopravviveva all'errore, e il deposito partiva alla ripartenza, anche ore
    /// dopo, senza chiedere se il gestionale teneva ancora la cella.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ADeviceThatGoesInErrorForgetsTheConfirmationOfTheCell(bool error)
    {
        var mission = await AnOperatorsMissionOnPReadyToDepositInS8();
        var step = Assert.Single(mission.MissionSteps);
        var relocator = _plant["P"];
        await TheRouteLoopLooksForTheCell(mission);
        Assert.Equal([nameof(IWarehouse.GetStorageReservationAsync)], _warehouse.Calls);
        Assert.Same(step, Assert.Single(mission.MissionSteps));

        if (error)
        {
            relocator.RunningStepId = step.StepId;
            await relocator.ConnectionAsiMovementIsCompleted(new AsiMovementIsCompletedEventArgs(
                    relocator.ItemGuid, step.StepId, ResultCodeTypes.Error, 35, "errore del campo", null))
                .WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(DeviceStatusTypes.Warning, relocator.Status);
            Assert.Equal(ResultCodeTypes.Error, step.StepStatus);
            // La conferma e' gia' caduta con l'errore: la ripartenza qui sotto passa dal setter dello
            // stato, che la butterebbe comunque (AutomationDeviceStateMachine.LeaveTheError).
            Assert.Null(mission.TakeStorageCellConfirmation());

            // L'operatore lo rimette in marcia.
            relocator.RunningStepId = Guid.Empty;
            relocator.Status = DeviceStatusTypes.Ready;
        }

        RoutesToBuild.Instance.Items = new();
        _warehouse.Calls.Clear();

        await relocator.StorageManager!.StartStoreLu();

        if (!error)
        {
            Assert.Equal(step.StepId, relocator.RunningStepId);
            Assert.Null(RoutesToBuild.Instance[mission.MissionId]);
            return;
        }

        Assert.Equal(Guid.Empty, relocator.RunningStepId);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);

        RoutesToBuild.Instance.Items = new();
        await TheRouteLoopLooksForTheCell(mission);

        Assert.Equal([nameof(IWarehouse.GetStorageReservationAsync)], _warehouse.Calls);
        Assert.Equal(NewDoorCode, mission.StorageDoorCode);
    }

    /// <summary>
    /// La conferma che arriva mentre il dispositivo e' in errore non sopravvive alla ripartenza
    /// (risposta 6 del proprietario dell'8/10, primo giro di correzioni del compito 12). P va in errore
    /// e aspetta (l'errore 35, <c>StepError</c>) quando la missione dell'operatore non ha ancora una
    /// conferma, e l'errore non ha niente da buttare; intanto il navigatore, che non guarda lo stato di
    /// chi deposita, conferma la cella 8. L'operatore rimette in marcia P: il deposito non parte con
    /// quella conferma, e la missione torna al navigatore, che richiede al gestionale. Prima la conferma
    /// presa durante l'errore restava fino alla ripartenza, anche ore dopo.
    /// </summary>
    [Fact]
    public async Task AConfirmationGivenWhileTheDeviceIsInErrorDoesNotOutliveTheRestart()
    {
        var mission = await AnOperatorsMissionOnPReadyToDepositInS8();
        var step = Assert.Single(mission.MissionSteps);
        var relocator = _plant["P"];

        relocator.RunningStepId = step.StepId;
        await relocator.ConnectionAsiMovementIsCompleted(new AsiMovementIsCompletedEventArgs(
                relocator.ItemGuid, step.StepId, ResultCodeTypes.Error, 35, "errore del campo", null))
            .WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(DeviceStatusTypes.Warning, relocator.Status);
        Assert.Equal(ResultCodeTypes.Error, step.StepStatus);

        // Il navigatore conferma la cella mentre P aspetta l'operatore.
        RoutesToBuild.Instance.Items = new();
        _warehouse.Calls.Clear();
        await TheRouteLoopLooksForTheCell(mission);
        Assert.Equal([nameof(IWarehouse.GetStorageReservationAsync)], _warehouse.Calls);
        Assert.Same(step, Assert.Single(mission.MissionSteps));
        Assert.Equal(DeviceStatusTypes.Warning, relocator.Status);

        // L'operatore lo rimette in marcia.
        relocator.RunningStepId = Guid.Empty;
        relocator.Status = DeviceStatusTypes.Ready;
        RoutesToBuild.Instance.Items = new();
        _warehouse.Calls.Clear();

        await relocator.StorageManager!.StartStoreLu();

        Assert.Equal(Guid.Empty, relocator.RunningStepId);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);

        RoutesToBuild.Instance.Items = new();
        await TheRouteLoopLooksForTheCell(mission);

        Assert.Equal([nameof(IWarehouse.GetStorageReservationAsync)], _warehouse.Calls);
        Assert.Equal(NewDoorCode, mission.StorageDoorCode);
    }

    // ---------------------------------------------------------------------------------------
    // Il deposito di una missione della configurazione con la cella prenotata per lei (voce 39,
    // compito 3: risposta 4 del proprietario del 2/10)
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Una missione della configurazione con la cella che il giro delle rotte le ha prenotato (la 8
    /// di S8) sta per essere depositata da P, che la tiene ancora presso il gestionale. Il deposito
    /// non parte da solo: la missione va al navigatore, che chiede una volta al gestionale (la domanda
    /// rinnova la prenotazione), non prenota niente e non tocca la rotta; allora il deposito parte,
    /// verso A8. La configurazione di P non si rifa' alla conferma, qualunque sia la sua modalita' di
    /// deposito: con le celle collegate cercherebbe di nuovo la cella e rifarebbe la rotta. Prima il
    /// deposito partiva senza chiedere niente a nessuno.
    /// </summary>
    [Theory]
    [InlineData(LookWhereAllocateLuTypes.No)]
    [InlineData(LookWhereAllocateLuTypes.BuildRouteToStore)]
    public async Task TheDepositOfAConfigurationMissionAsksTheNavigatorForTheCellReservedForIt(LookWhereAllocateLuTypes mode)
    {
        var (mission, step) = await AConfigurationMissionOnPWithCell8ReservedByTheNavigator();
        _plant["P"].LookWhereAllocateLu = mode;

        await _plant["P"].StorageManager!.StartStoreLu();

        Assert.Equal(Guid.Empty, _plant["P"].RunningStepId);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
        Assert.Empty(_warehouse.Calls);

        RoutesToBuild.Instance.Remove(mission.MissionId);   // come la pesca il giro delle rotte
        await TheRouteLoopLooksForTheCell(mission);

        Assert.Equal([nameof(IWarehouse.GetStorageReservationAsync)], _warehouse.Calls);
        Assert.Same(step, Assert.Single(mission.MissionSteps));
        Assert.Null(RoutesToBuild.Instance[mission.MissionId]);

        await _plant["P"].StorageManager!.StartStoreLu();

        Assert.Equal(step.StepId, _plant["P"].RunningStepId);
        Assert.Equal(NewDoorCode, mission.StorageDoorCode);
        Assert.Equal(NewCellId, _warehouse.StorageReservationsByMission[mission.MissionId].CellId);
        Assert.Equal(1, _warehouse.Calls.Count(call => call == nameof(IWarehouse.GetStorageReservationAsync)));
        Assert.DoesNotContain(nameof(IWarehouse.ReserveStorageAsync), _warehouse.Calls);
    }

    /// <summary>
    /// La cella che il giro delle rotte prenota a monte, dall'ingresso I con la modalita' di deposito
    /// (una cella qualunque, perche' I non e' collegato a un magazzino), e' una cella della missione
    /// anche quando la missione arriva al ricollocatore per un'altra via: l'operatore la sposta su P,
    /// e lo spostamento rifa' la rotta fino alla porta A8 senza chiedere al gestionale. Il deposito da
    /// P non parte da solo: il navigatore chiede, conferma, e il deposito parte verso A8.
    /// </summary>
    [Fact]
    public async Task ACellTheNavigatorReservedUpstreamIsAskedForWhenTheMissionIsDepositedAfterAMove()
    {
        var mission = await AMissionOnPInTheStorageScene(on: "I", boundForX: false);
        _plant["I"].LookWhereAllocateLu = LookWhereAllocateLuTypes.BuildRouteToStore;
        _plant["I"].LinkedCellId = OldCellId;
        Assert.False(_plant["I"].IsLinkedToStorage);
        TheWarehouseAnswers(NewCellId, NewDoorCode);

        await TheRouteLoopLooksForTheCell(mission);

        Assert.Equal(NewCellId, _warehouse.StorageReservationsByMission[mission.MissionId].CellId);
        Assert.Equal(NewDoorCode, mission.StorageDoorCode);
        Assert.True(mission.StorageCellReserved);
        Assert.Equal(["I", "P", "S8"], RouteOf(mission));

        Assert.Equal(MissionMoveOutcome.Moved, MissionGateway.Move(mission.MissionId, _plant["P"].ItemGuid));
        await mission.PositionWork;
        Assert.Equal(["P", "S8"], RouteOf(mission));
        var step = Assert.Single(mission.MissionSteps);
        Assert.True(mission.StorageCellReserved);
        _plant["P"].RecordStorageLength = RecordStorageTypes.FromPlc;
        RoutesToBuild.Instance.Items = new();
        _warehouse.Calls.Clear();

        await _plant["P"].StorageManager!.StartStoreLu();

        Assert.Equal(Guid.Empty, _plant["P"].RunningStepId);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);

        RoutesToBuild.Instance.Remove(mission.MissionId);
        await TheRouteLoopLooksForTheCell(mission);
        await _plant["P"].StorageManager!.StartStoreLu();

        Assert.Equal(step.StepId, _plant["P"].RunningStepId);
        Assert.Equal(1, _warehouse.Calls.Count(call => call == nameof(IWarehouse.GetStorageReservationAsync)));
        Assert.DoesNotContain(nameof(IWarehouse.ReserveStorageAsync), _warehouse.Calls);
    }

    /// <summary>
    /// Lontano dal deposito la configurazione della posizione decide come prima del controllo, anche
    /// per una missione della configurazione con la cella prenotata per lei e la sua rotta. L'ingresso
    /// I prenota la cella 8 di S8 (rotta I->P->S8); poi I, che adesso ha <c>CheckOnly</c> con la
    /// destinazione fissa Y, rimanda la missione al giro delle rotte con la sua rotta, come fa una
    /// stazione di lettura quando legge l'UDC (<c>ProcessDeviceTypeSpecificLogic</c>). Il navigatore
    /// guarda la cella (ancora sua), e poi la configurazione di I manda la missione in Y: rotta I->Y,
    /// senza porta e senza il segno. Con la conferma che rispondeva «niente da rifare» ovunque la
    /// missione fosse, la configurazione di I non girava piu' e la missione restava verso S8.
    /// </summary>
    [Fact]
    public async Task AwayFromTheDepositThePositionsConfigurationStillDecidesForAMissionWithItsCell()
    {
        var mission = await AMissionOnPInTheStorageScene(on: "I", boundForX: false);
        _plant["I"].LookWhereAllocateLu = LookWhereAllocateLuTypes.BuildRouteToStore;
        _plant["I"].LinkedCellId = OldCellId;
        TheWarehouseAnswers(NewCellId, NewDoorCode);
        await TheRouteLoopLooksForTheCell(mission);
        Assert.Equal(NewDoorCode, mission.StorageDoorCode);
        Assert.True(mission.StorageCellReserved);
        Assert.Equal(["I", "P", "S8"], RouteOf(mission));

        _plant.Device("Y");
        _plant.Link("I", "Y");
        _plant["I"].LookWhereAllocateLu = LookWhereAllocateLuTypes.CheckOnly;
        _plant["I"].DestinationItemId = _plant["Y"].ItemGuid;
        RoutesToBuild.Instance.Items = new();
        _warehouse.Calls.Clear();

        await TheRouteLoopLooksForTheCell(mission);

        Assert.Equal(nameof(IWarehouse.GetStorageReservationAsync), _warehouse.Calls[0]);
        Assert.Equal(_plant["Y"].ItemGuid, mission.DestinationItemId);
        Assert.Equal(["I", "Y"], RouteOf(mission));
        Assert.Null(mission.StorageDoorCode);
        Assert.False(mission.StorageCellReserved);
    }

    /// <summary>
    /// Chi deposita non e' sempre il ricollocatore: Q, collegato a S8 ma non suo ricollocatore, ci
    /// deposita se la rotta passa da lui. La missione della configurazione con la cella 8 prenotata
    /// per lei e' spostata su Q (rotta Q->S8), e Q ha <c>CheckOnly</c> con la destinazione fissa Y. Il
    /// deposito da Q chiede al navigatore, che conferma la cella; la configurazione di Q non si rifa',
    /// perche' il prossimo passo e' il deposito, e il deposito parte verso A8, come partiva prima del
    /// controllo. Con la conferma che lasciava la rotta solo sul ricollocatore o su un membro del suo
    /// gruppo, la configurazione di Q mandava in Y l'UDC che stava per entrare in S8.
    /// </summary>
    [Fact]
    public async Task TheDepositFromADeviceThatIsNotTheRelocatorKeepsItsRouteAfterTheConfirmation()
    {
        var (mission, _) = await AConfigurationMissionOnPWithCell8ReservedByTheNavigator();
        Assert.Equal(MissionMoveOutcome.Moved, MissionGateway.Move(mission.MissionId, _plant["Q"].ItemGuid));
        await mission.PositionWork;
        Assert.Equal(["Q", "S8"], RouteOf(mission));
        var step = Assert.Single(mission.MissionSteps);
        Assert.True(mission.StorageCellReserved);
        Assert.NotEqual(_plant["Q"].ItemGuid, _plant["S8"].RelocateItemId);

        _plant.Device("Y");
        _plant.Link("Q", "Y");
        _plant["Q"].LookWhereAllocateLu = LookWhereAllocateLuTypes.CheckOnly;
        _plant["Q"].DestinationItemId = _plant["Y"].ItemGuid;
        _plant["Q"].LinkedCellId = OldCellId;
        _plant["Q"].RecordStorageLength = RecordStorageTypes.FromPlc;
        RoutesToBuild.Instance.Items = new();
        _warehouse.Calls.Clear();

        await _plant["Q"].StorageManager!.StartStoreLu();

        Assert.Equal(Guid.Empty, _plant["Q"].RunningStepId);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);

        RoutesToBuild.Instance.Remove(mission.MissionId);
        await TheRouteLoopLooksForTheCell(mission);

        Assert.Equal(_plant["S8"].ItemGuid, mission.DestinationItemId);
        Assert.Same(step, Assert.Single(mission.MissionSteps));
        Assert.Equal([nameof(IWarehouse.GetStorageReservationAsync)], _warehouse.Calls);

        await _plant["Q"].StorageManager!.StartStoreLu();

        Assert.Equal(step.StepId, _plant["Q"].RunningStepId);
        Assert.Equal(NewDoorCode, mission.StorageDoorCode);
    }

    /// <summary>
    /// La stessa missione, e mentre viaggiava il gestionale non le ha piu' tenuto la cella 8 (scaduta,
    /// o revocata). Il deposito non parte verso A8: il navigatore vede che la cella non e' piu' sua, la
    /// lascia, e le prenota un'altra cella di S8, la 10 della porta B8, con la rotta verso di lei e il
    /// segno della cella sua; il deposito parte verso B8. Prima partiva verso la cella 8, che il
    /// gestionale poteva aver dato a un'altra UDC.
    /// </summary>
    [Fact]
    public async Task AConfigurationMissionWhoseCellTheWarehouseRevokedDepositsInTheCellTheNavigatorReservesAgain()
    {
        var (mission, oldStep) = await AConfigurationMissionOnPWithCell8ReservedByTheNavigator();
        await S8HasASecondDoorB8OnCell10();
        _warehouse.RevokeStorageReservation(mission.MissionId);
        TheWarehouseAnswers(10, "B8");

        await _plant["P"].StorageManager!.StartStoreLu();

        Assert.Equal(Guid.Empty, _plant["P"].RunningStepId);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);

        RoutesToBuild.Instance.Remove(mission.MissionId);
        await TheRouteLoopLooksForTheCell(mission);

        Assert.Equal([false, true], _warehouse.StorageRequests.Select(request => request.AssignPosition));
        Assert.Equal(10, _warehouse.StorageReservationsByMission[mission.MissionId].CellId);
        Assert.Equal("B8", mission.StorageDoorCode);
        Assert.Equal("0031", mission.DestinationAsi);
        Assert.Equal(_plant["S8"].ItemGuid, mission.DestinationItemId);
        Assert.False(mission.OperatorDestination);
        Assert.False(mission.StorageCellLost);
        Assert.True(mission.StorageCellReserved);
        Assert.DoesNotContain(oldStep, MissionSteps.Instance.List);
        var step = Assert.Single(mission.MissionSteps);
        Assert.Equal(["P", "S8"], RouteOf(mission));

        await _plant["P"].StorageManager!.StartStoreLu();

        Assert.Equal(step.StepId, _plant["P"].RunningStepId);
        Assert.Equal("B8", mission.StorageDoorCode);
    }

    /// <summary>
    /// La conferma vale per la porta che il navigatore ha guardato (revisione finale della voce 39). Il
    /// navigatore conferma la cella 8 della porta A8; prima che il deposito parta, un'altra via scrive
    /// sulla missione la porta B8 di S8 con il segno della cella sua (come la riverifica del
    /// ricollocatore, che lascia la cella e ne cerca un'altra). Il deposito non parte verso B8 con la
    /// conferma di A8: la missione torna al navigatore. Prima la conferma era legata solo alla
    /// posizione, e il deposito partiva verso una porta che il gestionale non aveva confermato.
    /// </summary>
    [Fact]
    public async Task AConfirmationIsOnlyForTheDoorItLookedAt()
    {
        var (mission, step) = await AConfigurationMissionOnPWithCell8ReservedByTheNavigator();
        await S8HasASecondDoorB8OnCell10();
        await TheRouteLoopLooksForTheCell(mission);
        Assert.Equal([nameof(IWarehouse.GetStorageReservationAsync)], _warehouse.Calls);
        Assert.Same(step, Assert.Single(mission.MissionSteps));

        mission.StorageDoorCode = "B8";
        mission.StorageCellReserved = true;
        await _plant["P"].StorageManager!.StartStoreLu();

        Assert.Equal(Guid.Empty, _plant["P"].RunningStepId);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
    }

    /// <summary>
    /// La stessa missione con il gestionale che non risponde: il deposito non parte, e il navigatore
    /// lascia porta, cella e rotta come sono e rimette la missione in coda con il suo freno (il
    /// fallimento e' contato); un altro giro di deposito intanto non parte. Quando il gestionale
    /// torna, il navigatore conferma la cella e il deposito parte verso A8. Prima partiva senza
    /// chiedere.
    /// </summary>
    [Fact]
    public async Task WhileTheWarehouseDoesNotAnswerTheDepositOfAConfigurationMissionWaitsUnderTheBrake()
    {
        var (mission, step) = await AConfigurationMissionOnPWithCell8ReservedByTheNavigator();
        _warehouse.UnavailableOperations.Add(nameof(IWarehouse.GetStorageReservationAsync));

        await _plant["P"].StorageManager!.StartStoreLu();

        Assert.Equal(Guid.Empty, _plant["P"].RunningStepId);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);

        RoutesToBuild.Instance.Remove(mission.MissionId);
        await TheRouteLoopLooksForTheCell(mission);

        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
        Assert.Equal(2, Navigator.RoutePathRetries.RegisterFailure(mission.MissionId));
        Assert.Same(step, Assert.Single(mission.MissionSteps));
        Assert.Equal(NewDoorCode, mission.StorageDoorCode);
        Assert.True(mission.StorageCellReserved);
        Assert.False(mission.StorageCellLost);

        await _plant["P"].StorageManager!.StartStoreLu();

        Assert.Equal(Guid.Empty, _plant["P"].RunningStepId);

        _warehouse.UnavailableOperations.Clear();
        RoutesToBuild.Instance.Remove(mission.MissionId);
        await TheRouteLoopLooksForTheCell(mission);
        await _plant["P"].StorageManager!.StartStoreLu();

        Assert.Equal(step.StepId, _plant["P"].RunningStepId);
        Assert.Equal(NewDoorCode, mission.StorageDoorCode);
        Assert.Equal(NewCellId, _warehouse.StorageReservationsByMission[mission.MissionId].CellId);
    }

    /// <summary>
    /// Depositano come prima, senza chiedere niente al gestionale, le missioni la cui porta non e'
    /// una prenotazione loro (risposta del proprietario del 2/10 alla domanda del compito 3): un
    /// ostacolo spostato per estrarre l'UDC dietro, la cui cella <c>MoveObstacleLu</c> prenota prima
    /// che la missione esista (il gestionale non la lega a nessuno); e una missione che aveva la sua
    /// cella 8 e prende la porta A9 di un'altra missione dello stesso tipo, come il selettore di un
    /// deposito di gruppo, e con la porta perde il segno. Chiedendo, il gestionale risponderebbe «non
    /// c'e'» e la cella si rifarebbe altrove. L'ostacolo qui e' scritto a mano, e guarda solo il deposito;
    /// che la via vera dell'ostacolo (<c>AutomationDevice.AddMissionToPickUp</c>) non alzi il segno lo
    /// guarda MissionCarriesItsNoteAssignmentTest.AnObstacleMovedAsideDepositsWithoutAskingForItsCell (voce 51).
    /// </summary>
    [Theory]
    [InlineData("obstacle")]
    [InlineData("copied door")]
    public async Task ADepositToADoorThatIsNotTheMissionsOwnReservationDoesNotAsk(string how)
    {
        PathMission mission;
        if (how == "obstacle")
        {
            mission = await AMissionOnPReadyToDepositInS7();
            mission.MissionType = MissionTypes.ChangeStoreLocation;
            _warehouse.RevokeStorageReservation(mission.MissionId);
        }
        else
        {
            (mission, _) = await AConfigurationMissionOnPWithCell8ReservedByTheNavigator();
            mission.StorageDoorCode = OtherDoorCode;
            mission.DestinationItemId = _plant["S9"].ItemGuid;
            _plant.PlannedStep(mission, "P", "S9");
        }

        _warehouse.Calls.Clear();
        Assert.False(mission.StorageCellReserved);
        var step = Assert.Single(mission.MissionSteps);

        await _plant["P"].StorageManager!.StartStoreLu();

        Assert.Equal(step.StepId, _plant["P"].RunningStepId);
        Assert.Null(RoutesToBuild.Instance[mission.MissionId]);
        Assert.DoesNotContain(nameof(IWarehouse.GetStorageReservationAsync), _warehouse.Calls);
    }

    /// <summary>
    /// La missione della configurazione con la cella 8 prenotata dal giro delle rotte, poi il riavvio
    /// del motore: la missione rinasce dalla sua riga con la cella sua, e il suo deposito chiede ancora
    /// al navigatore prima di partire. Senza il segno nella riga, dopo un riavvio la missione
    /// depositava senza chiedere.
    /// </summary>
    [Fact]
    public async Task AfterARestartTheDepositOfAConfigurationMissionStillAsksForItsCell()
    {
        var (mission, _) = await AConfigurationMissionOnPWithCell8ReservedByTheNavigator();

        // Il riavvio: le collezioni di esecuzione si svuotano, i dispositivi restano (PathDevices
        // non si svuota alla ricarica), e le missioni rinascono dalle righe.
        ActiveMissions.Instance.RemoveAll(_ => true);
        MissionSteps.Instance.RemoveAll(_ => true);
        RoutesToBuild.Instance.Items = new();
        Navigator.NavStatus = NavigatorStatusTypes.Recover;

        await ORM.DbInfo.RecoverFailure();

        var reloaded = ActiveMissions.Instance[mission.MissionId];
        Assert.NotNull(reloaded);
        Assert.NotSame(mission, reloaded);
        await reloaded.PositionWork;
        await reloaded.RouteWork.WaitAsync(TimeSpan.FromSeconds(30));
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        Assert.True(reloaded.StorageCellReserved);
        Assert.Equal(NewDoorCode, reloaded.StorageDoorCode);
        Assert.Equal(_plant["S8"].ItemGuid, reloaded.DestinationItemId);

        // La ricostruzione che fa la ricarica (ActiveMissions.Add, in un lavoro che non si puo'
        // attendere), rifatta qui: la rotta verso la porta A8.
        await reloaded.BuildPathToDestination();
        Assert.Equal(["P", "S8"], RouteOf(reloaded));
        RoutesToBuild.Instance.Items = new();

        await _plant["P"].StorageManager!.StartStoreLu();

        Assert.Equal(Guid.Empty, _plant["P"].RunningStepId);
        Assert.Same(reloaded, RoutesToBuild.Instance[reloaded.MissionId]);
    }

    /// <summary>
    /// In marcia il giro completo del deposito di una missione della configurazione con la cella
    /// prenotata per lei, dal ciclo delle estrazioni di P come in campo (vedi
    /// <see cref="InRunTheRelocatorsOwnLoopDepositsWithTheConfirmedCell"/>): al primo giro il deposito
    /// manda la missione al navigatore; il navigatore chiede al gestionale (una domanda, nessuna
    /// prenotazione) e conferma; al giro dopo il deposito parte da solo verso A8.
    /// </summary>
    [Fact]
    public async Task InRunTheRelocatorsOwnLoopAsksForTheConfigurationMissionsCellBeforeTheDeposit()
    {
        var (mission, step) = await AConfigurationMissionOnPWithCell8ReservedByTheNavigator();
        Navigator.NavStatus = NavigatorStatusTypes.Run;
        Assert.False(_plant["P"].AutoExecuteStep);
        Assert.Equal(DeviceStatusTypes.Ready, _plant["P"].Status);
        Navigator.EventSink = _recorder;

        await OneTurnOfTheExtractionLoopOf(_plant["P"]);
        await ChangeDestinationStepStartsTest.NoStepStartIsLeftCountedOnceItIsOver(mission);

        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
        Assert.Equal(Guid.Empty, _plant["P"].RunningStepId);

        RoutesToBuild.Instance.Remove(mission.MissionId);
        await TheRouteLoopLooksForTheCell(mission);
        await OneTurnOfTheExtractionLoopOf(_plant["P"]);
        await _recorder.Until(() => _plant["P"].RunningStepId == step.StepId).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, _warehouse.Calls.Count(call => call == nameof(IWarehouse.GetStorageReservationAsync)));
        Assert.DoesNotContain(nameof(IWarehouse.ReserveStorageAsync), _warehouse.Calls);
        Assert.Equal(NewDoorCode, mission.StorageDoorCode);
    }

    // ---------------------------------------------------------------------------------------
    // Gli avvii degli step mentre il cambio aspetta il gestionale
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// La missione va in S7 con la cella 7 e la rotta P->S7. Mentre il cambio verso S8 aspetta il
    /// gestionale, P prova a partire per S7 (in Run la rotta vecchia continuerebbe): con il cambio
    /// cominciato lo step non parte, e il cambio passa con la cella 8. Prima lo step partiva, il
    /// commit lo trovava in volo dopo che la prenotazione aveva gia' lasciato la cella 7, e la
    /// missione andava verso la porta A7 senza che il gestionale gliela tenesse piu'.
    /// </summary>
    [Fact]
    public async Task TheOldRouteDoesNotSetOffWhileTheWarehouseAnswers()
    {
        var mission = await AMissionOnPInTheStorageScene();
        var oldStep = TheMissionIsBoundForS7WithCell7(mission);
        _warehouse.ReserveStorageGate = new TaskCompletionSource();
        var change = MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid);
        await _warehouse.ReserveStorageAsked.WaitAsync(TimeSpan.FromSeconds(30));

        _plant["P"].StartPutToMOVE_LU();

        Assert.Equal(Guid.Empty, _plant["P"].RunningStepId);
        _warehouse.ReserveStorageGate.SetResult();

        Assert.Equal(new MissionDestinationResult(MissionDestinationOutcome.Changed),
            await change.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(NewCellId, _warehouse.StorageReservationsByMission[mission.MissionId].CellId);
        Assert.Equal(NewDoorCode, mission.StorageDoorCode);
        Assert.Equal(["P", "S8"], RouteOf(mission));
        Assert.DoesNotContain(oldStep, MissionSteps.Instance.List);
        ChangeDestinationStepStartsTest.NoStepStartIsLeftCounted(mission);
    }

    /// <summary>
    /// Mentre il cambio verso S8 aspetta il gestionale, X - che riceve con GetFrom - prova a tirare a
    /// se' l'UDC per lo step vecchio P->X: con il cambio cominciato non parte (ne' il posto su X ne'
    /// il gettone su P), e il cambio passa. Il GetFrom gira sotto il semaforo di X, che il commit non
    /// tiene: prima lo step partiva, e il commit lo trovava in volo.
    /// </summary>
    [Fact]
    public async Task AGetFromWhileTheWarehouseAnswersDoesNotSetOff()
    {
        var mission = await AMissionOnPInTheStorageScene();
        var oldStep = _plant.PlannedStep(mission, "P", "X");
        _plant["X"].MotionType = MotionTypes.GetFrom;
        _plant["P"].Status = DeviceStatusTypes.Ready;
        _plant["X"].Status = DeviceStatusTypes.Ready;
        TheWarehouseAnswers(NewCellId, NewDoorCode);
        _warehouse.ReserveStorageGate = new TaskCompletionSource();
        var change = MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid);
        await _warehouse.ReserveStorageAsked.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.NotNull(_plant["X"].StepExecutor);
        _plant["X"].StepExecutor!.StartReceivingLu(_plant["P"]);

        Assert.Equal(Guid.Empty, _plant["X"].WaitingStepId);
        Assert.Equal(Guid.Empty, _plant["P"].RunningStepId);
        _warehouse.ReserveStorageGate.SetResult();

        Assert.Equal(MissionDestinationOutcome.Changed, (await change.WaitAsync(TimeSpan.FromSeconds(30))).Outcome);
        Assert.Equal(["P", "S8"], RouteOf(mission));
        Assert.DoesNotContain(oldStep, MissionSteps.Instance.List);
        ChangeDestinationStepStartsTest.NoStepStartIsLeftCounted(mission);
    }

    /// <summary>
    /// Con un cambio dell'operatore in corso P non comincia il deposito in S7: niente verifica della
    /// cella presso il gestionale (che potrebbe riscegliere la cella e prenotare per la stessa
    /// missione), niente gettone.
    /// </summary>
    [Fact]
    public async Task AnOperatorChangeInProgressHoldsBackTheDeposit()
    {
        var mission = await AMissionOnPReadyToDepositInS7();
        Assert.Equal(PathMission.OperatorChangeStart.Begun, mission.TryBeginOperatorChange());

        try
        {
            Assert.NotNull(_plant["P"].StorageManager);
            await _plant["P"].StorageManager!.StartStoreLu();
        }
        finally
        {
            mission.EndOperatorChange();
        }

        Assert.Empty(_warehouse.Calls);
        Assert.Equal(Guid.Empty, _plant["P"].RunningStepId);
        ChangeDestinationStepStartsTest.NoStepStartIsLeftCounted(mission);
    }

    // ---------------------------------------------------------------------------------------
    // Gli avvii finiti non restano contati
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Il movimento singolo di P verso A parte (il gettone su P, il posto su A), e finito di avviare
    /// non lascia contato niente sulla missione: un conto rimasto aperto rifiuterebbe ogni cambio
    /// dell'operatore come passo in volo, per sempre.
    /// </summary>
    [Fact]
    public async Task AMovementThatSetsOffLeavesNoStartCounted()
    {
        var (mission, oldStep) = await AMissionOnPBoundForA();

        _plant["P"].StartPutToMOVE_LU();

        Assert.Equal(oldStep.StepId, _plant["P"].RunningStepId);
        Assert.Equal(oldStep.StepId, _plant["A"].WaitingStepId);
        ChangeDestinationStepStartsTest.NoStepStartIsLeftCounted(mission);
    }

    /// <summary>Lo stesso per il GetFrom: X tira a se' l'UDC da P (il posto su X, il gettone su P).</summary>
    [Fact]
    public async Task AGetFromThatSetsOffLeavesNoStartCounted()
    {
        var mission = await AMissionOnPInTheStorageScene();
        var step = _plant.PlannedStep(mission, "P", "X");
        _plant["X"].MotionType = MotionTypes.GetFrom;
        _plant["P"].Status = DeviceStatusTypes.Ready;
        _plant["X"].Status = DeviceStatusTypes.Ready;

        Assert.NotNull(_plant["X"].StepExecutor);
        _plant["X"].StepExecutor!.StartReceivingLu(_plant["P"]);

        Assert.Equal(step.StepId, _plant["X"].WaitingStepId);
        Assert.Equal(step.StepId, _plant["P"].RunningStepId);
        ChangeDestinationStepStartsTest.NoStepStartIsLeftCounted(mission);
    }

    /// <summary>Lo stesso per il deposito: P deposita in S7 (il gettone su P), con la cella 7.</summary>
    [Fact]
    public async Task ADepositThatSetsOffLeavesNoStartCounted()
    {
        var mission = await AMissionOnPReadyToDepositInS7();

        Assert.NotNull(_plant["P"].StorageManager);
        await _plant["P"].StorageManager!.StartStoreLu();

        Assert.Equal(Assert.Single(mission.MissionSteps).StepId, _plant["P"].RunningStepId);
        ChangeDestinationStepStartsTest.NoStepStartIsLeftCounted(mission);
    }

    // ---------------------------------------------------------------------------------------
    // Il perche' dei rifiuti nel registro
    // ---------------------------------------------------------------------------------------

    /// <summary>L'UDC senza l'identificativo del gestionale: «nessuna cella», con il perche' in un avviso.</summary>
    [Fact]
    public async Task ALoadUnitWithoutAnIdIsRefusedWithItsReasonInTheLog()
    {
        using var log = new RecordingLogWriter();
        var mission = await AMissionOnPInTheStorageScene();
        mission.LuId = 0;

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid);

        Assert.Equal(MissionDestinationOutcome.NoStorageCell, result.Outcome);
        await TheRefusalIsLogged(log, mission, "LuId 0");
    }

    /// <summary>Un magazzino senza porte mappate nel database del WCS: «nessuna cella», e il registro dice che e' configurazione.</summary>
    [Fact]
    public async Task AStorageWithoutMappedDoorsIsRefusedWithItsReasonInTheLog()
    {
        using var log = new RecordingLogWriter();
        var mission = await AMissionOnPInTheStorageScene();
        _plant.Device("S4", DeviceTypes.Storage);
        _plant.Link("P", "S4");

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S4"].ItemGuid);

        Assert.Equal(MissionDestinationOutcome.NoStorageCell, result.Outcome);
        Assert.Empty(_warehouse.Calls);
        await TheRefusalIsLogged(log, mission, "nessuna porta del magazzino scelto");
    }

    /// <summary>La cella del magazzino scelto senza un magazzino del gestionale: anche questo e' configurazione.</summary>
    [Fact]
    public async Task ACellWithoutAWarehouseIsRefusedWithItsReasonInTheLog()
    {
        using var log = new RecordingLogWriter();
        var mission = await AMissionOnPInTheStorageScene();
        _warehouse.Cells[NewCellId] = Cell(NewCellId, NewDoorCode) with { WarehouseId = 0 };

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid);

        Assert.Equal(MissionDestinationOutcome.NoStorageCell, result.Outcome);
        await TheRefusalIsLogged(log, mission, "non ha un magazzino del gestionale");
    }

    /// <summary>Il gestionale risponde che l'UDC non ha riferimenti: non e' un magazzino pieno, e il registro lo dice.</summary>
    [Fact]
    public async Task ALoadUnitWithoutReferencesIsRefusedWithItsReasonInTheLog()
    {
        using var log = new RecordingLogWriter();
        var mission = await AMissionOnPInTheStorageScene();
        _warehouse.StorageReservations[LoadUnitId] = new StorageReservation(Guid.Empty,
            StorageReservationOutcome.LoadUnitHasNoReferences, 0, string.Empty, string.Empty, 0, NeedsCompaction: false);

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid);

        Assert.Equal(MissionDestinationOutcome.NoStorageCell, result.Outcome);
        await TheRefusalIsLogged(log, mission, "non ha riferimenti");
    }

    /// <summary>
    /// La riga del rifiuto: un avviso (passa anche con la diagnostica spenta, come in produzione),
    /// con la missione, l'esito e il perche'. Si aspetta la riga, che il registro scrive da un altro
    /// thread, con un tetto che fa fallire invece di appendere.
    /// </summary>
    private static Task TheRefusalIsLogged(RecordingLogWriter log, PathMission mission, string reason) =>
        log.Wait((description, level) =>
                level == LogEntryType.Warning &&
                description.Contains(mission.MissionId.ToString()) &&
                description.Contains(nameof(MissionDestinationOutcome.NoStorageCell)) &&
                description.Contains(reason))
            .WaitAsync(TimeSpan.FromSeconds(30));

    // ---------------------------------------------------------------------------------------
    // In Run
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// In marcia una rotta costruita fuori dal giro del navigatore non la vede nessuno: il dispositivo
    /// di posizione si sveglia da se' e parte verso X (il suo gettone e' lo step nuovo).
    /// </summary>
    [Fact]
    public async Task InRunThePositionDeviceSetsOffByItself()
    {
        var (mission, _) = await AMissionOnPBoundForA();

        // Il passaggio in Run sveglia i dispositivi con AutoExecuteStep, e P partirebbe da solo per
        // la rotta vecchia (P->A) prima del cambio: AutoExecuteStep si accende dopo il passaggio.
        foreach (var name in _plant.DeviceNames) _plant[name].AutoExecuteStep = false;
        Navigator.NavStatus = NavigatorStatusTypes.Run;
        _plant["P"].AutoExecuteStep = true;
        Navigator.EventSink = _recorder;

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["X"].ItemGuid);

        Assert.Equal(MissionDestinationOutcome.Changed, result.Outcome);
        var step = Assert.Single(mission.MissionSteps);
        await _recorder.Until(() => _plant["P"].RunningStepId == step.StepId).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(step.StepId, _plant["X"].WaitingStepId);
        await ChangeDestinationStepStartsTest.NoStepStartIsLeftCountedOnceItIsOver(mission);
    }

    /// <summary>
    /// In marcia si sveglia anche chi avvia il prossimo step dall'altra parte: X riceve con GetFrom e
    /// ha P in coda (come dopo un GetFrom rinunciato durante il cambio, che nessuno ripeterebbe), e P
    /// non si sveglia da se'. Dopo il cambio verso X e' X a tirare a se' l'UDC con lo step nuovo.
    /// </summary>
    [Fact]
    public async Task InRunTheDeviceThatPullsTheNextStepSetsOffByItself()
    {
        var (mission, _) = await AMissionOnPBoundForA();
        foreach (var name in _plant.DeviceNames) _plant[name].AutoExecuteStep = false;
        _plant["X"].MotionType = MotionTypes.GetFrom;
        _plant["P"].Status = DeviceStatusTypes.Ready;
        _plant["X"].Status = DeviceStatusTypes.Ready;
        Assert.True(_plant["X"].AddDeviceReadyToTransferLu(_plant["P"]));
        Navigator.NavStatus = NavigatorStatusTypes.Run;
        _plant["X"].AutoExecuteStep = true;
        Navigator.EventSink = _recorder;

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["X"].ItemGuid);

        Assert.Equal(MissionDestinationOutcome.Changed, result.Outcome);
        var step = Assert.Single(mission.MissionSteps);
        await _recorder.Until(() => _plant["P"].RunningStepId == step.StepId).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(step.StepId, _plant["X"].WaitingStepId);
        await ChangeDestinationStepStartsTest.NoStepStartIsLeftCountedOnceItIsOver(mission);
    }

    /// <summary>
    /// In marcia il giro completo del deposito di una missione dell'operatore. P, il ricollocatore,
    /// non si avvia da se' (<c>AutoExecuteStep</c> e' falso per ogni dispositivo collegato a un
    /// magazzino, cioe' per ogni dispositivo che ci deposita) e il risveglio del navigatore non lo
    /// muove: lo muove il suo ciclo delle estrazioni, che richiama <c>ExecuteStep</c> ogni due
    /// secondi. Al primo giro il deposito non ha la conferma della cella e manda la missione al
    /// navigatore; il navigatore chiede al gestionale (una domanda, nessuna prenotazione) e conferma;
    /// al giro dopo il deposito parte da solo verso A8 con quella conferma. Le altre prove del
    /// deposito chiamano <c>StartStoreLu</c> a mano a motore fermo: questa e' quella che passa da
    /// <c>ExecuteStep</c> in marcia, come in campo.
    /// </summary>
    [Fact]
    public async Task InRunTheRelocatorsOwnLoopDepositsWithTheConfirmedCell()
    {
        var mission = await AnOperatorsMissionOnPReadyToDepositInS8();
        var step = Assert.Single(mission.MissionSteps);
        Navigator.NavStatus = NavigatorStatusTypes.Run;
        Assert.False(_plant["P"].AutoExecuteStep);
        Assert.Equal(DeviceStatusTypes.Ready, _plant["P"].Status);
        Navigator.EventSink = _recorder;

        await OneTurnOfTheExtractionLoopOf(_plant["P"]);
        await ChangeDestinationStepStartsTest.NoStepStartIsLeftCountedOnceItIsOver(mission);

        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
        Assert.Equal(Guid.Empty, _plant["P"].RunningStepId);

        await TheRouteLoopLooksForTheCell(mission);
        await OneTurnOfTheExtractionLoopOf(_plant["P"]);
        await _recorder.Until(() => _plant["P"].RunningStepId == step.StepId).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, _warehouse.Calls.Count(call => call == nameof(IWarehouse.GetStorageReservationAsync)));
        Assert.DoesNotContain(nameof(IWarehouse.ReserveStorageAsync), _warehouse.Calls);
    }

    /// <summary>
    /// Un giro del ciclo delle estrazioni del dispositivo (<c>CheckExtraction</c>, ogni due secondi in
    /// marcia): con un'UDC a bordo non estrae niente, e richiama <c>ExecuteStep</c> senza aspettarlo.
    /// </summary>
    private static Task OneTurnOfTheExtractionLoopOf(AutomationDevice device)
    {
        var method = typeof(Wcs.Engine.Items.Managers.AutomationDeviceStorageManager).GetMethod("CheckExtraction",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        return (Task)method!.Invoke(device.StorageManager, null)!;
    }

    // ---------------------------------------------------------------------------------------
    // Un'estrazione gia' prelevata mandata in un magazzino (decisione 8)
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Decisione 8 del proprietario e risposta 3 del 2/10: l'UDC dell'estrazione e' uscita dalla cella
    /// 7 di S7 ed e' sul suo ricollocatore P; l'operatore la manda in S8. Al commit la porta di
    /// prelievo si toglie (nulla, non vuota) e la missione diventa un deposito (<c>Storing</c>) verso
    /// la cella 8; il gestionale non riceve segnalazioni, nemmeno con un'assegnazione e una riga di coda
    /// ancora sulla missione (la riga di coda c'e' perche' l'assenza della sua segnalazione dica qualcosa:
    /// senza, il motore non la manderebbe comunque, revisione finale del primo giro della voce 39,
    /// M86/M107). Una ricostruzione non la rifa' cambio cella, e un secondo cambio la reindirizza.
    /// Prima restava un'estrazione con le due porte di due magazzini: la ricostruzione ne faceva un
    /// cambio cella, e il secondo cambio rispondeva «non reindirizzabile».
    /// </summary>
    [Fact]
    public async Task APickedExtractionSentToAStorageBecomesADepositThere()
    {
        var mission = await APickedExtractionOnP();
        mission.GuidAssignmentLu = new Guid("0b0e5a1c-7700-4000-8000-000000000042");
        mission.ExtractionQueueId = 42;

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid);

        Assert.Equal(MissionDestinationOutcome.Changed, result.Outcome);
        Assert.Equal(["P", "S8"], RouteOf(mission));
        Assert.Null(mission.PickUpDoorCode);
        Assert.Equal(MissionTypes.Storing, mission.MissionType);
        Assert.Equal(NewDoorCode, mission.StorageDoorCode);
        Assert.False(await mission.TheDoorsKeepItOffTheStraightRoute());
        Assert.DoesNotContain(nameof(IWarehouse.ReportMissionLeftAssignmentUnservedAsync), _warehouse.Calls);
        Assert.DoesNotContain(nameof(IWarehouse.ReportLoadUnitLeftExtractionQueueAsync), _warehouse.Calls);

        // Voce 45: il prelievo non ha servito niente (i tre campi sono vuoti), e la coda del fatto
        // «l'estrazione prelevata e' diventata un deposito» resta vuota.
        Assert.Empty(await ORM.PickedExtractionReportOutbox.PendingAsync(10));

        // La ricostruzione che fanno il navigatore e la ricarica.
        await mission.BuildPathToDestination();
        Assert.Equal(MissionTypes.Storing, mission.MissionType);
        Assert.Equal(["P", "S8"], RouteOf(mission));

        var again = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["X"].ItemGuid);
        Assert.Equal(MissionDestinationOutcome.Changed, again.Outcome);
        Assert.Equal(["P", "X"], RouteOf(mission));
    }

    /// <summary>
    /// Item 54, decision 4, with the order of item 45: a picked-up extraction moves from the conveyor I to
    /// the relocator P, and the operator sends it to S8 meanwhile - the change waits. The step leaves the
    /// route before its arrival writes the position, so the change is applied by the arrival. The commit of
    /// a picked extraction that becomes a deposit waits for the background work of the last change of
    /// position (<c>MissionGateway.AfterThePickUpHandOffAsync</c>): applied from inside that work, it
    /// would wait for itself for ever. It is applied once the arrival is over: a deposit towards S8, from P.
    /// </summary>
    [Fact]
    public async Task APickedExtractionWhoseChangeWaitedForTheStepIsAppliedAfterTheArrival()
    {
        var mission = await AMissionOnPInTheStorageScene(on: "I");
        mission.MissionType = MissionTypes.Extraction;
        mission.PickUpDoorCode = OldDoorCode;
        TheWarehouseAnswers(NewCellId, NewDoorCode);
        var step = _plant.PlannedStep(mission, "I", "P");
        _plant["I"].RunningStepId = step.StepId;
        _plant["I"].Status = DeviceStatusTypes.Moving;

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid);
        await PendingMissionChanges.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(MissionDestinationOutcome.Pending, result.Outcome);

        _plant["I"].RemoveStep(step);
        _plant["I"].Status = DeviceStatusTypes.Ready;
        await PendingMissionChanges.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(MissionGateway.PendingChangeOf(mission.MissionId));

        mission.PositionItemId = _plant["P"].ItemGuid;
        await mission.PositionWork.WaitAsync(TimeSpan.FromSeconds(30));
        await PendingMissionChanges.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Null(MissionGateway.PendingChangeOf(mission.MissionId));
        Assert.Equal(_plant["S8"].ItemGuid, mission.DestinationItemId);
        Assert.Equal(MissionTypes.Storing, mission.MissionType);
        Assert.Equal(["P", "S8"], RouteOf(mission));
    }

    private static readonly Guid ServedGuid = new("5a1e0045-0000-4000-8000-000000000045");

    /// <summary>
    /// Voce 45, decisione 1 del proprietario del 10/10: l'estrazione prelevata aveva servito
    /// l'assegnazione 902 (GUID <see cref="ServedGuid"/>) e la riga di estrazione 8045; mandata in S8
    /// diventa un deposito, e il fatto per il gestionale entra nella coda del WCS con i tre campi,
    /// l'UDC, l'utente del motore e il motivo. Durante il cambio non parte niente: lo manda il
    /// dispatcher. I tre campi si azzerano, in memoria e nella riga della missione, che dice gia' il
    /// tipo nuovo.
    /// </summary>
    [Fact]
    public async Task APickedExtractionSentToAStorageQueuesTheReopenOfWhatItServed()
    {
        var mission = await APickedExtractionOnP();
        mission.ServedAssignmentId = 902m;
        mission.ServedAssignmentGuid = ServedGuid;
        mission.ServedExtractionRowId = 8045m;

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid);

        Assert.Equal(MissionDestinationOutcome.Changed, result.Outcome);
        Assert.Null(mission.PickUpDoorCode);
        Assert.Equal(MissionTypes.Storing, mission.MissionType);
        Assert.DoesNotContain(nameof(IWarehouse.ReportPickedExtractionBecameDepositAsync), _warehouse.Calls);

        var report = Assert.Single(await ORM.PickedExtractionReportOutbox.PendingAsync(10));
        Assert.NotEqual(Guid.Empty, report.OperationId);
        Assert.Equal(mission.MissionId, report.MissionId);
        Assert.Equal(42m, report.LoadUnitId);
        Assert.Equal(902m, report.AssignmentId);
        Assert.Equal(ServedGuid, report.AssignmentGuid);
        Assert.Equal(8045m, report.ExtractionRowId);
        Assert.Equal("KazamControl.Process", report.UserId);
        Assert.Equal("The operator sent the picked-up extraction to a storage (cell A8): it became a deposit", report.Reason);
        Assert.Equal(0, report.AttemptCount);
        Assert.Null(report.LastError);
        Assert.False(report.Parked);

        Assert.Equal(0m, mission.ServedAssignmentId);
        Assert.Equal(Guid.Empty, mission.ServedAssignmentGuid);
        Assert.Equal(0m, mission.ServedExtractionRowId);

        await using var context = _database.CreateContext();
        var row = context.Missions.Single(m => m.MissionId == mission.MissionId);
        Assert.Equal((short)MissionTypes.Storing, row.MissionType);
        Assert.Null(row.PickUpDoorCode);
        Assert.Equal(0m, row.ServedAssignmentId);
        Assert.Null(row.ServedAssignmentGuid);
        Assert.Equal(0m, row.ServedExtractionRowId);
    }

    /// <summary>
    /// Basta una delle tre parti servite: la sola riga di estrazione (nessuna assegnazione, nessun GUID)
    /// accoda il fatto con le altre due vuote.
    /// </summary>
    [Fact]
    public async Task AnExtractionRowAloneIsEnoughToQueueTheReopen()
    {
        var mission = await APickedExtractionOnP();
        mission.ServedExtractionRowId = 8046m;

        Assert.Equal(MissionDestinationOutcome.Changed,
            (await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid)).Outcome);

        var report = Assert.Single(await ORM.PickedExtractionReportOutbox.PendingAsync(10));
        Assert.Equal(0m, report.AssignmentId);
        Assert.Equal(Guid.Empty, report.AssignmentGuid);
        Assert.Equal(8046m, report.ExtractionRowId);
    }

    /// <summary>
    /// Il fatto si accoda una volta sola: un secondo cambio del deposito che ne e' nato non ne aggiunge
    /// un altro (la missione non e' piu' un'estrazione, e i tre campi sono gia' vuoti), qui verso un
    /// altro magazzino, S9, con la cella 9.
    /// </summary>
    [Fact]
    public async Task ASecondChangeDoesNotQueueTheReopenAgain()
    {
        var mission = await APickedExtractionOnP();
        mission.ServedAssignmentId = 902m;
        Assert.Equal(MissionDestinationOutcome.Changed,
            (await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid)).Outcome);
        var first = Assert.Single(await ORM.PickedExtractionReportOutbox.PendingAsync(10));
        TheWarehouseAnswers(OtherCellId, OtherDoorCode);

        Assert.Equal(MissionDestinationOutcome.Changed,
            (await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S9"].ItemGuid)).Outcome);
        Assert.Equal(OtherDoorCode, mission.StorageDoorCode);

        Assert.Equal(first.OperationId, Assert.Single(await ORM.PickedExtractionReportOutbox.PendingAsync(10)).OperationId);
    }

    /// <summary>
    /// <b>Una scrittura sola.</b> Con lo scrittore di stato (come in esercizio), la riga della coda va
    /// nella stessa scrittura della riga della missione che dice «deposito» con i tre campi vuoti, e
    /// nessuna scrittura prima di lei dice gia' «deposito»: un arresto fra due scritture non lascia un
    /// deposito che ha dimenticato il fatto. Lo scrittore qui esegue una scrittura per contesto, per
    /// vedere che cosa porta ciascuna.
    /// </summary>
    [Fact]
    public async Task TheReopenIsWrittenInTheSameWriteAsTheDeposit()
    {
        var mission = await APickedExtractionOnP();
        mission.ServedAssignmentId = 902m;
        mission.ServedExtractionRowId = 8045m;

        var writes = new List<(bool Report, short? MissionType, decimal? Served)>();
        var writer = new QueuedDatabaseWriter<WcsWrite>(async (batch, ct) =>
        {
            foreach (var write in batch)
            {
                await using var context = _database.CreateContext();
                await write(context);
                var row = context.ChangeTracker.Entries<Mission>().Select(entry => entry.Entity)
                    .SingleOrDefault(entity => entity.MissionId == mission.MissionId);
                lock (writes)
                    writes.Add((context.ChangeTracker.Entries<PendingPickedExtractionReport>().Any(),
                        row?.MissionType, row?.ServedAssignmentId));
                await context.SaveChangesAsync(ct);
            }
        });
        Navigator.StateWriter = writer;
        try
        {
            Assert.Equal(MissionDestinationOutcome.Changed,
                (await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid)).Outcome);
        }
        finally
        {
            Navigator.StateWriter = null;
            await writer.DisposeAsync();
        }

        var withTheReport = Assert.Single(writes, write => write.Report);
        Assert.Equal((short)MissionTypes.Storing, withTheReport.MissionType);
        Assert.Equal(0m, withTheReport.Served);
        Assert.DoesNotContain(writes.TakeWhile(write => !write.Report),
            write => write.MissionType == (short)MissionTypes.Storing);
        Assert.Single(await ORM.PickedExtractionReportOutbox.PendingAsync(10));
    }

    /// <summary>
    /// La scrittura non riesce (lo scrittore di stato e' fermo): il cambio resta avvenuto, e una riga di
    /// errore dice che il gestionale non riaprira' l'ordine, che va sistemato a mano.
    /// </summary>
    [Fact]
    public async Task AReopenThatCannotBeWrittenIsSaidAsAnError()
    {
        using var log = new RecordingLogWriter();
        var mission = await APickedExtractionOnP();
        mission.ServedAssignmentId = 902m;

        var writer = new QueuedDatabaseWriter<WcsWrite>((_, _) => Task.CompletedTask);
        await writer.StopAsync();
        Navigator.StateWriter = writer;
        MissionDestinationResult result;
        try
        {
            result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid);
        }
        finally
        {
            Navigator.StateWriter = null;
            await writer.DisposeAsync();
        }

        Assert.Equal(MissionDestinationOutcome.Changed, result.Outcome);
        Assert.Equal(MissionTypes.Storing, mission.MissionType);
        Assert.Empty(await ORM.PickedExtractionReportOutbox.PendingAsync(10));
        await log.Wait((description, level) => level == LogEntryType.Error &&
            description.StartsWith($"Missione {mission.MissionId}, UDC 42: l'estrazione gia' prelevata e' diventata un deposito, ma la riga con il fatto per il gestionale non e' stata scritta nel database del WCS", StringComparison.Ordinal) &&
            description.EndsWith("il gestionale non riaprira' l'ordine, che va sistemato a mano", StringComparison.Ordinal)).WaitAsync(TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// Voce 45, primo giro di correzioni del compito 3: il cambio arriva mentre la consegna del prelievo
    /// e' in volo, ferma sulla conferma diretta al gestionale (<see cref="FakeWarehouse.ConfirmServedAssignmentsGate"/>):
    /// l'assegnazione 902 e la riga 8045 sono gia' copiate sulla missione, il GUID no. Il commit aspetta
    /// che la consegna finisca: il fatto accodato ha tutt'e tre le parti, la missione non tiene niente,
    /// e la conferma e «assegnazione servita» sono partite prima. Senza l'attesa il fatto partiva
    /// senza il GUID, e il deposito si teneva il GUID servito.
    /// </summary>
    [Fact]
    public async Task AChangeWhileThePickUpIsBeingConfirmedQueuesTheReopenAfterTheConfirmation()
    {
        var mission = await APickedExtractionOnP();
        await TheMissionServesQueueRow8045(mission);
        _warehouse.ConfirmServedAssignmentsGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MissionGateway.WaitingForThePickUpHandOffForTesting = _ => waiting.TrySetResult();

        var handOff = mission.TryPersistExtractionOutcomeBeforeUnload();
        await _warehouse.ConfirmServedAssignmentsAsked.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(902m, mission.ServedAssignmentId);
        Assert.Equal(Guid.Empty, mission.ServedAssignmentGuid);

        var change = MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid);
        await Task.WhenAny(change, waiting.Task).WaitAsync(TimeSpan.FromSeconds(30));
        _warehouse.ConfirmServedAssignmentsGate.SetResult();

        Assert.True(await handOff.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(MissionDestinationOutcome.Changed, (await change.WaitAsync(TimeSpan.FromSeconds(30))).Outcome);
        await ThePickUpWasReportedAndThenQueuedWhole(mission);
    }

    /// <summary>
    /// Voce 45, primo giro di correzioni del compito 3: l'UDC arriva sul ricollocatore P, agganciato al
    /// magazzino, e il lavoro di sfondo dell'arrivo (<c>PathMission.PositionWork</c>) deve ancora
    /// consegnare il prelievo: il lucchetto delle estrazioni lo tiene un altro flusso, qui la prova.
    /// Il cambio verso S8 arriva prima che la consegna copi qualcosa, e il commit la aspetta. Senza
    /// l'attesa la missione diventava un deposito senza niente da riaprire, e la consegna, partita dopo
    /// sul deposito, confermava l'assegnazione e la diceva servita: nessun fatto in coda.
    /// </summary>
    [Fact]
    public async Task AChangeBeforeThePickUpIsHandedOffWaitsForTheHandOffOfTheArrival()
    {
        var mission = await AMissionOnPInTheStorageScene(on: "I");
        mission.MissionType = MissionTypes.Extraction;
        mission.PickUpDoorCode = OldDoorCode;
        TheWarehouseAnswers(NewCellId, NewDoorCode);
        await TheMissionServesQueueRow8045(mission);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MissionGateway.WaitingForThePickUpHandOffForTesting = _ => waiting.TrySetResult();

        Task<MissionDestinationResult> change;
        Assert.True(await Navigator.SyncExtraction.WaitAsync(TimeSpan.FromSeconds(30)));
        try
        {
            mission.PositionItemId = _plant["P"].ItemGuid;
            Assert.Equal(0m, mission.ServedAssignmentId);

            change = MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid);
            await Task.WhenAny(change, waiting.Task).WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            Navigator.SyncExtraction.Release();
        }

        Assert.Equal(MissionDestinationOutcome.Changed, (await change.WaitAsync(TimeSpan.FromSeconds(30))).Outcome);
        await mission.PositionWork.WaitAsync(TimeSpan.FromSeconds(30));
        await ThePickUpWasReportedAndThenQueuedWhole(mission);
    }

    /// <summary>
    /// La riga della coda di estrazione del WCS che il prelievo della missione serve: l'assegnazione 902
    /// (GUID <see cref="ServedGuid"/>) e la riga di estrazione 8045, per l'UDC 42. La missione la tiene
    /// come la tiene un'estrazione della coda.
    /// </summary>
    private static async Task TheMissionServesQueueRow8045(PathMission mission)
    {
        await ORM.ExtractionQueue.UpsertAsync(new ExtractionQueueItem
        {
            LoadUnitId = LoadUnitId,
            AssignmentId = 902m,
            ExtractionRowId = 8045m,
            AssignmentGuid = ServedGuid,
            SourceItemId = Guid.NewGuid(),
            Processed = false
        });
        var queued = await ExtractionQueueQueries.ListAsync(processed: null, loadUnitId: LoadUnitId, take: 10);
        mission.ExtractionQueueId = Assert.Single(queued).Id;
        mission.GuidAssignmentLu = ServedGuid;
        mission.LuNoteAssignmentId = 902m;
    }

    /// <summary>
    /// Dopo il cambio e la consegna: il gestionale ha avuto la conferma e «assegnazione servita», il
    /// fatto «l'estrazione prelevata e' diventata un deposito» e' in coda con le tre parti (e non e'
    /// ancora partito), e la missione, un deposito, non tiene piu' niente del prelievo.
    /// </summary>
    private async Task ThePickUpWasReportedAndThenQueuedWhole(PathMission mission)
    {
        Assert.Contains(nameof(IWarehouse.ConfirmServedAssignmentsAsync), _warehouse.Calls);
        Assert.Contains(nameof(IWarehouse.ReportMissionServedAssignmentAsync), _warehouse.Calls);
        Assert.DoesNotContain(nameof(IWarehouse.ReportPickedExtractionBecameDepositAsync), _warehouse.Calls);

        var report = Assert.Single(await ORM.PickedExtractionReportOutbox.PendingAsync(10));
        Assert.Equal(902m, report.AssignmentId);
        Assert.Equal(ServedGuid, report.AssignmentGuid);
        Assert.Equal(8045m, report.ExtractionRowId);

        Assert.Equal(MissionTypes.Storing, mission.MissionType);
        Assert.Equal(0m, mission.ExtractionQueueId);
        Assert.Equal(Guid.Empty, mission.GuidAssignmentLu);
        Assert.Equal(0m, mission.ServedAssignmentId);
        Assert.Equal(Guid.Empty, mission.ServedAssignmentGuid);
        Assert.Equal(0m, mission.ServedExtractionRowId);
    }

    /// <summary>
    /// Decisioni 8 e 10 insieme: l'estrazione prelevata e mandata in S8, poi spostata su Q, che e'
    /// collegato a S8, prende subito la rotta Q->S8, senza chiedere niente al gestionale e senza
    /// restare in coda. Prima le due porte ne facevano un cambio cella, e lo spostamento la mandava in
    /// coda.
    /// </summary>
    [Fact]
    public async Task APickedExtractionSentToAStorageGetsItsRouteAtOnceWhenMoved()
    {
        var mission = await APickedExtractionOnP();
        Assert.Equal(MissionDestinationOutcome.Changed,
            (await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid)).Outcome);
        _warehouse.Calls.Clear();

        Assert.Equal(MissionMoveOutcome.Moved, MissionGateway.Move(mission.MissionId, _plant["Q"].ItemGuid));
        await mission.PositionWork;

        Assert.Equal(["Q", "S8"], RouteOf(mission));
        Assert.Null(RoutesToBuild.Instance[mission.MissionId]);
        Assert.Empty(_warehouse.Calls);
    }

    /// <summary>
    /// L'estrazione prelevata e mandata in S8, poi il riavvio del motore: la missione rinasce dalla sua
    /// riga senza porta di prelievo, come deposito dell'operatore verso la porta A8, e la ricostruzione
    /// della ricarica le da' la rotta P->S8 senza farne un cambio cella.
    /// </summary>
    [Fact]
    public async Task AfterARestartAPickedExtractionSentToAStorageIsStillADepositThere()
    {
        var mission = await APickedExtractionOnP();
        Assert.Equal(MissionDestinationOutcome.Changed,
            (await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid)).Outcome);

        // Il riavvio: le collezioni di esecuzione si svuotano, i dispositivi restano (PathDevices
        // non si svuota alla ricarica), e le missioni rinascono dalle righe.
        ActiveMissions.Instance.RemoveAll(_ => true);
        MissionSteps.Instance.RemoveAll(_ => true);
        RoutesToBuild.Instance.Items = new();
        Navigator.NavStatus = NavigatorStatusTypes.Recover;

        await ORM.DbInfo.RecoverFailure();

        var reloaded = ActiveMissions.Instance[mission.MissionId];
        Assert.NotNull(reloaded);
        Assert.NotSame(mission, reloaded);
        await reloaded.PositionWork;
        await reloaded.RouteWork.WaitAsync(TimeSpan.FromSeconds(30));
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        Assert.Null(reloaded.PickUpDoorCode);
        Assert.Equal(MissionTypes.Storing, reloaded.MissionType);
        Assert.True(reloaded.OperatorDestination);
        Assert.Equal(NewDoorCode, reloaded.StorageDoorCode);
        Assert.Equal(_plant["S8"].ItemGuid, reloaded.DestinationItemId);

        // La ricostruzione che fa la ricarica (ActiveMissions.Add, in un lavoro che non si puo'
        // attendere), rifatta qui.
        await reloaded.BuildPathToDestination();
        Assert.Equal(MissionTypes.Storing, reloaded.MissionType);
        Assert.Equal(["P", "S8"], RouteOf(reloaded));
    }

    /// <summary>
    /// Voce 45: il fatto accodato al cambio e' nel database del WCS, e un riavvio lo ritrova, mentre la
    /// missione rinasce dalla sua riga come deposito senza niente da segnalare (i tre campi vuoti):
    /// la ricarica non accoda un secondo fatto.
    /// </summary>
    [Fact]
    public async Task AfterARestartTheQueuedReopenIsStillThereAndTheMissionHasNothingLeftToReport()
    {
        var mission = await APickedExtractionOnP();
        mission.ServedAssignmentId = 902m;
        mission.ServedAssignmentGuid = ServedGuid;
        mission.ServedExtractionRowId = 8045m;
        Assert.Equal(MissionDestinationOutcome.Changed,
            (await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid)).Outcome);
        var queued = Assert.Single(await ORM.PickedExtractionReportOutbox.PendingAsync(10));

        ActiveMissions.Instance.RemoveAll(_ => true);
        MissionSteps.Instance.RemoveAll(_ => true);
        RoutesToBuild.Instance.Items = new();
        Navigator.NavStatus = NavigatorStatusTypes.Recover;

        await ORM.DbInfo.RecoverFailure();

        var reloaded = ActiveMissions.Instance[mission.MissionId];
        Assert.NotNull(reloaded);
        await reloaded.PositionWork;
        await reloaded.RouteWork.WaitAsync(TimeSpan.FromSeconds(30));
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        Assert.Equal(MissionTypes.Storing, reloaded.MissionType);
        Assert.Equal(0m, reloaded.ServedAssignmentId);
        Assert.Equal(Guid.Empty, reloaded.ServedAssignmentGuid);
        Assert.Equal(0m, reloaded.ServedExtractionRowId);
        var after = Assert.Single(await ORM.PickedExtractionReportOutbox.PendingAsync(10));
        Assert.Equal(queued.OperationId, after.OperationId);
        Assert.Equal(902m, after.AssignmentId);
    }

    /// <summary>
    /// <see cref="AMissionOnPInTheStorageScene"/> con un'estrazione: l'UDC 42 e' uscita dalla cella 7
    /// di S7 (porta di prelievo A7) ed e' sul ricollocatore P, verso X; il gestionale ha la cella 8 di
    /// S8 per lei.
    /// </summary>
    private async Task<PathMission> APickedExtractionOnP()
    {
        var mission = await AMissionOnPInTheStorageScene();
        mission.MissionType = MissionTypes.Extraction;
        mission.PickUpDoorCode = OldDoorCode;
        TheWarehouseAnswers(NewCellId, NewDoorCode);
        Assert.False(mission.LoadUnitIsInItsStorageCell());
        return mission;
    }

    // ---------------------------------------------------------------------------------------
    // La regola delle due porte guarda dov'e' l'UDC (voce 54, decisione 2)
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Voce 54, il caso UC256355 (decisione 2 del proprietario del 13/10): un'estrazione uscita dalla
    /// cella A8 di S8 sta sull'ingresso I, a monte del ricollocatore P, e ha la porta di deposito B8,
    /// dello stesso magazzino (la cella che un punto d'ingresso le ha prenotato). La ricostruzione della
    /// rotta non ne fa un cambio cella: l'UDC e' fuori dalla sua cella, e la missione diventa quello che
    /// e' fisicamente, un deposito verso S8 (porta di prelievo nulla, la porta B8 resta), con la rotta
    /// I->P->S8 che parte da dove l'UDC e'. Il prelievo non aveva servito niente: nessun fatto in coda,
    /// e nessuna chiamata al gestionale. Prima diventava un cambio cella con gli step S8->P e P->S8,
    /// che partono dal magazzino e non dalla rulliera: nessun dispositivo li eseguiva.
    /// </summary>
    [Fact]
    public async Task APickedExtractionOutOfItsCellWithTwoDoorsOfOneStorageBecomesADepositNotAChangeOfCell()
    {
        var mission = await APickedExtractionOnTheConveyorIWithTheDoorsA8AndB8();

        Assert.False(await mission.BuildPathToDestination());

        Assert.Equal(MissionTypes.Storing, mission.MissionType);
        Assert.Null(mission.PickUpDoorCode);
        Assert.Equal("B8", mission.StorageDoorCode);
        Assert.Equal(_plant["S8"].ItemGuid, mission.DestinationItemId);
        Assert.Equal(["I", "P", "S8"], RouteOf(mission));
        Assert.Equal(_plant["I"].ItemGuid, mission.MissionSteps.Single(s => s.ToNodeId == _plant["P"].ItemGuid).FromNodeId);
        Assert.Empty(await ORM.PickedExtractionReportOutbox.PendingAsync(10));
        Assert.Empty(_warehouse.Calls);
    }

    /// <summary>
    /// Voce 54, risposta 3 del proprietario del 13/10: la regola delle due porte che fa di
    /// un'estrazione gia' prelevata un deposito, fuori da un cambio dell'operatore, manda al gestionale
    /// il fatto della voce 45 come il commit dell'operatore: la stessa coda durevole, la stessa
    /// scrittura (il tipo, la porta di prelievo e i tre campi azzerati nella riga della missione), e il
    /// motivo che dice chi l'ha fatto. Durante la ricostruzione non parte niente: lo manda il dispatcher.
    /// </summary>
    [Fact]
    public async Task APickedExtractionThatServedSomethingQueuesTheReopenWhenTheDoorsMakeItADeposit()
    {
        var mission = await APickedExtractionOnTheConveyorIWithTheDoorsA8AndB8();
        mission.ServedAssignmentId = 902m;
        mission.ServedAssignmentGuid = ServedGuid;
        mission.ServedExtractionRowId = 8045m;

        Assert.False(await mission.BuildPathToDestination());

        Assert.Equal(MissionTypes.Storing, mission.MissionType);
        Assert.Null(mission.PickUpDoorCode);
        Assert.Equal(["I", "P", "S8"], RouteOf(mission));
        Assert.DoesNotContain(nameof(IWarehouse.ReportPickedExtractionBecameDepositAsync), _warehouse.Calls);

        var report = Assert.Single(await ORM.PickedExtractionReportOutbox.PendingAsync(10));
        Assert.Equal(mission.MissionId, report.MissionId);
        Assert.Equal(42m, report.LoadUnitId);
        Assert.Equal(902m, report.AssignmentId);
        Assert.Equal(ServedGuid, report.AssignmentGuid);
        Assert.Equal(8045m, report.ExtractionRowId);
        Assert.Equal("KazamControl.Process", report.UserId);
        Assert.Equal(
            "The route rebuild found the picked-up extraction out of its cell, bound for a storage (cell B8): it became a deposit",
            report.Reason);

        Assert.Equal(0m, mission.ServedAssignmentId);
        Assert.Equal(Guid.Empty, mission.ServedAssignmentGuid);
        Assert.Equal(0m, mission.ServedExtractionRowId);

        await using var context = _database.CreateContext();
        var row = context.Missions.Single(m => m.MissionId == mission.MissionId);
        Assert.Equal((short)MissionTypes.Storing, row.MissionType);
        Assert.Null(row.PickUpDoorCode);
        Assert.Equal(0m, row.ServedAssignmentId);
        Assert.Null(row.ServedAssignmentGuid);
        Assert.Equal(0m, row.ServedExtractionRowId);
    }

    /// <summary>
    /// Voce 54, risposta 3, con l'ordine della voce 45: l'UDC arriva sul ricollocatore P, agganciato al
    /// magazzino, e il lavoro di sfondo dell'arrivo deve ancora consegnare il prelievo (il lucchetto
    /// delle estrazioni lo tiene la prova). La ricostruzione che ne fa un deposito aspetta la consegna,
    /// come il commit dell'operatore: il fatto in coda ha le tre parti, e la conferma e «assegnazione
    /// servita» sono partite prima. Senza l'attesa la missione diventava un deposito senza niente da
    /// riaprire, e la consegna, partita dopo sul deposito, diceva servito l'ordine: nessun fatto in coda.
    /// </summary>
    [Fact]
    public async Task TheRebuildThatMakesADepositWaitsForTheHandOffOfThePickUp()
    {
        var mission = await APickedExtractionOnTheConveyorIWithTheDoorsA8AndB8();
        await TheMissionServesQueueRow8045(mission);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MissionGateway.WaitingForThePickUpHandOffForTesting = _ => waiting.TrySetResult();

        Task<bool> rebuild;
        Assert.True(await Navigator.SyncExtraction.WaitAsync(TimeSpan.FromSeconds(30)));
        try
        {
            mission.PositionItemId = _plant["P"].ItemGuid;
            Assert.Equal(0m, mission.ServedAssignmentId);

            rebuild = mission.BuildPathToDestination();
            await Task.WhenAny(rebuild, waiting.Task).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(MissionTypes.Extraction, mission.MissionType);
        }
        finally
        {
            Navigator.SyncExtraction.Release();
        }

        Assert.False(await rebuild.WaitAsync(TimeSpan.FromSeconds(30)));
        await mission.PositionWork.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(["P", "S8"], RouteOf(mission));
        await ThePickUpWasReportedAndThenQueuedWhole(mission);
    }

    /// <summary>
    /// Voce 54, decisione 2: la regola guarda dov'e' l'UDC, e un cambio cella gia' in corso, con l'UDC a
    /// bordo del suo ricollocatore P, resta un cambio cella (fra i due magazzini l'UDC la porta il
    /// ricollocatore, non e' un deposito); la rotta P->S8 e' quella di sempre.
    /// </summary>
    [Fact]
    public async Task AChangeOfCellOnItsRelocatorStaysAChangeOfCell()
    {
        var mission = await AMissionOnPInTheStorageScene();
        await S8HasASecondDoorB8OnCell10();
        mission.MissionType = MissionTypes.ChangeStoreLocation;
        mission.RelocateItemId = _plant["P"].ItemGuid;
        mission.SourceItemId = _plant["S8"].ItemGuid;
        mission.PickUpDoorCode = NewDoorCode;
        mission.StorageDoorCode = "B8";
        mission.DestinationItemId = _plant["S8"].ItemGuid;

        Assert.False(await mission.BuildPathToDestination());

        Assert.Equal(MissionTypes.ChangeStoreLocation, mission.MissionType);
        Assert.Equal(NewDoorCode, mission.PickUpDoorCode);
        Assert.Equal(["P", "S8"], RouteOf(mission));
        Assert.Empty(_warehouse.Calls);
    }

    /// <summary>
    /// Il caso UC256355 nella scena del magazzino: un'estrazione da S8 (porta di prelievo A8) gia' fuori
    /// dalla sua cella, sull'ingresso I a monte del ricollocatore P, verso S8 con la porta di deposito
    /// B8 (la seconda porta di S8, cella 10), senza rotta; il navigatore e' fermo.
    /// </summary>
    private async Task<PathMission> APickedExtractionOnTheConveyorIWithTheDoorsA8AndB8()
    {
        var mission = await AMissionOnPInTheStorageScene(on: "I");
        await S8HasASecondDoorB8OnCell10();
        mission.MissionType = MissionTypes.Extraction;
        mission.SourceItemId = _plant["S8"].ItemGuid;
        mission.PickUpDoorCode = NewDoorCode;
        mission.StorageDoorCode = "B8";
        mission.DestinationItemId = _plant["S8"].ItemGuid;
        Assert.False(mission.LoadUnitIsInItsStorageCell());
        Assert.Empty(mission.MissionSteps);
        return mission;
    }

    // ---------------------------------------------------------------------------------------
    // Scene
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// P collegato ad A, a X, a G (che porta a Y) e ad H (che porta a X); Z isolato. Su P una missione
    /// verso A con la rotta P->A, aggiunta in Recover; poi il navigatore e' fermo. Con
    /// <paramref name="positionInAGroup"/> P fa parte del gruppo GP.
    /// </summary>
    private async Task<(PathMission Mission, MissionStep OldStep)> AMissionOnPBoundForA(
        MissionTypes type = MissionTypes.MovingToItem, bool positionInAGroup = false)
    {
        foreach (var name in new[] { "P", "A", "X", "G", "Y", "H", "Z" }) _plant.Device(name);
        _plant.Link("P", "A");
        _plant.Link("P", "X");
        _plant.Link("P", "G");
        _plant.Link("G", "Y");
        _plant.Link("P", "H");
        _plant.Link("H", "X");

        if (positionInAGroup)
        {
            var group = _plant.Device("GP", DeviceTypes.GroupOfAutomationDevices);
            group.GroupItemId = 91;
            _plant["P"].BelongingGroupId = group.GroupItemId;
            Assert.Same(group, _plant["P"].BelongedGroup);
        }

        var mission = await MissionOn("P", type);
        mission.DestinationItemId = _plant["A"].ItemGuid;
        var oldStep = _plant.PlannedStep(mission, "P", "A");
        Navigator.NavStatus = NavigatorStatusTypes.Stop;

        Assert.Same(oldStep, Assert.Single(mission.MissionSteps));
        return (mission, oldStep);
    }

    /// <summary>
    /// P collegato a X, a Q e ai magazzini S7, S8 e S9, di cui e' il ricollocatore (cella collegata
    /// 7, con la modalita' di deposito data); Q collegato a S8; S5 magazzino isolato. Le porte A7,
    /// A8 e A9 sono le celle 7, 8 e 9, tutte del magazzino 1 del gestionale, con l'indirizzo di
    /// campo della 8. Su P una missione verso X con l'unita' di carico 42, senza rotta; il navigatore
    /// e' fermo. Con <paramref name="storagesInAGroup"/> S8 fa parte del gruppo di magazzini GS. Con
    /// <paramref name="on"/> diverso da P la missione sta su quel dispositivo, un ingresso a monte di P
    /// e collegato a lui, senza una cella collegata.
    /// With <paramref name="boundForX"/> false the mission has no destination: a deposit, whose cell the
    /// storing mode of its position looks for. Item 54 (decision 1 of the owner of 13/10): a storing
    /// mode no longer touches a mission bound for X, which is not a storage, so the tests of the
    /// configuration missions that a storing mode sends into S8 start from a mission with no destination.
    /// </summary>
    private async Task<PathMission> AMissionOnPInTheStorageScene(
        LookWhereAllocateLuTypes mode = LookWhereAllocateLuTypes.No, bool storagesInAGroup = false, string on = "P",
        bool boundForX = true)
    {
        var position = _plant.Device("P");
        foreach (var name in new[] { "X", "Q" }) _plant.Device(name);
        foreach (var name in new[] { "S5", "S7", "S8", "S9" }) _plant.Device(name, DeviceTypes.Storage);
        _plant.Link("P", "X");
        _plant.Link("P", "Q");
        _plant.Link("P", "S7");
        _plant.Link("P", "S8");
        _plant.Link("P", "S9");
        _plant.Link("Q", "S8");

        foreach (var name in new[] { "S7", "S8", "S9" }) _plant[name].RelocateItemId = position.ItemGuid;
        position.LinkedCellId = OldCellId;
        position.LookWhereAllocateLu = mode;
        Assert.True(position.IsLinkedToStorage);

        if (storagesInAGroup)
        {
            var group = _plant.Device("GS", DeviceTypes.GroupOfAutomationDevices);
            group.GroupItemId = 92;
            _plant["S8"].BelongingGroupId = group.GroupItemId;
            Assert.Same(group, _plant["S8"].BelongedGroup);
        }

        await PlantPersistence.SaveDoorMappingsAsync(_plant["S7"].ItemGuid,
            [new DoorMapping { ItemId = _plant["S7"].ItemGuid, DoorCode = OldDoorCode, CellId = OldCellId, Side = "A" }]);
        await PlantPersistence.SaveDoorMappingsAsync(_plant["S8"].ItemGuid,
            [new DoorMapping { ItemId = _plant["S8"].ItemGuid, DoorCode = NewDoorCode, CellId = NewCellId, Side = "A" }]);
        await PlantPersistence.SaveDoorMappingsAsync(_plant["S9"].ItemGuid,
            [new DoorMapping { ItemId = _plant["S9"].ItemGuid, DoorCode = OtherDoorCode, CellId = OtherCellId, Side = "A" }]);

        _warehouse.Cells[OldCellId] = Cell(OldCellId, OldDoorCode);
        _warehouse.Cells[NewCellId] = Cell(NewCellId, NewDoorCode);
        _warehouse.Cells[OtherCellId] = Cell(OtherCellId, OtherDoorCode);
        _warehouse.CellPartitionMaps[NewCellId] = new CellPartitionMap(NewCellId, "0011", "0012",
            AsiDoorB: string.Empty, SubitemDoorB: string.Empty, [new CellPartition("0014", -1)]);
        _warehouse.CellPartitionMaps[OtherCellId] = new CellPartitionMap(OtherCellId, "0021", "0022",
            AsiDoorB: string.Empty, SubitemDoorB: string.Empty, [new CellPartition("0024", -1)]);

        if (on != "P")
        {
            _plant.Device(on);
            _plant.Link(on, "P");
        }

        var mission = await MissionOn(on);
        if (boundForX)
            mission.DestinationItemId = _plant["X"].ItemGuid;
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        _warehouse.Calls.Clear();
        _warehouse.StorageRequests.Clear();
        return mission;
    }

    /// <summary>S8 ha una seconda porta, B8, sulla cella 10, con l'indirizzo di campo 0031.0032.</summary>
    private async Task S8HasASecondDoorB8OnCell10()
    {
        await PlantPersistence.SaveDoorMappingsAsync(_plant["S8"].ItemGuid,
        [
            new DoorMapping { ItemId = _plant["S8"].ItemGuid, DoorCode = NewDoorCode, CellId = NewCellId, Side = "A" },
            new DoorMapping { ItemId = _plant["S8"].ItemGuid, DoorCode = "B8", CellId = 10, Side = "A" }
        ]);
        _warehouse.Cells[10] = Cell(10, "B8");
        _warehouse.CellPartitionMaps[10] = new CellPartitionMap(10, "0031", "0032",
            AsiDoorB: string.Empty, SubitemDoorB: string.Empty, [new CellPartition("0034", -1)]);
    }

    /// <summary>La cella che il gestionale risponde per l'unita' di carico, con partizione 0013 e profondita' 150.</summary>
    private void TheWarehouseAnswers(decimal cellId, string doorCode) =>
        _warehouse.StorageReservations[LoadUnitId] = new StorageReservation(Guid.Empty,
            StorageReservationOutcome.CellFound, cellId, doorCode, Partition: "0013", StorageDepth: 150,
            NeedsCompaction: false);

    /// <summary>
    /// La missione va in S7 con la cella 7 (porta A7) che il gestionale le tiene, e la rotta P->S7,
    /// come la lascia il flusso del deposito; da un ingresso a monte di P, il primo step della rotta
    /// e' quello verso P.
    /// </summary>
    private MissionStep TheMissionIsBoundForS7WithCell7(PathMission mission)
    {
        mission.DestinationItemId = _plant["S7"].ItemGuid;
        mission.StorageDoorCode = OldDoorCode;
        var position = _plant.NameOf(mission.PositionItemId);
        var step = _plant.PlannedStep(mission, position, position == "P" ? "S7" : "P");
        _warehouse.StorageReservationsByMission[mission.MissionId] = new StorageReservation(mission.MissionId,
            StorageReservationOutcome.CellFound, OldCellId, OldDoorCode, Partition: "0003", StorageDepth: 150,
            NeedsCompaction: false);
        TheWarehouseAnswers(NewCellId, NewDoorCode);
        _warehouse.Calls.Clear();
        return step;
    }

    /// <summary>
    /// <see cref="TheMissionIsBoundForS7WithCell7"/>, con l'indirizzo di campo della cella 7
    /// (0001.0002.0003) e la profondita' 150 sulla missione, come li scrive il flusso del deposito.
    /// </summary>
    private MissionStep TheMissionHoldsCell7WithItsAddress(PathMission mission)
    {
        var step = TheMissionIsBoundForS7WithCell7(mission);
        mission.DestinationAsi = "0001";
        mission.DestinationSubitem = "0002";
        mission.DestinationPartition = "0003";
        mission.StorageDepth = 150;
        return step;
    }

    /// <summary>
    /// Alla seconda chiamata di <see cref="IWarehouse.ReserveStorageAsync"/> da adesso - la
    /// prenotazione, dopo la domanda senza prenotare - il gestionale cambia risposta o cade: il
    /// cambio arriva alla prenotazione, e quello che succede li' lo decide la prova.
    /// </summary>
    private void TheReservationAnswers(Action atTheReservation)
    {
        var calls = 0;
        _warehouse.Before = operation =>
        {
            if (operation == nameof(IWarehouse.ReserveStorageAsync) && ++calls == 2)
                atTheReservation();

            return Task.CompletedTask;
        };
    }

    /// <summary>
    /// La cella persa di una missione della configurazione, con la scena di <c>d7</c> §5.3: sulla
    /// posizione <paramref name="on"/> (P, o l'ingresso I a monte di P) la missione va in S7 con la
    /// cella 7, il cambio verso S8 e' rifiutato dopo la prenotazione (il gestionale non risponde), e la
    /// missione ha perso la cella, verso S7 e senza segno. Poi esce dalla coda, come la pesca il giro
    /// delle rotte prima di cercare (<c>BuildRoutePath</c>): se dopo la ricerca e' di nuovo in coda, ce
    /// l'ha rimessa il freno.
    /// </summary>
    private async Task<PathMission> AConfigurationMissionThatLostItsCellInS7(string on = "P")
    {
        var mission = await AMissionOnPInTheStorageScene(on: on);
        var oldStep = TheMissionHoldsCell7WithItsAddress(mission);
        TheReservationAnswers(() => throw new WarehouseUnavailableException(nameof(IWarehouse.ReserveStorageAsync)));
        Assert.Equal(MissionDestinationOutcome.WarehouseUnavailable,
            (await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid)).Outcome);
        AssertTheCellIsLostAndTheMissionIsBackWithTheNavigator(mission, oldStep);
        RoutesToBuild.Instance.Remove(mission.MissionId);
        return mission;
    }

    /// <summary>
    /// Il gestionale non ha una cella libera per la domanda del giro delle rotte nel magazzino della
    /// destinazione; alla richiesta dopo (la ricerca negli altri magazzini del ricollocatore) risponde
    /// quello che <paramref name="afterwards"/> gli fa rispondere, o ancora nessuna cella. Le richieste
    /// registrate ripartono da qui.
    /// </summary>
    private void TheStorageIsFullAndThenTheWarehouseAnswers(Action? afterwards = null)
    {
        _warehouse.StorageRequests.Clear();
        _warehouse.StorageReservations[LoadUnitId] = new StorageReservation(Guid.Empty,
            StorageReservationOutcome.NoFreeCells, 0, string.Empty, string.Empty, 0, NeedsCompaction: false);
        var calls = 0;
        _warehouse.Before = operation =>
        {
            if (operation == nameof(IWarehouse.ReserveStorageAsync) && ++calls == 2)
                afterwards?.Invoke();

            return Task.CompletedTask;
        };
    }

    /// <summary>
    /// Il gruppo G (gruppo 94) con i membri P1 (ordine 1, cella collegata 7) e P2 (ordine 2, cella
    /// collegata 8), ricollocatore dei magazzini S7 (porta A7, collegato a P1), S8 (porta A8, collegato
    /// a P2) e S9 (porta A9, non collegato a niente); la cella 7 e' del magazzino 1 del gestionale, la 8
    /// e la 9 del magazzino 2. L'ingresso I e' collegato a P1 e a P2. Sulla posizione
    /// <paramref name="on"/> una missione della configurazione verso S7 che ha perso la cella (senza
    /// porta, senza rotta, fuori dalla coda), e il navigatore fermo. Con
    /// <paramref name="groupLinkedCell"/> anche G ha una cella collegata, la 10 del magazzino 3. Il
    /// gestionale risponde a ogni prenotazione quello che <paramref name="answer"/> da' per il magazzino
    /// chiesto; le richieste registrate ripartono da qui.
    /// </summary>
    private async Task<PathMission> AConfigurationMissionUpstreamOfTheGroupThatLostItsCellInS7(
        Func<decimal, StorageReservation> answer, string on = "I", bool groupLinkedCell = false)
    {
        var group = _plant.Device("G", DeviceTypes.GroupOfAutomationDevices);
        group.GroupItemId = 94;
        var first = _plant.Device("P1");
        first.BelongingGroupId = group.GroupItemId;
        first.OrderNumberGroup = 1;
        first.LinkedCellId = OldCellId;
        var second = _plant.Device("P2");
        second.BelongingGroupId = group.GroupItemId;
        second.OrderNumberGroup = 2;
        second.LinkedCellId = NewCellId;
        if (groupLinkedCell) group.LinkedCellId = 10;
        Assert.Equal([first, second], group.BelongedItems);

        _plant.Device("I");
        foreach (var name in new[] { "S7", "S8", "S9" })
            _plant.Device(name, DeviceTypes.Storage).RelocateItemId = group.ItemGuid;
        _plant.Link("I", "P1");
        _plant.Link("I", "P2");
        _plant.Link("P1", "S7");
        _plant.Link("P2", "S8");

        await PlantPersistence.SaveDoorMappingsAsync(_plant["S7"].ItemGuid,
            [new DoorMapping { ItemId = _plant["S7"].ItemGuid, DoorCode = OldDoorCode, CellId = OldCellId, Side = "A" }]);
        await PlantPersistence.SaveDoorMappingsAsync(_plant["S8"].ItemGuid,
            [new DoorMapping { ItemId = _plant["S8"].ItemGuid, DoorCode = NewDoorCode, CellId = NewCellId, Side = "A" }]);
        await PlantPersistence.SaveDoorMappingsAsync(_plant["S9"].ItemGuid,
            [new DoorMapping { ItemId = _plant["S9"].ItemGuid, DoorCode = OtherDoorCode, CellId = OtherCellId, Side = "A" }]);

        _warehouse.Cells[OldCellId] = Cell(OldCellId, OldDoorCode);
        _warehouse.Cells[NewCellId] = Cell(NewCellId, NewDoorCode) with { WarehouseId = 2 };
        _warehouse.Cells[OtherCellId] = Cell(OtherCellId, OtherDoorCode) with { WarehouseId = 2 };
        _warehouse.Cells[10] = Cell(10, "LINK") with { WarehouseId = 3 };
        _warehouse.CellPartitionMaps[NewCellId] = new CellPartitionMap(NewCellId, "0011", "0012",
            AsiDoorB: string.Empty, SubitemDoorB: string.Empty, [new CellPartition("0014", -1)]);
        _warehouse.CellPartitionMaps[OtherCellId] = new CellPartitionMap(OtherCellId, "0021", "0022",
            AsiDoorB: string.Empty, SubitemDoorB: string.Empty, [new CellPartition("0024", -1)]);

        var mission = await MissionOn(on);
        mission.DestinationItemId = _plant["S7"].ItemGuid;
        mission.StorageCellLost = true;
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        RoutesToBuild.Instance.Remove(mission.MissionId);
        Assert.Empty(mission.MissionSteps);
        Assert.True(string.IsNullOrEmpty(mission.StorageDoorCode));

        _warehouse.Calls.Clear();
        _warehouse.StorageRequests.Clear();
        _warehouse.Before = operation =>
        {
            if (operation == nameof(IWarehouse.ReserveStorageAsync))
                _warehouse.StorageReservations[LoadUnitId] = answer(_warehouse.StorageRequests[^1].WarehouseId);

            return Task.CompletedTask;
        };
        return mission;
    }

    /// <summary>L'ingresso I ha il punto di scarto R, collegato a lui.</summary>
    private void TheEntranceHasTheRejectionPointR()
    {
        _plant.Device("R");
        _plant.Link("I", "R");
        _plant["I"].RejectItemDestinationAllocateLu = _plant["R"].ItemGuid;
    }

    /// <summary>La cella che il gestionale prenota per l'unita' di carico, con partizione 0013 e profondita' 150.</summary>
    private static StorageReservation Found(decimal cellId, string doorCode) =>
        new(Guid.Empty, StorageReservationOutcome.CellFound, cellId, doorCode, Partition: "0013", StorageDepth: 150,
            NeedsCompaction: false);

    /// <summary>Il gestionale non ha una cella libera.</summary>
    private static StorageReservation NoFreeCells() =>
        new(Guid.Empty, StorageReservationOutcome.NoFreeCells, 0, string.Empty, string.Empty, 0, NeedsCompaction: false);

    /// <summary>
    /// S11, un magazzino di cui P e' il ricollocatore, con la porta A11 sulla cella 11 del magazzino 1
    /// del gestionale e il suo indirizzo di campo, ma senza nessuna giunzione: da P non si raggiunge.
    /// </summary>
    private async Task S11IsAStorageOfPWithoutARouteFromP()
    {
        var storage = _plant.Device("S11", DeviceTypes.Storage);
        storage.RelocateItemId = _plant["P"].ItemGuid;
        await PlantPersistence.SaveDoorMappingsAsync(storage.ItemGuid,
            [new DoorMapping { ItemId = storage.ItemGuid, DoorCode = "A11", CellId = 11, Side = "A" }]);
        _warehouse.Cells[11] = Cell(11, "A11");
        _warehouse.CellPartitionMaps[11] = new CellPartitionMap(11, "0041", "0042",
            AsiDoorB: string.Empty, SubitemDoorB: string.Empty, [new CellPartition("0044", -1)]);
    }

    /// <summary>
    /// S12, un magazzino di cui e' il ricollocatore Q, non P, raggiungibile da P attraverso Q, con la
    /// porta A12 sulla cella 12 del magazzino 1 del gestionale e il suo indirizzo di campo.
    /// </summary>
    private async Task S12IsAStorageOfQ()
    {
        var storage = _plant.Device("S12", DeviceTypes.Storage);
        storage.RelocateItemId = _plant["Q"].ItemGuid;
        _plant.Link("Q", "S12");
        await PlantPersistence.SaveDoorMappingsAsync(storage.ItemGuid,
            [new DoorMapping { ItemId = storage.ItemGuid, DoorCode = "A12", CellId = 12, Side = "A" }]);
        _warehouse.Cells[12] = Cell(12, "A12");
        _warehouse.CellPartitionMaps[12] = new CellPartitionMap(12, "0051", "0052",
            AsiDoorB: string.Empty, SubitemDoorB: string.Empty, [new CellPartition("0054", -1)]);
    }

    /// <summary>
    /// I nomi delle proprieta' della missione scritte da adesso (<see cref="PathMission.PropertyChanged"/>,
    /// che parte solo quando il valore cambia davvero). Le scritture arrivano dal lavoro della ricerca,
    /// sul thread pool: la coda si legge dopo averlo aspettato.
    /// </summary>
    private static ConcurrentQueue<string> TheWritesOf(PathMission mission)
    {
        var written = new ConcurrentQueue<string>();
        mission.PropertyChanged += (_, args) => written.Enqueue(args.PropertyName ?? string.Empty);
        return written;
    }

    /// <summary>
    /// La cella persa (decisioni del proprietario dell'1/10): la missione va ancora verso S7, la
    /// destinazione di prima e senza segno, ma senza porta, indirizzo, profondita' e rotta, ed e' in
    /// coda al navigatore; il gestionale non le tiene niente, ne' la cella del cambio (restituita) ne'
    /// la 7 (lasciata dalla prenotazione).
    /// </summary>
    private void AssertTheCellIsLostAndTheMissionIsBackWithTheNavigator(PathMission mission, MissionStep oldStep)
    {
        Assert.False(_warehouse.StorageReservationsByMission.ContainsKey(mission.MissionId));
        Assert.Equal(_plant["S7"].ItemGuid, mission.DestinationItemId);
        Assert.False(mission.OperatorDestination);
        Assert.Null(mission.StorageDoorCode);
        Assert.Equal(string.Empty, mission.DestinationAsi);
        Assert.Equal(string.Empty, mission.DestinationSubitem);
        Assert.Equal(string.Empty, mission.DestinationPartition);
        Assert.Equal(0, mission.StorageDepth);
        Assert.Empty(mission.MissionSteps);
        Assert.DoesNotContain(oldStep, MissionSteps.Instance.List);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
        Assert.True(mission.StorageCellLost);
    }

    /// <summary>
    /// Dopo la cella persa, il giro delle rotte del navigatore (decisioni del proprietario dell'1/10):
    /// le prenota una cella nel magazzino della destinazione rimasta, S7, con il flusso del cambio -
    /// la domanda senza prenotare e poi la prenotazione - e la cella 7, di nuovo libera, torna sua con
    /// la porta A7, l'indirizzo 0001.0002, la partizione e la profondita' della prenotazione, e la
    /// rotta P->S7. La destinazione resta quella della configurazione: il cambio e' stato rifiutato, e
    /// il segno dell'operatore resta giu'.
    /// </summary>
    private async Task TheNavigatorReservesACellInS7Again(PathMission mission)
    {
        _warehouse.Before = null;
        _warehouse.StorageRequests.Clear();
        TheWarehouseAnswers(OldCellId, OldDoorCode);
        _warehouse.CellPartitionMaps[OldCellId] = new CellPartitionMap(OldCellId, "0001", "0002",
            AsiDoorB: string.Empty, SubitemDoorB: string.Empty, [new CellPartition("0003", -1)]);

        await TheRouteLoopLooksForTheCell(mission);

        Assert.Equal([false, true], _warehouse.StorageRequests.Select(request => request.AssignPosition));
        Assert.Equal(OldCellId, _warehouse.StorageReservationsByMission[mission.MissionId].CellId);
        Assert.Equal(OldDoorCode, mission.StorageDoorCode);
        Assert.Equal("0001", mission.DestinationAsi);
        Assert.Equal("0002", mission.DestinationSubitem);
        Assert.Equal("0013", mission.DestinationPartition);
        Assert.Equal(150, mission.StorageDepth);
        Assert.Equal(_plant["S7"].ItemGuid, mission.DestinationItemId);
        Assert.False(mission.OperatorDestination);
        Assert.False(mission.StorageCellLost);
        Assert.Equal(["P", "S7"], RouteOf(mission));
    }

    /// <summary>
    /// Dopo la cella persa con la missione spostata su Q, il giro delle rotte del navigatore: da Q
    /// non c'e' percorso verso S7, e la domanda al gestionale non parte (la sonda viene prima). La
    /// missione resta senza porta e senza rotta, con la cella persa, per il giro dopo.
    /// </summary>
    private async Task FromQTheNavigatorFindsNoRouteToS7AndAsksNothing(PathMission mission)
    {
        _warehouse.Before = null;
        _warehouse.Calls.Clear();
        TheWarehouseAnswers(OldCellId, OldDoorCode);

        await TheRouteLoopLooksForTheCell(mission);

        Assert.DoesNotContain(nameof(IWarehouse.ReserveStorageAsync), _warehouse.Calls);
        Assert.False(_warehouse.StorageReservationsByMission.ContainsKey(mission.MissionId));
        Assert.Null(mission.StorageDoorCode);
        Assert.Empty(mission.MissionSteps);
        Assert.Equal(_plant["S7"].ItemGuid, mission.DestinationItemId);
        Assert.False(mission.OperatorDestination);
        Assert.True(mission.StorageCellLost);
    }

    /// <summary>S6, magazzino del gruppo GS con la porta A6 sulla cella 6, che da P non si raggiunge.</summary>
    private async Task S6IsAStorageOfGSWithCell6()
    {
        var unreachable = _plant.Device("S6", DeviceTypes.Storage);
        unreachable.BelongingGroupId = _plant["GS"].GroupItemId;
        Assert.Same(_plant["GS"], unreachable.BelongedGroup);
        await PlantPersistence.SaveDoorMappingsAsync(unreachable.ItemGuid,
            [new DoorMapping { ItemId = unreachable.ItemGuid, DoorCode = "A6", CellId = 6, Side = "A" }]);
        _warehouse.Cells[6] = Cell(6, "A6");
    }

    /// <summary>
    /// La scena del magazzino, e l'operatore manda la missione in S8: la cella 8 (porta A8) prenotata,
    /// la rotta P->S8, il segno. Il registro delle chiamate al gestionale riparte da qui.
    /// </summary>
    private async Task<PathMission> AMissionTheOperatorSentToS8()
    {
        var mission = await AMissionOnPInTheStorageScene();
        TheWarehouseAnswers(NewCellId, NewDoorCode);
        Assert.Equal(MissionDestinationOutcome.Changed,
            (await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid)).Outcome);
        Assert.Equal(NewCellId, _warehouse.StorageReservationsByMission[mission.MissionId].CellId);
        _warehouse.Calls.Clear();
        _warehouse.StorageRequests.Clear();
        return mission;
    }

    /// <summary>
    /// <see cref="AMissionTheOperatorSentToS8"/>, e P registra la quota dal campo: il deposito in S8,
    /// con l'indirizzo della cella 8, e' il prossimo passo di P. Fuori dalla coda delle rotte, come
    /// la lascia il cambio.
    /// </summary>
    private async Task<PathMission> AnOperatorsMissionOnPReadyToDepositInS8()
    {
        var mission = await AMissionTheOperatorSentToS8();
        _plant["P"].RecordStorageLength = RecordStorageTypes.FromPlc;
        Assert.NotNull(_plant["P"].StorageManager);
        Assert.Null(RoutesToBuild.Instance[mission.MissionId]);
        return mission;
    }

    /// <summary>
    /// La scena del magazzino con P nella modalita' di deposito (le sue celle collegate), e il giro
    /// delle rotte cerca la cella della missione: il gestionale le prenota la cella 8 di S8 (porta A8)
    /// e la missione va in S8, con l'indirizzo della cella 8, la rotta P->S8, senza il segno
    /// dell'operatore e con il segno della cella sua. P registra la quota dal campo: il deposito in S8
    /// e' il suo prossimo passo. La missione non e' in coda, e il registro delle chiamate al gestionale
    /// riparte da qui.
    /// </summary>
    private async Task<(PathMission Mission, MissionStep Step)> AConfigurationMissionOnPWithCell8ReservedByTheNavigator()
    {
        var mission = await AMissionOnPInTheStorageScene(LookWhereAllocateLuTypes.BuildRouteToStore, boundForX: false);
        TheWarehouseAnswers(NewCellId, NewDoorCode);

        await TheRouteLoopLooksForTheCell(mission);

        Assert.Equal(NewCellId, _warehouse.StorageReservationsByMission[mission.MissionId].CellId);
        Assert.Equal(NewDoorCode, mission.StorageDoorCode);
        Assert.Equal("0011", mission.DestinationAsi);
        Assert.Equal(_plant["S8"].ItemGuid, mission.DestinationItemId);
        Assert.False(mission.OperatorDestination);
        Assert.True(mission.StorageCellReserved);
        Assert.Equal(["P", "S8"], RouteOf(mission));
        Assert.Null(RoutesToBuild.Instance[mission.MissionId]);

        _plant["P"].RecordStorageLength = RecordStorageTypes.FromPlc;
        _warehouse.Calls.Clear();
        _warehouse.StorageRequests.Clear();
        return (mission, Assert.Single(mission.MissionSteps));
    }

    /// <summary>
    /// Il giro delle rotte pesca la missione e avvia la ricerca della sua cella (<c>StartCellSearch</c>,
    /// come la avvia <c>BuildRoutePath</c>), e la prova la aspetta, con un tetto che fa fallire invece
    /// di appendere.
    /// </summary>
    private static async Task TheRouteLoopLooksForTheCell(PathMission mission)
    {
        Assert.NotNull(mission.PositionItem);
        StartCellSearch(mission, mission.PositionItem);

        if (Navigator.CellSearchInFlight(mission.MissionId) is { } search)
            await search.WaitAsync(TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// La missione va in S7 con la cella 7 e la rotta P->S7 (<see cref="TheMissionIsBoundForS7WithCell7"/>),
    /// e P registra la quota dal campo, con l'indirizzo della cella 7: il deposito, avviato, parte.
    /// </summary>
    private async Task<PathMission> AMissionOnPReadyToDepositInS7()
    {
        var mission = await AMissionOnPInTheStorageScene();
        TheMissionIsBoundForS7WithCell7(mission);
        _plant["P"].RecordStorageLength = RecordStorageTypes.FromPlc;
        _warehouse.CellPartitionMaps[OldCellId] = new CellPartitionMap(OldCellId, "0001", "0002",
            AsiDoorB: string.Empty, SubitemDoorB: string.Empty, [new CellPartition("0003", -1)]);
        return mission;
    }

    private void AssertStillBoundForS7WithCell7(PathMission mission, MissionStep oldStep)
    {
        Assert.Equal(OldCellId, _warehouse.StorageReservationsByMission[mission.MissionId].CellId);
        Assert.Empty(_warehouse.ReleasedStorageReservations);
        Assert.Equal(_plant["S7"].ItemGuid, mission.DestinationItemId);
        Assert.Equal(OldDoorCode, mission.StorageDoorCode);
        Assert.False(mission.OperatorDestination);
        Assert.Same(oldStep, Assert.Single(mission.MissionSteps));
    }

    private void AssertStillBoundForA(PathMission mission, MissionStep oldStep,
        MissionTypes type = MissionTypes.MovingToItem, string? gateway = null)
    {
        Assert.Equal(_plant["A"].ItemGuid, mission.DestinationItemId);
        Assert.False(mission.OperatorDestination);
        Assert.Same(oldStep, Assert.Single(mission.MissionSteps));
        Assert.Equal(type, mission.MissionType);
        Assert.Equal(gateway == null ? Guid.Empty : _plant[gateway].ItemGuid, mission.Gateway);
    }

    /// <summary>
    /// La rotta della missione come nomi, dalla posizione seguendo gli step: gli step stanno in una
    /// collezione senza ordine, e la catena dice anche che sono una rotta sola.
    /// </summary>
    private string[] RouteOf(PathMission mission)
    {
        var steps = mission.MissionSteps;
        var names = new List<string> { _plant.NameOf(mission.PositionItemId) };
        var at = mission.PositionItemId;

        while (steps.FirstOrDefault(s => s.FromNodeId == at) is { } next)
        {
            names.Add(_plant.NameOf(next.ToNodeId));
            at = next.ToNodeId ?? Guid.Empty;
            steps.Remove(next);
        }

        Assert.Empty(steps);
        return [.. names];
    }

    /// <summary>
    /// Una missione a bordo di <paramref name="position"/>, aggiunta in Recover (in Stop
    /// <c>ActiveMissions.Add</c> esce subito), con il primo posizionamento gia' finito.
    /// </summary>
    private async Task<PathMission> MissionOn(string position, MissionTypes type = MissionTypes.MovingToItem)
    {
        Assert.Equal(NavigatorStatusTypes.Recover, Navigator.NavStatus);

        var mission = new PathMission
        {
            PositionItemId = _plant[position].ItemGuid,
            LuId = LoadUnitId,
            MissionType = type,
            OperationType = OperationTypes.Outbound
        };
        ActiveMissions.Instance.Add(mission, buildRoute: false);
        Assert.Same(mission, _plant[position].LoadedLu);
        await mission.PositionWork;
        return mission;
    }

    /// <summary>La ricerca della cella del giro delle rotte, come la avvia <c>BuildRoutePath</c>.</summary>
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
