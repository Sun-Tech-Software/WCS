using Microsoft.EntityFrameworkCore;
using Wcs.Connection.Interface;
using Wcs.Engine.Collections;
using Wcs.Engine.Items;
using Wcs.Engine.Persistence;
using Xunit;
using NavigatorLogRecord = Wcs.Engine.Items.NavigatorLogRecord;

namespace Wcs.Engine.Test;

/// <summary>
/// Le due code sono due: un'ondata di telegrammi riempie la coda dei messaggi dei PLC e la fa
/// scartare, e il registro del navigatore non perde una riga.
/// </summary>
/// <remarks>
/// E' la voce <c>D183</c>. Con una coda sola per i due registri lo scarto del piu' vecchio
/// (<c>DropOldestWhenFull</c>) non guarda di che riga si tratti: le righe in attesa nella coda
/// sono uscite nell'ordine in cui erano entrate, e siccome i telegrammi sono molti di piu' quelle
/// a cadere erano soprattutto le altre — proprio la traccia che dice perche' l'impianto si e'
/// fermato. Qui la coda dei messaggi viene riempita mentre le righe del navigatore aspettano
/// nella loro, con la stessa capacita' piccola per tutt'e due: se la coda fosse una sola, quelle
/// righe sarebbero le prime a cadere.
/// <para>
/// Le due code sono deliberatamente ferme: lo scrittore prende il primo lotto e resta dentro
/// l'esecutore finche' la prova non lo lascia andare. Cosi' «piena» e' un fatto e non un
/// tempismo, e la prova non dipende da quanto e' veloce SQLite.
/// </para>
/// </remarks>
public sealed class PlcLogQueueSeparationTest : IAsyncLifetime
{
    /// <summary>Quattro scritture in coda: abbastanza piccola da riempirla senza scriverne 50 000.</summary>
    private const int Capacity = 4;

    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _historyExecuting = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _plcExecuting = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private WcsPlcLogDatabase _plcLog = null!;
    private WcsHistoryDatabase _history = null!;
    private QueuedDatabaseWriter<PlcLogWrite> _plcWriter = null!;
    private QueuedDatabaseWriter<HistoryWrite> _historyWriter = null!;
    private readonly RecordingConnectionFactory _factory = new();
    private ConnectionFactoryScope _installed = null!;

    public Task InitializeAsync()
    {
        _plcLog = WcsPlcLogDatabase.InMemory();
        _history = WcsHistoryDatabase.InMemory();

        var plcExecutor = new WcsPlcLogBatchExecutor(_plcLog);
        _plcWriter = new QueuedDatabaseWriter<PlcLogWrite>(async (batch, token) =>
        {
            _plcExecuting.TrySetResult();
            await _release.Task;
            await plcExecutor.ExecuteAsync(batch, token);
        }, new QueuedDatabaseWriterOptions { Capacity = Capacity, FlushInterval = TimeSpan.FromMilliseconds(10) });

        var historyExecutor = new WcsHistoryBatchExecutor(_history);
        _historyWriter = new QueuedDatabaseWriter<HistoryWrite>(async (batch, token) =>
        {
            _historyExecuting.TrySetResult();
            await _release.Task;
            await historyExecutor.ExecuteAsync(batch, token);
        }, new QueuedDatabaseWriterOptions { Capacity = Capacity, FlushInterval = TimeSpan.FromMilliseconds(10) });

        Navigator.PlcLog = _plcLog;
        Navigator.PlcLogWriter = _plcWriter;
        Navigator.History = _history;
        Navigator.HistoryWriter = _historyWriter;
        // The services the engine hands a driver, through the same path a driver gets them.
        _installed = ConnectionFactoryScope.Install(_factory);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _installed.Dispose();
        Navigator.PlcLogWriter = null;
        Navigator.PlcLog = null;
        Navigator.HistoryWriter = null;
        Navigator.History = null;
        _release.TrySetResult();
        await _plcWriter.StopAsync();
        await _historyWriter.StopAsync();
        _plcLog.Dispose();
        _history.Dispose();
    }

    [Fact]
    public async Task AFloodOfPlcMessagesDropsOnlyPlcMessages()
    {
        var services = _factory.Services;
        Assert.NotNull(services);

        // Una riga per registro apre le danze: lo scrittore la prende e resta nell'esecutore, cosi'
        // da qui in poi tutto quello che si accoda resta in coda.
        ORM.NavigatorLog.Add(Line("apripista"));
        await _historyExecuting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await services.WriteMessageAsync(Message(1));
        await _plcExecuting.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Le righe del navigatore che devono sopravvivere: quante ne tiene la coda.
        for (var i = 0; i < Capacity; i++)
            ORM.NavigatorLog.Add(Line($"riga {i}"));

        // L'ondata: molti piu' telegrammi di quanti la coda ne tenga.
        for (var i = 0; i < 200; i++)
            await services.WriteMessageAsync(Message(i + 2));

        Assert.True(_plcWriter.Dropped > 0, "l'ondata doveva riempire la coda dei messaggi e farle scartare qualcosa");
        Assert.Equal(0L, _historyWriter.Dropped);

        // Si lascia andare tutto e si guarda che cosa e' stato scritto davvero.
        _release.SetResult();
        await _plcWriter.StopAsync();
        await _historyWriter.StopAsync();

        await using var history = _history.CreateContext();
        Assert.Equal(Capacity + 1, await history.NavigatorLog.CountAsync());
        Assert.Equal(0L, _historyWriter.Dropped);

        await using var plc = _plcLog.CreateContext();
        Assert.True(await plc.PlcMessages.CountAsync() > 0, "qualche telegramma doveva pure arrivarci");
    }

    private static NavigatorLogRecord Line(string description) => new() { ASI = "0100", Description = description };

    private static MessageLogRecord Message(int id) =>
        new(DateTime.Now, 1m, 0, 8, "", 0, 0, 1, 2, 3, 4, false, "", "0100", "", "", 16, id, "<Flood />");
}
