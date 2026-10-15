using System.Text.Json;
using Wcs.WebEditor.Live;
using Wcs.WebEditor.Model;
using Xunit;

namespace Wcs.WebEditor.Test;

public sealed class LiveFrameJsonTest
{
    [Fact]
    public void ThePlantsIdentifiersBecomeTheDrawingsCells()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var frame = new LiveFrame([new LiveDeviceView(a, "#ff0000", "In Error", "LU-7 (7)")], [new LiveMotion(a, b)]);

        var json = JsonDocument.Parse(LiveFrameJson.Build(frame, id => id == a ? "cell-a" : "cell-b", _ => LoadUnitSize.Palette)).RootElement;

        var device = json.GetProperty("devices")[0];
        Assert.Equal("cell-a", device.GetProperty("cellId").GetString());
        Assert.Equal("#ff0000", device.GetProperty("color").GetString());
        Assert.Equal("In Error", device.GetProperty("statusName").GetString());
        Assert.Equal("LU-7 (7)", device.GetProperty("loadUnit").GetString());
        var motion = json.GetProperty("motions")[0];
        Assert.Equal("cell-a", motion.GetProperty("from").GetString());
        Assert.Equal("cell-b", motion.GetProperty("to").GetString());
    }

    /// <summary>
    /// Queue item 21: every device of the frame carries the size its load unit is drawn with, the
    /// one the page's graph gives for THAT device - asked by its plant identifier, not by its cell.
    /// Two devices with two different sizes, so that a size taken from the wrong device, or the
    /// same size written for all, shows.
    /// </summary>
    [Fact]
    public void EveryDeviceCarriesItsOwnLoadUnitSize()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var frame = new LiveFrame(
            [new LiveDeviceView(a, "#ff0000", "In Error", "LU-7 (7)"), new LiveDeviceView(b, null, "Ready", "LU-8 (8)")], []);
        var sizes = new Dictionary<Guid, LoadUnitSize> { [a] = new(40, 30), [b] = new(105, 90) };

        var devices = JsonDocument.Parse(LiveFrameJson.Build(frame, id => id == a ? "cell-a" : "cell-b", id => sizes[id]))
            .RootElement.GetProperty("devices").EnumerateArray()
            .ToDictionary(d => d.GetProperty("cellId").GetString()!);

        Assert.Equal(40, devices["cell-a"].GetProperty("loadUnitWidth").GetInt32());
        Assert.Equal(30, devices["cell-a"].GetProperty("loadUnitHeight").GetInt32());
        Assert.Equal(105, devices["cell-b"].GetProperty("loadUnitWidth").GetInt32());
        Assert.Equal(90, devices["cell-b"].GetProperty("loadUnitHeight").GetInt32());
    }
}
