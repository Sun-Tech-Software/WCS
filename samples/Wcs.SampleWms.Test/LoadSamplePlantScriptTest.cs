using Wcs.Api.Client;
using Wcs.Api.Contracts.Dtos;
using Wcs.Wms.Contracts;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.SampleWms.Test;

/// <summary>
/// What load-sample-plant.ps1 prints when the host refuses the plant: the reason the host gives, as
/// the troubleshooting table of the quick start quotes it. With Windows PowerShell 5.1, the shell of
/// step 5, an error of Invoke-RestMethod shows only "(409) Conflict" (in the language of Windows),
/// not the body that says why (quick start run of 15/10, D2).
/// </summary>
public sealed class LoadSamplePlantScriptTest : IAsyncLifetime
{
    private static readonly Guid Infeed = Guid.Parse("5a000000-0000-4000-8000-000000000001");

    private SampleWmsServer _wms = null!;
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
        await _wms.DisposeAsync();
    }

    [Fact]
    public async Task WithAMissionStillInTheEngineTheScriptSaysWhyTheHostRefusesThePlant()
    {
        Assert.Equal(0, (await PlantScript.RunAsync(_host)).ExitCode);

        // Every storage cell locked in the WMS: pallet 5001 gets no cell and its mission waits on the
        // infeed for as long as the test needs it.
        var warehouse = _wms.CreateWarehouseClient();
        foreach (var door in new[] { "A0101", "A0102", "A0201", "A0202", "A0301", "A0302" })
            await warehouse.SetCellLockStateAsync(door, CellLockState.Full);

        await _wcs.EnqueueCommandAsync(new CommandRequest((int)CommandTypes.SetNavigatorRun));
        await Eventually.Until("the engine in Run", () => _wcs.GetStatusAsync(), s => s.NavStatus == NavigatorStatusTypes.Run, NoDiagnosis);
        await _wcs.EnqueueCommandAsync(new CommandRequest((int)CommandTypes.LoadLu, ItemId: Infeed, LoadUnitId: 5001));
        await Eventually.Until("the mission of pallet 5001", () => _wcs.GetMissionsAsync(5001), m => m.Count == 1, NoDiagnosis);
        await _wcs.EnqueueCommandAsync(new CommandRequest((int)CommandTypes.SetNavigatorStop));
        await Eventually.Until("the engine in Stop", () => _wcs.GetStatusAsync(), s => s.NavStatus == NavigatorStatusTypes.Stop, NoDiagnosis);

        var run = await PlantScript.RunAsync(_host);

        Assert.Equal(1, run.ExitCode);
        AssertOnScreen("409: Impossibile sostituire il grafo con lavoro pendente - Ci sono missioni attive in memoria", run);
    }

    [Fact]
    public async Task WithTheEditLeaseHeldBySomeoneElseTheScriptNamesTheHolder()
    {
        await _wcs.AcquireEditLockAsync("someone-else");

        var run = await PlantScript.RunAsync(_host);

        Assert.Equal(1, run.ExitCode);
        AssertOnScreen("409: the edit lease is held by someone-else", run);
    }

    private static void AssertOnScreen(string expected, PlantScript.Outcome run) =>
        Assert.True(run.Screen.Contains(expected, StringComparison.Ordinal),
            $"Expected on screen: {expected}{Environment.NewLine}The script printed:{Environment.NewLine}{run.Screen}");

    private static Task<string> NoDiagnosis() => Task.FromResult(string.Empty);
}
