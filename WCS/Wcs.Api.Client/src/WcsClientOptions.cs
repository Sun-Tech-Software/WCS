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
    public string ApiKey { get; set; } = string.Empty;
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
}
