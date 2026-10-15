using Wcs.Api.Contracts;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Live;

namespace Wcs.WebEditor.Test;

/// <summary>The host's event channel, driven by the test.</summary>
/// <remarks>
/// It keeps the one rule of SignalR's <c>HubConnection.StartAsync</c> that the feed can trip on: a
/// connection that is not Disconnected cannot be started again, and says so with
/// <see cref="InvalidOperationException"/>. A started fake stays "not Disconnected" through
/// <see cref="RaiseReconnecting"/> and <see cref="RaiseReconnected"/>, like the real one; only
/// <see cref="RaiseClosed"/> puts it back to Disconnected.
/// </remarks>
public sealed class FakePlantEventSource : IPlantEventSource
{
    private bool _connected;

    public event Action<ItemStatus>? ItemStatusChanged;
    public event Action<EngineStatus>? NavigatorStatusChanged;
    public event Action<CommandDto>? CommandChanged;
    public event Action<MissionDto, MissionChange>? MissionChanged;
    public event Action? Reconnecting;
    public event Action? Reconnected;
    public event Action? Closed;

    /// <summary>How many times StartAsync has to throw before it succeeds.</summary>
    public int FailStart { get; set; }
    public int StartCalls { get; private set; }
    public bool Disposed { get; private set; }

    public Task StartAsync(CancellationToken ct = default)
    {
        StartCalls++;
        if (_connected)
            throw new InvalidOperationException(
                "The HubConnection cannot be started if it is not in the Disconnected state (test).");
        if (StartCalls <= FailStart) throw new HttpRequestException("the hub did not answer (test)");
        _connected = true;
        return Task.CompletedTask;
    }

    public void RaiseItemStatus(ItemStatus status) => ItemStatusChanged?.Invoke(status);
    public void RaiseNavigator(EngineStatus status) => NavigatorStatusChanged?.Invoke(status);
    public void RaiseCommand(CommandDto command) => CommandChanged?.Invoke(command);
    public void RaiseMission(MissionDto mission, MissionChange change) => MissionChanged?.Invoke(mission, change);
    public void RaiseReconnecting() => Reconnecting?.Invoke();
    public void RaiseReconnected() => Reconnected?.Invoke();

    public void RaiseClosed()
    {
        _connected = false;
        Closed?.Invoke();
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        _connected = false;
        return ValueTask.CompletedTask;
    }
}
