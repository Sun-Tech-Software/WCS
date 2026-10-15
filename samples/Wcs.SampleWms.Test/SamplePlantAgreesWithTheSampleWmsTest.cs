using System.Text.Json;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Wms;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.SampleWms.Test;

/// <summary>
/// samples/plant/sample-plant.json and the sample WMS describe the same building from two sides (see
/// <see cref="SampleData"/>). A door code, a cell id or a device id changed on one side only gives a
/// plant that loads and a WMS that answers, and a pallet that never reaches its cell: these tests say
/// which of the two moved.
/// </summary>
public sealed class SamplePlantAgreesWithTheSampleWmsTest
{
    private static readonly PlantGraphDto Plant = JsonSerializer.Deserialize<PlantGraphDto>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "plant", "sample-plant.json")),
        new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static readonly IReadOnlyList<SampleCell> Cells = SampleData.Cells();

    private static PlantItemConfigDto Device(string name) => Plant.Items.Single(i => i.Name == name);

    [Fact]
    public void EveryDoorMappingOfThePlantIsADoorOfTheSampleWms()
    {
        Assert.Equal(Cells.Count, Plant.DoorMappings.Count);

        foreach (var mapping in Plant.DoorMappings)
        {
            var cell = Assert.Single(Cells, c => c.Id == mapping.CellId);
            Assert.Equal(cell.DoorCodeA, mapping.DoorCode);
            Assert.Equal("A", mapping.Side);
            Assert.Contains(mapping.ItemId, cell.AccessDeviceIds);
        }
    }

    [Fact]
    public void TheDevicesTheWmsNamesAreThoseOfThePlant()
    {
        Assert.Equal(SampleData.Infeed, Device("IN01").ItemId);
        Assert.Equal(SampleData.StackerCrane, Device("SC01").ItemId);
        Assert.Equal(SampleData.Storage, Device("ST01").ItemId);
        Assert.Equal(SampleData.Outfeed, Device("OUT01").ItemId);
    }

    /// <summary>
    /// The storage device is served by the crane, which picks from the conveyor and stores (GetFrom)
    /// at a fixed depth - the configuration the WCS needs to store into a cell and pick from it.
    /// </summary>
    [Fact]
    public void TheStorageIsServedByTheCrane()
    {
        Assert.Equal((short)DeviceTypes.Storage, Device("ST01").DeviceType);
        Assert.Equal(SampleData.StackerCrane, Device("ST01").RelocateItemId);
        Assert.Equal((short)MotionTypes.GetFrom, Device("SC01").MotionType);
        Assert.Equal((short)RecordStorageTypes.FixedLength, Device("SC01").RecordStorageLength);
    }

    /// <summary>The bays are linked to the WMS's bay cells: the infeed tells the WCS which warehouse to store in.</summary>
    [Fact]
    public void TheBaysAreLinkedToTheBayCells()
    {
        Assert.Equal(SampleData.InfeedBayCellId, Device("IN01").LinkedCellId);
        Assert.Equal((short)LookWhereAllocateLuTypes.BuildRouteToStore, Device("IN01").LookWhereAllocateLoadUnit);
        Assert.Equal(SampleData.OutfeedBayCellId, Device("OUT01").LinkedCellId);
        Assert.Equal((short)DeviceTypes.Buffer, Device("OUT01").DeviceType);
    }

    /// <summary>
    /// The editor's cell-linking panel of a bay lists the WMS cells it can be linked to, and asks for
    /// them with the area type of the device type and automatic areas only - the very call of
    /// <c>DrawioSelectionCoordinator</c>, built here with the editor's own <see cref="CellLinking"/>.
    /// The cell each bay is linked to must be among them, or the panel opens without it.
    /// </summary>
    [Theory]
    [InlineData("IN01", 800)]
    [InlineData("OUT01", 900)]
    public void TheEditorListsTheCellABayIsLinkedTo(string deviceName, int linkedCellId)
    {
        var device = Device(deviceName);
        var deviceType = (DeviceTypes)device.DeviceType;
        var warehouse = new SampleWarehouse(TimeProvider.System, TimeSpan.FromMinutes(5));

        Assert.Equal(CellPanelKind.LinkedCell, CellLinking.KindFor(deviceType));
        Assert.Equal(linkedCellId, device.LinkedCellId);
        Assert.Contains(warehouse.GetCellsForEditor(CellLinking.AreaTypeFor(deviceType), CellLinking.KindFor(deviceType) is CellPanelKind.LinkedCell),
            row => row.CellId == linkedCellId);
    }

    /// <summary>Every device of the sample runs on the loopback driver: no PLC is ever contacted.</summary>
    [Fact]
    public void EveryDeviceIsOnTheLoopbackDriver()
    {
        Assert.All(Plant.Items, item => Assert.Equal((short)ConnectionTypes.Test, item.ConnectionType));
        Assert.All(Plant.Items, item => Assert.Equal(0m, item.DeviceConnectionId));
        Assert.Null(Plant.PlcConnections);
    }

    /// <summary>The sample listens where the WCS host looks for a WMS by default (Wms:ApiAddress).</summary>
    [Fact]
    public void TheSampleListensOnTheHostsDefaultWmsAddress()
    {
        var root = WcsHostProcess.RepositoryRoot();
        var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        using var sample = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "samples", "Wcs.SampleWms", "appsettings.json")), options);
        using var host = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "WCS", "Wcs.Host", "src", "appsettings.json")), options);

        Assert.Equal("http://localhost:5142", sample.RootElement.GetProperty("Urls").GetString());
        Assert.Equal("http://localhost:5142", host.RootElement.GetProperty("Wms").GetProperty("ApiAddress").GetString());
    }
}
