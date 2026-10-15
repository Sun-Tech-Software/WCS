using Microsoft.EntityFrameworkCore;
using Wcs.Engine.Api;
using Wcs.Engine.Collections;
using Wcs.Engine.ORM;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Wcs.Wms.Contracts;
using Wcs.Wms.Contracts.Test;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// Voce 45, compito 1: al prelievo di un'estrazione la missione ricorda che cosa ha servito -
/// l'assegnazione della nota, il GUID dell'assegnazione e la riga di estrazione per UDC - prima di
/// staccarsi dalla riga di coda e dal GUID. Senza, il cambio verso un magazzino di un'estrazione
/// gia' prelevata non saprebbe che cosa chiedere al gestionale di riaprire (specifica della voce 45,
/// sezione 1.2).
/// </summary>
public sealed class ServedOrderMemoryTest : IDisposable
{
    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly WcsDatabase? _previousStore = Navigator.Store;
    private readonly QueuedDatabaseWriter<WcsWrite>? _previousStateWriter = Navigator.StateWriter;
    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly NavigatorStatusTypes _previousNavStatus = Navigator.NavStatus;

    public ServedOrderMemoryTest()
    {
        Navigator.Store = _database;
        Navigator.StateWriter = null;
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        Navigator.ExtractionOutcomeDispatcher.Reset();
        Navigator.Warehouse = new FakeWarehouse();
    }

    public void Dispose()
    {
        Navigator.ExtractionOutcomeDispatcher.Reset();
        Navigator.NavStatus = _previousNavStatus;
        Navigator.Warehouse = _previousWarehouse;
        Navigator.StateWriter = _previousStateWriter;
        Navigator.Store = _previousStore;
        _database.Dispose();
    }

    private static readonly Guid AssignmentGuid = new("5a1e0045-0000-4000-8000-000000000045");

    /// <summary>La riga di coda con l'assegnazione e la riga di estrazione che il prelievo servira'.</summary>
    private static async Task<long> QueueRow(decimal loadUnitId, decimal? assignmentId, decimal? extractionRowId)
    {
        await ExtractionQueue.UpsertAsync(new ExtractionQueueItem
        {
            LoadUnitId = loadUnitId,
            AssignmentId = assignmentId,
            ExtractionRowId = extractionRowId,
            AssignmentGuid = AssignmentGuid,
            SourceItemId = Guid.NewGuid(),
            Processed = false
        });

        var queued = await ExtractionQueueQueries.ListAsync(processed: null, loadUnitId: loadUnitId, take: 10);
        return Assert.Single(queued).Id;
    }

    /// <summary>
    /// Il giro del prelievo, quello che lo scarico chiama prima di togliere l'UDC
    /// (<c>TryPersistExtractionOutcomeBeforeUnload</c>, gli stessi due passi di
    /// <c>ProcessExtractionQueue</c>): la missione si stacca come prima dalla riga di coda e dal
    /// GUID, e tiene i tre campi.
    /// </summary>
    [Fact]
    public async Task ThePickUpRemembersTheAssignmentTheGuidAndTheExtractionRowItServed()
    {
        var rowId = await QueueRow(4545m, assignmentId: 902m, extractionRowId: 8045m);
        var mission = new Items.PathMission(Guid.NewGuid())
        {
            LuId = 4545m, ExtractionQueueId = rowId, GuidAssignmentLu = AssignmentGuid, LuNoteAssignmentId = 902m
        };

        Assert.True(await mission.TryPersistExtractionOutcomeBeforeUnload());

        Assert.Equal(0m, mission.ExtractionQueueId);
        Assert.Equal(Guid.Empty, mission.GuidAssignmentLu);
        Assert.Equal(902m, mission.ServedAssignmentId);
        Assert.Equal(AssignmentGuid, mission.ServedAssignmentGuid);
        Assert.Equal(8045m, mission.ServedExtractionRowId);
    }

    /// <summary>
    /// I tre campi stanno nella riga della missione del <c>wcs.db</c>: la ricarica dopo un riavvio
    /// (<see cref="MissionStore.ToPathMission"/>) li ritrova uguali.
    /// </summary>
    [Fact]
    public async Task TheServedFieldsSurviveARestart()
    {
        var rowId = await QueueRow(4546m, assignmentId: 903m, extractionRowId: 8046m);
        var mission = new Items.PathMission(Guid.NewGuid())
        {
            LuId = 4546m, ExtractionQueueId = rowId, GuidAssignmentLu = AssignmentGuid, LuNoteAssignmentId = 903m
        };

        Assert.True(await mission.TryPersistExtractionOutcomeBeforeUnload());

        await using var context = _database.CreateContext();
        var row = await context.Missions.AsNoTracking().SingleAsync(m => m.MissionId == mission.MissionId);
        Assert.Equal(0m, row.ExtractionQueueId);
        Assert.Null(row.AssignmentGuid);
        Assert.Equal(903m, row.ServedAssignmentId);
        Assert.Equal(AssignmentGuid, row.ServedAssignmentGuid);
        Assert.Equal(8046m, row.ServedExtractionRowId);

        var reloaded = MissionStore.ToPathMission(row);
        Assert.Equal(903m, reloaded.ServedAssignmentId);
        Assert.Equal(AssignmentGuid, reloaded.ServedAssignmentGuid);
        Assert.Equal(8046m, reloaded.ServedExtractionRowId);
    }

    /// <summary>
    /// Una missione che non ha ancora prelevato non ha servito niente: i tre campi restano vuoti nella
    /// riga e tornano vuoti dalla ricarica (e' anche la riga che c'era prima della migrazione).
    /// </summary>
    [Fact]
    public async Task AMissionThatHasNotPickedUpHasServedNothing()
    {
        var mission = new Items.PathMission(Guid.NewGuid())
        {
            LuId = 4547m, GuidAssignmentLu = AssignmentGuid, LuNoteAssignmentId = 904m
        };

        await using var context = _database.CreateContext();
        var row = await context.Missions.AsNoTracking().SingleAsync(m => m.MissionId == mission.MissionId);
        Assert.Equal(0m, row.ServedAssignmentId);
        Assert.Null(row.ServedAssignmentGuid);
        Assert.Equal(0m, row.ServedExtractionRowId);

        var reloaded = MissionStore.ToPathMission(row);
        Assert.Equal(0m, reloaded.ServedAssignmentId);
        Assert.Equal(Guid.Empty, reloaded.ServedAssignmentGuid);
        Assert.Equal(0m, reloaded.ServedExtractionRowId);
    }
}
