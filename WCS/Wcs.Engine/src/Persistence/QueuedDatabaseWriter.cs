using System.Threading.Channels;

namespace Wcs.Engine.Persistence;

/// <summary>
/// Esegue un lotto di scritture in una sola transazione. Se solleva, l'intero lotto e' da
/// considerare non scritto.
/// </summary>
public delegate Task BatchExecutor<in TWrite>(IReadOnlyList<TWrite> batch, CancellationToken cancellationToken);

/// <summary>
/// Le regole con cui lo scrittore raccoglie i lotti.
/// </summary>
public sealed class QueuedDatabaseWriterOptions
{
    /// <summary>
    /// Quante scritture la coda tiene al massimo.
    /// </summary>
    public int Capacity { get; init; } = 50_000;

    /// <summary>
    /// A coda piena: vero, cadono le scritture piu' vecchie (storico: si perde passato, mai il
    /// controllo dell'impianto); falso, <see cref="QueuedDatabaseWriter{TWrite}.EnqueueOrWait"/> fa
    /// aspettare chi scrive finche' c'e' posto (stato: non si perde nulla).
    /// </summary>
    public bool DropOldestWhenFull { get; init; } = true;

    /// <summary>
    /// Quante scritture al massimo in un lotto.
    /// </summary>
    public int MaxBatchSize { get; init; } = 200;

    /// <summary>
    /// Quanto lo scrittore aspetta, dalla prima scrittura di un lotto, prima di eseguirlo anche
    /// se non e' pieno.
    /// </summary>
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromMilliseconds(250);
}

/// <summary>
/// Un solo thread scrive sul database, a lotti; chi produce una riga la mette in coda e prosegue.
/// </summary>
/// <typeparam name="TWrite">
/// La forma di una scrittura: testo SQL con parametri (<see cref="DatabaseWrite"/>) per le tabelle
/// ancora su SQL Server, un'operazione sul contesto EF Core per il database del WCS.
/// </typeparam>
/// <remarks>
/// E' la forma decisa nel documento sul database del WCS: toglie il database dal thread del
/// campo e dai cicli del motore. La coda e' limitata; a coda piena, secondo le opzioni, cadono le
/// righe piu' vecchie (contate in <see cref="Dropped"/>) oppure chi scrive aspetta. Un lotto che
/// fallisce viene ritentato una scrittura per volta, e quelle che falliscono da sole vengono
/// scartate e contate in <see cref="Failed"/>. L'esecutore e' un delegato: i test lo sostituiscono
/// con uno finto e provano ordine, lotti, attese, guasti e arresto senza un database.
/// </remarks>
public sealed class QueuedDatabaseWriter<TWrite> : IAsyncDisposable
{
    /// <summary>
    /// La busta di una scrittura in coda: il carico, e chi eventualmente ne aspetta l'esito.
    /// </summary>
    private sealed class Envelope
    {
        public TWrite? Payload { get; init; }
        public TaskCompletionSource? Completion { get; init; }
        public bool IsBarrier { get; init; }
        public bool IsExclusive { get; init; }

        public static Envelope Of(TWrite payload, bool waitable) => new()
        {
            Payload = payload,
            Completion = waitable ? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) : null
        };

        public static Envelope Barrier() => new()
        {
            IsBarrier = true,
            Completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };

        public static Envelope Exclusive(TWrite payload) => new()
        {
            Payload = payload,
            IsExclusive = true,
            Completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
    }

    private readonly Channel<Envelope> _channel;
    private readonly BatchExecutor<TWrite> _execute;
    private readonly QueuedDatabaseWriterOptions _options;
    private readonly Action<string, Exception?>? _log;
    private readonly Func<TWrite, string> _describe;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _loop;

    private long _dropped;
    private long _written;
    private long _failed;
    private int _stopped;

    /// <param name="execute">Chi esegue un lotto, in una transazione.</param>
    /// <param name="options">Le regole dei lotti; nulle, valgono i predefiniti.</param>
    /// <param name="log">Dove dire di lotti falliti e scritture scartate; nullo, non si dice nulla.</param>
    /// <param name="describe">Come descrivere una scrittura nel log; nullo, si usa ToString.</param>
    public QueuedDatabaseWriter(BatchExecutor<TWrite> execute, QueuedDatabaseWriterOptions? options = null,
        Action<string, Exception?>? log = null, Func<TWrite, string>? describe = null)
    {
        _execute = execute;
        _options = options ?? new QueuedDatabaseWriterOptions();
        _log = log;
        _describe = describe ?? (w => w?.ToString() ?? string.Empty);

        _channel = Channel.CreateBounded<Envelope>(new BoundedChannelOptions(_options.Capacity)
        {
            FullMode = _options.DropOldestWhenFull ? BoundedChannelFullMode.DropOldest : BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        }, OnDropped);

        _loop = Task.Run(LoopAsync);
    }

    /// <summary>Scritture eseguite.</summary>
    public long Written => Interlocked.Read(ref _written);

    /// <summary>Scritture cadute perche' la coda era piena.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Scritture fallite anche da sole, e scartate.</summary>
    public long Failed => Interlocked.Read(ref _failed);

    /// <summary>Scritture in coda, non ancora eseguite.</summary>
    public int Pending => _channel.Reader.Count;

    /// <summary>
    /// Mette in coda e torna subito. Restituisce false se la coda non ha accettato la scrittura:
    /// lo scrittore e' fermo, oppure - su uno scrittore che non fa cadere nulla - la coda e' piena,
    /// perche' l'attesa promessa da quelle opzioni e' di <c>WriteAsync</c> e non di <c>TryWrite</c>.
    /// </summary>
    public bool Enqueue(TWrite write) => _channel.Writer.TryWrite(Envelope.Of(write, waitable: false));

    /// <summary>
    /// Mette in coda; a coda piena, se lo scrittore non scarta, aspetta che ci sia posto. E' la
    /// forma per le scritture di stato, che non si possono perdere e devono restare in ordine: una
    /// scrittura eseguita direttamente mentre le precedenti sono ancora in coda arriverebbe prima
    /// di loro. Restituisce false se lo scrittore e' fermo.
    /// </summary>
    public bool EnqueueOrWait(TWrite write)
    {
        var envelope = Envelope.Of(write, waitable: false);
        if (_channel.Writer.TryWrite(envelope)) return true;

        try
        {
            _channel.Writer.WriteAsync(envelope).AsTask().GetAwaiter().GetResult();
            return true;
        }
        catch (ChannelClosedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Mette in coda e restituisce un compito completato quando la scrittura e' stata eseguita,
    /// fallito con <see cref="QueuedWriteRejectedException"/> se la coda non l'ha accettata, e
    /// fallito con l'errore dell'esecutore se e' stata tentata e non e' riuscita. Per chi deve
    /// rileggere subito quello che ha scritto.
    /// </summary>
    /// <remarks>
    /// Il rifiuto ha due cause, e il compito che torna dice quale. <c>TryWrite</c> non aspetta mai:
    /// a coda piena risponde <c>false</c> anche quando le opzioni dicono di far aspettare chi
    /// scrive, perche' quella modalita' governa <c>WriteAsync</c>. Le si distingue guardando se
    /// l'arresto e' cominciato. E a differenza di <see cref="EnqueueOrWait"/>, qui il posto in coda
    /// non si aspetta: il rifiuto torna a chi ha chiamato.
    /// </remarks>
    public Task EnqueueAndWaitAsync(TWrite write)
    {
        var envelope = Envelope.Of(write, waitable: true);
        if (_channel.Writer.TryWrite(envelope)) return envelope.Completion!.Task;

        return Task.FromException(Volatile.Read(ref _stopped) == 1
            ? QueuedWriteRejectedException.WriterStopped()
            : QueuedWriteRejectedException.QueueFull());
    }

    /// <summary>
    /// Mette in coda una scrittura che l'esecutore riceve da sola, dopo tutte le scritture gia'
    /// accettate e prima di quelle successive.
    /// </summary>
    public Task EnqueueExclusiveAndWaitAsync(TWrite write)
    {
        var envelope = Envelope.Exclusive(write);
        if (_channel.Writer.TryWrite(envelope)) return envelope.Completion!.Task;

        return Task.FromException(Volatile.Read(ref _stopped) == 1
            ? QueuedWriteRejectedException.WriterStopped()
            : QueuedWriteRejectedException.QueueFull());
    }

    /// <summary>
    /// Completato quando tutto cio' che era in coda al momento della chiamata e' stato eseguito.
    /// </summary>
    /// <remarks>
    /// Passa da una barriera messa in coda: a coda piena la barriera prende il posto della riga
    /// piu' vecchia, come qualunque altra scrittura. Per svuotare senza aggiungere nulla c'e'
    /// <see cref="StopAsync"/>.
    /// </remarks>
    public Task FlushAsync()
    {
        var barrier = Envelope.Barrier();
        return _channel.Writer.TryWrite(barrier) ? barrier.Completion!.Task : Task.CompletedTask;
    }

    /// <summary>
    /// Non accetta piu' scritture, esegue quelle in coda e si ferma.
    /// </summary>
    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 1)
        {
            await _loop;
            return;
        }

        _channel.Writer.TryComplete();
        // L'attesa fra un lotto e l'altro non ha piu' senso: si svuota alla massima velocita'.
        await _stopping.CancelAsync();
        await _loop;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _stopping.Dispose();
    }

    private void OnDropped(Envelope envelope)
    {
        Interlocked.Increment(ref _dropped);
        envelope.Completion?.TrySetException(QueuedWriteRejectedException.Dropped());
    }

    private async Task LoopAsync()
    {
        var reader = _channel.Reader;
        var batch = new List<Envelope>(_options.MaxBatchSize);
        Envelope? deferred = null;

        // WaitToReadAsync restituisce false solo quando la coda e' completata E vuota: e' cosi'
        // che l'arresto svuota tutto prima di uscire.
        while (deferred != null || await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            batch.Clear();
            Collect(reader, batch, ref deferred);

            if (!batch[0].IsExclusive && batch.Count < _options.MaxBatchSize && !_stopping.IsCancellationRequested)
            {
                // Il lotto non e' pieno: si aspetta l'intervallo dalla prima riga, per raccoglierne
                // altre. Durante l'arresto l'attesa e' interrotta subito.
                try
                {
                    await Task.Delay(_options.FlushInterval, _stopping.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Arresto: si esegue quello che c'e'.
                }

                Collect(reader, batch, ref deferred);
            }

            await ExecuteAsync(batch).ConfigureAwait(false);
        }
    }

    private void Collect(ChannelReader<Envelope> reader, List<Envelope> batch, ref Envelope? deferred)
    {
        if (deferred != null && batch.Count == 0)
        {
            batch.Add(deferred);
            deferred = null;
            if (batch[0].IsExclusive)
                return;
        }

        if (deferred?.IsExclusive == true)
            return;

        while (batch.Count < _options.MaxBatchSize && reader.TryRead(out var envelope))
        {
            if (envelope.IsExclusive && batch.Count > 0)
            {
                deferred = envelope;
                return;
            }

            batch.Add(envelope);
            if (envelope.IsExclusive)
                return;
        }
    }

    private async Task ExecuteAsync(List<Envelope> batch)
    {
        var writes = batch.Where(e => !e.IsBarrier).ToList();

        try
        {
            if (writes.Count > 0)
            {
                await _execute(writes.Select(e => e.Payload!).ToList(), CancellationToken.None).ConfigureAwait(false);
                Interlocked.Add(ref _written, writes.Count);
            }

            foreach (var envelope in writes)
                envelope.Completion?.TrySetResult();
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Un lotto di {writes.Count} scritture e' fallito: si riprova una riga per volta.", ex);
            await RetryOneByOneAsync(writes).ConfigureAwait(false);
        }
        finally
        {
            // Le barriere si completano comunque: chi aspetta vuole sapere che il lotto e' passato,
            // non che sia andato bene.
            foreach (var envelope in batch)
                if (envelope.IsBarrier)
                    envelope.Completion?.TrySetResult();
        }
    }

    private async Task RetryOneByOneAsync(List<Envelope> writes)
    {
        foreach (var envelope in writes)
        {
            try
            {
                await _execute([envelope.Payload!], CancellationToken.None).ConfigureAwait(false);
                Interlocked.Increment(ref _written);
                envelope.Completion?.TrySetResult();
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _failed);
                _log?.Invoke($"Scrittura scartata dopo il secondo tentativo: {Summary(_describe(envelope.Payload!))}", ex);
                envelope.Completion?.TrySetException(ex);
            }
        }
    }

    private static string Summary(string text)
    {
        var compact = string.Join(' ', text.Split((char[])null!, StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= 120 ? compact : compact[..120] + "...";
    }
}
