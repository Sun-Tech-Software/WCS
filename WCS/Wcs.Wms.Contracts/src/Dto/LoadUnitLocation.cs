namespace Wcs.Wms.Contracts;

/// <summary>
/// Dove sta un'unità di carico. Null significa «non è a magazzino», che è una risposta e non un
/// guasto: il client traduce il 404 in null.
/// </summary>
/// <param name="StoringDepth">A che profondità sta dentro la cella.</param>
public sealed record LoadUnitLocation(
    decimal LoadUnitId,
    decimal CellId,
    int StoringDepth);
