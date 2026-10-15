using Log.Interface;
using Microsoft.Data.Sqlite;
using Wcs.Engine.Collections;
using Wcs.Engine.ORM;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Wcs.Wms.Contracts;
using Wcs.Wms.Contracts.Test;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// Voce 45, compito 3: la coda durevole del fatto «l'estrazione prelevata e' diventata un deposito»
/// e il suo dispatcher (<see cref="PendingPickedExtractionReportDispatcher"/>). Con il gestionale
/// muto la riga resta e si ritenta; con una risposta, qualunque esito porti, la riga esce e l'esito
/// va nel registro; un riavvio la ritrova; e la riapertura di una riga di estrazione aspetta che
/// la sua «estratta» sia arrivata. Nessuna attesa a tempo: il freno ha un orologio a mano.
/// </summary>
public sealed class PickedExtractionReportDispatchTest : IDisposable
{
    private static readonly DateTimeOffset FixedUtc = new(2026, 10, 11, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid MissionId = new("4b0e5a1c-0045-4000-8000-000000000001");
    private static readonly Guid OperationId = new("0b0e5a1c-0045-4000-8000-0000000000aa");
    private static readonly Guid AssignmentGuid = new("5a1e0045-0000-4000-8000-000000000045");

    /// <summary>Il tetto delle attese di una riga di registro: fa fallire invece di appendere.</summary>
    private static readonly TimeSpan LogCap = TimeSpan.FromSeconds(30);

    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly WcsDatabase? _previousStore = Navigator.Store;
    private readonly QueuedDatabaseWriter<WcsWrite>? _previousStateWriter = Navigator.StateWriter;
    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly NavigatorStatusTypes _previousNavStatus = Navigator.NavStatus;
    private readonly bool _previousDebugLog = Navigator.ActivateDebugLog;
    private readonly FakeWarehouse _warehouse = new();
    private readonly ManualClock _clock = new();

    public PickedExtractionReportDispatchTest()
    {
        ActiveMissions.Instance.RemoveAll(_ => true);
        Navigator.Store = _database;
        Navigator.StateWriter = null;
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        Navigator.Warehouse = _warehouse;
    }

    public void Dispose()
    {
        ActiveMissions.Instance.RemoveAll(_ => true);
        Navigator.ActivateDebugLog = _previousDebugLog;
        Navigator.NavStatus = _previousNavStatus;
        Navigator.Warehouse = _previousWarehouse;
        Navigator.StateWriter = _previousStateWriter;
        Navigator.Store = _previousStore;
        _database.Dispose();
    }

    private static PendingPickedExtractionReport Report(Guid operationId, decimal assignmentId = 902m,
        decimal extractionRowId = 8045m, decimal loadUnitId = 4545m) => new()
    {
        OperationId = operationId,
        MissionId = MissionId,
        LoadUnitId = loadUnitId,
        AssignmentId = assignmentId,
        AssignmentGuid = AssignmentGuid,
        ExtractionRowId = extractionRowId,
        UserId = "KazamControl.Process",
        Reason = "The operator sent the picked-up extraction to a storage (cell A8): it became a deposit",
        CreatedAtUtc = FixedUtc
    };

    private static Task Queue(PendingPickedExtractionReport report) =>
        ExtractionQueue.WriteAsync(context =>
        {
            PendingPickedExtractionReportStore.Add(context, report);
            return Task.CompletedTask;
        });

    private static async Task<List<PendingPickedExtractionReport>> AllRows()
    {
        await using var context = Navigator.Store!.CreateContext();
        return [.. context.PendingPickedExtractionReports.OrderBy(row => row.Id)];
    }

    /// <summary>
    /// Il dispatcher manda la riga con i suoi campi, letterali, e dopo la risposta la toglie.
    /// </summary>
    [Fact]
    public async Task TheRowIsSentWithItsFieldsAndLeavesTheQueueAfterTheAnswer()
    {
        await Queue(Report(OperationId));

        await new PendingPickedExtractionReportDispatcher(_clock).TryDispatchAsync();

        var sent = Assert.Single(_warehouse.PickedExtractionsBecameDeposit);
        Assert.Equal(new PickedExtractionBecameDepositRequest(OperationId, MissionId, 4545m, 902m, AssignmentGuid, 8045m,
            "KazamControl.Process", "The operator sent the picked-up extraction to a storage (cell A8): it became a deposit"), sent);
        Assert.Empty(await AllRows());
    }

    /// <summary>
    /// Il gestionale muto: la riga resta, con il tentativo contato, e il freno non la rimanda prima
    /// del suo tempo; passato il tempo il dispatcher ritenta, e il gestionale che risponde la toglie.
    /// </summary>
    [Fact]
    public async Task WithTheWarehouseMuteTheRowStaysAndIsTriedAgain()
    {
        await Queue(Report(OperationId));
        _warehouse.Unavailable = true;
        var dispatcher = new PendingPickedExtractionReportDispatcher(_clock);

        await dispatcher.TryDispatchAsync();

        var kept = Assert.Single(await AllRows());
        Assert.Equal(1, kept.AttemptCount);
        Assert.NotNull(kept.LastError);
        Assert.False(kept.Parked);
        Assert.Single(_warehouse.Calls, nameof(IWarehouse.ReportPickedExtractionBecameDepositAsync));

        await dispatcher.TryDispatchAsync();
        Assert.Single(_warehouse.Calls, nameof(IWarehouse.ReportPickedExtractionBecameDepositAsync));

        _clock.Advance(TimeSpan.FromSeconds(2));
        await dispatcher.TryDispatchAsync();
        Assert.Equal(2, _warehouse.Calls.Count(call => call == nameof(IWarehouse.ReportPickedExtractionBecameDepositAsync)));
        Assert.Equal(2, Assert.Single(await AllRows()).AttemptCount);

        _warehouse.Unavailable = false;
        _clock.Advance(TimeSpan.FromSeconds(4));
        await dispatcher.TryDispatchAsync();

        Assert.Equal(OperationId, Assert.Single(_warehouse.PickedExtractionsBecameDeposit).OperationId);
        Assert.Empty(await AllRows());
    }

    /// <summary>
    /// Il gestionale non cablato e' come un gestionale muto: la riga resta.
    /// </summary>
    [Fact]
    public async Task WithoutAWarehouseTheRowStays()
    {
        await Queue(Report(OperationId));
        Navigator.Warehouse = null;

        await new PendingPickedExtractionReportDispatcher(_clock).TryDispatchAsync();

        Assert.Equal(1, Assert.Single(await AllRows()).AttemptCount);
    }

    /// <summary>
    /// Ogni esito del gestionale e' una risposta: la riga esce in tutti i casi. Nel registro, una riga
    /// informativa se ha riaperto quello che la missione aveva servito; un avviso per «ordine gia'
    /// chiuso» e per «niente da riaprire» su una parte servita; «niente da riaprire» su una parte che
    /// la missione non aveva (assegnazione 0) e' la risposta attesa, e resta informativa.
    /// </summary>
    [Theory]
    [InlineData(902, PickedExtractionReopenOutcome.Reopened, PickedExtractionReopenOutcome.Reopened, LogEntryType.Information)]
    [InlineData(902, PickedExtractionReopenOutcome.OrderAlreadyClosed, PickedExtractionReopenOutcome.Reopened, LogEntryType.Warning)]
    [InlineData(902, PickedExtractionReopenOutcome.Reopened, PickedExtractionReopenOutcome.OrderAlreadyClosed, LogEntryType.Warning)]
    [InlineData(902, PickedExtractionReopenOutcome.NothingToReopen, PickedExtractionReopenOutcome.Reopened, LogEntryType.Warning)]
    [InlineData(902, PickedExtractionReopenOutcome.Reopened, PickedExtractionReopenOutcome.NothingToReopen, LogEntryType.Warning)]
    [InlineData(0, PickedExtractionReopenOutcome.NothingToReopen, PickedExtractionReopenOutcome.Reopened, LogEntryType.Information)]
    [InlineData(0, PickedExtractionReopenOutcome.OrderAlreadyClosed, PickedExtractionReopenOutcome.Reopened, LogEntryType.Warning)]
    public async Task EveryAnswerTakesTheRowOutAndGoesInTheLog(int assignmentId, PickedExtractionReopenOutcome assignment,
        PickedExtractionReopenOutcome extractionRow, LogEntryType expected)
    {
        Navigator.ActivateDebugLog = true;
        using var log = new RecordingLogWriter();
        await Queue(Report(OperationId, assignmentId: assignmentId));
        _warehouse.PickedExtractionBecameDepositAnswer = _ => new PickedExtractionBecameDepositResult(assignment, extractionRow, "nota 77");

        await new PendingPickedExtractionReportDispatcher(_clock).TryDispatchAsync();

        Assert.Empty(await AllRows());
        var prefix = $"Missione {MissionId}, UDC 4545: alla riapertura di quello che la missione serviva il gestionale risponde: " +
                     $"assegnazione {assignmentId} {assignment}, riga di estrazione 8045 {extractionRow} (nota 77)";
        await log.Wait((description, _) => description.StartsWith(prefix, StringComparison.Ordinal)).WaitAsync(LogCap);
        var line = Assert.Single(log.Lines, line => line.Description.StartsWith(prefix, StringComparison.Ordinal));
        Assert.Equal(expected, line.Level);
        Assert.Equal(expected == LogEntryType.Warning ? "; quello che non e' stato riaperto va guardato nel gestionale" : string.Empty,
            line.Description[prefix.Length..]);
    }

    /// <summary>
    /// Il riavvio: la riga scritta nel <c>wcs.db</c> su file c'e' ancora quando il database si riapre,
    /// e un dispatcher nuovo (quello del motore ripartito) la manda.
    /// </summary>
    [Fact]
    public async Task AfterAReloadTheRowIsStillThereAndIsSent()
    {
        var directory = Directory.CreateTempSubdirectory("wcs-picked-extraction-report-");
        WcsDatabase? database = null;
        try
        {
            database = WcsDatabase.Open(directory.FullName);
            Navigator.Store = database;
            _warehouse.Unavailable = true;
            await Queue(Report(OperationId));
            await new PendingPickedExtractionReportDispatcher(_clock).TryDispatchAsync();
            Assert.Single(await AllRows());

            database.Dispose();
            database = WcsDatabase.Open(directory.FullName);
            Navigator.Store = database;
            _warehouse.Unavailable = false;

            await new PendingPickedExtractionReportDispatcher(_clock).TryDispatchAsync();

            Assert.Equal(OperationId, Assert.Single(_warehouse.PickedExtractionsBecameDeposit).OperationId);
            Assert.Empty(await AllRows());
        }
        finally
        {
            Navigator.Store = null;
            database?.Dispose();
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// La riapertura della riga di estrazione 8045 aspetta che la sua «estratta» sia uscita dalla coda
    /// degli esiti: prima non parte, e un'altra riga senza esito in attesa passa lo stesso. Uscito
    /// l'esito, parte.
    /// </summary>
    [Fact]
    public async Task TheReopenWaitsForItsExtractedOutcome()
    {
        await ExtractionQueue.WriteAsync(context =>
        {
            context.PendingExtractionOutcomes.Add(new PendingExtractionOutcome
            {
                ExtractionRowId = 8045m, Status = ExtractionRowStatus.Extracted, WorkstationId = "KazamControl",
                CreatedAtUtc = FixedUtc
            });
            return Task.CompletedTask;
        });
        var other = new Guid("0b0e5a1c-0045-4000-8000-0000000000bb");
        await Queue(Report(OperationId));
        await Queue(Report(other, extractionRowId: 8046m));
        var dispatcher = new PendingPickedExtractionReportDispatcher(_clock);

        await dispatcher.TryDispatchAsync();

        Assert.Equal(other, Assert.Single(_warehouse.PickedExtractionsBecameDeposit).OperationId);
        Assert.Equal(OperationId, Assert.Single(await AllRows()).OperationId);

        await new PendingExtractionOutcomeDispatcher(_clock).TryDispatchAsync();
        await dispatcher.TryDispatchAsync();

        Assert.Equal([other, OperationId], _warehouse.PickedExtractionsBecameDeposit.Select(request => request.OperationId));
        Assert.Empty(await AllRows());
    }

    /// <summary>
    /// Decisione A del proprietario dell'11/10: la riapertura parte solo quando nessuna missione
    /// attiva porta piu' quell'UDC. Con l'UDC 4545 ancora sulla sua missione (il deposito del compito
    /// 3, il cambio cella del compito 4) la riga resta, senza tentativi contati, e un'altra UDC senza
    /// missione passa lo stesso; finita la missione, parte. Mandata prima, il gestionale rimetterebbe
    /// la riga di estrazione da servire mentre l'UDC e' ancora in missione, e il giro di assegnazione
    /// la rifarebbe «in estrazione» senza una riga di coda (rapporto del compito 4, sezione 2).
    /// </summary>
    [Fact]
    public async Task TheReopenWaitsUntilNoActiveMissionCarriesTheLoadUnit()
    {
        var other = new Guid("0b0e5a1c-0045-4000-8000-0000000000dd");
        await Queue(Report(OperationId, loadUnitId: 4545m));
        await Queue(Report(other, loadUnitId: 4546m));
        var carrying = new Items.PathMission { LuId = 4545m, LuCode = "UDC4545" };
        // Recover per l'aggiunta (in Stop ActiveMissions.Add esce subito), poi di nuovo Stop.
        Navigator.NavStatus = NavigatorStatusTypes.Recover;
        ActiveMissions.Instance.Add(carrying, buildRoute: false);
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        Assert.True(ActiveMissions.Instance.Contains(carrying));
        var dispatcher = new PendingPickedExtractionReportDispatcher(_clock);

        await dispatcher.TryDispatchAsync();

        Assert.Equal(other, Assert.Single(_warehouse.PickedExtractionsBecameDeposit).OperationId);
        var held = Assert.Single(await AllRows());
        Assert.Equal(OperationId, held.OperationId);
        Assert.Equal(0, held.AttemptCount);
        Assert.False(held.Parked);

        ActiveMissions.Instance.RemoveAll(mission => ReferenceEquals(mission, carrying));
        await dispatcher.TryDispatchAsync();

        Assert.Equal([other, OperationId], _warehouse.PickedExtractionsBecameDeposit.Select(request => request.OperationId));
        Assert.Empty(await AllRows());
    }

    /// <summary>
    /// Un rifiuto definitivo del gestionale (un 4xx): la riga resta parcheggiata con il motivo, non si
    /// ritenta, la riga dopo passa, e una riga di errore dice che l'ordine va sistemato a mano.
    /// </summary>
    [Fact]
    public async Task ARejectedRowIsParkedAndTheNextOneIsSent()
    {
        using var log = new RecordingLogWriter();
        var other = new Guid("0b0e5a1c-0045-4000-8000-0000000000cc");
        await Queue(Report(OperationId, loadUnitId: 4545m));
        await Queue(Report(other, loadUnitId: 4546m));
        _warehouse.Fault = operation =>
            operation == nameof(IWarehouse.ReportPickedExtractionBecameDepositAsync) &&
            _warehouse.Calls.Count(call => call == operation) == 1
                ? FakeWarehouse.Rejection(operation)
                : null;
        var dispatcher = new PendingPickedExtractionReportDispatcher(_clock);

        await dispatcher.TryDispatchAsync();

        Assert.Equal(other, Assert.Single(_warehouse.PickedExtractionsBecameDeposit).OperationId);
        var parked = Assert.Single(await AllRows());
        Assert.Equal(OperationId, parked.OperationId);
        Assert.True(parked.Parked);
        Assert.Equal(1, parked.AttemptCount);
        Assert.NotNull(parked.LastError);
        await log.Wait((description, level) => level == LogEntryType.Error &&
            description.StartsWith($"Missione {MissionId}, UDC 4545: la riapertura dell'ordine (operazione {OperationId}) e' stata RIFIUTATA DEFINITIVAMENTE", StringComparison.Ordinal)).WaitAsync(LogCap);

        _clock.Advance(TimeSpan.FromMinutes(5));
        await dispatcher.TryDispatchAsync();
        Assert.Equal(2, _warehouse.Calls.Count(call => call == nameof(IWarehouse.ReportPickedExtractionBecameDepositAsync)));
        Assert.True(Assert.Single(await AllRows()).Parked);
    }
}
