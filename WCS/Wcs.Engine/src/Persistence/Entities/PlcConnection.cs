namespace Wcs.Engine.Persistence.Entities;

/// <summary>
/// Una connessione al PLC: indirizzi e porte, e lo stato dei canali che il driver aggiorna.
/// Sostituisce <c>PCM_PLC</c> su SQL Server.
/// </summary>
/// <remarks>
/// La chiave e' quella che i dispositivi del diagramma ricordano come <c>DeviceConnectionId</c>:
/// alla migrazione dei dati va conservata. Corrispondenza con le vecchie colonne:
/// <c>ID_PCM_PLC</c>→<c>Id</c>, <c>PLC_ADDRESS</c>→<c>PlcAddress</c>, <c>PCM_PORT</c>→<c>PcmPort</c>,
/// <c>LOCAL_ADDRESS</c>→<c>LocalAddress</c>, <c>LOCAL_PORT</c>→<c>LocalPort</c>, <c>OP_PROGRESS</c>→<c>OpProgress</c>,
/// e i quattro stati dei canali con lo stesso nome.
/// </remarks>
public sealed class PlcConnection
{
    public long Id { get; set; }
    public string? Description { get; set; }
    public string? PlcAddress { get; set; }
    public int? PcmPort { get; set; }
    public string? LocalAddress { get; set; }
    public int? LocalPort { get; set; }
    public int OpProgress { get; set; }
    public int LastCommandId { get; set; }
    public short StatusSenderChannel { get; set; }
    public short StatusReceiverChannel { get; set; }
    public short OutboundChannelStatus { get; set; }
    public short InboundChannelStatus { get; set; }
}
