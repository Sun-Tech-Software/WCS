using Wcs.Engine.Collections;
using Wcs.Engine.Items;
using Wcs.Engine.ORM;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Wcs.Wms.Contracts;
using Wcs.Wms.Contracts.Test;
using Xunit;
using AutomationDevice = Wcs.Engine.Items.AutomationDevice;
using Command = Wcs.Engine.Items.Command;
using PathMission = Wcs.Engine.Items.PathMission;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// Compito 18: la decisione dello svuotamento fisico di una cella
/// (<c>ExecutableCommands.EmptyCellPisically</c>) passa dal confine invece che dai servizi del
/// gestionale caricati nel processo del motore. Sono due operazioni nuove che vanno insieme -
/// <see cref="IWarehouse.GetCellPositionsAsync"/> legge le posizioni della cella con lo stato di
/// prenotazione, <see cref="IWarehouse.ClearCellPositionsAsync"/> le cancella tutte - e il passo che
/// resta di qua: «nessuna delle unità di carico prenotate è in missione», che solo il motore sa
/// perché le missioni attive sono sue.
/// <para>
/// Gli esiti sono test distinti perché sono cose diverse che un operatore legge quando il comando
/// non parte: tutte prenotate → rifiuto; qualcuna prenotata ma nessuna in missione → procede;
/// qualcuna prenotata e una in missione → rifiuto, con un messaggio diverso dal primo; nessuna
/// posizione → rifiuto, con il messaggio del primo.
/// </para>
/// </summary>
public sealed class EmptyCellDecisionTest : IDisposable
{
    private const string SourceDoorCode = "ECD-SRC";
    private const string DestinationDoorCode = "ECD-DST";
    private const decimal SourceCellId = 9100m;
    private const decimal DestinationCellId = 9200m;

    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly NavigatorStatusTypes _previousNavStatus = Navigator.NavStatus;

    /// <summary>
    /// I dispositivi messi nel diagramma, per toglierli alla fine: <c>PathDevices</c> e' statico e
    /// vive quanto il processo dei test, e ogni <see cref="AutomationDevice"/> si iscrive a
    /// <c>Navigator.StatusChanged</c> quando viene costruito.
    /// </summary>
    private readonly List<AutomationDevice> _devices = [];

    /// <summary>Le missioni messe in <see cref="ActiveMissions"/>, per toglierle alla fine.</summary>
    private readonly List<Guid> _missions = [];

    /// <summary>
    /// Il ricollocatore del dispositivo di deposito: e' lui che riceve la missione di svuotamento
    /// (<c>AutomationDevice.AddMissionToEmptyCell</c>), ed e' li' che si va a leggere la modalita'
    /// di movimentazione arrivata dal confine.
    /// </summary>
    private AutomationDevice? _relocator;

    public EmptyCellDecisionTest()
    {
        Navigator.Store = _database;

        // Il navigatore in marcia serve alle missioni, non al metodo sotto prova: ActiveMissions.Add
        // esce subito se lo stato e' Stop o Pause (e Stop e' il valore che il campo statico ha nei
        // test). Senza questa riga i due test che mettono una missione attiva non ne metterebbero
        // nessuna, e quello che si aspetta un rifiuto lo vedrebbe diventare un «procede».
        Navigator.NavStatus = NavigatorStatusTypes.Run;
    }

    public void Dispose()
    {
        // L'esito «procede» arriva fino a AddMissionToEmptyCell, che aggiunge una missione sua: si
        // toglie tutto quello che porta il codice porta di questi test, non solo cio' che si e'
        // aggiunto a mano.
        foreach (var missionId in _missions) ActiveMissions.Instance.RemoveAll(m => m.MissionId == missionId);
        ActiveMissions.Instance.RemoveAll(m => m.PickUpDoorCode == SourceDoorCode);

        // Prima dello Store: togliere un dispositivo dal diagramma cancella anche la sua riga.
        foreach (var device in _devices)
        {
            PathDevices.Instance.Remove(device.ItemGuid);
            device.Dispose();
        }

        Navigator.NavStatus = _previousNavStatus;
        Navigator.Store = null;
        Navigator.Warehouse = _previousWarehouse;
        _database.Dispose();
    }

    private AutomationDevice AddDevice(Guid itemId, DeviceTypes deviceType)
    {
        var device = TestDevices.WithRow(itemId, d => d.DeviceType = deviceType);
        PathDevices.Instance.Add(device);
        _devices.Add(device);
        return device;
    }

    private static CellDescription Cell(decimal cellId, string doorCodeA) => new(
        cellId, AreaId: 1, WarehouseId: 1, Depth: 1, Inverted: false, Locked: CellLockState.None,
        AllowedLoadUnitTypeId: null, GroupId: null, GroupOrder: 0,
        DoorCodeA: doorCodeA, DoorCodeB: string.Empty, DoorCount: 1, MultiDepthStorage: false);

    /// <summary>
    /// Il diagramma minimo che <c>EmptyCellPisically</c> pretende prima di arrivare alla decisione:
    /// una cella sorgente dietro un dispositivo di deposito che ha il suo ricollocatore, e una cella
    /// di destinazione dietro un dispositivo qualunque. Senza uno di questi pezzi il metodo si ferma
    /// prima, con un altro messaggio, e i tre test proverebbero un rifiuto che non e' il loro.
    /// </summary>
    private async Task<FakeWarehouse> PrepareDiagramAsync(List<CellPosition> sourceCellPositions)
    {
        var storageItem = Guid.NewGuid();
        var relocatorItem = Guid.NewGuid();
        var destinationItem = Guid.NewGuid();

        _relocator = AddDevice(relocatorItem, DeviceTypes.CircularDistributor);
        AddDevice(storageItem, DeviceTypes.Storage).RelocateItemId = relocatorItem;
        AddDevice(destinationItem, DeviceTypes.CircularDistributor);

        await PlantPersistence.SaveDoorMappingsAsync(storageItem,
            [new DoorMapping { ItemId = storageItem, DoorCode = SourceDoorCode, CellId = SourceCellId, Side = "A" }]);
        await PlantPersistence.SaveDoorMappingsAsync(destinationItem,
        [
            new DoorMapping
                { ItemId = destinationItem, DoorCode = DestinationDoorCode, CellId = DestinationCellId, Side = "A" }
        ]);

        var warehouse = new FakeWarehouse();
        warehouse.CellsByDoor[SourceDoorCode] = [Cell(SourceCellId, SourceDoorCode)];
        warehouse.CellsByDoor[DestinationDoorCode] = [Cell(DestinationCellId, DestinationDoorCode)];
        warehouse.CellPositions[SourceCellId] = sourceCellPositions;
        Navigator.Warehouse = warehouse;

        return warehouse;
    }

    /// <summary>
    /// Il comando cosi' come lo scrive l'operatore, eseguito in modalita' manuale: e' quella che
    /// lascia il motivo del rifiuto in <c>Command.ErrorDescription</c> invece di riassorbirlo.
    /// Il tipo unita' di carico e' zero perche' la decisione sotto prova non lo guarda; solo la
    /// prova sulla modalita' di movimentazione (Q1) ne passa uno vero.
    /// </summary>
    private static async Task<Command> EmptyTheCellAsync(decimal luTypeId = 0)
    {
        var command = new Command
        {
            CommandType = Command.CommandTypes.EmptyCellPisically,
            PickUpDoorCode = SourceDoorCode,
            StorageDoorCode = DestinationDoorCode,
            WorkstationId = "ECD-WS",
            LuTypeId = luTypeId
        };

        await ExecutableCommands.EmptyCellPhisically(command, ExecutableCommands.ExecuteMoveLuTypes.Manual);

        return command;
    }

    /// <summary>
    /// Primo esito: <b>tutte</b> le posizioni della cella sono prenotate. Il comando non parte, e il
    /// motivo che l'operatore legge e' quello di sempre. La cella non viene toccata: rifiutare e
    /// cancellare sono due cose diverse, e questo test lo dice guardando che
    /// <see cref="IWarehouse.ClearCellPositionsAsync"/> non sia stata chiamata.
    /// </summary>
    [Fact]
    public async Task WhenEveryPositionIsReservedTheCommandIsRefusedAndTheCellIsNotCleared()
    {
        var warehouse = await PrepareDiagramAsync(
            [new CellPosition(4101m, Reserved: true), new CellPosition(4102m, Reserved: true)]);

        var command = await EmptyTheCellAsync();

        Assert.Equal($"The requested cell {SourceDoorCode} have LU on it", command.ErrorDescription);
        Assert.Equal(Command.StatusTypes.Error, command.Status);
        Assert.Empty(warehouse.ClearedCells);
    }

    /// <summary>
    /// Secondo esito: <b>qualcuna</b> delle posizioni e' prenotata, e nessuna delle unita' di carico
    /// prenotate e' in missione. Il comando procede, e la cella resta senza posizioni - tutte, anche
    /// quella non prenotata che le stava insieme.
    /// <para>
    /// La missione attiva che questo test mette c'e' apposta: e' su un'unita' di carico che nella
    /// cella <b>non</b> e' prenotata. Serve a distinguere «nessuna missione attiva» da «nessuna
    /// missione attiva sulle unita' di carico prenotate», che sono due condizioni diverse e solo la
    /// seconda e' quella vera.
    /// </para>
    /// </summary>
    [Fact]
    public async Task WhenSomePositionsAreReservedButNoneOfThemIsOnAMissionTheCellIsCleared()
    {
        var warehouse = await PrepareDiagramAsync(
            [new CellPosition(4201m, Reserved: false), new CellPosition(4202m, Reserved: true)]);

        AddActiveMissionOn(4201m);

        var command = await EmptyTheCellAsync();

        Assert.Equal([SourceCellId], warehouse.ClearedCells);

        // Lo stato preciso, non «diverso da errore»: quello accetterebbe qualunque altro valore -
        // compreso il valore iniziale di un comando mai eseguito.
        Assert.Equal(Command.StatusTypes.Done, command.Status);
    }

    /// <summary>
    /// Quarto ingresso della decisione: la cella <b>non ha nessuna posizione</b>. Il comando viene
    /// rifiutato con lo stesso messaggio del primo esito, perche' <c>All</c> su un elenco vuoto e'
    /// vero - ed e' cosi' che faceva anche prima del compito 18. Era affermato da un commento nel
    /// codice e da nessun collaudo.
    /// </summary>
    [Fact]
    public async Task WhenTheCellHasNoPositionAtAllTheCommandIsRefusedAndTheCellIsNotCleared()
    {
        var warehouse = await PrepareDiagramAsync([]);

        var command = await EmptyTheCellAsync();

        Assert.Equal($"The requested cell {SourceDoorCode} have LU on it", command.ErrorDescription);
        Assert.Equal(Command.StatusTypes.Error, command.Status);
        Assert.Empty(warehouse.ClearedCells);
    }

    /// <summary>
    /// Terzo esito: <b>qualcuna</b> delle posizioni e' prenotata, e una di quelle unita' di carico e'
    /// in missione. Il comando non parte, con un messaggio diverso dal primo esito, e la cella non
    /// viene toccata. E' questo il passo che resta nel motore: il gestionale non ha il dato delle
    /// missioni attive e non potrebbe deciderlo.
    /// </summary>
    [Fact]
    public async Task WhenAReservedLoadUnitIsOnAMissionTheCommandIsRefusedAndTheCellIsNotCleared()
    {
        var warehouse = await PrepareDiagramAsync(
            [new CellPosition(4301m, Reserved: false), new CellPosition(4302m, Reserved: true)]);

        AddActiveMissionOn(4302m);

        var command = await EmptyTheCellAsync();

        Assert.Equal(
            $"Into the requested cell {SourceDoorCode} is stored a LU, the process can't start",
            command.ErrorDescription);
        Assert.Equal(Command.StatusTypes.Error, command.Status);
        Assert.Empty(warehouse.ClearedCells);
    }

    /// <summary>
    /// Q1: lo svuotamento fisico non ha un'unita' di carico reale - la missione nasce con
    /// <c>LuId = 0</c> - e la modalita' di movimentazione arriva dal <b>tipo</b> passato dal
    /// comando, chiesta al confine con <see cref="IWarehouse.GetLoadUnitTypeHandlingModeAsync"/>.
    /// Era <c>DbInfo.GetHandlingMode</c>, che senza database rispondeva zero: il test guarda il
    /// valore sulla missione accodata al ricollocatore, non il fatto che la domanda sia partita,
    /// perche' e' il valore che decide come il campo movimenta l'unita' di carico.
    /// </summary>
    [Fact]
    public async Task TheHandlingModeOfTheTypeReachesTheMissionQueuedOnTheDevice()
    {
        var warehouse = await PrepareDiagramAsync(
            [new CellPosition(4401m, Reserved: false), new CellPosition(4402m, Reserved: true)]);

        warehouse.LoadUnitTypeHandlingModes[7] = new LoadUnitTypeHandlingMode(7, 2);

        var command = await EmptyTheCellAsync(luTypeId: 7);

        Assert.Equal(Command.StatusTypes.Done, command.Status);

        var mission = Assert.Single(_relocator!.QueuedLUtoPickUp,
            m => m != null && m.PickUpDoorCode == SourceDoorCode);

        Assert.Equal(7m, mission!.IdLuType);
        Assert.Equal(2, mission.HandlingMode);
    }

    /// <summary>
    /// Q1, la via del guasto: il gestionale non risponde mentre lo svuotamento sta partendo. E' la
    /// sola cosa che cambia davvero in campo - <c>DbInfo.GetHandlingMode</c> rispondeva zero e la
    /// missione partiva con una modalita' inventata (voce D115), adesso il guasto risale.
    /// <para>
    /// Quello che il test guarda non e' l'eccezione ma il <b>gettone</b>: <c>EmptyCellStatus</c> in
    /// <c>Active</c> lo restituisce solo la fine dello svuotamento
    /// (<c>AutomationDeviceStepExecutor.Private.cs:893</c>), e la guardia in cima al metodo fa
    /// uscire ogni altra chiamata finche' e' preso. Se un guasto del gestionale lo lasciasse preso,
    /// lo svuotamento di questo dispositivo resterebbe fermo fino al riavvio del processo.
    /// </para>
    /// <para>
    /// Si chiama <c>AddMissionToEmptyCell</c> direttamente invece di passare dal comando perche'
    /// <c>EmptyCellPhisically</c> interroga il confine molto prima di arrivare qui: un doppio
    /// indisponibile dall'inizio farebbe fallire la decisione e non proverebbe questo punto.
    /// </para>
    /// </summary>
    [Fact]
    public async Task WhenTheWarehouseIsUnavailableTheEmptyCellTokenIsGivenBack()
    {
        var warehouse = await PrepareDiagramAsync(
            [new CellPosition(4501m, Reserved: false), new CellPosition(4502m, Reserved: true)]);

        var statusBefore = _relocator!.EmptyCellStatus;
        warehouse.Unavailable = true;

        await Assert.ThrowsAsync<WarehouseUnavailableException>(() =>
            _relocator.AddMissionToEmptyCell(SourceDoorCode, DestinationDoorCode, "ECD-WS", idLuType: 7));

        Assert.Equal(statusBefore, _relocator.EmptyCellStatus);

        // Nessuna missione accodata: il guasto non lascia dietro di se' meta' svuotamento.
        Assert.DoesNotContain(_relocator.QueuedLUtoPickUp, m => m != null && m.PickUpDoorCode == SourceDoorCode);
    }

    /// <summary>
    /// Una missione attiva su quell'unita' di carico. Verifica che sia entrata davvero: con il
    /// navigatore fermo <c>ActiveMissions.Add</c> non aggiunge niente e non lo dice, e un test che
    /// dichiara «c'e' una missione» proverebbe il caso senza missione.
    /// </summary>
    private void AddActiveMissionOn(decimal loadUnitId)
    {
        var mission = new PathMission(Guid.NewGuid()) { LuId = loadUnitId };
        ActiveMissions.Instance.Add(mission, buildRoute: false);
        _missions.Add(mission.MissionId);

        Assert.True(ActiveMissions.Instance.Any(m => m != null && m.MissionId == mission.MissionId));
    }
}
