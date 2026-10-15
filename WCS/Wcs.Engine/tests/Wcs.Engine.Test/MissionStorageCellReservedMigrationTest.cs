using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Engine.Persistence;
using Xunit;

namespace Wcs.Engine.Test;

/// <summary>
/// La migrazione del segno «la cella e' una prenotazione sua» (<c>PathMission.StorageCellReserved</c>,
/// voce 39, compito 3), vista dal punto di vista che conta: un database con missioni in viaggio
/// scritte prima del segno.
/// </summary>
/// <remarks>
/// <para>
/// Le righe di prima valgono «non sua». Di una riga vecchia non si sa se la porta fosse una
/// prenotazione della missione: l'ostacolo spostato, lo spostamento manuale, l'UDC rimessa nella sua
/// cella e le missioni di gruppo che copiano la porta di un'altra hanno una porta e nessuna
/// prenotazione loro, e con «sua» il loro deposito chiederebbe al gestionale, che risponde «non
/// c'e'», e la cella si rifarebbe altrove. Con «non sua» depositano come prima dell'aggiornamento.
/// </para>
/// <para>
/// Le missioni dell'operatore con una porta valgono «sua»: prima del segno ogni cambio dell'operatore
/// verso un magazzino prenotava la cella per la missione, e non c'era altra via per dargliene una.
/// Con il segno giu', su un membro di un gruppo sarebbero copie (<c>MissionGateway.IsACopy</c>), e il
/// loro deposito non chiederebbe piu' la cella (revisione del quarto giro del compito 17).
/// </para>
/// </remarks>
public sealed class MissionStorageCellReservedMigrationTest : IDisposable
{
    /// <summary>L'ultima migrazione <b>prima</b> di quella in prova: lo schema di ieri, senza la colonna.</summary>
    private const string BeforeTheMark = "20261001105617_AddMissionStorageCellLost";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"wcs-reserved-{Guid.NewGuid():N}");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    /// <summary>
    /// Una missione verso la porta A8, scritta con lo schema di prima: dopo la migrazione la riga c'e'
    /// ancora, con la sua porta, e il segno della cella sua e' giu'.
    /// </summary>
    [Fact]
    public async Task AMissionWrittenBeforeTheMarkComesBackWithoutIt()
    {
        var missionId = Guid.NewGuid();
        SeedAMissionAtThePreviousSchema(missionId, "A8");

        using var database = WcsDatabase.Open(_directory);
        await using var context = database.CreateContext();

        var row = Assert.Single(await context.Missions.AsNoTracking().ToListAsync());
        Assert.Equal(missionId, row.MissionId);
        Assert.Equal("A8", row.StorageDoorCode);
        Assert.False(row.StorageCellReserved);
    }

    /// <summary>
    /// Le righe dell'operatore scritte prima del segno: con una porta, la cella era una prenotazione sua,
    /// e il segno torna su; senza porta (la cella persa, che la porta l'ha lasciata) non c'e' niente di
    /// suo, e il segno resta giu', come per una missione della configurazione con la porta.
    /// </summary>
    [Theory]
    [InlineData(true, "NEW-A", true)]
    [InlineData(true, null, false)]
    [InlineData(true, "", false)]
    [InlineData(false, "NEW-A", false)]
    public async Task AnOperatorsMissionWithADoorWrittenBeforeTheMarkComesBackWithIt(bool operatorDestination,
        string? storageDoorCode, bool reserved)
    {
        var missionId = Guid.NewGuid();
        SeedAMissionAtThePreviousSchema(missionId, storageDoorCode, operatorDestination);

        using var database = WcsDatabase.Open(_directory);
        await using var context = database.CreateContext();

        var row = Assert.Single(await context.Missions.AsNoTracking().ToListAsync());
        Assert.Equal(missionId, row.MissionId);
        Assert.Equal(operatorDestination, row.OperatorDestination);
        Assert.Equal(storageDoorCode, row.StorageDoorCode);
        Assert.Equal(reserved, row.StorageCellReserved);
    }

    /// <summary>
    /// Porta il database allo schema precedente alla migrazione in prova e ci scrive una missione con
    /// SQL grezzo, come stava allora: il modello di oggi scriverebbe anche la colonna nuova.
    /// </summary>
    private void SeedAMissionAtThePreviousSchema(Guid missionId, string? storageDoorCode, bool operatorDestination = false)
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
            "NoteProcessingId, ActiveFilter, Tare, ReallocatedDestination, OperatorDestination, StorageCellLost) " +
            "VALUES ({0}, '42', '2026-10-02 10:00:00', {1}, 0, 0, 0, '0', '0', '0', 0, 150, '0', '0', 0, '0', 0, {2}, 0);",
            missionId.ToString().ToUpperInvariant(), storageDoorCode, operatorDestination ? 1 : 0);
    }
}
