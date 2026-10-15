namespace Wcs.Wms.Contracts;

/// <summary>
/// One cell of <see cref="IWarehouse.ListStorageCellCandidatesAsync"/> (item 54): free and suited to the
/// load unit when the warehouse answered, in the order of the search. A row of a list to choose from,
/// not a reservation: the operator's choice is reserved with <see cref="IWarehouse.ReserveStorageCellAsync"/>,
/// which checks it again.
/// </summary>
/// <param name="CellId">The cell.</param>
/// <param name="DoorCode">Its door A code (<c>CODICE_PORTA_A</c>), the code the reservation takes.</param>
/// <param name="AreaId">The warehouse area of the cell.</param>
/// <param name="AreaDescription">The description of the area, for the operator.</param>
/// <param name="Column">The column of the cell in its area.</param>
/// <param name="Floor">The floor of the cell in its area.</param>
/// <param name="Description">The description of the cell, when it has one.</param>
/// <param name="LoadUnitsInCell">How many load units the cell holds already.</param>
public sealed record StorageCellCandidate(
    decimal CellId,
    string DoorCode,
    decimal AreaId,
    string AreaDescription,
    int Column,
    int Floor,
    string? Description,
    int LoadUnitsInCell);
