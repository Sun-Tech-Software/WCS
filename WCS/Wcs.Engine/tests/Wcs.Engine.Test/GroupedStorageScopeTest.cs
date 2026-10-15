using Wcs.Engine.Items;
using Wcs.Engine.Items.Managers;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// Il giro di ricerca raggruppato possiede soltanto i blocchi che riesce a prendere
/// atomicamente. Due giri non possono quindi rilasciare il blocco l'uno dell'altro.
/// </summary>
public sealed class GroupedStorageScopeTest : IAsyncLifetime
{
    private WcsDatabase _store = null!;
    private QueuedDatabaseWriter<WcsWrite> _writer = null!;

    public Task InitializeAsync()
    {
        _store = WcsDatabase.InMemory();
        _writer = new QueuedDatabaseWriter<WcsWrite>(new WcsBatchExecutor(_store).ExecuteAsync,
            new QueuedDatabaseWriterOptions
            {
                DropOldestWhenFull = false,
                MaxBatchSize = 100,
                FlushInterval = TimeSpan.FromMilliseconds(10)
            });
        Navigator.Store = _store;
        Navigator.StateWriter = _writer;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        Navigator.StateWriter = null;
        Navigator.Store = null;
        await _writer.StopAsync();
        _store.Dispose();
    }

    [Fact]
    public async Task OnlyOneScopeOwnsAFreeDoor()
    {
        await SeedDoorAsync("P01A", storingLocked: false);
        var first = Scope();
        var second = Scope();

        var results = await Task.WhenAll(
            first.TryAcquireDoorAsync("P01A"),
            second.TryAcquireDoorAsync("P01A"));

        Assert.Equal(1, results.Count(result => result));
        await first.RestoreAsync(null);
        await second.RestoreAsync(null);
        Assert.False(await ORM.DbInfo.GetDoorLockedForStoring("P01A"));
    }

    [Fact]
    public async Task APreExistingDoorLockIsNeverOwnedOrReleased()
    {
        await SeedDoorAsync("P01A", storingLocked: true);
        var scope = Scope();

        Assert.False(await scope.TryAcquireDoorAsync("P01A"));
        await scope.RestoreAsync(null);

        Assert.True(await ORM.DbInfo.GetDoorLockedForStoring("P01A"));
    }

    [Fact]
    public async Task ARepeatedDoorIsAcquiredAndReleasedOnce()
    {
        var acquisitions = 0;
        var releases = 0;
        var scope = new GroupedStorageScope(
            Device(),
            (_, _) => { },
            _ =>
            {
                acquisitions++;
                return Task.FromResult(true);
            },
            _ =>
            {
                releases++;
                return Task.CompletedTask;
            });

        Assert.True(await scope.TryAcquireDoorAsync("P01A"));
        Assert.True(await scope.TryAcquireDoorAsync("p01a"));
        await scope.RestoreAsync(null);

        Assert.Equal(1, acquisitions);
        Assert.Equal(1, releases);
    }

    [Fact]
    public async Task OneUnlockFailureDoesNotSkipOtherUnlocksOrStatusRestoration()
    {
        var attemptedUnlocks = new List<string>();
        var cleanupLog = new List<string>();
        var device = Device();
        var position = Device();
        position.Status = DeviceStatusTypes.Warning;
        var scope = new GroupedStorageScope(
            device,
            (resource, _) => cleanupLog.Add(resource),
            _ => Task.FromResult(true),
            door =>
            {
                attemptedUnlocks.Add(door);
                return door == "P01A"
                    ? Task.FromException(new InvalidOperationException("unlock failed"))
                    : Task.CompletedTask;
            });
        scope.TrackPosition(position);
        await scope.TryAcquireDoorAsync("P01A");
        await scope.TryAcquireDoorAsync("P02A");
        device.Status = DeviceStatusTypes.Recovering;
        position.Status = DeviceStatusTypes.Recovering;

        var error = await Assert.ThrowsAsync<AggregateException>(() => scope.RestoreAsync(null));

        Assert.Single(error.InnerExceptions);
        Assert.Equal(["P01A", "P02A"], attemptedUnlocks);
        Assert.Contains(cleanupLog, resource => resource.Contains("P01A", StringComparison.Ordinal));
        Assert.Equal(DeviceStatusTypes.Ready, device.Status);
        Assert.Equal(DeviceStatusTypes.Warning, position.Status);
    }

    [Fact]
    public async Task CleanupFailureIsAttachedWithoutReplacingTheBodyFailure()
    {
        var primary = new InvalidOperationException("selection failed");
        var scope = new GroupedStorageScope(
            Device(),
            (_, _) => { },
            _ => Task.FromResult(true),
            _ => Task.FromException(new InvalidOperationException("unlock failed")));
        await scope.TryAcquireDoorAsync("P01A");

        await scope.RestoreAsync(primary);

        var cleanup = Assert.IsType<AggregateException>(
            primary.Data["GroupedStorageCleanupFailures"]);
        Assert.Single(cleanup.InnerExceptions);
    }

    [Fact]
    public async Task AFailingCleanupLoggerCannotStopLaterCleanup()
    {
        var attemptedUnlocks = new List<string>();
        var device = Device();
        var scope = new GroupedStorageScope(
            device,
            (_, _) => throw new InvalidOperationException("logger failed"),
            _ => Task.FromResult(true),
            door =>
            {
                attemptedUnlocks.Add(door);
                return door == "P01A"
                    ? Task.FromException(new InvalidOperationException("unlock failed"))
                    : Task.CompletedTask;
            });
        await scope.TryAcquireDoorAsync("P01A");
        await scope.TryAcquireDoorAsync("P02A");
        device.Status = DeviceStatusTypes.Recovering;

        var error = await Assert.ThrowsAsync<AggregateException>(() => scope.RestoreAsync(null));

        Assert.Single(error.InnerExceptions);
        Assert.Equal(["P01A", "P02A"], attemptedUnlocks);
        Assert.Equal(DeviceStatusTypes.Ready, device.Status);
    }

    /// <summary>
    /// Il giro mette il gruppo e una posizione in Recovering; intanto un membro va in errore e manda il
    /// gruppo in Warning. Alla fine il giro rimette la posizione com'era, ma non copre il Warning del
    /// gruppo con lo stato preso all'inizio: il gruppo aspetta l'operatore. Prima tornava Ready.
    /// </summary>
    [Fact]
    public async Task AWarningRaisedDuringTheSearchIsNotCoveredByTheRestore()
    {
        var device = Device();
        var position = Device();
        var scope = new GroupedStorageScope(device, (_, _) => { }, _ => Task.FromResult(true), _ => Task.CompletedTask);
        scope.TrackPosition(position);
        device.Status = DeviceStatusTypes.Recovering;
        position.Status = DeviceStatusTypes.Recovering;

        device.Status = DeviceStatusTypes.Warning;
        await scope.RestoreAsync(null);

        Assert.Equal(DeviceStatusTypes.Warning, device.Status);
        Assert.Equal(DeviceStatusTypes.Ready, position.Status);
    }

    private static GroupedStorageScope Scope() => new(Device(), (_, _) => { });

    private static AutomationDevice Device() => new(Guid.NewGuid())
    {
        Status = DeviceStatusTypes.Ready
    };

    private static async Task SeedDoorAsync(string doorCode, bool storingLocked)
    {
        var itemId = Guid.NewGuid();
        await PlantRows.EnsureAsync(itemId);
        await PlantPersistence.SaveDoorMappingsAsync(itemId,
        [
            new DoorMapping
            {
                ItemId = itemId,
                DoorCode = doorCode,
                CellId = 1,
                Side = "A",
                StoringLocked = storingLocked
            }
        ]);
    }
}
