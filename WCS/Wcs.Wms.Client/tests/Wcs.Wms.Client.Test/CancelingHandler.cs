namespace Wcs.Wms.Client.Test;

/// <summary>
/// Un gestore HTTP finto che simula l'annullamento chiesto dal chiamante: solleva una
/// <see cref="TaskCanceledException"/> che porta il <see cref="CancellationToken"/> ricevuto,
/// come farebbe l'implementazione vera quando quel token viene annullato durante l'invio.
/// </summary>
internal sealed class CancelingHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw new TaskCanceledException("richiesta annullata dal chiamante", null, cancellationToken);
}
