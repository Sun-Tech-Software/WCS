using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;

namespace Wcs.SampleWms.Test;

/// <summary>
/// A WCS host started as a real process from its build output, on a free loopback port, with its own
/// temporary data folder (so its own <c>wcs.db</c>) and its own key, and pointed at a WMS address.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a process.</b> The engine is static: one per process. A host in this process would share it
/// with whatever else runs here, and it would not be the host a user starts.
/// </para>
/// <para>
/// <b>Which binary.</b> The test project references <c>Wcs.Host</c> without using its assembly, only so
/// that a build of the tests builds the host too; the executable is then taken from the host's own
/// output folder, for the configuration the tests were built in.
/// </para>
/// </remarks>
public sealed class WcsHostProcess : IAsyncDisposable
{
    private const string ListeningPrefix = "Now listening on: ";
    private const int LogLines = 400;

    /// <summary>
    /// The ceiling of the wait for the host to announce its port. Not a timed wait: the wait is the
    /// "Now listening on:" line, and the process exiting ends it at once.
    /// </summary>
    private static readonly TimeSpan StartCeiling = TimeSpan.FromMinutes(2);

    private readonly Process _process;
    private readonly string _dataDirectory;
    private readonly ConcurrentQueue<string> _log;

    private WcsHostProcess(Process process, string dataDirectory, ConcurrentQueue<string> log, Uri baseAddress, string apiKey)
    {
        _process = process;
        _dataDirectory = dataDirectory;
        _log = log;
        BaseAddress = baseAddress;
        ApiKey = apiKey;
    }

    public Uri BaseAddress { get; }
    public string ApiKey { get; }

    /// <summary>The folder of the host's databases (wcs.db and the history), deleted on dispose.</summary>
    public string DataDirectory => _dataDirectory;

    /// <summary>The last lines the host wrote, for a failure message.</summary>
    public string LogTail() => string.Join(Environment.NewLine, _log);

    public static async Task<WcsHostProcess> StartAsync(Uri warehouseAddress, string warehouseApiKey)
    {
        var executable = HostExecutable();
        var dataDirectory = Directory.CreateTempSubdirectory("wcs-sample-e2e-").FullName;
        var apiKey = "sample-e2e-" + Guid.NewGuid().ToString("N");
        var port = SampleWmsServer.FreePort();

        var start = new ProcessStartInfo(executable.FileName)
        {
            WorkingDirectory = Path.GetDirectoryName(executable.Assembly)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in executable.Arguments) start.ArgumentList.Add(argument);

        start.Environment["Wcs__ApiKey"] = apiKey;
        start.Environment["Wcs__DatabaseDirectory"] = dataDirectory;
        start.Environment["Wcs__StartEngine"] = "true";
        start.Environment["Wcs__AutoRun"] = "false";
        start.Environment["Wms__ApiAddress"] = warehouseAddress.ToString();
        start.Environment["Wms__ApiKey"] = warehouseApiKey;
        start.Environment["Kestrel__Endpoints__Http__Url"] = $"http://127.0.0.1:{port}";
        start.Environment["Logging__LogLevel__Microsoft.Hosting.Lifetime"] = "Information";
        // Every call to the WMS is logged at Information by HttpClient: thousands of lines that would
        // push the engine's own lines out of the tail a failure message shows.
        start.Environment["Logging__LogLevel__System.Net.Http.HttpClient"] = "Warning";

        var log = new ConcurrentQueue<string>();
        var listening = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };

        void Record(string? line)
        {
            if (line is null) return;
            log.Enqueue(line);
            while (log.Count > LogLines) log.TryDequeue(out _);

            var at = line.IndexOf(ListeningPrefix, StringComparison.Ordinal);
            if (at >= 0 && Uri.TryCreate(line[(at + ListeningPrefix.Length)..].Trim(), UriKind.Absolute, out var address))
                listening.TrySetResult(address);
        }

        process.OutputDataReceived += (_, e) => Record(e.Data);
        process.ErrorDataReceived += (_, e) => Record(e.Data);
        process.Exited += (_, _) =>
        {
            process.WaitForExit();
            listening.TrySetException(new InvalidOperationException(
                $"The WCS host ({executable.FileName}) exited with code {process.ExitCode} before listening. Its last lines:" +
                Environment.NewLine + string.Join(Environment.NewLine, log)));
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var host = new WcsHostProcess(process, dataDirectory, log, new Uri($"http://127.0.0.1:{port}"), apiKey);
        try
        {
            await listening.Task.WaitAsync(StartCeiling);
            return host;
        }
        catch (TimeoutException)
        {
            var tail = host.LogTail();
            await host.DisposeAsync();
            throw new InvalidOperationException(
                $"The WCS host did not announce its port within {StartCeiling.TotalSeconds:0} s. Its last lines:{Environment.NewLine}{tail}");
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }

        _process.Dispose();

        try
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
        catch (IOException)
        {
            // SQLite may hold a file for an instant after the exit; the folder is under the system's
            // temporary folder with a unique name, so leaving it does not disturb anything.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record Launch(string FileName, IReadOnlyList<string> Arguments, string Assembly);

    /// <summary>
    /// The host's build output for the configuration of this test assembly:
    /// <c>WCS/Wcs.Host/src/bin/{Configuration}/net10.0/</c>. The apphost (<c>Wcs.Host.exe</c>) on Windows,
    /// <c>dotnet Wcs.Host.dll</c> elsewhere.
    /// </summary>
    private static Launch HostExecutable()
    {
        var configuration = typeof(WcsHostProcess).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug";
        var root = RepositoryRoot();
        var folder = Path.Combine(root, "WCS", "Wcs.Host", "src", "bin", configuration, "net10.0");
        var assembly = Path.Combine(folder, "Wcs.Host.dll");

        if (!File.Exists(assembly))
            throw new InvalidOperationException(
                $"The WCS host is not built at {folder}. Build the solution (dotnet build Wcs.slnx), or this test project, " +
                $"in {configuration}: the test project references Wcs.Host only to have it built.");

        var apphost = Path.Combine(folder, OperatingSystem.IsWindows() ? "Wcs.Host.exe" : "Wcs.Host");
        return File.Exists(apphost)
            ? new Launch(apphost, [], assembly)
            : new Launch("dotnet", [assembly], assembly);
    }

    /// <summary>The folder that holds <c>Wcs.slnx</c>, walking up from the test output.</summary>
    internal static string RepositoryRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder != null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "Wcs.slnx"))) return folder.FullName;
        }

        throw new InvalidOperationException($"No Wcs.slnx above {AppContext.BaseDirectory}: the test does not run from the repository.");
    }
}
