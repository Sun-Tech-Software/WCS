using System.Diagnostics;
using Wcs.Engine.Persistence;
using Xunit;

namespace Wcs.Engine.Test;

/// <summary>
/// Lo scrittore a coda: un solo consumatore, lotti, chi produce non aspetta il database.
/// </summary>
/// <remarks>
/// L'esecutore e' finto e registra i lotti che riceve: cosi' si provano ordine, dimensione dei
/// lotti, attese, guasti, coda piena e arresto senza un database. I tempi sono larghi: i test
/// dicono «entro» e «non prima», mai «esattamente».
/// </remarks>
public class QueuedDatabaseWriterTest
{
    /// <summary>
    /// Un esecutore che registra tutto cio' che riceve e puo' essere reso lento, bloccato o guasto.
    /// </summary>
    private sealed class FakeExecutor
    {
        public readonly List<IReadOnlyList<string>> Batches = [];
        public readonly List<string> Executed = [];
        public TimeSpan Delay = TimeSpan.Zero;
        public SemaphoreSlim? Gate;
        public ManualResetEventSlim? Entered;
        public Func<IReadOnlyList<string>, bool>? Fails;

        public async Task ExecuteAsync(IReadOnlyList<string> batch, CancellationToken _)
        {
            Entered?.Set();
            if (Gate != null) await Gate.WaitAsync();
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay);
            if (Fails?.Invoke(batch) == true) throw new InvalidOperationException("guasto simulato");

            lock (Batches)
            {
                Batches.Add(batch);
                Executed.AddRange(batch);
            }
        }
    }

    private static string Write(string name) => name;

    [Fact]
    public async Task WritesArriveInOrderAndInBatchesNoLargerThanTheMaximum()
    {
        var executor = new FakeExecutor();
        await using var writer = new QueuedDatabaseWriter<string>(executor.ExecuteAsync,
            new QueuedDatabaseWriterOptions { MaxBatchSize = 100, FlushInterval = TimeSpan.FromMilliseconds(20) });

        for (var i = 0; i < 500; i++)
            Assert.True(writer.Enqueue(Write($"w{i}")));

        await writer.FlushAsync();

        Assert.Equal(Enumerable.Range(0, 500).Select(i => $"w{i}"), executor.Executed);
        Assert.All(executor.Batches, b => Assert.True(b.Count <= 100));
        Assert.True(executor.Batches.Count >= 5);
        Assert.Equal(500, writer.Written);
    }

    [Fact]
    public async Task ManyProducersLoseNothingAndEachKeepsItsOrder()
    {
        var executor = new FakeExecutor();
        await using var writer = new QueuedDatabaseWriter<string>(executor.ExecuteAsync,
            new QueuedDatabaseWriterOptions { MaxBatchSize = 50, FlushInterval = TimeSpan.FromMilliseconds(10) });

        Contesa.Insieme(8, producer =>
        {
            for (var i = 0; i < 200; i++)
                writer.Enqueue(Write($"p{producer}-{i}"));
            return 0;
        });

        await writer.FlushAsync();

        Assert.Equal(1600, executor.Executed.Count);
        for (var producer = 0; producer < 8; producer++)
        {
            var mine = executor.Executed.Where(s => s.StartsWith($"p{producer}-")).ToList();
            Assert.Equal(Enumerable.Range(0, 200).Select(i => $"p{producer}-{i}"), mine);
        }
    }

    /// <summary>
    /// Chi produce non aspetta il database: mille <c>Enqueue</c> passano mentre l'esecutore e'
    /// fermo dentro una scrittura.
    /// </summary>
    /// <remarks>
    /// Il discriminante non e' un tempo. L'esecutore si ferma su un gettone che tiene il test, e
    /// <c>Entered</c> dice che ci e' arrivato davvero: da li' in poi il database <b>non sta
    /// rispondendo</b>, e le mille scritture passano lo stesso. Se <c>Enqueue</c> aspettasse
    /// l'esecutore il ciclo non finirebbe, e il collaudo si appenderebbe invece di dare un verde
    /// falso. Prima c'era un <c>Task.Delay(50)</c> che <i>sperava</i> che il primo lotto fosse gia'
    /// dentro l'esecutore, e un <c>elapsed &lt; 100</c> su mille <c>Enqueue</c>: cento millisecondi
    /// di scheduling, su una macchina che compila, sono ordinari.
    /// </remarks>
    [Fact]
    public async Task TheProducerIsNeverHeldByTheDatabase()
    {
        var executor = new FakeExecutor { Gate = new SemaphoreSlim(0), Entered = new ManualResetEventSlim() };
        await using var writer = new QueuedDatabaseWriter<string>(executor.ExecuteAsync,
            new QueuedDatabaseWriterOptions { FlushInterval = TimeSpan.FromMilliseconds(1) });

        writer.Enqueue(Write("first"));

        // Il tetto fa fallire invece di appendere il collaudo: non discrimina niente.
        Assert.True(executor.Entered.Wait(TimeSpan.FromSeconds(30)));

        // L'esecutore e' fermo dentro la prima scrittura e ci resta.
        for (var i = 0; i < 1000; i++)
            Assert.True(writer.Enqueue(Write($"w{i}")));

        executor.Gate.Release(1000);

        await writer.FlushAsync();
        Assert.Equal(1001, writer.Written);
    }

    /// <summary>
    /// <c>EnqueueAndWaitAsync</c> si completa solo dopo che la scrittura e' stata eseguita.
    /// </summary>
    /// <remarks>
    /// Il discriminante non e' un tempo ma il gettone dell'esecutore: finche' e' chiuso il
    /// <see cref="Task"/> non e' completo, e quando si apre lo diventa. Prima c'era un
    /// <c>ElapsedMilliseconds &gt;= 90</c> per un ritardo di 100 ms: la granularita' del timer di
    /// Windows (~15,6 ms) e il coalescing possono far scadere il ritardo appena sotto la soglia.
    /// </remarks>
    [Fact]
    public async Task EnqueueAndWaitCompletesOnlyAfterTheWriteIsExecuted()
    {
        var executor = new FakeExecutor { Gate = new SemaphoreSlim(0), Entered = new ManualResetEventSlim() };
        await using var writer = new QueuedDatabaseWriter<string>(executor.ExecuteAsync,
            new QueuedDatabaseWriterOptions { FlushInterval = TimeSpan.FromMilliseconds(1) });

        var waited = writer.EnqueueAndWaitAsync(Write("waited"));

        Assert.True(executor.Entered.Wait(TimeSpan.FromSeconds(30)));
        Assert.False(waited.IsCompleted);

        executor.Gate.Release();
        await waited.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Contains("waited", executor.Executed);
    }

    [Fact]
    public async Task AnExclusiveWriteIsExecutedAloneBetweenWritesAlreadyQueuedAndWritesAfterIt()
    {
        var executor = new FakeExecutor();
        await using var writer = new QueuedDatabaseWriter<string>(executor.ExecuteAsync,
            new QueuedDatabaseWriterOptions { MaxBatchSize = 20, FlushInterval = TimeSpan.FromMilliseconds(100) });

        writer.Enqueue(Write("before"));
        var exclusive = writer.EnqueueExclusiveAndWaitAsync(Write("graph"));
        writer.Enqueue(Write("after"));
        await writer.FlushAsync();
        await exclusive;

        Assert.Equal(new[] { "before", "graph", "after" }, executor.Batches.SelectMany(b => b));
        Assert.Single(executor.Batches.Single(b => b.Contains("graph")));
    }

    [Fact]
    public async Task AnExclusiveWriteAtEitherEndStillPreservesFifoOrder()
    {
        var executor = new FakeExecutor();
        await using var writer = new QueuedDatabaseWriter<string>(executor.ExecuteAsync,
            new QueuedDatabaseWriterOptions { MaxBatchSize = 20, FlushInterval = TimeSpan.FromMilliseconds(100) });

        var first = writer.EnqueueExclusiveAndWaitAsync(Write("first"));
        writer.Enqueue(Write("middle"));
        var last = writer.EnqueueExclusiveAndWaitAsync(Write("last"));
        await writer.FlushAsync();
        await Task.WhenAll(first, last);

        Assert.Equal(new[] { "first", "middle", "last" }, executor.Batches.SelectMany(b => b));
        Assert.All(executor.Batches.Where(b => b.Contains("first") || b.Contains("last")), b => Assert.Single(b));
    }

    [Fact]
    public async Task AnExclusiveWritePropagatesItsExecutionFailure()
    {
        var executor = new FakeExecutor { Fails = batch => batch.Contains("graph") };
        await using var writer = new QueuedDatabaseWriter<string>(executor.ExecuteAsync,
            new QueuedDatabaseWriterOptions { FlushInterval = TimeSpan.FromMilliseconds(1) });

        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.EnqueueExclusiveAndWaitAsync(Write("graph")));
    }

    [Fact]
    public async Task StopDrainsAnExclusiveWriteDeferredByEarlierWrites()
    {
        var executor = new FakeExecutor { Entered = new ManualResetEventSlim() };
        var writer = new QueuedDatabaseWriter<string>(executor.ExecuteAsync,
            new QueuedDatabaseWriterOptions { MaxBatchSize = 20, FlushInterval = TimeSpan.FromSeconds(5) });

        writer.Enqueue(Write("before"));
        var exclusive = writer.EnqueueExclusiveAndWaitAsync(Write("graph"));
        var stopping = writer.StopAsync();

        // Quando "before" entra nell'esecutore, "graph" e' gia' stato trattenuto come deferred.
        Assert.True(executor.Entered.Wait(TimeSpan.FromSeconds(5)));
        await stopping;
        await exclusive.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { "before", "graph" }, executor.Batches.SelectMany(b => b));
        Assert.Single(executor.Batches.Single(b => b.Contains("graph")));
    }

    [Fact]
    public async Task AFailingBatchIsRetriedOneByOneAndTheWriterSurvives()
    {
        var executor = new FakeExecutor { Fails = batch => batch.Any(w => w == "bad") };
        var messages = new List<string>();
        await using var writer = new QueuedDatabaseWriter<string>(executor.ExecuteAsync,
            new QueuedDatabaseWriterOptions { MaxBatchSize = 10, FlushInterval = TimeSpan.FromMilliseconds(20) },
            (m, _) => { lock (messages) messages.Add(m); });

        writer.Enqueue(Write("a"));
        writer.Enqueue(Write("bad"));
        writer.Enqueue(Write("b"));
        await writer.FlushAsync();

        // Il lotto e' fallito per colpa di "bad": "a" e "b" sono state scritte da sole, "bad" scartata.
        Assert.Equal(["a", "b"], executor.Executed);
        Assert.Equal(1, writer.Failed);
        Assert.Equal(2, writer.Written);
        Assert.Contains(messages, m => m.Contains("riprova"));
        Assert.Contains(messages, m => m.Contains("scartata"));

        // E lo scrittore e' ancora vivo.
        writer.Enqueue(Write("c"));
        await writer.FlushAsync();
        Assert.Equal(["a", "b", "c"], executor.Executed);
    }

    [Fact]
    public async Task AWaitedWriteThatFailsAloneFaultsItsTask()
    {
        var executor = new FakeExecutor { Fails = batch => batch.Any(w => w == "bad") };
        await using var writer = new QueuedDatabaseWriter<string>(executor.ExecuteAsync,
            new QueuedDatabaseWriterOptions { FlushInterval = TimeSpan.FromMilliseconds(1) });

        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.EnqueueAndWaitAsync(Write("bad")));
    }

    /// <summary>
    /// La coda che non fa cadere nulla - quella dello stato - non mette al riparo dal rifiuto:
    /// <c>TryWrite</c> a coda piena risponde <c>false</c> invece di aspettare, anche in modalita'
    /// <c>Wait</c>, che governa solo <c>WriteAsync</c>. Una scrittura attesa viene quindi rifiutata
    /// anche mentre lo scrittore e' vivissimo, e il messaggio deve dire questa causa e non l'altra.
    /// </summary>
    [Fact]
    public async Task OnAFullQueueThatDoesNotDropAWaitedWriteIsRejectedWithTheQueueAsTheCause()
    {
        var executor = new FakeExecutor { Gate = new SemaphoreSlim(0), Entered = new ManualResetEventSlim() };
        var writer = new QueuedDatabaseWriter<string>(executor.ExecuteAsync,
            new QueuedDatabaseWriterOptions
                { Capacity = 4, MaxBatchSize = 1, FlushInterval = TimeSpan.FromMilliseconds(1), DropOldestWhenFull = false });

        // La prima scrittura entra nell'esecutore e resta bloccata al cancello: da qui in poi
        // nessuno svuota piu' la coda.
        Assert.True(writer.Enqueue(Write("first")));
        Assert.True(executor.Entered.Wait(TimeSpan.FromSeconds(5)));

        // Si riempie fino al rifiuto: e' la coda stessa a dire quando e' piena, non un'attesa a tempo.
        var filled = 0;
        while (writer.Enqueue(Write($"f{filled}"))) filled++;
        Assert.True(filled > 0, "la coda non ha accettato nemmeno una scrittura");

        var rejected = await Assert.ThrowsAsync<QueuedWriteRejectedException>(
            () => writer.EnqueueAndWaitAsync(Write("waited")));
        Assert.Contains("la coda e' piena", rejected.Message);

        executor.Gate.Release(1000);
        await writer.StopAsync();
    }

    /// <summary>
    /// L'altra causa del rifiuto, quella che il messaggio nominava da sola: lo scrittore fermo.
    /// </summary>
    [Fact]
    public async Task AWaitedWriteOnAStoppedWriterIsRejectedWithTheWriterAsTheCause()
    {
        var executor = new FakeExecutor();
        var writer = new QueuedDatabaseWriter<string>(executor.ExecuteAsync,
            new QueuedDatabaseWriterOptions { FlushInterval = TimeSpan.FromMilliseconds(1) });

        await writer.StopAsync();

        var rejected = await Assert.ThrowsAsync<QueuedWriteRejectedException>(
            () => writer.EnqueueAndWaitAsync(Write("late")));
        Assert.Contains("lo scrittore e' fermo", rejected.Message);
    }

    [Fact]
    public async Task WhenTheQueueIsFullTheOldestRowsAreDroppedAndCounted()
    {
        var executor = new FakeExecutor { Gate = new SemaphoreSlim(0), Entered = new ManualResetEventSlim() };
        var writer = new QueuedDatabaseWriter<string>(executor.ExecuteAsync,
            new QueuedDatabaseWriterOptions { Capacity = 10, MaxBatchSize = 1, FlushInterval = TimeSpan.FromMilliseconds(1) });

        // La prima scrittura entra nell'esecutore e resta bloccata al cancello.
        writer.Enqueue(Write("first"));
        Assert.True(executor.Entered.Wait(TimeSpan.FromSeconds(5)));

        // Con l'esecutore fermo, trenta scritture in una coda da dieci: le venti piu' vecchie cadono.
        for (var i = 0; i < 30; i++)
            writer.Enqueue(Write($"w{i}"));

        Assert.Equal(20, writer.Dropped);

        // Si svuota con l'arresto e non con FlushAsync: a coda piena la barriera del flush
        // prenderebbe il posto di un'altra riga.
        executor.Gate.Release(1000);
        await writer.StopAsync();

        Assert.Equal(new[] { "first" }.Concat(Enumerable.Range(20, 10).Select(i => $"w{i}")), executor.Executed);
        Assert.Equal(20, writer.Dropped);
    }

    [Fact]
    public async Task StopExecutesWhatIsQueuedWithoutWaitingTheInterval()
    {
        var executor = new FakeExecutor();
        var writer = new QueuedDatabaseWriter<string>(executor.ExecuteAsync,
            new QueuedDatabaseWriterOptions { MaxBatchSize = 1000, FlushInterval = TimeSpan.FromSeconds(5) });

        for (var i = 0; i < 50; i++)
            writer.Enqueue(Write($"w{i}"));

        var clock = Stopwatch.StartNew();
        await writer.StopAsync();

        Assert.Equal(50, executor.Executed.Count);
        Assert.True(clock.ElapsedMilliseconds < 2000, $"l'arresto ha aspettato l'intervallo: {clock.ElapsedMilliseconds} ms");
        Assert.False(writer.Enqueue(Write("late")));
    }

    [Fact]
    public async Task WhenTheWriterDoesNotDropAFullQueueMakesTheProducerWaitAndLosesNothing()
    {
        var executor = new FakeExecutor { Gate = new SemaphoreSlim(0), Entered = new ManualResetEventSlim() };
        var writer = new QueuedDatabaseWriter<string>(executor.ExecuteAsync,
            new QueuedDatabaseWriterOptions
                { Capacity = 10, MaxBatchSize = 1, FlushInterval = TimeSpan.FromMilliseconds(1), DropOldestWhenFull = false });

        writer.EnqueueOrWait(Write("first"));
        Assert.True(executor.Entered.Wait(TimeSpan.FromSeconds(5)));

        // Trenta scritture di stato in una coda da dieci, con l'esecutore fermo: il produttore si
        // ferma alla decima e riparte solo quando l'esecutore consuma.
        var produced = 0;
        var producer = new Thread(() =>
        {
            for (var i = 0; i < 30; i++)
            {
                writer.EnqueueOrWait(Write($"s{i}"));
                Interlocked.Increment(ref produced);
            }
        });
        producer.Start();

        // La coda tiene dieci righe e l'esecutore e' fermo sulla prima: il produttore arriva a
        // dieci e li' si ferma. L'attesa e' su quella condizione, non su un tempo, e il tetto fa
        // fallire invece di appendere il collaudo.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (Volatile.Read(ref produced) < 10)
        {
            Assert.True(DateTime.UtcNow < deadline, $"il produttore si e' fermato a {Volatile.Read(ref produced)} righe");
            Thread.Sleep(1);
        }

        // Fermo davvero: e' ancora vivo, e non e' andato oltre la capacita' della coda.
        Assert.True(producer.IsAlive);
        Assert.Equal(10, Volatile.Read(ref produced));

        executor.Gate.Release(1000);
        producer.Join();
        await writer.StopAsync();

        Assert.Equal(0, writer.Dropped);
        Assert.Equal(new[] { "first" }.Concat(Enumerable.Range(0, 30).Select(i => $"s{i}")), executor.Executed);
    }
}
