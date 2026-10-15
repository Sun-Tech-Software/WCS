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
/// Compito 4: «quest'area va deframmentata per questo tipo di unita' di carico?». La domanda la
/// pone <c>AutomationDeviceGroupManager.VerifyCellToDefrag</c> quando una unita' di carico del
/// gruppo e' appena finita in cella, e la risposta decide una cosa sola:
/// <c>device.IdCellToDefragment</c>, cioe' se quella cella diventa candidata alla compattazione.
/// </summary>
/// <remarks>
/// <para>
/// Prima la domanda era <c>DbInfo.IsCellToDefragmentByLuType</c>: tre sottoquery in T-SQL diretto su
/// <c>CELLE_AREE</c>, <c>POSIZIONI_UDC</c> e <c>AREE_MAGAZZINO</c> - tabelle del gestionale - e le
/// due soglie (85 % e 10 %) scritte in uno <c>switch</c> dentro la funzione di lettura del motore.
/// Ora il gestionale risponde si' o no, e la politica resta sua.
/// </para>
/// <para>
/// <b>Cambia il modo di rompersi, ed e' voluto.</b> <c>IsCellToDefragmentByLuType</c> inghiottiva
/// l'errore della lettura, lo scriveva nel registro e rispondeva falso, cioe' «l'area e' a posto»:
/// la compattazione non partiva e nessuno lo sapeva. E senza database
/// (<c>Navigator.Database</c> nullo, com'era in ogni prova del motore) non era nemmeno un falso, era un
/// <c>NullReferenceException</c> - il rosso di queste prove lo mostra. Adesso un gestionale che non
/// risponde solleva <see cref="WarehouseUnavailableException"/>, che si ferma nel <c>catch</c> per
/// giro di <c>StepDoneGroup</c>: il completamento del gruppo si interrompe a meta', esattamente
/// come per ogni altra lettura di quel metodo (vedi
/// <see cref="GroupedMoveReservationTest.AFailureHalfwayThroughTheGroupCompletionStillGivesEveryReservationBack"/>,
/// che descrive la stessa forma e la rete che la copre).
/// </para>
/// </remarks>
public sealed class AreaDefragmentationVerdictTest : IDisposable
{
    private const decimal CellId = 780;
    private const string StorageDoorCode = "P780A";
    private const decimal AreaId = 12;
    private const decimal LoadUnitTypeId = 7;

    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly DefragmentationTypes _previousDefragmentation = Navigator.Defragmentation;
    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly RoutingTestPlant _plant = new();
    private readonly FakeWarehouse _warehouse = new();

    public AreaDefragmentationVerdictTest()
    {
        // La compattazione accesa prima di collegare il deposito: il setter la scrive nel database
        // del WCS da un gestore async void, e qui quella scrittura non c'entra con la prova.
        Navigator.Defragmentation = DefragmentationTypes.Enabled;

        Navigator.Store = _database;
        Navigator.Warehouse = _warehouse;
    }

    public void Dispose()
    {
        _plant.Dispose();
        Navigator.Warehouse = _previousWarehouse;
        Navigator.Store = null;
        Navigator.Defragmentation = _previousDefragmentation;
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// La scena: un gruppo che ha appena depositato in cella. Il mittente appartiene al gruppo e la
    /// destinazione e' il magazzino - e' quel verso che porta <c>StepDoneGroup</c> dentro
    /// <c>MoveGroupedLuToStorage</c>, e da li' in <c>VerifyCellToDefrag</c>. Il mittente registra a
    /// lunghezza fissa perche' e' uno dei due modi che chiedono il verdetto (l'altro converte la
    /// profondita' in partizione; quello dal PLC non lo chiede affatto).
    /// </summary>
    private async Task<(AutomationDevice Group, MissionStep ToStorage, Guid Token)> AGroupedStoreJustCompleted()
    {
        var group = _plant.Device("G");
        var storageSide = _plant.Device("A1");
        var storage = _plant.Device("S", DeviceTypes.Storage);

        group.GroupItemId = 1;
        group.EnableDefragmentation = true;
        storageSide.BelongingGroupId = 1;
        storageSide.RecordStorageLength = RecordStorageTypes.FixedLength;

        _plant.Link("A1", "S");

        await PlantPersistence.SaveDoorMappingsAsync(storage.ItemGuid,
            [new DoorMapping { ItemId = storage.ItemGuid, DoorCode = StorageDoorCode, CellId = CellId, Side = "A" }]);

        var storing = new PathMission { LuId = 42, StorageDoorCode = StorageDoorCode, DestinationItemId = storage.ItemGuid };
        ActiveMissions.Instance.Add(storing, buildRoute: false);

        var token = Guid.NewGuid();
        var toStorage = new MissionStep
        {
            StepId = Guid.NewGuid(),
            MissionId = storing.MissionId,
            GroupedMovementId = token,
            Date = DateTime.Now,
            FromNodeId = storageSide.ItemGuid,
            ToNodeId = storage.ItemGuid
        };
        MissionSteps.Instance.Add(toStorage);

        // La cella dice l'area e il tipo di unita' di carico ammesso: sono le due meta' della
        // chiave del verdetto, e il motore le prende da qui e non le inventa.
        _warehouse.Cells[CellId] = new CellDescription(CellId, AreaId, WarehouseId: 1, Depth: 1,
            Inverted: false, CellLockState.None, AllowedLoadUnitTypeId: LoadUnitTypeId, GroupId: null,
            GroupOrder: 0, StorageDoorCode, DoorCodeB: string.Empty, DoorCount: 1, MultiDepthStorage: false);

        return (group, toStorage, token);
    }

    /// <summary>
    /// Il gestionale dice di si': la cella appena riempita diventa quella da compattare. La chiave
    /// e' la coppia area piu' tipo, e il doppio risponde solo a quella - una domanda con l'area
    /// giusta e il tipo sbagliato riceverebbe «non serve».
    /// </summary>
    [Fact]
    public async Task AnAreaTheWarehouseSaysToDefragmentMarksTheCellJustFilled()
    {
        var (group, _, token) = await AGroupedStoreJustCompleted();

        _warehouse.DefragmentationVerdicts[(AreaId, LoadUnitTypeId)] = true;

        Assert.NotNull(group.GroupManager);
        await group.GroupManager.StepDoneGroup(token, StepTypes.Automatic);

        Assert.Contains(nameof(IWarehouse.GetAreaDefragmentationVerdictAsync), _warehouse.Calls);
        Assert.Equal(CellId, group.IdCellToDefragment);
    }

    /// <summary>
    /// La meta' che deve riuscire al contrario, senza la quale una verifica che rispondesse sempre
    /// «da compattare» passerebbe quella qui sopra: il gestionale dice di no e non resta nessuna
    /// cella da compattare. Il verdetto e' comunque stato <b>chiesto</b> - e' la differenza fra
    /// questo e un motore che non lo domanda affatto - e il completamento del gruppo arriva in
    /// fondo, cioe' toglie lo step.
    /// </summary>
    [Fact]
    public async Task AnAreaTheWarehouseSaysIsTidyLeavesNoCellToDefragment()
    {
        var (group, toStorage, token) = await AGroupedStoreJustCompleted();

        _warehouse.DefragmentationVerdicts[(AreaId, LoadUnitTypeId)] = false;

        Assert.NotNull(group.GroupManager);
        await group.GroupManager.StepDoneGroup(token, StepTypes.Automatic);

        Assert.Contains(nameof(IWarehouse.GetAreaDefragmentationVerdictAsync), _warehouse.Calls);
        Assert.Equal(0m, group.IdCellToDefragment);
        Assert.DoesNotContain(toStorage, MissionSteps.Instance.List);
    }

    /// <summary>
    /// Il gestionale non risponde <b>a questa domanda</b>: solleva, e non finge un «l'area e' a
    /// posto». Si vede dal confronto con la prova qui sopra - li' il completamento del gruppo
    /// arrivava in fondo e toglieva lo step, qui si ferma nel <c>catch</c> per giro e lo step
    /// resta, che e' il modo in cui un guasto del confine si manifesta su questo percorso.
    /// <para>
    /// Serve <see cref="FakeWarehouse.UnavailableOperations"/> e non
    /// <see cref="FakeWarehouse.Unavailable"/>: con quello avrebbe sollevato la
    /// <c>DescribeCellAsync</c> di qualche riga prima - la lettura che porta area e tipo - e la
    /// prova non avrebbe detto niente su questa domanda.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AVerdictTheWarehouseCannotGiveRaisesInsteadOfAnsweringTidy()
    {
        var (group, toStorage, token) = await AGroupedStoreJustCompleted();

        _warehouse.UnavailableOperations.Add(nameof(IWarehouse.GetAreaDefragmentationVerdictAsync));

        Assert.NotNull(group.GroupManager);
        await group.GroupManager.StepDoneGroup(token, StepTypes.Automatic);

        Assert.Contains(nameof(IWarehouse.GetAreaDefragmentationVerdictAsync), _warehouse.Calls);
        Assert.Contains(toStorage, MissionSteps.Instance.List);
    }

    /// <summary>
    /// Con la compattazione spenta la domanda <b>non si fa</b>: le guardie a buon mercato -
    /// lo stato della compattazione, l'interruttore del dispositivo, il tipo di missione, i tre
    /// identificativi - stanno prima della chiamata al confine e la risparmiano, come faceva il
    /// cortocircuito dell'<c>&amp;&amp;</c> di prima. Non e' un dettaglio: <c>StepDoneGroup</c> gira
    /// a ogni step di gruppo completato.
    /// </summary>
    [Fact]
    public async Task WithDefragmentationOffTheWarehouseIsNotAskedAtAll()
    {
        var (group, _, token) = await AGroupedStoreJustCompleted();

        Navigator.Defragmentation = DefragmentationTypes.Disabled;
        _warehouse.DefragmentationVerdicts[(AreaId, LoadUnitTypeId)] = true;

        Assert.NotNull(group.GroupManager);
        await group.GroupManager.StepDoneGroup(token, StepTypes.Automatic);

        Assert.DoesNotContain(nameof(IWarehouse.GetAreaDefragmentationVerdictAsync), _warehouse.Calls);
        Assert.Equal(0m, group.IdCellToDefragment);
    }
}
