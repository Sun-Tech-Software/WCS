using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wcs.Api.Contracts;
using Wcs.Api.Contracts.Dtos;

namespace Wcs.ContractCheck;

/// <summary>
/// The OpenAPI document of the host API, <c>guide/contracts/wcs-host.openapi.json</c>. It starts from the
/// document the real host serves at <c>/openapi/v1.json</c> (the host started in memory, engine off),
/// then adds what ASP.NET Core cannot see: the contract version, the two keys, the edit lease, the
/// answers of the endpoints that return <c>IResult</c> (<see cref="HostApiAnswers"/>) and the answers
/// every operation shares, from the endpoint metadata of the same running host.
/// </summary>
internal static class HostApiDocument
{
    public const string FileName = "wcs-host.openapi.json";

    private const string ProblemJson = "application/problem+json";
    private const string ProblemSchema = "#/components/schemas/ProblemDetails";

    public static async Task<JsonObject> GenerateAsync()
    {
        var (raw, operations) = await ReadFromTheHostAsync();
        return Complete(raw, operations);
    }

    /// <summary>What the endpoint metadata says about an operation.</summary>
    internal sealed record EndpointFacts(string Method, string Path, bool Lease);

    private static async Task<(JsonObject Document, IReadOnlyDictionary<string, EndpointFacts> Operations)> ReadFromTheHostAsync()
    {
        // Outside a test project WebApplicationFactory finds no content root of its own: the host runs
        // from this folder, as an installed host runs from its own.
        Environment.SetEnvironmentVariable("ASPNETCORE_TEST_CONTENTROOT_WCS_HOST", AppContext.BaseDirectory);
        await using var host = new InMemoryHost();
        var document = JsonNode.Parse(await host.CreateClient().GetStringAsync("/openapi/v1.json"))!.AsObject();

        var operations = new Dictionary<string, EndpointFacts>();
        foreach (var endpoint in host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
        {
            if (endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName is not { } name) continue;
            if (!(endpoint.RoutePattern.RawText ?? "").StartsWith(ApiRoutes.Prefix, StringComparison.Ordinal)) continue;
            var lease = endpoint.Metadata.Any(m => m.GetType().Name == "RequiresEditPermissionMarker");
            var method = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Single();
            operations.Add(name, new EndpointFacts(method, endpoint.RoutePattern.RawText!, lease));
        }

        return (document, operations);
    }

    internal static JsonObject Complete(JsonObject raw, IReadOnlyDictionary<string, EndpointFacts> endpoints)
    {
        var document = new JsonObject
        {
            ["openapi"] = raw["openapi"]!.DeepClone(),
            ["info"] = new JsonObject
            {
                ["title"] = "Sun Tech WCS host API",
                ["version"] = HostApiContract.Version,
                ["description"] =
                    "The HTTP API of the WCS host under `" + ApiRoutes.Prefix + "`, generated from the running host by " +
                    "tools/Wcs.ContractCheck (do not edit by hand). `info.version` is `HostApiContract.Version` " +
                    "(WCS/Wcs.Api.Contracts): its major part changes on a change an existing client would not survive. " +
                    "Every refusal is `application/problem+json`; the titles are the host's own words, in Italian " +
                    "(`x-wcs-problem-titles` lists them per answer): program against the status code and, for 503, the " +
                    "`unavailable` field. The SignalR hub `" + ApiRoutes.Hub + "` is not described here " +
                    "(see guide/integration/host-api.md).",
                ["license"] = new JsonObject { ["name"] = "Apache-2.0", ["identifier"] = "Apache-2.0" },
            },
            ["x-wcs-provider"] = "wcs",
        };

        var paths = new JsonObject();
        var areas = new SortedSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>();
        foreach (var (path, item) in raw["paths"]!.AsObject())
        {
            var operations = new JsonObject();
            foreach (var (method, node) in item!.AsObject())
            {
                var operation = node!.AsObject();
                var id = operation["operationId"]?.GetValue<string>()
                         ?? throw new InvalidOperationException($"{method.ToUpperInvariant()} {path} has no name (WithName).");
                if (!endpoints.TryGetValue(id, out var facts))
                    throw new InvalidOperationException($"Operation {id} is in the document but not among the host's endpoints.");
                seen.Add(id);

                var area = AreaOf(path);
                areas.Add(area);
                operations[method] = CompleteOperation(id, method, operation, facts, area);
            }
            paths[path] = operations;
        }

        var undocumented = endpoints.Keys.Except(seen).ToList();
        if (undocumented.Count > 0)
            throw new InvalidOperationException("Endpoints missing from the OpenAPI document: " + string.Join(", ", undocumented));
        var stale = HostApiAnswers.ByOperation.Keys.Concat(HostApiAnswers.EngineState.Keys)
            .Concat(HostApiAnswers.NeedTheWarehouse).Concat(HostApiAnswers.ReadTheHistory)
            .Concat(HostApiAnswers.OutsideTheEngineGate).Except(seen).ToList();
        if (stale.Count > 0)
            throw new InvalidOperationException("HostApiAnswers names operations the host does not have: " + string.Join(", ", stale));

        document["tags"] = new JsonArray([.. areas.Select(a => (JsonNode)new JsonObject { ["name"] = a })]);
        document["paths"] = paths;

        var components = raw["components"]!.DeepClone().AsObject();
        DescribeEnumerations(components["schemas"]!.AsObject());
        components["securitySchemes"] = new JsonObject
        {
            ["fullKey"] = new JsonObject
            {
                ["type"] = "apiKey",
                ["in"] = "header",
                ["name"] = ApiRoutes.ApiKeyHeader,
                ["description"] = "The full key, host setting `Wcs:ApiKey` (required): every operation.",
            },
            ["readOnlyKey"] = new JsonObject
            {
                ["type"] = "apiKey",
                ["in"] = "header",
                ["name"] = ApiRoutes.ApiKeyHeader,
                ["description"] = "The read-only key, host setting `Wcs:ReadOnlyApiKey` (optional): GET operations and the hub; " +
                                  "anything else is 403. The key is read from the header only: `access_token` in the query is refused with 401.",
            },
        };
        document["components"] = components;
        return document;
    }

    private static JsonObject CompleteOperation(string id, string method, JsonObject source, EndpointFacts facts, string area)
    {
        var operation = new JsonObject { ["tags"] = new JsonArray(area), ["operationId"] = id };
        var isRead = method == "get";

        var parameters = source["parameters"]?.DeepClone().AsArray() ?? [];
        if (facts.Lease)
            parameters.Add(new JsonObject
            {
                ["name"] = ApiRoutes.EditHolderHeader,
                ["in"] = "header",
                ["required"] = true,
                ["description"] = "The holder of the edit lease (`POST " + ApiRoutes.Prefix + ApiRoutes.EditLock + "`); without it, or with another name, 423.",
                ["schema"] = new JsonObject { ["type"] = "string" },
            });
        if (parameters.Count > 0) operation["parameters"] = parameters;

        if (source["requestBody"] is { } body) operation["requestBody"] = body.DeepClone();
        else if (id == "ReadLegacyDiagramGeometry")
            operation["requestBody"] = new JsonObject
            {
                ["description"] = "An old drawing, `.edd` or `.xml`, as it is on disk.",
                ["required"] = true,
                ["content"] = new JsonObject
                {
                    ["application/octet-stream"] = new JsonObject
                    {
                        ["schema"] = new JsonObject { ["type"] = "string", ["contentMediaType"] = "application/octet-stream" },
                    },
                },
            };

        operation["responses"] = Responses(id, source, facts, isRead, operation);

        operation["security"] = isRead
            ? new JsonArray(new JsonObject { ["fullKey"] = new JsonArray() }, new JsonObject { ["readOnlyKey"] = new JsonArray() })
            : new JsonArray(new JsonObject { ["fullKey"] = new JsonArray() });
        operation["x-wcs-edit-lease"] = facts.Lease;
        if (HostApiAnswers.EngineState.TryGetValue(id, out var state)) operation["x-wcs-engine-state"] = state;
        return operation;
    }

    private static JsonObject Responses(string id, JsonObject source, EndpointFacts facts, bool isRead, JsonObject operation)
    {
        var answers = new List<HostAnswer>();
        var declared = source["responses"]!.AsObject();
        var bare = declared.Count == 1 && declared["200"] is JsonObject ok && ok["content"] is null;
        var described = HostApiAnswers.ByOperation.GetValueOrDefault(id);
        if (described != null)
            answers.AddRange(described);
        else if (bare)
            throw new InvalidOperationException(
                $"Operation {id} returns IResult and its answers are not described: add it to HostApiAnswers.ByOperation.");

        var responses = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);
        // What the endpoint declares itself (a typed return, or Produces/ProducesProblem), unless it is the
        // bare 200 that ASP.NET writes for an IResult.
        if (!bare)
            foreach (var (status, response) in declared)
                responses[status] = response!.DeepClone().AsObject();

        if (!isRead) answers.Add(new(403, "The read-only key on a write.", Titles: [HostProblemTitles.ReadOnlyKey]));
        answers.Add(new(401, "No key, a wrong key, or the key in the `access_token` query parameter.",
            Titles: [HostProblemTitles.KeyMissingOrWrong, HostProblemTitles.KeyInUrl]));
        if (facts.Lease)
            answers.Add(new(423, "This write changes the plant and needs the edit lease: nobody holds it, or someone else does (the detail names them).",
                Titles: [HostProblemTitles.NobodyHoldsTheLease, HostProblemTitles.SomeoneElseHoldsTheLease]));
        if (HostApiAnswers.EngineState.TryGetValue(id, out var state))
            answers.Add(new(409, $"The engine is not in the state this write needs ({state}): the detail names the state.",
                Titles: [HostProblemTitles.EngineNotStopped]));
        if (!HostApiAnswers.OutsideTheEngineGate.Contains(id))
            answers.Add(new(503, "The engine is not loaded (`unavailable`: `" + ServiceUnavailableProblems.EngineNotLoaded +
                                 "`; the reason is `engineLoadError` in `GET " + ApiRoutes.Prefix + ApiRoutes.Status +
                                 "`), or its database is not open (no `unavailable` field).",
                Titles: [HostProblemTitles.EngineNotLoaded, HostProblemTitles.EngineStoreUnavailable]));
        if (HostApiAnswers.NeedTheWarehouse.Contains(id))
            answers.Add(new(503, "The WMS did not answer, or is not configured (`unavailable`: `" + ServiceUnavailableProblems.WarehouseUnreachable + "`).",
                Titles: [MissionDestinationProblems.WarehouseUnavailableTitle]));
        if (HostApiAnswers.ReadTheHistory.Contains(id))
            answers.Add(new(400, "`from` starts before the first month the history keeps (`historyFrom` in the status).",
                Titles: [HostProblemTitles.HistoryRange]));
        if (operation["requestBody"] != null || operation["parameters"] is JsonArray { Count: > 0 })
            answers.Add(new(400, "The body or a parameter cannot be read (not JSON, a wrong type, a required one missing): in the Development " +
                                 "environment the title is `" + HostProblemTitles.Unreadable + "` with the parser's reason in `detail`, " +
                                 "elsewhere the framework's `" + HostProblemTitles.BadRequest + "`.",
                Titles: [HostProblemTitles.Unreadable, HostProblemTitles.BadRequest]));

        foreach (var answer in answers)
        {
            var key = answer.Status.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!responses.TryGetValue(key, out var response))
                responses[key] = response = new JsonObject { ["description"] = answer.Description };
            else if (response["description"]?.GetValue<string>() is { } existing && existing != ReasonPhrases.GetReasonPhrase(answer.Status))
                response["description"] = existing + " " + answer.Description;
            else
                response["description"] = answer.Description;

            if (answer.Location)
                response["headers"] = new JsonObject
                {
                    ["Location"] = new JsonObject
                    {
                        ["description"] = "The path of the resource.",
                        ["schema"] = new JsonObject { ["type"] = "string" },
                    },
                };

            if (answer.Json == null && !answer.IsProblem && answer.ContentType == null) continue;
            if (response["content"] is not JsonObject content) response["content"] = content = new JsonObject();

            if (answer.IsProblem)
            {
                content.TryAdd(ProblemJson, new JsonObject { ["schema"] = new JsonObject { ["$ref"] = ProblemSchema } });
                if (response["x-wcs-problem-titles"] is not JsonArray titles) response["x-wcs-problem-titles"] = titles = [];
                foreach (var title in answer.Titles!)
                    if (!titles.Any(t => t!.GetValue<string>() == title)) titles.Add(title);
            }
            else if (answer.Json != null)
                content["application/json"] = new JsonObject { ["schema"] = SchemaOf(answer.Json, answer.Array) };
            else
                content[answer.ContentType!] = new JsonObject { ["schema"] = new JsonObject { ["type"] = "string" } };
        }

        var result = new JsonObject();
        foreach (var (status, response) in responses) result[status] = response;
        return result;
    }

    private static JsonNode SchemaOf(Type type, bool array)
    {
        JsonNode item = type == typeof(bool)
            ? new JsonObject { ["type"] = "boolean" }
            : new JsonObject { ["$ref"] = "#/components/schemas/" + type.Name };
        return array ? new JsonObject { ["type"] = "array", ["items"] = item } : item;
    }

    /// <summary><c>/api/v1/plant/items/{id}</c> -> <c>plant</c>.</summary>
    private static string AreaOf(string path) =>
        path[ApiRoutes.Prefix.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries)[0];

    /// <summary>
    /// The numeric enumerations are bare <c>{"type":"integer"}</c> in ASP.NET Core's document: the values
    /// and their names are added from the CLR enumeration of the same name.
    /// </summary>
    private static void DescribeEnumerations(JsonObject schemas)
    {
        var enums = typeof(HostApiContract).Assembly.GetReferencedAssemblies().Select(Assembly.Load)
            .Append(typeof(HostApiContract).Assembly)
            .SelectMany(a => a.GetTypes()).Where(t => t.IsEnum)
            .GroupBy(t => t.Name).ToDictionary(g => g.Key, g => g.ToList());

        foreach (var (name, node) in schemas)
        {
            if (node is not JsonObject { Count: 1 } schema || schema["type"]?.GetValue<string>() != "integer") continue;
            if (!enums.TryGetValue(name, out var candidates) || candidates.Count != 1)
                throw new InvalidOperationException($"No single enumeration named {name} for the schema of the same name.");
            EnumSchemas.AddValues(schema, candidates[0]);
        }
    }

    private sealed class InMemoryHost : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Wcs:ApiKey"] = "contract-check-key-0123456789",
                ["Wcs:StartEngine"] = "false"
            }));
            // The bodies that only IResult endpoints answer are not in ASP.NET Core's components: they are
            // generated here, by the same schema service, so they look like the others.
            builder.ConfigureServices(services => services.Configure<OpenApiOptions>("v1", options =>
                options.AddDocumentTransformer(async (document, context, cancellationToken) =>
                {
                    var bodies = HostApiAnswers.ByOperation.Values.SelectMany(a => a)
                        .Select(a => a.Json).OfType<Type>().Where(t => t != typeof(bool)).Distinct();
                    foreach (var type in bodies)
                    {
                        if (document.Components?.Schemas?.ContainsKey(type.Name) == true) continue;
                        var schema = await context.GetOrCreateSchemaAsync(type, null, cancellationToken);
                        document.AddComponent(type.Name, schema);
                    }
                })));
        }
    }
}
