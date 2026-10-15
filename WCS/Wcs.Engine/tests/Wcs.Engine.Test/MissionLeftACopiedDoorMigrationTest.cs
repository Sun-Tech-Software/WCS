using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Engine.Persistence;
using Xunit;

namespace Wcs.Engine.Test;

/// <summary>
/// La migrazione del segno della copia che ha lasciato la porta (<c>PathMission.LeftACopiedDoor</c>,
/// voce 39, secondo giro, compito 12), vista dal punto di vista che conta: un database con missioni
/// scritte prima del segno.
/// </summary>
/// <remarks>
/// Le righe di prima valgono «nessuna porta copiata lasciata»: con il segno alzato, ogni missione
/// senza porta al momento dell'aggiornamento tornerebbe come una copia da rifare, e la ricarica non le
/// costruirebbe la rotta.
/// </remarks>
public sealed class MissionLeftACopiedDoorMigrationTest : IDisposable
{
    /// <summary>L'ultima migrazione <b>prima</b> di quella in prova: lo schema di ieri, senza la colonna.</summary>
    private const string BeforeTheMark = "20261008083745_AddMissionChangeOfCellReportsSent";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"wcs-copied-door-{Guid.NewGuid():N}");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    /// <summary>
    /// Una missione di deposito (tipo 2) ancora senza porta, scritta con lo schema di prima: dopo la
    /// migrazione la riga c'e' ancora, senza porta, e il segno della porta copiata lasciata e' giu'.
    /// </summary>
    [Fact]
    public async Task AMissionWithoutADoorWrittenBeforeTheMarkComesBackWithoutIt()
    {
        var missionId = Guid.NewGuid();
        SeedAMissionWithoutADoorAtThePreviousSchema(missionId);

        using var database = WcsDatabase.Open(_directory);
        await using var context = database.CreateContext();

        var row = Assert.Single(await context.Missions.AsNoTracking().ToListAsync());
        Assert.Equal(missionId, row.MissionId);
        Assert.Equal((short)2, row.MissionType);
        Assert.Null(row.StorageDoorCode);
        Assert.False(row.LeftACopiedDoor);
        Assert.False(row.ChangeOfCellReportsSent);
    }

    /// <summary>
    /// Porta il database allo schema precedente alla migrazione in prova e ci scrive una missione con
    /// SQL grezzo, come stava allora: il modello di oggi scriverebbe anche la colonna nuova.
    /// </summary>
    private void SeedAMissionWithoutADoorAtThePreviousSchema(Guid missionId)
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
            "StorageCellReserved, ChangeOfCellReportsSent) " +
            "VALUES ({0}, '43', '2026-10-08 10:00:00', NULL, 0, 2, 1, '0', '0', '0', 0, 150, '0', '0', 0, '0', 0, 0, 0, 0, 0);",
            missionId.ToString().ToUpperInvariant());
    }
}
