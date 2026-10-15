using Microsoft.EntityFrameworkCore;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Xunit;

namespace Wcs.Engine.Test;

/// <summary>
/// Lo store in memoria dei test deve reggere quello che regge il file in esercizio: piu'
/// contesti aperti nello stesso momento, da thread diversi.
/// </summary>
/// <remarks>
/// In esercizio ogni contesto apre la propria connessione al file, e SQLite mette in attesa chi
/// trova il database occupato. Lo store in memoria era invece una sola connessione condivisa da
/// tutti i contesti: due contesti che ci lavoravano insieme la facevano cadere («cannot start a
/// transaction within a transaction», righe lette a meta'), e nella suite dell'host questo
/// arrivava come un 500 a intermittenza, perche' i cicli di sfondo del motore scrivono sullo
/// store mentre i test fanno le loro richieste.
/// </remarks>
public sealed class WcsDatabaseTest
{
    [Fact]
    public async Task TheInMemoryStoreServesConcurrentContextsLikeTheFileStore()
    {
        using var database = WcsDatabase.InMemory();
        const int writers = 8;
        const int rowsPerWriter = 25;

        // Ogni scrittore apre un contesto per riga, come fanno le facciate e gli scrittori del
        // motore, e nel frattempo un lettore rilegge la tabella: e' la forma della collisione
        // vista nella suite dell'host (una scrittura di sfondo dentro una richiesta che legge).
        var tasks = Enumerable.Range(0, writers).Select(writer => Task.Run(async () =>
        {
            for (var i = 0; i < rowsPerWriter; i++)
            {
                await using (var context = database.CreateContext())
                {
                    context.PlantItems.Add(new PlantItem { ItemId = Guid.NewGuid(), Name = $"W{writer}-{i}" });
                    await context.SaveChangesAsync();
                }

                await using (var context = database.CreateContext())
                    await context.PlantItems.AsNoTracking().CountAsync();
            }
        })).ToList();

        await Task.WhenAll(tasks);

        await using var check = database.CreateContext();
        Assert.Equal(writers * rowsPerWriter, await check.PlantItems.CountAsync());
    }

    [Fact]
    public async Task DisposingTheInMemoryStoreDropsTheDatabase()
    {
        // La connessione che tiene in vita il database si chiude con lo store: un secondo store
        // non deve trovare le righe del primo, e i test che ne aprono uno per classe restano
        // isolati l'uno dall'altro.
        var first = WcsDatabase.InMemory();
        await using (var context = first.CreateContext())
        {
            context.PlantItems.Add(new PlantItem { ItemId = Guid.NewGuid(), Name = "del primo" });
            await context.SaveChangesAsync();
        }
        first.Dispose();

        using var second = WcsDatabase.InMemory();
        await using var other = second.CreateContext();
        Assert.Equal(0, await other.PlantItems.CountAsync());
    }
}
