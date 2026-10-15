using Microsoft.EntityFrameworkCore;
using Wcs.Engine.Collections;
using Wcs.Engine.Items;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Wcs.Engine.Test.Routing;
using Wcs.Wms.Contracts;
using Wcs.Wms.Contracts.Test;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// Lo svuotamento di una cella chiesto dall'operatore
/// (<c>ExecutableCommands.ChangeLUsStoreLocation</c>): il comando risulta eseguito solo se le righe
/// di coda sono state scritte davvero.
/// </summary>
/// <remarks>
/// Le due scritture di questo percorso erano lanciate e non attese (<c>_ = ExtractionQueue.
/// UpsertAsync(row)</c>), mentre le altre quattro dello stesso file erano attese.
/// <c>EnqueueAndWaitAsync</c> e' progettata per <b>riferire il rifiuto</b> — coda piena o scrittore
/// che si sta fermando — e con <c>_ =</c> quel rifiuto non lo guardava nessuno: subito dopo il
/// ciclo il comando dell'operatore veniva marcato <c>Done</c> con la coda che poteva essere vuota.
/// </remarks>
public sealed class EmptyCellCommandQueueWriteTest : IDisposable
{
    private const decimal SourceCellId = 501;
    private const decimal DestinationCellId = 502;
    private const string PickUpDoor = "P01A";
    private const string StorageDoor = "S01A";
    private const decimal LoadUnitId = 900;

    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly QueuedDatabaseWriter<WcsWrite>? _previousStateWriter = Navigator.StateWriter;
    private readonly FakeWarehouse _warehouse = new();
    private readonly RoutingTestPlant _plant = new(NavigatorStatusTypes.Run);

    public EmptyCellCommandQueueWriteTest()
    {
        Navigator.Store = _database;
        Navigator.Warehouse = _warehouse;
        Navigator.StateWriter = null;
    }

    public void Dispose()
    {
        _plant.Dispose();
        Navigator.StateWriter = _previousStateWriter;
        Navigator.Warehouse = _previousWarehouse;
        Navigator.Store = null;
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// La coda rifiuta la scrittura: il comando va in errore, non in <c>Done</c>. E' la meta' che
    /// senza l'<c>await</c> non poteva scattare, perche' il rifiuto viveva in un <see cref="Task"/>
    /// che nessuno guardava.
    /// </summary>
    [Fact]
    public async Task ARejectedQueueWriteDoesNotMarkTheCommandAsDone()
    {
        var command = await ACommandToEmptyTheCell();

        // Uno scrittore fermato rifiuta ogni scrittura con QueuedWriteRejectedException: e' una
        // delle due condizioni che EnqueueAndWaitAsync riferisce al chiamante.
        var writer = new QueuedDatabaseWriter<WcsWrite>((_, _) => Task.CompletedTask);
        await writer.DisposeAsync();
        Navigator.StateWriter = writer;

        await ExecutableCommands.ChangeLUsStoreLocation(command);

        Assert.Equal(Command.StatusTypes.Error, command.Status);
        Assert.NotEqual(Command.StatusTypes.Done, command.Status);
        Assert.False(string.IsNullOrEmpty(command.ErrorDescription));
    }

    /// <summary>
    /// La meta' che deve riuscire: senza questa, uno svuotamento che non facesse mai niente
    /// passerebbe il collaudo qui sopra. La riga entra in coda e il comando risulta eseguito.
    /// </summary>
    [Fact]
    public async Task AnAcceptedQueueWriteMarksTheCommandAsDoneAndLeavesTheRowInTheQueue()
    {
        var command = await ACommandToEmptyTheCell();

        await ExecutableCommands.ChangeLUsStoreLocation(command);

        Assert.Equal(Command.StatusTypes.Done, command.Status);
        Assert.Equal([LoadUnitId], await QueuedLoadUnits());
    }

    private async Task<Command> ACommandToEmptyTheCell()
    {
        var storage = _plant.Device("S", DeviceTypes.Storage);
        var relocator = _plant.Device("R");
        var destination = _plant.Device("D");

        storage.RelocateItemId = relocator.ItemGuid;

        await PlantPersistence.SaveDoorMappingsAsync(storage.ItemGuid,
            [new DoorMapping { ItemId = storage.ItemGuid, DoorCode = PickUpDoor, CellId = SourceCellId, Side = "A" }]);
        await PlantPersistence.SaveDoorMappingsAsync(destination.ItemGuid,
            [new DoorMapping { ItemId = destination.ItemGuid, DoorCode = StorageDoor, CellId = DestinationCellId, Side = "A" }]);

        _warehouse.CellsByDoor[PickUpDoor] = [Cell(SourceCellId, PickUpDoor)];
        _warehouse.CellsByDoor[StorageDoor] = [Cell(DestinationCellId, StorageDoor)];
        _warehouse.CellContents[SourceCellId] = [LoadUnitId];

        return new Command
        {
            PickUpDoorCode = PickUpDoor,
            StorageDoorCode = StorageDoor,
            WorkstationId = "PC-01",
            Status = Command.StatusTypes.Running
        };
    }

    private static CellDescription Cell(decimal cellId, string doorCode) =>
        new(cellId, AreaId: 1, WarehouseId: 1, Depth: 1, Inverted: false, CellLockState.None,
            AllowedLoadUnitTypeId: null, GroupId: null, GroupOrder: 0, doorCode, DoorCodeB: string.Empty,
            DoorCount: 1, MultiDepthStorage: false);

    private async Task<List<decimal>> QueuedLoadUnits()
    {
        await using var context = _database.CreateContext();
        return await context.ExtractionQueue.Select(r => r.LoadUnitId).OrderBy(id => id).ToListAsync();
    }
}
