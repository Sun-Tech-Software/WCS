using Wcs.Api.Contracts.Dtos;

namespace Wcs.ContractCheck;

/// <summary>One answer of a host operation: a status, what it means, and its body.</summary>
/// <param name="Json">The body of a JSON answer (<c>application/json</c>), or none.</param>
/// <param name="Titles">A problem answer (<c>application/problem+json</c>): the <c>title</c> values it can carry.</param>
/// <param name="Location">The answer carries a <c>Location</c> header.</param>
internal sealed record HostAnswer(
    int Status, string Description, Type? Json = null, bool Array = false, string[]? Titles = null,
    string? ContentType = null, bool Location = false)
{
    public bool IsProblem => Titles != null;
}

/// <summary>
/// The titles of the problem answers written by the host itself (middleware, filters and the exception
/// handler), which are not constants of <c>Wcs.Api.Contracts</c>. They are the host's own words, in
/// Italian; <c>HostProblemTitlesTest</c> asks a host started in memory for each of them, so a title
/// changed in the host fails there instead of going stale here.
/// </summary>
internal static class HostProblemTitles
{
    public const string KeyMissingOrWrong = "Chiave assente o sbagliata";
    public const string KeyInUrl = "Chiave nell'URL non più accettata";
    public const string ReadOnlyKey = "Chiave priva del permesso di scrittura";
    public const string NobodyHoldsTheLease = "Nessuno tiene il permesso di modifica";
    public const string SomeoneElseHoldsTheLease = "Permesso di modifica di qualcun altro";
    public const string EngineNotLoaded = "Il motore non è caricato";
    public const string EngineStoreUnavailable = "Il database del motore non è disponibile";
    public const string EngineNotStopped = "Il motore non è fermo";
    public const string GraphWorkPending = "Impossibile sostituire il grafo con lavoro pendente";
    public const string PlcConnectionInUse = "Connessione PLC ancora usata da dispositivi";
    public const string HistoryRange = "Intervallo fuori dallo storico interrogabile";
    public const string Unreadable = "Richiesta non leggibile";

    // The default titles ASP.NET Core gives Results.Problem without a title and the empty status
    // answers that UseStatusCodePages turns into problem details.
    public const string BadRequest = "Bad Request";
    public const string NotFound = "Not Found";
    public const string Conflict = "Conflict";
    public const string ServiceUnavailable = "Service Unavailable";
}

/// <summary>
/// What each host operation really answers, where the endpoint does not declare it: the endpoints
/// return <c>IResult</c> (<c>Results.NoContent()</c>, <c>Results.Problem(...)</c>), which ASP.NET Core's
/// OpenAPI generator cannot see through, so it writes a bare <c>200 OK</c> for them. This table was
/// written from the endpoint code in <c>WCS/Wcs.Host/src/Endpoints</c>; the generator refuses an
/// operation that has a bare <c>200</c> and no entry here, so a new endpoint cannot slip in undescribed.
/// </summary>
/// <remarks>
/// The answers every operation shares (401, 403 on writes, 423 on the lease routes, 503 while the engine
/// is not loaded, 400 on a body that cannot be read) are not repeated here: <see cref="HostApiDocument"/>
/// adds them from the endpoint metadata.
/// </remarks>
internal static class HostApiAnswers
{
    /// <summary>The engine state a plant write needs, or it is refused with 409 <see cref="HostProblemTitles.EngineNotStopped"/>.</summary>
    public static readonly IReadOnlyDictionary<string, string> EngineState = new Dictionary<string, string>
    {
        ["CreateItem"] = Stop,
        ["DeleteAllItems"] = NotRun,
        ["DeleteItem"] = NotRun,
        ["SaveItemConfiguration"] = NotRunOrStopForConnection,
        ["SetItemGeometry"] = NotRun,
        ["SetItemGeometries"] = NotRun,
        ["SetConnectionTypeForAll"] = Stop,
        ["KeepItems"] = NotRun,
        ["AddJunction"] = NotRun,
        ["SetJunctionPriority"] = NotRun,
        ["DeleteJunction"] = NotRun,
        ["ReplaceDoorMappings"] = NotRun,
        ["LinkBufferCell"] = NotRun,
        ["ClearDoorMappings"] = NotRun,
        ["SetPlantDecoration"] = NotRun,
        ["ReplacePlantGraph"] = Stop,
        ["ReplacePlcConnections"] = Stop,
    };

    public const string Stop = "Stop";
    public const string NotRun = "not Run";
    public const string NotRunOrStopForConnection = "not Run; Stop if connectionType or deviceConnectionId change";

    /// <summary>The operations that need the WMS and answer 503 <c>warehouseUnreachable</c> when it does not answer.</summary>
    public static readonly IReadOnlySet<string> NeedTheWarehouse = new HashSet<string>
    {
        "ListWmsCells", "WmsConfigurationCheck", "SetMissionDestination", "ListMissionStorageCells",
    };

    /// <summary>The operations whose <c>from</c>/<c>to</c> must fall in the months the history keeps.</summary>
    public static readonly IReadOnlySet<string> ReadTheHistory = new HashSet<string>
    {
        "ListHandledLoadUnits", "HandledLoadUnitSummary", "ListLog", "TransitTimes", "ListPlcMessages",
    };

    /// <summary>
    /// The operations outside the engine gate (<c>Program.cs</c>: <c>MapStatus</c> and <c>MapEditLock</c>),
    /// which answer even while the engine is not loaded.
    /// </summary>
    public static readonly IReadOnlySet<string> OutsideTheEngineGate = new HashSet<string>
    {
        "GetStatus", "AcquireOrRenewEditLock", "ReleaseEditLock",
    };

    private static HostAnswer NoContent(string description = "Done.") => new(204, description);
    private static HostAnswer Ok(Type body, string description = "The answer.", bool array = false) => new(200, description, body, array);
    private static HostAnswer Problem(int status, string description, params string[] titles) => new(status, description, Titles: titles);
    private static HostAnswer NotFound(string description) => Problem(404, description, HostProblemTitles.NotFound);
    private static HostAnswer BadRequest(string description) => Problem(400, description, HostProblemTitles.BadRequest);

    public static readonly IReadOnlyDictionary<string, HostAnswer[]> ByOperation = new Dictionary<string, HostAnswer[]>
    {
        ["AcquireOrRenewEditLock"] =
        [
            Ok(typeof(EditLockDto), "The lease is granted or renewed to the caller (`isMine: true`). It lasts 60 seconds."),
            new(409, "Someone else holds the lease: the body names them (`isMine: false`).", typeof(EditLockDto)),
            BadRequest("`holder` is empty."),
        ],
        ["ReleaseEditLock"] = [NoContent("Released; releasing a lease the caller does not hold does nothing.")],

        ["EnqueueCommand"] =
        [
            new(201, "Queued: the command as stored, with its id.", typeof(CommandDto), Location: true),
            BadRequest("Unknown command type, or a status other than 0 (to run) or 2 (already done)."),
            Problem(503, "The command needs the engine in Run, and it is not (no `unavailable` field).", HostProblemTitles.ServiceUnavailable),
        ],
        ["GetCommand"] = [Ok(typeof(CommandDto)), NotFound("No command with this id.")],
        ["UpdateCommand"] =
        [
            NoContent(), NotFound("No command with this id."),
            BadRequest("Unknown status, or a command already started sent back to 0."),
        ],

        ["GetDiagram"] = [Ok(typeof(DiagramDto), "The legacy drawing."), NotFound("No legacy drawing.")],
        ["SaveDiagram"] =
        [
            NoContent(), BadRequest("`diagramXml` is empty."),
            new(405, "Legacy: the geometry has been migrated (the host does it at its first start), the drawing is no longer saved. Plain text, `Allow: GET`.", ContentType: "text/plain"),
        ],
        ["CreateItem"] =
        [
            new(201, "Created: the configuration as sent.", typeof(PlantItemConfigDto), Location: true),
            BadRequest("`itemId` is empty."),
            Problem(409, "A device with this id exists.", HostProblemTitles.Conflict),
        ],
        ["DeleteAllItems"] = [NoContent("Every device removed.")],
        ["GetItemConfiguration"] = [Ok(typeof(PlantItemConfigDto)), NotFound("No device with this id.")],
        ["SaveItemConfiguration"] =
        [
            NoContent(), BadRequest("The `itemId` of the body is not the one of the path."),
            NotFound("No device with this id."),
        ],
        ["SetItemGeometry"] =
        [
            new(200, "Written: the new geometry version."),
            BadRequest("The `itemId` of the body is not the one of the path, or `expectedGeometryVersion` is missing."),
            Problem(404, "The device does not exist.", HostProblemTitles.NotFound),
            new(409, "The geometry changed since `expectedGeometryVersion`: the body names the device.", typeof(PlantItemGeometryConflictDto)),
        ],
        ["SetItemGeometries"] =
        [
            new(200, "Written, all or none: the new geometry version of each device."),
            BadRequest("`expectedGeometryVersion` is missing, or a device appears twice."),
            Problem(404, "Some devices do not exist: the detail lists them; nothing is written.", HostProblemTitles.NotFound),
            new(409, "The geometry changed since `expectedGeometryVersion`: the body lists the devices; nothing is written.", typeof(PlantItemGeometryConflictDto)),
        ],
        ["SetConnectionTypeForAll"] = [NoContent(), BadRequest("Unknown connection type.")],
        ["GetItem"] = [Ok(typeof(PlantItemDto)), NotFound("No device with this id.")],
        ["DeleteItem"] = [NoContent("Removed, with its junctions."), NotFound("No device with this id.")],
        ["KeepItems"] = [NoContent("Every device not in the list removed.")],
        ["AddJunction"] =
        [
            new(201, "Created.", typeof(JunctionDto), Location: true),
            BadRequest("An end is empty or unknown, or both ends are the same device."),
        ],
        ["SetJunctionPriority"] = [NoContent("Only `priority` is applied."), NotFound("No junction with this id.")],
        ["DeleteJunction"] = [NoContent(), NotFound("No junction with this id.")],
        ["ReplaceDoorMappings"] = [NoContent(), Problem(404, "Some devices do not exist: the detail lists them.", HostProblemTitles.NotFound)],
        ["LinkBufferCell"] = [NoContent(), Problem(404, "The device does not exist.", HostProblemTitles.NotFound)],
        ["ClearDoorMappings"] = [NoContent(), Problem(404, "The device does not exist.", HostProblemTitles.NotFound)],
        ["GetPlantDecoration"] = [Ok(typeof(PlantDecorationDto), "The free texts and boxes of the drawing (empty if never set).")],
        ["SetPlantDecoration"] = [NoContent()],
        ["GetPlantGraph"] = [Ok(typeof(PlantGraphDto), "The whole plant: the export file.")],
        ["ReplacePlantGraph"] =
        [
            NoContent("The plant is replaced."),
            BadRequest("The file is not a valid plant: the detail says why."),
            Problem(409, "Active missions or rows in the extraction queue.", HostProblemTitles.GraphWorkPending),
        ],
        ["ReadLegacyDiagramGeometry"] = [Ok(typeof(LegacyGeometryImportDto), "The positions read from the old drawing; nothing is written.")],

        ["GetMission"] = [Ok(typeof(MissionDto)), NotFound("No active mission with this id.")],
        ["GetMissionSteps"] = [Ok(typeof(MissionStepDto), array: true), NotFound("No active mission with this id.")],
        ["SetMissionPosition"] =
        [
            NoContent("Moved."),
            new(202, "Accepted: applied at the end of the current movement."),
            NotFound("No active mission with this id."),
            BadRequest("The device does not exist."),
            Problem(409, "The device carries another mission.", HostProblemTitles.Conflict),
            Problem(503, "The engine is in Recover, still loading the missions: nothing was done, retry shortly.",
                MissionMoveProblems.EngineRecoveringTitle),
        ],
        ["SetMissionDestination"] =
        [
            NoContent("Changed, or already the destination."),
            new(202, "Accepted: applied at the end of the current movement."),
            Problem(404, "The mission is not active.", MissionDestinationProblems.MissionNotFoundTitle),
            Problem(400, "An invalid choice, or an unknown device.",
                MissionDestinationProblems.InvalidGatewayChoiceTitle, MissionDestinationProblems.InvalidDestinationChoiceTitle,
                MissionDestinationProblems.DeviceNotFoundTitle, MissionDestinationProblems.GatewayNotFoundTitle,
                MissionDestinationProblems.NotARelocatorTitle),
            Problem(409, "Not now: something is moving; retry.",
                MissionDestinationProblems.StepInFlightTitle, MissionDestinationProblems.CopyInFlightTitle,
                MissionDestinationProblems.CellSearchInFlightTitle, MissionDestinationProblems.AlreadyThereTitle),
            Problem(422, "The change cannot be made. Extensions: `" + MissionDestinationProblems.GatewayExtension + "`, `" +
                         MissionDestinationProblems.WouldRouteWithoutGatewayExtension + "` (no route), `" +
                         MissionDestinationProblems.NoOtherFreeCellExtension + "` (no storage cell), `" +
                         MissionDestinationProblems.CellRefusalExtension + "` (cell refused).",
                MissionDestinationProblems.NoRouteTitle, MissionDestinationProblems.NoStorageCellTitle,
                MissionDestinationProblems.WarehouseRefusedTitle, MissionDestinationProblems.CellRefusedTitle),
        ],
        ["ListMissionStorageCells"] =
        [
            Ok(typeof(StorageCellCandidateDto), "The candidate cells, from the WMS route `reservations/candidates`.", array: true),
            NotFound("No active mission with this id."),
            Problem(400, "The device does not exist, or has no cells.",
                StorageCellCandidatesProblems.DeviceNotFoundTitle, StorageCellCandidatesProblems.NotAStorageTitle),
            Problem(422, "There is nothing to ask the WMS for this load unit.", StorageCellCandidatesProblems.NoStorageCellTitle),
        ],
        ["ExtractionQueueContains"] = [Ok(typeof(bool)), BadRequest("Neither `udc` nor `cell`.")],
        ["GetExtractionQueueItem"] = [Ok(typeof(ExtractionQueueItemDto)), NotFound("No row with this id.")],
        ["SetExtractionPriority"] = [NoContent(), NotFound("No row with this id.")],
        ["DeleteFromExtractionQueue"] =
        [
            new(202, "Command 4001 is queued for the row's load unit: the command, with its id.", typeof(CommandDto), Location: true),
            NotFound("No row with this id."),
        ],

        ["HandledLoadUnitSummary"] =
        [
            Ok(typeof(HandledLoadUnitSummary), array: true),
            BadRequest("`groupBy` is not day, week, month or year."),
        ],
        ["HandledLoadUnitExists"] = [Ok(typeof(bool)), BadRequest("None of `process`, `loadUnitProcess`, `noteHeader`.")],
        ["AppendLog"] =
        [
            new(202, "Accepted: the line is written by the history writer."),
            Problem(400, "`date`, when given, is not in the current month.", HostProblemTitles.HistoryRange),
        ],

        ["ReplacePlcConnections"] =
        [
            NoContent(),
            Problem(409, "A removed row is still used by devices: the extension `" + PlcConnectionProblems.InUseExtension +
                         "` lists the connections and their devices.", HostProblemTitles.PlcConnectionInUse),
        ],
        ["PlcConnectionStatus"] = [Ok(typeof(PlcConnectionStatus), "What the driver last persisted."), NotFound("No connection with this id.")],
    };
}
