using System.Text.Json.Serialization;
using static Wcs.Common.Enums;

namespace Wcs.Api.Contracts.Dtos;

/// <summary>Una missione attiva: le colonne di <c>Mission</c> del motore, con lo stesso nome.</summary>
/// <param name="OperatorDestination">
/// Il segno della destinazione scelta dall'operatore (<c>PathMission.OperatorDestination</c> del
/// motore): vero finche' la destinazione e' quella che l'operatore ha scelto con un cambio di
/// destinazione. Lo stesso magazzino scelto di nuovo con il segno chiede un'altra cella; senza,
/// chiede di nuovo una cella, forse la stessa (voce 44 della coda, decisione del proprietario del 10/10).
/// </param>
/// <param name="PendingChange">
/// Item 54, decision 4 of the owner of 13/10: a change the operator asked while a step of the mission
/// was in flight, which waits for the end of the step and is applied from where it ends - a change of
/// destination or a move; null when none waits. Only in memory on the host: a restart loses it, and the
/// row shows it while it exists, so that its disappearance is seen (owner's answer 4 of 13/10).
/// </param>
/// <param name="PendingChangeItemId">The device of <paramref name="PendingChange"/>: the destination chosen, or the device to move onto.</param>
/// <param name="RelocatorArrival">
/// Item 54, decision 5 of the owner of 13/10: what the relocator the operator sent the mission to does
/// with the load unit - deposit it, or keep it there; null when the operator chose nothing. It stays
/// after the arrival (it is where it applies), and falls when the destination changes.
/// </param>
public sealed record MissionDto(Guid MissionId, decimal LoadUnitId, string? LoadUnitCode, Guid? PositionItemId,
    Guid? SourceItemId, Guid? DestinationItemId, DateTime Date, string? StatusDescription, string? StorageDoorCode,
    string? PickUpDoorCode, MissionStatusTypes Status, decimal? CommandId, Guid? RelocateItemId,
    MissionTypes MissionType, string? MovingDescription, OperationTypes OperationType, decimal BufferCellId,
    decimal ExtractionQueueId, decimal LoadUnitTypeId, Guid? GroupedMovementId, int HandlingMode,
    Guid? GatewayItemId, string? SourceAsi, string? SourceSubitem, string? SourcePartition,
    string? DestinationAsi, string? DestinationSubitem, string? DestinationPartition, decimal NoteAssignmentId,
    Guid? AssignmentGuid, decimal NoteProcessingId, bool ActiveFilter, decimal Tare, bool ReallocatedDestination,
    bool OperatorDestination, MissionPendingChangeKind? PendingChange = null, Guid? PendingChangeItemId = null,
    MissionRelocatorArrival? RelocatorArrival = null);

/// <summary>What a change waiting for the end of the mission's step does (<see cref="MissionDto.PendingChange"/>, item 54).</summary>
public enum MissionPendingChangeKind
{
    /// <summary>A change of destination (<c>PUT /missions/{id}/destination</c>).</summary>
    Destination,

    /// <summary>A move onto another device (<c>PUT /missions/{id}/position</c>).</summary>
    Position
}

/// <summary>
/// When a change asked with <c>PUT /missions/{id}/position</c> or <c>PUT /missions/{id}/destination</c>
/// takes effect: at once (204), or at the end of the step of the mission that is in flight (202, item 54,
/// decision 4 of the owner of 13/10 - the change waits instead of being refused, and the mission's row
/// shows it in <see cref="MissionDto.PendingChange"/> while it waits).
/// </summary>
public enum MissionChangeApplied
{
    /// <summary>204: the change is made.</summary>
    Now,

    /// <summary>202: a step of the mission is in flight; the change is applied as soon as it ends, from where it ends.</summary>
    AtTheEndOfTheMovement
}

/// <summary>Il corpo di <c>PUT /missions/{id}/position</c>: il dispositivo su cui portare la missione.</summary>
public sealed record MissionPositionUpdate(Guid ItemId);

/// <summary>
/// I titoli dei rifiuti di <c>PUT /missions/{id}/position</c> che la pagina deve riconoscere. Gli
/// altri rifiuti dello spostamento portano il titolo predefinito del loro stato HTTP e le parole
/// dell'host nel dettaglio; questo no, perche' lo stesso 503 arriva sulla rotta anche con il motore
/// non caricato o senza il database del motore, che non sono un «riprova tra poco».
/// </summary>
public static class MissionMoveProblems
{
    /// <summary>
    /// 503: il motore e' in Recover, sta ancora caricando le missioni, e lo spostamento e' rifiutato
    /// senza toccare niente (risposta 4 del proprietario dell'8/10). Da riprovare tra poco.
    /// </summary>
    public const string EngineRecoveringTitle = "Il motore sta ancora caricando le missioni";
}

/// <summary>
/// Il corpo di <c>PUT /missions/{id}/destination</c>: il dispositivo verso cui mandare la missione
/// - o un magazzino, o un gruppo di magazzini, e allora la cella la sceglie il gestionale - e che
/// cosa fare del suo gateway. Senza <see cref="Gateway"/> il gateway che la missione ha resta.
/// </summary>
/// <param name="AtTheRelocator">
/// Item 54, decision 5 of the owner of 13/10: with a relocator as <paramref name="ItemId"/>, what it does
/// with the load unit - <c>"deposit"</c> (the warehouse gives a cell the relocator reaches, and the
/// relocator deposits it) or <c>"stay"</c> (only bring it there). Null: the relocator does what its
/// configuration says, as before. Towards a device that is not a relocator the answer is 400
/// (<see cref="MissionDestinationProblems.NotARelocatorTitle"/>).
/// </param>
/// <param name="CellDoorCode">
/// Item 54, decision 6: a precise storage cell, by its door A code (owner's answer 7 of 13/10), chosen
/// from <c>GET /missions/{id}/storage-cells</c> or written by the operator; <paramref name="ItemId"/> is
/// then the storage, the group of storages or the relocator it is chosen in. The warehouse reserves that
/// cell or refuses it with the reason (422 <see cref="MissionDestinationProblems.CellRefusedTitle"/>).
/// Not together with <paramref name="AtTheRelocator"/>, and not blank (400
/// <see cref="MissionDestinationProblems.InvalidDestinationChoiceTitle"/>).
/// </param>
public sealed record MissionDestinationUpdate(Guid ItemId, MissionGatewayUpdate? Gateway = null,
    MissionRelocatorArrival? AtTheRelocator = null, string? CellDoorCode = null);

/// <summary>
/// What a relocator does with the load unit the operator sends to it (item 54, decision 5 of the owner of
/// 13/10). On the wire the name in lower case, like <see cref="MissionGatewayUpdateMode"/>: no numbers.
/// </summary>
[JsonConverter(typeof(MissionRelocatorArrivalConverter))]
public enum MissionRelocatorArrival
{
    /// <summary>Deposit it: the warehouse gives a cell the relocator reaches, and the relocator deposits it there.</summary>
    [JsonStringEnumMemberName("deposit")] Deposit,

    /// <summary>Only bring it there: the load unit arrives at the relocator and stays.</summary>
    [JsonStringEnumMemberName("stay")] Stay
}

/// <summary>The converter of <see cref="MissionRelocatorArrival"/>: the names of the contract, and no number.</summary>
public sealed class MissionRelocatorArrivalConverter()
    : JsonStringEnumConverter<MissionRelocatorArrival>(namingPolicy: null, allowIntegerValues: false);

/// <summary>
/// A storage cell the operator can choose for the mission's load unit (item 54, decision 6): one row of
/// <c>GET /missions/{id}/storage-cells?itemId=</c>, the first 20 the warehouse proposes with the search of
/// every day (owner's answer 6 of 13/10), in the storage, the group of storages or among the cells of the
/// relocator chosen. Nothing is reserved: the choice goes back in <see cref="MissionDestinationUpdate.CellDoorCode"/>.
/// </summary>
/// <param name="CellId">The cell.</param>
/// <param name="DoorCode">Its door A code, the code the change of destination takes.</param>
/// <param name="AreaId">The warehouse area of the cell.</param>
/// <param name="AreaDescription">The description of the area.</param>
/// <param name="Column">The column of the cell in its area.</param>
/// <param name="Floor">The floor of the cell in its area.</param>
/// <param name="Description">The description of the cell, when it has one.</param>
/// <param name="LoadUnitsInCell">How many load units the cell holds already.</param>
public sealed record StorageCellCandidateDto(decimal CellId, string DoorCode, decimal AreaId, string AreaDescription,
    int Column, int Floor, string? Description, int LoadUnitsInCell);

/// <summary>
/// The titles of the refusals of <c>GET /missions/{id}/storage-cells</c> (item 54, decision 6), one per
/// outcome of the engine; the 404 of a mission that is not active has no body, as for <c>GET /missions/{id}</c>,
/// and the warehouse that does not answer is the 503 of <see cref="MissionDestinationProblems.WarehouseUnavailableTitle"/>.
/// </summary>
public static class StorageCellCandidatesProblems
{
    /// <summary>400: the device chosen does not exist.</summary>
    public const string DeviceNotFoundTitle = "Dispositivo sconosciuto";

    /// <summary>400: the device chosen is not a storage, a group of storages or a relocator: it has no cells.</summary>
    public const string NotAStorageTitle = "Il dispositivo non ha celle";

    /// <summary>
    /// 422: nothing to ask the warehouse - the load unit has no identifier of the warehouse, or the
    /// storages chosen have no door mapped to a cell, or their cell has no warehouse.
    /// </summary>
    public const string NoStorageCellTitle = "Nessuna cella da chiedere al gestionale";
}

/// <summary>
/// La scelta dell'operatore sul gateway della missione, che viaggia con il cambio di destinazione
/// (decisione del proprietario del 30/9, sera): sul filo <c>{ "mode": "keep" | "clear" | "set",
/// "itemId": ... }</c>. <see cref="ItemId"/> va con <see cref="MissionGatewayUpdateMode.Set"/> e
/// solo con lui: un dispositivo da tenere o da togliere, o un «imposta» senza dispositivo, e' una
/// richiesta sbagliata (400), non una scelta da indovinare.
/// </summary>
public sealed record MissionGatewayUpdate(MissionGatewayUpdateMode Mode, Guid? ItemId = null)
{
    public static MissionGatewayUpdate Keep { get; } = new(MissionGatewayUpdateMode.Keep);

    public static MissionGatewayUpdate Clear { get; } = new(MissionGatewayUpdateMode.Clear);

    public static MissionGatewayUpdate Set(Guid itemId) => new(MissionGatewayUpdateMode.Set, itemId);
}

/// <summary>
/// Che cosa fare del gateway. Sul filo e' il nome in minuscolo, non il numero come per gli enum del
/// motore: e' la forma che il contratto della rotta scrive (<c>keep|clear|set</c>), e chi la chiama
/// da uno script la legge senza una tabella. I nomi si leggono esattamente cosi': <c>"Keep"</c> non
/// si lega, e nemmeno un numero (<c>0</c>), e la richiesta e' rifiutata. Il convertitore sta sul
/// tipo (<see cref="MissionGatewayUpdateModeConverter"/>), cosi' vale ovunque il tipo passi da
/// System.Text.Json, senza opzioni da ricordare: l'host, il client, e chi ne legge il corpo.
/// </summary>
[JsonConverter(typeof(MissionGatewayUpdateModeConverter))]
public enum MissionGatewayUpdateMode
{
    /// <summary>Tenere il gateway che la missione ha, anche nessuno: la scelta di sempre.</summary>
    [JsonStringEnumMemberName("keep")] Keep,

    /// <summary>Toglierlo.</summary>
    [JsonStringEnumMemberName("clear")] Clear,

    /// <summary>Impostare il dispositivo di <see cref="MissionGatewayUpdate.ItemId"/>, al posto di quello che c'e' o dove non c'era.</summary>
    [JsonStringEnumMemberName("set")] Set
}

/// <summary>
/// Il convertitore di <see cref="MissionGatewayUpdateMode"/>: i nomi del contratto, e nessun numero.
/// Quello di serie accetta anche i numeri (<c>0</c> diventava «keep»), cioe' una forma che il
/// contratto non scrive e che nessuno ha scelto.
/// </summary>
public sealed class MissionGatewayUpdateModeConverter()
    : JsonStringEnumConverter<MissionGatewayUpdateMode>(namingPolicy: null, allowIntegerValues: false);

/// <summary>
/// I titoli dei rifiuti di <c>PUT /missions/{id}/destination</c>, uno per esito del motore, e i nomi
/// dei campi che alcuni aggiungono al <c>ProblemDetails</c>. Lo stato HTTP da solo non basta a chi
/// deve dire all'operatore che cosa e' successo - il 409 copre quattro esiti, il 422 due, il 400 tre
/// - e la pagina distingue i rifiuti dal titolo, come vuole la specifica delle missioni; i campi
/// li legge invece delle parole del dettaglio, che restano per le persone.
/// </summary>
public static class MissionDestinationProblems
{
    /// <summary>404: la missione non e' (piu') fra le attive.</summary>
    public const string MissionNotFoundTitle = "Missione non attiva";

    /// <summary>400: la destinazione scelta non e' un dispositivo.</summary>
    public const string DeviceNotFoundTitle = "Destinazione sconosciuta";

    /// <summary>400: il gateway da impostare non e' un dispositivo.</summary>
    public const string GatewayNotFoundTitle = "Gateway sconosciuto";

    /// <summary>400: la scelta sul gateway non e' ben formata (si veda <see cref="MissionGatewayUpdate"/>).</summary>
    public const string InvalidGatewayChoiceTitle = "Scelta del gateway non valida";

    /// <summary>
    /// 409: la missione e' stata spostata mentre il cambio era in corso, or another cause of the engine
    /// that passes by itself. Da riprovare quando finisce. A step of the mission in flight, or being
    /// started, when the change is asked is no longer a refusal: since item 54 (decision 4) the answer is
    /// 202 and the change waits for the end of the step (<see cref="MissionChangeApplied"/>).
    /// </summary>
    public const string StepInFlightTitle = "Passo della missione in esecuzione";

    /// <summary>
    /// 409: un'altra UDC dello stesso gruppo, la cui missione ha copiato la porta di questa (la stessa
    /// cella), si sta muovendo e non si puo' fermare adesso. La missione scelta e' ferma, e il cambio
    /// non ha toccato niente: da riprovare fra un momento (specifica delle missioni, risposta 4 del
    /// 9/10). Prima era <see cref="StepInFlightTitle"/>, e la pagina diceva che si muoveva la missione
    /// dell'operatore.
    /// </summary>
    public const string CopyInFlightTitle = "Un'altra UDC della stessa cella in movimento";

    /// <summary>
    /// 409: per la missione e' in corso una ricerca della cella di deposito, o un altro cambio di
    /// destinazione, o uno spostamento che ne costruisce la rotta. Da riprovare quando finisce.
    /// </summary>
    public const string CellSearchInFlightTitle = "Ricerca della cella o altro cambio in corso";

    /// <summary>409: la destinazione scelta e' la posizione dell'UDC, o il gruppo della sua posizione.</summary>
    public const string AlreadyThereTitle = "UDC già nella destinazione";

    /// <summary>422: dalla posizione non c'e' percorso, con il gateway scelto. Porta <see cref="GatewayExtension"/> e <see cref="WouldRouteWithoutGatewayExtension"/>.</summary>
    public const string NoRouteTitle = "Nessun percorso verso la destinazione";

    /// <summary>422: nessuna cella da prenotare nel magazzino scelto (il perche' e' nel registro del motore). Porta <see cref="NoOtherFreeCellExtension"/>.</summary>
    public const string NoStorageCellTitle = "Nessuna cella da prenotare nel magazzino";

    /// <summary>
    /// 503: il gestionale non risponde, o non e' configurato. E' lo stesso titolo del 503 che l'host
    /// scrive quando una rotta solleva il guasto del gestionale (<c>EngineExceptionHandler</c>, che usa
    /// questa costante): chi lo riconosce la', lo riconosce anche qui. Lo stesso 503 puo' arrivare da
    /// questa rotta con altri titoli - il motore non caricato, il database del motore che non c'e' -:
    /// quale dei due sia, gestionale o motore non caricato, lo dice il campo
    /// <see cref="ServiceUnavailableProblems.ReasonExtension"/> (voce 34), che le pagine leggono al posto del titolo.
    /// </summary>
    public const string WarehouseUnavailableTitle = "Il gestionale non è raggiungibile dal motore";

    /// <summary>
    /// 422: il gestionale ha risposto di no alla prima segnalazione del cambio cella di un'estrazione
    /// con l'UDC ancora nella sua cella (la missione lascia l'ordine). Non l'ha applicata: il cambio e'
    /// rifiutato e la missione resta l'estrazione che era. Non e' un gestionale fermo, e la pagina lo
    /// dice diversamente da <see cref="WarehouseUnavailableTitle"/> (specifica delle missioni, risposta 4
    /// dell'8/10 sera).
    /// </summary>
    public const string WarehouseRefusedTitle = "Il gestionale ha rifiutato il cambio";

    /// <summary>
    /// 400 (item 54): the choice that goes with the destination is not well formed - a relocator's arrival
    /// and a precise cell together, or a blank cell code.
    /// </summary>
    public const string InvalidDestinationChoiceTitle = "Scelta della destinazione non valida";

    /// <summary>
    /// 400 (item 54, decision 5): a relocator's arrival (<see cref="MissionDestinationUpdate.AtTheRelocator"/>)
    /// was chosen for a device that is not a relocator - no storage names it.
    /// </summary>
    public const string NotARelocatorTitle = "Il dispositivo non è un ricollocatore";

    /// <summary>
    /// 422 (item 54, decision 6): the precise cell (<see cref="MissionDestinationUpdate.CellDoorCode"/>) is
    /// refused, by the warehouse or by the WCS before asking it; the detail says why in words, and
    /// <see cref="CellRefusalExtension"/> says it for the page.
    /// </summary>
    public const string CellRefusedTitle = "Cella rifiutata";

    /// <summary>
    /// Of the 422 <see cref="CellRefusedTitle"/>: why the cell was refused - <c>unknownDoor</c>,
    /// <c>notADoorA</c>, <c>cellNotAvailable</c>, <c>unknownLoadUnit</c> or <c>notInTheChoice</c> (the cell
    /// is not in the storage chosen, nor served by the relocator chosen).
    /// </summary>
    public const string CellRefusalExtension = "cellRefusal";

    /// <summary>Del 422 <see cref="NoRouteTitle"/>: il gateway con cui si e' cercato il percorso (un Guid), o <c>null</c> se nessuno.</summary>
    public const string GatewayExtension = "gatewayItemId";

    /// <summary>Del 422 <see cref="NoRouteTitle"/>: senza quel gateway il percorso c'e' (la pagina propone allora di toglierlo).</summary>
    public const string WouldRouteWithoutGatewayExtension = "wouldRouteWithoutGateway";

    /// <summary>
    /// Del 422 <see cref="NoStorageCellTitle"/>: l'operatore ha scelto di nuovo il magazzino dove la
    /// missione sta gia' andando, cioe' ha chiesto un'altra cella, e un'altra libera non c'e'; la
    /// cella che la missione ha resta sua (decisione del proprietario dell'1/10).
    /// </summary>
    public const string NoOtherFreeCellExtension = "noOtherFreeCell";

    /// <summary>
    /// Di un rifiuto del cambio verso un magazzino arrivato dopo la prenotazione (un 503, un 422, un
    /// 409): la missione ha perso la cella che teneva - la prenotazione l'aveva gia' lasciata - e il
    /// motore gliene prenota un'altra nel magazzino della sua destinazione. C'e' solo quando e' vero.
    /// </summary>
    public const string LostTheCellExtension = "lostTheCell";

    /// <summary>
    /// Del rifiuto del cambio cella di un'estrazione con l'UDC ancora nella sua cella, arrivato dopo
    /// che il gestionale aveva saputo che la missione lascia l'ordine (un 503, un 422, un 409): la
    /// missione e' chiusa, e l'UDC resta nella sua cella senza ordine (risposta del proprietario del
    /// 2/10 sulla domanda del compito 4 della voce 39: «il rifiuto lo dice»). C'e' solo quando e' vero.
    /// </summary>
    public const string MissionClosedExtension = "missionClosed";
}
