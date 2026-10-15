namespace Wcs.Common;

/// <summary>
/// One PLC connection as the host replaces it: its key and the five values an operator sets
/// (<c>PlcSettingsStore.ReplaceConnectionsAsync</c> writes exactly these). The operational state of
/// the row - progress, channel statuses, the command-id counter - is not part of it.
/// </summary>
public sealed record PlcConnectionSettings(long Id, string? Description, string? PlcAddress, int? PcmPort,
    string? LocalAddress, int? LocalPort);

/// <summary>What a list of PLC connections would change on another, by Id, each list in ascending order.</summary>
public sealed record PlcConnectionChanges(IReadOnlyList<long> Added, IReadOnlyList<long> Removed, IReadOnlyList<long> Modified)
{
    /// <summary>True when the two lists hold the same connections with the same values.</summary>
    public bool None => Added.Count == 0 && Removed.Count == 0 && Modified.Count == 0;
}

/// <summary>
/// THE comparison of two lists of PLC connections, used by the Web Editor (the grid of the
/// connections and the import's preview of what a file changes, through <c>PlcConnectionRules.Compare</c>),
/// which maps the API's DTO onto <see cref="PlcConnectionSettings"/>. It lives here because the engine
/// used it too, from Task 4c until the owner's decision of 27/9 on Pause: an import is now accepted only
/// with the engine stopped whatever connections it carries, so the engine no longer compares them.
/// </summary>
/// <remarks>
/// By Id, the key the host replaces by: the rows whose Id is not proposed are removed, the others
/// updated or added. An Id changed on a row is therefore one connection removed and another added -
/// which is what the host does with it. A connection is modified when any of its five values differs
/// (ordinal text, a missing value and an empty text are different).
/// </remarks>
public static class PlcConnectionComparison
{
    /// <summary>
    /// What <paramref name="proposed"/> would change on <paramref name="current"/>, by Id. Neither list
    /// may repeat an Id: a repeated Id throws (<see cref="ArgumentException"/>).
    /// </summary>
    public static PlcConnectionChanges Compare(IEnumerable<PlcConnectionSettings> current, IEnumerable<PlcConnectionSettings> proposed)
    {
        var currentById = current.ToDictionary(connection => connection.Id);
        var proposedById = proposed.ToDictionary(connection => connection.Id);
        return new PlcConnectionChanges(
            [.. proposedById.Keys.Where(id => !currentById.ContainsKey(id)).Order()],
            [.. currentById.Keys.Where(id => !proposedById.ContainsKey(id)).Order()],
            [.. proposedById.Where(pair => currentById.TryGetValue(pair.Key, out var existing) && existing != pair.Value)
                .Select(pair => pair.Key).Order()]);
    }
}
