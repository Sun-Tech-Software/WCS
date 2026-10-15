namespace Wcs.Wms.Contracts;

/// <summary>
/// La richiesta di <see cref="IWarehouse.ClearCellPositionsAsync"/> (compito 18): la cella che
/// resta senza posizioni. Un corpo con un campo solo, come
/// <see cref="ReleaseReservationRequest"/>, perché è una scrittura e le scritture di questo
/// confine viaggiano come POST con corpo.
/// </summary>
/// <param name="CellId">La cella da svuotare di ogni posizione, prenotata o fisica.</param>
public sealed record ClearCellPositionsRequest(decimal CellId);
