using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Wcs.Api.Contracts;
using Wcs.Api.Contracts.Dtos;
using Wcs.Engine;
using Wcs.Engine.Persistence;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.ContractCheck.Test;

/// <summary>
/// The host document promises titles and gates that the host writes itself (<see cref="HostProblemTitles"/>,
/// <see cref="HostApiAnswers.OutsideTheEngineGate"/>): each is asked of a host started in memory, so a title
/// changed in the host fails here instead of going stale in the contract.
/// </summary>
[Collection("host")]
public sealed class HostProblemTitlesTest : IDisposable
{
    private const string FullKey = "titles-full-key";
    private const string ReadOnlyKey = "titles-read-only-key";

    private readonly NavigatorStatusTypes _status = Navigator.NavStatus;
    private readonly object? _store = Engine("Store");
    private readonly ProbeHost _host = new();

    public void Dispose()
    {
        _host.Dispose();
        SetEngine(nameof(Navigator.LoadError), null);
        SetEngine(nameof(Navigator.NavStatus), _status);
        SetEngine("Store", _store);
    }

    private sealed class ProbeHost : WebApplicationFactory<Program>
    {
        public ProbeHost() =>
            Environment.SetEnvironmentVariable("ASPNETCORE_TEST_CONTENTROOT_WCS_HOST", AppContext.BaseDirectory);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Wcs:ApiKey"] = FullKey,
                ["Wcs:ReadOnlyApiKey"] = ReadOnlyKey,
                ["Wcs:StartEngine"] = "false",
            }));
        }
    }

    private HttpClient Client(string? key, string? holder = null)
    {
        var client = _host.CreateClient();
        if (key != null) client.DefaultRequestHeaders.Add(ApiRoutes.ApiKeyHeader, key);
        if (holder != null) client.DefaultRequestHeaders.Add(ApiRoutes.EditHolderHeader, holder);
        return client;
    }

    private static async Task<JsonObject> Problem(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
    }

    private static async Task AssertTitle(HttpResponseMessage response, HttpStatusCode status, string title) =>
        Assert.Equal(title, (await Problem(response, status))["title"]?.GetValue<string>());

    /// <summary>
    /// The engine state the gates read (<c>Navigator.LoadError</c>, <c>NavStatus</c>, <c>Store</c>) is set by the
    /// engine itself, through internal members: the test reaches them by reflection rather than widening
    /// the engine's InternalsVisibleTo for a tool.
    /// </summary>
    private static void SetEngine(string name, object? value)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        if (typeof(Navigator).GetProperty(name, flags) is { } property) property.SetValue(null, value);
        else typeof(Navigator).GetField(name, flags)!.SetValue(null, value);
    }

    private static object? Engine(string name)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        return typeof(Navigator).GetProperty(name, flags)?.GetValue(null) ?? typeof(Navigator).GetField(name, flags)?.GetValue(null);
    }

    private const string Prefix = ApiRoutes.Prefix;

    [Fact]
    public async Task TheKeyGatesAnswerWithTheirTitles()
    {
        await AssertTitle(await Client(null).GetAsync(Prefix + ApiRoutes.Status), HttpStatusCode.Unauthorized, HostProblemTitles.KeyMissingOrWrong);
        await AssertTitle(await Client(null).GetAsync(Prefix + ApiRoutes.Status + "?access_token=" + FullKey),
            HttpStatusCode.Unauthorized, HostProblemTitles.KeyInUrl);
        await AssertTitle(await Client(ReadOnlyKey).PostAsJsonAsync(Prefix + ApiRoutes.Commands, new { commandType = 1001 }),
            HttpStatusCode.Forbidden, HostProblemTitles.ReadOnlyKey);
    }

    [Fact]
    public async Task TheLeaseGateAnswersWithItsTwoTitles()
    {
        var decoration = new PlantDecorationDto("<decoration/>");
        await AssertTitle(await Client(FullKey).PutAsJsonAsync(Prefix + ApiRoutes.PlantDecoration, decoration),
            HttpStatusCode.Locked, HostProblemTitles.NobodyHoldsTheLease);

        var owner = Client(FullKey);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(Prefix + ApiRoutes.EditLock, new { holder = "titles-owner" })).StatusCode);
        try
        {
            await AssertTitle(await Client(FullKey, holder: "titles-intruder").PutAsJsonAsync(Prefix + ApiRoutes.PlantDecoration, decoration),
                HttpStatusCode.Locked, HostProblemTitles.SomeoneElseHoldsTheLease);
        }
        finally
        {
            await owner.DeleteAsync(Prefix + ApiRoutes.EditLock + "?holder=titles-owner");
        }
    }

    [Fact]
    public async Task TheEngineGatesAnswerWithTheirTitles()
    {
        // No database: the 503 without the unavailable field.
        SetEngine("Store", null);
        await AssertTitle(await Client(FullKey).GetAsync(Prefix + ApiRoutes.ItemsConfig),
            HttpStatusCode.ServiceUnavailable, HostProblemTitles.EngineStoreUnavailable);

        // A database and the engine in Run: a plant write is 409.
        using var store = WcsDatabase.InMemory();
        SetEngine("Store", store);
        SetEngine(nameof(Navigator.NavStatus), NavigatorStatusTypes.Run);
        var owner = Client(FullKey, holder: "titles-engine");
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(Prefix + ApiRoutes.EditLock, new { holder = "titles-engine" })).StatusCode);
        try
        {
            await AssertTitle(await owner.PutAsJsonAsync(Prefix + ApiRoutes.PlantDecoration, new PlantDecorationDto("<decoration/>")),
                HttpStatusCode.Conflict, HostProblemTitles.EngineNotStopped);
        }
        finally
        {
            await owner.DeleteAsync(Prefix + ApiRoutes.EditLock + "?holder=titles-engine");
            SetEngine(nameof(Navigator.NavStatus), NavigatorStatusTypes.Stop);
        }

        // The engine not loaded: 503 with unavailable, except on the operations outside the gate.
        SetEngine(nameof(Navigator.LoadError), "titles test");
        var problem = await Problem(await Client(FullKey).GetAsync(Prefix + ApiRoutes.Items), HttpStatusCode.ServiceUnavailable);
        Assert.Equal(HostProblemTitles.EngineNotLoaded, problem["title"]?.GetValue<string>());
        Assert.Equal(ServiceUnavailableProblems.EngineNotLoaded, problem[ServiceUnavailableProblems.ReasonExtension]?.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, (await Client(FullKey).GetAsync(Prefix + ApiRoutes.Status)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Client(FullKey).DeleteAsync(Prefix + ApiRoutes.EditLock + "?holder=nobody")).StatusCode);
        Assert.Contains("GetStatus", HostApiAnswers.OutsideTheEngineGate);
        Assert.Contains("ReleaseEditLock", HostApiAnswers.OutsideTheEngineGate);
    }

    [Fact]
    public async Task AnUnreadableBodyAndAnEmptyNotFoundAreProblemsWithTheirTitles()
    {
        await AssertTitle(
            await Client(FullKey).PostAsync(Prefix + ApiRoutes.Commands, new StringContent("not json", Encoding.UTF8, "application/json")),
            HttpStatusCode.BadRequest, HostProblemTitles.Unreadable);
        await AssertTitle(await Client(FullKey).GetAsync($"{Prefix}{ApiRoutes.Missions}/{Guid.NewGuid()}"),
            HttpStatusCode.NotFound, HostProblemTitles.NotFound);
    }
}
