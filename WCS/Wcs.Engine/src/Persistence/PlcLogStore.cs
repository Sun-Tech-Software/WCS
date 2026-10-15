using Wcs.Engine.Persistence.Entities;
// Solo il record del registro dei messaggi: lo spazio dei nomi intero renderebbe ambiguo
// NavigatorLogRecord, che esiste con lo stesso nome nel driver e fra gli oggetti del motore.
using MessageLogRecord = Wcs.Connection.Interface.MessageLogRecord;

namespace Wcs.Engine.Persistence;

/// <summary>
/// Una scrittura sul registro dei messaggi dei PLC: un'operazione sul contesto del mese corrente.
/// </summary>
public delegate Task PlcLogWrite(WcsPlcLogDbContext context);

/// <summary>
/// La mappa fra i record in memoria e le righe del registro dei messaggi dei PLC.
/// </summary>
internal static class PlcLogStore
{
    /// <summary>
    /// La riga di un messaggio scambiato con un PLC, nella forma in cui il driver la consegna.
    /// I quattro campi <c>uint</c> passano a <c>long</c> senza perdere niente; la data e' quella
    /// che il driver ha messo sul messaggio, non quella della scrittura.
    /// </summary>
    public static PlcMessage Snapshot(MessageLogRecord record) => new()
    {
        Date = record.Date,
        PlcId = record.IdPlc,
        Type = record.Type,
        HeaderLength = record.HeaderLen,
        Syscode = NullIfEmpty(record.Syscode),
        OpType = record.OpType,
        OpCode = record.OpCode,
        OpProgress = record.OpProgress,
        FrameLength = record.FrameLen,
        DataLength = record.DataLen,
        DataCrc32 = record.DataCrc32,
        CodingError = record.CodingError,
        CodingErrorDescription = NullIfEmpty(record.CodingErrorDescription),
        Asi = NullIfEmpty(record.Asi),
        Subitem = NullIfEmpty(record.Subitem),
        Partition = NullIfEmpty(record.Partition),
        MessageLength = record.MsgLen,
        MessageId = record.MsgId,
        Xml = NullIfEmpty(record.Xml)
    };

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}

/// <summary>
/// Esegue un lotto di scritture sul registro dei messaggi dei PLC: un contesto sul file del mese
/// di adesso, tutte le operazioni, un solo <c>SaveChanges</c>. Un lotto a cavallo del cambio di
/// mese finisce per intero nel mese nuovo: e' l'imprecisione di un intervallo di raccolta, pochi
/// millisecondi.
/// </summary>
internal sealed class WcsPlcLogBatchExecutor(WcsPlcLogDatabase plcLog)
{
    public async Task ExecuteAsync(IReadOnlyList<PlcLogWrite> batch, CancellationToken cancellationToken)
    {
        await using var context = plcLog.CreateContext();

        foreach (var write in batch)
            await write(context).ConfigureAwait(false);

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
