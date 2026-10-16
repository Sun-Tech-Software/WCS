namespace Wcs.Api.Client;

public sealed class WcsClientOptions
{
    /// <summary>
    /// L'origine dell'host: schema, nome e porta, <b>senza percorso</b> — per esempio
    /// <c>http://wcs-host:5580</c>.
    /// </summary>
    /// <remarks>
    /// Il client vi appende da se' il prefisso dell'API e la rotta dell'hub costruendo un
    /// <see cref="Uri"/> relativo: un indirizzo con un percorso (<c>http://host/wcs/</c>) vedrebbe
    /// l'ultimo segmento sostituito, non conservato, e le richieste finirebbero altrove. Se l'host
    /// sta dietro a un percorso di un reverse proxy, quel percorso va tolto dal proxy, non messo qui.
    /// </remarks>
    public Uri BaseAddress { get; set; } = new("http://localhost:5580");

    /// <summary>
    /// <see cref="BaseAddress"/>, checked: an absolute http or https origin with no path, query,
    /// fragment or credentials. Without the check <c>http://host/wcs</c> and <c>http://host/wcs/</c>
    /// sent the requests to two different places, neither of them said.
    /// </summary>
    internal Uri Origin()
    {
        var address = BaseAddress ?? throw new ArgumentException("WcsClientOptions.BaseAddress is required");
        if (!address.IsAbsoluteUri || (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException($"WcsClientOptions.BaseAddress '{address}' must be an absolute http or https address");
        if (address.AbsolutePath != "/" || address.Query.Length > 0 || address.Fragment.Length > 0 || address.UserInfo.Length > 0)
            throw new ArgumentException($"WcsClientOptions.BaseAddress '{address}' must be an origin only, as http://wcs-host:5580: " +
                                        "no path, query, fragment or credentials");
        return address;
    }
    public string ApiKey { get; set; } = string.Empty;
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
}
