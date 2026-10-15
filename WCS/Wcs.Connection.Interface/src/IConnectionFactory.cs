namespace Wcs.Connection.Interface;

/// <summary>
/// Builds the connections of a field driver: the plug point between the engine and a driver that
/// talks to real PLCs.
/// </summary>
/// <remarks>
/// <para>
/// The engine knows only this contract and <see cref="IConnection"/>. The host picks the factory
/// from its configuration (the key <c>Wcs:ConnectionFactory</c>, an assembly-qualified type name
/// loaded by reflection from the host folder) and hands it to the engine before the plant is
/// loaded. The type needs a public parameterless constructor.
/// </para>
/// <para>
/// Without a factory the engine serves only the devices whose connection type is <c>Test</c>,
/// through the loopback driver (<c>Wcs.Connection.Test</c>). A device configured for a real
/// connection (<c>Connected</c>) then gets no connection: the engine writes an error in the
/// navigator log and puts the device <c>OffLine</c>, so that no mission waits on it.
/// </para>
/// <para>
/// Call order: <see cref="Attach"/> when the factory is installed and again before every plant
/// load, then <see cref="Create"/> once per configured connection, on the engine's loading thread.
/// </para>
/// </remarks>
public interface IConnectionFactory
{
    /// <summary>
    /// Receives the engine services the driver writes to (command identifiers, connection status,
    /// driver log, message log). Each call hands over a fresh set, which replaces the previous one.
    /// </summary>
    /// <param name="services">The services of the engine that hosts the driver.</param>
    void Attach(IConnectionServices services);

    /// <summary>
    /// Builds the connection described by one row of the driver settings.
    /// </summary>
    /// <param name="settings">The settings of the connection.</param>
    /// <returns>
    /// The connection, not yet opened (the engine opens it when it starts running), or <c>null</c>
    /// when the settings are not enough for this driver: the devices that point to it then get no
    /// connection, go OffLine, and Run is refused while they lack it (only the explicit setting
    /// <c>Wcs:AllowSimulatorForMissingConnections</c>, refused in Production, serves them with the
    /// loopback driver instead).
    /// </returns>
    IConnection? Create(ConnectionSettings settings);
}
