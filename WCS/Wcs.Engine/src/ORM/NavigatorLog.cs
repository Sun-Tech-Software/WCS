using Wcs.Engine.Items;
using Wcs.Engine.Persistence;

namespace Wcs.Engine.ORM;

/// <summary>
/// Il registro del navigatore sullo storico del WCS: una riga per evento, nel file del mese.
/// </summary>
internal static class NavigatorLog
{
    #region Public Methods

    /// <summary>
    /// Mette in coda una riga del registro. Non aspetta il database.
    /// </summary>
    /// <remarks>
    /// Passa dallo scrittore a coda di <see cref="Navigator.HistoryWriter"/>: e' la scrittura piu'
    /// frequente del motore e partiva dal thread del campo. La data e' quella dell'evento, presa
    /// qui. Senza scrittore si scrive direttamente sullo storico, se c'e'; senza storico — motore
    /// non configurato, test — non c'e' nulla su cui scrivere. Prima era una INSERT su
    /// <c>NAVIGATOR_LOG</c> in SQL Server, che non viene piu' scritta: tappa 2 del database del WCS.
    /// </remarks>
    public static void Add(NavigatorLogRecord value)
    {
        var entry = HistoryStore.Snapshot(value, DateTime.Now);

        if (Navigator.HistoryWriter is { } writer && writer.Enqueue(context => Append(context, entry)))
            return;

        var history = Navigator.History;
        if (history == null) return;

        using var direct = history.CreateContext();
        direct.NavigatorLog.Add(entry);
        direct.SaveChanges();
    }

    #endregion Public Methods

    private static Task Append(WcsHistoryDbContext context, Persistence.Entities.NavigatorLogEntry entry)
    {
        context.NavigatorLog.Add(entry);
        return Task.CompletedTask;
    }
}
