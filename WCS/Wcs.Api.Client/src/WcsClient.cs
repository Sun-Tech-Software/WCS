using System.Net;
using System.Net.Http.Json;
using Wcs.Api.Contracts;
using Wcs.Api.Contracts.Dtos;
using static Wcs.Common.Enums;

namespace Wcs.Api.Client;

/// <summary>Il client HTTP dell'API del motore. Gli eventi stanno in <see cref="PlantEventsConnection"/>.</summary>
public sealed class WcsClient : IDisposable
{
    private readonly HttpClient _http;
    private string? _editHolder;

    /// <param name="options">Indirizzo, chiave e scadenza; <see cref="WcsClientOptions.BaseAddress"/> dev'essere un'origine senza percorso.</param>
    /// <param name="inner">
    /// Il gestore su cui appoggiare le richieste — nei test quello del <c>TestServer</c>. Attenzione:
    /// <see cref="Dispose"/> dispone l'<see cref="HttpClient"/> e con esso <b>anche questo gestore</b>,
    /// che quindi non va condiviso con altri client ne' riusato dopo.
    /// </param>
    public WcsClient(WcsClientOptions options, HttpMessageHandler? inner = null)
    {
        _http = new HttpClient(new ApiKeyHandler(options.ApiKey, new EditHolderHandler(() => _editHolder, inner)))
        {
            BaseAddress = new Uri(options.BaseAddress, ApiRoutes.Prefix.TrimStart('/') + "/"),
            Timeout = options.Timeout
        };
    }

    /// <summary>
    /// Il titolare del permesso di modifica per questa sessione: da quando e' impostato, ogni
    /// richiesta porta l'intestazione <see cref="ApiRoutes.EditHolderHeader"/> (compito 4b,
    /// estensione del permesso oltre le rotte del permesso stesso). Impostarlo <b>non chiede</b> il
    /// permesso - quello resta <see cref="AcquireEditLockAsync"/> - dice solo all'host chi sta
    /// scrivendo, cosi' le rotte che cambiano la forma dell'impianto possono confrontarlo con chi lo
    /// tiene davvero. Nullo (il valore predefinito): nessuna intestazione parte, come un client che
    /// non ha mai chiesto il permesso - e' cosi' che si comporta un client di prova o uno script
    /// senza editor.
    /// </summary>
    public string? EditHolder
    {
        get => _editHolder;
        set => _editHolder = value;
    }

    private static string R(string route) => route.TrimStart('/');

    public Task<EngineStatus> GetStatusAsync(CancellationToken ct = default) => GetAsync<EngineStatus>(R(ApiRoutes.Status), ct);

    public async Task<CommandDto> EnqueueCommandAsync(CommandRequest request, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync(R(ApiRoutes.Commands), request, ct);
        await EnsureAsync(response);
        return (await response.Content.ReadFromJsonAsync<CommandDto>(ct))!;
    }
    public Task<List<CommandDto>> GetCommandsAsync(short? status = null, string? workstationId = null, string? userId = null,
        DateTime? from = null, DateTime? to = null, Guid? itemId = null, short? showStatus = null, int? minType = null,
        int? maxType = null, string? excludeUserId = null, int take = 200, CancellationToken ct = default) =>
        GetAsync<List<CommandDto>>(R(ApiRoutes.Commands) + Query(("status", status), ("workstationId", workstationId), ("userId", userId), ("from", from), ("to", to),
            ("itemId", itemId), ("showStatus", showStatus), ("minType", minType), ("maxType", maxType), ("excludeUserId", excludeUserId), ("take", take)), ct);
    public Task<CommandDto?> GetCommandAsync(long id, CancellationToken ct = default) => GetOrNullAsync<CommandDto>($"{R(ApiRoutes.Commands)}/{id}", ct);
    public async Task<bool> UpdateCommandAsync(long id, CommandUpdate update, CancellationToken ct = default)
    {
        var response = await _http.PutAsJsonAsync($"{R(ApiRoutes.Commands)}/{id}", update, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        await EnsureAsync(response);
        return true;
    }

    public Task<DiagramDto?> GetDiagramAsync(CancellationToken ct = default) => GetOrNullAsync<DiagramDto>(R(ApiRoutes.Diagram), ct);
    public async Task SaveDiagramAsync(DiagramDto diagram, CancellationToken ct = default) => await EnsureAsync(await _http.PutAsJsonAsync(R(ApiRoutes.Diagram), diagram, ct));
    public Task<List<PlantItemDto>> GetItemsAsync(IEnumerable<DeviceTypes>? deviceTypes = null, CancellationToken ct = default) =>
        GetAsync<List<PlantItemDto>>(R(ApiRoutes.Items) + RepeatedQuery("deviceType", deviceTypes?.Select(t => (int)t)), ct);
    public Task<List<ItemStatus>> GetItemStatusesAsync(CancellationToken ct = default) => GetAsync<List<ItemStatus>>(R(ApiRoutes.Items) + "/status", ct);
    public Task<PlantItemDto?> GetItemAsync(Guid id, CancellationToken ct = default) => GetOrNullAsync<PlantItemDto>($"{R(ApiRoutes.Items)}/{id}", ct);
    /// <summary>
    /// Crea un dispositivo con l'identificativo che porta <paramref name="config"/>: e'
    /// <c>POST /plant/items</c>, la rotta con cui si crea <b>un</b> dispositivo.
    /// <see cref="SaveItemConfigurationAsync"/> non lo fa piu' - risponde <c>404</c> se il
    /// dispositivo non c'e'.
    /// </summary>
    /// <remarks>
    /// Non e' la sola rotta da cui un dispositivo puo' nascere: <see cref="ReplacePlantGraphAsync"/>
    /// (<c>PUT /plant/graph</c>, l'importazione dell'impianto intero) scrive le configurazioni con
    /// <c>PlantQueries.UpsertItemConfigurationAsync</c> e fa nascere i dispositivi che non ci sono.
    /// E' l'eccezione dichiarata, non una dimenticanza: sostituisce l'impianto, non lo modifica.
    /// <para>
    /// Solleva <see cref="WcsApiException"/> con <see cref="HttpStatusCode.Conflict"/> se quel
    /// dispositivo esiste gia' - <b>oppure</b> se il motore e' in Run, che sulla stessa rotta da' lo
    /// stesso codice (<c>PlantQueries.RequireNotRunning</c>, tradotto da
    /// <c>EngineExceptionHandler</c>). Nessuna delle due e' teorica: sono i due modi in cui fallisce
    /// l'annulla di una cancellazione, che le distingue rileggendo <see cref="GetStatusAsync"/>.
    /// </para>
    /// </remarks>
    public async Task CreateItemAsync(PlantItemConfigDto config, CancellationToken ct = default) =>
        await EnsureAsync(await _http.PostAsJsonAsync(R(ApiRoutes.Items), config, ct));
    public async Task DeleteItemAsync(Guid id, CancellationToken ct = default) => await EnsureAsync(await _http.DeleteAsync($"{R(ApiRoutes.Items)}/{id}", ct));
    public async Task KeepItemsAsync(IEnumerable<Guid> kept, CancellationToken ct = default) => await EnsureAsync(await _http.PostAsJsonAsync(R(ApiRoutes.Items) + "/keep", kept.ToArray(), ct));
    /// <summary>Svuota l'impianto: dispositivi, giunzioni, mappa porte, missioni e coda di estrazione. Rifiutato con <c>409</c> se il motore e' in Run.</summary>
    public async Task RemoveAllItemsAsync(CancellationToken ct = default) => await EnsureAsync(await _http.DeleteAsync(R(ApiRoutes.Items), ct));
    public Task<List<JunctionDto>> GetJunctionsAsync(CancellationToken ct = default) => GetAsync<List<JunctionDto>>(R(ApiRoutes.Junctions), ct);
    public async Task DeleteJunctionAsync(Guid id, CancellationToken ct = default) => await EnsureAsync(await _http.DeleteAsync($"{R(ApiRoutes.Junctions)}/{id}", ct));
    public Task<List<DoorMappingDto>> GetDoorMappingsAsync(CancellationToken ct = default) => GetAsync<List<DoorMappingDto>>(R(ApiRoutes.DoorMappings), ct);
    public async Task ReplaceDoorMappingsAsync(IEnumerable<DoorMappingDto> mappings, CancellationToken ct = default) => await EnsureAsync(await _http.PutAsJsonAsync(R(ApiRoutes.DoorMappings), mappings.ToArray(), ct));
    /// <summary>Svuota la mappa porte di un dispositivo: nessuna riga per quell'item, non una riga vuota.</summary>
    public async Task ClearDoorMappingsAsync(Guid itemId, CancellationToken ct = default) => await EnsureAsync(await _http.DeleteAsync($"{R(ApiRoutes.DoorMappings)}/{itemId}", ct));
    public async Task LinkBufferCellAsync(DoorMappingLink link, CancellationToken ct = default) => await EnsureAsync(await _http.PostAsJsonAsync(R(ApiRoutes.DoorMappings) + "/link", link, ct));
    public Task<List<AutomaticCell>> GetAutomaticCellsAsync(CancellationToken ct = default) => GetAsync<List<AutomaticCell>>(R(ApiRoutes.AutomaticCells), ct);

    /// <summary>La decorazione del disegno: il vuoto se l'impianto non ne ha mai avuta una - non un errore.</summary>
    public async Task<string> GetPlantDecorationAsync(CancellationToken ct = default) =>
        (await GetAsync<PlantDecorationDto>(R(ApiRoutes.PlantDecoration), ct)).Xml;

    /// <summary>Sostituisce la decorazione del disegno. Esige il permesso di modifica (423 senza), 409 se il motore e' in Run.</summary>
    public async Task SetPlantDecorationAsync(string xml, CancellationToken ct = default) =>
        await EnsureAsync(await _http.PutAsJsonAsync(R(ApiRoutes.PlantDecoration), new PlantDecorationDto(xml), ct));

    public Task<List<MissionDto>> GetMissionsAsync(decimal? loadUnitId = null, Guid? itemId = null, MissionTypes? type = null, CancellationToken ct = default) =>
        GetAsync<List<MissionDto>>(R(ApiRoutes.Missions) + Query(("udc", loadUnitId), ("itemId", itemId), ("type", type == null ? null : (int)type)), ct);
    public Task<MissionDto?> GetMissionAsync(Guid id, CancellationToken ct = default) => GetOrNullAsync<MissionDto>($"{R(ApiRoutes.Missions)}/{id}", ct);
    public Task<List<MissionStepDto>> GetMissionStepsAsync(Guid id, CancellationToken ct = default) => GetAsync<List<MissionStepDto>>($"{R(ApiRoutes.Missions)}/{id}/steps", ct);
    /// <summary>Gli step di tutte le missioni attive in una richiesta sola: e' quello che serve a chi ricostruisce la vista completa.</summary>
    public Task<List<MissionStepDto>> GetAllMissionStepsAsync(CancellationToken ct = default) => GetAsync<List<MissionStepDto>>(R(ApiRoutes.Missions) + "/steps", ct);
    /// <summary>C'e' una missione attiva per l'UDC? <paramref name="status"/> restringe agli stati dati (ripetuti nella query: <c>&amp;status=0&amp;status=1</c>).</summary>
    public Task<bool> MissionExistsForLoadUnitAsync(decimal loadUnitId, IEnumerable<MissionStatusTypes>? status = null, CancellationToken ct = default) =>
        GetAsync<bool>($"{R(ApiRoutes.Missions)}/exists" + Query(("udc", loadUnitId)) + AndRepeated("status", status?.Select(s => (int)s)), ct);

    /// <summary>
    /// Il motore ha gia' l'UDC? Domanda piu' larga di <see cref="MissionExistsForLoadUnitAsync"/>:
    /// missioni attive a qualunque stato, piu' le UDC che un dispositivo sta prelevando e quelle in
    /// coda di prelievo. E' l'insieme su cui il motore ferma poi il comando di estrazione.
    /// <paramref name="excludeAssignmentId"/> non conta la missione che porta gia' quell'assegnazione
    /// di nota.
    /// </summary>
    public Task<bool> MissionHoldsLoadUnitAsync(decimal loadUnitId, decimal? excludeAssignmentId = null, CancellationToken ct = default) =>
        GetAsync<bool>($"{R(ApiRoutes.Missions)}/holds" + Query(("udc", loadUnitId), ("excludeAssignmentId", excludeAssignmentId)), ct);

    /// <summary>
    /// Porta la missione sul dispositivo indicato (404 missione assente, 400 dispositivo assente, 409
    /// dispositivo gia' carico, 503 con il titolo <c>MissionMoveProblems.EngineRecoveringTitle</c> se il
    /// motore e' ancora in Recover, 503 con il campo <see cref="ServiceUnavailableProblems.ReasonExtension"/>
    /// a <see cref="ServiceUnavailableProblems.EngineNotLoaded"/> se il motore non e' caricato).
    /// </summary>
    /// <returns>
    /// <see cref="MissionChangeApplied.Now"/> (204), or <see cref="MissionChangeApplied.AtTheEndOfTheMovement"/>
    /// (202): a step of the mission is in flight, and the move waits for its end (item 54, decision 4).
    /// </returns>
    public async Task<MissionChangeApplied> SetMissionPositionAsync(Guid missionId, Guid itemId, CancellationToken ct = default) =>
        await ChangeAppliedAsync(await _http.PutAsJsonAsync($"{R(ApiRoutes.Missions)}/{missionId}/position", new MissionPositionUpdate(itemId), ct));

    /// <summary>
    /// Manda la missione verso il dispositivo indicato - un magazzino vuol dire una cella scelta dal
    /// gestionale - con la rotta ricalcolata subito, anche a motore fermo, e con la scelta sul gateway
    /// (<c>null</c>: tenere quello che c'e'). I rifiuti arrivano come <see cref="WcsApiException"/>,
    /// con un titolo per esito nel corpo (<see cref="MissionDestinationProblems"/>): 404 missione non
    /// attiva; 400 destinazione o gateway sconosciuti, o scelta sul gateway malformata; 409 passo in
    /// esecuzione (only the causes that are not the mission's own step in flight, which since item 54 answers
    /// 202), ricerca della cella o altro cambio in corso, UDC gia' li' (the defragmentation and the
    /// change of cell «not redirectable» are gone with item 54); 422 nessun percorso (con il gateway usato e se senza di lui il percorso c'e',
    /// nei campi del problema), nessuna cella da prenotare, o il gestionale che rifiuta il cambio cella
    /// di un'estrazione (<see cref="MissionDestinationProblems.WarehouseRefusedTitle"/>); 503 gestionale
    /// non raggiungibile, con il titolo <see cref="MissionDestinationProblems.WarehouseUnavailableTitle"/>. La stessa rotta puo'
    /// rispondere 503 anche con il motore non caricato o senza il suo database: quale dei due 503 della
    /// voce 34 sia lo dice il campo <see cref="ServiceUnavailableProblems.ReasonExtension"/>, non il titolo.
    /// </summary>
    /// <remarks>
    /// <b>Senza risposta l'esito non e' noto, e il cambio puo' essere avvenuto.</b> Se questo client
    /// smette di aspettare - la scadenza <see cref="WcsClientOptions.Timeout"/>, trenta secondi di
    /// serie, o <paramref name="ct"/> annullato - arriva una cancellazione (da <c>HttpClient</c> una
    /// <see cref="TaskCanceledException"/>), non una <see cref="WcsApiException"/>; ma l'host non
    /// passa al motore il gettone della richiesta, apposta (una prenotazione interrotta lascerebbe una
    /// cella appesa), e il motore finisce il suo lavoro anche senza nessuno ad aspettarlo: verso un
    /// magazzino, con la cella prenotata. Non serve un guasto perche' succeda. Verso un magazzino il
    /// motore chiama il gestionale piu' volte di seguito, ognuna entro <c>Wms:TimeoutSeconds</c>
    /// dell'host, trenta secondi di serie: un gestionale lento allunga il cambio oltre la scadenza del
    /// client. E con il gestionale raggiungibile ma fermo il 503 parte
    /// solo alla prima di quelle scadenze, quando il client con la scadenza di serie ha gia' smesso di
    /// aspettare: chi chiama vede la cancellazione, non il 503. Anche verso un dispositivo, se la
    /// missione andava in un magazzino, la prenotazione vecchia si restituisce dopo il cambio, e la
    /// risposta aspetta il gestionale. Chi chiama quindi rilegge la missione
    /// (<see cref="GetMissionAsync"/>) prima di proporre di riprovare, e non rimanda la stessa scelta
    /// alla cieca: verso un magazzino scegliere di nuovo vuol dire chiedere un'altra cella, e dopo un
    /// cambio avvenuto la missione la cambia, o il 422 dice che un'altra non c'e'.
    /// </remarks>
    /// <returns>
    /// <see cref="MissionChangeApplied.Now"/> (204), or <see cref="MissionChangeApplied.AtTheEndOfTheMovement"/>
    /// (202): a step of the mission is in flight, or being started, and the change waits for its end
    /// (item 54, decision 4); the mission's row shows it meanwhile (<see cref="MissionDto.PendingChange"/>).
    /// </returns>
    public async Task<MissionChangeApplied> SetMissionDestinationAsync(Guid missionId, Guid itemId, MissionGatewayUpdate? gateway = null, CancellationToken ct = default) =>
        await ChangeAppliedAsync(await _http.PutAsJsonAsync($"{R(ApiRoutes.Missions)}/{missionId}/destination", new MissionDestinationUpdate(itemId, gateway), ct));

    /// <summary>
    /// <see cref="SetMissionDestinationAsync(Guid, Guid, MissionGatewayUpdate?, CancellationToken)"/> with the
    /// whole body (item 54): what a relocator does with the load unit (<see cref="MissionDestinationUpdate.AtTheRelocator"/>),
    /// or a precise cell (<see cref="MissionDestinationUpdate.CellDoorCode"/>). The same answers, the same
    /// remarks; a refused cell is a <see cref="WcsApiException"/> with the 422 «Cella rifiutata».
    /// </summary>
    public async Task<MissionChangeApplied> SetMissionDestinationAsync(Guid missionId, MissionDestinationUpdate update, CancellationToken ct = default) =>
        await ChangeAppliedAsync(await _http.PutAsJsonAsync($"{R(ApiRoutes.Missions)}/{missionId}/destination", update, ct));

    /// <summary>
    /// The storage cells the operator can choose for the mission's load unit in a storage, a group of
    /// storages or among the cells of a relocator (item 54, decision 6): the first 20 the warehouse proposes.
    /// Nothing is reserved. A refusal is a <see cref="WcsApiException"/> (404 for a mission that is not active).
    /// </summary>
    public Task<List<StorageCellCandidateDto>> GetMissionStorageCellsAsync(Guid missionId, Guid itemId, CancellationToken ct = default) =>
        GetAsync<List<StorageCellCandidateDto>>($"{R(ApiRoutes.Missions)}/{missionId}/storage-cells" + Query(("itemId", itemId)), ct);

    /// <summary>The answer of a change of a mission: made now (204), waiting for the end of a step (202), or a refusal thrown.</summary>
    private static async Task<MissionChangeApplied> ChangeAppliedAsync(HttpResponseMessage response)
    {
        await EnsureAsync(response);
        return response.StatusCode == HttpStatusCode.Accepted ? MissionChangeApplied.AtTheEndOfTheMovement : MissionChangeApplied.Now;
    }

    public Task<List<ExtractionQueueItemDto>> GetExtractionQueueAsync(bool? processed = null, decimal? loadUnitId = null, int take = 500, CancellationToken ct = default) =>
        GetAsync<List<ExtractionQueueItemDto>>(R(ApiRoutes.ExtractionQueue) + Query(("processed", processed), ("udc", loadUnitId), ("take", take)), ct);
    public Task<ExtractionQueueItemDto?> GetExtractionQueueItemAsync(long id, CancellationToken ct = default) => GetOrNullAsync<ExtractionQueueItemDto>($"{R(ApiRoutes.ExtractionQueue)}/{id}", ct);
    /// <summary>
    /// La UDC e' in coda di estrazione? Con <paramref name="excludeRow"/> non conta la riga di
    /// estrazione corrente: «e' in un'altra estrazione?». <paramref name="excludeAssignmentId"/> fa
    /// la stessa domanda, indipendente, sullo spazio delle assegnazioni di nota (compito 14).
    /// </summary>
    public Task<bool> ExtractionQueueContainsLoadUnitAsync(decimal loadUnitId, decimal? excludeRow = null, decimal? excludeAssignmentId = null, CancellationToken ct = default) =>
        GetAsync<bool>($"{R(ApiRoutes.ExtractionQueue)}/contains" + Query(("udc", loadUnitId), ("excludeRow", excludeRow), ("excludeAssignmentId", excludeAssignmentId)), ct);
    public Task<bool> ExtractionQueueContainsCellAsync(decimal cellId, CancellationToken ct = default) => GetAsync<bool>($"{R(ApiRoutes.ExtractionQueue)}/contains" + Query(("cell", cellId)), ct);
    public Task<List<decimal>> GetExtractionQueueCellsAsync(CancellationToken ct = default) => GetAsync<List<decimal>>(R(ApiRoutes.ExtractionQueue) + "/cells", ct);
    public Task<List<decimal>> GetExtractionQueueLoadUnitsAsync(CancellationToken ct = default) => GetAsync<List<decimal>>(R(ApiRoutes.ExtractionQueue) + "/load-units", ct);
    public async Task<bool> SetExtractionPriorityAsync(long id, int priority, CancellationToken ct = default)
    {
        var response = await _http.PatchAsJsonAsync($"{R(ApiRoutes.ExtractionQueue)}/{id}/priority", new PriorityUpdate(priority), ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        await EnsureAsync(response);
        return true;
    }
    public async Task<CommandDto?> DeleteFromExtractionQueueAsync(long id, string? workstationId = null, string? userId = null, CancellationToken ct = default)
    {
        var response = await _http.DeleteAsync($"{R(ApiRoutes.ExtractionQueue)}/{id}" + Query(("workstationId", workstationId), ("userId", userId)), ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureAsync(response);
        return await response.Content.ReadFromJsonAsync<CommandDto>(ct);
    }

    public Task<List<HandledLoadUnitDto>> GetHandledLoadUnitsAsync(DateTime from, DateTime to, short? type = null, decimal? loadUnitId = null, decimal? processId = null, int take = 1000, CancellationToken ct = default) =>
        GetAsync<List<HandledLoadUnitDto>>(R(ApiRoutes.HandledLoadUnits) + Query(("from", from), ("to", to), ("type", type), ("udc", loadUnitId), ("process", processId), ("take", take)), ct);
    public Task<List<HandledLoadUnitSummary>> GetHandledLoadUnitSummaryAsync(DateTime from, DateTime to, SummaryPeriod period = SummaryPeriod.Day, CancellationToken ct = default) =>
        GetAsync<List<HandledLoadUnitSummary>>(R(ApiRoutes.HandledLoadUnits) + "/summary" + Query(("from", from), ("to", to), ("groupBy", period)), ct);
    public Task<bool> HandledLoadUnitExistsAsync(decimal? processId = null, decimal? loadUnitProcessId = null, decimal? noteHeaderId = null, CancellationToken ct = default) =>
        GetAsync<bool>(R(ApiRoutes.HandledLoadUnits) + "/exists" + Query(("process", processId), ("loadUnitProcess", loadUnitProcessId), ("noteHeader", noteHeaderId)), ct);
    public Task<List<LogEntryDto>> GetLogAsync(DateTime from, DateTime to, Guid? itemId = null, Guid? missionId = null, int take = 1000, CancellationToken ct = default) =>
        GetAsync<List<LogEntryDto>>(R(ApiRoutes.Log) + Query(("from", from), ("to", to), ("itemId", itemId), ("missionId", missionId), ("take", take)), ct);
    public Task<List<TransitTime>> GetTransitTimesAsync(DateTime from, DateTime to, string fromAsi, string toAsi, CancellationToken ct = default) =>
        GetAsync<List<TransitTime>>(R(ApiRoutes.Log) + "/transit-times" + Query(("from", from), ("to", to), ("fromAsi", fromAsi), ("toAsi", toAsi)), ct);
    public async Task AppendLogAsync(LogEntryRequest entry, CancellationToken ct = default) => await EnsureAsync(await _http.PostAsJsonAsync(R(ApiRoutes.Log), entry, ct));
    /// <summary>
    /// I messaggi scambiati con i PLC nell'intervallo. E' la lettura che <c>FrmPcmLog</c> del
    /// Client fa oggi con una <c>SELECT</c> su <c>PCM_LOG</c> del gestionale, tabella che nessuno
    /// scrive piu'.
    /// </summary>
    public Task<List<PlcMessageDto>> GetPlcMessagesAsync(DateTime from, DateTime to, int take = 1000, CancellationToken ct = default) =>
        GetAsync<List<PlcMessageDto>>(R(ApiRoutes.PlcMessages) + Query(("from", from), ("to", to), ("take", take)), ct);

    public Task<List<PlcConnectionDto>> GetPlcConnectionsAsync(CancellationToken ct = default) => GetAsync<List<PlcConnectionDto>>(R(ApiRoutes.PlcConnections), ct);
    /// <summary>Sostituisce l'elenco intero delle connessioni PLC. Esige il permesso di modifica (423 senza) e il motore fermo, Stop e non solo fuori da Run (409 altrimenti); non toglie una connessione che un dispositivo usa ancora (409, con le connessioni e i dispositivi nel campo <see cref="PlcConnectionProblems.InUseExtension"/>, compito 4c).</summary>
    public async Task ReplacePlcConnectionsAsync(IEnumerable<PlcConnectionDto> connections, CancellationToken ct = default) => await EnsureAsync(await _http.PutAsJsonAsync(R(ApiRoutes.PlcConnections), connections.ToArray(), ct));
    public Task<PlcConnectionStatus?> GetPlcConnectionStatusAsync(long id, CancellationToken ct = default) => GetOrNullAsync<PlcConnectionStatus>($"{R(ApiRoutes.PlcConnections)}/{id}/status", ct);

    public Task<List<PlantItemConfigDto>> GetItemConfigurationsAsync(CancellationToken ct = default) => GetAsync<List<PlantItemConfigDto>>(R(ApiRoutes.ItemsConfig), ct);
    public Task<PlantItemConfigDto?> GetItemConfigurationAsync(Guid id, CancellationToken ct = default) => GetOrNullAsync<PlantItemConfigDto>($"{R(ApiRoutes.Items)}/{id}/config", ct);
    /// <summary>
    /// Scrive la configurazione di un dispositivo che esiste. Solleva <see cref="WcsApiException"/>
    /// con <see cref="HttpStatusCode.NotFound"/> se non esiste: per crearlo c'e'
    /// <see cref="CreateItemAsync"/>.
    /// </summary>
    public async Task SaveItemConfigurationAsync(PlantItemConfigDto config, CancellationToken ct = default) => await EnsureAsync(await _http.PutAsJsonAsync($"{R(ApiRoutes.Items)}/{config.ItemId}/config", config, ct));
    /// <summary>
    /// Colloca un dispositivo nel disegno solo se la versione letta dal chiamante e' ancora
    /// corrente. Restituisce la nuova versione da usare per la scrittura successiva.
    /// </summary>
    public async Task<PlantItemGeometryVersionDto> SetItemGeometryAsync(
        PlantItemGeometryUpdateDto geometry,
        CancellationToken ct = default)
    {
        var response = await _http.PutAsJsonAsync(
            $"{R(ApiRoutes.Items)}/{geometry.ItemId}/geometry",
            geometry,
            ct);
        await EnsureGeometryWriteAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<PlantItemGeometryVersionDto>(ct))!;
    }

    /// <summary>
    /// Colloca un insieme di dispositivi in una chiamata sola. Se anche uno solo non esiste non
    /// viene spostato nessuno, e la risposta e' 404.
    /// </summary>
    public async Task<List<PlantItemGeometryVersionDto>> SetItemGeometriesAsync(
        IReadOnlyCollection<PlantItemGeometryUpdateDto> geometries,
        CancellationToken ct = default)
    {
        var response = await _http.PutAsJsonAsync(R(ApiRoutes.ItemsGeometry), geometries, ct);
        await EnsureGeometryWriteAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<List<PlantItemGeometryVersionDto>>(ct))!;
    }
    public async Task SetConnectionTypeForAllAsync(ConnectionTypes connectionType, CancellationToken ct = default) => await EnsureAsync(await _http.PostAsJsonAsync(R(ApiRoutes.Items) + "/connection-type", new ConnectionTypeUpdate(connectionType), ct));
    public async Task<JunctionDto> AddJunctionAsync(JunctionRequest request, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync(R(ApiRoutes.Junctions), request, ct);
        await EnsureAsync(response);
        return (await response.Content.ReadFromJsonAsync<JunctionDto>(ct))!;
    }
    public async Task<bool> SetJunctionPriorityAsync(Guid junctionId, JunctionRequest request, CancellationToken ct = default)
    {
        var response = await _http.PutAsJsonAsync($"{R(ApiRoutes.Junctions)}/{junctionId}", request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        await EnsureAsync(response);
        return true;
    }
    public Task<List<WmsCell>> GetWmsCellsAsync(short? areaType, bool automaticOnly, CancellationToken ct = default) =>
        GetAsync<List<WmsCell>>(R(ApiRoutes.WmsCells) + Query(("areaType", areaType), ("automaticOnly", automaticOnly)), ct);
    public Task<List<WmsConfigurationWarning>> GetWmsConfigurationCheckAsync(int mode, CancellationToken ct = default) =>
        GetAsync<List<WmsConfigurationWarning>>(R(ApiRoutes.WmsConfigurationCheck) + Query(("mode", mode)), ct);

    /// <summary>La fotografia per una ricerca celle del WMS: celle automatiche con blocco e stato del ricollocatore, celle e UDC in coda, in una richiesta sola.</summary>
    public Task<CellSearchContextDto> GetCellSearchContextAsync(CancellationToken ct = default) =>
        GetAsync<CellSearchContextDto>(R(ApiRoutes.WmsCellSearchContext), ct);

    /// <summary>I tipi di dispositivo che il motore dichiara, con forma e porte.</summary>
    public Task<List<DeviceTypeDto>> GetDeviceTypesAsync(CancellationToken ct = default) =>
        GetAsync<List<DeviceTypeDto>>(R(ApiRoutes.DeviceTypes), ct);

    /// <summary>L'impianto intero: dispositivi, geometria, giunzioni, mappature porte (compito 12).</summary>
    public Task<PlantGraphDto> GetPlantGraphAsync(CancellationToken ct = default) => GetAsync<PlantGraphDto>(R(ApiRoutes.PlantGraph), ct);

    /// <summary>Sostituisce l'impianto intero con quello dato (compito 12): esige il permesso di modifica, risponde 423 senza.</summary>
    public async Task ReplacePlantGraphAsync(PlantGraphDto graph, CancellationToken ct = default) =>
        await EnsureAsync(await _http.PutAsJsonAsync(R(ApiRoutes.PlantGraph), graph, ct));

    /// <summary>
    /// Legge la geometria di un vecchio disegno <c>.edd</c>/<c>.xml</c> caricato per l'importazione
    /// (compito 12): non scrive niente sull'impianto, prepara solo il verbale. <paramref name="content"/>
    /// viaggia cosi' com'e' - <c>StreamContent</c>, non un DTO JSON - perche' sull'impianto di prova
    /// il disegno pesa 29 MB e un giro per il JSON lo farebbe un terzo piu' pesante (base64) oltre a
    /// tenerlo in memoria due volte fra questo client e l'host.
    /// </summary>
    public async Task<LegacyGeometryImportDto> ReadLegacyGeometryAsync(Stream content, CancellationToken ct = default)
    {
        using var body = new StreamContent(content);
        body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        var response = await _http.PostAsync(R(ApiRoutes.PlantGraphLegacyGeometry), body, ct);
        await EnsureAsync(response);
        return (await response.Content.ReadFromJsonAsync<LegacyGeometryImportDto>(ct))!;
    }

    /// <summary>
    /// Chiede il permesso di modifica dell'impianto per <paramref name="holder"/>. La stessa rotta
    /// concede (nessun titolare, o uno scaduto) o rinnova (il titolare e' gia' lui): il risultato
    /// e' sempre 200 con lo stato del permesso, <c>IsMine</c> vero se e' andata. Non lancia se
    /// qualcun altro lo tiene (409): quello e' un esito legittimo, non un errore, e il chiamante lo
    /// legge da <see cref="EditLockDto.IsMine"/>.
    /// </summary>
    public async Task<EditLockDto> AcquireEditLockAsync(string holder, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync(R(ApiRoutes.EditLock), new EditLockRequest(holder), ct);
        if (response.StatusCode != HttpStatusCode.OK && response.StatusCode != HttpStatusCode.Conflict)
            await EnsureAsync(response);
        return (await response.Content.ReadFromJsonAsync<EditLockDto>(ct))!;
    }

    /// <summary>Rinnova il permesso: stessa rotta e stessa risposta di <see cref="AcquireEditLockAsync"/>, chiamata a intervalli regolari entro la durata concessa (la cadenza esatta e' decisa da chi chiama, non da questo client).</summary>
    public Task<EditLockDto> RenewEditLockAsync(string holder, CancellationToken ct = default) => AcquireEditLockAsync(holder, ct);

    /// <summary>Rilascia il permesso. Se <paramref name="holder"/> non e' il titolare l'host non fa niente: non serve saperlo qui per chiamarlo alla chiusura del circuito.</summary>
    public async Task ReleaseEditLockAsync(string holder, CancellationToken ct = default) =>
        await EnsureAsync(await _http.DeleteAsync(R(ApiRoutes.EditLock) + Query(("holder", holder)), ct));

    private async Task<T> GetAsync<T>(string url, CancellationToken ct)
    {
        var response = await _http.GetAsync(url, ct);
        await EnsureAsync(response);
        return (await response.Content.ReadFromJsonAsync<T>(ct))!;
    }

    private async Task<T?> GetOrNullAsync<T>(string url, CancellationToken ct) where T : class
    {
        var response = await _http.GetAsync(url, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureAsync(response);
        return await response.Content.ReadFromJsonAsync<T>(ct);
    }

    private static async Task EnsureAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync();
        throw new WcsApiException(response.StatusCode, body);
    }

    private static async Task EnsureGeometryWriteAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync(ct);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            try
            {
                var conflict = System.Text.Json.JsonSerializer.Deserialize<PlantItemGeometryConflictDto>(
                    body,
                    new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
                if (conflict?.ItemIds is { Count: > 0 } itemIds)
                    throw new GeometryConflictException(itemIds, body);
            }
            catch (System.Text.Json.JsonException)
            {
                // Un 409 non conforme al contratto della geometria resta un errore API generico.
            }
        }

        throw new WcsApiException(response.StatusCode, body);
    }

    private static string Query(params (string Name, object? Value)[] parameters)
    {
        var parts = parameters.Where(p => p.Value != null).Select(p => $"{p.Name}={Uri.EscapeDataString(Format(p.Value!))}").ToList();
        return parts.Count == 0 ? string.Empty : "?" + string.Join("&", parts);
    }

    /// <summary>Un parametro ripetuto una volta per valore (`?deviceType=2&amp;deviceType=6`): quello che l'array della query lega dal lato host.</summary>
    private static string RepeatedQuery(string name, IEnumerable<int>? values)
    {
        var list = values?.ToList();
        return list is { Count: > 0 } ? "?" + string.Join("&", list.Select(v => $"{name}={v}")) : string.Empty;
    }

    /// <summary>Un parametro ripetuto in coda a una query gia' aperta da <see cref="Query"/> (`&amp;status=0&amp;status=1`); vuoto se non ci sono valori.</summary>
    private static string AndRepeated(string name, IEnumerable<int>? values) =>
        values == null ? string.Empty : string.Concat(values.Select(v => $"&{name}={v}"));

    private static string Format(object value) => value switch
    {
        DateTime d => d.ToString("O"),
        bool b => b ? "true" : "false",
        Enum e => e.ToString(),
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

    public void Dispose() => _http.Dispose();
}

public class WcsApiException(HttpStatusCode statusCode, string body) : Exception($"L'API del motore ha risposto {(int)statusCode}: {body}")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string Body { get; } = body;
}

/// <summary>
/// La geometria e' cambiata dopo la lettura del chiamante. <see cref="ItemIds"/> contiene
/// esattamente i dispositivi in conflitto; il client non ritenta automaticamente.
/// </summary>
public sealed class GeometryConflictException(IReadOnlyList<Guid> itemIds, string body)
    : WcsApiException(HttpStatusCode.Conflict, body)
{
    public IReadOnlyList<Guid> ItemIds { get; } = itemIds;
}
