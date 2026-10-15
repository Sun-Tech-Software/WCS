using Wcs.Api.Client;
using Wcs.Api.Contracts.Dtos;
using Xunit.Abstractions;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// The test host's engine as the tests that start it leave it: stopped. Written once for the three
/// test classes that start it (the engine switch, Operations' Test Route, the missions' seed), which
/// carried three verbatim copies until compito 3's fix round 1.
/// </summary>
public static class TestHostEngine
{
    /// <summary>How long a condition of the host is waited for before it is declared not reached.</summary>
    public static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Whatever happened before, the host is left stopped. A Stop goes out ALWAYS: a status that
    /// reads Stop now says nothing of a Run still queued behind it, which the engine would carry out
    /// after the test. Then the wait for the Stop command's own outcome (the engine has taken it),
    /// and then for the status Stop, both bounded. A cleanup that fails is written to the output and
    /// not thrown: from a finally it would replace the failure the test itself is reporting.
    /// </summary>
    public static async Task LeaveStoppedAsync(WcsClient client, ITestOutputHelper output)
    {
        try
        {
            var stop = await client.EnqueueCommandAsync(new CommandRequest((int)CommandTypes.SetNavigatorStop));
            if (!await WithinAsync(async () => (await client.GetCommandAsync(stop.Id))?.Status is { } status && status != 0))
            {
                output.WriteLine($"CLEANUP: the engine did not take the Stop command {stop.Id} within {Ceiling.TotalSeconds:0} seconds.");
                return;
            }

            if (!await WithinAsync(async () => (await client.GetStatusAsync()).NavStatus == NavigatorStatusTypes.Stop))
                output.WriteLine($"CLEANUP: the host's engine did not reach Stop within {Ceiling.TotalSeconds:0} seconds.");
        }
        catch (Exception ex)
        {
            output.WriteLine($"CLEANUP FAILED: the host's engine may be left running ({ex.GetType().Name}: {ex.Message}).");
        }
    }

    /// <summary>Asks <paramref name="condition"/> of the host until it holds or <see cref="Ceiling"/> has gone by: true if it held.</summary>
    public static async Task<bool> WithinAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + Ceiling;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) return false;
            await Task.Delay(PollInterval);
        }

        return true;
    }
}
