using Wcs.Engine.Api;
using Wcs.Engine.Collections;
using Wcs.Engine.Items;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

public sealed class EngineApiFacadesTest : IDisposable
{
    /// <summary>Il primo giorno del mese corrente: lo storico via API non risponde su date precedenti.</summary>
    private static DateTime FirstOfThisMonth => new(DateTime.Now.Year, DateTime.Now.Month, 1);

    private readonly NavigatorStatusTypes _previousStatus = Navigator.NavStatus;
    private readonly WcsDatabase _store = WcsDatabase.InMemory();
    private readonly WcsHistoryDatabase _history = WcsHistoryDatabase.InMemory();

    public EngineApiFacadesTest()
    {
        Navigator.Store = _store;
        Navigator.History = _history;
        Navigator.NavStatus = NavigatorStatusTypes.Run;
    }

    public void Dispose()
    {
        Navigator.NavStatus = _previousStatus;
        Navigator.Store = null;
        Navigator.History = null;
        _store.Dispose();
        _history.Dispose();
    }

    [Fact]
    public async Task ACommandEnqueuedGetsAKeyAndIsToDo()
    {
        var id = await CommandGateway.EnqueueAsync(new CommandRecord { CommandType = 3003, LoadUnitId = 5 });
        Assert.True(id > 0);
        var stored = await CommandGateway.FindAsync(id);
        Assert.NotNull(stored);
        Assert.Equal(0, stored.Status);
        Assert.True(stored.FromApi);
        Assert.Single(await CommandGateway.ListAsync(new CommandFilter(Status: 0, Take: 10)));
    }

    [Fact]
    public async Task ACommandIsFoundByAWindowGivenInUtc()
    {
        if (TimeZoneInfo.Local.BaseUtcOffset == TimeSpan.Zero) return; // su una macchina in UTC il test non distingue nulla
        // Le colonne di data sono marcate con DateTime.Now; un client che manda una data in "O"
        // porta l'offset e il binder la consegna in Kind.Utc. La facciata la riporta a locale.
        var id = await CommandGateway.EnqueueAsync(new CommandRecord { CommandType = 1001 });
        var from = DateTime.UtcNow.AddMinutes(-1);
        var to = DateTime.UtcNow.AddMinutes(1);
        Assert.Equal(DateTimeKind.Utc, from.Kind);
        var found = await CommandGateway.ListAsync(new CommandFilter(From: from, To: to, Take: 10));
        Assert.Contains(found, c => c.Id == id);
    }

    [Fact]
    public async Task ACommandKeepsTheDateItBringsAndTakesNowOtherwise()
    {
        // Le tracce a posteriori del WMS portano la data dell'operazione, che non e' adesso.
        var given = new DateTime(2026, 3, 4, 5, 6, 7);
        var id = await CommandGateway.EnqueueAsync(new CommandRecord { CommandType = 1001, Status = 2, Date = given });
        Assert.Equal(given, (await CommandGateway.FindAsync(id))!.Date);

        // In UTC (il binder consegna Kind.Utc quando la data porta l'offset): riportata a locale come le altre colonne.
        var utc = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        var idUtc = await CommandGateway.EnqueueAsync(new CommandRecord { CommandType = 1001, Status = 2, Date = utc });
        Assert.Equal(utc.ToLocalTime(), (await CommandGateway.FindAsync(idUtc))!.Date);

        var before = DateTime.Now.AddSeconds(-1);
        var idNow = await CommandGateway.EnqueueAsync(new CommandRecord { CommandType = 1001 });
        Assert.InRange((await CommandGateway.FindAsync(idNow))!.Date, before, DateTime.Now.AddSeconds(1));
    }

    [Fact]
    public async Task ACommandCanBeUpdatedAndAMissingOneSaysSo()
    {
        var id = await CommandGateway.EnqueueAsync(new CommandRecord { CommandType = 1001 });
        Assert.Equal(CommandGateway.UpdateResults.Updated, await CommandGateway.UpdateAsync(id, status: 2, errorDescription: null, info: "fatto", showStatus: 1));
        Assert.Equal(CommandGateway.UpdateResults.NotFound, await CommandGateway.UpdateAsync(id + 100, 2, null, null, null));
        // Il comando e' ora Done: riportarlo a ToDo lo rimetterebbe in coda.
        Assert.Equal(CommandGateway.UpdateResults.StatusGoesBack, await CommandGateway.UpdateAsync(id, 0, null, null, null));
        // «Segna come letto» con una fotografia vecchia (stato 0) scrive solo ShowStatus: lo stato resta Done.
        Assert.Equal(CommandGateway.UpdateResults.Updated, await CommandGateway.UpdateAsync(id, 0, null, null, 2));
        Assert.Equal(2, (await CommandGateway.FindAsync(id))!.Status);
        Assert.Equal(2, (await CommandGateway.FindAsync(id))!.ShowStatus);
        Assert.Equal(CommandGateway.UpdateResults.UnknownStatus, await CommandGateway.UpdateAsync(id, 7, null, null, null));
        Assert.Equal("fatto", (await CommandGateway.FindAsync(id))!.Info);
    }

    [Fact]
    public async Task TheQueueAnswersWhetherALoadUnitOrACellIsQueued()
    {
        await using (var context = _store.CreateContext())
        {
            await ExtractionQueueStore.UpsertAsync(context, new ExtractionQueueItem { LoadUnitId = 42, CellId = 9, SourceItemId = Guid.NewGuid() });
            await context.SaveChangesAsync();
        }
        Assert.True(await ExtractionQueueQueries.ContainsLoadUnitAsync(42));
        Assert.False(await ExtractionQueueQueries.ContainsLoadUnitAsync(43));
        Assert.Equal([9m], await ExtractionQueueQueries.CellsAsync());
        Assert.Equal([42m], await ExtractionQueueQueries.LoadUnitsAsync());
        var row = Assert.Single(await ExtractionQueueQueries.ListAsync(processed: null, loadUnitId: null, take: 10));
        Assert.True(await ExtractionQueueQueries.SetPriorityAsync(row.Id, 7));
        Assert.Equal(7, (await ExtractionQueueQueries.FindAsync(row.Id))!.Priority);
    }

    [Fact]
    public async Task DoorMappingsAreReplacedPerItemAndAutomaticCellsFollowTheRelocator()
    {
        // Da questo compito («run sola lettura») ReplaceDoorMappingsAsync chiede anche lei il
        // motore fermo, come RemovingAnItemOrAJunctionThatIsNotThereSaysSo qui sotto: il costruttore
        // della classe mette Run per gli altri test delle facciate.
        Navigator.NavStatus = NavigatorStatusTypes.Stop;

        var storage = Guid.NewGuid();
        var relocator = Guid.NewGuid();
        await using (var context = _store.CreateContext())
        {
            context.PlantItems.Add(new PlantItem { ItemId = storage, RelocateItemId = relocator });
            context.PlantItems.Add(new PlantItem { ItemId = relocator });
            await context.SaveChangesAsync();
        }
        await PlantQueries.ReplaceDoorMappingsAsync(
        [
            new DoorMapping { ItemId = storage, DoorCode = "A", CellId = 100 },
            new DoorMapping { ItemId = relocator, DoorCode = "B", CellId = 200 }
        ]);
        Assert.Equal(2, (await PlantQueries.DoorMappingsAsync()).Count);
        var automatic = Assert.Single(await PlantQueries.AutomaticCellsAsync());
        Assert.Equal((100m, storage, relocator), (automatic.CellId, automatic.ItemId, automatic.RelocatorItemId));
    }

    [Fact]
    public async Task ALogEntryAppendedIsReadBackAndTransitTimesPairStartAndEnd()
    {
        var mission = Guid.NewGuid();
        // Nel mese corrente: in memoria lo storico ha solo il mese corrente (HistoryRangeNotAvailableException).
        var t0 = FirstOfThisMonth.AddDays(1).AddHours(10);
        await HistoryQueries.AppendLogAsync(new NavigatorLogEntry { Date = t0, Asi = "0100", MissionId = mission, Status = 1 });
        await HistoryQueries.AppendLogAsync(new NavigatorLogEntry { Date = t0.AddSeconds(90), Asi = "0200", MissionId = mission, Status = 1 });
        Assert.Equal(2, (await HistoryQueries.LogAsync(t0.AddMinutes(-1), t0.AddMinutes(5), itemId: null, missionId: mission, take: 10)).Count);
        var transit = Assert.Single(await HistoryQueries.TransitTimesAsync(t0.AddMinutes(-1), t0.AddMinutes(5), "0100", "0200"));
        Assert.Equal(90, transit.Seconds);
    }

    [Fact]
    public async Task ARowOnBothPatternsIsADepartureAndAnArrivalButNotItsOwnArrival()
    {
        // "01%" and "0%" both take 0100: it is the departure, and the arrival is the first row on
        // "0%" strictly after it; equal dates go to the row written first.
        var mission = Guid.NewGuid();
        var other = Guid.NewGuid();
        var t0 = FirstOfThisMonth.AddDays(1).AddHours(10);
        await HistoryQueries.AppendLogAsync(new NavigatorLogEntry { Date = t0, Asi = "0100", MissionId = mission, Status = 1 });
        await HistoryQueries.AppendLogAsync(new NavigatorLogEntry { Date = t0.AddSeconds(30), Asi = "0300", MissionId = mission, Status = 1 });
        await HistoryQueries.AppendLogAsync(new NavigatorLogEntry { Date = t0.AddSeconds(30), Asi = "0200", MissionId = mission, Status = 1 });
        await HistoryQueries.AppendLogAsync(new NavigatorLogEntry { Date = t0, Asi = "0100", MissionId = other, Status = 1 });

        var transit = Assert.Single(await HistoryQueries.TransitTimesAsync(t0.AddMinutes(-1), t0.AddMinutes(5), "01%", "0%"));

        Assert.Equal((mission, "0100", "0300", 30), (transit.MissionId, transit.FromAsi, transit.ToAsi, transit.Seconds));
    }

    [Fact]
    public async Task HandledLoadUnitsAreSummarisedByPeriodAndType()
    {
        var monday = FirstOfThisMonth.AddDays(7); // dentro il mese, di sicuro
        monday = monday.AddDays(-(((int)monday.DayOfWeek + 6) % 7)); // il lunedi' di quella settimana
        if (monday < FirstOfThisMonth) monday = monday.AddDays(7);
        await using (var context = _history.CreateContext())
        {
            context.HandledLoadUnits.Add(new HandledLoadUnit { MissionId = Guid.NewGuid(), Type = 1, Date = monday.AddHours(8), ProcessId = 5 });
            context.HandledLoadUnits.Add(new HandledLoadUnit { MissionId = Guid.NewGuid(), Type = 1, Date = monday.AddDays(1).AddHours(9) });
            context.HandledLoadUnits.Add(new HandledLoadUnit { MissionId = Guid.NewGuid(), Type = 2, Date = monday.AddDays(1).AddHours(10) });
            await context.SaveChangesAsync();
        }
        var from = monday; var to = monday.AddDays(2);

        var byDay = await HistoryQueries.HandledLoadUnitSummaryAsync(from, to, SummaryPeriod.Day);
        Assert.Equal(3, byDay.Count);
        Assert.Equal((DateOnly.FromDateTime(monday), (short)1, 1), (byDay[0].Period, byDay[0].Type, byDay[0].Count));

        var byWeek = await HistoryQueries.HandledLoadUnitSummaryAsync(from, to, SummaryPeriod.Week);
        Assert.Equal(2, byWeek.Count);
        Assert.Equal((DateOnly.FromDateTime(monday), (short)1, 2), (byWeek[0].Period, byWeek[0].Type, byWeek[0].Count));

        var byMonth = await HistoryQueries.HandledLoadUnitSummaryAsync(from, to, SummaryPeriod.Month);
        Assert.Equal(DateOnly.FromDateTime(FirstOfThisMonth), byMonth[0].Period);
        Assert.Equal(2, byMonth.Count);

        Assert.True(await HistoryQueries.HandledLoadUnitExistsAsync(processId: 5, loadUnitProcessId: null, noteHeaderId: null));
        Assert.False(await HistoryQueries.HandledLoadUnitExistsAsync(processId: 6, loadUnitProcessId: null, noteHeaderId: null));
    }

    [Fact]
    public async Task AnIntermediateStatusIsRefusedAndDoneIsKept()
    {
        var done = await CommandGateway.EnqueueAsync(new CommandRecord { CommandType = 1001, Status = 2 });
        Assert.Equal(2, (await CommandGateway.FindAsync(done))!.Status);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            CommandGateway.EnqueueAsync(new CommandRecord { CommandType = 1001, Status = 1 }));
    }

    [Fact]
    public async Task AMonthBeforeThisOneIsRefusedInsteadOfAnsweringEmpty()
    {
        var before = FirstOfThisMonth.AddDays(-1);
        await Assert.ThrowsAsync<HistoryRangeNotAvailableException>(() =>
            HistoryQueries.LogAsync(before, DateTime.Now, itemId: null, missionId: null, take: 10));
        await Assert.ThrowsAsync<HistoryRangeNotAvailableException>(() =>
            HistoryQueries.HandledLoadUnitsAsync(before, DateTime.Now, null, null, null, 10));
    }

    [Fact]
    public async Task WithoutAStoreTheFacadesSayItInsteadOfPretending()
    {
        Navigator.Store = null;
        Navigator.History = null;
        try
        {
            await Assert.ThrowsAsync<EngineStoreUnavailableException>(() =>
                CommandGateway.ListAsync(new CommandFilter(Take: 10)));
            await Assert.ThrowsAsync<EngineStoreUnavailableException>(() =>
                CommandGateway.EnqueueAsync(new CommandRecord { CommandType = 1001 }));
            await Assert.ThrowsAsync<EngineStoreUnavailableException>(() =>
                HistoryQueries.LogAsync(FirstOfThisMonth, DateTime.Now, null, null, 10));
            await Assert.ThrowsAsync<EngineStoreUnavailableException>(() =>
                HistoryQueries.AppendLogAsync(new NavigatorLogEntry { Status = 1 }));
            await Assert.ThrowsAsync<EngineStoreUnavailableException>(CellSearchQueries.ContextAsync);
        }
        finally
        {
            Navigator.Store = _store;
            Navigator.History = _history;
        }
    }

    [Fact]
    public void RequiresRunningEngineExemptsTheOperatorTracesAndTheNavigatorCommandsButNotDeviceOrItemCommands()
    {
        // Le quattro tracce dell'operatore arrivano gia' Done: il motore non le esegue, quindi non richiedono Run.
        Assert.False(CommandGateway.RequiresRunningEngine(4002)); // ModifyRushMoverStatus
        Assert.False(CommandGateway.RequiresRunningEngine(4004)); // ChangeCellLockStatus
        Assert.False(CommandGateway.RequiresRunningEngine(4005)); // AddOrModifyLuPosition
        Assert.False(CommandGateway.RequiresRunningEngine(4006)); // ModifyDatabaseCellContents
        Assert.False(CommandGateway.RequiresRunningEngine(1001)); // SetNavigatorRun: sempre ammesso
        Assert.True(CommandGateway.RequiresRunningEngine(2004)); // ItemUnloadLu: richiede il motore avviato
        Assert.True(CommandGateway.RequiresRunningEngine(3007)); // MoveLuToItem: richiede il motore avviato
    }

    [Fact]
    public async Task ExcludeUserIdAlsoExcludesTheRowsWithoutAUser()
    {
        var mario = await CommandGateway.EnqueueAsync(new CommandRecord { CommandType = 1001, UserId = "MARIO" });
        var orchestra = await CommandGateway.EnqueueAsync(new CommandRecord { CommandType = 1001, UserId = "ORCHESTRA_ROUTE" });
        var noUser = await CommandGateway.EnqueueAsync(new CommandRecord { CommandType = 1001, UserId = null });

        var found = await CommandGateway.ListAsync(new CommandFilter(ExcludeUserId: "ORCHESTRA_ROUTE", Take: 1000));

        Assert.Contains(found, c => c.Id == mario);
        Assert.DoesNotContain(found, c => c.Id == orchestra);
        Assert.DoesNotContain(found, c => c.Id == noUser);
    }

    [Fact]
    public async Task RemovingAnItemOrAJunctionThatIsNotThereSaysSo()
    {
        // Da I7 la cancellazione e' rifiutata in Run, come le sue sorelle; il costruttore della
        // classe mette Run per gli altri test delle facciate. Qui si guarda il «non c'era»
        // (falso), che vuole il motore fermo. La guardia su Run ha il suo collaudo in
        // PlantDeletionGuardsTest, lato host.
        Navigator.NavStatus = NavigatorStatusTypes.Stop;

        Assert.False(await PlantQueries.RemoveItemAsync(Guid.NewGuid()));
        Assert.False(await PlantQueries.RemoveJunctionAsync(Guid.NewGuid()));

        var itemId = Guid.NewGuid();
        await using (var context = _store.CreateContext())
        {
            context.PlantItems.Add(new PlantItem { ItemId = itemId });
            await context.SaveChangesAsync();
        }
        Assert.True(await PlantQueries.RemoveItemAsync(itemId));
        Assert.False(await PlantQueries.RemoveItemAsync(itemId));
    }

    [Fact]
    public async Task PlcConnectionsAreReplacedAsAWhole()
    {
        // Le connessioni si sostituiscono solo a motore fermo (PlantQueries.RequireStopped), e questa
        // classe parte in Run.
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        await PlcSettingsFacade.ReplaceAsync([new PlcConnection { Id = 1, Description = "uno" }, new PlcConnection { Id = 2, Description = "due" }]);
        await PlcSettingsFacade.ReplaceAsync([new PlcConnection { Id = 2, Description = "due bis" }]);
        var only = Assert.Single(await PlcSettingsFacade.ConnectionsAsync());
        Assert.Equal((2L, "due bis"), (only.Id, only.Description));
    }

    [Fact]
    public void MissionExistsCanBeRestrictedToSomeStatuses()
    {
        var mission = new PathMission(Guid.NewGuid()) { OperationType = OperationTypes.Outbound, LuId = 4242, LuCode = "UDC4242" };
        ActiveMissions.Instance.Add(mission, buildRoute: false);
        try
        {
            Assert.True(MissionQueries.ExistsForLoadUnit(4242));
            Assert.True(MissionQueries.ExistsForLoadUnit(4242, [MissionStatusTypes.Run, MissionStatusTypes.Stop]));
            mission.Status = MissionStatusTypes.Error;
            // Senza filtro la missione in errore c'e' ancora (e' in memoria); con STATUS IN (0,1) no.
            Assert.True(MissionQueries.ExistsForLoadUnit(4242));
            Assert.True(MissionQueries.ExistsForLoadUnit(4242, []));
            Assert.False(MissionQueries.ExistsForLoadUnit(4242, [MissionStatusTypes.Run, MissionStatusTypes.Stop]));
            Assert.False(MissionQueries.ExistsForLoadUnit(4243, [MissionStatusTypes.Run]));
        }
        finally { ActiveMissions.Instance.RemoveAll(m => m.MissionId == mission.MissionId); }
    }

    /// <summary>
    /// Compito 14: <see cref="ExtractionQueueQueries.ContainsLoadUnitAsync"/> guadagna una seconda
    /// esclusione indipendente dalla prima (<c>excludeRow</c>, sullo spazio delle estrazioni per
    /// unita' di carico): <c>excludeAssignmentId</c>, sullo spazio delle assegnazioni di nota.
    /// </summary>
    [Fact]
    public async Task TheQueueCanExcludeARowCarryingAGivenNoteAssignment()
    {
        await using (var context = _store.CreateContext())
        {
            await ExtractionQueueStore.UpsertAsync(context, new ExtractionQueueItem { LoadUnitId = 4245, AssignmentId = 910, SourceItemId = Guid.NewGuid() });
            await context.SaveChangesAsync();
        }

        Assert.True(await ExtractionQueueQueries.ContainsLoadUnitAsync(4245));
        Assert.False(await ExtractionQueueQueries.ContainsLoadUnitAsync(4245, excludeAssignmentId: 910));
        Assert.True(await ExtractionQueueQueries.ContainsLoadUnitAsync(4245, excludeAssignmentId: 911));
    }

    /// <summary>
    /// <see cref="MissionQueries.HoldsLoadUnit"/> non e' <see cref="MissionQueries.ExistsForLoadUnit"/>
    /// con un altro filtro: e' la domanda «il motore ha gia' questa UDC?», che conta le missioni
    /// attive <b>a qualunque stato</b> - una missione in Error tiene la UDC come una in Run. Chi
    /// valida un'assegnazione di nota deve chiedere questo, perche' e' lo stesso insieme su cui
    /// <c>Navigator.IsLuInExtraction</c> ferma poi il comando di estrazione: con la domanda piu'
    /// stretta l'assegnazione passava la validazione, tornava al motore e il comando usciva in
    /// silenzio, senza essere ne' servita ne' liberata.
    /// </summary>
    [Fact]
    public void HoldsLoadUnitCountsAMissionInErrorTooUnlikeMissionExists()
    {
        var mission = new PathMission(Guid.NewGuid())
        { OperationType = OperationTypes.Outbound, LuId = 4246, LuCode = "UDC4246", Status = MissionStatusTypes.Error };
        ActiveMissions.Instance.Add(mission, buildRoute: false);
        try
        {
            Assert.False(MissionQueries.ExistsForLoadUnit(4246, [MissionStatusTypes.Run, MissionStatusTypes.Stop]));
            Assert.True(MissionQueries.HoldsLoadUnit(4246));

            mission.Status = MissionStatusTypes.Done;
            Assert.True(MissionQueries.HoldsLoadUnit(4246));

            Assert.False(MissionQueries.HoldsLoadUnit(4247));
        }
        finally { ActiveMissions.Instance.RemoveAll(m => m.MissionId == mission.MissionId); }
    }

    // L'esclusione per assegnazione (excludeAssignmentId) sta in
    // MissionCarriesItsNoteAssignmentTest, non qui. Il test che stava in questo punto costruiva la
    // missione a mano e le scriveva lui LuNoteAssignmentId: provava il confronto della facciata, e
    // restava verde mentre in esercizio nessuno valorizzava quel campo, cioe' mentre l'esclusione
    // non escludeva mai. Il test nuovo parte dal lavoro pronto del gestionale e non scrive nessun
    // campo della missione: fallisce se il legame non viene scritto dove la missione nasce.

    /// <summary>
    /// Il secondo dei tre posti che <c>Navigator.GetMissionByLuId</c> guarda: una UDC che nessuna
    /// missione attiva porta, ma che un dispositivo sta prelevando (<c>LUtoPick</c>), e' comunque
    /// tenuta dal motore. <see cref="MissionQueries.ExistsForLoadUnit"/> guarda solo l'elenco delle
    /// attive e non la vede.
    /// </summary>
    /// <remarks>
    /// Il dispositivo <b>non ha niente a bordo</b>, che e' il caso normale di chi sta prelevando, ed
    /// e' quello che il collaudo di prima non aveva: quel collaudo metteva sul dispositivo una
    /// missione con un'altra UDC, e passava perche' la ricerca restituiva <i>quella</i> — cioe'
    /// rispondeva «si'» guardando la missione sbagliata. Senza la missione a bordo, la ricerca
    /// rispondeva <c>null</c> e la UDC risultava libera.
    /// </remarks>
    [Fact]
    public void HoldsLoadUnitSeesALoadUnitHeldByADeviceWithNoActiveMissionOfItsOwn()
    {
        var device = new AutomationDevice(Guid.NewGuid()) { DeviceType = DeviceTypes.CircularDistributor };
        PathDevices.Instance.Add(device);

        var beingPicked = new PathMission(Guid.NewGuid())
        { OperationType = OperationTypes.Outbound, LuId = 4249, LuCode = "UDC4249" };
        device.LUtoPick = beingPicked;

        try
        {
            Assert.Null(device.LoadedLu);
            Assert.False(MissionQueries.ExistsForLoadUnit(4249));
            Assert.True(MissionQueries.HoldsLoadUnit(4249));
            Assert.Same(beingPicked, Navigator.GetMissionByLuId(4249));
        }
        finally
        {
            device.LUtoPick = null;
            PathDevices.Instance.Remove(device.ItemGuid);
        }
    }

    /// <summary>
    /// Con una missione a bordo che porta un'altra UDC, la ricerca deve rispondere quella che sta
    /// prelevando, non quella a bordo. E' il difetto vero: cercava per <c>LUtoPick</c> e
    /// restituiva <c>LoadedLu</c>.
    /// </summary>
    [Fact]
    public void TheLoadUnitBeingPickedIsNotConfusedWithTheOneOnBoard()
    {
        var device = new AutomationDevice(Guid.NewGuid()) { DeviceType = DeviceTypes.CircularDistributor };
        PathDevices.Instance.Add(device);

        var onBoard = new PathMission(Guid.NewGuid())
        { OperationType = OperationTypes.Outbound, LuId = 4250, LuCode = "UDC4250", PositionItemId = device.ItemGuid };
        ActiveMissions.Instance.Add(onBoard, buildRoute: false);

        var beingPicked = new PathMission(Guid.NewGuid())
        { OperationType = OperationTypes.Outbound, LuId = 4249, LuCode = "UDC4249" };
        device.LUtoPick = beingPicked;

        try
        {
            Assert.Same(onBoard, device.LoadedLu);
            Assert.Same(beingPicked, Navigator.GetMissionByLuId(4249));
            Assert.Same(onBoard, Navigator.GetMissionByLuId(4250));
        }
        finally
        {
            device.LUtoPick = null;
            ActiveMissions.Instance.RemoveAll(m => m.MissionId == onBoard.MissionId);
            PathDevices.Instance.Remove(device.ItemGuid);
        }
    }
}
