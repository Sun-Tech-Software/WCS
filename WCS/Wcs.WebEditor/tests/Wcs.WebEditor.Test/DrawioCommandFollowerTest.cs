using Wcs.Api.Client;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Drawio.Coordination;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

/// <summary>
/// Fix round 1, IMPORTANT 3: <see cref="DrawioCommandFollower"/> tested directly for re-entrancy - a
/// second <c>SendAsync</c> while one is already in flight. The outcome-following races (the live
/// channel, the resume loop, the folds) are already covered through the components that own an
/// instance of this class: <c>DrawioDeviceCommandsTest</c> and <c>DrawioOperationsTest</c>.
/// </summary>
public sealed class DrawioCommandFollowerTest
{
    private readonly DrawioWriteHandler _host = new();
    private readonly WcsClient _client;

    public DrawioCommandFollowerTest()
    {
        _client = new WcsClient(new WcsClientOptions { BaseAddress = new Uri("http://localhost:5580"), ApiKey = "test-key" }, _host);
    }

    /// <summary>No real Blazor circuit here: the dispatcher just runs the work item directly, which is enough for a unit test with no renderer to marshal onto.</summary>
    private DrawioCommandFollower Follower(string subject = "the plant") => new(() => _client, subject, work => work());

    /// <summary>
    /// A second call arrives while the first is still out on its own POST (held at
    /// <see cref="DrawioWriteHandler.CommandPostGate"/>): it must be refused outright - not queued
    /// as a second command, and not allowed to touch any of the first send's own state
    /// (<c>_lastId</c>, <c>_lastLabel</c>, the outcome line) while that first send is still using it.
    /// Before this fix, a second call would silently overwrite that state and the host would receive
    /// TWO commands for what the operator saw as a single confirmed gesture.
    /// </summary>
    [Fact]
    public async Task ASecondSendWhileOneIsInFlightIsRefused()
    {
        _host.CommandPostGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var follower = Follower();

        var first = follower.SendAsync(new CommandRequest((int)CommandTypes.RemoveAllLu), "Remove All LU", "the plant", _ => true);
        await _host.CommandPostGateReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(follower.Sending);
        Assert.Contains("Sending Remove All LU", follower.Outcome?.Text);

        // The second call, on the SAME instance, while the first is still out. A refused call returns
        // before its first await, so it is already complete here - no wait at all (final review,
        // M10): a guard that regressed would leave it held at the same gate as the first, and this
        // fails at once instead of waiting for anything.
        var second = follower.SendAsync(new CommandRequest((int)CommandTypes.SetStatusReadyAllItems), "Set All Ready", "the plant", _ => true);
        Assert.True(second.IsCompleted, "The second send was not refused outright: it is still in flight.");

        // Refused: the first send's own state is untouched, and nothing SECOND was queued on the
        // host - the first command IS already recorded here (DrawioWriteHandler records a POST /commands
        // as soon as its body is read, BEFORE the gate hold: its id is already fixed), so the
        // assertion is "still exactly one", not "none".
        Assert.True(follower.Sending);
        Assert.Contains("Sending Remove All LU", follower.Outcome?.Text);
        var recordedSoFar = Assert.Single(_host.Commands);
        Assert.Equal((int)CommandTypes.RemoveAllLu, recordedSoFar.CommandType);

        _host.CommandPostGate.SetResult();
        await first;

        Assert.False(follower.Sending);
        var sent = Assert.Single(_host.Commands);
        Assert.Equal((int)CommandTypes.RemoveAllLu, sent.CommandType);
    }

    /// <summary>
    /// Fix round 2, MINOR 4: a refused re-entrant call must not relabel the send it was refused
    /// against, even though it carries a DIFFERENT subject. Before this fix the caller (Test Route)
    /// assigned <c>follower.Subject</c> directly before calling <c>SendAsync</c>, which mutated the
    /// shared property regardless of whether the call went on to be accepted or refused; now the
    /// subject is a parameter of <c>SendAsync</c> itself, assigned only after the re-entrancy guard.
    /// </summary>
    [Fact]
    public async Task ARefusedSecondSendDoesNotRelabelTheFirst()
    {
        _host.CommandPostGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var follower = Follower();

        var first = follower.SendAsync(new CommandRequest((int)CommandTypes.TestRoute), "Test Route", "Shuttle 1 -> Shuttle 2", _ => true);
        await _host.CommandPostGateReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = follower.SendAsync(new CommandRequest((int)CommandTypes.TestRoute), "Test Route", "Shuttle 3 -> Shuttle 4", _ => true);
        Assert.True(second.IsCompleted, "The second send was not refused outright: it is still in flight.");

        Assert.Equal("Shuttle 1 -> Shuttle 2", follower.Subject);
        Assert.Contains("Shuttle 1 -> Shuttle 2", follower.Outcome?.Text);
        Assert.DoesNotContain("Shuttle 3", follower.Outcome?.Text ?? "");

        _host.CommandPostGate.SetResult();
        await first;
    }

    /// <summary>Once a send has finished, a new one is accepted normally - the guard is about RE-entrancy, not about sending more than once ever.</summary>
    [Fact]
    public async Task ASendAfterThePreviousOneFinishedIsAccepted()
    {
        var follower = Follower();

        await follower.SendAsync(new CommandRequest((int)CommandTypes.RemoveAllLu), "Remove All LU", "the plant", _ => true);
        await follower.SendAsync(new CommandRequest((int)CommandTypes.SetStatusReadyAllItems), "Set All Ready", "the plant", _ => true);

        Assert.Equal(2, _host.Commands.Count);
        Assert.Contains("Set All Ready", follower.Outcome?.Text);
    }
}
