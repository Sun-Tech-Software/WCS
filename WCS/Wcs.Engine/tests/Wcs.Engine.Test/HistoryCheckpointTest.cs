using Microsoft.Data.Sqlite;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Xunit;

namespace Wcs.Engine.Test;

/// <summary>
/// Voce D84: i file mensili dello storico sono in WAL, e quello che si scrive vive nel <c>-wal</c>
/// finche' nessuno lo riversa. Con le connessioni nel pool il riversamento alla chiusura
/// dell'ultima connessione non arriva mai, e chi copia il solo <c>.db</c> porta via uno stub.
/// Lo storico riversa da se' nei due momenti in cui un file smette di essere scritto: alla
/// rotazione del mese, sul file che diventa passato, e nel <c>Dispose</c>, sul mese corrente.
/// </summary>
public sealed class HistoryCheckpointTest : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wcs-history-checkpoint-" + Guid.NewGuid().ToString("N"));
    private DateTime _now = new(2026, 9, 10, 10, 0, 0);
    private WcsHistoryDatabase? _history;

    public void Dispose()
    {
        _history?.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static void WriteALine(WcsHistoryDatabase history, DateTime date, string description)
    {
        using var context = history.CreateContext();
        context.NavigatorLog.Add(new NavigatorLogEntry { Date = date, Description = description, Status = 1 });
        context.SaveChanges();
    }

    private static void AssertWalIsDrained(string databasePath)
    {
        var wal = databasePath + "-wal";
        if (!File.Exists(wal)) return;
        Assert.Equal(0, new FileInfo(wal).Length);
    }

    [Fact]
    public void DisposingTheHistoryDrainsTheWalOfTheCurrentMonth()
    {
        _history = WcsHistoryDatabase.Open(_directory, monthsToKeep: 3, clock: () => _now);
        WriteALine(_history, _now, "settembre");
        var september = WcsHistoryDatabase.FilePath(_directory, _now);

        // Prima di chiudere il contenuto sta nel -wal: e' la premessa, senza la quale il resto
        // del test non proverebbe niente.
        Assert.True(new FileInfo(september + "-wal").Length > 0, "il -wal doveva contenere la riga scritta");

        _history.Dispose();
        _history = null;

        AssertWalIsDrained(september);
    }

    [Fact]
    public void RotatingTheMonthDrainsTheWalOfTheMonthLeftBehind()
    {
        _history = WcsHistoryDatabase.Open(_directory, monthsToKeep: 3, clock: () => _now);
        WriteALine(_history, _now, "settembre");
        var september = WcsHistoryDatabase.FilePath(_directory, _now);
        Assert.True(new FileInfo(september + "-wal").Length > 0, "il -wal doveva contenere la riga scritta");

        _now = new DateTime(2026, 10, 5, 10, 0, 0);
        WriteALine(_history, _now, "ottobre");

        // Settembre non si scrive piu': il suo contenuto deve stare nel .db, non nel -wal.
        AssertWalIsDrained(september);
    }
}
