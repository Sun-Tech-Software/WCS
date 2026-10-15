using System.Text.Json;
using Wcs.Api.Client;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Live;

namespace Wcs.WebEditor.Drawio.Coordination;

/// <summary>
/// Follows the outcome of one queued command: sent now, its outcome told later on the live channel
/// (<c>CommandChanged</c>), and resumed by asking the host directly if the channel is down when the
/// outcome would otherwise arrive. Extracted for task 1 (rows 55-57 of the parity table) out of
/// <c>DrawioDeviceCommands</c>, which owned the ORIGINAL of every race this class defends against
/// (its round 1/2/3 fixes, moved here together with the code they fixed - see the remarks below and
/// on every member). <c>DrawioDeviceCommands</c> now delegates to an instance of this class instead
/// of carrying its own copy; the whole-plant operations (Test Route, Remove All LU, Set All Ready,
/// task 1) use their own instances, one per gesture, because each can be sent independently of the
/// others and each follows its own command.
/// </summary>
/// <remarks>
/// <para>
/// <b>What replaces "the device":</b> <c>DrawioDeviceCommands</c> matched a command seen WHILE a send
/// was still on its way (id not known yet) by <c>ItemId</c>, because every command a device's own
/// buttons can send is about that one device. A whole-plant operation has no such device to match on -
/// <see cref="SendAsync"/> takes the match itself, a predicate the caller builds from whatever it does
/// know before the id comes back (the command type, for the operations in this task, exactly like
/// <c>DrawioEngineCoordinator._sending</c> does for the engine's own two commands).
/// </para>
/// <para>
/// <b>What replaces "DeviceName" in every sentence:</b> a plain <see cref="Subject"/> string - settable,
/// not fixed at construction, because <c>DrawioDeviceCommands.DeviceName</c> can change under an
/// existing instance (the same device, renamed from the properties panel while its commands are on
/// screen - the component is keyed on <c>ItemId</c>, not on the name). Every sentence below reads
/// <see cref="Subject"/> at the moment it is built, exactly as the original read <c>DeviceName</c>
/// directly, so a rename lands on whatever message is produced from then on, same as before. Row 55's
/// Test Route freezes it just before its own send instead of tracking its pickers live, for the same
/// reason in reverse (fix round 1, IMPORTANT 5) - a route already sent must keep naming what it was
/// actually sent for, not whatever the pickers say now.
/// </para>
/// <para>
/// <b>Not re-entrant, and refuses to be (fix round 1, IMPORTANT 3).</b> <see cref="SendAsync"/> owns
/// exactly one in-flight send's worth of state - <see cref="_lastId"/>, <see cref="_lastLabel"/>,
/// <see cref="_matchWhileSending"/>, <see cref="_seenWhileSending"/> - and a second call while
/// <see cref="Sending"/> is already true would overwrite all of it out from under the first send's
/// own POST, which is still going to land and look up an id that no longer means what it did: the
/// first command's early outcome (a CommandChanged that arrives before its POST returns, exactly the
/// race <see cref="_seenWhileSending"/> exists for) would be silently lost, and two commands would
/// have been queued on the host for what the operator saw as one gesture. Guarded here, not only by
/// disabling the button while <see cref="Sending"/> is true: a disabled DOM attribute is not enforced
/// by bUnit's own <c>Click()</c>, and is not a defence against a second call reaching this method by
/// any other path.
/// </para>
/// </remarks>
internal sealed class DrawioCommandFollower(Func<WcsClient> client, string subject, Func<Func<Task>, Task> dispatch)
{
    // Command.StatusTypes of the engine (Wcs.Engine/src/Items/Command.cs): copied, because the Editor
    // does not reference the engine (BoundaryTest).
    private const short Running = 1;
    private const short Done = 2;
    private const short Warning = 3;
    private const short Error = 99;

    public string Subject { get; set; } = subject;

    /// <summary>Whether a send is currently in flight: the DOM disables the buttons while true, and <see cref="SendAsync"/> itself refuses a second call while true (fix round 1, IMPORTANT 3).</summary>
    public bool Sending => _sending;

    private PlantLiveFeed? _subscribed;
    private bool _sending;
    private DrawioSaid? _outcome;
    private long? _lastId;
    private string _lastLabel = "";
    private Func<CommandDto, bool> _matchWhileSending = _ => false;

    /// <summary>
    /// The line waits for the engine's outcome of <see cref="_lastId"/>: it comes only on the live
    /// channel (<c>CommandChanged</c>), so while the live status is not updated it cannot come.
    /// </summary>
    private bool _waiting;

    /// <summary>
    /// The line said "cannot be followed" while the live channel was down and a command was still
    /// pending: true from <see cref="SayUnfollowable"/> until either a new send starts or the channel
    /// comes back and <see cref="ResumeFollowingAsync"/> resolves it, one way or the other.
    /// </summary>
    private bool _unfollowable;

    /// <summary>
    /// A <see cref="ResumeFollowingAsync"/> is in flight: while true, a further request to resume is
    /// folded into <see cref="_resumeAgain"/> instead of starting a second, overlapping read for the
    /// same id (fix round 1, reviewer's point 1d, of the ORIGINAL DrawioDeviceCommands review).
    /// </summary>
    private bool _resuming;

    /// <summary>
    /// A resume was asked for WHILE one was already in flight: the read already out may predate
    /// whatever prompted this new request (an outage that swallowed the engine's real outcome, then a
    /// reconnection - fix round 2, reviewer's point 1). Folded like <c>PlantLiveFeed._snapshotAgain</c>:
    /// the loop in <see cref="ResumeFollowingAsync"/> sees it, discards the answer it just got, and
    /// reads again instead of trusting an answer that may already be stale.
    /// </summary>
    private bool _resumeAgain;

    /// <summary>
    /// The task of the current, or the last, resume loop (<see cref="ResumeFollowingAsync"/>, folded
    /// re-reads included) - or more generally of the last reaction to the feed's <c>StatusChanged</c>,
    /// like <c>DrawioEngineCoordinator.Following</c> - for the tests to await instead of guessing with
    /// a delay. Completed when there is none in flight.
    /// </summary>
    internal Task Resumed { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Commands matching <see cref="_matchWhileSending"/> seen while a send is in flight and its id is
    /// not known yet - the two channels (the POST's own response, and <c>CommandChanged</c> on the
    /// live feed) have no ordering guarantee, so the engine can announce an outcome before
    /// <c>EnqueueCommandAsync</c> returns. Keyed by id, latest wins; cleared at the start and the end
    /// of every send, so it is bounded to exactly the one in-flight send it was collected for.
    /// </summary>
    private readonly Dictionary<long, CommandDto> _seenWhileSending = [];

    /// <summary>
    /// The outcome line: one sentence, and - for a send that failed - what came back behind its "?"
    /// (row 37 of the queue: the host's answer as it came, or the failure's own type and message, where
    /// the sentence used to quote the host's words or <c>ex.Message</c>). The panel's commands and the
    /// Operations window's Test Route, Remove All LU and Set All Ready show it the same way.
    /// </summary>
    public DrawioSaid? Outcome => _outcome;

    /// <summary>
    /// Raised whenever <see cref="Outcome"/> may have changed and the owner should re-render. Always
    /// raised on the circuit's own sync context - inside <paramref name="dispatch"/> when the trigger
    /// was the feed's own thread (<see cref="OnCommandChanged"/>, <see cref="OnFeedStatusChanged"/>),
    /// or directly from <see cref="SendAsync"/>, itself only ever called from an event handler that is
    /// already on it. A subscriber can call <c>StateHasChanged()</c> straight from the handler.
    /// </summary>
    public event Action? Changed;

    private bool FeedIsLive => _subscribed?.Connection == LiveConnection.Live;

    /// <summary>Subscribes to (or unsubscribes from) the live feed's own events - one per owning component's parameter set, as <c>DrawioDeviceCommands.OnParametersSet</c> did directly before this class existed.</summary>
    public void SetFeed(PlantLiveFeed? feed)
    {
        if (ReferenceEquals(_subscribed, feed)) return;
        if (_subscribed is not null)
        {
            _subscribed.CommandChanged -= OnCommandChanged;
            _subscribed.StatusChanged -= OnFeedStatusChanged;
        }

        _subscribed = feed;
        if (_subscribed is not null)
        {
            _subscribed.CommandChanged += OnCommandChanged;
            _subscribed.StatusChanged += OnFeedStatusChanged;
        }
    }

    /// <summary>
    /// Sends <paramref name="request"/>, labelled <paramref name="label"/> on the outcome line, and
    /// follows its outcome. <paramref name="matchWhileSending"/> decides whether a CommandChanged
    /// event seen while the id is not known yet belongs to THIS send (see the class remarks).
    /// <paramref name="subject"/> becomes <see cref="Subject"/> for THIS send - passed here rather
    /// than left to a caller's own assignment just before calling, so that a refused re-entrant call
    /// (see the remarks) can never relabel the send already in flight (fix round 2, MINOR 4).
    /// </summary>
    /// <remarks>
    /// Fix round 1, IMPORTANT 3: refuses re-entry outright. A second call while <see cref="Sending"/>
    /// is already true returns immediately, touching NONE of the in-flight send's own state - see the
    /// class remarks for what a second, unguarded call would corrupt. <see cref="Subject"/> is
    /// assigned from <paramref name="subject"/> only AFTER that guard (fix round 2, MINOR 4): a caller
    /// that used to do <c>follower.Subject = "..."; await follower.SendAsync(...)</c> - Test Route did
    /// - would still mutate the shared property even when this call goes on to be refused, corrupting
    /// whatever send IS in flight. <see cref="Subject"/> remains a public settable property for
    /// <c>DrawioDeviceCommands</c>'s own use (a device's live rename tracked continuously, independent
    /// of any particular send - see the class remarks), which this parameter does not replace.
    /// </remarks>
    public async Task SendAsync(CommandRequest request, string label, string subject, Func<CommandDto, bool> matchWhileSending)
    {
        if (_sending) return;
        _sending = true;
        Subject = subject;
        _waiting = false;
        _unfollowable = false;
        _lastId = null;
        _lastLabel = label;
        _matchWhileSending = matchWhileSending;
        _outcome = $"Sending {label} to {Subject}...";
        _seenWhileSending.Clear();
        Changed?.Invoke();
        try
        {
            var queued = await client().EnqueueCommandAsync(request);
            _lastId = queued.Id;
            _outcome = _seenWhileSending.TryGetValue(queued.Id, out var already) && OutcomeFor(already, label) is { } caughtUp
                ? caughtUp
                : $"{label} queued for {Subject} as command {queued.Id}: waiting for the engine.";
            _waiting = already is null || !IsFinal(already);
            if (_waiting && !FeedIsLive) SayUnfollowable();
        }
        // Final review, I1: three different facts, not one "NOT sent". WcsClient.EnqueueCommandAsync
        // reads the body only after a 2xx, so an unreadable body means the host DID queue the
        // command; only the host's own 4xx means it did not; no answer at all (a dropped connection,
        // a timeout - possibly after the POST arrived) or a 5xx leaves it unknown. A false "NOT sent"
        // invites a second press, and a second Test Route is a second mission. Same distinction as
        // the connection mode's own write (DrawioOperations.ConfirmModeAsync).
        catch (WcsApiException ex) when ((int)ex.StatusCode is >= 400 and < 500)
        {
            _outcome = DrawioSaid.Failed($"{label} was NOT sent to {Subject} because the host refused it.", ex);
        }
        // Final review, M9: the 503 of the engine not loaded is the filter of the "engine" group
        // (Program.cs) turning the POST away before the endpoint runs - a refusal as certain as a 4xx,
        // not one of the 5xx after which it is not known whether the command was queued.
        catch (WcsApiException ex) when (DrawioFailureWords.RefusalMeaning(ex) is { } refused)
        {
            _outcome = DrawioSaid.Failed($"{label} was NOT sent to {Subject} because {refused}.", ex);
        }
        catch (JsonException ex)
        {
            _outcome = DrawioSaid.Failed(
                $"{label} was queued for {Subject}, but the host's answer could not be read: its id is not known and its outcome cannot be followed here - check before sending it again.",
                ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or WcsApiException or TaskCanceledException)
        {
            _outcome = DrawioSaid.Failed(
                $"It is NOT known whether {label} was queued for {Subject}, because {DrawioFailureWords.Why(ex)} - check before sending it again.", ex);
        }
        finally
        {
            _sending = false;
            _seenWhileSending.Clear();
            Changed?.Invoke();
        }
    }

    private static bool IsFinal(CommandDto command) => command.Status is Done or Warning or Error;

    /// <summary>
    /// A send that <see cref="SendAsync"/> let through with a failure it does not recognise (its own
    /// catch takes only transport, host and JSON failures): said on the outcome line, which would
    /// otherwise stay on "Sending..." for ever. Whether the host queued the command is not known -
    /// the failure may have come after the POST - so nothing is followed from here on.
    /// </summary>
    public void SayTheSendFailed(string label, Exception failure)
    {
        _waiting = false;
        _unfollowable = false;
        _outcome = DrawioSaid.Failed(
            $"{label} for {Subject}: the send failed in a way this page does not know how to tell apart, and whether the host queued it is NOT known - look at the device before sending it again.",
            failure);
        Changed?.Invoke();
    }

    /// <summary>
    /// The outcome comes only on the live channel, and the live status is not being updated: said on
    /// the line instead of waiting for ever. If the channel comes back and the outcome arrives on it
    /// after all, it still replaces this line (<see cref="OnCommandChanged"/>); if instead the channel
    /// comes back and nothing has arrived on it, <see cref="ResumeFollowingAsync"/> asks the host
    /// directly (fix round 1 of the ORIGINAL DrawioDeviceCommands review - the line used to stay here
    /// for good until the outcome happened to arrive live).
    /// </summary>
    private void SayUnfollowable()
    {
        _waiting = false;
        _unfollowable = true;
        _outcome = $"{_lastLabel} queued for {Subject} as command {_lastId}, but the outcome cannot be followed while the live status is not updated.";
    }

    private void SayWaiting()
    {
        _waiting = true;
        _unfollowable = false;
        _outcome = $"{_lastLabel} queued for {Subject} as command {_lastId}: waiting for the engine.";
    }

    private string? OutcomeFor(CommandDto command, string label) => command.Status switch
    {
        Running => $"{label} for {Subject} (command {command.Id}) is being executed.",
        Done => $"{label} for {Subject} (command {command.Id}) was executed.",
        Warning => $"{label} for {Subject} (command {command.Id}) was executed with a warning: {command.ErrorDescription}",
        Error => $"{label} for {Subject} (command {command.Id}) FAILED: {command.ErrorDescription}",
        _ => null,
    };

    /// <summary>
    /// Raised on the feed's own threads (see <c>PlantLiveFeed.CommandChanged</c>): goes back to the
    /// circuit with <paramref name="dispatch"/> and never throws towards the feed, like the page's own
    /// live-event handlers (<c>RunLiveAsync</c> / <c>OnLiveEvent</c> in <c>DrawioEditor.razor</c>). It
    /// can also fire after the owning component's renderer is torn down - the feed outlives no page,
    /// but an event queued just before disposal can still land after it.
    /// </summary>
    private void OnCommandChanged(CommandDto command)
    {
        try
        {
            _ = dispatch(() =>
            {
                if (_sending)
                {
                    // The id this send will get is not known yet: an event matching this send may be
                    // this very command, arrived over the live channel ahead of the POST's own
                    // response. Remembered, not applied - SendAsync looks it up once the id is known.
                    if (_matchWhileSending(command)) _seenWhileSending[command.Id] = command;
                    return Task.CompletedTask;
                }

                if (command.Id != _lastId) return Task.CompletedTask;
                if (OutcomeFor(command, _lastLabel) is not { } outcome) return Task.CompletedTask;
                _outcome = outcome;
                _waiting = !IsFinal(command);
                // A resume read for this same id may still be in flight (ResumeFollowingAsync): its
                // own answer predates this live event and must not overwrite what it just said (fix
                // round 1, reviewer's point 1a) - clearing the flag here is what its own re-check
                // after the await relies on. Only when the outcome is final, OR the feed is already
                // Live: a non-final Running can arrive on the live channel between SignalR's own
                // Reconnected and this feed's own promotion to Live (its snapshot has not landed yet),
                // and clearing the flag there would tell a concurrent resume "already resolved" for a
                // line that is not really being followed reliably yet (fix round 2, reviewer's point 3).
                if (IsFinal(command) || FeedIsLive) _unfollowable = false;
                Changed?.Invoke();
                return Task.CompletedTask;
            });
        }
        catch (ObjectDisposedException)
        {
            // The circuit's renderer is already torn down: there is no component left to update.
        }
    }

    /// <summary>
    /// The feed's connection changed (on its own threads, like <see cref="OnCommandChanged"/>): a line
    /// still waiting for the engine says, from now on, that its outcome cannot be followed; a line
    /// that already said so, when the channel comes back, asks the host directly instead of staying
    /// stuck (<see cref="ResumeFollowingAsync"/>) - or, if one is already in flight, folds into it
    /// (<see cref="_resumeAgain"/>) instead of starting a second one.
    /// </summary>
    /// <remarks>
    /// <see cref="Resumed"/> is a fresh <see cref="TaskCompletionSource"/>'s task, assigned BEFORE
    /// <paramref name="dispatch"/> is even called, and completed from a <c>finally</c> INSIDE the
    /// dispatched lambda (fix round 3, reviewer's point 1, of the ORIGINAL DrawioDeviceCommands
    /// review) - not the task <paramref name="dispatch"/> itself returns. Blazor's dispatcher runs a
    /// dispatched lambda SYNCHRONOUSLY up to its first real await, and that can already reach a gate
    /// (a resume's own <c>await GetCommandAsync</c>) before <paramref name="dispatch"/> returns to its
    /// caller: a test that waited for the gate and then read the property straight off
    /// <paramref name="dispatch"/>'s return value could still observe the PREVIOUS cycle's
    /// already-completed task - a false green, and the fold test could go intermittently red.
    /// Assigning the property to a task this method itself owns, before dispatching anything, closes
    /// that window: a test reading <see cref="Resumed"/> right after raising the event that calls this
    /// always sees the CURRENT cycle, whatever the dispatcher's timing.
    /// </remarks>
    private void OnFeedStatusChanged()
    {
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Resumed = settled.Task;
        try
        {
            _ = dispatch(async () =>
            {
                try
                {
                    if (_sending) return;
                    if (_waiting && !FeedIsLive)
                    {
                        SayUnfollowable();
                        Changed?.Invoke();
                        return;
                    }

                    if (!_unfollowable || !FeedIsLive) return;
                    if (_resuming)
                    {
                        _resumeAgain = true;
                        return;
                    }

                    await ResumeFollowingAsync();
                }
                finally
                {
                    settled.TrySetResult();
                }
            });
        }
        catch (ObjectDisposedException)
        {
            // The circuit's renderer is already torn down: there is nothing left to dispatch to, and
            // nobody is left to await Resumed either, but it must not hang for a test that already has
            // a reference to it.
            settled.TrySetResult();
        }
    }

    /// <summary>
    /// Follows the pending command <paramref name="id"/> until something ends it: see the remarks on
    /// the class. Returns as soon as another command, an event or the owning component going away has
    /// taken over. Throws nothing: its task is discarded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every read is awaited without holding anything: by the time it lands, the line may have moved
    /// on - a new send, <see cref="OnCommandChanged"/> already closing it from a live event that
    /// arrived in the meantime, or the channel dropping again (fix round 1, reviewer's points
    /// 1a/1b/1c, of the ORIGINAL DrawioDeviceCommands review). The re-check right after each await is
    /// what makes an answer apply only to the SAME, STILL PENDING line it was asked about - never
    /// blindly.
    /// </para>
    /// <para>
    /// <b>The loop and <see cref="_resumeAgain"/>.</b> A request to resume that arrives while this one
    /// is already in flight (<see cref="OnFeedStatusChanged"/>) does not start a second, overlapping
    /// read: it folds into <see cref="_resumeAgain"/>, and this loop, seeing it once the current read
    /// lands, discards that answer and reads again instead of applying it (fix round 2, reviewer's
    /// point 1). Without the fold, an outage that swallowed the engine's real Done while a read issued
    /// BEFORE the outage was still out (and so still carries a pre-outage, non-final status) would
    /// leave the line waiting for ever: the channel coming back would find a resume already "in
    /// flight" and do nothing, and the stale read landing after would apply its own outdated answer as
    /// if it were current.
    /// </para>
    /// <para>
    /// <b>A fold can be for a DIFFERENT line.</b> <paramref name="id"/> (really the local <c>id</c>,
    /// reassignable below) is the line this loop started for, but the fold that <see cref="_resumeAgain"/>
    /// records only means "resume was asked for again while busy" - it does not say for which id. If a
    /// new send took over the line WHILE this loop was still out on its original id, and THAT new
    /// line went unfollowable and the channel came back before this loop's read finally landed, the
    /// fold is for the NEW id, not the old one: reading <see cref="_lastId"/> fresh at the top of every
    /// pass and adopting it is what carries that fold over, instead of this loop returning at its own,
    /// now-stale, id and silently wiping <see cref="_resumeAgain"/> in the <c>finally</c> below without
    /// ever having served it (fix round 3, reviewer's point 3, of the ORIGINAL DrawioDeviceCommands
    /// review).
    /// </para>
    /// </remarks>
    private async Task ResumeFollowingAsync()
    {
        if (_lastId is not { } id) return;
        _resuming = true;
        try
        {
            while (true)
            {
                CommandDto? command;
                try
                {
                    command = await client().GetCommandAsync(id);
                }
                catch (Exception ex) when (ex is HttpRequestException or WcsApiException or TaskCanceledException or JsonException or ObjectDisposedException)
                {
                    return;
                }

                if (_resumeAgain)
                {
                    _resumeAgain = false;
                    // The fold may be for a line other than the one this read was about (see the
                    // remarks above): follow it onto whatever _lastId is now, as long as THAT line is
                    // not itself moot (a send in progress, or nothing pending). Reassigning id, unlike
                    // a fresh local, changes what the NEXT read in this same loop asks for.
                    if (!_sending && _lastId is { } newId && _unfollowable)
                    {
                        id = newId;
                        continue;
                    }

                    // Nothing pending to resume for any more: whatever resolved it, resolved it.
                    return;
                }

                // The line has moved on while the read was in flight, with no fold to carry it over: a
                // new send owns it now (_sending), or a new send already finished and gave it a
                // different id - this stale answer is not about that line any more.
                if (_sending || _lastId != id) return;

                // OnCommandChanged already resolved this line (a live event arrived and was final, or
                // arrived while already Live) - this stale answer does not touch it.
                if (!_unfollowable) return;

                if (!FeedIsLive)
                {
                    // The channel dropped again while this read was in flight: its answer is moot, and
                    // the line must keep saying "cannot be followed" - not move to "waiting for the
                    // engine" while there is, again, no live status to wait on.
                    SayUnfollowable();
                    Changed?.Invoke();
                    return;
                }

                if (command is null) return;
                if (IsFinal(command) && OutcomeFor(command, _lastLabel) is { } outcome)
                {
                    _unfollowable = false;
                    // Otherwise a command this very answer just closed would still be marked as
                    // "waiting for the engine" - true only while _unfollowable is also false, but
                    // needlessly ready to flip back to "cannot be followed" on the very next outage,
                    // for a command that is not pending any more at all (fix round 3, reviewer's point 3).
                    _waiting = false;
                    _outcome = outcome;
                }
                else
                {
                    SayWaiting();
                }

                Changed?.Invoke();
                return;
            }
        }
        finally
        {
            _resuming = false;
            _resumeAgain = false;
        }
    }

    public void Dispose()
    {
        if (_subscribed is null) return;
        _subscribed.CommandChanged -= OnCommandChanged;
        _subscribed.StatusChanged -= OnFeedStatusChanged;
    }
}
