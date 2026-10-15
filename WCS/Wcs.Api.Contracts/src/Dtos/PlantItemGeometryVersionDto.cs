namespace Wcs.Api.Contracts.Dtos;

/// <summary>Il token assegnato alla geometria appena confermata.</summary>
public sealed record PlantItemGeometryVersionDto(Guid ItemId, Guid GeometryVersion);
