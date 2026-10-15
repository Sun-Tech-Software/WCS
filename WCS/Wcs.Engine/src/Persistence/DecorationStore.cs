using Microsoft.EntityFrameworkCore;
using Wcs.Engine.Persistence.Entities;

namespace Wcs.Engine.Persistence;

/// <summary>
/// La decorazione del disegno sul database del WCS: una riga sola, letta e scritta come blocco
/// unico. Non la interpretiamo, la custodiamo cosi' com'e' per l'Editor - stessa idea di
/// <see cref="PlantStore.GetOrCreateDiagramAsync"/>, sull'unica riga di <c>PlantDiagrams</c>.
/// </summary>
internal static class DecorationStore
{
    /// <summary>
    /// L'XML della decorazione, o il vuoto se l'impianto non ne ha mai avuta una: e' il caso
    /// normale di ogni impianto appena creato, non un guasto da segnalare.
    /// </summary>
    public static async Task<string> GetAsync(WcsDbContext context)
    {
        var tracked = context.ChangeTracker.Entries<PlantDecoration>().Select(e => e.Entity)
            .FirstOrDefault(d => d.Id == PlantDecoration.DefaultId);
        if (tracked != null) return tracked.Xml;

        var row = await context.PlantDecorations.AsNoTracking()
            .SingleOrDefaultAsync(d => d.Id == PlantDecoration.DefaultId);
        return row?.Xml ?? string.Empty;
    }

    /// <summary>
    /// Sostituisce la decorazione, creando la riga se manca. Non salva: come le sorelle
    /// (<see cref="PlantStore"/>, <see cref="DoorMappingStore"/>), il salvataggio e' di chi chiama.
    /// </summary>
    public static async Task SetAsync(WcsDbContext context, string xml)
    {
        var tracked = context.ChangeTracker.Entries<PlantDecoration>().Select(e => e.Entity)
            .FirstOrDefault(d => d.Id == PlantDecoration.DefaultId);
        var existing = tracked ?? await context.PlantDecorations.SingleOrDefaultAsync(d => d.Id == PlantDecoration.DefaultId);

        if (existing == null)
        {
            context.PlantDecorations.Add(new PlantDecoration { Id = PlantDecoration.DefaultId, Xml = xml, UpdatedAtUtc = DateTime.UtcNow });
            return;
        }

        existing.Xml = xml;
        existing.UpdatedAtUtc = DateTime.UtcNow;
    }
}
