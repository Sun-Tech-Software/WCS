using Wcs.Api.Client;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Model;

namespace Wcs.WebEditor.Editing;

/// <summary>
/// Cambia la priorita' di una giunzione esistente (compito 8): <c>PUT /plant/junctions/{id}</c> -
/// l'unica strada per cambiarla davvero. Una seconda <c>POST /plant/junctions</c> sulla stessa
/// coppia <b>non</b> aggiorna la priorita' (si veda il remarks di <see cref="AddJunctionCommand"/>):
/// per questo la tela offre un gesto separato, dedicato, invece di lasciar credere che ricollegare
/// due dispositivi gia' collegati cambi la priorita'.
/// </summary>
/// <remarks>
/// <see cref="Junction"/> porta la priorita' <b>precedente</b> alla scrittura - catturata da chi
/// costruisce il comando (<c>PlantCanvas</c>) prima di chiamare <see cref="ApplyAsync"/>, non
/// ricostruita dopo - cosi' <see cref="UndoAsync"/> sa a quale priorita' tornare senza doverla
/// rileggere dall'host. <c>WcsClient.SetJunctionPriorityAsync</c> torna <c>false</c> se la giunzione
/// non e' (piu') sull'host (risposta <c>404</c>, per esempio cancellata da un'altra sessione fra la
/// selezione e questo clic): sia <see cref="ApplyAsync"/> che <see cref="UndoAsync"/> lo trattano come
/// un fallimento esplicito, non come un no-op silenzioso - il grafo locale non deve mostrare una
/// priorita' che l'host non ha mai accettato.
/// </remarks>
public sealed record SetJunctionPriorityCommand(JunctionDto Junction, int NewPriority) : EditCommand
{
    public override string Description => "Cambia priorità della giunzione";

    public override async Task ApplyAsync(WcsClient client, PlantGraph graph)
    {
        var found = await client.SetJunctionPriorityAsync(Junction.JunctionId,
            new JunctionRequest(Junction.SourceItemId, Junction.DestinationItemId, NewPriority));
        if (!found)
            throw new InvalidOperationException(
                $"Junction {Junction.JunctionId} is not on the host any more: its priority cannot be changed.");

        graph.AddJunction(Junction with { Priority = NewPriority });
    }

    public override async Task UndoAsync(WcsClient client, PlantGraph graph)
    {
        var found = await client.SetJunctionPriorityAsync(Junction.JunctionId,
            new JunctionRequest(Junction.SourceItemId, Junction.DestinationItemId, Junction.Priority));
        if (!found)
            throw new InvalidOperationException(
                $"Junction {Junction.JunctionId} is not on the host any more: its priority cannot be put back.");

        graph.AddJunction(Junction);
    }
}
