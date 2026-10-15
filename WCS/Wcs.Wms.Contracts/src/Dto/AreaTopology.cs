namespace Wcs.Wms.Contracts;

/// <summary>
/// La topologia di un magazzino: quali aree sono servite dall'automazione e a quale area
/// appartiene ciascuna cella. Cambia quando si riconfigura il magazzino, non durante il lavoro.
/// </summary>
public sealed record AreaTopology(
    decimal WarehouseId,
    IReadOnlyList<decimal> AutomaticAreaIds,
    IReadOnlyDictionary<decimal, decimal> CellToArea);
