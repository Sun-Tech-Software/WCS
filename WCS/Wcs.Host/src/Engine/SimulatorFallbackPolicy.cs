namespace Wcs.Host.Engine;

/// <summary>
/// The start-up check of <c>Wcs:AllowSimulatorForMissingConnections</c>: the explicit fallback that
/// lets a device of connection type Connected whose connection was not built be served by the
/// loopback driver (the simulator). In Production, or in an environment with no name, the host refuses to start with it on: a simulated
/// device makes the engine believe it drives a machine while nothing moves. Elsewhere it is accepted,
/// with a warning at every start.
/// </summary>
/// <remarks>
/// The engine side is <c>DeviceConnections.AllowSimulatorForMissingConnections</c>, which
/// <see cref="EngineHostedService"/> sets from the same setting.
/// </remarks>
internal static class SimulatorFallbackPolicy
{
    public const string SettingKey = "Wcs:AllowSimulatorForMissingConnections";

    internal static void Enforce(bool allow, IHostEnvironment environment, ILogger logger)
    {
        if (!allow) return;

        // An environment with no name is not a test installation: ASP.NET reads an unset
        // ASPNETCORE_ENVIRONMENT as Production, and an empty or blank one must not open a way around it.
        if (environment.IsProduction() || string.IsNullOrWhiteSpace(environment.EnvironmentName))
            throw new InvalidOperationException(
                $"{SettingKey} is true in the Production environment (or in one with no name): the host does not start. With it, a device of " +
                "connection type Connected whose connection was not built is served by the simulator, and the engine " +
                "believes it drives a machine while nothing moves. Remove the setting (appsettings.json, or the " +
                "environment variable Wcs__AllowSimulatorForMissingConnections), or set it to false; it is meant only " +
                "for a test or development installation.");

        logger.LogWarning(
            "{Key} is true (environment {Environment}): a device of connection type Connected whose connection was " +
            "not built is served by the SIMULATOR instead of going OffLine, and Run is not refused for it. Never in production.",
            SettingKey, environment.EnvironmentName);
    }
}
