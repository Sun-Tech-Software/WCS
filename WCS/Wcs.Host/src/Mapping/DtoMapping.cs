using Wcs.Api.Contracts.Dtos;
using Wcs.Engine;
using Wcs.Engine.Api;
using Wcs.Engine.Items;
using Wcs.Engine.Persistence.Entities;
using Wcs.Wms.Contracts;
using static Wcs.Common.Enums;

namespace Wcs.Host.Mapping;

internal static class DtoMapping
{
    public static EngineStatus Status() => new(Navigator.NavStatus, Navigator.Defragmentation,
        typeof(Navigator).Assembly.GetName().Version?.ToString() ?? "0", DateTime.UtcNow,
        HistoryQueries.FirstMonthAvailable(), Navigator.LoadError);

    public static ItemStatus ToStatus(AutomationDevice d) => new(d.ItemGuid, d.ASI, d.PARTITION, d.SUBITEM,
        d.Name ?? string.Empty, d.Status, d.LuLoadStatus, d.LoadedLu?.LuCode ?? string.Empty, d.LoadedLu?.LuId ?? 0,
        d.RunningStepId, d.WaitingStepId);

    /// <summary>
    /// La geometria arriva da fuori perche' sta solo sullo store, mentre questo DTO nasce
    /// dall'oggetto in memoria (voce <c>I4</c> di <c>api.md</c>, revisione 2026-09-11-seconda, che
    /// resta com'e'): chi elenca i dispositivi la carica <b>una volta</b> e la passa qui, invece
    /// di una lettura per riga.
    /// </summary>
    public static PlantItemDto ToDto(AutomationDevice d, VersionedItemGeometry versionedGeometry)
    {
        var row = PlantQueries.Snapshot(d);
        var geometry = versionedGeometry.Geometry;
        return new PlantItemDto(row.ItemId, row.Name, (DeviceTypes)row.DeviceType, row.Asi, row.Partition, row.Subitem,
            (ConnectionTypes)row.ConnectionType, (MotionTypes)row.MotionType, row.LinkedCellId, row.RelocateItemId,
            row.DeviceConnectionId, row.GroupItemId, row.BelongingGroupId, row.GroupLevelId, ToStatus(d),
            geometry.X, geometry.Y, geometry.Width, geometry.Height, geometry.Rotation)
        {
            GeometryVersion = versionedGeometry.Version
        };
    }

    public static DeviceTypeDto ToDto(DeviceTypeDescriptor t) => new(
        t.Code, t.NumericCode, t.Label,
        t.Shape.Select(p => new ShapePartDto(p.Path, p.Color, p.Filled, p.Rigid)).ToList(),
        t.Ports.Select(p => new DevicePortDto(p.Name, p.X, p.Y, p.Direction)).ToList(),
        new ShapePointDto(t.LoadUnitSlot.X, t.LoadUnitSlot.Y),
        new ShapePointDto(t.StatusSlot.X, t.StatusSlot.Y));

    public static JunctionDto ToDto(AutomationJunction j) => new(j.ItemGuid, j.SourceId, j.DestinationId, j.Priority);

    /// <summary>Come <see cref="ToDto(AutomationJunction)"/>, dalla riga dello store invece che dalla giunzione in memoria (compito 12, <c>GET /plant/graph</c>).</summary>
    public static JunctionDto ToDto(PlantJunction j) => new(j.JunctionId, j.SourceItemId, j.DestinationItemId, j.Priority);

    /// <summary><c>IsMine</c> non sta in <see cref="EditLockState"/>: dipende da chi ha chiesto, non dal permesso in se', quindi lo passa la rotta invece di ricavarlo qui dentro.</summary>
    public static EditLockDto ToDto(EditLockState s, bool isMine) => new(s.Holder, s.SinceLocal, s.ExpiresLocal, isMine);

    public static DiagramDto ToDto(PlantDiagram d) => new(d.Diagram, d.DiagramXml, d.View, d.CheckCounter, d.StatusCounter, d.CompactingStatus);

    public static DoorMappingDto ToDto(DoorMapping m) => new(m.Id, m.ItemId, m.DoorCode, m.CellId, m.Side, m.OperationActive, m.StoringLocked);
    public static DoorMapping ToEntity(DoorMappingDto m) => new() { Id = m.Id, ItemId = m.ItemId, DoorCode = m.DoorCode, CellId = m.CellId, Side = m.Side, OperationActive = m.OperationActive, StoringLocked = m.StoringLocked };
    public static AutomaticCell ToDto(AutomaticCellInfo c) => new(c.CellId, c.ItemId, c.RelocatorItemId);
    public static AutomaticCellDto ToDto(AutomaticCellState c) => new(c.CellId, c.ItemId, c.RelocatorItemId, c.StoringLocked, c.RelocatorStatus);
    public static CellSearchContextDto ToDto(CellSearchContext c) => new(c.AutomaticCells.Select(ToDto).ToList(), c.CellsInExtraction, c.LoadUnitsInExtraction);

    public static PlantItemConfigDto ToConfigDto(PlantItem row) => SameNameCopier.Copy(row, new PlantItemConfigDto());
    public static PlantItem ToEntity(PlantItemConfigDto dto) => SameNameCopier.Copy(dto, new PlantItem());
    /// <summary>
    /// La cella dell'elenco dell'Editor dal confine al DTO dell'API del WCS (compito 7). I campi
    /// sono gli stessi otto e nello stesso ordine: due cambiano nome perche' di la' dal confine
    /// sono descrizioni dichiarate tali (<c>WarehouseDescription</c>, <c>AreaDescription</c>),
    /// mentre di qua il DTO resta com'era - lo legge l'Editor, che non si tocca.
    /// </summary>
    public static WmsCell ToDto(EditorCellRow c) =>
        new(c.CellId, c.WarehouseDescription, c.AreaDescription, c.Column, c.Floor, c.DoorCodeA, c.DoorCodeB, c.Description);

    public static MissionDto ToDto(PathMission m)
    {
        var r = MissionQueries.Snapshot(m);
        // Item 54: the change that waits for the end of the mission's step, only in memory in the engine.
        var pending = MissionGateway.PendingChangeOf(m.MissionId);
        return new MissionDto(r.MissionId, r.LoadUnitId, r.LoadUnitCode, r.PositionItemId, r.SourceItemId, r.DestinationItemId,
            r.Date, r.StatusDescription, r.StorageDoorCode, r.PickUpDoorCode, (MissionStatusTypes)r.Status, r.CommandId,
            r.RelocateItemId, (MissionTypes)r.MissionType, r.MovingDescription, (OperationTypes)r.OperationType, r.BufferCellId,
            r.ExtractionQueueId, r.LoadUnitTypeId, r.GroupedMovementId, r.HandlingMode, r.GatewayItemId, r.SourceAsi,
            r.SourceSubitem, r.SourcePartition, r.DestinationAsi, r.DestinationSubitem, r.DestinationPartition,
            r.NoteAssignmentId, r.AssignmentGuid, r.NoteProcessingId, r.ActiveFilter, r.Tare, r.ReallocatedDestination,
            r.OperatorDestination,
            pending?.Kind switch
            {
                PendingMissionChangeKind.Destination => MissionPendingChangeKind.Destination,
                PendingMissionChangeKind.Position => MissionPendingChangeKind.Position,
                _ => null
            },
            pending?.ItemId,
            // Item 54, decision 5: what the relocator chosen by the operator does with the load unit.
            m.RelocatorArrival switch
            {
                RelocatorArrival.Deposit => MissionRelocatorArrival.Deposit,
                RelocatorArrival.Stay => MissionRelocatorArrival.Stay,
                _ => null
            });
    }

    /// <summary>A candidate storage cell of the warehouse, for the Editor (item 54, decision 6).</summary>
    public static StorageCellCandidateDto ToDto(Wcs.Wms.Contracts.StorageCellCandidate c) =>
        new(c.CellId, c.DoorCode, c.AreaId, c.AreaDescription, c.Column, c.Floor, c.Description, c.LoadUnitsInCell);

    public static MissionStepDto ToDto(MissionStep s) => new(s.StepId, s.MissionId, s.FromNodeId, s.ToNodeId, s.RunningNode,
        s.Date, s.StartStep, s.EndStep, s.StepStatus, s.GroupedMovementId, s.FromGroupRoutePath);

    public static CommandDto ToDto(CommandRecord c) => new(c.Id, c.Date, c.CommandType, c.ItemId, c.LoadUnitId, c.StorageDoorCode,
        c.Status, c.ErrorDescription, c.AssignmentGuid, c.Info, c.ShowStatus, c.WorkstationId, c.ProcessId, c.BufferCellId,
        c.PickUpDoorCode, c.LoadUnitTypeId, c.UserId, c.DestinationItemId, c.ReferenceId, c.FromApi, c.ApiStatus);

    public static CommandRecord ToEntity(CommandRequest r) => new()
    {
        CommandType = r.CommandType, ItemId = r.ItemId, LoadUnitId = r.LoadUnitId, StorageDoorCode = r.StorageDoorCode,
        PickUpDoorCode = r.PickUpDoorCode, BufferCellId = r.BufferCellId, ProcessId = r.ProcessId, LoadUnitTypeId = r.LoadUnitTypeId,
        DestinationItemId = r.DestinationItemId, ReferenceId = r.ReferenceId, Info = r.Info, WorkstationId = r.WorkstationId,
        UserId = r.UserId, AssignmentGuid = r.AssignmentGuid, Status = r.Status, Date = r.Date ?? default
    };

    public static ExtractionQueueItemDto ToDto(ExtractionQueueItem q) => new(q.Id, q.AssignmentId, q.LoadUnitId, q.CellId,
        q.BufferCellId, q.SourceItemId, q.DestinationItemId, q.Extract, q.WorkstationId, q.Processed, q.RelocatorItemId, q.DoorCode,
        q.SourceAssignmentId, q.ExtractionRowId, q.DestinationCellId, q.DestinationDoorCode, q.Priority, q.Compacting,
        q.AssignmentGuid, q.ApiCommandId);

    public static HandledLoadUnitDto ToDto(HandledLoadUnit h) => new(h.Id, h.MissionId, h.Type, h.Date, h.CellId, h.LoadUnitCode,
        h.ProcessId, h.ResultDescription, h.LoadUnitProcessId, h.NoteHeaderId, h.ExtractionHeaderId, h.LoadUnitId);
    public static HandledLoadUnitSummary ToDto(HandledLoadUnitSummaryRow s) => new(s.Period, s.Type, s.Count);

    public static LogEntryDto ToDto(NavigatorLogEntry e) => new(e.Id, e.Date, e.ItemId, e.Asi, e.Partition, e.Subitem, e.Status,
        e.Description, e.DestinationItemId, e.MissionId, e.MissionStart, e.MissionEnd, e.MissionStatus, e.RunningItemId);
    public static NavigatorLogEntry ToEntity(LogEntryRequest r) => new()
    {
        ItemId = r.ItemId, Asi = r.Asi, Partition = r.Partition, Subitem = r.Subitem, Status = r.Status,
        Description = r.Description, DestinationItemId = r.DestinationItemId, MissionId = r.MissionId,
        Date = r.Date ?? default
    };
    public static TransitTime ToDto(TransitTimeRow t) => new(t.MissionId, t.FromAsi, t.Start, t.ToAsi, t.End, t.Seconds);

    public static PlcMessageDto ToDto(PlcMessage m) => new(m.Id, m.Date, m.PlcId, m.Type, m.HeaderLength, m.Syscode,
        m.OpType, m.OpCode, m.OpProgress, m.FrameLength, m.DataLength, m.DataCrc32, m.CodingError,
        m.CodingErrorDescription, m.Asi, m.Subitem, m.Partition, m.MessageLength, m.MessageId, m.Xml);

    public static PlcConnectionDto ToDto(PlcConnection p) => new(p.Id, p.Description, p.PlcAddress, p.PcmPort, p.LocalAddress, p.LocalPort);
    public static PlcConnectionStatus ToStatus(PlcConnection p) => new(p.Id, p.OpProgress, p.StatusSenderChannel, p.StatusReceiverChannel, p.OutboundChannelStatus, p.InboundChannelStatus);
    public static PlcConnection ToEntity(PlcConnectionDto p) => new() { Id = p.Id, Description = p.Description, PlcAddress = p.PlcAddress, PcmPort = p.PcmPort, LocalAddress = p.LocalAddress, LocalPort = p.LocalPort };
}
