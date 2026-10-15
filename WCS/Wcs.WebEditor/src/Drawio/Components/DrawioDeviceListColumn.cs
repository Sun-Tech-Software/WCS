using System.Globalization;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Properties;

namespace Wcs.WebEditor.Drawio.Components;

/// <summary>How a column of the device list shows its value and how a cell of it is changed.</summary>
internal enum DeviceListColumnKind
{
    /// <summary>The live status of the device (the channel, not the configuration): read-only.</summary>
    LiveStatus,

    /// <summary>The load unit the live status says the device holds: read-only.</summary>
    LiveLoadUnit,

    /// <summary>A configuration field the contract marks <c>[ReadOnly(true)]</c> - the device's own identifier.</summary>
    ReadOnly,

    /// <summary>A <c>Guid</c> column: chosen from the list of the plant's devices, as the WinForms grid did.</summary>
    Device,

    /// <summary>A code that carries an enum (<c>EnumCodeConverter</c>): chosen from the enum's values.</summary>
    EnumCode,

    Bool,

    /// <summary>Every other field: typed, and read through the converter the contract resolves for its type.</summary>
    Typed,
}

/// <summary>
/// A column of the device list (row 58, compito 2): the configuration fields come from
/// <see cref="PropertyModel"/> - the same reading of the contract the properties panel makes, not a
/// list written here - plus the two live columns in front.
/// </summary>
/// <remarks>
/// <para>
/// <b>Which columns are lists of devices.</b> The WinForms grid (<c>Helpers.AddColumnsToGrid</c>) puts
/// the device drop-down on EVERY <c>Guid</c>/<c>Guid?</c> column except the ones it is told to leave
/// alone (<c>ItemId</c>): nine columns, not only the four the property grid turns into pickers
/// (<see cref="PropertyField.IsItemReference"/>). This list follows the grid it replaces.
/// </para>
/// <para>
/// <b>Order.</b> The WinForms grid orders its columns alphabetically; here the live status, the
/// load unit, the name and the device type come first - what tells one row from another - and the
/// rest follow alphabetically by the label the contract gives them, with the identifier last.
/// </para>
/// </remarks>
internal sealed class DeviceListColumn
{
    private DeviceListColumn(string key, string label, string help, DeviceListColumnKind kind, PropertyField? field)
    {
        Key = key;
        Label = label;
        Help = help;
        Kind = kind;
        Field = field;
    }

    /// <summary>The property's name for a configuration column (what <c>data-property</c> carries), or a name of this list's own for the live ones.</summary>
    public string Key { get; }

    public string Label { get; }

    public string Help { get; }

    public DeviceListColumnKind Kind { get; }

    /// <summary>The configuration field behind the column; null for the two live ones.</summary>
    public PropertyField? Field { get; }

    public bool IsEditable => Field is not null && Kind is not (DeviceListColumnKind.ReadOnly or DeviceListColumnKind.LiveStatus or DeviceListColumnKind.LiveLoadUnit);

    public bool IsNumeric => Field?.Descriptor.PropertyType == typeof(int) || Field?.Descriptor.PropertyType == typeof(decimal);

    public static IReadOnlyList<DeviceListColumn> All { get; } = Build();

    private static IReadOnlyList<DeviceListColumn> Build()
    {
        var fields = PropertyModel.BuildGroups().SelectMany(group => group.Fields).ToList();
        string[] first = [nameof(PlantItemConfigDto.Name), nameof(PlantItemConfigDto.DeviceType)];
        var ordered = first.Select(name => fields.Single(field => field.PropertyName == name))
            .Concat(fields.Where(field => !first.Contains(field.PropertyName) && !field.IsReadOnly)
                .OrderBy(field => field.Label, StringComparer.Ordinal))
            .Concat(fields.Where(field => field.IsReadOnly).OrderBy(field => field.Label, StringComparer.Ordinal));

        return
        [
            new("Status", "Status", "The device's live status, as the engine tells it on the live channel.", DeviceListColumnKind.LiveStatus, null),
            new("LoadUnit", "Load unit", "The load unit the device holds, as the engine tells it on the live channel.", DeviceListColumnKind.LiveLoadUnit, null),
            .. ordered.Select(field => new DeviceListColumn(field.PropertyName, field.Label, $"{field.Category}: {field.Help}", KindOf(field), field)),
        ];
    }

    private static DeviceListColumnKind KindOf(PropertyField field)
    {
        var type = field.Descriptor.PropertyType;
        if (field.IsReadOnly) return DeviceListColumnKind.ReadOnly;
        if (type == typeof(Guid) || type == typeof(Guid?)) return DeviceListColumnKind.Device;
        if (field.IsEnumCoded) return DeviceListColumnKind.EnumCode;
        if (type == typeof(bool)) return DeviceListColumnKind.Bool;
        return DeviceListColumnKind.Typed;
    }

    /// <summary>
    /// The text of a configuration cell, or of what is being typed into it: the converter's own, in
    /// the invariant culture (a number input speaks it, and the properties panel reads it that way).
    /// </summary>
    public string TextOf(object? value) => Field is null
        ? ""
        : Field.Descriptor.Converter.ConvertToString(null, CultureInfo.InvariantCulture, value) ?? "";

    /// <summary>
    /// Reads what was typed or chosen into a value of the field's type; false when it is not one
    /// (letters in a number, an emptied number box), and nothing is written then.
    /// </summary>
    public bool TryRead(string text, bool check, out object? value)
    {
        value = null;
        if (Field is null) return false;
        switch (Kind)
        {
            case DeviceListColumnKind.Bool:
                value = check;
                return true;
            case DeviceListColumnKind.Device:
                if (string.IsNullOrEmpty(text)) return true;
                if (!Guid.TryParse(text, out var device)) return false;
                value = device;
                return true;
            case DeviceListColumnKind.EnumCode:
                if (!short.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code)) return false;
                value = code;
                return true;
            case DeviceListColumnKind.Typed:
                try
                {
                    value = Field.Descriptor.Converter.ConvertFromString(null, CultureInfo.InvariantCulture, text);
                    return true;
                }
                catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException or NotSupportedException)
                {
                    return false;
                }
            default:
                return false;
        }
    }

    /// <summary>The values an <see cref="DeviceListColumnKind.EnumCode"/> column can take, as (code, text).</summary>
    public IEnumerable<(short Code, string Text)> Codes() => Field is null || Kind != DeviceListColumnKind.EnumCode
        ? []
        : Field.Descriptor.Converter.GetStandardValues()!.Cast<object>()
            .Select(candidate => (Convert.ToInt16(candidate, CultureInfo.InvariantCulture), Field.Descriptor.Converter.ConvertToString(candidate) ?? ""));
}
