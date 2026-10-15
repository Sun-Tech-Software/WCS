using Wcs.Api.Contracts;
using Wcs.Api.Contracts.Dtos;
using Wcs.Engine.Api;
using Wcs.Engine.Persistence.Entities;
using Wcs.Host.Mapping;
using Wcs.Host.Security;
using static Wcs.Common.Enums;

namespace Wcs.Host.Endpoints;

/// <summary>Le rotte di <c>/plant/*</c>: geometria, configurazione, giunzioni, mappature porte, celle collegate.</summary>
/// <remarks>
/// <para>
/// <b>Il permesso di modifica, esteso oltre le rotte del permesso stesso (compito 4b).</b> Il
/// compito 4 aveva fermato <c>EditLock</c> alle sue due rotte (<c>POST</c>/<c>DELETE
/// /plant/edit-lock</c>); questa e' la decisione del proprietario del progetto che lo estende a
/// ogni scrittura di qui sotto che cambia la forma dell'impianto - geometria (singola e
/// d'insieme), creazione e configurazione di un dispositivo, modalita' di connessione per tutti, cancellazione
/// di un dispositivo, svuotamento dell'impianto, «tieni solo questi», le tre rotte delle giunzioni,
/// le due delle mappature porte, il collegamento celle, il disegno legacy pre-migrazione (ancora
/// scritto da <c>PlantGateway.SaveAsync</c> dell'Editor WinForms finche' la migrazione della
/// geometria non e' avvenuta), e da qui in poi la decorazione (compito 7): fa parte dell'impianto
/// come le altre. Le rotte marcate <see cref="EditPermissionEndpointExtensions.RequiresEditPermission"/>
/// sono quelle - non le letture, che restano libere per costruzione: elencare, leggere una
/// configurazione, il registro dei tipi, le celle del gestionale.
/// </para>
/// <para>
/// <b>La conseguenza voluta: l'Editor WinForms smette di poter salvare qualunque cosa.</b> Ogni sua
/// scrittura passa da <c>EditorSession.Client</c> (<c>Wcs.Editor/src/PlantGateway.cs</c>,
/// <c>DiagramManager.cs</c>, <c>FrmEDITOR.Private.cs</c>, <c>Model/PlantItemModel.cs</c>), e quel
/// client non chiede mai il permesso ne' sa mandare <see cref="ApiRoutes.EditHolderHeader"/>: da
/// oggi ogni sua scrittura su queste rotte trova <c>423 Locked</c>. Non e' un guasto: questo ramo
/// (<c>feature/editor-web</c>) non si unisce a <c>develop</c> finche' la 4b non e' finita, quindi
/// quell'Editor rotto non raggiunge nessun impianto - insegnargli a chiedere il permesso non e'
/// nello scopo di questo compito, e non lo tocchiamo.
/// </para>
/// </remarks>
internal static class PlantEndpoints
{
    public static RouteGroupBuilder MapPlant(this RouteGroupBuilder api)
    {
        api.MapGet(ApiRoutes.Diagram, async () =>
            await PlantQueries.DiagramAsync() is { } diagram ? Results.Ok(DtoMapping.ToDto(diagram)) : Results.NotFound())
            .WithName("GetDiagram");

        api.MapPut(ApiRoutes.Diagram, async (DiagramDto dto, HttpContext http) =>
        {
            // Dalla tappa 4a la geometria vive nel modello (colonne X, Y, Width, Height, Rotation di
            // PlantItems) e il disegno non si salva piu' da qui: il metodo su questa risorsa e'
            // ritirato, la GET resta perche' l'Editor WinForms deve ancora disegnare quello che c'e'.
            if (await PlantQueries.GeometryMigratedAsync())
            {
                // Il messaggio e' per chi lo legge davvero: l'operatore davanti al dialogo d'errore
                // dell'Editor WinForms dopo aver premuto Salva, non uno sviluppatore. Non conosce
                // rotte REST ne' ha il repository sotto mano - il riferimento tecnico, per chi
                // indaga, resta in coda e separato.
                // L'intestazione Allow elenca solo GET, il metodo davvero ammesso: PUT e' ritirato
                // secondo RFC 9110 §15.5.6.
                http.Response.Headers["Allow"] = HttpMethods.Get;
                http.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                http.Response.ContentType = "text/plain; charset=utf-8";
                await http.Response.WriteAsync(
                    "La posizione dei dispositivi ora e' custodita dal WCS: il disegno non si salva " +
                    "piu' da questo Editor. Il resto continua a funzionare normalmente - proprieta', " +
                    "giunzioni, comandi, liste - e per spostare i dispositivi si dovra' usare il " +
                    "nuovo Editor web. " +
                    "[Rif. tecnico: PUT /plant/diagram ritirata dalla tappa 4a, si veda WCS/doc/roadmap.md; " +
                    "la geometria si scrive ora con PUT /plant/items/{id}/geometry.]");
                return Results.Empty;
            }

            // Solo l'XML e' obbligatorio: il binario e' quello che SaveBinary di Syncfusion produce,
            // ed e' vuoto (sull'impianto di prova lo era gia' con il vecchio Editor); nessuno lo legge.
            if (dto.DiagramXml is not { Length: > 0 })
                return Results.Problem("DiagramXml e' obbligatorio", statusCode: StatusCodes.Status400BadRequest);
            await PlantQueries.SaveDiagramAndReloadAsync(dto.Diagram ?? [], dto.DiagramXml);
            return Results.NoContent();
        }).RequiresEditPermission().WithName("SaveDiagram");

        api.MapGet(ApiRoutes.Items, async (DeviceTypes[]? deviceType) =>
        {
            var geometries = await PlantQueries.ItemGeometriesAsync();
            return PlantQueries.Devices(deviceType)
                .Select(d => DtoMapping.ToDto(d, geometries.GetValueOrDefault(d.ItemGuid, VersionedItemGeometry.None)))
                .ToList();
        }).WithName("ListItems");
        api.MapGet(ApiRoutes.Items + "/status", () => PlantQueries.Devices().Select(DtoMapping.ToStatus).ToList()).WithName("ListItemStatuses");

        api.MapGet(ApiRoutes.DeviceTypes, () => DeviceTypeRegistry.All.Select(DtoMapping.ToDto).ToList())
            .WithName("ListDeviceTypes");

        // Segmenti letterali prima di {id:guid}: ASP.NET Core li preferisce comunque ("config" non è un Guid), ma così si legge meglio.
        api.MapGet(ApiRoutes.ItemsConfig, async () => (await PlantQueries.ItemConfigurationsAsync()).Select(DtoMapping.ToConfigDto).ToList()).WithName("ListItemConfigurations");
        api.MapGet(ApiRoutes.Items + "/{id:guid}/config", async (Guid id) =>
            await PlantQueries.ItemConfigurationAsync(id) is { } row ? Results.Ok(DtoMapping.ToConfigDto(row)) : Results.NotFound()).WithName("GetItemConfiguration");
        // POST /plant/items: la creazione ha una rotta sua. Fino a questo lotto non ce l'aveva - il
        // dispositivo nasceva dalla PUT qui sotto, che era un upsert, e la stessa richiesta voleva
        // dire «modifica» o «ricrea quello che qualcuno ha appena cancellato» a seconda di quello
        // che c'era sull'host in quel momento. L'identificativo continua a sceglierlo il client (i
        // due Editor fanno Guid.NewGuid() e lo tengono nella loro mappa di sessione): la rotta non
        // ne genera uno, dice solo se quello proposto era libero.
        api.MapPost(ApiRoutes.Items, async (PlantItemConfigDto config) =>
        {
            if (config.ItemId == Guid.Empty)
                return Results.Problem("L'ItemId del dispositivo da creare è obbligatorio", statusCode: StatusCodes.Status400BadRequest);
            if (!await PlantQueries.CreateItemConfigurationAsync(DtoMapping.ToEntity(config)))
                return Results.Problem($"Il dispositivo {config.ItemId} esiste già", statusCode: StatusCodes.Status409Conflict);
            return Results.Created($"{ApiRoutes.Prefix}{ApiRoutes.Items}/{config.ItemId}", config);
        }).RequiresEditPermission().WithName("CreateItem");

        api.MapPut(ApiRoutes.Items + "/{id:guid}/config", async (Guid id, PlantItemConfigDto config) =>
        {
            if (config.ItemId != id)
                return Results.Problem("L'ItemId del corpo non coincide con quello del percorso", statusCode: StatusCodes.Status400BadRequest);
            // 404, non piu' un upsert: modificare la configurazione di un dispositivo che non c'e'
            // piu' lo faceva rinascere, e chi scriveva non poteva accorgersene. Per crearlo c'e'
            // POST /plant/items, sopra.
            return await PlantQueries.UpdateItemConfigurationAsync(DtoMapping.ToEntity(config))
                ? Results.NoContent()
                : Results.Problem($"Il dispositivo {id} non esiste", statusCode: StatusCodes.Status404NotFound);
        }).RequiresEditPermission().WithName("SaveItemConfiguration");
        api.MapPut(ApiRoutes.Items + "/{id:guid}/geometry", async (Guid id, PlantItemGeometryUpdateDto dto) =>
        {
            // Come la gemella .../config: l'ItemId del corpo deve coincidere con quello della
            // rotta, non venire ignorato in silenzio a favore di quello della rotta.
            if (dto.ItemId != id)
                return Results.Problem("L'ItemId del corpo non coincide con quello del percorso", statusCode: StatusCodes.Status400BadRequest);
            if (dto.ExpectedGeometryVersion is not { } expectedVersion)
                return Results.Problem("ExpectedGeometryVersion è obbligatoria", statusCode: StatusCodes.Status400BadRequest);

            var outcome = await PlantQueries.SetItemGeometryAsync(new GeometryWrite(
                id, new ItemGeometry(dto.X, dto.Y, dto.Width, dto.Height, dto.Rotation), expectedVersion));
            return GeometryWriteResult(outcome, single: true);
        }).RequiresEditPermission()
            .Produces<PlantItemGeometryVersionDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .Produces<PlantItemGeometryConflictDto>(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status423Locked)
            .WithName("SetItemGeometry");

        api.MapPut(ApiRoutes.ItemsGeometry, async (PlantItemGeometryUpdateDto[] items) =>
        {
            if (items.Any(i => i.ExpectedGeometryVersion is null))
                return Results.Problem("ExpectedGeometryVersion è obbligatoria per ogni dispositivo",
                    statusCode: StatusCodes.Status400BadRequest);
            var duplicate = items.GroupBy(i => i.ItemId).FirstOrDefault(group => group.Count() > 1);
            if (duplicate is not null)
                return Results.Problem($"Il dispositivo {duplicate.Key} compare più di una volta",
                    statusCode: StatusCodes.Status400BadRequest);

            var outcome = await PlantQueries.SetItemGeometriesAsync(items.Select(i => new GeometryWrite(
                i.ItemId, new ItemGeometry(i.X, i.Y, i.Width, i.Height, i.Rotation),
                i.ExpectedGeometryVersion!.Value)).ToList());
            return GeometryWriteResult(outcome, single: false);
        }).RequiresEditPermission()
            .Produces<PlantItemGeometryVersionDto[]>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .Produces<PlantItemGeometryConflictDto>(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status423Locked)
            .WithName("SetItemGeometries");
        api.MapPost(ApiRoutes.Items + "/connection-type", async (ConnectionTypeUpdate update) =>
        {
            // Come la rotta gemella POST /commands. Senza questo controllo un corpo con un valore
            // fuori dall'enum si legava senza errore e finiva su OGNI dispositivo in memoria e su
            // OGNI riga di PlantItems: la successiva OpenConnection scriveva la riga del ramo
            // default («tipo di connessione non riconosciuto») per ciascuno e lasciava l'impianto
            // senza connessioni. Meglio rifiutare il corpo che scrivere una configurazione che
            // nessun dispositivo puo' usare.
            if (!Enum.IsDefined(typeof(ConnectionTypes), update.ConnectionType))
                return Results.Problem($"Modalità di connessione sconosciuta: {(int)update.ConnectionType}",
                    statusCode: StatusCodes.Status400BadRequest);
            await PlantQueries.SetConnectionTypeForAllAsync(update.ConnectionType);
            return Results.NoContent();
        }).RequiresEditPermission().WithName("SetConnectionTypeForAll");

        api.MapGet(ApiRoutes.Items + "/{id:guid}", async (Guid id) =>
        {
            if (PlantQueries.Device(id) is not { } device) return Results.NotFound();
            var geometries = await PlantQueries.ItemGeometriesAsync();
            return Results.Ok(DtoMapping.ToDto(device, geometries.GetValueOrDefault(id, VersionedItemGeometry.None)));
        }).WithName("GetItem");
        api.MapDelete(ApiRoutes.Items + "/{id:guid}", async (Guid id) =>
            await PlantQueries.RemoveItemAsync(id) ? Results.NoContent() : Results.NotFound()).RequiresEditPermission().WithName("DeleteItem");
        api.MapPost(ApiRoutes.Items + "/keep", async (List<Guid> kept) => { await PlantQueries.KeepItemsAsync(kept); return Results.NoContent(); }).RequiresEditPermission().WithName("KeepItems");
        // Svuota l'impianto: e' «Nuovo diagramma» dell'Editor. Il 409 in Run arriva dalla facciata.
        api.MapDelete(ApiRoutes.Items, async () => { await PlantQueries.RemoveAllItemsAsync(); return Results.NoContent(); }).RequiresEditPermission().WithName("DeleteAllItems");

        api.MapGet(ApiRoutes.Junctions, () => PlantQueries.Junctions().Select(DtoMapping.ToDto).ToList()).WithName("ListJunctions");
        api.MapPost(ApiRoutes.Junctions, async (JunctionRequest request) =>
        {
            // La giunzione e' un arco del grafo su cui gira la ricerca dei percorsi, e
            // l'invariante «gli estremi sono dispositivi esistenti» era difesa da un ingresso
            // solo: l'importatore interno (non pubblicato) scarta le giunzioni orfane e le conta nel rapporto, questa
            // rotta le accettava. Un Guid sbagliato dall'Editor restava nel database finche'
            // qualcuno non lo andava a cercare.
            if (request.SourceItemId == Guid.Empty || request.DestinationItemId == Guid.Empty)
                return Results.Problem("Gli estremi della giunzione sono obbligatori",
                    statusCode: StatusCodes.Status400BadRequest);
            if (request.SourceItemId == request.DestinationItemId)
                return Results.Problem("Una giunzione non può avere lo stesso dispositivo come origine e destinazione",
                    statusCode: StatusCodes.Status400BadRequest);
            if (!await PlantQueries.ItemExistsAsync(request.SourceItemId))
                return Results.Problem($"L'origine {request.SourceItemId} non è un dispositivo dell'impianto",
                    statusCode: StatusCodes.Status400BadRequest);
            if (!await PlantQueries.ItemExistsAsync(request.DestinationItemId))
                return Results.Problem($"La destinazione {request.DestinationItemId} non è un dispositivo dell'impianto",
                    statusCode: StatusCodes.Status400BadRequest);

            var junction = await PlantQueries.AddJunctionAsync(request.SourceItemId, request.DestinationItemId, request.Priority);
            return Results.Created($"{ApiRoutes.Prefix}{ApiRoutes.Junctions}/{junction.ItemGuid}", DtoMapping.ToDto(junction));
        }).RequiresEditPermission().WithName("AddJunction");
        api.MapPut(ApiRoutes.Junctions + "/{id:guid}", async (Guid id, JunctionRequest request) =>
            await PlantQueries.SetJunctionPriorityAsync(id, request.Priority) ? Results.NoContent() : Results.NotFound()).RequiresEditPermission().WithName("SetJunctionPriority");
        api.MapDelete(ApiRoutes.Junctions + "/{id:guid}", async (Guid id) =>
            await PlantQueries.RemoveJunctionAsync(id) ? Results.NoContent() : Results.NotFound()).RequiresEditPermission().WithName("DeleteJunction");

        api.MapGet(ApiRoutes.DoorMappings, async () => (await PlantQueries.DoorMappingsAsync()).Select(DtoMapping.ToDto).ToList()).WithName("ListDoorMappings");
        // Le tre rotte della mappa porte rispondono 404 sul dispositivo assente, come la PUT della
        // configurazione: prima rispondevano 204 e lasciavano righe orfane, e il collegamento di una
        // cella - che scrive la configurazione per PRIMA - faceva per di piu' rinascere il
        // dispositivo cancellato prima di scriverle.
        api.MapPut(ApiRoutes.DoorMappings, async (DoorMappingDto[] mappings) =>
        {
            var missing = await PlantQueries.ReplaceDoorMappingsAsync(mappings.Select(DtoMapping.ToEntity).ToList());

            // Nessuna mappa scritta se anche un solo dispositivo non esiste, e il corpo dice quali:
            // la stessa forma di PUT /plant/items/geometry, per la stessa ragione.
            return missing.Count == 0
                ? Results.NoContent()
                : Results.Problem($"Dispositivi non trovati: {string.Join(", ", missing)}",
                    statusCode: StatusCodes.Status404NotFound);
        }).RequiresEditPermission().WithName("ReplaceDoorMappings");
        api.MapPost(ApiRoutes.DoorMappings + "/link", async (DoorMappingLink link) =>
            await PlantQueries.LinkBufferCellAsync(link.ItemId, link.DoorCode, link.CellId)
                ? Results.NoContent()
                : Results.Problem($"Il dispositivo {link.ItemId} non esiste", statusCode: StatusCodes.Status404NotFound))
            .RequiresEditPermission().WithName("LinkBufferCell");
        api.MapDelete(ApiRoutes.DoorMappings + "/{itemId:guid}", async (Guid itemId) =>
            await PlantQueries.ClearDoorMappingsAsync(itemId)
                ? Results.NoContent()
                : Results.Problem($"Il dispositivo {itemId} non esiste", statusCode: StatusCodes.Status404NotFound))
            .RequiresEditPermission().WithName("ClearDoorMappings");

        api.MapGet(ApiRoutes.AutomaticCells, async () => (await PlantQueries.AutomaticCellsAsync()).Select(DtoMapping.ToDto).ToList()).WithName("ListAutomaticCells");

        // Sempre 200: un impianto senza decorazione risponde con il vuoto, non con un 404 - e' il
        // caso normale di ogni impianto appena creato, non un guasto (compito 7).
        api.MapGet(ApiRoutes.PlantDecoration, async () =>
            Results.Ok(new PlantDecorationDto(await PlantQueries.PlantDecorationAsync())))
            .WithName("GetPlantDecoration");
        api.MapPut(ApiRoutes.PlantDecoration, async (PlantDecorationDto dto) =>
        {
            await PlantQueries.SetPlantDecorationAsync(dto.Xml);
            return Results.NoContent();
        }).RequiresEditPermission().WithName("SetPlantDecoration");

        return api;
    }

    private static IResult GeometryWriteResult(GeometryWriteOutcome outcome, bool single)
    {
        if (outcome.MissingItemIds.Count > 0)
            return Results.Problem($"Dispositivi non trovati: {string.Join(", ", outcome.MissingItemIds)}",
                statusCode: StatusCodes.Status404NotFound);
        if (outcome.ConflictingItemIds.Count > 0)
            return Results.Conflict(new PlantItemGeometryConflictDto(outcome.ConflictingItemIds));

        var written = outcome.Written
            .Select(item => new PlantItemGeometryVersionDto(item.ItemId, item.GeometryVersion))
            .ToList();
        return single ? Results.Ok(written.Single()) : Results.Ok(written);
    }
}
