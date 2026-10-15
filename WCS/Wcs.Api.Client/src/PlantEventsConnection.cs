using Microsoft.AspNetCore.SignalR.Client;
using Wcs.Api.Contracts;
using Wcs.Api.Contracts.Dtos;

namespace Wcs.Api.Client;

/// <summary>
/// L'hub visto dal client: un evento C# per ogni messaggio, riconnessione automatica, e
/// <see cref="Reconnected"/> per chi deve ricaricare lo stato completo (la regola: prima GET,
/// poi le differenze; a ogni riconnessione si ripete). Anche la negoziazione dell'hub invia la
/// chiave soltanto nell'header X-Api-Key; la query access_token non e' supportata.
/// </summary>
/// <remarks>
/// La riconnessione non si arrende mai (<see cref="InfiniteRetryPolicy"/>): un client aperto
/// durante un riavvio dell'host torna da solo. Anche <see cref="NavigatorStatusChanged"/> vale
/// come invito a ricaricare lo stato completo: l'host lo manda quando la sua coda di eventi ne
/// ha scartati e la sequenza delle differenze ha un buco.
/// </remarks>
public sealed class PlantEventsConnection : IAsyncDisposable
{
    private readonly HubConnection _connection;

    public PlantEventsConnection(WcsClientOptions options, HttpMessageHandler? inner = null)
    {
        _connection = new HubConnectionBuilder()
            .WithUrl(new Uri(options.BaseAddress, ApiRoutes.Hub.TrimStart('/')), o =>
            {
                o.Headers[ApiRoutes.ApiKeyHeader] = options.ApiKey;
                if (inner != null) o.HttpMessageHandlerFactory = _ => inner;
            })
            .WithAutomaticReconnect(new InfiniteRetryPolicy())
            .Build();

        _connection.On<ItemStatus>(nameof(IPlantEvents.ItemStatusChanged), s => ItemStatusChanged?.Invoke(s));
        _connection.On<EngineStatus>(nameof(IPlantEvents.NavigatorStatusChanged), s => NavigatorStatusChanged?.Invoke(s));
        _connection.On<MissionDto, MissionChange>(nameof(IPlantEvents.MissionChanged), (m, c) => MissionChanged?.Invoke(m, c));
        _connection.On<ExtractionQueueItemDto, QueueChange>(nameof(IPlantEvents.ExtractionQueueChanged), (i, c) => ExtractionQueueChanged?.Invoke(i, c));
        _connection.On<CommandDto>(nameof(IPlantEvents.CommandChanged), c => CommandChanged?.Invoke(c));
        _connection.On<DateTime>(nameof(IPlantEvents.Watchdog), t => Watchdog?.Invoke(t));
        _connection.Reconnecting += _ => { Reconnecting?.Invoke(); return Task.CompletedTask; };
        _connection.Reconnected += _ => { Reconnected?.Invoke(); return Task.CompletedTask; };
        _connection.Closed += _ => { Closed?.Invoke(); return Task.CompletedTask; };
    }

    public event Action<ItemStatus>? ItemStatusChanged;
    public event Action<EngineStatus>? NavigatorStatusChanged;
    public event Action<MissionDto, MissionChange>? MissionChanged;
    public event Action<ExtractionQueueItemDto, QueueChange>? ExtractionQueueChanged;
    public event Action<CommandDto>? CommandChanged;
    public event Action<DateTime>? Watchdog;
    /// <summary>The channel fell and SignalR is trying again by itself: <see cref="Reconnected"/> or <see cref="Closed"/> follows.</summary>
    public event Action? Reconnecting;
    public event Action? Reconnected;
    public event Action? Closed;

    public bool IsConnected => _connection.State == HubConnectionState.Connected;
    public Task StartAsync(CancellationToken ct = default) => _connection.StartAsync(ct);
    public Task StopAsync(CancellationToken ct = default) => _connection.StopAsync(ct);
    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
