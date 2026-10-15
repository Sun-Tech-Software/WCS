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
/// Il ciclo di vita della prenotazione di deposito (compito 17): la prenotazione appartiene alla
/// missione, resta valida mentre la missione aspetta, e a ogni tentativo successivo il motore chiede
/// se sia ancora valida invece di riprenotare.
/// <para>
/// <b>Non</b> e' la prenotazione omonima del motore (<see cref="IncomingStepReservation"/>, il
/// gettone con cui un dispositivo e' prenotato come destinazione di uno step): quella non ha niente
/// a che vedere con il gestionale.
/// </para>
/// </summary>
public sealed class StorageReservationLifecycleTest : IDisposable
{
    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly NavigatorStatusTypes _previousStatus = Navigator.NavStatus;
    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly List<Guid> _devices = [];

    public StorageReservationLifecycleTest()
    {
        Navigator.NavStatus = NavigatorStatusTypes.Recover;
        Navigator.Store = _database;
        ActiveMissions.Instance.RemoveAll(_ => true);
    }

    public void Dispose()
    {
        ActiveMissions.Instance.RemoveAll(_ => true);
        foreach (var device in _devices) PathDevices.Instance.Remove(device);
        Navigator.Store = null;
        _database.Dispose();
        Navigator.Warehouse = _previousWarehouse;
        Navigator.NavStatus = _previousStatus;
    }

    private AutomationDevice NewDevice()
    {
        var device = TestDevices.WithRow(Guid.NewGuid());
        PathDevices.Instance.Add(device);
        _devices.Add(device.ItemGuid);
        return device;
    }

    /// <summary>
    /// L'impianto minimo che i vincoli della richiesta nominano davvero: due celle di deposito, ognuna
    /// servita da un ricollocatore suo. La cella si lega al dispositivo dalla mappa porte del motore
    /// (<c>DbInfo.GetItemId(cellId)</c>), e il dispositivo al suo ricollocatore da
    /// <c>RelocateItemId</c>: e' la stessa catena con cui <c>SetSearchCellParameter</c> costruisce
    /// l'elenco dei dispositivi raggiungibili, quindi un tentativo che non nomina piu' un
    /// ricollocatore sta dicendo che le sue celle non sono piu' raggiungibili.
    /// </summary>
    private async Task<(Guid RelocatorOfCell7, Guid RelocatorOfCell8)> APlantWithTwoCellsEachOnItsOwnRelocator()
    {
        var relocator7 = NewDevice();
        var relocator8 = NewDevice();

        var storage7 = NewDevice();
        storage7.DeviceType = DeviceTypes.Storage;
        storage7.RelocateItemId = relocator7.ItemGuid;

        var storage8 = NewDevice();
        storage8.DeviceType = DeviceTypes.Storage;
        storage8.RelocateItemId = relocator8.ItemGuid;

        await PlantPersistence.SaveDoorMappingsAsync(storage7.ItemGuid,
            [new DoorMapping { ItemId = storage7.ItemGuid, DoorCode = "A7", CellId = 7, Side = "A" }]);
        await PlantPersistence.SaveDoorMappingsAsync(storage8.ItemGuid,
            [new DoorMapping { ItemId = storage8.ItemGuid, DoorCode = "A8", CellId = 8, Side = "A" }]);

        return (relocator7.ItemGuid, relocator8.ItemGuid);
    }

    private static StorageReservation CellFound(Guid missionId, decimal cellId, string doorCode) =>
        new(missionId, StorageReservationOutcome.CellFound, cellId, doorCode, Partition: "0002",
            StorageDepth: 150, NeedsCompaction: false);

    private static ReserveStorageRequest RequestFor(Guid missionId) => RequestFor(missionId, []);

    private static ReserveStorageRequest RequestFor(Guid missionId, IReadOnlyList<Guid> reachableDeviceIds,
        IReadOnlyList<decimal>? excludedCellIds = null) => new(
        missionId,
        LoadUnitId: 42,
        WarehouseId: 1,
        SearchAreaIds: [1],
        ExcludedCellIds: excludedCellIds ?? [],
        ReachableDeviceIds: reachableDeviceIds,
        StorageAreaSelectionMode.All,
        StorageCellSearchMode.FreeAutomaticOperator,
        CellFullnessPreference.Fullest,
        StorageSearchOperatorType.Automatic,
        AssignPosition: true,
        VerifyPosition: false,
        RequestedByWorkstationId: "WS1",
        RequestedByUserId: "user1");

    /// <summary>
    /// Comportamento 1: una missione che non trova posto viene riaccodata e riprova; i tentativi
    /// dopo il primo non riscrivono. La chiamata che prenota e' l'unica scrittura in transazione del
    /// percorso caldo: farla a ogni tentativo la si paga proprio quando l'impianto e' gia' in
    /// difficolta', e nel frattempo un'altra missione puo' prendere la cella che questa aspetta.
    /// <para>
    /// Il collaudo passa da <see cref="PathMission.ReserveOrKeepStorageAsync"/>, che e' il codice
    /// vero che <c>FindRouteAllocateLu</c> chiama - non un metodo aggiunto per il test: e' la stessa
    /// ragione per cui <c>CategorizeReservationOutcome</c> e' <c>internal</c> (vedi
    /// <see cref="ReserveStorageOutcomeTest"/>), cioe' che <c>FindRouteAllocateLu</c> vuole un
    /// <see cref="AutomationDevice"/> completo, fuori dalla portata di un test unitario.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AMissionThatRetriesReservesOnceAndThenAsks()
    {
        var (relocator7, relocator8) = await APlantWithTwoCellsEachOnItsOwnRelocator();
        var warehouse = new FakeWarehouse();
        var missionId = Guid.NewGuid();
        warehouse.StorageReservations[42] = CellFound(missionId, 7, "A7");

        var request = RequestFor(missionId, [relocator7, relocator8]);

        var first = await PathMission.ReserveOrKeepStorageAsync(warehouse, request, keepExisting: true);
        var second = await PathMission.ReserveOrKeepStorageAsync(warehouse, request, keepExisting: true);
        var third = await PathMission.ReserveOrKeepStorageAsync(warehouse, request, keepExisting: true);

        Assert.Equal(1, warehouse.Calls.Count(c => c == nameof(IWarehouse.ReserveStorageAsync)));
        Assert.Equal(3, warehouse.Calls.Count(c => c == nameof(IWarehouse.GetStorageReservationAsync)));

        // La cella e' la stessa a tutti e tre i tentativi: e' quello che vuol dire «tenuta».
        Assert.Equal(7, first.CellId);
        Assert.Equal(7, second.CellId);
        Assert.Equal(7, third.CellId);
    }

    /// <summary>
    /// Comportamento 2: una prenotazione revocata dal gestionale (la cella se l'e' presa qualcun
    /// altro, o l'assegnazione e' stata cancellata da un'altra strada) non e' piu' valida, e il
    /// tentativo dopo riprenota davvero. Il gestionale risponde «non c'e'» con un «non trovato», che
    /// il client traduce in nulla: e' una risposta, non un guasto.
    /// </summary>
    [Fact]
    public async Task AReservationRevokedByTheWarehouseIsBookedAgain()
    {
        var (relocator7, relocator8) = await APlantWithTwoCellsEachOnItsOwnRelocator();
        var warehouse = new FakeWarehouse();
        var missionId = Guid.NewGuid();
        warehouse.StorageReservations[42] = CellFound(missionId, 7, "A7");

        var request = RequestFor(missionId, [relocator7, relocator8]);

        var first = await PathMission.ReserveOrKeepStorageAsync(warehouse, request, keepExisting: true);

        // Da qui in poi una prenotazione nuova troverebbe un'altra cella: e' quello che distingue la
        // prenotazione tenuta da una riscritta a ogni giro.
        warehouse.StorageReservations[42] = CellFound(missionId, 8, "A8");

        var kept = await PathMission.ReserveOrKeepStorageAsync(warehouse, request, keepExisting: true);

        // Il gestionale revoca: la cella 7 non e' piu' di questa missione.
        warehouse.RevokeStorageReservation(missionId);

        var booked = await PathMission.ReserveOrKeepStorageAsync(warehouse, request, keepExisting: true);

        Assert.Equal(7, first.CellId);
        Assert.Equal(7, kept.CellId);
        Assert.Equal(8, booked.CellId);
        Assert.Equal(2, warehouse.Calls.Count(c => c == nameof(IWarehouse.ReserveStorageAsync)));
    }

    /// <summary>
    /// La prenotazione tenuta si riverifica contro i vincoli del tentativo corrente, che sono stati
    /// appena riempiti con lo stato vivo dell'impianto. Una cella raggiungibile solo attraverso un
    /// dispositivo che nel frattempo e' andato fuori linea non e' piu' un posto dove questa missione
    /// possa andare: tenerla vorrebbe dire farsi rispondere sempre la stessa, e la missione non si
    /// muoverebbe piu'. La si lascia e si riprenota - la stessa strada gia' scritta per
    /// <c>AutomationDeviceStorageManager.FindNewStoreLocation</c>.
    /// </summary>
    [Fact]
    public async Task AKeptReservationWhoseAccessDeviceWentOffLineIsGivenUpAndBookedAgain()
    {
        var (relocator7, relocator8) = await APlantWithTwoCellsEachOnItsOwnRelocator();
        var warehouse = new FakeWarehouse();
        var missionId = Guid.NewGuid();
        warehouse.StorageReservations[42] = CellFound(missionId, 7, "A7");

        var first = await PathMission.ReserveOrKeepStorageAsync(warehouse,
            RequestFor(missionId, [relocator7, relocator8]), keepExisting: true);

        // Il ricollocatore che serve la cella 7 va fuori linea: il tentativo dopo non lo mette piu'
        // fra i raggiungibili, e l'unica cella che resta e' la 8.
        warehouse.StorageReservations[42] = CellFound(missionId, 8, "A8");

        var booked = await PathMission.ReserveOrKeepStorageAsync(warehouse,
            RequestFor(missionId, [relocator8]), keepExisting: true);

        Assert.Equal(7, first.CellId);
        Assert.Equal(8, booked.CellId);

        // Lasciata, non solo sovrascritta: la cella 7 torna libera per chi puo' ancora usarla.
        Assert.Equal([missionId], warehouse.ReleasedStorageReservations);
        Assert.Equal(2, warehouse.Calls.Count(c => c == nameof(IWarehouse.ReserveStorageAsync)));
    }

    /// <summary>
    /// L'altro vincolo del tentativo: le celle escluse. Una prenotazione tenuta sulla cella che
    /// questo tentativo esclude - la cella di provenienza, per non depositare dove si e' appena
    /// prelevato - non e' una risposta valida a questa domanda.
    /// </summary>
    [Fact]
    public async Task AKeptReservationOnACellTheAttemptExcludesIsGivenUpAndBookedAgain()
    {
        var (relocator7, relocator8) = await APlantWithTwoCellsEachOnItsOwnRelocator();
        var warehouse = new FakeWarehouse();
        var missionId = Guid.NewGuid();
        warehouse.StorageReservations[42] = CellFound(missionId, 7, "A7");

        var first = await PathMission.ReserveOrKeepStorageAsync(warehouse,
            RequestFor(missionId, [relocator7, relocator8]), keepExisting: true);

        warehouse.StorageReservations[42] = CellFound(missionId, 8, "A8");

        var booked = await PathMission.ReserveOrKeepStorageAsync(warehouse,
            RequestFor(missionId, [relocator7, relocator8], excludedCellIds: [7]), keepExisting: true);

        Assert.Equal(7, first.CellId);
        Assert.Equal(8, booked.CellId);
        Assert.Equal([missionId], warehouse.ReleasedStorageReservations);
    }

    /// <summary>
    /// «Trovami un posto» e «trovamene un <b>altro</b>» non sono la stessa domanda, e la seconda non
    /// tiene la prenotazione: e' <c>AutomationDeviceStorageManager.FindNewStoreLocation</c>, che
    /// parte proprio perche' la cella che la missione aveva non va piu' bene (occupata da
    /// un'estrazione, bloccata, o senza quota) e che prima di cercare blocca quella cella per non
    /// riprenderla. Tenendo la prenotazione si sarebbe fatta rispondere la stessa cella, e la
    /// missione non si sarebbe mossa mai piu'.
    /// <para>
    /// La prenotazione vecchia si <b>lascia</b>, non si sovrascrive soltanto: cosi' la cella torna
    /// libera per chi puo' usarla anche quando la ricerca nuova non trova niente.
    /// </para>
    /// </summary>
    [Fact]
    public async Task LookingForADifferentPlaceGivesUpTheOneItHad()
    {
        var warehouse = new FakeWarehouse();
        var missionId = Guid.NewGuid();
        warehouse.StorageReservations[42] = CellFound(missionId, 7, "A7");

        await PathMission.ReserveOrKeepStorageAsync(warehouse, RequestFor(missionId), keepExisting: true);

        // La cella 7 non va piu' bene: la ricerca nuova ne trova un'altra.
        warehouse.StorageReservations[42] = CellFound(missionId, 8, "A8");

        var again = await PathMission.ReserveOrKeepStorageAsync(warehouse, RequestFor(missionId), keepExisting: false);

        Assert.Equal(8, again.CellId);
        Assert.Equal([missionId], warehouse.ReleasedStorageReservations);
        Assert.Equal(2, warehouse.Calls.Count(c => c == nameof(IWarehouse.ReserveStorageAsync)));

        // Non ha nemmeno chiesto se ne avesse una: la domanda e' un'altra.
        Assert.DoesNotContain(nameof(IWarehouse.GetStorageReservationAsync),
            warehouse.Calls.Skip(warehouse.Calls.IndexOf(nameof(IWarehouse.ReleaseStorageReservationAsync))));
    }

    /// <summary>
    /// Il caso peggiore della domanda «trovamene un altro»: la ricerca nuova non trova niente. La
    /// missione non resta attaccata alla cella che non poteva usare - l'ha gia' lasciata - e la
    /// cella e' libera per chi puo' usarla.
    /// </summary>
    [Fact]
    public async Task LookingForADifferentPlaceAndFindingNoneLeavesTheMissionWithNothing()
    {
        var warehouse = new FakeWarehouse();
        var missionId = Guid.NewGuid();
        warehouse.StorageReservations[42] = CellFound(missionId, 7, "A7");

        await PathMission.ReserveOrKeepStorageAsync(warehouse, RequestFor(missionId), keepExisting: true);

        warehouse.StorageReservations[42] = new StorageReservation(
            missionId, StorageReservationOutcome.NoFreeCells, CellId: 0,
            DoorCode: string.Empty, Partition: null, StorageDepth: 0, NeedsCompaction: false);

        var again = await PathMission.ReserveOrKeepStorageAsync(warehouse, RequestFor(missionId), keepExisting: false);

        Assert.Equal(StorageReservationOutcome.NoFreeCells, again.Outcome);
        Assert.Null(await warehouse.GetStorageReservationAsync(missionId));
    }

    /// <summary>
    /// Comportamento 3: la missione che si chiude - o che viene annullata - rilascia la prenotazione.
    /// <see cref="ActiveMissions.Remove"/> e' la sola via da cui una missione esce dal motore
    /// <b>perche' e' finita</b>, e la pulizia verso il gestionale sta gia' li' (l'assegnazione della
    /// nota, la coda di estrazione). Le altre due vie che tolgono missioni dalla collezione non sono
    /// chiusure e non devono rilasciare niente: <c>RemoveAll</c> non ha chiamanti di produzione (la
    /// usano i test per ripulire), e <c>Clear(false)</c> svuota lo stato in memoria senza toccare il
    /// database - l'avvio del motore, che subito dopo ricarica le missioni da li', e la
    /// cancellazione dell'impianto dell'importatore interno (non pubblicato), che le righe le ha gia' cancellate lei. La
    /// <c>Clear(true)</c> dell'annullamento generale, che invece chiude davvero, passa da
    /// <see cref="ActiveMissions.Remove"/> una missione alla volta.
    /// </summary>
    [Fact]
    public async Task ClosingTheMissionReleasesTheReservation()
    {
        var warehouse = new FakeWarehouse();
        Navigator.Warehouse = warehouse;

        var device = NewDevice();

        var mission = new PathMission { PositionItemId = device.ItemGuid };
        ActiveMissions.Instance.Add(mission, buildRoute: false);

        warehouse.StorageReservations[42] = CellFound(mission.MissionId, 7, "A7");
        await PathMission.ReserveOrKeepStorageAsync(warehouse, RequestFor(mission.MissionId), keepExisting: true);

        Assert.True(await ActiveMissions.Instance.Remove(mission));

        Assert.Equal([mission.MissionId], warehouse.ReleasedStorageReservations);
        Assert.Null(await warehouse.GetStorageReservationAsync(mission.MissionId));
    }

    /// <summary>
    /// Il rilascio e' a miglior sforzo: un gestionale che non risponde non deve impedire alla
    /// missione di uscire dal motore. E' la stessa regola gia' scritta per la pulizia esterna di
    /// <see cref="ActiveMissions.Remove"/> («il motore e' gia' coerente: la missione non c'e'
    /// piu'»), e la rete che raccoglie la prenotazione rimasta appesa e' il compito periodico del
    /// gestionale, non un secondo tentativo del motore.
    /// <para>
    /// L'idempotenza vera del rilascio - rilasciare due volte non e' un errore - si collauda dove
    /// vive la cancellazione, cioe' su <c>MagazzinoPerWcs.ReleaseStorageReservationAsync</c>
    /// (<c>MagazzinoPerWcsTest</c>): qui sopra ci sarebbe solo la controfigura.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AWarehouseThatDoesNotAnswerDoesNotKeepTheMissionInTheEngine()
    {
        var warehouse = new FakeWarehouse();
        Navigator.Warehouse = warehouse;

        var device = NewDevice();

        var mission = new PathMission { PositionItemId = device.ItemGuid };
        ActiveMissions.Instance.Add(mission, buildRoute: false);

        warehouse.Unavailable = true;

        Assert.True(await ActiveMissions.Instance.Remove(mission));
        Assert.False(ActiveMissions.Instance.Contains(mission));
        Assert.Contains(nameof(IWarehouse.ReleaseStorageReservationAsync), warehouse.Calls);
    }
}
