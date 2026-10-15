using Wcs.Api.Client;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Model;

namespace Wcs.WebEditor.Editing;

/// <summary>
/// Cancella una giunzione sola (compito 8): <c>DELETE /plant/junctions/{id}</c> - a differenza di
/// <see cref="DeleteItemCommand"/>, che porta via anche le giunzioni toccate dal dispositivo, qui non
/// c'e' nient'altro da portare via insieme: una giunzione non ha figli.
/// </summary>
/// <remarks>
/// <b>L'annulla rimette la forma, non l'identita'.</b> Come <see cref="DeleteItemCommand"/> dichiara
/// per le proprie giunzioni: <see cref="JunctionRequest"/> non porta un identificativo, quindi
/// <see cref="UndoAsync"/> ricrea la giunzione con <c>POST /plant/junctions</c> e riceve un
/// <see cref="JunctionDto.JunctionId"/> nuovo, mai quello cancellato. Non serve invece la guardia
/// "gia' ripristinata?" che <see cref="DeleteItemCommand.UndoAsync"/> applica sulle proprie: quella
/// esiste perche' l'annulla di una cancellazione di dispositivo e' piu' passi (dispositivo, poi N
/// giunzioni) che possono fallire a meta' e essere ritentati parzialmente. Qui l'annulla e' un passo
/// solo: se fallisce, <see cref="CommandStack"/> lascia il comando in pila e un ritentativo rifa'
/// la stessa singola <c>POST</c> - innocua anche ripetuta, perche' l'host collassa sulla coppia
/// (origine, destinazione): si veda il remarks di <see cref="AddJunctionCommand"/>.
/// </remarks>
public sealed record DeleteJunctionCommand(JunctionDto Junction) : EditCommand
{
    public override string Description => "Cancella giunzione";

    public override async Task ApplyAsync(WcsClient client, PlantGraph graph)
    {
        await client.DeleteJunctionAsync(Junction.JunctionId);
        graph.RemoveJunction(Junction.JunctionId);
    }

    public override async Task UndoAsync(WcsClient client, PlantGraph graph)
    {
        var recreated = await client.AddJunctionAsync(
            new JunctionRequest(Junction.SourceItemId, Junction.DestinationItemId, Junction.Priority));
        graph.AddJunction(recreated);
    }
}
