using System.Reflection;
using Log.Interface;
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
/// Il freno del giro delle rotte (<c>Navigator.RoutePathRetries</c>, voce B70) cresce davvero (voce
/// 39, compito 1): il conto dei tentativi di una missione si dimentica quando la ricerca riesce,
/// quando l'operatore sposta la missione o ne cambia la destinazione, o quando la missione esce dal
/// motore, non prima di ogni ricerca. Fino alla voce 39 il giro lo
/// dimenticava prima di ogni ricerca della cella: l'attesa restava 200 ms, una missione bloccata
/// interrogava il gestionale circa cinque volte al secondo, e l'avviso al decimo tentativo non
/// partiva mai. Cresce solo per le ricerche fallite: una missione che aspetta uno step in volo
/// aspetta un passo fisso, come prima.
/// </summary>
/// <remarks>
/// <para>
/// La missione sta su U, da dove un percorso verso D non c'e': ogni ricerca fallisce, e senza
/// gestionale, perche' U non ha modalita' di deposito ne' destinazione fissa e D non e' un
/// magazzino. Il navigatore e' in <c>Run</c>, perche' il giro gira solo li'; la missione si
/// aggiunge in <c>Recover</c> perche' <c>ActiveMissions.Add</c> esce subito in <c>Stop</c>. Il giro
/// non gira da solo: la prova ne fa uno alla volta (<c>Navigator.BuildRoutePath</c>) e aspetta la
/// ricerca che avvia.
/// </para>
/// <para>
/// Il freno ha un orologio finto: fra un giro e l'altro la prova lo porta avanti dell'attesa che il
/// freno ha fissato, cosi' il giro dopo cerca di nuovo invece di riaccodare e basta. Nessuna attesa
/// a tempo: i tetti servono a far fallire invece di appendere. L'avviso del freno va nel registro
/// del navigatore, cioe' nello storico, qui in memoria e senza scrittore a coda: la riga e' scritta
/// prima che la ricerca finisca.
/// </para>
/// </remarks>
public sealed class RouteLoopBrakeTest : IDisposable
{
    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly RetryBackoff _previousRetries = Navigator.RoutePathRetries;
    private readonly QueuedDatabaseWriter<HistoryWrite>? _previousHistoryWriter = Navigator.HistoryWriter;
    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly WcsHistoryDatabase _history = WcsHistoryDatabase.InMemory();
    private readonly RoutingTestPlant _plant = new();
    private readonly FakeWarehouse _warehouse = new();
    private readonly ManualClock _clock = new();

    public RouteLoopBrakeTest()
    {
        Navigator.Store = _database;
        Navigator.History = _history;
        Navigator.HistoryWriter = null;
        Navigator.Warehouse = _warehouse;
        Navigator.RoutePathRetries = new RetryBackoff(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(5), _clock);
        RoutesToBuild.Instance.Items = new();
    }

    public void Dispose()
    {
        RoutesToBuild.Instance.Items = new();
        _plant.Dispose();
        Navigator.RoutePathRetries = _previousRetries;
        Navigator.Warehouse = _previousWarehouse;
        Navigator.HistoryWriter = _previousHistoryWriter;
        Navigator.History = null;
        Navigator.Store = null;
        _history.Dispose();
        _database.Dispose();
    }

    /// <summary>
    /// Dieci ricerche fallite di seguito: l'attesa prima della successiva cresce di 200 ms a ogni
    /// fallimento, e al decimo, non prima, il registro del navigatore ha l'avviso, uno solo, che
    /// dice dopo quanti tentativi falliti e perche'. Fino alla voce 39 le dieci attese erano tutte di
    /// 200 ms e l'avviso non c'era; fino alla voce 51 diceva «da 10 giri», ma il numero e' quello dei
    /// fallimenti, non dei giri del ciclo.
    /// </summary>
    [Fact]
    public async Task TheWaitGrowsFromOneFailedSearchToTheNextAndTheTenthIsLogged()
    {
        var mission = await AMissionOnUWithoutARouteToD();
        var waits = new List<double>();

        for (var turn = 1; turn <= 10; turn++)
        {
            Assert.Empty(TheBrakeWarnings(mission));
            await ASearchThatFails(mission);
            waits.Add(TheBrakeRunsOut(mission).TotalMilliseconds);
        }

        Assert.Equal([200, 400, 600, 800, 1000, 1200, 1400, 1600, 1800, 2000], waits);
        var warning = Assert.Single(TheBrakeWarnings(mission));
        Assert.Contains("dopo 10 tentativi falliti (nessuna posizione di deposito trovata)", warning);
    }

    /// <summary>
    /// Mentre la ricerca fallita e' in volo qualcuno rimette la missione in coda (come fa un dispositivo
    /// che riaccoda la sua UDC caricata, <c>Standard.AddLoadedLuToRouteToBuildQueue</c>; non il deposito
    /// che aspetta la conferma, <c>PathMission.MayStartTheDeposit</c>, che con la ricerca in volo non
    /// accoda): il fallimento si
    /// conta lo stesso, e la missione resta in coda una volta sola. Il giro dopo, prima che il freno
    /// scada, la riaccoda senza cercare. Fino alla voce 51 il fallimento non si contava se la missione
    /// era gia' in coda, e il giro dopo la ripescava subito, con una domanda al gestionale in piu'
    /// (revisione finale del primo giro della voce 39, C14).
    /// </summary>
    /// <remarks>
    /// La missione su U va nel magazzino S, con la cella A6 prenotata per lei e il deposito U->S come
    /// prossimo passo: il navigatore chiede al gestionale se la cella e' ancora sua, e il gestionale
    /// non risponde. La ricerca finisce senza rotta (il deposito aspetta), e la domanda e' il punto in
    /// cui la prova la rimette in coda, dentro la ricerca.
    /// </remarks>
    [Fact]
    public async Task AFailedSearchIsCountedAlsoWhenTheMissionWasQueuedAgainMeanwhile()
    {
        _plant.Device("U").AutoExecuteStep = false;
        _plant.Device("S", DeviceTypes.Storage).AutoExecuteStep = false;
        _plant.Link("U", "S");
        Assert.Equal(NavigatorStatusTypes.Recover, Navigator.NavStatus);
        var mission = _plant.MissionAt("U", "S");
        await mission.PositionWork;
        mission.StorageDoorCode = "A6";
        mission.StorageCellReserved = true;
        _plant.PlannedStep(mission, "U", "S");
        Navigator.NavStatus = NavigatorStatusTypes.Run;
        var asked = 0;
        _warehouse.Fault = operation =>
        {
            if (operation != nameof(IWarehouse.GetStorageReservationAsync)) return null;
            if (++asked == 1) RoutesToBuild.Instance.Enqueue(mission);
            return new WarehouseUnavailableException(operation);
        };

        // La console raccoglie le eccezioni che il giro e la ricerca catturano: il messaggio della
        // domanda mancata le riporta (voce 41, compito 7).
        using var console = new RecordingConsole();
        var before = TheLoopNow(mission);

        await OneSearch(mission, justBeforeTheTurn: () => before = TheLoopNow(mission));

        if (asked != 1) Assert.Fail(TheWarehouseWasNotAskedOnce(asked, mission, before, console));
        Assert.Equal(["U", "S"], _plant.Names(mission.MissionSteps));
        Assert.Single(RoutesToBuild.Instance.Items, queued => ReferenceEquals(queued, mission));
        Assert.True(Navigator.RoutePathRetries.IsWaiting(mission.MissionId));

        before = TheLoopNow(mission);
        await OneTurnOfTheRouteLoop().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(Navigator.CellSearchInFlight(mission.MissionId));
        if (asked != 1) Assert.Fail(TheWarehouseWasNotAskedOnce(asked, mission, before, console));
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
    }

    /// <summary>
    /// Dopo tre ricerche fallite il percorso c'e' (U collegato a D): la ricerca riesce, la missione
    /// ha la rotta e non torna in coda, e il freno ha dimenticato i suoi fallimenti: il prossimo
    /// conta da uno. I tre fallimenti sono stati contati davvero, con le loro attese crescenti: senza,
    /// il conto finale passerebbe anche se il freno non avesse mai contato niente.
    /// </summary>
    [Fact]
    public async Task ASearchThatSucceedsForgetsTheFailures()
    {
        var mission = await AMissionOnUWithoutARouteToD();
        var waits = new List<double>();
        for (var turn = 1; turn <= 3; turn++)
        {
            await ASearchThatFails(mission);
            waits.Add(TheBrakeRunsOut(mission).TotalMilliseconds);
        }

        Assert.Equal([200, 400, 600], waits);
        _plant.Link("U", "D");
        await OneSearch(mission);

        Assert.Equal(["U", "D"], _plant.Names(mission.MissionSteps));
        Assert.Null(RoutesToBuild.Instance[mission.MissionId]);
        Assert.Equal(1, Navigator.RoutePathRetries.RegisterFailure(mission.MissionId));
    }

    /// <summary>
    /// La missione esce dal motore dopo tre ricerche fallite, contate davvero: il freno dimentica i
    /// suoi fallimenti, che non servono piu' a nessuno, invece di tenerli per tutta la vita del
    /// processo, e la sua richiesta esce dalla coda delle rotte.
    /// </summary>
    [Fact]
    public async Task AMissionThatLeavesTheEngineLeavesNoFailuresBehind()
    {
        var mission = await AMissionOnUWithoutARouteToD();
        var waits = new List<double>();
        for (var turn = 1; turn <= 3; turn++)
        {
            await ASearchThatFails(mission);
            waits.Add(TheBrakeRunsOut(mission).TotalMilliseconds);
        }

        Assert.Equal([200, 400, 600], waits);
        Assert.True(await ActiveMissions.Instance.Remove(mission));

        Assert.Null(RoutesToBuild.Instance[mission.MissionId]);
        Assert.Equal(1, Navigator.RoutePathRetries.RegisterFailure(mission.MissionId));
    }

    /// <summary>
    /// Una missione uscita dal motore che qualcuno rimette in coda dopo l'uscita (la fine di una
    /// ricerca in volo, un deposito che aspetta la conferma): il giro la scarta, senza cercare e senza
    /// contare, e non la rimette in coda. Fino alla revisione finale della voce 39 girava per sempre,
    /// contando di nuovo i fallimenti che l'uscita aveva dimenticato.
    /// </summary>
    [Fact]
    public async Task AMissionThatLeftTheEngineIsDroppedByTheNextTurn()
    {
        var mission = await AMissionOnUWithoutARouteToD();
        await ASearchThatFails(mission);
        TheBrakeRunsOut(mission);
        Assert.True(await ActiveMissions.Instance.Remove(mission));

        await OneSearch(mission);

        Assert.Null(RoutesToBuild.Instance[mission.MissionId]);
        Assert.Null(Navigator.CellSearchInFlight(mission.MissionId));
        Assert.Equal(1, Navigator.RoutePathRetries.RegisterFailure(mission.MissionId));
    }

    /// <summary>
    /// Il freno cresce solo per le ricerche fallite (scelta tecnica della specifica, 2/10). La missione
    /// su U ha la rotta U->D con lo step in volo, come la mette in coda il ramo
    /// <c>BuildRouteToStoreForcing</c>: dieci giri la trovano cosi', e ogni volta torna in coda con
    /// l'attesa fissa di un passo, 200 ms, la rotta intatta e nessun avviso; finito lo step, il primo
    /// fallimento conta da uno. Con il conto che cresceva anche qui (compito 1) le attese erano 200,
    /// 400 ... 2000 ms, la ricerca dopo lo step partiva fino a 5 s dopo la sua fine, e al decimo giro
    /// partiva l'avviso di una missione «non costruibile».
    /// </summary>
    [Fact]
    public async Task AMissionWaitingForAStepInFlightKeepsTheFlatWait()
    {
        var mission = await AMissionOnUWithoutARouteToD();
        _plant.Link("U", "D");
        var step = _plant.PlannedStep(mission, "U", "D");
        _plant["U"].RunningStepId = step.StepId;
        Assert.Same(step, mission.StepInFlight());
        var waits = new List<double>();

        for (var turn = 1; turn <= 10; turn++)
        {
            await OneSearch(mission);

            Assert.Same(step, Assert.Single(mission.MissionSteps));
            Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
            waits.Add(TheBrakeRunsOut(mission).TotalMilliseconds);
        }

        Assert.Equal([200, 200, 200, 200, 200, 200, 200, 200, 200, 200], waits);
        Assert.Empty(TheBrakeWarnings(mission));
        Assert.Equal(1, Navigator.RoutePathRetries.RegisterFailure(mission.MissionId));
    }

    // ---------------------------------------------------------------------------------------
    // L'operatore cambia la situazione: i fallimenti di prima non contano piu'
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Dopo tre ricerche fallite verso D, con l'attesa dopo la terza ancora in corso, l'operatore
    /// cambia la destinazione in E, che da U si raggiunge: il commit ha la rotta U->E, e il freno
    /// dimentica i fallimenti verso D. La richiesta rimasta in coda si riprende al giro dopo, e la
    /// ricerca trova la rotta e la toglie dalla coda; prima la riprendeva solo allo scadere di
    /// un'attesa che non riguardava la destinazione nuova (qui 600 ms, fino a 5 s per una missione
    /// ferma da trenta giri), e per una missione mandata in un magazzino la conferma della cella, e
    /// con lei il deposito, aspettavano altrettanto.
    /// </summary>
    [Fact]
    public async Task AnOperatorsChangeOfDestinationForgetsTheFailures()
    {
        var mission = await AMissionOnUWithoutARouteToD();
        await ThreeSearchesFailAndTheBrakeIsRunning(mission);
        _plant.Device("E").AutoExecuteStep = false;
        _plant.Link("U", "E");

        Assert.Equal(MissionDestinationOutcome.Changed,
            (await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["E"].ItemGuid)).Outcome);
        Assert.Equal(["U", "E"], _plant.Names(mission.MissionSteps));
        Assert.False(Navigator.RoutePathRetries.IsWaiting(mission.MissionId));

        await OneSearch(mission);

        Assert.Equal(["U", "E"], _plant.Names(mission.MissionSteps));
        Assert.Null(RoutesToBuild.Instance[mission.MissionId]);
    }

    /// <summary>
    /// Dopo tre ricerche fallite da U, con l'attesa dopo la terza ancora in corso, l'operatore
    /// sposta la missione su V, da dove lo spostamento non le costruisce la rotta: V ha una
    /// destinazione fissa o una modalita' di deposito, e la rotta la fa il giro come prima, oppure da
    /// V un percorso verso D non c'e'. I fallimenti erano di U: il freno li dimentica anche qui, non
    /// solo quando lo spostamento costruisce la rotta. La richiesta in coda si riprende al giro dopo,
    /// non allo scadere dell'attesa di U, e un fallimento da V conta da uno: con il conto di U una
    /// missione ferma da nove giri scriveva l'avviso del decimo al primo fallimento da V.
    /// Item 54 (decision 1 of the owner of 13/10): a storing mode no longer touches a mission bound for
    /// a device that is not a storage, and the move builds its route at once; in the row with the
    /// storing mode D is therefore a storage, the mission a deposit as the row means. Until item 54 D
    /// was an automation device in every row.
    /// </summary>
    [Theory]
    [InlineData("fixed destination", NavigatorStatusTypes.Run)]
    [InlineData("store mode", NavigatorStatusTypes.Run)]
    [InlineData("no route", NavigatorStatusTypes.Run)]
    [InlineData("fixed destination", NavigatorStatusTypes.Stop)]
    public async Task AMoveForgetsTheFailuresAlsoWhenItDoesNotBuildTheRoute(string situation, NavigatorStatusTypes status)
    {
        var mission = await AMissionOnUWithoutARouteToD(situation == "store mode" ? DeviceTypes.Storage : DeviceTypes.Automation);
        await ThreeSearchesFailAndTheBrakeIsRunning(mission);
        var v = _plant.Device("V");
        v.AutoExecuteStep = false;
        switch (situation)
        {
            case "fixed destination":
                _plant.Link("V", "D");
                v.DestinationItemId = _plant["D"].ItemGuid;
                break;
            case "store mode":
                _plant.Link("V", "D");
                v.LookWhereAllocateLu = LookWhereAllocateLuTypes.BuildRouteToStore;
                break;
            case "no route":
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(situation), situation, null);
        }
        Navigator.NavStatus = status;

        Assert.Equal(MissionMoveOutcome.Moved, MissionGateway.Move(mission.MissionId, v.ItemGuid));
        Assert.Empty(mission.MissionSteps);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);

        Assert.False(Navigator.RoutePathRetries.IsWaiting(mission.MissionId));
        Assert.Equal(1, Navigator.RoutePathRetries.RegisterFailure(mission.MissionId));
        await mission.PositionWork;
    }

    // ---------------------------------------------------------------------------------------
    // L'impianto e il giro
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// U e D senza giunzione fra loro, e su U una missione verso D, aggiunta in Recover con il primo
    /// posizionamento finito; poi il navigatore in Run. Nessun dispositivo esegue da solo
    /// (<c>AutoExecuteStep</c> spento): il giro, trovata la rotta, non sveglia nessuno.
    /// </summary>
    private async Task<PathMission> AMissionOnUWithoutARouteToD(DeviceTypes destinationType = DeviceTypes.Automation)
    {
        _plant.Device("U");
        _plant.Device("D", destinationType);
        foreach (var name in _plant.DeviceNames) _plant[name].AutoExecuteStep = false;

        Assert.Equal(NavigatorStatusTypes.Recover, Navigator.NavStatus);
        var mission = _plant.MissionAt("U", "D");
        Assert.Same(mission, _plant["U"].LoadedLu);
        await mission.PositionWork;

        Navigator.NavStatus = NavigatorStatusTypes.Run;
        return mission;
    }

    /// <summary>
    /// Un giro con la sola missione in coda, e la ricerca che avvia, fino alla fine: la coda e' un
    /// singleton che il lavoro di sfondo di altre prove puo' riempire, e un giro deve pescare questa
    /// missione. <paramref name="justBeforeTheTurn"/>, se c'e', guarda la coda appena riempita, prima
    /// del giro.
    /// </summary>
    private static async Task OneSearch(PathMission mission, Action? justBeforeTheTurn = null)
    {
        RoutesToBuild.Instance.Items = new();
        RoutesToBuild.Instance.Enqueue(mission);
        justBeforeTheTurn?.Invoke();

        await OneTurnOfTheRouteLoop().WaitAsync(TimeSpan.FromSeconds(10));

        // La ricerca prende il suo posto prima che il giro torni, e lo lascia alla fine: se non c'e'
        // piu', e' gia' finita.
        if (Navigator.CellSearchInFlight(mission.MissionId) is { } search)
            await search.WaitAsync(TimeSpan.FromSeconds(30));
    }

    /// <summary>Una ricerca che fallisce: la missione resta senza rotta, e il freno la rimette in coda.</summary>
    private static async Task ASearchThatFails(PathMission mission)
    {
        await OneSearch(mission);

        Assert.Empty(mission.MissionSteps);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
    }

    /// <summary>
    /// Tre ricerche fallite, e l'attesa fissata dopo la terza ancora in corso: una richiesta della
    /// missione ripescata adesso dal giro si riaccoderebbe senza cercare.
    /// </summary>
    private async Task ThreeSearchesFailAndTheBrakeIsRunning(PathMission mission)
    {
        for (var turn = 1; turn <= 3; turn++)
        {
            await ASearchThatFails(mission);
            if (turn < 3) TheBrakeRunsOut(mission);
        }

        Assert.True(Navigator.RoutePathRetries.IsWaiting(mission.MissionId));
    }

    /// <summary>
    /// L'attesa che il freno ha fissato per la missione, dall'ora dell'orologio finto; l'orologio va
    /// avanti di tanto, cosi' il giro dopo cerca di nuovo.
    /// </summary>
    private TimeSpan TheBrakeRunsOut(PathMission mission)
    {
        var wait = Navigator.RoutePathRetries.NextAttemptAt(mission.MissionId) - _clock.GetUtcNow();
        _clock.Advance(wait);
        Assert.False(Navigator.RoutePathRetries.IsWaiting(mission.MissionId));
        return wait;
    }

    /// <summary>Gli avvisi del freno per la missione nel registro del navigatore.</summary>
    private List<string> TheBrakeWarnings(PathMission mission)
    {
        using var context = _history.CreateContext();
        return context.NavigatorLog
            .Where(row => row.Status == (short)LogEntryType.Warning)
            .AsEnumerable()
            .Select(row => row.Description ?? string.Empty)
            .Where(description => description.StartsWith("BuildRoutePath:") && description.Contains(mission.MissionId.ToString()))
            .ToList();
    }

    /// <summary>
    /// Che cosa il giro trova prima di partire: le due guardie con cui esce senza pescare niente (lo
    /// stato del navigatore e un giro gia' in corso, <c>Navigator._isBuildingPath</c>), la coda che
    /// pesca, e i fallimenti gia' contati per la missione. Dopo il giro non lo dicono piu'.
    /// </summary>
    private static LoopBefore TheLoopNow(PathMission mission)
    {
        var building = typeof(Navigator).GetField("_isBuildingPath", BindingFlags.NonPublic | BindingFlags.Static);
        return new LoopBefore(
            $"navigatore {Navigator.NavStatus}, giro gia' in corso {building?.GetValue(null) ?? "?"}",
            TheQueue(mission),
            Navigator.RoutePathRetries.Failures(mission.MissionId));
    }

    /// <summary>Lo stato prima di un giro, per il messaggio della domanda mancata.</summary>
    private sealed record LoopBefore(string Guards, string Queue, int Failures);

    /// <summary>
    /// Il messaggio della domanda al gestionale mancata (o ripetuta): la strada che il giro ha preso,
    /// per quanto si vede da fuori, e lo stato della missione. La prova e' caduta una volta su due
    /// nella suite del motore, con 0 domande, e mai da sola (rapporto del compito 5 della voce 41): le
    /// cause compatibili sono un'altra voce in testa alla coda, un'eccezione prima della domanda, o il
    /// segno della cella caduto. Qui si leggono tutte e tre, senza cambiare che cosa la prova controlla.
    /// </summary>
    /// <remarks>
    /// Come leggerlo: senza un fallimento contato nel giro e senza ricerca in volo il giro non ha
    /// cercato questa missione (una guardia, un'altra voce in testa alla coda, il freno in attesa: lo
    /// stato di prima del giro), o la ricerca e' riuscita (la rotta, nello stato della missione); con
    /// un fallimento contato nel giro e senza domanda la ricerca e' partita e si e' fermata prima
    /// (un'eccezione, nelle righe della console, o il segno della cella caduto, nello stato della
    /// missione).
    /// </remarks>
    private string TheWarehouseWasNotAskedOnce(int asked, PathMission mission, LoopBefore before, RecordingConsole console)
    {
        var failures = Navigator.RoutePathRetries.Failures(mission.MissionId);
        var waiting = Navigator.RoutePathRetries.IsWaiting(mission.MissionId);
        var inFlight = Navigator.CellSearchInFlight(mission.MissionId);
        var road = inFlight != null
            ? "la ricerca e' ancora in volo"
            : failures > before.Failures
                ? "ricerca partita e contata come fallita in questo giro (finita senza rotta, o con un'eccezione)"
                : "nessun fallimento contato in questo giro: il giro non ha cercato questa missione, o la ricerca e' riuscita";

        return string.Join(Environment.NewLine,
            $"Il gestionale doveva essere interrogato una volta, e lo e' stato {asked} volte.",
            $"Strada del giro: {road}.",
            $"Prima del giro: {before.Guards}; coda [{before.Queue}]; {before.Failures} fallimenti.",
            $"Dopo il giro: navigatore {Navigator.NavStatus}; coda [{TheQueue(mission)}]; " +
            $"{failures} fallimenti, in attesa {waiting}.",
            $"Missione: attiva {ReferenceEquals(ActiveMissions.Instance[mission.MissionId], mission)}, " +
            $"su {_plant.NameOf(mission.PositionItemId)}, rotta [{Tolerant(() => string.Join(", ", _plant.Names(mission.MissionSteps)))}], " +
            $"segno della cella prenotata {mission.StorageCellReserved}, porta {mission.StorageDoorCode ?? "null"}, " +
            $"segno dell'operatore {mission.OperatorDestination}.",
            $"Chiamate al gestionale: [{Tolerant(() => string.Join(", ", _warehouse.Calls.ToArray()))}].",
            $"Eccezioni catturate (console): [{Tolerant(() => string.Join(" | ", console.Lines.Where(line => line.StartsWith("Error On"))))}].");
    }

    /// <summary>La coda delle rotte, voce per voce: questa missione, un'altra, o un nullo.</summary>
    private static string TheQueue(PathMission mission) => Tolerant(() => string.Join(", ",
        RoutesToBuild.Instance.Items.ToArray().Select(queued => queued switch
        {
            null => "null",
            _ when ReferenceEquals(queued, mission) => "questa missione",
            _ => $"un'altra missione {queued.MissionId}"
        })));

    /// <summary>
    /// Una lettura per il messaggio: le liste lette li' non sono protette (le chiamate del gestionale
    /// finto, la console), e una lettura che cade non deve togliere al messaggio il resto.
    /// </summary>
    private static string Tolerant(Func<string> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            return $"(lettura fallita: {ex.GetType().Name})";
        }
    }

    /// <summary>Un giro del ciclo delle rotte, come lo fa il motore ogni 10 ms.</summary>
    private static Task OneTurnOfTheRouteLoop()
    {
        var method = typeof(Navigator).GetMethod("BuildRoutePath", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (Task)method!.Invoke(null, null)!;
    }
}
