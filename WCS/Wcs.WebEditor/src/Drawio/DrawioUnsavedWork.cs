namespace Wcs.WebEditor.Drawio;

/// <summary>
/// What a save would write now, said in words - or null when it would write nothing. The engine
/// switch refuses to start while there is any: with the engine running the save writes nothing, and
/// the work would stay in the browser without the operator knowing why.
/// </summary>
/// <remarks>
/// Refusals and notices do not count: they are what the save would NOT write anyway, and counting
/// them could hold the engine back until somebody repaired the drawing. The decoration does not count
/// either: the save rewrites it every time without comparing it, so there is no comparison to reuse,
/// and it does not touch the engine (spec 2026-09-24, amended).
/// </remarks>
internal static class DrawioUnsavedWork
{
    public static string? Describe(DrawioDeltaResult delta)
    {
        var parts = new List<string>();
        if (delta.Moves.Count > 0) parts.Add($"{delta.Moves.Count} moved or resized device(s)");
        if (delta.Deleted.Count > 0) parts.Add($"{delta.Deleted.Count} deleted device(s)");
        if (delta.NewDevices.Count > 0) parts.Add($"{delta.NewDevices.Count} new device(s)");
        if (delta.NewJunctions.Count > 0) parts.Add($"{delta.NewJunctions.Count} new junction(s)");
        if (delta.DeletedJunctions.Count > 0) parts.Add($"{delta.DeletedJunctions.Count} deleted junction(s)");
        if (delta.JunctionPriorities.Count > 0) parts.Add($"{delta.JunctionPriorities.Count} junction priority change(s)");

        return parts.Count == 0
            ? null
            : $"The drawing holds work that is not saved yet ({string.Join(", ", parts)}): with the engine running the save writes nothing, so the engine was NOT started. Save first, then start it.";
    }
}
