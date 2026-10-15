using Wcs.Api.Contracts;

namespace Wcs.Api.Client;

/// <summary>
/// Aggiunge l'intestazione <see cref="ApiRoutes.EditHolderHeader"/> a ogni richiesta, se
/// <paramref name="holder"/> restituisce un valore in quel momento. A differenza di
/// <see cref="ApiKeyHandler"/> il titolare non e' fisso per la vita del client: si legge da una
/// funzione invece che da un campo, cosi' <c>WcsClient.EditHolder</c> puo' cambiare (o tornare
/// nullo) durante la vita della sessione senza dover ricostruire il client.
/// </summary>
public sealed class EditHolderHandler(Func<string?> holder, HttpMessageHandler? inner = null)
    : DelegatingHandler(inner ?? new HttpClientHandler())
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Remove(ApiRoutes.EditHolderHeader);
        var value = holder();
        // Nullo o vuoto: nessuna intestazione parte, come un client che non ha mai chiesto il
        // permesso - non si manda un titolare vuoto che l'host dovrebbe comunque rifiutare.
        if (!string.IsNullOrWhiteSpace(value))
            request.Headers.Add(ApiRoutes.EditHolderHeader, value);
        return base.SendAsync(request, cancellationToken);
    }
}
