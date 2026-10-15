using static Wcs.Common.Enums;

namespace Wcs.Engine.Routing;

/// <summary>
/// La ricerca del percorso come funzione pura: una visita in profondita' che enumera i cammini
/// semplici dalla sorgente alla destinazione e sceglie il piu' corto in passi e, a parita', quello
/// con meno step in coda sui dispositivi attraversati. Due passate: la prima (<c>High</c>) evita i
/// dispositivi con step in coda la cui UDC va altrove, la seconda (<c>Normal</c>) li accetta.
/// Le regole sono quelle che <c>PathBuilder</c> aveva prima del 2026-09, con quattro differenze:
/// il gateway e' un vincolo sulla rotta intera (decisione del 2026-09-04: una rotta che non lo
/// attraversa non vale); la potatura scarta una rotta parziale gia' lunga quanto la migliore (le
/// manca almeno un passo, quindi non puo' ne' batterla ne' pareggiarla); le letture passano da
/// indici invece che da scansioni; il risultato e' sempre una lista, mai <c>null</c>.
/// </summary>
public static class RouteSearch
{
    /// <summary>Tetto di sicurezza sulla profondita' di una rotta; la terminazione la garantisce il controllo del ciclo.</summary>
    private const int MaxRouteDepth = 500;

    /// <summary>Quante chiamate ricorsive ha fatto l'ultima ricerca su questo thread: solo per i test di regressione sulle prestazioni.</summary>
    [ThreadStatic] public static int LastVisitCount;

    private enum Pass : byte { Normal = 0, High = 1 }

    public static IReadOnlyList<RouteHop> Find(RouteGraph graph, RouteRequest request) =>
        Find(graph, request, out _);

    /// <param name="arrivalsRejectedByGateway">
    /// Quante volte una rotta ha raggiunto la destinazione senza passare dal gateway ed e' stata
    /// scartata: con un risultato vuoto dice al chiamante che una rotta esisteva, e perche' non vale.
    /// </param>
    public static IReadOnlyList<RouteHop> Find(RouteGraph graph, RouteRequest request, out int arrivalsRejectedByGateway)
    {
        LastVisitCount = 0;
        arrivalsRejectedByGateway = 0;
        if (request.SourceId == Guid.Empty || request.DestinationId == Guid.Empty || request.SourceId == request.DestinationId)
            return [];

        // Un gateway che non e' un dispositivo non vale (ValidateGatewayItem); uno uguale alla
        // sorgente e' gia' raggiunto: la sorgente non compare mai come arrivo di un passo, e
        // il vincolo non potrebbe essere soddisfatto da nessuna rotta.
        var gateway = graph.Contains(request.GatewayId) && request.GatewayId != request.SourceId
            ? request.GatewayId
            : Guid.Empty;
        var state = new SearchState(graph, request with { GatewayId = gateway });

        state.Visit(Guid.Empty, request.SourceId, Pass.High, []);
        if (state.Routes.Count == 0)
            state.Visit(Guid.Empty, request.SourceId, Pass.Normal, []);

        arrivalsRejectedByGateway = state.ArrivalsRejectedByGateway;
        return state.Best();
    }

    private sealed class SearchState(RouteGraph graph, RouteRequest request)
    {
        public readonly List<List<RouteHop>> Routes = [];
        public int ArrivalsRejectedByGateway;
        private int _bestLength = int.MaxValue;

        public void Visit(Guid callingId, Guid fromId, Pass pass, List<RouteHop> soFar)
        {
            LastVisitCount++;

            // BuildCurrentRoute: la rotta parziale con il passo appena fatto
            var route = new List<RouteHop>(soFar);
            if (callingId != Guid.Empty)
                route.Add(Hop(callingId, fromId));

            // ShouldTerminatePath: profondita', potatura, ciclo, livello
            if (route.Count > MaxRouteDepth) return;
            if (_bestLength <= route.Count) return;
            var level = graph.Node(fromId)?.GroupRoutePathId ?? 0;
            for (var i = 0; i < route.Count; i++)
            {
                if (route[i].FromId == fromId) return;
                if (level != 0 && route[i].FromGroupRoutePath == level) return;
            }

            // GetAvailableJunctions: uscite che non tornano al chiamante ne' alla sorgente, poi gateway, poi scarto
            var edges = graph.OutgoingFrom(fromId)
                .Where(e => e.DestinationId != callingId && e.DestinationId != request.SourceId)
                .ToList();
            if (request.GatewayId != Guid.Empty && edges.Any(e => e.DestinationId == request.GatewayId))
                edges = edges.Where(e => e.DestinationId == request.GatewayId).ToList();
            if (request.ItemsToDiscard.Count > 0)
                edges = edges.Where(e => !request.ItemsToDiscard.Contains(e.DestinationId)).ToList();

            // TryReachDestination: la destinazione, o un membro del gruppo di destinazione, chiude la
            // rotta. Con un gateway la rotta vale solo se uno dei suoi passi arriva sul gateway
            // (compreso l'ultimo: gateway uguale alla destinazione o a un suo membro). Il filtro
            // sulle uscite qui sopra e' la guida verso il gateway, questo e' il vincolo. Se la rotta
            // chiusa qui non vale, la ricerca da questo nodo prosegue sulle altre uscite, senza
            // attraversare la destinazione: potrebbe esistere una deviazione che passa dal gateway.
            var arrival = edges.FirstOrDefault(IsArrival);
            if (arrival != null)
            {
                var complete = new List<RouteHop>(route) { Hop(fromId, arrival.DestinationId) };
                if (PassesThroughGateway(complete))
                {
                    AddRoute(complete);
                    return;
                }

                ArrivalsRejectedByGateway++;
                edges = edges.Where(e => !IsArrival(e)).ToList();
            }

            // ShouldStopAtStorageDevice: magazzini e buffer sono partenza o arrivo, mai transito
            if (route.Count > 0 && graph.Node(fromId)?.DeviceType is DeviceTypes.Storage or DeviceTypes.Buffer)
                return;

            // ContinuePathSearch: le uscite attraversabili, in ordine di step in coda crescente
            var next = new List<(Guid Id, int Queued)>();
            foreach (var edge in edges)
            {
                var node = graph.Node(edge.DestinationId);
                if (node == null) continue;
                if (IsAccessible(node, pass))
                    next.Add((node.Id, node.QueuedSteps));
            }
            foreach (var (id, _) in next.OrderBy(n => n.Queued))
                Visit(fromId, id, pass, route);
        }

        private RouteHop Hop(Guid fromId, Guid toId) => new(fromId, toId, graph.Node(fromId)?.GroupRoutePathId ?? 0);

        private bool IsArrival(RouteEdge edge) =>
            edge.DestinationId == request.DestinationId ||
            graph.Node(edge.DestinationId)?.BelongedGroupId == request.DestinationId;

        /// <summary>
        /// Il vincolo del gateway, con la stessa regola del setter di <c>PathMission.PositionItemId</c>:
        /// il gateway e' raggiunto quando un passo arriva su di lui, oppure su un dispositivo il cui
        /// gruppo di appartenenza e' il gateway. Senza la seconda meta', un gateway che e' un
        /// dispositivo-gruppo non sarebbe raggiungibile da nessuna rotta (un membro non e' il gruppo)
        /// e la clausola del setter non scatterebbe mai.
        /// </summary>
        private bool PassesThroughGateway(List<RouteHop> route) =>
            request.GatewayId == Guid.Empty ||
            route.Any(h => h.ToId == request.GatewayId ||
                           graph.Node(h.ToId)?.BelongedGroupId == request.GatewayId);

        /// <summary>IsJunctionAccessible + IsDeviceStatusAccessible: la tabella di stato di oggi.</summary>
        private bool IsAccessible(RouteNode node, Pass pass)
        {
            if (request.IgnoreItemStatus)
                return !IsBlocking(node, pass);

            return node.Status switch
            {
                DeviceStatusTypes.Ready or DeviceStatusTypes.Busy or DeviceStatusTypes.Recovering or DeviceStatusTypes.Locked => true,
                DeviceStatusTypes.Loading or DeviceStatusTypes.Unloading or DeviceStatusTypes.Moving => !IsBlocking(node, pass),
                DeviceStatusTypes.OffLine => request.ConsiderOffLineAvailable && !IsBlocking(node, pass),
                DeviceStatusTypes.Error or DeviceStatusTypes.Warning => request.ConsiderOffLineAvailable,
                _ => false
            };
        }

        /// <summary>
        /// CheckItemMissionSteps: nella passata High un dispositivo con step in coda blocca se l'UDC
        /// a bordo va altrove, o se non ha nessuna UDC a bordo (oggi: <c>null != destinazione</c>).
        /// </summary>
        private bool IsBlocking(RouteNode node, Pass pass)
        {
            if (node.QueuedSteps > 0 && pass == Pass.High)
                return !node.HasLoadedLu || node.LoadedLuDestinationId != request.DestinationId;
            return false;
        }

        /// <summary>
        /// AddRoute: una rotta piu' corta scarta le altre; una piu' lunga della migliore non entra
        /// (con la potatura non puo' arrivare qui). La lista entra per riferimento senza copia: e'
        /// la candidata allocata a ogni arrivo in <see cref="Visit"/>, che dopo questa chiamata
        /// nessuno tocca piu'.
        /// </summary>
        private void AddRoute(List<RouteHop> route)
        {
            if (route.Count == 0 || route.Count > _bestLength) return;
            if (route.Count < _bestLength)
            {
                Routes.Clear();
                _bestLength = route.Count;
            }
            Routes.Add(route);
        }

        /// <summary>SelectBestRoute: fra le piu' corte, la prima con meno step in coda sui dispositivi di partenza di ogni passo.</summary>
        public IReadOnlyList<RouteHop> Best()
        {
            List<RouteHop>? best = null;
            var bestWait = int.MaxValue;
            foreach (var route in Routes)
            {
                var wait = route.Sum(h => graph.Node(h.FromId)?.QueuedSteps ?? 0);
                if (wait >= bestWait) continue;
                bestWait = wait;
                best = route;
            }
            return best ?? [];
        }
    }
}
