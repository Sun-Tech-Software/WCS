using System.Security.Cryptography;
using System.Text;

namespace Wcs.Host.Security;

internal enum ApiKeyAccess { None, ReadOnly, Full }

internal static class ApiKeySecurity
{
    internal const string Placeholder = "cambiami-prima-di-installare";

    internal static void Validate(WcsHostOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey) || options.ApiKey == Placeholder)
            throw new InvalidOperationException(
                "Wcs:ApiKey manca o e' quella di esempio: senza chiave l'host non parte.");

        if (string.IsNullOrWhiteSpace(options.ReadOnlyApiKey)) return;
        if (options.ReadOnlyApiKey == Placeholder)
            throw new InvalidOperationException(
                "Wcs:ReadOnlyApiKey e' quella di esempio: cambiala o rimuovila.");
        if (Matches(options.ReadOnlyApiKey, options.ApiKey))
            throw new InvalidOperationException(
                "Wcs:ReadOnlyApiKey deve essere diversa da Wcs:ApiKey.");
    }

    internal static ApiKeyAccess Authenticate(string provided, WcsHostOptions options)
    {
        if (Matches(provided, options.ApiKey)) return ApiKeyAccess.Full;
        return !string.IsNullOrWhiteSpace(options.ReadOnlyApiKey) &&
               Matches(provided, options.ReadOnlyApiKey)
            ? ApiKeyAccess.ReadOnly
            : ApiKeyAccess.None;
    }

    private static bool Matches(string provided, string expected) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(expected));
}
