using System.Net;
using System.Net.Http.Json;
using Wcs.Api.Contracts;
using Wcs.Api.Contracts.Dtos;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Host.Test;

[Collection("host")]
public sealed class StatusEndpointsTest(HostFixture host)
{
    [Fact]
    public async Task WithoutTheKeyTheAnswerIs401()
    {
        var response = await host.CreateClient().GetAsync(ApiRoutes.Prefix + ApiRoutes.Status);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        // Il corpo e' ProblemDetails come ogni altro rifiuto, non un oggetto anonimo.
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task WithTheKeyTheStatusComesBack()
    {
        var status = await host.CreateAuthenticatedClient().GetFromJsonAsync<EngineStatus>(ApiRoutes.Prefix + ApiRoutes.Status);
        Assert.NotNull(status);
        Assert.Equal(NavigatorStatusTypes.Run, status.NavStatus);
    }

    [Fact]
    public async Task TheStatusSaysFromWhichMonthTheHistoryAnswers()
    {
        var client = host.CreateAuthenticatedClient();
        var status = await client.GetFromJsonAsync<EngineStatus>(ApiRoutes.Prefix + ApiRoutes.Status);
        // In memoria c'e' solo il mese corrente.
        Assert.Equal(new DateOnly(DateTime.Now.Year, DateTime.Now.Month, 1), status!.HistoryFrom);
    }
}
