using Wcs.Engine;
using Xunit;

namespace Wcs.Engine.Test;

/// <summary>
/// Il freno dei ritentativi: l'attesa cresce di un passo per tentativo, si ferma a un tetto, e si
/// dimentica quando la cosa riesce.
/// <para>
/// E' il meccanismo della voce B70, che <c>Navigator.BuildRoutePath</c> aveva in casa come funzione
/// locale e due dizionari statici: qui diventa un oggetto perche' lo usa anche
/// <c>AutomationDeviceStorageManager</c>, e il comportamento deve restare uno solo.
/// </para>
/// <para>
/// L'orologio e' finto: un freno provato con attese vere sarebbe lento e, sotto carico, rosso su
/// codice sano.
/// </para>
/// </summary>
public sealed class RetryBackoffTest
{
    private static readonly Guid AKey = Guid.NewGuid();

    [Fact]
    public void NothingIsWaitingBeforeTheFirstFailure()
    {
        var backoff = new RetryBackoff(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(5), new ManualClock());

        Assert.False(backoff.IsWaiting(AKey));
    }

    /// <summary>
    /// Il conteggio parte da uno e sale di uno per fallimento: e' il numero su cui il chiamante
    /// decide ogni quanto scrivere nel registro.
    /// </summary>
    [Fact]
    public void FailuresAreCountedFromOne()
    {
        var backoff = new RetryBackoff(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(5), new ManualClock());

        Assert.Equal(1, backoff.RegisterFailure(AKey));
        Assert.Equal(2, backoff.RegisterFailure(AKey));
        Assert.Equal(3, backoff.RegisterFailure(AKey));
    }

    /// <summary>
    /// L'attesa e' <c>passo * tentativi</c>: dopo il secondo fallimento sono due passi, e a un passo
    /// e mezzo si aspetta ancora.
    /// </summary>
    [Fact]
    public void TheWaitGrowsWithEveryFailure()
    {
        var clock = new ManualClock();
        var backoff = new RetryBackoff(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(5), clock);

        backoff.RegisterFailure(AKey);
        clock.Advance(TimeSpan.FromMilliseconds(200));
        Assert.False(backoff.IsWaiting(AKey));

        backoff.RegisterFailure(AKey);
        backoff.RegisterFailure(AKey);
        clock.Advance(TimeSpan.FromMilliseconds(300));

        // Tre fallimenti: l'attesa e' 600 ms e ne sono passati 300.
        Assert.True(backoff.IsWaiting(AKey));

        clock.Advance(TimeSpan.FromMilliseconds(300));
        Assert.False(backoff.IsWaiting(AKey));
    }

    /// <summary>
    /// Il tetto ferma la crescita: senza, una cosa bloccata per giorni finirebbe per non essere piu'
    /// ritentata affatto.
    /// </summary>
    [Fact]
    public void TheWaitStopsAtTheCap()
    {
        var clock = new ManualClock();
        var backoff = new RetryBackoff(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(5), clock);

        for (var i = 0; i < 1000; i++) backoff.RegisterFailure(AKey);

        clock.Advance(TimeSpan.FromSeconds(5));

        Assert.False(backoff.IsWaiting(AKey));
    }

    /// <summary>
    /// Dimenticare azzera il conteggio, non solo l'attesa: al fallimento dopo si riparte dal passo
    /// piu' corto, che e' cio' che rende il freno reversibile.
    /// </summary>
    [Fact]
    public void ForgettingResetsBothTheWaitAndTheCount()
    {
        var clock = new ManualClock();
        var backoff = new RetryBackoff(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(5), clock);

        backoff.RegisterFailure(AKey);
        backoff.RegisterFailure(AKey);
        Assert.True(backoff.IsWaiting(AKey));

        backoff.Forget(AKey);

        Assert.False(backoff.IsWaiting(AKey));
        Assert.Equal(1, backoff.RegisterFailure(AKey));
    }

    /// <summary>
    /// Le chiavi non si disturbano fra loro: e' il caso di due missioni, una bloccata e una no.
    /// </summary>
    [Fact]
    public void KeysAreIndependent()
    {
        var clock = new ManualClock();
        var backoff = new RetryBackoff(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(5), clock);
        var otherKey = Guid.NewGuid();

        backoff.RegisterFailure(AKey);

        Assert.True(backoff.IsWaiting(AKey));
        Assert.False(backoff.IsWaiting(otherKey));
    }

    /// <summary>
    /// Svuotare toglie tutto: e' quello che serve a una ricarica dell'impianto, che rifa' lo stato
    /// di esecuzione da zero. Prima della voce B70 estratta qui, la ricarica svuotava il conteggio
    /// ma non gli istanti, e un'attesa gia' fissata sopravviveva alla ricarica.
    /// </summary>
    [Fact]
    public void ClearingForgetsEveryKey()
    {
        var clock = new ManualClock();
        var backoff = new RetryBackoff(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(5), clock);
        var otherKey = Guid.NewGuid();

        backoff.RegisterFailure(AKey);
        backoff.RegisterFailure(otherKey);

        backoff.Clear();

        Assert.False(backoff.IsWaiting(AKey));
        Assert.False(backoff.IsWaiting(otherKey));
        Assert.Equal(1, backoff.RegisterFailure(AKey));
    }
}
