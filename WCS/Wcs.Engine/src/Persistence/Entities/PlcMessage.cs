namespace Wcs.Engine.Persistence.Entities;

/// <summary>
/// Un messaggio scambiato con un PLC: quando, con quale connessione, in che verso, con quale
/// intestazione e con quale corpo. Sta nel file mensile del registro dei messaggi dei PLC
/// (<see cref="WcsPlcLogDbContext"/>). Sostituisce <c>PCM_LOG</c>, che era una tabella del
/// database del gestionale.
/// </summary>
/// <remarks>
/// Le colonne sono quelle che la vecchia <c>INSERT INTO [PCM_LOG]</c> scriveva, con i nomi in
/// inglese: <c>DATE</c>→<c>Date</c>, <c>ID_PLC</c>→<c>PlcId</c>, <c>TYPE</c>→<c>Type</c>,
/// <c>HEADER_LEN</c>→<c>HeaderLength</c>, <c>SYSCODE</c>→<c>Syscode</c>, <c>OP_TYPE</c>→<c>OpType</c>,
/// <c>OP_CODE_TYPES</c>→<c>OpCode</c>, <c>OP_PROGRESS</c>→<c>OpProgress</c>,
/// <c>FRAME_LEN</c>→<c>FrameLength</c>, <c>DATA_LEN</c>→<c>DataLength</c>,
/// <c>DATA_CRC32</c>→<c>DataCrc32</c>, <c>CODING_ERROR</c>→<c>CodingError</c>,
/// <c>CODING_ERROR_DESCRIPTION</c>→<c>CodingErrorDescription</c>, <c>ASI</c>→<c>Asi</c>,
/// <c>SUBITEM</c>→<c>Subitem</c>, <c>PARTITION</c>→<c>Partition</c>, <c>MSG_LEN</c>→<c>MessageLength</c>,
/// <c>MSG_ID</c>→<c>MessageId</c>, <c>XML</c>→<c>Xml</c>.
/// <para>
/// I quattro campi che il driver porta come <c>uint</c> (progressivo, lunghezze del frame e dei
/// dati, CRC32) sono <c>long</c> e non <c>int</c>: la vecchia <c>INSERT</c> li castava a
/// <c>int</c>, e un valore oltre <c>int.MaxValue</c> ci finiva dentro negativo. Un intero a 64 bit
/// tiene tutto l'intervallo di <c>uint</c> senza inventare niente, e SQLite lo memorizza comunque
/// come <c>INTEGER</c>.
/// </para>
/// <para>
/// La ritenzione e' doppia: la potatura giornaliera del driver
/// (<c>Wcs.Engine.ORM.PlcMessageLog.ClearOlderThan</c>, 7 giorni come la vecchia
/// <c>DELETE FROM PCM_LOG WHERE DATE &lt; ...</c>) sul file del mese corrente, e la rotazione
/// mensile dei file (<see cref="WcsPlcLogDatabase"/>, due mesi), che cancella per intero i file
/// oltre i mesi da tenere. Fino alla voce <c>D183</c> questa tabella stava nei file dello
/// storico, con la rotazione a dodici mesi di quelli.
/// </para>
/// </remarks>
public sealed class PlcMessage
{
    public long Id { get; set; }
    public DateTime Date { get; set; }

    /// <summary>L'identificativo della connessione PLC (<c>PCM_PLC.ID</c>).</summary>
    public decimal PlcId { get; set; }

    /// <summary>Il verso: 0 ricevuto, 1 inviato. Sono i valori di <c>MessageLog.MessageTypes</c>.</summary>
    public short Type { get; set; }

    public short HeaderLength { get; set; }
    public string? Syscode { get; set; }
    public short OpType { get; set; }
    public short OpCode { get; set; }
    public long OpProgress { get; set; }
    public long FrameLength { get; set; }
    public long DataLength { get; set; }
    public long DataCrc32 { get; set; }
    public bool CodingError { get; set; }
    public string? CodingErrorDescription { get; set; }
    public string? Asi { get; set; }
    public string? Subitem { get; set; }
    public string? Partition { get; set; }
    public int MessageLength { get; set; }
    public int MessageId { get; set; }

    /// <summary>Il messaggio serializzato in XML, come lo scriveva la colonna <c>XML</c>.</summary>
    public string? Xml { get; set; }
}
