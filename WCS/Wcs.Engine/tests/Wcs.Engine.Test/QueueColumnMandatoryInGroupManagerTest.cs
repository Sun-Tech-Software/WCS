using System.Data;
using Wcs.Engine.Items.Managers;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Wcs.Engine.Test.Routing;
using Wcs.Wms.Contracts;
using Wcs.Wms.Contracts.Test;
using Xunit;
using AutomationDevice = Wcs.Engine.Items.AutomationDevice;
using Command = Wcs.Engine.Items.Command;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// Voce R11, <c>AutomationDeviceGroupManager.AddMissionToGroupPickUp</c>: le colonne della coda
/// di estrazione sono obbligatorie (si veda <see cref="QueueColumnMandatoryInDeviceTest"/>). E'
/// il file in cui il refuso <c>ID_LAVORAZIONEN</c> era rimasto invisibile per anni proprio perche'
/// <c>GetValue</c> rispondeva nullo invece di sollevare.
/// </summary>
public sealed class QueueColumnMandatoryInGroupManagerTest : IDisposable
{
    private const decimal LoadUnitId = 5m;

    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly RoutingTestPlant _plant = new();

    public QueueColumnMandatoryInGroupManagerTest()
    {
        Navigator.Store = _database;
        Navigator.Warehouse = new FakeWarehouse();
    }

    public void Dispose()
    {
        _plant.Dispose();
        Navigator.Warehouse = _previousWarehouse;
        Navigator.Store = null;
        _database.Dispose();
    }

    [Fact]
    public async Task AMissingColumnThrowsNamingItAndNoMissionIsBorn()
    {
        var group = _plant.Device("G", DeviceTypes.GroupOfAutomationDevices);
        var manager = new AutomationDeviceGroupManager(group);
        var table = QueueTable(group);
        table.Columns.Remove("ID_UDC");

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            manager.AddMissionToGroupPickUp(AMoveCommand(), Guid.NewGuid(), table.Rows[0], MissionTypes.Extraction));

        Assert.Contains("ID_UDC", error.Message);
        Assert.Empty(group.QueuedLUtoPickUp);
    }

    [Fact]
    public async Task ANullCellStillReadsAsZero()
    {
        var group = _plant.Device("G", DeviceTypes.GroupOfAutomationDevices);
        var manager = new AutomationDeviceGroupManager(group);
        var table = QueueTable(group);
        Assert.Equal(DBNull.Value, table.Rows[0]["ID_ITEM_DESTINATION"]);

        await manager.AddMissionToGroupPickUp(AMoveCommand(), Guid.NewGuid(), table.Rows[0], MissionTypes.Extraction);

        var mission = Assert.Single(group.QueuedLUtoPickUp)!;
        Assert.Equal((LoadUnitId, 0m, 0m, Guid.Empty, Guid.Empty, 0m),
            (mission.LuId, mission.LuNoteAssignmentId, mission.NoteProcessingId, mission.RelocateItemId,
                mission.DestinationItemId, mission.BufferCellId));
    }

    private static DataTable QueueTable(AutomationDevice device) => ExtractionQueueStore.ToDataTable(
        [new ExtractionQueueItem { Id = 1, LoadUnitId = LoadUnitId, SourceItemId = device.ItemGuid }]);

    private static Command AMoveCommand() => new()
    {
        Date = DateTime.Now,
        CommandType = Command.CommandTypes.MoveLuAutomatic,
        LuId = LoadUnitId,
        Status = Command.StatusTypes.Done,
        WorkstationId = "WS1"
    };
}
