namespace Wcs.Connection.Interface;

/// <summary>
/// One row of the driver message log (one message exchanged with the field), as the driver hands
/// it over to be persisted (<see cref="IConnectionServices.WriteMessageAsync"/>). The engine stores
/// it in the <c>PlcMessages</c> table of the monthly <c>wcs-plc-YYYY-MM.db</c>.
/// </summary>
/// <remarks>
/// The header fields are those of the historical message log; a driver fills the ones that mean
/// something for its protocol and leaves the others at zero or empty. Enumerations travel as bytes,
/// so the engine does not depend on the types of any protocol.
/// <para>
/// The engine stores every field unchanged and returns the rows from
/// <c>GET /api/v1/history/plc-messages</c>; it reads none of them. The header fields below are
/// named after the frame of the historical protocol: for any other protocol their meaning belongs
/// to the driver, and a field with no counterpart stays at 0 or empty.
/// </para>
/// </remarks>
/// <param name="Date">When the message was sent or received.</param>
/// <param name="IdPlc">The connection identifier (<see cref="ConnectionSettings.Id"/>).</param>
/// <param name="Type">0 = received, 1 = sent.</param>
/// <param name="HeaderLen">The length of the message header, in bytes; meaning belongs to the driver.</param>
/// <param name="Syscode">The system code carried by the header; meaning belongs to the driver.</param>
/// <param name="OpType">The operation type of the message; meaning belongs to the driver.</param>
/// <param name="OpCode">The operation code of the message; meaning belongs to the driver.</param>
/// <param name="OpProgress">The message counter of the header (see <see cref="ConnectionStatusSnapshot.OpProgress"/>).</param>
/// <param name="FrameLen">The length of the whole frame, in bytes.</param>
/// <param name="DataLen">The length of the payload, in bytes.</param>
/// <param name="DataCrc32">The checksum of the payload, as the frame carries it.</param>
/// <param name="CodingError">Whether the driver could not decode the message.</param>
/// <param name="CodingErrorDescription">Why the message could not be decoded; empty when it could.</param>
/// <param name="Asi">The field address of the device the message is about; empty when the message is
/// about the connection as a whole.</param>
/// <param name="Subitem">The sub-address within <paramref name="Asi"/>; meaning belongs to the driver.</param>
/// <param name="Partition">The partition of <paramref name="Asi"/>; meaning belongs to the driver.</param>
/// <param name="MsgLen">The length of the command part of the message, in bytes; meaning belongs to the driver.</param>
/// <param name="MsgId">The kind of message, as a code of the protocol; meaning belongs to the
/// driver. 0 when the message has none.</param>
/// <param name="Xml">The whole message, serialized as the driver sees fit (XML for the historical driver).</param>
public sealed record MessageLogRecord(
    DateTime Date,
    decimal IdPlc,
    byte Type,
    byte HeaderLen,
    string Syscode,
    byte OpType,
    byte OpCode,
    uint OpProgress,
    uint FrameLen,
    uint DataLen,
    uint DataCrc32,
    bool CodingError,
    string CodingErrorDescription,
    string Asi,
    string Subitem,
    string Partition,
    int MsgLen,
    int MsgId,
    string Xml);
