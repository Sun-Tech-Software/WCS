using Microsoft.Extensions.Logging;
using Wcs.Host.Security;
using Xunit;

namespace Wcs.Host.Test;

/// <summary>
/// A short API key is reported at startup, and only reported: the host still starts with it, and
/// the key itself never reaches the log.
/// </summary>
public sealed class ApiKeyStrengthTest
{
    private sealed class CapturingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }

    private static readonly string LongKey = new('a', ApiKeySecurity.RecommendedMinimumLength);

    [Fact]
    public void AShortKeyIsReportedWithoutWritingItDown()
    {
        var logger = new CapturingLogger();
        var options = new WcsHostOptions { ApiKey = "collaudo-2026", ReadOnlyApiKey = "sola-lettura" };

        ApiKeySecurity.Validate(options);
        ApiKeySecurity.WarnIfWeak(options, logger);

        Assert.Equal(2, logger.Warnings.Count);
        Assert.Contains("Wcs:ApiKey has 13 characters", logger.Warnings[0]);
        Assert.Contains("Wcs:ReadOnlyApiKey has 12 characters", logger.Warnings[1]);
        Assert.DoesNotContain(logger.Warnings, w => w.Contains("collaudo-2026") || w.Contains("sola-lettura"));
    }

    [Fact]
    public void LongKeysAndAMissingReadOnlyKeyAreNotReported()
    {
        var logger = new CapturingLogger();

        ApiKeySecurity.WarnIfWeak(new WcsHostOptions { ApiKey = LongKey }, logger);

        Assert.Empty(logger.Warnings);
    }
}
