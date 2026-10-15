namespace Wcs.SampleWms.Test;

/// <summary>
/// Waiting for something the WCS does on its own threads (its master loop turns once a second, the
/// loopback driver completes a move after the device's delay). The wait is the condition: it is asked
/// again until it holds, and the ceiling only turns a WCS that never gets there into a failure that
/// says what was awaited and what the two sides held at that moment, instead of a test that hangs.
/// </summary>
public static class Eventually
{
    public static readonly TimeSpan Ceiling = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(200);

    public static async Task<T> Until<T>(string what, Func<Task<T>> read, Func<T, bool> holds, Func<Task<string>> diagnose)
    {
        var deadline = DateTime.UtcNow + Ceiling;
        T last;
        while (true)
        {
            last = await read();
            if (holds(last)) return last;
            if (DateTime.UtcNow > deadline) break;
            await Task.Delay(Pause);
        }

        throw new Xunit.Sdk.XunitException(
            $"Waited {Ceiling.TotalSeconds:0} s for: {what}. Last value read: {last}{Environment.NewLine}{await diagnose()}");
    }
}
