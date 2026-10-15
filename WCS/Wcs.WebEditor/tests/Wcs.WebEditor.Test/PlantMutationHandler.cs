using System.Net;
using System.Net.Http.Json;
using Wcs.Api.Contracts.Dtos;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

/// <summary>
/// Il gestore HTTP finto per i tre gesti del compito 7 (creare, cancellare, duplicare): a
/// differenza di <see cref="GeometryCountingHandler"/> (che conta solo le due rotte della
/// geometria) tiene uno stato vero - configurazioni, geometrie e giunzioni - cosi' da poter
/// rispondere a <c>GET /plant/items/{id}</c> e <c>GET /plant/items/{id}/config</c> con quello che
/// e' stato scritto davvero, non con un corpo vuoto. Senza questo stato <c>CreateItemCommand.ApplyAsync</c>
/// (che chiude con una <c>GetItemAsync</c> per non inventare lo stato dal vivo) non avrebbe nulla
/// da leggere.
/// </summary>
/// <remarks>
/// Un gesto del compito 7 e' piu' di una chiamata (configurazione poi geometria, o dispositivo poi
/// giunzioni): un singolo "prossimo stato forzato" alla <see cref="GeometryCountingHandler.ResponseStatus"/>
/// non basterebbe a isolare quale passo fallisce. <see cref="FailNext"/> forza invece una sola rotta
/// (<see cref="Route"/>) per una sola richiesta a quella rotta, poi torna a nullo - le altre rotte
/// restano al loro esito normale nel frattempo.
/// </remarks>
public sealed class PlantMutationHandler : HttpMessageHandler
{
    public Dictionary<Guid, PlantItemConfigDto> Configs { get; } = [];
    public Dictionary<Guid, PlantItemGeometryDto> Geometries { get; } = [];
    public Dictionary<Guid, Guid> GeometryVersions { get; } = [];
    public List<JunctionDto> Junctions { get; } = [];

    /// <summary>
    /// La mappa porte dell'impianto intero (compito 11): una tabella sola, come sull'host vero
    /// (<c>c.DoorMappings</c> in <c>WcsDbContext</c>) - <c>GET /plant/door-mappings</c> non prende un
    /// ItemId e la torna sempre tutta, quindi il gestore finto deve tenerla cosi' per essere fedele
    /// alla forma vera della rotta, non per comodita' del test.
    /// </summary>
    public List<DoorMappingDto> DoorMappings { get; } = [];

    /// <summary>Le celle del gestionale che <c>GET /wms/cells</c> restituisce (compito 11) - impostate dal test, ignorando areaType/automaticOnly: chi vuole provare che il client li abbia mandati giusti legge <see cref="LastWmsCellsRequest"/>, non si aspetta che questo gestore finto rifiltri come farebbe l'host vero.</summary>
    public List<WmsCell> WmsCells { get; set; } = [];

    /// <summary>Le celle automatiche che <c>GET /plant/automatic-cells</c> restituisce (compito 11).</summary>
    public List<AutomaticCell> AutomaticCells { get; set; } = [];

    /// <summary>L'ultima richiesta a <c>GET /wms/cells</c> (compito 11): permette di verificare areaType/automaticOnly nella query, come <c>WmsConfigurationCheckHandler.LastRequest</c>.</summary>
    public HttpRequestMessage? LastWmsCellsRequest { get; private set; }

    /// <summary>Quante <c>POST /plant/items</c> sono arrivate, comprese quelle rifiutate con 409: la richiesta e' partita lo stesso.</summary>
    public int ItemCreates { get; private set; }

    /// <summary>
    /// Lo stato che <c>GET /status</c> risponde. Fermo di default, come l'impianto che si modifica.
    /// </summary>
    /// <remarks>
    /// Serve da quando <c>DeleteItemCommand</c> distingue le due cause del <c>409</c> di
    /// <c>POST /plant/items</c> - identificativo occupato contro motore in Run - chiedendo all'host
    /// il suo stato: un gestore finto che non rispondesse a questa rotta darebbe sempre la prima
    /// causa, e la prova del motore in marcia non potrebbe fallire.
    /// </remarks>
    public NavigatorStatusTypes NavStatus { get; set; } = NavigatorStatusTypes.Stop;

    /// <summary>Quante <c>GET /status</c> sono arrivate: e' una lettura in piu' sul percorso d'errore, e si vede.</summary>
    public int StatusGets { get; private set; }

    /// <summary>
    /// Mette sull'host finto dei dispositivi che ci sono gia', senza passare da nessuna rotta e
    /// senza contare come scrittura.
    /// </summary>
    /// <remarks>
    /// Serve da quando le rotte della mappa porte rispondono <c>404</c> sul dispositivo assente:
    /// un test che semina solo <see cref="DoorMappings"/> semina righe orfane, che sull'host vero
    /// la chiave esterna rifiuta. Non tocca i contatori apposta - quello che il test vuole contare
    /// sono le richieste del gesto in prova, non la preparazione.
    /// </remarks>
    public void SeedItems(params Guid[] itemIds)
    {
        foreach (var itemId in itemIds)
        {
            Configs[itemId] = new PlantItemConfigDto { ItemId = itemId };
            GeometryVersions[itemId] = Guid.NewGuid();
        }
    }

    public int ConfigWrites { get; private set; }
    public int ConfigGets { get; private set; }
    public int GeometryWrites { get; private set; }
    public int ItemGets { get; private set; }
    public int ItemDeletes { get; private set; }
    public int JunctionAdds { get; private set; }
    public int JunctionPriorityPuts { get; private set; }
    public int JunctionDeletes { get; private set; }
    public int DoorMappingGets { get; private set; }
    public int DoorMappingPuts { get; private set; }
    public int DoorMappingLinks { get; private set; }
    public int DoorMappingDeletes { get; private set; }
    public int WmsCellGets { get; private set; }
    public int AutomaticCellGets { get; private set; }

    /// <summary>
    /// Spento di default: ogni <c>POST /plant/junctions</c> crea sempre una giunzione nuova (si veda
    /// il commento sulla rotta sotto) - il comportamento che <c>DeleteItemCommandTest</c> vuole, per
    /// provare la guardia locale del proprio annulla contro un gestore piu' permissivo dell'host
    /// vero. Acceso, il gestore imita invece <c>PlantQueries.AddJunctionAsync</c> davvero: collassa
    /// sulla coppia (origine, destinazione) e restituisce la giunzione gia' presente invece di
    /// crearne una seconda - <c>JunctionDrawingTest</c> lo accende per provare la premessa 2 del
    /// compito 8 (una giunzione gia' esistente non si duplica, e non cambia priorita' con una
    /// seconda POST) senza toccare il comportamento che l'altra suite gia' si aspetta.
    /// </summary>
    public bool CollapseJunctionsByPair { get; set; }

    /// <summary>Le rotte che <see cref="FailNext"/> puo' mirare.</summary>
    public enum Route
    {
        ItemPost, ConfigPut, ConfigGet, GeometryPut, JunctionPost, JunctionPut, JunctionDelete, ItemDelete, ItemGet,
        DoorMappingsGet, DoorMappingsPut, DoorMappingsLink, DoorMappingsDelete, AutomaticCellsGet, WmsCellsGet
    }

    private readonly Dictionary<Route, HttpStatusCode> _pendingFailures = [];
    private readonly HashSet<Route> _pendingTimeouts = [];

    /// <summary>La prossima richiesta a <paramref name="route"/> risponde <paramref name="status"/> invece dell'esito normale; poi questa rotta torna a comportarsi normalmente (si veda il remarks della classe).</summary>
    public void FailNext(Route route, HttpStatusCode status) => _pendingFailures[route] = status;

    /// <summary>
    /// La prossima richiesta a <paramref name="route"/> solleva <see cref="TaskCanceledException"/>
    /// invece di rispondere - un host <b>appeso</b>, non spento: quello che il timeout di 30 s di
    /// <c>WcsClient</c> produce davvero quando l'host non risponde in tempo, a
    /// differenza di <see cref="FailNext"/> che simula una risposta arrivata con un codice di errore.
    /// Poi questa rotta torna al suo esito normale, come <see cref="FailNext"/>.
    /// </summary>
    public void ThrowTimeoutNext(Route route) => _pendingTimeouts.Add(route);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var segments = request.RequestUri!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var route = Classify(request.Method, segments);

        if (route is { } tr && _pendingTimeouts.Remove(tr))
            throw new TaskCanceledException("timeout finto: l'host non ha risposto entro il termine del client");

        if (route is { } r && _pendingFailures.Remove(r, out var forced))
            return new HttpResponseMessage(forced) { Content = new StringContent(string.Empty) };

        var (status, response) = await HandleAsync(request, segments, ct);
        return response ?? new HttpResponseMessage(status) { Content = new StringContent(string.Empty) };
    }

    private static Route? Classify(HttpMethod method, string[] segments)
    {
        // POST .../items: la creazione (il lotto del 404). Prima della ConfigPut per sola
        // leggibilita' - l'ultimo segmento "items" non e' ambiguo con nessun'altra rotta di qui,
        // nemmeno con "items/keep" o "items/connection-type", che finiscono altrimenti.
        if (method == HttpMethod.Post && segments is [.., "items"]) return Route.ItemPost;
        if (method == HttpMethod.Put && segments is [.., _, "config"]) return Route.ConfigPut;
        // ConfigGet: serve ai test che simulano un 500/503 sulla lettura della
        // configurazione che DeleteSelectedAsync/DuplicateSelectedAsync fanno prima di scrivere -
        // prima di questa rotta FailNext non poteva mirare quella GET, quindi quel caso
        // (l'eccezione che usciva dal try) non era provabile con questo gestore finto.
        if (method == HttpMethod.Get && segments is [.., _, "config"]) return Route.ConfigGet;
        if (method == HttpMethod.Put && segments is [.., var geometryId, "geometry"] && Guid.TryParse(geometryId, out _)) return Route.GeometryPut;
        if (method == HttpMethod.Post && segments is [.., "junctions"]) return Route.JunctionPost;
        // Le due rotte sotto (giunzione singola, compito 8) sono controllate PRIMA di ItemDelete:
        // "/plant/junctions/{id}" ha anch'esso un Guid come ultimo segmento, ambiguo con
        // "/plant/items/{id}" se si guarda solo l'ultimo segmento. L'ordine da solo basterebbe gia' a
        // risolverla (verificato: rimuovendo il controllo del penultimo segmento su ItemDelete, sotto,
        // e lasciando invariato l'ordine, la suite resta verde) - il controllo esplicito del penultimo
        // segmento resta comunque, come difesa contro un futuro riordino di questi rami, non perche'
        // un test di oggi lo richieda.
        if (method == HttpMethod.Put && segments is [.., "junctions", var priorityId] && Guid.TryParse(priorityId, out _)) return Route.JunctionPut;
        if (method == HttpMethod.Delete && segments is [.., "junctions", var junctionDeleteId] && Guid.TryParse(junctionDeleteId, out _)) return Route.JunctionDelete;
        if (method == HttpMethod.Delete && segments is [.., "items", var deleteId] && Guid.TryParse(deleteId, out _)) return Route.ItemDelete;
        // Le quattro rotte della mappa porte (compito 11), tutte controllate PRIMA del ripiego
        // generico ItemGet: "link" sotto POST non e' ambiguo con nient'altro, ma il DELETE con un Guid
        // in coda segue la stessa cautela gia' scritta sopra per le giunzioni/i dispositivi.
        if (method == HttpMethod.Get && segments is [.., "door-mappings"]) return Route.DoorMappingsGet;
        if (method == HttpMethod.Put && segments is [.., "door-mappings"]) return Route.DoorMappingsPut;
        if (method == HttpMethod.Post && segments is [.., "door-mappings", "link"]) return Route.DoorMappingsLink;
        if (method == HttpMethod.Delete && segments is [.., "door-mappings", var clearId] && Guid.TryParse(clearId, out _)) return Route.DoorMappingsDelete;
        if (method == HttpMethod.Get && segments is [.., "automatic-cells"]) return Route.AutomaticCellsGet;
        if (method == HttpMethod.Get && segments is [.., "wms", "cells"]) return Route.WmsCellsGet;
        if (method == HttpMethod.Get && segments is [.., var getId] && Guid.TryParse(getId, out _)) return Route.ItemGet;
        return null;
    }

    private async Task<(HttpStatusCode Status, HttpResponseMessage? Response)> HandleAsync(HttpRequestMessage request, string[] segments, CancellationToken ct)
    {
        // GET /status: lo stato del motore. Prima di ogni altra rotta perche' non ha nulla a che
        // vedere con l'impianto; "items/status" (l'elenco degli stati dal vivo) resta fuori, e'
        // un'altra rotta e questo gestore non la serve.
        if (request.Method == HttpMethod.Get && segments is [.., "status"] && segments is not [.., "items", "status"])
        {
            StatusGets++;
            return (HttpStatusCode.OK, Json(new EngineStatus(NavStatus, DefragmentationTypes.None, "finto", DateTime.UtcNow)));
        }

        // POST .../items: la creazione, con l'identificativo scelto dal client. 409 se
        // quell'identificativo e' gia' un dispositivo, come l'host: fino al lotto del 404 la
        // creazione non aveva una rotta sua e passava dalla PUT qui sotto, che era un upsert.
        if (request.Method == HttpMethod.Post && segments is [.., "items"])
        {
            ItemCreates++;
            var created = (await request.Content!.ReadFromJsonAsync<PlantItemConfigDto>(ct))!;
            if (Configs.ContainsKey(created.ItemId)) return (HttpStatusCode.Conflict, null);
            Configs[created.ItemId] = created;
            GeometryVersions[created.ItemId] = Guid.NewGuid();
            return (HttpStatusCode.Created, null);
        }

        // PUT .../items/{id}/config: MODIFICA, non piu' upsert - 404 sul dispositivo che non c'e',
        // e non scrive niente. Era la rotta che faceva nascere i dispositivi, e nessun chiamante
        // poteva distinguere "aggiorno" da "resuscito".
        if (request.Method == HttpMethod.Put && segments is [.., var configId, "config"] && Guid.TryParse(configId, out var configItemId))
        {
            ConfigWrites++;
            var config = (await request.Content!.ReadFromJsonAsync<PlantItemConfigDto>(ct))!;
            if (!Configs.ContainsKey(configItemId)) return (HttpStatusCode.NotFound, null);
            Configs[configItemId] = config;
            return (HttpStatusCode.NoContent, null);
        }

        // GET .../items/{id}/config
        if (request.Method == HttpMethod.Get && segments is [.., var getConfigId, "config"] && Guid.TryParse(getConfigId, out var getConfigItemId))
        {
            ConfigGets++;
            return Configs.TryGetValue(getConfigItemId, out var config)
                ? (HttpStatusCode.OK, Json(config))
                : (HttpStatusCode.NotFound, null);
        }

        // PUT .../items/{id}/geometry (singola - la rotta di gruppo non serve a questo compito, resta a GeometryCountingHandler)
        if (request.Method == HttpMethod.Put && segments is [.., var geometryId, "geometry"] && Guid.TryParse(geometryId, out var geometryItemId))
        {
            GeometryWrites++;
            var update = (await request.Content!.ReadFromJsonAsync<PlantItemGeometryUpdateDto>(ct))!;
            if (!GeometryVersions.TryGetValue(geometryItemId, out var currentVersion))
                return (HttpStatusCode.NotFound, null);
            if (update.ExpectedGeometryVersion != currentVersion)
                return (HttpStatusCode.Conflict,
                    Json(new PlantItemGeometryConflictDto([geometryItemId]), HttpStatusCode.Conflict));

            Geometries[geometryItemId] = new PlantItemGeometryDto(
                geometryItemId,
                update.X,
                update.Y,
                update.Width,
                update.Height,
                update.Rotation);
            var nextVersion = Guid.NewGuid();
            GeometryVersions[geometryItemId] = nextVersion;
            return (HttpStatusCode.OK, Json(
                new PlantItemGeometryVersionDto(geometryItemId, nextVersion),
                HttpStatusCode.OK));
        }

        // POST .../junctions
        // Qui il gestore finto e' PIU' PERMISSIVO dell'host vero - divergenza dichiarata, non una
        // svista: Wcs.Engine.Api.PlantQueries.AddJunctionAsync
        // (WCS/Wcs.Engine/src/Api/PlantQueries.cs, senza numero di riga: gia' spostata una volta
        // nello stesso commit che citava un numero) collassa sulla coppia (origine,
        // destinazione) e restituisce la giunzione gia' esistente - con lo stesso JunctionId della
        // prima POST - invece di crearne una seconda; questo gestore no, ne aggiunge
        // sempre una nuova (Guid.NewGuid() a ogni POST, sotto). E' voluto: DeleteItemCommandTest.ARetriedUndoSkipsWhatAnEarlierPartialAttemptAlreadyRestored
        // e i test analoghi vogliono dimostrare che la guardia LOCALE di DeleteItemCommand.UndoAsync
        // (non l'host, e nemmeno PlantGraph.AddJunction, che dedupe per
        // JunctionId ma qui ne riceverebbe uno diverso a ogni POST) e' quella che impedisce il
        // duplicato in un tentativo ripetuto - un gestore che collassasse per coppia come l'host
        // nasconderebbe quello che il test vuole provare, perche' la seconda POST sarebbe gia'
        // innocua da sola. Un test che volesse provare il comportamento dell'host su questo punto
        // dovrebbe farlo contro le suite di Wcs.Engine.Test/Wcs.Host.Test, non con questo gestore
        // finto.
        if (request.Method == HttpMethod.Post && segments is [.., "junctions"])
        {
            JunctionAdds++;
            var body = (await request.Content!.ReadFromJsonAsync<JunctionRequest>(ct))!;

            // CollapseJunctionsByPair (compito 8, spento di default - si veda il suo commento):
            // imita PlantQueries.AddJunctionAsync vero, che restituisce la giunzione gia' presente
            // sulla stessa coppia invece di crearne una seconda, ignorando la priorita' della
            // richiesta. Di default resta spento: e' il comportamento "sempre nuova" che
            // DeleteItemCommandTest si aspetta dal gestore finto.
            if (CollapseJunctionsByPair)
            {
                var existing = Junctions.FirstOrDefault(j =>
                    j.SourceItemId == body.SourceItemId && j.DestinationItemId == body.DestinationItemId);
                if (existing is not null) return (HttpStatusCode.Created, Json(existing));
            }

            var created = new JunctionDto(Guid.NewGuid(), body.SourceItemId, body.DestinationItemId, body.Priority);
            Junctions.Add(created);
            return (HttpStatusCode.Created, Json(created));
        }

        // PUT .../junctions/{id} - cambia solo la priorita' (l'host legge request.Priority e ignora
        // origine/destinazione del corpo, si veda PlantQueries.SetJunctionPriorityAsync: la stessa
        // scelta e' ripetuta qui).
        if (request.Method == HttpMethod.Put && segments is [.., "junctions", var priorityId] && Guid.TryParse(priorityId, out var priorityJunctionId))
        {
            JunctionPriorityPuts++;
            var body = (await request.Content!.ReadFromJsonAsync<JunctionRequest>(ct))!;
            var index = Junctions.FindIndex(j => j.JunctionId == priorityJunctionId);
            if (index < 0) return (HttpStatusCode.NotFound, null);
            Junctions[index] = Junctions[index] with { Priority = body.Priority };
            return (HttpStatusCode.NoContent, null);
        }

        // DELETE .../junctions/{id}
        if (request.Method == HttpMethod.Delete && segments is [.., "junctions", var junctionDeleteId] && Guid.TryParse(junctionDeleteId, out var junctionDeleteGuid))
        {
            JunctionDeletes++;
            var removed = Junctions.RemoveAll(j => j.JunctionId == junctionDeleteGuid) > 0;
            return removed ? (HttpStatusCode.NoContent, null) : (HttpStatusCode.NotFound, null);
        }

        // DELETE .../items/{id}: si porta via anche la mappa porte del dispositivo, come
        // PlantStore.DeleteItemAsync da quando DoorMapping.ItemId ha la sua chiave esterna.
        if (request.Method == HttpMethod.Delete && segments is [.., "items", var deleteId] && Guid.TryParse(deleteId, out var deleteItemId))
        {
            ItemDeletes++;
            var existed = Configs.Remove(deleteItemId);
            Geometries.Remove(deleteItemId);
            GeometryVersions.Remove(deleteItemId);
            Junctions.RemoveAll(j => j.SourceItemId == deleteItemId || j.DestinationItemId == deleteItemId);
            DoorMappings.RemoveAll(m => m.ItemId == deleteItemId);
            return existed ? (HttpStatusCode.NoContent, null) : (HttpStatusCode.NotFound, null);
        }

        // GET .../door-mappings (compito 11): l'intera tabella, come l'host vero - nessun filtro sull'ItemId qui, la rotta non ne prende uno.
        if (request.Method == HttpMethod.Get && segments is [.., "door-mappings"])
        {
            DoorMappingGets++;
            return (HttpStatusCode.OK, Json(DoorMappings));
        }

        // PUT .../door-mappings (compito 11): sostituisce, PER DISPOSITIVO PRESENTE NEL CORPO, come
        // PlantQueries.ReplaceDoorMappingsAsync (Wcs.Engine) - un dispositivo assente dal corpo resta
        // come sta, si veda il remarks di DoorMappingWrites. Un corpo vuoto e' quindi un no-op vero,
        // non un errore: nessun gruppo, nessuna sostituzione, per nessuno.
        if (request.Method == HttpMethod.Put && segments is [.., "door-mappings"])
        {
            DoorMappingPuts++;
            var body = (await request.Content!.ReadFromJsonAsync<DoorMappingDto[]>(ct))!;
            // 404 e nessuna mappa scritta se anche un solo dispositivo non c'e', come l'host: una
            // riga su un dispositivo assente e' una riga orfana, e la chiave esterna su
            // DoorMapping.ItemId la rifiuterebbe comunque.
            if (body.Select(m => m.ItemId).Distinct().Any(id => !Configs.ContainsKey(id))) return (HttpStatusCode.NotFound, null);
            foreach (var group in body.GroupBy(m => m.ItemId))
            {
                DoorMappings.RemoveAll(m => m.ItemId == group.Key);
                DoorMappings.AddRange(group);
            }
            return (HttpStatusCode.NoContent, null);
        }

        // POST .../door-mappings/link (compito 11): come PlantPersistence.LinkBufferCellAsync -
        // sostituisce TUTTE le righe del dispositivo con una riga sola, lato "A" fisso a prescindere
        // da quello che il corpo porta (l'host vero non legge nessun campo "Side" dalla richiesta).
        if (request.Method == HttpMethod.Post && segments is [.., "door-mappings", "link"])
        {
            DoorMappingLinks++;
            var link = (await request.Content!.ReadFromJsonAsync<DoorMappingLink>(ct))!;
            if (!Configs.ContainsKey(link.ItemId)) return (HttpStatusCode.NotFound, null);
            DoorMappings.RemoveAll(m => m.ItemId == link.ItemId);
            DoorMappings.Add(new DoorMappingDto(0, link.ItemId, link.DoorCode, link.CellId, "A", 0, false));
            return (HttpStatusCode.NoContent, null);
        }

        // DELETE .../door-mappings/{itemId} (compito 11): svuota davvero, nessuna riga per quell'item - a differenza di una PUT con corpo vuoto (si veda sopra).
        if (request.Method == HttpMethod.Delete && segments is [.., "door-mappings", var clearItemId] && Guid.TryParse(clearItemId, out var clearItemGuid))
        {
            DoorMappingDeletes++;
            if (!Configs.ContainsKey(clearItemGuid)) return (HttpStatusCode.NotFound, null);
            DoorMappings.RemoveAll(m => m.ItemId == clearItemGuid);
            return (HttpStatusCode.NoContent, null);
        }

        // GET .../automatic-cells (compito 11): sola lettura, nessun filtro sulla rotta.
        if (request.Method == HttpMethod.Get && segments is [.., "automatic-cells"])
        {
            AutomaticCellGets++;
            return (HttpStatusCode.OK, Json(AutomaticCells));
        }

        // GET .../wms/cells (compito 11): il gestore finto non rifiltra per areaType/automaticOnly (si
        // veda il commento di WmsCells) - torna sempre l'elenco impostato dal test, LastWmsCellsRequest
        // resta il modo per verificare che il client abbia chiesto i parametri giusti.
        if (request.Method == HttpMethod.Get && segments is [.., "wms", "cells"])
        {
            WmsCellGets++;
            LastWmsCellsRequest = request;
            return (HttpStatusCode.OK, Json(WmsCells));
        }

        // GET .../items/{id} (senza "/config": il dispositivo intero, come lo ricostruisce l'host da configurazione + geometria)
        if (request.Method == HttpMethod.Get && segments is [.., var getId] && Guid.TryParse(getId, out var getItemId))
        {
            ItemGets++;
            if (!Configs.TryGetValue(getItemId, out var config)) return (HttpStatusCode.NotFound, null);

            var geometry = Geometries.GetValueOrDefault(getItemId);
            if (!GeometryVersions.TryGetValue(getItemId, out var geometryVersion))
            {
                geometryVersion = Guid.NewGuid();
                GeometryVersions[getItemId] = geometryVersion;
            }
            var live = new ItemStatus(getItemId, config.Asi ?? "", config.Partition ?? "", config.Subitem ?? "",
                config.Name ?? "", default, default, string.Empty, 0, Guid.Empty, Guid.Empty);
            var dto = new PlantItemDto(getItemId, config.Name, (DeviceTypes)config.DeviceType, config.Asi, config.Partition,
                config.Subitem, (ConnectionTypes)config.ConnectionType, (MotionTypes)config.MotionType, config.LinkedCellId,
                config.RelocateItemId, config.DeviceConnectionId, config.GroupItemId, config.BelongingGroupId, config.GroupLevelId,
                live, geometry?.X, geometry?.Y, geometry?.Width, geometry?.Height, geometry?.Rotation)
            {
                GeometryVersion = geometryVersion
            };
            return (HttpStatusCode.OK, Json(dto));
        }

        return (HttpStatusCode.NotFound, null);
    }

    private static HttpResponseMessage Json<T>(T value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = JsonContent.Create(value) };
}
