using System.Runtime.CompilerServices;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// Raises the thread pool's minimum of worker threads in this test process before any test runs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every child process this project reads holds a pool thread for its whole life.</b> On Windows
/// <c>Process</c> opens the redirected pipes as synchronous handles (<c>new FileStream(..., isAsync:
/// false)</c>), and an asynchronous read on a synchronous handle is a blocking read run on a pool
/// thread (<c>RandomAccess.ReadAtOffsetAsync</c>, then <c>AsyncOverSyncWithIoCancellation</c>, in
/// .NET 10.0.12). <see cref="WcsHostProcess"/> reads stdout and stderr of each host that way
/// (<c>BeginOutputReadLine</c>, <c>BeginErrorReadLine</c>), and Playwright reads its driver the same
/// way: about three blocked threads per class fixture. xunit runs one class per core, so the first
/// wave starts some twenty hosts at once against a minimum equal to the core count (20 here).
/// </para>
/// <para>
/// MEASURED on 9/10 with a probe in <see cref="WcsHostProcess"/>, on a run that passed: the pool
/// grew one thread a second, from 20 to 80, with up to 45 work items waiting, and the first line
/// of one host reached the test 67 s after the host was started. That is the slow path the pool
/// takes when starved, and above 80% CPU it gets much slower: the pool adds a thread only after
/// NumThreadsGoal seconds without a dequeue (<c>PortableThreadPool.GateThread
/// .SufficientDelaySinceLastDequeue</c>), against 0.5 s otherwise. With other builds and test runs
/// on the machine it practically stops growing, and on 8/10 85 and then 173 tests failed with
/// "non ha dichiarato la propria porta entro 180 secondi" while the host's own log said it was
/// listening: the line was there, nobody had a thread to read it.
/// </para>
/// <para>
/// A minimum does not create threads up front: below it the pool creates one as soon as work is
/// waiting, without the starvation delay.
/// </para>
/// </remarks>
internal static class ThreadPoolHeadroom
{
    /// <summary>
    /// Three blocked threads per fixture, a fixture per core, and room for the work they are there
    /// to wait for: well above what the suite holds at once.
    /// </summary>
    internal const int MinimumWorkerThreads = 256;

#pragma warning disable CA2255 // A test assembly is the place for it: it must run before any fixture.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Raise()
    {
        ThreadPool.GetMinThreads(out var workerThreads, out var completionPortThreads);
        if (workerThreads < MinimumWorkerThreads)
            ThreadPool.SetMinThreads(MinimumWorkerThreads, completionPortThreads);
    }
}
