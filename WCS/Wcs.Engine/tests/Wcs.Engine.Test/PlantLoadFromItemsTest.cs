using Wcs.Engine.Collections;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// Voce D161: l'impianto in memoria nasce da <c>PlantItems</c>, non dai soli estremi delle
/// giunzioni, e il caricamento non cancella nulla dallo store.
/// </summary>
/// <remarks>
/// Prima <c>BuildNavigatorItemsJunctions</c> costruiva i dispositivi dagli estremi delle
/// giunzioni e poi <c>ClearUnUsed</c> toglieva da <c>PlantItems</c> ogni riga che non era in
/// memoria: un dispositivo configurato e non ancora collegato perdeva la configurazione al primo
/// riavvio, in silenzio, e l'Editor lo ricreava vuoto. La cancellazione resta alla sola via che
/// la fa confermare all'operatore (<c>POST /plant/items/keep</c>).
/// </remarks>
public sealed class PlantLoadFromItemsTest : IDisposable
{
    private readonly NavigatorStatusTypes _previousStatus = Navigator.NavStatus;
    private readonly WcsDatabase _store = WcsDatabase.InMemory();

    public PlantLoadFromItemsTest()
    {
        Navigator.Store = _store;
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
    }

    public void Dispose()
    {
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        PathDevices.Instance.Clear();
        PathJunctions.Instance.Clear();
        Navigator.NavStatus = _previousStatus;
        Navigator.Store = null;
        _store.Dispose();
    }

    /// <summary>
    /// Tre righe, una giunzione fra due: dopo il caricamento i tre dispositivi sono in memoria e
    /// la riga del terzo, che non e' estremo di nessuna giunzione, e' ancora sullo store.
    /// </summary>
    [Fact]
    public async Task ADeviceWithoutJunctionsIsLoadedAndKeptOnTheStore()
    {
        var source = Guid.NewGuid();
        var destination = Guid.NewGuid();
        var alone = Guid.NewGuid();
        await using (var context = _store.CreateContext())
        {
            context.PlantItems.AddRange(
                new PlantItem { ItemId = source, Name = "sorgente" },
                new PlantItem { ItemId = destination, Name = "destinazione" },
                new PlantItem { ItemId = alone, Name = "non ancora collegato", DelayBlind = 7 });
            context.PlantJunctions.Add(new PlantJunction
                { JunctionId = Guid.NewGuid(), SourceItemId = source, DestinationItemId = destination });
            await context.SaveChangesAsync();
        }

        await Navigator.BuildNavigatorItemsJunctions();

        Assert.NotNull(PathDevices.Instance[source]);
        Assert.NotNull(PathDevices.Instance[destination]);
        // Prima: non in memoria, e cancellato dallo store.
        var loaded = PathDevices.Instance[alone];
        Assert.NotNull(loaded);
        Assert.Equal(7, loaded.DelayBlind);
        await using (var context = _store.CreateContext())
            Assert.Contains(alone, await PlantStore.LoadItemIdsAsync(context));
    }

    /// <summary>
    /// La meta' che deve restare: gli estremi delle giunzioni continuano a entrare in memoria, e
    /// il carico di una riga configurata arriva sul dispositivo.
    /// </summary>
    [Fact]
    public async Task JunctionEndpointsStillBecomeDevices()
    {
        var source = Guid.NewGuid();
        var destination = Guid.NewGuid();
        await using (var context = _store.CreateContext())
        {
            context.PlantItems.Add(new PlantItem { ItemId = source, Name = "sorgente", Asi = "0100" });
            context.PlantJunctions.Add(new PlantJunction
                { JunctionId = Guid.NewGuid(), SourceItemId = source, DestinationItemId = destination });
            await context.SaveChangesAsync();
        }

        await Navigator.BuildNavigatorItemsJunctions();

        Assert.Equal("0100", PathDevices.Instance[source]?.ASI);
        Assert.NotNull(PathDevices.Instance[destination]);
        Assert.Single(PathJunctions.Instance.List);
    }
}
