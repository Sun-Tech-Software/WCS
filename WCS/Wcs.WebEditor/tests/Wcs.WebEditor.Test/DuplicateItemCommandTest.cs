using Wcs.Api.Client;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Editing;
using Wcs.WebEditor.Model;
using Xunit;

namespace Wcs.WebEditor.Test;

/// <summary><see cref="DuplicateItemCommand"/> in isolamento, contro un <see cref="WcsClient"/> vero appoggiato su <see cref="PlantMutationHandler"/>.</summary>
public sealed class DuplicateItemCommandTest
{
    private static WcsClient NewClient(PlantMutationHandler handler) =>
        new(new WcsClientOptions { BaseAddress = new Uri("http://localhost:5580") }, handler);

    /// <summary>
    /// Copia ogni proprieta' tranne <c>ItemId</c> (che diventa quella nuova) e riporta <c>Asi</c> a
    /// <c>"XXXX"</c> - la stessa convenzione di <c>Wcs.Editor.Model.ConfigCopier.Copy</c>, replicata
    /// indipendentemente perche' <c>Wcs.WebEditor</c> non referenzia il progetto WinForms.
    /// </summary>
    [Fact]
    public void CopyConfigWithNewIdCopiesEveryPropertyExceptItemIdAndResetsAsi()
    {
        var sourceId = Guid.NewGuid();
        var newId = Guid.NewGuid();
        var source = new PlantItemConfigDto
        {
            ItemId = sourceId,
            Name = "Nastro 1",
            Asi = "0100",
            MaxUnloadWeight = 900,
            StoragePartition = "P1",
        };

        var copy = DuplicateItemCommand.CopyConfigWithNewId(source, newId);

        Assert.Equal(newId, copy.ItemId);
        Assert.Equal("Nastro 1", copy.Name);
        Assert.Equal(900, copy.MaxUnloadWeight);
        Assert.Equal("P1", copy.StoragePartition);
        Assert.Equal("XXXX", copy.Asi); // non "0100": due dispositivi con lo stesso indirizzo fisico sarebbero un errore, non una comodita'
    }

    [Fact]
    public async Task ApplyCreatesANewItemWithTheOffsetGeometryAndRereadsIt()
    {
        var handler = new PlantMutationHandler();
        var client = NewClient(handler);
        var graph = new PlantGraph();
        var newId = Guid.NewGuid();
        var newConfig = new PlantItemConfigDto { ItemId = newId, Name = "Nastro 1" };
        var newGeometry = new PlantItemGeometryDto(newId, 150, 90, 100, 100, 0); // originale (100,40) + meta' cella (50,50)
        var command = new DuplicateItemCommand(newConfig, newGeometry, "Nastro 1");

        await command.ApplyAsync(client, graph);

        Assert.Equal(1, handler.ItemCreates); // la copia nasce dalla POST, non dalla PUT della configurazione
        Assert.Equal(0, handler.ConfigWrites);
        Assert.Equal(1, handler.GeometryWrites);
        var duplicated = graph.Items.Single(i => i.ItemId == newId);
        Assert.Equal(150, duplicated.X);
        Assert.Equal(90, duplicated.Y);
        Assert.Equal(handler.GeometryVersions[newId], duplicated.GeometryVersion);
    }

    /// <summary>Un originale non collocato (X/Y nulli): il duplicato non riceve una geometria inventata, resta anche lui non collocato.</summary>
    [Fact]
    public async Task ApplyWithoutGeometrySkipsTheGeometryWriteAndTheDuplicateStaysUnplaced()
    {
        var handler = new PlantMutationHandler();
        var client = NewClient(handler);
        var graph = new PlantGraph();
        var newId = Guid.NewGuid();
        var command = new DuplicateItemCommand(new PlantItemConfigDto { ItemId = newId }, null, "Nastro 1");

        await command.ApplyAsync(client, graph);

        Assert.Equal(0, handler.GeometryWrites);
        var duplicated = graph.Items.Single(i => i.ItemId == newId);
        Assert.Null(duplicated.X);
        Assert.Null(duplicated.Y);
    }

    [Fact]
    public async Task UndoDeletesOnlyTheDuplicateNotTheOriginal()
    {
        var handler = new PlantMutationHandler();
        var client = NewClient(handler);
        var graph = new PlantGraph();
        var originalId = Guid.NewGuid();
        await client.CreateItemAsync(new PlantItemConfigDto { ItemId = originalId });
        var original = await client.GetItemAsync(originalId) ?? throw new InvalidOperationException();
        await client.SetItemGeometryAsync(GeometryWriter.ToUpdate(
            new PlantItemGeometryDto(originalId, 100, 40, 100, 100, 0),
            original.GeometryVersion));
        graph.AddItem((await client.GetItemAsync(originalId))!);

        var newId = Guid.NewGuid();
        var command = new DuplicateItemCommand(
            new PlantItemConfigDto { ItemId = newId },
            new PlantItemGeometryDto(newId, 150, 90, 100, 100, 0),
            "Nastro 1");
        await command.ApplyAsync(client, graph);
        Assert.Equal(2, graph.Items.Count);

        await command.UndoAsync(client, graph);

        Assert.Equal(1, handler.ItemDeletes);
        var remaining = Assert.Single(graph.Items);
        Assert.Equal(originalId, remaining.ItemId); // l'originale e' ancora li'
    }
}
