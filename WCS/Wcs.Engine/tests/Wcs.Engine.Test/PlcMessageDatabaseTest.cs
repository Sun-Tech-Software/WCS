using Microsoft.EntityFrameworkCore;
using Wcs.Connection.Interface;
using Wcs.Engine.Collections;
using Wcs.Engine.Items;
using Wcs.Engine.Persistence;
using Xunit;
using NavigatorLogRecord = Wcs.Engine.Items.NavigatorLogRecord;

namespace Wcs.Engine.Test;

/// <summary>
/// Il registro dei messaggi dei PLC finisce nel database dei PLC, e lo storico non lo vede
/// nemmeno.
/// </summary>
/// <remarks>
/// Fino alla voce <c>D183</c> la tabella <c>PlcMessages</c> stava nel file mensile dello storico,
/// insieme al registro del navigatore e alle UDC lavorate, e passava dalla stessa coda di
/// scrittura: sotto pieno cadevano le righe piu' vecchie senza guardare di quale registro
/// fossero, e i telegrammi sono molti di piu'. Adesso il registro dei messaggi ha un file suo
/// (<c>wcs-plc-AAAA-MM.db</c>), una coda sua e una ritenzione sua di sette giorni. Qui si guarda
/// che la riga arrivi di la' e che lo storico resti quello che era.
/// </remarks>
public sealed class PlcMessageDatabaseTest : IAsyncLifetime
{
    private WcsPlcLogDatabase _plcLog = null!;
    private QueuedDatabaseWriter<PlcLogWrite> _writer = null!;
    private WcsHistoryDatabase _history = null!;
    private QueuedDatabaseWriter<HistoryWrite> _historyWriter = null!;
    private readonly RecordingConnectionFactory _factory = new();
    private ConnectionFactoryScope _installed = null!;

    public Task InitializeAsync()
    {
        _plcLog = WcsPlcLogDatabase.InMemory();
        _writer = new QueuedDatabaseWriter<PlcLogWrite>(new WcsPlcLogBatchExecutor(_plcLog).ExecuteAsync,
            new QueuedDatabaseWriterOptions { FlushInterval = TimeSpan.FromMilliseconds(10) });
        // Lo storico c'e' davvero, con il suo scrittore: senza di lui «la riga non e' finita nello
        // storico» sarebbe vero anche se il codice provasse a scriverla.
        _history = WcsHistoryDatabase.InMemory();
        _historyWriter = new QueuedDatabaseWriter<HistoryWrite>(new WcsHistoryBatchExecutor(_history).ExecuteAsync,
            new QueuedDatabaseWriterOptions { FlushInterval = TimeSpan.FromMilliseconds(10) });

        Navigator.PlcLog = _plcLog;
        Navigator.PlcLogWriter = _writer;
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
        await _writer.StopAsync();
        await _historyWriter.StopAsync();
        _plcLog.Dispose();
        _history.Dispose();
    }

    /// <summary>
    /// Una riga passata ai servizi che il motore consegna al driver si rilegge dal database dei PLC con tutti
    /// i campi che la vecchia <c>INSERT INTO [PCM_LOG]</c> scriveva; e lo storico, accanto, non ha
    /// nemmeno piu' la tabella dove metterla.
    /// </summary>
    [Fact]
    public async Task AMessageWrittenThroughTheHookGoesToThePlcDatabaseAndNotToTheHistory()
    {
        var date = new DateTime(DateTime.Now.Year, DateTime.Now.Month, 3, 9, 30, 0);
        var services = _factory.Services;
        Assert.NotNull(services);

        // I quattro campi uint al massimo: la vecchia INSERT li castava a int e li avrebbe scritti
        // negativi.
        await services.WriteMessageAsync(new MessageLogRecord(date, 7m, 1, 12, "SYS1", 3, 4, uint.MaxValue,
            uint.MaxValue - 1, uint.MaxValue - 2, uint.MaxValue - 3, true, "crc non torna",
            "0100", "0002", "0003", 64, 987, "<Command />"));
        await _writer.FlushAsync();
        await _historyWriter.FlushAsync();

        await using (var context = _plcLog.CreateContext())
        {
            var row = Assert.Single(context.PlcMessages);
            Assert.Equal(date, row.Date);
            Assert.Equal(7m, row.PlcId);
            Assert.Equal(1, row.Type);
            Assert.Equal(12, row.HeaderLength);
            Assert.Equal("SYS1", row.Syscode);
            Assert.Equal(3, row.OpType);
            Assert.Equal(4, row.OpCode);
            Assert.Equal(uint.MaxValue, row.OpProgress);
            Assert.Equal(uint.MaxValue - 1, row.FrameLength);
            Assert.Equal(uint.MaxValue - 2, row.DataLength);
            Assert.Equal(uint.MaxValue - 3, row.DataCrc32);
            Assert.True(row.CodingError);
            Assert.Equal("crc non torna", row.CodingErrorDescription);
            Assert.Equal("0100", row.Asi);
            Assert.Equal("0002", row.Subitem);
            Assert.Equal("0003", row.Partition);
            Assert.Equal(64, row.MessageLength);
            Assert.Equal(987, row.MessageId);
            Assert.Equal("<Command />", row.Xml);
        }

        Assert.Empty(await TablesNamedPlcMessagesInTheHistoryAsync());
    }

    /// <summary>
    /// La potatura cancella davvero, e cancella nel file nuovo: le righe piu' vecchie della soglia
    /// se ne vanno, quelle piu' nuove restano, e il registro del navigatore nello storico non
    /// perde niente.
    /// </summary>
    /// <remarks>
    /// E' la ritenzione che il driver aveva su SQL Server — <c>PlcData.ClearOldData(7)</c>, una
    /// volta al giorno. Adesso che i due registri hanno due file, e' anche la prova che potare i
    /// messaggi non tocca lo storico: prima erano righe dello stesso database.
    /// </remarks>
    [Fact]
    public async Task PruningDeletesTheRowsOlderThanTheThresholdAndLeavesTheHistoryAlone()
    {
        var services = _factory.Services;
        Assert.NotNull(services);

        await services.WriteMessageAsync(Row(DateTime.Now.AddDays(-30), "<Old />"));
        await services.WriteMessageAsync(Row(DateTime.Now.AddDays(-8), "<JustOverTheEdge />"));
        await services.WriteMessageAsync(Row(DateTime.Now.AddDays(-1), "<Recent />"));
        await _writer.FlushAsync();
        var navigatorRows = await WriteNavigatorLogRowsAsync(3);

        await services.ClearMessagesOlderThanAsync(7);

        await using (var context = _plcLog.CreateContext())
            Assert.Equal("<Recent />", Assert.Single(context.PlcMessages).Xml);
        await using (var history = _history.CreateContext())
            Assert.Equal(navigatorRows, await history.NavigatorLog.CountAsync());
    }

    /// <summary>
    /// Una soglia di zero giorni non cancella niente: e' il ritorno anticipato che
    /// <c>PlcData.ClearOldData</c> aveva gia', e che qui non deve diventare «cancella tutto».
    /// </summary>
    [Fact]
    public async Task PruningWithNoThresholdDeletesNothing()
    {
        var services = _factory.Services;
        Assert.NotNull(services);

        await services.WriteMessageAsync(Row(DateTime.Now.AddYears(-5), "<VeryOld />"));
        await _writer.FlushAsync();

        await services.ClearMessagesOlderThanAsync(0);

        await using var context = _plcLog.CreateContext();
        Assert.Single(context.PlcMessages);
    }

    /// <summary>
    /// I nomi di tabella <c>PlcMessages</c> nello schema dello storico: dopo la migrazione che la
    /// toglie, nessuno.
    /// </summary>
    private async Task<List<string>> TablesNamedPlcMessagesInTheHistoryAsync()
    {
        await using var context = _history.CreateContext();
        return await context.Database
            .SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'table' AND name = 'PlcMessages'")
            .ToListAsync();
    }

    /// <summary>Scrive qualche riga del navigatore nello storico e ne restituisce il numero.</summary>
    private async Task<int> WriteNavigatorLogRowsAsync(int count)
    {
        for (var i = 0; i < count; i++)
            ORM.NavigatorLog.Add(new NavigatorLogRecord { ASI = "0100", Description = $"riga {i}" });
        await _historyWriter.FlushAsync();
        return count;
    }

    private static MessageLogRecord Row(DateTime date, string xml) =>
        new(date, 1m, 0, 8, "", 0, 0, 1, 2, 3, 4, false, "", "0100", "", "", 16, 1, xml);
}
