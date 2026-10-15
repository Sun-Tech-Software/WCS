using Wcs.Engine.Collections;
using Wcs.Engine.Items;
using Wcs.Engine.ORM;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Wcs.Wms.Contracts;
using Wcs.Wms.Contracts.Test;
using Xunit;
using static Wcs.Common.Enums;
using AutomationDevice = Wcs.Engine.Items.AutomationDevice;

namespace Wcs.Engine.Test;

/// <summary>
/// Voce D185: il recupero all'avvio e' un lavoro per missione. Un dato o un servizio guasto per
/// una riga non deve impedire alle altre righe dello stesso gruppo di tornare operative.
/// </summary>
public sealed class MissionRecoveryIsolationTest : IDisposable
{
    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly WcsDatabase? _previousStore = Navigator.Store;
    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly NavigatorStatusTypes _previousStatus = Navigator.NavStatus;
    private readonly List<AutomationDevice> _devices = [];
    private readonly List<Guid> _missionIds = [];

    public MissionRecoveryIsolationTest()
    {
        Navigator.Store = _database;

        // In Recover le modifiche ricostruite non vengono riscritte nel database mentre lo si sta
        // leggendo. ActiveMissions accetta comunque le missioni: esclude soltanto Stop e Pause.
        Navigator.NavStatus = NavigatorStatusTypes.Recover;
    }

    public void Dispose()
    {
        // Prima si aspetta il lavoro di sfondo delle missioni ricaricate. Il ricaricamento imposta
        // PositionItemId, e il setter lancia PositionWork in un Task.Run che alla fine riscrive la
        // missione: senza attenderlo, sotto carico quella scrittura arrivava nella prova dopo
        // (era la riga (Run, null) estranea di ScrittureDellaMissioneTest; coda, voci 42 e 46).
        foreach (var missionId in _missionIds)
            if (ActiveMissions.Instance[missionId] is { } reloaded)
                Assert.True(reloaded.PositionWork.Wait(TimeSpan.FromSeconds(30)), $"PositionWork di {missionId} non finito");

        foreach (var missionId in _missionIds)
            ActiveMissions.Instance.RemoveAll(mission => mission.MissionId == missionId);

        foreach (var device in _devices)
        {
            PathDevices.Instance.Remove(device.ItemGuid);
            device.Dispose();
        }

        Navigator.NavStatus = _previousStatus;
        Navigator.Store = _previousStore;
        Navigator.Warehouse = _previousWarehouse;
        _database.Dispose();
    }

    [Fact]
    public async Task AFailedPreparationDoesNotPreventTheNextMissionFromRecovering()
    {
        var warehouse = new FakeWarehouse();
        warehouse.UnavailableOperations.Add(nameof(IWarehouse.LocateLoadUnitAsync));
        Navigator.Warehouse = warehouse;

        var failedDevice = AddDevice(DeviceTypes.CircularDistributor);
        var healthyDevice = AddDevice(DeviceTypes.Buffer);
        var failedMissionId = Guid.NewGuid();
        var healthyMissionId = Guid.NewGuid();

        await SaveMissionAsync(failedMissionId, loadUnitId: 6511, failedDevice,
            MissionTypes.Extraction, OperationTypes.Outbound, activeFilter: true, storageDepth: 0);
        await SaveMissionAsync(healthyMissionId, loadUnitId: 6512, healthyDevice,
            MissionTypes.Extraction, OperationTypes.Outbound, activeFilter: true, storageDepth: 5);

        await DbInfo.RecoverFailure();

        Assert.Null(ActiveMissions.Instance[failedMissionId]);
        Assert.NotNull(ActiveMissions.Instance[healthyMissionId]);
        Assert.Contains(nameof(IWarehouse.LocateLoadUnitAsync), warehouse.Calls);
        await AssertMissionStillStoredAsync(failedMissionId);
    }

    [Fact]
    public async Task AFailedDeviceRestoreDoesNotPreventTheNextMissionFromRecovering()
    {
        var warehouse = new FakeWarehouse();
        warehouse.UnavailableOperations.Add(nameof(IWarehouse.GetPickingBufferAreasAsync));
        Navigator.Warehouse = warehouse;

        // Le righe sono lette in ordine di inserimento e ripristinate al contrario. La missione
        // sul magazzino viene quindi tentata per prima e fa fallire la lettura delle aree; quella
        // sana deve comunque arrivare a LoadLu, che su questo dispositivo attiva il filtro.
        var healthyDevice = AddDevice(DeviceTypes.Buffer);
        healthyDevice.OnLoadLuActiveFilter = true;
        var failedStorage = AddDevice(DeviceTypes.Storage);
        var healthyMissionId = Guid.NewGuid();
        var failedMissionId = Guid.NewGuid();

        await SaveMissionAsync(healthyMissionId, loadUnitId: 6521, healthyDevice,
            MissionTypes.MovingToItem, OperationTypes.Outbound, activeFilter: false);
        await SaveMissionAsync(failedMissionId, loadUnitId: 6522, failedStorage,
            MissionTypes.MovingToItem, OperationTypes.Outbound, activeFilter: false, bufferCellId: 51);

        await DbInfo.RecoverFailure();

        var recovered = ActiveMissions.Instance[healthyMissionId];
        Assert.NotNull(recovered);
        Assert.True(recovered.ActiveFilter);
        Assert.Contains(nameof(IWarehouse.GetPickingBufferAreasAsync), warehouse.Calls);
        await AssertMissionStillStoredAsync(failedMissionId);
    }

    private AutomationDevice AddDevice(DeviceTypes type)
    {
        var device = new AutomationDevice(Guid.NewGuid()) { DeviceType = type };
        PathDevices.Instance.Add(device);
        _devices.Add(device);
        return device;
    }

    private async Task SaveMissionAsync(Guid missionId, decimal loadUnitId, AutomationDevice position,
        MissionTypes missionType, OperationTypes operationType, bool activeFilter, int storageDepth = 0,
        decimal bufferCellId = 0)
    {
        _missionIds.Add(missionId);

        await using var context = _database.CreateContext();
        context.Missions.Add(new Mission
        {
            MissionId = missionId,
            LoadUnitId = loadUnitId,
            PositionItemId = position.ItemGuid,
            Date = DateTime.Now,
            MissionType = (short)missionType,
            OperationType = (short)operationType,
            ActiveFilter = activeFilter,
            StorageDepth = storageDepth,
            BufferCellId = bufferCellId
        });
        await context.SaveChangesAsync();
    }

    private async Task AssertMissionStillStoredAsync(Guid missionId)
    {
        await using var context = _database.CreateContext();
        Assert.Contains(context.Missions, mission => mission.MissionId == missionId);
    }
}
