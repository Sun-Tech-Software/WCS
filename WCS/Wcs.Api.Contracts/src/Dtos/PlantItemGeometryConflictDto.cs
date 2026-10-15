namespace Wcs.Api.Contracts.Dtos;

/// <summary>Dispositivi la cui geometria non porta piu' la versione attesa dal chiamante.</summary>
public sealed record PlantItemGeometryConflictDto(IReadOnlyList<Guid> ItemIds);
