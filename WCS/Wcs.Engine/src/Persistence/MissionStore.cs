using Microsoft.EntityFrameworkCore;
using Wcs.Engine.Persistence.Entities;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Persistence;

/// <summary>
/// L'unico punto in cui la riga <see cref="Mission"/> e l'oggetto <see cref="Items.PathMission"/>
/// si incontrano: la mappa nei due versi, e le tre operazioni sul database.
/// </summary>
/// <remarks>
/// Corrispondenza con le colonne di <c>MISSION_LU</c>, per chi legge il vecchio schema:
/// <c>LU_ID</c>→<c>LoadUnitId</c>, <c>ITEM_LOCATION</c>→<c>PositionItemId</c>,
/// <c>SSCC</c>→<c>LoadUnitCode</c>, <c>ID_COMMAND_TODO</c>→<c>CommandId</c>,
/// <c>TYPE</c>→<c>MissionType</c>, <c>ID_LU_TYPE</c>→<c>LoadUnitTypeId</c>,
/// <c>GATEWAY</c>→<c>GatewayItemId</c>, <c>ID_ASSEGNAZIONE_NOTA</c>→<c>NoteAssignmentId</c>,
/// <c>GUID_ASSEGNAZIONE_UDC</c>→<c>AssignmentGuid</c>, <c>ID_LAVORAZIONE_NOTA</c>→<c>NoteProcessingId</c>.
/// Gli identificatori vuoti diventano nulli nella riga e tornano vuoti in memoria, come facevano
/// i parametri SQL e il lettore di <c>LoadMissions</c>.
/// </remarks>
internal static class MissionStore
{
    /// <summary>
    /// Un'istantanea della missione, presa ora: e' quello che va in coda allo scrittore, cosi' i
    /// valori sono quelli del momento dell'evento e non di quando la riga viene scritta.
    /// </summary>
    public static Mission Snapshot(Items.PathMission mission) => new()
    {
        MissionId = mission.MissionId,
        LoadUnitId = mission.LuId,
        PositionItemId = NullIfEmpty(mission.PositionItemId),
        SourceItemId = NullIfEmpty(mission.SourceItemId),
        DestinationItemId = NullIfEmpty(mission.DestinationItemId),
        Date = mission.Date,
        LoadUnitCode = mission.LuCode,
        StatusDescription = mission.StatusDescription,
        StorageDoorCode = mission.StorageDoorCode,
        PickUpDoorCode = mission.PickUpDoorCode,
        Status = (short)mission.Status,
        CommandId = mission.IdCommandToDo == 0 ? null : mission.IdCommandToDo,
        RelocateItemId = NullIfEmpty(mission.RelocateItemId),
        MissionType = (short)mission.MissionType,
        MovingDescription = mission.MovingDescription,
        OperationType = (short)mission.OperationType,
        BufferCellId = mission.BufferCellId,
        ExtractionQueueId = mission.ExtractionQueueId,
        LoadUnitTypeId = mission.IdLuType,
        GroupedMovementId = NullIfEmpty(mission.GroupedMovementId),
        HandlingMode = mission.HandlingMode,
        GatewayItemId = NullIfEmpty(mission.Gateway),
        StorageDepth = mission.StorageDepth,
        SourceAsi = NullIfEmpty(mission.SourceAsi),
        SourceSubitem = NullIfEmpty(mission.SourceSubitem),
        SourcePartition = NullIfEmpty(mission.SourcePartition),
        DestinationAsi = NullIfEmpty(mission.DestinationAsi),
        DestinationSubitem = NullIfEmpty(mission.DestinationSubitem),
        DestinationPartition = NullIfEmpty(mission.DestinationPartition),
        NoteAssignmentId = mission.LuNoteAssignmentId,
        AssignmentGuid = NullIfEmpty(mission.GuidAssignmentLu),
        NoteProcessingId = mission.NoteProcessingId,
        ActiveFilter = mission.ActiveFilter,
        Tare = mission.Tare,
        ReallocatedDestination = mission.ReallocatedDestination,
        OperatorDestination = mission.OperatorDestination,
        RelocatorArrival = (short)mission.RelocatorArrival,
        StorageCellLost = mission.StorageCellLost,
        StorageCellReserved = mission.StorageCellReserved,
        StorageDepthAfterPickUp = mission.StorageDepthAfterPickUp,
        ChangeOfCellReportsSent = mission.ChangeOfCellReportsSent,
        LeftACopiedDoor = mission.LeftACopiedDoorMark,
        CellsRefusedByThePlc = CellsToText(mission.CellsRefusedByThePlc),
        DepositRefusedByThePlc = mission.DepositRefusedByThePlc,
        ServedAssignmentId = mission.ServedAssignmentId,
        ServedAssignmentGuid = NullIfEmpty(mission.ServedAssignmentGuid),
        ServedExtractionRowId = mission.ServedExtractionRowId
    };

    /// <summary>
    /// Ricostruisce la missione in memoria da una riga: e' quello che fa la ricarica all'avvio.
    /// </summary>
    /// <remarks>
    /// Il segno della destinazione scelta dall'operatore si rimette per ultimo, e con
    /// <see cref="Items.PathMission.SetOperatorDestination"/>, l'unica via che lo alza: il setter
    /// della destinazione lo abbassa a ogni cambio, quindi deve trovare la destinazione gia' scritta.
    /// Lo stesso vale per la cella persa (<see cref="Items.PathMission.StorageCellLost"/>), che il
    /// setter della destinazione abbassa allo stesso modo, e per la cella prenotata per la missione
    /// (<see cref="Items.PathMission.StorageCellReserved"/>), che il setter della porta abbassa.
    /// </remarks>
    public static Items.PathMission ToPathMission(Mission row)
    {
        var mission = NewPathMission(row);

        if (row.OperatorDestination && row.DestinationItemId is { } destination)
            mission.SetOperatorDestination(destination);

        // Item 54: after the destination, which drops the mark when it changes.
        if (row.RelocatorArrival != 0)
            mission.RelocatorArrival = (Api.RelocatorArrival)row.RelocatorArrival;

        if (row.StorageCellLost)
            mission.StorageCellLost = true;

        if (row.StorageCellReserved)
            mission.StorageCellReserved = true;

        // Il segno delle segnalazioni del cambio cella torna com'e': che cosa farne lo decide la
        // ricarica (DbInfo.PrepareLoadedMission), che chiude l'estrazione che lo porta.
        if (row.ChangeOfCellReportsSent)
            mission.ChangeOfCellReportsSent = true;

        // La copia che ha lasciato la porta (PathMission.LeftACopiedDoor): il ciclo del gruppo la
        // cerca ancora. Il segno lo guarda con la scena di adesso, quindi torna com'e'.
        if (row.LeftACopiedDoor)
            mission.LeftACopiedDoorMark = true;

        // Le celle che il PLC le ha rifiutato restano escluse anche dopo il riavvio, finche' l'UDC non
        // deposita (risposta 2 del proprietario del 9/10).
        if (row.DepositRefusedByThePlc || !string.IsNullOrEmpty(row.CellsRefusedByThePlc))
            mission.RestoreCellsRefusedByThePlc(TextToCells(row.CellsRefusedByThePlc), row.DepositRefusedByThePlc);

        return mission;
    }

    /// <summary>
    /// Le celle rifiutate nella colonna: nullo per nessuna, altrimenti gli id separati da virgola, senza
    /// gli zeri in coda che la lettura dal database del WCS lascia (6, non 6.0).
    /// </summary>
    private static string? CellsToText(IReadOnlyList<decimal> cells) =>
        cells.Count == 0
            ? null
            : string.Join(',', cells.Select(cell => cell.ToString("G29", System.Globalization.CultureInfo.InvariantCulture)));

    private static IEnumerable<decimal> TextToCells(string? text) =>
        string.IsNullOrEmpty(text)
            ? []
            : text.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(cell => decimal.Parse(cell, System.Globalization.CultureInfo.InvariantCulture));

    private static Items.PathMission NewPathMission(Mission row) => new(row.MissionId)
    {
        Date = row.Date,
        DestinationItemId = row.DestinationItemId ?? Guid.Empty,
        LuId = row.LoadUnitId,
        SourceItemId = row.SourceItemId ?? Guid.Empty,
        LuCode = row.LoadUnitCode,
        StatusDescription = row.StatusDescription,
        StorageDoorCode = row.StorageDoorCode,
        PickUpDoorCode = row.PickUpDoorCode,
        IdCommandToDo = row.CommandId ?? 0,
        RelocateItemId = row.RelocateItemId ?? Guid.Empty,
        MissionType = (MissionTypes)row.MissionType,
        MovingDescription = row.MovingDescription,
        OperationType = (OperationTypes)row.OperationType,
        BufferCellId = row.BufferCellId,
        ExtractionQueueId = row.ExtractionQueueId,
        IdLuType = row.LoadUnitTypeId,
        GroupedMovementId = row.GroupedMovementId ?? Guid.Empty,
        HandlingMode = row.HandlingMode,
        Gateway = row.GatewayItemId ?? Guid.Empty,
        StorageDepth = row.StorageDepth,
        // Prima della posizione, come StorageDepth: e' il setter della posizione a passarla in
        // StorageDepth quando l'UDC e' fuori dalla sua cella.
        StorageDepthAfterPickUp = row.StorageDepthAfterPickUp,
        SourceAsi = row.SourceAsi ?? string.Empty,
        SourceSubitem = row.SourceSubitem ?? string.Empty,
        SourcePartition = row.SourcePartition ?? string.Empty,
        DestinationAsi = row.DestinationAsi ?? string.Empty,
        DestinationSubitem = row.DestinationSubitem ?? string.Empty,
        DestinationPartition = row.DestinationPartition ?? string.Empty,
        LuNoteAssignmentId = row.NoteAssignmentId,
        GuidAssignmentLu = row.AssignmentGuid ?? Guid.Empty,
        NoteProcessingId = row.NoteProcessingId,
        ServedAssignmentId = row.ServedAssignmentId,
        ServedAssignmentGuid = row.ServedAssignmentGuid ?? Guid.Empty,
        ServedExtractionRowId = row.ServedExtractionRowId,
        ActiveFilter = row.ActiveFilter,
        Tare = row.Tare,
        ReallocatedDestination = row.ReallocatedDestination,
        Status = (MissionStatusTypes)row.Status,
        PositionItemId = row.PositionItemId ?? Guid.Empty
    };

    /// <summary>
    /// Inserisce la riga se non c'e', altrimenti la aggiorna con i valori dell'istantanea. Se la
    /// stessa missione compare due volte nello stesso lotto, la seconda trova la prima gia' nel
    /// contesto e la sovrascrive: l'ordine della coda e' l'ordine delle scritture.
    /// </summary>
    public static async Task UpsertAsync(WcsDbContext context, Mission snapshot)
    {
        var existing = await FindTrackedOrStoredAsync(context, snapshot.MissionId);

        if (existing == null)
        {
            context.Missions.Add(snapshot);
            return;
        }

        // La chiave resta quella della riga: l'istantanea nasce con Id zero, e SetValues copierebbe
        // anche quello.
        snapshot.Id = existing.Id;
        context.Entry(existing).CurrentValues.SetValues(snapshot);
    }

    /// <summary>Toglie la riga, se c'e'. Una missione mai scritta non e' un errore.</summary>
    public static async Task DeleteAsync(WcsDbContext context, Guid missionId)
    {
        var existing = await FindTrackedOrStoredAsync(context, missionId);
        if (existing != null)
            context.Missions.Remove(existing);
    }

    /// <summary>
    /// Le righe di un tipo di operazione e di un filtro, nell'ordine di creazione: la ricarica
    /// all'avvio le chiede nelle quattro combinazioni, nell'ordine di <c>RecoverFailure</c>.
    /// </summary>
    public static Task<List<Mission>> LoadAsync(WcsDbContext context, OperationTypes operationType, bool activeFilter)
    {
        var type = (short)operationType;
        return context.Missions.AsNoTracking()
            .Where(m => m.OperationType == type && m.ActiveFilter == activeFilter)
            .OrderBy(m => m.Id)
            .ToListAsync();
    }

    /// <summary>Dice se la missione è ancora durevole, anche quando il recupero non l'ha caricata.</summary>
    public static Task<bool> ExistsAsync(WcsDbContext context, Guid missionId) =>
        context.Missions.AsNoTracking().AnyAsync(mission => mission.MissionId == missionId);

    private static async Task<Mission?> FindTrackedOrStoredAsync(WcsDbContext context, Guid missionId)
    {
        // Prima fra le righe gia' nel contesto (aggiunte o caricate in questo lotto), poi sul
        // database: cosi' due scritture della stessa missione nello stesso lotto si incontrano.
        var tracked = context.ChangeTracker.Entries<Mission>()
            .Select(e => e.Entity)
            .FirstOrDefault(m => m.MissionId == missionId);

        return tracked ?? await context.Missions.SingleOrDefaultAsync(m => m.MissionId == missionId);
    }

    private static Guid? NullIfEmpty(Guid? value) => value is null || value == Guid.Empty ? null : value;

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
