namespace Wcs.Wms.Contracts;

/// <summary>
/// The request of <see cref="IWarehouse.ReleaseLoadUnitCellAsync"/> (item 54, answer 1 of the owner of
/// 13/10): the WCS gives back the storage cell it had reserved for this load unit, because the mission
/// that was going there now goes elsewhere (a change of cell or a defragmentation redirected by the
/// operator). The warehouse releases it only if it still holds that cell for that load unit.
/// </summary>
/// <remarks>
/// <b>By load unit and cell, not by mission.</b> The reservations of the changes of cell born in the
/// engine (the obstacle to move, the pick-up queue reloaded after a restart) are taken without a
/// mission, and the warehouse writes no link for them: the release by mission
/// (<see cref="IWarehouse.ReleaseStorageReservationAsync"/>) does not free them. The cell is in the
/// request because the load unit may meanwhile hold another one - the reservation that replaced it -
/// and that one must stay.
/// <para>
/// <b>The shape is the engine's side only.</b> Item 54, task 3 needs it to deliver its durable queue;
/// the route, the client and the warehouse's endpoint arrive with task 5, which may reshape it.
/// </para>
/// </remarks>
/// <param name="OperationId">The stable identity of the give-back, the same on every attempt.</param>
/// <param name="MissionId">The mission that had the cell: for the log; it may hold no link to it.</param>
/// <param name="LoadUnitId">The load unit the cell was reserved for.</param>
/// <param name="CellId">The cell to give back.</param>
/// <param name="UserId">Who gives it back: <c>Navigator.UserId</c>, the identity of the WCS.</param>
/// <param name="Reason">What happened, for the warehouse's log.</param>
public sealed record ReleaseLoadUnitCellRequest(
    Guid OperationId,
    Guid MissionId,
    decimal LoadUnitId,
    decimal CellId,
    string UserId,
    string Reason);
