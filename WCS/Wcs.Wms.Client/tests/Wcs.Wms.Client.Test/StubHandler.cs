using System.Net;
using System.Text;

namespace Wcs.Wms.Client.Test;

/// <summary>
/// Un gestore HTTP finto: risponde sempre lo stesso stato e lo stesso corpo, e conserva l'ultima
/// richiesta ricevuta perché un test la ispeziona (l'intestazione della chiave, il metodo, il
/// percorso).
/// </summary>
internal sealed class StubHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }

    /// <summary>
    /// Il corpo dell'ultima richiesta, letto qui e non dal test: <c>HttpClient</c> smaltisce il
    /// contenuto della richiesta appena la chiamata finisce, e rileggerlo dopo solleva
    /// <see cref="ObjectDisposedException"/>.
    /// </summary>
    public string? LastRequestBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        LastRequestBody = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }
}
