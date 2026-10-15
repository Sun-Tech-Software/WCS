using System.Reflection;
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
/// Voce D160: il giro di <c>BuildRoutePath</c> (ogni 10 ms, una missione per giro) attendeva la
/// ricerca della cella di deposito, cioe' una chiamata al gestionale con un tetto di 30 s: con il
/// WMS lento la costruzione delle rotte scendeva a due missioni al minuto per tutto l'impianto.
/// Ora la ricerca parte come lavoro osservato fuori dal giro, che torna subito; e un insieme
/// «in volo» per missione impedisce di lanciarne due per la stessa missione.
/// </summary>
/// <remarks>
/// Il gestionale finto si ferma dentro <c>ReserveStorageAsync</c> finche' il collaudo non lo
/// lascia rispondere (<see cref="FakeWarehouse.ReserveStorageGate"/>): il tempo non discrimina
/// niente, i tetti servono a far fallire invece di appendere il collaudo. La scena e' quella di
/// <see cref="StoreDestinationRaceTest"/> ridotta: un ricollocatore collegato a un magazzino, una
/// missione a bordo con un'unita' di carico, e il gestionale che risponde la cella del magazzino.
/// </remarks>
public sealed class CellSearchOutsideTheRouteLoopTest : IDisposable
{
    private const decimal LinkedCellId = 7;
    private const decimal FoundCellId = 8;
    private const string FoundDoorCode = "A8";
    private const decimal LoadUnitId = 42;

    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly RoutingTestPlant _plant = new();
    private readonly FakeWarehouse _warehouse = new();

    public CellSearchOutsideTheRouteLoopTest()
    {
        Navigator.Store = _database;
        Navigator.Warehouse = _warehouse;
    }

    public void Dispose()
    {
        RoutesToBuild.Instance.Items = new();
        _plant.Dispose();
        Navigator.Warehouse = _previousWarehouse;
        Navigator.Store = null;
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    private static CellDescription Cell(decimal cellId, string doorCode) =>
        new(cellId, AreaId: 1, WarehouseId: 1, Depth: 1, Inverted: false, CellLockState.None,
            AllowedLoadUnitTypeId: null, GroupId: null, GroupOrder: 0, doorCode, DoorCodeB: string.Empty,
            DoorCount: 1, MultiDepthStorage: false);

    /// <summary>Un giro del ciclo delle rotte, come lo fa il motore ogni 10 ms.</summary>
    private static Task OneTurnOfTheRouteLoop()
    {
        var method = typeof(Navigator).GetMethod("BuildRoutePath", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (Task)method!.Invoke(null, null)!;
    }

    /// <summary>
    /// Una missione a bordo del ricollocatore, in coda per la rotta, che il giro portera' alla
    /// ricerca della cella; il motore in marcia, come vuole il giro.
    /// </summary>
    private async Task<(PathMission Mission, AutomationDevice Storage)> AMissionWaitingForItsCell()
    {
        var relocator = _plant.Device("R");
        var storage = _plant.Device("S8", DeviceTypes.Storage);
        _plant.Link("R", "S8");

        storage.RelocateItemId = relocator.ItemGuid;
        relocator.LinkedCellId = LinkedCellId;
        relocator.LookWhereAllocateLu = LookWhereAllocateLuTypes.BuildRouteToStore;

        await PlantPersistence.SaveDoorMappingsAsync(storage.ItemGuid,
            [new DoorMapping { ItemId = storage.ItemGuid, DoorCode = FoundDoorCode, CellId = FoundCellId, Side = "A" }]);

        var mission = new PathMission { PositionItemId = relocator.ItemGuid, LuId = LoadUnitId };
        ActiveMissions.Instance.Add(mission, buildRoute: false);
        Assert.Same(mission, relocator.LoadedLu);

        _warehouse.Cells[LinkedCellId] = Cell(LinkedCellId, "A7");
        _warehouse.Cells[FoundCellId] = Cell(FoundCellId, FoundDoorCode);
        _warehouse.CellPartitionMaps[FoundCellId] = new CellPartitionMap(FoundCellId, "0011", "0012",
            AsiDoorB: string.Empty, SubitemDoorB: string.Empty, [new CellPartition("0013", 0)]);
        _warehouse.StorageReservations[LoadUnitId] = new StorageReservation(mission.MissionId,
            StorageReservationOutcome.CellFound, FoundCellId, FoundDoorCode, Partition: "0013", StorageDepth: 150,
            NeedsCompaction: false);

        // La coda delle rotte e' un singleton che altri collaudi riempiono dai percorsi del motore
        // (HandleNoStepToDo, AddLoadedLuToRouteToBuildQueue) senza svuotarla: un giro solo deve
        // pescare questa missione, non una lasciata li' da un altro.
        RoutesToBuild.Instance.Items = new();
        RoutesToBuild.Instance.Enqueue(mission);
        Navigator.NavStatus = NavigatorStatusTypes.Run;

        return (mission, storage);
    }

    /// <summary>
    /// Il giro torna mentre il gestionale sta ancora cercando; la ricerca finisce per conto suo e
    /// scrive la destinazione sulla missione.
    /// </summary>
    [Fact]
    public async Task TheRouteLoopReturnsWhileTheWarehouseIsStillSearching()
    {
        var (mission, storage) = await AMissionWaitingForItsCell();
        _warehouse.ReserveStorageGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var turn = OneTurnOfTheRouteLoop();

        // La ricerca e' arrivata al gestionale, che non risponde ancora: il giro deve essere gia'
        // tornato. Prima aspettava qui la risposta, fino a 30 s.
        await _warehouse.ReserveStorageAsked.WaitAsync(TimeSpan.FromSeconds(30));
        await turn.WaitAsync(TimeSpan.FromSeconds(10));

        var search = Navigator.CellSearchInFlight(mission.MissionId);
        Assert.NotNull(search);

        _warehouse.ReserveStorageGate.SetResult();
        await search.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Null(Navigator.CellSearchInFlight(mission.MissionId));
        Assert.Equal(storage.ItemGuid, mission.DestinationItemId);
        Assert.Null(RoutesToBuild.Instance[mission.MissionId]);
    }

    /// <summary>
    /// La stessa missione che torna in coda mentre la sua ricerca e' in volo non fa partire una
    /// seconda ricerca: il giro la rimette in coda e basta.
    /// </summary>
    [Fact]
    public async Task AMissionWhoseSearchIsInFlightIsNotSearchedTwice()
    {
        var (mission, _) = await AMissionWaitingForItsCell();
        _warehouse.ReserveStorageGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var firstTurn = OneTurnOfTheRouteLoop();
        await _warehouse.ReserveStorageAsked.WaitAsync(TimeSpan.FromSeconds(30));
        await firstTurn.WaitAsync(TimeSpan.FromSeconds(10));

        var search = Navigator.CellSearchInFlight(mission.MissionId);
        Assert.NotNull(search);

        // La missione torna in coda (una richiesta di rotta dall'Editor, per dire) e il giro la
        // ripesca mentre la prima ricerca e' ancora ferma nel gestionale.
        RoutesToBuild.Instance.Enqueue(mission);
        await OneTurnOfTheRouteLoop().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Same(search, Navigator.CellSearchInFlight(mission.MissionId));
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);

        _warehouse.ReserveStorageGate.SetResult();
        await search.WaitAsync(TimeSpan.FromSeconds(30));

        // Una sola ricerca e' arrivata al gestionale.
        Assert.Equal(1, _warehouse.Calls.Count(operation => operation == nameof(IWarehouse.ReserveStorageAsync)));
    }
}
