using Microsoft.Playwright;
using Wcs.Api.Client;
using Xunit;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// Like <see cref="DrawioEditorFixture"/>, with a warehouse that <b>answers</b> on top
/// (<see cref="StubWarehouse"/>): the host points at it instead of at the unreachable address it
/// uses by default, so <c>GET /wms/cells</c> comes back with real cells and the panel has something
/// to show.
/// </summary>
/// <remarks>
/// Each class that uses it has its own (<c>IClassFixture</c>), so this changes nothing for the
/// others, whose host stays without a warehouse exactly as before. Two classes use it: the cells
/// test, for the cells, and - final review, I3 - <see cref="DrawioOperationsBrowserTest"/>, for a
/// warehouse that answers AT ONCE (503 on every route but the cells): Test Route asks it a question
/// while its command is Running, and an address that does not answer made that outcome slow and
/// intermittent. The fake is stopped LAST, after the host, for the same reason the base fixture stops the
/// host in a <c>finally</c>: it is the piece that does most harm if it is left running.
/// </remarks>
public sealed class DrawioCellsFixture : IAsyncLifetime
{
    /// <summary>The cells this fake warehouse offers. They are the numbers the test looks for on screen, so they live here and not inside the test.</summary>
    public const decimal StorageCellId = 900101m;

    public const decimal AutomationCellId = 900202m;
    public const string StorageDoorA = "PORTA-900101-A";
    public const string StorageDoorB = "PORTA-900101-B";
    public const string AutomationDoorA = "PORTA-900202-A";

    private IPlaywright? _playwright;
    private StubWarehouse? _warehouse;

    public WcsHostProcess Host { get; private set; } = null!;

    public IBrowser Browser { get; private set; } = null!;

    public StubWarehouse Warehouse => _warehouse!;

    public async Task InitializeAsync()
    {
        _warehouse = StubWarehouse.Start(
            new StubCell(StorageCellId, "MAG1", "AREA-STOCCAGGIO", 4, 2, StorageDoorA, StorageDoorB, "cella di collaudo"),
            new StubCell(AutomationCellId, "MAG1", "AREA-AUTOMAZIONE", 7, 1, AutomationDoorA, null, "cella polmone di collaudo"));

        try
        {
            Host = await WcsHostProcess.StartAsync(_warehouse.BaseAddress);
        }
        catch
        {
            await _warehouse.DisposeAsync();
            throw;
        }

        try
        {
            _playwright = await Microsoft.Playwright.Playwright.CreateAsync();
            Browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        }
        catch (PlaywrightException ex)
        {
            await DisposeAsync();
            throw new InvalidOperationException(
                "Il browser di Playwright non si e' potuto avviare: quasi sempre vuol dire che i browser non sono " +
                "stati scaricati su questa macchina. Si scaricano una volta sola, con lo script che il pacchetto " +
                $"copia accanto alle prove:{Environment.NewLine}" +
                $"    pwsh \"{Path.Combine(AppContext.BaseDirectory, "playwright.ps1")}\" install chromium{Environment.NewLine}" +
                $"Il guasto originale era: {ex.Message}", ex);
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        try
        {
            try
            {
                try
                {
                    if (Browser is not null) await Browser.CloseAsync();
                }
                finally
                {
                    _playwright?.Dispose();
                }
            }
            finally
            {
                if (Host is not null) await Host.DisposeAsync();
            }
        }
        finally
        {
            if (_warehouse is not null) await _warehouse.DisposeAsync();
        }
    }

    /// <summary>An API client pointed at this host, with its key: the road by which the test seeds the plant and reads it back.</summary>
    public WcsClient CreateClient() =>
        new(new WcsClientOptions { BaseAddress = Host.BaseAddress, ApiKey = Host.ApiKey });
}
