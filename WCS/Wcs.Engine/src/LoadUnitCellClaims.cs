namespace Wcs.Engine;

/// <summary>
/// The claim of a load unit between who reserves a storage cell for it and who gives its cells back
/// (item 54, fix round 1 of task 3). The give-back is by load unit and cell
/// (<see cref="Wcs.Wms.Contracts.IWarehouse.ReleaseLoadUnitCellAsync"/>): a reservation of the same cell
/// for the same load unit taken while a row of the queue is pending would be released by it. The
/// gateway's storage flow (<c>MissionGateway.ReserveACellAndCommitAsync</c>) holds the claim from before
/// the reservation until the commit has written and has taken the rows of the reserved cell out of the
/// queue; the dispatcher (<see cref="PendingCellGiveBackDispatcher"/>) holds it from its look at the row
/// until the row is gone, and does not wait for it: a load unit claimed waits for the next turn.
/// </summary>
/// <remarks>
/// Not an engine lock: an asynchronous claim per load unit, held across warehouse calls by both sides
/// like the operator's change begun on a mission, and taken before any engine lock, never under one.
/// The entries live only while someone holds or waits for them.
/// </remarks>
internal static class LoadUnitCellClaims
{
    private sealed class Entry
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public int Users;
    }

    private static readonly Dictionary<decimal, Entry> Entries = [];

    /// <summary>
    /// For the tests only: called with the load unit when <see cref="EnterAsync"/> finds the claim held
    /// and is about to wait for it. Null in production.
    /// </summary>
    internal static Action<decimal>? WaitingForTesting;

    /// <summary>Waits for the claim of the load unit (the gateway). Dispose the result to give it back.</summary>
    public static async Task<IDisposable> EnterAsync(decimal loadUnitId)
    {
        var entry = Join(loadUnitId);
        try
        {
            if (!entry.Gate.Wait(0))
            {
                WaitingForTesting?.Invoke(loadUnitId);
                await entry.Gate.WaitAsync();
            }
        }
        catch
        {
            Leave(loadUnitId, entry);
            throw;
        }

        return new Claim(loadUnitId, entry);
    }

    /// <summary>The claim of the load unit if nobody holds it (the dispatcher), otherwise null.</summary>
    public static IDisposable? TryEnter(decimal loadUnitId)
    {
        var entry = Join(loadUnitId);
        if (entry.Gate.Wait(0)) return new Claim(loadUnitId, entry);

        Leave(loadUnitId, entry);
        return null;
    }

    private static Entry Join(decimal loadUnitId)
    {
        lock (Entries)
        {
            if (!Entries.TryGetValue(loadUnitId, out var entry))
                Entries[loadUnitId] = entry = new Entry();
            entry.Users++;
            return entry;
        }
    }

    private static void Leave(decimal loadUnitId, Entry entry)
    {
        lock (Entries)
        {
            if (--entry.Users == 0) Entries.Remove(loadUnitId);
        }
    }

    private sealed class Claim(decimal loadUnitId, Entry entry) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            entry.Gate.Release();
            Leave(loadUnitId, entry);
        }
    }
}
