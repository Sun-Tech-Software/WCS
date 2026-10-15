namespace Wcs.Api.Contracts.Dtos;

/// <summary>
/// La decorazione del disegno: scritte e riquadri liberi che l'operatore aggiunge, non
/// interpretati dal motore (decisione 3.6 del progetto). Usato sia in lettura sia in scrittura -
/// per la <c>PUT</c> conta solo <see cref="Xml"/>.
/// </summary>
public sealed record PlantDecorationDto(string Xml);
