namespace Wcs.Api.Contracts.Dtos;

/// <summary>Una cella servita da un magazzino automatico con ricollocatore.</summary>
public sealed record AutomaticCell(decimal CellId, Guid ItemId, Guid RelocatorItemId);
