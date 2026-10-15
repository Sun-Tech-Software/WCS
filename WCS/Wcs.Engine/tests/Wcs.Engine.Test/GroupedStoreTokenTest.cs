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
/// Il terzo punto con la stessa forma, e non e' del tutto la stessa:
/// <c>ConfigureStorageDestination</c>/<c>Source</c> leggono l'indirizzo di campo dopo che
/// <c>ExecuteGroupedMOVE_LU_TO</c> ha gia' scritto <c>Status = Busy</c> e <c>RunningStepId</c>.
/// </summary>
/// <remarks>
/// <para>
/// I due gettoni del guasto del 2026-09-09 qui non corrono lo stesso rischio: su questo percorso
/// non si prende nessuna prenotazione, e l'identificativo scritto in <c>RunningStepId</c> non e' lo
/// <c>StepId</c> di nessuno step ma un <c>GroupedMovementId</c> inventato sul momento, che la
/// normalizzazione del getter — «lo step non esiste piu'» — scioglie da sola.
/// </para>
/// <para>
/// Quello che restava addosso e' lo <b>stato</b>: <c>Busy</c> rende il dispositivo impegnato per
/// sempre e la guardia d'ingresso di <c>GroupExecuteStep</c> ammette solo <c>Ready</c> o
/// <c>Recovering</c>, quindi il gruppo non ripartiva. Il danno osservato e' lo stesso; il gettone
/// che lo produce, no.
/// </para>
/// </remarks>
public sealed class GroupedStoreTokenTest : IDisposable
{
    private const decimal CellId = 720;
    private const string StorageDoorCode = "P720A";

    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly RoutingTestPlant _plant = new();
    private readonly FakeWarehouse _warehouse = new();

    public GroupedStoreTokenTest()
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

    /// <summary>
    /// Un movimento raggruppato verso il magazzino, con la cella gia' occupata cosi' che la
    /// verifica di allocazione si chiuda senza chiedere altro al gestionale: l'unica lettura che
    /// resta e' quella dell'indirizzo di campo, dentro <c>ConfigureStorageDestination</c>.
    /// </summary>
    private async Task<(AutomationDevice Device, List<MissionStep> Steps)> AGroupedStoreAboutToStart()
    {
        var device = _plant.Device("G");
        var storage = _plant.Device("S", DeviceTypes.Storage);

        _plant.Link("G", "S");

        device.RecordStorageLength = RecordStorageTypes.FromPlc;

        await PlantPersistence.SaveDoorMappingsAsync(storage.ItemGuid,
            [new DoorMapping { ItemId = storage.ItemGuid, DoorCode = StorageDoorCode, CellId = CellId, Side = "A" }]);

        var mission = new PathMission
        {
            LuId = 42,
            StorageDoorCode = StorageDoorCode,
            DestinationItemId = storage.ItemGuid
        };

        ActiveMissions.Instance.Add(mission, buildRoute: false);
        Assert.NotNull(ActiveMissions.Instance[mission.MissionId]);

        var step = new MissionStep
        {
            StepId = Guid.NewGuid(),
            MissionId = mission.MissionId,
            Date = DateTime.Now,
            FromNodeId = device.ItemGuid,
            ToNodeId = storage.ItemGuid
        };
        MissionSteps.Instance.Add(step);

        _warehouse.Cells[CellId] = Cell(CellId, StorageDoorCode);

        // La cella non e' vuota: ShouldVerifyAllocation si ferma alla prima condizione vera e non
        // arriva a leggere la descrizione della cella, che e' la lettura che il test fa fallire.
        _warehouse.CellContents[CellId] = [7];

        return (device, [step]);
    }

    /// <summary>
    /// La lettura fallisce nel mezzo del movimento raggruppato e il dispositivo non resta
    /// <c>Busy</c>: se ci restasse, la guardia d'ingresso del gruppo non lo riammetterebbe mai.
    /// </summary>
    [Fact]
    public async Task AFailedReadDoesNotLeaveTheGroupBusy()
    {
        var (device, steps) = await AGroupedStoreAboutToStart();

        _warehouse.UnavailableOnCells.Add(CellId);

        Assert.NotNull(device.StepExecutor);
        await Assert.ThrowsAsync<WarehouseUnavailableException>(
            () => device.StepExecutor.ExecuteGroupedMOVE_LU_TO(steps));

        Assert.Equal(DeviceStatusTypes.Ready, device.Status);
        Assert.Equal(Guid.Empty, device.RunningStepId);
        Assert.False(device.IsBusyWithCommand);
    }
}
