using Microsoft.EntityFrameworkCore;
using Wcs.Engine.Persistence.Entities;

namespace Wcs.Engine.Persistence;

/// <summary>
/// Le operazioni sulla mappa porte-celle-dispositivi nel database del WCS.
/// </summary>
/// <remarks>
/// Le letture rispondono a cinque domande del motore — quale cella sta dietro una porta, quale porta
/// ha un dispositivo, quale dispositivo serve una cella o una porta, se una cella e' bloccata al
/// deposito, quali celle serve un ricollocatore — e le scritture toccano solo i due stati. Il
/// collegamento in se' lo scrive l'Editor con <see cref="ReplaceForItemAsync"/>: tutte le righe di
/// un dispositivo insieme, come faceva l'adattatore del suo dataset.
/// </remarks>
internal static class DoorMappingStore
{
    public static Task<decimal> CellIdByDoorAsync(WcsDbContext context, string doorCode) =>
        context.DoorMappings.AsNoTracking().Where(m => m.DoorCode == doorCode).Select(m => m.CellId).FirstOrDefaultAsync();

    public static Task<string?> DoorCodeByItemAsync(WcsDbContext context, Guid itemId) =>
        context.DoorMappings.AsNoTracking().Where(m => m.ItemId == itemId).OrderBy(m => m.Id).Select(m => m.DoorCode).FirstOrDefaultAsync();

    /// <summary>Il dispositivo della prima porta della cella, <c>null</c> se la cella non e' mappata: una riga con l'item vuoto da' <see cref="Guid.Empty"/>, e i due casi restano distinti.</summary>
    public static Task<Guid?> ItemIdByCellAsync(WcsDbContext context, decimal cellId) =>
        context.DoorMappings.AsNoTracking().Where(m => m.CellId == cellId).OrderBy(m => m.Id).Select(m => (Guid?)m.ItemId).FirstOrDefaultAsync();

    public static Task<Guid> ItemIdByDoorAsync(WcsDbContext context, string doorCode) =>
        context.DoorMappings.AsNoTracking().Where(m => m.DoorCode == doorCode).OrderBy(m => m.Id).Select(m => m.ItemId).FirstOrDefaultAsync();

    /// <summary>
    /// The side of a door (<c>A</c> or <c>B</c>, <see cref="DoorMapping.Side"/>), <c>null</c> for a door
    /// that is not mapped. Item 54: a precise cell is chosen by its door A code (owner's answer 7 of 13/10).
    /// </summary>
    public static Task<string?> SideByDoorAsync(WcsDbContext context, string doorCode) =>
        context.DoorMappings.AsNoTracking().Where(m => m.DoorCode == doorCode).OrderBy(m => m.Id).Select(m => (string?)m.Side).FirstOrDefaultAsync();

    public static Task<List<decimal>> CellsWithActiveOperationAsync(WcsDbContext context) =>
        context.DoorMappings.AsNoTracking().Where(m => m.OperationActive != 0).Select(m => m.CellId).Distinct().ToListAsync();

    public static Task<List<decimal>> MappedCellIdsAsync(WcsDbContext context) =>
        context.DoorMappings.AsNoTracking().Select(m => m.CellId).Distinct().ToListAsync();

    public static Task<List<DoorMapping>> ByItemAsync(WcsDbContext context, Guid itemId) =>
        context.DoorMappings.AsNoTracking().Where(m => m.ItemId == itemId).OrderBy(m => m.Id).ToListAsync();

    public static async Task SetStoringLockedAsync(WcsDbContext context, string doorCode, bool locked)
    {
        var rows = await context.DoorMappings.Where(m => m.DoorCode == doorCode).ToListAsync();
        foreach (var row in rows) row.StoringLocked = locked;
    }

    /// <summary>
    /// Blocca tutte le righe di una porta soltanto se la porta esiste ed e' interamente libera.
    /// Lettura e modifica avvengono sullo stesso contesto dello state writer: due richieste
    /// concorrenti vengono quindi serializzate e una sola puo' diventare proprietaria.
    /// </summary>
    public static async Task<bool> TrySetStoringLockedAsync(WcsDbContext context, string doorCode)
    {
        var rows = await context.DoorMappings.Where(mapping => mapping.DoorCode == doorCode).ToListAsync();
        if (rows.Count == 0 || rows.Any(mapping => mapping.StoringLocked)) return false;

        foreach (var row in rows) row.StoringLocked = true;
        return true;
    }

    /// <summary>
    /// La cella e' bloccata al deposito? Vero se almeno una delle sue porte lo e': e' la domanda
    /// del controllo di allocazione («posso depositare qui adesso?»), prudente. La ricerca celle
    /// (<c>CellSearchQueries</c>) risponde a un'altra domanda e considera la cella bloccata solo se
    /// tutte le sue porte lo sono: le due regole non vanno unificate.
    /// </summary>
    public static Task<bool> StoringLockedByCellAsync(WcsDbContext context, decimal cellId) =>
        context.DoorMappings.AsNoTracking().AnyAsync(m => m.CellId == cellId && m.StoringLocked);

    /// <summary>
    /// La <b>porta</b> e' bloccata al deposito? E' la domanda di chi sta per mettere il blocco e
    /// deve sapere se c'era gia': <see cref="SetStoringLockedAsync"/> scrive per codice porta, e
    /// solo chi ha trovato la porta libera puo' poi rimetterla libera senza revocare il blocco di
    /// un altro. Non e' <see cref="StoringLockedByCellAsync"/>, che risponde sulla cella intera
    /// (vero se <b>una qualunque</b> delle sue porte e' bloccata): li' il blocco di un'altra porta
    /// farebbe credere bloccata anche questa, e il blocco messo qui non verrebbe mai tolto.
    /// </summary>
    public static Task<bool> StoringLockedByDoorAsync(WcsDbContext context, string doorCode) =>
        context.DoorMappings.AsNoTracking().AnyAsync(m => m.DoorCode == doorCode && m.StoringLocked);

    /// <summary>
    /// Le celle servite da un ricollocatore: quelle delle porte dei dispositivi che lo hanno come
    /// <c>RelocateItemId</c>, distinte e ordinate (la stessa join di <c>PlantQueries.AutomaticCellsAsync</c>).
    /// </summary>
    public static Task<List<decimal>> CellsByRelocatorAsync(WcsDbContext context, Guid relocatorItemId) =>
        (from mapping in context.DoorMappings.AsNoTracking()
         join item in context.PlantItems.AsNoTracking() on mapping.ItemId equals item.ItemId
         where item.RelocateItemId == relocatorItemId
         select mapping.CellId).Distinct().OrderBy(id => id).ToListAsync();

    public static async Task SetOperationActiveAsync(WcsDbContext context, decimal cellId, short operationActive)
    {
        var rows = await context.DoorMappings.Where(m => m.CellId == cellId).ToListAsync();
        foreach (var row in rows) row.OperationActive = operationActive;
    }

    /// <summary>
    /// Sostituisce tutte le righe di un dispositivo con quelle date, conservando i due stati delle
    /// righe che restano sulla stessa porta: un collegamento rifatto dall'Editor non deve
    /// sbloccare una cella che il motore ha impegnato.
    /// </summary>
    public static async Task ReplaceForItemAsync(WcsDbContext context, Guid itemId, IReadOnlyCollection<DoorMapping> mappings)
    {
        var existing = await context.DoorMappings.Where(m => m.ItemId == itemId).ToListAsync();
        context.DoorMappings.RemoveRange(existing);

        foreach (var mapping in mappings)
        {
            var previous = existing.FirstOrDefault(m => m.DoorCode == mapping.DoorCode && m.CellId == mapping.CellId);
            context.DoorMappings.Add(new DoorMapping
            {
                ItemId = itemId,
                DoorCode = mapping.DoorCode,
                CellId = mapping.CellId,
                Side = mapping.Side,
                OperationActive = previous?.OperationActive ?? mapping.OperationActive,
                StoringLocked = previous?.StoringLocked ?? mapping.StoringLocked
            });
        }
    }
}
