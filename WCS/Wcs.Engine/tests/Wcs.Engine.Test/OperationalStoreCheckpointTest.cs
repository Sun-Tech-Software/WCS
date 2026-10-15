using Microsoft.Data.Sqlite;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Xunit;

namespace Wcs.Engine.Test;

/// <summary>
/// Il gemello di <see cref="HistoryCheckpointTest"/> sul database operativo (voce D84): anche
/// <c>wcs.db</c> e' in WAL con le connessioni nel pool, e quello che si scrive vive nel <c>-wal</c>
/// finche' nessuno lo riversa. E' proprio il file della prova dal vivo del 2026-09-10 (37 MB nel
/// <c>-wal</c> a host appena chiuso). Lo store riversa da se' nel <c>Dispose</c>, l'unico momento
/// in cui smette di essere scritto: non ruota per mese.
/// </summary>
public sealed class OperationalStoreCheckpointTest : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wcs-store-checkpoint-" + Guid.NewGuid().ToString("N"));
    private WcsDatabase? _store;

    public void Dispose()
    {
        _store?.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void DisposingTheStoreDrainsItsWal()
    {
        _store = WcsDatabase.Open(_directory);
        using (var context = _store.CreateContext())
        {
            context.PlantItems.Add(new PlantItem { ItemId = Guid.NewGuid(), Name = "una riga" });
            context.SaveChanges();
        }

        var path = Path.Combine(_directory, WcsDatabase.OperationalFileName);

        // Prima di chiudere il contenuto sta nel -wal: e' la premessa, senza la quale il resto
        // del test non proverebbe niente.
        Assert.True(new FileInfo(path + "-wal").Length > 0, "il -wal doveva contenere la riga scritta");

        _store.Dispose();
        _store = null;

        var wal = path + "-wal";
        if (File.Exists(wal))
            Assert.Equal(0, new FileInfo(wal).Length);
    }
}
