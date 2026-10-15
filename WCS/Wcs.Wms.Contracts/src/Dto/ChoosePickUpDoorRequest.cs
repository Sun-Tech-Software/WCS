namespace Wcs.Wms.Contracts;

/// <summary>La richiesta di <see cref="IWarehouse.ChoosePickUpDoorAsync"/>: due parametri, quindi POST con corpo.</summary>
public sealed record ChoosePickUpDoorRequest(decimal LoadUnitId, decimal CellId);
