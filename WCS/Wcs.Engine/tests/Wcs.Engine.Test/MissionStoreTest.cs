using Wcs.Engine.Items;
using Wcs.Engine.Persistence;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// Le missioni sul database del WCS: la mappa nei due versi, l'inserimento che diventa
/// aggiornamento, la cancellazione, la ricarica nell'ordine di creazione.
/// </summary>
/// <remarks>
/// Il database e' SQLite in memoria con lo schema delle migrazioni: lo stesso che gira in
/// esercizio, senza un server. Il navigatore e' in <c>Recover</c> per rendere inerte il vecchio
/// ORM su SQL Server, che i setter di <c>PathMission</c> chiamano ancora.
/// </remarks>
public sealed class MissionStoreTest : IDisposable
{
    private readonly NavigatorStatusTypes _previousStatus = Navigator.NavStatus;
    private readonly WcsDatabase _database = WcsDatabase.InMemory();

    public MissionStoreTest()
    {
        Navigator.NavStatus = NavigatorStatusTypes.Recover;
    }

    public void Dispose()
    {
        _database.Dispose();
        Navigator.NavStatus = _previousStatus;
    }

    private static PathMission SampleMission() => new(Guid.NewGuid())
    {
        LuId = 1234,
        SourceItemId = Guid.NewGuid(),
        DestinationItemId = Guid.NewGuid(),
        Date = new DateTime(2026, 9, 2, 10, 30, 0),
        LuCode = "SSCC-1",
        StatusDescription = "in marcia",
        StorageDoorCode = "P01A",
        PickUpDoorCode = "P02B",
        IdCommandToDo = 77,
        RelocateItemId = Guid.NewGuid(),
        MissionType = MissionTypes.Extraction,
        MovingDescription = "verso il polmone",
        OperationType = OperationTypes.Outbound,
        BufferCellId = 5,
        ExtractionQueueId = 9,
        IdLuType = 2,
        GroupedMovementId = Guid.NewGuid(),
        HandlingMode = 3,
        Gateway = Guid.NewGuid(),
        StorageDepth = 2,
        StorageDepthAfterPickUp = 1200,
        SourceAsi = "0001",
        SourceSubitem = "0002",
        SourcePartition = "0003",
        DestinationAsi = "0004",
        DestinationSubitem = "0005",
        DestinationPartition = "0006",
        LuNoteAssignmentId = 11,
        GuidAssignmentLu = Guid.NewGuid(),
        NoteProcessingId = 12,
        ActiveFilter = true,
        Tare = 12.5m,
        ReallocatedDestination = true,
        Status = MissionStatusTypes.Run
    };

    /// <summary>
    /// Una missione senza il segno dell'operatore, senza la cella persa, senza una cella prenotata
    /// per lei, senza le segnalazioni del cambio cella partite, senza una porta copiata lasciata e
    /// senza celle rifiutate dal PLC torna senza: il giro di andata e ritorno con tutti alzati (la
    /// prova qui sotto) non vedrebbe una riga che li alza sempre. E' anche la riga che c'era prima della
    /// migrazione delle celle rifiutate: nasce vuota.
    /// </summary>
    [Fact]
    public async Task AMissionWithoutTheMarksComesBackWithoutThem()
    {
        var mission = SampleMission();
        Assert.False(mission.OperatorDestination);
        Assert.False(mission.StorageCellLost);
        Assert.False(mission.StorageCellReserved);
        Assert.False(mission.ChangeOfCellReportsSent);
        Assert.False(mission.LeftACopiedDoorMark);

        await using (var context = _database.CreateContext())
        {
            await MissionStore.UpsertAsync(context, MissionStore.Snapshot(mission));
            await context.SaveChangesAsync();
        }

        await using var reading = _database.CreateContext();
        var row = Assert.Single(await MissionStore.LoadAsync(reading, OperationTypes.Outbound, activeFilter: true));
        Assert.False(row.OperatorDestination);
        Assert.False(row.StorageCellLost);
        Assert.False(row.StorageCellReserved);
        Assert.False(row.ChangeOfCellReportsSent);
        Assert.False(row.LeftACopiedDoor);
        Assert.Null(row.CellsRefusedByThePlc);
        Assert.False(row.DepositRefusedByThePlc);
        var reloaded = MissionStore.ToPathMission(row);
        Assert.False(reloaded.OperatorDestination);
        Assert.False(reloaded.StorageCellLost);
        Assert.False(reloaded.StorageCellReserved);
        Assert.False(reloaded.ChangeOfCellReportsSent);
        Assert.False(reloaded.LeftACopiedDoorMark);
        Assert.Empty(reloaded.CellsRefusedByThePlc);
        Assert.False(reloaded.DepositRefusedByThePlc);
        Assert.Equal(mission.DestinationItemId, reloaded.DestinationItemId);
    }

    [Fact]
    public async Task AMissionSurvivesTheRoundTripWithEveryField()
    {
        var mission = SampleMission();

        // Il segno della destinazione scelta dall'operatore non si imposta dall'inizializzatore:
        // si alza solo insieme alla destinazione. La cella persa si scrive dopo, perche' il setter
        // della destinazione la abbassa come il segno; e la cella prenotata per la missione dopo la
        // porta, che il suo setter la abbassa.
        Assert.True(mission.SetOperatorDestination(mission.DestinationItemId!.Value));
        mission.StorageCellLost = true;
        mission.StorageCellReserved = true;
        mission.ChangeOfCellReportsSent = true;
        mission.LeftACopiedDoorMark = true;

        // Le celle che il PLC ha rifiutato (risposta 2 del proprietario del 9/10): una con i decimali,
        // che nella cultura italiana avrebbe la virgola del separatore.
        mission.RememberCellsRefusedByThePlc([6m, 529.5m]);

        await using (var context = _database.CreateContext())
        {
            await MissionStore.UpsertAsync(context, MissionStore.Snapshot(mission));
            await context.SaveChangesAsync();
        }

        await using var reading = _database.CreateContext();
        var rows = await MissionStore.LoadAsync(reading, OperationTypes.Outbound, activeFilter: true);
        var reloaded = MissionStore.ToPathMission(Assert.Single(rows));

        Assert.True(reloaded.OperatorDestination);
        Assert.True(reloaded.StorageCellLost);
        Assert.True(reloaded.StorageCellReserved);
        Assert.True(reloaded.ChangeOfCellReportsSent);
        Assert.True(reloaded.LeftACopiedDoorMark);
        Assert.Equal([6m, 529.5m], reloaded.CellsRefusedByThePlc);
        Assert.True(reloaded.DepositRefusedByThePlc);

        Assert.Equal(mission.MissionId, reloaded.MissionId);
        Assert.Equal(mission.LuId, reloaded.LuId);
        Assert.Equal(mission.SourceItemId, reloaded.SourceItemId);
        Assert.Equal(mission.DestinationItemId, reloaded.DestinationItemId);
        Assert.Equal(mission.Date, reloaded.Date);
        Assert.Equal(mission.LuCode, reloaded.LuCode);
        Assert.Equal(mission.StatusDescription, reloaded.StatusDescription);
        Assert.Equal(mission.StorageDoorCode, reloaded.StorageDoorCode);
        Assert.Equal(mission.PickUpDoorCode, reloaded.PickUpDoorCode);
        Assert.Equal(mission.IdCommandToDo, reloaded.IdCommandToDo);
        Assert.Equal(mission.RelocateItemId, reloaded.RelocateItemId);
        Assert.Equal(mission.MissionType, reloaded.MissionType);
        Assert.Equal(mission.MovingDescription, reloaded.MovingDescription);
        Assert.Equal(mission.OperationType, reloaded.OperationType);
        Assert.Equal(mission.BufferCellId, reloaded.BufferCellId);
        Assert.Equal(mission.ExtractionQueueId, reloaded.ExtractionQueueId);
        Assert.Equal(mission.IdLuType, reloaded.IdLuType);
        Assert.Equal(mission.GroupedMovementId, reloaded.GroupedMovementId);
        Assert.Equal(mission.HandlingMode, reloaded.HandlingMode);
        Assert.Equal(mission.Gateway, reloaded.Gateway);
        Assert.Equal(mission.StorageDepth, reloaded.StorageDepth);
        Assert.Equal(1200, reloaded.StorageDepthAfterPickUp);
        Assert.Equal(mission.SourceAsi, reloaded.SourceAsi);
        Assert.Equal(mission.SourceSubitem, reloaded.SourceSubitem);
        Assert.Equal(mission.SourcePartition, reloaded.SourcePartition);
        Assert.Equal(mission.DestinationAsi, reloaded.DestinationAsi);
        Assert.Equal(mission.DestinationSubitem, reloaded.DestinationSubitem);
        Assert.Equal(mission.DestinationPartition, reloaded.DestinationPartition);
        Assert.Equal(mission.LuNoteAssignmentId, reloaded.LuNoteAssignmentId);
        Assert.Equal(mission.GuidAssignmentLu, reloaded.GuidAssignmentLu);
        Assert.Equal(mission.NoteProcessingId, reloaded.NoteProcessingId);
        Assert.Equal(mission.ActiveFilter, reloaded.ActiveFilter);
        Assert.Equal(mission.Tare, reloaded.Tare);
        Assert.Equal(mission.ReallocatedDestination, reloaded.ReallocatedDestination);
        Assert.Equal(mission.Status, reloaded.Status);
    }

    [Fact]
    public async Task EmptyIdentifiersBecomeNullInTheRowAndEmptyAgainInMemory()
    {
        var mission = new PathMission(Guid.NewGuid()) { OperationType = OperationTypes.Inbound };

        await using (var context = _database.CreateContext())
        {
            await MissionStore.UpsertAsync(context, MissionStore.Snapshot(mission));
            await context.SaveChangesAsync();
        }

        await using var reading = _database.CreateContext();
        var row = Assert.Single(await MissionStore.LoadAsync(reading, OperationTypes.Inbound, activeFilter: false));

        Assert.Null(row.RelocateItemId);
        Assert.Null(row.GroupedMovementId);
        Assert.Null(row.GatewayItemId);
        Assert.Null(row.AssignmentGuid);
        Assert.Null(row.CommandId);
        Assert.Null(row.SourceAsi);

        var reloaded = MissionStore.ToPathMission(row);
        Assert.Equal(Guid.Empty, reloaded.RelocateItemId);
        Assert.Equal(Guid.Empty, reloaded.GroupedMovementId);
        Assert.Equal(Guid.Empty, reloaded.Gateway);
        Assert.Equal(Guid.Empty, reloaded.GuidAssignmentLu);
        Assert.Equal(0, reloaded.IdCommandToDo);
    }

    [Fact]
    public async Task WritingTheSameMissionTwiceKeepsOneRowWithTheLastValues()
    {
        var mission = SampleMission();

        await using (var context = _database.CreateContext())
        {
            await MissionStore.UpsertAsync(context, MissionStore.Snapshot(mission));
            await context.SaveChangesAsync();
        }

        mission.StatusDescription = "seconda scrittura";

        // Nello stesso lotto: prima e seconda scrittura della stessa missione.
        await using (var context = _database.CreateContext())
        {
            await MissionStore.UpsertAsync(context, MissionStore.Snapshot(mission));
            mission.StatusDescription = "terza scrittura";
            await MissionStore.UpsertAsync(context, MissionStore.Snapshot(mission));
            await context.SaveChangesAsync();
        }

        await using var reading = _database.CreateContext();
        var row = Assert.Single(reading.Missions);
        Assert.Equal("terza scrittura", row.StatusDescription);
    }

    [Fact]
    public async Task DeletingRemovesTheRowAndDeletingAgainIsNotAnError()
    {
        var mission = SampleMission();

        await using (var context = _database.CreateContext())
        {
            await MissionStore.UpsertAsync(context, MissionStore.Snapshot(mission));
            await context.SaveChangesAsync();
        }

        await using (var context = _database.CreateContext())
        {
            await MissionStore.DeleteAsync(context, mission.MissionId);
            await MissionStore.DeleteAsync(context, mission.MissionId);
            await context.SaveChangesAsync();
        }

        await using var reading = _database.CreateContext();
        Assert.Empty(reading.Missions);
    }

    [Fact]
    public async Task ReloadingReturnsMissionsInCreationOrderAndOnlyThoseAsked()
    {
        var first = SampleMission();
        var second = SampleMission();
        var other = SampleMission();
        other.OperationType = OperationTypes.Inbound;

        await using (var context = _database.CreateContext())
        {
            await MissionStore.UpsertAsync(context, MissionStore.Snapshot(first));
            await MissionStore.UpsertAsync(context, MissionStore.Snapshot(second));
            await MissionStore.UpsertAsync(context, MissionStore.Snapshot(other));
            await context.SaveChangesAsync();
        }

        await using var reading = _database.CreateContext();
        var outbound = await MissionStore.LoadAsync(reading, OperationTypes.Outbound, activeFilter: true);

        Assert.Equal([first.MissionId, second.MissionId], outbound.Select(m => m.MissionId));
    }
}
