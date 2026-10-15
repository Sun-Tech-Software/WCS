using Microsoft.EntityFrameworkCore;
using Wcs.Engine.Api;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Xunit;

namespace Wcs.Engine.Test;

public sealed class PlantBackupSnapshotTest : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(),
        $"wcs-backup-snapshot-{Guid.NewGuid():N}");
    private readonly WcsDatabase _store;
    private readonly WcsDatabase? _previousStore = Navigator.Store;

    public PlantBackupSnapshotTest()
    {
        _store = WcsDatabase.Open(_directory);
        Navigator.Store = _store;
    }

    public void Dispose()
    {
        Navigator.Store = _previousStore;
        _store.Dispose();
        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Le connessioni del pool SQLite possono trattenere per poco il file su Windows.
        }
    }

    [Fact]
    public async Task BackupReadsOneDatabaseSnapshotWhileAReplacementCommits()
    {
        var itemId = Guid.NewGuid();
        await using (var seed = _store.CreateContext())
        {
            seed.PlantItems.Add(new PlantItem { ItemId = itemId, DeviceConnectionId = 1 });
            seed.PlcConnections.Add(new PlcConnection { Id = 1, Description = "before" });
            await seed.SaveChangesAsync();
        }

        var itemsRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueBackup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backupTask = PlantQueries.ReadPlantBackupCoreAsync(async () =>
        {
            itemsRead.SetResult();
            await continueBackup.Task;
        });
        await itemsRead.Task;

        await using (var replacing = _store.CreateContext())
        {
            var item = await replacing.PlantItems.SingleAsync(row => row.ItemId == itemId);
            item.DeviceConnectionId = 2;
            replacing.PlcConnections.Remove(await replacing.PlcConnections.SingleAsync(row => row.Id == 1));
            replacing.PlcConnections.Add(new PlcConnection { Id = 2, Description = "after" });
            await replacing.SaveChangesAsync();
        }
        continueBackup.SetResult();

        var backup = await backupTask;
        Assert.Equal(1, Assert.Single(backup.Items).DeviceConnectionId);
        var connection = Assert.Single(backup.PlcConnections);
        Assert.Equal((1L, "before"), (connection.Id, connection.Description));
    }
}
