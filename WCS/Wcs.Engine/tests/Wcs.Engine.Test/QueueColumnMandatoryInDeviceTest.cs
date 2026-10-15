using System.Data;
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
/// Voce R11, <c>AutomationDevice.AddMissionToPickUp</c>: le colonne della coda di estrazione sono
/// obbligatorie. Una colonna che non c'e' solleva e nomina la colonna, invece di diventare uno
/// zero indistinguibile da uno zero legittimo; una cella nulla continua a leggersi come zero.
/// </summary>
/// <remarks>
/// La riga viene dall'unico costruttore della tabella (<c>ExtractionQueueStore.ToDataTable</c>),
/// a cui si toglie una colonna: e' il solo modo in cui una colonna puo' mancare, cioe' un errore
/// di programmazione, ed e' proprio quello che <c>GetValue</c> nascondeva.
/// </remarks>
public sealed class QueueColumnMandatoryInDeviceTest : IDisposable
{
    private const decimal LoadUnitId = 5m;

    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly RoutingTestPlant _plant = new();

    public QueueColumnMandatoryInDeviceTest()
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
        var device = _plant.Device("R");
        var table = QueueTable(device);
        table.Columns.Remove("ID_ESTRAZIONE_UDC");

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            device.AddMissionToPickUp(AMoveCommand(), table.Rows[0], MissionTypes.ChangeStoreLocation));

        Assert.Contains("ID_ESTRAZIONE_UDC", error.Message);
        Assert.Empty(device.QueuedLUtoPickUp);
    }

    /// <summary>
    /// La meta' che deve restare: le colonne annullabili a <c>DBNull</c> (assegnazione, lavorazione,
    /// ricollocatore) si leggono come zero e vuoto, come prima della conversione.
    /// </summary>
    [Fact]
    public async Task ANullCellStillReadsAsZero()
    {
        var device = _plant.Device("R");
        var table = QueueTable(device);
        Assert.Equal(DBNull.Value, table.Rows[0]["ID_ASSEGNAZIONE_UDC"]);

        Assert.True(await device.AddMissionToPickUp(AMoveCommand(), table.Rows[0], MissionTypes.ChangeStoreLocation));

        var mission = Assert.Single(device.QueuedLUtoPickUp)!;
        Assert.Equal((LoadUnitId, 0m, 0m, Guid.Empty),
            (mission.LuId, mission.LuNoteAssignmentId, mission.NoteProcessingId, mission.RelocateItemId));
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
