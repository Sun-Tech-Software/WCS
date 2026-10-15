namespace Wcs.Api.Contracts.Dtos;

/// <summary>
/// Un messaggio scambiato con un PLC, come lo restituisce lo storico del WCS. Prende il posto
/// della riga di <c>PCM_LOG</c>, che era una tabella del database del gestionale.
/// </summary>
/// <param name="Type">Il verso: 0 ricevuto, 1 inviato.</param>
public sealed record PlcMessageDto(long Id, DateTime Date, decimal PlcId, short Type, short HeaderLength,
    string? Syscode, short OpType, short OpCode, long OpProgress, long FrameLength, long DataLength,
    long DataCrc32, bool CodingError, string? CodingErrorDescription, string? Asi, string? Subitem,
    string? Partition, int MessageLength, int MessageId, string? Xml);
