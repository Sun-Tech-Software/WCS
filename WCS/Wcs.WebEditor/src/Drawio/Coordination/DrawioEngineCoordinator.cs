using System.Text.Json;
using Wcs.Api.Client;
using Wcs.Api.Contracts.Dtos;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Drawio.Coordination;

/// <summary>
/// The engine switch's line: what is under way, or the error - and, for an error about a request that
/// failed, what came back, behind the "?" of the line (row 37 of the queue).
/// </summary>
public sealed record DrawioEngineState(string? Pending, string? Error, string? ErrorDetail = null);

/// <summary>
/// Starting and stopping the engine from the drawing (row 47). The switch does not anticipate: after
/// the command it says "Starting..." until the host's event confirms, or the command fails.
/// </summary>
/// <remarks>
/// <para>
/// <b>Starting takes the edit permit, as a save does</b> (spec, decision 2): on this page the permit
/// is taken at the moment of writing and lasts sixty seconds, and starting the engine turns every
/// page read-only - so it is refused while somebody else is writing. It is also refused while this
/// page is writing (the write gate), and while the drawing holds unsaved work.
/// </para>
/// <para>
/// <b>The start holds the write gate until it is over, not only until the POST.</b> The engine
/// reaches Run well after the command is queued (<c>ExecutableCommands.StartNavigator</c> opens the
/// PLC connections and waits before setting Run): with the gate free in that window, a save, a panel,
/// a cells write or a drop of this page could go through - the page's own GET still reads Stop -
/// and straddle the switch to Run, and a drawing edited after the unsaved-work check could be saved
/// into it. So the gate's lease is kept until the start ends: Run confirmed, a failure or refusal of
/// the engine, the POST failing, the bound giving up, a Stop sent over it, or the page going. At that
/// point the edit permit the start took is given back (<c>releasePermit</c>) - the engine running
/// keeps everybody read-only anyway, and a failed start must not leave other pages refused for the
/// rest of the minute - and only THEN is the gate freed: no write of this page can have taken the
/// permit in between and see it released under it.
/// </para>
/// <para>
/// <b>Stopping takes nothing.</b> It turns nobody read-only.
/// </para>
/// <para>
/// <b>It is never left pending for good.</b> The host's event is what normally ends a pending
/// command, and it can fail to come: the page's live channel may never have opened, or be down when
/// the event goes by. While the feed is not Live the page reads the engine's status again
/// (<c>refreshStatus</c>, which shows what it reads and so calls <see cref="EngineStatusArrived"/>)
/// and the command by its id, every <c>pollEvery</c>; and whatever the feed does, past
/// <c>giveUpAfter</c> the switch stops waiting and says it does not know. What it gave up on is
/// remembered: the status or the outcome that comes after all still corrects what the switch says,
/// until the next press.
/// </para>
/// <para>
/// Everything here runs on the page's circuit: the page calls <see cref="EngineStatusArrived"/> and
/// <see cref="CommandArrived"/> through its own live-event guard, which goes back to the circuit
/// first. No lock is needed, and none is taken.
/// </para>
/// </remarks>
internal sealed class DrawioEngineCoordinator(
    DrawioPlantWriteGate writeGate,
    Func<WcsClient> client,
    Func<Task<DrawioSaid?>> ensurePermit,
    Func<Task<DrawioSaid?>> unsavedWork,
    Func<Task> releasePermit,
    Func<NavigatorStatusTypes?> navStatus,
    Func<Task> refreshStatus,
    Func<bool> liveFeedIsLive,
    CancellationToken pageGone,
    TimeSpan pollEvery,
    TimeSpan giveUpAfter,
    Func<TimeSpan, CancellationToken, Task>? delay = null,
    TimeProvider? time = null)
{
    /// <summary>The wait between two rounds of <see cref="FollowAsync"/>: Task.Delay, or the tests' own.</summary>
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    /// <summary>The clock the bound is measured on: the system's, or the tests' own.</summary>
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>The write gate's lease of the start under way, from the press until the start ends; null otherwise.</summary>
    private DrawioPlantWriteLease? _startLease;

    /// <summary>Frees the gate if the page goes while the start holds it.</summary>
    private CancellationTokenRegistration _startLeaseOnGone;

    // Command.StatusTypes of the engine (Wcs.Engine/src/Items/Command.cs), copied: the Editor does
    // not reference the engine.
    private const short Warning = 3;
    private const short Error = 99;

    private long? _pendingCommand;
    private NavigatorStatusTypes? _awaited;

    /// <summary>
    /// The type of the command whose POST is on its way, while its id is not known yet; null otherwise.
    /// </summary>
    private CommandTypes? _sending;

    /// <summary>
    /// The outcomes of engine commands seen while a POST was on its way: the POST's own answer and
    /// the live channel have no order, and the engine can fail the command before this page knows
    /// its id. Looked up once the id is known, and emptied at the start and the end of every send.
    /// </summary>
    private readonly Dictionary<long, CommandDto> _seenWhileSending = [];

    /// <summary>
    /// The command the switch stopped waiting for at the bound, and the status it was waiting for:
    /// what arrives for it later is still told. Forgotten at the next press.
    /// </summary>
    private (long Id, NavigatorStatusTypes Awaited)? _givenUp;

    public DrawioEngineState State { get; private set; } = new(null, null);

    /// <summary>
    /// The follow-up of the last command sent (<see cref="FollowAsync"/>), for the tests to await its
    /// end instead of guessing it; completed when there is none.
    /// </summary>
    internal Task Following { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// The end of the last start's hold (<see cref="EndStart"/>: the permit given back, then the gate
    /// freed), for the tests to await; completed when there is none.
    /// </summary>
    internal Task Released { get; private set; } = Task.CompletedTask;

    public event Action? Changed;

    public async Task StartAsync()
    {
        _givenUp = null;
        if (!writeGate.TryEnter(DrawioPlantWriter.Engine, out var lease))
        {
            Set(null, DrawioWriteRefusal.ForEngine(writeGate.Current));
            return;
        }

        // Kept until the start is over, not until this method returns: see the remarks on the class.
        _startLease = lease;
        _startLeaseOnGone = pageGone.Register(() => lease!.Dispose());
        try
        {
            Set("Checking that the engine can be started...", null);
            // The whole sentence comes from the page: only it knows why its permit check refused.
            if (await ensurePermit() is { } refused)
            {
                Set(null, refused);
                return;
            }

            if (await unsavedWork() is { } unsaved)
            {
                Set(null, unsaved);
                return;
            }

            await SendAsync(CommandTypes.SetNavigatorRun, NavigatorStatusTypes.Run, "Starting the engine...", "started");
        }
        finally
        {
            // The start still pending: its hold ends with it (EndPending), not here. Anything else -
            // a refusal, the POST failing, Run already reached, the page gone - ends it now.
            if (!(_awaited == NavigatorStatusTypes.Run && _pendingCommand is not null)) EndStart();
        }
    }

    public async Task StopAsync()
    {
        var hadGivenUp = _givenUp is not null;
        _givenUp = null;
        if (navStatus() is NavigatorStatusTypes.Stop or NavigatorStatusTypes.Pause)
        {
            // Nothing to send, but still a new press: the "has not confirmed yet" of before is not left behind.
            if (hadGivenUp) Set(null, null);
            return;
        }

        // A Stop sent over a pending start ends the start: its hold goes with it. Not over a start
        // still being checked (nothing awaited yet): that one ends its hold itself when it returns.
        if (_awaited is not null) EndPending();
        await SendAsync(CommandTypes.SetNavigatorStop, NavigatorStatusTypes.Stop, "Stopping the engine...", "stopped");
    }

    /// <summary>Nothing is pending any more; a start that was ends its hold.</summary>
    private void EndPending()
    {
        _awaited = null;
        _pendingCommand = null;
        EndStart();
    }

    /// <summary>
    /// Ends the start's hold, once: the edit permit given back, THEN the gate freed. If the page is
    /// gone the gate is already free (the registration), and the permit is the page's own to release.
    /// </summary>
    private void EndStart()
    {
        if (_startLease is not { } lease) return;
        _startLease = null;
        _startLeaseOnGone.Unregister();
        Released = ReleaseStartAsync(lease);
    }

    private async Task ReleaseStartAsync(DrawioPlantWriteLease lease)
    {
        try
        {
            if (!pageGone.IsCancellationRequested) await releasePermit();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Its task is discarded: a permit not given back expires by itself, and the gate below
            // must be freed whatever happened to it.
        }
        finally
        {
            lease.Dispose();
        }
    }

    /// <summary>The engine's status as the page now shows it: ends the command waiting for exactly that status.</summary>
    public void EngineStatusArrived(NavigatorStatusTypes status)
    {
        if (_givenUp is { } givenUp && givenUp.Awaited == status)
        {
            // It came after all: "the host has not confirmed yet" is no longer true.
            _givenUp = null;
            Set(null, null);
            return;
        }

        if (_awaited is null || _awaited != status) return;
        EndPending();
        Set(null, null);
    }

    /// <summary>
    /// A command's outcome from the live channel. Only the pending command's failure (Error) or
    /// refusal (Warning: the engine did not carry it out, and no status change will follow) ends it
    /// here; its success is the status change, told by <see cref="EngineStatusArrived"/>.
    /// </summary>
    public void CommandArrived(CommandDto command)
    {
        if (_sending is { } sending)
        {
            if (command.CommandType == (int)sending) _seenWhileSending[command.Id] = command;
            return;
        }

        if (command.Status is not (Error or Warning)) return;

        if (_givenUp is { } givenUp && givenUp.Id == command.Id)
        {
            // The engine's own reason, late: it replaces "the host has not confirmed yet".
            _givenUp = null;
            Set(null, ReasonOf(command));
            return;
        }

        if (command.Id != _pendingCommand || _awaited is null) return;
        EndPending();
        Set(null, ReasonOf(command));
    }

    private static string ReasonOf(CommandDto command) => command.Status == Error
        ? $"The engine refused the command: {command.ErrorDescription}"
        : $"The engine did not carry out the command: {command.ErrorDescription}";

    private async Task SendAsync(CommandTypes type, NavigatorStatusTypes awaited, string pending, string verb)
    {
        // The operator left the page while it was checking: no command goes out after that.
        if (pageGone.IsCancellationRequested) return;
        _seenWhileSending.Clear();
        try
        {
            _awaited = awaited;
            _pendingCommand = null;
            Set(pending, null);
            _sending = type;
            CommandDto queued;
            try
            {
                queued = await client().EnqueueCommandAsync(new CommandRequest((int)type));
            }
            finally
            {
                _sending = null;
            }

            // The status may have been reached while the POST was on its way: then nothing is pending.
            if (_awaited is null) return;
            _pendingCommand = queued.Id;
            if (_seenWhileSending.TryGetValue(queued.Id, out var already)) CommandArrived(already);
            if (navStatus() == awaited) EngineStatusArrived(awaited);
            // Not awaited: the click is over once the command is sent. Started on the circuit, it
            // comes back to the circuit after every await.
            if (_pendingCommand == queued.Id) Following = FollowAsync(queued.Id, verb);
        }
        catch (Exception ex) when (ex is HttpRequestException or WcsApiException or TaskCanceledException or JsonException)
        {
            EndPending();
            // One sentence with the why in the page's words, and the host's answer - or the failure's
            // own type and message - behind its "?" (row 37 of the queue, extended to the whole page
            // on 10/10); the client's "L'API del motore ha risposto ..." used to be in it.
            Set(null, DrawioSaid.Failed($"The engine was NOT {verb} because {DrawioFailureWords.Why(ex)}.", ex));
        }
        finally
        {
            _seenWhileSending.Clear();
        }
    }

    /// <summary>
    /// Follows the pending command <paramref name="id"/> until something ends it: see the remarks on
    /// the class. Returns as soon as another command, an event or the page going away has taken over.
    /// Throws nothing: its task is discarded.
    /// </summary>
    private async Task FollowAsync(long id, string verb)
    {
        var since = _time.GetTimestamp();
        while (true)
        {
            try
            {
                await _delay(pollEvery, pageGone);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (pageGone.IsCancellationRequested || _pendingCommand != id) return;

            if (_time.GetElapsedTime(since) >= giveUpAfter)
            {
                _givenUp = _awaited is { } awaited ? (id, awaited) : null;
                EndPending();
                // Not "reload the page": a start can merely be slow (the PLC connections), and the status
                // or the outcome that comes later still corrects this line (_givenUp).
                Set(null, $"The host has not confirmed yet that the engine {verb} (no answer within {giveUpAfter.TotalSeconds:0} seconds): it may still be on its way. The engine status shown here is the last one known, and it updates by itself when the host confirms.");
                return;
            }

            // The feed tells every change while it is Live: reading on top of it would only add load.
            if (liveFeedIsLive()) continue;

            try
            {
                await refreshStatus();
                if (_pendingCommand != id || pageGone.IsCancellationRequested) return;
                if (await client().GetCommandAsync(id, pageGone) is { } read) CommandArrived(read);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Whatever it is, its task is discarded and it would go nowhere: a read that failed
                // is tried again at the next round, and the bound above still ends the wait.
                if (pageGone.IsCancellationRequested) return;
            }
        }
    }

    private void Set(string? pending, DrawioSaid? error)
    {
        State = new DrawioEngineState(pending, error?.Text, error?.Detail);
        Changed?.Invoke();
    }
}
