using System.Globalization;
using System.Text.RegularExpressions;

namespace Wcs.WebEditor.Drawio.Components;

/// <summary>
/// A column of a read-only list built from a DTO the way the WinForms grids built theirs
/// (compito 3, rows 60 and 61): <c>Helpers.AddColumnsToGrid</c> turned on the grid's
/// <c>AutoPopulateColumns</c> - one column per property, in the record's own order, the caption
/// the property's name split into words - and showed every <c>Guid</c>/<c>Guid?</c> column as a
/// device, by name, except the ones it was told to leave alone.
/// </summary>
internal sealed class DrawioDtoColumn<T>
{
    private DrawioDtoColumn(string key, string label, bool isDevice, Func<T, object?> value)
    {
        Key = key;
        Label = label;
        IsDevice = isDevice;
        Value = value;
    }

    /// <summary>The property's name: what <c>data-column</c> carries.</summary>
    public string Key { get; }

    public string Label { get; }

    /// <summary>A device's identifier, shown by the device's name.</summary>
    public bool IsDevice { get; }

    public Func<T, object?> Value { get; }

    /// <summary>
    /// Every property of <typeparamref name="T"/>, in the order of its primary constructor (the
    /// record's declaration order - reflection on the properties does not promise one).
    /// <paramref name="notDevices"/> are the <c>Guid</c> columns that are identifiers of something
    /// else and are shown as they are.
    /// </summary>
    public static IReadOnlyList<DrawioDtoColumn<T>> Of(params string[] notDevices)
    {
        var constructor = typeof(T).GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
        return
        [
            .. constructor.GetParameters().Select(parameter =>
            {
                var property = typeof(T).GetProperty(parameter.Name!)
                    ?? throw new InvalidOperationException($"{typeof(T).Name} has no property {parameter.Name}.");
                var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                var isDevice = type == typeof(Guid) && !notDevices.Contains(property.Name);
                return new DrawioDtoColumn<T>(property.Name, Words().Replace(property.Name, " "), isDevice,
                    row => property.GetValue(row));
            }),
        ];
    }

    /// <summary>The text a cell shows: a device by its name (<paramref name="deviceName"/>), everything else as the grid printed it.</summary>
    public string TextOf(T row, Func<Guid, string> deviceName) => Value(row) switch
    {
        null => "",
        Guid id when IsDevice => id == Guid.Empty ? "" : deviceName(id),
        Guid id => id == Guid.Empty ? "" : id.ToString(),
        bool flag => flag ? "Yes" : "No",
        DateTime date => date == default ? "" : date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        decimal number => number.ToString("0.##########", CultureInfo.InvariantCulture),
        IFormattable formattable when formattable is not Enum => formattable.ToString(null, CultureInfo.InvariantCulture),
        var other => other.ToString() ?? "",
    };

    /// <summary>What a column sorts on: a device by its name, a value by its own order.</summary>
    public object? SortValue(T row, Func<Guid, string> deviceName) =>
        IsDevice ? TextOf(row, deviceName) : Value(row);

    /// <summary>Two sort values of the same column: text without regard to case, values by their own order, nulls first.</summary>
    public static int Compare(object? a, object? b)
    {
        if (a is null) return b is null ? 0 : -1;
        if (b is null) return 1;
        if (a is string left && b is string right) return StringComparer.OrdinalIgnoreCase.Compare(left, right);
        return a is IComparable comparable && a.GetType() == b.GetType()
            ? comparable.CompareTo(b)
            : StringComparer.Ordinal.Compare(a.ToString(), b.ToString());
    }

    private static readonly Regex WordBoundary = new("(?<=[a-z0-9])(?=[A-Z])", RegexOptions.Compiled);

    private static Regex Words() => WordBoundary;
}
