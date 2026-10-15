using Wcs.Engine.Api;
using Wcs.Engine.Collections;
using Wcs.Engine.Items;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Wcs.Wms.Contracts;
using Wcs.Wms.Contracts.Test;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// Il comando 1010 (<c>TestRoute</c>) e' il rimedio al pulsante «rotta di prova» dell'Editor, che
/// mandava 3007 con <c>loadUnitId: 0</c> e veniva sempre rifiutato da <c>ManageMoveLuToItem</c>
/// perche' <c>DbInfo.GetLuCode(0)</c> e' vuoto. <see cref="ExecutableCommands.TestRoute"/> non cerca
/// nessuna UDC: costruisce direttamente la missione dal dispositivo sorgente alla destinazione.
/// </summary>
/// <remarks>
/// Ogni setter di <see cref="Command"/> e di <see cref="PathMission"/> scrive subito sul database:
/// senza uno scrittore che serializzi le scritture (lo stesso di <see cref="Navigator.StateWriter"/>
/// in esercizio) le scritture concorrenti sul database in memoria di SQLite tornavano «database is
/// locked». Qui si usa lo stesso <see cref="QueuedDatabaseWriter{TWrite}"/> di
/// <c>MissionPersistenceTest</c>, non per verificare la persistenza ma solo per non farla incepparsi.
/// </remarks>
public sealed class TestRouteCommandTest : IAsyncLifetime
{
    private readonly NavigatorStatusTypes _previousStatus = Navigator.NavStatus;
    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private WcsDatabase _store = null!;
    private QueuedDatabaseWriter<WcsWrite> _writer = null!;

    /// <summary>
    /// Q1: la rotta di prova chiede al confine la modalita' di movimentazione del tipo dettato dal
    /// comando. Senza un gestionale cablato <c>RequireWarehouse</c> solleva, e il comando finirebbe
    /// in errore: il doppio c'e' perche' questo test provi la rotta di prova e non l'assenza del
    /// confine.
    /// </summary>
    private readonly FakeWarehouse _warehouse = new();

    public Task InitializeAsync()
    {
        _store = WcsDatabase.InMemory();
        _writer = new QueuedDatabaseWriter<WcsWrite>(new WcsBatchExecutor(_store).ExecuteAsync,
            new QueuedDatabaseWriterOptions
                { DropOldestWhenFull = false, MaxBatchSize = 100, FlushInterval = TimeSpan.FromMilliseconds(10) });

        Navigator.Store = _store;
        Navigator.StateWriter = _writer;
        Navigator.Warehouse = _warehouse;
        Navigator.NavStatus = NavigatorStatusTypes.Stop;

        // Come in AttesaNonBloccanteTest e altri: ActiveMissions e' un singleton di processo, e una
        // missione lasciata da un test precedente (in coda di rimozione, o mai ripulita) sballerebbe
        // il conteggio di questo test. Si pulisce anche qui, non solo in Dispose.
        ActiveMissions.Instance.RemoveAll(_ => true);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        // Prima si ferma il navigatore, poi si rilasciano i dispositivi: lo StatusChanged a Stop
        // fa chiudere i cicli di sfondo, che altrimenti girerebbero ancora mentre la classe di
        // test successiva legge le stesse collezioni singleton.
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        PathDevices.Instance.Clear();
        ActiveMissions.Instance.RemoveAll(_ => true);
        Navigator.NavStatus = _previousStatus;
        Navigator.StateWriter = null;
        Navigator.Warehouse = _previousWarehouse;
        Navigator.Store = null;
        await _writer.StopAsync();
        _store.Dispose();
    }

    [Fact]
    public async Task WithTheEngineStoppedTheCommandIsRefusedAndNoMissionIsAdded()
    {
        Navigator.NavStatus = NavigatorStatusTypes.Stop;

        var command = new Command
        {
            CommandType = Command.CommandTypes.TestRoute,
            ItemCommandId = Guid.NewGuid(),
            ItemDestinationId = Guid.NewGuid(),
            LuTypeId = 1
        };

        await ExecutableCommands.TestRoute(command).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(Command.StatusTypes.Error, command.Status);
        Assert.Equal(0, ActiveMissions.Instance.Count);
    }

    /// <summary>
    /// Una destinazione che non e' un dispositivo costruirebbe una missione che nessun percorso
    /// puo' chiudere: il comando si rifiuta, come gia' faceva per la sorgente (C38).
    /// </summary>
    [Fact]
    public async Task WithAMissingDestinationTheCommandIsRefusedAndNoMissionIsAdded()
    {
        var sourceId = Guid.NewGuid();

        await PlantQueries.UpsertItemConfigurationAsync(new PlantItem
        {
            ItemId = sourceId,
            Name = "Nastro di prova",
            DeviceType = (short)DeviceTypes.Automation,
            ConnectionType = (short)ConnectionTypes.Test
        });

        Navigator.NavStatus = NavigatorStatusTypes.Run;

        var command = new Command
        {
            CommandType = Command.CommandTypes.TestRoute,
            ItemCommandId = sourceId,
            ItemDestinationId = Guid.NewGuid(),
            LuTypeId = 1
        };

        await ExecutableCommands.TestRoute(command).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(Command.StatusTypes.Error, command.Status);
        Assert.Contains("Destination device not found", command.ErrorDescription ?? string.Empty);

        // Non si conta quante missioni ci sono: ActiveMissions e' un singleton di processo e i
        // cicli di sfondo dei dispositivi lasciati da altre classi di test ne aggiungono e ne
        // tolgono. Si guarda l'unica cosa che questo comando poteva creare: una missione con
        // questa sorgente.
        Assert.DoesNotContain(ActiveMissions.Instance.List, m => m != null && m.SourceItemId == sourceId);
    }

    [Fact]
    public async Task WithTheEngineRunningAndASourceDeviceTheCommandAddsOneMission()
    {
        var sourceId = Guid.NewGuid();
        var destinationId = Guid.NewGuid();

        await PlantQueries.UpsertItemConfigurationAsync(new PlantItem
        {
            ItemId = sourceId,
            Name = "Nastro di prova",
            DeviceType = (short)DeviceTypes.Automation,
            ConnectionType = (short)ConnectionTypes.Test
        });

        // La destinazione dev'essere un dispositivo vero: da questa revisione TestRoute rifiuta
        // una destinazione che non esiste.
        await PlantQueries.UpsertItemConfigurationAsync(new PlantItem
        {
            ItemId = destinationId,
            Name = "Destinazione di prova",
            DeviceType = (short)DeviceTypes.Automation,
            ConnectionType = (short)ConnectionTypes.Test
        });

        Navigator.NavStatus = NavigatorStatusTypes.Run;

        // Q1: la modalita' di movimentazione della missione di prova viene dal tipo, chiesta al
        // confine. Era DbInfo.GetHandlingMode, che senza database rispondeva zero.
        _warehouse.LoadUnitTypeHandlingModes[7] = new LoadUnitTypeHandlingMode(7, 2);

        var command = new Command
        {
            CommandType = Command.CommandTypes.TestRoute,
            ItemCommandId = sourceId,
            ItemDestinationId = destinationId,
            LuTypeId = 7
        };

        // ExecutableCommands.TestRoute chiude con item.LoadLu(...), che in fondo tocca il
        // LuManager del dispositivo: se lo stato PLC che il test non fornisce lo bloccasse, il
        // timeout fa fallire il test invece di lasciarlo appeso.
        await ExecutableCommands.TestRoute(command).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, ActiveMissions.Instance.Count);
        var mission = Assert.Single(ActiveMissions.Instance.List);
        Assert.Equal(MissionTypes.MovingToItem, mission!.MissionType);
        Assert.Equal(sourceId, mission.SourceItemId);
        Assert.Equal(destinationId, mission.DestinationItemId);
        Assert.Equal(7, mission.IdLuType);
        Assert.Equal(2, mission.HandlingMode);
        Assert.Equal(Command.StatusTypes.Done, command.Status);
    }
}
