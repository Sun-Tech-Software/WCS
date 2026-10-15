namespace Wcs.Api.Contracts.Dtos;

/// <summary>Una riga della coda di estrazione: le colonne di <c>ExtractionQueueItem</c> del motore, con lo stesso nome.</summary>
public sealed record ExtractionQueueItemDto(long Id, decimal? AssignmentId, decimal LoadUnitId, decimal? CellId,
    decimal? BufferCellId, Guid SourceItemId, Guid? DestinationItemId, bool Extract, string? WorkstationId,
    bool Processed, Guid? RelocatorItemId, string? DoorCode, decimal? SourceAssignmentId, decimal? ExtractionRowId,
    decimal? DestinationCellId, string? DestinationDoorCode, int Priority, bool Compacting, Guid? AssignmentGuid,
    decimal? ApiCommandId);
public sealed record PriorityUpdate(int Priority);
