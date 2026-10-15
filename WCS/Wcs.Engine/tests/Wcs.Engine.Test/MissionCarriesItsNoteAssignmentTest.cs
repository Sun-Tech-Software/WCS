using System.Data;
using Helper.Converters;
using Log.Interface;
using Wcs.Engine.Api;
using Wcs.Engine.Collections;
using Wcs.Engine.ORM;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Wcs.Engine.Test.Routing;
using Wcs.Wms.Contracts;
using Wcs.Wms.Contracts.Test;
using Xunit;
using AutomationDevice = Wcs.Engine.Items.AutomationDevice;
using Command = Wcs.Engine.Items.Command;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// Il legame fra la missione e l'assegnazione di nota che sta servendo
/// (<c>PathMission.LuNoteAssignmentId</c>), preso per la via vera: dal lavoro pronto del gestionale
/// (<see cref="PendingNote"/>) alla coda di estrazione, dalla coda alla missione, e da li' alla
/// domanda con cui il gestionale rivaluta l'assegnazione
/// (<see cref="MissionQueries.HoldsLoadUnit"/> con l'esclusione).
/// <para>
/// Perche' la via vera e non il campo scritto a mano: il campo non era valorizzato da nessuno, e
/// l'esclusione - «non contare la missione che porta gia' questa assegnazione» - era quindi sempre
/// falsa. Ogni assegnazione diventata missione veniva scartata e cancellata al giro successivo,
/// cioe' esattamente il guasto che l'esclusione era stata costruita per impedire. Il test che
/// copriva l'esclusione impostava lui <c>LuNoteAssignmentId</c> sulla missione ed era verde:
/// provava il confronto, non il legame. Questo test parte dalla riga pronta e non scrive nessun
/// campo della missione.
/// </para>
/// </summary>
public sealed class MissionCarriesItsNoteAssignmentTest : IDisposable
{
    private const decimal LoadUnitId = 8601m;
    private const decimal ObstacleLoadUnitId = 8602m;
    private const decimal CellId = 8610m;
    private const decimal BufferCellId = 8620m;
    private const decimal AssignmentId = 601m;
    private const decimal NoteProcessingId = 6010m;
    private const string DoorCode = "A601";

    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly RoutingTestPlant _plant = new();
    private readonly FakeWarehouse _warehouse = new();

    public MissionCarriesItsNoteAssignmentTest()
    {
        Navigator.Store = _database;
        Navigator.Warehouse = _warehouse;

        // Dal compito 9 la lavorazione di ciascuna assegnazione non si rilegge piu' dal gestionale:
        // viaggia con l'assegnazione pronta e resta nella coda del WCS, quindi DbInfo.GetLuToExtract
        // non apre nessuna connessione. Dal compito 16 non c'e' nemmeno piu' una connessione da
        // aprire: il motore non referenzia piu' SQL Server. La profondita' di deposito viene dal
        // gestionale finto.
    }

    public void Dispose()
    {
        _plant.Dispose();
        Navigator.Warehouse = _previousWarehouse;
        Navigator.Store = null;
        _database.Dispose();
    }

    /// <summary>
    /// L'impianto minimo su cui il giro dell'estrazione arriva fino in fondo: la porta di prelievo
    /// sta su un magazzino, il suo ricollocatore e' il dispositivo che prende in carico la coda
    /// (ed e' collegato al magazzino, quindi <c>Navigator.GetMissionByLuId</c> ne guarda la coda di
    /// prelievo), e la destinazione e' il dispositivo che serve il polmone.
    /// </summary>
    private async Task<AutomationDevice> APlantWithAQueuedAssignment(IReadOnlyList<decimal> obstacles,
        decimal processingId = NoteProcessingId)
    {
        var storage = _plant.Device("S", DeviceTypes.Storage);
        var relocator = _plant.Device("R");
        var destination = _plant.Device("D");

        storage.RelocateItemId = relocator.ItemGuid;
        _plant.Link("R", "S");

        await PlantPersistence.SaveDoorMappingsAsync(storage.ItemGuid,
            [new DoorMapping { ItemId = storage.ItemGuid, DoorCode = DoorCode, CellId = CellId, Side = "A" }]);

        _warehouse.LoadUnitSearchResults[LoadUnitId] = new LoadUnitSearchResult(LoadUnitId, CellId);
        _warehouse.PickUpDoorChoices[LoadUnitId] =
            new PickUpDoorChoice(CellDoor.A, DoorCode, obstacles.Count, obstacles);
        _warehouse.LoadUnits[LoadUnitId] = Description(LoadUnitId);
        _warehouse.LoadUnits[ObstacleLoadUnitId] = Description(ObstacleLoadUnitId);

        await Navigator.BuildLuExtraction(
        [
            new PendingNote(AssignmentId, Guid.NewGuid(), LoadUnitId, BufferCellId, [1m], destination.ItemGuid, "WS1",
                processingId)
        ]);

        return relocator;
    }

    private static LoadUnitDescription Description(decimal loadUnitId) =>
        new(loadUnitId, $"UDC{loadUnitId}", LoadUnitTypeId: 1, HandlingMode: 0, Tare: 0,
            Height: 0, Width: 0, Depth: 0, Weight: 0);

    /// <summary>Le righe in coda per il ricollocatore, nella forma in cui le legge chi le consuma.</summary>
    private static async Task<List<DataRow>> QueuedRows(AutomationDevice relocator)
    {
        var table = await DbInfo.GetLuToExtract(relocator);
        Assert.NotNull(table);
        return [.. table.Rows.Cast<DataRow>()];
    }

    private static Command AMoveCommand(decimal loadUnitId) => new()
    {
        Date = DateTime.Now,
        CommandType = Command.CommandTypes.MoveLuAutomatic,
        LuId = loadUnitId,
        PickUpDoorCode = DoorCode,
        Status = Command.StatusTypes.Done,
        WorkstationId = "WS1",
        BufferCellId = BufferCellId
    };

    /// <summary>
    /// <b>Il test che conta</b>: la missione nata dall'assegnazione porta quell'assegnazione, quindi
    /// il gestionale che la rivaluta con la propria esclusione sente «no, non e' tenuta da altri» e
    /// non la cancella. Senza il legame scritto al momento della costruzione della missione,
    /// l'esclusione risponderebbe «si'» e l'assegnazione verrebbe cancellata da sotto un movimento
    /// gia' avviato.
    /// </summary>
    [Fact]
    public async Task AMissionBornFromANoteAssignmentCarriesItSoTheWarehouseCanExcludeItsOwn()
    {
        var relocator = await APlantWithAQueuedAssignment([]);

        var row = Assert.Single(await QueuedRows(relocator));
        Assert.Equal(AssignmentId, row.GetValue<decimal>("ID_ASSEGNAZIONE_UDC"));

        // La missione nasce qui, dal consumo della riga: e' il punto di costruzione vero, quello che
        // il ciclo di estrazione del magazzino attraversa (AutomationDeviceStorageManager.ExtractTargetLu).
        Assert.True(await relocator.AddMissionToPickUp(AMoveCommand(LoadUnitId), row, MissionTypes.Extraction),
            "la missione non e' stata costruita: senza missione la domanda del gestionale non prova nulla");

        var mission = Assert.Single(relocator.QueuedLUtoPickUp, m => m != null && m.LuId == LoadUnitId)!;

        // La domanda del gestionale per prima: e' quella che in esercizio decide, e senza il legame
        // risponde «si', e' tenuta» anche all'assegnazione che ha generato la missione.
        Assert.True(MissionQueries.HoldsLoadUnit(LoadUnitId));
        Assert.False(MissionQueries.HoldsLoadUnit(LoadUnitId, excludeAssignmentId: AssignmentId));
        Assert.True(MissionQueries.HoldsLoadUnit(LoadUnitId, excludeAssignmentId: AssignmentId + 1));

        Assert.Equal(AssignmentId, mission.LuNoteAssignmentId);

        // Compito 9: sulla stessa via, e dalla stessa riga di coda, viaggia anche la lavorazione
        // della nota - quella che il motore rileggeva dal gestionale a ogni giro di ogni
        // ricollocatore (DbInfo.ProcessingIdsByAssignment, cancellata) e su cui la missione decide
        // se la nota e' evasa (PathMission.ProcessNotes).
        Assert.Equal(NoteProcessingId, mission.NoteProcessingId);
    }

    /// <summary>
    /// Il contrario: una missione nata per un'altra ragione - il ricollocamento dell'unita' di
    /// carico che ostruisce quella voluta - non porta nessuna assegnazione, e nessuna esclusione la
    /// toglie di mezzo. E' il motivo per cui il legame lo porta solo la riga della UDC voluta:
    /// se lo portassero anche gli ostacoli, il gestionale escluderebbe una missione che non e' la
    /// sua e crederebbe libera un'unita' di carico che il motore sta muovendo.
    /// </summary>
    [Fact]
    public async Task AMissionBornForAnotherReasonCarriesNoAssignmentAndNoExclusionHidesIt()
    {
        var relocator = await APlantWithAQueuedAssignment([ObstacleLoadUnitId]);

        var rows = await QueuedRows(relocator);
        var obstacleRow = Assert.Single(rows, r => r.GetValue<decimal>("ID_UDC") == ObstacleLoadUnitId);
        Assert.Equal(0m, obstacleRow.GetValue<decimal>("ID_ASSEGNAZIONE_UDC"));

        Assert.True(await relocator.AddMissionToPickUp(AMoveCommand(ObstacleLoadUnitId), obstacleRow,
                MissionTypes.ChangeStoreLocation),
            "la missione di ricollocamento non e' stata costruita: senza missione la domanda del gestionale non prova nulla");

        var mission = Assert.Single(relocator.QueuedLUtoPickUp, m => m != null && m.LuId == ObstacleLoadUnitId)!;
        Assert.Equal(0m, mission.LuNoteAssignmentId);

        // Compito 9: e nemmeno la lavorazione, per la stessa ragione - la riga di ricollocamento non
        // serve nessuna nota, e una lavorazione addosso la farebbe dichiarare evasa da un movimento
        // che non la riguarda.
        Assert.Equal(0m, mission.NoteProcessingId);

        Assert.True(MissionQueries.HoldsLoadUnit(ObstacleLoadUnitId));
        Assert.True(MissionQueries.HoldsLoadUnit(ObstacleLoadUnitId, excludeAssignmentId: AssignmentId));
    }

    /// <summary>
    /// L'ostacolo spostato per estrarre l'UDC dietro, come lo costruisce <c>MoveObstacleLu</c>: la porta
    /// di destinazione e' quella della cella che il gestionale ha prenotato prima che la missione
    /// esistesse, senza legarla a nessuna missione. Non e' una prenotazione della missione: la missione
    /// nasce senza il segno della cella sua (<c>PathMission.StorageCellReserved</c>), e il suo
    /// deposito non chiede la cella al gestionale (<c>PathMission.MayStartTheDeposit</c>), che
    /// risponderebbe «non c'e'» e la farebbe rifare altrove (risposta del proprietario del 2/10 alla
    /// domanda del compito 3). La prova di prima lo guardava su una missione scritta a mano, che da
    /// questa via non passava (revisione finale del primo giro della voce 39, C42).
    /// </summary>
    [Fact]
    public async Task AnObstacleMovedAsideDepositsWithoutAskingForItsCell()
    {
        var relocator = await APlantWithAQueuedAssignment([ObstacleLoadUnitId]);
        var obstacleRow = Assert.Single(await QueuedRows(relocator), r => r.GetValue<decimal>("ID_UDC") == ObstacleLoadUnitId);
        var command = AMoveCommand(ObstacleLoadUnitId);
        command.StorageDoorCode = "A9";
        command.BufferCellId = 0;

        Assert.True(await relocator.AddMissionToPickUp(command, obstacleRow, MissionTypes.ChangeStoreLocation));

        var mission = Assert.Single(relocator.QueuedLUtoPickUp, m => m != null && m.LuId == ObstacleLoadUnitId)!;
        Assert.Equal("A9", mission.StorageDoorCode);
        Assert.Equal(_plant["S"].ItemGuid, mission.DestinationItemId);
        Assert.False(mission.StorageCellReserved);
        Assert.False(mission.OperatorDestination);
        Assert.True(mission.MayStartTheDeposit(confirmedOn: null, "prova"));
        Assert.Null(RoutesToBuild.Instance[mission.MissionId]);
    }

    /// <summary>
    /// La combinazione che era la finestra dell'aggiornamento del compito 9: una riga accodata
    /// <b>prima</b> della migrazione <c>AddNoteProcessingIdToExtractionQueue</c> porta
    /// l'assegnazione ma la lavorazione a zero. La missione nasce lo stesso, con l'assegnazione
    /// addosso e la lavorazione a zero.
    /// </summary>
    /// <remarks>
    /// <b>Compito 13</b>: qui c'era l'attesa della riga di registro «la nota non verra' chiusa», e
    /// quell'avviso e' stato cancellato perche' non e' piu' vero. La chiusura della nota non passa
    /// piu' da <c>NoteProcessingId</c>: il gestionale ricava la lavorazione dall'<b>assegnazione</b>
    /// che la missione porta, e una riga senza lavorazione in coda si chiude lo stesso. Lo zero
    /// resta, perche' quel campo governa ancora due decisioni del motore (la missione di gruppo che
    /// non mescola lavorazioni diverse, e la scelta del polmone), ed e' l'assegnazione - non lui -
    /// che questo test guarda.
    /// </remarks>
    [Fact]
    public async Task AnAssignmentQueuedWithoutItsProcessingStillCarriesItsAssignment()
    {
        var relocator = await APlantWithAQueuedAssignment([ObstacleLoadUnitId], processingId: 0m);

        var rows = await QueuedRows(relocator);

        var obstacleRow = Assert.Single(rows, r => r.GetValue<decimal>("ID_UDC") == ObstacleLoadUnitId);
        Assert.True(await relocator.AddMissionToPickUp(AMoveCommand(ObstacleLoadUnitId), obstacleRow,
            MissionTypes.ChangeStoreLocation));

        var row = Assert.Single(rows, r => r.GetValue<decimal>("ID_UDC") == LoadUnitId);
        Assert.Equal(AssignmentId, row.GetValue<decimal>("ID_ASSEGNAZIONE_UDC"));
        Assert.True(row.IsNull("ID_LAVORAZIONE"));

        Assert.True(await relocator.AddMissionToPickUp(AMoveCommand(LoadUnitId), row, MissionTypes.Extraction),
            "la missione non e' stata costruita");

        var mission = Assert.Single(relocator.QueuedLUtoPickUp, m => m != null && m.LuId == LoadUnitId)!;

        Assert.Equal(0m, mission.NoteProcessingId);
        Assert.Equal(AssignmentId, mission.LuNoteAssignmentId);

        // L'ostacolo resta il caso contrario: nessuna assegnazione, quindi niente da chiudere.
        var obstacle = Assert.Single(relocator.QueuedLUtoPickUp, m => m != null && m.LuId == ObstacleLoadUnitId)!;
        Assert.Equal(0m, obstacle.LuNoteAssignmentId);
        Assert.Equal(0m, obstacle.NoteProcessingId);
    }
}
