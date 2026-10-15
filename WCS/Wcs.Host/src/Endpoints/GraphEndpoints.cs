using Wcs.Api.Contracts;
using Wcs.Api.Contracts.Dtos;
using Wcs.Engine.Api;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Wcs.Host.Mapping;
using Wcs.Host.Security;

namespace Wcs.Host.Endpoints;

/// <summary>
/// Le rotte di <c>/plant/graph</c>: l'impianto intero, in lettura e in scrittura (compito 12,
/// esportazione e importazione).
/// </summary>
/// <remarks>
/// <para>
/// <b>Il permesso di modifica, come le altre rotte di scrittura di <c>/plant/*</c>.</b>
/// <c>PUT /plant/graph</c> cambia la forma dell'impianto e lo esige, con lo stesso cancello
/// (<see cref="EditPermissionEndpointExtensions.RequiresEditPermission"/>) di
/// <c>PlantEndpoints</c> — risponde <b>423 Locked</b> senza, non 409: in questo host 409 e' gia'
/// preso da «il motore non e' fermo» (<see cref="EngineBusyException"/>). <c>POST .../legacy-geometry</c>
/// invece <b>non</b> lo esige (dal 27/9): legge un file caricato e lo trasforma in geometrie, senza
/// toccare database ne' Navigator, quindi non c'e' niente che il permesso debba proteggere. Fino al
/// 27/9 lo esigeva «perche' fa parte dello stesso gesto di chi tiene il permesso»: valeva per il
/// vecchio <c>/editor</c>, che teneva il permesso per tutta la sessione; <c>/editor/drawio</c> lo
/// prende solo mentre scrive, e ogni scelta di un .edd/.xml tornava 423. La scrittura vera
/// dell'importazione, <c>PUT /plant/graph</c>, lo esige ancora. <c>GET /plant/graph</c> resta
/// libera, come ogni lettura.
/// </para>
/// <para>
/// <b>Il cancello di <c>EditPermissionGateTest</c>.</b> Le due rotte <c>POST</c>/<c>PUT</c> qui
/// sotto sono enumerate da quel test insieme a quelle di <c>PlantEndpoints</c>; la lettura del
/// vecchio disegno vi e' nominata come l'unica eccezione sotto <c>/plant/</c>, oltre alle rotte del
/// permesso stesso (<c>/plant/edit-lock</c>).
/// </para>
/// </remarks>
internal static class GraphEndpoints
{
    public static RouteGroupBuilder MapGraph(this RouteGroupBuilder api)
    {
        api.MapGet(ApiRoutes.PlantGraph, async () =>
        {
            // Un solo snapshot del database: un commit concorrente non può mescolare dispositivi
            // di una versione con connessioni PLC di un'altra e produrre un backup non importabile.
            var snapshot = await PlantQueries.ReadPlantBackupAsync();

            return Results.Ok(new PlantGraphDto(
                snapshot.Items.Select(DtoMapping.ToConfigDto).ToList(),
                snapshot.Items.Select(item => ToDto(item.ItemId,
                    snapshot.Geometries.GetValueOrDefault(item.ItemId, VersionedItemGeometry.None).Geometry)).ToList(),
                snapshot.Junctions.Select(DtoMapping.ToDto).ToList(),
                snapshot.DoorMappings.Select(DtoMapping.ToDto).ToList(),
                snapshot.Decoration,
                snapshot.PlcConnections.Select(DtoMapping.ToDto).ToList()));
        }).WithName("GetPlantGraph");

        // Sostituisce l'impianto intero: e' l'importazione (compito 12). PlantQueries.ReplaceGraphAsync
        // non conosce i DTO (il motore non li referenzia) - la conversione sta tutta qui, come per
        // ogni altra rotta di questo file che tocca il motore.
        api.MapPut(ApiRoutes.PlantGraph, async (PlantGraphDto graph) =>
        {
            // Le stesse invarianti che POST /plant/junctions gia' difende da un solo ingresso
            // (PlantEndpoints.cs), applicate al secondo ingresso che le poteva violare: identificativi
            // non vuoti e non ripetuti, nessun cappio, estremi delle giunzioni e ItemId di mappature e
            // geometrie noti fra gli Items proposti. Se il file porta le connessioni PLC, anche i
            // loro identificativi devono essere positivi e univoci e i dispositivi devono
            // riferirne una presente. L'engine serializza il commit atomico e la pubblicazione in
            // memoria; questa validazione fa rifiutare un file sbagliato prima di entrarvi.
            if (Validate(graph) is { } error)
                return Results.Problem(error, statusCode: StatusCodes.Status400BadRequest);

            // Gli archivi vecchi non avevano la chiave della giunzione: assegnarla qui, una sola
            // volta, evita che un retry della scrittura cambi il grafo richiesto.
            var requestedJunctions = graph.Junctions.Select(j =>
                (j.JunctionId == Guid.Empty ? Guid.NewGuid() : j.JunctionId,
                    j.SourceItemId, j.DestinationItemId, j.Priority)).ToList();

            await PlantQueries.ReplaceGraphAsync(
                graph.Items.Select(DtoMapping.ToEntity).ToList(),
                graph.Geometries.Select(g => (g.ItemId, new ItemGeometry(g.X, g.Y, g.Width, g.Height, g.Rotation))).ToList(),
                requestedJunctions,
                graph.DoorMappings.Select(DtoMapping.ToEntity).ToList(),
                // Passato com'e', nullo compreso: il nullo non e' un valore mancante da riempire con
                // qualcosa di ragionevole, e' l'unica differenza fra «il file non parla del disegno»
                // e «il file dice che il disegno e' vuoto». Riempirlo qui - con la decorazione di
                // adesso, per esempio - renderebbe le due cose indistinguibili a valle.
                graph.Decoration,
                graph.PlcConnections?.Select(DtoMapping.ToEntity).ToList());
            return Results.NoContent();
        }).RequiresEditPermission().WithName("ReplacePlantGraph");

        // Il vecchio disegno .edd/.xml (compito 12, punto 3): non scrive niente, legge il corpo grezzo
        // e restituisce quello che DiagramGeometryReader (tappa 4a) ha trovato. Corpo come parametro,
        // non un DTO con un byte[]: un DTO JSON obbligherebbe il client a mandare il file in base64
        // (un terzo in piu' di peso). Un giro per bufferizzare resta necessario (correzione Importante
        // 4 della revisione, versione corretta di un primo tentativo che passava il corpo grezzo di
        // Kestrel direttamente a XDocument.Load: quel tentativo si e' rotto in test veri con
        // InvalidOperationException, "Synchronous operations are disallowed" - Kestrel non permette
        // letture sincrone sul corpo della richiesta, e XDocument.Load(Stream) le fa), ma non due:
        // un solo MemoryStream, dimensionato da Content-Length quando c'e' invece di raddoppiare a
        // ogni crescita, letto una volta con CopyToAsync e passato per intero a
        // DiagramGeometryReader.Read(Stream) - nessun ToArray() che ne rifaccia una copia identica.
        api.MapPost(ApiRoutes.PlantGraphLegacyGeometry, async (HttpContext context) =>
        {
            var contentLength = context.Request.ContentLength;
            using var buffer = contentLength is > 0 and <= int.MaxValue ? new MemoryStream((int)contentLength.Value) : new MemoryStream();
            await context.Request.Body.CopyToAsync(buffer);
            buffer.Position = 0;

            var result = DiagramGeometryReader.Read(buffer);
            return Results.Ok(new LegacyGeometryImportDto(
                result.Geometries.Select(kv => ToDto(kv.Key, kv.Value)).ToList(),
                result.ElementsWithoutGuid, result.GuidsWithoutGeometry, result.PartiallyPlaced));
        })
        // No edit permit (27/9): the route parses the file in its body and touches neither the
        // database nor the Navigator (DiagramGeometryReader), so there is nothing for the permit to
        // guard. It used to share the import's permit, and on /editor/drawio - which takes the permit
        // only while it writes - that made the pick of every .edd/.xml come back 423. The import's
        // write, PUT /plant/graph above, still requires it. EditPermissionGateTest names this route
        // as the one exception under /plant/ besides the permit's own routes (/plant/edit-lock).
        .DoesNotRequireEditPermission("legge un vecchio disegno dal corpo della richiesta, non scrive niente")
        .WithName("ReadLegacyDiagramGeometry");

        return api;
    }

    private static PlantItemGeometryDto ToDto(Guid itemId, ItemGeometry g) => new(itemId, g.X, g.Y, g.Width, g.Height, g.Rotation);

    /// <summary>
    /// Le stesse invarianti del grafo che <c>POST /plant/junctions</c> difende (<c>PlantEndpoints.cs</c>),
    /// applicate a <c>PUT /plant/graph</c>: nullo se il grafo proposto e' accettabile, altrimenti il
    /// messaggio del 400. <c>PlantQueries.ReplaceGraphAsync</c> ripete per conto suo quasi tutte
    /// queste invarianti (identificativi vuoti o ripetuti, anche delle giunzioni; riferimenti a
    /// dispositivi assenti; connessioni PLC; dalla voce 5 le posizioni ripetute, dalla voce 17 le
    /// giunzioni ripetute fra gli stessi due dispositivi nello stesso verso - non il cappio, che
    /// resta solo qui) e solleva <c>ArgumentException</c>: e' la difesa del motore, non un
    /// messaggio per chi importa. Questi controlli stanno qui, sui DTO che il motore non conosce,
    /// perche' il file sbagliato sia rifiutato con un 400 che nomina la cosa, prima di arrivarci.
    /// </summary>
    private static string? Validate(PlantGraphDto graph)
    {
        var itemIds = new HashSet<Guid>();
        foreach (var item in graph.Items)
        {
            if (item.ItemId == Guid.Empty)
                return "Un dispositivo con identificativo vuoto non è ammesso";
            if (!itemIds.Add(item.ItemId))
                return $"L'identificativo {item.ItemId} compare più di una volta fra i dispositivi";
        }

        var junctionIds = new HashSet<Guid>();
        var joined = new HashSet<(Guid From, Guid To)>();
        foreach (var junction in graph.Junctions)
        {
            if (junction.JunctionId != Guid.Empty && !junctionIds.Add(junction.JunctionId))
                return $"La giunzione {junction.JunctionId} compare più di una volta";
            if (junction.SourceItemId == Guid.Empty || junction.DestinationItemId == Guid.Empty)
                return "Gli estremi di una giunzione sono obbligatori";
            if (junction.SourceItemId == junction.DestinationItemId)
                return "Una giunzione non può avere lo stesso dispositivo come origine e destinazione";
            if (!itemIds.Contains(junction.SourceItemId))
                return $"L'origine {junction.SourceItemId} non è un dispositivo dell'impianto proposto";
            if (!itemIds.Contains(junction.DestinationItemId))
                return $"La destinazione {junction.DestinationItemId} non è un dispositivo dell'impianto proposto";
            // Una giunzione per coppia ORDINATA (voce 17 della coda, decisione del proprietario del
            // 28/9): il motore riconosce una giunzione dalla coppia origine-destinazione e la ricerca
            // dei percorsi ne farebbe due passi identici; A->B e B->A restano due giunzioni.
            if (!joined.Add((junction.SourceItemId, junction.DestinationItemId)))
                return $"Due giunzioni collegano {Named(graph, junction.SourceItemId)} a {Named(graph, junction.DestinationItemId)} " +
                       "nello stesso verso: fra due dispositivi ce n'è una sola per verso";
        }

        foreach (var mapping in graph.DoorMappings)
            if (!itemIds.Contains(mapping.ItemId))
                return $"La mappatura porte su {mapping.ItemId} non è un dispositivo dell'impianto proposto";

        // Una posizione per dispositivo (voce 5 della coda dei lavori, 28/9): con due, il motore
        // teneva l'ultima senza dirlo. Fino ad allora le fermava solo la pagina di importazione.
        var placedItemIds = new HashSet<Guid>();
        foreach (var geometry in graph.Geometries)
        {
            if (!itemIds.Contains(geometry.ItemId))
                return $"La geometria di {geometry.ItemId} non è un dispositivo dell'impianto proposto";
            if (!placedItemIds.Add(geometry.ItemId))
                return $"Il dispositivo {geometry.ItemId} ha più di una posizione nel file";
        }

        if (graph.PlcConnections is { } plcConnections)
        {
            var plcIds = new HashSet<long>();
            foreach (var connection in plcConnections)
            {
                if (connection.Id <= 0)
                    return "Gli identificativi delle connessioni PLC devono essere maggiori di zero";
                if (!plcIds.Add(connection.Id))
                    return $"La connessione PLC {connection.Id} compare più di una volta";
            }

            foreach (var item in graph.Items)
            {
                if (item.DeviceConnectionId < 0 ||
                    item.DeviceConnectionId != decimal.Truncate(item.DeviceConnectionId) ||
                    item.DeviceConnectionId > long.MaxValue)
                    return $"Il dispositivo {item.ItemId} ha una connessione PLC non valida";
                var connectionId = (long)item.DeviceConnectionId;
                if (connectionId > 0 && !plcIds.Contains(connectionId))
                    return $"Il dispositivo {item.ItemId} riferisce la connessione PLC " +
                           $"{item.DeviceConnectionId}, assente dal backup";
            }
        }

        return null;
    }

    /// <summary>Un dispositivo del grafo proposto come lo nomina un messaggio: il nome con l'identificativo, o l'identificativo solo.</summary>
    private static string Named(PlantGraphDto graph, Guid itemId) =>
        graph.Items.FirstOrDefault(item => item.ItemId == itemId) is { Name: { Length: > 0 } name } ? $"{name} ({itemId})" : itemId.ToString();
}
