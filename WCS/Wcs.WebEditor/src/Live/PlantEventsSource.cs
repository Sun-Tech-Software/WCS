using Wcs.Api.Client;
using Wcs.Api.Contracts;
using Wcs.Api.Contracts.Dtos;

namespace Wcs.WebEditor.Live;

/// <summary>The real channel: a <see cref="PlantEventsConnection"/> towards the host that serves this page.</summary>
internal sealed class PlantEventsSource : IPlantEventSource
{
    private readonly PlantEventsConnection _connection;

    public PlantEventsSource(WcsClientOptions options)
    {
        _connection = new PlantEventsConnection(options);
        _connection.ItemStatusChanged += status => ItemStatusChanged?.Invoke(status);
        _connection.NavigatorStatusChanged += status => NavigatorStatusChanged?.Invoke(status);
        _connection.CommandChanged += command => CommandChanged?.Invoke(command);
        _connection.MissionChanged += (mission, change) => MissionChanged?.Invoke(mission, change);
        _connection.Reconnecting += () => Reconnecting?.Invoke();
        _connection.Reconnected += () => Reconnected?.Invoke();
        _connection.Closed += () => Closed?.Invoke();
    }

    public event Action<ItemStatus>? ItemStatusChanged;
    public event Action<EngineStatus>? NavigatorStatusChanged;
    public event Action<CommandDto>? CommandChanged;
    public event Action<MissionDto, MissionChange>? MissionChanged;
    public event Action? Reconnecting;
    public event Action? Reconnected;
    public event Action? Closed;

    public Task StartAsync(CancellationToken ct = default) => _connection.StartAsync(ct);
    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}

/// <summary>
/// A channel that connects and says nothing: what a session built with a fake HTTP handler gets when
/// its test did not ask for a <see cref="IPlantEventSource"/> of its own. The page tests that do not
/// speak about the live status must not open a SignalR connection towards a host that is not there.
/// </summary>
internal sealed class QuietPlantEventSource : IPlantEventSource
{
    public event Action<ItemStatus>? ItemStatusChanged { add { } remove { } }
    public event Action<EngineStatus>? NavigatorStatusChanged { add { } remove { } }
    public event Action<CommandDto>? CommandChanged { add { } remove { } }
    public event Action<MissionDto, MissionChange>? MissionChanged { add { } remove { } }
    public event Action? Reconnecting { add { } remove { } }
    public event Action? Reconnected { add { } remove { } }
    public event Action? Closed { add { } remove { } }

    public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
