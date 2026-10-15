using System.Net;
using Wcs.Api.Client;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Editing;
using Wcs.WebEditor.Model;
using Xunit;

namespace Wcs.WebEditor.Test;

/// <summary>
/// <see cref="ReplaceDoorMappingsCommand"/> in isolamento (compito 11), contro un
/// <see cref="WcsClient"/> vero appoggiato su <see cref="PlantMutationHandler"/> - come
/// <c>EditItemConfigCommandTest</c>. Il cablaggio con <c>DoorMappings.razor</c> e il pannello e'
/// provato a parte in <c>PlantCanvasCellsPanelTest</c>.
/// </summary>
public sealed class ReplaceDoorMappingsCommandTest
{
    private static WcsClient NewClient(PlantMutationHandler handler) =>
        new(new WcsClientOptions { BaseAddress = new Uri("http://localhost:5580") }, handler);

    [Fact]
    public void ConstructingWithMismatchedItemIdsThrows()
    {
        var itemId = Guid.NewGuid();
        var wrongItemId = Guid.NewGuid();
        var badMapping = new DoorMappingDto(0, wrongItemId, "A1", 1, "A", 0, false);

        Assert.Throws<ArgumentException>(() => new ReplaceDoorMappingsCommand(itemId, [badMapping], []));
        Assert.Throws<ArgumentException>(() => new ReplaceDoorMappingsCommand(itemId, [], [badMapping]));
    }

    /// <summary>
    /// La trappola del compito: sostituire NON significa aggiungere. Il dispositivo aveva gia' una
    /// riga (cella 1); il comando la sostituisce con due righe nuove (celle 2 e 3) - dopo Apply la
    /// cella 1 non c'e' piu', non resta accanto alle due nuove.
    /// </summary>
    [Fact]
    public async Task ApplyReplacesEveryRowOfTheItemNotJustAddsToThem()
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

        var newMappings = new List<DoorMappingDto>
        {
            new(0, itemId, "A2", 2, "A", 0, false),
            new(0, itemId, "B2", 2, "B", 0, false),
        };
        var command = new ReplaceDoorMappingsCommand(itemId, [oldMapping], newMappings);

        await command.ApplyAsync(client, graph);

        Assert.Equal(1, handler.DoorMappingPuts);
        Assert.Equal(0, handler.DoorMappingDeletes);
        var forItem = handler.DoorMappings.Where(m => m.ItemId == itemId).ToList();
        Assert.Equal(2, forItem.Count);
        Assert.DoesNotContain(forItem, m => m.CellId == 1); // la riga di prima non c'e' piu'
        Assert.Contains(forItem, m => m.CellId == 2 && m.Side == "A");
        Assert.Contains(forItem, m => m.CellId == 2 && m.Side == "B");
        // Il dispositivo ASSENTE dal corpo (otherItemId) resta come sta: ReplaceDoorMappingsAsync
        // raggruppa per ItemId e sostituisce solo chi e' presente nell'elenco.
        Assert.Contains(handler.DoorMappings, m => m.ItemId == otherItemId);
    }

    /// <summary>
    /// La trappola gemella: un insieme desiderato VUOTO non deve tradursi in una PUT con corpo vuoto
    /// (che sarebbe un no-op silenzioso sull'host vero, si veda il remarks di DoorMappingWrites) -
    /// deve passare dalla DELETE.
    /// </summary>
    [Fact]
    public async Task ApplyWithAnEmptyDesiredSetClearsThroughDeleteNotThroughAnEmptyPut()
    {
        var handler = new PlantMutationHandler();
        var client = NewClient(handler);
        var itemId = Guid.NewGuid();
        handler.SeedItems(itemId);
        var oldMapping = new DoorMappingDto(0, itemId, "A1", 1, "A", 0, false);
        handler.DoorMappings.Add(oldMapping);
        var graph = new PlantGraph([], [], []);

        var command = new ReplaceDoorMappingsCommand(itemId, [oldMapping], []);
        await command.ApplyAsync(client, graph);

        Assert.Equal(0, handler.DoorMappingPuts);
        Assert.Equal(1, handler.DoorMappingDeletes);
        Assert.DoesNotContain(handler.DoorMappings, m => m.ItemId == itemId);
    }

    [Fact]
    public async Task UndoWritesBackTheMappingsCapturedBeforeTheEdit()
    {
        var handler = new PlantMutationHandler();
        var client = NewClient(handler);
        var itemId = Guid.NewGuid();
        handler.SeedItems(itemId);
        var oldMapping = new DoorMappingDto(0, itemId, "A1", 1, "A", 0, false);
        handler.DoorMappings.Add(oldMapping);
        var graph = new PlantGraph([], [], []);

        var newMappings = new List<DoorMappingDto> { new(0, itemId, "A2", 2, "A", 0, false) };
        var command = new ReplaceDoorMappingsCommand(itemId, [oldMapping], newMappings);
        await command.ApplyAsync(client, graph);

        await command.UndoAsync(client, graph);

        Assert.Equal(2, handler.DoorMappingPuts);
        var restored = Assert.Single(handler.DoorMappings, m => m.ItemId == itemId);
        Assert.Equal(1, restored.CellId);
        Assert.Equal("A1", restored.DoorCode);
    }

    /// <summary>L'annulla di un collegamento nato da niente (OldMappings vuoto) deve tornare a niente: passa dalla DELETE, non da una PUT vuota - la stessa trappola di ApplyWithAnEmptyDesiredSetClearsThroughDeleteNotThroughAnEmptyPut, sul lato dell'annulla.</summary>
    [Fact]
    public async Task UndoBackToAnEmptySetClearsThroughDeleteNotThroughAnEmptyPut()
    {
        var handler = new PlantMutationHandler();
        var client = NewClient(handler);
        var itemId = Guid.NewGuid();
        handler.SeedItems(itemId);
        var graph = new PlantGraph([], [], []);

        var newMappings = new List<DoorMappingDto> { new(0, itemId, "A2", 2, "A", 0, false) };
        var command = new ReplaceDoorMappingsCommand(itemId, [], newMappings);
        await command.ApplyAsync(client, graph);
        Assert.Contains(handler.DoorMappings, m => m.ItemId == itemId);

        await command.UndoAsync(client, graph);

        Assert.Equal(1, handler.DoorMappingDeletes);
        Assert.DoesNotContain(handler.DoorMappings, m => m.ItemId == itemId);
    }

    /// <summary>
    /// 423 (il permesso di modifica non e' di questa sessione): la scrittura solleva WcsApiException, la
    /// mappa non cambia sull'host. <b>Il 423 e' un esito fra gli altri, non l'unico:</b> questo
    /// commento diceva che quella rotta non puo' mai rispondere 409 perche'
    /// <c>ReplaceDoorMappingsAsync</c> non chiamerebbe <c>RequireNotRunning</c> - la chiama
    /// (<c>PlantQueries.cs</c>), da quando l'impianto e' in sola lettura a motore in marcia, e da
    /// questo lotto la stessa rotta risponde anche <c>404</c> sul dispositivo che non c'e'. Quello
    /// che questo test prova e' il 423; gli altri due stanno nella suite dell'host, che e' dove
    /// vivono i codici di stato veri.
    /// </summary>
    [Fact]
    public async Task WhenTheWriteIsRejectedApplyThrowsAndNothingIsWritten()
    {
        var handler = new PlantMutationHandler();
        var client = NewClient(handler);
        var itemId = Guid.NewGuid();
        var graph = new PlantGraph([], [], []);

        handler.FailNext(PlantMutationHandler.Route.DoorMappingsPut, HttpStatusCode.Locked);
        var newMappings = new List<DoorMappingDto> { new(0, itemId, "A2", 2, "A", 0, false) };
        var command = new ReplaceDoorMappingsCommand(itemId, [], newMappings);

        var ex = await Assert.ThrowsAsync<WcsApiException>(() => command.ApplyAsync(client, graph));
        Assert.Equal(HttpStatusCode.Locked, ex.StatusCode);
        Assert.Empty(handler.DoorMappings);
    }
}
