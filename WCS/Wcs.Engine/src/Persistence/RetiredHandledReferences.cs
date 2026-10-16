using Microsoft.EntityFrameworkCore;
using Wcs.Engine.Persistence.Entities;

namespace Wcs.Engine.Persistence;

/// <summary>
/// The references of the months of history that left the retention (item D103), kept in <c>wcs.db</c>.
/// </summary>
internal static class RetiredHandledReferences
{
    /// <summary>
    /// The references of handled load units in a month of history: the process, the process of the load
    /// unit and the header of the note, each once. Zero is no reference.
    /// </summary>
    public static IReadOnlyCollection<RetiredHandledReference> Of(WcsHistoryDbContext month)
    {
        var rows = month.HandledLoadUnits.AsNoTracking()
            .Select(h => new { h.ProcessId, h.LoadUnitProcessId, h.NoteHeaderId })
            .ToList();
        var references = new HashSet<(HandledReferenceKind, decimal)>();
        foreach (var row in rows)
        {
            if (row.ProcessId is { } process and not 0) references.Add((HandledReferenceKind.Process, process));
            if (row.LoadUnitProcessId is { } loadUnitProcess and not 0) references.Add((HandledReferenceKind.LoadUnitProcess, loadUnitProcess));
            if (row.NoteHeaderId is { } noteHeader and not 0) references.Add((HandledReferenceKind.NoteHeader, noteHeader));
        }
        return [.. references.Select(r => new RetiredHandledReference { Kind = r.Item1, Value = r.Item2 })];
    }

    /// <summary>
    /// Keeps the references in <c>wcs.db</c>, the ones already there left as they are, in one
    /// transaction. Synchronous: it runs inside the rotation of the history, under its lock.
    /// </summary>
    public static void Keep(WcsDatabase store, IReadOnlyCollection<RetiredHandledReference> references)
    {
        if (references.Count == 0) return;

        using var context = store.CreateContext();
        var values = references.Select(r => r.Value).Distinct().ToList();
        var kept = context.RetiredHandledReferences.AsNoTracking()
            .Where(r => values.Contains(r.Value))
            .Select(r => new { r.Kind, r.Value })
            .AsEnumerable()
            .Select(r => (r.Kind, r.Value))
            .ToHashSet();
        foreach (var reference in references)
            if (kept.Add((reference.Kind, reference.Value)))
                context.RetiredHandledReferences.Add(new RetiredHandledReference { Kind = reference.Kind, Value = reference.Value });
        context.SaveChanges();
    }

    /// <summary>Whether a retired month had a handled load unit with one of these references.</summary>
    public static Task<bool> ExistAsync(WcsDbContext context, decimal? processId, decimal? loadUnitProcessId, decimal? noteHeaderId) =>
        context.RetiredHandledReferences.AsNoTracking().AnyAsync(r =>
            (processId != null && r.Kind == HandledReferenceKind.Process && r.Value == processId) ||
            (loadUnitProcessId != null && r.Kind == HandledReferenceKind.LoadUnitProcess && r.Value == loadUnitProcessId) ||
            (noteHeaderId != null && r.Kind == HandledReferenceKind.NoteHeader && r.Value == noteHeaderId));
}
