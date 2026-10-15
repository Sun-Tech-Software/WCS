using Wcs.Engine.Api;
using Xunit;

namespace Wcs.Engine.Test;

/// <summary>
/// Il permesso di modifica dell'impianto (compito 4, 4b): il primo che chiede lo ottiene, il
/// secondo no finche' non e' scaduto, e la scadenza si prova spostando un orologio finto - non
/// aspettando davvero i minuti della durata vera.
/// </summary>
public sealed class EditLockTest : IDisposable
{
    private readonly Func<DateTime> _originalClock = EditLock.Clock;
    private readonly List<string> _holders = [];

    private static void SetTime(DateTime now) => EditLock.Clock = () => now;

    /// <summary>Chiama <see cref="EditLock.TryAcquire"/> e registra il titolare per il rilascio in <see cref="Dispose"/>: lo stato di <see cref="EditLock"/> e' statico, e un test non deve lasciarlo occupato per il successivo.</summary>
    private bool TryAcquire(string holder, TimeSpan lease)
    {
        _holders.Add(holder);
        return EditLock.TryAcquire(holder, lease);
    }

    public void Dispose()
    {
        foreach (var holder in _holders) EditLock.Release(holder);
        EditLock.Clock = _originalClock;
    }

    [Fact]
    public void TheFirstToAskGetsIt()
    {
        SetTime(new DateTime(2026, 1, 1, 10, 0, 0));
        Assert.True(TryAcquire("a", TimeSpan.FromMinutes(1)));
        Assert.Equal("a", EditLock.Current!.Holder);
    }

    [Fact]
    public void TheSecondToAskDoesNotGetIt()
    {
        SetTime(new DateTime(2026, 1, 1, 10, 0, 0));
        Assert.True(TryAcquire("a", TimeSpan.FromMinutes(1)));
        Assert.False(TryAcquire("b", TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void TheSecondGetsItAfterExpiry()
    {
        var now = new DateTime(2026, 1, 1, 10, 0, 0);
        SetTime(now);
        Assert.True(TryAcquire("a", TimeSpan.FromMinutes(1)));

        // Un minuto e un secondo dopo: oltre la scadenza di "a", senza che nessuno l'abbia mai
        // rilasciato - il browser di "a" e' semplicemente sparito.
        SetTime(now.AddMinutes(1).AddSeconds(1));
        Assert.True(TryAcquire("b", TimeSpan.FromMinutes(1)));
        Assert.Equal("b", EditLock.Current!.Holder);
    }

    [Fact]
    public void RenewingAsTheHolderMovesTheExpiryPastTheOriginalDeadline()
    {
        var now = new DateTime(2026, 1, 1, 10, 0, 0);
        SetTime(now);
        Assert.True(TryAcquire("a", TimeSpan.FromMinutes(1))); // scadrebbe a +60s

        SetTime(now.AddSeconds(50));
        Assert.True(EditLock.Renew("a")); // sposta la scadenza a +110s

        // +65s: dopo i +60s della concessione originaria, ma prima dei +110s del rinnovo - se il
        // rinnovo non avesse spostato nulla, qui il permesso sarebbe gia' scaduto.
        SetTime(now.AddSeconds(65));
        Assert.NotNull(EditLock.Current);
        Assert.Equal("a", EditLock.Current!.Holder);
    }

    [Fact]
    public void RenewingAsSomeoneElseFails()
    {
        SetTime(new DateTime(2026, 1, 1, 10, 0, 0));
        Assert.True(TryAcquire("a", TimeSpan.FromMinutes(1)));
        Assert.False(EditLock.Renew("b"));
    }

    [Fact]
    public void ReleasingAsSomeoneElseDoesNothing()
    {
        SetTime(new DateTime(2026, 1, 1, 10, 0, 0));
        Assert.True(TryAcquire("a", TimeSpan.FromMinutes(1)));

        EditLock.Release("qualcun-altro");
        Assert.NotNull(EditLock.Current);
        Assert.Equal("a", EditLock.Current!.Holder);
    }

    /// <summary>
    /// La verifica che il cancello delle scritture (compito 4b, ultima passata) fa a ogni
    /// scrittura autorizzata: se il titolare combacia, la scadenza si sposta avanti nello stesso
    /// passo, non con un <see cref="EditLock.Renew"/> composto dopo come chiamata separata.
    /// </summary>
    [Fact]
    public void AnAuthorizedWriteMovesTheExpiryPastTheOriginalDeadline()
    {
        var now = new DateTime(2026, 1, 1, 10, 0, 0);
        SetTime(now);
        Assert.True(TryAcquire("a", TimeSpan.FromMinutes(1))); // scadrebbe a +60s

        SetTime(now.AddSeconds(50));
        var (valid, state) = EditLock.VerifyAndRenew("a"); // sposta la scadenza a +110s
        Assert.True(valid);
        Assert.Equal("a", state!.Holder);

        // +65s: dopo i +60s della concessione originaria, ma prima dei +110s del rinnovo - se la
        // scrittura non avesse spostato nulla, qui il permesso sarebbe gia' scaduto.
        SetTime(now.AddSeconds(65));
        Assert.NotNull(EditLock.Current);
        Assert.Equal("a", EditLock.Current!.Holder);
    }

    [Fact]
    public void AWriteFromSomeoneElseDoesNotRenewAndDoesNotSucceed()
    {
        SetTime(new DateTime(2026, 1, 1, 10, 0, 0));
        Assert.True(TryAcquire("a", TimeSpan.FromMinutes(1)));

        var (valid, state) = EditLock.VerifyAndRenew("b");
        Assert.False(valid);
        Assert.Equal("a", state!.Holder); // il messaggio di rifiuto dice chi tiene davvero il permesso

        // Il titolare vero non ha perso nulla: la sua scadenza e' quella originaria, non rinnovata da "b".
        Assert.Equal("a", EditLock.Current!.Holder);
    }

    [Fact]
    public void AWriteAfterExpiryDoesNotRenewAndReportsNoHolder()
    {
        var now = new DateTime(2026, 1, 1, 10, 0, 0);
        SetTime(now);
        Assert.True(TryAcquire("a", TimeSpan.FromMinutes(1)));

        SetTime(now.AddMinutes(1).AddSeconds(1)); // oltre la scadenza, nessuno l'ha rilasciato
        var (valid, state) = EditLock.VerifyAndRenew("a");
        Assert.False(valid);
        Assert.Null(state); // scaduto: nessun titolare da riportare, come Current gia' fa
    }
}
