using System.Net;
using System.Net.Http.Json;
using Wcs.Api.Contracts;
using Wcs.Api.Contracts.Dtos;
using Wcs.Host.Security;
using Xunit;

namespace Wcs.Host.Test;

[Collection("host")]
public sealed class ApiAuthorizationTest(HostFixture host)
{
    [Fact]
    public void TheReadOnlyKeyMustDifferFromTheFullKey()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => ApiKeySecurity.Validate(
            new WcsHostOptions { ApiKey = "same-key", ReadOnlyApiKey = "same-key" }));
        Assert.Contains("Wcs:ReadOnlyApiKey", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReadOnlyKeyCannotUseTheTrackedPlaceholder()
    {
        Assert.Throws<InvalidOperationException>(() => ApiKeySecurity.Validate(new WcsHostOptions
        {
            ApiKey = "full-key",
            ReadOnlyApiKey = "cambiami-prima-di-installare"
        }));
    }

    [Fact]
    public void AnAbsentReadOnlyKeyIsValid()
    {
        ApiKeySecurity.Validate(new WcsHostOptions { ApiKey = "full-key", ReadOnlyApiKey = " " });
    }

    [Fact]
    public async Task TheReadOnlyKeyCanReadStatus()
    {
        using var client = host.CreateReadOnlyClient();
        Assert.Equal(HttpStatusCode.OK,
            (await client.GetAsync(ApiRoutes.Prefix + ApiRoutes.Status)).StatusCode);
    }

    [Fact]
    public async Task TheReadOnlyKeyGetsForbiddenAndTheCommandIsNotCreated()
    {
        var workstation = $"READ-ONLY-{Guid.NewGuid()}";
        using var client = host.CreateReadOnlyClient();
        var response = await client.PostAsJsonAsync(
            ApiRoutes.Prefix + ApiRoutes.Commands,
            new CommandRequest(1001, WorkstationId: workstation));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        using var full = host.CreateAuthenticatedClient();
        var commands = await full.GetFromJsonAsync<CommandDto[]>(
            ApiRoutes.Prefix + ApiRoutes.Commands + "?workstationId=" + workstation);
        Assert.Empty(commands!);
    }

    /// <summary>
    /// <b>La chiave nell'URL resta rifiutata, ma il rifiuto dice perche'.</b> Un client costruito
    /// prima di 69e7036 - il Client di Kazam con un sottomodulo vecchio - manda la chiave come
    /// access_token sull'hub: prima riceveva un 401 identico a quello di una chiave sbagliata, e
    /// riprovava per sempre senza che nessuno capisse. Adesso il 401 porta la sua ragione, e il
    /// rimedio: aggiornare il client insieme all'host.
    /// </summary>
    [Fact]
    public async Task AKeyInTheQueryStringIsRefusedWithItsOwnReason()
    {
        using var client = host.CreateClient();
        var response = await client.PostAsync(
            ApiRoutes.Hub + "/negotiate?negotiateVersion=1&access_token=" + HostFixture.ApiKey, null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("non più accettata", body, StringComparison.Ordinal);
        Assert.Contains(ApiRoutes.ApiKeyHeader, body, StringComparison.Ordinal);
    }
}
