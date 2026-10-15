using System.Net.Http.Json;
using System.Text.Json;
using Wcs.Api.Client;
using Wcs.Api.Contracts.Dtos;
using Wcs.Wms.Contracts;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.SampleWms.Test;

/// <summary>
/// The quick start, run for real: the sample WMS and a WCS host on the loopback, the sample plant
/// loaded with its script, a pallet entered at the infeed and stored in a cell of the sample WMS, then
/// an extraction order and the pallet delivered to the outfeed bay. Every device is on the loopback
/// driver; the WCS and the WMS talk only through the contract, over HTTP.
/// </summary>
public sealed class QuickStartEndToEndTest : IAsyncLifetime
{
    private const decimal Pallet = 5001;

    // From samples/plant/sample-plant.json.
    private static readonly Guid Infeed = Guid.Parse("5a000000-0000-4000-8000-000000000001");
    private static readonly Guid Outfeed = Guid.Parse("5a000000-0000-4000-8000-000000000007");

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
    public async Task APalletEntersIsStoredInASampleCellIsDeliveredToTheBayAndLeaves()
    {
        // The plant, loaded by the quick start's script.
        await LoadThePlantWithTheScript();
        var graph = await _wcs.GetPlantGraphAsync();
        Assert.Equal(7, graph.Items.Count);
        Assert.Equal(7, graph.Junctions.Count);
        Assert.Equal(8, graph.DoorMappings.Count);

        await _wcs.EnqueueCommandAsync(new CommandRequest((int)CommandTypes.SetNavigatorRun));
        await Eventually.Until("the engine in Run", () => _wcs.GetStatusAsync(), s => s.NavStatus == NavigatorStatusTypes.Run, Diagnose);

        // 3002 LoadLu: the pallet is on the infeed. The WCS asks the WMS for a cell (reservations),
        // carries the pallet there and reports it stored (load-units/allocate).
        await _wcs.EnqueueCommandAsync(new CommandRequest((int)CommandTypes.LoadLu, ItemId: Infeed, LoadUnitId: Pallet));

        var warehouse = _wms.CreateWarehouseClient();
        var stored = await Eventually.Until("the pallet stored in a cell of the sample WMS, with no mission left in the WCS",
            async () => (Location: await warehouse.LocateLoadUnitAsync(Pallet), Missions: (await _wcs.GetMissionsAsync(Pallet)).Count),
            x => x.Location?.CellId is >= 101 and <= 106 && x.Missions == 0, Diagnose);

        // The first free cell in the WMS's order (column 1, floor 1), at the cell's depth.
        Assert.Equal(new LoadUnitLocation(Pallet, 101, 1200), stored.Location);
        Assert.True(await warehouse.IsLoadUnitStoredAsync("LU005001"));

        // An extraction order, created in the WMS as its users would: the WCS takes it on its next turn.
        using var admin = _wms.CreateAdminClient();
        using var created = await admin.PostAsJsonAsync("/sample-admin/extraction-orders", new { loadUnitIds = new[] { Pallet } });
        Assert.Equal(System.Net.HttpStatusCode.Created, created.StatusCode);

        var delivered = await Eventually.Until("the pallet on the outfeed bay OUT01, and the order closed in the WMS",
            async () => (Bay: (await _wcs.GetItemStatusesAsync()).Single(s => s.ItemId == Outfeed),
                Location: await warehouse.LocateLoadUnitAsync(Pallet),
                Order: _wms.Warehouse.Snapshot().ExtractionOrders.Single()),
            x => x.Bay.LoadedLuId == Pallet && x.Location?.CellId == 900 && x.Order.Closed, Diagnose);

        Assert.Equal("LU005001", delivered.Bay.LoadedLuCode);
        Assert.Equal(new LoadUnitLocation(Pallet, 900, 1200), delivered.Location);
        Assert.Equal("Extracted", Assert.Single(delivered.Order.Rows).State);
        Assert.False(await warehouse.IsLoadUnitStoredAsync("LU005001"));

        // 2004 ItemUnloadLu: someone takes the pallet off the bay. The WCS reports that it left the
        // automation (load-units/release), and the WMS marks it shipped.
        await _wcs.EnqueueCommandAsync(new CommandRequest((int)CommandTypes.ItemUnloadLu, ItemId: Outfeed));

        var shipped = await Eventually.Until("the pallet shipped in the WMS and the bay empty in the WCS",
            async () => (Bay: (await _wcs.GetItemStatusesAsync()).Single(s => s.ItemId == Outfeed),
                LoadUnit: _wms.Warehouse.Snapshot().LoadUnits.Single(l => l.LoadUnitId == Pallet)),
            x => x.Bay.LoadedLuId == 0 && x.LoadUnit.Shipped, Diagnose);

        Assert.Null(shipped.LoadUnit.CellId);
        Assert.Null(await warehouse.LocateLoadUnitAsync(Pallet));

        // The facts the WCS reported, as the WMS wrote them in its journal.
        var journal = _wms.Warehouse.Snapshot().Journal.Select(entry => entry.What).ToList();
        Assert.Contains(journal, what => what.StartsWith("cell 101 (door A0101) reserved for load unit 5001, mission ", StringComparison.Ordinal));
        Assert.Contains("load unit 5001 is in cell 101 (stored through door A0101)", journal);
        Assert.Contains("order 1, row 1 (load unit 5001): Extracted", journal);
        Assert.Contains("order 1 closed: every row extracted", journal);
        Assert.Contains("load unit 5001 is in cell 900 (operator cell)", journal);
        Assert.Contains("load unit 5001 left the automation", journal);

        // Step 12, then step 5 again on the same host: with the engine stopped and no mission left, the
        // plant can be replaced. A storing mission that stayed in wcs.db after it ended made the host
        // refuse it with 409 until a restart.
        await _wcs.EnqueueCommandAsync(new CommandRequest((int)CommandTypes.SetNavigatorStop));
        await Eventually.Until("the engine in Stop", () => _wcs.GetStatusAsync(), s => s.NavStatus == NavigatorStatusTypes.Stop, Diagnose);
        Assert.Empty(await _wcs.GetMissionsAsync());

        await LoadThePlantWithTheScript();
    }

    private async Task LoadThePlantWithTheScript()
    {
        var run = await PlantScript.RunAsync(_host);
        Assert.True(run.ExitCode == 0, $"load-sample-plant.ps1 exited with {run.ExitCode}.{Environment.NewLine}{run.Screen}");
        Assert.Contains("7 devices, 7 junctions, 8 door mappings", run.Output);
    }

    /// <summary>What both sides held when a wait ran out: the failure message of this test.</summary>
    private async Task<string> Diagnose()
    {
        var json = new JsonSerializerOptions { WriteIndented = true };
        var parts = new List<string> { "--- sample WMS state ---", JsonSerializer.Serialize(_wms.Warehouse.Snapshot(), json) };

        try
        {
            parts.Add("--- WCS missions ---");
            parts.Add(JsonSerializer.Serialize(await _wcs.GetMissionsAsync(), json));
            parts.Add("--- WCS mission steps ---");
            parts.Add(JsonSerializer.Serialize(await _wcs.GetAllMissionStepsAsync(), json));
            parts.Add("--- WCS devices ---");
            parts.Add(string.Join(Environment.NewLine, (await _wcs.GetItemStatusesAsync())
                .Select(s => $"{s.Name}: {s.Status}, {s.LuLoadStatus}, load unit {s.LoadedLuId}, running step {s.RunningStepId}, waiting step {s.WaitingStepId}")));
            parts.Add("--- WCS navigator log ---");
            parts.Add(string.Join(Environment.NewLine, (await _wcs.GetLogAsync(DateTime.Now.AddHours(-1), DateTime.Now.AddHours(1)))
                .Select(l => $"{l.Date:HH:mm:ss.fff} [{l.Status}] {l.Description}")));
        }
        catch (Exception ex)
        {
            parts.Add($"(the WCS host did not answer: {ex.Message})");
        }

        parts.Add("--- WCS host output ---");
        parts.Add(_host.LogTail());
        return string.Join(Environment.NewLine, parts);
    }
}
