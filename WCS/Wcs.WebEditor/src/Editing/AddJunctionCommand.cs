using Wcs.Api.Client;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Model;

namespace Wcs.WebEditor.Editing;

/// <summary>
/// Collega due dispositivi (compito 8): <c>POST /plant/junctions</c>. L'host rifiuta con <b>400</b>
/// gli estremi vuoti, gli estremi che non sono dispositivi dell'impianto e una giunzione fra un
/// dispositivo e se stesso (<c>PlantEndpoints.cs</c>, nella <c>MapPost</c> delle giunzioni) - chi
/// costruisce questo comando (<c>PlantCanvas</c>) non propone nemmeno il gesto in quel terzo caso,
/// invece di mandare una richiesta garantita fallire.
/// </summary>
/// <remarks>
/// <para>
/// <b>Una seconda POST sulla stessa coppia non duplica, ma non aggiorna nemmeno la priorita'.</b>
/// <c>PlantQueries.AddJunctionAsync</c> collassa sulla coppia (origine, destinazione) e, se la trova
/// gia' presente, restituisce quella esistente <b>senza toccarla</b> - una seconda <c>POST</c> con
/// una priorita' diversa risponde <c>201</c> con il DTO, ma la priorita' resta quella vecchia. Questo
/// comando non lo nasconde: <see cref="ApplyAsync"/> scrive nel grafo locale esattamente il DTO che
/// l'host restituisce, non la priorita' richiesta - se la coppia esisteva gia', il grafo locale
/// mostra la priorita' vera (quella vecchia), non quella che l'utente ha appena provato a dare.
/// <see cref="PlantGraph.AddJunction"/> deduplica per <see cref="JunctionDto.JunctionId"/>: se l'host
/// restituisce la giunzione gia' esistente (stesso <c>JunctionId</c> di prima), il grafo locale non
/// ne guadagna una seconda.
/// </para>
/// <para>
/// <b>L'identificativo nasce dall'host, non da chi costruisce il comando</b> - a differenza di
/// <see cref="CreateItemCommand"/>, dove l'<c>ItemId</c> e' scelto prima della scrittura.
/// <see cref="JunctionRequest"/> non porta un identificativo: <see cref="ApplyAsync"/> lo scopre solo
/// dopo che la scrittura e' riuscita, e lo tiene in un campo privato mutabile perche'
/// <see cref="UndoAsync"/> (chiamato piu' tardi sulla stessa istanza) ne ha bisogno per cancellare la
/// giunzione giusta. E' la stessa deviazione dal modello "tutto immutabile nel costruttore" gia'
/// dichiarata in <see cref="EditCommand"/>: qui e' necessaria, non solo comoda, perche' l'host non
/// promette in anticipo quale identificativo assegnera'. <see cref="CommandStack.UndoAsync"/> toglie
/// i comandi dalla pila per riferimento (non per valore), quindi questo campo mutabile non rompe
/// quella ricerca - si veda il commento di <see cref="CommandStack"/>.
/// </para>
/// </remarks>
public sealed record AddJunctionCommand : EditCommand
{
    public JunctionRequest Request { get; }

    private Guid? _createdJunctionId;

    /// <summary>
    /// L'identificativo assegnato dall'host alla giunzione appena creata: nullo prima di un
    /// <see cref="ApplyAsync"/> riuscito. Espone <see cref="_createdJunctionId"/> a chi costruisce il
    /// comando (<c>PlantCanvas.OnPortMouseUp</c>) perche' possa selezionare la giunzione appena nata -
    /// senza saperne l'identificativo il campo della priorita' resterebbe invisibile finche' l'utente
    /// non la sceglie da solo, anche se la priorita' scritta e' quella vera.
    /// </summary>
    public Guid? CreatedJunctionId => _createdJunctionId;

    public AddJunctionCommand(JunctionRequest request) => Request = request;

    public override string Description => "Crea giunzione";

    public override async Task ApplyAsync(WcsClient client, PlantGraph graph)
    {
        var created = await client.AddJunctionAsync(Request);
        _createdJunctionId = created.JunctionId;
        graph.AddJunction(created);
    }

    /// <summary>
    /// Cancella la giunzione appena creata. Se <see cref="ApplyAsync"/> non e' mai stato chiamato con
    /// successo su questa istanza <see cref="_createdJunctionId"/> resta nullo:
    /// <see cref="CommandStack.DoAsync"/> mette un comando in pila solo dopo un <c>ApplyAsync</c>
    /// riuscito, quindi questo ramo e' difensivo (nessun percorso noto lo raggiunge), non un caso
    /// atteso.
    /// </summary>
    public override async Task UndoAsync(WcsClient client, PlantGraph graph)
    {
        if (_createdJunctionId is not { } junctionId)
            throw new InvalidOperationException(
                "AddJunctionCommand.UndoAsync chiamato prima di un ApplyAsync riuscito: nessuna giunzione da cancellare.");

        await client.DeleteJunctionAsync(junctionId);
        graph.RemoveJunction(junctionId);
    }
}
