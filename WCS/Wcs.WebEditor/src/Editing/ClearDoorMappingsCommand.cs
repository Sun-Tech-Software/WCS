using Wcs.Api.Client;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Model;

namespace Wcs.WebEditor.Editing;

/// <summary>
/// Scioglie tutte le mappature porte di un dispositivo Storage (compito 11): <c>DELETE
/// /plant/door-mappings/{itemId}</c> diretta, non una <see cref="ReplaceDoorMappingsCommand"/> con un
/// elenco vuoto - un gesto dichiaratamente distinto, per lo stesso motivo per cui l'host offre due
/// rotte invece di una (si veda il remarks di <see cref="DoorMappingWrites"/>): "sciogliere" e'
/// un'intenzione diversa da "sostituire con niente", anche se il risultato sull'host coincide.
/// </summary>
public sealed record ClearDoorMappingsCommand : EditCommand
{
    public Guid ItemId { get; }
    public IReadOnlyList<DoorMappingDto> OldMappings { get; }

    public ClearDoorMappingsCommand(Guid itemId, IReadOnlyList<DoorMappingDto> oldMappings)
    {
        if (oldMappings.Any(m => m.ItemId != itemId))
            throw new ArgumentException($"Le mappature devono appartenere tutte al dispositivo {itemId}.");

        ItemId = itemId;
        OldMappings = oldMappings;
    }

    public override string Description => "Sciogli mappa porte";

    public override Task ApplyAsync(WcsClient client, PlantGraph graph) => client.ClearDoorMappingsAsync(ItemId);

    public override Task UndoAsync(WcsClient client, PlantGraph graph) => DoorMappingWrites.WriteAsync(client, ItemId, OldMappings);
}
