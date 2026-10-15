using System.Net;
using Wcs.Api.Client;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Editing;
using Wcs.WebEditor.Model;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

/// <summary>
/// <see cref="LinkBufferCellCommand"/> in isolamento (compito 11): le due scritture (<c>LinkedCellId</c>,
/// poi la mappa porte - in quest'ordine perche' solo la prima puo' rispondere 409 col motore in Run, si
/// veda il remarks della classe) e la rilettura del dispositivo - come <c>EditItemConfigCommandTest</c> e
/// <c>CreateItemCommandTest</c> per i comandi a due chiamate. Il cablaggio con <c>LinkedCells.razor</c>
/// e il pannello e' provato a parte in <c>PlantCanvasCellsPanelTest</c>.
/// </summary>
public sealed class LinkBufferCellCommandTest
{
    private static WcsClient NewClient(PlantMutationHandler handler) =>
        new(new WcsClientOptions { BaseAddress = new Uri("http://localhost:5580") }, handler);

    private static PlantItemDto Item(Guid id) => new(
        id, "prima", DeviceTypes.Automation, null, null, null, default, default, 0, null, 0, 0, 0, 0, default!);

    [Fact]
    public void ConstructingWithMismatchedItemIdsThrows()
    {
        var id = Guid.NewGuid();
        var oldConfig = new PlantItemConfigDto { ItemId = id };
        var newConfig = new PlantItemConfigDto { ItemId = Guid.NewGuid() };
        var link = new DoorMappingLink(id, "A1", 1);

        Assert.Throws<ArgumentException>(() => new LinkBufferCellCommand(oldConfig, newConfig, [], link));
    }

    [Fact]
    public void ConstructingWithMappingsOfAnotherItemThrows()
    {
        var id = Guid.NewGuid();
        var config = new PlantItemConfigDto { ItemId = id };
        var link = new DoorMappingLink(id, "A1", 1);
        var foreignMapping = new DoorMappingDto(0, Guid.NewGuid(), "A9", 9, "A", 0, false);

        Assert.Throws<ArgumentException>(() => new LinkBufferCellCommand(config, config, [foreignMapping], link));
    }

    /// <summary>
    /// La ragione d'essere del comando (si veda il suo remarks): il collegamento non e' solo la riga
    /// della mappa porte, <c>LinkedCellId</c> deve muoversi insieme a lei - senza la seconda scrittura
    /// il motore instraderebbe ancora verso la cella di prima.
    /// </summary>
    [Fact]
    public async Task ApplyWritesBothTheDoorMappingAndLinkedCellIdThenRereadsTheItem()
    {
        var handler = new PlantMutationHandler();
        var client = NewClient(handler);
        var id = Guid.NewGuid();
        var oldConfig = new PlantItemConfigDto { ItemId = id, Name = "prima", DeviceType = (short)DeviceTypes.Automation, LinkedCellId = 0m };
        handler.Configs[id] = oldConfig;
        var graph = new PlantGraph([Item(id)], [], []);

        var newConfig = new PlantItemConfigDto { ItemId = id, Name = "prima", DeviceType = (short)DeviceTypes.Automation, LinkedCellId = 5m };
        var link = new DoorMappingLink(id, "A5", 5m);
        var command = new LinkBufferCellCommand(oldConfig, newConfig, [], link);

        await command.ApplyAsync(client, graph);

        Assert.Equal(1, handler.DoorMappingLinks);
        Assert.Equal(1, handler.ConfigWrites);
        Assert.Equal(1, handler.ItemGets);
        Assert.Equal(5m, handler.Configs[id].LinkedCellId);
        var mapping = Assert.Single(handler.DoorMappings, m => m.ItemId == id);
        Assert.Equal("A5", mapping.DoorCode);
        Assert.Equal(5m, mapping.CellId);
        Assert.Equal(5m, graph.Items.Single(i => i.ItemId == id).LinkedCellId); // il grafo locale rilegge dall'host, non inventa
    }

    /// <summary>L'annulla rimette ENTRAMBI i dati di prima: la mappa porte (qui, vuota - il dispositivo non era ancora collegato) e LinkedCellId a zero.</summary>
    [Fact]
    public async Task UndoWritesBackTheEmptyMappingAndZeroLinkedCellIdWhenTheItemHadNoLinkBefore()
    {
        var handler = new PlantMutationHandler();
        var client = NewClient(handler);
        var id = Guid.NewGuid();
        var oldConfig = new PlantItemConfigDto { ItemId = id, DeviceType = (short)DeviceTypes.Automation, LinkedCellId = 0m };
        handler.Configs[id] = oldConfig;
        var graph = new PlantGraph([Item(id)], [], []);

        var newConfig = new PlantItemConfigDto { ItemId = id, DeviceType = (short)DeviceTypes.Automation, LinkedCellId = 5m };
        var link = new DoorMappingLink(id, "A5", 5m);
        var command = new LinkBufferCellCommand(oldConfig, newConfig, [], link);
        await command.ApplyAsync(client, graph);

        await command.UndoAsync(client, graph);

        // La trappola del compito: OldMappings e' vuoto, l'annulla deve svuotare davvero (DELETE),
        // non lasciare la riga appena collegata con una PUT vuota che sarebbe un no-op.
        Assert.Equal(1, handler.DoorMappingDeletes);
        Assert.DoesNotContain(handler.DoorMappings, m => m.ItemId == id);
        Assert.Equal(0m, handler.Configs[id].LinkedCellId);
        Assert.Equal(0m, graph.Items.Single(i => i.ItemId == id).LinkedCellId);
    }

    /// <summary>Quando il dispositivo aveva gia' una riga (un ricollegamento), l'annulla la rimette com'era, non la svuota.</summary>
    [Fact]
    public async Task UndoWritesBackAPreExistingMappingInsteadOfClearing()
    {
        var handler = new PlantMutationHandler();
        var client = NewClient(handler);
        var id = Guid.NewGuid();
        var previousMapping = new DoorMappingDto(0, id, "A1", 1m, "A", 0, false);
        handler.DoorMappings.Add(previousMapping);
        var oldConfig = new PlantItemConfigDto { ItemId = id, DeviceType = (short)DeviceTypes.Automation, LinkedCellId = 1m };
        handler.Configs[id] = oldConfig;
        var graph = new PlantGraph([Item(id)], [], []);

        var newConfig = new PlantItemConfigDto { ItemId = id, DeviceType = (short)DeviceTypes.Automation, LinkedCellId = 5m };
        var link = new DoorMappingLink(id, "A5", 5m);
        var command = new LinkBufferCellCommand(oldConfig, newConfig, [previousMapping], link);
        await command.ApplyAsync(client, graph);

        await command.UndoAsync(client, graph);

        Assert.Equal(0, handler.DoorMappingDeletes);
        var restored = Assert.Single(handler.DoorMappings, m => m.ItemId == id);
        Assert.Equal(1m, restored.CellId);
        Assert.Equal(1m, handler.Configs[id].LinkedCellId);
    }

    /// <summary>
    /// Se la scrittura della configurazione fallisce, la mappa porte non deve essere toccata. La
    /// configurazione si scrive PER PRIMA proprio perche' e' l'unica delle due rotte a chiamare
    /// <c>RequireNotRunning</c> - col motore in Run risponde sempre 409, e scrivendola prima quel 409
    /// arriva a mani vuote invece di lasciare la mappa porte gia' cambiata e orfana della configurazione
    /// (si veda il remarks della classe).
    /// </summary>
    [Fact]
    public async Task WhenTheConfigurationWriteIsRejectedWithConflictTheDoorMappingIsNeverTouched()
    {
        var handler = new PlantMutationHandler();
        var client = NewClient(handler);
        var id = Guid.NewGuid();
        var previousMapping = new DoorMappingDto(0, id, "A1", 1m, "A", 0, false);
        handler.DoorMappings.Add(previousMapping);
        var oldConfig = new PlantItemConfigDto { ItemId = id, DeviceType = (short)DeviceTypes.Automation, LinkedCellId = 1m };
        handler.Configs[id] = oldConfig;
        var graph = new PlantGraph([Item(id)], [], []);

        handler.FailNext(PlantMutationHandler.Route.ConfigPut, HttpStatusCode.Conflict);
        var newConfig = new PlantItemConfigDto { ItemId = id, DeviceType = (short)DeviceTypes.Automation, LinkedCellId = 5m };
        var link = new DoorMappingLink(id, "A5", 5m);
        var command = new LinkBufferCellCommand(oldConfig, newConfig, [previousMapping], link);

        var ex = await Assert.ThrowsAsync<WcsApiException>(() => command.ApplyAsync(client, graph));

        Assert.Equal(HttpStatusCode.Conflict, ex.StatusCode);
        // Niente e' stato scritto: la mappa porte resta quella di prima (la riga sulla cella 1), non
        // quella nuova, e il tentativo di collegamento non e' nemmeno arrivato all'host.
        Assert.Equal(0, handler.DoorMappingLinks);
        var stillThere = Assert.Single(handler.DoorMappings, m => m.ItemId == id);
        Assert.Equal(1m, stillThere.CellId);
        Assert.Equal(1m, handler.Configs[id].LinkedCellId);
    }

    /// <summary>Lo stesso caso durante l'annulla: <see cref="UndoAsync"/> scrive nello stesso ordine di <see cref="ApplyAsync"/>, per la stessa ragione.</summary>
    [Fact]
    public async Task WhenTheConfigurationWriteIsRejectedWithConflictDuringUndoTheDoorMappingIsNeverTouched()
    {
        var handler = new PlantMutationHandler();
        var client = NewClient(handler);
        var id = Guid.NewGuid();
        var newMapping = new DoorMappingDto(0, id, "A5", 5m, "A", 0, false);
        handler.DoorMappings.Add(newMapping);
        var oldConfig = new PlantItemConfigDto { ItemId = id, DeviceType = (short)DeviceTypes.Automation, LinkedCellId = 0m };
        var newConfig = new PlantItemConfigDto { ItemId = id, DeviceType = (short)DeviceTypes.Automation, LinkedCellId = 5m };
        handler.Configs[id] = newConfig;
        var graph = new PlantGraph([Item(id)], [], []);

        var link = new DoorMappingLink(id, "A5", 5m);
        var command = new LinkBufferCellCommand(oldConfig, newConfig, [], link);

        handler.FailNext(PlantMutationHandler.Route.ConfigPut, HttpStatusCode.Conflict);

        var ex = await Assert.ThrowsAsync<WcsApiException>(() => command.UndoAsync(client, graph));

        Assert.Equal(HttpStatusCode.Conflict, ex.StatusCode);
        Assert.Equal(0, handler.DoorMappingDeletes);
        Assert.Equal(0, handler.DoorMappingPuts);
        var stillThere = Assert.Single(handler.DoorMappings, m => m.ItemId == id);
        Assert.Equal(5m, stillThere.CellId);
        Assert.Equal(5m, handler.Configs[id].LinkedCellId);
    }
}
