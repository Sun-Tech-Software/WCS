using Microsoft.EntityFrameworkCore;
using Wcs.Engine.Api;
using Wcs.Engine.Collections;
using Wcs.Engine.Items;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using DbInfo = Wcs.Engine.ORM.DbInfo;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

public sealed class PlantConfigurationTest : IDisposable
{
    private readonly NavigatorStatusTypes _previousStatus = Navigator.NavStatus;
    private readonly WcsDatabase _store = WcsDatabase.InMemory();

    public PlantConfigurationTest()
    {
        Navigator.Store = _store;
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        // Si parte puliti, come fa RoutingTestPlant: le collezioni sono singleton e la pulizia di
        // chi c'era prima non basta. Un dispositivo ancora in marcia puo' aggiungere una missione
        // dopo il Dispose della sua classe - ActiveMissions.Add controlla lo Stop prima di prendere
        // il lucchetto, e l'inserimento arriva a motore gia' fermo - e la sostituzione dell'impianto
        // la trovava ancora li' («Ci sono missioni attive in memoria»). Visto sotto carico dopo
        // GetFromFailureTest, la cui missione e' sopravvissuta a tutta PausedExtractionRowsTest.
        ActiveMissions.Instance.Clear(removeFromDb: false).GetAwaiter().GetResult();
        PathDevices.Instance.Clear();
        PathJunctions.Instance.Clear();
    }

    public void Dispose()
    {
        // Prima si ferma il navigatore, poi si rilasciano i dispositivi: lo StatusChanged a Stop
        // fa chiudere i cicli di sfondo, che altrimenti girerebbero ancora mentre la classe di
        // test successiva legge le stesse collezioni singleton.
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        ActiveMissions.Instance.Clear(removeFromDb: false).GetAwaiter().GetResult();
        PathDevices.Instance.Clear();
        PathJunctions.Instance.Clear();
        Navigator.NavStatus = _previousStatus;
        Navigator.Store = null;
        _store.Dispose();
    }

    [Fact]
    public async Task AConfigurationForANewDeviceCreatesTheRowAndTheDeviceInMemory()
    {
        var id = Guid.NewGuid();
        await PlantQueries.UpsertItemConfigurationAsync(new PlantItem { ItemId = id, Name = "Nastro 1", Asi = "0100", DeviceType = (short)DeviceTypes.Automation, DelayBlind = 7 });
        var stored = await PlantQueries.ItemConfigurationAsync(id);
        Assert.Equal(("Nastro 1", 7), (stored!.Name, stored.DelayBlind));
        var device = PathDevices.Instance[id];
        Assert.NotNull(device);
        Assert.Equal("0100", device.ASI);
        Assert.Single(await PlantQueries.ItemConfigurationsAsync());
    }

    [Fact]
    public async Task AConfigurationChangeDoesNotTouchTheLiveStatus()
    {
        var id = Guid.NewGuid();
        await PlantQueries.UpsertItemConfigurationAsync(new PlantItem { ItemId = id, Name = "A" });
        var device = PathDevices.Instance[id]!;
        device.Status = DeviceStatusTypes.Busy;
        await PlantQueries.UpsertItemConfigurationAsync(new PlantItem { ItemId = id, Name = "B", DelayBlind = 3 });
        Assert.Equal(("B", 3, DeviceStatusTypes.Busy), (device.Name, device.DelayBlind, device.Status));
    }

    [Fact]
    public async Task WritingConfigurationWhileRunningIsRefused()
    {
        Navigator.NavStatus = NavigatorStatusTypes.Run;
        await Assert.ThrowsAsync<EngineBusyException>(() => PlantQueries.UpsertItemConfigurationAsync(new PlantItem { ItemId = Guid.NewGuid() }));
        await Assert.ThrowsAsync<EngineBusyException>(() => PlantQueries.SetConnectionTypeForAllAsync(ConnectionTypes.Test));
        await Assert.ThrowsAsync<EngineBusyException>(() => PlantQueries.AddJunctionAsync(Guid.NewGuid(), Guid.NewGuid(), 1));
        await Assert.ThrowsAsync<EngineBusyException>(() => PlantQueries.SetJunctionPriorityAsync(Guid.NewGuid(), 1));
    }

    /// <summary>
    /// Le cinque facciate che il compito "run sola lettura" ha messo sotto la stessa guardia delle
    /// sorelle qui sopra: la posizione di un dispositivo (singola e d'insieme) e le tre rotte delle
    /// mappature porte. La guardia e' la prima riga di ognuna (si veda il remarks su
    /// <see cref="PlantQueries.SetItemGeometryAsync"/>), quindi solleva anche con un identificativo
    /// mai creato - qui non serve un dispositivo vero per provarlo.
    /// </summary>
    [Fact]
    public async Task WritingGeometryOrDoorMappingsWhileRunningIsRefused()
    {
        Navigator.NavStatus = NavigatorStatusTypes.Run;
        var itemId = Guid.NewGuid();
        await Assert.ThrowsAsync<EngineBusyException>(
            () => PlantQueries.SetItemGeometryAsync(
                new GeometryWrite(itemId, new ItemGeometry(1, 2, 3, 4, 0), Guid.NewGuid())));
        await Assert.ThrowsAsync<EngineBusyException>(
            () => PlantQueries.SetItemGeometriesAsync(
                [new GeometryWrite(itemId, new ItemGeometry(1, 2, 3, 4, 0), Guid.NewGuid())]));
        await Assert.ThrowsAsync<EngineBusyException>(
            () => PlantQueries.ReplaceDoorMappingsAsync([new DoorMapping { ItemId = itemId, DoorCode = "A", CellId = 1 }]));
        await Assert.ThrowsAsync<EngineBusyException>(() => PlantQueries.ClearDoorMappingsAsync(itemId));
        await Assert.ThrowsAsync<EngineBusyException>(() => PlantQueries.LinkBufferCellAsync(itemId, "A", 1));
    }

    [Fact]
    public async Task TheConnectionTypeIsAppliedToEveryDevice()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        await PlantQueries.UpsertItemConfigurationAsync(new PlantItem { ItemId = a });
        await PlantQueries.UpsertItemConfigurationAsync(new PlantItem { ItemId = b });
        await PlantQueries.SetConnectionTypeForAllAsync(ConnectionTypes.Test);
        Assert.All(new[] { a, b }, id => Assert.Equal(ConnectionTypes.Test, PathDevices.Instance[id]!.ConnectionType));
        Assert.All(await PlantQueries.ItemConfigurationsAsync(), row => Assert.Equal((short)ConnectionTypes.Test, row.ConnectionType));
    }

    [Fact]
    public async Task AJunctionIsCreatedOnceAndItsPriorityCanChange()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        await PlantQueries.UpsertItemConfigurationAsync(new PlantItem { ItemId = a });
        await PlantQueries.UpsertItemConfigurationAsync(new PlantItem { ItemId = b });
        var first = await PlantQueries.AddJunctionAsync(a, b, 5);
        var again = await PlantQueries.AddJunctionAsync(a, b, 9);
        Assert.Equal(first.ItemGuid, again.ItemGuid);
        Assert.True(await PlantQueries.SetJunctionPriorityAsync(first.ItemGuid, 2));
        Assert.Equal(2, PathJunctions.Instance.FirstOrDefault(j => j.ItemGuid == first.ItemGuid)!.Priority);
        Assert.False(await PlantQueries.SetJunctionPriorityAsync(Guid.NewGuid(), 1));
        await using var context = _store.CreateContext();
        Assert.Equal(2, (await PlantStore.FindJunctionAsync(context, first.ItemGuid))!.Priority);
    }

    [Fact]
    public async Task KeepingAnEmptyListDeletesNothing()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        await PlantQueries.UpsertItemConfigurationAsync(new PlantItem { ItemId = a, Name = "A" });
        await PlantQueries.UpsertItemConfigurationAsync(new PlantItem { ItemId = b, Name = "B" });

        await PlantQueries.KeepItemsAsync([]);
        Assert.Equal(2, (await PlantQueries.ItemConfigurationsAsync()).Count);

        await PlantQueries.KeepItemsAsync([a]);
        var remaining = await PlantQueries.ItemConfigurationsAsync();
        Assert.Single(remaining);
        Assert.Equal(a, remaining[0].ItemId);
    }

    /// <summary>
    /// Senza store la facciata solleva invece di rispondere come se avesse scritto: e' il
    /// comportamento di <c>StoreAccess.WriteAsync</c>, e ora la scrittura della configurazione ci
    /// passa davvero (prima attendeva una scrittura vuota come barriera).
    /// </summary>
    [Fact]
    public async Task WithoutAStoreTheConfigurationWriteThrows()
    {
        // Anche lo scrittore va azzerato esplicitamente: se fosse configurato, UpsertItemConfigurationAsync
        // passa da StoreAccess.WriteAsync (non da Navigator.StateWriter), ma il test deve restare
        // valido anche se un giorno la scrittura della configurazione iniziasse a preferirlo.
        var previousWriter = Navigator.StateWriter;
        Navigator.Store = null;
        Navigator.StateWriter = null;
        try
        {
            await Assert.ThrowsAsync<EngineStoreUnavailableException>(
                () => PlantQueries.UpsertItemConfigurationAsync(new PlantItem { ItemId = Guid.NewGuid(), Name = "Senza store" }));
        }
        finally
        {
            Navigator.Store = _store;
            Navigator.StateWriter = previousWriter;
        }
    }

    [Fact]
    public async Task RemovingEveryItemEmptiesMemoryAndStore()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        await PlantQueries.UpsertItemConfigurationAsync(new PlantItem { ItemId = a, Name = "A" });
        await PlantQueries.UpsertItemConfigurationAsync(new PlantItem { ItemId = b, Name = "B" });
        await PlantQueries.AddJunctionAsync(a, b, 3);
        await PlantQueries.ReplaceDoorMappingsAsync([new DoorMapping { ItemId = a, DoorCode = "P1", CellId = 10 }]);

        // Una missione salvata: il difetto 4 lasciava questa riga orfana, RemoveAllItemsAsync
        // svuotava dispositivi, giunzioni e mappa porte ma non toccava Missions. La scrittura passa
        // direttamente da MissionStore, non dal setter di PathMission, per non passare anche
        // dallo scrittore di stato del motore (qui non configurato).
        var mission = new PathMission(Guid.NewGuid());
        await using (var writing = _store.CreateContext())
        {
            await MissionStore.UpsertAsync(writing, MissionStore.Snapshot(mission));
        }

        await PlantQueries.RemoveAllItemsAsync();

        Assert.Empty(await PlantQueries.ItemConfigurationsAsync());
        Assert.Empty(await PlantQueries.DoorMappingsAsync());
        Assert.Equal(0, PathDevices.Instance.Count);
        Assert.Equal(0, PathJunctions.Instance.Count);
        await using var context = _store.CreateContext();
        Assert.Empty(await PlantStore.LoadJunctionsAsync(context));
        Assert.Empty(await MissionStore.LoadAsync(context, OperationTypes.Inbound, activeFilter: false));
    }

    [Fact]
    public async Task RemovingEveryItemWhileRunningIsRefused()
    {
        var a = Guid.NewGuid();
        await PlantQueries.UpsertItemConfigurationAsync(new PlantItem { ItemId = a, Name = "A" });
        Navigator.NavStatus = NavigatorStatusTypes.Run;
        await Assert.ThrowsAsync<EngineBusyException>(PlantQueries.RemoveAllItemsAsync);
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        Assert.Single(await PlantQueries.ItemConfigurationsAsync());
    }

    /// <summary>
    /// L'importazione (compito 12): un impianto preesistente (un dispositivo, una geometria, una
    /// giunzione, una mappatura porte) sparisce, e al suo posto compare solo quello dato -
    /// dispositivo nuovo, geometria nuova, nessuna giunzione ne' mappatura per chi non le porta.
    /// </summary>
    [Fact]
    public async Task ReplaceGraphAsyncReplacesDevicesGeometryJunctionsAndDoorMappings()
    {
        var oldA = Guid.NewGuid(); var oldB = Guid.NewGuid();
        await PlantQueries.UpsertItemConfigurationAsync(new PlantItem { ItemId = oldA, Name = "Vecchio A" });
        await PlantQueries.UpsertItemConfigurationAsync(new PlantItem { ItemId = oldB, Name = "Vecchio B" });
        await PlantQueries.AddJunctionAsync(oldA, oldB, 1);
        await PlantQueries.ReplaceDoorMappingsAsync([new DoorMapping { ItemId = oldA, DoorCode = "P1", CellId = 10 }]);

        var newA = Guid.NewGuid(); var newB = Guid.NewGuid();
        await PlantQueries.ReplaceGraphAsync(
            [new PlantItem { ItemId = newA, Name = "Nuovo A" }, new PlantItem { ItemId = newB, Name = "Nuovo B" }],
            [(newA, new ItemGeometry(1, 2, 3, 4, 0))],
            [(Guid.NewGuid(), newA, newB, 7)],
            []);

        var items = await PlantQueries.ItemConfigurationsAsync();
        Assert.Equal(2, items.Count);
        Assert.DoesNotContain(items, i => i.ItemId == oldA || i.ItemId == oldB);
        Assert.Contains(items, i => i.ItemId == newA && i.Name == "Nuovo A");

        var geometries = await PlantQueries.ItemGeometriesAsync();
        Assert.Equal(new ItemGeometry(1, 2, 3, 4, 0), geometries[newA].Geometry);

        Assert.Single(PathJunctions.Instance.Where(j => j.SourceId == newA && j.DestinationId == newB));
        Assert.Empty(await PlantQueries.DoorMappingsAsync());
    }

    /// <summary>
    /// Il disegno libero attraverso la sostituzione dell'impianto (decisione del proprietario del
    /// 2026-09-19). Le due meta' sono la stessa distinzione: <b>nullo</b> vuol dire «non ne parlo»
    /// - ed e' il comportamento che questa facciata ha sempre avuto, cioe' la decorazione resta
    /// dov'era in mezzo a dispositivi svuotati e ricreati - mentre un valore, <b>il vuoto
    /// compreso</b>, e' un ripristino.
    /// </summary>
    [Fact]
    public async Task ReplaceGraphAsyncTouchesTheDecorationOnlyWhenOneIsGiven()
    {
        await PlantQueries.SetPlantDecorationAsync("<mxCell id=\"nota\"/>");

        // Nullo (il valore predefinito, cioe' i quattro argomenti di sempre): non la tocca.
        await PlantQueries.ReplaceGraphAsync([], [], [], []);
        Assert.Equal("<mxCell id=\"nota\"/>", await PlantQueries.PlantDecorationAsync());

        // Un valore: la sostituisce.
        await PlantQueries.ReplaceGraphAsync([], [], [], [], "<mxCell id=\"altra\"/>");
        Assert.Equal("<mxCell id=\"altra\"/>", await PlantQueries.PlantDecorationAsync());

        // Il vuoto e' un valore come gli altri: toglie il disegno, non «non ne parla».
        await PlantQueries.ReplaceGraphAsync([], [], [], [], "");
        Assert.Equal("", await PlantQueries.PlantDecorationAsync());
    }

    [Fact]
    public async Task ReplaceGraphAsyncReplacesPlcConfigurationAndPreservesRuntimeStateForRetainedIds()
    {
        await using (var seed = _store.CreateContext())
        {
            seed.PlcConnections.AddRange(
                new PlcConnection
                {
                    Id = 7, Description = "old retained", PlcAddress = "old",
                    OpProgress = 11, LastCommandId = 12, StatusSenderChannel = 13,
                    StatusReceiverChannel = 14, OutboundChannelStatus = 15,
                    InboundChannelStatus = 16
                },
                new PlcConnection { Id = 8, Description = "removed", OpProgress = 99 });
            await seed.SaveChangesAsync();
        }

        await PlantQueries.ReplaceGraphAsync([], [], [], [], plcConnections:
        [
            new PlcConnection
            {
                Id = 7, Description = "new retained", PlcAddress = "10.0.0.7", PcmPort = 2007,
                LocalAddress = "10.0.1.7", LocalPort = 3007
            },
            new PlcConnection { Id = 9, Description = "new connection" }
        ]);

        await using var read = _store.CreateContext();
        var connections = await read.PlcConnections.OrderBy(c => c.Id).ToListAsync();
        Assert.Equal([7L, 9L], connections.Select(c => c.Id));
        var retained = connections[0];
        Assert.Equal(("new retained", "10.0.0.7", 2007, "10.0.1.7", 3007),
            (retained.Description, retained.PlcAddress, retained.PcmPort,
                retained.LocalAddress, retained.LocalPort));
        Assert.Equal((11, 12, (short)13, (short)14, (short)15, (short)16),
            (retained.OpProgress, retained.LastCommandId, retained.StatusSenderChannel,
                retained.StatusReceiverChannel, retained.OutboundChannelStatus,
                retained.InboundChannelStatus));
        var added = connections[1];
        Assert.Equal((0, 0, (short)0, (short)0, (short)0, (short)0),
            (added.OpProgress, added.LastCommandId, added.StatusSenderChannel,
                added.StatusReceiverChannel, added.OutboundChannelStatus,
                added.InboundChannelStatus));
    }

    [Fact]
    public async Task ReplaceGraphAsyncRejectsInvalidPlcConfigurationBeforeWriting()
    {
        await PlcSettingsFacade.ReplaceAsync(
            [new PlcConnection { Id = 81, Description = "unchanged" }]);
        var itemId = Guid.NewGuid();

        await Assert.ThrowsAsync<ArgumentException>(() => PlantQueries.ReplaceGraphAsync([], [], [], [],
            plcConnections:
            [
                new PlcConnection { Id = 82, Description = "first" },
                new PlcConnection { Id = 82, Description = "duplicate" }
            ]));
        await Assert.ThrowsAsync<ArgumentException>(() => PlantQueries.ReplaceGraphAsync([], [], [], [],
            plcConnections: [new PlcConnection { Id = 0 }]));
        await Assert.ThrowsAsync<ArgumentException>(() => PlantQueries.ReplaceGraphAsync(
            [new PlantItem { ItemId = itemId, DeviceConnectionId = 83 }], [], [], [],
            plcConnections: [new PlcConnection { Id = 82 }]));

        var unchanged = Assert.Single(await PlcSettingsFacade.ConnectionsAsync());
        Assert.Equal((81L, "unchanged"), (unchanged.Id, unchanged.Description));
        Assert.Empty(await PlantQueries.ItemConfigurationsAsync());
    }

    [Fact]
    public async Task ReplaceGraphAsyncWhileRunningIsRefused()
    {
        var a = Guid.NewGuid();
        await PlantQueries.UpsertItemConfigurationAsync(new PlantItem { ItemId = a, Name = "A" });
        Navigator.NavStatus = NavigatorStatusTypes.Run;
        await Assert.ThrowsAsync<EngineBusyException>(() => PlantQueries.ReplaceGraphAsync([], [], [], []));
        Navigator.NavStatus = NavigatorStatusTypes.Stop;

        // Rifiutata prima di svuotare: il vecchio impianto resta intatto.
        Assert.Single(await PlantQueries.ItemConfigurationsAsync());
    }

    [Fact]
    public async Task ReloadFailureAfterGraphCommitStopsTheEngineAndRequiresRecovery()
    {
        var previousLoadError = Navigator.LoadError;
        var incoming = Guid.NewGuid();
        try
        {
            // Stop, not Pause any more: since the owner's decision of 27/9 a plant is imported only with
            // the engine stopped. What this test proves - the committed graph stays, the engine is left
            // stopped with the load error, and the caller gets EngineNotLoadedException - is unchanged.
            Navigator.NavStatus = NavigatorStatusTypes.Stop;
            var error = await Record.ExceptionAsync(() => PlantQueries.ReplaceGraphCoreAsync(
                [new PlantItem { ItemId = incoming, Name = "Committed" }], [], [], [], "committed decoration",
                () => throw new InvalidOperationException("reload-test-failure")));

            Assert.Equal(incoming, Assert.Single(await PlantQueries.ItemConfigurationsAsync()).ItemId);
            Assert.Equal("committed decoration", await PlantQueries.PlantDecorationAsync());
            Assert.Contains("reload-test-failure", Navigator.LoadError ?? "");
            Assert.Equal(NavigatorStatusTypes.Stop, Navigator.NavStatus);
            Assert.IsType<EngineNotLoadedException>(error);
            Assert.Equal(Navigator.LoadError, error.Message);
        }
        finally { Navigator.LoadError = previousLoadError; }
    }

    [Fact]
    public async Task ConcurrentGraphReplacementWaitsForTheFirstMemoryPublication()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var firstPublicationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstPublication = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondPublicationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var firstReplacement = PlantQueries.ReplaceGraphCoreAsync(
            [new PlantItem { ItemId = first, Name = "First" }], [], [], [], null,
            async () =>
            {
                firstPublicationStarted.SetResult();
                await releaseFirstPublication.Task;
            });
        await firstPublicationStarted.Task;

        var secondReplacement = PlantQueries.ReplaceGraphCoreAsync(
            [new PlantItem { ItemId = second, Name = "Second" }], [], [], [], null,
            () =>
            {
                secondPublicationStarted.SetResult();
                return Task.CompletedTask;
            });

        try
        {
            // Il metodo asincrono avanza sincronicamente fino al gate: quando restituisce il Task,
            // la seconda sostituzione ha gia' tentato la presa e deve essere ancora sospesa.
            Assert.False(secondReplacement.IsCompleted);
            Assert.False(secondPublicationStarted.Task.IsCompleted);
            Assert.Equal(first, Assert.Single(await PlantQueries.ItemConfigurationsAsync()).ItemId);
        }
        finally
        {
            releaseFirstPublication.TrySetResult();
            await Task.WhenAll(firstReplacement, secondReplacement);
        }
        Assert.Equal(second, Assert.Single(await PlantQueries.ItemConfigurationsAsync()).ItemId);
    }

    [Fact]
    public async Task PublishingCapturedGraphRowsDoesNotRereadRowsChangedAfterCapture()
    {
        var source = Guid.NewGuid();
        var destination = Guid.NewGuid();
        var junctionId = Guid.NewGuid();
        await PlantQueries.ReplaceGraphAsync(
            [new PlantItem { ItemId = source, Name = "Captured source" }, new PlantItem { ItemId = destination }], [],
            [(junctionId, source, destination, 3)], []);
        var captured = await PlantQueries.CaptureGraphMemorySnapshotAsync();

        await using (var context = _store.CreateContext())
        {
            await context.PlantJunctions.ExecuteDeleteAsync();
            await context.PlantItems.ExecuteDeleteAsync();
        }

        PlantQueries.PublishGraphMemory(captured);

        Assert.Equal("Captured source", PathDevices.Instance[source]!.Name);
        Assert.Equal(junctionId, Assert.Single(PathJunctions.Instance.List)!.ItemGuid);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryPlantMutationWaitsForImportSnapshotPublication(bool delete)
    {
        var itemId = Guid.NewGuid();
        var snapshotCaptured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePublication = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var importing = PlantQueries.ReplaceGraphCoreAsync(
            [new PlantItem { ItemId = itemId, Name = "Imported" }], [], [], [], null,
            async () =>
            {
                var snapshot = await PlantQueries.CaptureGraphMemorySnapshotAsync();
                snapshotCaptured.SetResult();
                await releasePublication.Task;
                PlantQueries.PublishGraphMemory(snapshot);
            });
        await snapshotCaptured.Task;

        var ordinaryMutation = delete
            ? PlantQueries.RemoveItemAsync(itemId)
            : PlantQueries.UpdateItemConfigurationAsync(new PlantItem { ItemId = itemId, Name = "Edited" });

        try
        {
            // Come sopra: il ritorno del metodo dimostra che la mutazione e' arrivata al gate;
            // non serve concederle una finestra temporale arbitraria per vedere se passa.
            Assert.False(ordinaryMutation.IsCompleted);
        }
        finally
        {
            releasePublication.TrySetResult();
            await importing;
        }

        Assert.True(await ordinaryMutation);
        var persisted = await PlantQueries.ItemConfigurationAsync(itemId);
        Assert.Equal(delete ? null : "Edited", persisted?.Name);
        Assert.Equal(delete ? null : "Edited", PathDevices.Instance[itemId]?.Name);
    }

    [Fact]
    public async Task LoadingJunctionsRetainsTheirPersistedIdentifiers()
    {
        var source = Guid.NewGuid();
        var destination = Guid.NewGuid();
        await PlantQueries.UpsertItemConfigurationAsync(new PlantItem { ItemId = source });
        await PlantQueries.UpsertItemConfigurationAsync(new PlantItem { ItemId = destination });
        var stored = await PlantQueries.AddJunctionAsync(source, destination, 4);

        PathJunctions.Instance.Clear();
        await DbInfo.LoadJunctions();

        var reloaded = Assert.Single(PathJunctions.Instance.List);
        Assert.NotNull(reloaded);
        Assert.Equal(stored.ItemGuid, reloaded.ItemGuid);
    }

    [Theory]
    [InlineData("mission")]
    [InlineData("queue")]
    [InlineData("memory")]
    public async Task ReplacingGraphWithPendingWorkPreservesGraphAndWork(string work)
    {
        var id = Guid.NewGuid();
        await PlantQueries.UpsertItemConfigurationAsync(new PlantItem { ItemId = id, Name = "Old" });
        var device = PathDevices.Instance[id];
        var missionId = Guid.NewGuid();
        PathMission? inMemory = null;
        await using (var c = _store.CreateContext())
        {
            if (work == "mission") c.Missions.Add(new Mission { MissionId = missionId });
            if (work == "queue") c.ExtractionQueue.Add(new ExtractionQueueItem { SourceItemId = id });
            await c.SaveChangesAsync();
            if (work == "memory")
            {
                // Il Run sveglia anche i dispositivi di altre classi ancora iscritti a
                // Navigator.StatusChanged, che possono aggiungere missioni loro: si guarda la
                // propria, non il conteggio.
                Navigator.NavStatus = NavigatorStatusTypes.Run;
                inMemory = new PathMission(missionId);
                ActiveMissions.Instance.Add(inMemory, buildRoute: false);
                await c.Missions.ExecuteDeleteAsync();
                Navigator.NavStatus = NavigatorStatusTypes.Stop;
                Assert.True(ActiveMissions.Instance.Contains(inMemory));
            }
        }

        await using var writer = new QueuedDatabaseWriter<WcsWrite>(new WcsBatchExecutor(_store).ExecuteAsync,
            new QueuedDatabaseWriterOptions { FlushInterval = TimeSpan.FromMilliseconds(1) });
        var previousWriter = Navigator.StateWriter;
        Exception? error;
        try
        {
            Navigator.StateWriter = writer;
            error = await Record.ExceptionAsync(() => PlantQueries.ReplaceGraphAsync([], [], [], []));
        }
        finally { Navigator.StateWriter = previousWriter; }

        Assert.NotNull(error);
        Assert.IsType<PlantGraphWorkPendingException>(error);
        Assert.Equal(0, writer.Failed);
        Assert.Equal(id, Assert.Single(await PlantQueries.ItemConfigurationsAsync()).ItemId);
        Assert.Same(device, PathDevices.Instance[id]);
        await using var read = _store.CreateContext();
        Assert.Equal(work == "mission" ? 1 : 0, await read.Missions.CountAsync(m => m.MissionId == missionId));
        Assert.Equal(work == "queue" ? 1 : 0, await read.ExtractionQueue.CountAsync());
        Assert.Equal(work == "memory", inMemory != null && ActiveMissions.Instance.Contains(inMemory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedGraphInsertRollsBackEveryGraphTableAndPreservesMemory(bool queued)
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var incoming = Guid.NewGuid();
        await PlantQueries.UpsertItemConfigurationAsync(new PlantItem { ItemId = a, Name = "Old A" });
        await PlantQueries.UpsertItemConfigurationAsync(new PlantItem { ItemId = b, Name = "Old B" });
        var version = (await PlantQueries.ItemGeometriesAsync())[a].Version;
        await PlantQueries.SetItemGeometryAsync(
            new GeometryWrite(a, new ItemGeometry(10, 20, 30, 40, 90), version));
        var junction = await PlantQueries.AddJunctionAsync(a, b, 4);
        await PlantQueries.ReplaceDoorMappingsAsync([new DoorMapping { ItemId = a, DoorCode = "Old door", CellId = 12 }]);
        await PlantQueries.SetPlantDecorationAsync("old decoration");
        await PlcSettingsFacade.ReplaceAsync(
            [new PlcConnection { Id = 71, Description = "old connection" }]);
        var device = PathDevices.Instance[a];
        await using (var c = _store.CreateContext())
        {
            // Only a generated Guid enters this DDL; SQLite cannot parameterize a trigger body.
            var trigger = $"CREATE TRIGGER fail_graph BEFORE INSERT ON PlantItems WHEN NEW.ItemId = '{incoming.ToString().ToUpperInvariant()}' BEGIN SELECT RAISE(ABORT, 'graph-test-failure'); END;";
            await c.Database.ExecuteSqlRawAsync(trigger);
        }

        await using var writer = new QueuedDatabaseWriter<WcsWrite>(new WcsBatchExecutor(_store).ExecuteAsync,
            new QueuedDatabaseWriterOptions { FlushInterval = TimeSpan.FromMilliseconds(1) });
        var previousWriter = Navigator.StateWriter;
        Exception? error;
        try
        {
            Navigator.StateWriter = queued ? writer : null;
            error = await Record.ExceptionAsync(() => PlantQueries.ReplaceGraphAsync(
                [new PlantItem { ItemId = incoming }], [], [], [], "new decoration",
                [new PlcConnection { Id = 72, Description = "new connection" }]));
        }
        finally { Navigator.StateWriter = previousWriter; }

        Assert.NotNull(error);
        Assert.Contains("graph-test-failure", error.ToString());
        Assert.Null(Navigator.LoadError);
        var rows = await PlantQueries.ItemConfigurationsAsync();
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.ItemId == a && r.Name == "Old A" && r.X == 10 && r.Rotation == 90);
        Assert.Contains(rows, r => r.ItemId == b && r.Name == "Old B");
        Assert.Equal(junction.ItemGuid, Assert.Single(await PlantQueries.JunctionsFromStoreAsync()).JunctionId);
        Assert.Equal("Old door", Assert.Single(await PlantQueries.DoorMappingsAsync()).DoorCode);
        Assert.Equal("old decoration", await PlantQueries.PlantDecorationAsync());
        var plc = Assert.Single(await PlcSettingsFacade.ConnectionsAsync());
        Assert.Equal((71L, "old connection"), (plc.Id, plc.Description));
        Assert.Same(device, PathDevices.Instance[a]);
        Assert.Equal(junction.ItemGuid, Assert.Single(PlantQueries.Junctions()).ItemGuid);
    }

    [Fact]
    public async Task FailedPlcInsertRollsBackTheGraphAndThePlcConfigurationTogether()
    {
        var oldItem = Guid.NewGuid();
        var incoming = Guid.NewGuid();
        await PlantQueries.UpsertItemConfigurationAsync(new PlantItem
            { ItemId = oldItem, Name = "old graph", DeviceConnectionId = 91 });
        await PlcSettingsFacade.ReplaceAsync(
            [new PlcConnection { Id = 91, Description = "old connection" }]);
        await using (var context = _store.CreateContext())
            await context.Database.ExecuteSqlRawAsync(
                "CREATE TRIGGER fail_plc_backup BEFORE INSERT ON PlcConnections " +
                "WHEN NEW.Id = 92 BEGIN SELECT RAISE(ABORT, 'plc-backup-test-failure'); END;");

        var error = await Record.ExceptionAsync(() => PlantQueries.ReplaceGraphAsync(
            [new PlantItem { ItemId = incoming, Name = "new graph", DeviceConnectionId = 92 }],
            [], [], [], plcConnections:
            [new PlcConnection { Id = 92, Description = "new connection" }]));

        Assert.NotNull(error);
        Assert.Contains("plc-backup-test-failure", error.ToString());
        var item = Assert.Single(await PlantQueries.ItemConfigurationsAsync());
        Assert.Equal((oldItem, "old graph", 91m),
            (item.ItemId, item.Name, item.DeviceConnectionId));
        var plc = Assert.Single(await PlcSettingsFacade.ConnectionsAsync());
        Assert.Equal((91L, "old connection"), (plc.Id, plc.Description));
    }

    [Fact]
    public async Task ReplacingGraphUpdatesRetainedKeysAndReplacesGeneratedMappingIds()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        await PlantQueries.UpsertItemConfigurationAsync(new PlantItem { ItemId = a, Name = "Old A" });
        await PlantQueries.UpsertItemConfigurationAsync(new PlantItem { ItemId = b, Name = "Old B" });
        var junction = await PlantQueries.AddJunctionAsync(a, b, 1);
        await PlantQueries.ReplaceDoorMappingsAsync([new DoorMapping { ItemId = a, DoorCode = "Old" }]);
        var oldMapping = Assert.Single(await PlantQueries.DoorMappingsAsync());

        await PlantQueries.ReplaceGraphAsync(
            [new PlantItem { ItemId = a, Name = "New A" }, new PlantItem { ItemId = b, Name = "New B" }],
            [(a, new ItemGeometry(2, 3, 4, 5, 90))],
            [(junction.ItemGuid, b, a, 9)],
            [new DoorMapping { Id = oldMapping.Id, ItemId = b, DoorCode = "New", CellId = 25, Side = "B", OperationActive = 2, StoringLocked = true }]);

        Assert.Equal("New A", (await PlantQueries.ItemConfigurationAsync(a))!.Name);
        Assert.Equal(new ItemGeometry(2, 3, 4, 5, 90), (await PlantQueries.ItemGeometriesAsync())[a].Geometry);
        var savedJunction = Assert.Single(await PlantQueries.JunctionsFromStoreAsync());
        Assert.Equal((junction.ItemGuid, b, a, 9),
            (savedJunction.JunctionId, savedJunction.SourceItemId, savedJunction.DestinationItemId, savedJunction.Priority));
        var mapping = Assert.Single(await PlantQueries.DoorMappingsAsync());
        Assert.NotEqual(oldMapping.Id, mapping.Id);
        Assert.Equal((b, "New", 25m, "B", (short)2, true),
            (mapping.ItemId, mapping.DoorCode, mapping.CellId, mapping.Side, mapping.OperationActive, mapping.StoringLocked));
        Assert.Equal("New A", PathDevices.Instance[a]!.Name);
        Assert.Single(PlantQueries.Junctions());
    }

    [Fact]
    public async Task ReplacingTheGraphInvalidatesGeometryRequestsFromThePreviousPlant()
    {
        var itemId = Guid.NewGuid();
        var geometry = new ItemGeometry(10, 20, 30, 40, 50);
        await PlantQueries.UpsertItemConfigurationAsync(new PlantItem { ItemId = itemId, Name = "Before" });
        var initial = (await PlantQueries.ItemGeometriesAsync())[itemId];
        var positioned = await PlantQueries.SetItemGeometryAsync(
            new GeometryWrite(itemId, geometry, initial.Version));
        var versionBeforeImport = Assert.Single(positioned.Written).GeometryVersion;

        await PlantQueries.ReplaceGraphAsync(
            [new PlantItem { ItemId = itemId, Name = "After" }],
            [(itemId, geometry)], [], []);

        var imported = (await PlantQueries.ItemGeometriesAsync())[itemId];
        Assert.Equal(geometry, imported.Geometry);
        Assert.NotEqual(Guid.Empty, imported.Version);
        Assert.NotEqual(versionBeforeImport, imported.Version);

        var stale = await PlantQueries.SetItemGeometryAsync(new GeometryWrite(
            itemId, new ItemGeometry(90, 91, 92, 93, 94), versionBeforeImport));
        Assert.Equal([itemId], stale.ConflictingItemIds);
        Assert.Equal(geometry, (await PlantQueries.ItemGeometriesAsync())[itemId].Geometry);
    }

    [Fact]
    public async Task RetryingAGraphReplacementReusesTheSameGeometryVersion()
    {
        var itemId = Guid.NewGuid();
        var executor = new WcsBatchExecutor(_store);
        var attempts = 0;
        var versionAfterCommittedFirstAttempt = Guid.Empty;

        async Task CommitThenFailTheFirstAttempt(IReadOnlyList<WcsWrite> writes, CancellationToken ct)
        {
            await executor.ExecuteAsync(writes, ct);
            attempts++;
            await using var context = _store.CreateContext();
            var committedVersion = await context.PlantItems.AsNoTracking()
                .Where(item => item.ItemId == itemId)
                .Select(item => item.GeometryVersion)
                .SingleAsync(ct);
            if (attempts == 1)
            {
                versionAfterCommittedFirstAttempt = committedVersion;
                throw new InvalidOperationException("guasto simulato dopo il commit");
            }
        }

        await using var writer = new QueuedDatabaseWriter<WcsWrite>(CommitThenFailTheFirstAttempt,
            new QueuedDatabaseWriterOptions
            {
                DropOldestWhenFull = false,
                MaxBatchSize = 1,
                FlushInterval = TimeSpan.FromMilliseconds(1)
            });
        Navigator.StateWriter = writer;
        try
        {
            await PlantQueries.ReplaceGraphAsync(
                [new PlantItem { ItemId = itemId, Name = "Imported" }],
                [(itemId, new ItemGeometry(1, 2, 3, 4, 5))], [], []);
            // Il conto si legge con la coda vuota: una scrittura messa in coda dalla sostituzione e
            // non ancora eseguita non deve sfuggirgli (voce 42 della coda: due scritture della
            // ricarica in memoria correvano con questa asserzione).
            await writer.FlushAsync();

            Assert.Equal(2, attempts);
            Assert.NotEqual(Guid.Empty, versionAfterCommittedFirstAttempt);
            var finalVersion = (await PlantQueries.ItemGeometriesAsync())[itemId].Version;
            Assert.Equal(versionAfterCommittedFirstAttempt, finalVersion);
        }
        finally
        {
            Navigator.StateWriter = null;
            await writer.StopAsync();
        }
    }

    /// <summary>
    /// La ricarica in memoria dopo una sostituzione del grafo copia nei dispositivi le righe appena
    /// scritte, e non le riscrive: nello scrittore di stato passa la sola scrittura della
    /// sostituzione. Fino alla voce 42 della coda <c>StorageOffsetReversed</c> e
    /// <c>IdCellToDefragment</c> scrivevano l'istantanea del dispositivo anche durante la copia
    /// dalla riga, e le loro due scritture correvano con il conto della prova qui sopra.
    /// </summary>
    [Fact]
    public async Task ReloadingTheGraphIntoMemoryWritesNothingBack()
    {
        var itemId = Guid.NewGuid();
        var executor = new WcsBatchExecutor(_store);
        var executed = 0;

        async Task CountTheWrites(IReadOnlyList<WcsWrite> writes, CancellationToken ct)
        {
            await executor.ExecuteAsync(writes, ct);
            Interlocked.Add(ref executed, writes.Count);
        }

        await using var writer = new QueuedDatabaseWriter<WcsWrite>(CountTheWrites,
            new QueuedDatabaseWriterOptions
            {
                DropOldestWhenFull = false,
                MaxBatchSize = 1,
                FlushInterval = TimeSpan.FromMilliseconds(1)
            });
        Navigator.StateWriter = writer;
        try
        {
            await PlantQueries.ReplaceGraphAsync(
                [new PlantItem { ItemId = itemId, Name = "Imported", StorageOffsetReversed = 4, CellToDefragmentId = 9 }],
                [], [], []);
            await writer.FlushAsync();

            Assert.Equal(1, Volatile.Read(ref executed));
            var device = PathDevices.Instance[itemId]!;
            Assert.Equal((4, 9m), (device.StorageOffsetReversed, device.IdCellToDefragment));
        }
        finally
        {
            Navigator.StateWriter = null;
            await writer.StopAsync();
        }
    }

    [Fact]
    public async Task DuplicateJunctionIdsAreRejectedBeforeChangingGraph()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var junctionId = Guid.NewGuid();
        await PlantQueries.UpsertItemConfigurationAsync(new PlantItem { ItemId = a, Name = "Old" });
        await Assert.ThrowsAsync<ArgumentException>(() => PlantQueries.ReplaceGraphAsync(
            [new PlantItem { ItemId = a }, new PlantItem { ItemId = b }], [],
            [(junctionId, a, b, 1), (junctionId, b, a, 2)], []));
        Assert.Equal("Old", Assert.Single(await PlantQueries.ItemConfigurationsAsync()).Name);
        Assert.Empty(await PlantQueries.JunctionsFromStoreAsync());
    }

    /// <summary>
    /// Due giunzioni con gli stessi estremi nello stesso verso (voce 17 della coda, decisione del
    /// proprietario del 28/9): il motore riconosce una giunzione dalla coppia origine-destinazione
    /// (<c>PlantStore.UpsertJunctionAsync</c>, <c>FindJunctionAsync</c>, <c>AddJunctionAsync</c>) e
    /// la ricerca dei percorsi ne fa due passi identici, quindi la seconda non ha nessun significato.
    /// Anche senza identificativo, come negli archivi vecchi. Rifiutata prima di toccare l'impianto,
    /// nominando i due dispositivi.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TwoJunctionsWithTheSameEndsAreRejectedBeforeChangingGraph(bool withoutIdentifiers)
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        await PlantQueries.UpsertItemConfigurationAsync(new PlantItem { ItemId = a, Name = "Old" });
        var first = withoutIdentifiers ? Guid.Empty : Guid.NewGuid();
        var second = withoutIdentifiers ? Guid.Empty : Guid.NewGuid();

        var error = await Assert.ThrowsAsync<ArgumentException>(() => PlantQueries.ReplaceGraphAsync(
            [new PlantItem { ItemId = a, Name = "Rulliera" }, new PlantItem { ItemId = b, Name = "Navetta" }], [],
            [(first, a, b, 1), (second, a, b, 2)], []));

        Assert.Equal("junctions", error.ParamName);
        Assert.Contains("Rulliera", error.Message);
        Assert.Contains("Navetta", error.Message);
        Assert.Equal("Old", Assert.Single(await PlantQueries.ItemConfigurationsAsync()).Name);
        Assert.Empty(await PlantQueries.JunctionsFromStoreAsync());
    }

    /// <summary>A->B e B->A sono due giunzioni diverse: la ricerca parte dall'origine di ciascuna.</summary>
    [Fact]
    public async Task AJunctionEachWayBetweenTheSameTwoDevicesIsAccepted()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var there = Guid.NewGuid(); var back = Guid.NewGuid();

        await PlantQueries.ReplaceGraphAsync(
            [new PlantItem { ItemId = a }, new PlantItem { ItemId = b }], [],
            [(there, a, b, 1), (back, b, a, 2)], []);

        var stored = await PlantQueries.JunctionsFromStoreAsync();
        Assert.Equal(2, stored.Count);
        Assert.Contains(stored, j => j.JunctionId == there && j.SourceItemId == a && j.DestinationItemId == b);
        Assert.Contains(stored, j => j.JunctionId == back && j.SourceItemId == b && j.DestinationItemId == a);
        Assert.Equal(2, PathJunctions.Instance.List.Count(j => j != null));
    }

    [Theory]
    [InlineData("empty-item")]
    [InlineData("duplicate-item")]
    [InlineData("geometry")]
    [InlineData("junction")]
    [InlineData("mapping")]
    [InlineData("duplicate-geometry")]
    public async Task InvalidGraphIsRejectedBeforeChangingTheOldGraph(string invalid)
    {
        var old = Guid.NewGuid(); var proposed = Guid.NewGuid(); var missing = Guid.NewGuid();
        await PlantQueries.UpsertItemConfigurationAsync(new PlantItem { ItemId = old, Name = "Old" });
        var items = new List<PlantItem> { new() { ItemId = proposed } };
        if (invalid == "empty-item") items[0].ItemId = Guid.Empty;
        if (invalid == "duplicate-item") items.Add(new PlantItem { ItemId = proposed });

        var error = await Record.ExceptionAsync(() => PlantQueries.ReplaceGraphAsync(items,
            invalid switch
            {
                "geometry" => [(missing, new ItemGeometry(1, 2, 3, 4, 0))],
                // Due posizioni per lo stesso dispositivo (voce 5 della coda): vinceva l'ultima senza dirlo.
                "duplicate-geometry" => [(proposed, new ItemGeometry(1, 2, 3, 4, 0)), (proposed, new ItemGeometry(5, 6, 7, 8, 0))],
                _ => []
            },
            invalid == "junction" ? [(Guid.NewGuid(), proposed, missing, 1)] : [],
            invalid == "mapping" ? [new DoorMapping { ItemId = missing }] : []));

        Assert.NotNull(error);
        // Il rifiuto delle due posizioni e' quello nuovo della voce 5, non un altro che capiti prima.
        if (invalid == "duplicate-geometry")
            Assert.Equal("geometries", Assert.IsType<ArgumentException>(error).ParamName);
        Assert.Equal(old, Assert.Single(await PlantQueries.ItemConfigurationsAsync()).ItemId);
        Assert.NotNull(PathDevices.Instance[old]);
    }

    /// <summary>La decorazione e' parte dell'impianto: stessa guardia delle sue sorelle (compito 7).</summary>
    [Fact]
    public async Task WritingDecorationWhileRunningIsRefused()
    {
        Navigator.NavStatus = NavigatorStatusTypes.Run;
        await Assert.ThrowsAsync<EngineBusyException>(() => PlantQueries.SetPlantDecorationAsync("<mxCell/>"));
    }
}
