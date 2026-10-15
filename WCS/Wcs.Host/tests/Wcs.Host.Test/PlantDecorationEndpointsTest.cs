using System.Net;
using System.Net.Http.Json;
using Wcs.Api.Contracts;
using Wcs.Api.Contracts.Dtos;
using Wcs.Engine;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Host.Test;

/// <summary>
/// <c>/plant/decoration</c> (compito 7): la decorazione del disegno - scritte e riquadri liberi
/// dell'operatore - vive nel wcs.db come blocco unico di XML, non interpretato dal motore.
/// </summary>
/// <remarks>
/// La collezione "host" condivide un solo <see cref="HostFixture"/> fra tutte le classi di test
/// (<c>DisableTestParallelization</c>): niente qui assume che il database sia vuoto all'avvio,
/// perche' un'altra classe puo' aver gia' scritto qualcosa prima. Il caso "vuoto per davvero" e'
/// gia' provato senza questo rischio in <c>Wcs.Engine.Test.DecorationStoreTest</c>, su un database
/// isolato per test; qui si guarda solo il contratto dell'API - mai 404, il permesso, il motore
/// fermo - con un marcatore unico per evitare collisioni con quello che altre classi hanno scritto.
/// </remarks>
[Collection("host")]
public sealed class PlantDecorationEndpointsTest(HostFixture host) : IDisposable
{
    private readonly NavigatorStatusTypes _previousStatus = Navigator.NavStatus;
    private const string Url = ApiRoutes.Prefix + ApiRoutes.PlantDecoration;

    public void Dispose() => Navigator.NavStatus = _previousStatus;

    /// <summary>Mai 404: un impianto senza decorazione (o con una scritta da un'altra classe) risponde comunque 200.</summary>
    [Fact]
    public async Task GetNeverAnswersNotFound()
    {
        var client = host.CreateAuthenticatedClient();
        var response = await client.GetAsync(Url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TheDecorationSurvivesASaveAndAReread()
    {
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        var client = host.CreateAuthenticatedClient();
        using var _ = host.GrantEditPermission(client);

        var marker = $"Zona-{Guid.NewGuid()}";
        var put = await client.PutAsJsonAsync(Url, new PlantDecorationDto($"<mxCell id=\"nota-1\" value=\"{marker}\"/>"));
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        var read = await client.GetFromJsonAsync<PlantDecorationDto>(Url);
        Assert.Contains(marker, read!.Xml);
    }

    [Fact]
    public async Task PutWithoutThePermissionHeaderIsRefusedWith423()
    {
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        var client = host.CreateAuthenticatedClient();
        var put = await client.PutAsJsonAsync(Url, new PlantDecorationDto("<mxCell/>"));
        Assert.Equal(423, (int)put.StatusCode);
    }

    [Fact]
    public async Task PutWhileTheEngineIsRunningIsRefusedWith409()
    {
        var client = host.CreateAuthenticatedClient();
        using var _ = host.GrantEditPermission(client);
        Navigator.NavStatus = NavigatorStatusTypes.Run;

        var put = await client.PutAsJsonAsync(Url, new PlantDecorationDto("<mxCell/>"));
        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
    }
}
