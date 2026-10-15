namespace Wcs.Engine.Test;

/// <summary>
/// Fa partire N azioni <b>nello stesso istante</b> e restituisce i loro esiti.
/// </summary>
/// <remarks>
/// Senza la barriera i thread partono scaglionati e la finestra che si vuole colpire non si apre
/// quasi mai: il test passerebbe anche sul codice rotto. La barriera non garantisce la
/// sovrapposizione — nessun test di concorrenza puo' garantirla — ma la rende il caso normale
/// invece che l'eccezione, e ripetendo il tentativo la si incontra.
/// </remarks>
internal static class Contesa
{
    /// <summary>
    /// Esegue <paramref name="azione"/> da <paramref name="quanti"/> thread che partono insieme.
    /// </summary>
    /// <param name="quanti">Quanti thread far partire.</param>
    /// <param name="azione">L'azione, che riceve l'indice del proprio thread.</param>
    /// <returns>Gli esiti, nell'ordine degli indici.</returns>
    public static T[] Insieme<T>(int quanti, Func<int, T> azione)
    {
        var esiti = new T[quanti];
        var barriera = new Barrier(quanti);
        var thread = new Thread[quanti];

        for (var i = 0; i < quanti; i++)
        {
            var indice = i;
            thread[i] = new Thread(() =>
            {
                barriera.SignalAndWait();
                esiti[indice] = azione(indice);
            });
            thread[i].Start();
        }

        foreach (var t in thread)
            t.Join();

        return esiti;
    }

    /// <summary>
    /// Esegue <paramref name="azione"/> da <paramref name="quanti"/> thread che partono insieme,
    /// raccogliendo l'eccezione di ciascuno se ne solleva una.
    /// </summary>
    /// <returns>Le eccezioni sollevate; vuoto se nessuna.</returns>
    public static List<Exception> InsiemeRaccogliendoErrori(int quanti, Action<int> azione)
    {
        var errori = new List<Exception>();

        var esiti = Insieme(quanti, indice =>
        {
            try
            {
                azione(indice);
                return (Exception?)null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        });

        foreach (var e in esiti)
            if (e != null)
                errori.Add(e);

        return errori;
    }
}
