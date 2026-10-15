using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using Wcs.Api.Contracts;
using Wcs.Engine.Api;
using Wcs.Engine.Items;
using Wcs.Engine.Persistence.Entities;
using Wcs.Host.Hubs;
using Wcs.Host.Mapping;

namespace Wcs.Host.Engine;

/// <summary>
/// Il sink del motore: riceve gli eventi sul thread del motore, li mette in coda e un solo
/// consumatore li spinge all'hub. Il motore non aspetta mai la rete. Il watchdog batte ogni
/// secondo finché il servizio vive.
/// </summary>
/// <remarks>
/// <para>
/// Che cosa gira su quale thread: il DTO si costruisce <b>sul thread del motore</b>, dentro la
/// chiamata del sink, perché l'oggetto vivo può cambiare un istante dopo; in coda finisce solo
/// l'invio già confezionato. Mappa e accodamento stanno dentro un <c>try</c>: un guasto qui non
/// deve rompere la scrittura o la decisione del motore da cui l'evento è nato.
/// </para>
/// <para>
/// La coda tiene diecimila voci e a coda piena scarta la più vecchia. Quando qualcosa è stato
/// scartato il consumatore, appena svuotata la coda, manda un <c>NavigatorStatusChanged</c>: per
/// i client quel messaggio vale anche come <b>«ricarica lo stato completo»</b>, perché la
/// sequenza delle differenze ha un buco.
/// </para>
/// </remarks>
public sealed class PlantEventRelay : IEngineEventSink, IHostedService
{
    private readonly IHubContext<PlantHub, IPlantEvents> _hub;
    private readonly ILogger<PlantEventRelay> _logger;
    private readonly Channel<Func<IPlantEvents, Task>> _queue;

    private CancellationTokenSource? _cts;
    private Task? _pump;
    private Task? _tick;
    private PeriodicTimer? _watchdog;

    /// <summary>Alzato quando un evento è stato scartato: il prossimo svuotamento della coda manda il segnale di riallineamento.</summary>
    private int _resyncNeeded;

    /// <summary>Quando è stata scritta l'ultima riga di registro sugli scarti: una per raffica, non una per evento.</summary>
    private long _lastDropLog;

    public PlantEventRelay(IHubContext<PlantHub, IPlantEvents> hub, ILogger<PlantEventRelay> logger)
    {
        _hub = hub;
        _logger = logger;
        _queue = Channel.CreateBounded<Func<IPlantEvents, Task>>(
            new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true },
            _ => NoteDrop());
    }

    public void ItemChanged(AutomationDevice device) =>
        Relay(nameof(ItemChanged), () => { var dto = DtoMapping.ToStatus(device); Post(c => c.ItemStatusChanged(dto)); });

    public void NavigatorStatusChanged() =>
        Relay(nameof(NavigatorStatusChanged), () => { var dto = DtoMapping.Status(); Post(c => c.NavigatorStatusChanged(dto)); });

    public void MissionChanged(PathMission mission, EngineMissionChange change) =>
        Relay(nameof(MissionChanged), () => { var dto = DtoMapping.ToDto(mission); Post(c => c.MissionChanged(dto, (MissionChange)change)); });

    public void ExtractionQueueChanged(ExtractionQueueItem item, EngineQueueChange change) =>
        Relay(nameof(ExtractionQueueChanged), () => { var dto = DtoMapping.ToDto(item); Post(c => c.ExtractionQueueChanged(dto, (QueueChange)change)); });

    public void CommandChanged(CommandRecord command) =>
        Relay(nameof(CommandChanged), () => { var dto = DtoMapping.ToDto(command); Post(c => c.CommandChanged(dto)); });

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        _watchdog = new PeriodicTimer(TimeSpan.FromSeconds(1));
        _pump = Task.Run(() => PumpAsync(_cts.Token), CancellationToken.None);
        _tick = Task.Run(() => TickAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <remarks>
    /// Idempotente e al riparo da due arresti in parallelo: chi prende il <c>CancellationTokenSource</c>
    /// se lo porta via, gli altri escono subito. L'host di prova lo chiama due volte (<c>Dispose</c>
    /// e <c>DisposeAsync</c>), e leggere i campi due volte lasciava passare un riferimento nullo.
    /// </remarks>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts == null) return;

        var watchdog = _watchdog;
        var running = new[] { _pump, _tick }.Where(task => task != null).Select(task => task!).ToArray();
        _watchdog = null;
        _pump = null;
        _tick = null;

        await cts.CancelAsync();
        try
        {
            if (running.Length > 0)
                await Task.WhenAll(running).WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // L'arresto è quello che abbiamo chiesto noi.
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Il relay degli eventi non si è fermato entro due secondi: si prosegue con l'arresto.");
        }
        finally
        {
            watchdog?.Dispose();
            cts.Dispose();
        }
    }

    /// <summary>Mappa e accoda sotto guardia: il motore non deve mai vedere un'eccezione che nasce qui.</summary>
    private void Relay(string what, Action mapAndPost)
    {
        try
        {
            mapAndPost();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Evento {Event} non inoltrato: la mappatura o l'accodamento sono falliti", what);
        }
    }

    private void Post(Func<IPlantEvents, Task> send)
    {
        if (!_queue.Writer.TryWrite(send)) NoteDrop();
    }

    /// <summary>
    /// Un evento è caduto: si alza il flag e si scrive una riga di registro, ma non una per evento
    /// — a coda piena sarebbero migliaia. Gira sul thread del motore: deve restare economico.
    /// </summary>
    private void NoteDrop()
    {
        Volatile.Write(ref _resyncNeeded, 1);

        var now = Environment.TickCount64;
        var previous = Interlocked.Read(ref _lastDropLog);
        if (now - previous < 5_000) return;
        if (Interlocked.CompareExchange(ref _lastDropLog, now, previous) != previous) return;

        _logger.LogWarning(
            "Coda degli eventi piena: eventi scartati. Ai client verrà mandato NavigatorStatusChanged come segnale di riallineamento.");
    }

    private async Task PumpAsync(CancellationToken token)
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync(token))
            {
                while (_queue.Reader.TryRead(out var send))
                {
                    try { await send(_hub.Clients.All); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Evento non consegnato all'hub"); }
                }

                await ResyncIfSomethingWasDroppedAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // Arresto del servizio.
        }
    }

    private async Task TickAsync(CancellationToken token)
    {
        var watchdog = _watchdog;
        if (watchdog == null) return;

        try
        {
            while (await watchdog.WaitForNextTickAsync(token))
                Post(c => c.Watchdog(DateTime.UtcNow));
        }
        catch (OperationCanceledException)
        {
            // Arresto del servizio.
        }
        catch (ObjectDisposedException)
        {
            // Il timer è stato disposto mentre si aspettava il tick: è l'arresto.
        }
    }

    /// <summary>
    /// Coda svuotata: se qualcosa era caduto, i client hanno un buco nella sequenza delle
    /// differenze e devono ricaricare. Il segnale è <c>NavigatorStatusChanged</c>.
    /// </summary>
    private async Task ResyncIfSomethingWasDroppedAsync()
    {
        if (Interlocked.Exchange(ref _resyncNeeded, 0) == 0) return;

        try { await _hub.Clients.All.NavigatorStatusChanged(DtoMapping.Status()); }
        catch (Exception ex) { _logger.LogWarning(ex, "Segnale di riallineamento non consegnato all'hub"); }
    }
}
