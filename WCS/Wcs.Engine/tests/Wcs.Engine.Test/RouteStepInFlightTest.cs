using Wcs.Engine.Api;
using Wcs.Engine.Collections;
using Wcs.Engine.Items;
using Wcs.Engine.Persistence;
using Wcs.Engine.Test.Routing;
using Wcs.Wms.Contracts;
using Wcs.Wms.Contracts.Test;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// Lo step in volo si riconosce anche nel prelievo e nei movimenti di gruppo (voce 41, compito 8,
/// risposta 2 del proprietario dell'11/10): <c>PathMission.StepInFlight</c> guarda tutti i gettoni con
/// cui uno step parte, quello della sorgente, quello della destinazione (il prelievo) e quello del
/// gruppo, e lo rispettano la ricostruzione della rotta, lo spostamento e l'uscita dal motore. L'uscita
/// guarda e toglie dentro il lucchetto della rotta, in un colpo solo.
/// </summary>
/// <remarks>
/// <para>
/// Il motore sta in <c>Stop</c>, e in <c>Run</c> solo per l'ingresso fra le attive, con
/// <c>AutoExecuteStep</c> spento su tutti i dispositivi perche' il passaggio in Run li sveglia. Mai in
/// Recover.
/// </para>
/// <para>
/// Nessuna attesa a tempo: chi deve fermarsi si ferma in una cucitura, e l'altro lavora li' su un
/// thread suo, che la prova aspetta fermo sul lucchetto della rotta o finito. I tetti di 30 s fanno
/// fallire invece di appendere, e non decidono niente.
/// </para>
/// </remarks>
public sealed class RouteStepInFlightTest : IDisposable
{
    private const decimal LoadUnitId = 42;
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);

    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly QueuedDatabaseWriter<WcsWrite>? _previousWriter = Navigator.StateWriter;
    private readonly FakeWarehouse _warehouse = new();
    private readonly RoutingTestPlant _plant = new(NavigatorStatusTypes.Stop);

    public RouteStepInFlightTest()
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

    /// <summary>Chi tiene il gettone dello step P->R in volo.</summary>
    public enum Holder
    {
        /// <summary>R, la destinazione: il prelievo scrive il gettone su chi preleva.</summary>
        Destination,

        /// <summary>G, che tiene il gettone del movimento di gruppo di cui lo step fa parte.</summary>
        Group
    }

    /// <summary>
    /// Lo step P->R e' in volo con il gettone sulla destinazione R (il prelievo) o sul gruppo G, e la
    /// sorgente P non ne porta. La ricostruzione della rotta lo vede e non toglie niente: lo step resta,
    /// e con lui il gettone. Prima lo sguardo guardava solo la sorgente, e la ricostruzione toglieva lo
    /// step in volo, liberando a meta' movimento chi lo eseguiva.
    /// </summary>
    [Theory]
    [InlineData(Holder.Destination)]
    [InlineData(Holder.Group)]
    public async Task ARouteIsNotRebuiltUnderAStepInFlightOnItsDestinationOrItsGroup(Holder holder)
    {
        var (mission, step, token) = await AStepInFlightHeldBy(holder);

        Assert.False(await mission.BuildPathToDestination());

        TheStepIsStillInFlight(mission, step, holder, token);
    }

    /// <summary>
    /// Lo stesso step in volo, e l'operatore sposta la missione su Q: lo spostamento lo vede and waits
    /// for its end (item 54, decision 4; a refusal until then), la missione resta su P con lo step e il
    /// gettone. Prima la spostava su Q togliendo lo step in volo.
    /// </summary>
    [Theory]
    [InlineData(Holder.Destination)]
    [InlineData(Holder.Group)]
    public async Task AMissionIsNotMovedUnderAStepInFlightOnItsDestinationOrItsGroup(Holder holder)
    {
        var (mission, step, token) = await AStepInFlightHeldBy(holder);

        Assert.Equal(MissionMoveOutcome.Pending, MissionGateway.Move(mission.MissionId, _plant["Q"].ItemGuid));
        await PendingMissionChanges.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(PendingMissionChange.Position(_plant["Q"].ItemGuid), MissionGateway.PendingChangeOf(mission.MissionId));

        Assert.Equal(_plant["P"].ItemGuid, mission.PositionItemId);
        TheStepIsStillInFlight(mission, step, holder, token);
    }

    /// <summary>
    /// Lo stesso step in volo, e la missione esce dal motore: l'uscita lo vede e si rifiuta, la missione
    /// resta fra le attive con lo step e il gettone. Prima usciva togliendo lo step in volo.
    /// </summary>
    [Theory]
    [InlineData(Holder.Destination)]
    [InlineData(Holder.Group)]
    public async Task AMissionDoesNotLeaveUnderAStepInFlightOnItsDestinationOrItsGroup(Holder holder)
    {
        var (mission, step, token) = await AStepInFlightHeldBy(holder);

        Assert.False(await ActiveMissions.Instance.Remove(mission).WaitAsync(Ceiling));

        Assert.Same(mission, ActiveMissions.Instance[mission.MissionId]);
        TheStepIsStillInFlight(mission, step, holder, token);
    }

    /// <summary>
    /// La missione esce dal motore: l'uscita ha visto che nessuno step e' in volo e sta per togliere la
    /// missione e la rotta; intanto P avvia lo step P->Q, prende il posto su Q e arriva al gettone. Lo
    /// sguardo e l'uscita stanno sotto il lucchetto della rotta, come il gettone: l'avvio aspetta, trova
    /// la rotta tolta e rinuncia, e nessun comando parte per lo step di una missione uscita. Con lo
    /// sguardo e l'uscita separati il gettone si scriveva in mezzo, il comando partiva, e l'uscita toglieva
    /// lo step in volo.
    /// </summary>
    [Fact]
    public async Task AStepThatStartsWhileTheMissionLeavesDoesNotSetOff()
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

        var leaving = Task.Run(() => ActiveMissions.Instance.Remove(mission));
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

        Assert.True(await leaving.WaitAsync(Ceiling));
        Assert.True(starter.Join(Ceiling));

        Assert.Null(ActiveMissions.Instance[mission.MissionId]);
        Assert.DoesNotContain(step, MissionSteps.Instance.List);
        Assert.Null(step.RunningItem);
        Assert.NotEqual(DeviceStatusTypes.Moving, _plant["P"].Status);
        Assert.Equal(Guid.Empty, _plant["Q"].WaitingStepId);
        Assert.Equal(DeviceStatusTypes.Ready, _plant["Q"].Status);
    }

    /// <summary>
    /// La rimozione dal motore ha visto che nessuno step e' in volo; prima del secondo sguardo, sotto il
    /// lucchetto della rotta, P avvia lo step P->Q e ne scrive il gettone. La rimozione lo vede li' e
    /// rinuncia: la missione resta fra le attive, con lo step in volo, e la rimozione risponde no (il ramo
    /// «partito mentre usciva», rilievo M149 della revisione finale della voce 41). Senza il secondo
    /// sguardo l'uscita toglieva la missione e lo step in volo.
    /// </summary>
    [Fact]
    public async Task AStepThatStartedBetweenTheTwoLooksOfTheRemovalKeepsTheMission()
    {
        var mission = await AMissionOnPTowardsQ();
        var step = _plant.PlannedStep(mission, "P", "Q");
        mission.LeavePauseForTesting = () =>
        {
            mission.LeavePauseForTesting = null;
            _plant["P"].StartPutToMOVE_LU();
        };

        Assert.False(await ActiveMissions.Instance.Remove(mission).WaitAsync(Ceiling));

        Assert.Same(_plant["P"], step.RunningItem);
        Assert.Equal(step.StepId, _plant["P"].RunningStepId);
        Assert.Same(mission, ActiveMissions.Instance[mission.MissionId]);
        Assert.Same(step, Assert.Single(mission.MissionSteps));
        Assert.Same(step, mission.StepInFlight());
    }

    // ---------------------------------------------------------------------------------------
    // Scene
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// P collegato a R e a Q, G un dispositivo a parte; su P una missione verso R con lo step P->R in
    /// volo, il cui gettone lo tiene R (il prelievo, R in <c>Loading</c>) o G (il gruppo, G in
    /// <c>Moving</c>, con il gettone del gruppo sullo step). P e' <c>Ready</c> e non porta niente.
    /// </summary>
    private async Task<(PathMission Mission, MissionStep Step, Guid Token)> AStepInFlightHeldBy(Holder holder)
    {
        foreach (var name in new[] { "P", "R", "Q", "G" }) _plant.Device(name);
        _plant.Link("P", "R");
        _plant.Link("P", "Q");
        foreach (var name in _plant.DeviceNames) _plant[name].Status = DeviceStatusTypes.Ready;

        var mission = await MissionOn("P", towards: "R");
        var step = _plant.PlannedStep(mission, "P", "R");

        Guid token;
        if (holder == Holder.Destination)
        {
            token = step.StepId;
            _plant["R"].RunningStepId = token;
            _plant["R"].Status = DeviceStatusTypes.Loading;
        }
        else
        {
            token = Guid.NewGuid();
            step.GroupedMovementId = token;
            _plant["G"].RunningStepId = token;
            _plant["G"].Status = DeviceStatusTypes.Moving;
        }

        Assert.Equal(Guid.Empty, _plant["P"].RunningStepId);
        Assert.Equal(token, _plant[holder == Holder.Destination ? "R" : "G"].RunningStepId);
        return (mission, step, token);
    }

    /// <summary>Lo step e' ancora l'unico della missione, e chi ne tiene il gettone lo tiene ancora.</summary>
    private void TheStepIsStillInFlight(PathMission mission, MissionStep step, Holder holder, Guid token)
    {
        Assert.Same(step, Assert.Single(mission.MissionSteps));
        Assert.Equal(token, _plant[holder == Holder.Destination ? "R" : "G"].RunningStepId);
        Assert.Same(step, mission.StepInFlight());
    }

    /// <summary>
    /// P collegato a Q, tutti e due <c>Ready</c>; su P una missione verso Q, fra le attive e senza
    /// rotta.
    /// </summary>
    private async Task<PathMission> AMissionOnPTowardsQ()
    {
        foreach (var name in new[] { "P", "Q" }) _plant.Device(name);
        _plant.Link("P", "Q");
        _plant["P"].Status = DeviceStatusTypes.Ready;
        _plant["Q"].Status = DeviceStatusTypes.Ready;
        var mission = await MissionOn("P", towards: "Q");
        Assert.Same(mission, _plant["P"].LoadedLu);
        return mission;
    }

    /// <summary>
    /// Una missione su <paramref name="on"/> verso <paramref name="towards"/>, fra le attive e senza
    /// rotta; il motore e' fermo. L'ingresso si fa in Run, con <c>AutoExecuteStep</c> spento.
    /// </summary>
    private async Task<PathMission> MissionOn(string on, string towards)
    {
        var mission = new PathMission
        {
            PositionItemId = _plant[on].ItemGuid,
            DestinationItemId = _plant[towards].ItemGuid,
            LuId = LoadUnitId,
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
}
