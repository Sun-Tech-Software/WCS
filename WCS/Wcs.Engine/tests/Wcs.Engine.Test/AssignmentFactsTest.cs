using Wcs.Engine.Api;
using Wcs.Engine.Collections;
using Wcs.Engine.Items;
using Wcs.Engine.ORM;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Wcs.Wms.Contracts;
using Wcs.Wms.Contracts.Test;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// I fatti che hanno preso il posto dell'ultimo ordine del motore al gestionale in processo
/// (compito 21, ex <c>NoteMagazzino.CancellaAssegnazioneUdc</c> da sei punti).
/// <para>
/// <b>I sei punti non erano un fatto solo.</b> Tre dicono «la missione se n'e' andata senza servire
/// l'assegnazione» (<see cref="IWarehouse.ReportMissionLeftAssignmentUnservedAsync"/>), uno dice
/// l'opposto - «l'assegnazione e' stata servita»
/// (<see cref="IWarehouse.ReportMissionServedAssignmentAsync"/>) - uno non ha nessuna missione di
/// mezzo (<see cref="IWarehouse.ReportUnservableQueuedExtractionAsync"/>), e il sesto era gia' un
/// fatto segnalato: <c>ReportLoadUnitLeftExtractionQueueAsync</c>, che ora ne trae anche questa
/// conseguenza (vedi <see cref="LoadUnitLeftExtractionQueueTest"/>).
/// </para>
/// </summary>
public sealed class AssignmentFactsTest : IDisposable
{
    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly NavigatorStatusTypes _previousStatus = Navigator.NavStatus;
    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly FakeWarehouse _warehouse = new();

    public AssignmentFactsTest()
    {
        Clear();
        Navigator.Store = _database;
        // Recover, come negli altri test sulle collezioni: cosi' ORM.PathMission esce prima di
        // scrivere. Non e' Stop ne' Pause, altrimenti ActiveMissions.Add uscirebbe subito e le
        // missioni dei test non sarebbero mai attive - l'errore che su questo piano ha reso verdi
        // sei prove per il motivo sbagliato.
        Navigator.NavStatus = NavigatorStatusTypes.Recover;
        Navigator.Warehouse = _warehouse;
    }

    public void Dispose()
    {
        Clear();
        Navigator.Warehouse = _previousWarehouse;
        Navigator.NavStatus = _previousStatus;
        Navigator.Store = null;
        _database.Dispose();
    }

    private static void Clear()
    {
        ActiveMissions.Instance.RemoveAll(_ => true);
        MissionSteps.Instance.RemoveAll(_ => true);
        PathDevices.Instance.Clear();
    }

    /// <summary>Un dispositivo qualunque che non sia di stoccaggio: e' la condizione dei tre punti.</summary>
    private static Items.AutomationDevice Device()
    {
        var device = TestDevices.WithRow(Guid.NewGuid(), d => d.DeviceType = DeviceTypes.Automation);
        PathDevices.Instance.Add(device);
        return device;
    }

    // ------------------------------------------------- la missione lascia l'assegnazione non servita

    /// <summary>
    /// Primo dei tre punti: la missione viene tolta dalle attive mentre l'unita' di carico e' su un
    /// dispositivo, e porta ancora la sua assegnazione. Il motore non cancella piu' niente da se':
    /// dice il fatto, con la chiave che e' la missione e con il motivo che prima finiva solo nel
    /// suo registro.
    /// </summary>
    [Fact]
    public async Task RemovingAMissionThatStillHoldsItsAssignmentReportsItAsUnserved()
    {
        var device = Device();
        var assignment = Guid.NewGuid();
        var mission = new Items.PathMission
        {
            PositionItemId = device.ItemGuid, LuId = 7001m, LuCode = "UDC7001", GuidAssignmentLu = assignment
        };

        ActiveMissions.Instance.Add(mission, buildRoute: false);
        // La missione dev'essere davvero attiva, altrimenti la prova sarebbe verde per il motivo
        // sbagliato: Remove uscirebbe lo stesso e non parlerebbe con nessuno.
        Assert.True(ActiveMissions.Instance.Contains(mission));

        Assert.True(await ActiveMissions.Instance.Remove(mission));

        var fact = Assert.Single(_warehouse.MissionsLeftAssignmentUnserved);
        Assert.Equal(mission.MissionId, fact.MissionId);
        Assert.Equal(7001m, fact.LoadUnitId);
        Assert.Equal(assignment, fact.AssignmentGuid);
        Assert.Equal(0m, fact.AssignmentId);
        Assert.NotEmpty(fact.Reason);

        // Il fatto opposto non parte: qui l'assegnazione non e' stata servita.
        Assert.Empty(_warehouse.MissionsServedAssignment);
    }

    /// <summary>
    /// <b>Compito 13</b>: la missione che se ne va nomina l'assegnazione anche quando a portarla e'
    /// la sua <b>riga di coda</b> e non il suo GUID. Era l'ultima scrittura diretta del motore su
    /// <c>ASSEGNAZIONE_NOTE</c> (<c>DbInfo.DeleteAssignedLuByIdEstrazioneUdc</c> →
    /// <c>DeleteAssignedLuByAssignmentId</c>), che girava subito dopo la segnalazione e cancellava
    /// una riga diversa da quella che la segnalazione nominava.
    /// <para>
    /// L'ultima asserzione e' quella che dice perche' non bastava togliere la chiamata: finche' la
    /// cancellazione era T-SQL diretto, in questo giro <c>Navigator.Database</c> nullo la faceva
    /// esplodere, e la riga di coda - che si cancella dopo - restava dov'era.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RemovingAMissionReportsTheAssignmentItsQueueRowCarries()
    {
        const decimal loadUnitId = 7007m;

        var device = Device();
        await ExtractionQueue.UpsertAsync(new ExtractionQueueItem
        {
            LoadUnitId = loadUnitId, AssignmentId = 8007m, SourceItemId = Guid.NewGuid()
        });
        var rowId = Assert.Single(await ExtractionQueueQueries.ListAsync(processed: null, loadUnitId: loadUnitId, take: 10)).Id;

        var mission = new Items.PathMission
        {
            PositionItemId = device.ItemGuid, LuId = loadUnitId, LuCode = "UDC7007", ExtractionQueueId = rowId
        };

        ActiveMissions.Instance.Add(mission, buildRoute: false);
        Assert.True(ActiveMissions.Instance.Contains(mission));

        Assert.True(await ActiveMissions.Instance.Remove(mission));

        var fact = Assert.Single(_warehouse.MissionsLeftAssignmentUnserved);
        Assert.Equal(mission.MissionId, fact.MissionId);
        Assert.Equal(loadUnitId, fact.LoadUnitId);
        Assert.Equal(8007m, fact.AssignmentId);
        Assert.Equal(Guid.Empty, fact.AssignmentGuid);
        Assert.NotEmpty(fact.Reason);

        Assert.Empty(await ExtractionQueueQueries.ListAsync(processed: null, loadUnitId: loadUnitId, take: 10));
    }

    /// <summary>
    /// La meta' contraria: una riga di coda <b>senza</b> assegnazione non produce nessun fatto. E'
    /// il caso del ricollocamento, che con le note non c'entra, e senza questa prova una
    /// segnalazione incondizionata passerebbe quella qui sopra.
    /// </summary>
    [Fact]
    public async Task RemovingAMissionWhoseQueueRowHasNoAssignmentReportsNothing()
    {
        const decimal loadUnitId = 7008m;

        var device = Device();
        await ExtractionQueue.UpsertAsync(new ExtractionQueueItem
        {
            LoadUnitId = loadUnitId, AssignmentId = null, SourceItemId = Guid.NewGuid()
        });
        var rowId = Assert.Single(await ExtractionQueueQueries.ListAsync(processed: null, loadUnitId: loadUnitId, take: 10)).Id;

        var mission = new Items.PathMission
        {
            PositionItemId = device.ItemGuid, LuId = loadUnitId, ExtractionQueueId = rowId
        };

        ActiveMissions.Instance.Add(mission, buildRoute: false);
        Assert.True(await ActiveMissions.Instance.Remove(mission));

        Assert.Empty(_warehouse.MissionsLeftAssignmentUnserved);
        Assert.Empty(await ExtractionQueueQueries.ListAsync(processed: null, loadUnitId: loadUnitId, take: 10));
    }

    // ------------------------------------------- l'assegnazione in coda che il motore non serve

    /// <summary>
    /// <b>Compito 13</b>: il ciclo di sfondo del magazzino trova che l'unita' di carico di questa
    /// riga di coda e' gia' in missione per un'altra riga di estrazione. La riga se ne va, e con lei
    /// l'assegnazione che stava aspettando: il motore la <b>scarta</b> dicendo perche', invece di
    /// cancellarla da se' con un <c>DELETE</c> su <c>ASSEGNAZIONE_NOTE</c>.
    /// <para>
    /// <b>Perche' questa operazione e non un'altra.</b> Il fatto nomina la riga di assegnazione
    /// (<c>ID_ASSEGNAZIONE_UDC</c>), che e' la chiave di <c>RejectNoteAssignmentAsync</c> e la
    /// stessa riga che il <c>DELETE</c> di prima toglieva.
    /// <c>ReportUnservableQueuedExtractionAsync</c>, che semanticamente sarebbe la piu' vicina,
    /// porta solo il GUID e cancellerebbe <b>tutte</b> le righe dell'assegnazione dell'unita' di
    /// carico: un comportamento diverso. E qui nessuna missione se ne va - la missione viva resta -
    /// quindi il fatto della missione non servita direbbe il falso.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AQueuedAssignmentDisplacedByAnotherExtractionIsRejected()
    {
        var table = new System.Data.DataTable();
        table.Columns.Add("ID_ESTRAZIONE_UDC", typeof(decimal));
        table.Columns.Add("ID_ASSEGNAZIONE_UDC", typeof(decimal));
        table.Columns.Add("GUID_ASSEGNAZIONE_UDC", typeof(Guid));
        table.Columns.Add("ID_UDC", typeof(decimal));
        table.Rows.Add(55m, 8100m, Guid.NewGuid(), 7010m);

        // La missione viva serve un'altra riga di coda: e' la prima delle due uscite del metodo.
        var existing = new Items.PathMission(Guid.NewGuid()) { LuId = 7010m, ExtractionQueueId = 99m };

        Assert.False(await Items.Managers.AutomationDeviceStorageManager.ProcessExistingMission(table.Rows[0], existing));

        var rejected = Assert.Single(_warehouse.RejectedNoteAssignments);
        Assert.Equal(8100m, rejected.AssignmentId);
        Assert.Equal(7010m, rejected.LoadUnitId);
        Assert.NotEmpty(rejected.Reason);
    }

    /// <summary>
    /// La seconda uscita dello stesso metodo: la missione viva sulla stessa unita' di carico porta
    /// <b>un'altra</b> assegnazione. Stesso fatto, stessa operazione, motivo diverso - ed e' il
    /// motivo che un operatore legge quando l'assegnazione sparisce.
    /// </summary>
    [Fact]
    public async Task AQueuedAssignmentDisplacedByAnotherAssignmentIsRejected()
    {
        var table = new System.Data.DataTable();
        table.Columns.Add("ID_ESTRAZIONE_UDC", typeof(decimal));
        table.Columns.Add("ID_ASSEGNAZIONE_UDC", typeof(decimal));
        table.Columns.Add("GUID_ASSEGNAZIONE_UDC", typeof(Guid));
        table.Columns.Add("ID_UDC", typeof(decimal));
        table.Rows.Add(56m, 8101m, Guid.NewGuid(), 7011m);

        // Nessuna riga di coda per la missione viva, ma un'assegnazione diversa da quella della riga.
        var existing = new Items.PathMission(Guid.NewGuid()) { LuId = 7011m, GuidAssignmentLu = Guid.NewGuid() };

        Assert.False(await Items.Managers.AutomationDeviceStorageManager.ProcessExistingMission(table.Rows[0], existing));

        Assert.Equal(8101m, Assert.Single(_warehouse.RejectedNoteAssignments).AssignmentId);
    }

    /// <summary>
    /// Una missione senza assegnazione non produce nessun fatto: la guardia del motore resta dov'era.
    /// </summary>
    [Fact]
    public async Task RemovingAMissionWithoutAnAssignmentReportsNothing()
    {
        var device = Device();
        var mission = new Items.PathMission { PositionItemId = device.ItemGuid, LuId = 7002m };

        ActiveMissions.Instance.Add(mission, buildRoute: false);
        Assert.True(ActiveMissions.Instance.Contains(mission));

        Assert.True(await ActiveMissions.Instance.Remove(mission));

        Assert.Empty(_warehouse.MissionsLeftAssignmentUnserved);
        Assert.DoesNotContain(nameof(IWarehouse.ReportMissionLeftAssignmentUnservedAsync), _warehouse.Calls);
    }

    /// <summary>
    /// Secondo dei tre punti: la missione di compattazione non trova piu' l'unita' di carico nella
    /// cella di partenza e si toglie da sola. Stesso fatto del primo - chiave, forma e semantica -
    /// quindi stessa operazione; cambia solo il motivo, che e' quello che un operatore legge.
    /// </summary>
    [Fact]
    public async Task ADefragmentMissionThatLostItsLoadUnitReportsTheAssignmentAsUnserved()
    {
        var device = Device();
        var assignment = Guid.NewGuid();
        var mission = new Items.PathMission
        {
            PositionItemId = device.ItemGuid,
            LuId = 7003m,
            LuCode = "UDC7003",
            MissionType = MissionTypes.DefragmentCell,
            GuidAssignmentLu = assignment
        };

        ActiveMissions.Instance.Add(mission, buildRoute: false);
        Assert.True(ActiveMissions.Instance.Contains(mission));

        await mission.BuildPathToDestination();

        var fact = Assert.Single(_warehouse.MissionsLeftAssignmentUnserved);
        Assert.Equal(mission.MissionId, fact.MissionId);
        Assert.Equal(7003m, fact.LoadUnitId);
        Assert.Equal(assignment, fact.AssignmentGuid);
        Assert.Equal(0m, fact.AssignmentId);
        Assert.NotEmpty(fact.Reason);
    }

    /// <summary>
    /// Terzo dei tre punti, ed e' quello che rende la domanda del compito interessante: la missione
    /// ricaricata all'avvio che risulta gia' a destinazione nomina la propria assegnazione con
    /// <c>ID_ASSEGNAZIONE_UDC</c>, non con il GUID.
    /// <para>
    /// <b>Il GUID non viaggia, ed e' voluto.</b> La missione ricaricata puo' portarli tutt'e due, ma
    /// il punto ha sempre guardato solo l'identificativo di riga: mandare anche il GUID
    /// cancellerebbe tutte le righe dell'assegnazione dell'unita' di carico invece di quella sola -
    /// un comportamento diverso, non una correzione di questo compito. La prova lo blocca.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AReloadedMissionAlreadyAtItsDestinationReportsTheAssignmentRowNotTheGuid()
    {
        var device = Device();
        await PlantPersistence.SaveDoorMappingsAsync(device.ItemGuid,
            [new DoorMapping { ItemId = device.ItemGuid, DoorCode = "P21A", CellId = 21, Side = "A" }]);

        var mission = new Items.PathMission
        {
            PositionItemId = device.ItemGuid,
            LuId = 7005m,
            LuCode = "UDC7005",
            PickUpDoorCode = "P21A",
            StorageDoorCode = "P21A",
            LuNoteAssignmentId = 8005m,
            GuidAssignmentLu = Guid.NewGuid()
        };

        await DbInfo.PrepareLoadedMission(mission, []);

        var fact = Assert.Single(_warehouse.MissionsLeftAssignmentUnserved);
        Assert.Equal(mission.MissionId, fact.MissionId);
        Assert.Equal(7005m, fact.LoadUnitId);
        Assert.Equal(8005m, fact.AssignmentId);
        Assert.Equal(Guid.Empty, fact.AssignmentGuid);
        Assert.NotEmpty(fact.Reason);
    }

    // --------------------------------------------------------------- l'assegnazione e' stata servita

    /// <summary>
    /// Il fatto opposto: l'unita' di carico e' arrivata alla postazione e l'assegnazione e' stata
    /// servita. Chiave uguale al fatto di sopra (la missione) e forma quasi uguale, semantica
    /// opposta - e non finisce nella stessa operazione proprio per quello.
    /// <para>
    /// L'ultima asserzione e' anche la prova che i due fatti non si sovrappongono mai: qui la
    /// missione lascia l'assegnazione azzerata, quindi la chiusura che verra' dopo non la segnalera'
    /// come non servita.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AMissionThatDeliveredReportsItsAssignmentAsServed()
    {
        var assignment = Guid.NewGuid();
        var mission = new Items.PathMission { LuId = 7004m, LuCode = "UDC7004", GuidAssignmentLu = assignment };

        await mission.ProcessAssignments();

        var fact = Assert.Single(_warehouse.MissionsServedAssignment);
        Assert.Equal(mission.MissionId, fact.MissionId);
        Assert.Equal(7004m, fact.LoadUnitId);
        Assert.Equal(assignment, fact.AssignmentGuid);
        Assert.NotEmpty(fact.Reason);

        Assert.Empty(_warehouse.MissionsLeftAssignmentUnserved);
        Assert.Equal(Guid.Empty, mission.GuidAssignmentLu);
    }

    /// <summary>
    /// Il guasto del confine non fa saltare il giro dell'estrazione: il punto ha il suo
    /// <c>try/catch</c> da sempre, e l'assegnazione resta azzerata come prima. Cambia una cosa, e
    /// va detta: prima qui non c'era niente che potesse fallire per la rete.
    /// </summary>
    [Fact]
    public async Task AWarehouseFailureOnTheServedFactDoesNotEscapeTheExtractionRound()
    {
        _warehouse.Unavailable = true;

        var mission = new Items.PathMission { LuId = 7006m, GuidAssignmentLu = Guid.NewGuid() };

        await mission.ProcessAssignments();

        Assert.Equal(Guid.Empty, mission.GuidAssignmentLu);
    }
}
