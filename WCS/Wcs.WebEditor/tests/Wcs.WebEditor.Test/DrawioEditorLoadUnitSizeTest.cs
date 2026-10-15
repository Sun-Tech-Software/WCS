using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Pages;
using Wcs.WebEditor.Session;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

/// <summary>
/// Queue item 21, through the WHOLE page: the size of each device's load unit reaches the canvas in
/// the live frame, from the configuration read with the plant - and a size written by this page (the
/// properties panel, the device list) is on the canvas at once, in a new frame, without waiting for
/// a live event that on an idle plant may never come.
/// </summary>
/// <remarks>
/// The frames are the ones the page hands to <c>drawio-live.js</c>'s <c>render</c>, recorded as they
/// are sent: the wait is on the frame itself (a <see cref="TaskCompletionSource"/> completed by the
/// recorder), not on a clock - the timeout only stops a hang.
/// </remarks>
public sealed class DrawioEditorLoadUnitSizeTest : PlantCanvasBunitContext
{
    private const string LiveModule = "./_content/Wcs.WebEditor/drawio-live.js";
    private static readonly Guid A = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002");

    private readonly DrawioWriteHandler _host = new();
    private readonly FakePlantEventSource _source = new();
    private readonly object _framesLock = new();
    private readonly List<string> _frames = [];
    private (Func<string, bool> Want, TaskCompletionSource Seen)? _waiting;

    public DrawioEditorLoadUnitSizeTest()
    {
        _host.Items = [Item(A, "Shuttle 1", 10), Item(B, "Shuttle 2", 300)];
        // A has a size of its own; B has one side only, which is the palette's (105 x 105).
        _host.Configs[A] = Config(A, "Shuttle 1", 40, 30);
        _host.Configs[B] = Config(B, "Shuttle 2", 0, 30);
        // Both loaded and in error: both are in every frame, each with its load unit.
        _host.ItemStatuses = [Loaded(A), Loaded(B)];

        var live = JSInterop.SetupModule(LiveModule);
        live.Setup<string>("render", invocation => Record((string)invocation.Arguments[0]!)).SetResult("drawn");
        var bridge = JSInterop.SetupModule("./_content/Wcs.WebEditor/drawio-bridge.js");
        bridge.Setup<string?>("selectCell", _ => true).SetResult("ok");
        bridge.Setup<bool>("renameCell", _ => true).SetResult(true);
    }

    private static PlantItemDto Item(Guid id, string name, double x) =>
        new(id, name, DeviceTypes.Automation, null, null, null, ConnectionTypes.Test,
            default, 0, null, 0, 0, 0, 0, default!, x, 10, 120, 60, 0);

    private static PlantItemConfigDto Config(Guid id, string name, int luWidth, int luHeight) => new()
    {
        ItemId = id,
        Name = name,
        DeviceType = (short)DeviceTypes.Automation,
        ConnectionType = (short)ConnectionTypes.Test,
        LoadUnitImageWidth = luWidth,
        LoadUnitImageHeight = luHeight,
    };

    private static ItemStatus Loaded(Guid id) =>
        new(id, "", "", "", "A", DeviceStatusTypes.Error, LuLoadStatusTypes.Loaded, "LU-7", 7m, Guid.Empty, Guid.Empty);

    /// <summary>The recorder bUnit calls for every <c>render</c> the page sends: always a match.</summary>
    private bool Record(string frame)
    {
        lock (_framesLock)
        {
            _frames.Add(frame);
            if (_waiting is { } waiting && waiting.Want(frame)) waiting.Seen.TrySetResult();
        }

        return true;
    }

    /// <summary>Waits for a frame sent from now on (or already sent after <paramref name="from"/>) that <paramref name="want"/> accepts.</summary>
    private async Task WaitForFrameAsync(int from, Func<string, bool> want, string what)
    {
        var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_framesLock)
        {
            if (_frames.Skip(from).Any(want)) return;
            _waiting = (want, seen);
        }

        try
        {
            await seen.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
            string[] sent;
            lock (_framesLock) sent = [.. _frames.Skip(from)];
            Assert.Fail($"No frame {what} reached the canvas. Frames sent since: {sent.Length}{(sent.Length > 0 ? ", the last " + sent[^1] : "")}");
        }
        finally
        {
            lock (_framesLock) _waiting = null;
        }
    }

    private int FramesSoFar()
    {
        lock (_framesLock) return _frames.Count;
    }

    /// <summary>The load unit size a frame gives the device drawn as <paramref name="itemId"/>'s cell (seeded devices: the cell is the plant identifier).</summary>
    private static (int Width, int Height)? SizeIn(string frame, Guid itemId)
    {
        foreach (var device in JsonDocument.Parse(frame).RootElement.GetProperty("devices").EnumerateArray())
            if (device.GetProperty("cellId").GetString() == itemId.ToString())
                return (device.GetProperty("loadUnitWidth").GetInt32(), device.GetProperty("loadUnitHeight").GetInt32());
        return null;
    }

    private async Task<IRenderedComponent<DrawioEditor>> RenderLiveAsync()
    {
        var state = new EditorSessionState(new Uri("http://localhost:5580"), _host) { EventSourceForTesting = () => _source };
        state.UseKey("test-key");
        Services.AddSingleton(state);
        var cut = Render<DrawioEditor>();
        cut.WaitForAssertion(() => Assert.False(cut.Find("#drawio-mode-toggle").HasAttribute("disabled")));
        await cut.InvokeAsync(() => cut.Instance.OnDrawioReady());
        await WaitForFrameAsync(0, frame => SizeIn(frame, A) is not null && SizeIn(frame, B) is not null, "with both loaded devices");
        return cut;
    }

    private static void Act(IRenderedComponent<DrawioEditor> cut, Action gesture) =>
        cut.InvokeAsync(gesture).GetAwaiter().GetResult();

    /// <summary>The configuration read with the plant is what the first frame carries: A's own size, and the palette's for B.</summary>
    [Fact]
    public async Task TheFirstFrameCarriesTheSizesReadWithThePlant()
    {
        await RenderLiveAsync();

        string first;
        lock (_framesLock) first = _frames.Last(frame => SizeIn(frame, A) is not null && SizeIn(frame, B) is not null);
        Assert.Equal((40, 30), SizeIn(first, A));
        Assert.Equal((105, 105), SizeIn(first, B));
    }

    /// <summary>
    /// A size written from the properties panel is in a NEW frame at once: nothing happens on the
    /// plant after the write, so no live event could have sent it.
    /// </summary>
    [Fact]
    public async Task ASizeWrittenFromThePanelIsOnTheCanvasAtOnce()
    {
        var cut = await RenderLiveAsync();
        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([A.ToString()]));
        cut.WaitForAssertion(() =>
        {
            Assert.NotEmpty(cut.FindAll("div.property-panel-field[data-property='LoadUnitImageWidth'] input"));
            Assert.Empty(cut.FindAll("#drawio-cells-loading"));
        });
        var before = FramesSoFar();

        Act(cut, () => cut.Find("div.property-panel-field[data-property='LoadUnitImageWidth'] input").Change("90"));
        Act(cut, () => cut.Find("#property-panel-save-button").Click());

        cut.WaitForAssertion(() => Assert.Equal(90, Assert.Single(_host.ConfigWrites).LoadUnitImageWidth));
        await WaitForFrameAsync(before, frame => SizeIn(frame, A) == (90, 30), "with A's load unit at the 90 x 30 just written");
    }

    /// <summary>The same for a cell of the device list: B had one side only (the palette's); with both written it has its own.</summary>
    [Fact]
    public async Task ASizeWrittenFromTheDeviceListIsOnTheCanvasAtOnce()
    {
        var cut = await RenderLiveAsync();
        Act(cut, () => cut.Find("#drawio-device-list-toggle").Click());
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("#drawio-device-list-table tbody tr").Count));
        var before = FramesSoFar();

        Act(cut, () => cut.Find($"#drawio-device-list-table tr[data-item-id='{B}'] td[data-property='LoadUnitImageWidth']").DoubleClick());
        Act(cut, () => cut.Find("#drawio-device-list-editor-value").Input("70"));
        Act(cut, () => cut.Find("#drawio-device-list-editor-write").Click());

        cut.WaitForAssertion(() => Assert.Contains("Written", cut.Find("#drawio-device-list-outcome").TextContent));
        await WaitForFrameAsync(before, frame => SizeIn(frame, B) == (70, 30), "with B's load unit at the 70 x 30 just written");
    }
}
