using Microsoft.Extensions.Logging;
using Wcs.Api.Client;
using Wcs.Api.Contracts;
using Wcs.Api.Contracts.Dtos;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Live;

public enum LiveConnection
{
    Connecting,
    Live,
    Interrupted,
}

/// <summary>What the canvas has to show now: the devices that show something, and the movements.</summary>
public sealed record LiveFrame(IReadOnlyList<LiveDeviceView> Devices, IReadOnlyList<LiveMotion> Motions);

/// <summary>
/// The live status of the plant for one page: the snapshot, the events after it, and the frames
/// for the canvas at most one every <see cref="FlushInterval"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Events are raised on the channel's threads.</b> Whoever handles <see cref="FrameReady"/>,
/// <see cref="StatusChanged"/> and <see cref="CommandChanged"/> goes back to the circuit with
/// <c>InvokeAsync</c> before touching the page.
/// </para>
/// <para>
/// <b>A snapshot is read at the start, after every reconnection and at every
/// <c>NavigatorStatusChanged</c></b>: the events that went by while the channel was down do not come
/// back, and the host sends <c>NavigatorStatusChanged</c> as its resync signal when its own event
/// queue dropped some (<c>PlantEventRelay.ResyncIfSomethingWasDroppedAsync</c>). A colour left from
/// before the gap would be a lie drawn on the plant. At most one snapshot is in flight: the requests
/// that arrive during it are folded into exactly one more read after it.
/// </para>
/// <para>
/// <b>First the GET, then the differences.</b> The <c>ItemStatusChanged</c> that arrive while a
/// snapshot is in flight are held (per device, the last one wins) and replayed on top of the snapshot
/// once it lands: the answer in flight may predate them, and applying them before the
/// <see cref="PlantLiveState.Reset"/> would lose them. If the snapshot fails they are applied to the
/// state the feed already has, so that nothing is lost either way.
/// </para>
/// <para>
/// <b>Nothing here throws towards the page.</b> While the channel is closed or reconnecting, or the
/// snapshot cannot be read, the feed is <see cref="LiveConnection.Interrupted"/>, it says so, and the
/// canvas keeps the last state it had. The two failures are retried apart, every
/// <see cref="RetryInterval"/> until the feed is disposed of: a channel that did not start (or closed
/// for good) is started again; a snapshot that could not be read is read again WITHOUT touching the
/// channel - SignalR refuses to start a connection that is not Disconnected. A channel that is
/// reconnecting is left to SignalR, which says <c>Reconnected</c> or <c>Closed</c> when it is done.
/// </para>
/// </remarks>
public sealed class PlantLiveFeed : IAsyncDisposable
{
    public static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(150);
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);

    private readonly WcsClient _client;
    private readonly IPlantEventSource _source;
    private readonly TimeProvider _time;
    private readonly PlantLiveState _state;
    private readonly ILogger? _logger;

    // Everything below is guarded by _sync; the feed's own events are raised outside it.
    private readonly object _sync = new();
    private ITimer? _flush;
    private ITimer? _startRetry;
    private ITimer? _snapshotRetry;
    private bool _dirty;
    private bool _started;
    private bool _disposed;

    /// <summary>The channel is started and neither reconnecting nor closed: only then can a snapshot make the feed Live.</summary>
    private bool _channelOpen;

    private bool _snapshotRunning;
    private bool _snapshotAgain;

    /// <summary>The device events that arrived while a snapshot was in flight: the last one per device.</summary>
    private readonly Dictionary<Guid, ItemStatus> _held = [];

    private LiveConnection _connection = LiveConnection.Connecting;
    private NavigatorStatusTypes? _navStatus;
    private Task _settled = Task.CompletedTask;

    /// <summary>
    /// Bumped every time <see cref="OnNavigator"/> sets <see cref="_navStatus"/> straight from a live
    /// event. A snapshot loop captures it before its own network calls: if it has moved by the time
    /// the answer lands, a more recent engine event arrived while the snapshot was in flight, and the
    /// snapshot's own (now stale) engine field must not overwrite what the event already set.
    /// </summary>
    private int _navSeq;

    public PlantLiveFeed(WcsClient client, IPlantEventSource source, Func<Guid, DeviceTypes?> deviceTypeOf,
        TimeProvider? time = null, ILogger? logger = null)
    {
        _client = client;
        _source = source;
        _time = time ?? TimeProvider.System;
        _logger = logger;
        _state = new PlantLiveState(deviceTypeOf);
    }

    public LiveConnection Connection
    {
        get { lock (_sync) return _connection; }
    }

    public NavigatorStatusTypes? NavStatus
    {
        get { lock (_sync) return _navStatus; }
    }

    /// <summary>
    /// The background work in progress (a snapshot, a retry of the start): it completes when all of
    /// it has. The tests await it instead of sleeping.
    /// </summary>
    internal Task Settled
    {
        get { lock (_sync) return _settled; }
    }

    /// <summary>A frame is ready: take it with <see cref="TakeFrame"/>.</summary>
    public event Action? FrameReady;

    /// <summary><see cref="Connection"/> or <see cref="NavStatus"/> changed.</summary>
    public event Action? StatusChanged;

    public event Action<CommandDto>? CommandChanged;

    /// <summary>An active mission changed (compito 3, row 60): handed on as it came, on the channel's thread.</summary>
    public event Action<MissionDto, MissionChange>? MissionChanged;

    /// <summary>
    /// Events may have gone by unseen: whoever keeps what it read with a GET (the missions, their
    /// steps) reads it again. Raised at the same moments the feed reads its own snapshot - the
    /// channel's opening (also after a terminal close), every reconnection, the host's resync signal.
    /// </summary>
    public event Action? Resynced;

    /// <summary>
    /// Opens the channel and reads the snapshot. Only the first call does anything: a second
    /// <c>StartAsync</c> on a channel that is already up would throw inside SignalR.
    /// </summary>
    public Task StartAsync()
    {
        lock (_sync)
        {
            if (_disposed || _started) return _settled;
            _started = true;
        }

        _source.ItemStatusChanged += OnItemStatus;
        _source.NavigatorStatusChanged += OnNavigator;
        _source.CommandChanged += OnCommand;
        _source.MissionChanged += OnMission;
        _source.Reconnecting += OnReconnecting;
        _source.Reconnected += OnReconnected;
        _source.Closed += OnClosed;
        return Track(OpenAsync());
    }

    public ItemStatus? StatusOf(Guid itemId)
    {
        lock (_sync) return _state.StatusOf(itemId);
    }

    public LiveFrame TakeFrame()
    {
        lock (_sync)
        {
            _dirty = false;
            return new LiveFrame(_state.Devices(), _state.Motions());
        }
    }

    /// <summary>
    /// Starts the channel - the first time, or again after a failed start or a terminal close, when
    /// it is Disconnected - and then reads the snapshot.
    /// </summary>
    private async Task OpenAsync()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _startRetry?.Dispose();
            _startRetry = null;
        }

        try
        {
            await _source.StartAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Any failure of the channel is the same fact for the page: not live. SignalR throws
            // HttpRequestException, InvalidOperationException, TaskCanceledException and more.
            ChannelDown(retryStart: true);
            return;
        }

        lock (_sync)
        {
            if (_disposed) return;
            _channelOpen = true;
        }

        var snapshot = RequestSnapshot();
        RaiseResynced();
        await snapshot;
    }

    /// <summary>
    /// Reads the snapshot or, if one is already in flight, asks it for exactly one more read after
    /// itself: the answer in flight may predate the reason for this request.
    /// </summary>
    private Task RequestSnapshot()
    {
        lock (_sync)
        {
            if (_disposed) return Task.CompletedTask;
            // This request covers the armed retry.
            _snapshotRetry?.Dispose();
            _snapshotRetry = null;
            if (_snapshotRunning)
            {
                _snapshotAgain = true;
                return Task.CompletedTask;
            }

            _snapshotRunning = true;
        }

        return Track(SnapshotLoopAsync());
    }

    private async Task SnapshotLoopAsync()
    {
        // This loop owns _snapshotRunning until it clears it; once cleared, a new loop may own it, so
        // whoever clears it says so here and the finally below leaves it alone.
        var released = false;
        try
        {
            while (true)
            {
                int seqAtStart;
                lock (_sync) seqAtStart = _navSeq;
                var statuses = await _client.GetItemStatusesAsync();
                var engine = await _client.GetStatusAsync();
                // ReadFromJsonAsync hands back null for a "null" body: refused before the state is touched.
                if (statuses is null || engine is null)
                    throw new InvalidDataException("The host answered the snapshot with an empty body.");

                bool changed;
                lock (_sync)
                {
                    if (_disposed) return;
                    _state.Reset(statuses);
                    ReplayHeld();
                    _dirty = true;
                    // A navigator event that arrived AFTER this request started is more recent than
                    // this answer: the answer's own engine field predates it and must not overwrite
                    // what the event already set (the flicker of compito 26/9, punto 1).
                    if (_navSeq == seqAtStart)
                    {
                        changed = _navStatus != engine.NavStatus;
                        _navStatus = engine.NavStatus;
                    }
                    else
                    {
                        changed = false;
                    }

                    // A snapshot read while the channel is reconnecting is kept, but it does not make the
                    // feed live: Reconnected reads another one.
                    if (_channelOpen) changed |= SetConnection(LiveConnection.Live);
                }

                if (changed) StatusChanged?.Invoke();
                ScheduleFlush();

                bool replayed;
                lock (_sync)
                {
                    if (_snapshotAgain && !_disposed)
                    {
                        _snapshotAgain = false;
                        continue;
                    }

                    // Events that arrived while StatusChanged was being raised were held too.
                    _snapshotRunning = false;
                    _snapshotAgain = false;
                    released = true;
                    replayed = !_disposed && ReplayHeld();
                }

                if (replayed) ScheduleFlush();
                return;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Not only HTTP and JSON failures: a WcsClient disposed with its session throws
            // ObjectDisposedException, the host may answer an empty body, and a StatusChanged
            // subscriber may throw while the snapshot lands. Each of them is "the snapshot did not go
            // through" for the page, and nothing may escape towards it.
            released = true;
            SnapshotFailed();
        }
        finally
        {
            // Whatever else happened above (an OutOfMemoryException, a disposal in the middle), the
            // feed must never be left "a snapshot is in flight": every device event would be held for
            // ever, every snapshot request folded away, and the overlays would freeze without a word.
            if (!released)
            {
                bool replayed;
                lock (_sync)
                {
                    _snapshotRunning = false;
                    _snapshotAgain = false;
                    if (_disposed) _held.Clear();
                    replayed = !_disposed && ReplayHeld();
                }

                if (replayed) ScheduleFlush();
            }
        }
    }

    /// <summary>
    /// The snapshot did not go through: its flag is released, the held events go on the state the
    /// feed has, and the feed is Interrupted with a snapshot retry.
    /// </summary>
    private void SnapshotFailed()
    {
        bool told, flush;
        lock (_sync)
        {
            _snapshotRunning = false;
            _snapshotAgain = false;
            if (_disposed)
            {
                _held.Clear();
                return;
            }

            ReplayHeld();
            // Also a snapshot that was applied before a subscriber threw: its frame is still owed.
            flush = _dirty;
            told = SetConnection(LiveConnection.Interrupted);
            // Only the snapshot is retried: the channel may be perfectly healthy, and starting it
            // again would throw. If the channel is down instead, its own recovery (Reconnected, or
            // the start retry) reads the snapshot anyway.
            if (_channelOpen)
            {
                _snapshotRetry?.Dispose();
                _snapshotRetry = _time.CreateTimer(_ => RequestSnapshot(), null, RetryInterval,
                    Timeout.InfiniteTimeSpan);
            }
        }

        if (flush) ScheduleFlush();
        if (told) StatusChanged?.Invoke();
    }

    private void OnItemStatus(ItemStatus status)
    {
        lock (_sync)
        {
            if (_disposed) return;
            if (_snapshotRunning)
            {
                // The snapshot in flight would wipe it: it is replayed on top of it.
                _held[status.ItemId] = status;
                return;
            }

            _state.Apply(status);
            _dirty = true;
        }

        ScheduleFlush();
    }

    /// <summary>
    /// The engine's status, and the host's resync signal too: it sends this event when its event
    /// queue dropped some, so the snapshot is read again.
    /// </summary>
    private void OnNavigator(EngineStatus status)
    {
        lock (_sync)
        {
            if (_disposed) return;
            _navStatus = status.NavStatus;
            _navSeq++;
        }

        StatusChanged?.Invoke();
        RequestSnapshot();
        RaiseResynced();
    }

    private void OnCommand(CommandDto command)
    {
        lock (_sync)
        {
            if (_disposed) return;
        }

        CommandChanged?.Invoke(command);
    }

    private void OnMission(MissionDto mission, MissionChange change)
    {
        lock (_sync)
        {
            if (_disposed) return;
        }

        Guarded(nameof(MissionChanged), () => MissionChanged?.Invoke(mission, change));
    }

    /// <summary>
    /// Tells the GET readers to read again (see <see cref="Resynced"/>). Raised AFTER the snapshot is
    /// asked for, and guarded: a subscriber that throws must not keep the feed from its own snapshot,
    /// nor escape into a retry timer's thread.
    /// </summary>
    private void RaiseResynced()
    {
        lock (_sync)
        {
            if (_disposed) return;
        }

        Guarded(nameof(Resynced), () => Resynced?.Invoke());
    }

    /// <summary>
    /// A subscriber of the two mission events that throws stops here: they are raised on the
    /// channel's threads and on the start retry's timer thread, where an exception has nobody to
    /// catch it - the page's own handlers do not throw, and this is the net under them. Stopped, but
    /// not in silence (fix round 1): the feed's log says which event and what was thrown.
    /// </summary>
    private void Guarded(string eventName, Action raise)
    {
        try
        {
            raise();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger?.LogWarning(ex, "A subscriber of PlantLiveFeed.{Event} threw; the feed goes on without it.", eventName);
        }
    }

    /// <summary>SignalR is reconnecting by itself: the page is told, and nothing is retried here.</summary>
    private void OnReconnecting() => ChannelDown(retryStart: false);

    private void OnReconnected()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _channelOpen = true;
        }

        RequestSnapshot();
        RaiseResynced();
    }

    /// <summary>
    /// With an infinite retry policy SignalR raises Closed only when it will NOT reconnect by itself:
    /// the connection is Disconnected then, and this feed starts it again.
    /// </summary>
    private void OnClosed() => ChannelDown(retryStart: true);

    private void ChannelDown(bool retryStart)
    {
        bool told;
        lock (_sync)
        {
            if (_disposed) return;
            _channelOpen = false;
            // The channel's recovery reads the snapshot: a separate retry would only run into it.
            _snapshotRetry?.Dispose();
            _snapshotRetry = null;
            told = SetConnection(LiveConnection.Interrupted);
            if (retryStart)
            {
                _startRetry?.Dispose();
                _startRetry = _time.CreateTimer(_ => Track(OpenAsync()), null, RetryInterval,
                    Timeout.InfiniteTimeSpan);
            }
        }

        if (told) StatusChanged?.Invoke();
    }

    /// <summary>Call under <see cref="_sync"/>. Applies and forgets the held events; true if there were any.</summary>
    private bool ReplayHeld()
    {
        if (_held.Count == 0) return false;
        foreach (var status in _held.Values) _state.Apply(status);
        _held.Clear();
        _dirty = true;
        return true;
    }

    /// <summary>Call under <see cref="_sync"/>. True if the connection really changed.</summary>
    private bool SetConnection(LiveConnection connection)
    {
        if (_connection == connection) return false;
        _connection = connection;
        return true;
    }

    private Task Track(Task work)
    {
        lock (_sync) _settled = _settled.IsCompleted ? work : Task.WhenAll(_settled, work);
        return work;
    }

    private void ScheduleFlush()
    {
        lock (_sync)
        {
            if (_disposed || _flush is not null) return;
            _flush = _time.CreateTimer(_ => Flush(), null, FlushInterval, Timeout.InfiniteTimeSpan);
        }
    }

    private void Flush()
    {
        bool dirty;
        lock (_sync)
        {
            _flush?.Dispose();
            _flush = null;
            if (_disposed) return;
            dirty = _dirty;
        }

        if (!dirty) return;
        try
        {
            FrameReady?.Invoke();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Flush runs on a System.Threading.Timer thread: an exception escaping it is unhandled and
            // ENDS THE HOST PROCESS, which also runs the plant's engine. A UI subscriber must never
            // take down the process that runs the engine. The frame is still owed (_dirty stays set
            // until TakeFrame), and the next change schedules the next flush.
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _flush?.Dispose();
            _flush = null;
            _startRetry?.Dispose();
            _startRetry = null;
            _snapshotRetry?.Dispose();
            _snapshotRetry = null;
        }

        _source.ItemStatusChanged -= OnItemStatus;
        _source.NavigatorStatusChanged -= OnNavigator;
        _source.CommandChanged -= OnCommand;
        _source.MissionChanged -= OnMission;
        _source.Reconnecting -= OnReconnecting;
        _source.Reconnected -= OnReconnected;
        _source.Closed -= OnClosed;
        await _source.DisposeAsync();
    }
}
