using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Wcs.Host.Security;

internal static class TransportSecurityPolicy
{
    internal static void Enforce(
        IConfiguration configuration, bool allowInsecureRemoteHttp, ILogger logger)
    {
        var insecure = ConfiguredUrls(configuration)
            .Where(IsRemoteHttp)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (insecure.Length == 0) return;

        var endpoints = string.Join(", ", insecure);
        if (!allowInsecureRemoteHttp)
            throw new InvalidOperationException(
                $"Endpoint HTTP remoto non consentito: {endpoints}. Configura HTTPS oppure, " +
                "soltanto su una LAN d'impianto segregata, imposta " +
                "Wcs__AllowInsecureRemoteHttp=true.");

        logger.LogCritical(
            "Trasporto HTTP remoto accettato esplicitamente su {Endpoints}: chi intercetta " +
            "X-Api-Key puo' comandare l'impianto. Configurare HTTPS.", endpoints);
    }

    private static IEnumerable<string> ConfiguredUrls(IConfiguration configuration)
    {
        var endpointUrls = configuration.GetSection("Kestrel:Endpoints").GetChildren()
            .Select(endpoint => endpoint["Url"]);
        return endpointUrls.Append(configuration["urls"])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .SelectMany(value => value!.Split(';',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static bool IsRemoteHttp(string configured)
    {
        if (!configured.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return false;
        // Usa lo stesso parser pubblico di Kestrel: troncare al primo ':' o ']' puo'
        // scambiare per loopback un host ambiguo che Kestrel associa a tutte le interfacce.
        var host = BindingAddress.Parse(configured).Host;
        if (host is "*" or "+" or "0.0.0.0" or "::") return true;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return false;
        return !IPAddress.TryParse(host, out var address) || !IPAddress.IsLoopback(address);
    }
}
