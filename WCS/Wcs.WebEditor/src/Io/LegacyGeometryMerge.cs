using Wcs.Api.Contracts.Dtos;

namespace Wcs.WebEditor.Io;

/// <summary>
/// Quello che <c>LegacyGeometryImportDto</c> (letto da un vecchio disegno <c>.edd</c>/<c>.xml</c>
/// con <c>DiagramGeometryReader</c>, lato host) diventa quando si versa nello stesso percorso
/// dell'importazione nativa (compito 12, punto 3 del brief: "la sua uscita si versa nello stesso
/// percorso").
/// </summary>
/// <remarks>
/// <b>Un vecchio disegno non porta la configurazione di un dispositivo</b> - solo il suo Guid e la
/// sua posizione nel riquadro. Non puo' quindi ricreare un impianto da zero: puo' solo dire dove
/// stanno i dispositivi che l'impianto di ADESSO gia' conosce. Per questo <see cref="Apply"/> parte
/// da <paramref name="current"/> (dispositivi, giunzioni e mappature restano quelli di adesso, non
/// toccati) e sostituisce solo le geometrie che il disegno porta per un Guid noto: un dispositivo il
/// cui Guid non compare affatto nel disegno mantiene la posizione che aveva, non la perde - il
/// disegno semplicemente non ne parla, il che e' diverso da "il disegno dice che non e' collocato".
/// </remarks>
public static class LegacyGeometryMerge
{
    /// <summary>
    /// L'impianto proposto (per <see cref="PlantGraphDiff.Compare"/> e per la successiva
    /// <c>PUT /plant/graph</c>), quanti Guid del disegno non corrispondono a nessun dispositivo di
    /// <paramref name="current"/> - non possono diventare dispositivi nuovi (manca tutto il resto
    /// della loro configurazione), quindi restano scartati: <see cref="UnknownGuids"/> lo dice,
    /// invece di sparire in silenzio - e quanti dispositivi di <paramref name="current"/> il disegno
    /// non nomina affatto (<see cref="ItemsNotMentioned"/>, correzione Importante 6 della revisione):
    /// mantengono la posizione che avevano, ma l'utente merita di sapere quanti sono, non solo che
    /// esistono.
    /// </summary>
    public static (PlantGraphDto Proposed, int UnknownGuids, int ItemsNotMentioned) Apply(PlantGraphDto current, LegacyGeometryImportDto legacy)
    {
        var currentItemIds = current.Items.Select(i => i.ItemId).ToHashSet();
        var unknownGuids = legacy.Geometries.Count(g => !currentItemIds.Contains(g.ItemId));

        var mentionedItemIds = legacy.Geometries.Select(g => g.ItemId).ToHashSet();
        var itemsNotMentioned = current.Items.Count(i => !mentionedItemIds.Contains(i.ItemId));

        var replacements = legacy.Geometries
            .Where(g => currentItemIds.Contains(g.ItemId))
            .ToDictionary(g => g.ItemId);

        var geometries = current.Geometries
            .Select(g => replacements.TryGetValue(g.ItemId, out var replacement) ? replacement : g)
            .ToList();

        // Decoration and PLC connections back to null, and NOT the values "with" would carry over
        // from current: an old .edd/.xml speaks about neither. Besides making the confirmation
        // truthful, null avoids rewriting a stale snapshot of PLC settings changed after this read.
        return (current with { Geometries = geometries, Decoration = null, PlcConnections = null },
            unknownGuids, itemsNotMentioned);
    }
}
