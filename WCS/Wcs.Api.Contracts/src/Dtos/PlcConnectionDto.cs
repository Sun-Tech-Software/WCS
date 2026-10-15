namespace Wcs.Api.Contracts.Dtos;

public sealed record PlcConnectionDto(long Id, string? Description, string? PlcAddress, int? PcmPort,
    string? LocalAddress, int? LocalPort);
public sealed record PlcConnectionStatus(long Id, int OpProgress, short StatusSenderChannel,
    short StatusReceiverChannel, short OutboundChannelStatus, short InboundChannelStatus);

/// <summary>
/// Una connessione che una sostituzione toglierebbe e i dispositivi dell'impianto che la usano ancora:
/// il campo <see cref="PlcConnectionProblems.InUseExtension"/> del <c>409</c> di
/// <c>PUT /plc/connections</c> (compito 4c della console). Chi legge il rifiuto sa quali righe segnare
/// senza interpretare le parole del dettaglio.
/// </summary>
public sealed record PlcConnectionInUseDto(long ConnectionId, IReadOnlyList<PlcConnectionUserDto> Devices);

/// <summary>Un dispositivo che usa una connessione PLC: il suo Id e il suo nome, se ne ha uno.</summary>
public sealed record PlcConnectionUserDto(Guid ItemId, string? Name);

/// <summary>I nomi dei campi che i rifiuti delle rotte PLC aggiungono al <c>ProblemDetails</c>.</summary>
public static class PlcConnectionProblems
{
    /// <summary>L'elenco di <see cref="PlcConnectionInUseDto"/> del rifiuto «connessione ancora usata».</summary>
    public const string InUseExtension = "plcConnectionsInUse";
}
