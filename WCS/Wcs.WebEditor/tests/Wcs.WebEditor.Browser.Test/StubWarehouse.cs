using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// A fake warehouse that answers ONE question of the boundary: the Editor's cell list
/// (<c>GET /api/wcs/warehouse/cells</c>), and answers every other one at once with 503. It is for
/// the cells test class, and for <see cref="DrawioOperationsBrowserTest"/>, which needs a warehouse
/// that answers at once rather than one that does not answer at all (final review, I3). Started
/// with <see cref="StartDescribing"/> it answers a second one instead, the description of a load
/// unit (<c>GET /api/wcs/warehouse/load-units/{id}/description</c>), for
/// <see cref="DrawioMissionsBrowserTest"/>: see there. A test can also give it, at any moment, the
/// answer to one more request - a method and a path, query included - with <see cref="Answer"/>
/// (item 54: the cells, the precise cell's reservation), and read the bodies that came with the
/// requests in <see cref="RequestBodies"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it is needed.</b> The host's <c>/wms</c> routes pass the question on to the boundary
/// (<c>WmsEndpoints</c>), and the browser tests' host points by default at an address that does not
/// answer: <c>GET /wms/cells</c> comes back <c>503</c>, and the cells panel has no cell to show.
/// Without a warehouse that answers, the browser test would be measuring the failure message and
/// nothing else.
/// </para>
/// <para>
/// <b>Why by hand, on a socket, and neither an <c>HttpListener</c> nor a Kestrel.</b>
/// <c>HttpListener</c> on Windows wants an address reservation (<c>netsh http add urlacl</c>) for
/// anyone who is not an administrator - that is, a test that passes or not depending on how the
/// session was started; a Kestrel would want the web SDK on this test project. A TCP listener and an
/// HTTP/1.1 answer written out by hand ask nothing of anybody - and the request to serve is a GET
/// with no body.
/// </para>
/// <para>
/// <b>It does not filter.</b> The route's two filters (<c>areaType</c>, <c>automaticOnly</c>) are the
/// real warehouse's work, not the WCS's: here every question gets the same list, and the requests
/// that arrived stay in <see cref="RequestedPaths"/> so that a test can say what the WCS really
/// asked for instead of deducing it.
/// </para>
/// </remarks>
public sealed class StubWarehouse : IAsyncDisposable
{
    /// <summary>The boundary route this fake answers. Everything else gets a 503, which is how the boundary says it is not available.</summary>
    private const string CellsPath = "/api/wcs/warehouse/cells";

    /// <summary>The boundary route of a load unit's description, around its identifier: <c>WarehouseRoutes.LoadUnit</c>.</summary>
    private const string LoadUnitsPath = "/api/wcs/warehouse/load-units/";

    private const string DescriptionSuffix = "/description";

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _serving;
    private readonly string _cellsJson;
    private readonly Dictionary<decimal, string> _loadUnitsJson = [];
    private readonly List<string> _requestedPaths = [];
    private readonly List<(string Path, string Body)> _requestBodies = [];
    private readonly Dictionary<string, string> _answers = [];
    private readonly List<(string Path, TaskCompletionSource Arrived)> _waiters = [];
    private readonly Lock _gate = new();

    private StubWarehouse(TcpListener listener, string cellsJson, IEnumerable<StubLoadUnit>? loadUnits = null)
    {
        _listener = listener;
        _cellsJson = cellsJson;
        // In the boundary's own shape (LoadUnitDescription), serialised here for the same reason as
        // the cells: the test project does not reference Wcs.Wms.Contracts.
        foreach (var loadUnit in loadUnits ?? [])
            _loadUnitsJson[loadUnit.LoadUnitId] = JsonSerializer.Serialize(new
            {
                loadUnitId = loadUnit.LoadUnitId,
                code = loadUnit.Code,
                loadUnitTypeId = 0m,
                handlingMode = 0m,
                tare = 0,
                height = 0,
                width = 0,
                depth = 0,
                weight = 0,
            });
        _serving = ServeAsync(_stopping.Token);
    }

    /// <summary>The address to hand to <c>Wms:ApiAddress</c>.</summary>
    public Uri BaseAddress { get; private set; } = null!;

    /// <summary>The paths (query included) the WCS really asked for, in order.</summary>
    public IReadOnlyList<string> RequestedPaths
    {
        get { lock (_gate) return [.. _requestedPaths]; }
    }

    /// <summary>The bodies of the requests that carried one (a POST), with their path, in order.</summary>
    public IReadOnlyList<(string Path, string Body)> RequestBodies
    {
        get { lock (_gate) return [.. _requestBodies]; }
    }

    /// <summary>
    /// From now on <paramref name="method"/> on <paramref name="path"/> (query included, as the WCS
    /// writes it) answers 200 with <paramref name="json"/>, before every other rule of this fake. The
    /// JSON is written by the test in the boundary's own shape, for the reason the cells are.
    /// </summary>
    public void Answer(string method, string path, string json)
    {
        lock (_gate) _answers[$"{method} {path}"] = json;
    }

    /// <summary>
    /// Completes when the WCS has asked for <paramref name="path"/> (query included) - at once if it
    /// already has. The way to wait for a request the engine makes in the background, after what the
    /// page shows, without waiting for a time.
    /// </summary>
    public Task Requested(string path)
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_requestedPaths.Contains(path)) return Task.CompletedTask;
            _waiters.Add((path, arrived));
        }

        return arrived.Task;
    }

    /// <summary>
    /// Starts the fake on a free port with these cells. The rows are in the boundary's own shape
    /// (<c>EditorCellRow</c>), serialised here once: the browser test project does not reference
    /// <c>Wcs.Wms.Contracts</c>, and there is no reason to make it do so for seven fields.
    /// </summary>
    public static StubWarehouse Start(params StubCell[] cells)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var json = JsonSerializer.Serialize(cells.Select(cell => new
        {
            cellId = cell.CellId,
            warehouseDescription = cell.Warehouse,
            areaDescription = cell.Area,
            column = cell.Column,
            floor = cell.Floor,
            doorCodeA = cell.DoorA,
            doorCodeB = cell.DoorB,
            description = cell.Description,
        }));

        return new StubWarehouse(listener, json) { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    }

    /// <summary>
    /// Starts the fake on a free port answering the description of these load units - and 503 to
    /// everything else, the cells included: a warehouse that knows the load units it is asked about
    /// and does not answer anything else, which is what lets the engine build a mission with a real
    /// load unit (<c>LoadLu</c> asks for its description) and then find the warehouse unavailable.
    /// </summary>
    public static StubWarehouse StartDescribing(params StubLoadUnit[] loadUnits)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return new StubWarehouse(listener, cellsJson: "", loadUnits) { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    }

    private async Task ServeAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(stopping);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            // One connection at a time: the WCS asks one question at a time, and a test that hung
            // here would be better than one falling over on hand-written concurrency.
            using (client)
            {
                try
                {
                    await AnswerAsync(client, stopping);
                }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
                {
                    // The client went away halfway through the answer: not this test's business.
                }
            }
        }
    }

    private async Task AnswerAsync(TcpClient client, CancellationToken stopping)
    {
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);

        var requestLine = await reader.ReadLineAsync(stopping);
        if (requestLine is null) return;

        // The headers are read and thrown away, but they ARE read: without that the answer would go
        // out while the client is still writing, and on Windows a close at that moment reaches it as
        // a reset connection instead of as the answer. The body too, for the same reason, when the
        // request says how long it is (the WCS's POSTs with an answer write it whole, StringContent):
        // read through the same ASCII reader, one character per byte.
        var length = 0;
        while (await reader.ReadLineAsync(stopping) is { Length: > 0 } header)
        {
            if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(header["Content-Length:".Length..].Trim(), out var declared))
                length = declared;
        }

        var requestBody = "";
        if (length > 0)
        {
            var buffer = new char[length];
            var read = await reader.ReadBlockAsync(buffer.AsMemory(), stopping);
            requestBody = new string(buffer, 0, read);
        }

        var parts = requestLine.Split(' ');
        var method = parts[0];
        var target = parts.Length > 1 ? parts[1] : "";
        List<TaskCompletionSource> arrived;
        string? answer;
        lock (_gate)
        {
            _requestedPaths.Add(target);
            if (requestBody.Length > 0) _requestBodies.Add((target, requestBody));
            arrived = [.. _waiters.Where(waiter => waiter.Path == target).Select(waiter => waiter.Arrived)];
            _waiters.RemoveAll(waiter => waiter.Path == target);
            answer = _answers.GetValueOrDefault($"{method} {target}");
        }

        foreach (var waiter in arrived) waiter.TrySetResult();

        var (status, body) = answer is not null
            ? ("200 OK", answer)
            : target.StartsWith(CellsPath, StringComparison.Ordinal) && _cellsJson.Length > 0
                ? ("200 OK", _cellsJson)
                : LoadUnitOf(target) is { } description
                    ? ("200 OK", description)
                    : ("503 Service Unavailable", "{}");

        var payload = Encoding.UTF8.GetBytes(body);
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\n" +
            "Content-Type: application/json; charset=utf-8\r\n" +
            $"Content-Length: {payload.Length}\r\n" +
            "Connection: close\r\n\r\n");

        await stream.WriteAsync(head, stopping);
        await stream.WriteAsync(payload, stopping);
        await stream.FlushAsync(stopping);
    }

    /// <summary>
    /// The description of the load unit a request asks about, if it is one of those this fake knows.
    /// By value, not by text: the engine writes the identifier with the scale it has
    /// (<c>load-units/4242.0/description</c>, MEASURED), and 4242.0 is 4242.
    /// </summary>
    private string? LoadUnitOf(string target) =>
        target.StartsWith(LoadUnitsPath, StringComparison.Ordinal) && target.EndsWith(DescriptionSuffix, StringComparison.Ordinal)
        && decimal.TryParse(target[LoadUnitsPath.Length..^DescriptionSuffix.Length], System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out var id)
        && _loadUnitsJson.TryGetValue(id, out var json)
            ? json
            : null;

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        _listener.Stop();
        try
        {
            await _serving;
        }
        catch (OperationCanceledException)
        {
        }

        _stopping.Dispose();
    }
}

/// <summary>One cell of the fake warehouse, in the shape the boundary hands to the WCS.</summary>
public sealed record StubCell(decimal CellId, string Warehouse, string Area, int Column, int? Floor,
    string? DoorA, string? DoorB, string? Description);

/// <summary>A load unit the fake warehouse describes: its identifier and its code, the rest zero.</summary>
public sealed record StubLoadUnit(decimal LoadUnitId, string Code);
