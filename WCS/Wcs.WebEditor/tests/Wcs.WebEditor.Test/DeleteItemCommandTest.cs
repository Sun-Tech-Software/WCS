using System.Net;
using Wcs.Api.Client;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Editing;
using Wcs.WebEditor.Model;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

/// <summary><see cref="DeleteItemCommand"/> in isolamento, contro un <see cref="WcsClient"/> vero appoggiato su <see cref="PlantMutationHandler"/>.</summary>
public sealed class DeleteItemCommandTest
{
    private static WcsClient NewClient(PlantMutationHandler handler) =>
        new(new WcsClientOptions { BaseAddress = new Uri("http://localhost:5580") }, handler);

    /// <summary>Colloca un dispositivo gia' esistente nel gestore finto - come se lo avesse creato un gesto precedente - cosi' i test di questa classe possano partire da "il dispositivo c'e' gia'".</summary>
    private static async Task SeedAsync(WcsClient client, Guid id, PlantItemConfigDto config, PlantItemGeometryDto geometry)
    {
        await client.CreateItemAsync(config);
        var created = await client.GetItemAsync(id) ?? throw new InvalidOperationException();
        await client.SetItemGeometryAsync(GeometryWriter.ToUpdate(geometry, created.GeometryVersion));
    }

    /// <summary>Premessa 2 del compito, verificata contro il codice: cancellare porta via anche le giunzioni che toccano il dispositivo, non solo la riga del dispositivo.</summary>
    [Fact]
    public async Task ApplyDeletesTheItemAndRemovesItAndItsJunctionsFromTheGraph()
    {
        var handler = new PlantMutationHandler();
        var client = NewClient(handler);
        var id = Guid.NewGuid();
        var other = Guid.NewGuid();
        var config = new PlantItemConfigDto { ItemId = id };
        var geometry = new PlantItemGeometryDto(id, 0, 0, 100, 100, 0);
        await SeedAsync(client, id, config, geometry);

        var touching = new JunctionDto(Guid.NewGuid(), id, other, 1);
        var untouched = new JunctionDto(Guid.NewGuid(), other, Guid.NewGuid(), 1);
        var graph = new PlantGraph([await client.GetItemAsync(id) ?? throw new InvalidOperationException()], [touching, untouched], []);

        var command = new DeleteItemCommand(config, geometry, [touching]);
        await command.ApplyAsync(client, graph);

        Assert.Equal(1, handler.ItemDeletes);
        Assert.Empty(graph.Items);
        Assert.DoesNotContain(graph.Junctions, j => j.JunctionId == touching.JunctionId);
        Assert.Contains(graph.Junctions, j => j.JunctionId == untouched.JunctionId); // non toccata: non tocca l'altro dispositivo
    }

    /// <summary>Premessa 3 del compito: l'annulla rimette la FORMA del grafo (stessi estremi, stessa priorita'), non l'identita' - JunctionRequest non porta un identificativo, quindi la giunzione ricreata ha un JunctionId nuovo.</summary>
    [Fact]
    public async Task UndoRecreatesTheDeviceAndItsJunctionsWithNewJunctionIdsNotTheOldOnes()
    {
        var handler = new PlantMutationHandler();
        var client = NewClient(handler);
        var id = Guid.NewGuid();
        var other = Guid.NewGuid();
        var config = new PlantItemConfigDto { ItemId = id, Name = "Nastro" };
        var geometry = new PlantItemGeometryDto(id, 130, 70, 100, 100, 0);
        await SeedAsync(client, id, config, geometry);
        await SeedAsync(client, other, new PlantItemConfigDto { ItemId = other }, new PlantItemGeometryDto(other, 500, 70, 100, 100, 0));

        var oldJunction = new JunctionDto(Guid.NewGuid(), id, other, Priority: 3);
        var graph = new PlantGraph(
            [(await client.GetItemAsync(id))!, (await client.GetItemAsync(other))!],
            [oldJunction], []);

        var command = new DeleteItemCommand(config, geometry, [oldJunction]);
        await command.ApplyAsync(client, graph);
        Assert.DoesNotContain(graph.Items, i => i.ItemId == id);

        await command.UndoAsync(client, graph);

        var recreatedItem = graph.Items.Single(i => i.ItemId == id);
        Assert.Equal(130, recreatedItem.X);
        Assert.Equal(70, recreatedItem.Y);
        Assert.Equal("Nastro", recreatedItem.Name);
        Assert.Equal(handler.GeometryVersions[id], recreatedItem.GeometryVersion);

        var recreatedJunction = Assert.Single(graph.Junctions);
        Assert.Equal(id, recreatedJunction.SourceItemId);
        Assert.Equal(other, recreatedJunction.DestinationItemId);
        Assert.Equal(3, recreatedJunction.Priority);
        Assert.NotEqual(oldJunction.JunctionId, recreatedJunction.JunctionId); // dichiarato, non nascosto
    }

    /// <summary>
    /// Decisione del compito (remarks di <see cref="DeleteItemCommand"/>): un annulla interrotto a
    /// meta' - qui simulato direttamente sullo stato del grafo, invece di inseguire l'ordine esatto
    /// delle chiamate HTTP di un primo tentativo fallito - deve poter essere ritentato senza
    /// duplicare cio' che e' gia' stato rimesso: ne' il dispositivo (gia' presente nel grafo) ne',
    /// soprattutto, la giunzione gia' ripristinata (una seconda POST la duplicherebbe davvero, non
    /// essendoci un identificativo da confrontare - premessa 3 del compito).
    /// </summary>
    [Fact]
    public async Task ARetriedUndoSkipsWhatAnEarlierPartialAttemptAlreadyRestored()
    {
        var handler = new PlantMutationHandler();
        var client = NewClient(handler);
        var id = Guid.NewGuid();
        var otherA = Guid.NewGuid();
        var otherB = Guid.NewGuid();
        var config = new PlantItemConfigDto { ItemId = id };
        var geometry = new PlantItemGeometryDto(id, 0, 0, 100, 100, 0);
        await SeedAsync(client, id, config, geometry);
        await SeedAsync(client, otherA, new PlantItemConfigDto { ItemId = otherA }, new PlantItemGeometryDto(otherA, 300, 0, 100, 100, 0));
        await SeedAsync(client, otherB, new PlantItemConfigDto { ItemId = otherB }, new PlantItemGeometryDto(otherB, 0, 300, 100, 100, 0));

        var junctionA = new JunctionDto(Guid.NewGuid(), id, otherA, 1);
        var junctionB = new JunctionDto(Guid.NewGuid(), id, otherB, 2);
        var command = new DeleteItemCommand(config, geometry, [junctionA, junctionB]);

        // Lo stato dopo un primo tentativo di annullamento interrotto a meta': il dispositivo e'
        // gia' stato ricreato, la giunzione verso otherA pure (con un JunctionId nuovo, come farebbe
        // davvero una POST riuscita), ma quella verso otherB no - il tentativo si e' fermato li'.
        var alreadyRestoredJunctionA = new JunctionDto(Guid.NewGuid(), id, otherA, 1);
        var graph = new PlantGraph([(await client.GetItemAsync(id))!], [alreadyRestoredJunctionA], []);
        var itemCreatesBeforeUndo = handler.ItemCreates;
        var geometryWritesBeforeUndo = handler.GeometryWrites;

        await command.UndoAsync(client, graph);

        // Il dispositivo non e' stato ricreato una seconda volta: era gia' nel grafo. E adesso la
        // guardia non e' piu' solo un risparmio: una seconda POST su un identificativo gia' preso
        // risponderebbe 409, e l'annulla si fermerebbe (DeleteItemCommand.CreateAsync).
        Assert.Equal(itemCreatesBeforeUndo, handler.ItemCreates);
        Assert.Equal(geometryWritesBeforeUndo, handler.GeometryWrites);

        // Una sola POST di giunzione - solo per otherB, quella davvero mancante.
        Assert.Equal(1, handler.JunctionAdds);
        Assert.Equal(2, graph.Junctions.Count); // le due, non tre: nessun duplicato per otherA
        Assert.Contains(graph.Junctions, j => j.JunctionId == alreadyRestoredJunctionA.JunctionId); // quella di prima, intatta
        Assert.Contains(graph.Junctions, j => j.DestinationItemId == otherB && j.JunctionId != alreadyRestoredJunctionA.JunctionId);
    }

    /// <summary>
    /// <see cref="PlantGraph.AddJunction"/> deduplica per <see cref="JunctionDto.JunctionId"/>
    /// come gia' fa <see cref="PlantGraph.AddItem"/> per <see cref="PlantItemDto.ItemId"/>. E' la
    /// meta' locale del meccanismo: un impianto migrato dalla tabella legacy puo' portare due
    /// giunzioni sulla stessa coppia (origine, destinazione) con priorita' diverse, e l'host - che
    /// collassa sulla coppia - restituirebbe la stessa giunzione esistente a una seconda POST;
    /// senza questa deduplica <c>DeleteItemCommand.UndoAsync</c> la infilerebbe due volte nel grafo
    /// locale con lo stesso <c>JunctionId</c>.
    /// </summary>
    [Fact]
    public void AddJunctionReplacesAnExistingJunctionWithTheSameIdInsteadOfDuplicatingIt()
    {
        var graph = new PlantGraph();
        var sharedId = Guid.NewGuid();
        var first = new JunctionDto(sharedId, Guid.NewGuid(), Guid.NewGuid(), Priority: 1);
        var second = new JunctionDto(sharedId, first.SourceItemId, first.DestinationItemId, Priority: 1); // stesso JunctionId, come lo restituirebbe l'host su una POST ripetuta

        graph.AddJunction(first);
        graph.AddJunction(second);

        var only = Assert.Single(graph.Junctions);
        Assert.Same(second, only); // l'ultima vince, come per AddItem
    }
}
