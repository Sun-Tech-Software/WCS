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
/// L'annuncio dei due dispositivi toccati da uno spostamento (voce 31 della coda): quello lasciato
/// e quello raggiunto.
/// </summary>
/// <remarks>
/// <para>
/// Carico e UDC a bordo di un dispositivo il motore li calcola dalle missioni, quindi spostare una
/// missione cambia lo stato di due dispositivi, e la pagina lo sa solo da
/// <c>FireStatusLuChanged</c>. Quell'annuncio stava in fondo al lavoro di sfondo del setter
/// <see cref="PathMission.PositionItemId"/>, dopo le chiamate al gestionale: con il gestionale che
/// non rispondeva l'eccezione di <c>AllocateLoadUnitAsync</c> lo saltava, e sulla tela l'UDC restava
/// dove non era piu' (giro dal vivo del 30/9 sulla copia di DEMOPLANT).
/// </para>
/// <para>
/// Il navigatore sta in <c>Stop</c>, non in <c>Recover</c>: in Recover
/// <c>ProcessAllocateOnItem</c> esce subito e il gestionale non viene mai chiamato, cioe' proprio
/// il ramo da provare non gira. Le missioni si aggiungono in Recover perche'
/// <c>ActiveMissions.Add</c> esce subito in Stop, e passare da Recover a Stop non sveglia i
/// dispositivi dei test precedenti come farebbe il passaggio in Run.
/// </para>
/// </remarks>
public sealed class MissionMoveAnnouncementTest : IDisposable
{
    private const decimal LinkedCell = 610;
    private const decimal LoadUnit = 4242;
    private const decimal StorageCell = 620;
    private const string StorageDoor = "S01A";
    private const string Allocation = nameof(FakeWarehouse.AllocateLoadUnitAsync);

    /// <summary>
    /// Il registro degli eventi del motore, in ordine di arrivo, con in mezzo le chiamate al
    /// gestionale che passano da <see cref="FakeWarehouse.Before"/>: l'allocazione nella cella
    /// collegata (<c>AllocateLoadUnitAsync</c>) si', le segnalazioni della coda di estrazione
    /// (<c>ReportMissionServedAssignmentAsync</c>, <c>ConfirmServedAssignmentsAsync</c>) no, e qui non
    /// si vedono. Gli annunci arrivano dal thread pool, quindi si scrive sotto lock; quello che
    /// l'annuncio riporta - il posto in attesa e la missione a bordo - si legge al momento
    /// dell'annuncio, come fa l'host quando costruisce il DTO.
    /// </summary>
    private sealed class Recorder : IEngineEventSink
    {
        private readonly Lock _lock = new();
        private readonly List<(string Kind, Guid ItemId, Guid WaitingStepId, Guid? LoadedMission)> _events = [];

        public IReadOnlyList<(string Kind, Guid ItemId, Guid WaitingStepId, Guid? LoadedMission)> Events
        {
            get
            {
                lock (_lock) return [.. _events];
            }
        }

        public void WarehouseCalled(string operation)
        {
            lock (_lock) _events.Add(($"wms:{operation}", Guid.Empty, Guid.Empty, null));
        }

        public void ItemChanged(AutomationDevice device)
        {
            lock (_lock) _events.Add(("item", device.ItemGuid, device.WaitingStepId, device.LoadedLu?.MissionId));
        }

        public void NavigatorStatusChanged() { }
        public void MissionChanged(PathMission mission, EngineMissionChange change) { }
        public void ExtractionQueueChanged(ExtractionQueueItem item, EngineQueueChange change) { }
        public void CommandChanged(CommandRecord command) { }
    }

    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly FakeWarehouse _warehouse = new();
    private readonly RoutingTestPlant _plant = new();
    private readonly Recorder _recorder = new();

    public MissionMoveAnnouncementTest()
    {
        Navigator.Store = _database;
        Navigator.Warehouse = _warehouse;
        _warehouse.Before = operation =>
        {
            _recorder.WarehouseCalled(operation);
            return Task.CompletedTask;
        };
    }

    public void Dispose()
    {
        Navigator.EventSink = null;
        RoutesToBuild.Instance.Items = new();
        _plant.Dispose();
        Navigator.Warehouse = _previousWarehouse;
        Navigator.Store = null;
        _database.Dispose();
    }

    /// <summary>
    /// Il caso della voce 31: il gestionale rifiuta la registrazione nella cella collegata al
    /// dispositivo raggiunto, e i due dispositivi vengono annunciati lo stesso, con l'UDC spostata:
    /// <c>from</c> vuoto, <c>to</c> con la missione a bordo.
    /// </summary>
    [Fact]
    public async Task AWarehouseThatFailsTheAllocationDoesNotSilenceTheTwoDevices()
    {
        var (mission, from, to) = await AMissionReadyToMove();
        _warehouse.UnavailableOperations.Add(Allocation);

        Assert.Equal(MissionMoveOutcome.Moved, MissionGateway.Move(mission.MissionId, to.ItemGuid));
        await mission.PositionWork;

        // La chiamata c'e' stata ed e' fallita: senza questi due controlli la prova potrebbe
        // passare per una strada che non arriva al gestionale.
        Assert.Contains(Allocation, _warehouse.Calls);
        Assert.Empty(_warehouse.AllocatedLoadUnits);

        TheUnitIsAnnouncedOnTo(_recorder.Events, mission, from, to);
    }

    /// <summary>
    /// La meta' che deve restare com'era: con il gestionale che risponde la registrazione avviene
    /// e i due dispositivi sono annunciati.
    /// </summary>
    [Fact]
    public async Task AWarehouseThatAnswersRegistersTheAllocationAndTheTwoDevicesAreAnnounced()
    {
        var (mission, from, to) = await AMissionReadyToMove();

        Assert.Equal(MissionMoveOutcome.Moved, MissionGateway.Move(mission.MissionId, to.ItemGuid));
        await mission.PositionWork;

        var allocation = Assert.Single(_warehouse.AllocatedLoadUnits);
        Assert.Equal(LoadUnit, allocation.LoadUnitId);
        Assert.Equal(LinkedCell, allocation.CellId);

        TheUnitIsAnnouncedOnTo(_recorder.Events, mission, from, to);
    }

    /// <summary>
    /// L'annuncio non aspetta il gestionale: quando parte l'allocazione nella cella collegata i due
    /// dispositivi sono gia' stati annunciati, e gia' con l'UDC spostata (quello che riportano lo
    /// calcola il motore dalle missioni, ed e' gia' giusto prima del gestionale). Un gestionale che va
    /// in timeout invece di rifiutare costa fino a 30 s per chiamata, e un annuncio messo dopo (anche
    /// in un <c>finally</c>) terrebbe ferma la tela per tutto quel tempo. Le segnalazioni della coda
    /// di estrazione, che il lavoro di sfondo fa prima dell'allocazione, il registro non le vede
    /// (<see cref="Recorder"/>): qui non c'e' una coda di estrazione, e non partono.
    /// </summary>
    [Fact]
    public async Task TheTwoDevicesAreAnnouncedBeforeTheWarehouseIsCalled()
    {
        var (mission, from, to) = await AMissionReadyToMove();

        Assert.Equal(MissionMoveOutcome.Moved, MissionGateway.Move(mission.MissionId, to.ItemGuid));
        await mission.PositionWork;

        var events = _recorder.Events;
        var call = events.ToList().FindIndex(e => e.Kind == $"wms:{Allocation}");
        Assert.True(call >= 0, "il gestionale non e' stato chiamato");

        TheUnitIsAnnouncedOnTo([.. events.Take(call)], mission, from, to);
    }

    /// <summary>
    /// Il lavoro di sfondo puo' cambiare cio' che l'annuncio riporta, e allora si annuncia anche
    /// dopo, qualunque cosa risponda il gestionale.
    /// </summary>
    /// <remarks>
    /// Il caso vero: una missione <c>PickUpAndStoreAgain</c> che arriva alla sua destinazione viene
    /// rigirata verso il magazzino (<c>HandlePickUpAndStoreAgainMission</c>), e la nuova
    /// destinazione scarta la rotta vecchia. Se l'ultimo passo e' rimasto addosso alla sorgente
    /// dopo un comando non passato (<c>Warning</c>: non e' in volo), scartarlo azzera il suo
    /// <c>RunningStepId</c>, e il setter restituisce il posto che il dispositivo raggiunto teneva
    /// per quel passo senza annunciarlo. L'arrivo si scrive con il setter, come fa il campo
    /// (<c>LoadLu</c>), non con <see cref="MissionGateway.Move"/>, che scarta la rotta per conto
    /// suo.
    /// </remarks>
    [Fact]
    public async Task WhatTheBackgroundWorkChangesIsAnnouncedAfterItEvenIfTheWarehouseFails()
    {
        var from = _plant.Device("from");
        var to = _plant.Device("to");
        var storage = _plant.Device("storage", DeviceTypes.Storage);
        _plant.Link("from", "to");
        _plant.Link("to", "storage");
        to.LinkedCellId = LinkedCell;

        await PlantPersistence.SaveDoorMappingsAsync(storage.ItemGuid,
            [new DoorMapping { ItemId = storage.ItemGuid, DoorCode = StorageDoor, CellId = StorageCell, Side = "A" }]);

        var mission = new PathMission
        {
            PositionItemId = from.ItemGuid,
            DestinationItemId = to.ItemGuid,
            MissionType = MissionTypes.PickUpAndStoreAgain,
            StorageDoorCode = StorageDoor,
            LuId = LoadUnit
        };
        ActiveMissions.Instance.Add(mission, buildRoute: false);
        await mission.PositionWork;

        var lastStep = _plant.PlannedStep(mission, "from", "to");
        from.RunningStepId = lastStep.StepId;
        from.Status = DeviceStatusTypes.Warning;
        Assert.True(to.TryReserveForIncomingStep(lastStep.StepId));

        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        Navigator.EventSink = _recorder;
        _warehouse.UnavailableOperations.Add(Allocation);

        mission.PositionItemId = to.ItemGuid;
        await mission.PositionWork;

        // Il lavoro di sfondo ha preso davvero il ramo che cambia lo stato del dispositivo, ed e'
        // arrivato fino al gestionale che rifiuta.
        Assert.Equal(storage.ItemGuid, mission.DestinationItemId);
        Assert.Equal(Guid.Empty, to.WaitingStepId);
        Assert.Contains(Allocation, _warehouse.Calls);
        Assert.Empty(_warehouse.AllocatedLoadUnits);

        // Il primo annuncio di to, prima del lavoro di sfondo, riportava ancora il posto tenuto per
        // l'ultimo passo; l'ultimo lo riporta libero. Senza il primo, l'ultimo non direbbe che lo
        // stato e' cambiato fra i due.
        var announcedTo = _recorder.Events.Where(e => e.Kind == "item" && e.ItemId == to.ItemGuid).ToList();
        Assert.Equal(lastStep.StepId, announcedTo.First().WaitingStepId);
        Assert.Equal(Guid.Empty, announcedTo.Last().WaitingStepId);
    }

    /// <summary>
    /// Fra gli annunci dati, <paramref name="from"/> e <paramref name="to"/> ci sono, e riportano
    /// l'UDC spostata: <paramref name="from"/> senza missione a bordo, <paramref name="to"/> con
    /// <paramref name="mission"/>.
    /// </summary>
    private static void TheUnitIsAnnouncedOnTo(
        IReadOnlyList<(string Kind, Guid ItemId, Guid WaitingStepId, Guid? LoadedMission)> events,
        PathMission mission, AutomationDevice from, AutomationDevice to)
    {
        var items = events.Where(e => e.Kind == "item").ToList();
        Assert.Contains(items, e => e.ItemId == from.ItemGuid);
        Assert.Contains(items, e => e.ItemId == to.ItemGuid);
        Assert.All(items.Where(e => e.ItemId == from.ItemGuid), e => Assert.Null(e.LoadedMission));
        Assert.All(items.Where(e => e.ItemId == to.ItemGuid), e => Assert.Equal(mission.MissionId, e.LoadedMission));
    }

    /// <summary>
    /// Una missione con un'UDC a bordo di <c>from</c>, e un <c>to</c> con la cella collegata: e' la
    /// cella che fa chiamare al lavoro di sfondo <c>AllocateLoadUnitAsync</c>. Il lavoro del primo
    /// posizionamento si aspetta prima di mettersi in ascolto, altrimenti il suo annuncio di
    /// <c>from</c> potrebbe finire nel registro e far passare la prova per il motivo sbagliato.
    /// </summary>
    private async Task<(PathMission Mission, AutomationDevice From, AutomationDevice To)> AMissionReadyToMove()
    {
        var from = _plant.Device("from");
        var to = _plant.Device("to");
        to.LinkedCellId = LinkedCell;

        var mission = new PathMission { PositionItemId = from.ItemGuid, LuId = LoadUnit };
        ActiveMissions.Instance.Add(mission, buildRoute: false);
        await mission.PositionWork;
        Assert.Same(mission, from.LoadedLu);

        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        Navigator.EventSink = _recorder;

        return (mission, from, to);
    }

}
