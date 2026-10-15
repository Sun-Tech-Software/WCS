namespace Wcs.Engine.Persistence.Entities;

/// <summary>
/// A storage cell the WCS gives back to the warehouse (item 54, answer 1 of the owner of 13/10): the
/// cell a change of cell or a defragmentation was going to, when the operator sends the mission
/// elsewhere. Written with the mission's row, in the same write (<c>Items.PathMission.CommitRoute</c>),
/// delivered by the master loop (<c>PendingCellGiveBackDispatcher</c>) and retried until the
/// warehouse answers; it survives a restart. The warehouse releases the cell only if it still holds it
/// for that load unit, and the dispatcher then frees the door of the WCS's door map.
/// </summary>
/// <remarks>
/// Not <see cref="ReservationToGiveBack"/>: that one releases by mission, once, on the way to Run, and
/// a live mission that has just taken another cell would lose the new one. Here the row carries the
/// load unit and the cell, so the reservations taken without a mission (the obstacle, the reload) are
/// reached too. The same row stays through every attempt with the same <see cref="OperationId"/>.
/// </remarks>
public sealed class PendingCellGiveBack
{
    public long Id { get; set; }
    public Guid OperationId { get; set; }
    public Guid MissionId { get; set; }
    public decimal LoadUnitId { get; set; }

    /// <summary>The cell to give back (<c>ID_CELLA</c>).</summary>
    public decimal CellId { get; set; }

    /// <summary>The door the mission was going to, for the log.</summary>
    public string DoorCode { get; set; } = string.Empty;

    public string UserId { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }

    /// <summary>
    /// The warehouse refused it for good (a 4xx): it stays with the reason in <see cref="LastError"/>, so
    /// someone sees it and decides, but it is not tried again.
    /// </summary>
    public bool Parked { get; set; }
}
