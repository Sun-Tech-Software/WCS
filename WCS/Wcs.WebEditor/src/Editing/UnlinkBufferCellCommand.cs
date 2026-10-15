using Wcs.Api.Client;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Model;

namespace Wcs.WebEditor.Editing;

/// <summary>
/// Scioglie il collegamento di un dispositivo Automation/CircularDistributor/Buffer/LoadingAndBuffer/Loading
/// (compito 11): il simmetrico di <see cref="LinkBufferCellCommand"/>, con le stesse due scritture
/// (configurazione, poi mappa porte) nello stesso ordine - <c>DELETE /plant/door-mappings/{itemId}</c>
/// invece di <c>POST .../link</c>, <c>LinkedCellId</c> riportato a zero invece che alla cella scelta.
/// </summary>
/// <remarks>
/// <para>
/// Zero e' il valore che il motore stesso tratta come "nessuna cella collegata", non un'invenzione di
/// questo comando: <c>AutomationDeviceStorageManager.MoveObstacleLu</c> (<c>Wcs.Engine</c>) lo
/// commenta esplicitamente ("Nessuna cella collegata: zero e' un dato legittimo, non un difetto").
/// </para>
/// <para>
/// <b>L'ordine (configurazione prima, mappa porte dopo) e' lo stesso di <see cref="LinkBufferCellCommand"/>
/// e per la stessa ragione (si veda il suo remarks):</b> il rifiuto arriva a mani vuote, e lo
/// scioglimento non riesce mai a meta' - con la mappa porte gia' svuotata e <c>LinkedCellId</c> ancora
/// sulla cella di prima. <b>Anche qui il remarks diceva il falso</b> ("solo <c>PUT .../config</c>
/// chiama <c>RequireNotRunning</c>"): <c>PlantQueries.ClearDoorMappingsAsync</c> la chiama anche lei,
/// e con il motore in Run rispondono <c>409</c> tutte e due.
/// </para>
/// </remarks>
public sealed record UnlinkBufferCellCommand : EditCommand
{
    public PlantItemConfigDto OldConfig { get; }
    public PlantItemConfigDto NewConfig { get; }
    public IReadOnlyList<DoorMappingDto> OldMappings { get; }

    /// <summary><paramref name="oldConfig"/> e <paramref name="newConfig"/> devono portare lo stesso <see cref="PlantItemConfigDto.ItemId"/> - due fotografie dello stesso dispositivo, non di dispositivi diversi.</summary>
    public UnlinkBufferCellCommand(PlantItemConfigDto oldConfig, PlantItemConfigDto newConfig, IReadOnlyList<DoorMappingDto> oldMappings)
    {
        if (oldConfig.ItemId != newConfig.ItemId)
            throw new ArgumentException(
                $"La configurazione precedente porta l'ItemId {oldConfig.ItemId}, quella nuova {newConfig.ItemId}: devono essere lo stesso dispositivo.");
        if (oldMappings.Any(m => m.ItemId != oldConfig.ItemId))
            throw new ArgumentException($"Le mappature precedenti devono appartenere al dispositivo {oldConfig.ItemId}.");

        OldConfig = oldConfig;
        NewConfig = newConfig;
        OldMappings = oldMappings;
    }

    public override string Description => "Scollega cella automatica";

    public override async Task ApplyAsync(WcsClient client, PlantGraph graph)
    {
        await client.SaveItemConfigurationAsync(NewConfig);
        // Queue item 21: the configuration is written from here on, whatever the unlink does next.
        graph.ApplyConfiguration(NewConfig);
        await client.ClearDoorMappingsAsync(OldConfig.ItemId);
        await RereadAsync(client, graph, OldConfig.ItemId);
    }

    public override async Task UndoAsync(WcsClient client, PlantGraph graph)
    {
        await client.SaveItemConfigurationAsync(OldConfig);
        graph.ApplyConfiguration(OldConfig);
        await DoorMappingWrites.WriteAsync(client, OldConfig.ItemId, OldMappings);
        await RereadAsync(client, graph, OldConfig.ItemId);
    }

    private static async Task RereadAsync(WcsClient client, PlantGraph graph, Guid itemId)
    {
        var updated = await client.GetItemAsync(itemId)
            ?? throw new InvalidOperationException(
                $"Device {itemId} is not on the host any more after its cell link was removed.");
        graph.AddItem(updated);
    }
}
