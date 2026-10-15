using Wcs.Engine.Items;
using Wcs.Engine.Persistence;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// Lo storico mensile: il registro del navigatore arriva nel file del mese, il file cambia al
/// cambio di mese, i file oltre la ritenzione spariscono.
/// </summary>
/// <remarks>
/// Il cambio di mese si prova con un orologio iniettato su una cartella temporanea: e' l'unico
/// modo di vederlo senza aspettarlo. La cartella viene cancellata alla fine.
/// </remarks>
public sealed class HistoryStoreTest : IAsyncLifetime
{
    private WcsHistoryDatabase _history = null!;
    private QueuedDatabaseWriter<HistoryWrite> _writer = null!;

    public Task InitializeAsync()
    {
        _history = WcsHistoryDatabase.InMemory();
        _writer = new QueuedDatabaseWriter<HistoryWrite>(new WcsHistoryBatchExecutor(_history).ExecuteAsync,
            new QueuedDatabaseWriterOptions { FlushInterval = TimeSpan.FromMilliseconds(10) });
        Navigator.History = _history;
        Navigator.HistoryWriter = _writer;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        Navigator.HistoryWriter = null;
        Navigator.History = null;
        await _writer.StopAsync();
        _history.Dispose();
    }

    [Fact]
    public async Task ANavigatorLogRecordEndsUpInTheHistoryWithItsFields()
    {
        var device = new AutomationDevice(Guid.NewGuid()) { ASI = "0007", SUBITEM = "0001", PARTITION = "0002" };
        var step = new MissionStep
        {
            StepId = Guid.NewGuid(),
            MissionId = Guid.NewGuid(),
            ToNodeId = Guid.NewGuid(),
            RunningNode = device.ItemGuid,
            StartStep = new DateTime(2026, 9, 2, 8, 0, 0),
            EndStep = new DateTime(2026, 9, 2, 8, 0, 5),
            StepStatus = ResultCodeTypes.Success
        };

        Navigator.RecordNavigatorLog(device, "passo concluso", Log.Interface.LogEntryType.Information, step);
        await _writer.FlushAsync();

        await using var context = _history.CreateContext();
        var row = Assert.Single(context.NavigatorLog);
        Assert.Equal(device.ItemGuid, row.ItemId);
        Assert.Equal("0007", row.Asi);
        Assert.Equal("passo concluso", row.Description);
        Assert.Equal(step.MissionId, row.MissionId);
        Assert.Equal(step.ToNodeId, row.DestinationItemId);
        Assert.Equal(step.StartStep, row.MissionStart);
        Assert.Equal(step.EndStep, row.MissionEnd);
        Assert.Equal((short)ResultCodeTypes.Success, row.MissionStatus);
        Assert.Equal(device.ItemGuid, row.RunningItemId);
        Assert.True(row.Date > DateTime.Now.AddMinutes(-1));
    }

    [Fact]
    public async Task WithoutADeviceTheRecordCarriesTheSentinelAddress()
    {
        Navigator.RecordNavigatorLog(null, "avvio", Log.Interface.LogEntryType.Warning);
        await _writer.FlushAsync();

        await using var context = _history.CreateContext();
        var row = Assert.Single(context.NavigatorLog);
        Assert.Null(row.ItemId);
        Assert.Equal("9999", row.Asi);
        Assert.Null(row.MissionId);
        Assert.Null(row.MissionStart);
    }

    [Fact]
    public void TheMonthChangesTheFileAndOldMonthsFallOutOfRetention()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wcs-history-test-" + Guid.NewGuid().ToString("N"));
        var now = new DateTime(2026, 9, 2, 10, 0, 0);

        try
        {
            using var history = WcsHistoryDatabase.Open(directory, monthsToKeep: 2, clock: () => now);

            using (var context = history.CreateContext())
            {
                context.NavigatorLog.Add(new Persistence.Entities.NavigatorLogEntry { Date = now, Description = "settembre" });
                context.SaveChanges();
            }

            Assert.Equal([WcsHistoryDatabase.FilePath(directory, now)], history.Files());

            // Ottobre: file nuovo, settembre resta (ritenzione due mesi).
            now = new DateTime(2026, 10, 1, 0, 0, 1);
            using (var context = history.CreateContext())
            {
                context.NavigatorLog.Add(new Persistence.Entities.NavigatorLogEntry { Date = now, Description = "ottobre" });
                context.SaveChanges();
            }

            Assert.Equal(2, history.Files().Count);

            // Novembre: settembre e' oltre la ritenzione e sparisce; ottobre e novembre restano.
            now = new DateTime(2026, 11, 1, 0, 0, 1);
            using (var context = history.CreateContext())
            {
                Assert.Empty(context.NavigatorLog);
            }

            var files = history.Files().Select(Path.GetFileName).ToList();
            Assert.Equal(["wcs-history-2026-10.db", "wcs-history-2026-11.db"], files);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
