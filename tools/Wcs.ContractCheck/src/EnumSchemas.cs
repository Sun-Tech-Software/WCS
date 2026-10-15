using System.Text.Json.Nodes;

namespace Wcs.ContractCheck;

/// <summary>The values of a numeric enumeration, as JSON Schema: <c>enum</c> and their names in <c>x-enum-varnames</c>.</summary>
internal static class EnumSchemas
{
    public static void AddValues(JsonObject schema, Type enumType)
    {
        var values = Enum.GetValues(enumType).Cast<object>()
            .Select(v => (Value: Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture), Name: Enum.GetName(enumType, v)!))
            .DistinctBy(v => v.Value)
            .OrderBy(v => v.Value)
            .ToList();
        schema["enum"] = new JsonArray([.. values.Select(v => (JsonNode)JsonValue.Create(v.Value))]);
        schema["x-enum-varnames"] = new JsonArray([.. values.Select(v => (JsonNode)JsonValue.Create(v.Name))]);
    }
}
