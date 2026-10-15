using Wcs.Api.Client;
using Wcs.Api.Contracts.Dtos;

namespace Wcs.WebEditor.Editing;

/// <summary>
/// Scrive l'insieme finale delle mappature porte di UN dispositivo (compito 11), scegliendo da sola
/// fra le due rotte che l'host offre - nessuna delle due, presa da sola, copre ogni insieme
/// desiderato.
/// </summary>
/// <remarks>
/// <c>WcsClient.ReplaceDoorMappingsAsync</c> arriva a <c>PlantQueries.ReplaceDoorMappingsAsync</c>
/// (<c>Wcs.Engine/src/Api/PlantQueries.cs</c>), che raggruppa l'elenco per <c>ItemId</c>
/// (<c>mappings.GroupBy(m =&gt; m.ItemId)</c>) e sostituisce solo i dispositivi presenti in quel
/// raggruppamento - un dispositivo ASSENTE dall'elenco resta come sta (e' proprio la ragione per cui
/// una PUT con un elenco parziale non "aggiunge" una riga: bisogna passare l'intero insieme desiderato
/// per quel dispositivo, non solo la riga nuova). Passare un elenco VUOTO per svuotare la mappa di un
/// dispositivo e' pero' un no-op silenzioso: <c>GroupBy</c> su un elenco vuoto non produce nessun
/// gruppo, <c>ReplaceForItemAsync</c> non viene mai chiamata per nessuno, e le righe che si volevano
/// cancellare restano sull'host - un difetto che non si vede finche' non si rilegge la mappa dopo il
/// salvataggio. <c>ClearDoorMappingsAsync</c> (<c>DELETE /plant/door-mappings/{itemId}</c>) e' la sola
/// rotta che svuota la mappa di UN dispositivo lasciandolo al suo posto (<c>DELETE /plant/items</c>
/// svuota anche lei la mappa di ogni dispositivo, ma insieme all'impianto intero:
/// <c>PlantQueries.RemoveAllItemsAsync</c>), e questo metodo la sceglie ogni volta che l'insieme
/// desiderato e' vuoto - cosi' <see cref="ReplaceDoorMappingsCommand"/>, <see cref="ClearDoorMappingsCommand"/>,
/// <see cref="LinkBufferCellCommand"/> e <see cref="UnlinkBufferCellCommand"/> non devono replicare
/// ciascuno la stessa scelta.
/// </remarks>
public static class DoorMappingWrites
{
    /// <summary>
    /// <paramref name="mappings"/> deve portare solo righe di <paramref name="itemId"/>: guardia
    /// difensiva, non un filtro - chi chiama ha gia' preso solo le righe di quel dispositivo (per
    /// esempio filtrando <c>GetDoorMappingsAsync()</c>, che le torna tutte, non una per dispositivo).
    /// </summary>
    public static Task WriteAsync(WcsClient client, Guid itemId, IReadOnlyList<DoorMappingDto> mappings)
    {
        if (mappings.Any(m => m.ItemId != itemId))
            throw new ArgumentException($"Una riga della mappa porte non appartiene al dispositivo {itemId}.", nameof(mappings));

        return mappings.Count == 0
            ? client.ClearDoorMappingsAsync(itemId)
            : client.ReplaceDoorMappingsAsync(mappings);
    }
}
