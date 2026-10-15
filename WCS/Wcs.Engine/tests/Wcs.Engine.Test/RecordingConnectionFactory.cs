using Wcs.Connection.Interface;
using Wcs.Engine.Collections;

namespace Wcs.Engine.Test;

/// <summary>
/// A driver factory that records what the engine hands it: the services of every
/// <see cref="Attach"/> and the settings of every <see cref="Create"/>, in order. What it builds is
/// up to <see cref="Build"/> (nothing by default).
/// </summary>
internal sealed class RecordingConnectionFactory : IConnectionFactory
{
    private readonly List<string> _calls = [];

    public Func<ConnectionSettings, IConnection?> Build { get; init; } = _ => null;

    /// <summary>The services of the last <see cref="Attach"/>.</summary>
    public IConnectionServices? Services { get; private set; }

    public List<ConnectionSettings> Created { get; } = [];

    /// <summary>"Attach" and "Create:{id}", in the order the engine called them.</summary>
    public IReadOnlyList<string> Calls => _calls;

    public void Attach(IConnectionServices services)
    {
        Services = services;
        _calls.Add("Attach");
    }

    public IConnection? Create(ConnectionSettings settings)
    {
        Created.Add(settings);
        _calls.Add($"Create:{settings.Id}");
        return Build(settings);
    }
}

/// <summary>
/// Installs a driver factory on <see cref="DeviceConnections"/> for the length of a test and puts
/// the previous one back (the factory is static, and the suite runs serially).
/// </summary>
internal sealed class ConnectionFactoryScope : IDisposable
{
    private readonly IConnectionFactory? _previous = DeviceConnections.ConnectionFactory;

    private ConnectionFactoryScope(IConnectionFactory? factory) => DeviceConnections.UseConnectionFactory(factory);

    public static ConnectionFactoryScope Install(IConnectionFactory? factory) => new(factory);

    public void Dispose() => DeviceConnections.UseConnectionFactory(_previous);
}

/// <summary>
/// Sets <see cref="DeviceConnections.AllowSimulatorForMissingConnections"/> for the length of a test
/// and puts the previous value back (it is static, and the suite runs serially).
/// </summary>
internal sealed class SimulatorFallbackScope : IDisposable
{
    private readonly bool _previous = DeviceConnections.AllowSimulatorForMissingConnections;

    private SimulatorFallbackScope(bool allow) => DeviceConnections.AllowSimulatorForMissingConnections = allow;

    public static SimulatorFallbackScope Allow(bool allow) => new(allow);

    public void Dispose() => DeviceConnections.AllowSimulatorForMissingConnections = _previous;
}
