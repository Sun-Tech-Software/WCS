using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

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

    /// <summary>The length below which a key is reported as weak at startup.</summary>
    internal const int RecommendedMinimumLength = 32;

    /// <summary>
    /// A warning for each key shorter than <see cref="RecommendedMinimumLength"/> characters. Only a
    /// warning: installations with a short key keep starting, and the operator replaces it with a
    /// long random value when they can. The key itself never goes into the log.
    /// </summary>
    internal static void WarnIfWeak(WcsHostOptions options, ILogger logger)
    {
        foreach (var (name, key) in new[] { ("Wcs:ApiKey", options.ApiKey), ("Wcs:ReadOnlyApiKey", options.ReadOnlyApiKey) })
            if (!string.IsNullOrWhiteSpace(key) && key.Length < RecommendedMinimumLength)
                logger.LogWarning(
                    "{Setting} has {Length} characters: use a random value of at least {Minimum} " +
                    "(for example 32 random bytes in hex), the shorter it is the easier it is to guess",
                    name, key.Length, RecommendedMinimumLength);
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
