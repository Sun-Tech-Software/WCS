using Wcs.Api.Contracts;

namespace Wcs.Api.Client;

public sealed class ApiKeyHandler(string apiKey, HttpMessageHandler? inner = null) : DelegatingHandler(inner ?? new HttpClientHandler())
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Remove(ApiRoutes.ApiKeyHeader);
        request.Headers.Add(ApiRoutes.ApiKeyHeader, apiKey);
        return base.SendAsync(request, cancellationToken);
    }
}
