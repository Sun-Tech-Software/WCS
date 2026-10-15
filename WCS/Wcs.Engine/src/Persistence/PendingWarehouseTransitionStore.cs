using Microsoft.EntityFrameworkCore;
using Wcs.Engine.Persistence.Entities;

namespace Wcs.Engine.Persistence;

/// <summary>Operazioni sullo stesso contesto per l'outbox delle transizioni fisiche.</summary>
internal static class PendingWarehouseTransitionStore
{
    public static async Task<PendingWarehouseTransition> EnqueueAsync(
        WcsDbContext context,
        Guid operationId,
        WarehouseTransitionKind kind,
        Guid missionId,
        decimal loadUnitId,
        string payloadJson,
        DateTimeOffset createdAtUtc)
    {
        var existing = context.PendingWarehouseTransitions.Local.FirstOrDefault(
                           row => row.MissionId == missionId && row.Kind == kind)
                       ?? await context.PendingWarehouseTransitions.FirstOrDefaultAsync(
                           row => row.MissionId == missionId && row.Kind == kind);
        if (existing != null) return existing;

        var transition = new PendingWarehouseTransition
        {
            OperationId = operationId,
            Kind = kind,
            MissionId = missionId,
            LoadUnitId = loadUnitId,
            PayloadJson = payloadJson,
            CreatedAtUtc = createdAtUtc,
            NextAttemptAtUtc = createdAtUtc
        };
        context.PendingWarehouseTransitions.Add(transition);
        return transition;
    }

    /// <summary>
    /// Il tempo di un tentativo che non arriverà mai: una transizione che il gestionale ha rifiutato
    /// per sempre (un 4xx, un payload illeggibile) resta nell'outbox, con il suo motivo in
    /// <see cref="PendingWarehouseTransition.LastError"/>, ma non torna più dovuta. Resta lì perché
    /// qualcuno la veda e decida, e perché continua a trattenere cio' che dipende da lei - si veda
    /// <see cref="OldestDueAsync"/>.
    /// </summary>
    public static readonly DateTimeOffset ParkedUntil = DateTimeOffset.MaxValue;

    /// <summary>
    /// Le transizioni da consegnare adesso, in ordine di arrivo, saltando soltanto quelle che
    /// dipendono da una rimasta indietro.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Fino al 24/9/2026 si prendeva il prefisso globale</b> e ci si fermava alla prima riga non
    /// dovuta. Era corretto per l'ordine - una riga nuova non scavalcava mai una vecchia in backoff -
    /// ma una sola transizione rifiutata per sempre fermava ogni allocazione e ogni rilascio
    /// dell'impianto, mentre il motore continuava a muovere carichi.
    /// </para>
    /// <para>
    /// <b>L'ordine serve solo fra mutazioni che si toccano</b>, e sono due casi. Stessa UDC: un rilascio
    /// non può precedere l'allocazione della stessa unità. E un rilascio contro un'allocazione:
    /// <see cref="Wcs.Wms.Contracts.ReleaseLoadUnitRequest"/> porta soltanto l'UDC, non la cella che libera, quindi
    /// un'allocazione successiva potrebbe andare proprio in quella cella - e il gestionale la
    /// troverebbe ancora occupata. Non sapendo quale cella, un rilascio rimasto indietro trattiene
    /// <b>tutte</b> le allocazioni dopo di lui. E' piu' stretto del necessario, ed e' la parte sicura
    /// del dubbio.
    /// </para>
    /// <para>
    /// <b>Chi resta indietro trattiene a sua volta.</b> Una riga saltata perche' dipende da una
    /// rimasta indietro e' essa stessa in sospeso: se e' un rilascio, trattiene le allocazioni dopo
    /// di lei come se fosse lei a non essere dovuta. E' cio' che copre, per transitivita', due
    /// allocazioni nella stessa cella: la seconda puo' avvenire solo dopo il rilascio della prima,
    /// che e' della stessa UDC della prima e quindi in sospeso con lei.
    /// </para>
    /// <para>
    /// Si legge tutta la coda, ma di ogni riga solo le quattro colonne che decidono; le righe intere
    /// si caricano per le sole scelte. La data si confronta in memoria, come prima: SQLite non sa
    /// confrontare un <see cref="DateTimeOffset"/>.
    /// </para>
    /// </remarks>
    public static async Task<List<PendingWarehouseTransition>> OldestDueAsync(
        WcsDbContext context,
        DateTimeOffset nowUtc,
        int take)
    {
        var queue = await context.PendingWarehouseTransitions
            .AsNoTracking()
            .OrderBy(row => row.Id)
            .Select(row => new { row.Id, row.LoadUnitId, row.Kind, row.NextAttemptAtUtc })
            .ToListAsync();

        var heldLoadUnits = new HashSet<decimal>();
        var aReleaseIsHeld = false;
        var chosen = new List<long>();
        foreach (var row in queue)
        {
            var held = row.NextAttemptAtUtc > nowUtc ||
                       heldLoadUnits.Contains(row.LoadUnitId) ||
                       (aReleaseIsHeld && row.Kind == WarehouseTransitionKind.Allocate);
            if (held)
            {
                heldLoadUnits.Add(row.LoadUnitId);
                if (row.Kind == WarehouseTransitionKind.Release) aReleaseIsHeld = true;
                continue;
            }

            chosen.Add(row.Id);
            if (chosen.Count == take) break;
        }

        if (chosen.Count == 0) return [];
        return await context.PendingWarehouseTransitions
            .AsNoTracking()
            .Where(row => chosen.Contains(row.Id))
            .OrderBy(row => row.Id)
            .ToListAsync();
    }

    public static async Task RecordFailureAsync(
        WcsDbContext context,
        long id,
        string error,
        DateTimeOffset nextAttemptAtUtc)
    {
        var transition = await context.PendingWarehouseTransitions.SingleAsync(row => row.Id == id);
        transition.AttemptCount++;
        transition.LastError = error;
        transition.NextAttemptAtUtc = nextAttemptAtUtc;
    }

    public static async Task DeleteAsync(WcsDbContext context, long id)
    {
        var transition = await context.PendingWarehouseTransitions.SingleOrDefaultAsync(row => row.Id == id);
        if (transition != null) context.PendingWarehouseTransitions.Remove(transition);
    }

    public static Task<bool> HasPendingAsync(
        WcsDbContext context,
        Guid missionId,
        WarehouseTransitionKind kind) =>
        context.PendingWarehouseTransitions.AsNoTracking()
            .AnyAsync(row => row.MissionId == missionId && row.Kind == kind);
}
