using System.Diagnostics;
using Wcs.Api.Client;
using Wcs.Api.Contracts.Dtos;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.SampleWms.Test;

/// <summary>
/// The monitoring scripts of tools/monitoring (guide: integration/monitoring-and-alerting.md), run with
/// PowerShell 7 against a real host process on a free port with its own data folder, and the sample WMS:
/// the exit codes they document (0 OK, 1 WARNING, 2 CRITICAL, 3 UNKNOWN) for the situations they are
/// meant to catch, and the key never on screen.
/// </summary>
public sealed class MonitoringScriptsTest : IAsyncLifetime
{
    private const string KeyVariable = "WCS_MONITOR_API_KEY";

    private SampleWmsServer? _wms;
    private WcsHostProcess _host = null!;
    private WcsClient _wcs = null!;

    public async Task InitializeAsync()
    {
        _wms = await SampleWmsServer.StartAsync();
        _host = await WcsHostProcess.StartAsync(_wms.BaseAddress, _wms.ApiKey);
        _wcs = new WcsClient(new WcsClientOptions { BaseAddress = _host.BaseAddress, ApiKey = _host.ApiKey });
    }

    public async Task DisposeAsync()
    {
        _wcs.Dispose();
        await _host.DisposeAsync();
        if (_wms != null) await _wms.DisposeAsync();
    }

    [Fact]
    public async Task AHealthyHostIsOkAndAWrongOrMissingKeyIsReportedWithoutPrintingIt()
    {
        var healthy = await RunAsync("Test-WcsHealth.ps1", _host.ApiKey, "-BaseUrl", Url, "-ExpectedEngineState", "Stop");
        Assert.Equal(0, healthy.ExitCode);
        healthy.AssertOnScreen("OK       status: engine Stop");
        healthy.AssertKeyNotOnScreen(_host.ApiKey);

        var wrongState = await RunAsync("Test-WcsHealth.ps1", _host.ApiKey, "-BaseUrl", Url, "-ExpectedEngineState", "Run");
        Assert.Equal(2, wrongState.ExitCode);
        wrongState.AssertOnScreen("CRITICAL status: engine in Stop, expected Run");

        const string wrongKey = "not-the-key-0123456789";
        var refused = await RunAsync("Test-WcsHealth.ps1", wrongKey, "-BaseUrl", Url, "-ExpectedEngineState", "Any");
        Assert.Equal(2, refused.ExitCode);
        refused.AssertOnScreen("answered 401");
        refused.AssertKeyNotOnScreen(wrongKey);

        var noKey = await RunAsync("Test-WcsHealth.ps1", null, "-BaseUrl", Url);
        Assert.Equal(3, noKey.ExitCode);
        noKey.AssertOnScreen($"the environment variable {KeyVariable} is not set");

        var storage = await RunAsync("Test-WcsStorage.ps1", null, "-DataDirectory", _host.DataDirectory,
            "-WarnFreePercent", "0", "-CriticalFreePercent", "0");
        Assert.Equal(0, storage.ExitCode);
        storage.AssertOnScreen("OK       files: wcs.db");

        var full = await RunAsync("Test-WcsStorage.ps1", null, "-DataDirectory", _host.DataDirectory, "-CriticalFreePercent", "100");
        Assert.Equal(2, full.ExitCode);
        full.AssertOnScreen("CRITICAL disk:");
    }

    [Fact]
    public async Task ADeviceOffLineIsAWarningOrCriticalAsAskedAndCanBeIgnored()
    {
        Assert.Equal(0, (await PlantScript.RunAsync(_host)).ExitCode);
        var outfeed = (await _wcs.GetItemsAsync()).Single(i => i.Name == "OUT01");
        // A device is put OffLine by command 2002, which the host accepts in Run only.
        await _wcs.EnqueueCommandAsync(new CommandRequest((int)CommandTypes.SetNavigatorRun));
        await Eventually.Until("the engine in Run", () => _wcs.GetStatusAsync(), s => s.NavStatus == NavigatorStatusTypes.Run, () => Task.FromResult(_host.LogTail()));
        await _wcs.EnqueueCommandAsync(new CommandRequest((int)CommandTypes.ItemSetOffLine, ItemId: outfeed.ItemId));
        await Eventually.Until("OUT01 OffLine", () => _wcs.GetItemStatusesAsync(),
            s => s.Single(x => x.ItemId == outfeed.ItemId).Status == DeviceStatusTypes.OffLine, () => Task.FromResult(_host.LogTail()));

        var warning = await RunAsync("Test-WcsHealth.ps1", _host.ApiKey, "-BaseUrl", Url, "-ExpectedEngineState", "Run");
        Assert.Equal(1, warning.ExitCode);
        warning.AssertOnScreen("WARNING  devices: 1 OffLine: OUT01");

        var critical = await RunAsync("Test-WcsHealth.ps1", _host.ApiKey, "-BaseUrl", Url, "-ExpectedEngineState", "Run", "-OffLineLevel", "CRITICAL");
        Assert.Equal(2, critical.ExitCode);

        var ignored = await RunAsync("Test-WcsHealth.ps1", _host.ApiKey, "-BaseUrl", Url, "-ExpectedEngineState", "Run", "-IgnoreDevice", "OUT01");
        Assert.Equal(0, ignored.ExitCode);
        ignored.AssertOnScreen("(1 ignored)");
    }

    [Fact]
    public async Task TheWmsProbeIsCriticalWhenTheWmsIsDown()
    {
        var up = await RunAsync("Test-WcsHealth.ps1", _host.ApiKey, "-BaseUrl", Url, "-ExpectedEngineState", "Stop", "-ProbeWms");
        Assert.Equal(0, up.ExitCode);
        up.AssertOnScreen("OK       wms: the WMS answered the configuration check");

        await _wms!.DisposeAsync();
        _wms = null;

        var down = await RunAsync("Test-WcsHealth.ps1", _host.ApiKey, "-BaseUrl", Url, "-ExpectedEngineState", "Stop", "-ProbeWms");
        Assert.Equal(2, down.ExitCode);
        down.AssertOnScreen("CRITICAL wms: GET /api/v1/wms/configuration-check?mode=0 answered 503");
    }

    private string Url => _host.BaseAddress.ToString();

    internal static Task<ScriptRun> RunAsync(string script, string? apiKey, params string[] arguments) =>
        ScriptRun.RunAsync(script, apiKey is null ? new Dictionary<string, string?> { [KeyVariable] = null } : new() { [KeyVariable] = apiKey }, arguments);
}

/// <summary>
/// Test-WcsEvents.ps1 on log files written here in the host's own formats (the texts are copied from
/// the engine's WriteLog calls): no host needed.
/// </summary>
public sealed class MonitoringEventsScriptTest : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("wcs-monitoring-events-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task AParkedFactIsCriticalOnceAndTheNextRunReadsOnlyNewLines()
    {
        var log = Path.Combine(_folder, "host.log");
        var state = Path.Combine(_folder, "state", "host-log.json");
        await File.WriteAllLinesAsync(log,
        [
            "info: Wcs.Host.Engine.EngineHostedService[0]",
            "      Motore caricato da C:\\Wcs\\Data",
            "fail: Wcs.Host.Engine.EngineHostedService[0]",
            "      Transizione WMS 6f1c0000-0000-4000-8000-000000000001 (Allocate, UDC 5001) RIFIUTATA DEFINITIVAMENTE e parcheggiata: non verra' ritentata. Motivo: 400",
            "fail: Wcs.Host.Engine.EngineHostedService[0]",
            "      Error during the ProcessoAssegnazioni process, Description: Il gestionale non ha risposto a \u00abGetPendingExtractionWork\u00bb."
        ]);

        var first = await ScriptRun.RunAsync("Test-WcsEvents.ps1", new(), "-LogFile", log, "-StateFile", state);
        Assert.Equal(2, first.ExitCode);
        first.AssertOnScreen("CRITICAL parked: 1 fact(s) towards the WMS refused for good and parked");
        first.AssertOnScreen("WARNING  wms: 1 failed call(s) to the WMS");

        var second = await ScriptRun.RunAsync("Test-WcsEvents.ps1", new(), "-LogFile", log, "-StateFile", state);
        Assert.Equal(0, second.ExitCode);
        second.AssertOnScreen("nothing to report");

        await File.AppendAllLinesAsync(log, ["fail: Microsoft.AspNetCore.Server.Kestrel[13]", "      Connection id \"0HN\": an unhandled exception"]);
        var third = await ScriptRun.RunAsync("Test-WcsEvents.ps1", new(), "-LogFile", log, "-StateFile", state);
        Assert.Equal(1, third.ExitCode);
        third.AssertOnScreen("WARNING  errors: 1 other error(s)");
    }

    [Fact]
    public async Task NothingToCheckIsUnknown()
    {
        var run = await ScriptRun.RunAsync("Test-WcsEvents.ps1", new());
        Assert.Equal(3, run.ExitCode);
    }
}

/// <summary>One run of a monitoring script with PowerShell 7 (pwsh), as a scheduler would start it.</summary>
public sealed record ScriptRun(int ExitCode, string Screen)
{
    public static async Task<ScriptRun> RunAsync(string script, Dictionary<string, string?> environment, params string[] arguments)
    {
        var path = Path.Combine(WcsHostProcess.RepositoryRoot(), "tools", "monitoring", script);
        var start = new ProcessStartInfo("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File", path }.Concat(arguments))
            start.ArgumentList.Add(argument);
        foreach (var (name, value) in environment)
        {
            if (value is null) start.Environment.Remove(name);
            else start.Environment[name] = value;
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(Eventually.Ceiling);
        return new ScriptRun(process.ExitCode, await output + Environment.NewLine + await error);
    }

    public void AssertOnScreen(string expected) =>
        Assert.True(Screen.Contains(expected, StringComparison.Ordinal),
            $"Expected on screen: {expected}{Environment.NewLine}The script printed (exit code {ExitCode}):{Environment.NewLine}{Screen}");

    public void AssertKeyNotOnScreen(string key) =>
        Assert.False(Screen.Contains(key, StringComparison.Ordinal), $"The key is on screen:{Environment.NewLine}{Screen}");
}
