namespace Wcs.Engine.Persistence.Entities;

/// <summary>Which reference of a handled load unit a <see cref="RetiredHandledReference"/> keeps.</summary>
public enum HandledReferenceKind : byte
{
    /// <summary><c>HandledLoadUnit.ProcessId</c>, the process of the WMS.</summary>
    Process = 1,

    /// <summary><c>HandledLoadUnit.LoadUnitProcessId</c>, the process of the load unit.</summary>
    LoadUnitProcess = 2,

    /// <summary><c>HandledLoadUnit.NoteHeaderId</c>, the header of the shipping note.</summary>
    NoteHeader = 3
}

/// <summary>
/// A reference that a month of history leaving the retention carried (item D103): the month file goes,
/// the fact «this process had handled load units» stays in <c>wcs.db</c>, which does not rotate, so
/// <c>HistoryQueries.HandledLoadUnitExistsAsync</c> still answers yes for it. One row per kind and value.
/// </summary>
public sealed class RetiredHandledReference
{
    public HandledReferenceKind Kind { get; set; }

    public decimal Value { get; set; }
}
