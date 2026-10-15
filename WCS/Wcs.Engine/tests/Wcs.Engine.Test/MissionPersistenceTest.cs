using Wcs.Engine.Items;
using Wcs.Engine.Persistence;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// Il percorso intero di una missione fino al file: i setter di <see cref="PathMission"/> passano
/// dall'ORM, l'ORM prende l'istantanea e la mette in coda allo scrittore di stato, lo scrittore la
/// applica sul database del WCS.
/// </summary>
/// <remarks>
/// <para>
/// E' il collegamento che <c>Navigator</c> fa all'avvio, rifatto qui su un database in memoria e
/// con il navigatore in <c>Run</c>, perche' in <c>Recover</c> l'ORM non scrive. Alla fine tutto
/// viene rimesso com'era: le altre classi di test contano su uno stato statico pulito.
/// </para>
/// <para>
/// Le prove guardano solo le righe della propria missione. Il lavoro di sfondo che altre classi
/// lasciano in volo - il PositionWork di una missione spostata o tolta, che riscrive missione e
/// dispositivo - scrive nello scrittore e nel database correnti quando arriva, cioe' in questi:
/// sotto carico una riga estranea restava nella tabella.
/// </remarks>
public sealed class MissionPersistenceTest : IAsyncLifetime
{
    private readonly NavigatorStatusTypes _previousStatus = Navigator.NavStatus;
    private WcsDatabase _database = null!;
    private QueuedDatabaseWriter<WcsWrite> _writer = null!;

    public Task InitializeAsync()
    {
        _database = WcsDatabase.InMemory();
        _writer = new QueuedDatabaseWriter<WcsWrite>(new WcsBatchExecutor(_database).ExecuteAsync,
            new QueuedDatabaseWriterOptions
                { DropOldestWhenFull = false, MaxBatchSize = 100, FlushInterval = TimeSpan.FromMilliseconds(10) });

        Navigator.Store = _database;
        Navigator.StateWriter = _writer;
        Navigator.NavStatus = NavigatorStatusTypes.Run;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        Navigator.NavStatus = _previousStatus;
        Navigator.StateWriter = null;
        Navigator.Store = null;
        await _writer.StopAsync();
        _database.Dispose();
    }

    [Fact]
    public async Task ASetterEndsUpInTheFileAndARemovalTakesItAway()
    {
        var mission = new PathMission(Guid.NewGuid()) { OperationType = OperationTypes.Outbound, ActiveFilter = true };
        ORM.PathMission.Add(mission);

        // Un setter qualunque: passa da SetProperty, dall'ORM, dalla coda.
        mission.StatusDescription = "scritta dal setter";
        await _writer.FlushAsync();

        await using (var context = _database.CreateContext())
        {
            var row = Assert.Single(await MissionStore.LoadAsync(context, OperationTypes.Outbound, activeFilter: true),
                m => m.MissionId == mission.MissionId);
            Assert.Equal(mission.MissionId, row.MissionId);
            Assert.Equal("scritta dal setter", row.StatusDescription);
        }

        ORM.PathMission.Remove(mission);
        await _writer.FlushAsync();

        await using var reading = _database.CreateContext();
        Assert.Empty(reading.Missions.Where(m => m.MissionId == mission.MissionId));
    }

    [Fact]
    public async Task ManySettersInARowLeaveOneRowWithTheLastValueAndTheWriterCountsThem()
    {
        var mission = new PathMission(Guid.NewGuid()) { OperationType = OperationTypes.Inbound };
        ORM.PathMission.Add(mission);

        for (var i = 1; i <= 50; i++)
            mission.StatusDescription = $"passo {i}";

        await _writer.FlushAsync();

        await using var reading = _database.CreateContext();
        var row = Assert.Single(reading.Missions.Where(m => m.MissionId == mission.MissionId));
        Assert.Equal("passo 50", row.StatusDescription);
        Assert.True(_writer.Written >= 51, $"scritture eseguite: {_writer.Written}");
        Assert.Equal(0, _writer.Failed);
    }

    /// <summary>
    /// A mission that has left the engine does not come back to the file. The arrival work of a
    /// stored mission ends with a full write of the row (StartUpdate) after the storing has already
    /// removed it (AllocateOnStorage dispatches the Allocate, and its completion removes the mission
    /// and deletes the row): the row came back with status Run, and PUT /plant/graph answered 409
    /// "work pending" until the host restarted (quick start run of 15/10, D1).
    /// </summary>
    [Fact]
    public async Task AWriteAfterTheRemovalDoesNotBringTheRowBack()
    {
        var mission = new PathMission(Guid.NewGuid()) { OperationType = OperationTypes.Inbound };
        Collections.ActiveMissions.Instance.Add(mission, buildRoute: false);
        try
        {
            mission.StatusDescription = "before the removal";
            await _writer.FlushAsync();
            await using (var context = _database.CreateContext())
                Assert.Single(context.Missions.Where(m => m.MissionId == mission.MissionId));

            Assert.True(await Collections.ActiveMissions.Instance.Remove(mission, releaseStorageReservation: false));
            // The deletion in its own batch, as in the host, where the arrival work writes later: in
            // the same batch the upsert would find the deleted entity and leave it deleted.
            await _writer.FlushAsync();
            mission.StatusDescription = "after the removal";
            await _writer.FlushAsync();

            await using var reading = _database.CreateContext();
            Assert.Empty(reading.Missions.Where(m => m.MissionId == mission.MissionId));
        }
        finally
        {
            Collections.ActiveMissions.Instance.RemoveAll(m => m.MissionId == mission.MissionId);
        }
    }

    /// <summary>
    /// The other side: a mission put back among the active ones is written again.
    /// </summary>
    [Fact]
    public async Task AMissionAddedAgainAfterTheRemovalIsWrittenAgain()
    {
        var mission = new PathMission(Guid.NewGuid()) { OperationType = OperationTypes.Inbound };
        Collections.ActiveMissions.Instance.Add(mission, buildRoute: false);
        try
        {
            Assert.True(await Collections.ActiveMissions.Instance.Remove(mission, releaseStorageReservation: false));
            Collections.ActiveMissions.Instance.Add(mission, buildRoute: false);
            mission.StatusDescription = "back in the engine";
            await _writer.FlushAsync();

            await using var reading = _database.CreateContext();
            var row = Assert.Single(reading.Missions.Where(m => m.MissionId == mission.MissionId));
            Assert.Equal("back in the engine", row.StatusDescription);
        }
        finally
        {
            Collections.ActiveMissions.Instance.RemoveAll(m => m.MissionId == mission.MissionId);
        }
    }
}
