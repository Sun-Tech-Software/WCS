using Log.Interface;
using Wcs.Engine.Collections;
using Wcs.Engine.Items;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Wcs.Engine.Test.Routing;
using Wcs.Wms.Contracts;
using Wcs.Wms.Contracts.Test;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// Il seguito del cambio di posizione della missione: un gestionale che non risponde deve lasciare
/// una riga.
/// </summary>
/// <remarks>
/// Il setter di <c>PathMission.PositionItemId</c> lancia un <c>Task.Run</c> con dentro tre
/// operazioni del confine — <c>ReportMissionServedAssignmentAsync</c>,
/// <c>ConfirmServedAssignmentsAsync</c> e <c>AllocateLoadUnitAsync</c> — e il suo <c>catch</c> stava
/// <b>fuori</b>: <c>Task.Run</c> ritorna subito, quindi copriva solo la parte sincrona. Da li' in
/// poi l'eccezione viveva in un <see cref="Task"/> che nessuno osserva, e in .NET moderno viene
/// scartata in silenzio alla raccolta. <c>AllocateLoadUnitAsync</c> e' la registrazione del deposito
/// fisico: se falliva, la missione proseguiva come se l'unita' di carico fosse stata riposta e non
/// lo sapeva nessuno — ne' un registro, ne' uno stato di errore.
/// </remarks>
public sealed class MissionPositionFailureTest : IDisposable
{
    private const decimal CellId = 610;
    private const string StorageDoor = "S01A";

    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly FakeWarehouse _warehouse = new();
    private readonly RoutingTestPlant _plant = new(NavigatorStatusTypes.Run);

    public MissionPositionFailureTest()
    {
        Navigator.Store = _database;
        Navigator.Warehouse = _warehouse;
    }

    public void Dispose()
    {
        _plant.Dispose();
        Navigator.Warehouse = _previousWarehouse;
        Navigator.Store = null;
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// L'unita' di carico arriva a magazzino e il gestionale non risponde: il seguito del cambio di
    /// posizione lascia una riga di errore invece di sparire.
    /// </summary>
    [Fact]
    public async Task AWarehouseThatDoesNotAnswerOnTheStoreLeavesALine()
    {
        using var log = new RecordingLogWriter();
        var (mission, storage) = AMissionArrivingAtTheStorage();
        _warehouse.Unavailable = true;

        mission.PositionItemId = storage.ItemGuid;

        await log.Wait((description, level) =>
                level == LogEntryType.Error && description.Contains(mission.MissionId.ToString()))
            .WaitAsync(TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// La meta' che deve riuscire: senza questa, un seguito che sollevasse sempre passerebbe il
    /// collaudo qui sopra. Con il gestionale che risponde, il deposito viene registrato e nessuna
    /// riga di errore nomina la missione.
    /// </summary>
    [Fact]
    public async Task AWarehouseThatAnswersRegistersTheStoreWithoutAnyErrorLine()
    {
        using var log = new RecordingLogWriter();
        var (mission, storage) = AMissionArrivingAtTheStorage();

        mission.PositionItemId = storage.ItemGuid;

        await WaitFor(() => _warehouse.AllocatedLoadUnits.Count > 0);

        Assert.DoesNotContain(log.Lines,
            line => line.Level == LogEntryType.Error && line.Description.Contains(mission.MissionId.ToString()));
    }

    private (PathMission Mission, AutomationDevice Storage) AMissionArrivingAtTheStorage()
    {
        var relocator = _plant.Device("R");
        var storage = _plant.Device("S", DeviceTypes.Storage);
        _plant.Link("R", "S");

        // La cella collegata e' cio' che fa passare AllocateOnItem, cioe' il punto in cui il
        // seguito registra il deposito fisico al gestionale.
        storage.LinkedCellId = CellId;

        PlantPersistence.SaveDoorMappingsAsync(storage.ItemGuid,
            [new DoorMapping { ItemId = storage.ItemGuid, DoorCode = StorageDoor, CellId = CellId, Side = "A" }])
            .GetAwaiter().GetResult();

        var mission = new PathMission
        {
            PositionItemId = relocator.ItemGuid,
            LuId = 4242,
            StorageDoorCode = StorageDoor
        };

        ActiveMissions.Instance.Add(mission, buildRoute: false);

        // L'aggiunta alla collezione attiva esce subito se il navigatore non e' in marcia: senza
        // questa verifica il resto proverebbe il contrario di cio' che dice il nome.
        Assert.Same(mission, relocator.LoadedLu);

        return (mission, storage);
    }

    /// <summary>
    /// Aspetta che una condizione diventi vera. Il tetto e' li' per far fallire invece di appendere
    /// il collaudo, non per discriminare: il seguito del cambio di posizione gira su un
    /// <see cref="Task"/> lanciato, e non c'e' un gettone che dica quando ha finito.
    /// </summary>
    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);

        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "la condizione non si e' avverata entro il tetto");
            await Task.Delay(5);
        }
    }
}
