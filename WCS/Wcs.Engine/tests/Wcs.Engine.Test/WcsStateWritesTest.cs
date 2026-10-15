using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Xunit;

namespace Wcs.Engine.Test;

public sealed class WcsStateWritesTest
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExclusiveFallbackPreventsWorkFromCommittingBetweenCheckAndGraphWrite(bool file, bool queue)
    {
        var directory = file ? Directory.CreateTempSubdirectory("wcs-exclusive-") : null;
        var previousStore = Navigator.Store;
        var previousWriter = Navigator.StateWriter;
        try
        {
            using var store = file ? WcsDatabase.Open(directory!.FullName) : WcsDatabase.InMemory();
            Navigator.Store = store;
            Navigator.StateWriter = null;
            var oldId = Guid.NewGuid(); var newId = Guid.NewGuid(); var competingMissionId = Guid.NewGuid();
            await using (var seed = store.CreateContext())
            {
                seed.PlantItems.Add(new PlantItem { ItemId = oldId, Name = "Old" });
                await seed.SaveChangesAsync();
            }
            await using var competitor = store.CreateContext();
            competitor.Database.SetCommandTimeout(1);
            if (queue) competitor.ExtractionQueue.Add(new ExtractionQueueItem { SourceItemId = oldId });
            else competitor.Missions.Add(new Mission { MissionId = competingMissionId });
            Exception? concurrentFailure = null;

            await WcsStateWrites.WriteExclusiveAndWaitAsync(async c =>
            {
                Assert.False(await c.Missions.AnyAsync(mission => mission.MissionId == competingMissionId));
                Assert.False(await c.ExtractionQueue.AnyAsync());
                // The exclusive delegate is suspended until this independent connection finishes
                // its write attempt. No sleeps or scheduling assumptions define the race window.
                concurrentFailure = await Task.Run(() => Record.ExceptionAsync(async () =>
                    await competitor.SaveChangesAsync()));
                c.PlantItems.RemoveRange(await c.PlantItems.ToListAsync());
                c.PlantItems.Add(new PlantItem { ItemId = newId, Name = "New" });
            });

            var updateFailure = Assert.IsType<DbUpdateException>(concurrentFailure);
            var sqliteFailure = Assert.IsType<SqliteException>(updateFailure.InnerException);
            Assert.Contains(sqliteFailure.SqliteErrorCode, new[] { 5, 6 }); // BUSY or LOCKED
            await using (var read = store.CreateContext())
            {
                // Only the rows of this test: Navigator.Store is static, and the device and mission rows
                // that earlier tests leave to background writes (an announcement's UpdateStatus, a
                // mission's PositionWork, queued with Task.Run) land in whatever store is current when
                // they run - this one, sometimes. A stray row is not what this test is about.
                Assert.Equal(newId, (await read.PlantItems.SingleAsync(item => item.ItemId == oldId || item.ItemId == newId)).ItemId);
                Assert.False(await read.Missions.AnyAsync(mission => mission.MissionId == competingMissionId));
                Assert.False(await read.ExtractionQueue.AnyAsync());
            }
            // The lock ends at commit: a later write succeeds, proving it was contention,
            // not malformed work or a permanently unusable second connection.
            await competitor.SaveChangesAsync();
            await using var after = store.CreateContext();
            Assert.Equal(queue ? 1 : 0, await after.ExtractionQueue.CountAsync());
            Assert.Equal(queue ? 0 : 1, await after.Missions.CountAsync(mission => mission.MissionId == competingMissionId));
        }
        finally
        {
            Navigator.Store = previousStore;
            Navigator.StateWriter = previousWriter;
            if (directory != null)
            {
                SqliteConnection.ClearAllPools();
                directory.Delete(recursive: true);
            }
        }
    }
}
