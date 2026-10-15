namespace Wcs.WebEditor.Test;

/// <summary>
/// A clock whose timers fire only when the test says so: the 150 ms of the feed and the 5 s of its
/// retry are measured in steps, not in wall time, so that no test here depends on how fast the
/// machine is.
/// </summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];

    public int PendingTimers
    {
        get { lock (_timers) return _timers.Count(timer => timer.Armed); }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(callback, state, dueTime != Timeout.InfiniteTimeSpan);
        lock (_timers) _timers.Add(timer);
        return timer;
    }

    /// <summary>Fires every armed timer once, as if its due time had come.</summary>
    public void FireAll()
    {
        ManualTimer[] armed;
        lock (_timers) armed = [.. _timers.Where(timer => timer.Armed)];
        foreach (var timer in armed) timer.Fire();
    }

    private sealed class ManualTimer(TimerCallback callback, object? state, bool armed) : ITimer
    {
        public bool Armed { get; private set; } = armed;

        public void Fire()
        {
            Armed = false;
            callback(state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Armed = dueTime != Timeout.InfiniteTimeSpan;
            return true;
        }

        public void Dispose() => Armed = false;
        public ValueTask DisposeAsync()
        {
            Armed = false;
            return ValueTask.CompletedTask;
        }
    }
}
