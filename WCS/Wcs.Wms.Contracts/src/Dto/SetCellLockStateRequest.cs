namespace Wcs.Wms.Contracts;

/// <summary>La richiesta di <see cref="IWarehouse.SetCellLockStateAsync"/>: la chiave è il codice porta, non l'id di cella (così leggeva già <c>DbInfo.SetLockCell</c>).</summary>
public sealed record SetCellLockStateRequest(string DoorCode, CellLockState LockState);
