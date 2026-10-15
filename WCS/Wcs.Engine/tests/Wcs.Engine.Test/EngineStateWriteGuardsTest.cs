using Wcs.Engine.Api;
using Wcs.Engine.Collections;
using Wcs.Engine.Items;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// Le decisioni del proprietario del 27/9 sulla revisione finale della console dell'Editor.
/// <list type="bullet">
/// <item><b>D1</b>: la modalita' di connessione dell'impianto intero, e il tipo di connessione o la
/// connessione PLC di un dispositivo, si cambiano solo a motore fermo (Stop). Il motore assegna
/// simulatore o PLC a un dispositivo solo quando apre le connessioni
/// (<c>DeviceConnections.OpenConnection</c>), e la ripresa da Pause non le riapre
/// (<c>ExecutableCommands.StartNavigator</c>). Le altre scritture della configurazione restano
/// permesse in Pause.</item>
/// <item><b>D2a</b>: ogni scrittura dell'impianto rifiutata in Run lo e' anche in Recover (il
/// caricamento dell'impianto, che finisce da solo in Stop) e in Dispose; il dettaglio nomina lo stato.
/// Pause resta permessa (decisione del 15/9).</item>
/// <item><b>D2b</b>: il ricaricamento dell'impianto - il comando di ricaricamento e il ritentativo
/// dell'avvio dell'host - corre sotto il cancello delle scritture, cosi' che una scrittura non
/// cada a meta' di un caricamento.</item>
/// </list>
/// </summary>
/// <remarks>
/// Il cancello si prova senza tempi: <see cref="PlantQueries.MutatePlantAsync(Func{Task})"/> con il
/// cancello libero entra nella mutazione IN MODO SINCRONO (il <c>WaitAsync</c> di un semaforo libero e'
/// gia' completo), quindi una sonda che non e' entrata subito dopo la chiamata e' una sonda che il
/// cancello sta trattenendo.
/// </remarks>
public sealed class EngineStateWriteGuardsTest : IAsyncLifetime
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
        CommandToDo.Instance.Clear();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        Navigator.LoadStartingForTesting = null;
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        CommandToDo.Instance.Clear();
        PathDevices.Instance.Clear();
        PathJunctions.Instance.Clear();
        Navigator.NavStatus = _previousStatus;
        Navigator.StateWriter = null;
        Navigator.Store = null;
        await _writer.StopAsync();
        _store.Dispose();
    }

    private static async Task<Guid> ADeviceAsync()
    {
        var id = Guid.NewGuid();
        await PlantQueries.UpsertItemConfigurationAsync(new PlantItem
        {
            ItemId = id, Name = "Navetta", DeviceType = (short)DeviceTypes.Automation,
            ConnectionType = (short)ConnectionTypes.Test, DeviceConnectionId = 0,
        });
        return id;
    }

    // ---- D2a --------------------------------------------------------------------------------------

    [Theory]
    [InlineData(NavigatorStatusTypes.Recover)]
    [InlineData(NavigatorStatusTypes.Dispose)]
    public async Task InRecoverAndDisposeAPlantWriteIsRefusedNamingTheState(NavigatorStatusTypes state)
    {
        var id = await ADeviceAsync();
        Navigator.NavStatus = state;

        var config = await Assert.ThrowsAsync<EngineBusyException>(() => PlantQueries.UpdateItemConfigurationAsync(
            new PlantItem { ItemId = id, Name = "Rinominata", DeviceType = (short)DeviceTypes.Automation }));
        var decoration = await Assert.ThrowsAsync<EngineBusyException>(() => PlantQueries.SetPlantDecorationAsync("<mxCell/>"));
        var junction = await Assert.ThrowsAsync<EngineBusyException>(() => PlantQueries.AddJunctionAsync(id, Guid.NewGuid(), 1));

        foreach (var refusal in new[] { config, decoration, junction })
            Assert.Contains($"il motore è in {state}", refusal.Message);
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        Assert.Equal("Navetta", (await PlantQueries.ItemConfigurationAsync(id))!.Name);
    }

    // ---- D1 ---------------------------------------------------------------------------------------

    [Fact]
    public async Task InPauseAConfigurationWriteThatLeavesTheConnectionAloneIsAccepted()
    {
        var id = await ADeviceAsync();
        Navigator.NavStatus = NavigatorStatusTypes.Pause;

        Assert.True(await PlantQueries.UpdateItemConfigurationAsync(new PlantItem
        {
            ItemId = id, Name = "Rinominata in pausa", DeviceType = (short)DeviceTypes.Automation,
            ConnectionType = (short)ConnectionTypes.Test, DeviceConnectionId = 0,
        }));

        Assert.Equal("Rinominata in pausa", (await PlantQueries.ItemConfigurationAsync(id))!.Name);
    }

    [Theory]
    [InlineData(nameof(PlantItem.ConnectionType))]
    [InlineData(nameof(PlantItem.DeviceConnectionId))]
    public async Task InPauseChangingADevicesConnectionIsRefused(string field)
    {
        var id = await ADeviceAsync();
        Navigator.NavStatus = NavigatorStatusTypes.Pause;
        var changed = new PlantItem
        {
            ItemId = id, Name = "Navetta", DeviceType = (short)DeviceTypes.Automation,
            ConnectionType = (short)(field == nameof(PlantItem.ConnectionType) ? ConnectionTypes.Connected : ConnectionTypes.Test),
            DeviceConnectionId = field == nameof(PlantItem.DeviceConnectionId) ? 7 : 0,
        };

        var refusal = await Assert.ThrowsAsync<EngineBusyException>(() => PlantQueries.UpdateItemConfigurationAsync(changed));

        Assert.Contains("il motore è in Pause", refusal.Message);
        Assert.Contains("solo a motore fermo (Stop)", refusal.Message);
        var stored = (await PlantQueries.ItemConfigurationAsync(id))!;
        Assert.Equal(((short)ConnectionTypes.Test, 0m), (stored.ConnectionType, stored.DeviceConnectionId));
    }

    [Fact]
    public async Task InStopChangingADevicesConnectionIsAccepted()
    {
        var id = await ADeviceAsync();

        Assert.True(await PlantQueries.UpdateItemConfigurationAsync(new PlantItem
        {
            ItemId = id, Name = "Navetta", DeviceType = (short)DeviceTypes.Automation,
            ConnectionType = (short)ConnectionTypes.Connected, DeviceConnectionId = 7,
        }));

        var stored = (await PlantQueries.ItemConfigurationAsync(id))!;
        Assert.Equal(((short)ConnectionTypes.Connected, 7m), (stored.ConnectionType, stored.DeviceConnectionId));
    }

    [Theory]
    [InlineData(NavigatorStatusTypes.Pause)]
    [InlineData(NavigatorStatusTypes.Recover)]
    [InlineData(NavigatorStatusTypes.Dispose)]
    public async Task TheConnectionModeOfThePlantIsChangedOnlyInStop(NavigatorStatusTypes state)
    {
        var id = await ADeviceAsync();
        Navigator.NavStatus = state;

        var refusal = await Assert.ThrowsAsync<EngineBusyException>(() => PlantQueries.SetConnectionTypeForAllAsync(ConnectionTypes.Connected));

        Assert.Contains($"il motore è in {state}", refusal.Message);
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        Assert.Equal((short)ConnectionTypes.Test, (await PlantQueries.ItemConfigurationAsync(id))!.ConnectionType);
        await PlantQueries.SetConnectionTypeForAllAsync(ConnectionTypes.Connected);
        Assert.Equal((short)ConnectionTypes.Connected, (await PlantQueries.ItemConfigurationAsync(id))!.ConnectionType);
    }

    // ---- La Pause per importazione, creazione e duplicazione (decisione del 27/9) ----------------
    // Un'importazione ricostruisce i dispositivi in memoria (PublishGraphMemory) e una creazione ne fa
    // nascere uno (WriteItemConfigurationAsync) SENZA connessione; la ripresa da Pause non apre le
    // connessioni (StartNavigator), e i comandi di un dispositivo passano da DeviceConnection?.: dopo
    // la ripresa non arriverebbero a niente. Quindi solo in Stop. La duplicazione della pagina e' una
    // creazione (POST /plant/items).

    [Theory]
    [InlineData(NavigatorStatusTypes.Pause)]
    [InlineData(NavigatorStatusTypes.Recover)]
    [InlineData(NavigatorStatusTypes.Dispose)]
    public async Task ADeviceIsCreatedOnlyWithTheEngineStopped(NavigatorStatusTypes state)
    {
        Navigator.NavStatus = state;
        var id = Guid.NewGuid();

        var refusal = await Assert.ThrowsAsync<EngineBusyException>(() => PlantQueries.CreateItemConfigurationAsync(
            new PlantItem { ItemId = id, Name = "Nuova", DeviceType = (short)DeviceTypes.Automation }));

        Assert.Contains($"il motore è in {state}", refusal.Message);
        Assert.Contains("solo a motore fermo (Stop)", refusal.Message);
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        Assert.Null(await PlantQueries.ItemConfigurationAsync(id));
        Assert.True(await PlantQueries.CreateItemConfigurationAsync(new PlantItem { ItemId = id, Name = "Nuova" }));
    }

    /// <summary>
    /// La porta di servizio (UpsertItemConfigurationAsync, per la semina delle prove) crea un dispositivo
    /// che non c'e': anche lei solo in Stop, come la creazione; su un dispositivo che c'e' resta la regola
    /// delle altre scritture (Pause ammessa).
    /// </summary>
    [Fact]
    public async Task TheServiceUpsertCreatesADeviceOnlyWithTheEngineStopped()
    {
        var existing = await ADeviceAsync();
        Navigator.NavStatus = NavigatorStatusTypes.Pause;
        var id = Guid.NewGuid();

        var refusal = await Assert.ThrowsAsync<EngineBusyException>(() => PlantQueries.UpsertItemConfigurationAsync(
            new PlantItem { ItemId = id, Name = "Nuova" }));
        await PlantQueries.UpsertItemConfigurationAsync(new PlantItem
        {
            ItemId = existing, Name = "Rinominata in pausa", DeviceType = (short)DeviceTypes.Automation,
            ConnectionType = (short)ConnectionTypes.Test, DeviceConnectionId = 0,
        });

        Assert.Contains("il motore è in Pause", refusal.Message);
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        Assert.Null(await PlantQueries.ItemConfigurationAsync(id));
        Assert.Equal("Rinominata in pausa", (await PlantQueries.ItemConfigurationAsync(existing))!.Name);
    }

    [Theory]
    [InlineData(NavigatorStatusTypes.Pause, false)]
    [InlineData(NavigatorStatusTypes.Pause, true)]
    [InlineData(NavigatorStatusTypes.Recover, false)]
    [InlineData(NavigatorStatusTypes.Dispose, false)]
    public async Task APlantIsImportedOnlyWithTheEngineStopped(NavigatorStatusTypes state, bool withTheHostsOwnPlcConnections)
    {
        var id = await ADeviceAsync();
        Navigator.NavStatus = state;
        IReadOnlyList<PlcConnection>? plc = withTheHostsOwnPlcConnections ? await PlcSettingsFacade.ConnectionsAsync() : null;

        var refusal = await Assert.ThrowsAsync<EngineBusyException>(() => PlantQueries.ReplaceGraphAsync(
            [new PlantItem { ItemId = Guid.NewGuid(), Name = "Importata" }], [], [], [], plcConnections: plc));

        Assert.Contains($"il motore è in {state}", refusal.Message);
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        Assert.Equal(id, Assert.Single(await PlantQueries.ItemConfigurationsAsync()).ItemId);
    }

    // ---- D2b --------------------------------------------------------------------------------------

    /// <summary>
    /// Una scrittura che arriva mentre un ricaricamento tiene il cancello aspetta, e poi passa: la
    /// sonda non entra finche' il ricaricamento non finisce, la decorazione scritta nel frattempo
    /// arriva sullo store dopo.
    /// </summary>
    [Fact]
    public async Task AWriteDuringAReloadUnderTheGateWaitsAndThenApplies()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reload = PlantQueries.ReloadUnderGateAsync(async () =>
        {
            inside.SetResult();
            await release.Task;
        });
        await inside.Task;

        var entered = false;
        var probe = PlantQueries.MutatePlantAsync(() => { entered = true; return Task.CompletedTask; });
        var enteredAtOnce = entered;
        var write = PlantQueries.SetPlantDecorationAsync("<mxCell id=\"scritta-durante-il-ricaricamento\"/>");

        Assert.False(enteredAtOnce);
        Assert.False(write.IsCompleted);

        release.SetResult();
        await reload;
        await probe;
        await write;
        Assert.True(entered);
        Assert.Equal("<mxCell id=\"scritta-durante-il-ricaricamento\"/>", await PlantQueries.PlantDecorationAsync());
    }

    /// <summary>
    /// Il comando di ricaricamento (1003) carica l'impianto sotto il cancello: nel momento in cui
    /// <c>LoadNavigatorData</c> parte, una mutazione non entra. Il caricamento vero non serve, e
    /// lo si ferma li' - il comando si chiude in errore, e il cancello si libera.
    /// </summary>
    [Fact]
    public async Task TheReloadCommandLoadsThePlantUnderTheGate()
    {
        bool? enteredAtOnce = null;
        Task? probe = null;
        var entered = false;
        Navigator.LoadStartingForTesting = () =>
        {
            probe = PlantQueries.MutatePlantAsync(() => { entered = true; return Task.CompletedTask; });
            enteredAtOnce = entered;
            throw new InvalidOperationException("caricamento fermato dalla prova");
        };
        var reload = new Command(loadingFromDatabase: true)
        {
            Date = DateTime.Now, CommandType = Command.CommandTypes.ReloadDiagram, Status = Command.StatusTypes.ToDo,
        };
        await ORM.CommandToDo.Update(reload);

        await CommandToDo.Instance.Execute().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(enteredAtOnce);
        await probe!.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(entered);
        Assert.Equal(Command.StatusTypes.Error, Assert.Single(CommandToDo.Instance.List, c => c.IdCommandToDo == reload.IdCommandToDo).Status);
    }
}

/// <summary>
/// D2b, l'altra strada: l'avvio del motore (<see cref="Navigator.StartAsync"/>, che l'host chiama
/// all'avvio e a ogni ritentativo del caricamento) carica l'impianto sotto il cancello delle
/// scritture. Come <c>GeometryMigrationFailureDoesNotBlockStartupTest</c>, i database si aprono in
/// una cartella temporanea e si chiudono alla fine; il caricamento vero non serve, e lo si ferma al
/// suo primo passo.
/// </summary>
public sealed class EngineStartLoadsUnderTheGateTest : IDisposable
{
    private readonly string _previousDirectory = Navigator.DatabaseDirectory;
    private readonly string? _previousLoadError = Navigator.LoadError;
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"wcs-start-gate-{Guid.NewGuid():N}");

    public EngineStartLoadsUnderTheGateTest()
    {
        Directory.CreateDirectory(_root);
        Navigator.DatabaseDirectory = _root;
    }

    public void Dispose()
    {
        Navigator.LoadStartingForTesting = null;
        Navigator.Dispose();
        Navigator.LoadError = _previousLoadError;
        Navigator.DatabaseDirectory = _previousDirectory;
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* file ancora aperto: pazienza */ }
    }

    [Fact]
    public async Task TheEngineStartLoadsThePlantUnderTheGate()
    {
        bool? enteredAtOnce = null;
        Task? probe = null;
        var entered = false;
        Navigator.LoadStartingForTesting = () =>
        {
            probe = PlantQueries.MutatePlantAsync(() => { entered = true; return Task.CompletedTask; });
            enteredAtOnce = entered;
            throw new InvalidOperationException("caricamento fermato dalla prova");
        };

        Assert.False(await Navigator.StartAsync());

        Assert.False(enteredAtOnce);
        await probe!.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(entered);
        Assert.Equal("caricamento fermato dalla prova", Navigator.LoadError);
    }
}
