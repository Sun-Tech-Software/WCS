namespace Wcs.Connection.Interface;

/// <summary>
/// What the engine offers to a field driver: the places where the driver persists what it needs to
/// keep across restarts and what it reports.
/// </summary>
/// <remarks>
/// The engine implements it on its own databases (<c>wcs.db</c> for command identifiers and
/// connection status, the monthly <c>wcs-plc-YYYY-MM.db</c> for the message log). A driver receives
/// it through <see cref="IConnectionFactory.Attach"/>.
/// </remarks>
public interface IConnectionServices
{
    /// <summary>
    /// Returns the next command identifier of a connection. Identifiers survive a restart: the
    /// engine reserves them in blocks in its database, so after a restart at most one block per
    /// connection is skipped.
    /// </summary>
    /// <param name="connectionId">The connection identifier (<see cref="ConnectionSettings.Id"/>).</param>
    /// <exception cref="InvalidOperationException">The engine database is not open.</exception>
    int ReserveCommandId(decimal connectionId);

    /// <summary>
    /// Persists the status of a connection (read back by <c>GET /api/v1/plc/connections/{id}/status</c>).
    /// </summary>
    Task WriteStatusAsync(ConnectionStatusSnapshot status);

    /// <summary>
    /// Writes one line in the navigator log, the log operators read.
    /// </summary>
    Task WriteLogAsync(DriverLogRecord record);

    /// <summary>
    /// Persists one message exchanged with the field in the driver message log.
    /// </summary>
    Task WriteMessageAsync(MessageLogRecord record);

    /// <summary>
    /// Deletes from the driver message log the rows older than the given number of days.
    /// </summary>
    Task ClearMessagesOlderThanAsync(int days);
}

/// <summary>
/// The settings of one driver connection: one row of the driver settings
/// (<c>PUT /api/v1/plc/connections</c>). The column names are the historical ones; their meaning
/// belongs to the driver.
/// </summary>
/// <param name="Id">The connection identifier; devices point to it with <c>DeviceConnectionId</c>.</param>
/// <param name="RemoteAddress">The address of the field device (<c>PlcAddress</c>).</param>
/// <param name="RemotePort">The port of the field device (<c>PcmPort</c>); 0 when not set.</param>
/// <param name="LocalAddress">The local address to bind (<c>LocalAddress</c>).</param>
/// <param name="LocalPort">The local port to bind (<c>LocalPort</c>); 0 when not set.</param>
/// <param name="OpProgress">The last message counter the driver persisted (<c>OpProgress</c>).</param>
public sealed record ConnectionSettings(
    int Id,
    string? RemoteAddress,
    int RemotePort,
    string? LocalAddress,
    int LocalPort,
    uint OpProgress);

/// <summary>
/// The status of a connection when it changes, as the driver hands it over to be persisted.
/// </summary>
/// <remarks>
/// The engine does not interpret these values: it stores them in the row of the connection in
/// <c>wcs.db</c> and returns them unchanged from <c>GET /api/v1/plc/connections/{id}/status</c>
/// (<c>PlcConnectionStatus</c>). The four channel bytes are named after the historical driver,
/// which has two channels per connection; what each value means belongs to the driver and should
/// be written in its own documentation. A driver with fewer channels leaves the others at 0.
/// </remarks>
/// <param name="Id">The connection identifier (<see cref="ConnectionSettings.Id"/>).</param>
/// <param name="OpProgress">The message counter to resume from after a restart: it comes back as
/// <see cref="ConnectionSettings.OpProgress"/>. Stored as a signed 32-bit number.</param>
/// <param name="StatusSenderChannel">The status of the sending side of the connection; meaning
/// belongs to the driver.</param>
/// <param name="StatusReceiverChannel">The status of the receiving side of the connection; meaning
/// belongs to the driver.</param>
/// <param name="OutboundChannelStatus">The status of the outbound channel (towards the field);
/// meaning belongs to the driver.</param>
/// <param name="InboundChannelStatus">The status of the inbound channel (from the field); meaning
/// belongs to the driver.</param>
public sealed record ConnectionStatusSnapshot(
    decimal Id,
    uint OpProgress,
    byte StatusSenderChannel,
    byte StatusReceiverChannel,
    byte OutboundChannelStatus,
    byte InboundChannelStatus);

/// <summary>
/// One line of the driver for the navigator log. The address is the device the line is about
/// (empty when it is about the connection as a whole); <see cref="Status"/> is a
/// <c>Log.Interface.LogEntryType</c> value.
/// </summary>
/// <remarks>
/// The engine stores the line as it is, with the time it receives it, and returns it from
/// <c>GET /api/v1/log</c>: the fields below become <c>ItemId</c>, <c>Asi</c>, <c>Subitem</c> and
/// <c>Partition</c> of the entry. They are free text for the engine, which does not look them up.
/// </remarks>
/// <param name="IdItem">The engine item the line is about (a device), or <see cref="Guid.Empty"/>
/// when the driver does not know it (the entry then has no <c>ItemId</c>).</param>
/// <param name="Asi">The field address of the device the line is about, as the driver writes it;
/// a driver may use a fixed placeholder for lines about the whole connection.</param>
/// <param name="Subitem">The sub-address within <paramref name="Asi"/>; meaning belongs to the driver.</param>
/// <param name="Partition">The partition of <paramref name="Asi"/>; meaning belongs to the driver.</param>
/// <param name="Status">The severity, a <c>Log.Interface.LogEntryType</c> value as a byte.</param>
/// <param name="Description">The text operators read.</param>
public sealed record DriverLogRecord(
    Guid IdItem,
    string Asi,
    string Subitem,
    string Partition,
    byte Status,
    string Description);
