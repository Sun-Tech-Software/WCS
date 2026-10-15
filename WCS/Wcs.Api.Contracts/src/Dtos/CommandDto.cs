namespace Wcs.Api.Contracts.Dtos;

public sealed record CommandDto(long Id, DateTime Date, int CommandType, Guid? ItemId, decimal? LoadUnitId,
    string? StorageDoorCode, short Status, string? ErrorDescription, Guid? AssignmentGuid, string? Info,
    short ShowStatus, string? WorkstationId, decimal? ProcessId, decimal BufferCellId, string? PickUpDoorCode,
    decimal LoadUnitTypeId, string? UserId, Guid? DestinationItemId, string? ReferenceId, bool FromApi, int ApiStatus);

/// <summary>
/// Un comando da accodare: il tipo e' Command.CommandTypes del motore (1001..4001). <c>Date</c> e'
/// il momento dell'operazione: chi registra una traccia gia' avvenuta (le utilita' del WMS) lo
/// indica, cosi' comando e riga di registro portano lo stesso istante; chi non lo indica prende adesso.
/// </summary>
public sealed record CommandRequest(int CommandType, Guid? ItemId = null, decimal? LoadUnitId = null,
    string? StorageDoorCode = null, string? PickUpDoorCode = null, decimal BufferCellId = 0, decimal? ProcessId = null,
    decimal LoadUnitTypeId = 0, Guid? DestinationItemId = null, string? ReferenceId = null, string? Info = null,
    string? WorkstationId = null, string? UserId = null, Guid? AssignmentGuid = null, short Status = 0, DateTime? Date = null);

public sealed record CommandUpdate(short Status, string? ErrorDescription, string? Info, short? ShowStatus);
