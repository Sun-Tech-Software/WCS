using Microsoft.EntityFrameworkCore;
using Wcs.Engine.Items;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;

namespace Wcs.Engine.Api;

/// <summary>I comandi da fuori: si scrivono come ToDo e il ciclo del motore li raccoglie (CommandToDo.Execute).</summary>
public static class CommandGateway
{
    /// <summary>Le tracce dell'operatore dal Client del WMS: arrivano gia' concluse (Done), il motore non le esegue, quindi non hanno bisogno che sia in Run.</summary>
    private static readonly HashSet<int> OperatorTraceTypes =
    [
        (int)Command.CommandTypes.ModifyRushMoverStatus,
        (int)Command.CommandTypes.ChangeCellLockStatus,
        (int)Command.CommandTypes.AddOrModifyLuPosition,
        (int)Command.CommandTypes.ModifyDatabaseCellContents
    ];

    /// <summary>I comandi sui dispositivi e sulle UDC (2001 in su) hanno senso solo con il motore in Run; quelli sul navigatore (1001-1009) sempre; le tracce dell'operatore (sopra 4001) non eseguono nulla, quindi non richiedono il motore in Run.</summary>
    public static bool RequiresRunningEngine(int commandType) => commandType >= 2000 && !OperatorTraceTypes.Contains(commandType);

    /// <summary>
    /// Gli unici stati che si scrivono da fuori: <c>0</c> (ToDo) per un comando da eseguire e
    /// <c>2</c> (Done) per una traccia gia' conclusa, come le registra il WMS. Gli stati
    /// intermedi — Running, Warning, Error — li scrive il motore mentre esegue.
    /// </summary>
    public static bool IsWritableStatus(short status) => status is 0 or 2;

    /// <summary>Accoda il comando e restituisce la chiave. <c>Date</c>, se il chiamante l'ha valorizzata, resta la sua.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Lo stato non e' uno di quelli che si scrivono da fuori (<see cref="IsWritableStatus"/>).
    /// </exception>
    public static async Task<long> EnqueueAsync(CommandRecord record)
    {
        if (!IsWritableStatus(record.Status))
            throw new ArgumentOutOfRangeException(nameof(record), record.Status,
                "Da fuori si scrive un comando da eseguire (0) o una traccia conclusa (2): gli stati intermedi li scrive il motore.");

        record.Id = 0;
        // La data e' il momento dell'operazione: una traccia del WMS la porta gia' (e se e' in UTC
        // la riporta a locale come le altre colonne del WCS); un comando che non la porta prende adesso.
        record.Date = record.Date == default ? DateTime.Now : StoreAccess.Local(record.Date);
        record.FromApi = true;
        await StoreAccess.WriteAsync(context => CommandStore.UpsertAsync(context, record));
        return record.Id;
    }

    public static Task<CommandRecord?> FindAsync(long id) =>
        StoreAccess.ReadAsync(c => c.Commands.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id));

    public static Task<List<CommandRecord>> ListAsync(CommandFilter filter)
    {
        var start = StoreAccess.Local(filter.From);
        var end = StoreAccess.Local(filter.To);
        return StoreAccess.ReadAsync(c =>
        {
            var query = c.Commands.AsNoTracking().AsQueryable();
            if (filter.Status != null) query = query.Where(x => x.Status == filter.Status);
            if (filter.WorkstationId != null) query = query.Where(x => x.WorkstationId == filter.WorkstationId);
            if (filter.UserId != null) query = query.Where(x => x.UserId == filter.UserId);
            if (filter.ExcludeUserId != null) query = query.Where(x => x.UserId != null && x.UserId != filter.ExcludeUserId);
            if (filter.ItemId != null) query = query.Where(x => x.ItemId == filter.ItemId);
            if (filter.ShowStatus != null) query = query.Where(x => x.ShowStatus == filter.ShowStatus);
            if (filter.MinType != null) query = query.Where(x => x.CommandType >= filter.MinType);
            if (filter.MaxType != null) query = query.Where(x => x.CommandType <= filter.MaxType);
            if (start != null) query = query.Where(x => x.Date >= start);
            if (end != null) query = query.Where(x => x.Date < end);
            return query.OrderByDescending(x => x.Id).Take(Math.Clamp(filter.Take, 1, 1000)).ToListAsync();
        });
    }

    /// <summary>L'esito di <see cref="UpdateAsync"/>: la rotta lo traduce in 204, 404 o 400.</summary>
    public enum UpdateResults
    {
        /// <summary>Le colonne sono state scritte.</summary>
        Updated,
        /// <summary>Nessun comando con quella chiave.</summary>
        NotFound,
        /// <summary>Lo stato chiesto non e' un valore di <see cref="Command.StatusTypes"/>.</summary>
        UnknownStatus,
        /// <summary>Il comando e' gia' partito e lo stato chiesto e' piu' basso: lo rimetterebbe in coda.</summary>
        StatusGoesBack
    }

    /// <summary>
    /// Aggiorna stato, errore, nota e stato di visualizzazione: le colonne che la vista dei comandi
    /// del Client toccava. Lo stato non torna mai indietro da <see cref="Command.StatusTypes.Running"/>
    /// in su: il motore raccoglie i pendenti con <c>Status &lt; 1</c>, quindi riportare a ToDo un
    /// comando gia' concluso lo farebbe rieseguire. Lo stesso stato passa — e' il caso di
    /// «segna come letto», che rispedisce la fotografia solo per scrivere <c>ShowStatus</c>.
    /// </summary>
    public static async Task<UpdateResults> UpdateAsync(long id, short status, string? errorDescription, string? info, short? showStatus)
    {
        if (status is < 0 or > byte.MaxValue || !Enum.IsDefined(typeof(Command.StatusTypes), (byte)status))
            return UpdateResults.UnknownStatus;

        var outcome = UpdateResults.NotFound;
        await StoreAccess.WriteAsync(async context =>
        {
            var existing = await context.Commands.SingleOrDefaultAsync(x => x.Id == id);
            if (existing == null) return;
            if (existing.Status >= (short)Command.StatusTypes.Running && status < existing.Status)
            {
                // «Segna come letto» rispedisce la fotografia della griglia: se nel frattempo il
                // motore ha portato avanti il comando, si scrive solo ShowStatus e lo stato resta
                // quello del motore. Il rifiuto vale per chi vuole davvero tornare indietro.
                if (showStatus != null)
                {
                    existing.ShowStatus = showStatus.Value;
                    context.Deferred.Add(() => Navigator.EventSink?.CommandChanged(existing));
                    outcome = UpdateResults.Updated;
                    return;
                }
                outcome = UpdateResults.StatusGoesBack;
                return;
            }
            outcome = UpdateResults.Updated;
            existing.Status = status;
            existing.ErrorDescription = errorDescription;
            if (info != null) existing.Info = info;
            if (showStatus != null) existing.ShowStatus = showStatus.Value;
            context.Deferred.Add(() => Navigator.EventSink?.CommandChanged(existing));
        });
        return outcome;
    }
}
