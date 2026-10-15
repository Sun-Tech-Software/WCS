namespace Wcs.Api.Contracts.Dtos;

public sealed record DoorMappingDto(long Id, Guid ItemId, string? DoorCode, decimal CellId, string? Side,
    short OperationActive, bool StoringLocked);
public sealed record DoorMappingLink(Guid ItemId, string DoorCode, decimal CellId);
