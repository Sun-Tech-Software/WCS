using System.Net;
using Wcs.Api.Client;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Editing;
using Wcs.WebEditor.Model;
using Xunit;

namespace Wcs.WebEditor.Test;

/// <summary>
/// <see cref="ClearDoorMappingsCommand"/> in isolamento (compito 11): la DELETE diretta, distinta da
/// <see cref="ReplaceDoorMappingsCommand"/> con un elenco vuoto - si veda il remarks della classe.
/// </summary>
public sealed class ClearDoorMappingsCommandTest
{
    private static WcsClient NewClient(PlantMutationHandler handler) =>
        new(new WcsClientOptions { BaseAddress = new Uri("http://localhost:5580") }, handler);

    [Fact]
    public void ConstructingWithAMismatchedItemIdThrows()
    {
        var itemId = Guid.NewGuid();
        var badMapping = new DoorMappingDto(0, Guid.NewGuid(), "A1", 1, "A", 0, false);

        Assert.Throws<ArgumentException>(() => new ClearDoorMappingsCommand(itemId, [badMapping]));
    }

    [Fact]
    public async Task ApplyCallsDeleteNotPut()
    {
        var handler = new PlantMutationHandler();
        var client = NewClient(handler);
        var itemId = Guid.NewGuid();
        var otherItemId = Guid.NewGuid();
        // I due dispositivi esistono: le rotte della mappa porte rispondono 404 su un dispositivo
        // che non c'e', e una riga su un dispositivo assente e' una riga orfana.
        handler.SeedItems(itemId, otherItemId);
        var oldMapping = new DoorMappingDto(0, itemId, "A1", 1, "A", 0, false);
        var otherMapping = new DoorMappingDto(0, otherItemId, "A9", 9, "A", 0, false);
        handler.DoorMappings.Add(oldMapping);
        handler.DoorMappings.Add(otherMapping);
        var graph = new PlantGraph([], [], []);

        var command = new ClearDoorMappingsCommand(itemId, [oldMapping]);
        await command.ApplyAsync(client, graph);

        Assert.Equal(1, handler.DoorMappingDeletes);
        Assert.Equal(0, handler.DoorMappingPuts);
        Assert.DoesNotContain(handler.DoorMappings, m => m.ItemId == itemId);
        Assert.Contains(handler.DoorMappings, m => m.ItemId == otherItemId); // l'altro dispositivo non e' toccato
    }

    [Fact]
    public async Task UndoWritesBackTheMappingsCapturedBeforeClearing()
    {
        var handler = new PlantMutationHandler();
        var client = NewClient(handler);
        var itemId = Guid.NewGuid();
        handler.SeedItems(itemId);
        var oldMapping = new DoorMappingDto(0, itemId, "A1", 1, "A", 0, false);
        handler.DoorMappings.Add(oldMapping);
        var graph = new PlantGraph([], [], []);

        var command = new ClearDoorMappingsCommand(itemId, [oldMapping]);
        await command.ApplyAsync(client, graph);
        Assert.Empty(handler.DoorMappings);

        await command.UndoAsync(client, graph);

        Assert.Equal(1, handler.DoorMappingPuts);
        var restored = Assert.Single(handler.DoorMappings, m => m.ItemId == itemId);
        Assert.Equal(1, restored.CellId);
    }

    [Fact]
    public async Task WhenTheDeleteIsRejectedApplyThrowsAndNothingChanges()
    {
        var handler = new PlantMutationHandler();
        var client = NewClient(handler);
        var itemId = Guid.NewGuid();
        handler.SeedItems(itemId);
        var oldMapping = new DoorMappingDto(0, itemId, "A1", 1, "A", 0, false);
        handler.DoorMappings.Add(oldMapping);
        var graph = new PlantGraph([], [], []);

        handler.FailNext(PlantMutationHandler.Route.DoorMappingsDelete, HttpStatusCode.Locked);
        var command = new ClearDoorMappingsCommand(itemId, [oldMapping]);

        var ex = await Assert.ThrowsAsync<WcsApiException>(() => command.ApplyAsync(client, graph));
        Assert.Equal(HttpStatusCode.Locked, ex.StatusCode);
        Assert.Contains(handler.DoorMappings, m => m.ItemId == itemId);
    }
}
