using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Wcs.Api.Contracts;
using Wcs.Api.Contracts.Dtos;
using Wcs.Engine;
using Wcs.Engine.Persistence.Entities;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Host.Test;

[Collection("host")]
public sealed class PlantEndpointsTest(HostFixture host)
{
    [Fact]
    public async Task DoorMappingsAreWrittenReadAndLinked()
    {
        // Da questo compito («run sola lettura») le tre rotte delle mappature porte chiedono anche
        // loro il motore fermo, come le loro sorelle: la fixture lascia il motore in Run (si veda
        // DeletingAnItemOrAJunctionThatIsNotThereIs404 qui sotto).
        var previousStatus = Navigator.NavStatus;
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        try
        {
            var client = host.CreateAuthenticatedClient();
            using var _ = host.GrantEditPermission(client);
            var storage = Guid.NewGuid();
            var relocator = Guid.NewGuid();
            await using (var context = host.Store.CreateContext())
            {
                context.PlantItems.Add(new PlantItem { ItemId = storage, RelocateItemId = relocator });
                await context.SaveChangesAsync();
            }
            var url = ApiRoutes.Prefix + ApiRoutes.DoorMappings;
            var put = await client.PutAsJsonAsync(url, new[] { new DoorMappingDto(0, storage, "A", 100, null, 0, false) });
            Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
            Assert.Contains(await client.GetFromJsonAsync<List<DoorMappingDto>>(url) ?? [], m => m.ItemId == storage && m.CellId == 100);

            var link = await client.PostAsJsonAsync(url + "/link", new DoorMappingLink(storage, "A", 101));
            Assert.Equal(HttpStatusCode.NoContent, link.StatusCode);

            var automatic = await client.GetFromJsonAsync<List<AutomaticCell>>(ApiRoutes.Prefix + ApiRoutes.AutomaticCells);
            Assert.Contains(automatic ?? [], c => c.ItemId == storage && c.RelocatorItemId == relocator);
        }
        finally
        {
            Navigator.NavStatus = previousStatus;
        }
    }

    [Fact]
    public async Task ClearingDoorMappingsRemovesOnlyTheGivenItem()
    {
        var previousStatus = Navigator.NavStatus;
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        try
        {
            var client = host.CreateAuthenticatedClient();
            using var _ = host.GrantEditPermission(client);
            var storage = Guid.NewGuid();
            var other = Guid.NewGuid();
            // I due dispositivi prima delle loro porte: da questo lotto la mappa porte risponde 404
            // su un dispositivo che non c'e', invece di 204 lasciando due righe orfane.
            var items = ApiRoutes.Prefix + ApiRoutes.Items;
            foreach (var itemId in new[] { storage, other })
                Assert.Equal(HttpStatusCode.Created,
                    (await client.PostAsJsonAsync(items, new PlantItemConfigDto { ItemId = itemId })).StatusCode);
            var url = ApiRoutes.Prefix + ApiRoutes.DoorMappings;
            var put = await client.PutAsJsonAsync(url, new[]
            {
                new DoorMappingDto(0, storage, "A", 200, null, 0, false),
                new DoorMappingDto(0, other, "B", 201, null, 0, false)
            });
            Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

            var delete = await client.DeleteAsync($"{url}/{storage}");
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

            var remaining = await client.GetFromJsonAsync<List<DoorMappingDto>>(url) ?? [];
            Assert.DoesNotContain(remaining, m => m.ItemId == storage);
            Assert.Contains(remaining, m => m.ItemId == other);
        }
        finally
        {
            Navigator.NavStatus = previousStatus;
        }
    }

    [Fact]
    public async Task ItemsAndJunctionsAreEmptyWithoutAPlantAndAMissingItemIs404()
    {
        var client = host.CreateAuthenticatedClient();
        Assert.Empty((await client.GetFromJsonAsync<List<PlantItemDto>>(ApiRoutes.Prefix + ApiRoutes.Items))!);
        Assert.Empty((await client.GetFromJsonAsync<List<JunctionDto>>(ApiRoutes.Prefix + ApiRoutes.Junctions))!);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{ApiRoutes.Prefix}{ApiRoutes.Items}/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task DeletingAnItemOrAJunctionThatIsNotThereIs404()
    {
        var client = host.CreateAuthenticatedClient();
        // Le tre cancellazioni sono rifiutate in Run come le loro sorelle, e la fixture lascia il
        // motore in Run: qui si guarda il 404, che e' un'altra cosa. Il 409 sta in
        // PlantDeletionGuardsTest.
        var previousStatus = Navigator.NavStatus;
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        using var _ = host.GrantEditPermission(client);
        try
        {
            Assert.Equal(HttpStatusCode.NotFound,
                (await client.DeleteAsync($"{ApiRoutes.Prefix}{ApiRoutes.Items}/{Guid.NewGuid()}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound,
                (await client.DeleteAsync($"{ApiRoutes.Prefix}{ApiRoutes.Junctions}/{Guid.NewGuid()}")).StatusCode);

            var itemId = Guid.NewGuid();
            await using (var context = host.Store.CreateContext())
            {
                context.PlantItems.Add(new PlantItem { ItemId = itemId });
                await context.SaveChangesAsync();
            }
            Assert.Equal(HttpStatusCode.NoContent,
                (await client.DeleteAsync($"{ApiRoutes.Prefix}{ApiRoutes.Items}/{itemId}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound,
                (await client.DeleteAsync($"{ApiRoutes.Prefix}{ApiRoutes.Items}/{itemId}")).StatusCode);
        }
        finally
        {
            Navigator.NavStatus = previousStatus;
        }
    }

    [Fact]
    public async Task TheDiagramIsNotFoundUntilSomeoneSavesIt()
    {
        var client = host.CreateAuthenticatedClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(ApiRoutes.Prefix + ApiRoutes.Diagram)).StatusCode);
    }

    /// <summary>
    /// L'Editor manda il binario di <c>SaveBinary</c> vuoto (sull'impianto di prova lo era gia' nel
    /// vecchio database): il salvataggio deve passare con il solo XML, e fallire senza XML.
    /// </summary>
    [Fact]
    public async Task TheDiagramIsSavedWithTheXmlAloneAndRefusedWithoutIt()
    {
        var client = host.CreateAuthenticatedClient();
        using var _ = host.GrantEditPermission(client);
        var xml = System.Text.Encoding.UTF8.GetBytes("<DiagramDocumentWrapper />");
        var previousStatus = Navigator.NavStatus;
        try
        {
            var refused = await client.PutAsJsonAsync(ApiRoutes.Prefix + ApiRoutes.Diagram, new DiagramDto([1], null, null, 0, 0, 0));
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

            var saved = await client.PutAsJsonAsync(ApiRoutes.Prefix + ApiRoutes.Diagram, new DiagramDto([], xml, null, 0, 0, 0));
            Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
            // Il salvataggio ricarica il motore (ReloadRouteDiagram -> LoadNavigatorData), che avvia
            // i suoi cicli di sfondo anche con StartEngine falso: sono quelli che il finally ferma.
            Assert.True(Navigator.BackgroundLoopsRunning);

            var read = await client.GetFromJsonAsync<DiagramDto>(ApiRoutes.Prefix + ApiRoutes.Diagram);
            Assert.Equal(xml, read!.DiagramXml);
        }
        finally
        {
            // Con StartEngine falso l'host non chiama Navigator.Dispose: lasciati vivi, i cicli
            // scriverebbero sullo store per tutta la suite ed eseguirebbero i comandi che gli altri
            // test creano. La ricarica lascia anche il motore in Stop: StartNavigator non riparte
            // senza stringa di connessione, e nella suite non ce n'e' una — la fixture non la
            // applica, e Navigator.Dispose, che EngineLoadFailureTest chiama da StopAsync, la
            // azzera. Lo stato torna quello che la fixture aveva messo.
            await Navigator.StopBackgroundLoopsAsync();
            Navigator.NavStatus = previousStatus;
            await using var context = host.Store.CreateContext();
            await context.PlantDiagrams.ExecuteDeleteAsync();
        }
        Assert.False(Navigator.BackgroundLoopsRunning);
    }
}
