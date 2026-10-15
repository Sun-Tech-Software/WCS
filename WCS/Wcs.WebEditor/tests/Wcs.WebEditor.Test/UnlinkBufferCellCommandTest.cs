using System.Net;
using Wcs.Api.Client;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Editing;
using Wcs.WebEditor.Model;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

/// <summary>
/// <see cref="UnlinkBufferCellCommand"/> in isolamento (compito 11): il simmetrico di
/// <see cref="LinkBufferCellCommand"/> - si veda <c>LinkBufferCellCommandTest</c> per lo schema.
/// </summary>
public sealed class UnlinkBufferCellCommandTest
{
    private static WcsClient NewClient(PlantMutationHandler handler) =>
        new(new WcsClientOptions { BaseAddress = new Uri("http://localhost:5580") }, handler);

    private static PlantItemDto Item(Guid id) => new(
        id, "prima", DeviceTypes.Automation, null, null, null, default, default, 0, null, 0, 0, 0, 0, default!);

    [Fact]
    public void ConstructingWithMismatchedItemIdsThrows()
    {
        var oldConfig = new PlantItemConfigDto { ItemId = Guid.NewGuid() };
        var newConfig = new PlantItemConfigDto { ItemId = Guid.NewGuid() };

        Assert.Throws<ArgumentException>(() => new UnlinkBufferCellCommand(oldConfig, newConfig, []));
    }

    /// <summary>Scioglie con una DELETE e riporta LinkedCellId a zero - il valore che il motore stesso tratta come "nessuna cella" (si veda il remarks della classe).</summary>
    [Fact]
    public async Task ApplyClearsTheMappingAndZeroesLinkedCellIdThenRereadsTheItem()
    {
        var handler = new PlantMutationHandler();
        var client = NewClient(handler);
        var id = Guid.NewGuid();
        var mapping = new DoorMappingDto(0, id, "A5", 5m, "A", 0, false);
        handler.DoorMappings.Add(mapping);
        var oldConfig = new PlantItemConfigDto { ItemId = id, DeviceType = (short)DeviceTypes.Automation, LinkedCellId = 5m };
        handler.Configs[id] = oldConfig;
        var graph = new PlantGraph([Item(id)], [], []);

        var newConfig = new PlantItemConfigDto { ItemId = id, DeviceType = (short)DeviceTypes.Automation, LinkedCellId = 0m };
        var command = new UnlinkBufferCellCommand(oldConfig, newConfig, [mapping]);

        await command.ApplyAsync(client, graph);

        Assert.Equal(1, handler.DoorMappingDeletes);
        Assert.Equal(1, handler.ConfigWrites);
        Assert.Equal(1, handler.ItemGets);
        Assert.DoesNotContain(handler.DoorMappings, m => m.ItemId == id);
        Assert.Equal(0m, handler.Configs[id].LinkedCellId);
        Assert.Equal(0m, graph.Items.Single(i => i.ItemId == id).LinkedCellId);
    }

    [Fact]
    public async Task UndoWritesBackTheMappingAndTheOriginalLinkedCellId()
    {
        var handler = new PlantMutationHandler();
        var client = NewClient(handler);
        var id = Guid.NewGuid();
        var mapping = new DoorMappingDto(0, id, "A5", 5m, "A", 0, false);
        handler.DoorMappings.Add(mapping);
        var oldConfig = new PlantItemConfigDto { ItemId = id, DeviceType = (short)DeviceTypes.Automation, LinkedCellId = 5m };
        handler.Configs[id] = oldConfig;
        var graph = new PlantGraph([Item(id)], [], []);

        var newConfig = new PlantItemConfigDto { ItemId = id, DeviceType = (short)DeviceTypes.Automation, LinkedCellId = 0m };
        var command = new UnlinkBufferCellCommand(oldConfig, newConfig, [mapping]);
        await command.ApplyAsync(client, graph);

        await command.UndoAsync(client, graph);

        Assert.Equal(1, handler.DoorMappingPuts);
        var restored = Assert.Single(handler.DoorMappings, m => m.ItemId == id);
        Assert.Equal(5m, restored.CellId);
        Assert.Equal(5m, handler.Configs[id].LinkedCellId);
    }

    /// <summary>
    /// Se la scrittura della configurazione fallisce, la mappa porte non deve essere toccata - il
    /// simmetrico del test omonimo di <c>LinkBufferCellCommandTest</c>. La configurazione si scrive PER
    /// PRIMA proprio perche' e' l'unica delle due rotte a chiamare <c>RequireNotRunning</c> - col motore
    /// in Run risponde sempre 409, e scrivendola prima quel 409 arriva a mani vuote invece di lasciare
    /// la mappa porte gia' svuotata e la configurazione ancora sulla cella di prima (si veda il remarks
    /// della classe).
    /// </summary>
    [Fact]
    public async Task WhenTheConfigurationWriteIsRejectedWithConflictTheDoorMappingIsNeverTouched()
    {
        var handler = new PlantMutationHandler();
        var client = NewClient(handler);
        var id = Guid.NewGuid();
        var mapping = new DoorMappingDto(0, id, "A5", 5m, "A", 0, false);
        handler.DoorMappings.Add(mapping);
        var oldConfig = new PlantItemConfigDto { ItemId = id, DeviceType = (short)DeviceTypes.Automation, LinkedCellId = 5m };
        handler.Configs[id] = oldConfig;
        var graph = new PlantGraph([Item(id)], [], []);

        handler.FailNext(PlantMutationHandler.Route.ConfigPut, HttpStatusCode.Conflict);
        var newConfig = new PlantItemConfigDto { ItemId = id, DeviceType = (short)DeviceTypes.Automation, LinkedCellId = 0m };
        var command = new UnlinkBufferCellCommand(oldConfig, newConfig, [mapping]);

        var ex = await Assert.ThrowsAsync<WcsApiException>(() => command.ApplyAsync(client, graph));

        Assert.Equal(HttpStatusCode.Conflict, ex.StatusCode);
        Assert.Equal(0, handler.DoorMappingDeletes);
        var stillThere = Assert.Single(handler.DoorMappings, m => m.ItemId == id);
        Assert.Equal(5m, stillThere.CellId);
        Assert.Equal(5m, handler.Configs[id].LinkedCellId);
    }

    /// <summary>Lo stesso caso durante l'annulla: <see cref="UnlinkBufferCellCommand.UndoAsync"/> scrive nello stesso ordine di <see cref="UnlinkBufferCellCommand.ApplyAsync"/>, per la stessa ragione.</summary>
    [Fact]
    public async Task WhenTheConfigurationWriteIsRejectedWithConflictDuringUndoTheDoorMappingIsNeverTouched()
    {
        var handler = new PlantMutationHandler();
        var client = NewClient(handler);
        var id = Guid.NewGuid();
        var oldConfig = new PlantItemConfigDto { ItemId = id, DeviceType = (short)DeviceTypes.Automation, LinkedCellId = 5m };
        var newConfig = new PlantItemConfigDto { ItemId = id, DeviceType = (short)DeviceTypes.Automation, LinkedCellId = 0m };
        handler.Configs[id] = newConfig;
        var mapping = new DoorMappingDto(0, id, "A5", 5m, "A", 0, false);
        var graph = new PlantGraph([Item(id)], [], []);
        var command = new UnlinkBufferCellCommand(oldConfig, newConfig, [mapping]);

        handler.FailNext(PlantMutationHandler.Route.ConfigPut, HttpStatusCode.Conflict);

        var ex = await Assert.ThrowsAsync<WcsApiException>(() => command.UndoAsync(client, graph));

        Assert.Equal(HttpStatusCode.Conflict, ex.StatusCode);
        Assert.Equal(0, handler.DoorMappingPuts);
        Assert.DoesNotContain(handler.DoorMappings, m => m.ItemId == id);
        Assert.Equal(0m, handler.Configs[id].LinkedCellId);
    }
}
