using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;

namespace Wcs.Engine.ORM;

/// <summary>
/// La coda di estrazione sul database del WCS, per chi la scrive e la legge dal motore.
/// </summary>
/// <remarks>
/// Le scritture passano dallo scrittore di stato e vengono <b>attese</b>: chi accoda un ostacolo
/// controlla subito dopo se il successivo e' gia' in coda, e deve trovare la riga. Le letture
/// aprono un contesto sullo store. Senza scrittore si scrive direttamente sullo store; senza
/// store non c'e' nulla su cui lavorare e le letture rispondono vuoto.
/// </remarks>
internal static class ExtractionQueue
{
    /// <summary>Inserisce o aggiorna una riga e aspetta che sia scritta.</summary>
    public static Task UpsertAsync(ExtractionQueueItem item) =>
        WriteAsync(context => ExtractionQueueStore.UpsertAsync(context, item));

    public static async Task WriteAsync(WcsWrite write)
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

    public static async Task<T> ReadAsync<T>(Func<WcsDbContext, Task<T>> read, T whenNoStore)
    {
        var store = Navigator.Store;
        if (store == null) return whenNoStore;

        await using var context = store.CreateContext();
        return await read(context);
    }
}
