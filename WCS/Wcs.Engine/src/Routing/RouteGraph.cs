using static Wcs.Common.Enums;

namespace Wcs.Engine.Routing;

/// <summary>
/// Un dispositivo come lo vede la ricerca: tipo, stato, livello di gruppo, gruppo di appartenenza,
/// step in coda (di qualunque missione, che partono da qui) e l'UDC a bordo con la sua destinazione.
/// E' una fotografia: la ricerca non rilegge il motore.
/// </summary>
/// <param name="BelongedGroupId">L'identificatore del dispositivo-gruppo a cui questo appartiene, se c'e': una rotta verso il gruppo si chiude su un suo membro.</param>
/// <param name="QueuedSteps">Quanti step, di qualunque missione, partono da questo dispositivo: e' il «costo di attesa» e il criterio di ordine fra le uscite.</param>
public sealed record RouteNode(
    Guid Id,
    DeviceTypes DeviceType,
    DeviceStatusTypes Status,
    int GroupRoutePathId,
    Guid? BelongedGroupId,
    int QueuedSteps,
    bool HasLoadedLu,
    Guid? LoadedLuDestinationId);

/// <summary>Una giunzione: da dove, verso dove, con quale priorita' (crescente = esplorata prima).</summary>
public sealed record RouteEdge(Guid SourceId, Guid DestinationId, int Priority);

/// <summary>
/// Il grafo immutabile su cui gira <see cref="RouteSearch"/>: indice dei nodi e liste di adiacenza
/// gia' ordinate per priorita'. Un arco puo' puntare a un identificatore senza nodo (una giunzione
/// verso un dispositivo che il motore non conosce): resta nell'adiacenza, come oggi, ma non si
/// attraversa.
/// </summary>
public sealed class RouteGraph
{
    private static readonly IReadOnlyList<RouteEdge> NoEdges = [];
    private readonly Dictionary<Guid, RouteNode> _nodes;
    private readonly Dictionary<Guid, IReadOnlyList<RouteEdge>> _outgoing;

    public RouteGraph(IEnumerable<RouteNode> nodes, IEnumerable<RouteEdge> edges)
    {
        _nodes = nodes.ToDictionary(n => n.Id);
        _outgoing = edges
            .GroupBy(e => e.SourceId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<RouteEdge>)g.OrderBy(e => e.Priority).ToList());
    }

    public RouteNode? Node(Guid id) => _nodes.GetValueOrDefault(id);

    public bool Contains(Guid id) => _nodes.ContainsKey(id);

    public IReadOnlyList<RouteEdge> OutgoingFrom(Guid id) => _outgoing.GetValueOrDefault(id, NoEdges);
}
