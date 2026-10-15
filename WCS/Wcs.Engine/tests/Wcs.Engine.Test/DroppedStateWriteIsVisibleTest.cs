using Wcs.Engine.Api;
using Wcs.Engine.Persistence;
using Xunit;

namespace Wcs.Engine.Test;

/// <summary>
/// Una scrittura di stato scartata definitivamente non sparisce piu' in silenzio.
/// </summary>
/// <remarks>
/// <para>
/// Lo scrittore ritenta il lotto una riga per volta e, se anche il secondo tentativo fallisce,
/// conta la riga in <c>Failed</c>, chiama il proprio registro e completa con l'eccezione chi
/// aspetta. Ma <c>Completion</c> e' <c>null</c> per tutto cio' che entra da <c>Enqueue</c> o
/// <c>EnqueueOrWait</c>, cioe' per tutte le scritture di stato ordinarie: nessuno riceveva
/// l'eccezione. Il registro cablato passava da <c>WriteLog</c> di livello Avviso, muto per default
/// (voce D94). E <c>Dropped</c> e <c>Failed</c> non li leggeva nessuno: nessuna API, nessun
/// allarme, nessun contatore esposto.
/// </para>
/// <para>
/// Adesso la riga passa anche da <c>ReportError</c>, che e' il canale «errore che non puo'
/// sollevare» e non dipende dalla diagnostica, e i contatori si possono chiedere.
/// </para>
/// </remarks>
public sealed class DroppedStateWriteIsVisibleTest : IDisposable
{
    private readonly Action<Exception>? _previousHostErrorLog = Navigator.HostErrorLog;
    private readonly bool _previousDebugLog = Navigator.ActivateDebugLog;
    private readonly QueuedDatabaseWriter<WcsWrite>? _previousStateWriter = Navigator.StateWriter;

    public void Dispose()
    {
        Navigator.StateWriter = _previousStateWriter;
        Navigator.HostErrorLog = _previousHostErrorLog;
        Navigator.ActivateDebugLog = _previousDebugLog;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// I contatori dei due scrittori si possono chiedere da fuori, e senza motore rispondono «non
    /// c'e' nessuno scrittore» invece di inventare degli zeri.
    /// </summary>
    [Fact]
    public async Task TheWriterCountersAreReadableFromOutside()
    {
        Navigator.StateWriter = null;
        Assert.Null(PlantQueries.StateWriterCounters());

        var executor = new CountingExecutor();
        var writer = new QueuedDatabaseWriter<WcsWrite>(executor.ExecuteAsync,
            new QueuedDatabaseWriterOptions { FlushInterval = TimeSpan.FromMilliseconds(1) });
        Navigator.StateWriter = writer;

        try
        {
            var counters = PlantQueries.StateWriterCounters();

            Assert.NotNull(counters);
            Assert.Equal(0, counters.Failed);
            Assert.Equal(0, counters.Dropped);
        }
        finally
        {
            Navigator.StateWriter = null;
            await writer.DisposeAsync();
        }
    }

    /// <summary>
    /// La riga scartata arriva al registro dell'host <b>con la diagnostica spenta</b>, e il
    /// contatore delle scritture perse la conta.
    /// </summary>
    [Fact]
    public async Task ADiscardedWriteReachesTheHostLogAndTheCounter()
    {
        Navigator.ActivateDebugLog = false;
        var reported = new List<Exception>();
        Navigator.HostErrorLog = ex => { lock (reported) reported.Add(ex); };

        // Fallisce solo la scrittura di questa prova. Lo scrittore e' quello del navigatore, e ci
        // arrivano anche le scritture di sfondo che altre classi lasciano in volo: con un esecutore
        // che faceva fallire ogni lotto, sotto carico il contatore ne trovava 7 invece di 1.
        WcsWrite discarded = _ => Task.CompletedTask;
        var executor = new CountingExecutor { Failing = discarded };
        var writer = new QueuedDatabaseWriter<WcsWrite>(executor.ExecuteAsync,
            new QueuedDatabaseWriterOptions { FlushInterval = TimeSpan.FromMilliseconds(1) },
            // Lo stesso registro che Navigator.OpenDatabases cabla allo scrittore vero.
            Navigator.ReportStateWriterProblem);

        try
        {
            Navigator.StateWriter = writer;
            writer.Enqueue(discarded);
            await writer.FlushAsync();

            lock (reported) Assert.NotEmpty(reported);

            var counters = PlantQueries.StateWriterCounters();
            Assert.NotNull(counters);
            Assert.Equal(1, counters.Failed);
        }
        finally
        {
            Navigator.StateWriter = null;
            await writer.DisposeAsync();
        }
    }

    private sealed class CountingExecutor
    {
        public WcsWrite? Failing { get; init; }

        public Task ExecuteAsync(IReadOnlyList<WcsWrite> batch, CancellationToken _) =>
            batch.Any(write => ReferenceEquals(write, Failing))
                ? Task.FromException(new InvalidOperationException("guasto simulato"))
                : Task.CompletedTask;
    }
}
