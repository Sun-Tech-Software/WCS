using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Wms.Client;
using Wcs.Wms.Contracts;

namespace Wcs.SampleWms.Test;

/// <summary>
/// The sample WMS, running in this process with its real Kestrel on a free loopback port and its own
/// key: the WCS host (another process) calls it over HTTP like it would call a real WMS.
/// </summary>
public sealed class SampleWmsServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly string _contentRoot;

    private SampleWmsServer(WebApplication app, string contentRoot, Uri baseAddress, string apiKey)
    {
        _app = app;
        _contentRoot = contentRoot;
        BaseAddress = baseAddress;
        ApiKey = apiKey;
    }

    public Uri BaseAddress { get; }
    public string ApiKey { get; }

    /// <summary>The sample's warehouse, to read what it holds (never to prepare it: the test goes through the routes).</summary>
    public SampleWarehouse Warehouse => _app.Services.GetRequiredService<SampleWarehouse>();

    public static async Task<SampleWmsServer> StartAsync()
    {
        var apiKey = "sample-test-" + Guid.NewGuid().ToString("N");

        // An empty content root: the test output holds the sample's appsettings.json, whose address
        // (localhost:5142) and public key must not be picked up by a test. Everything the sample
        // needs comes from the arguments.
        var contentRoot = Directory.CreateTempSubdirectory("sample-wms-test-").FullName;
        var address = new Uri($"http://127.0.0.1:{FreePort()}");

        var app = SampleWmsApp.Create(
        [
            $"--contentRoot={contentRoot}",
            $"--urls={address}",
            $"--SampleWms:ApiKey={apiKey}",
            "--Logging:LogLevel:Default=Warning"
        ]);
        await app.StartAsync();
        return new SampleWmsServer(app, contentRoot, address, apiKey);
    }

    /// <summary>The WCS's own client of the contract, pointed at this server.</summary>
    public HttpWarehouse CreateWarehouseClient(string? apiKey = null) =>
        new(new HttpClient { BaseAddress = BaseAddress, Timeout = TimeSpan.FromSeconds(30) },
            new HttpWarehouseOptions { ApiKey = apiKey ?? ApiKey });

    /// <summary>A plain HTTP client with the key, for the sample's admin routes.</summary>
    public HttpClient CreateAdminClient()
    {
        var client = new HttpClient { BaseAddress = BaseAddress };
        client.DefaultRequestHeaders.Add(WarehouseRoutes.ApiKeyHeader, ApiKey);
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Directory.Delete(_contentRoot, recursive: true);
    }

    internal static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
