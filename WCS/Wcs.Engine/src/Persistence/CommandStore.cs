using Microsoft.EntityFrameworkCore;
using Wcs.Engine.Api;
using Wcs.Engine.Items;
using Wcs.Engine.Persistence.Entities;

namespace Wcs.Engine.Persistence;

/// <summary>
/// L'unico punto in cui la riga <see cref="CommandRecord"/> e l'oggetto <see cref="Command"/> si
/// incontrano: le mappe nei due versi e le operazioni sul database.
/// </summary>
internal static class CommandStore
{
    /// <summary>
    /// Un'istantanea del comando, presa ora. Se il comando ha gia' una chiave, l'istantanea la
    /// porta e la riga viene aggiornata; altrimenti la riga viene inserita e la chiave assegnata
    /// dal database resta sull'istantanea, da dove il chiamante la copia sul comando.
    /// </summary>
    public static CommandRecord Snapshot(Command command) => new()
    {
        Id = (long)command.IdCommandToDo,
        Date = command.Date,
        CommandType = (int)command.CommandType,
        ItemId = NullIfEmpty(command.ItemCommandId),
        LoadUnitId = command.LuId == 0 ? null : command.LuId,
        StorageDoorCode = command.StorageDoorCode,
        Status = (short)command.Status,
        ErrorDescription = command.ErrorDescription,
        Info = command.Info,
        WorkstationId = command.WorkstationId,
        ProcessId = command.ProcessId == 0 ? null : command.ProcessId,
        BufferCellId = command.BufferCellId,
        LoadUnitTypeId = command.LuTypeId,
        PickUpDoorCode = command.PickUpDoorCode,
        DestinationItemId = NullIfEmpty(command.ItemDestinationId),
        ReferenceId = command.ReferenceId
    };

    /// <summary>
    /// Ricostruisce il comando da una riga, in materializzazione: le assegnazioni non rispediscono
    /// al database quello che il database ha appena dato. Chi lo riceve e' gia' un comando vivo.
    /// </summary>
    public static Command ToCommand(CommandRecord row)
    {
        var command = new Command(loadingFromDatabase: true)
        {
            IdCommandToDo = row.Id,
            Date = row.Date,
            CommandType = (Command.CommandTypes)row.CommandType,
            ItemCommandId = row.ItemId ?? Guid.Empty,
            LuId = row.LoadUnitId ?? 0,
            StorageDoorCode = row.StorageDoorCode,
            Status = (Command.StatusTypes)row.Status,
            Info = row.Info,
            WorkstationId = row.WorkstationId,
            BufferCellId = row.BufferCellId,
            ProcessId = row.ProcessId ?? 0,
            LuTypeId = row.LoadUnitTypeId,
            PickUpDoorCode = row.PickUpDoorCode,
            ItemDestinationId = row.DestinationItemId ?? Guid.Empty,
            ReferenceId = row.ReferenceId ?? string.Empty
        };

        command.EndLoadFromDatabase();
        return command;
    }

    /// <summary>I comandi con stato sotto la soglia, nell'ordine di arrivo: quelli ancora da eseguire.</summary>
    public static Task<List<CommandRecord>> PendingAsync(WcsDbContext context, Command.StatusTypes below)
    {
        var threshold = (short)below;
        return context.Commands.AsNoTracking()
            .Where(c => c.Status < threshold)
            .OrderBy(c => c.Id)
            .ToListAsync();
    }

    /// <remarks>
    /// L'evento di un inserimento parte dopo il salvataggio (<see cref="DeferredEvents"/>): prima
    /// la riga non ha ancora la chiave che il database le assegna, e un evento con Id 0 non si
    /// riconcilia con nulla. L'evento di un aggiornamento parte subito: la chiave c'e' gia'.
    /// </remarks>
    public static async Task UpsertAsync(WcsDbContext context, CommandRecord snapshot)
    {
        if (snapshot.Id == 0)
        {
            context.Commands.Add(snapshot);
            context.Deferred.Add(() => Navigator.EventSink?.CommandChanged(snapshot));
            return;
        }

        var existing = await context.Commands.SingleOrDefaultAsync(c => c.Id == snapshot.Id);
        if (existing == null)
        {
            // Una chiave che il database non conosce: si inserisce con quella, come faceva la
            // vecchia INSERT dopo l'IF EXISTS fallito.
            context.Commands.Add(snapshot);
            context.Deferred.Add(() => Navigator.EventSink?.CommandChanged(snapshot));
            return;
        }

        // Le colonne che il motore non governa restano com'erano: chi ha scritto il comando da
        // fuori (API, utente, stato di visualizzazione) non deve vederle azzerate.
        snapshot.AssignmentGuid = existing.AssignmentGuid;
        snapshot.ShowStatus = existing.ShowStatus;
        snapshot.UserId = existing.UserId;
        snapshot.FromApi = existing.FromApi;
        snapshot.ApiStatus = existing.ApiStatus;
        context.Entry(existing).CurrentValues.SetValues(snapshot);
        Navigator.EventSink?.CommandChanged(existing);
    }

    /// <summary>Stato ed eventuale errore di un comando, e lo stato di visualizzazione azzerato, come faceva <c>SetStatus</c>.</summary>
    public static async Task SetStatusAsync(WcsDbContext context, long id, Command.StatusTypes status, string? errorDescription)
    {
        var existing = await context.Commands.SingleOrDefaultAsync(c => c.Id == id);
        if (existing == null) return;

        existing.Status = (short)status;
        existing.ShowStatus = 0;
        if (!string.IsNullOrEmpty(errorDescription)) existing.ErrorDescription = errorDescription;
        Navigator.EventSink?.CommandChanged(existing);
    }

    private static Guid? NullIfEmpty(Guid value) => value == Guid.Empty ? null : value;
}
