using Microsoft.Data.Sqlite;
using System.Text.Json;
using Wcs.Engine.ORM;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Wcs.Engine.Collections;
using Wcs.Wms.Contracts;
using Wcs.Wms.Contracts.Test;
using Xunit;
using static Wcs.Common.Enums;
using Microsoft.EntityFrameworkCore;

namespace Wcs.Engine.Test;

public sealed class DurableWarehouseTransitionTest : IDisposable
{
    private static readonly Guid MissionId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset FixedUtc = new(2026, 9, 23, 9, 0, 0, TimeSpan.Zero);
    private readonly WcsDatabase? _previousStore = Navigator.Store;
    private readonly QueuedDatabaseWriter<WcsWrite>? _previousWriter = Navigator.StateWriter;
    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly NavigatorStatusTypes _previousStatus = Navigator.NavStatus;
    private readonly bool _previousLockCellOnStoringError = Navigator.LockCellOnStoringError;
    private readonly WcsDatabase _database = WcsDatabase.InMemory();

    public DurableWarehouseTransitionTest()
    {
        Navigator.Store = _database;
        Navigator.StateWriter = null;
        ActiveMissions.Instance.RemoveAll(_ => true);
        PathDevices.Instance.Clear();
        Navigator.WarehouseTransitionDispatcher.Reset();
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
    }

    public void Dispose()
    {
        Navigator.StateWriter = _previousWriter;
        Navigator.Warehouse = _previousWarehouse;
        Navigator.NavStatus = _previousStatus;
        Navigator.LockCellOnStoringError = _previousLockCellOnStoringError;
        Navigator.WarehouseTransitionDispatcher.Reset();
        ActiveMissions.Instance.RemoveAll(_ => true);
        PathDevices.Instance.Clear();
        Navigator.Store = _previousStore;
        _database.Dispose();
    }

    [Fact]
    public async Task AllocateAndReleaseSurviveStoreReopen()
    {
        var directory = Directory.CreateTempSubdirectory("wcs-transition-outbox-");
        var operationId = Guid.Parse("20000000-0000-0000-0000-000000000002");
        WcsDatabase? fileStore = null;

        try
        {
            fileStore = WcsDatabase.Open(directory.FullName);
            Navigator.Store = fileStore;
            var request = new ReleaseLoadUnitRequest(42, false, "wcs", operationId);
            await WarehouseTransitionOutbox.EnqueueAsync(
                operationId,
                WarehouseTransitionKind.Release,
                MissionId,
                request.LoadUnitId,
                JsonSerializer.Serialize(request),
                FixedUtc);

            fileStore.Dispose();
            fileStore = WcsDatabase.Open(directory.FullName);
            Navigator.Store = fileStore;

            var row = Assert.Single(await WarehouseTransitionOutbox.DueAsync(DateTimeOffset.MaxValue, 10));
            Assert.Equal(operationId, row.OperationId);
            Assert.Equal(WarehouseTransitionKind.Release, row.Kind);
            Assert.Equal(MissionId, row.MissionId);
            Assert.Equal(42, row.LoadUnitId);
            Assert.Equal(FixedUtc, row.CreatedAtUtc);
            Assert.Equal(FixedUtc, row.NextAttemptAtUtc);
        }
        finally
        {
            Navigator.Store = _database;
            fileStore?.Dispose();
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SameMissionAndKindReuseTheLiveOperation()
    {
        var first = await WarehouseTransitionOutbox.EnqueueAsync(
            Guid.Parse("30000000-0000-0000-0000-000000000003"),
            WarehouseTransitionKind.Allocate,
            MissionId,
            42,
            AllocateJson(42),
            FixedUtc);
        var second = await WarehouseTransitionOutbox.EnqueueAsync(
            Guid.Parse("40000000-0000-0000-0000-000000000004"),
            WarehouseTransitionKind.Allocate,
            MissionId,
            42,
            AllocateJson(42),
            FixedUtc.AddMinutes(1));

        Assert.Equal(first.OperationId, second.OperationId);
        Assert.Equal(first.PayloadJson, second.PayloadJson);
        Assert.Single(await WarehouseTransitionOutbox.DueAsync(DateTimeOffset.MaxValue, 10));
    }

    /// <summary>
    /// <b>L'ordine tiene dove due mutazioni si toccano, e soltanto li'.</b> Fino al 24/9/2026 la
    /// prima riga in attesa tratteneva l'intera coda - e questa prova lo fissava con due rilasci di
    /// UDC diverse. Ma due rilasci di unita' diverse non condividono niente: ognuno libera la cella
    /// della propria. Trattenerli insieme voleva dire che una sola transizione rifiutata per sempre
    /// fermava allocazioni e rilasci di tutto l'impianto.
    /// </summary>
    [Fact]
    public async Task AWaitingReleaseDoesNotHoldBackAReleaseOfAnotherLoadUnit()
    {
        var first = await EnqueueReleaseAsync(MissionId, 41, FixedUtc);
        var second = await EnqueueReleaseAsync(Guid.NewGuid(), 42, FixedUtc.AddSeconds(1));
        await WarehouseTransitionOutbox.RecordFailureAsync(
            first.Id,
            new InvalidOperationException("warehouse unavailable"),
            FixedUtc.AddMinutes(5));

        var beforeRetry = await WarehouseTransitionOutbox.DueAsync(FixedUtc.AddMinutes(1), 10);
        Assert.Equal(new[] { second.Id }, beforeRetry.Select(row => row.Id));

        var afterRetry = await WarehouseTransitionOutbox.DueAsync(FixedUtc.AddMinutes(6), 10);
        Assert.Equal(new[] { first.Id, second.Id }, afterRetry.Select(row => row.Id));
        Assert.Equal(1, afterRetry[0].AttemptCount);
        Assert.Contains("warehouse unavailable", afterRetry[0].LastError);
    }

    /// <summary>Stessa UDC: una mutazione non scavalca mai quella prima di lei.</summary>
    [Fact]
    public async Task AWaitingTransitionHoldsBackTheNewerOnesOfItsOwnLoadUnit()
    {
        var allocate = await EnqueueAllocateAsync(41, FixedUtc);
        await EnqueueReleaseAsync(MissionId, 41, FixedUtc.AddSeconds(1));
        await WarehouseTransitionOutbox.RecordFailureAsync(
            allocate.Id, new InvalidOperationException("warehouse unavailable"), FixedUtc.AddMinutes(5));

        Assert.Empty(await WarehouseTransitionOutbox.DueAsync(FixedUtc.AddMinutes(1), 10));
    }

    /// <summary>
    /// Un rilascio non dice quale cella libera, e un'allocazione successiva potrebbe andare proprio
    /// li': il gestionale la troverebbe ancora occupata. Quindi un rilascio in attesa trattiene ogni
    /// allocazione dopo di lui, di qualunque UDC.
    /// </summary>
    [Fact]
    public async Task AWaitingReleaseHoldsBackEveryNewerAllocation()
    {
        var release = await EnqueueReleaseAsync(MissionId, 41, FixedUtc);
        await EnqueueAllocateAsync(42, FixedUtc.AddSeconds(1));
        await WarehouseTransitionOutbox.RecordFailureAsync(
            release.Id, new InvalidOperationException("warehouse unavailable"), FixedUtc.AddMinutes(5));

        Assert.Empty(await WarehouseTransitionOutbox.DueAsync(FixedUtc.AddMinutes(1), 10));
    }

    /// <summary>
    /// Chi resta indietro trattiene a sua volta. Il rilascio della 41 non e' in attesa per conto suo:
    /// e' trattenuto dall'allocazione della 41 che lo precede. Ma resta un rilascio non ancora
    /// avvenuto, quindi l'allocazione della 42 dopo di lui aspetta - mentre il rilascio della 43, che
    /// non tocca niente di tutto questo, passa.
    /// </summary>
    [Fact]
    public async Task AReleaseHeldBackByItsLoadUnitHoldsBackNewerAllocationsInTurn()
    {
        var waiting = await EnqueueAllocateAsync(41, FixedUtc);
        await EnqueueReleaseAsync(MissionId, 41, FixedUtc.AddSeconds(1));
        await EnqueueAllocateAsync(42, FixedUtc.AddSeconds(2));
        var independent = await EnqueueReleaseAsync(Guid.NewGuid(), 43, FixedUtc.AddSeconds(3));
        await WarehouseTransitionOutbox.RecordFailureAsync(
            waiting.Id, new InvalidOperationException("warehouse unavailable"), FixedUtc.AddMinutes(5));

        var due = await WarehouseTransitionOutbox.DueAsync(FixedUtc.AddMinutes(1), 10);
        Assert.Equal(new[] { independent.Id }, due.Select(row => row.Id));
    }

    [Fact]
    public async Task DeleteRemovesOnlyTheCompletedTransition()
    {
        var first = await EnqueueReleaseAsync(MissionId, 41, FixedUtc);
        var second = await EnqueueReleaseAsync(Guid.NewGuid(), 42, FixedUtc.AddSeconds(1));

        await WarehouseTransitionOutbox.DeleteAsync(first.Id);

        var remaining = Assert.Single(await WarehouseTransitionOutbox.DueAsync(DateTimeOffset.MaxValue, 10));
        Assert.Equal(second.Id, remaining.Id);
    }

    [Fact]
    public async Task EnqueueWithoutAnOperationalStoreFailsVisibly()
    {
        Navigator.Store = null;

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WarehouseTransitionOutbox.EnqueueAsync(
                Guid.NewGuid(),
                WarehouseTransitionKind.Release,
                MissionId,
                42,
                "{}",
                FixedUtc));

        Assert.Contains("operational store", failure.Message);
        Navigator.Store = _database;
    }

    [Fact]
    public async Task FailedReleaseKeepsMissionAndRetriesTheSameOperation()
    {
        var clock = new TransitionTimeProvider(FixedUtc);
        var dispatcher = new PendingWarehouseTransitionDispatcher(clock);
        var warehouse = new FakeWarehouse { Unavailable = true };
        Navigator.Warehouse = warehouse;
        Navigator.NavStatus = NavigatorStatusTypes.Run;

        var device = TestDevices.WithRow(Guid.NewGuid(), d => d.DeviceType = DeviceTypes.Automation);
        PathDevices.Instance[device.ItemGuid] = device;
        device.Status = DeviceStatusTypes.Ready;
        var mission = new Items.PathMission(MissionId) { LuId = 42, PositionItemId = device.ItemGuid };
        ActiveMissions.Instance.Add(mission, buildRoute: false);
        var operationId = Guid.Parse("50000000-0000-0000-0000-000000000005");
        await WarehouseTransitionOutbox.EnqueueAsync(
            operationId,
            WarehouseTransitionKind.Release,
            mission.MissionId,
            mission.LuId,
            JsonSerializer.Serialize(new ReleaseLoadUnitRequest(42, false, "wcs", operationId)),
            FixedUtc);

        await dispatcher.TryDispatchAsync();

        var failed = Assert.Single(await WarehouseTransitionOutbox.DueAsync(DateTimeOffset.MaxValue, 10));
        Assert.Equal(1, failed.AttemptCount);
        Assert.Contains(mission, ActiveMissions.Instance.List);
        Assert.Equal(DeviceStatusTypes.Warning, device.Status);

        warehouse.Unavailable = false;
        clock.Advance(TimeSpan.FromSeconds(2));
        await dispatcher.TryDispatchAsync();

        Assert.Equal(operationId, Assert.Single(warehouse.ReleasedLoadUnits).OperationId);
        Assert.DoesNotContain(mission, ActiveMissions.Instance.List);
        Assert.Empty(await WarehouseTransitionOutbox.DueAsync(DateTimeOffset.MaxValue, 10));
    }

    /// <summary>
    /// <b>Un rifiuto non guarisce riprovando.</b> Il gestionale risponde 400 al rilascio della 41: la
    /// riga si parcheggia - resta nell'outbox con il suo motivo, ma non torna piu' dovuta nemmeno fra
    /// cent'anni - e il rilascio della 42, che non tocca niente della 41, passa al giro dopo. Prima
    /// si riprovava ogni 30 secondi per sempre, e l'ordine globale tratteneva la 42 con lei.
    /// </summary>
    [Fact]
    public async Task APermanentRejectionIsParkedAndDoesNotHoldBackAnIndependentTransition()
    {
        var warehouse = new FakeWarehouse();
        warehouse.RejectedTransitionLoadUnits.Add(41);
        Navigator.Warehouse = warehouse;
        // Un minuto dopo: le due righe nascono a FixedUtc e FixedUtc+1s, e devono essere tutte e due
        // dovute, altrimenti la 42 non passerebbe per l'orologio e non per la regola.
        var dispatcher = new PendingWarehouseTransitionDispatcher(new TransitionTimeProvider(FixedUtc.AddMinutes(1)));
        var rejected = await EnqueueReleaseAsync(MissionId, 41, FixedUtc);
        await EnqueueReleaseAsync(Guid.NewGuid(), 42, FixedUtc.AddSeconds(1));

        await dispatcher.TryDispatchAsync();

        await using (var context = _database.CreateContext())
        {
            var parked = await context.PendingWarehouseTransitions.SingleAsync(row => row.Id == rejected.Id);
            Assert.Equal(PendingWarehouseTransitionStore.ParkedUntil, parked.NextAttemptAtUtc);
            Assert.Contains("rifiutata", parked.LastError);
        }

        var dueLater = await WarehouseTransitionOutbox.DueAsync(FixedUtc.AddYears(100), 10);
        Assert.DoesNotContain(dueLater, row => row.Id == rejected.Id);

        await dispatcher.TryDispatchAsync();
        Assert.Contains(warehouse.ReleasedLoadUnits, request => request.LoadUnitId == 42);
    }

    [Fact]
    public async Task RemoteSuccessBeforeMissionRecoveryKeepsTheOutboxRow()
    {
        var warehouse = new FakeWarehouse();
        Navigator.Warehouse = warehouse;
        var dispatcher = new PendingWarehouseTransitionDispatcher(new TransitionTimeProvider(FixedUtc));
        var mission = new Items.PathMission(MissionId) { LuId = 43 };
        await using (var context = _database.CreateContext())
        {
            await MissionStore.UpsertAsync(context, MissionStore.Snapshot(mission));
            await context.SaveChangesAsync();
        }
        var operationId = Guid.Parse("60000000-0000-0000-0000-000000000006");
        await WarehouseTransitionOutbox.EnqueueAsync(
            operationId,
            WarehouseTransitionKind.Release,
            mission.MissionId,
            mission.LuId,
            JsonSerializer.Serialize(new ReleaseLoadUnitRequest(43, false, "wcs", operationId)),
            FixedUtc);

        await dispatcher.TryDispatchAsync();

        Assert.Single(await WarehouseTransitionOutbox.DueAsync(DateTimeOffset.MaxValue, 10));
        await using (var context = _database.CreateContext())
            Assert.True(await MissionStore.ExistsAsync(context, mission.MissionId));

        Navigator.NavStatus = NavigatorStatusTypes.Run;
        ActiveMissions.Instance.Add(mission, buildRoute: false);
        await dispatcher.TryDispatchAsync();

        Assert.Equal(2, warehouse.ReleasedLoadUnits.Count);
        Assert.All(warehouse.ReleasedLoadUnits, request => Assert.Equal(operationId, request.OperationId));
        Assert.Empty(await WarehouseTransitionOutbox.DueAsync(DateTimeOffset.MaxValue, 10));
        await using (var context = _database.CreateContext())
            Assert.False(await MissionStore.ExistsAsync(context, mission.MissionId));
    }

    [Fact]
    public async Task CorruptPayloadIsRetainedAsAFailedTransition()
    {
        Navigator.Warehouse = new FakeWarehouse();
        var dispatcher = new PendingWarehouseTransitionDispatcher(new TransitionTimeProvider(FixedUtc));
        await WarehouseTransitionOutbox.EnqueueAsync(
            Guid.NewGuid(), WarehouseTransitionKind.Release, MissionId, 44, "not-json", FixedUtc);

        await dispatcher.TryDispatchAsync();

        var failed = Assert.Single(await WarehouseTransitionOutbox.DueAsync(DateTimeOffset.MaxValue, 10));
        Assert.Equal(1, failed.AttemptCount);
        Assert.Contains("Json", failed.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PayloadWithAnotherOperationIdIsNeverDelivered()
    {
        var warehouse = new FakeWarehouse();
        Navigator.Warehouse = warehouse;
        var rowOperationId = Guid.NewGuid();
        var payloadOperationId = Guid.NewGuid();
        await WarehouseTransitionOutbox.EnqueueAsync(
            rowOperationId,
            WarehouseTransitionKind.Release,
            MissionId,
            44,
            JsonSerializer.Serialize(new ReleaseLoadUnitRequest(44, false, "wcs", payloadOperationId)),
            FixedUtc);

        await new PendingWarehouseTransitionDispatcher(
            new TransitionTimeProvider(FixedUtc.AddSeconds(1))).TryDispatchAsync();

        Assert.Empty(warehouse.ReleasedLoadUnits);
        var failed = Assert.Single(await WarehouseTransitionOutbox.DueAsync(DateTimeOffset.MaxValue, 10));
        Assert.Contains(rowOperationId.ToString(), failed.LastError);
        Assert.Contains(payloadOperationId.ToString(), failed.LastError);
    }

    [Fact]
    public async Task StandardUnloadPersistsBeforeCallingAnUnavailableWarehouse()
    {
        var warehouse = new FakeWarehouse { Unavailable = true };
        Navigator.Warehouse = warehouse;
        Navigator.NavStatus = NavigatorStatusTypes.Run;
        var device = TestDevices.WithRow(Guid.NewGuid(), d => d.DeviceType = DeviceTypes.Automation);
        PathDevices.Instance[device.ItemGuid] = device;
        device.Status = DeviceStatusTypes.Ready;
        var mission = new Items.PathMission(Guid.NewGuid())
        {
            LuId = 45,
            PositionItemId = device.ItemGuid
        };
        ActiveMissions.Instance.Add(mission, buildRoute: false);

        await device.LuUnloadedFromAsi(StepTypes.Automatic);

        var pending = Assert.Single(await WarehouseTransitionOutbox.DueAsync(DateTimeOffset.MaxValue, 10));
        Assert.Equal(WarehouseTransitionKind.Release, pending.Kind);
        Assert.Equal(mission.MissionId, pending.MissionId);
        Assert.Contains(mission, ActiveMissions.Instance.List);
        Assert.Equal(DeviceStatusTypes.Warning, device.Status);
    }

    [Fact]
    public async Task MasterProcessDispatchesTransitionsWhileNavigatorIsStopped()
    {
        var warehouse = new FakeWarehouse();
        Navigator.Warehouse = warehouse;
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        var operationId = Guid.NewGuid();
        await WarehouseTransitionOutbox.EnqueueAsync(
            operationId,
            WarehouseTransitionKind.Release,
            Guid.NewGuid(),
            46,
            JsonSerializer.Serialize(new ReleaseLoadUnitRequest(46, false, "wcs", operationId)),
            DateTimeOffset.UtcNow.AddSeconds(-1));

        await Navigator.MasterProcess();

        Assert.Equal(operationId, Assert.Single(warehouse.ReleasedLoadUnits).OperationId);
        Assert.Empty(await WarehouseTransitionOutbox.DueAsync(DateTimeOffset.MaxValue, 10));
    }

    [Fact]
    public async Task FailedStorageAllocateKeepsMissionReservationAndProtectiveDoorLock()
    {
        const string doorCode = "A47";
        var warehouse = new FakeWarehouse();
        warehouse.UnavailableOperations.Add(nameof(IWarehouse.AllocateLoadUnitAsync));
        warehouse.CellsByDoor[doorCode] =
        [
            new CellDescription(47, 1, 1, 300, false, CellLockState.None, null, null, 0,
                doorCode, string.Empty, 1, MultiDepthStorage: false)
        ];
        Navigator.Warehouse = warehouse;

        Navigator.NavStatus = NavigatorStatusTypes.Recover;
        var storage = TestDevices.WithRow(Guid.NewGuid(), d => d.DeviceType = DeviceTypes.Storage);
        PathDevices.Instance[storage.ItemGuid] = storage;
        await PlantPersistence.SaveDoorMappingsAsync(storage.ItemGuid,
            [new DoorMapping { ItemId = storage.ItemGuid, DoorCode = doorCode, CellId = 47, Side = "A" }]);
        var mission = new Items.PathMission(Guid.NewGuid())
        {
            LuId = 47,
            LuCode = "LU47",
            DestinationItemId = storage.ItemGuid,
            PositionItemId = storage.ItemGuid,
            StorageDoorCode = doorCode
        };
        await mission.PositionWork;
        Navigator.NavStatus = NavigatorStatusTypes.Run;
        ActiveMissions.Instance.Add(mission, buildRoute: false);
        warehouse.StorageReservationsByMission[mission.MissionId] = new StorageReservation(
            mission.MissionId, StorageReservationOutcome.CellFound, 47, doorCode, null, 0, false);
        await DbInfo.SetCellLockedForStoring(doorCode);

        await Items.PathMission.AllocateOnStorage(storage, mission);

        var pending = Assert.Single(await WarehouseTransitionOutbox.DueAsync(DateTimeOffset.MaxValue, 10));
        Assert.Equal(WarehouseTransitionKind.Allocate, pending.Kind);
        Assert.Contains(mission, ActiveMissions.Instance.List);
        Assert.NotNull(await warehouse.GetStorageReservationAsync(mission.MissionId));
        Assert.True(await DbInfo.GetDoorLockedForStoring(doorCode));
        Assert.Equal(DeviceStatusTypes.Warning, storage.Status);

        warehouse.UnavailableOperations.Remove(nameof(IWarehouse.AllocateLoadUnitAsync));
        var dispatcher = new PendingWarehouseTransitionDispatcher(
            new TransitionTimeProvider(DateTimeOffset.UtcNow.AddDays(1)));
        await dispatcher.TryDispatchAsync();

        Assert.Equal(pending.OperationId, Assert.Single(warehouse.AllocatedLoadUnits).OperationId);
        Assert.DoesNotContain(mission, ActiveMissions.Instance.List);
        Assert.Null(await warehouse.GetStorageReservationAsync(mission.MissionId));
        Assert.False(await DbInfo.GetDoorLockedForStoring(doorCode));
        Assert.Empty(await WarehouseTransitionOutbox.DueAsync(DateTimeOffset.MaxValue, 10));
    }

    [Fact]
    public async Task SuccessfulAllocatePreservesAStoringErrorQuarantine()
    {
        const string doorCode = "A48";
        var warehouse = new FakeWarehouse();
        warehouse.CellsByDoor[doorCode] =
        [
            new CellDescription(48, 1, 1, 100, false, CellLockState.None, null, null, 0,
                doorCode, string.Empty, 1, MultiDepthStorage: true)
        ];
        Navigator.Warehouse = warehouse;
        Navigator.LockCellOnStoringError = true;
        Navigator.NavStatus = NavigatorStatusTypes.Recover;
        var storage = TestDevices.WithRow(Guid.NewGuid(), d => d.DeviceType = DeviceTypes.Storage);
        PathDevices.Instance[storage.ItemGuid] = storage;
        await PlantPersistence.SaveDoorMappingsAsync(storage.ItemGuid,
            [new DoorMapping { ItemId = storage.ItemGuid, DoorCode = doorCode, CellId = 48, Side = "A" }]);
        var mission = new Items.PathMission(Guid.NewGuid())
        {
            LuId = 48,
            LuCode = "LU48",
            DestinationItemId = storage.ItemGuid,
            PositionItemId = storage.ItemGuid,
            StorageDoorCode = doorCode,
            DetectedDepth = 150
        };
        await mission.PositionWork;
        Navigator.NavStatus = NavigatorStatusTypes.Run;
        ActiveMissions.Instance.Add(mission, buildRoute: false);
        warehouse.StorageReservationsByMission[mission.MissionId] = new StorageReservation(
            mission.MissionId, StorageReservationOutcome.CellFound, 48, doorCode, null, 0, false);
        await DbInfo.SetCellLockedForStoring(doorCode);
        var operationId = Guid.NewGuid();
        var request = new AllocateLoadUnitRequest(
            mission.LuId, 48, doorCode, true, false, 80, mission.DetectedDepth, "wcs", operationId);
        await WarehouseTransitionOutbox.EnqueueAsync(
            operationId,
            WarehouseTransitionKind.Allocate,
            mission.MissionId,
            mission.LuId,
            JsonSerializer.Serialize(request),
            FixedUtc);

        await new PendingWarehouseTransitionDispatcher(
            new TransitionTimeProvider(FixedUtc.AddSeconds(1))).TryDispatchAsync();

        Assert.DoesNotContain(mission, ActiveMissions.Instance.List);
        Assert.True(await DbInfo.GetDoorLockedForStoring(doorCode));
        Assert.Empty(await WarehouseTransitionOutbox.DueAsync(DateTimeOffset.MaxValue, 10));
    }

    private static string AllocateJson(decimal loadUnitId)
    {
        var operationId = Guid.NewGuid();
        return JsonSerializer.Serialize(new AllocateLoadUnitRequest(
            loadUnitId, 7, "P1", true, false, 150, 148, "wcs", operationId));
    }

    private static Task<PendingWarehouseTransition> EnqueueAllocateAsync(
        decimal loadUnitId,
        DateTimeOffset createdAtUtc)
    {
        var operationId = Guid.NewGuid();
        return WarehouseTransitionOutbox.EnqueueAsync(
            operationId,
            WarehouseTransitionKind.Allocate,
            Guid.NewGuid(),
            loadUnitId,
            JsonSerializer.Serialize(new AllocateLoadUnitRequest(
                loadUnitId, 7, "P1", true, false, 150, 148, "wcs", operationId)),
            createdAtUtc);
    }

    private static Task<PendingWarehouseTransition> EnqueueReleaseAsync(
        Guid missionId,
        decimal loadUnitId,
        DateTimeOffset createdAtUtc)
    {
        var operationId = Guid.NewGuid();
        return WarehouseTransitionOutbox.EnqueueAsync(
            operationId,
            WarehouseTransitionKind.Release,
            missionId,
            loadUnitId,
            JsonSerializer.Serialize(new ReleaseLoadUnitRequest(loadUnitId, false, "wcs", operationId)),
            createdAtUtc);
    }

    private sealed class TransitionTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan elapsed) => utcNow += elapsed;
    }
}
