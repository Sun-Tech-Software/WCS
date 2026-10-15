using Wcs.Api.Client;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Model;

namespace Wcs.WebEditor.Editing;

/// <summary>
/// Riscrive l'intera mappa porte di un dispositivo Storage (compito 11): <c>DoorMappings.razor</c>
/// costruisce l'insieme desiderato dalle due tabelle (porta A, porta B) e questo comando lo scrive con
/// una chiamata sola attraverso <see cref="DoorMappingWrites.WriteAsync"/>, che sceglie da sola fra PUT
/// e DELETE a seconda che l'insieme sia vuoto o no - si veda il remarks di quel metodo.
/// </summary>
/// <remarks>
/// <see cref="OldMappings"/> e' la fotografia "di prima", catturata da chi costruisce il comando
/// (<c>PlantCanvas</c>) prima di scrivere - la stessa idea di <see cref="EditItemConfigCommand.OldConfig"/>:
/// <see cref="UndoAsync"/> la rimette cosi' com'era, non ricostruita da una nuova lettura.
/// <para>
/// Nessuna rilettura del grafo qui: la mappa porte non fa parte di <see cref="PlantGraph.Items"/> (si
/// veda il record <c>PlantItemDto</c>) - a differenza di <see cref="LinkBufferCellCommand"/>, che
/// tocca anche <c>PlantItemConfigDto.LinkedCellId</c> e per questo rilegge il dispositivo.
/// </para>
/// </remarks>
public sealed record ReplaceDoorMappingsCommand : EditCommand
{
    public Guid ItemId { get; }
    public IReadOnlyList<DoorMappingDto> OldMappings { get; }
    public IReadOnlyList<DoorMappingDto> NewMappings { get; }

    public ReplaceDoorMappingsCommand(Guid itemId, IReadOnlyList<DoorMappingDto> oldMappings, IReadOnlyList<DoorMappingDto> newMappings)
    {
        if (oldMappings.Any(m => m.ItemId != itemId) || newMappings.Any(m => m.ItemId != itemId))
            throw new ArgumentException($"Le mappature devono appartenere tutte al dispositivo {itemId}.");

        ItemId = itemId;
        OldMappings = oldMappings;
        NewMappings = newMappings;
    }

    public override string Description => "Modifica mappa porte";

    public override Task ApplyAsync(WcsClient client, PlantGraph graph) => DoorMappingWrites.WriteAsync(client, ItemId, NewMappings);

    public override Task UndoAsync(WcsClient client, PlantGraph graph) => DoorMappingWrites.WriteAsync(client, ItemId, OldMappings);
}
