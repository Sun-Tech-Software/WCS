using System;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Helper.Converters;

public class PropertyTypeConverter<TEnum> : EnumConverter where TEnum : struct, Enum
{
    public PropertyTypeConverter() : base(typeof(TEnum)) { }

    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
        => sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

    public override bool CanConvertTo(ITypeDescriptorContext? context, Type? destinationType)
        => destinationType == typeof(string) || base.CanConvertTo(context, destinationType);

    public override object ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
    {
        if (value == null) throw new ArgumentNullException(nameof(value));

        string stringValue = value.ToString() ?? string.Empty;

        foreach (var field in typeof(TEnum).GetFields())
        {
            if (Attribute.GetCustomAttribute(field, typeof(DescriptionAttribute)) is DescriptionAttribute desc &&
                string.Equals(desc.Description, stringValue, StringComparison.InvariantCultureIgnoreCase))
            {
                return Enum.Parse(typeof(TEnum), field.Name);
            }
        }

        return Enum.Parse(typeof(TEnum), stringValue, ignoreCase: true);
    }

    public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType)
    {
        if (value == null || destinationType != typeof(string))
            return base.ConvertTo(context, culture, value, destinationType);

        var name = Enum.GetName(typeof(TEnum), value);
        if (name == null) return value.ToString();

        var field = typeof(TEnum).GetField(name);
        if (field != null && Attribute.GetCustomAttribute(field, typeof(DescriptionAttribute)) is DescriptionAttribute desc)
        {
            return desc.Description;
        }

        return name;
    }
}


public class PropertyTypeConverter : EnumConverter
{
    private readonly Type _enumType;

    public PropertyTypeConverter(Type type) : base(type)
    {
        _enumType = type ?? throw new ArgumentNullException(nameof(type));
    }

    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
    {
        return sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);
    }

    public override bool CanConvertTo(ITypeDescriptorContext? context, [NotNullWhen(true)] Type? destinationType)
    {
        return destinationType == typeof(string) || base.CanConvertTo(context, destinationType);
    }

    public override object ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
    {
        if (value is null)
            throw new ArgumentNullException(nameof(value));

        string stringValue = value.ToString() ?? string.Empty;

        // Cerca per DescriptionAttribute
        foreach (var field in _enumType.GetFields())
        {
            if (Attribute.GetCustomAttribute(field, typeof(DescriptionAttribute)) is DescriptionAttribute description &&
                string.Equals(description.Description, stringValue, StringComparison.InvariantCultureIgnoreCase))
            {
                return Enum.Parse(_enumType, field.Name);
            }
        }

        // Fallback al nome enum
        return Enum.Parse(_enumType, stringValue, ignoreCase: true);
    }

    public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType)
    {
        if (value == null || destinationType != typeof(string))
            return base.ConvertTo(context, culture, value, destinationType);

        string? enumName = Enum.GetName(_enumType, value);
        if (enumName == null) return value.ToString();

        var field = _enumType.GetField(enumName);
        if (field != null && Attribute.GetCustomAttribute(field, typeof(DescriptionAttribute)) is DescriptionAttribute description)
        {
            return description.Description;
        }

        return enumName;
    }
}