using Microsoft.Data.Sqlite;
using System.Text;
using Wcs.Engine.Api;
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
/// L'esito <see cref="ExtractionRowStatus.Extracted"/> entra prima nell'outbox locale: la riga
/// della coda puo' quindi uscire senza che il fatto destinato al WMS sparisca con lei.
/// </summary>
public sealed class DurableExtractionOutcomeTest : IDisposable
{
    private static readonly DateTimeOffset FixedUtc = new(2026, 9, 22, 10, 30, 0, TimeSpan.Zero);
    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly WcsDatabase? _previousStore = Navigator.Store;
    private readonly QueuedDatabaseWriter<WcsWrite>? _previousStateWriter = Navigator.StateWriter;
    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly NavigatorStatusTypes _previousNavStatus = Navigator.NavStatus;
    private QueuedDatabaseWriter<WcsWrite>? _stateWriter;

    public DurableExtractionOutcomeTest()
    {
        Navigator.Store = _database;
        Navigator.StateWriter = null;
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        Navigator.ExtractionOutcomeDispatcher.Reset();
    }

    public void Dispose()
    {
        Navigator.ExtractionOutcomeDispatcher.Reset();
        Navigator.NavStatus = _previousNavStatus;
        Navigator.Warehouse = _previousWarehouse;
        Navigator.StateWriter = _previousStateWriter;
        _stateWriter?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Navigator.Store = _previousStore;
        _database.Dispose();
    }

    [Fact]
    public async Task EnqueuePersistsTheWmsRowOutcomeBeforeTheQueueRowCanDisappear()
    {
        var queueId = await QueueExtractionRow(7101m, 8101m);

        Assert.True(await ExtractionOutcomeOutbox.EnqueueAsync(
            queueId, ExtractionRowStatus.Extracted, FixedUtc));

        var pending = Assert.Single(await ExtractionOutcomeOutbox.PendingAsync(100));
        Assert.Equal(8101m, pending.ExtractionRowId);
        Assert.Equal(ExtractionRowStatus.Extracted, pending.Status);
        Assert.Equal(Navigator.WorkstationId, pending.WorkstationId);
        Assert.Equal(FixedUtc, pending.CreatedAtUtc);
    }

    [Fact]
    public async Task EnqueueingTheSameWmsRowTwiceKeepsOneOriginalRecord()
    {
        var queueId = await QueueExtractionRow(7102m, 8102m);
        Assert.True(await ExtractionOutcomeOutbox.EnqueueAsync(queueId, ExtractionRowStatus.Extracted, FixedUtc));
        Assert.True(await ExtractionOutcomeOutbox.EnqueueAsync(queueId, ExtractionRowStatus.Extracted, FixedUtc.AddMinutes(1)));

        var pending = Assert.Single(await ExtractionOutcomeOutbox.PendingAsync(100));
        Assert.Equal(FixedUtc, pending.CreatedAtUtc);
    }

    [Fact]
    public async Task AQueueRowWithoutAWmsExtractionRowCreatesNoOutboxRecord()
    {
        var queueId = await QueueExtractionRow(7103m, extractionRowId: null);

        Assert.False(await ExtractionOutcomeOutbox.EnqueueAsync(queueId, ExtractionRowStatus.Extracted, FixedUtc));
        Assert.Empty(await ExtractionOutcomeOutbox.PendingAsync(100));
    }

    [Fact]
    public async Task AQueuedOutcomeSurvivesClosingAndReopeningTheOperationalDatabase()
    {
        var directory = Directory.CreateTempSubdirectory("wcs-extraction-outbox-");
        WcsDatabase? database = null;
        try
        {
            database = WcsDatabase.Open(directory.FullName);
            Navigator.Store = database;
            var queueId = await QueueExtractionRow(7104m, 8104m);
            Assert.True(await ExtractionOutcomeOutbox.EnqueueAsync(queueId, ExtractionRowStatus.Extracted, FixedUtc));

            database.Dispose();
            database = WcsDatabase.Open(directory.FullName);
            Navigator.Store = database;

            var pending = Assert.Single(await ExtractionOutcomeOutbox.PendingAsync(100));
            Assert.Equal(8104m, pending.ExtractionRowId);
            Assert.Equal(FixedUtc, pending.CreatedAtUtc);
        }
        finally
        {
            Navigator.Store = null;
            database?.Dispose();
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task FailedDeliveryKeepsTheOutboxAndTheNextEligibleAttemptRemovesIt()
    {
        var warehouse = new FakeWarehouse { Unavailable = true };
        Navigator.Warehouse = warehouse;
        var queueId = await QueueExtractionRow(7105m, 8105m);
        Assert.True(await ExtractionOutcomeOutbox.EnqueueAsync(queueId, ExtractionRowStatus.Extracted, FixedUtc));
        var time = new ManualTimeProvider();
        var dispatcher = new PendingExtractionOutcomeDispatcher(time);

        await dispatcher.TryDispatchAsync();

        Assert.Single(await ExtractionOutcomeOutbox.PendingAsync(100));
        Assert.Single(warehouse.Calls, nameof(IWarehouse.ReportExtractionOutcomesAsync));

        await dispatcher.TryDispatchAsync();

        Assert.Single(warehouse.Calls, nameof(IWarehouse.ReportExtractionOutcomesAsync));
        warehouse.Unavailable = false;
        time.Advance(TimeSpan.FromSeconds(2));

        await dispatcher.TryDispatchAsync();

        var request = Assert.Single(warehouse.ReportedExtractionOutcomes);
        Assert.Equal(0m, request.ExtractionHeaderId);
        Assert.Equal("KazamControl", request.WorkstationId);
        Assert.Equal([new ExtractionRowOutcome(8105m, ExtractionRowStatus.Extracted)], request.Outcomes);
        Assert.Empty(await ExtractionOutcomeOutbox.PendingAsync(100));
    }

    /// <summary>
    /// <b>Una riga rifiutata non trattiene le altre.</b> Il WCS ha una postazione sola, quindi il lotto
    /// e' tutto l'outbox: fino al 24/9/2026 un 400 del gestionale su una riga faceva riprovare lo
    /// stesso lotto ogni 30 secondi per sempre, e nessun esito arrivava piu'. Adesso il lotto
    /// rifiutato si riprova riga per riga: le buone arrivano, la rifiutata esce dall'outbox - e un
    /// solo giro basta, perche' non c'e' niente da aspettare.
    /// </summary>
    [Fact]
    public async Task ARowTheWarehouseRejectsLeavesTheOutboxAndTheOthersAreDelivered()
    {
        var warehouse = new FakeWarehouse();
        warehouse.RejectedExtractionRows.Add(8108m);
        Navigator.Warehouse = warehouse;
        foreach (var (queue, row) in new[] { (7107m, 8107m), (7108m, 8108m), (7109m, 8109m) })
            Assert.True(await ExtractionOutcomeOutbox.EnqueueAsync(
                await QueueExtractionRow(queue, row), ExtractionRowStatus.Extracted, FixedUtc));

        await new PendingExtractionOutcomeDispatcher(new ManualTimeProvider()).TryDispatchAsync();

        Assert.Empty(await ExtractionOutcomeOutbox.PendingAsync(100));
        var delivered = warehouse.ReportedExtractionOutcomes.SelectMany(request => request.Outcomes)
            .Select(outcome => outcome.ExtractionRowId).Order().ToArray();
        Assert.Equal([8107m, 8109m], delivered);
    }

    [Fact]
    public async Task ConcurrentDispatchCallsNeverSendTheSameBatchAtTheSameTime()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var warehouse = new FakeWarehouse { ReportExtractionOutcomesGate = gate };
        Navigator.Warehouse = warehouse;
        var queueId = await QueueExtractionRow(7106m, 8106m);
        Assert.True(await ExtractionOutcomeOutbox.EnqueueAsync(queueId, ExtractionRowStatus.Extracted, FixedUtc));
        var dispatcher = new PendingExtractionOutcomeDispatcher();

        var firstDispatch = dispatcher.TryDispatchAsync();
        await warehouse.ReportExtractionOutcomesAsked;
        var secondDispatch = dispatcher.TryDispatchAsync();

        Assert.True(secondDispatch.IsCompleted);
        await secondDispatch;
        Assert.Empty(warehouse.ReportedExtractionOutcomes);

        gate.TrySetResult();
        await firstDispatch;

        Assert.Single(warehouse.ReportedExtractionOutcomes);
        Assert.Empty(await ExtractionOutcomeOutbox.PendingAsync(100));
    }

    [Fact]
    public async Task ASuccessfulBatchContainsAtMostOneHundredOldestOutcomes()
    {
        var warehouse = new FakeWarehouse();
        Navigator.Warehouse = warehouse;
        for (var i = 0; i < 101; i++)
        {
            var queueId = await QueueExtractionRow(7200m + i, 8200m + i);
            Assert.True(await ExtractionOutcomeOutbox.EnqueueAsync(queueId, ExtractionRowStatus.Extracted, FixedUtc));
        }

        var dispatcher = new PendingExtractionOutcomeDispatcher();

        await dispatcher.TryDispatchAsync();

        var request = Assert.Single(warehouse.ReportedExtractionOutcomes);
        Assert.Equal(100, request.Outcomes.Count);
        Assert.Equal(Enumerable.Range(0, 100).Select(i => 8200m + i),
            request.Outcomes.Select(outcome => outcome.ExtractionRowId));
        Assert.All(request.Outcomes, outcome => Assert.Equal(ExtractionRowStatus.Extracted, outcome.Status));
        var remaining = Assert.Single(await ExtractionOutcomeOutbox.PendingAsync(200));
        Assert.Equal(8300m, remaining.ExtractionRowId);
    }

    [Fact]
    public async Task ADiagnosticFailureDoesNotEscapeAfterPersistence()
    {
        var warehouse = new FakeWarehouse { Unavailable = true };
        Navigator.Warehouse = warehouse;
        var queueId = await QueueExtractionRow(7301m, 8301m);
        Assert.True(await ExtractionOutcomeOutbox.EnqueueAsync(queueId, ExtractionRowStatus.Extracted, FixedUtc));
        var dispatcher = new PendingExtractionOutcomeDispatcher();
        var previousError = Console.Error;
        var previousHostErrorLog = Navigator.HostErrorLog;

        try
        {
            Navigator.HostErrorLog = null;
            Console.SetError(new FailingConsoleErrorWriter());

            var exception = await Record.ExceptionAsync(dispatcher.TryDispatchAsync);

            Assert.Null(exception);
        }
        finally
        {
            Console.SetError(previousError);
            Navigator.HostErrorLog = previousHostErrorLog;
        }

        Assert.Single(await ExtractionOutcomeOutbox.PendingAsync(100));
    }

    [Fact]
    public async Task WarehouseFailureAfterDurableEnqueueDoesNotKeepTheMissionOpen()
    {
        var warehouse = new FakeWarehouse();
        warehouse.UnavailableOperations.Add(nameof(IWarehouse.ReportExtractionOutcomesAsync));
        Navigator.Warehouse = warehouse;
        var queueId = await QueueExtractionRow(7302m, 8302m);
        var mission = new Items.PathMission(Guid.NewGuid()) { ExtractionQueueId = queueId };

        await mission.ProcessExtractionItems();

        Assert.Equal(0m, mission.ExtractionQueueId);
        Assert.Empty(await ExtractionQueueQueries.ListAsync(processed: null, loadUnitId: 7302m, take: 10));
        var pending = Assert.Single(await ExtractionOutcomeOutbox.PendingAsync(100));
        Assert.Equal(8302m, pending.ExtractionRowId);
    }

    [Fact]
    public async Task RejectedDurableWriteKeepsTheMissionAttachedAndTheQueueRowIntact()
    {
        var warehouse = new FakeWarehouse();
        Navigator.Warehouse = warehouse;
        var queueId = await QueueExtractionRow(7303m, 8303m);
        await UseAStoppedStateWriter();
        var mission = new Items.PathMission(Guid.NewGuid()) { ExtractionQueueId = queueId };

        await mission.ProcessExtractionItems();

        Assert.Equal(queueId, mission.ExtractionQueueId);
        Assert.Single(await ExtractionQueueQueries.ListAsync(processed: null, loadUnitId: 7303m, take: 10));
        Assert.Empty(await ExtractionOutcomeOutbox.PendingAsync(100));
        Assert.Empty(warehouse.Calls);
    }

    [Fact]
    public async Task RejectedArrivalWritePreventsAutoUnloadUntilADurableRetry()
    {
        var warehouse = new FakeWarehouse();
        Navigator.Warehouse = warehouse;
        var (mission, exit, queueId) = await ArrivingExtraction();
        var assignmentId = Guid.NewGuid();
        mission.GuidAssignmentLu = assignmentId;
        await UseAStoppedStateWriter();

        try
        {
            await exit.LoadLu(mission.MissionId, activateExecuteStep: false).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Same(mission, exit.LoadedLu);
            Assert.Contains(mission, ActiveMissions.Instance.List);
            Assert.Equal(queueId, mission.ExtractionQueueId);
            Assert.Equal(assignmentId, mission.GuidAssignmentLu);
            Assert.Single(await ExtractionQueueQueries.ListAsync(null, mission.LuId, 10));
            Assert.Empty(await ExtractionOutcomeOutbox.PendingAsync(100));
            Assert.DoesNotContain(nameof(IWarehouse.ReportMissionServedAssignmentAsync), warehouse.Calls);
            Assert.DoesNotContain(nameof(IWarehouse.ConfirmServedAssignmentsAsync), warehouse.Calls);
            Assert.DoesNotContain(nameof(IWarehouse.ReleaseLoadUnitAsync), warehouse.Calls);

            // The transfer makes the destination ready once LoadLu returns. A later automatic
            // unload must retry the retained extraction even though the position has not changed.
            Navigator.StateWriter = null;
            exit.Status = DeviceStatusTypes.Ready;
            await exit.LuUnloadedFromAsi(StepTypes.Automatic).WaitAsync(TimeSpan.FromSeconds(10));
            await mission.PositionWork;

            Assert.Null(exit.LoadedLu);
            Assert.DoesNotContain(mission, ActiveMissions.Instance.List);
            Assert.Equal(0m, mission.ExtractionQueueId);
            Assert.Empty(await ExtractionQueueQueries.ListAsync(null, mission.LuId, 10));
            var report = Assert.Single(warehouse.ReportedExtractionOutcomes);
            Assert.Equal([new ExtractionRowOutcome(8401m, ExtractionRowStatus.Extracted)], report.Outcomes);
            Assert.Empty(await ExtractionOutcomeOutbox.PendingAsync(100));
            Assert.Contains(nameof(IWarehouse.ReportMissionServedAssignmentAsync), warehouse.Calls);
        }
        finally
        {
            Navigator.StateWriter = null;
            await mission.PositionWork;
            ActiveMissions.Instance.RemoveAll(_ => true);
            PathJunctions.Instance.RemoveAll(_ => true);
            PathDevices.Instance.Clear();
        }
    }

    [Fact]
    public async Task WarehouseFailureAfterDurableArrivalStillAllowsAutoUnload()
    {
        var warehouse = new FakeWarehouse();
        warehouse.UnavailableOperations.Add(nameof(IWarehouse.ReportExtractionOutcomesAsync));
        Navigator.Warehouse = warehouse;
        var (mission, exit, _) = await ArrivingExtraction();
        try
        {
            await exit.LoadLu(mission.MissionId, activateExecuteStep: false).WaitAsync(TimeSpan.FromSeconds(10));
            await mission.PositionWork;

            Assert.Null(exit.LoadedLu);
            Assert.DoesNotContain(mission, ActiveMissions.Instance.List);
            Assert.Equal(0m, mission.ExtractionQueueId);
            Assert.Empty(await ExtractionQueueQueries.ListAsync(null, mission.LuId, 10));
            var pending = Assert.Single(await ExtractionOutcomeOutbox.PendingAsync(100));
            Assert.Equal(8401m, pending.ExtractionRowId);
            Assert.Equal(ExtractionRowStatus.Extracted, pending.Status);
            Assert.Contains(nameof(IWarehouse.ReleaseLoadUnitAsync), warehouse.Calls);
        }
        finally
        {
            await mission.PositionWork;
            ActiveMissions.Instance.RemoveAll(_ => true);
            PathJunctions.Instance.RemoveAll(_ => true);
            PathDevices.Instance.Clear();
        }
    }

    [Fact]
    public async Task AutomaticRetryPersistsAfterMissionMovesToANonStorageLinkedExit()
    {
        var warehouse = new FakeWarehouse();
        Navigator.Warehouse = warehouse;
        var finalExit = await AddNonStorageLinkedExit();
        var (mission, linkedExit, _) = await ArrivingExtraction();
        await UseAStoppedStateWriter();

        try
        {
            await linkedExit.LoadLu(mission.MissionId, activateExecuteStep: false)
                .WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(7401m, linkedExit.LoadedLu?.LuId);
            Assert.Empty(await ExtractionOutcomeOutbox.PendingAsync(100));

            Navigator.StateWriter = null;
            Assert.False(finalExit.IsLinkedToStorage);
            await finalExit.LoadLu(mission.MissionId, activateExecuteStep: false)
                .WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Null(finalExit.LoadedLu);
            Assert.DoesNotContain(mission, ActiveMissions.Instance.List);
            Assert.Equal(0m, mission.ExtractionQueueId);
            Assert.Empty(await ExtractionQueueQueries.ListAsync(null, mission.LuId, 10));
            var report = Assert.Single(warehouse.ReportedExtractionOutcomes);
            Assert.Equal([new ExtractionRowOutcome(8401m, ExtractionRowStatus.Extracted)], report.Outcomes);
            Assert.Empty(await ExtractionOutcomeOutbox.PendingAsync(100));
        }
        finally
        {
            Navigator.StateWriter = null;
            await mission.PositionWork;
            ActiveMissions.Instance.RemoveAll(_ => true);
            PathJunctions.Instance.RemoveAll(_ => true);
            PathDevices.Instance.Clear();
        }
    }

    [Fact]
    public async Task ManualUnloadWaitsForDurableOutcomeThenRetries()
    {
        var warehouse = new FakeWarehouse();
        Navigator.Warehouse = warehouse;
        var (mission, exit, queueId) = await ArrivingExtraction();
        await UseAStoppedStateWriter();

        try
        {
            await exit.LoadLu(mission.MissionId, activateExecuteStep: false)
                .WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(7401m, exit.LoadedLu?.LuId);

            exit.Status = DeviceStatusTypes.Ready;
            await exit.LuUnloadedFromAsi(StepTypes.Manual)
                .WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Same(mission, exit.LoadedLu);
            Assert.Contains(mission, ActiveMissions.Instance.List);
            Assert.Equal(queueId, mission.ExtractionQueueId);
            Assert.Single(await ExtractionQueueQueries.ListAsync(null, mission.LuId, 10));
            Assert.Empty(await ExtractionOutcomeOutbox.PendingAsync(100));
            Assert.DoesNotContain(nameof(IWarehouse.ReleaseLoadUnitAsync), warehouse.Calls);

            Navigator.StateWriter = null;
            await exit.LuUnloadedFromAsi(StepTypes.Manual)
                .WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Null(exit.LoadedLu);
            Assert.DoesNotContain(mission, ActiveMissions.Instance.List);
            Assert.Equal(0m, mission.ExtractionQueueId);
            Assert.Empty(await ExtractionQueueQueries.ListAsync(null, mission.LuId, 10));
            var report = Assert.Single(warehouse.ReportedExtractionOutcomes);
            Assert.Equal([new ExtractionRowOutcome(8401m, ExtractionRowStatus.Extracted)], report.Outcomes);
            Assert.Empty(await ExtractionOutcomeOutbox.PendingAsync(100));
            Assert.Contains(nameof(IWarehouse.ReleaseLoadUnitAsync), warehouse.Calls);
        }
        finally
        {
            Navigator.StateWriter = null;
            await mission.PositionWork;
            ActiveMissions.Instance.RemoveAll(_ => true);
            PathJunctions.Instance.RemoveAll(_ => true);
            PathDevices.Instance.Clear();
        }
    }

    private static async Task<(Items.PathMission Mission, Items.AutomationDevice Exit, decimal QueueId)> ArrivingExtraction()
    {
        ActiveMissions.Instance.RemoveAll(_ => true);
        var sourceId = Guid.NewGuid();
        var exitId = Guid.NewGuid();
        await PlantQueries.UpsertItemConfigurationAsync(new PlantItem
        {
            ItemId = sourceId, Name = "Extraction source",
            DeviceType = (short)DeviceTypes.Automation, ConnectionType = (short)ConnectionTypes.Test
        });
        await PlantQueries.UpsertItemConfigurationAsync(new PlantItem
        {
            ItemId = exitId, Name = "Extraction exit", LinkedCellId = 2795,
            DeviceType = (short)DeviceTypes.Buffer, ConnectionType = (short)ConnectionTypes.Test,
            AutoUnloadLoadUnit = true
        });
        var storage = TestDevices.WithRow(Guid.NewGuid(), d => d.DeviceType = DeviceTypes.Storage);
        PathDevices.Instance.Add(storage);
        PathJunctions.Instance.Add(new Items.AutomationJunction(exitId, storage.ItemGuid));
        Assert.True(PathDevices.Instance[exitId]!.IsLinkedToStorage);
        Navigator.NavStatus = NavigatorStatusTypes.Run;
        var queueId = await QueueExtractionRow(7401m, 8401m);
        var mission = new Items.PathMission
        {
            MissionType = MissionTypes.MovingToItem, LuId = 7401m,
            SourceItemId = sourceId, DestinationItemId = exitId, PositionItemId = sourceId,
            ExtractionQueueId = queueId
        };
        await mission.PositionWork;
        ActiveMissions.Instance.Add(mission, buildRoute: false);
        Assert.Contains(mission, ActiveMissions.Instance.List);
        var exit = PathDevices.Instance[exitId]!;
        exit.Status = DeviceStatusTypes.Busy;
        return (mission, exit, queueId);
    }

    private static async Task<Items.AutomationDevice> AddNonStorageLinkedExit()
    {
        var exitId = Guid.NewGuid();
        await PlantQueries.UpsertItemConfigurationAsync(new PlantItem
        {
            ItemId = exitId, Name = "Final extraction exit",
            DeviceType = (short)DeviceTypes.Buffer, ConnectionType = (short)ConnectionTypes.Test,
            AutoUnloadLoadUnit = true
        });
        var exit = PathDevices.Instance[exitId]!;
        exit.Status = DeviceStatusTypes.Busy;
        return exit;
    }

    [Fact]
    public async Task AStoppedNavigatorStillFlushesDurableOutcomesDuringAMasterCycle()
    {
        var warehouse = new FakeWarehouse();
        Navigator.Warehouse = warehouse;
        var queueId = await QueueExtractionRow(7304m, 8304m);
        Assert.True(await ExtractionOutcomeOutbox.EnqueueAsync(queueId, ExtractionRowStatus.Extracted, FixedUtc));
        Navigator.NavStatus = NavigatorStatusTypes.Stop;

        await Navigator.MasterProcess();

        var report = Assert.Single(warehouse.ReportedExtractionOutcomes);
        Assert.Equal([new ExtractionRowOutcome(8304m, ExtractionRowStatus.Extracted)], report.Outcomes);
        Assert.Empty(await ExtractionOutcomeOutbox.PendingAsync(100));
    }

    private static async Task<decimal> QueueExtractionRow(decimal loadUnitId, decimal? extractionRowId)
    {
        var row = new ExtractionQueueItem
        {
            LoadUnitId = loadUnitId,
            SourceItemId = Guid.NewGuid(),
            ExtractionRowId = extractionRowId,
            WorkstationId = Navigator.WorkstationId
        };

        await ExtractionQueue.UpsertAsync(row);
        return row.Id;
    }

    private async Task UseAStoppedStateWriter()
    {
        var writer = new QueuedDatabaseWriter<WcsWrite>((_, _) => Task.CompletedTask);
        await writer.StopAsync();

        _stateWriter = writer;
        Navigator.StateWriter = writer;
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = FixedUtc;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan value) => _now += value;
    }

    private sealed class FailingConsoleErrorWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value) => throw new IOException("The diagnostic stream failed.");

        public override void Write(string? value) => throw new IOException("The diagnostic stream failed.");

        public override void WriteLine(string? value) => throw new IOException("The diagnostic stream failed.");
    }
}
