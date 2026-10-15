using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// La mappa porte-celle-dispositivi sul database del WCS: le domande del motore — le tre ricerche,
/// il blocco al deposito, le celle di un ricollocatore —, i due stati, e il collegamento rifatto
/// dall'Editor che non perde gli stati.
/// </summary>
public sealed class DoorMappingStoreTest : IDisposable
{
    private readonly WcsDatabase _database = WcsDatabase.InMemory();

    public DoorMappingStoreTest()
    {
        Navigator.Store = _database;
    }

    public void Dispose()
    {
        Navigator.Store = null;
        _database.Dispose();
    }

    [Fact]
    public async Task TheThreeLookupsAnswerFromTheMapping()
    {
        var item = Guid.NewGuid();
        await PlantRows.EnsureAsync(item);
        await PlantPersistence.SaveDoorMappingsAsync(item,
        [
            new DoorMapping { ItemId = item, DoorCode = "P01A", CellId = 10, Side = "A" },
            new DoorMapping { ItemId = item, DoorCode = "P01B", CellId = 10, Side = "B" }
        ]);

        Assert.Equal(10m, await ORM.DbInfo.GetCellId("P01A"));
        Assert.Equal(0m, await ORM.DbInfo.GetCellId("P99Z"));
        Assert.Equal("P01A", await ORM.DbInfo.GetDoorCode(item));
        Assert.Equal(string.Empty, await ORM.DbInfo.GetDoorCode(Guid.NewGuid()));
        Assert.Equal(item, await ORM.DbInfo.GetItemId(10m));
        Assert.Equal(item, await ORM.DbInfo.GetItemId("P01B"));
        Assert.Equal(Guid.Empty, await ORM.DbInfo.GetItemId("P99Z"));
    }

    [Fact]
    public async Task TheTwoStatesAreWrittenByTheEngineAndReadBack()
    {
        var item = Guid.NewGuid();
        await PlantRows.EnsureAsync(item);
        await PlantPersistence.SaveDoorMappingsAsync(item, [new DoorMapping { ItemId = item, DoorCode = "P02A", CellId = 20, Side = "A" }]);

        await ORM.DbInfo.SetCellLockedForStoring("P02A");
        await ORM.DbInfo.SetOperationLockingCell(20, StatoOperazioneCella.Deposito);

        await using (var context = _database.CreateContext())
        {
            var row = Assert.Single(context.DoorMappings);
            Assert.True(row.StoringLocked);
            Assert.Equal((short)StatoOperazioneCella.Deposito, row.OperationActive);
            Assert.Equal([20m], await DoorMappingStore.CellsWithActiveOperationAsync(context));
        }

        await ORM.DbInfo.SetCellUnlockedForStoring("P02A");

        await using var reading = _database.CreateContext();
        Assert.False(reading.DoorMappings.Single().StoringLocked);
    }

    /// <summary>
    /// Il motore chiedeva al WMS «la cella e' bloccata al deposito?» su un dato che e' suo. Basta
    /// una porta bloccata: la lettura prudente del SELECT STORING_LOCKED ... WHERE ID_CELLA, che
    /// prendeva la prima riga in un ordine non definito.
    /// </summary>
    [Fact]
    public async Task ACellIsLockedForStoringWhenAnyOfItsDoorsIs()
    {
        var item = Guid.NewGuid();
        await PlantRows.EnsureAsync(item);
        await PlantPersistence.SaveDoorMappingsAsync(item,
        [
            new DoorMapping { ItemId = item, DoorCode = "P04A", CellId = 40, Side = "A" },
            new DoorMapping { ItemId = item, DoorCode = "P04B", CellId = 40, Side = "B" }
        ]);

        Assert.False(await ORM.DbInfo.GetCellLockedForStoring(40));
        await ORM.DbInfo.SetCellLockedForStoring("P04A");
        Assert.True(await ORM.DbInfo.GetCellLockedForStoring(40));
        await ORM.DbInfo.SetCellUnlockedForStoring("P04A");
        Assert.False(await ORM.DbInfo.GetCellLockedForStoring(40));
        Assert.False(await ORM.DbInfo.GetCellLockedForStoring(99));
        Assert.False(await ORM.DbInfo.GetCellLockedForStoring(0));
    }

    /// <summary>
    /// Le celle di un ricollocatore: quelle delle porte dei magazzini che lo hanno come
    /// RelocateItemId. Era la sottoquery su ITEMS_MAP e NAVIGATOR_ITEMS.RELOCATE_LU_ITEM che il WMS
    /// faceva in TrovaUdc(idUdc, idItem) per conto del motore: il dato e' del WCS e si legge qui.
    /// </summary>
    [Fact]
    public async Task TheCellsServedByARelocatorAreThoseOfTheStoragesItRelocatesFor()
    {
        var relocator = Guid.NewGuid();
        var otherRelocator = Guid.NewGuid();
        var storage1 = Guid.NewGuid();
        var storage2 = Guid.NewGuid();
        var farStorage = Guid.NewGuid();
        await using (var context = _database.CreateContext())
        {
            context.PlantItems.Add(new PlantItem { ItemId = relocator });
            context.PlantItems.Add(new PlantItem { ItemId = storage1, RelocateItemId = relocator });
            context.PlantItems.Add(new PlantItem { ItemId = storage2, RelocateItemId = relocator });
            context.PlantItems.Add(new PlantItem { ItemId = farStorage, RelocateItemId = otherRelocator });
            await context.SaveChangesAsync();
        }
        await PlantPersistence.SaveDoorMappingsAsync(storage1,
        [
            new DoorMapping { ItemId = storage1, DoorCode = "P05A", CellId = 50, Side = "A" },
            new DoorMapping { ItemId = storage1, DoorCode = "P05B", CellId = 50, Side = "B" }, // due porte, una cella: una volta sola
            new DoorMapping { ItemId = storage1, DoorCode = "P06A", CellId = 51, Side = "A" }
        ]);
        await PlantPersistence.SaveDoorMappingsAsync(storage2, [new DoorMapping { ItemId = storage2, DoorCode = "P07A", CellId = 52, Side = "A" }]);
        await PlantPersistence.SaveDoorMappingsAsync(farStorage, [new DoorMapping { ItemId = farStorage, DoorCode = "P08A", CellId = 53, Side = "A" }]);

        Assert.Equal([50m, 51m, 52m], await ORM.DbInfo.GetCellsServedByRelocator(relocator));
        Assert.Equal([53m], await ORM.DbInfo.GetCellsServedByRelocator(otherRelocator));
        Assert.Empty(await ORM.DbInfo.GetCellsServedByRelocator(Guid.NewGuid()));
        Assert.Empty(await ORM.DbInfo.GetCellsServedByRelocator(Guid.Empty));
    }

    [Fact]
    public async Task RelinkingADeviceKeepsTheStatesOfTheDoorsThatStayAndDropsTheOthers()
    {
        var item = Guid.NewGuid();
        await PlantRows.EnsureAsync(item);
        await PlantPersistence.SaveDoorMappingsAsync(item,
        [
            new DoorMapping { ItemId = item, DoorCode = "P03A", CellId = 30, Side = "A" },
            new DoorMapping { ItemId = item, DoorCode = "P03B", CellId = 30, Side = "B" }
        ]);
        await ORM.DbInfo.SetCellLockedForStoring("P03A");

        // L'Editor rifa' il collegamento: resta la porta A, sparisce la B, arriva una porta nuova.
        await PlantPersistence.SaveDoorMappingsAsync(item,
        [
            new DoorMapping { ItemId = item, DoorCode = "P03A", CellId = 30, Side = "A" },
            new DoorMapping { ItemId = item, DoorCode = "P04A", CellId = 40, Side = "A" }
        ]);

        var mappings = await PlantPersistence.LoadDoorMappingsAsync(item);
        Assert.Equal(["P03A", "P04A"], mappings.Select(m => m.DoorCode));
        Assert.True(mappings.Single(m => m.DoorCode == "P03A").StoringLocked);
        Assert.False(mappings.Single(m => m.DoorCode == "P04A").StoringLocked);
    }

    [Fact]
    public async Task LinkingABufferCellReplacesWhateverTheDeviceHad()
    {
        var item = Guid.NewGuid();
        await PlantRows.EnsureAsync(item);
        await PlantPersistence.SaveDoorMappingsAsync(item, [new DoorMapping { ItemId = item, DoorCode = "OLD", CellId = 1, Side = "A" }]);

        await PlantPersistence.LinkBufferCellAsync(item, "BUF1", 50);

        var mapping = Assert.Single(await PlantPersistence.LoadDoorMappingsAsync(item));
        Assert.Equal("BUF1", mapping.DoorCode);
        Assert.Equal(50m, mapping.CellId);
        Assert.Equal("A", mapping.Side);
    }
}
