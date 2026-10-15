using System.Net;
using System.Net.Http.Json;
using Wcs.Api.Contracts.Dtos;

namespace Wcs.WebEditor.Test;

/// <summary>
/// Il gestore HTTP finto per <c>GET /wms/configuration-check</c> (compito 10): a differenza di
/// <see cref="PlantMutationHandler"/> (che tiene lo stato di un impianto intero) risponde a una sola
/// rotta, con lo stesso schema di "prossimo esito forzato" di <see cref="GeometryCountingHandler.ResponseStatus"/>
/// - qui pero' un esito per chiamata (si azzera dopo averlo consumato), perche' <c>ChecksTest</c>
/// deve poter provare un tentativo fallito seguito da uno riuscito nello stesso test.
/// </summary>
public sealed class WmsConfigurationCheckHandler : HttpMessageHandler
{
    public List<WmsConfigurationWarning> Warnings { get; set; } = [];
    public int Requests { get; private set; }

    /// <summary>
    /// L'ultima richiesta ricevuta (compito 10, correzione): a differenza di <see cref="PlantMutationHandler"/>
    /// questo gestore risponde a qualunque rotta, quindi da solo non prova che il pulsante abbia
    /// davvero chiamato <c>/wms/configuration-check?mode=0</c> - <c>LastRequest</c> lo rende
    /// verificabile, come <c>HttpWarehouseTest</c> fa su <c>PathAndQuery</c>.
    /// </summary>
    public HttpRequestMessage? LastRequest { get; private set; }

    private HttpStatusCode? _forcedStatus;
    private string _forcedBody = string.Empty;
    private bool _throwTimeout;

    /// <summary>La prossima risposta e' <paramref name="status"/>, con <paramref name="body"/> (vuoto se non lo si da': un problem document dell'host, per esempio).</summary>
    public void FailNext(HttpStatusCode status, string body = "")
    {
        _forcedStatus = status;
        _forcedBody = body;
    }

    public void ThrowTimeoutNext() => _throwTimeout = true;

    /// <summary>
    /// Tiene la risposta in sospeso finche' il chiamante non la rilascia (<see cref="Release"/>): il
    /// modo per <c>ChecksTest</c> di guardare lo schermo mentre la richiesta e' ancora in volo,
    /// invece che solo prima e dopo - serve a provare che il pulsante resta spento durante l'attesa
    /// (correzione del compito 10).
    /// </summary>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests++;
        LastRequest = request;

        if (_throwTimeout)
        {
            _throwTimeout = false;
            throw new TaskCanceledException("timeout finto: l'host non ha risposto entro il termine del client");
        }

        if (_forcedStatus is { } status)
        {
            _forcedStatus = null;
            return new HttpResponseMessage(status) { Content = new StringContent(_forcedBody) };
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Warnings) };
    }
}
