using System.Net;
using System.Text;
using Wcs.Api.Contracts;
using Wcs.Api.Contracts.Dtos;
using Xunit;

namespace Wcs.Api.Client.Test;

/// <summary>
/// What the client refuses at its edges: an address that is not a bare origin, and a successful
/// response whose body is <c>null</c> where the contract promises an object.
/// </summary>
public sealed class WcsClientBoundaryTest
{
    private sealed class NullBodyHandler : HttpMessageHandler
    {
        public Uri? Seen { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent("null", Encoding.UTF8, "application/json")
            });
        }
    }

    [Theory]
    [InlineData("http://wcs-host:5580/wcs")]
    [InlineData("http://wcs-host:5580/wcs/")]
    [InlineData("http://wcs-host:5580/?x=1")]
    [InlineData("http://user:secret@wcs-host:5580/")]
    [InlineData("ftp://wcs-host/")]
    public void AnAddressThatIsNotABareOriginIsRefused(string address)
    {
        var options = new WcsClientOptions { BaseAddress = new Uri(address) };

        Assert.Throws<ArgumentException>(() => new WcsClient(options, new NullBodyHandler()));
        Assert.Throws<ArgumentException>(() => new PlantEventsConnection(options, new NullBodyHandler()));
    }

    [Theory]
    [InlineData("http://wcs-host:5580")]
    [InlineData("http://wcs-host:5580/")]
    [InlineData("https://wcs-host")]
    public async Task AnOriginWithOrWithoutTheSlashReachesTheSameRoute(string address)
    {
        var handler = new NullBodyHandler();
        using var client = new WcsClient(new WcsClientOptions { BaseAddress = new Uri(address) }, handler);

        await Assert.ThrowsAsync<WcsApiException>(() => client.GetStatusAsync());

        Assert.Equal(ApiRoutes.Prefix + ApiRoutes.Status, handler.Seen!.AbsolutePath);
    }

    [Fact]
    public async Task ANullBodyOnSuccessIsAnErrorThatNamesTheRequest()
    {
        using var client = new WcsClient(new WcsClientOptions(), new NullBodyHandler());

        var failure = await Assert.ThrowsAsync<WcsApiException>(
            () => client.EnqueueCommandAsync(new CommandRequest(1001)));

        Assert.Contains("null body", failure.Message);
        Assert.Contains(ApiRoutes.Commands, failure.Message);
    }
}
