using Microsoft.EntityFrameworkCore;
using Wcs.Connection.Interface;
using Wcs.Engine.Persistence;

namespace Wcs.Engine.ORM;

/// <summary>
/// Il registro dei messaggi scambiati con i PLC, sul database dei PLC del WCS: una riga per
/// messaggio, nel file del mese.
/// </summary>
/// <remarks>
/// Prende il posto della <c>INSERT INTO [PCM_LOG]</c> sul database del gestionale, che e' uscita
/// con il compito 15. La destinazione e' un database del WCS, come dice la specifica delle
/// letture dirette («i ganci dello strato PLC vanno verso il database del WCS»): il primo
/// tentativo aveva mandato queste righe sulla diagnostica del motore, dove con
/// <c>ActivateDebugLog</c> spento — che e' la configurazione di serie — non venivano scritte da
/// nessuna parte. Fino alla voce <c>D183</c> il database era quello dello storico, condiviso con
/// il registro del navigatore; adesso e' <see cref="WcsPlcLogDatabase"/>, un file mensile suo.
/// </remarks>
internal static class PlcMessageLog
{
    #region Public Methods

    /// <summary>
    /// Mette in coda la riga di un messaggio. Non aspetta il database.
    /// </summary>
    /// <remarks>
    /// Passa dallo scrittore a coda di <see cref="Navigator.PlcLogWriter"/>, come il registro del
    /// navigatore passa dal suo: e' una scrittura che parte dal thread del campo, una per
    /// messaggio per PLC, e non deve fermarlo. La coda e' una sola per tutti i PLC. Senza
    /// scrittore si scrive direttamente sul database dei PLC, se c'e'; senza database non c'e'
    /// nulla su cui scrivere e lo si dice sollevando, perche' chi chiama
    /// (<c>MessageLog.PersistAsync</c>) lo raccoglie e lascia una riga sul registro del
    /// navigatore: un messaggio perso in silenzio e' esattamente quello che questa correzione
    /// toglie.
    /// </remarks>
    public static void Add(MessageLogRecord record)
    {
        var row = PlcLogStore.Snapshot(record);

        // La coda e' quella dei soli messaggi dei PLC: a coda piena scarta i piu' vecchi, e i piu'
        // vecchi sono telegrammi. Le righe del navigatore stanno in un'altra coda e non cadono per
        // colpa di un'ondata di messaggi. Voce D183.
        if (Navigator.PlcLogWriter is { } writer && writer.Enqueue(context => Append(context, row)))
            return;

        var plcLog = Navigator.PlcLog
                     ?? throw new Api.EngineStoreUnavailableException(
                         "Il registro dei messaggi dei PLC non e' aperto: la riga non e' stata scritta.");

        using var direct = plcLog.CreateContext();
        direct.PlcMessages.Add(row);
        direct.SaveChanges();
    }

    /// <summary>
    /// La potatura del registro dei messaggi: cancella dal file del mese corrente le righe piu'
    /// vecchie dei giorni indicati.
    /// </summary>
    /// <remarks>
    /// Prende il posto della <c>DELETE FROM PCM_LOG WHERE DATE &lt; DATEADD(DAY, -@DAYS, GETDATE())</c>
    /// che il driver faceva su SQL Server, e ne conserva la ritenzione: chi chiama passa gli stessi
    /// 7 giorni di prima. Qui si tocca solo il file del mese corrente, che e' l'unico scrivibile;
    /// i mesi passati se ne vanno con la rotazione dei file
    /// (<see cref="WcsPlcLogDatabase.DefaultMonthsToKeep"/>, due mesi, che e' il minimo che non
    /// contraddice la finestra di sette giorni a cavallo di un mese).
    /// <para>
    /// E' un <c>ExecuteDelete</c>, cioe' una sola istruzione sul database: nessuna transazione
    /// nuova e nessuna entita' caricata in memoria per essere cancellata una a una.
    /// </para>
    /// </remarks>
    public static async Task ClearOlderThan(int daysToClean)
    {
        if (daysToClean <= 0) return;

        // Senza database non c'e' niente da potare. Non e' il caso di Add, che solleva: li' si
        // perderebbe una riga, qui non c'e' nessuna riga da cui cominciare.
        if (Navigator.PlcLog is not { } plcLog) return;

        var threshold = DateTime.Now.AddDays(-daysToClean);

        await using var context = plcLog.CreateContext();
        await context.PlcMessages.Where(message => message.Date < threshold).ExecuteDeleteAsync();
    }

    #endregion Public Methods

    private static Task Append(WcsPlcLogDbContext context, Persistence.Entities.PlcMessage row)
    {
        context.PlcMessages.Add(row);
        return Task.CompletedTask;
    }
}
