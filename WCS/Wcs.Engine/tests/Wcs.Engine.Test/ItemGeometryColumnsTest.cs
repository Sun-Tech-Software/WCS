using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Xunit;

namespace Wcs.Engine.Test;

/// <summary>
/// Le cinque colonne della geometria: ci sono, sono annullabili, e un salvataggio di stato del
/// motore non le cancella.
/// </summary>
/// <remarks>
/// Il secondo test e' quello che conta. Il motore salva lo stato ricostruendo l'intera riga dal
/// dispositivo in memoria (PlantStore.Snapshot), che la geometria non ce l'ha: senza la guardia in
/// UpsertItemAsync ogni salvataggio la azzererebbe. Stessa forma del precedente in
/// MissionStore.cs:122-125.
/// </remarks>
public sealed class ItemGeometryColumnsTest : IDisposable
{
    private readonly WcsDatabase _database = WcsDatabase.InMemory();

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task GeometryIsWrittenAndReadBack()
    {
        var itemId = Guid.NewGuid();
        await using (var context = _database.CreateContext())
        {
            await PlantStore.UpsertItemAsync(context, new PlantItem { ItemId = itemId, Name = "IN01" });
            await context.SaveChangesAsync();
        }

        await using (var context = _database.CreateContext())
        {
            var row = await PlantStore.FindItemAsync(context, itemId);
            Assert.NotNull(row);
            Assert.Null(row!.X);
            Assert.Null(row.Y);
            Assert.Null(row.Width);
            Assert.Null(row.Height);
            Assert.Null(row.Rotation);
        }
    }

    [Fact]
    public async Task AStateSaveDoesNotWipeTheGeometry()
    {
        var itemId = Guid.NewGuid();

        await using (var context = _database.CreateContext())
        {
            context.PlantItems.Add(new PlantItem
            {
                ItemId = itemId, Name = "IN01",
                X = 120.5, Y = 340.25, Width = 80, Height = 20, Rotation = 37.5
            });
            await context.SaveChangesAsync();
        }

        // L'istantanea che arriva dal motore: la riga intera, con la geometria nulla perche' il
        // dispositivo in memoria non ce l'ha.
        await using (var context = _database.CreateContext())
        {
            await PlantStore.UpsertItemAsync(context, new PlantItem { ItemId = itemId, Name = "IN01", Status = 3 });
            await context.SaveChangesAsync();
        }

        await using (var verify = _database.CreateContext())
        {
            var row = await PlantStore.FindItemAsync(verify, itemId);
            Assert.NotNull(row);
            Assert.Equal(3, row!.Status);          // lo stato e' arrivato
            Assert.Equal(120.5, row.X);            // la geometria e' rimasta
            Assert.Equal(340.25, row.Y);
            Assert.Equal(80, row.Width);
            Assert.Equal(20, row.Height);
            Assert.Equal(37.5, row.Rotation);
        }
    }

    [Fact]
    public async Task ASecondUpsertOnTheSameContextDoesNotWipeTheGeometry()
    {
        var itemId = Guid.NewGuid();

        // Stessa istanza di WcsDbContext per tutt'e due le scritture, senza disporla in mezzo:
        // e' il caso dello scrittore a coda del motore, dove la seconda scrittura trova l'entita'
        // gia' tracciata e la guardia in UpsertItemAsync prende il ramo da ChangeTracker, non
        // quello dalla query.
        await using var context = _database.CreateContext();

        await PlantStore.UpsertItemAsync(context, new PlantItem
        {
            ItemId = itemId, Name = "IN01",
            X = 120.5, Y = 340.25, Width = 80, Height = 20, Rotation = 37.5
        });
        await context.SaveChangesAsync();

        // L'istantanea che arriva dal motore: la riga intera, con la geometria nulla perche' il
        // dispositivo in memoria non ce l'ha.
        await PlantStore.UpsertItemAsync(context, new PlantItem { ItemId = itemId, Name = "IN01", Status = 3 });
        await context.SaveChangesAsync();

        await using (var verify = _database.CreateContext())
        {
            var row = await PlantStore.FindItemAsync(verify, itemId);
            Assert.NotNull(row);
            Assert.Equal(3, row!.Status);          // lo stato e' arrivato
            Assert.Equal(120.5, row.X);            // la geometria e' rimasta, in tutt'e due le istantanee
            Assert.Equal(340.25, row.Y);
            Assert.Equal(80, row.Width);
            Assert.Equal(20, row.Height);
            Assert.Equal(37.5, row.Rotation);
        }
    }
}
