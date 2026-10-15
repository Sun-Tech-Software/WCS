namespace Wcs.Wms.Contracts;

/// <summary>La richiesta di <see cref="IWarehouse.GetCellBusyStateAsync"/>: due parametri, quindi POST con corpo invece di GET con segnaposto.</summary>
public sealed record CellBusyStateRequest(decimal CellId, decimal LoadUnitIdToExclude);
