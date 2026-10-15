using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Engine.Api;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>Le versioni persistenti che rendono condizionali le scritture di geometria.</summary>
public sealed class GeometryVersioningTest : IDisposable
{
    private const string BeforeGeometryVersion = "20260922083603_AddPendingExtractionOutcomes";
    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"wcs-geometry-version-{Guid.NewGuid():N}");
    private readonly NavigatorStatusTypes _previousStatus = Navigator.NavStatus;

    public GeometryVersioningTest()
    {
        Navigator.Store = _database;
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
    }

    public void Dispose()
    {
        Navigator.Store = null;
        Navigator.NavStatus = _previousStatus;
        _database.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task ANewItemGetsANonEmptyGeometryVersion()
    {
        var itemId = Guid.NewGuid();
        await using (var context = _database.CreateContext())
        {
            await PlantStore.UpsertItemAsync(context, new PlantItem { ItemId = itemId, Name = "new" });
            await context.SaveChangesAsync();
        }

        await using var verify = _database.CreateContext();
        var row = await verify.PlantItems.AsNoTracking().SingleAsync();
        Assert.Equal(itemId, row.ItemId);
        Assert.NotEqual(Guid.Empty, row.GeometryVersion);
    }

    [Fact]
    public async Task ConfigurationUpdatesPreserveGeometryAndItsVersion()
    {
        var itemId = Guid.NewGuid();
        var version = Guid.NewGuid();
        await using (var context = _database.CreateContext())
        {
            context.PlantItems.Add(new PlantItem
            {
                ItemId = itemId,
                Name = "before",
                X = 11,
                Y = 22,
                Width = 33,
                Height = 44,
                Rotation = 55,
                GeometryVersion = version
            });
            await context.SaveChangesAsync();
        }

        await using (var context = _database.CreateContext())
        {
            await PlantStore.UpsertItemAsync(context, new PlantItem { ItemId = itemId, Name = "after" });
            await context.SaveChangesAsync();
        }

        await using var verify = _database.CreateContext();
        var row = await verify.PlantItems.AsNoTracking().SingleAsync(i => i.ItemId == itemId);
        Assert.Equal("after", row.Name);
        Assert.Equal(11, row.X);
        Assert.Equal(22, row.Y);
        Assert.Equal(33, row.Width);
        Assert.Equal(44, row.Height);
        Assert.Equal(55, row.Rotation);
        Assert.Equal(version, row.GeometryVersion);
    }

    [Fact]
    public async Task GeometryAndVersionAreReadFromTheSameSnapshot()
    {
        var itemId = Guid.NewGuid();
        var version = Guid.NewGuid();
        await using (var context = _database.CreateContext())
        {
            context.PlantItems.Add(new PlantItem
            {
                ItemId = itemId,
                X = 10,
                Y = 20,
                Width = 30,
                Height = 40,
                Rotation = 50,
                GeometryVersion = version
            });
            await context.SaveChangesAsync();
        }

        await using var verify = _database.CreateContext();
        var loaded = (await PlantStore.LoadGeometriesAsync(verify))[itemId];

        Assert.Equal(new ItemGeometry(10, 20, 30, 40, 50), loaded.Geometry);
        Assert.Equal(version, loaded.Version);
    }

    [Fact]
    public async Task MigrationKeepsExistingGeometryAndAddsTheInitialVersion()
    {
        var itemId = Guid.NewGuid();
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, WcsDatabase.OperationalFileName);
        var options = new DbContextOptionsBuilder<WcsDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString())
            .Options;

        await using (var previous = new WcsDbContext(options))
        {
            previous.GetInfrastructure().GetRequiredService<IMigrator>().Migrate(BeforeGeometryVersion);
            await InsertLegacyItemAsync(previous, itemId);
        }

        using var migrated = WcsDatabase.Open(_directory);
        await using (var verify = migrated.CreateContext())
        {
            var row = await verify.PlantItems.AsNoTracking().SingleAsync();
            Assert.Equal(itemId, row.ItemId);
            Assert.Equal(11, row.X);
            Assert.Equal(22, row.Y);
            Assert.Equal(33, row.Width);
            Assert.Equal(44, row.Height);
            Assert.Equal(55, row.Rotation);
            Assert.Equal(Guid.Empty, row.GeometryVersion);
        }

        GeometryWriteOutcome firstWrite;
        await using (var write = migrated.CreateContext())
        {
            firstWrite = await PlantStore.SetGeometriesAsync(write,
            [
                new GeometryWrite(itemId, new ItemGeometry(60, 70, 80, 90, 100), Guid.Empty)
            ]);
            await write.SaveChangesAsync();
        }

        Assert.Empty(firstWrite.MissingItemIds);
        Assert.Empty(firstWrite.ConflictingItemIds);
        var confirmation = Assert.Single(firstWrite.Written);
        Assert.NotEqual(Guid.Empty, confirmation.GeometryVersion);
        await using var afterWrite = migrated.CreateContext();
        var written = await afterWrite.PlantItems.AsNoTracking().SingleAsync();
        Assert.Equal((60d, 70d, 80d, 90d, 100d),
            (written.X, written.Y, written.Width, written.Height, written.Rotation));
        Assert.Equal(confirmation.GeometryVersion, written.GeometryVersion);
    }

    [Fact]
    public async Task AVersionedWriteChangesGeometryAndReturnsANewVersion()
    {
        var itemId = Guid.NewGuid();
        await using (var context = _database.CreateContext())
        {
            await PlantStore.UpsertItemAsync(context, new PlantItem { ItemId = itemId });
            await context.SaveChangesAsync();
        }
        var before = (await PlantQueries.ItemGeometriesAsync())[itemId];

        var outcome = await PlantQueries.SetItemGeometryAsync(new GeometryWrite(
            itemId, new ItemGeometry(10, 20, 30, 40, 50), before.Version));

        Assert.Empty(outcome.MissingItemIds);
        Assert.Empty(outcome.ConflictingItemIds);
        var written = Assert.Single(outcome.Written);
        Assert.Equal(itemId, written.ItemId);
        Assert.NotEqual(Guid.Empty, written.GeometryVersion);
        Assert.NotEqual(before.Version, written.GeometryVersion);
        var after = (await PlantQueries.ItemGeometriesAsync())[itemId];
        Assert.Equal(new ItemGeometry(10, 20, 30, 40, 50), after.Geometry);
        Assert.Equal(written.GeometryVersion, after.Version);
    }

    [Fact]
    public async Task AWriteWithTheOldVersionIsRejectedWithoutChangingTheWinner()
    {
        var itemId = Guid.NewGuid();
        await using (var context = _database.CreateContext())
        {
            await PlantStore.UpsertItemAsync(context, new PlantItem { ItemId = itemId });
            await context.SaveChangesAsync();
        }
        var originalVersion = (await PlantQueries.ItemGeometriesAsync())[itemId].Version;
        var first = await PlantQueries.SetItemGeometryAsync(new GeometryWrite(
            itemId, new ItemGeometry(1, 2, 3, 4, 5), originalVersion));
        var winningVersion = Assert.Single(first.Written).GeometryVersion;

        var stale = await PlantQueries.SetItemGeometryAsync(new GeometryWrite(
            itemId, new ItemGeometry(90, 91, 92, 93, 94), originalVersion));

        Assert.Empty(stale.Written);
        Assert.Empty(stale.MissingItemIds);
        Assert.Equal([itemId], stale.ConflictingItemIds);
        var current = (await PlantQueries.ItemGeometriesAsync())[itemId];
        Assert.Equal(new ItemGeometry(1, 2, 3, 4, 5), current.Geometry);
        Assert.Equal(winningVersion, current.Version);
    }

    [Fact]
    public async Task OneStaleMemberMakesTheWholeGroupLeaveEveryGeometryUnchanged()
    {
        var firstId = Guid.NewGuid();
        var staleId = Guid.NewGuid();
        await using (var context = _database.CreateContext())
        {
            await PlantStore.UpsertItemAsync(context, new PlantItem { ItemId = firstId });
            await PlantStore.UpsertItemAsync(context, new PlantItem { ItemId = staleId });
            await context.SaveChangesAsync();
        }
        var before = await PlantQueries.ItemGeometriesAsync();

        var outcome = await PlantQueries.SetItemGeometriesAsync(
        [
            new GeometryWrite(firstId, new ItemGeometry(10, 10, 10, 10, 10), before[firstId].Version),
            new GeometryWrite(staleId, new ItemGeometry(20, 20, 20, 20, 20), Guid.Empty)
        ]);

        Assert.Empty(outcome.Written);
        Assert.Empty(outcome.MissingItemIds);
        Assert.Equal([staleId], outcome.ConflictingItemIds);
        var after = await PlantQueries.ItemGeometriesAsync();
        Assert.Equal(ItemGeometry.None, after[firstId].Geometry);
        Assert.Equal(before[firstId].Version, after[firstId].Version);
        Assert.Equal(ItemGeometry.None, after[staleId].Geometry);
        Assert.Equal(before[staleId].Version, after[staleId].Version);
    }

    [Fact]
    public async Task AMissingMemberMakesTheWholeGroupLeaveKnownItemsUnchanged()
    {
        var knownId = Guid.NewGuid();
        var missingId = Guid.NewGuid();
        await using (var context = _database.CreateContext())
        {
            await PlantStore.UpsertItemAsync(context, new PlantItem { ItemId = knownId });
            await context.SaveChangesAsync();
        }
        var before = (await PlantQueries.ItemGeometriesAsync())[knownId];

        var outcome = await PlantQueries.SetItemGeometriesAsync(
        [
            new GeometryWrite(knownId, new ItemGeometry(10, 10, 10, 10, 10), before.Version),
            new GeometryWrite(missingId, new ItemGeometry(20, 20, 20, 20, 20), Guid.NewGuid())
        ]);

        Assert.Empty(outcome.Written);
        Assert.Equal([missingId], outcome.MissingItemIds);
        Assert.Empty(outcome.ConflictingItemIds);
        var after = (await PlantQueries.ItemGeometriesAsync())[knownId];
        Assert.Equal(ItemGeometry.None, after.Geometry);
        Assert.Equal(before.Version, after.Version);
    }

    [Fact]
    public async Task DuplicateItemIdsAreRejectedBeforeAnyGeometryChanges()
    {
        var itemId = Guid.NewGuid();
        await using (var context = _database.CreateContext())
        {
            await PlantStore.UpsertItemAsync(context, new PlantItem { ItemId = itemId });
            await context.SaveChangesAsync();
        }
        var before = (await PlantQueries.ItemGeometriesAsync())[itemId];

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            PlantQueries.SetItemGeometriesAsync(
            [
                new GeometryWrite(itemId, new ItemGeometry(10, 10, 10, 10, 10), before.Version),
                new GeometryWrite(itemId, new ItemGeometry(20, 20, 20, 20, 20), before.Version)
            ]));

        Assert.Equal("requests", error.ParamName);
        var after = (await PlantQueries.ItemGeometriesAsync())[itemId];
        Assert.Equal(ItemGeometry.None, after.Geometry);
        Assert.Equal(before.Version, after.Version);
    }

    [Fact]
    public async Task RecreatingTheSameItemIdInvalidatesWritesForTheDeletedRow()
    {
        var itemId = Guid.NewGuid();
        await using (var context = _database.CreateContext())
        {
            await PlantStore.UpsertItemAsync(context, new PlantItem { ItemId = itemId });
            await context.SaveChangesAsync();
        }
        var deletedVersion = (await PlantQueries.ItemGeometriesAsync())[itemId].Version;

        await using (var context = _database.CreateContext())
        {
            await PlantStore.DeleteItemAsync(context, itemId);
            await context.SaveChangesAsync();
        }
        await using (var context = _database.CreateContext())
        {
            await PlantStore.UpsertItemAsync(context, new PlantItem { ItemId = itemId });
            await context.SaveChangesAsync();
        }
        var recreated = (await PlantQueries.ItemGeometriesAsync())[itemId];
        Assert.NotEqual(Guid.Empty, recreated.Version);
        Assert.NotEqual(deletedVersion, recreated.Version);

        var stale = await PlantQueries.SetItemGeometryAsync(new GeometryWrite(
            itemId, new ItemGeometry(90, 91, 92, 93, 94), deletedVersion));

        Assert.Equal([itemId], stale.ConflictingItemIds);
        var after = (await PlantQueries.ItemGeometriesAsync())[itemId];
        Assert.Equal(ItemGeometry.None, after.Geometry);
        Assert.Equal(recreated.Version, after.Version);
    }

    /// <summary>
    /// Inserisce una riga nello schema precedente usando le colonne obbligatorie che quello schema
    /// dichiara. Il modello corrente non puo' essere usato: chiederebbe gia' GeometryVersion.
    /// </summary>
    private static async Task InsertLegacyItemAsync(WcsDbContext context, Guid itemId)
    {
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();
        var columns = new List<(string Name, bool Required)>();
        await using (var schema = connection.CreateCommand())
        {
            schema.CommandText = "PRAGMA table_info('PlantItems');";
            await using var reader = await schema.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                columns.Add((reader.GetString(1), reader.GetInt64(3) != 0 || reader.GetInt64(5) != 0));
        }

        var geometryNames = new HashSet<string>(["X", "Y", "Width", "Height", "Rotation"]);
        var written = columns.Where(c => c.Required || geometryNames.Contains(c.Name)).ToList();
        await using var insert = connection.CreateCommand();
        insert.CommandText =
            $"INSERT INTO PlantItems ({string.Join(", ", written.Select(c => $"\"{c.Name}\""))}) " +
            $"VALUES ({string.Join(", ", written.Select(c => c.Name switch
            {
                "ItemId" => "$itemId",
                "X" => "11",
                "Y" => "22",
                "Width" => "33",
                "Height" => "44",
                "Rotation" => "55",
                _ => "0"
            }))});";
        // Il provider salva i Guid nel formato testuale canonico usato anche dai parametri EF.
        // Una stringa costruita a mano avrebbe un confronto case-sensitive diverso in SQLite.
        insert.Parameters.Add(new SqliteParameter("$itemId", itemId));
        await insert.ExecuteNonQueryAsync();
    }
}
