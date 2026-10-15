using System.Diagnostics;

namespace Wcs.SampleWms.Test;

/// <summary>
/// Runs samples/plant/load-sample-plant.ps1 against a host with the arguments of step 5 of the quick
/// start (<c>-NoProfile -ExecutionPolicy Bypass -File</c>), plus <c>-NonInteractive</c> and the test's
/// address and key.
/// </summary>
public static class PlantScript
{
    public sealed record Outcome(int ExitCode, string Output, string Error)
    {
        /// <summary>Everything the script printed, as the reader of the quick start sees it in the terminal.</summary>
        public string Screen => Output + Environment.NewLine + Error;
    }

    public static async Task<Outcome> RunAsync(WcsHostProcess host)
    {
        var script = Path.Combine(AppContext.BaseDirectory, "plant", "load-sample-plant.ps1");
        var shell = new ProcessStartInfo(PowerShell())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
                     "-HostUrl", host.BaseAddress.ToString(), "-ApiKey", host.ApiKey })
            shell.ArgumentList.Add(argument);

        using var process = Process.Start(shell)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(Eventually.Ceiling);
        return new Outcome(process.ExitCode, await output, await error);
    }

    /// <summary>
    /// The shell step 5 of the quick start names: Windows PowerShell on Windows, where it is always
    /// installed and is the strictest (version 5.1, Restricted by default), PowerShell 7 elsewhere.
    /// </summary>
    private static string PowerShell() => OperatingSystem.IsWindows() ? "powershell" : "pwsh";
}
