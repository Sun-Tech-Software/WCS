using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Wcs.Connection.Interface;
using Wcs.Connection.Interface.CommandData;
using Wcs.Connection.Interface.Events;
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

namespace Wcs.Engine.Test;

/// <summary>
/// Il cambio di destinazione dell'operatore (<see cref="MissionGateway.ChangeDestinationAsync"/>) e
/// gli avvii degli step che il semaforo del dispositivo di posizione non vede (compito 3, primo giro
/// di correzioni): il prelievo, che avvia il dispositivo che preleva e porta il gettone sulla
/// destinazione dello step, e i movimenti di un gruppo, che girano nel ciclo del gruppo senza
/// semafori e portano il gettone sul gruppo. With a step under way, or being started, the change waits
/// for its end (item 54, decision 4: it was a refusal as a step in flight until then), and touches
/// nothing meanwhile; e con il cambio cominciato l'avvio non parte. Con la
/// stessa scena del prelievo, l'estrazione con l'UDC ancora nella sua cella mandata in un altro
/// magazzino: il cambio cella (voce 39, compito 4).
/// </summary>
/// <remarks>
/// Come in <see cref="MissionGatewayChangeDestinationTest"/>: il navigatore sta in <c>Stop</c> (le
/// missioni si aggiungono in <c>Recover</c>, perche' in Stop <c>ActiveMissions.Add</c> esce subito),
/// il database del WCS e' in memoria e senza scrittore in coda, il gestionale e' quello finto, e
/// nessuna attesa a tempo: il gestionale si ferma su un cancello del test, con un tetto che fa
/// fallire invece di appendere.
/// </remarks>
public sealed class ChangeDestinationStepStartsTest : IDisposable
{
    private const decimal LoadUnitId = 42;
    private const decimal PickUpCellId = 5;
    private const string PickUpDoorCode = "A5";
    private const decimal StorageCellId = 6;
    private const string StorageDoorCode = "A6";
    private const decimal OtherCellId = 8;
    private const string OtherDoorCode = "A8";
    private const decimal AssignmentId = 77;
    private const decimal ExtractionRowId = 901;
    private static readonly Guid Assignment = new("0b0e5a1c-7700-4000-8000-000000000077");

    /// <summary>Il motivo del fatto che rimette da servire la riga di estrazione del cambio cella (voce 45, compito 4).</summary>
    private const string ChangeOfCellReopenReason =
        "The operator sent the load unit, still in its cell, to another storage before the pick-up: the extraction became a change of cell";

    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly QueuedDatabaseWriter<WcsWrite>? _previousWriter = Navigator.StateWriter;
    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly RoutingTestPlant _plant = new();
    private readonly FakeWarehouse _warehouse = new();

    public ChangeDestinationStepStartsTest()
    {
        Navigator.Store = _database;
        Navigator.StateWriter = null;
        Navigator.Warehouse = _warehouse;
        RoutesToBuild.Instance.Items = new();
    }

    public void Dispose()
    {
        RoutesToBuild.Instance.Items = new();
        _plant.Dispose();
        Navigator.Warehouse = _previousWarehouse;
        Navigator.StateWriter = _previousWriter;
        Navigator.Store = null;
        _database.Dispose();
    }

    // ---------------------------------------------------------------------------------------
    // Il prelievo
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// R sta prelevando dal magazzino S l'UDC della missione: il gettone e il posto sono su R, la
    /// destinazione dello step, e S non porta niente. The change waits for the end of the pick-up (item
    /// 54, decision 4), e lo step e i gettoni di R restano: tolto lo step, il completamento del campo
    /// sarebbe stato scartato e l'UDC sarebbe rimasta su R per il campo e in S per il motore.
    /// </summary>
    [Fact]
    public async Task APickUpInFlightMakesTheChangeWait()
    {
        var (mission, step) = await AMissionToBePickedFromS();
        Assert.True(_plant["R"].TryReserveForIncomingStep(step.StepId));
        _plant["R"].RunningStepId = step.StepId;
        _plant["R"].Status = DeviceStatusTypes.Loading;

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["X"].ItemGuid);
        await PendingMissionChanges.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(MissionDestinationOutcome.Pending, result.Outcome);
        Assert.Equal(PendingMissionChange.Destination(_plant["X"].ItemGuid, MissionGatewayChoice.Keep),
            MissionGateway.PendingChangeOf(mission.MissionId));
        Assert.Same(step, Assert.Single(mission.MissionSteps));
        Assert.Equal(step.StepId, _plant["R"].RunningStepId);
        Assert.Equal(step.StepId, _plant["R"].WaitingStepId);
        Assert.Equal(_plant["Y"].ItemGuid, mission.DestinationItemId);
        Assert.False(mission.OperatorDestination);
    }

    /// <summary>
    /// R ha cominciato il prelievo e aspetta il gestionale per l'indirizzo della cella, prima dei
    /// gettoni: the change that arrives now waits (item 54, decision 4), e il prelievo, ripreso, parte
    /// con il suo step, che c'e' ancora; the change keeps waiting, now for the end of the pick-up.
    /// </summary>
    [Fact]
    public async Task APickUpThatIsStartingMakesTheChangeWait()
    {
        var (mission, step) = await AMissionToBePickedFromS();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _warehouse.Before = async operation =>
        {
            if (operation != nameof(IWarehouse.DescribeCellAsync) || reached.Task.IsCompleted) return;

            reached.TrySetResult();
            await gate.Task;
        };

        Assert.NotNull(_plant["R"].StepExecutor);
        var pickUp = _plant["R"].StepExecutor!.StartPickUpLu(mission);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["X"].ItemGuid);
        gate.SetResult();
        await pickUp.WaitAsync(TimeSpan.FromSeconds(30));
        await PendingMissionChanges.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(MissionDestinationOutcome.Pending, result.Outcome);
        Assert.Equal(PendingMissionChange.Destination(_plant["X"].ItemGuid, MissionGatewayChoice.Keep),
            MissionGateway.PendingChangeOf(mission.MissionId));
        Assert.Same(step, Assert.Single(mission.MissionSteps));
        Assert.Equal(step.StepId, _plant["R"].RunningStepId);
        Assert.Equal(_plant["Y"].ItemGuid, mission.DestinationItemId);
        Assert.False(mission.OperatorDestination);
        NoStepStartIsLeftCounted(mission);
    }

    /// <summary>
    /// Con un cambio dell'operatore in corso R non comincia il prelievo: nessuna lettura dal
    /// gestionale, nessun gettone. A cambio finito lo sveglia il cambio stesso.
    /// </summary>
    [Fact]
    public async Task AnOperatorChangeInProgressHoldsBackThePickUp()
    {
        var (mission, step) = await AMissionToBePickedFromS();
        Assert.Equal(PathMission.OperatorChangeStart.Begun, mission.TryBeginOperatorChange());

        try
        {
            Assert.NotNull(_plant["R"].StepExecutor);
            await _plant["R"].StepExecutor!.StartPickUpLu(mission);
        }
        finally
        {
            mission.EndOperatorChange();
        }

        Assert.Equal(Guid.Empty, _plant["R"].RunningStepId);
        Assert.Equal(Guid.Empty, _plant["R"].WaitingStepId);
        Assert.Empty(_warehouse.Calls);
        Assert.Same(step, Assert.Single(mission.MissionSteps));
        NoStepStartIsLeftCounted(mission);
    }

    // ---------------------------------------------------------------------------------------
    // Il gruppo
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Il gruppo G ha mandato al campo il movimento di M1 verso N: il gettone e' di G, M1 non porta
    /// niente. The change waits (item 54, decision 4), e lo step resta quello del movimento. Il
    /// movimento, finito di avviare, non lascia contato niente sulla missione: the wait comes from the
    /// group's token, il punto di questa prova, e non da un avvio rimasto aperto.
    /// </summary>
    [Fact]
    public async Task AGroupedMoveInFlightMakesTheChangeWait()
    {
        var (mission, step) = await AMissionOnAMemberOfG("N");
        Assert.NotNull(_plant["G"].StepExecutor);
        await _plant["G"].StepExecutor!.ExecuteGroupedMOVE_LU_TO([step]);
        Assert.NotEqual(Guid.Empty, _plant["G"].RunningStepId);
        Assert.Equal(_plant["G"].RunningStepId, step.GroupedMovementId);
        Assert.Equal(Guid.Empty, _plant["M1"].RunningStepId);
        NoStepStartIsLeftCounted(mission);

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["X"].ItemGuid);
        await PendingMissionChanges.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(MissionDestinationOutcome.Pending, result.Outcome);
        Assert.Equal(PendingMissionChange.Destination(_plant["X"].ItemGuid, MissionGatewayChoice.Keep),
            MissionGateway.PendingChangeOf(mission.MissionId));
        Assert.Same(step, Assert.Single(mission.MissionSteps));
        Assert.Equal(step.StepId, _plant["N"].WaitingStepId);
        Assert.Equal(_plant["N"].ItemGuid, mission.DestinationItemId);
        Assert.False(mission.OperatorDestination);
    }

    /// <summary>
    /// Il gruppo G ha scelto lo step di M1 verso il magazzino SG e aspetta il gestionale per la
    /// verifica della cella, prima dei gettoni: the change that arrives now waits (item 54, decision
    /// 4), e il movimento, ripreso, parte con lo step della missione, che c'e' ancora (e non torna
    /// accanto a una rotta nuova: <c>ConfigureStep</c> rimette in <c>MissionSteps</c> lo step che non
    /// trova); the change keeps waiting, now for the end of the move.
    /// </summary>
    [Fact]
    public async Task AGroupedMoveThatIsStartingMakesTheChangeWait()
    {
        var (mission, step) = await AMissionOnAMemberOfG("SG");
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _warehouse.Before = async operation =>
        {
            if (operation != nameof(IWarehouse.GetCellContentAsync) || reached.Task.IsCompleted) return;

            reached.TrySetResult();
            await gate.Task;
        };

        Assert.NotNull(_plant["G"].StepExecutor);
        var groupedMove = _plant["G"].StepExecutor!.ExecuteGroupedMOVE_LU_TO([step]);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["X"].ItemGuid);
        gate.SetResult();
        await groupedMove.WaitAsync(TimeSpan.FromSeconds(30));
        await PendingMissionChanges.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(MissionDestinationOutcome.Pending, result.Outcome);
        Assert.Equal(PendingMissionChange.Destination(_plant["X"].ItemGuid, MissionGatewayChoice.Keep),
            MissionGateway.PendingChangeOf(mission.MissionId));
        Assert.Same(step, Assert.Single(mission.MissionSteps));
        Assert.NotEqual(Guid.Empty, _plant["G"].RunningStepId);
        Assert.Equal(_plant["G"].RunningStepId, step.GroupedMovementId);
        Assert.Equal(_plant["SG"].ItemGuid, mission.DestinationItemId);
        Assert.False(mission.OperatorDestination);
        NoStepStartIsLeftCounted(mission);
    }

    /// <summary>
    /// Con un cambio dell'operatore in corso il gruppo lascia fuori dal giro lo step della missione:
    /// nessun posto su N, nessun gettone del gruppo sullo step.
    /// </summary>
    [Fact]
    public async Task AnOperatorChangeInProgressHoldsBackTheGroupedMove()
    {
        var (mission, step) = await AMissionOnAMemberOfG("N");
        Assert.Equal(PathMission.OperatorChangeStart.Begun, mission.TryBeginOperatorChange());

        try
        {
            Assert.NotNull(_plant["G"].StepExecutor);
            await _plant["G"].StepExecutor!.ExecuteGroupedMOVE_LU_TO([step]);
        }
        finally
        {
            mission.EndOperatorChange();
        }

        Assert.Equal(Guid.Empty, _plant["N"].WaitingStepId);
        Assert.Equal(Guid.Empty, _plant["G"].RunningStepId);
        Assert.Equal(Guid.Empty, step.GroupedMovementId);
        NoStepStartIsLeftCounted(mission);
    }

    /// <summary>
    /// Lo stesso per i movimenti singoli del gruppo (<c>ExecuteSingleMOVE_LU_TO</c>): lo step della
    /// missione resta fuori dal giro, e il gruppo non si mette in movimento (<c>Moving</c>, con il suo
    /// gettone) per uno step che non parte. Il deposito di M1 lo fermerebbe comunque
    /// (<c>StartPutToMOVE_LU</c> conta anche lui l'avvio), ma dopo che il gruppo si e' gia' preso lo
    /// stato, e nessun completamento glielo restituirebbe.
    /// </summary>
    [Fact]
    public async Task AnOperatorChangeInProgressHoldsBackTheGroupsSingleMove()
    {
        var (mission, step) = await AMissionOnAMemberOfG("N");
        var groupStatus = _plant["G"].Status;
        Assert.NotEqual(DeviceStatusTypes.Moving, groupStatus);
        Assert.Equal(PathMission.OperatorChangeStart.Begun, mission.TryBeginOperatorChange());

        try
        {
            Assert.NotNull(_plant["G"].StepExecutor);
            await _plant["G"].StepExecutor!.ExecuteSingleMOVE_LU_TO([step]);
        }
        finally
        {
            mission.EndOperatorChange();
        }

        Assert.Equal(groupStatus, _plant["G"].Status);
        Assert.Equal(Guid.Empty, _plant["M1"].RunningStepId);
        Assert.Equal(Guid.Empty, _plant["N"].WaitingStepId);
        NoStepStartIsLeftCounted(mission);
    }

    /// <summary>
    /// La meta' che deve riuscire, per la prova qui sopra: senza un cambio in corso il movimento
    /// singolo del gruppo parte (il gettone su M1), cosi' che «non parte» non passi per un guasto
    /// della scena. Finito il giro, la missione non ha avvii contati: ne' quello del giro del
    /// gruppo ne' quello del movimento di M1 (<c>StartPutToMOVE_LU</c>, che si conta anche lui).
    /// </summary>
    [Fact]
    public async Task WithoutAnOperatorChangeTheGroupsSingleMoveSetsOff()
    {
        var (mission, step) = await AMissionOnAMemberOfG("N");

        Assert.NotNull(_plant["G"].StepExecutor);
        await _plant["G"].StepExecutor!.ExecuteSingleMOVE_LU_TO([step]);

        Assert.Equal(DeviceStatusTypes.Moving, _plant["G"].Status);
        Assert.Equal(step.StepId, _plant["M1"].RunningStepId);
        Assert.Equal(step.StepId, _plant["N"].WaitingStepId);
        NoStepStartIsLeftCounted(mission);
    }

    /// <summary>
    /// Il gruppo ha scelto lo step M1->N, e prima che il movimento cominci un cambio dell'operatore
    /// verso X comincia e finisce: lo step scelto non c'e' piu'. Il movimento di gruppo lo lascia
    /// fuori: niente posto su N, niente gettone del gruppo, e soprattutto lo step non torna accanto
    /// alla rotta nuova (<c>ConfigureStep</c> rimette in <c>MissionSteps</c> lo step che non trova).
    /// </summary>
    [Fact]
    public async Task AGroupedMoveLeavesOutAStepTheChangeRemovedAfterItWasChosen()
    {
        var (mission, chosen) = await AMissionOnAMemberOfG("N");
        Assert.Equal(MissionDestinationOutcome.Changed,
            (await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["X"].ItemGuid)).Outcome);
        var newStep = Assert.Single(mission.MissionSteps);

        Assert.NotNull(_plant["G"].StepExecutor);
        await _plant["G"].StepExecutor!.ExecuteGroupedMOVE_LU_TO([chosen]);

        Assert.Same(newStep, Assert.Single(mission.MissionSteps));
        Assert.Equal(Guid.Empty, _plant["N"].WaitingStepId);
        Assert.Equal(Guid.Empty, _plant["G"].RunningStepId);
        NoStepStartIsLeftCounted(mission);
    }

    /// <summary>
    /// Lo stesso per i movimenti singoli del gruppo: lo step scelto prima del cambio non riparte, e
    /// non torna in <c>MissionSteps</c> (<c>ProcessSingleStep</c> ve lo rimetterebbe).
    /// </summary>
    [Fact]
    public async Task AGroupsSingleMoveLeavesOutAStepTheChangeRemovedAfterItWasChosen()
    {
        var (mission, chosen) = await AMissionOnAMemberOfG("N");
        Assert.Equal(MissionDestinationOutcome.Changed,
            (await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["X"].ItemGuid)).Outcome);
        var newStep = Assert.Single(mission.MissionSteps);
        var groupStatus = _plant["G"].Status;

        Assert.NotNull(_plant["G"].StepExecutor);
        await _plant["G"].StepExecutor!.ExecuteSingleMOVE_LU_TO([chosen]);

        Assert.Same(newStep, Assert.Single(mission.MissionSteps));
        Assert.Equal(Guid.Empty, _plant["N"].WaitingStepId);
        Assert.Equal(groupStatus, _plant["G"].Status);
        NoStepStartIsLeftCounted(mission);
    }

    /// <summary>
    /// La missione su M1 va nel magazzino SG scelto dall'operatore, con la cella 6 (porta A6) che il
    /// gestionale le tiene e lo step M1->SG. Il movimento di gruppo che la deposita la lascia fuori dal
    /// giro (nessun gettone del gruppo sullo step) e la mette in coda al navigatore, finche' lui non
    /// ha confermato la cella presso il gestionale: il ciclo del gruppo, che nessun semaforo esclude,
    /// non chiede niente al gestionale per lei. Confermata, il giro dopo la deposita. Prima il gruppo
    /// la depositava senza chiedere, anche in una cella che il gestionale non le teneva piu'.
    /// </summary>
    [Fact]
    public async Task AGroupedDepositOfAnOperatorsMissionWaitsForTheNavigatorToConfirmTheCell()
    {
        var (mission, step) = await AMissionOnM1ChosenForSGWithItsCell();
        Assert.NotNull(_plant["G"].StepExecutor);

        await _plant["G"].StepExecutor!.ExecuteGroupedMOVE_LU_TO([step]);

        Assert.Equal(Guid.Empty, _plant["G"].RunningStepId);
        Assert.Equal(Guid.Empty, step.GroupedMovementId);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
        Assert.DoesNotContain(nameof(IWarehouse.GetStorageReservationAsync), _warehouse.Calls);
        NoStepStartIsLeftCounted(mission);

        await TheRouteLoopLooksForTheCell(mission);
        Assert.Same(step, Assert.Single(mission.MissionSteps));
        await _plant["G"].StepExecutor!.ExecuteGroupedMOVE_LU_TO([step]);

        Assert.NotEqual(Guid.Empty, _plant["G"].RunningStepId);
        Assert.Equal(_plant["G"].RunningStepId, step.GroupedMovementId);
        NoStepStartIsLeftCounted(mission);
    }

    /// <summary>
    /// La missione su M1 va verso SG, scelto in precedenza dall'operatore, e non ha rotta. Con un
    /// cambio dell'operatore in corso la riverifica delle celle del gruppo la lascia fuori: senza,
    /// il gruppo le ricostruiva la rotta (<c>FindWhereStoreLu</c>, ramo della scelta dell'operatore)
    /// mentre il cambio la stava calcolando, dal suo ciclo, che nessun semaforo esclude.
    /// </summary>
    [Fact]
    public async Task AnOperatorChangeInProgressHoldsBackTheGroupsCellVerification()
    {
        var mission = await AMissionOnM1ChosenForSGWithoutARoute();
        Assert.Equal(PathMission.OperatorChangeStart.Begun, mission.TryBeginOperatorChange());

        try
        {
            await _plant["G"].VerifyWhereAllocateLuGrouped(_plant["G"].LUsLoaded).WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            mission.EndOperatorChange();
        }

        Assert.Empty(mission.MissionSteps);
        Assert.Empty(_warehouse.Calls);
        NoStepStartIsLeftCounted(mission);
    }

    /// <summary>
    /// La meta' che deve riuscire, per la prova qui sopra: senza un cambio in corso la riverifica
    /// del gruppo lavora sulla missione (le ricostruisce la rotta verso SG), e finito il lavoro la
    /// missione non resta contata: un conto lasciato aperto rifiuterebbe ogni cambio dell'operatore
    /// come passo in volo, per sempre.
    /// </summary>
    [Fact]
    public async Task TheGroupsCellVerificationGivesBackItsCountWhenItIsOver()
    {
        var mission = await AMissionOnM1ChosenForSGWithoutARoute();

        await _plant["G"].VerifyWhereAllocateLuGrouped(_plant["G"].LUsLoaded).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(_plant["SG"].ItemGuid, Assert.Single(mission.MissionSteps).ToNodeId);
        NoStepStartIsLeftCounted(mission);
    }

    /// <summary>
    /// La ricerca delle celle del gruppo (dopo un errore del campo, o a comando) comincia togliendo
    /// alle missioni dei membri la rotta: con un cambio dell'operatore in corso la missione su M1 resta
    /// fuori, e tiene lo step. (N l'aveva gia' scelto l'operatore: cosi' la ricerca, se partisse,
    /// finirebbe senza chiedere niente al gestionale.) Il gateway X non lo guarda: la ricerca lo tiene
    /// anche quando lavora sulla missione (risposta 6 del proprietario del 2/10, la prova qui sotto), e
    /// l'asserzione che c'era non poteva fallire (revisione finale del primo giro della voce 39, M125).
    /// </summary>
    [Fact]
    public async Task AnOperatorChangeInProgressHoldsBackTheGroupsStorageSearch()
    {
        var (mission, step) = await AMissionOnM1ChosenForNThroughX();
        Assert.Equal(PathMission.OperatorChangeStart.Begun, mission.TryBeginOperatorChange());

        try
        {
            Assert.NotNull(_plant["G"].StorageManager);
            await _plant["G"].StorageManager!.FindNewStoreLocationGroupedForTest().WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            mission.EndOperatorChange();
        }

        Assert.Same(step, Assert.Single(mission.MissionSteps));
        NoStepStartIsLeftCounted(mission);
    }

    /// <summary>
    /// La meta' che deve riuscire, per la prova qui sopra: senza un cambio in corso la ricerca delle
    /// celle del gruppo lavora sulla missione (le toglie la rotta, che ricostruisce verso N passando
    /// dal gateway X, che resta suo), e finito il lavoro la missione non resta contata. Prima la
    /// ricerca le toglieva anche il gateway che l'operatore aveva scelto, e la rotta nuova andava dritta
    /// a N (risposta 6 del proprietario del 2/10, che corregge la decisione 9).
    /// </summary>
    [Fact]
    public async Task TheGroupsStorageSearchGivesBackItsCountWhenItIsOver()
    {
        var (mission, step) = await AMissionOnM1ChosenForNThroughX();

        Assert.NotNull(_plant["G"].StorageManager);
        await _plant["G"].StorageManager!.FindNewStoreLocationGroupedForTest().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(_plant["X"].ItemGuid, mission.Gateway);
        Assert.DoesNotContain(step, mission.MissionSteps);
        Assert.Equal(["M1->X", "X->N"], StepsOf(mission));
        Assert.True(mission.OperatorDestination);
        NoStepStartIsLeftCounted(mission);
    }

    // ---------------------------------------------------------------------------------------
    // La cella dell'operatore rifiutata dal PLC
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Risposta 1 del proprietario dell'8/10: il PLC rifiuta con l'errore 85 il deposito di gruppo
    /// (<c>HandleGroupStorageErrors</c>, che passa alla ricerca del gruppo le missioni degli step del
    /// movimento rifiutato), e la ricerca delle celle del gruppo cerca per la missione che l'operatore ha
    /// mandato in SG un'altra cella nello stesso magazzino, esclusa la 6: la cella 8, con porta,
    /// indirizzo, profondita' e rotta verso di lei; destinazione, segno e gateway X restano, e il gruppo
    /// torna pronto. Prima l'85 del gruppo andava subito in errore, senza cercare (era il 137 a
    /// cercare, e il 137 non cerca piu').
    /// </summary>
    /// <remarks>
    /// La missione senza segno di M2, che non era nel movimento rifiutato, qui <b>non ha una
    /// prenotazione nel gestionale</b>: la sua porta A6 non e' una cella sua (la 6 il gestionale la tiene
    /// alla missione di M1). E' una copia della porta della missione di M1: della configurazione, senza
    /// una cella prenotata, sullo stesso gruppo, con la stessa porta, come resta la copia dopo il cambio
    /// del solo gateway della missione da cui l'ha copiata. Dall'8/10 sera (risposta 8 del proprietario) la
    /// ricerca dopo il rifiuto non le chiede niente al gestionale e le tiene il gateway X; ma la cella 6
    /// non e' piu' della missione di M1, e la copia lascia la porta A6 (punto 3 dell'8/10 sera, primo giro
    /// di correzioni del compito 15): niente porta ne' rotta, la rifa' la ricerca per tipo del gruppo.
    /// Fino a quella correzione la teneva, con lo step M2->SG, e avrebbe depositato da sola nella cella
    /// rifiutata. Prima ancora la ricerca del gruppo di sempre le toglieva il gateway e le prendeva la
    /// cella che il gestionale le dava per la configurazione, la 9. La missione non rifiutata che ha
    /// ancora la sua cella: <see cref="OnTheGroupAMissionWhoseDepositThePlcDidNotRefuseKeepsItsCell"/>.
    /// </remarks>
    [Fact]
    public async Task AfterError85OnTheGroupTheOperatorsMissionFindsAnotherCellInTheChosenStorage()
    {
        var (mission, step) = await AMissionOnM1WhoseCellInSGThePlcRefuses();
        var other = await AnUnmarkedMissionOnM2ThroughX();
        var otherStep = Assert.Single(other.MissionSteps);
        Assert.False(_warehouse.StorageReservationsByMission.ContainsKey(other.MissionId));

        await ThePlcRefusesTheDeposit(_plant["G"], step, 85);

        Assert.Equal(DeviceStatusTypes.Ready, _plant["G"].Status);
        TheOperatorsMissionHasCell8(mission, step);
        Assert.Equal(_plant["M1"].ItemGuid, mission.TakeStorageCellConfirmation());

        // La copia della porta A6 la lascia, e tiene il gateway (vedi le osservazioni).
        Assert.Equal(_plant["X"].ItemGuid, other.Gateway);
        TheCopyLeftTheDoor(other);
        Assert.False(other.OperatorDestination);
        Assert.DoesNotContain(otherStep, MissionSteps.Instance.List);

        NoStepStartIsLeftCounted(mission);
        NoStepStartIsLeftCounted(other);
    }

    /// <summary>
    /// Risposta 1 del proprietario dell'8/10 (brief del compito 8): sul gruppo cambia cella solo la
    /// missione il cui deposito il PLC ha rifiutato, e le altre tengono la loro. Il PLC rifiuta con
    /// l'errore <paramref name="errorCode"/> il deposito di gruppo della missione dell'operatore su M1,
    /// che prende la cella 8; la missione della configurazione su M2, che non era nel movimento
    /// rifiutato, ha la cella 9 (porta A9) che il gestionale le tiene ancora, e per l'UDC 43 il
    /// gestionale proporrebbe la 10. Dopo la ricerca del gruppo la missione di M2 ha ancora la cella 9,
    /// con porta, indirizzo e profondita', nessuna domanda di prenotazione e nessun rilascio per lei, e
    /// la rotta rifatta verso la stessa porta. Una ricerca che rifacesse da capo la cella di ogni
    /// missione del gruppo le darebbe la 10.
    /// </summary>
    /// <remarks>
    /// Il gateway e lo step non si guardano qui: la ricerca del gruppo toglie a ogni missione senza il
    /// segno la rotta e il gateway e le rifa' la rotta (<c>ResetLuProperties</c>, le missioni senza il
    /// segno "come oggi" del compito 5), e la risposta dell'8/10 parla della cella. Con il 67 la cella
    /// che si blocca come piena nel gestionale e' solo la 6 rifiutata, non la 9 dell'altra missione, che
    /// bloccata la rifiuterebbe al suo deposito.
    /// </remarks>
    [Theory]
    [InlineData(67)]
    [InlineData(85)]
    [InlineData(133)]
    public async Task OnTheGroupAMissionWhoseDepositThePlcDidNotRefuseKeepsItsCell(int errorCode)
    {
        var (mission, step) = await AMissionOnM1WhoseCellInSGThePlcRefuses();
        var other = await AnUnmarkedMissionOnM2ThroughX();
        await TheStorageSGHasTheCells(6, 8, 9, 10);
        _warehouse.StorageReservationsByMission[other.MissionId] = new StorageReservation(other.MissionId,
            StorageReservationOutcome.CellFound, 9, "A9", Partition: "0093", StorageDepth: 200, NeedsCompaction: false);
        other.StorageDoorCode = "A9";
        other.DestinationAsi = "0091";
        other.DestinationSubitem = "0092";
        other.DestinationPartition = "0093";
        other.StorageDepth = 200;
        other.StorageCellReserved = true;
        _warehouse.StorageReservations[43] = new StorageReservation(Guid.Empty,
            StorageReservationOutcome.CellFound, 10, "A10", Partition: "0103", StorageDepth: 300, NeedsCompaction: false);

        await ThePlcRefusesTheDeposit(_plant["G"], step, errorCode);

        Assert.Equal(DeviceStatusTypes.Ready, _plant["G"].Status);
        TheOperatorsMissionHasCell8(mission, step);

        Assert.Equal(_plant["SG"].ItemGuid, other.DestinationItemId);
        Assert.Equal("A9", other.StorageDoorCode);
        Assert.Equal("0091", other.DestinationAsi);
        Assert.Equal("0092", other.DestinationSubitem);
        Assert.Equal("0093", other.DestinationPartition);
        Assert.Equal(200, other.StorageDepth);
        Assert.True(other.StorageCellReserved);
        Assert.Equal(["M2->SG"], StepsOf(other));
        Assert.Equal(9m, _warehouse.StorageReservationsByMission[other.MissionId].CellId);
        Assert.DoesNotContain(other.MissionId, _warehouse.ReleasedStorageReservations);
        Assert.DoesNotContain(_warehouse.StorageRequests, request => request.MissionId == other.MissionId);
        Assert.Equal(errorCode == 67 ? [(StorageDoorCode, CellLockState.Full)] : [], _warehouse.CellLockStateChanges);
        NoStepStartIsLeftCounted(mission);
        NoStepStartIsLeftCounted(other);
    }

    /// <summary>
    /// La ricerca del gruppo per la missione il cui deposito il PLC ha rifiutato, come la chiama
    /// <c>HandleGroupStorageErrors</c>, e in SG non c'e' un'altra cella: la missione va allo scarto R per
    /// mancanza di celle, il segno cade con la riga di avviso, e per il comando manuale il gruppo non
    /// trova posto (come per una missione della configurazione scartata). Prima tornava verso la stessa
    /// porta A6, e il gruppo si diceva pronto.
    /// </summary>
    [Fact]
    public async Task TheGroupSearchWithoutAnotherCellInTheChosenStorageRejectsTheOperatorsMission()
    {
        using var log = new RecordingLogWriter();
        var (mission, _) = await AMissionOnM1WhoseCellInSGThePlcRefuses();
        TheWarehouseHasNoFreeCell();

        Assert.False(await _plant["G"].StorageManager!.FindNewStoreLocationGroupedForTest(mission).WaitAsync(TimeSpan.FromSeconds(30)));

        await TheOperatorsMissionIsRejectedToR(mission, log);
    }

    /// <summary>
    /// Risposta 7 del proprietario del 2/10: l'errore 85 sul deposito di M1, dispositivo singolo fuori
    /// da ogni gruppo (<c>HandleStorageErrors</c>), e la missione che l'operatore ha mandato in SG
    /// prende la cella 8 dello stesso magazzino, con il gateway X. Prima, fuori da un gruppo, nessuna
    /// ricerca: il rigetto, che lo step del deposito appena rifiutato, ancora sul gettone di M1, faceva
    /// rimandare.
    /// </summary>
    [Fact]
    public async Task AfterError85OnASingleDeviceTheOperatorsMissionTakesAnotherCellInTheChosenStorage()
    {
        var (mission, step) = await AMissionOnM1WhoseCellInSGThePlcRefuses(inTheGroup: false);

        await ThePlcRefusesTheDeposit(_plant["M1"], step, 85);

        TheOperatorsMissionHasCell8(mission, step);
        Assert.Equal(Guid.Empty, _plant["M1"].RunningStepId);
        NoStepStartIsLeftCounted(mission);
    }

    /// <summary>
    /// Come sopra, ma in SG non c'e' un'altra cella: lo scarto R di M1, e il segno cade con la riga di
    /// avviso.
    /// </summary>
    [Fact]
    public async Task AfterError85OnASingleDeviceWithoutAnotherCellTheOperatorsMissionIsRejected()
    {
        using var log = new RecordingLogWriter();
        var (mission, step) = await AMissionOnM1WhoseCellInSGThePlcRefuses(inTheGroup: false);
        TheWarehouseHasNoFreeCell();

        await ThePlcRefusesTheDeposit(_plant["M1"], step, 85);

        await TheOperatorsMissionIsRejectedToR(mission, log);
    }

    /// <summary>
    /// L'errore 133 sul movimento singolo di M1, membro del gruppo G: il gestore degli errori del
    /// dispositivo singolo chiama la ricerca del gruppo, e la missione dell'operatore prende la cella 8
    /// di SG con il gateway X. Prima perdeva gateway e indirizzo, e tornava verso la porta A6.
    /// </summary>
    [Fact]
    public async Task AfterError133OnAMemberTheOperatorsMissionTakesAnotherCellInTheChosenStorage()
    {
        var (mission, step) = await AMissionOnM1WhoseCellInSGThePlcRefuses();

        await ThePlcRefusesTheDeposit(_plant["M1"], step, 133);

        TheOperatorsMissionHasCell8(mission, step);
        NoStepStartIsLeftCounted(mission);
    }

    /// <summary>
    /// L'errore 67 sul deposito di gruppo: la cella 6 si blocca, e la missione dell'operatore prende la
    /// cella 8 di SG con il gateway X invece di andare allo scarto; il gruppo torna pronto. Prima andava
    /// allo scarto R, e il segno cadeva.
    /// </summary>
    [Fact]
    public async Task AfterError67OnTheGroupTheOperatorsMissionTakesAnotherCellInTheChosenStorage()
    {
        var (mission, step) = await AMissionOnM1WhoseCellInSGThePlcRefuses();

        await ThePlcRefusesTheDeposit(_plant["G"], step, 67);

        TheOperatorsMissionHasCell8(mission, step);
        Assert.Equal(DeviceStatusTypes.Ready, _plant["G"].Status);
        NoStepStartIsLeftCounted(mission);
    }

    /// <summary>
    /// Come sopra, ma in SG non c'e' un'altra cella: lo scarto R, e il segno cade con la riga di avviso.
    /// </summary>
    [Fact]
    public async Task AfterError67OnTheGroupWithoutAnotherCellTheOperatorsMissionIsRejected()
    {
        using var log = new RecordingLogWriter();
        var (mission, step) = await AMissionOnM1WhoseCellInSGThePlcRefuses();
        TheWarehouseHasNoFreeCell();

        await ThePlcRefusesTheDeposit(_plant["G"], step, 67);

        await TheOperatorsMissionIsRejectedToR(mission, log);
    }

    /// <summary>
    /// Risposta 1 del proprietario dell'8/10 sul gruppo: tre 67 di fila cercano ogni volta una cella
    /// nuova - la 8, la 9, la 10 - e ogni domanda esclude le celle rifiutate fin li'; il quarto 67 manda
    /// il gruppo in errore, senza chiedere niente al gestionale e senza scarto: la missione resta
    /// dell'operatore, verso la cella 10, in errore. Ogni 67 blocca la sua cella nel gestionale, anche il
    /// quarto. Prima (il tetto del compito 5) al terzo 67 la missione andava allo scarto R.
    /// </summary>
    [Fact]
    public async Task AfterFourErrors67OnTheGroupTheThirdStillTakesACellAndTheFourthStopsTheGroup()
    {
        var (mission, step) = await AMissionOnM1WhoseCellInSGThePlcRefuses();
        await TheStorageSGHasTheCells(6, 8, 9, 10);

        await TheGroupRefusesAndTheOperatorsMissionTakes(67, step, 8, [6]);
        await TheGroupRefusesAndTheOperatorsMissionTakes(67, mission.MissionSteps[0], 9, [6, 8]);
        await TheGroupRefusesAndTheOperatorsMissionTakes(67, mission.MissionSteps[0], 10, [6, 8, 9]);
        Assert.Equal(DeviceStatusTypes.Ready, _plant["G"].Status);

        TheWarehouseHasCell(11);
        var asked = _warehouse.StorageRequests.Count;
        var refused = mission.MissionSteps[0];
        await ThePlcRefusesTheDeposit(_plant["G"], refused, 67);

        Assert.Equal(asked, _warehouse.StorageRequests.Count);
        TheGroupWaitsInErrorWithTheOperatorsMission(mission, refused, "A10");
        Assert.Equal(4, _warehouse.Calls.Count(call => call == nameof(IWarehouse.SetCellLockStateAsync)));
        NoStepStartIsLeftCounted(mission);
    }

    /// <summary>
    /// Risposta 1 del proprietario del 9/10: come sopra, ma al quarto 67 il gestionale non risponde al
    /// blocco della cella 10 piena. Il gruppo va in errore e aspetta, senza ricerca e senza scarto, e la
    /// missione lascia la porta A10 come una cella persa (resta dell'operatore, verso SG, con il gateway
    /// X): alla ripartenza il navigatore le cerca un'altra cella. La 10 va nell'elenco delle celle da
    /// segnare piene, in memoria e nel database del WCS (risposta 3). Prima la missione teneva la porta
    /// A10, che il gestionale non sapeva piena e poteva dare a un'altra UDC.
    /// </summary>
    [Fact]
    public async Task AtTheFourthError67WithTheWarehouseSilentAtTheLockTheMissionLeavesTheDoor()
    {
        var (mission, step) = await AMissionOnM1WhoseCellInSGThePlcRefuses();
        await TheStorageSGHasTheCells(6, 8, 9, 10);

        await TheGroupRefusesAndTheOperatorsMissionTakes(67, step, 8, [6]);
        await TheGroupRefusesAndTheOperatorsMissionTakes(67, mission.MissionSteps[0], 9, [6, 8]);
        await TheGroupRefusesAndTheOperatorsMissionTakes(67, mission.MissionSteps[0], 10, [6, 8, 9]);
        Assert.Empty(CellsToMarkFull.Doors);

        _warehouse.UnavailableOperations.Add(nameof(IWarehouse.SetCellLockStateAsync));
        var asked = _warehouse.StorageRequests.Count;
        var refused = mission.MissionSteps[0];
        await ThePlcRefusesTheDeposit(_plant["G"], refused, 67);

        Assert.Equal(asked, _warehouse.StorageRequests.Count);
        Assert.Equal(DeviceStatusTypes.Warning, _plant["G"].Status);
        Assert.Equal(MissionStatusTypes.Error, mission.Status);
        Assert.Equal(ResultCodeTypes.Error, refused.StepStatus);
        Assert.Equal(4, _plant["G"].RefusedDepositsInARow);
        TheMissionLeftTheRefusedDoor(mission);
        Assert.Equal(_plant["SG"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.OperatorDestination);
        Assert.Equal(_plant["X"].ItemGuid, mission.Gateway);
        Assert.NotEqual(MissionTypes.Rejection, mission.MissionType);
        Assert.Equal(["A10"], CellsToMarkFull.Doors);
        Assert.Equal(["A10"], TheCellsToMarkFullInTheDatabase());

        // Le quattro celle rifiutate stanno anche nella riga della missione (risposta 2 del 9/10).
        var row = TheRowOf(mission);
        Assert.NotNull(row);
        Assert.Equal("6,8,9,10", row.CellsRefusedByThePlc);
        Assert.True(row.DepositRefusedByThePlc);
        NoStepStartIsLeftCounted(mission);
    }

    /// <summary>
    /// Risposta 3 del proprietario del 9/10: il 67 sulla cella 6 con il gestionale muto al blocco. La
    /// porta A6 va nell'elenco delle celle da segnare piene, con una riga di avviso. In Run, a ogni giro
    /// delle rotte con il freno scaduto il blocco si ritenta: con il gestionale ancora muto la cella
    /// resta nell'elenco, nessuna riga in piu', e prima che il freno scada un giro non chiede niente;
    /// tornato il gestionale, la cella e' segnata piena ed esce dall'elenco e dal database del WCS, e i
    /// giri dopo non chiedono piu'. Prima il blocco non si ritentava: la 6 restava libera nel gestionale.
    /// </summary>
    [Fact]
    public async Task ACellTheSilentWarehouseDidNotLockIsTriedAgainAtTheBrakeTurnsUntilItAnswers()
    {
        using var log = new RecordingLogWriter();
        var clock = new ManualClock();
        var previousRetries = CellsToMarkFull.Retries;
        CellsToMarkFull.Retries = new RetryBackoff(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(5), clock);

        try
        {
            var (_, step) = await AMissionOnM1WhoseCellInSGThePlcRefuses();
            _warehouse.UnavailableOperations.Add(nameof(IWarehouse.SetCellLockStateAsync));

            await ThePlcRefusesTheDeposit(_plant["G"], step, 67);

            Assert.Equal([StorageDoorCode], CellsToMarkFull.Doors);
            Assert.Equal([StorageDoorCode], TheCellsToMarkFullInTheDatabase());
            await log.Wait(IsTheWarningOfTheLockOfA6).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(1, LockCalls());

            // I giri delle rotte girano in Run; la missione che ha lasciato la porta non si costruisce qui.
            RoutesToBuild.Instance.Items = new();
            Navigator.NavStatus = NavigatorStatusTypes.Run;

            await OneTurnOfTheRouteLoopAndItsRetry();
            Assert.Equal(2, LockCalls());
            Assert.Equal([StorageDoorCode], CellsToMarkFull.Doors);

            await OneTurnOfTheRouteLoopAndItsRetry();
            Assert.Equal(2, LockCalls());

            clock.Advance(TimeSpan.FromMilliseconds(200));
            await OneTurnOfTheRouteLoopAndItsRetry();
            Assert.Equal(3, LockCalls());
            Assert.Equal([StorageDoorCode], CellsToMarkFull.Doors);
            Assert.Equal([StorageDoorCode], TheCellsToMarkFullInTheDatabase());
            Assert.Empty(_warehouse.CellLockStateChanges);

            _warehouse.UnavailableOperations.Clear();
            clock.Advance(TimeSpan.FromMilliseconds(400));
            await OneTurnOfTheRouteLoopAndItsRetry();

            Assert.Equal(4, LockCalls());
            Assert.Equal([(StorageDoorCode, CellLockState.Full)], _warehouse.CellLockStateChanges);
            Assert.Empty(CellsToMarkFull.Doors);
            Assert.Empty(TheCellsToMarkFullInTheDatabase());

            clock.Advance(TimeSpan.FromSeconds(5));
            await OneTurnOfTheRouteLoopAndItsRetry();
            Assert.Equal(4, LockCalls());
            Assert.Single(log.Lines, line => IsTheWarningOfTheLockOfA6(line.Description, line.Level));
        }
        finally
        {
            CellsToMarkFull.Retries = previousRetries;
        }

        int LockCalls() => _warehouse.Calls.Count(call => call == nameof(IWarehouse.SetCellLockStateAsync));

        static bool IsTheWarningOfTheLockOfA6(string line, Log.Interface.LogEntryType level) =>
            level == Log.Interface.LogEntryType.Warning && line.Contains("cella piena") && line.Contains(StorageDoorCode);
    }

    /// <summary>
    /// Risposta 1 del proprietario dell'8/10: l'errore 67 sul deposito di gruppo, e il gestionale non
    /// risponde alla prenotazione di un'altra cella in SG. Adesso non si sa: il gruppo va in errore e
    /// aspetta l'operatore, e la missione non va allo scarto - resta dell'operatore, verso SG, con il
    /// gateway X - e non riparte verso la cella rifiutata. Prima il 67 di gruppo la mandava allo scarto R.
    /// La prova guarda anche che la domanda al gestionale sia partita davvero: senza, un 67 che non cerca
    /// affatto («adesso non si sa» per un'altra ragione) finiva nello stesso stato (revisione finale del
    /// primo giro della voce 39, C44).
    /// </summary>
    [Fact]
    public async Task AfterError67OnTheGroupWithTheWarehouseSilentTheGroupWaitsInError()
    {
        var (mission, step) = await AMissionOnM1WhoseCellInSGThePlcRefuses();
        _warehouse.UnavailableOperations.Add(nameof(IWarehouse.ReserveStorageAsync));
        _warehouse.Calls.Clear();

        await ThePlcRefusesTheDeposit(_plant["G"], step, 67);

        Assert.Contains(nameof(IWarehouse.ReserveStorageAsync), _warehouse.Calls);

        Assert.Equal(DeviceStatusTypes.Warning, _plant["G"].Status);
        Assert.Equal(MissionStatusTypes.Error, mission.Status);
        Assert.Equal(ResultCodeTypes.Error, step.StepStatus);
        Assert.Equal(_plant["SG"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.OperatorDestination);
        Assert.Equal(_plant["X"].ItemGuid, mission.Gateway);
        Assert.NotEqual(MissionTypes.Rejection, mission.MissionType);
        NoStepStartIsLeftCounted(mission);
    }

    /// <summary>
    /// Come sopra, ma il gestionale non risponde gia' al blocco della cella 6 piena, che il 67 chiede
    /// prima della ricerca: il gruppo va in errore e aspetta, la missione e lo step del deposito sono in
    /// errore e restano dell'operatore, verso SG, con il gateway X; nessuna domanda di un'altra cella e
    /// nessuno scarto. Il PLC il deposito l'ha rifiutato comunque: il conto e' a uno. Dopo l'errore la
    /// missione lascia la porta rifiutata A6, come una cella persa, e torna al navigatore (risposta 7 del
    /// proprietario dell'8/10 sera). Prima l'eccezione del blocco usciva dal gestore e la prendeva solo il
    /// log: il gruppo restava impegnato, ne' in errore ne' pronto, e niente era in errore; poi (secondo
    /// giro) la missione teneva la porta A6 e lo step verso di lei.
    /// </summary>
    [Fact]
    public async Task AfterError67OnTheGroupWithTheWarehouseSilentAtTheLockTheGroupWaitsInError()
    {
        var (mission, step) = await AMissionOnM1WhoseCellInSGThePlcRefuses();
        _warehouse.UnavailableOperations.Add(nameof(IWarehouse.SetCellLockStateAsync));

        await ThePlcRefusesTheDeposit(_plant["G"], step, 67);

        Assert.Equal(DeviceStatusTypes.Warning, _plant["G"].Status);
        Assert.Equal(MissionStatusTypes.Error, mission.Status);
        Assert.Equal(ResultCodeTypes.Error, step.StepStatus);
        TheMissionLeftTheRefusedDoor(mission);
        Assert.Equal(_plant["SG"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.OperatorDestination);
        Assert.Equal(_plant["X"].ItemGuid, mission.Gateway);
        Assert.NotEqual(MissionTypes.Rejection, mission.MissionType);
        Assert.DoesNotContain(nameof(IWarehouse.ReserveStorageAsync), _warehouse.Calls);
        Assert.Equal(67, _plant["G"].RefusedDepositErrorCode);
        Assert.Equal(1, _plant["G"].RefusedDepositsInARow);
        Assert.Equal([StorageDoorCode], CellsToMarkFull.Doors);
        NoStepStartIsLeftCounted(mission);
    }

    /// <summary>
    /// Revisione finale del quarto giro: il 67 sul movimento di gruppo che porta la missione di M1 (porta
    /// A6) e quella di M3 (porta A9, con la cella prenotata per lei); il gestionale blocca la prima cella
    /// che gli si chiede e non risponde alla seconda. Nell'elenco delle celle da segnare piene va solo la
    /// porta non bloccata, in memoria e nel database del WCS, e la riga di avviso nomina solo lei; il
    /// gruppo va in errore. L'ordine delle due domande e' quello del movimento, che non e' fisso: la prova
    /// guarda quale porta e' stata bloccata e vuole l'altra nell'elenco.
    /// </summary>
    [Fact]
    public async Task APartialLockPutsOnlyTheDoorThatWasNotLockedOnTheList()
    {
        using var log = new RecordingConsole();
        var (_, step) = await AMissionOnM1WhoseCellInSGThePlcRefuses();
        var third = await AnUnmarkedMissionOnM3ThroughX();
        third.StorageCellReserved = true;
        var thirdStep = Assert.Single(third.MissionSteps);
        var locks = 0;
        _warehouse.Fault = operation => operation == nameof(IWarehouse.SetCellLockStateAsync) && ++locks == 2
            ? new WarehouseUnavailableException(operation)
            : null;

        var group = _plant["G"];
        var token = Guid.NewGuid();
        step.GroupedMovementId = token;
        thirdStep.GroupedMovementId = token;
        group.RunningStepId = token;
        await group.ConnectionAsiMovementIsCompleted(new AsiMovementIsCompletedEventArgs(
            group.ItemGuid, token, ResultCodeTypes.Error, 67, "deposito rifiutato", null))
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(2, locks);
        var (locked, state) = Assert.Single(_warehouse.CellLockStateChanges);
        Assert.Equal(CellLockState.Full, state);
        Assert.Contains(locked, new[] { "A6", "A9" });
        var notLocked = locked == "A6" ? "A9" : "A6";
        Assert.Equal([notLocked], CellsToMarkFull.Doors);
        Assert.Equal([notLocked], TheCellsToMarkFullInTheDatabase());
        Assert.Equal(DeviceStatusTypes.Warning, group.Status);
        Assert.Single(log.Lines, line => line.Contains($"il blocco delle porte {notLocked} si ritenta"));
    }

    /// <summary>
    /// Revisione finale del quarto giro: il blocco della cella 6 dopo il 67 cade per un guasto che non e'
    /// il gestionale muto (qui un <see cref="InvalidOperationException"/>). Il gruppo va in errore e
    /// aspetta, e la riga di avviso dice il tipo dell'eccezione; ma la cella non va nell'elenco delle celle
    /// da segnare piene, che e' per il gestionale muto, e la missione tiene la porta A6: lasciarla e' la
    /// regola del gestionale muto (risposta 7 dell'8/10 sera).
    /// </summary>
    [Fact]
    public async Task ALockThatFailsForAnotherReasonDoesNotFillTheList()
    {
        using var log = new RecordingConsole();
        var (mission, step) = await AMissionOnM1WhoseCellInSGThePlcRefuses();
        _warehouse.Fault = operation => operation == nameof(IWarehouse.SetCellLockStateAsync)
            ? new InvalidOperationException("un difetto del WCS")
            : null;

        await ThePlcRefusesTheDeposit(_plant["G"], step, 67);

        Assert.Equal(DeviceStatusTypes.Warning, _plant["G"].Status);
        Assert.Equal(StorageDoorCode, mission.StorageDoorCode);
        Assert.False(mission.StorageCellLost);
        Assert.Empty(CellsToMarkFull.Doors);
        Assert.Empty(TheCellsToMarkFullInTheDatabase());
        Assert.Single(log.Lines, line => line.Contains("e' caduto (InvalidOperationException: un difetto del WCS)"));
        Assert.DoesNotContain(log.Lines, line => line.Contains("si ritenta"));
    }

    /// <summary>
    /// Revisione finale del quarto giro: il gestionale non risponde al blocco della cella 6 dopo il 67, e
    /// la porta A6 non entra nell'elenco delle celle da segnare piene perche' la scrittura del database del
    /// WCS cade (qui la tabella non c'e'). La riga di avviso e' una, e dice che il blocco non si ritenta;
    /// il gruppo va in errore e la missione lascia la porta lo stesso. Prima la riga del blocco diceva gia'
    /// «si ritenta finche' il gestionale risponde», e una seconda riga diceva il contrario.
    /// </summary>
    [Fact]
    public async Task WhenTheListCannotBeWrittenTheWarningSaysTheLockIsNotTriedAgain()
    {
        using var log = new RecordingConsole();
        var (mission, step) = await AMissionOnM1WhoseCellInSGThePlcRefuses();
        _warehouse.UnavailableOperations.Add(nameof(IWarehouse.SetCellLockStateAsync));
        using (var context = _database.CreateContext())
            Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.ExecuteSqlRaw(context.Database,
                "DROP TABLE CellsToMarkFull");

        await ThePlcRefusesTheDeposit(_plant["G"], step, 67);

        Assert.Equal(DeviceStatusTypes.Warning, _plant["G"].Status);
        TheMissionLeftTheRefusedDoor(mission);
        Assert.Empty(CellsToMarkFull.Doors);
        var line = Assert.Single(log.Lines, line => line.Contains("porte " + StorageDoorCode));
        Assert.Contains("il blocco delle porte A6 non si ritenta", line);
        Assert.DoesNotContain("finche'", line);
    }

    /// <summary>
    /// Risposta 2 del proprietario del 9/10 pomeriggio: il 67 sulla cella 6, e il gestionale rifiuta il
    /// blocco della cella piena con un 4xx gia' al primo tentativo. E' una risposta: una sola riga di
    /// avviso, la porta A6 non va nell'elenco delle celle da segnare piene, e il 67 prosegue come dopo un
    /// blocco riuscito: la missione dell'operatore prende la cella 8 di SG con la domanda che esclude la 6,
    /// e il gruppo torna pronto. Prima il 4xx valeva il gestionale muto: il gruppo andava in errore, la
    /// missione lasciava la porta A6 e la A6 entrava nell'elenco.
    /// </summary>
    [Fact]
    public async Task AFourHundredAtTheLockAfterError67IsAnAnswerAndTheMissionTakesAnotherCell()
    {
        using var log = new RecordingConsole();
        var (mission, step) = await AMissionOnM1WhoseCellInSGThePlcRefuses();
        _warehouse.Fault = operation => operation == nameof(IWarehouse.SetCellLockStateAsync)
            ? FakeWarehouse.Rejection(operation)
            : null;

        await ThePlcRefusesTheDeposit(_plant["G"], step, 67);

        TheOperatorsMissionHasCell8(mission, step);
        Assert.Equal(DeviceStatusTypes.Ready, _plant["G"].Status);
        Assert.False(mission.StorageCellLost);
        Assert.Equal(1, _warehouse.Calls.Count(call => call == nameof(IWarehouse.SetCellLockStateAsync)));
        Assert.Empty(CellsToMarkFull.Doors);
        Assert.Empty(TheCellsToMarkFullInTheDatabase());
        Assert.Single(log.Lines, line => line.Contains("cella piena") || line.Contains("piena la cella"));
        Assert.Single(log.Lines, line =>
            line.Contains("il gestionale ha rifiutato di segnare piena la cella della porta A6") &&
            line.Contains("(risposta 400)") && line.Contains("non si ritenta"));
        NoStepStartIsLeftCounted(mission);
    }

    /// <summary>
    /// Come sopra, al quarto 67 di fila (sulla cella 10): il 4xx al blocco e' una risposta anche qui, e il
    /// quarto prosegue come dopo un blocco riuscito. Il gruppo va in errore e aspetta, senza ricerca, e la
    /// missione tiene la porta A10 (non la lascia come con il gestionale muto); la A10 non va nell'elenco
    /// delle celle da segnare piene.
    /// </summary>
    [Fact]
    public async Task AtTheFourthError67AFourHundredAtTheLockKeepsTheDoorAsAfterALock()
    {
        using var log = new RecordingConsole();
        var (mission, step) = await AMissionOnM1WhoseCellInSGThePlcRefuses();
        await TheStorageSGHasTheCells(6, 8, 9, 10);

        await TheGroupRefusesAndTheOperatorsMissionTakes(67, step, 8, [6]);
        await TheGroupRefusesAndTheOperatorsMissionTakes(67, mission.MissionSteps[0], 9, [6, 8]);
        await TheGroupRefusesAndTheOperatorsMissionTakes(67, mission.MissionSteps[0], 10, [6, 8, 9]);

        _warehouse.Fault = operation => operation == nameof(IWarehouse.SetCellLockStateAsync)
            ? FakeWarehouse.Rejection(operation)
            : null;
        var asked = _warehouse.StorageRequests.Count;
        var refused = mission.MissionSteps[0];
        await ThePlcRefusesTheDeposit(_plant["G"], refused, 67);

        Assert.Equal(asked, _warehouse.StorageRequests.Count);
        TheGroupWaitsInErrorWithTheOperatorsMission(mission, refused, "A10");
        Assert.False(mission.StorageCellLost);
        Assert.Equal(4, _plant["G"].RefusedDepositsInARow);
        Assert.Empty(CellsToMarkFull.Doors);
        Assert.Empty(TheCellsToMarkFullInTheDatabase());
        Assert.Single(log.Lines, line => line.Contains("il gestionale ha rifiutato di segnare piena la cella della porta A10"));
        NoStepStartIsLeftCounted(mission);
    }

    /// <summary>
    /// Il 67 sul movimento di gruppo che porta la missione di M1 (porta A6) e quella di M3 (porta A9): il
    /// gestionale rifiuta con un 4xx il blocco della prima cella che gli si chiede, e non risponde alla
    /// seconda. Solo la porta del gestionale muto va nell'elenco delle celle da segnare piene; quella
    /// rifiutata ha la sua riga di avviso e non si ritenta. Il gruppo va in errore, per il gestionale muto.
    /// L'ordine delle due domande e' quello del movimento, che non e' fisso: la prova legge dalla riga del
    /// rifiuto quale porta e' stata rifiutata e vuole l'altra nell'elenco.
    /// </summary>
    [Fact]
    public async Task AFourHundredAndASilentWarehouseInOneLockPutOnlyTheSilentDoorOnTheList()
    {
        using var log = new RecordingConsole();
        var (_, step) = await AMissionOnM1WhoseCellInSGThePlcRefuses();
        var third = await AnUnmarkedMissionOnM3ThroughX();
        third.StorageCellReserved = true;
        var thirdStep = Assert.Single(third.MissionSteps);
        var locks = 0;
        _warehouse.Fault = operation => operation == nameof(IWarehouse.SetCellLockStateAsync)
            ? ++locks == 1 ? FakeWarehouse.Rejection(operation) : new WarehouseUnavailableException(operation)
            : null;

        var group = _plant["G"];
        var token = Guid.NewGuid();
        step.GroupedMovementId = token;
        thirdStep.GroupedMovementId = token;
        group.RunningStepId = token;
        await group.ConnectionAsiMovementIsCompleted(new AsiMovementIsCompletedEventArgs(
            group.ItemGuid, token, ResultCodeTypes.Error, 67, "deposito rifiutato", null))
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(2, locks);
        var rejection = Assert.Single(log.Lines, line => line.Contains("il gestionale ha rifiutato di segnare piena la cella della porta"));
        var rejected = rejection.Contains("porta A6") ? "A6" : rejection.Contains("porta A9") ? "A9" : null;
        Assert.NotNull(rejected);
        var silent = rejected == "A6" ? "A9" : "A6";
        Assert.Equal([silent], CellsToMarkFull.Doors);
        Assert.Equal([silent], TheCellsToMarkFullInTheDatabase());
        Assert.Equal(DeviceStatusTypes.Warning, group.Status);
        Assert.Single(log.Lines, line => line.Contains($"il blocco delle porte {silent} si ritenta"));
    }

    /// <summary>
    /// La A6 e' gia' nell'elenco delle celle da segnare piene, da un 67 di prima con il gestionale muto, e
    /// con lei la A9. Un 67 nuovo sulla A6, e questa volta il gestionale risponde al blocco: la segna
    /// piena, o la rifiuta con un 4xx. In tutti e due i casi la A6 esce dall'elenco, in memoria e nel
    /// database del WCS, e la A9, a cui nessuno ha risposto, resta; la missione prende la cella 8. Prima la
    /// A6 restava, e il primo ritentativo chiedeva di nuovo un segno a cui il gestionale aveva gia'
    /// risposto (revisione finale del quinto giro della voce 39).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ADoorAlreadyOnTheListLeavesItWhenTheWarehouseAnswersANewLock(bool fourHundred)
    {
        var (mission, step) = await AMissionOnM1WhoseCellInSGThePlcRefuses();
        await CellsToMarkFull.AddAsync([StorageDoorCode, "A9"]);
        if (fourHundred)
            _warehouse.Fault = operation => operation == nameof(IWarehouse.SetCellLockStateAsync)
                ? FakeWarehouse.Rejection(operation)
                : null;

        await ThePlcRefusesTheDeposit(_plant["G"], step, 67);

        TheOperatorsMissionHasCell8(mission, step);
        Assert.Equal(DeviceStatusTypes.Ready, _plant["G"].Status);
        Assert.Equal(1, _warehouse.Calls.Count(call => call == nameof(IWarehouse.SetCellLockStateAsync)));
        Assert.Equal(["A9"], CellsToMarkFull.Doors);
        Assert.Equal(["A9"], TheCellsToMarkFullInTheDatabase());
    }

    /// <summary>
    /// Come sopra, ma quando arriva il 67 la missione sta cambiando per mano dell'operatore: nessuna
    /// ricerca, e adesso non si sa. Il gruppo va in errore e aspetta; la missione tiene la cella 6, il
    /// gateway X e lo step del deposito, marcato in errore, e non va allo scarto. Prima andava allo
    /// scarto R.
    /// </summary>
    [Fact]
    public async Task AfterError67OnTheGroupDuringAnOperatorChangeTheGroupWaitsInError()
    {
        var (mission, step) = await AMissionOnM1WhoseCellInSGThePlcRefuses();
        Assert.Equal(PathMission.OperatorChangeStart.Begun, mission.TryBeginOperatorChange());

        try
        {
            await ThePlcRefusesTheDeposit(_plant["G"], step, 67);
        }
        finally
        {
            mission.EndOperatorChange();
        }

        Assert.Equal(DeviceStatusTypes.Warning, _plant["G"].Status);
        Assert.Equal(MissionStatusTypes.Error, mission.Status);
        Assert.Equal(ResultCodeTypes.Error, step.StepStatus);
        Assert.Same(step, Assert.Single(mission.MissionSteps));
        Assert.Equal(StorageDoorCode, mission.StorageDoorCode);
        Assert.Equal(_plant["SG"].ItemGuid, mission.DestinationItemId);
        Assert.Equal(_plant["X"].ItemGuid, mission.Gateway);
        Assert.DoesNotContain(_warehouse.StorageRequests, request => request.MissionId == mission.MissionId);
    }

    /// <summary>
    /// L'operatore ha mandato la missione proprio nel punto di scarto R, passando da X (collegato a R): lo
    /// scarto per mancanza di celle non le cambia la destinazione, il segno resta, e con lui il gateway X
    /// e la rotta M1->X->R. Il gateway cade con il segno, non con ogni scarto. (Prima la prova passava dal
    /// 67 di gruppo, che mandava allo scarto ogni missione a bordo; dall'8/10 il 67 cerca una cella nuova
    /// solo per le missioni rifiutate, e lo scarto si chiama qui come lo chiama chi non trova un'altra
    /// cella.)
    /// </summary>
    [Fact]
    public async Task ARejectionOfAMissionTheOperatorSentToTheRejectionPointKeepsItsGateway()
    {
        var (mission, _) = await AMissionOnM1WhoseCellInSGThePlcRefuses();
        _plant.Link("X", "R");
        Assert.True(mission.SetOperatorDestination(_plant["R"].ItemGuid));
        mission.Gateway = _plant["X"].ItemGuid;

        Assert.True(await mission.RejectTo(PathMission.RejectionTypes.NoStoreCellAvailable).WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.Equal(_plant["R"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.OperatorDestination);
        Assert.Equal(_plant["X"].ItemGuid, mission.Gateway);
        Assert.Equal(["M1->X", "X->R"], StepsOf(mission));
    }

    /// <summary>
    /// Il rilievo del primo giro di correzioni del compito 5: la ricerca del gruppo la fa anche un
    /// membro che non c'entra con la missione dell'operatore. L'errore 85 rifiuta il deposito della
    /// missione senza segno di M2, e la ricerca del gruppo che <c>HandleStorageErrors</c> chiama per M2
    /// non tocca la missione dell'operatore su M1, che nessuno ha rifiutato: tiene la cella 6, la porta
    /// A6, l'indirizzo, il gateway X e il suo step M1->SG, che la ricerca non rifa' piu' (risposta 8 del
    /// proprietario dell'8/10 sera), e al gestionale non si chiede niente per lei. In SG non c'e'
    /// un'altra cella: prima la ricerca gliene cercava una, non la trovava, e la mandava allo scarto R,
    /// col segno caduto. La missione di M2 prende la cella 9 passando ancora dal suo gateway X (risposta 2).
    /// </summary>
    [Fact]
    public async Task AfterError85OnAnotherMemberTheOperatorsMissionKeepsItsCell()
    {
        var (mission, _) = await AMissionOnM1WhoseCellInSGThePlcRefuses();
        var other = await AnUnmarkedMissionOnM2ThroughX();
        TheWarehouseHasNoFreeCell();

        await ThePlcRefusesTheDeposit(_plant["M2"], Assert.Single(other.MissionSteps), 85);

        TheOperatorsMissionKeepsCell6(mission, "M1->SG");
        Assert.Equal("A9", other.StorageDoorCode);
        Assert.Equal(["M2->X", "X->SG"], StepsOf(other));
        NoStepStartIsLeftCounted(mission);
        NoStepStartIsLeftCounted(other);
    }

    /// <summary>
    /// Il comando manuale della ricerca del gruppo (<c>ItemFindStoringLocation</c>) non viene da un
    /// deposito rifiutato: la missione dell'operatore tiene la cella 6 e la rotta verso di lei, e il
    /// gruppo trova posto. In SG non c'e' un'altra cella: prima la ricerca la mandava allo scarto R.
    /// </summary>
    [Fact]
    public async Task TheManualGroupSearchLeavesTheOperatorsMissionItsCell()
    {
        var (mission, _) = await AMissionOnM1WhoseCellInSGThePlcRefuses();
        TheWarehouseHasNoFreeCell();

        Assert.True(await _plant["G"].StorageManager!.FindNewStoreLocationGroupedForTest().WaitAsync(TimeSpan.FromSeconds(30)));

        TheOperatorsMissionKeepsCell6(mission, "M1->X", "X->SG");
        NoStepStartIsLeftCounted(mission);
    }

    /// <summary>
    /// Come sopra, sul dispositivo singolo M1 fuori da ogni gruppo: il comando manuale non viene da un
    /// deposito rifiutato, e la missione dell'operatore tiene la cella 6, il gateway X, l'indirizzo e
    /// lo step del deposito; la ricerca risponde «nessuna posizione», come per ogni altra missione di
    /// un dispositivo fuori gruppo. Prima le cercava un'altra cella in SG, non la trovava, e le toglieva
    /// il gateway dell'operatore.
    /// </summary>
    [Fact]
    public async Task TheManualSearchOnASingleDeviceLeavesTheOperatorsMissionItsCell()
    {
        var (mission, step) = await AMissionOnM1WhoseCellInSGThePlcRefuses(inTheGroup: false);
        TheWarehouseHasNoFreeCell();

        Assert.False(await _plant["M1"].StorageManager!.FindStoringLocation().WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.Equal(_plant["SG"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.OperatorDestination);
        Assert.Equal(_plant["X"].ItemGuid, mission.Gateway);
        Assert.Equal(StorageDoorCode, mission.StorageDoorCode);
        Assert.Equal("0061", mission.DestinationAsi);
        Assert.Same(step, Assert.Single(mission.MissionSteps));
        Assert.DoesNotContain(_warehouse.StorageRequests, request => request.MissionId == mission.MissionId);
        NoStepStartIsLeftCounted(mission);
    }

    // ---------------------------------------------------------------------------------------
    // Il deposito rifiutato dal PLC, per tutte le missioni (risposta 1 del proprietario dell'8/10)
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// L'errore 85 tre volte di fila sul deposito di M1, dispositivo singolo: ogni volta una cella nuova
    /// nel magazzino scelto dall'operatore - la 8, la 9, la 10 - e ogni domanda al gestionale esclude
    /// tutte le celle rifiutate fin li', cosi' le tre celle sono diverse. Il quarto 85 di fila manda M1
    /// in errore: nessuna domanda al gestionale, nessuno scarto, e la missione resta dell'operatore, verso
    /// la cella 10, con lo step marcato in errore. Prima: due cambi di cella, al terzo errore lo scarto R.
    /// </summary>
    [Fact]
    public async Task ThreeErrors85InARowTakeThreeDifferentCellsAndTheFourthStopsTheDevice()
    {
        var (mission, step) = await AMissionOnM1WhoseCellInSGThePlcRefuses(inTheGroup: false);
        await TheStorageSGHasTheCells(6, 8, 9, 10);

        await M1RefusesAndTheMissionTakes(85, step, 8, [6]);
        await M1RefusesAndTheMissionTakes(85, mission.MissionSteps[0], 9, [6, 8]);
        await M1RefusesAndTheMissionTakes(85, mission.MissionSteps[0], 10, [6, 8, 9]);
        Assert.NotEqual(DeviceStatusTypes.Warning, _plant["M1"].Status);

        TheWarehouseHasCell(11);
        var asked = _warehouse.StorageRequests.Count;
        var refused = mission.MissionSteps[0];
        await ThePlcRefusesTheDeposit(_plant["M1"], refused, 85);

        Assert.Equal(asked, _warehouse.StorageRequests.Count);
        TheDeviceWaitsInErrorWithTheOperatorsMission(_plant["M1"], mission, refused, "A10");
        NoStepStartIsLeftCounted(mission);
    }

    /// <summary>
    /// 85, 67, 85 sul deposito di M1: il conto e' per codice, e un codice diverso lo fa ripartire da uno
    /// (1, 1, 1). Tre celle nuove, diverse, e M1 non va in errore. Prima, al terzo errore di qualunque
    /// codice, lo scarto. Un quarto rifiuto, ancora l'85, e' il secondo di fila con quel codice: cerca
    /// ancora, la cella 11. Un conto che non guardasse il codice sarebbe a quattro, e fermerebbe M1.
    /// </summary>
    [Fact]
    public async Task ADifferentCodeStartsTheCountAgain()
    {
        var (mission, step) = await AMissionOnM1WhoseCellInSGThePlcRefuses(inTheGroup: false);
        await TheStorageSGHasTheCells(6, 8, 9, 10, 11);

        await M1RefusesAndTheMissionTakes(85, step, 8, [6]);
        await M1RefusesAndTheMissionTakes(67, mission.MissionSteps[0], 9, [6, 8]);
        await M1RefusesAndTheMissionTakes(85, mission.MissionSteps[0], 10, [6, 8, 9]);

        Assert.NotEqual(DeviceStatusTypes.Warning, _plant["M1"].Status);
        Assert.Equal(85, _plant["M1"].RefusedDepositErrorCode);
        Assert.Equal(1, _plant["M1"].RefusedDepositsInARow);

        await M1RefusesAndTheMissionTakes(85, mission.MissionSteps[0], 11, [6, 8, 9, 10]);
        Assert.NotEqual(DeviceStatusTypes.Warning, _plant["M1"].Status);
        NoStepStartIsLeftCounted(mission);
    }

    /// <summary>
    /// Tre 85 di fila sul deposito di M1, poi l'operatore rimette in marcia il dispositivo (il comando
    /// «esegui step», o «tutti pronti»): il conto ricomincia, e il quarto 85 cerca ancora una cella nuova,
    /// la 11, invece di mandare M1 in errore. Le celle rifiutate restano escluse: sono della missione, e
    /// la ripartenza non le tocca (risposta 7 del proprietario dell'8/10 sera); prima la ripartenza le
    /// dimenticava con il conto, e la domanda escludeva la 10 soltanto.
    /// </summary>
    [Theory]
    [InlineData("execute step")]
    [InlineData("all ready")]
    public async Task TheOperatorWhoSetsTheDeviceGoingStartsTheCountAgain(string restart)
    {
        var (mission, step) = await AMissionOnM1WhoseCellInSGThePlcRefuses(inTheGroup: false);
        await TheStorageSGHasTheCells(6, 8, 9, 10, 11);

        await M1RefusesAndTheMissionTakes(85, step, 8, [6]);
        await M1RefusesAndTheMissionTakes(85, mission.MissionSteps[0], 9, [6, 8]);
        await M1RefusesAndTheMissionTakes(85, mission.MissionSteps[0], 10, [6, 8, 9]);

        if (restart == "execute step")
            await ExecutableCommands.ItemExecuteStep(new Command { ItemCommandId = _plant["M1"].ItemGuid })
                .WaitAsync(TimeSpan.FromSeconds(30));
        else
            ExecutableCommands.SetStatusReadyAllItems();

        Assert.Equal(0, _plant["M1"].RefusedDepositsInARow);

        // Il conto di nuovo da uno, ma le celle rifiutate prima ancora escluse.
        await M1RefusesAndTheMissionTakes(85, mission.MissionSteps[0], 11, [6, 8, 9, 10]);
        Assert.NotEqual(DeviceStatusTypes.Warning, _plant["M1"].Status);
    }

    /// <summary>
    /// Il comando generale «esegui step» dell'operatore, come quello sul dispositivo e «tutti pronti»: il
    /// ricollocatore andato in errore al quarto 85 di fila torna pronto con il conto azzerato, e il
    /// rifiuto successivo e' il primo della fila. Prima
    /// tornava pronto con il conto a quattro, e il quinto 85 lo rimandava subito in errore senza cercare.
    /// Il ricollocatore non ha missioni a bordo: il comando non gli fa partire niente, lo rimette solo
    /// pronto. Il navigatore in Run serve al comando, e il cambio di stato del navigatore sveglia ogni
    /// dispositivo ancora iscritto (anche quelli lasciati da altre classi di prove), che prova un
    /// ExecuteStep suo: la prova guarda solo il ricollocatore, che nessuno di quelli tocca.
    /// </summary>
    [Fact]
    public void TheOperatorsGeneralExecuteStepsStartsTheCountAgain()
    {
        var relocator = _plant.Device("M1");
        for (var refusal = 0; refusal < 4; refusal++)
            relocator.CountRefusedDeposit(85);
        Assert.Equal(4, relocator.RefusedDepositsInARow);
        relocator.Status = DeviceStatusTypes.Warning;
        Navigator.NavStatus = NavigatorStatusTypes.Run;

        ExecutableCommands.ExecuteSteps();

        Assert.Equal(DeviceStatusTypes.Ready, relocator.Status);
        Assert.Equal(0, relocator.RefusedDepositsInARow);
        Assert.Equal(0, relocator.RefusedDepositErrorCode);
        Assert.Equal(1, relocator.CountRefusedDeposit(85));
    }

    /// <summary>
    /// Quattro 85 di fila sul deposito di <paramref name="refusing"/> (M1, membro del gruppo G, o G
    /// stesso): al quarto, nessuna ricerca, e il dispositivo va in errore con la missione verso la cella
    /// 10. L'operatore rimette in marcia con «esegui step» l'altro dei due (<paramref name="restarted"/>),
    /// che riporta a Ready anche lui (il gruppo i membri, il membro il gruppo): il conto ricomincia, e il
    /// quinto 85 cerca ancora, la cella 11, escludendo ancora le quattro celle rifiutate (risposta 7 del
    /// proprietario dell'8/10 sera: sono della missione, e la ripartenza non le tocca). Prima ricominciava
    /// solo il conto del dispositivo del comando, e il quinto 85 rimandava in errore senza cercare.
    /// </summary>
    [Theory]
    [InlineData("M1", "G")]
    [InlineData("G", "M1")]
    public async Task RestartingTheGroupOrAMemberStartsTheCountOfBothAgain(string refusing, string restarted)
    {
        var (mission, step) = await AMissionOnM1WhoseCellInSGThePlcRefuses();
        await TheStorageSGHasTheCells(6, 8, 9, 10, 11);
        var device = _plant[refusing];
        Func<MissionStep, decimal, decimal[], Task> refusesAndTakes = refusing == "G"
            ? (refused, cell, excluded) => TheGroupRefusesAndTheOperatorsMissionTakes(85, refused, cell, excluded)
            : (refused, cell, excluded) => M1RefusesAndTheMissionTakes(85, refused, cell, excluded);

        await refusesAndTakes(step, 8, [6]);
        await refusesAndTakes(mission.MissionSteps[0], 9, [6, 8]);
        await refusesAndTakes(mission.MissionSteps[0], 10, [6, 8, 9]);

        var fourth = mission.MissionSteps[0];
        var asked = _warehouse.StorageRequests.Count;
        await ThePlcRefusesTheDeposit(device, fourth, 85);
        Assert.Equal(asked, _warehouse.StorageRequests.Count);
        TheDeviceWaitsInErrorWithTheOperatorsMission(device, mission, fourth, "A10");

        await ExecutableCommands.ItemExecuteStep(new Command { ItemCommandId = _plant[restarted].ItemGuid })
            .WaitAsync(TimeSpan.FromSeconds(30));

        await refusesAndTakes(mission.MissionSteps[0], 11, [6, 8, 9, 10]);
    }

    /// <summary>
    /// Tre 85 di fila sul deposito di gruppo, poi un deposito riuscito del gruppo, quello della missione
    /// della configurazione su M2 nella cella 9, con il gettone del movimento del gruppo: il conto
    /// ricomincia, e il quarto 85 cerca ancora una cella nuova, la 11, e il gruppo torna pronto invece di
    /// andare in errore. Le celle rifiutate alla missione dell'operatore restano escluse: a depositare e'
    /// stata un'altra UDC.
    /// </summary>
    [Fact]
    public async Task ASuccessfulMovementInBetweenStartsTheCountAgain()
    {
        var (mission, step) = await AMissionOnM1WhoseCellInSGThePlcRefuses();
        await TheStorageSGHasTheCells(6, 8, 9, 10, 11);

        await TheGroupRefusesAndTheOperatorsMissionTakes(85, step, 8, [6]);
        await TheGroupRefusesAndTheOperatorsMissionTakes(85, mission.MissionSteps[0], 9, [6, 8]);
        await TheGroupRefusesAndTheOperatorsMissionTakes(85, mission.MissionSteps[0], 10, [6, 8, 9]);

        var other = await AnUnmarkedMissionOnM2ThroughX();
        var deposit = Assert.Single(other.MissionSteps);
        var token = Guid.NewGuid();
        deposit.GroupedMovementId = token;
        _plant["G"].RunningStepId = token;
        await _plant["G"].ConnectionAsiMovementIsCompleted(new AsiMovementIsCompletedEventArgs(
            _plant["G"].ItemGuid, token, ResultCodeTypes.Success, 0, string.Empty, null))
            .WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(ResultCodeTypes.Success, deposit.StepStatus);
        Assert.Equal(0, _plant["G"].RefusedDepositsInARow);

        await TheGroupRefusesAndTheOperatorsMissionTakes(85, mission.MissionSteps[0], 11, [6, 8, 9, 10]);
        Assert.Equal(DeviceStatusTypes.Ready, _plant["G"].Status);
    }

    /// <summary>
    /// Risposta 5 del proprietario dell'8/10 sera: tre 85 di fila sul deposito di gruppo, poi un errore di
    /// deposito mentre il gruppo deframmenta la cella 9, che va in errore senza contare. Non e' un
    /// movimento riuscito ne' l'operatore: il conto resta a tre, e il rifiuto dopo, ancora l'85, e' il
    /// quarto di fila. Il gruppo va in errore senza chiedere niente al gestionale. Prima l'errore del
    /// gruppo che deframmenta azzerava il conto, e il quarto 85 cercava ancora.
    /// </summary>
    [Fact]
    public async Task AnErrorWhileTheGroupDefragmentsDoesNotStartTheCountAgain()
    {
        var (mission, step) = await AMissionOnM1WhoseCellInSGThePlcRefuses();
        await TheStorageSGHasTheCells(6, 8, 9, 10, 11);

        await TheGroupRefusesAndTheOperatorsMissionTakes(85, step, 8, [6]);
        await TheGroupRefusesAndTheOperatorsMissionTakes(85, mission.MissionSteps[0], 9, [6, 8]);
        await TheGroupRefusesAndTheOperatorsMissionTakes(85, mission.MissionSteps[0], 10, [6, 8, 9]);

        var group = _plant["G"];
        group.IdCellToDefragment = 9;
        try
        {
            await ThePlcRefusesTheDeposit(group, mission.MissionSteps[0], 85);
        }
        finally
        {
            group.IdCellToDefragment = 0;
        }

        Assert.Equal(3, group.RefusedDepositsInARow);
        Assert.Equal(85, group.RefusedDepositErrorCode);

        TheWarehouseHasCell(11);
        var asked = _warehouse.StorageRequests.Count;
        var fourth = mission.MissionSteps[0];
        await ThePlcRefusesTheDeposit(group, fourth, 85);

        Assert.Equal(asked, _warehouse.StorageRequests.Count);
        TheGroupWaitsInErrorWithTheOperatorsMission(mission, fourth, "A10");
    }

    /// <summary>
    /// L'errore 85 sul deposito di M1, e il gestionale non risponde alla domanda della cella nuova: M1 va
    /// in errore e aspetta l'operatore. Nessun nuovo deposito verso la cella rifiutata (il dispositivo e'
    /// in errore, non riparte) e nessuno scarto: la missione resta dell'operatore, verso SG, in errore.
    /// Prima la missione teneva la cella rifiutata e il deposito ripartiva verso di lei.
    /// </summary>
    [Fact]
    public async Task AWarehouseSilentOnTheNewCellStopsTheDeviceInError()
    {
        var (mission, step) = await AMissionOnM1WhoseCellInSGThePlcRefuses(inTheGroup: false);
        _warehouse.UnavailableOperations.Add(nameof(IWarehouse.ReserveStorageAsync));

        await ThePlcRefusesTheDeposit(_plant["M1"], step, 85);

        Assert.Equal(DeviceStatusTypes.Warning, _plant["M1"].Status);
        Assert.Equal(MissionStatusTypes.Error, mission.Status);
        Assert.Equal(ResultCodeTypes.Error, step.StepStatus);
        Assert.Equal(_plant["SG"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.OperatorDestination);
        Assert.NotEqual(MissionTypes.Rejection, mission.MissionType);
        Assert.DoesNotContain(mission.MissionSteps, planned => planned.ToNodeId == _plant["R"].ItemGuid);
        NoStepStartIsLeftCounted(mission);
    }

    /// <summary>
    /// L'errore 85 sul deposito di M1, nel magazzino scelto non c'e' un'altra cella, e non c'e' un punto
    /// di scarto per mancanza di celle: M1 va in errore e aspetta l'operatore, e la missione resta
    /// dell'operatore con la cella 6 rifiutata e lo step in errore. Prima la missione restava senza
    /// scarto e M1 non andava in errore: lo <c>StepError</c> di chi gestiva l'errore non trovava piu' lo step.
    /// </summary>
    [Fact]
    public async Task WithoutAnotherCellAndWithoutARejectionPointTheDeviceWaitsInError()
    {
        var (mission, step) = await AMissionOnM1WhoseCellInSGThePlcRefuses(inTheGroup: false);
        _plant["M1"].RejectItemDestinationAllocateLu = Guid.Empty;
        TheWarehouseHasNoFreeCell();

        await ThePlcRefusesTheDeposit(_plant["M1"], step, 85);

        Assert.Equal(DeviceStatusTypes.Warning, _plant["M1"].Status);
        Assert.Equal(MissionStatusTypes.Error, mission.Status);
        Assert.Equal(ResultCodeTypes.Error, step.StepStatus);
        Assert.Equal(_plant["SG"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.OperatorDestination);
        Assert.NotEqual(MissionTypes.Rejection, mission.MissionType);
    }

    /// <summary>
    /// Una missione della configurazione sul dispositivo singolo M1, fuori da ogni gruppo: l'errore 85 al
    /// deposito nella cella 6, e il motore le cerca una cella nuova con la ricerca della riverifica del
    /// ricollocatore, con la 6 esclusa dalla domanda: la cella 8, con la rotta M1->SG, e M1 riparte. Prima,
    /// fuori da un gruppo, nessuna ricerca: lo scarto R.
    /// </summary>
    [Fact]
    public async Task AConfigurationMissionOnASingleDeviceTakesANewCellThatIsNotTheRefusedOne()
    {
        var (mission, step) = await AnUnmarkedMissionOnTheSingleDeviceM1WhoseCellInSGThePlcRefuses();

        await ThePlcRefusesTheDeposit(_plant["M1"], step, 85);

        Assert.Equal(OtherDoorCode, mission.StorageDoorCode);
        Assert.Equal(_plant["SG"].ItemGuid, mission.DestinationItemId);
        Assert.Equal("0081", mission.DestinationAsi);
        Assert.Equal(["M1->SG"], StepsOf(mission));
        Assert.NotEqual(DeviceStatusTypes.Warning, _plant["M1"].Status);
        var requests = _warehouse.StorageRequests.Where(request => request.MissionId == mission.MissionId).ToList();
        Assert.NotEmpty(requests);
        Assert.All(requests, request => Assert.Contains(StorageCellId, request.ExcludedCellIds));
        Assert.Equal(OtherCellId, _warehouse.StorageReservationsByMission[mission.MissionId].CellId);
        NoStepStartIsLeftCounted(mission);
    }

    /// <summary>
    /// Come sopra, ma il gestionale non ha un'altra cella: la missione della configurazione va allo scarto
    /// R per mancanza di celle, e M1 riparte.
    /// </summary>
    [Fact]
    public async Task AConfigurationMissionWithoutAnotherCellGoesToTheRejectionPoint()
    {
        var (mission, step) = await AnUnmarkedMissionOnTheSingleDeviceM1WhoseCellInSGThePlcRefuses();
        TheWarehouseHasNoFreeCell();

        await ThePlcRefusesTheDeposit(_plant["M1"], step, 85);

        Assert.Equal(_plant["R"].ItemGuid, mission.DestinationItemId);
        Assert.Equal(MissionTypes.Rejection, mission.MissionType);
        Assert.Equal(["M1->R"], StepsOf(mission));
        Assert.NotEqual(DeviceStatusTypes.Warning, _plant["M1"].Status);
    }

    /// <summary>
    /// Come sopra, ma mentre il rigetto calcola la rotta M1->R l'UDC viene spostata su Z, collegato a R:
    /// il rigetto perde la costruzione, e si completa dalla posizione nuova (risposta 5 del proprietario
    /// dell'11/10). La missione e' un rigetto verso R, in coda al giro delle rotte, e M1 non va in errore.
    /// Prima il rigetto che perdeva rispondeva come un rigetto mancato: M1 andava in errore con la missione
    /// gia' in coda, e la missione restava a meta', verso R con il tipo di prima.
    /// </summary>
    [Fact]
    public async Task ARejectionWhoseRouteIsLostToAMoveIsCompletedAndTheDeviceDoesNotStop()
    {
        var (mission, step) = await AnUnmarkedMissionOnTheSingleDeviceM1WhoseCellInSGThePlcRefuses();
        TheWarehouseHasNoFreeCell();
        _plant.Device("Z");
        _plant.Link("Z", "R");

        // La missione entra in Recover (vedi le note della classe), ma il rifiuto e lo spostamento girano
        // in Stop: la scena ci torna alla fine.
        Assert.Equal(NavigatorStatusTypes.Stop, Navigator.NavStatus);

        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mission.RouteBuildPauseForTesting = () =>
        {
            mission.RouteBuildPauseForTesting = null;
            reached.TrySetResult();
            return released.Task;
        };

        var refusal = ThePlcRefusesTheDeposit(_plant["M1"], step, 85);
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(_plant["R"].ItemGuid, mission.DestinationItemId);
            Assert.Equal(PathMission.RouteWriteOutcome.Written, mission.MoveTo(_plant["Z"].ItemGuid));
        }
        finally
        {
            released.TrySetResult();
        }

        await refusal;

        Assert.NotEqual(DeviceStatusTypes.Warning, _plant["M1"].Status);
        Assert.Equal(_plant["Z"].ItemGuid, mission.PositionItemId);
        Assert.Equal(_plant["R"].ItemGuid, mission.DestinationItemId);
        Assert.Equal(MissionTypes.Rejection, mission.MissionType);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
    }

    /// <summary>
    /// Come sopra, ma mentre il rigetto calcola la rotta M1->R un altro manda la missione verso X: non e'
    /// un rigetto, la missione resta verso X con il tipo di prima, in coda al giro delle rotte, e M1 non va
    /// in errore per lei (risposta 5 del proprietario dell'11/10: il falso «perso, gia' in coda» non e' un
    /// rifiuto). Prima M1 andava in errore con la missione gia' in coda.
    /// </summary>
    [Fact]
    public async Task ARejectionWhoseRouteIsTakenOverDoesNotStopTheDevice()
    {
        var (mission, step) = await AnUnmarkedMissionOnTheSingleDeviceM1WhoseCellInSGThePlcRefuses();
        TheWarehouseHasNoFreeCell();
        var typeBefore = mission.MissionType;
        Assert.NotEqual(MissionTypes.Rejection, typeBefore);
        Assert.Equal(NavigatorStatusTypes.Stop, Navigator.NavStatus);

        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mission.RouteBuildPauseForTesting = () =>
        {
            mission.RouteBuildPauseForTesting = null;
            reached.TrySetResult();
            return released.Task;
        };

        var refusal = ThePlcRefusesTheDeposit(_plant["M1"], step, 85);
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
            mission.DestinationItemId = _plant["X"].ItemGuid;
        }
        finally
        {
            released.TrySetResult();
        }

        await refusal;

        Assert.NotEqual(DeviceStatusTypes.Warning, _plant["M1"].Status);
        Assert.Equal(_plant["X"].ItemGuid, mission.DestinationItemId);
        Assert.Equal(typeBefore, mission.MissionType);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
    }

    /// <summary>
    /// Come sopra, con il gateway X e una porta A7 che nel database del WCS non ha una cella: il PLC rifiuta
    /// il deposito, la ricerca tiene il gateway (risposta 2 dell'8/10 sera), il gestionale non ha un'altra
    /// cella, e lo scarto toglie il gateway anche qui, dove nessuna cella rifiutata e' stata contata: la
    /// missione va in R con la rotta M1->R. Prima lo scarto guardava le celle rifiutate, e con nessuna teneva
    /// il gateway: verso R per X non c'e' percorso, il rigetto non si faceva e M1 andava in errore
    /// (revisione finale del terzo giro).
    /// </summary>
    [Fact]
    public async Task ARefusedDoorWithoutACellStillDropsTheGatewayAtTheRejection()
    {
        var (mission, step) = await AnUnmarkedMissionOnTheSingleDeviceM1WhoseCellInSGThePlcRefuses();
        _plant.Link("X", "SG");
        mission.Gateway = _plant["X"].ItemGuid;
        mission.StorageDoorCode = "A7";
        TheWarehouseHasNoFreeCell();

        await ThePlcRefusesTheDeposit(_plant["M1"], step, 85);

        Assert.Empty(mission.CellsRefusedByThePlc);
        Assert.True(mission.DepositRefusedByThePlc);
        Assert.Equal(_plant["R"].ItemGuid, mission.DestinationItemId);
        Assert.Equal(MissionTypes.Rejection, mission.MissionType);
        Assert.Equal(Guid.Empty, mission.Gateway);
        Assert.Equal(["M1->R"], StepsOf(mission));
        Assert.NotEqual(DeviceStatusTypes.Warning, _plant["M1"].Status);
    }

    /// <summary>
    /// Come sopra, ma il gestionale non risponde alla ricerca della missione della configurazione: M1 va
    /// in errore e aspetta, la missione non va allo scarto e resta verso SG con lo step in errore. Prima
    /// il guasto risaliva al gestore del completamento, e M1 restava com'era, senza errore e senza deposito.
    /// </summary>
    [Fact]
    public async Task AConfigurationMissionWithTheWarehouseSilentStopsTheDeviceInError()
    {
        var (mission, step) = await AnUnmarkedMissionOnTheSingleDeviceM1WhoseCellInSGThePlcRefuses();
        _warehouse.UnavailableOperations.Add(nameof(IWarehouse.ReserveStorageAsync));

        await ThePlcRefusesTheDeposit(_plant["M1"], step, 85);

        Assert.Equal(DeviceStatusTypes.Warning, _plant["M1"].Status);
        Assert.Equal(MissionStatusTypes.Error, mission.Status);
        Assert.Equal(ResultCodeTypes.Error, step.StepStatus);
        Assert.Equal(_plant["SG"].ItemGuid, mission.DestinationItemId);
        Assert.NotEqual(MissionTypes.Rejection, mission.MissionType);
        NoStepStartIsLeftCounted(mission);
    }

    /// <summary>
    /// Una missione della configurazione sul membro M2 del gruppo G: il PLC rifiuta con l'85 il deposito
    /// nella cella 6, che il gestionale le tiene ancora. La ricerca del gruppo la esclude dalla domanda,
    /// la prenotazione della 6 si lascia, e la missione prende la cella 9. Prima la ricerca teneva la
    /// prenotazione della 6, ancora buona per lei, e la ridava uguale. La missione e' senza il segno, e il
    /// suo gateway X resta: la rotta verso la cella 9 passa ancora da lui (risposta 2 del proprietario
    /// dell'8/10 sera). Prima la ricerca del gruppo glielo toglieva, e la rotta era M2->SG.
    /// </summary>
    [Fact]
    public async Task AConfigurationMissionOnAMemberDoesNotGetTheRefusedCellBack()
    {
        await AMissionOnM1WhoseCellInSGThePlcRefuses();
        var other = await AnUnmarkedMissionOnM2ThroughX();
        _warehouse.StorageReservationsByMission[other.MissionId] = new StorageReservation(other.MissionId,
            StorageReservationOutcome.CellFound, StorageCellId, StorageDoorCode, Partition: "0063", StorageDepth: 150,
            NeedsCompaction: false);

        await ThePlcRefusesTheDeposit(_plant["M2"], Assert.Single(other.MissionSteps), 85);

        Assert.Equal("A9", other.StorageDoorCode);
        Assert.False(other.OperatorDestination);
        Assert.Equal(_plant["X"].ItemGuid, other.Gateway);
        Assert.Equal(["M2->X", "X->SG"], StepsOf(other));
        Assert.Contains(other.MissionId, _warehouse.ReleasedStorageReservations);
        Assert.Equal(9m, _warehouse.StorageReservationsByMission[other.MissionId].CellId);
        Assert.All(_warehouse.StorageRequests.Where(request => request.MissionId == other.MissionId),
            request => Assert.Contains(StorageCellId, request.ExcludedCellIds));
        NoStepStartIsLeftCounted(other);
    }

    /// <summary>
    /// Come sopra, ma il gestionale non ha un'altra cella per la missione della configurazione del membro
    /// M2: la ricerca del gruppo la manda allo scarto R del gruppo, e M2 riparte. Lo scarto e' un esito che
    /// va bene, non un posto che manca: il dispositivo non va in errore.
    /// </summary>
    [Fact]
    public async Task AConfigurationMissionOnAMemberWithoutAnotherCellGoesToTheRejectionPoint()
    {
        await AMissionOnM1WhoseCellInSGThePlcRefuses();
        var other = await AnUnmarkedMissionOnM2ThroughX();
        _plant.Link("M2", "R");
        _warehouse.StorageReservations[43] = new StorageReservation(Guid.Empty,
            StorageReservationOutcome.NoFreeCells, 0, string.Empty, string.Empty, 0, NeedsCompaction: false);

        await ThePlcRefusesTheDeposit(_plant["M2"], Assert.Single(other.MissionSteps), 85);

        Assert.Equal(_plant["R"].ItemGuid, other.DestinationItemId);
        Assert.Equal(MissionTypes.Rejection, other.MissionType);
        Assert.Equal(["M2->R"], StepsOf(other));
        Assert.NotEqual(DeviceStatusTypes.Warning, _plant["M2"].Status);
        Assert.NotEqual(MissionStatusTypes.Error, other.Status);
        NoStepStartIsLeftCounted(other);
    }

    /// <summary>
    /// Come sopra, ma il gestionale non risponde alla ricerca del gruppo per la missione della
    /// configurazione del membro M2: M2 va in errore e aspetta, e la missione resta verso SG, in errore,
    /// senza scarto. La ricerca del gruppo le ha gia' tolto lo step del deposito rifiutato: l'errore lo
    /// marca lo stesso, perche' lo step si prende prima della ricerca. Prima il guasto risaliva al gestore
    /// del completamento, e M2 restava com'era.
    /// </summary>
    [Fact]
    public async Task AConfigurationMissionOnAMemberWithTheWarehouseSilentStopsTheDeviceInError()
    {
        await AMissionOnM1WhoseCellInSGThePlcRefuses();
        var other = await AnUnmarkedMissionOnM2ThroughX();
        var refused = Assert.Single(other.MissionSteps);
        _warehouse.UnavailableOperations.Add(nameof(IWarehouse.ReserveStorageAsync));

        await ThePlcRefusesTheDeposit(_plant["M2"], refused, 85);

        Assert.Equal(DeviceStatusTypes.Warning, _plant["M2"].Status);
        Assert.Equal(MissionStatusTypes.Error, other.Status);
        Assert.Equal(ResultCodeTypes.Error, refused.StepStatus);
        Assert.Equal(_plant["SG"].ItemGuid, other.DestinationItemId);
        Assert.NotEqual(MissionTypes.Rejection, other.MissionType);
        NoStepStartIsLeftCounted(other);
    }

    /// <summary>
    /// L'errore <paramref name="errorCode"/> sul deposito di gruppo della missione dell'operatore, in SG
    /// non c'e' un'altra cella, e il gruppo non ha un punto di scarto per mancanza di celle: il gruppo va
    /// in errore e aspetta l'operatore. La missione resta dell'operatore, verso SG, in errore, con lo step
    /// del deposito marcato, e senza una rotta nuova: nessun deposito riparte verso la cella 6 rifiutata,
    /// e nessuno scarto. E' la regola del dispositivo singolo
    /// (<see cref="WithoutAnotherCellAndWithoutARejectionPointTheDeviceWaitsInError"/>), che la ricerca del
    /// gruppo prima ripeteva per conto suo e nessuna prova guardava.
    /// </summary>
    [Theory]
    [InlineData(85)]
    [InlineData(67)]
    public async Task OnTheGroupWithoutAnotherCellAndWithoutARejectionPointTheGroupWaitsInError(int errorCode)
    {
        var (mission, step) = await AMissionOnM1WhoseCellInSGThePlcRefuses();
        _plant["G"].RejectItemDestinationAllocateLu = Guid.Empty;
        TheWarehouseHasNoFreeCell();

        await ThePlcRefusesTheDeposit(_plant["G"], step, errorCode);

        Assert.Equal(DeviceStatusTypes.Warning, _plant["G"].Status);
        Assert.Equal(MissionStatusTypes.Error, mission.Status);
        Assert.Equal(ResultCodeTypes.Error, step.StepStatus);
        Assert.Equal(_plant["SG"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.OperatorDestination);
        Assert.NotEqual(MissionTypes.Rejection, mission.MissionType);
        Assert.Empty(StepsOf(mission));
        NoStepStartIsLeftCounted(mission);
    }

    /// <summary>
    /// L'85 sul deposito di gruppo della missione dell'operatore, con la cella 6 prenotata per lei: la
    /// domanda senza prenotare propone la cella 8, ma alla prenotazione il gestionale non ne ha piu'. La
    /// prenotazione aveva gia' lasciato la cella 6: la missione la perde (porta, indirizzo e rotta tolti,
    /// in coda al navigatore), e senza un punto di scarto il gruppo va in errore e aspetta. E' la via della
    /// cella rifiutata dal PLC dentro il flusso del magazzino che ha in comune con il cambio
    /// dell'operatore (MissionGateway.ReserveOrLetGoAsync, voce 51): prima la scriveva per conto suo, e
    /// nessuna prova la guardava.
    /// </summary>
    [Fact]
    public async Task ARefusedCellWhoseNewReservationFindsNoCellLosesTheCellItHeld()
    {
        var (mission, step) = await AMissionOnM1WhoseCellInSGThePlcRefuses();
        _plant["G"].RejectItemDestinationAllocateLu = Guid.Empty;
        _warehouse.StorageReservations[mission.LuId] = new StorageReservation(Guid.Empty,
            StorageReservationOutcome.CellFound, OtherCellId, OtherDoorCode, Partition: "0083", StorageDepth: 250,
            NeedsCompaction: false);
        var reservations = 0;
        _warehouse.Before = operation =>
        {
            if (operation == nameof(IWarehouse.ReserveStorageAsync) && ++reservations == 2)
                _warehouse.StorageReservations[mission.LuId] = new StorageReservation(Guid.Empty,
                    StorageReservationOutcome.NoFreeCells, 0, string.Empty, string.Empty, 0, NeedsCompaction: false);
            return Task.CompletedTask;
        };

        await ThePlcRefusesTheDeposit(_plant["G"], step, 85);

        Assert.Equal(2, reservations);
        Assert.True(mission.StorageCellLost);
        Assert.Null(mission.StorageDoorCode);
        Assert.Empty(StepsOf(mission));
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
        Assert.Equal(DeviceStatusTypes.Warning, _plant["G"].Status);
        Assert.Equal(_plant["SG"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.OperatorDestination);
        NoStepStartIsLeftCounted(mission);
    }

    /// <summary>
    /// Come <see cref="AConfigurationMissionWithoutAnotherCellGoesToTheRejectionPoint"/>, ma M1 non ha un
    /// punto di scarto per mancanza di celle: M1 va in errore e aspetta l'operatore, e la missione della
    /// configurazione resta verso SG, in errore, senza scarto, con il solo step del deposito rifiutato,
    /// marcato.
    /// </summary>
    [Fact]
    public async Task AConfigurationMissionWithoutAnotherCellAndWithoutARejectionPointStopsTheDeviceInError()
    {
        var (mission, step) = await AnUnmarkedMissionOnTheSingleDeviceM1WhoseCellInSGThePlcRefuses();
        _plant["M1"].RejectItemDestinationAllocateLu = Guid.Empty;
        TheWarehouseHasNoFreeCell();

        await ThePlcRefusesTheDeposit(_plant["M1"], step, 85);

        Assert.Equal(DeviceStatusTypes.Warning, _plant["M1"].Status);
        Assert.Equal(MissionStatusTypes.Error, mission.Status);
        Assert.Equal(ResultCodeTypes.Error, step.StepStatus);
        Assert.Equal(_plant["SG"].ItemGuid, mission.DestinationItemId);
        Assert.NotEqual(MissionTypes.Rejection, mission.MissionType);
        Assert.Same(step, Assert.Single(mission.MissionSteps));
        NoStepStartIsLeftCounted(mission);
    }

    /// <summary>
    /// Come <see cref="AConfigurationMissionOnAMemberWithoutAnotherCellGoesToTheRejectionPoint"/>, ma il
    /// gruppo G non ha un punto di scarto per mancanza di celle: M2 va in errore e aspetta l'operatore, e
    /// la missione della configurazione resta verso SG, in errore, senza scarto e senza rotta.
    /// </summary>
    [Fact]
    public async Task AConfigurationMissionOnAMemberWithoutAnotherCellAndWithoutARejectionPointStopsTheMemberInError()
    {
        await AMissionOnM1WhoseCellInSGThePlcRefuses();
        var other = await AnUnmarkedMissionOnM2ThroughX();
        var refused = Assert.Single(other.MissionSteps);
        _plant["G"].RejectItemDestinationAllocateLu = Guid.Empty;
        _warehouse.StorageReservations[43] = new StorageReservation(Guid.Empty,
            StorageReservationOutcome.NoFreeCells, 0, string.Empty, string.Empty, 0, NeedsCompaction: false);

        await ThePlcRefusesTheDeposit(_plant["M2"], refused, 85);

        Assert.Equal(DeviceStatusTypes.Warning, _plant["M2"].Status);
        Assert.Equal(MissionStatusTypes.Error, other.Status);
        Assert.Equal(ResultCodeTypes.Error, refused.StepStatus);
        Assert.Equal(_plant["SG"].ItemGuid, other.DestinationItemId);
        Assert.NotEqual(MissionTypes.Rejection, other.MissionType);
        Assert.Empty(StepsOf(other));
        NoStepStartIsLeftCounted(other);
    }

    // ---------------------------------------------------------------------------------------
    // La rifinitura degli errori del PLC (compito 13, risposte del proprietario dell'8/10 sera)
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Risposta 2 del proprietario dell'8/10 sera: una missione della configurazione, senza il segno, sul
    /// dispositivo singolo M1, con il gateway X (collegato a SG). Il PLC rifiuta con l'85 il deposito nella
    /// cella 6: la missione prende la cella 8, e il gateway X resta, con la rotta che passa da lui
    /// (M1->X->SG). Cambiano cella, porta e rotta; il segno non si alza. Prima la ricerca le toglieva il
    /// gateway, e la rotta era M1->SG. Sul membro di un gruppo lo stesso:
    /// <see cref="AConfigurationMissionOnAMemberDoesNotGetTheRefusedCellBack"/>.
    /// </summary>
    [Fact]
    public async Task AConfigurationMissionKeepsItsGatewayWhenThePlcRefusesItsCell()
    {
        var (mission, step) = await AnUnmarkedMissionOnTheSingleDeviceM1WhoseCellInSGThePlcRefuses();
        _plant.Link("X", "SG");
        mission.Gateway = _plant["X"].ItemGuid;

        await ThePlcRefusesTheDeposit(_plant["M1"], step, 85);

        Assert.Equal(OtherDoorCode, mission.StorageDoorCode);
        Assert.Equal(_plant["SG"].ItemGuid, mission.DestinationItemId);
        Assert.False(mission.OperatorDestination);
        Assert.Equal(_plant["X"].ItemGuid, mission.Gateway);
        Assert.Equal(["M1->X", "X->SG"], StepsOf(mission));
        Assert.Equal(OtherCellId, _warehouse.StorageReservationsByMission[mission.MissionId].CellId);
        Assert.NotEqual(DeviceStatusTypes.Warning, _plant["M1"].Status);
        NoStepStartIsLeftCounted(mission);
    }

    /// <summary>
    /// Risposta 5 del proprietario dell'8/10 sera: 85, 85, 85, poi un <paramref name="fault"/> del campo
    /// sullo stesso deposito, poi ancora 85. Il timeout e l'errore di connessione non toccano la fila: il
    /// quinto movimento e' il quarto 85 di fila, e M1 va in errore senza chiedere niente al gestionale, con
    /// la missione verso la cella 10. Prima il timeout faceva ripartire il conto, e il quarto 85 cercava
    /// ancora una cella nuova.
    /// </summary>
    [Theory]
    [InlineData(ResultCodeTypes.TimeOut)]
    [InlineData(ResultCodeTypes.ConnectionError)]
    public async Task ATimeoutBetweenTheRefusalsDoesNotStartTheCountAgain(ResultCodeTypes fault)
    {
        var (mission, step) = await AMissionOnM1WhoseCellInSGThePlcRefuses(inTheGroup: false);
        await TheStorageSGHasTheCells(6, 8, 9, 10, 11);

        await M1RefusesAndTheMissionTakes(85, step, 8, [6]);
        await M1RefusesAndTheMissionTakes(85, mission.MissionSteps[0], 9, [6, 8]);
        await M1RefusesAndTheMissionTakes(85, mission.MissionSteps[0], 10, [6, 8, 9]);

        var relocator = _plant["M1"];
        var timedOut = mission.MissionSteps[0];
        relocator.RunningStepId = timedOut.StepId;
        await relocator.ConnectionAsiMovementIsCompleted(new AsiMovementIsCompletedEventArgs(
            relocator.ItemGuid, timedOut.StepId, fault, 0, string.Empty, null))
            .WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(fault, timedOut.StepStatus);
        Assert.Equal(3, relocator.RefusedDepositsInARow);

        // Il ritentativo riparte dal dispositivo pronto: lo stato di errore qui sotto e' del quarto 85.
        relocator.Status = DeviceStatusTypes.Ready;
        TheWarehouseHasCell(11);
        var asked = _warehouse.StorageRequests.Count;
        var fourth = mission.MissionSteps[0];
        await ThePlcRefusesTheDeposit(relocator, fourth, 85);

        Assert.Equal(asked, _warehouse.StorageRequests.Count);
        Assert.Equal(4, relocator.RefusedDepositsInARow);
        TheDeviceWaitsInErrorWithTheOperatorsMission(relocator, mission, fourth, "A10");
    }

    /// <summary>
    /// Risposta 9 del proprietario dell'8/10 sera: il 67 («cella piena») sul deposito di M1, dispositivo
    /// singolo o membro del gruppo G, blocca la cella 6 come piena nel gestionale, come fa il gruppo; la
    /// missione dell'operatore prende poi la cella 8. Prima il 67 bloccava la cella solo sul gruppo.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Error67OnASingleDeviceOrAMemberLocksTheCellAsFull(bool inTheGroup)
    {
        var (mission, step) = await AMissionOnM1WhoseCellInSGThePlcRefuses(inTheGroup);

        await ThePlcRefusesTheDeposit(_plant["M1"], step, 67);

        Assert.Equal([(StorageDoorCode, CellLockState.Full)], _warehouse.CellLockStateChanges);
        TheOperatorsMissionHasCell8(mission, step);
        NoStepStartIsLeftCounted(mission);
    }

    /// <summary>
    /// Risposta 7 del proprietario dell'8/10 sera, per la missione dell'operatore e per quella della
    /// configurazione sul dispositivo singolo M1: il PLC rifiuta il deposito nella cella 6 con
    /// <paramref name="errorCode"/>, e il gestionale non risponde (alla domanda della cella nuova, o per il
    /// 67 gia' al blocco della cella piena). M1 va in errore e aspetta, e la missione lascia la porta
    /// rifiutata A6: niente porta, indirizzo ne' rotta, la cella persa, in coda al navigatore. Il
    /// gestionale torna, l'operatore rimette in marcia M1 (il conto riparte), e il navigatore le cerca la
    /// cella prima del deposito: la domanda esclude ancora la 6, la missione prende la 8 (porta A8), con la
    /// rotta verso di lei. Nessun deposito torna verso la cella rifiutata. Prima la missione teneva la porta
    /// A6 e lo step verso di lei, e alla ripartenza ci depositava di nuovo.
    /// </summary>
    [Theory]
    [InlineData(true, 85)]
    [InlineData(true, 67)]
    [InlineData(false, 85)]
    public async Task AfterTheWarehouseWasSilentTheMissionLeavesTheRefusedCellAndTakesAnotherOne(bool operatorsMission,
        int errorCode)
    {
        var (mission, step) = operatorsMission
            ? await AMissionOnM1WhoseCellInSGThePlcRefuses(inTheGroup: false)
            : await AnUnmarkedMissionOnTheSingleDeviceM1WhoseCellInSGThePlcRefuses();
        _warehouse.UnavailableOperations.Add(errorCode == 67
            ? nameof(IWarehouse.SetCellLockStateAsync)
            : nameof(IWarehouse.ReserveStorageAsync));

        await ThePlcRefusesTheDeposit(_plant["M1"], step, errorCode);

        Assert.Equal(DeviceStatusTypes.Warning, _plant["M1"].Status);
        Assert.Equal(MissionStatusTypes.Error, mission.Status);
        Assert.Equal(ResultCodeTypes.Error, step.StepStatus);
        TheMissionLeftTheRefusedDoor(mission);
        Assert.Equal(_plant["SG"].ItemGuid, mission.DestinationItemId);
        Assert.Equal(operatorsMission, mission.OperatorDestination);
        Assert.NotEqual(MissionTypes.Rejection, mission.MissionType);

        _warehouse.UnavailableOperations.Clear();
        await ExecutableCommands.ItemExecuteStep(new Command { ItemCommandId = _plant["M1"].ItemGuid })
            .WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0, _plant["M1"].RefusedDepositsInARow);

        var asked = _warehouse.StorageRequests.Count;
        await TheRouteLoopLooksForTheCell(mission);

        Assert.Equal(OtherDoorCode, mission.StorageDoorCode);
        Assert.False(mission.StorageCellLost);
        Assert.Equal(_plant["SG"].ItemGuid, mission.DestinationItemId);
        Assert.Equal(operatorsMission ? ["M1->X", "X->SG"] : ["M1->SG"], StepsOf(mission));
        Assert.Equal(OtherCellId, _warehouse.StorageReservationsByMission[mission.MissionId].CellId);

        var requests = _warehouse.StorageRequests.Skip(asked).ToList();
        Assert.NotEmpty(requests);
        Assert.All(requests, request => Assert.Equal([StorageCellId], request.ExcludedCellIds));
    }

    /// <summary>
    /// Risposta 7 del proprietario dell'8/10 sera, quando la missione resta sulla porta rifiutata: il PLC
    /// rifiuta con l'85 il deposito della missione dell'operatore di M1 nella cella 6, mentre un'altra
    /// ricerca tiene il posto delle ricerche della missione. Adesso non si cerca: M1 va in errore, e la
    /// missione tiene la porta A6, che il gestionale le tiene ancora. Poi il navigatore guarda la cella
    /// prima del deposito: la 6 e' esclusa finche' l'UDC non deposita, e non la conferma anche se il
    /// gestionale la tiene. La missione prende la 8 (porta A8), con la 6 esclusa dalla domanda. Prima la
    /// conferma passava, e il deposito tornava nella cella rifiutata (revisione finale del terzo giro).
    /// </summary>
    [Fact]
    public async Task TheNavigatorDoesNotConfirmACellThePlcRefusedEvenIfTheWarehouseStillHoldsIt()
    {
        var (mission, step) = await AMissionOnM1WhoseCellInSGThePlcRefuses(inTheGroup: false);
        var search = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(Navigator.TryClaimCellSearch(mission.MissionId, search.Task));

        try
        {
            await ThePlcRefusesTheDeposit(_plant["M1"], step, 85);
        }
        finally
        {
            Navigator.ReleaseCellSearchClaim(mission.MissionId, search.Task);
            search.TrySetResult();
        }

        TheDeviceWaitsInErrorWithTheOperatorsMission(_plant["M1"], mission, step, StorageDoorCode);
        Assert.Equal(StorageCellId, _warehouse.StorageReservationsByMission[mission.MissionId].CellId);
        Assert.Equal([StorageCellId], mission.CellsRefusedByThePlc);

        await ExecutableCommands.ItemExecuteStep(new Command { ItemCommandId = _plant["M1"].ItemGuid })
            .WaitAsync(TimeSpan.FromSeconds(30));
        var asked = _warehouse.StorageRequests.Count;
        await TheRouteLoopLooksForTheCell(mission);

        Assert.Equal(OtherDoorCode, mission.StorageDoorCode);
        Assert.False(mission.StorageCellLost);
        Assert.Equal(["M1->X", "X->SG"], StepsOf(mission));
        Assert.Equal(OtherCellId, _warehouse.StorageReservationsByMission[mission.MissionId].CellId);
        var requests = _warehouse.StorageRequests.Skip(asked).ToList();
        Assert.NotEmpty(requests);
        Assert.All(requests, request => Assert.Equal([StorageCellId], request.ExcludedCellIds));
    }

    /// <summary>
    /// Risposta 8 del proprietario dell'8/10 sera: sul gruppo G tre UDC, la missione dell'operatore su M1
    /// e due missioni della configurazione su M2 e su M3, con il gateway X e il loro step verso SG. Il PLC
    /// rifiuta con l'85 il deposito della sola missione di M1. La missione di M3, con la porta A9, non si
    /// tocca: tiene porta, gateway e lo stesso step, e al gestionale non si chiede niente per lei, sia
    /// quando la missione di M1 trova la cella 8, sia quando il gestionale non risponde
    /// (<paramref name="silent"/>; allora la missione di M1 lascia la porta rifiutata, e il gruppo va in
    /// errore). Quella di M2 ha la porta A6 della missione di M1 senza una prenotazione sua: e' una sua
    /// copia, e tutte e due le volte la cella 6 non e' piu' di quella. Tiene il gateway e non chiede niente
    /// al gestionale, ma lascia la porta come copia (punto 3 dell'8/10 sera, primo giro di correzioni del
    /// compito 15; fino ad allora la teneva). Prima la ricerca del gruppo azzerava tutte le missioni a
    /// bordo prima di sapere, e con il gestionale muto le altre restavano senza rotta e senza gateway.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnTheGroupOnlyTheRefusedMissionIsTouched(bool silent)
    {
        var (mission, step) = await AMissionOnM1WhoseCellInSGThePlcRefuses();
        var second = await AnUnmarkedMissionOnM2ThroughX();
        var third = await AnUnmarkedMissionOnM3ThroughX();
        var secondStep = Assert.Single(second.MissionSteps);
        var thirdStep = Assert.Single(third.MissionSteps);
        Assert.Equal(3, _plant["G"].LUsLoaded.Count);
        if (silent) _warehouse.UnavailableOperations.Add(nameof(IWarehouse.ReserveStorageAsync));

        await ThePlcRefusesTheDeposit(_plant["G"], step, 85);

        if (silent)
        {
            Assert.Equal(DeviceStatusTypes.Warning, _plant["G"].Status);
            TheMissionLeftTheRefusedDoor(mission);
        }
        else
        {
            Assert.Equal(DeviceStatusTypes.Ready, _plant["G"].Status);
            TheOperatorsMissionHasCell8(mission, step);
        }

        Assert.Equal(_plant["SG"].ItemGuid, third.DestinationItemId);
        Assert.Equal("A9", third.StorageDoorCode);
        Assert.Equal(_plant["X"].ItemGuid, third.Gateway);
        Assert.Same(thirdStep, Assert.Single(third.MissionSteps));
        Assert.NotEqual(ResultCodeTypes.Error, thirdStep.StepStatus);
        Assert.DoesNotContain(_warehouse.StorageRequests, request => request.MissionId == third.MissionId);
        NoStepStartIsLeftCounted(third);

        // La missione di M2 ha la porta A6 della missione di M1 senza una prenotazione sua: e' una sua
        // copia, e la cella 6 non e' piu' di quella (punto 3 dell'8/10 sera). Tiene il gateway, e lascia la
        // porta come copia, senza domande al gestionale (primo giro di correzioni del compito 15).
        Assert.Equal(_plant["X"].ItemGuid, second.Gateway);
        TheCopyLeftTheDoor(second);
        Assert.DoesNotContain(secondStep, MissionSteps.Instance.List);
        NoStepStartIsLeftCounted(second);

        NoStepStartIsLeftCounted(mission);
    }

    /// <summary>
    /// Punto 3 dell'8/10 sera, con la risposta 8: sul gruppo G la missione della configurazione su M2 ha la
    /// cella 6 prenotata per lei (porta A6), e quella su M3, dello stesso tipo di UDC, ne ha copiato la
    /// porta, in un altro movimento. Il PLC rifiuta con l'85 il deposito della sola missione di M2, che
    /// prende la cella 9. La copia di M3, che il PLC non ha rifiutato, lascia la porta A6 come copia:
    /// niente porta ne' rotta, senza la cella persa, senza tornare al navigatore e senza una domanda al
    /// gestionale per lei; il giro del gruppo la rifa' con la porta A9 della missione di M2, senza una
    /// prenotazione sua. Prima del correttivo la ricerca dopo il rifiuto toccava solo la missione di M2, e
    /// la copia restava con la porta A6 e lo step verso SG: avrebbe depositato da sola nella cella che il
    /// PLC aveva appena rifiutato e che nessuna missione teneva piu'.
    /// </summary>
    [Fact]
    public async Task ACopyInAnotherMovementLeavesTheRefusedDoorWhenItsMissionTakesAnotherCell()
    {
        var (lender, copy) = await AMissionOnM2AndACopyOfItsDoorOnM3();

        await ThePlcRefusesTheDeposit(_plant["M2"], Assert.Single(lender.MissionSteps), 85);

        Assert.Equal("A9", lender.StorageDoorCode);
        Assert.Equal(["M2->X", "X->SG"], StepsOf(lender));
        Assert.Equal(9m, _warehouse.StorageReservationsByMission[lender.MissionId].CellId);
        TheCopyLeftTheDoor(copy);
        NoStepStartIsLeftCounted(copy);
        NoStepStartIsLeftCounted(lender);

        await _plant["G"].VerifyWhereAllocateLuGrouped(_plant["G"].LUsLoaded).WaitAsync(TimeSpan.FromSeconds(30));

        TheCopyGoesWithTheMissionIntoCell9(copy);
    }

    /// <summary>
    /// La stessa scena, ma il gestionale non ha un'altra cella per la missione di M2, che va allo scarto R
    /// per mancanza di celle. La copia di M3 lascia la porta A6 come copia, perche' la missione non ha piu'
    /// quella cella. Prima del correttivo restava
    /// verso la cella rifiutata, che nessuna missione teneva piu'.
    /// </summary>
    [Fact]
    public async Task ACopyInAnotherMovementLeavesTheRefusedDoorWhenItsMissionIsRejected()
    {
        var (lender, copy) = await AMissionOnM2AndACopyOfItsDoorOnM3();
        _plant.Link("M2", "R");
        _warehouse.StorageReservations[43] = new StorageReservation(Guid.Empty,
            StorageReservationOutcome.NoFreeCells, 0, string.Empty, string.Empty, 0, NeedsCompaction: false);

        await ThePlcRefusesTheDeposit(_plant["M2"], Assert.Single(lender.MissionSteps), 85);

        Assert.Equal(_plant["R"].ItemGuid, lender.DestinationItemId);
        Assert.Equal(MissionTypes.Rejection, lender.MissionType);
        Assert.Null(copy.StorageDoorCode);
        Assert.Empty(copy.MissionSteps);
        Assert.True(copy.LeftACopiedDoor);
        Assert.False(copy.StorageCellLost);
        Assert.Null(RoutesToBuild.Instance[copy.MissionId]);
        NoStepStartIsLeftCounted(copy);
    }

    /// <summary>
    /// La scena della prova qui sopra con la cella 9, ma la scrittura del cambio della copia, che lascia la
    /// porta A6 dopo la ricerca del gruppo, solleva (il gestore degli eventi del motore). La copia riparte lo
    /// stesso: un cambio la puo' fermare di nuovo. Prima un'eccezione mentre una copia lasciava la porta la
    /// lasciava ferma per sempre, con le copie delle porte che venivano dopo (revisione finale del terzo giro).
    /// </summary>
    [Fact]
    public async Task AFailureWhileACopyLeavesTheRefusedDoorLeavesNoCopyStopped()
    {
        var (lender, copy) = await AMissionOnM2AndACopyOfItsDoorOnM3();
        Navigator.EventSink = new ThrowsWhenTheMissionChanges(copy);

        try
        {
            await Record.ExceptionAsync(() =>
                ThePlcRefusesTheDeposit(_plant["M2"], Assert.Single(lender.MissionSteps), 85));
        }
        finally
        {
            Navigator.EventSink = null;
        }

        Assert.Equal("A9", lender.StorageDoorCode);
        Assert.Null(copy.StorageDoorCode);
        NoStepStartIsLeftCounted(copy);
        NoStepStartIsLeftCounted(lender);
    }

    /// <summary>
    /// Punto 3 dell'8/10 sera dopo un cambio del solo gateway (primo giro di correzioni del compito 15):
    /// l'operatore toglie il gateway X alla missione di M2, che tiene la cella 6 (porta A6) con il segno
    /// dell'operatore e la rotta M2->SG, e la copia di M3 tiene la porta A6, come vuole il cambio del solo
    /// gateway. Poi il PLC rifiuta con l'85 il deposito della missione di M2, da sola: il gestionale le da'
    /// la cella 9, o non risponde (<paramref name="silent"/>) e lei lascia la porta rifiutata come una
    /// cella persa. Tutte e due le volte la cella 6 non e' piu' sua, e la copia lascia la porta A6 come
    /// copia. Prima le vie del rifiuto del PLC guardavano solo chi presta la porta alle copie nuove, che
    /// con il segno lei non e' piu': la copia teneva la porta A6, e depositava da sola nella cella che il
    /// PLC aveva appena rifiutato.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AfterAGatewayOnlyChangeACopyLeavesTheDoorThePlcRefusesToItsMission(bool silent)
    {
        var (lender, copy) = await AMissionOnM2AndACopyOfItsDoorOnM3();
        Assert.Equal(MissionDestinationOutcome.Changed,
            (await MissionGateway.ChangeDestinationAsync(lender.MissionId, _plant["SG"].ItemGuid,
                MissionGatewayChoice.Clear)).Outcome);
        Assert.True(lender.OperatorDestination);
        Assert.Equal(StorageDoorCode, lender.StorageDoorCode);
        Assert.Equal(["M2->SG"], StepsOf(lender));
        Assert.Equal(StorageDoorCode, copy.StorageDoorCode);
        Assert.Equal(["M3->SG"], StepsOf(copy));
        _warehouse.Calls.Clear();
        if (silent) _warehouse.UnavailableOperations.Add(nameof(IWarehouse.ReserveStorageAsync));

        await ThePlcRefusesTheDeposit(_plant["M2"], Assert.Single(lender.MissionSteps), 85);

        if (silent)
            TheMissionLeftTheRefusedDoor(lender);
        else
            Assert.Equal("A9", lender.StorageDoorCode);

        TheCopyLeftTheDoor(copy);
        NoStepStartIsLeftCounted(copy);
        NoStepStartIsLeftCounted(lender);
    }

    /// <summary>
    /// La stessa scena dopo il cambio del solo gateway, ma il gestionale non ha un'altra cella per la
    /// missione di M2 e il gruppo non ha un punto di scarto: M2 va in errore e aspetta l'operatore, e la
    /// missione tiene la porta A6, ancora prenotata per lei. Allora la copia di M3 tiene la porta, lo step
    /// M3->SG e nessun segno, come con una missione della configurazione (la regola di
    /// <c>MissionGateway.LeaveTheDoorsThatChanged</c>): con il segno dell'operatore la cella resta sua lo
    /// stesso. Guardando chi presta la porta alle copie nuove, la copia la lasciava anche qui, con la
    /// missione che teneva la cella (primo giro di correzioni del compito 15).
    /// </summary>
    [Fact]
    public async Task AfterAGatewayOnlyChangeACopyKeepsTheDoorItsRefusedMissionKeeps()
    {
        var (lender, copy) = await AMissionOnM2AndACopyOfItsDoorOnM3();
        var copyStep = Assert.Single(copy.MissionSteps);
        Assert.Equal(MissionDestinationOutcome.Changed,
            (await MissionGateway.ChangeDestinationAsync(lender.MissionId, _plant["SG"].ItemGuid,
                MissionGatewayChoice.Clear)).Outcome);
        Assert.True(lender.OperatorDestination);
        _plant["G"].RejectItemDestinationAllocateLu = Guid.Empty;
        _warehouse.StorageReservations[43] = new StorageReservation(Guid.Empty,
            StorageReservationOutcome.NoFreeCells, 0, string.Empty, string.Empty, 0, NeedsCompaction: false);
        _warehouse.Calls.Clear();

        await ThePlcRefusesTheDeposit(_plant["M2"], Assert.Single(lender.MissionSteps), 85);

        Assert.Equal(DeviceStatusTypes.Warning, _plant["M2"].Status);
        Assert.Equal(StorageDoorCode, lender.StorageDoorCode);
        Assert.Equal(StorageDoorCode, copy.StorageDoorCode);
        Assert.Same(copyStep, Assert.Single(copy.MissionSteps));
        Assert.False(copy.LeftACopiedDoor);
        NoStepStartIsLeftCounted(copy);
        NoStepStartIsLeftCounted(lender);
    }

    /// <summary>
    /// Risposta 7 dell'8/10 sera con le copie (risposta 8 dell'8/10, punto 3 dell'8/10 sera): la stessa
    /// scena, ma il gestionale non risponde alla domanda della cella nuova. Il PLC rifiuta con l'85 il
    /// deposito della missione di M2 da sola (su M2), o il movimento del gruppo con le due missioni
    /// (<paramref name="sameMovement"/>). La missione di M2 lascia la porta rifiutata come una cella persa,
    /// e torna al navigatore; la copia la lascia come copia: senza la cella persa, senza tornare al
    /// navigatore. Il gestionale torna: il giro del gruppo che arriva prima del navigatore non rifa' la
    /// copia (nessuna domanda al gestionale per lei), perche' la missione del suo tipo aspetta la cella dal
    /// navigatore; il navigatore le prenota la 9, esclusa la 6; scaduto il freno, il giro del gruppo rimette
    /// la copia con lei nella cella 9, senza una prenotazione sua. Prima la copia nello stesso movimento
    /// perdeva la cella come se fosse sua, e il navigatore le prenotava una cella sua; quella di un altro
    /// movimento teneva la porta A6, che nessuna missione teneva piu'.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WithTheWarehouseSilentTheCopyLeavesTheRefusedDoorWithItsMission(bool sameMovement)
    {
        var (lender, copy) = await AMissionOnM2AndACopyOfItsDoorOnM3();
        var group = _plant["G"];
        var clock = new ManualClock();
        group.GroupManager!.CopiedDoorRetries = new RetryBackoff(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30), clock);
        _warehouse.UnavailableOperations.Add(nameof(IWarehouse.ReserveStorageAsync));

        if (sameMovement)
        {
            var token = Guid.NewGuid();
            Assert.Single(lender.MissionSteps).GroupedMovementId = token;
            Assert.Single(copy.MissionSteps).GroupedMovementId = token;
            group.RunningStepId = token;
            await group.ConnectionAsiMovementIsCompleted(new AsiMovementIsCompletedEventArgs(
                group.ItemGuid, token, ResultCodeTypes.Error, 85, "deposito rifiutato", null))
                .WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(DeviceStatusTypes.Warning, group.Status);
        }
        else
        {
            await ThePlcRefusesTheDeposit(_plant["M2"], Assert.Single(lender.MissionSteps), 85);
            Assert.Equal(DeviceStatusTypes.Warning, _plant["M2"].Status);
        }

        TheMissionLeftTheRefusedDoor(lender);
        TheCopyLeftTheDoor(copy);
        NoStepStartIsLeftCounted(copy);

        _warehouse.UnavailableOperations.Clear();
        await group.VerifyWhereAllocateLuGrouped(group.LUsLoaded).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Null(copy.StorageDoorCode);
        Assert.True(copy.LeftACopiedDoor);
        Assert.DoesNotContain(_warehouse.StorageRequests, request => request.MissionId == copy.MissionId);

        var asked = _warehouse.StorageRequests.Count;
        await TheRouteLoopLooksForTheCell(lender);

        Assert.Equal("A9", lender.StorageDoorCode);
        Assert.False(lender.StorageCellLost);
        Assert.Equal(9m, _warehouse.StorageReservationsByMission[lender.MissionId].CellId);
        var requests = _warehouse.StorageRequests.Skip(asked).ToList();
        Assert.NotEmpty(requests);
        Assert.All(requests, request => Assert.Equal([StorageCellId], request.ExcludedCellIds));

        clock.Advance(TimeSpan.FromSeconds(2));
        await group.VerifyWhereAllocateLuGrouped(group.LUsLoaded).WaitAsync(TimeSpan.FromSeconds(30));

        TheCopyGoesWithTheMissionIntoCell9(copy);
    }

    /// <summary>
    /// Una copia rifiutata dal PLC con il gestionale muto, la cui missione ha gia' un'altra cella (la 9,
    /// porta A9): la copia lascia la porta rifiutata A6 come copia, non come una cella persa sua - non
    /// torna al navigatore, che le prenoterebbe una cella sua - e il giro del gruppo la rimette con la
    /// missione nella cella 9. Prima la copia perdeva la cella come se fosse sua.
    /// </summary>
    [Fact]
    public async Task ARefusedCopyLeavesTheDoorAsACopyWhenItsMissionHasAnotherCell()
    {
        var (lender, copy) = await AMissionOnM2AndACopyOfItsDoorOnM3();
        lender.StorageDoorCode = "A9";
        lender.StorageCellReserved = true;
        _warehouse.StorageReservationsByMission[lender.MissionId] = new StorageReservation(lender.MissionId,
            StorageReservationOutcome.CellFound, 9, "A9", Partition: "0093", StorageDepth: 200, NeedsCompaction: false);

        Assert.True(MissionGateway.LeaveTheRefusedCell(copy, StorageDoorCode, "prova", [copy]));

        TheCopyLeftTheDoor(copy);
        NoStepStartIsLeftCounted(copy);

        await _plant["G"].VerifyWhereAllocateLuGrouped(_plant["G"].LUsLoaded).WaitAsync(TimeSpan.FromSeconds(30));

        TheCopyGoesWithTheMissionIntoCell9(copy);
    }

    /// <summary>
    /// Risposta 7 dell'8/10 sera, con la copia rifiutata da sola: sul gruppo G la missione della
    /// configurazione di M2 ha la cella 6 prenotata per lei (porta A6), e la copia di M3 ne ha copiato la
    /// porta. Il PLC rifiuta con l'<paramref name="errorCode"/> il deposito della sola copia, in un altro
    /// movimento, e il gestionale non risponde (alla domanda della cella nuova, o per il 67 gia' al
    /// blocco). M3 va in errore; la missione di M2, non rifiutata, tiene porta A6, prenotazione e step
    /// (risposta 8); la copia lascia la porta A6 come una cella persa sua - niente porta ne' rotta, in
    /// coda al navigatore, senza il segno della copia - perche' con la missione di M2 tornerebbe nella
    /// cella 6. Il gestionale torna, e il navigatore le prenota la 9 (porta A9), con la 6 esclusa. Prima la
    /// copia restava con la missione di M2 con la porta A6, e alla ripartenza dell'operatore ci depositava
    /// di nuovo, nella cella che il PLC le aveva appena rifiutato.
    /// </summary>
    [Theory]
    [InlineData(85)]
    [InlineData(67)]
    public async Task WithTheWarehouseSilentARefusedCopyLeavesTheDoorItsUnrefusedMissionKeeps(int errorCode)
    {
        var (lender, copy) = await AMissionOnM2AndACopyOfItsDoorOnM3();
        var lenderStep = Assert.Single(lender.MissionSteps);
        _plant["M3"].LinkedCellId = 9;
        _warehouse.StorageReservations[44] = new StorageReservation(Guid.Empty,
            StorageReservationOutcome.CellFound, 9, "A9", Partition: "0093", StorageDepth: 200, NeedsCompaction: false);
        _warehouse.UnavailableOperations.Add(errorCode == 67
            ? nameof(IWarehouse.SetCellLockStateAsync)
            : nameof(IWarehouse.ReserveStorageAsync));

        await ThePlcRefusesTheDeposit(_plant["M3"], Assert.Single(copy.MissionSteps), errorCode);

        Assert.Equal(DeviceStatusTypes.Warning, _plant["M3"].Status);
        TheMissionLeftTheRefusedDoor(copy);
        Assert.False(copy.LeftACopiedDoor);
        NoStepStartIsLeftCounted(copy);

        Assert.Equal(StorageDoorCode, lender.StorageDoorCode);
        Assert.True(lender.StorageCellReserved);
        Assert.False(lender.StorageCellLost);
        Assert.Same(lenderStep, Assert.Single(lender.MissionSteps));
        Assert.Null(RoutesToBuild.Instance[lender.MissionId]);
        NoStepStartIsLeftCounted(lender);

        _warehouse.UnavailableOperations.Clear();
        var asked = _warehouse.StorageRequests.Count;
        await TheRouteLoopLooksForTheCell(copy);

        Assert.Equal("A9", copy.StorageDoorCode);
        Assert.False(copy.StorageCellLost);
        Assert.Equal(9m, _warehouse.StorageReservationsByMission[copy.MissionId].CellId);
        var requests = _warehouse.StorageRequests.Skip(asked).ToList();
        Assert.NotEmpty(requests);
        Assert.All(requests, request => Assert.Equal([StorageCellId], request.ExcludedCellIds));
        Assert.Equal(StorageDoorCode, lender.StorageDoorCode);
    }

    /// <summary>
    /// Come la prova qui sopra, ma il gestionale torna e il ciclo del gruppo, con la modalita' per tipo di
    /// UDC, arriva prima del navigatore: la missione di M2 tiene la cella 6 (porta A6) e ne da' il
    /// selettore al suo tipo, e la copia, che ha perso la cella come se fosse sua, non lo prende - resta
    /// senza porta ne' rotta, con la cella persa, in coda al navigatore, senza nessuna domanda al
    /// gestionale per lei. Poi il navigatore le prenota la 9 (porta A9), con la 6 esclusa. Prima la copia
    /// prendeva dal giro del gruppo la porta A6 e la rotta verso di lei, e alla ripartenza dell'operatore
    /// depositava di nuovo nella cella che il PLC le aveva appena rifiutato (terzo giro di correzioni del
    /// compito 13).
    /// </summary>
    [Theory]
    [InlineData(85)]
    [InlineData(67)]
    public async Task ARefusedCopyThatLostTheCellDoesNotTakeTheDoorOfItsTypeFromTheGroupCycle(int errorCode)
    {
        var (lender, copy) = await AMissionOnM2AndACopyOfItsDoorOnM3();
        _plant["M3"].LinkedCellId = 9;
        _warehouse.StorageReservations[44] = new StorageReservation(Guid.Empty,
            StorageReservationOutcome.CellFound, 9, "A9", Partition: "0093", StorageDepth: 200, NeedsCompaction: false);
        _warehouse.UnavailableOperations.Add(errorCode == 67
            ? nameof(IWarehouse.SetCellLockStateAsync)
            : nameof(IWarehouse.ReserveStorageAsync));

        await ThePlcRefusesTheDeposit(_plant["M3"], Assert.Single(copy.MissionSteps), errorCode);

        TheMissionLeftTheRefusedDoor(copy);
        Assert.Equal(StorageDoorCode, lender.StorageDoorCode);

        _warehouse.UnavailableOperations.Clear();
        _plant["G"].LookWhereAllocateLu = LookWhereAllocateLuTypes.BuildRouteToStoreByIdLuType;
        var askedBeforeTheGroup = _warehouse.StorageRequests.Count;
        await _plant["G"].VerifyWhereAllocateLuGrouped(_plant["G"].LUsLoaded).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(StorageDoorCode, lender.StorageDoorCode);
        Assert.True(lender.StorageCellReserved);
        TheMissionLeftTheRefusedDoor(copy);
        Assert.False(copy.LeftACopiedDoor);
        Assert.DoesNotContain(_warehouse.StorageRequests.Skip(askedBeforeTheGroup),
            request => request.MissionId == copy.MissionId);
        NoStepStartIsLeftCounted(copy);

        var asked = _warehouse.StorageRequests.Count;
        await TheRouteLoopLooksForTheCell(copy);

        Assert.Equal("A9", copy.StorageDoorCode);
        Assert.False(copy.StorageCellLost);
        Assert.Equal(9m, _warehouse.StorageReservationsByMission[copy.MissionId].CellId);
        var requests = _warehouse.StorageRequests.Skip(asked).ToList();
        Assert.NotEmpty(requests);
        Assert.All(requests, request => Assert.Equal([StorageCellId], request.ExcludedCellIds));
    }

    /// <summary>
    /// La stessa scena, ma dopo il ritorno del gestionale arriva il comando manuale della ricerca delle
    /// celle del gruppo (<c>ItemFindStoringLocation</c>), con la modalita' per tipo di UDC, invece del giro
    /// del gruppo. La missione di M2 tiene la cella 6 (porta A6) e ne da' il selettore al suo tipo; la
    /// copia, con la cella persa, non lo prende e non si cerca: resta senza porta ne' rotta, in coda al
    /// navigatore, senza domande al gestionale per lei, e il navigatore le prenota poi la 9, con la 6
    /// esclusa. Prima il comando manuale le dava la porta A6, e alla ripartenza dell'operatore depositava
    /// di nuovo nella cella che il PLC le aveva appena rifiutato (revisione finale del terzo giro: il terzo
    /// giro di correzioni del compito 13 l'aveva tolto solo dal giro del gruppo).
    /// </summary>
    [Fact]
    public async Task ARefusedCopyThatLostTheCellDoesNotTakeTheDoorOfItsTypeFromTheManualSearch()
    {
        var (lender, copy) = await AMissionOnM2AndACopyOfItsDoorOnM3();
        _plant["M3"].LinkedCellId = 9;
        _warehouse.StorageReservations[44] = new StorageReservation(Guid.Empty,
            StorageReservationOutcome.CellFound, 9, "A9", Partition: "0093", StorageDepth: 200, NeedsCompaction: false);
        _warehouse.UnavailableOperations.Add(nameof(IWarehouse.ReserveStorageAsync));

        await ThePlcRefusesTheDeposit(_plant["M3"], Assert.Single(copy.MissionSteps), 85);

        TheMissionLeftTheRefusedDoor(copy);
        Assert.Equal(StorageDoorCode, lender.StorageDoorCode);

        _warehouse.UnavailableOperations.Clear();
        _plant["G"].LookWhereAllocateLu = LookWhereAllocateLuTypes.BuildRouteToStoreByIdLuType;
        var askedBeforeTheSearch = _warehouse.StorageRequests.Count;
        await _plant["G"].StorageManager!.FindNewStoreLocationGroupedForTest().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(StorageDoorCode, lender.StorageDoorCode);
        TheMissionLeftTheRefusedDoor(copy);
        Assert.False(copy.LeftACopiedDoor);
        Assert.DoesNotContain(_warehouse.StorageRequests.Skip(askedBeforeTheSearch),
            request => request.MissionId == copy.MissionId);
        NoStepStartIsLeftCounted(copy);

        var asked = _warehouse.StorageRequests.Count;
        await TheRouteLoopLooksForTheCell(copy);

        Assert.Equal("A9", copy.StorageDoorCode);
        Assert.False(copy.StorageCellLost);
        var requests = _warehouse.StorageRequests.Skip(asked).ToList();
        Assert.NotEmpty(requests);
        Assert.All(requests, request => Assert.Equal([StorageCellId], request.ExcludedCellIds));
    }

    /// <summary>
    /// Una copia rifiutata dal PLC con il gestionale muto, mentre la missione da cui ha copiato la porta A6,
    /// rifiutata con lei, la tiene ancora, perche' un cambio dell'operatore la ferma e lei non puo' lasciarla: la copia resta
    /// con lei - porta A6, step, niente cella persa, niente segno della copia, non in coda al navigatore - e
    /// la porta la decide la missione. Prima la copia perdeva la cella come se fosse sua, e il navigatore le
    /// prenotava una cella sua, separata dalla missione con cui deposita.
    /// </summary>
    [Fact]
    public async Task ARefusedCopyStaysWithItsMissionWhileTheMissionKeepsTheDoor()
    {
        var (lender, copy) = await AMissionOnM2AndACopyOfItsDoorOnM3();
        var copyStep = Assert.Single(copy.MissionSteps);
        Assert.Equal(PathMission.OperatorChangeStart.Begun, lender.TryBeginOperatorChange());

        try
        {
            Assert.False(MissionGateway.LeaveTheRefusedCell(copy, StorageDoorCode, "prova", [lender, copy]));
            Assert.False(MissionGateway.LeaveTheRefusedCell(lender, StorageDoorCode, "prova", [lender, copy]));
        }
        finally
        {
            lender.EndOperatorChange();
        }

        foreach (var mission in new[] { lender, copy })
        {
            Assert.Equal(StorageDoorCode, mission.StorageDoorCode);
            Assert.False(mission.StorageCellLost);
            Assert.False(mission.LeftACopiedDoor);
            Assert.Null(RoutesToBuild.Instance[mission.MissionId]);
            NoStepStartIsLeftCounted(mission);
        }

        Assert.Same(copyStep, Assert.Single(copy.MissionSteps));
    }

    /// <summary>
    /// Risposta 7 dell'8/10 sera con la copia rifiutata insieme alla missione da cui ha copiato la porta A6,
    /// nei due ordini (<paramref name="copyFirst"/>), con il gestionale muto. Prima la copia: la missione
    /// tiene ancora la porta ed e' rifiutata con lei, e la copia resta; poi la missione la lascia come una
    /// cella persa, e la fa lasciare alla copia come copia. Prima la missione: lascia la porta con la copia,
    /// e la copia non ha piu' niente da lasciare. Nei due ordini la missione ha lasciato la porta rifiutata
    /// ed e' in coda al navigatore, e la copia l'ha lasciata come copia, senza la cella persa. Il gestore del
    /// rifiuto le fa passare con le copie prima; l'altro ordine lo vede solo questa prova (revisione finale
    /// del terzo giro: l'ordine veniva da un dizionario con chiavi casuali).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ACopyRefusedWithItsMissionLeavesTheDoorWithItInEitherOrder(bool copyFirst)
    {
        var (lender, copy) = await AMissionOnM2AndACopyOfItsDoorOnM3();
        PathMission[] refusedTogether = [lender, copy];

        var firstLeft = MissionGateway.LeaveTheRefusedCell(copyFirst ? copy : lender, StorageDoorCode, "prova",
            refusedTogether);
        var secondLeft = MissionGateway.LeaveTheRefusedCell(copyFirst ? lender : copy, StorageDoorCode, "prova",
            refusedTogether);

        Assert.Equal(copyFirst ? (false, true) : (true, false), (firstLeft, secondLeft));
        TheMissionLeftTheRefusedDoor(lender);
        TheCopyLeftTheDoor(copy);
        NoStepStartIsLeftCounted(lender);
        NoStepStartIsLeftCounted(copy);
    }

    /// <summary>
    /// Sul gruppo il PLC aveva rifiutato la cella 6 a due missioni, e la ricerca ha dato alla missione di M2
    /// la cella 9 (porta A9) prima che il gestionale smettesse di rispondere per l'altra: la porta di M2 non
    /// e' piu' quella rifiutata, e con il gestionale muto M2 la tiene. Niente porta tolta, nessuna cella
    /// persa, non in coda al navigatore, e la copia di M3 resta com'era (revisione finale del terzo giro:
    /// la guardia della porta cambiata non aveva una prova).
    /// </summary>
    [Fact]
    public async Task AMissionThatAlreadyHasAnotherCellKeepsItWhenTheWarehouseFallsSilent()
    {
        var (lender, copy) = await AMissionOnM2AndACopyOfItsDoorOnM3();
        lender.StorageDoorCode = "A9";
        var lenderStep = Assert.Single(lender.MissionSteps);

        Assert.False(MissionGateway.LeaveTheRefusedCell(lender, StorageDoorCode, "prova", [lender]));

        Assert.Equal("A9", lender.StorageDoorCode);
        Assert.False(lender.StorageCellLost);
        Assert.Same(lenderStep, Assert.Single(lender.MissionSteps));
        Assert.Null(RoutesToBuild.Instance[lender.MissionId]);
        Assert.Equal(StorageDoorCode, copy.StorageDoorCode);
        NoStepStartIsLeftCounted(lender);
    }

    /// <summary>
    /// Il gestionale muto dopo il rifiuto della cella 6 alla missione di M2, ma una ricerca della cella
    /// tiene il posto delle ricerche della missione: la porta rifiutata la decide quella ricerca, e qui non
    /// si tocca niente. La missione tiene la porta A6 e lo step, non torna al navigatore, e la copia di M3
    /// tiene la porta con lei. Senza la guardia la missione lasciava la porta mentre un'altra ricerca le
    /// scriveva la cella (revisione finale del terzo giro: la guardia non aveva una prova).
    /// </summary>
    [Fact]
    public async Task WhileACellSearchHoldsTheMissionItKeepsTheRefusedDoor()
    {
        var (lender, copy) = await AMissionOnM2AndACopyOfItsDoorOnM3();
        var lenderStep = Assert.Single(lender.MissionSteps);
        var copyStep = Assert.Single(copy.MissionSteps);
        var search = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(Navigator.TryClaimCellSearch(lender.MissionId, search.Task));

        bool left;
        try
        {
            left = MissionGateway.LeaveTheRefusedCell(lender, StorageDoorCode, "prova", [lender]);
        }
        finally
        {
            Navigator.ReleaseCellSearchClaim(lender.MissionId, search.Task);
            search.TrySetResult();
        }

        Assert.False(left);
        Assert.Equal(StorageDoorCode, lender.StorageDoorCode);
        Assert.False(lender.StorageCellLost);
        Assert.Same(lenderStep, Assert.Single(lender.MissionSteps));
        Assert.Null(RoutesToBuild.Instance[lender.MissionId]);
        Assert.Equal(StorageDoorCode, copy.StorageDoorCode);
        Assert.Same(copyStep, Assert.Single(copy.MissionSteps));
        NoStepStartIsLeftCounted(lender);
        NoStepStartIsLeftCounted(copy);
    }

    /// <summary>
    /// Il PLC rifiuta con l'85 il deposito della missione di M2, e la copia della sua porta, in un altro
    /// movimento su M3, non si puo' fermare: un ciclo del gruppo ne sta avviando il deposito. La ricerca non
    /// si fa, come quando una missione rifiutata non si puo' contare: M2 va in errore e aspetta, nessuna
    /// domanda al gestionale, e la missione e la copia tengono la porta A6. Senza, la missione prendeva la
    /// cella 9 mentre la copia, che nessuno teneva, restava verso la 6 e ci depositava da sola (revisione
    /// finale del terzo giro: il ramo non aveva una prova).
    /// </summary>
    [Fact]
    public async Task ACopyThatCannotBeStoppedMakesTheRefusedSearchWait()
    {
        var (lender, copy) = await AMissionOnM2AndACopyOfItsDoorOnM3();
        var copyStep = Assert.Single(copy.MissionSteps);
        Assert.True(copy.TryBeginStepStart());

        try
        {
            await ThePlcRefusesTheDeposit(_plant["M2"], Assert.Single(lender.MissionSteps), 85);
        }
        finally
        {
            copy.EndStepStart();
        }

        Assert.Equal(DeviceStatusTypes.Warning, _plant["M2"].Status);
        Assert.Equal(StorageDoorCode, lender.StorageDoorCode);
        Assert.DoesNotContain(_warehouse.StorageRequests, request => request.MissionId == lender.MissionId);
        Assert.Equal(StorageDoorCode, copy.StorageDoorCode);
        Assert.Same(copyStep, Assert.Single(copy.MissionSteps));
        Assert.False(copy.LeftACopiedDoor);
        NoStepStartIsLeftCounted(lender);
        NoStepStartIsLeftCounted(copy);
    }

    /// <summary>
    /// La regola di chi lascia la porta dopo la ricerca del rifiuto (<c>MissionGateway.LeaveTheDoorsThatChanged</c>)
    /// quando la missione rifiutata ha ancora la porta A6 ma non puo' piu' avere copie: qui non ha piu' una
    /// cella prenotata per lei, ed e' a sua volta una copia. La cella 6 non e' piu' di nessuno, e la copia di
    /// M3, ferma per la ricerca, la lascia come copia, e riparte. Oggi nessuna ricerca dopo un rifiuto lascia
    /// la porta uguale togliendo la prenotazione o il magazzino (lo scarto toglie la porta): la prova chiama
    /// la regola da sola, perche' quel ramo e' una rete senza una via che lo faccia passare (revisione finale
    /// del terzo giro).
    /// </summary>
    [Fact]
    public async Task ACopyLeavesTheDoorOfARefusedMissionThatCanNoLongerLendIt()
    {
        var (lender, copy) = await AMissionOnM2AndACopyOfItsDoorOnM3();
        Assert.Equal(PathMission.OperatorChangeStart.Begun, copy.TryBeginOperatorChange());
        lender.StorageCellReserved = false;

        MissionGateway.LeaveTheDoorsThatChanged([new MissionGateway.HeldCopies(lender, StorageDoorCode, [copy])]);

        Assert.Equal(StorageDoorCode, lender.StorageDoorCode);
        TheCopyLeftTheDoor(copy);
        NoStepStartIsLeftCounted(copy);
    }

    /// <summary>
    /// Sul gruppo G, dopo la missione dell'operatore di M1: la missione della configurazione di M2 (l'UDC
    /// 43) con la cella 6 prenotata per lei (porta A6), il gateway X e lo step M2->SG, e il gestionale che
    /// per lei ha la cella 9; e su M3 la missione della configurazione dell'UDC 44, dello stesso tipo, che
    /// ne ha copiato la porta A6 senza una prenotazione sua, con il gateway X e lo step M3->SG.
    /// </summary>
    private async Task<(PathMission Lender, PathMission Copy)> AMissionOnM2AndACopyOfItsDoorOnM3()
    {
        await AMissionOnM1WhoseCellInSGThePlcRefuses();
        var lender = await AnUnmarkedMissionOnM2ThroughX();
        _warehouse.StorageReservationsByMission[lender.MissionId] = new StorageReservation(lender.MissionId,
            StorageReservationOutcome.CellFound, StorageCellId, StorageDoorCode, Partition: "0063", StorageDepth: 150,
            NeedsCompaction: false);
        lender.StorageCellReserved = true;

        var copy = await AnUnmarkedMissionOnM3ThroughX();
        copy.StorageDoorCode = StorageDoorCode;
        copy.IdLuType = lender.IdLuType;
        Assert.False(copy.StorageCellReserved);
        Assert.Equal(["M3->SG"], StepsOf(copy));
        Assert.Equal(3, _plant["G"].LUsLoaded.Count);
        _warehouse.Calls.Clear();
        return (lender, copy);
    }

    /// <summary>
    /// La copia ha lasciato la porta A6 come copia: niente porta, indirizzo ne' rotta, il segno della copia,
    /// non la cella persa, la destinazione SG, nessuna domanda al gestionale e non in coda al navigatore.
    /// </summary>
    private void TheCopyLeftTheDoor(PathMission copy)
    {
        Assert.Null(copy.StorageDoorCode);
        Assert.Equal(string.Empty, copy.DestinationAsi);
        Assert.Empty(copy.MissionSteps);
        Assert.True(copy.LeftACopiedDoor);
        Assert.False(copy.StorageCellLost);
        Assert.Equal(_plant["SG"].ItemGuid, copy.DestinationItemId);
        Assert.Null(RoutesToBuild.Instance[copy.MissionId]);
        Assert.DoesNotContain(_warehouse.StorageRequests, request => request.MissionId == copy.MissionId);
    }

    /// <summary>
    /// Il giro del gruppo ha rimesso la copia con la missione di M2 nella cella 9: porta A9, la rotta
    /// M3->X->SG, senza il segno della copia e senza una prenotazione sua.
    /// </summary>
    private void TheCopyGoesWithTheMissionIntoCell9(PathMission copy)
    {
        Assert.Equal("A9", copy.StorageDoorCode);
        Assert.False(copy.LeftACopiedDoor);
        Assert.False(copy.StorageCellReserved);
        Assert.Equal(["M3->X", "X->SG"], StepsOf(copy));
        Assert.False(_warehouse.StorageReservationsByMission.ContainsKey(copy.MissionId));
        Assert.DoesNotContain(_warehouse.StorageRequests, request => request.MissionId == copy.MissionId);
    }

    /// <summary>
    /// L'errore 137 sul deposito del gruppo («area in errore»): nessun tentativo, il gruppo va subito in
    /// errore e aspetta l'operatore. Nessuna domanda al gestionale; la missione tiene la cella 6, il
    /// gateway X e lo step del deposito, marcato in errore. Prima il gruppo cercava nuove celle, fino a
    /// dieci volte.
    /// </summary>
    [Fact]
    public async Task AnError137OnTheDepositStopsTheGroupAtOnce()
    {
        var (mission, step) = await AMissionOnM1WhoseCellInSGThePlcRefuses();

        await ThePlcRefusesTheDeposit(_plant["G"], step, 137);

        Assert.Equal(DeviceStatusTypes.Warning, _plant["G"].Status);
        Assert.Equal(MissionStatusTypes.Error, mission.Status);
        Assert.Equal(ResultCodeTypes.Error, step.StepStatus);
        Assert.Same(step, Assert.Single(mission.MissionSteps));
        Assert.Equal(StorageDoorCode, mission.StorageDoorCode);
        Assert.Equal(_plant["X"].ItemGuid, mission.Gateway);
        Assert.Empty(_warehouse.StorageRequests);
        NoStepStartIsLeftCounted(mission);
    }

    /// <summary>
    /// L'errore 137 sul prelievo del gruppo scarico: il gruppo va subito in errore e aspetta, e lo step
    /// del prelievo resta, marcato in errore. Prima il gruppo toglieva gli step del movimento e tornava
    /// pronto, fino a dieci volte.
    /// </summary>
    [Fact]
    public async Task AnError137OnThePickUpStopsTheGroupAtOnce()
    {
        var group = _plant.Device("G", DeviceTypes.GroupOfAutomationDevices);
        var member = _plant.Device("M1");
        _plant.Device("SG", DeviceTypes.Storage);
        _plant.Link("SG", "M1");
        group.GroupItemId = 93;
        member.BelongingGroupId = group.GroupItemId;

        Assert.Equal(NavigatorStatusTypes.Recover, Navigator.NavStatus);
        var mission = new PathMission
        {
            PositionItemId = _plant["SG"].ItemGuid,
            LuId = LoadUnitId,
            MissionType = MissionTypes.Extraction,
            OperationType = OperationTypes.Outbound
        };
        ActiveMissions.Instance.Add(mission, buildRoute: false);
        await mission.PositionWork;
        mission.DestinationItemId = member.ItemGuid;
        var step = _plant.PlannedStep(mission, "SG", "M1");
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        Assert.Empty(group.LUsLoaded);

        await ThePlcRefusesTheDeposit(group, step, 137);

        Assert.Equal(DeviceStatusTypes.Warning, group.Status);
        Assert.Equal(ResultCodeTypes.Error, step.StepStatus);
        Assert.Contains(step, MissionSteps.Instance.List);
        Assert.Equal(MissionStatusTypes.Error, mission.Status);
    }

    // ---------------------------------------------------------------------------------------
    // La conferma della cella cade con l'errore (risposta 6 del proprietario dell'8/10)
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Il navigatore conferma la cella 6 della missione dell'operatore su M1, membro del gruppo G, con
    /// lo step M1->SG del deposito; poi G va in errore e aspetta (il 137 sul deposito,
    /// <c>StepErrorGroup</c>), e l'operatore lo rimette in marcia. Il giro del gruppo non deposita con la
    /// conferma di prima: la missione torna al navigatore, che richiede al gestionale. Senza l'errore lo
    /// stesso giro deposita con la conferma: la conferma c'era. Prima sopravviveva all'errore, e il
    /// gruppo depositava alla ripartenza, anche ore dopo, senza chiedere se il gestionale teneva ancora
    /// la cella.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AGroupThatGoesInErrorForgetsTheConfirmationOfTheCell(bool error)
    {
        var (mission, step) = await AMissionOnM1WhoseCellInSGThePlcRefuses();
        var group = _plant["G"];
        group.Status = DeviceStatusTypes.Ready;
        await TheRouteLoopLooksForTheCell(mission);
        Assert.Equal([nameof(IWarehouse.GetStorageReservationAsync)], _warehouse.Calls);
        Assert.Equal(["M1->SG"], StepsOf(mission));
        Assert.Same(step, mission.MissionSteps[0]);

        if (error)
        {
            await ThePlcRefusesTheDeposit(group, step, 137);
            Assert.Equal(DeviceStatusTypes.Warning, group.Status);
            Assert.Equal(ResultCodeTypes.Error, step.StepStatus);
            // La conferma e' gia' caduta con l'errore: la ripartenza qui sotto passa dal setter dello
            // stato, che la butterebbe comunque (AutomationDeviceStateMachine.LeaveTheError).
            Assert.Null(mission.TakeStorageCellConfirmation());

            // L'operatore lo rimette in marcia.
            group.RunningStepId = Guid.Empty;
            group.Status = DeviceStatusTypes.Ready;
            step.GroupedMovementId = Guid.Empty;
        }

        RoutesToBuild.Instance.Items = new();
        _warehouse.Calls.Clear();
        Assert.NotNull(group.StepExecutor);

        await group.StepExecutor.ExecuteGroupedMOVE_LU_TO([step]);

        if (!error)
        {
            Assert.NotEqual(Guid.Empty, step.GroupedMovementId);
            Assert.Equal(group.RunningStepId, step.GroupedMovementId);
            Assert.Null(RoutesToBuild.Instance[mission.MissionId]);
            return;
        }

        Assert.Equal(Guid.Empty, step.GroupedMovementId);
        Assert.Equal(Guid.Empty, group.RunningStepId);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);

        RoutesToBuild.Instance.Items = new();
        await TheRouteLoopLooksForTheCell(mission);

        Assert.Equal([nameof(IWarehouse.GetStorageReservationAsync)], _warehouse.Calls);
        Assert.Equal(StorageDoorCode, mission.StorageDoorCode);
    }

    /// <summary>
    /// Come sopra, ma in errore va un altro membro: M2 sul movimento singolo della sua missione
    /// (l'errore 35, <c>StepError</c>), e con lui il gruppo G, fermo finche' l'operatore non lo rimette
    /// in marcia. La conferma della cella della missione dell'operatore su M1, a bordo del gruppo, si
    /// butta anche qui: il giro del gruppo, rimesso in marcia, non deposita con lei, e la missione torna
    /// al navigatore.
    /// </summary>
    [Fact]
    public async Task AMemberThatGoesInErrorForgetsTheConfirmationsOfTheWholeGroup()
    {
        var (mission, step) = await AMissionOnM1WhoseCellInSGThePlcRefuses();
        var other = await AnUnmarkedMissionOnM2ThroughX();
        var group = _plant["G"];
        var member = _plant["M2"];
        await TheRouteLoopLooksForTheCell(mission);
        Assert.Equal([nameof(IWarehouse.GetStorageReservationAsync)], _warehouse.Calls);
        Assert.Same(step, Assert.Single(mission.MissionSteps));

        var refused = Assert.Single(other.MissionSteps);
        member.RunningStepId = refused.StepId;
        await member.ConnectionAsiMovementIsCompleted(new AsiMovementIsCompletedEventArgs(
                member.ItemGuid, refused.StepId, ResultCodeTypes.Error, 35, "errore del campo", null))
            .WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(DeviceStatusTypes.Warning, member.Status);
        Assert.Equal(DeviceStatusTypes.Warning, group.Status);
        // La conferma e' gia' caduta con l'errore: la ripartenza qui sotto passa dal setter dello
        // stato, che la butterebbe comunque (AutomationDeviceStateMachine.LeaveTheError).
        Assert.Null(mission.TakeStorageCellConfirmation());

        // L'operatore li rimette in marcia.
        member.RunningStepId = Guid.Empty;
        member.Status = DeviceStatusTypes.Ready;
        group.Status = DeviceStatusTypes.Ready;
        RoutesToBuild.Instance.Items = new();
        Assert.NotNull(group.StepExecutor);

        await group.StepExecutor.ExecuteGroupedMOVE_LU_TO([step]);

        Assert.Equal(Guid.Empty, step.GroupedMovementId);
        Assert.Equal(Guid.Empty, group.RunningStepId);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
    }

    /// <summary>
    /// La conferma che arriva mentre il gruppo e' in errore non sopravvive alla ripartenza (risposta 6
    /// del proprietario dell'8/10, primo giro di correzioni del compito 12). G va in errore e aspetta
    /// quando la missione dell'operatore su M1 non ha ancora una conferma - per il 137 sul suo deposito
    /// (<c>StepErrorGroup</c>), o per l'errore 35 del membro M2 (<c>StepError</c>) - e l'errore non ha
    /// niente da buttare. Intanto il navigatore, che non guarda lo stato di chi deposita, chiede al
    /// gestionale e conferma la cella. L'operatore rimette in marcia: il giro del gruppo non deposita
    /// con quella conferma, e la missione torna al navigatore, che richiede. Prima la conferma presa
    /// durante l'errore restava fino alla ripartenza, anche ore dopo, e il deposito partiva senza
    /// chiedere.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AConfirmationGivenWhileTheGroupIsInErrorDoesNotOutliveTheRestart(bool memberInError)
    {
        var (mission, step) = await AMissionOnM1WhoseCellInSGThePlcRefuses();
        var group = _plant["G"];
        AutomationDevice? member = null;

        if (memberInError)
        {
            var other = await AnUnmarkedMissionOnM2ThroughX();
            member = _plant["M2"];
            var refused = Assert.Single(other.MissionSteps);
            member.RunningStepId = refused.StepId;
            await member.ConnectionAsiMovementIsCompleted(new AsiMovementIsCompletedEventArgs(
                    member.ItemGuid, refused.StepId, ResultCodeTypes.Error, 35, "errore del campo", null))
                .WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(DeviceStatusTypes.Warning, member.Status);
        }
        else
        {
            group.Status = DeviceStatusTypes.Ready;
            await ThePlcRefusesTheDeposit(group, step, 137);
            Assert.Equal(ResultCodeTypes.Error, step.StepStatus);
        }

        Assert.Equal(DeviceStatusTypes.Warning, group.Status);
        _warehouse.Calls.Clear();

        // Il navigatore conferma la cella mentre il gruppo aspetta l'operatore.
        RoutesToBuild.Instance.Items = new();
        await TheRouteLoopLooksForTheCell(mission);
        Assert.Equal([nameof(IWarehouse.GetStorageReservationAsync)], _warehouse.Calls);
        Assert.Same(step, Assert.Single(mission.MissionSteps));
        Assert.Equal(DeviceStatusTypes.Warning, group.Status);

        // L'operatore lo rimette in marcia.
        if (member != null)
        {
            member.RunningStepId = Guid.Empty;
            member.Status = DeviceStatusTypes.Ready;
        }

        group.RunningStepId = Guid.Empty;
        group.Status = DeviceStatusTypes.Ready;
        step.GroupedMovementId = Guid.Empty;
        RoutesToBuild.Instance.Items = new();
        _warehouse.Calls.Clear();
        Assert.NotNull(group.StepExecutor);

        await group.StepExecutor.ExecuteGroupedMOVE_LU_TO([step]);

        Assert.Equal(Guid.Empty, step.GroupedMovementId);
        Assert.Equal(Guid.Empty, group.RunningStepId);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);

        RoutesToBuild.Instance.Items = new();
        await TheRouteLoopLooksForTheCell(mission);

        Assert.Equal([nameof(IWarehouse.GetStorageReservationAsync)], _warehouse.Calls);
        Assert.Equal(StorageDoorCode, mission.StorageDoorCode);
    }

    /// <summary>
    /// Il dispositivo singolo M1 rifiuta il deposito con <paramref name="errorCode"/>, e la missione
    /// dell'operatore prende la cella <paramref name="cell"/> di SG: ogni domanda al gestionale di questo
    /// rifiuto esclude proprio le celle <paramref name="excluded"/>.
    /// </summary>
    private async Task M1RefusesAndTheMissionTakes(int errorCode, MissionStep refused, decimal cell, decimal[] excluded)
    {
        var mission = refused.PathMission!;
        TheWarehouseHasCell(cell);
        var asked = _warehouse.StorageRequests.Count;

        await ThePlcRefusesTheDeposit(_plant["M1"], refused, errorCode);

        TheMissionHasTheCell(mission, cell, asked, excluded);
    }

    /// <summary>Come <see cref="M1RefusesAndTheMissionTakes"/>, per il deposito di gruppo di G.</summary>
    private async Task TheGroupRefusesAndTheOperatorsMissionTakes(int errorCode, MissionStep refused, decimal cell,
        decimal[] excluded)
    {
        var mission = refused.PathMission!;
        TheWarehouseHasCell(cell);
        var asked = _warehouse.StorageRequests.Count;

        await ThePlcRefusesTheDeposit(_plant["G"], refused, errorCode);

        TheMissionHasTheCell(mission, cell, asked, excluded);
        Assert.Equal(DeviceStatusTypes.Ready, _plant["G"].Status);
    }

    /// <summary>
    /// La missione dell'operatore ha la cella <paramref name="cell"/> di SG, con il segno e il gateway X,
    /// e le domande al gestionale fatte dopo le prime <paramref name="asked"/> escludono proprio le celle
    /// <paramref name="excluded"/>.
    /// </summary>
    private void TheMissionHasTheCell(PathMission mission, decimal cell, int asked, decimal[] excluded)
    {
        Assert.Equal($"A{cell}", mission.StorageDoorCode);
        Assert.Equal(_plant["SG"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.OperatorDestination);
        Assert.Equal(_plant["X"].ItemGuid, mission.Gateway);
        Assert.Equal(["M1->X", "X->SG"], StepsOf(mission));
        Assert.Equal(cell, _warehouse.StorageReservationsByMission[mission.MissionId].CellId);

        var requests = _warehouse.StorageRequests.Skip(asked).ToList();
        Assert.NotEmpty(requests);
        Assert.All(requests, request => Assert.Equal(excluded.Order(), request.ExcludedCellIds.Order()));
    }

    /// <summary>
    /// <paramref name="device"/> e' in errore e aspetta l'operatore: la missione dell'operatore e' in
    /// errore, con lo step del deposito rifiutato marcato, e resta verso la porta <paramref name="door"/>
    /// di SG con il segno, senza scarto.
    /// </summary>
    private void TheDeviceWaitsInErrorWithTheOperatorsMission(AutomationDevice device, PathMission mission,
        MissionStep refused, string door)
    {
        Assert.Equal(DeviceStatusTypes.Warning, device.Status);
        Assert.Equal(MissionStatusTypes.Error, mission.Status);
        Assert.Equal(ResultCodeTypes.Error, refused.StepStatus);
        Assert.Equal(door, mission.StorageDoorCode);
        Assert.Equal(_plant["SG"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.OperatorDestination);
        Assert.NotEqual(MissionTypes.Rejection, mission.MissionType);
    }

    /// <summary>Come <see cref="TheDeviceWaitsInErrorWithTheOperatorsMission"/>, per il gruppo G.</summary>
    private void TheGroupWaitsInErrorWithTheOperatorsMission(PathMission mission, MissionStep refused, string door) =>
        TheDeviceWaitsInErrorWithTheOperatorsMission(_plant["G"], mission, refused, door);

    /// <summary>
    /// Il magazzino SG ha le celle <paramref name="cells"/>, con la porta «A» e il numero, e l'indirizzo
    /// che il gestionale ne da': per la cella n, 00n1/00n2 e la partizione 00n3.
    /// </summary>
    private async Task TheStorageSGHasTheCells(params int[] cells)
    {
        var storage = _plant["SG"];
        await PlantPersistence.SaveDoorMappingsAsync(storage.ItemGuid,
            [.. cells.Select(cell => new DoorMapping { ItemId = storage.ItemGuid, DoorCode = $"A{cell}", CellId = cell, Side = "A" })]);

        foreach (var cell in cells)
        {
            _warehouse.Cells[cell] = Cell(cell, $"A{cell}");
            _warehouse.CellPartitionMaps[cell] = new CellPartitionMap(cell, $"{cell * 10 + 1:0000}", $"{cell * 10 + 2:0000}",
                AsiDoorB: string.Empty, SubitemDoorB: string.Empty, [new CellPartition($"{cell * 10 + 3:0000}", -1)]);
        }
    }

    /// <summary>Il gestionale ha per l'UDC la cella <paramref name="cell"/> di SG, porta «A» e il numero.</summary>
    private void TheWarehouseHasCell(decimal cell) =>
        _warehouse.StorageReservations[LoadUnitId] = new StorageReservation(Guid.Empty,
            StorageReservationOutcome.CellFound, cell, $"A{cell}", Partition: $"{cell * 10 + 3:0000}", StorageDepth: 300,
            NeedsCompaction: false);

    /// <summary>
    /// Sul dispositivo singolo M1, fuori da ogni gruppo, una missione della configurazione (l'UDC 42)
    /// verso SG con la cella 6 (porta A6) che il gestionale le tiene, e lo step M1->SG del deposito. M1 e'
    /// il ricollocatore di SG, con la cella collegata 6; il gestionale ha per l'UDC la cella 8 (porta A8);
    /// il punto di scarto per mancanza di celle di M1 e' R.
    /// </summary>
    private async Task<(PathMission Mission, MissionStep Step)> AnUnmarkedMissionOnTheSingleDeviceM1WhoseCellInSGThePlcRefuses()
    {
        var (mission, step) = await AMissionOnAMemberOfG("SG", inTheGroup: false);
        var storage = _plant["SG"];
        _warehouse.StorageReservationsByMission[mission.MissionId] = new StorageReservation(mission.MissionId,
            StorageReservationOutcome.CellFound, StorageCellId, StorageDoorCode, Partition: "0063", StorageDepth: 150,
            NeedsCompaction: false);
        mission.DestinationAsi = "0061";
        mission.DestinationSubitem = "0062";
        mission.DestinationPartition = "0063";
        mission.StorageDepth = 150;
        mission.StorageCellReserved = true;

        await TheStorageSGHasTheCells(6, 8, 9);
        storage.RelocateItemId = _plant["M1"].ItemGuid;
        _plant["M1"].LinkedCellId = StorageCellId;
        TheWarehouseHasCell8();

        _plant.Device("R");
        _plant.Link("M1", "R");
        _plant["M1"].RejectItemDestinationAllocateLu = _plant["R"].ItemGuid;

        Assert.False(mission.OperatorDestination);
        Assert.Equal(StorageDoorCode, mission.StorageDoorCode);
        Assert.Same(step, Assert.Single(mission.MissionSteps));
        _warehouse.Calls.Clear();
        return (mission, step);
    }

    /// <summary>
    /// La missione dell'operatore tiene la cella 6 di SG che aveva: porta A6, indirizzo
    /// 0061/0062/0063, profondita' 150, gateway X, segno, e la rotta <paramref name="steps"/>; nessuna
    /// domanda al gestionale per lei, e la prenotazione e' ancora quella della cella 6.
    /// </summary>
    /// <param name="steps">
    /// La rotta: M1->X->SG rifatta dalla ricerca del comando manuale, che cerca tutte le missioni del
    /// gruppo; lo step M1->SG di prima, intatto, dopo il rifiuto di un'altra missione, che non la tocca
    /// (risposta 8 del proprietario dell'8/10 sera).
    /// </param>
    private void TheOperatorsMissionKeepsCell6(PathMission mission, params string[] steps)
    {
        Assert.Equal(_plant["SG"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.OperatorDestination);
        Assert.Equal(_plant["X"].ItemGuid, mission.Gateway);
        Assert.Equal(StorageDoorCode, mission.StorageDoorCode);
        Assert.Equal("0061", mission.DestinationAsi);
        Assert.Equal("0062", mission.DestinationSubitem);
        Assert.Equal("0063", mission.DestinationPartition);
        Assert.Equal(150, mission.StorageDepth);
        Assert.Equal(steps, StepsOf(mission));
        Assert.DoesNotContain(_warehouse.StorageRequests, request => request.MissionId == mission.MissionId);
        Assert.Equal(StorageCellId, _warehouse.StorageReservationsByMission[mission.MissionId].CellId);
    }

    /// <summary>
    /// Dopo un deposito rifiutato e il gestionale muto, la missione ha lasciato la porta rifiutata: niente
    /// porta, indirizzo ne' rotta, la cella persa, e in coda al navigatore, che le cerchera' un'altra cella
    /// (risposta 7 del proprietario dell'8/10 sera).
    /// </summary>
    private static void TheMissionLeftTheRefusedDoor(PathMission mission)
    {
        Assert.Null(mission.StorageDoorCode);
        Assert.Equal(string.Empty, mission.DestinationAsi);
        Assert.Empty(mission.MissionSteps);
        Assert.True(mission.StorageCellLost);
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
    }

    /// <summary>Le porte delle celle da segnare piene nel database del WCS, nell'ordine di arrivo.</summary>
    private List<string> TheCellsToMarkFullInTheDatabase()
    {
        using var context = _database.CreateContext();
        return [.. context.CellsToMarkFull.OrderBy(row => row.Id).Select(row => row.DoorCode)];
    }

    /// <summary>
    /// Un giro del ciclo delle rotte, come lo fa il motore ogni 10 ms, e il ritentativo delle celle da
    /// segnare piene che ha fatto partire, se l'ha fatto partire.
    /// </summary>
    private static async Task OneTurnOfTheRouteLoopAndItsRetry()
    {
        var method = typeof(Navigator).GetMethod("BuildRoutePath", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        await ((Task)method!.Invoke(null, null)!).WaitAsync(TimeSpan.FromSeconds(30));
        await CellsToMarkFull.Retried.WaitAsync(TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// Il PLC risponde con l'errore <paramref name="errorCode"/> al deposito dello step della missione,
    /// mandato da <paramref name="device"/>: il gettone e' quello dello step (per un gruppo, quello del
    /// movimento di gruppo, che lo step porta).
    /// </summary>
    private static async Task ThePlcRefusesTheDeposit(AutomationDevice device, MissionStep step, int errorCode)
    {
        var token = step.StepId;

        if (device.DeviceType == DeviceTypes.GroupOfAutomationDevices)
        {
            token = Guid.NewGuid();
            step.GroupedMovementId = token;
        }

        device.RunningStepId = token;
        await device.ConnectionAsiMovementIsCompleted(new AsiMovementIsCompletedEventArgs(
            device.ItemGuid, token, ResultCodeTypes.Error, errorCode, "deposito rifiutato", null))
            .WaitAsync(TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// La missione dell'operatore ha la cella 8 di SG, prenotata per lei nel gestionale con la domanda
    /// che escludeva la 6: porta A8, indirizzo 0081/0082/0083, profondita' 300 e la rotta M1->X->SG;
    /// destinazione SG, segno e gateway X restano, e lo step verso la porta A6 non c'e' piu'.
    /// </summary>
    private void TheOperatorsMissionHasCell8(PathMission mission, MissionStep refused)
    {
        Assert.Equal(_plant["SG"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.OperatorDestination);
        Assert.Equal(_plant["X"].ItemGuid, mission.Gateway);
        Assert.Equal(OtherDoorCode, mission.StorageDoorCode);
        Assert.Equal("0081", mission.DestinationAsi);
        Assert.Equal("0082", mission.DestinationSubitem);
        Assert.Equal("0083", mission.DestinationPartition);
        Assert.Equal(300, mission.StorageDepth);
        Assert.True(mission.StorageCellReserved);
        Assert.DoesNotContain(refused, mission.MissionSteps);
        Assert.Equal(["M1->X", "X->SG"], StepsOf(mission));

        var requests = _warehouse.StorageRequests.Where(request => request.MissionId == mission.MissionId).ToList();
        Assert.NotEmpty(requests);
        Assert.All(requests, request => Assert.Equal(new[] { StorageCellId }, request.ExcludedCellIds));
        Assert.Single(requests, request => request.AssignPosition);
        Assert.Equal(OtherCellId, _warehouse.StorageReservationsByMission[mission.MissionId].CellId);
    }

    /// <summary>
    /// La missione dell'operatore e' andata allo scarto R per mancanza di celle: destinazione R, una
    /// rotta verso R, niente segno, e la riga di avviso della scelta che cade.
    /// </summary>
    private async Task TheOperatorsMissionIsRejectedToR(PathMission mission, RecordingLogWriter log)
    {
        Assert.Equal(_plant["R"].ItemGuid, mission.DestinationItemId);
        Assert.False(mission.OperatorDestination);
        Assert.Equal(Guid.Empty, mission.Gateway);
        Assert.Equal(["M1->R"], StepsOf(mission));
        Assert.Equal(MissionTypes.Rejection, mission.MissionType);

        await log.Wait((line, level) => level == Log.Interface.LogEntryType.Warning &&
                                        line.Contains(mission.MissionId.ToString()) &&
                                        line.Contains("la scelta dell'operatore cade"))
            .WaitAsync(TimeSpan.FromSeconds(30));
    }

    // ---------------------------------------------------------------------------------------
    // Gli avvii finiti non restano contati
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Nessun avvio resta contato sulla missione: un cambio dell'operatore, adesso, comincerebbe
    /// (<c>PathMission.TryBeginOperatorChange</c>), e si chiude subito. Un conto lasciato aperto (un
    /// <c>EndStepStart</c> mancato in uno dei punti che si contano) rifiuterebbe ogni cambio della
    /// missione come passo in volo, per sempre; e in una prova che aspetta quel rifiuto per un'altra
    /// ragione - il gettone di uno step in volo - lo farebbe passare per la ragione sbagliata.
    /// </summary>
    internal static void NoStepStartIsLeftCounted(PathMission mission)
    {
        Assert.Equal(PathMission.OperatorChangeStart.Begun, mission.TryBeginOperatorChange());
        mission.EndOperatorChange();
    }

    /// <summary>
    /// Come <see cref="NoStepStartIsLeftCounted"/>, per un avvio partito da un risveglio
    /// (<c>_ = device.ExecuteStep()</c>): non c'e' un <c>Task</c> da aspettare, e il gettone che la
    /// prova aspetta si scrive dentro il conto, prima che si chiuda. Si riprova finche' il cambio
    /// comincia, senza attese a tempo, con un tetto che fa fallire invece di appendere; il tentativo
    /// che non comincia non tocca niente (<c>StepStarting</c>).
    /// </summary>
    internal static async Task NoStepStartIsLeftCountedOnceItIsOver(PathMission mission)
    {
        using var cap = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        PathMission.OperatorChangeStart start;

        while ((start = mission.TryBeginOperatorChange()) != PathMission.OperatorChangeStart.Begun)
        {
            Assert.Equal(PathMission.OperatorChangeStart.StepStarting, start);
            Assert.False(cap.IsCancellationRequested, "un avvio della missione e' rimasto contato");
            await Task.Yield();
        }

        mission.EndOperatorChange();
    }

    // ---------------------------------------------------------------------------------------
    // Il cambio cella di un'estrazione con l'UDC ancora nella sua cella
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Risposte 1 e 2 del proprietario del 2/10: l'estrazione con l'UDC ancora nella cella 5 di S,
    /// mandata dall'operatore nel magazzino S2, e' un cambio cella. Prima di prenotare la cella 6 di
    /// S2, e dopo la domanda senza prenotare, il gestionale sa che la missione lascia l'assegnazione
    /// (per GUID e per la riga di coda) e che l'UDC esce dalla coda di estrazione; la riga della coda
    /// del WCS si cancella, senza l'esito «non raggiungibile». Al commit la missione diventa un cambio
    /// cella verso la porta A6, con la porta di prelievo A5 che serve al prelievo, il ricollocatore R e
    /// senza estrazione ne' assegnazione; al prelievo non parte ne' «estratta» ne' «servita». Prima
    /// restava un'estrazione, e al prelievo il gestionale sentiva «estratta» e «servita» per un'UDC
    /// che andava in un'altra cella.
    /// </summary>
    [Fact]
    public async Task AnExtractionStillInItsCellSentToAnotherStorageBecomesAChangeOfCell()
    {
        var mission = await AnExtractionInItsCellWithTheStorageS2();

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S2"].ItemGuid);

        Assert.Equal(MissionDestinationOutcome.Changed, result.Outcome);
        Assert.False(result.MissionClosed);
        Assert.Equal(MissionTypes.ChangeStoreLocation, mission.MissionType);
        Assert.Equal(PickUpDoorCode, mission.PickUpDoorCode);
        Assert.Equal(StorageDoorCode, mission.StorageDoorCode);
        Assert.Equal(_plant["S2"].ItemGuid, mission.DestinationItemId);
        Assert.Equal(_plant["R"].ItemGuid, mission.RelocateItemId);
        Assert.Equal(new[] { "R->S2", "S->R" }, StepsOf(mission));

        Assert.Equal(0m, mission.ExtractionQueueId);
        Assert.Equal(Guid.Empty, mission.GuidAssignmentLu);
        Assert.Equal(0m, mission.LuNoteAssignmentId);

        Assert.Equal(
            new[] { (mission.MissionId, LoadUnitId, Assignment, 0m), (mission.MissionId, LoadUnitId, Guid.Empty, AssignmentId) },
            _warehouse.MissionsLeftAssignmentUnserved.Select(r => (r.MissionId, r.LoadUnitId, r.AssignmentGuid, r.AssignmentId)));
        Assert.All(_warehouse.MissionsLeftAssignmentUnserved, r => Assert.NotEmpty(r.Reason));
        Assert.Equal(new LoadUnitLeftExtractionQueueRequest(LoadUnitId, AssignmentId),
            Assert.Single(_warehouse.LoadUnitsLeftExtractionQueue));

        // Dopo la domanda senza prenotare, prima della prenotazione.
        Assert.Equal(
            [
                nameof(IWarehouse.ReserveStorageAsync),
                nameof(IWarehouse.ReportMissionLeftAssignmentUnservedAsync),
                nameof(IWarehouse.ReportMissionLeftAssignmentUnservedAsync),
                nameof(IWarehouse.ReportLoadUnitLeftExtractionQueueAsync),
                nameof(IWarehouse.ReserveStorageAsync)
            ],
            _warehouse.Calls.Where(TheReservationOrAReport));
        Assert.Equal([false, true], _warehouse.StorageRequests.Select(request => request.AssignPosition));

        Assert.Empty(await ExtractionQueueQueries.ListAsync(processed: null, loadUnitId: LoadUnitId, take: 10));
        Assert.Empty(_warehouse.ReportedExtractionOutcomes);

        // Il prelievo: l'UDC arriva su R, che e' collegato a un magazzino.
        _warehouse.Calls.Clear();
        mission.PositionItemId = _plant["R"].ItemGuid;
        await mission.PositionWork;

        Assert.DoesNotContain(nameof(IWarehouse.ReportExtractionOutcomesAsync), _warehouse.Calls);
        Assert.DoesNotContain(nameof(IWarehouse.ReportMissionServedAssignmentAsync), _warehouse.Calls);
        Assert.DoesNotContain(nameof(IWarehouse.ConfirmServedAssignmentsAsync), _warehouse.Calls);
    }

    /// <summary>
    /// Voce 45, compito 4 (decisione 3 del proprietario del 10/10, decisione A dell'11/10): nel cambio
    /// cella la riga di estrazione per UDC 901, che il gestionale aveva messo «in estrazione», restava
    /// ferma senza piu' una coda che la servisse. Ora, insieme alla cancellazione della riga di coda,
    /// il fatto va nella coda durevole del WCS con la riga 901, senza assegnazione ne' GUID (le
    /// lasciano le segnalazioni del cambio cella), e nessuna chiamata parte durante il cambio. Il
    /// dispatcher lo tiene finche' il cambio cella porta l'UDC e lo manda quando la missione e'
    /// finita: allora il gestionale la rimette da servire, e il giro di assegnazione la serve dalla
    /// cella nuova.
    /// </summary>
    [Fact]
    public async Task AChangeOfCellPutsItsExtractionRowBackToBeServedOnceTheMissionIsOver()
    {
        var mission = await AnExtractionInItsCellWithTheStorageS2();

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S2"].ItemGuid);

        Assert.Equal(MissionDestinationOutcome.Changed, result.Outcome);
        var report = Assert.Single(await ORM.PickedExtractionReportOutbox.PendingAsync(10));
        Assert.NotEqual(Guid.Empty, report.OperationId);
        Assert.Equal((mission.MissionId, LoadUnitId, 0m, Guid.Empty, ExtractionRowId, "KazamControl.Process", ChangeOfCellReopenReason),
            (report.MissionId, report.LoadUnitId, report.AssignmentId, report.AssignmentGuid, report.ExtractionRowId,
                report.UserId, report.Reason));
        Assert.DoesNotContain(nameof(IWarehouse.ReportPickedExtractionBecameDepositAsync), _warehouse.Calls);

        var dispatcher = new PendingPickedExtractionReportDispatcher();
        await dispatcher.TryDispatchAsync();

        Assert.Empty(_warehouse.PickedExtractionsBecameDeposit);
        Assert.Equal(0, Assert.Single(await ORM.PickedExtractionReportOutbox.PendingAsync(10)).AttemptCount);

        // Il cambio cella e' finito: la missione esce dalle attive.
        ActiveMissions.Instance.RemoveAll(active => ReferenceEquals(active, mission));
        Assert.False(Navigator.IsLuInExtraction(LoadUnitId));
        await dispatcher.TryDispatchAsync();

        Assert.Equal(new PickedExtractionBecameDepositRequest(report.OperationId, mission.MissionId, LoadUnitId, 0m,
                Guid.Empty, ExtractionRowId, "KazamControl.Process", ChangeOfCellReopenReason),
            Assert.Single(_warehouse.PickedExtractionsBecameDeposit));
        Assert.Empty(await ORM.PickedExtractionReportOutbox.PendingAsync(10));
    }

    /// <summary>
    /// Una riga di coda che non serve una riga di estrazione per UDC (solo l'assegnazione di una
    /// nota): il cambio cella non ha niente da rimettere da servire, e la coda del fatto resta vuota.
    /// L'assegnazione la lasciano le segnalazioni di sempre.
    /// </summary>
    [Fact]
    public async Task AChangeOfCellWithoutAnExtractionRowQueuesNothing()
    {
        var mission = await AnExtractionInItsCellWithTheStorageS2();
        await ORM.ExtractionQueue.WriteAsync(async context =>
            (await context.ExtractionQueue.SingleAsync(row => row.Id == (long)mission.ExtractionQueueId)).ExtractionRowId = null);

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S2"].ItemGuid);

        Assert.Equal(MissionDestinationOutcome.Changed, result.Outcome);
        Assert.Equal(2, _warehouse.MissionsLeftAssignmentUnserved.Count);
        Assert.Empty(await ExtractionQueueQueries.ListAsync(processed: null, loadUnitId: LoadUnitId, take: 10));
        Assert.Empty(await ORM.PickedExtractionReportOutbox.PendingAsync(10));
    }

    /// <summary>
    /// <b>Una scrittura sola.</b> Con lo scrittore di stato (come in esercizio), la riga di coda
    /// cancellata e il fatto aggiunto stanno nella stessa scrittura: un arresto fra le due non lascia
    /// ne' una riga di coda sparita senza il fatto - la riga di estrazione resterebbe ferma per sempre -
    /// ne' il fatto con la riga di coda ancora li'. Lo scrittore esegue una scrittura per contesto, per
    /// vedere che cosa porta ciascuna.
    /// </summary>
    [Fact]
    public async Task TheQueueRowLeavesInTheSameWriteAsTheFact()
    {
        var mission = await AnExtractionInItsCellWithTheStorageS2();
        var queueRow = (long)mission.ExtractionQueueId;

        var writes = new List<(bool QueueRowDeleted, bool FactAdded)>();
        var writer = new QueuedDatabaseWriter<WcsWrite>(async (batch, ct) =>
        {
            foreach (var write in batch)
            {
                await using var context = _database.CreateContext();
                await write(context);
                lock (writes)
                    writes.Add((
                        context.ChangeTracker.Entries<ExtractionQueueItem>().Any(entry =>
                            entry.State == EntityState.Deleted && entry.Entity.Id == queueRow),
                        context.ChangeTracker.Entries<PendingPickedExtractionReport>().Any(entry =>
                            entry.State == EntityState.Added)));
                await context.SaveChangesAsync(ct);
            }
        });
        Navigator.StateWriter = writer;
        try
        {
            Assert.Equal(MissionDestinationOutcome.Changed,
                (await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S2"].ItemGuid)).Outcome);
        }
        finally
        {
            Navigator.StateWriter = null;
            await writer.DisposeAsync();
        }

        Assert.Equal((true, true), Assert.Single(writes, write => write.QueueRowDeleted || write.FactAdded));
        Assert.Single(await ORM.PickedExtractionReportOutbox.PendingAsync(10));
        Assert.Empty(await ExtractionQueueQueries.ListAsync(processed: null, loadUnitId: LoadUnitId, take: 10));
    }

    /// <summary>
    /// La prima segnalazione del cambio cella non puo' essere arrivata al gestionale: la connessione
    /// e' rifiutata (la richiesta non e' partita), o il gestionale risponde di no con un 400 (non l'ha
    /// applicata). Il cambio e' rifiutato - come gestionale fermo la connessione rifiutata, come
    /// «il gestionale ha rifiutato» il 400, che non e' un gestionale fermo (risposta 4 dell'8/10 sera;
    /// prima anche il 400 era «gestionale fermo») - e la missione resta l'estrazione che era, verso Y
    /// con la sua rotta, la sua riga di coda e la sua assegnazione; nessuna altra segnalazione parte,
    /// nessuna cella prenotata, e il segno «segnalazioni partite» non resta, ne' in memoria ne' nel
    /// database del WCS (specifica, risposte dell'8/10 ai punti 2 e 3).
    /// </summary>
    [Theory]
    [InlineData("refused", MissionDestinationOutcome.WarehouseUnavailable)]
    [InlineData("rejected", MissionDestinationOutcome.WarehouseRefused)]
    public async Task AFirstReportThatCouldNotArriveLeavesTheExtractionAsItWas(string fault,
        MissionDestinationOutcome outcome)
    {
        var mission = await AnExtractionInItsCellWithTheStorageS2();
        var step = Assert.Single(mission.MissionSteps);
        var row = mission.ExtractionQueueId;
        _warehouse.Fault = operation => TheChangeOfCellReport(operation) ? Fault(operation, fault) : null;

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S2"].ItemGuid);

        Assert.Equal(outcome, result.Outcome);
        Assert.False(result.MissionClosed);
        Assert.Same(mission, ActiveMissions.Instance[mission.MissionId]);
        Assert.Equal(MissionTypes.Extraction, mission.MissionType);
        Assert.Equal(_plant["Y"].ItemGuid, mission.DestinationItemId);
        Assert.False(mission.OperatorDestination);
        Assert.Equal(PickUpDoorCode, mission.PickUpDoorCode);
        Assert.Same(step, Assert.Single(mission.MissionSteps));
        Assert.Equal(row, mission.ExtractionQueueId);
        Assert.Equal(Assignment, mission.GuidAssignmentLu);
        Assert.Equal(AssignmentId, mission.LuNoteAssignmentId);

        Assert.Single(_warehouse.Calls, TheChangeOfCellReport);
        Assert.Equal([false], _warehouse.StorageRequests.Select(request => request.AssignPosition));
        Assert.Single(await ExtractionQueueQueries.ListAsync(processed: null, loadUnitId: LoadUnitId, take: 10));

        Assert.False(mission.ChangeOfCellReportsSent);
        Assert.False(TheRowOf(mission)!.ChangeOfCellReportsSent);
        Assert.Empty(await ORM.PickedExtractionReportOutbox.PendingAsync(10));
    }

    /// <summary>
    /// Item 55: the extraction still in the cell 5 of S sent by the operator to the precise cell A6. The
    /// warehouse refuses the cell already at the check, before the reports: the mission stays the
    /// extraction it was, with its assignment and its queue row, and the warehouse hears nothing. Before
    /// the check the reports went first and the refusal of the reservation closed the mission.
    /// </summary>
    [Fact]
    public async Task APreciseCellRefusedAtTheCheckLeavesTheExtractionAsItWas()
    {
        var mission = await AnExtractionInItsCellWithTheStorageS2();
        _warehouse.ReserveStorageCellAnswer = _ => new StorageCellReservationResult(
            StorageCellReservationOutcome.CellNotAvailable, null, "la cella A6 e' occupata");

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S2"].ItemGuid,
            MissionGatewayChoice.Keep, MissionDestinationOptions.Cell(StorageDoorCode));

        Assert.Equal(MissionDestinationOutcome.CellRefused, result.Outcome);
        Assert.Equal(MissionCellRefusal.CellNotAvailable, result.CellRefusal);
        Assert.Equal("la cella A6 e' occupata", result.CellRefusalReason);
        Assert.False(result.MissionClosed);
        Assert.Same(mission, ActiveMissions.Instance[mission.MissionId]);
        Assert.Equal(MissionTypes.Extraction, mission.MissionType);
        Assert.Equal(Assignment, mission.GuidAssignmentLu);
        Assert.NotEqual(0m, mission.ExtractionQueueId);
        Assert.Single(await ExtractionQueueQueries.ListAsync(processed: null, loadUnitId: LoadUnitId, take: 10));
        Assert.Equal([nameof(IWarehouse.CheckStorageCellAsync)],
            _warehouse.Calls.Where(call => TheReservationOrAReport(call) || call is nameof(IWarehouse.CheckStorageCellAsync)
                or nameof(IWarehouse.ReserveStorageCellAsync)));
        Assert.Empty(_warehouse.StorageCellReservationRequests);
        Assert.False(TheRowOf(mission)!.ChangeOfCellReportsSent);
    }

    /// <summary>
    /// Item 55: the same change with the cell A6 free. The check says it would, then the reports, then
    /// the reservation: the mission becomes the change of cell towards A6.
    /// </summary>
    [Fact]
    public async Task APreciseCellPassingTheCheckIsReservedAfterTheReports()
    {
        var mission = await AnExtractionInItsCellWithTheStorageS2();
        _warehouse.ReservableStorageCells[StorageDoorCode] = new StorageReservation(Guid.Empty,
            StorageReservationOutcome.CellFound, StorageCellId, StorageDoorCode, Partition: "0063", StorageDepth: 150,
            NeedsCompaction: false);

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S2"].ItemGuid,
            MissionGatewayChoice.Keep, MissionDestinationOptions.Cell(StorageDoorCode));

        Assert.Equal(MissionDestinationOutcome.Changed, result.Outcome);
        Assert.Equal(MissionTypes.ChangeStoreLocation, mission.MissionType);
        Assert.Equal(StorageDoorCode, mission.StorageDoorCode);
        Assert.Equal(
            [
                nameof(IWarehouse.CheckStorageCellAsync),
                nameof(IWarehouse.ReportMissionLeftAssignmentUnservedAsync),
                nameof(IWarehouse.ReportMissionLeftAssignmentUnservedAsync),
                nameof(IWarehouse.ReportLoadUnitLeftExtractionQueueAsync),
                nameof(IWarehouse.ReserveStorageCellAsync)
            ],
            _warehouse.Calls.Where(call => TheReservationOrAReport(call) || call is nameof(IWarehouse.CheckStorageCellAsync)
                or nameof(IWarehouse.ReserveStorageCellAsync)));
    }

    /// <summary>
    /// Item 55: a warehouse without the check (404, null). The change goes on as before the check
    /// existed: the reports, then the reservation, whose refusal closes the mission.
    /// </summary>
    [Fact]
    public async Task WithoutTheCheckARefusalAfterTheReportsStillClosesTheMission()
    {
        var mission = await AnExtractionInItsCellWithTheStorageS2();
        _warehouse.OffersStorageCellCheck = false;
        _warehouse.ReserveStorageCellAnswer = _ => new StorageCellReservationResult(
            StorageCellReservationOutcome.CellNotAvailable, null, "la cella A6 e' occupata");

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S2"].ItemGuid,
            MissionGatewayChoice.Keep, MissionDestinationOptions.Cell(StorageDoorCode));

        Assert.Equal(MissionDestinationOutcome.CellRefused, result.Outcome);
        Assert.True(result.MissionClosed);
        Assert.Null(ActiveMissions.Instance[mission.MissionId]);
        Assert.Single(_warehouse.StorageCellReservationRequests);
    }

    /// <summary>
    /// Una segnalazione del cambio cella puo' essere arrivata al gestionale e il cambio non va in porto
    /// (specifica, risposta dell'8/10 al punto 2): la prima va in tempo scaduto, resta senza la causa di
    /// trasporto (la regola della specifica: se non si sa, conta come arrivata) o ha un 5xx - il
    /// gestionale puo' averla applicata -, o arriva e il gestionale non risponde a quella dopo - la seconda, con la
    /// risposta nuda o con la connessione rifiutata, che dopo la prima non conta piu', o la terza. La
    /// missione si chiude come dopo la prenotazione, e il rifiuto lo dice: fuori dalle attive e dal
    /// database del WCS, la riga della coda cancellata, nessuna cella prenotata. Prima il cambio era
    /// rifiutato e la missione restava l'estrazione che era, con l'ordine gia' lasciato per il
    /// gestionale.
    /// </summary>
    [Theory]
    [InlineData(1, "timeout")]
    [InlineData(1, "mute")]
    [InlineData(1, "5xx")]
    [InlineData(2, "mute")]
    [InlineData(2, "refused")]
    [InlineData(3, "mute")]
    public async Task AReportThatMayHaveArrivedClosesTheMissionWhenTheChangeDoesNotGoThrough(int failing, string fault)
    {
        var mission = await AnExtractionInItsCellWithTheStorageS2();
        var reports = 0;
        _warehouse.Fault = operation =>
            TheChangeOfCellReport(operation) && ++reports == failing ? Fault(operation, fault) : null;

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S2"].ItemGuid);

        Assert.Equal(MissionDestinationOutcome.WarehouseUnavailable, result.Outcome);
        Assert.True(result.MissionClosed);
        Assert.Null(ActiveMissions.Instance[mission.MissionId]);
        Assert.Null(TheRowOf(mission));
        Assert.Equal(failing, reports);
        Assert.Equal([false], _warehouse.StorageRequests.Select(request => request.AssignPosition));
        Assert.Empty(await ExtractionQueueQueries.ListAsync(processed: null, loadUnitId: LoadUnitId, take: 10));
        Assert.Empty(_warehouse.ReportedExtractionOutcomes);
        Assert.Equal(ExtractionRowId, Assert.Single(await ORM.PickedExtractionReportOutbox.PendingAsync(10)).ExtractionRowId);
    }

    /// <summary>
    /// Il segno «segnalazioni partite» e' gia' nel database del WCS quando il gestionale riceve la
    /// prima segnalazione del cambio cella, e il commit del cambio lo toglie, in memoria e nel database
    /// (specifica, risposta dell'8/10 al punto 3). Prima non c'era niente di durevole fra le
    /// segnalazioni e il commit. Senza lo scrittore in coda, come il resto della classe: che il segno
    /// sia aspettato, e non solo messo in coda, lo guarda
    /// <see cref="WithTheQueuedWriterTheFirstReportWaitsForTheMarkInTheDatabase"/>.
    /// </summary>
    [Fact]
    public async Task TheMarkIsInTheDatabaseWhenTheFirstReportLeavesAndTheCommitTakesItDown()
    {
        var mission = await AnExtractionInItsCellWithTheStorageS2();
        Assert.False(TheRowOf(mission)!.ChangeOfCellReportsSent);
        bool? markedAtTheFirstReport = null;
        _warehouse.Fault = operation =>
        {
            if (TheChangeOfCellReport(operation) && markedAtTheFirstReport == null)
                markedAtTheFirstReport = TheRowOf(mission)!.ChangeOfCellReportsSent;
            return null;
        };

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S2"].ItemGuid);

        Assert.Equal(MissionDestinationOutcome.Changed, result.Outcome);
        Assert.True(markedAtTheFirstReport);
        Assert.Equal(MissionTypes.ChangeStoreLocation, mission.MissionType);
        Assert.False(mission.ChangeOfCellReportsSent);

        var committed = TheRowOf(mission)!;
        Assert.False(committed.ChangeOfCellReportsSent);
        Assert.Equal((short)MissionTypes.ChangeStoreLocation, committed.MissionType);
    }

    /// <summary>
    /// Il cambio cella riuscito con il motore in Recover, mentre ricarica (specifica, risposta 1
    /// dell'8/10 sera; la prova gira in Recover per questo, accanto a quelle dei compiti 1 e 10): li' i
    /// setter del commit non scrivono, e la riga restava l'estrazione con il segno «segnalazioni
    /// partite». Dopo il commit la riga e' il cambio cella, senza il segno, senza estrazione ne'
    /// assegnazione; e una ricarica dal database del WCS rimette in piedi il cambio cella, invece di
    /// chiuderlo come un'estrazione con le segnalazioni partite. La prova aspetta la costruzione della
    /// rotta che la ricarica avvia senza attenderla (<c>ActiveMissions.Add</c>), come le altre prove che
    /// ricaricano (voce 46 del develop): senza, la costruzione poteva finire dopo la prova, con lo stato
    /// del motore della prova dopo (voce 51). Il lavoro della posizione non c'e': l'estrazione nella sua
    /// cella non ha una posizione, e la ricarica non la imposta.
    /// </summary>
    [Fact]
    public async Task AChangeOfCellInRecoverIsWrittenAndAReloadKeepsIt()
    {
        var mission = await AnExtractionInItsCellWithTheStorageS2();
        Navigator.NavStatus = NavigatorStatusTypes.Recover;

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S2"].ItemGuid);

        Assert.Equal(MissionDestinationOutcome.Changed, result.Outcome);
        Assert.Equal(MissionTypes.ChangeStoreLocation, mission.MissionType);

        var row = TheRowOf(mission);
        Assert.NotNull(row);
        Assert.False(row.ChangeOfCellReportsSent);
        Assert.Equal((short)MissionTypes.ChangeStoreLocation, row.MissionType);
        Assert.Equal(StorageDoorCode, row.StorageDoorCode);
        Assert.Equal(0m, row.ExtractionQueueId);
        Assert.Null(row.AssignmentGuid);

        // Il riavvio: le attive si svuotano senza toccare il database, e la ricarica legge le righe.
        await ActiveMissions.Instance.Clear(false);
        await ORM.DbInfo.RecoverFailure();

        var reloaded = ActiveMissions.Instance[mission.MissionId];
        Assert.NotNull(reloaded);
        Assert.NotSame(mission, reloaded);
        await reloaded.RouteWork.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(MissionTypes.ChangeStoreLocation, reloaded.MissionType);
        Assert.Equal(StorageDoorCode, reloaded.StorageDoorCode);
        Assert.False(reloaded.ChangeOfCellReportsSent);
        Assert.NotNull(TheRowOf(mission));
    }

    /// <summary>
    /// Il cambio cella e' appena registrato, e l'operatore toglie la missione prima che il motore ne
    /// scriva la riga con il segno giu' (qui la rimozione parte dall'annuncio del cambio): la rimozione
    /// cancella la riga, e la scrittura dopo il commit non la rifa'. Prima la scrittura dopo il commit,
    /// un'istantanea intera, riportava nel database del WCS la riga di una missione che non c'era piu', e
    /// la ricarica l'avrebbe rimessa fra le attive (revisione finale del terzo giro).
    /// </summary>
    [Fact]
    public async Task AChangeOfCellRemovedRightAfterTheCommitDoesNotComeBackInTheDatabase()
    {
        var mission = await AnExtractionInItsCellWithTheStorageS2();
        Task<bool>? removal = null;
        Navigator.EventSink = new OnTheChangeOfCell(mission, () => removal = ActiveMissions.Instance.Remove(mission));

        try
        {
            await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S2"].ItemGuid)
                .WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            Navigator.EventSink = null;
        }

        Assert.NotNull(removal);
        Assert.True(await removal.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Null(ActiveMissions.Instance[mission.MissionId]);
        Assert.Null(TheRowOf(mission));
    }

    /// <summary>
    /// Il gestore degli eventi del motore che fa una cosa sola, la prima volta che la missione
    /// <c>watched</c> cambia da cambio cella registrato: e' il punto fra il commit e la scrittura della riga.
    /// </summary>
    private sealed class OnTheChangeOfCell(PathMission watched, Action action) : IEngineEventSink
    {
        private int _done;

        public void MissionChanged(PathMission mission, EngineMissionChange change)
        {
            if (ReferenceEquals(mission, watched) && mission.MissionType == MissionTypes.ChangeStoreLocation &&
                Interlocked.Exchange(ref _done, 1) == 0)
                action();
        }

        public void CommandChanged(CommandRecord command) { }
        public void ItemChanged(AutomationDevice device) { }
        public void NavigatorStatusChanged() { }
        public void ExtractionQueueChanged(ExtractionQueueItem item, EngineQueueChange change) { }
    }

    /// <summary>
    /// Come sopra, con lo scrittore in coda che in produzione c'e' sempre: senza, la scrittura della
    /// missione va dritta nel database e il segno scritto con un setter qualunque sembrerebbe
    /// aspettato. La scrittura che porta il segno si ferma in coda, prima di salvare, finche' la prova
    /// non la lascia andare: la prima segnalazione non parte mentre e' ferma, e quando parte il segno e'
    /// nel database. Un segno solo messo in coda lascerebbe partire le segnalazioni subito, con la riga
    /// ancora senza segno: un host che si ferma li' ricaricherebbe l'estrazione che il gestionale ha gia'
    /// lasciato andare (specifica, risposta dell'8/10 al punto 3).
    /// </summary>
    [Fact]
    public async Task WithTheQueuedWriterTheFirstReportWaitsForTheMarkInTheDatabase()
    {
        var mission = await AnExtractionInItsCellWithTheStorageS2();
        var markHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task ExecuteAsync(IReadOnlyList<WcsWrite> batch, CancellationToken token)
        {
            await using var context = _database.CreateContext();
            foreach (var write in batch)
                await write(context);

            if (context.ChangeTracker.Entries<Mission>().Any(entry =>
                    entry.Entity.MissionId == mission.MissionId && entry.Entity.ChangeOfCellReportsSent) &&
                markHeld.TrySetResult())
                await released.Task;

            await context.SaveChangesAsync(token);
        }

        await using var writer = new QueuedDatabaseWriter<WcsWrite>(ExecuteAsync,
            new QueuedDatabaseWriterOptions { DropOldestWhenFull = false, FlushInterval = TimeSpan.FromMilliseconds(20) });
        Navigator.StateWriter = writer;

        bool? reportedWhileHeld = null;
        bool? markedAtTheFirstReport = null;
        _warehouse.Fault = operation =>
        {
            if (TheChangeOfCellReport(operation) && markedAtTheFirstReport == null)
            {
                reportedWhileHeld = !released.Task.IsCompleted;
                markedAtTheFirstReport = TheRowOf(mission)!.ChangeOfCellReportsSent;
            }

            return null;
        };

        try
        {
            var change = MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S2"].ItemGuid);

            await markHeld.Task.WaitAsync(TimeSpan.FromSeconds(30));
            released.SetResult();
            var result = await change.WaitAsync(TimeSpan.FromSeconds(30));
            await writer.FlushAsync().WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(MissionDestinationOutcome.Changed, result.Outcome);
            Assert.False(reportedWhileHeld);
            Assert.True(markedAtTheFirstReport);

            var committed = TheRowOf(mission)!;
            Assert.False(committed.ChangeOfCellReportsSent);
            Assert.Equal((short)MissionTypes.ChangeStoreLocation, committed.MissionType);
        }
        finally
        {
            released.TrySetResult();
            Navigator.StateWriter = null;
        }
    }

    /// <summary>
    /// Il cambio cella rifiutato dopo le segnalazioni (risposta del proprietario del 2/10 sulla domanda
    /// del compito 4): alla prenotazione il gestionale non ha piu' una cella. La missione non puo'
    /// tornare l'estrazione che era, e si chiude: fuori dalle attive, senza rotta, la riga della coda
    /// cancellata, nessuna cella tenuta; e chi l'aveva presa in carico per prelevarla la lascia: il
    /// ricollocatore R fra l'attivazione e il prelievo, la sua coda di prelievo dopo un riavvio, o il
    /// gruppo G che l'aveva presa con altre. Anche la coda delle rotte da costruire la perde. L'UDC
    /// resta nella cella 5, e il rifiuto lo dice. Prima la missione restava un'estrazione verso Y, e R
    /// la prelevava; poi, chiusa, restava nella lista del gruppo e nella coda delle rotte (primo giro
    /// di correzioni).
    /// </summary>
    [Theory]
    [InlineData("taken")]
    [InlineData("queued")]
    [InlineData("grouped")]
    public async Task AChangeOfCellRefusedAfterTheReportsClosesTheMissionAndTheLoadUnitStaysInItsCell(string holder)
    {
        var mission = await AnExtractionInItsCellWithTheStorageS2();
        var picker = holder == "grouped" ? _plant.Device("G", DeviceTypes.GroupOfAutomationDevices) : _plant["R"];
        switch (holder)
        {
            case "taken": picker.LUtoPick = mission; break;
            case "queued": picker.EnqueueLuToPickUp(mission); break;
            default: picker.LUsToPick = [mission]; break;
        }

        // Un prelievo cominciato senza step (StartPickUpLu -> HandleNoStepToDo) la manda al navigatore.
        RoutesToBuild.Instance.Enqueue(mission);
        NoCellAtTheReservation();

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S2"].ItemGuid);

        Assert.Equal(MissionDestinationOutcome.NoStorageCell, result.Outcome);
        Assert.True(result.MissionClosed);
        Assert.Null(ActiveMissions.Instance[mission.MissionId]);
        Assert.Empty(mission.MissionSteps);
        Assert.Null(picker.LUtoPick);
        Assert.Equal(0, picker.QueuedLuToPickUpCount);
        Assert.Empty(picker.LUsToPick);
        Assert.Null(RoutesToBuild.Instance[mission.MissionId]);

        Assert.Equal(2, _warehouse.MissionsLeftAssignmentUnserved.Count);
        Assert.Single(_warehouse.LoadUnitsLeftExtractionQueue);
        Assert.Equal([false, true], _warehouse.StorageRequests.Select(request => request.AssignPosition));
        Assert.False(_warehouse.StorageReservationsByMission.ContainsKey(mission.MissionId));
        Assert.Empty(await ExtractionQueueQueries.ListAsync(processed: null, loadUnitId: LoadUnitId, take: 10));
        Assert.Empty(_warehouse.ReportedExtractionOutcomes);

        // Voce 45, compito 4: la riga di estrazione 901 torna da servire, e con la missione chiusa
        // nessuno porta piu' l'UDC: il dispatcher la manda al primo giro.
        var report = Assert.Single(await ORM.PickedExtractionReportOutbox.PendingAsync(10));
        await new PendingPickedExtractionReportDispatcher().TryDispatchAsync();
        Assert.Equal(new PickedExtractionBecameDepositRequest(report.OperationId, mission.MissionId, LoadUnitId, 0m,
                Guid.Empty, ExtractionRowId, "KazamControl.Process", ChangeOfCellReopenReason),
            Assert.Single(_warehouse.PickedExtractionsBecameDeposit));
        Assert.Empty(await ORM.PickedExtractionReportOutbox.PendingAsync(10));
    }

    /// <summary>
    /// Il cambio cella fra i magazzini di due ricollocatori (revisione finale della voce 39): la
    /// cella 5 di S e' di R, il magazzino S2 scelto dall'operatore e' di T2, e S2 da R si raggiunge. Il
    /// prelievo dalla cella 5 lo fa R: il cambio cella tiene R come suo ricollocatore, al commit e
    /// quando la rotta si ricostruisce, come la ricostruisce la ricarica dopo un riavvio, che mette il
    /// prelievo nella coda di quel dispositivo (<c>DbInfo.AddMissionToPickUpQueue</c>). Prima diventava
    /// T2, che dalla cella 5 non preleva: dopo un riavvio il prelievo finiva nella coda di T2, e la
    /// missione si perdeva senza una parola.
    /// </summary>
    [Fact]
    public async Task AChangeOfCellToTheStorageOfAnotherRelocatorIsPickedUpByTheRelocatorOfItsCell()
    {
        var mission = await AnExtractionInItsCellWithTheStorageS2();
        _plant["S2"].RelocateItemId = _plant.Device("T2").ItemGuid;

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S2"].ItemGuid);

        Assert.Equal(MissionDestinationOutcome.Changed, result.Outcome);
        Assert.Equal(MissionTypes.ChangeStoreLocation, mission.MissionType);
        Assert.Equal(_plant["R"].ItemGuid, mission.RelocateItemId);

        await mission.BuildPathToDestination();

        Assert.Equal(MissionTypes.ChangeStoreLocation, mission.MissionType);
        Assert.Equal(_plant["R"].ItemGuid, mission.RelocateItemId);
    }

    /// <summary>
    /// La missione esce dal motore mentre il gestionale risponde sull'indirizzo della cella 6 proposta,
    /// dopo la domanda e prima delle segnalazioni: il gestionale non sente ne' che la missione lascia
    /// l'assegnazione ne' che l'UDC esce dalla coda di estrazione, e il cambio e' rifiutato con
    /// «missione non trovata». Fino alla revisione finale della voce 39 l'ultima guardia stava prima
    /// di quelle domande, e le segnalazioni, che non si ritirano, partivano per una missione che non
    /// c'era piu'.
    /// </summary>
    [Fact]
    public async Task AMissionThatLeavesBeforeTheReportsIsNotReported()
    {
        var mission = await AnExtractionInItsCellWithTheStorageS2();
        var asked = false;
        var removed = false;
        _warehouse.Before = async operation =>
        {
            if (operation == nameof(IWarehouse.ReserveStorageAsync))
            {
                // La descrizione della cella 6 e' gia' nella memoria della missione: dimenticata, la
                // lettura dell'indirizzo dopo la domanda la chiede di nuovo, ed e' li' che la missione esce.
                asked = true;
                mission.ClearWarehouseCaches();
            }
            else if (asked && !removed && operation == nameof(IWarehouse.DescribeCellAsync))
            {
                removed = true;
                Assert.True(await ActiveMissions.Instance.Remove(mission, releaseStorageReservation: false));
                _warehouse.MissionsLeftAssignmentUnserved.Clear();
                _warehouse.LoadUnitsLeftExtractionQueue.Clear();
            }
        };

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S2"].ItemGuid);

        Assert.True(removed);
        Assert.Equal(MissionDestinationOutcome.MissionNotFound, result.Outcome);
        Assert.Empty(_warehouse.MissionsLeftAssignmentUnserved);
        Assert.Empty(_warehouse.LoadUnitsLeftExtractionQueue);
        Assert.Equal([false], _warehouse.StorageRequests.Select(request => request.AssignPosition));
    }

    /// <summary>
    /// La chiusura dopo che una cella e' stata prenotata davvero: la domanda senza prenotare propone
    /// la cella 6 di S2, ma la prenotazione, dopo le segnalazioni, da' la cella 99 della porta Z99, che
    /// nessun magazzino ha. Il cambio e' rifiutato e la missione si chiude, e la cella 99 torna al
    /// gestionale con lei. Fino alla revisione finale della voce 39 nessuna prova chiudeva una missione
    /// che avesse davvero una cella prenotata (la prova accanto ha «nessuna cella» alla prenotazione).
    /// </summary>
    [Fact]
    public async Task AChangeOfCellClosedAfterACellWasReservedGivesTheCellBack()
    {
        var mission = await AnExtractionInItsCellWithTheStorageS2();
        var reservations = 0;
        _warehouse.Before = operation =>
        {
            if (operation == nameof(IWarehouse.ReserveStorageAsync) && ++reservations == 2)
                _warehouse.StorageReservations[LoadUnitId] = new StorageReservation(Guid.Empty,
                    StorageReservationOutcome.CellFound, 99, "Z99", Partition: "0993", StorageDepth: 150,
                    NeedsCompaction: false);
            return Task.CompletedTask;
        };

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S2"].ItemGuid);

        Assert.Equal(2, reservations);
        Assert.Equal([false, true], _warehouse.StorageRequests.Select(request => request.AssignPosition));
        Assert.NotEqual(MissionDestinationOutcome.Changed, result.Outcome);
        Assert.True(result.MissionClosed);
        Assert.Null(ActiveMissions.Instance[mission.MissionId]);
        Assert.False(_warehouse.StorageReservationsByMission.ContainsKey(mission.MissionId));
    }

    /// <summary>
    /// Chiudendo la missione, un dispositivo non la lascia: la sua lista delle missioni prese con
    /// altre cade (qui non c'e', come se il ciclo del gruppo l'avesse cambiata nel mezzo). La chiusura
    /// prosegue: la missione esce dalle attive, e il rifiuto dice che e' chiusa. Fino alla revisione
    /// finale della voce 39 l'eccezione fermava la chiusura, e la missione restava ritirata e attiva,
    /// con estrazione e assegnazione gia' azzerate.
    /// </summary>
    [Fact]
    public async Task AClosureGoesOnWhenADeviceFailsToLetGoOfTheMission()
    {
        var mission = await AnExtractionInItsCellWithTheStorageS2();
        _plant.Device("D").LUsToPick = null!;
        NoCellAtTheReservation();

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S2"].ItemGuid);

        Assert.Equal(MissionDestinationOutcome.NoStorageCell, result.Outcome);
        Assert.True(result.MissionClosed);
        Assert.Null(ActiveMissions.Instance[mission.MissionId]);
        Assert.Empty(await ExtractionQueueQueries.ListAsync(processed: null, loadUnitId: LoadUnitId, take: 10));
    }

    /// <summary>
    /// Dopo un riavvio l'estrazione e' attiva e nella coda di prelievo di R. R la prende in carico e
    /// chiede al gestionale se l'UDC e' nella cella (<c>VerifyPickUp</c>); mentre aspetta la risposta
    /// l'operatore la manda in S2, e il cambio cella, rifiutato dopo le segnalazioni (alla prenotazione
    /// nessuna cella), la chiude. Quando il gestionale risponde a R, la missione non torna fra le
    /// attive, R non comincia il prelievo e non la manda al navigatore. Prima tornava (primo giro di
    /// correzioni): un'estrazione verso Y senza riga di coda ne' assegnazione, con la sua riga di nuovo
    /// nel database del WCS, e R la prelevava per una missione che il gestionale non aveva piu'.
    /// </summary>
    /// <remarks>In marcia: a motore fermo le attive non riprendono niente, e la prova non vedrebbe il ritorno.</remarks>
    [Fact]
    public async Task AMissionClosedWhileItsPickUpWasBeingVerifiedDoesNotComeBack()
    {
        var mission = await AnExtractionInItsCellWithTheStorageS2();
        var picker = TheRelocatorHasItInItsQueueAfterARestart(mission);

        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        NoCellAtTheReservation(async operation =>
        {
            if (operation != nameof(IWarehouse.FindLoadUnitAsync) || reached.Task.IsCompleted) return;

            reached.TrySetResult();
            await gate.Task;
        });
        Navigator.NavStatus = NavigatorStatusTypes.Run;

        var take = TheRelocatorTakesFromItsQueue(picker);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Same(mission, picker.LUtoPick);

        var result = await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S2"].ItemGuid);
        Assert.True(result.MissionClosed);

        gate.SetResult();
        await take.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Null(ActiveMissions.Instance[mission.MissionId]);
        Assert.Null(picker.LUtoPick);
        Assert.Equal(Guid.Empty, picker.RunningStepId);
        Assert.Null(RoutesToBuild.Instance[mission.MissionId]);
        Assert.Empty(mission.MissionSteps);
    }

    /// <summary>
    /// La stessa estrazione nella coda di prelievo di R dopo un riavvio; il cambio cella rifiutato dopo
    /// le segnalazioni la chiude, e la rimozione aspetta il gestionale per restituire la prenotazione.
    /// In quel momento R l'ha gia' lasciata: la sua coda e' vuota, e un giro della coda non prende
    /// niente. Prima la coda si svuotava dopo la rimozione (primo giro di correzioni): R la prendeva
    /// durante l'attesa e, dopo la sua domanda al gestionale, la rimetteva fra le attive.
    /// </summary>
    [Fact]
    public async Task WhileTheClosingRemovalWaitsForTheWarehouseTheRelocatorHasNothingToTake()
    {
        var mission = await AnExtractionInItsCellWithTheStorageS2();
        var picker = TheRelocatorHasItInItsQueueAfterARestart(mission);

        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        NoCellAtTheReservation(async operation =>
        {
            // La restituzione della prenotazione che fa la rimozione: la missione e' gia' fuori dalle attive.
            if (operation != nameof(IWarehouse.ReleaseStorageReservationAsync) || reached.Task.IsCompleted ||
                ActiveMissions.Instance[mission.MissionId] != null)
                return;

            reached.TrySetResult();
            await gate.Task;
        });
        Navigator.NavStatus = NavigatorStatusTypes.Run;

        var change = MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S2"].ItemGuid);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, picker.QueuedLuToPickUpCount);
        await TheRelocatorTakesFromItsQueue(picker).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Null(picker.LUtoPick);

        gate.SetResult();
        Assert.True((await change.WaitAsync(TimeSpan.FromSeconds(30))).MissionClosed);
        Assert.Null(ActiveMissions.Instance[mission.MissionId]);
        Assert.Equal(Guid.Empty, picker.RunningStepId);
    }

    // ---------------------------------------------------------------------------------------
    // Il cambio cella: la profondita' del prelievo e quella del deposito
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// L'UDC 42 sta alla profondita' 2400 della cella 5 di S, che ha le partizioni a 1200 e a 2400, e R
    /// converte la profondita' in partizione; il gestionale prenota per lei la cella 6 di S2 alla
    /// profondita' 1200. Dopo il cambio cella R preleva dalla partizione a 2400, dove l'UDC sta; con
    /// l'UDC su R la profondita' e' quella della cella prenotata, che il deposito legge. Prima il commit
    /// scriveva subito 1200 (primo giro di correzioni), e R prelevava dalla partizione a 1200 della
    /// cella 5: un altro posto, vuoto o con un'altra UDC.
    /// </summary>
    [Fact]
    public async Task AChangeOfCellPicksTheLoadUnitUpFromTheDepthItIsAt()
    {
        var (mission, field) = await AnExtractionAtDepth2400WithTheStorageS2();

        Assert.Equal(MissionDestinationOutcome.Changed,
            (await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S2"].ItemGuid)).Outcome);
        Assert.NotNull(_plant["R"].StepExecutor);
        await _plant["R"].StepExecutor!.StartPickUpLu(mission);

        var pickUp = Assert.Single(field.Movements);
        Assert.Equal(("0051", "0052", "0054"), (pickUp.Asi, pickUp.Subitem, pickUp.Partition));
        Assert.Equal(2400, mission.StorageDepth);
        Assert.Equal(1200, mission.StorageDepthAfterPickUp);

        // Il prelievo e' fatto: l'UDC e' su R.
        mission.PositionItemId = _plant["R"].ItemGuid;
        await mission.PositionWork;

        Assert.Equal(1200, mission.StorageDepth);
        Assert.Null(mission.StorageDepthAfterPickUp);
    }

    /// <summary>
    /// La cella 6 prenotata per il cambio cella si perde prima del prelievo, e la porta si lascia
    /// (<c>PathMission.DropStorageCell</c>): la profondita' in attesa se ne va con la cella, e quella a
    /// cui l'UDC sta resta per il prelievo. Prima la cella persa azzerava la profondita' (primo giro di
    /// correzioni), e R avrebbe cercato nella cella 5 la partizione della profondita' zero.
    /// </summary>
    [Fact]
    public async Task ACellLostBeforeThePickUpLeavesTheDepthTheLoadUnitIsAt()
    {
        var (mission, _) = await AnExtractionAtDepth2400WithTheStorageS2();
        Assert.Equal(MissionDestinationOutcome.Changed,
            (await MissionGateway.ChangeDestinationAsync(mission.MissionId, _plant["S2"].ItemGuid)).Outcome);

        Assert.Equal(PathMission.OperatorChangeStart.Begun, mission.TryBeginOperatorChange());
        try
        {
            Assert.True(mission.DropStorageCell());
        }
        finally
        {
            mission.EndOperatorChange();
        }

        Assert.True(mission.StorageCellLost);
        Assert.Equal(2400, mission.StorageDepth);
        Assert.Null(mission.StorageDepthAfterPickUp);
    }

    /// <summary>Una delle segnalazioni del cambio cella: la missione lascia l'assegnazione, l'UDC esce dalla coda.</summary>
    private static bool TheChangeOfCellReport(string call) =>
        call is nameof(IWarehouse.ReportMissionLeftAssignmentUnservedAsync)
            or nameof(IWarehouse.ReportLoadUnitLeftExtractionQueueAsync);

    /// <summary>
    /// Il guasto come lo solleva il client vero (<c>HttpWarehouse</c>): «refused», la connessione
    /// rifiutata prima di partire; «rejected», un 400; «timeout», il tempo scaduto a richiesta partita;
    /// «mute», l'eccezione senza causa, che non dice se la richiesta e' arrivata.
    /// </summary>
    private static Exception Fault(string operation, string kind) => kind switch
    {
        "refused" => new WarehouseUnavailableException(operation,
            new HttpRequestException(HttpRequestError.ConnectionError, "connessione rifiutata")),
        "rejected" => FakeWarehouse.Rejection(operation),
        "timeout" => new WarehouseUnavailableException(operation, new TaskCanceledException("tempo scaduto")),
        "mute" => new WarehouseUnavailableException(operation),
        "5xx" => new WarehouseUnavailableException(operation,
            new HttpRequestException("errore del gestionale", null, System.Net.HttpStatusCode.InternalServerError)),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    /// <summary>La riga della missione nel database del WCS, letta adesso; <c>null</c> se non c'e'.</summary>
    private Mission? TheRowOf(PathMission mission)
    {
        using var context = _database.CreateContext();
        return context.Missions.SingleOrDefault(row => row.MissionId == mission.MissionId);
    }

    private static bool TheReservationOrAReport(string call) =>
        call is nameof(IWarehouse.ReserveStorageAsync) or nameof(IWarehouse.ReportMissionLeftAssignmentUnservedAsync)
            or nameof(IWarehouse.ReportLoadUnitLeftExtractionQueueAsync);

    /// <summary>
    /// Alla prenotazione, la seconda domanda dopo quella senza prenotare, il gestionale non ha piu' una
    /// cella per l'UDC. <paramref name="also"/>, se c'e', gira davanti a ogni operazione.
    /// </summary>
    private void NoCellAtTheReservation(Func<string, Task>? also = null)
    {
        var reservations = 0;
        _warehouse.Before = operation =>
        {
            if (operation == nameof(IWarehouse.ReserveStorageAsync) && ++reservations == 2)
                _warehouse.StorageReservations[LoadUnitId] = new StorageReservation(Guid.Empty,
                    StorageReservationOutcome.NoFreeCells, 0, string.Empty, string.Empty, 0, NeedsCompaction: false);
            return also?.Invoke(operation) ?? Task.CompletedTask;
        };
    }

    /// <summary>
    /// Come dopo un riavvio (<c>DbInfo.AddMissionToPickUpQueue</c>): l'estrazione, attiva, e' nella coda
    /// di prelievo di R, che e' in ripresa. Y e' pronto a ricevere, e il gestionale trova l'UDC nella
    /// cella 5 e sceglie la porta A5 senza ostacoli: R la puo' prendere.
    /// </summary>
    private AutomationDevice TheRelocatorHasItInItsQueueAfterARestart(PathMission mission)
    {
        var picker = _plant["R"];
        picker.EnqueueLuToPickUp(mission);
        picker.Status = DeviceStatusTypes.Recovering;
        _plant["Y"].Status = DeviceStatusTypes.Ready;
        _warehouse.LoadUnitSearchResults[LoadUnitId] = new LoadUnitSearchResult(LoadUnitId, PickUpCellId);
        _warehouse.PickUpDoorChoices[LoadUnitId] = new PickUpDoorChoice(CellDoor.A, PickUpDoorCode, 0, []);
        return picker;
    }

    /// <summary>
    /// Un giro della coda di prelievo del dispositivo (<c>ExecuteQueueLuToPickUp</c>, che il suo
    /// <c>ExecuteStep</c> chiama): la presa in carico, la domanda al gestionale, le attive e il prelievo.
    /// </summary>
    private static Task TheRelocatorTakesFromItsQueue(AutomationDevice picker)
    {
        var method = typeof(Wcs.Engine.Items.Managers.AutomationDeviceStepExecutor).GetMethod("ExecuteQueueLuToPickUp",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        Assert.NotNull(picker.StepExecutor);
        return (Task)method!.Invoke(picker.StepExecutor, null)!;
    }

    /// <summary>Gli step della missione come «da->a», in ordine alfabetico.</summary>
    private string[] StepsOf(PathMission mission) =>
        [.. mission.MissionSteps.Select(s => $"{_plant.NameOf(s.FromNodeId)}->{_plant.NameOf(s.ToNodeId)}").Order()];

    // ---------------------------------------------------------------------------------------
    // Scene
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Il magazzino S, con la porta A5 sulla cella 5, collegato a R, che preleva; R collegato a X e
    /// a Y. Una missione con l'UDC nella cella 5, senza posizione (la sorgente e' S), diretta a Y, con
    /// lo step di prelievo S->R pianificato; il navigatore e' fermo.
    /// </summary>
    private async Task<(PathMission Mission, MissionStep Step)> AMissionToBePickedFromS()
    {
        var storage = _plant.Device("S", DeviceTypes.Storage);
        var picker = _plant.Device("R");
        foreach (var name in new[] { "X", "Y" }) _plant.Device(name);
        _plant.Link("S", "R");
        _plant.Link("R", "X");
        _plant.Link("R", "Y");
        picker.RecordStorageLength = RecordStorageTypes.FromPlc;

        await PlantPersistence.SaveDoorMappingsAsync(storage.ItemGuid,
            [new DoorMapping { ItemId = storage.ItemGuid, DoorCode = PickUpDoorCode, CellId = PickUpCellId, Side = "A" }]);
        _warehouse.Cells[PickUpCellId] = Cell(PickUpCellId, PickUpDoorCode);
        _warehouse.CellPartitionMaps[PickUpCellId] = new CellPartitionMap(PickUpCellId, "0051", "0052",
            AsiDoorB: string.Empty, SubitemDoorB: string.Empty, [new CellPartition("0053", -1)]);

        var mission = new PathMission
        {
            LuId = LoadUnitId,
            PickUpDoorCode = PickUpDoorCode,
            SourceItemId = storage.ItemGuid,
            DestinationItemId = _plant["Y"].ItemGuid,
            MissionType = MissionTypes.Extraction,
            OperationType = OperationTypes.Outbound
        };
        Assert.Equal(NavigatorStatusTypes.Recover, Navigator.NavStatus);
        ActiveMissions.Instance.Add(mission, buildRoute: false);
        Assert.Same(mission, ActiveMissions.Instance[mission.MissionId]);

        var step = _plant.PlannedStep(mission, "S", "R");
        Assert.Same(step, picker.LuManager?.StepToDoDestination(mission));
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        _warehouse.Calls.Clear();
        return (mission, step);
    }

    /// <summary>
    /// <see cref="AMissionToBePickedFromS"/>, con R ricollocatore di S e di un secondo magazzino S2,
    /// collegato a R, con la porta A6 sulla cella 6 e il suo indirizzo di campo; il gestionale ha la
    /// cella 6 per l'UDC. L'estrazione porta l'assegnazione (GUID, e la riga 77) e la sua riga nella
    /// coda di estrazione del WCS, che serve l'assegnazione 77 e la riga di estrazione 901.
    /// </summary>
    private async Task<PathMission> AnExtractionInItsCellWithTheStorageS2()
    {
        var (mission, _) = await AMissionToBePickedFromS();
        var picker = _plant["R"];
        var other = _plant.Device("S2", DeviceTypes.Storage);
        _plant.Link("R", "S2");
        _plant["S"].RelocateItemId = picker.ItemGuid;
        other.RelocateItemId = picker.ItemGuid;

        await PlantPersistence.SaveDoorMappingsAsync(other.ItemGuid,
            [new DoorMapping { ItemId = other.ItemGuid, DoorCode = StorageDoorCode, CellId = StorageCellId, Side = "A" }]);
        _warehouse.Cells[StorageCellId] = Cell(StorageCellId, StorageDoorCode);
        _warehouse.CellPartitionMaps[StorageCellId] = new CellPartitionMap(StorageCellId, "0061", "0062",
            AsiDoorB: string.Empty, SubitemDoorB: string.Empty, [new CellPartition("0063", -1)]);
        _warehouse.StorageReservations[LoadUnitId] = new StorageReservation(Guid.Empty,
            StorageReservationOutcome.CellFound, StorageCellId, StorageDoorCode, Partition: "0063", StorageDepth: 150,
            NeedsCompaction: false);

        await ORM.ExtractionQueue.UpsertAsync(new ExtractionQueueItem
        {
            LoadUnitId = LoadUnitId, AssignmentId = AssignmentId, ExtractionRowId = ExtractionRowId,
            CellId = PickUpCellId, SourceItemId = _plant["S"].ItemGuid
        });
        mission.ExtractionQueueId =
            Assert.Single(await ExtractionQueueQueries.ListAsync(processed: null, loadUnitId: LoadUnitId, take: 10)).Id;
        mission.GuidAssignmentLu = Assignment;
        mission.LuNoteAssignmentId = AssignmentId;

        Assert.True(mission.LoadUnitIsInItsStorageCell());
        _warehouse.Calls.Clear();
        return mission;
    }

    /// <summary>
    /// <see cref="AnExtractionInItsCellWithTheStorageS2"/>, con l'UDC alla profondita' 2400 della cella
    /// 5, che ha le partizioni 0053 a 1200 e 0054 a 2400; R converte la profondita' in partizione e
    /// manda i suoi comandi al campo che li registra. Il gestionale prenota la cella 6 di S2 alla
    /// profondita' 1200.
    /// </summary>
    private async Task<(PathMission Mission, RecordingConnection Field)> AnExtractionAtDepth2400WithTheStorageS2()
    {
        var mission = await AnExtractionInItsCellWithTheStorageS2();
        var picker = _plant["R"];
        var field = new RecordingConnection();
        picker.RecordStorageLength = RecordStorageTypes.ConvertDepthToPartition;
        picker.DeviceConnection = field;

        _warehouse.CellPartitionMaps[PickUpCellId] = new CellPartitionMap(PickUpCellId, "0051", "0052",
            AsiDoorB: string.Empty, SubitemDoorB: string.Empty,
            [new CellPartition("0053", 1200), new CellPartition("0054", 2400)]);
        _warehouse.StorageReservations[LoadUnitId] = new StorageReservation(Guid.Empty,
            StorageReservationOutcome.CellFound, StorageCellId, StorageDoorCode, Partition: "0063", StorageDepth: 1200,
            NeedsCompaction: false);
        mission.StorageDepth = 2400;
        return (mission, field);
    }

    /// <summary>Il campo nelle prove del prelievo: non parla con nessun PLC, tiene da dove si preleva.</summary>
    private sealed class RecordingConnection : IConnection
    {
        internal List<DataAsi> Movements { get; } = [];

        public decimal Id => 1;
        public bool Managed { get; set; }

        // Il motore non li solleva in queste prove: servono a soddisfare l'interfaccia.
#pragma warning disable CS0067
        public event Handlers.AsiEventEventHandler? AsiEvent;
        public event Handlers.AsiMovementIsCompletedEventHandler? AsiMovementIsCompleted;
        public event Handlers.AsiRequestsLuInfoEventHandler? AsiRequestsLuInfo;
        public event Handlers.AsiSendLuDataEventHandler? AsiSendLuData;
        public event Handlers.LuUnloadedFromAsiEventHandler? LuUnloadedFromAsi;
        public event Handlers.NewLuOnAsiEventHandler? NewLuOnAsi;
#pragma warning restore CS0067

        public void CloseConnection() { }
        public void Dispose() { }

        public void MoveLuFromAsiToAsi(RunAsi sourceItem, DataAsi actionItemPerformer, DataAsi fromAsi, DataAsi toAsi,
            Guid stepGuid, decimal luId, int luHandlingMode = 0) => Movements.Add(fromAsi);

        public void MoveLuFromAsiToAsi(RunAsi sourceItem, DataAsi actionItemPerformer, Guid groupedMovementId,
            List<MovementLuDataCommand> loadUnitsToMove, int luHandlingMode = 0) { }

        public void OpenConnection() { }
        public void RequestLuDataFromAsi(RunAsi item) { }
        public void SendCommandToAsi(RunAsi destinationItem, int commandCode, List<string?> parameters) { }
        public void SendLuDataToAsi(RunAsi item, LuInfoDataCommand value) { }
        public void ShowMessageToAsi(RunAsi destinationItem, string message) { }
    }

    /// <summary>
    /// Il gruppo G con il membro M1; M1 collegato a N, a X e al magazzino SG (porta A6 sulla cella 6,
    /// non vuota, cosi' che la verifica di allocazione del gruppo chieda al gestionale e poi si
    /// fermi). Su M1 una missione diretta a <paramref name="destination"/>, con lo step M1->destinazione
    /// pianificato; M1 e N sono pronti, il navigatore e' fermo.
    /// </summary>
    /// <param name="inTheGroup">Falso: M1 e' un dispositivo singolo, e G non ha membri.</param>
    private async Task<(PathMission Mission, MissionStep Step)> AMissionOnAMemberOfG(string destination,
        bool inTheGroup = true)
    {
        var group = _plant.Device("G", DeviceTypes.GroupOfAutomationDevices);
        var member = _plant.Device("M1");
        foreach (var name in new[] { "N", "X" }) _plant.Device(name);
        var storage = _plant.Device("SG", DeviceTypes.Storage);
        _plant.Link("M1", "N");
        _plant.Link("M1", "X");
        _plant.Link("M1", "SG");

        group.GroupItemId = 93;
        group.RecordStorageLength = RecordStorageTypes.FromPlc;
        if (inTheGroup) member.BelongingGroupId = group.GroupItemId;
        Assert.Same(inTheGroup ? group : null, member.BelongedGroup);

        await PlantPersistence.SaveDoorMappingsAsync(storage.ItemGuid,
            [new DoorMapping { ItemId = storage.ItemGuid, DoorCode = StorageDoorCode, CellId = StorageCellId, Side = "A" }]);
        _warehouse.Cells[StorageCellId] = Cell(StorageCellId, StorageDoorCode);
        _warehouse.CellContents[StorageCellId] = [7];
        _warehouse.CellPartitionMaps[StorageCellId] = new CellPartitionMap(StorageCellId, "0061", "0062",
            AsiDoorB: string.Empty, SubitemDoorB: string.Empty, [new CellPartition("0063", -1)]);

        Assert.Equal(NavigatorStatusTypes.Recover, Navigator.NavStatus);
        var mission = new PathMission
        {
            PositionItemId = member.ItemGuid,
            LuId = LoadUnitId,
            MissionType = MissionTypes.MovingToItem,
            OperationType = OperationTypes.Outbound
        };
        ActiveMissions.Instance.Add(mission, buildRoute: false);
        Assert.Same(mission, member.LoadedLu);
        await mission.PositionWork;

        mission.DestinationItemId = _plant[destination].ItemGuid;
        if (destination == "SG") mission.StorageDoorCode = StorageDoorCode;
        var step = _plant.PlannedStep(mission, "M1", destination);
        member.Status = DeviceStatusTypes.Ready;
        _plant["N"].Status = DeviceStatusTypes.Ready;
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        _warehouse.Calls.Clear();
        return (mission, step);
    }

    /// <summary>
    /// La missione su M1 va verso SG, scelto in precedenza dall'operatore, con la porta A6 e senza
    /// rotta; il gruppo G riverifica le celle costruendo la rotta verso il magazzino.
    /// </summary>
    private async Task<PathMission> AMissionOnM1ChosenForSGWithoutARoute()
    {
        var (mission, _) = await AMissionOnAMemberOfG("SG");
        mission.RemoveMissionSteps();
        Assert.True(mission.SetOperatorDestination(_plant["SG"].ItemGuid));
        mission.StorageDoorCode = StorageDoorCode;
        _plant["G"].LookWhereAllocateLu = LookWhereAllocateLuTypes.BuildRouteToStore;
        return mission;
    }

    /// <summary>
    /// La missione su M1 va nel magazzino SG, scelto in precedenza dall'operatore, con la cella 6
    /// (porta A6) che il gestionale le tiene e lo step M1->SG del deposito. La cella l'ha prenotata il
    /// motore per lei (<c>PathMission.StorageCellReserved</c>), come il flusso del cambio dell'operatore:
    /// senza il segno una missione dell'operatore con una porta su un membro di un gruppo e' una copia, a
    /// cui l'operatore ha cambiato solo il gateway (<c>MissionGateway.IsACopy</c>, risposta del 9/10 alla
    /// seconda domanda del compito 17), e il suo deposito non chiede la cella.
    /// </summary>
    private async Task<(PathMission Mission, MissionStep Step)> AMissionOnM1ChosenForSGWithItsCell()
    {
        var (mission, step) = await AMissionOnAMemberOfG("SG");
        Assert.True(mission.SetOperatorDestination(_plant["SG"].ItemGuid));
        mission.StorageCellReserved = true;
        _warehouse.StorageReservationsByMission[mission.MissionId] = new StorageReservation(mission.MissionId,
            StorageReservationOutcome.CellFound, StorageCellId, StorageDoorCode, Partition: "0063", StorageDepth: 150,
            NeedsCompaction: false);
        Assert.Equal(StorageDoorCode, mission.StorageDoorCode);
        Assert.Same(step, Assert.Single(mission.MissionSteps));
        _warehouse.Calls.Clear();
        return (mission, step);
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

    /// <summary>
    /// La missione su M1 va verso N, scelto in precedenza dall'operatore, con lo step M1->N e il
    /// gateway X, collegato a N. (Con N gia' dell'operatore la ricerca delle celle del gruppo, se parte,
    /// finisce senza chiedere niente al gestionale.)
    /// </summary>
    private async Task<(PathMission Mission, MissionStep Step)> AMissionOnM1ChosenForNThroughX()
    {
        var (mission, step) = await AMissionOnAMemberOfG("N");
        _plant.Link("X", "N");
        Assert.True(mission.SetOperatorDestination(_plant["N"].ItemGuid));
        Assert.Same(step, Assert.Single(mission.MissionSteps));
        mission.Gateway = _plant["X"].ItemGuid;
        return (mission, step);
    }

    /// <summary>
    /// La missione su M1 va nel magazzino SG scelto dall'operatore, come la lascia il commit del suo
    /// cambio: la cella 6 (porta A6) prenotata per lei, con l'indirizzo 0061/0062/0063 e la profondita'
    /// 150, il gateway X (collegato a SG) e lo step M1->SG del deposito. SG ha anche la cella 8 (porta
    /// A8, indirizzo 0081/0082), che il gestionale propone per l'UDC (<see cref="TheWarehouseHasCell8"/>).
    /// Il punto di scarto per mancanza di celle e' R, collegato a M1: quello del gruppo G, o di M1 se e'
    /// un dispositivo singolo.
    /// </summary>
    /// <param name="inTheGroup">Falso: M1 e' un dispositivo singolo.</param>
    private async Task<(PathMission Mission, MissionStep Step)> AMissionOnM1WhoseCellInSGThePlcRefuses(
        bool inTheGroup = true)
    {
        var (mission, step) = await AMissionOnAMemberOfG("SG", inTheGroup);
        var storage = _plant["SG"];
        Assert.True(mission.SetOperatorDestination(storage.ItemGuid));
        _warehouse.StorageReservationsByMission[mission.MissionId] = new StorageReservation(mission.MissionId,
            StorageReservationOutcome.CellFound, StorageCellId, StorageDoorCode, Partition: "0063", StorageDepth: 150,
            NeedsCompaction: false);
        mission.DestinationAsi = "0061";
        mission.DestinationSubitem = "0062";
        mission.DestinationPartition = "0063";
        mission.StorageDepth = 150;
        mission.StorageCellReserved = true;
        _plant.Link("X", "SG");
        mission.Gateway = _plant["X"].ItemGuid;

        await PlantPersistence.SaveDoorMappingsAsync(storage.ItemGuid,
        [
            new DoorMapping { ItemId = storage.ItemGuid, DoorCode = StorageDoorCode, CellId = StorageCellId, Side = "A" },
            new DoorMapping { ItemId = storage.ItemGuid, DoorCode = OtherDoorCode, CellId = OtherCellId, Side = "A" },
            new DoorMapping { ItemId = storage.ItemGuid, DoorCode = "A9", CellId = 9, Side = "A" }
        ]);
        _warehouse.Cells[OtherCellId] = Cell(OtherCellId, OtherDoorCode);
        _warehouse.CellPartitionMaps[OtherCellId] = new CellPartitionMap(OtherCellId, "0081", "0082",
            AsiDoorB: string.Empty, SubitemDoorB: string.Empty, [new CellPartition("0083", -1)]);
        TheWarehouseHasCell8();

        _plant.Device("R");
        _plant.Link("M1", "R");
        (inTheGroup ? _plant["G"] : _plant["M1"]).RejectItemDestinationAllocateLu = _plant["R"].ItemGuid;

        Assert.Equal(StorageDoorCode, mission.StorageDoorCode);
        Assert.Same(step, Assert.Single(mission.MissionSteps));
        _warehouse.Calls.Clear();
        return (mission, step);
    }

    /// <summary>
    /// Sul membro M2 del gruppo G, collegato a SG, una missione della configurazione (l'UDC 43) verso
    /// SG con il gateway X e lo step M2->SG pianificato; per lei il gestionale ha la cella 9 di SG
    /// (porta A9). Da aggiungere dopo <see cref="AMissionOnM1WhoseCellInSGThePlcRefuses"/>.
    /// </summary>
    private async Task<PathMission> AnUnmarkedMissionOnM2ThroughX()
    {
        var member = _plant.Device("M2");
        member.BelongingGroupId = _plant["G"].GroupItemId;
        _plant.Link("M2", "SG");
        _plant.Link("M2", "X");

        // La ricerca della configurazione (StoreInLinkedCell): il magazzino del gestionale dalla cella
        // collegata di M2, e SG del ricollocatore G.
        member.LinkedCellId = 9;
        _plant["SG"].RelocateItemId = _plant["G"].ItemGuid;
        _warehouse.Cells[9] = Cell(9, "A9");
        _warehouse.CellPartitionMaps[9] = new CellPartitionMap(9, "0091", "0092",
            AsiDoorB: string.Empty, SubitemDoorB: string.Empty, [new CellPartition("0093", -1)]);
        _warehouse.StorageReservations[43] = new StorageReservation(Guid.Empty,
            StorageReservationOutcome.CellFound, 9, "A9", Partition: "0093", StorageDepth: 200, NeedsCompaction: false);

        Navigator.NavStatus = NavigatorStatusTypes.Recover;
        var mission = new PathMission
        {
            PositionItemId = member.ItemGuid,
            LuId = 43,
            MissionType = MissionTypes.MovingToItem,
            OperationType = OperationTypes.Outbound
        };
        ActiveMissions.Instance.Add(mission, buildRoute: false);
        Assert.Same(mission, member.LoadedLu);
        await mission.PositionWork;

        mission.DestinationItemId = _plant["SG"].ItemGuid;
        mission.StorageDoorCode = StorageDoorCode;
        mission.Gateway = _plant["X"].ItemGuid;
        _plant.PlannedStep(mission, "M2", "SG");
        member.Status = DeviceStatusTypes.Ready;
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        Assert.Equal(2, _plant["G"].LUsLoaded.Count);
        _warehouse.Calls.Clear();
        return mission;
    }

    /// <summary>
    /// Sul membro M3 del gruppo G, collegato a SG e a X, una missione della configurazione (l'UDC 44) verso
    /// SG con la porta A9, il gateway X e lo step M3->SG pianificato. Da aggiungere dopo
    /// <see cref="AnUnmarkedMissionOnM2ThroughX"/>.
    /// </summary>
    private async Task<PathMission> AnUnmarkedMissionOnM3ThroughX()
    {
        var member = _plant.Device("M3");
        member.BelongingGroupId = _plant["G"].GroupItemId;
        _plant.Link("M3", "SG");
        _plant.Link("M3", "X");

        Navigator.NavStatus = NavigatorStatusTypes.Recover;
        var mission = new PathMission
        {
            PositionItemId = member.ItemGuid,
            LuId = 44,
            MissionType = MissionTypes.MovingToItem,
            OperationType = OperationTypes.Outbound
        };
        ActiveMissions.Instance.Add(mission, buildRoute: false);
        Assert.Same(mission, member.LoadedLu);
        await mission.PositionWork;

        mission.DestinationItemId = _plant["SG"].ItemGuid;
        mission.StorageDoorCode = "A9";
        mission.Gateway = _plant["X"].ItemGuid;
        _plant.PlannedStep(mission, "M3", "SG");
        member.Status = DeviceStatusTypes.Ready;
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        _warehouse.Calls.Clear();
        return mission;
    }

    /// <summary>Il gestionale ha per l'UDC la cella 8 di SG, porta A8, alla profondita' 300.</summary>
    private void TheWarehouseHasCell8() =>
        _warehouse.StorageReservations[LoadUnitId] = new StorageReservation(Guid.Empty,
            StorageReservationOutcome.CellFound, OtherCellId, OtherDoorCode, Partition: "0083", StorageDepth: 300,
            NeedsCompaction: false);

    /// <summary>Il gestionale non ha celle libere per l'UDC.</summary>
    private void TheWarehouseHasNoFreeCell() =>
        _warehouse.StorageReservations[LoadUnitId] = new StorageReservation(Guid.Empty,
            StorageReservationOutcome.NoFreeCells, 0, string.Empty, string.Empty, 0, NeedsCompaction: false);

    private static CellDescription Cell(decimal cellId, string doorCode) =>
        new(cellId, AreaId: 1, WarehouseId: 1, Depth: 1, Inverted: false, CellLockState.None,
            AllowedLoadUnitTypeId: null, GroupId: null, GroupOrder: 0, doorCode, DoorCodeB: string.Empty,
            DoorCount: 1, MultiDepthStorage: false);
}
