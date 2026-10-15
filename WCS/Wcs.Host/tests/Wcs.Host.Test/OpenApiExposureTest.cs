using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Wcs.Api.Contracts;
using Log.Interface;
using Wcs.Engine;
using Wcs.Engine.Api;
using Wcs.Engine.Persistence;
using Xunit;

namespace Wcs.Host.Test;

/// <summary>
/// Il contratto OpenAPI si legge senza chiave, ed e' una scelta -- ma non in produzione.
/// </summary>
/// <remarks>
/// <c>MapOpenApi</c> non era condizionato all'ambiente e il middleware della chiave lascia passare
/// <c>/openapi</c> apposta. L'host ascolta su <c>0.0.0.0:5580</c>: su una rete aziendale chiunque
/// veda la porta leggeva l'elenco intero delle rotte. Non e' un dato di esercizio, ma e' la mappa
/// per arrivarci.
/// </remarks>
[Collection("host")]
public sealed class OpenApiExposureTest(HostFixture host) : IDisposable
{
    // Un secondo host nello stesso processo si porta via i quattro cablaggi statici del motore
    // che EngineHostedService imposta PRIMA della guardia su StartEngine, e allo spegnimento li
    // azzera: senza questo salvataggio l'host della collezione resterebbe senza sink e i collaudi
    // dell'hub che girano dopo fallirebbero. E' la stessa cura di EngineWarehouseWiringTest.
    private readonly IEngineEventSink? _previousSink = Navigator.EventSink;
    private readonly Action<Exception>? _previousErrorLog = Navigator.HostErrorLog;
    private readonly IWriter? _previousEngineLog = Navigator.EventLogWriter;
    private readonly Action<string>? _previousNotice = WcsHistoryDatabase.Notice;
    private readonly Action<string>? _previousPlcLogNotice = WcsPlcLogDatabase.Notice;

    public void Dispose()
    {
        Navigator.EventSink = _previousSink;
        Navigator.HostErrorLog = _previousErrorLog;
        Navigator.SetEventLogWriter(_previousEngineLog);
        WcsHistoryDatabase.Notice = _previousNotice;
        WcsPlcLogDatabase.Notice = _previousPlcLogNotice;
    }

    [Fact]
    public async Task OutsideProductionTheContractIsReadableWithoutAKey()
    {
        var response = await host.CreateClient().GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GeometryWritesExposeTheVersionedBodyAndEveryDocumentedOutcome()
    {
        var document = JsonNode.Parse(await host.CreateClient().GetStringAsync("/openapi/v1.json"))!;
        var paths = document["paths"]!.AsObject();

        var single = paths[$"{ApiRoutes.Prefix}{ApiRoutes.Items}/{{id}}/geometry"]!["put"]!;
        var bulk = paths[$"{ApiRoutes.Prefix}{ApiRoutes.ItemsGeometry}"]!["put"]!;

        Assert.EndsWith("/PlantItemGeometryUpdateDto",
            single["requestBody"]!["content"]!["application/json"]!["schema"]!["$ref"]!.GetValue<string>());
        Assert.EndsWith("/PlantItemGeometryUpdateDto",
            bulk["requestBody"]!["content"]!["application/json"]!["schema"]!["items"]!["$ref"]!.GetValue<string>());

        AssertResponses(single, "200", "400", "404", "409", "423");
        AssertResponses(bulk, "200", "400", "404", "409", "423");
    }

    private static void AssertResponses(JsonNode operation, params string[] expected)
    {
        var responses = operation["responses"]!.AsObject();
        foreach (var status in expected)
            Assert.True(responses.ContainsKey(status), $"Manca la risposta OpenAPI {status}");
    }

    [Fact]
    public async Task InProductionTheContractIsNotThere()
    {
        using var production = new ProductionHost();

        var response = await production.CreateClient().GetAsync("/openapi/v1.json");

        // 404 e non 401: il middleware continua a far passare /openapi senza chiave, ma dietro
        // non c'e' piu' niente. Con la chiave giusta la risposta e' la stessa.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var authenticated = production.CreateClient();
        authenticated.DefaultRequestHeaders.Add(ApiRoutes.ApiKeyHeader, HostFixture.ApiKey);
        Assert.Equal(HttpStatusCode.NotFound, (await authenticated.GetAsync("/openapi/v1.json")).StatusCode);
    }

    /// <summary>
    /// Lo stesso host della suite, in ambiente Production e con il motore spento.
    /// </summary>
    private sealed class ProductionHost : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Wcs:ApiKey"] = HostFixture.ApiKey,
                    ["Wcs:StartEngine"] = "false"
                }));
        }
    }
}
