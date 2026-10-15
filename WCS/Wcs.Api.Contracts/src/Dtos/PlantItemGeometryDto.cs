namespace Wcs.Api.Contracts.Dtos;

/// <summary>
/// Dove collocare un dispositivo nel disegno. Ogni numero nullo significa <b>non collocato</b>:
/// mandarli tutti nulli e' un'operazione legittima, e non equivale a metterlo all'origine.
/// </summary>
public sealed record PlantItemGeometryDto(Guid ItemId, double? X, double? Y, double? Width, double? Height, double? Rotation);
