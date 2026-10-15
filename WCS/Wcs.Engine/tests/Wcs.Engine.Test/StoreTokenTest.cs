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
/// La porta accanto a quella del prelievo: <c>StartStoreLu</c> aveva la stessa forma —
/// <c>RunningStepId</c> preso, poi la lettura dell'indirizzo di campo, e due uscite mute (la
/// lettura che risponde nulla e la lettura che solleva) che lasciavano il gettone addosso.
/// </summary>
/// <remarks>
/// Qui la destinazione dello step e' un magazzino, e i magazzini restano fuori dalla prenotazione
/// (voce <c>D73</c>): il gettone in gioco e' il solo <c>RunningStepId</c> — che e' anche quello che
/// blocca davvero, come ha mostrato il guasto del 2026-09-09.
/// </remarks>
public sealed class StoreTokenTest : IDisposable
{
    private const decimal CellId = 610;
    private const string StorageDoorCode = "P610A";

    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly RoutingTestPlant _plant = new();
    private readonly FakeWarehouse _warehouse = new();

    public StoreTokenTest()
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

    private static CellDescription Cell(decimal cellId, string doorCode) =>
        new(cellId, AreaId: 1, WarehouseId: 1, Depth: 1, Inverted: false, CellLockState.None,
            AllowedLoadUnitTypeId: null, GroupId: null, GroupOrder: 0, doorCode, DoorCodeB: string.Empty,
            DoorCount: 1, MultiDepthStorage: false);

    private static CellPartitionMap PartitionMap(decimal cellId, string asi, string subitem, string partition) =>
        new(cellId, asi, subitem, AsiDoorB: string.Empty, SubitemDoorB: string.Empty,
            [new CellPartition(partition, -1)]);

    /// <summary>
    /// Un dispositivo carico, con lo step di deposito verso il magazzino gia' pianificato.
    /// <c>RecordStorageLength</c> e' <c>FromPlc</c> perche' cosi' <c>VerifyStoreInfo</c> conferma
    /// senza interrogare nessuno: l'unica lettura verso il gestionale di questo giro e' quella
    /// dell'indirizzo di campo, che e' il punto in prova.
    /// </summary>
    private async Task<(AutomationDevice Device, PathMission Mission, MissionStep Step)> APlantAboutToStore()
    {
        var device = _plant.Device("A");
        var storage = _plant.Device("S", DeviceTypes.Storage);

        _plant.Link("A", "S");

        device.RecordStorageLength = RecordStorageTypes.FromPlc;

        await PlantPersistence.SaveDoorMappingsAsync(storage.ItemGuid,
            [new DoorMapping { ItemId = storage.ItemGuid, DoorCode = StorageDoorCode, CellId = CellId, Side = "A" }]);

        var mission = new PathMission
        {
            PositionItemId = device.ItemGuid,
            LuId = 42,
            StorageDoorCode = StorageDoorCode,
            DestinationItemId = storage.ItemGuid
        };

        ActiveMissions.Instance.Add(mission, buildRoute: false);

        // L'aggiunta alla collezione attiva esce subito se il motore e' in Stop o in Pause: senza
        // questa verifica il resto del test proverebbe il contrario di cio' che dice il nome.
        Assert.Same(mission, device.LoadedLu);

        var step = new MissionStep
        {
            StepId = Guid.NewGuid(),
            MissionId = mission.MissionId,
            Date = DateTime.Now,
            FromNodeId = device.ItemGuid,
            ToNodeId = storage.ItemGuid
        };
        MissionSteps.Instance.Add(step);

        Assert.Same(step, mission.StepToDo);

        _warehouse.Cells[CellId] = Cell(CellId, StorageDoorCode);
        _warehouse.CellPartitionMaps[CellId] = PartitionMap(CellId, "0001", "0002", "0003");

        return (device, mission, step);
    }

    /// <summary>
    /// La lettura dell'indirizzo di deposito fallisce e il dispositivo non resta impegnato.
    /// </summary>
    [Fact]
    public async Task AFailedReadLeavesNoTokenBehind()
    {
        var (device, _, _) = await APlantAboutToStore();

        _warehouse.UnavailableOnCells.Add(CellId);

        Assert.NotNull(device.StorageManager);
        await Assert.ThrowsAsync<WarehouseUnavailableException>(() => device.StorageManager.StartStoreLu());

        Assert.Equal(Guid.Empty, device.RunningStepId);
        Assert.False(device.IsBusyWithCommand);
    }

    /// <summary>
    /// L'altra uscita muta della stessa forma: la lettura risponde «non c'e'» invece di sollevare —
    /// e' quello che faceva la lettura locale prima che diventasse una chiamata al gestionale — e
    /// anche li' il gettone non deve restare addosso.
    /// </summary>
    [Fact]
    public async Task AnEmptyReadLeavesNoTokenBehind()
    {
        var (device, _, _) = await APlantAboutToStore();

        // Nessuna partizione all'offset cercato: GetAsiCellInfoAsync risponde nulla, senza sollevare.
        _warehouse.CellPartitionMaps[CellId] = new CellPartitionMap(CellId, "0001", "0002",
            AsiDoorB: string.Empty, SubitemDoorB: string.Empty, []);

        Assert.NotNull(device.StorageManager);
        await device.StorageManager.StartStoreLu();

        Assert.Equal(Guid.Empty, device.RunningStepId);
        Assert.False(device.IsBusyWithCommand);
    }

    /// <summary>
    /// L'altra meta' della regola: quando la lettura riesce il deposito parte come prima. Senza
    /// questo, un «non parte mai» passerebbe i due collaudi qui sopra.
    /// </summary>
    [Fact]
    public async Task ASuccessfulReadStillTakesTheToken()
    {
        var (device, _, step) = await APlantAboutToStore();

        Assert.NotNull(device.StorageManager);
        await device.StorageManager.StartStoreLu();

        Assert.Equal(step.StepId, device.RunningStepId);
        Assert.Equal(DeviceStatusTypes.Unloading, device.Status);
    }
}
