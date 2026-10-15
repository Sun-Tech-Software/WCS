using System.Net;
using System.Net.Http.Json;
using Wcs.Api.Contracts;
using Wcs.Api.Contracts.Dtos;
using Wcs.Engine;
using Wcs.Engine.Persistence.Entities;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Host.Test;

/// <summary>
/// <c>POST /plant/junctions</c> non costruisce piu' archi senza estremi.
/// </summary>
/// <remarks>
/// La giunzione e' un arco del grafo su cui gira la ricerca dei percorsi, e l'invariante «gli
/// estremi sono dispositivi esistenti» era difesa da un ingresso solo: l'importatore interno (non pubblicato) scarta le
/// giunzioni orfane e le conta nel rapporto, questa rotta le accettava con qualunque coppia di
/// <c>Guid</c>, l'anello su se' stesso compreso. Il client e' l'Editor, dove un <c>Guid</c>
/// sbagliato e' un errore ordinario, e l'arco restava nel database finche' qualcuno non lo andava
/// a cercare.
/// </remarks>
[Collection("host")]
public sealed class JunctionValidationTest(HostFixture host) : IDisposable
{
    private readonly NavigatorStatusTypes _previousStatus = Navigator.NavStatus;

    public void Dispose() => Navigator.NavStatus = _previousStatus;

    [Fact]
    public async Task AJunctionWithoutRealEndsIsRefused()
    {
        var client = host.CreateAuthenticatedClient();
        using var _ = host.GrantEditPermission(client);
        Navigator.NavStatus = NavigatorStatusTypes.Stop;

        var url = ApiRoutes.Prefix + ApiRoutes.Junctions;
        var known = await NewStoredItem();
        var unknown = Guid.NewGuid();

        // Estremi vuoti, anello su se' stesso, origine sconosciuta, destinazione sconosciuta.
        foreach (var request in new[]
                 {
                     new JunctionRequest(Guid.Empty, known, 0),
                     new JunctionRequest(known, Guid.Empty, 0),
                     new JunctionRequest(known, known, 0),
                     new JunctionRequest(unknown, known, 0),
                     new JunctionRequest(known, unknown, 0)
                 })
        {
            var response = await client.PostAsJsonAsync(url, request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        // E nessuna delle cinque e' finita fra le giunzioni.
        Assert.DoesNotContain(await client.GetFromJsonAsync<List<JunctionDto>>(url) ?? [],
            j => j.SourceItemId == unknown || j.DestinationItemId == unknown ||
                 j.SourceItemId == Guid.Empty || j.DestinationItemId == Guid.Empty ||
                 j.SourceItemId == j.DestinationItemId);
    }

    /// <summary>
    /// Senza questo collaudo il precedente sarebbe verde anche con una rotta che rifiuta tutto.
    /// </summary>
    [Fact]
    public async Task AJunctionBetweenTwoRealItemsIsStillCreated()
    {
        var client = host.CreateAuthenticatedClient();
        using var _ = host.GrantEditPermission(client);
        Navigator.NavStatus = NavigatorStatusTypes.Stop;

        var url = ApiRoutes.Prefix + ApiRoutes.Junctions;
        var source = await NewStoredItem();
        var destination = await NewStoredItem();

        var response = await client.PostAsJsonAsync(url, new JunctionRequest(source, destination, 3));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var junction = await response.Content.ReadFromJsonAsync<JunctionDto>();
        Assert.Equal(source, junction!.SourceItemId);
        Assert.Equal(destination, junction.DestinationItemId);

        // Pulizia: la giunzione resta in memoria e nello store per tutta la suite.
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{url}/{junction.JunctionId}")).StatusCode);
    }

    /// <summary>
    /// Voce 17 della coda, misurata: una seconda <c>POST</c> sulla stessa coppia NON scrive un
    /// doppione. <c>PlantQueries.AddJunctionAsync</c> collassa sulla coppia origine-destinazione e
    /// risponde con la giunzione che c'e' gia' (la priorita' chiesta resta quella di prima), e
    /// l'Editor ci conta (<c>AddJunctionCommand</c>, <c>DeleteItemCommand</c>). Il verso conta:
    /// B->A dopo A->B e' una giunzione nuova.
    /// </summary>
    [Fact]
    public async Task ASecondPostOnTheSamePairWritesNoSecondJunctionAndTheOtherWayIsANewOne()
    {
        var client = host.CreateAuthenticatedClient();
        using var _ = host.GrantEditPermission(client);
        Navigator.NavStatus = NavigatorStatusTypes.Stop;

        var url = ApiRoutes.Prefix + ApiRoutes.Junctions;
        var source = await NewStoredItem();
        var destination = await NewStoredItem();

        var first = await (await client.PostAsJsonAsync(url, new JunctionRequest(source, destination, 3))).Content.ReadFromJsonAsync<JunctionDto>();
        var again = await client.PostAsJsonAsync(url, new JunctionRequest(source, destination, 99));
        var back = await (await client.PostAsJsonAsync(url, new JunctionRequest(destination, source, 3))).Content.ReadFromJsonAsync<JunctionDto>();

        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
        var second = await again.Content.ReadFromJsonAsync<JunctionDto>();
        Assert.Equal(first!.JunctionId, second!.JunctionId);
        Assert.Equal(3, second.Priority);
        await using (var context = host.Store.CreateContext())
        {
            Assert.Equal(1, context.PlantJunctions.Count(j => j.SourceItemId == source && j.DestinationItemId == destination));
            Assert.Equal(1, context.PlantJunctions.Count(j => j.SourceItemId == destination && j.DestinationItemId == source));
        }
        Assert.NotEqual(first.JunctionId, back!.JunctionId);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{url}/{first.JunctionId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{url}/{back.JunctionId}")).StatusCode);
    }

    private async Task<Guid> NewStoredItem()
    {
        var itemId = Guid.NewGuid();
        await using var context = host.Store.CreateContext();
        context.PlantItems.Add(new PlantItem { ItemId = itemId });
        await context.SaveChangesAsync();
        return itemId;
    }
}
