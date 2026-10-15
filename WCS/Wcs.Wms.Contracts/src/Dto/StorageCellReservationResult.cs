using System.Text.Json.Serialization;

namespace Wcs.Wms.Contracts;

/// <summary>
/// The answer of <see cref="IWarehouse.ReserveStorageCellAsync"/> (item 54, decision 6). The outcome
/// is in the body of a 200, as for <see cref="StorageReservation"/>: «this cell is not free» is an
/// answer, not a failure.
/// </summary>
/// <remarks>
/// <b>The outcome is required on the wire</b> (<see cref="JsonRequiredAttribute"/>): its value 0 is
/// <see cref="StorageCellReservationOutcome.Reserved"/>, and a 200 with an empty body read with the
/// defaults would say «reserved» for a cell nobody reserved. Without it the body does not read, which
/// is a failure.
/// </remarks>
/// <param name="Outcome">What became of the request.</param>
/// <param name="Reservation">
/// With <see cref="StorageCellReservationOutcome.Reserved"/>, the reservation as
/// <see cref="IWarehouse.ReserveStorageAsync"/> would give it (cell, door, partition, depth,
/// compaction); <see langword="null"/> otherwise.
/// </param>
/// <param name="Reason">Why the cell was refused, for the operator and the log; <see langword="null"/> when reserved.</param>
public sealed record StorageCellReservationResult(
    [property: JsonRequired] StorageCellReservationOutcome Outcome,
    StorageReservation? Reservation,
    string? Reason);
