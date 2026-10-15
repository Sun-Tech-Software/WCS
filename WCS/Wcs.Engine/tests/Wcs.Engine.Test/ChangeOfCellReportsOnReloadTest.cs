using Wcs.Engine.Api;
using Wcs.Engine.Collections;
using Wcs.Engine.ORM;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Wcs.Wms.Contracts.Test;
using Xunit;
using AutomationDevice = Wcs.Engine.Items.AutomationDevice;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// La ricarica di una missione con il segno «segnalazioni del cambio cella partite»
/// (<c>PathMission.ChangeOfCellReportsSent</c>; specifica, risposta dell'8/10 al punto 3; voce 39,
/// secondo giro, compito 9): l'host si e' fermato dopo che il gestionale poteva aver saputo che
/// l'estrazione lascia l'assegnazione e che l'UDC esce dalla coda di estrazione, e prima del commit
/// del cambio cella. Un'estrazione con il segno si chiude, senza rimandare niente al gestionale; senza
/// il segno, o con il cambio gia' registrato, resta.
/// </summary>
/// <remarks>
/// La ricarica passa da <c>DbInfo.RecoverFailure</c>, come in <see cref="StorageDepthOnReloadTest"/>:
/// il navigatore in marcia serve alla collezione (in Stop <c>ActiveMissions.Add</c> esce subito), e il
/// dispositivo nel diagramma non e' collegato a un magazzino, cosi' la ricarica non arriva alla
/// ripresa della coda di prelievo. La riga della coda di estrazione del WCS c'e', e la prova guarda
/// che la chiusura la tolga: rimasta, quella ripresa ne farebbe un'estrazione nuova.
/// </remarks>
public sealed class ChangeOfCellReportsOnReloadTest : IDisposable
{
    private const decimal LoadUnitId = 6200m;

    /// <summary>
    /// Le missioni che la ricarica ricostruisce, prese dal loro annuncio di posizione: la ricarica non
    /// le restituisce, e il lavoro della posizione che fanno partire si aspetta da qui. Con
    /// <see cref="HoldThePositionWorkOf"/> il primo annuncio del dispositivo che quel lavoro fa
    /// (<c>AutomationDevice.FireLuLoadStatusChanged</c>) si ferma finche' la prova non lo lascia andare.
    /// </summary>
    private sealed class Rebuilt : Api.IEngineEventSink
    {
        private readonly Lock _lock = new();
        private readonly List<Items.PathMission> _missions = [];
        private Guid _heldMission;
        private Guid _heldDevice;
        private bool _armed;

        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void HoldThePositionWorkOf(Guid missionId, Guid deviceId)
        {
            _heldMission = missionId;
            _heldDevice = deviceId;
        }

        public Items.PathMission? Of(Guid missionId)
        {
            lock (_lock) return _missions.LastOrDefault(mission => mission.MissionId == missionId);
        }

        public void MissionChanged(Items.PathMission mission, Api.EngineMissionChange change)
        {
            lock (_lock)
            {
                _missions.Add(mission);
                if (mission.MissionId == _heldMission && change == Api.EngineMissionChange.PositionChanged)
                    _armed = true;
            }
        }

        public void ItemChanged(AutomationDevice device)
        {
            lock (_lock)
            {
                if (!_armed || device.ItemGuid != _heldDevice || Reached.Task.IsCompleted) return;
                Reached.TrySetResult();
            }

            // Sul thread del lavoro della posizione (Task.Run), non su quello della ricarica.
            Released.Task.Wait(TimeSpan.FromSeconds(30));
        }
        public void NavigatorStatusChanged() { }
        public void ExtractionQueueChanged(ExtractionQueueItem item, Api.EngineQueueChange change) { }
        public void CommandChanged(CommandRecord command) { }
    }

    private readonly Rebuilt _rebuilt = new();

    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly FakeWarehouse _warehouse = new();
    private readonly Wms.Contracts.IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly QueuedDatabaseWriter<WcsWrite>? _previousWriter = Navigator.StateWriter;
    private readonly NavigatorStatusTypes _previousNavStatus = Navigator.NavStatus;
    private readonly List<AutomationDevice> _devices = [];
    private readonly List<Guid> _missions = [];

    public ChangeOfCellReportsOnReloadTest()
    {
        Navigator.Store = _database;
        Navigator.StateWriter = null;
        Navigator.Warehouse = _warehouse;

        // Le missioni chiuse da altre ricariche non si restituiscono al gestionale finto di questa
        // prova al passaggio a Run qui sotto.
        ReservationsOfMissionsClosedOnReload.Forget();
        Navigator.NavStatus = NavigatorStatusTypes.Run;
        Navigator.EventSink = _rebuilt;
    }

    public void Dispose()
    {
        // Prima si aspetta il lavoro di sfondo delle missioni ricaricate, come in StorageDepthOnReloadTest
        // (voce 46 del develop). Il ricaricamento imposta PositionItemId, e il setter lancia PositionWork,
        // che alla fine riscrive la missione nello store corrente quando arriva: senza attenderlo la riga
        // della missione di una prova finiva nel database della prova dopo, la ricarica di quella la
        // rimetteva fra le attive per prima, e la missione della prova, con la stessa UDC 6200, restava
        // fuori (AMissionWithoutTheMarkOrWithTheChangeRegisteredStays rossa a intermittenza dopo l'unione
        // con il develop, voce 39 compito 19).
        foreach (var missionId in _missions)
            if (ActiveMissions.Instance[missionId] is { } reloaded)
                Assert.True(reloaded.PositionWork.Wait(TimeSpan.FromSeconds(30)), $"PositionWork di {missionId} non finito");

        foreach (var missionId in _missions) ActiveMissions.Instance.RemoveAll(m => m.MissionId == missionId);

        foreach (var device in _devices)
        {
            PathDevices.Instance.Remove(device.ItemGuid);
            device.Dispose();
        }

        Navigator.EventSink = null;
        ReservationsOfMissionsClosedOnReload.Forget();
        Navigator.NavStatus = _previousNavStatus;
        Navigator.StateWriter = _previousWriter;
        Navigator.Store = null;
        Navigator.Warehouse = _previousWarehouse;
        _database.Dispose();
    }

    /// <summary>
    /// L'estrazione dell'UDC 6200 ricaricata con il segno: non torna fra le attive, la sua riga e la
    /// riga della coda di estrazione non ci sono piu', il gestionale non riceve niente, e una riga di
    /// avviso del registro lo dice. Prima tornava l'estrazione di prima, con l'assegnazione e la riga
    /// di coda che il gestionale aveva gia' lasciato andare. La riga resta tolta anche finito il lavoro
    /// della posizione che la ricostruzione della missione fa partire in sottofondo, e che alla fine
    /// riscrive la riga: prima la ricarica la cancellava senza aspettarlo, e la riga tornava (il rosso
    /// intermittente di questa prova nella suite intera).
    /// </summary>
    [Fact]
    public async Task AnExtractionReloadedWithTheMarkIsClosed()
    {
        using var log = new RecordingLogWriter();
        var missionId = await SaveAMissionAsync(MissionTypes.Extraction, reportsSent: true);

        await DbInfo.RecoverFailure();
        var rebuilt = _rebuilt.Of(missionId);
        Assert.NotNull(rebuilt);
        await rebuilt.PositionWork.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Null(ActiveMissions.Instance.FirstOrDefault(m => m != null && m.MissionId == missionId));
        Assert.Null(TheRow(missionId));
        Assert.Empty(await ExtractionQueueQueries.ListAsync(processed: null, loadUnitId: LoadUnitId, take: 10));
        Assert.Empty(_warehouse.Calls);

        await log.Wait((line, level) => level == Log.Interface.LogEntryType.Warning &&
                                        line.Contains(missionId.ToString()) &&
                                        line.Contains("segnalazioni del cambio cella gia' partite"))
            .WaitAsync(TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// La stessa estrazione, con il lavoro della posizione che la ricostruzione della missione fa
    /// partire in sottofondo fermo finche' la ricarica non ha tolto la riga della coda di estrazione,
    /// il passo della chiusura subito prima di togliere la riga della missione. Quel lavoro alla fine
    /// riscrive la riga: la chiusura lo aspetta prima di toglierla, e la riga della missione chiusa
    /// non torna. Prima la cancellava senza aspettarlo, e un lavoro della posizione arrivato dopo la
    /// rimetteva nel database del WCS, con il segno: il rosso intermittente di
    /// <see cref="AnExtractionReloadedWithTheMarkIsClosed"/> nella suite intera (revisione finale del
    /// secondo giro), e alla ricarica dopo l'estrazione si chiudeva di nuovo.
    /// </summary>
    [Fact]
    public async Task TheRowOfTheClosedExtractionDoesNotComeBackWithThePositionWork()
    {
        var missionId = await SaveAMissionAsync(MissionTypes.Extraction, reportsSent: true);
        _rebuilt.HoldThePositionWorkOf(missionId, _devices[^1].ItemGuid);

        var recover = DbInfo.RecoverFailure();

        try
        {
            await _rebuilt.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30));

            using var cap = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (!recover.IsCompleted &&
                   (await ExtractionQueueQueries.ListAsync(processed: null, loadUnitId: LoadUnitId, take: 10)).Count > 0)
            {
                Assert.False(cap.IsCancellationRequested, "la ricarica non ha tolto la riga della coda di estrazione");
                await Task.Yield();
            }
        }
        finally
        {
            _rebuilt.Released.TrySetResult();
        }

        await recover.WaitAsync(TimeSpan.FromSeconds(30));
        var rebuilt = _rebuilt.Of(missionId);
        Assert.NotNull(rebuilt);
        await rebuilt.PositionWork.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Null(ActiveMissions.Instance.FirstOrDefault(m => m != null && m.MissionId == missionId));
        Assert.Null(TheRow(missionId));
    }

    /// <summary>
    /// L'estrazione dell'UDC 6200 con il segno aveva preso la cella 6 nel gestionale prima del riavvio
    /// (fra la prenotazione e il commit del cambio cella). La ricarica, in Recover come la fa il motore
    /// (<c>Navigator</c> prima di <c>DbInfo.RecoverFailure</c>), la chiude senza chiamare il gestionale;
    /// al passaggio a Run la prenotazione torna al gestionale, una volta: un altro passaggio a Run non la
    /// richiede (specifica, risposta 10 dell'8/10 sera). Prima nessuno la restituiva, e la cella restava
    /// presa fino alla scadenza del gestionale.
    /// </summary>
    [Fact]
    public async Task TheReservationOfAClosedExtractionGoesBackToTheWarehouseAtRun()
    {
        var missionId = await SaveAMissionAsync(MissionTypes.Extraction, reportsSent: true);
        _warehouse.StorageReservationsByMission[missionId] = TheCell6Reserved(missionId);
        Navigator.NavStatus = NavigatorStatusTypes.Recover;

        await DbInfo.RecoverFailure();
        var rebuilt = _rebuilt.Of(missionId);
        Assert.NotNull(rebuilt);
        await rebuilt.PositionWork.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Null(TheRow(missionId));
        Assert.Empty(_warehouse.Calls);
        Assert.True(_warehouse.StorageReservationsByMission.ContainsKey(missionId));

        Navigator.NavStatus = NavigatorStatusTypes.Run;
        await ReservationsOfMissionsClosedOnReload.GivenBack.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal([missionId], _warehouse.ReleasedStorageReservations);
        Assert.False(_warehouse.StorageReservationsByMission.ContainsKey(missionId));
        Assert.Empty(TheReservationsToGiveBack());

        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        Navigator.NavStatus = NavigatorStatusTypes.Run;
        await ReservationsOfMissionsClosedOnReload.GivenBack.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal([nameof(Wms.Contracts.IWarehouse.ReleaseStorageReservationAsync)], _warehouse.Calls);
    }

    /// <summary>
    /// Risposta 2 del proprietario del 9/10: l'estrazione dell'UDC 6200 con il segno, chiusa dalla
    /// ricarica, aveva la cella 6. La prenotazione da restituire sta nel database del WCS; l'host si
    /// ferma prima del Run (la memoria si perde, il database resta), e al riavvio la ricarica la
    /// rilegge senza chiamare il gestionale; al passaggio a Run torna al gestionale, e la riga esce dal
    /// database. Prima l'elenco stava solo in memoria: con l'host fermato prima del Run nessuno la
    /// restituiva, e la cella restava presa fino alla scadenza del gestionale.
    /// </summary>
    [Fact]
    public async Task AReservationToGiveBackSurvivesAHostStoppedBeforeRun()
    {
        var missionId = await SaveAMissionAsync(MissionTypes.Extraction, reportsSent: true);
        _warehouse.StorageReservationsByMission[missionId] = TheCell6Reserved(missionId);
        Navigator.NavStatus = NavigatorStatusTypes.Recover;

        await DbInfo.RecoverFailure();
        var rebuilt = _rebuilt.Of(missionId);
        Assert.NotNull(rebuilt);
        await rebuilt.PositionWork.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Null(TheRow(missionId));
        Assert.Equal([missionId], TheReservationsToGiveBack());

        // L'host si ferma prima del Run: lo stato in memoria si perde, il database del WCS resta.
        ReservationsOfMissionsClosedOnReload.Forget();
        await DbInfo.RecoverFailure();
        Assert.Empty(_warehouse.Calls);

        Navigator.NavStatus = NavigatorStatusTypes.Run;
        await ReservationsOfMissionsClosedOnReload.GivenBack.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal([missionId], _warehouse.ReleasedStorageReservations);
        Assert.False(_warehouse.StorageReservationsByMission.ContainsKey(missionId));
        Assert.Empty(TheReservationsToGiveBack());
    }

    /// <summary>
    /// Risposta 2 del proprietario del 9/10, l'ordine delle due righe: la ricarica chiude l'estrazione con
    /// il segno, e la riga della prenotazione da restituire non si puo' scrivere (qui la tabella non c'e',
    /// tolta mentre il lavoro della posizione e' fermo, prima che la chiusura scriva). La riga della
    /// missione resta nel database del WCS: la prossima ricarica la richiude e la riannota. Nell'ordine
    /// opposto - prima tolta la riga della missione - la prenotazione si perdeva senza traccia (revisione
    /// finale del quarto giro: l'ordine non era mai stato visto rosso).
    /// </summary>
    [Fact]
    public async Task WhenTheReservationRowCannotBeWrittenTheMissionRowStays()
    {
        var missionId = await SaveAMissionAsync(MissionTypes.Extraction, reportsSent: true);
        _rebuilt.HoldThePositionWorkOf(missionId, _devices[^1].ItemGuid);
        Navigator.NavStatus = NavigatorStatusTypes.Recover;

        var recover = DbInfo.RecoverFailure();

        try
        {
            await _rebuilt.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await using var context = _database.CreateContext();
            Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.ExecuteSqlRaw(context.Database,
                "DROP TABLE ReservationsToGiveBack");
        }
        finally
        {
            _rebuilt.Released.TrySetResult();
        }

        await Record.ExceptionAsync(() => recover.WaitAsync(TimeSpan.FromSeconds(30)));
        var rebuilt = _rebuilt.Of(missionId);
        Assert.NotNull(rebuilt);
        await rebuilt.PositionWork.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.NotNull(TheRow(missionId));
        Assert.Empty(_warehouse.Calls);
    }

    /// <summary>
    /// Come sopra, con il gestionale che non risponde al rilascio quando il motore va in Run: una riga
    /// di avviso lo dice, e la prenotazione resta alla scadenza del gestionale. Nessun ritentativo: il
    /// gestionale tornato, un altro passaggio a Run non la chiede piu' (specifica, risposta 10
    /// dell'8/10 sera).
    /// </summary>
    [Fact]
    public async Task WithTheWarehouseSilentAtRunAWarningSaysSoAndNothingIsTriedAgain()
    {
        using var log = new RecordingLogWriter();
        var missionId = await SaveAMissionAsync(MissionTypes.Extraction, reportsSent: true);
        _warehouse.StorageReservationsByMission[missionId] = TheCell6Reserved(missionId);
        Navigator.NavStatus = NavigatorStatusTypes.Recover;

        await DbInfo.RecoverFailure();
        var rebuilt = _rebuilt.Of(missionId);
        Assert.NotNull(rebuilt);
        await rebuilt.PositionWork.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Empty(_warehouse.Calls);

        _warehouse.UnavailableOperations.Add(nameof(Wms.Contracts.IWarehouse.ReleaseStorageReservationAsync));
        Navigator.NavStatus = NavigatorStatusTypes.Run;
        await ReservationsOfMissionsClosedOnReload.GivenBack.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Empty(_warehouse.ReleasedStorageReservations);
        await log.Wait((line, level) => level == Log.Interface.LogEntryType.Warning &&
                                        line.Contains(missionId.ToString()) &&
                                        line.Contains("non e' stata restituita al gestionale"))
            .WaitAsync(TimeSpan.FromSeconds(30));

        // Scartata con la riga di avviso, la prenotazione esce anche dal database del WCS (risposta 2
        // del proprietario del 9/10): una ricarica non la richiederebbe.
        Assert.Empty(TheReservationsToGiveBack());

        _warehouse.UnavailableOperations.Clear();
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        Navigator.NavStatus = NavigatorStatusTypes.Run;
        await ReservationsOfMissionsClosedOnReload.GivenBack.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal([nameof(Wms.Contracts.IWarehouse.ReleaseStorageReservationAsync)], _warehouse.Calls);
        Assert.Empty(_warehouse.ReleasedStorageReservations);
        Assert.True(_warehouse.StorageReservationsByMission.ContainsKey(missionId));
    }

    /// <summary>
    /// Senza il segno l'estrazione ricaricata resta, con la sua riga di coda; con il segno ma con il
    /// cambio cella gia' registrato (il tipo e' quello nuovo) resta anche lei, e il segno in memoria
    /// e' giu'.
    /// </summary>
    [Theory]
    [InlineData(MissionTypes.Extraction, false)]
    [InlineData(MissionTypes.ChangeStoreLocation, true)]
    public async Task AMissionWithoutTheMarkOrWithTheChangeRegisteredStays(MissionTypes type, bool reportsSent)
    {
        var missionId = await SaveAMissionAsync(type, reportsSent);

        await DbInfo.RecoverFailure();

        var reloaded = ActiveMissions.Instance.FirstOrDefault(m => m != null && m.MissionId == missionId);
        Assert.NotNull(reloaded);
        Assert.Equal(type, reloaded.MissionType);
        Assert.False(reloaded.ChangeOfCellReportsSent);
        Assert.NotNull(TheRow(missionId));
        Assert.Single(await ExtractionQueueQueries.ListAsync(processed: null, loadUnitId: LoadUnitId, take: 10));
    }

    /// <summary>
    /// Una missione dell'UDC 6200 sul dispositivo D, con la quota (la ricarica non chiede la quota al
    /// gestionale) e la sua riga nella coda di estrazione del WCS, del tipo e con il segno dati.
    /// </summary>
    private async Task<Guid> SaveAMissionAsync(MissionTypes type, bool reportsSent)
    {
        var device = TestDevices.WithRow(Guid.NewGuid(), d => d.DeviceType = DeviceTypes.CircularDistributor);
        PathDevices.Instance.Add(device);
        _devices.Add(device);

        await ORM.ExtractionQueue.UpsertAsync(new ExtractionQueueItem
        {
            LoadUnitId = LoadUnitId, AssignmentId = 77, ExtractionRowId = 901, CellId = 5, SourceItemId = device.ItemGuid
        });
        var queueRow = Assert.Single(await ExtractionQueueQueries.ListAsync(processed: null, loadUnitId: LoadUnitId, take: 10)).Id;

        var missionId = Guid.NewGuid();
        _missions.Add(missionId);

        await using var context = _database.CreateContext();
        context.Missions.Add(new Mission
        {
            MissionId = missionId,
            LoadUnitId = LoadUnitId,
            PositionItemId = device.ItemGuid,
            Date = DateTime.Now,
            MissionType = (short)type,
            OperationType = (short)OperationTypes.Outbound,
            ActiveFilter = true,
            StorageDepth = 5,
            ExtractionQueueId = queueRow,
            ChangeOfCellReportsSent = reportsSent
        });
        await context.SaveChangesAsync();

        return missionId;
    }

    /// <summary>La cella 6 (porta A6) che il gestionale tiene per la missione.</summary>
    private static Wms.Contracts.StorageReservation TheCell6Reserved(Guid missionId) =>
        new(missionId, Wms.Contracts.StorageReservationOutcome.CellFound, 6, "A6", Partition: "0063", StorageDepth: 150,
            NeedsCompaction: false);

    /// <summary>Le missioni la cui prenotazione e' da restituire, nel database del WCS.</summary>
    private List<Guid> TheReservationsToGiveBack()
    {
        using var context = _database.CreateContext();
        return [.. context.ReservationsToGiveBack.OrderBy(row => row.Id).Select(row => row.MissionId)];
    }

    private Mission? TheRow(Guid missionId)
    {
        using var context = _database.CreateContext();
        return context.Missions.SingleOrDefault(row => row.MissionId == missionId);
    }
}
