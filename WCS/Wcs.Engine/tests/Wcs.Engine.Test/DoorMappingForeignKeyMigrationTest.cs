using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Engine.Persistence;
using Xunit;

namespace Wcs.Engine.Test;

/// <summary>
/// La migrazione che porta la chiave esterna di <c>DoorMappings.ItemId</c> verso <c>PlantItems</c>,
/// vista dal punto di vista che conta: <b>un database che le righe orfane ce le ha gia'</b>.
/// </summary>
/// <remarks>
/// SQLite non aggiunge una chiave esterna a una tabella che c'e': ricostruisce la tabella e ci
/// ricopia dentro le righe. Con una riga orfana la ricopiatura fallisce con «FOREIGN KEY constraint
/// failed» e l'host non parte piu' - per questo la migrazione cancella le orfane prima, e per questo
/// vale la pena partire da un database vero della versione precedente invece che dal modello di
/// oggi. Sull'impianto di collaudo di orfane non ce n'era nessuna su 2.729 righe (misura riportata
/// dal brief del lotto, 2026-09-18, non rifatta qui): senza un database costruito apposta questa
/// strada non la percorrerebbe nessuno.
/// </remarks>
public sealed class DoorMappingForeignKeyMigrationTest : IDisposable
{
    /// <summary>L'ultima migrazione <b>prima</b> di quella in prova: il punto da cui si parte per avere lo schema di ieri, senza la chiave esterna.</summary>
    private const string BeforeTheForeignKey = "20260915175619_AddPlantDecoration";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"wcs-fk-{Guid.NewGuid():N}");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    /// <summary>
    /// Due righe di mappa porte su un dispositivo che non c'e' piu', piu' una su un dispositivo vero:
    /// dopo la migrazione le orfane sono sparite, la sana e' intatta, e il registro del motore dice
    /// quante erano e su quale identificativo.
    /// </summary>
    [Fact]
    public async Task TheMigrationDropsTheOrphanRowsKeepsTheGoodOnesAndSaysHowManyAndWhich()
    {
        var orphan = Guid.NewGuid();
        var real = Guid.NewGuid();
        SeedAtPreviousSchema(
            realItems: [real],
            mappings: [(orphan, "PORTA-ORFANA-A", 1), (orphan, "PORTA-ORFANA-B", 2), (real, "PORTA-BUONA", 3)]);

        using var log = new RecordingLogWriter();
        using (var database = WcsDatabase.Open(_directory))
        {
            // Con un tetto, e non e' pignoleria: Navigator.WriteLog manda la riga da dentro un
            // Task.Run, quindi si aspetta - ma un'attesa senza tetto, se la riga non arriva piu',
            // non fa fallire questa prova: appende il processo, e una suite che non finisce non ha
            // nessun rosso da mostrare. MISURATO togliendo la riga di registro dallo store: senza
            // questo tetto la suite del motore restava appesa invece di diventare rossa.
            await log.Wait((line, _) => line.Contains("mappa porte", StringComparison.Ordinal))
                .WaitAsync(TimeSpan.FromSeconds(30));

            await using var context = database.CreateContext();
            Assert.False(await context.DoorMappings.AnyAsync(m => m.ItemId == orphan));
            var kept = Assert.Single(await context.DoorMappings.Where(m => m.ItemId == real).ToListAsync());
            Assert.Equal("PORTA-BUONA", kept.DoorCode);
        }

        var said = Assert.Single(log.Lines, line => line.Description.Contains("mappa porte", StringComparison.Ordinal));
        Assert.Contains("2 riga/e", said.Description, StringComparison.Ordinal);
        Assert.Contains(orphan.ToString(), said.Description, StringComparison.Ordinal);
        Assert.DoesNotContain(real.ToString(), said.Description, StringComparison.Ordinal);
        Assert.Equal(Log.Interface.LogEntryType.Warning, said.Level);
    }

    /// <summary>
    /// Il vincolo c'e' davvero dopo la migrazione: una riga orfana scritta ora viene rifiutata dal
    /// database, e cancellare un dispositivo si porta via la sua mappa.
    /// </summary>
    /// <remarks>
    /// La prima meta' e' quella che non poteva fallire prima del lotto e ora si': sull'impianto di
    /// collaudo <c>LinkBufferCellCommand</c> riusciva proprio cosi', lasciando una riga che nessuna
    /// vista mostrava piu'.
    /// </remarks>
    [Fact]
    public async Task AfterTheMigrationAnOrphanRowIsRefusedAndDeletingADeviceTakesItsMapWithIt()
    {
        var real = Guid.NewGuid();
        SeedAtPreviousSchema(realItems: [real], mappings: [(real, "PORTA-BUONA", 3)]);

        using var log = new RecordingLogWriter();
        using var database = WcsDatabase.Open(_directory);

        await using (var context = database.CreateContext())
        {
            context.DoorMappings.Add(new Persistence.Entities.DoorMapping { ItemId = Guid.NewGuid(), DoorCode = "NUOVA-ORFANA", CellId = 9 });
            var refused = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            Assert.Contains("FOREIGN KEY", refused.InnerException!.Message, StringComparison.OrdinalIgnoreCase);
        }

        await using (var context = database.CreateContext())
        {
            Navigator.Store = database;
            try
            {
                await PlantStore.DeleteItemAsync(context, real);
                await context.SaveChangesAsync();
            }
            finally
            {
                Navigator.Store = null;
            }

            Assert.Empty(await context.DoorMappings.ToListAsync());
        }

        // Nessuna riga di registro: su un database senza orfane la migrazione non ha niente da dire,
        // e dirlo lo stesso sarebbe rumore su ogni avvio.
        Assert.DoesNotContain(log.Lines, line => line.Description.Contains("mappa porte", StringComparison.Ordinal));
    }

    /// <summary>
    /// Su un database <b>appena nato</b> non c'e' niente da dire: la tabella <c>DoorMappings</c> non
    /// esiste ancora, e la guardia «nessuna migrazione applicata» e' quello che tiene fuori, dal primo
    /// avvio di ogni impianto nuovo, un avviso che parla di righe orfane e in realta' dice solo che la
    /// tabella non c'e'.
    /// </summary>
    /// <remarks>
    /// MISURATO: tolta quella guardia, questo caso passa alla <c>SELECT</c>, che fallisce con
    /// «no such table: DoorMappings», e il registro dell'avvio riceve
    /// «non sono riuscito a contare le righe orfane della mappa porte». Nessuna prova della suite lo
    /// vedeva: quella sopra parte da un database gia' migrato, dove la guardia non serve.
    /// <para>
    /// Si guarda il valore restituito, non il registro: <c>Navigator.WriteLog</c> manda la riga da
    /// dentro un <c>Task.Run</c>, e un'asserzione «il registro non dice niente» sarebbe verde anche
    /// un istante prima che la riga arrivi - una prova che non puo' fallire.
    /// </para>
    /// </remarks>
    [Fact]
    public void OnABrandNewDatabaseThereIsNothingToSayAboutOrphanRows()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, WcsDatabase.OperationalFileName);
        var options = new DbContextOptionsBuilder<WcsDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString())
            .Options;

        using var context = new WcsDbContext(options);

        // Le due premesse, dette e non date per buone: il database e' davvero vergine, e la
        // migrazione della chiave esterna e' davvero fra quelle da applicare - senza la seconda il
        // metodo uscirebbe per l'altra porta e questa prova non direbbe niente.
        Assert.Empty(context.Database.GetAppliedMigrations());
        Assert.Contains(WcsDatabase.DoorMappingForeignKeyMigration, context.Database.GetPendingMigrations());

        Assert.Null(WcsDatabase.DescribeOrphanDoorMappings(context));
    }

    /// <summary>
    /// Porta il database allo schema <b>precedente</b> alla migrazione in prova e ci scrive dentro
    /// dispositivi e mappature come stavano allora - le mappature con SQL grezzo, perche' il modello
    /// di oggi non permetterebbe piu' di scrivere un'orfana.
    /// </summary>
    private void SeedAtPreviousSchema(Guid[] realItems, (Guid ItemId, string DoorCode, decimal CellId)[] mappings)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, WcsDatabase.OperationalFileName);
        var options = new DbContextOptionsBuilder<WcsDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString())
            .Options;

        using var context = new WcsDbContext(options);
        context.GetInfrastructure().GetRequiredService<IMigrator>().Migrate(BeforeTheForeignKey);

        SeedPlantItemsWithTheHistoricalSchema(context, realItems);

        foreach (var (itemId, doorCode, cellId) in mappings)
            context.Database.ExecuteSqlRaw(
                "INSERT INTO DoorMappings (ItemId, DoorCode, CellId, Side, OperationActive, StoringLocked) VALUES ({0}, {1}, {2}, 'A', 0, 0);",
                itemId, doorCode, cellId);
    }

    /// <summary>
    /// Inserisce soltanto dati validi per lo schema storico. Usare qui il modello EF corrente
    /// renderebbe la prova dipendente dalle colonne aggiunte dopo la migrazione sotto esame (per
    /// esempio GeometryVersion), che nello schema storico non devono ancora esistere.
    /// </summary>
    private static void SeedPlantItemsWithTheHistoricalSchema(WcsDbContext context, IEnumerable<Guid> itemIds)
    {
        context.Database.OpenConnection();
        var connection = context.Database.GetDbConnection();
        var columns = new List<(string Name, string Type)>();

        using (var schema = connection.CreateCommand())
        {
            schema.CommandText = "PRAGMA table_info('PlantItems');";
            using var reader = schema.ExecuteReader();
            while (reader.Read())
            {
                var name = reader.GetString(1);
                var type = reader.GetString(2);
                var isRequired = reader.GetInt64(3) != 0;
                if (name == "ItemId" || isRequired) columns.Add((name, type));
            }
        }

        foreach (var itemId in itemIds)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = $"INSERT INTO PlantItems ({string.Join(", ", columns.Select(c => $"\"{c.Name}\""))}) " +
                                 $"VALUES ({string.Join(", ", columns.Select((_, index) => $"@p{index}"))});";

            for (var index = 0; index < columns.Count; index++)
            {
                var column = columns[index];
                var parameter = insert.CreateParameter();
                parameter.ParameterName = $"@p{index}";
                parameter.Value = column.Name == "ItemId"
                    ? itemId
                    : column.Type.Contains("TEXT", StringComparison.OrdinalIgnoreCase) ? "0"
                    : column.Type.Contains("BLOB", StringComparison.OrdinalIgnoreCase) ? Array.Empty<byte>()
                    : 0;
                insert.Parameters.Add(parameter);
            }

            insert.ExecuteNonQuery();
        }
    }
}
