using Wcs.Engine.Items;
using Wcs.Engine.Test.Routing;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// Le partenze lanciate e non attese del motore: un giro che fallisce non deve portarsi via il
/// ciclo, e un'eccezione non deve uscire da dove nessuno la osserva.
/// </summary>
/// <remarks>
/// I tre cicli di sfondo del motore (<c>UpdateStatus</c>, <c>BuildRoutePath</c>,
/// <c>MasterProcess</c>) avevano il proprio <c>catch</c> <b>fuori</b> dal <c>while</c>: la prima
/// eccezione terminava il ciclo per la vita del processo, in silenzio. E <c>ExecuteStep</c> aveva
/// la rete dei gettoni appesi e la presa del semaforo fuori dal proprio <c>try</c>, mentre i suoi
/// diciotto chiamanti lo invocano come <c>_ = device.ExecuteStep()</c>: un'eccezione di li' non la
/// osservava nessuno.
/// </remarks>
public sealed class BackgroundLoopTest : IDisposable
{
    private readonly RoutingTestPlant _plant = new(NavigatorStatusTypes.Run);

    public void Dispose()
    {
        _plant.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Un giro che solleva lascia una riga e il ciclo va avanti. Il collaudo aspetta il terzo giro,
    /// che e' una condizione e non un tempo: il tetto dei trenta secondi fa fallire invece di
    /// appendere il collaudo, e non discrimina niente.
    /// </summary>
    [Fact]
    public async Task AFailedTurnDoesNotTakeTheLoopAwayWithIt()
    {
        var turns = 0;
        var thirdTurn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();

        var loop = Navigator.BackgroundLoop("prova", () =>
        {
            var turn = Interlocked.Increment(ref turns);

            if (turn == 1) throw new InvalidOperationException("il primo giro non riesce");
            if (turn == 3) thirdTurn.TrySetResult();

            return Task.CompletedTask;
        }, () => 1, cts.Token);

        await thirdTurn.Task.WaitAsync(TimeSpan.FromSeconds(30));

        await cts.CancelAsync();
        await loop;

        Assert.True(turns >= 3);
    }

    /// <summary>
    /// <b>La segnalazione I-1 della revisione dell'ondata 1.</b> Una riga per giro fallito era
    /// troppe: <c>BuildRoutePath</c> gira ogni 10 ms e dopo la voce D94 le righe di errore passano
    /// sempre, anche verso il registro eventi di Windows. Lo stesso guasto ripetuto adesso scrive
    /// una volta, e poi un riepilogo.
    /// </summary>
    /// <remarks>
    /// Il canale che si guarda e' la console, che <c>Navigator.WriteLog</c> scrive nel proprio
    /// <c>finally</c> in modo sincrono: dopo che il ciclo e' stato annullato e atteso, quello che
    /// non c'e' non arrivera'. Con il registro non si potrebbe contare - la riga ci va da dentro un
    /// <c>Task.Run</c>. Il riepilogo e' a un'ora, quindi qui non scatta mai per tempo: l'unico che
    /// esce e' quello della fermata, e questo collaudo non dipende dall'orologio.
    /// </remarks>
    [Fact]
    public async Task ARepeatedFailureIsWrittenOnceAndThenCounted()
    {
        var turns = 0;
        var twentiethTurn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        using var console = new RecordingConsole();

        var loop = Navigator.BackgroundLoop("prova", () =>
        {
            if (Interlocked.Increment(ref turns) == 20) twentiethTurn.TrySetResult();
            throw new InvalidOperationException("il gestionale non risponde");
        }, () => 1, cts.Token, TimeSpan.FromHours(1));

        await twentiethTurn.Task.WaitAsync(TimeSpan.FromSeconds(30));

        await cts.CancelAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(30));

        var written = console.Lines.Where(line => line.StartsWith("prova background task failed:", StringComparison.Ordinal)).ToList();

        Assert.True(turns >= 20, $"giri: {turns}");
        Assert.Equal(2, written.Count);
        Assert.Contains("il gestionale non risponde", written[0]);
        Assert.DoesNotContain("altri", written[0]);
        Assert.Contains("altri", written[1]);
    }

    /// <summary>
    /// La meta' che deve riuscire, e senza la quale «non scrivere mai» passerebbe il collaudo qui
    /// sopra: un guasto <b>diverso</b> passa subito, e il conto di quello di prima si chiude.
    /// </summary>
    [Fact]
    public async Task ADifferentFailureIsWrittenStraightAway()
    {
        var turns = 0;
        var sixthTurn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        using var console = new RecordingConsole();

        var loop = Navigator.BackgroundLoop("prova", () =>
        {
            var turn = Interlocked.Increment(ref turns);
            if (turn == 6) sixthTurn.TrySetResult();
            throw new InvalidOperationException(turn <= 3 ? "il primo guasto" : "il secondo guasto");
        }, () => 1, cts.Token, TimeSpan.FromHours(1));

        await sixthTurn.Task.WaitAsync(TimeSpan.FromSeconds(30));

        await cts.CancelAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(30));

        var written = console.Lines
            .Where(line => line.StartsWith("prova background task failed:", StringComparison.Ordinal) &&
                           !line.Contains("altri", StringComparison.Ordinal))
            .ToList();

        Assert.Contains(written, line => line.Contains("il primo guasto", StringComparison.Ordinal));
        Assert.Contains(written, line => line.Contains("il secondo guasto", StringComparison.Ordinal));
    }

    /// <summary>
    /// E quando il giro torna a riuscire lo dice, una volta sola: senza, chi legge il registro vede
    /// un guasto e non sa mai che e' passato.
    /// </summary>
    [Fact]
    public async Task AFailingLoopThatRecoversSaysSoOnce()
    {
        var turns = 0;
        var fifthTurn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        using var console = new RecordingConsole();

        var loop = Navigator.BackgroundLoop("prova", () =>
        {
            var turn = Interlocked.Increment(ref turns);
            if (turn <= 3) throw new InvalidOperationException("il gestionale non risponde");
            if (turn == 5) fifthTurn.TrySetResult();
            return Task.CompletedTask;
        }, () => 1, cts.Token, TimeSpan.FromHours(1));

        await fifthTurn.Task.WaitAsync(TimeSpan.FromSeconds(30));

        await cts.CancelAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Single(console.Lines, line => line.Contains("il giro e' tornato a riuscire", StringComparison.Ordinal));
    }

    /// <summary>
    /// La meta' che deve riuscire: l'annullamento ferma il ciclo, e lo ferma davvero. Senza questa,
    /// un ciclo che non si fermasse mai passerebbe il collaudo qui sopra.
    /// </summary>
    [Fact]
    public async Task CancellingStopsTheLoop()
    {
        var firstTurn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();

        var loop = Navigator.BackgroundLoop("prova", () =>
        {
            firstTurn.TrySetResult();
            return Task.CompletedTask;
        }, () => 1, cts.Token);

        await firstTurn.Task.WaitAsync(TimeSpan.FromSeconds(30));

        await cts.CancelAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(loop.IsCompleted);
    }

    /// <summary>
    /// Un dispositivo in chiusura: il gettone di annullamento e' gia' scattato e la presa del
    /// semaforo solleva. L'esecuzione risponde «non ho eseguito», non lascia un'eccezione a
    /// nessuno - i suoi chiamanti la invocano come <c>_ = device.ExecuteStep()</c> e non la
    /// guarderebbero.
    /// </summary>
    [Fact]
    public async Task ExecuteStepOnADisposedDeviceAnswersInsteadOfThrowing()
    {
        var device = new AutomationDevice(Guid.NewGuid());
        device.Dispose();

        Assert.False(await device.ExecuteStep());
    }

    /// <summary>
    /// <b>La segnalazione K2 della revisione dell'ondata 1.</b> Il ciclo di estrazione di
    /// <c>AutomationDeviceStorageManager</c> era l'ultimo con il <c>catch</c> <b>fuori</b> dal
    /// <c>while</c>: un guasto qualunque del confine - <c>CheckExtraction</c> attraversa il
    /// gestionale - lo spegneva per la vita del processo, e l'estrazione di quel magazzino non
    /// ripartiva piu'. La riga c'era, il giro dopo no.
    /// </summary>
    [Fact]
    public async Task AFailedExtractionTurnDoesNotTakeTheExtractionLoopAwayWithIt()
    {
        using var device = new AutomationDevice(Guid.NewGuid());
        var manager = device.StorageManager!;
        var turns = 0;
        var thirdTurn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        manager.ExtractionEveryMilliseconds = 1;
        manager.ExtractionTurn = () =>
        {
            var turn = Interlocked.Increment(ref turns);

            if (turn == 1) throw new InvalidOperationException("il primo giro dell'estrazione non riesce");
            if (turn == 3) thirdTurn.TrySetResult();

            return Task.CompletedTask;
        };

        manager.StartProcessExtractionLu();

        // Il terzo giro e' una condizione, non un tempo: il tetto fa fallire invece di appendere.
        await thirdTurn.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(turns >= 3);
    }

    /// <summary>
    /// La meta' che deve riuscire: l'annullamento ferma anche questo ciclo. Senza, un ciclo che non
    /// si fermasse mai passerebbe il collaudo qui sopra - e resterebbe acceso per tutta la suite.
    /// </summary>
    [Fact]
    public async Task DisposingTheStorageManagerStopsTheExtractionLoop()
    {
        using var device = new AutomationDevice(Guid.NewGuid());
        var manager = device.StorageManager!;
        var firstTurn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        manager.ExtractionEveryMilliseconds = 1;
        manager.ExtractionTurn = () =>
        {
            firstTurn.TrySetResult();
            return Task.CompletedTask;
        };

        manager.StartProcessExtractionLu();
        await firstTurn.Task.WaitAsync(TimeSpan.FromSeconds(30));

        manager.Dispose();
        await manager.ExtractionLoop!.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(manager.ExtractionLoop.IsCompleted);
    }
}
