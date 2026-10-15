using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Engine.Persistence;
using Xunit;

namespace Wcs.Engine.Test;

/// <summary>
/// La migrazione del segno «segnalazioni del cambio cella partite»
/// (<c>PathMission.ChangeOfCellReportsSent</c>, voce 39, secondo giro, compito 9), vista dal punto di
/// vista che conta: un database con missioni scritte prima del segno.
/// </summary>
/// <remarks>
/// Le righe di prima valgono «nessuna segnalazione partita». Con il segno alzato, la ricarica
/// chiuderebbe ogni estrazione in viaggio al momento dell'aggiornamento (<c>DbInfo.PrepareLoadedMission</c>).
/// </remarks>
public sealed class MissionChangeOfCellReportsSentMigrationTest : IDisposable
{
    /// <summary>L'ultima migrazione <b>prima</b> di quella in prova: lo schema di ieri, senza la colonna.</summary>
    private const string BeforeTheMark = "20261002141049_AddMissionStorageDepthAfterPickUp";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"wcs-reports-sent-{Guid.NewGuid():N}");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    /// <summary>
    /// Un'estrazione (tipo 1) scritta con lo schema di prima: dopo la migrazione la riga c'e' ancora,
    /// con il suo tipo, e il segno delle segnalazioni partite e' giu'.
    /// </summary>
    [Fact]
    public async Task AnExtractionWrittenBeforeTheMarkComesBackWithoutIt()
    {
        var missionId = Guid.NewGuid();
        SeedAnExtractionAtThePreviousSchema(missionId);

        using var database = WcsDatabase.Open(_directory);
        await using var context = database.CreateContext();

        var row = Assert.Single(await context.Missions.AsNoTracking().ToListAsync());
        Assert.Equal(missionId, row.MissionId);
        Assert.Equal((short)1, row.MissionType);
        Assert.False(row.ChangeOfCellReportsSent);
    }

    /// <summary>
    /// Porta il database allo schema precedente alla migrazione in prova e ci scrive un'estrazione con
    /// SQL grezzo, come stava allora: il modello di oggi scriverebbe anche la colonna nuova.
    /// </summary>
    private void SeedAnExtractionAtThePreviousSchema(Guid missionId)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, WcsDatabase.OperationalFileName);
        var options = new DbContextOptionsBuilder<WcsDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString())
            .Options;

        using var context = new WcsDbContext(options);
        context.GetInfrastructure().GetRequiredService<IMigrator>().Migrate(BeforeTheMark);

        context.Database.ExecuteSqlRaw(
            "INSERT INTO Missions (MissionId, LoadUnitId, Date, StorageDoorCode, Status, MissionType, OperationType, " +
            "BufferCellId, ExtractionQueueId, LoadUnitTypeId, HandlingMode, StorageDepth, NoteAssignmentId, " +
            "NoteProcessingId, ActiveFilter, Tare, ReallocatedDestination, OperatorDestination, StorageCellLost, " +
            "StorageCellReserved) " +
            "VALUES ({0}, '42', '2026-10-08 10:00:00', NULL, 0, 1, 1, '0', '7', '0', 0, 150, '0', '0', 0, '0', 0, 0, 0, 0);",
            missionId.ToString().ToUpperInvariant());
    }
}
