using Wcs.Connection.Interface.CommandData;

namespace Wcs.Connection.Interface;

/// <summary>
///     One connection of a field driver: the channel through which the engine sends commands to the
///     devices of a PLC and receives what happens on them.
/// </summary>
/// <remarks>
///     <para>
///     Instances are built by <see cref="IConnectionFactory.Create" />, one per row of the driver
///     settings. The engine then drives them through this life cycle:
///     </para>
///     <list type="number">
///         <item>
///             <description>
///             <b>Plant load.</b> The previous connections are closed with
///             <see cref="CloseConnection" /> and released with <see cref="Dispose" />, then new ones
///             are built. A connection is not opened while it is being built.
///             </description>
///         </item>
///         <item>
///             <description>
///             <b>Engine start</b> (from Stop, not from Pause). For every device whose connection type
///             is <c>Connected</c>, the engine looks the connection up by <see cref="Id" />, sets
///             <see cref="Managed" /> to <c>true</c> on it, subscribes to its six events and calls
///             <see cref="OpenConnection" />. A connection that no device points to is never opened.
///             </description>
///         </item>
///         <item>
///             <description>
///             <b>Engine stop</b> (also when the host shuts down). On every connection with
///             <see cref="Managed" /> set, the engine unsubscribes from the events, calls
///             <see cref="CloseConnection" /> and sets <see cref="Managed" /> back to <c>false</c>.
///             The same object is opened again at the next start.
///             </description>
///         </item>
///     </list>
///     <para>
///     So <see cref="OpenConnection" /> must work again after <see cref="CloseConnection" />, and
///     <see cref="CloseConnection" /> must be harmless on a connection that was never opened or is
///     already closed. <see cref="Dispose" /> is called only at the next plant load, never at host
///     shutdown: release in <see cref="CloseConnection" /> whatever must not outlive the engine.
///     </para>
///     <para>
///     The command methods return immediately: the outcome comes back later through the events,
///     which the driver may raise from any thread. The engine catches an exception thrown by a
///     command method and logs it on the device; for a movement it also treats the step as failed.
///     </para>
/// </remarks>
public interface IConnection
{
    #region Public Events

    /// <summary>
    ///     A device reported a generic event. The engine finds the device by the Asi, Subitem and
    ///     Partition in the event data.
    /// </summary>
    event Handlers.AsiEventEventHandler? AsiEvent;

    /// <summary>
    ///     A movement started by <c>MoveLuFromAsiToAsi</c> ended. The engine finds the device by
    ///     <c>ItemGuid</c> (the <see cref="RunAsi.ItemGuid" /> of the command) and the movement by
    ///     <c>StepGuid</c> (the step or group identifier of the command).
    /// </summary>
    event Handlers.AsiMovementIsCompletedEventHandler? AsiMovementIsCompleted;

    /// <summary>
    ///     A device asks for the data of the load unit it holds. The engine answers with
    ///     <see cref="SendLuDataToAsi" />, carrying back the request identifier of the event.
    /// </summary>
    event Handlers.AsiRequestsLuInfoEventHandler? AsiRequestsLuInfo;

    /// <summary>
    ///     The outcome of a <see cref="RequestLuDataFromAsi" />: the device sent the data of its load
    ///     unit. The engine finds the device by <c>ItemGuid</c>.
    /// </summary>
    event Handlers.AsiSendLuDataEventHandler? AsiSendLuData;

    /// <summary>
    ///     A load unit left a device. The engine finds the device by the Asi, Subitem and Partition in
    ///     the event data.
    /// </summary>
    event Handlers.LuUnloadedFromAsiEventHandler? LuUnloadedFromAsi;

    /// <summary>
    ///     A new load unit appeared on a device. The engine finds the device by the Asi, Subitem and
    ///     Partition in the event data.
    /// </summary>
    event Handlers.NewLuOnAsiEventHandler? NewLuOnAsi;

    #endregion Public Events

    #region Public Properties

    /// <summary>
    ///     The identifier of the connection: return the <see cref="ConnectionSettings.Id" /> the
    ///     connection was built with.
    /// </summary>
    /// <remarks>
    ///     Devices point to their connection by this value. A <c>Connected</c> device whose connection
    ///     identifier matches no <see cref="Id" /> gets no connection: it goes OffLine with an error in
    ///     the navigator log, and Run is refused while it lacks it (only the explicit setting
    ///     <c>Wcs:AllowSimulatorForMissingConnections</c>, refused in Production, serves it with the
    ///     loopback driver instead).
    /// </remarks>
    decimal Id { get; }

    /// <summary>
    ///     Whether the engine is using this connection. The engine sets it; the driver only stores it.
    /// </summary>
    /// <remarks>
    ///     <c>true</c> from the engine start that opened the connection to the next engine stop, and
    ///     <c>false</c> otherwise. It must start as <c>false</c>. The engine opens and closes only the
    ///     connections that have it set.
    /// </remarks>
    public bool Managed { get; set; }

    #endregion Public Properties

    #region Public Methods

    /// <summary>
    ///     Closes the connection to the PLC. Called at engine stop and before <see cref="Dispose" />;
    ///     it must not throw on a connection that is not open.
    /// </summary>
    void CloseConnection();

    /// <summary>
    ///     Releases the connection for good. Called once, after <see cref="CloseConnection" />, when
    ///     the engine replaces the connections at a plant load; the object is not used again.
    /// </summary>
    void Dispose();

    /// <summary>
    ///     Moves a single load unit from one position to another.
    /// </summary>
    /// <param name="sourceItem">
    ///     The device that runs the movement: its <see cref="RunAsi.ItemGuid" /> goes back in
    ///     <see cref="AsiMovementIsCompleted" />, and <see cref="RunAsi.TimeOutPeriod" /> is the
    ///     movement timeout configured on the device.
    /// </param>
    /// <param name="actionItemPerformer">The position of the device that performs the action.</param>
    /// <param name="fromAsi">The position the load unit leaves.</param>
    /// <param name="toAsi">The position the load unit reaches.</param>
    /// <param name="stepGuid">
    ///     The step being run: it goes back as <c>StepGuid</c> in
    ///     <see cref="AsiMovementIsCompleted" />, where the engine uses it to find the step.
    /// </param>
    /// <param name="luId">The identifier of the load unit.</param>
    /// <param name="luHandlingMode">The handling mode of the mission (<c>0</c> when not set).</param>
    void MoveLuFromAsiToAsi(RunAsi sourceItem, DataAsi actionItemPerformer, DataAsi fromAsi, DataAsi toAsi,
        Guid stepGuid, decimal luId, int luHandlingMode = 0);

    /// <summary>
    ///     Moves several load units in one command, for a group of devices.
    /// </summary>
    /// <param name="sourceItem">
    ///     The device that runs the movement: its <see cref="RunAsi.ItemGuid" /> goes back in
    ///     <see cref="AsiMovementIsCompleted" />.
    /// </param>
    /// <param name="actionItemPerformer">The position of the device that performs the action.</param>
    /// <param name="groupedMovementId">
    ///     The identifier of the grouped movement: it goes back as <c>StepGuid</c> in
    ///     <see cref="AsiMovementIsCompleted" />.
    /// </param>
    /// <param name="loadUnitsToMove">The load units with their source and destination positions.</param>
    /// <param name="luHandlingMode">The handling mode (<c>0</c> when not set).</param>
    void MoveLuFromAsiToAsi(RunAsi sourceItem, DataAsi actionItemPerformer, Guid groupedMovementId,
        List<MovementLuDataCommand> loadUnitsToMove, int luHandlingMode = 0);

    /// <summary>
    ///     Opens the connection to the PLC. Called at engine start, after the engine has subscribed to
    ///     the events; it may be called again after <see cref="CloseConnection" />.
    /// </summary>
    void OpenConnection();

    /// <summary>
    ///     Asks a device for the data of the load unit it holds. The answer comes back through
    ///     <see cref="AsiSendLuData" />.
    /// </summary>
    /// <param name="item">The device asked, with the timeout of the request.</param>
    void RequestLuDataFromAsi(RunAsi item);

    /// <summary>
    ///     Sends a generic command to a device.
    /// </summary>
    /// <param name="destinationItem">The device the command is for.</param>
    /// <param name="commandCode">The code of the command; its meaning belongs to the driver.</param>
    /// <param name="parameters">The parameters of the command; their meaning belongs to the driver.</param>
    /// <remarks>
    ///     The engine never calls this method. A driver may implement it as a method that does
    ///     nothing, as the loopback driver does.
    /// </remarks>
    void SendCommandToAsi(RunAsi destinationItem, int commandCode, List<string?> parameters);

    /// <summary>
    ///     Answers an <see cref="AsiRequestsLuInfo" /> event with the data of the load unit.
    /// </summary>
    /// <param name="item">The device that asked.</param>
    /// <param name="value">The data of the load unit, with the request identifier of the event.</param>
    void SendLuDataToAsi(RunAsi item, LuInfoDataCommand value);

    /// <summary>
    ///     Shows a text message on a device (for example on its operator panel).
    /// </summary>
    /// <param name="destinationItem">The device the message is for.</param>
    /// <param name="message">The text to show. The engine currently always passes an empty string.</param>
    void ShowMessageToAsi(RunAsi destinationItem, string message);

    #endregion Public Methods
}
