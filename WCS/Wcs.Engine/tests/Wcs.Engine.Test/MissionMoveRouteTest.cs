using System.Reflection;
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
/// La rotta dello spostamento (<see cref="MissionGateway.Move"/>, compito 4 del piano delle
/// missioni): dove il navigatore andrebbe dritto alla destinazione la rotta nuova si costruisce
/// subito, anche a motore fermo; altrove la missione va in coda come prima. In Recover lo spostamento
/// e' rifiutato.
/// </summary>
/// <remarks>
/// <para>
/// Il navigatore sta in <c>Stop</c>, e in <c>Run</c> dove conta il risveglio, mai in
/// <c>Recover</c>: in Recover <c>PathBuilder</c> salta i filtri e il lavoro di sfondo del setter
/// della posizione salta il gestionale, cioe' i rami da provare. L'eccezione e' la prova di Recover
/// (<see cref="InRecoverTheMoveIsRefusedAndNothingChanges"/>), che riguarda proprio quello stato. Le
/// missioni si aggiungono in Recover perche' <c>ActiveMissions.Add</c> esce subito in Stop, poi si
/// passa allo stato della prova.
/// Il giro delle rotte del navigatore nelle prove non gira da solo: una missione in coda ci resta, e
/// dove serve la prova ne fa un giro (<c>Navigator.BuildRoutePath</c>).
/// </para>
/// <para>
/// L'impianto: F, la posizione di partenza, collegato a N, e N a D, la destinazione; T collegato a
/// D e a Y; U isolato. Il database del WCS e' in memoria, il gestionale e' quello finto. Nessuna
/// attesa a tempo: si attende il lavoro di sfondo del setter (<c>PathMission.PositionWork</c>), e il
/// risveglio in Run dal suo annuncio, con un tetto che fa fallire invece di appendere.
/// </para>
/// </remarks>
public sealed class MissionMoveRouteTest : IDisposable
{
    private const decimal LoadUnit = 4242;
    private const decimal StorageCell = 620;
    private const string StorageDoor = "S01A";

    /// <summary>
    /// Gli eventi del motore, in ordine di arrivo, con i due gettoni del dispositivo letti al momento
    /// dell'annuncio, come fa l'host quando costruisce il DTO. Arrivano anche dal thread pool, quindi
    /// si scrive sotto lock.
    /// </summary>
    private sealed class Recorder : IEngineEventSink
    {
        private readonly Lock _lock = new();
        private readonly List<(string Kind, Guid Id, Guid RunningStepId, Guid WaitingStepId)> _events = [];
        private readonly List<(Func<bool> Condition, TaskCompletionSource Reached)> _waiters = [];

        /// <summary>Chiamato sul thread dell'annuncio di un dispositivo, prima di registrarlo.</summary>
        public Action<AutomationDevice>? BeforeItem { get; set; }

        public IReadOnlyList<(string Kind, Guid Id, Guid RunningStepId, Guid WaitingStepId)> Events
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
            BeforeItem?.Invoke(device);

            (Func<bool> Condition, TaskCompletionSource Reached)[] waiters;
            lock (_lock)
            {
                _events.Add(("item", device.ItemGuid, device.RunningStepId, device.WaitingStepId));
                waiters = [.. _waiters];
            }

            foreach (var (condition, reached) in waiters)
                if (condition()) reached.TrySetResult();
        }

        public void MissionChanged(PathMission mission, EngineMissionChange change)
        {
            lock (_lock) _events.Add(($"mission:{change}", mission.MissionId, Guid.Empty, Guid.Empty));
        }

        public void NavigatorStatusChanged() { }
        public void ExtractionQueueChanged(ExtractionQueueItem item, EngineQueueChange change) { }
        public void CommandChanged(CommandRecord command) { }
    }

    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly RoutingTestPlant _plant = new();
    private readonly FakeWarehouse _warehouse = new();
    private readonly Recorder _recorder = new();

    public MissionMoveRouteTest()
    {
        Navigator.Store = _database;
        Navigator.Warehouse = _warehouse;
        RoutesToBuild.Instance.Items = new();
    }

    public void Dispose()
    {
        Navigator.EventSink = null;
        RoutesToBuild.Instance.Items = new();
        _plant.Dispose();
        Navigator.Warehouse = _previousWarehouse;
        Navigator.Store = null;
        _database.Dispose();
    }

    // ---------------------------------------------------------------------------------------
    // Dove il navigatore va dritto: la rotta subito
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Il caso della decisione 2: T non ha modalita' di deposito ne' destinazione fissa, e da li' la
    /// rotta verso D c'e' appena lo spostamento risponde, a motore fermo come in marcia; la missione
    /// non e' in coda, la rotta vecchia non c'e' piu', la missione si annuncia con i suoi step nuovi,
    /// e il posto delle ricerche che lo spostamento ha preso per costruirla e' restituito.
    /// </summary>
    [Theory]
    [InlineData(NavigatorStatusTypes.Stop)]
    [InlineData(NavigatorStatusTypes.Run)]
    public async Task FromADeviceThatGoesStraightTheNewRouteIsThereAtOnce(NavigatorStatusTypes status)
    {
        var mission = await AMissionOnFBoundForD();
        var oldStep = _plant.PlannedStep(mission, "F", "N");
        TheEngineIs(status);
        Navigator.EventSink = _recorder;

        Assert.Equal(MissionMoveOutcome.Moved, MissionGateway.Move(mission.MissionId, _plant["T"].ItemGuid));

        Assert.Equal(["T", "D"], _plant.Names(mission.MissionSteps));
        Assert.DoesNotContain(oldStep, MissionSteps.Instance.List);
        Assert.Null(RoutesToBuild.Instance[mission.MissionId]);
        Assert.Equal(_plant["D"].ItemGuid, mission.DestinationItemId);
        Assert.Contains(("mission:StepChanged", mission.MissionId, Guid.Empty, Guid.Empty), _recorder.Events);
        Assert.Null(Navigator.CellSearchInFlight(mission.MissionId));
        await mission.PositionWork;
    }

    /// <summary>
    /// Risposta 4 del proprietario dell'8/10 (al posto della decisione 11 del 2/10): in Recover il
    /// motore sta ancora caricando le missioni, e lo spostamento e' rifiutato senza toccare niente.
    /// Anche verso T, da dove in Stop e in Run la rotta T->D c'e' subito
    /// (<see cref="FromADeviceThatGoesStraightTheNewRouteIsThereAtOnce"/>): la missione resta su F con
    /// la sua rotta F->N, F la porta ancora e T no, la coda delle rotte e' com'era (vuota), nessun
    /// annuncio, nessun posto delle ricerche preso, e il fallimento che il giro le aveva contato su F
    /// resta (un secondo fallimento conta 2). Il wcs.db resta senza riga: in Recover le scritture dei
    /// setter non partono comunque, quindi quella asserzione da sola non distingue. E' l'eccezione
    /// voluta alla regola della classe: la prova riguarda proprio Recover.
    /// </summary>
    [Fact]
    public async Task InRecoverTheMoveIsRefusedAndNothingChanges()
    {
        var mission = await AMissionOnFBoundForD();
        var oldStep = _plant.PlannedStep(mission, "F", "N");
        Assert.Equal(1, Navigator.RoutePathRetries.RegisterFailure(mission.MissionId));
        TheEngineIs(NavigatorStatusTypes.Recover);
        Navigator.EventSink = _recorder;

        Assert.Equal(MissionMoveOutcome.EngineRecovering, MissionGateway.Move(mission.MissionId, _plant["T"].ItemGuid));
        await mission.PositionWork;

        Assert.Equal(_plant["F"].ItemGuid, mission.PositionItemId);
        Assert.Same(mission, _plant["F"].LoadedLu);
        Assert.Null(_plant["T"].LoadedLu);
        Assert.Same(oldStep, Assert.Single(mission.MissionSteps));
        Assert.Contains(oldStep, MissionSteps.Instance.List);
        Assert.Null(RoutesToBuild.Instance[mission.MissionId]);
        Assert.Equal(_plant["D"].ItemGuid, mission.DestinationItemId);
        Assert.Empty(_recorder.Events);
        Assert.Null(Navigator.CellSearchInFlight(mission.MissionId));
        using (var context = _database.CreateContext())
            Assert.Empty(context.Missions.Where(row => row.MissionId == mission.MissionId));
        Assert.Equal(2, Navigator.RoutePathRetries.RegisterFailure(mission.MissionId));
        Navigator.RoutePathRetries.Forget(mission.MissionId);
    }

    /// <summary>
    /// La destinazione scelta dall'operatore vince sulla configurazione (decisione 1), e da' la
    /// rotta subito anche da un dispositivo con una destinazione fissa o con una modalita' di
    /// deposito: verso D, la destinazione non cambia, il segno resta, e il gestionale non si chiama.
    /// </summary>
    [Theory]
    [InlineData("fixed destination", NavigatorStatusTypes.Stop)]
    [InlineData("store mode", NavigatorStatusTypes.Stop)]
    [InlineData("fixed destination", NavigatorStatusTypes.Run)]
    [InlineData("store mode", NavigatorStatusTypes.Run)]
    public async Task TheOperatorsDestinationIsBuiltAtOnceAlsoWhereTheDeviceIsConfiguredOtherwise(string configuration,
        NavigatorStatusTypes status)
    {
        var mission = await AMissionOnFBoundForD();
        Assert.True(mission.SetOperatorDestination(_plant["D"].ItemGuid));
        TIsConfiguredWith(configuration);
        TheEngineIs(status);

        Assert.Equal(MissionMoveOutcome.Moved, MissionGateway.Move(mission.MissionId, _plant["T"].ItemGuid));

        Assert.Equal(["T", "D"], _plant.Names(mission.MissionSteps));
        Assert.Null(RoutesToBuild.Instance[mission.MissionId]);
        Assert.Equal(_plant["D"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.OperatorDestination);
        await mission.PositionWork;
        Assert.Empty(_warehouse.Calls);
    }

    /// <summary>
    /// In marcia la rotta nuova parte da sola: il dispositivo di posizione si sveglia, come dopo
    /// una rotta costruita dal giro del navigatore, e prende lo step T->D con il posto su D.
    /// </summary>
    [Fact]
    public async Task InRunThePositionDeviceSetsOffByItself()
    {
        var mission = await AMissionOnFBoundForD();

        // Il passaggio in Run sveglia i dispositivi con AutoExecuteStep: T si accende dopo.
        TheEngineIs(NavigatorStatusTypes.Run);
        _plant["T"].AutoExecuteStep = true;
        Navigator.EventSink = _recorder;

        Assert.Equal(MissionMoveOutcome.Moved, MissionGateway.Move(mission.MissionId, _plant["T"].ItemGuid));

        var step = Assert.Single(mission.MissionSteps);
        Assert.Equal(["T", "D"], _plant.Names([step]));
        await _recorder.Until(() => _plant["T"].RunningStepId == step.StepId).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(step.StepId, _plant["D"].WaitingStepId);
        await mission.PositionWork;
    }

    // ---------------------------------------------------------------------------------------
    // La missione che era gia' in coda
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// La missione era gia' in coda per la rotta, come ci va quando si ferma su un dispositivo, e
    /// anche due volte (chi accoda non sempre guarda se c'e' gia': <c>HandleNoStepToDo</c>). Con la
    /// rotta costruita subito ne esce del tutto, non solo dalla cache della coda: la richiesta rimasta
    /// chiedeva la rotta che lo spostamento ha appena costruito, e il giro, ripescandola piu' avanti
    /// con la missione ferma su un dispositivo di passaggio, farebbe ripartire da li' la ricerca
    /// secondo la sua modalita' di deposito.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task AMissionAlreadyQueuedLeavesTheQueueWhenTheRouteIsBuiltAtOnce(int timesQueued)
    {
        var mission = await AMissionOnFBoundForD();
        for (var i = 0; i < timesQueued; i++) RoutesToBuild.Instance.Enqueue(mission);
        TheEngineIs(NavigatorStatusTypes.Stop);

        Assert.Equal(MissionMoveOutcome.Moved, MissionGateway.Move(mission.MissionId, _plant["T"].ItemGuid));

        Assert.Equal(["T", "D"], _plant.Names(mission.MissionSteps));
        Assert.Null(RoutesToBuild.Instance[mission.MissionId]);
        Assert.DoesNotContain(RoutesToBuild.Instance.Items, queued => queued?.MissionId == mission.MissionId);
        await mission.PositionWork;
    }

    /// <summary>
    /// La missione e' ferma su U, da dove un percorso verso D non c'e': il giro delle rotte l'ha
    /// cercata, non l'ha costruita e l'ha rimessa in coda con un fallimento contato nel freno dei
    /// ritentativi. Spostata su T ha la rotta subito, esce dalla coda, e il freno dimentica i
    /// fallimenti di U, come quando il giro la trova costruibile: il prossimo conta da uno.
    /// </summary>
    [Fact]
    public async Task AMissionTheRouteLoopCouldNotBuildLeavesTheQueueAndItsRetriesWhenTheRouteIsBuiltAtOnce()
    {
        var mission = await AMissionBoundForD("U");
        Assert.Null(RoutesToBuild.Instance[mission.MissionId]);

        // Un giro solo deve pescare questa missione: la coda e' un singleton, e il lavoro di sfondo
        // lasciato da altre prove ci puo' accodare missioni anche dopo il costruttore. Si svuota qui,
        // subito prima di accodare.
        RoutesToBuild.Instance.Items = new();
        RoutesToBuild.Instance.Enqueue(mission);
        TheEngineIs(NavigatorStatusTypes.Run);

        await OneTurnOfTheRouteLoop().WaitAsync(TimeSpan.FromSeconds(10));
        if (Navigator.CellSearchInFlight(mission.MissionId) is { } search)
            await search.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Empty(mission.MissionSteps);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
        // Due: quello del giro, e questo.
        Assert.Equal(2, Navigator.RoutePathRetries.RegisterFailure(mission.MissionId));

        Assert.Equal(MissionMoveOutcome.Moved, MissionGateway.Move(mission.MissionId, _plant["T"].ItemGuid));

        Assert.Equal(["T", "D"], _plant.Names(mission.MissionSteps));
        Assert.Null(RoutesToBuild.Instance[mission.MissionId]);
        Assert.DoesNotContain(RoutesToBuild.Instance.Items, queued => queued?.MissionId == mission.MissionId);
        Assert.Equal(1, Navigator.RoutePathRetries.RegisterFailure(mission.MissionId));
        Navigator.RoutePathRetries.Forget(mission.MissionId);
        await mission.PositionWork;
    }

    // ---------------------------------------------------------------------------------------
    // Dove il navigatore non va dritto: in coda come prima
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// La prova che resta verde: da un dispositivo con una destinazione fissa o con una modalita' di
    /// deposito il navigatore riscrive la destinazione o chiede una cella al gestionale, e lo
    /// spostamento lascia la missione in coda come prima, senza rotta, verso D, e senza chiamare il
    /// gestionale.
    /// </summary>
    /// <remarks>
    /// Item 54 (decision 1 and answer 8 of the owner of 13/10): a storing mode no longer touches a
    /// mission bound for a device that is not a storage, and from there the navigator goes straight
    /// (<see cref="AStoringModeMeetingAMissionBoundForADeviceThatIsNotAStorageGivesTheRouteAtOnce"/>).
    /// With a storing mode the mission is queued as before only when bound for a storage: in those rows
    /// D is one. Until item 54 every row had D as an automation device.
    /// </remarks>
    [Theory]
    [InlineData("fixed destination", NavigatorStatusTypes.Stop)]
    [InlineData("second fixed destination", NavigatorStatusTypes.Stop)]
    [InlineData("check only", NavigatorStatusTypes.Stop)]
    [InlineData("store mode", NavigatorStatusTypes.Stop)]
    [InlineData("store mode by load unit type", NavigatorStatusTypes.Stop)]
    [InlineData("store mode forcing", NavigatorStatusTypes.Stop)]
    [InlineData("different store location", NavigatorStatusTypes.Stop)]
    [InlineData("fixed destination", NavigatorStatusTypes.Run)]
    [InlineData("store mode", NavigatorStatusTypes.Run)]
    public async Task WhereTheNavigatorDoesNotGoStraightTheMissionIsQueuedAsToday(string configuration,
        NavigatorStatusTypes status)
    {
        var mission = await AMissionOnFBoundForD(destinationType: configuration.Contains("fixed destination")
            ? DeviceTypes.Automation
            : DeviceTypes.Storage);
        var oldStep = _plant.PlannedStep(mission, "F", "N");
        TIsConfiguredWith(configuration);
        TheEngineIs(status);

        Assert.Equal(MissionMoveOutcome.Moved, MissionGateway.Move(mission.MissionId, _plant["T"].ItemGuid));

        Assert.Empty(mission.MissionSteps);
        Assert.DoesNotContain(oldStep, MissionSteps.Instance.List);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
        Assert.Equal(_plant["D"].ItemGuid, mission.DestinationItemId);
        await mission.PositionWork;
        Assert.Empty(_warehouse.Calls);
    }

    /// <summary>
    /// Item 54 (decision 1 and answer 8 of the owner of 13/10): a storing mode of T (1-5, forcing
    /// included) does not touch a mission bound for D, which is not a storage, and the navigator goes
    /// straight from there (<c>Navigator.GoesStraightToTheDestination</c>): the move builds the route
    /// T->D at once, with the engine stopped as in Run, the mission leaves the queue, keeps D, and the
    /// warehouse is not called. Until item 54 the mission was queued, and the route loop in Run asked
    /// the warehouse for a cell and rewrote the destination.
    /// </summary>
    [Theory]
    [InlineData("check only", NavigatorStatusTypes.Stop)]
    [InlineData("store mode", NavigatorStatusTypes.Stop)]
    [InlineData("store mode by load unit type", NavigatorStatusTypes.Stop)]
    [InlineData("store mode forcing", NavigatorStatusTypes.Stop)]
    [InlineData("different store location", NavigatorStatusTypes.Stop)]
    [InlineData("store mode", NavigatorStatusTypes.Run)]
    public async Task AStoringModeMeetingAMissionBoundForADeviceThatIsNotAStorageGivesTheRouteAtOnce(string configuration,
        NavigatorStatusTypes status)
    {
        var mission = await AMissionOnFBoundForD();
        var oldStep = _plant.PlannedStep(mission, "F", "N");
        TIsConfiguredWith(configuration);
        TheEngineIs(status);

        Assert.Equal(MissionMoveOutcome.Moved, MissionGateway.Move(mission.MissionId, _plant["T"].ItemGuid));

        Assert.Equal(["T", "D"], _plant.Names(mission.MissionSteps));
        Assert.DoesNotContain(oldStep, MissionSteps.Instance.List);
        Assert.Null(RoutesToBuild.Instance[mission.MissionId]);
        Assert.Equal(_plant["D"].ItemGuid, mission.DestinationItemId);
        Assert.False(mission.OperatorDestination);
        await mission.PositionWork;
        Assert.Empty(_warehouse.Calls);
    }

    /// <summary>
    /// Le missioni che il navigatore costruisce a modo suo vanno in coda come prima, e lo
    /// spostamento non fa niente di quello che farebbe la loro costruzione: la deframmentazione, da
    /// un dispositivo che non e' la sua cella, direbbe al gestionale che la missione lascia la sua
    /// assegnazione non servita e ne toglierebbe la riga; il cambio cella senza le sue due porte
    /// costruirebbe i suoi step dalla sorgente (item 54: with its two doors and out of its cell it has the
    /// route at once as a deposit, <see cref="RedirectedChangeOfCellTest.AChangeOfCellMovedOutOfItsCellHasItsRouteAtOnceAsADeposit"/>);
    /// una missione con due porte che la mappa non conosce diventava un cambio cella, e
    /// con l'UDC fuori dalla sua cella (voce 54, decisione 2) non ha un dispositivo di arrivo, e la
    /// costruzione non scrive niente (le porte mappate: <see cref="WhereTheDoorsKeepItOffTheStraightRouteTheMissionIsQueuedAsToday"/>).
    /// Tipo, rotta e coda come prima, e nessuna chiamata al gestionale.
    /// </summary>
    [Theory]
    [InlineData(MissionTypes.DefragmentCell, "", "")]
    [InlineData(MissionTypes.ChangeStoreLocation, "", "")]
    [InlineData(MissionTypes.MovingToItem, "P01A", "S02A")]
    public async Task AMissionTheNavigatorBuildsItsOwnWayIsQueuedAsToday(MissionTypes type, string pickUpDoor, string storageDoor)
    {
        var mission = await AMissionOnFBoundForD(type);
        mission.PickUpDoorCode = pickUpDoor;
        mission.StorageDoorCode = storageDoor;
        mission.GuidAssignmentLu = new Guid("4a7e0c2d-0000-0000-0000-000000000031");
        TheEngineIs(NavigatorStatusTypes.Stop);

        Assert.Equal(MissionMoveOutcome.Moved, MissionGateway.Move(mission.MissionId, _plant["T"].ItemGuid));

        Assert.Same(mission, ActiveMissions.Instance[mission.MissionId]);
        Assert.Equal(type, mission.MissionType);
        Assert.Empty(mission.MissionSteps);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
        await mission.PositionWork;
        Assert.Empty(_warehouse.Calls);
    }

    /// <summary>
    /// Le due porte da sole non mandano la missione in coda: il navigatore ne fa un cambio cella solo
    /// quando portano allo stesso dispositivo, a uno che la mappa non conosce, o tutte e due a un
    /// magazzino. Un'estrazione dal magazzino verso un polmone le ha tutte e due, la porta della cella
    /// da cui esce e quella del polmone (<c>AddMissionToPickUp</c>), e resta un'estrazione dritta
    /// verso la destinazione; cosi' una missione uscita dal magazzino e mandata dall'operatore su un
    /// polmone (il commit del cambio scrive la porta del polmone e tiene quella della cella), anche da
    /// un T con una modalita' di deposito; e un <c>PickUpAndStoreAgain</c>, il cui tipo le porte non
    /// toccano mai. Spostata su T, la rotta T->D c'e' subito, a motore fermo, il tipo resta, la
    /// missione non e' in coda, e il gestionale non si chiama.
    /// Voce 54, decisione 2 del proprietario del 13/10: con l'UDC fuori dalla sua cella (qui su T) le
    /// porte non fanno piu' un cambio cella nemmeno quando portano allo stesso polmone (B01A e B01B) o
    /// quando quella di partenza la mappa non la conosce (X99A): l'estrazione e' quello che e'
    /// fisicamente, un'estrazione verso il polmone D, e va dritta. Prima andavano in coda come cambio
    /// cella.
    /// </summary>
    [Theory]
    [InlineData(MissionTypes.Extraction, StorageDoor, "B01A", false)]
    [InlineData(MissionTypes.Extraction, StorageDoor, "B01A", true)]
    [InlineData(MissionTypes.PickUpAndStoreAgain, StorageDoor, "S02A", false)]
    [InlineData(MissionTypes.Extraction, "B01A", "B01B", false)]
    [InlineData(MissionTypes.Extraction, "X99A", "B01A", false)]
    public async Task WhereTheDoorsLeaveTheMissionGoingStraightTheNewRouteIsThereAtOnce(MissionTypes type,
        string pickUpDoor, string storageDoor, bool chosenByTheOperator)
    {
        var mission = await AMissionOnFBoundForD(type, destinationType: DeviceTypes.Buffer);
        await TheDoorsAreMapped();
        if (chosenByTheOperator)
        {
            Assert.True(mission.SetOperatorDestination(_plant["D"].ItemGuid));
            TIsConfiguredWith("store mode");
        }

        mission.PickUpDoorCode = pickUpDoor;
        mission.StorageDoorCode = storageDoor;
        TheEngineIs(NavigatorStatusTypes.Stop);
        Navigator.EventSink = _recorder;

        Assert.Equal(MissionMoveOutcome.Moved, MissionGateway.Move(mission.MissionId, _plant["T"].ItemGuid));

        Assert.Equal(["T", "D"], _plant.Names(mission.MissionSteps));
        Assert.Equal(type, mission.MissionType);
        Assert.Null(RoutesToBuild.Instance[mission.MissionId]);
        Assert.Equal(_plant["D"].ItemGuid, mission.DestinationItemId);
        Assert.Equal(chosenByTheOperator, mission.OperatorDestination);
        Assert.Contains(("mission:StepChanged", mission.MissionId, Guid.Empty, Guid.Empty), _recorder.Events);
        await mission.PositionWork;
        Assert.Empty(_warehouse.Calls);
    }

    /// <summary>
    /// Dove le due porte tengono la missione lontana dalla rotta dritta lo spostamento la lascia in
    /// coda come prima, e la costruisce il giro delle rotte. Con l'UDC fuori dalla sua cella (voce 54,
    /// decisione 2) le porte di due magazzini non fanno piu' un cambio cella: l'estrazione diventa un
    /// deposito verso S2, ma la ricostruzione prima aspetta la consegna del prelievo, che puo' chiamare
    /// il gestionale, e lo spostamento, sincrono, la lascia al giro (risposta 3 del proprietario del
    /// 13/10). La porta di arrivo che la mappa non conosce non porta a nessun dispositivo, e la rotta
    /// non c'e'. Il tipo resta un'estrazione, nessuna rotta, e nessuna chiamata al gestionale.
    /// </summary>
    [Theory]
    [InlineData(StorageDoor, "S02A")]
    [InlineData(StorageDoor, "X99A")]
    public async Task WhereTheDoorsKeepItOffTheStraightRouteTheMissionIsQueuedAsToday(string pickUpDoor, string storageDoor)
    {
        var mission = await AMissionOnFBoundForD(MissionTypes.Extraction, destinationType: DeviceTypes.Buffer);
        await TheDoorsAreMapped();

        // La mappa si legge davvero: una lettura che non riesce risponde Guid.Empty per ogni porta, e
        // due porte vuote sono «lo stesso dispositivo», cioe' un cambio cella per la ragione sbagliata.
        Assert.Equal(_plant["D"].ItemGuid, await ORM.DbInfo.GetItemId("B01A"));
        Assert.Equal(_plant["S"].ItemGuid, await ORM.DbInfo.GetItemId(StorageDoor));

        mission.PickUpDoorCode = pickUpDoor;
        mission.StorageDoorCode = storageDoor;
        TheEngineIs(NavigatorStatusTypes.Stop);

        Assert.Equal(MissionMoveOutcome.Moved, MissionGateway.Move(mission.MissionId, _plant["T"].ItemGuid));

        Assert.Equal(MissionTypes.Extraction, mission.MissionType);
        Assert.Empty(mission.MissionSteps);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
        await mission.PositionWork;
        Assert.Empty(_warehouse.Calls);
    }

    /// <summary>
    /// Senza destinazione non c'e' niente verso cui andare: la missione va in coda come prima, e il
    /// suo tipo resta quello che e' (la costruzione del navigatore lo farebbe diventare un movimento).
    /// La destinazione assente e' vuota, o nulla (la scelta fra due destinazioni fisse senza nessuna
    /// delle due scrive il nullo): tutte e due sono «senza destinazione».
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AMissionWithoutADestinationIsQueuedAsToday(bool nullDestination)
    {
        var mission = await AMissionOnFBoundForD(MissionTypes.None, withDestination: false);
        if (nullDestination) mission.DestinationItemId = null;
        TheEngineIs(NavigatorStatusTypes.Stop);

        Assert.Equal(MissionMoveOutcome.Moved, MissionGateway.Move(mission.MissionId, _plant["T"].ItemGuid));

        Assert.Equal(MissionTypes.None, mission.MissionType);
        Assert.Empty(mission.MissionSteps);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
        await mission.PositionWork;
    }

    /// <summary>
    /// Da U un percorso verso D non c'e': lo spostamento non si rifiuta per questo (e' un fatto del
    /// campo), la missione resta spostata e va in coda come prima, senza rotta e senza annunciarne una.
    /// </summary>
    [Fact]
    public async Task WithoutARouteFromTheNewPositionTheMissionIsQueuedAsToday()
    {
        var mission = await AMissionOnFBoundForD();
        TheEngineIs(NavigatorStatusTypes.Stop);
        Navigator.EventSink = _recorder;

        Assert.Equal(MissionMoveOutcome.Moved, MissionGateway.Move(mission.MissionId, _plant["U"].ItemGuid));

        Assert.Equal(_plant["U"].ItemGuid, mission.PositionItemId);
        Assert.Empty(mission.MissionSteps);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
        Assert.DoesNotContain(_recorder.Events, e => e.Kind == "mission:StepChanged");
        await mission.PositionWork;
    }

    /// <summary>
    /// Con una ricerca della cella (o un cambio di destinazione) in corso per la missione, lo
    /// spostamento non costruisce la rotta accanto a lei: la missione va in coda come prima.
    /// </summary>
    [Fact]
    public async Task WhileACellSearchIsInFlightTheMissionIsQueuedAsToday()
    {
        var mission = await AMissionOnFBoundForD();
        TheEngineIs(NavigatorStatusTypes.Stop);
        var search = new TaskCompletionSource();
        Assert.True(Navigator.TryClaimCellSearch(mission.MissionId, search.Task));

        try
        {
            Assert.Equal(MissionMoveOutcome.Moved, MissionGateway.Move(mission.MissionId, _plant["T"].ItemGuid));

            Assert.Empty(mission.MissionSteps);
            Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
            Assert.Same(search.Task, Navigator.CellSearchInFlight(mission.MissionId));
        }
        finally
        {
            Navigator.ReleaseCellSearchClaim(mission.MissionId, search.Task);
            search.TrySetResult();
        }

        await mission.PositionWork;
    }

    // ---------------------------------------------------------------------------------------
    // Gli annunci
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// L'operatore sposta l'UDC dove e' davvero: l'ultimo step F->T e' rimasto addosso a F dopo un
    /// comando non passato (<c>Warning</c>, non in volo), e T gli tiene il posto. Lo spostamento
    /// scarta lo step, e i due dispositivi non si annunciano mai con i gettoni di prima: ne' F con lo
    /// step, ne' T con il posto, nemmeno nel lavoro di sfondo del setter, che parte dopo lo scarto.
    /// </summary>
    [Fact]
    public async Task TheTwoDevicesAreNeverAnnouncedWithTheTokensOfTheDiscardedRoute()
    {
        var mission = await AMissionOnFBoundForD();
        TheLastStepIsLeftOnFWithTheSeatOn(mission, "T");
        TheEngineIs(NavigatorStatusTypes.Stop);
        Navigator.EventSink = _recorder;

        Assert.Equal(MissionMoveOutcome.Moved, MissionGateway.Move(mission.MissionId, _plant["T"].ItemGuid));
        await mission.PositionWork;

        Assert.Equal(["T", "D"], _plant.Names(mission.MissionSteps));
        var announced = _recorder.Events
            .Where(e => e.Kind == "item" && (e.Id == _plant["F"].ItemGuid || e.Id == _plant["T"].ItemGuid))
            .ToList();
        Assert.Contains(announced, e => e.Id == _plant["F"].ItemGuid);
        Assert.Contains(announced, e => e.Id == _plant["T"].ItemGuid);
        Assert.All(announced, e =>
        {
            Assert.Equal(Guid.Empty, e.RunningStepId);
            Assert.Equal(Guid.Empty, e.WaitingStepId);
        });
    }

    /// <summary>
    /// Il posto dello step scartato lo teneva N, che non e' nessuno dei due dispositivi dello
    /// spostamento: lo scarto glielo restituisce, e il setter di <c>RunningStepId</c> annuncia solo F.
    /// Lo spostamento annuncia anche N, dopo il lavoro sulla rotta, con il posto libero.
    /// </summary>
    [Fact]
    public async Task ADeviceThatHeldTheSeatOfTheDiscardedRouteIsAnnouncedWithTheSeatGivenBack()
    {
        var mission = await AMissionOnFBoundForD();
        TheLastStepIsLeftOnFWithTheSeatOn(mission, "N");
        TheEngineIs(NavigatorStatusTypes.Stop);
        Navigator.EventSink = _recorder;

        Assert.Equal(MissionMoveOutcome.Moved, MissionGateway.Move(mission.MissionId, _plant["T"].ItemGuid));
        await mission.PositionWork;

        Assert.Equal(Guid.Empty, _plant["N"].WaitingStepId);
        var last = _recorder.Events.Last(e => e.Kind == "item" && e.Id == _plant["N"].ItemGuid);
        Assert.Equal(Guid.Empty, last.WaitingStepId);
    }

    // ---------------------------------------------------------------------------------------
    // La corsa con il lavoro di sfondo del setter (critic.md §5)
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Una missione <c>PickUpAndStoreAgain</c> spostata sulla sua destinazione T: il lavoro di sfondo
    /// del setter la rigira verso il magazzino della sua porta e scrive lui la rotta T->S. Lo
    /// spostamento non la tocca: la rotta vecchia l'ha scartata prima di cambiare la posizione, e
    /// verso la destinazione che la missione aveva (T, il dispositivo raggiunto) non costruisce
    /// niente. La missione va in coda come prima.
    /// </summary>
    /// <remarks>
    /// L'ultimo step F->T e' rimasto addosso a F, quindi lo scarto annuncia F sul thread dello
    /// spostamento; li' la prova aspetta il lavoro di sfondo che c'e' in quel momento. Scartando dopo
    /// il setter, quel lavoro e' gia' il nuovo: lo scarto riprende dopo che ha scritto la rotta T->S,
    /// e la cancella.
    /// </remarks>
    [Fact]
    public async Task AMoveOntoTheDestinationOfAPickUpAndStoreAgainKeepsTheRouteOfTheBackgroundWork()
    {
        foreach (var name in new[] { "F", "T" }) _plant.Device(name);
        var storage = _plant.Device("S", DeviceTypes.Storage);
        _plant.Link("F", "T");
        _plant.Link("T", "S");
        foreach (var name in _plant.DeviceNames) _plant[name].AutoExecuteStep = false;
        await PlantPersistence.SaveDoorMappingsAsync(storage.ItemGuid,
            [new DoorMapping { ItemId = storage.ItemGuid, DoorCode = StorageDoor, CellId = StorageCell, Side = "A" }]);

        var mission = new PathMission
        {
            PositionItemId = _plant["F"].ItemGuid,
            DestinationItemId = _plant["T"].ItemGuid,
            MissionType = MissionTypes.PickUpAndStoreAgain,
            StorageDoorCode = StorageDoor,
            LuId = LoadUnit
        };
        ActiveMissions.Instance.Add(mission, buildRoute: false);
        await mission.PositionWork;
        TheLastStepIsLeftOnFWithTheSeatOn(mission, "T");
        TheEngineIs(NavigatorStatusTypes.Stop);

        var moving = Environment.CurrentManagedThreadId;
        var waited = false;
        _recorder.BeforeItem = device =>
        {
            if (waited || device != _plant["F"] || Environment.CurrentManagedThreadId != moving) return;

            waited = true;
            Assert.True(mission.PositionWork.Wait(TimeSpan.FromSeconds(30)), "il lavoro di sfondo non e' finito");
        };
        Navigator.EventSink = _recorder;

        Assert.Equal(MissionMoveOutcome.Moved, MissionGateway.Move(mission.MissionId, _plant["T"].ItemGuid));
        await mission.PositionWork;

        Assert.True(waited);
        Assert.Equal(storage.ItemGuid, mission.DestinationItemId);
        Assert.Equal(MissionTypes.StoringByPickUp, mission.MissionType);
        Assert.Equal(["T", "S"], _plant.Names(mission.MissionSteps));
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
    }

    // ---------------------------------------------------------------------------------------
    // L'impianto
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// F collegato a N, N a D; T collegato a D e a Y; U isolato. Su F una missione del tipo dato,
    /// verso D (o senza destinazione), aggiunta in Recover con il primo posizionamento finito e senza
    /// rotta; tutti i dispositivi senza modalita' di deposito ne' destinazione fissa, e nessuno che
    /// esegue da solo (<c>AutoExecuteStep</c> spento).
    /// </summary>
    private Task<PathMission> AMissionOnFBoundForD(MissionTypes type = MissionTypes.MovingToItem,
        bool withDestination = true, DeviceTypes destinationType = DeviceTypes.Automation) =>
        AMissionBoundForD("F", type, withDestination, destinationType);

    /// <summary>
    /// Lo stesso impianto di <see cref="AMissionOnFBoundForD"/>, con la missione su
    /// <paramref name="position"/>; D e' del tipo dato (un polmone, per le prove delle porte).
    /// </summary>
    private async Task<PathMission> AMissionBoundForD(string position, MissionTypes type = MissionTypes.MovingToItem,
        bool withDestination = true, DeviceTypes destinationType = DeviceTypes.Automation)
    {
        foreach (var name in new[] { "F", "N", "D", "T", "Y", "U" })
            _plant.Device(name, name == "D" ? destinationType : DeviceTypes.Automation);
        _plant.Link("F", "N");
        _plant.Link("N", "D");
        _plant.Link("T", "D");
        _plant.Link("T", "Y");
        Assert.All(_plant.DeviceNames, name => Assert.Equal(LookWhereAllocateLuTypes.No, _plant[name].LookWhereAllocateLu));

        // Nessuno parte da solo: il passaggio in Run sveglierebbe F per la rotta vecchia.
        foreach (var name in _plant.DeviceNames) _plant[name].AutoExecuteStep = false;

        Assert.Equal(NavigatorStatusTypes.Recover, Navigator.NavStatus);
        var mission = new PathMission { PositionItemId = _plant[position].ItemGuid, MissionType = type };
        if (withDestination) mission.DestinationItemId = _plant["D"].ItemGuid;
        ActiveMissions.Instance.Add(mission, buildRoute: false);
        Assert.Same(mission, _plant[position].LoadedLu);
        await mission.PositionWork;
        return mission;
    }

    /// <summary>
    /// La mappa delle porte nel database del WCS: S01A e S02A di due magazzini, S e S2 (fuori dal
    /// grafo: servono solo alle porte), e B01A e B01B tutte e due di D. X99A non e' nella mappa.
    /// </summary>
    private async Task TheDoorsAreMapped()
    {
        var storage = _plant.Device("S", DeviceTypes.Storage);
        var otherStorage = _plant.Device("S2", DeviceTypes.Storage);
        var buffer = _plant["D"];
        await PlantPersistence.SaveDoorMappingsAsync(storage.ItemGuid,
            [new DoorMapping { ItemId = storage.ItemGuid, DoorCode = StorageDoor, CellId = StorageCell, Side = "A" }]);
        await PlantPersistence.SaveDoorMappingsAsync(otherStorage.ItemGuid,
            [new DoorMapping { ItemId = otherStorage.ItemGuid, DoorCode = "S02A", CellId = 621, Side = "A" }]);
        await PlantPersistence.SaveDoorMappingsAsync(buffer.ItemGuid,
        [
            new DoorMapping { ItemId = buffer.ItemGuid, DoorCode = "B01A", CellId = 710, Side = "A" },
            new DoorMapping { ItemId = buffer.ItemGuid, DoorCode = "B01B", CellId = 710, Side = "B" }
        ]);
    }

    /// <summary>La configurazione di T: una destinazione fissa (Y) o una modalita' di deposito.</summary>
    private void TIsConfiguredWith(string configuration)
    {
        var device = _plant["T"];
        switch (configuration)
        {
            case "fixed destination":
                device.DestinationItemId = _plant["Y"].ItemGuid;
                break;
            case "second fixed destination":
                device.DestinationItemId2 = _plant["Y"].ItemGuid;
                break;
            case "check only":
                device.LookWhereAllocateLu = LookWhereAllocateLuTypes.CheckOnly;
                break;
            case "store mode":
                device.LookWhereAllocateLu = LookWhereAllocateLuTypes.BuildRouteToStore;
                break;
            case "store mode by load unit type":
                device.LookWhereAllocateLu = LookWhereAllocateLuTypes.BuildRouteToStoreByIdLuType;
                break;
            case "store mode forcing":
                device.LookWhereAllocateLu = LookWhereAllocateLuTypes.BuildRouteToStoreForcing;
                break;
            case "different store location":
                device.LookWhereAllocateLu = LookWhereAllocateLuTypes.BuildRouteToDifferentStoreLocation;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(configuration), configuration, null);
        }
    }

    /// <summary>
    /// L'ultimo step F-><paramref name="next"/> rimasto addosso a F dopo un comando non passato
    /// (<c>Warning</c>: non e' in volo, lo spostamento lo puo' scartare), con il posto tenuto da
    /// <paramref name="next"/>.
    /// </summary>
    private void TheLastStepIsLeftOnFWithTheSeatOn(PathMission mission, string next)
    {
        var step = _plant.PlannedStep(mission, "F", next);
        _plant["F"].RunningStepId = step.StepId;
        _plant["F"].Status = DeviceStatusTypes.Warning;
        Assert.True(_plant[next].TryReserveForIncomingStep(step.StepId));
        Assert.Null(mission.StepInFlight());
    }

    /// <summary>Lo stato del motore per la prova, dopo l'aggiunta della missione in Recover.</summary>
    private static void TheEngineIs(NavigatorStatusTypes status) => Navigator.NavStatus = status;

    /// <summary>Un giro del ciclo delle rotte, come lo fa il motore ogni 10 ms.</summary>
    private static Task OneTurnOfTheRouteLoop()
    {
        var method = typeof(Navigator).GetMethod("BuildRoutePath", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (Task)method!.Invoke(null, null)!;
    }
}
