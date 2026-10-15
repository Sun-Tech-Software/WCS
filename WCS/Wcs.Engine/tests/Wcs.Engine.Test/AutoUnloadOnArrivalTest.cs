using Wcs.Engine.Api;
using Wcs.Engine.Collections;
using Wcs.Engine.Items;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Wcs.Wms.Contracts;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// Lo scarico automatico (<c>AutoUnloadLu</c>) deve scattare quando l'UDC <b>arriva</b> sul
/// dispositivo, non solo al passaggio in Run. Trovato alla prova generale del 2026-09-10 sull'elemento
/// di uscita OUT02: il trasferimento tiene il dispositivo <c>Busy</c> mentre chiama <c>LoadLu</c> e lo
/// rimette <c>Ready</c> solo dopo; dentro <c>LoadLu</c> la guardia sullo stato vedeva <c>Busy</c> e lo
/// scarico usciva in silenzio. L'UDC restava a bordo finche' qualcuno non dava Stop e Run.
/// </summary>
/// <remarks>
/// Stessa messa in scena di <c>TestRouteCommandTest</c>: store in memoria, scrittore a coda per non
/// incepparlo, e <c>NavStatus</c> in Run — con Stop o Pause <c>ActiveMissions.Add</c> esce subito, per
/// questo la missione aggiunta si asserisce. La missione non ha unita' di carico (<c>LuId</c> 0),
/// cosi' lo scarico non passa dal confine con il gestionale: qui si prova la sequenza, non il confine.
/// </remarks>
public sealed class AutoUnloadOnArrivalTest : IAsyncLifetime
{
    private readonly NavigatorStatusTypes _previousStatus = Navigator.NavStatus;
    private WcsDatabase _store = null!;
    private QueuedDatabaseWriter<WcsWrite> _writer = null!;

    public Task InitializeAsync()
    {
        _store = WcsDatabase.InMemory();
        _writer = new QueuedDatabaseWriter<WcsWrite>(new WcsBatchExecutor(_store).ExecuteAsync,
            new QueuedDatabaseWriterOptions
                { DropOldestWhenFull = false, MaxBatchSize = 100, FlushInterval = TimeSpan.FromMilliseconds(10) });

        Navigator.Store = _store;
        Navigator.StateWriter = _writer;
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        ActiveMissions.Instance.RemoveAll(_ => true);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        Navigator.AutoUnloadDelay = TimeSpan.Zero;
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        PathDevices.Instance.Clear();
        ActiveMissions.Instance.RemoveAll(_ => true);
        Navigator.NavStatus = _previousStatus;
        Navigator.StateWriter = null;
        Navigator.Store = null;
        await _writer.StopAsync();
        _store.Dispose();
    }

    /// <summary>
    /// Il dispositivo e' <c>Busy</c> perche' sta ricevendo (e' cosi' che <c>ThreadLoadLu</c> lo lascia
    /// mentre chiama <c>LoadLu</c>): con lo scarico automatico acceso, alla fine di <c>LoadLu</c>
    /// l'UDC non deve piu' essere a bordo e la missione non deve piu' essere attiva.
    /// </summary>
    [Fact]
    public async Task AnArrivingLoadUnitIsUnloadedWhileTheDeviceIsStillBusyReceivingIt()
    {
        var sourceId = Guid.NewGuid();
        var exitId = Guid.NewGuid();

        await PlantQueries.UpsertItemConfigurationAsync(new PlantItem
        {
            ItemId = sourceId, Name = "Nastro di prova",
            DeviceType = (short)DeviceTypes.Automation, ConnectionType = (short)ConnectionTypes.Test
        });
        await PlantQueries.UpsertItemConfigurationAsync(new PlantItem
        {
            ItemId = exitId, Name = "Uscita di prova",
            DeviceType = (short)DeviceTypes.Buffer, ConnectionType = (short)ConnectionTypes.Test,
            AutoUnloadLoadUnit = true
        });

        var exit = PathDevices.Instance[exitId];
        Assert.NotNull(exit);
        Assert.True(exit!.AutoUnloadLu);

        Navigator.NavStatus = NavigatorStatusTypes.Run;

        var mission = new PathMission
        {
            MissionType = MissionTypes.MovingToItem,
            SourceItemId = sourceId,
            DestinationItemId = exitId,
            PositionItemId = sourceId
        };
        ActiveMissions.Instance.Add(mission);
        Assert.Contains(ActiveMissions.Instance.List, m => m?.MissionId == mission.MissionId);

        // Lo stato che il trasferimento lascia addosso al ricevente mentre chiama LoadLu.
        exit.Status = DeviceStatusTypes.Busy;

        await exit.LoadLu(mission.MissionId, activateExecuteStep: false).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(exit.LoadedLu);
        Assert.DoesNotContain(ActiveMissions.Instance.List, m => m?.MissionId == mission.MissionId);
        Assert.Equal(DeviceStatusTypes.Ready, exit.Status);
    }

    /// <summary>
    /// L'attesa configurata (<c>Navigator.AutoUnloadDelay</c>): l'UDC resta a bordo per quel tempo,
    /// poi viene scaricata. Senza attesa spariva dall'uscita nell'istante dell'arrivo, e chi guarda
    /// non la vedeva mai (visto alla prova della demo dell'11/9).
    /// </summary>
    [Fact]
    public async Task TheAutoUnloadWaitsTheConfiguredDelayBeforeUnloading()
    {
        var sourceId = Guid.NewGuid();
        var exitId = Guid.NewGuid();

        await PlantQueries.UpsertItemConfigurationAsync(new PlantItem
        {
            ItemId = sourceId, Name = "Nastro di prova",
            DeviceType = (short)DeviceTypes.Automation, ConnectionType = (short)ConnectionTypes.Test
        });
        await PlantQueries.UpsertItemConfigurationAsync(new PlantItem
        {
            ItemId = exitId, Name = "Uscita di prova",
            DeviceType = (short)DeviceTypes.Buffer, ConnectionType = (short)ConnectionTypes.Test,
            AutoUnloadLoadUnit = true
        });

        var exit = PathDevices.Instance[exitId]!;
        Navigator.NavStatus = NavigatorStatusTypes.Run;
        Navigator.AutoUnloadDelay = TimeSpan.FromMilliseconds(400);

        var mission = new PathMission
        {
            MissionType = MissionTypes.MovingToItem,
            SourceItemId = sourceId,
            DestinationItemId = exitId,
            PositionItemId = sourceId
        };
        ActiveMissions.Instance.Add(mission);
        Assert.Contains(ActiveMissions.Instance.List, m => m?.MissionId == mission.MissionId);
        exit.Status = DeviceStatusTypes.Busy;

        var clock = System.Diagnostics.Stopwatch.StartNew();
        await exit.LoadLu(mission.MissionId, activateExecuteStep: false).WaitAsync(TimeSpan.FromSeconds(10));
        clock.Stop();

        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(380), $"scaricata dopo {clock.ElapsedMilliseconds} ms: l'attesa non c'e' stata");
        Assert.Null(exit.LoadedLu);
        Assert.DoesNotContain(ActiveMissions.Instance.List, m => m?.MissionId == mission.MissionId);
    }

    /// <summary>
    /// Il contrario, che deve restare vero: senza lo scarico automatico l'UDC arrivata resta a bordo.
    /// E' la riga che dice che il test sopra prova lo scarico e non un'altra cosa.
    /// </summary>
    [Fact]
    public async Task WithoutAutoUnloadTheArrivingLoadUnitStaysOnBoard()
    {
        var sourceId = Guid.NewGuid();
        var exitId = Guid.NewGuid();

        await PlantQueries.UpsertItemConfigurationAsync(new PlantItem
        {
            ItemId = sourceId, Name = "Nastro di prova",
            DeviceType = (short)DeviceTypes.Automation, ConnectionType = (short)ConnectionTypes.Test
        });
        await PlantQueries.UpsertItemConfigurationAsync(new PlantItem
        {
            ItemId = exitId, Name = "Uscita di prova",
            DeviceType = (short)DeviceTypes.Buffer, ConnectionType = (short)ConnectionTypes.Test,
            AutoUnloadLoadUnit = false
        });

        var exit = PathDevices.Instance[exitId]!;
        Navigator.NavStatus = NavigatorStatusTypes.Run;

        var mission = new PathMission
        {
            MissionType = MissionTypes.MovingToItem,
            SourceItemId = sourceId,
            DestinationItemId = exitId,
            PositionItemId = sourceId
        };
        ActiveMissions.Instance.Add(mission);
        Assert.Contains(ActiveMissions.Instance.List, m => m?.MissionId == mission.MissionId);
        exit.Status = DeviceStatusTypes.Busy;

        await exit.LoadLu(mission.MissionId, activateExecuteStep: false).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotNull(exit.LoadedLu);
        Assert.Equal(mission.MissionId, exit.LoadedLu!.MissionId);
    }

    /// <summary>
    /// Con un'unita' di carico vera e una cella collegata, l'arrivo alloca l'UDC nella cella del
    /// gestionale (in sfondo, dal setter della posizione) e lo scarico la rilascia: <b>prima
    /// l'allocazione, poi il rilascio</b>. Nell'ordine inverso la posizione risorge nel gestionale
    /// dopo che l'UDC e' uscita: e' successo dal vivo su OUT02 (due righe nello stesso secondo).
    /// Qui il confine e' finto e registra l'ordine delle chiamate.
    /// </summary>
    [Fact]
    public async Task TheArrivalAllocatesInTheLinkedCellBeforeTheAutoUnloadReleases()
    {
        var previousWarehouse = Navigator.Warehouse;
        var warehouse = new Wcs.Wms.Contracts.Test.FakeWarehouse
        {
            // Il gestionale vero ci mette un giro di rete ad allocare: senza questo ritardo il
            // doppio risponde all'istante e la corsa non si vede (sei giri verdi anche senza
            // l'attesa, verificato). Con il ritardo, senza l'attesa il rilascio supera l'allocazione.
            Before = op => op == nameof(IWarehouse.AllocateLoadUnitAsync) ? Task.Delay(300) : Task.CompletedTask
        };
        Navigator.Warehouse = warehouse;
        try
        {
            var sourceId = Guid.NewGuid();
            var exitId = Guid.NewGuid();

            await PlantQueries.UpsertItemConfigurationAsync(new PlantItem
            {
                ItemId = sourceId, Name = "Nastro di prova",
                DeviceType = (short)DeviceTypes.Automation, ConnectionType = (short)ConnectionTypes.Test
            });
            await PlantQueries.UpsertItemConfigurationAsync(new PlantItem
            {
                ItemId = exitId, Name = "Uscita di prova",
                DeviceType = (short)DeviceTypes.Buffer, ConnectionType = (short)ConnectionTypes.Test,
                AutoUnloadLoadUnit = true, LinkedCellId = 2795
            });

            var exit = PathDevices.Instance[exitId]!;
            Navigator.NavStatus = NavigatorStatusTypes.Run;

            var mission = new PathMission
            {
                MissionType = MissionTypes.MovingToItem,
                SourceItemId = sourceId,
                DestinationItemId = exitId,
                PositionItemId = sourceId,
                LuId = 753425,
                LuCode = "UG01185"
            };
            ActiveMissions.Instance.Add(mission);
            Assert.Contains(ActiveMissions.Instance.List, m => m?.MissionId == mission.MissionId);
            exit.Status = DeviceStatusTypes.Busy;

            await exit.LoadLu(mission.MissionId, activateExecuteStep: false).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Null(exit.LoadedLu);
            var allocate = warehouse.Calls.IndexOf(nameof(IWarehouse.AllocateLoadUnitAsync));
            var release = warehouse.Calls.IndexOf(nameof(IWarehouse.ReleaseLoadUnitAsync));
            Assert.True(allocate >= 0, "l'arrivo deve allocare l'UDC nella cella collegata; chiamate: " + string.Join(", ", warehouse.Calls));
            Assert.True(release >= 0, "lo scarico deve rilasciare l'UDC; chiamate: " + string.Join(", ", warehouse.Calls));
            Assert.True(allocate < release, "prima l'allocazione, poi il rilascio; chiamate: " + string.Join(", ", warehouse.Calls));
        }
        finally
        {
            Navigator.Warehouse = previousWarehouse;
        }
    }
}
