using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Wcs.Engine.Persistence;
using Wcs.Wms.Client;
using Wcs.Wms.Contracts;

namespace Wcs.ContractCheck;

/// <summary>
/// The OpenAPI document of the WCS -> WMS contract, <c>guide/contracts/wms-contract.openapi.json</c>: what a
/// WMS must serve. Nothing in it is written by hand from the guide: every operation is a method of
/// <see cref="IWarehouse"/> called on the real <see cref="HttpWarehouse"/> against a recording handler, and
/// the document says what that call put on the wire (method, path, query, body and its casing) and how
/// the client read the answers it was given (a 404 read as "none" or raised as a failure). The schemas come
/// from <see cref="JsonSchemaExporter"/> with the serializer options the client really uses: the default
/// ones (PascalCase) for the bodies it writes with <c>JsonSerializer.Serialize</c>, the web ones
/// (camelCase) for those it writes with <c>JsonContent</c> and for the answers it reads.
/// </summary>
/// <remarks>
/// The only knowledge added by hand is <see cref="DurableQueues"/>: which routes are sent from a durable
/// queue of <c>wcs.db</c>. Its table names are <c>nameof</c> of <see cref="WcsDbContext"/>, so a queue
/// renamed or removed does not compile here.
/// </remarks>
internal static class WmsContractDocument
{
    public const string FileName = "wms-contract.openapi.json";

    internal const string PascalCase = "PascalCase";
    internal const string CamelCase = "camelCase";

    /// <summary>The routes sent from a durable list of <c>wcs.db</c>, by <see cref="IWarehouse"/> method.</summary>
    internal static readonly IReadOnlyDictionary<string, (string Table, string Behaviour)> DurableQueues =
        new Dictionary<string, (string, string)>
        {
            [nameof(IWarehouse.AllocateLoadUnitAsync)] = (nameof(WcsDbContext.PendingWarehouseTransitions),
                "Sent from the durable queue when the load unit has moved on the field (at least once, retried 2 s, 4 s, 6 s ... up to 30 s, " +
                "in every engine state). A permanent refusal (4xx) parks the row, which holds back the later facts about the same load unit " +
                "until a person resolves it. Also sent directly, with `OperationId` = the empty GUID and no receipt, when a load unit arrives " +
                "on a device linked to a cell."),
            [nameof(IWarehouse.ReleaseLoadUnitAsync)] = (nameof(WcsDbContext.PendingWarehouseTransitions),
                "Sent from the durable queue (at least once, retried 2 s, 4 s, 6 s ... up to 30 s, in every engine state). A permanent refusal " +
                "(4xx) parks the row, which holds back the later facts about the same load unit, and every later allocation, until a person resolves it."),
            [nameof(IWarehouse.ReportExtractionOutcomesAsync)] = (nameof(WcsDbContext.PendingExtractionOutcomes),
                "The `Extracted` outcome is sent from the durable queue (at least once, retried 2 s, 4 s, 6 s ... up to 30 s). It has no " +
                "`OperationId`: it is idempotent by content. On a permanent refusal the batch is resent row by row; a row refused alone is logged and dropped."),
            [nameof(IWarehouse.ReportPickedExtractionBecameDepositAsync)] = (nameof(WcsDbContext.PendingPickedExtractionReports),
                "Sent from the durable queue (at least once, retried 2 s, 4 s, 6 s ... up to 30 s). A permanent refusal (4xx, the 409 included) parks the row."),
            [nameof(IWarehouse.ReleaseLoadUnitCellAsync)] = (nameof(WcsDbContext.PendingCellGiveBacks),
                "Sent from the durable queue (at least once, retried 2 s, 4 s, 6 s ... up to 30 s). A permanent refusal (4xx, the 409 included) parks the row."),
            [nameof(IWarehouse.SetCellLockStateAsync)] = (nameof(WcsDbContext.CellsToMarkFull),
                "After error 67 (the machine refused to store into the cell) a request the WMS did not receive is kept and retried in Run " +
                "until it answers; a 4xx drops it with a warning."),
            [nameof(IWarehouse.ReleaseStorageReservationAsync)] = (nameof(WcsDbContext.ReservationsToGiveBack),
                "The reservations of missions closed while the engine reloaded are kept and released once at the next Run; a failure leaves " +
                "them to the WMS's own expiry."),
        };

    /// <summary>What <c>JsonSerializer.Serialize(body)</c> writes: PascalCase.</summary>
    private static readonly JsonSerializerOptions Written = Options(JsonSerializerDefaults.General);

    /// <summary>What <c>JsonContent.Create(body)</c> writes: camelCase, numbers as numbers.</summary>
    private static readonly JsonSerializerOptions WrittenCamel = Options(JsonSerializerDefaults.Web, JsonNumberHandling.Strict);

    /// <summary>How <c>ReadFromJsonAsync</c> reads an answer: any casing, numbers also as strings.</summary>
    private static readonly JsonSerializerOptions Web = Options(JsonSerializerDefaults.Web);

    private static JsonSerializerOptions Options(JsonSerializerDefaults defaults, JsonNumberHandling? numbers = null)
    {
        // The schemas describe the wire, not the constructors: without the constructor the exporter does not
        // try to write the C# default of an optional parameter (`Guid OperationId = default`), which it
        // cannot serialize.
        var resolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers =
            {
                info =>
                {
                    if (info.Kind == JsonTypeInfoKind.Object && IsContractRecord(info.Type))
                        info.CreateObject = () => System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(info.Type);
                },
            },
        };
        var options = new JsonSerializerOptions(defaults) { TypeInfoResolver = resolver };
        if (numbers is { } handling) options.NumberHandling = handling;
        options.MakeReadOnly();
        return options;
    }

    /// <summary>One operation as the client really performs it.</summary>
    internal sealed record Operation(
        MethodInfo Method, string HttpMethod, string Route, IReadOnlyList<(string Name, ParameterInfo Parameter)> Query,
        Type? RequestType, string? Casing, Type? ResponseType, bool NotFoundIsNone, IReadOnlyList<string> NotSentWhenEmpty,
        bool Optional = false);

    public static async Task<JsonObject> GenerateAsync()
    {
        var operations = new List<Operation>();
        foreach (var method in typeof(IWarehouse).GetMethods().OrderBy(m => m.Name, StringComparer.Ordinal))
            operations.Add(await ObserveAsync(method));
        return Build(operations);
    }

    // ---------------------------------------------------------------- observing the client

    internal static async Task<Operation> ObserveAsync(MethodInfo method)
    {
        var failing = await CallAsync(method, HttpStatusCode.InternalServerError, empty: false);
        if (failing.Request is not { } request)
            throw new InvalidOperationException($"{method.Name} sent nothing with sample arguments.");
        if (failing.Outcome != Outcome.Failure)
            throw new InvalidOperationException($"{method.Name} did not raise WarehouseUnavailableException on a 500.");

        var notFound = await CallAsync(method, HttpStatusCode.NotFound, empty: false);
        var notFoundIsNone = notFound.Outcome == Outcome.ReturnedNull;
        if (!notFoundIsNone && notFound.Outcome != Outcome.Failure)
            throw new InvalidOperationException($"{method.Name} neither failed nor answered none on a 404.");

        // Optional (item 55): the client reads a 501 too as «not offered», so a WMS that has not
        // implemented the operation - and answers 404 or 501, as the guide says - keeps working.
        var optional = notFoundIsNone &&
                       (await CallAsync(method, HttpStatusCode.NotImplemented, empty: false)).Outcome == Outcome.ReturnedNull;

        var notSent = new List<string>();
        foreach (var parameter in method.GetParameters().Where(p => p.ParameterType == typeof(string) || IsList(p.ParameterType)))
            if ((await CallAsync(method, HttpStatusCode.InternalServerError, empty: true, emptied: parameter)).Request is null)
                notSent.Add(parameter.Name!);

        var uri = new Uri("http://wms" + request.PathAndQuery);
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        if (!path.StartsWith(WarehouseRoutes.Prefix + "/", StringComparison.Ordinal))
            throw new InvalidOperationException($"{method.Name} called {path}, outside {WarehouseRoutes.Prefix}.");
        var route = RouteOf(method, path[(WarehouseRoutes.Prefix.Length + 1)..]);

        var query = new List<(string, ParameterInfo)>();
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = pair.Split('=')[0];
            var parameter = method.GetParameters().SingleOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidOperationException($"{method.Name}: query parameter {name} is not a parameter of the method.");
            query.Add((name, parameter));
        }

        Type? requestType = null;
        string? casing = null;
        if (request.Body is { } body)
            (requestType, casing) = ReadBody(method, request, body);

        var returned = method.ReturnType.IsGenericType ? method.ReturnType.GetGenericArguments()[0] : null;
        return new Operation(method, request.Method, route, query, requestType, casing, returned, notFoundIsNone, notSent, optional);
    }

    private static (Type, string) ReadBody(MethodInfo method, RecordedRequest request, string body)
    {
        var names = JsonNode.Parse(body)!.AsObject().Select(p => p.Key).ToList();
        var type = request.ObjectType
                   ?? method.GetParameters().Select(p => p.ParameterType).SingleOrDefault(IsContractRecord)
                   ?? SingleRecordWithNames(method, names);
        var pascal = PropertyNames(type, Written);
        var camel = PropertyNames(type, Web);
        var casing = names.SequenceEqual(pascal) ? PascalCase
            : names.SequenceEqual(camel) ? CamelCase
            : throw new InvalidOperationException($"{method.Name}: the body's names ({string.Join(", ", names)}) are neither casing of {type.Name}.");
        return (type, casing);
    }

    private static Type SingleRecordWithNames(MethodInfo method, IReadOnlyList<string> names)
    {
        var matches = typeof(IWarehouse).Assembly.GetExportedTypes().Where(IsContractRecord)
            .Where(t => t.Name.EndsWith("Request", StringComparison.Ordinal))
            .Where(t => PropertyNames(t, Written).SequenceEqual(names) || PropertyNames(t, Web).SequenceEqual(names))
            .ToList();
        return matches.Count == 1
            ? matches[0]
            : throw new InvalidOperationException($"{method.Name}: {matches.Count} contract types have the names of its body.");
    }

    private static List<string> PropertyNames(Type type, JsonSerializerOptions options) =>
        [.. options.GetTypeInfo(type).Properties.Select(p => p.Name)];

    private static bool IsContractRecord(Type type) =>
        type.Assembly == typeof(IWarehouse).Assembly && type is { IsClass: true, IsAbstract: false } &&
        type.Namespace == typeof(IWarehouse).Namespace && !typeof(Exception).IsAssignableFrom(type);

    private static bool IsList(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>);

    /// <summary>The <see cref="WarehouseRoutes"/> template a path was filled from; a literal route wins over a template.</summary>
    private static string RouteOf(MethodInfo method, string path)
    {
        var candidates = AllRoutes()
            .Where(route => Matches(route, path))
            .OrderBy(route => route.Count(c => c == '{'))
            .ToList();
        if (candidates.Count == 0) throw new InvalidOperationException($"{method.Name}: {path} matches no WarehouseRoutes constant.");
        if (candidates.Count > 1 && candidates[0].Count(c => c == '{') == candidates[1].Count(c => c == '{'))
            throw new InvalidOperationException($"{method.Name}: {path} matches {candidates[0]} and {candidates[1]}.");
        return candidates[0];
    }

    internal static IReadOnlyList<string> AllRoutes() =>
        [.. typeof(WarehouseRoutes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.Name is not (nameof(WarehouseRoutes.Prefix) or nameof(WarehouseRoutes.ApiKeyHeader)))
            .Select(f => (string)f.GetRawConstantValue()!)];

    private static bool Matches(string route, string path)
    {
        var routeSegments = route.Split('/');
        var pathSegments = path.Split('/');
        return routeSegments.Length == pathSegments.Length &&
               routeSegments.Zip(pathSegments).All(s => s.First.StartsWith('{') || s.First == s.Second);
    }

    private enum Outcome { Answered, ReturnedNull, Failure }

    private sealed record RecordedRequest(string Method, string PathAndQuery, string? Body, Type? ObjectType);

    private sealed record Call(RecordedRequest? Request, Outcome Outcome);

    private static async Task<Call> CallAsync(MethodInfo method, HttpStatusCode status, bool empty, ParameterInfo? emptied = null)
    {
        var handler = new RecordingHandler(status);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://wms") };
        var warehouse = new HttpWarehouse(http, new HttpWarehouseOptions { ApiKey = "contract-check" });
        var arguments = method.GetParameters()
            .Select(p => empty && p == emptied ? Samples.Empty(p.ParameterType) : Samples.For(p.ParameterType))
            .ToArray();

        try
        {
            var task = (Task)method.Invoke(warehouse, arguments)!;
            await task;
            var result = task.GetType().GetProperty("Result")?.GetValue(task);
            return new Call(handler.Recorded, method.ReturnType.IsGenericType && result is null ? Outcome.ReturnedNull : Outcome.Answered);
        }
        catch (WarehouseUnavailableException)
        {
            return new Call(handler.Recorded, Outcome.Failure);
        }
    }

    private sealed class RecordingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public RecordedRequest? Recorded { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Headers.GetValues(WarehouseRoutes.ApiKeyHeader).SingleOrDefault() != "contract-check")
                throw new InvalidOperationException($"{request.RequestUri} was sent without {WarehouseRoutes.ApiKeyHeader}.");
            Recorded = new RecordedRequest(
                request.Method.Method.ToLowerInvariant(),
                request.RequestUri!.IsAbsoluteUri ? request.RequestUri.PathAndQuery : request.RequestUri.OriginalString,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken),
                (request.Content as JsonContent)?.ObjectType);
            return new HttpResponseMessage(status) { Content = new StringContent("") };
        }
    }

    // ---------------------------------------------------------------- the document

    private static JsonObject Build(IReadOnlyList<Operation> operations)
    {
        var schemas = new SchemaSet();
        var paths = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);

        foreach (var operation in operations.OrderBy(o => o.Route, StringComparer.Ordinal))
        {
            var path = WarehouseRoutes.Prefix + "/" + operation.Route;
            if (!paths.TryGetValue(path, out var item)) paths[path] = item = new JsonObject();
            if (item.ContainsKey(operation.HttpMethod))
                throw new InvalidOperationException($"Two operations on {operation.HttpMethod.ToUpperInvariant()} {path}.");
            item[operation.HttpMethod] = BuildOperation(operation, schemas);
        }

        var unused = AllRoutes().Except(operations.Select(o => o.Route)).ToList();
        if (unused.Count > 0)
            throw new InvalidOperationException("WarehouseRoutes constants no IWarehouse method calls: " + string.Join(", ", unused));

        var document = new JsonObject
        {
            ["openapi"] = "3.1.1",
            ["info"] = new JsonObject
            {
                ["title"] = "Sun Tech WCS: the WMS contract",
                ["version"] = WarehouseContract.Version,
                ["description"] = Description(),
                ["license"] = new JsonObject { ["name"] = "Apache-2.0", ["identifier"] = "Apache-2.0" },
            },
            ["x-wcs-provider"] = "wms",
            ["servers"] = new JsonArray(new JsonObject
            {
                ["url"] = "{wmsApiAddress}",
                ["description"] = "Your WMS, or an adapter in front of it.",
                ["variables"] = new JsonObject
                {
                    ["wmsApiAddress"] = new JsonObject
                    {
                        ["default"] = "http://localhost:5142",
                        ["description"] = "The host setting `Wms:ApiAddress`: an origin without path.",
                    },
                },
            }),
            ["security"] = new JsonArray(new JsonObject { ["wmsKey"] = new JsonArray() }),
        };
        var pathsNode = new JsonObject();
        foreach (var (path, item) in paths) pathsNode[path] = item;
        var (components, names) = schemas.ToJson();
        document["paths"] = SchemaSet.Resolve(pathsNode, names);
        document["components"] = new JsonObject
        {
            ["schemas"] = components,
            ["securitySchemes"] = new JsonObject
            {
                ["wmsKey"] = new JsonObject
                {
                    ["type"] = "apiKey",
                    ["in"] = "header",
                    ["name"] = WarehouseRoutes.ApiKeyHeader,
                    ["description"] = "The host setting `Wms:ApiKey`, on every request. Refuse a missing or wrong key with 401: the WCS treats it as a permanent refusal.",
                },
            },
        };
        return document;
    }

    private static string Description() =>
        "The routes a WMS (or an adapter in front of it) serves for the WCS, generated by tools/Wcs.ContractCheck from " +
        "`IWarehouse` and the real client `HttpWarehouse` (do not edit by hand). `info.version` is `WarehouseContract.Version` " +
        "(WCS/Wcs.Wms.Contracts): its major part changes on a change an existing WMS would not survive.\n\n" +
        "On the wire:\n" +
        "- Request bodies come in two casings, written by two serializers of the client: `x-wcs-request-casing` says which " +
        "per operation (PascalCase for the POSTs that read an answer body, camelCase for those whose answer body is ignored). " +
        "Read property names case-insensitively. Answers are read case-insensitively, and numbers in answers may also be strings.\n" +
        "- Identifiers of load units, cells, areas, warehouses and rows are .NET `decimal` (`format: decimal`): the same id may come " +
        "as `5001` or `5001.0`, in bodies and in path segments. Parse them as numbers, never compare them as text. Mission ids and " +
        "operation ids are GUID strings. Enumerations are numbers (`enum` and `x-enum-varnames`).\n" +
        "- Answers: any 2xx with the expected body is used. A 404 is \"there is none\" only where the operation has a `404` answer " +
        "(`x-wcs-not-found: none`); anywhere else it is a failure. Every 4xx except 408 and 429 is a permanent refusal: answer it only " +
        "for a request that can never succeed. 5xx, 408, 429, a timeout, a dropped connection, a body that does not read and a `null` " +
        "body are temporary failures: the WCS leaves the work waiting and asks again, or retries from a durable queue.\n" +
        "- The operations with an `OperationId` in the body (`x-wcs-operation-id: true`) are delivered at least once: keep a receipt per " +
        "`OperationId`. Same id and same content (same values: `5005` and `5005.0` are the same id): answer what you answered the first " +
        "time without applying it again. Same id, different content: 409. The empty GUID is not from a queue: no receipt.\n" +
        "- `x-wcs-durable-queue` names the table of the WCS's database (`wcs.db`) the operation is sent from.";

    private static JsonObject BuildOperation(Operation operation, SchemaSet schemas)
    {
        var name = operation.Method.Name.EndsWith("Async", StringComparison.Ordinal)
            ? operation.Method.Name[..^"Async".Length]
            : operation.Method.Name;
        var hasOperationId = operation.RequestType?.GetProperty("OperationId")?.PropertyType == typeof(Guid);
        DurableQueues.TryGetValue(operation.Method.Name, out var queue);

        var notes = new List<string> { $"`IWarehouse.{operation.Method.Name}`." };
        if (operation.Casing != null)
            notes.Add($"The body is written in {operation.Casing} (`{{\"{(operation.Casing == PascalCase ? "LoadUnitId" : "loadUnitId")}\":5001}}`); read its names case-insensitively.");
        notes.Add(operation.Optional
            ? "Optional: a WMS that does not offer it answers 404 or 501, and the WCS goes on without it."
            : operation.NotFoundIsNone
                ? "A 404 means \"there is none\"."
                : "A 404 is a failure like any other unexpected status.");
        if (operation.ResponseType == null) notes.Add("The answer body is not read.");
        foreach (var parameter in operation.NotSentWhenEmpty)
            notes.Add($"Not sent when `{parameter}` is empty: the WCS gives the answer itself.");
        if (queue.Table != null) notes.Add(queue.Behaviour);

        var result = new JsonObject
        {
            ["operationId"] = name,
            ["description"] = string.Join(" ", notes),
        };

        var parameters = new JsonArray();
        foreach (var segment in operation.Route.Split('/').Where(s => s.StartsWith('{')))
        {
            var placeholder = segment.Trim('{', '}');
            var parameter = operation.Method.GetParameters().SingleOrDefault(p => p.Name == placeholder)
                            ?? throw new InvalidOperationException($"{name}: the placeholder {placeholder} is not a parameter of the method.");
            parameters.Add(Parameter(placeholder, "path", required: true, parameter.ParameterType));
        }
        foreach (var (queryName, parameter) in operation.Query)
            parameters.Add(Parameter(queryName, "query", Nullable.GetUnderlyingType(parameter.ParameterType) == null, parameter.ParameterType));
        if (parameters.Count > 0) result["parameters"] = parameters;

        if (operation.RequestType != null)
            result["requestBody"] = new JsonObject
            {
                ["required"] = true,
                ["content"] = new JsonObject
                {
                    ["application/json"] = new JsonObject
                    {
                        ["schema"] = schemas.Reference(operation.RequestType, operation.Casing == PascalCase ? SchemaUse.PascalRequest : SchemaUse.CamelRequest),
                    },
                },
            };

        var responses = new JsonObject();
        if (operation.ResponseType == null)
            responses["200"] = new JsonObject { ["description"] = "Done. Any 2xx status is accepted; the body is not read." };
        else
            responses["200"] = new JsonObject
            {
                ["description"] = "The answer. Any 2xx status is accepted; a body that does not read, or `null`, is a failure.",
                ["content"] = new JsonObject
                {
                    ["application/json"] = new JsonObject { ["schema"] = schemas.Reference(operation.ResponseType, SchemaUse.Answer) },
                },
            };
        if (operation.Optional)
        {
            responses["404"] = new JsonObject { ["description"] = "Not offered: the WCS goes on without it." };
            responses["501"] = new JsonObject { ["description"] = "Not offered, as a 404." };
        }
        else if (operation.NotFoundIsNone)
            responses["404"] = new JsonObject { ["description"] = "There is none: the WCS reads it as an empty answer, not as a failure." };
        if (hasOperationId)
            responses["409"] = new JsonObject
            {
                ["description"] = "The same `OperationId` was received with a different content: a bug on one side. It is a permanent " +
                                  "refusal, and a durable queue parks the row.",
            };
        responses["4XX"] = new JsonObject
        {
            ["description"] = "A permanent refusal (every 4xx except 408 and 429" + (operation.NotFoundIsNone ? ", and the 404 above" : "") +
                              "): answer it only for a request that can never succeed. The WCS logs it and does not read the body; a " +
                              "durable queue parks the row.",
        };
        responses["5XX"] = new JsonObject
        {
            ["description"] = "A temporary failure, like 408, 429, a timeout or a dropped connection: the WCS leaves the work waiting and " +
                              "asks again, or retries from its durable queue.",
        };
        result["responses"] = responses;

        if (operation.Casing != null) result["x-wcs-request-casing"] = operation.Casing;
        result["x-wcs-not-found"] = operation.NotFoundIsNone ? "none" : "failure";
        if (operation.Optional) result["x-wcs-optional"] = true;
        result["x-wcs-answer-body"] = operation.ResponseType == null ? "ignored" : "read";
        result["x-wcs-operation-id"] = hasOperationId;
        if (queue.Table != null) result["x-wcs-durable-queue"] = queue.Table;
        return result;
    }

    private static JsonObject Parameter(string name, string location, bool required, Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        JsonObject schema = underlying == typeof(decimal) ? new JsonObject { ["type"] = "number", ["format"] = "decimal" }
            : underlying == typeof(Guid) ? new JsonObject { ["type"] = "string", ["format"] = "uuid" }
            : underlying == typeof(bool) ? new JsonObject { ["type"] = "boolean" }
            : underlying == typeof(int) ? new JsonObject { ["type"] = "integer", ["format"] = "int32" }
            : underlying == typeof(short) ? new JsonObject { ["type"] = "integer", ["format"] = "int16" }
            : underlying == typeof(string) ? new JsonObject { ["type"] = "string" }
            : throw new InvalidOperationException($"No schema for the parameter {name} of type {type}.");
        var parameter = new JsonObject { ["name"] = name, ["in"] = location, ["required"] = required, ["schema"] = schema };
        if (underlying == typeof(decimal))
            parameter["description"] = "Written with the invariant culture as the WCS holds it: `5001` or `5001.0` are the same id.";
        else if (underlying == typeof(string) && location == "path")
            parameter["description"] = "Escaped as a URI segment.";
        else if (!required)
            parameter["description"] = "Left out when the WCS has no value.";
        return parameter;
    }

    // ---------------------------------------------------------------- schemas

    internal enum SchemaUse { CamelRequest, PascalRequest, Answer }

    /// <summary>
    /// The component schemas, one per contract type and use. A type used one way only keeps its name; one
    /// used in more gets a suffix on the request variants: <c>.PascalCase</c>, or <c>.Request</c> for camelCase.
    /// </summary>
    private sealed class SchemaSet
    {
        private readonly Dictionary<(Type Type, SchemaUse Use), JsonNode?> _entries = [];

        public JsonNode Reference(Type type, SchemaUse use)
        {
            var element = ElementType(type);
            if (element != null) return new JsonObject { ["type"] = "array", ["items"] = Reference(element, use) };
            if (type == typeof(decimal)) return new JsonObject { ["type"] = "number", ["format"] = "decimal" };
            if (type == typeof(bool)) return new JsonObject { ["type"] = "boolean" };
            Register(type, use);
            return new JsonObject { ["$ref"] = Placeholder(type, use) };
        }

        private static SchemaUse Normalised(Type type, SchemaUse use) => type.IsEnum ? SchemaUse.Answer : use;

        private void Register(Type type, SchemaUse use)
        {
            var key = (type, Normalised(type, use));
            if (_entries.ContainsKey(key)) return;
            _entries[key] = null;
            _entries[key] = Generate(type, use);
        }

        private JsonNode Generate(Type type, SchemaUse use)
        {
            if (type.IsEnum)
            {
                var schema = new JsonObject { ["type"] = "integer" };
                EnumSchemas.AddValues(schema, type);
                return schema;
            }

            var options = use switch
            {
                SchemaUse.PascalRequest => Written,
                SchemaUse.CamelRequest => WrittenCamel,
                _ => Web,
            };
            var nested = new List<Type>();
            var exporter = new JsonSchemaExporterOptions
            {
                TreatNullObliviousAsNonNullable = true,
                TransformSchemaNode = (context, node) =>
                {
                    var current = Nullable.GetUnderlyingType(context.TypeInfo.Type) ?? context.TypeInfo.Type;
                    if (current == typeof(decimal) && node is JsonObject number) number["format"] = "decimal";
                    if (context.Path.IsEmpty || !(current.IsEnum || IsContractRecord(current))) return node;

                    nested.Add(current);
                    JsonNode reference = new JsonObject { ["$ref"] = Placeholder(current, use) };
                    var nullable = node is JsonObject o && (o["type"] is JsonArray types && types.Any(t => t?.GetValue<string>() == "null") ||
                                                           o["enum"] is JsonArray values && values.Any(v => v is null));
                    return nullable
                        ? new JsonObject { ["anyOf"] = new JsonArray(reference, new JsonObject { ["type"] = "null" }) }
                        : reference;
                },
            };
            var generated = JsonSchemaExporter.GetJsonSchemaAsNode(options, type, exporter).AsObject();
            if (use != SchemaUse.Answer)
            {
                if (generated["properties"] is JsonObject properties)
                {
                    // The client writes every property of a request, always: they are all on the wire.
                    generated["required"] = new JsonArray([.. properties.Select(p => (JsonNode)JsonValue.Create(p.Key)!)]);
                    generated["additionalProperties"] = false;
                }
                generated["x-wcs-casing"] = use == SchemaUse.PascalRequest ? PascalCase : CamelCase;
            }

            foreach (var child in nested.Distinct()) Register(child, use);
            return generated;
        }

        /// <summary>Resolved to the final names in <see cref="ToJson"/>.</summary>
        private static string Placeholder(Type type, SchemaUse use) => $"wcs-schema:{Normalised(type, use)}:{type.FullName}";

        public (JsonObject Schemas, IReadOnlyDictionary<(Type Type, SchemaUse Use), string> Names) ToJson()
        {
            var shared = _entries.Keys.GroupBy(k => k.Type).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
            var names = _entries.Keys.ToDictionary(k => k, k => !shared.Contains(k.Type) || k.Use == SchemaUse.Answer
                ? k.Type.Name
                : k.Type.Name + (k.Use == SchemaUse.PascalRequest ? ".PascalCase" : ".Request"));
            if (names.Values.Distinct().Count() != names.Count)
                throw new InvalidOperationException("Two contract types share a name: " +
                    string.Join(", ", names.Values.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key)));

            var result = new JsonObject();
            foreach (var (key, schema) in _entries.OrderBy(e => names[e.Key], StringComparer.Ordinal))
                result[names[key]] = Resolve(schema!.DeepClone(), names);
            return (result, names);
        }

        public static JsonNode Resolve(JsonNode node, IReadOnlyDictionary<(Type Type, SchemaUse Use), string> names)
        {
            switch (node)
            {
                case JsonObject o:
                    if (o["$ref"] is JsonValue r && r.GetValue<string>() is var target && target.StartsWith("wcs-schema:", StringComparison.Ordinal))
                    {
                        var key = names.Keys.Single(k => Placeholder(k.Type, k.Use) == target);
                        o["$ref"] = "#/components/schemas/" + names[key];
                    }
                    foreach (var (_, child) in o.ToList())
                        if (child != null) Resolve(child, names);
                    break;
                case JsonArray a:
                    foreach (var child in a)
                        if (child != null) Resolve(child, names);
                    break;
            }
            return node;
        }

        private static Type? ElementType(Type type) =>
            type.IsArray ? type.GetElementType()
            : type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>) ? type.GetGenericArguments()[0]
            : null;
    }

    // ---------------------------------------------------------------- sample arguments

    /// <summary>Arguments that make every method of the client send its request.</summary>
    private static class Samples
    {
        private static readonly Guid SampleGuid = new("11111111-1111-4111-8111-111111111111");

        public static object? Empty(Type type) =>
            type == typeof(string) ? "" : Activator.CreateInstance(typeof(List<>).MakeGenericType(type.GetGenericArguments()[0]));

        public static object? For(Type type)
        {
            var underlying = Nullable.GetUnderlyingType(type) ?? type;
            if (underlying == typeof(CancellationToken)) return CancellationToken.None;
            if (underlying == typeof(decimal)) return 7m;
            if (underlying == typeof(int)) return 7;
            if (underlying == typeof(short)) return (short)7;
            if (underlying == typeof(long)) return 7L;
            if (underlying == typeof(bool)) return true;
            if (underlying == typeof(string)) return "X";
            if (underlying == typeof(Guid)) return SampleGuid;
            if (underlying == typeof(DateTime)) return new DateTime(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc);
            if (underlying.IsEnum) return Enum.GetValues(underlying).GetValue(0);
            if (underlying.IsGenericType && underlying.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
            {
                var element = underlying.GetGenericArguments()[0];
                var list = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element))!;
                list.Add(For(element));
                return list;
            }
            if (IsContractRecord(underlying))
            {
                var constructor = underlying.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
                return constructor.Invoke([.. constructor.GetParameters().Select(p => For(p.ParameterType))]);
            }
            throw new InvalidOperationException($"No sample value for {type}.");
        }
    }

}
