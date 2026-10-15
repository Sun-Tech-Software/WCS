using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Wcs.Host.Security;
using Xunit;

namespace Wcs.Host.Test;

public sealed class TransportSecurityPolicyTest
{
    [Theory]
    [InlineData("http://localhost:5580")]
    [InlineData("http://127.0.0.1:5580")]
    [InlineData("http://127.42.1.9:5580")]
    [InlineData("http://[::1]:5580")]
    [InlineData("https://0.0.0.0:5580")]
    [InlineData("https://wcs-impianto:5580")]
    public void LocalHttpAndEveryHttpsEndpointAreAccepted(string url)
    {
        var logger = new CapturingLogger();
        TransportSecurityPolicy.Enforce(ConfigurationWithEndpoint(url), allowInsecureRemoteHttp: false, logger);
        Assert.Empty(logger.CriticalMessages);
    }

    [Theory]
    [InlineData("http://0.0.0.0:5580")]
    [InlineData("http://*:5580")]
    [InlineData("http://+:5580")]
    [InlineData("http://[::]:5580")]
    [InlineData("http://192.0.2.20:5580")]
    [InlineData("http://wcs-impianto:5580")]
    public void RemoteHttpIsRejectedWithoutAnExplicitOptIn(string url)
    {
        var failure = Assert.Throws<InvalidOperationException>(() =>
            TransportSecurityPolicy.Enforce(ConfigurationWithEndpoint(url), false, new CapturingLogger()));
        Assert.Contains(url, failure.Message, StringComparison.Ordinal);
        Assert.Contains("Wcs__AllowInsecureRemoteHttp=true", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Kestrel:Endpoints:Http:Url", "http://localhost:bad:5580")]
    [InlineData("Kestrel:Endpoints:Http:Url", "http://127.0.0.1:bad:5580")]
    [InlineData("Kestrel:Endpoints:Http:Url", "http://[::1]bad:5580")]
    [InlineData("urls", "http://localhost:bad:5580")]
    [InlineData("urls", "http://127.0.0.1:bad:5580")]
    [InlineData("urls", "http://[::1]bad:5580")]
    public void AmbiguousLoopbackAuthorityIsRejectedWithoutOptIn(string key, string url)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [key] = url }).Build();

        var failure = Assert.Throws<InvalidOperationException>(() =>
            TransportSecurityPolicy.Enforce(configuration, false, new CapturingLogger()));

        Assert.Contains(url, failure.Message, StringComparison.Ordinal);
        Assert.Contains("Wcs__AllowInsecureRemoteHttp=true", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Kestrel:Endpoints:Http:Url", "http://localhost:bad:5580")]
    [InlineData("Kestrel:Endpoints:Http:Url", "http://127.0.0.1:bad:5580")]
    [InlineData("Kestrel:Endpoints:Http:Url", "http://[::1]bad:5580")]
    [InlineData("urls", "http://localhost:bad:5580")]
    [InlineData("urls", "http://127.0.0.1:bad:5580")]
    [InlineData("urls", "http://[::1]bad:5580")]
    public void AmbiguousLoopbackAuthorityRequiresCriticalWarningWithOptIn(string key, string url)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [key] = url }).Build();
        var logger = new CapturingLogger();

        TransportSecurityPolicy.Enforce(configuration, true, logger);

        Assert.Collection(logger.CriticalMessages,
            message => Assert.Contains(url, message, StringComparison.Ordinal));
    }

    [Fact]
    public void OneUnsafeEndpointRejectsTheWholeMultiEndpointConfiguration()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kestrel:Endpoints:Local:Url"] = "http://127.0.0.1:5580",
            ["Kestrel:Endpoints:Remote:Url"] = "http://0.0.0.0:5581"
        }).Build();
        Assert.Throws<InvalidOperationException>(() =>
            TransportSecurityPolicy.Enforce(configuration, false, new CapturingLogger()));
    }

    [Fact]
    public void AspNetCoreUrlsIsCheckedToo()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["urls"] = "https://+:5443;http://+:5580" }).Build();
        var failure = Assert.Throws<InvalidOperationException>(() =>
            TransportSecurityPolicy.Enforce(configuration, false, new CapturingLogger()));
        Assert.Contains("http://+:5580", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitOptInAllowsRemoteHttpAndWritesOneCriticalMessage()
    {
        var logger = new CapturingLogger();
        TransportSecurityPolicy.Enforce(ConfigurationWithEndpoint("http://0.0.0.0:5580"), true, logger);
        Assert.Collection(logger.CriticalMessages,
            message => Assert.Contains("http://0.0.0.0:5580", message, StringComparison.Ordinal));
    }

    [Fact]
    public void AProductionHostDoesNotStartOnRemoteHttpWithoutOptIn()
    {
        using var host = new RemoteHttpHost();

        var failure = Assert.Throws<InvalidOperationException>(() => host.CreateClient());

        Assert.Contains("http://0.0.0.0:5580", failure.Message, StringComparison.Ordinal);
    }

    private static IConfiguration ConfigurationWithEndpoint(string url) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kestrel:Endpoints:Http:Url"] = url
        }).Build();

    private sealed class CapturingLogger : ILogger
    {
        public List<string> CriticalMessages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Critical) CriticalMessages.Add(formatter(state, exception));
        }
    }

    private sealed class RemoteHttpHost : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Kestrel:Endpoints:Http:Url"] = "http://0.0.0.0:5580",
                    ["Wcs:ApiKey"] = HostFixture.ApiKey,
                    ["Wcs:AllowInsecureRemoteHttp"] = "false",
                    ["Wcs:StartEngine"] = "false"
                }));
        }
    }
}
