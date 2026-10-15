using System.ComponentModel;
using System.Globalization;

namespace Wcs.Api.Contracts.Dtos;

/// <summary>
/// Mostra come testo il codice numerico di un enum: la griglia delle proprietà dell'Editor legge
/// un <c>short</c> e vede la <see cref="DescriptionAttribute"/> del valore corrispondente, con
/// l'elenco dei valori ammessi in una casella a discesa.
/// </summary>
/// <remarks>
/// Nel motore le stesse proprietà erano tipizzate con l'enum e ci pensava
/// <c>Helper.Converters.PropertyTypeConverter</c>; sul DTO sono numeri, perché i contratti non
/// vincolano il client a conoscere gli enum del motore. Senza questo convertitore la griglia
/// mostrerebbe la cifra al posto del testo. Un codice che non appartiene all'enum non è un errore:
/// si mostra così com'è, perché il DTO può arrivare da una versione più recente dell'host.
/// </remarks>
/// <typeparam name="TEnum">L'enum che il codice rappresenta.</typeparam>
public sealed class EnumCodeConverter<TEnum> : TypeConverter where TEnum : struct, Enum
{
    public override bool GetStandardValuesSupported(ITypeDescriptorContext? context) => true;

    /// <summary>Solo i valori dell'enum: un codice scritto a mano non ha nessun significato per il motore.</summary>
    public override bool GetStandardValuesExclusive(ITypeDescriptorContext? context) => true;

    public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? context) =>
        new(Enum.GetValues<TEnum>().Select(Code).ToArray());

    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) =>
        sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

    public override bool CanConvertTo(ITypeDescriptorContext? context, Type? destinationType) =>
        destinationType == typeof(string) || base.CanConvertTo(context, destinationType);

    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
    {
        if (value is not string text) return base.ConvertFrom(context, culture, value);

        foreach (var candidate in Enum.GetValues<TEnum>())
            if (string.Equals(Text(candidate), text, StringComparison.InvariantCultureIgnoreCase) ||
                string.Equals(candidate.ToString(), text, StringComparison.InvariantCultureIgnoreCase))
                return Code(candidate);

        return short.Parse(text, CultureInfo.InvariantCulture);
    }

    public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType)
    {
        if (destinationType != typeof(string) || value is null) return base.ConvertTo(context, culture, value, destinationType);

        var code = Convert.ToInt16(value, CultureInfo.InvariantCulture);
        foreach (var candidate in Enum.GetValues<TEnum>())
            if (Code(candidate) == code)
                return Text(candidate);

        return code.ToString(CultureInfo.InvariantCulture);
    }

    private static short Code(TEnum value) => Convert.ToInt16(value, CultureInfo.InvariantCulture);

    /// <summary>La descrizione del valore, o il suo nome se non ne ha una: è quello che si legge nella griglia.</summary>
    private static string Text(TEnum value)
    {
        var name = value.ToString();
        var field = typeof(TEnum).GetField(name);
        return field?.GetCustomAttributes(typeof(DescriptionAttribute), false) is [DescriptionAttribute description]
            ? description.Description
            : name;
    }
}
