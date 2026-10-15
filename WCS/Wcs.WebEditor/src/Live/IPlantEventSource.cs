using Wcs.Api.Contracts;
using Wcs.Api.Contracts.Dtos;

namespace Wcs.WebEditor.Live;

/// <summary>
/// The host's event channel, as the Editor uses it. <see cref="Wcs.Api.Client.PlantEventsConnection"/>
/// is a sealed SignalR connection: behind this interface the tests can drive it.
/// </summary>
public interface IPlantEventSource : IAsyncDisposable
{
    event Action<ItemStatus>? ItemStatusChanged;
    event Action<EngineStatus>? NavigatorStatusChanged;
    event Action<CommandDto>? CommandChanged;

    /// <summary>An active mission started, moved, got a new step, ended or was removed (compito 3, row 60).</summary>
    event Action<MissionDto, MissionChange>? MissionChanged;

    /// <summary>The channel fell and SignalR is trying again by itself: <see cref="Reconnected"/> or <see cref="Closed"/> follows.</summary>
    event Action? Reconnecting;

    event Action? Reconnected;

    /// <summary>
    /// The channel is down for good: with an infinite retry policy SignalR raises it only when it
    /// will NOT reconnect by itself (a stop, a disposal, a start that the policy does not cover).
    /// The connection is Disconnected then, and a new <see cref="StartAsync"/> is valid.
    /// </summary>
    event Action? Closed;

    Task StartAsync(CancellationToken ct = default);
}
