namespace Wcs.Api.Contracts.Dtos;

public sealed record JunctionDto(Guid JunctionId, Guid SourceItemId, Guid DestinationItemId, int Priority);
