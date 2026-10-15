using System.Data;
using Helper.Converters;
using Log.Interface;
using Wcs.Engine.ORM;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Wcs.Engine.Test.Routing;
using Wcs.Wms.Contracts;
using Wcs.Wms.Contracts.Test;
using Xunit;
using AutomationDevice = Wcs.Engine.Items.AutomationDevice;
using AutomationDeviceGroupManager = Wcs.Engine.Items.Managers.AutomationDeviceGroupManager;
using Command = Wcs.Engine.Items.Command;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// Il legame fra la missione <b>di gruppo</b> e la lavorazione della nota che sta servendo
/// (<c>PathMission.NoteProcessingId</c>), preso dalla riga di coda che la genera.
/// <para>
/// Perche' serve un test proprio: la guardia di
/// <c>AutomationDeviceGroupManager.AddMissionToGroupPickUp</c> leggeva <c>ID_LAVORAZIONEN</c>, una
/// colonna che non esiste in nessuna tabella e che nessuna query restituisce.
/// <c>DataRowConverter.GetValue</c> non solleva su una colonna assente: risponde nullo. La guardia
/// era quindi sempre falsa e la riga sotto - quella con il nome giusto - non veniva mai eseguita:
/// <b>ogni</b> missione di gruppo nasceva con la lavorazione a zero. Il gemello per le missioni
/// singole (<c>AutomationDevice.AddMissionToPickUp</c>) legge la colonna giusta ed e' coperto da
/// <see cref="MissionCarriesItsNoteAssignmentTest"/>.
/// </para>
/// <para>
/// <b>Da dove passa il valore, tutto intero (compito 9).</b> La lavorazione arriva con
/// l'assegnazione pronta del gestionale (<see cref="PendingNote.ProcessingId"/>), entra in coda con
/// la riga attraverso <c>Navigator.BuildLuExtraction</c> e torna da <c>DbInfo.GetLuToExtract</c>,
/// che non sostituisce piu' niente: la <c>SELECT</c> diretta su SQL Server che rileggeva le
/// lavorazioni delle assegnazioni in coda (<c>DbInfo.ProcessingIdsByAssignment</c>) non c'e' piu', e
/// il test non passa piu' nessun ingresso da fuori. Il campo della missione non viene scritto a mano
/// da nessuna parte.
/// </para>
/// </summary>
public sealed class GroupMissionCarriesItsNoteProcessingTest : IDisposable
{
    private const decimal LoadUnitId = 8701m;
    private const decimal CellId = 8710m;
    private const decimal BufferCellId = 8720m;
    private const decimal AssignmentId = 701m;
    private const decimal NoteProcessingId = 7010m;
    private const string DoorCode = "A701";

    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly RoutingTestPlant _plant = new();
    private readonly FakeWarehouse _warehouse = new();

    public GroupMissionCarriesItsNoteProcessingTest()
    {
        Navigator.Store = _database;
        Navigator.Warehouse = _warehouse;

        // Come nel test gemello, di SQL Server non c'e' piu' niente da nessuna parte (compito 16):
        // la profondita' di deposito viene dal gestionale finto, e nessuna lettura di questo
        // collaudo arriva al database del gestionale. Dal compito 9 non ce ne sarebbe piu' nessuna
        // da sostituire: la lavorazione viaggia con l'assegnazione e resta in coda.
    }

    public void Dispose()
    {
        _plant.Dispose();
        Navigator.Warehouse = _previousWarehouse;
        Navigator.Store = null;
        _database.Dispose();
    }

    /// <summary>
    /// L'impianto minimo: la porta di prelievo sta su un magazzino, il suo ricollocatore e' il
    /// gruppo di dispositivi che prende in carico la coda, e la destinazione e' il dispositivo che
    /// serve il polmone.
    /// </summary>
    private async Task<AutomationDevice> APlantWithAQueuedAssignment(decimal processingId = NoteProcessingId)
    {
        var storage = _plant.Device("S", DeviceTypes.Storage);
        var group = _plant.Device("G", DeviceTypes.GroupOfAutomationDevices);
        var destination = _plant.Device("D");

        storage.RelocateItemId = group.ItemGuid;
        _plant.Link("G", "S");

        await PlantPersistence.SaveDoorMappingsAsync(storage.ItemGuid,
            [new DoorMapping { ItemId = storage.ItemGuid, DoorCode = DoorCode, CellId = CellId, Side = "A" }]);

        _warehouse.LoadUnitSearchResults[LoadUnitId] = new LoadUnitSearchResult(LoadUnitId, CellId);
        _warehouse.PickUpDoorChoices[LoadUnitId] = new PickUpDoorChoice(CellDoor.A, DoorCode, 0, []);
        _warehouse.LoadUnits[LoadUnitId] = new LoadUnitDescription(LoadUnitId, $"UDC{LoadUnitId}",
            LoadUnitTypeId: 1, HandlingMode: 0, Tare: 0, Height: 0, Width: 0, Depth: 0, Weight: 0);
        _warehouse.Cells[CellId] = new CellDescription(CellId, AreaId: 1, WarehouseId: 1, Depth: 1,
            Inverted: false, CellLockState.None, AllowedLoadUnitTypeId: null, GroupId: null, GroupOrder: 0,
            DoorCodeA: DoorCode, DoorCodeB: string.Empty, DoorCount: 1, MultiDepthStorage: false);

        await Navigator.BuildLuExtraction(
        [
            new PendingNote(AssignmentId, Guid.NewGuid(), LoadUnitId, BufferCellId, [1m], destination.ItemGuid, "WS1",
                processingId)
        ]);

        return group;
    }

    /// <summary>
    /// Le righe in coda per il gruppo, nella forma in cui le legge chi le consuma: la via vera per
    /// intero (<c>DbInfo.GetLuToExtract</c>), che dal compito 9 non chiede piu' niente a SQL Server.
    /// </summary>
    private static async Task<List<DataRow>> QueuedRows(AutomationDevice group)
    {
        var table = await DbInfo.GetLuToExtract(group);

        Assert.NotNull(table);

        return [.. table.Rows.Cast<DataRow>()];
    }

    private static Command AMoveCommand() => new()
    {
        Date = DateTime.Now,
        CommandType = Command.CommandTypes.MoveLuAutomatic,
        LuId = LoadUnitId,
        PickUpDoorCode = DoorCode,
        Status = Command.StatusTypes.Done,
        WorkstationId = "WS1",
        BufferCellId = BufferCellId
    };

    /// <summary>
    /// <b>Il test che conta</b>: la missione di gruppo nata da una riga che porta la lavorazione
    /// porta quella lavorazione. E' il campo su cui <c>ProcessGroupExtractionLu</c> decide se
    /// un'unita' di carico gia' in missione la stia servendo per <b>quella</b> lavorazione o per
    /// un'altra; con il refuso rispondeva zero a ogni confronto, quindi «un'altra» sempre.
    /// </summary>
    [Fact]
    public async Task AGroupMissionBornFromANoteAssignmentCarriesItsProcessing()
    {
        var group = await APlantWithAQueuedAssignment();
        var manager = new AutomationDeviceGroupManager(group);

        var row = Assert.Single(await QueuedRows(group));
        Assert.Equal(NoteProcessingId, row.GetValue<decimal>("ID_LAVORAZIONE"));

        await manager.AddMissionToGroupPickUp(AMoveCommand(), Guid.NewGuid(), row, MissionTypes.Extraction);

        var mission = Assert.Single(group.QueuedLUtoPickUp, m => m != null && m.LuId == LoadUnitId)!;

        Assert.Equal(NoteProcessingId, mission.NoteProcessingId);
        Assert.Equal(AssignmentId, mission.LuNoteAssignmentId);
    }

    /// <summary>
    /// Il contrario, perche' il legame non deve comparire dove non c'e': una riga di coda senza
    /// lavorazione - il ricollocamento di un ostacolo, o una riga scritta prima del compito 9 e
    /// rimasta in coda all'aggiornamento - lascia la colonna nulla, la guardia e' falsa davvero e la
    /// missione nasce con lo zero, invece di inventarsi una lavorazione. E' lo stesso zero di prima
    /// della correzione del refuso: e' per questo che il primo test e' quello che distingue, e
    /// questo da solo non proverebbe niente.
    /// </summary>
    /// <remarks>
    /// <b>Compito 13</b>: qui c'era anche l'attesa della riga di registro «la nota non verra'
    /// chiusa». Quell'avviso e' stato cancellato perche' non e' piu' vero: la lavorazione della nota
    /// la ricava il gestionale dall'assegnazione, quindi una riga con l'assegnazione e senza
    /// lavorazione si chiude lo stesso. Lo zero resta, perche' <c>NoteProcessingId</c> governa
    /// ancora due decisioni del motore - la missione di gruppo che non mescola lavorazioni diverse,
    /// e la scelta del polmone.
    /// </remarks>
    [Fact]
    public async Task AGroupMissionWithoutAProcessingKeepsItAtZero()
    {
        var group = await APlantWithAQueuedAssignment(processingId: 0m);
        var manager = new AutomationDeviceGroupManager(group);

        var row = Assert.Single(await QueuedRows(group));
        Assert.True(row.IsNull("ID_LAVORAZIONE"));

        await manager.AddMissionToGroupPickUp(AMoveCommand(), Guid.NewGuid(), row, MissionTypes.Extraction);

        var mission = Assert.Single(group.QueuedLUtoPickUp, m => m != null && m.LuId == LoadUnitId)!;

        Assert.Equal(0m, mission.NoteProcessingId);

        // L'assegnazione invece c'e', ed e' lei che il gestionale usa per chiudere la nota dal
        // compito 13: e' la ragione per cui questo zero non e' piu' una nota che resta aperta.
        Assert.Equal(AssignmentId, mission.LuNoteAssignmentId);
    }
}
