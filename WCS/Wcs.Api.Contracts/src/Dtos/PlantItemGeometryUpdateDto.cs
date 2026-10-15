namespace Wcs.Api.Contracts.Dtos;

/// <summary>
/// Una scrittura geometrica condizionale. Il token nullable distingue un client vecchio che non lo
/// invia dalla versione iniziale legittima <see cref="Guid.Empty"/> di una riga migrata.
/// </summary>
public sealed record PlantItemGeometryUpdateDto(
    Guid ItemId,
    double? X,
    double? Y,
    double? Width,
    double? Height,
    double? Rotation,
    Guid? ExpectedGeometryVersion);
