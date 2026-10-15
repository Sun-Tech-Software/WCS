namespace Wcs.WebEditor.Io;

/// <summary>Where an import that has a file chosen stands: see <see cref="PlantImportStatus"/>.</summary>
public enum PlantImportPhase
{
    /// <summary>The confirmation is waiting for "Confirm" or "Cancel".</summary>
    Waiting,

    /// <summary>"Confirm" was pressed and the write is on its way.</summary>
    Writing,

    /// <summary>"Confirm" was pressed and nothing was written: refused, or the plant could not be read again first. The dialog says why.</summary>
    NotWritten,

    /// <summary>The plant changed since the comparison the operator last saw: the dialog shows the new one, and nothing was written.</summary>
    PlantChanged,

    /// <summary>A comparison made again without "Confirm" could not read the plant: the dialog may be old, and says so. Nothing was attempted.</summary>
    NotCompared,
}

/// <summary>
/// What <c>ImportExport</c> tells the component that hosts it about the file waiting in its
/// confirmation, so that the host can say it where the dialog cannot be seen - the summary line of
/// the draw.io page's shut "Plant file" section. It changes nothing of what the import does.
/// </summary>
public sealed record PlantImportStatus(string FileName, PlantImportPhase Phase);
