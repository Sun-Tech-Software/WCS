namespace Wcs.Wms.Contracts;

/// <summary>
/// Dove sta l'unità di carico trovata da <see cref="IWarehouse.FindLoadUnitAsync"/> (compito 11): solo
/// la cella, perché è tutto quello che i chiamanti nel motore usano dal risultato prima di passarlo a
/// <see cref="IWarehouse.ChoosePickUpDoorAsync"/> - il resto di quello che portava
/// <c>CellaReferenza</c> (magazzino, descrizioni, quantità) non aveva un lettore reale.
/// </summary>
public sealed record LoadUnitSearchResult(decimal LoadUnitId, decimal CellId);
