using System.Reflection;
using Wcs.Engine.Collections;
using Wcs.Engine.ORM;
using Wcs.Engine.Persistence;
using Wcs.Wms.Contracts;
using Wcs.Wms.Contracts.Test;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// Il giro delle celle da segnare piene (<see cref="CellsToMarkFull"/>, risposta 3 del proprietario del
/// 9/10) nei casi che la prova del giro con il gestionale muto non tocca: il rifiuto del gestionale, un
/// guasto che non viene dal gestionale, il motore che esce da Run mentre un ritentativo e' in volo, e
/// l'elenco dimenticato fra una prova e l'altra (revisione finale del quarto giro).
/// </summary>
/// <remarks>
/// Le porte A6 e A8 entrano nell'elenco come dopo un 67 con il gestionale muto
/// (<see cref="CellsToMarkFull.AddAsync"/>), a motore fermo; un giro delle rotte in Run le ritenta. Il
/// freno ha un orologio finto: nessuna attesa vera. Le righe del registro si contano sulla console
/// (<see cref="RecordingConsole"/>), che il motore scrive in modo sincrono: «una sola riga» si puo'
/// asserire appena il ritentativo e' finito.
/// </remarks>
public sealed class CellsToMarkFullRetryTest : IDisposable
{
    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly FakeWarehouse _warehouse = new();
    private readonly ManualClock _clock = new();
    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly QueuedDatabaseWriter<WcsWrite>? _previousWriter = Navigator.StateWriter;
    private readonly NavigatorStatusTypes _previousNavStatus = Navigator.NavStatus;
    private readonly RetryBackoff _previousRetries = CellsToMarkFull.Retries;

    public CellsToMarkFullRetryTest()
    {
        Navigator.Store = _database;
        Navigator.StateWriter = null;
        Navigator.Warehouse = _warehouse;
        ReservationsOfMissionsClosedOnReload.Forget();
        CellsToMarkFull.Forget();
        CellsToMarkFull.Retries = new RetryBackoff(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(5), _clock);
        RoutesToBuild.Instance.Items = new();
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
    }

    public void Dispose()
    {
        RoutesToBuild.Instance.Items = new();
        CellsToMarkFull.Forget();
        CellsToMarkFull.Retries = _previousRetries;
        ReservationsOfMissionsClosedOnReload.Forget();
        Navigator.NavStatus = _previousNavStatus;
        Navigator.StateWriter = _previousWriter;
        Navigator.Store = null;
        Navigator.Warehouse = _previousWarehouse;
        _database.Dispose();
    }

    /// <summary>
    /// Il gestionale rifiuta il segno della A6 con un 4xx: e' una risposta, e riprovare non la cambia. La
    /// A6 esce dall'elenco e dal database del WCS con una riga di avviso che dice il rifiuto, la A8 e'
    /// segnata piena, e il giro dopo, a freno scaduto, non chiede piu' niente. La riga dice il codice
    /// della risposta, e non «il gestionale non ha risposto», che e' il messaggio dell'eccezione e diceva
    /// il contrario di quello che e' successo (revisione finale del quinto giro della voce 39).
    /// </summary>
    [Fact]
    public async Task AFourHundredFromTheWarehouseTakesTheDoorOutWithAWarning()
    {
        using var log = new RecordingConsole();
        await CellsToMarkFull.AddAsync(["A6", "A8"]);
        var calls = 0;
        _warehouse.Fault = operation => operation == nameof(IWarehouse.SetCellLockStateAsync) && ++calls == 1
            ? FakeWarehouse.Rejection(operation)
            : null;

        Navigator.NavStatus = NavigatorStatusTypes.Run;
        await OneTurnOfTheRouteLoopAndItsRetry();

        Assert.Equal([("A8", CellLockState.Full)], _warehouse.CellLockStateChanges);
        Assert.Empty(CellsToMarkFull.Doors);
        Assert.Empty(TheCellsToMarkFullInTheDatabase());
        var rejection = Assert.Single(log.Lines, line => line.Contains("ha rifiutato il segno di cella piena della porta A6") &&
                                                         line.Contains("non si ritenta"));
        Assert.Contains("(risposta 400)", rejection);
        Assert.DoesNotContain("non ha risposto", rejection);

        _clock.Advance(TimeSpan.FromSeconds(5));
        await OneTurnOfTheRouteLoopAndItsRetry();
        Assert.Equal(2, LockCalls());
    }

    /// <summary>
    /// Il segno della A6 cade per un guasto che non viene dal gestionale (qui un
    /// <see cref="InvalidOperationException"/>, come quello del gestionale non configurato): non e' il
    /// gestionale muto. Una riga di avviso lo dice, con il tipo dell'eccezione; la A6 e la A8 restano
    /// nell'elenco, il freno conta la mancata (un giro prima che scada non chiede niente), e lo stesso
    /// guasto al giro dopo non riscrive la riga. Prima ogni eccezione valeva «il gestionale non risponde
    /// ancora», senza nessuna riga: un difetto si ritentava ogni 5 s per sempre senza traccia.
    /// </summary>
    [Fact]
    public async Task AFailureThatIsNotTheWarehouseIsWrittenInTheLogAndTriedAgainWithTheBrake()
    {
        using var log = new RecordingConsole();
        await CellsToMarkFull.AddAsync(["A6", "A8"]);
        _warehouse.Fault = operation => operation == nameof(IWarehouse.SetCellLockStateAsync)
            ? new InvalidOperationException("un difetto del WCS")
            : null;

        Navigator.NavStatus = NavigatorStatusTypes.Run;
        await OneTurnOfTheRouteLoopAndItsRetry();

        Assert.Equal(1, LockCalls());
        Assert.Equal(["A6", "A8"], CellsToMarkFull.Doors);
        Assert.Equal(["A6", "A8"], TheCellsToMarkFullInTheDatabase());
        Assert.Single(log.Lines, IsTheWarningOfTheFailure);

        await OneTurnOfTheRouteLoopAndItsRetry();
        Assert.Equal(1, LockCalls());

        _clock.Advance(TimeSpan.FromMilliseconds(200));
        await OneTurnOfTheRouteLoopAndItsRetry();
        Assert.Equal(2, LockCalls());
        Assert.Single(log.Lines, IsTheWarningOfTheFailure);

        static bool IsTheWarningOfTheFailure(string line) =>
            line.Contains("porta A6") && line.Contains("guasto del WCS") &&
            line.Contains("InvalidOperationException: un difetto del WCS");
    }

    /// <summary>
    /// Mentre il gestionale segna la A6, il motore esce da Run (qui in Stop; la ricarica passa da
    /// Recover allo stesso modo). Il ritentativo in volo si ferma: la A6, segnata, esce dall'elenco, e la
    /// A8 non si chiede, e resta nell'elenco e nel database del WCS. Tornato in Run, il primo giro la
    /// segna. Prima il ritentativo finiva la sua copia dell'elenco anche fuori da Run, e chiamava il
    /// gestionale durante la ricarica (risposta 3 del 9/10: nessuna chiamata al gestionale in Recover).
    /// </summary>
    [Fact]
    public async Task ARetryInFlightStopsWhenTheEngineLeavesRun()
    {
        await CellsToMarkFull.AddAsync(["A6", "A8"]);
        _warehouse.Fault = operation =>
        {
            if (operation == nameof(IWarehouse.SetCellLockStateAsync) && LockCalls() == 1)
                Navigator.NavStatus = NavigatorStatusTypes.Stop;
            return null;
        };

        Navigator.NavStatus = NavigatorStatusTypes.Run;
        await OneTurnOfTheRouteLoopAndItsRetry();

        Assert.Equal(NavigatorStatusTypes.Stop, Navigator.NavStatus);
        Assert.Equal([("A6", CellLockState.Full)], _warehouse.CellLockStateChanges);
        Assert.Equal(["A8"], CellsToMarkFull.Doors);
        Assert.Equal(["A8"], TheCellsToMarkFullInTheDatabase());

        _warehouse.Fault = null;
        Navigator.NavStatus = NavigatorStatusTypes.Run;
        await OneTurnOfTheRouteLoopAndItsRetry();

        Assert.Equal([("A6", CellLockState.Full), ("A8", CellLockState.Full)], _warehouse.CellLockStateChanges);
        Assert.Empty(CellsToMarkFull.Doors);
    }

    /// <summary>
    /// Le prove condividono lo stato statico, e ognuna dimentica l'elenco prima di cominciare
    /// (<see cref="CellsToMarkFull.Forget"/>). Un ritentativo della prova di prima ancora in volo (qui:
    /// l'elenco si dimentica mentre il gestionale segna la A6) non chiede piu' la A8 e non tocca l'elenco
    /// nuovo: la A9 della prova dopo resta. Prima andava avanti contro il gestionale e l'elenco della
    /// prova dopo.
    /// </summary>
    [Fact]
    public async Task ARetryInFlightStopsWhenTheListIsForgotten()
    {
        await CellsToMarkFull.AddAsync(["A6", "A8"]);
        Task? stale = null;
        _warehouse.Fault = operation =>
        {
            if (operation == nameof(IWarehouse.SetCellLockStateAsync) && LockCalls() == 1)
            {
                // Il ritentativo in volo, preso prima che Forget lo dimentichi: Retry lo assegna sotto il
                // lucchetto, che il ritentativo prende prima di chiamare il gestionale.
                stale = CellsToMarkFull.Retried;
                CellsToMarkFull.Forget();
                CellsToMarkFull.AddAsync(["A9"]).GetAwaiter().GetResult();
            }

            return null;
        };

        Navigator.NavStatus = NavigatorStatusTypes.Run;
        await TheRouteLoopTurns();
        await CellsToMarkFull.Retried.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(stale);
        await stale.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, LockCalls());
        Assert.Equal(["A9"], CellsToMarkFull.Doors);
    }

    private int LockCalls() => _warehouse.Calls.Count(call => call == nameof(IWarehouse.SetCellLockStateAsync));

    /// <summary>Le porte delle celle da segnare piene nel database del WCS, nell'ordine di arrivo.</summary>
    private List<string> TheCellsToMarkFullInTheDatabase()
    {
        using var context = _database.CreateContext();
        return [.. context.CellsToMarkFull.OrderBy(row => row.Id).Select(row => row.DoorCode)];
    }

    /// <summary>Un giro del ciclo delle rotte, come lo fa il motore ogni 10 ms, e il ritentativo che ha fatto partire.</summary>
    private static async Task OneTurnOfTheRouteLoopAndItsRetry()
    {
        await TheRouteLoopTurns();
        await CellsToMarkFull.Retried.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static async Task TheRouteLoopTurns()
    {
        var method = typeof(Navigator).GetMethod("BuildRoutePath", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        await ((Task)method!.Invoke(null, null)!).WaitAsync(TimeSpan.FromSeconds(30));
    }
}
