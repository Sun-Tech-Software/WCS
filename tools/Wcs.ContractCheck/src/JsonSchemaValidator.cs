using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Wcs.ContractCheck;

/// <summary>
/// A validator for the part of JSON Schema (2020-12, as OpenAPI 3.1 uses it) that the two contract
/// documents use: <c>$ref</c> to their own components, <c>type</c> (one or several), <c>properties</c>,
/// <c>required</c>, <c>additionalProperties</c>, <c>items</c>, <c>enum</c>, <c>anyOf</c>/<c>oneOf</c>/<c>allOf</c>,
/// <c>pattern</c> and the formats <c>uuid</c>, <c>date-time</c> and <c>date</c>. Anything else in a schema is
/// an annotation. Written here so that checking the examples needs no package and no network.
/// </summary>
internal static class JsonSchemaValidator
{
    private static readonly HashSet<string> Understood =
    [
        "$ref", "type", "properties", "required", "additionalProperties", "items", "enum", "anyOf", "oneOf", "allOf",
        "pattern", "format", "description", "title", "default", "nullable", "contentMediaType",
    ];

    /// <summary>The errors of <paramref name="instance"/> against <paramref name="schema"/>, empty when it is valid.</summary>
    public static IReadOnlyList<string> Validate(JsonObject document, JsonNode schema, JsonNode? instance)
    {
        var errors = new List<string>();
        Check(document, schema.AsObject(), instance, "$", errors);
        return errors;
    }

    private static void Check(JsonObject document, JsonObject schema, JsonNode? instance, string at, List<string> errors)
    {
        foreach (var keyword in schema.Select(p => p.Key))
            if (!Understood.Contains(keyword) && !keyword.StartsWith("x-", StringComparison.Ordinal))
                throw new NotSupportedException($"The validator does not know the keyword {keyword} (at {at}).");

        if (schema["$ref"] is JsonValue reference)
        {
            const string prefix = "#/components/schemas/";
            var target = reference.GetValue<string>();
            var name = target.StartsWith(prefix, StringComparison.Ordinal) ? target[prefix.Length..] : throw new NotSupportedException(target);
            Check(document, document["components"]!["schemas"]![name]!.AsObject(), instance, at, errors);
        }

        if (schema["type"] is { } type)
        {
            var types = type is JsonArray many ? many.Select(t => t!.GetValue<string>()).ToList() : [type.GetValue<string>()];
            if (!types.Any(t => IsOfType(instance, t)))
            {
                errors.Add($"{at}: expected {string.Join(" or ", types)}, found {Describe(instance)}");
                return;
            }
        }

        if (schema["enum"] is JsonArray values && !values.Any(v => JsonNode.DeepEquals(v, instance)))
            errors.Add($"{at}: {Describe(instance)} is not one of {values.ToJsonString()}");

        if (instance is JsonValue text && text.GetValueKind() == JsonValueKind.String)
        {
            var value = text.GetValue<string>();
            if (schema["pattern"] is JsonValue pattern && !Regex.IsMatch(value, pattern.GetValue<string>()))
                errors.Add($"{at}: \"{value}\" does not match {pattern.GetValue<string>()}");
            switch (schema["format"]?.GetValue<string>())
            {
                case "uuid" when !Guid.TryParseExact(value, "D", out _):
                case "date-time" when !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _):
                case "date" when !DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _):
                    errors.Add($"{at}: \"{value}\" is not a {schema["format"]!.GetValue<string>()}");
                    break;
            }
        }

        if (instance is JsonObject @object)
        {
            var properties = schema["properties"] as JsonObject;
            foreach (var name in schema["required"]?.AsArray().Select(r => r!.GetValue<string>()) ?? [])
                if (!@object.ContainsKey(name))
                    errors.Add($"{at}: the required property {name} is missing");
            foreach (var (name, value) in @object)
            {
                if (properties?[name] is JsonObject property)
                    Check(document, property, value, $"{at}.{name}", errors);
                else if (schema["additionalProperties"] is JsonValue allowed && !allowed.GetValue<bool>())
                    errors.Add($"{at}: the property {name} is not allowed");
                else if (schema["additionalProperties"] is JsonObject additional)
                    Check(document, additional, value, $"{at}.{name}", errors);
            }
        }

        if (instance is JsonArray array && schema["items"] is JsonObject items)
            for (var i = 0; i < array.Count; i++)
                Check(document, items, array[i], $"{at}[{i}]", errors);

        if (schema["allOf"] is JsonArray all)
            foreach (var part in all)
                Check(document, part!.AsObject(), instance, at, errors);

        foreach (var keyword in new[] { "anyOf", "oneOf" })
        {
            if (schema[keyword] is not JsonArray alternatives) continue;
            // oneOf is checked as anyOf: ASP.NET Core writes `oneOf: [{"type":"null"}, {"$ref": ...}]` for a
            // nullable string enumeration whose own schema already admits null, so a null matches both and a
            // strict oneOf would refuse exactly what the host sends.
            var matching = alternatives.Count(a => Validate(document, a!, instance).Count == 0);
            if (matching == 0)
                errors.Add($"{at}: {Describe(instance)} matches {matching} of the {keyword} alternatives");
        }
    }

    private static bool IsOfType(JsonNode? instance, string type) => type switch
    {
        "null" => instance is null,
        "object" => instance is JsonObject,
        "array" => instance is JsonArray,
        "string" => instance is JsonValue v && v.GetValueKind() == JsonValueKind.String,
        "boolean" => instance is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False,
        "number" => instance is JsonValue v && v.GetValueKind() == JsonValueKind.Number,
        "integer" => instance is JsonValue v && v.GetValueKind() == JsonValueKind.Number && decimal.TryParse(
            v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d == decimal.Truncate(d),
        _ => throw new NotSupportedException($"type {type}"),
    };

    private static string Describe(JsonNode? instance) => instance?.ToJsonString() ?? "null";
}
