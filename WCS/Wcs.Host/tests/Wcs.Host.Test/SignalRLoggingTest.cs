using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;
using Microsoft.Extensions.Options;
using Xunit;

namespace Wcs.Host.Test;

/// <summary>
/// Queue item 13, the owner's decision of 28/9: a SignalR connection the host closes - a message from
/// the browser over the circuit's ceiling, for one - is written to the host's log. SignalR writes why
/// under <see cref="Category"/> at Debug and at no other level, so under the "Microsoft.AspNetCore":
/// "Warning" of appsettings.json a circuit that died left nothing on the server's side.
/// </summary>
/// <remarks>
/// Twice in appsettings.json, and the second is not redundant: on Windows the default builder
/// (<c>WebApplication.CreateBuilder</c>, through <c>AddDefaultServices</c>) adds the Event Log provider
/// with a filter IN CODE that lets only Warning and above through - read here, it is the first
/// assertion - and a level under <c>Logging:LogLevel</c> alone does not get past it; a rule under
/// <c>Logging:EventLog:LogLevel</c> for the category does, because a rule naming both the provider and
/// the category is more specific than the code's, which names the provider alone.
/// <para>
/// What this test does NOT see: the host running as a Windows service. Outside a service, where the
/// test runs, <c>UseWindowsService</c> registers nothing; as a service it adds the Event Log provider
/// again with its source name, and - as far as the framework's source was read, not measured here - no
/// filter of its own, so the same rule applies.
/// </para>
/// </remarks>
[Collection("host")]
public sealed class SignalRLoggingTest(HostFixture host)
{
    private const string Category = "Microsoft.AspNetCore.SignalR.HubConnectionHandler";

    [Fact]
    public void ADroppedConnectionIsWrittenToEveryLogTheHostKeeps()
    {
        var rules = host.Services.GetRequiredService<IOptions<LoggerFilterOptions>>().Value.Rules;
        var eventLog = typeof(EventLogLoggerProvider).FullName;
        bool IsEventLog(LoggerFilterRule rule) => rule.ProviderName == eventLog || rule.ProviderName == "EventLog";

        // The claim the second entry rests on, read and not assumed: on Windows the Event Log has ONE
        // filter in code, for every category - the default builder's.
        if (OperatingSystem.IsWindows())
            Assert.Single(rules, rule => IsEventLog(rule) && rule.Filter is not null && rule.CategoryName is null);

        Assert.Contains(rules, rule => rule.ProviderName is null && rule.CategoryName == Category && rule.LogLevel == LogLevel.Debug);
        Assert.Contains(rules, rule => IsEventLog(rule) && rule.CategoryName == Category && rule.LogLevel == LogLevel.Debug);

        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger(Category);
        Assert.True(logger.IsEnabled(LogLevel.Debug));
    }
}
