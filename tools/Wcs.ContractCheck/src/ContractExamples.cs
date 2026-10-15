using System.Text.Json.Nodes;

namespace Wcs.ContractCheck;

/// <summary>
/// The JSON examples of <c>examples/</c>, next to the contracts, checked against the schemas of the contract
/// they belong to. The name of a file says where it goes:
/// <c>{host|wms}/{operationId}.request.json</c> is the request body of the operation,
/// <c>{operationId}.response.json</c> its successful answer (the first 2xx with a body), and
/// <c>{operationId}.response.{status}.json</c> the answer with that status.
/// </summary>
internal static class ContractExamples
{
    public const string Folder = "examples";

    private static readonly IReadOnlyDictionary<string, string> Documents = new Dictionary<string, string>
    {
        ["host"] = HostApiDocument.FileName,
        ["wms"] = WmsContractDocument.FileName,
    };

    /// <summary>Every example under <paramref name="contracts"/>, as a path relative to it.</summary>
    public static IReadOnlyList<string> All(string contracts)
    {
        var root = Path.Combine(contracts, Folder);
        return Directory.Exists(root)
            ? [.. Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(contracts, f).Replace('\\', '/')).Order(StringComparer.Ordinal)]
            : [];
    }

    /// <summary>The errors of every example, each prefixed by its file; empty when all are valid.</summary>
    public static IReadOnlyList<string> Validate(string contracts, IReadOnlyDictionary<string, JsonObject>? documents = null)
    {
        var errors = new List<string>();
        foreach (var relative in All(contracts))
            errors.AddRange(Validate(contracts, relative, documents).Select(e => $"{relative}: {e}"));
        return errors;
    }

    public static IReadOnlyList<string> Validate(string contracts, string relative, IReadOnlyDictionary<string, JsonObject>? documents = null)
    {
        var parts = relative.Split('/');
        if (parts.Length != 3 || parts[0] != Folder || !Documents.TryGetValue(parts[1], out var documentName))
            return [$"not under {Folder}/host or {Folder}/wms"];
        var document = documents?[documentName] ?? ContractFiles.Read(Path.Combine(contracts, documentName))
                       ?? throw new InvalidOperationException($"{documentName} is missing");
        var instance = JsonNode.Parse(File.ReadAllText(Path.Combine(contracts, relative)));
        return Validate(document, Path.GetFileNameWithoutExtension(parts[2]), instance);
    }

    /// <summary><paramref name="name"/> is the file name without <c>.json</c>: <c>ReserveStorage.request</c>, <c>GetStatus.response.401</c>.</summary>
    public static IReadOnlyList<string> Validate(JsonObject document, string name, JsonNode? instance)
    {
        var parts = name.Split('.');
        if (parts.Length is < 2 or > 3 || parts[1] is not ("request" or "response") || (parts.Length == 3 && parts[1] != "response"))
            return ["the name is not {operationId}.request, {operationId}.response or {operationId}.response.{status}"];

        var operation = FindOperation(document, parts[0]);
        if (operation == null) return [$"no operation {parts[0]} in the contract"];

        JsonNode? schema;
        if (parts[1] == "request")
        {
            schema = operation["requestBody"]?["content"]?["application/json"]?["schema"];
            if (schema == null) return [$"{parts[0]} has no JSON request body"];
        }
        else
        {
            var responses = operation["responses"]!.AsObject();
            var status = parts.Length == 3
                ? parts[2]
                : responses.Where(r => r.Key.StartsWith('2') && r.Value?["content"] != null).Select(r => r.Key).Order().FirstOrDefault();
            if (status == null || responses[status]?["content"] is not JsonObject content)
                return [$"{parts[0]} has no answer {(parts.Length == 3 ? parts[2] : "2xx")} with a body"];
            schema = (content["application/json"] ?? content["application/problem+json"])?["schema"];
            if (schema == null) return [$"{parts[0]} answer {status} has no JSON body"];
        }

        return JsonSchemaValidator.Validate(document, schema, instance);
    }

    private static JsonObject? FindOperation(JsonObject document, string operationId) =>
        document["paths"]!.AsObject()
            .SelectMany(p => p.Value!.AsObject().Select(o => o.Value!.AsObject()))
            .FirstOrDefault(o => o["operationId"]?.GetValue<string>() == operationId);
}
