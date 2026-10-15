using Wcs.Engine.Items;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// Il diagramma sul database del WCS: dispositivi, giunzioni e il modello disegnato.
/// </summary>
/// <remarks>
/// Un dispositivo nasce con <c>Load()</c>, che legge la riga dallo store se c'e': per questo il
/// test scrive prima la riga e poi crea il dispositivo, e controlla che la riga sia arrivata nelle
/// proprieta'. Lo store e' nel <c>Navigator</c> solo per la durata del test, senza scrittore: le
/// scritture dell'ORM vanno direttamente sul database in memoria.
/// </remarks>
public sealed class PlantStoreTest : IDisposable
{
    private readonly NavigatorStatusTypes _previousStatus = Navigator.NavStatus;
    private readonly WcsDatabase _database = WcsDatabase.InMemory();

    public PlantStoreTest()
    {
        Navigator.NavStatus = NavigatorStatusTypes.Recover;
        Navigator.Store = _database;
    }

    public void Dispose()
    {
        Navigator.Store = null;
        Navigator.NavStatus = _previousStatus;
        _database.Dispose();
    }

    private static PlantItem SampleRow(Guid itemId) => new()
    {
        ItemId = itemId,
        Name = "Trasloelevatore 1",
        DeviceType = (short)DeviceTypes.GroupOfAutomationDevices,
        Status = (short)DeviceStatusTypes.Ready,
        Asi = "0007",
        Partition = "0001",
        Subitem = "0002",
        DelayBlind = 300,
        TimeOutPlcMoving = 45,
        LoadUnitImageHeight = 80,
        LoadUnitImageWidth = 120,
        AutoExecuteStep = true,
        BuildDestinationItemId = Guid.NewGuid(),
        ConnectionType = 1,
        MotionType = 1,
        RelocateItemId = Guid.NewGuid(),
        LinkedCellId = 42,
        StorageOffset = 3,
        GroupItemId = 5,
        GroupLevelId = 2,
        StoragePartition = "A001",
        MaxUnloadWeight = 1200,
        WaitLookingLevels = 4
    };

    [Fact]
    public async Task ADeviceBornWithARowInTheStoreReadsItsProperties()
    {
        var itemId = Guid.NewGuid();
        var row = SampleRow(itemId);

        using (var context = _database.CreateContext())
        {
            await PlantStore.UpsertItemAsync(context, row);
            context.SaveChanges();
        }

        var device = new AutomationDevice(itemId);
        device.Load();

        Assert.Equal("Trasloelevatore 1", device.Name);
        Assert.Equal(DeviceTypes.GroupOfAutomationDevices, device.DeviceType);
        Assert.Equal("0007", device.ASI);
        Assert.Equal(300, device.DelayBlind);
        Assert.Equal(80, device.LuImageSize.Height);
        Assert.Equal(120, device.LuImageSize.Width);
        Assert.True(device.AutoExecuteStep);
        Assert.Equal(row.BuildDestinationItemId, device.DestinationItemId);
        Assert.Equal(row.RelocateItemId, device.RelocateItemId);
        Assert.Equal(42, device.LinkedCellId);
        Assert.Equal(5, device.GroupItemId);
        Assert.Equal("A001", device.StoragePartition);
        Assert.Equal(1200, device.MaxUnloadWeight);
        Assert.Equal(4, device.WaitLookingLevels);
        Assert.Equal(Guid.Empty, device.RunningStepId);
    }

    [Fact]
    public void ADeviceWithoutARowWritesOneWithItsCurrentValuesAndTheSnapshotRoundTrips()
    {
        var device = new AutomationDevice(Guid.NewGuid()) { Name = "Rulliera 3", DelayBlind = 150 };
        device.Load();

        using var context = _database.CreateContext();
        var row = Assert.Single(context.PlantItems);
        Assert.Equal(device.ItemGuid, row.ItemId);
        Assert.Equal("Rulliera 3", row.Name);
        Assert.Equal(150, row.DelayBlind);

        // Un identificatore vuoto in memoria e' nullo nella riga; l'ASI di un dispositivo nuovo
        // e' "0000", non vuoto, e resta tale.
        Assert.Null(row.RelocateItemId);
        Assert.Equal("0000", row.Asi);
    }

    [Fact]
    public async Task DeletingADeviceTakesItsJunctionsWithIt()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();

        await using (var context = _database.CreateContext())
        {
            await PlantStore.UpsertItemAsync(context, SampleRow(a));
            await PlantStore.UpsertItemAsync(context, SampleRow(b));
            await PlantStore.UpsertItemAsync(context, SampleRow(c));
            await PlantStore.UpsertJunctionAsync(context, new PlantJunction { JunctionId = Guid.NewGuid(), SourceItemId = a, DestinationItemId = b, Priority = 1 });
            await PlantStore.UpsertJunctionAsync(context, new PlantJunction { JunctionId = Guid.NewGuid(), SourceItemId = b, DestinationItemId = c, Priority = 2 });
            await context.SaveChangesAsync();
        }

        await using (var context = _database.CreateContext())
        {
            await PlantStore.DeleteItemAsync(context, b);
            await context.SaveChangesAsync();
        }

        await using var reading = _database.CreateContext();
        Assert.Equal([a, c], reading.PlantItems.Select(i => i.ItemId).OrderBy(id => id == c).ToList());
        Assert.Empty(reading.PlantJunctions);
    }

    [Fact]
    public async Task AJunctionIsRecognisedByItsEndpointsAndKeepsItsKey()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var first = new PlantJunction { JunctionId = Guid.NewGuid(), SourceItemId = a, DestinationItemId = b, Priority = 1 };

        await using (var context = _database.CreateContext())
        {
            await PlantStore.UpsertJunctionAsync(context, first);
            await context.SaveChangesAsync();
        }

        // Stessa coppia, chiave diversa, priorita' nuova: e' la stessa giunzione.
        await using (var context = _database.CreateContext())
        {
            await PlantStore.UpsertJunctionAsync(context, new PlantJunction { JunctionId = Guid.NewGuid(), SourceItemId = a, DestinationItemId = b, Priority = 7 });
            await context.SaveChangesAsync();
        }

        await using var reading = _database.CreateContext();
        var row = Assert.Single(reading.PlantJunctions);
        Assert.Equal(first.JunctionId, row.JunctionId);
        Assert.Equal(7, row.Priority);

        var junction = new AutomationJunction(a, b);
        Assert.Equal(first.JunctionId, junction.ItemGuid);
        Assert.Equal(7, junction.Priority);
    }

    [Fact]
    public async Task TheDiagramIsSavedAndReadBackAndTheCountersLiveOnTheSameRow()
    {
        byte[] binary = [1, 2, 3];
        byte[] xml = "<diagram/>"u8.ToArray();

        await PlantPersistence.SaveDiagramAsync(binary, xml);
        await PlantPersistence.SaveCountersAsync(123, 456);
        await PlantPersistence.SaveCompactingStatusAsync(2);

        Assert.Equal(xml, await PlantPersistence.LoadDiagramXmlAsync());
        Assert.Equal(2, await PlantPersistence.LoadCompactingStatusAsync());

        await using var reading = _database.CreateContext();
        var row = Assert.Single(reading.PlantDiagrams);
        Assert.Equal(PlantDiagram.DefaultId, row.Id);
        Assert.Equal(binary, row.Diagram);
        Assert.Equal(123, row.CheckCounter);
        Assert.Equal(456, row.StatusCounter);
    }
}
