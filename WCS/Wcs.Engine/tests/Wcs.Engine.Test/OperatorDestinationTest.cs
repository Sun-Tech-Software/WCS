using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Engine.Api;
using Wcs.Engine.Collections;
using Wcs.Engine.Items;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Wcs.Engine.Test.Routing;
using Wcs.Wms.Contracts;
using Wcs.Wms.Contracts.Test;
using Xunit;
using static Wcs.Common.Enums;
using RouteBuildStatusTypes = Wcs.Engine.Items.AutomationDevice.RouteBuildStatusTypes;

namespace Wcs.Engine.Test;

/// <summary>
/// La destinazione scelta dall'operatore resta (compito 2 del piano delle missioni, decisione del
/// proprietario del 30/9): quando il motore ricostruisce la rotta non riapplica la destinazione
/// fissa, la modalita' di deposito o il selettore del dispositivo, e la scelta sopravvive alla
/// ricarica dal database.
/// </summary>
/// <remarks>
/// <para>
/// Il navigatore sta in <c>Stop</c>, e in <c>Run</c> dove serve il giro delle rotte, mai in
/// <c>Recover</c>: in Recover il setter della destinazione salta il suo blocco e
/// <c>PathBuilder</c> i filtri, cioe' proprio i rami da provare. Le missioni si aggiungono in
/// Recover perche' <c>ActiveMissions.Add</c> esce subito in Stop, poi si passa a Stop. La sola
/// ricarica gira in Recover, perche' e' li' che la fa il motore (<c>Navigator</c> prima di
/// <c>DbInfo.RecoverFailure</c>).
/// </para>
/// <para>
/// Il database del WCS e' in memoria e senza scrittore in coda: le righe della missione si
/// scrivono subito, come in <see cref="MissionMoveAnnouncementTest"/>.
/// </para>
/// </remarks>
public sealed class OperatorDestinationTest : IDisposable
{
    private const decimal LinkedCellId = 7;
    private const decimal FoundCellId = 8;
    private const string FoundDoorCode = "A8";
    private const decimal LoadUnitId = 42;

    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly QueuedDatabaseWriter<WcsWrite>? _previousWriter = Navigator.StateWriter;
    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly RoutingTestPlant _plant = new();
    private readonly FakeWarehouse _warehouse = new();

    public OperatorDestinationTest()
    {
        Navigator.Store = _database;
        Navigator.StateWriter = null;
        Navigator.Warehouse = _warehouse;
        RoutesToBuild.Instance.Items = new();
    }

    public void Dispose()
    {
        Navigator.EventSink = null;
        RoutesToBuild.Instance.Items = new();
        _plant.Dispose();
        Navigator.Warehouse = _previousWarehouse;
        Navigator.StateWriter = _previousWriter;
        Navigator.Store = null;
        _database.Dispose();
    }

    // ---------------------------------------------------------------------------------------
    // La ricostruzione della rotta: FindWhereStoreLu
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// La posizione ha una destinazione fissa (Y) diversa da quella scelta dall'operatore (X): la
    /// rotta si costruisce verso X, e la destinazione resta X.
    /// </summary>
    [Fact]
    public async Task TheFixedDestinationOfThePositionDoesNotReplaceTheOperatorsChoice()
    {
        var mission = await AnOperatorMissionOnAPositionWithAFixedDestination();

        var outcome = await Navigator.FindWhereStoreLu(mission, _plant["P"], _plant["P"].LookWhereAllocateLu);

        Assert.Equal(RouteBuildStatusTypes.Available, outcome);
        Assert.Equal(_plant["X"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.OperatorDestination);
        AssertSingleStep(mission, "P", "X");
    }

    /// <summary>
    /// La stessa scena senza il segno: la destinazione fissa della posizione si applica, come oggi.
    /// Verde prima e dopo il compito.
    /// </summary>
    [Fact]
    public async Task WithoutTheMarkTheFixedDestinationOfThePositionStillApplies()
    {
        ThreeDevicesWithAFixedDestination();
        var mission = await MissionOn("P");
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        mission.DestinationItemId = _plant["X"].ItemGuid;

        var outcome = await Navigator.FindWhereStoreLu(mission, _plant["P"], _plant["P"].LookWhereAllocateLu);

        Assert.Equal(RouteBuildStatusTypes.Available, outcome);
        Assert.Equal(_plant["Y"].ItemGuid, mission.DestinationItemId);
        Assert.False(mission.OperatorDestination);
        AssertSingleStep(mission, "P", "Y");
    }

    /// <summary>
    /// La posizione ha una modalita' di deposito, e il gestionale avrebbe una cella nel magazzino
    /// collegato: con il segno non lo si chiama nemmeno, e la rotta va verso X. Una modalita' per
    /// caso, tutte quelle diverse da <c>No</c>. Per <c>BuildRouteToStoreForcing</c> il controllo sul
    /// gestionale non prova niente (<c>FindWhereStoreLu</c> non ha un ramo per lei, e non lo chiama
    /// comunque): quella riga prova che con il segno la rotta verso X c'e', dove senza il segno la
    /// ricerca risponde «non disponibile».
    /// </summary>
    [Theory]
    [InlineData(LookWhereAllocateLuTypes.CheckOnly)]
    [InlineData(LookWhereAllocateLuTypes.BuildRouteToStore)]
    [InlineData(LookWhereAllocateLuTypes.BuildRouteToStoreByIdLuType)]
    [InlineData(LookWhereAllocateLuTypes.BuildRouteToDifferentStoreLocation)]
    [InlineData(LookWhereAllocateLuTypes.BuildRouteToStoreForcing)]
    public async Task AStoringModeOfThePositionDoesNotReplaceTheOperatorsChoice(LookWhereAllocateLuTypes mode)
    {
        await APositionThatStoresInItsLinkedCell(mode);
        var mission = await MissionOn("P");
        TheWarehouseAnswersCell8For(mission);
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        Assert.True(mission.SetOperatorDestination(_plant["X"].ItemGuid));
        _warehouse.Calls.Clear();

        var outcome = await Navigator.FindWhereStoreLu(mission, _plant["P"], mode);

        Assert.Equal(RouteBuildStatusTypes.Available, outcome);
        Assert.Empty(_warehouse.Calls);
        Assert.Equal(_plant["X"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.OperatorDestination);
        AssertSingleStep(mission, "P", "X");
    }

    /// <summary>
    /// La stessa scena senza il segno: il gestionale viene chiamato e la missione va nella cella
    /// che risponde, come oggi. Verde prima e dopo il compito.
    /// Item 54 (decision 1 of the owner of 13/10): the mission has no destination, is bound for the
    /// storage S8 itself or for the group GS with S8 in it, or is bound for P, where it already is. Until
    /// item 54 it was bound for X, a device that is not a storage: such a mission now keeps its
    /// destination (<see cref="AStoringModeDoesNotTouchAMissionBoundForADeviceThatIsNotAStorage"/>).
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("S8")]
    [InlineData("GS")]
    [InlineData("P")]
    public async Task WithoutTheMarkAStoringModeStillAsksTheWarehouse(string destination)
    {
        await APositionThatStoresInItsLinkedCell(LookWhereAllocateLuTypes.BuildRouteToStore);
        if (destination == "GS")
        {
            var group = _plant.Device("GS", DeviceTypes.GroupOfAutomationDevices);
            group.GroupItemId = 93;
            _plant["S8"].BelongingGroupId = group.GroupItemId;
            Assert.Same(group, _plant["S8"].BelongedGroup);
        }

        var mission = await MissionOn("P");
        TheWarehouseAnswersCell8For(mission);
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        if (destination.Length > 0)
            mission.DestinationItemId = _plant[destination].ItemGuid;
        _warehouse.Calls.Clear();

        var outcome = await Navigator.FindWhereStoreLu(mission, _plant["P"], LookWhereAllocateLuTypes.BuildRouteToStore);

        Assert.Equal(RouteBuildStatusTypes.Available, outcome);
        Assert.Contains(nameof(IWarehouse.ReserveStorageAsync), _warehouse.Calls);
        Assert.Equal(_plant["S8"].ItemGuid, mission.DestinationItemId);
        Assert.Equal(FoundDoorCode, mission.StorageDoorCode);
    }

    /// <summary>
    /// La strada che fa il motore in marcia: il dispositivo di posizione esegue la sua strategia,
    /// trova la missione senza step e la mette in coda; il giro delle rotte la pesca e avvia la
    /// ricerca, che costruisce verso X. E' la strada per cui la strategia <c>Standard</c> e la coda
    /// arrivano a <c>FindWhereStoreLu</c>.
    /// </summary>
    [Fact]
    public async Task InRunTheDeviceStrategyAndTheRouteQueueBuildTowardsTheOperatorsChoice()
    {
        var mission = await AnOperatorMissionOnAPositionWithAFixedDestination();

        // Nessuno riparte da solo al passaggio in Run: l'esecuzione la chiama la prova, una volta.
        foreach (var name in _plant.DeviceNames) _plant[name].AutoExecuteStep = false;
        Navigator.NavStatus = NavigatorStatusTypes.Run;

        Assert.True(await _plant["P"].ExecuteStep());
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);

        // Il giro prende la testa della coda, e la coda non e' solo di questa prova: nella suite
        // intera ci arrivano, anche dopo l'azzeramento del costruttore, missioni gia' rimosse dai
        // lavori asincroni rimasti dagli altri collaudi (senza posizione: il giro le riaccoda in
        // fondo). Con un giro solo la prova falliva cosi' in cinque passate su sette della suite.
        // Si gira finche' la nostra non e' uscita dalla coda; il tetto fa fallire invece di girare
        // per sempre.
        for (var turn = 0; RoutesToBuild.Instance[mission.MissionId] != null && turn < 100; turn++)
            await OneTurnOfTheRouteLoop().WaitAsync(TimeSpan.FromSeconds(30));

        if (Navigator.CellSearchInFlight(mission.MissionId) is { } search)
            await search.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Null(RoutesToBuild.Instance[mission.MissionId]);
        Assert.Equal(_plant["X"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.OperatorDestination);
        AssertSingleStep(mission, "P", "X");
    }

    // ---------------------------------------------------------------------------------------
    // Item 54, decision 1: a storing mode does not touch a mission bound for a device that is not a storage
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The case UC256355 in small (item 54, decision 1 and answer 8 of the owner of 13/10): an extraction
    /// out of the cell 9 of S8, bound for the bay X, on the entry point P, whose storing mode would get
    /// the cell 8 of S8 from the warehouse. In every storing mode (1-5, forcing included) the mission
    /// keeps its destination: no question to the warehouse, the route P->X, still an extraction. Until
    /// item 54 the modes 2-4 reserved the cell 8 and rewrote the destination to S8 (with the two doors
    /// of S8 the extraction became a change of cell, or after task 1 a deposit), the check only asked
    /// the warehouse, and the forcing answered «not available».
    /// </summary>
    [Theory]
    [InlineData(LookWhereAllocateLuTypes.CheckOnly)]
    [InlineData(LookWhereAllocateLuTypes.BuildRouteToStore)]
    [InlineData(LookWhereAllocateLuTypes.BuildRouteToStoreByIdLuType)]
    [InlineData(LookWhereAllocateLuTypes.BuildRouteToDifferentStoreLocation)]
    [InlineData(LookWhereAllocateLuTypes.BuildRouteToStoreForcing)]
    public async Task AStoringModeDoesNotTouchAMissionBoundForADeviceThatIsNotAStorage(LookWhereAllocateLuTypes mode)
    {
        var mission = await AnExtractionOfS8BoundForTheBayXOn("P", mode);

        var outcome = await Navigator.FindWhereStoreLu(mission, _plant["P"], mode);

        Assert.Equal(RouteBuildStatusTypes.Available, outcome);
        Assert.Empty(_warehouse.Calls);
        TheExtractionStillGoesToTheBayX(mission, from: "P");
    }

    /// <summary>
    /// The same in Run, the way the engine gets there: the route loop picks the mission up and starts
    /// its cell search (<c>Navigator.StartCellSearch</c>), which builds the route to the bay X without
    /// asking the warehouse, and the mission leaves the queue.
    /// </summary>
    [Fact]
    public async Task InRunTheRouteLoopKeepsTheDestinationOfAMissionBoundForADeviceThatIsNotAStorage()
    {
        var mission = await AnExtractionOfS8BoundForTheBayXOn("P", LookWhereAllocateLuTypes.BuildRouteToStore);
        foreach (var name in _plant.DeviceNames) _plant[name].AutoExecuteStep = false;
        Navigator.NavStatus = NavigatorStatusTypes.Run;

        await TheRouteLoopLooksForTheCell(mission);

        Assert.Empty(_warehouse.Calls);
        Assert.Null(RoutesToBuild.Instance[mission.MissionId]);
        TheExtractionStillGoesToTheBayX(mission, from: "P");
    }

    /// <summary>
    /// The move of the case UC256355: the operator moves the extraction from the conveyor C onto the
    /// entry point P. The navigator would go straight to the bay X from there
    /// (<c>Navigator.GoesStraightToTheDestination</c>), so the move builds the route at once, with the
    /// engine stopped, as for a device without a storing mode. Until item 54 the mission was queued, and
    /// the route loop in Run gave it a cell of S8.
    /// </summary>
    [Fact]
    public async Task AMoveOntoAnEntryPointBuildsTheRouteToTheBayAtOnce()
    {
        var mission = await AnExtractionOfS8BoundForTheBayXOn("C", LookWhereAllocateLuTypes.BuildRouteToStore);

        Assert.Equal(MissionMoveOutcome.Moved, MissionGateway.Move(mission.MissionId, _plant["P"].ItemGuid));
        await mission.PositionWork;

        // The only call is the new position of the load unit, written by the background work of the
        // move (PathMission.PositionWork): no cell is asked for.
        Assert.Null(RoutesToBuild.Instance[mission.MissionId]);
        Assert.Equal([nameof(IWarehouse.AllocateLoadUnitAsync)], _warehouse.Calls);
        TheExtractionStillGoesToTheBayX(mission, from: "P");
    }

    /// <summary>
    /// The group selector for the load unit type (<c>AutomationDevice.VerifyWhereAllocateLuGrouped</c>),
    /// which writes door and destination without going through <c>FindWhereStoreLu</c>, respects the
    /// rule too (answer 8): two load units of the same type on the group, the second bound for X. The
    /// first gets the cell NEW-A from the warehouse and leaves the selector of its type; the second does
    /// not take it, and goes to X. Until item 54 it took the door NEW-A and the destination S.
    /// </summary>
    [Fact]
    public async Task TheGroupedAllocationDoesNotGiveAMissionBoundForADeviceThatIsNotAStorageTheSelector()
    {
        var (normal, boundForX) = await TwoMissionsOfTheSameTypeOnAGroupTheSecondBoundForX(boundForXFirst: false);

        await _plant["G"].VerifyWhereAllocateLuGrouped([normal, boundForX]).WaitAsync(TimeSpan.FromSeconds(30));

        TheNormalOneGetsNewAAndTheOtherGoesToX(normal, boundForX);
    }

    /// <summary>
    /// The reverse order: the mission bound for X comes first. It keeps X, and X does not become the
    /// selector of its type for the other one, which asks the warehouse for its own cell. Until item 54
    /// the first asked the warehouse too (and got NEW-B).
    /// </summary>
    [Fact]
    public async Task TheGroupedAllocationDoesNotTurnTheDestinationOfAMissionBoundForADeviceThatIsNotAStorageIntoASelector()
    {
        var (normal, boundForX) = await TwoMissionsOfTheSameTypeOnAGroupTheSecondBoundForX(boundForXFirst: true);

        await _plant["G"].VerifyWhereAllocateLuGrouped([boundForX, normal]).WaitAsync(TimeSpan.FromSeconds(30));

        TheNormalOneGetsNewAAndTheOtherGoesToX(normal, boundForX);
    }

    /// <summary>
    /// The grouped search of a new cell (<c>FindNewStoreLocationGrouped</c>, the manual command: every
    /// mission on board), with the mission bound for X second: it does not take the selector of the first.
    /// </summary>
    [Fact]
    public async Task TheGroupedStoreSearchDoesNotGiveAMissionBoundForADeviceThatIsNotAStorageTheSelector()
    {
        var (normal, boundForX) = await TwoMissionsOfTheSameTypeOnAGroupTheSecondBoundForX(boundForXFirst: false);

        Assert.True(await _plant["G"].StorageManager!.FindNewStoreLocationGroupedForTest().WaitAsync(TimeSpan.FromSeconds(30)));

        TheNormalOneGetsNewAAndTheOtherGoesToX(normal, boundForX);
    }

    /// <summary>
    /// The grouped search of a new cell with the mission bound for X first: its destination does not
    /// become the selector of the other one.
    /// </summary>
    [Fact]
    public async Task TheGroupedStoreSearchDoesNotTurnTheDestinationOfAMissionBoundForADeviceThatIsNotAStorageIntoASelector()
    {
        var (normal, boundForX) = await TwoMissionsOfTheSameTypeOnAGroupTheSecondBoundForX(boundForXFirst: true);

        Assert.True(await _plant["G"].StorageManager!.FindNewStoreLocationGroupedForTest().WaitAsync(TimeSpan.FromSeconds(30)));

        TheNormalOneGetsNewAAndTheOtherGoesToX(normal, boundForX);
    }

    /// <summary>
    /// Item 54, task 6 (decision 5): the group selector does not give its cell to a load unit the operator
    /// only brought to the relocator, the group G itself (the relocator of S): on B, a member of G, the
    /// choice decides (<c>FindWhereStoreLu</c> answers that it stays), and the load unit keeps G, with no
    /// door. Without the guard it took the door NEW-A of the first one and the destination S.
    /// </summary>
    [Fact]
    public async Task TheGroupedAllocationDoesNotGiveTheSelectorToALoadUnitBroughtToItsRelocatorToStay()
    {
        var (normal, broughtToG) = await TwoMissionsOfTheSameTypeOnAGroupTheSecondBroughtToGToStay();

        await _plant["G"].VerifyWhereAllocateLuGrouped([normal, broughtToG]).WaitAsync(TimeSpan.FromSeconds(30));

        TheNormalOneGetsNewAAndTheOtherStaysOnG(normal, broughtToG);
    }

    /// <summary>
    /// The same in the grouped search of a new cell (<c>FindNewStoreLocationGrouped</c>): the load unit
    /// that stays does not take the selector, and the search goes on («found») instead of answering «no
    /// place» for it.
    /// </summary>
    [Fact]
    public async Task TheGroupedStoreSearchDoesNotGiveTheSelectorToALoadUnitBroughtToItsRelocatorToStay()
    {
        var (normal, broughtToG) = await TwoMissionsOfTheSameTypeOnAGroupTheSecondBroughtToGToStay();

        Assert.True(await _plant["G"].StorageManager!.FindNewStoreLocationGroupedForTest().WaitAsync(TimeSpan.FromSeconds(30)));

        TheNormalOneGetsNewAAndTheOtherStaysOnG(normal, broughtToG);
    }

    // ---------------------------------------------------------------------------------------
    // La scelta fatta mentre una ricerca aspetta il gestionale
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// La ricerca della cella parte senza il segno e aspetta la prenotazione del gestionale (fino a
    /// 30 s sul campo); intanto l'operatore sceglie X, con la sua rotta. Quando il gestionale
    /// risponde la ricerca non scrive niente: la destinazione, il segno e la rotta restano quelli
    /// dell'operatore, e la cella che la ricerca si era presa torna al gestionale. Con la posizione
    /// collegata al magazzino (la cella collegata) e senza (una cella qualunque).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AChoiceMadeWhileTheStoringModeWaitsForTheReservationStands(bool linkedToTheStorage)
    {
        await APositionThatStores(LookWhereAllocateLuTypes.BuildRouteToStore, linkedToTheStorage);
        var mission = await MissionOn("P");
        TheWarehouseAnswersCell8For(mission);
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        var reservation = new TaskCompletionSource();
        _warehouse.ReserveStorageGate = reservation;

        var search = Navigator.FindWhereStoreLu(mission, _plant["P"], LookWhereAllocateLuTypes.BuildRouteToStore);
        await _warehouse.ReserveStorageAsked.WaitAsync(TimeSpan.FromSeconds(30));
        var committed = await TheOperatorSendsItTo(mission, "X");
        reservation.SetResult();
        var outcome = await search.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(RouteBuildStatusTypes.Available, outcome);
        Assert.Equal(_plant["X"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.OperatorDestination);
        Assert.Same(committed, Assert.Single(mission.MissionSteps));
        Assert.Null(mission.StorageDoorCode);
        Assert.Equal([mission.MissionId], _warehouse.ReleasedStorageReservations);
        Assert.False(_warehouse.StorageReservationsByMission.ContainsKey(mission.MissionId));
    }

    /// <summary>
    /// L'operatore sceglie proprio il magazzino S8 mentre la ricerca aspetta la prenotazione: la
    /// cella che la ricerca ha preso (la 8, porta A8) ha la porta su S8, cioe' serve la destinazione
    /// scelta, e resta alla missione invece di tornare al gestionale. La scelta qui e' simulata: il
    /// segno e la porta A8, senza prenotare (in campo il cambio verso un magazzino prenota lui, e
    /// con una ricerca in corso non comincia). Si controlla da tutte e due le parti: il gestionale
    /// tiene la cella 8 per la missione, e la porta della missione e' la sua.
    /// </summary>
    [Fact]
    public async Task AChoiceOfTheStorageTheSearchReservedInKeepsTheCell()
    {
        await APositionThatStores(LookWhereAllocateLuTypes.BuildRouteToStore, linkedToTheStorage: true);
        var mission = await MissionOn("P");
        TheWarehouseAnswersCell8For(mission);
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        var reservation = new TaskCompletionSource();
        _warehouse.ReserveStorageGate = reservation;

        var search = Navigator.FindWhereStoreLu(mission, _plant["P"], LookWhereAllocateLuTypes.BuildRouteToStore);
        await _warehouse.ReserveStorageAsked.WaitAsync(TimeSpan.FromSeconds(30));

        // Il cambio verso un magazzino scrive la porta della cella che prenota (qui la stessa 8): una
        // missione dell'operatore verso un magazzino senza porta non ha rotta, la cella la prenota il
        // navigatore (decisioni del proprietario dell'1/10).
        mission.StorageDoorCode = FoundDoorCode;
        var committed = await TheOperatorSendsItTo(mission, "S8");
        reservation.SetResult();
        var outcome = await search.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(RouteBuildStatusTypes.Available, outcome);
        Assert.Equal(_plant["S8"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.OperatorDestination);
        Assert.Same(committed, Assert.Single(mission.MissionSteps));
        Assert.Empty(_warehouse.ReleasedStorageReservations);
        Assert.Equal(FoundCellId, _warehouse.StorageReservationsByMission[mission.MissionId].CellId);
        Assert.Equal(FoundDoorCode, mission.StorageDoorCode);
    }

    /// <summary>
    /// La scelta arriva un passo dopo, mentre la ricerca chiede al gestionale l'indirizzo di campo
    /// della cella (la porta e' gia' scritta): l'indirizzo, la destinazione e la rotta non si
    /// scrivono, e la cella torna al gestionale.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AChoiceMadeWhileTheStoringModeReadsTheCellAddressStands(bool linkedToTheStorage)
    {
        await APositionThatStores(LookWhereAllocateLuTypes.BuildRouteToStore, linkedToTheStorage);
        var mission = await MissionOn("P");
        TheWarehouseAnswersCell8For(mission);
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var address = new TaskCompletionSource();
        _warehouse.Before = operation =>
        {
            if (operation != nameof(IWarehouse.DescribeCellAsync) ||
                !_warehouse.Calls.Contains(nameof(IWarehouse.ReserveStorageAsync)))
                return Task.CompletedTask;

            reached.TrySetResult();
            return address.Task;
        };

        var search = Navigator.FindWhereStoreLu(mission, _plant["P"], LookWhereAllocateLuTypes.BuildRouteToStore);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var committed = await TheOperatorSendsItTo(mission, "X");
        address.SetResult();
        var outcome = await search.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(RouteBuildStatusTypes.Available, outcome);
        Assert.Equal(_plant["X"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.OperatorDestination);
        Assert.Same(committed, Assert.Single(mission.MissionSteps));
        Assert.Equal(string.Empty, mission.DestinationPartition);
        Assert.Equal(-1, mission.StorageDepth);
        Assert.Equal([mission.MissionId], _warehouse.ReleasedStorageReservations);
    }

    /// <summary>
    /// L'operatore sceglie un altro magazzino, S9, mentre la ricerca legge l'indirizzo della cella 8
    /// che ha appena preso. Il suo cambio prenota per la missione la cella 9 di S9 (il compito 3), e
    /// il gestionale lascia la 8: la prenotazione e' una per missione. Quando la ricerca riprende non
    /// scrive niente e non rilascia: il rilascio va per missione, e toglierebbe la cella
    /// dell'operatore invece di quella della ricerca.
    /// </summary>
    [Fact]
    public async Task AChoiceOfAnotherStorageReservedMeanwhileKeepsTheOperatorsCell()
    {
        await APositionThatStores(LookWhereAllocateLuTypes.BuildRouteToStore, linkedToTheStorage: true);
        var otherStorage = _plant.Device("S9", DeviceTypes.Storage);
        _plant.Link("P", "S9");
        otherStorage.RelocateItemId = _plant["P"].ItemGuid;
        await PlantPersistence.SaveDoorMappingsAsync(otherStorage.ItemGuid,
            [new DoorMapping { ItemId = otherStorage.ItemGuid, DoorCode = "A9", CellId = 9, Side = "A" }]);
        var mission = await MissionOn("P");
        TheWarehouseAnswersCell8For(mission);
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var address = new TaskCompletionSource();
        _warehouse.Before = operation =>
        {
            if (operation != nameof(IWarehouse.DescribeCellAsync) ||
                !_warehouse.Calls.Contains(nameof(IWarehouse.ReserveStorageAsync)))
                return Task.CompletedTask;

            reached.TrySetResult();
            return address.Task;
        };

        var search = Navigator.FindWhereStoreLu(mission, _plant["P"], LookWhereAllocateLuTypes.BuildRouteToStore);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(FoundCellId, _warehouse.StorageReservationsByMission[mission.MissionId].CellId);
        _warehouse.StorageReservationsByMission[mission.MissionId] = new StorageReservation(mission.MissionId,
            StorageReservationOutcome.CellFound, 9, "A9", Partition: "0019", StorageDepth: 150, NeedsCompaction: false);
        mission.StorageDoorCode = "A9";
        var committed = await TheOperatorSendsItTo(mission, "S9");
        address.SetResult();
        var outcome = await search.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(RouteBuildStatusTypes.Available, outcome);
        Assert.Equal(_plant["S9"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.OperatorDestination);
        Assert.Same(committed, Assert.Single(mission.MissionSteps));
        Assert.Equal("A9", mission.StorageDoorCode);
        Assert.Empty(_warehouse.ReleasedStorageReservations);
        Assert.Equal(9, _warehouse.StorageReservationsByMission[mission.MissionId].CellId);
    }

    /// <summary>
    /// <c>CheckOnly</c> con la destinazione fissa Y: la ricerca chiede al gestionale se una cella
    /// c'e', e intanto l'operatore sceglie X. La destinazione fissa, che la ricerca scriverebbe
    /// dopo la risposta, non si scrive (e' la guardia in <c>BuildRouteToFixedDestination</c>, che si
    /// raggiunge con il segno alzato proprio cosi'). Nessuna cella da restituire: <c>CheckOnly</c>
    /// chiede senza prenotare.
    /// </summary>
    [Fact]
    public async Task AChoiceMadeWhileCheckOnlyAsksTheWarehouseKeepsTheFixedDestinationOut()
    {
        await APositionThatStores(LookWhereAllocateLuTypes.CheckOnly, linkedToTheStorage: true);
        _plant.Device("Y");
        _plant.Link("P", "Y");
        _plant["P"].DestinationItemId = _plant["Y"].ItemGuid;
        var mission = await MissionOn("P");
        TheWarehouseAnswersCell8For(mission);
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        var reservation = new TaskCompletionSource();
        _warehouse.ReserveStorageGate = reservation;

        var search = Navigator.FindWhereStoreLu(mission, _plant["P"], LookWhereAllocateLuTypes.CheckOnly);
        await _warehouse.ReserveStorageAsked.WaitAsync(TimeSpan.FromSeconds(30));
        var committed = await TheOperatorSendsItTo(mission, "X");
        reservation.SetResult();
        var outcome = await search.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(RouteBuildStatusTypes.Available, outcome);
        Assert.Equal(_plant["X"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.OperatorDestination);
        Assert.Same(committed, Assert.Single(mission.MissionSteps));
        Assert.Empty(_warehouse.ReleasedStorageReservations);
    }

    /// <summary>
    /// La stessa corsa, con la scelta che arriva senza rotta (il segno e la destinazione soltanto):
    /// la ricerca finisce verso la scelta dell'operatore, come i rami del deposito, e la rotta P->X
    /// la costruisce lei. Prima la guardia della destinazione fissa usciva e basta, e la ricerca
    /// rispondeva «non disponibile» a una missione che aveva una destinazione raggiungibile.
    /// </summary>
    [Fact]
    public async Task AChoiceMadeWhileCheckOnlyAsksTheWarehouseEndsWithTheRouteToTheChoice()
    {
        await APositionThatStores(LookWhereAllocateLuTypes.CheckOnly, linkedToTheStorage: true);
        _plant.Device("Y");
        _plant.Link("P", "Y");
        _plant["P"].DestinationItemId = _plant["Y"].ItemGuid;
        var mission = await MissionOn("P");
        TheWarehouseAnswersCell8For(mission);
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        var reservation = new TaskCompletionSource();
        _warehouse.ReserveStorageGate = reservation;

        var search = Navigator.FindWhereStoreLu(mission, _plant["P"], LookWhereAllocateLuTypes.CheckOnly);
        await _warehouse.ReserveStorageAsked.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(mission.SetOperatorDestination(_plant["X"].ItemGuid));
        Assert.Empty(mission.MissionSteps);
        reservation.SetResult();
        var outcome = await search.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(RouteBuildStatusTypes.Available, outcome);
        Assert.Equal(_plant["X"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.OperatorDestination);
        AssertSingleStep(mission, "P", "X");
    }

    /// <summary>
    /// Il gestionale risponde che non ci sono celle, ma nel frattempo l'operatore ha scelto X: la
    /// missione non si rigetta. Il rigetto toglierebbe la rotta dell'operatore e manderebbe l'UDC al
    /// punto di rigetto R.
    /// </summary>
    [Fact]
    public async Task AChoiceMadeWhileTheWarehouseFindsNoCellIsNotRejected()
    {
        await APositionThatStores(LookWhereAllocateLuTypes.BuildRouteToStore, linkedToTheStorage: true);
        _plant.Device("R");
        _plant.Link("P", "R");
        _plant["P"].RejectItemDestinationAllocateLu = _plant["R"].ItemGuid;
        var mission = await MissionOn("P");
        _warehouse.StorageReservations[mission.LuId] = new StorageReservation(mission.MissionId,
            StorageReservationOutcome.NoFreeCells, 0, string.Empty, Partition: null, StorageDepth: 0,
            NeedsCompaction: false);
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        var reservation = new TaskCompletionSource();
        _warehouse.ReserveStorageGate = reservation;

        var search = Navigator.FindWhereStoreLu(mission, _plant["P"], LookWhereAllocateLuTypes.BuildRouteToStore);
        await _warehouse.ReserveStorageAsked.WaitAsync(TimeSpan.FromSeconds(30));
        var committed = await TheOperatorSendsItTo(mission, "X");
        reservation.SetResult();
        var outcome = await search.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(RouteBuildStatusTypes.Available, outcome);
        Assert.Equal(_plant["X"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.OperatorDestination);
        Assert.Same(committed, Assert.Single(mission.MissionSteps));
        Assert.Equal(MissionTypes.MovingToItem, mission.MissionType);
    }

    /// <summary>
    /// La scelta arriva a rigetto gia' cominciato: il gestionale ha risposto che non ci sono celle,
    /// il rigetto ha gia' guardato il segno e sta chiudendo il comando della missione, e in quel
    /// momento (l'evento del comando chiuso) l'operatore sceglie X. La destinazione del rigetto non
    /// si scrive: e' la guardia al momento della scrittura dentro <c>RejectTo</c>, che lo sguardo
    /// all'ingresso non copre. La rotta verso X la costruisce la ricerca.
    /// </summary>
    [Fact]
    public async Task AChoiceMadeWhileTheRejectionClosesTheCommandIsNotRejected()
    {
        await APositionThatStores(LookWhereAllocateLuTypes.BuildRouteToStore, linkedToTheStorage: true);
        _plant.Device("R");
        _plant.Link("P", "R");
        _plant["P"].RejectItemDestinationAllocateLu = _plant["R"].ItemGuid;
        var mission = await MissionOn("P");
        _warehouse.StorageReservations[mission.LuId] = new StorageReservation(mission.MissionId,
            StorageReservationOutcome.NoFreeCells, 0, string.Empty, Partition: null, StorageDepth: 0,
            NeedsCompaction: false);
        var command = new Command { Status = Command.StatusTypes.Running };
        await ORM.CommandToDo.Update(command);
        Assert.True(command.IdCommandToDo > 0);
        mission.IdCommandToDo = command.IdCommandToDo;
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        bool? chosen = null;
        Navigator.EventSink = new OnFirstCommandChanged(() => chosen = mission.SetOperatorDestination(_plant["X"].ItemGuid));

        var outcome = await Navigator.FindWhereStoreLu(mission, _plant["P"], LookWhereAllocateLuTypes.BuildRouteToStore);

        Assert.True(chosen);
        Assert.Equal(RouteBuildStatusTypes.Available, outcome);
        Assert.Equal(_plant["X"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.OperatorDestination);
        AssertSingleStep(mission, "P", "X");
        Assert.Equal(MissionTypes.MovingToItem, mission.MissionType);
    }

    /// <summary>
    /// Il gruppo cerca la cella per la prima UDC (su B) e aspetta il gestionale; intanto l'operatore
    /// la manda a X. La sua destinazione non diventa il selettore del tipo: la seconda UDC (su A),
    /// dello stesso tipo, chiede la sua cella al gestionale e va in S dalla porta NEW-A.
    /// </summary>
    [Fact]
    public async Task AChoiceMadeWhileTheGroupedAllocationWaitsDoesNotBecomeTheSelector()
    {
        var (onA, onB) = await TwoMissionsOfTheSameTypeOnAGroup(operatorMissionFirst: true, withTheMark: false);
        var reservation = new TaskCompletionSource();
        _warehouse.ReserveStorageGate = reservation;

        var allocation = _plant["G"].VerifyWhereAllocateLuGrouped([onB, onA]);
        await _warehouse.ReserveStorageAsked.WaitAsync(TimeSpan.FromSeconds(30));
        var committed = await TheOperatorSendsItTo(onB, "X");
        reservation.SetResult();
        await allocation.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(_plant["X"].ItemGuid, onB.DestinationItemId);
        Assert.True(onB.OperatorDestination);
        Assert.Same(committed, Assert.Single(onB.MissionSteps));
        Assert.Equal(_plant["S"].ItemGuid, onA.DestinationItemId);
        Assert.Equal("NEW-A", onA.StorageDoorCode);
        Assert.Equal(2, _warehouse.Calls.Count(call => call == nameof(IWarehouse.ReserveStorageAsync)));
        Assert.Equal([onB.MissionId], _warehouse.ReleasedStorageReservations);
    }

    /// <summary>
    /// La stessa corsa nella ricerca raggruppata di una nuova cella (<c>FindNewStoreLocationGrouped</c>).
    /// </summary>
    [Fact]
    public async Task AChoiceMadeWhileTheGroupedStoreSearchWaitsDoesNotBecomeTheSelector()
    {
        var (onA, onB) = await TwoMissionsOfTheSameTypeOnAGroup(operatorMissionFirst: true, withTheMark: false);
        var reservation = new TaskCompletionSource();
        _warehouse.ReserveStorageGate = reservation;

        var storeSearch = _plant["G"].StorageManager!.FindNewStoreLocationGroupedForTest();
        await _warehouse.ReserveStorageAsked.WaitAsync(TimeSpan.FromSeconds(30));
        var committed = await TheOperatorSendsItTo(onB, "X");
        reservation.SetResult();

        Assert.True(await storeSearch.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(_plant["X"].ItemGuid, onB.DestinationItemId);
        Assert.True(onB.OperatorDestination);
        Assert.Same(committed, Assert.Single(onB.MissionSteps));
        Assert.Equal(_plant["S"].ItemGuid, onA.DestinationItemId);
        Assert.Equal("NEW-A", onA.StorageDoorCode);
        Assert.Equal(2, _warehouse.Calls.Count(call => call == nameof(IWarehouse.ReserveStorageAsync)));
        Assert.Equal([onB.MissionId], _warehouse.ReleasedStorageReservations);
    }

    /// <summary>
    /// La ricerca raggruppata di una nuova cella prenota per la prima UDC (su B) la cella 202, porta
    /// NEW-B, e ne calcola la rotta; prima che la scriva un altro manda la missione verso X (voce 41,
    /// rilievo 1 del secondo giro del compito 3). La rotta non si scrive e la missione e' in coda al giro
    /// delle rotte: non e' «nessun posto», e il suo stato di adesso, che e' di chi ha vinto, non diventa
    /// il selettore del suo tipo. La seconda UDC (su A) chiede la sua cella e va in S dalla porta NEW-A,
    /// e la ricerca trova posto. Prima la rotta mancante della ricerca della cella collegata contava come
    /// costruita, e la seconda UDC prendeva come selettore la destinazione X senza porta. La cella 202 la
    /// prima UDC la restituisce, e ne toglie la porta (risposta 6 del proprietario dell'11/10).
    /// </summary>
    [Fact]
    public async Task AGroupedStoreSearchWhoseRouteIsTakenOverDoesNotBecomeTheSelector()
    {
        var (onA, onB) = await TwoMissionsOfTheSameTypeOnAGroup(operatorMissionFirst: true, withTheMark: false);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        onB.RouteBuildPauseForTesting = () =>
        {
            reached.TrySetResult();
            return released.Task;
        };

        Task<bool> storeSearch;
        try
        {
            storeSearch = _plant["G"].StorageManager!.FindNewStoreLocationGroupedForTest();
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal("NEW-B", onB.StorageDoorCode);
            onB.DestinationItemId = _plant["X"].ItemGuid;
        }
        finally
        {
            released.TrySetResult();
        }

        Assert.True(await storeSearch.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(_plant["X"].ItemGuid, onB.DestinationItemId);
        Assert.Empty(onB.MissionSteps);
        Assert.Same(onB, RoutesToBuild.Instance[onB.MissionId]);
        Assert.False(_warehouse.StorageReservationsByMission.ContainsKey(onB.MissionId));
        Assert.Contains(onB.MissionId, _warehouse.ReleasedStorageReservations);
        Assert.True(string.IsNullOrEmpty(onB.StorageDoorCode));
        Assert.Equal(_plant["S"].ItemGuid, onA.DestinationItemId);
        Assert.Equal("NEW-A", onA.StorageDoorCode);
        Assert.Equal(2, _warehouse.Calls.Count(call => call == nameof(IWarehouse.ReserveStorageAsync)));
    }

    // ---------------------------------------------------------------------------------------
    // I selettori per tipo di UDC
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Due UDC dello stesso tipo sul gruppo: la prima (senza segno) chiede la cella al gestionale e
    /// lascia il selettore del suo tipo; la seconda, con il segno, non lo prende.
    /// </summary>
    [Fact]
    public async Task TheGroupedAllocationDoesNotGiveTheOperatorsMissionTheSelectorOfItsType()
    {
        var (normal, chosen) = await TwoMissionsOfTheSameTypeOnAGroup(operatorMissionFirst: false);

        await _plant["G"].VerifyWhereAllocateLuGrouped([normal, chosen]).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(_plant["S"].ItemGuid, normal.DestinationItemId);
        Assert.Equal(_plant["X"].ItemGuid, chosen.DestinationItemId);
        Assert.True(chosen.OperatorDestination);
        AssertSingleStep(chosen, "B", "X");
    }

    /// <summary>
    /// L'ordine inverso: la missione con il segno viene prima, e la sua destinazione non diventa il
    /// selettore del tipo per l'altra, che chiede la sua cella al gestionale.
    /// </summary>
    [Fact]
    public async Task TheGroupedAllocationDoesNotTurnTheOperatorsChoiceIntoASelector()
    {
        var (normal, chosen) = await TwoMissionsOfTheSameTypeOnAGroup(operatorMissionFirst: true);

        await _plant["G"].VerifyWhereAllocateLuGrouped([chosen, normal]).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(_plant["X"].ItemGuid, chosen.DestinationItemId);
        Assert.Equal(_plant["S"].ItemGuid, normal.DestinationItemId);
        Assert.Equal("NEW-A", normal.StorageDoorCode);
        Assert.Equal(1, _warehouse.Calls.Count(call => call == nameof(IWarehouse.ReserveStorageAsync)));
    }

    /// <summary>
    /// La ricerca raggruppata di una nuova cella (<c>FindNewStoreLocationGrouped</c>): la missione
    /// con il segno non prende il selettore lasciato dalla prima.
    /// </summary>
    [Fact]
    public async Task TheGroupedStoreSearchDoesNotGiveTheOperatorsMissionTheSelectorOfItsType()
    {
        var (normal, chosen) = await TwoMissionsOfTheSameTypeOnAGroup(operatorMissionFirst: false);

        Assert.True(await _plant["G"].StorageManager!.FindNewStoreLocationGroupedForTest().WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.Equal(_plant["S"].ItemGuid, normal.DestinationItemId);
        Assert.Equal(_plant["X"].ItemGuid, chosen.DestinationItemId);
        Assert.True(chosen.OperatorDestination);
        AssertSingleStep(chosen, "B", "X");
    }

    /// <summary>
    /// La ricerca raggruppata con la missione con il segno per prima: la sua destinazione non
    /// diventa il selettore dell'altra.
    /// </summary>
    [Fact]
    public async Task TheGroupedStoreSearchDoesNotTurnTheOperatorsChoiceIntoASelector()
    {
        var (normal, chosen) = await TwoMissionsOfTheSameTypeOnAGroup(operatorMissionFirst: true);

        Assert.True(await _plant["G"].StorageManager!.FindNewStoreLocationGroupedForTest().WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.Equal(_plant["X"].ItemGuid, chosen.DestinationItemId);
        Assert.Equal(_plant["S"].ItemGuid, normal.DestinationItemId);
        Assert.Equal("NEW-A", normal.StorageDoorCode);
        Assert.Equal(1, _warehouse.Calls.Count(call => call == nameof(IWarehouse.ReserveStorageAsync)));
    }

    /// <summary>
    /// La prova di controllo dei selettori: due UDC dello stesso tipo, nessuna con il segno. La
    /// prima chiede la cella al gestionale e lascia il selettore, la seconda lo prende - la porta
    /// NEW-A della prima, e il gestionale chiamato una volta sola - come oggi. Il gestionale avrebbe
    /// per la seconda un'altra porta, NEW-B: e' quella che avrebbe se il selettore non ci fosse.
    /// </summary>
    [Fact]
    public async Task WithoutTheMarkTheGroupedAllocationStillGivesTheSelectorToTheSameType()
    {
        var (first, second) = await TwoMissionsOfTheSameTypeOnAGroup(operatorMissionFirst: false, withTheMark: false);

        await _plant["G"].VerifyWhereAllocateLuGrouped([first, second]).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(_plant["S"].ItemGuid, first.DestinationItemId);
        Assert.Equal("NEW-A", first.StorageDoorCode);
        Assert.Equal(_plant["S"].ItemGuid, second.DestinationItemId);
        Assert.Equal("NEW-A", second.StorageDoorCode);
        Assert.Equal(1, _warehouse.Calls.Count(call => call == nameof(IWarehouse.ReserveStorageAsync)));
        AssertSingleStep(second, "B", "S");
    }

    /// <summary>
    /// Il deposito di gruppo dopo la ricerca per tipo di UDC (voce 39, compito 3: risposta 4 del
    /// proprietario del 2/10, chiarita sulla domanda del compito). La prima UDC ha la cella che il
    /// gestionale le ha prenotato (la 201, porta NEW-A), la seconda, dello stesso tipo, ne ha copiato
    /// la porta dal selettore. Il giro del gruppo lascia fuori il deposito della prima e la manda al
    /// navigatore per la conferma; la seconda, la cui porta non e' una prenotazione sua, non chiede
    /// niente al gestionale e non va al navigatore, ma aspetta con la prima, in qualunque ordine il
    /// gruppo le abbia scelte. Confermata la cella della prima, il giro dopo le deposita tutte e due
    /// nello stesso movimento, come prima del controllo. Prima della revisione finale la seconda
    /// partiva da sola nel primo giro, e il deposito di gruppo per tipo di UDC si spezzava in due
    /// movimenti, con la copia in una cella non ancora riconfermata.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheGroupedDepositAsksForTheCellTheGroupReservedAndTheCopiedDoorWaitsForIt(bool copyFirst)
    {
        var (first, second) = await TwoMissionsOfTheSameTypeOnAGroup(operatorMissionFirst: false, withTheMark: false);
        await _plant["G"].VerifyWhereAllocateLuGrouped([first, second]).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(first.StorageCellReserved);
        Assert.False(second.StorageCellReserved);
        Assert.Equal("NEW-A", second.StorageDoorCode);

        var group = _plant["G"];
        group.RecordStorageLength = RecordStorageTypes.FromPlc;
        group.Status = DeviceStatusTypes.Ready;
        var firstStep = Assert.Single(first.MissionSteps);
        var secondStep = Assert.Single(second.MissionSteps);
        List<MissionStep> chosen = copyFirst ? [secondStep, firstStep] : [firstStep, secondStep];
        RoutesToBuild.Instance.Items = new();
        _warehouse.Calls.Clear();
        Assert.NotNull(group.StepExecutor);

        await group.StepExecutor.ExecuteGroupedMOVE_LU_TO(chosen);

        Assert.Equal(Guid.Empty, firstStep.GroupedMovementId);
        Assert.Equal(Guid.Empty, secondStep.GroupedMovementId);
        Assert.Equal(Guid.Empty, group.RunningStepId);
        Assert.Same(first, RoutesToBuild.Instance[first.MissionId]);
        Assert.Null(RoutesToBuild.Instance[second.MissionId]);
        Assert.Same(secondStep, Assert.Single(second.MissionSteps));
        Assert.DoesNotContain(nameof(IWarehouse.GetStorageReservationAsync), _warehouse.Calls);

        RoutesToBuild.Instance.Items = new();
        await TheRouteLoopLooksForTheCell(first);
        Assert.Equal(1, _warehouse.Calls.Count(call => call == nameof(IWarehouse.GetStorageReservationAsync)));
        Assert.Same(firstStep, Assert.Single(first.MissionSteps));
        group.RunningStepId = Guid.Empty;
        group.Status = DeviceStatusTypes.Ready;

        await group.StepExecutor.ExecuteGroupedMOVE_LU_TO(chosen);

        Assert.NotEqual(Guid.Empty, firstStep.GroupedMovementId);
        Assert.Equal(group.RunningStepId, firstStep.GroupedMovementId);
        Assert.Equal(firstStep.GroupedMovementId, secondStep.GroupedMovementId);
    }

    /// <summary>
    /// Le copie si rifanno con la cella persa (risposta 8 del proprietario dell'8/10). La prima UDC ha
    /// la cella 201 (porta NEW-A), la seconda ne ha copiato la porta; il gestionale non tiene piu' la 201
    /// per la prima, e per lei ha la 202 (porta NEW-B). Il navigatore le prenota la 202, e la copia
    /// perde la porta NEW-A con lei: niente porta, indirizzo ne' rotta, e non e' una cella persa sua
    /// (la destinazione resta S). La ricerca per tipo del gruppo, che il ciclo del gruppo fa partire per
    /// le missioni a bordo senza porta, la rimette con la prima nella cella nuova, e il giro dopo le
    /// deposita tutte e due nello stesso movimento verso NEW-B. Prima la copia teneva NEW-A: la prima
    /// depositava nella 202, la copia da sola nella 201, che nessuna missione teneva piu'.
    /// </summary>
    [Fact]
    public async Task TheCopyOfALostCellLeavesItsDoorAndGoesWithTheMissionIntoTheNewCell()
    {
        var (first, second, _, secondStep) = await ACopyOfTheDoorOfACellTheWarehouseNoLongerHolds();
        var group = _plant["G"];

        await TheRouteLoopLooksForTheCell(first);

        Assert.Equal("NEW-B", first.StorageDoorCode);
        Assert.Equal(202, _warehouse.StorageReservationsByMission[first.MissionId].CellId);
        Assert.Null(second.StorageDoorCode);
        Assert.Equal(string.Empty, second.DestinationAsi);
        Assert.Empty(second.MissionSteps);
        Assert.DoesNotContain(secondStep, MissionSteps.Instance.List);
        Assert.False(second.StorageCellLost);
        Assert.False(second.StorageCellReserved);
        Assert.Equal(_plant["S"].ItemGuid, second.DestinationItemId);
        Assert.True(group.LUsWithNoStorage);
        Assert.False(_warehouse.StorageReservationsByMission.ContainsKey(second.MissionId));

        await group.VerifyWhereAllocateLuGrouped(group.LUsLoaded).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("NEW-B", second.StorageDoorCode);
        Assert.Equal("NEW-B", first.StorageDoorCode);
        AssertSingleStep(second, "B", "S");
        var firstStep = Assert.Single(first.MissionSteps);
        var copiedStep = Assert.Single(second.MissionSteps);
        group.RunningStepId = Guid.Empty;
        group.Status = DeviceStatusTypes.Ready;
        RoutesToBuild.Instance.Items = new();

        await group.StepExecutor!.ExecuteGroupedMOVE_LU_TO([firstStep, copiedStep]);

        Assert.NotEqual(Guid.Empty, firstStep.GroupedMovementId);
        Assert.Equal(group.RunningStepId, firstStep.GroupedMovementId);
        Assert.Equal(firstStep.GroupedMovementId, copiedStep.GroupedMovementId);
    }

    /// <summary>
    /// La stessa scena, con la modalita' <c>No</c>, ma il giro del gruppo cade mentre il navigatore
    /// aspetta il gestionale per la cella nuova della prima (terzo giro di correzioni del compito 12): sul
    /// campo e' il caso solito, perche' il ciclo del gruppo passa ogni <c>TimeLapseToExecuteStep</c>. La
    /// prima e' ferma, e la copia, che ha gia' lasciato la porta, resta ferma con lei: il giro non la
    /// conta e non la rifa'. Prenotata la 202 (NEW-B) per la prima, il giro dopo rimette la copia con lei
    /// nella cella nuova, senza una prenotazione sua. Il gestionale avrebbe per la copia la 203 (NEW-C):
    /// prima la copia ripartiva appena lasciata la porta, il giro che cadeva li' la contava senza la
    /// prima, e la copia si prenotava la 203 e depositava da sola.
    /// </summary>
    [Fact]
    public async Task AGroupTurnDuringTheNewReservationDoesNotRedoTheCopyWithoutTheMission()
    {
        var (first, second, _, _) = await ACopyOfTheDoorOfACellTheWarehouseNoLongerHolds(LookWhereAllocateLuTypes.No);
        var group = _plant["G"];

        var storage = _plant["S"];
        await PlantPersistence.SaveDoorMappingsAsync(storage.ItemGuid,
        [
            new DoorMapping { ItemId = storage.ItemGuid, DoorCode = "NEW-A", CellId = 201, Side = "A" },
            new DoorMapping { ItemId = storage.ItemGuid, DoorCode = "NEW-B", CellId = 202, Side = "A" },
            new DoorMapping { ItemId = storage.ItemGuid, DoorCode = "NEW-C", CellId = 203, Side = "A" }
        ]);
        _warehouse.Cells[203] = Cell(203, "NEW-C");
        _warehouse.CellPartitionMaps[203] = new CellPartitionMap(203, "0007", "0008",
            AsiDoorB: string.Empty, SubitemDoorB: string.Empty, [new CellPartition("0009", 0)]);
        _warehouse.StorageReservations[second.LuId] = new StorageReservation(second.MissionId,
            StorageReservationOutcome.CellFound, 203, "NEW-C", "0009", 100, NeedsCompaction: false);

        var asked = _warehouse.StorageRequests.Count;

        // La prima domanda di prenotazione dopo la scena e' quella del navigatore per la prima: si
        // tiene ferma finche' il giro del gruppo non e' passato.
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _warehouse.Before = async operation =>
        {
            if (operation != nameof(IWarehouse.ReserveStorageAsync) || reached.Task.IsCompleted) return;

            reached.TrySetResult();
            await gate.Task;
        };

        var method = typeof(Navigator).GetMethod("StartCellSearch", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        method!.Invoke(null, [first, first.PositionItem]);
        var search = Navigator.CellSearchInFlight(first.MissionId);
        Assert.NotNull(search);

        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(first.LuId, _warehouse.StorageRequests[^1].LoadUnitId);
            Assert.Null(second.StorageDoorCode);
            Assert.True(second.LeftACopiedDoor);

            await group.VerifyWhereAllocateLuGrouped(group.LUsLoaded).WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            gate.TrySetResult();
        }

        await search!.WaitAsync(TimeSpan.FromSeconds(30));
        _warehouse.Before = null;

        Assert.Null(second.StorageDoorCode);
        Assert.Empty(second.MissionSteps);
        Assert.True(second.LeftACopiedDoor);
        Assert.Equal("NEW-B", first.StorageDoorCode);
        Assert.Equal(202, _warehouse.StorageReservationsByMission[first.MissionId].CellId);

        await group.VerifyWhereAllocateLuGrouped(group.LUsLoaded).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("NEW-B", second.StorageDoorCode);
        AssertSingleStep(second, "B", "S");
        Assert.False(second.LeftACopiedDoor);
        Assert.False(_warehouse.StorageReservationsByMission.ContainsKey(second.MissionId));
        Assert.All(_warehouse.StorageRequests.Skip(asked), request => Assert.Equal(first.LuId, request.LoadUnitId));
        Assert.Equal("NEW-B", first.StorageDoorCode);
        AssertSingleStep(first, "A", "S");
    }

    /// <summary>
    /// La copia lascia la porta persa nel database del WCS prima della missione che ha prenotato la
    /// cella: sono due scritture di riga, e un host che si ferma fra le due deve ricaricare uno stato
    /// che converge. Prima le copie, con il segno: la missione, ricaricata con la porta, rivede persa la
    /// cella e le ferma di nuovo con lei. Prima la missione: ricaricata senza porta, e le copie con la
    /// porta persa e senza segno, che nessuna missione tratteneva piu'.
    /// </summary>
    [Fact]
    public async Task TheCopyLeavesTheLostDoorInTheDatabaseBeforeTheMission()
    {
        var (first, second, _, _) = await ACopyOfTheDoorOfACellTheWarehouseNoLongerHolds();
        var writes = new List<(Guid Mission, string? Door)>();
        ORM.PathMission.SondaScrittura = written =>
        {
            lock (writes) writes.Add((written.MissionId, written.StorageDoorCode));
        };

        try
        {
            await TheRouteLoopLooksForTheCell(first);
        }
        finally
        {
            ORM.PathMission.SondaScrittura = null;
        }

        Assert.Equal("NEW-B", first.StorageDoorCode);
        Assert.Null(second.StorageDoorCode);
        var copyLeft = writes.FindIndex(write => write.Mission == second.MissionId && write.Door == null);
        var missionLeft = writes.FindIndex(write => write.Mission == first.MissionId && write.Door == null);
        Assert.True(copyLeft >= 0, "la copia non ha scritto la porta vuota");
        Assert.True(missionLeft >= 0, "la missione non ha scritto la porta vuota");
        Assert.True(copyLeft < missionLeft, $"la copia ha scritto la porta vuota alla riga {copyLeft}, la missione alla {missionLeft}");
    }

    /// <summary>
    /// La stessa scena, ma quando il navigatore trova la cella persa un ciclo del gruppo sta avviando il
    /// deposito della copia (l'avvio e' contato, e la copia non si puo' fermare). Il navigatore non tocca
    /// niente: la prima tiene porta, cella e rotta, la copia la porta NEW-A, nessuna prenotazione, e la
    /// missione torna in coda al navigatore. Finito l'avvio, il giro dopo del navigatore le rifa' tutte
    /// e due. Senza fermare le copie la prima prendeva la 202 e la copia restava verso NEW-A.
    /// </summary>
    [Fact]
    public async Task ACopyThatCannotBeStoppedLeavesTheLostCellAsItIsUntilTheNextTurn()
    {
        var (first, second, firstStep, secondStep) = await ACopyOfTheDoorOfACellTheWarehouseNoLongerHolds();
        Assert.True(second.TryBeginStepStart());

        // La riga del rifiuto e' un'informazione: si scrive con la diagnostica accesa.
        var previousDebugLog = Navigator.ActivateDebugLog;
        Navigator.ActivateDebugLog = true;
        var log = new RecordingConsole();
        IReadOnlyList<string> lines;

        try
        {
            await TheRouteLoopLooksForTheCell(first);
        }
        finally
        {
            lines = log.Lines;
            log.Dispose();
            Navigator.ActivateDebugLog = previousDebugLog;
            second.EndStepStart();
        }

        // L'esito del rifiuto e' quello della copia che si muove, come per il cambio dell'operatore, non lo
        // step in volo della missione stessa (revisione finale del quarto giro).
        Assert.Single(lines, line => line.Contains($"Missione {first.MissionId}: il navigatore non le ha rifatto la cella") &&
                                         line.Contains("(CopyInFlight)") &&
                                         line.Contains($"la missione {second.MissionId}, che ne ha copiato la porta, non si puo' fermare"));

        Assert.Equal("NEW-A", first.StorageDoorCode);
        Assert.Same(firstStep, Assert.Single(first.MissionSteps));
        Assert.Equal("NEW-A", second.StorageDoorCode);
        Assert.Same(secondStep, Assert.Single(second.MissionSteps));
        Assert.DoesNotContain(nameof(IWarehouse.ReserveStorageAsync), _warehouse.Calls);
        Assert.False(_warehouse.StorageReservationsByMission.ContainsKey(first.MissionId));
        Assert.Same(first, RoutesToBuild.Instance[first.MissionId]);

        RoutesToBuild.Instance.Items = new();
        await TheRouteLoopLooksForTheCell(first);

        Assert.Equal("NEW-B", first.StorageDoorCode);
        Assert.Null(second.StorageDoorCode);
        Assert.Empty(second.MissionSteps);
    }

    /// <summary>
    /// La stessa scena, ma la copia ha uno step in volo quando il navigatore trova la cella persa (qui il
    /// suo B->S, con il gettone su B: conta solo che sia in volo). Fermarla riesce - il cambio guarda
    /// gli avvii, non gli step gia' partiti - ma la sua porta non si puo' lasciare. Il navigatore non
    /// tocca niente: la prima tiene NEW-A e la rotta, la copia anche, nessuna prenotazione, e la
    /// missione riprova con il freno. Atterrato lo step, il giro dopo le rifa' tutte e due. Prima la
    /// prima lasciava la 201 e prendeva la 202, mentre la copia, che non poteva lasciare la porta,
    /// restava verso NEW-A: finito lo step, nessuna missione con il segno aveva piu' quella porta, il
    /// giro del gruppo non la tratteneva, e la copia depositava da sola nella 201 (primo giro di
    /// correzioni del compito 12).
    /// </summary>
    [Fact]
    public async Task ACopyWithAStepInFlightLeavesTheLostCellAsItIsUntilTheNextTurn()
    {
        var (first, second, firstStep, secondStep) = await ACopyOfTheDoorOfACellTheWarehouseNoLongerHolds();
        var carrier = _plant["B"];
        carrier.RunningStepId = secondStep.StepId;
        Assert.Same(secondStep, second.StepInFlight());

        try
        {
            await TheRouteLoopLooksForTheCell(first);
        }
        finally
        {
            carrier.RunningStepId = Guid.Empty;
        }

        Assert.Equal("NEW-A", first.StorageDoorCode);
        Assert.Same(firstStep, Assert.Single(first.MissionSteps));
        Assert.False(first.StorageCellLost);
        Assert.Equal("NEW-A", second.StorageDoorCode);
        Assert.Same(secondStep, Assert.Single(second.MissionSteps));
        Assert.DoesNotContain(nameof(IWarehouse.ReserveStorageAsync), _warehouse.Calls);
        Assert.False(_warehouse.StorageReservationsByMission.ContainsKey(first.MissionId));
        Assert.Same(first, RoutesToBuild.Instance[first.MissionId]);

        RoutesToBuild.Instance.Items = new();
        await TheRouteLoopLooksForTheCell(first);

        Assert.Equal("NEW-B", first.StorageDoorCode);
        Assert.Null(second.StorageDoorCode);
        Assert.Empty(second.MissionSteps);
    }

    /// <summary>
    /// La copia che lascia la porta si rifa' con la ricerca per tipo del gruppo in ogni modalita' di
    /// deposito del gruppo, non solo in quella per tipo di UDC (primo giro di correzioni del compito
    /// 12). La copia la fa la ricerca di una cella nuova dopo un rifiuto del PLC
    /// (<c>FindNewStoreLocationGrouped</c>), che cerca per tipo qualunque sia la modalita'; poi la prima
    /// perde la 201 e il navigatore le prenota la 202 (porta NEW-B), e la copia resta senza porta. Il
    /// giro del gruppo la rifa': prende la porta della prima, nella cella nuova, e la rotta B->S, senza
    /// una prenotazione sua e senza toccare la prima. Prima, fuori dalla modalita' per tipo di UDC, la
    /// copia restava senza porta: con <c>No</c> nessuno la cercava, con le altre il giro del gruppo
    /// tornava a ogni passata nella ricerca della sua modalita', che non la guarda, e il gruppo restava
    /// li'.
    /// </summary>
    [Theory]
    [InlineData(LookWhereAllocateLuTypes.No)]
    [InlineData(LookWhereAllocateLuTypes.BuildRouteToStore)]
    [InlineData(LookWhereAllocateLuTypes.BuildRouteToStoreForcing)]
    [InlineData(LookWhereAllocateLuTypes.BuildRouteToDifferentStoreLocation)]
    [InlineData(LookWhereAllocateLuTypes.BuildRouteToStoreByIdLuType)]
    public async Task TheGroupTurnRedoesACopyThatLeftItsDoorInEveryMode(LookWhereAllocateLuTypes mode)
    {
        var (first, second, _, _) = await ACopyOfTheDoorOfACellTheWarehouseNoLongerHolds(mode);
        var group = _plant["G"];
        await TheRouteLoopLooksForTheCell(first);
        Assert.Equal("NEW-B", first.StorageDoorCode);
        AssertSingleStep(first, "A", "S");
        Assert.Null(second.StorageDoorCode);
        Assert.Empty(second.MissionSteps);
        _warehouse.Calls.Clear();

        // Il giro del gruppo, come lo fa il suo ciclo in Run; il ciclo di sfondo non nasce.
        group.GroupLoadingActive = true;
        group.GroupManager!.CancellationGroupExStepTokenSource = new CancellationTokenSource();
        group.Status = DeviceStatusTypes.Ready;
        Navigator.NavStatus = NavigatorStatusTypes.Run;

        try
        {
            await group.GroupManager.GroupExecuteStep();
        }
        finally
        {
            Navigator.NavStatus = NavigatorStatusTypes.Stop;
        }

        Assert.Equal("NEW-B", second.StorageDoorCode);
        Assert.Equal(_plant["S"].ItemGuid, second.DestinationItemId);
        AssertSingleStep(second, "B", "S");
        Assert.False(second.StorageCellReserved);
        Assert.False(_warehouse.StorageReservationsByMission.ContainsKey(second.MissionId));
        Assert.DoesNotContain(nameof(IWarehouse.ReserveStorageAsync), _warehouse.Calls);
        Assert.Equal("NEW-B", first.StorageDoorCode);
        AssertSingleStep(first, "A", "S");
        Assert.Equal(202, _warehouse.StorageReservationsByMission[first.MissionId].CellId);
        Assert.False(group.LUsWithNoStorage);
    }

    /// <summary>
    /// La copia che ha lasciato la porta non prende il selettore di una missione che il giro del gruppo
    /// non ha contato (secondo giro di correzioni del compito 12). La prima ha la 202 (NEW-B), ma qualcuno
    /// la sta cambiando - il navigatore che ne ha visto persa la cella la ferma cosi' - e la sua porta
    /// puo' sparire durante il giro, dopo che <c>StopTheCopiesOfTheDoor</c> ha guardato le copie e saltato
    /// quella senza porta. La copia cerca la sua cella: per lei il gestionale ha la 203 (NEW-C). Prima il
    /// selettore veniva da ogni missione con la cella prenotata, anche da quella ferma: la copia prendeva
    /// NEW-B senza chiedere niente, e se il navigatore poi la lasciava ci depositava da sola, senza segno
    /// ne' una missione con quella porta che la trattenesse.
    /// </summary>
    [Fact]
    public async Task TheCopyDoesNotTakeTheDoorOfAMissionThatIsBeingChanged()
    {
        var (first, second, _, _) = await ACopyOfTheDoorOfACellTheWarehouseNoLongerHolds();
        var group = _plant["G"];
        await TheRouteLoopLooksForTheCell(first);
        Assert.Equal("NEW-B", first.StorageDoorCode);
        Assert.True(first.StorageCellReserved);
        Assert.True(second.LeftACopiedDoor);

        var storage = _plant["S"];
        await PlantPersistence.SaveDoorMappingsAsync(storage.ItemGuid,
        [
            new DoorMapping { ItemId = storage.ItemGuid, DoorCode = "NEW-A", CellId = 201, Side = "A" },
            new DoorMapping { ItemId = storage.ItemGuid, DoorCode = "NEW-B", CellId = 202, Side = "A" },
            new DoorMapping { ItemId = storage.ItemGuid, DoorCode = "NEW-C", CellId = 203, Side = "A" }
        ]);
        _warehouse.Cells[203] = Cell(203, "NEW-C");
        _warehouse.CellPartitionMaps[203] = new CellPartitionMap(203, "0007", "0008",
            AsiDoorB: string.Empty, SubitemDoorB: string.Empty, [new CellPartition("0009", 0)]);
        _warehouse.StorageReservations[second.LuId] = new StorageReservation(second.MissionId,
            StorageReservationOutcome.CellFound, 203, "NEW-C", "0009", 100, NeedsCompaction: false);
        _warehouse.Calls.Clear();
        Assert.Equal(PathMission.OperatorChangeStart.Begun, first.TryBeginOperatorChange());

        try
        {
            await group.VerifyWhereAllocateLuGrouped(group.LUsLoaded).WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            first.EndOperatorChange();
        }

        Assert.Equal("NEW-C", second.StorageDoorCode);
        Assert.Equal(203, _warehouse.StorageReservationsByMission[second.MissionId].CellId);
        AssertSingleStep(second, "B", "S");
        Assert.False(second.LeftACopiedDoor);
        Assert.Equal("NEW-B", first.StorageDoorCode);
        Assert.Equal(202, _warehouse.StorageReservationsByMission[first.MissionId].CellId);
    }

    /// <summary>
    /// La copia che ha lasciato la porta si rifa' anche dopo un riavvio (secondo giro di correzioni del
    /// compito 12). Il navigatore lascia la porta della prima e della copia, poi l'host si ferma prima che
    /// il gruppo rifaccia la copia: la copia torna dalla sua riga del database del WCS, senza porta. Con
    /// il segno nella riga la ricarica non le costruisce la rotta verso il magazzino nudo, e il giro del
    /// gruppo la rifa' con la porta della prima, nella cella nuova. Prima il segno stava solo in memoria:
    /// la copia tornava senza porta e senza segno, con la modalita' <c>No</c> nessuno la cercava, con
    /// <c>BuildRouteToStore</c> il giro del gruppo tornava a ogni passata nella ricerca della sua
    /// modalita', che guarda solo le missioni con la porta.
    /// </summary>
    [Theory]
    [InlineData(LookWhereAllocateLuTypes.No)]
    [InlineData(LookWhereAllocateLuTypes.BuildRouteToStore)]
    public async Task ACopyThatLeftItsDoorIsStillRedoneAfterARestart(LookWhereAllocateLuTypes mode)
    {
        var (first, second, _, _) = await ACopyOfTheDoorOfACellTheWarehouseNoLongerHolds(mode);
        var group = _plant["G"];
        await TheRouteLoopLooksForTheCell(first);
        Assert.Equal("NEW-B", first.StorageDoorCode);
        Assert.Null(second.StorageDoorCode);

        // Il riavvio: la copia torna dalla sua riga, come la ricarica (DbInfo.LoadMissions).
        Mission row;
        await using (var reading = _database.CreateContext())
            row = await reading.Missions.SingleAsync(mission => mission.MissionId == second.MissionId);
        Assert.Null(row.StorageDoorCode);
        var reloaded = MissionStore.ToPathMission(row);
        ActiveMissions.Instance.RemoveAll(mission => mission.MissionId == second.MissionId);
        Navigator.NavStatus = NavigatorStatusTypes.Recover;
        ActiveMissions.Instance.Add(reloaded, buildRoute: false);
        await reloaded.PositionWork;
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        Assert.Equal(_plant["B"].ItemGuid, reloaded.PositionItemId);
        Assert.Equal(_plant["S"].ItemGuid, reloaded.DestinationItemId);

        // La rotta che la ricarica costruisce (ActiveMissions.Add con la rotta).
        await reloaded.BuildPathToDestination();
        Assert.Empty(reloaded.MissionSteps);
        _warehouse.Calls.Clear();

        // Il giro del gruppo, come lo fa il suo ciclo in Run; il ciclo di sfondo non nasce.
        group.GroupLoadingActive = true;
        group.GroupManager!.CancellationGroupExStepTokenSource = new CancellationTokenSource();
        group.Status = DeviceStatusTypes.Ready;
        Navigator.NavStatus = NavigatorStatusTypes.Run;

        try
        {
            await group.GroupManager.GroupExecuteStep();
        }
        finally
        {
            Navigator.NavStatus = NavigatorStatusTypes.Stop;
        }

        Assert.Equal("NEW-B", reloaded.StorageDoorCode);
        AssertSingleStep(reloaded, "B", "S");
        Assert.False(reloaded.LeftACopiedDoor);
        Assert.False(_warehouse.StorageReservationsByMission.ContainsKey(reloaded.MissionId));
        Assert.DoesNotContain(nameof(IWarehouse.ReserveStorageAsync), _warehouse.Calls);
        Assert.Equal("NEW-B", first.StorageDoorCode);
        AssertSingleStep(first, "A", "S");
    }

    /// <summary>
    /// La copia che ha lasciato la porta e non ne trova un'altra non tiene il ciclo del gruppo nella sua
    /// ricerca. Con la modalita' <c>No</c>: la prima, che ha la cella nuova, la sta cambiando qualcuno e
    /// non da' il selettore, e il gestionale non ha celle per la copia. Il primo giro chiede al gestionale
    /// e lo dice con una riga di avviso; il giro dopo non chiede piu' e va avanti, finche' il freno non
    /// scade; scaduto, la ricerca si riprova. Prima ogni giro tornava nella ricerca, chiedeva di nuovo al
    /// gestionale e non arrivava mai ai trasferimenti e alle estrazioni, senza una riga: con la modalita'
    /// <c>No</c> era nuovo del compito 12.
    /// </summary>
    [Fact]
    public async Task ACopyThatFindsNoDoorLetsTheGroupTurnGoOnUntilItsBrakeRunsOut()
    {
        using var log = new RecordingLogWriter();
        var (first, second, _, _) = await ACopyOfTheDoorOfACellTheWarehouseNoLongerHolds(LookWhereAllocateLuTypes.No);
        var group = _plant["G"];
        await TheRouteLoopLooksForTheCell(first);
        Assert.Equal("NEW-B", first.StorageDoorCode);
        Assert.True(second.LeftACopiedDoor);
        var clock = new ManualClock();
        group.GroupManager!.CopiedDoorRetries = new RetryBackoff(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30), clock);
        _warehouse.StorageReservations[second.LuId] = new StorageReservation(Guid.Empty,
            StorageReservationOutcome.NoFreeCells, 0, string.Empty, string.Empty, 0, NeedsCompaction: false);
        _warehouse.Calls.Clear();

        int Asked() => _warehouse.Calls.Count(call => call == nameof(IWarehouse.ReserveStorageAsync));

        // Il giro del gruppo, come lo fa il suo ciclo in Run; il ciclo di sfondo non nasce.
        group.GroupLoadingActive = true;
        group.GroupManager.CancellationGroupExStepTokenSource = new CancellationTokenSource();
        group.Status = DeviceStatusTypes.Ready;
        Assert.Equal(PathMission.OperatorChangeStart.Begun, first.TryBeginOperatorChange());
        Navigator.NavStatus = NavigatorStatusTypes.Run;

        try
        {
            await group.GroupManager.GroupExecuteStep();
            var asked = Asked();
            Assert.True(asked > 0);
            Assert.True(second.LeftACopiedDoor);

            await group.GroupManager.GroupExecuteStep();
            Assert.Equal(asked, Asked());

            clock.Advance(TimeSpan.FromSeconds(2));
            await group.GroupManager.GroupExecuteStep();
            Assert.True(Asked() > asked);
        }
        finally
        {
            Navigator.NavStatus = NavigatorStatusTypes.Stop;
            first.EndOperatorChange();
        }

        Assert.True(second.LeftACopiedDoor);
        await log.Wait((line, level) => level == Log.Interface.LogEntryType.Warning &&
                                        line.Contains(second.MissionId.ToString()) &&
                                        line.Contains("non ha trovato una porta"))
            .WaitAsync(TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// La missione che ha prenotato la cella resta fuori dal giro del gruppo perche' qualcuno la sta
    /// cambiando - il navigatore che le riguarda la cella la ferma cosi' - e le copie della sua porta
    /// aspettano con lei: il giro non deposita niente. Prima la copia partiva da sola verso NEW-A
    /// mentre il navigatore poteva scoprire che la cella era persa.
    /// </summary>
    [Fact]
    public async Task TheCopyWaitsWhileTheMissionThatReservedTheCellIsBeingChanged()
    {
        var (first, second) = await TwoMissionsOfTheSameTypeOnAGroup(operatorMissionFirst: false, withTheMark: false);
        await _plant["G"].VerifyWhereAllocateLuGrouped([first, second]).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("NEW-A", second.StorageDoorCode);
        var group = _plant["G"];
        group.RecordStorageLength = RecordStorageTypes.FromPlc;
        group.Status = DeviceStatusTypes.Ready;
        var firstStep = Assert.Single(first.MissionSteps);
        var secondStep = Assert.Single(second.MissionSteps);
        Assert.Equal(PathMission.OperatorChangeStart.Begun, first.TryBeginOperatorChange());

        try
        {
            await group.StepExecutor!.ExecuteGroupedMOVE_LU_TO([firstStep, secondStep]);
        }
        finally
        {
            first.EndOperatorChange();
        }

        Assert.Equal(Guid.Empty, firstStep.GroupedMovementId);
        Assert.Equal(Guid.Empty, secondStep.GroupedMovementId);
        Assert.Equal(Guid.Empty, group.RunningStepId);
    }

    /// <summary>
    /// Le copie si rifanno quando l'operatore cambia la cella o la destinazione della missione che
    /// l'aveva prenotata (punto 3 dell'8/10 sera, compito 15). La prima UDC ha la cella 201 (porta
    /// NEW-A), la seconda ne ha copiato la porta. L'operatore manda la prima di nuovo in S, che e'
    /// chiedere un'altra cella (il gestionale le da' la 202, porta NEW-B), o su X, fuori dal magazzino.
    /// La copia lascia la porta NEW-A: niente porta, indirizzo ne' rotta, il segno della copia, e nessuna
    /// prenotazione sua; la rifa' la ricerca per tipo del gruppo. Ed e' ripartita: un cambio la puo'
    /// fermare di nuovo. Prima teneva NEW-A e la rotta B->S, e a fermo finito ci depositava da sola,
    /// nella 201 che nessuna missione teneva piu'.
    /// </summary>
    [Theory]
    [InlineData("S")]
    [InlineData("X")]
    public async Task TheCopyLeavesTheDoorWhenTheOperatorChangesTheCellOfTheMission(string destination)
    {
        var (first, second, secondStep) = await ACopyOfTheDoorOfTheFirst();
        _plant.Link("A", "X");
        _warehouse.StorageReservations[first.LuId] = new StorageReservation(first.MissionId,
            StorageReservationOutcome.CellFound, 202, "NEW-B", "0006", 100, NeedsCompaction: false);

        var result = await MissionGateway.ChangeDestinationAsync(first.MissionId, _plant[destination].ItemGuid);

        Assert.Equal(MissionDestinationOutcome.Changed, result.Outcome);
        Assert.Equal(destination == "S" ? "NEW-B" : null, first.StorageDoorCode);
        Assert.Null(second.StorageDoorCode);
        Assert.Equal(string.Empty, second.DestinationAsi);
        Assert.Empty(second.MissionSteps);
        Assert.DoesNotContain(secondStep, MissionSteps.Instance.List);
        Assert.True(second.LeftACopiedDoor);
        Assert.False(second.StorageCellLost);
        Assert.Equal(_plant["S"].ItemGuid, second.DestinationItemId);
        Assert.False(_warehouse.StorageReservationsByMission.ContainsKey(second.MissionId));
        Assert.Equal(PathMission.OperatorChangeStart.Begun, second.TryBeginOperatorChange());
        second.EndOperatorChange();
    }

    /// <summary>
    /// La stessa scena, ma la missione della prima UDC esce dal motore (<c>ActiveMissions.Remove</c>,
    /// come la toglie l'operatore da un dispositivo): la cella 201 non e' piu' di nessuna missione, e la
    /// copia lascia la porta NEW-A prima che la prima esca, ed e' ripartita. Prima la teneva, con la
    /// rotta B->S, e ci depositava da sola.
    /// </summary>
    [Fact]
    public async Task TheCopyLeavesTheDoorWhenTheMissionIsRemoved()
    {
        var (first, second, secondStep) = await ACopyOfTheDoorOfTheFirst();

        Assert.True(await ActiveMissions.Instance.Remove(first));

        Assert.Null(ActiveMissions.Instance[first.MissionId]);
        Assert.Null(second.StorageDoorCode);
        Assert.Empty(second.MissionSteps);
        Assert.DoesNotContain(secondStep, MissionSteps.Instance.List);
        Assert.True(second.LeftACopiedDoor);
        Assert.False(second.StorageCellLost);
        Assert.Equal(PathMission.OperatorChangeStart.Begun, second.TryBeginOperatorChange());
        second.EndOperatorChange();
    }

    /// <summary>
    /// La stessa rimozione mentre un ciclo del gruppo sta avviando il deposito della copia (l'avvio e'
    /// contato, e la copia non si puo' fermare): la rimozione si rifiuta, come per uno step in volo della
    /// missione, e niente e' toccato. Finito l'avvio, la rimozione riprovata la fa. Senza fermare la copia
    /// la prima usciva, e la copia restava verso NEW-A.
    /// </summary>
    [Fact]
    public async Task AMissionWhoseCopyCannotBeStoppedIsNotRemovedUntilItCan()
    {
        var (first, second, secondStep) = await ACopyOfTheDoorOfTheFirst();
        Assert.True(second.TryBeginStepStart());

        try
        {
            Assert.False(await ActiveMissions.Instance.Remove(first));
        }
        finally
        {
            second.EndStepStart();
        }

        Assert.Same(first, ActiveMissions.Instance[first.MissionId]);
        Assert.Equal("NEW-A", first.StorageDoorCode);
        Assert.Equal("NEW-A", second.StorageDoorCode);
        Assert.Same(secondStep, Assert.Single(second.MissionSteps));

        Assert.True(await ActiveMissions.Instance.Remove(first));
        Assert.Null(second.StorageDoorCode);
    }

    /// <summary>
    /// Lo scarico dell'operatore («Unload LU») della prima UDC dal dispositivo A, mentre un ciclo del gruppo
    /// sta avviando il deposito della copia su B (risposta del 9/10 alla domanda del compito 17). Il
    /// comando si chiude fatto, e il rilascio passa dal gestionale (l'outbox, Release); la rimozione che lo
    /// completa si rifiuta perche' la copia non si ferma, con le parole nuove nella riga del registro, e la
    /// riga dell'outbox resta. Finito l'avvio, il giro dopo dell'outbox (quello che fa il navigatore a ogni
    /// giro, <c>Navigator.MasterProcess</c>) rimanda la stessa operazione e la rimozione passa: la prima
    /// esce, e la copia lascia la porta NEW-A. Se la rimozione rifiutata chiudesse la riga dell'outbox, la
    /// prima resterebbe nel motore per sempre.
    /// </summary>
    [Fact]
    public async Task AnUnloadRefusedForAMovingCopyIsRetriedByTheOutboxOnceTheCopyHasStarted()
    {
        var (first, second, secondStep) = await ACopyOfTheDoorOfTheFirst();
        Navigator.WarehouseTransitionDispatcher.Reset();
        using var log = new RecordingLogWriter();
        _plant["A"].Status = DeviceStatusTypes.Ready;
        var unload = new Command(loadingFromDatabase: true)
        {
            Date = DateTime.Now,
            CommandType = Command.CommandTypes.ItemUnloadLu,
            Status = Command.StatusTypes.ToDo,
            ItemCommandId = _plant["A"].ItemGuid
        };
        Assert.True(second.TryBeginStepStart());

        try
        {
            await ExecutableCommands.ItemUnloadLu(unload);
        }
        finally
        {
            second.EndStepStart();
        }

        Assert.Equal(Command.StatusTypes.Done, unload.Status);
        Assert.Same(first, ActiveMissions.Instance[first.MissionId]);
        Assert.Equal("NEW-A", second.StorageDoorCode);
        Assert.Same(secondStep, Assert.Single(second.MissionSteps));
        var pending = Assert.Single(await ORM.WarehouseTransitionOutbox.DueAsync(DateTimeOffset.MaxValue, 10));
        Assert.Equal(WarehouseTransitionKind.Release, pending.Kind);
        Assert.Equal(first.MissionId, pending.MissionId);
        await log.Wait((line, level) => level == Log.Interface.LogEntryType.Warning &&
                                        line.Contains($"Rimozione della missione {first.MissionId} rifiutata") &&
                                        line.Contains("another load unit sharing its cell is moving") &&
                                        line.Contains(second.MissionId.ToString()))
            .WaitAsync(TimeSpan.FromSeconds(30));

        await Navigator.WarehouseTransitionDispatcher.TryDispatchAsync();

        Assert.Null(ActiveMissions.Instance[first.MissionId]);
        Assert.Empty(await ORM.WarehouseTransitionOutbox.DueAsync(DateTimeOffset.MaxValue, 10));
        Assert.Equal(2, _warehouse.ReleasedLoadUnits.Count);
        Assert.All(_warehouse.ReleasedLoadUnits, request => Assert.Equal(pending.OperationId, request.OperationId));
        Assert.Null(second.StorageDoorCode);
        Assert.True(second.LeftACopiedDoor);
    }

    /// <summary>
    /// La rimozione mentre un ciclo del gruppo ha gia' contato la missione che esce (primo giro di
    /// correzioni del compito 15). La prima UDC ha la porta NEW-A della 201, la seconda non ha ancora una
    /// porta, e non e' una copia. Il ciclo del gruppo le conta tutte e due e aspetta il gestionale, che
    /// riprenota la 201 per la prima; intanto l'operatore la toglie. La rimozione si rifiuta, come per una
    /// copia che non si ferma, da riprovare: il ciclo lavora ancora sulla prima, e la sua porta la darebbe
    /// alla seconda. Finito il ciclo, la seconda e' una copia della NEW-A, e la rimozione riprovata
    /// gliela fa lasciare. Prima la prima usciva e restituiva la prenotazione, e il ciclo ripartiva su una
    /// missione gia' fuori: riprenotava la 201 per lei, e le dava la porta da prestare alla seconda (con
    /// la cella collegata di A, come qui, cadeva prima, senza posizione).
    /// </summary>
    [Fact]
    public async Task AMissionThatAGroupCycleHasCountedIsNotRemovedUntilTheCycleIsOver()
    {
        var (first, second) = await TwoMissionsOfTheSameTypeOnAGroup(operatorMissionFirst: false, withTheMark: false);
        await _plant["G"].VerifyWhereAllocateLuGrouped([first]).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(first.StorageCellReserved);
        Assert.Equal("NEW-A", first.StorageDoorCode);
        Assert.Equal(string.Empty, second.StorageDoorCode);
        Assert.False(second.LeftACopiedDoor);

        // La prenotazione della 201 e' scaduta: il ciclo la chiede di nuovo per la prima, e si ferma li'.
        _warehouse.RevokeStorageReservation(first.MissionId);
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _warehouse.Before = async operation =>
        {
            if (operation == nameof(IWarehouse.ReserveStorageAsync) && asked.TrySetResult())
                await answer.Task;
        };

        var cycle = _plant["G"].VerifyWhereAllocateLuGrouped([first, second]);
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(30));

        bool removedDuringTheCycle;
        Exception? cycleFailure;
        try
        {
            removedDuringTheCycle = await ActiveMissions.Instance.Remove(first);
        }
        finally
        {
            answer.TrySetResult();
            cycleFailure = await Record.ExceptionAsync(() => cycle.WaitAsync(TimeSpan.FromSeconds(30)));
        }

        Assert.False(removedDuringTheCycle);
        Assert.Null(cycleFailure);
        Assert.Same(first, ActiveMissions.Instance[first.MissionId]);
        Assert.Equal("NEW-A", second.StorageDoorCode);

        Assert.True(await ActiveMissions.Instance.Remove(first));
        Assert.Null(second.StorageDoorCode);
        Assert.Empty(second.MissionSteps);
        Assert.True(second.LeftACopiedDoor);
    }

    /// <summary>
    /// Un ciclo del gruppo che ha letto i carichi prima della rimozione, e conta le missioni solo dopo che
    /// la prima e' uscita (secondo giro di correzioni del compito 15). La prima ha la 201 (porta NEW-A), la
    /// seconda ne e' copia; l'operatore toglie la prima, che restituisce la 201, e la seconda lascia la
    /// porta. Il ciclo parte con la lista letta prima, che contiene ancora la prima: la prima resta fuori
    /// dal giro, e la seconda, che non ha piu' una missione del suo tipo con la cella, chiede la sua al
    /// gestionale, la 202 (NEW-B). Prima la rimozione rilasciava la prima come una missione viva, il ciclo
    /// la contava e la usava come selettore: la seconda prendeva di nuovo la NEW-A, copia di una missione
    /// uscita, e ci avrebbe depositato da sola in una cella che nessuna missione teneva piu'. Il difetto lo
    /// vedono la porta NEW-B e la prenotazione 202 della seconda; che la prima resti senza prenotazione e'
    /// lo stato atteso, ma passava anche prima (revisione finale del terzo giro).
    /// </summary>
    [Fact]
    public async Task AGroupCycleThatReadTheLoadsBeforeTheRemovalLeavesTheRemovedMissionOut()
    {
        var (first, second, _) = await ACopyOfTheDoorOfTheFirst();
        List<PathMission?> readBeforeTheRemoval = [first, second];

        Assert.True(await ActiveMissions.Instance.Remove(first));
        Assert.False(_warehouse.StorageReservationsByMission.ContainsKey(first.MissionId));
        Assert.Null(second.StorageDoorCode);
        Assert.True(second.LeftACopiedDoor);

        await _plant["G"].VerifyWhereAllocateLuGrouped(readBeforeTheRemoval).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(_warehouse.StorageReservationsByMission.ContainsKey(first.MissionId));
        Assert.Equal("NEW-B", second.StorageDoorCode);
        Assert.Equal(202, _warehouse.StorageReservationsByMission[second.MissionId].CellId);
        Assert.False(second.LeftACopiedDoor);
        AssertSingleStep(second, "B", "S");
    }

    /// <summary>
    /// La rimozione guarda lo step in volo della missione prima di fermarla; uno step partito fra lo
    /// sguardo e l'arresto si vede solo dopo l'arresto (revisione finale del terzo giro). Qui lo step A->S
    /// della prima e' gia' in volo quando la si ferma per far lasciare la porta alla copia: la missione
    /// non esce, nessuno resta fermo, e la copia tiene la porta NEW-A e la sua rotta. Prima la missione si
    /// fermava, la copia lasciava la porta, e la rimozione toglieva la rotta di uno step in movimento.
    /// </summary>
    [Fact]
    public async Task AMissionWhoseStepSetsOffWhileItIsStoppedForTheRemovalLetsNothingGo()
    {
        var (first, second, secondStep) = await ACopyOfTheDoorOfTheFirst();
        var firstStep = Assert.Single(first.MissionSteps);
        _plant["A"].RunningStepId = firstStep.StepId;
        _plant["A"].Status = DeviceStatusTypes.Moving;

        Assert.False(MissionGateway.TheCopiesLeaveTheDoorOfAMissionThatLeaves(first, out var held, out var why));

        Assert.Empty(held);
        Assert.Contains(firstStep.StepId.ToString(), why);
        Assert.DoesNotContain("another load unit", why);
        Assert.Equal("NEW-A", second.StorageDoorCode);
        Assert.Same(secondStep, Assert.Single(second.MissionSteps));
        Assert.False(second.LeftACopiedDoor);
        ChangeDestinationStepStartsTest.NoStepStartIsLeftCounted(first);
        ChangeDestinationStepStartsTest.NoStepStartIsLeftCounted(second);
    }

    /// <summary>
    /// La copia lascia la porta della missione che esce, e la scrittura di quel cambio solleva (qui il
    /// gestore degli eventi del motore, chiamato quando la copia lascia la porta): la rimozione risale
    /// con l'eccezione, e la missione e la copia ripartono, un cambio le puo' fermare di nuovo. Prima
    /// restavano ferme per sempre: la rimozione apriva il suo try solo dopo aver fatto lasciare la porta
    /// alle copie (revisione finale del terzo giro).
    /// </summary>
    [Fact]
    public async Task AFailureWhileTheCopiesLeaveTheDoorOfARemovedMissionLeavesNobodyStopped()
    {
        var (first, second, _) = await ACopyOfTheDoorOfTheFirst();
        Navigator.EventSink = new ThrowsWhenTheMissionChanges(second);

        await Assert.ThrowsAsync<InvalidOperationException>(() => ActiveMissions.Instance.Remove(first))
            .WaitAsync(TimeSpan.FromSeconds(30));
        Navigator.EventSink = null;

        Assert.Same(first, ActiveMissions.Instance[first.MissionId]);
        ChangeDestinationStepStartsTest.NoStepStartIsLeftCounted(first);
        ChangeDestinationStepStartsTest.NoStepStartIsLeftCounted(second);
    }

    /// <summary>
    /// L'operatore manda la prima su X, fuori dal magazzino, e mentre il commit guarda un altro
    /// scrittore riscrive la rotta della prima, la stessa (la versione della rotta sale): il commit si
    /// rifiuta, da riprovare, e la prima tiene la 201, la porta NEW-A e la rotta. Allora la copia tiene
    /// la porta, la rotta e nessun segno: un cambio rifiutato non tocca niente (primo giro di correzioni
    /// del compito 15). Prima la copia lasciava la porta prima del commit, e restava senza porta mentre
    /// la prima andava ancora nella 201. Fino al compito 5 della voce 41 il rifiuto veniva dal semaforo
    /// di esecuzione di A preso, che il commit non prende piu'.
    /// </summary>
    [Fact]
    public async Task ACommitRefusedTowardsADeviceLeavesTheCopyOnTheDoor()
    {
        var (first, second, secondStep) = await ACopyOfTheDoorOfTheFirst();
        var firstStep = Assert.Single(first.MissionSteps);
        _plant.Link("A", "X");
        first.CommitRoutePauseForTesting = () =>
        {
            first.CommitRoutePauseForTesting = null;
            first.MissionSteps = [firstStep];
        };

        var result = await MissionGateway.ChangeDestinationAsync(first.MissionId, _plant["X"].ItemGuid);

        Assert.Equal(MissionDestinationOutcome.StepInFlight, result.Outcome);
        Assert.Equal("NEW-A", first.StorageDoorCode);
        Assert.Same(firstStep, Assert.Single(first.MissionSteps));
        Assert.Equal(201, _warehouse.StorageReservationsByMission[first.MissionId].CellId);
        Assert.Equal("NEW-A", second.StorageDoorCode);
        Assert.Same(secondStep, Assert.Single(second.MissionSteps));
        Assert.False(second.LeftACopiedDoor);
        Assert.Equal(PathMission.OperatorChangeStart.Begun, second.TryBeginOperatorChange());
        second.EndOperatorChange();
        ChangeDestinationStepStartsTest.NoStepStartIsLeftCounted(first);
    }

    /// <summary>
    /// La stessa scena, ma il commit passa lo sguardo alla posizione e, prima di scrivere, un altro
    /// operatore sposta la prima su Q (lo spostamento non prende il semaforo di A): il commit perde il
    /// confronto della versione della rotta e il cambio e' rifiutato. La prima tiene la 201 e la porta
    /// NEW-A, e la copia tiene la porta, la rotta e nessun segno: le copie lasciano la porta solo con un
    /// commit che scrive. Prima la lasciavano prima del confronto, e restavano senza porta mentre la prima
    /// andava ancora nella 201 (primo giro di correzioni del compito 4 della voce 41).
    /// </summary>
    [Fact]
    public async Task ACommitThatLosesTheRouteVersionLeavesTheCopyOnTheDoor()
    {
        var (first, second, secondStep) = await ACopyOfTheDoorOfTheFirst();
        _plant.Device("Q");
        _plant.Link("A", "X");

        MissionMoveOutcome? moved = null;
        first.CommitRoutePauseForTesting = () =>
        {
            first.CommitRoutePauseForTesting = null;
            moved = MissionGateway.Move(first.MissionId, _plant["Q"].ItemGuid);
        };

        var result = await MissionGateway.ChangeDestinationAsync(first.MissionId, _plant["X"].ItemGuid);

        Assert.Equal(MissionMoveOutcome.Moved, moved);
        Assert.Equal(MissionDestinationOutcome.StepInFlight, result.Outcome);
        Assert.Equal(_plant["Q"].ItemGuid, first.PositionItemId);
        Assert.Equal("NEW-A", first.StorageDoorCode);
        Assert.Equal(201, _warehouse.StorageReservationsByMission[first.MissionId].CellId);
        Assert.Equal("NEW-A", second.StorageDoorCode);
        Assert.Same(secondStep, Assert.Single(second.MissionSteps));
        Assert.False(second.LeftACopiedDoor);
        Assert.Equal(PathMission.OperatorChangeStart.Begun, second.TryBeginOperatorChange());
        second.EndOperatorChange();
        ChangeDestinationStepStartsTest.NoStepStartIsLeftCounted(first);
    }

    /// <summary>
    /// Il cambio del solo gateway che tiene la cella (decisione 6 del proprietario del 2/10) non tocca
    /// le copie: l'operatore sceglie di nuovo S per la prima con il gateway W, la prima tiene la 201
    /// (NEW-A) con la rotta A->W->S, e la copia tiene la porta NEW-A, la sua rotta B->S e nessun segno.
    /// </summary>
    [Fact]
    public async Task ChangingOnlyTheGatewayLeavesTheCopyOnTheDoor()
    {
        var (first, second, secondStep) = await ACopyOfTheDoorOfTheFirst();
        _plant.Device("W");
        _plant.Link("A", "W");
        _plant.Link("W", "S");

        var result = await MissionGateway.ChangeDestinationAsync(first.MissionId, _plant["S"].ItemGuid,
            MissionGatewayChoice.Set(_plant["W"].ItemGuid));

        Assert.Equal(MissionDestinationOutcome.Changed, result.Outcome);
        Assert.Equal("NEW-A", first.StorageDoorCode);
        Assert.Equal(_plant["W"].ItemGuid, first.Gateway);
        Assert.Equal(["A->W", "W->S"],
            first.MissionSteps.Select(step => $"{_plant.NameOf(step.FromNodeId)}->{_plant.NameOf(step.ToNodeId)}").Order());
        Assert.Equal("NEW-A", second.StorageDoorCode);
        Assert.Same(secondStep, Assert.Single(second.MissionSteps));
        Assert.False(second.LeftACopiedDoor);
        Assert.DoesNotContain(nameof(IWarehouse.ReserveStorageAsync), _warehouse.Calls);
    }

    /// <summary>
    /// Dopo il cambio del solo gateway la prima ha il segno dell'operatore e tiene la 201, e la copia la
    /// porta NEW-A. Se poi la prima perde la 201 - l'operatore le chiede un'altra cella (la 202, NEW-B),
    /// o la missione esce dal motore - la copia lascia la porta lo stesso. Il segno non conta: conta chi
    /// tiene la porta che la copia ha copiato. Altrimenti la copia restava verso la 201 e ci depositava
    /// da sola.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AfterAGatewayOnlyChangeTheCopyStillLeavesTheDoorTheMissionLoses(bool removed)
    {
        var (first, second, _) = await ACopyOfTheDoorOfTheFirst();
        _plant.Device("W");
        _plant.Link("A", "W");
        _plant.Link("W", "S");
        Assert.Equal(MissionDestinationOutcome.Changed,
            (await MissionGateway.ChangeDestinationAsync(first.MissionId, _plant["S"].ItemGuid,
                MissionGatewayChoice.Set(_plant["W"].ItemGuid))).Outcome);
        Assert.True(first.OperatorDestination);
        Assert.Equal("NEW-A", second.StorageDoorCode);
        _warehouse.StorageReservations[first.LuId] = new StorageReservation(first.MissionId,
            StorageReservationOutcome.CellFound, 202, "NEW-B", "0006", 100, NeedsCompaction: false);

        if (removed)
        {
            Assert.True(await ActiveMissions.Instance.Remove(first));
        }
        else
        {
            Assert.Equal(MissionDestinationOutcome.Changed,
                (await MissionGateway.ChangeDestinationAsync(first.MissionId, _plant["S"].ItemGuid)).Outcome);
            Assert.Equal("NEW-B", first.StorageDoorCode);
        }

        Assert.Null(second.StorageDoorCode);
        Assert.Empty(second.MissionSteps);
        Assert.True(second.LeftACopiedDoor);
    }

    /// <summary>
    /// La copia della NEW-A su B e' un'UDC senza l'identificativo del gestionale (LuId 0), e l'operatore le
    /// cambia solo il gateway (W): ne prende il segno, tiene la porta copiata e resta una copia (risposta del
    /// 9/10 alla seconda domanda del compito 17). Poi B la scarica («Unload LU», o all'arrivo) mentre un ciclo
    /// del gruppo la sta contando. Lo scarico di un'UDC senza identificativo toglie la missione direttamente
    /// (<c>AutomationDeviceLuManager.ProcessStandardUnloading</c>), senza guardare l'esito e senza nessuno che la
    /// riprovi: la missione esce, e B resta vuota. Prima, con il segno, non era piu' una copia: la rimozione la
    /// voleva fermare per far lasciare la porta alle sue copie, non poteva, e si rifiutava; B tornava Ready
    /// con la missione ancora a bordo, per sempre.
    /// </summary>
    [Fact]
    public async Task AnOperatorsCopyWithoutAnIdentifierIsUnloadedAlsoWhileAGroupCycleCountsIt()
    {
        var (first, second, _) = await ACopyOfTheDoorOfTheFirst();
        second.LuId = 0;
        await TheOperatorChangesOnlyTheGatewayOf(second);
        _plant["B"].Status = DeviceStatusTypes.Ready;
        Assert.True(second.TryBeginStepStart());

        try
        {
            await _plant["B"].LuUnloadedFromAsi(StepTypes.Manual);
        }
        finally
        {
            second.EndStepStart();
        }

        Assert.Null(ActiveMissions.Instance[second.MissionId]);
        Assert.Null(_plant["B"].LoadedLu);
        Assert.Equal("NEW-A", first.StorageDoorCode);
        Assert.Equal(201, _warehouse.StorageReservationsByMission[first.MissionId].CellId);
    }

    /// <summary>
    /// Una copia vera della NEW-A su C, e su B la copia a cui l'operatore ha cambiato solo il gateway, che ne
    /// ha il segno. La copia di B se ne va: tolta dal motore, o mandata dall'operatore in un'altra cella (la
    /// 202, porta NEW-B, prenotata per lei). La cella 201 resta della prima, che la tiene: la copia di C tiene
    /// la porta NEW-A, la sua rotta e nessun segno, e la prima la sua cella (risposta del 9/10 alla seconda
    /// domanda del compito 17). Prima la copia con il segno non era piu' una copia: chi la toglieva o le
    /// cambiava la cella fermava le copie della sua porta e gliela faceva lasciare, come se la 201 fosse persa.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheRealCopiesKeepTheDoorWhenAnOperatorsCopyLeaves(bool removed)
    {
        var (first, second, _) = await ACopyOfTheDoorOfTheFirst();
        var (third, thirdStep) = await ARealCopyOfTheDoorOnC();
        await TheOperatorChangesOnlyTheGatewayOf(second);

        if (removed)
        {
            Assert.True(await ActiveMissions.Instance.Remove(second));
            Assert.Null(ActiveMissions.Instance[second.MissionId]);
        }
        else
        {
            Assert.Equal(MissionDestinationOutcome.Changed,
                (await MissionGateway.ChangeDestinationAsync(second.MissionId, _plant["S"].ItemGuid)).Outcome);
            Assert.Equal("NEW-B", second.StorageDoorCode);
            Assert.True(second.StorageCellReserved);
        }

        Assert.Equal("NEW-A", third.StorageDoorCode);
        Assert.Same(thirdStep, Assert.Single(third.MissionSteps));
        Assert.False(third.LeftACopiedDoor);
        Assert.False(third.StorageCellLost);
        Assert.Equal("NEW-A", first.StorageDoorCode);
        Assert.Equal(201, _warehouse.StorageReservationsByMission[first.MissionId].CellId);
        ChangeDestinationStepStartsTest.NoStepStartIsLeftCounted(third);
    }

    /// <summary>
    /// La stessa scena, e il giro delle rotte del navigatore pesca la copia con il segno (per esempio il
    /// suo deposito che chiedeva la conferma della cella). La copia non chiede niente al gestionale e il
    /// navigatore non ha niente da rifarle (<c>MissionGateway.KeepTheStorageCellAsync</c> risponde «niente da
    /// fare»): tiene la porta NEW-A, e la copia di C anche. Prima il navigatore chiedeva al gestionale la
    /// cella della copia, che non e' sua, la trovava «non piu' prenotata», fermava le copie della sua porta
    /// - quella di C - e gliela faceva lasciare, poi toglieva la porta alla copia e le prenotava la 202.
    /// </summary>
    [Fact]
    public async Task TheNavigatorLeavesAnOperatorsCopyAndTheRealCopiesOnTheDoor()
    {
        var (first, second, _) = await ACopyOfTheDoorOfTheFirst();
        var (third, thirdStep) = await ARealCopyOfTheDoorOnC();
        await TheOperatorChangesOnlyTheGatewayOf(second);
        var secondSteps = second.MissionSteps.ToList();

        Assert.Null(await MissionGateway.KeepTheStorageCellAsync(second));

        Assert.Equal("NEW-A", second.StorageDoorCode);
        Assert.Equal(secondSteps, second.MissionSteps);
        Assert.False(second.StorageCellLost);
        Assert.False(_warehouse.StorageReservationsByMission.ContainsKey(second.MissionId));
        Assert.Equal("NEW-A", third.StorageDoorCode);
        Assert.Same(thirdStep, Assert.Single(third.MissionSteps));
        Assert.False(third.LeftACopiedDoor);
        Assert.Empty(_warehouse.Calls);
        Assert.Equal(201, _warehouse.StorageReservationsByMission[first.MissionId].CellId);
    }

    /// <summary>
    /// La stessa scena, e il PLC rifiuta la cella della NEW-A al deposito della copia con il segno, mentre il
    /// gestionale non risponde (<c>MissionGateway.LeaveTheRefusedCell</c>). La prima, che tiene la 201, non e'
    /// rifiutata: la copia lascia la porta come una cella persa sua e torna al navigatore, e la copia di C e
    /// la prima tengono porta e cella, come per ogni copia rifiutata da sola (risposta del 9/10 alla seconda
    /// domanda del compito 17). Prima la copia con il segno non era piu' una copia: il rifiuto fermava le
    /// copie della sua porta - quella di C - e gliela faceva lasciare, come se la 201 fosse persa.
    /// </summary>
    [Fact]
    public async Task APlcRefusalOfAnOperatorsCopyLeavesTheRealCopiesOnTheDoor()
    {
        var (first, second, _) = await ACopyOfTheDoorOfTheFirst();
        var (third, thirdStep) = await ARealCopyOfTheDoorOnC();
        await TheOperatorChangesOnlyTheGatewayOf(second);
        RoutesToBuild.Instance.Items = new();

        Assert.True(MissionGateway.LeaveTheRefusedCell(second, "NEW-A", "prova", [second]));

        Assert.Null(second.StorageDoorCode);
        Assert.Empty(second.MissionSteps);
        Assert.True(second.StorageCellLost);
        Assert.Same(second, RoutesToBuild.Instance[second.MissionId]);
        Assert.Equal("NEW-A", third.StorageDoorCode);
        Assert.Same(thirdStep, Assert.Single(third.MissionSteps));
        Assert.False(third.LeftACopiedDoor);
        Assert.Equal("NEW-A", first.StorageDoorCode);
        Assert.Equal(201, _warehouse.StorageReservationsByMission[first.MissionId].CellId);
        ChangeDestinationStepStartsTest.NoStepStartIsLeftCounted(second);
        ChangeDestinationStepStartsTest.NoStepStartIsLeftCounted(third);
        RoutesToBuild.Instance.Items = new();
    }

    /// <summary>
    /// La stessa scena, ma la prima non presta piu' la porta (e' uscita dal motore) quando il PLC rifiuta la
    /// cella della NEW-A al deposito della copia con il segno, con il gestionale muto. La copia la lascia
    /// come una cella persa sua, non come copia: il magazzino S l'ha scelto l'operatore, e la ricerca per
    /// tipo del gruppo non le scriverebbe la destinazione. Torna al navigatore, con una riga che lo dice; e
    /// il navigatore, quando il gestionale risponde, le prenota la cella 202 in S, con il suo gateway W e
    /// la 201 rifiutata esclusa. Revisione finale del quarto giro: prima nessuna prova passava di qui.
    /// </summary>
    [Fact]
    public async Task APlcRefusalOfAnOperatorsCopyWithoutALenderSendsItToTheNavigatorThatReservesItACell()
    {
        using var log = new RecordingConsole();
        var (first, second, _) = await ACopyOfTheDoorOfTheFirst();
        await TheOperatorChangesOnlyTheGatewayOf(second);
        ActiveMissions.Instance.RemoveAll(mission => mission.MissionId == first.MissionId);
        RoutesToBuild.Instance.Items = new();

        Assert.True(MissionGateway.LeaveTheRefusedCell(second, "NEW-A", "prova", [second]));

        Assert.Null(second.StorageDoorCode);
        Assert.Empty(second.MissionSteps);
        Assert.True(second.StorageCellLost);
        Assert.False(second.LeftACopiedDoor);
        Assert.Same(second, RoutesToBuild.Instance[second.MissionId]);
        Assert.Single(log.Lines, line => line.Contains($"alla missione {second.MissionId}") &&
                                         line.Contains("l'ha scelto l'operatore: la cella gliela prenota il navigatore"));
        ChangeDestinationStepStartsTest.NoStepStartIsLeftCounted(second);
        RoutesToBuild.Instance.Items = new();

        _warehouse.StorageReservations[second.LuId] = new StorageReservation(second.MissionId,
            StorageReservationOutcome.CellFound, 202, "NEW-B", "0006", 100, NeedsCompaction: false);
        Assert.Equal(RouteBuildStatusTypes.Available, await MissionGateway.KeepTheStorageCellAsync(second));

        Assert.Equal("NEW-B", second.StorageDoorCode);
        Assert.True(second.StorageCellReserved);
        Assert.False(second.StorageCellLost);
        Assert.Equal(202, _warehouse.StorageReservationsByMission[second.MissionId].CellId);
        Assert.Equal(_plant["S"].ItemGuid, second.DestinationItemId);
        Assert.Equal(_plant["W"].ItemGuid, second.Gateway);
        Assert.Equal(["B->W", "W->S"],
            second.MissionSteps.Select(step => $"{_plant.NameOf(step.FromNodeId)}->{_plant.NameOf(step.ToNodeId)}").Order());
        RoutesToBuild.Instance.Items = new();
    }

    /// <summary>
    /// La copia della NEW-A su B con il segno dell'operatore (la destinazione S scelta di nuovo, come la
    /// lascia il cambio del solo gateway), e il gestionale che non tiene piu' la 201 per la prima. Il giro del
    /// gruppo lascia fuori il deposito della prima, che va al navigatore; quello della copia non chiede la
    /// cella (non e' sua) e aspetta con la prima, come ogni copia (risposta del 9/10 alla seconda domanda del
    /// compito 17): nessuno step parte, e la copia non va al navigatore. Prima la copia con il segno chiedeva
    /// la conferma come una missione dell'operatore, e andava al navigatore, che non la trovava sua.
    /// </summary>
    [Fact]
    public async Task TheDepositOfAnOperatorsCopyDoesNotAskAndWaitsWithTheMissionItCopied()
    {
        var (first, second, secondStep) = await ACopyOfTheDoorOfTheFirst();
        Assert.True(second.SetOperatorDestination(_plant["S"].ItemGuid));
        Assert.Equal("NEW-A", second.StorageDoorCode);
        Assert.Same(secondStep, Assert.Single(second.MissionSteps));
        var firstStep = Assert.Single(first.MissionSteps);
        var group = _plant["G"];
        group.RecordStorageLength = RecordStorageTypes.FromPlc;
        group.Status = DeviceStatusTypes.Ready;
        _warehouse.RevokeStorageReservation(first.MissionId);
        RoutesToBuild.Instance.Items = new();

        await group.StepExecutor!.ExecuteGroupedMOVE_LU_TO([firstStep, secondStep]);

        Assert.Equal(Guid.Empty, firstStep.GroupedMovementId);
        Assert.Equal(Guid.Empty, secondStep.GroupedMovementId);
        Assert.Same(first, RoutesToBuild.Instance[first.MissionId]);
        Assert.Null(RoutesToBuild.Instance[second.MissionId]);
        ChangeDestinationStepStartsTest.NoStepStartIsLeftCounted(first);
        ChangeDestinationStepStartsTest.NoStepStartIsLeftCounted(second);
        RoutesToBuild.Instance.Items = new();
    }

    /// <summary>
    /// Dopo il cambio del solo gateway la copia di B ha il segno e resta una copia, e segue la regola delle
    /// copie: quando la prima perde la 201 (l'operatore le chiede un'altra cella, la 202) la lascia con lei.
    /// Senza porta non e' piu' una copia, e il magazzino S l'ha scelto l'operatore: la lascia come una cella
    /// persa, con il suo gateway W, e va in coda al navigatore, che le prenota una cella in S. Prima teneva la
    /// NEW-A, e ci avrebbe depositato da sola in una cella che nessuna missione teneva piu'.
    /// </summary>
    [Fact]
    public async Task AnOperatorsCopyLeavesTheDoorWithTheMissionItCopiedAndGoesToTheNavigator()
    {
        var (first, second, _) = await ACopyOfTheDoorOfTheFirst();
        await TheOperatorChangesOnlyTheGatewayOf(second);
        _warehouse.StorageReservations[first.LuId] = new StorageReservation(first.MissionId,
            StorageReservationOutcome.CellFound, 202, "NEW-B", "0006", 100, NeedsCompaction: false);
        RoutesToBuild.Instance.Items = new();

        Assert.Equal(MissionDestinationOutcome.Changed,
            (await MissionGateway.ChangeDestinationAsync(first.MissionId, _plant["S"].ItemGuid)).Outcome);

        Assert.Equal("NEW-B", first.StorageDoorCode);
        Assert.Null(second.StorageDoorCode);
        Assert.Empty(second.MissionSteps);
        Assert.True(second.StorageCellLost);
        Assert.False(second.LeftACopiedDoor);
        Assert.True(second.OperatorDestination);
        Assert.Equal(_plant["S"].ItemGuid, second.DestinationItemId);
        Assert.Equal(_plant["W"].ItemGuid, second.Gateway);
        Assert.Same(second, RoutesToBuild.Instance[second.MissionId]);
        Assert.Equal(PathMission.OperatorChangeStart.Begun, second.TryBeginOperatorChange());
        second.EndOperatorChange();
        RoutesToBuild.Instance.Items = new();
    }

    /// <summary>
    /// L'aggiornamento da un motore senza il segno della cella prenotata: la missione dell'operatore su A,
    /// membro del gruppo G, verso S con la porta NEW-A, e' scritta allo schema di prima del segno. Allora ogni
    /// cambio dell'operatore verso un magazzino prenotava la cella, e la NEW-A e' una prenotazione sua. Intanto
    /// il gestionale non la tiene piu' (scaduta, o revocata), e per la sua UDC ha la 202 (NEW-B). Ricaricata,
    /// la missione ha il segno e non e' una copia: il suo deposito chiede la cella e aspetta il navigatore, che
    /// la trova non piu' sua e le prenota la 202. Prima, con il segno giu', su un membro di un gruppo era una
    /// copia (revisione del quarto giro del compito 17): il deposito partiva senza chiedere, verso una cella
    /// che il gestionale non le teneva piu', e il navigatore non la guardava.
    /// </summary>
    [Fact]
    public async Task AnOperatorsMissionWrittenBeforeTheReservedMarkStillAsksForItsCellAfterTheReload()
    {
        var (normal, _) = await TwoMissionsOfTheSameTypeOnAGroup(operatorMissionFirst: false, withTheMark: false);
        var row = await AnOperatorsRowWrittenBeforeTheReservedMark(normal.MissionId, normal.LuId,
            _plant["A"].ItemGuid, _plant["S"].ItemGuid, "NEW-A");
        Assert.False(_warehouse.StorageReservationsByMission.ContainsKey(normal.MissionId));
        _warehouse.StorageReservations[normal.LuId] = new StorageReservation(normal.MissionId,
            StorageReservationOutcome.CellFound, 202, "NEW-B", "0006", 100, NeedsCompaction: false);

        // La riga ricaricata (MissionStore.ToPathMission, come in DbInfo.LoadMissions) torna fra le attive.
        // A motore fermo l'aggiunta non fa niente: si aggiunge in Run, come le missioni della scena, e si
        // torna in Stop. Il punto e' la riga che la migrazione ha scritto, non la ricarica in Recover.
        var reloaded = MissionStore.ToPathMission(row);
        ActiveMissions.Instance.RemoveAll(mission => mission.MissionId == normal.MissionId);
        Navigator.NavStatus = NavigatorStatusTypes.Run;
        ActiveMissions.Instance.Add(reloaded, buildRoute: false);
        await reloaded.PositionWork;
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        Assert.Equal(_plant["A"].ItemGuid, reloaded.PositionItemId);
        Assert.NotNull(reloaded.PositionItem!.BelongedGroup);
        Assert.True(reloaded.OperatorDestination);
        Assert.Equal("NEW-A", reloaded.StorageDoorCode);
        RoutesToBuild.Instance.Items = new();
        _warehouse.Calls.Clear();

        Assert.True(reloaded.StorageCellReserved);
        Assert.False(MissionGateway.IsACopy(reloaded));
        Assert.False(reloaded.MayStartTheDeposit(confirmedOn: null, "prova"));
        Assert.Same(reloaded, RoutesToBuild.Instance[reloaded.MissionId]);
        RoutesToBuild.Instance.Items = new();

        Assert.Equal(RouteBuildStatusTypes.Available, await MissionGateway.KeepTheStorageCellAsync(reloaded));

        Assert.Contains(nameof(IWarehouse.GetStorageReservationAsync), _warehouse.Calls);
        Assert.Equal("NEW-B", reloaded.StorageDoorCode);
        Assert.True(reloaded.StorageCellReserved);
        Assert.Equal(202, _warehouse.StorageReservationsByMission[reloaded.MissionId].CellId);
        AssertSingleStep(reloaded, "A", "S");
        RoutesToBuild.Instance.Items = new();
    }

    /// <summary>
    /// La riga di una missione dell'operatore verso <paramref name="destination"/> con la porta
    /// <paramref name="door"/>, scritta allo schema di prima del segno della cella prenotata (un file vero, con
    /// SQL grezzo: il modello di oggi la colonna la scriverebbe), e riletta dopo le migrazioni di oggi.
    /// </summary>
    private static async Task<Mission> AnOperatorsRowWrittenBeforeTheReservedMark(Guid missionId, decimal luId,
        Guid position, Guid destination, string door)
    {
        const string beforeTheMark = "20261001105617_AddMissionStorageCellLost";
        var directory = Path.Combine(Path.GetTempPath(), $"wcs-operator-reserved-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(directory);
            var options = new DbContextOptionsBuilder<WcsDbContext>()
                .UseSqlite(new SqliteConnectionStringBuilder
                {
                    DataSource = Path.Combine(directory, WcsDatabase.OperationalFileName),
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Pooling = false
                }.ToString())
                .Options;

            using (var context = new WcsDbContext(options))
            {
                context.GetInfrastructure().GetRequiredService<IMigrator>().Migrate(beforeTheMark);
                context.Database.ExecuteSqlRaw(
                    "INSERT INTO Missions (MissionId, LoadUnitId, PositionItemId, DestinationItemId, Date, StorageDoorCode, " +
                    "Status, MissionType, OperationType, BufferCellId, ExtractionQueueId, LoadUnitTypeId, HandlingMode, " +
                    "StorageDepth, NoteAssignmentId, NoteProcessingId, ActiveFilter, Tare, ReallocatedDestination, " +
                    "OperatorDestination, StorageCellLost) " +
                    "VALUES ({0}, {1}, {2}, {3}, {4}, {5}, 0, 0, 2, 0, 0, 1, 0, 100, 0, 0, 0, 0, 0, 1, 0);",
                    missionId, luId, position, destination, new DateTime(2026, 10, 1, 12, 0, 0), door);
            }

            using var database = WcsDatabase.Open(directory);
            await using var reading = database.CreateContext();
            return Assert.Single(await reading.Missions.AsNoTracking().ToListAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Il cambio del solo gateway della copia di B (<see cref="ACopyOfTheDoorOfTheFirst"/>): l'operatore
    /// sceglie di nuovo S con il gateway W. La copia ne prende il segno e tiene la porta copiata NEW-A, senza
    /// una prenotazione sua, con la rotta B->W->S; il gestionale non sente niente.
    /// </summary>
    private async Task TheOperatorChangesOnlyTheGatewayOf(PathMission copy)
    {
        _plant.Device("W");
        _plant.Link("B", "W");
        _plant.Link("W", "S");

        Assert.Equal(MissionDestinationOutcome.Changed,
            (await MissionGateway.ChangeDestinationAsync(copy.MissionId, _plant["S"].ItemGuid,
                MissionGatewayChoice.Set(_plant["W"].ItemGuid))).Outcome);

        Assert.True(copy.OperatorDestination);
        Assert.False(copy.StorageCellReserved);
        Assert.Equal("NEW-A", copy.StorageDoorCode);
        Assert.Equal(_plant["W"].ItemGuid, copy.Gateway);
        Assert.Equal(["B->W", "W->S"],
            copy.MissionSteps.Select(step => $"{_plant.NameOf(step.FromNodeId)}->{_plant.NameOf(step.ToNodeId)}").Order());
        Assert.DoesNotContain(nameof(IWarehouse.ReserveStorageAsync), _warehouse.Calls);
        _warehouse.Calls.Clear();
    }

    /// <summary>
    /// Una terza UDC dello stesso tipo su C, terzo membro del gruppo G, che copia la porta NEW-A della prima
    /// come gliela da' la ricerca per tipo del gruppo (porta e destinazione dal selettore, poi la rotta C->S),
    /// senza segno ne' prenotazione sua.
    /// </summary>
    private async Task<(PathMission Copy, MissionStep Step)> ARealCopyOfTheDoorOnC()
    {
        var member = _plant.Device("C");
        member.BelongingGroupId = _plant["G"].GroupItemId;
        member.OrderNumberGroup = 3;
        member.LinkedCellId = 303;
        _plant.Link("C", "S");

        // A motore fermo l'aggiunta non fa niente (ActiveMissions.Add): si aggiunge in Run, come le
        // missioni della scena (TwoMissionsOfTheSameTypeOnAGroup), e si torna in Stop. Senza, la copia non
        // sarebbe fra le attive, nessuno la cercherebbe come copia, e le prove che la guardano tenere la
        // porta passerebbero anche con il difetto.
        Navigator.NavStatus = NavigatorStatusTypes.Run;
        var copy = new PathMission { PositionItemId = member.ItemGuid, LuId = 33, IdLuType = 1 };
        ActiveMissions.Instance.Add(copy, buildRoute: false);
        await copy.PositionWork;
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        Assert.Same(copy, ActiveMissions.Instance[copy.MissionId]);
        copy.StorageDoorCode = "NEW-A";
        copy.DestinationItemId = _plant["S"].ItemGuid;
        await copy.BuildPathToDestination();

        Assert.Same(_plant["G"], member.BelongedGroup);
        Assert.False(copy.OperatorDestination);
        Assert.False(copy.StorageCellReserved);
        AssertSingleStep(copy, "C", "S");
        _warehouse.Calls.Clear();
        return (copy, Assert.Single(copy.MissionSteps));
    }

    /// <summary>
    /// Il cambio della cella della prima mentre un ciclo del gruppo sta avviando il deposito della copia:
    /// la copia non si puo' fermare, e il cambio si rifiuta prima di chiedere al gestionale, da riprovare,
    /// come il navigatore con la cella persa. La prima tiene la 201 e la rotta, la copia NEW-A. Senza
    /// fermarla la prima prendeva la 202, e la copia, che non lasciava la porta, ci depositava da sola.
    /// </summary>
    [Fact]
    public async Task ACopyThatCannotBeStoppedRefusesTheOperatorsChangeOfTheCell()
    {
        var (first, second, secondStep) = await ACopyOfTheDoorOfTheFirst();
        var firstStep = Assert.Single(first.MissionSteps);
        _warehouse.StorageReservations[first.LuId] = new StorageReservation(first.MissionId,
            StorageReservationOutcome.CellFound, 202, "NEW-B", "0006", 100, NeedsCompaction: false);
        Assert.True(second.TryBeginStepStart());

        MissionDestinationResult result;
        try
        {
            result = await MissionGateway.ChangeDestinationAsync(first.MissionId, _plant["S"].ItemGuid);
        }
        finally
        {
            second.EndStepStart();
        }

        Assert.Equal(MissionDestinationOutcome.CopyInFlight, result.Outcome);
        Assert.Equal("NEW-A", first.StorageDoorCode);
        Assert.Same(firstStep, Assert.Single(first.MissionSteps));
        Assert.Equal(201, _warehouse.StorageReservationsByMission[first.MissionId].CellId);
        Assert.Equal("NEW-A", second.StorageDoorCode);
        Assert.Same(secondStep, Assert.Single(second.MissionSteps));
        Assert.DoesNotContain(nameof(IWarehouse.ReserveStorageAsync), _warehouse.Calls);
        ChangeDestinationStepStartsTest.NoStepStartIsLeftCounted(first);
        ChangeDestinationStepStartsTest.NoStepStartIsLeftCounted(second);
    }

    /// <summary>
    /// Lo stesso rifiuto verso un dispositivo: l'operatore manda la prima su X mentre un ciclo del gruppo
    /// sta avviando il deposito della copia. Il cambio si rifiuta con l'esito della copia che si muove
    /// (risposta 4 del 9/10), non con il passo in volo della prima, che e' ferma; la prima tiene la 201,
    /// la porta NEW-A e la rotta, e la copia la porta e la rotta.
    /// </summary>
    [Fact]
    public async Task ACopyThatCannotBeStoppedRefusesTheOperatorsChangeTowardsADevice()
    {
        var (first, second, secondStep) = await ACopyOfTheDoorOfTheFirst();
        var firstStep = Assert.Single(first.MissionSteps);
        _plant.Link("A", "X");
        Assert.True(second.TryBeginStepStart());

        MissionDestinationResult result;
        try
        {
            result = await MissionGateway.ChangeDestinationAsync(first.MissionId, _plant["X"].ItemGuid);
        }
        finally
        {
            second.EndStepStart();
        }

        Assert.Equal(MissionDestinationOutcome.CopyInFlight, result.Outcome);
        Assert.Equal("NEW-A", first.StorageDoorCode);
        Assert.Equal(_plant["S"].ItemGuid, first.DestinationItemId);
        Assert.Same(firstStep, Assert.Single(first.MissionSteps));
        Assert.Equal(201, _warehouse.StorageReservationsByMission[first.MissionId].CellId);
        Assert.Equal("NEW-A", second.StorageDoorCode);
        Assert.Same(secondStep, Assert.Single(second.MissionSteps));
        Assert.False(second.LeftACopiedDoor);
        ChangeDestinationStepStartsTest.NoStepStartIsLeftCounted(first);
        ChangeDestinationStepStartsTest.NoStepStartIsLeftCounted(second);
    }

    /// <summary>
    /// Un dispositivo sta avviando uno step della prima, e anche quello della copia. The step of the
    /// mission itself is the first thing looked at, before the copies (owner's answer 4 of 9/10), and
    /// since item 54 (decision 4) the change waits for it instead of being refused: nothing is touched
    /// while the starts are under way; when they end without a step in flight, the change is applied.
    /// </summary>
    [Fact]
    public async Task AStepOfTheMissionItselfMakesTheChangeWaitAlsoWithACopyMoving()
    {
        var (first, second, secondStep) = await ACopyOfTheDoorOfTheFirst();
        var firstStep = Assert.Single(first.MissionSteps);
        Assert.True(first.TryBeginStepStart());
        Assert.True(second.TryBeginStepStart());

        MissionDestinationResult result;
        try
        {
            result = await MissionGateway.ChangeDestinationAsync(first.MissionId, _plant["S"].ItemGuid);
            await PendingMissionChanges.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(MissionDestinationOutcome.Pending, result.Outcome);
            Assert.Equal(PendingMissionChange.Destination(_plant["S"].ItemGuid, MissionGatewayChoice.Keep),
                MissionGateway.PendingChangeOf(first.MissionId));
            Assert.Equal("NEW-A", first.StorageDoorCode);
            Assert.Same(firstStep, Assert.Single(first.MissionSteps));
            Assert.Equal("NEW-A", second.StorageDoorCode);
            Assert.Same(secondStep, Assert.Single(second.MissionSteps));
            Assert.DoesNotContain(nameof(IWarehouse.ReserveStorageAsync), _warehouse.Calls);
        }
        finally
        {
            second.EndStepStart();
            first.EndStepStart();
        }

        // The starts ended and no step went: the change waited for nothing more, and it is applied.
        await PendingMissionChanges.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Null(MissionGateway.PendingChangeOf(first.MissionId));
        Assert.True(first.OperatorDestination);
        ChangeDestinationStepStartsTest.NoStepStartIsLeftCounted(first);
        ChangeDestinationStepStartsTest.NoStepStartIsLeftCounted(second);
    }

    /// <summary>
    /// Item 54, fix round 1 of task 4: a change accepted while a step of the first was being started meets,
    /// when that start ends, a copy of its door that cannot be stopped (a group cycle is starting its
    /// deposit). «Copy in flight» is a refusal that passes by itself: the change is kept (since fix round 2 it
    /// stays in the registry until the outcome is known), the first keeps its cell, destination and route; once the
    /// copy's start has ended the master loop applies it.
    /// </summary>
    [Fact]
    public async Task AChangeThatMeetsAMovingCopyWhenTheStepEndsWaitsForIt()
    {
        var (first, second, secondStep) = await ACopyOfTheDoorOfTheFirst();
        var firstStep = Assert.Single(first.MissionSteps);
        _plant.Link("A", "X");
        Assert.True(first.TryBeginStepStart());
        Assert.True(second.TryBeginStepStart());

        try
        {
            try
            {
                Assert.Equal(MissionDestinationOutcome.Pending,
                    (await MissionGateway.ChangeDestinationAsync(first.MissionId, _plant["X"].ItemGuid)).Outcome);
                await PendingMissionChanges.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));
            }
            finally
            {
                first.EndStepStart();
            }

            // The start of the first ended without a step: the change is applied, and meets the copy.
            await PendingMissionChanges.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(PendingMissionChange.Destination(_plant["X"].ItemGuid, MissionGatewayChoice.Keep),
                MissionGateway.PendingChangeOf(first.MissionId));
            Assert.Equal(_plant["S"].ItemGuid, first.DestinationItemId);
            Assert.False(first.OperatorDestination);
            Assert.Equal("NEW-A", first.StorageDoorCode);
            Assert.Same(firstStep, Assert.Single(first.MissionSteps));
            Assert.Equal("NEW-A", second.StorageDoorCode);
            Assert.Same(secondStep, Assert.Single(second.MissionSteps));
        }
        finally
        {
            second.EndStepStart();
        }

        PendingMissionChanges.SweepAll();
        await PendingMissionChanges.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Null(MissionGateway.PendingChangeOf(first.MissionId));
        Assert.Equal(_plant["X"].ItemGuid, first.DestinationItemId);
        Assert.True(first.OperatorDestination);
        ChangeDestinationStepStartsTest.NoStepStartIsLeftCounted(first);
        ChangeDestinationStepStartsTest.NoStepStartIsLeftCounted(second);
    }

    /// <summary>
    /// Item 54, fix round 2 of task 4: the same scene, seen from the devices the change wakes when it ends
    /// (<c>MissionGateway.WakeTheDevicesThatStartTheNextStep</c>). The change being applied meets the copy
    /// that cannot be stopped («copy in flight», a refusal that passes by itself) after it has begun: when
    /// it wakes the devices that start the first's next step, the change must still be waiting, so that no
    /// step of the old route starts (<c>PathMission.TryBeginStepStart</c> answers no). Until this round the
    /// change was taken out when it began and put back only after the wake: in Run the woken device started
    /// the step A->S of the old route in between. In Stop <c>ExecuteStep</c> exits at once, and the probe
    /// asks the question the woken device would ask.
    /// </summary>
    [Fact]
    public async Task WhenAChangeMeetsAMovingCopyTheDevicesItWakesCannotStartTheOldRoute()
    {
        var (first, second, _) = await ACopyOfTheDoorOfTheFirst();
        _plant.Link("A", "X");
        Assert.True(first.TryBeginStepStart());
        Assert.True(second.TryBeginStepStart());

        var answers = new List<(bool Pending, bool Starts)>();
        MissionGateway.WakingTheDevicesForTesting = woken =>
        {
            if (!ReferenceEquals(woken, first)) return;

            var starts = first.TryBeginStepStart();
            lock (answers) answers.Add((MissionGateway.PendingChangeOf(first.MissionId) != null, starts));
            if (starts) first.EndStepStart();
        };

        try
        {
            try
            {
                Assert.Equal(MissionDestinationOutcome.Pending,
                    (await MissionGateway.ChangeDestinationAsync(first.MissionId, _plant["X"].ItemGuid)).Outcome);
                await PendingMissionChanges.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));
            }
            finally
            {
                first.EndStepStart();
            }

            // The start of the first ended without a step: the change is applied, meets the copy, and wakes
            // the devices of the first with the change still waiting.
            await PendingMissionChanges.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));
            lock (answers) Assert.Equal([(true, false)], answers);
            Assert.Equal(PendingMissionChange.Destination(_plant["X"].ItemGuid, MissionGatewayChoice.Keep),
                MissionGateway.PendingChangeOf(first.MissionId));
            Assert.Equal(_plant["S"].ItemGuid, first.DestinationItemId);
        }
        finally
        {
            second.EndStepStart();
            MissionGateway.WakingTheDevicesForTesting = null;
        }

        PendingMissionChanges.SweepAll();
        await PendingMissionChanges.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Null(MissionGateway.PendingChangeOf(first.MissionId));
        Assert.Equal(_plant["X"].ItemGuid, first.DestinationItemId);
        ChangeDestinationStepStartsTest.NoStepStartIsLeftCounted(first);
        ChangeDestinationStepStartsTest.NoStepStartIsLeftCounted(second);
    }

    /// <summary>
    /// Punto 3 dell'8/10 sera: l'operatore chiede un'altra cella in S per la prima, e il gestionale, alla
    /// domanda senza prenotare, non ne ha (nessuna cella libera). Il cambio si rifiuta prima della
    /// prenotazione: la prima tiene la 201 (NEW-A) e la rotta, e la copia tiene la porta NEW-A, la rotta e
    /// nessun segno, e riparte con la prima. Le copie lasciano la porta solo quando parte la prenotazione,
    /// che lascia la cella della missione: un rifiuto prima non tocca la cella, ne' loro (revisione finale
    /// del terzo giro: niente falliva se le copie lasciavano la porta appena ferme).
    /// </summary>
    [Fact]
    public async Task AChangeRefusedBeforeTheReservationLeavesTheCopyOnTheDoor()
    {
        var (first, second, secondStep) = await ACopyOfTheDoorOfTheFirst();
        var firstStep = Assert.Single(first.MissionSteps);
        _warehouse.StorageReservations[first.LuId] = new StorageReservation(Guid.Empty,
            StorageReservationOutcome.NoFreeCells, 0, string.Empty, string.Empty, 0, NeedsCompaction: false);
        var asked = _warehouse.StorageRequests.Count;

        var result = await MissionGateway.ChangeDestinationAsync(first.MissionId, _plant["S"].ItemGuid);

        Assert.Equal(MissionDestinationOutcome.NoStorageCell, result.Outcome);
        Assert.Equal("NEW-A", first.StorageDoorCode);
        Assert.Same(firstStep, Assert.Single(first.MissionSteps));
        Assert.Equal(201, _warehouse.StorageReservationsByMission[first.MissionId].CellId);
        Assert.Equal("NEW-A", second.StorageDoorCode);
        Assert.Same(secondStep, Assert.Single(second.MissionSteps));
        Assert.False(second.LeftACopiedDoor);
        var request = Assert.Single(_warehouse.StorageRequests.Skip(asked));
        Assert.False(request.AssignPosition);
        ChangeDestinationStepStartsTest.NoStepStartIsLeftCounted(first);
        ChangeDestinationStepStartsTest.NoStepStartIsLeftCounted(second);
    }

    /// <summary>
    /// Il cambio dell'operatore di una cella con le copie: la copia scrive la porta vuota nel database
    /// del WCS prima che la prima scriva la cella nuova, come con la cella persa, perche' una ricarica fra
    /// le due righe converga. Nell'ordine opposto la prima tornava con NEW-B e la copia con NEW-A senza
    /// segno: nessuna missione teneva piu' NEW-A, e la copia ci depositava da sola.
    /// </summary>
    [Fact]
    public async Task TheCopyLeavesTheDoorInTheDatabaseBeforeTheOperatorsCellIsWritten()
    {
        var (first, second, _) = await ACopyOfTheDoorOfTheFirst();
        _warehouse.StorageReservations[first.LuId] = new StorageReservation(first.MissionId,
            StorageReservationOutcome.CellFound, 202, "NEW-B", "0006", 100, NeedsCompaction: false);
        var writes = new List<(Guid Mission, string? Door)>();
        ORM.PathMission.SondaScrittura = written =>
        {
            lock (writes) writes.Add((written.MissionId, written.StorageDoorCode));
        };

        try
        {
            Assert.Equal(MissionDestinationOutcome.Changed,
                (await MissionGateway.ChangeDestinationAsync(first.MissionId, _plant["S"].ItemGuid)).Outcome);
        }
        finally
        {
            ORM.PathMission.SondaScrittura = null;
        }

        var copyLeft = writes.FindIndex(write => write.Mission == second.MissionId && write.Door == null);
        var missionChanged = writes.FindIndex(write => write.Mission == first.MissionId && write.Door == "NEW-B");
        Assert.True(copyLeft >= 0, "la copia non ha scritto la porta vuota");
        Assert.True(missionChanged >= 0, "la missione non ha scritto la porta nuova");
        Assert.True(copyLeft < missionChanged, $"la copia ha scritto la porta vuota alla riga {copyLeft}, la missione la nuova alla {missionChanged}");
    }

    /// <summary>
    /// La stessa prova di controllo nella ricerca raggruppata di una nuova cella
    /// (<c>FindNewStoreLocationGrouped</c>): la seconda UDC prende il selettore della prima.
    /// </summary>
    [Fact]
    public async Task WithoutTheMarkTheGroupedStoreSearchStillGivesTheSelectorToTheSameType()
    {
        var (first, second) = await TwoMissionsOfTheSameTypeOnAGroup(operatorMissionFirst: false, withTheMark: false);

        Assert.True(await _plant["G"].StorageManager!.FindNewStoreLocationGroupedForTest().WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.Equal(_plant["S"].ItemGuid, first.DestinationItemId);
        Assert.Equal("NEW-A", first.StorageDoorCode);
        Assert.Equal(_plant["S"].ItemGuid, second.DestinationItemId);
        Assert.Equal("NEW-A", second.StorageDoorCode);
        Assert.Equal(1, _warehouse.Calls.Count(call => call == nameof(IWarehouse.ReserveStorageAsync)));
        AssertSingleStep(second, "B", "S");
    }

    // ---------------------------------------------------------------------------------------
    // La distribuzione sui polmoni
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Un'estrazione con il segno passa da un dispositivo che distribuisce sui polmoni: la
    /// destinazione resta quella dell'operatore invece di diventare il polmone di turno.
    /// </summary>
    [Fact]
    public async Task TheDistributionOnBuffersDoesNotReplaceTheOperatorsChoice()
    {
        var mission = await AnExtractionArrivingOnADistributor(operatorChoice: true);

        await _plant["D"].LoadLu(mission.MissionId);
        await mission.PositionWork;

        Assert.Equal(_plant["X"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.OperatorDestination);
    }

    /// <summary>La stessa senza il segno: la distribuzione sceglie il polmone, come oggi.</summary>
    [Fact]
    public async Task WithoutTheMarkTheDistributionOnBuffersStillApplies()
    {
        var mission = await AnExtractionArrivingOnADistributor(operatorChoice: false);

        await _plant["D"].LoadLu(mission.MissionId);
        await mission.PositionWork;

        Assert.Equal(_plant["BUF"].ItemGuid, mission.DestinationItemId);
        Assert.False(mission.OperatorDestination);
    }

    // ---------------------------------------------------------------------------------------
    // La cella che non va piu' bene sul ricollocatore: FindNewStoreLocation
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// La cella scelta dall'operatore (la 7 di S7) e' impegnata da un'estrazione quando l'UDC e' sul
    /// ricollocatore R, che prima di depositare la riverifica (con la cella confermata dal
    /// navigatore: il gestionale la tiene ancora): il motore non ne cerca un'altra. Nessuna
    /// prenotazione e nessun rilascio, e il gestionale tiene ancora la cella 7 per la missione;
    /// porta, destinazione, segno, gateway e rotta restano quelli dell'operatore; il deposito non
    /// parte, la riga di avviso dice che la cella l'ha scelta
    /// l'operatore, e la verifica dopo aspetta il freno. Prima la ricerca cancellava il gateway,
    /// lasciava la cella 7, prendeva la 8, si fermava sulla guardia del segno e rilasciava la 8: la
    /// missione restava senza prenotazione, e ogni giro rifaceva lo stesso.
    /// </summary>
    [Fact]
    public async Task TheRelocatorDoesNotLookForAnotherCellInsteadOfTheOperatorsOne()
    {
        using var log = new RecordingLogWriter();
        var (relocator, mission, step) = await AMissionWhoseCellIsNoLongerGoodOnTheRelocator(operatorChoice: true);
        Assert.NotNull(relocator.StorageManager);
        relocator.StorageManager.StoreSearchRetries =
            new RetryBackoff(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30), new ManualClock());
        await TheRouteLoopLooksForTheCell(mission);

        await relocator.StorageManager.StartStoreLu();

        // La verifica c'e' stata davvero: senza, i controlli qui sotto passerebbero anche se il
        // deposito non fosse nemmeno arrivato alla cella.
        Assert.Contains(nameof(IWarehouse.GetCellBusyStateAsync), _warehouse.Calls);
        await log.Wait((line, level) => level == Log.Interface.LogEntryType.Warning &&
                                        line.Contains($"per la missione {mission.MissionId}, ma la cella l'ha scelta l'operatore"))
            .WaitAsync(TimeSpan.FromSeconds(30));
        Assert.DoesNotContain(nameof(IWarehouse.ReserveStorageAsync), _warehouse.Calls);
        Assert.DoesNotContain(nameof(IWarehouse.ReleaseStorageReservationAsync), _warehouse.Calls);
        Assert.Equal(LinkedCellId, _warehouse.StorageReservationsByMission[mission.MissionId].CellId);
        Assert.Equal("A7", mission.StorageDoorCode);
        Assert.Equal(_plant["S7"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.OperatorDestination);
        Assert.Equal(_plant["G"].ItemGuid, mission.Gateway);
        Assert.Same(step, Assert.Single(mission.MissionSteps));
        Assert.Equal(Guid.Empty, relocator.RunningStepId);
        Assert.True(relocator.StorageManager.StoreSearchRetries.IsWaiting(mission.MissionId));
    }

    /// <summary>
    /// L'attesa non e' una rinuncia: passato il freno la missione torna al navigatore, che conferma
    /// la cella 7 (il gestionale la tiene ancora), e la cella si riverifica, ancora senza cercarne
    /// un'altra; quando l'estrazione l'ha lasciata il deposito parte verso la cella 7. Prima della
    /// correzione il deposito partiva lo stesso, ma verso una cella che il gestionale non teneva piu'
    /// per nessuno. Il deposito di una missione dell'operatore parte con la cella confermata presso
    /// il gestionale dal navigatore, e la conferma si chiede prima della riverifica: senza, la
    /// missione va al giro delle rotte, e la riverifica parte al risveglio.
    /// </summary>
    [Fact]
    public async Task TheOperatorsCellIsWaitedForAndUsedOnceItIsGoodAgain()
    {
        var (relocator, mission, step) = await AMissionWhoseCellIsNoLongerGoodOnTheRelocator(operatorChoice: true);
        Assert.NotNull(relocator.StorageManager);
        var clock = new ManualClock();
        relocator.StorageManager.StoreSearchRetries = new RetryBackoff(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30), clock);

        await TheRouteLoopLooksForTheCell(mission);
        await relocator.StorageManager.StartStoreLu();
        clock.Advance(TimeSpan.FromSeconds(2));
        await relocator.StorageManager.StartStoreLu();

        // Passato il freno il giro non riverifica senza conferma: la missione va al navigatore.
        Assert.Equal(1, _warehouse.Calls.Count(x => x == nameof(IWarehouse.GetCellBusyStateAsync)));
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
        RoutesToBuild.Instance.Items = new();

        await TheRouteLoopLooksForTheCell(mission);
        await relocator.StorageManager.StartStoreLu();

        // Il secondo giro con la conferma ha riverificato davvero (due letture dello stato della
        // cella), e la cella era ancora impegnata.
        Assert.Equal(2, _warehouse.Calls.Count(x => x == nameof(IWarehouse.GetCellBusyStateAsync)));
        Assert.Equal(Guid.Empty, relocator.RunningStepId);

        _warehouse.CellBusyStates[LinkedCellId] = new CellBusyState(Extraction: false, PickUp: false, Storing: false);
        clock.Advance(TimeSpan.FromSeconds(4));
        await relocator.StorageManager.StartStoreLu();

        Assert.Equal(Guid.Empty, relocator.RunningStepId);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
        await TheRouteLoopLooksForTheCell(mission);
        await relocator.StorageManager.StartStoreLu();

        Assert.Equal(step.StepId, relocator.RunningStepId);
        Assert.Equal("A7", mission.StorageDoorCode);
        Assert.DoesNotContain(nameof(IWarehouse.ReserveStorageAsync), _warehouse.Calls);
        Assert.DoesNotContain(nameof(IWarehouse.ReleaseStorageReservationAsync), _warehouse.Calls);
        Assert.Equal(LinkedCellId, _warehouse.StorageReservationsByMission[mission.MissionId].CellId);
    }

    /// <summary>
    /// La conferma della cella vale per il giro di deposito che la trova, anche se quel giro si ferma
    /// prima del deposito. Il navigatore conferma la cella 7 dell'operatore; il giro dopo la trova
    /// impegnata da un'estrazione e aspetta. Quando l'estrazione l'ha lasciata, il deposito non parte
    /// con la conferma di prima - nel frattempo il gestionale puo' aver lasciato la cella - ma la
    /// missione torna al navigatore per una conferma nuova, prima ancora di riverificare la cella.
    /// </summary>
    [Fact]
    public async Task AConfirmationIsNotKeptByADepositThatWaitsForItsCell()
    {
        var (relocator, mission, _) = await AMissionWhoseCellIsNoLongerGoodOnTheRelocator(operatorChoice: true);
        Assert.NotNull(relocator.StorageManager);
        var clock = new ManualClock();
        relocator.StorageManager.StoreSearchRetries = new RetryBackoff(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30), clock);

        await TheRouteLoopLooksForTheCell(mission);
        await relocator.StorageManager.StartStoreLu();

        Assert.Equal(1, _warehouse.Calls.Count(x => x == nameof(IWarehouse.GetCellBusyStateAsync)));
        Assert.Equal(Guid.Empty, relocator.RunningStepId);
        Assert.Null(RoutesToBuild.Instance[mission.MissionId]);

        _warehouse.CellBusyStates[LinkedCellId] = new CellBusyState(Extraction: false, PickUp: false, Storing: false);
        clock.Advance(TimeSpan.FromSeconds(4));
        await relocator.StorageManager.StartStoreLu();

        Assert.Equal(1, _warehouse.Calls.Count(x => x == nameof(IWarehouse.GetCellBusyStateAsync)));
        Assert.Equal(Guid.Empty, relocator.RunningStepId);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
    }

    /// <summary>
    /// Mentre il freno della riverifica corre, il giro di deposito non fa niente: nemmeno mandare la
    /// missione al navigatore, che chiederebbe al gestionale a ogni giro del ciclo delle estrazioni
    /// (ogni due secondi) invece che a ogni scadenza del freno.
    /// </summary>
    [Fact]
    public async Task WhileTheBrakeRunsTheDepositDoesNotGoToTheNavigator()
    {
        var (relocator, mission, _) = await AMissionWhoseCellIsNoLongerGoodOnTheRelocator(operatorChoice: true);
        Assert.NotNull(relocator.StorageManager);
        var clock = new ManualClock();
        relocator.StorageManager.StoreSearchRetries = new RetryBackoff(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30), clock);
        await TheRouteLoopLooksForTheCell(mission);
        await relocator.StorageManager.StartStoreLu();
        Assert.True(relocator.StorageManager.StoreSearchRetries.IsWaiting(mission.MissionId));

        await relocator.StorageManager.StartStoreLu();

        Assert.Null(RoutesToBuild.Instance[mission.MissionId]);
        Assert.Equal(1, _warehouse.Calls.Count(x => x == nameof(IWarehouse.GetCellBusyStateAsync)));
    }

    /// <summary>
    /// Il freno della riverifica vale per la cella che non andava bene: la cella 7 e' impegnata, il
    /// freno corre, e l'operatore manda la missione in S8 (cella 8). Il deposito verso la cella 8 non
    /// aspetta la scadenza di un freno che non la riguarda: il primo giro manda gia' la missione al
    /// navigatore per la conferma. Prima il freno della 7 teneva fermo il deposito nella 8 fino a 30 s.
    /// </summary>
    [Fact]
    public async Task AChangeOfCellDoesNotWaitForTheBrakeOfTheOldOne()
    {
        var (relocator, mission, _) = await AMissionWhoseCellIsNoLongerGoodOnTheRelocator(operatorChoice: true);
        Assert.NotNull(relocator.StorageManager);
        relocator.StorageManager.StoreSearchRetries =
            new RetryBackoff(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30), new ManualClock());
        await TheRouteLoopLooksForTheCell(mission);
        await relocator.StorageManager.StartStoreLu();
        Assert.True(relocator.StorageManager.StoreSearchRetries.IsWaiting(mission.MissionId));
        Assert.Null(RoutesToBuild.Instance[mission.MissionId]);

        Assert.Equal(MissionDestinationOutcome.Changed,
            (await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S8"].ItemGuid,
                MissionGatewayChoice.Clear)).Outcome);
        Assert.Equal(FoundDoorCode, mission.StorageDoorCode);

        await relocator.StorageManager.StartStoreLu();

        Assert.False(relocator.StorageManager.StoreSearchRetries.IsWaiting(mission.MissionId));
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
    }

    /// <summary>
    /// La cella 7 che l'operatore aveva scelto in S7 il gestionale l'ha lasciata scadere (il WCS era
    /// fermo) e data a un'altra UDC, che la impegna (in carico). Sul ricollocatore R, che riverifica
    /// la cella prima di depositare, la missione va prima al navigatore: lui vede che la cella non e'
    /// piu' sua, lascia la porta A7 e le prenota un'altra cella di S7, la 17 della porta B7, con la
    /// rotta verso di lei; e il deposito parte verso B7. Prima la riverifica veniva per prima, la
    /// cella impegnata rispondeva «la aspetta l'operatore» a ogni giro, e la missione non arrivava
    /// mai al navigatore: l'UDC restava su R per sempre.
    /// </summary>
    [Fact]
    public async Task ACellTheWarehouseGaveAwayIsReplacedEvenWhileTheOtherLoadUnitHoldsIt()
    {
        var (relocator, mission, oldStep) = await AMissionWhoseCellIsNoLongerGoodOnTheRelocator(operatorChoice: true);
        Assert.NotNull(relocator.StorageManager);
        mission.Gateway = Guid.Empty;
        _warehouse.CellBusyStates[LinkedCellId] = new CellBusyState(Extraction: false, PickUp: false, Storing: false);
        _warehouse.Cells[LinkedCellId] = Cell(LinkedCellId, "A7") with { Locked = CellLockState.Loading };
        _warehouse.RevokeStorageReservation(mission.MissionId);
        await PlantPersistence.SaveDoorMappingsAsync(_plant["S7"].ItemGuid,
        [
            new DoorMapping { ItemId = _plant["S7"].ItemGuid, DoorCode = "A7", CellId = LinkedCellId, Side = "A" },
            new DoorMapping { ItemId = _plant["S7"].ItemGuid, DoorCode = "B7", CellId = 17, Side = "A" }
        ]);
        _warehouse.Cells[17] = Cell(17, "B7");
        _warehouse.CellPartitionMaps[17] = new CellPartitionMap(17, "0031", "0032",
            AsiDoorB: string.Empty, SubitemDoorB: string.Empty, [new CellPartition("0034", -1)]);
        _warehouse.CellStoringConstraintsByCell[17] = new CellStoringConstraints(150);
        _warehouse.StorageReservations[LoadUnitId] = new StorageReservation(mission.MissionId,
            StorageReservationOutcome.CellFound, 17, "B7", Partition: "0033", StorageDepth: 150, NeedsCompaction: false);

        await relocator.StorageManager.StartStoreLu();

        Assert.Equal(Guid.Empty, relocator.RunningStepId);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);

        await TheRouteLoopLooksForTheCell(mission);

        Assert.Equal(17, _warehouse.StorageReservationsByMission[mission.MissionId].CellId);
        Assert.Equal("B7", mission.StorageDoorCode);
        Assert.Equal(_plant["S7"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.OperatorDestination);
        Assert.DoesNotContain(oldStep, MissionSteps.Instance.List);
        var step = Assert.Single(mission.MissionSteps);

        await relocator.StorageManager.StartStoreLu();

        Assert.Equal(step.StepId, relocator.RunningStepId);
        Assert.Equal("B7", mission.StorageDoorCode);
    }

    /// <summary>
    /// La stessa scena senza il segno: il ricollocatore cerca un'altra cella, come oggi. La
    /// prenotazione della 7 si lascia, si prende la 8 di S8, e la missione ci va senza gateway.
    /// </summary>
    [Fact]
    public async Task WithoutTheMarkTheRelocatorStillLooksForAnotherCell()
    {
        var (relocator, mission, _) = await AMissionWhoseCellIsNoLongerGoodOnTheRelocator(operatorChoice: false);
        Assert.NotNull(relocator.StorageManager);

        await relocator.StorageManager.StartStoreLu();

        Assert.Equal(1, _warehouse.Calls.Count(x => x == nameof(IWarehouse.ReserveStorageAsync)));
        Assert.Equal([mission.MissionId], _warehouse.ReleasedStorageReservations);
        Assert.Equal(FoundCellId, _warehouse.StorageReservationsByMission[mission.MissionId].CellId);
        Assert.Equal(FoundDoorCode, mission.StorageDoorCode);
        Assert.Equal(_plant["S8"].ItemGuid, mission.DestinationItemId);
        Assert.Equal(Guid.Empty, mission.Gateway);
        Assert.False(mission.OperatorDestination);
    }

    /// <summary>
    /// La cella che la riverifica del ricollocatore prenota per una missione della configurazione e'
    /// una cella sua (voce 39, compito 3: risposta 4 del proprietario del 2/10): R prende la cella 8
    /// al posto della 7, impegnata, e il giro di deposito dopo non parte verso A8 da solo, ma manda la
    /// missione al navigatore; confermata la cella presso il gestionale, il deposito parte. Prima il
    /// giro dopo la riverifica depositava senza chiedere.
    /// </summary>
    [Fact]
    public async Task TheCellTheRelocatorsReverificationReservesIsAskedForBeforeTheDeposit()
    {
        var (relocator, mission, _) = await AMissionWhoseCellIsNoLongerGoodOnTheRelocator(operatorChoice: false);
        Assert.NotNull(relocator.StorageManager);
        Assert.False(mission.StorageCellReserved);

        // L'indirizzo della cella 8 anche senza quota (-1), quello che la ricerca legge: con il solo
        // indirizzo della quota 0 la missione restava senza indirizzo, e il deposito non partiva.
        _warehouse.CellPartitionMaps[FoundCellId] = new CellPartitionMap(FoundCellId, "0011", "0012",
            AsiDoorB: string.Empty, SubitemDoorB: string.Empty, [new CellPartition("0013", 0), new CellPartition("0015", -1)]);

        await relocator.StorageManager.StartStoreLu();

        Assert.Equal(FoundDoorCode, mission.StorageDoorCode);
        Assert.True(mission.StorageCellReserved);
        AssertSingleStep(mission, "R", "S8");
        var step = Assert.Single(mission.MissionSteps);
        Assert.Equal(Guid.Empty, relocator.RunningStepId);
        _warehouse.Calls.Clear();

        await relocator.StorageManager.StartStoreLu();

        Assert.Equal(Guid.Empty, relocator.RunningStepId);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
        Assert.Empty(_warehouse.Calls);

        RoutesToBuild.Instance.Items = new();
        await TheRouteLoopLooksForTheCell(mission);
        await relocator.StorageManager.StartStoreLu();

        Assert.Equal(step.StepId, relocator.RunningStepId);
        Assert.Equal(FoundDoorCode, mission.StorageDoorCode);
        Assert.Equal(1, _warehouse.Calls.Count(x => x == nameof(IWarehouse.GetStorageReservationAsync)));
        Assert.DoesNotContain(nameof(IWarehouse.ReserveStorageAsync), _warehouse.Calls);
    }

    // ---------------------------------------------------------------------------------------
    // La vita del segno
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// La ricarica del motore dal database: la missione torna verso X, con il segno, e la
    /// ricostruzione della rotta a motore fermo rispetta il segno (verso X, non verso la destinazione
    /// fissa Y di P). La rotta che la ricarica costruisce da se', in Recover, non guarda ne' il segno
    /// ne' la destinazione fissa: la prova la aspetta solo per non trovarsela addosso dopo.
    /// </summary>
    [Fact]
    public async Task TheOperatorsChoiceSurvivesTheReloadFromTheDatabase()
    {
        var mission = await AnOperatorMissionOnAPositionWithAFixedDestination();

        // Il riavvio: le collezioni di esecuzione si svuotano, i dispositivi restano (PathDevices
        // non si svuota alla ricarica, voci B1 e B12), e le missioni rinascono dalle righe.
        ActiveMissions.Instance.RemoveAll(_ => true);
        MissionSteps.Instance.RemoveAll(_ => true);
        RoutesToBuild.Instance.Items = new();
        Navigator.NavStatus = NavigatorStatusTypes.Recover;

        await ORM.DbInfo.RecoverFailure();

        var reloaded = ActiveMissions.Instance[mission.MissionId];
        Assert.NotNull(reloaded);
        Assert.NotSame(mission, reloaded);
        await reloaded.PositionWork;
        await reloaded.RouteWork.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(_plant["X"].ItemGuid, reloaded.DestinationItemId);
        Assert.True(reloaded.OperatorDestination);

        // La ricarica ricostruisce la rotta in un lavoro che non si puo' attendere
        // (ActiveMissions.Add), che non prova niente sul segno: si aspetta che abbia finito, con un
        // tetto per non appendere la prova, perche' non scriva dopo la ricostruzione qui sotto.
        Assert.True(SpinWait.SpinUntil(() => reloaded.MissionSteps.Count > 0, TimeSpan.FromSeconds(30)));

        // Il motore torna fermo, come dopo la ricarica vera, e la ricostruzione rispetta il segno.
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        reloaded.RemoveMissionSteps();
        Assert.Equal(RouteBuildStatusTypes.Available,
            await Navigator.FindWhereStoreLu(reloaded, _plant["P"], _plant["P"].LookWhereAllocateLu));
        Assert.Equal(_plant["X"].ItemGuid, reloaded.DestinationItemId);
        AssertSingleStep(reloaded, "P", "X");
    }

    /// <summary>
    /// Una riga scritta prima che le due colonne esistessero: le due migrazioni la portano a «non
    /// scelta dall'operatore» e «cella non persa», e la missione ricaricata non ha ne' il segno ne' la
    /// cella persa. Si parte da un file vero allo
    /// schema precedente, con SQL grezzo, perche' il modello di oggi la colonna la scriverebbe.
    /// </summary>
    [Fact]
    public async Task ARowWrittenBeforeTheMarkExistedReloadsAsNotChosenByTheOperator()
    {
        const string beforeTheMark = "20260923090000_AddPendingWarehouseTransitions";
        var directory = Path.Combine(Path.GetTempPath(), $"wcs-operator-destination-{Guid.NewGuid():N}");
        var missionId = Guid.NewGuid();
        var destination = Guid.NewGuid();

        try
        {
            Directory.CreateDirectory(directory);
            var options = new DbContextOptionsBuilder<WcsDbContext>()
                .UseSqlite(new SqliteConnectionStringBuilder
                {
                    DataSource = Path.Combine(directory, WcsDatabase.OperationalFileName),
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Pooling = false
                }.ToString())
                .Options;

            using (var context = new WcsDbContext(options))
            {
                context.GetInfrastructure().GetRequiredService<IMigrator>().Migrate(beforeTheMark);
                context.Database.ExecuteSqlRaw(
                    "INSERT INTO Missions (MissionId, LoadUnitId, DestinationItemId, Date, Status, MissionType, " +
                    "OperationType, BufferCellId, ExtractionQueueId, LoadUnitTypeId, HandlingMode, StorageDepth, " +
                    "NoteAssignmentId, NoteProcessingId, ActiveFilter, Tare, ReallocatedDestination) " +
                    "VALUES ({0}, 0, {1}, {2}, 0, 0, 2, 0, 0, 0, 0, -1, 0, 0, 0, 0, 0);",
                    missionId, destination, new DateTime(2026, 9, 30, 12, 0, 0));
            }

            using var database = WcsDatabase.Open(directory);
            await using var reading = database.CreateContext();
            var row = Assert.Single(await reading.Missions.ToListAsync());

            Assert.Equal(missionId, row.MissionId);
            Assert.False(row.OperatorDestination);
            Assert.False(row.StorageCellLost);
            var reloaded = MissionStore.ToPathMission(row);
            Assert.Equal(destination, reloaded.DestinationItemId);
            Assert.False(reloaded.OperatorDestination);
            Assert.False(reloaded.StorageCellLost);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// La missione arriva alla destinazione scelta: il segno cade, come il gateway raggiunto, e il
    /// dispositivo raggiunto fa quello che fa con ogni missione arrivata - qui la sua destinazione
    /// fissa Z. Con il segno ancora su, la ricostruzione non avrebbe niente verso cui costruire e
    /// la missione girerebbe in coda per sempre.
    /// </summary>
    [Fact]
    public async Task ReachingTheChosenDestinationDropsTheMark()
    {
        foreach (var name in new[] { "P", "X", "Z" }) _plant.Device(name);
        _plant.Chain("P", "X", "Z");
        _plant["X"].DestinationItemId = _plant["Z"].ItemGuid;
        var mission = await MissionOn("P");
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        Assert.True(mission.SetOperatorDestination(_plant["X"].ItemGuid));

        await _plant["X"].LoadLu(mission.MissionId);
        await mission.PositionWork;

        Assert.Equal(_plant["X"].ItemGuid, mission.PositionItemId);
        Assert.False(mission.OperatorDestination);

        Assert.Equal(RouteBuildStatusTypes.Available,
            await Navigator.FindWhereStoreLu(mission, _plant["X"], _plant["X"].LookWhereAllocateLu));
        Assert.Equal(_plant["Z"].ItemGuid, mission.DestinationItemId);
        AssertSingleStep(mission, "X", "Z");
    }

    /// <summary>
    /// La destinazione cambia per un'altra via - qui il rifiuto chiesto dall'operatore dal
    /// dispositivo, che manda l'UDC al punto di rifiuto: da quel momento la destinazione non e'
    /// piu' quella scelta, e il segno cade.
    /// </summary>
    [Fact]
    public async Task ADestinationChangedAnotherWayDropsTheMark()
    {
        using var log = new RecordingLogWriter();
        foreach (var name in new[] { "P", "X", "R" }) _plant.Device(name);
        _plant.Link("P", "X");
        _plant.Link("P", "R");
        _plant["P"].RejectDestinationUserRequest = _plant["R"].ItemGuid;
        var mission = await MissionOn("P");
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        Assert.True(mission.SetOperatorDestination(_plant["X"].ItemGuid));

        Assert.True(await mission.RejectTo(PathMission.RejectionTypes.DefaultNavigatorRejectionItem));

        Assert.Equal(_plant["R"].ItemGuid, mission.DestinationItemId);
        Assert.False(mission.OperatorDestination);

        // La scelta che sparisce lascia una riga di avviso, scritta anche con la diagnostica spenta.
        await log.Wait((line, level) => level == Log.Interface.LogEntryType.Warning &&
                                        line.Contains(mission.MissionId.ToString()) &&
                                        line.Contains("la scelta dell'operatore cade"))
            .WaitAsync(TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// Scegliere la destinazione che la missione ha gia' alza il segno senza toccare la rotta: il
    /// setter della destinazione, anche per lo stesso valore, cancellava gli step senza passare dai
    /// dispositivi.
    /// </summary>
    [Fact]
    public async Task ChoosingTheDestinationTheMissionAlreadyHasKeepsItsRoute()
    {
        foreach (var name in new[] { "P", "X" }) _plant.Device(name);
        _plant.Link("P", "X");
        var mission = await MissionOn("P");
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        mission.DestinationItemId = _plant["X"].ItemGuid;
        var step = _plant.PlannedStep(mission, "P", "X");

        Assert.True(mission.SetOperatorDestination(_plant["X"].ItemGuid));

        Assert.True(mission.OperatorDestination);
        Assert.Same(step, Assert.Single(mission.MissionSteps));
    }

    /// <summary>
    /// L'operatore ha scelto un gruppo, GX: il segno cade quando la missione arriva su un suo
    /// membro, M, come il gateway raggiunto in un dispositivo del suo gruppo.
    /// </summary>
    [Fact]
    public async Task ReachingAMemberOfTheChosenGroupDropsTheMark()
    {
        foreach (var name in new[] { "P", "M" }) _plant.Device(name);
        var group = _plant.Device("GX", DeviceTypes.GroupOfAutomationDevices);
        group.GroupItemId = 93;
        _plant["M"].BelongingGroupId = group.GroupItemId;
        Assert.Same(group, _plant["M"].BelongedGroup);
        _plant.Link("P", "M");
        var mission = await MissionOn("P");
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        Assert.True(mission.SetOperatorDestination(group.ItemGuid));

        await _plant["M"].LoadLu(mission.MissionId);
        await mission.PositionWork;

        Assert.Equal(_plant["M"].ItemGuid, mission.PositionItemId);
        Assert.False(mission.OperatorDestination);
    }

    /// <summary>
    /// Riscrivere la stessa destinazione non e' un cambio: il segno resta. E' la scrittura che fa
    /// chi rimette la destinazione che la missione ha gia' (la ricarica, la scelta fra due
    /// destinazioni fisse quando vince quella attuale).
    /// </summary>
    [Fact]
    public async Task WritingTheSameDestinationAgainKeepsTheMark()
    {
        foreach (var name in new[] { "P", "X" }) _plant.Device(name);
        _plant.Link("P", "X");
        var mission = await MissionOn("P");
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        Assert.True(mission.SetOperatorDestination(_plant["X"].ItemGuid));

        mission.DestinationItemId = _plant["X"].ItemGuid;

        Assert.True(mission.OperatorDestination);
    }

    /// <summary>
    /// Con uno step in volo il setter della destinazione scarta la scrittura: il segno non si alza
    /// su una destinazione che l'operatore non ha scelto, e chi chiama lo sa dal risultato.
    /// </summary>
    [Fact]
    public async Task AStepInFlightRefusesTheOperatorsChoiceAndLeavesTheMarkDown()
    {
        foreach (var name in new[] { "P", "X", "W" }) _plant.Device(name);
        _plant.Link("P", "X");
        _plant.Link("P", "W");
        var mission = await MissionOn("P");
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        mission.DestinationItemId = _plant["X"].ItemGuid;
        var step = _plant.PlannedStep(mission, "P", "X");
        _plant["P"].RunningStepId = step.StepId;
        _plant["P"].Status = DeviceStatusTypes.Moving;

        Assert.False(mission.SetOperatorDestination(_plant["W"].ItemGuid));

        Assert.Equal(_plant["X"].ItemGuid, mission.DestinationItemId);
        Assert.False(mission.OperatorDestination);
    }

    // ---------------------------------------------------------------------------------------
    // Scene
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Il gestore degli eventi del motore che fa una cosa sola, alla prima chiusura di un comando:
    /// e' il punto, dentro <c>RejectTo</c>, fra lo sguardo al segno e la scrittura della destinazione.
    /// </summary>
    private sealed class OnFirstCommandChanged(Action action) : IEngineEventSink
    {
        private int _done;

        public void CommandChanged(CommandRecord command)
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) action();
        }

        public void ItemChanged(AutomationDevice device) { }
        public void NavigatorStatusChanged() { }
        public void MissionChanged(PathMission mission, EngineMissionChange change) { }
        public void ExtractionQueueChanged(ExtractionQueueItem item, EngineQueueChange change) { }
    }

    /// <summary>Il giro del ciclo delle rotte, come lo fa il motore ogni 10 ms.</summary>
    private static Task OneTurnOfTheRouteLoop()
    {
        var method = typeof(Navigator).GetMethod("BuildRoutePath", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (Task)method!.Invoke(null, null)!;
    }

    /// <summary>P collegato a X e a Y, con Y destinazione fissa di P.</summary>
    private void ThreeDevicesWithAFixedDestination()
    {
        foreach (var name in new[] { "P", "X", "Y" }) _plant.Device(name);
        _plant.Link("P", "X");
        _plant.Link("P", "Y");
        _plant["P"].DestinationItemId = _plant["Y"].ItemGuid;
    }

    /// <summary>
    /// Una missione su P, senza rotta, con la destinazione X scelta dall'operatore; P ha Y come
    /// destinazione fissa. Il navigatore e' fermo.
    /// </summary>
    private async Task<PathMission> AnOperatorMissionOnAPositionWithAFixedDestination()
    {
        ThreeDevicesWithAFixedDestination();
        var mission = await MissionOn("P");
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        Assert.True(mission.SetOperatorDestination(_plant["X"].ItemGuid));
        Assert.Empty(mission.MissionSteps);
        return mission;
    }

    /// <summary>
    /// P collegato a X e al magazzino S8, con la cella collegata e la modalita' di deposito data;
    /// il gestionale risponde per l'unita' di carico la cella 8 di S8, porta A8.
    /// </summary>
    private async Task APositionThatStoresInItsLinkedCell(LookWhereAllocateLuTypes mode)
    {
        var position = _plant.Device("P");
        _plant.Device("X");
        var storage = _plant.Device("S8", DeviceTypes.Storage);
        _plant.Link("P", "X");
        _plant.Link("P", "S8");

        storage.RelocateItemId = position.ItemGuid;
        position.LinkedCellId = LinkedCellId;
        position.LookWhereAllocateLu = mode;

        await PlantPersistence.SaveDoorMappingsAsync(storage.ItemGuid,
            [new DoorMapping { ItemId = storage.ItemGuid, DoorCode = FoundDoorCode, CellId = FoundCellId, Side = "A" }]);

        _warehouse.Cells[LinkedCellId] = Cell(LinkedCellId, "A7");
        _warehouse.Cells[FoundCellId] = Cell(FoundCellId, FoundDoorCode);
        _warehouse.CellPartitionMaps[FoundCellId] = new CellPartitionMap(FoundCellId, "0011", "0012",
            AsiDoorB: string.Empty, SubitemDoorB: string.Empty, [new CellPartition("0013", 0)]);
    }

    /// <summary>
    /// Come <see cref="APositionThatStoresInItsLinkedCell"/>, oppure, con
    /// <paramref name="linkedToTheStorage"/> falso, P non collegato al magazzino: P va in S8
    /// passando da Q, che ne e' il ricollocatore, e la ricerca e' quella di una cella qualunque
    /// (<c>StoreInAnyAvailableCell</c>) invece che della cella collegata.
    /// </summary>
    private async Task APositionThatStores(LookWhereAllocateLuTypes mode, bool linkedToTheStorage)
    {
        if (linkedToTheStorage)
        {
            await APositionThatStoresInItsLinkedCell(mode);
            return;
        }

        var position = _plant.Device("P");
        _plant.Device("X");
        var relocator = _plant.Device("Q");
        var storage = _plant.Device("S8", DeviceTypes.Storage);
        _plant.Link("P", "X");
        _plant.Link("P", "Q");
        _plant.Link("Q", "S8");

        storage.RelocateItemId = relocator.ItemGuid;
        position.LinkedCellId = LinkedCellId;
        position.LookWhereAllocateLu = mode;
        Assert.False(position.IsLinkedToStorage);

        await PlantPersistence.SaveDoorMappingsAsync(storage.ItemGuid,
            [new DoorMapping { ItemId = storage.ItemGuid, DoorCode = FoundDoorCode, CellId = FoundCellId, Side = "A" }]);

        _warehouse.Cells[LinkedCellId] = Cell(LinkedCellId, "A7");
        _warehouse.Cells[FoundCellId] = Cell(FoundCellId, FoundDoorCode);
        _warehouse.CellPartitionMaps[FoundCellId] = new CellPartitionMap(FoundCellId, "0011", "0012",
            AsiDoorB: string.Empty, SubitemDoorB: string.Empty, [new CellPartition("0013", 0)]);
    }

    /// <summary>
    /// The plant of <see cref="APositionThatStoresInItsLinkedCell"/> with the given storing mode on P,
    /// plus the conveyor C (on its own) and the door A9 of the cell 9 of S8. On
    /// <paramref name="position"/> an extraction out of the cell 9 (pick-up door A9, source S8) bound for
    /// the bay X, with no storage door and no route; the warehouse would answer the cell 8 (door A8).
    /// The engine is stopped, and the calls to the warehouse start from here.
    /// </summary>
    private async Task<PathMission> AnExtractionOfS8BoundForTheBayXOn(string position, LookWhereAllocateLuTypes mode)
    {
        await APositionThatStoresInItsLinkedCell(mode);
        _plant.Device("C");
        var storage = _plant["S8"];
        await PlantPersistence.SaveDoorMappingsAsync(storage.ItemGuid,
        [
            new DoorMapping { ItemId = storage.ItemGuid, DoorCode = FoundDoorCode, CellId = FoundCellId, Side = "A" },
            new DoorMapping { ItemId = storage.ItemGuid, DoorCode = "A9", CellId = 9, Side = "A" }
        ]);

        var mission = await MissionOn(position, MissionTypes.Extraction);
        TheWarehouseAnswersCell8For(mission);
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        mission.SourceItemId = storage.ItemGuid;
        mission.PickUpDoorCode = "A9";
        mission.DestinationItemId = _plant["X"].ItemGuid;
        Assert.False(mission.OperatorDestination);
        Assert.Empty(mission.MissionSteps);
        _warehouse.Calls.Clear();
        return mission;
    }

    /// <summary>The extraction of <see cref="AnExtractionOfS8BoundForTheBayXOn"/> is still that, bound for X.</summary>
    private void TheExtractionStillGoesToTheBayX(PathMission mission, string from)
    {
        Assert.Equal(_plant["X"].ItemGuid, mission.DestinationItemId);
        Assert.False(mission.OperatorDestination);
        Assert.Equal(MissionTypes.Extraction, mission.MissionType);
        Assert.True(string.IsNullOrEmpty(mission.StorageDoorCode));
        Assert.Equal("A9", mission.PickUpDoorCode);
        Assert.Empty(_warehouse.StorageReservationsByMission);
        AssertSingleStep(mission, from, "X");
    }

    /// <summary>
    /// <see cref="TwoMissionsOfTheSameTypeOnAGroup"/> without the mark, with the second mission (on B)
    /// bound for X by the configuration, not by the operator: the warehouse would answer NEW-A for the
    /// first and NEW-B for the second. With <paramref name="boundForXFirst"/> the second one comes first
    /// on the group.
    /// </summary>
    private async Task<(PathMission Normal, PathMission BoundForX)> TwoMissionsOfTheSameTypeOnAGroupTheSecondBoundForX(
        bool boundForXFirst)
    {
        var (normal, boundForX) = await TwoMissionsOfTheSameTypeOnAGroup(boundForXFirst, withTheMark: false);
        boundForX.DestinationItemId = _plant["X"].ItemGuid;
        Assert.False(boundForX.OperatorDestination);
        _warehouse.Calls.Clear();
        return (normal, boundForX);
    }

    /// <summary>
    /// <see cref="TwoMissionsOfTheSameTypeOnAGroup"/> without the mark, with the second mission (on B, a
    /// member of the group G that relocates S) sent by the operator to G «only to bring it there» (item 54,
    /// task 6), as the commit leaves it after the arrival: destination G, the choice, no mark.
    /// </summary>
    private async Task<(PathMission Normal, PathMission BroughtToG)> TwoMissionsOfTheSameTypeOnAGroupTheSecondBroughtToGToStay()
    {
        var (normal, broughtToG) = await TwoMissionsOfTheSameTypeOnAGroup(operatorMissionFirst: false, withTheMark: false);
        broughtToG.DestinationItemId = _plant["G"].ItemGuid;
        broughtToG.RelocatorArrival = RelocatorArrival.Stay;
        Assert.True(broughtToG.RelocatorArrivalApplies);
        _warehouse.Calls.Clear();
        return (normal, broughtToG);
    }

    /// <summary>After the group: the normal mission has the cell NEW-A of S, asked once; the one brought to G stays there, with no door and no route.</summary>
    private void TheNormalOneGetsNewAAndTheOtherStaysOnG(PathMission normal, PathMission broughtToG)
    {
        Assert.Equal(_plant["S"].ItemGuid, normal.DestinationItemId);
        Assert.Equal("NEW-A", normal.StorageDoorCode);
        Assert.Equal(_plant["G"].ItemGuid, broughtToG.DestinationItemId);
        Assert.True(string.IsNullOrEmpty(broughtToG.StorageDoorCode));
        Assert.Equal(RelocatorArrival.Stay, broughtToG.RelocatorArrival);
        Assert.Empty(broughtToG.MissionSteps);
        Assert.Equal(1, _warehouse.Calls.Count(call => call == nameof(IWarehouse.ReserveStorageAsync)));
    }

    /// <summary>
    /// After the group: the normal mission has the cell NEW-A of S, asked once to the warehouse; the one
    /// bound for X kept X, with no door, and has the route B->X.
    /// </summary>
    private void TheNormalOneGetsNewAAndTheOtherGoesToX(PathMission normal, PathMission boundForX)
    {
        Assert.Equal(_plant["S"].ItemGuid, normal.DestinationItemId);
        Assert.Equal("NEW-A", normal.StorageDoorCode);
        Assert.Equal(_plant["X"].ItemGuid, boundForX.DestinationItemId);
        Assert.True(string.IsNullOrEmpty(boundForX.StorageDoorCode));
        Assert.False(boundForX.OperatorDestination);
        Assert.Equal(1, _warehouse.Calls.Count(call => call == nameof(IWarehouse.ReserveStorageAsync)));
        AssertSingleStep(boundForX, "B", "X");
    }

    /// <summary>
    /// Il cambio dell'operatore come lo fara' il compito 3 nel suo tratto di commit: la destinazione
    /// con il segno, e la rotta verso di lei. Restituisce lo step della rotta, per riconoscerlo dopo.
    /// </summary>
    private async Task<MissionStep> TheOperatorSendsItTo(PathMission mission, string device)
    {
        Assert.True(mission.SetOperatorDestination(_plant[device].ItemGuid));
        await mission.BuildPathToDestination();
        var step = Assert.Single(mission.MissionSteps);
        Assert.Equal(device, _plant.NameOf(step.ToNodeId));
        return step;
    }

    /// <summary>La cella 8 di S8, porta A8, che il gestionale risponde per la missione.</summary>
    private void TheWarehouseAnswersCell8For(PathMission mission) =>
        _warehouse.StorageReservations[mission.LuId] = new StorageReservation(mission.MissionId,
            StorageReservationOutcome.CellFound, FoundCellId, FoundDoorCode, Partition: "0013", StorageDepth: 150,
            NeedsCompaction: false);

    /// <summary>
    /// Due UDC dello stesso tipo sul gruppo G, senza segno (<see cref="TwoMissionsOfTheSameTypeOnAGroup"/>):
    /// la ricerca per tipo del gruppo prenota per la prima la cella 201 (porta NEW-A), e la seconda ne
    /// copia la porta. Poi il gestionale non tiene piu' la 201 per la prima (scaduta, o revocata), e per
    /// la sua UDC ha la 202 (porta NEW-B). Il giro del gruppo lascia fuori il deposito della prima, che
    /// va al navigatore, e quello della copia, che aspetta con lei. Il registro delle chiamate riparte
    /// da qui, e la coda delle rotte e' vuota.
    /// </summary>
    /// <param name="madeInMode">
    /// Non dato, la copia la fa la ricerca per tipo del giro del gruppo, nella modalita' per tipo di UDC.
    /// Dato, il gruppo ha questa modalita' e la copia la fa la ricerca di una cella nuova dopo un rifiuto
    /// del PLC (<c>FindNewStoreLocationGrouped</c>), che cerca per tipo in ogni modalita'.
    /// </param>
    private async Task<(PathMission First, PathMission Second, MissionStep FirstStep, MissionStep SecondStep)>
        ACopyOfTheDoorOfACellTheWarehouseNoLongerHolds(LookWhereAllocateLuTypes? madeInMode = null)
    {
        var (first, second) = await TwoMissionsOfTheSameTypeOnAGroup(operatorMissionFirst: false, withTheMark: false);

        if (madeInMode is { } mode)
        {
            _plant["G"].LookWhereAllocateLu = mode;
            Assert.True(await _plant["G"].StorageManager!.FindNewStoreLocationGroupedForTest().WaitAsync(TimeSpan.FromSeconds(30)));
        }
        else
        {
            await _plant["G"].VerifyWhereAllocateLuGrouped([first, second]).WaitAsync(TimeSpan.FromSeconds(30));
        }

        Assert.True(first.StorageCellReserved);
        Assert.False(second.StorageCellReserved);
        Assert.Equal("NEW-A", second.StorageDoorCode);

        var group = _plant["G"];
        group.RecordStorageLength = RecordStorageTypes.FromPlc;
        group.Status = DeviceStatusTypes.Ready;
        var firstStep = Assert.Single(first.MissionSteps);
        var secondStep = Assert.Single(second.MissionSteps);
        _warehouse.RevokeStorageReservation(first.MissionId);
        _warehouse.StorageReservations[first.LuId] = new StorageReservation(first.MissionId,
            StorageReservationOutcome.CellFound, 202, "NEW-B", "0006", 100, NeedsCompaction: false);
        RoutesToBuild.Instance.Items = new();

        await group.StepExecutor!.ExecuteGroupedMOVE_LU_TO([firstStep, secondStep]);

        Assert.Equal(Guid.Empty, firstStep.GroupedMovementId);
        Assert.Equal(Guid.Empty, secondStep.GroupedMovementId);
        Assert.Same(first, RoutesToBuild.Instance[first.MissionId]);
        RoutesToBuild.Instance.Items = new();
        _warehouse.Calls.Clear();
        return (first, second, firstStep, secondStep);
    }

    /// <summary>
    /// Due UDC dello stesso tipo sul gruppo G, senza segno (<see cref="TwoMissionsOfTheSameTypeOnAGroup"/>):
    /// la ricerca per tipo del gruppo prenota per la prima, su A, la cella 201 (porta NEW-A) con la rotta
    /// A->S, e la seconda, su B, ne copia la porta con la rotta B->S. Il registro delle chiamate riparte
    /// da qui.
    /// </summary>
    private async Task<(PathMission First, PathMission Second, MissionStep SecondStep)> ACopyOfTheDoorOfTheFirst()
    {
        var (first, second) = await TwoMissionsOfTheSameTypeOnAGroup(operatorMissionFirst: false, withTheMark: false);
        await _plant["G"].VerifyWhereAllocateLuGrouped([first, second]).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(first.StorageCellReserved);
        Assert.Equal("NEW-A", first.StorageDoorCode);
        Assert.Equal(201, _warehouse.StorageReservationsByMission[first.MissionId].CellId);
        AssertSingleStep(first, "A", "S");
        Assert.False(second.StorageCellReserved);
        Assert.Equal("NEW-A", second.StorageDoorCode);
        AssertSingleStep(second, "B", "S");
        _warehouse.Calls.Clear();
        return (first, second, Assert.Single(second.MissionSteps));
    }

    /// <summary>
    /// Il gruppo G con due membri A e B collegati al magazzino S (e B anche a X), e la modalita'
    /// per tipo di UDC; su A una missione senza segno, su B una con il segno verso X, tutte e due
    /// dello stesso tipo. Il gestionale risponde per la prima la cella 201, porta NEW-A. L'ordine
    /// in cui il gruppo le vede e' quello delle date.
    /// </summary>
    /// <param name="operatorMissionFirst">La missione su B viene prima di quella su A.</param>
    /// <param name="withTheMark">
    /// Falso per le prove di controllo: la missione su B resta senza segno, e il gestionale ha una
    /// risposta anche per lei, la cella 202 dello stesso magazzino con la porta NEW-B. Cosi' chi
    /// non prende il selettore e chiede al gestionale si vede dalla porta, e dalle due prenotazioni.
    /// </param>
    private async Task<(PathMission Normal, PathMission Chosen)> TwoMissionsOfTheSameTypeOnAGroup(bool operatorMissionFirst,
        bool withTheMark = true)
    {
        var group = _plant.Device("G", DeviceTypes.GroupOfAutomationDevices);
        group.GroupItemId = 71;
        group.LookWhereAllocateLu = LookWhereAllocateLuTypes.BuildRouteToStoreByIdLuType;

        var first = _plant.Device("A");
        first.BelongingGroupId = group.GroupItemId;
        first.OrderNumberGroup = 1;
        first.LinkedCellId = 301;

        var second = _plant.Device("B");
        second.BelongingGroupId = group.GroupItemId;
        second.OrderNumberGroup = 2;
        second.LinkedCellId = 302;

        _plant.Device("X");
        var storage = _plant.Device("S", DeviceTypes.Storage);
        storage.RelocateItemId = group.ItemGuid;
        _plant.Link("G", "S");
        _plant.Link("A", "S");
        _plant.Link("B", "S");
        _plant.Link("B", "X");

        await PlantPersistence.SaveDoorMappingsAsync(storage.ItemGuid,
            [new DoorMapping { ItemId = storage.ItemGuid, DoorCode = "NEW-A", CellId = 201, Side = "A" }]);

        var normal = new PathMission { PositionItemId = first.ItemGuid, LuId = 11, IdLuType = 1 };
        var chosen = new PathMission { PositionItemId = second.ItemGuid, LuId = 22, IdLuType = 1 };
        ActiveMissions.Instance.Add(normal, buildRoute: false);
        ActiveMissions.Instance.Add(chosen, buildRoute: false);
        await normal.PositionWork;
        await chosen.PositionWork;

        var now = DateTime.Now;
        normal.Date = operatorMissionFirst ? now.AddSeconds(1) : now;
        chosen.Date = operatorMissionFirst ? now : now.AddSeconds(1);

        _warehouse.Cells[301] = Cell(301, "LINK");
        _warehouse.Cells[302] = Cell(302, "LINK");
        _warehouse.Cells[201] = Cell(201, "NEW-A");
        _warehouse.CellPartitionMaps[201] = new CellPartitionMap(201, "0001", "0002",
            AsiDoorB: string.Empty, SubitemDoorB: string.Empty, [new CellPartition("0003", 0)]);
        _warehouse.StorageReservations[normal.LuId] = new StorageReservation(normal.MissionId,
            StorageReservationOutcome.CellFound, 201, "NEW-A", "0003", 100, NeedsCompaction: false);

        Navigator.NavStatus = NavigatorStatusTypes.Stop;

        if (withTheMark)
        {
            Assert.True(chosen.SetOperatorDestination(_plant["X"].ItemGuid));
        }
        else
        {
            await PlantPersistence.SaveDoorMappingsAsync(storage.ItemGuid,
            [
                new DoorMapping { ItemId = storage.ItemGuid, DoorCode = "NEW-A", CellId = 201, Side = "A" },
                new DoorMapping { ItemId = storage.ItemGuid, DoorCode = "NEW-B", CellId = 202, Side = "A" }
            ]);
            _warehouse.Cells[202] = Cell(202, "NEW-B");
            _warehouse.CellPartitionMaps[202] = new CellPartitionMap(202, "0004", "0005",
                AsiDoorB: string.Empty, SubitemDoorB: string.Empty, [new CellPartition("0006", 0)]);
            _warehouse.StorageReservations[chosen.LuId] = new StorageReservation(chosen.MissionId,
                StorageReservationOutcome.CellFound, 202, "NEW-B", "0006", 100, NeedsCompaction: false);
        }

        Assert.Equal(2, group.LUsLoaded.Count);
        Assert.Same(operatorMissionFirst ? chosen : normal, group.LUsLoaded[0]);
        _warehouse.Calls.Clear();

        return (normal, chosen);
    }

    /// <summary>
    /// Un'estrazione su P verso X, che sta per arrivare su D: D distribuisce le estrazioni sui
    /// polmoni che raggiunge, e ne raggiunge uno, BUF. Con <paramref name="operatorChoice"/> X e'
    /// scelta dall'operatore.
    /// </summary>
    private async Task<PathMission> AnExtractionArrivingOnADistributor(bool operatorChoice)
    {
        foreach (var name in new[] { "P", "D", "X" }) _plant.Device(name);
        _plant.Device("BUF", DeviceTypes.Buffer);
        _plant.Link("P", "D");
        _plant.Link("D", "X");
        _plant.Link("D", "BUF");
        _plant["D"].DistributeOnBuffers = true;
        Assert.Equal([_plant["BUF"]], _plant["D"].ReachableBuffers);

        var mission = await MissionOn("P", MissionTypes.Extraction);
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        if (operatorChoice)
            Assert.True(mission.SetOperatorDestination(_plant["X"].ItemGuid));
        else
            mission.DestinationItemId = _plant["X"].ItemGuid;

        return mission;
    }

    /// <summary>
    /// Il ricollocatore R dei magazzini S7 e S8, che deposita convertendo la profondita' in
    /// partizione: e' il dispositivo che prima di depositare riverifica la cella
    /// (<c>StartStoreLu</c>). Su R una missione verso S7 con la cella 7 (porta A7), che il gestionale
    /// le tiene, il gateway G e la rotta R->S7; la cella 7 e' impegnata da un'estrazione, e una
    /// ricerca nuova avrebbe dal gestionale la cella 8 di S8 (porta A8). Con
    /// <paramref name="operatorChoice"/> S7 e' scelto dall'operatore, come lo lascia il cambio verso
    /// un magazzino (compito 3): il segno e' ancora su, perche' R non e' S7. Il navigatore e' fermo.
    /// </summary>
    private async Task<(AutomationDevice Relocator, PathMission Mission, MissionStep Step)>
        AMissionWhoseCellIsNoLongerGoodOnTheRelocator(bool operatorChoice)
    {
        var relocator = _plant.Device("R");
        var chosen = _plant.Device("S7", DeviceTypes.Storage);
        var other = _plant.Device("S8", DeviceTypes.Storage);
        _plant.Device("G");
        _plant.Link("R", "S7");
        _plant.Link("R", "S8");

        chosen.RelocateItemId = relocator.ItemGuid;
        other.RelocateItemId = relocator.ItemGuid;
        relocator.RecordStorageLength = RecordStorageTypes.ConvertDepthToPartition;
        relocator.LinkedCellId = LinkedCellId;
        Assert.True(relocator.IsLinkedToStorage);

        await PlantPersistence.SaveDoorMappingsAsync(chosen.ItemGuid,
            [new DoorMapping { ItemId = chosen.ItemGuid, DoorCode = "A7", CellId = LinkedCellId, Side = "A" }]);
        await PlantPersistence.SaveDoorMappingsAsync(other.ItemGuid,
            [new DoorMapping { ItemId = other.ItemGuid, DoorCode = FoundDoorCode, CellId = FoundCellId, Side = "A" }]);

        var mission = await MissionOn("R");
        Navigator.NavStatus = NavigatorStatusTypes.Stop;

        if (operatorChoice)
            Assert.True(mission.SetOperatorDestination(chosen.ItemGuid));
        else
            mission.DestinationItemId = chosen.ItemGuid;

        mission.StorageDoorCode = "A7";
        mission.DestinationAsi = "0001";
        mission.DestinationSubitem = "0002";
        mission.DestinationPartition = "0003";
        mission.Gateway = _plant["G"].ItemGuid;
        var step = _plant.PlannedStep(mission, "R", "S7");
        Assert.Same(step, mission.StepToDo);
        Assert.Equal(operatorChoice, mission.OperatorDestination);

        _warehouse.Cells[LinkedCellId] = Cell(LinkedCellId, "A7");
        _warehouse.Cells[FoundCellId] = Cell(FoundCellId, FoundDoorCode);
        _warehouse.CellPartitionMaps[LinkedCellId] = new CellPartitionMap(LinkedCellId, "0001", "0002",
            AsiDoorB: string.Empty, SubitemDoorB: string.Empty, [new CellPartition("0003", 0)]);
        _warehouse.CellPartitionMaps[FoundCellId] = new CellPartitionMap(FoundCellId, "0011", "0012",
            AsiDoorB: string.Empty, SubitemDoorB: string.Empty, [new CellPartition("0013", 0)]);
        _warehouse.CellBusyStates[LinkedCellId] = new CellBusyState(Extraction: true, PickUp: false, Storing: false);
        _warehouse.CellStoringConstraintsByCell[LinkedCellId] = new CellStoringConstraints(150);
        _warehouse.CellStoringConstraintsByCell[FoundCellId] = new CellStoringConstraints(150);
        _warehouse.StorageReservationsByMission[mission.MissionId] = new StorageReservation(mission.MissionId,
            StorageReservationOutcome.CellFound, LinkedCellId, "A7", Partition: "0003", StorageDepth: 150,
            NeedsCompaction: false);
        TheWarehouseAnswersCell8For(mission);
        _warehouse.Calls.Clear();

        return (relocator, mission, step);
    }

    /// <summary>
    /// Una missione a bordo di <paramref name="position"/>, aggiunta in Recover (in Stop
    /// <c>ActiveMissions.Add</c> esce subito), con il primo posizionamento gia' finito.
    /// </summary>
    private async Task<PathMission> MissionOn(string position, MissionTypes type = MissionTypes.MovingToItem)
    {
        Assert.Equal(NavigatorStatusTypes.Recover, Navigator.NavStatus);

        var mission = new PathMission
        {
            PositionItemId = _plant[position].ItemGuid,
            LuId = LoadUnitId,
            MissionType = type,
            OperationType = OperationTypes.Outbound
        };
        ActiveMissions.Instance.Add(mission, buildRoute: false);
        Assert.Same(mission, _plant[position].LoadedLu);
        await mission.PositionWork;
        return mission;
    }

    /// <summary>
    /// Il giro delle rotte pesca la missione e avvia la ricerca della sua cella (<c>StartCellSearch</c>,
    /// come la avvia <c>BuildRoutePath</c>), e la prova la aspetta, con un tetto che fa fallire invece
    /// di appendere.
    /// </summary>
    private static async Task TheRouteLoopLooksForTheCell(PathMission mission)
    {
        Assert.NotNull(mission.PositionItem);
        var method = typeof(Navigator).GetMethod("StartCellSearch", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        method!.Invoke(null, [mission, mission.PositionItem]);

        if (Navigator.CellSearchInFlight(mission.MissionId) is { } search)
            await search.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private void AssertSingleStep(PathMission mission, string from, string to)
    {
        var step = Assert.Single(mission.MissionSteps);
        Assert.Equal(from, _plant.NameOf(step.FromNodeId));
        Assert.Equal(to, _plant.NameOf(step.ToNodeId));
    }

    private static CellDescription Cell(decimal cellId, string doorCode) =>
        new(cellId, AreaId: 1, WarehouseId: 1, Depth: 1, Inverted: false, CellLockState.None,
            AllowedLoadUnitTypeId: null, GroupId: null, GroupOrder: 0, doorCode, DoorCodeB: string.Empty,
            DoorCount: 1, MultiDepthStorage: false);
}
