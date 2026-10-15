namespace Wcs.Wms.Contracts;

/// <summary>
/// What became of the reservation of a precise cell (<see cref="IWarehouse.ReserveStorageCellAsync"/>,
/// item 54). Only <see cref="Reserved"/> holds a cell; the others are refusals the operator reads, with
/// the reason in <see cref="StorageCellReservationResult.Reason"/>.
/// </summary>
/// <remarks>The values are written by hand because they travel on the wire as numbers.</remarks>
public enum StorageCellReservationOutcome : byte
{
    /// <summary>The cell was free and suited to the load unit, and it is now reserved for it.</summary>
    Reserved = 0,

    /// <summary>No cell has this code as door A or door B.</summary>
    UnknownDoor = 1,

    /// <summary>The code is the door B of a cell: only door A codes are accepted (owner's answer 7).</summary>
    NotADoorA = 2,

    /// <summary>The cell is not free, or not suited to this load unit: the search of every day does not propose it.</summary>
    CellNotAvailable = 3,

    /// <summary>The warehouse does not know the load unit.</summary>
    UnknownLoadUnit = 4
}
