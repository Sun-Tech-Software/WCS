using Wcs.Engine.Collections;
using Wcs.Engine.Items;
using Wcs.Engine.Persistence;
using Wcs.Engine.Test.Routing;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// L'avvio di uno step e la ricostruzione della rotta (voce 41, compito 5, corsa 5 della ricognizione,
/// una specifica interna, non pubblicata): P avvia lo step
/// P->Q della missione, prende il posto su Q e solo dopo scrive il gettone; in mezzo una ricostruzione
/// (l'ingresso fra le attive, l'arrivo, il giro delle rotte) non vedeva lo step in volo e lo toglieva, e
/// il comando partiva lo stesso per uno step che non c'era piu': l'UDC su Q per il campo, su P per il
/// motore. Ora delle due l'una: o il gettone c'e' gia' e la ricostruzione non toglie niente, o la rotta
/// e' tolta prima e l'avvio rinuncia, restituisce il posto e non manda niente.
/// </summary>
/// <remarks>
/// <para>
/// Il motore sta in <c>Stop</c>, e in <c>Run</c> solo per l'ingresso fra le attive, con
/// <c>AutoExecuteStep</c> spento su tutti i dispositivi perche' il passaggio in Run li sveglia. Mai in
/// Recover. L'avvio si chiama a mano (<c>StartPutToMOVE_LU</c>), come lo chiama la strategia
/// <c>Standard</c>; senza collegamento al campo, un comando partito si riconosce dallo step marcato
/// (<c>RunningItem</c>), da P in <c>Moving</c> e da Q in <c>Busy</c>.
/// </para>
/// <para>
/// Nessuna attesa a tempo: chi deve fermarsi si ferma in una cucitura, e l'altro lavora li' (su un
/// thread suo, che la prova aspetta finito, o fermo sul lucchetto della rotta). I tetti di 30 s fanno
/// fallire invece di appendere, e non decidono niente.
/// </para>
/// </remarks>
public sealed class RouteStepStartTest : IDisposable
{
    private const decimal LoadUnitId = 42;
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);

    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly QueuedDatabaseWriter<WcsWrite>? _previousWriter = Navigator.StateWriter;
    private readonly RoutingTestPlant _plant = new(NavigatorStatusTypes.Stop);

    public RouteStepStartTest()
    {
        Navigator.Store = _database;
        Navigator.StateWriter = null;
        RoutesToBuild.Instance.Items = new();
    }

    public void Dispose()
    {
        RoutesToBuild.Instance.Items = new();
        _plant.Dispose();
        Navigator.StateWriter = _previousWriter;
        Navigator.Store = null;
        _database.Dispose();
    }

    /// <summary>
    /// Corsa 5. P ha preso il posto su Q per lo step P->Q, e prima che scriva il gettone la rotta della
    /// missione si ricostruisce (lo step P->Q tolto, uno nuovo al suo posto). L'avvio trova la rotta
    /// cambiata e rinuncia: nessun comando per lo step tolto, il posto su Q restituito, Q di nuovo
    /// <c>Ready</c>, e P puo' avviare lo step della rotta nuova. Prima il gettone si scriveva per lo step
    /// tolto e il comando partiva: Q restava impegnata e P in <c>Moving</c>.
    /// </summary>
    [Fact]
    public async Task ARouteRebuiltBetweenTheSeatAndTheTokenDoesNotSetOffTheRemovedStep()
    {
        var mission = await AMissionOnPTowardsQ();
        var step = _plant.PlannedStep(mission, "P", "Q");

        var rebuilt = false;
        _plant["P"].StepStartPauseForTesting = () =>
        {
            _plant["P"].StepStartPauseForTesting = null;
            Assert.True(_plant["Q"].IsWaitingForStep(step.StepId));

            // La ricostruzione vera, su un altro thread: l'avvio e' fermo qui senza lucchetti.
            Assert.False(Task.Run(() => mission.BuildPathToDestination()).WaitAsync(Ceiling).GetAwaiter().GetResult());
            rebuilt = true;
        };

        _plant["P"].StartPutToMOVE_LU();

        Assert.True(rebuilt);
        AssertTheRemovedStepDidNotSetOff(mission, step);
        TheNewStepSetsOff(mission);
    }

    /// <summary>
    /// L'altro ordine della corsa 5. La ricostruzione ha visto che nessuno step e' in volo e sta per
    /// togliere la rotta; intanto P avvia lo step P->Q, prende il posto su Q e arriva al gettone. Lo
    /// sguardo e la rimozione stanno sotto il lucchetto della rotta, come il gettone: l'avvio aspetta la
    /// rimozione, trova la rotta cambiata e rinuncia. Prima lo sguardo e la rimozione erano separati: il
    /// gettone si scriveva in mezzo, e la rimozione toglieva uno step in volo con il comando gia' partito.
    /// </summary>
    [Fact]
    public async Task AStepThatStartsWhileTheRouteIsBeingRemovedDoesNotSetOff()
    {
        var mission = await AMissionOnPTowardsQ();
        var step = _plant.PlannedStep(mission, "P", "Q");

        using var removalReached = new ManualResetEventSlim();
        using var removalReleased = new ManualResetEventSlim();
        mission.RouteRemovalPauseForTesting = () =>
        {
            mission.RouteRemovalPauseForTesting = null;
            removalReached.Set();
            removalReleased.Wait(Ceiling);
        };

        var rebuild = Task.Run(() => mission.BuildPathToDestination());
        Assert.True(removalReached.Wait(Ceiling));

        // Fermo sul lucchetto della rotta, o finito: e' la condizione, non un tempo. La cucitura del gettone
        // dice che l'avvio ha gia' letto versione e step ed e' alla porta del lucchetto: solo dopo di lei
        // lo stato WaitSleepJoin vuol dire il lucchetto. Prima bastava WaitSleepJoin, che puo' scattare
        // prima (l'avvio del thread), e allora la prova passava senza la corsa (giro finale delle
        // correzioni della voce 41).
        var atTheToken = false;
        mission.StepTokenPauseForTesting = () => Volatile.Write(ref atTheToken, true);
        var starter = new Thread(() => _plant["P"].StartPutToMOVE_LU()) { IsBackground = true };
        starter.Start();

        Assert.True(SpinWait.SpinUntil(
            () => !starter.IsAlive ||
                  Volatile.Read(ref atTheToken) && (starter.ThreadState & ThreadState.WaitSleepJoin) != 0, Ceiling));
        removalReleased.Set();

        Assert.False(await rebuild.WaitAsync(Ceiling));
        Assert.True(starter.Join(Ceiling));

        AssertTheRemovedStepDidNotSetOff(mission, step);
        TheNewStepSetsOff(mission);
    }

    /// <summary>
    /// La corsa 5 con il rigetto dell'operatore al posto della ricostruzione. Il rigetto ha visto che
    /// nessuno step e' in volo e sta per togliere la rotta; intanto P avvia lo step P->Q, prende il posto
    /// su Q e arriva al gettone. Lo sguardo e la rimozione del rigetto stanno sotto il lucchetto della
    /// rotta (<c>RemoveTheRouteUnlessAStepIsInFlight</c>): l'avvio aspetta, trova la rotta cambiata e
    /// rinuncia, e il rigetto si fa, verso R. Con lo sguardo e la rimozione separati il gettone si
    /// scriveva in mezzo, e il rigetto toglieva uno step in volo con il comando gia' partito (rilievo M99
    /// della revisione finale della voce 41).
    /// </summary>
    [Fact]
    public async Task AStepThatStartsWhileTheRejectionRemovesTheRouteDoesNotSetOff()
    {
        _plant.Device("R").Status = DeviceStatusTypes.Ready;
        var mission = await AMissionOnPTowardsQ();
        _plant.Link("P", "R");
        _plant["P"].RejectDestinationUserRequest = _plant["R"].ItemGuid;
        var step = _plant.PlannedStep(mission, "P", "Q");

        using var removalReached = new ManualResetEventSlim();
        using var removalReleased = new ManualResetEventSlim();
        mission.RouteRemovalPauseForTesting = () =>
        {
            mission.RouteRemovalPauseForTesting = null;
            removalReached.Set();
            removalReleased.Wait(Ceiling);
        };

        var rejection = Task.Run(() => mission.RejectTo(PathMission.RejectionTypes.DefaultNavigatorRejectionItem));
        Assert.True(removalReached.Wait(Ceiling));

        // Fermo sul lucchetto della rotta, o finito: e' la condizione, non un tempo. La cucitura del gettone
        // dice che l'avvio ha gia' letto versione e step ed e' alla porta del lucchetto: solo dopo di lei
        // lo stato WaitSleepJoin vuol dire il lucchetto. Prima bastava WaitSleepJoin, che puo' scattare
        // prima (l'avvio del thread), e allora la prova passava senza la corsa (giro finale delle
        // correzioni della voce 41).
        var atTheToken = false;
        mission.StepTokenPauseForTesting = () => Volatile.Write(ref atTheToken, true);
        var starter = new Thread(() => _plant["P"].StartPutToMOVE_LU()) { IsBackground = true };
        starter.Start();

        Assert.True(SpinWait.SpinUntil(
            () => !starter.IsAlive ||
                  Volatile.Read(ref atTheToken) && (starter.ThreadState & ThreadState.WaitSleepJoin) != 0, Ceiling));
        removalReleased.Set();

        var rejected = await rejection.WaitAsync(Ceiling);
        Assert.True(starter.Join(Ceiling));

        Assert.DoesNotContain(step, MissionSteps.Instance.List);
        Assert.Null(step.RunningItem);
        Assert.NotEqual(DeviceStatusTypes.Moving, _plant["P"].Status);
        Assert.Equal(Guid.Empty, _plant["Q"].WaitingStepId);
        Assert.Equal(DeviceStatusTypes.Ready, _plant["Q"].Status);
        Assert.True(rejected);
        Assert.Equal(MissionTypes.Rejection, mission.MissionType);
        var toR = Assert.Single(mission.MissionSteps);
        Assert.Equal(_plant["P"].ItemGuid, toR.FromNodeId);
        Assert.Equal(_plant["R"].ItemGuid, toR.ToNodeId);
        ChangeDestinationStepStartsTest.NoStepStartIsLeftCounted(mission);

        _plant["P"].StartPutToMOVE_LU();

        Assert.Equal(toR.StepId, _plant["P"].RunningStepId);
        Assert.True(_plant["R"].IsWaitingForStep(toR.StepId));
    }

    /// <summary>
    /// Senza nessuno in mezzo l'avvio fa quello che faceva: il gettone su P, il posto su Q, Q in
    /// <c>Busy</c> e P in <c>Moving</c>. E la versione della rotta sale con il gettone: chi aveva
    /// calcolato una rotta prima che lo step partisse non la scrive sopra lo step in volo.
    /// </summary>
    [Fact]
    public async Task AStepThatStartsAloneTakesItsTokenAndARouteComputedBeforeLoses()
    {
        var mission = await AMissionOnPTowardsQ();
        var step = _plant.PlannedStep(mission, "P", "Q");
        var computedBefore = mission.RouteVersion;

        _plant["P"].StartPutToMOVE_LU();

        Assert.Equal(step.StepId, _plant["P"].RunningStepId);
        Assert.True(_plant["Q"].IsWaitingForStep(step.StepId));
        Assert.Equal(DeviceStatusTypes.Busy, _plant["Q"].Status);
        Assert.Equal(DeviceStatusTypes.Moving, _plant["P"].Status);
        Assert.Same(step, mission.StepInFlight());

        var other = new MissionStep
        {
            StepId = Guid.NewGuid(),
            MissionId = mission.MissionId,
            Date = DateTime.Now,
            FromNodeId = _plant["P"].ItemGuid,
            ToNodeId = _plant["Q"].ItemGuid
        };
        Assert.Equal(PathMission.RouteWriteOutcome.Lost, mission.TryWriteRoute(computedBefore, [other]));
        Assert.Same(step, Assert.Single(mission.MissionSteps));
        ChangeDestinationStepStartsTest.NoStepStartIsLeftCounted(mission);
    }

    /// <summary>
    /// La rinuncia al gettone non lascia niente dell'avvio (risposta 4 del proprietario dell'11/10): la
    /// missione era in <c>Stop</c> con il movimento di gruppo G, e P era iscritto fra chi aspetta Q.
    /// L'avvio prende il posto su Q, la rotta si ricostruisce, e l'avvio rinuncia: la missione resta in
    /// <c>Stop</c> con G, P resta iscritto su Q, e il posto su Q e' restituito. Prima la rinuncia lasciava
    /// la missione in <c>Run</c> (scritto sul database), il movimento di gruppo azzerato e P fuori dalla
    /// lista di Q.
    /// </summary>
    [Fact]
    public async Task ARenouncedStartLeavesTheMissionAndTheWaitingListAsTheyWere()
    {
        var mission = await AMissionOnPTowardsQ();
        var step = _plant.PlannedStep(mission, "P", "Q");
        var group = Guid.NewGuid();
        mission.Status = MissionStatusTypes.Stop;
        mission.GroupedMovementId = group;
        Assert.True(_plant["Q"].AddDeviceReadyToTransferLu(_plant["P"]));

        var rebuilt = false;
        _plant["P"].StepStartPauseForTesting = () =>
        {
            _plant["P"].StepStartPauseForTesting = null;
            Assert.False(Task.Run(() => mission.BuildPathToDestination()).WaitAsync(Ceiling).GetAwaiter().GetResult());
            rebuilt = true;
        };

        _plant["P"].StartPutToMOVE_LU();

        Assert.True(rebuilt);
        AssertTheRemovedStepDidNotSetOff(mission, step);
        Assert.Equal(MissionStatusTypes.Stop, mission.Status);
        Assert.Equal(group, mission.GroupedMovementId);
        Assert.Contains(_plant["P"], _plant["Q"].DevicesReadyToTransferLu);
    }

    /// <summary>
    /// La rinuncia risveglia P, che riprova con la rotta nuova (risposta 4 del proprietario dell'11/10):
    /// P esegue in <c>Run</c>, avvia lo step P->Q, e fra il posto e il gettone la rotta si ricostruisce;
    /// l'avvio rinuncia dentro l'esecuzione di P, e alla fine di quella P esegue di nuovo e avvia lo step
    /// della rotta nuova. Prima nessuno lo risvegliava: chi lo richiamava mentre era ancora in esecuzione
    /// era rifiutato, e P aspettava il prossimo richiamo.
    /// </summary>
    [Fact]
    public async Task ARenouncedStartWakesTheDeviceThatStartsTheNewRoute()
    {
        var mission = await AMissionOnPTowardsQ();
        var step = _plant.PlannedStep(mission, "P", "Q");

        _plant["P"].StepStartPauseForTesting = () =>
        {
            _plant["P"].StepStartPauseForTesting = null;
            Assert.False(Task.Run(() => mission.BuildPathToDestination()).WaitAsync(Ceiling).GetAwaiter().GetResult());
        };

        Navigator.NavStatus = NavigatorStatusTypes.Run;
        try
        {
            Assert.True(await _plant["P"].ExecuteStep().WaitAsync(Ceiling));
            Assert.Null(step.RunningItem);
            var rebuilt = Assert.Single(mission.MissionSteps);
            Assert.NotSame(step, rebuilt);

            // La seconda esecuzione parte sul pool: si aspetta la condizione, con il tetto. Lo step e'
            // marcato per ultimo, subito prima del comando.
            Assert.True(SpinWait.SpinUntil(() => rebuilt.RunningItem != null, Ceiling));
            Assert.Same(_plant["P"], rebuilt.RunningItem);
            Assert.Equal(rebuilt.StepId, _plant["P"].RunningStepId);
            Assert.True(_plant["Q"].IsWaitingForStep(rebuilt.StepId));
        }
        finally
        {
            // Lo step marcato non vuol dire l'esecuzione finita: la seconda esecuzione puo' ancora
            // mandare il comando e uscire dal suo finally. Si aspetta che il lavoro lanciato sul pool
            // (RunObserved, tenuto da BackgroundWork) sia finito prima di tornare a Stop e di smontare
            // l'impianto (rilievo M151 della revisione finale della voce 41).
            var idle = BackgroundWork.WaitUntilIdle(Ceiling);
            Navigator.NavStatus = NavigatorStatusTypes.Stop;
            Assert.True(idle, "il lavoro lanciato dalla rinuncia non e' finito");
        }
    }

    /// <summary>
    /// Il gettone confronta anche lo step, non solo la versione: lo step P->Q esce dalla rotta senza che la
    /// versione salga (come lo toglie chi scrive fuori dalla primitiva, per esempio la rimozione per gruppo),
    /// e l'avvio che ha letto la versione prima non scrive il gettone. E' la seconda meta' del controllo di
    /// <c>TryTakeTheStepToken</c> (rilievo M98 della revisione finale della voce 41).
    /// </summary>
    [Fact]
    public async Task AStepRemovedWithoutRaisingTheVersionGetsNoToken()
    {
        var mission = await AMissionOnPTowardsQ();
        var step = _plant.PlannedStep(mission, "P", "Q");
        var seen = mission.RouteVersion;

        Assert.True(MissionSteps.Instance.Remove(step));
        Assert.Equal(seen, mission.RouteVersion);

        var tokenWritten = false;
        Assert.False(mission.TryTakeTheStepToken(seen, step, () => tokenWritten = true));
        Assert.False(tokenWritten);
    }

    // ---------------------------------------------------------------------------------------
    // Scene
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// P collegato a Q, tutti e due <c>Ready</c>; su P una missione verso Q, fra le attive e senza
    /// rotta. L'ingresso si fa in Run, con <c>AutoExecuteStep</c> spento; poi il motore e' fermo.
    /// </summary>
    private async Task<PathMission> AMissionOnPTowardsQ()
    {
        foreach (var name in new[] { "P", "Q" }) _plant.Device(name);
        _plant.Link("P", "Q");
        _plant["P"].Status = DeviceStatusTypes.Ready;
        _plant["Q"].Status = DeviceStatusTypes.Ready;

        var mission = new PathMission
        {
            PositionItemId = _plant["P"].ItemGuid,
            DestinationItemId = _plant["Q"].ItemGuid,
            LuId = LoadUnitId,
            MissionType = MissionTypes.MovingToItem
        };
        await mission.PositionWork;

        foreach (var name in _plant.DeviceNames) _plant[name].AutoExecuteStep = false;
        Navigator.NavStatus = NavigatorStatusTypes.Run;
        ActiveMissions.Instance.Add(mission, buildRoute: false);
        Assert.Same(mission, ActiveMissions.Instance[mission.MissionId]);
        Assert.Same(mission, _plant["P"].LoadedLu);
        await mission.PositionWork;
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        return mission;
    }

    /// <summary>
    /// Lo step tolto non e' partito: nessun comando (lo step non e' marcato, P non e' in
    /// <c>Moving</c>), il posto su Q restituito e Q di nuovo <c>Ready</c>, nessun gettone su P. La
    /// missione ha la rotta nuova P->Q, che non e' in volo, e nessun avvio resta contato.
    /// </summary>
    private void AssertTheRemovedStepDidNotSetOff(PathMission mission, MissionStep removed)
    {
        Assert.DoesNotContain(removed, MissionSteps.Instance.List);
        Assert.Null(removed.RunningItem);
        Assert.NotEqual(DeviceStatusTypes.Moving, _plant["P"].Status);
        Assert.False(_plant["Q"].IsWaitingForStep(removed.StepId));
        Assert.Equal(Guid.Empty, _plant["Q"].WaitingStepId);
        Assert.Equal(DeviceStatusTypes.Ready, _plant["Q"].Status);
        Assert.Equal(Guid.Empty, _plant["P"].RunningStepId);

        var rebuilt = Assert.Single(mission.MissionSteps);
        Assert.Equal(_plant["P"].ItemGuid, rebuilt.FromNodeId);
        Assert.Equal(_plant["Q"].ItemGuid, rebuilt.ToNodeId);
        Assert.Null(mission.StepInFlight());
        ChangeDestinationStepStartsTest.NoStepStartIsLeftCounted(mission);
    }

    /// <summary>Lo step della rotta nuova parte da P: il posto su Q era stato restituito.</summary>
    private void TheNewStepSetsOff(PathMission mission)
    {
        var rebuilt = Assert.Single(mission.MissionSteps);

        _plant["P"].StartPutToMOVE_LU();

        Assert.Equal(rebuilt.StepId, _plant["P"].RunningStepId);
        Assert.True(_plant["Q"].IsWaitingForStep(rebuilt.StepId));
        Assert.Same(_plant["P"], rebuilt.RunningItem);
    }
}
