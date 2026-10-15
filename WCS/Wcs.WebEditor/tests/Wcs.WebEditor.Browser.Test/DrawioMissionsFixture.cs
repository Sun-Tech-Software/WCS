using Microsoft.Playwright;
using Wcs.Api.Client;
using Xunit;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// Like <see cref="DrawioEditorFixture"/>, with a warehouse that knows ONE load unit
/// (<see cref="LoadUnitId"/>, <see cref="LoadUnitCode"/>) and answers 503 to everything else
/// (<see cref="StubWarehouse.StartDescribing"/>): for <see cref="DrawioMissionsBrowserTest"/>, which
/// needs a mission that carries a real load unit and a warehouse that then does not answer - queue
/// item 31 (see there).
/// </summary>
/// <remarks>
/// The class that uses it has its own (<c>IClassFixture</c>), so this changes nothing for the others.
/// The fake is stopped LAST, after the host, as in <see cref="DrawioCellsFixture"/> and for the same
/// reason.
/// </remarks>
public sealed class DrawioMissionsFixture : IAsyncLifetime
{
    /// <summary>The load unit the fake warehouse describes: the numbers the test seeds and looks for, so they live here.</summary>
    public const decimal LoadUnitId = 4242m;

    public const string LoadUnitCode = "UDC4242";

    private IPlaywright? _playwright;
    private StubWarehouse? _warehouse;

    public WcsHostProcess Host { get; private set; } = null!;

    public IBrowser Browser { get; private set; } = null!;

    public StubWarehouse Warehouse => _warehouse!;

    public async Task InitializeAsync()
    {
        _warehouse = StubWarehouse.StartDescribing(new StubLoadUnit(LoadUnitId, LoadUnitCode));

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
                "Playwright's browser could not start: almost always the browsers have not been downloaded on " +
                "this machine. They are downloaded once, with the script the package copies next to the tests:" +
                $"{Environment.NewLine}" +
                $"    pwsh \"{Path.Combine(AppContext.BaseDirectory, "playwright.ps1")}\" install chromium{Environment.NewLine}" +
                $"The original failure was: {ex.Message}", ex);
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
