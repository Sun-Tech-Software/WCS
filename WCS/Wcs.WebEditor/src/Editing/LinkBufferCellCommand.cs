using Wcs.Api.Client;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Model;

namespace Wcs.WebEditor.Editing;

/// <summary>
/// Collega un dispositivo Automation/CircularDistributor/Buffer/LoadingAndBuffer/Loading a UNA cella
/// del gestionale (compito 11): <c>POST /plant/door-mappings/link</c>, la rotta che l'Editor WinForms
/// usa gia' per lo stesso gesto (<c>FrmEDITOR.Private.OpenCellToLink</c>) - non una
/// <see cref="ReplaceDoorMappingsCommand"/> costruita a mano, anche se il risultato sull'host e' lo
/// stesso (una sola riga, lato "A": <c>PlantPersistence.LinkBufferCellAsync</c> fissa <c>Side =
/// "A"</c> a prescindere da quello che questo comando manda).
/// </summary>
/// <remarks>
/// <para>
/// <b>Due scritture, come <see cref="CreateItemCommand"/> e <see cref="DuplicateItemCommand"/>.</b>
/// Il collegamento non e' solo la riga della mappa porte: <c>PlantItemConfigDto.LinkedCellId</c> e' la
/// cella che il motore legge davvero per instradare (<c>AutomationDeviceStorageManager</c>,
/// <c>PathMission.Methods</c>, entrambi in <c>Wcs.Engine</c>) - un campo distinto dalla mappa porte,
/// che l'Editor WinForms tiene allineato a mano dopo un collegamento riuscito
/// (<c>item.Config.LinkedCellId = idCella; item.TrySaveAsync(...)</c>). Senza la seconda scrittura la
/// riga della mappa porte esisterebbe mentre il motore instraderebbe ancora verso la cella (o verso
/// nessuna cella) di prima - un disallineamento silenzioso fra i due dati, non solo una mancata
/// parita' con l'Editor di oggi.
/// </para>
/// <para>
/// <b>L'ordine e' rovesciato rispetto all'Editor WinForms apposta: prima la configurazione, poi la
/// mappa porte.</b> Con il motore in Run <b>entrambe</b> le rotte rispondono <c>409</c>: da quando la
/// decisione del proprietario del progetto (l'impianto in sola lettura a motore in marcia) e' stata
/// applicata, <c>PlantQueries.LinkBufferCellAsync</c> chiama <c>RequireNotRunning</c> come
/// <c>UpsertItemConfigurationAsync</c> - si veda il remarks di
/// <c>PlantQueries.SetItemGeometryAsync</c>, che elenca le rotte a cui la guardia e' stata aggiunta.
/// <b>Questo remarks diceva il contrario</b> ("solo <c>PUT .../config</c> chiama
/// <c>RequireNotRunning</c>, nessuna guardia sul collegamento"): era vero quando e' stato scritto, ed
/// e' rimasto li' dopo che l'host era cambiato.
/// <para>
/// L'ordine resta questo, e la ragione sopravvive alla correzione: scrivendo per prima la
/// configurazione, il rifiuto arriva prima di qualunque scrittura - <see cref="ApplyAsync"/> solleva a
/// mani vuote, niente resta disallineato, e <see cref="CommandStack.DoAsync"/> non impila niente, come
/// deve, dato che non e' cambiato nulla. Con la guardia su tutte e due il caso del motore in Run e'
/// ormai coperto da qualunque ordine; quello che l'ordine continua a coprire e' ogni altro rifiuto che
/// tocchi una sola delle due rotte (il permesso, un <c>423</c>, un guasto del trasporto). Resta un
/// guasto piu' raro non coperto: se la configurazione scrive e la mappa porte fallisce poi, i due dati
/// restano disallineati finche' non si ritenta. <see cref="UndoAsync"/> rimette entrambi nello stesso
/// ordine, per lo stesso motivo.
/// </para>
/// </para>
/// </remarks>
public sealed record LinkBufferCellCommand : EditCommand
{
    public PlantItemConfigDto OldConfig { get; }
    public PlantItemConfigDto NewConfig { get; }
    public IReadOnlyList<DoorMappingDto> OldMappings { get; }
    public DoorMappingLink Link { get; }

    /// <summary><paramref name="oldConfig"/>, <paramref name="newConfig"/> e <paramref name="link"/> devono portare lo stesso <see cref="PlantItemConfigDto.ItemId"/> - tre fotografie dello stesso dispositivo, non di dispositivi diversi.</summary>
    public LinkBufferCellCommand(PlantItemConfigDto oldConfig, PlantItemConfigDto newConfig,
        IReadOnlyList<DoorMappingDto> oldMappings, DoorMappingLink link)
    {
        if (oldConfig.ItemId != newConfig.ItemId || oldConfig.ItemId != link.ItemId)
            throw new ArgumentException(
                $"La configurazione precedente porta l'ItemId {oldConfig.ItemId}, quella nuova {newConfig.ItemId}, il collegamento {link.ItemId}: devono essere lo stesso dispositivo.");
        if (oldMappings.Any(m => m.ItemId != link.ItemId))
            throw new ArgumentException($"Le mappature precedenti devono appartenere al dispositivo {link.ItemId}.");

        OldConfig = oldConfig;
        NewConfig = newConfig;
        OldMappings = oldMappings;
        Link = link;
    }

    public override string Description => "Collega cella automatica";

    public override async Task ApplyAsync(WcsClient client, PlantGraph graph)
    {
        await client.SaveItemConfigurationAsync(NewConfig);
        // Queue item 21: the configuration is written from here on, whatever the link does next.
        graph.ApplyConfiguration(NewConfig);
        await client.LinkBufferCellAsync(Link);
        await RereadAsync(client, graph, Link.ItemId);
    }

    public override async Task UndoAsync(WcsClient client, PlantGraph graph)
    {
        await client.SaveItemConfigurationAsync(OldConfig);
        graph.ApplyConfiguration(OldConfig);
        await DoorMappingWrites.WriteAsync(client, Link.ItemId, OldMappings);
        await RereadAsync(client, graph, Link.ItemId);
    }

    private static async Task RereadAsync(WcsClient client, PlantGraph graph, Guid itemId)
    {
        var updated = await client.GetItemAsync(itemId)
            ?? throw new InvalidOperationException(
                $"Device {itemId} is not on the host any more after its cell link was written.");
        graph.AddItem(updated);
    }
}
