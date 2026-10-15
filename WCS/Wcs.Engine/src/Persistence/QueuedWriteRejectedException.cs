namespace Wcs.Engine.Persistence;

/// <summary>
/// La coda delle scritture non ha preso in carico una scrittura: chi l'aspettava deve sapere che
/// non e' avvenuta.
/// </summary>
/// <remarks>
/// <para>
/// Le due cause sono <b>legittime e transitorie</b>, non difetti di programmazione: lo scrittore
/// che si sta fermando mentre un giro e' in corso (arresto o riavvio del motore), e la coda piena.
/// Chi le distingue le tratta diversamente da un guasto della scrittura vera, che invece arriva
/// dall'esecutore: la' la riga e' stata tentata, qui non e' nemmeno entrata in coda.
/// </para>
/// <para>
/// Deriva da <see cref="InvalidOperationException"/>, come le eccezioni proprie del motore in
/// <c>Api/EngineExceptions.cs</c>: al posto di quella generica che i due punti di rifiuto
/// sollevavano prima, e quindi ogni chiamante che gia' la catturava continua a catturarla.
/// </para>
/// </remarks>
public sealed class QueuedWriteRejectedException(string message) : InvalidOperationException(message)
{
    /// <summary>Lo scrittore si sta fermando o e' fermo: non accetta piu' nulla.</summary>
    internal static QueuedWriteRejectedException WriterStopped() =>
        new("La scrittura non e' stata accettata: lo scrittore e' fermo.");

    /// <summary>
    /// La coda e' piena e non fa cadere nulla. Non e' una contraddizione con l'attesa che quelle
    /// opzioni promettono: l'attesa e' di <c>WriteAsync</c>, mentre <c>TryWrite</c> non aspetta mai
    /// e a coda piena risponde <c>false</c> anche in quella modalita'.
    /// </summary>
    internal static QueuedWriteRejectedException QueueFull() =>
        new("La scrittura non e' stata accettata: la coda e' piena.");

    /// <summary>La coda era piena e la scrittura, essendo la piu' vecchia, e' stata fatta cadere.</summary>
    internal static QueuedWriteRejectedException Dropped() =>
        new("La scrittura e' caduta: la coda era piena.");
}
