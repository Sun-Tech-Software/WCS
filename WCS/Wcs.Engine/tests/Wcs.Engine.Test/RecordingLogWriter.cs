using Log.Interface;

namespace Wcs.Engine.Test;

/// <summary>
/// Il registro del motore nei test: si collega a <c>Navigator.SetEventLogWriter</c>, tiene le righe
/// ricevute e si stacca quando viene smaltito.
/// </summary>
/// <remarks>
/// <c>Navigator.WriteLog</c> manda la riga al registro da dentro un <c>Task.Run</c>: chi la aspetta
/// non deve discriminare con un'attesa a tempo, e usa <see cref="Wait"/>, che si completa quando la
/// riga cercata arriva.
/// </remarks>
internal sealed class RecordingLogWriter : IWriter, IDisposable
{
    private readonly IWriter? _previous = Navigator.EventLogWriter;
    private readonly Lock _lock = new();
    private readonly List<(string Description, LogEntryType Level)> _lines = [];
    private readonly List<(Func<string, LogEntryType, bool> Matches, TaskCompletionSource Signal)> _waiters = [];

    public RecordingLogWriter() => Navigator.SetEventLogWriter(this);

    public IReadOnlyList<(string Description, LogEntryType Level)> Lines
    {
        get
        {
            lock (_lock) return [.. _lines];
        }
    }

    public void WriteLog(string description, LogEntryType logType)
    {
        List<TaskCompletionSource> toSignal = [];

        lock (_lock)
        {
            _lines.Add((description, logType));

            for (var i = _waiters.Count - 1; i >= 0; i--)
            {
                if (!_waiters[i].Matches(description, logType)) continue;

                toSignal.Add(_waiters[i].Signal);
                _waiters.RemoveAt(i);
            }
        }

        foreach (var signal in toSignal) signal.TrySetResult();
    }

    /// <summary>
    /// Si completa quando arriva una riga che soddisfa <paramref name="matches"/> — subito, se ne e'
    /// gia' arrivata una.
    /// </summary>
    public Task Wait(Func<string, LogEntryType, bool> matches)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_lock)
        {
            if (_lines.Any(line => matches(line.Description, line.Level))) return Task.CompletedTask;

            _waiters.Add((matches, signal));
        }

        return signal.Task;
    }

    public void Dispose() => Navigator.SetEventLogWriter(_previous);
}
