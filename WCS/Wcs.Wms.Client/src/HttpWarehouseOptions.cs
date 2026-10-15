namespace Wcs.Wms.Client;

/// <summary>
/// Le opzioni per parlare con il gestionale. L'indirizzo lo porta l'<see cref="System.Net.Http.HttpClient"/>
/// tramite <c>BaseAddress</c>; qui sta solo la chiave, che non compare mai nel repository.
/// </summary>
public sealed class HttpWarehouseOptions
{
    public required string ApiKey { get; init; }
}
