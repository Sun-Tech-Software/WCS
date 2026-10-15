using System.Collections.Concurrent;
using Log.Interface;
using Wcs.Engine.Collections;
using Wcs.Engine.Items;

namespace Wcs.Engine.Api;

/// <summary>What a pending change of a mission does when it is applied (item 54, decision 4).</summary>
public enum PendingMissionChangeKind
{
    /// <summary>A change of destination (<see cref="MissionGateway.ChangeDestinationAsync(Guid, Guid, MissionGatewayChoice)"/>).</summary>
    Destination,

    /// <summary>A move onto another device (<see cref="MissionGateway.Move(Guid, Guid)"/>).</summary>
    Position
}

/// <summary>
/// A change the operator asked while a step of the mission was in flight: it waits for the end of the
/// step, and is applied from where the step ends (item 54, decision 4 of the owner of 13/10).
/// </summary>
/// <param name="Kind">A change of destination or a move.</param>
/// <param name="ItemId">The destination chosen, or the device to move the mission onto.</param>
/// <param name="Gateway">The choice on the gateway that goes with a change of destination.</param>
/// <param name="Options">
/// What else goes with a change of destination (item 54, task 6): the relocator's arrival, or the precise
/// cell; applied with the change.
/// </param>
public sealed record PendingMissionChange(PendingMissionChangeKind Kind, Guid ItemId, MissionGatewayChoice Gateway,
    MissionDestinationOptions Options = default)
{
    public static PendingMissionChange Destination(Guid itemId, MissionGatewayChoice gateway,
        MissionDestinationOptions options = default) =>
        new(PendingMissionChangeKind.Destination, itemId, gateway, options);

    public static PendingMissionChange Position(Guid itemId) =>
        new(PendingMissionChangeKind.Position, itemId, MissionGatewayChoice.Keep);
}

/// <summary>
/// The changes that wait for the end of a step of their mission (item 54, decision 4 of the owner of
/// 13/10): one per mission, the last one asked wins, only in memory (owner's answer 4 of 13/10: a
/// restart loses it, and the Editor shows it on the mission's row while it exists).
/// </summary>
/// <remarks>
/// <para>
/// <b>The step in flight wins.</b> A change of destination or a move asked while a step of the mission
/// is in flight (<c>PathMission.StepInFlight</c>), or while a device is starting one, is not refused any
/// more: it is kept here, and the mission's steps do not start while it waits
/// (<c>PathMission.TryBeginStepStart</c> answers no), so the old route does not take one more step
/// before the change. It is applied as soon as the step ends, from the point where it ends, with the
/// same gateway calls the operator would make (<see cref="MissionGateway.ChangeDestinationAsync(Guid, Guid, MissionGatewayChoice)"/>,
/// <see cref="MissionGateway.Move(Guid, Guid)"/>), and with every rule of theirs: the route primitive of
/// item 41, the warehouse called outside every engine lock.
/// </para>
/// <para>
/// <b>When a step has ended</b> (<see cref="IsDue"/>): no step of the mission in flight and none being
/// started; and, when the step seen in flight has left the route while the mission is still where it
/// started from, its arrival is on its way (the arrival writes the position on the thread pool,
/// <c>ThreadLoadLu</c>, and the step can leave the route before or after it): the change waits for it,
/// otherwise it would compute its route from the device the load unit has just left. A step that ended
/// in error stays in the route, and its device in Warning or Error has nothing in flight: the change is
/// applied from where the load unit stopped. An arrival that never comes (a broken engine) does not keep
/// the mission waiting for ever: after <see cref="ChecksBeforeGivingUpTheArrival"/> rounds of the master
/// loop the change is applied anyway.
/// </para>
/// <para>
/// <b>Who applies it.</b> Whoever sees the step end, outside its locks: the arrival (the background work
/// of the position, <c>PathMission.PositionWork</c>), the step leaving the route
/// (<c>AutomationDevice.RemoveStep</c>), a start of a step that ends (<c>PathMission.EndStepStart</c>);
/// and, for every other way a step stops being in flight (its device going into Warning or Error), the
/// master loop, in every state of the engine (<see cref="SweepAll"/>, <c>Navigator.MasterProcess</c>).
/// They all only nudge (<see cref="Nudge"/>): the application runs on the thread pool, one at a time per
/// change, and a nudge that arrives during it makes it look again. The arrival does not await it: the
/// change can wait for the arrival's own work (the commit of a picked-up extraction that becomes a
/// deposit, <c>MissionGateway.AfterThePickUpHandOffAsync</c>), and would wait for itself.
/// </para>
/// <para>
/// <b>How it ends.</b> Applied, or refused for good (a refusal is written in the log, and the change
/// disappears from the mission's row): either way the change is gone, and the devices that can start the
/// mission's next step are woken. A refusal that passes by itself (a step in flight again, a cell search
/// of the route loop, another load unit of the same cell moving) keeps it, and it is tried again. A new
/// change asked by the operator replaces it: deferred, as the new pending change; begun, as the change
/// the operator made (<see cref="Settle"/>); with nothing to do (the destination they chose, unchanged; a
/// move onto where the mission stands, with nothing in flight), as their last word all the same. A change
/// towards the device the step is leaving, or a move onto it, waits like any other: it is applied from
/// where the step ends. A request refused before it begins (a device that does not exist, the claim of
/// the cell searches taken, the load unit standing at the destination asked) leaves it as it is.
/// </para>
/// <para>
/// <b>A postponed field rejection wins</b> (fix round 3 of task 4): a field rejection postponed for the
/// step in flight drops a pending change of destination (<see cref="DropAtAFieldRejection"/>), a change of
/// destination asked while it waits is refused at once, and one that finds it when it is applied is
/// refused for good before asking the warehouse anything. The commit refuses a route while the mission
/// waits for the rejection, and only the route loop redoes it, in Run: taken as a refusal that passes by
/// itself, the change was tried again every round of the master loop, with a cell reserved and given back
/// each time, for ever with the engine stopped.
/// </para>
/// </remarks>
internal static class PendingMissionChanges
{
    /// <summary>
    /// Rounds of the master loop (<see cref="SweepAll"/>, one a second) after which a change that waits for
    /// an arrival that does not come is applied anyway (see the remarks).
    /// </summary>
    internal const int ChecksBeforeGivingUpTheArrival = 5;

    /// <summary>A pending change, and what its application needs to know.</summary>
    internal sealed class Entry
    {
        internal Entry(PendingMissionChange change, MissionStep? inFlight)
        {
            Change = change;
            if (inFlight != null) Saw(inFlight);
        }

        internal PendingMissionChange Change { get; }

        /// <summary>The last step of the mission seen in flight, and where it went from and to.</summary>
        internal (Guid StepId, Guid From, Guid To)? Step { get; private set; }

        /// <summary>Rounds of the master loop that found the arrival of <see cref="Step"/> still on its way.</summary>
        internal int ArrivalChecks;

        internal int Applying;
        internal int Again;

        internal void Saw(MissionStep step)
        {
            Step = (step.StepId, step.FromNodeId ?? Guid.Empty, step.ToNodeId ?? Guid.Empty);
            ArrivalChecks = 0;
        }
    }

    private static readonly ConcurrentDictionary<Guid, Entry> Entries = new();
    private static readonly ConcurrentDictionary<Task, byte> Running = new();

    /// <summary>
    /// For tests only: pauses a transient application immediately before it decides whether to keep
    /// waiting. Null in production.
    /// </summary>
    internal static Func<Task>? BeforeKeepingForTesting;

    /// <summary>The change that waits for the end of the mission's step, or null.</summary>
    internal static PendingMissionChange? Of(Guid missionId) =>
        Entries.TryGetValue(missionId, out var entry) ? entry.Change : null;

    /// <summary>A change waits for the end of the mission's step: its steps do not start meanwhile.</summary>
    internal static bool Holds(Guid missionId) => Entries.ContainsKey(missionId);

    /// <summary>
    /// The change waits for the end of the step: asked by the operator (<paramref name="applying"/> null),
    /// it becomes the mission's pending change, in place of the one there was; applied from here, it stays
    /// as it is (a newer one asked meanwhile is not overwritten).
    /// </summary>
    internal static void Defer(PathMission mission, PendingMissionChange change, MissionStep? inFlight, Entry? applying)
    {
        if (applying != null)
        {
            if (inFlight != null) applying.Saw(inFlight);
            Entries.TryAdd(mission.MissionId, applying);
        }
        else
        {
            Entries[mission.MissionId] = new Entry(change, inFlight);
            Navigator.EventSink?.MissionChanged(mission, EngineMissionChange.StepChanged);
        }

        // The step may have ended between the look and the record: whoever saw it end found nothing to apply.
        Nudge(mission.MissionId);
    }

    /// <summary>
    /// A change of the mission was written, or began: the operator's last word. The pending change being
    /// applied (<paramref name="applying"/>) is gone; asked directly by the operator (null), any pending
    /// change of the mission is gone with it. Also a request with nothing to do now (the destination the
    /// operator chose, unchanged; a move onto where the mission stands) is their last word.
    /// </summary>
    /// <returns>Whether a pending change was taken out.</returns>
    internal static bool Settle(PathMission mission, Entry? applying)
    {
        var removed = applying != null
            ? Entries.TryRemove(new KeyValuePair<Guid, Entry>(mission.MissionId, applying))
            : Entries.TryRemove(mission.MissionId, out _);

        if (removed) Navigator.EventSink?.MissionChanged(mission, EngineMissionChange.StepChanged);
        return removed;
    }

    /// <summary>
    /// A field rejection of the mission was postponed for its step in flight (<c>PathMission.FieldRejectionPostponed</c>):
    /// the rejection wins over a change of destination waiting for the end of the same step (item 54, fix
    /// round 3 of task 4), which is dropped, with the reason in the log. A pending move stays: the move
    /// writes without the comparison of the version, and the route loop redoes the rejection from where it
    /// puts the load unit.
    /// </summary>
    internal static void DropAtAFieldRejection(PathMission mission)
    {
        if (!Entries.TryGetValue(mission.MissionId, out var entry) ||
            entry.Change.Kind != PendingMissionChangeKind.Destination ||
            !Entries.TryRemove(new KeyValuePair<Guid, Entry>(mission.MissionId, entry)))
            return;

        Navigator.EventSink?.MissionChanged(mission, EngineMissionChange.StepChanged);
        Navigator.WriteLog(
            $"Missione {mission.MissionId}: il cambio in sospeso ({entry.Change.Kind} verso {entry.Change.ItemId}) cade, il rigetto dal campo rimandato per lo step in volo vince",
            LogEntryType.Warning);
    }

    /// <summary>
    /// Someone saw a step of the mission end, or might have: if a change waits for it, its application
    /// starts on the thread pool. Never under the caller's locks, never waited for.
    /// </summary>
    internal static void Nudge(Guid missionId)
    {
        if (!Entries.TryGetValue(missionId, out var entry)) return;
        Track(Task.Run(() => TryApplyAsync(missionId, entry, sweep: false)));
    }

    /// <summary>
    /// The master loop's round (<c>Navigator.MasterProcess</c>, in every state of the engine): every
    /// pending change is looked at, for the ways a step stops being in flight that nobody announces.
    /// </summary>
    internal static void SweepAll()
    {
        foreach (var (missionId, entry) in Entries.ToArray())
            Track(Task.Run(() => TryApplyAsync(missionId, entry, sweep: true)));
    }

    private static async Task TryApplyAsync(Guid missionId, Entry entry, bool sweep)
    {
        Interlocked.Exchange(ref entry.Again, 1);
        if (Interlocked.CompareExchange(ref entry.Applying, 1, 0) != 0) return;

        try
        {
            while (Interlocked.Exchange(ref entry.Again, 0) == 1)
            {
                if (!IsCurrent(missionId, entry)) return;

                var mission = ActiveMissions.Instance[missionId];
                if (mission == null || mission.IsWithdrawn)
                {
                    Entries.TryRemove(new KeyValuePair<Guid, Entry>(missionId, entry));
                    Navigator.WriteLog(
                        $"Missione {missionId}: il cambio in sospeso ({entry.Change.Kind} verso {entry.Change.ItemId}) cade, la missione non e' piu' attiva",
                        LogEntryType.Information);
                    return;
                }

                if (!IsDue(mission, entry, sweep)) continue;

                var (outcome, written, waitsAgain) = await ApplyAsync(mission, entry);

                if (waitsAgain)
                {
                    // A refusal that passes by itself: the change stays (the gateway keeps it in the
                    // registry until the outcome of a change that began is known, fix round 2 of task 4:
                    // it is there when the change ends and wakes the devices), and the next nudge, or
                    // the master loop, tries it again. The application itself never took it out. If it
                    // is no longer the current entry, a newer operator request deliberately removed or
                    // replaced it: putting it back would make the older request win afterwards.
                    if (BeforeKeepingForTesting is { } beforeKeeping) await beforeKeeping();
                    if (!IsCurrent(missionId, entry)) return;
                    continue;
                }

                var dropped = Entries.TryRemove(new KeyValuePair<Guid, Entry>(missionId, entry));
                if (dropped) Navigator.EventSink?.MissionChanged(mission, EngineMissionChange.StepChanged);

                Navigator.WriteLog(
                    $"Missione {missionId}: il cambio in sospeso ({entry.Change.Kind} verso {entry.Change.ItemId}) applicato a fine step: {outcome}",
                    written ? LogEntryType.Information : LogEntryType.Warning);

                // The starts given up while the change waited: a refusal, or a change with nothing to
                // do, leaves them to nobody (a change written wakes them too, and once more is harmless).
                MissionGateway.WakeAfterAPendingChange(mission);
                return;
            }
        }
        catch (Exception ex)
        {
            Navigator.WriteLog(
                $"Missione {missionId}: errore nell'applicare il cambio in sospeso ({ex.Message})\r\nStackTrace: {ex.StackTrace}",
                LogEntryType.Error);
        }
        finally
        {
            Volatile.Write(ref entry.Applying, 0);
            if (Volatile.Read(ref entry.Again) == 1 && IsCurrent(missionId, entry)) Nudge(missionId);
        }
    }

    private static bool IsCurrent(Guid missionId, Entry entry) =>
        Entries.TryGetValue(missionId, out var now) && ReferenceEquals(now, entry);

    /// <summary>Has the step the change waits for ended? See the remarks of the class.</summary>
    internal static bool IsDue(PathMission mission, Entry entry, bool sweep)
    {
        if (mission.StepInFlight() is { } inFlight)
        {
            entry.Saw(inFlight);
            return false;
        }

        if (mission.StepStarting) return false;

        if (entry.Step is { } seen && seen.From != seen.To && mission.PositionItemId == seen.From &&
            !MissionSteps.Instance.Any(step => step.StepId == seen.StepId))
        {
            // The step left the route and the load unit is not where it went yet: its arrival is on its
            // way. Only the master loop counts the rounds of a wait for an arrival that does not come.
            if (!sweep) return false;
            return Interlocked.Increment(ref entry.ArrivalChecks) >= ChecksBeforeGivingUpTheArrival;
        }

        return true;
    }

    /// <summary>
    /// The change, through the gateway, as the operator would ask it: its outcome, whether it was written
    /// (the mission moved, or changed destination), and whether it has to wait again.
    /// </summary>
    private static async Task<(string Outcome, bool Written, bool WaitsAgain)> ApplyAsync(PathMission mission, Entry entry)
    {
        var change = entry.Change;

        if (change.Kind == PendingMissionChangeKind.Position)
        {
            var moved = MissionGateway.Move(mission.MissionId, change.ItemId, entry);
            return (moved.ToString(), moved == MissionMoveOutcome.Moved, moved == MissionMoveOutcome.Pending);
        }

        // Item 54, fix round 3 of task 4: the mission waits for a postponed field rejection, that only the
        // route loop redoes (in Run): the commit would refuse the change, after a reservation of a cell in
        // the warehouse given back at once, and the refusal does not pass by itself in Stop. The rejection
        // wins, and the change is refused for good before asking the warehouse anything.
        if (mission.AwaitsFieldRejection)
            return ("StepInFlight: la missione aspetta il rigetto dal campo, rimandato per uno step che era in volo", false, false);

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, change.ItemId, change.Gateway, change.Options, entry);
        return (result.Outcome.ToString(), result.Outcome == MissionDestinationOutcome.Changed,
            result.Outcome == MissionDestinationOutcome.Pending || PassesByItself(result.Outcome));
    }

    /// <summary>
    /// A refusal of a change of destination that passes by itself (a step in flight, a cell search of the
    /// route loop, another load unit of the same cell moving): a pending change that meets it keeps waiting.
    /// A commit refused because the mission waits for a postponed field rejection is a StepInFlight too, but
    /// does not get here: the application refuses it for good before (<see cref="ApplyAsync"/>).
    /// </summary>
    internal static bool PassesByItself(MissionDestinationOutcome outcome) =>
        outcome is MissionDestinationOutcome.StepInFlight or MissionDestinationOutcome.CellSearchInFlight
            or MissionDestinationOutcome.CopyInFlight;

    private static void Track(Task work)
    {
        BackgroundWork.Track(work);
        if (work.IsCompleted) return;
        Running.TryAdd(work, 0);
        work.ContinueWith(static done => Running.TryRemove(done, out _), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>For the tests: every application started so far, and those they started, have ended.</summary>
    internal static async Task WhenIdleAsync()
    {
        while (Running.Keys.ToArray() is { Length: > 0 } running)
            await Task.WhenAll(running).ContinueWith(static _ => { }, TaskScheduler.Default);
    }

    /// <summary>The engine loads again (<c>Navigator.LoadNavigatorData</c>): the pending changes, only in memory, are gone.</summary>
    internal static void Reset()
    {
        Entries.Clear();
        BeforeKeepingForTesting = null;
    }
}
