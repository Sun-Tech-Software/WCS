using System.Net;
using Wcs.Api.Client;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Editing;
using Wcs.WebEditor.Model;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

/// <summary>
/// <see cref="EditItemConfigCommand"/> in isolamento (compito 9), contro un <see cref="WcsClient"/>
/// vero appoggiato su <see cref="PlantMutationHandler"/> - come <c>CreateItemCommandTest</c>: niente
/// qui tocca <c>PlantCanvas</c> o il DOM. Il cablaggio con il pulsante "Configura" e il pannello e'
/// provato a parte in <c>PlantCanvasConfigurePanelTest</c>.
/// </summary>
public sealed class EditItemConfigCommandTest
{
    private static WcsClient NewClient(PlantMutationHandler handler) =>
        new(new WcsClientOptions { BaseAddress = new Uri("http://localhost:5580") }, handler);

    [Fact]
    public void ConstructingWithMismatchedItemIdsThrows()
    {
        var oldConfig = new PlantItemConfigDto { ItemId = Guid.NewGuid() };
        var newConfig = new PlantItemConfigDto { ItemId = Guid.NewGuid() };

        Assert.Throws<ArgumentException>(() => new EditItemConfigCommand(oldConfig, newConfig));
    }

    [Fact]
    public async Task ApplyWritesTheNewConfigurationAndRereadsTheItemFromTheHost()
    {
        var handler = new PlantMutationHandler();
        var client = NewClient(handler);
        var id = Guid.NewGuid();
        var oldConfig = new PlantItemConfigDto { ItemId = id, Name = "prima", DeviceType = (short)DeviceTypes.Automation };
        handler.Configs[id] = oldConfig;
        var graph = new PlantGraph([Item(id, "prima")], [], []);

        var newConfig = new PlantItemConfigDto { ItemId = id, Name = "dopo", DeviceType = (short)DeviceTypes.Automation, TimeOutPlcMoving = 5000 };
        var command = new EditItemConfigCommand(oldConfig, newConfig);

        await command.ApplyAsync(client, graph);

        Assert.Equal(1, handler.ConfigWrites);
        Assert.Equal(1, handler.ItemGets); // rilegge dall'host, non copia NewConfig nel grafo (remarks della classe)
        Assert.Equal("dopo", handler.Configs[id].Name);
        Assert.Equal(5000, handler.Configs[id].TimeOutPlcMoving);

        var updated = graph.Items.Single(i => i.ItemId == id);
        Assert.Equal("dopo", updated.Name); // il grafo locale vede il nome nuovo, letto dall'host
    }

    /// <summary>
    /// Cambia due proprieta' (Name e TimeOutPlcMoving), non una sola: l'annulla rimette il DTO intero
    /// (si veda ApplyAsync/UndoAsync), quindi l'invariante da provare e' che *entrambe* tornino a
    /// quella di prima - un solo campo cambiato non l'avrebbe distinta da un annulla che rimette solo
    /// il campo appena toccato.
    /// </summary>
    [Fact]
    public async Task UndoWritesBackTheConfigurationCapturedBeforeTheEdit()
    {
        var handler = new PlantMutationHandler();
        var client = NewClient(handler);
        var id = Guid.NewGuid();
        var oldConfig = new PlantItemConfigDto { ItemId = id, Name = "prima", DeviceType = (short)DeviceTypes.Automation, TimeOutPlcMoving = 1000 };
        handler.Configs[id] = oldConfig;
        var graph = new PlantGraph([Item(id, "prima")], [], []);

        var newConfig = new PlantItemConfigDto { ItemId = id, Name = "dopo", DeviceType = (short)DeviceTypes.Automation, TimeOutPlcMoving = 5000 };
        var command = new EditItemConfigCommand(oldConfig, newConfig);
        await command.ApplyAsync(client, graph);

        await command.UndoAsync(client, graph);

        Assert.Equal(2, handler.ConfigWrites);
        // Torna esattamente cio' che era stato catturato prima della scrittura, su entrambi i campi.
        Assert.Equal("prima", handler.Configs[id].Name);
        Assert.Equal(1000, handler.Configs[id].TimeOutPlcMoving);
        var restored = graph.Items.Single(i => i.ItemId == id);
        Assert.Equal("prima", restored.Name);
    }

    /// <summary>
    /// 409 (il motore e' in Run): la scrittura solleva <see cref="WcsApiException"/>, il grafo locale
    /// non cambia - nessuna rilettura parte se la scrittura non e' riuscita.
    /// </summary>
    [Fact]
    public async Task WhenTheConfigWriteIsRejectedApplyThrowsAndTheGraphIsNotUpdated()
    {
        var handler = new PlantMutationHandler();
        var client = NewClient(handler);
        var id = Guid.NewGuid();
        var oldConfig = new PlantItemConfigDto { ItemId = id, Name = "prima" };
        handler.Configs[id] = oldConfig;
        var graph = new PlantGraph([Item(id, "prima")], [], []);

        handler.FailNext(PlantMutationHandler.Route.ConfigPut, HttpStatusCode.Conflict);
        var newConfig = new PlantItemConfigDto { ItemId = id, Name = "dopo" };
        var command = new EditItemConfigCommand(oldConfig, newConfig);

        var ex = await Assert.ThrowsAsync<WcsApiException>(() => command.ApplyAsync(client, graph));
        Assert.Equal(HttpStatusCode.Conflict, ex.StatusCode);
        Assert.Equal(0, handler.ItemGets);
        Assert.Equal("prima", graph.Items.Single(i => i.ItemId == id).Name);
    }

    private static PlantItemDto Item(Guid id, string name) => new(
        id, name, DeviceTypes.Automation, null, null, null, default, default, 0, null, 0, 0, 0, 0, default!);
}
