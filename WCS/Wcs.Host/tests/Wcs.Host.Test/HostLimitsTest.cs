using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Wcs.Host.Test;

/// <summary>
/// Il limite di Kestrel sul corpo delle richieste: 30 MB predefiniti non bastano al PUT del
/// diagramma (JSON base64, 48 MB sull'impianto di prova), che senza questa configurazione
/// riceveva un 413. Vale anche col TestServer perché <c>ConfigureKestrel</c> non fa altro che
/// registrare un <c>IConfigureOptions&lt;KestrelServerOptions&gt;</c>.
/// </summary>
[Collection("host")]
public sealed class HostLimitsTest(HostFixture host)
{
    [Fact]
    public void TheRequestBodyLimitIs256Megabytes()
    {
        var kestrel = host.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value;
        Assert.Equal(256L * 1024 * 1024, kestrel.Limits.MaxRequestBodySize);
    }
}
