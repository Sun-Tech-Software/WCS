namespace Wcs.Wms.Contracts;

/// <summary>
/// What the warehouse did with a cell the WCS gave back (<see cref="IWarehouse.ReleaseLoadUnitCellAsync"/>,
/// item 54). Both are domain answers: whoever sends the call from a queue takes the row out for
/// either. Only a failure is retried, and a failure is not an outcome.
/// </summary>
/// <remarks>The values are written by hand because they travel on the wire as numbers.</remarks>
public enum LoadUnitCellReleaseOutcome : byte
{
    /// <summary>The warehouse held that cell for that load unit, and released it.</summary>
    Released = 0,

    /// <summary>
    /// The warehouse no longer held that cell for that load unit (already released, replaced by another
    /// reservation, or taken by someone else): nothing was touched.
    /// </summary>
    NotHeld = 1
}
