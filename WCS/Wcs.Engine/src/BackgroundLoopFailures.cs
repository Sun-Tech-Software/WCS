using Log.Interface;

namespace Wcs.Engine;

/// <summary>
/// Il conto dei giri falliti di <b>un</b> ciclo di sfondo, e la regola con cui finiscono nel
/// registro: la prima occorrenza passa, le ripetizioni dello stesso guasto si contano e diventano
/// un riepilogo ogni tanto, un guasto diverso passa subito, e quando il giro torna a riuscire lo
/// dice.
/// </summary>
/// <remarks>
/// <b>Perche' esiste.</b> <c>Navigator.BackgroundLoop</c> scriveva una riga per ogni giro fallito, e
/// il ciclo <c>BuildRoutePath</c> gira ogni <b>10 ms</b>. Dopo la voce <c>D94</c> le righe di errore
/// passano sempre e finiscono, tramite l'<c>ILogger</c> dell'host, anche nel registro eventi di
/// Windows quando il motore gira come servizio: un gestionale che non risponde - il guasto piu'
/// ordinario che ci sia - avrebbe scritto un centinaio di voci al secondo, ognuna con il proprio
/// stack trace. Il difetto di prima era peggiore (il ciclo moriva alla prima riga e non ripartiva),
/// ma il rimedio ha aperto questo.
/// <para>
/// <b>Il ritmo del ciclo non cambia.</b> Allungare l'attesa dopo un giro fallito sarebbe stato
/// l'altro rimedio, e costava di piu': la ricerca dei percorsi resterebbe ferma fino a mezzo minuto
/// dopo che il gestionale e' tornato. Qui si toglie il volume dal registro, non i giri al ciclo.
/// </para>
/// <para>
/// <b>Il raggruppamento e' per messaggio dell'eccezione</b>, che e' quanto di piu' stabile ci sia
/// senza guardare il tipo o lo stack: un guasto il cui messaggio cambia a ogni giro (perche' ci
/// porta dentro un identificativo, per dire) non si raggruppa, e scrive come prima. E' una perdita
/// accettabile: i guasti che inondano sono quelli che si ripetono uguali.
/// </para>
/// <para>
/// Non e' condiviso fra cicli e non ha bisogno di sincronizzazione: ne esiste uno per ogni
/// <c>Task</c> avviato da <c>BackgroundLoop</c>, e lo tocca solo quel <c>Task</c>.
/// </para>
/// </remarks>
/// <param name="name">Il nome del ciclo, come compare nel registro</param>
/// <param name="summaryEvery">Ogni quanto, al piu', esce un riepilogo mentre il guasto si ripete</param>
internal sealed class BackgroundLoopFailures(string name, TimeSpan summaryEvery)
{
    private string? _lastMessage;
    private int _repeated;
    private DateTime _lastWritten;

    /// <summary>Un giro e' fallito.</summary>
    public void Report(Exception ex)
    {
        if (ex.Message != _lastMessage)
        {
            // Guasto diverso: si chiude il conto del precedente e questo passa subito.
            WriteSummaryIfAny();

            _lastMessage = ex.Message;
            _lastWritten = DateTime.UtcNow;

            Navigator.WriteLog($"{name} background task failed: {ex.Message}\r\nStackTrace: {ex.StackTrace}",
                LogEntryType.Error);
            return;
        }

        _repeated++;

        if (DateTime.UtcNow - _lastWritten < summaryEvery) return;

        WriteSummaryIfAny();
        _lastWritten = DateTime.UtcNow;
    }

    /// <summary>Un giro e' riuscito: se prima falliva, lo si dice una volta sola.</summary>
    public void TurnWentThrough()
    {
        if (_lastMessage == null) return;

        WriteSummaryIfAny();

        Navigator.WriteLog($"{name} background task: il giro e' tornato a riuscire.", LogEntryType.Warning);

        _lastMessage = null;
    }

    /// <summary>Il riepilogo dei giri contati e non scritti, se ce ne sono.</summary>
    public void WriteSummaryIfAny()
    {
        if (_repeated == 0) return;

        Navigator.WriteLog(
            $"{name} background task failed: altri {_repeated} giri con lo stesso guasto, ultimo: {_lastMessage}",
            LogEntryType.Error);

        _repeated = 0;
    }
}
