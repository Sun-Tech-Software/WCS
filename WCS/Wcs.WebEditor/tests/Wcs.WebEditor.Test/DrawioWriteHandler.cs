using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Io;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

/// <summary>
/// L'host finto per il salvataggio del disegno (compito 7b): risponde alle letture che
/// <c>DrawioEditor</c> fa all'apertura (impianto, decorazione, stato del motore), al permesso di
/// modifica, e registra le scritture che il salvataggio manda davvero.
/// </summary>
/// <remarks>
/// Non riusa <see cref="PlantMutationHandler"/> di proposito: quello serve ai gesti della tela SVG
/// e non conosce ne' le tre GET dell'elenco (items, junctions, device-types) ne' la decorazione ne'
/// il permesso, che qui sono il presupposto di ogni prova. Allargarlo avrebbe cambiato il gestore
/// sotto sei suite gia' verdi per servirne una nuova.
/// <para>
/// Quello che questo gestore fa e che un elenco di contatori non farebbe: <b>tiene traccia
/// dell'ordine</b> (<see cref="Writes"/>) e del corpo di ogni scrittura. Un salvataggio che scrive
/// le cose giuste nell'ordine sbagliato - una giunzione creata verso un dispositivo appena
/// cancellato - passerebbe un controllo fatto di soli conteggi.
/// </para>
/// </remarks>
public sealed class DrawioWriteHandler : HttpMessageHandler
{
    public List<PlantItemDto> Items { get; set; } = [];
    public List<JunctionDto> Junctions { get; set; } = [];
    public List<DeviceTypeDto> Types { get; set; } = [];
    public Dictionary<Guid, PlantItemConfigDto> Configs { get; } = [];

    /// <summary>La decorazione che l'host tiene: <see cref="DecorationWrites"/> raccoglie quelle che il salvataggio ci scrive sopra.</summary>
    public string Decoration { get; set; } = string.Empty;

    public NavigatorStatusTypes NavStatus { get; set; } = NavigatorStatusTypes.Stop;

    /// <summary>
    /// Every <c>POST /plant/items/connection-type</c> that went through (task 1, row 50), in
    /// order: refused ones (see <see cref="NavStatus"/> below) do not land here, like the real host's
    /// <c>PlantQueries.SetConnectionTypeForAllAsync</c> (<c>RequireNotRunning</c>, BEFORE the write).
    /// </summary>
    public List<ConnectionTypes> ConnectionTypeWrites { get; } = [];

    /// <summary>What <c>GET /plant/items/status</c> answers: the live status of every device.</summary>
    public List<ItemStatus> ItemStatuses { get; set; } = [];

    /// <summary>How many <c>GET /plant/items/status</c> went out: one per snapshot the live feed read.</summary>
    public int ItemStatusRequests => Volatile.Read(ref _itemStatusRequests);

    private int _itemStatusRequests;

    /// <summary>
    /// Holds the FIRST <c>GET /plant/items/status</c> until the test opens it, then disarms itself:
    /// the only way to see what the live feed does with the requests that arrive while a snapshot is
    /// in flight. Same one-shot shape as <see cref="DoorMappingReadGate"/>.
    /// </summary>
    public TaskCompletionSource? ItemStatusGate { get; set; }

    /// <summary>Completed when a snapshot really enters <see cref="ItemStatusGate"/>: see <see cref="ItemsGateReached"/>.</summary>
    public TaskCompletionSource ItemStatusGateReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Holds the FIRST <c>GET /status</c> (the engine's own status, not the items' one above) until
    /// the test opens it, then disarms itself - same one-shot shape as <see cref="ItemStatusGate"/>.
    /// The answer is captured from <see cref="NavStatus"/> BEFORE the hold and handed back frozen
    /// after the release: it is the only way to make a request that was already in flight answer
    /// with the value it had when it was ISSUED, even after the test changes <see cref="NavStatus"/>
    /// and raises a live event while the request sits at the gate - the race compito 26/9 measures.
    /// </summary>
    public TaskCompletionSource? EngineStatusGate { get; set; }

    /// <summary>Completed when an engine status read really enters <see cref="EngineStatusGate"/>: see <see cref="ItemsGateReached"/>.</summary>
    public TaskCompletionSource EngineStatusGateReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _engineStatusGateUsed;

    /// <summary>The active missions; <c>GET /missions?itemId=</c> filters them on PositionItemId, like the host.</summary>
    public List<MissionDto> Missions { get; set; } = [];

    public Dictionary<Guid, List<MissionStepDto>> MissionSteps { get; } = [];

    /// <summary>
    /// Holds every <c>GET /missions</c> until the test opens it: the only way to see what the hover
    /// route does with an answer that arrives after the mouse has moved on. Null for every test that
    /// does not set it.
    /// </summary>
    public TaskCompletionSource? MissionsGate { get; set; }

    /// <summary>
    /// The same on <c>GET /missions/{id}/steps</c>, the second read of the hover route: the mouse
    /// can move on between the two reads as well as during the first.
    /// </summary>
    public TaskCompletionSource? MissionStepsGate { get; set; }

    /// <summary>Completed when a steps read really enters <see cref="MissionStepsGate"/>: see <see cref="ItemsGateReached"/>.</summary>
    public TaskCompletionSource MissionStepsGateReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completed when a <c>GET /missions</c> really enters <see cref="MissionsGate"/>: see <see cref="ItemsGateReached"/>.</summary>
    public TaskCompletionSource MissionsGateReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Holds every <c>GET /missions/steps</c> (the steps of EVERY active mission, compito 3 row 61)
    /// while set: the window in which the selection moves to another device before the first one's
    /// steps arrive.
    /// </summary>
    public TaskCompletionSource? AllMissionStepsGate { get; set; }

    /// <summary>Completed when a read of all the steps really enters <see cref="AllMissionStepsGate"/>.</summary>
    public TaskCompletionSource AllMissionStepsGateReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// The missions whose step is in flight on the device executing it: since item 54 (decision 4)
    /// <c>PUT /missions/{id}/position</c> and <c>PUT /missions/{id}/destination</c> answer them 202, as
    /// <c>MissionGateway</c> does (<c>Pending</c>), and the change waits on the mission's row
    /// (<c>MissionDto.PendingChange</c>) instead of being made - a fact the DTOs
    /// do not carry, so the test says it here.
    /// </summary>
    public HashSet<Guid> MissionsWithAStepInFlight { get; } = [];

    /// <summary>
    /// The missions that moved while their change of destination was being made: <c>PUT
    /// /missions/{id}/destination</c> refuses them with the 409 that is left of <c>StepInFlight</c> after
    /// item 54 (the engine's <c>StillWhereTheChangeStarted</c>), in the place of the step in flight.
    /// </summary>
    public HashSet<Guid> MissionsMovedDuringTheChange { get; } = [];

    /// <summary>Every move that went through (<c>PUT /missions/{id}/position</c> answered 204 and moved something), in order.</summary>
    public List<(Guid MissionId, Guid ItemId)> MissionMoves { get; } = [];

    /// <summary>
    /// Thrown DIRECTLY on <c>PUT /missions/{id}/position</c> instead of answering - the same shape as
    /// <see cref="GraphPutFailure"/>: an exception of a kind none of the page's filters names by type.
    /// </summary>
    public Exception? MissionMoveFailure { get; set; }

    /// <summary>Holds every <c>PUT /missions/{id}/position</c> while set, after its checks and before it moves the mission.</summary>
    public TaskCompletionSource? MissionMoveGate { get; set; }

    /// <summary>Completed when a move really enters <see cref="MissionMoveGate"/>.</summary>
    public TaskCompletionSource MissionMoveGateReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Every <c>PUT /missions/{id}/destination</c> that arrived, with its body, in order - refused ones
    /// too: the request went out, and what it asked for the gateway is what a test of the choice reads.
    /// </summary>
    public List<(Guid MissionId, MissionDestinationUpdate Update)> MissionDestinationRequests { get; } = [];

    /// <summary>
    /// The missions for which a search of the storage cell, or another change, is under way:
    /// <c>PUT /missions/{id}/destination</c> refuses them with 409 (<c>CellSearchInFlight</c>) - a fact
    /// of the engine the DTOs do not carry, so the test says it here.
    /// </summary>
    public HashSet<Guid> MissionsWithACellSearchInFlight { get; } = [];

    /// <summary>
    /// The missions another one of whose group copied the door and cannot be stopped right now:
    /// <c>PUT /missions/{id}/destination</c> refuses them with 409 (<c>CopyInFlight</c>, owner's answer
    /// 4 of 9/10) - a fact of the engine the DTOs do not carry, so the test says it here.
    /// </summary>
    public HashSet<Guid> MissionsWithACopyInFlight { get; } = [];

    /// <summary>The warehouses (Storage devices) in which the warehouse system has no free cell for the load unit: 422 <c>NoStorageCell</c>.</summary>
    public HashSet<Guid> StoragesWithoutAFreeCell { get; } = [];

    /// <summary>The warehouse system does not answer: a change towards a warehouse is refused with 503.</summary>
    public bool WarehouseUnreachable { get; set; }

    /// <summary>
    /// The warehouse system stops answering AFTER the reservation has let go of the cell the mission
    /// held: a change towards a warehouse is refused with 503 and <c>lostTheCell</c>, and the mission
    /// loses its storage door, as the engine's LoseTheCell does (the destination stays).
    /// </summary>
    public bool WarehouseUnreachableAfterReserving { get; set; }

    /// <summary>
    /// An extraction still in its cell sent to another warehouse is a change of cell, and the engine
    /// tells the warehouse system that it leaves its order BEFORE it reserves (task 4 of voce 39):
    /// with this set, a warehouse of <see cref="StoragesWithoutAFreeCell"/> refuses such a change only
    /// at the reservation, after those reports, and the engine closes the mission - it is gone from the
    /// active ones. The host's refusal says so, with the field <c>missionClosed</c> and its words. Only a
    /// mission with an order to leave (an assignment, or a row of the extraction queue) is reported, and
    /// so closed.
    /// </summary>
    public bool ChangeOfCellRefusedAfterTheReports { get; set; }

    /// <summary>
    /// The warehouse system says no (a 4xx) to the first report of a change of cell - an extraction
    /// still in its cell sent to a warehouse: the change is refused with 422 <c>WarehouseRefused</c>, and
    /// the mission stays the extraction it was (owner's answer 4 of 8/10, evening).
    /// </summary>
    public bool WarehouseRefusesTheChangeOfCell { get; set; }

    /// <summary>Thrown DIRECTLY on <c>PUT /missions/{id}/destination</c> instead of answering: see <see cref="MissionMoveFailure"/>.</summary>
    public Exception? MissionDestinationFailure { get; set; }

    /// <summary>Holds every <c>PUT /missions/{id}/destination</c> while set, after its checks and before it changes the mission.</summary>
    public TaskCompletionSource? MissionDestinationGate { get; set; }

    /// <summary>Completed when a change of destination really enters <see cref="MissionDestinationGate"/>.</summary>
    public TaskCompletionSource MissionDestinationGateReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Holds every <c>PUT /missions/{id}/destination</c> while set, as it arrives - recorded, and BEFORE
    /// any check: whatever the answer is, a refusal too, it waits. The engine's refusals can come late:
    /// towards a warehouse it asks the warehouse system for a cell first, and only then finds that the
    /// gateway blocks the way to the door it proposed (MissionGateway.ReserveACellAndCommitAsync).
    /// </summary>
    public TaskCompletionSource? MissionDestinationArrivalGate { get; set; }

    /// <summary>Completed when a change of destination really enters <see cref="MissionDestinationArrivalGate"/>.</summary>
    public TaskCompletionSource MissionDestinationArrivalGateReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Every request, as <c>"METHOD /path"</c>, in arrival order. A concurrent queue, not a list: on
    /// a page test the live feed reads its snapshot from its own threads while the page reads too.
    /// </summary>
    public ConcurrentQueue<string> Requests { get; } = new();

    /// <summary>Every <c>POST /commands</c>, in order.</summary>
    public List<CommandRequest> Commands { get; } = [];

    /// <summary>
    /// What <c>GET /commands/{id}</c> answers, by id: a command not in here is answered as the POST
    /// recorded it, still to do (status 0) - or 404 if no POST made it.
    /// </summary>
    public ConcurrentDictionary<long, CommandDto> CommandReads { get; } = new();

    /// <summary>When set, <c>POST /commands</c> answers this status instead of accepting.</summary>
    public HttpStatusCode? CommandRefusal { get; set; }

    /// <summary>
    /// Holds a <c>POST /commands</c> after it is recorded in <see cref="Commands"/> (so its id is
    /// already fixed) and before the answer goes out: the window in which a live CommandChanged for
    /// that same id can race ahead of the POST's own response. Null for every test that does not set
    /// it - same one-shot-less shape as <see cref="GeometryGate"/>, held for every request while set.
    /// </summary>
    public TaskCompletionSource? CommandPostGate { get; set; }

    /// <summary>Completed when a command POST really enters <see cref="CommandPostGate"/>: see <see cref="ItemsGateReached"/>.</summary>
    public TaskCompletionSource CommandPostGateReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Se il permesso di modifica viene concesso a chi lo chiede. Falso: risponde 409 con un titolare diverso, come l'host vero.</summary>
    public bool GrantEditLock { get; set; } = true;

    /// <summary>
    /// Quante richieste del permesso vengono concesse prima che l'host cominci a rispondere
    /// "e' di un altro": nullo vuol dire sempre. Serve a provare la finestra vera della concessione -
    /// dura 60 secondi, e un piano fatto di molte chiamate da 30 secondi di timeout ciascuna puo'
    /// superarla a meta' strada.
    /// </summary>
    public int? RevokeEditLockAfter { get; set; }

    /// <summary>Quante volte il permesso e' stato chiesto o rinnovato: una sola chiamata per tutto un salvataggio vuol dire nessun rinnovo durante il piano.</summary>
    public int EditLockRequests { get; private set; }

    public DateTime LockSince { get; set; } = new(2026, 9, 15, 14, 32, 10);

    /// <summary>Ogni scrittura arrivata, nell'ordine: "item-post", "geometry", "junction-post", "junction-delete", "item-delete", "decoration", "graph".</summary>
    public List<string> Writes { get; } = [];

    /// <summary>
    /// Gli impianti interi arrivati con <c>PUT /plant/graph</c>, nell'ordine: e' l'unica scrittura
    /// che <c>ImportExport</c> manda (righe 43-44 della tabella di parita'), e un elenco invece di
    /// un contatore perche' qui il corpo e' proprio la cosa che conta - il quinto campo, la
    /// decorazione, dice se quel file parlava o no del disegno.
    /// </summary>
    public List<PlantGraphDto> GraphPuts { get; } = [];

    /// <summary>
    /// Il cancello sulla <c>PUT /plant/graph</c>: tiene ferma un'importazione dentro la sua
    /// scrittura, com'e' l'unico modo di vedere che cosa rispondono gli altri quattro scrittori
    /// della pagina mentre lei ha in mano l'impianto.
    /// </summary>
    public TaskCompletionSource? GraphPutGate { get; set; }

    /// <summary>Completato quando un'importazione entra davvero in <see cref="GraphPutGate"/>.</summary>
    public TaskCompletionSource GraphPutGateReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Solleva DIRETTAMENTE questa eccezione su <c>PUT /plant/graph</c>, invece di rispondere -
    /// stesso schema di <see cref="SingleItemFailure"/>. Serve a un genere di guasto che
    /// <see cref="Failed(string)"/> non sa produrre: non un rifiuto HTTP (<see cref="FailRoute"/>),
    /// non un timeout (<see cref="TimeoutRoute"/>), non un guasto del trasporto
    /// (<see cref="HttpFailureRoute"/>) - <c>WcsClient</c> traduce tutti e tre in
    /// <c>WcsApiException</c>/<c>TaskCanceledException</c>/<c>HttpRequestException</c>, i tre generi
    /// che sia <c>ImportExport.ConfirmImportAsync</c> sia <c>DrawioEditor.ImportThroughTheGateAsync</c>
    /// riconoscono per nome. Qui invece l'eccezione arriva com'e' - un bug nel percorso della
    /// scrittura stessa, di un genere che nessuno dei due filtri nomina - ed e' quello che la prova
    /// sul "non dimentica niente" deve poter fabbricare.
    /// </summary>
    public Exception? GraphPutFailure { get; set; }

    public List<PlantItemGeometryDto> GeometryWrites { get; } = [];
    public IReadOnlyList<Guid>? GeometryConflictItemIds { get; set; }

    /// <summary>
    /// The single geometry PUT writes, and then confirms it with an empty version - an answer the page
    /// does not take (<c>GeometryWriter.ApplyConfirmation</c>): the device is on the host in full all
    /// the same.
    /// </summary>
    public bool ConfirmGeometryWithAnEmptyVersion { get; set; }

    /// <summary>
    /// Le configurazioni arrivate con una PUT, nell'ordine: il lotto C del pannello delle proprieta'
    /// scrive di qui. Come l'host vero (<c>PlantQueries.UpdateItemConfigurationAsync</c>) e' una
    /// <b>modifica</b>, non piu' un upsert: su un dispositivo che non c'e' risponde <c>404</c> e non
    /// scrive niente - ed e' un fatto che le prove devono poter vedere, non un dettaglio del finto.
    /// Chi crea usa <c>POST /plant/items</c>, che qui sotto risponde <c>409</c> se l'identificativo
    /// e' gia' preso.
    /// </summary>
    public List<PlantItemConfigDto> ConfigWrites { get; } = [];
    /// <summary>I corpi arrivati a <c>POST /plant/items</c>, nell'ordine - anche quelli rifiutati con 409: la richiesta e' partita, e una prova deve poterlo vedere.</summary>
    public List<PlantItemConfigDto> ItemCreations { get; } = [];

    public List<JunctionRequest> JunctionPosts { get; } = [];

    /// <summary>
    /// Le priorita' arrivate con una PUT su una giunzione che esiste gia' (riga 20), nell'ordine:
    /// quale giunzione e con che numero. Un contatore non basterebbe - "e' partita una PUT" non dice
    /// se sia partita sulla freccia giusta ne' con il verso giusto.
    /// </summary>
    public List<(Guid JunctionId, int Priority)> JunctionPriorityWrites { get; } = [];

    public List<Guid> JunctionDeletes { get; } = [];
    public List<Guid> ItemDeletes { get; } = [];
    public List<string> DecorationWrites { get; } = [];

    /// <summary>
    /// The door map of the whole plant, as the host holds it: the read route takes no device and
    /// hands back all of them (<c>PlantQueries.DoorMappingsAsync</c>); filtering by device is the
    /// caller's work.
    /// </summary>
    public List<DoorMappingDto> DoorMappings { get; } = [];

    /// <summary>The warehouse cells <c>GET /wms/cells</c> hands back. Empty: a real answer and not a failure - a plant may have no candidate cells.</summary>
    public List<WmsCell> WmsCells { get; } = [];

    /// <summary>
    /// Item 54, decision 6: the cells <c>GET /missions/{id}/storage-cells?itemId=</c> proposes, by the
    /// device asked about (a warehouse, a group of warehouses or a relocator); one that is none of them
    /// is refused as the host does, and one of them that is not here has no cell to propose.
    /// </summary>
    public Dictionary<Guid, List<StorageCellCandidateDto>> StorageCells { get; } = [];

    /// <summary>Every <c>GET /missions/{id}/storage-cells</c> that arrived: the mission and the device asked about.</summary>
    public List<(Guid MissionId, Guid ItemId)> StorageCellsRequests { get; } = [];

    /// <summary>Holds every <c>GET /missions/{id}/storage-cells</c> while set, after it is recorded.</summary>
    public TaskCompletionSource? StorageCellsGate { get; set; }

    /// <summary>Completed when a read of the cells really enters <see cref="StorageCellsGate"/>.</summary>
    public TaskCompletionSource StorageCellsGateReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Item 54, decision 6: the precise cells the warehouse system refuses when they are reserved, by
    /// their door code, with the refusal's name on the wire (<c>cellNotAvailable</c>,
    /// <c>unknownLoadUnit</c>) - a fact of the warehouse the DTOs do not carry.
    /// </summary>
    public Dictionary<string, string> CellsTheWarehouseRefuses { get; } = [];

    /// <summary>
    /// What <c>GET /wms/cells</c> answers <b>to the request's two filters</b> (<c>areaType</c>, null
    /// when it is missing, and <c>automaticOnly</c>), for a test that needs different lists to come
    /// back to different questions. Null: it answers <see cref="WmsCells"/> to everybody.
    /// <para>
    /// This is not a convenience: the filtering is the warehouse's work and not the WCS's, and
    /// without this lever two devices of different types get the SAME list - which makes a late
    /// answer landing in the wrong panel invisible. MEASURED: the first version of the race test
    /// stayed green with the generation check taken out, for exactly that reason.
    /// </para>
    /// </summary>
    public Func<short?, bool, List<WmsCell>>? WmsCellsFor { get; set; }

    /// <summary>The automatic cells <c>GET /plant/automatic-cells</c> hands back.</summary>
    public List<AutomaticCell> AutomaticCells { get; } = [];

    /// <summary>
    /// What <c>GET /wms/configuration-check</c> hands back: the rows the WMS procedure returns.
    /// Empty is a real answer - the configuration is fine - and not a failure.
    /// </summary>
    public List<WmsConfigurationWarning> WmsConfigurationWarnings { get; } = [];

    /// <summary>The path and query of every <c>GET /wms/configuration-check</c> that went out, in order: the only way to prove that the button really asked the host and with which <c>mode</c>.</summary>
    public List<string> WmsConfigurationCheckRequests { get; } = [];

    /// <summary>
    /// A gate on <c>GET /wms/configuration-check</c>: null for every test that does not set it. It
    /// is the window in which the check is really in flight - what the page uses to say it is
    /// working instead of looking dead.
    /// </summary>
    public TaskCompletionSource? WmsConfigurationCheckGate { get; set; }

    /// <summary>Completed when the configuration check really enters <see cref="WmsConfigurationCheckGate"/>: see <see cref="ItemsGateReached"/>.</summary>
    public TaskCompletionSource WmsConfigurationCheckGateReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The door-map writes that arrived, in order and with their body: <c>"door-put"</c>, <c>"door-delete"</c> and <c>"door-link"</c> are in <see cref="Writes"/> as well.</summary>
    public List<DoorMappingDto[]> DoorMappingWrites { get; } = [];

    /// <summary>The unmappings that arrived (<c>DELETE /plant/door-mappings/{itemId}</c>), in order.</summary>
    public List<Guid> DoorMappingClears { get; } = [];

    /// <summary>The links that arrived (<c>POST /plant/door-mappings/link</c>), in order.</summary>
    public List<DoorMappingLink> CellLinks { get; } = [];

    /// <summary>
    /// How many READS of the three cell routes went out. They measure what a collection of writes
    /// cannot say: that for a device with no cells nothing is asked of the warehouse at all, and that
    /// the automatic cells are not read for a Storage device, whose panel has nowhere to put them.
    /// </summary>
    public int DoorMappingReads { get; private set; }

    public int WmsCellReads { get; private set; }

    public int AutomaticCellReads { get; private set; }

    /// <summary>
    /// The same gate as the others, on the GET of the warehouse cells: null for every test that does
    /// not set it. It is the late answer's window - one device's cells read still out on the wire
    /// while the selection has already moved to another.
    /// </summary>
    public TaskCompletionSource? WmsCellsGate { get; set; }

    /// <summary>Completed when a GET of the cells really enters <see cref="WmsCellsGate"/>: see <see cref="ItemsGateReached"/>.</summary>
    public TaskCompletionSource WmsCellsGateReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// A gate on the READ of the door map (<c>GET /plant/door-mappings</c>), and it holds the FIRST
    /// read that meets it and no other - the same one-shot shape as <see cref="EditLockGate"/>, for
    /// the same reason.
    /// <para>
    /// The window it opens is the reread the page does AFTER a cells write, which is a different
    /// race from <see cref="WmsCellsGate"/>'s: there the selection overtakes the panel's first read,
    /// here it overtakes an answer that is coming back to a panel already full. One-shot because the
    /// new selection reads the same route, and a gate that held that one too would hang the test
    /// that armed it.
    /// </para>
    /// </summary>
    public TaskCompletionSource? DoorMappingReadGate { get; set; }

    /// <summary>Completed when a read of the door map really enters <see cref="DoorMappingReadGate"/>: see <see cref="ItemsGateReached"/>.</summary>
    public TaskCompletionSource DoorMappingReadGateReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// The same gate on the PUT of the door map, that is <b>inside</b> a cells write already started
    /// and not yet finished: it is for whoever measures the overlap between that gesture and the
    /// others that write to the plant from this page.
    /// </summary>
    public TaskCompletionSource? DoorMappingGate { get; set; }

    /// <summary>Completed when a PUT of the door map really enters <see cref="DoorMappingGate"/>: see <see cref="ItemsGateReached"/>.</summary>
    public TaskCompletionSource DoorMappingGateReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>La rotta su cui la prossima richiesta fallisce con <see cref="FailureStatus"/>, e basta una volta: serve a provare che una scrittura fallita FERMI il salvataggio.</summary>
    public string? FailRoute { get; set; }

    public HttpStatusCode FailureStatus { get; set; } = HttpStatusCode.InternalServerError;

    /// <summary>The body of the answer <see cref="FailRoute"/> gives: empty unless a test hands it the host's own words (a problem document, say).</summary>
    public string FailureBody { get; set; } = string.Empty;

    /// <summary>
    /// When set, what <c>GET /plant/items/config</c> answers VERBATIM instead of one row per device of
    /// <see cref="Items"/>: the way to stage an answer the real host should never give (the same
    /// device twice).
    /// </summary>
    public List<PlantItemConfigDto>? ConfigListAnswer { get; set; }

    /// <summary>
    /// La rotta su cui la prossima richiesta <b>scade</b> invece di rispondere, e basta una volta:
    /// <see cref="HttpClient"/> traduce il proprio timeout (30 s, <c>WcsClientOptions.Timeout</c>)
    /// in una <see cref="TaskCanceledException"/>, ed e' esattamente quello che questo campo fa
    /// succedere subito. Diverso da <see cref="FailRoute"/>: li' l'host ha risposto "no", qui non ha
    /// risposto affatto - e chi ha chiamato non sa se la scrittura sia passata o no.
    /// </summary>
    public string? TimeoutRoute { get; set; }

    /// <summary>
    /// La rotta su cui la prossima richiesta e' interrotta dal TRASPORTO invece che dall'host, e
    /// basta una volta: una connessione azzerata o un intermediario che chiude senza rispondere.
    /// <see cref="HttpClient"/> traduce un guasto
    /// cosi' in un <see cref="HttpRequestException"/>, non in un <see cref="TaskCanceledException"/>
    /// (quello e' <see cref="TimeoutRoute"/>) ne' in un rifiuto con uno stato HTTP (quello e'
    /// <see cref="FailRoute"/>): la richiesta e' partita davvero, e chi ha chiamato non sa se
    /// l'host l'abbia eseguita.
    /// </summary>
    public string? HttpFailureRoute { get; set; }

    /// <summary>
    /// Che <b>genere</b> di guasto del trasporto sia quello di <see cref="HttpFailureRoute"/>.
    /// <see cref="HttpRequestError.Unknown"/> - il valore che porta un
    /// <see cref="HttpRequestException"/> costruito senza dirlo - e' il caso in cui non si sa a che
    /// punto la richiesta sia arrivata. <see cref="HttpRequestError.ConnectionError"/> e i suoi
    /// fratelli (la risoluzione del nome, la stretta di mano TLS, il tunnel del proxy) dicono
    /// invece che la connessione non e' mai stata stabilita, cioe' che la richiesta non e' mai
    /// partita: e' la differenza fra "non si sa" e "non e' stato scritto", e il verbale la usa.
    /// </summary>
    public HttpRequestError HttpFailureKind { get; set; } = HttpRequestError.Unknown;

    /// <summary>Le rotte di sola lettura che devono rispondere 500 (la decorazione all'apertura, per esempio): non si consumano, valgono per tutta la prova.</summary>
    public HashSet<string> BrokenReads { get; } = [];

    /// <summary>
    /// When set, the body of the <c>503</c> the two reads of the cells panel - the door map
    /// (<c>GET /plant/door-mappings</c>) and the warehouse's cells (<c>GET /wms/cells</c>) - answer
    /// with, every time: the engine that stops being loaded, which the host's filter of the "engine"
    /// group (<c>Program.cs</c>) says on every route behind it, both of these included (final review,
    /// C3). Not consumed, unlike <see cref="FailRoute"/>, which fails one route once.
    /// </summary>
    public string? EngineNotLoadedAnswer { get; set; }

    /// <summary>
    /// La rotta su cui la prossima risposta arriva con <b>200 e un corpo che non e' JSON</b>, e
    /// basta una volta: un intermediario che risponde con la propria pagina di accesso, che e' il
    /// caso vero dietro al rilievo 5 della revisione. Le rotte che lo guardano sono
    /// <c>"edit-lock"</c> e <c>"item"</c> (la GET di un dispositivo solo).
    /// <para>
    /// Non e' un doppione di <see cref="FailRoute"/>: li' l'host dice "no" con uno stato HTTP, e il
    /// client lo traduce in una <c>WcsApiException</c> che ogni filtro della pagina conosce. Qui
    /// l'host dice "si'" e poi consegna qualcosa che non si legge, e
    /// <c>HttpContent.ReadFromJsonAsync</c> solleva una <c>JsonException</c> che
    /// <c>WcsClient</c> <b>non avvolge</b>: e' l'eccezione che nessun filtro nominava.
    /// </para>
    /// </summary>
    public string? GarbledRoute { get; set; }

    /// <summary>
    /// Quando e' vero, la GET di UN dispositivo solo risponde 404 anche se l'elenco lo porta: e' il
    /// caso in cui le due scritture di <c>CreateItemCommand</c> sono passate tutte e due e
    /// <b>solo la rilettura finale</b> non trova il dispositivo. Serve a misurare che cosa la pagina
    /// dice li', perche' li' il dispositivo e' intero e collocato - e la frase vecchia mandava a
    /// cercarlo fra i "non collocati", dove non puo' essere.
    /// </summary>
    public bool HideSingleItemReads { get; set; }

    /// <summary>
    /// Un cancello opzionale sulla GET dell'elenco dei dispositivi (stesso schema del
    /// <c>ResponseGate</c> di <see cref="GeometryCountingHandler"/>): nullo per ogni prova che non
    /// lo imposta. Quando c'e', la lettura dell'impianto all'apertura resta sospesa finche' la prova
    /// non lo apre - e' la finestra in cui draw.io puo' dire 'init' PRIMA che il grafo sia arrivato,
    /// la corsa che lascerebbe la tela vuota.
    /// </summary>
    public TaskCompletionSource? ItemsGate { get; set; }

    /// <summary>
    /// Completato quando una GET dell'elenco entra davvero in <see cref="ItemsGate"/>: e' il modo
    /// per una prova di sapere che il salvataggio del disegno e' DENTRO, oltre il permesso e fermo
    /// sulla rilettura dell'impianto, senza andare a tentoni con un'attesa a tempo. Serve alla prova
    /// dell'accavallamento fra il salvataggio del disegno e la scrittura del pannello.
    /// </summary>
    public TaskCompletionSource ItemsGateReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Lo stesso cancello sulla GET di UN dispositivo solo (<c>/plant/items/{id}</c>), che e' una
    /// rotta diversa da quella dell'elenco: e' la rilettura che <c>EditItemConfigCommand</c> fa dopo
    /// aver scritto la configurazione, cioe' la finestra in cui la scrittura del pannello e' gia'
    /// partita e non e' ancora finita.
    /// </summary>
    public TaskCompletionSource? SingleItemGate { get; set; }

    /// <summary>Errore iniettato nella rilettura di un singolo dispositivo, dopo l'eventuale cancello.</summary>
    public Exception? SingleItemFailure { get; set; }

    /// <summary>Completato quando una GET di un dispositivo entra davvero in <see cref="SingleItemGate"/>: si veda <see cref="ItemsGateReached"/>.</summary>
    public TaskCompletionSource SingleItemGateReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Lo stesso cancello sulla scrittura della geometria, cioe' <b>dentro il piano</b> del
    /// salvataggio del disegno: dopo che l'impianto e' stato riletto. E' il punto che conta per
    /// l'accavallamento fra i due gesti che scrivono - una scrittura del pannello che arriva qui
    /// cade DOPO la fotografia su cui il salvataggio si chiudera'.
    /// </summary>
    public TaskCompletionSource? GeometryGate { get; set; }

    /// <summary>Completato quando una scrittura della geometria entra davvero in <see cref="GeometryGate"/>: si veda <see cref="ItemsGateReached"/>.</summary>
    public TaskCompletionSource GeometryGateReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Lo stesso cancello sulla <c>POST /plant/items</c>, cioe' <b>dentro la nascita</b> di un
    /// dispositivo rilasciato sulla tela. E' l'unico modo per tenere fermo il quarto scrittore della
    /// pagina (<c>PlantWriter.Creating</c>) e vedere che cosa rispondono gli altri tre mentre lui
    /// ha in mano l'impianto: senza di lui, tre dei quattordici rifiuti incrociati non erano
    /// provabili affatto.
    /// </summary>
    public TaskCompletionSource? ItemPostGate { get; set; }

    /// <summary>Completato quando una creazione entra davvero in <see cref="ItemPostGate"/>: si veda <see cref="ItemsGateReached"/>.</summary>
    public TaskCompletionSource ItemPostGateReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Lo stesso cancello sulla RICHIESTA DEL PERMESSO (<c>POST /edit-lock</c>), e vale <b>una volta
    /// sola</b>: la prima richiesta che ci entra lo consuma, tutte le altre passano.
    /// <para>
    /// Serve alla finestra che sta <b>dentro</b> <c>EnsureWritableAsync</c> - due chiamate all'host,
    /// il permesso e lo stato del motore - cioe' fra il controllo della mutua esclusione e la sua
    /// accensione. Una volta sola perche' li' dentro il gesto trattenuto e' uno: il secondo gesto
    /// deve poter arrivare fino in fondo (o essere rifiutato dal cancello della pagina, che e'
    /// quello che si sta misurando), non fermarsi anche lui qui.
    /// </para>
    /// </summary>
    public TaskCompletionSource? EditLockGate { get; set; }

    /// <summary>Completato quando una richiesta del permesso entra davvero in <see cref="EditLockGate"/>: si veda <see cref="ItemsGateReached"/>.</summary>
    public TaskCompletionSource EditLockGateReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Holds the FIRST <c>GET /commands/{id}</c> (a resume read, <c>DrawioDeviceCommands.ResumeFollowingAsync</c>)
    /// until the test opens it, then disarms itself: same one-shot shape as <see cref="ItemStatusGate"/>.
    /// It is the only way to make that read still in flight when a live <c>CommandChanged</c> (or a
    /// second connection drop) arrives - the races the round-1 review found (fix round 1, compito 2).
    /// </summary>
    public TaskCompletionSource? CommandReadGate { get; set; }

    /// <summary>Completed when a resume read really enters <see cref="CommandReadGate"/>: see <see cref="ItemsGateReached"/>.</summary>
    public TaskCompletionSource CommandReadGateReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _commandReadGateUsed;

    /// <summary>
    /// The PLC connections the host holds (row 62, compito 4): what <c>GET /plc/connections</c>
    /// answers, in Id order as <c>PlcSettingsFacade.ConnectionsAsync</c> reads them, and what
    /// <c>PUT /plc/connections</c> replaces - by Id, as <c>PlcSettingsStore.ReplaceConnectionsAsync</c>
    /// does. A test changes it to stage somebody else's save.
    /// </summary>
    public List<PlcConnectionDto> PlcConnections { get; set; } = [];

    /// <summary>Every list a <c>PUT /plc/connections</c> brought, whole, in order.</summary>
    public List<PlcConnectionDto[]> PlcConnectionWrites { get; } = [];

    /// <summary>
    /// A gate on <c>PUT /plc/connections</c>, after its body is read and before anything is replaced:
    /// null for every test that does not set it; held for every PUT while set.
    /// </summary>
    public TaskCompletionSource? PlcConnectionsPutGate { get; set; }

    /// <summary>Completed when a PUT really enters <see cref="PlcConnectionsPutGate"/>: see <see cref="ItemsGateReached"/>.</summary>
    public TaskCompletionSource PlcConnectionsPutGateReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Holds the FIRST <c>GET /plc/connections</c> after it is set, then disarms itself (the shape of
    /// <see cref="ItemStatusGate"/>). The answer is captured BEFORE the hold: a read already on its way
    /// answers with the list as it was when it was issued.
    /// </summary>
    public TaskCompletionSource? PlcConnectionsGetGate { get; set; }

    /// <summary>Completed when a read really enters <see cref="PlcConnectionsGetGate"/>.</summary>
    public TaskCompletionSource PlcConnectionsGetGateReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _plcConnectionsGetGateUsed;

    /// <summary>
    /// What the next <c>PUT /plc/connections</c> throws instead of answering, once: an exception no
    /// filter of the page names (the unforeseen arm), the way <see cref="GraphPutFailure"/> does it.
    /// </summary>
    public Exception? PlcConnectionsPutFailure { get; set; }

    /// <summary>The same for the next <c>GET /plc/connections</c>, once.</summary>
    public Exception? PlcConnectionsGetFailure { get; set; }

    /// <summary>
    /// The connections <paramref name="proposed"/> would remove that a device of <see cref="Items"/>
    /// uses (a <c>DeviceConnectionId</c> above zero, exactly that Id), with those devices: the real
    /// host's <c>PlcSettingsStore.RemovedConnectionsInUseAsync</c>, in its order.
    /// </summary>
    private List<PlcConnectionInUseDto> PlcConnectionsInUse(IReadOnlyList<PlcConnectionDto> proposed)
    {
        var proposedIds = proposed.Select(connection => connection.Id).ToHashSet();
        var removed = PlcConnections.Select(connection => connection.Id).Where(id => id > 0 && !proposedIds.Contains(id)).ToHashSet();
        return [.. Items
            .Where(item => item.DeviceConnectionId > 0 && item.DeviceConnectionId == decimal.Truncate(item.DeviceConnectionId) &&
                           removed.Contains((long)item.DeviceConnectionId))
            .GroupBy(item => (long)item.DeviceConnectionId)
            .OrderBy(group => group.Key)
            .Select(group => new PlcConnectionInUseDto(group.Key, [.. group
                .OrderBy(item => item.Name, StringComparer.Ordinal).ThenBy(item => item.ItemId)
                .Select(item => new PlcConnectionUserDto(item.ItemId, item.Name))]))];
    }

    /// <summary>The real host's detail for that refusal (<c>PlcConnectionInUseException</c>), word for word.</summary>
    private static string PlcConnectionsInUseDetail(IReadOnlyList<PlcConnectionInUseDto> inUse) =>
        "connessioni PLC: " +
        string.Join("; ", inUse.Select(connection =>
            $"la connessione {connection.ConnectionId} è ancora usata da " +
            (connection.Devices.Count == 1 ? "un dispositivo: " : $"{connection.Devices.Count} dispositivi: ") +
            string.Join(", ", connection.Devices.Select(device =>
                string.IsNullOrWhiteSpace(device.Name) ? device.ItemId.ToString() : $"{device.Name} ({device.ItemId})")))) +
        ". Spostarli su un'altra connessione prima di toglierla: l'elenco non è stato sostituito.";

    /// <summary>
    /// Lo stesso cancello sulla GET della decorazione: nullo per ogni prova che non lo imposta.
    /// Quando c'e', la lettura della decorazione all'apertura resta sospesa finche' la prova non lo
    /// apre - ed e' la finestra in cui la decorazione non e' ancora arrivata e <b>non</b> e'
    /// fallita: due fatti diversi, che un salvataggio capitato li' in mezzo non deve confondere.
    /// </summary>
    public TaskCompletionSource? DecorationGate { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var method = request.Method;
        Requests.Enqueue(method + " " + path);

        // Mirrors Wcs.Host's PlcEndpoints and PlcSettingsFacade: the GET in Id order; the PUT refused
        // with 409 while the engine is not stopped - any state but Stop, Pause included - with the
        // real host's title and detail (PlantQueries.RequireStopped, Task 4b of the console). The real
        // route also requires the edit permit (423 without); this fake, like on every other route,
        // knows no permit holder, and the page's permit refusals are tested through GrantEditLock.
        // What the real one does with a repeated Id is kept too: a repeated Id the host
        // already had is updated twice (the last row wins), a repeated NEW Id fails inside the
        // database write (EF tracks two rows with one key) - a 500, not a refusal.
        // Task 4c: a list that removes a connection a device still uses - Items' DeviceConnectionId,
        // above zero, as the real store's PlantItems (PlcSettingsStore.RemovedConnectionsInUseAsync) -
        // is refused with 409, the real title and detail and the plcConnectionsInUse field, checked
        // inside the write (after PlcConnectionsPutGate, so a device moved meanwhile counts) and before
        // the repeated-Id failure, in the real host's order.
        if (path.EndsWith("/plc/connections"))
        {
            if (method == HttpMethod.Get)
            {
                if (Failed("plc-get") is { } refusedRead) return refusedRead;
                if (Garbled("plc-get") is { } garbledRead) return garbledRead;
                if (PlcConnectionsGetFailure is { } getFailure)
                {
                    PlcConnectionsGetFailure = null;
                    throw getFailure;
                }

                var frozenPlc = PlcConnections.OrderBy(connection => connection.Id).ToList();
                if (PlcConnectionsGetGate is { } getGate && Interlocked.Exchange(ref _plcConnectionsGetGateUsed, 1) == 0)
                {
                    PlcConnectionsGetGateReached.TrySetResult();
                    await getGate.Task;
                }

                return Json(frozenPlc);
            }

            var proposed = (await request.Content!.ReadFromJsonAsync<PlcConnectionDto[]>(ct))!;
            if (NavStatus != NavigatorStatusTypes.Stop)
                return Json(new
                {
                    title = "Il motore non è fermo",
                    status = 409,
                    detail = $"connessioni PLC: il motore è in {NavStatus}, e le connessioni PLC si cambiano solo a motore fermo (Stop)",
                }, HttpStatusCode.Conflict);
            if (Failed("plc-put") is { } refusedWrite) return refusedWrite;
            if (PlcConnectionsPutFailure is { } putFailure)
            {
                PlcConnectionsPutFailure = null;
                throw putFailure;
            }

            if (PlcConnectionsPutGate is { } putGate)
            {
                PlcConnectionsPutGateReached.TrySetResult();
                await putGate.Task;
            }

            if (PlcConnectionsInUse(proposed) is { Count: > 0 } inUse)
                return Json(new Dictionary<string, object>
                {
                    ["title"] = "Connessione PLC ancora usata da dispositivi",
                    ["status"] = 409,
                    ["detail"] = PlcConnectionsInUseDetail(inUse),
                    [PlcConnectionProblems.InUseExtension] = inUse,
                }, HttpStatusCode.Conflict);

            var stored = PlcConnections.Select(connection => connection.Id).ToHashSet();
            if (proposed.Where(connection => !stored.Contains(connection.Id)).GroupBy(connection => connection.Id).Any(group => group.Count() > 1))
                return Status(HttpStatusCode.InternalServerError);
            PlcConnectionWrites.Add(proposed);
            PlcConnections = [.. proposed.GroupBy(connection => connection.Id).Select(group => group.Last())];
            Writes.Add("plc-connections");
            return Status(HttpStatusCode.NoContent);
        }

        if (method == HttpMethod.Get && path.EndsWith("/plant/items/status"))
        {
            Interlocked.Increment(ref _itemStatusRequests);
            await HoldAtItemStatusGateAsync();
            return Json(ItemStatuses);
        }

        if (method == HttpMethod.Post && path.EndsWith("/plant/items/connection-type"))
        {
            var update = (await request.Content!.ReadFromJsonAsync<ConnectionTypeUpdate>(ct))!;
            // Mirrors PlantQueries.SetConnectionTypeForAllAsync on the real host: refused BEFORE
            // anything is written, 409, unless the engine is stopped (RequireStopped - the owner's
            // decision of 27/9, D1; it was "while the engine is in Run" before).
            if (NavStatus != NavigatorStatusTypes.Stop)
                return Json(new { title = "Il motore non è fermo" }, HttpStatusCode.Conflict);
            if (Failed("connection-type") is { } refusedMode) return refusedMode;
            ConnectionTypeWrites.Add(update.ConnectionType);
            for (var i = 0; i < Items.Count; i++) Items[i] = Items[i] with { ConnectionType = update.ConnectionType };
            // Configs is a SEPARATE store from Items (GET /plant/items/{id}/config reads from here,
            // not from Items): the real host writes both from the same PlantItems row, so a fake that
            // touched only Items would let a test believe the properties panel's own reread (GET
            // .../config) sees the new mode when it would not - fix round 1, CRITICAL 1's own test
            // caught exactly this gap.
            foreach (var config in Configs.Values) config.ConnectionType = (short)update.ConnectionType;
            return Status(HttpStatusCode.NoContent);
        }

        if (method == HttpMethod.Post && path.EndsWith("/commands"))
        {
            var command = (await request.Content!.ReadFromJsonAsync<CommandRequest>(ct))!;
            Commands.Add(command);
            if (CommandPostGate is { } commandGate)
            {
                CommandPostGateReached.TrySetResult();
                await commandGate.Task;
            }
            if (CommandRefusal is { } refusal) return Status(refusal);
            // "command-post": a refusal with the host's own words (FailRoute, FailureStatus,
            // FailureBody), no answer at all (TimeoutRoute, HttpFailureRoute), or - GarbledRoute - a
            // 200 whose body is not JSON: the command IS recorded above, as the real host would have
            // queued it before answering.
            if (Failed("command-post") is { } failedPost) return failedPost;
            if (Garbled("command-post") is { } garbledPost) return garbledPost;
            return Json(new CommandDto(Commands.Count, DateTime.UtcNow, command.CommandType, command.ItemId, null,
                null, 0, null, null, null, 0, null, null, 0m, null, 0m, null, null, null, true, 0));
        }

        if (method == HttpMethod.Get && segments is [.., "commands", var readId] && long.TryParse(readId, out var commandId))
        {
            if (Failed("command-read") is { } refusedRead) return refusedRead;
            // Captured BEFORE the hold, like EngineStatusGate: a read already in flight answers with
            // the value it had when it was ISSUED, even if the test changes CommandReads (or posts
            // more commands) while it sits at the gate - the fold-loop race fix round 2 measures.
            var frozen = CommandReads.TryGetValue(commandId, out var read)
                ? read
                : commandId >= 1 && commandId <= Commands.Count
                    ? new CommandDto(commandId, DateTime.UtcNow, Commands[(int)commandId - 1].CommandType, Commands[(int)commandId - 1].ItemId,
                        null, null, 0, null, null, null, 0, null, null, 0m, null, 0m, null, null, null, true, 0)
                    : (CommandDto?)null;
            await HoldAtCommandReadGateAsync();
            return frozen is { } value ? Json(value) : Status(HttpStatusCode.NotFound);
        }

        if (method == HttpMethod.Get && path.EndsWith("/missions/steps"))
        {
            if (AllMissionStepsGate is { } allStepsGate)
            {
                AllMissionStepsGateReached.TrySetResult();
                await allStepsGate.Task;
            }

            if (BrokenReads.Contains("all-mission-steps")) return Status(HttpStatusCode.InternalServerError);
            return Json(MissionSteps.Values.SelectMany(steps => steps).ToList());
        }

        // Mirrors MissionEndpoints' PUT /missions/{id}/position and MissionGateway.Move, check by
        // check and in the same order: the engine still in Recover first (503 with its own title,
        // owner's answer 4 of 8/10), the mission (404), then the device (400, a problem
        // document), a move onto where it already is (204, nothing moved), a device that carries
        // another mission (409), a step in flight (409) - each refusal BEFORE anything is touched.
        // A move that goes through drops the mission's steps, as Move does there before the position
        // changes; and since the missions plan (task 4) it builds the new route AT ONCE where the
        // navigator would go straight to the destination - which every device of these test plants
        // does (Test devices, no fixed destination, no deposit mode): the way along Junctions from the
        // new device, through the mission's gateway (RouteOf). Without a destination, or with no way
        // from there, the mission is left without steps, waiting in the navigator's queue as before.
        if (method == HttpMethod.Put && segments is [.., "missions", var movedOf, "position"]
            && Guid.TryParse(movedOf, out var movedMission))
        {
            var update = (await request.Content!.ReadFromJsonAsync<MissionPositionUpdate>(ct))!;
            if (Failed("mission-position") is { } refusedMove) return refusedMove;
            if (MissionMoveFailure is { } moveFailure) throw moveFailure;
            if (NavStatus == NavigatorStatusTypes.Recover)
                return DestinationProblem(HttpStatusCode.ServiceUnavailable, MissionMoveProblems.EngineRecoveringTitle,
                    "Il motore è in Recover: sta ancora caricando le missioni, e lo spostamento non è stato fatto. Riprovare tra poco.");
            var index = Missions.FindIndex(m => m.MissionId == movedMission);
            if (index < 0) return Status(HttpStatusCode.NotFound);
            if (!Items.Any(i => i.ItemId == update.ItemId))
                return Problem($"Il dispositivo {update.ItemId} non esiste", HttpStatusCode.BadRequest);
            if (Missions[index].PositionItemId == update.ItemId) return Status(HttpStatusCode.NoContent);
            if (Missions.Any(m => m.MissionId != movedMission && m.PositionItemId == update.ItemId))
                return Problem($"Il dispositivo {update.ItemId} porta già un'altra missione", HttpStatusCode.Conflict);
            if (MissionsWithAStepInFlight.Contains(movedMission))
            {
                Missions[index] = Missions[index] with { PendingChange = MissionPendingChangeKind.Position, PendingChangeItemId = update.ItemId };
                return Status(HttpStatusCode.Accepted);
            }
            if (MissionMoveGate is { } moveGate)
            {
                MissionMoveGateReached.TrySetResult();
                await moveGate.Task;
            }

            // The engine's setter drops a gateway on arrival at it (or at a device of its group), and
            // the mark of the operator's choice on arrival at the destination, by the same rule
            // (PathMission.PositionItemId, Reaches).
            var arrived = Missions[index];
            var moved = Missions[index] = arrived with
            {
                PositionItemId = update.ItemId,
                GatewayItemId = arrived.GatewayItemId is { } passed && Reaches(update.ItemId, passed) ? null : arrived.GatewayItemId,
                OperatorDestination = arrived.OperatorDestination
                                      && !(arrived.DestinationItemId is { } bound && Reaches(update.ItemId, bound))
            };
            MissionSteps.Remove(movedMission);
            if (moved.DestinationItemId is { } destination && destination != Guid.Empty && destination != update.ItemId
                && RouteOf(update.ItemId, destination, moved.GatewayItemId) is { } route)
                MissionSteps[movedMission] = StepsOf(movedMission, route);
            MissionMoves.Add((movedMission, update.ItemId));
            return Status(HttpStatusCode.NoContent);
        }

        // Item 54, decision 6: mirrors MissionEndpoints' GET /missions/{id}/storage-cells and
        // MissionGateway.StorageCellCandidatesAsync: 404 for a mission that is not active (no body), 400 for
        // a device that does not exist or has no cells, 503 for the warehouse system that does not answer;
        // otherwise the cells the test put there for that device, none when it put none.
        if (method == HttpMethod.Get && segments is [.., "missions", var cellsOf, "storage-cells"]
            && Guid.TryParse(cellsOf, out var cellsMission))
        {
            var cellsItemId = Guid.TryParse(System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["itemId"], out var askedAbout) ? askedAbout : Guid.Empty;
            // What the warehouse system proposes is taken as the request arrives: a held request answers
            // with the cells of its own moment, not with those a test put there for the next one.
            var proposed = StorageCells.GetValueOrDefault(cellsItemId)?.ToList() ?? [];
            StorageCellsRequests.Add((cellsMission, cellsItemId));
            if (StorageCellsGate is { } cellsGate)
            {
                StorageCellsGateReached.TrySetResult();
                await cellsGate.Task;
            }

            if (Failed("mission-storage-cells") is { } refusedCells) return refusedCells;
            if (Missions.All(m => m.MissionId != cellsMission)) return Status(HttpStatusCode.NotFound);
            if (Items.FirstOrDefault(i => i.ItemId == cellsItemId) is not { } device)
                return DestinationProblem(HttpStatusCode.BadRequest, StorageCellCandidatesProblems.DeviceNotFoundTitle,
                    $"Il dispositivo {cellsItemId} non esiste");
            if (StoragesOf(device).Count == 0 && !IsARelocator(cellsItemId))
                return DestinationProblem(HttpStatusCode.BadRequest, StorageCellCandidatesProblems.NotAStorageTitle,
                    $"Il dispositivo {cellsItemId} non è un magazzino, un gruppo di magazzini o un ricollocatore: non ha celle");
            if (WarehouseUnreachable)
                return DestinationProblem(HttpStatusCode.ServiceUnavailable, MissionDestinationProblems.WarehouseUnavailableTitle,
                    "Il gestionale non ha risposto, o non è configurato: le celle non si possono elencare",
                    new Dictionary<string, object?> { ["unavailable"] = "warehouseUnreachable" });
            return Json(proposed);
        }

        // Mirrors MissionEndpoints' PUT /missions/{id}/destination and
        // MissionGateway.ChangeDestinationAsync, check by check and in the same order: a choice on the
        // gateway that is not well formed (400, the host's own check, before the engine), the mission
        // (404), the destination (400), the gateway to set (400), a mission of a kind that builds its
        // own route (409), the destination where the load unit is or its position's group (409), a
        // cell search or another change under way (409: the engine takes the search's place first),
        // a step in flight (409); a gateway the load unit has already reached (its position, or the
        // position's group) is dropped, as the engine does, not refused; then the route from where the
        // load unit is, through the gateway chosen (422, with the gateway and whether there is a route
        // without it); then a copy of the mission's door that cannot be stopped (409), when the mission
        // has a door in a warehouse and does not keep it; then, for a warehouse, the warehouse itself
        // (503, or 422 when it has no cell) - not
        // when the same warehouse with the gateway changed keeps the cell it has. Each refusal answers the
        // host's title (MissionDestinationProblems) and, where the host puts them, its fields, BEFORE
        // anything is touched. A change that goes through writes the destination, the gateway as
        // chosen and the new route at once, whatever the engine's state, as the engine does.
        if (method == HttpMethod.Put && segments is [.., "missions", var redirectedOf, "destination"]
            && Guid.TryParse(redirectedOf, out var redirectedMission))
        {
            var update = (await request.Content!.ReadFromJsonAsync<MissionDestinationUpdate>(ct))!;
            MissionDestinationRequests.Add((redirectedMission, update));
            if (MissionDestinationArrivalGate is { } arrivalGate)
            {
                MissionDestinationArrivalGateReached.TrySetResult();
                await arrivalGate.Task;
            }

            if (Failed("mission-destination") is { } refusedChange) return refusedChange;
            if (MissionDestinationFailure is { } changeFailure) throw changeFailure;
            if (update.Gateway is { } choice && !(choice.Mode is MissionGatewayUpdateMode.Keep or MissionGatewayUpdateMode.Clear
                    ? choice.ItemId is null
                    : choice.Mode == MissionGatewayUpdateMode.Set && choice.ItemId is { } chosen && chosen != Guid.Empty))
                return DestinationProblem(HttpStatusCode.BadRequest, MissionDestinationProblems.InvalidGatewayChoiceTitle,
                    "La scelta sul gateway non è valida: il dispositivo va con «set» e solo con lui");
            // Item 54: the host's own check of what goes with the destination, before the engine.
            if (update is { AtTheRelocator: not null, CellDoorCode: not null } || update.CellDoorCode is { } blank && string.IsNullOrWhiteSpace(blank))
                return DestinationProblem(HttpStatusCode.BadRequest, MissionDestinationProblems.InvalidDestinationChoiceTitle,
                    "Una cella precisa e la scelta del ricollocatore non vanno insieme, e il codice della cella non è vuoto");
            var index = Missions.FindIndex(m => m.MissionId == redirectedMission);
            if (index < 0)
                return DestinationProblem(HttpStatusCode.NotFound, MissionDestinationProblems.MissionNotFoundTitle,
                    $"La missione {redirectedMission} non è attiva");
            var mission = Missions[index];
            if (Items.FirstOrDefault(i => i.ItemId == update.ItemId) is not { } target)
                return DestinationProblem(HttpStatusCode.BadRequest, MissionDestinationProblems.DeviceNotFoundTitle,
                    $"Il dispositivo {update.ItemId} non esiste");
            if (update.Gateway is { Mode: MissionGatewayUpdateMode.Set, ItemId: { } toSet } && Items.All(i => i.ItemId != toSet))
                return DestinationProblem(HttpStatusCode.BadRequest, MissionDestinationProblems.GatewayNotFoundTitle,
                    $"Il gateway {toSet} non è un dispositivo");
            // Item 54, decision 5: what a relocator does, asked of a device that is not one.
            if (update.AtTheRelocator is not null && !IsARelocator(update.ItemId))
                return DestinationProblem(HttpStatusCode.BadRequest, MissionDestinationProblems.NotARelocatorTitle,
                    $"Il dispositivo {update.ItemId} non è il ricollocatore di nessun magazzino");
            // Item 54, decision 6: the precise cell resolved before the change (MissionGateway.CellInTheChoiceAsync),
            // from the door map: mapped, a door A, and in what was chosen - the warehouse, a warehouse of the
            // group, or one the relocator chosen serves.
            DoorMappingDto? preciseDoor = null;
            if (update.CellDoorCode is { } cellCode)
            {
                preciseDoor = DoorMappings.FirstOrDefault(m => m.DoorCode == cellCode.Trim());
                var cellRefusal = preciseDoor switch
                {
                    null => "unknownDoor",
                    { Side: "B" } => "notADoorA",
                    { } mapped when !StoragesOf(target).Contains(mapped.ItemId)
                                  && !Items.Any(i => i.ItemId == mapped.ItemId && i.DeviceType == DeviceTypes.Storage && i.RelocateItemId == target.ItemId)
                        => "notInTheChoice",
                    _ => null,
                };
                if (cellRefusal is not null) return CellRefused(cellCode, cellRefusal);
            }
            if (Reaches(mission.PositionItemId, update.ItemId))
                return DestinationProblem(HttpStatusCode.Conflict, MissionDestinationProblems.AlreadyThereTitle,
                    $"L'UDC è già sul dispositivo {update.ItemId}, o nel suo gruppo");
            if (MissionsWithACellSearchInFlight.Contains(redirectedMission))
                return DestinationProblem(HttpStatusCode.Conflict, MissionDestinationProblems.CellSearchInFlightTitle,
                    "Per la missione è in corso una ricerca della cella di deposito, o un altro cambio di destinazione: riprovare quando finisce");
            if (MissionsWithAStepInFlight.Contains(redirectedMission))
            {
                Missions[index] = mission with { PendingChange = MissionPendingChangeKind.Destination, PendingChangeItemId = update.ItemId };
                return Status(HttpStatusCode.Accepted);
            }
            if (MissionsMovedDuringTheChange.Contains(redirectedMission))
                return DestinationProblem(HttpStatusCode.Conflict, MissionDestinationProblems.StepInFlightTitle,
                    "La missione ha un passo in esecuzione, o un dispositivo lo sta avviando: aspettare che finisca e riprovare");
            var gateway = update.Gateway?.Mode switch
            {
                MissionGatewayUpdateMode.Clear => null,
                MissionGatewayUpdateMode.Set => update.Gateway.ItemId,
                _ => mission.GatewayItemId,
            };
            if (gateway is { } through && Reaches(mission.PositionItemId, through)) gateway = null;

            // Item 54, decision 6: towards the precise cell the destination is the warehouse of its door,
            // reserved by the warehouse system or refused with the reason; the cell the mission had is not
            // kept (the operator chose another one).
            if (preciseDoor is { } chosenDoor)
            {
                var code = update.CellDoorCode!.Trim();
                if (RouteOf(mission.PositionItemId, chosenDoor.ItemId, gateway) is not { } toTheCell)
                    return DestinationProblem(HttpStatusCode.UnprocessableEntity, MissionDestinationProblems.NoRouteTitle,
                        $"Dalla posizione dell'UDC non c'è un percorso verso {chosenDoor.ItemId}",
                        new Dictionary<string, object?>
                        {
                            [MissionDestinationProblems.GatewayExtension] = gateway,
                            [MissionDestinationProblems.WouldRouteWithoutGatewayExtension] =
                                gateway is not null && RouteOf(mission.PositionItemId, chosenDoor.ItemId, null) is not null,
                        });
                if (WarehouseUnreachable)
                    return DestinationProblem(HttpStatusCode.ServiceUnavailable, MissionDestinationProblems.WarehouseUnavailableTitle,
                        "Il gestionale non ha risposto, o non è configurato: la destinazione non è cambiata",
                        new Dictionary<string, object?> { ["unavailable"] = "warehouseUnreachable" });
                if (CellsTheWarehouseRefuses.TryGetValue(code, out var refusedBecause)) return CellRefused(code, refusedBecause);
                if (MissionDestinationGate is { } cellGate)
                {
                    MissionDestinationGateReached.TrySetResult();
                    await cellGate.Task;
                }

                Missions[index] = mission with
                {
                    DestinationItemId = chosenDoor.ItemId, GatewayItemId = gateway, OperatorDestination = true,
                    StorageDoorCode = code, RelocatorArrival = null,
                };
                MissionSteps[redirectedMission] = StepsOf(redirectedMission, toTheCell);
                return Status(HttpStatusCode.NoContent);
            }

            if (RouteOf(mission.PositionItemId, update.ItemId, gateway) is not { } newRoute)
            {
                var withoutIt = gateway is not null && RouteOf(mission.PositionItemId, update.ItemId, null) is not null;
                return DestinationProblem(HttpStatusCode.UnprocessableEntity, MissionDestinationProblems.NoRouteTitle,
                    $"Dalla posizione dell'UDC non c'è un percorso verso {update.ItemId}",
                    new Dictionary<string, object?>
                    {
                        [MissionDestinationProblems.GatewayExtension] = gateway,
                        [MissionDestinationProblems.WouldRouteWithoutGatewayExtension] = withoutIt,
                    });
            }

            // Decision 6 of 2/10 (MissionGateway.ChangeToStorageAsync): the same warehouse again with the
            // gateway changed keeps the cell the mission has, and asks the warehouse system nothing;
            // without a cell it runs the whole request. A gateway named again, or one already reached, is
            // no change. "The same warehouse" is the engine's, read from the mission's door through the
            // door map, not from its destination (row 51 of the queue: the fake compared the destination
            // with the target, and a group chosen again over its member did not keep the cell here, where
            // the engine keeps it): to keep the cell the door's device is one of the warehouses chosen
            // (MissionGateway.DoorInTheChosenStoragesAsync, StoragesOf), and an extraction still in its
            // cell does not keep one; "another cell" is asked when the operator chose the destination and
            // the door is in the warehouse chosen or in its group (CellToReplaceAsync, IsInChosenStorage).
            // The operator's mark is the one MissionDto carries (OperatorDestination, row 44 of the queue):
            // a destination written by the configuration excludes no cell.
            var gatewayChanged = update.Gateway is { Mode: not MissionGatewayUpdateMode.Keep }
                                 && gateway != (mission.GatewayItemId is { } now && now != Guid.Empty ? now : null);
            var door = mission.StorageDoorCode is { Length: > 0 } doorCode
                ? DoorMappings.FirstOrDefault(m => m.DoorCode == doorCode)
                : null;
            var keepsTheCell = gatewayChanged && !StillInItsCell(mission) && door != null
                               && StoragesOf(target).Contains(door.ItemId);
            var anotherCell = mission.OperatorDestination && door is { CellId: not 0 } && IsInChosenStorage(door.ItemId, target);
            // A group of warehouses is a warehouse (StoragesOf): the engine reserves in one of its members.
            var isAWarehouse = StoragesOf(target).Count > 0;

            // Keeping the cell, the engine's destination is the door's warehouse, and the route goes to it
            // (KeepTheCellAndRouteAgain), also when the group was chosen: without a route there, it is
            // refused as any route that is missing.
            var written = keepsTheCell ? door!.ItemId : update.ItemId;
            if (written != update.ItemId)
            {
                if (RouteOf(mission.PositionItemId, written, gateway) is not { } toTheDoor)
                    return DestinationProblem(HttpStatusCode.UnprocessableEntity, MissionDestinationProblems.NoRouteTitle,
                        $"Dalla posizione dell'UDC non c'è un percorso verso {written}",
                        new Dictionary<string, object?>
                        {
                            [MissionDestinationProblems.GatewayExtension] = gateway,
                            [MissionDestinationProblems.WouldRouteWithoutGatewayExtension] =
                                gateway is not null && RouteOf(mission.PositionItemId, written, null) is not null,
                        });
                newRoute = toTheDoor;
            }

            // The copies of the mission's door (MissionGateway.CopiesOfTheDoor.Stop) are stopped after the
            // route probe and before the warehouse system is asked, and only when the mission has a door
            // in a warehouse and leaves the cell: a gateway-only change that keeps it does not touch them.
            if (MissionsWithACopyInFlight.Contains(redirectedMission) && !keepsTheCell
                && mission.StorageDoorCode is { Length: > 0 }
                && Items.FirstOrDefault(i => i.ItemId == mission.DestinationItemId) is { DeviceType: DeviceTypes.Storage })
                return DestinationProblem(HttpStatusCode.Conflict, MissionDestinationProblems.CopyInFlightTitle,
                    "Un'altra UDC che condivide la cella della missione si sta muovendo: la missione è ferma e la destinazione non è cambiata, riprovare fra un momento");
            if (isAWarehouse && !keepsTheCell && WarehouseRefusesTheChangeOfCell
                && mission.MissionType == MissionTypes.Extraction)
                return DestinationProblem(HttpStatusCode.UnprocessableEntity, MissionDestinationProblems.WarehouseRefusedTitle,
                    "Il gestionale ha rifiutato il cambio: la destinazione non è cambiata, e la missione resta l'estrazione che era");
            // The field that says which of the host's two 503s it is (row 34 of the queue), as
            // MissionEndpoints.DestinationAnswer writes it: the page reads it, not the title.
            if (isAWarehouse && !keepsTheCell && WarehouseUnreachable)
                return DestinationProblem(HttpStatusCode.ServiceUnavailable, MissionDestinationProblems.WarehouseUnavailableTitle,
                    "Il gestionale non ha risposto, o non è configurato: la destinazione non è cambiata",
                    new Dictionary<string, object?> { ["unavailable"] = "warehouseUnreachable" });
            if (isAWarehouse && !keepsTheCell && WarehouseUnreachableAfterReserving)
            {
                Missions[index] = mission with { StorageDoorCode = null };
                MissionSteps.Remove(redirectedMission);
                return DestinationProblem(HttpStatusCode.ServiceUnavailable, MissionDestinationProblems.WarehouseUnavailableTitle,
                    "Il gestionale non ha risposto, o non è configurato: la destinazione non è cambiata. La cella che la missione teneva però è stata lasciata durante il cambio",
                    new Dictionary<string, object?> { ["unavailable"] = "warehouseUnreachable", [MissionDestinationProblems.LostTheCellExtension] = true });
            }
            if (isAWarehouse && !keepsTheCell && StoragesWithoutAFreeCell.Contains(update.ItemId))
            {
                if (ChangeOfCellRefusedAfterTheReports && mission.MissionType == MissionTypes.Extraction
                    && (mission.AssignmentGuid is { } assignment && assignment != Guid.Empty || mission.ExtractionQueueId > 0)
                    && (Items.FirstOrDefault(i => i.ItemId == mission.PositionItemId) is { } position
                        ? position.DeviceType == DeviceTypes.Storage
                        : Items.FirstOrDefault(i => i.ItemId == mission.SourceItemId) is { DeviceType: DeviceTypes.Storage }))
                {
                    Missions.RemoveAt(index);
                    MissionSteps.Remove(redirectedMission);
                    return DestinationProblem(HttpStatusCode.UnprocessableEntity, MissionDestinationProblems.NoStorageCellTitle,
                        $"Nessuna cella da prenotare per questa UDC nel magazzino {update.ItemId}: il perché è nel registro del motore."
                        + " Il gestionale aveva però già saputo che la missione lascia il suo ordine: la missione è chiusa, e l'UDC resta nella sua cella senza ordine",
                        new Dictionary<string, object?>
                        {
                            [MissionDestinationProblems.NoOtherFreeCellExtension] = false,
                            [MissionDestinationProblems.MissionClosedExtension] = true,
                        });
                }

                return DestinationProblem(HttpStatusCode.UnprocessableEntity, MissionDestinationProblems.NoStorageCellTitle,
                    anotherCell
                        ? $"Il magazzino {update.ItemId} non ha un'altra cella libera per questa UDC: quella che la missione ha resta prenotata"
                        : $"Nessuna cella da prenotare per questa UDC nel magazzino {update.ItemId}: il perché è nel registro del motore",
                    new Dictionary<string, object?> { [MissionDestinationProblems.NoOtherFreeCellExtension] = anotherCell });
            }

            if (MissionDestinationGate is { } changeGate)
            {
                MissionDestinationGateReached.TrySetResult();
                await changeGate.Task;
            }

            // A change that goes through is the operator's choice: the engine's commit raises its mark
            // (PathMission.SetOperatorDestination), also when only the gateway changed and the cell stays;
            // and it writes what the relocator does, or takes it away (item 54, PathMission.CommitRoute).
            Missions[index] = mission with
            {
                DestinationItemId = written, GatewayItemId = gateway, OperatorDestination = true, RelocatorArrival = update.AtTheRelocator,
            };
            MissionSteps[redirectedMission] = StepsOf(redirectedMission, newRoute);
            return Status(HttpStatusCode.NoContent);
        }

        if (method == HttpMethod.Get && segments is [.., "missions", var stepsOf, "steps"]
            && Guid.TryParse(stepsOf, out var stepsMission))
        {
            if (MissionStepsGate is { } stepsGate)
            {
                MissionStepsGateReached.TrySetResult();
                await stepsGate.Task;
            }

            return Json(MissionSteps.GetValueOrDefault(stepsMission) ?? []);
        }

        if (method == HttpMethod.Get && path.EndsWith("/status"))
        {
            if (BrokenReads.Contains("status")) return Status(HttpStatusCode.InternalServerError);
            // Captured BEFORE the hold: a request already in flight answers with the value it had
            // when it was issued, not with whatever NavStatus becomes while it waits at the gate.
            var frozen = NavStatus;
            await HoldAtEngineStatusGateAsync();
            return Json(new EngineStatus(frozen, DefragmentationTypes.None, "test", DateTime.UtcNow));
        }

        if (path.EndsWith("/edit-lock"))
        {
            if (method == HttpMethod.Delete) return Status(HttpStatusCode.NoContent);
            if (Garbled("edit-lock") is { } garbledLock) return garbledLock;
            if (Failed("edit-lock") is { } failedLock) return failedLock;
            await HoldAtEditLockGateAsync();
            var holder = (await request.Content!.ReadFromJsonAsync<EditLockRequest>(ct))!.Holder;
            EditLockRequests++;
            var granted = GrantEditLock && (RevokeEditLockAfter is not { } limit || EditLockRequests <= limit);
            return granted
                ? Json(new EditLockDto(holder, LockSince, LockSince.AddMinutes(1), IsMine: true))
                : Json(new EditLockDto("un-altro-circuito", LockSince, LockSince.AddMinutes(1), IsMine: false),
                    HttpStatusCode.Conflict);
        }

        if (path.EndsWith("/plant/decoration"))
        {
            if (method == HttpMethod.Get)
            {
                if (DecorationGate is not null) await DecorationGate.Task;
                return BrokenReads.Contains("decoration")
                    ? Status(HttpStatusCode.InternalServerError)
                    : Json(new PlantDecorationDto(Decoration));
            }

            if (Failed("decoration") is { } refused) return refused;
            var body = (await request.Content!.ReadFromJsonAsync<PlantDecorationDto>(ct))!;
            Decoration = body.Xml;
            DecorationWrites.Add(body.Xml);
            Writes.Add("decoration");
            return Status(HttpStatusCode.NoContent);
        }

        // L'impianto intero: le due rotte dell'esportazione e dell'importazione. La lettura si
        // compone dagli stessi pezzi che questo gestore gia' tiene - configurazioni, geometrie
        // ricavate dai dispositivi, giunzioni - piu' la decorazione, che dal 2026-09-19 viaggia nel
        // quinto campo del contratto. Prima di /plant/items, perche' quel confronto e' su un
        // suffisso e "/plant/graph" non lo tocca, ma l'ordine dice quale rotta e' quale.
        // The GET carries the PLC connections, as the real export does (GraphEndpoints), and the PUT
        // keeps the real host's engine rule (PlantQueries.ReplaceGraphCoreAsync): since the owner's
        // decision of 27/9 on Pause, every file is refused in any state but Stop. A file that carries
        // connections replaces them. The real route also requires the permit; this fake knows no holder.
        if (path.EndsWith("/plant/graph"))
        {
            if (method == HttpMethod.Get)
                return Json(new PlantGraphDto(
                    [.. Items.Select(i => Configs.TryGetValue(i.ItemId, out var config) ? config : new PlantItemConfigDto { ItemId = i.ItemId })],
                    [.. Items.Select(i => new PlantItemGeometryDto(i.ItemId, i.X, i.Y, i.Width, i.Height, i.Rotation))],
                    Junctions,
                    [],
                    Decoration,
                    [.. PlcConnections.OrderBy(connection => connection.Id)]));

            if (Failed("graph") is { } refusedGraph) return refusedGraph;
            if (GraphPutFailure is { } graphPutFailure) throw graphPutFailure;
            var imported = (await request.Content!.ReadFromJsonAsync<PlantGraphDto>(ct))!;
            // As the real host (owner's decision of 27/9 on Pause): any import only with the engine stopped.
            if (NavStatus != NavigatorStatusTypes.Stop)
                return Json(new
                {
                    title = "Il motore non è fermo",
                    status = 409,
                    detail = $"sostituzione dell'impianto: il motore è in {NavStatus}, e un impianto si importa solo a motore fermo (Stop): i dispositivi nuovi nascono senza connessione, e il motore gliela assegna solo quando apre le connessioni: la ripresa da Pause non le riapre",
                }, HttpStatusCode.Conflict);
            if (GraphPutGate is { } graphGate)
            {
                GraphPutGateReached.TrySetResult();
                await graphGate.Task;
            }
            GraphPuts.Add(imported);
            Writes.Add("graph");
            if (imported.Decoration is { } importedDecoration) Decoration = importedDecoration;
            if (imported.PlcConnections is { } replacedConnections) PlcConnections = [.. replacedConnections];
            return Status(HttpStatusCode.NoContent);
        }

        // Le due letture che il riquadro di conferma fa sempre, qualunque sia il formato del file:
        // un'importazione cancella missioni e coda di estrazione comunque, e la conferma le conta.
        // Vuote qui: le prove di questa suite contano quello che l'importazione SCRIVE, non quello
        // che perde - quella meta' e' gia' misurata in ImportExportTest.
        if (method == HttpMethod.Get && path.EndsWith("/missions"))
        {
            // Captured BEFORE the hold, like EngineStatusGate: a read already in flight answers with the
            // missions as they were when it was ISSUED, even if the test changes them meanwhile - the
            // answer that predates what the channel says while it is on its way.
            var frozenMissions = Missions.ToList();
            if (MissionsGate is { } gate)
            {
                MissionsGateReached.TrySetResult();
                await gate.Task;
            }

            if (BrokenReads.Contains("missions")) return Status(HttpStatusCode.InternalServerError);
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
            return Guid.TryParse(query["itemId"], out var positionItemId)
                ? Json(frozenMissions.Where(m => m.PositionItemId == positionItemId).ToList())
                : Json(frozenMissions);
        }

        if (method == HttpMethod.Get && path.EndsWith("/extraction-queue")) return Json(Array.Empty<ExtractionQueueItemDto>());

        if (method == HttpMethod.Get && path.EndsWith("/plant/items"))
        {
            if (ItemsGate is not null)
            {
                ItemsGateReached.TrySetResult();
                await ItemsGate.Task;
            }
            // The lever for the plant going unreadable HALFWAY THROUGH A SAVE, which is the only
            // moment in which this page can leave somebody's work nowhere: the save stopped, and
            // the plant cannot be read again to compute another one.
            //
            // It waits for the first write on purpose, and not because a plain flag would be
            // simpler: the page reads the plant BEFORE the plan too, and a flag armed from the
            // start would stop the save there - a different failure, with a different message,
            // which never reaches the moment being measured.
            if (BrokenReads.Contains("items-after-the-first-write") && Writes.Count > 0)
                return Status(HttpStatusCode.InternalServerError);
            if (Failed("items") is { } refusedItems) return refusedItems;
            return Json(Items);
        }

        if (method == HttpMethod.Get && path.EndsWith("/plant/device-types")) return Json(Types);
        if (method == HttpMethod.Get && path.EndsWith("/plant/junctions")) return Json(Junctions);

        if (method == HttpMethod.Get && path.EndsWith("/plant/automatic-cells"))
        {
            AutomaticCellReads++;
            return BrokenReads.Contains("automatic-cells") ? Status(HttpStatusCode.InternalServerError) : Json(AutomaticCells);
        }

        // GET /wms/configuration-check: the only one of the two plant checks that talks to the host.
        // BrokenReads is the lever for the failure - the page must show it as a failed check and not
        // as "no problems found", which is the very difference ChecksPanel keeps.
        if (method == HttpMethod.Get && path.EndsWith("/wms/configuration-check"))
        {
            WmsConfigurationCheckRequests.Add(request.RequestUri!.PathAndQuery);
            if (BrokenReads.Contains("configuration-check")) return Status(HttpStatusCode.InternalServerError);
            if (WmsConfigurationCheckGate is not null)
            {
                WmsConfigurationCheckGateReached.TrySetResult();
                await WmsConfigurationCheckGate.Task;
            }
            return Json(WmsConfigurationWarnings);
        }

        if (method == HttpMethod.Get && path.EndsWith("/wms/cells"))
        {
            WmsCellReads++;
            if (BrokenReads.Contains("wms-cells")) return Status(HttpStatusCode.ServiceUnavailable);
            if (EngineNotLoadedAnswer is { } unloadedForTheCells)
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent(unloadedForTheCells) };
            // "wms-cells": the host's own answer (FailRoute, FailureStatus, FailureBody) - the problem
            // document of a warehouse that does not answer, which BrokenReads' empty 503 does not carry.
            if (Failed("wms-cells") is { } failedCells) return failedCells;
            if (Garbled("wms-cells") is { } garbledCells) return garbledCells;
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
            var areaType = short.TryParse(query["areaType"], out var parsedArea) ? parsedArea : (short?)null;
            var automaticOnly = bool.TryParse(query["automaticOnly"], out var parsedAutomatic) && parsedAutomatic;
            if (WmsCellsGate is not null)
            {
                WmsCellsGateReached.TrySetResult();
                await WmsCellsGate.Task;
            }
            return Json(WmsCellsFor?.Invoke(areaType, automaticOnly) ?? WmsCells);
        }

        if (method == HttpMethod.Get && path.EndsWith("/plant/door-mappings"))
        {
            DoorMappingReads++;
            if (BrokenReads.Contains("door-mappings")) return Status(HttpStatusCode.InternalServerError);
            if (EngineNotLoadedAnswer is { } unloadedForTheDoors)
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent(unloadedForTheDoors) };
            if (Garbled("door-mappings") is { } garbledDoors) return garbledDoors;
            await HoldAtDoorMappingReadGateAsync();
            return Json(DoorMappings);
        }

        // The PUT: it replaces the map of the devices present in the list and of no others, like
        // PlantQueries.ReplaceDoorMappingsAsync (mappings.GroupBy(m => m.ItemId)) - an EMPTY list
        // touches nothing, and that is exactly why DoorMappingWrites picks the DELETE when the wanted
        // set is empty.
        if (method == HttpMethod.Put && path.EndsWith("/plant/door-mappings"))
        {
            if (Failed("door-put") is { } refused) return refused;
            if (DoorMappingGate is not null)
            {
                DoorMappingGateReached.TrySetResult();
                await DoorMappingGate.Task;
            }
            var wanted = (await request.Content!.ReadFromJsonAsync<DoorMappingDto[]>(ct))!;
            // 404 and NOTHING written if even one device is gone, like the host: the map of a device
            // that does not exist is an orphan row, and the foreign key on DoorMapping.ItemId would
            // refuse it anyway. The body is recorded all the same - a test that wants to see what
            // went out on the wire has to see it even when the host said no.
            DoorMappingWrites.Add(wanted);
            if (wanted.Select(m => m.ItemId).Distinct().Any(id => !Knows(id))) return Status(HttpStatusCode.NotFound);
            Writes.Add("door-put");
            foreach (var group in wanted.GroupBy(m => m.ItemId)) ReplaceDoorMappingsForItem(group.Key, [.. group]);
            return Status(HttpStatusCode.NoContent);
        }

        if (method == HttpMethod.Post && path.EndsWith("/plant/door-mappings/link"))
        {
            if (Failed("door-link") is { } refused) return refused;
            var link = (await request.Content!.ReadFromJsonAsync<DoorMappingLink>(ct))!;
            CellLinks.Add(link);
            if (!Knows(link.ItemId)) return Status(HttpStatusCode.NotFound);
            Writes.Add("door-link");
            // Like PlantPersistence.LinkBufferCellAsync: ONE row, side "A", REPLACING the device's
            // whole map - not one row more.
            ReplaceDoorMappingsForItem(link.ItemId, [new DoorMappingDto(0, link.ItemId, link.DoorCode, link.CellId, "A", 0, false)]);
            return Status(HttpStatusCode.NoContent);
        }

        if (method == HttpMethod.Delete && segments is [.., "door-mappings", var clearId] && Guid.TryParse(clearId, out var clearItemId))
        {
            if (Failed("door-delete") is { } refused) return refused;
            DoorMappingClears.Add(clearItemId);
            if (!Knows(clearItemId)) return Status(HttpStatusCode.NotFound);
            Writes.Add("door-delete");
            ReplaceDoorMappingsForItem(clearItemId, []);
            return Status(HttpStatusCode.NoContent);
        }

        // GET /plant/items/config: EVERY configuration in one answer (compito 2, the device list). Like
        // the real host (PlantQueries.ItemConfigurationsAsync) it is one row per device of the plant:
        // the device's written configuration when a test seeded one, otherwise the little the device
        // list itself knows about it - so a test that seeds only Items still sees its devices here.
        if (method == HttpMethod.Get && segments is [.., "items", "config"])
        {
            if (BrokenReads.Contains("configs")) return Status(HttpStatusCode.InternalServerError);
            if (ConfigListAnswer is { } verbatim) return Json(verbatim);
            return Json(Items.Select(i => Configs.TryGetValue(i.ItemId, out var known)
                ? known
                : new PlantItemConfigDto
                {
                    ItemId = i.ItemId,
                    Name = i.Name,
                    DeviceType = (short)i.DeviceType,
                    ConnectionType = (short)i.ConnectionType,
                    DeviceConnectionId = i.DeviceConnectionId,
                }).ToList());
        }

        // GET /plant/items/{id}/config: like the real host, one row for every device of the plant - the
        // written configuration when a test seeded one, otherwise the little the device list itself
        // knows (the same fallback as the list route above). 404 only for a device the plant has not.
        // Since the owner's decision of 27/9 (D3) every configuration write of the page reads this
        // first, so a device seeded only in Items must answer here as the real host would.
        if (method == HttpMethod.Get && segments is [.., var configId, "config"] && Guid.TryParse(configId, out var configItemId))
        {
            // "config-get": the read of one device's configuration fails - the one the panel shows, or
            // the one every configuration write of the page makes just before writing (row 37 of the queue).
            if (Failed("config-get") is { } failedRead) return failedRead;
            if (Configs.TryGetValue(configItemId, out var config)) return Json(config);
            return Items.FirstOrDefault(item => item.ItemId == configItemId) is { } known
                ? Json(new PlantItemConfigDto
                {
                    ItemId = known.ItemId,
                    Name = known.Name,
                    DeviceType = (short)known.DeviceType,
                    ConnectionType = (short)known.ConnectionType,
                    DeviceConnectionId = known.DeviceConnectionId,
                })
                : Status(HttpStatusCode.NotFound);
        }

        if (method == HttpMethod.Put && segments is [.., var putConfigId, "config"] && Guid.TryParse(putConfigId, out var putItemId))
        {
            if (Failed("config") is { } refused) return refused;
            var body = (await request.Content!.ReadFromJsonAsync<PlantItemConfigDto>(ct))!;
            // Mirrors PlantQueries.UpdateItemConfigurationAsync (owner's decisions of 27/9): refused in
            // Run, Recover and Dispose (D2); a change of ConnectionType or DeviceConnectionId refused
            // unless the engine is stopped (D1). Before anything is written, and so before it is recorded.
            if (NavStatus is NavigatorStatusTypes.Run or NavigatorStatusTypes.Recover or NavigatorStatusTypes.Dispose)
                return Json(new
                {
                    title = "Il motore non è fermo",
                    status = 409,
                    detail = $"configurazione del dispositivo: il motore è in {NavStatus}, e l'impianto si cambia solo a motore fermo (Stop) o in pausa (Pause)",
                }, HttpStatusCode.Conflict);
            if (NavStatus != NavigatorStatusTypes.Stop && Configs.TryGetValue(putItemId, out var held)
                && (held.ConnectionType != body.ConnectionType || held.DeviceConnectionId != body.DeviceConnectionId))
                return Json(new
                {
                    title = "Il motore non è fermo",
                    status = 409,
                    detail = $"connessione del dispositivo: il motore è in {NavStatus}, e la modalità di connessione e la connessione PLC dei dispositivi si cambiano solo a motore fermo (Stop)",
                }, HttpStatusCode.Conflict);
            ConfigWrites.Add(body);
            // 404 su un dispositivo che non c'e', come l'host: questa rotta era un upsert e faceva
            // nascere il dispositivo, ed era la sola strada per cui ne nasceva uno DALLA PAGINA -
            // non la sola in assoluto, perche' PUT /plant/graph li fa nascere da sempre. Adesso la
            // creazione e' POST /plant/items, qui sotto.
            if (!Knows(putItemId)) return Status(HttpStatusCode.NotFound);
            Writes.Add("config");
            Configs[putItemId] = body;
            // Il nome sta anche sull'elenco dei dispositivi, ed e' quello che la pagina rilegge
            // subito dopo (EditItemConfigCommand): senza questo la rilettura riporterebbe il nome
            // vecchio e nessuna prova vedrebbe la rinomina arrivare davvero.
            var at = Items.FindIndex(i => i.ItemId == putItemId);
            // The PLC connection too, as the real host (one PlantItems row behind both): the grid of the
            // PLC connections (row 62) refuses to remove a connection a device uses, and a device moved
            // off it through the device list must be seen moved in the plant the page reads back.
            if (at >= 0) Items[at] = Items[at] with { Name = body.Name, DeviceType = (DeviceTypes)body.DeviceType, DeviceConnectionId = body.DeviceConnectionId };
            return Status(HttpStatusCode.NoContent);
        }

        // POST /plant/items: la creazione. 409 se quell'identificativo e' gia' un dispositivo, come
        // l'host. Il dispositivo nasce SENZA riquadro: la geometria arriva subito dopo, con la sua
        // rotta - e' la sequenza di CreateItemCommand.
        if (method == HttpMethod.Post && segments is [.., "items"])
        {
            if (Failed("item-post") is { } refusedCreate) return refusedCreate;
            // As the real host (owner's decision of 27/9 on Pause): a device is created only in Stop.
            if (NavStatus != NavigatorStatusTypes.Stop)
                return Json(new
                {
                    title = "Il motore non è fermo",
                    status = 409,
                    detail = $"creazione del dispositivo: il motore è in {NavStatus}, e un dispositivo si crea solo a motore fermo (Stop)",
                }, HttpStatusCode.Conflict);
            await HoldAtItemPostGateAsync();
            var created = (await request.Content!.ReadFromJsonAsync<PlantItemConfigDto>(ct))!;
            ItemCreations.Add(created);
            if (Knows(created.ItemId)) return Status(HttpStatusCode.Conflict);
            Writes.Add("item-post");
            Configs[created.ItemId] = created;
            // Not placed, as on the real host until the geometry arrives: X/Y null (PlantGraph.IsPlaced).
            Items.Add(new PlantItemDto(created.ItemId, created.Name, (DeviceTypes)created.DeviceType,
                null, null, null, default, default, 0, null, 0, 0, 0, 0, default!)
            {
                GeometryVersion = Guid.NewGuid()
            });
            return Status(HttpStatusCode.Created);
        }

        if (method == HttpMethod.Get && segments is [.., "items", var singleId] && Guid.TryParse(singleId, out var singleItemId))
        {
            if (Garbled("item") is { } garbledItem) return garbledItem;
            if (HideSingleItemReads) return Status(HttpStatusCode.NotFound);
            if (SingleItemGate is not null)
            {
                SingleItemGateReached.TrySetResult();
                await SingleItemGate.Task;
            }
            if (SingleItemFailure is { } singleItemFailure) throw singleItemFailure;

            return Items.FirstOrDefault(i => i.ItemId == singleItemId) is { } found ? Json(found) : Status(HttpStatusCode.NotFound);
        }

        if (method == HttpMethod.Put && segments is [.., var geometryId, "geometry"] && Guid.TryParse(geometryId, out _))
        {
            if (GeometryConflictItemIds is not null)
                return Json(new PlantItemGeometryConflictDto(GeometryConflictItemIds), HttpStatusCode.Conflict);
            if (Failed("geometry") is { } refused) return refused;
            await HoldAtGeometryGateAsync();
            var update = (await request.Content!.ReadFromJsonAsync<PlantItemGeometryUpdateDto>(ct))!;
            var written = GeometryOf(update);
            GeometryWrites.Add(written);
            Writes.Add("geometry");
            var confirmation = Place(written);
            return Json(ConfirmGeometryWithAnEmptyVersion ? confirmation with { GeometryVersion = Guid.Empty } : confirmation);
        }

        if (method == HttpMethod.Put && segments is [.., "items", "geometry"])
        {
            if (GeometryConflictItemIds is not null)
                return Json(new PlantItemGeometryConflictDto(GeometryConflictItemIds), HttpStatusCode.Conflict);
            if (Failed("geometry") is { } refused) return refused;
            await HoldAtGeometryGateAsync();
            var updates = (await request.Content!.ReadFromJsonAsync<List<PlantItemGeometryUpdateDto>>(ct))!;
            var all = updates.Select(GeometryOf).ToList();
            GeometryWrites.AddRange(all);
            Writes.Add("geometry");
            var confirmations = all.Select(Place).ToList();
            return Json(confirmations);
        }

        if (method == HttpMethod.Post && path.EndsWith("/plant/junctions"))
        {
            if (Failed("junction-post") is { } refused) return refused;
            var body = (await request.Content!.ReadFromJsonAsync<JunctionRequest>(ct))!;
            JunctionPosts.Add(body);
            Writes.Add("junction-post");
            // Come l'host vero (PlantQueries.AddJunctionAsync): si COLLASSA sulla coppia (origine,
            // destinazione) e, se la giunzione c'e' gia', restituisce quella esistente SENZA
            // toccarne la priorita'. Il gestore finto non lo faceva, e per questo nessuna prova
            // poteva vedere il verbale dichiarare una priorita' che sull'impianto non e' mai
            // arrivata.
            if (Junctions.FirstOrDefault(j => j.SourceItemId == body.SourceItemId
                    && j.DestinationItemId == body.DestinationItemId) is { } existing)
                return Json(existing, HttpStatusCode.Created);

            var created = new JunctionDto(Guid.NewGuid(), body.SourceItemId, body.DestinationItemId, body.Priority);
            Junctions.Add(created);
            return Json(created, HttpStatusCode.Created);
        }

        // PUT /plant/junctions/{id}: l'unica rotta che cambia davvero la priorita' di una giunzione
        // che esiste gia' (la POST si collassa sulla coppia e NON la tocca, si veda il ramo qui
        // sopra). Come l'host vero (PlantEndpoints): 204 se la giunzione c'e', 404 se non c'e'
        // piu' - e il 404 non e' un dettaglio, e' il caso che SetJunctionPriorityCommand tratta come
        // un fallimento esplicito invece che come un no-op silenzioso.
        if (method == HttpMethod.Put && segments is [.., "junctions", var priorityId] && Guid.TryParse(priorityId, out var priorityGuid))
        {
            if (Failed("junction-put") is { } refused) return refused;
            var body = (await request.Content!.ReadFromJsonAsync<JunctionRequest>(ct))!;
            var at = Junctions.FindIndex(j => j.JunctionId == priorityGuid);
            if (at < 0) return Status(HttpStatusCode.NotFound);
            JunctionPriorityWrites.Add((priorityGuid, body.Priority));
            Writes.Add("junction-put");
            Junctions[at] = Junctions[at] with { Priority = body.Priority };
            return Status(HttpStatusCode.NoContent);
        }

        if (method == HttpMethod.Delete && segments is [.., "junctions", var junctionId] && Guid.TryParse(junctionId, out var junctionGuid))
        {
            if (Failed("junction-delete") is { } refused) return refused;
            JunctionDeletes.Add(junctionGuid);
            Writes.Add("junction-delete");
            Junctions.RemoveAll(j => j.JunctionId == junctionGuid);
            return Status(HttpStatusCode.NoContent);
        }

        if (method == HttpMethod.Delete && segments is [.., "items", var itemId] && Guid.TryParse(itemId, out var itemGuid))
        {
            if (Failed("item-delete") is { } refused) return refused;
            ItemDeletes.Add(itemGuid);
            Writes.Add("item-delete");
            Items.RemoveAll(i => i.ItemId == itemGuid);
            // Come l'host vero (PlantStore.DeleteItemAsync): la cancellazione di un dispositivo
            // porta via anche le giunzioni che lo toccano.
            Junctions.RemoveAll(j => j.SourceItemId == itemGuid || j.DestinationItemId == itemGuid);
            return Status(HttpStatusCode.NoContent);
        }

        return Status(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Il dispositivo esiste per questo host finto: sta nell'elenco dell'impianto oppure ha una
    /// configurazione scritta. Tutti e due i posti, perche' i test seminano ora l'uno ora l'altra -
    /// e una rotta che rispondesse 404 a un dispositivo seminato solo in <see cref="Items"/>
    /// renderebbe rosse prove che non parlano di questo.
    /// </summary>
    private bool Knows(Guid itemId) => Configs.ContainsKey(itemId) || Items.Any(i => i.ItemId == itemId);

    /// <summary>
    /// Rewrites ONE device's door map the way <c>DoorMappingStore.ReplaceForItemAsync</c> does: the
    /// old rows out, the new rows in - and <b>the two states the host keeps for itself are
    /// preserved</b> for every new row that matches an old one by door and cell
    /// (<c>OperationActive</c>, <c>StoringLocked</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// That preservation is not a detail of the fake: it is the reason the page REREADS the map after
    /// writing it instead of keeping what it sent - the <c>DoorMappings</c> component builds its rows
    /// with zero and false, having no way of knowing either. A fake that did not preserve them would
    /// make that reread invisible: the map read back would be identical to the map sent, and the test
    /// that measures it could not fail.
    /// </para>
    /// <para>
    /// <b>The caller checks that the device exists, this method does not.</b> The three door-map
    /// routes above answer <c>404</c> through <see cref="Knows"/> before they get here, and that is
    /// the contract of the real host since the 404 batch: <c>DoorMapping.ItemId</c> now carries a
    /// foreign key towards <c>PlantItems</c> (<c>WcsDbContext</c>), so an orphan row can no longer be
    /// written at all. Until that batch the same three routes answered <c>204</c> and left orphan
    /// rows behind for a deleted device - which is what this comment used to describe.
    /// </para>
    /// </remarks>
    private void ReplaceDoorMappingsForItem(Guid itemId, IReadOnlyList<DoorMappingDto> wanted)
    {
        var existing = DoorMappings.Where(m => m.ItemId == itemId).ToList();
        DoorMappings.RemoveAll(m => m.ItemId == itemId);
        foreach (var row in wanted)
        {
            var previous = existing.FirstOrDefault(m => m.DoorCode == row.DoorCode && m.CellId == row.CellId);
            DoorMappings.Add(row with
            {
                OperationActive = previous?.OperationActive ?? row.OperationActive,
                StoringLocked = previous?.StoringLocked ?? row.StoringLocked,
            });
        }
    }

    /// <summary>
    /// Mette una geometria arrivata sul dispositivo a cui appartiene, come fa l'host vero: senza
    /// questo <see cref="Items"/> restava alla posizione di partenza, e la GET con cui
    /// <c>CreateItemCommand</c> rilegge il dispositivo appena creato tornava un riquadro a zero -
    /// cioe' un dispositivo che <c>DrawioDocument.IsDrawn</c> considera non disegnato, e che quindi
    /// spariva dal disegno subito dopo essere nato.
    /// <para>
    /// Non e' una comodita' per la creazione: e' il finto che smette di mentire. Una prova che
    /// salvava due volte vedeva il secondo salvataggio riproporre lo stesso spostamento, perche'
    /// l'host finto continuava a dichiarare la posizione vecchia.
    /// </para>
    /// </summary>
    private PlantItemGeometryVersionDto Place(PlantItemGeometryDto geometry)
    {
        var at = Items.FindIndex(i => i.ItemId == geometry.ItemId);
        if (at < 0) throw new InvalidOperationException($"Il dispositivo {geometry.ItemId} non esiste nel finto.");
        var version = Guid.NewGuid();
        Items[at] = Items[at] with
        {
            X = geometry.X ?? Items[at].X,
            Y = geometry.Y ?? Items[at].Y,
            Width = geometry.Width ?? Items[at].Width,
            Height = geometry.Height ?? Items[at].Height,
            Rotation = geometry.Rotation ?? Items[at].Rotation,
            GeometryVersion = version
        };
        return new PlantItemGeometryVersionDto(geometry.ItemId, version);
    }

    private static PlantItemGeometryDto GeometryOf(PlantItemGeometryUpdateDto update) =>
        new(update.ItemId, update.X, update.Y, update.Width, update.Height, update.Rotation);

    /// <summary>
    /// Trattiene una scrittura della geometria su <see cref="GeometryGate"/>, se c'e'. Sta qui e non
    /// nei due rami perche' le rotte della geometria sono DUE (una sola geometria, o tutte insieme)
    /// e il comando sceglie da solo quale usare: un cancello su una sola delle due si sarebbe fatto
    /// aspettare per sempre da una prova che credeva di averlo armato - misurato.
    /// </summary>
    private async Task HoldAtGeometryGateAsync()
    {
        if (GeometryGate is null) return;
        GeometryGateReached.TrySetResult();
        await GeometryGate.Task;
    }

    /// <summary>
    /// Trattiene una creazione su <see cref="ItemPostGate"/>, se c'e'. E' PRIMA della lettura del
    /// corpo apposta: la richiesta e' gia' partita dal prodotto, quindi la pagina ha gia' alzato il
    /// suo cancello, ed e' quello che le prove dell'accavallamento vogliono vedere.
    /// </summary>
    private async Task HoldAtItemPostGateAsync()
    {
        // The gate is read once, before it is said to be reached: a test that puts a second gate in
        // place as soon as the first POST is held must not have that POST caught by the second one.
        if (ItemPostGate is not { } gate) return;
        ItemPostGateReached.TrySetResult();
        await gate.Task;
    }

    /// <summary>
    /// Trattiene la PRIMA richiesta del permesso su <see cref="EditLockGate"/>, se c'e', e poi lo
    /// disarma: si veda il campo per il perche' una volta sola.
    /// </summary>
    private async Task HoldAtEditLockGateAsync()
    {
        // Consumato con una bandiera e non azzerando la proprieta': la prova che lo ha armato deve
        // poterlo aprire dalla stessa proprieta', come fa con gli altri cancelli.
        if (EditLockGate is not { } gate || _editLockGateUsed) return;
        _editLockGateUsed = true;
        EditLockGateReached.TrySetResult();
        await gate.Task;
    }

    private bool _editLockGateUsed;

    /// <summary>
    /// Holds the FIRST read of the door map on <see cref="DoorMappingReadGate"/>, if there is one,
    /// and then disarms itself: see the field for why only one.
    /// </summary>
    private async Task HoldAtDoorMappingReadGateAsync()
    {
        // Consumed with a flag and not by clearing the property, so that the test which armed it can
        // still open it through the same property - the same shape as the permit's gate.
        if (DoorMappingReadGate is not { } gate || _doorMappingReadGateUsed) return;
        _doorMappingReadGateUsed = true;
        DoorMappingReadGateReached.TrySetResult();
        await gate.Task;
    }

    private bool _doorMappingReadGateUsed;

    /// <summary>Holds the FIRST read of the live statuses on <see cref="ItemStatusGate"/>, if there is one, and then disarms itself.</summary>
    private async Task HoldAtItemStatusGateAsync()
    {
        if (ItemStatusGate is not { } gate || Interlocked.Exchange(ref _itemStatusGateUsed, 1) == 1) return;
        ItemStatusGateReached.TrySetResult();
        await gate.Task;
    }

    private int _itemStatusGateUsed;

    /// <summary>Holds the FIRST read of the engine status on <see cref="EngineStatusGate"/>, if there is one, and then disarms itself.</summary>
    private async Task HoldAtEngineStatusGateAsync()
    {
        if (EngineStatusGate is not { } gate || Interlocked.Exchange(ref _engineStatusGateUsed, 1) == 1) return;
        EngineStatusGateReached.TrySetResult();
        await gate.Task;
    }

    /// <summary>Holds the FIRST read of a command on <see cref="CommandReadGate"/>, if there is one, and then disarms itself.</summary>
    private async Task HoldAtCommandReadGateAsync()
    {
        if (CommandReadGate is not { } gate || Interlocked.Exchange(ref _commandReadGateUsed, 1) == 1) return;
        CommandReadGateReached.TrySetResult();
        await gate.Task;
    }

    /// <summary>
    /// La risposta di guasto se questa e' la rotta armata da <see cref="FailRoute"/> - una volta
    /// sola, poi la rotta torna normale - e i due modi in cui invece non arriva nessuna risposta,
    /// anch'essi una volta sola. <see cref="TimeoutRoute"/>: solleva la stessa
    /// <see cref="TaskCanceledException"/> con cui <see cref="HttpClient"/> annuncia il proprio
    /// timeout. <see cref="HttpFailureRoute"/>: solleva un <see cref="HttpRequestException"/> del
    /// genere dichiarato in <see cref="HttpFailureKind"/>, cioe' un guasto del TRASPORTO - che a
    /// seconda del genere vuol dire "non si sa se sia passata" oppure "non e' mai partita".
    /// </summary>
    private HttpResponseMessage? Failed(string route)
    {
        if (TimeoutRoute == route)
        {
            TimeoutRoute = null;
            throw new TaskCanceledException($"La richiesta su '{route}' e' scaduta senza risposta (prova).");
        }

        if (HttpFailureRoute == route)
        {
            HttpFailureRoute = null;
            throw new HttpRequestException(HttpFailureKind,
                $"Il trasporto su '{route}' e' fallito ({HttpFailureKind}) (prova).");
        }

        if (FailRoute != route) return null;
        FailRoute = null;
        return new HttpResponseMessage(FailureStatus) { Content = new StringContent(FailureBody) };
    }

    /// <summary>
    /// La risposta 200 con un corpo che non e' JSON, se questa e' la rotta armata da
    /// <see cref="GarbledRoute"/> - una volta sola, poi la rotta torna normale.
    /// </summary>
    private HttpResponseMessage? Garbled(string route)
    {
        if (GarbledRoute != route) return null;
        GarbledRoute = null;
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html><body>Accedi al portale per continuare</body></html>"),
        };
    }

    /// <summary>
    /// An answer the way ASP.NET Core's <c>Results.Problem(text, statusCode: ...)</c> writes it: its
    /// first parameter is the DETAIL, so the host's words land there, and the title is the status's
    /// own default ("Bad Request", "Conflict").
    /// </summary>
    private static HttpResponseMessage Problem(string detail, HttpStatusCode status) =>
        new(status)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    type = "https://tools.ietf.org/html/rfc9110",
                    title = status == HttpStatusCode.Conflict ? "Conflict" : "Bad Request",
                    status = (int)status,
                    detail,
                }),
                System.Text.Encoding.UTF8, "application/problem+json"),
        };

    /// <summary>
    /// The warehouses a choice names, as MissionGateway.StoragesOf: the storage itself, or the storages of
    /// a group chosen.
    /// </summary>
    private List<Guid> StoragesOf(PlantItemDto chosen) => chosen.DeviceType switch
    {
        DeviceTypes.Storage => [chosen.ItemId],
        DeviceTypes.GroupOfAutomationDevices when chosen.GroupItemId != 0 =>
            [.. Items.Where(i => i.BelongingGroupId == chosen.GroupItemId && i.DeviceType == DeviceTypes.Storage).Select(i => i.ItemId)],
        _ => [],
    };

    /// <summary>A relocator, as MissionGateway.IsARelocator: a warehouse names it as its relocator.</summary>
    private bool IsARelocator(Guid itemId) =>
        Items.Any(i => i.DeviceType == DeviceTypes.Storage && i.RelocateItemId == itemId);

    /// <summary>The 422 of a precise cell refused, as MissionEndpoints.DestinationAnswer writes it: the title, the words, the field.</summary>
    private static HttpResponseMessage CellRefused(string code, string refusal) =>
        DestinationProblem(HttpStatusCode.UnprocessableEntity, MissionDestinationProblems.CellRefusedTitle,
            $"La cella {code} non è stata prenotata: {refusal}",
            new Dictionary<string, object?> { [MissionDestinationProblems.CellRefusalExtension] = refusal });

    /// <summary>
    /// The door's device is in the warehouse chosen, as MissionGateway.IsInChosenStorage: the warehouse
    /// itself, a member of the group chosen, or a sibling in the chosen warehouse's group.
    /// </summary>
    private bool IsInChosenStorage(Guid doorDevice, PlantItemDto chosen)
    {
        if (doorDevice == chosen.ItemId) return true;
        var groupOfTheDoor = Items.FirstOrDefault(i => i.ItemId == doorDevice)?.BelongingGroupId ?? 0;
        if (groupOfTheDoor == 0) return false;
        return chosen.DeviceType == DeviceTypes.GroupOfAutomationDevices && chosen.GroupItemId == groupOfTheDoor
               || chosen.BelongingGroupId == groupOfTheDoor;
    }

    /// <summary>
    /// An extraction whose load unit is still in its cell (PathMission.LoadUnitIsInItsStorageCell): on a
    /// warehouse, or nowhere with a warehouse as its source. Sent to a warehouse it becomes a change of
    /// cell, which does not keep a cell.
    /// </summary>
    private bool StillInItsCell(MissionDto mission) =>
        mission.MissionType == MissionTypes.Extraction
        && (Items.FirstOrDefault(i => i.ItemId == mission.PositionItemId) is { } position
            ? position.DeviceType == DeviceTypes.Storage
            : Items.FirstOrDefault(i => i.ItemId == mission.SourceItemId) is { DeviceType: DeviceTypes.Storage });

    /// <summary>
    /// The engine's rule for "arrived" (PathMission.Reaches): a load unit on <paramref name="position"/>
    /// has reached <paramref name="target"/> when it is that device, or a device of the group that is
    /// <paramref name="target"/>.
    /// </summary>
    private bool Reaches(Guid? position, Guid target) =>
        position is { } at && at != Guid.Empty && target != Guid.Empty &&
        (at == target || Items.FirstOrDefault(i => i.ItemId == at) is { BelongingGroupId: not 0 } member &&
            Items.Any(g => g.ItemId == target && g.DeviceType == DeviceTypes.GroupOfAutomationDevices && g.GroupItemId == member.BelongingGroupId));

    /// <summary>
    /// A refusal of the destination's route as the real host writes it
    /// (<c>Results.Problem(detail, statusCode, title, extensions)</c>): its own title per outcome, and
    /// the fields it adds at the document's top level.
    /// </summary>
    private static HttpResponseMessage DestinationProblem(HttpStatusCode status, string title, string detail,
        IReadOnlyDictionary<string, object?>? fields = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["type"] = "https://tools.ietf.org/html/rfc9110",
            ["title"] = title,
            ["status"] = (int)status,
            ["detail"] = detail,
        };
        foreach (var (name, value) in fields ?? new Dictionary<string, object?>()) body[name] = value;
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/problem+json"),
        };
    }

    /// <summary>
    /// The way the navigator would find from one device to another, as this fake knows the plant: along
    /// <see cref="Junctions"/>, in the fewest hops, through <paramref name="via"/> when there is one
    /// that is not where the load unit already is (the engine drops a gateway already reached). The
    /// hops, or null when there is no way.
    /// </summary>
    private List<(Guid From, Guid To)>? RouteOf(Guid? from, Guid to, Guid? via)
    {
        if (from is not { } start) return null;
        if (via is not { } gateway || gateway == Guid.Empty || gateway == start) return WayOf(start, to);
        return WayOf(start, gateway) is { } first && WayOf(gateway, to) is { } second ? [.. first, .. second] : null;
    }

    private List<(Guid From, Guid To)>? WayOf(Guid from, Guid to)
    {
        if (from == to) return [];
        var cameFrom = new Dictionary<Guid, Guid> { [from] = from };
        var queue = new Queue<Guid>([from]);
        while (queue.TryDequeue(out var at))
        {
            foreach (var next in Junctions.Where(junction => junction.SourceItemId == at).Select(junction => junction.DestinationItemId))
            {
                if (!cameFrom.TryAdd(next, at)) continue;
                if (next != to)
                {
                    queue.Enqueue(next);
                    continue;
                }

                var hops = new List<(Guid From, Guid To)>();
                for (var hop = to; hop != from; hop = cameFrom[hop]) hops.Insert(0, (cameFrom[hop], hop));
                return hops;
            }
        }

        return null;
    }

    /// <summary>A route's hops as the steps <c>GET /missions/{id}/steps</c> hands back: none started yet.</summary>
    private static List<MissionStepDto> StepsOf(Guid missionId, List<(Guid From, Guid To)> route) =>
        [.. route.Select(hop => TestMissions.Step(Guid.NewGuid(), missionId, hop.From, hop.To))];

    private static HttpResponseMessage Status(HttpStatusCode status) =>
        new(status) { Content = new StringContent(string.Empty) };

    private static HttpResponseMessage Json<T>(T body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body)) };
}
