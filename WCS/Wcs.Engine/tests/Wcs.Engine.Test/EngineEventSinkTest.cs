using Wcs.Engine.Api;
using Wcs.Engine.Items;
using Wcs.Engine.Persistence;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

public sealed class EngineEventSinkTest : IDisposable
{
    private sealed class RecordingSink : IEngineEventSink
    {
        public readonly List<string> Calls = [];
        public void ItemChanged(AutomationDevice device) => Calls.Add($"item:{device.ItemGuid}");
        public void NavigatorStatusChanged() => Calls.Add("status");
        public void MissionChanged(PathMission mission, EngineMissionChange change) => Calls.Add($"mission:{change}");
        public void ExtractionQueueChanged(Persistence.Entities.ExtractionQueueItem item, EngineQueueChange change) => Calls.Add($"queue:{change}:{item.Id}");
        public void CommandChanged(Persistence.Entities.CommandRecord command) => Calls.Add($"command:{command.Id}");
    }

    private readonly NavigatorStatusTypes _previousStatus = Navigator.NavStatus;
    private readonly WcsDatabase _store = WcsDatabase.InMemory();
    private readonly RecordingSink _sink = new();

    public EngineEventSinkTest()
    {
        Navigator.Store = _store;
        Navigator.NavStatus = NavigatorStatusTypes.Run;
        Navigator.EventSink = _sink;
    }

    public void Dispose()
    {
        Navigator.EventSink = null;
        Navigator.NavStatus = _previousStatus;
        Navigator.Store = null;
        _store.Dispose();
    }

    [Fact]
    public void AStatusChangeReachesTheSink()
    {
        Navigator.NavStatus = NavigatorStatusTypes.Pause;
        Assert.Contains("status", _sink.Calls);
    }

    [Fact]
    public void WithoutASinkNothingBreaks()
    {
        Navigator.EventSink = null;
        Navigator.NavStatus = NavigatorStatusTypes.Pause;
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
    }

    [Fact]
    public async Task ACommandWrittenToTheStoreReachesTheSinkWithTheKeyItGot()
    {
        await using var context = _store.CreateContext();
        var record = new Persistence.Entities.CommandRecord { CommandType = 1001, Date = DateTime.Now };
        await CommandStore.UpsertAsync(context, record);
        // Prima del salvataggio la riga non ha ancora una chiave: l'evento non e' partito.
        Assert.DoesNotContain(_sink.Calls, c => c.StartsWith("command:"));
        await context.SaveChangesAsync();
        Assert.True(record.Id > 0);
        Assert.Equal($"command:{record.Id}", Assert.Single(_sink.Calls, c => c.StartsWith("command:")));
    }

    [Fact]
    public async Task AQueueRowAddedAndRemovedReachesTheSinkTwiceWithTheKeyItGot()
    {
        await using var context = _store.CreateContext();
        var row = new Persistence.Entities.ExtractionQueueItem { LoadUnitId = 7, SourceItemId = Guid.NewGuid() };
        await ExtractionQueueStore.UpsertAsync(context, row);
        await context.SaveChangesAsync();
        Assert.True(row.Id > 0);
        await ExtractionQueueStore.DeleteAsync(context, row.Id);
        await context.SaveChangesAsync();
        Assert.Equal([$"queue:Added:{row.Id}", $"queue:Removed:{row.Id}"], _sink.Calls.Where(c => c.StartsWith("queue:")));
    }

    [Fact]
    public async Task AnUpdateAndARemovalAreAnnouncedOnlyOnceSaved()
    {
        await using var context = _store.CreateContext();
        var row = new Persistence.Entities.ExtractionQueueItem { LoadUnitId = 7, SourceItemId = Guid.NewGuid() };
        await ExtractionQueueStore.UpsertAsync(context, row);
        var command = new Persistence.Entities.CommandRecord { CommandType = 1001, Date = DateTime.Now };
        await CommandStore.UpsertAsync(context, command);
        await context.SaveChangesAsync();
        _sink.Calls.Clear();

        await ExtractionQueueStore.SetProcessedAsync(context, row.Id, true);
        await CommandStore.SetStatusAsync(context, command.Id, Command.StatusTypes.Running, null);
        Assert.Empty(_sink.Calls);
        await context.SaveChangesAsync();
        Assert.Equal([$"queue:Updated:{row.Id}", $"command:{command.Id}"], _sink.Calls);

        _sink.Calls.Clear();
        await ExtractionQueueStore.DeleteAsync(context, row.Id);
        Assert.Empty(_sink.Calls);
        await context.SaveChangesAsync();
        Assert.Equal([$"queue:Removed:{row.Id}"], _sink.Calls);
    }

    [Fact]
    public async Task AnUpdateWhoseSaveFailsIsNeverAnnounced()
    {
        long id;
        await using (var first = _store.CreateContext())
        {
            var row = new Persistence.Entities.ExtractionQueueItem { LoadUnitId = 7, SourceItemId = Guid.NewGuid() };
            await ExtractionQueueStore.UpsertAsync(first, row);
            await first.SaveChangesAsync();
            id = row.Id;
        }

        _sink.Calls.Clear();
        await using var context = _store.CreateContext();
        await ExtractionQueueStore.SetProcessedAsync(context, id, true);
        // Somebody else removes the row in the meantime: the update touches nothing and the save fails.
        await using (var other = _store.CreateContext())
        {
            await ExtractionQueueStore.DeleteAsync(other, id);
            await other.SaveChangesAsync();
        }

        await Assert.ThrowsAnyAsync<Microsoft.EntityFrameworkCore.DbUpdateException>(() => context.SaveChangesAsync());
        Assert.Equal([$"queue:Removed:{id}"], _sink.Calls);
    }

    [Fact]
    public void AMissionAddedToTheEngineReachesTheSinkAsStarted()
    {
        var mission = new PathMission(Guid.NewGuid()) { OperationType = OperationTypes.Outbound };
        Collections.ActiveMissions.Instance.Add(mission, buildRoute: false);
        Assert.Contains("mission:Started", _sink.Calls);
        Collections.ActiveMissions.Instance.RemoveAll(m => m.MissionId == mission.MissionId);
    }

    /// <summary>
    /// Il cambio di posizione esce dal motore come evento della missione: senza, un client che
    /// tiene le missioni in cache dagli eventi le vedeva ferme sulla sorgente dopo la prima mossa
    /// (collaudo del 2026-09-07: la freccia del passo successivo spariva dopo il primo spostamento).
    /// La stessa posizione riscritta non e' un cambiamento e non parte nulla.
    /// </summary>
    [Fact]
    public void AMissionThatMovesReachesTheSinkAsPositionChanged()
    {
        var mission = new PathMission(Guid.NewGuid()) { OperationType = OperationTypes.Outbound };
        Collections.ActiveMissions.Instance.Add(mission, buildRoute: false);
        _sink.Calls.Clear();

        var position = Guid.NewGuid();
        mission.PositionItemId = position;
        Assert.Contains("mission:PositionChanged", _sink.Calls);

        _sink.Calls.Clear();
        mission.PositionItemId = position;
        Assert.DoesNotContain("mission:PositionChanged", _sink.Calls);

        Collections.ActiveMissions.Instance.RemoveAll(m => m.MissionId == mission.MissionId);
    }
}
