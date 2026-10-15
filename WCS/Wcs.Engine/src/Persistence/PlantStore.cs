using System.Drawing;
using Microsoft.EntityFrameworkCore;
using Wcs.Engine.Api;
using Wcs.Engine.Persistence.Entities;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Persistence;

/// <summary>
/// L'unico punto in cui le righe del diagramma — <see cref="PlantItem"/>,
/// <see cref="PlantJunction"/>, <see cref="PlantDiagram"/> — e gli oggetti in memoria si
/// incontrano: le mappe nei due versi e le operazioni sul database.
/// </summary>
/// <remarks>
/// Corrispondenza con le vecchie colonne di <c>NAVIGATOR_ITEMS</c>, per chi legge il vecchio
/// schema: <c>ID_ITEM</c>→<c>ItemId</c>, <c>ITEM_TYPE</c>→<c>DeviceType</c>,
/// <c>LU_STATUS</c>→<c>LoadUnitStatus</c>, <c>LU_SIZE_H/W</c>→<c>LoadUnitImageHeight/Width</c>,
/// <c>BUILD_DESTINATION_ITEM</c>→<c>BuildDestinationItemId</c>, <c>MANUFACTURER</c>→<c>ConnectionType</c>,
/// <c>MOVEMENT_TYPE</c>→<c>MotionType</c>, <c>LOOK_ALLOCATE_LU</c>→<c>LookWhereAllocateLoadUnit</c>,
/// <c>REJECT_LU_NO_ALLOCATE</c>→<c>RejectItemWhenNoAllocation</c>, <c>RELOCATE_LU_ITEM</c>→<c>RelocateItemId</c>,
/// <c>RECORD_LU_INFO</c>→<c>RecordLoadUnitInfo</c>, <c>REJECT_LU_INFO_LOCATION</c>→<c>RejectItemWhenInfoError</c>,
/// <c>ID_CELL</c>→<c>LinkedCellId</c>, <c>RECORD_STORAGE_LENGHT</c>→<c>RecordStorageLength</c>,
/// <c>ID_PCM_PLC</c>→<c>DeviceConnectionId</c>, <c>DOWNLOAD_UNIT_STEP</c>→<c>StoreUnitStep</c>,
/// <c>UPLOAD_UNIT_STEP</c>→<c>PickUpUnitStep</c>, <c>ORDER_NUMBER_GROUP</c>→<c>OrderNumberInGroup</c>,
/// <c>ID_GROUP_ITEM</c>→<c>GroupItemId</c>, <c>ID_GROUP_ROUTE</c>→<c>GroupRoutePathId</c>,
/// <c>ID_GROUP_PRIORITY_ROUTE</c>→<c>PriorityInRoutePath</c>, <c>TIME_LAPSE_BUILD_ROUTE</c>→<c>TimeLapseToExecuteStep</c>,
/// <c>BELONGING_ID_GROUP</c>→<c>BelongingGroupId</c>, <c>TIME_FORCED_LOADING</c>→<c>WaitingTimeForcedLoading</c>,
/// <c>GROUPED_MOVE_LU_TO</c>→<c>GroupedMoveLoadUnitTo</c>, <c>TRIGGER_ITEM_GET_LU_INFO</c>→<c>TriggerItemGetLoadUnitInfo</c>,
/// <c>TRIGGER_ITEM_EXTRACT/2</c>→<c>TriggerItemToExtractPoint1/2</c>, <c>ADD_STORAGE_LENGHT_LU_DEPTH</c>→<c>AddStorageLengthLoadUnitDepth</c>,
/// <c>ID_CELL_DEFRAGMENT</c>→<c>CellToDefragmentId</c>, <c>ACCEPT_LU_DESTINATION_AVAILABLE</c>→<c>AcceptLoadUnitIfDestinationIsAvailable</c>,
/// <c>TIMEOUT_GET_LU_INFO</c>→<c>TimeOutGetLoadUnitInfo</c>, <c>ON_LOADLU_ACTIVE_FILTER</c>→<c>OnLoadLoadUnitActiveFilter</c>.
/// </remarks>
internal static class PlantStore
{
    #region Dispositivi

    /// <summary>
    /// Un'istantanea del dispositivo, presa ora: e' quello che va in coda allo scrittore.
    /// </summary>
    public static PlantItem Snapshot(Items.AutomationDevice device) => new()
    {
        ItemId = device.ItemGuid,
        Name = device.Name,
        DeviceType = (short)device.DeviceType,
        Status = (short)device.Status,
        LoadUnitStatus = (short)device.LuLoadStatus,
        Asi = NullIfEmpty(device.ASI),
        Partition = NullIfEmpty(device.PARTITION),
        Subitem = NullIfEmpty(device.SUBITEM),
        DelayBlind = device.DelayBlind,
        TimeOutPlcMoving = device.TimeOutPlcMoving,
        LoadUnitImageHeight = (int)device.LuImageSize.Height,
        LoadUnitImageWidth = (int)device.LuImageSize.Width,
        AutoExecuteStep = device.AutoExecuteStep,
        AutoUnloadLoadUnit = device.AutoUnloadLu,
        BuildDestinationItemId = NullIfEmpty(device.DestinationItemId),
        BuildDestinationItemId2 = NullIfEmpty(device.DestinationItemId2),
        ConnectionType = (short)device.ConnectionType,
        MotionType = (short)device.MotionType,
        GetLoadUnitInfo = device.GetLuInfo,
        LookWhereAllocateLoadUnit = (short)device.LookWhereAllocateLu,
        RejectItemWhenNoAllocation = NullIfEmpty(device.RejectItemDestinationAllocateLu),
        RelocateItemId = NullIfEmpty(device.RelocateItemId),
        RecordLoadUnitInfo = device.RecordLuDataOnGetLuInfo,
        RejectItemWhenInfoError = NullIfEmpty(device.RejectItemDestinationIdGetLuInfo),
        LinkedCellId = device.LinkedCellId,
        StorageOffset = device.StorageOffset,
        RecordStorageLength = (short)device.RecordStorageLength,
        DeviceConnectionId = device.DeviceConnectionId,
        ShowInfoMessageAssignedLoadUnit = device.ShowInfoMessageAssignedLu,
        StoreUnitStep = device.StoreUnitStep,
        PickUpUnitStep = device.PickUpUnitStep,
        OrderNumberInGroup = device.OrderNumberGroup,
        GroupItemId = device.GroupItemId,
        GroupRoutePathId = device.GroupRoutePathId,
        PriorityInRoutePath = device.PriorityInRoutePath,
        TimeLapseToExecuteStep = device.TimeLapseToExecuteStep,
        BelongingGroupId = device.BelongingGroupId,
        GroupLevelId = device.GroupLevelId,
        WaitingTimeForcedLoading = device.WaitingTimeForcedLoading,
        GroupLoadingActive = device.GroupLoadingActive,
        GroupTransferringActive = device.GroupTransferringActive,
        GroupedMoveLoadUnitTo = device.GroupedMoveLuTo,
        RespectQueue = device.RespectQueue,
        WaitBelongedItemsEmptyBeforeLoading = device.WaitBelongedItemsEmptyBeforeLoading,
        StorageOffsetReversed = device.StorageOffsetReversed,
        StoragePartition = device.StoragePartition,
        StoragePartitionReversed = device.StoragePartitionReversed,
        TriggerItemGetLoadUnitInfo = NullIfEmpty(device.TriggerIdGetLuInfo),
        RunningStepId = NullIfEmpty(device.RunningStepId),
        TriggerItemToExtractPoint1 = NullIfEmpty(device.TriggerItemToExtractPoint1),
        TriggerItemToExtractPoint2 = NullIfEmpty(device.TriggerItemToExtractPoint2),
        DistributeOnBuffers = device.DistributeOnBuffers,
        AddStorageLengthLoadUnitDepth = device.AddStorageLengthLuDepth,
        EnableDefragmentation = device.EnableDefragmentation,
        CellToDefragmentId = device.IdCellToDefragment,
        AcceptLoadUnitIfDestinationIsAvailable = device.AcceptLuIfDestinationIsAvailable,
        MaxUnloadWeight = device.MaxUnloadWeight,
        ExtractionCheckPoint = device.ExtractionCheckPoint,
        RejectDestinationUserRequest = NullIfEmpty(device.RejectDestinationUserRequest),
        TimeOutGetLoadUnitInfo = device.TimeOutGetLuInfo,
        ToBeFiltered = device.ToBeFiltered,
        OnLoadLoadUnitActiveFilter = device.OnLoadLuActiveFilter,
        WaitLookingLevels = device.WaitLookingLevels
    };

    /// <summary>
    /// Copia la riga nel dispositivo, come faceva la lettura da <c>NAVIGATOR_ITEMS</c>: proprieta'
    /// per proprieta', con la guardia del chiamante che tiene ferme le scritture. Lo step in corso
    /// non si ricarica: al riavvio nessun dispositivo ha nulla in volo.
    /// </summary>
    public static void Apply(PlantItem row, Items.AutomationDevice device)
    {
        ApplyConfiguration(row, device);
        device.Status = (DeviceStatusTypes)row.Status;
        device.RunningStepId = Guid.Empty;
    }

    /// <summary>
    /// Copia la configurazione della riga nel dispositivo, senza toccare lo stato dal vivo
    /// (<see cref="Items.AutomationDevice.Status"/>, <see cref="Items.AutomationDevice.RunningStepId"/>):
    /// e' quello che arriva dall'API mentre il dispositivo e' gia' in memoria.
    /// </summary>
    public static void ApplyConfiguration(PlantItem row, Items.AutomationDevice device)
    {
        device.Name = row.Name;
        device.DeviceType = (DeviceTypes)row.DeviceType;
        device.DelayBlind = row.DelayBlind;
        device.TimeOutPlcMoving = row.TimeOutPlcMoving;

        var size = new SizeF(105f, 105f);
        if (row.LoadUnitImageHeight > 0) size.Height = row.LoadUnitImageHeight;
        if (row.LoadUnitImageWidth > 0) size.Width = row.LoadUnitImageWidth;
        device.LuImageSize = size;

        device.AutoExecuteStep = row.AutoExecuteStep;
        device.AutoUnloadLu = row.AutoUnloadLoadUnit;
        device.ASI = row.Asi ?? string.Empty;
        device.PARTITION = row.Partition ?? string.Empty;
        device.SUBITEM = row.Subitem ?? string.Empty;
        device.DestinationItemId = row.BuildDestinationItemId ?? Guid.Empty;
        device.DestinationItemId2 = row.BuildDestinationItemId2 ?? Guid.Empty;
        device.ConnectionType = (ConnectionTypes)row.ConnectionType;
        device.MotionType = (MotionTypes)row.MotionType;
        device.GetLuInfo = row.GetLoadUnitInfo;
        device.LookWhereAllocateLu = (LookWhereAllocateLuTypes)row.LookWhereAllocateLoadUnit;
        device.RejectItemDestinationAllocateLu = row.RejectItemWhenNoAllocation ?? Guid.Empty;
        device.RelocateItemId = row.RelocateItemId ?? Guid.Empty;
        device.RecordLuDataOnGetLuInfo = row.RecordLoadUnitInfo;
        device.RejectItemDestinationIdGetLuInfo = row.RejectItemWhenInfoError ?? Guid.Empty;
        device.LinkedCellId = row.LinkedCellId;
        device.StorageOffset = row.StorageOffset;
        device.RecordStorageLength = (RecordStorageTypes)row.RecordStorageLength;
        device.DeviceConnectionId = row.DeviceConnectionId;
        device.ShowInfoMessageAssignedLu = row.ShowInfoMessageAssignedLoadUnit;
        device.StoreUnitStep = row.StoreUnitStep;
        device.PickUpUnitStep = row.PickUpUnitStep;
        device.OrderNumberGroup = row.OrderNumberInGroup;
        device.GroupItemId = row.GroupItemId;
        device.GroupRoutePathId = row.GroupRoutePathId;
        device.PriorityInRoutePath = row.PriorityInRoutePath;
        device.TimeLapseToExecuteStep = row.TimeLapseToExecuteStep;
        device.BelongingGroupId = row.BelongingGroupId;
        device.GroupLevelId = row.GroupLevelId;
        device.WaitingTimeForcedLoading = row.WaitingTimeForcedLoading;
        device.GroupLoadingActive = row.GroupLoadingActive;
        device.GroupTransferringActive = row.GroupTransferringActive;
        device.GroupedMoveLuTo = row.GroupedMoveLoadUnitTo;
        device.RespectQueue = row.RespectQueue;
        device.WaitBelongedItemsEmptyBeforeLoading = row.WaitBelongedItemsEmptyBeforeLoading;
        device.StorageOffsetReversed = row.StorageOffsetReversed;
        device.StoragePartition = row.StoragePartition;
        device.StoragePartitionReversed = row.StoragePartitionReversed;
        device.TriggerIdGetLuInfo = row.TriggerItemGetLoadUnitInfo ?? Guid.Empty;
        device.TriggerItemToExtractPoint1 = row.TriggerItemToExtractPoint1 ?? Guid.Empty;
        device.TriggerItemToExtractPoint2 = row.TriggerItemToExtractPoint2 ?? Guid.Empty;
        device.DistributeOnBuffers = row.DistributeOnBuffers;
        device.AddStorageLengthLuDepth = row.AddStorageLengthLoadUnitDepth;
        device.EnableDefragmentation = row.EnableDefragmentation;
        device.IdCellToDefragment = row.CellToDefragmentId;
        device.AcceptLuIfDestinationIsAvailable = row.AcceptLoadUnitIfDestinationIsAvailable;
        device.MaxUnloadWeight = row.MaxUnloadWeight;
        device.ExtractionCheckPoint = row.ExtractionCheckPoint;
        device.RejectDestinationUserRequest = row.RejectDestinationUserRequest ?? Guid.Empty;
        device.TimeOutGetLuInfo = row.TimeOutGetLoadUnitInfo;
        device.ToBeFiltered = row.ToBeFiltered;
        device.OnLoadLuActiveFilter = row.OnLoadLoadUnitActiveFilter;
        device.WaitLookingLevels = row.WaitLookingLevels;
    }

    public static Task<PlantItem?> FindItemAsync(WcsDbContext context, Guid itemId) =>
        context.PlantItems.AsNoTracking().SingleOrDefaultAsync(i => i.ItemId == itemId);

    public static Task<List<Guid>> LoadItemIdsAsync(WcsDbContext context) =>
        context.PlantItems.AsNoTracking().Select(i => i.ItemId).ToListAsync();

    /// <summary>
    /// Inserisce la riga se non c'e', altrimenti la aggiorna con l'istantanea. Due scritture dello
    /// stesso dispositivo nello stesso lotto si incontrano nel contesto.
    /// </summary>
    public static async Task UpsertItemAsync(WcsDbContext context, PlantItem snapshot)
    {
        var tracked = context.ChangeTracker.Entries<PlantItem>().Select(e => e.Entity)
            .FirstOrDefault(i => i.ItemId == snapshot.ItemId);
        var existing = tracked ?? await context.PlantItems.SingleOrDefaultAsync(i => i.ItemId == snapshot.ItemId);

        if (existing == null)
        {
            if (snapshot.GeometryVersion == Guid.Empty)
                snapshot.GeometryVersion = Guid.NewGuid();
            context.PlantItems.Add(snapshot);
            return;
        }

        CopyKeepingGeometry(context, existing, snapshot);
    }

    /// <summary>
    /// Aggiorna la riga con l'istantanea, se la riga c'e'. Non la inserisce: restituisce false.
    /// </summary>
    /// <remarks>
    /// E' la scrittura dello stato e dei setter del dispositivo. Passava da
    /// <see cref="UpsertItemAsync"/>, e una scrittura in coda per un dispositivo tolto nel
    /// frattempo reinseriva la riga: il dispositivo rinasceva, senza geometria (coda, voce 42).
    /// Una riga tolta nello stesso lotto e' tracciata come <c>Deleted</c>, e conta come assente.
    /// </remarks>
    public static async Task<bool> UpdateItemAsync(WcsDbContext context, PlantItem snapshot)
    {
        var entry = context.ChangeTracker.Entries<PlantItem>()
            .FirstOrDefault(e => e.Entity.ItemId == snapshot.ItemId);
        if (entry is { State: EntityState.Deleted or EntityState.Detached }) return false;

        var existing = entry?.Entity
                       ?? await context.PlantItems.SingleOrDefaultAsync(i => i.ItemId == snapshot.ItemId);
        if (existing == null) return false;

        CopyKeepingGeometry(context, existing, snapshot);
        return true;
    }

    private static void CopyKeepingGeometry(WcsDbContext context, PlantItem existing, PlantItem snapshot)
    {
        // La geometria non sta sul dispositivo in memoria: l'istantanea che arriva dal motore la
        // porta nulla, e SetValues la cancellerebbe a ogni salvataggio di stato. Va conservata
        // quella della riga esistente. Stessa forma della chiave in MissionStore.UpsertAsync, e
        // stesso motivo: SetValues copia tutto, anche cio' che l'istantanea non sa.
        var geometry = new
        {
            existing.X,
            existing.Y,
            existing.Width,
            existing.Height,
            existing.Rotation,
            existing.GeometryVersion
        };

        context.Entry(existing).CurrentValues.SetValues(snapshot);

        existing.X = geometry.X;
        existing.Y = geometry.Y;
        existing.Width = geometry.Width;
        existing.Height = geometry.Height;
        existing.Rotation = geometry.Rotation;
        existing.GeometryVersion = geometry.GeometryVersion;
    }

    /// <summary>
    /// Toglie il dispositivo, ogni giunzione che lo tocca e la sua mappa porte, come faceva la
    /// cancellazione su <c>NAVIGATOR_ITEMS</c> e <c>NAVIGATOR_JUNCTIONS</c>.
    /// </summary>
    /// <remarks>
    /// <b>La mappa porte se ne va con il dispositivo, e prima di questo lotto non se ne andava.</b>
    /// Le righe di <c>DoorMappings</c> restavano, orfane: nessuna rotta le mostrava piu' (l'Editor
    /// filtra per <c>ItemId</c>), ma le letture per cella e per porta del motore continuavano a
    /// vederle. Fra la cascata e il rifiuto (cancellare non si puo' finche' la mappa c'e') si e'
    /// scelta la cascata perche' e' quella che <b>non cambia</b> cio' che si vede: la cancellazione
    /// riusciva con la mappa piena e continua a riuscire.
    /// <para>
    /// La cancellazione delle righe e' scritta qui, ed e' un <b>secondo giro di chiave</b>: la
    /// chiave esterna (<c>DoorMapping.ItemId</c> verso <c>PlantItems</c>, con
    /// <c>DeleteBehavior.Cascade</c>, si veda <c>WcsDbContext</c>) le porterebbe via da sola.
    /// <b>Misurato</b> il 2026-09-18 togliendo queste due righe: le suite del motore e dell'host
    /// restano verdi, perche' la cascata del database fa il lavoro. Nessuna prova di questo ramo
    /// distingue quindi le due strade, e questa resta per una ragione che le prove non coprono: la
    /// cascata del database vive nel <c>PRAGMA foreign_keys</c> della connessione, che il provider
    /// accende per conto suo, e questa no.
    /// </para>
    /// </remarks>
    public static async Task DeleteItemAsync(WcsDbContext context, Guid itemId)
    {
        var item = await context.PlantItems.SingleOrDefaultAsync(i => i.ItemId == itemId);
        if (item != null) context.PlantItems.Remove(item);

        var junctions = await context.PlantJunctions
            .Where(j => j.SourceItemId == itemId || j.DestinationItemId == itemId)
            .ToListAsync();
        context.PlantJunctions.RemoveRange(junctions);

        var mappings = await context.DoorMappings.Where(m => m.ItemId == itemId).ToListAsync();
        context.DoorMappings.RemoveRange(mappings);
    }

    /// <summary>
    /// La geometria di tutti i dispositivi, per identita'. Un dispositivo senza posizione compare
    /// lo stesso, con <see cref="ItemGeometry.None"/>: l'assenza e' un dato, non un buco.
    /// </summary>
    public static async Task<Dictionary<Guid, VersionedItemGeometry>> LoadGeometriesAsync(WcsDbContext context) =>
        await context.PlantItems.AsNoTracking()
            .Select(i => new { i.ItemId, i.X, i.Y, i.Width, i.Height, i.Rotation, i.GeometryVersion })
            .ToDictionaryAsync(i => i.ItemId, i => new VersionedItemGeometry(
                new ItemGeometry(i.X, i.Y, i.Width, i.Height, i.Rotation), i.GeometryVersion));

    /// <summary>
    /// Colloca un dispositivo. Falso se il dispositivo non c'e': chi chiama decide se e' un 404 o
    /// un rifiuto in blocco. Non passa da <c>UpsertItemAsync</c> apposta — quella conserva la
    /// geometria, questa e' l'unica che la cambia.
    /// </summary>
    public static async Task<bool> SetGeometryAsync(WcsDbContext context, Guid itemId, ItemGeometry geometry)
    {
        var row = await context.PlantItems.SingleOrDefaultAsync(i => i.ItemId == itemId);
        if (row == null) return false;

        row.X = geometry.X;
        row.Y = geometry.Y;
        row.Width = geometry.Width;
        row.Height = geometry.Height;
        row.Rotation = geometry.Rotation;
        return true;
    }

    /// <summary>
    /// Scrive un gruppo soltanto se ogni riga esiste e porta ancora il token letto dal chiamante.
    /// Nessuna entita' viene modificata prima di aver completato tutti i controlli.
    /// </summary>
    public static async Task<GeometryWriteOutcome> SetGeometriesAsync(
        WcsDbContext context, IReadOnlyCollection<GeometryWrite> requests)
    {
        var duplicate = requests.GroupBy(r => r.ItemId).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException($"Il dispositivo {duplicate.Key} compare più di una volta", nameof(requests));

        var ids = requests.Select(r => r.ItemId).ToList();
        var rows = await context.PlantItems.Where(i => ids.Contains(i.ItemId)).ToDictionaryAsync(i => i.ItemId);
        var missing = ids.Where(id => !rows.ContainsKey(id)).ToList();
        if (missing.Count > 0) return GeometryWriteOutcome.Missing(missing);

        var conflicts = requests
            .Where(request => rows[request.ItemId].GeometryVersion != request.ExpectedVersion)
            .Select(request => request.ItemId)
            .ToList();
        if (conflicts.Count > 0) return GeometryWriteOutcome.Conflict(conflicts);

        var written = new List<GeometryWriteConfirmation>(requests.Count);
        foreach (var request in requests)
        {
            var row = rows[request.ItemId];
            row.X = request.Geometry.X;
            row.Y = request.Geometry.Y;
            row.Width = request.Geometry.Width;
            row.Height = request.Geometry.Height;
            row.Rotation = request.Geometry.Rotation;
            row.GeometryVersion = Guid.NewGuid();
            written.Add(new GeometryWriteConfirmation(row.ItemId, row.GeometryVersion));
        }

        return GeometryWriteOutcome.Success(written);
    }

    /// <summary>
    /// Quali dei dispositivi chiesti non esistono. Serve a rifiutare in blocco una selezione
    /// trascinata: spostarne meta' sarebbe peggio che non spostarne nessuno.
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> MissingItemIdsAsync(WcsDbContext context, IReadOnlyCollection<Guid> itemIds)
    {
        var known = await context.PlantItems.AsNoTracking()
            .Where(i => itemIds.Contains(i.ItemId)).Select(i => i.ItemId).ToListAsync();
        return itemIds.Where(id => !known.Contains(id)).Distinct().ToList();
    }

    #endregion Dispositivi

    #region Giunzioni

    public static PlantJunction Snapshot(Items.AutomationJunction junction) => new()
    {
        JunctionId = junction.ItemGuid,
        SourceItemId = junction.SourceId,
        DestinationItemId = junction.DestinationId,
        Priority = junction.Priority
    };

    public static void Apply(PlantJunction row, Items.AutomationJunction junction)
    {
        junction.ItemGuid = row.JunctionId;
        junction.SourceId = row.SourceItemId;
        junction.DestinationId = row.DestinationItemId;
        junction.Priority = row.Priority;
    }

    public static Task<PlantJunction?> FindJunctionAsync(WcsDbContext context, Guid sourceItemId, Guid destinationItemId) =>
        context.PlantJunctions.AsNoTracking()
            .FirstOrDefaultAsync(j => j.SourceItemId == sourceItemId && j.DestinationItemId == destinationItemId);

    public static Task<PlantJunction?> FindJunctionAsync(WcsDbContext context, Guid junctionId) =>
        context.PlantJunctions.AsNoTracking().SingleOrDefaultAsync(j => j.JunctionId == junctionId);

    public static Task<List<PlantJunction>> LoadJunctionsAsync(WcsDbContext context) =>
        context.PlantJunctions.AsNoTracking().ToListAsync();

    /// <summary>
    /// Come per i dispositivi. La giunzione si riconosce dalla coppia sorgente-destinazione, come
    /// faceva l'aggiornamento su <c>NAVIGATOR_JUNCTIONS</c>, oppure dalla chiave.
    /// </summary>
    public static async Task UpsertJunctionAsync(WcsDbContext context, PlantJunction snapshot)
    {
        var tracked = context.ChangeTracker.Entries<PlantJunction>().Select(e => e.Entity)
            .FirstOrDefault(j => j.JunctionId == snapshot.JunctionId ||
                                 (j.SourceItemId == snapshot.SourceItemId && j.DestinationItemId == snapshot.DestinationItemId));
        var existing = tracked ?? await context.PlantJunctions.FirstOrDefaultAsync(j =>
            j.JunctionId == snapshot.JunctionId ||
            (j.SourceItemId == snapshot.SourceItemId && j.DestinationItemId == snapshot.DestinationItemId));

        if (existing == null)
        {
            context.PlantJunctions.Add(snapshot);
            return;
        }

        snapshot.JunctionId = existing.JunctionId;
        context.Entry(existing).CurrentValues.SetValues(snapshot);
    }

    public static async Task DeleteJunctionAsync(WcsDbContext context, Guid junctionId)
    {
        var existing = await context.PlantJunctions.SingleOrDefaultAsync(j => j.JunctionId == junctionId);
        if (existing != null) context.PlantJunctions.Remove(existing);
    }

    #endregion Giunzioni

    #region Diagramma

    public static Task<PlantDiagram?> LoadDiagramAsync(WcsDbContext context) =>
        context.PlantDiagrams.AsNoTracking().SingleOrDefaultAsync(d => d.Id == PlantDiagram.DefaultId);

    /// <summary>
    /// Solo <see cref="PlantDiagram.GeometryMigratedAt"/>, senza materializzare <c>DiagramXml</c> e
    /// <c>View</c>: sull'impianto vero sono 29.024.816 e 7.074.930 byte, e <c>PUT /plant/diagram</c>
    /// chiama questa domanda a ogni chiamata solo per sapere se il disegno e' congelato (I10 del
    /// registro, aggravata qui: prima di questa proiezione la si leggeva con
    /// <see cref="LoadDiagramAsync"/>, l'entita' intera).
    /// </summary>
    public static Task<DateTime?> LoadGeometryMigratedAtAsync(WcsDbContext context) =>
        context.PlantDiagrams.AsNoTracking().Where(d => d.Id == PlantDiagram.DefaultId)
            .Select(d => d.GeometryMigratedAt).SingleOrDefaultAsync();

    /// <summary>
    /// L'unica riga del diagramma, tracciata dal contesto, creata se manca: chi la chiede la
    /// modifica e salva.
    /// </summary>
    public static async Task<PlantDiagram> GetOrCreateDiagramAsync(WcsDbContext context)
    {
        var tracked = context.ChangeTracker.Entries<PlantDiagram>().Select(e => e.Entity)
            .FirstOrDefault(d => d.Id == PlantDiagram.DefaultId);
        var existing = tracked ?? await context.PlantDiagrams.SingleOrDefaultAsync(d => d.Id == PlantDiagram.DefaultId);
        if (existing != null) return existing;

        var created = new PlantDiagram { Id = PlantDiagram.DefaultId, IsDefault = true };
        context.PlantDiagrams.Add(created);
        return created;
    }

    #endregion Diagramma

    private static Guid? NullIfEmpty(Guid value) => value == Guid.Empty ? null : value;

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
