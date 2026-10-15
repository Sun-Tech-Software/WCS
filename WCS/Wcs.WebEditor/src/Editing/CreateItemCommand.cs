using Wcs.Api.Client;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Model;

namespace Wcs.WebEditor.Editing;

/// <summary>
/// Crea un dispositivo (compito 7): <c>POST /plant/items</c> con l'identificativo che porta
/// <see cref="Config"/>, e la geometria si scrive subito dopo con la rotta della geometria, nel punto
/// dove l'utente ha rilasciato - la stessa sequenza che il brief del compito descrive.
/// </summary>
/// <remarks>
/// <para>
/// <b>La prima scrittura era <c>PUT /plant/items/{id}/config</c>, e non lo e' piu'.</b> Quella rotta
/// era un upsert: creava quando l'identificativo era nuovo e sovrascriveva quando c'era gia', e non
/// c'era modo di distinguere «creo» da «resuscito un dispositivo che qualcun altro ha appena
/// cancellato». Da questo lotto la PUT risponde <c>404</c> sul dispositivo assente e la creazione ha
/// una rotta sua, che risponde <c>409</c> se quell'identificativo e' gia' un dispositivo
/// dell'impianto - un esito che questo comando non tratta a parte, perche' l'identificativo lo
/// sceglie chi lo costruisce con un <c>Guid.NewGuid()</c> appena prima (<c>DrawioEditor</c> e
/// <c>PlantCanvas</c> fanno cosi'): a un <c>409</c> ci arriverebbe solo un <c>Guid</c> ripetuto.
/// </para>
/// <para>
/// <b>Due scritture, non una - un limite dichiarato, non nascosto.</b> A differenza di
/// <see cref="MoveItemsCommand"/>, che scrive con una sola PUT, creare un dispositivo vuole due
/// chiamate (creazione, poi geometria): non esiste una rotta che le componga in una
/// transazione sola. Se la prima riesce e la seconda fallisce (per esempio il permesso perso a
/// meta' gesto), <see cref="ApplyAsync"/> solleva e <see cref="CommandStack.DoAsync"/> non mette il
/// comando in pila (compito 6): sull'host resta una riga di configurazione senza
/// geometria, orfana e non annullabile da questa sessione. Non e' un dato invisibile - al prossimo
/// caricamento del grafo comparirebbe fra i "Dispositivi non collocati" di <c>PlantCanvas</c> - ma
/// non si ripulisce da sola. Costruire una compensazione automatica (cancellare la configurazione
/// appena scritta se la geometria fallisce) e' fuori dallo scopo di questo compito: la rotta di
/// configurazione e quella di geometria restano due chiamate HTTP indipendenti sul contratto di
/// oggi.
/// </para>
/// <para>
/// <b>L'ultimo passo legge dall'host, non inventa lo stato dal vivo.</b> <see cref="PlantItemDto.Live"/>
/// porta stato che solo il motore conosce (nessun comando in corso, nessun carico) - inventare qui
/// un valore di default sarebbe indovinare, non leggere un dato vero. <see cref="ApplyAsync"/> chiude
/// quindi con <c>client.GetItemAsync</c>, che rilegge il dispositivo appena creato cosi' come il
/// motore lo vede davvero.
/// </para>
/// </remarks>
public sealed record CreateItemCommand : EditCommand
{
    public PlantItemConfigDto Config { get; }
    public PlantItemGeometryDto Geometry { get; }
    private readonly string _typeLabel;

    /// <summary><paramref name="geometry"/> deve portare lo stesso <see cref="PlantItemConfigDto.ItemId"/> di <paramref name="config"/> - sono due meta' dello stesso dispositivo nuovo, non due dispositivi diversi.</summary>
    public CreateItemCommand(PlantItemConfigDto config, PlantItemGeometryDto geometry, string typeLabel)
    {
        if (geometry.ItemId != config.ItemId)
            throw new ArgumentException(
                $"La geometria porta l'ItemId {geometry.ItemId}, la configurazione {config.ItemId}: devono essere lo stesso dispositivo nuovo.");

        Config = config;
        Geometry = geometry;
        _typeLabel = typeLabel;
    }

    public override string Description => $"Crea {_typeLabel}";

    public override async Task ApplyAsync(WcsClient client, PlantGraph graph)
    {
        await client.CreateItemAsync(Config);
        // Queue item 21: the device exists with this configuration from here on.
        graph.ApplyConfiguration(Config);
        var created = await client.GetItemAsync(Config.ItemId)
            ?? throw new InvalidOperationException(
                $"The device just created ({Config.ItemId}) is not on the host any more: the creation is inconsistent.");
        var confirmation = await client.SetItemGeometryAsync(
            GeometryWriter.ToUpdate(Geometry, created.GeometryVersion));
        graph.AddItem(GeometryWriter.ApplyConfirmation(created, Geometry, confirmation));
    }

    /// <summary>Disfare una creazione e' cancellarla: una sola chiamata, perche' un dispositivo appena nato non puo' ancora avere giunzioni proprie (nessun gesto di questo compito le crea insieme al dispositivo).</summary>
    public override async Task UndoAsync(WcsClient client, PlantGraph graph)
    {
        await client.DeleteItemAsync(Config.ItemId);
        graph.RemoveItem(Config.ItemId);
    }
}
