namespace Wcs.Wms.Client.Test;

/// <summary>Un gestore HTTP finto che solleva sempre la stessa eccezione, per simulare un trasporto rotto.</summary>
internal sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw exception;
}
