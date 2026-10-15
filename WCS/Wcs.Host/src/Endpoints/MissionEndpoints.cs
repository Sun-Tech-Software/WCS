using Wcs.Api.Contracts;
using Wcs.Api.Contracts.Dtos;
using Wcs.Engine.Api;
using Wcs.Host.Mapping;
using Wcs.Host.Security;
using static Wcs.Common.Enums;

namespace Wcs.Host.Endpoints;

internal static class MissionEndpoints
{
    public static RouteGroupBuilder MapMissions(this RouteGroupBuilder api)
    {
        var group = api.MapGroup(ApiRoutes.Missions);
        group.MapGet("/", (decimal? udc, Guid? itemId, MissionTypes? type) =>
            MissionQueries.Active(udc, itemId, type).Select(DtoMapping.ToDto).ToList()).WithName("ListMissions");
        group.MapGet("/exists", (decimal udc, MissionStatusTypes[]? status) => MissionQueries.ExistsForLoadUnit(udc, status)).WithName("MissionExists");

        // La domanda larga, che non e' /exists con un altro filtro: conta le missioni a qualunque
        // stato e guarda anche i due posti fuori dall'elenco delle attive. Senza stati da passare,
        // quindi senza il parametro ripetuto.
        group.MapGet("/holds", (decimal udc, decimal? excludeAssignmentId) =>
            MissionQueries.HoldsLoadUnit(udc, excludeAssignmentId)).WithName("MissionHoldsLoadUnit");

        // Gli step di tutte le missioni attive in una richiesta sola: chi ricostruisce la vista
        // completa li vuole tutti, e chiederli missione per missione è una richiesta per missione.
        group.MapGet("/steps", () => MissionQueries.AllSteps().Select(DtoMapping.ToDto).ToList()).WithName("ListAllMissionSteps");

        group.MapGet("/{id:guid}", (Guid id) =>
            MissionQueries.Find(id) is { } mission ? Results.Ok(DtoMapping.ToDto(mission)) : Results.NotFound()).WithName("GetMission");

        // Una missione che non è più attiva non ha «zero step»: non c'è. La lista vuota di prima
        // non si distingueva da una missione viva senza step ancora costruiti.
        group.MapGet("/{id:guid}/steps", (Guid id) =>
            MissionQueries.Find(id) == null
                ? Results.NotFound()
                : Results.Ok(MissionQueries.Steps(id).Select(DtoMapping.ToDto).ToList())).WithName("GetMissionSteps");

        // Lo spostamento di una missione da fuori (l'Editor, dalla lista delle missioni in corso):
        // prima dell'estrazione era un'assegnazione diretta sull'oggetto vivo, ora passa di qui.
        // In Recover e' rifiutato con 503, lo stato dei comandi che richiedono Run (CommandEndpoints),
        // e con un titolo suo: la pagina lo distingue dagli altri 503 della rotta (motore non
        // caricato, database del motore assente) e lo dice come un «riprova tra poco».
        group.MapPut("/{id:guid}/position", (Guid id, MissionPositionUpdate update) => MissionGateway.Move(id, update.ItemId) switch
        {
            MissionMoveOutcome.Moved => Results.NoContent(),
            // Item 54, decision 4: a step of the mission is in flight, and the move waits for its end.
            MissionMoveOutcome.Pending => Results.Accepted(),
            MissionMoveOutcome.MissionNotFound => Results.NotFound(),
            MissionMoveOutcome.DeviceNotFound => Results.Problem($"Il dispositivo {update.ItemId} non esiste", statusCode: StatusCodes.Status400BadRequest),
            MissionMoveOutcome.DeviceOccupied => Results.Problem($"Il dispositivo {update.ItemId} porta già un'altra missione", statusCode: StatusCodes.Status409Conflict),
            MissionMoveOutcome.EngineRecovering => Results.Problem(
                "Il motore è in Recover: sta ancora caricando le missioni, e lo spostamento non è stato fatto. Riprovare tra poco.",
                statusCode: StatusCodes.Status503ServiceUnavailable, title: MissionMoveProblems.EngineRecoveringTitle),
            var other => throw new InvalidOperationException($"Esito non previsto: {other}")
        }).DoesNotRequireEditPermission("sposta una missione in corso: lavoro del motore, non configurazione")
        .WithName("SetMissionPosition");

        // Il cambio di destinazione (piano delle missioni del 30/9, compito 5): come lo spostamento,
        // lavoro del motore e non configurazione, in qualunque stato del motore. Il gettone della
        // richiesta HTTP non arriva al motore, apposta: verso un magazzino il cambio prenota una
        // cella nel gestionale, e una prenotazione interrotta dal client che chiude, con la risposta
        // persa, lascerebbe una cella appesa. Il motore finisce il suo lavoro anche se nessuno
        // aspetta piu' la risposta.
        //
        // Item 54 (decisions 5 and 6 of the owner of 13/10): the body can say what a relocator does with
        // the load unit, or name a precise cell; a choice that is not well formed is a 400 before the engine.
        group.MapPut("/{id:guid}/destination", async (Guid id, MissionDestinationUpdate update) =>
            GatewayChoice(update.Gateway) is not { } gateway
                ? InvalidGatewayChoice(update.Gateway)
                : DestinationOptions(update) is not { } options
                    ? InvalidDestinationChoice(update)
                    : DestinationAnswer(id, update, await MissionGateway.ChangeDestinationAsync(id, update.ItemId, gateway, options)))
        .DoesNotRequireEditPermission("cambia la destinazione di una missione in corso: lavoro del motore, non configurazione")
        .WithName("SetMissionDestination");

        // Item 54, decision 6 (owner's answer 6 of 13/10): the cells the operator can choose for the
        // mission's load unit in a storage, a group of storages or among the cells of a relocator - the
        // first 20 of the warehouse's search of every day. A read: nothing is reserved, in every state of
        // the engine, without the edit permission (it is not a write at all).
        group.MapGet("/{id:guid}/storage-cells", async (Guid id, Guid itemId) =>
            StorageCellsAnswer(itemId, await MissionGateway.StorageCellCandidatesAsync(id, itemId)))
        .WithName("ListMissionStorageCells");
        return api;
    }

    /// <summary>
    /// What goes with the destination in the engine's form (item 54); <c>null</c> if it is not well formed:
    /// a relocator's arrival and a precise cell together (the precise cell is deposited by the relocator
    /// that serves it), or a blank cell code.
    /// </summary>
    private static MissionDestinationOptions? DestinationOptions(MissionDestinationUpdate update) => update switch
    {
        { AtTheRelocator: not null, CellDoorCode: not null } => null,
        { CellDoorCode: { } code } when string.IsNullOrWhiteSpace(code) => null,
        { CellDoorCode: { } code } => MissionDestinationOptions.Cell(code),
        { AtTheRelocator: MissionRelocatorArrival.Deposit } => MissionDestinationOptions.Relocator(RelocatorArrival.Deposit),
        { AtTheRelocator: MissionRelocatorArrival.Stay } => MissionDestinationOptions.Relocator(RelocatorArrival.Stay),
        { AtTheRelocator: not null } => null,
        _ => MissionDestinationOptions.None
    };

    private static IResult InvalidDestinationChoice(MissionDestinationUpdate update) =>
        Refusal(StatusCodes.Status400BadRequest, MissionDestinationProblems.InvalidDestinationChoiceTitle,
            update is { AtTheRelocator: not null, CellDoorCode: not null }
                ? "Una cella precisa e la scelta del ricollocatore non vanno insieme: la cella la deposita il ricollocatore che la serve"
                : "Il codice della cella è vuoto");

    /// <summary>
    /// The answer of <c>GET /missions/{id}/storage-cells</c> (item 54): 200 with the cells, or a refusal
    /// with the title of its outcome (<see cref="StorageCellCandidatesProblems"/>); the warehouse that does
    /// not answer is the 503 of every other route, with the field that says which 503 it is.
    /// </summary>
    internal static IResult StorageCellsAnswer(Guid itemId, StorageCellCandidates answer) => answer.Outcome switch
    {
        StorageCellCandidatesOutcome.Listed => Results.Ok(answer.Cells.Select(DtoMapping.ToDto).ToList()),
        StorageCellCandidatesOutcome.MissionNotFound => Results.NotFound(),
        StorageCellCandidatesOutcome.DeviceNotFound => Refusal(StatusCodes.Status400BadRequest,
            StorageCellCandidatesProblems.DeviceNotFoundTitle, $"Il dispositivo {itemId} non esiste"),
        StorageCellCandidatesOutcome.NotAStorage => Refusal(StatusCodes.Status400BadRequest,
            StorageCellCandidatesProblems.NotAStorageTitle,
            $"Il dispositivo {itemId} non è un magazzino, un gruppo di magazzini o un ricollocatore: non ha celle"),
        StorageCellCandidatesOutcome.NoStorageCell => Refusal(StatusCodes.Status422UnprocessableEntity,
            StorageCellCandidatesProblems.NoStorageCellTitle,
            $"Non c'è niente da chiedere al gestionale per questa UDC in {itemId}: {answer.Reason}"),
        StorageCellCandidatesOutcome.WarehouseUnavailable => Refusal(StatusCodes.Status503ServiceUnavailable,
            MissionDestinationProblems.WarehouseUnavailableTitle,
            "Il gestionale non ha risposto, o non è configurato: le celle non si possono elencare",
            new Dictionary<string, object?> { [ServiceUnavailableProblems.ReasonExtension] = ServiceUnavailableProblems.WarehouseUnreachable }),
        var other => throw new InvalidOperationException($"Esito non previsto: {other}")
    };

    /// <summary>
    /// La scelta sul gateway della richiesta, nella forma del motore; <c>null</c> se non e' ben
    /// formata. Senza scelta il gateway resta, come per chi non sa che c'e'. Il dispositivo va con
    /// «set» e solo con lui: tenere o togliere un dispositivo nominato, o impostarne uno senza dirlo,
    /// e' una richiesta confusa, e indovinare che cosa volesse dire potrebbe mandare l'UDC per una
    /// strada che l'operatore non ha scelto. Anche un modo fuori dall'enum (il numero, che il
    /// convertitore accetta, di un valore che non esiste) e' una richiesta sbagliata.
    /// </summary>
    private static MissionGatewayChoice? GatewayChoice(MissionGatewayUpdate? update) => update switch
    {
        null => MissionGatewayChoice.Keep,
        { Mode: MissionGatewayUpdateMode.Keep, ItemId: null } => MissionGatewayChoice.Keep,
        { Mode: MissionGatewayUpdateMode.Clear, ItemId: null } => MissionGatewayChoice.Clear,
        { Mode: MissionGatewayUpdateMode.Set, ItemId: { } itemId } when itemId != Guid.Empty => MissionGatewayChoice.Set(itemId),
        _ => null
    };

    /// <summary>
    /// Il 400 di una scelta sul gateway malformata (<see cref="GatewayChoice"/> ha risposto <c>null</c>,
    /// cosa che fa solo per una scelta che c'e'), con il perche' della forma che e' arrivata, scritta
    /// come sul filo: «set» senza dispositivo, «keep» o «clear» con un dispositivo.
    /// </summary>
    private static IResult InvalidGatewayChoice(MissionGatewayUpdate? update)
    {
        var detail = update switch
        {
            { Mode: MissionGatewayUpdateMode.Set } =>
                "La scelta sul gateway «set» vuole il dispositivo da impostare, e non c'è (o è il Guid vuoto)",
            { Mode: MissionGatewayUpdateMode.Keep or MissionGatewayUpdateMode.Clear } =>
                $"La scelta sul gateway «{(update.Mode == MissionGatewayUpdateMode.Keep ? "keep" : "clear")}» non vuole un dispositivo " +
                $"(arrivato {update.ItemId}): il dispositivo va con «set» e solo con lui",
            _ => "La scelta sul gateway non è valida: il modo è «keep», «clear» o «set»"
        };

        return Refusal(StatusCodes.Status400BadRequest, MissionDestinationProblems.InvalidGatewayChoiceTitle, detail);
    }

    /// <summary>
    /// La risposta a un cambio di destinazione: <c>204</c> se la missione va dove l'operatore ha
    /// scelto (anche se ci andava gia'), <c>202</c> if the change waits for the end of a step of the
    /// mission in flight (item 54, decision 4), altrimenti un problema con il titolo dell'esito
    /// (<see cref="MissionDestinationProblems"/>), le parole per le persone nel dettaglio e, dove
    /// servono alla pagina, i campi.
    /// </summary>
    /// <remarks>
    /// <b>422</b> per nessun percorso e nessuna cella, il primo dell'host: la convenzione
    /// (<c>EngineExceptionHandler</c>) ha 400 per la richiesta sbagliata in se' e 409 per lo stato che
    /// la vieta adesso e smettera' di vietarla. Qui la richiesta e' ben formata e nomina dispositivi
    /// che esistono, ma da dove l'UDC si trova la destinazione non si raggiunge, o il magazzino non ha
    /// una cella da darle: riprovarla tale e quale non serve, serve un'altra scelta (un'altra
    /// destinazione, un altro gateway). Cosi' anche il no del gestionale al cambio cella di
    /// un'estrazione: ha ricevuto e rifiutato, e non e' il 503 di un gestionale che non risponde. Il 409 resta ai quattro rifiuti per lo stato della missione,
    /// come la specifica li raggruppa: il passo in volo e la ricerca della cella in corso, che passano
    /// da soli; l'UDC gia' nella destinazione, che passa quando l'UDC riparte; e la missione di un tipo
    /// che non si reindirizza. Con loro, e con lo stesso 409, l'altra UDC della stessa cella che si
    /// muove (risposta 4 del 9/10), che passa da sola come il passo in volo, ma con un titolo suo: a
    /// muoversi non e' la missione scelta.
    /// </remarks>
    internal static IResult DestinationAnswer(Guid missionId, MissionDestinationUpdate update, MissionDestinationResult result)
    {
        // Un rifiuto arrivato dopo la prenotazione verso un magazzino ha fatto perdere alla missione la
        // cella che teneva: la destinazione non e' cambiata, ma la missione si', e il rifiuto lo dice
        // nel campo e nelle parole, qualunque sia l'esito. Cosi' la chiusura della missione dopo il
        // rifiuto di un cambio cella (il gestionale aveva gia' saputo che lascia l'ordine).
        IResult Refusal(int status, string title, string detail, IDictionary<string, object?>? extensions = null)
        {
            if (result.MissionClosed)
                return MissionEndpoints.Refusal(status, title,
                    $"{detail}. Il gestionale aveva però già saputo che la missione lascia il suo ordine: la missione è chiusa, e l'UDC resta nella sua cella senza ordine",
                    new Dictionary<string, object?>(extensions ?? new Dictionary<string, object?>())
                    {
                        [MissionDestinationProblems.MissionClosedExtension] = true
                    });

            if (!result.LostTheCell) return MissionEndpoints.Refusal(status, title, detail, extensions);

            var withTheCell = new Dictionary<string, object?>(extensions ?? new Dictionary<string, object?>())
            {
                [MissionDestinationProblems.LostTheCellExtension] = true
            };
            return MissionEndpoints.Refusal(status, title,
                $"{detail}. La cella che la missione teneva però è stata lasciata durante il cambio: il motore gliene prenota un'altra nel magazzino della sua destinazione",
                withTheCell);
        }

        return result.Outcome switch
        {
            MissionDestinationOutcome.Changed or MissionDestinationOutcome.Unchanged => Results.NoContent(),
            // Item 54, decision 4: a step of the mission is in flight (or being started), and the change
            // waits for its end; the mission's row shows it while it waits (MissionDto.PendingChange).
            MissionDestinationOutcome.Pending => Results.Accepted(),
            MissionDestinationOutcome.MissionNotFound => Refusal(StatusCodes.Status404NotFound,
                MissionDestinationProblems.MissionNotFoundTitle, $"La missione {missionId} non è attiva"),
            MissionDestinationOutcome.DeviceNotFound => Refusal(StatusCodes.Status400BadRequest,
                MissionDestinationProblems.DeviceNotFoundTitle, $"Il dispositivo {update.ItemId} non esiste"),
            MissionDestinationOutcome.GatewayNotFound => Refusal(StatusCodes.Status400BadRequest,
                MissionDestinationProblems.GatewayNotFoundTitle, $"Il gateway {update.Gateway?.ItemId} non è un dispositivo"),
            MissionDestinationOutcome.StepInFlight => Refusal(StatusCodes.Status409Conflict,
                MissionDestinationProblems.StepInFlightTitle,
                "La missione ha un passo in esecuzione, o un dispositivo lo sta avviando: aspettare che finisca e riprovare"),
            MissionDestinationOutcome.CopyInFlight => Refusal(StatusCodes.Status409Conflict,
                MissionDestinationProblems.CopyInFlightTitle,
                "Un'altra UDC che condivide la cella della missione si sta muovendo: la missione è ferma e la destinazione non è cambiata, riprovare fra un momento"),
            MissionDestinationOutcome.CellSearchInFlight => Refusal(StatusCodes.Status409Conflict,
                MissionDestinationProblems.CellSearchInFlightTitle,
                "Per la missione è in corso una ricerca della cella di deposito, o un altro cambio di destinazione: riprovare quando finisce"),
            MissionDestinationOutcome.AlreadyThere => Refusal(StatusCodes.Status409Conflict,
                MissionDestinationProblems.AlreadyThereTitle, $"L'UDC è già sul dispositivo {update.ItemId}, o nel suo gruppo"),
            MissionDestinationOutcome.NoRoute => Refusal(StatusCodes.Status422UnprocessableEntity,
                MissionDestinationProblems.NoRouteTitle, NoRouteDetail(update.ItemId, result),
                new Dictionary<string, object?>
                {
                    [MissionDestinationProblems.GatewayExtension] = result.Gateway,
                    [MissionDestinationProblems.WouldRouteWithoutGatewayExtension] = result.WouldRouteWithoutGateway
                }),
            MissionDestinationOutcome.NoStorageCell => Refusal(StatusCodes.Status422UnprocessableEntity,
                MissionDestinationProblems.NoStorageCellTitle,
                result.NoOtherFreeCell
                    ? $"Il magazzino {update.ItemId} non ha un'altra cella libera per questa UDC: quella che la missione ha resta prenotata"
                    : $"Nessuna cella da prenotare per questa UDC nel magazzino {update.ItemId}: il perché è nel registro del motore",
                new Dictionary<string, object?> { [MissionDestinationProblems.NoOtherFreeCellExtension] = result.NoOtherFreeCell }),
            MissionDestinationOutcome.WarehouseUnavailable => Refusal(StatusCodes.Status503ServiceUnavailable,
                MissionDestinationProblems.WarehouseUnavailableTitle,
                "Il gestionale non ha risposto, o non è configurato: la destinazione non è cambiata",
                new Dictionary<string, object?> { [ServiceUnavailableProblems.ReasonExtension] = ServiceUnavailableProblems.WarehouseUnreachable }),
            MissionDestinationOutcome.WarehouseRefused => Refusal(StatusCodes.Status422UnprocessableEntity,
                MissionDestinationProblems.WarehouseRefusedTitle,
                "Il gestionale ha rifiutato il cambio: la destinazione non è cambiata, e la missione resta l'estrazione che era"),
            // Item 54, decision 5: a relocator's arrival for a device that is not a relocator.
            MissionDestinationOutcome.NotARelocator => Refusal(StatusCodes.Status400BadRequest,
                MissionDestinationProblems.NotARelocatorTitle,
                $"Il dispositivo {update.ItemId} non è il ricollocatore di nessun magazzino: «depositarla» o «solo portarla lì» non vale"),
            // Item 54, decision 6: the precise cell refused, with the reason in words and in the field.
            MissionDestinationOutcome.CellRefused => Refusal(StatusCodes.Status422UnprocessableEntity,
                MissionDestinationProblems.CellRefusedTitle,
                $"La cella {update.CellDoorCode} non è stata prenotata: {result.CellRefusalReason}",
                new Dictionary<string, object?> { [MissionDestinationProblems.CellRefusalExtension] = CellRefusalName(result.CellRefusal) }),
            var other => throw new InvalidOperationException($"Esito non previsto: {other}")
        };
    }

    /// <summary>The name of the refusal of a precise cell on the wire (<see cref="MissionDestinationProblems.CellRefusalExtension"/>).</summary>
    private static string? CellRefusalName(MissionCellRefusal? refusal) => refusal switch
    {
        MissionCellRefusal.UnknownDoor => "unknownDoor",
        MissionCellRefusal.NotADoorA => "notADoorA",
        MissionCellRefusal.CellNotAvailable => "cellNotAvailable",
        MissionCellRefusal.UnknownLoadUnit => "unknownLoadUnit",
        MissionCellRefusal.NotInTheChoice => "notInTheChoice",
        _ => null
    };

    private static string NoRouteDetail(Guid destination, MissionDestinationResult result) => result.Gateway switch
    {
        null => $"Dalla posizione dell'UDC non c'è un percorso verso {destination}",
        { } gateway when result.WouldRouteWithoutGateway =>
            $"Dalla posizione dell'UDC non c'è un percorso verso {destination} passando per il gateway {gateway}: senza il gateway il percorso c'è",
        { } gateway => $"Dalla posizione dell'UDC non c'è un percorso verso {destination} passando per il gateway {gateway}, e nemmeno senza"
    };

    private static IResult Refusal(int status, string title, string detail, IDictionary<string, object?>? extensions = null) =>
        Results.Problem(detail: detail, statusCode: status, title: title, extensions: extensions);
}
