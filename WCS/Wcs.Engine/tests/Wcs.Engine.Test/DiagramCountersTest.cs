using Microsoft.EntityFrameworkCore;
using Wcs.Engine.ORM;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Xunit;

namespace Wcs.Engine.Test;

/// <summary>
/// I due contatori del diagramma si scrivono senza portarsi dietro la riga che li ospita.
/// </summary>
/// <remarks>
/// <c>DiagramXml</c> nel collaudo e' di 27,7 MB, e <c>SaveCountersAsync</c> la caricava intera una
/// volta al secondo per cambiare due <c>int</c>. In piu' <c>rnd.Next(1, 9999)</c> cambiava larghezza
/// ogni volta che il valore attraversava 127 (1 byte contro 2): a ogni cambio di larghezza SQLite
/// riscrive il record intero, 36 MB nella <c>-wal</c>, in circa il 2,5 % dei secondi.
/// </remarks>
public sealed class DiagramCountersTest : IDisposable
{
    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly QueuedDatabaseWriter<WcsWrite>? _previousStateWriter = Navigator.StateWriter;

    public DiagramCountersTest()
    {
        Navigator.Store = _database;
        Navigator.StateWriter = null;
    }

    public void Dispose()
    {
        Navigator.StateWriter = _previousStateWriter;
        Navigator.Store = null;
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// La riga del diagramma non viene materializzata: il contesto che esegue la scrittura non deve
    /// avere nessun <see cref="PlantDiagram"/> tracciato, altrimenti il <c>byte[]</c> del modello e'
    /// stato letto per intero. E il modello resta quello di prima.
    /// </summary>
    [Fact]
    public async Task SavingTheCountersDoesNotMaterialiseTheDiagramRow()
    {
        var xml = new byte[1024 * 1024];
        Random.Shared.NextBytes(xml);
        await PlantPersistence.SaveDiagramAsync([1, 2, 3], xml);

        // Lo scrittore di stato consegna la scrittura al lotto: qui il lotto e' eseguito su un
        // contesto che il test tiene in mano, cosi' si puo' guardare che cosa e' finito tracciato.
        var tracked = -1;
        var writer = new QueuedDatabaseWriter<WcsWrite>(async (batch, cancellationToken) =>
        {
            await using var context = _database.CreateContext();
            foreach (var write in batch) await write(context);
            await context.SaveChangesAsync(cancellationToken);
            tracked = context.ChangeTracker.Entries<PlantDiagram>().Count();
        });
        Navigator.StateWriter = writer;

        await PlantPersistence.SaveCountersAsync(1_073_741_900, 1_073_741_901);
        await writer.DisposeAsync();
        Navigator.StateWriter = null;

        Assert.Equal(0, tracked);

        await using var reading = _database.CreateContext();
        var row = Assert.Single(reading.PlantDiagrams);
        Assert.Equal(1_073_741_900, row.CheckCounter);
        Assert.Equal(1_073_741_901, row.StatusCounter);
        Assert.Equal(xml, row.DiagramXml);
    }

    /// <summary>Se la riga non c'e' ancora, i contatori la creano: e' il primo giro del motore su un database nuovo.</summary>
    [Fact]
    public async Task OnAnEmptyStoreTheCountersCreateTheRow()
    {
        await PlantPersistence.SaveCountersAsync(1_073_741_902, null);

        await using var reading = _database.CreateContext();
        var row = Assert.Single(reading.PlantDiagrams);
        Assert.Equal(1_073_741_902, row.CheckCounter);
        Assert.Equal(0, row.StatusCounter);
    }

    /// <summary>
    /// Il valore scritto e' sempre a quattro byte: e' quello che impedisce a SQLite di riscrivere
    /// il record - e con esso i 27,7 MB del modello - a ogni cambio di larghezza.
    /// </summary>
    [Fact]
    public async Task TheCounterIsAlwaysFourBytesWide()
    {
        for (var round = 0; round < 5; round++)
        {
            await DbInfo.UpdateNavigatorStatusCounters(updateStatus: true);

            await using var reading = _database.CreateContext();
            var row = Assert.Single(reading.PlantDiagrams);
            // Un intero >= 2^30 occupa sempre quattro byte nel serial type di SQLite.
            Assert.True(row.CheckCounter >= 1 << 30, $"CheckCounter {row.CheckCounter} non e' a quattro byte");
            Assert.True(row.StatusCounter >= 1 << 30, $"StatusCounter {row.StatusCounter} non e' a quattro byte");
        }
    }
}
