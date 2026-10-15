using Wcs.Api.Contracts.Dtos;

namespace Wcs.WebEditor.Io;

/// <summary>
/// Che cosa un'importazione cambierebbe rispetto all'impianto di adesso: i quattro conteggi delle
/// perdite del grafo e i tre conteggi delle connessioni PLC aggiunte, rimosse o modificate.
/// <see cref="PlantGraphDiff.Compare"/> li ricava confrontando due <see cref="PlantGraphDto"/> per
/// intero, senza applicare filtri propri.
/// </summary>
/// <remarks>
/// Correzione Importante 8 della revisione: questa classe non puo' garantire da sola che i due
/// <see cref="PlantGraphDto"/> che riceve siano completi - quella garanzia sta a chi li ha letti
/// (<c>GET /plant/graph</c>, corretto per leggere le giunzioni dallo store come tutto il resto).
/// L'esclusiva "nessun filtro nascosto" prometteva una garanzia che <see cref="Compare"/> non puo'
/// dare guardando solo i due grafi che gli vengono passati.
/// </remarks>
public sealed record PlantGraphDiff(
    int LostItems,
    int LostJunctions,
    int LostMappings,
    int LostGeometries,
    int AddedPlcConnections = 0,
    int RemovedPlcConnections = 0,
    int ModifiedPlcConnections = 0)
{
    /// <summary>Vero se l'importazione toglierebbe davvero qualcosa - non solo se sostituisce con qualcosa di identico.</summary>
    public bool IsDestructive => LostItems > 0 || LostJunctions > 0 || LostMappings > 0 ||
                                 LostGeometries > 0 || RemovedPlcConnections > 0;

    /// <summary>
    /// Confronta l'impianto di adesso (<paramref name="current"/>) con quello che l'importazione
    /// applicherebbe (<paramref name="proposed"/>): che cosa di <paramref name="current"/> non ha
    /// piu' un corrispondente in <paramref name="proposed"/>.
    /// </summary>
    /// <remarks>
    /// Le giunzioni si confrontano per la coppia (origine, destinazione), non per
    /// <see cref="JunctionDto.JunctionId"/>: un'importazione ricrea ogni giunzione con un Guid nuovo
    /// (<c>PlantQueries.AddJunctionAsync</c> nel motore), quindi confrontare per identificativo
    /// direbbe che ogni giunzione e' persa anche quando la stessa coppia di dispositivi resta
    /// collegata. Le mappature si confrontano per (dispositivo, codice porta), per la stessa ragione
    /// - l'Id della riga e' generato dallo store, non porta significato per chi importa.
    /// <para>
    /// Le geometrie non si confrontano dispositivo per dispositivo con un'uguaglianza secca: quello
    /// che conta e' se un dispositivo *collocato* adesso smette di esserlo (o sparisce del tutto) -
    /// non se le sue coordinate cambiano. Spostare un dispositivo non e' "perderlo".
    /// </para>
    /// <para>
    /// Le connessioni PLC si confrontano per identificativo e solo quando il file proposto porta il
    /// campo: il nullo identifica un backup precedente e significa che le configurazioni attuali
    /// saranno conservate, quindi tutti e tre i conteggi restano zero.
    /// </para>
    /// </remarks>
    public static PlantGraphDiff Compare(PlantGraphDto current, PlantGraphDto proposed)
    {
        var proposedItemIds = proposed.Items.Select(i => i.ItemId).ToHashSet();
        var lostItems = current.Items.Count(i => !proposedItemIds.Contains(i.ItemId));

        var proposedJunctionPairs = proposed.Junctions.Select(j => (j.SourceItemId, j.DestinationItemId)).ToHashSet();
        var lostJunctions = current.Junctions.Count(j => !proposedJunctionPairs.Contains((j.SourceItemId, j.DestinationItemId)));

        var proposedMappingKeys = proposed.DoorMappings.Select(m => (m.ItemId, m.DoorCode)).ToHashSet();
        var lostMappings = current.DoorMappings.Count(m => !proposedMappingKeys.Contains((m.ItemId, m.DoorCode)));

        var proposedGeometryByItem = proposed.Geometries.ToDictionary(g => g.ItemId);
        var lostGeometries = current.Geometries.Count(g =>
            IsPlaced(g) && (!proposedGeometryByItem.TryGetValue(g.ItemId, out var replacement) || !IsPlaced(replacement)));

        var addedPlcConnections = 0;
        var removedPlcConnections = 0;
        var modifiedPlcConnections = 0;
        if (proposed.PlcConnections is { } proposedConnections)
        {
            // The same comparison the grid of the PLC connections counts (row 62): one copy.
            var changes = PlcConnectionRules.Compare(current.PlcConnections ?? [], proposedConnections);
            addedPlcConnections = changes.Added.Count;
            removedPlcConnections = changes.Removed.Count;
            modifiedPlcConnections = changes.Modified.Count;
        }

        return new PlantGraphDiff(lostItems, lostJunctions, lostMappings, lostGeometries,
            addedPlcConnections, removedPlcConnections, modifiedPlcConnections);
    }

    /// <summary>
    /// Collocato per intero: coordinate <b>e</b> riquadro, non solo <c>X</c>/<c>Y</c> (correzione
    /// minore collegata all'Importante 8 della revisione - una geometria che perde larghezza e
    /// altezza ma tiene le coordinate non veniva contata, mentre il riquadro di conferma dice
    /// "confrontandoli per intero").
    /// </summary>
    private static bool IsPlaced(PlantItemGeometryDto geometry) =>
        geometry.X is not null && geometry.Y is not null && geometry.Width is not null && geometry.Height is not null;
}
