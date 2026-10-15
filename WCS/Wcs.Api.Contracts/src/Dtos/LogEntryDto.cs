namespace Wcs.Api.Contracts.Dtos;

public sealed record LogEntryDto(long Id, DateTime Date, Guid? ItemId, string? Asi, string? Partition, string? Subitem,
    short Status, string? Description, Guid? DestinationItemId, Guid? MissionId, DateTime? MissionStart,
    DateTime? MissionEnd, short MissionStatus, Guid? RunningItemId);
/// <summary>
/// Una riga di registro da scrivere. <c>Date</c> e' il momento dell'evento, non quello della
/// richiesta: chi registra una traccia gia' avvenuta lo indica, chi non lo indica prende adesso.
/// </summary>
public sealed record LogEntryRequest(Guid? ItemId, string? Asi, string? Partition, string? Subitem, short Status,
    string? Description, Guid? DestinationItemId, Guid? MissionId, DateTime? Date = null);
public sealed record TransitTime(Guid MissionId, string FromAsi, DateTime Start, string ToAsi, DateTime End,
    int Seconds);
