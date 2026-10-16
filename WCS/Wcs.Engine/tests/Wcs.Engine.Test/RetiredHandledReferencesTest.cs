using Wcs.Engine.Api;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Xunit;

namespace Wcs.Engine.Test;

/// <summary>
/// Item D103: the guard «does this process have handled load units?» looked only at the months still
/// in line, and a process whose picks were in a month gone with the retention passed it. Now a month
/// leaving the retention leaves its references in <c>wcs.db</c> first, and the guard looks there too.
/// </summary>
public sealed class RetiredHandledReferencesTest : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wcs-retired-" + Guid.NewGuid().ToString("N"));
    private readonly WcsDatabase _store = WcsDatabase.InMemory();
    private DateTime _now = new(2026, 9, 10, 10, 0, 0);
    private WcsHistoryDatabase? _history;

    public RetiredHandledReferencesTest() => Navigator.Store = _store;

    public void Dispose()
    {
        Navigator.History = null;
        Navigator.Store = null;
        _history?.Dispose();
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    /// <summary>Two months in line; September has the picks of process 91 (load unit process 92, note 93).</summary>
    private WcsHistoryDatabase SeptemberWithPicks(Action<IReadOnlyCollection<RetiredHandledReference>>? keep)
    {
        _history = WcsHistoryDatabase.Open(_directory, monthsToKeep: 2, clock: () => _now, keepReferences: keep);
        using (var september = _history.CreateContext())
        {
            september.HandledLoadUnits.Add(new HandledLoadUnit
            {
                MissionId = Guid.NewGuid(), Type = 1, Date = _now, ProcessId = 91, LoadUnitProcessId = 92, NoteHeaderId = 93
            });
            september.HandledLoadUnits.Add(new HandledLoadUnit { MissionId = Guid.NewGuid(), Type = 1, Date = _now, ProcessId = 0 });
            september.SaveChanges();
        }
        Navigator.History = _history;
        return _history;
    }

    private string SeptemberFile => WcsHistoryDatabase.FilePath(_directory, new DateTime(2026, 9, 1));

    [Fact]
    public async Task AMonthLeavingTheRetentionLeavesItsReferencesAndTheGuardStillFindsThem()
    {
        var history = SeptemberWithPicks(references => RetiredHandledReferences.Keep(_store, references));

        _now = new DateTime(2026, 11, 3, 10, 0, 0);
        history.CreateContext().Dispose();

        Assert.False(File.Exists(SeptemberFile));
        Assert.True(await HistoryQueries.HandledLoadUnitExistsAsync(processId: 91, loadUnitProcessId: null, noteHeaderId: null));
        Assert.True(await HistoryQueries.HandledLoadUnitExistsAsync(processId: null, loadUnitProcessId: 92, noteHeaderId: null));
        Assert.True(await HistoryQueries.HandledLoadUnitExistsAsync(processId: null, loadUnitProcessId: null, noteHeaderId: 93));
        Assert.False(await HistoryQueries.HandledLoadUnitExistsAsync(processId: 92, loadUnitProcessId: null, noteHeaderId: null));
        Assert.False(await HistoryQueries.HandledLoadUnitExistsAsync(processId: 0, loadUnitProcessId: null, noteHeaderId: null));
    }

    [Fact]
    public void KeepingTheSameReferencesTwiceAddsNothing()
    {
        RetiredHandledReferences.Keep(_store, [new RetiredHandledReference { Kind = HandledReferenceKind.Process, Value = 91 }]);
        RetiredHandledReferences.Keep(_store,
        [
            new RetiredHandledReference { Kind = HandledReferenceKind.Process, Value = 91 },
            new RetiredHandledReference { Kind = HandledReferenceKind.NoteHeader, Value = 91 }
        ]);

        using var context = _store.CreateContext();
        Assert.Equal(2, context.RetiredHandledReferences.Count());
    }

    [Fact]
    public async Task WhenTheReferencesCannotBeKeptTheMonthStaysInLine()
    {
        var notices = new List<string>();
        var previousNotice = WcsHistoryDatabase.Notice;
        WcsHistoryDatabase.Notice = notices.Add;
        try
        {
            var history = SeptemberWithPicks(_ => throw new IOException("wcs.db is locked"));

            _now = new DateTime(2026, 11, 3, 10, 0, 0);
            history.CreateContext().Dispose();

            Assert.True(File.Exists(SeptemberFile));
            Assert.Contains(notices, n => n.Contains("wcs.db is locked") && n.Contains("resta in linea"));
            Assert.True(await HistoryQueries.HandledLoadUnitExistsAsync(processId: 91, loadUnitProcessId: null, noteHeaderId: null));
        }
        finally
        {
            WcsHistoryDatabase.Notice = previousNotice;
        }
    }
}
