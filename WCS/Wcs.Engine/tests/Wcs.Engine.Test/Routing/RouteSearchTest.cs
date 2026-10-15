using Wcs.Engine.Routing;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test.Routing;

/// <summary>
/// La ricerca come funzione pura: un grafo costruito a mano, nessun singleton del motore.
/// </summary>
public sealed class RouteSearchTest
{
    private static readonly Guid S = Guid.NewGuid(), A = Guid.NewGuid(), B = Guid.NewGuid(), C = Guid.NewGuid(), D = Guid.NewGuid();

    private static RouteNode Node(Guid id, DeviceTypes type = DeviceTypes.Automation, DeviceStatusTypes status = DeviceStatusTypes.Ready,
        int level = 0, int queued = 0, bool hasLu = false, Guid? luDestination = null) =>
        new(id, type, status, level, null, queued, hasLu, luDestination);

    private static RouteRequest Request(Guid from, Guid to, bool offline = false, Guid? gateway = null) =>
        new(from, to, gateway ?? Guid.Empty, IgnoreItemStatus: false, ConsiderOffLineAvailable: offline, ItemsToDiscard: new HashSet<Guid>());

    [Fact]
    public void FindsTheShortestRouteAndReportsTheGroupLevelOfEachHop()
    {
        var graph = new RouteGraph(
            [Node(S), Node(A, level: 3), Node(B), Node(C), Node(D)],
            [new(S, A, 1), new(A, D, 1), new(S, B, 2), new(B, C, 2), new(C, D, 2)]);

        var hops = RouteSearch.Find(graph, Request(S, D));

        Assert.Equal([(S, A, 0), (A, D, 3)], hops.Select(h => (h.FromId, h.ToId, h.FromGroupRoutePath)));
    }

    [Fact]
    public void PrunesPartialRoutesAsLongAsTheBestOneWithoutLosingEqualLengthAlternatives()
    {
        // Due rotte di due passi: la seconda deve restare in gioco per il confronto sugli step in coda
        var graph = new RouteGraph(
            [Node(S), Node(A, queued: 1), Node(B), Node(D)],
            [new(S, A, 1), new(A, D, 1), new(S, B, 2), new(B, D, 2)]);

        var hops = RouteSearch.Find(graph, Request(S, D));

        Assert.Equal([S, B, D], new[] { hops[0].FromId }.Concat(hops.Select(h => h.ToId)));
    }

    [Fact]
    public void AnUnreachableDestinationVisitsEachSimplePathOnce()
    {
        // Una scala a 4 traverse: 8 nodi, 20 archi, la destinazione non c'e'
        var left = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        var right = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        var nodes = left.Concat(right).Select(id => Node(id)).ToList();
        var edges = new List<RouteEdge>();
        for (var i = 0; i < 4; i++)
        {
            edges.Add(new(left[i], right[i], 1)); edges.Add(new(right[i], left[i], 1));
            if (i + 1 < 4)
            {
                edges.Add(new(left[i], left[i + 1], 1)); edges.Add(new(left[i + 1], left[i], 1));
                edges.Add(new(right[i], right[i + 1], 1)); edges.Add(new(right[i + 1], right[i], 1));
            }
        }
        var graph = new RouteGraph(nodes, edges);

        var hops = RouteSearch.Find(graph, Request(left[0], Guid.NewGuid()));

        Assert.Empty(hops);
        // Il motore ne fa 114: 57 per passata — il numero della tabella 3.3 del rapporto (scala a
        // 4 traverse, destinazione irraggiungibile) — e le passate sono due, perche' senza rotte
        // la High viene ripetuta come Normal. La potatura non scatta mai: il conteggio non dipende
        // da < o <=. L'uguaglianza fissa anche il calo: una passata in meno sarebbe una regola in meno
        Assert.Equal(114, RouteSearch.LastVisitCount);
    }

    [Fact]
    public void WhenEveryRouteSkipsTheGatewayTheSearchSaysHowManyItRejected()
    {
        // S -> A -> D; il gateway G esiste ma non e' raggiungibile da S
        var G = Guid.NewGuid();
        var graph = new RouteGraph(
            [Node(S), Node(A), Node(D), Node(G)],
            [new(S, A, 1), new(A, D, 1), new(G, D, 1)]);

        var hops = RouteSearch.Find(graph, Request(S, D, gateway: G), out var rejected);

        Assert.Empty(hops);
        Assert.Equal(2, rejected); // una per passata: High e poi Normal
    }
}
