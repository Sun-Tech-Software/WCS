using Wcs.Engine.Api;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// Leggere e scrivere la geometria: il singolo, l'insieme, e il rifiuto in blocco quando uno degli
/// identificativi non esiste.
/// </summary>
/// <remarks>
/// I primi tre test lavorano direttamente su <see cref="PlantStore"/> e non hanno bisogno del
/// Navigator. Gli ultimi due passano dalla facciata <see cref="PlantQueries"/>, che scrive tramite
/// <c>Navigator.Store</c> (schema di <see cref="PlantConfigurationTest"/>): per questo il costruttore
/// lo imposta e il <see cref="Dispose"/> lo rimette a <c>null</c>, cosi' non resta visibile alle
/// classi di test successive. Da quando <see cref="PlantQueries.SetItemGeometryAsync"/> e
/// <see cref="PlantQueries.SetItemGeometriesAsync"/> chiedono anche loro il motore fermo (compito
/// «run sola lettura»), il costruttore impone <c>NavStatus = Stop</c> invece di fidarsi
/// dell'ambiente lasciato dalla classe precedente - <c>Navigator</c> e' statico e condiviso con
/// l'intera suite (<c>DisableTestParallelization</c>, si veda <c>Impostazioni.cs</c>).
/// </remarks>
public sealed class ItemGeometryStoreTest : IDisposable
{
    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly NavigatorStatusTypes _previousStatus = Navigator.NavStatus;

    public ItemGeometryStoreTest()
    {
        Navigator.Store = _database;
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
    }

    public void Dispose()
    {
        Navigator.Store = null;
        Navigator.NavStatus = _previousStatus;
        _database.Dispose();
    }

    private async Task<Guid> AddItemAsync(string name)
    {
        var itemId = Guid.NewGuid();
        await using var context = _database.CreateContext();
        context.PlantItems.Add(new PlantItem { ItemId = itemId, Name = name });
        await context.SaveChangesAsync();
        return itemId;
    }

    [Fact]
    public async Task TheGeometryOfADeviceIsStored()
    {
        var itemId = await AddItemAsync("IN01");

        await using (var context = _database.CreateContext())
        {
            var done = await PlantStore.SetGeometryAsync(context, itemId, new ItemGeometry(10, 20, 30, 40, 90));
            await context.SaveChangesAsync();
            Assert.True(done);
        }

        await using (var context = _database.CreateContext())
        {
            var map = await PlantStore.LoadGeometriesAsync(context);
            Assert.Equal(new ItemGeometry(10, 20, 30, 40, 90), map[itemId].Geometry);
        }
    }

    [Fact]
    public async Task ADeviceThatDoesNotExistAnswersFalse()
    {
        await using var context = _database.CreateContext();
        Assert.False(await PlantStore.SetGeometryAsync(context, Guid.NewGuid(), new ItemGeometry(1, 2, 3, 4, 0)));
    }

    [Fact]
    public async Task ANullGeometryIsALegitimateValue()
    {
        var itemId = await AddItemAsync("CV02");

        await using (var context = _database.CreateContext())
        {
            await PlantStore.SetGeometryAsync(context, itemId, new ItemGeometry(5, 5, 5, 5, 0));
            await context.SaveChangesAsync();
        }

        await using (var context = _database.CreateContext())
        {
            await PlantStore.SetGeometryAsync(context, itemId, new ItemGeometry(null, null, null, null, null));
            await context.SaveChangesAsync();
        }

        await using (var verify = _database.CreateContext())
        {
            var map = await PlantStore.LoadGeometriesAsync(verify);
            Assert.Equal(new ItemGeometry(null, null, null, null, null), map[itemId].Geometry);
        }
    }

    /// <summary>
    /// Passa dalla vera <see cref="PlantQueries.SetItemGeometriesAsync"/>, non da un doppione: se
    /// qualcuno invertisse il controllo degli identificativi mancanti e la scrittura, questo test se
    /// ne accorgerebbe.
    /// </summary>
    [Fact]
    public async Task ABatchWithAnUnknownIdMovesNobody()
    {
        var primo = await AddItemAsync("IN01");
        var ignoto = Guid.NewGuid();

        var current = await PlantQueries.ItemGeometriesAsync();
        var outcome = await PlantQueries.SetItemGeometriesAsync(
        [
            new GeometryWrite(primo, new ItemGeometry(1, 1, 1, 1, 0), current[primo].Version),
            new GeometryWrite(ignoto, new ItemGeometry(2, 2, 2, 2, 0), Guid.NewGuid())
        ]);

        Assert.Equal([ignoto], outcome.MissingItemIds);

        var map = await PlantQueries.ItemGeometriesAsync();
        Assert.Equal(new ItemGeometry(null, null, null, null, null), map[primo].Geometry);
    }

    /// <summary>
    /// La facciata singolare e la lettura d'insieme, quelle che l'host usa: dai test dell'host
    /// (<c>PlantGeometryEndpointsTest</c>) e da tre test del client (<c>WcsClientTest</c>) passano
    /// dalle rotte, qui invece direttamente dalla facciata.
    /// </summary>
    [Fact]
    public async Task TheSingularFacadeWritesAndRereadsAGeometry()
    {
        var itemId = await AddItemAsync("IN02");

        var before = (await PlantQueries.ItemGeometriesAsync())[itemId];
        var outcome = await PlantQueries.SetItemGeometryAsync(
            new GeometryWrite(itemId, new ItemGeometry(11, 22, 33, 44, 45), before.Version));
        Assert.True(outcome.Succeeded);

        var map = await PlantQueries.ItemGeometriesAsync();
        Assert.Equal(new ItemGeometry(11, 22, 33, 44, 45), map[itemId].Geometry);
    }
}
