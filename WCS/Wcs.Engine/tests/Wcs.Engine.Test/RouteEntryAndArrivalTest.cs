using Wcs.Engine.Collections;
using Wcs.Engine.Items;
using DbInfo = Wcs.Engine.ORM.DbInfo;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Wcs.Engine.Test.Routing;
using Wcs.Wms.Contracts;
using Wcs.Wms.Contracts.Test;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// L'ingresso fra le attive e l'arrivo scrivono la rotta con la primitiva (voce 41, compito 3, disegno
/// C): chi ha calcolato la rotta su una versione vecchia non la scrive, e la missione torna al giro
/// delle rotte del navigatore. Le corse 1 e 2 della ricognizione
/// (una specifica interna, non pubblicata).
/// </summary>
/// <remarks>
/// <para>
/// Il motore sta in <c>Run</c> per l'ingresso (<c>ActiveMissions.Add</c> esce subito in Stop), con
/// <c>AutoExecuteStep</c> spento su tutti i dispositivi, perche' il passaggio in Run li sveglia; per il
/// resto in <c>Stop</c>. Mai in Recover: il setter della destinazione salta il suo blocco e
/// <c>PathBuilder</c> i filtri.
/// </para>
/// <para>
/// Nessuna attesa a tempo: il primo costruttore si ferma in una cucitura
/// (<see cref="PathMission.RouteBuildPauseForTesting"/>, <see cref="PathMission.ArrivalPauseForTesting"/>)
/// e la prova lo lascia andare quando l'altro scrittore ha finito. I tetti di 30 s fanno fallire invece
/// di appendere, e non decidono niente. Il secondo scrittore e' quello che il navigatore fa scrivere
/// alla missione dopo la ricerca della cella (la porta, la destinazione, poi
/// <see cref="PathMission.BuildPathToDestination"/>), chiamato dalla prova senza il gestionale.
/// </para>
/// <para>
/// La coerenza della rotta e' quella della ricognizione (§1): gli step partono dalla posizione, vanno
/// in fila uno dopo l'altro senza due step con la stessa sorgente, e finiscono sulla destinazione.
/// Si guarda la proprieta', non si ricalcola la rotta.
/// </para>
/// </remarks>
public sealed class RouteEntryAndArrivalTest : IDisposable
{
    private const string PickUpDoor = "A5";
    private const string DoorOfS7 = "A7";
    private const string DoorOfS8 = "A8";
    private const string DoorOfP = "A9";
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);

    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly FakeWarehouse _warehouse = new();
    private readonly RoutingTestPlant _plant = new(NavigatorStatusTypes.Stop);

    public RouteEntryAndArrivalTest()
    {
        Navigator.Store = _database;
        Navigator.Warehouse = _warehouse;
        RoutesToBuild.Instance.Items = new();
    }

    public void Dispose()
    {
        RoutesToBuild.Instance.Items = new();
        _plant.Dispose();
        Navigator.Warehouse = _previousWarehouse;
        Navigator.Store = null;
        _database.Dispose();
    }

    // ---------------------------------------------------------------------------------------
    // Corsa 1: l'ingresso fra le attive
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Corsa 1. L'ingresso fra le attive costruisce la rotta P->X in sottofondo, e si ferma prima di
    /// scriverla. Intanto un altro scrittore manda la missione verso Y e le scrive la rotta P->H->Y.
    /// L'ingresso riparte: la sua rotta e' calcolata verso una destinazione che non e' piu' quella, e
    /// non la scrive. La missione resta verso Y con la rotta verso Y, e torna al giro delle rotte. Prima
    /// l'ingresso scriveva la sua rotta sopra: destinazione Y, step verso X.
    /// </summary>
    [Fact]
    public async Task AnEntryRouteComputedBeforeTheDestinationChangedIsNotWritten()
    {
        foreach (var name in new[] { "P", "X", "H", "Y" }) _plant.Device(name);
        _plant.Link("P", "X");
        _plant.Link("P", "H");
        _plant.Link("H", "Y");
        var mission = await AMissionOn("P", towards: "X");
        using var pause = new PauseOnce();
        mission.RouteBuildPauseForTesting = pause.Pause;

        ActiveMissions.Instance.Add(mission);
        await pause.Reached.WaitAsync(Ceiling);

        mission.DestinationItemId = _plant["Y"].ItemGuid;
        await mission.BuildPathToDestination();
        AssertCoherent(mission, "P", "Y");

        pause.Release();
        await mission.RouteWork.WaitAsync(Ceiling);

        Assert.Equal(_plant["Y"].ItemGuid, mission.DestinationItemId);
        AssertCoherent(mission, "P", "Y");
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
    }

    /// <summary>
    /// Senza nessun altro scrittore l'ingresso scrive la sua rotta, come prima, e la missione non va
    /// in coda al giro delle rotte.
    /// </summary>
    [Fact]
    public async Task AnEntryAloneWritesItsRoute()
    {
        foreach (var name in new[] { "P", "H", "Y" }) _plant.Device(name);
        _plant.Link("P", "H");
        _plant.Link("H", "Y");
        var mission = await AMissionOn("P", towards: "Y");

        ActiveMissions.Instance.Add(mission);
        await mission.RouteWork.WaitAsync(Ceiling);

        AssertCoherent(mission, "P", "Y");
        Assert.Null(RoutesToBuild.Instance[mission.MissionId]);
    }

    /// <summary>
    /// Un costruttore calcola la rotta da P e si ferma prima di scriverla; intanto l'UDC arriva su Q (la
    /// posizione cambia, come all'arrivo di uno step). La rotta da P non vale piu': non si scrive, e la
    /// missione torna al giro delle rotte, che la rifa' da Q. Prima la missione restava su Q con una
    /// rotta che parte da P.
    /// </summary>
    [Fact]
    public async Task ARouteComputedBeforeThePositionChangedIsNotWritten()
    {
        foreach (var name in new[] { "P", "Q", "X" }) _plant.Device(name);
        _plant.Link("P", "X");
        _plant.Link("Q", "X");
        var mission = await AMissionOn("P", towards: "X");
        await ActiveInStop(mission);
        using var pause = new PauseOnce();
        mission.RouteBuildPauseForTesting = pause.Pause;

        var building = mission.BuildPathToDestination();
        await pause.Reached.WaitAsync(Ceiling);

        mission.PositionItemId = _plant["Q"].ItemGuid;
        await mission.PositionWork;

        pause.Release();
        await building.WaitAsync(Ceiling);

        Assert.Empty(mission.MissionSteps);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
    }

    // ---------------------------------------------------------------------------------------
    // Corsa 2: l'arrivo di un PickUpAndStoreAgain
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// L'arrivo di un <c>PickUpAndStoreAgain</c> sulla sua destinazione P, da solo: la missione diventa
    /// un deposito verso il magazzino della sua porta (S7), con la rotta P->S7, e la cella di quella
    /// porta si sblocca. L'altra porta resta com'era, e la missione non va in coda al giro delle rotte.
    /// </summary>
    [Fact]
    public async Task AnArrivalAloneTurnsTheMissionTowardsItsStorageDoor()
    {
        var mission = await APickUpAndStoreAgainArrivingOnP();

        mission.PositionItemId = _plant["P"].ItemGuid;
        await mission.PositionWork;

        Assert.Equal(_plant["S7"].ItemGuid, mission.DestinationItemId);
        Assert.Equal(DoorOfS7, mission.StorageDoorCode);
        Assert.Equal(MissionTypes.StoringByPickUp, mission.MissionType);
        Assert.Equal(string.Empty, mission.PickUpDoorCode);
        Assert.Equal(_plant["P"].ItemGuid, mission.SourceItemId);
        AssertCoherent(mission, "P", "S7");
        Assert.False(await DbInfo.GetDoorLockedForStoring(DoorOfS7));
        Assert.True(await DbInfo.GetDoorLockedForStoring(DoorOfS8));
        Assert.Null(RoutesToBuild.Instance[mission.MissionId]);
    }

    /// <summary>
    /// L'arrivo cerca la rotta del deposito che la missione diventa, come prima, quando il tipo si
    /// scriveva prima della ricerca: da P a S7 la via corta passa per W, che e' in errore, e quella
    /// lunga per A e B. Un deposito non passa per un dispositivo in errore finche' ne trova un'altra; un
    /// <c>PickUpAndStoreAgain</c>, che ignora lo stato dei dispositivi, ci passerebbe.
    /// </summary>
    [Fact]
    public async Task AnArrivalLooksForTheRouteOfTheDepositItBecomes()
    {
        var mission = await APickUpAndStoreAgainArrivingOnP(directToS7: false);
        foreach (var name in new[] { "W", "A", "B" }) _plant.Device(name);
        _plant.Chain("P", "W", "S7");
        _plant.Chain("P", "A", "B", "S7");
        _plant["W"].Status = DeviceStatusTypes.Error;

        mission.PositionItemId = _plant["P"].ItemGuid;
        await mission.PositionWork;

        AssertCoherent(mission, "P", "S7");
        Assert.Equal(["P", "A", "B", "S7"], RouteNames(mission));
    }

    /// <summary>
    /// Corsa 2. L'arrivo di un <c>PickUpAndStoreAgain</c> legge la porta S7 e si ferma prima di
    /// scrivere. Intanto il navigatore gli prenota una cella in S8: scrive la porta e la destinazione
    /// S8 e costruisce la rotta P->S8. L'arrivo riparte su una versione vecchia e non scrive niente: ne'
    /// la destinazione ne' la rotta. Porta, destinazione e rotta restano tutte di S8, e la missione torna
    /// al giro delle rotte. La porta A7 che l'arrivo aveva preso si sblocca, anche nel database del WCS
    /// (risposta 3 del proprietario dell'11/10), e quella di S8 resta bloccata. Prima l'arrivo scriveva la
    /// destinazione S7 e la rotta verso S7, e sbloccava la porta che la missione aveva allora, quella di
    /// S8; e dopo la corsa 2, perdendo, lasciava A7 bloccata per sempre.
    /// </summary>
    [Fact]
    public async Task AnArrivalComputedBeforeTheNavigatorChoseAnotherStoreIsNotWritten()
    {
        var mission = await APickUpAndStoreAgainArrivingOnP();
        using var pause = new PauseOnce();
        mission.ArrivalPauseForTesting = pause.Pause;

        mission.PositionItemId = _plant["P"].ItemGuid;
        await pause.Reached.WaitAsync(Ceiling);

        mission.StorageDoorCode = DoorOfS8;
        mission.DestinationItemId = _plant["S8"].ItemGuid;
        await mission.BuildPathToDestination();
        AssertCoherent(mission, "P", "S8");

        pause.Release();
        await mission.PositionWork.WaitAsync(Ceiling);

        Assert.Equal(_plant["S8"].ItemGuid, mission.DestinationItemId);
        Assert.Equal(DoorOfS8, mission.StorageDoorCode);
        AssertCoherent(mission, "P", "S8");
        Assert.False(await DbInfo.GetDoorLockedForStoring(DoorOfS7));
        Assert.True(await DbInfo.GetDoorLockedForStoring(DoorOfS8));
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
    }

    /// <summary>
    /// L'arrivo trova ancora in volo lo step che ha portato l'UDC su P: il completamento ha gia'
    /// lanciato il carico (che cambia la posizione e fa partire l'arrivo) ma non ha ancora tolto lo
    /// step eseguito, e la sorgente U ne tiene il gettone. Il setter della destinazione scarta la
    /// scrittura, e l'arrivo non scrive nient'altro: ne' il tipo, ne' la porta di prelievo, ne' la
    /// rotta, e non sblocca la porta. Prima scriveva tutto il resto e lasciava la missione con il
    /// tipo del deposito e la rotta P->S7, ma con la destinazione su P. E la missione torna al giro delle
    /// rotte, che rifa' la conversione quando lo step e' finito (risposta 3 del proprietario dell'11/10):
    /// prima non la ritentava nessuno, e restava ferma su P.
    /// </summary>
    [Fact]
    public async Task AnArrivalWithTheStepThatBroughtItStillInFlightWritesNothing()
    {
        var mission = await APickUpAndStoreAgainArrivingOnP();
        var executed = TheStepThatBroughtItInFlight(mission);

        mission.PositionItemId = _plant["P"].ItemGuid;
        await mission.PositionWork;

        Assert.Equal(_plant["P"].ItemGuid, mission.DestinationItemId);
        Assert.Equal(MissionTypes.PickUpAndStoreAgain, mission.MissionType);
        Assert.Equal(PickUpDoor, mission.PickUpDoorCode);
        Assert.Equal(DoorOfS7, mission.StorageDoorCode);
        Assert.Same(executed, Assert.Single(mission.MissionSteps));
        Assert.Equal(executed.StepId, _plant["U"].RunningStepId);
        Assert.True(await DbInfo.GetDoorLockedForStoring(DoorOfS7));
        Assert.True(await DbInfo.GetDoorLockedForStoring(DoorOfS8));
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
    }

    /// <summary>
    /// L'arrivo rifiutato per lo step che l'ha portata su P ancora in volo: quando lo step e' finito (il
    /// gettone di U libero, lo step fuori dalla rotta) il giro delle rotte rifa' la conversione. La
    /// missione diventa un deposito verso S7 con la rotta P->S7, e la porta A7 si sblocca. Prima il giro
    /// cercava per lei una cella con la configurazione di P, o niente: una missione gia' sulla sua
    /// destinazione non si costruiva, e restava ferma.
    /// </summary>
    [Fact]
    public async Task TheRouteLoopRedoesTheConversionWhenTheStepThatBroughtItIsOver()
    {
        var mission = await APickUpAndStoreAgainArrivingOnP();
        var executed = TheStepThatBroughtItInFlight(mission);
        mission.PositionItemId = _plant["P"].ItemGuid;
        await mission.PositionWork;
        Assert.Equal(MissionTypes.PickUpAndStoreAgain, mission.MissionType);

        _plant["U"].RunningStepId = Guid.Empty;
        Collections.MissionSteps.Instance.Remove(executed);
        Assert.Null(mission.StepInFlight());

        StartCellSearch(mission, _plant["P"]);
        await (Navigator.CellSearchInFlight(mission.MissionId) ?? Task.CompletedTask).WaitAsync(Ceiling);

        Assert.Equal(MissionTypes.StoringByPickUp, mission.MissionType);
        Assert.Equal(string.Empty, mission.PickUpDoorCode);
        Assert.Equal(DoorOfS7, mission.StorageDoorCode);
        AssertCoherent(mission, "P", "S7");
        Assert.False(await DbInfo.GetDoorLockedForStoring(DoorOfS7));
        Assert.True(await DbInfo.GetDoorLockedForStoring(DoorOfS8));
    }

    /// <summary>
    /// Un <c>PickUpAndStoreAgain</c> arriva su P, e la sua porta di deposito porta a P stesso (P e' il
    /// dispositivo della porta del magazzino). L'arrivo non ha un magazzino verso cui convertirla: la rimette
    /// al giro, e il giro va avanti con lei come con le altre (la cella prenotata, la ricerca del deposito),
    /// senza rifare la conversione a ogni giro. Fino al giro finale delle correzioni della voce 41 il giro
    /// rifaceva la conversione e rispondeva <c>NotAvailable</c> ogni volta: la missione aspettava con il
    /// freno, per sempre, e la ricerca del deposito non arrivava mai.
    /// </summary>
    [Fact]
    public async Task APickUpAndStoreAgainWhoseDoorLeadsToItsPositionIsLeftToTheRouteLoop()
    {
        var mission = await APickUpAndStoreAgainArrivingOnP();
        await PlantPersistence.SaveDoorMappingsAsync(_plant["P"].ItemGuid,
            [new DoorMapping { ItemId = _plant["P"].ItemGuid, DoorCode = DoorOfP, CellId = 9, Side = "A" }]);
        mission.StorageDoorCode = DoorOfP;

        mission.PositionItemId = _plant["P"].ItemGuid;
        await mission.PositionWork.WaitAsync(Ceiling);

        Assert.Equal(_plant["P"].ItemGuid, mission.DestinationItemId);
        Assert.Equal(MissionTypes.PickUpAndStoreAgain, mission.MissionType);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);

        Assert.Null(await mission.RedoInTheRouteLoopAsync().WaitAsync(Ceiling));
        Assert.Empty(mission.MissionSteps);
    }

    /// <summary>
    /// Lo stesso arrivo con lo step U->P ancora in volo, e il magazzino della porta (S7) senza una rotta da
    /// P. Finito lo step, il giro rifa' la conversione: c'e' un magazzino ma nessuna rotta, e il giro lo
    /// conta come un fallimento (<c>NotAvailable</c>), con la missione in coda. E' l'unico caso
    /// dell'arrivo che il freno conta.
    /// </summary>
    [Fact]
    public async Task APickUpAndStoreAgainWithoutARouteToItsStoreIsAFailureForTheRouteLoop()
    {
        var mission = await APickUpAndStoreAgainArrivingOnP(directToS7: false);
        var executed = TheStepThatBroughtItInFlight(mission);
        mission.PositionItemId = _plant["P"].ItemGuid;
        await mission.PositionWork.WaitAsync(Ceiling);
        Assert.Equal(MissionTypes.PickUpAndStoreAgain, mission.MissionType);

        _plant["U"].RunningStepId = Guid.Empty;
        Collections.MissionSteps.Instance.Remove(executed);
        Assert.Null(mission.StepInFlight());
        RoutesToBuild.Instance.Items = new();

        Assert.Equal(AutomationDevice.RouteBuildStatusTypes.NotAvailable,
            await mission.RedoInTheRouteLoopAsync().WaitAsync(Ceiling));
        Assert.Empty(mission.MissionSteps);
        Assert.Equal(_plant["S7"].ItemGuid, mission.DestinationItemId);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
    }

    /// <summary>
    /// Una costruzione che non trova niente da scrivere (qui una missione senza tipo, con le due porte su
    /// due dispositivi che non sono magazzini: il tipo resta <c>None</c> e la rotta e' vuota) non fa salire
    /// la versione della rotta: uno scrittore che l'ha letta prima scrive la sua rotta. Fino al giro finale
    /// delle correzioni della voce 41 la costruzione scriveva la rotta vuota con la primitiva, la versione
    /// saliva, e lo scrittore perdeva il confronto per niente.
    /// </summary>
    [Fact]
    public async Task ABuildWithNothingToWriteDoesNotRaiseTheRouteVersion()
    {
        foreach (var name in new[] { "P", "A", "Q" }) _plant.Device(name);
        _plant.Link("P", "Q");
        await PlantPersistence.SaveDoorMappingsAsync(_plant["A"].ItemGuid,
            [new DoorMapping { ItemId = _plant["A"].ItemGuid, DoorCode = "B1", CellId = 11, Side = "A" }]);
        await PlantPersistence.SaveDoorMappingsAsync(_plant["Q"].ItemGuid,
            [new DoorMapping { ItemId = _plant["Q"].ItemGuid, DoorCode = "B2", CellId = 12, Side = "A" }]);
        var mission = new PathMission
        {
            PositionItemId = _plant["P"].ItemGuid,
            DestinationItemId = _plant["Q"].ItemGuid,
            PickUpDoorCode = "B1",
            StorageDoorCode = "B2"
        };
        await mission.PositionWork.WaitAsync(Ceiling);
        Assert.Equal(MissionTypes.None, mission.MissionType);
        var seen = mission.RouteVersion;
        var computedBefore = new MissionStep
        {
            StepId = Guid.NewGuid(),
            MissionId = mission.MissionId,
            Date = mission.Date,
            FromNodeId = _plant["P"].ItemGuid,
            ToNodeId = _plant["Q"].ItemGuid
        };

        Assert.False(await mission.BuildPathToDestination().WaitAsync(Ceiling));
        Assert.Equal(MissionTypes.None, mission.MissionType);
        Assert.Empty(mission.MissionSteps);

        Assert.Equal(PathMission.RouteWriteOutcome.Written, mission.TryWriteRoute(seen, [computedBefore]));
        AssertCoherent(mission, "P", "Q");
    }

    // ---------------------------------------------------------------------------------------
    // Scene
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// U collegato a P, P ai magazzini S7 e S8, con le porte A7 e A8 bloccate al deposito. Su U un
    /// <c>PickUpAndStoreAgain</c> verso P con la porta di deposito A7 e la porta di prelievo A5, fra le
    /// attive e senza rotta; il motore e' fermo.
    /// </summary>
    private async Task<PathMission> APickUpAndStoreAgainArrivingOnP(bool directToS7 = true)
    {
        foreach (var name in new[] { "U", "P" }) _plant.Device(name);
        foreach (var name in new[] { "S7", "S8" }) _plant.Device(name, DeviceTypes.Storage);
        _plant.Link("U", "P");
        if (directToS7) _plant.Link("P", "S7");
        _plant.Link("P", "S8");

        await PlantPersistence.SaveDoorMappingsAsync(_plant["S7"].ItemGuid,
            [new DoorMapping { ItemId = _plant["S7"].ItemGuid, DoorCode = DoorOfS7, CellId = 7, Side = "A" }]);
        await PlantPersistence.SaveDoorMappingsAsync(_plant["S8"].ItemGuid,
            [new DoorMapping { ItemId = _plant["S8"].ItemGuid, DoorCode = DoorOfS8, CellId = 8, Side = "A" }]);
        await DbInfo.SetCellLockedForStoring(DoorOfS7);
        await DbInfo.SetCellLockedForStoring(DoorOfS8);
        Assert.True(await DbInfo.GetDoorLockedForStoring(DoorOfS7));
        Assert.True(await DbInfo.GetDoorLockedForStoring(DoorOfS8));

        var mission = await AMissionOn("U", towards: "P");
        mission.MissionType = MissionTypes.PickUpAndStoreAgain;
        mission.StorageDoorCode = DoorOfS7;
        mission.PickUpDoorCode = PickUpDoor;
        await ActiveInStop(mission);
        return mission;
    }

    /// <summary>
    /// Lo step U->P che ha portato l'UDC su P, ancora in volo: il completamento ha gia' lanciato il carico
    /// ma non ha ancora tolto lo step eseguito, e la sorgente U ne tiene il gettone.
    /// </summary>
    private MissionStep TheStepThatBroughtItInFlight(PathMission mission)
    {
        var executed = new MissionStep
        {
            StepId = Guid.NewGuid(),
            MissionId = mission.MissionId,
            Date = mission.Date,
            FromNodeId = _plant["U"].ItemGuid,
            ToNodeId = _plant["P"].ItemGuid
        };
        Collections.MissionSteps.Instance.AddRange([executed]);
        _plant["U"].RunningStepId = executed.StepId;
        _plant["U"].Status = DeviceStatusTypes.Ready;
        Assert.Same(executed, mission.StepInFlight());
        return executed;
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

    /// <summary>Una missione su <paramref name="on"/> verso <paramref name="towards"/>, non ancora fra le attive.</summary>
    private async Task<PathMission> AMissionOn(string on, string towards)
    {
        var mission = new PathMission
        {
            PositionItemId = _plant[on].ItemGuid,
            DestinationItemId = _plant[towards].ItemGuid
        };
        await mission.PositionWork;

        // L'ingresso fra le attive si fa in Run: il passaggio sveglia i dispositivi con
        // AutoExecuteStep, che qui non devono partire da soli.
        foreach (var name in _plant.DeviceNames) _plant[name].AutoExecuteStep = false;
        Navigator.NavStatus = NavigatorStatusTypes.Run;
        return mission;
    }

    /// <summary>La missione fra le attive senza rotta, e poi il motore fermo.</summary>
    private static async Task ActiveInStop(PathMission mission)
    {
        ActiveMissions.Instance.Add(mission, buildRoute: false);
        Assert.Same(mission, ActiveMissions.Instance[mission.MissionId]);
        await mission.PositionWork;
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
    }

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

    /// <summary>I nomi della rotta in fila, dalla posizione: la rotta e' gia' coerente (<see cref="AssertCoherent"/>).</summary>
    private string[] RouteNames(PathMission mission)
    {
        var steps = mission.MissionSteps;
        var names = new List<string> { _plant.NameOf(mission.PositionItemId) };
        Guid? at = mission.PositionItemId;
        foreach (var _ in steps)
        {
            at = steps.Single(s => s.FromNodeId == at).ToNodeId;
            names.Add(_plant.NameOf(at));
        }

        return [.. names];
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
}
