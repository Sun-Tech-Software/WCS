using Wcs.Api.Client;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Model;

namespace Wcs.WebEditor.Editing;

/// <summary>
/// Scrive le sessanta proprieta' di configurazione di un dispositivo (compito 9): <c>PUT
/// /plant/items/{id}/config</c>, la stessa rotta di <see cref="CreateItemCommand"/> e
/// <see cref="DuplicateItemCommand"/> - qui pero' il dispositivo esiste gia', non nasce.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="OldConfig"/> e' catturato da chi costruisce il comando, non ricostruito dopo.</b>
/// <c>PropertyPanel</c> tiene la configurazione letta all'apertura del pannello; e' quella - non una
/// rilettura al momento dell'annulla - che <see cref="UndoAsync"/> rimette sull'host, per lo stesso
/// motivo di <see cref="SetJunctionPriorityCommand"/>: lo stato "di prima" deve essere quello visto
/// prima della scrittura, non quello che l'host ha adesso.
/// </para>
/// <para>
/// <b>Il grafo locale si aggiorna rileggendo dall'host, non copiando <see cref="NewConfig"/>.</b> Come
/// <see cref="CreateItemCommand"/>: <see cref="PlantItemDto.Live"/> porta stato che solo il motore
/// conosce, e alcune proprieta' scritte qui (<see cref="PlantItemConfigDto.Name"/>,
/// <see cref="PlantItemConfigDto.DeviceType"/> fra le altre) sono anche quelle che la tela disegna -
/// <see cref="ApplyAsync"/> e <see cref="UndoAsync"/> chiudono entrambi con <c>client.GetItemAsync</c>,
/// cosi' il nodo sulla tela mostra sempre cio' che l'host ha davvero accettato, non un'anticipazione.
/// </para>
/// </remarks>
public sealed record EditItemConfigCommand : EditCommand
{
    public PlantItemConfigDto OldConfig { get; }
    public PlantItemConfigDto NewConfig { get; }

    /// <summary><paramref name="oldConfig"/> e <paramref name="newConfig"/> devono portare lo stesso <see cref="PlantItemConfigDto.ItemId"/> - due fotografie dello stesso dispositivo, non due dispositivi diversi.</summary>
    public EditItemConfigCommand(PlantItemConfigDto oldConfig, PlantItemConfigDto newConfig)
    {
        if (oldConfig.ItemId != newConfig.ItemId)
            throw new ArgumentException(
                $"La configurazione precedente porta l'ItemId {oldConfig.ItemId}, quella nuova {newConfig.ItemId}: devono essere lo stesso dispositivo.");

        OldConfig = oldConfig;
        NewConfig = newConfig;
    }

    public override string Description => "Modifica configurazione dispositivo";

    public override async Task ApplyAsync(WcsClient client, PlantGraph graph)
    {
        await client.SaveItemConfigurationAsync(NewConfig);
        // Queue item 21: before the reread, so that a device written but not read back still has
        // its load unit drawn at the size that was written.
        graph.ApplyConfiguration(NewConfig);
        await RereadAsync(client, graph, NewConfig.ItemId);
    }

    public override async Task UndoAsync(WcsClient client, PlantGraph graph)
    {
        await client.SaveItemConfigurationAsync(OldConfig);
        graph.ApplyConfiguration(OldConfig);
        await RereadAsync(client, graph, OldConfig.ItemId);
    }

    private static async Task RereadAsync(WcsClient client, PlantGraph graph, Guid itemId)
    {
        var updated = await client.GetItemAsync(itemId)
            ?? throw new InvalidOperationException(
                $"Device {itemId} is not on the host any more after its configuration was written.");
        graph.AddItem(updated);
    }
}
