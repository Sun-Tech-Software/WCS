using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Wcs.Engine.Api;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// Lo storico interrogato su piu' mesi: due file su disco con un orologio iniettato, un
/// intervallo a cavallo, uno tutto nel passato, uno che parte prima del primo file → rifiutato.
/// </summary>
public sealed class HistoryMonthsTest : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wcs-history-months-" + Guid.NewGuid().ToString("N"));
    private DateTime _now = new(2026, 9, 10, 10, 0, 0);
    private readonly WcsHistoryDatabase _history;

    public HistoryMonthsTest()
    {
        _history = WcsHistoryDatabase.Open(_directory, monthsToKeep: 3, clock: () => _now);
        using (var september = _history.CreateContext())
        {
            september.HandledLoadUnits.Add(new HandledLoadUnit { MissionId = Guid.NewGuid(), Type = 1, Date = new DateTime(2026, 9, 10, 8, 0, 0), ProcessId = 91 });
            september.NavigatorLog.Add(new NavigatorLogEntry { Date = new DateTime(2026, 9, 10, 8, 0, 0), Description = "settembre", Status = 1 });
            september.SaveChanges();
        }
        _now = new DateTime(2026, 10, 5, 10, 0, 0);
        using (var october = _history.CreateContext())
        {
            october.HandledLoadUnits.Add(new HandledLoadUnit { MissionId = Guid.NewGuid(), Type = 2, Date = new DateTime(2026, 10, 5, 8, 0, 0) });
            october.NavigatorLog.Add(new NavigatorLogEntry { Date = new DateTime(2026, 10, 5, 8, 0, 0), Description = "ottobre", Status = 1 });
            october.SaveChanges();
        }
        Navigator.History = _history;
    }

    public void Dispose()
    {
        Navigator.History = null;
        _history.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void TheMonthsOnDiskAreListedFromTheOldest()
    {
        Assert.Equal([(2026, 9), (2026, 10)], _history.MonthsAvailable());
    }

    [Fact]
    public async Task AnIntervalAcrossTwoMonthsReadsBothFilesInDateOrder()
    {
        var rows = await HistoryQueries.HandledLoadUnitsAsync(new DateTime(2026, 9, 1), new DateTime(2026, 10, 31), null, null, null, 100);
        Assert.Equal([(short)2, (short)1], rows.Select(r => r.Type).ToList()); // dal piu' recente, come oggi

        var log = await HistoryQueries.LogAsync(new DateTime(2026, 9, 1), new DateTime(2026, 10, 31), null, null, 100);
        Assert.Equal(["ottobre", "settembre"], log.Select(l => l.Description).ToList());

        var summary = await HistoryQueries.HandledLoadUnitSummaryAsync(new DateTime(2026, 9, 1), new DateTime(2026, 10, 31), SummaryPeriod.Day);
        Assert.Equal(2, summary.Count);
        Assert.True(await HistoryQueries.HandledLoadUnitExistsAsync(processId: 91, loadUnitProcessId: null, noteHeaderId: null));
    }

    [Fact]
    public async Task AnIntervalEntirelyInThePastReadsOnlyThatFile()
    {
        var rows = await HistoryQueries.HandledLoadUnitsAsync(new DateTime(2026, 9, 1), new DateTime(2026, 9, 30), null, null, null, 100);
        Assert.Equal((short)1, Assert.Single(rows).Type);
    }

    [Fact]
    public async Task AnIntervalStartingBeforeTheOldestFileIsRefusedNamingTheFirstMonth()
    {
        var ex = await Assert.ThrowsAsync<HistoryRangeNotAvailableException>(() =>
            HistoryQueries.LogAsync(new DateTime(2026, 8, 20), new DateTime(2026, 10, 31), null, null, 100));
        Assert.Contains("2026-09", ex.Message);
    }

    [Fact]
    public async Task TakeAppliesToTheWholeInterval()
    {
        var rows = await HistoryQueries.HandledLoadUnitsAsync(new DateTime(2026, 9, 1), new DateTime(2026, 10, 31), null, null, null, 1);
        Assert.Equal((short)2, Assert.Single(rows).Type);
    }

    [Fact]
    public async Task AMonthLeftWithItsWalIsStillReadable()
    {
        // I file mensili sono in WAL, e finche' nessuno riversa il contenuto sta nel -wal: se il
        // processo si ferma sporco a cavallo del cambio di mese, il file del mese passato resta con
        // il proprio -wal pieno e nessuno lo tiene aperto. Questo test tiene fermo che quel mese si
        // legga lo stesso, con la connessione in sola lettura di CreateReadContext.
        //
        // Riprodurre lo stato: si scrive su settembre con l'autocheckpoint spento — la riga resta
        // solo nel -wal — e si mettono da parte il .db e il -wal *prima* di chiudere, perche' alla
        // chiusura dell'ultima connessione SQLite riversa e toglie il -wal. Rimessi al loro posto,
        // e tolto il -shm che dopo un arresto sporco non e' attendibile, la cartella e' nello stato
        // di dopo lo schianto. Tenere semplicemente viva una connessione nel pool *non* riprodurrebbe
        // il caso: con l'indice del WAL gia' in memoria condivisa la lettura non deve ricostruire
        // niente, e il test passerebbe senza provare niente.
        var september = WcsHistoryDatabase.FilePath(_directory, new DateTime(2026, 9, 1));
        var (savedDatabase, savedWal) = (september + ".salvato", september + "-wal.salvato");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = september,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString();
        using (var connection = new SqliteConnection(connectionString))
        {
            connection.Open();
            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA wal_autocheckpoint = 0;";
                pragma.ExecuteNonQuery();
            }
            using (var context = new WcsHistoryDbContext(
                new DbContextOptionsBuilder<WcsHistoryDbContext>().UseSqlite(connection).Options))
            {
                context.NavigatorLog.Add(new NavigatorLogEntry { Date = new DateTime(2026, 9, 20, 8, 0, 0), Description = "settembre sporco", Status = 1 });
                context.SaveChanges();
            }
            File.Copy(september, savedDatabase, overwrite: true);
            File.Copy(september + "-wal", savedWal, overwrite: true);
        }

        SqliteConnection.ClearAllPools(); // chiude anche le connessioni che il contesto del mese aveva lasciato nel pool
        File.Copy(savedDatabase, september, overwrite: true);
        File.Copy(savedWal, september + "-wal", overwrite: true);
        if (File.Exists(september + "-shm")) File.Delete(september + "-shm");
        Assert.True(new FileInfo(september + "-wal").Length > 0, "il -wal di settembre doveva restare pieno");

        // La riga sta davvero solo nel -wal: il .db da solo, in una cartella dove il -wal non c'e',
        // non la contiene — oggi non ha nemmeno la tabella, perche' finche' nessuno riversa il file
        // del mese resta la pagina vuota della creazione. Senza questo controllo, rileggere la riga
        // piu' avanti non proverebbe che il riversamento e' avvenuto.
        var aside = Path.Combine(_directory, "senza-wal");
        Directory.CreateDirectory(aside);
        var asidePath = Path.Combine(aside, Path.GetFileName(september));
        File.Copy(savedDatabase, asidePath, overwrite: true);
        using (var check = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = asidePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
        {
            check.Open();
            using var command = check.CreateCommand();
            command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'NavigatorLog';";
            if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
            {
                command.CommandText = "SELECT count(*) FROM NavigatorLog WHERE Description = 'settembre sporco';";
                Assert.Equal(0L, Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture));
            }
        }

        // Il contesto di settembre e' quello in sola lettura di CreateReadContext (settembre non e'
        // piu' il mese di adesso), e legge lo stesso: SQLITE_OPEN_READONLY vale per il file del
        // database, non per il -shm, che SQLite crea e scrive per ricostruire l'indice del WAL.
        var log = await HistoryQueries.LogAsync(new DateTime(2026, 9, 1), new DateTime(2026, 9, 30), null, null, 100);
        Assert.Contains("settembre sporco", log.Select(l => l.Description));
        Assert.True(File.Exists(september + "-shm"), "la lettura doveva ricostruire il -shm del mese passato");
    }

    [Fact]
    public void OpeningAPastMonthWritesInItsFileAndLeavesTheNewerMonthsInPlace()
    {
        // E' il modo in cui l'importatore interno degli impianti (non pubblicato) scrive lo storico di un impianto migrato: un'apertura per
        // il mese di ogni riga, con l'orologio fermo sul primo giorno di quel mese e la ritenzione
        // disattivata. Due cose devono valere insieme, e sono quelle che l'importazione rischia:
        // che il file del mese passato nasca gia' migrato (altrimenti la scrittura cade), e che
        // l'apertura non porti via i mesi piu' nuovi — ApplyRetention cancella solo cio' che e'
        // piu' vecchio dell'orologio meno la ritenzione, ma qui l'orologio e' indietro di anni.
        var august2019 = new DateTime(2019, 8, 1);
        var mission = Guid.NewGuid();

        using (var past = WcsHistoryDatabase.Open(_directory, monthsToKeep: 1200, clock: () => august2019))
        {
            using var context = past.CreateContext();
            context.HandledLoadUnits.Add(new HandledLoadUnit
            {
                MissionId = mission, Type = 3, Date = new DateTime(2019, 8, 21, 18, 17, 52)
            });
            context.SaveChanges();
        }

        Assert.Equal(
            ["wcs-history-2019-08.db", "wcs-history-2026-09.db", "wcs-history-2026-10.db"],
            Directory.GetFiles(_directory, "wcs-history-*.db").Select(f => Path.GetFileName(f)).Order().ToArray());

        // La riga sta nel file del 2019, non in quello del mese di adesso.
        using var august = _history.CreateReadContext(2019, 8);
        Assert.Equal(mission, Assert.Single(august.HandledLoadUnits.AsNoTracking().ToList()).MissionId);
        using var october = _history.CreateReadContext(2026, 10);
        Assert.Equal((short)2, Assert.Single(october.HandledLoadUnits.AsNoTracking().ToList()).Type);
    }

    [Fact]
    public void RetentionSaysWhichFileItDeleted()
    {
        // Cancellare un file mensile e' un dato che se ne va: deve lasciare un segno. Il gancio e'
        // statico (l'host ci collega il proprio registro all'avvio, come per Navigator.HostErrorLog),
        // quindi si rimette com'era nel finally: un altro test della stessa corsa lo troverebbe
        // agganciato alla lista di questo.
        var previous = WcsHistoryDatabase.Notice;
        var notices = new List<string>();
        var directory = Path.Combine(Path.GetTempPath(), "wcs-history-retention-" + Guid.NewGuid().ToString("N"));
        var clock = new DateTime(2026, 1, 10, 10, 0, 0);

        try
        {
            WcsHistoryDatabase.Notice = notices.Add;
            using (var history = WcsHistoryDatabase.Open(directory, monthsToKeep: 2, clock: () => clock))
            {
                using (var january = history.CreateContext()) january.SaveChanges();

                // Aprile: gennaio e' oltre la ritenzione di due mesi, e il primo contesto del mese
                // nuovo e' quello che la applica.
                notices.Clear();
                clock = new DateTime(2026, 4, 5, 10, 0, 0);
                using (var april = history.CreateContext()) april.SaveChanges();
            }

            Assert.False(File.Exists(Path.Combine(directory, "wcs-history-2026-01.db")),
                "gennaio doveva essere cancellato dalla ritenzione");
            Assert.Contains(notices, n => n.Contains("cancellato") && n.Contains("wcs-history-2026-01.db"));
            Assert.Contains(notices, n => n.Contains("1 file mensili cancellati"));
        }
        finally
        {
            WcsHistoryDatabase.Notice = previous;
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TakeAppliedPerMonthStillGivesTheGlobalTopRows()
    {
        // Con take: 2 servono entrambe le righe, una per file: se il taglio applicato dentro la
        // query di ogni mese tagliasse via qualcosa che serve, qui mancherebbe la riga di settembre.
        var rows = await HistoryQueries.HandledLoadUnitsAsync(new DateTime(2026, 9, 1), new DateTime(2026, 10, 31), null, null, null, 2);
        Assert.Equal([(short)2, (short)1], rows.Select(r => r.Type).ToList());
    }
}
