using System.Net;
using System.Net.Http.Json;
using Wcs.Api.Contracts;
using Wcs.Api.Contracts.Dtos;
using Wcs.Engine;
using Wcs.Engine.Api;
using Wcs.Engine.Persistence.Entities;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Host.Test;

/// <summary>
/// Le tre rotte che cancellano pezzi d'impianto hanno la guardia su Run delle loro sorelle, e
/// tolgono dalla memoria quello che tolgono dallo store.
/// </summary>
/// <remarks>
/// <para>
/// <b>La guardia.</b> <c>RequireNotRunning</c> c'era su scrittura di configurazione, svuotamento
/// totale, modalita' di connessione, aggiunta e priorita' della giunzione; su
/// <c>RemoveItemAsync</c>, <c>KeepItemsAsync</c> e <c>RemoveJunctionAsync</c> no. A motore in Run
/// si poteva togliere un dispositivo o un arco mentre ci passavano missioni.
/// </para>
/// <para>
/// <b>La memoria.</b> Le cancellazioni chiamavano solo <c>PlantPersistence</c>, che scrive solo il
/// database, mentre <c>GET /plant/items</c>, <c>GET /plant/items/{id}</c> e
/// <c>GET /plant/junctions</c> leggono la memoria: dopo un <c>DELETE</c> riuscito il dispositivo
/// continuava a rispondere <c>200</c> e la sua configurazione non c'era piu'. Le rotte che
/// <i>creano</i> toccano tutte e due.
/// </para>
/// </remarks>
[Collection("host")]
public sealed class PlantDeletionGuardsTest(HostFixture host) : IDisposable
{
    private readonly NavigatorStatusTypes _previousStatus = Navigator.NavStatus;

    public void Dispose()
    {
        Navigator.NavStatus = _previousStatus;
        Wcs.Engine.Collections.PathDevices.Instance.Clear();
        Wcs.Engine.Collections.PathJunctions.Instance.Clear();
    }

    [Fact]
    public async Task InRunTheThreeDeletionsAre409()
    {
        var client = host.CreateAuthenticatedClient();
        using var _ = host.GrantEditPermission(client);
        Navigator.NavStatus = NavigatorStatusTypes.Run;

        Assert.Equal(HttpStatusCode.Conflict,
            (await client.DeleteAsync($"{ApiRoutes.Prefix}{ApiRoutes.Items}/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.DeleteAsync($"{ApiRoutes.Prefix}{ApiRoutes.Junctions}/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.PostAsJsonAsync($"{ApiRoutes.Prefix}{ApiRoutes.Items}/keep", new List<Guid> { Guid.NewGuid() })).StatusCode);
    }

    /// <summary>
    /// A motore fermo la cancellazione riesce, e il dispositivo sparisce anche dalla memoria
    /// insieme alle giunzioni che lo toccavano.
    /// </summary>
    [Fact]
    public async Task ADeletedItemLeavesTheMemoryToo()
    {
        var client = host.CreateAuthenticatedClient();
        using var _ = host.GrantEditPermission(client);
        Navigator.NavStatus = NavigatorStatusTypes.Stop;

        var itemId = await NewItemInMemoryAndStore(client);
        var otherId = await NewItemInMemoryAndStore(client);

        var junction = await (await client.PostAsJsonAsync(ApiRoutes.Prefix + ApiRoutes.Junctions,
            new JunctionRequest(itemId, otherId, 0))).Content.ReadFromJsonAsync<JunctionDto>();
        Assert.NotNull(junction);

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.DeleteAsync($"{ApiRoutes.Prefix}{ApiRoutes.Items}/{itemId}")).StatusCode);

        // Prima rispondeva 200: la memoria non veniva toccata.
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"{ApiRoutes.Prefix}{ApiRoutes.Items}/{itemId}")).StatusCode);
        Assert.DoesNotContain(await client.GetFromJsonAsync<List<PlantItemDto>>(ApiRoutes.Prefix + ApiRoutes.Items) ?? [],
            i => i.ItemId == itemId);
        // L'arco senza un estremo e' una giunzione orfana: se ne va con il dispositivo, in
        // memoria come sullo store.
        Assert.DoesNotContain(await client.GetFromJsonAsync<List<JunctionDto>>(ApiRoutes.Prefix + ApiRoutes.Junctions) ?? [],
            j => j.JunctionId == junction.JunctionId);
        // E il dispositivo rimasto e' ancora li'.
        Assert.Equal(HttpStatusCode.OK,
            (await client.GetAsync($"{ApiRoutes.Prefix}{ApiRoutes.Items}/{otherId}")).StatusCode);
    }

    /// <summary>
    /// Lo stesso per «tieni questi»: quello che non e' nell'elenco esce dalla memoria.
    /// </summary>
    [Fact]
    public async Task KeepItemsForgetsWhatItDeletes()
    {
        var client = host.CreateAuthenticatedClient();
        using var _ = host.GrantEditPermission(client);
        Navigator.NavStatus = NavigatorStatusTypes.Stop;

        var kept = await NewItemInMemoryAndStore(client);
        var dropped = await NewItemInMemoryAndStore(client);

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsJsonAsync($"{ApiRoutes.Prefix}{ApiRoutes.Items}/keep", new List<Guid> { kept })).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"{ApiRoutes.Prefix}{ApiRoutes.Items}/{dropped}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{ApiRoutes.Prefix}{ApiRoutes.Items}/{kept}")).StatusCode);
    }

    /// <summary>
    /// Il dispositivo entra dalla rotta che lo crea, che e' quella che tocca memoria e store
    /// insieme: e' la strada dell'Editor.
    /// </summary>
    private async Task<Guid> NewItemInMemoryAndStore(HttpClient client)
    {
        var itemId = Guid.NewGuid();
        var response = await client.PostAsJsonAsync($"{ApiRoutes.Prefix}{ApiRoutes.Items}",
            DtoMapping(itemId));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(PlantQueries.Device(itemId));
        return itemId;
    }

    private static PlantItemConfigDto DtoMapping(Guid itemId) =>
        Host.Mapping.DtoMapping.ToConfigDto(new PlantItem { ItemId = itemId, Name = "collaudo" });
}
