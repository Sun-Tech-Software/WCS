using System.Globalization;
using System.Text.Json.Nodes;

namespace Wcs.ContractCheck;

public enum ChangeKind { NonBreaking, Breaking }

internal sealed record ContractChange(ChangeKind Kind, string Where, string What)
{
    public override string ToString() => $"{(Kind == ChangeKind.Breaking ? "BREAKING" : "compatible")}  {Where}: {What}";
}

/// <summary>
/// Compares two versions of a contract document and classifies every difference as breaking or not,
/// <b>from the side of the party that is not the WCS</b>, named by the document's <c>x-wcs-provider</c>:
/// <list type="bullet">
/// <item><c>wcs</c> (the host API): the other party is a client. It calls the routes, writes the requests
/// and reads the answers. A route removed, a required request field added, an answer field removed, a
/// type narrowed where it writes or widened where it reads, break it.</item>
/// <item><c>wms</c> (the WMS contract): the other party is the server. It serves the routes, reads the
/// requests and writes the answers. A route <b>added</b> (it does not serve it yet), a request field
/// removed or renamed (a casing change is one), an answer field now required, a 404 that changes meaning,
/// break it; a route the WCS no longer calls does not.</item>
/// </list>
/// Descriptions, titles of problems and other annotations are compatible changes.
/// </summary>
internal static class ContractComparer
{
    /// <summary>Who writes a piece of data: the other party (the WCS reads it), or the WCS (the other party reads it).</summary>
    private enum Flow { ToWcs, FromWcs }

    public static IReadOnlyList<ContractChange> Compare(JsonObject before, JsonObject after)
    {
        var changes = new List<ContractChange>();
        var provider = after["x-wcs-provider"]?.GetValue<string>() ?? before["x-wcs-provider"]?.GetValue<string>()
                       ?? throw new InvalidOperationException("The document has no x-wcs-provider.");
        var clientSide = provider == "wcs";
        var requests = clientSide ? Flow.ToWcs : Flow.FromWcs;
        var answers = clientSide ? Flow.FromWcs : Flow.ToWcs;
        var context = new Context(before, after, changes);

        var old = Operations(before);
        var @new = Operations(after);
        foreach (var key in old.Keys.Except(@new.Keys))
            changes.Add(new(clientSide ? ChangeKind.Breaking : ChangeKind.NonBreaking, key,
                clientSide ? "operation removed" : "operation removed: the WCS no longer calls it"));
        foreach (var key in @new.Keys.Except(old.Keys))
        {
            // An optional operation (x-wcs-optional) is one the WCS does without: a WMS that does not
            // serve it answers 404 or 501 and keeps working.
            var optional = @new[key]["x-wcs-optional"]?.GetValue<bool>() == true;
            changes.Add(new(clientSide || optional ? ChangeKind.NonBreaking : ChangeKind.Breaking, key,
                clientSide ? "operation added"
                : optional ? "optional operation added: an existing WMS answers 404 or 501"
                : "operation added: an existing WMS does not serve it"));
        }

        foreach (var key in old.Keys.Intersect(@new.Keys))
        {
            var (o, n) = (old[key], @new[key]);
            CompareParameters(context, key, o, n, requests);
            CompareBody(context, key, o["requestBody"], n["requestBody"], requests);
            CompareResponses(context, key, o, n, answers, clientSide);
            foreach (var extension in new[] { "x-wcs-request-casing", "x-wcs-not-found", "x-wcs-engine-state", "x-wcs-edit-lease", "x-wcs-optional" })
                if (Text(o[extension]) != Text(n[extension]))
                    changes.Add(new(ChangeKind.Breaking, key, $"{extension}: {Text(o[extension]) ?? "none"} -> {Text(n[extension]) ?? "none"}"));
        }

        if (!JsonNode.DeepEquals(before["components"]?["securitySchemes"], after["components"]?["securitySchemes"]))
            changes.Add(new(ChangeKind.Breaking, "components.securitySchemes", "the authentication changed"));

        if (changes.Count == 0 && !JsonNode.DeepEquals(Unversioned(before), Unversioned(after)))
            changes.Add(new(ChangeKind.NonBreaking, "document", "descriptions or annotations changed"));
        return [.. changes.Distinct()];
    }

    /// <summary>The major part of <c>info.version</c>.</summary>
    public static int Major(JsonObject document) =>
        int.Parse(Text(document["info"]?["version"])!.Split('.')[0], CultureInfo.InvariantCulture);

    public static string? Version(JsonObject document) => Text(document["info"]?["version"]);

    private static JsonNode Unversioned(JsonObject document)
    {
        var copy = document.DeepClone().AsObject();
        copy["info"]!.AsObject().Remove("version");
        return copy;
    }

    private static Dictionary<string, JsonObject> Operations(JsonObject document)
    {
        var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var (path, item) in document["paths"]?.AsObject() ?? [])
        foreach (var (method, operation) in item!.AsObject())
            result[$"{method.ToUpperInvariant()} {path}"] = operation!.AsObject();
        return result;
    }

    private static string? Text(JsonNode? node) => node switch
    {
        null => null,
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        _ => node.ToJsonString(),
    };

    private sealed record Context(JsonObject Before, JsonObject After, List<ContractChange> Changes)
    {
        public HashSet<(string, string, Flow)> Visited { get; } = [];

        public void Add(ChangeKind kind, string where, string what) => Changes.Add(new(kind, where, what));
    }

    private static void CompareParameters(Context context, string key, JsonObject o, JsonObject n, Flow flow)
    {
        static Dictionary<string, JsonObject> Index(JsonObject operation) =>
            (operation["parameters"]?.AsArray() ?? []).Select(p => p!.AsObject())
            .ToDictionary(p => $"{Text(p["in"])} {Text(p["name"])}");

        var before = Index(o);
        var after = Index(n);
        foreach (var name in before.Keys.Except(after.Keys))
            context.Add(flow == Flow.ToWcs ? ChangeKind.NonBreaking : ChangeKind.Breaking, key, $"parameter {name} removed");
        foreach (var name in after.Keys.Except(before.Keys))
        {
            var required = after[name]["required"]?.GetValue<bool>() == true;
            context.Add(flow == Flow.ToWcs && required ? ChangeKind.Breaking : ChangeKind.NonBreaking, key,
                $"parameter {name} added{(required ? ", required" : "")}");
        }
        foreach (var name in before.Keys.Intersect(after.Keys))
        {
            var wasRequired = before[name]["required"]?.GetValue<bool>() == true;
            var isRequired = after[name]["required"]?.GetValue<bool>() == true;
            if (wasRequired != isRequired)
                context.Add(Narrowing(flow, isRequired), key, $"parameter {name} is {(isRequired ? "now required" : "no longer required")}");
            CompareSchema(context, $"{key} parameter {name}", before[name]["schema"], after[name]["schema"], flow);
        }
    }

    private static void CompareBody(Context context, string key, JsonNode? o, JsonNode? n, Flow flow)
    {
        if (o == null && n == null) return;
        if (o == null || n == null)
        {
            var added = o == null;
            context.Add(added == (flow == Flow.ToWcs) ? ChangeKind.Breaking : ChangeKind.NonBreaking, key,
                $"request body {(added ? "added" : "removed")}");
            return;
        }
        CompareContent(context, key + " request", o["content"]?.AsObject(), n["content"]?.AsObject(), flow);
    }

    private static void CompareResponses(Context context, string key, JsonObject o, JsonObject n, Flow flow, bool clientSide)
    {
        var before = o["responses"]?.AsObject() ?? [];
        var after = n["responses"]?.AsObject() ?? [];
        foreach (var status in before.Select(r => r.Key).Union(after.Select(r => r.Key)))
        {
            var (was, @is) = (before[status], after[status]);
            if (was == null || @is == null)
            {
                // A success status the client must handle, or a 404 that changes meaning for the WMS, breaks;
                // an error answer added to or removed from the description does not.
                var breaking = status.StartsWith('2') || (!clientSide && status == "404");
                context.Add(breaking ? ChangeKind.Breaking : ChangeKind.NonBreaking, key,
                    $"answer {status} {(was == null ? "added" : "removed")}");
                continue;
            }
            CompareContent(context, $"{key} answer {status}", was["content"]?.AsObject(), @is["content"]?.AsObject(), flow);
        }
    }

    private static void CompareContent(Context context, string where, JsonObject? o, JsonObject? n, Flow flow)
    {
        o ??= [];
        n ??= [];
        foreach (var (mediaType, _) in o.Where(c => !n.ContainsKey(c.Key)))
            context.Add(flow == Flow.ToWcs ? ChangeKind.NonBreaking : ChangeKind.Breaking, where, $"{mediaType} body removed");
        foreach (var (mediaType, _) in n.Where(c => !o.ContainsKey(c.Key)))
            context.Add(flow == Flow.ToWcs ? ChangeKind.Breaking : ChangeKind.NonBreaking, where, $"{mediaType} body added");
        foreach (var (mediaType, body) in o.Where(c => n.ContainsKey(c.Key)))
            CompareSchema(context, where, body?["schema"], n[mediaType]?["schema"], flow);
    }

    /// <summary>
    /// A rule narrows when the writer must now do more: breaking where the other party writes
    /// (<see cref="Flow.ToWcs"/>), compatible where the WCS writes. <paramref name="narrower"/> false is the
    /// opposite move.
    /// </summary>
    private static ChangeKind Narrowing(Flow flow, bool narrower) =>
        (flow == Flow.ToWcs) == narrower ? ChangeKind.Breaking : ChangeKind.NonBreaking;

    private static void CompareSchema(Context context, string where, JsonNode? oldNode, JsonNode? newNode, Flow flow)
    {
        if (oldNode == null || newNode == null)
        {
            if (oldNode != newNode) context.Add(ChangeKind.Breaking, where, "schema " + (oldNode == null ? "added" : "removed"));
            return;
        }

        var (oldSchema, oldName) = Resolve(context.Before, oldNode);
        var (newSchema, newName) = Resolve(context.After, newNode);
        if (oldName != null && newName != null && !context.Visited.Add((oldName, newName, flow))) return;

        var oldTypes = Types(context.Before, oldSchema);
        var newTypes = Types(context.After, newSchema);
        var widened = newTypes.Except(oldTypes).ToList();
        var narrowed = oldTypes.Except(newTypes).ToList();
        // A type the writer may now use is breaking where the WCS writes; one it may no longer use, where the other party writes.
        if (widened.Count > 0)
            context.Add(flow == Flow.FromWcs ? ChangeKind.Breaking : ChangeKind.NonBreaking, where, $"now also {string.Join(", ", widened)}");
        if (narrowed.Count > 0)
            context.Add(flow == Flow.ToWcs ? ChangeKind.Breaking : ChangeKind.NonBreaking, where, $"no longer {string.Join(", ", narrowed)}");

        var (oldFormat, newFormat) = (Text(oldSchema["format"]), Text(newSchema["format"]));
        if (oldFormat != null && newFormat != null && oldFormat != newFormat)
            context.Add(ChangeKind.Breaking, where, $"format {oldFormat} -> {newFormat}");

        CompareEnum(context, where, oldSchema, newSchema, flow);

        var oldProperties = Properties(context.Before, oldSchema);
        var newProperties = Properties(context.After, newSchema);
        if (oldProperties != null || newProperties != null)
        {
            oldProperties ??= ([], []);
            newProperties ??= ([], []);
            var (before, wasRequired) = oldProperties.Value;
            var (after, isRequired) = newProperties.Value;
            foreach (var name in before.Keys.Except(after.Keys))
                context.Add(flow == Flow.ToWcs ? ChangeKind.NonBreaking : ChangeKind.Breaking, where, $"property {name} removed");
            foreach (var name in after.Keys.Except(before.Keys))
            {
                var required = isRequired.Contains(name);
                context.Add(flow == Flow.ToWcs && required ? ChangeKind.Breaking : ChangeKind.NonBreaking, where,
                    $"property {name} added{(required ? ", required" : "")}");
            }
            foreach (var name in before.Keys.Intersect(after.Keys))
            {
                if (wasRequired.Contains(name) != isRequired.Contains(name))
                    context.Add(Narrowing(flow, isRequired.Contains(name)), where,
                        $"property {name} is {(isRequired.Contains(name) ? "now required" : "no longer required")}");
                CompareSchema(context, $"{where}.{name}", before[name], after[name], flow);
            }
        }

        CompareSchema(context, where + "[]", Items(context.Before, oldSchema), Items(context.After, newSchema), flow, optional: true);
        if (oldSchema["additionalProperties"] is JsonObject oldValues && newSchema["additionalProperties"] is JsonObject newValues)
            CompareSchema(context, where + "{}", oldValues, newValues, flow);
    }

    private static void CompareSchema(Context context, string where, JsonNode? o, JsonNode? n, Flow flow, bool optional)
    {
        if (optional && o == null && n == null) return;
        CompareSchema(context, where, o, n, flow);
    }

    private static void CompareEnum(Context context, string where, JsonObject o, JsonObject n, Flow flow)
    {
        if (o["enum"] is not JsonArray oldValues || n["enum"] is not JsonArray newValues) return;
        var before = oldValues.Select(v => v?.ToJsonString() ?? "null").ToHashSet();
        var after = newValues.Select(v => v?.ToJsonString() ?? "null").ToHashSet();
        foreach (var value in after.Except(before))
            context.Add(flow == Flow.FromWcs ? ChangeKind.Breaking : ChangeKind.NonBreaking, where, $"enum value {value} added");
        foreach (var value in before.Except(after))
            context.Add(flow == Flow.ToWcs ? ChangeKind.Breaking : ChangeKind.NonBreaking, where, $"enum value {value} removed");
    }

    /// <summary>The schema behind a local <c>$ref</c>, and the referenced name.</summary>
    private static (JsonObject Schema, string? Name) Resolve(JsonObject document, JsonNode node)
    {
        var schema = node.AsObject();
        string? name = null;
        for (var hops = 0; schema["$ref"] is JsonValue reference; hops++)
        {
            if (hops > 32) throw new InvalidOperationException("$ref loop");
            var target = reference.GetValue<string>();
            const string prefix = "#/components/schemas/";
            if (!target.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidOperationException($"Unsupported $ref {target}");
            name = target[prefix.Length..];
            schema = document["components"]?["schemas"]?[name]?.AsObject()
                     ?? throw new InvalidOperationException($"Missing schema {name}");
        }
        return (schema, name);
    }

    /// <summary>The JSON types a schema admits, through <c>anyOf</c>/<c>oneOf</c> and references.</summary>
    private static HashSet<string> Types(JsonObject document, JsonObject schema)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        switch (schema["type"])
        {
            case JsonValue single: result.Add(single.GetValue<string>()); break;
            case JsonArray many: foreach (var t in many) result.Add(t!.GetValue<string>()); break;
        }
        foreach (var keyword in new[] { "anyOf", "oneOf" })
            if (schema[keyword] is JsonArray alternatives)
                foreach (var alternative in alternatives)
                    result.UnionWith(Types(document, Resolve(document, alternative!).Schema));
        if (schema["enum"] is JsonArray values && values.Any(v => v is null)) result.Add("null");
        if (result.Count == 0 && schema["properties"] != null) result.Add("object");
        return result;
    }

    /// <summary>The properties and required names of an object schema, looking into a nullable <c>anyOf</c>.</summary>
    private static (Dictionary<string, JsonNode?> Properties, HashSet<string> Required)? Properties(JsonObject document, JsonObject schema)
    {
        foreach (var candidate in Alternatives(document, schema))
            if (candidate["properties"] is JsonObject properties)
                return (properties.ToDictionary(p => p.Key, p => p.Value),
                    (candidate["required"]?.AsArray() ?? []).Select(r => r!.GetValue<string>()).ToHashSet());
        return null;
    }

    private static JsonNode? Items(JsonObject document, JsonObject schema) =>
        Alternatives(document, schema).Select(c => c["items"]).FirstOrDefault(i => i != null);

    private static IEnumerable<JsonObject> Alternatives(JsonObject document, JsonObject schema)
    {
        yield return schema;
        foreach (var keyword in new[] { "anyOf", "oneOf" })
            if (schema[keyword] is JsonArray alternatives)
                foreach (var alternative in alternatives)
                    yield return Resolve(document, alternative!).Schema;
    }
}
