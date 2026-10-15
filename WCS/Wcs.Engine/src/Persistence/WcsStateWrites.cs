using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Wcs.Engine.Persistence;

/// <summary>
/// Come una scrittura di stato arriva al database del WCS: dallo scrittore di stato se c'e',
/// altrimenti direttamente sullo store; senza store non c'e' nulla su cui scrivere.
/// </summary>
/// <remarks>
/// E' la stessa regola che ogni ORM del motore applicava per conto suo; qui sta una volta sola.
/// <see cref="Write"/> torna subito; <see cref="WriteAndWaitAsync"/> aspetta che la scrittura sia
/// eseguita, per chi deve rileggere subito o vuole indietro una chiave.
/// </remarks>
internal static class WcsStateWrites
{
    public static void Write(WcsWrite write)
    {
        if (Navigator.StateWriter is { } writer && writer.EnqueueOrWait(write))
            return;

        var store = Navigator.Store;
        if (store == null)
            return;

        using var context = store.CreateContext();
        write(context).GetAwaiter().GetResult();
        context.SaveChanges();
    }

    public static async Task WriteAndWaitAsync(WcsWrite write)
    {
        if (Navigator.StateWriter is { } writer)
        {
            await writer.EnqueueAndWaitAsync(write);
            return;
        }

        var store = Navigator.Store;
        if (store == null) return;

        await using var context = store.CreateContext();
        await write(context);
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Esegue una scrittura di stato da sola nel suo lotto, mantenendo l'ordine della coda.
    /// </summary>
    public static async Task WriteExclusiveAndWaitAsync(WcsWrite write)
    {
        if (Navigator.StateWriter is { } writer)
        {
            await writer.EnqueueExclusiveAndWaitAsync(write);
            return;
        }

        var store = Navigator.Store;
        if (store == null) return;

        await using var context = store.CreateContext();
        // Reserve the SQLite writer before the delegate's checks. A deferred transaction
        // would let another connection insert pending work between those reads and SaveChanges.
        await context.Database.OpenConnectionAsync();
        await using var transaction = ((SqliteConnection)context.Database.GetDbConnection())
            .BeginTransaction(deferred: false);
        await context.Database.UseTransactionAsync(transaction);
        await write(context);
        await context.SaveChangesAsync();
        await transaction.CommitAsync();
    }
}
