using System.Collections.Concurrent;

namespace Wcs.Engine;

/// <summary>
/// Il freno dei ritentativi: per ogni chiave, quanti fallimenti consecutivi conta e da quando si
/// puo' riprovare. L'attesa cresce di un passo per fallimento e si ferma a un tetto.
/// </summary>
/// <param name="step">Di quanto cresce l'attesa a ogni fallimento.</param>
/// <param name="cap">L'attesa massima: oltre, il freno non stringe piu'.</param>
/// <param name="time">L'orologio, per i collaudi. Non passato, e' quello di sistema.</param>
/// <remarks>
/// <para>
/// E' il meccanismo della voce B70, nato dentro <c>Navigator.BuildRoutePath</c> come funzione locale
/// piu' due dizionari statici: una missione irrisolvibile faceva girare una ricerca di percorso
/// completa a ogni giro da 10 ms. Estratto qui perche' serve anche a
/// <c>AutomationDeviceStorageManager</c>, dove una ricerca di cella che non trova niente si ripeteva
/// a ogni giro da 2 secondi, con tre chiamate al confine e una riprenotazione ciascuno: due copie
/// della stessa idea sarebbero diventate due comportamenti.
/// </para>
/// <para>
/// Non decide nulla da solo: chi lo usa chiede <see cref="IsWaiting"/> prima di rifare il lavoro,
/// dichiara il fallimento con <see cref="RegisterFailure"/> - che gli dice a quanti fallimenti di
/// seguito e' arrivato, cosi' puo' scrivere nel registro di rado invece che a ogni giro - e
/// <see cref="Forget"/> quando riesce. Chi non dimentica fa crescere i dizionari per tutta la vita
/// del processo.
/// </para>
/// <para>
/// L'attesa e' la stessa per tutti i tentativi oltre il tetto: non e' un numero massimo di
/// tentativi, e non c'e' una rinuncia definitiva. Le condizioni che questo freno rallenta -
/// magazzino pieno, missione non ancora costruibile - sono ordinarie e si risolvono da sole.
/// </para>
/// </remarks>
internal sealed class RetryBackoff(TimeSpan step, TimeSpan cap, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    private readonly ConcurrentDictionary<Guid, int> _attempts = new();

    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _nextAttempt = new();

    /// <summary>
    /// Non e' ancora il momento di riprovare questa chiave. Falso anche per una chiave mai vista:
    /// il primo tentativo non aspetta.
    /// </summary>
    public bool IsWaiting(Guid key) =>
        _nextAttempt.TryGetValue(key, out var next) && next > _time.GetUtcNow();

    /// <summary>
    /// Registra un fallimento e restituisce quanti ne conta di seguito per questa chiave: il primo
    /// e' 1.
    /// </summary>
    public int RegisterFailure(Guid key)
    {
        var attempts = _attempts.AddOrUpdate(key, 1, (_, n) => n + 1);

        // Long esplicito: a tentativi molto alti (una missione bloccata per giorni) il prodotto in
        // int potrebbe avvolgersi; il tetto arriva comunque molto prima.
        var wait = Math.Min((long)attempts * (long)step.TotalMilliseconds, (long)cap.TotalMilliseconds);

        _nextAttempt[key] = _time.GetUtcNow() + TimeSpan.FromMilliseconds(wait);

        return attempts;
    }

    /// <summary>
    /// Quanti fallimenti di seguito conta per questa chiave, senza registrarne uno: 0 per una chiave
    /// mai fallita, o dimenticata. Per chi scrive nel registro a soglia prima che il fallimento del
    /// giro sia contato.
    /// </summary>
    public int Failures(Guid key) => _attempts.GetValueOrDefault(key);

    /// <summary>
    /// Un'attesa fissa di un passo, senza registrare un fallimento: per chi aspetta che finisca
    /// qualcos'altro e non sta fallendo, come una missione con uno step in volo. Il conteggio resta
    /// quello che era, e l'attesa non cresce.
    /// </summary>
    public void WaitOneStep(Guid key) => _nextAttempt[key] = _time.GetUtcNow() + step;

    /// <summary>
    /// L'istante calcolato dall'ultimo <see cref="RegisterFailure"/> o <see cref="WaitOneStep"/>. Per
    /// una chiave mai fallita risponde l'istante corrente, così il chiamante può persistere un retry
    /// immediato.
    /// </summary>
    public DateTimeOffset NextAttemptAt(Guid key) =>
        _nextAttempt.TryGetValue(key, out var next) ? next : _time.GetUtcNow();

    /// <summary>
    /// La chiave non serve piu': e' riuscita, o e' uscita di scena. Toglie sia il conteggio sia
    /// l'istante, cosi' un fallimento successivo riparte dal passo piu' corto.
    /// </summary>
    public void Forget(Guid key)
    {
        _attempts.TryRemove(key, out _);
        _nextAttempt.TryRemove(key, out _);
    }

    /// <summary>
    /// Dimentica tutto: serve a chi rifa' il proprio stato da zero, come la ricarica dell'impianto.
    /// </summary>
    public void Clear()
    {
        _attempts.Clear();
        _nextAttempt.Clear();
    }
}
