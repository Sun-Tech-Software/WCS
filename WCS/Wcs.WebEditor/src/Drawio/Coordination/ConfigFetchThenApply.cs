using System.ComponentModel;
using System.Globalization;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Properties;

namespace Wcs.WebEditor.Drawio.Coordination;

/// <summary>
/// Owner's decision of 27/9 (D3): a configuration write of this page - the properties panel's and
/// the device list's alike - sends only what the operator changed, on top of the device's CURRENT
/// configuration read from the host inside the write gate, never back the whole copy the panel or the
/// list read earlier. A field another client changed meanwhile is kept; a field the operator is
/// changing that another client changed too is not overwritten in silence: it is a conflict, named.
/// </summary>
/// <remarks>
/// "What the operator changed" is every writable property whose value differs between the copy the
/// caller read (<c>before</c>) and the one it wants written (<c>edited</c>) - for the list that is the
/// one cell, for the panel the fields the operator touched compared with what the panel read. By
/// reflection, like <see cref="PropertyModel.Copy"/>, and for the same reason: some sixty properties,
/// and a list written by hand would stop being true at the first field added to the contract.
/// </remarks>
internal static class ConfigFetchThenApply
{
    private static readonly PropertyDescriptorCollection Properties = TypeDescriptor.GetProperties(typeof(PlantItemConfigDto));

    /// <summary>A field the operator is changing that the host changed too, since the caller read it.</summary>
    internal sealed record Conflict(string Label, string OnTheHost, string Wanted);

    /// <summary>The names of the properties whose value differs between <paramref name="before"/> and <paramref name="edited"/>.</summary>
    public static IReadOnlyList<string> Changed(PlantItemConfigDto before, PlantItemConfigDto edited) =>
        [.. Properties.Cast<PropertyDescriptor>()
            .Where(property => !property.IsReadOnly && !Equals(property.GetValue(before), property.GetValue(edited)))
            .Select(property => property.Name)];

    /// <summary>
    /// The fields in <paramref name="changed"/> that the host changed since <paramref name="before"/>
    /// was read, to a value that is not the operator's either - the host holding exactly what the
    /// operator wants is no conflict.
    /// </summary>
    public static IReadOnlyList<Conflict> Conflicts(IReadOnlyList<string> changed, PlantItemConfigDto before, PlantItemConfigDto edited, PlantItemConfigDto onTheHost) =>
        [.. changed.Select(name => Properties[name]!)
            .Where(property => !Equals(property.GetValue(onTheHost), property.GetValue(before))
                               && !Equals(property.GetValue(onTheHost), property.GetValue(edited)))
            .Select(property => new Conflict(property.DisplayName, Shown(property, property.GetValue(onTheHost)), Shown(property, property.GetValue(edited))))];

    /// <summary>The host's configuration with the operator's <paramref name="changed"/> fields set on it: what the write sends.</summary>
    public static PlantItemConfigDto Merge(IReadOnlyList<string> changed, PlantItemConfigDto edited, PlantItemConfigDto onTheHost)
    {
        var merged = PropertyModel.Copy(onTheHost);
        foreach (var name in changed)
        {
            var property = Properties[name]!;
            property.SetValue(merged, property.GetValue(edited));
        }

        return merged;
    }

    /// <summary>
    /// A value as the line names it: the property's own converter in the invariant culture - the one
    /// the device list shows its cells with - a text in quotes, and "(empty)" for nothing.
    /// </summary>
    private static string Shown(PropertyDescriptor property, object? value)
    {
        if (value is null) return "(empty)";
        if (value is string text) return text.Length == 0 ? "(empty)" : $"\"{text}\"";
        try
        {
            return property.Converter.ConvertToString(null, CultureInfo.InvariantCulture, value) ?? value.ToString() ?? "";
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or FormatException)
        {
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        }
    }
}
