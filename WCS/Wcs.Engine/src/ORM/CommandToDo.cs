using System.Collections.Concurrent;
using Wcs.Engine.Items;
using Wcs.Engine.Persistence;
using static Wcs.Engine.Items.Command;

namespace Wcs.Engine.ORM;

/// <summary>
/// I comandi in ingresso sul database del WCS: la riga di <c>Commands</c>, attraverso
/// <see cref="CommandStore"/>.
/// </summary>
/// <remarks>
/// Prima erano T-SQL su <c>COMMAND_TODO</c>. La lettura dei comandi da eseguire apre un contesto
/// sullo store. Le scritture esplicite vengono attese, perche' chi crea un comando vuole indietro
/// la chiave e chi ne cambia lo stato non vuole che il ciclo successivo lo rilegga; i setter di un
/// comando gia' persistito accodano invece l'istantanea prima di tornare. Senza store non c'e'
/// nulla da leggere ne' da scrivere.
/// </remarks>
internal static class CommandToDo
{
    #region Private Fields

    private static bool _loadingFromDatabase;

    #endregion Private Fields

    #region Internal Methods

    /// <summary>
    /// Mette in coda i comandi ancora da eseguire, nell'ordine di arrivo.
    /// </summary>
    internal static async Task LoadFromDatabase(ConcurrentQueue<Command> values)
    {
        if (_loadingFromDatabase)
            return;

        _loadingFromDatabase = true;

        try
        {
            var store = Navigator.Store;
            if (store == null) return;

            List<Persistence.Entities.CommandRecord> rows;
            await using (var context = store.CreateContext())
                rows = await CommandStore.PendingAsync(context, StatusTypes.Running);

            foreach (var row in rows)
                values.Enqueue(CommandStore.ToCommand(row));
        }
        catch (Exception ex)
        {
            Navigator.WriteLog($"Error On CommandToDo->LoadFromDatabase Description: {ex.Message}\r\nStackTrace: {ex.StackTrace}",
                Log.Interface.LogEntryType.Error);
        }
        finally
        {
            _loadingFromDatabase = false;
        }
    }

    internal static Task SetStatus(decimal idCommandToDo, StatusTypes status, string errorDescription = "")
    {
        if (idCommandToDo == 0)
            return Task.CompletedTask;

        return WriteAsync(context => CommandStore.SetStatusAsync(context, (long)idCommandToDo, status, errorDescription));
    }

    /// <summary>
    /// Accoda l'istantanea di un comando gia' persistito prima di tornare al setter. In questo
    /// modo due cambi consecutivi mantengono il loro ordine e una barriera dello scrittore puo'
    /// attenderli senza polling. Senza scrittore conserva il comportamento non bloccante storico.
    /// </summary>
    internal static void QueueUpdate(Command value)
    {
        var snapshot = CommandStore.Snapshot(value);
        if (Navigator.StateWriter is { } writer)
        {
            if (!writer.EnqueueOrWait(context => CommandStore.UpsertAsync(context, snapshot)))
                Navigator.WriteLog("Command update rejected because the state writer is stopped.",
                    Log.Interface.LogEntryType.Warning);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await WriteAsync(context => CommandStore.UpsertAsync(context, snapshot));
            }
            catch (Exception ex)
            {
                Navigator.WriteLog($"Error updating command: {ex.Message}", Log.Interface.LogEntryType.Warning);
            }
        });
    }

    /// <summary>
    /// Scrive il comando: inserito se e' nuovo, aggiornato se ha gia' una chiave. Al ritorno il
    /// comando porta la chiave assegnata dal database, come faceva <c>SCOPE_IDENTITY</c>.
    /// </summary>
    internal static async Task Update(Command value)
    {
        var snapshot = CommandStore.Snapshot(value);
        await WriteAsync(context => CommandStore.UpsertAsync(context, snapshot));
        if (snapshot.Id > 0) value.IdCommandToDo = snapshot.Id;
    }

    #endregion Internal Methods

    #region Private Methods

    private static async Task WriteAsync(WcsWrite write)
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

    #endregion Private Methods
}
