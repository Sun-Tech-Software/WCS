using System.Reflection;
using Wcs.Engine.Collections;
using Wcs.Engine.ORM;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Wcs.Wms.Contracts;
using Wcs.Wms.Contracts.Test;
using Xunit;
using AutomationDevice = Wcs.Engine.Items.AutomationDevice;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// Le risposte 2 e 3 del proprietario del 9/10 attraverso un riavvio dell'host: le celle che il PLC ha
/// rifiutato a una missione tornano con la riga della missione, e la ricerca della missione ricaricata
/// le esclude ancora; le celle da segnare piene dopo un 67 con il gestionale muto tornano con la loro
/// tabella, e il giro del freno le ritenta.
/// </summary>
/// <remarks>
/// La ricarica passa da <c>DbInfo.RecoverFailure</c> in Recover, come la fa il motore e come in
/// <see cref="ChangeOfCellReportsOnReloadTest"/>: e' il punto che si prova. Il resto gira in Stop, e il
/// giro delle rotte in Run, dove soltanto gira. Il dispositivo D non e' collegato a un magazzino, cosi'
/// la ricarica non arriva alla ripresa della coda di prelievo.
/// </remarks>
public sealed class RefusedCellsOnReloadTest : IDisposable
{
    private const decimal LoadUnitId = 6300m;

    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly FakeWarehouse _warehouse = new();
    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly QueuedDatabaseWriter<WcsWrite>? _previousWriter = Navigator.StateWriter;
    private readonly NavigatorStatusTypes _previousNavStatus = Navigator.NavStatus;
    private readonly RetryBackoff _previousRetries = CellsToMarkFull.Retries;
    private readonly List<AutomationDevice> _devices = [];
    private readonly List<Guid> _missions = [];

    public RefusedCellsOnReloadTest()
    {
        Navigator.Store = _database;
        Navigator.StateWriter = null;
        Navigator.Warehouse = _warehouse;
        ReservationsOfMissionsClosedOnReload.Forget();
        CellsToMarkFull.Forget();
        RoutesToBuild.Instance.Items = new();
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
    }

    public void Dispose()
    {
        foreach (var missionId in _missions) ActiveMissions.Instance.RemoveAll(m => m.MissionId == missionId);

        foreach (var device in _devices)
        {
            PathDevices.Instance.Remove(device.ItemGuid);
            device.Dispose();
        }

        RoutesToBuild.Instance.Items = new();
        CellsToMarkFull.Forget();
        CellsToMarkFull.Retries = _previousRetries;
        ReservationsOfMissionsClosedOnReload.Forget();
        Navigator.NavStatus = _previousNavStatus;
        Navigator.StateWriter = _previousWriter;
        Navigator.Store = null;
        Navigator.Warehouse = _previousWarehouse;
        _database.Dispose();
    }

    /// <summary>
    /// La missione dell'UDC 6300, sul dispositivo D, aveva le celle 6 e 8 rifiutate dal PLC prima del
    /// riavvio, scritte nella sua riga. Ricaricata, le ha ancora, con il segno del deposito rifiutato; e
    /// la sua ricerca di una cella le esclude dalla domanda al gestionale. Prima stavano solo in memoria:
    /// la missione ricaricata non ne aveva, e la ricerca poteva ridarle la 6.
    /// </summary>
    [Fact]
    public async Task TheRefusedCellsComeBackWithTheMissionAndItsSearchExcludesThem()
    {
        var device = TheDeviceD();
        var missionId = await SaveAMissionOnAsync(device, refusedCells: "6,8", depositRefused: true);

        Navigator.NavStatus = NavigatorStatusTypes.Recover;
        await DbInfo.RecoverFailure();
        Navigator.NavStatus = NavigatorStatusTypes.Stop;

        var reloaded = ActiveMissions.Instance.FirstOrDefault(m => m != null && m.MissionId == missionId);
        Assert.NotNull(reloaded);
        await reloaded.PositionWork.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal([6m, 8m], reloaded.CellsRefusedByThePlc);
        Assert.True(reloaded.DepositRefusedByThePlc);

        await reloaded.SearchTheStoreAsync(Items.PathMission.StoringSelectionTypes.StoreInAnyAvailableCell, keepExistingReservation: false)
            .WaitAsync(TimeSpan.FromSeconds(30));

        var request = Assert.Single(_warehouse.StorageRequests);
        Assert.Equal(missionId, request.MissionId);
        Assert.Contains(6m, request.ExcludedCellIds);
        Assert.Contains(8m, request.ExcludedCellIds);
    }

    /// <summary>
    /// Le celle rifiutate si scrivono nella riga della missione nel momento in cui il PLC le rifiuta, non
    /// alla prossima scrittura della missione per un altro motivo: un riavvio subito dopo il rifiuto le
    /// ritrova. Il segno del deposito rifiutato con loro.
    /// </summary>
    [Fact]
    public void TheRefusedCellsAreWrittenWhenThePlcRefusesThem()
    {
        var mission = new Items.PathMission(Guid.NewGuid())
        {
            LuId = LoadUnitId,
            MissionType = MissionTypes.MovingToItem,
            OperationType = OperationTypes.Inbound,
            ActiveFilter = true
        };
        Assert.Null(TheRow(mission.MissionId)?.CellsRefusedByThePlc);

        mission.RememberCellsRefusedByThePlc([6m, 8m]);

        var row = TheRow(mission.MissionId);
        Assert.NotNull(row);
        Assert.Equal("6,8", row.CellsRefusedByThePlc);
        Assert.True(row.DepositRefusedByThePlc);
    }

    /// <summary>
    /// La porta A6 aspettava il segno di cella piena quando l'host si e' fermato: la riga c'e' nel
    /// database del WCS, la memoria no. La ricarica la rimette nell'elenco senza chiamare il gestionale;
    /// in Run il primo giro delle rotte la ritenta, il gestionale risponde, e la cella esce dall'elenco
    /// e dal database. Prima l'elenco non c'era: il segno mancato non si ritentava mai.
    /// </summary>
    [Fact]
    public async Task ACellToMarkFullComesBackWithTheReloadAndIsTriedAgainAtRun()
    {
        TheDeviceD();
        CellsToMarkFull.Retries = new RetryBackoff(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(5), new ManualClock());
        await using (var context = _database.CreateContext())
        {
            context.CellsToMarkFull.Add(new CellToMarkFull { DoorCode = "A6" });
            await context.SaveChangesAsync();
        }

        Navigator.NavStatus = NavigatorStatusTypes.Recover;
        await DbInfo.RecoverFailure();

        Assert.Equal(["A6"], CellsToMarkFull.Doors);
        Assert.Empty(_warehouse.Calls);

        Navigator.NavStatus = NavigatorStatusTypes.Run;
        var method = typeof(Navigator).GetMethod("BuildRoutePath", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        await ((Task)method!.Invoke(null, null)!).WaitAsync(TimeSpan.FromSeconds(30));
        await CellsToMarkFull.Retried.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal([("A6", CellLockState.Full)], _warehouse.CellLockStateChanges);
        Assert.Empty(CellsToMarkFull.Doors);
        await using var reading = _database.CreateContext();
        Assert.Empty(reading.CellsToMarkFull);
    }

    /// <summary>
    /// Il dispositivo D, con la cella collegata 5 del magazzino 1 del gestionale: la ricerca della
    /// missione che sta su di lui chiede al gestionale una cella del magazzino 1.
    /// </summary>
    private AutomationDevice TheDeviceD()
    {
        var device = TestDevices.WithRow(Guid.NewGuid(), d =>
        {
            d.DeviceType = DeviceTypes.CircularDistributor;
            d.LinkedCellId = 5;
        });
        PathDevices.Instance.Add(device);
        _devices.Add(device);

        _warehouse.Cells[5] = new CellDescription(5, AreaId: 1, WarehouseId: 1, Depth: 1, Inverted: false, CellLockState.None,
            AllowedLoadUnitTypeId: null, GroupId: null, GroupOrder: 0, "D5", DoorCodeB: string.Empty, DoorCount: 1,
            MultiDepthStorage: false);
        _warehouse.StorageReservations[LoadUnitId] = new StorageReservation(Guid.Empty, StorageReservationOutcome.NoFreeCells,
            0, string.Empty, string.Empty, 0, NeedsCompaction: false);
        return device;
    }

    private Mission? TheRow(Guid missionId)
    {
        using var context = _database.CreateContext();
        return context.Missions.SingleOrDefault(row => row.MissionId == missionId);
    }

    /// <summary>Una missione dell'UDC 6300 su <paramref name="device"/>, con le celle rifiutate scritte nella riga.</summary>
    private async Task<Guid> SaveAMissionOnAsync(AutomationDevice device, string refusedCells, bool depositRefused)
    {
        var missionId = Guid.NewGuid();
        _missions.Add(missionId);

        await using var context = _database.CreateContext();
        context.Missions.Add(new Mission
        {
            MissionId = missionId,
            LoadUnitId = LoadUnitId,
            PositionItemId = device.ItemGuid,
            Date = DateTime.Now,
            MissionType = (short)MissionTypes.MovingToItem,
            OperationType = (short)OperationTypes.Inbound,
            ActiveFilter = true,
            CellsRefusedByThePlc = refusedCells,
            DepositRefusedByThePlc = depositRefused
        });
        await context.SaveChangesAsync();

        return missionId;
    }
}
