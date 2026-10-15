namespace Wcs.Wms.Contracts;

/// <summary>
/// The request of <see cref="IWarehouse.ReserveStorageCellAsync"/> (item 54, decision 6): reserve this
/// precise cell for the load unit, named by the code the operator chose from the list or wrote.
/// </summary>
/// <remarks>
/// <b>A door A code only</b> (owner's answer 7 of 13/10): <c>CELLE_AREE</c> has no cell code of its
/// own, and the WCS works with door codes; a door B code is refused with the reason
/// (<see cref="StorageCellReservationOutcome.NotADoorA"/>).
/// <para>
/// As for <see cref="ReserveStorageRequest"/>, the reservation <b>belongs to the mission</b>: with a
/// mission the warehouse writes the link, and <see cref="IWarehouse.GetStorageReservationAsync"/> and
/// <see cref="IWarehouse.ReleaseStorageReservationAsync"/> see it. Without a mission
/// (<see cref="Guid.Empty"/>) the cell is held by the load unit's assignment alone.
/// </para>
/// </remarks>
/// <param name="MissionId">The mission the cell is for; <see cref="Guid.Empty"/> for none.</param>
/// <param name="LoadUnitId">The load unit to place.</param>
/// <param name="DoorCode">The door A code of the cell, for example <c>3B02.0363.0001</c>.</param>
/// <param name="RequestedByWorkstationId">The workstation asking, for the trace of the write.</param>
/// <param name="RequestedByUserId">The user asking, for the trace of the write.</param>
public sealed record ReserveStorageCellRequest(
    Guid MissionId,
    decimal LoadUnitId,
    string DoorCode,
    string RequestedByWorkstationId,
    string RequestedByUserId);
