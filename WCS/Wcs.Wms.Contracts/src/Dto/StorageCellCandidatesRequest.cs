namespace Wcs.Wms.Contracts;

/// <summary>
/// The request of <see cref="IWarehouse.ListStorageCellCandidatesAsync"/> (item 54, decision 6 and the
/// owner's answer 6 of 13/10): the storage cells suited to a load unit, in a warehouse or reachable by
/// a relocator, for the operator to choose one from the Editor. Nothing is reserved.
/// </summary>
/// <remarks>
/// <b>The search of every day, with a different limit.</b> The fields are the search fields of
/// <see cref="ReserveStorageRequest"/>, so the engine asks with what it would pass to reserve; the
/// warehouse runs the same search (all the areas given, the fullest cells first, an automatic
/// operator) and answers at most <see cref="Limit"/> cells, in that order. The cells it proposes are
/// named by their door A code, the only one the operator may write (answer 7).
/// </remarks>
/// <param name="LoadUnitId">The load unit to place.</param>
/// <param name="WarehouseId">The warehouse to search in.</param>
/// <param name="SearchAreaIds">The areas admitted to the search, identifiers known to the WCS.</param>
/// <param name="ExcludedCellIds">The cells to leave out (for example the one the load unit comes from).</param>
/// <param name="ReachableDeviceIds">
/// The relocators that may serve the cell: with a relocator as destination, that relocator only.
/// Empty restricts nothing.
/// </param>
/// <param name="CellSearchMode">Which cells the search admits, as in <see cref="ReserveStorageRequest.CellSearchMode"/>.</param>
public sealed record StorageCellCandidatesRequest(
    decimal LoadUnitId,
    decimal WarehouseId,
    IReadOnlyList<decimal> SearchAreaIds,
    IReadOnlyList<decimal> ExcludedCellIds,
    IReadOnlyList<Guid> ReachableDeviceIds,
    StorageCellSearchMode CellSearchMode)
{
    /// <summary>The most cells the warehouse answers: the first 20 of the search (owner's answer 6 of 13/10).</summary>
    public const int Limit = 20;
}
