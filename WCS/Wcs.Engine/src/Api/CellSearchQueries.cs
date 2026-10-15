using Microsoft.EntityFrameworkCore;
using Wcs.Engine.Collections;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Api;

/// <summary>Una cella automatica con il blocco al deposito e lo stato del suo ricollocatore: vedi <see cref="CellSearchQueries"/>.</summary>
public sealed record AutomaticCellState(decimal CellId, Guid ItemId, Guid RelocatorItemId, bool StoringLocked, DeviceStatusTypes RelocatorStatus);

/// <summary>La fotografia per una ricerca celle: celle automatiche, celle in coda, UDC in coda.</summary>
public sealed record CellSearchContext(List<AutomaticCellState> AutomaticCells, List<decimal> CellsInExtraction, List<decimal> LoadUnitsInExtraction);

/// <summary>
/// La fotografia che la ricerca celle del WMS riceveva dalle tabelle: la CTE LISTA_CELLE_AUTOMATICHE
/// (ITEMS_MAP, NAVIGATOR_ITEMS con il ricollocatore, STORING_LOCKED, STATUS) e i due NOT EXISTS su
/// ESTRAZIONI_UDC_AUTOMATICHE. Una lettura sola dello store, poi lo stato dal vivo dai dispositivi
/// in memoria: e' l'unione di <see cref="PlantQueries.AutomaticCellsAsync"/>,
/// <see cref="ExtractionQueueQueries.CellsAsync"/> e <see cref="ExtractionQueueQueries.LoadUnitsAsync"/>,
/// che restano per chi le usa gia'.
/// </summary>
/// <remarks>
/// Una cella con due porte ha due righe nella mappa: e' bloccata al deposito solo se lo sono
/// tutte, perche' la CTE la teneva se almeno una riga aveva STORING_LOCKED = 0. Un ricollocatore che
/// il motore non ha in memoria e' <c>OffLine</c>: un dispositivo che non c'e' non e' pronto.
/// </remarks>
public static class CellSearchQueries
{
    private sealed record MappingRow(decimal CellId, Guid ItemId, Guid RelocatorItemId, bool StoringLocked);
    private sealed record QueueRow(decimal? CellId, decimal LoadUnitId);
    private sealed record Snapshot(List<MappingRow> Mappings, List<QueueRow> Queue);

    public static async Task<CellSearchContext> ContextAsync()
    {
        var snapshot = await StoreAccess.ReadAsync(async c => new Snapshot(
            await (
                from mapping in c.DoorMappings.AsNoTracking()
                join item in c.PlantItems.AsNoTracking() on mapping.ItemId equals item.ItemId
                where item.RelocateItemId != null
                select new MappingRow(mapping.CellId, item.ItemId, item.RelocateItemId!.Value, mapping.StoringLocked)).ToListAsync(),
            await c.ExtractionQueue.AsNoTracking().Where(x => !x.Processed)
                .Select(x => new QueueRow(x.CellId, x.LoadUnitId)).ToListAsync()));

        var automatic = snapshot.Mappings
            .GroupBy(r => (r.CellId, r.ItemId, r.RelocatorItemId))
            .Select(g => new AutomaticCellState(g.Key.CellId, g.Key.ItemId, g.Key.RelocatorItemId,
                // Bloccata solo se tutte le porte lo sono: la ricerca celle la teneva se almeno una
                // porta era libera. Il controllo di allocazione (DoorMappingStore.StoringLockedByCellAsync)
                // fa la domanda opposta e blocca con una sola porta: due regole, non una da unificare.
                g.All(r => r.StoringLocked),
                PathDevices.Instance[g.Key.RelocatorItemId]?.Status ?? DeviceStatusTypes.OffLine))
            .OrderBy(a => a.CellId).ToList();
        var cells = snapshot.Queue.Where(q => q.CellId != null).Select(q => q.CellId!.Value).Distinct().ToList();
        var loadUnits = snapshot.Queue.Select(q => q.LoadUnitId).Distinct().ToList();
        return new CellSearchContext(automatic, cells, loadUnits);
    }
}
