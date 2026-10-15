namespace Wcs.Wms.Contracts;

/// <summary>
/// La richiesta di <see cref="IWarehouse.GetCellStoringConstraintsAsync"/>. Non porta il parametro
/// «considera assegnati» di <c>QuotaDepositoUdc</c>: dei tre chiamanti del motore nessuno lo mette a
/// vero (vedi il rapporto del compito 9), quindi il lato gestionale lo tratta come sempre falso.
/// </summary>
public sealed record CellStoringConstraintsRequest(decimal CellId, CellDoor Door, int LoadUnitDepth, decimal LoadUnitIdToExclude);
