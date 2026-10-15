using System.Net;
using Wcs.Api.Client;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Editing;
using Wcs.WebEditor.Model;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

/// <summary>
/// <see cref="CreateItemCommand"/> in isolamento, contro un <see cref="WcsClient"/> vero appoggiato
/// su <see cref="PlantMutationHandler"/> (non bUnit: niente qui tocca <c>PlantCanvas</c> o il DOM -
/// il cablaggio con la palette e il clic sulla tela e' provato a parte in
/// <c>PlantCanvasCreateDeleteDuplicateTest</c>).
/// </summary>
public sealed class CreateItemCommandTest
{
    private static WcsClient NewClient(PlantMutationHandler handler) =>
        new(new WcsClientOptions { BaseAddress = new Uri("http://localhost:5580") }, handler);

    [Fact]
    public void ConstructingWithMismatchedItemIdsThrows()
    {
        var configId = Guid.NewGuid();
        var geometryId = Guid.NewGuid();
        var config = new PlantItemConfigDto { ItemId = configId };
        var geometry = new PlantItemGeometryDto(geometryId, 0, 0, 100, 100, 0);

        Assert.Throws<ArgumentException>(() => new CreateItemCommand(config, geometry, "Automation"));
    }

    /// <summary>
    /// La creazione e' <c>POST /plant/items</c> seguita da <c>PUT .../geometry</c>, nello stesso
    /// punto dove l'utente ha rilasciato - qui verificato contando le richieste che arrivano davvero
    /// al gestore finto, non le chiamate a un metodo C#. Fino al lotto del 404 la prima delle due era
    /// <c>PUT .../config</c>, che creava perche' era un upsert: la rotta della creazione non esisteva.
    /// </summary>
    [Fact]
    public async Task ApplyWritesConfigurationThenGeometryAndRereadsTheCreatedItem()
    {
        var handler = new PlantMutationHandler();
        var client = NewClient(handler);
        var graph = new PlantGraph();
        var id = Guid.NewGuid();
        var config = new PlantItemConfigDto { ItemId = id, DeviceType = (short)DeviceTypes.Automation };
        var geometry = new PlantItemGeometryDto(id, 130, 70, 100, 100, 0);
        var command = new CreateItemCommand(config, geometry, "Automation");

        await command.ApplyAsync(client, graph);

        Assert.Equal(1, handler.ItemCreates);
        Assert.Equal(0, handler.ConfigWrites); // la PUT della configurazione non c'entra piu' con la nascita
        Assert.Equal(1, handler.GeometryWrites);
        Assert.Equal(1, handler.ItemGets); // rilegge dall'host, non inventa Live (remarks della classe)

        var created = graph.Items.Single(i => i.ItemId == id);
        Assert.Equal(130, created.X);
        Assert.Equal(70, created.Y);
        Assert.Equal(DeviceTypes.Automation, created.DeviceType);
        Assert.Equal(handler.GeometryVersions[id], created.GeometryVersion);
    }

    [Fact]
    public async Task UndoDeletesTheCreatedItemAndRemovesItFromTheGraph()
    {
        var handler = new PlantMutationHandler();
        var client = NewClient(handler);
        var graph = new PlantGraph();
        var id = Guid.NewGuid();
        var command = new CreateItemCommand(
            new PlantItemConfigDto { ItemId = id },
            new PlantItemGeometryDto(id, 0, 0, 100, 100, 0),
            "Automation");
        await command.ApplyAsync(client, graph);
        Assert.Single(graph.Items);

        await command.UndoAsync(client, graph);

        Assert.Equal(1, handler.ItemDeletes);
        Assert.Empty(graph.Items);
        Assert.False(handler.Configs.ContainsKey(id));
    }

    /// <summary>
    /// Decisione 4 del compito: se la configurazione riesce ma la geometria e' rifiutata (per
    /// esempio 409, il motore e' tornato in Run a meta' gesto), <c>ApplyAsync</c> solleva - il
    /// comando non entra in pila (contratto di <c>CommandStack.DoAsync</c>, provato in isolamento in
    /// <c>CommandStackTest</c>) - e il grafo locale non vede il dispositivo nuovo: e' il limite
    /// dichiarato nel remarks della classe, non un fallimento nascosto.
    /// </summary>
    [Fact]
    public async Task WhenTheGeometryWriteFailsApplyThrowsAndTheGraphIsNotUpdated()
    {
        var handler = new PlantMutationHandler();
        var client = NewClient(handler);
        var graph = new PlantGraph();
        var id = Guid.NewGuid();
        var command = new CreateItemCommand(
            new PlantItemConfigDto { ItemId = id },
            new PlantItemGeometryDto(id, 0, 0, 100, 100, 0),
            "Automation");

        // La configurazione riesce, la geometria no (409: il motore e' tornato in Run a meta' gesto).
        handler.FailNext(PlantMutationHandler.Route.GeometryPut, HttpStatusCode.Conflict);
        var ex = await Assert.ThrowsAsync<WcsApiException>(() => command.ApplyAsync(client, graph));
        Assert.Equal(HttpStatusCode.Conflict, ex.StatusCode);

        Assert.Equal(1, handler.ItemCreates); // la scrittura orfana: dichiarata nel remarks, non nascosta
        Assert.True(handler.Configs.ContainsKey(id));
        Assert.Empty(graph.Items); // ma il grafo locale non la vede: nessun comando in pila, nessun dato inventato
    }
}
